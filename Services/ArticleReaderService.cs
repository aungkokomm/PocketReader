using HtmlAgilityPack;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Models;

namespace PocketReader.Services;

public class ArticleReaderService
{
    private readonly DatabaseService _db;

    /// <summary>
    /// Optional real-browser renderer (WebView2). Set by the UI once the main window exists.
    /// Used as a fallback when a plain HTTP fetch is blocked (e.g. Medium/Cloudflare 403)
    /// or returns a near-empty, JS-rendered shell.
    /// </summary>
    public Func<string, CancellationToken, Task<(string Html, string Url)>> BrowserRender { get; set; }

    public ArticleReaderService(DatabaseService db)
    {
        _db = db;
    }

    // The URL we should actually use: resolved short-link if known, else unwrapped.
    public static string EffectiveUrl(Article a) =>
        !string.IsNullOrEmpty(a.ResolvedUrl) ? a.ResolvedUrl : Helpers.UrlHelper.Unwrap(a.Url);

    // Lightweight: follow redirects to get the real URL + a cover, WITHOUT storing content.
    public async Task<bool> ResolveLinkAndCoverAsync(Article article)
    {
        try
        {
            var start = EffectiveUrl(article);
            var (html, finalUrl) = await HttpFetcher.GetWithFinalUrlAsync(start);

            if (!string.IsNullOrEmpty(finalUrl) && !string.Equals(finalUrl, start, StringComparison.OrdinalIgnoreCase))
                article.ResolvedUrl = finalUrl;

            if (string.IsNullOrEmpty(article.Cover))
            {
                var og = ExtractOgImage(html, finalUrl ?? start);
                if (!string.IsNullOrEmpty(og)) article.Cover = og;
            }

            await CacheFaviconAsync(article);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Resolve error: {ex.Message}");
            return false;
        }
    }

    public async Task<int> ResolveLinksAsync(
        IList<Article> articles,
        IProgress<(int Current, int Total)> progress,
        CancellationToken ct,
        Func<Task> waitIfPaused,
        int concurrency = 8)
    {
        int done = 0;
        var total = articles.Count;
        var gate = new SemaphoreSlim(concurrency);
        var writeLock = new SemaphoreSlim(1, 1);
        var tasks = new List<Task>();

        foreach (var article in articles)
        {
            if (ct.IsCancellationRequested) break;
            await gate.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (waitIfPaused != null) await waitIfPaused();
                    ct.ThrowIfCancellationRequested();

                    if (await ResolveLinkAndCoverAsync(article))
                    {
                        await writeLock.WaitAsync(ct);
                        try { await _db.UpdateLinkMetaAsync(article.Id, article.ResolvedUrl, article.Cover, article.FaviconPath); }
                        finally { writeLock.Release(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Resolve '{article.Title}': {ex.Message}"); }
                finally
                {
                    gate.Release();
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
            }, ct));
        }

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }
        return done;
    }

    public async Task<string> CacheFaviconAsync(Article article)
    {
        try
        {
            var domain = new Uri(EffectiveUrl(article)).Host;
            var faviconName = $"{domain.Replace(".", "_")}.ico";
            var faviconPath = Path.Combine(AppContext.BaseDirectory, "data", "favicons", faviconName);

            if (File.Exists(faviconPath))
            {
                article.FaviconPath = faviconPath;
                return faviconPath;
            }

            var faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=64";
            var faviconData = await HttpFetcher.Client.GetByteArrayAsync(faviconUrl);

            File.WriteAllBytes(faviconPath, faviconData);
            article.FaviconPath = faviconPath;
            return faviconPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Favicon error: {ex.Message}");
            return null;
        }
    }

    // Fetch + extract + inline images + scrub — sets article fields but does NOT
    // touch the DB. Safe to run concurrently; the caller serializes DB writes.
    public async Task<bool> ExtractContentAsync(Article article, CancellationToken ct = default)
    {
        try
        {
            var start = EffectiveUrl(article);

            // 1) Fast path: plain HTTP. Works for the vast majority of sites.
            string html = null;
            string finalUrl = start;
            try
            {
                var res = await HttpFetcher.GetWithFinalUrlAsync(start);
                html = res.Html;
                finalUrl = res.FinalUrl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HTTP fetch blocked for {start}: {ex.Message}");
            }

            var content = html != null ? await BuildContentAsync(html, finalUrl ?? start, article) : null;

            // 2) Fallback: a real browser engine for sites that 403 our HttpClient
            //    (Medium/Cloudflare) or render the article only via JavaScript.
            if (BrowserRender != null && !ct.IsCancellationRequested && NeedsBrowser(content))
            {
                try
                {
                    var (bHtml, bUrl) = await BrowserRender(start, ct);
                    if (!string.IsNullOrWhiteSpace(bHtml))
                    {
                        if (!string.IsNullOrEmpty(bUrl)) finalUrl = bUrl;
                        var viaBrowser = await BuildContentAsync(bHtml, finalUrl ?? start, article);
                        if (TextLength(viaBrowser) > TextLength(content))
                            content = viaBrowser;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Browser render failed for {start}: {ex.Message}");
                }
            }

            if (string.IsNullOrWhiteSpace(content))
                return false; // leave uncached for a later retry

            // Capture the real URL after following short-link redirects (persists).
            if (!string.IsNullOrEmpty(finalUrl) && !string.Equals(finalUrl, start, StringComparison.OrdinalIgnoreCase))
                article.ResolvedUrl = finalUrl;

            article.Content = ScrubMedia(content);
            article.ContentCached = true;
            article.ContentCachedAt = DateTime.UtcNow;

            await CacheFaviconAsync(article);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Extract error: {ex.Message}");
            return false; // leave uncached for a later retry
        }
    }

    // Run Readability (+ image inlining) over a page's HTML, falling back to a structural
    // extraction. Also harvests an og:image cover if the article doesn't have one. Returns
    // the article body HTML (un-scrubbed) or null.
    private async Task<string> BuildContentAsync(string html, string realUrl, Article article)
    {
        if (string.IsNullOrEmpty(html)) return null;

        if (string.IsNullOrEmpty(article.Cover))
        {
            var og = ExtractOgImage(html, realUrl);
            if (!string.IsNullOrEmpty(og)) article.Cover = og;
        }

        string content = null;
        try
        {
            var reader = new SmartReader.Reader(realUrl, html)
            {
                ContinueIfNotReadable = true,
                CharThreshold = 200
            };
            var smart = reader.GetArticle();
            if (smart != null && !string.IsNullOrEmpty(smart.Content))
            {
                try { await smart.ConvertImagesToDataUriAsync(0); } catch { }
                content = smart.Content;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SmartReader failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(content))
            content = ExtractFallback(html);

        return content;
    }

    // Decide whether the cheap HTTP result is too thin and we should try a real browser.
    private static bool NeedsBrowser(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return true;
        if (content.Length > 4000) return false; // clearly a real article — skip the browser
        return TextLength(content) < 600;
    }

    private static int TextLength(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return 0;
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return (doc.DocumentNode.InnerText ?? "").Trim().Length;
        }
        catch { return html.Length; }
    }

    // Single-article path (used by the reader on click): extract + persist.
    public async Task<string> FetchAndExtractArticleAsync(Article article)
    {
        var ok = await ExtractContentAsync(article);
        if (ok)
        {
            await _db.UpdateArticleAsync(article);
            return article.Content;
        }
        return "<p>Could not load this article offline.</p>";
    }

    /// <summary>
    /// Cache many articles concurrently, with cooperative pause and stop.
    /// Network/parse runs in parallel; DB writes are serialized.
    /// </summary>
    public async Task<int> BatchCacheContentAsync(
        IList<Article> articles,
        IProgress<(int Current, int Total)> progress,
        CancellationToken ct,
        Func<Task> waitIfPaused,
        int concurrency = 8)
    {
        int done = 0;
        var total = articles.Count;
        var gate = new SemaphoreSlim(concurrency);
        var writeLock = new SemaphoreSlim(1, 1);
        var tasks = new List<Task>();

        foreach (var article in articles)
        {
            if (ct.IsCancellationRequested) break;
            await gate.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (waitIfPaused != null) await waitIfPaused();
                    ct.ThrowIfCancellationRequested();

                    if (!article.ContentCached && await ExtractContentAsync(article, ct))
                    {
                        await writeLock.WaitAsync(ct);
                        try { await _db.UpdateArticleAsync(article); }
                        finally { writeLock.Release(); }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Batch '{article.Title}': {ex.Message}"); }
                finally
                {
                    gate.Release();
                    progress?.Report((Interlocked.Increment(ref done), total));
                }
            }, ct));
        }

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { }

        return done;
    }

    /// <summary>Pull a representative cover image (og:image / twitter:image / image_src).</summary>
    private static string ExtractOgImage(string html, string baseUrl)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var content =
                MetaContent(doc, "//meta[@property='og:image']") ??
                MetaContent(doc, "//meta[@name='og:image']") ??
                MetaContent(doc, "//meta[@name='twitter:image']") ??
                MetaContent(doc, "//meta[@property='twitter:image']") ??
                doc.DocumentNode.SelectSingleNode("//link[@rel='image_src']")?.GetAttributeValue("href", null);

            if (string.IsNullOrWhiteSpace(content)) return null;

            // Resolve protocol-relative / relative URLs against the page.
            if (Uri.TryCreate(new Uri(baseUrl), content, out var abs)) return abs.ToString();
            return content;
        }
        catch { return null; }
    }

    private static string MetaContent(HtmlDocument doc, string xpath)
    {
        var c = doc.DocumentNode.SelectSingleNode(xpath)?.GetAttributeValue("content", null);
        return string.IsNullOrWhiteSpace(c) ? null : c;
    }

    /// <summary>Remove non-article media; turn video embeds into a source link (Pocket-style).</summary>
    private static string ScrubMedia(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;

        foreach (var tag in new[] { "script", "style", "noscript", "video", "audio", "source", "object", "embed", "form", "svg" })
        {
            var nodes = root.SelectNodes($"//{tag}");
            if (nodes != null)
                foreach (var n in nodes.ToList())
                    n.Remove();
        }

        var iframes = root.SelectNodes("//iframe");
        if (iframes != null)
        {
            foreach (var n in iframes.ToList())
            {
                var src = n.GetAttributeValue("src", "");
                if (!string.IsNullOrEmpty(src) &&
                    (src.Contains("youtube") || src.Contains("youtu.be") || src.Contains("vimeo") || src.Contains("dailymotion")))
                {
                    var link = HtmlNode.CreateNode($"<p class=\"pr-video\"><a href=\"{src}\">▶ Watch video on the original site</a></p>");
                    n.ParentNode?.ReplaceChild(link, n);
                }
                else
                {
                    n.Remove();
                }
            }
        }

        // Defense-in-depth: with scripts enabled in the reader (for progress/highlights),
        // strip any inline event handlers and javascript: URLs so only OUR injected script
        // can run — page-supplied JS cannot.
        StripActiveAttributes(root);

        return root.InnerHtml;
    }

    // Remove on* event-handler attributes and javascript:/vbscript: URLs from every element.
    private static void StripActiveAttributes(HtmlNode root)
    {
        foreach (var el in root.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
        {
            if (el.Attributes == null || el.Attributes.Count == 0) continue;
            foreach (var attr in el.Attributes.ToList())
            {
                var name = attr.Name?.ToLowerInvariant() ?? "";
                var val = (attr.Value ?? "").TrimStart();
                if (name.StartsWith("on") ||
                    ((name == "href" || name == "src" || name == "xlink:href") &&
                     (val.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                      val.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))))
                {
                    el.Attributes.Remove(attr);
                }
            }
        }
    }

    // Sanitize content (possibly cached before the hardening) right before it's rendered
    // in a script-enabled reader.
    public static string SanitizeForScript(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return html;
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            // Drop any <script>/<svg> that slipped through, plus active attributes.
            foreach (var tag in new[] { "script", "noscript", "svg" })
            {
                var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
                if (nodes != null) foreach (var n in nodes.ToList()) n.Remove();
            }
            StripActiveAttributes(doc.DocumentNode);
            return doc.DocumentNode.InnerHtml;
        }
        catch { return html; }
    }

    private string ExtractFallback(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var junk = doc.DocumentNode.Descendants()
            .Where(n => n.Name == "script" || n.Name == "style")
            .ToList();
        foreach (var node in junk)
            node.Remove();

        var main = doc.DocumentNode.SelectSingleNode("//article")
                   ?? doc.DocumentNode.SelectSingleNode("//main")
                   ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'content')]")
                   ?? doc.DocumentNode.SelectSingleNode("//body");

        return main != null ? main.InnerHtml : "<p>Unable to extract article content.</p>";
    }

    public string StripHtmlTags(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc.DocumentNode.InnerText;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Models;
using PocketReader.Services;
using System.Collections.ObjectModel;

namespace PocketReader;

public sealed partial class ReaderPage : Window
{
    public Article Article { get; private set; }
    private DatabaseService _db;
    private ArticleReaderService _readerService;
    private TaggingService _tagging;
    private string _readerTheme = "Light"; // "Light" | "Sepia" | "Dark"
    private double _fontScale = 1.0;
    private ObservableCollection<string> _tags = new();
    private bool _initialized;
    private bool _webReady;

    // Text-to-speech
    private Windows.Media.SpeechSynthesis.SpeechSynthesizer _synth;
    private Windows.Media.Playback.MediaPlayer _player;
    private bool _speaking;
    private bool _paused;

    // Reading-time tracking (counts only while the reader window is focused).
    private readonly System.Diagnostics.Stopwatch _readSw = new();

    // Reading progress / resume
    private string _renderHtml;     // sanitized content for the script-enabled reader
    private int _savedScroll;       // position to restore for the current article (0-100)
    private int _curScroll;         // latest reported position
    private bool _closing;          // guards WebView2 callbacks during teardown

    // Injected once; runs on every rendered document. Reports scroll %, restores position
    // (#pr hash), and applies/saves highlights. The host never calls into the WebView2 after
    // navigation (that crashed on close). Page scripts are stripped, so this is the only JS.
    // NOTE: single quotes only — this is a C# verbatim string.
    private const string ReaderScript = @"
(function(){
  var last=-1, t=null, ART=null;
  function art(){ if(!ART){ ART=document.querySelector('article.reader')||document.body; } return ART; }
  function post(o){ try{ window.chrome.webview.postMessage(JSON.stringify(o)); }catch(e){} }
  function pct(){ var d=document.documentElement, m=d.scrollHeight-d.clientHeight; return m>0?Math.round((window.scrollY||d.scrollTop)/m*100):0; }
  window.addEventListener('scroll', function(){ if(t)clearTimeout(t); t=setTimeout(function(){ var p=pct(); if(p!==last){ last=p; post({type:'scroll',pct:p}); } }, 200); }, {passive:true});
  function offsetOf(node,off){ var w=document.createTreeWalker(art(),NodeFilter.SHOW_TEXT,null), pos=0, n; while(n=w.nextNode()){ if(n===node) return pos+off; pos+=n.nodeValue.length; } return -1; }
  function apply(start,end){ if(end<=start) return; var w=document.createTreeWalker(art(),NodeFilter.SHOW_TEXT,null), pos=0, n, segs=[]; while(n=w.nextNode()){ var s=pos, e=pos+n.nodeValue.length; pos=e; if(e<=start||s>=end) continue; if(n.parentNode&&n.parentNode.classList&&n.parentNode.classList.contains('pr-hl')) continue; segs.push({node:n,a:Math.max(start,s)-s,b:Math.min(end,e)-s}); } for(var i=0;i<segs.length;i++){ var g=segs[i], node=g.node, a=g.a, b=g.b; if(b<=a) continue; var mid=a>0?node.splitText(a):node; if(b-a<mid.nodeValue.length) mid.splitText(b-a); var mk=document.createElement('mark'); mk.className='pr-hl'; mk.setAttribute('data-s',start); mk.setAttribute('data-e',end); mid.parentNode.insertBefore(mk,mid); mk.appendChild(mid); } }
  function unmark(start,end){ var all=art().querySelectorAll('mark.pr-hl'); for(var i=0;i<all.length;i++){ var mk=all[i]; if(+mk.getAttribute('data-s')===start && +mk.getAttribute('data-e')===end){ var p=mk.parentNode; while(mk.firstChild) p.insertBefore(mk.firstChild,mk); p.removeChild(mk); p.normalize(); } } }
  window.__prHighlight=function(){ var sel=window.getSelection(); if(!sel||sel.rangeCount===0||sel.isCollapsed) return; var r=sel.getRangeAt(0); if(!art().contains(r.commonAncestorContainer)) return; var s=offsetOf(r.startContainer,r.startOffset), e=offsetOf(r.endContainer,r.endOffset); if(s<0||e<0) return; if(e<s){ var tmp=s; s=e; e=tmp; } if(e<=s) return; var text=sel.toString(); apply(s,e); sel.removeAllRanges(); post({type:'hl-add',s:s,e:e,text:text}); };
  document.addEventListener('click', function(ev){ var el=ev.target; var mk=(el&&el.closest)?el.closest('mark.pr-hl'):null; if(!mk) return; if(window.getSelection&&!window.getSelection().isCollapsed) return; var s=+mk.getAttribute('data-s'), e=+mk.getAttribute('data-e'); unmark(s,e); post({type:'hl-remove',s:s,e:e}); });
  function init(){ try{ var raw=art().getAttribute('data-hl'); if(raw){ var arr=JSON.parse(raw); for(var i=0;i<arr.length;i++){ apply(arr[i].s, arr[i].e); } } }catch(e){} try{ var mm=/pr=(\d+)/.exec(location.hash||''); if(mm){ var p=+mm[1]; var d=document.documentElement, m=d.scrollHeight-d.clientHeight; if(p>0) window.scrollTo(0, m*p/100); } }catch(e){} }
  if(document.readyState==='complete'){ setTimeout(init,60); } else { window.addEventListener('load', function(){ setTimeout(init,60); }); }
})();";

    public ReaderPage(DatabaseService db, ArticleReaderService readerService, TaggingService tagging)
    {
        this.InitializeComponent();

        _db = db;
        _readerService = readerService;
        _tagging = tagging;

        var settings = AppSettingsService.Load();
        _readerTheme = string.IsNullOrEmpty(settings.ReaderTheme) ? "Light" : settings.ReaderTheme;

        this.SetAppIcon();
        this.ApplyTheme(settings.Theme);

        // Modern title bar: draw our own (toolbar lives here) and let Mica show through.
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
        this.TryEnableMica();

        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(1200, 860));
        }
        catch { }

        // Window has no Loaded event in WinUI 3 — init WebView2 once on first Activated.
        this.Activated += OnFirstActivated;
        this.Activated += OnReaderActivated;   // reading-time tracking
        this.Closed += OnReaderClosed;
    }

    // Count focused reading time. Pause when the window loses focus, resume when it regains.
    private void OnReaderActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
            FlushReadingTime();
        else if (Article != null && !_readSw.IsRunning)
            _readSw.Start();
    }

    private void OnReaderClosed(object sender, WindowEventArgs e)
    {
        _closing = true;               // stop any WebView2 callback from touching the view
        SaveNoteIfNeeded();
        FlushReadingTime();
        FlushScroll();
        StopSpeech();

        // Dispose media so its native pipeline doesn't fault during teardown.
        try { if (_player != null) { _player.MediaEnded -= OnSpeechEnded; _player.Dispose(); _player = null; } } catch { }
        try { _synth?.Dispose(); _synth = null; } catch { }

        // Tear the WebView2 down explicitly BEFORE the window finishes closing — closing a
        // live WebView2 implicitly is what raised the stack-buffer-overrun fast-fail.
        try
        {
            if (ContentWebView?.CoreWebView2 != null)
            {
                ContentWebView.CoreWebView2.WebMessageReceived -= OnWebMessage;
                ContentWebView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
            }
            ContentWebView?.Close();
        }
        catch { }
    }

    private void FlushReadingTime()
    {
        if (!_readSw.IsRunning) return;
        var secs = (int)_readSw.Elapsed.TotalSeconds;
        _readSw.Reset();
        if (secs > 0) _ = _db.AddReadingSecondsAsync(secs);
    }

    // ---- Reading progress ----------------------------------------------------

    private async void OnWebMessage(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
                                    Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_closing) return;
        try
        {
            var json = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;

            if (type == "scroll" && root.TryGetProperty("pct", out var pe))
            {
                _curScroll = Math.Clamp(pe.GetInt32(), 0, 100);
                if (ReaderProgress != null) ReaderProgress.Value = _curScroll;
            }
            else if (type == "hl-add" && Article != null
                     && root.TryGetProperty("s", out var se) && root.TryGetProperty("e", out var ee))
            {
                int s = se.GetInt32(), en = ee.GetInt32();
                var text = root.TryGetProperty("text", out var te) ? te.GetString() : "";
                (Article.Highlights ??= new System.Collections.Generic.List<Highlight>())
                    .Add(new Highlight { ArticleId = Article.Id, StartOffset = s, EndOffset = en, Text = text });
                await _db.AddHighlightAsync(Article.Id, s, en, text, null);
            }
            else if (type == "hl-remove" && Article != null
                     && root.TryGetProperty("s", out var se2) && root.TryGetProperty("e", out var ee2))
            {
                int s = se2.GetInt32(), en = ee2.GetInt32();
                Article.Highlights?.RemoveAll(h => h.StartOffset == s && h.EndOffset == en);
                await _db.DeleteHighlightAsync(Article.Id, s, en);
            }
        }
        catch { }
    }

    // Build the JSON the page reads on load to re-apply highlights.
    private string HighlightsJson()
    {
        var list = Article?.Highlights;
        if (list == null || list.Count == 0) return "[]";
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"s\":").Append(list[i].StartOffset).Append(",\"e\":").Append(list[i].EndOffset).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private async void OnHighlightClick(object sender, RoutedEventArgs e)
    {
        if (!_webReady || _closing) return;
        try { await ContentWebView.CoreWebView2.ExecuteScriptAsync("window.__prHighlight && window.__prHighlight();"); }
        catch { }
    }

    private void OnNavCompleted(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
                                Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        // The injected script restores the position itself (from the #pr hash); we only
        // reflect it on the progress bar. No host→WebView2 call after navigation.
        if (_closing) return;
        if (ReaderProgress != null) ReaderProgress.Value = _curScroll;
    }

    private void FlushScroll()
    {
        if (Article == null) return;
        if (_curScroll != Article.ScrollPercent)
        {
            Article.ScrollPercent = _curScroll;
            _ = _db.SetScrollPercentAsync(Article.Id, _curScroll);
        }
    }

    // Render a (possibly different) article into this reused window.
    public void ShowArticle(Article article)
    {
        SaveNoteIfNeeded(); // persist the previous article's note before switching
        StopSpeech();       // stop narrating the previous article
        FlushReadingTime(); // bank time spent on the previous article
        FlushScroll();      // save the previous article's reading position

        Article = article;
        _renderHtml = ArticleReaderService.SanitizeForScript(article.Content);
        _savedScroll = Math.Clamp(article.ScrollPercent, 0, 100);
        _curScroll = _savedScroll;
        _readSw.Restart();  // start timing the new article
        _tags = new ObservableCollection<string>(article.GetTagsList());

        NoteBox.Text = article.Note ?? "";

        // Meta strip: favicon + source + read time.
        var url = PocketReader.Services.ArticleReaderService.EffectiveUrl(article);
        try
        {
            var host = new Uri(url).Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host.Substring(4);
            MetaSource.Text = host;
            MetaFavicon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                !string.IsNullOrEmpty(article.FaviconPath) && System.IO.File.Exists(article.FaviconPath)
                    ? new Uri(article.FaviconPath)
                    : new Uri($"https://www.google.com/s2/favicons?domain={host}&sz=64"));
        }
        catch { MetaSource.Text = ""; MetaFavicon.Source = null; }

        UrlBlock.Text = "";   // url shown via source/favicon now
        TitleBarText.Text = string.IsNullOrEmpty(article.Title) ? "PocketReader" : $"PocketReader — {Truncate(article.Title, 80)}";

        var rt = EstimateReadTime(article.Content);
        ReadTimeBlock.Text = string.IsNullOrEmpty(rt) ? "" : rt.TrimStart(' ', '·').Trim();

        UpdateOfflineIndicator();
        UpdateTagsUI();
        BuildSuggestions();

        if (_webReady) RenderArticleContent();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";

    private void BuildSuggestions()
    {
        SuggestPanel.Children.Clear();
        if (_tagging == null || !_tagging.IsTrained || Article == null)
        {
            SuggestSection.Visibility = Visibility.Collapsed;
            return;
        }

        var suggestions = _tagging.Suggest(Article.Title, Article.Url, _tags).Take(6).ToList();
        if (suggestions.Count == 0)
        {
            SuggestSection.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var s in suggestions)
        {
            // Subtle pill: "+ tag" with accent text, soft fill, faint border.
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new FontIcon
            {
                Glyph = "", // Add
                FontSize = 11,
                Foreground = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush,
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = s.Tag,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(10, 4, 10, 4),
                CornerRadius = new CornerRadius(14),
                Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush,
                BorderThickness = new Thickness(0)
            };
            var tag = s.Tag;
            btn.Click += (_, __) => AddSuggestedTag(tag);
            SuggestPanel.Children.Add(btn);
        }
        SuggestSection.Visibility = Visibility.Visible;
    }

    private void AddSuggestedTag(string tag)
    {
        if (!_tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
        {
            _tags.Add(tag);
            Article.SetTags(_tags);
            _ = _db.UpdateTagsAsync(Article.Id, Article.Tags);
            UpdateTagsUI();
        }
        BuildSuggestions();
    }

    private string EstimateReadTime(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var words = _readerService.StripHtmlTags(html)
            .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words == 0) return "";
        var minutes = Math.Max(1, (int)Math.Round(words / 200.0));
        return $"{minutes} min read";
    }

    private void OnFontDec(object sender, RoutedEventArgs e)
    {
        _fontScale = Math.Max(0.8, _fontScale - 0.1);
        if (_webReady) RenderArticleContent();
    }

    private void OnFontInc(object sender, RoutedEventArgs e)
    {
        _fontScale = Math.Min(1.8, _fontScale + 0.1);
        if (_webReady) RenderArticleContent();
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        await ContentWebView.EnsureCoreWebView2Async();

        // Scripts are enabled ONLY so our injected reading-progress reporter can run; the
        // article HTML itself is sanitized (no <script>, no on* handlers, no javascript:
        // URLs) so page-supplied JS cannot execute.
        var s = ContentWebView.CoreWebView2.Settings;
        s.IsScriptEnabled = true;
        s.AreDefaultScriptDialogsEnabled = false;
        s.IsWebMessageEnabled = true;
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;

        ContentWebView.CoreWebView2.WebMessageReceived += OnWebMessage;
        ContentWebView.CoreWebView2.NavigationCompleted += OnNavCompleted;
        try { await ContentWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(ReaderScript); } catch { }

        _webReady = true;

        UpdateThemeRadio();
        UpdateWebShellBackground();
        if (Article != null) RenderArticleContent();
    }

    private void UpdateOfflineIndicator()
    {
        // The pill is a Border in the new layout; toggle its visibility based on cache state.
        OfflineIndicator.Visibility = Article != null && Article.ContentCached
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void RenderArticleContent()
    {
        // Use the sanitized HTML (no page JS) for the script-enabled reader.
        var html = !string.IsNullOrEmpty(_renderHtml) ? _renderHtml
                   : ArticleReaderService.SanitizeForScript(Article.Content);
        var body = string.IsNullOrEmpty(html)
            ? "<p>No offline content for this article yet.</p>"
            : html;

        // Render the title inside the article so it scrolls naturally with the content.
        var titleHtml = $"<h1>{System.Net.WebUtility.HtmlEncode(Article.Title ?? "")}</h1>";
        var styledHtml = WrapInHtml(titleHtml + body);

        // NavigateToString has a ~2MB limit; inlined base64 images blow past it and
        // crash the view. Write to a temp file and navigate to it instead (no limit).
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data", "cache");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "reader.html");
            File.WriteAllText(file, styledHtml, System.Text.Encoding.UTF8);
            // Cache-bust the query so theme/font re-renders reload; the #pr hash tells the
            // injected script where to scroll (current position, so re-renders keep place).
            var url = new Uri(file).AbsoluteUri + "?v=" + DateTime.Now.Ticks + "#pr=" + _curScroll;
            ContentWebView.CoreWebView2.Navigate(url);
        }
        catch
        {
            try { ContentWebView.NavigateToString(styledHtml); } catch { }
        }
    }

    private string WrapInHtml(string content)
    {
        // Measured reading column, serif body, sans headings — Pocket-style.
        string bg, fg, muted, rule, link, accent, codeBg, hl;
        switch (_readerTheme)
        {
            case "Dark":
                bg = "#1b1b1d"; fg = "#e6e6e6"; muted = "#9a9a9a"; rule = "#333333";
                link = "#7db4ff"; accent = "#5fd08a"; codeBg = "rgba(255,255,255,0.08)";
                hl = "rgba(255,213,0,0.28)"; break;
            case "Sepia":
                bg = "#f4ecd8"; fg = "#5b4636"; muted = "#8a7a64"; rule = "#e0d6bf";
                link = "#9a5b2c"; accent = "#8a6d3b"; codeBg = "rgba(91,70,54,0.08)";
                hl = "rgba(180,130,30,0.35)"; break;
            default: // Light
                bg = "#ffffff"; fg = "#1a1a1a"; muted = "#6b6b6b"; rule = "#e7e7e7";
                link = "#1565c0"; accent = "#0a7d3c"; codeBg = "rgba(0,0,0,0.05)";
                hl = "rgba(255,213,0,0.45)"; break;
        }
        var bodySize = Math.Round(21 * _fontScale);

        return $@"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<style>
  :root {{ color-scheme: {( _readerTheme == "Dark" ? "dark" : "light")}; }}
  * {{ box-sizing: border-box; }}
  html, body {{ margin:0; background:{bg}; color:{fg}; }}
  ::selection {{ background:{accent}33; }}
  .reader {{
    max-width: 720px; margin: 0 auto; padding: 44px 32px 112px;
    font-family: Georgia, 'Iowan Old Style', Charter, 'Times New Roman', serif;
    font-size: {bodySize}px; line-height: 1.75; letter-spacing: .1px;
    -webkit-font-smoothing: antialiased; text-rendering: optimizeLegibility;
    word-wrap: break-word; overflow-wrap: break-word;
  }}
  .reader p {{ margin: 0 0 24px; }}
  .reader > p:first-of-type {{ font-size: 1.05em; }}
  .reader a {{ color: {link}; text-decoration: none; border-bottom: 1px solid {link}55; }}
  .reader a:hover {{ border-bottom-color: {link}; }}
  .reader img {{ max-width: 100%; height: auto; border-radius: 10px; margin: 10px 0 6px; display:block; }}
  .reader figure {{ margin: 32px 0; }}
  .reader figcaption {{ font-family:'Segoe UI',system-ui,sans-serif; font-size:13px; color:{muted}; margin-top:10px; text-align:center; }}
  .reader h1 {{ font-family:'Segoe UI Variable Display','Segoe UI',system-ui,sans-serif; font-weight:700; font-size:1.85em; line-height:1.18; letter-spacing:-.4px; margin:0 0 20px; }}
  .reader h2 {{ font-family:'Segoe UI Variable Display','Segoe UI',system-ui,sans-serif; font-weight:600; font-size:1.32em; line-height:1.3; letter-spacing:-.2px; margin:44px 0 12px; }}
  .reader h3 {{ font-family:'Segoe UI',system-ui,sans-serif; font-weight:600; font-size:1.12em; margin:32px 0 10px; }}
  .reader h4 {{ font-family:'Segoe UI',system-ui,sans-serif; font-weight:600; font-size:1em; margin:26px 0 8px; }}
  .reader ul, .reader ol {{ margin: 0 0 24px; padding-left: 1.4em; }}
  .reader li {{ margin: 0 0 10px; }}
  .reader li::marker {{ color:{muted}; }}
  .reader blockquote {{ margin:28px 0; padding:6px 0 6px 24px; border-left:3px solid {accent}; color:{muted}; font-style:italic; }}
  .reader blockquote p:last-child {{ margin-bottom:0; }}
  .reader strong, .reader b {{ font-weight:700; color:{fg}; }}
  .reader mark {{ background:{accent}33; color:inherit; padding:0 2px; border-radius:3px; }}
  .reader kbd {{ font-family:ui-monospace,Consolas,monospace; font-size:.82em; background:{codeBg}; border:1px solid {rule}; border-radius:5px; padding:1px 6px; }}
  .reader pre {{ font-family:ui-monospace,'Cascadia Code',Consolas,monospace; font-size:14.5px; line-height:1.55; background:{codeBg}; padding:16px 18px; border-radius:10px; overflow:auto; margin:0 0 24px; }}
  .reader pre code {{ background:none; padding:0; border:0; }}
  .reader code {{ font-family:ui-monospace,'Cascadia Code',Consolas,monospace; font-size:.88em; background:{codeBg}; padding:2px 6px; border-radius:5px; }}
  .reader table {{ width:100%; border-collapse:collapse; font-family:'Segoe UI',system-ui,sans-serif; font-size:.9em; margin:0 0 24px; }}
  .reader th, .reader td {{ border:1px solid {rule}; padding:8px 12px; text-align:left; }}
  .reader th {{ background:{codeBg}; font-weight:600; }}
  .reader hr {{ border:none; border-top:1px solid {rule}; margin:40px 0; }}
  .reader .pr-video {{
    font-family:'Segoe UI',system-ui,sans-serif; display:flex; align-items:center; gap:12px;
    margin:28px 0; padding:14px 16px; border:1px solid {rule}; border-radius:12px;
    background:{codeBg};
  }}
  .reader .pr-video a {{ color:{fg}; border:0; }}
  .reader mark.pr-hl {{ background:{hl}; color:inherit; cursor:pointer; border-radius:2px; padding:0 1px; -webkit-box-decoration-break:clone; box-decoration-break:clone; }}
</style>
</head>
<body>
  <article class='reader' data-hl='{HighlightsJson()}'>
    {content}
  </article>
</body>
</html>";
    }

    private void OnThemeLight(object sender, RoutedEventArgs e) => SetReaderTheme("Light");
    private void OnThemeSepia(object sender, RoutedEventArgs e) => SetReaderTheme("Sepia");
    private void OnThemeDark(object sender, RoutedEventArgs e) => SetReaderTheme("Dark");

    private void SetReaderTheme(string theme)
    {
        _readerTheme = theme;
        var st = AppSettingsService.Load();
        st.ReaderTheme = theme;
        AppSettingsService.Save(st);
        UpdateThemeRadio();
        UpdateWebShellBackground();
        if (_webReady) RenderArticleContent();
    }

    // Paint the chrome behind the WebView2 with the article's outer color so the WebUI
    // never flashes white during navigation and any scrollbar gutter blends in.
    private void UpdateWebShellBackground()
    {
        if (WebShell == null) return;
        var color = _readerTheme switch
        {
            "Dark" => Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1D),
            "Sepia" => Windows.UI.Color.FromArgb(0xFF, 0xF4, 0xEC, 0xD8),
            _ => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
        };
        WebShell.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
    }

    private void UpdateThemeRadio()
    {
        ThemeLightItem.IsChecked = _readerTheme == "Light";
        ThemeSepiaItem.IsChecked = _readerTheme == "Sepia";
        ThemeDarkItem.IsChecked = _readerTheme == "Dark";
    }

    // ---- Text-to-speech (offline, native Windows voices) --------------------

    private async void OnListenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Already playing → toggle pause/resume.
            if (_player != null && _speaking)
            {
                if (_paused) { _player.Play(); _paused = false; ListenIcon.Glyph = ""; }
                else { _player.Pause(); _paused = true; ListenIcon.Glyph = ""; }
                return;
            }

            var body = _readerService.StripHtmlTags(Article?.Content ?? "");
            if (string.IsNullOrWhiteSpace(body)) return;
            var text = (Article?.Title ?? "") + ". \n" + body;

            _synth ??= new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            ListenIcon.Glyph = ""; // pause (we're about to play)
            var stream = await _synth.SynthesizeTextToStreamAsync(text);

            if (_player == null)
            {
                _player = new Windows.Media.Playback.MediaPlayer();
                _player.MediaEnded += OnSpeechEnded;
            }
            _player.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
            _speaking = true; _paused = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"TTS error: {ex.Message}");
            StopSpeech();
        }
    }

    private void OnSpeechEnded(Windows.Media.Playback.MediaPlayer sender, object args)
        => DispatcherQueue.TryEnqueue(StopSpeech);

    private void StopSpeech()
    {
        try { _player?.Pause(); if (_player != null) _player.Source = null; } catch { }
        _speaking = false; _paused = false;
        if (ListenIcon != null) ListenIcon.Glyph = ""; // play
    }

    // ---- Export to PDF (renders the styled reader page) ---------------------

    private async void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (Article == null || !_webReady) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                SuggestedFileName = SafeFileName(Article.Title)
            };
            picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;

            var ok = await ContentWebView.CoreWebView2.PrintToPdfAsync(file.Path, null);
            await new ContentDialog
            {
                Title = ok ? "Exported" : "Export failed",
                Content = ok ? $"Saved to:\n{file.Path}" : "Could not create the PDF.",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PDF export error: {ex.Message}");
        }
    }

    private static string SafeFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "article" : title.Trim();
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Length > 80 ? name.Substring(0, 80).TrimEnd() : name;
        return name;
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e) => SaveNoteIfNeeded();

    private void SaveNoteIfNeeded()
    {
        if (Article == null || NoteBox == null) return;
        var text = NoteBox.Text ?? "";
        if (!string.Equals(text, Article.Note ?? "", StringComparison.Ordinal))
        {
            Article.Note = text;
            _ = _db.UpdateNoteAsync(Article.Id, text);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        SaveNoteIfNeeded();
        StopSpeech();
        FlushScroll();
        this.Close();
    }

    private void OnEscInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        SaveNoteIfNeeded();
        StopSpeech();
        FlushScroll();
        args.Handled = true;
        this.Close();
    }

    private async void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dataPackage.SetText(Article.Url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

        var dialog = new ContentDialog
        {
            Title = "Copied",
            Content = "URL copied to clipboard",
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = PocketReader.Services.ArticleReaderService.EffectiveUrl(Article);
            if (!PocketReader.Helpers.UrlHelper.IsWebUrl(target)) return;
            await Windows.System.Launcher.LaunchUriAsync(new Uri(target));
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Error",
                Content = $"Failed to open URL: {ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void OnTagsSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.QueryText))
            return;

        var newTags = args.QueryText.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t));

        foreach (var tag in newTags)
        {
            if (!_tags.Contains(tag))
            {
                _tags.Add(tag);
            }
        }

        Article.SetTags(_tags);
        _ = _db.UpdateTagsAsync(Article.Id, Article.Tags);
        UpdateTagsUI();
        BuildSuggestions();
        TagsBox.Text = "";
    }

    private void UpdateTagsUI()
    {
        var stackPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var accentBrush = Application.Current.Resources["AccentTextFillColorPrimaryBrush"] as Brush;
        var muted = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        var fill = Application.Current.Resources["SubtleFillColorTertiaryBrush"] as Brush;
        var stroke = Application.Current.Resources["ControlStrokeColorDefaultBrush"] as Brush;

        foreach (var tag in _tags)
        {
            // Chip = [ # tag ][ x ]
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };

            row.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 11,
                Foreground = accentBrush,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = tag,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var del = new Button
            {
                Padding = new Thickness(0),
                Width = 18,
                Height = 18,
                MinHeight = 0,
                MinWidth = 0,
                CornerRadius = new CornerRadius(9),
                Background = null,
                BorderThickness = new Thickness(0),
                Foreground = muted,
                VerticalAlignment = VerticalAlignment.Center,
                Content = new FontIcon { Glyph = "", FontSize = 9 },
                Margin = new Thickness(2, 0, 0, 0)
            };
            ToolTipService.SetToolTip(del, "Remove tag");
            var captured = tag;
            del.Click += (s, e) =>
            {
                _tags.Remove(captured);
                Article.SetTags(_tags);
                _ = _db.UpdateTagsAsync(Article.Id, Article.Tags);
                UpdateTagsUI();
                BuildSuggestions();
            };
            row.Children.Add(del);

            var border = new Border
            {
                Padding = new Thickness(10, 4, 6, 4),
                CornerRadius = new CornerRadius(14),
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
                Child = row
            };
            stackPanel.Children.Add(border);
        }

        TagsControl.Content = stackPanel;
        if (TagCountHint != null) TagCountHint.Text = _tags.Count == 0 ? "(none)" : $"({_tags.Count})";
    }
}

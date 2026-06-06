using System.Net;
using System.Net.Http.Headers;

namespace PocketReader.Helpers;

/// <summary>
/// One shared, browser-like HttpClient for the whole app. A realistic User-Agent
/// and Accept headers dramatically improve success on sites that block bare clients.
/// </summary>
public static class HttpFetcher
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0.0.0 Safari/537.36";

    public static HttpClient Client { get; } = Build();

    private static HttpClient Build()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var h = client.DefaultRequestHeaders;
        h.UserAgent.ParseAdd(UserAgent);
        h.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        h.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        // Full modern-Chrome navigation headers — many sites (Medium, Cloudflare) gate
        // content unless the request looks like a real browser navigation.
        h.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        h.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        h.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        h.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        h.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        h.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
        h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        return client;
    }

    /// <summary>Fetch a page's HTML with one retry. Throws on final failure.</summary>
    public static async Task<string> GetHtmlAsync(string url)
    {
        var (html, _) = await GetWithFinalUrlAsync(url);
        return html;
    }

    /// <summary>Fetch HTML and return the final URL after following any redirects.</summary>
    public static async Task<(string Html, string FinalUrl)> GetWithFinalUrlAsync(string url)
    {
        Exception last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                var html = await resp.Content.ReadAsStringAsync();
                var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
                return (html, finalUrl);
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == 0) await Task.Delay(800);
            }
        }
        throw last ?? new Exception("Fetch failed");
    }
}

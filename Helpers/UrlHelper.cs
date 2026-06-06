using System.Linq;
using System.Web;

namespace PocketReader.Helpers;

/// <summary>
/// Unwraps "share/redirect" links (search.app, l.facebook.com, google /url, …) to the
/// real destination URL so parsing, favicons, domains and covers work.
/// </summary>
public static class UrlHelper
{
    // host (or suffix) → it carries the real link in a query parameter.
    private static readonly string[] WrapperHosts =
    {
        "search.app", "l.facebook.com", "lm.facebook.com", "l.instagram.com",
        "out.reddit.com", "href.li", "news.google.com", "t.umblr.com",
        "www.google.com", "google.com"
    };

    private static readonly string[] ParamNames = { "link", "url", "u", "q", "target", "continue" };

    // Pure short-links: the real URL is NOT in the string — it needs a network redirect.
    private static readonly string[] ShortenerHosts =
    {
        "search.app", "bit.ly", "t.co", "goo.gl", "tinyurl.com", "ow.ly", "buff.ly",
        "dlvr.it", "trib.al", "lnkd.in", "rb.gy", "is.gd", "cutt.ly", "shorturl.at", "flip.it"
    };

    /// <summary>True when a URL can only be resolved by following an HTTP redirect.</summary>
    public static bool NeedsResolution(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var host = uri.Host.ToLowerInvariant();
            // search.app/?link=... carries the URL (handled by Unwrap) → not a shortener case.
            if (host == "search.app" && !string.IsNullOrEmpty(HttpUtility.ParseQueryString(uri.Query)["link"]))
                return false;
            return ShortenerHosts.Any(h => host == h || host.EndsWith("." + h));
        }
        catch { return false; }
    }

    /// <summary>Only http/https URLs are safe to hand to the shell / launcher.</summary>
    public static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    public static string Unwrap(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;

        try
        {
            for (var i = 0; i < 3; i++) // handle a few levels of nesting
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) break;
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) break;

                var host = uri.Host.ToLowerInvariant();
                var isWrapper = WrapperHosts.Any(h => host == h || host.EndsWith("." + h));
                if (!isWrapper) break;

                var q = HttpUtility.ParseQueryString(uri.Query);
                var inner = ParamNames
                    .Select(p => q[p])
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v) &&
                                         (v.StartsWith("http://") || v.StartsWith("https://")));

                if (string.IsNullOrEmpty(inner) || inner == url) break;
                url = inner;
            }
        }
        catch { }

        return url;
    }
}

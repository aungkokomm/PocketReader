using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using WinRT.Interop;

namespace PocketReader.Helpers;

/// <summary>
/// Renders a page through a real (hidden) WebView2 / Edge Chromium engine and returns the
/// fully-rendered HTML. Used as a fallback for sites that block our plain HttpClient
/// (Medium &amp; other Cloudflare-gated pages return 403 to non-browser clients) or that
/// only build their article body with JavaScript.
///
/// All WebView2 work runs on the UI thread (a hard WebView2 requirement) and is serialized
/// through a gate, so concurrent batch workers queue one navigation at a time.
/// </summary>
public sealed class BrowserRenderer
{
    private readonly Window _window;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CoreWebView2Environment _env;
    private CoreWebView2Controller _controller;
    private CoreWebView2 _core;

    public BrowserRenderer(Window window)
    {
        _window = window;
        _dispatcher = window.DispatcherQueue;
    }

    /// <summary>
    /// Navigate to <paramref name="url"/> in the hidden browser and return its rendered HTML
    /// plus the final (post-redirect) URL.
    /// </summary>
    public Task<(string Html, string Url)> RenderAsync(string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<(string, string)>();

        var queued = _dispatcher.TryEnqueue(async () =>
        {
            await _gate.WaitAsync(ct).ConfigureAwait(true);
            try { tcs.TrySetResult(await RenderCoreAsync(url, ct)); }
            catch (Exception ex) { tcs.TrySetException(ex); }
            finally { _gate.Release(); }
        });

        if (!queued) tcs.TrySetException(new InvalidOperationException("UI dispatcher unavailable."));
        return tcs.Task;
    }

    private async Task EnsureAsync()
    {
        if (_core != null) return;

        var udf = Path.Combine(AppContext.BaseDirectory, "data", "wv2cache");
        Directory.CreateDirectory(udf);

        _env = await CoreWebView2Environment.CreateWithOptionsAsync(null, udf, new CoreWebView2EnvironmentOptions());
        var hwnd = WindowNative.GetWindowHandle(_window);
        var winRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd);
        _controller = await _env.CreateCoreWebView2ControllerAsync(winRef);
        _controller.IsVisible = false;                                   // render off-screen
        _controller.Bounds = new Rect(0, 0, 1200, 1600);                 // give it a viewport so JS lays out
        _core = _controller.CoreWebView2;

        var s = _core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
    }

    private async Task<(string Html, string Url)> RenderCoreAsync(string url, CancellationToken ct)
    {
        await EnsureAsync();

        var html = await NavigateAndReadAsync(url, 1500, ct);

        // A Cloudflare interstitial ("Just a moment…") resolves to the real page after a
        // few seconds of JS. If we still see it, wait longer and re-read once.
        if (LooksLikeChallenge(html))
        {
            try { await Task.Delay(4500, ct); } catch { }
            var again = await ReadHtmlAsync();
            if (!string.IsNullOrWhiteSpace(again)) html = again;
        }

        var finalUrl = _core.Source;
        if (string.IsNullOrEmpty(finalUrl) || finalUrl == "about:blank") finalUrl = url;
        return (html, finalUrl);
    }

    private async Task<string> NavigateAndReadAsync(string url, int settleMs, CancellationToken ct)
    {
        var navDone = new TaskCompletionSource<bool>();
        void OnNav(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs e) => navDone.TrySetResult(e.IsSuccess);

        _core.NavigationCompleted += OnNav;
        try
        {
            _core.Navigate(url);
            using (ct.Register(() => navDone.TrySetCanceled()))
                await Task.WhenAny(navDone.Task, Task.Delay(25000, CancellationToken.None));
        }
        finally { _core.NavigationCompleted -= OnNav; }

        // Allow lazy/hydrated content to settle before snapshotting the DOM.
        try { await Task.Delay(settleMs, ct); } catch { }
        return await ReadHtmlAsync();
    }

    private async Task<string> ReadHtmlAsync()
    {
        var json = await _core.ExecuteScriptAsync("document.documentElement.outerHTML");
        if (string.IsNullOrEmpty(json) || json == "null") return null;
        try { return JsonSerializer.Deserialize<string>(json); }
        catch { return null; }
    }

    private static bool LooksLikeChallenge(string html)
    {
        if (string.IsNullOrEmpty(html)) return false;
        return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);
    }
}

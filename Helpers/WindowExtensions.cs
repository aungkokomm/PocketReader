using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace PocketReader.Helpers;

public static class WindowExtensions
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const int GWLP_HWNDPARENT = -8;

    // A second Window created from another window's event often opens *behind* the owner
    // in WinUI 3. Activate, raise its z-order, and force it to the foreground.
    public static void BringToFront(this Window window)
    {
        try
        {
            window.Activate();
            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow.GetFromWindowId(windowId)?.MoveInZOrderAtTop();
            SetForegroundWindow(hwnd);
        }
        catch { /* non-fatal */ }
    }

    // Make this window owned by another, so it always sits above its owner (like a proper
    // dialog) and doesn't get a separate taskbar button.
    public static void SetOwner(this Window window, Window owner)
    {
        try
        {
            var child = WindowNative.GetWindowHandle(window);
            var parent = WindowNative.GetWindowHandle(owner);
            SetWindowLongPtr(child, GWLP_HWNDPARENT, parent);
        }
        catch { /* non-fatal */ }
    }

    // Open as a proper owned dialog: stays above the owner, focused, on top.
    public static void ShowOwnedDialog(this Window window, Window owner)
    {
        window.SetOwner(owner);
        window.BringToFront();
    }

    // Modern Mica material when the OS supports it (Windows 11). On Windows 10 this is a
    // no-op and the window keeps its solid background — so it never regresses to black.
    public static void TryEnableMica(this Window window)
    {
        try
        {
            if (!Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) return;
            window.SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            if (window.Content is Panel p) p.Background = null;          // let Mica show through
            else if (window.Content is Control c) c.Background = null;
        }
        catch { /* non-fatal */ }
    }

    // Global light/dark applied to a window's root element.
    public static void ApplyTheme(this Window window, string theme)
    {
        if (window.Content is FrameworkElement fe)
        {
            fe.RequestedTheme = theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            };
        }
    }

    // WinUI 3 doesn't auto-apply the exe icon to the window; set it via AppWindow.
    public static void SetAppIcon(this Window window)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            // Content copy preserves the relative path → ships under Assets\app.ico.
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!System.IO.File.Exists(iconPath))
                iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath))
                appWindow.SetIcon(iconPath);
        }
        catch
        {
            // Non-fatal — missing icon should never crash the window.
        }
    }
}

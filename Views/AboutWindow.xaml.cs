using System;
using Microsoft.UI.Xaml;
using PocketReader.Helpers;
using PocketReader.Services;

namespace PocketReader;

public sealed partial class AboutWindow : Window
{
    public AboutWindow(Window owner, AppSettings settings)
    {
        this.InitializeComponent();

        this.SetAppIcon();
        this.ApplyTheme(settings.Theme);
        this.TryEnableMica();
        Title = "About PocketReader";

        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(460, 440));
        }
        catch { }

        VersionText.Text = "Version " + GetAppVersion();
        CopyrightText.Text = $"© {DateTime.Now.Year} PocketReader";
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    private static string GetAppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info.Substring(0, plus) : info;
            }
            var v = asm.GetName().Version;
            if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
        return "1.8.2";
    }
}

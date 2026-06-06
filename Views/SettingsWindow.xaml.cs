using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Services;
using Windows.Storage.Pickers;

namespace PocketReader;

public sealed partial class SettingsWindow : Window
{
    private readonly Window _owner;
    private readonly DatabaseService _db;
    private readonly RaindropService _raindrop;
    private readonly AppSettings _settings;
    private readonly Action _onChanged;
    private bool _loaded;

    public SettingsWindow(Window owner, DatabaseService db, RaindropService raindrop, AppSettings settings, Action onChanged)
    {
        this.InitializeComponent();

        _owner = owner;
        _db = db;
        _raindrop = raindrop;
        _settings = settings;
        _onChanged = onChanged;

        this.SetAppIcon();
        this.ApplyTheme(_settings.Theme);
        this.TryEnableMica();
        Title = "PocketReader — Settings";

        // Default size (resizable by the user).
        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(660, 780));
        }
        catch { }

        ThemeCombo.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        ReaderThemeCombo.SelectedIndex = _settings.ReaderTheme switch { "Sepia" => 1, "Dark" => 2, _ => 0 };
        BatchSizeBox.Value = _settings.BatchSize;
        ConcurrencyBox.Value = _settings.Concurrency;
        AccountStatus.Text = _raindrop.IsAuthenticated ? "Connected to Raindrop.io" : "Not connected";
        LogoutButton.IsEnabled = _raindrop.IsAuthenticated;
        DataPath.Text = System.IO.Path.Combine(AppContext.BaseDirectory, "data");

        VersionText.Text = $"PocketReader v{GetAppVersion()}";

        _loaded = true;
    }

    // Robust version string — prefer the informational version (set from <Version>),
    // fall back to assembly version, then a literal so this is never blank.
    private static string GetAppVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var info = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+'); // strip any "+gitsha" suffix
                return plus > 0 ? info.Substring(0, plus) : info;
            }
            var v = asm.GetName().Version;
            if (v != null) return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }
        return "1.2.0";
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    private void OnManageTags(object sender, RoutedEventArgs e)
    {
        // Opens its own owned window so it can sit beside Settings.
        new TagManagerWindow(_owner, _db, _settings, _onChanged).ShowOwnedDialog(_owner);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        new AboutWindow(this, _settings).ShowOwnedDialog(this);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Default";
        _settings.Theme = tag;
        AppSettingsService.Save(_settings);
        _owner.ApplyTheme(tag);
        this.ApplyTheme(tag);
    }

    private void OnReaderThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (ReaderThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Light";
        _settings.ReaderTheme = tag;
        AppSettingsService.Save(_settings);
    }

    private void OnDownloadSettingChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loaded) return;
        if (!double.IsNaN(BatchSizeBox.Value)) _settings.BatchSize = (int)BatchSizeBox.Value;
        if (!double.IsNaN(ConcurrencyBox.Value)) _settings.Concurrency = (int)ConcurrencyBox.Value;
        AppSettingsService.Save(_settings);
    }

    private IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
            picker.SuggestedFileName = $"pocketreader_backup_{DateTime.Now:yyyyMMdd}";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                var json = await _db.ExportAsJsonAsync();
                await Windows.Storage.FileIO.WriteTextAsync(file, json);
                BackupStatus.Text = "Export complete.";
            }
        }
        catch (Exception ex) { BackupStatus.Text = $"Export failed: {ex.Message}"; }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                await _db.ImportFromJsonAsync(json);
                BackupStatus.Text = "Import complete.";
                _onChanged?.Invoke();
            }
        }
        catch (Exception ex) { BackupStatus.Text = $"Import failed: {ex.Message}"; }
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        _raindrop.Logout();
        AccountStatus.Text = "Not connected";
        LogoutButton.IsEnabled = false;
        _onChanged?.Invoke();
    }

    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderPathAsync(path);
        }
        catch { }
    }
}

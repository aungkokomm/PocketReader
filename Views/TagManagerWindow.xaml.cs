using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Services;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;

namespace PocketReader;

public class TagRow
{
    public string Name { get; set; }
    public int Count { get; set; }
    public string CountText => Count > 0 ? $"{Count}" : "new";
}

public sealed partial class TagManagerWindow : Window
{
    private readonly DatabaseService _db;
    private readonly Action _onChanged;
    private List<string> _dragged = new();

    public ObservableCollection<TagRow> Tags { get; } = new();

    public TagManagerWindow(Window owner, DatabaseService db, AppSettings settings, Action onChanged)
    {
        this.InitializeComponent();
        _db = db;
        _onChanged = onChanged;

        this.SetAppIcon();
        this.ApplyTheme(settings.Theme);
        this.TryEnableMica();
        Title = "PocketReader — Tag Manager";

        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(560, 740));
        }
        catch { }

        this.Activated += async (_, __) => { if (Tags.Count == 0) await ReloadAsync(); };
    }

    private async System.Threading.Tasks.Task ReloadAsync()
    {
        var counts = await _db.GetAllTagCountsAsync();
        Tags.Clear();
        foreach (var (tag, count) in counts) Tags.Add(new TagRow { Name = tag, Count = count });
        StatusText.Text = $"{Tags.Count} tags";
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();
    private async void OnRefresh(object sender, RoutedEventArgs e) => await ReloadAsync();

    // ---- Drag & drop merge --------------------------------------------------

    private void OnTagDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        _dragged = e.Items.OfType<TagRow>().Select(t => t.Name).ToList();
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnTagDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move;
        if (e.DragUIOverride != null) { e.DragUIOverride.Caption = "Merge into"; e.DragUIOverride.IsCaptionVisible = true; }
    }

    private async void OnTagDrop(object sender, DragEventArgs e)
    {
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as TagRow;
        if (target == null || _dragged.Count == 0) return;

        var sources = _dragged.Where(s => !string.Equals(s, target.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        _dragged = new();
        if (sources.Count == 0) return;

        await MergeAsync(sources, target.Name);
    }

    // ---- Operations ---------------------------------------------------------

    private async void OnMerge(object sender, RoutedEventArgs e)
    {
        var selected = TagList.SelectedItems.OfType<TagRow>().ToList();
        if (selected.Count < 2) { StatusText.Text = "Select two or more tags to merge."; return; }

        var target = selected.OrderByDescending(t => t.Count).First().Name; // canonical = biggest
        var sources = selected.Where(t => !string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase)).Select(t => t.Name).ToList();

        if (await ConfirmAsync("Merge tags", $"Merge {sources.Count} tag(s) into “{target}”? This updates every affected article."))
            await MergeAsync(sources, target);
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        var sel = TagList.SelectedItems.OfType<TagRow>().ToList();
        if (sel.Count != 1) { StatusText.Text = "Select exactly one tag to rename."; return; }
        var old = sel[0].Name;

        var newName = await PromptAsync("Rename tag", $"New name for “{old}”:", old);
        if (string.IsNullOrWhiteSpace(newName) || string.Equals(newName, old, StringComparison.Ordinal)) return;

        var n = await _db.RewriteTagsAsync(tags => tags.Select(t => string.Equals(t, old, StringComparison.OrdinalIgnoreCase) ? newName.Trim() : t).ToList());
        await AfterChangeAsync($"Renamed “{old}” → “{newName.Trim()}” in {n} articles.");
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = TagList.SelectedItems.OfType<TagRow>().Select(t => t.Name).ToList();
        if (sel.Count == 0) { StatusText.Text = "Select one or more tags to delete."; return; }

        if (!await ConfirmAsync("Delete tags", $"Remove {sel.Count} tag(s) from every article? The articles stay; only the tag is removed.")) return;

        var set = new HashSet<string>(sel, StringComparer.OrdinalIgnoreCase);
        var n = await _db.RewriteTagsAsync(tags => tags.Where(t => !set.Contains(t)).ToList());
        await AfterChangeAsync($"Removed {sel.Count} tag(s) from {n} articles.");
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New tag", "Tag name (then drag existing tags onto it to fill it):", "");
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (Tags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) { StatusText.Text = "That tag already exists."; return; }
        Tags.Insert(0, new TagRow { Name = name, Count = 0 });
        StatusText.Text = $"Created “{name}” — drag tags onto it to merge, or it'll appear once articles use it.";
    }

    private async System.Threading.Tasks.Task MergeAsync(List<string> sources, string target)
    {
        var set = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        var n = await _db.RewriteTagsAsync(tags => tags.Select(t => set.Contains(t) ? target : t).ToList());
        await AfterChangeAsync($"Merged into “{target}” across {n} articles.");
    }

    private async System.Threading.Tasks.Task AfterChangeAsync(string message)
    {
        await ReloadAsync();
        StatusText.Text = message;
        _onChanged?.Invoke();
    }

    // ---- Dialog helpers -----------------------------------------------------

    private async System.Threading.Tasks.Task<bool> ConfirmAsync(string title, string body)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = "Yes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private async System.Threading.Tasks.Task<string> PromptAsync(string title, string label, string initial)
    {
        var box = new TextBox { Text = initial ?? "", SelectionStart = (initial ?? "").Length };
        var panel = new StackPanel { Spacing = 8, Width = 320 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);

        var dlg = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }
}

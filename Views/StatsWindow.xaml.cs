using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Services;

namespace PocketReader;

public sealed partial class StatsWindow : Window
{
    private readonly DatabaseService _db;

    public StatsWindow(Window owner, DatabaseService db, AppSettings settings)
    {
        this.InitializeComponent();
        _db = db;

        this.SetAppIcon();
        this.ApplyTheme(settings.Theme);
        this.TryEnableMica();
        Title = "PocketReader — Statistics";

        try
        {
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id).Resize(new Windows.Graphics.SizeInt32(820, 720));
        }
        catch { }

        _ = LoadAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => this.Close();

    private async System.Threading.Tasks.Task LoadAsync()
    {
        StatusLine.Text = "Loading…";
        ReadingStats s;
        try { s = await _db.GetStatsAsync(); }
        catch (Exception ex) { StatusLine.Text = "Error: " + ex.Message; return; }

        StatArticles.Text = s.ArticlesRead.ToString("N0");
        StatWords.Text = FormatCount(s.TotalWords);
        StatTime.Text = FormatTime(s.TotalSeconds);
        StatStreak.Text = s.DayStreak.ToString("N0");

        BuildChart(s.Last30);
        BuildList(SourcesPanel, s.TopSources.Select(x => (x.Source, x.Count)).ToList(), "No reads yet.");
        BuildList(TagsPanel, s.TopTags.Select(x => (x.Tag, x.Count)).ToList(), "No tags on read articles.");

        LibraryLine.Text = $"Library: {s.ArticlesTotal:N0} articles · {s.ArticlesCached:N0} cached for offline.";
        StatusLine.Text = "";
    }

    private void BuildChart(List<(string Day, int Seconds)> data)
    {
        ChartPanel.Children.Clear();
        ChartPanel.ColumnDefinitions.Clear();
        var max = data.Count > 0 ? data.Max(d => d.Seconds) : 0;
        var accent = Application.Current.Resources["AccentFillColorDefaultBrush"] as Brush;
        var faint = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush;

        for (var i = 0; i < data.Count; i++)
        {
            ChartPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var h = max > 0 ? Math.Max(3, data[i].Seconds / (double)max * 84) : 3;
            var bar = new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = h,
                Margin = new Thickness(1.5, 0, 1.5, 0),
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = data[i].Seconds > 0 ? accent : faint
            };
            var mins = data[i].Seconds / 60;
            ToolTipService.SetToolTip(bar, $"{FormatDay(data[i].Day)} · {(mins > 0 ? mins + " min" : data[i].Seconds + " s")}");
            Grid.SetColumn(bar, i);
            ChartPanel.Children.Add(bar);
        }

        ChartHint.Text = max > 0
            ? $"Most in a day: {FormatTime(max)}"
            : "No reading time recorded yet — open an article to start.";
    }

    private static void BuildList(Panel panel, List<(string Name, int Count)> items, string emptyMsg)
    {
        panel.Children.Clear();
        var muted = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        if (items.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = emptyMsg, FontSize = 12, Foreground = muted });
            return;
        }
        foreach (var (name, count) in items)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var t = new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            var c = new TextBlock { Text = count.ToString("N0"), FontSize = 13, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(t, 0);
            Grid.SetColumn(c, 1);
            g.Children.Add(t);
            g.Children.Add(c);
            panel.Children.Add(g);
        }
    }

    private static string FormatCount(long n)
    {
        if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.#") + "M";
        if (n >= 10_000) return (n / 1_000.0).ToString("0.#") + "k";
        return n.ToString("N0");
    }

    private static string FormatTime(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        var hours = minutes / 60;
        var rem = minutes % 60;
        return rem > 0 ? $"{hours}h {rem}m" : $"{hours}h";
    }

    private static string FormatDay(string yyyyMMdd)
    {
        return DateTime.TryParse(yyyyMMdd, out var d) ? d.ToString("MMM d") : yyyyMMdd;
    }
}

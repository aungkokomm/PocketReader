using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PocketReader.Data;
using PocketReader.Helpers;
using PocketReader.Models;
using PocketReader.Services;
using System.Collections.ObjectModel;

namespace PocketReader;

public sealed partial class MainWindow : Window
{
    private DatabaseService _db;
    private RaindropService _raindropService;
    private ArticleReaderService _readerService;
    private TaggingService _tagging;
    private IncrementalArticleSource _articles;   // lazy/windowed display collection
    private List<Article> _view = new();          // the full current view (for batch ops + counts)
    private BrowserRenderer _browser;             // hidden WebView2 for blocked/JS sites
    private AppSettings _settings;
    private CancellationTokenSource _searchCts;
    private bool _compact;                         // density
    private Action _emptyAction;                   // primary action for the empty state
    private DispatcherTimer _infoTimer;            // auto-dismiss for the InfoBar
    private bool _skeletonBuilt;

    private string _currentFilter = "all";
    private NavigationViewItem _lastSelected;
    private bool _suppressNav;
    private bool _initialized;
    private ReaderPage _reader; // single reused reader window (warm WebView2)

    private CancellationTokenSource _dlCts;
    private TaskCompletionSource<bool> _pauseTcs;
    private readonly object _pauseLock = new();

    // Browser OAuth needs a Raindrop app's credentials. They are intentionally NOT shipped
    // in the public source. Supply your own (raindrop.io -> Settings -> Integrations ->
    // create an app; redirect URI http://localhost:8080/callback), or just use the
    // "Sign in with a token" option. Left blank, only browser OAuth is disabled.
    private const string RaindropClientId = "";
    private const string RaindropClientSecret = "";

    public MainWindow()
    {
        this.InitializeComponent();

        _db = new DatabaseService();
        _raindropService = new RaindropService(_db);
        _readerService = new ArticleReaderService(_db);
        _tagging = new TaggingService();
        _articles = new IncrementalArticleSource();
        ArticlesListView.ItemsSource = _articles;
        ArticlesGridView.ItemsSource = _articles;

        _settings = AppSettingsService.Load();

        this.SetAppIcon();
        this.ApplyTheme(_settings.Theme);

        // Modern title bar: draw our own (icon + name) and let Mica show through (Win11).
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);
        this.TryEnableMica();
        Title = "PocketReader";

        this.Activated += OnFirstActivated;

        // Diagnostic hook (inert unless POCKET_SELFTEST=1): construct the reader headlessly
        // so a build pipeline can confirm its XAML parses without a manual click.
        if (Environment.GetEnvironmentVariable("POCKET_SELFTEST") == "1")
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var log = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "selftest.log");
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log));
                    var r = new ReaderPage(_db, _readerService, _tagging);
                    r.Close();
                    var st = new StatsWindow(this, _db, _settings);
                    st.Close();
                    var ab = new AboutWindow(this, _settings);
                    ab.Close();
                    System.IO.File.WriteAllText(log, "READER_OK");
                }
                catch (Exception ex) { System.IO.File.WriteAllText(log, "READER_FAIL: " + ex); }
            });
        }
    }

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        // The hidden WebView2 renderer needs a window handle, so create it here (not in the
        // ctor). It's the fallback for sites that block our HttpClient (Medium/Cloudflare).
        _browser = new BrowserRenderer(this);
        _readerService.BrowserRender = _browser.RenderAsync;

        _lastSelected = NavAll;
        InitViewOptions();
        SetViewMode(_settings.ViewMode == "Card");

        if (_raindropService.IsAuthenticated)
        {
            SyncButton.IsEnabled = true;
            LoginButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            LoginButton.Visibility = Visibility.Visible;
            SyncButton.IsEnabled = false;
            DownloadButton.IsEnabled = false;
        }

        // One-time: build the covering index that makes list/count/tag queries fast on a
        // large, offline-cached library. Only the first launch after upgrade pays for it.
        if (await _db.NeedsListIndexAsync())
        {
            StatusText.Text = "Optimizing your library for fast loading (one-time, please wait)…";
            SyncProgress.IsActive = true;
            try { await _db.BuildListIndexAsync(); } catch { }
            SyncProgress.IsActive = false;
        }

        await RefreshAllAsync();

        // Warm up the local tag model in the background so reader suggestions are ready.
        _ = EnsureTaggingTrainedAsync();

    }

    // ---- Navigation / filters ----------------------------------------------

    private async void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_db == null || _suppressNav) return;

        if (args.IsSettingsSelected)
        {
            await OpenSettingsAsync();
            return;
        }

        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            // Footer "Manage tags" is an action, not a filter — open the window and keep
            // the current view selected.
            if (tag == "action:managetags")
            {
                _suppressNav = true;
                Nav.SelectedItem = _lastSelected ?? NavAll;
                _suppressNav = false;
                OnManageTagsClick(null, null);
                return;
            }

            _lastSelected = item;
            _currentFilter = tag;
            SectionTitle.Text = tag.StartsWith("rating:")
                ? $"Rated {tag.Substring(7)}★"
                : item.Content?.ToString() ?? "Articles";
            ApplyBinContext();
            await LoadArticlesAsync();
        }
    }

    // Swap the multi-select bar between normal actions and Recycle-Bin actions (Restore /
    // Delete forever).
    private void ApplyBinContext()
    {
        var bin = InRecycleBin;
        if (BatchNormalActions != null) BatchNormalActions.Visibility = bin ? Visibility.Collapsed : Visibility.Visible;
        if (BatchRestoreButton != null) BatchRestoreButton.Visibility = bin ? Visibility.Visible : Visibility.Collapsed;
        if (BatchDeleteText != null) BatchDeleteText.Text = bin ? "Delete forever" : "Delete";
        if (EmptyBinButton != null) EmptyBinButton.Visibility = bin ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnEmptyBinClick(object sender, RoutedEventArgs e)
    {
        var (_, _, _, _, _, deleted) = await _db.GetFilterCountsAsync();
        if (deleted == 0) { ShowInfo("Recycle Bin is already empty."); return; }
        if (!await ConfirmDeleteAsync($"Empty Recycle Bin ({deleted})?",
                "All articles in the Recycle Bin will be permanently deleted. This can't be undone.")) return;
        await _db.EmptyRecycleBinAsync();
        await RefreshAllAsync();
        ShowInfo($"Recycle Bin emptied ({deleted} removed).");
    }

    private async Task RefreshAllAsync()
    {
        await LoadArticlesAsync();
        await UpdateCountBadgesAsync();
        await UpdateRatingBadgesAsync();
        await UpdateTagItemsAsync();
        await UpdateDownloadButtonAsync();
    }

    private async Task LoadArticlesAsync()
    {
        // Show a skeleton only if the load is slow enough to matter (avoids flicker on the
        // now-instant indexed queries).
        var skel = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        skel.Tick += (s, e) => { skel.Stop(); ShowSkeleton(true); };
        skel.Start();
        try
        {
            EmptyState.Visibility = Visibility.Collapsed;

            List<Article> articles;
            if (_currentFilter == "fav")
                articles = await _db.GetFavoritesAsync();
            else if (_currentFilter == "unread")
                articles = await _db.GetUnreadAsync();
            else if (_currentFilter == "archive")
                articles = await _db.GetArchivedAsync();
            else if (_currentFilter == "notes")
                articles = await _db.GetNotedAsync();
            else if (_currentFilter == "highlights")
                articles = await _db.GetHighlightedAsync();
            else if (_currentFilter == "deleted")
                articles = await _db.GetDeletedAsync();
            else if (_currentFilter != null && _currentFilter.StartsWith("rating:")
                     && int.TryParse(_currentFilter.Substring(7), out var rv))
                articles = await _db.GetByRatingAsync(rv);
            else if (_currentFilter != null && _currentFilter.StartsWith("tag:"))
            {
                var tag = _currentFilter.Substring(4);
                var active = await _db.GetActiveAsync();
                articles = active.Where(a => a.GetTagsList()
                    .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else
                articles = await _db.GetActiveAsync();

            _view = articles;
            _articles.Load(articles);   // paints the first screenful instantly; rest on scroll

            StatusText.Text = $"{articles.Count} article{(articles.Count == 1 ? "" : "s")}";
            ShowEmptyState(articles.Count == 0);
            if (CardToggle.IsChecked == true) DispatcherQueue.TryEnqueue(FitCards);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error loading articles: {ex.Message}";
        }
        finally
        {
            skel.Stop();
            ShowSkeleton(false);
        }
    }

    private void ShowEmptyState(bool empty)
    {
        if (!empty) { EmptyState.Visibility = Visibility.Collapsed; return; }

        string text; string btn = null; Action act = null;

        if (!_raindropService.IsAuthenticated)
        {
            text = "Sign in with Raindrop, then Sync to pull your articles.";
            btn = "Sign in"; act = () => OnLoginClick(null, null);
        }
        else
        {
            switch (_currentFilter)
            {
                case "fav": text = "No favorites yet. Star articles in Raindrop to see them here."; break;
                case "unread": text = "You're all caught up — nothing unread."; break;
                case "notes": text = "No notes yet. Open an article and write a note in the reader."; break;
                case "highlights": text = "No highlights yet. Select text in the reader and click the Highlight button."; break;
                case "archive": text = "Your archive is empty."; break;
                case "deleted": text = "Recycle Bin is empty. Deleted articles appear here and can be restored."; break;
                default:
                    if (_currentFilter != null && _currentFilter.StartsWith("tag:"))
                        text = "No articles with this tag.";
                    else if (_currentFilter != null && _currentFilter.StartsWith("rating:"))
                        text = $"Nothing rated {_currentFilter.Substring(7)}★ yet — click the stars on a card to rate it.";
                    else
                    {
                        text = "No articles yet — Sync to fetch your Raindrop bookmarks.";
                        btn = "Sync now"; act = () => _ = DoSyncAsync(true);
                    }
                    break;
            }
        }

        EmptyText.Text = text;
        _emptyAction = act;
        EmptyActionButton.Content = btn ?? "";
        EmptyActionButton.Visibility = btn != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void OnEmptyAction(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    // ---- Transient InfoBar toasts ------------------------------------------

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Success, int ms = 3500)
    {
        MainInfoBar.Title = "";
        MainInfoBar.Message = message;
        MainInfoBar.Severity = severity;
        MainInfoBar.IsOpen = true;

        _infoTimer ??= new DispatcherTimer();
        _infoTimer.Stop();
        _infoTimer.Tick -= OnInfoTick;
        _infoTimer.Tick += OnInfoTick;
        _infoTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _infoTimer.Start();
    }

    private void OnInfoTick(object sender, object e)
    {
        _infoTimer.Stop();
        MainInfoBar.IsOpen = false;
    }

    // ---- View options: sort + density --------------------------------------

    private void InitViewOptions()
    {
        _db.SortOrder = string.IsNullOrEmpty(_settings.SortOrder) ? "DateDesc" : _settings.SortOrder;
        SortNewest.IsChecked = _db.SortOrder == "DateDesc";
        SortOldest.IsChecked = _db.SortOrder == "DateAsc";
        SortTitle.IsChecked = _db.SortOrder == "Title";
        DensityComfort.IsChecked = _settings.Density != "Compact";
        DensityCompact.IsChecked = _settings.Density == "Compact";
        ApplyDensity();
    }

    private async void OnSortChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem r && r.Tag is string s)
        {
            _settings.SortOrder = s; _db.SortOrder = s; AppSettingsService.Save(_settings);
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) await RunSearchAsync(SearchBox.Text);
            else await LoadArticlesAsync();
        }
    }

    private void OnDensityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem r && r.Tag is string s)
        {
            _settings.Density = s; AppSettingsService.Save(_settings);
            ApplyDensity();
        }
    }

    private void ApplyDensity()
    {
        _compact = _settings.Density == "Compact";
        ArticlesListView.ItemContainerStyle = (Style)RootLayout.Resources[_compact ? "ListCompact" : "ListComfort"];
        if (CardToggle.IsChecked == true) DispatcherQueue.TryEnqueue(FitCards);
    }

    // ---- Star ratings -------------------------------------------------------

    // Each star is a Button. A Button inside a ListView/GridView item is guaranteed by WinUI
    // not to raise the item's click — so tapping a star sets the rating and never opens the
    // reader. We own the rating logic entirely (no RatingControl preview/commit quirks).
    private async void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.DataContext is not ArticleViewModel vm) return;
        if (!int.TryParse(b.Tag as string, out var star)) return;

        // Click the current rating again to clear it.
        var newVal = vm.Article.Rating == star ? 0 : star;
        if (vm.Article.Rating == newVal) return;

        vm.Article.Rating = newVal;
        vm.RaiseRatingChanged();                 // refresh the star glyphs in place
        await _db.SetRatingAsync(vm.Article.Id, newVal);
        await UpdateRatingBadgesAsync();

        // If we're viewing a specific rating and this item no longer matches, drop it.
        if (_currentFilter != null && _currentFilter.StartsWith("rating:")
            && int.TryParse(_currentFilter.Substring(7), out var rv) && rv != newVal)
        {
            _articles.Remove(vm);
        }
    }

    private async Task UpdateRatingBadgesAsync()
    {
        try
        {
            var rc = await _db.GetRatingCountsAsync();
            NavRate5.InfoBadge = rc[5] > 0 ? new InfoBadge { Value = rc[5] } : null;
            NavRate4.InfoBadge = rc[4] > 0 ? new InfoBadge { Value = rc[4] } : null;
            NavRate3.InfoBadge = rc[3] > 0 ? new InfoBadge { Value = rc[3] } : null;
            NavRate2.InfoBadge = rc[2] > 0 ? new InfoBadge { Value = rc[2] } : null;
            NavRate1.InfoBadge = rc[1] > 0 ? new InfoBadge { Value = rc[1] } : null;
        }
        catch { }
    }

    // ---- Card hover ---------------------------------------------------------

    private void OnCardEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.FindName("CardHover") is UIElement h) h.Opacity = 1;
    }

    private void OnCardExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.FindName("CardHover") is UIElement h) h.Opacity = 0;
    }

    // ---- Skeleton placeholder (slow loads) ---------------------------------

    private void ShowSkeleton(bool on)
    {
        if (on) EnsureSkeleton();
        Skeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureSkeleton()
    {
        if (_skeletonBuilt) return;
        _skeletonBuilt = true;
        var brush = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;
        for (var i = 0; i < 9; i++)
        {
            var row = new Grid { Padding = new Thickness(8, 10, 8, 10), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Background = brush };
            Grid.SetColumn(icon, 0);

            var lines = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(new Border { Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(4), Background = brush });
            lines.Children.Add(new Border { Height = 10, Width = 180, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(4), Background = brush, Opacity = 0.6 });
            Grid.SetColumn(lines, 1);

            row.Children.Add(icon);
            row.Children.Add(lines);
            Skeleton.Items.Add(row);
        }
    }

    // Cheap: COUNT queries. Safe to call often (e.g. after opening an article).
    private async Task UpdateCountBadgesAsync()
    {
        try
        {
            var (total, fav, unread, archived, noted, deleted) = await _db.GetFilterCountsAsync();
            NavAll.InfoBadge = total > 0 ? new InfoBadge { Value = total } : null;
            NavFav.InfoBadge = fav > 0 ? new InfoBadge { Value = fav } : null;
            NavUnread.InfoBadge = unread > 0 ? new InfoBadge { Value = unread } : null;
            NavNotes.InfoBadge = noted > 0 ? new InfoBadge { Value = noted } : null;
            NavArchive.InfoBadge = archived > 0 ? new InfoBadge { Value = archived } : null;
            NavRecycle.InfoBadge = deleted > 0 ? new InfoBadge { Value = deleted } : null;
            var highlighted = await _db.GetHighlightedCountAsync();
            NavHighlights.InfoBadge = highlighted > 0 ? new InfoBadge { Value = highlighted } : null;
        }
        catch { }
    }

    // Heavier: scans all articles to tally tags. Call only after sync/import.
    private async Task UpdateTagItemsAsync()
    {
        try
        {
            var oldTags = Nav.MenuItems.OfType<NavigationViewItem>()
                .Where(i => i.Tag is string s && s.StartsWith("tag:")).ToList();
            foreach (var it in oldTags) Nav.MenuItems.Remove(it);

            var tagCounts = await _db.GetTagCountsAsync();
            foreach (var (tag, count) in tagCounts)
            {
                Nav.MenuItems.Add(new NavigationViewItem
                {
                    Content = tag,
                    Tag = $"tag:{tag}",
                    Icon = new SymbolIcon(Symbol.Tag),
                    InfoBadge = new InfoBadge { Value = count }
                });
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Sidebar error: {ex.Message}";
        }
    }

    private async Task UpdateDownloadButtonAsync()
    {
        try
        {
            var total = await _db.GetCountAsync();
            var cached = await _db.GetOfflineReadyCountAsync();
            DownloadButton.IsEnabled = _raindropService.IsAuthenticated && total > 0 && cached < total;
        }
        catch { }
    }

    // ---- View toggle --------------------------------------------------------

    private void OnViewToggle(object sender, RoutedEventArgs e)
    {
        var cards = CardToggle.IsChecked == true;
        SetViewMode(cards);
        _settings.ViewMode = cards ? "Card" : "List";
        AppSettingsService.Save(_settings);
    }

    private void SetViewMode(bool cards)
    {
        CardToggle.IsChecked = cards;
        ArticlesGridView.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        ArticlesListView.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        if (cards) DispatcherQueue.TryEnqueue(FitCards);
    }

    // ---- Login / Sync / Download -------------------------------------------

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(RaindropClientId) || string.IsNullOrEmpty(RaindropClientSecret))
        {
            await new ContentDialog
            {
                Title = "Browser sign-in isn't configured",
                Content = "This build has no Raindrop app credentials. Add your own in the source, "
                          + "or use “Sign in with a token” instead.",
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            }.ShowAsync();
            return;
        }
        try
        {
            var oauth = new OAuthHelper();
            await Windows.System.Launcher.LaunchUriAsync(new Uri(oauth.GetAuthorizationUrl(RaindropClientId)));

            StatusText.Text = "Waiting for authorization in your browser...";
            var authCode = await oauth.ListenForCallbackAsync();

            StatusText.Text = "Exchanging token...";
            var (accessToken, _) = await oauth.ExchangeCodeForTokenAsync(authCode, RaindropClientId, RaindropClientSecret);

            _raindropService.SetAccessToken(accessToken);
            SyncButton.IsEnabled = true;
            LoginButton.Visibility = Visibility.Collapsed;

            StatusText.Text = "Authenticated. Click Sync to fetch your bookmarks.";
            await RefreshAllAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Login failed: {ex.Message}";
        }
    }

    private async void OnTokenLoginClick(object sender, RoutedEventArgs e)
    {
        // Build a clear, numbered step list for non-technical users.
        StackPanel Step(string num, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = num, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, MinWidth = 16 });
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            return row;
        }

        var openBtn = new HyperlinkButton
        {
            Content = "Open the Raindrop Integrations page",
            NavigateUri = new Uri("https://app.raindrop.io/settings/integrations"),
            Margin = new Thickness(20, 0, 0, 0)
        };
        var box = new TextBox { PlaceholderText = "Paste the token here", TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Spacing = 8, Width = 430 };
        panel.Children.Add(new TextBlock
        {
            Text = "Get a token from Raindrop (one-time, takes a minute):",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(Step("1.", "Open the page below and sign in to Raindrop if it asks."));
        panel.Children.Add(openBtn);
        panel.Children.Add(Step("2.", "Click “+ Create new app”, type any name, then click the app you just made to open it."));
        panel.Children.Add(Step("3.", "Scroll down to “For Developers” and click “Create test token” (confirm if asked)."));
        panel.Children.Add(Step("4.", "Copy the token it shows, come back here, and paste it below:"));
        panel.Children.Add(box);

        var dlg = new ContentDialog
        {
            Title = "Sign in with a token",
            Content = panel,
            PrimaryButtonText = "Sign in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var token = box.Text?.Trim();
        if (string.IsNullOrEmpty(token)) { StatusText.Text = "No token entered."; return; }

        StatusText.Text = "Validating token...";
        if (!await _raindropService.ValidateTokenAsync(token))
        {
            StatusText.Text = "That token didn't work — double-check you copied the test token correctly.";
            return;
        }

        _raindropService.SetAccessToken(token);
        SyncButton.IsEnabled = true;
        LoginButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Signed in. Open Sync → Sync new bookmarks.";
        await RefreshAllAsync();
    }

    private async void OnSyncNewClick(object sender, RoutedEventArgs e) => await DoSyncAsync(incremental: true);
    private async void OnSyncAllClick(object sender, RoutedEventArgs e) => await DoSyncAsync(incremental: false);

    private async Task DoSyncAsync(bool incremental)
    {
        try
        {
            SyncProgress.IsActive = true;

            DateTime? since = null;
            if (incremental)
            {
                if (DateTime.TryParse(_settings.LastSyncUpdate, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var wm))
                {
                    since = wm;
                }
                else
                {
                    // No watermark yet — bootstrap from the newest existing article so we
                    // skip the slow full baseline. Small buffer guards against clock skew.
                    var maxSaved = await _db.GetMaxDateSavedAsync();
                    if (maxSaved.HasValue) since = maxSaved.Value.AddDays(-1);
                }
            }

            StatusText.Text = since.HasValue ? "Checking for new bookmarks..." : "Full sync...";
            var progress = new Progress<(int, int)>(r => StatusText.Text = $"Syncing... {r.Item1} new/updated");

            var (processed, newWatermark) = await _raindropService.SyncAsync(since, progress);

            if (newWatermark > DateTime.MinValue)
            {
                _settings.LastSyncUpdate = newWatermark.ToString("O");
                AppSettingsService.Save(_settings);
            }

            await RefreshAllAsync();
            var syncMsg = processed == 0
                ? "Up to date — no new bookmarks."
                : $"Synced {processed} new/updated bookmark{(processed == 1 ? "" : "s")}.";
            StatusText.Text = syncMsg;
            ShowInfo(syncMsg);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Sync failed: {ex.Message}";
        }
        finally
        {
            SyncProgress.IsActive = false;
        }
    }

    private async void OnDownloadOfflineClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Cache the current view (filter) in configurable batches per click.
            var batchSize = Math.Max(1, _settings.BatchSize);
            var concurrency = Math.Clamp(_settings.Concurrency, 1, 24);
            var pending = _view.Where(a => !a.ContentCached).ToList();
            if (pending.Count == 0)
            {
                StatusText.Text = "Everything in this view is already cached.";
                return;
            }

            var thisBatch = pending.Take(batchSize).ToList();
            var remaining = pending.Count - thisBatch.Count;
            var section = SectionTitle.Text;

            _dlCts = new CancellationTokenSource();
            ShowDownloadControls(true);
            DownloadButton.IsEnabled = false;
            DownloadProgress.Value = 0;
            DownloadProgress.Maximum = thisBatch.Count;

            var progress = new Progress<(int Current, int Total)>(r =>
            {
                DownloadProgress.Maximum = r.Total;
                DownloadProgress.Value = r.Current;
                var pct = r.Total > 0 ? (r.Current * 100 / r.Total) : 0;
                StatusText.Text = $"Caching '{section}'... {r.Current}/{r.Total} ({pct}%)";
            });

            var stopped = false;
            try
            {
                await _readerService.BatchCacheContentAsync(thisBatch, progress, _dlCts.Token, WaitIfPausedAsync, concurrency);
                stopped = _dlCts.IsCancellationRequested;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Download failed: {ex.Message}";
            }
            finally
            {
                ShowDownloadControls(false);
                _dlCts?.Dispose();
                _dlCts = null;
                lock (_pauseLock) { _pauseTcs = null; }
                PauseResumeButton.Content = "Pause";
            }

            // Flip visible cards to the "Offline" badge now that their Content is cached.
            foreach (var vm in _articles) vm.RefreshOffline();

            await UpdateCountBadgesAsync();
            await UpdateDownloadButtonAsync();

            var cachedNow = thisBatch.Count(a => a.ContentCached);
            if (stopped)
                StatusText.Text = $"Stopped — {cachedNow} cached in '{section}'.";
            else if (remaining > 0)
            {
                StatusText.Text = $"Cached {cachedNow} in '{section}'. {remaining} left — click Download for the next {Math.Min(batchSize, remaining)}.";
                ShowInfo($"Cached {cachedNow} — {remaining} left in '{section}'.");
            }
            else
            {
                StatusText.Text = $"'{section}' cached for offline ({cachedNow} this batch).";
                ShowInfo($"'{section}' is ready offline.");
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download failed: {ex.Message}";
        }
    }

    // Lightweight pass: follow short-link redirects + grab covers, without storing content.
    private async void OnResolveLinksClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var batchSize = Math.Max(1, _settings.BatchSize);
            var concurrency = Math.Clamp(_settings.Concurrency, 1, 24);

            var pending = _view
                .Where(a => (string.IsNullOrEmpty(a.ResolvedUrl) &&
                             PocketReader.Helpers.UrlHelper.NeedsResolution(PocketReader.Helpers.UrlHelper.Unwrap(a.Url)))
                            || string.IsNullOrEmpty(a.Cover))
                .ToList();

            if (pending.Count == 0)
            {
                StatusText.Text = "Nothing to fix in this view — links resolved and covers present.";
                return;
            }

            var thisBatch = pending.Take(batchSize).ToList();
            var remaining = pending.Count - thisBatch.Count;
            var section = SectionTitle.Text;

            _dlCts = new CancellationTokenSource();
            ShowDownloadControls(true);
            DownloadProgress.Value = 0;
            DownloadProgress.Maximum = thisBatch.Count;

            var progress = new Progress<(int Current, int Total)>(r =>
            {
                DownloadProgress.Maximum = r.Total;
                DownloadProgress.Value = r.Current;
                var pct = r.Total > 0 ? (r.Current * 100 / r.Total) : 0;
                StatusText.Text = $"Fixing links & covers in '{section}'... {r.Current}/{r.Total} ({pct}%)";
            });

            var stopped = false;
            try
            {
                await _readerService.ResolveLinksAsync(thisBatch, progress, _dlCts.Token, WaitIfPausedAsync, concurrency);
                stopped = _dlCts.IsCancellationRequested;
            }
            catch (Exception ex) { StatusText.Text = $"Fix failed: {ex.Message}"; }
            finally
            {
                ShowDownloadControls(false);
                _dlCts?.Dispose();
                _dlCts = null;
                lock (_pauseLock) { _pauseTcs = null; }
                PauseResumeButton.Content = "Pause";
            }

            await LoadArticlesAsync(); // reflect resolved domains + new covers
            await UpdateCountBadgesAsync();
            StatusText.Text = stopped
                ? "Stopped."
                : remaining > 0
                    ? $"Fixed {thisBatch.Count} in '{section}'. {remaining} left — click again for the next {Math.Min(batchSize, remaining)}."
                    : $"Links & covers fixed in '{section}'.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Fix failed: {ex.Message}";
        }
    }

    private async Task EnsureTaggingTrainedAsync()
    {
        if (_tagging.IsTrained) return;
        var all = await _db.GetActiveAsync();           // titles + tags, off-thread, no content
        await Task.Run(() => _tagging.Train(all));
    }

    // Auto-apply high-confidence tags (learned + domain) across the current view.
    private async void OnAutoTagClick(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Learning your tags...";
            await EnsureTaggingTrainedAsync();

            var items = _view.ToList();
            if (items.Count == 0) { StatusText.Text = "No articles in this view."; return; }

            _dlCts = new CancellationTokenSource();
            var ct = _dlCts.Token;
            ShowDownloadControls(true);
            DownloadProgress.Value = 0;
            DownloadProgress.Maximum = items.Count;
            var section = SectionTitle.Text;

            int taggedArticles = 0, addedTags = 0;

            await Task.Run(async () =>
            {
                var pending = new List<(int Id, string TagsCsv)>();
                var processed = 0;

                foreach (var art in items)
                {
                    if (ct.IsCancellationRequested) break;
                    await WaitIfPausedAsync();

                    var existing = art.GetTagsList();
                    var auto = _tagging.Suggest(art.Title, art.Url, existing)
                        .Where(s => s.Auto).Select(s => s.Tag).ToList();

                    if (auto.Count > 0)
                    {
                        var merged = existing.Concat(auto).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        art.SetTags(merged);
                        pending.Add((art.Id, string.Join(",", merged)));
                        taggedArticles++;
                        addedTags += auto.Count;
                    }

                    processed++;
                    if (processed % 50 == 0)
                    {
                        var p = processed;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            DownloadProgress.Value = p;
                            StatusText.Text = $"Auto-tagging '{section}'... {p}/{items.Count}";
                        });
                    }

                    if (pending.Count >= 500)
                    {
                        var chunk = pending.ToList();
                        pending.Clear();
                        await _db.SetTagsManyAsync(chunk);
                    }
                }

                if (pending.Count > 0) await _db.SetTagsManyAsync(pending);
            });

            ShowDownloadControls(false);
            _dlCts?.Dispose();
            _dlCts = null;

            await RefreshAllAsync();
            var tagMsg = $"Auto-tagged {taggedArticles} article{(taggedArticles == 1 ? "" : "s")} " +
                         $"with {addedTags} tag{(addedTags == 1 ? "" : "s")} in '{section}'.";
            StatusText.Text = tagMsg;
            ShowInfo(tagMsg);
        }
        catch (Exception ex)
        {
            ShowDownloadControls(false);
            StatusText.Text = $"Auto-tag failed: {ex.Message}";
        }
    }

    private Task WaitIfPausedAsync()
    {
        lock (_pauseLock) { return _pauseTcs?.Task ?? Task.CompletedTask; }
    }

    private void ShowDownloadControls(bool show)
    {
        var v = show ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Visibility = v;
        PauseResumeButton.Visibility = v;
        StopButton.Visibility = v;
        SyncProgress.IsActive = show;
    }

    private void OnPauseResumeClick(object sender, RoutedEventArgs e)
    {
        lock (_pauseLock)
        {
            if (_pauseTcs == null)
            {
                _pauseTcs = new TaskCompletionSource<bool>();
                PauseResumeButton.Content = "Resume";
                StatusText.Text = "Paused.";
            }
            else
            {
                _pauseTcs.TrySetResult(true);
                _pauseTcs = null;
                PauseResumeButton.Content = "Pause";
            }
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        _dlCts?.Cancel();
        lock (_pauseLock) { _pauseTcs?.TrySetResult(true); _pauseTcs = null; } // unblock paused workers
        PauseResumeButton.Content = "Pause";
    }

    // ---- Search / open ------------------------------------------------------

    private async void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await RunSearchAsync(args.QueryText);
    }

    // Instant search as the user types, lightly debounced.
    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        var text = sender.Text;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(text))
        {
            await LoadArticlesAsync();
            SectionTitle.Text = _lastSelected?.Content?.ToString() ?? "All";
            return;
        }

        try { await Task.Delay(200, token); } catch { return; }
        if (token.IsCancellationRequested) return;

        await RunSearchAsync(text);
    }

    private async void OnSearchEscape(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Text = "";
        SectionTitle.Text = _lastSelected?.Content?.ToString() ?? "All";
        await LoadArticlesAsync();
        ArticlesListView.Focus(FocusState.Programmatic);
    }

    private async Task RunSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            await LoadArticlesAsync();
            return;
        }

        try
        {
            var results = await _db.SearchAsync(query);
            _view = results;
            _articles.Load(results);
            SectionTitle.Text = $"Search: {query}";
            StatusText.Text = $"{results.Count} result{(results.Count == 1 ? "" : "s")}";
            if (results.Count == 0)
            {
                EmptyText.Text = $"No results for “{query}”.";
                EmptyState.Visibility = Visibility.Visible;
            }
            else EmptyState.Visibility = Visibility.Collapsed;
            if (CardToggle.IsChecked == true) DispatcherQueue.TryEnqueue(FitCards);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search error: {ex.Message}";
        }
    }

    // ---- Right-click context menu (cards + list) ----------------------------

    private void OnItemRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        var vm = element?.DataContext as ArticleViewModel;
        if (vm == null) return;

        var menu = BuildItemMenu(vm);
        menu.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = e.GetPosition(element)
        });
    }

    private MenuFlyout BuildItemMenu(ArticleViewModel vm)
    {
        var mf = new MenuFlyout();

        void Add(string text, Symbol icon, Action<ArticleViewModel> action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new SymbolIcon(icon) };
            item.Click += (s, e) => action(vm);
            mf.Items.Add(item);
        }

        void AddOpenInBrowser() => Add("Open in browser", Symbol.Link, async v =>
        {
            try
            {
                var target = ArticleReaderService.EffectiveUrl(v.Article);
                if (PocketReader.Helpers.UrlHelper.IsWebUrl(target))
                    await Windows.System.Launcher.LaunchUriAsync(new Uri(target));
            }
            catch { }
        });

        // Recycle Bin: restore or delete forever.
        if (InRecycleBin)
        {
            Add("Restore", Symbol.Undo, async v =>
            {
                await _db.RestoreManyAsync(new[] { v.Article.Id });
                v.Article.IsDeleted = false;
                _articles.Remove(v);
                await UpdateCountBadgesAsync();
                await UpdateRatingBadgesAsync();
                ShowInfo("Restored.");
            });
            AddOpenInBrowser();
            mf.Items.Add(new MenuFlyoutSeparator());
            Add("Delete permanently", Symbol.Delete, async v =>
            {
                if (!await ConfirmDeleteAsync($"Permanently delete “{v.Title}”?",
                        "This can't be undone.")) return;
                await _db.DeleteManyAsync(new[] { v.Article.Id });
                _articles.Remove(v);
                await UpdateCountBadgesAsync();
                ShowInfo("Deleted permanently.");
            });
            return mf;
        }

        AddOpenInBrowser();
        Add("Download offline", Symbol.Download, async v =>
        {
            StatusText.Text = $"Caching '{v.Title}'...";
            try { await _readerService.FetchAndExtractArticleAsync(v.Article); v.RefreshOffline(); StatusText.Text = "Cached for offline."; }
            catch (Exception ex) { StatusText.Text = $"Cache failed: {ex.Message}"; }
            await UpdateDownloadButtonAsync();
        });
        Add(vm.Article.IsRead ? "Mark as unread" : "Mark as read", Symbol.Read, async v =>
        {
            v.Article.IsRead = !v.Article.IsRead;
            await _db.SetReadAsync(v.Article.Id, v.Article.IsRead);
            if (_currentFilter == "unread" && v.Article.IsRead) _articles.Remove(v);
            await UpdateCountBadgesAsync();
        });
        Add(vm.Article.IsArchived ? "Unarchive" : "Archive", Symbol.Folder, async v =>
        {
            v.Article.IsArchived = !v.Article.IsArchived;
            await _db.SetArchivedAsync(v.Article.Id, v.Article.IsArchived);
            _articles.Remove(v); // leaves the current view either way
            await UpdateCountBadgesAsync();
        });

        mf.Items.Add(new MenuFlyoutSeparator());
        // Soft delete — no confirmation needed since it's reversible from the Recycle Bin.
        Add("Delete", Symbol.Delete, async v =>
        {
            await _db.SoftDeleteManyAsync(new[] { v.Article.Id });
            v.Article.IsDeleted = true;
            _articles.Remove(v);
            await UpdateCountBadgesAsync();
            await UpdateRatingBadgesAsync();
            ShowInfo("Moved to Recycle Bin.");
        });

        return mf;
    }

    private bool InRecycleBin => _currentFilter == "deleted";

    // Confirmation for destructive actions. Default button is Cancel so Enter doesn't delete.
    private async Task<bool> ConfirmDeleteAsync(string title, string body)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.Content.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    // ---- Card view auto-fit (fill the row, no trailing gap) -----------------

    private void OnCardsSizeChanged(object sender, SizeChangedEventArgs e) => FitCards();

    private void FitCards()
    {
        if (ArticlesGridView.ItemsPanelRoot is ItemsWrapGrid wg)
        {
            var avail = ArticlesGridView.ActualWidth
                        - ArticlesGridView.Padding.Left - ArticlesGridView.Padding.Right - 4;
            if (avail <= 0) return;
            double min = _compact ? 212 : 240;
            int cols = Math.Max(1, (int)(avail / min));
            wg.ItemWidth = Math.Floor(avail / cols);
            wg.ItemHeight = _compact ? 208 : 234;
        }
    }

    // ---- Multi-select + batch actions ---------------------------------------

    private void OnSelectToggle(object sender, RoutedEventArgs e) => SetSelectMode(SelectToggle.IsChecked == true);

    private void SetSelectMode(bool on)
    {
        SelectToggle.IsChecked = on;
        var mode = on ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        ArticlesListView.SelectionMode = mode;
        ArticlesGridView.SelectionMode = mode;
        ArticlesListView.IsItemClickEnabled = !on;
        ArticlesGridView.IsItemClickEnabled = !on;
        if (!on)
        {
            ArticlesListView.SelectedItems.Clear();
            ArticlesGridView.SelectedItems.Clear();
        }
        UpdateSelectionBar();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var o in e.AddedItems) if (o is ArticleViewModel v) v.IsSelected = true;
        foreach (var o in e.RemovedItems) if (o is ArticleViewModel v) v.IsSelected = false;
        UpdateSelectionBar();
    }

    private ListViewBase ActiveView =>
        ArticlesGridView.Visibility == Visibility.Visible ? ArticlesGridView : ArticlesListView;

    private IList<object> CurrentSelection() => ActiveView.SelectedItems;
    private List<ArticleViewModel> SelectedVms() => CurrentSelection().OfType<ArticleViewModel>().ToList();
    private List<int> SelectedIds() => SelectedVms().Select(v => v.Article.Id).ToList();

    private void UpdateSelectionBar()
    {
        var count = SelectToggle.IsChecked == true ? CurrentSelection().Count : 0;
        SelectionCount.Text = $"{count} selected";
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClearSelection(object sender, RoutedEventArgs e) => SetSelectMode(false);

    private void OnSelectAllInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (SelectToggle.IsChecked != true) SetSelectMode(true);
        ActiveView.SelectAll();
        UpdateSelectionBar();
    }

    private void OnEscapeMain(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (SelectToggle.IsChecked == true)
        {
            args.Handled = true;
            SetSelectMode(false);
        }
    }

    private async void OnBatchMarkRead(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVms();
        if (vms.Count == 0) return;
        var ids = vms.Select(v => v.Article.Id).ToList();
        await _db.SetReadManyAsync(ids, true);
        foreach (var vm in vms) vm.Article.IsRead = true;
        if (_currentFilter == "unread") foreach (var vm in vms) _articles.Remove(vm);
        SetSelectMode(false);
        await UpdateCountBadgesAsync();
        StatusText.Text = $"Marked {ids.Count} as read.";
    }

    private async void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVms();
        if (vms.Count == 0) return;
        var ids = vms.Select(v => v.Article.Id).ToList();

        if (InRecycleBin)
        {
            if (!await ConfirmDeleteAsync($"Permanently delete {ids.Count} article{(ids.Count == 1 ? "" : "s")}?",
                    "This can't be undone.")) return;
            await _db.DeleteManyAsync(ids);
            foreach (var vm in vms) _articles.Remove(vm);
            SetSelectMode(false);
            await UpdateCountBadgesAsync();
            ShowInfo($"Deleted {ids.Count} permanently.");
        }
        else
        {
            await _db.SoftDeleteManyAsync(ids);
            foreach (var vm in vms) _articles.Remove(vm);
            SetSelectMode(false);
            await UpdateCountBadgesAsync();
            await UpdateRatingBadgesAsync();
            ShowInfo($"Moved {ids.Count} to Recycle Bin.");
        }
    }

    private async void OnBatchRestore(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVms();
        if (vms.Count == 0) return;
        var ids = vms.Select(v => v.Article.Id).ToList();
        await _db.RestoreManyAsync(ids);
        foreach (var vm in vms) _articles.Remove(vm);
        SetSelectMode(false);
        await UpdateCountBadgesAsync();
        await UpdateRatingBadgesAsync();
        ShowInfo($"Restored {ids.Count} article{(ids.Count == 1 ? "" : "s")}.");
    }

    private async void OnBatchArchive(object sender, RoutedEventArgs e)
    {
        var vms = SelectedVms();
        if (vms.Count == 0) return;
        var ids = vms.Select(v => v.Article.Id).ToList();
        await _db.SetArchivedManyAsync(ids, true);
        foreach (var vm in vms) { vm.Article.IsArchived = true; if (_currentFilter != "archive") _articles.Remove(vm); }
        SetSelectMode(false);
        await UpdateCountBadgesAsync();
        StatusText.Text = $"Archived {ids.Count}.";
    }

    private async void OnBatchAddTag(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // Capture selection FIRST (before the flyout/SetSelectMode can disturb it).
        var ids = SelectedIds();
        var tag = (args.QueryText ?? sender.Text)?.Trim();   // fall back to the box text
        if (string.IsNullOrWhiteSpace(tag)) { ShowInfo("Type a tag name first.", InfoBarSeverity.Warning); return; }
        if (ids.Count == 0) { ShowInfo("No articles selected.", InfoBarSeverity.Warning); return; }
        try
        {
            await _db.AddTagToManyAsync(ids, tag);
            sender.Text = "";
            SetSelectMode(false);
            await RefreshAllAsync();
            var msg = $"Tagged {ids.Count} article{(ids.Count == 1 ? "" : "s")} with '{tag}'.";
            StatusText.Text = msg;
            ShowInfo(msg);
        }
        catch (Exception ex) { ShowInfo($"Tagging failed: {ex.Message}", InfoBarSeverity.Error, 7000); }
    }

    private async void OnBatchRemoveTag(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var ids = SelectedIds();
        var tag = (args.QueryText ?? sender.Text)?.Trim();
        if (string.IsNullOrWhiteSpace(tag)) { ShowInfo("Type a tag name first.", InfoBarSeverity.Warning); return; }
        if (ids.Count == 0) { ShowInfo("No articles selected.", InfoBarSeverity.Warning); return; }
        try
        {
            await _db.RemoveTagFromManyAsync(ids, tag);
            sender.Text = "";
            SetSelectMode(false);
            await RefreshAllAsync();
            var msg = $"Removed '{tag}' from {ids.Count} article{(ids.Count == 1 ? "" : "s")}.";
            StatusText.Text = msg;
            ShowInfo(msg);
        }
        catch (Exception ex) { ShowInfo($"Remove-tag failed: {ex.Message}", InfoBarSeverity.Error, 7000); }
    }

    private async void OnArticleClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArticleViewModel vm) await OpenArticle(vm);
    }

    private async Task OpenArticle(ArticleViewModel vm)
    {
        var article = vm.Article;
        if (string.IsNullOrEmpty(article.Content))
        {
            // List items don't carry Content. If it's already cached, load it from the
            // DB (fast, offline); otherwise fetch + extract from the web.
            if (article.ContentCached)
                article.Content = await _db.GetArticleContentAsync(article.Id);

            if (string.IsNullOrEmpty(article.Content))
            {
                StatusText.Text = "Loading article...";
                await _readerService.FetchAndExtractArticleAsync(article);
            }
            StatusText.Text = $"{_view.Count} article{(_view.Count == 1 ? "" : "s")}";
        }

        // Load saved reading position + highlights so the reader can restore them.
        article.ScrollPercent = await _db.GetScrollPercentAsync(article.Id);
        article.Highlights = await _db.GetHighlightsAsync(article.Id);

        // Capture word count for the Statistics page (cheap; idempotent).
        if (!string.IsNullOrEmpty(article.Content))
        {
            var words = _readerService.StripHtmlTags(article.Content)
                .Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            if (words > 0 && words != article.WordCount)
            {
                article.WordCount = words;
                _ = _db.SetWordCountAsync(article.Id, words);
            }
        }

        if (!article.IsRead)
        {
            article.IsRead = true;
            await _db.MarkReadAsync(article.Id);
            // Drop it from the Unread view without a full reload.
            if (_currentFilter == "unread") _articles.Remove(vm);
        }

        // Reuse one warm reader window (no per-open WebView2 cold start).
        if (_reader == null)
        {
            _reader = new ReaderPage(_db, _readerService, _tagging);
            _reader.Closed += (s, ev) => _reader = null;
        }
        _reader.ShowArticle(article);
        _reader.Activate();

        await UpdateCountBadgesAsync();
    }

    // ---- Settings -----------------------------------------------------------

    private Task OpenSettingsAsync()
    {
        // Restore the menu selection FIRST (so the main window settles), then open Settings
        // and force it to the foreground — otherwise it can open behind the main window.
        _suppressNav = true;
        Nav.SelectedItem = _lastSelected ?? NavAll;
        _suppressNav = false;

        var win = new SettingsWindow(this, _db, _raindropService, _settings, OnSettingsChanged);
        win.ShowOwnedDialog(this);
        return Task.CompletedTask;
    }

    private void OnManageTagsClick(object sender, RoutedEventArgs e)
    {
        new TagManagerWindow(this, _db, _settings, OnTagsManaged).ShowOwnedDialog(this);
    }

    private void OnStatsClick(object sender, RoutedEventArgs e)
    {
        new StatsWindow(this, _db, _settings).ShowOwnedDialog(this);
    }

    private async void OnTagsManaged()
    {
        await RefreshAllAsync();
        // Tags changed — retrain the suggester in the background.
        _tagging = new TaggingService();
        _ = EnsureTaggingTrainedAsync();
    }

    private async void OnSettingsChanged()
    {
        // Reflect logout / import in the main UI.
        if (!_raindropService.IsAuthenticated)
        {
            LoginButton.Visibility = Visibility.Visible;
            SyncButton.IsEnabled = false;
            DownloadButton.IsEnabled = false;
        }
        await RefreshAllAsync();
    }
}

public class ArticleViewModel : System.ComponentModel.INotifyPropertyChanged
{
    public Article Article { get; }

    public ArticleViewModel(Article article)
    {
        Article = article;
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    public Visibility SelectionVisibility => _isSelected ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public string Title => Article.Title;
    public string Url => Article.Url;

    private string RealUrl => !string.IsNullOrEmpty(Article.ResolvedUrl)
        ? Article.ResolvedUrl
        : PocketReader.Helpers.UrlHelper.Unwrap(Article.Url);

    public string Domain
    {
        get { try { return new Uri(RealUrl).Host; } catch { return ""; } }
    }

    public string FaviconUrl
    {
        get
        {
            try
            {
                // Cached favicon — unpackaged apps have no ms-appdata, so use a file:// URI.
                if (!string.IsNullOrEmpty(Article.FaviconPath) && File.Exists(Article.FaviconPath))
                    return new Uri(Article.FaviconPath).AbsoluteUri;

                var domain = new Uri(RealUrl).Host;
                return $"https://www.google.com/s2/favicons?domain={domain}&sz=64";
            }
            catch
            {
                return "";
            }
        }
    }

    public string TagsDisplay => string.Join("  •  ", Article.GetTagsList());
    public Visibility HasTags => Article.GetTagsList().Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Return a real ImageSource (or null) — binding a possibly-null string to
    // Image.Source throws ArgumentException during template realization.
    public Microsoft.UI.Xaml.Media.ImageSource CoverImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Article.Cover)) return null;
            try { return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Article.Cover)); }
            catch { return null; }
        }
    }

    public Visibility CoverVisibility => string.IsNullOrWhiteSpace(Article.Cover) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoCoverVisibility => string.IsNullOrWhiteSpace(Article.Cover) ? Visibility.Visible : Visibility.Collapsed;

    // Read/unread styling.
    public double ReadOpacity => Article.IsRead ? 0.5 : 1.0;
    public Visibility UnreadDot => Article.IsRead ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Article.Note) ? Visibility.Collapsed : Visibility.Visible;

    // Star rating shown as five Button glyphs. Filled (FavoriteStarFill) when the star's
    // index is within the rating, otherwise an outline star.
    private const string StarFilled = "";
    private const string StarEmpty = "";
    public string Star1 => Article.Rating >= 1 ? StarFilled : StarEmpty;
    public string Star2 => Article.Rating >= 2 ? StarFilled : StarEmpty;
    public string Star3 => Article.Rating >= 3 ? StarFilled : StarEmpty;
    public string Star4 => Article.Rating >= 4 ? StarFilled : StarEmpty;
    public string Star5 => Article.Rating >= 5 ? StarFilled : StarEmpty;

    public void RaiseRatingChanged()
    {
        OnPropertyChanged(nameof(Star1));
        OnPropertyChanged(nameof(Star2));
        OnPropertyChanged(nameof(Star3));
        OnPropertyChanged(nameof(Star4));
        OnPropertyChanged(nameof(Star5));
    }

    // Friendly relative date ("just now", "2h ago", "Yesterday", "Mar 4").
    public string DateSavedFormatted
    {
        get
        {
            var dt = Article.DateSaved.ToLocalTime();
            var span = DateTime.Now - dt;
            if (span.TotalSeconds < 60) return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays < 2) return "Yesterday";
            if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
            return dt.Year == DateTime.Now.Year ? dt.ToString("MMM d") : dt.ToString("MMM d, yyyy");
        }
    }

    // Source label = the site's registrable host without "www.".
    public string SourceName
    {
        get
        {
            var d = Domain;
            return d.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? d.Substring(4) : d;
        }
    }

    public string OfflineStatus => Article.ContentCached ? "✓ offline" : "";
    public Visibility OfflineVisibility => Article.ContentCached ? Visibility.Visible : Visibility.Collapsed;

    // Called after a Download-offline batch so visible cards flip to the "Offline" badge
    // (and pick up any cover fetched during caching) without rebuilding the whole view.
    public void RefreshOffline()
    {
        OnPropertyChanged(nameof(OfflineStatus));
        OnPropertyChanged(nameof(OfflineVisibility));
        OnPropertyChanged(nameof(CoverImage));
        OnPropertyChanged(nameof(CoverVisibility));
        OnPropertyChanged(nameof(NoCoverVisibility));
    }
}

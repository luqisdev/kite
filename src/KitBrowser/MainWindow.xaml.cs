using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KitBrowser.Core.Models;
using KitBrowser.Core.Services;
using Microsoft.Win32;

namespace KitBrowser;

public partial class MainWindow : Window
{
    private readonly ProfileManager _profileManager;
    private readonly UserProfile _activeProfile;
    private readonly StorageService _storage;
    private readonly AdBlockService _adBlock;
    private readonly FocusModeService _focusMode;
    private readonly SitePermissionService _permissions;
    private readonly FaviconCacheService _faviconCache;
    private readonly TabHostController _host;

    private static readonly string[] GroupColors = ["#4F8CFF", "#8B5CF6", "#10B981", "#F97316", "#EC4899", "#EF4444"];

    private readonly List<TabItemModel> _allTabs = new();
    private readonly ObservableCollection<TabItemModel> _visibleTabs = new();
    private readonly ObservableCollection<BookmarkItem> _bookmarks = new();
    private readonly ObservableCollection<HistoryItem> _history = new();
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private readonly ObservableCollection<NoteItem> _notes = new();
    private readonly ObservableCollection<PasswordEntry> _passwords = new();
    private readonly ObservableCollection<ReadingListItem> _readingList = new();
    private readonly ObservableCollection<string> _customBlocked = new();
    private readonly ObservableCollection<string> _whitelistedSites = new();
    private readonly ObservableCollection<string> _focusBlocked = new();
    private readonly ObservableCollection<DevToolsConsoleEntry> _devConsole = new();
    private readonly ObservableCollection<DevToolsNetworkEntry> _devNetwork = new();
    private readonly ObservableCollection<SpeedDialItem> _speedDial = new();
    private readonly Stack<(string Url, string Title, string? GroupName, string? GroupColorHex)> _closedTabs = new();
    private int _groupCounter = 1;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _sleepTimer;
    private readonly DispatcherTimer _toastTimer;

    private BrowserSettings _settings = new();
    private TabItemModel? _activeTab;
    private string? _openSidePanel;
    private bool _isLoadingSettingsUi;
    private bool _isFullscreen;
    private WindowState _preFullscreenState = WindowState.Normal;
    private Action? _toastAction;
    private Point _dragStartPoint;
    private readonly AppUpdateService _updater = new();

    public MainWindow()
    {
        InitializeComponent();

        _profileManager = new ProfileManager();
        var profileId = ProfileManager.ResolveProfileIdFromArgs(Environment.GetCommandLineArgs());
        var profiles = _profileManager.LoadProfiles();
        _activeProfile = profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)) ?? profiles[0];
        TxtProfileName.Text = _activeProfile.Name;
        Title = _activeProfile.Id == "default" ? "KitBrowser" : $"KitBrowser — {_activeProfile.Name}";

        _storage = StorageService.OpenForProfile(_profileManager.GetDatabasePath(_activeProfile.Id));
        _adBlock = new AdBlockService(enabled: true);
        _focusMode = new FocusModeService();
        _permissions = new SitePermissionService();
        _faviconCache = new FaviconCacheService();
        _host = new TabHostController(WebViewHostGrid, SecondaryWebViewHostGrid, _adBlock, _focusMode, _permissions, _storage, _profileManager.GetWebViewDataPath(_activeProfile.Id));

        HorizontalTabsList.ItemsSource = _visibleTabs;
        PerfTabsList.ItemsSource = _visibleTabs;
        FullBookmarksList.ItemsSource = _bookmarks;
        BookmarksBarList.ItemsSource = _bookmarks;
        FullHistoryList.ItemsSource = _history;
        DownloadsList.ItemsSource = _downloads;
        FullDownloadsList.ItemsSource = _downloads;
        NotesList.ItemsSource = _notes;
        PasswordsList.ItemsSource = _passwords;
        ReadingListView.ItemsSource = _readingList;
        CustomBlockedList.ItemsSource = _customBlocked;
        WhitelistedSitesList.ItemsSource = _whitelistedSites;
        FocusBlockedList.ItemsSource = _focusBlocked;
        DevConsoleList.ItemsSource = _devConsole;
        DevNetworkList.ItemsSource = _devNetwork;
        SpeedDialList.ItemsSource = _speedDial;

        _host.NetworkLogged += entry =>
        {
            _devNetwork.Insert(0, entry);
            if (_devNetwork.Count > 120) _devNetwork.RemoveAt(_devNetwork.Count - 1);
        };
        _host.ConsoleLogged += entry =>
        {
            _devConsole.Add(entry);
            if (_devConsole.Count > 200) _devConsole.RemoveAt(0);
        };
        _host.PageContextMenuRequested += ShowKitPageContextMenu;

        _host.NewTabRequested += url => Dispatcher.BeginInvoke(() => _ = OpenNewTabAsync(url, _activeTab?.IsInPrivate == true));
        _host.DownloadStarted += item => Dispatcher.BeginInvoke(() =>
        {
            _downloads.Insert(0, item);
            OpenSidePanel("Downloads");
            ShowToast($"İndiriliyor: {item.FileName}");
        });
        _host.StatsChanged += () => Dispatcher.BeginInvoke(RefreshStatsUi);
        _host.ActiveTabStateChanged += tab => Dispatcher.BeginInvoke(() =>
        {
            if (tab == _activeTab)
                SyncToolbarToActiveTab();
            if (tab == _host.SplitTab)
                TxtSplitHeader.Text = $"Bölünmüş: {tab.Title}";
        });

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClock();
            TickFocusTimer();
        };
        _clockTimer.Start();
        UpdateClock();

        _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _sleepTimer.Tick += (_, _) => RunAutoSleepSweep();
        _sleepTimer.Start();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBar.Visibility = Visibility.Collapsed;
            _toastAction = null;
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _storage.GetSettingsAsync();
        _adBlock.Enabled = _settings.AdBlockEnabled;
        _adBlock.CosmeticEnabled = _settings.CosmeticAdBlockEnabled;
        _adBlock.LoadWhitelist(_settings.WhitelistedHosts);
        _adBlock.LoadCustomBlocked(_settings.CustomBlockedDomains);
        _focusMode.LoadBlockedHosts(_settings.FocusBlockedHosts);
        _permissions.Load(_settings.SitePermissions);
        RefreshFocusBlockedUi();
        _host.GlobalForceDark = _settings.ForceDarkModeGlobal;

        App.ApplyTheme(_settings.Theme, _settings.AccentColor);
        WindowsThemeHelper.ApplyWindowChromeEffects(this, _settings.Theme == "Dark", _settings.EnableMicaBackdrop);
        ApplyLayoutMode();
        ApplyNewTabBackground();
        SyncSettingsControls();

        _speedDial.Clear();
        foreach (var sd in _settings.SpeedDial)
        {
            _speedDial.Add(sd);
            _ = LoadSpeedDialIconAsync(sd);
        }

        await RefreshBookmarksAsync();
        await RefreshDownloadsAsync();
        await RefreshNotesAsync();

        // Session restore (only real pages; never restore multiple empty kit://newtab tabs)
        bool restoredAny = false;
        if (_settings.RestoreSessionOnStartup)
        {
            var saved = (await _storage.LoadSessionTabsAsync())
                .Where(s => !s.Url.StartsWith("kit://", StringComparison.OrdinalIgnoreCase) &&
                            !s.Url.StartsWith("kite://", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (saved.Count > 0)
            {
                TabItemModel? activeCandidate = null;
                foreach (var s in saved)
                {
                    var tab = new TabItemModel
                    {
                        Url = s.Url,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? s.Url : s.Title,
                        GroupName = s.GroupName,
                        GroupColorHex = s.GroupColorHex,
                        IsPinned = s.IsPinned,
                        SleepState = TabSleepState.Discarded
                    };
                    _allTabs.Add(tab);
                    if (s.IsActive)
                        activeCandidate = tab;
                }

                RebuildVisibleTabs();
                var toActivate = activeCandidate ?? _visibleTabs.FirstOrDefault();
                if (toActivate is not null)
                {
                    await ActivateTabAsync(toActivate);
                    restoredAny = true;
                }
            }
        }

        if (!restoredAny)
            await OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: false);

        AnimateEntrance(RootFrame, fromY: 0, durationMs: 180);
        RefreshStatsUi();

        if (_settings.RequirePinOnStartup && !string.IsNullOrWhiteSpace(_settings.AppLockPinHash))
            LockBrowser();

        if (_settings.AutoCheckUpdates)
            _ = CheckForUpdatesSilentlyAsync();
    }

    // ===================== TABS & OPTIONAL GROUPS =====================

    private void RebuildVisibleTabs()
    {
        var ordered = _allTabs
            .OrderByDescending(t => t.IsPinned)
            .ToList();

        _visibleTabs.Clear();
        foreach (var t in ordered)
            _visibleTabs.Add(t);
    }

    private async Task OpenNewTabAsync(string url, bool isInPrivate = false)
    {
        var initialTitle = url switch
        {
            TabItemModel.SettingsUrl => "Ayarlar",
            TabItemModel.PerformanceUrl => "Performans",
            TabItemModel.HistoryUrl => "Geçmiş",
            TabItemModel.BookmarksUrl => "Yer İmleri",
            TabItemModel.DownloadsUrl => "İndirmeler",
            TabItemModel.PasswordsUrl => "Şifre Kasası",
            TabItemModel.AdBlockUrl => "Reklam Kalkanı",
            TabItemModel.NewTabUrl => isInPrivate ? "Gizli Sekme" : "Yeni Sekme",
            _ => url
        };

        var tab = new TabItemModel
        {
            Url = url,
            Title = initialTitle,
            IsInPrivate = isInPrivate
        };
        _allTabs.Add(tab);
        RebuildVisibleTabs();
        await ActivateTabAsync(tab, forceInternalAnimate: true);

        if (!tab.IsInternalPage)
            await _host.NavigateAsync(tab, url);
    }

    private async Task OpenOrFocusInternalTabAsync(string url)
    {
        var existing = _allTabs.FirstOrDefault(t => string.Equals(t.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            await ActivateTabAsync(existing);
            return;
        }
        await OpenNewTabAsync(url, isInPrivate: false);
    }

    private Task OpenOrFocusSettingsTabAsync() => OpenOrFocusInternalTabAsync(TabItemModel.SettingsUrl);

    private async Task ActivateTabAsync(TabItemModel tab, bool forceInternalAnimate = false)
    {
        if (_activeTab is not null && _activeTab != tab)
        {
            _activeTab.IsSelected = false;
            var previous = _activeTab;
            _ = _host.SoftSuspendTabAsync(previous);
        }

        _activeTab = tab;
        tab.IsSelected = true;
        tab.LastActiveUtc = DateTime.UtcNow;

        bool paletteOpen = CommandPaletteOverlay.Visibility == Visibility.Visible;
        await _host.ShowTabAsync(tab, suppressVisual: paletteOpen);

        UpdateInternalOverlays(tab, forceInternalAnimate);
        InPrivateTopBadge.Visibility = tab.IsInPrivate ? Visibility.Visible : Visibility.Collapsed;
        SyncToolbarToActiveTab();
        RefreshStatsUi();
    }

    private static void AnimateEntrance(UIElement element, double fromY = 10, double fromX = 0, int durationMs = 165)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(durationMs);

        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, dur) { EasingFunction = ease });

        if (element.RenderTransform is TranslateTransform tt)
        {
            if (Math.Abs(fromY) > 0.1)
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, dur) { EasingFunction = ease });
            if (Math.Abs(fromX) > 0.1)
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, dur) { EasingFunction = ease });
        }
    }

    private async void UpdateInternalOverlays(TabItemModel tab, bool forceAnimate = false)
    {
        bool showNewTab = tab.IsNewTab;
        bool showSettings = tab.IsSettingsPage;
        bool showPerf = tab.IsPerformancePage;
        bool showPasswords = tab.IsPasswordsPage;
        bool showAdBlock = tab.IsAdBlockPage;
        bool showLibrary = tab.IsHistoryPage || tab.IsBookmarksPage || tab.IsDownloadsPage;

        bool newTabOpening = showNewTab && (forceAnimate || NewTabOverlay.Visibility != Visibility.Visible);
        bool settingsOpening = showSettings && (forceAnimate || SettingsPageOverlay.Visibility != Visibility.Visible);
        bool perfOpening = showPerf && (forceAnimate || PerformancePageOverlay.Visibility != Visibility.Visible);
        bool passOpening = showPasswords && (forceAnimate || PasswordsPageOverlay.Visibility != Visibility.Visible);
        bool adBlockOpening = showAdBlock && (forceAnimate || AdBlockPageOverlay.Visibility != Visibility.Visible);
        bool libOpening = showLibrary && (forceAnimate || LibraryPageOverlay.Visibility != Visibility.Visible);

        NewTabOverlay.Visibility = showNewTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageOverlay.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        PerformancePageOverlay.Visibility = showPerf ? Visibility.Visible : Visibility.Collapsed;
        PasswordsPageOverlay.Visibility = showPasswords ? Visibility.Visible : Visibility.Collapsed;
        AdBlockPageOverlay.Visibility = showAdBlock ? Visibility.Visible : Visibility.Collapsed;
        LibraryPageOverlay.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;

        if (newTabOpening)
            AnimateEntrance(NewTabContent, fromY: 14, durationMs: 170);

        if (showSettings)
        {
            SyncSettingsControls();
            if (settingsOpening)
                AnimateEntrance(SettingsPageContent, fromY: 14, durationMs: 180);
        }

        if (showPerf)
        {
            RefreshPerformanceDashboard();
            if (perfOpening)
                AnimateEntrance(PerformancePageContent, fromY: 14, durationMs: 180);
        }

        if (showPasswords)
        {
            await RefreshPasswordsAsync();
            if (passOpening)
                AnimateEntrance(PasswordsPageContent, fromY: 14, durationMs: 180);
        }

        if (showAdBlock)
        {
            RefreshAdBlockManagerUi();
            if (adBlockOpening)
                AnimateEntrance(AdBlockPageContent, fromY: 14, durationMs: 180);
        }

        if (showLibrary)
        {
            FullHistoryList.Visibility = tab.IsHistoryPage ? Visibility.Visible : Visibility.Collapsed;
            FullBookmarksList.Visibility = tab.IsBookmarksPage ? Visibility.Visible : Visibility.Collapsed;
            FullDownloadsList.Visibility = tab.IsDownloadsPage ? Visibility.Visible : Visibility.Collapsed;
            TxtLibrarySearch.Visibility = tab.IsHistoryPage ? Visibility.Visible : Visibility.Collapsed;
            BtnLibraryClear.Visibility = (tab.IsHistoryPage || tab.IsDownloadsPage) ? Visibility.Visible : Visibility.Collapsed;
            BtnImportBookmarks.Visibility = tab.IsBookmarksPage ? Visibility.Visible : Visibility.Collapsed;
            BtnExportBookmarks.Visibility = tab.IsBookmarksPage ? Visibility.Visible : Visibility.Collapsed;

            if (tab.IsHistoryPage)
            {
                TxtLibraryTitle.Text = "Geçmiş";
                TxtLibrarySubtitle.Text = "kite://history • Ziyaret ettiğin web siteleri";
                TxtLibraryIcon.Text = "\uE81C";
                await RefreshHistoryAsync(TxtLibrarySearch.Text);
            }
            else if (tab.IsBookmarksPage)
            {
                TxtLibraryTitle.Text = "Yer İmleri";
                TxtLibrarySubtitle.Text = "kite://bookmarks • Kaydettiğin favori siteler";
                TxtLibraryIcon.Text = "\uE734";
                await RefreshBookmarksAsync();
            }
            else if (tab.IsDownloadsPage)
            {
                TxtLibraryTitle.Text = "İndirmeler";
                TxtLibrarySubtitle.Text = "kite://downloads • İndirilen dosyalar";
                TxtLibraryIcon.Text = "\uE896";
                await RefreshDownloadsAsync();
            }

            if (libOpening)
                AnimateEntrance(LibraryPageContent, fromY: 14, durationMs: 180);
        }
    }

    private void RefreshPerformanceDashboard()
    {
        var ramMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        var sleeping = _allTabs.Count(t => t.IsSleeping);
        TxtPerfRam.Text = $"{ramMb:F0} MB";
        TxtPerfWebViews.Text = $"{_host.ActiveWebViewCount} aktif WebView motoru";
        TxtPerfTabs.Text = $"{_allTabs.Count} sekme";
        TxtPerfSleeping.Text = $"{sleeping} uyuyan (0 MB RAM)";
        TxtPerfBlocked.Text = _adBlock.TotalBlocked.ToString("N0");
    }

    private void ApplyNewTabBackground()
    {
        switch (_settings.NewTabBackground)
        {
            case "Aurora":
                NewTabOverlay.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0F172A"),
                    (Color)ColorConverter.ConvertFromString("#1E1B4B"),
                    new Point(0, 0), new Point(1, 1));
                break;
            case "Midnight":
                NewTabOverlay.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#090B10"),
                    (Color)ColorConverter.ConvertFromString("#172554"),
                    new Point(0, 0), new Point(1, 1));
                break;
            case "Sunset":
                NewTabOverlay.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#1F1221"),
                    (Color)ColorConverter.ConvertFromString("#431407"),
                    new Point(0, 0), new Point(1, 1));
                break;
            case "Forest":
                NewTabOverlay.Background = new LinearGradientBrush(
                    (Color)ColorConverter.ConvertFromString("#0B1614"),
                    (Color)ColorConverter.ConvertFromString("#064E3B"),
                    new Point(0, 0), new Point(1, 1));
                break;
            case "Custom" when !string.IsNullOrWhiteSpace(_settings.NewTabWallpaperPath) && File.Exists(_settings.NewTabWallpaperPath):
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(_settings.NewTabWallpaperPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    NewTabOverlay.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill, Opacity = 0.42 };
                }
                catch
                {
                    NewTabOverlay.SetResourceReference(Panel.BackgroundProperty, "BgWindow");
                }
                break;
            default:
                NewTabOverlay.SetResourceReference(Panel.BackgroundProperty, "BgWindow");
                break;
        }
    }

    private async Task NavigateActiveTabAsync(string rawInput)
    {
        if (_activeTab is null) return;
        var resolved = OmniboxParser.ResolveInput(rawInput, _settings.SearchEngine);
        await _host.NavigateAsync(_activeTab, resolved);
        UpdateInternalOverlays(_activeTab);
        SyncToolbarToActiveTab();
    }

    private async void CloseTab(TabItemModel tab)
    {
        if (!tab.IsInPrivate && !tab.IsNewTab)
            _closedTabs.Push((tab.Url, tab.Title, tab.GroupName, tab.GroupColorHex));

        if (_host.SplitTab == tab)
            CloseSplitView();

        var visIdx = _visibleTabs.IndexOf(tab);
        _host.CloseTab(tab);
        _allTabs.Remove(tab);
        RebuildVisibleTabs();

        if (_visibleTabs.Count == 0)
        {
            await OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: false);
            return;
        }

        if (_activeTab == tab)
        {
            var next = _visibleTabs[Math.Clamp(visIdx - 1, 0, _visibleTabs.Count - 1)];
            await ActivateTabAsync(next);
        }
        RefreshStatsUi();
    }

    private async Task ReopenLastClosedTabAsync()
    {
        if (_closedTabs.Count == 0)
        {
            ShowToast("Geri açılacak kapanmış sekme yok.");
            return;
        }

        var (url, title, groupName, groupColor) = _closedTabs.Pop();
        await OpenNewTabAsync(url, isInPrivate: false);
        if (_activeTab is not null)
        {
            _activeTab.Title = title;
            _activeTab.GroupName = groupName;
            _activeTab.GroupColorHex = groupColor;
        }
    }

    // ===================== SPLIT VIEW =====================

    private async Task OpenSplitViewAsync(TabItemModel? secondaryTab = null)
    {
        TabItemModel target;
        if (secondaryTab is not null && secondaryTab != _activeTab)
        {
            target = secondaryTab;
        }
        else
        {
            var existingOther = _visibleTabs.FirstOrDefault(t => t != _activeTab && !t.IsNewTab);
            if (existingOther is not null)
            {
                target = existingOther;
            }
            else
            {
                target = new TabItemModel
                {
                    Url = "https://duckduckgo.com",
                    Title = "DuckDuckGo"
                };
                _allTabs.Add(target);
                RebuildVisibleTabs();
            }
        }

        SplitSplitterColumn.Width = new GridLength(4);
        SplitPaneColumn.Width = new GridLength(1, GridUnitType.Star);
        TxtSplitHeader.Text = $"Bölünmüş: {target.Title}";

        bool paletteOpen = CommandPaletteOverlay.Visibility == Visibility.Visible;
        await _host.SetSplitTabAsync(target, suppressVisual: paletteOpen);
        ShowToast($"Bölünmüş ekran açıldı: {target.Title}");
    }

    private void CloseSplitView()
    {
        _host.ClearSplitTab();
        SplitSplitterColumn.Width = new GridLength(0);
        SplitPaneColumn.Width = new GridLength(0);
    }

    // ===================== SLEEP & PERFORMANCE =====================

    private void RunAutoSleepSweep()
    {
        if (_settings.SleepTimeoutMinutes <= 0) return;
        var threshold = TimeSpan.FromMinutes(_settings.SleepTimeoutMinutes);
        var now = DateTime.UtcNow;

        foreach (var tab in _allTabs)
        {
            if (tab.IsSelected || tab == _host.SplitTab || tab.IsNewTab || tab.IsPinned || tab.SleepState == TabSleepState.Discarded)
                continue;

            if (now - tab.LastActiveUtc >= threshold)
                _host.DiscardTab(tab, markAsDiscarded: true);
        }
    }

    private void SleepAllInactiveTabs()
    {
        int count = 0;
        foreach (var tab in _allTabs)
        {
            if (!tab.IsSelected && tab != _host.SplitTab && !tab.IsNewTab && tab.SleepState != TabSleepState.Discarded)
            {
                _host.DiscardTab(tab, markAsDiscarded: true);
                count++;
            }
        }
        RefreshStatsUi();
        if (count > 0) ShowToast($"{count} arka plan sekmesi uyutuldu (0 MB RAM).");
    }

    // ===================== UI SYNC & TOAST =====================

    private void SyncToolbarToActiveTab()
    {
        if (_activeTab is null) return;

        BtnBack.IsEnabled = _activeTab.CanGoBack;
        BtnForward.IsEnabled = _activeTab.CanGoForward;
        BtnReload.Content = _activeTab.IsLoading ? "\uE711" : "\uE72C";
        LoadingProgressBar.Visibility = _activeTab.IsLoading ? Visibility.Visible : Visibility.Collapsed;

        if (!TxtAddressBar.IsKeyboardFocused)
            TxtAddressBar.Text = _activeTab.IsNewTab ? string.Empty : _activeTab.Url;

        TxtSecurityIcon.Text = _activeTab.IsSettingsPage ? "\uE713"
            : _activeTab.IsPerformancePage ? "\uE945"
            : _activeTab.IsHistoryPage ? "\uE81C"
            : _activeTab.IsBookmarksPage ? "\uE734"
            : _activeTab.IsDownloadsPage ? "\uE896"
            : _activeTab.IsPasswordsPage ? "\uE8D7"
            : _activeTab.IsAdBlockPage ? "\uEA18"
            : (_activeTab.IsInPrivate
                ? "\uE727"
                : (_activeTab.IsNewTab ? "\uE721" : (_activeTab.IsSecure ? "\uE72E" : "\uE7BA")));

        var isBookmarked = !_activeTab.IsNewTab &&
            _bookmarks.Any(b => string.Equals(b.Url, _activeTab.Url, StringComparison.OrdinalIgnoreCase));
        BtnBookmark.Content = isBookmarked ? "\uE735" : "\uE734";

        var host = Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var u) ? u.Host : null;
        var isWhitelisted = _adBlock.IsWhitelisted(host);
        BtnAdBlock.Tag = !_adBlock.Enabled ? "Kapalı" : (isWhitelisted ? "İzinli" : _activeTab.BlockedCount.ToString());
        BtnAdBlock.Opacity = (_adBlock.Enabled && !isWhitelisted) ? 1.0 : 0.48;

        // Zoom indicator
        if (Math.Abs(_activeTab.ZoomFactor - 1.0) > 0.01)
        {
            BtnZoomReset.Content = _activeTab.ZoomDisplay;
            BtnZoomReset.Visibility = Visibility.Visible;
        }
        else
        {
            BtnZoomReset.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshStatsUi()
    {
        var sleepingCount = _allTabs.Count(t => t.IsSleeping);
        var totalBlocked = _adBlock.TotalBlocked;
        var adStatus = _adBlock.Enabled ? "Aktif" : "Kapalı";

        TxtNewTabStats.Text = $"Engellenen Reklam: {totalBlocked}  •  Uyuyan Sekme: {sleepingCount}  •  Reklam Kalkanı: {adStatus}";
        TxtSettingsPerfInfo.Text = $"Saf Native .NET 8 + WebView2 • {_adBlock.RuleCount} kural • {_allTabs.Count} sekme ({sleepingCount} uyuyan)";
        if (_activeTab?.IsPerformancePage == true)
            RefreshPerformanceDashboard();
        SyncToolbarToActiveTab();
    }

    private void ShowToast(string message, string? actionLabel = null, Action? action = null)
    {
        TxtToastMessage.Text = message;
        if (!string.IsNullOrWhiteSpace(actionLabel) && action is not null)
        {
            BtnToastAction.Content = actionLabel;
            BtnToastAction.Visibility = Visibility.Visible;
            _toastAction = action;
        }
        else
        {
            BtnToastAction.Visibility = Visibility.Collapsed;
            _toastAction = null;
        }

        ToastBar.Visibility = Visibility.Visible;
        AnimateEntrance(ToastBar, fromY: 0, durationMs: 140);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TxtNewTabClock.Text = now.ToString("HH:mm");
        var greet = now.Hour switch
        {
            < 6 => "İyi geceler",
            < 12 => "Günaydın",
            < 18 => "İyi günler",
            _ => "İyi akşamlar"
        };
        TxtNewTabGreeting.Text = $"{greet} • KitBrowser ile hızlı ve hafif gezinme";
    }

    private void ApplyLayoutMode()
    {
        if (_isFullscreen) return;

        BookmarksBarRow.Height = _settings.ShowBookmarksBar ? new GridLength(32) : new GridLength(0);
        HorizontalTabBarContainer.Visibility = Visibility.Visible;
    }

    private void ToggleFullscreen()
    {
        _isFullscreen = !_isFullscreen;
        if (_isFullscreen)
        {
            _preFullscreenState = WindowState;
            TopBarRow.Height = new GridLength(0);
            AddressBarRow.Height = new GridLength(0);
            BookmarksBarRow.Height = new GridLength(0);
            SidePanelColumn.Width = new GridLength(0);
            WindowState = WindowState.Maximized;
            ShowToast("Tam ekran modu (Çıkmak için F11)");
        }
        else
        {
            TopBarRow.Height = new GridLength(38);
            AddressBarRow.Height = new GridLength(46);
            WindowState = _preFullscreenState;
            ApplyLayoutMode();
            if (_openSidePanel is not null)
                SidePanelColumn.Width = new GridLength(330);
        }
    }

    private void SyncSettingsControls()
    {
        _isLoadingSettingsUi = true;
        try
        {
            SelectComboByTag(CmbTheme, _settings.Theme);
            SelectComboByTag(CmbNewTabBg, _settings.NewTabBackground);
            SelectComboByTag(CmbSleepTimeout, _settings.SleepTimeoutMinutes.ToString());
            SelectComboByTag(CmbSearchEngine, _settings.SearchEngine);
            ChkAdBlock.IsChecked = _settings.AdBlockEnabled;
            ChkCosmeticAdBlock.IsChecked = _settings.CosmeticAdBlockEnabled;
            ChkForceDarkGlobal.IsChecked = _settings.ForceDarkModeGlobal;
            ChkRestoreSession.IsChecked = _settings.RestoreSessionOnStartup;
            ChkShowBookmarksBar.IsChecked = _settings.ShowBookmarksBar;
            ChkMicaBackdrop.IsChecked = _settings.EnableMicaBackdrop;
            ChkRequirePinOnStartup.IsChecked = _settings.RequirePinOnStartup;
            ChkAutoCheckUpdates.IsChecked = _settings.AutoCheckUpdates;
            TxtUpdateStatus.Text = $"Mevcut sürüm: v{_updater.CurrentVersion}";
            TxtPinStatus.Text = string.IsNullOrWhiteSpace(_settings.AppLockPinHash)
                ? "PIN ayarlanmadı • 4-8 haneli PIN belirleyebilirsin"
                : "PIN kilidi aktif • Ctrl+Shift+L ile anında kilitleyebilirsin";
        }
        finally
        {
            _isLoadingSettingsUi = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tagValue)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tagValue, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    // ===================== SIDE PANELS =====================

    private async void OpenSidePanel(string panelName)
    {
        if (_openSidePanel == panelName)
        {
            CloseSidePanel();
            return;
        }

        _openSidePanel = panelName;
        SidePanelColumn.Width = new GridLength(panelName == "DevTools" ? 440 : (panelName == "WebApp" ? 410 : 330));
        AnimateEntrance(SidePanelContainer, fromY: 0, fromX: 24, durationMs: 170);

        PanelNotes.Visibility = panelName == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        PanelDownloads.Visibility = panelName == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
        PanelFocus.Visibility = panelName == "Focus" ? Visibility.Visible : Visibility.Collapsed;
        PanelReadingList.Visibility = panelName == "ReadingList" ? Visibility.Visible : Visibility.Collapsed;
        PanelWebApp.Visibility = panelName == "WebApp" ? Visibility.Visible : Visibility.Collapsed;
        PanelDevTools.Visibility = panelName == "DevTools" ? Visibility.Visible : Visibility.Collapsed;

        _host.IsDevToolsActive = panelName == "DevTools";
        if (panelName != "WebApp")
            _host.SuspendSideWebApp();

        TxtSidePanelTitle.Text = panelName switch
        {
            "Notes" => "Hızlı Notlar",
            "Downloads" => "İndirmeler",
            "Focus" => "Odak Modu (Pomodoro)",
            "ReadingList" => "Okuma Listesi",
            "WebApp" => "Hızlı Yan Uygulamalar",
            "DevTools" => "KitBrowser DevTools",
            _ => "Panel"
        };

        if (panelName == "Notes") await RefreshNotesAsync();
        else if (panelName == "Downloads") await RefreshDownloadsAsync();
        else if (panelName == "ReadingList") await RefreshReadingListAsync();
        else if (panelName == "Focus") RefreshFocusBlockedUi();
        else if (panelName == "WebApp") await _host.OpenSideWebAppAsync(SideWebAppHostGrid, "https://chatgpt.com");
        else if (panelName == "DevTools" && _activeTab is not null && !_activeTab.IsInternalPage)
            await _host.AttachConsoleHookAsync(_activeTab);
    }

    private void CloseSidePanel()
    {
        _openSidePanel = null;
        _host.IsDevToolsActive = false;
        _host.SuspendSideWebApp();
        SidePanelColumn.Width = new GridLength(0);
    }

    private async Task RefreshNotesAsync()
    {
        var items = await _storage.ListNotesAsync();
        _notes.Clear();
        foreach (var n in items) _notes.Add(n);
    }

    private async Task RefreshBookmarksAsync()
    {
        var items = await _storage.ListBookmarksAsync();
        _bookmarks.Clear();
        foreach (var b in items)
        {
            _bookmarks.Add(b);
            _ = LoadBookmarkIconAsync(b);
        }
        SyncToolbarToActiveTab();
    }

    private async Task LoadBookmarkIconAsync(BookmarkItem item)
    {
        var icon = await _faviconCache.GetFaviconAsync(item.Url);
        if (icon is not null)
            _ = Dispatcher.BeginInvoke(() => item.Icon = icon);
    }

    private async Task RefreshPasswordsAsync()
    {
        var items = await _storage.ListPasswordsAsync();
        _passwords.Clear();
        foreach (var p in items) _passwords.Add(p);
    }

    private async Task RefreshHistoryAsync(string? query)
    {
        var items = await _storage.ListHistoryAsync(query, 100);
        _history.Clear();
        foreach (var h in items) _history.Add(h);
    }

    private async Task RefreshDownloadsAsync()
    {
        var items = await _storage.ListDownloadsAsync();
        _downloads.Clear();
        foreach (var d in items) _downloads.Add(d);
    }

    // ===================== COMMAND PALETTE (Ctrl+K) =====================

    private void OpenCommandPalette()
    {
        _host.SetAllWebViewsVisible(false, _activeTab);
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        AnimateEntrance(CommandPaletteCard, fromY: -12, durationMs: 150);
        TxtCommandSearch.Text = string.Empty;
        PopulateCommandResults(string.Empty);
        TxtCommandSearch.Focus();
    }

    private void CloseCommandPalette()
    {
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
        _host.SetAllWebViewsVisible(true, _activeTab);
    }

    private async void PopulateCommandResults(string query)
    {
        var q = query.Trim();
        var results = new List<CommandPaletteEntry>();

        if (!string.IsNullOrEmpty(q))
        {
            if (InstantCalculatorService.TryEvaluate(q, out var calcVal))
            {
                var copyVal = calcVal;
                results.Add(new CommandPaletteEntry
                {
                    Kind = "Hesap",
                    Title = $"= {calcVal}",
                    Subtitle = "Kopyalamak için Enter'a bas veya tıkla",
                    Execute = () =>
                    {
                        Clipboard.SetText(copyVal);
                        ShowToast($"Sonuç panoya kopyalandı: {copyVal}");
                    }
                });
            }

            var resolved = OmniboxParser.ResolveInput(q, _settings.SearchEngine);
            results.Add(new CommandPaletteEntry
            {
                Kind = "Git / Ara",
                Title = q,
                Subtitle = resolved,
                Execute = () => _ = NavigateActiveTabAsync(q)
            });
        }

        foreach (var tab in _allTabs)
        {
            if (string.IsNullOrEmpty(q) ||
                tab.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                tab.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                var captured = tab;
                results.Add(new CommandPaletteEntry
                {
                    Kind = captured.IsInPrivate ? "Gizli" : (captured.HasGroup ? captured.GroupName! : "Sekme"),
                    Title = captured.Title,
                    Subtitle = captured.IsSleeping ? $"[Uyuyor] {captured.Url}" : captured.Url,
                    Execute = () => _ = ActivateTabAsync(captured)
                });
            }
        }

        var commands = new List<CommandPaletteEntry>
        {
            new() { Kind = "Sayfa", Title = "Ayarlar (kite://settings)", Subtitle = "Ctrl+,", Execute = () => _ = OpenOrFocusSettingsTabAsync() },
            new() { Kind = "Sayfa", Title = "Performans Merkezi (kite://performance)", Subtitle = "Canlı RAM, sekme ve reklam durumu", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.PerformanceUrl) },
            new() { Kind = "Sayfa", Title = "Geçmiş (kite://history)", Subtitle = "Ctrl+H", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.HistoryUrl) },
            new() { Kind = "Sayfa", Title = "Yer İmleri (kite://bookmarks)", Subtitle = "Ctrl+Shift+B", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.BookmarksUrl) },
            new() { Kind = "Sayfa", Title = "İndirmeler (kite://downloads)", Subtitle = "Ctrl+J", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.DownloadsUrl) },
            new() { Kind = "Sayfa", Title = "Şifre Kasası (kite://passwords)", Subtitle = "DPAPI şifreli yerel şifreler", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.PasswordsUrl) },
            new() { Kind = "Sayfa", Title = "Reklam Kalkanı (kite://adblock)", Subtitle = "Özel engelleme kuralları & beyaz liste", Execute = () => _ = OpenOrFocusInternalTabAsync(TabItemModel.AdBlockUrl) },
            new() { Kind = "Komut", Title = "Odak Modu (Pomodoro)", Subtitle = "25 dk odak sayacı + dikkat dağıtıcı engelleyici", Execute = () => OpenSidePanel("Focus") },
            new() { Kind = "Komut", Title = "Hızlı Yan Uygulamalar", Subtitle = "ChatGPT / WhatsApp / Spotify çekmecesi", Execute = () => OpenSidePanel("WebApp") },
            new() { Kind = "Komut", Title = "Okuma Listesi (Sonra Oku)", Subtitle = "Kaydedilen makaleler", Execute = () => OpenSidePanel("ReadingList") },
            new() { Kind = "Komut", Title = "Mobil Görünüm Modu (iPhone)", Subtitle = "Aktif sekmeyi mobil görünümde aç", Execute = ToggleMobileModeForActiveTab },
            new() { Kind = "Komut", Title = "Video Hızı & %200 Ses Yükseltici", Subtitle = "Aktif videonun hızını ve sesini ayarla", Execute = () => MediaBoostPopup.IsOpen = true },
            new() { Kind = "Komut", Title = "Tarayıcıyı Kilitle (PIN)", Subtitle = "Ctrl+Shift+L", Execute = LockBrowser },
            new() { Kind = "Komut", Title = "Çift Sekmeleri Temizle", Subtitle = "Aynı URL'li kopya sekmeleri kapat", Execute = CloseDuplicateTabs },
            new() { Kind = "Komut", Title = "Resim İçinde Resim (PiP)", Subtitle = "Aktif videoyu yüzen pencereye al", Execute = () => _ = TogglePiPForActiveTabAsync() },
            new() { Kind = "Komut", Title = "Yer İmi Çubuğunu Aç / Kapat", Subtitle = "Ctrl+Shift+O", Execute = ToggleBookmarksBar },
            new() { Kind = "Komut", Title = "KitBrowser DevTools (Geliştirici Araçları)", Subtitle = "F12", Execute = () => OpenSidePanel("DevTools") },
            new() { Kind = "Komut", Title = "Okuyucu Modu (Reader Mode)", Subtitle = "Makaleleri sade görünümde oku", Execute = () => _ = ToggleReaderModeForActiveTabAsync() },
            new() { Kind = "Komut", Title = "Sayfayı PDF Olarak Kaydet", Subtitle = "Ctrl+P", Execute = () => _ = SaveActiveTabAsPdfAsync() },
            new() { Kind = "Komut", Title = "Tarama Verilerini Temizle", Subtitle = "Ctrl+Shift+Del", Execute = () => _ = ClearAllBrowsingDataAsync() },
            new() { Kind = "Komut", Title = "Yeni Sekme Aç", Subtitle = "Ctrl+T", Execute = () => _ = OpenNewTabAsync(TabItemModel.NewTabUrl) },
            new() { Kind = "Komut", Title = "Gizli Sekme Aç (InPrivate)", Subtitle = "Ctrl+Shift+N", Execute = () => _ = OpenNewTabAsync(TabItemModel.NewTabUrl, true) },
            new() { Kind = "Komut", Title = "Bölünmüş Ekran Aç / Kapat (Split View)", Subtitle = "İki sekmeyi yan yana kullan", Execute = () => { if (_host.SplitTab is not null) CloseSplitView(); else _ = OpenSplitViewAsync(); } },
            new() { Kind = "Komut", Title = "Ekran Görüntüsü Al (PNG)", Subtitle = "Ctrl+Shift+S", Execute = () => _ = TakeScreenshotAsync() },
            new() { Kind = "Komut", Title = "Kapanan Sekmeyi Geri Aç", Subtitle = "Ctrl+Shift+T", Execute = () => _ = ReopenLastClosedTabAsync() },
            new() { Kind = "Komut", Title = "Tüm Arka Plan Sekmelerini Uyut (0 MB RAM)", Subtitle = "Aktif olmayan sekmelerin belleğini boşaltır", Execute = SleepAllInactiveTabs },
            new() { Kind = "Komut", Title = "Tema Değiştir (Koyu ↔ Açık)", Subtitle = $"Şu an: {_settings.Theme}", Execute = ToggleTheme },
            new() { Kind = "Komut", Title = "Tam Ekran Modu", Subtitle = "F11", Execute = ToggleFullscreen },
            new() { Kind = "Komut", Title = "Reklam & İzleyici Engelleyiciyi Aç / Kapat", Subtitle = $"Şu an: {(_adBlock.Enabled ? "Aktif" : "Kapalı")}", Execute = ToggleAdBlock }
        };

        foreach (var cmd in commands)
        {
            if (string.IsNullOrEmpty(q) ||
                cmd.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                cmd.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                results.Add(cmd);
        }

        foreach (var bm in _bookmarks)
        {
            if (!string.IsNullOrEmpty(q) &&
                (bm.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                 bm.Url.Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                var url = bm.Url;
                results.Add(new CommandPaletteEntry
                {
                    Kind = "Yer İmi",
                    Title = bm.Title,
                    Subtitle = url,
                    Execute = () => _ = NavigateActiveTabAsync(url)
                });
            }
        }

        if (!string.IsNullOrEmpty(q))
        {
            var hist = await _storage.ListHistoryAsync(q, 8);
            foreach (var h in hist)
            {
                var url = h.Url;
                results.Add(new CommandPaletteEntry
                {
                    Kind = "Geçmiş",
                    Title = h.Title,
                    Subtitle = url,
                    Execute = () => _ = NavigateActiveTabAsync(url)
                });
            }
        }

        CommandResultsList.ItemsSource = results;
        if (results.Count > 0)
            CommandResultsList.SelectedIndex = 0;
    }

    private void ExecuteSelectedCommand()
    {
        if (CommandResultsList.SelectedItem is CommandPaletteEntry entry)
        {
            CloseCommandPalette();
            entry.Execute();
        }
    }

    // ===================== TOOLS & ACTIONS =====================

    private async Task TakeScreenshotAsync()
    {
        if (_activeTab is null || _activeTab.IsNewTab)
        {
            ShowToast("Ekran görüntüsü almak için bir web sayfası açın.");
            return;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "KitBrowser");
        var file = Path.Combine(dir, $"KitBrowser_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        var ok = await _host.CaptureScreenshotAsync(_activeTab, file);
        if (ok)
        {
            ShowToast($"Ekran görüntüsü kaydedildi: {Path.GetFileName(file)}", "Klasörde Göster", () =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            });
        }
    }

    private async void ToggleTheme()
    {
        _settings.Theme = _settings.Theme == "Dark" ? "Light" : "Dark";
        App.ApplyTheme(_settings.Theme, _settings.AccentColor);
        WindowsThemeHelper.ApplyWindowChromeEffects(this, _settings.Theme == "Dark", _settings.EnableMicaBackdrop);
        SyncSettingsControls();
        await _storage.SaveSettingsAsync(_settings);
    }

    private async void ToggleAdBlock()
    {
        _adBlock.Enabled = !_adBlock.Enabled;
        _settings.AdBlockEnabled = _adBlock.Enabled;
        SyncToolbarToActiveTab();
        SyncSettingsControls();
        RefreshStatsUi();
        await _storage.SaveSettingsAsync(_settings);
    }

    // ===================== KEYBOARD SHORTCUTS =====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F12)
        {
            OpenSidePanel("DevTools");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (CommandPaletteOverlay.Visibility == Visibility.Visible)
            {
                CloseCommandPalette();
                e.Handled = true;
                return;
            }
            if (FindBar.Visibility == Visibility.Visible)
            {
                FindBar.Visibility = Visibility.Collapsed;
                e.Handled = true;
                return;
            }
            if (_isFullscreen)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
        }

        var mods = Keyboard.Modifiers;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.Key)
            {
                case Key.Tab:
                    CycleTab(-1);
                    e.Handled = true;
                    return;
                case Key.C:
                    CopyCleanActiveUrl();
                    e.Handled = true;
                    return;
                case Key.L:
                    LockBrowser();
                    e.Handled = true;
                    return;
                case Key.T:
                    _ = ReopenLastClosedTabAsync();
                    e.Handled = true;
                    return;
                case Key.N:
                    _ = OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: true);
                    e.Handled = true;
                    return;
                case Key.S:
                    _ = TakeScreenshotAsync();
                    e.Handled = true;
                    return;
                case Key.B:
                    _ = OpenOrFocusInternalTabAsync(TabItemModel.BookmarksUrl);
                    e.Handled = true;
                    return;
                case Key.O:
                    ToggleBookmarksBar();
                    e.Handled = true;
                    return;
                case Key.Delete:
                    _ = ClearAllBrowsingDataAsync();
                    e.Handled = true;
                    return;
            }
        }
        else if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Tab:
                    CycleTab(1);
                    e.Handled = true;
                    break;
                case Key.D1: JumpToTabIndex(0); e.Handled = true; break;
                case Key.D2: JumpToTabIndex(1); e.Handled = true; break;
                case Key.D3: JumpToTabIndex(2); e.Handled = true; break;
                case Key.D4: JumpToTabIndex(3); e.Handled = true; break;
                case Key.D5: JumpToTabIndex(4); e.Handled = true; break;
                case Key.D6: JumpToTabIndex(5); e.Handled = true; break;
                case Key.D7: JumpToTabIndex(6); e.Handled = true; break;
                case Key.D8: JumpToTabIndex(7); e.Handled = true; break;
                case Key.D9: JumpToTabIndex(_visibleTabs.Count - 1); e.Handled = true; break;
                case Key.OemComma:
                    _ = OpenOrFocusSettingsTabAsync();
                    e.Handled = true;
                    break;
                case Key.H:
                    _ = OpenOrFocusInternalTabAsync(TabItemModel.HistoryUrl);
                    e.Handled = true;
                    break;
                case Key.J:
                    _ = OpenOrFocusInternalTabAsync(TabItemModel.DownloadsUrl);
                    e.Handled = true;
                    break;
                case Key.P:
                    _ = SaveActiveTabAsPdfAsync();
                    e.Handled = true;
                    break;
                case Key.U:
                    ViewActiveTabSource();
                    e.Handled = true;
                    break;
                case Key.K:
                    if (CommandPaletteOverlay.Visibility == Visibility.Visible) CloseCommandPalette();
                    else OpenCommandPalette();
                    e.Handled = true;
                    break;
                case Key.T:
                    _ = OpenNewTabAsync(TabItemModel.NewTabUrl);
                    e.Handled = true;
                    break;
                case Key.W:
                    if (_activeTab is not null) CloseTab(_activeTab);
                    e.Handled = true;
                    break;
                case Key.L:
                    TxtAddressBar.Focus();
                    TxtAddressBar.SelectAll();
                    e.Handled = true;
                    break;
                case Key.F:
                    FindBar.Visibility = Visibility.Visible;
                    AnimateEntrance(FindBar, fromY: -12, durationMs: 140);
                    TxtFindQuery.Focus();
                    TxtFindQuery.SelectAll();
                    e.Handled = true;
                    break;
                case Key.R:
                    if (_activeTab is not null) _host.ReloadOrStop(_activeTab);
                    e.Handled = true;
                    break;
                case Key.OemPlus:
                case Key.Add:
                    if (_activeTab is not null) { _host.SetZoom(_activeTab, _activeTab.ZoomFactor + 0.1); SyncToolbarToActiveTab(); }
                    e.Handled = true;
                    break;
                case Key.OemMinus:
                case Key.Subtract:
                    if (_activeTab is not null) { _host.SetZoom(_activeTab, _activeTab.ZoomFactor - 0.1); SyncToolbarToActiveTab(); }
                    e.Handled = true;
                    break;
                case Key.D0:
                case Key.NumPad0:
                    if (_activeTab is not null) { _host.SetZoom(_activeTab, 1.0); SyncToolbarToActiveTab(); }
                    e.Handled = true;
                    break;
            }
        }
        else if (e.Key == Key.F5 && _activeTab is not null)
        {
            _host.ReloadOrStop(_activeTab);
            e.Handled = true;
        }
        else if (mods == ModifierKeys.Alt && _activeTab is not null)
        {
            if (e.SystemKey == Key.Left) { _host.GoBack(_activeTab); e.Handled = true; }
            else if (e.SystemKey == Key.Right) { _host.GoForward(_activeTab); e.Handled = true; }
        }
    }

    // ===================== WINDOW & TAB EVENTS =====================

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        RootFrame.Padding = (WindowState == WindowState.Maximized && !_isFullscreen) ? new Thickness(7) : new Thickness(0);
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _clockTimer.Stop();
        _sleepTimer.Stop();
        _toastTimer.Stop();

        // Persist non-InPrivate real website tabs synchronously before disposing storage
        if (_settings.RestoreSessionOnStartup)
        {
            var sessionTabs = _allTabs
                .Where(t => !t.IsInPrivate && !t.IsInternalPage)
                .Select((t, idx) => new SavedSessionTab
                {
                    SortOrder = idx,
                    Url = t.Url,
                    Title = t.Title,
                    GroupName = t.GroupName,
                    GroupColorHex = t.GroupColorHex,
                    IsPinned = t.IsPinned,
                    IsActive = t == _activeTab
                })
                .ToList();

            _storage.SaveSessionTabsAsync(sessionTabs).GetAwaiter().GetResult();
        }

        _host.Dispose();
        _storage.Dispose();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void BtnCloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnNewTab_Click(object sender, RoutedEventArgs e) => _ = OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: false);
    private void BtnNewInPrivateTab_Click(object sender, RoutedEventArgs e) => _ = OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: true);

    // ===================== OPTIONAL TAB GROUPS =====================

    private void MenuAddToNewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab }) return;

        var color = GroupColors[(_groupCounter - 1) % GroupColors.Length];
        var name = $"Grup {_groupCounter++}";
        tab.GroupName = name;
        tab.GroupColorHex = color;
        ShowToast($"Sekme '{name}' grubuna eklendi.");
    }

    private void MenuRemoveFromGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab } || !tab.HasGroup) return;

        var oldGroup = tab.GroupName;
        tab.GroupName = null;
        tab.GroupColorHex = null;
        ShowToast($"Sekme '{oldGroup}' grubundan çıkarıldı.");
    }

    private void MenuUngroupAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab } || !tab.HasGroup) return;

        var groupName = tab.GroupName;
        foreach (var t in _allTabs.Where(x => string.Equals(x.GroupName, groupName, StringComparison.OrdinalIgnoreCase)))
        {
            t.GroupName = null;
            t.GroupColorHex = null;
        }
        ShowToast($"'{groupName}' grubu çözüldü.");
    }

    private async void MenuCloseGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab } || !tab.HasGroup) return;

        var groupName = tab.GroupName;
        var toClose = _allTabs.Where(x => string.Equals(x.GroupName, groupName, StringComparison.OrdinalIgnoreCase)).ToList();
        bool closedActive = false;

        foreach (var t in toClose)
        {
            if (t == _activeTab) closedActive = true;
            if (!t.IsInPrivate && !t.IsNewTab)
                _closedTabs.Push((t.Url, t.Title, t.GroupName, t.GroupColorHex));
            if (_host.SplitTab == t)
                CloseSplitView();
            _host.CloseTab(t);
            _allTabs.Remove(t);
        }

        RebuildVisibleTabs();
        if (_visibleTabs.Count == 0)
        {
            await OpenNewTabAsync(TabItemModel.NewTabUrl, isInPrivate: false);
        }
        else if (closedActive)
        {
            await ActivateTabAsync(_visibleTabs[0]);
        }

        RefreshStatsUi();
        ShowToast($"'{groupName}' grubu kapatıldı ({toClose.Count} sekme).");
    }

    private void MenuDuplicateTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            _ = OpenNewTabAsync(tab.Url, tab.IsInPrivate);
    }

    private void MenuCycleGroupColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab } || !tab.HasGroup) return;

        var groupName = tab.GroupName;
        var currentIdx = Array.FindIndex(GroupColors, c => string.Equals(c, tab.GroupColorHex, StringComparison.OrdinalIgnoreCase));
        var nextColor = GroupColors[(currentIdx + 1) % GroupColors.Length];
        foreach (var t in _allTabs.Where(x => string.Equals(x.GroupName, groupName, StringComparison.OrdinalIgnoreCase)))
            t.GroupColorHex = nextColor;
    }

    private async void MenuCloseTabsToRight_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TabItemModel tab }) return;

        var idx = _visibleTabs.IndexOf(tab);
        if (idx < 0 || idx >= _visibleTabs.Count - 1) return;

        var toClose = _visibleTabs.Skip(idx + 1).Where(t => !t.IsPinned).ToList();
        if (toClose.Count == 0) return;

        bool closedActive = false;
        foreach (var t in toClose)
        {
            if (t == _activeTab) closedActive = true;
            if (!t.IsInPrivate && !t.IsInternalPage)
                _closedTabs.Push((t.Url, t.Title, t.GroupName, t.GroupColorHex));
            if (_host.SplitTab == t)
                CloseSplitView();
            _host.CloseTab(t);
            _allTabs.Remove(t);
        }

        RebuildVisibleTabs();
        if (closedActive && _visibleTabs.Count > 0)
            await ActivateTabAsync(_visibleTabs[Math.Min(idx, _visibleTabs.Count - 1)]);

        RefreshStatsUi();
        ShowToast($"{toClose.Count} sekme kapatıldı.");
    }

    // File Drag & Drop onto Browser Window
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            foreach (var file in files.Where(File.Exists))
            {
                var fileUri = new Uri(file).AbsoluteUri;
                _ = OpenNewTabAsync(fileUri, isInPrivate: false);
            }
            e.Handled = true;
        }
    }

    private void BtnMainMenu_Click(object sender, RoutedEventArgs e)
    {
        if (BtnMainMenu.ContextMenu is { } menu)
        {
            menu.PlacementTarget = BtnMainMenu;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    // Drag & Drop Tab Reordering
    private void TabItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _dragStartPoint = e.GetPosition(null);

    private void TabItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { Tag: TabItemModel tab })
        {
            e.Handled = true;
            CloseTab(tab);
        }
    }

    private void CycleTab(int delta)
    {
        if (_visibleTabs.Count <= 1 || _activeTab is null) return;
        var idx = _visibleTabs.IndexOf(_activeTab);
        if (idx < 0) return;
        var next = (idx + delta + _visibleTabs.Count) % _visibleTabs.Count;
        _ = ActivateTabAsync(_visibleTabs[next]);
    }

    private void JumpToTabIndex(int index)
    {
        if (index >= 0 && index < _visibleTabs.Count)
            _ = ActivateTabAsync(_visibleTabs[index]);
    }

    private void CopyCleanActiveUrl()
    {
        if (_activeTab is null || _activeTab.IsNewTab) return;
        var clean = UrlCleanerService.StripTrackingParameters(_activeTab.Url);
        Clipboard.SetText(clean);
        ShowToast("Temiz bağlantı panoya kopyalandı.");
    }

    private void TabItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement { Tag: TabItemModel tab })
                DragDrop.DoDragDrop(sender as DependencyObject, tab, DragDropEffects.Move);
        }
    }

    private void TabItem_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TabItemModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void TabItem_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TabItemModel)) is TabItemModel source &&
            sender is FrameworkElement { Tag: TabItemModel target } &&
            source != target)
        {
            var oldIdx = _allTabs.IndexOf(source);
            var newIdx = _allTabs.IndexOf(target);
            if (oldIdx >= 0 && newIdx >= 0)
            {
                _allTabs.RemoveAt(oldIdx);
                _allTabs.Insert(newIdx, source);
                RebuildVisibleTabs();
            }
        }
    }

    private void TabItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            _ = ActivateTabAsync(tab);
    }

    private void BtnCloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            CloseTab(tab);
    }

    private void BtnMuteTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            _host.ToggleMute(tab);
    }

    private void MenuSplitTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            _ = OpenSplitViewAsync(tab);
    }

    private void MenuPinTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab })
        {
            tab.IsPinned = !tab.IsPinned;
            RebuildVisibleTabs();
        }
    }

    private void MenuSleepTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab } && !tab.IsSelected && !tab.IsNewTab)
        {
            _host.DiscardTab(tab, markAsDiscarded: true);
            RefreshStatsUi();
        }
    }

    private void MenuSleepOtherTabs_Click(object sender, RoutedEventArgs e) => SleepAllInactiveTabs();

    private void MenuCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TabItemModel tab })
            CloseTab(tab);
    }

    // ===================== TOOLBAR & NAVIGATION EVENTS =====================

    private void BtnBack_Click(object sender, RoutedEventArgs e) { if (_activeTab is not null) _host.GoBack(_activeTab); }
    private void BtnForward_Click(object sender, RoutedEventArgs e) { if (_activeTab is not null) _host.GoForward(_activeTab); }
    private void BtnReload_Click(object sender, RoutedEventArgs e) { if (_activeTab is not null) _host.ReloadOrStop(_activeTab); }
    private void BtnHome_Click(object sender, RoutedEventArgs e) => _ = NavigateActiveTabAsync(TabItemModel.NewTabUrl);

    private void TxtAddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (OmniboxPopup.IsOpen && OmniboxSuggestionsList.Items.Count > 0)
        {
            if (e.Key == Key.Down)
            {
                OmniboxSuggestionsList.SelectedIndex = Math.Min(OmniboxSuggestionsList.SelectedIndex + 1, OmniboxSuggestionsList.Items.Count - 1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Up)
            {
                OmniboxSuggestionsList.SelectedIndex = Math.Max(OmniboxSuggestionsList.SelectedIndex - 1, -1);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                OmniboxPopup.IsOpen = false;
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            if (OmniboxPopup.IsOpen && OmniboxSuggestionsList.SelectedItem is OmniboxSuggestion sug)
            {
                OmniboxPopup.IsOpen = false;
                if (sug.Kind == "Hesap")
                {
                    Clipboard.SetText(sug.Url);
                    ShowToast($"Sonuç panoya kopyalandı: {sug.Url}");
                }
                else
                {
                    _ = NavigateActiveTabAsync(sug.Url);
                }
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }

            OmniboxPopup.IsOpen = false;
            _ = NavigateActiveTabAsync(TxtAddressBar.Text);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private async void TxtAddressBar_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!TxtAddressBar.IsKeyboardFocused)
        {
            OmniboxPopup.IsOpen = false;
            return;
        }

        var q = TxtAddressBar.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            OmniboxPopup.IsOpen = false;
            return;
        }

        var list = new List<OmniboxSuggestion>();

        // Instant calculator suggestion
        if (InstantCalculatorService.TryEvaluate(q, out var calcVal))
            list.Add(new OmniboxSuggestion { Kind = "Hesap", Title = $"= {calcVal}  (Kopyalamak için tıkla)", Url = calcVal });

        // Internal kite:// pages
        var internals = new (string Title, string Url)[]
        {
            ("Ayarlar", TabItemModel.SettingsUrl),
            ("Performans Merkezi", TabItemModel.PerformanceUrl),
            ("Geçmiş", TabItemModel.HistoryUrl),
            ("Yer İmleri", TabItemModel.BookmarksUrl),
            ("İndirmeler", TabItemModel.DownloadsUrl),
            ("Şifre Kasası", TabItemModel.PasswordsUrl),
            ("Reklam Kalkanı", TabItemModel.AdBlockUrl)
        };
        foreach (var (title, url) in internals)
        {
            if (url.Contains(q, StringComparison.OrdinalIgnoreCase) || title.Contains(q, StringComparison.OrdinalIgnoreCase))
                list.Add(new OmniboxSuggestion { Kind = "Sayfa", Title = title, Url = url });
        }

        // Bookmarks
        foreach (var bm in _bookmarks.Where(b => b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(4))
            list.Add(new OmniboxSuggestion { Kind = "Yer İmi", Title = bm.Title, Url = bm.Url });

        // History
        var hist = await _storage.ListHistoryAsync(q, 5);
        foreach (var h in hist)
        {
            if (!list.Any(x => string.Equals(x.Url, h.Url, StringComparison.OrdinalIgnoreCase)))
                list.Add(new OmniboxSuggestion { Kind = "Geçmiş", Title = h.Title, Url = h.Url });
        }

        OmniboxSuggestionsList.ItemsSource = list;
        OmniboxSuggestionsList.SelectedIndex = -1;
        OmniboxPopup.IsOpen = list.Count > 0 && TxtAddressBar.IsKeyboardFocused;
    }

    private void TxtAddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => TxtAddressBar.SelectAll();

    private void TxtAddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => OmniboxPopup.IsOpen = false;

    private void OmniboxSuggestion_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OmniboxSuggestion sug })
        {
            OmniboxPopup.IsOpen = false;
            if (sug.Kind == "Hesap")
            {
                Clipboard.SetText(sug.Url);
                ShowToast($"Sonuç panoya kopyalandı: {sug.Url}");
            }
            else
            {
                _ = NavigateActiveTabAsync(sug.Url);
            }
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TxtAddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TxtAddressBar.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            TxtAddressBar.Focus();
        }
    }

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
        {
            _host.SetZoom(_activeTab, 1.0);
            SyncToolbarToActiveTab();
        }
    }

    private void BtnAdBlock_Click(object sender, RoutedEventArgs e) => ToggleAdBlock();

    private async void BtnAdBlock_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsNewTab) return;
        if (Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var whitelisted = _adBlock.ToggleWhitelist(uri.Host);
            _settings.WhitelistedHosts = _adBlock.GetWhitelistedHosts();
            await _storage.SaveSettingsAsync(_settings);
            SyncToolbarToActiveTab();
            ShowToast(whitelisted ? $"Bu sitede reklamlara izin verildi: {uri.Host}" : $"Reklam engelleme tekrar aktif: {uri.Host}");
        }
    }

    private async void BtnBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsNewTab) return;
        var exists = _bookmarks.Any(b => string.Equals(b.Url, _activeTab.Url, StringComparison.OrdinalIgnoreCase));
        if (exists) await _storage.RemoveBookmarkAsync(_activeTab.Url);
        else await _storage.AddBookmarkAsync(_activeTab.Title, _activeTab.Url);
        await RefreshBookmarksAsync();
    }

    private void BtnOpenCommandPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void BtnToggleSplitView_Click(object sender, RoutedEventArgs e)
    {
        if (_host.SplitTab is not null) CloseSplitView();
        else _ = OpenSplitViewAsync();
    }

    private void BtnCloseSplitView_Click(object sender, RoutedEventArgs e) => CloseSplitView();
    private void BtnScreenshot_Click(object sender, RoutedEventArgs e) => _ = TakeScreenshotAsync();

    private async void BtnForceDark_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsNewTab) return;
        await _host.ApplyForceDarkModeAsync(_activeTab, !_activeTab.ForceDarkMode);
        SyncToolbarToActiveTab();
        ShowToast(_activeTab.ForceDarkMode ? "Bu sekme için Zorunlu Karanlık Mod açıldı." : "Zorunlu Karanlık Mod kapatıldı.");
    }

    private void BtnPanelNotes_Click(object sender, RoutedEventArgs e) => OpenSidePanel("Notes");
    private void BtnPanelDownloads_Click(object sender, RoutedEventArgs e) => OpenSidePanel("Downloads");
    private void BtnPanelSettings_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusSettingsTabAsync();
    private void BtnOpenPerformancePage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.PerformanceUrl);
    private void BtnOpenHistoryPage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.HistoryUrl);
    private void BtnOpenBookmarksPage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.BookmarksUrl);
    private void BtnOpenDownloadsPage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.DownloadsUrl);
    private void BtnOpenPasswordsPage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.PasswordsUrl);
    private void BtnReaderMode_Click(object sender, RoutedEventArgs e) => _ = ToggleReaderModeForActiveTabAsync();
    private void BtnPictureInPicture_Click(object sender, RoutedEventArgs e) => _ = TogglePiPForActiveTabAsync();
    private void BtnToggleBookmarksBar_Click(object sender, RoutedEventArgs e) => ToggleBookmarksBar();
    private void BtnDevTools_Click(object sender, RoutedEventArgs e) => OpenSidePanel("DevTools");

    // ===================== NATIVE KITBROWSER DEVTOOLS =====================

    private async void BtnDevTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tabName }) return;

        DevTabConsole.Visibility = tabName == "Console" ? Visibility.Visible : Visibility.Collapsed;
        DevTabElements.Visibility = tabName == "Elements" ? Visibility.Visible : Visibility.Collapsed;
        DevTabNetwork.Visibility = tabName == "Network" ? Visibility.Visible : Visibility.Collapsed;
        DevTabStorage.Visibility = tabName == "Storage" ? Visibility.Visible : Visibility.Collapsed;

        if (tabName == "Elements" && _activeTab is not null)
            TxtDevDomOutput.Text = await _host.InspectDomAsync(_activeTab, TxtDevSelector.Text);
        else if (tabName == "Storage" && _activeTab is not null)
            TxtDevStorageOutput.Text = await _host.InspectStorageAsync(_activeTab);
    }

    private async Task RunDevConsoleJsAsync()
    {
        var code = TxtDevJsInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(code) || _activeTab is null) return;

        _devConsole.Add(new DevToolsConsoleEntry { Level = "IN", Message = $"> {code}" });
        TxtDevJsInput.Text = string.Empty;
        var res = await _host.EvaluateJsAsync(_activeTab, code);
        _devConsole.Add(new DevToolsConsoleEntry { Level = "OUT", Message = res });
    }

    private void TxtDevJsInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = RunDevConsoleJsAsync();
    }

    private void BtnDevRunJs_Click(object sender, RoutedEventArgs e) => _ = RunDevConsoleJsAsync();
    private void BtnDevClearConsole_Click(object sender, RoutedEventArgs e) => _devConsole.Clear();
    private void BtnDevClearNetwork_Click(object sender, RoutedEventArgs e) => _devNetwork.Clear();

    private async void BtnDevInspectDom_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
            TxtDevDomOutput.Text = await _host.InspectDomAsync(_activeTab, TxtDevSelector.Text);
    }

    private void TxtDevSelector_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _activeTab is not null)
            _ = Dispatcher.BeginInvoke(async () => TxtDevDomOutput.Text = await _host.InspectDomAsync(_activeTab, TxtDevSelector.Text));
    }

    private async void BtnDevRefreshStorage_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null)
            TxtDevStorageOutput.Text = await _host.InspectStorageAsync(_activeTab);
    }
    private void BtnViewSource_Click(object sender, RoutedEventArgs e) => ViewActiveTabSource();
    private void BtnPrintPdf_Click(object sender, RoutedEventArgs e) => _ = SaveActiveTabAsPdfAsync();

    private async void ToggleBookmarksBar()
    {
        _settings.ShowBookmarksBar = !_settings.ShowBookmarksBar;
        ApplyLayoutMode();
        SyncSettingsControls();
        await _storage.SaveSettingsAsync(_settings);
    }

    private void ViewActiveTabSource()
    {
        if (_activeTab is not null && !_activeTab.IsInternalPage)
            _ = OpenNewTabAsync("view-source:" + _activeTab.Url, _activeTab.IsInPrivate);
    }

    private async Task TogglePiPForActiveTabAsync()
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Resim İçinde Resim için video içeren bir sayfa açın.");
            return;
        }
        var ok = await _host.TogglePictureInPictureAsync(_activeTab);
        ShowToast(ok ? "Video Resim İçinde Resim (PiP) moduna alındı." : "Sayfada aktif video bulunamadı veya PiP kapatıldı.");
    }

    // Multi-Profile Switcher
    private void BtnProfile_Click(object sender, RoutedEventArgs e)
    {
        var profiles = _profileManager.LoadProfiles();
        var menu = new ContextMenu
        {
            PlacementTarget = BtnProfile,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (var prof in profiles)
        {
            var captured = prof;
            var isCurrent = string.Equals(captured.Id, _activeProfile.Id, StringComparison.OrdinalIgnoreCase);
            var mi = new MenuItem
            {
                Header = isCurrent ? $"✓  {captured.Name}" : captured.Name,
                InputGestureText = isCurrent ? "Aktif" : "Yeni Pencere"
            };
            mi.Click += (_, _) => LaunchProfileWindow(captured);
            menu.Items.Add(mi);
        }

        menu.Items.Add(new Separator());
        var createItem = new MenuItem { Header = "+ Yeni İzole Profil Oluştur" };
        createItem.Click += (_, _) =>
        {
            var color = GroupColors[profiles.Count % GroupColors.Length];
            var created = _profileManager.CreateProfile($"Profil {profiles.Count + 1}", color);
            LaunchProfileWindow(created);
        };
        menu.Items.Add(createItem);
        menu.IsOpen = true;
    }

    private void LaunchProfileWindow(UserProfile profile)
    {
        if (string.Equals(profile.Id, _activeProfile.Id, StringComparison.OrdinalIgnoreCase))
            return;

        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            Process.Start(new ProcessStartInfo(exePath, $"--profile={profile.Id}") { UseShellExecute = true });
            ShowToast($"'{profile.Name}' profili yeni izole pencerede açıldı.");
        }
    }

    // Chrome / Edge Bookmark Import & HTML Export
    private async void BtnImportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var autoPath = BookmarkTransferService.FindInstalledChromiumBookmarksFile();
        var dlg = new OpenFileDialog
        {
            Title = "Chrome / Edge Yer İmleri Dosyasını Seç (HTML veya Bookmarks JSON)",
            Filter = "Yer İmi Dosyaları (*.html;*.htm;Bookmarks;*.*)|*.html;*.htm;Bookmarks;*.*"
        };
        if (!string.IsNullOrEmpty(autoPath))
            dlg.InitialDirectory = Path.GetDirectoryName(autoPath);

        if (dlg.ShowDialog() != true) return;

        try
        {
            var imported = BookmarkTransferService.ParseFromFile(dlg.FileName);
            foreach (var (title, url) in imported)
                await _storage.AddBookmarkAsync(title, url);

            await RefreshBookmarksAsync();
            ShowToast($"{imported.Count} yer imi başarıyla içe aktarıldı.");
        }
        catch (Exception ex)
        {
            ShowToast($"İçe aktarma hatası: {ex.Message}");
        }
    }

    private void BtnExportBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Yer İmlerini Dışa Aktar",
            Filter = "HTML Yer İmleri (*.html)|*.html",
            FileName = $"KitBrowser_Bookmarks_{DateTime.Now:yyyyMMdd}.html"
        };
        if (dlg.ShowDialog() == true)
        {
            var html = BookmarkTransferService.ExportToNetscapeHtml(_bookmarks);
            File.WriteAllText(dlg.FileName, html);
            ShowToast($"{_bookmarks.Count} yer imi dışa aktarıldı.");
        }
    }

    // Password Vault (kite://passwords)
    private void BtnGeneratePassword_Click(object sender, RoutedEventArgs e)
        => TxtPassValue.Text = PasswordVaultService.GenerateStrongPassword(18);

    private async void BtnSavePassword_Click(object sender, RoutedEventArgs e)
    {
        var site = TxtPassSite.Text.Trim();
        var user = TxtPassUser.Text.Trim();
        var pass = TxtPassValue.Text;
        if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(pass))
        {
            ShowToast("Site ve şifre alanlarını doldurun.");
            return;
        }

        await _storage.SavePasswordAsync(new PasswordEntry
        {
            SiteUrl = site,
            Username = user,
            PlainPassword = pass
        });
        TxtPassSite.Text = string.Empty;
        TxtPassUser.Text = string.Empty;
        TxtPassValue.Text = string.Empty;
        await RefreshPasswordsAsync();
        ShowToast("Şifre Windows DPAPI ile şifrelenerek kaydedildi.");
    }

    private void BtnToggleRevealPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PasswordEntry entry })
            entry.IsRevealed = !entry.IsRevealed;
    }

    private void BtnCopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PasswordEntry entry })
        {
            Clipboard.SetText(entry.PlainPassword);
            ShowToast($"'{entry.SiteUrl}' şifresi panoya kopyalandı.");
        }
    }

    private async void BtnDeletePassword_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PasswordEntry entry })
        {
            await _storage.DeletePasswordAsync(entry.Id);
            await RefreshPasswordsAsync();
            ShowToast("Şifre kaydı silindi.");
        }
    }

    // ===================== FOCUS MODE (POMODORO) =====================

    private void BtnPanelFocus_Click(object sender, RoutedEventArgs e) => OpenSidePanel("Focus");

    private void TickFocusTimer()
    {
        if (!_focusMode.IsRunning)
        {
            BtnFocusBadge.Visibility = Visibility.Collapsed;
            return;
        }

        bool finished = _focusMode.TickOneSecond();
        var timeText = _focusMode.FormattedTime;
        TxtFocusTimer.Text = timeText;
        TxtFocusBadgeTime.Text = timeText;
        BtnFocusBadge.Visibility = Visibility.Visible;

        if (finished)
        {
            BtnFocusBadge.Visibility = Visibility.Collapsed;
            BtnFocusStartPause.Content = "Başlat (25 dk)";
            TxtFocusStatus.Text = _focusMode.IsBreak ? "Mola Bitti!" : "Odak Oturumu Tamamlandı!";
            ShowToast(_focusMode.IsBreak ? "Mola sona erdi, odaklanmaya hazırsın." : "Harika! 25 dakikalık odak oturumu tamamlandı.");
        }
    }

    private void RefreshFocusBlockedUi()
    {
        _focusBlocked.Clear();
        foreach (var h in _focusMode.GetBlockedHosts()) _focusBlocked.Add(h);
        TxtFocusTimer.Text = _focusMode.FormattedTime;
        BtnFocusStartPause.Content = _focusMode.IsRunning ? "Duraklat" : "Başlat (25 dk)";
        TxtFocusStatus.Text = !_focusMode.IsRunning
            ? "Odaklanmaya Hazır"
            : (_focusMode.IsBreak ? "Mola Modu (Kalkan Pasif)" : "Odak Kalkanı Aktif");
    }

    private void BtnFocusStartPause_Click(object sender, RoutedEventArgs e)
    {
        if (_focusMode.IsRunning)
            _focusMode.Pause();
        else
            _focusMode.StartWork(25);

        RefreshFocusBlockedUi();
        BtnFocusBadge.Visibility = _focusMode.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        TxtFocusBadgeTime.Text = _focusMode.FormattedTime;
    }

    private void BtnFocusBreak_Click(object sender, RoutedEventArgs e)
    {
        _focusMode.StartBreak(5);
        RefreshFocusBlockedUi();
        BtnFocusBadge.Visibility = Visibility.Visible;
        TxtFocusBadgeTime.Text = _focusMode.FormattedTime;
    }

    private void BtnFocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusMode.Reset(25);
        RefreshFocusBlockedUi();
        BtnFocusBadge.Visibility = Visibility.Collapsed;
    }

    private async void BtnAddFocusHost_Click(object sender, RoutedEventArgs e)
    {
        var raw = TxtFocusBlockHost.Text.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;
        _focusMode.AddBlockedHost(raw);
        TxtFocusBlockHost.Text = string.Empty;
        _settings.FocusBlockedHosts = _focusMode.GetBlockedHosts();
        await _storage.SaveSettingsAsync(_settings);
        RefreshFocusBlockedUi();
    }

    private async void BtnRemoveFocusHost_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string host })
        {
            _focusMode.RemoveBlockedHost(host);
            _settings.FocusBlockedHosts = _focusMode.GetBlockedHosts();
            await _storage.SaveSettingsAsync(_settings);
            RefreshFocusBlockedUi();
        }
    }

    // ===================== READING LIST & DUPLICATE TAB CLEANER =====================

    private void BtnPanelReadingList_Click(object sender, RoutedEventArgs e) => OpenSidePanel("ReadingList");

    private async Task RefreshReadingListAsync()
    {
        var items = await _storage.ListReadingItemsAsync();
        _readingList.Clear();
        foreach (var item in items) _readingList.Add(item);
    }

    private async void BtnAddToReadingList_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Sonra Oku listesine eklemek için bir web sayfası açın.");
            return;
        }
        await _storage.AddReadingItemAsync(_activeTab.Title, _activeTab.Url);
        await RefreshReadingListAsync();
        ShowToast($"Sonra Oku'ya eklendi: {_activeTab.Title}");
    }

    private void ReadingItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReadingListItem item })
            _ = NavigateActiveTabAsync(item.Url);
    }

    private async void BtnToggleReadingRead_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ReadingListItem item })
        {
            item.IsRead = !item.IsRead;
            await _storage.ToggleReadingItemReadAsync(item.Id, item.IsRead);
        }
    }

    private async void BtnDeleteReadingItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: ReadingListItem item })
        {
            await _storage.DeleteReadingItemAsync(item.Id);
            await RefreshReadingListAsync();
        }
    }

    private void BtnCloseDuplicateTabs_Click(object sender, RoutedEventArgs e) => CloseDuplicateTabs();

    private void CloseDuplicateTabs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toClose = new List<TabItemModel>();

        // Keep active tab first
        if (_activeTab is not null && !_activeTab.IsInternalPage)
            seen.Add(_activeTab.Url);

        foreach (var tab in _allTabs)
        {
            if (tab == _activeTab || tab.IsInternalPage || tab.IsPinned) continue;
            if (!seen.Add(tab.Url))
                toClose.Add(tab);
        }

        foreach (var dup in toClose)
        {
            if (_host.SplitTab == dup) CloseSplitView();
            _host.CloseTab(dup);
            _allTabs.Remove(dup);
        }

        if (toClose.Count > 0)
        {
            RebuildVisibleTabs();
            RefreshStatsUi();
            ShowToast($"{toClose.Count} adet kopya sekme temizlendi.");
        }
        else
        {
            ShowToast("Kopya sekme bulunamadı.");
        }
    }

    // ===================== MOBILE VIEW, ADBLOCK MANAGER & SIDEBAR WEB APPS =====================

    private void BtnMobileMode_Click(object sender, RoutedEventArgs e) => ToggleMobileModeForActiveTab();

    private void ToggleMobileModeForActiveTab()
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Mobil görünüm için bir web sayfası açın.");
            return;
        }
        var enabled = _host.ToggleMobileMode(_activeTab);
        ShowToast(enabled ? "Mobil Görünüm Modu (iPhone) açıldı." : "Masaüstü görünümüne dönüldü.");
    }

    private void BtnOpenAdBlockPage_Click(object sender, RoutedEventArgs e) => _ = OpenOrFocusInternalTabAsync(TabItemModel.AdBlockUrl);

    private void RefreshAdBlockManagerUi()
    {
        _customBlocked.Clear();
        foreach (var d in _adBlock.GetCustomBlockedHosts()) _customBlocked.Add(d);

        _whitelistedSites.Clear();
        foreach (var w in _adBlock.GetWhitelistedHosts()) _whitelistedSites.Add(w);

        TxtAdBlockPageStats.Text = $"kite://adblock • {_adBlock.RuleCount} aktif kural • {_adBlock.TotalBlocked} engellenen istek";
    }

    private async void BtnAddCustomBlockDomain_Click(object sender, RoutedEventArgs e)
    {
        var domain = TxtCustomBlockDomain.Text.Trim();
        if (string.IsNullOrWhiteSpace(domain)) return;

        _adBlock.AddCustomBlocked(domain);
        TxtCustomBlockDomain.Text = string.Empty;
        _settings.CustomBlockedDomains = _adBlock.GetCustomBlockedHosts();
        await _storage.SaveSettingsAsync(_settings);
        RefreshAdBlockManagerUi();
        ShowToast($"'{domain}' engelleme listesine eklendi.");
    }

    private async void BtnRemoveCustomBlockDomain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string domain })
        {
            _adBlock.RemoveCustomBlocked(domain);
            _settings.CustomBlockedDomains = _adBlock.GetCustomBlockedHosts();
            await _storage.SaveSettingsAsync(_settings);
            RefreshAdBlockManagerUi();
        }
    }

    private async void BtnRemoveWhitelistSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string host })
        {
            _adBlock.ToggleWhitelist(host);
            _settings.WhitelistedHosts = _adBlock.GetWhitelistedHosts();
            await _storage.SaveSettingsAsync(_settings);
            RefreshAdBlockManagerUi();
            SyncToolbarToActiveTab();
        }
    }

    private void BtnPanelWebApp_Click(object sender, RoutedEventArgs e) => OpenSidePanel("WebApp");

    private async void BtnSideWebAppSelect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url })
            await _host.OpenSideWebAppAsync(SideWebAppHostGrid, url);
    }

    // ===================== SITE INFO & PERMISSIONS =====================

    private void BtnSecurityInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Site izinleri yalnızca web siteleri için kullanılabilir.");
            return;
        }

        var host = Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var uri) ? uri.Host : _activeTab.HostDisplay;
        TxtSiteInfoHost.Text = host;
        TxtSiteInfoSecurity.Text = _activeTab.IsSecure ? "Güvenli Bağlantı (HTTPS)" : "Güvensiz Bağlantı (HTTP)";
        TxtSiteInfoSecurity.SetResourceReference(TextBlock.ForegroundProperty, _activeTab.IsSecure ? "SuccessBrush" : "WarningBrush");

        RefreshSitePermissionButtons(host);
        SiteInfoPopup.IsOpen = true;
    }

    private void RefreshSitePermissionButtons(string host)
    {
        BtnPermCamera.Content = SitePermissionService.ToTurkishLabel(_permissions.GetState(host, "camera"));
        BtnPermMic.Content = SitePermissionService.ToTurkishLabel(_permissions.GetState(host, "microphone"));
        BtnPermGeo.Content = SitePermissionService.ToTurkishLabel(_permissions.GetState(host, "geolocation"));
        BtnPermNotif.Content = SitePermissionService.ToTurkishLabel(_permissions.GetState(host, "notifications"));
    }

    private async void BtnCyclePermission_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || sender is not FrameworkElement { Tag: string kind }) return;
        var host = Uri.TryCreate(_activeTab.Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        if (string.IsNullOrEmpty(host)) return;

        var next = _permissions.CycleState(host, kind);
        _settings.SitePermissions = _permissions.Export();
        await _storage.SaveSettingsAsync(_settings);
        RefreshSitePermissionButtons(host);
        ShowToast($"{host} • {kind}: {SitePermissionService.ToTurkishLabel(next)}");
    }

    private async void BtnClearActiveSiteData_Click(object sender, RoutedEventArgs e)
    {
        SiteInfoPopup.IsOpen = false;
        if (_activeTab is null || _activeTab.IsInternalPage) return;
        await _host.ClearSiteDataAsync(_activeTab);
        ShowToast($"{_activeTab.HostDisplay} çerezleri ve site verileri temizlendi.");
    }

    // ===================== VIDEO SPEED & VOLUME BOOSTER =====================

    private void BtnMediaControl_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Video kontrolü için medya içeren bir web sayfası açın.");
            return;
        }
        MediaBoostPopup.IsOpen = true;
    }

    private async void BtnSetSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || sender is not FrameworkElement { Tag: string raw }) return;
        if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            var ok = await _host.SetVideoPlaybackRateAsync(_activeTab, rate);
            ShowToast(ok ? $"Video hızı {rate}x olarak ayarlandı." : "Sayfada aktif video/ses bulunamadı.");
        }
    }

    private async void BtnSetVolumeBoost_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is null || sender is not FrameworkElement { Tag: string raw }) return;
        if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var gain))
        {
            var ok = await _host.SetVolumeBoostAsync(_activeTab, gain);
            ShowToast(ok ? $"Ses seviyesi %{(int)(gain * 100)} olarak ayarlandı." : "Sayfada aktif video/ses bulunamadı.");
        }
    }

    // ===================== BROWSER PIN LOCK (Ctrl+Shift+L) =====================

    private void BtnLockBrowser_Click(object sender, RoutedEventArgs e) => LockBrowser();

    private void LockBrowser()
    {
        if (string.IsNullOrWhiteSpace(_settings.AppLockPinHash))
        {
            _ = OpenOrFocusSettingsTabAsync();
            ShowToast("Önce Ayarlar > Gizlilik kısmından bir PIN kodu belirleyin.");
            return;
        }

        _host.SetAllWebViewsVisible(false, _activeTab);
        _host.SuspendSideWebApp();
        TxtUnlockPin.Password = string.Empty;
        TxtUnlockPinError.Visibility = Visibility.Collapsed;
        PinLockOverlay.Visibility = Visibility.Visible;
        AnimateEntrance(PinLockCard, fromY: 16, durationMs: 160);
        TxtUnlockPin.Focus();
    }

    private void TryUnlockWithPin()
    {
        if (PinLockService.VerifyPin(TxtUnlockPin.Password, _settings.AppLockPinHash))
        {
            PinLockOverlay.Visibility = Visibility.Collapsed;
            TxtUnlockPin.Password = string.Empty;
            TxtUnlockPinError.Visibility = Visibility.Collapsed;
            _host.SetAllWebViewsVisible(true, _activeTab);
        }
        else
        {
            TxtUnlockPinError.Visibility = Visibility.Visible;
            TxtUnlockPin.Password = string.Empty;
            TxtUnlockPin.Focus();
        }
    }

    private void BtnUnlockPin_Click(object sender, RoutedEventArgs e) => TryUnlockWithPin();

    private void TxtUnlockPin_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TryUnlockWithPin();
    }

    private async void BtnSavePin_Click(object sender, RoutedEventArgs e)
    {
        var pin = TxtNewPin.Text.Trim();
        if (pin.Length < 4 || pin.Length > 8)
        {
            ShowToast("PIN kodu 4 ile 8 karakter arasında olmalıdır.");
            return;
        }

        _settings.AppLockPinHash = PinLockService.HashPin(pin);
        TxtNewPin.Text = string.Empty;
        SyncSettingsControls();
        await _storage.SaveSettingsAsync(_settings);
        ShowToast("Tarayıcı PIN kilidi kaydedildi (Ctrl+Shift+L ile kilitleyebilirsin).");
    }

    private async void BtnRemovePin_Click(object sender, RoutedEventArgs e)
    {
        _settings.AppLockPinHash = null;
        _settings.RequirePinOnStartup = false;
        SyncSettingsControls();
        await _storage.SaveSettingsAsync(_settings);
        ShowToast("Tarayıcı PIN kilidi kaldırıldı.");
    }

    // ===================== CUSTOM KITBROWSER PAGE CONTEXT MENU =====================

    private void ShowKitPageContextMenu(PageContextMenuRequest req)
    {
        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint
        };

        MenuItem MakeItem(string header, string iconGlyph, Action onClick, string shortcut = "", bool enabled = true)
        {
            var mi = new MenuItem
            {
                Header = header,
                InputGestureText = shortcut,
                IsEnabled = enabled,
                Icon = new TextBlock
                {
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    Text = iconGlyph,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextSecondary")
                }
            };
            mi.Click += (_, _) => onClick();
            return mi;
        }

        // Link actions
        if (!string.IsNullOrWhiteSpace(req.LinkUri))
        {
            var link = req.LinkUri;
            menu.Items.Add(MakeItem("Bağlantıyı Yeni Sekmede Aç", "\uE8A7", () => _ = OpenNewTabAsync(link, req.Tab.IsInPrivate)));
            menu.Items.Add(MakeItem("Bağlantıyı Gizli Sekmede Aç", "\uE727", () => _ = OpenNewTabAsync(link, isInPrivate: true)));
            menu.Items.Add(MakeItem("Bağlantıyı Temiz Kopyala", "\uE8C8", () =>
            {
                Clipboard.SetText(UrlCleanerService.StripTrackingParameters(link));
                ShowToast("Temiz bağlantı panoya kopyalandı.");
            }));
            menu.Items.Add(MakeItem("Sonra Oku'ya Ekle", "\uE82D", async () =>
            {
                await _storage.AddReadingItemAsync(link, link);
                await RefreshReadingListAsync();
                ShowToast("Bağlantı Sonra Oku listesine eklendi.");
            }));
            menu.Items.Add(new Separator());
        }

        // Selection actions
        if (!string.IsNullOrWhiteSpace(req.SelectionText))
        {
            var sel = req.SelectionText.Trim();
            var preview = sel.Length > 26 ? sel[..26] + "..." : sel;
            menu.Items.Add(MakeItem("Kopyala", "\uE8C8", () => Clipboard.SetText(sel), "Ctrl+C"));
            menu.Items.Add(MakeItem($"Ara: \"{preview}\"", "\uE721", () =>
            {
                var url = OmniboxParser.ResolveInput(sel, _settings.SearchEngine);
                _ = OpenNewTabAsync(url, req.Tab.IsInPrivate);
            }));
            menu.Items.Add(new Separator());
        }

        // Image / Media source actions
        if (!string.IsNullOrWhiteSpace(req.SourceUri) && !string.Equals(req.SourceUri, req.LinkUri, StringComparison.OrdinalIgnoreCase))
        {
            var src = req.SourceUri;
            menu.Items.Add(MakeItem("Medyayı Yeni Sekmede Aç", "\uEB9F", () => _ = OpenNewTabAsync(src, req.Tab.IsInPrivate)));
            menu.Items.Add(MakeItem("Medya Adresini Kopyala", "\uE8C8", () =>
            {
                Clipboard.SetText(src);
                ShowToast("Medya adresi kopyalandı.");
            }));
            menu.Items.Add(new Separator());
        }

        // Editable input actions
        if (req.IsEditable)
        {
            menu.Items.Add(MakeItem("Yapıştır", "\uE77F", async () =>
            {
                if (Clipboard.ContainsText())
                {
                    var textJson = System.Text.Json.JsonSerializer.Serialize(Clipboard.GetText());
                    await _host.EvaluateJsAsync(req.Tab, $"document.execCommand('insertText', false, {textJson})");
                }
            }, "Ctrl+V"));
            menu.Items.Add(new Separator());
        }

        // Standard Navigation
        menu.Items.Add(MakeItem("Geri", "\uE72B", () => _host.GoBack(req.Tab), "Alt+Sol", req.Tab.CanGoBack));
        menu.Items.Add(MakeItem("İleri", "\uE72A", () => _host.GoForward(req.Tab), "Alt+Sağ", req.Tab.CanGoForward));
        menu.Items.Add(MakeItem("Yenile", "\uE72C", () => _host.ReloadOrStop(req.Tab), "F5"));
        menu.Items.Add(new Separator());

        // Page Tools
        menu.Items.Add(MakeItem("Okuyucu Modu", "\uE736", () => _ = ToggleReaderModeForActiveTabAsync()));
        menu.Items.Add(MakeItem("Ekran Görüntüsü Al", "\uE722", () => _ = TakeScreenshotAsync(), "Ctrl+Shift+S"));
        menu.Items.Add(MakeItem("PDF Olarak Kaydet", "\uE749", () => _ = SaveActiveTabAsPdfAsync(), "Ctrl+P"));
        menu.Items.Add(new Separator());

        // KitBrowser Developer
        menu.Items.Add(MakeItem("Sayfa Kaynağını Görüntüle", "\uE943", ViewActiveTabSource, "Ctrl+U"));
        menu.Items.Add(MakeItem("KitBrowser DevTools", "\uE943", () => OpenSidePanel("DevTools"), "F12"));

        menu.IsOpen = true;
    }
    private void BtnClearBrowsingData_Click(object sender, RoutedEventArgs e) => _ = ClearAllBrowsingDataAsync();
    private void BtnCloseSidePanel_Click(object sender, RoutedEventArgs e) => CloseSidePanel();
    private void BtnSleepInactiveTabs_Click(object sender, RoutedEventArgs e) => SleepAllInactiveTabs();
    private void BtnToastAction_Click(object sender, RoutedEventArgs e) => _toastAction?.Invoke();

    private void BtnOptimizeMemory_Click(object sender, RoutedEventArgs e)
    {
        SleepAllInactiveTabs();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        RefreshPerformanceDashboard();
        ShowToast("Bellek optimize edildi ve arka plan sekmeleri uyutuldu.");
    }

    private async Task ToggleReaderModeForActiveTabAsync()
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("Okuyucu Modu için bir web sayfası açın.");
            return;
        }
        var enabled = await _host.ToggleReaderModeAsync(_activeTab);
        ShowToast(enabled ? "Okuyucu Modu açıldı." : "Okuyucu Modu kapatıldı.");
    }

    private async Task SaveActiveTabAsPdfAsync()
    {
        if (_activeTab is null || _activeTab.IsInternalPage)
        {
            ShowToast("PDF kaydetmek için bir web sayfası açın.");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "PDF Dosyası (*.pdf)|*.pdf",
            FileName = $"KitBrowser_{DateTime.Now:yyyyMMdd_HHmm}.pdf"
        };
        if (dlg.ShowDialog() == true)
        {
            var ok = await _host.PrintToPdfAsync(_activeTab, dlg.FileName);
            if (ok)
            {
                ShowToast($"PDF kaydedildi: {Path.GetFileName(dlg.FileName)}", "Klasörde Göster", () =>
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dlg.FileName}\"") { UseShellExecute = true }));
            }
        }
    }

    private async Task ClearAllBrowsingDataAsync()
    {
        await _storage.ClearHistoryAsync();
        await _storage.ClearDownloadsAsync();
        await _host.ClearWebViewBrowsingDataAsync();
        await RefreshHistoryAsync(null);
        await RefreshDownloadsAsync();
        ShowToast("Geçmiş, indirme listesi, çerezler ve WebView önbelleği temizlendi.");
    }

    private void TxtLibrarySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeTab?.IsHistoryPage == true)
            _ = RefreshHistoryAsync(TxtLibrarySearch.Text);
    }

    private async void BtnLibraryClear_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab?.IsHistoryPage == true)
        {
            await _storage.ClearHistoryAsync();
            await RefreshHistoryAsync(null);
            ShowToast("Geçmiş temizlendi.");
        }
        else if (_activeTab?.IsDownloadsPage == true)
        {
            await _storage.ClearDownloadsAsync();
            await RefreshDownloadsAsync();
            ShowToast("İndirme listesi temizlendi.");
        }
    }

    private async void BtnPickWallpaper_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Yeni Sekme Duvar Kağıdı Seç",
            Filter = "Resim Dosyaları (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp"
        };
        if (dlg.ShowDialog() == true)
        {
            _settings.NewTabWallpaperPath = dlg.FileName;
            _settings.NewTabBackground = "Custom";
            SelectComboByTag(CmbNewTabBg, "Custom");
            ApplyNewTabBackground();
            await _storage.SaveSettingsAsync(_settings);
            ShowToast("Özel duvar kağıdı uygulandı.");
        }
    }

    // Find in Page
    private void TxtFindQuery_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _activeTab is not null)
            _ = _host.FindInPageAsync(_activeTab, TxtFindQuery.Text, forward: Keyboard.Modifiers != ModifierKeys.Shift);
    }

    private void BtnFindNext_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) _ = _host.FindInPageAsync(_activeTab, TxtFindQuery.Text, forward: true);
    }

    private void BtnFindPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is not null) _ = _host.FindInPageAsync(_activeTab, TxtFindQuery.Text, forward: false);
    }

    private void BtnCloseFind_Click(object sender, RoutedEventArgs e) => FindBar.Visibility = Visibility.Collapsed;

    // New Tab Page
    private void TxtNewTabSearch_TextChanged(object sender, TextChangedEventArgs e)
        => TxtNewTabPlaceholder.Visibility = string.IsNullOrEmpty(TxtNewTabSearch.Text) ? Visibility.Visible : Visibility.Collapsed;

    private void TxtNewTabSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(TxtNewTabSearch.Text))
        {
            var q = TxtNewTabSearch.Text;
            TxtNewTabSearch.Text = string.Empty;
            _ = NavigateActiveTabAsync(q);
        }
    }

    private void BtnNewTabSearchGo_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TxtNewTabSearch.Text))
        {
            var q = TxtNewTabSearch.Text;
            TxtNewTabSearch.Text = string.Empty;
            _ = NavigateActiveTabAsync(q);
        }
    }

    private void BtnToggleAddSpeedDial_Click(object sender, RoutedEventArgs e)
        => AddSpeedDialPanel.Visibility = AddSpeedDialPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private async void BtnSaveSpeedDial_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtNewSdTitle.Text.Trim();
        var rawUrl = TxtNewSdUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl)) return;

        var resolved = OmniboxParser.ResolveInput(rawUrl, _settings.SearchEngine);
        var item = new SpeedDialItem { Title = string.IsNullOrWhiteSpace(title) ? rawUrl : title, Url = resolved };

        _speedDial.Add(item);
        _ = LoadSpeedDialIconAsync(item);
        _settings.SpeedDial = _speedDial.ToList();
        await _storage.SaveSettingsAsync(_settings);

        TxtNewSdTitle.Text = string.Empty;
        TxtNewSdUrl.Text = string.Empty;
        AddSpeedDialPanel.Visibility = Visibility.Collapsed;
    }

    private async Task LoadSpeedDialIconAsync(SpeedDialItem item)
    {
        var icon = await _faviconCache.GetFaviconAsync(item.Url);
        if (icon is not null)
            _ = Dispatcher.BeginInvoke(() => item.Icon = icon);
    }

    private void SpeedDialCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SpeedDialItem item })
            _ = NavigateActiveTabAsync(item.Url);
    }

    private async void BtnRemoveSpeedDial_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: SpeedDialItem item })
        {
            _speedDial.Remove(item);
            _settings.SpeedDial = _speedDial.ToList();
            await _storage.SaveSettingsAsync(_settings);
        }
    }

    // Command Palette
    private void CommandPaletteOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == CommandPaletteOverlay) CloseCommandPalette();
    }

    private void TxtCommandSearch_TextChanged(object sender, TextChangedEventArgs e) => PopulateCommandResults(TxtCommandSearch.Text);

    private void TxtCommandSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && CommandResultsList.Items.Count > 0)
        {
            CommandResultsList.SelectedIndex = Math.Min(CommandResultsList.SelectedIndex + 1, CommandResultsList.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && CommandResultsList.Items.Count > 0)
        {
            CommandResultsList.SelectedIndex = Math.Max(CommandResultsList.SelectedIndex - 1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ExecuteSelectedCommand();
            e.Handled = true;
        }
    }

    private void CommandResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ExecuteSelectedCommand();

    private void CommandResultItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: CommandPaletteEntry entry })
        {
            CloseCommandPalette();
            entry.Execute();
        }
    }

    // Notes Panel
    private async void BtnSaveNote_Click(object sender, RoutedEventArgs e)
    {
        var title = TxtNoteTitle.Text.Trim();
        var content = TxtNoteContent.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content)) return;

        var note = new NoteItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Hızlı Not" : title,
            Content = content,
            LinkedUrl = (ChkNoteLinkSite.IsChecked == true && _activeTab is not null && !_activeTab.IsNewTab)
                ? _activeTab.Url
                : string.Empty
        };

        await _storage.SaveNoteAsync(note);
        TxtNoteTitle.Text = string.Empty;
        TxtNoteContent.Text = string.Empty;
        await RefreshNotesAsync();
    }

    private void NoteLink_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NoteItem note } && !string.IsNullOrWhiteSpace(note.LinkedUrl))
            _ = NavigateActiveTabAsync(note.LinkedUrl);
    }

    private async void BtnDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: NoteItem note })
        {
            await _storage.DeleteNoteAsync(note.Id);
            await RefreshNotesAsync();
        }
    }

    // Bookmarks / History / Downloads
    private void BookmarkItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BookmarkItem bm }) _ = NavigateActiveTabAsync(bm.Url);
    }

    private async void BtnDeleteBookmark_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: BookmarkItem bm })
        {
            await _storage.RemoveBookmarkAsync(bm.Url);
            await RefreshBookmarksAsync();
        }
    }

    private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: HistoryItem item }) _ = NavigateActiveTabAsync(item.Url);
    }

    private void DownloadItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DownloadItem item } && File.Exists(item.SavePath))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.SavePath}\"") { UseShellExecute = true });
    }

    private async void BtnClearDownloads_Click(object sender, RoutedEventArgs e)
    {
        await _storage.ClearDownloadsAsync();
        await RefreshDownloadsAsync();
    }

    // Settings
    private async void BtnAccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string hex })
        {
            _settings.AccentColor = hex;
            WindowsThemeHelper.ApplyAccentColor(hex);
            await _storage.SaveSettingsAsync(_settings);
        }
    }

    private async void SettingControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettingsUi) return;

        if (CmbTheme.SelectedItem is ComboBoxItem themeItem && themeItem.Tag is string theme)
            _settings.Theme = theme;

        if (CmbNewTabBg.SelectedItem is ComboBoxItem bgItem && bgItem.Tag is string bg)
            _settings.NewTabBackground = bg;

        if (CmbSleepTimeout.SelectedItem is ComboBoxItem sleepItem && int.TryParse(sleepItem.Tag?.ToString(), out var mins))
            _settings.SleepTimeoutMinutes = mins;

        if (CmbSearchEngine.SelectedItem is ComboBoxItem engineItem && engineItem.Tag is string engine)
            _settings.SearchEngine = engine;

        _settings.AdBlockEnabled = ChkAdBlock.IsChecked == true;
        _settings.CosmeticAdBlockEnabled = ChkCosmeticAdBlock.IsChecked == true;
        _settings.ForceDarkModeGlobal = ChkForceDarkGlobal.IsChecked == true;
        _settings.RestoreSessionOnStartup = ChkRestoreSession.IsChecked == true;
        _settings.ShowBookmarksBar = ChkShowBookmarksBar.IsChecked == true;
        _settings.EnableMicaBackdrop = ChkMicaBackdrop.IsChecked == true;
        _settings.RequirePinOnStartup = ChkRequirePinOnStartup.IsChecked == true;
        _settings.AutoCheckUpdates = ChkAutoCheckUpdates.IsChecked == true;

        _adBlock.Enabled = _settings.AdBlockEnabled;
        _adBlock.CosmeticEnabled = _settings.CosmeticAdBlockEnabled;

        App.ApplyTheme(_settings.Theme, _settings.AccentColor);
        WindowsThemeHelper.ApplyWindowChromeEffects(this, _settings.Theme == "Dark", _settings.EnableMicaBackdrop);
        ApplyLayoutMode();
        ApplyNewTabBackground();
        await _host.ApplyGlobalForceDarkAsync(_allTabs, _settings.ForceDarkModeGlobal);
        SyncToolbarToActiveTab();
        RefreshStatsUi();
        await _storage.SaveSettingsAsync(_settings);
    }

    // ===================== VELOPACK DELTA UPDATES =====================

    private async Task CheckForUpdatesSilentlyAsync()
    {
        try
        {
            await Task.Delay(2500);
            var (available, message, targetVersion) = await _updater.CheckForUpdatesAsync(_settings.UpdateFeedUrl);
            TxtUpdateStatus.Text = message;
            if (available)
            {
                TxtUpdateBanner.Text = $"KitBrowser v{targetVersion} güncellemesi mevcut.";
                UpdateBanner.Visibility = Visibility.Visible;
                BtnApplyUpdateSettings.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private void BtnOpenUpdateSettings_Click(object sender, RoutedEventArgs e)
        => _ = OpenOrFocusSettingsTabAsync();

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        TxtUpdateStatus.Text = "Güncellemeler denetleniyor...";
        try
        {
            var (available, message, targetVersion) = await _updater.CheckForUpdatesAsync(_settings.UpdateFeedUrl);
            TxtUpdateStatus.Text = message;
            if (available)
            {
                TxtUpdateBanner.Text = $"KitBrowser v{targetVersion} güncellemesi mevcut.";
                UpdateBanner.Visibility = Visibility.Visible;
                BtnApplyUpdateSettings.Visibility = Visibility.Visible;
            }
            else
            {
                BtnApplyUpdateSettings.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private async void BtnApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnUpdateNowBanner.IsEnabled = false;
        BtnApplyUpdateSettings.IsEnabled = false;
        BtnCheckUpdates.IsEnabled = false;

        var (success, message) = await _updater.DownloadAndApplyAsync(pct =>
            Dispatcher.BeginInvoke(() =>
            {
                var status = $"Delta güncelleme indiriliyor... %{pct}";
                TxtUpdateBanner.Text = status;
                TxtUpdateStatus.Text = status;
            }));

        if (!success)
        {
            TxtUpdateStatus.Text = message;
            TxtUpdateBanner.Text = message;
            BtnUpdateNowBanner.IsEnabled = true;
            BtnApplyUpdateSettings.IsEnabled = true;
            BtnCheckUpdates.IsEnabled = true;
        }
    }

    private void BtnDismissUpdateBanner_Click(object sender, RoutedEventArgs e)
        => UpdateBanner.Visibility = Visibility.Collapsed;
}

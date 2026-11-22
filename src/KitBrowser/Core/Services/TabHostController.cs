using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using KitBrowser.Core.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KitBrowser.Core.Services;

public sealed class TabHostController : IDisposable
{
    private const string ForceDarkEnableScript = """
        (() => {
            if (document.getElementById('kitbrowser-force-dark')) return;
            const style = document.createElement('style');
            style.id = 'kitbrowser-force-dark';
            style.textContent = `
                html { filter: invert(0.92) hue-rotate(180deg) !important; background: #111 !important; }
                img, video, picture, canvas, svg, iframe { filter: invert(1) hue-rotate(180deg) !important; }
            `;
            (document.head || document.documentElement).appendChild(style);
        })();
        """;

    private const string ForceDarkDisableScript = """
        (() => {
            const el = document.getElementById('kitbrowser-force-dark');
            if (el) el.remove();
        })();
        """;

    private const string ReaderModeEnableScript = """
        (() => {
            if (document.getElementById('kitbrowser-reader-overlay')) return true;
            const candidate = document.querySelector('article') || document.querySelector('main') || document.body;
            const title = document.title || 'Okuyucu Modu';
            const paragraphs = Array.from(candidate.querySelectorAll('h1,h2,h3,p,li,blockquote,pre'))
                .filter(el => el.innerText && el.innerText.trim().length > 18)
                .map(el => {
                    const tag = el.tagName.toLowerCase();
                    const text = el.innerText.trim().replace(/</g, '&lt;').replace(/>/g, '&gt;');
                    if (tag === 'h1' || tag === 'h2') return `<h2 style="margin:24px 0 10px;color:#f3f5fa;font-size:22px;">${text}</h2>`;
                    if (tag === 'h3') return `<h3 style="margin:18px 0 8px;color:#dce3f2;font-size:18px;">${text}</h3>`;
                    if (tag === 'blockquote') return `<blockquote style="border-left:3px solid #4F8CFF;padding-left:14px;color:#b8c2d8;margin:14px 0;">${text}</blockquote>`;
                    if (tag === 'pre') return `<pre style="background:#161923;padding:12px;border-radius:8px;overflow:auto;font-size:13px;">${text}</pre>`;
                    return `<p style="margin:0 0 16px;line-height:1.78;">${text}</p>`;
                }).join('');
            const overlay = document.createElement('div');
            overlay.id = 'kitbrowser-reader-overlay';
            overlay.style.cssText = 'position:fixed;inset:0;z-index:2147483647;background:#11131a;color:#e4e8f2;overflow:auto;padding:48px 24px;font-family:Georgia,Segoe UI,serif;font-size:18px;';
            overlay.innerHTML = `<div style="max-width:720px;margin:0 auto;"><h1 style="font-size:30px;margin:0 0 12px;color:#ffffff;font-family:Segoe UI,sans-serif;">${title.replace(/</g,'&lt;')}</h1><div style="font-size:13px;color:#7e8aa3;margin-bottom:28px;font-family:Segoe UI,sans-serif;">KitBrowser Okuyucu Modu</div>${paragraphs || '<p>Bu sayfada okunabilir makale metni bulunamadı.</p>'}</div>`;
            document.documentElement.appendChild(overlay);
            return true;
        })();
        """;

    private const string ReaderModeDisableScript = """
        (() => {
            const el = document.getElementById('kitbrowser-reader-overlay');
            if (el) el.remove();
            return false;
        })();
        """;

    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1";

    private readonly Grid _primaryHostGrid;
    private readonly Grid _secondaryHostGrid;
    private readonly AdBlockService _adBlock;
    private readonly FocusModeService _focusMode;
    private readonly SitePermissionService _permissions;
    private readonly StorageService _storage;
    private readonly string _userDataFolder;
    private readonly Dictionary<string, WebView2> _views = new();
    private readonly SemaphoreSlim _envGate = new(1, 1);
    private CoreWebView2Environment? _sharedEnvironment;
    private WebView2? _sideWebAppView;
    private TabItemModel? _splitTab;
    private bool _globalForceDark;
    private string? _defaultUserAgent;

    public event Action<string>? NewTabRequested;
    public event Action<DownloadItem>? DownloadStarted;
    public event Action? StatsChanged;
    public event Action<TabItemModel>? ActiveTabStateChanged;
    public event Action<DevToolsNetworkEntry>? NetworkLogged;
    public event Action<DevToolsConsoleEntry>? ConsoleLogged;
    public event Action<PageContextMenuRequest>? PageContextMenuRequested;

    public bool IsDevToolsActive { get; set; }

    public TabItemModel? SplitTab => _splitTab;

    public int ActiveWebViewCount => _views.Count;

    public bool GlobalForceDark
    {
        get => _globalForceDark;
        set => _globalForceDark = value;
    }

    public TabHostController(Grid primaryHostGrid, Grid secondaryHostGrid, AdBlockService adBlock, FocusModeService focusMode, SitePermissionService permissions, StorageService storage, string? userDataFolder = null)
    {
        _primaryHostGrid = primaryHostGrid;
        _secondaryHostGrid = secondaryHostGrid;
        _adBlock = adBlock;
        _focusMode = focusMode;
        _permissions = permissions;
        _storage = storage;
        _userDataFolder = userDataFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitBrowser",
            "WebView2Data");
    }

    private async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_sharedEnvironment is not null)
            return _sharedEnvironment;

        await _envGate.WaitAsync();
        try
        {
            if (_sharedEnvironment is not null)
                return _sharedEnvironment;

            Directory.CreateDirectory(_userDataFolder);
            _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            return _sharedEnvironment;
        }
        finally
        {
            _envGate.Release();
        }
    }

    public async Task ShowTabAsync(TabItemModel tab, bool suppressVisual = false)
    {
        if (_splitTab == tab)
            ClearSplitTab();

        foreach (var (id, view) in _views)
        {
            if (id == tab.Id || (_splitTab is not null && id == _splitTab.Id)) continue;
            if (view.Visibility == Visibility.Visible)
            {
                view.Visibility = Visibility.Collapsed;
                _ = SuspendWebViewAsync(view);
            }
        }

        tab.LastActiveUtc = DateTime.UtcNow;

        if (tab.IsInternalPage)
        {
            tab.SleepState = TabSleepState.Active;
            ActiveTabStateChanged?.Invoke(tab);
            return;
        }

        var targetView = await EnsureWebViewAsync(tab, _primaryHostGrid);
        MoveViewToHost(targetView, _primaryHostGrid);
        tab.SleepState = TabSleepState.Active;

        ResumeWebView(targetView);
        targetView.ZoomFactor = tab.ZoomFactor;
        targetView.Visibility = suppressVisual ? Visibility.Collapsed : Visibility.Visible;
        ActiveTabStateChanged?.Invoke(tab);
    }

    public async Task SetSplitTabAsync(TabItemModel tab, bool suppressVisual = false)
    {
        if (_splitTab is not null && _splitTab != tab)
            ClearSplitTab();

        _splitTab = tab;
        tab.LastActiveUtc = DateTime.UtcNow;

        if (tab.IsNewTab)
            return;

        var view = await EnsureWebViewAsync(tab, _secondaryHostGrid);
        MoveViewToHost(view, _secondaryHostGrid);
        tab.SleepState = TabSleepState.Active;
        ResumeWebView(view);
        view.ZoomFactor = tab.ZoomFactor;
        view.Visibility = suppressVisual ? Visibility.Collapsed : Visibility.Visible;
    }

    public void ClearSplitTab()
    {
        if (_splitTab is null) return;
        var prev = _splitTab;
        _splitTab = null;

        if (_views.TryGetValue(prev.Id, out var view))
        {
            MoveViewToHost(view, _primaryHostGrid);
            if (!prev.IsSelected)
            {
                view.Visibility = Visibility.Collapsed;
                _ = SuspendWebViewAsync(view);
            }
        }
    }

    public async Task NavigateAsync(TabItemModel tab, string resolvedUrl)
    {
        tab.Url = resolvedUrl;
        tab.LastActiveUtc = DateTime.UtcNow;
        tab.BlockedCount = 0;

        if (tab.IsInternalPage)
        {
            DiscardTab(tab, markAsDiscarded: false);
            tab.Title = tab.IsSettingsPage ? "Ayarlar"
                : tab.IsPerformancePage ? "Performans"
                : tab.IsHistoryPage ? "Geçmiş"
                : tab.IsBookmarksPage ? "Yer İmleri"
                : tab.IsDownloadsPage ? "İndirmeler"
                : tab.IsPasswordsPage ? "Şifre Kasası"
                : tab.IsAdBlockPage ? "Reklam Kalkanı"
                : (tab.IsInPrivate ? "Gizli Sekme" : "Yeni Sekme");
            tab.Favicon = null;
            tab.CanGoBack = false;
            tab.CanGoForward = false;
            tab.IsLoading = false;
            tab.IsPlayingAudio = false;
            tab.IsReaderMode = false;
            tab.SleepState = TabSleepState.Active;
            ActiveTabStateChanged?.Invoke(tab);
            return;
        }

        var targetHost = _splitTab == tab ? _secondaryHostGrid : _primaryHostGrid;
        var view = await EnsureWebViewAsync(tab, targetHost);
        tab.SleepState = TabSleepState.Active;
        view.Visibility = Visibility.Visible;

        if (view.CoreWebView2 is not null)
            view.CoreWebView2.Navigate(resolvedUrl);
        else
            view.Source = new Uri(resolvedUrl);
    }

    public void SetAllWebViewsVisible(bool visible, TabItemModel? activeTab)
    {
        foreach (var (id, view) in _views)
        {
            bool shouldShow = visible &&
                ((activeTab is not null && !activeTab.IsInternalPage && id == activeTab.Id) ||
                 (_splitTab is not null && !_splitTab.IsInternalPage && id == _splitTab.Id));
            view.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public void GoBack(TabItemModel tab)
    {
        if (_views.TryGetValue(tab.Id, out var view) && view.CoreWebView2?.CanGoBack == true)
            view.CoreWebView2.GoBack();
    }

    public void GoForward(TabItemModel tab)
    {
        if (_views.TryGetValue(tab.Id, out var view) && view.CoreWebView2?.CanGoForward == true)
            view.CoreWebView2.GoForward();
    }

    public void ReloadOrStop(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return;

        if (tab.IsLoading)
            view.CoreWebView2.Stop();
        else
            view.CoreWebView2.Reload();
    }

    public void ToggleMute(TabItemModel tab)
    {
        if (_views.TryGetValue(tab.Id, out var view) && view.CoreWebView2 is { } core)
        {
            core.IsMuted = !core.IsMuted;
            tab.IsMuted = core.IsMuted;
        }
    }

    public void SetZoom(TabItemModel tab, double zoomFactor)
    {
        tab.ZoomFactor = zoomFactor;
        if (_views.TryGetValue(tab.Id, out var view))
            view.ZoomFactor = tab.ZoomFactor;
    }

    public async Task ApplyForceDarkModeAsync(TabItemModel tab, bool enable)
    {
        tab.ForceDarkMode = enable;
        if (_views.TryGetValue(tab.Id, out var view) && view.CoreWebView2 is { } core)
        {
            try
            {
                await core.ExecuteScriptAsync(enable ? ForceDarkEnableScript : ForceDarkDisableScript);
            }
            catch { }
        }
    }

    public async Task ApplyGlobalForceDarkAsync(IEnumerable<TabItemModel> tabs, bool enable)
    {
        _globalForceDark = enable;
        foreach (var tab in tabs)
        {
            if (_views.TryGetValue(tab.Id, out var view) && view.CoreWebView2 is { } core)
            {
                try
                {
                    bool shouldEnable = enable || tab.ForceDarkMode;
                    await core.ExecuteScriptAsync(shouldEnable ? ForceDarkEnableScript : ForceDarkDisableScript);
                }
                catch { }
            }
        }
    }

    public async Task<bool> FindInPageAsync(TabItemModel tab, string query, bool forward = true)
    {
        if (string.IsNullOrEmpty(query) || !_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        try
        {
            var jsonQuery = JsonSerializer.Serialize(query);
            var backwards = forward ? "false" : "true";
            var res = await view.CoreWebView2.ExecuteScriptAsync($"window.find({jsonQuery}, false, {backwards}, true, false, true, false)");
            return string.Equals(res, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CaptureScreenshotAsync(TabItemModel tab, string outputPath)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var fs = File.Create(outputPath);
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        return true;
    }

    public async Task<bool> ToggleReaderModeAsync(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        try
        {
            bool next = !tab.IsReaderMode;
            await view.CoreWebView2.ExecuteScriptAsync(next ? ReaderModeEnableScript : ReaderModeDisableScript);
            tab.IsReaderMode = next;
            return next;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PrintToPdfAsync(TabItemModel tab, string outputPath)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return await view.CoreWebView2.PrintToPdfAsync(outputPath);
    }

    public async Task<bool> ClearWebViewBrowsingDataAsync()
    {
        var anyCore = _views.Values.Select(v => v.CoreWebView2).FirstOrDefault(c => c is not null);
        if (anyCore is null)
            return false;

        try
        {
            await anyCore.Profile.ClearBrowsingDataAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task AttachConsoleHookAsync(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return;

        try
        {
            await view.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    if (window.__kitConsoleHooked) return;
                    window.__kitConsoleHooked = true;
                    ['log', 'warn', 'error'].forEach(lvl => {
                        const orig = console[lvl].bind(console);
                        console[lvl] = (...args) => {
                            try {
                                const msg = args.map(a => typeof a === 'object' ? JSON.stringify(a) : String(a)).join(' ');
                                window.chrome?.webview?.postMessage(JSON.stringify({ __kitConsole: true, level: lvl.toUpperCase(), message: msg }));
                            } catch {}
                            orig(...args);
                        };
                    });
                })();
                """);
        }
        catch { }
    }

    public async Task<string> EvaluateJsAsync(TabItemModel tab, string expression)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return "Aktif bir web sayfası yok.";

        try
        {
            var raw = await view.CoreWebView2.ExecuteScriptAsync(expression);
            return raw ?? "undefined";
        }
        catch (Exception ex)
        {
            return $"Hata: {ex.Message}";
        }
    }

    public async Task<string> InspectDomAsync(TabItemModel tab, string? cssSelector = null)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return "Aktif bir web sayfası yok.";

        try
        {
            string script;
            if (!string.IsNullOrWhiteSpace(cssSelector))
            {
                var selJson = JsonSerializer.Serialize(cssSelector.Trim());
                script = $$"""
                    (() => {
                        try {
                            const els = Array.from(document.querySelectorAll({{selJson}})).slice(0, 40);
                            if (els.length === 0) return 'Eşleşen öğe bulunamadı.';
                            return els.map((e, i) => `[${i+1}] <${e.tagName.toLowerCase()}${e.id ? ' id="'+e.id+'"' : ''}${e.className ? ' class="'+String(e.className).slice(0,40)+'"' : ''}> ${(e.innerText || '').trim().slice(0, 90)}`).join('\n');
                        } catch (err) { return 'Geçersiz CSS seçici: ' + err.message; }
                    })();
                    """;
            }
            else
            {
                script = """
                    (() => {
                        const html = document.documentElement ? document.documentElement.outerHTML : '';
                        return html.slice(0, 12000);
                    })();
                    """;
            }

            var raw = await view.CoreWebView2.ExecuteScriptAsync(script);
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (Exception ex)
        {
            return $"Hata: {ex.Message}";
        }
    }

    public async Task<string> InspectStorageAsync(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return "Aktif bir web sayfası yok.";

        try
        {
            var raw = await view.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    const out = [];
                    out.push('=== localStorage ===');
                    try {
                        if (localStorage.length === 0) out.push('(boş)');
                        for (let i = 0; i < Math.min(localStorage.length, 40); i++) {
                            const k = localStorage.key(i);
                            out.push(`${k} = ${String(localStorage.getItem(k)).slice(0, 120)}`);
                        }
                    } catch { out.push('(erişim kısıtlı)'); }
                    out.push('\n=== sessionStorage ===');
                    try {
                        if (sessionStorage.length === 0) out.push('(boş)');
                        for (let i = 0; i < Math.min(sessionStorage.length, 40); i++) {
                            const k = sessionStorage.key(i);
                            out.push(`${k} = ${String(sessionStorage.getItem(k)).slice(0, 120)}`);
                        }
                    } catch { out.push('(erişim kısıtlı)'); }
                    out.push('\n=== Cookies ===');
                    out.push(document.cookie ? document.cookie.split('; ').join('\n') : '(boş)');
                    return out.join('\n');
                })();
                """);
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (Exception ex)
        {
            return $"Hata: {ex.Message}";
        }
    }

    public async Task<bool> TogglePictureInPictureAsync(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        try
        {
            var res = await view.CoreWebView2.ExecuteScriptAsync("""
                (async () => {
                    if (document.pictureInPictureElement) {
                        await document.exitPictureInPicture();
                        return false;
                    }
                    const vid = Array.from(document.querySelectorAll('video')).find(v => v.readyState >= 2) || document.querySelector('video');
                    if (vid && vid.requestPictureInPicture) {
                        await vid.requestPictureInPicture();
                        return true;
                    }
                    return false;
                })();
                """);
            return string.Equals(res, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public bool ToggleMobileMode(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is not { } core)
            return false;

        _defaultUserAgent ??= core.Settings.UserAgent;
        tab.IsMobileMode = !tab.IsMobileMode;
        core.Settings.UserAgent = tab.IsMobileMode ? MobileUserAgent : _defaultUserAgent;
        core.Reload();
        return tab.IsMobileMode;
    }

    public async Task OpenSideWebAppAsync(Grid sideHostGrid, string url)
    {
        if (_sideWebAppView is null)
        {
            _sideWebAppView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 23, 32)
            };
            sideHostGrid.Children.Add(_sideWebAppView);
            var env = await GetEnvironmentAsync();
            await _sideWebAppView.EnsureCoreWebView2Async(env);
            _sideWebAppView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        }

        _sideWebAppView.Visibility = Visibility.Visible;
        ResumeWebView(_sideWebAppView);
        if (_sideWebAppView.CoreWebView2 is { } core &&
            !string.Equals(core.Source, url, StringComparison.OrdinalIgnoreCase))
        {
            core.Navigate(url);
        }
    }

    public void SuspendSideWebApp()
    {
        if (_sideWebAppView is not null)
        {
            _sideWebAppView.Visibility = Visibility.Collapsed;
            _ = SuspendWebViewAsync(_sideWebAppView);
        }
    }

    public async Task<bool> SetVideoPlaybackRateAsync(TabItemModel tab, double rate)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        try
        {
            var r = Math.Clamp(rate, 0.25, 4.0).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var res = await view.CoreWebView2.ExecuteScriptAsync($$"""
                (() => {
                    const media = Array.from(document.querySelectorAll('video, audio'));
                    media.forEach(m => { m.playbackRate = {{r}}; });
                    return media.length;
                })();
                """);
            return int.TryParse(res, out var count) && count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetVolumeBoostAsync(TabItemModel tab, double gainFactor)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is null)
            return false;

        try
        {
            var g = Math.Clamp(gainFactor, 0.0, 2.5).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var res = await view.CoreWebView2.ExecuteScriptAsync($$"""
                (() => {
                    const media = Array.from(document.querySelectorAll('video, audio'));
                    if (media.length === 0) return 0;
                    window.__kitAudioCtx = window.__kitAudioCtx || new (window.AudioContext || window.webkitAudioContext)();
                    if (window.__kitAudioCtx.state === 'suspended') window.__kitAudioCtx.resume();
                    media.forEach(m => {
                        if (!m.__kitGainNode) {
                            try {
                                const src = window.__kitAudioCtx.createMediaElementSource(m);
                                const gain = window.__kitAudioCtx.createGain();
                                src.connect(gain);
                                gain.connect(window.__kitAudioCtx.destination);
                                m.__kitGainNode = gain;
                            } catch {}
                        }
                        if (m.__kitGainNode) m.__kitGainNode.gain.value = {{g}};
                    });
                    return media.length;
                })();
                """);
            return int.TryParse(res, out var count) && count > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task ClearSiteDataAsync(TabItemModel tab)
    {
        if (!_views.TryGetValue(tab.Id, out var view) || view.CoreWebView2 is not { } core)
            return;

        try
        {
            await core.ExecuteScriptAsync("try { localStorage.clear(); sessionStorage.clear(); } catch {}");
            if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri))
            {
                var cookies = await core.CookieManager.GetCookiesAsync(uri.GetLeftPart(UriPartial.Authority));
                foreach (var c in cookies)
                    core.CookieManager.DeleteCookie(c);
            }
            core.Reload();
        }
        catch { }
    }

    public async Task SoftSuspendTabAsync(TabItemModel tab)
    {
        if (tab.IsSelected || tab == _splitTab || tab.IsNewTab || tab.SleepState == TabSleepState.Discarded)
            return;

        if (_views.TryGetValue(tab.Id, out var view))
        {
            view.Visibility = Visibility.Collapsed;
            var suspended = await SuspendWebViewAsync(view);
            if (suspended && !tab.IsSelected && tab != _splitTab)
            {
                tab.SleepState = TabSleepState.Suspended;
                StatsChanged?.Invoke();
            }
        }
    }

    public void DiscardTab(TabItemModel tab, bool markAsDiscarded = true)
    {
        if (_splitTab == tab)
            _splitTab = null;

        if (_views.Remove(tab.Id, out var view))
        {
            RemoveViewFromParent(view);
            try { view.Dispose(); } catch { }
        }

        if (markAsDiscarded && !tab.IsNewTab)
        {
            tab.IsLoading = false;
            tab.IsPlayingAudio = false;
            tab.SleepState = TabSleepState.Discarded;
            StatsChanged?.Invoke();
        }
    }

    public void CloseTab(TabItemModel tab)
    {
        DiscardTab(tab, markAsDiscarded: false);
    }

    private async Task<WebView2> EnsureWebViewAsync(TabItemModel tab, Grid hostGrid)
    {
        if (_views.TryGetValue(tab.Id, out var existing))
            return existing;

        var view = new WebView2
        {
            Visibility = Visibility.Collapsed,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 20, 26),
            ZoomFactor = tab.ZoomFactor
        };

        _views[tab.Id] = view;
        hostGrid.Children.Add(view);

        var env = await GetEnvironmentAsync();
        if (tab.IsInPrivate)
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(env, options);
        }
        else
        {
            await view.EnsureCoreWebView2Async(env);
        }

        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = true;

        // Per-site Permissions (Camera, Microphone, Geolocation, Notifications)
        core.PermissionRequested += (_, e) =>
        {
            var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var pu) ? pu.Host : string.Empty;
            var kind = e.PermissionKind switch
            {
                CoreWebView2PermissionKind.Camera => "camera",
                CoreWebView2PermissionKind.Microphone => "microphone",
                CoreWebView2PermissionKind.Geolocation => "geolocation",
                CoreWebView2PermissionKind.Notifications => "notifications",
                _ => string.Empty
            };

            if (!string.IsNullOrEmpty(kind) && !string.IsNullOrEmpty(host))
            {
                var state = _permissions.GetState(host, kind);
                if (state == SitePermissionState.Allow) e.State = CoreWebView2PermissionState.Allow;
                else if (state == SitePermissionState.Deny) e.State = CoreWebView2PermissionState.Deny;
            }
        };

        // Native network-level Ad & Tracker Blocker
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            string? pageHost = null;
            if (Uri.TryCreate(tab.Url, UriKind.Absolute, out var currentUri))
                pageHost = currentUri.Host;

            if (_adBlock.ShouldBlock(e.Request.Uri, pageHost))
            {
                e.Response = env.CreateWebResourceResponse(null, 403, "Blocked", "");
                _adBlock.IncrementBlocked();
                _primaryHostGrid.Dispatcher.BeginInvoke(() =>
                {
                    tab.BlockedCount++;
                    StatsChanged?.Invoke();
                    if (IsDevToolsActive && tab.IsSelected)
                        NetworkLogged?.Invoke(new DevToolsNetworkEntry { Status = "ENGEL", Url = e.Request.Uri, IsBlocked = true });
                });
            }
            else if (IsDevToolsActive && tab.IsSelected)
            {
                var reqUrl = e.Request.Uri;
                _primaryHostGrid.Dispatcher.BeginInvoke(() =>
                    NetworkLogged?.Invoke(new DevToolsNetworkEntry { Status = "OK", Url = reqUrl, IsBlocked = false }));
            }
        };

        core.WebMessageReceived += (_, e) =>
        {
            if (!IsDevToolsActive) return;
            try
            {
                var json = e.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(json) || !json.Contains("__kitConsole")) return;
                using var doc = JsonDocument.Parse(json);
                var level = doc.RootElement.TryGetProperty("level", out var lv) ? lv.GetString() ?? "LOG" : "LOG";
                var msg = doc.RootElement.TryGetProperty("message", out var mg) ? mg.GetString() ?? "" : "";
                _primaryHostGrid.Dispatcher.BeginInvoke(() =>
                    ConsoleLogged?.Invoke(new DevToolsConsoleEntry { Level = level, Message = msg }));
            }
            catch { }
        };

        // Custom KitBrowser native context menu (replaces default Edge menu completely)
        core.ContextMenuRequested += (_, e) =>
        {
            e.Handled = true;
            var t = e.ContextMenuTarget;
            var req = new PageContextMenuRequest
            {
                Tab = tab,
                LinkUri = t.HasLinkUri ? t.LinkUri : null,
                SelectionText = t.HasSelection ? t.SelectionText : null,
                SourceUri = t.HasSourceUri ? t.SourceUri : null,
                IsEditable = t.IsEditable
            };
            _primaryHostGrid.Dispatcher.BeginInvoke(() => PageContextMenuRequested?.Invoke(req));
        };

        // Favicon loader
        core.FaviconChanged += async (_, _) =>
        {
            try
            {
                using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream is null || stream.Length == 0) return;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();

                _ = _primaryHostGrid.Dispatcher.BeginInvoke(() => tab.Favicon = bmp);
            }
            catch
            {
                // Keep fallback letter badge
            }
        };

        // Audio / Mute indicators
        core.IsDocumentPlayingAudioChanged += (_, _) =>
            _primaryHostGrid.Dispatcher.BeginInvoke(() => tab.IsPlayingAudio = core.IsDocumentPlayingAudio);

        core.IsMutedChanged += (_, _) =>
            _primaryHostGrid.Dispatcher.BeginInvoke(() => tab.IsMuted = core.IsMuted);

        core.NavigationStarting += (_, e) =>
        {
            if (_focusMode.IsUrlBlocked(e.Uri))
            {
                e.Cancel = true;
                var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var bu) ? bu.Host : e.Uri;
                core.NavigateToString(_focusMode.BuildBlockedHtml(host));
                tab.IsLoading = false;
                return;
            }

            tab.IsLoading = true;
            if (!string.IsNullOrWhiteSpace(e.Uri))
                tab.Url = e.Uri;
            tab.BlockedCount = 0;
            ActiveTabStateChanged?.Invoke(tab);
        };

        core.SourceChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.Source))
                tab.Url = core.Source;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            ActiveTabStateChanged?.Invoke(tab);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                tab.Title = core.DocumentTitle;
        };

        core.DOMContentLoaded += async (_, _) =>
        {
            string? pageHost = Uri.TryCreate(tab.Url, UriKind.Absolute, out var u) ? u.Host : null;
            if (_adBlock.Enabled && _adBlock.CosmeticEnabled && !_adBlock.IsWhitelisted(pageHost))
            {
                try { await core.ExecuteScriptAsync(AdBlockService.CosmeticAdHideScript); } catch { }
            }
            if (_globalForceDark || tab.ForceDarkMode)
            {
                try { await core.ExecuteScriptAsync(ForceDarkEnableScript); } catch { }
            }
        };

        core.NavigationCompleted += (_, _) =>
        {
            tab.IsLoading = false;
            tab.CanGoBack = core.CanGoBack;
            tab.CanGoForward = core.CanGoForward;
            if (!string.IsNullOrWhiteSpace(core.DocumentTitle))
                tab.Title = core.DocumentTitle;
            if (!string.IsNullOrWhiteSpace(core.Source))
                tab.Url = core.Source;

            // Never record history for InPrivate tabs
            if (!tab.IsInPrivate)
            {
                var titleSnapshot = tab.Title;
                var urlSnapshot = tab.Url;
                _ = Task.Run(() => _storage.RecordHistoryAsync(titleSnapshot, urlSnapshot));
            }
            ActiveTabStateChanged?.Invoke(tab);
        };

        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(e.Uri))
                NewTabRequested?.Invoke(e.Uri);
        };

        core.DownloadStarting += (_, e) =>
        {
            var op = e.DownloadOperation;
            var savePath = e.ResultFilePath;
            var item = new DownloadItem
            {
                FileName = Path.GetFileName(savePath),
                Url = op.Uri,
                SavePath = savePath,
                TotalBytes = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : 0,
                Status = "İndiriliyor"
            };

            DownloadStarted?.Invoke(item);
            if (!tab.IsInPrivate)
                _ = Task.Run(() => _storage.UpsertDownloadAsync(item));

            op.BytesReceivedChanged += (_, _) =>
            {
                _primaryHostGrid.Dispatcher.BeginInvoke(() =>
                {
                    item.BytesReceived = op.BytesReceived;
                    if (op.TotalBytesToReceive.HasValue)
                        item.TotalBytes = (long)op.TotalBytesToReceive.Value;
                });
            };

            op.StateChanged += (_, _) =>
            {
                var status = op.State switch
                {
                    CoreWebView2DownloadState.Completed => "Tamamlandı",
                    CoreWebView2DownloadState.Interrupted => "İptal Edildi",
                    _ => "İndiriliyor"
                };

                _primaryHostGrid.Dispatcher.BeginInvoke(() =>
                {
                    item.Status = status;
                    if (op.TotalBytesToReceive.HasValue)
                        item.TotalBytes = (long)op.TotalBytesToReceive.Value;
                });
                if (!tab.IsInPrivate)
                    _ = Task.Run(() => _storage.UpsertDownloadAsync(item));
            };
        };

        if (!tab.IsNewTab && Uri.TryCreate(tab.Url, UriKind.Absolute, out _))
            core.Navigate(tab.Url);

        return view;
    }

    private static void MoveViewToHost(WebView2 view, Grid targetHost)
    {
        if (view.Parent == targetHost) return;
        RemoveViewFromParent(view);
        targetHost.Children.Add(view);
    }

    private static void RemoveViewFromParent(WebView2 view)
    {
        if (view.Parent is Panel parent)
            parent.Children.Remove(view);
    }

    private static void ResumeWebView(WebView2 view)
    {
        if (view.CoreWebView2 is { } core)
        {
            try
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                core.Resume();
            }
            catch { }
        }
    }

    private static async Task<bool> SuspendWebViewAsync(WebView2 view)
    {
        try
        {
            if (view.CoreWebView2 is { } core)
            {
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                return await core.TrySuspendAsync();
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        foreach (var view in _views.Values)
        {
            try { view.Dispose(); } catch { }
        }
        _views.Clear();
        try { _sideWebAppView?.Dispose(); } catch { }
        _sideWebAppView = null;
        _envGate.Dispose();
    }
}

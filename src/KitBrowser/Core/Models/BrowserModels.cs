using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace KitBrowser.Core.Models;

public enum TabSleepState
{
    Active,
    Suspended,
    Discarded
}

public sealed class TabItemModel : INotifyPropertyChanged
{
    public const string NewTabUrl = "kit://newtab";
    public const string SettingsUrl = "kite://settings";
    public const string PerformanceUrl = "kite://performance";
    public const string HistoryUrl = "kite://history";
    public const string BookmarksUrl = "kite://bookmarks";
    public const string DownloadsUrl = "kite://downloads";
    public const string PasswordsUrl = "kite://passwords";
    public const string AdBlockUrl = "kite://adblock";

    private string _title = "Yeni Sekme";
    private string _url = NewTabUrl;
    private string? _groupName;
    private string? _groupColorHex;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isSelected;
    private bool _isPinned;
    private bool _isInPrivate;
    private bool _isPlayingAudio;
    private bool _isMuted;
    private bool _forceDarkMode;
    private bool _isReaderMode;
    private bool _isMobileMode;
    private double _zoomFactor = 1.0;
    private ImageSource? _favicon;
    private TabSleepState _sleepState = TabSleepState.Active;
    private int _blockedCount;
    private DateTime _lastActiveUtc = DateTime.UtcNow;

    public string Id { get; } = Guid.NewGuid().ToString("N");

    public string? GroupName
    {
        get => _groupName;
        set
        {
            if (SetField(ref _groupName, string.IsNullOrWhiteSpace(value) ? null : value.Trim()))
                OnPropertyChanged(nameof(HasGroup));
        }
    }

    public string? GroupColorHex
    {
        get => _groupColorHex;
        set
        {
            if (SetField(ref _groupColorHex, value))
                OnPropertyChanged(nameof(GroupBrush));
        }
    }

    public bool HasGroup => !string.IsNullOrWhiteSpace(_groupName);

    public Brush GroupBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_groupColorHex))
                return Brushes.Transparent;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(_groupColorHex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch
            {
                return Brushes.CornflowerBlue;
            }
        }
    }

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value))
                OnPropertyChanged(nameof(BadgeLetter));
        }
    }

    public string Url
    {
        get => _url;
        set
        {
            if (SetField(ref _url, value))
            {
                OnPropertyChanged(nameof(IsNewTab));
                OnPropertyChanged(nameof(IsSettingsPage));
                OnPropertyChanged(nameof(IsPerformancePage));
                OnPropertyChanged(nameof(IsHistoryPage));
                OnPropertyChanged(nameof(IsBookmarksPage));
                OnPropertyChanged(nameof(IsDownloadsPage));
                OnPropertyChanged(nameof(IsPasswordsPage));
                OnPropertyChanged(nameof(IsAdBlockPage));
                OnPropertyChanged(nameof(IsInternalPage));
                OnPropertyChanged(nameof(IsSecure));
                OnPropertyChanged(nameof(HostDisplay));
                OnPropertyChanged(nameof(BadgeLetter));
            }
        }
    }

    public bool IsNewTab => string.Equals(_url, NewTabUrl, StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsPage =>
        string.Equals(_url, SettingsUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://settings", StringComparison.OrdinalIgnoreCase);

    public bool IsPerformancePage =>
        string.Equals(_url, PerformanceUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://performance", StringComparison.OrdinalIgnoreCase);

    public bool IsHistoryPage =>
        string.Equals(_url, HistoryUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://history", StringComparison.OrdinalIgnoreCase);

    public bool IsBookmarksPage =>
        string.Equals(_url, BookmarksUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://bookmarks", StringComparison.OrdinalIgnoreCase);

    public bool IsDownloadsPage =>
        string.Equals(_url, DownloadsUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://downloads", StringComparison.OrdinalIgnoreCase);

    public bool IsPasswordsPage =>
        string.Equals(_url, PasswordsUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://passwords", StringComparison.OrdinalIgnoreCase);

    public bool IsAdBlockPage =>
        string.Equals(_url, AdBlockUrl, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_url, "kit://adblock", StringComparison.OrdinalIgnoreCase);

    public bool IsInternalPage =>
        IsNewTab || IsSettingsPage || IsPerformancePage || IsHistoryPage || IsBookmarksPage || IsDownloadsPage || IsPasswordsPage || IsAdBlockPage;

    public bool IsSecure => _url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public string HostDisplay
    {
        get
        {
            if (IsSettingsPage) return "Ayarlar";
            if (IsPerformancePage) return "Performans";
            if (IsHistoryPage) return "Geçmiş";
            if (IsBookmarksPage) return "Yer İmleri";
            if (IsDownloadsPage) return "İndirmeler";
            if (IsPasswordsPage) return "Şifre Kasası";
            if (IsAdBlockPage) return "Reklam Kalkanı";
            if (IsNewTab) return IsInPrivate ? "Gizli Sekme" : "KitBrowser";
            return Uri.TryCreate(_url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : _url;
        }
    }

    public string BadgeLetter
    {
        get
        {
            if (IsSettingsPage) return "⚙";
            if (IsPerformancePage) return "⚡";
            if (IsHistoryPage) return "↺";
            if (IsBookmarksPage) return "★";
            if (IsDownloadsPage) return "↓";
            if (IsPasswordsPage) return "🔑";
            if (IsAdBlockPage) return "🛡";
            if (IsInPrivate && IsNewTab) return "G";
            if (IsNewTab) return "K";
            var source = !string.IsNullOrWhiteSpace(HostDisplay) ? HostDisplay.Replace("www.", "") : Title;
            return string.IsNullOrWhiteSpace(source) ? "•" : char.ToUpperInvariant(source[0]).ToString();
        }
    }

    public ImageSource? Favicon
    {
        get => _favicon;
        set
        {
            if (SetField(ref _favicon, value))
                OnPropertyChanged(nameof(HasFavicon));
        }
    }

    public bool HasFavicon => _favicon is not null;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        set => SetField(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        set => SetField(ref _canGoForward, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public bool IsInPrivate
    {
        get => _isInPrivate;
        set
        {
            if (SetField(ref _isInPrivate, value))
            {
                OnPropertyChanged(nameof(HostDisplay));
                OnPropertyChanged(nameof(BadgeLetter));
            }
        }
    }

    public bool IsPlayingAudio
    {
        get => _isPlayingAudio;
        set
        {
            if (SetField(ref _isPlayingAudio, value))
                OnPropertyChanged(nameof(ShowAudioButton));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(AudioIcon));
                OnPropertyChanged(nameof(ShowAudioButton));
            }
        }
    }

    public bool ShowAudioButton => _isPlayingAudio || _isMuted;

    public string AudioIcon => _isMuted ? "\uE74F" : "\uE767";

    public bool ForceDarkMode
    {
        get => _forceDarkMode;
        set => SetField(ref _forceDarkMode, value);
    }

    public bool IsReaderMode
    {
        get => _isReaderMode;
        set => SetField(ref _isReaderMode, value);
    }

    public bool IsMobileMode
    {
        get => _isMobileMode;
        set => SetField(ref _isMobileMode, value);
    }

    public double ZoomFactor
    {
        get => _zoomFactor;
        set
        {
            if (SetField(ref _zoomFactor, Math.Clamp(value, 0.25, 3.0)))
                OnPropertyChanged(nameof(ZoomDisplay));
        }
    }

    public string ZoomDisplay => $"{(int)Math.Round(_zoomFactor * 100)}%";

    public TabSleepState SleepState
    {
        get => _sleepState;
        set
        {
            if (SetField(ref _sleepState, value))
            {
                OnPropertyChanged(nameof(IsSleeping));
                OnPropertyChanged(nameof(SleepBadge));
            }
        }
    }

    public bool IsSleeping => _sleepState != TabSleepState.Active;

    public string SleepBadge => _sleepState switch
    {
        TabSleepState.Discarded => "Zz",
        TabSleepState.Suspended => "z",
        _ => string.Empty
    };

    public int BlockedCount
    {
        get => _blockedCount;
        set => SetField(ref _blockedCount, value);
    }

    public DateTime LastActiveUtc
    {
        get => _lastActiveUtc;
        set => SetField(ref _lastActiveUtc, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SavedSessionTab
{
    public string Url { get; set; } = TabItemModel.NewTabUrl;
    public string Title { get; set; } = "Yeni Sekme";
    public string? GroupName { get; set; }
    public string? GroupColorHex { get; set; }
    public bool IsPinned { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
}

public sealed class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string LinkedUrl { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Preview => Content.Length > 90 ? Content[..90] + "..." : Content;
}

public sealed class BookmarkItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    [JsonIgnore]
    public bool HasIcon => _icon is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed class PasswordEntry : INotifyPropertyChanged
{
    private bool _isRevealed;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SiteUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PlainPassword { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsRevealed
    {
        get => _isRevealed;
        set
        {
            if (_isRevealed == value) return;
            _isRevealed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayPassword));
        }
    }

    public string DisplayPassword => _isRevealed ? PlainPassword : "••••••••••••";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed class UserProfile
{
    public string Id { get; set; } = "default";
    public string Name { get; set; } = "Kişisel";
    public string ColorHex { get; set; } = "#4F8CFF";
}

public sealed class ReadingListItem : INotifyPropertyChanged
{
    private bool _isRead;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    public string StatusLabel => _isRead ? "Okundu" : "Okunmadı";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int VisitCount { get; set; } = 1;
    public DateTime LastVisitedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class DownloadItem : INotifyPropertyChanged
{
    private string _status = "İndiriliyor";
    private long _bytesReceived;
    private long _totalBytes;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FileName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string SavePath { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (_bytesReceived == value) return;
            _bytesReceived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes == value) return;
            _totalBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText
    {
        get
        {
            if (Status == "Tamamlandı") return TotalBytes > 0 ? $"Tamamlandı • {FormatBytes(TotalBytes)}" : "Tamamlandı";
            if (Status == "İptal Edildi") return "İptal Edildi";
            if (TotalBytes > 0)
            {
                var pct = (int)Math.Clamp((BytesReceived * 100L) / TotalBytes, 0, 100);
                return $"{pct}% • {FormatBytes(BytesReceived)} / {FormatBytes(TotalBytes)}";
            }
            return BytesReceived > 0 ? $"{Status} • {FormatBytes(BytesReceived)}" : Status;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class SpeedDialItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    [JsonIgnore]
    public string Letter => string.IsNullOrWhiteSpace(Title) ? "W" : char.ToUpperInvariant(Title.Trim()[0]).ToString();

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    [JsonIgnore]
    public bool HasIcon => _icon is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed class BrowserSettings
{
    public string LayoutMode { get; set; } = "Vertical"; // "Vertical" | "Horizontal"
    public string Theme { get; set; } = "Dark"; // "Dark" | "Light"
    public string AccentColor { get; set; } = "#4F8CFF";
    public bool EnableMicaBackdrop { get; set; } = true;
    public int SleepTimeoutMinutes { get; set; } = 5;
    public bool AdBlockEnabled { get; set; } = true;
    public bool CosmeticAdBlockEnabled { get; set; } = true;
    public bool ForceDarkModeGlobal { get; set; }
    public bool RestoreSessionOnStartup { get; set; } = true;
    public string SearchEngine { get; set; } = "https://www.google.com/search?q=";
    public bool SidebarCollapsed { get; set; }
    public bool ShowBookmarksBar { get; set; }
    public string NewTabBackground { get; set; } = "Default"; // "Default" | "Aurora" | "Midnight" | "Sunset" | "Forest" | "Custom"
    public string? NewTabWallpaperPath { get; set; }
    public List<string> WhitelistedHosts { get; set; } = new();
    public List<string> CustomBlockedDomains { get; set; } = new();
    public List<string> FocusBlockedHosts { get; set; } = ["youtube.com", "instagram.com", "tiktok.com", "x.com", "twitter.com", "reddit.com", "twitch.tv"];
    public Dictionary<string, string> SitePermissions { get; set; } = new();
    public string? AppLockPinHash { get; set; }
    public bool RequirePinOnStartup { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateFeedUrl { get; set; } = "https://github.com/billythestudent/kite";
    public List<SpeedDialItem> SpeedDial { get; set; } =
    [
        new() { Id = "sd1", Title = "Google", Url = "https://www.google.com" },
        new() { Id = "sd2", Title = "YouTube", Url = "https://www.youtube.com" },
        new() { Id = "sd3", Title = "GitHub", Url = "https://github.com" },
        new() { Id = "sd4", Title = "Wikipedia", Url = "https://tr.wikipedia.org" },
        new() { Id = "sd5", Title = "DuckDuckGo", Url = "https://duckduckgo.com" },
        new() { Id = "sd6", Title = "MDN Web Docs", Url = "https://developer.mozilla.org" }
    ];
}

public sealed class CommandPaletteEntry
{
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public Action Execute { get; init; } = static () => { };
}

public sealed class OmniboxSuggestion
{
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
}

public sealed class DevToolsConsoleEntry
{
    public string Level { get; init; } = "LOG";
    public string Message { get; init; } = string.Empty;
    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");
}

public sealed class DevToolsNetworkEntry
{
    public string Status { get; init; } = "REQ";
    public string Url { get; init; } = string.Empty;
    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");
    public bool IsBlocked { get; init; }
}

public sealed class PageContextMenuRequest
{
    public required TabItemModel Tab { get; init; }
    public string? LinkUri { get; init; }
    public string? SelectionText { get; init; }
    public string? SourceUri { get; init; }
    public bool IsEditable { get; init; }
}

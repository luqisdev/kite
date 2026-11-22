using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KitBrowser.Core.Models;
using Velopack;
using Velopack.Sources;

namespace KitBrowser.Core.Services;

// ===================== OMNIBOX URL / SEARCH PARSER =====================

public static class OmniboxParser
{
    public static string ResolveInput(string? rawInput, string searchEngineTemplate)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return TabItemModel.NewTabUrl;

        var trimmed = rawInput.Trim();

        if (trimmed.Equals(TabItemModel.NewTabUrl, StringComparison.OrdinalIgnoreCase))
            return TabItemModel.NewTabUrl;

        if (trimmed.Equals(TabItemModel.SettingsUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://settings", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.SettingsUrl;

        if (trimmed.Equals(TabItemModel.PerformanceUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://performance", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.PerformanceUrl;

        if (trimmed.Equals(TabItemModel.HistoryUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://history", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.HistoryUrl;

        if (trimmed.Equals(TabItemModel.BookmarksUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://bookmarks", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.BookmarksUrl;

        if (trimmed.Equals(TabItemModel.DownloadsUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://downloads", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.DownloadsUrl;

        if (trimmed.Equals(TabItemModel.PasswordsUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://passwords", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.PasswordsUrl;

        if (trimmed.Equals(TabItemModel.AdBlockUrl, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("kit://adblock", StringComparison.OrdinalIgnoreCase))
            return TabItemModel.AdBlockUrl;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri) &&
            (absoluteUri.Scheme == Uri.UriSchemeHttp ||
             absoluteUri.Scheme == Uri.UriSchemeHttps ||
             absoluteUri.Scheme == Uri.UriSchemeFile))
        {
            return UrlCleanerService.StripTrackingParameters(absoluteUri.AbsoluteUri);
        }

        if (!trimmed.Contains(' ') &&
            (trimmed.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase)))
        {
            var candidate = "http://" + trimmed;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var localUri))
                return localUri.AbsoluteUri;
        }

        if (!trimmed.Contains(' ') && trimmed.Contains('.'))
        {
            var candidate = "https://" + trimmed;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var domainUri) &&
                !string.IsNullOrWhiteSpace(domainUri.Host) &&
                domainUri.Host.Contains('.'))
            {
                return domainUri.AbsoluteUri;
            }
        }

        var engine = string.IsNullOrWhiteSpace(searchEngineTemplate)
            ? "https://www.google.com/search?q="
            : searchEngineTemplate;

        return engine + Uri.EscapeDataString(trimmed);
    }
}

// ===================== URL TRACKING PARAMETER STRIPPER =====================

public static class UrlCleanerService
{
    private static readonly FrozenSet<string> TrackingParams = new[]
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "fbclid", "gclid", "dclid", "msclkid", "twclid", "yclid",
        "mc_cid", "mc_eid", "igshid", "ref_src", "ref_url",
        "_hsenc", "_hsmi", "spm", "vero_id", "oly_anon_id", "oly_enc_id"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string StripTrackingParameters(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Contains('?'))
            return url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return url;

        var rawQuery = uri.Query.TrimStart('?');
        if (string.IsNullOrEmpty(rawQuery))
            return url;

        var parts = rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        bool removedAny = false;

        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            var key = eqIdx >= 0 ? part[..eqIdx] : part;
            if (TrackingParams.Contains(key) || key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            {
                removedAny = true;
                continue;
            }
            kept.Add(part);
        }

        if (!removedAny)
            return url;

        var builder = new UriBuilder(uri)
        {
            Query = kept.Count > 0 ? string.Join("&", kept) : string.Empty
        };

        if ((uri.Scheme == Uri.UriSchemeHttp && uri.Port == 80) ||
            (uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443))
        {
            builder.Port = -1;
        }

        return builder.Uri.AbsoluteUri;
    }
}

// ===================== INSTANT OMNIBOX CALCULATOR =====================

public static class InstantCalculatorService
{
    public static bool TryEvaluate(string? input, out string formattedResult)
    {
        formattedResult = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim().Replace(',', '.');
        bool hasOp = false;
        foreach (var ch in s)
        {
            if (ch is '+' or '-' or '*' or '/' or '%' or '^')
                hasOp = true;
            else if (!char.IsDigit(ch) && ch is not ('.' or '(' or ')' or ' '))
                return false;
        }
        if (!hasOp) return false;

        try
        {
            var parser = new ExprParser(s);
            var value = parser.ParseExpression();
            parser.SkipSpaces();
            if (!parser.IsAtEnd || double.IsNaN(value) || double.IsInfinity(value))
                return false;

            formattedResult = Math.Abs(value - Math.Round(value)) < 1e-9
                ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                : Math.Round(value, 6).ToString("G12", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private ref struct ExprParser(string text)
    {
        private readonly string _text = text;
        private int _pos = 0;

        public bool IsAtEnd => _pos >= _text.Length;

        public void SkipSpaces()
        {
            while (_pos < _text.Length && _text[_pos] == ' ') _pos++;
        }

        public double ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == '+') { _pos++; left += ParseTerm(); }
                else if (_pos < _text.Length && _text[_pos] == '-') { _pos++; left -= ParseTerm(); }
                else return left;
            }
        }

        private double ParseTerm()
        {
            var left = ParsePower();
            while (true)
            {
                SkipSpaces();
                if (_pos < _text.Length && _text[_pos] == '*') { _pos++; left *= ParsePower(); }
                else if (_pos < _text.Length && _text[_pos] == '/')
                {
                    _pos++;
                    var right = ParsePower();
                    if (Math.Abs(right) < 1e-15) throw new DivideByZeroException();
                    left /= right;
                }
                else if (_pos < _text.Length && _text[_pos] == '%')
                {
                    _pos++;
                    var right = ParsePower();
                    if (Math.Abs(right) < 1e-15) throw new DivideByZeroException();
                    left %= right;
                }
                else return left;
            }
        }

        private double ParsePower()
        {
            var left = ParseUnary();
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == '^')
            {
                _pos++;
                return Math.Pow(left, ParsePower());
            }
            return left;
        }

        private double ParseUnary()
        {
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == '-') { _pos++; return -ParseUnary(); }
            if (_pos < _text.Length && _text[_pos] == '+') { _pos++; return ParseUnary(); }
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipSpaces();
            if (_pos < _text.Length && _text[_pos] == '(')
            {
                _pos++;
                var val = ParseExpression();
                SkipSpaces();
                if (_pos >= _text.Length || _text[_pos] != ')') throw new FormatException();
                _pos++;
                return val;
            }

            int start = _pos;
            bool hasDot = false;
            while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || (_text[_pos] == '.' && !hasDot)))
            {
                if (_text[_pos] == '.') hasDot = true;
                _pos++;
            }
            if (start == _pos) throw new FormatException();
            return double.Parse(_text[start.._pos], CultureInfo.InvariantCulture);
        }
    }
}

// ===================== BOOKMARK IMPORT / EXPORT =====================

public static partial class BookmarkTransferService
{
    [GeneratedRegex(@"<A\s+[^>]*HREF\s*=\s*""([^""]+)""[^>]*>(.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlLinkRegex();

    public static List<(string Title, string Url)> ParseFromFile(string filePath)
    {
        var content = File.ReadAllText(filePath, Encoding.UTF8);
        return content.TrimStart().StartsWith('{') ? ParseChromiumJson(content) : ParseNetscapeHtml(content);
    }

    public static List<(string Title, string Url)> ParseNetscapeHtml(string html)
    {
        var results = new List<(string Title, string Url)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HtmlLinkRegex().Matches(html))
        {
            var url = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            var title = WebUtility.HtmlDecode(match.Groups[2].Value.Trim());
            if ((url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                seen.Add(url))
            {
                results.Add((string.IsNullOrWhiteSpace(title) ? url : title, url));
            }
        }
        return results;
    }

    public static List<(string Title, string Url)> ParseChromiumJson(string json)
    {
        var results = new List<(string Title, string Url)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("roots", out var roots))
        {
            foreach (var prop in roots.EnumerateObject())
                WalkChromiumNode(prop.Value, results, seen);
        }
        return results;
    }

    private static void WalkChromiumNode(JsonElement node, List<(string Title, string Url)> results, HashSet<string> seen)
    {
        if (node.ValueKind != JsonValueKind.Object) return;

        if (node.TryGetProperty("type", out var typeProp) &&
            string.Equals(typeProp.GetString(), "url", StringComparison.OrdinalIgnoreCase) &&
            node.TryGetProperty("url", out var urlProp))
        {
            var url = urlProp.GetString()?.Trim() ?? string.Empty;
            var title = node.TryGetProperty("name", out var nameProp) ? (nameProp.GetString()?.Trim() ?? url) : url;
            if ((url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                seen.Add(url))
            {
                results.Add((string.IsNullOrWhiteSpace(title) ? url : title, url));
            }
        }

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
                WalkChromiumNode(child, results, seen);
        }
    }

    public static string ExportToNetscapeHtml(IEnumerable<BookmarkItem> bookmarks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>KitBrowser Yer İmleri</TITLE>");
        sb.AppendLine("<H1>KitBrowser Yer İmleri</H1>");
        sb.AppendLine("<DL><p>");
        foreach (var bm in bookmarks)
            sb.AppendLine($"    <DT><A HREF=\"{WebUtility.HtmlEncode(bm.Url)}\">{WebUtility.HtmlEncode(bm.Title)}</A>");
        sb.AppendLine("</DL><p>");
        return sb.ToString();
    }

    public static string? FindInstalledChromiumBookmarksFile()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Bookmarks"),
            Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Bookmarks")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

// ===================== FAVICON DISK & MEMORY CACHE =====================

public sealed class FaviconCacheService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, ImageSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    public FaviconCacheService(string? cacheDir = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitBrowser",
            "FaviconCache");
        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<ImageSource?> GetFaviconAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (_memoryCache.TryGetValue(host, out var cached))
            return cached;

        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(host.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        var filePath = Path.Combine(_cacheDir, safeName + ".png");

        if (File.Exists(filePath))
        {
            var fromDisk = await Task.Run(() =>
            {
                try { return DecodeFrozenImage(File.ReadAllBytes(filePath)); } catch { return null; }
            }).ConfigureAwait(false);

            if (fromDisk is not null)
            {
                _memoryCache[host] = fromDisk;
                return fromDisk;
            }
        }

        try
        {
            var endpoint = $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(host)}&sz=64";
            var bytes = await Http.GetByteArrayAsync(endpoint).ConfigureAwait(false);
            if (bytes.Length < 32) return null;

            var img = DecodeFrozenImage(bytes);
            if (img is null) return null;

            _memoryCache[host] = img;
            _ = Task.Run(async () => { try { await File.WriteAllBytesAsync(filePath, bytes).ConfigureAwait(false); } catch { } });
            return img;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? DecodeFrozenImage(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}

// ===================== VELOPACK DELTA AUTO-UPDATER =====================

public sealed class AppUpdateService
{
    public const string DefaultFeedUrl = "https://github.com/luqisdev/kite";
    public UpdateInfo? PendingUpdate { get; private set; }
    private UpdateManager? _manager;

    public string CurrentVersion =>
        typeof(AppUpdateService).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.0.0";

    private static UpdateManager CreateManager(string? feedUrl = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(feedUrl) ? DefaultFeedUrl : feedUrl.Trim();
        if (trimmed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return new UpdateManager(new GithubSource(trimmed, accessToken: null, prerelease: false));

        return new UpdateManager(trimmed);
    }

    public async Task<(bool Available, string StatusMessage, string? TargetVersion)> CheckForUpdatesAsync(string? feedUrl = null)
    {
        try
        {
            _manager = CreateManager(feedUrl);
            if (!_manager.IsInstalled)
                return (false, $"v{CurrentVersion} (Geliştirici / taşınabilir mod • Kurulu sürümde otomatik delta güncelleme aktif olur)", null);

            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                PendingUpdate = null;
                return (false, $"KitBrowser güncel (v{CurrentVersion})", null);
            }

            PendingUpdate = info;
            var targetVer = info.TargetFullRelease.Version.ToString();
            var isDelta = info.DeltasToTarget.Length > 0;
            var modeText = isDelta ? "Delta (fark) paketi" : "Tam paket";
            return (true, $"Yeni sürüm hazır: v{targetVer} ({modeText})", targetVer);
        }
        catch (Exception ex)
        {
            return (false, $"Güncelleme denetlenemedi: {ex.Message}", null);
        }
    }

    public async Task<(bool Success, string Message)> DownloadAndApplyAsync(Action<int>? onProgress = null)
    {
        if (_manager is null || PendingUpdate is null)
            return (false, "İndirilecek güncelleme bulunamadı.");

        try
        {
            await _manager.DownloadUpdatesAsync(PendingUpdate, progress => onProgress?.Invoke(progress)).ConfigureAwait(false);
            _manager.ApplyUpdatesAndRestart(PendingUpdate);
            return (true, "Güncelleme uygulanıyor...");
        }
        catch (Exception ex)
        {
            return (false, $"Güncelleme indirilemedi: {ex.Message}");
        }
    }
}

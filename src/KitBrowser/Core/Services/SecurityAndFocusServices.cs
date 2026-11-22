using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace KitBrowser.Core.Services;

// ===================== AD & TRACKER BLOCKER =====================

public sealed class AdBlockService
{
    public const string CosmeticAdHideScript = """
        (() => {
            if (document.getElementById('kitbrowser-cosmetic-adblock')) return;
            const style = document.createElement('style');
            style.id = 'kitbrowser-cosmetic-adblock';
            style.textContent = `
                ins.adsbygoogle, [id^="google_ads_"], [id^="div-gpt-ad"], .adsbygoogle,
                .ad-container, .ad-banner, .sponsored-ad, .taboola-widget, .outbrain-widget,
                iframe[src*="doubleclick.net"], iframe[src*="googlesyndication.com"] {
                    display: none !important; visibility: hidden !important; height: 0 !important; min-height: 0 !important;
                }
            `;
            (document.head || document.documentElement).appendChild(style);
        })();
        """;

    private readonly FrozenSet<string> _blockedDomains;
    private readonly ConcurrentDictionary<string, byte> _customBlocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _whitelist = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _enabled;
    private volatile bool _cosmeticEnabled = true;
    private long _totalBlocked;

    public AdBlockService(bool enabled = true, IEnumerable<string>? customDomains = null)
    {
        _enabled = enabled;
        var domains = customDomains is not null
            ? customDomains.Select(Normalize).Where(d => d.Length > 0)
            : LoadEmbeddedBlocklist();

        _blockedDomains = domains.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool Enabled { get => _enabled; set => _enabled = value; }
    public bool CosmeticEnabled { get => _cosmeticEnabled; set => _cosmeticEnabled = value; }
    public long TotalBlocked => Interlocked.Read(ref _totalBlocked);
    public int RuleCount => _blockedDomains.Count + _customBlocked.Count;
    public long IncrementBlocked() => Interlocked.Increment(ref _totalBlocked);

    public void LoadCustomBlocked(IEnumerable<string> hosts)
    {
        _customBlocked.Clear();
        foreach (var h in hosts)
        {
            var key = Normalize(h);
            if (!string.IsNullOrEmpty(key)) _customBlocked[key] = 1;
        }
    }

    public List<string> GetCustomBlockedHosts() => _customBlocked.Keys.OrderBy(k => k).ToList();
    public bool AddCustomBlocked(string host) { var k = Normalize(host); return !string.IsNullOrEmpty(k) && _customBlocked.TryAdd(k, 1); }
    public bool RemoveCustomBlocked(string host) => _customBlocked.TryRemove(Normalize(host), out _);

    public void LoadWhitelist(IEnumerable<string> hosts)
    {
        _whitelist.Clear();
        foreach (var h in hosts)
        {
            var key = Normalize(h);
            if (!string.IsNullOrEmpty(key)) _whitelist[key] = 1;
        }
    }

    public List<string> GetWhitelistedHosts() => _whitelist.Keys.OrderBy(k => k).ToList();

    public bool ToggleWhitelist(string host)
    {
        var key = Normalize(host);
        if (string.IsNullOrEmpty(key)) return false;
        if (_whitelist.TryRemove(key, out _)) return false;
        _whitelist[key] = 1;
        return true;
    }

    public bool IsWhitelisted(string? host) => !string.IsNullOrWhiteSpace(host) && _whitelist.ContainsKey(Normalize(host));

    public bool ShouldBlock(string? requestUrl, string? pageHost = null)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(requestUrl)) return false;
        if (!string.IsNullOrWhiteSpace(pageHost) && IsWhitelisted(pageHost)) return false;
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return false;

        var candidate = uri.Host;
        while (true)
        {
            if (_blockedDomains.Contains(candidate) || _customBlocked.ContainsKey(candidate))
                return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0 || dot == candidate.Length - 1) return false;
            candidate = candidate[(dot + 1)..];
        }
    }

    private static IEnumerable<string> LoadEmbeddedBlocklist()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("blocklist.txt", StringComparison.OrdinalIgnoreCase));
        if (name is null) return Array.Empty<string>();
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null) return Array.Empty<string>();
        using var reader = new StreamReader(stream);
        var list = new List<string>(256);
        while (reader.ReadLine() is { } line)
        {
            var t = Normalize(line);
            if (t.Length > 0 && !t.StartsWith('#')) list.Add(t);
        }
        return list;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}

// ===================== FOCUS MODE (POMODORO) =====================

public sealed class FocusModeService
{
    private readonly ConcurrentDictionary<string, byte> _blockedHosts = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isRunning;
    private volatile bool _isBreak;
    private int _remainingSeconds = 25 * 60;

    public FocusModeService(IEnumerable<string>? initialBlockedHosts = null)
    {
        LoadBlockedHosts(initialBlockedHosts ?? ["youtube.com", "instagram.com", "tiktok.com", "x.com", "twitter.com", "reddit.com", "twitch.tv"]);
    }

    public bool IsRunning => _isRunning;
    public bool IsBreak => _isBreak;
    public int RemainingSeconds => _remainingSeconds;
    public string FormattedTime => $"{_remainingSeconds / 60:D2}:{_remainingSeconds % 60:D2}";

    public void LoadBlockedHosts(IEnumerable<string> hosts)
    {
        _blockedHosts.Clear();
        foreach (var h in hosts)
        {
            var clean = NormalizeHost(h);
            if (!string.IsNullOrEmpty(clean)) _blockedHosts[clean] = 1;
        }
    }

    public List<string> GetBlockedHosts() => _blockedHosts.Keys.OrderBy(k => k).ToList();
    public bool AddBlockedHost(string host) { var c = NormalizeHost(host); return !string.IsNullOrEmpty(c) && _blockedHosts.TryAdd(c, 1); }
    public bool RemoveBlockedHost(string host) => _blockedHosts.TryRemove(NormalizeHost(host), out _);

    public void StartWork(int minutes = 25)
    {
        _isBreak = false;
        if (_remainingSeconds <= 0) _remainingSeconds = Math.Clamp(minutes, 1, 180) * 60;
        _isRunning = true;
    }

    public void StartBreak(int minutes = 5)
    {
        _isBreak = true;
        _remainingSeconds = Math.Clamp(minutes, 1, 60) * 60;
        _isRunning = true;
    }

    public void Pause() => _isRunning = false;

    public void Reset(int minutes = 25)
    {
        _isRunning = false;
        _isBreak = false;
        _remainingSeconds = Math.Clamp(minutes, 1, 180) * 60;
    }

    public bool TickOneSecond()
    {
        if (!_isRunning) return false;
        if (_remainingSeconds > 0) { _remainingSeconds--; return false; }
        _isRunning = false;
        return true;
    }

    public bool IsUrlBlocked(string? url)
    {
        if (!_isRunning || _isBreak || string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return false;

        var candidate = uri.Host.ToLowerInvariant();
        while (true)
        {
            if (_blockedHosts.ContainsKey(candidate)) return true;
            var dot = candidate.IndexOf('.');
            if (dot < 0 || dot == candidate.Length - 1) return false;
            candidate = candidate[(dot + 1)..];
        }
    }

    public string BuildBlockedHtml(string blockedHost) => $$"""
        <!doctype html>
        <html lang="tr">
        <head>
            <meta charset="utf-8" /><title>Odak Kalkanı — KitBrowser</title>
            <style>
                body { margin:0; height:100vh; display:flex; align-items:center; justify-content:center;
                       background:#0F1117; color:#F3F5FA; font-family:'Segoe UI',sans-serif; }
                .card { max-width:480px; text-align:center; padding:40px 36px; border-radius:18px;
                        background:#1B1F2B; border:1px solid #262B3B; }
                .badge { display:inline-block; padding:6px 14px; border-radius:999px;
                         background:rgba(79,140,255,0.16); color:#4F8CFF; font-weight:600; font-size:13px; margin-bottom:16px; }
                h1 { margin:0 0 10px; font-size:26px; }
                p { color:#A3ADC2; line-height:1.6; font-size:14.5px; }
                .timer { font-size:42px; font-weight:300; margin:20px 0 8px; color:#34D399; }
            </style>
        </head>
        <body>
            <div class="card">
                <div class="badge">Odak Kalkanı Aktif</div>
                <h1>Şimdi Odaklanma Zamanı</h1>
                <p><strong>{{System.Net.WebUtility.HtmlEncode(blockedHost)}}</strong>, Pomodoro odak oturumun bitene kadar geçici olarak engellendi.</p>
                <div class="timer">{{FormattedTime}}</div>
                <p style="font-size:12px;color:#6B758A;">Odak Modu panelinden oturumu duraklatabilir veya site listesini düzenleyebilirsin.</p>
            </div>
        </body>
        </html>
        """;

    private static string NormalizeHost(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if ((s.StartsWith("http://") || s.StartsWith("https://")) && Uri.TryCreate(s, UriKind.Absolute, out var u))
            s = u.Host.ToLowerInvariant();
        return (s.StartsWith("www.") ? s[4..] : s).Trim('/');
    }
}

// ===================== PER-SITE PERMISSIONS =====================

public enum SitePermissionState { Ask, Allow, Deny }

public sealed class SitePermissionService
{
    private readonly ConcurrentDictionary<string, SitePermissionState> _rules = new(StringComparer.OrdinalIgnoreCase);

    public void Load(Dictionary<string, string>? saved)
    {
        _rules.Clear();
        if (saved is null) return;
        foreach (var (k, v) in saved)
            if (Enum.TryParse<SitePermissionState>(v, ignoreCase: true, out var state)) _rules[k] = state;
    }

    public Dictionary<string, string> Export()
        => _rules.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    public SitePermissionState GetState(string host, string permissionKind)
        => string.IsNullOrWhiteSpace(host) ? SitePermissionState.Ask : (_rules.TryGetValue(MakeKey(host, permissionKind), out var s) ? s : SitePermissionState.Ask);

    public SitePermissionState CycleState(string host, string permissionKind)
    {
        var next = GetState(host, permissionKind) switch
        {
            SitePermissionState.Ask => SitePermissionState.Allow,
            SitePermissionState.Allow => SitePermissionState.Deny,
            _ => SitePermissionState.Ask
        };
        var key = MakeKey(host, permissionKind);
        if (next == SitePermissionState.Ask) _rules.TryRemove(key, out _);
        else _rules[key] = next;
        return next;
    }

    public static string ToTurkishLabel(SitePermissionState state) => state switch
    {
        SitePermissionState.Allow => "İzin Ver",
        SitePermissionState.Deny => "Engelle",
        _ => "Sor"
    };

    private static string MakeKey(string host, string kind) => $"{host.Trim().ToLowerInvariant()}|{kind.Trim().ToLowerInvariant()}";
}

// ===================== DPAPI PASSWORD VAULT =====================

public static class PasswordVaultService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KitBrowser.DPAPI.Vault.v1");

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64)) return string.Empty;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipherBase64), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return string.Empty; }
    }

    public static string GenerateStrongPassword(int length = 18)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*()-_=+";
        var result = new char[Math.Clamp(length, 8, 64)];
        for (int i = 0; i < result.Length; i++)
            result[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        return new string(result);
    }
}

// ===================== BROWSER PIN LOCK =====================

public static class PinLockService
{
    private const string Salt = "KitBrowser.AppLock.v1::";

    public static string HashPin(string pin)
        => string.IsNullOrWhiteSpace(pin) ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + pin.Trim())));

    public static bool VerifyPin(string? inputPin, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(inputPin) || string.IsNullOrWhiteSpace(storedHash)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashPin(inputPin)),
            Encoding.UTF8.GetBytes(storedHash));
    }
}

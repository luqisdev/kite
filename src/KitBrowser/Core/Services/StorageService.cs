using System.IO;
using System.Text.Json;
using KitBrowser.Core.Models;
using Microsoft.Data.Sqlite;

namespace KitBrowser.Core.Services;

public sealed class StorageService : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StorageService(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        InitializeSchema();
    }

    public static StorageService OpenForProfile(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var storage = new StorageService($"Data Source={dbPath};Cache=Shared");
        storage.EnableWalMode();
        return storage;
    }

    public static StorageService OpenDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitBrowser");
        return OpenForProfile(Path.Combine(dir, "kitbrowser.db"));
    }

    private void EnableWalMode()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS bookmarks (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS history (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL UNIQUE,
                visit_count INTEGER NOT NULL DEFAULT 1,
                last_visited_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS downloads (
                id TEXT PRIMARY KEY,
                filename TEXT NOT NULL,
                url TEXT NOT NULL,
                save_path TEXT NOT NULL,
                status TEXT NOT NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS notes (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                content TEXT NOT NULL,
                linked_url TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS session_tabs (
                sort_order INTEGER PRIMARY KEY,
                url TEXT NOT NULL,
                title TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                is_pinned INTEGER NOT NULL DEFAULT 0,
                is_active INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS passwords (
                id TEXT PRIMARY KEY,
                site_url TEXT NOT NULL,
                username TEXT NOT NULL,
                encrypted_password TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reading_list (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL UNIQUE,
                is_read INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public async Task<BrowserSettings> GetSettingsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key = 'browser_settings'";
            var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;
            if (string.IsNullOrWhiteSpace(result))
                return new BrowserSettings();

            var settings = JsonSerializer.Deserialize<BrowserSettings>(result) ?? new BrowserSettings();
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveSettingsAsync(BrowserSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings (key, value) VALUES ('browser_settings', $val)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$val", json);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<BookmarkItem>> ListBookmarksAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, title, url, created_at FROM bookmarks ORDER BY created_at DESC";
            var list = new List<BookmarkItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new BookmarkItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Url = reader.GetString(2),
                    CreatedAtUtc = DateTime.Parse(reader.GetString(3)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BookmarkItem> AddBookmarkAsync(string title, string url)
    {
        var item = new BookmarkItem
        {
            Title = string.IsNullOrWhiteSpace(title) ? url : title.Trim(),
            Url = url.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO bookmarks (id, title, url, created_at)
                VALUES ($id, $title, $url, $created)
                ON CONFLICT(url) DO UPDATE SET title = excluded.title
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$title", item.Title);
            cmd.Parameters.AddWithValue("$url", item.Url);
            cmd.Parameters.AddWithValue("$created", item.CreatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveBookmarkAsync(string url)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM bookmarks WHERE url = $url";
            cmd.Parameters.AddWithValue("$url", url.Trim());
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordHistoryAsync(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.StartsWith("kit://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("kite://", StringComparison.OrdinalIgnoreCase))
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history (id, title, url, visit_count, last_visited_at)
                VALUES ($id, $title, $url, 1, $now)
                ON CONFLICT(url) DO UPDATE SET
                    title = CASE WHEN excluded.title != '' THEN excluded.title ELSE history.title END,
                    visit_count = history.visit_count + 1,
                    last_visited_at = excluded.last_visited_at
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url : title.Trim());
            cmd.Parameters.AddWithValue("$url", url.Trim());
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<HistoryItem>> ListHistoryAsync(string? query = null, int limit = 100)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = """
                    SELECT id, title, url, visit_count, last_visited_at FROM history
                    WHERE title LIKE $q OR url LIKE $q
                    ORDER BY last_visited_at DESC LIMIT $limit
                    """;
                cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, title, url, visit_count, last_visited_at FROM history
                    ORDER BY last_visited_at DESC LIMIT $limit
                    """;
            }
            cmd.Parameters.AddWithValue("$limit", limit);

            var list = new List<HistoryItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new HistoryItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Url = reader.GetString(2),
                    VisitCount = reader.GetInt32(3),
                    LastVisitedUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearHistoryAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM history";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertDownloadAsync(DownloadItem item)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO downloads (id, filename, url, save_path, status, total_bytes, created_at)
                VALUES ($id, $name, $url, $path, $status, $total, $created)
                ON CONFLICT(id) DO UPDATE SET
                    status = excluded.status,
                    save_path = excluded.save_path,
                    total_bytes = excluded.total_bytes
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$name", item.FileName);
            cmd.Parameters.AddWithValue("$url", item.Url);
            cmd.Parameters.AddWithValue("$path", item.SavePath);
            cmd.Parameters.AddWithValue("$status", item.Status);
            cmd.Parameters.AddWithValue("$total", item.TotalBytes);
            cmd.Parameters.AddWithValue("$created", item.CreatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<DownloadItem>> ListDownloadsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, filename, url, save_path, status, total_bytes, created_at
                FROM downloads ORDER BY created_at DESC LIMIT 100
                """;
            var list = new List<DownloadItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new DownloadItem
                {
                    Id = reader.GetString(0),
                    FileName = reader.GetString(1),
                    Url = reader.GetString(2),
                    SavePath = reader.GetString(3),
                    Status = reader.GetString(4),
                    TotalBytes = reader.GetInt64(5),
                    CreatedAtUtc = DateTime.Parse(reader.GetString(6)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearDownloadsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===================== NOTES =====================

    public async Task<List<NoteItem>> ListNotesAsync(string? query = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = """
                    SELECT id, title, content, linked_url, updated_at FROM notes
                    WHERE title LIKE $q OR content LIKE $q
                    ORDER BY updated_at DESC
                    """;
                cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
            }
            else
            {
                cmd.CommandText = "SELECT id, title, content, linked_url, updated_at FROM notes ORDER BY updated_at DESC";
            }

            var list = new List<NoteItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new NoteItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Content = reader.GetString(2),
                    LinkedUrl = reader.GetString(3),
                    UpdatedAtUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveNoteAsync(NoteItem note)
    {
        note.UpdatedAtUtc = DateTime.UtcNow;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO notes (id, title, content, linked_url, updated_at)
                VALUES ($id, $title, $content, $url, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    title = excluded.title,
                    content = excluded.content,
                    linked_url = excluded.linked_url,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", note.Id);
            cmd.Parameters.AddWithValue("$title", note.Title);
            cmd.Parameters.AddWithValue("$content", note.Content);
            cmd.Parameters.AddWithValue("$url", note.LinkedUrl);
            cmd.Parameters.AddWithValue("$updated", note.UpdatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteNoteAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM notes WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===================== SESSION RESTORE =====================

    public async Task SaveSessionTabsAsync(IReadOnlyList<SavedSessionTab> tabs)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var tx = _connection.BeginTransaction();
            using (var del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM session_tabs";
                await del.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            for (int i = 0; i < tabs.Count; i++)
            {
                var t = tabs[i];
                var groupMeta = string.IsNullOrWhiteSpace(t.GroupName)
                    ? string.Empty
                    : $"{t.GroupName}|{t.GroupColorHex ?? "#4F8CFF"}";
                using var ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO session_tabs (sort_order, url, title, workspace_id, is_pinned, is_active)
                    VALUES ($ord, $url, $title, $grp, $pin, $act)
                    """;
                ins.Parameters.AddWithValue("$ord", i);
                ins.Parameters.AddWithValue("$url", t.Url);
                ins.Parameters.AddWithValue("$title", t.Title);
                ins.Parameters.AddWithValue("$grp", groupMeta);
                ins.Parameters.AddWithValue("$pin", t.IsPinned ? 1 : 0);
                ins.Parameters.AddWithValue("$act", t.IsActive ? 1 : 0);
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            tx.Commit();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<SavedSessionTab>> LoadSessionTabsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT sort_order, url, title, workspace_id, is_pinned, is_active
                FROM session_tabs ORDER BY sort_order ASC
                """;
            var list = new List<SavedSessionTab>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var rawGroup = reader.GetString(3);
                string? groupName = null;
                string? groupColor = null;
                if (!string.IsNullOrWhiteSpace(rawGroup) && rawGroup.Contains('|'))
                {
                    var parts = rawGroup.Split('|', 2);
                    groupName = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0];
                    groupColor = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
                }

                list.Add(new SavedSessionTab
                {
                    SortOrder = reader.GetInt32(0),
                    Url = reader.GetString(1),
                    Title = reader.GetString(2),
                    GroupName = groupName,
                    GroupColorHex = groupColor,
                    IsPinned = reader.GetInt32(4) == 1,
                    IsActive = reader.GetInt32(5) == 1
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===================== PASSWORDS (DPAPI ENCRYPTED) =====================

    public async Task<List<PasswordEntry>> ListPasswordsAsync(string? query = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = """
                    SELECT id, site_url, username, encrypted_password, updated_at FROM passwords
                    WHERE site_url LIKE $q OR username LIKE $q
                    ORDER BY updated_at DESC
                    """;
                cmd.Parameters.AddWithValue("$q", $"%{query.Trim()}%");
            }
            else
            {
                cmd.CommandText = "SELECT id, site_url, username, encrypted_password, updated_at FROM passwords ORDER BY updated_at DESC";
            }

            var list = new List<PasswordEntry>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new PasswordEntry
                {
                    Id = reader.GetString(0),
                    SiteUrl = reader.GetString(1),
                    Username = reader.GetString(2),
                    PlainPassword = PasswordVaultService.Unprotect(reader.GetString(3)),
                    UpdatedAtUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SavePasswordAsync(PasswordEntry entry)
    {
        entry.UpdatedAtUtc = DateTime.UtcNow;
        var encrypted = PasswordVaultService.Protect(entry.PlainPassword);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO passwords (id, site_url, username, encrypted_password, updated_at)
                VALUES ($id, $site, $user, $enc, $updated)
                ON CONFLICT(id) DO UPDATE SET
                    site_url = excluded.site_url,
                    username = excluded.username,
                    encrypted_password = excluded.encrypted_password,
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id);
            cmd.Parameters.AddWithValue("$site", entry.SiteUrl.Trim());
            cmd.Parameters.AddWithValue("$user", entry.Username.Trim());
            cmd.Parameters.AddWithValue("$enc", encrypted);
            cmd.Parameters.AddWithValue("$updated", entry.UpdatedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeletePasswordAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM passwords WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ===================== READING LIST (SONRA OKU) =====================

    public async Task<List<ReadingListItem>> ListReadingItemsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, title, url, is_read, created_at FROM reading_list ORDER BY is_read ASC, created_at DESC";
            var list = new List<ReadingListItem>();
            using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                list.Add(new ReadingListItem
                {
                    Id = reader.GetString(0),
                    Title = reader.GetString(1),
                    Url = reader.GetString(2),
                    IsRead = reader.GetInt32(3) == 1,
                    CreatedAtUtc = DateTime.Parse(reader.GetString(4)).ToUniversalTime()
                });
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddReadingItemAsync(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO reading_list (id, title, url, is_read, created_at)
                VALUES ($id, $title, $url, 0, $created)
                ON CONFLICT(url) DO UPDATE SET title = excluded.title, is_read = 0
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? url : title.Trim());
            cmd.Parameters.AddWithValue("$url", url.Trim());
            cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ToggleReadingItemReadAsync(string id, bool isRead)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE reading_list SET is_read = $read WHERE id = $id";
            cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteReadingItemAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM reading_list WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _connection.Dispose();
    }
}

public sealed class ProfileManager
{
    private readonly string _rootDir;
    private readonly string _profilesFile;

    public ProfileManager(string? rootDir = null)
    {
        _rootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KitBrowser");
        Directory.CreateDirectory(_rootDir);
        _profilesFile = Path.Combine(_rootDir, "profiles.json");
    }

    public List<UserProfile> LoadProfiles()
    {
        try
        {
            if (File.Exists(_profilesFile))
            {
                var list = JsonSerializer.Deserialize<List<UserProfile>>(File.ReadAllText(_profilesFile));
                if (list is { Count: > 0 }) return list;
            }
        }
        catch { }

        var defaults = new List<UserProfile>
        {
            new() { Id = "default", Name = "Kişisel", ColorHex = "#4F8CFF" },
            new() { Id = "work", Name = "İş", ColorHex = "#10B981" }
        };
        SaveProfiles(defaults);
        return defaults;
    }

    public void SaveProfiles(List<UserProfile> profiles)
        => File.WriteAllText(_profilesFile, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));

    public UserProfile CreateProfile(string name, string colorHex)
    {
        var list = LoadProfiles();
        var profile = new UserProfile
        {
            Id = "p-" + Guid.NewGuid().ToString("N")[..8],
            Name = string.IsNullOrWhiteSpace(name) ? $"Profil {list.Count + 1}" : name.Trim(),
            ColorHex = string.IsNullOrWhiteSpace(colorHex) ? "#8B5CF6" : colorHex
        };
        list.Add(profile);
        SaveProfiles(list);
        return profile;
    }

    public string GetDatabasePath(string profileId)
    {
        if (string.Equals(profileId, "default", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_rootDir, "kitbrowser.db");
        var dir = Path.Combine(_rootDir, "Profiles", profileId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "kitbrowser.db");
    }

    public string GetWebViewDataPath(string profileId)
    {
        if (string.Equals(profileId, "default", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(_rootDir, "WebView2Data");
        var dir = Path.Combine(_rootDir, "Profiles", profileId, "WebView2Data");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ResolveProfileIdFromArgs(string[] args)
    {
        foreach (var arg in args)
            if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                var id = arg["--profile=".Length..].Trim();
                if (!string.IsNullOrEmpty(id)) return id;
            }
        return "default";
    }
}

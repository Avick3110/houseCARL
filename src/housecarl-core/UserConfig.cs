using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>The on-disk user config shape (houseCARL.user.json) — four independent concerns in one file; they coexist because the only writer is <see cref="UserConfigStore.Update"/>, as docs/architecture/mo2-instance.md sets out.</summary>
public sealed class UserConfig
{
    /// <summary>The MO2 instance folder housecarl_set_mo2_instance saved; it beats appsettings' Mo2InstanceDir, and absent means fall through to explicit paths.</summary>
    public string? Mo2InstanceDir { get; set; }

    /// <summary>External-tool paths the bridge saved, keyed by tool wire-name; absent until housecarl_set_tool_path is first called.</summary>
    public Dictionary<string, string>? ToolPaths { get; set; }

    /// <summary>Resolved plugin paths the user has acknowledged for IN-PLACE editing — the persistent, cross-session first-touch handshake; it waives the consent axis only, never the touched-record verify.</summary>
    public List<string>? InPlaceAcknowledged { get; set; }

    /// <summary>Named Papyrus import-directory sets: a project's dependency source folders supplied once and referenced by name thereafter.</summary>
    public Dictionary<string, List<string>>? ImportSets { get; set; }
}

/// <summary>The single OWNER of houseCARL.user.json — atomic, cross-process, and loud about a corrupt file; a write failure is RETURNED rather than thrown, so a tool can say the choice will not survive a restart.</summary>
public sealed class UserConfigStore
{
    readonly string _path;
    readonly object _gate = new();      // process-local fast path; the named mutex below adds the cross-process half
    readonly Mutex _mutex;              // named per-file: CLI + desktop server processes serialize on the same config
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public UserConfigStore(string path)
    {
        _path = path;
        _mutex = new Mutex(initiallyOwned: false, MutexName(path));
    }

    /// <summary>The file this store owns (for the tool confirmation / diagnostics).</summary>
    public string FilePath => _path;

    /// <summary>A stable, legal mutex name for the config file: the same file, case-insensitively, gives the same mutex in any process of this session.</summary>
    static string MutexName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return "Local\\houseCARL-user-config-" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Run <paramref name="body"/> holding BOTH locks; an abandoned mutex counts as acquired, and a timeout proceeds without the cross-process half rather than deadlocking a tool call.</summary>
    T WithLocks<T>(Func<T> body)
    {
        lock (_gate)
        {
            bool taken = false;
            try { taken = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { taken = true; }
            try { return body(); }
            finally { if (taken) _mutex.ReleaseMutex(); }
        }
    }

    /// <summary>Read the current config: a missing file yields a blank one, and a CORRUPT file is backed up beside itself and reported via <paramref name="note"/>.</summary>
    public UserConfig Load(out string? note)
    {
        var (cfg, n) = WithLocks(() => { var c = ReadOrRecover(out var rn); return (c, rn); });
        note = n;
        return cfg;
    }

    /// <summary>Read the current config, discarding any recovery note — for callers whose later <see cref="Update"/> or noted Load owns the loudness.</summary>
    public UserConfig Load() => Load(out _);

    /// <summary>Apply <paramref name="mutate"/> to the CURRENT on-disk config and write it back atomically — the only way the file is written; a write failure is reported in <c>error</c> and a recovered corrupt file in <c>note</c>.</summary>
    public (bool ok, string? error, string? note) Update(Action<UserConfig> mutate)
    {
        return WithLocks<(bool, string?, string?)>(() =>
        {
            string? note = null;
            try
            {
                var cfg = ReadOrRecover(out note);
                mutate(cfg);
                var dir = Path.GetDirectoryName(_path);   // the data dir (${CLAUDE_PLUGIN_DATA}) may not exist on the first save
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
                AtomicFile.Commit(tmp, _path);   // crash-atomic swap (File.Replace / rename) — a reader never sees a torn or vanished file
                return (true, null, note);
            }
            catch (Exception ex) { return (false, ex.Message, note); }
        });
    }

    /// <summary>True iff <paramref name="pluginPath"/> already carries a persisted in-place acknowledgement; FAIL-SAFE, so a missing or corrupt config reads as NOT acknowledged and the handshake re-prompts.</summary>
    public bool IsInPlaceAcknowledged(string pluginPath)
    {
        var key = NormalizePath(pluginPath);
        var ack = Load().InPlaceAcknowledged;
        return ack is not null && ack.Any(p => string.Equals(NormalizePath(p), key, StringComparison.Ordinal));
    }

    /// <summary>PERSIST an in-place acknowledgement (idempotent), through the same atomic read-modify-write as every other field; nothing caches it, so a failed write re-prompts on the very next call.</summary>
    public (bool ok, string? error) RecordInPlaceAcknowledged(string pluginPath)
    {
        var key = NormalizePath(pluginPath);
        var (ok, error, _) = Update(cfg =>
        {
            cfg.InPlaceAcknowledged ??= new List<string>();
            if (!cfg.InPlaceAcknowledged.Any(p => string.Equals(NormalizePath(p), key, StringComparison.Ordinal)))
                cfg.InPlaceAcknowledged.Add(key);
        });
        return (ok, error);
    }

    /// <summary>The saved import-set names, sorted, for a "did you mean" on an unknown name; empty when none are saved or the file cannot be read.</summary>
    public IReadOnlyList<string> ImportSetNames()
    {
        var sets = Load().ImportSets;
        if (sets is null) return Array.Empty<string>();
        return sets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The dirs saved under <paramref name="name"/>, or null if no such set; matched case-insensitively on the trimmed name, since the dictionary comes back from JSON with the ordinal comparer.</summary>
    public IReadOnlyList<string>? GetImportSet(string name)
    {
        var sets = Load().ImportSets;
        if (sets is null || string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim();
        foreach (var kv in sets)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value ?? new List<string>();
        return null;
    }

    /// <summary>Save or replace the import set <paramref name="name"/> through the same atomic read-modify-write as every other field; replacing removes the old key first, so a re-save under different casing leaves ONE set.</summary>
    public (bool ok, string? error) SaveImportSet(string name, IReadOnlyList<string> dirs)
    {
        var key = name.Trim();
        var (ok, error, _) = Update(cfg =>
        {
            cfg.ImportSets ??= new Dictionary<string, List<string>>();
            foreach (var existing in cfg.ImportSets.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
                cfg.ImportSets.Remove(existing);
            cfg.ImportSets[key] = dirs.ToList();
        });
        return (ok, error);
    }

    /// <summary>Canonical identity for an in-place acknowledgement: the full, lower-cased path; an un-rootable string falls back to a trimmed lower-case compare rather than throwing.</summary>
    static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).ToLowerInvariant(); }
        catch { return p.Trim().ToLowerInvariant(); }
    }

    /// <summary>The tolerant-but-LOUD read: missing yields blank, corrupt is COPIED to <c>.corrupt.bak</c> and returns blank with a note naming the backup, so a read never destroys evidence.</summary>
    UserConfig ReadOrRecover(out string? note)
    {
        note = null;
        if (!File.Exists(_path)) return new UserConfig();
        string text;
        try { text = File.ReadAllText(_path); }
        catch (Exception ex)
        {
            note = $"could not read '{_path}' ({ex.Message}) — proceeding as if nothing were saved; the file was left untouched.";
            return new UserConfig();
        }
        try { return JsonSerializer.Deserialize<UserConfig>(text) ?? new UserConfig(); }
        catch (Exception ex)
        {
            var backup = _path + ".corrupt.bak";
            try
            {
                File.Copy(_path, backup, overwrite: true);
                note = $"houseCARL.user.json was unreadable (corrupt JSON: {ex.Message}). The corrupt file was backed up to " +
                       $"'{backup}'; previously saved settings (MO2 instance / tool paths) are NOT loaded and need re-saving.";
            }
            catch (Exception bex)
            {
                note = $"houseCARL.user.json is unreadable (corrupt JSON: {ex.Message}) AND backing it up failed ({bex.Message}). " +
                       $"The corrupt file remains at '{_path}'; previously saved settings are NOT loaded.";
            }
            return new UserConfig();
        }
    }
}

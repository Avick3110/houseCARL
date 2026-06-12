using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// The on-disk user config shape (houseCARL.user.json) — the values houseCARL persists for ITSELF at runtime, separate
/// from the shipped appsettings.json. TWO independent concerns share this one file: the MO2 instance folder (written by
/// housecarl_set_mo2_instance) and the external-TOOL paths (written by housecarl_set_tool_path — the bridge for compile /
/// BSA / log access). They MUST coexist — a write of one must never clobber the other — which is why the only writer is
/// <see cref="UserConfigStore.Update"/> (read-modify-write under a lock), never a whole-object overwrite.
/// </summary>
public sealed class UserConfig
{
    /// <summary>The MO2 instance folder housecarl_set_mo2_instance saved (precedence §6d: this beats appsettings'
    /// Mo2InstanceDir). Null/absent ⇒ fall through to explicit paths / unconfigured.</summary>
    public string? Mo2InstanceDir { get; set; }

    /// <summary>External-tool paths the bridge saved, keyed by tool wire-name (papyrus_compiler, bsarch, papyrus_logs,
    /// crash_logs) → an absolute file/dir path. Null/absent until housecarl_set_tool_path is first called.</summary>
    public Dictionary<string, string>? ToolPaths { get; set; }
}

/// <summary>
/// The single OWNER of houseCARL.user.json — every read and write of that file goes through here, so the two independent
/// writers (housecarl_set_mo2_instance, housecarl_set_tool_path) can never clobber each other's field. Hardened per the
/// 2026-06-12 adversarial hunt (F3, hunter-PROVEN silent clobbers):
///   • ATOMIC — <see cref="Update"/> serializes to a sibling temp file and renames it over the target (same volume),
///     so a reader never sees a half-written file and a crash mid-write never corrupts the saved config.
///   • CROSS-PROCESS — the read-modify-write runs under a NAMED mutex derived from the file path, so two server
///     processes sharing the file (CLI plugin + desktop app) serialize instead of clobbering each other's field
///     (the old gate was process-local only).
///   • CORRUPT = LOUD — an unparseable file is BACKED UP beside itself (.corrupt.bak) and REPORTED via the returned
///     note, never silently treated as blank (the old path silently wiped every saved setting on the next Update).
/// Best-effort + HONEST: a write failure (e.g. a read-only data dir) is RETURNED, not thrown or swallowed, so the
/// calling tool can tell the user the choice won't survive a restart. One instance is registered as a singleton and
/// shared by <see cref="LoadOrderService"/> + the tool bridge.
/// </summary>
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

    /// <summary>A stable, legal mutex name for the config file: same file (case-insensitively) ⇒ same mutex in any
    /// process of this session. Local\ scope — both server hosts (CLI plugin, desktop app) run in the user's session.</summary>
    static string MutexName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return "Local\\houseCARL-user-config-" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Run <paramref name="body"/> holding BOTH locks. An abandoned mutex (the other process died holding it)
    /// counts as acquired — the file itself stays consistent because writes are atomic renames. A timeout proceeds
    /// WITHOUT the cross-process half rather than deadlocking a tool call forever; the process-local gate still holds,
    /// and the atomic rename bounds the damage to last-write-wins (never a torn file).</summary>
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

    /// <summary>Read the current config. A missing file yields a fresh blank <see cref="UserConfig"/>; a CORRUPT file is
    /// backed up beside itself and reported via <paramref name="note"/> (Q3 — never silently "nothing saved yet"), then
    /// also yields blank so a tool call still proceeds.</summary>
    public UserConfig Load(out string? note)
    {
        var (cfg, n) = WithLocks(() => { var c = ReadOrRecover(out var rn); return (c, rn); });
        note = n;
        return cfg;
    }

    /// <summary>Read the current config, discarding any recovery note — for callers that only need the values and a
    /// later <see cref="Update"/> (which re-reports) or the boot path's noted Load owns the loudness.</summary>
    public UserConfig Load() => Load(out _);

    /// <summary>Apply <paramref name="mutate"/> to the CURRENT on-disk config and write it back ATOMICALLY (temp +
    /// rename) — the ONLY way the file is written, so the two concerns merge instead of overwriting. Returns (ok, error,
    /// note): a write failure is reported in <c>error</c>, not thrown (Q3 — "works this session, won't persist"); a
    /// corrupt prior file is backed up and named in <c>note</c> even when the write itself succeeds, so a recovery is
    /// never silent. The whole read-modify-write runs under the cross-process lock.</summary>
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
                File.Move(tmp, _path, overwrite: true);   // atomic on the same volume — a reader never sees a torn file
                return (true, null, note);
            }
            catch (Exception ex) { return (false, ex.Message, note); }
        });
    }

    /// <summary>The tolerant-but-LOUD read (hunt F3): missing ⇒ blank, parseable ⇒ as saved, CORRUPT ⇒ back the file up
    /// beside itself (.corrupt.bak — kept until the user deletes it; re-copied while the corrupt file persists) and
    /// return blank with a note naming the backup and what was lost. The corrupt original is COPIED, not moved, so a
    /// read never destroys evidence; the next successful <see cref="Update"/> replaces it with a clean file.</summary>
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

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

    /// <summary>Resolved on-disk plugin paths (normalized, lower-cased full paths) the user has acknowledged for
    /// IN-PLACE editing — the PERSISTENT, cross-session first-touch handshake of the in-place write lane. A path present
    /// here means the user accepted that houseCARL writes that ORIGINAL file in place (it will no longer be untouched);
    /// it waives the CONSENT axis ONLY, never the touched-record verify (a tool-capability fact no acknowledgement can
    /// override). Null/absent until the first in-place acknowledgement. The third independent concern in this file —
    /// like the other two it is read-modify-written ONLY through <see cref="UserConfigStore.Update"/> so it can never
    /// clobber (or be clobbered by) the MO2 instance / tool paths.</summary>
    public List<string>? InPlaceAcknowledged { get; set; }

    /// <summary>Named Papyrus import-directory sets (housecarl_compile_script's <c>save_import_set=</c> /
    /// <c>import_set=</c>) — a project's dependency source folders supplied ONCE and referenced by name thereafter
    /// (issue #200's second half; the auto-scan covers frameworks that ship sources inside a mod, this covers the rest —
    /// local stubs, a dev project tree, sources extracted out of a BSA). Name → the ordered dirs. Null/absent until the
    /// first save. The fourth independent concern in this file, read-modify-written ONLY through
    /// <see cref="UserConfigStore.Update"/> so it can never clobber (or be clobbered by) the other three.</summary>
    public Dictionary<string, List<string>>? ImportSets { get; set; }
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
                AtomicFile.Commit(tmp, _path);   // crash-atomic swap (File.Replace / rename) — a reader never sees a torn or vanished file
                return (true, null, note);
            }
            catch (Exception ex) { return (false, ex.Message, note); }
        });
    }

    /// <summary>True iff <paramref name="pluginPath"/> already carries a PERSISTED in-place acknowledgement — the
    /// cross-session first-touch handshake (in-place write lane). Normalized full-path compare, so a file is identified
    /// the same way it was recorded regardless of the caller's path spelling. FAIL-SAFE (Q3): a missing / unreadable /
    /// corrupt config reads as NOT acknowledged, so the handshake re-prompts rather than silently proceeding to write a
    /// user's original. Waives the CONSENT axis only — the touched-record verify still runs.</summary>
    public bool IsInPlaceAcknowledged(string pluginPath)
    {
        var key = NormalizePath(pluginPath);
        var ack = Load().InPlaceAcknowledged;
        return ack is not null && ack.Any(p => string.Equals(NormalizePath(p), key, StringComparison.Ordinal));
    }

    /// <summary>PERSIST an in-place acknowledgement for <paramref name="pluginPath"/> (idempotent — never duplicated),
    /// through the same atomic read-modify-write as every other field so it can never clobber the MO2 instance / tool
    /// paths sharing this file. Returns (ok, error): a write failure is RETURNED, not thrown (Q3 — the caller can tell
    /// the user the edit proceeded but the acknowledgement won't survive a restart, so the next session re-prompts).</summary>
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

    /// <summary>The saved import-set names, sorted, for a "did you mean" on an unknown name (Q3 — an unknown set names
    /// what DOES exist rather than failing blank). Empty when none are saved or the file can't be read.</summary>
    public IReadOnlyList<string> ImportSetNames()
    {
        var sets = Load().ImportSets;
        if (sets is null) return Array.Empty<string>();
        return sets.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The dirs saved under <paramref name="name"/>, or null if no such set. Matched CASE-INSENSITIVELY and
    /// on the trimmed name: the dictionary comes back from JSON with the DEFAULT (ordinal) comparer, so a plain
    /// indexer lookup would miss a set the user saved as "MyProject" and recalled as "myproject".</summary>
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

    /// <summary>Save (or replace) the import set <paramref name="name"/> through the same atomic read-modify-write as
    /// every other field, so it can never clobber the MO2 instance / tool paths / in-place acknowledgements sharing
    /// this file. Replacing is case-insensitive AND removes the old key before adding the trimmed one, so re-saving
    /// "MyProject" as "myproject" leaves ONE set rather than two that <see cref="GetImportSet"/> would then resolve
    /// between arbitrarily. Returns (ok, error): a write failure is RETURNED, not thrown (Q3 — the caller can say the
    /// compile ran but the set won't survive a restart).</summary>
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

    /// <summary>Canonical identity for an in-place acknowledgement: the full, lower-cased path, so the same on-disk file
    /// matches whatever path spelling reaches the check. Best-effort — an un-rootable string falls back to a trimmed
    /// lower-case compare rather than throwing (the worst case is a redundant re-prompt, never a wrong waiver).</summary>
    static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).ToLowerInvariant(); }
        catch { return p.Trim().ToLowerInvariant(); }
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

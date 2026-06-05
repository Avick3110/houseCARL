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
/// writers (housecarl_set_mo2_instance, housecarl_set_tool_path) can never clobber each other's field. <see cref="Update"/>
/// is read-modify-write under a process-wide lock on this store; <see cref="Load"/> is a TOLERANT read (a missing or
/// corrupt file yields a blank config, never a crash — Q3). Best-effort + HONEST: a write failure (e.g. a read-only data
/// dir) is RETURNED, not thrown or swallowed, so the calling tool can tell the user the choice won't survive a restart.
/// One instance is registered as a singleton and shared by <see cref="LoadOrderService"/> + the tool bridge.
/// </summary>
public sealed class UserConfigStore
{
    readonly string _path;
    readonly object _gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public UserConfigStore(string path) => _path = path;

    /// <summary>The file this store owns (for the tool confirmation / diagnostics).</summary>
    public string FilePath => _path;

    /// <summary>Read the current config — a missing file or unparseable JSON yields a fresh blank <see cref="UserConfig"/>
    /// (Q3: a corrupt user file never crashes boot or a tool; it reads as "nothing saved yet").</summary>
    public UserConfig Load()
    {
        lock (_gate) { return ReadOrBlank(); }
    }

    /// <summary>Apply <paramref name="mutate"/> to the CURRENT on-disk config and write it back — the ONLY way the file is
    /// written, so the two concerns merge instead of overwriting. Returns (ok, error): a write failure is reported, not
    /// thrown, so the tool can surface "works this session, won't persist" (Q3). The mutate runs under the lock.</summary>
    public (bool ok, string? error) Update(Action<UserConfig> mutate)
    {
        lock (_gate)
        {
            try
            {
                var cfg = ReadOrBlank();
                mutate(cfg);
                var dir = Path.GetDirectoryName(_path);   // the data dir (${CLAUDE_PLUGIN_DATA}) may not exist on the first save
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(cfg, Json));
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
    }

    UserConfig ReadOrBlank()
    {
        if (!File.Exists(_path)) return new UserConfig();
        try { return JsonSerializer.Deserialize<UserConfig>(File.ReadAllText(_path)) ?? new UserConfig(); }
        catch { return new UserConfig(); }   // corrupt → treat as blank (Q3); a tool write then rewrites it clean
    }
}

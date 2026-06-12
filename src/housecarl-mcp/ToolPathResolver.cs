namespace HousecarlMcp;

/// <summary>
/// The runtime bridge between houseCARL's external-tool RIDERS (compile, BSA, log access) and the user-supplied paths they
/// need. Singleton. Sits on <see cref="UserConfigStore"/> (the saved paths, shared with the MO2-instance setting) and
/// <see cref="ToolBridge"/> (the catalog, validation, auto-detect, and the trained missing-dependency prompt).
///
/// Resolution order for a dependency (<see cref="Resolve"/>): (1) a path the user SAVED via housecarl_set_tool_path wins;
/// (2) else AUTO-DETECT a canonical home and, on a hit, persist it silently so we probe once; (3) else null — the caller
/// fires the forcing function (<see cref="RequireOrPrompt"/>), which returns the trained prompt so the AI reliably asks the
/// user and is handed the exact resolving call. This is Q3 pointed at external deps: a RETURNED string reaches the client,
/// where a throw would be genericized away — the same lesson the MO2 not-configured prompt proved (2026-06-02).
///
/// Step 1 builds + proves this bridge; the riders (compile, BSA) call <see cref="RequireOrPrompt"/> as they land.
/// </summary>
public sealed class ToolPathResolver
{
    readonly UserConfigStore _store;

    public ToolPathResolver(UserConfigStore store) => _store = store;

    /// <summary>The path the user SAVED for a dependency, or null if unset. Pure read (no probe, no persist) — for a status
    /// surface or a "what's configured" question.</summary>
    public string? Saved(ToolDependency dep)
    {
        var paths = _store.Load().ToolPaths;
        return paths is not null && paths.TryGetValue(ToolBridge.Info(dep).Key, out var p) ? p : null;
    }

    /// <summary>For a status / diagnostic surface (housecarl_load_order_status' log-folder section): where a dependency
    /// resolves right now — saved-and-valid → auto-detected canonical home → unset — WITHOUT persisting (unlike
    /// <see cref="Resolve"/>), so a ReadOnly status read never writes config. Thin wrapper over the pure
    /// <see cref="ToolBridge.Inspect"/>, supplying the user's saved path.</summary>
    public (string? path, ToolPathSource source) Inspect(ToolDependency dep) => ToolBridge.Inspect(dep, Saved(dep));

    /// <summary>Resolve a dependency to a usable path: saved → else auto-detect (persist on hit) → else null. A saved path
    /// that no longer validates (tool moved/uninstalled) is treated as unset, so it re-detects or re-prompts rather than
    /// failing opaquely downstream.</summary>
    public string? Resolve(ToolDependency dep)
    {
        var saved = Saved(dep);
        if (saved is not null && ToolBridge.Validate(dep, saved).ok) return saved;

        var found = ToolBridge.Probe(dep);
        if (found is not null)
        {
            // Persist the auto-detected home so we only probe once. This silent persist is the ONE path where a
            // corrupt-config recovery would otherwise vanish for good (the file is rewritten clean, so no later
            // Update re-reports it) — route the note to stderr, the same channel as the boot log (PR #51 review).
            var r = Save(dep, found);
            if (r.persistNote is not null) Console.Error.WriteLine("houseCARL user config recovered: " + r.persistNote);
        }
        return found;
    }

    /// <summary>Validate + SAVE a user-supplied path for a dependency (the housecarl_set_tool_path body). The path is
    /// trimmed of surrounding quotes and made absolute. On a validation failure NOTHING is saved (Q3) and the reason is
    /// returned; on success it's written to the shared user.json (coexisting with the MO2 instance setting).
    /// <c>persistNote</c> carries a corrupt-file recovery (hunt F3 — the prior file was backed up; other saved settings
    /// were lost), rendered even on success.</summary>
    public (bool ok, string? error, bool persisted, string? persistError, string? persistNote, string resolved) Save(ToolDependency dep, string rawPath)
    {
        var path = (rawPath ?? "").Trim().Trim('"');
        if (path.Length == 0) return (false, "no path given.", false, null, null, "");
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) { return (false, $"'{rawPath}' is not a usable path ({ex.Message}).", false, null, null, rawPath ?? ""); }

        var (ok, error) = ToolBridge.Validate(dep, path);
        if (!ok) return (false, error, false, null, null, path);

        var (persisted, persistError, persistNote) = _store.Update(c => (c.ToolPaths ??= new())[ToolBridge.Info(dep).Key] = path);
        return (true, null, persisted, persistError, persistNote, path);
    }

    /// <summary>The forcing function a rider calls: out <paramref name="path"/> is the resolved path when available and the
    /// return is null (proceed); otherwise the return is the trained prompt string for the rider to RETURN to the client
    /// (so the AI surfaces the ask + the resolving call) and path is null. Exactly one of the two is non-null.</summary>
    public string? RequireOrPrompt(ToolDependency dep, out string? path)
    {
        path = Resolve(dep);
        return path is null ? ToolBridge.RenderMissingPrompt(dep) : null;
    }
}

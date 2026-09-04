namespace HousecarlMcp;

/// <summary>Resolves the user-supplied paths to external tools (compile, BSA, log access). Sits on
/// <see cref="UserConfigStore"/> for the saved paths and <see cref="ToolBridge"/> for the catalog, validation,
/// auto-detect and the missing-dependency prompt. Resolution order: a saved path wins, else auto-detect a canonical
/// home and persist the hit so we probe once, else null and the caller returns the prompt string rather than
/// throwing — a returned string reaches the client, a throw is genericized away.</summary>
public sealed class ToolPathResolver
{
    readonly UserConfigStore _store;

    public ToolPathResolver(UserConfigStore store) => _store = store;

    /// <summary>The path the user saved for a dependency, or null if unset. Pure read: no probe, no persist.</summary>
    public string? Saved(ToolDependency dep)
    {
        var paths = _store.Load().ToolPaths;
        return paths is not null && paths.TryGetValue(ToolBridge.Info(dep).Key, out var p) ? p : null;
    }

    /// <summary>Where a dependency resolves right now — saved-and-valid, else auto-detected home, else unset —
    /// without persisting, unlike <see cref="Resolve"/>, so a read-only status surface never writes config.
    /// <paramref name="gameDirHints"/> anchor the compiler's auto-detect.</summary>
    public (string? path, ToolPathSource source) Inspect(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
        => ToolBridge.Inspect(dep, Saved(dep), gameDirHints);

    /// <summary>Resolve a dependency to a usable path: saved, else auto-detect and persist the hit, else null. A saved
    /// path that no longer validates (tool moved or uninstalled) is treated as unset so it re-detects or re-prompts
    /// rather than failing opaquely downstream. The saved path is checked first on every later call regardless of the
    /// active instance, which is what makes a detected path reusable across instances.</summary>
    public string? Resolve(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
    {
        var saved = Saved(dep);
        if (saved is not null && ToolBridge.Validate(dep, saved).ok) return saved;

        var found = ToolBridge.Probe(dep, gameDirHints);
        if (found is not null)
        {
            // Persist the auto-detected home so we only probe once. A corrupt-config recovery noticed here is
            // reported nowhere else — the file is rewritten clean, so no later Update repeats it — hence stderr.
            var r = Save(dep, found);
            if (r.persistNote is not null) Console.Error.WriteLine("houseCARL user config recovered: " + r.persistNote);
        }
        return found;
    }

    /// <summary>Validate and save a user-supplied path for a dependency. The path is trimmed of surrounding quotes and
    /// made absolute. On a validation failure nothing is saved and the reason is returned; on success it is written to
    /// the shared user.json alongside the MO2 instance setting. <c>persistNote</c> carries a corrupt-file recovery (the
    /// prior file was backed up and other saved settings were lost) and is rendered even on success.</summary>
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

    /// <summary>Exactly one of the two outputs is non-null: <paramref name="path"/> is the resolved path and the return
    /// is null (proceed), or the return is the prompt string for the caller to return to the client and path is null.
    /// <paramref name="gameDirHints"/> feed the compiler's auto-detect and the prompt's "looked here" note.</summary>
    public string? RequireOrPrompt(ToolDependency dep, out string? path, IReadOnlyList<string>? gameDirHints = null)
    {
        path = Resolve(dep, gameDirHints);
        return path is null ? ToolBridge.RenderMissingPrompt(dep, gameDirHints) : null;
    }
}

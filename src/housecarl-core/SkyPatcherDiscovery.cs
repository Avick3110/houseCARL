namespace HousecarlCore;

/// <summary>
/// SkyPatcher INI DISCOVERY (Wave 1 of the distributor subsystem; plan
/// dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md §5.1–5.2): enumerate the SkyPatcher
/// layer of the ACTIVE load order into the ordered union the overlay replays.
///
/// <para><b>Loose-only (plan §5.1).</b> SkyPatcher's DLL reads INIs off the (VFS-projected)
/// filesystem — a BSA-packed INI is invisible to it. An INI that resolves ONLY to a BSA source is
/// listed with <see cref="IniFile.NotApplied"/> naming that reason (Q3 visibility), never silently
/// dropped and never treated as applied.</para>
///
/// <para><b>Union, not filename-winner (plan §5.2).</b> Every distinct loose INI path applies. The
/// VFS winner rule only collapses two mods shipping the IDENTICAL relative path — that collision is
/// surfaced per file (<see cref="IniFile.ShadowedProviders"/>): the loser's content is NOT read by
/// the game and NOT parsed here (exactly the mod-manager collision the grammar reference warns
/// about).</para>
///
/// <para><b>Apply order.</b> Within a type folder, files sort <c>0</c>→<c>z</c> by their path
/// relative to that folder (ordinal, case-insensitive). DECLARED ASSUMPTION (Wave-1 empirical item):
/// the reference documents filename-sort for a flat folder; how the DLL orders files across NESTED
/// organisation subfolders is unverified — this sorts by full relative path, which matches flat-folder
/// filename sort exactly and gives nested files a deterministic, plausible order.</para>
///
/// <para><b>Gates.</b> The <c>Plugin.esp.ini</c> filename gate (file loads only when the named plugin
/// is active) and the <c>SkyPatcher.ini</c> <c>[Patcher]</c> per-type toggles are both evaluated and
/// carried as flags — a gated-off file keeps its parsed content but is marked
/// <see cref="IniFile.NotApplied"/>; a toggled-off folder is marked <see cref="FolderScan.PatchingEnabled"/>
/// = false (its INIs exist but the DLL skips the whole subfolder).</para>
/// </summary>
public static class SkyPatcherDiscovery
{
    /// <summary>The SkyPatcher tree root under Data (backslash, no trailing separator).</summary>
    public const string Root = "SKSE\\Plugins\\SkyPatcher";

    /// <summary>One INI in the layer. <see cref="NotApplied"/> is null when the game reads this file;
    /// otherwise it NAMES why it doesn't (BSA-only / plugin-gated off / unreadable). Parsed lines are
    /// kept either way (an inactive patch is still inspectable).</summary>
    public sealed record IniFile(
        string RelPath,
        string Subfolder,
        string SortKey,
        string? WinningProvider,
        IReadOnlyList<string> ShadowedProviders,
        string? GatePlugin,
        string? NotApplied,
        IReadOnlyList<SkyPatcherLine> Lines);

    /// <summary>One type subfolder's ordered scan. <see cref="Catalog"/> is null for a subfolder the
    /// grammar reference doesn't document (loud in <see cref="LayerScan.Notes"/>).</summary>
    public sealed record FolderScan(
        string Subfolder,
        SkyPatcherRecordCatalog? Catalog,
        bool PatchingEnabled,
        IReadOnlyList<IniFile> Files);

    /// <summary>The whole layer: per-folder ordered scans + layer-level notes + the build's Q3 caveat.</summary>
    public sealed record LayerScan(
        IReadOnlyList<FolderScan> Folders,
        IReadOnlyList<string> Notes,
        bool ReadIncomplete);

    /// <summary>Per-file INI parse cache — the same cheap-mtime freshness discipline the rest of
    /// houseCARL uses. Keyed on the winning loose file's full path; an entry is fresh while its
    /// (mtime, length) pair matches, so an edited/replaced INI re-reads on the next scan and an
    /// untouched layer costs zero re-parses per post-state call. Thread-safe (a post-state call runs
    /// outside the service gate). Entries for deleted files just stop being hit — bounded by the
    /// layer's INI count.</summary>
    public sealed class ParseCache
    {
        readonly object _lock = new();
        readonly Dictionary<string, (DateTime MtimeUtc, long Length, IReadOnlyList<SkyPatcherLine> Lines)> _byPath = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyList<SkyPatcherLine> GetOrParse(string path)
        {
            var fi = new FileInfo(path);
            var mtime = fi.LastWriteTimeUtc;
            var length = fi.Length;
            lock (_lock)
                if (_byPath.TryGetValue(path, out var hit) && hit.MtimeUtc == mtime && hit.Length == length)
                    return hit.Lines;
            // (If the file changes between the stamp and the read, the stale content is keyed under the
            //  OLD stamp and the next call's fresh stamp misses it — self-healing, never wedged.)
            var lines = SkyPatcherParse.ParseFile(File.ReadAllText(path));
            lock (_lock) _byPath[path] = (mtime, length, lines);
            return lines;
        }
    }

    /// <summary>
    /// Scan the SkyPatcher layer off one pinned asset view. <paramref name="pluginPresent"/> answers
    /// the filename gate (plugin filename incl. extension, case-insensitive). Pure read; the view is
    /// handle-free, so this can run outside the service gate like the SKSE inventory does.
    /// <paramref name="cache"/> (optional) skips the read+parse for INIs unchanged since the last scan.
    /// </summary>
    public static LayerScan Scan(AssetResolver.AssetView view, SkyPatcherCatalog catalog, Func<string, bool> pluginPresent, ParseCache? cache = null)
    {
        var notes = new List<string>();
        var byFolder = new Dictionary<string, List<IniFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in view.EnumerateUnder(Root))
        {
            if (!rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            // Subfolder = the first component under the root; deeper nesting is organisation (grammar §2).
            var underRoot = rel.Substring(Root.Length + 1);
            int slash = underRoot.IndexOf('\\');
            if (slash < 0)
            {
                notes.Add($"'{rel}' sits directly in the SkyPatcher root (no type subfolder) — the DLL reads INIs from type subfolders only; NOT applied.");
                continue;
            }
            var subfolder = underRoot[..slash];
            var sortKey = underRoot[(slash + 1)..];

            var place = view.ResolveForPlacement(rel);
            var looseSources = place.Sources.Where(s => s.Kind == AssetKind.Loose).ToList();
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;

            string? notApplied = null;
            IReadOnlyList<SkyPatcherLine> lines = Array.Empty<SkyPatcherLine>();
            var shadowed = looseSources.Skip(1).Select(s => s.ProviderName).ToList();

            if (winner is null || winner.Kind != AssetKind.Loose)
            {
                notApplied = "present only inside a BSA — SkyPatcher reads loose INIs off the filesystem only";
            }
            else
            {
                // The plugin-name filename gate: '<Plugin>.esp.ini' loads only when that plugin is active.
                var gate = GatePluginOf(rel);
                if (gate is not null && !pluginPresent(gate))
                    notApplied = $"filename-gated on plugin '{gate}', which is not in the active load order";

                try
                {
                    lines = cache is null
                        ? SkyPatcherParse.ParseFile(File.ReadAllText(winner.LooseFilePath!))
                        : cache.GetOrParse(winner.LooseFilePath!);
                }
                catch (Exception ex)
                {
                    notApplied ??= $"winning copy could not be read: {ex.Message}";
                    notes.Add($"'{rel}': could not read the winning loose copy ({ex.Message}) — its content is missing from this scan (Q3).");
                }

            }

            var file = new IniFile(rel, subfolder, sortKey, winner?.ProviderName, shadowed, GatePluginOf(rel), notApplied, lines);
            (byFolder.TryGetValue(subfolder, out var list) ? list : byFolder[subfolder] = new()).Add(file);

            if (shadowed.Count > 0)
                notes.Add($"'{rel}' is shipped by {looseSources.Count} mods — only '{winner!.ProviderName}' wins the VFS; the cop(ies) from {string.Join(", ", shadowed)} are SHADOWED and never read (the same-path collision the reference warns about — nest plugin-named INIs in a mod-specific subfolder).");
        }

        // SkyPatcher.ini [Patcher] toggles — a type folder can be switched off wholesale.
        var toggles = ReadPatcherToggles(view, notes);

        var folders = new List<FolderScan>();
        foreach (var (subfolder, files) in byFolder.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var cat = catalog.ForSubfolder(subfolder);
            if (cat is null)
                notes.Add($"subfolder '{subfolder}' is not a documented SkyPatcher record type — its {files.Count} INI(s) are listed but cannot be interpreted (not in the grammar reference; verify the folder name or the reference version).");
            bool enabled = ToggleEnabled(toggles, subfolder);
            if (!enabled)
                notes.Add($"SkyPatcher.ini disables '{subfolder}' patching (iEnable…Patching=0) — its {files.Count} INI(s) are present but the DLL skips the whole subfolder.");
            folders.Add(new FolderScan(subfolder, cat,
                enabled,
                files.OrderBy(f => f.SortKey, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new LayerScan(folders, notes, view.ReadIncomplete);
    }

    /// <summary>The plugin a filename gates on: 'Skyrim.esm.ini' → "Skyrim.esm"; 'myEdits.ini' → null.
    /// The gate is the ini-stripped basename ending in a plugin extension (grammar §2; the ONE
    /// extension list — <see cref="PluginFile.Extensions"/>).</summary>
    public static string? GatePluginOf(string relPath)
    {
        var stem = Path.GetFileNameWithoutExtension(relPath);   // strips the '.ini'
        var ext = Path.GetExtension(stem);
        return PluginFile.Extensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase)) ? stem : null;
    }

    /// <summary>The ordered, game-visible line union for one folder — what the overlay replays. Only
    /// files the game actually reads contribute (NotApplied ones are excluded by construction).</summary>
    public static IReadOnlyList<SkyPatcherOverlay.OrderedLine> OrderedLines(FolderScan folder)
    {
        var lines = new List<SkyPatcherOverlay.OrderedLine>();
        if (!folder.PatchingEnabled) return lines;
        foreach (var f in folder.Files)
        {
            if (f.NotApplied is not null) continue;
            for (int i = 0; i < f.Lines.Count; i++)
                lines.Add(new SkyPatcherOverlay.OrderedLine(f.RelPath, i + 1, f.Lines[i]));
        }
        return lines;
    }

    // ---- SkyPatcher.ini ------------------------------------------------------------------------------

    /// <summary>Read the winning loose SkyPatcher.ini's [Patcher] section into toggle→bool. Absent file
    /// (or no loose copy) ⇒ empty map = all types enabled (the DLL's default). Never throws (Q3 note).</summary>
    static Dictionary<string, bool> ReadPatcherToggles(AssetResolver.AssetView view, List<string> notes)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var place = view.ResolveForPlacement("SKSE\\Plugins\\SkyPatcher.ini");
            var winner = place.Sources.FirstOrDefault();
            if (winner is null || winner.Kind != AssetKind.Loose) return map;
            bool inPatcher = false;
            foreach (var raw in File.ReadAllLines(winner.LooseFilePath!))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    // The section is the text INSIDE the brackets — '[Patcher] ; note' must still match
                    // (review finding #9: trailing text silently re-enabled disabled folders).
                    int close = line.IndexOf(']');
                    var section = close > 0 ? line[1..close].Trim() : "";
                    inPatcher = section.Equals("Patcher", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inPatcher) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                // Inline ';' comments strip off the VALUE ('iEnableNpcPatching=0 ; off for testing' is 0,
                // not '0 ; off…' — the atoi-style read the DLL's INI layer does) — review finding #9.
                var val = line[(eq + 1)..].Split(';')[0].Trim();
                if (key.StartsWith("iEnable", StringComparison.OrdinalIgnoreCase) && key.EndsWith("Patching", StringComparison.OrdinalIgnoreCase))
                    map[key["iEnable".Length..^"Patching".Length]] = val != "0";
            }
        }
        catch (Exception ex)
        {
            notes.Add($"SkyPatcher.ini could not be read ({ex.Message}) — per-type toggles assumed default-on (Q3: if a type is disabled there, this scan over-reports).");
        }
        return map;
    }

    /// <summary>Whether a subfolder's patcher is enabled. The toggle token matches the subfolder
    /// case-insensitively for every documented type (npc→NPC, formList→Formlist, encounterzone→
    /// EncounterZone, …), so no hand-kept toggle↔folder table can drift.</summary>
    static bool ToggleEnabled(Dictionary<string, bool> toggles, string subfolder)
        => !toggles.TryGetValue(subfolder, out var on) || on;
}

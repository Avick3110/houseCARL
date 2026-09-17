namespace HousecarlCore;

/// <summary>Enumerate the active order's SkyPatcher layer into the ordered union the overlay replays; the loose-only, union, apply-order and gate contracts are in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherDiscovery
{
    /// <summary>The SkyPatcher tree root under Data (backslash, no trailing separator).</summary>
    public const string Root = "SKSE\\Plugins\\SkyPatcher";

    /// <summary>One INI in the layer; <see cref="NotApplied"/> names why the game does not read it, and <see cref="LooseFilePath"/> is the winning loose copy's path (null when there is none).</summary>
    public sealed record IniFile(
        string RelPath,
        string Subfolder,
        string SortKey,
        string? WinningProvider,
        string? LooseFilePath,
        IReadOnlyList<string> ShadowedProviders,
        string? GatePlugin,
        string? NotApplied,
        IReadOnlyList<SkyPatcherLine> Lines);

    /// <summary>One type subfolder's ordered scan; <see cref="Catalog"/> is null for an undocumented subfolder, which is loud in <see cref="LayerScan.Notes"/>.</summary>
    public sealed record FolderScan(
        string Subfolder,
        SkyPatcherRecordCatalog? Catalog,
        bool PatchingEnabled,
        IReadOnlyList<IniFile> Files);

    /// <summary>The whole layer: per-folder ordered scans, notes, whether the read was incomplete, and the whole <c>[Patcher]</c> toggle map (a folder this scan never built still needs its toggle).</summary>
    public sealed record LayerScan(
        IReadOnlyList<FolderScan> Folders,
        IReadOnlyList<string> Notes,
        bool ReadIncomplete,
        IReadOnlyDictionary<string, bool> PatcherToggles);

    /// <summary>Per-file INI parse cache on the shared <see cref="FileStamp"/> key, keyed by the winning loose file's path; thread-safe, bounded by the layer's INI count.</summary>
    public sealed class ParseCache
    {
        readonly object _lock = new();
        readonly Dictionary<string, (FileStamp Stamp, IReadOnlyList<SkyPatcherLine> Lines)> _byPath = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyList<SkyPatcherLine> GetOrParse(string path)
        {
            var stamp = FileStamp.Of(path);
            lock (_lock)
                if (_byPath.TryGetValue(path, out var hit) && hit.Stamp == stamp)
                    return hit.Lines;
            var lines = SkyPatcherParse.ParseFile(File.ReadAllText(path));
            lock (_lock) _byPath[path] = (stamp, lines);
            return lines;
        }
    }

    /// <summary>Scan the SkyPatcher layer off one pinned asset view; <paramref name="pluginPresent"/> answers the filename gate and <paramref name="cache"/> skips re-parsing unchanged INIs.</summary>
    public static LayerScan Scan(AssetResolver.AssetView view, SkyPatcherCatalog catalog, Func<string, bool> pluginPresent, ParseCache? cache = null)
    {
        var notes = new List<string>();
        var byFolder = new Dictionary<string, List<IniFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in view.EnumerateUnder(Root))
        {
            if (!rel.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;

            // Subfolder = the first component under the root; deeper nesting is organisation only.
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

            var file = new IniFile(rel, subfolder, sortKey, winner?.ProviderName,
                                   winner?.Kind == AssetKind.Loose ? winner.LooseFilePath : null,
                                   shadowed, GatePluginOf(rel), notApplied, lines);
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

        return new LayerScan(folders, notes, view.ReadIncomplete, toggles);
    }

    /// <summary>The plugin a filename gates on: 'Skyrim.esm.ini' to "Skyrim.esm", 'myEdits.ini' to null, off the shared <see cref="PluginFile.Extensions"/> list.</summary>
    public static string? GatePluginOf(string relPath)
    {
        var stem = Path.GetFileNameWithoutExtension(relPath);   // strips the '.ini'
        var ext = Path.GetExtension(stem);
        return PluginFile.Extensions.Any(e => ext.Equals(e, StringComparison.OrdinalIgnoreCase)) ? stem : null;
    }

    /// <summary>The ordered, game-visible line union for one folder — what the overlay replays; NotApplied files are excluded by construction.</summary>
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

    /// <summary>Read the winning loose SkyPatcher.ini's [Patcher] section into toggle to bool; an absent or unreadable file leaves the map empty, which means all types enabled.</summary>
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
                    // The section is the text INSIDE the brackets, so '[Patcher] ; note' still matches.
                    int close = line.IndexOf(']');
                    var section = close > 0 ? line[1..close].Trim() : "";
                    inPatcher = section.Equals("Patcher", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inPatcher) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                // An inline ';' comment strips off the VALUE, the atoi-style read the DLL's INI layer does.
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

    /// <summary>Whether a subfolder's patcher is enabled; the toggle token matches the subfolder case-insensitively for every documented type, so no hand-kept table can drift.</summary>
    public static bool ToggleEnabled(IReadOnlyDictionary<string, bool> toggles, string subfolder)
        => !toggles.TryGetValue(subfolder, out var on) || on;
}

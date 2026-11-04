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

    /// <summary>One type subfolder's ordered scan; <see cref="Catalog"/> is null for an undocumented subfolder, which is loud in <see cref="LayerScan.Notes"/>; <see cref="SwitchUnknown"/> when SkyPatcher.ini could not be read, so whether the patcher runs is not known.</summary>
    public sealed record FolderScan(
        string Subfolder,
        SkyPatcherRecordCatalog? Catalog,
        bool PatchingEnabled,
        IReadOnlyList<IniFile> Files,
        bool SwitchUnknown = false);

    /// <summary>The whole layer: per-folder scans in patcher order, notes, whether the read was incomplete, and the SkyPatcher.ini switches (a folder this scan never built still needs its switch).</summary>
    public sealed record LayerScan(
        IReadOnlyList<FolderScan> Folders,
        IReadOnlyList<string> Notes,
        bool ReadIncomplete,
        Switches PatcherSwitches);

    /// <summary>SkyPatcher.ini's <c>[Patcher]</c> keys as read; <see cref="Unreadable"/> names why the file could not be read.</summary>
    public sealed record Switches(IReadOnlyDictionary<string, bool> Keys, bool FilePresent, string? Unreadable);

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
            var name = Path.GetFileName(rel);
            if (!name.Contains(".ini", StringComparison.OrdinalIgnoreCase)) continue;

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
                notApplied = NotReadReason(name, pluginPresent);
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
                                   shadowed, GatePluginOf(name), notApplied, lines);
            (byFolder.TryGetValue(subfolder, out var list) ? list : byFolder[subfolder] = new()).Add(file);

            if (shadowed.Count > 0)
                notes.Add($"'{rel}' is shipped by {looseSources.Count} mods — only '{winner!.ProviderName}' wins the VFS; the cop(ies) from {string.Join(", ", shadowed)} are SHADOWED and never read (the same-path collision the reference warns about — nest plugin-named INIs in a mod-specific subfolder).");
        }

        var switches = ReadSwitches(view, byFolder.Count > 0 ? notes : new List<string>());

        var folders = new List<FolderScan>();
        foreach (var (subfolder, files) in byFolder.OrderBy(kv => kv.Key, FolderOrder(catalog)))
        {
            var cat = catalog.ForSubfolder(subfolder);
            if (cat is null)
                notes.Add($"subfolder '{subfolder}' is not a documented SkyPatcher record type — its {files.Count} INI(s) are listed but cannot be interpreted (not in the grammar reference; verify the folder name or the reference version).");
            bool enabled = PatcherRuns(switches, cat, subfolder);
            if (!enabled)
                notes.Add(switches.Keys.ContainsKey(subfolder)
                    ? $"SkyPatcher.ini disables '{subfolder}' patching (iEnable…Patching=0) — its {files.Count} INI(s) are present but the DLL skips the whole subfolder."
                    : $"SkyPatcher.ini has no iEnable…Patching key for '{subfolder}', which leaves that patcher off — its {files.Count} INI(s) are present but the DLL skips the whole subfolder.");
            folders.Add(new FolderScan(subfolder, cat, enabled, files.OrderBy(f => f.SortKey, FileOrder).ToList(),
                                       SwitchUnknown: switches.Unreadable is not null));
        }

        return new LayerScan(folders, notes, view.ReadIncomplete, switches);
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
                lines.Add(new SkyPatcherOverlay.OrderedLine(f.RelPath, i + 1, f.Lines[i], Listed: f.WinningProvider != SkyPatcherDraft.DraftProvider));
        }
        return lines;
    }

    // ---- the file rules, shared by the scan and the draft fold ----------------------------------------

    /// <summary>Why SkyPatcher does not read a file of this name, or null when it does: a name ending in an upper-case '.INI', a gate that is not exactly a plugin name, or a gate plugin not in the active order.</summary>
    public static string? NotReadReason(string fileName, Func<string, bool> pluginPresent)
    {
        if (fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(".ini", StringComparison.Ordinal))
            return $"the name ends in '{fileName[^4..]}', and SkyPatcher reads only a lower-case '.ini' ending";
        var stem = Stem(fileName);
        if (GatePluginOf(fileName) is { } gate)
            return pluginPresent(gate) ? null : $"filename-gated on plugin '{gate}', which is not in the active load order";
        return PluginFile.Extensions.Any(e => stem.Contains(e, StringComparison.OrdinalIgnoreCase))
            ? $"the name before '.ini' ('{stem}') names a plugin with more text after it, and SkyPatcher reads a plugin-named file only when that name is exactly the plugin's"
            : null;
    }

    /// <summary>The plugin a filename gates on — the name before '.ini' when it ends in a plugin extension ('Skyrim.esm.ini' and 'Skyrim.esm.ini.bak' to "Skyrim.esm"); null when ungated.</summary>
    public static string? GatePluginOf(string relPath)
    {
        var stem = Stem(Path.GetFileName(relPath));
        return PluginFile.Extensions.Any(e => stem.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? stem : null;
    }

    /// <summary>The name before the first '.ini'.</summary>
    static string Stem(string fileName)
    {
        int at = fileName.IndexOf(".ini", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? fileName : fileName[..at];
    }

    /// <summary>The read order inside one type folder, over folder-relative paths: the folder's own files, then each depth of subfolders, A to Z case-insensitive by path component within a level.</summary>
    public static readonly IComparer<string> FileOrder = Comparer<string>.Create((a, b) =>
    {
        var pa = a.Split('\\');
        var pb = b.Split('\\');
        if (pa.Length != pb.Length) return pa.Length.CompareTo(pb.Length);
        for (int i = 0; i < pa.Length; i++)
        {
            int c = string.Compare(pa[i], pb[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return 0;
    });

    /// <summary>The patcher order the DLL runs folders in, from the catalog; undocumented folders after, A to Z.</summary>
    public static IComparer<string> FolderOrder(SkyPatcherCatalog catalog) => Comparer<string>.Create((a, b) =>
    {
        int ia = Index(a), ib = Index(b);
        return ia != ib ? ia.CompareTo(ib) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        int Index(string s)
        {
            for (int i = 0; i < catalog.PatcherOrder.Count; i++)
                if (catalog.PatcherOrder[i].Equals(s, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
    });

    /// <summary>Whether a subfolder's patcher runs: its <c>iEnable…Patching</c> key when present (the token matches the subfolder case-insensitively), otherwise only a patcher that runs without its switch (Light).</summary>
    public static bool PatcherRuns(Switches switches, SkyPatcherRecordCatalog? catalog, string subfolder)
        => switches.Keys.TryGetValue(subfolder, out var on) ? on : catalog?.EnabledWithoutSwitch == true;

    /// <summary>Read the winning loose SkyPatcher.ini's [Patcher] section; no loose copy means every key is missing, and an unreadable one is named, never assumed either way.</summary>
    static Switches ReadSwitches(AssetResolver.AssetView view, List<string> notes)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var place = view.ResolveForPlacement("SKSE\\Plugins\\SkyPatcher.ini");
            var winner = place.Sources.FirstOrDefault();
            if (winner is null || winner.Kind != AssetKind.Loose)
            {
                notes.Add($"no loose SkyPatcher.ini in this order{(winner is null ? "" : " (only inside a BSA)")} — without its keys every patcher but Light is off.");
                return new Switches(map, false, null);
            }
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
                // An inline ';' comment strips off the VALUE; the value reads as the leading integer, 0 when there is none (atoi).
                var val = line[(eq + 1)..].Split(';')[0].Trim();
                if (key.StartsWith("iEnable", StringComparison.OrdinalIgnoreCase) && key.EndsWith("Patching", StringComparison.OrdinalIgnoreCase))
                    map[key["iEnable".Length..^"Patching".Length]] = LeadingInt(val) != 0;
            }
            return new Switches(map, true, null);
        }
        catch (Exception ex)
        {
            notes.Add($"SkyPatcher.ini could not be read ({ex.Message}) — whether each patcher runs is UNKNOWN; every folder is marked so, and its lines are replayed as if its patcher runs.");
            return new Switches(map, true, ex.Message);
        }
    }

    /// <summary>The leading integer of a value, as C's atoi reads it: optional sign, then digits; 0 when there are none.</summary>
    static long LeadingInt(string s)
    {
        int i = 0;
        bool neg = i < s.Length && (s[i] == '-' || s[i] == '+') && s[i++] == '-';
        long n = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]) && n < int.MaxValue) n = n * 10 + (s[i++] - '0');
        return neg ? -n : n;
    }
}

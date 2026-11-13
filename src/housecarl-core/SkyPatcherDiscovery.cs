namespace HousecarlCore;

/// <summary>Enumerate the active order's SkyPatcher layer into the ordered union the overlay replays; the loose-only, union, apply-order and gate contracts are in docs/architecture/skypatcher-layer.md.</summary>
public static class SkyPatcherDiscovery
{
    /// <summary>SkyPatcher's row of the runtime-format source, which every file rule here reads.</summary>
    static SkyPatcherFormat Format => RuntimeFormats.SkyPatcher;

    /// <summary>The SkyPatcher tree root under Data (backslash, no trailing separator).</summary>
    public static string Root => Format.Root;

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

    /// <summary>SkyPatcher.ini's <c>[Patcher]</c> keys as read; <see cref="Unreadable"/> names why the file could not be read, and <see cref="Loose"/> is false when the order has no loose copy.</summary>
    public sealed record Switches(IReadOnlyDictionary<string, bool> Keys, string? Unreadable, bool Loose = true);

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

    /// <summary>Scan the SkyPatcher layer off one pinned asset view; <paramref name="pluginPresent"/> answers the filename gate, <paramref name="cache"/> skips re-parsing unchanged INIs, and <paramref name="format"/> replaces the shipped row (uncached).</summary>
    public static LayerScan Scan(AssetResolver.AssetView view, SkyPatcherCatalog catalog, Func<string, bool> pluginPresent, ParseCache? cache = null, SkyPatcherFormat? format = null)
    {
        var f = format ?? Format;
        var notes = new List<string>();
        var byFolder = new Dictionary<string, List<IniFile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var rel in view.EnumerateUnder(f.Root))
        {
            var name = Path.GetFileName(rel);
            if (!HasIniName(name, f)) continue;

            // Subfolder = the first component under the root; deeper nesting is organisation only.
            var underRoot = rel.Substring(f.Root.Length + 1);
            int slash = underRoot.IndexOf('\\');
            if (slash < 0)
            {
                notes.Add($"'{rel}' sits directly in the SkyPatcher root (no type subfolder) — the DLL reads INIs from type subfolders only; NOT applied.");
                continue;
            }
            var subfolder = underRoot[..slash];
            var sortKey = underRoot[(slash + 1)..];

            var w = LayerFileWinner.Of(view, rel, RuntimeTargets.SkyPatcherName);
            var notApplied = w.NotApplied;
            IReadOnlyList<SkyPatcherLine> lines = Array.Empty<SkyPatcherLine>();

            if (w.LooseFilePath is { } loosePath)
            {
                notApplied = NotReadReason(name, pluginPresent, f);
                try
                {
                    lines = cache is null || format is not null
                        ? SkyPatcherParse.ParseFile(File.ReadAllText(loosePath), f.Grammar)
                        : cache.GetOrParse(loosePath);
                }
                catch (Exception ex)
                {
                    notApplied ??= $"winning copy could not be read: {ex.Message}";
                    notes.Add($"'{rel}': could not read the winning loose copy ({ex.Message}) — its content is missing from this scan (Q3).");
                }
            }

            var file = new IniFile(rel, subfolder, sortKey, w.Provider, w.LooseFilePath,
                                   w.Shadowed, GatePluginOf(name, f), notApplied, lines);
            (byFolder.TryGetValue(subfolder, out var list) ? list : byFolder[subfolder] = new()).Add(file);

            if (w.Shadowed.Count > 0)
                notes.Add($"'{rel}' is shipped by {w.Shadowed.Count + 1} mods — only '{w.Provider}' wins the VFS; the cop(ies) from {string.Join(", ", w.Shadowed)} are SHADOWED and never read (the same-path collision the reference warns about — nest plugin-named INIs in a mod-specific subfolder).");
        }

        var switches = ReadSwitches(view, notes, noteMissing: byFolder.Count > 0, f);

        var folders = new List<FolderScan>();
        foreach (var (subfolder, files) in byFolder.OrderBy(kv => kv.Key, FolderOrder(f)))
        {
            var cat = catalog.ForSubfolder(subfolder);
            if (cat is null)
                notes.Add($"subfolder '{subfolder}' is not a documented SkyPatcher record type — its {files.Count} INI(s) are listed but cannot be interpreted (not in the grammar reference; verify the folder name or the reference version).");
            bool enabled = PatcherRuns(switches, subfolder, f);
            if (!enabled)
                notes.Add($"{SwitchesName(f)} {SwitchOffReason(switches, subfolder, f)} — its {files.Count} INI(s) are present but the DLL skips the whole subfolder.");
            folders.Add(new FolderScan(subfolder, cat, enabled, files.OrderBy(x => x.SortKey, FileOrder).ToList(),
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

    /// <summary>Whether a file name is one SkyPatcher looks at: the name token ('.ini') anywhere in it.</summary>
    public static bool HasIniName(string fileName, SkyPatcherFormat? format = null)
        => fileName.Contains((format ?? Format).NameToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why SkyPatcher does not read a file of this name, or null when it does: a name ending in '.ini' spelt in any case but lower ('.INI', '.Ini'), a gate that is not exactly a plugin name, or a gate plugin not in the active order.</summary>
    public static string? NotReadReason(string fileName, Func<string, bool> pluginPresent, SkyPatcherFormat? format = null)
    {
        var f = format ?? Format;
        var token = f.NameToken;
        if (fileName.EndsWith(token, StringComparison.OrdinalIgnoreCase) && !fileName.EndsWith(token, StringComparison.Ordinal))
            return $"the name ends in '{fileName[^token.Length..]}', and SkyPatcher reads only a lower-case '{token}' ending";
        var stem = Stem(fileName, f);
        if (GatePluginOf(fileName, f) is { } gate)
            return pluginPresent(gate) ? null : $"filename-gated on plugin '{gate}', which is not in the active load order";
        return f.GateExtensions.Any(e => stem.Contains(e, StringComparison.OrdinalIgnoreCase))
            ? $"the name before '{token}' ('{stem}') names a plugin with more text after it, and SkyPatcher reads a plugin-named file only when that name is exactly the plugin's"
            : null;
    }

    /// <summary>The plugin a filename gates on — the name before '.ini' when it ends in a plugin extension ('Skyrim.esm.ini' and 'Skyrim.esm.ini.bak' to "Skyrim.esm"); null when ungated.</summary>
    public static string? GatePluginOf(string relPath, SkyPatcherFormat? format = null)
    {
        var f = format ?? Format;
        var stem = Stem(Path.GetFileName(relPath), f);
        return f.GateExtensions.Any(e => stem.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? stem : null;
    }

    /// <summary>The name before the first name token ('.ini').</summary>
    static string Stem(string fileName, SkyPatcherFormat f)
    {
        int at = fileName.IndexOf(f.NameToken, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>The patcher order the DLL runs folders in, from the source's passes; undocumented folders after, A to Z.</summary>
    public static IComparer<string> FolderOrder(SkyPatcherFormat? format = null)
    {
        var passes = (format ?? Format).Passes;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < passes.Count; i++) index.TryAdd(passes[i].Folder, i);
        int Index(string s) => index.TryGetValue(s, out var i) ? i : int.MaxValue;
        return Comparer<string>.Create((a, b) =>
        {
            int ia = Index(a), ib = Index(b);
            return ia != ib ? ia.CompareTo(ib) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Whether a subfolder's patcher runs: replayed as running when SkyPatcher.ini could not be read; else its <c>iEnable…Patching</c> key when present (the token matches the subfolder case-insensitively), otherwise only a patcher that runs without its switch (Light).</summary>
    public static bool PatcherRuns(Switches switches, string subfolder, SkyPatcherFormat? format = null)
        => switches.Unreadable is not null
           || (switches.Keys.TryGetValue(subfolder, out var on) ? on
               : (format ?? Format).RunsWithoutKey(subfolder));

    /// <summary>The switches file's name as the answers spell it ('SkyPatcher.ini').</summary>
    public static string SwitchesName(SkyPatcherFormat? format = null) => Path.GetFileName((format ?? Format).SwitchesFile);

    /// <summary>The clause naming why a patcher that does not run is off: its switch at 0, no switch in the file, or no loose file.</summary>
    public static string SwitchOffReason(Switches switches, string subfolder, SkyPatcherFormat? format = null)
    {
        var f = format ?? Format;
        var key = $"{f.SwitchKeyPrefix}…{f.SwitchKeySuffix}";
        return switches.Keys.ContainsKey(subfolder) ? $"disables '{subfolder}' patching ({key}=0)"
            : !switches.Loose ? $"is not loose in this order, which leaves '{subfolder}' patching off"
            : $"has no {key} key for '{subfolder}', which leaves that patcher off";
    }

    /// <summary>Read the winning loose SkyPatcher.ini's [Patcher] section; no loose copy means every key is missing, and an unreadable one is named, and <see cref="PatcherRuns"/> replays every patcher as running.</summary>
    static Switches ReadSwitches(AssetResolver.AssetView view, List<string> notes, bool noteMissing, SkyPatcherFormat f)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var place = view.ResolveForPlacement(f.SwitchesFile);
            var winner = place.Sources.FirstOrDefault();
            if (winner is null || winner.Kind != AssetKind.Loose)
            {
                if (noteMissing)
                {
                    var on = string.Join(", ", f.Passes.Where(p => f.RunsWithoutKey(p.Folder)).Select(p => p.Name));
                    notes.Add($"no loose {SwitchesName(f)} in this order{(winner is null ? "" : " (only inside a BSA)")} — without its keys every patcher but {on} is off.");
                }
                return new Switches(map, null, Loose: false);
            }
            // The value reads as the leading integer, 0 when there is none (atoi).
            foreach (var (section, key, val) in IniEntries.Read(winner.LooseFilePath!))
                if (section.Equals(f.SwitchesSection, StringComparison.OrdinalIgnoreCase)
                    && key.StartsWith(f.SwitchKeyPrefix, StringComparison.OrdinalIgnoreCase) && key.EndsWith(f.SwitchKeySuffix, StringComparison.OrdinalIgnoreCase))
                    map[key[f.SwitchKeyPrefix.Length..^f.SwitchKeySuffix.Length]] = LeadingInt(val) != 0;
            return new Switches(map, null);
        }
        catch (Exception ex)
        {
            notes.Add($"{SwitchesName(f)} could not be read ({ex.Message}) — whether each patcher runs is UNKNOWN; every folder is marked so, and its lines are replayed as if its patcher runs.");
            return new Switches(map, ex.Message);
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

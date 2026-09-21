namespace HousecarlCore;

/// <summary>One discovered Papyrus source folder: the loose root that provided it, the folder, the layout matched.</summary>
public sealed record PapyrusSourceRoot(string Provider, string Dir, string Layout);

/// <summary>Finds the Papyrus SOURCE folders an MO2 modlist contains; contracts in docs/architecture/papyrus.md.</summary>
public static class PapyrusSourceRoots
{
    /// <summary>The recognised on-disk source layouts, relative to a loose root, in preference order.</summary>
    public static readonly string[] Layouts = { @"Source\Scripts", @"Scripts\Source" };

    /// <summary>Every folder holding Papyrus sources, walked and emitted in the GIVEN order.</summary>
    public static IReadOnlyList<PapyrusSourceRoot> Discover(IReadOnlyList<(string Name, string Dir)> looseRoots)
    {
        var found = new List<PapyrusSourceRoot>();
        if (looseRoots is null) return found;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, dir) in looseRoots)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var layout in Layouts)
            {
                string candidate;
                try { candidate = Path.GetFullPath(Path.Combine(dir, layout)); }
                catch { continue; }                       // an un-rootable root (bad chars) costs one candidate, not the scan
                if (!HasSources(candidate)) continue;
                if (!seen.Add(candidate)) continue;       // same folder reached twice → the FIRST (higher-precedence) slot wins
                found.Add(new PapyrusSourceRoot(name, candidate, layout));
            }
        }
        return found;
    }

    /// <summary>Take the GAME's vanilla sources out of a <see cref="Discover"/> result, returning the mod candidates plus the folder removed (null if none).</summary>
    public static (IReadOnlyList<PapyrusSourceRoot> Mods, string? GameDataSources) SplitGameData(
        IReadOnlyList<PapyrusSourceRoot> found, string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return (found, null);
        var vanilla = new List<string>();
        foreach (var layout in Layouts)
        {
            try { vanilla.Add(Path.GetFullPath(Path.Combine(dataDir, layout))); }
            catch { /* an un-rootable data dir splits nothing — the same best-effort posture as the scan */ }
        }
        var set = new HashSet<string>(vanilla, StringComparer.OrdinalIgnoreCase);
        var mods = found.Where(r => !set.Contains(r.Dir)).ToList();
        // The layouts' own order (SE before LE), not discovery order, so a data dir carrying both answers stably.
        var gameData = vanilla.FirstOrDefault(v => found.Any(r => r.Dir.Equals(v, StringComparison.OrdinalIgnoreCase)));
        return (mods, gameData);
    }

    /// <summary>True iff <paramref name="dir"/> holds a top-level <c>.psc</c> (pinned by <c>compile-ergonomics-guard</c> part G); any I/O failure reads as none.</summary>
    public static bool HasSources(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var f in Directory.EnumerateFiles(dir, "*.psc"))
                if (f.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }
}

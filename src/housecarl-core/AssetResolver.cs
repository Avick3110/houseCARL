using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

// ======================================================================
//  AssetResolver — VFS-aware "which source provides this asset, and which copy WINS"
//  (facegen-diagnostics step 1; Aaron-locked 2026-06-14).
//
//  Resolves a Data-relative asset path through the SAME MO2 priority model Mo2LoadOrder
//  already uses for plugins — overwrite > enabled mods (highest priority first) > Data —
//  now generalized from "a top-level plugin filename" to an ARBITRARY relative path, AND
//  extended across active-plugin BSAs. General-purpose; the dark-face skill is the first
//  consumer (it computes a facegen path from a FormKey and asks "does the right copy win").
//
//  PRECEDENCE (the dark-face bug is literally a desync of THIS with plugin load order):
//    • loose files BEAT BSA-packed — the engine loads BSAs first, loose files override them;
//    • among loose: overwrite > higher-priority mod > Data (first sighting wins — MO2's rule,
//      identical to Mo2LoadOrder.BuildFilenameMap);
//    • among BSAs: the higher plugin-load-order rank wins.
//  WINNER = the top loose provider if ANY loose copy exists, else the top BSA provider.
//
//  Q3 — ALL providers are returned, not just the winner, plus an Ambiguous flag. Two mods
//  shipping the same facegen path IS the bug, so surfacing the contenders is the diagnostic
//  value; and the precise loose-vs-BSA outcome has real MO2 edge cases (managed archives),
//  so the resolver models the common rule and FLAGS contention rather than asserting a
//  falsely-precise single winner. A BSA that can't be read is collected into BsaFailures and
//  surfaced, never silently treated as "absent".
//
//  CORNERSTONE (#3, Aaron 2026-06-14 — "I also think #3 is fine"): holds DERIVED DATA only
//  (cached BSA file-tables as string sets) and ZERO archive handles at rest. Each BSA is
//  opened, its table copied into a HashSet, and the reader DISPOSED immediately — the exact
//  LoadOrderResolver contract (read → extract → dispose), so MO2/xEdit can still move/delete
//  archives freely. Loose presence is LIVE File.Exists (caches nothing, always current).
//  mtime-invalidated via RefreshIfStale; no live MO2 tracking, no daemon.
//
//  BSA reading uses Mutagen's NATIVE archive surface (Mutagen.Bethesda.Archives), in-process,
//  with NO BSArch dependency (BsaArchive.cs shells BSArch for pack/unpack — which Mutagen
//  cannot do — but LISTING a table it can). Decision #1 (2026-06-14): native, spike-verified
//  by the asset-resolver-guard probe.
//
//  ORDER IS INJECTED. Build takes the roots + the enabled-mod priority list + the active
//  archives already resolved (path + plugin rank) — the service computes those from the same
//  MO2 profile read it already does; correctness of the BSA winner is only as correct as the
//  injected plugin ranks (the §8.5 active-order gate, not this class).
// ======================================================================

/// <summary>How a provider supplies an asset.</summary>
public enum AssetKind { Loose, Bsa }

/// <summary>One source that provides an asset. <paramref name="Source"/> is the mod folder name, "overwrite",
/// "Data", or (for a BSA) the archive's filename.</summary>
public sealed record AssetProvider(string Source, AssetKind Kind);

/// <summary>The resolution of one asset path. <see cref="Winner"/> is null iff <see cref="Exists"/> is false.
/// <see cref="Providers"/> lists every source that has the asset, winner FIRST (then the rest in precedence order).
/// <see cref="Ambiguous"/> = more than one source provides it (contention — for facegen, the desync signal) OR a
/// loose copy coexists with a BSA copy (the one loose-vs-BSA edge the model can't promise exactly).</summary>
public sealed record AssetHit(string RelPath, bool Exists, AssetProvider? Winner, IReadOnlyList<AssetProvider> Providers, bool Ambiguous);

/// <summary>An active BSA the resolver should consider: its full path, the plugin it loads with, and that plugin's
/// load-order rank (higher = later in load order = wins among BSAs). The service derives these; the probe injects them.</summary>
public sealed record ActiveArchive(string Path, string OwningPlugin, int PluginRank);

public sealed class AssetResolver : IDisposable
{
    readonly string _overwriteDir;                       // MO2 overwrite layer (top loose source); "" if none
    readonly string _modsDir;                            // base\mods
    readonly string _dataDir;                            // game Data (lowest loose source)
    readonly IReadOnlyList<string> _enabledMods;         // mod folder names, HIGHEST priority FIRST (Mo2Composition.EnabledMods order)
    readonly IReadOnlyList<ActiveArchive> _archives;     // active BSAs (any order; winner decided by PluginRank)

    /// <summary>One table-build's whole output, swapped in as a single reference write (the LoadOrderResolver
    /// snapshot discipline) so a concurrent Resolve never sees a half-rebuilt cache. Holds string sets only.</summary>
    sealed class Snapshot
    {
        public readonly Dictionary<string, HashSet<string>> Tables;   // archive path → its file paths (normalized)
        public readonly Dictionary<string, DateTime> Mtimes;          // archive path → mtime at this build (freshness baseline)
        public readonly List<string> Failures;                        // archives that couldn't be read, with the reason (Q3)
        public Snapshot(Dictionary<string, HashSet<string>> tables, Dictionary<string, DateTime> mtimes, List<string> failures)
        { Tables = tables; Mtimes = mtimes; Failures = failures; }
    }

    volatile Snapshot _snap;

    /// <summary>Archives that could not be read this build (path: reason) — surfaced, never silently treated as empty (Q3).</summary>
    public IReadOnlyList<string> BsaFailures => _snap.Failures;

    AssetResolver(string overwriteDir, string modsDir, string dataDir,
                  IReadOnlyList<string> enabledMods, IReadOnlyList<ActiveArchive> archives)
    {
        _overwriteDir = overwriteDir ?? "";
        _modsDir = modsDir;
        _dataDir = dataDir ?? "";
        _enabledMods = enabledMods;
        _archives = DedupeArchives(archives);    // collapse a path bound by >1 plugin → ONE provider (no double-count → no false Ambiguous)
        _snap = BuildTables();
    }

    /// <summary>Collapse the injected archives to ONE entry per distinct path (OrdinalIgnoreCase). The same .bsa can
    /// be injected under more than one plugin binding; without this both <see cref="BuildTables"/> and <see cref="Resolve"/>
    /// would see it twice — Resolve would then list it twice in <see cref="AssetHit.Providers"/> and raise a FALSE
    /// <see cref="AssetHit.Ambiguous"/> (the contention signal the dark-face skill keys on). Keep the HIGHEST plugin rank
    /// (the later binding wins among BSAs) and its owning plugin; preserve first-seen order of distinct paths for a stable
    /// Providers list.</summary>
    static IReadOnlyList<ActiveArchive> DedupeArchives(IReadOnlyList<ActiveArchive> archives)
    {
        var byPath = new Dictionary<string, ActiveArchive>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var a in archives)
        {
            if (byPath.TryGetValue(a.Path, out var prev))
            {
                if (a.PluginRank > prev.PluginRank) byPath[a.Path] = a;   // keep the max-rank binding + its owning plugin
            }
            else { byPath[a.Path] = a; order.Add(a.Path); }
        }
        return order.Select(p => byPath[p]).ToList();
    }

    /// <summary>Build a resolver over the given roots, enabled-mod priority list (highest first), and active archives.
    /// Reads each archive's table ONCE (native Mutagen) and holds only the resulting string sets — zero handles at rest.</summary>
    public static AssetResolver Build(string overwriteDir, string modsDir, string dataDir,
                                      IReadOnlyList<string> enabledModsByPriority, IReadOnlyList<ActiveArchive> activeArchives)
        => new(overwriteDir, modsDir, dataDir, enabledModsByPriority, activeArchives);

    /// <summary>Open each active archive, copy its file table into a string set, and DISPOSE it immediately (Option B:
    /// no handle survives the build). An archive that won't read is recorded in Failures (Q3) and contributes nothing.</summary>
    Snapshot BuildTables()
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mtimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var a in _archives)                              // _archives is already path-deduped (DedupeArchives) — read each once
        {
            mtimes[a.Path] = SafeMtime(a.Path);
            try { tables[a.Path] = ReadArchiveTable(a.Path); }
            catch (Exception ex)
            {
                tables[a.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // empty = contributes nothing
                failures.Add($"{Path.GetFileName(a.Path)} (loaded by {a.OwningPlugin}): could not read the archive table — {Concise(ex)}");
            }
        }
        return new Snapshot(tables, mtimes, failures);
    }

    /// <summary>Read one BSA's file table with Mutagen's native reader, then DISPOSE the reader so no handle is held
    /// (the cornerstone). Paths are normalized (backslash, no leading slash) for OrdinalIgnoreCase matching.</summary>
    static HashSet<string> ReadArchiveTable(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                set.Add(Normalize(file.Path));
        }
        finally { (reader as IDisposable)?.Dispose(); }           // release the archive handle immediately (zero at rest)
        return set;
    }

    /// <summary>Resolve one Data-relative asset path: where it lives and which copy wins. See <see cref="AssetHit"/>.</summary>
    public AssetHit Resolve(string relPath)
    {
        var rel = Normalize(relPath);
        var snap = _snap;                                         // capture ONE snapshot for this call (consistent view)
        var loose = new List<AssetProvider>();

        // ---- loose, in MO2 precedence order (overwrite > mods by priority > Data) ----
        if (_overwriteDir.Length > 0 && FileExists(_overwriteDir, rel))
            loose.Add(new AssetProvider("overwrite", AssetKind.Loose));
        foreach (var mod in _enabledMods)
            if (FileExists(System.IO.Path.Combine(_modsDir, mod), rel))
                loose.Add(new AssetProvider(mod, AssetKind.Loose));
        if (_dataDir.Length > 0 && FileExists(_dataDir, rel))
            loose.Add(new AssetProvider("Data", AssetKind.Loose));

        // ---- BSA, highest plugin rank first ----
        var bsa = new List<(AssetProvider provider, int rank)>();
        foreach (var a in _archives)
            if (snap.Tables.TryGetValue(a.Path, out var t) && t.Contains(rel))
                bsa.Add((new AssetProvider(Path.GetFileName(a.Path), AssetKind.Bsa), a.PluginRank));
        // Higher plugin rank wins; the archive filename is a DETERMINISTIC tie-break so equal-rank BSAs (a plugin can
        // ship more than one) order stably across runs rather than by hash/enumeration order.
        var bsaOrdered = bsa.OrderByDescending(b => b.rank)
                            .ThenBy(b => b.provider.Source, StringComparer.OrdinalIgnoreCase)
                            .Select(b => b.provider).ToList();

        // ---- winner: loose beats BSA; providers = winner first, then the rest in precedence ----
        var providers = new List<AssetProvider>(loose);
        providers.AddRange(bsaOrdered);
        if (providers.Count == 0)
            return new AssetHit(rel, false, null, providers, false);

        var winner = providers[0];                                 // loose[0] if any loose, else bsaOrdered[0]
        // Ambiguous when >1 source provides it (contention), or a loose copy coexists with a BSA copy (the edge the
        // common-rule model can't promise exactly under MO2 managed archives).
        bool ambiguous = providers.Count > 1;
        return new AssetHit(rel, true, winner, providers, ambiguous);
    }

    /// <summary>Resolve many paths in one call (the facegen bulk scan). Each is independent; holds nothing past the return.</summary>
    public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
        => relPaths.Select(Resolve).ToList();

    /// <summary>Re-stat the active archives; if any changed (a BSA's bytes changed), rebuild the tables and return true.
    /// The cheap no-change path is just the stat sweep. A changed archive SET (active plugins added/removed) is an order
    /// change — the service rebuilds the whole resolver, like it does for LoadOrderResolver. Loose presence is live, so
    /// it needs no refresh. One reference swap; an in-flight Resolve keeps its captured snapshot.</summary>
    public bool RefreshIfStale()
    {
        var snap = _snap;
        bool stale = false;
        foreach (var a in _archives)
            if (!snap.Mtimes.TryGetValue(a.Path, out var m) || SafeMtime(a.Path) != m) { stale = true; break; }
        if (!stale) return false;
        _snap = BuildTables();
        return true;
    }

    static bool FileExists(string root, string rel)
    {
        try { return File.Exists(System.IO.Path.Combine(root, rel)); } catch { return false; }
    }

    /// <summary>Normalize an asset path for matching: forward slashes → backslashes, drop a leading separator. Matching is
    /// OrdinalIgnoreCase (Windows + BSA tables are case-insensitive), so case is left as-is and compared case-insensitively.</summary>
    static string Normalize(string p) => (p ?? "").Replace('/', '\\').TrimStart('\\');

    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    /// <summary>Holds no handles at rest (only the string-set snapshot) — Dispose is a no-op, kept so call sites can
    /// treat the resolver as a disposable resource the service builds and swaps over its lifetime.</summary>
    public void Dispose() { }
}

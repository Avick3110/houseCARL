using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

// AssetResolver — which source provides a Data-relative asset and which copy wins, through MO2's own precedence
// extended across active-plugin BSAs; precedence, injected order and the snapshot in docs/architecture/assets.md.
// Zero archive handles at rest: pinned by the asset-resolver-guard probe's at-rest arm.

public enum AssetKind { Loose, Bsa }

/// <summary>One source that provides an asset: the mod folder name, "overwrite", "Data", or a BSA's filename, plus
/// the MO2 layer a BSA's archive file lives in (null for a loose source, whose name already IS that layer).</summary>
public sealed record AssetProvider(string Source, AssetKind Kind, string? OwningMod = null);

/// <summary>One asset path's resolution: the winner (null iff not <see cref="Exists"/>), every provider winner-first,
/// and <see cref="Ambiguous"/> — contention to verify, never a confirmed problem (docs/architecture/assets.md).</summary>
public sealed record AssetHit(string RelPath, bool Exists, AssetProvider? Winner, IReadOnlyList<AssetProvider> Providers, bool Ambiguous);

/// <summary>A concrete on-disk source one provider supplies an asset from: a loose file path, or a .bsa path plus the
/// entry inside it. <see cref="OffOrder"/> marks a copy the game is not loading and <see cref="OwnerEnabled"/> says
/// which of the two reasons it is; <see cref="OwningMod"/> is a BSA archive's own MO2 layer.</summary>
public sealed record PlacementSource(string ProviderName, AssetKind Kind, string? LooseFilePath, string? ArchivePath,
                                     string EntryPath, bool OffOrder = false, string? OwningMod = null,
                                     bool OwnerEnabled = false);

public sealed record PlacementResolution(string RelPath, IReadOnlyList<PlacementSource> Sources, bool Ambiguous, bool ReadIncomplete);

/// <summary>An active BSA to consider: its path, the plugin it loads with, that plugin's rank (higher wins among
/// BSAs), and the MO2 layer the archive file lives in. The service derives these.</summary>
public sealed record ActiveArchive(string Path, string OwningPlugin, int PluginRank, string? OwningMod = null);

public sealed class AssetResolver : IDisposable
{
    readonly string _overwriteDir;                       // MO2 overwrite layer (top loose source); "" if none
    readonly string _modsDir;                            // base\mods
    readonly string _dataDir;                            // game Data (lowest loose source)
    readonly IReadOnlyList<string> _enabledMods;         // mod folder names, HIGHEST priority FIRST (Mo2Composition.EnabledMods order)
    readonly IReadOnlyList<ActiveArchive> _archives;     // active BSAs (path-deduped; winner decided by PluginRank)
    readonly IReadOnlyList<(string Name, string Dir)> _looseRoots;   // loose roots in PRECEDENCE order: overwrite > mods (priority) > Data

    /// <summary>One build's whole output, swapped in as one reference write so a concurrent Resolve never sees a half-rebuilt cache.</summary>
    internal sealed class Snapshot
    {
        public readonly Dictionary<string, HashSet<string>> Tables;   // archive path → its file paths (normalized)
        public readonly Dictionary<string, FileStamp> Stamps;         // archive path → freshness stamp at this build
        public readonly List<string> Failures;                        // archives that couldn't be read, with the reason
        public readonly ConcurrentDictionary<string, LooseSubtree> LooseCache;
        public Snapshot(Dictionary<string, HashSet<string>> tables, Dictionary<string, FileStamp> stamps, List<string> failures)
        { Tables = tables; Stamps = stamps; Failures = failures; LooseCache = new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>One subtree directory's loose resolution: the filename sets of the roots that have it, in precedence
    /// order, plus every root's stamp for that dir so a content change and an appear/disappear are both detectable.</summary>
    internal sealed class LooseSubtree
    {
        public readonly FileStamp[] DirStamps;                        // parallel to _looseRoots (length == root count)
        public readonly (int RootIndex, HashSet<string> Files)[] Present;   // roots that have the dir + ≥1 file, precedence order
        public LooseSubtree(FileStamp[] dirStamps, (int, HashSet<string>)[] present) { DirStamps = dirStamps; Present = present; }
    }

    volatile Snapshot _snap;

    /// <summary>The loose roots this resolver was built over, in precedence order. Fixed for its lifetime, so unlike
    /// the archive tables it needs no snapshot; exposed for consumers that need the VFS ORDER itself.</summary>
    public IReadOnlyList<(string Name, string Dir)> LooseRoots => _looseRoots;

    public IReadOnlyList<string> BsaFailures => _snap.Failures;

    public bool ReadIncomplete => _snap.Failures.Count > 0;

    AssetResolver(string overwriteDir, string modsDir, string dataDir,
                  IReadOnlyList<string> enabledMods, IReadOnlyList<ActiveArchive> archives)
    {
        _overwriteDir = overwriteDir ?? "";
        _modsDir = modsDir ?? "";
        _dataDir = dataDir ?? "";
        _enabledMods = enabledMods;
        _archives = DedupeArchives(archives);    // collapse a path bound by >1 plugin → ONE provider (no double-count → no false Ambiguous)
        _looseRoots = BuildLooseRoots();         // fixed ordered roots: overwrite > mods (priority) > Data
        _snap = BuildTables();
    }

    /// <summary>The loose roots in precedence order: overwrite, then each enabled mod highest-priority-first, then Data.</summary>
    IReadOnlyList<(string Name, string Dir)> BuildLooseRoots()
    {
        var roots = new List<(string, string)>(_enabledMods.Count + 2);
        if (_overwriteDir.Length > 0) roots.Add(("overwrite", _overwriteDir));
        foreach (var mod in _enabledMods) roots.Add((mod, Path.Combine(_modsDir, mod)));
        if (_dataDir.Length > 0) roots.Add(("Data", _dataDir));
        return roots;
    }

    /// <summary>Collapse the injected archives to one entry per distinct path, keeping the highest plugin rank: the
    /// same .bsa can arrive under two plugin bindings, which would raise a false <see cref="AssetHit.Ambiguous"/>.</summary>
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

    /// <summary>Build a resolver over the given roots, enabled-mod priority list and active archives, reading each archive's table once.</summary>
    public static AssetResolver Build(string overwriteDir, string modsDir, string dataDir,
                                      IReadOnlyList<string> enabledModsByPriority, IReadOnlyList<ActiveArchive> activeArchives)
        => new(overwriteDir, modsDir, dataDir, enabledModsByPriority, activeArchives);

    Snapshot BuildTables()
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var a in _archives)                              // _archives is already path-deduped (DedupeArchives) — read each once
        {
            stamps[a.Path] = FileStamp.Of(a.Path);
            try { tables[a.Path] = ReadArchiveTable(a.Path); }
            catch (Exception ex)
            {
                tables[a.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // empty = contributes nothing
                failures.Add($"{Path.GetFileName(a.Path)} (loaded by {a.OwningPlugin}): could not read the archive table — {Concise(ex)}");
            }
        }
        return new Snapshot(tables, stamps, failures);
    }

    /// <summary>Read one BSA's file table with Mutagen's native reader into a string set. IArchiveReader is not
    /// IDisposable in Mutagen 0.53.1, so the finally's cast disposes nothing and is not the release mechanism.</summary>
    static HashSet<string> ReadArchiveTable(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                set.Add(Normalize(file.Path));
        }
        finally { (reader as IDisposable)?.Dispose(); }           // disposes nothing in 0.53.1 — see the summary
        return set;
    }

    /// <summary>Read ONE entry's bytes out of a BSA with Mutagen's native reader. Returns a fresh array, null when
    /// the entry is not in the archive, and THROWS when the archive cannot be read so the caller reports it.</summary>
    public static byte[]? TryReadArchiveEntry(string archivePath, string entryPath)
    {
        var want = Normalize(entryPath);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                // OrdinalIgnoreCase — BSA tables store paths lowercased, so a case-sensitive compare would miss the entry.
                if (string.Equals(Normalize(file.Path), want, StringComparison.OrdinalIgnoreCase))
                    return file.GetBytes();                        // fresh array; no held handle (see the summary)
            return null;                                           // archive read fine, entry simply not present
        }
        finally { (reader as IDisposable)?.Dispose(); }           // disposes nothing in 0.53.1 — see ReadArchiveTable
    }

    /// <summary>Is <paramref name="entryPath"/> inside this archive? The existence half of <see cref="TryReadArchiveEntry"/> — same match, no GetBytes.</summary>
    public static bool ArchiveHasEntry(string archivePath, string entryPath)
    {
        var want = Normalize(entryPath);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                if (string.Equals(Normalize(file.Path), want, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        finally { (reader as IDisposable)?.Dispose(); }           // disposes nothing in 0.53.1 — see ReadArchiveTable
    }

    /// <summary>Read many entries out of ONE archive in a single table walk, where the single-entry call costs an
    /// open and a full table scan each. A wanted key missing from the result means the entry is not in the archive;
    /// an archive that cannot be opened THROWS, never a silent empty map.</summary>
    public static Dictionary<string, byte[]> TryReadArchiveEntries(string archivePath, IReadOnlyCollection<string> entryPaths)
    {
        var wanted = new Dictionary<string, string>(entryPaths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in entryPaths) wanted[Normalize(p)] = p;                 // normalized key → the caller's original string
        var result = new Dictionary<string, byte[]>(entryPaths.Count, StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
            {
                if (result.Count == wanted.Count) break;                        // everything found — stop walking the table
                if (wanted.TryGetValue(Normalize(file.Path), out var original) && !result.ContainsKey(original))
                    result[original] = file.GetBytes();
            }
            return result;
        }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    /// <summary>Read the bytes of ONE resolved provider — the shared home for the loose-vs-BSA winner read.</summary>
    public static (byte[]? Bytes, string? Error) ReadPlacementSource(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose)
        {
            var p = s.LooseFilePath;
            if (p is null || !File.Exists(p)) return (null, $"the resolved loose source '{p}' is no longer on disk");
            try { return (File.ReadAllBytes(p), null); }
            catch (Exception ex) { return (null, $"could not read resolved source '{p}': {ex.Message}"); }
        }
        try
        {
            var b = s.ArchivePath is null ? null : TryReadArchiveEntry(s.ArchivePath, s.EntryPath);
            return b is null ? (null, $"entry '{s.EntryPath}' not found inside '{Path.GetFileName(s.ArchivePath ?? "?")}'") : (b, null);
        }
        catch (Exception ex) { return (null, $"could not read archive '{Path.GetFileName(s.ArchivePath ?? "?")}': {ex.Message}"); }
    }

    /// <summary>Resolve one Data-relative asset path. Single-shot — a caller making many reads in one operation <see cref="Capture"/>s once instead.</summary>
    public AssetHit Resolve(string relPath) => Resolve(relPath, _snap);

    AssetHit Resolve(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);                 // ONE precedence path, winner first (shared with placement)
        if (sources.Count == 0)
            return new AssetHit(rel, false, null, Array.Empty<AssetProvider>(), false);
        // Project the concrete sources down to the display providers — the on-disk paths are placement-only.
        var providers = sources.Select(s => new AssetProvider(s.ProviderName, s.Kind, s.OwningMod)).ToList();
        return new AssetHit(rel, true, providers[0], providers, providers.Count > 1);
    }

    /// <summary>The ONE precedence resolution both <see cref="Resolve"/> and <see cref="ResolveForPlacement"/> ride,
    /// so the two cannot drift. Each provider comes back with its concrete on-disk descriptor, winner first.</summary>
    List<PlacementSource> ResolveProviders(string rel, Snapshot snap)
    {
        // ---- loose, in MO2 precedence order — via the per-subtree cache (warmed on first touch) ----
        var subtreeDir = Normalize(Path.GetDirectoryName(rel) ?? "");
        var fname = Path.GetFileName(rel);
        var st = snap.LooseCache.GetOrAdd(subtreeDir, WarmSubtree);
        var loose = new List<PlacementSource>();
        foreach (var (rootIndex, files) in st.Present)               // Present is already in precedence order
            if (files.Contains(fname))
                loose.Add(new PlacementSource(_looseRoots[rootIndex].Name, AssetKind.Loose,
                    LooseFilePath: Path.Combine(_looseRoots[rootIndex].Dir, rel), ArchivePath: null, EntryPath: rel));

        // ---- BSA, highest plugin rank first ----
        var bsa = new List<(PlacementSource source, int rank)>();
        foreach (var a in _archives)
            if (snap.Tables.TryGetValue(a.Path, out var t) && t.Contains(rel))
                bsa.Add((new PlacementSource(Path.GetFileName(a.Path), AssetKind.Bsa,
                    LooseFilePath: null, ArchivePath: a.Path, EntryPath: rel, OwningMod: a.OwningMod), a.PluginRank));
        // Higher plugin rank wins; the archive filename is a deterministic tie-break for a plugin's equal-rank archives.
        var bsaOrdered = bsa.OrderByDescending(b => b.rank)
                            .ThenBy(b => b.source.ProviderName, StringComparer.OrdinalIgnoreCase)
                            .Select(b => b.source);

        var providers = new List<PlacementSource>(loose);
        providers.AddRange(bsaOrdered);
        return providers;
    }

    /// <summary>Resolve one Data-relative asset path to its concrete on-disk sources for placement: exactly one source is an unambiguous copy to re-assert.</summary>
    public PlacementResolution ResolveForPlacement(string relPath) => ResolveForPlacement(relPath, _snap);

    internal PlacementResolution ResolveForPlacement(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);
        return new PlacementResolution(rel, sources, sources.Count > 1, snap.Failures.Count > 0);
    }

    public const string OverwriteLayerName = "overwrite";

    public const string DataLayerName = "Data";

    /// <summary>Is <paramref name="name"/> one of the two LAYER names — the half of the reserved set that needs no built resolver? Stated once, here.</summary>
    public static bool IsReservedLayerName(string name) =>
        string.Equals(OverwriteLayerName, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(DataLayerName, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Is <paramref name="name"/> a RESERVED provider name — "overwrite", "Data", or an active archive's
    /// filename? An enabled mod folder name is deliberately not reserved (docs/architecture/assets.md).</summary>
    public bool IsReservedProviderName(string name)
    {
        if (_overwriteDir.Length > 0 && string.Equals(OverwriteLayerName, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (_dataDir.Length > 0 && string.Equals(DataLayerName, name, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var a in _archives)
            if (string.Equals(Path.GetFileName(a.Path), name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    bool IsEnabledMod(string name)
    {
        foreach (var mod in _enabledMods)
            if (string.Equals(mod, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The copy of <paramref name="relPath"/> inside the mod folder <paramref name="providerName"/> names,
    /// when the built universe has no answer for that name at that path. <see cref="OffOrderAssetSource"/> owns the
    /// lane; the <see cref="PlacementSource.OwnerEnabled"/> qualifier for a BSA in an enabled mod is set here.</summary>
    public OffOrderLookup TryResolveOffOrderProvider(string? providerName, string relPath)
    {
        var look = OffOrderAssetSource.Resolve(_modsDir, IsReservedProviderName, providerName, relPath);
        return look.Source is { } s && s.Kind == AssetKind.Bsa && IsEnabledMod(s.ProviderName)
            ? look with { Source = s with { OwnerEnabled = true } }
            : look;
    }

    /// <summary>Resolve many paths against ONE pinned build, so a refresh landing mid-scan cannot split the scan.</summary>
    public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
    {
        var snap = _snap;                                         // pin ONE build for the whole scan
        return relPaths.Select(p => Resolve(p, snap)).ToList();
    }

    /// <summary>Every distinct Data-relative path that exists anywhere under <paramref name="prefix"/>, across all
    /// loose roots and all active BSAs. Every returned path is re-rooted on the NORMALIZED prefix, so one answer
    /// never carries two spellings of one folder; a loose root that will not enumerate contributes nothing.</summary>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix) => EnumerateUnder(prefix, _snap);

    /// <summary>As <see cref="EnumerateUnder(string)"/>, with the walk BOUNDED: <paramref name="keep"/> filters
    /// inside the walk so the cap counts matches, and the walk stops at <paramref name="max"/> of them, setting
    /// <paramref name="stopped"/>. 0 is no cap, null keeps everything.</summary>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix, Func<string, bool>? keep, int max, out bool stopped)
        => EnumerateUnder(prefix, _snap, keep, max, out stopped);

    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap)
        => EnumerateUnder(prefix, snap, null, 0, out _);

    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap, Func<string, bool>? keep, int max, out bool stopped)
    {
        stopped = false;
        var pre = NormalizeQueryPath(prefix).TrimEnd('\\');      // drive-root / '..' rejected loud, backslash-normalized
        // Match a SUBTREE, not a sibling whose name starts with 'pre'. An empty prefix is the Data root.
        var withSep = pre.Length == 0 ? "" : pre + "\\";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Full() => max > 0 && found.Count >= max;
        void Take(string rel) { if (keep is null || keep(rel)) found.Add(rel); }

        // loose: recurse each root's copy of the prefix dir (set-UNION across roots — the per-path winner is decided later).
        foreach (var (_, rootDir) in _looseRoots)
        {
            var baseDir = Path.Combine(rootDir, pre);
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                {
                    // Re-rooted on the NORMALIZED prefix, not sliced off rootDir, which would carry the caller's casing.
                    Take(withSep + Normalize(f.Substring(baseDir.Length)));
                    if (Full()) { stopped = true; return found; }
                }
            }
            catch { /* a root that won't enumerate contributes nothing */ }
            if (Full()) { stopped = true; return found; }
        }

        // BSA: every table entry under the prefix (the cached tables ARE the authoritative archive listing, zero handle at rest).
        foreach (var t in snap.Tables.Values)
            foreach (var entry in t)
                if (entry.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
                {
                    // Re-rooted on the same prefix as the loose lane, so one answer does not mix two spellings of a folder.
                    Take(withSep + entry.Substring(withSep.Length));
                    if (Full()) { stopped = true; return found; }
                }

        return found;
    }

    /// <summary>Capture the CURRENT build as a pinned read view, so a bulk scan and its read-failure list answer from one build.</summary>
    public AssetView Capture() => new(this, _snap);

    /// <summary>A read view pinned to ONE captured build (see <see cref="Capture"/>). No handles — safe to hold for a call.</summary>
    public readonly struct AssetView
    {
        readonly AssetResolver _r;
        readonly Snapshot _s;
        internal AssetView(AssetResolver r, Snapshot s) { _r = r; _s = s; }   // only Capture() constructs

        public IReadOnlyList<string> BsaFailures => _s.Failures;

        public bool ReadIncomplete => _s.Failures.Count > 0;

        public AssetHit Resolve(string relPath) => _r.Resolve(relPath, _s);

        public PlacementResolution ResolveForPlacement(string relPath) => _r.ResolveForPlacement(relPath, _s);

        /// <summary>The off-order source lane — see <see cref="AssetResolver.TryResolveOffOrderProvider"/>.
        /// Deliberately NOT pinned to this view's build: an off-order folder contributes to no build.</summary>
        public OffOrderLookup TryResolveOffOrderProvider(string? providerName, string relPath)
            => _r.TryResolveOffOrderProvider(providerName, relPath);

        public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
        {
            var r = _r; var s = _s;                              // locals — a struct's lambda can't capture 'this'
            return relPaths.Select(p => r.Resolve(p, s)).ToList();
        }

        public IReadOnlyCollection<string> EnumerateUnder(string prefix) => _r.EnumerateUnder(prefix, _s);

        public IReadOnlyCollection<string> EnumerateUnder(string prefix, Func<string, bool>? keep, int max, out bool stopped)
            => _r.EnumerateUnder(prefix, _s, keep, max, out stopped);
    }

    /// <summary>Re-stat the inputs — the active archives and every WARMED loose subtree's dirs across all roots —
    /// and rebuild in one reference swap if any changed. A changed archive or mod SET is an order change instead.</summary>
    public bool RefreshIfStale()
    {
        var snap = _snap;
        bool stale = false;
        foreach (var a in _archives)
            if (!snap.Stamps.TryGetValue(a.Path, out var s) || FileStamp.Of(a.Path) != s) { stale = true; break; }
        if (!stale)
            foreach (var kv in snap.LooseCache)                       // each warmed loose subtree — re-stat its dirs across all roots
                if (LooseSubtreeStale(kv.Key, kv.Value)) { stale = true; break; }
        if (!stale) return false;
        _snap = BuildTables();                                        // BSA tables re-read; loose cache starts empty, re-warms lazily
        return true;
    }

    bool LooseSubtreeStale(string subtreeDir, LooseSubtree st)
    {
        var roots = _looseRoots;
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = subtreeDir.Length == 0 ? roots[i].Dir : Path.Combine(roots[i].Dir, subtreeDir);
            if (FileStamp.OfDirectory(dir) != st.DirStamps[i]) return true;
        }
        return false;
    }

    LooseSubtree WarmSubtree(string subtreeDir)
    {
        var roots = _looseRoots;
        var stamps = new FileStamp[roots.Count];
        var present = new List<(int, HashSet<string>)>();
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = subtreeDir.Length == 0 ? roots[i].Dir : Path.Combine(roots[i].Dir, subtreeDir);
            stamps[i] = FileStamp.OfDirectory(dir);                  // Absent if it isn't there — baseline for an appear/disappear
            var files = SafeListFilenames(dir);
            if (files is { Count: > 0 }) present.Add((i, files));
        }
        return new LooseSubtree(stamps, present.ToArray());
    }

    static HashSet<string>? SafeListFilenames(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir)) set.Add(Path.GetFileName(f));
            return set;
        }
        catch { return null; }
    }

    /// <summary>Normalize an asset path for matching: forward slashes to backslashes, drop a leading separator. Lenient — never throws.</summary>
    static string Normalize(string p) => (p ?? "").Replace('/', '\\').TrimStart('\\');

    /// <summary>The public form of the one path gate — normalize and validate a Data-relative path — so the place lane's destination rides this validator.</summary>
    public static string ValidateRelPath(string relPath) => NormalizeQueryPath(relPath);

    /// <summary>Normalize AND validate a Data-relative QUERY path: a drive-rooted or '..'-carrying path is refused
    /// naming the input, and no-op segments are collapsed so the loose walk and the BSA-table match answer alike.</summary>
    static string NormalizeQueryPath(string relPath)
    {
        var rel = Normalize(relPath);
        if (Path.IsPathRooted(rel))
            throw new ArgumentException($"expected a Data-relative asset path, got a drive-rooted path: '{relPath}'");
        var kept = new List<string>();
        foreach (var seg in rel.Split('\\'))
        {
            if (seg == "..")
                throw new ArgumentException($"expected a Data-relative asset path, got a parent-escaping ('..') path: '{relPath}'");
            if (seg.Length > 0 && seg != ".") kept.Add(seg);
        }
        return string.Join('\\', kept);
    }

    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    /// <summary>Holds no handles at rest, so Dispose is a no-op; kept so a call site can treat the resolver as disposable.</summary>
    public void Dispose() { }
}

using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

// AssetResolver — which source provides a Data-relative asset and which copy wins, through MO2's own precedence
// extended across active-plugin BSAs; precedence, injected order and the snapshot in docs/architecture/assets.md.
// Zero archive handles at rest: pinned by AssetResolverTests.TheResolverHoldsNoArchiveHandleAtRest.

public enum AssetKind { Loose, Bsa }

/// <summary>One source that provides an asset: the mod folder name, "overwrite", "Data", or a BSA's filename, plus
/// the MO2 layer a BSA's archive file lives in (null for a loose source, whose name already IS that layer).</summary>
public sealed record AssetProvider(string Source, AssetKind Kind, string? OwningMod = null);

/// <summary>One asset path's resolution: the winner (null iff not <see cref="Exists"/>), every provider winner-first,
/// and <see cref="Ambiguous"/> — contention to verify, never a confirmed problem (docs/architecture/assets.md).</summary>
public sealed record AssetHit(string RelPath, bool Exists, AssetProvider? Winner, IReadOnlyList<AssetProvider> Providers, bool Ambiguous,
                              HiddenCopies? Hidden = null);

/// <summary>A concrete on-disk source one provider supplies an asset from: a loose file path, or a .bsa path plus the
/// entry inside it. <see cref="OffOrder"/> marks a copy the game is not loading and <see cref="OwnerEnabled"/> that
/// its mod is enabled (a root archive no plugin binds, or a loose file its folder did not list), else the mod is
/// unticked; <see cref="OwningMod"/> is a BSA archive's own MO2 layer.</summary>
public sealed record PlacementSource(string ProviderName, AssetKind Kind, string? LooseFilePath, string? ArchivePath,
                                     string EntryPath, bool OffOrder = false, string? OwningMod = null,
                                     bool OwnerEnabled = false);

public sealed record PlacementResolution(string RelPath, IReadOnlyList<PlacementSource> Sources, bool Ambiguous, bool ReadIncomplete,
                                         HiddenCopies? Hidden = null);

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
    readonly Mo2SkipRules _skip;                         // which loose files MO2 keeps out of the Data view

    /// <summary>One build's whole output, swapped in as one reference write so a concurrent Resolve never sees a half-rebuilt cache.</summary>
    internal sealed class Snapshot
    {
        public readonly Dictionary<string, HashSet<string>> Tables;   // archive path → its file paths (normalized)
        public readonly Dictionary<string, FileStamp> Stamps;         // archive path → freshness stamp at this build
        public readonly List<string> Failures;                        // archives that couldn't be read, with the reason
        public readonly ConcurrentDictionary<string, string> RootFailures;   // "root|dir" → a loose root that could not be walked or listed, with the reason
        public readonly ConcurrentDictionary<string, bool> Dirs;             // full dir path → Directory.Exists, asked once per build
        public readonly ConcurrentDictionary<string, HashSet<string>?> Children;   // full dir path → its names, for the absence verdict only; null = would not list
        public readonly ConcurrentDictionary<string, LooseSubtree> LooseCache;
        public readonly ConcurrentDictionary<string, WatchedDir> DirWatch;   // watched dir → what must not change about it; the whole loose freshness check
        public int WarmListings;                                             // directory listings the warm took; a test seam, never the check's
        public int Warms;                                                    // subtree warms finished on this build, counted after their watches
        public Snapshot(Dictionary<string, HashSet<string>> tables, Dictionary<string, FileStamp> stamps, List<string> failures)
        {
            Tables = tables; Stamps = stamps; Failures = failures;
            RootFailures = new(StringComparer.OrdinalIgnoreCase);
            Dirs = new(StringComparer.OrdinalIgnoreCase);
            Children = new(StringComparer.OrdinalIgnoreCase);
            LooseCache = new(StringComparer.OrdinalIgnoreCase);
            DirWatch = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>What one watched directory must keep looking like for this build to stay fresh, in NAMES and not in a
    /// timestamp: a directory's last-write comes off a clock coarser than a write and a delete next to each other, so
    /// a stamp cannot always tell two different listings apart.</summary>
    internal sealed class WatchedDir
    {
        /// <summary>The whole listing, compared name by name — set only for a directory a warmed subtree resolves FROM.</summary>
        public volatile HashSet<string>? Entries;

        /// <summary>Names whose APPEARANCE here is a change and nothing else: the first missing component of a subtree
        /// a root does not have. A byte value because the set is the point; a concurrent dictionary because two
        /// threads can warm two subtrees answered by the same ancestor.</summary>
        public readonly ConcurrentDictionary<string, byte> Forbidden = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names a plain FILE holds where a subtree wanted a directory — a real absence, so the change to
        /// look for is the name becoming a directory, or going altogether.</summary>
        public readonly ConcurrentDictionary<string, byte> FileHeld = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>One subtree directory's loose resolution: the filename sets of the roots that have it, in precedence
    /// order. Its freshness is not held here — warming puts one directory per root under the build's shared
    /// <see cref="Snapshot.DirWatch"/>, so two subtrees answered by the same directory are read once. <see cref="Warmed"/>
    /// orders it against a call's freshness check, and <see cref="Listed"/> holds the whole listings it was read off,
    /// which must still be the watch's baselines for a checked call to take it.</summary>
    internal sealed class LooseSubtree
    {
        public readonly (int RootIndex, HashSet<string> Files)[] Present;   // roots that have the dir + ≥1 file, precedence order
        public readonly bool AnySuffixed;                                  // a file here ends with a skip suffix, so a hidden twin may be here
        public readonly (string Dir, HashSet<string> Names)[] Listed;      // the subtree dirs this was read off, each whole
        public readonly int Warmed;                                        // the build's warm count once this warm's watches were in place
        public LooseSubtree((int, HashSet<string>)[] present, bool anySuffixed, (string, HashSet<string>)[] listed, int warmed)
        { Present = present; AnySuffixed = anySuffixed; Listed = listed; Warmed = warmed; }
    }

    volatile Snapshot _snap;

    /// <summary>The call's listings, on a copy <see cref="Frozen"/> made for one call; null on the live resolver, whose
    /// every pass takes its own.</summary>
    LooseListings? _callListings;

    /// <summary>The loose roots this resolver was built over, in precedence order. Fixed for its lifetime, so unlike
    /// the archive tables it needs no snapshot; exposed for consumers that need the VFS ORDER itself.</summary>
    public IReadOnlyList<(string Name, string Dir)> LooseRoots => _looseRoots;

    public IReadOnlyList<string> BsaFailures => _snap.Failures;

    /// <summary>The loose roots this build could not walk or list, each named with the reason — the loose twin of
    /// <see cref="BsaFailures"/>. Filled lazily, because the read that hits the failure is the enumeration itself, and
    /// kept for the life of the build: a root that would not read stays named until the next rebuild proves otherwise.</summary>
    public IReadOnlyList<string> RootFailures => SortedRootFailures(_snap);

    public bool ReadIncomplete => _snap.Failures.Count > 0 || !_snap.RootFailures.IsEmpty;

    /// <summary>How many directories this build's loose freshness check re-lists — the cost <see cref="RefreshIfStale"/>
    /// pays per call, exposed so a test can assert what that cost grows WITH: one entry per root per directory that
    /// answers for a subtree, which is not the same as one per root per subtree.</summary>
    internal int WatchedDirectoryCount => _snap.DirWatch.Count;

    /// <summary>How many directory listings this build's warms have taken, so a test can assert a read pass lists each directory once.</summary>
    internal int WarmListingCount => Volatile.Read(ref _snap.WarmListings);

    /// <summary>The unread loose roots as ONE short clause, for a per-item reason with no caveat block above it to
    /// point at: the first root in full and the rest counted, because that sentence repeats per item. Empty string
    /// when every root read, so a caller can append it unconditionally.</summary>
    public static string RootFailureBrief(IReadOnlyList<string> failures)
        => failures.Count == 0
            ? ""
            : " (the loose root that would not read: " + failures[0]
              + (failures.Count > 1 ? $"; and {failures.Count - 1} more" : "") + ")";

    /// <summary>The root failures in a stable order, so two renders of one build read the same.</summary>
    static IReadOnlyList<string> SortedRootFailures(Snapshot snap) =>
        snap.RootFailures.IsEmpty
            ? Array.Empty<string>()
            : snap.RootFailures.Values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

    AssetResolver(string overwriteDir, string modsDir, string dataDir,
                  IReadOnlyList<string> enabledMods, IReadOnlyList<ActiveArchive> archives, Mo2SkipRules skip)
    {
        _skip = skip;
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
                                      IReadOnlyList<string> enabledModsByPriority, IReadOnlyList<ActiveArchive> activeArchives,
                                      Mo2SkipRules? skip = null)
        => new(overwriteDir, modsDir, dataDir, enabledModsByPriority, activeArchives, skip ?? Mo2SkipRules.Default);

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
    public AssetHit Resolve(string relPath) { var (snap, listings) = BeginPass(); return Resolve(relPath, snap, listings); }

    AssetHit Resolve(string relPath, Snapshot snap, LooseListings listings)
    {
        var rel = NormalizeQueryPath(relPath);
        var (sources, hidden) = ResolveProviders(rel, snap, listings);   // ONE precedence path, winner first (shared with placement)
        if (sources.Count == 0)
            return new AssetHit(rel, false, null, Array.Empty<AssetProvider>(), false, hidden);
        // Project the concrete sources down to the display providers — the on-disk paths are placement-only.
        var providers = sources.Select(s => new AssetProvider(s.ProviderName, s.Kind, s.OwningMod)).ToList();
        return new AssetHit(rel, true, providers[0], providers, providers.Count > 1, hidden);
    }

    /// <summary>The ONE precedence resolution both <see cref="Resolve"/> and <see cref="ResolveForPlacement"/> ride,
    /// so the two cannot drift. Each provider comes back with its concrete on-disk descriptor, winner first, and the
    /// loose copies MO2 hides, which provide nothing.</summary>
    (List<PlacementSource> Sources, HiddenCopies? Hidden) ResolveProviders(string rel, Snapshot snap, LooseListings listings)
    {
        // ---- loose, in MO2 precedence order — via the per-subtree cache (warmed on first touch) ----
        var opened = AsWindowsOpens(rel);                          // the loose lookup only: an archive entry is matched as asked
        var subtreeDir = Normalize(Path.GetDirectoryName(opened) ?? "");
        var fname = Path.GetFileName(opened);
        var st = Subtree(subtreeDir, snap, listings);
        var loose = new List<PlacementSource>();
        // A file MO2 hides is not in the Data view, whichever root holds it; archives are not filtered.
        // The game's Data folder is never linked, so only a root name hides there.
        var linkedRule = _skip.Hides(opened, linked: true);
        var dataRule = linkedRule?.Kind == HiddenKind.RootName ? linkedRule : null;
        HiddenRule? hiddenRule = null;
        string? hiddenPath = null;
        List<string>? holders = null;
        void Hold(HiddenRule r, string path, int rootIndex)
        {
            hiddenRule ??= r; hiddenPath ??= path;
            (holders ??= new()).Add(_looseRoots[rootIndex].Name);
        }
        foreach (var (rootIndex, files) in st.Present)               // Present is already in precedence order
        {
            bool linked = !IsDataRoot(rootIndex);
            var rule = linked ? linkedRule : dataRule;
            if (files.Contains(fname))
            {
                if (rule is null)
                    loose.Add(new PlacementSource(_looseRoots[rootIndex].Name, AssetKind.Loose,
                        LooseFilePath: LooseListings.OnDisk(_looseRoots[rootIndex].Dir, opened), ArchivePath: null, EntryPath: rel));
                else Hold(rule, opened, rootIndex);
            }
            // The root that holds only x's hidden twin has x, hidden.
            else if (rule is null && linked && st.AnySuffixed && _skip.Twin(fname, files.Contains) is { } twin)
                Hold(twin.Rule, opened + twin.Rule.Token, rootIndex);
        }
        var hidden = hiddenRule is null ? null
            : new HiddenCopies(hiddenRule, hiddenPath!, holders!, Twin: !string.Equals(hiddenPath, opened, StringComparison.OrdinalIgnoreCase));

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
        return (providers, hidden);
    }

    /// <summary>Is this loose root the game's own Data folder, the one root MO2 never links?</summary>
    bool IsDataRoot(int rootIndex) => _dataDir.Length > 0 && rootIndex == _looseRoots.Count - 1;

    /// <summary>Resolve one Data-relative asset path to its concrete on-disk sources for placement: exactly one source is an unambiguous copy to re-assert.</summary>
    public PlacementResolution ResolveForPlacement(string relPath) { var (snap, listings) = BeginPass(); return ResolveForPlacement(relPath, snap, listings); }

    internal PlacementResolution ResolveForPlacement(string relPath, Snapshot snap, LooseListings listings)
    {
        var rel = NormalizeQueryPath(relPath);
        var (sources, hidden) = ResolveProviders(rel, snap, listings);
        return new PlacementResolution(rel, sources, sources.Count > 1, snap.Failures.Count > 0 || !snap.RootFailures.IsEmpty, hidden);
    }

    public const string OverwriteLayerName = "overwrite";

    public const string DataLayerName = "Data";

    /// <summary>Where a layer is, as a sentence names it.</summary>
    public static string LayerPlace(string layer) => layer switch
    {
        DataLayerName => "in your game's Data folder",
        OverwriteLayerName => "in MO2's overwrite folder",
        _ => $"in the mod folder '{layer}'",
    };

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
    /// lane; the <see cref="PlacementSource.OwnerEnabled"/> qualifier for a copy in an enabled mod is set here.</summary>
    public OffOrderLookup TryResolveOffOrderProvider(string? providerName, string relPath)
        => TryResolveOffOrderProvider(providerName, relPath, BeginPass().Listings);

    /// <summary>As above; an enabled folder is read off the call's <paramref name="listings"/>, an unticked one directly.</summary>
    OffOrderLookup TryResolveOffOrderProvider(string? providerName, string relPath, LooseListings listings)
    {
        var enabled = providerName is not null && IsEnabledMod(providerName.Trim());
        var look = OffOrderAssetSource.Resolve(_modsDir, IsReservedProviderName, providerName, AsWindowsOpens(Normalize(relPath)), relPath, _skip,
                                               enabled ? listings : null);
        return look.Source is { } s && enabled
            ? look with { Source = s with { OwnerEnabled = true } }
            : look;
    }

    /// <summary>Resolve many paths against ONE pinned build, so a refresh landing mid-scan cannot split the scan.</summary>
    public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
    {
        var (snap, listings) = BeginPass();                       // pin ONE build for the whole scan
        return relPaths.Select(p => Resolve(p, snap, listings)).ToList();
    }

    /// <summary>Every distinct Data-relative path that exists anywhere under <paramref name="prefix"/>, across all
    /// loose roots and all active BSAs. Every returned path is re-rooted on the NORMALIZED prefix, so one answer
    /// never carries two spellings of one folder; a loose root that will not enumerate contributes nothing and is
    /// named in <see cref="RootFailures"/>, which sets <see cref="ReadIncomplete"/>.</summary>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix) { var (snap, listings) = BeginPass(); return EnumerateUnder(prefix, snap, listings); }

    /// <summary>As <see cref="EnumerateUnder(string)"/>, with the walk BOUNDED: <paramref name="keep"/> filters
    /// inside the walk so the cap counts matches, and the walk stops at <paramref name="max"/> of them, setting
    /// <paramref name="stopped"/>. 0 is no cap, null keeps everything.</summary>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix, Func<string, bool>? keep, int max, out bool stopped)
    {
        var (snap, listings) = BeginPass();
        return EnumerateUnder(prefix, snap, listings, keep, max, out stopped);
    }

    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap, LooseListings listings)
        => EnumerateUnder(prefix, snap, listings, null, 0, out _);

    /// <summary>Is <paramref name="name"/> a provider this build has: a loose root's name, an active archive's
    /// filename, or the MO2 layer an active archive lives in — the names <see cref="AssetSourceSelection.NameMatches(string, string?, string)"/> accepts.</summary>
    public bool HasProvider(string name)
    {
        if (RootIndex(name) >= 0) return true;
        foreach (var a in _archives)
            if (AssetSourceSelection.NameMatches(Path.GetFileName(a.Path), a.OwningMod, name)) return true;
        return false;
    }

    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap, LooseListings listings, Func<string, bool>? keep, int max,
                                               out bool stopped, string? provider = null, HiddenTally? hidden = null)
    {
        stopped = false;
        var pre = NormalizeQueryPath(prefix).TrimEnd('\\');      // drive-root / '..' rejected loud, backslash-normalized
        // Match a SUBTREE, not a sibling whose name starts with 'pre'. An empty prefix is the Data root.
        var withSep = pre.Length == 0 ? "" : pre + "\\";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Full() => max > 0 && found.Count >= max;
        void Take(string rel) { if (keep is null || keep(rel)) found.Add(rel); }
        // A named provider walks its own root only (none when it names only archives); the winner is still decided later, against every root.
        int only = provider is null ? -1 : RootIndex(provider);

        // loose: recurse each root's copy of the prefix dir (set-UNION across roots — the per-path winner is decided later).
        for (int i = 0; i < _looseRoots.Count; i++)
        {
            if (provider is not null && i != only) continue;
            var (rootName, rootDir) = _looseRoots[i];
            bool linked = !IsDataRoot(i);
            var baseDir = LooseListings.OnDisk(rootDir, pre);
            // A "not there" is only an absence when it can be proved one; unreadable is named inside.
            if (!Directory.Exists(baseDir)) { AbsenceIsReal(rootName, rootDir, pre, baseDir, snap); continue; }
            // Top down over the call's listings, a folder's files in listing order and its folders queued after it:
            // the order a recursive enumeration yields, so a capped walk keeps the same matches.
            var pending = new Queue<string>();
            pending.Enqueue(baseDir);
            while (pending.TryDequeue(out var dir))
            {
                var read = listings.Get(dir);
                if (read.Listing is not { } listing)
                {
                    // A root that won't enumerate contributes nothing more, so it is NAMED on the build: it sets
                    // ReadIncomplete and the answer says which root was not walked, never a silent half-answer.
                    RecordRootFailure(rootName, pre, "walk", read.Failure!, snap);
                    break;
                }
                foreach (var name in listing.Names)
                {
                    var f = LooseListings.OnDisk(dir, name);
                    if (!listing.IsFile(name)) { pending.Enqueue(f); continue; }
                    // Re-rooted on the NORMALIZED prefix, not sliced off rootDir, which would carry the caller's casing.
                    var rel = withSep + Normalize(f.Substring(baseDir.Length));
                    if (_skip.Hides(rel, linked) is not { } rule) Take(rel);
                    // In scope, and dropped: the hidden name, or for a twin x.mohidden the x it hides.
                    else if (hidden is not null && (keep is null || keep(rel)
                             || (rule.Kind == HiddenKind.SkipSuffix && keep(rel[..^rule.Token.Length]))))
                        hidden.Add(rule, rel);
                    if (Full()) { stopped = true; return found; }
                }
            }
            if (Full()) { stopped = true; return found; }
        }

        // BSA: every table entry under the prefix (the cached tables ARE the authoritative archive listing, zero handle at rest).
        foreach (var a in _archives)
        {
            if (provider is not null && !AssetSourceSelection.NameMatches(Path.GetFileName(a.Path), a.OwningMod, provider)) continue;
            if (!snap.Tables.TryGetValue(a.Path, out var t)) continue;
            foreach (var entry in t)
                if (entry.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
                {
                    // Re-rooted on the same prefix as the loose lane, so one answer does not mix two spellings of a folder.
                    Take(withSep + entry.Substring(withSep.Length));
                    if (Full()) { stopped = true; return found; }
                }
        }

        return found;
    }

    /// <summary>Capture the CURRENT build as a pinned read view, so a bulk scan and its read-failure list answer from one build.</summary>
    public AssetView Capture() { var (snap, listings) = BeginPass(); return new(this, snap, listings); }

    /// <summary>A copy for one call: it holds the current build's snapshot, shares its loose cache, and reads the loose
    /// folders off <paramref name="listings"/> (a new set when none is given) for every view it captures; contract in
    /// docs/architecture/assets.md.</summary>
    public AssetResolver Frozen(LooseListings? listings = null)
    {
        var copy = (AssetResolver)MemberwiseClone();
        copy._callListings = listings ?? new LooseListings();
        return copy;
    }

    /// <summary>The current build and the listings this read takes: the call's on a frozen copy, else a new set, so no
    /// listing taken before this call answers it.</summary>
    (Snapshot Snap, LooseListings Listings) BeginPass() => (_snap, _callListings ?? new LooseListings());

    /// <summary>A read view pinned to ONE captured build (see <see cref="Capture"/>). No handles — safe to hold for a call.</summary>
    public readonly struct AssetView
    {
        readonly AssetResolver _r;
        readonly Snapshot _s;
        readonly LooseListings _l;                                              // the loose listings this view reads
        internal AssetView(AssetResolver r, Snapshot s, LooseListings l) { _r = r; _s = s; _l = l; }   // only Capture() constructs

        public IReadOnlyList<string> BsaFailures => _s.Failures;

        /// <summary>The loose roots this view's walks could not enumerate — see <see cref="AssetResolver.RootFailures"/>.</summary>
        public IReadOnlyList<string> RootFailures => SortedRootFailures(_s);

        /// <summary>This view's unread roots as one short clause — see <see cref="AssetResolver.RootFailureBrief"/>.</summary>
        public string RootFailureBrief => AssetResolver.RootFailureBrief(RootFailures);

        public bool ReadIncomplete => _s.Failures.Count > 0 || !_s.RootFailures.IsEmpty;

        /// <summary>How many loose directories this view's listings hold; a test seam.</summary>
        internal int ListingCount => _l.ListingCount;

        public AssetHit Resolve(string relPath) => _r.Resolve(relPath, _s, _l);

        /// <summary>The files at the top of one loose root by its name; null when it would not list, which is named in <see cref="RootFailures"/>.</summary>
        public IReadOnlyCollection<string>? LooseRootFiles(string rootName) => _r.LooseRootFiles(rootName, _s, _l);

        public PlacementResolution ResolveForPlacement(string relPath) => _r.ResolveForPlacement(relPath, _s, _l);

        /// <summary>The off-order source lane — see <see cref="AssetResolver.TryResolveOffOrderProvider(string?, string)"/>.
        /// Deliberately NOT pinned to this view's build: an off-order folder contributes to no build. An ENABLED
        /// folder is read off this view's listings.</summary>
        public OffOrderLookup TryResolveOffOrderProvider(string? providerName, string relPath)
            => _r.TryResolveOffOrderProvider(providerName, relPath, _l);

        public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
        {
            var r = _r; var s = _s; var l = _l;                  // locals — a struct's lambda can't capture 'this'
            return relPaths.Select(p => r.Resolve(p, s, l)).ToList();
        }

        public IReadOnlyCollection<string> EnumerateUnder(string prefix) => _r.EnumerateUnder(prefix, _s, _l);

        public IReadOnlyCollection<string> EnumerateUnder(string prefix, Func<string, bool>? keep, int max, out bool stopped)
            => _r.EnumerateUnder(prefix, _s, _l, keep, max, out stopped);

        /// <summary>As above, over ONE provider's own files: its loose root, and the active archives it names or owns.</summary>
        public IReadOnlyCollection<string> EnumerateUnder(string prefix, Func<string, bool>? keep, int max, out bool stopped, string? provider,
                                                          HiddenTally? hidden = null)
            => _r.EnumerateUnder(prefix, _s, _l, keep, max, out stopped, provider, hidden);

        public bool HasProvider(string name) => _r.HasProvider(name);
    }

    /// <summary>Re-read the inputs — the active archives and every directory the warmed loose subtrees put under
    /// watch — and rebuild in one reference swap if any changed. A changed archive or mod SET is an order change
    /// instead. The watch set holds each directory ONCE however many subtrees answer from it, so the check costs one
    /// listing per watched directory rather than one per root per warmed subtree. Given a call's
    /// <paramref name="listings"/>, the check lists into them, so what it read is what the call reads, and a build it
    /// finds fresh is marked checked for that call up to the subtrees warmed before it began.</summary>
    public bool RefreshIfStale(LooseListings? listings = null)
    {
        var snap = _snap;
        listings ??= new LooseListings();
        var warmed = Volatile.Read(ref snap.Warms);                   // every warm counted here has its watches in place
        bool stale = false;
        foreach (var a in _archives)
            if (!snap.Stamps.TryGetValue(a.Path, out var s) || FileStamp.Of(a.Path) != s) { stale = true; break; }
        if (!stale)
            foreach (var kv in snap.DirWatch)                          // one listing per watched directory
                if (WatchedDirStale(kv.Value, listings.TryGet(kv.Key))) { stale = true; break; }
        if (!stale)
        {
            listings.Checked(snap, warmed);
            return false;
        }
        _snap = BuildTables();                                        // BSA tables re-read; loose cache starts empty, re-warms lazily
        return true;
    }

    /// <summary>Has one watched directory changed, going by <paramref name="now"/>, a listing taken since the warm?
    /// Names, never a timestamp: a directory's last-write comes off a clock coarser than a write and a delete next to
    /// each other, so a stamped check reads two different listings as no change at all. One listing answers for
    /// every subtree that watches this directory.</summary>
    static bool WatchedDirStale(WatchedDir w, LooseListings.Listing? now)
    {
        // Only a directory that answered is ever watched, so one that will not answer now has vanished or stopped
        // reading — both are changes.
        if (now is null) return true;
        // A directory a subtree resolves FROM: any name coming or going changes what that subtree provides.
        if (w.Entries is { } baseline && !now.Names.SetEquals(baseline)) return true;
        // A directory watched only as an ancestor: ONLY the missing name appearing is a change, so an unrelated file
        // landing in a mod folder does not throw the build away.
        foreach (var name in w.Forbidden.Keys) if (now.Names.Contains(name)) return true;
        // A name a file was holding: gone, or now a directory, and the root may provide the subtree after all. Only a
        // mod with a FILE called `meshes` (or the like) has one at all.
        foreach (var name in w.FileHeld.Keys)
            if (!now.IsFile(name)) return true;
        return false;
    }

    /// <summary>One directory's entry names read fresh, never off a memo or a call's listings; null when it will not list.</summary>
    static HashSet<string>? FreshNames(string dir) => TryList(dir)?.Names;

    static LooseListings.Listing? TryList(string dir)
    {
        try { return LooseListings.List(dir); }
        catch { return null; }
    }

    /// <summary>The subtree this call resolves <paramref name="subtreeDir"/> from: the build's when this call warmed it,
    /// or when this call's freshness check covered it — warmed before the check began, and still resolving from the
    /// listings the check compared; else warmed again off this call's listings, so the call reads every folder once.</summary>
    LooseSubtree Subtree(string subtreeDir, Snapshot snap, LooseListings listings)
    {
        if (listings.Subtrees.TryGetValue(subtreeDir, out var mine)) return mine;
        LooseSubtree? made = null;
        var st = snap.LooseCache.GetOrAdd(subtreeDir, d => made = WarmSubtree(d, snap, listings));
        if (!ReferenceEquals(st, made) && !CheckedFor(st, snap, listings))
            snap.LooseCache[subtreeDir] = st = WarmSubtree(subtreeDir, snap, listings);
        return listings.Subtrees.GetOrAdd(subtreeDir, st);
    }

    /// <summary>Did this call's freshness check cover <paramref name="st"/>? Every directory a warm reads off is
    /// watched, so a build the check found fresh agrees with the call's listings for each subtree warmed before it
    /// began, as long as the whole listings the subtree resolves from are still the ones the check compared.</summary>
    static bool CheckedFor(LooseSubtree st, Snapshot snap, LooseListings listings)
    {
        if (!listings.CheckedUpTo(snap, st.Warmed)) return false;
        foreach (var (dir, names) in st.Listed)
            if (!snap.DirWatch.TryGetValue(dir, out var w) || !ReferenceEquals(w.Entries, names)) return false;
        return true;
    }

    /// <summary>Warm one subtree against the build that asked for it, off the call's listings, so a root that will not
    /// list is named on that build the way a root that will not walk is.</summary>
    LooseSubtree WarmSubtree(string subtreeDir, Snapshot snap, LooseListings listings)
    {
        var roots = _looseRoots;
        var segments = subtreeDir.Length == 0 ? Array.Empty<string>() : subtreeDir.Split('\\');
        var present = new List<(int, HashSet<string>)>();
        var listed = new List<(string, HashSet<string>)>();
        for (int i = 0; i < roots.Count; i++)
        {
            // Most roots are answered off listings this call already took, top folder down, with no stat of their own.
            if (TryDescend(roots[i].Dir, segments, snap, listings, listed, out var files))
            {
                if (files is { Count: > 0 }) present.Add((i, files));
                continue;
            }
            var dir = subtreeDir.Length == 0 ? roots[i].Dir : LooseListings.OnDisk(roots[i].Dir, subtreeDir);
            // ONE read of this root's copy: the whole listing is the watch baseline, the filenames in it are what the
            // subtree resolves from. Two reads of one directory would only differ by the split second between them.
            var (names, held, absent) = SafeListing(dir, roots[i].Name, roots[i].Dir, subtreeDir, snap, listings);
            Watch(dir, roots[i].Dir, snap, names, absent);           // the baseline for an appear/disappear, on the build's shared watch set
            if (names is not null) listed.Add((dir, names));
            if (held is { Count: > 0 }) present.Add((i, held));
        }
        return new LooseSubtree(present.ToArray(), present.Any(p => _skip.AnySuffixed(p.Item2)), listed.ToArray(),
                                Interlocked.Increment(ref snap.Warms));
    }

    /// <summary>One root's copy of a subtree off the call's listings, top down; false sends it to <see cref="SafeListing"/>.</summary>
    static bool TryDescend(string rootDir, string[] segments, Snapshot snap, LooseListings listings,
                           List<(string, HashSet<string>)> listed, out HashSet<string>? files)
    {
        files = null;
        var dir = rootDir;
        for (int i = 0; ; i++)
        {
            bool leaf = i == segments.Length;
            if (listings.TryGet(dir, () => Interlocked.Increment(ref snap.WarmListings)) is not { } listing) return false;
            if (leaf)
            {
                snap.DirWatch.GetOrAdd(dir, _ => new WatchedDir()).Entries = listing.Names;
                listed.Add((dir, listing.Names));
                files = listing.Files;
                return true;
            }
            var name = segments[i];
            // The first name a listing lacks, or a file holds, proves the absence and is watched there.
            if (!listing.Names.Contains(name))
            {
                snap.DirWatch.GetOrAdd(dir, _ => new WatchedDir()).Forbidden[name] = 0;
                return true;
            }
            if (listing.IsFile(name))
            {
                snap.DirWatch.GetOrAdd(dir, _ => new WatchedDir()).FileHeld[name] = 0;
                return true;
            }
            dir = LooseListings.OnDisk(dir, name);
        }
    }

    /// <summary>Watch this root's copy of a subtree, off reads taken at this warm: its own listing, else the missing name on
    /// the deepest ancestor that stats; nothing where the absence was not proved (a root failure already named).</summary>
    static void Watch(string dir, string rootDir, Snapshot snap, HashSet<string>? entries, bool provedAbsent)
    {
        if (entries is not null)                                       // the warm's own listing of this root's copy
        {
            snap.DirWatch.GetOrAdd(dir, _ => new WatchedDir()).Entries = entries;
            return;
        }
        if (!provedAbsent) return;                                     // not proved: a name listed that will not stat, or an ancestor that will not list
        // One level above the root, because the root's own folder can be the missing one.
        var floor = (Path.GetDirectoryName(rootDir.TrimEnd('\\')) ?? "").Length;
        var child = dir;
        for (var up = Path.GetDirectoryName(child); up is not null; child = up, up = Path.GetDirectoryName(up))
        {
            // Exists first, then the listing: a level that is simply not there is the common case on the way up, and
            // asking the enumeration to throw for it costs an exception per missing level per root.
            if (!Directory.Exists(up))
            {
                if (up.Length <= floor) break;                         // walked past the root with nothing there
                continue;
            }
            var name = Path.GetFileName(child);                          // stats as no directory: the walk climbed past it
            if (File.Exists(child))                                      // a FILE holds the name: watched for becoming a directory
                snap.DirWatch.GetOrAdd(up, _ => new WatchedDir()).FileHeld[name] = 0;
            else                                                         // neither: watched for appearing
                snap.DirWatch.GetOrAdd(up, _ => new WatchedDir()).Forbidden[name] = 0;
            break;
        }
    }

    /// <summary>One root's copy of a subtree in ONE read: every name in it, which is the watch baseline, and the
    /// filenames among them, which is what the subtree resolves from. A dir that is not there is simply absent; a dir
    /// that THROWS is named on the build, so the single-path lanes hedge an "absent" the same way a sweep does.
    /// <c>ProvedAbsent</c> is whether a dir that is not there was proved absent rather than named as a failure.</summary>
    (HashSet<string>? Names, HashSet<string>? Files, bool ProvedAbsent) SafeListing(string dir, string rootName, string rootDir,
                                                                                   string subtreeDir, Snapshot snap, LooseListings listings)
    {
        // The same rule the walk makes: a "not there" this account cannot prove is a read failure, not an absence.
        if (!Directory.Exists(dir)) return (null, null, AbsenceIsReal(rootName, rootDir, subtreeDir, dir, snap));
        var read = listings.Get(dir, () => Interlocked.Increment(ref snap.WarmListings));   // the watch baseline and the resolvable filenames off one enumeration
        if (read.Listing is { } listing) return (listing.Names, listing.Files, false);
        RecordRootFailure(rootName, subtreeDir, "read", read.Failure!, snap);
        return (null, null, false);
    }

    /// <summary>One loose root's top-level files, off the call's listing of it; empty when absent or no root, null (and named) when it would not list.</summary>
    IReadOnlyCollection<string>? LooseRootFiles(string rootName, Snapshot snap, LooseListings listings)
    {
        var at = RootIndex(rootName);
        if (at < 0) return Array.Empty<string>();                     // no loose root by that name, so nothing ships there
        var (name, dir) = _looseRoots[at];
        bool linked = !IsDataRoot(at);
        if (Directory.Exists(dir))
        {
            var read = listings.Get(dir);
            if (read.Listing is { } listing) return listing.Files.Where(n => _skip.Hides(n, linked) is null).ToList();
            RecordRootFailure(name, "", "read", read.Failure!, snap);
            return null;
        }
        if (WalkProvesAbsent(dir, dir, Directory.Exists, FreshNames)) return Array.Empty<string>();
        RecordRootFailure(name, "", snap);
        return null;
    }

    /// <summary>Where one root name sits in <see cref="_looseRoots"/>: overwrite and Data by position, so a mod folder named either cannot shadow them.</summary>
    int RootIndex(string rootName)
    {
        bool hasOverwrite = _overwriteDir.Length > 0, hasData = _dataDir.Length > 0;
        if (rootName.Equals(OverwriteLayerName, StringComparison.OrdinalIgnoreCase)) return hasOverwrite ? 0 : -1;
        if (rootName.Equals(DataLayerName, StringComparison.OrdinalIgnoreCase)) return hasData ? _looseRoots.Count - 1 : -1;
        for (int i = hasOverwrite ? 1 : 0; i < _looseRoots.Count - (hasData ? 1 : 0); i++)
            if (_looseRoots[i].Name.Equals(rootName, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Is a directory Directory.Exists calls absent really absent? On Windows it answers "not there" for a
    /// directory the account cannot stat through — a mod folder denied at its top, or a denial inherited down from
    /// meshes\ — so the deepest ancestor that does answer is asked whether the name is present. Really absent is the
    /// common case, and it stays cheap: the ancestor stats and that ancestor's own child names are read once per
    /// build, so a sweep over many missing subtrees pays syscalls per DIRECTORY, not per root per subtree. The answer
    /// itself is not cached — with those two memoized the walk is a handful of dictionary lookups.</summary>
    bool AbsenceIsReal(string rootName, string rootDir, string subtreeDir, string dir, Snapshot snap)
    {
        // A memo never makes a failure: a memoized walk that cannot prove the absence is walked again off the disk.
        if (WalkProvesAbsent(rootDir, dir, d => DirExists(d, snap), d => ChildNames(d, snap))
            || WalkProvesAbsent(rootDir, dir, Directory.Exists, d => { Interlocked.Increment(ref snap.WarmListings); return FreshNames(d); }))
            return true;
        RecordRootFailure(rootName, subtreeDir, snap);
        return false;
    }

    /// <summary>One walk up from a directory that does not stat: true when the deepest ancestor that answers lists the
    /// name as missing, or a FILE holds it, or a second look finds it gone; false when that cannot be proved.</summary>
    static bool WalkProvesAbsent(string rootDir, string dir, Func<string, bool> exists, Func<string, HashSet<string>?> list)
    {
        // One level above the root, because the root's own folder can be the unreadable one.
        var floor = (Path.GetDirectoryName(rootDir.TrimEnd('\\')) ?? "").Length;
        var child = dir;
        for (var up = Path.GetDirectoryName(child); up is not null; child = up, up = Path.GetDirectoryName(up))
        {
            if (!exists(up))
            {
                if (up.Length <= floor) return false;      // walked past the root with nothing readable on the way
                continue;
            }
            var names = list(up);
            if (names is null) return false;               // the readable-looking ancestor would not list
            // A parent that lists its children and does not hold this name settles it: the directory is not on disk.
            if (!names.Contains(Path.GetFileName(child))) return true;
            if (File.Exists(child)) return true;           // a FILE where a directory was asked for — the directory is absent
            return GoneOnASecondLook(up, child);           // listed, yet it would not stat: gone since, or a real failure
        }
        return false;
    }

    /// <summary>The rare verdict pays for a fresh syscall: is the name still in its parent, uncached? Gone means the
    /// directory really is absent; still there (or a parent that now refuses) means the read failure is real.</summary>
    static bool GoneOnASecondLook(string parent, string child)
    {
        try { return !Directory.EnumerateFileSystemEntries(parent, Path.GetFileName(child)).Any(); }
        catch (DirectoryNotFoundException) { return true; }   // the parent went too, so the child is gone with it
        catch { return false; }                               // the parent refuses to list now: a real read failure
    }

    /// <summary>Directory.Exists, remembered for this build: the ancestor walk asks about the same few directories
    /// for every root and every prefix.</summary>
    static bool DirExists(string dir, Snapshot snap) => snap.Dirs.GetOrAdd(dir, Directory.Exists);

    /// <summary>One directory's child names for the absence verdict only, remembered for this build; null when it would not list.</summary>
    static HashSet<string>? ChildNames(string dir, Snapshot snap) =>
        snap.Children.GetOrAdd(dir, d =>
        {
            Interlocked.Increment(ref snap.WarmListings);
            return FreshNames(d);
        });

    static void RecordRootFailure(string rootName, string dir, string verb, Exception ex, Snapshot snap) =>
        snap.RootFailures[RootFailureKey(rootName, dir)] = RootFailureLine(rootName, dir, verb, ex);

    /// <summary>The failure with no exception to carry: nothing threw, the directory simply would not answer, so the
    /// line says what was observed and not why — permissions, a dangling junction and a path past MAX_PATH all land here.</summary>
    static void RecordRootFailure(string rootName, string dir, Snapshot snap) =>
        snap.RootFailures[RootFailureKey(rootName, dir)] =
            $"{rootName}: could not reach '{(dir.Length == 0 ? "\\" : dir)}' — the folder does not stat and this build cannot prove it absent, so what it provides is unknown";

    /// <summary>One entry per root per directory, so a root blocked everywhere is named once for each place it was asked about.</summary>
    static string RootFailureKey(string rootName, string dir) => rootName + "|" + dir;

    static string RootFailureLine(string rootName, string dir, string verb, Exception ex) =>
        $"{rootName}: could not {verb} '{(dir.Length == 0 ? "\\" : dir)}' — {Concise(ex)}";

    /// <summary>Normalize an asset path for matching: forward slashes to backslashes, drop a leading separator. Lenient — never throws.</summary>
    internal static string Normalize(string p) => (p ?? "").Replace('/', '\\').TrimStart('\\');

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
        for (int i = 0; i < kept.Count; i++)
            if (AsWindowsOpens(kept[i], last: i == kept.Count - 1).Length == 0)
                throw new ArgumentException($"expected a Data-relative asset path, got a segment of only dots or spaces, which Windows opens as no name: '{relPath}'");
        return string.Join('\\', kept);
    }

    /// <summary>A normalized path spelt segment by segment as Windows opens it, for the loose lookup.</summary>
    static string AsWindowsOpens(string rel)
    {
        var segs = rel.Split('\\');
        for (int i = 0; i < segs.Length; i++) segs[i] = AsWindowsOpens(segs[i], last: i == segs.Length - 1);
        return string.Join('\\', segs);
    }

    /// <summary>One segment as Windows opens it: the last loses trailing dots and spaces, any other one trailing dot not after a dot.</summary>
    static string AsWindowsOpens(string seg, bool last) =>
        last ? seg.TrimEnd('.', ' ')
             : seg.Length > 1 && seg[^1] == '.' && seg[^2] != '.' ? seg[..^1] : seg;

    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    /// <summary>Holds no handles at rest, so Dispose is a no-op; kept so a call site can treat the resolver as disposable.</summary>
    public void Dispose() { }
}

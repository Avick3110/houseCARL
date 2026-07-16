using System.Collections.Concurrent;
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
//  shipping the same facegen path is a CONTENTION worth surfacing (the contender list is the
//  diagnostic value) — though the dark-face bug proper is a record-winner-vs-file-winner desync
//  this phase only half-answers; and the precise loose-vs-BSA outcome has real MO2 edge cases (managed archives),
//  so the resolver models the common rule and FLAGS contention rather than asserting a
//  falsely-precise single winner. A BSA that can't be read is collected into BsaFailures and
//  surfaced, never silently treated as "absent".
//
//  CORNERSTONE (#3, Aaron 2026-06-14 — "I also think #3 is fine"): holds DERIVED DATA only
//  (cached BSA file-tables as string sets) and ZERO archive handles at rest. Each BSA is opened,
//  its table copied into a HashSet, and the reader dropped — Mutagen's reader does NOT keep the
//  archive mapped for the resolver's lifetime (the reader is not IDisposable in 0.53.1, so there
//  is no held handle to dispose — the asset-resolver-guard's at-rest arm proves the .bsa stays
//  renamable/deletable while the resolver is alive). Same ZERO-AT-REST property LoadOrderResolver
//  gives plugins, by a DIFFERENT mechanism — the plugin overlay there IS disposed; here there is
//  simply nothing held — so MO2/xEdit can still move/delete archives freely. Loose presence is a
//  PER-SUBTREE cache (the filename set under each requested
//  directory, in EACH loose root, warmed on first touch) — so a bulk facegen scan warms the small
//  facegendata subtree ONCE and the rest is O(1), not a File.Exists per (path × every enabled mod).
//  Both caches mtime-invalidated via RefreshIfStale (BSA file mtimes + each warmed subtree's dirs);
//  no live MO2 tracking, no daemon.
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
/// <see cref="Ambiguous"/> = more than one source provides it (FILE-layer contention) OR a loose copy coexists with a
/// BSA copy (the one loose-vs-BSA edge the model can't promise exactly). This is the file-winner half only; the
/// dark-face desync is record-winner-vs-file-winner (a later phase joins them), so Ambiguous flags contention to
/// VERIFY, not a confirmed desync.</summary>
public sealed record AssetHit(string RelPath, bool Exists, AssetProvider? Winner, IReadOnlyList<AssetProvider> Providers, bool Ambiguous);

/// <summary>A CONCRETE, on-disk source one provider supplies an asset from — enough to READ its bytes (the place-asset
/// auto-resolve, facegen-diagnostics Phase 3). For a LOOSE provider, <see cref="LooseFilePath"/> is the file on disk and
/// <see cref="ArchivePath"/>/<see cref="EntryPath"/> describe nothing. For a BSA provider, <see cref="ArchivePath"/> is the
/// .bsa on disk and <see cref="EntryPath"/> is the file inside it (read via <see cref="AssetResolver.TryReadArchiveEntry"/>);
/// <see cref="LooseFilePath"/> is null. <see cref="ProviderName"/>/<see cref="Kind"/> mirror <see cref="AssetProvider"/>.</summary>
public sealed record PlacementSource(string ProviderName, AssetKind Kind, string? LooseFilePath, string? ArchivePath, string EntryPath);

/// <summary>Concrete-source resolution of one asset path for PLACEMENT (place_asset's auto-resolve when no explicit
/// source= is given): every provider with its on-disk descriptor, winner FIRST (the same precedence
/// <see cref="AssetHit"/> reports — they share one code path), an <see cref="Ambiguous"/> flag (&gt;1 provider), and the
/// build-level <see cref="ReadIncomplete"/> caveat (a BSA failed to read this build, so a missing source may merely be
/// unscanned — Q3). <see cref="Sources"/> empty ⇒ nothing active provides this path.</summary>
public sealed record PlacementResolution(string RelPath, IReadOnlyList<PlacementSource> Sources, bool Ambiguous, bool ReadIncomplete);

/// <summary>An active BSA the resolver should consider: its full path, the plugin it loads with, and that plugin's
/// load-order rank (higher = later in load order = wins among BSAs). The service derives these; the probe injects them.</summary>
public sealed record ActiveArchive(string Path, string OwningPlugin, int PluginRank);

public sealed class AssetResolver : IDisposable
{
    readonly string _overwriteDir;                       // MO2 overwrite layer (top loose source); "" if none
    readonly string _modsDir;                            // base\mods
    readonly string _dataDir;                            // game Data (lowest loose source)
    readonly IReadOnlyList<string> _enabledMods;         // mod folder names, HIGHEST priority FIRST (Mo2Composition.EnabledMods order)
    readonly IReadOnlyList<ActiveArchive> _archives;     // active BSAs (path-deduped; winner decided by PluginRank)
    readonly IReadOnlyList<(string Name, string Dir)> _looseRoots;   // loose roots in PRECEDENCE order: overwrite > mods (priority) > Data

    /// <summary>One table-build's whole output, swapped in as a single reference write (the LoadOrderResolver
    /// snapshot discipline) so a concurrent Resolve never sees a half-rebuilt cache. Holds string sets only.
    /// internal (not private) so <see cref="AssetView"/>'s ctor can take it; never leaves the assembly.</summary>
    internal sealed class Snapshot
    {
        public readonly Dictionary<string, HashSet<string>> Tables;   // archive path → its file paths (normalized)
        public readonly Dictionary<string, DateTime> Mtimes;          // archive path → mtime at this build (freshness baseline)
        public readonly List<string> Failures;                        // archives that couldn't be read, with the reason (Q3)
        // Loose subtree cache — LAZILY warmed (one entry per requested directory), invalidated wholesale with the
        // snapshot. A fresh build starts empty and re-warms on demand. ConcurrentDictionary: concurrent Resolve calls
        // may race to warm the same subtree (the result is deterministic, so the redundant warm is harmless).
        public readonly ConcurrentDictionary<string, LooseSubtree> LooseCache;
        public Snapshot(Dictionary<string, HashSet<string>> tables, Dictionary<string, DateTime> mtimes, List<string> failures)
        { Tables = tables; Mtimes = mtimes; Failures = failures; LooseCache = new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>One subtree directory's loose resolution, warmed on first touch. <see cref="Present"/> = the filename
    /// sets of the roots that HAVE that subtree (in <see cref="_looseRoots"/> precedence order); <see cref="DirMtimes"/>
    /// = the mtime of EVERY root's copy of the subtree dir (MinValue if absent), so RefreshIfStale catches a content
    /// change to an existing dir AND a root gaining or losing the subtree.</summary>
    internal sealed class LooseSubtree
    {
        public readonly DateTime[] DirMtimes;                         // parallel to _looseRoots (length == root count)
        public readonly (int RootIndex, HashSet<string> Files)[] Present;   // roots that have the dir + ≥1 file, precedence order
        public LooseSubtree(DateTime[] dirMtimes, (int, HashSet<string>)[] present) { DirMtimes = dirMtimes; Present = present; }
    }

    volatile Snapshot _snap;

    /// <summary>Archives that could not be read this build (path: reason) — surfaced, never silently treated as empty (Q3).</summary>
    public IReadOnlyList<string> BsaFailures => _snap.Failures;

    /// <summary>True iff this build had archive-read failures (<see cref="BsaFailures"/> non-empty). The Q3 caveat for an
    /// Exists=false answer: an asset present ONLY in an archive that failed to read is indistinguishable from a truly
    /// absent one, so a consumer acting on "no facegen → the NPC is fine" must check this — an Exists=false is
    /// authoritative only when the read was complete. Surfaced at the resolver level (like LoadOrderResolver's
    /// LoadFailures), never forced into a per-answer field that would diverge from the codebase idiom.</summary>
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

    /// <summary>The loose roots in PRECEDENCE order (the same order Mo2LoadOrder.BuildFilenameMap walks): MO2's
    /// overwrite layer first (top of the VFS), then each enabled mod highest-priority-first, then the game Data folder.
    /// Fixed for the resolver's lifetime — the set only changes with the profile, which rebuilds the whole resolver.
    /// The subtree cache stores per-subtree mtimes parallel to THIS list.</summary>
    IReadOnlyList<(string Name, string Dir)> BuildLooseRoots()
    {
        var roots = new List<(string, string)>(_enabledMods.Count + 2);
        if (_overwriteDir.Length > 0) roots.Add(("overwrite", _overwriteDir));
        foreach (var mod in _enabledMods) roots.Add((mod, Path.Combine(_modsDir, mod)));
        if (_dataDir.Length > 0) roots.Add(("Data", _dataDir));
        return roots;
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

    /// <summary>Read one BSA's file table with Mutagen's native reader and copy it into a string set. No handle survives:
    /// Mutagen's reader does not keep the archive mapped for the resolver's lifetime (the at-rest guard arm proves the
    /// .bsa stays renamable/deletable while the resolver is alive). NOTE — IArchiveReader is NOT IDisposable in Mutagen
    /// 0.53.1, so the cast in the finally is INERT (it disposes nothing); it is kept belt-and-braces in case a future
    /// Mutagen makes the reader disposable, NOT as the release mechanism (the absence of a held handle is). Paths are
    /// normalized (backslash, no leading slash) for OrdinalIgnoreCase matching.</summary>
    static HashSet<string> ReadArchiveTable(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                set.Add(Normalize(file.Path));
        }
        finally { (reader as IDisposable)?.Dispose(); }           // INERT in 0.53.1 (reader isn't IDisposable) — see the summary
        return set;
    }

    /// <summary>Read ONE entry's bytes out of a BSA with Mutagen's NATIVE archive surface (no BSArch) — the single-entry
    /// extraction place_asset uses for a source inside a BSA (a CC NPC's facegen, base-game assets). Returns a FRESH
    /// byte[] (<c>IArchiveFile.GetBytes</c> allocates a new array — nothing to dispose), and holds ZERO handle at rest:
    /// the reader isn't IDisposable in 0.53.1 and nothing keeps the archive mapped after this returns, so the .bsa stays
    /// renamable/deletable (the cornerstone the place guard's at-rest arm proves on the EXTRACTION path, not just the
    /// table read). <paramref name="entryPath"/> is matched <see cref="Normalize"/>'d (backslash, OrdinalIgnoreCase)
    /// against the archive table. Returns null if the entry isn't in the archive; lets an archive that can't be opened/read
    /// THROW (loud, Q3) so the caller reports it — never a silent empty result.</summary>
    public static byte[]? TryReadArchiveEntry(string archivePath, string entryPath)
    {
        var want = Normalize(entryPath);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                // OrdinalIgnoreCase — BSA tables store paths lowercased, so an ordinal (case-sensitive) compare against a
                // mixed-case query (e.g. "Dawnguard.esm\0001A51A.nif") would MISS the entry; matches ReadArchiveTable's
                // case-insensitive table (Q3: a case mismatch must never read as "entry absent").
                if (string.Equals(Normalize(file.Path), want, StringComparison.OrdinalIgnoreCase))
                    return file.GetBytes();                        // fresh array; no held handle (see the summary)
            return null;                                           // archive read fine, entry simply not present
        }
        finally { (reader as IDisposable)?.Dispose(); }           // INERT in 0.53.1 — belt-and-braces, see ReadArchiveTable
    }

    /// <summary>Read MANY entries' bytes out of ONE archive in a single table walk — the batch sibling of
    /// <see cref="TryReadArchiveEntry"/> (native-pairing review finding: reading K entries via the single-entry call
    /// costs K archive opens × a full linear table scan each — O(K·M) against Skyrim - Misc.bsa's ~10k scripts, the
    /// dominant wall-clock of a whole-order .pex sweep). One reader, one pass over <c>Files</c>, wanted paths matched
    /// via a normalized set. Returns entryPath → bytes for the entries FOUND (keyed by the caller's own strings);
    /// a wanted entry absent from the archive is simply absent from the result (the caller Q3-names it). Same
    /// zero-handle-at-rest contract; an unopenable archive still THROWS (loud), never a silent empty map.</summary>
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

    /// <summary>Read the bytes of ONE resolved provider — a loose file off disk, or a single BSA entry via
    /// <see cref="TryReadArchiveEntry"/>. THE shared home for the loose-vs-BSA winner read (review finding: this
    /// logic had grown three near-verbatim copies — AssetRenameService.ReadWinner and the NPC-copy asset carry now
    /// both ride this one; LoadOrderService.ReadResolvedSource remains a desc-carrying sibling with probe-pinned
    /// message strings, folded here on its next deliberate touch). A named error (Q3) when the resolved copy
    /// vanished between resolve and read, or the archive can't be read.</summary>
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

    /// <summary>Resolve one Data-relative asset path: where it lives and which copy wins. See <see cref="AssetHit"/>.
    /// Single-shot — this call captures its own build; a caller making MANY reads in one logical operation should
    /// <see cref="Capture"/> once and read off the view so the scan and its failure list share one build.</summary>
    public AssetHit Resolve(string relPath) => Resolve(relPath, _snap);

    AssetHit Resolve(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);                 // ONE precedence path, winner first (shared with placement)
        if (sources.Count == 0)
            return new AssetHit(rel, false, null, Array.Empty<AssetProvider>(), false);
        // Project the concrete sources down to the display providers — the on-disk paths are placement-only.
        var providers = sources.Select(s => new AssetProvider(s.ProviderName, s.Kind)).ToList();
        // Ambiguous when >1 source provides it (contention), or a loose copy coexists with a BSA copy (the edge the
        // common-rule model can't promise exactly under MO2 managed archives).
        return new AssetHit(rel, true, providers[0], providers, providers.Count > 1);
    }

    /// <summary>The ONE precedence resolution both the display answer (<see cref="Resolve"/>) and the placement answer
    /// (<see cref="ResolveForPlacement"/>) ride, so the two can never drift: loose (overwrite &gt; mod-priority &gt; Data,
    /// first sighting wins) BEATS BSA (higher plugin rank wins; archive filename a deterministic tie-break). Returns each
    /// provider with its CONCRETE on-disk descriptor (loose file path; or .bsa path + the entry inside it), WINNER FIRST.
    /// <paramref name="rel"/> is already <see cref="NormalizeQueryPath"/>'d by the caller. Pure read over the snapshot.</summary>
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
                    LooseFilePath: null, ArchivePath: a.Path, EntryPath: rel), a.PluginRank));
        // Higher plugin rank wins; the archive filename is a DETERMINISTIC tie-break so equal-rank BSAs (a plugin can
        // ship more than one) order stably across runs rather than by hash/enumeration order.
        var bsaOrdered = bsa.OrderByDescending(b => b.rank)
                            .ThenBy(b => b.source.ProviderName, StringComparer.OrdinalIgnoreCase)
                            .Select(b => b.source);

        // winner: loose beats BSA; the list is winner first, then the rest in precedence.
        var providers = new List<PlacementSource>(loose);
        providers.AddRange(bsaOrdered);
        return providers;
    }

    /// <summary>Resolve one Data-relative asset path to its CONCRETE on-disk sources for PLACEMENT (place_asset): every
    /// provider winner-first with the file/archive path needed to read its bytes, plus the ambiguity + read-incomplete
    /// caveats. The auto-resolve half of the precise placer — exactly ONE source means an unambiguous copy to re-assert;
    /// &gt;1 (ambiguous) means the caller must pick a source= (Q3). Rejects a drive-rooted / '..' path loud, like
    /// <see cref="Resolve"/>. Single-shot against the current build; holds nothing.</summary>
    public PlacementResolution ResolveForPlacement(string relPath) => ResolveForPlacement(relPath, _snap);

    /// <summary>The snapshot-pinned form, so a captured <see cref="AssetView"/> resolves placement against the SAME
    /// build its <see cref="AssetView.Resolve"/> uses (the single-capture discipline) — the dialogue validator's SEQ
    /// lint reads the winning .seq's on-disk path off the view it already holds.</summary>
    internal PlacementResolution ResolveForPlacement(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);
        return new PlacementResolution(rel, sources, sources.Count > 1, snap.Failures.Count > 0);
    }

    /// <summary>Resolve many paths in one call (the facegen bulk scan), all against ONE pinned build so the whole scan
    /// is internally consistent even if a RefreshIfStale lands mid-scan (the LoadOrderResolver.Capture discipline).
    /// To pair the scan with its read-failure list off the same build, use <see cref="Capture"/>. Holds nothing past return.</summary>
    public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
    {
        var snap = _snap;                                         // pin ONE build for the whole scan
        return relPaths.Select(p => Resolve(p, snap)).ToList();
    }

    /// <summary>Every DISTINCT Data-relative file path that exists anywhere under <paramref name="prefix"/> (recursively),
    /// across all loose roots and all active BSAs — the directory ENUMERATION the voice carry needs (Resolve answers ONE
    /// path; this lists what's there). The winner per path is left to <see cref="ResolveForPlacement"/>; here we only need
    /// the set of paths that exist in ANY source. Reuses the <see cref="NormalizeQueryPath"/> escape guard. Pure read of the
    /// snapshot's BSA tables + a bounded recursive loose walk under the prefix; holds nothing. A loose root that won't
    /// enumerate contributes nothing (its absence flows through <see cref="ReadIncomplete"/>, never a silent half-answer).</summary>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix) => EnumerateUnder(prefix, _snap);

    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap)
    {
        var pre = NormalizeQueryPath(prefix);                    // drive-root / '..' rejected loud (Q3), backslash-normalized
        var withSep = pre + "\\";                                // match a SUBTREE, not a sibling whose name starts with 'pre'
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // loose: recurse each root's copy of the prefix dir (set-UNION across roots — the per-path winner is decided later).
        foreach (var (_, rootDir) in _looseRoots)
        {
            var baseDir = Path.Combine(rootDir, pre);
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                    found.Add(Normalize(f.Substring(rootDir.Length)));       // f starts with rootDir → the remainder is Data-relative
            }
            catch { /* a root that won't enumerate contributes nothing; not silently trusted (see the summary) */ }
        }

        // BSA: every table entry under the prefix (the cached tables ARE the authoritative archive listing, zero handle at rest).
        foreach (var t in snap.Tables.Values)
            foreach (var entry in t)
                if (entry.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
                    found.Add(entry);

        return found;
    }

    /// <summary>Capture the CURRENT build as a pinned read view (the LoadOrderResolver.Capture discipline): a bulk
    /// facegen scan and its read-failure list answer from ONE build, so a RefreshIfStale landing mid-scan cannot make
    /// the scan's hits and <see cref="BsaFailures"/> describe two different builds. Pure data over the immutable snapshot.</summary>
    public AssetView Capture() => new(this, _snap);

    /// <summary>A read view pinned to ONE captured build (see <see cref="Capture"/>). Resolve / ResolveMany and
    /// BsaFailures / ReadIncomplete all answer from the SAME snapshot, so they can never disagree about which build
    /// they describe. No handles — safe to hold for a call.</summary>
    public readonly struct AssetView
    {
        readonly AssetResolver _r;
        readonly Snapshot _s;
        internal AssetView(AssetResolver r, Snapshot s) { _r = r; _s = s; }   // only Capture() constructs

        /// <summary>Archives that could not be read this build (Q3) — see <see cref="AssetResolver.BsaFailures"/>.</summary>
        public IReadOnlyList<string> BsaFailures => _s.Failures;

        /// <summary>The Exists=false caveat for THIS build — see <see cref="AssetResolver.ReadIncomplete"/>.</summary>
        public bool ReadIncomplete => _s.Failures.Count > 0;

        public AssetHit Resolve(string relPath) => _r.Resolve(relPath, _s);

        /// <summary>The placement (concrete on-disk source) form pinned to THIS view's build — see
        /// <see cref="AssetResolver.ResolveForPlacement(string)"/>. The dialogue validator's SEQ lint uses it to read
        /// the winning .seq's loose path off the same capture as <see cref="Resolve"/>.</summary>
        public PlacementResolution ResolveForPlacement(string relPath) => _r.ResolveForPlacement(relPath, _s);

        public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
        {
            var r = _r; var s = _s;                              // locals — a struct's lambda can't capture 'this'
            return relPaths.Select(p => r.Resolve(p, s)).ToList();
        }

        /// <summary>Enumerate every Data-relative path under <paramref name="prefix"/> pinned to THIS view's build — see
        /// <see cref="AssetResolver.EnumerateUnder(string)"/>. The voice carry lists the plugin's voice files off the same
        /// capture it resolves their winners from, so the scan and its <see cref="ReadIncomplete"/> caveat agree.</summary>
        public IReadOnlyCollection<string> EnumerateUnder(string prefix) => _r.EnumerateUnder(prefix, _s);
    }

    /// <summary>Re-stat the inputs; if any changed, rebuild the snapshot and return true. Inputs = the active archives
    /// (a BSA's bytes changed) AND every WARMED loose subtree's directories across all roots (a facegen file added /
    /// removed, or a root gaining/losing the subtree — both move the dir's mtime). The cheap no-change path is just the
    /// stat sweep. A changed archive/mod SET (active plugins added/removed) is an ORDER change — the service rebuilds the
    /// whole resolver, like it does for LoadOrderResolver. One reference swap; an in-flight Resolve keeps its captured
    /// snapshot, and the rebuilt snapshot's loose cache re-warms lazily on the next touch.</summary>
    public bool RefreshIfStale()
    {
        var snap = _snap;
        bool stale = false;
        foreach (var a in _archives)
            if (!snap.Mtimes.TryGetValue(a.Path, out var m) || SafeMtime(a.Path) != m) { stale = true; break; }
        if (!stale)
            foreach (var kv in snap.LooseCache)                       // each warmed loose subtree — re-stat its dirs across all roots
                if (LooseSubtreeStale(kv.Key, kv.Value)) { stale = true; break; }
        if (!stale) return false;
        _snap = BuildTables();                                        // BSA tables re-read; loose cache starts empty, re-warms lazily
        return true;
    }

    /// <summary>True if a warmed subtree's loose layer changed since warm: any root's copy of the subtree dir has a
    /// different mtime than recorded (a content edit, or the dir appeared/disappeared). Bounded by (warmed subtrees ×
    /// roots) — RefreshIfStale is called per query-batch, not per path.</summary>
    bool LooseSubtreeStale(string subtreeDir, LooseSubtree st)
    {
        var roots = _looseRoots;
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = subtreeDir.Length == 0 ? roots[i].Dir : Path.Combine(roots[i].Dir, subtreeDir);
            if (SafeMtime(dir) != st.DirMtimes[i]) return true;
        }
        return false;
    }

    /// <summary>Warm one subtree directory's loose layer: for each root (precedence order) record the subtree dir's
    /// mtime (MinValue if absent, so an appear/disappear is detectable) and, when it exists with ≥1 file, its top-level
    /// filename set. Walks every root ONCE — the cost the per-subtree cache pays a single time so each later query in
    /// that subtree is an O(1) set lookup. Pure read of the filesystem; the result is cached in the snapshot.</summary>
    LooseSubtree WarmSubtree(string subtreeDir)
    {
        var roots = _looseRoots;
        var mtimes = new DateTime[roots.Count];
        var present = new List<(int, HashSet<string>)>();
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = subtreeDir.Length == 0 ? roots[i].Dir : Path.Combine(roots[i].Dir, subtreeDir);
            mtimes[i] = SafeMtime(dir);                              // MinValue if absent — baseline for an appear/disappear
            var files = SafeListFilenames(dir);
            if (files is { Count: > 0 }) present.Add((i, files));
        }
        return new LooseSubtree(mtimes, present.ToArray());
    }

    /// <summary>The top-level filenames in a directory (OrdinalIgnoreCase), or null if it doesn't exist / can't be read.
    /// Top-level only — a "subtree" here is the immediate parent dir of the queried asset, not a recursive tree.</summary>
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

    /// <summary>Normalize an asset path for matching: forward slashes → backslashes, drop a leading separator. Matching is
    /// OrdinalIgnoreCase (Windows + BSA tables are case-insensitive), so case is left as-is and compared case-insensitively.
    /// Lenient — applied to BSA-table entries (already archive-relative), so it never throws.</summary>
    static string Normalize(string p) => (p ?? "").Replace('/', '\\').TrimStart('\\');

    /// <summary>Public, reusable gate: normalize + VALIDATE a Data-relative path, REJECTING a drive-rooted or
    /// parent-escaping one — the SAME check <see cref="Resolve"/> applies (it delegates to <see cref="NormalizeQueryPath"/>),
    /// exposed so the place path validates a destination through this ONE validator rather than a divergent copy
    /// (place_asset writes to Path.Combine(modRoot, rel), so the same escape would write OUTSIDE the owned folder — Q3).
    /// Throws ArgumentException naming the bad input; returns the normalized (backslash, no leading sep) path.</summary>
    public static string ValidateRelPath(string relPath) => NormalizeQueryPath(relPath);

    /// <summary>Normalize AND validate a Data-relative QUERY path. After the lenient <see cref="Normalize"/>, REJECT a
    /// drive-rooted path ("C:\…") or one carrying a ".." segment — both make <c>Path.Combine(root, rel)</c> ESCAPE the
    /// loose roots and resolve a file OUTSIDE the load order, a silently-wrong answer. The resolver API is documented
    /// general-purpose, so a caller can hand it a bad path; we fail LOUD naming it (Q3) rather than resolve the wrong
    /// file. (UNC inputs aren't drive-rooted after the leading-separator trim and aren't a real caller shape here.)</summary>
    static string NormalizeQueryPath(string relPath)
    {
        var rel = Normalize(relPath);
        if (Path.IsPathRooted(rel))
            throw new ArgumentException($"expected a Data-relative asset path, got a drive-rooted path: '{relPath}'", nameof(relPath));
        foreach (var seg in rel.Split('\\'))
            if (seg == "..")
                throw new ArgumentException($"expected a Data-relative asset path, got a parent-escaping ('..') path: '{relPath}'", nameof(relPath));
        return rel;
    }

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

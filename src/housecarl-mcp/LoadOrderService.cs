using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

/// <summary>
/// Owns the load-order resolver's lifecycle for the server and is the single place the tools reach the proven
/// cores (<see cref="LoadOrderResolver"/> + <see cref="ReadEngine"/>; writes join in Beat C). This is the
/// server-side half of the §8.4 cleave: tools call clean methods here; here calls the core's public API.
///
/// • LAZY build — the ~10s/180MB index build is deferred to first use, so startup + tools/list are instant.
/// • FRESH — each query runs the cheap mtime stat-sweep (<see cref="LoadOrderResolver.RefreshIfStale"/>);
///   a mid-session plugin edit auto-rebuilds (~11s), no restart needed.
/// • THREAD-SAFE — the HTTP server is concurrent; build + refresh are serialized on one gate.
///
/// ORDER is the TRUE active order (§8.5), read statically from the MO2 profile's loadorder.txt + modlist.txt +
/// plugins.txt via <see cref="Mo2LoadOrder"/> — masters first → highest-priority winner last, the ~110 duplicate-name
/// plugins resolved by mod priority. No USVFS, no live MO2 state (both failed in the legacy build); the server reads
/// REAL plugin paths and runs standalone. Freshness is AUTONOMOUS + lazy: the cheap mtime sweep re-reads the profile on
/// the NEXT tool call whenever the user's MO2 edits changed it — no restart, no manual refresh step. See memory
/// project_mo2_load_order_resolution.
/// </summary>
public sealed class LoadOrderService : IDisposable
{
    // INSTANCE mode (the product default): one configured path — the MO2 instance folder — from which ProfileDir/ModsDir/
    // DataDir + the active profile are DERIVED (via Mo2Instance, reading ModOrganizer.ini), and a profile SWITCH is picked
    // up on the next tool call. EXPLICIT mode (dev / non-portable override): the three paths are configured directly and
    // _instanceDir stays null (no ini watch). UNCONFIGURED: neither was set — the server still BOOTS; every tool returns the
    // trained prompt (so houseCARL asks the user for the path) until housecarl_set_mo2_instance is called.
    string? _instanceDir;                          // INSTANCE-mode source of truth; null in explicit/unconfigured mode
    string _dataDir;                               // DERIVED (instance mode) or configured (explicit); mutable for a live profile switch
    string _modsDir;
    string _profileDir;
    string _profileName;                           // the active profile (instance mode: from selected_profile)
    string _overwriteDir = "";                     // MO2's overwrite layer (instance mode: derived; explicit mode: none) — hunt F9
    bool _configured;                              // false ⇒ tools return the trained prompt instead of resolving
    readonly UserConfigStore _store;               // the sole owner of houseCARL.user.json (MO2 instance dir + tool paths)
    readonly int _maxPlugins;
    readonly object _gate = new();
    // Serializes the WHOLE resolve→stage→commit of every .esp write (2026-06-12 hunt F2): the MCP SDK dispatches tool
    // calls CONCURRENTLY, and without this two same-name writes could allocate the same folder (UniqueStem TOCTOU) and
    // cross-commit through the fixed .housecarl-tmp staging path — R1's success message shipping R2's bytes. Writes are
    // seconds-long and rare; serializing them is correct (accuracy over perf). SetInstance takes it too, so an instance
    // switch can never tear a write in flight across instances. Lock order where both are held: _writeGate THEN _gate.
    readonly object _writeGate = new();
    LoadOrderResolver? _resolver;
    CorpusRulebook? _rulebook;
    IReadOnlyList<string> _orderWarnings = Array.Empty<string>();
    // facegen-diagnostics Phase 2: the VFS-aware asset resolver (housecarl_asset_status), built LAZILY and only on an
    // ASSET query — a pure-record session never pays for it — and kept fresh the same way _resolver is. Dropped +
    // rebuilt whenever the active profile changes (InvalidateAssetResolver in ReResolve / SetInstance): an enabled-mod
    // toggle changes the loose roots and the active-archive set, not just the plugin order. CHEAP to build (it reads BSA
    // file-TABLES, not the ~10s/180MB record index), so a full rebuild on a profile change is fine. See memory
    // project_facegen_diagnostics_resolver.
    AssetResolver? _assetResolver;
    IReadOnlyList<string> _assetWarnings = Array.Empty<string>();   // discovery warnings from the asset build (e.g. a Skyrim.ini we couldn't find → base BSAs unscanned)
    // Freshness baselines are the files' LAST-SEEN MTIMES compared by VALUE (!=), the same model the resolver itself
    // uses — NOT wall-clock stamps compared by ORDER (2026-06-12 hunt F8: `mtime > builtUtc` was blind to an mtime
    // REGRESSION, so MO2's "Restore Backup" — which restores a profile file with an OLDER mtime — stayed invisible
    // for the process lifetime). Each baseline is statted BEFORE the read it baselines (TOCTOU: a write landing
    // during/after the read shows as a changed mtime on the next check, never absorbed).
    DateTime[] _profileMtimes = new DateTime[ProfileFileNames.Length];   // per ProfileFileNames, recorded at each order build
    DateTime _iniMtime = DateTime.MinValue;                              // ModOrganizer.ini (instance-mode profile-switch baseline)
    IReadOnlyList<string> _resolvedPaths = Array.Empty<string>();   // ordered paths the current snapshot was built from (the cheap "did the order actually change?" check)

    static readonly string[] ProfileFileNames = { "loadorder.txt", "modlist.txt", "plugins.txt" };

    LoadOrderService(string? instanceDir, string dataDir, string modsDir, string profileDir, bool configured, int maxPlugins, UserConfigStore store)
    {
        _instanceDir = instanceDir;
        _dataDir = dataDir;
        _modsDir = modsDir;
        _profileDir = profileDir;
        _profileName = profileDir.Length > 0 ? Path.GetFileName(profileDir.TrimEnd('\\', '/')) : "";
        _configured = configured;
        _maxPlugins = maxPlugins;
        _store = store;
    }

    /// <summary>INSTANCE mode (product default): derive the load-order roots + active profile from ONE MO2 instance folder
    /// (lazily, on the first build). A null/blank <paramref name="instanceDir"/> ⇒ UNCONFIGURED (boots; tools prompt for the
    /// path). The instance is re-read on a profile switch, so a mid-session switch is followed.</summary>
    public static LoadOrderService WithInstance(string? instanceDir, int maxPlugins, UserConfigStore store)
        => new(string.IsNullOrWhiteSpace(instanceDir) ? null : instanceDir.Trim(),
               "", "", "", configured: !string.IsNullOrWhiteSpace(instanceDir), maxPlugins, store);

    /// <summary>EXPLICIT mode (dev / non-portable override): the three roots are configured directly; no ModOrganizer.ini is
    /// read and no profile-switch watch runs (the paths are fixed for the process lifetime).</summary>
    public static LoadOrderService WithExplicitPaths(string dataDir, string modsDir, string profileDir, int maxPlugins, UserConfigStore store)
        => new(null, dataDir, modsDir, profileDir, configured: true, maxPlugins, store);

    /// <summary>TEST SEAM (the harness' CI regression guards only): wrap a PREBUILT resolver so a guard can drive
    /// the service-layer query logic (CrossQuery's scan loop) on synthetic plugins — no MO2 profile, no user config
    /// on disk. Explicit-mode freshness checks no-op (no ini, empty profile dir); the caller owns the resolver's
    /// lifetime. Never used by the product.</summary>
    internal static LoadOrderService ForGuard(LoadOrderResolver resolver, UserConfigStore store)
    {
        var svc = new LoadOrderService(null, "", "", "", configured: true, maxPlugins: 0, store);
        svc._resolver = resolver;
        return svc;
    }

    /// <summary>Non-fatal warnings from the last order build (Q3) — e.g. a plugin the load order lists that no enabled
    /// mod provides (stale profile files). Surfaced, never swallowed. Empty until the resolver first builds.</summary>
    public IReadOnlyList<string> OrderWarnings => _orderWarnings;

    /// <summary>The write pre-flight rulebook (corpus.json), loaded once. CorpusPath is set absolute at startup (§8.4),
    /// so this resolves regardless of the MO2-launched process's CWD.</summary>
    CorpusRulebook Rulebook => _rulebook ??= CorpusRulebook.Load();

    /// <summary>The resolver, built on first access and kept fresh on every subsequent access. Throws (loud, Q3)
    /// if the configured roots yield no plugins.</summary>
    LoadOrderResolver Resolver
    {
        get
        {
            lock (_gate)
            {
                if (!_configured) throw NotConfigured();          // fresh install / empty config → every tool prompts for the MO2 path
                if (_resolver is null)
                {
                    EnsurePathsDerived();                         // instance mode: derive ProfileDir/ModsDir/DataDir + active profile from ModOrganizer.ini
                    // §8.5: the TRUE active order, read statically from the MO2 profile (loadorder.txt + modlist.txt +
                    // plugins.txt) — no VFS, no live MO2 state. See HousecarlCore.Mo2LoadOrder + memory
                    // project_mo2_load_order_resolution.
                    var profileMtimes = StatProfileFiles();      // stat BEFORE the read (TOCTOU): a profile write during the build is caught next call, not missed
                    var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir);
                    _orderWarnings = order.Warnings;
                    var paths = order.OrderedPaths;
                    if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();
                    if (paths.Count == 0)
                        throw new InvalidOperationException(
                            $"No active plugins resolved from the MO2 profile. ProfileDir='{_profileDir}', " +
                            $"ModsDir='{_modsDir}', DataDir='{_dataDir}'. {order.Warnings.Count} warning(s). Check " +
                            "HouseCarl config and that MO2 has written loadorder.txt/modlist.txt (a refresh/re-sort in MO2).");
                    _resolver = LoadOrderResolver.Build(paths);
                    _resolvedPaths = paths;
                    _profileMtimes = profileMtimes;
                }
                else if (Monitor.TryEnter(_writeGate))
                {
                    // Lazy freshness, run on each tool call once the snapshot exists — but DEFERRED while a WRITE is
                    // in flight (PR #51 review note): a refresh here can rebuild the index — transiently mmap-opening
                    // every plugin INCLUDING the file a concurrent write is serializing (the PR #24 "no mapped handle
                    // on the target survives the serialize" invariant, breached from the read path) — and dispose-swap
                    // the resolver that write captured. TryEnter probes the write gate WITHOUT blocking: it cannot
                    // deadlock (no blocking _gate→_writeGate wait — the established blocking order stays _writeGate
                    // THEN _gate), and Monitor reentrancy keeps the write's OWN entry refresh working (it holds
                    // _writeGate, so its TryEnter succeeds). A skipped refresh serves the last good snapshot and
                    // re-checks on the next call — the freshness contract is per-call lazy, so deferral behind a
                    // seconds-long write is honest staleness, never wrongness (freshness-capture-guard arm 5).
                    try
                    {
                        RefreshOnProfileChange();     // to-do #6: lazy profile-membership refresh on THIS call (cheap-check first)
                        _resolver.RefreshIfStale();   // plugin-CONTENT freshness: cheap stat sweep; rebuilds if a plugin's bytes changed
                    }
                    finally { Monitor.Exit(_writeGate); }
                }
                return _resolver;
            }
        }
    }

    // ---- facegen-diagnostics Phase 2: VFS asset resolution (housecarl_asset_status) ----------------------

    /// <summary>The VFS-aware asset resolver, built on first ASSET query and kept fresh on every subsequent one — the
    /// asset twin of <see cref="Resolver"/>. Runs the SAME profile-freshness driver (a switch / toggle / re-sort drops it
    /// via <see cref="ReResolve"/> → <see cref="InvalidateAssetResolver"/>, so it rebuilds against the new profile), then
    /// its own cheap BSA-byte / warmed-loose-subtree content sweep. Crucially it does NOT force the heavy
    /// <see cref="Resolver"/> build — an asset-only query stays cheap (the freshness driver is null-safe for _resolver).
    /// The getter takes <see cref="_gate"/> (reentrant), so callers need not pre-hold it.</summary>
    AssetResolver Assets
    {
        get
        {
            lock (_gate)
            {
                if (!_configured) throw NotConfigured();           // fresh install → the tool returns the trained prompt instead
                EnsurePathsDerived();                              // derive the roots on first use (instance mode)
                // Profile freshness (switch / toggle / re-sort) — shared with the record path, deferred behind an in-flight
                // write like the record refresh. ReResolve is null-safe for _resolver, so this FOLLOWS a profile change
                // WITHOUT building the record index, and drops _assetResolver when the active set changed.
                if (Monitor.TryEnter(_writeGate))
                {
                    try { RefreshOnProfileChange(); }
                    finally { Monitor.Exit(_writeGate); }
                }
                if (_assetResolver is null)
                {
                    _assetResolver = BuildAssetResolverLocked();
                }
                else if (Monitor.TryEnter(_writeGate))
                {
                    try { _assetResolver.RefreshIfStale(); }       // BSA-byte / warmed-loose-subtree content freshness
                    finally { Monitor.Exit(_writeGate); }
                }
                return _assetResolver;
            }
        }
    }

    /// <summary>Build the asset resolver from the current roots: discover the active BSAs (co-name + Skyrim.ini base
    /// archives, VFS-resolved + ranked — <see cref="ArchiveDiscovery"/>) and read the enabled-mod priority list, both
    /// from the same cheap static profile read the record path uses. The gamePath (for the game-dir Skyrim.ini fallback)
    /// is DataDir's parent (DataDir = gamePath\Data). Caller holds <see cref="_gate"/>.</summary>
    AssetResolver BuildAssetResolverLocked()
    {
        var comp = Mo2LoadOrder.ReadComposition(_profileDir);                       // EnabledMods (priority) — cheap text parse
        var gamePath = _dataDir.Length > 0 ? Path.GetDirectoryName(_dataDir.TrimEnd('\\', '/')) ?? "" : "";
        var discovery = ArchiveDiscovery.Discover(_profileDir, _modsDir, _dataDir, _overwriteDir, gamePath);
        _assetWarnings = discovery.Warnings;
        return AssetResolver.Build(_overwriteDir, _modsDir, _dataDir, comp.EnabledMods, discovery.Archives);
    }

    /// <summary>Drop the asset resolver so the next asset query rebuilds it — the active-mod/archive SET changed
    /// (AssetResolver.RefreshIfStale only catches a BSA's bytes / a warmed subtree, not a membership change). No-op when
    /// none is built (a pure-record session never pays for the asset resolver). Caller holds <see cref="_gate"/>.</summary>
    void InvalidateAssetResolver() { _assetResolver?.Dispose(); _assetResolver = null; }

    /// <summary>Resolve a batch of Data-relative asset paths through the MO2 VFS (housecarl_asset_status): for each,
    /// which source provides it and which copy WINS (loose beats BSA; among BSAs the higher plugin rank). ONE
    /// <see cref="AssetResolver.Capture"/> for the whole batch, so every path AND the build-level BsaFailures /
    /// ReadIncomplete caveat describe a single build (Q3). A drive-rooted or '..'-escaping path is a per-path
    /// recoverable error (Q3), never a batch failure.</summary>
    public AssetStatusData AssetStatus(IReadOnlyList<string> relPaths)
    {
        lock (_gate)
        {
            var view = Assets.Capture();                          // reentrant gate; build/refresh the asset resolver once for the batch
            var results = new List<AssetPathResult>(relPaths.Count);
            foreach (var raw in relPaths)
            {
                var p = (raw ?? "").Trim();
                try { results.Add(new AssetPathResult(p, view.Resolve(p), null)); }
                catch (ArgumentException ex) { results.Add(new AssetPathResult(p, null, ex.Message)); }   // bad path → per-path Q3 note
            }
            return new AssetStatusData(results, view.BsaFailures, view.ReadIncomplete, _assetWarnings, _profileName);
        }
    }

    // ---- facegen-diagnostics Phase 3: place an asset so the correct copy WINS the VFS (housecarl_place_asset) ----

    /// <summary>Place one-or-more assets (FaceGen .nif/.dds, or any Data-relative file) into a NEW houseCARL-owned MO2 mod
    /// folder so the CORRECT copy can win the VFS (housecarl_place_asset = one; housecarl_bulk_place_asset = many). For
    /// each request: resolve its current providers (auto-resolve a source when none was named — sole provider used, &gt;1
    /// refused as ambiguous, 0 refused with guidance), read the source bytes IN PROCESS (a loose file, or a single entry
    /// out of a BSA via native Mutagen), and write them CRASH-ATOMICALLY (<see cref="AtomicFile.WriteAllBytes"/>) under the
    /// owned folder. Originals untouched (we only ever write a fresh / houseCARL-owned folder). NON-DESTRUCTIVE on failure:
    /// a fresh folder that ended up with NOTHING placed is removed (no orphan); a partial one is kept + named. Q3 honesty:
    /// "wrote it" ≠ "it wins" — the fresh mod must be ENABLED + SORTED above the current winner, which the render reports;
    /// this never claims the fix took effect on write. Serialized on the write gate (one placement batch at a time).</summary>
    public PlaceOutcome PlaceAssets(IReadOnlyList<PlaceRequest> requests, string? patchName, string? into)
    {
        if (requests is null || requests.Count == 0) return PlaceOutcome.Fail("no assets to place.");

        lock (_writeGate)                                                 // hunt F2 sibling: one placement batch at a time, resolve->stage->commit
        {
            // PRECONDITION: _writeGate is held for the WHOLE method. ResolvePatchModFolder and the `Assets` getter each
            // take-and-release _gate, so this method straddles two _gate sections — safe ONLY because _writeGate excludes
            // every other writer and instance-switch throughout, so no profile refresh can land between them. Do not call
            // PlaceOne/Assets here outside this _writeGate hold.
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_Assets"); }   // neutral default stem (general asset placer); the facegen skill passes its own patch_name
            catch (InvalidOperationException ex) { return PlaceOutcome.Fail(ex.Message); }

            // ONE asset build for the whole batch (auto-resolve sources + the post-write winner report), reentrant on _gate.
            AssetResolver resolver; IReadOnlyList<string> warnings;
            try { lock (_gate) { resolver = Assets; warnings = _assetWarnings; } }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);              // nothing placed yet → a fresh folder is an orphan
                return PlaceOutcome.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"
                    + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."));
            }

            var results = new List<PlaceResult>(requests.Count);
            int placed = 0;
            foreach (var req in requests)
            {
                var r = PlaceOne(req, resolver, rf.OutputDir);
                results.Add(r);
                if (r.Placed) placed++;
            }

            // Nothing placed into a FRESH folder → remove the orphan (the .esp F4 / rider H2 principle). A reused into=
            // folder (the user owns it) is never touched. A partial fresh folder is kept and its path surfaced.
            string? leftover = placed == 0 ? RemoveOrNameRiderResidue(rf) : null;
            return new PlaceOutcome(results, placed > 0 ? rf.ModFolder : null, warnings, leftover, null);
        }
    }

    /// <summary>Place ONE asset: validate the destination rel-path (reject drive-rooted/'..' through the resolver's own
    /// gate, Q3), get the source bytes (explicit source= or auto-resolve), and write them crash-atomically under
    /// <paramref name="outDir"/>. Reports the CURRENT VFS winner so the caller knows what to sort the fresh mod above —
    /// the placed file does NOT win until the mod is enabled + sorted (the fresh folder isn't in the active profile yet).
    /// A per-asset failure is a recoverable named error, never a thrown batch abort.</summary>
    PlaceResult PlaceOne(PlaceRequest req, AssetResolver resolver, string outDir)
    {
        string rel;
        try { rel = AssetResolver.ValidateRelPath(req.AssetPath); }
        catch (ArgumentException ex) { return PlaceResult.Fail(req.AssetPath, ex.Message); }

        var res = resolver.ResolveForPlacement(rel);                     // rel already validated — won't throw
        var winner = res.Sources.Count > 0 ? DescribeSource(res.Sources[0]) : null;

        // ---- source bytes: explicit source= wins; else auto-resolve the sole provider ----
        byte[] bytes; string sourceDesc;
        var explicitSrc = req.Source?.Trim();
        if (!string.IsNullOrEmpty(explicitSrc))
        {
            var (b, desc, err) = ReadExplicitSource(explicitSrc!, rel);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!; sourceDesc = desc!;
        }
        else
        {
            if (res.Sources.Count == 0)
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides '{rel}', so there is no copy to auto-place. Pass source= the correct copy "
                    + "(a loose file path, or '<archive.bsa>|<entry>', or a '.bsa' path)."
                    + (res.ReadIncomplete ? " NOTE: a BSA failed to read this build, so a source may merely be unscanned (see the warnings)." : ""),
                    winner);
            if (res.Ambiguous)
                return PlaceResult.Fail(rel,
                    $"{res.Sources.Count} sources provide '{rel}' — ambiguous, so place_asset will not guess which copy is correct (the skill decides). "
                    + $"Pass source= one of: {string.Join("; ", res.Sources.Select(SourceHint))}.",
                    winner);
            var (b, desc, err) = ReadResolvedSource(res.Sources[0]);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!; sourceDesc = desc!;
        }

        // ---- crash-atomic place under the owned folder (originals untouched; same-volume staging done in core) ----
        var dest = Path.Combine(outDir, rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            AtomicFile.WriteAllBytes(dest, bytes);
        }
        catch (Exception ex) { return PlaceResult.Fail(rel, $"could not write '{rel}' into the patch folder: {ex.Message}", winner); }

        // ---- integrity (Q3: THIS run wrote it; the on-disk size matches the source bytes — no false success) ----
        // Belt-and-braces truncation / short-write detection — NOT a content hash (the bytes are in-memory and the swap is
        // atomic, so a same-length corruption isn't a reachable failure of this path; a size mismatch would mean the OS
        // wrote fewer bytes than handed). Defensive, not the primary guarantee (that's AtomicFile's crash-atomic swap).
        long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
        if (size != bytes.Length)
            return PlaceResult.Fail(rel,
                $"wrote '{rel}' but its on-disk size ({size}) does not match the {bytes.Length} source byte(s) — verify before relying on it.", winner);
        return new PlaceResult(rel, true, bytes.Length, sourceDesc, winner, null);
    }

    /// <summary>Read an EXPLICIT source= the caller named. Forms: "&lt;archive.bsa&gt;|&lt;entry&gt;" (a specific BSA
    /// entry, split on the FIRST '|'); a path ending ".bsa" (the entry is the destination rel-path — the FaceGen case,
    /// where the entry inside the BSA IS the Data-relative path); any other path (a loose file on disk). Returns the bytes
    /// + a human description, or a NAMED error (Q3) for a missing file / missing entry / unreadable archive.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadExplicitSource(string source, string destRel)
    {
        source = source.Trim();
        int bar = source.IndexOf('|');
        if (bar >= 0)
            return ReadBsaEntry(source[..bar].Trim().Trim('"'), source[(bar + 1)..].Trim().Trim('"'));
        // Strip surrounding quotes BEFORE the .bsa-vs-loose routing decision (NOT inside each branch): a quoted ".bsa"
        // path ends in '"' not ".bsa", so routing on the raw string would read the WHOLE archive as a loose file and
        // place it as the asset — a silent-wrong placement that passes the size check (Q3). The ONE trim point.
        source = source.Trim('"');
        if (source.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase))
            return ReadBsaEntry(source, destRel);                        // .bsa with no explicit entry → entry := the destination path
        string path;
        try { path = Path.GetFullPath(source); }
        catch (Exception ex) { return (null, null, $"source '{source}' is not a usable path: {ex.Message}"); }
        if (!File.Exists(path)) return (null, null, $"source file not found: '{path}'.");
        try { return (File.ReadAllBytes(path), $"loose file {path}", null); }
        catch (Exception ex) { return (null, null, $"could not read source file '{path}': {ex.Message}"); }
    }

    /// <summary>Read the bytes of an AUTO-resolved provider (the sole VFS provider when no source= was named). A loose
    /// provider reads off disk; a BSA provider extracts its single entry natively. A named error (Q3) if the resolved copy
    /// vanished between resolve and read, or the archive can't be read.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadResolvedSource(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose)
        {
            var p = s.LooseFilePath!;
            if (!File.Exists(p)) return (null, null, $"the resolved loose source '{p}' is no longer on disk.");
            try { return (File.ReadAllBytes(p), $"loose file {p} (from {s.ProviderName})", null); }
            catch (Exception ex) { return (null, null, $"could not read resolved source '{p}': {ex.Message}"); }
        }
        return ReadBsaEntry(s.ArchivePath!, s.EntryPath);
    }

    /// <summary>Read one entry out of a BSA (native Mutagen, no BSArch, zero handles at rest — see
    /// <see cref="AssetResolver.TryReadArchiveEntry"/>). Named errors (Q3) for a missing archive, an entry not inside it,
    /// or an unreadable archive.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadBsaEntry(string archive, string entry)
    {
        string ap;
        try { ap = Path.GetFullPath(archive.Trim('"')); }
        catch (Exception ex) { return (null, null, $"source archive '{archive}' is not a usable path: {ex.Message}"); }
        if (!File.Exists(ap)) return (null, null, $"source archive not found: '{ap}'.");
        try
        {
            var b = AssetResolver.TryReadArchiveEntry(ap, entry);
            if (b is null) return (null, null, $"entry '{entry}' not found inside archive '{Path.GetFileName(ap)}'.");
            return (b, $"{Path.GetFileName(ap)}|{entry}", null);
        }
        catch (Exception ex) { return (null, null, $"could not read archive '{Path.GetFileName(ap)}': {ex.Message}"); }
    }

    /// <summary>A human label for the current winner (the sort target). "ModX (loose)" / "Y.bsa (BSA)".</summary>
    static string DescribeSource(PlacementSource s) => $"{s.ProviderName} ({(s.Kind == AssetKind.Bsa ? "BSA" : "loose")})";

    /// <summary>A copy-pasteable source= hint for an ambiguous-provider refusal: a BSA provider needs its archive PATH (the
    /// display name is just the filename), so name it as a BSA the caller must give the full path of; a loose provider's
    /// on-disk path is exact.</summary>
    static string SourceHint(PlacementSource s) => s.Kind == AssetKind.Bsa
        ? $"the BSA '{s.ProviderName}' (give its full path, or '<path>|{s.EntryPath}')"
        : $"'{s.LooseFilePath}'";

    /// <summary>Whole-order stats (forces the lazy build). For the server's stand-up / health check.</summary>
    public (int plugins, int records, int conflicts, int maxDepth, IReadOnlyList<string> loadFailures) Stats()
    {
        var view = Resolver.Capture();          // ONE build for every counter in the line (HCBR-2026-06-11-02)
        return (view.PluginCount, view.RecordCount, view.ConflictCount, view.MaxDepth, view.LoadFailures);
    }

    /// <summary>Diagnostic snapshot for housecarl_load_order_status: the CURRENT enabled/disabled composition (read fresh
    /// from the profile text files — cheap, no folder walk, so a just-toggled mod/plugin shows immediately), plus the
    /// resolver's resolved-plugin count + Q3 warnings from its last build, plus a staleness flag if the profile files
    /// changed since that build (Q3 — never present a stale picture as current). Forces the lazy resolver build.</summary>
    public LoadOrderStatusData StatusData()
    {
        // The view AND the per-build fields beside it (warnings / staleness / profile dir) are snapshotted under ONE
        // gate hold (2026-06-12 hunt F6): they used to be read outside the gate after Capture() returned, so a
        // concurrent freshness rebuild landing in that gap could compose one status line from TWO adjacent builds —
        // the count from one, the warnings beside it from another. The fresh composition stays OUTSIDE the gate by
        // design (it is documented as always-current and is not judged against the resolver's build).
        LoadOrderResolver.IndexView view; IReadOnlyList<string> warnings; bool profileChanged; string profileDir; string profileName; string? instanceDir;
        lock (_gate)
        {
            view = Resolver.Capture();                             // force build/refresh; ONE build for count + exclusions (HCBR-2026-06-11-02)
            warnings = _orderWarnings;
            profileChanged = ProfileFilesChanged();
            profileDir = _profileDir;
            profileName = _profileName;                            // captured under the SAME gate (hunt F6) — one snapshot, never re-derived at render
            instanceDir = _instanceDir;                            // the configured MO2 instance folder; null ⇒ explicit-paths / unconfigured mode
        }
        var comp = Mo2LoadOrder.ReadComposition(profileDir);       // FRESH composition (always current)
        return new LoadOrderStatusData(
            comp, warnings, view.PluginCount, _maxPlugins, profileChanged, profileDir, profileName, instanceDir, view.ExcludedPlugins);
    }

    /// <summary>Inspect a NAMED profile's enabled/disabled composition WITHOUT switching to it (9.2: "can't inspect an
    /// inactive profile") — INSTANCE MODE ONLY. The profiles root is the PARENT of the active profile's dir, so MO2's
    /// base_directory redirect is honored by construction (the active ProfileDir already incorporates it) and a stale
    /// active-profile dir doesn't matter — every profile is a sibling folder there. Reads with the cheap text-only
    /// <see cref="Mo2LoadOrder.ReadComposition"/>, NOT <see cref="Mo2LoadOrder.Build"/> (Build walks every enabled mod
    /// folder — thousands of dir enumerations) — so inspecting an inactive profile never builds the record index and never
    /// changes the active profile. EXPLICIT-paths mode has no profiles root (the dir is configured arbitrarily), so a named
    /// read REFUSES LOUD there rather than enumerate a non-profiles folder. A <paramref name="requested"/> name matching no
    /// profile is reported with the available names (Q3 — never a silently-empty composition); a null/blank name returns
    /// just the available list (the discovery affordance on the default status). Case-insensitive name match.</summary>
    public NamedProfileResult NamedProfileComposition(string? requested)
    {
        string? instanceDir; string profilesRoot;
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();              // fresh install → the tool returns the trained prompt
            EnsurePathsDerived();                                 // instance mode: derive the ACTIVE ProfileDir (cheap ini read; throws Q3 if the instance is unusable)
            instanceDir = _instanceDir;
            profilesRoot = instanceDir is null ? "" : (Path.GetDirectoryName(_profileDir.TrimEnd('\\', '/')) ?? "");
        }

        var name = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (instanceDir is null)                                  // explicit-paths mode — no profiles root (refuse loud; the tool renders the named-mode-only message)
            return new NamedProfileResult(InstanceMode: false, AvailableProfiles: Array.Empty<string>(), RequestedName: name, ResolvedProfileDir: null, Composition: null, Warnings: Array.Empty<string>());

        var available = ListProfiles(profilesRoot);              // directory listing OUTSIDE the gate (like StatusData's ReadComposition) — no lock held over I/O
        if (name is null)                                        // no name → the discovery list only (default-status affordance)
            return new NamedProfileResult(true, available, null, null, null, Array.Empty<string>());

        var match = available.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)                                       // named profile not found → Q3: report it WITH the available names, never an empty composition
            return new NamedProfileResult(true, available, name, null, null, Array.Empty<string>());

        var dir = Path.Combine(profilesRoot, match);
        var warnings = new List<string>();                       // surface read notes (e.g. a missing modlist.txt) — so a 0-mods inspected profile isn't silently mistaken for empty (Q3)
        var comp = Mo2LoadOrder.ReadComposition(dir, warnings);  // cheap text parse of THAT profile's loadorder/modlist/plugins — no index build, no switch
        return new NamedProfileResult(true, available, match, dir, comp, warnings);
    }

    /// <summary>The USABLE profile names under <paramref name="profilesRoot"/> — each MO2 profile is one subfolder, and a
    /// profile that's been opened at least once has a loadorder.txt (the same validity signal <see cref="Mo2Instance"/> uses
    /// for the ACTIVE profile). Folders WITHOUT one — a never-opened profile, or a stray non-profile dir — are skipped, so
    /// the list never OFFERS (and a name match never LANDS ON) a folder that would read back as an all-zero composition
    /// (Q3: don't present an uninitialized folder as an empty profile). Sorted case-insensitively. Never throws — an
    /// unreadable/absent root yields an empty list, so the caller surfaces "no profiles" honestly rather than failing the
    /// whole status read.</summary>
    static IReadOnlyList<string> ListProfiles(string profilesRoot)
    {
        if (profilesRoot.Length == 0) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(profilesRoot)
                .Where(d => File.Exists(Path.Combine(d, "loadorder.txt")))   // an opened MO2 profile has loadorder.txt — skip stray/never-opened folders (Q3, accuracy over the per-folder stat)
                .Select(d => Path.GetFileName(d.TrimEnd('\\', '/')))
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }                  // root vanished / access denied — empty, not a thrown status read
    }

    /// <summary>True if any of the three MO2 profile files' mtimes DIFFERS from the last build's baseline — the user
    /// toggled mods/plugins, re-sorted, or RESTORED A BACKUP since, so the resolver's resolved set is behind the live
    /// profile. Compared by value (!=), like the resolver's own plugin sweep: a restored backup carries an OLDER
    /// mtime, which an is-newer comparison was blind to (hunt F8). Caller holds <see cref="_gate"/>.</summary>
    bool ProfileFilesChanged()
    {
        if (_profileDir.Length == 0) return false;                 // guard seam / not yet derived — nothing to compare against
        for (int i = 0; i < ProfileFileNames.Length; i++)
            if (SafeMtime(Path.Combine(_profileDir, ProfileFileNames[i])) != _profileMtimes[i]) return true;
        return false;
    }

    /// <summary>The three profile files' current mtimes, in <see cref="ProfileFileNames"/> order — the freshness
    /// baseline a build records. Stat BEFORE the read it baselines (TOCTOU). Caller holds <see cref="_gate"/>.</summary>
    DateTime[] StatProfileFiles()
    {
        var m = new DateTime[ProfileFileNames.Length];
        for (int i = 0; i < ProfileFileNames.Length; i++) m[i] = SafeMtime(Path.Combine(_profileDir, ProfileFileNames[i]));
        return m;
    }

    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>To-do #6 — LAZY freshness, run on each tool call once the snapshot exists. Two signals, both cheap-mtime:
    /// (1) instance mode — did the user SWITCH PROFILES (ModOrganizer.ini changed)? then re-derive the roots + re-resolve
    /// against the new profile (<see cref="RederiveIfIniChanged"/>). (2) did the ACTIVE profile's files change (a toggle /
    /// re-sort)? then CHEAPLY re-resolve, paying the ~12s deep re-index ONLY when the resolved order actually changed — so a
    /// no-plugin toggle, or a change that nets back to the same order, costs ~nothing. NEVER fires BETWEEN tool calls (no
    /// watcher / no loop), so an actively-sorting/switching user can't make the server thrash. Caller holds <see cref="_gate"/>;
    /// <see cref="_resolver"/> is non-null.</summary>
    void RefreshOnProfileChange()
    {
        if (RederiveIfIniChanged()) return;                      // instance mode: a profile SWITCH already re-derived + re-resolved
        if (!ProfileFilesChanged()) return;                      // nothing touched the active profile → nothing to do
        ReResolve();
    }

    /// <summary>Instance mode only: if ModOrganizer.ini changed since we last read it AND the user switched profiles (or
    /// moved the game path), re-derive ProfileDir/ModsDir/DataDir + the active profile and re-resolve against the new
    /// profile. This is how a mid-session profile switch is followed — lazily, on the NEXT tool call, by the SAME cheap-mtime
    /// model as the per-profile-file check. Returns true iff it handled a switch (caller then skips the per-file check).
    /// Tolerates a transient/invalid read (MO2 mid-write): keeps the last good set and retries next call. Caller holds the gate.</summary>
    bool RederiveIfIniChanged()
    {
        if (_instanceDir is null) return false;                  // explicit/override mode — no ini to watch
        var ini = Mo2Instance.IniPath(_instanceDir);
        if (!File.Exists(ini)) return false;                     // missing/mid-replace → keep last good, retry next call
        var iniMtime = SafeMtime(ini);                           // stat BEFORE the read (TOCTOU): an ini write during/after TryResolve is caught next call
        if (iniMtime == _iniMtime) return false;                 // compared by VALUE — a restored-backup ini (OLDER mtime) is a change too (hunt F8)
        if (!Mo2Instance.TryResolve(_instanceDir, out var p) || p is null) return false;   // mid-write/invalid → keep last good, retry next call
        _iniMtime = iniMtime;                                    // advance only on a clean read
        bool switched = !PathEq(p.ProfileDir, _profileDir) || !PathEq(p.ModsDir, _modsDir) || !PathEq(p.DataDir, _dataDir)
                        || !PathEq(p.OverwriteDir, _overwriteDir);
        if (!switched) return false;                             // ini touched but nothing we resolve from changed
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        InvalidateClassParents();                                // the mods tree may have moved — drop the cached hierarchy with it
        ReResolve();                                             // a new profile ⇒ the order differs ⇒ ReResolve deep-re-indexes
        return true;
    }

    /// <summary>The cheap re-read against the CURRENT profile roots: re-list the winning plugin paths from the text files,
    /// and pay the ~12s deep re-index ONLY when the resolved set/order actually changed. Caller holds the gate;
    /// <see cref="_resolver"/> is non-null. Used by both freshness signals (active-profile change + profile switch).</summary>
    void ReResolve()
    {
        var profileMtimes = StatProfileFiles();                  // stat BEFORE the read (TOCTOU): a write during the re-read is caught next call, not missed
        var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir);
        var paths = order.OrderedPaths;
        if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();

        if (paths.Count > 0 && !paths.SequenceEqual(_resolvedPaths, StringComparer.OrdinalIgnoreCase))
        {
            // The active set/order genuinely changed → re-take the snapshot (the ~12s deep re-index). Build FIRST so the
            // old snapshot survives if it throws; only then dispose + swap. Guarded on `_resolver is not null`: an
            // ASSET-only query (Phase 2) can drive this re-resolve before any record index exists — it must NOT pay the
            // heavy build here (the record getter builds fresh against these paths on its own next call). The record
            // path always has a non-null _resolver when it reaches here, so its behaviour is unchanged.
            InvalidateAssetResolver();   // the active-mod/archive set changed → the asset resolver rebuilds lazily
            if (_resolver is not null)
            {
                var rebuilt = LoadOrderResolver.Build(paths);
                _resolver.Dispose();
                _resolver = rebuilt;
            }
            _resolvedPaths = paths;
            _orderWarnings = order.Warnings;
            _profileMtimes = profileMtimes;
        }
        else if (paths.Count > 0)
        {
            // The profile was touched but the resolved PLUGIN order is identical (e.g. a no-plugin mod toggled) — no deep
            // re-index. But a plugin-less toggle still changes the loose roots / active-archive set, so the asset resolver
            // is dropped to rebuild; just advance the freshness baseline so the staleness flag clears.
            InvalidateAssetResolver();
            _orderWarnings = order.Warnings;
            _profileMtimes = profileMtimes;
        }
        // paths.Count == 0 → almost certainly a transient mid-write read; keep the last good snapshot and DON'T advance the
        // baseline, so the next tool call re-checks and self-recovers once MO2 finishes writing.
    }

    /// <summary>Instance mode: on the first resolver build, read ModOrganizer.ini and derive ProfileDir/ModsDir/DataDir +
    /// the active profile — throwing a clear Q3 message (naming what's missing) if the configured instance isn't usable.
    /// Explicit mode (paths already set) and re-derives (paths already non-empty) are no-ops. Stamps the ini-read baseline
    /// so the profile-switch check has a reference point. Caller holds the gate.</summary>
    void EnsurePathsDerived()
    {
        if (_instanceDir is null) return;                        // explicit mode — roots configured directly
        if (_profileDir.Length > 0) return;                      // already derived (a prior build / SetInstance); RederiveIfIniChanged owns later updates
        var iniMtime = SafeMtime(Mo2Instance.IniPath(_instanceDir));   // stat BEFORE the read (TOCTOU): an ini write during/after Resolve is caught next call
        var p = Mo2Instance.Resolve(_instanceDir);               // throws (Q3) naming the missing piece if not a usable instance
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        _iniMtime = iniMtime;
        InvalidateClassParents();                                // _modsDir just gained a value — a cache built before derivation is baseline-only (hunt F1)
    }

    static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether houseCARL has an MO2 location to resolve against. False on a fresh install with no config — the
    /// server still runs; every tool returns the trained prompt until <see cref="SetInstance"/> is called.</summary>
    public bool IsConfigured { get { lock (_gate) { return _configured; } } }

    /// <summary>The active profile name (instance mode: ModOrganizer.ini selected_profile; explicit mode: the profile folder
    /// name); "" when unconfigured. For the status surface.</summary>
    public string ProfileName { get { lock (_gate) { return _profileName; } } }

    /// <summary>The game install directory the load order points at — DataDir's PARENT (DataDir = gamePath\Data), the same
    /// derivation the asset-discovery path uses — or null when it isn't derivable. The compile rider's auto-detect HINT: the
    /// CK installs its compiler at &lt;gamePath&gt;\Papyrus Compiler\PapyrusCompiler.exe (6.2). NULL-SAFE by contract — it is
    /// best-effort plumbing, so a failure here must fall through to the forcing prompt, NEVER throw and abort the compile:
    /// returns null when unconfigured (no _configured guard would otherwise hit EnsurePathsDerived's NotConfigured throw),
    /// when the instance is unusable (EnsurePathsDerived throws naming the missing piece — the rider's own config gate
    /// reports that; here it's just "no hint"), or when DataDir hasn't been derived yet. Takes <see cref="_gate"/> like the
    /// other derived-root reads; works in explicit mode too (DataDir is set directly, EnsurePathsDerived no-ops).</summary>
    public string? GameDirOrNull()
    {
        lock (_gate)
        {
            if (!_configured) return null;
            try { EnsurePathsDerived(); }
            catch { return null; }                                  // unusable instance → no hint (Q3: the rider's config gate names the real problem)
            return _dataDir.Length > 0 ? Path.GetDirectoryName(_dataDir.TrimEnd('\\', '/')) : null;
        }
    }

    /// <summary>The game directories to search for the Creation Kit's compiler, in PRIORITY ORDER — the compile rider's
    /// auto-detect hints (6.2). [0] = the load order's OWN game dir (<see cref="GameDirOrNull"/>): correct when MO2 points
    /// straight at a real, CK-equipped install. Then the GameFinder/Mutagen-located real Skyrim SE install(s): in the common
    /// MO2 "Stock Game" setup (Aaron 2026-06-17) the load order points at a COPY that has NEITHER the CK nor the vanilla
    /// script sources — both live in the Steam install — so the located install is the one that actually hits. De-duplicated,
    /// nulls dropped. BEST-EFFORT + NULL-SAFE end to end: the locator reads the registry/Steam, so a miss or a throw just
    /// yields fewer hints (the forcing prompt then names what was checked), it NEVER aborts the compile.
    /// <para>LOAD-BEARING (do NOT "simplify"): the compile rider derives the vanilla SOURCE folder from the RESOLVED
    /// COMPILER's own game dir (<see cref="CompileTools.BuildImports"/>), NOT from these hints and NOT from the data dir — so
    /// once the compiler resolves to the Steam install, its sibling Data\Source\Scripts is used, never the Stock Game copy's
    /// (which usually has none). Keying sources off the data dir would re-break exactly the Stock-Game case this fixes.</para></summary>
    public IReadOnlyList<string> CompilerGameDirHints()
    {
        var hints = new List<string>();
        if (GameDirOrNull() is { } loadOrderGameDir) hints.Add(loadOrderGameDir);
        try
        {
            // The bundled GameFinder locator (Steam/GOG/Xbox), via Mutagen — finds the REAL Skyrim SE install (App 489830),
            // where the Creation Kit + sources live, regardless of where MO2's load order points.
            if (new Mutagen.Bethesda.Installs.GameLocator().TryGetGameDirectory(
                    Mutagen.Bethesda.GameRelease.SkyrimSE, out var dir) && !string.IsNullOrWhiteSpace(dir.Path))
                hints.Add(NormalizeGameDir(dir.Path));
        }
        catch { /* GameFinder / registry hiccup → just the load-order hint (best-effort; the prompt still names it) */ }
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The locator returns the game-install ROOT (the folder holding the exe + Data); defend against a future build
    /// handing back the Data folder itself by stepping up one level so the &lt;game&gt;\Papyrus Compiler\ join stays correct.</summary>
    static string NormalizeGameDir(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return Path.GetFileName(t).Equals("Data", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(t) ?? t) : t;
    }

    /// <summary>Point houseCARL at an MO2 instance folder — first-run setup AND switching between instances ("jump around").
    /// VALIDATES it (<see cref="Mo2Instance.Resolve"/> throws a clear Q3 message if it isn't usable — nothing is changed or
    /// persisted on failure), then re-points the live service (derives the roots + active profile, drops the cached resolver
    /// so the next tool call rebuilds against the new instance) and PERSISTS the choice to the user config file so it
    /// survives a restart. Returns the derived paths + whether the persist succeeded, for the tool's confirmation.</summary>
    public (Mo2InstancePaths paths, bool persisted, string? persistError, string? persistNote) SetInstance(string instanceDir)
    {
        // The ini baseline is statted BEFORE Resolve reads the instance (hunt F7 — this was the one stamp-AFTER-the-read
        // in the file: an MO2 ini write landing between Resolve's read and the stamp was absorbed into the baseline and
        // its profile switch stayed invisible forever). Statting first makes a during/after-read write show as a changed
        // mtime on the next call — the same TOCTOU discipline every other baseline here follows.
        var iniMtime = SafeMtime(Mo2Instance.IniPath(instanceDir.Trim()));
        var paths = Mo2Instance.Resolve(instanceDir);            // throws (Q3) if not a usable MO2 instance — the tool renders the reason
        lock (_writeGate)                                        // hunt F2: an instance switch waits for any in-flight write — never tears one across instances
        lock (_gate)
        {
            _instanceDir = paths.InstanceDir;
            _dataDir = paths.DataDir; _modsDir = paths.ModsDir; _profileDir = paths.ProfileDir; _profileName = paths.ProfileName;
            _overwriteDir = paths.OverwriteDir;
            _iniMtime = iniMtime;
            _configured = true;
            _resolver?.Dispose(); _resolver = null;              // force a rebuild against the new instance on the next query
            _assetResolver?.Dispose(); _assetResolver = null;    // the asset resolver rebuilds against the new instance too (Phase 2)
            _resolvedPaths = Array.Empty<string>();
            _profileMtimes = new DateTime[ProfileFileNames.Length];   // unset — the next build records fresh baselines against the new profile
            _orderWarnings = Array.Empty<string>();
            InvalidateClassParents();                            // every sibling cache drops on a switch — the hierarchy too (PR #47 review)
        }
        var (persisted, persistError, persistNote) = PersistInstanceDir(paths.InstanceDir);
        return (paths, persisted, persistError, persistNote);
    }

    /// <summary>Persist the chosen instance dir through the shared <see cref="UserConfigStore"/> (read-modify-write), so it
    /// survives a restart AND coexists with any saved tool paths — the store never clobbers the other concern's field.
    /// Best-effort + HONEST (Q3): a write failure (e.g. a read-only data dir) is reported, not swallowed — the session
    /// still works, but the user is told the choice won't survive a restart. <c>note</c> carries a corrupt-file recovery
    /// (hunt F3 — the prior file was backed up; other saved settings were lost), rendered even on success.</summary>
    (bool ok, string? error, string? note) PersistInstanceDir(string instanceDir)
        => _store.Update(c => c.Mo2InstanceDir = instanceDir);

    /// <summary>The trained prompt shown while unconfigured: tells houseCARL to ask the user which MO2 instance to use (not
    /// silently pick among several) and call the setup tool. Tools RETURN this (so the client SEES it) via <see cref="ConfigPromptOrNull"/>; the <see cref="Resolver"/>
    /// getter also THROWS it as a backstop. The two must say the same thing, hence one shared string.</summary>
    const string NotConfiguredText =
        "houseCARL has no Mod Organizer 2 instance configured yet. Ask the user which MO2 instance folder to use — the " +
        "folder that contains ModOrganizer.ini (for a Wabbajack / portable list, that's the list's install folder). You " +
        "may help locate it, but do NOT silently pick one when more than one MO2 install exists: list the candidates you " +
        "found and let the user choose. State which folder you're using, then call housecarl_set_mo2_instance with that path.";

    /// <summary>Tools call this FIRST: returns the trained prompt (a normal result string the client SEES) when
    /// unconfigured, else null (proceed). Preferred over letting <see cref="Resolver"/> throw — the MCP framework
    /// genericizes a thrown exception to "An error occurred invoking '…'", so a THROW never delivers the guidance to the
    /// client, but a returned string does (measured 2026-06-02 during the server-driven proof).</summary>
    public string? ConfigPromptOrNull() { lock (_gate) { return _configured ? null : NotConfiguredText; } }

    static InvalidOperationException NotConfigured() => new(NotConfiguredText);

    /// <summary>Resolve + read one record (the read_record primitive). Reads the WINNER's body by default, or a
    /// named <paramref name="plugin"/>'s version; with <paramref name="conflictTree"/> also returns the ordered
    /// touching-plugin list. Honest, recoverable errors (Q3): not-in-order, plugin-doesn't-touch, fetch
    /// inconsistency — never a silent empty result.</summary>
    public ReadOutcome ResolveRead(FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1)
    {
        var resolver = Resolver;
        return ResolveRead(resolver, resolver.Capture(), fk, plugin, fields, conflictTree, depth);
    }

    /// <summary>Layer B unit C2 — the on-demand whole-topic dialogue-graph validator (housecarl_validate_dialogue):
    /// resolve <paramref name="fk"/> to its load-order winner and, when it is a dialogue topic (DIAL) validate that
    /// topic's whole graph, or when a quest (QUST) fan out to EVERY topic the quest owns — all against the resolved
    /// load-order winners (what the game actually sees). The on-demand counterpart of the per-create voice (unit B)
    /// + result-script (unit C1) teeth, auditing existing INFOs the create-time checks never re-touch. The whole
    /// Skyrim-typed walk (winner resolution, the DIAL/QUST branch, the graph + per-INFO checks) lives in CORE
    /// (<see cref="DialogueValidate"/>), so the service stays Mutagen.Skyrim-free exactly like the voice/script
    /// enrichers — here it just hands core the live record resolver + the VFS asset resolver. NEVER throws over a
    /// verify step: a mid-run resolve/asset failure rides <see cref="DialogueValidationReport.CheckError"/>, and a
    /// not-in-order / not-a-DIAL-or-QUST input is a NAMED <see cref="DialogueValidationReport.Error"/> (Q3).</summary>
    public DialogueValidationReport ValidateDialogue(FormKey fk) => DialogueValidate.Run(Resolver, Assets, fk);

    /// <summary>The read body, answered entirely off ONE captured view (HCBR-2026-06-11-02): excluded-check, winner,
    /// and touching-plugin list all describe the SAME build — a freshness rebuild landing mid-read can no longer make
    /// a record's reported winner disagree with its own TOUCHING LIST. (The body fetch reads the file on disk through
    /// the session; a mid-read file edit surfaces as the existing named fetch-inconsistency error, never torn values.
    /// Known residue, review #1: the conflict-tree DIFF the render layer adds is a separate <see cref="ResolveTree"/>
    /// call with its own capture, so one rendered response can still pair this read's build with an adjacent build's
    /// diff — same low-severity class, named for the next wave rather than threaded through the render API here.)</summary>
    ReadOutcome ResolveRead(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                            FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth)
    {
        // An explicitly-requested plugin that was EXCLUDED this session (unparseable/unopenable) → say so (Q3),
        // rather than fall through to a misleading "does not define this record".
        if (plugin is not null && view.ExcludedPlugins.TryGetValue(plugin, out var pWhy))
            return ReadOutcome.Fail(fk, $"Plugin '{plugin}' was excluded from this session: {pWhy}");

        // An explicitly-requested plugin that is NOT IN THE ORDER AT ALL is its own failure mode (HCBR-2026-06-11-02
        // wave (a)): GetRecord returns null for it, and falling through would render the FALSE "does not define this
        // record" — which reads as "my write was lost" and invites re-issuing the ops (duplicate list Adds into the
        // patch). Name the true condition + the working verify paths instead. Aaron-decided Option A: houseCARL does
        // NOT read disabled plugins off disk (non-winner content masquerading as load-order truth is the Q3 hazard).
        if (plugin is not null && !view.ContainsPlugin(plugin))
            return ReadOutcome.Fail(fk,
                $"Plugin '{plugin}' is not in the load order ({view.PluginCount} plugins; names match the plugin FILENAME " +
                "incl. .esp/.esm, case-insensitively) — houseCARL reads load-order truth only and does not open disabled " +
                "plugins off disk. If this is a freshly written houseCARL patch, it isn't enabled yet: enable + sort it in " +
                "MO2, then re-read. To verify a write BEFORE enabling, use the write call's own read-back " +
                "(full_readback=true returns the whole written record). If a prior write into this patch reported success, " +
                "the edits DID land — do not re-issue them (re-running list Adds would duplicate entries).");

        var winner = view.ResolveWinner(fk);
        if (winner is null)
        {
            // If the record's defining plugin was excluded, that's WHY it's missing — name it (Q3), not a bare "not present".
            var defining = fk.ModKey.FileName.ToString();
            if (view.ExcludedPlugins.TryGetValue(defining, out var dWhy))
                return ReadOutcome.Fail(fk, $"FormID {fk} is not resolvable: its plugin '{defining}' was excluded from this session: {dWhy}");
            return ReadOutcome.Fail(fk, $"FormID {fk} is not present in the load order ({view.PluginCount} plugins).");
        }

        var source = plugin ?? winner.Value.WinnerPlugin;
        using var session = resolver.OpenSession();                       // opens the source plugin; disposed at return (Option B)
        var rec = view.GetRecord(session, source, fk);                    // excluded-check pinned to the SAME view the winner came from (hunt F5 discipline)
        if (rec is null)
            return ReadOutcome.Fail(fk, plugin is null
                ? $"Winner '{winner.Value.WinnerPlugin}' did not yield {fk} on fetch — a load-order inconsistency."
                : $"Plugin '{plugin}' does not define {fk} (it does not touch this record). The winner is '{winner.Value.WinnerPlugin}'.");

        var record = ReadEngine.ReadFields(rec, fields, depth);           // materialise while the session (overlay) is open
        var touching = conflictTree ? view.TouchingPlugins(fk) : null;
        return new ReadOutcome(fk, record, source, winner.Value.WinnerPlugin, winner.Value.OverrideDepth, touching, null);
    }

    /// <summary>How deep the conflict diff reads each touching body. The diff must compare CONTENT, not the
    /// depth-1 count summaries that masked equal-count list deltas (HCBR-2026-06-09-01) — deep enough to reach
    /// every modeled scalar leaf (the walk is bounded by the modeled-corpus boundary + ReadEngine's expansion
    /// cap, whose truncation sentinel FieldsDiff surfaces as Complete=false).</summary>
    internal const int ConflictDiffDepth = 16;

    /// <summary>The winner's full conflict tree, MATERIALISED — every touching plugin's name + its fields read off its
    /// own body, in priority order (winner last) — for the field-level diff view, read DEEP (<see cref="ConflictDiffDepth"/>)
    /// so the diff compares list/substruct CONTENT, not depth-1 count summaries (HCBR-2026-06-09-01). Opens a per-call
    /// session, fetches each touching body, reads its <paramref name="fields"/> into a plain DTO, then DISPOSES the
    /// session (Option B): the render layer never touches a live overlay or holds a handle. null if the FormKey isn't
    /// in the order.</summary>
    public ConflictTreeView? ResolveTree(FormKey fk, IReadOnlyList<string>? fields)
    {
        var resolver = Resolver;
        using var session = resolver.OpenSession();
        var tree = resolver.ResolveTree(session, fk);
        if (tree is null) return null;
        var nodes = new List<ConflictNodeView>(tree.Nodes.Count);
        foreach (var n in tree.Nodes)
            nodes.Add(new ConflictNodeView(n.Plugin, ReadEngine.ReadFields(n.Record, fields, ConflictDiffDepth)));   // materialise while open
        return new ConflictTreeView(nodes);
    }

    /// <summary>A header-only summary for one record (winner + type + editorid, no field dump) — the compact
    /// one-line-per-match view cross_plugin_query uses by default. One winner-body fetch; holds nothing.</summary>
    public RecordSummary ResolveSummary(FormKey fk)
    {
        var resolver = Resolver;
        var view = resolver.Capture();                  // one capture per summary (winner + depth + fetch from one build)
        var w = view.ResolveWinner(fk);
        if (w is null) return new RecordSummary(fk, "?", null, "?", 0, $"{fk} not in the load order");
        using var session = resolver.OpenSession();
        var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
        if (body is null)
            return new RecordSummary(fk, "?", null, w.Value.WinnerPlugin, w.Value.OverrideDepth,
                $"winner '{w.Value.WinnerPlugin}' did not yield {fk} on fetch");
        return new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                 w.Value.WinnerPlugin, w.Value.OverrideDepth, null);
    }

    // ---- batch (Q4.9) -----------------------------------------------------------------------------------

    /// <summary>Resolve+read many records in one call (housecarl_batch_record_detail). Each formid runs the same
    /// <see cref="ResolveRead"/> path, so a bad/absent formid yields a per-item recoverable error (Q3) without
    /// failing the batch. Returns one <see cref="ReadOutcome"/> per input, in order.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1)
    {
        var resolver = Resolver;                // build/refresh ONCE for the batch
        var view = resolver.Capture();          // ONE build for every item — the whole batch is one logical operation (HCBR-2026-06-11-02)
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = FormKey.Factory(raw.Trim()); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            outcomes.Add(ResolveRead(resolver, view, fk, null, fields, conflictTree, depth));
        }
        return outcomes;
    }

    // ---- cross-plugin query (Q4.9) ----------------------------------------------------------------------

    /// <summary>Scan the order for records matching a filter (housecarl_cross_plugin_query). A SINGLE enumeration
    /// pass with the matching record's body in hand (no per-candidate re-fetch): type= streams the WINNER body
    /// (effective truth) via typed group enumeration; plugins= streams each scoped plugin's OWN body (a content
    /// audit); conflicts_only= alone reads the index. Body filters (editorid_contains/references) test the
    /// in-hand body and so require type= or plugins= to bound them. Returns pre-built match summaries (capped at
    /// <paramref name="limit"/>, with the true total) or a recoverable Q3 error. Holds nothing.</summary>
    public CrossQueryOutcome CrossQuery(string? type, FormKey? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit)
    {
        var resolver = Resolver;
        var view = resolver.Capture();          // ONE build for the SCAN and every per-match fill it makes (HCBR-2026-06-11-02)
        bool hasPlugins = plugins is { Count: > 0 };
        bool hasType = type is not null;
        bool hasWhere = where is { Count: > 0 };
        bool bodyFilter = references is not null || !string.IsNullOrEmpty(editoridContains) || hasWhere;

        if (!hasType && !conflictsOnly && !hasPlugins && !bodyFilter)
            return CrossQueryOutcome.Fail("cross_plugin_query needs at least one of: type=, conflicts_only=true, editorid_contains=, references=, where=, or plugins=.");
        if (bodyFilter && !hasType && !hasPlugins)
            return CrossQueryOutcome.Fail("editorid_contains/references/where is a body scan and must be combined with type= or plugins= to bound it (conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). A global reverse-reference index is a future capability.");

        // where= → the field-value predicate set. Parsed up front so a malformed predicate refuses the call BEFORE
        // any scan (Q3). The predicate reuses the read engine's path-walk, so its reach == the read surface's reach.
        FieldPredicateSet? predicate = null;
        if (hasWhere)
        {
            var (set, perr) = FieldPredicateSet.Parse(where!);
            if (perr is not null) return CrossQueryOutcome.Fail(perr);
            predicate = set;
        }

        IReadOnlyList<Type>? types;
        try { types = hasType ? ResolveTypeFilter(type!) : null; }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }   // unknown type

        var keys = new List<FormKey>();
        var sources = new List<string?>();                                    // parallel to keys: the plugin whose body matched (null ⇒ winner), so the render displays the SAME body it filtered
        List<RecordSummary>? prefilled = (hasType || hasPlugins) ? new() : null;   // parallel to keys; null = renderer fills lazily
        int total = 0;
        int unscannable = 0;                                                  // records whose body tests THREW (Mutagen-unparseable content) — excluded + accounted, never silent (Q3)
        var unscannableSamples = new List<string>();

        if (hasType || hasPlugins)                                            // a body-bearing scope: stream + filter in hand
        {
            // RecordsIn / WinnerRecordsOfType are LAZY iterators: ScopeIndices (a plugin not in the order) and
            // EnumerateMajorRecords(throwIfUnknown) throw on ENUMERATION, not on creation — so the try must wrap the
            // foreach, not just the assignment, or the clean Q3 message escapes as a generic framework error.
            var seen = new HashSet<FormKey>();
            try
            {
                // Carry the SOURCE plugin per record so the render shows the body the scan filtered (not the winner):
                // plugins= → the scoped plugin's filename; type= → null (⇒ the winner, the WinnerRecordsOfType body).
                IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string? source)> stream =
                    hasPlugins ? view.RecordsIn(plugins!, types).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)x.source))  // the scoped plugin's own body
                               : view.WinnerRecordsOfType(types!).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)null));    // the load-order winner's body
                foreach (var (fk, depth, body, source) in stream)
                {
                    if (conflictsOnly && depth <= 1) continue;
                    // PER-RECORD FAULT ISOLATION (HCBR-2026-06-09-03): the body tests lazily parse subrecord
                    // content (references= walks Effects etc. via Mutagen's EnumerateFormLinks), so ONE record
                    // Mutagen can't parse used to abort the WHOLE call as an opaque transport error — the
                    // scan-level twin of the PKCU index-build fix. Such a record is excluded and ACCOUNTED in
                    // the response (never a silent skip, never a guessed match — Q3).
                    try
                    {
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (body.EditorID is null || body.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        if (references is { } target
                            && !(body is IFormLinkContainerGetter flc && flc.EnumerateFormLinks().Any(l => l.FormKey == target)))
                            continue;
                        if (predicate is not null && !predicate.Matches(body))    // value filter — same in-hand body, no extra fetch
                        {
                            if (predicate.FatalError is not null) break;          // numeric op vs non-numeric field — abort + surface (Q3)
                            continue;
                        }
                        // De-dup (a FK can recur across scoped plugins). This runs AFTER the filters, so under
                        // plugins=[A,B] the source recorded for a shared FK is the FIRST scoped plugin (in plugins=
                        // array order) whose body PASSED the filters — deterministic, and it's the body we'll display.
                        if (!seen.Add(fk)) continue;
                        total++;
                        if (keys.Count < limit)                                   // in-hand body → fill the summary for free
                        {
                            keys.Add(fk);
                            sources.Add(source);                                  // the body we filtered IS the body we'll display (null ⇒ winner)
                            // winner= off the SAME view the scan runs on — a rebuild landing mid-scan can no longer
                            // make a row's winner reflect a newer build than the depth beside it (HCBR-2026-06-11-02).
                            prefilled!.Add(new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                                             view.ResolveWinner(fk)?.WinnerPlugin ?? "?", depth, null));
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 3)
                            unscannableSamples.Add($"{fk}{(source is null ? "" : $" in {source}")} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); } // plugin not in order / unknown type
            // Anything else escaping the stream itself still gets a NAMED failure — the MCP layer's generic
            // "An error occurred invoking …" must never be the terminal diagnostic for a data failure (Q3).
            catch (Exception ex) { return CrossQueryOutcome.Fail($"scan aborted: {ex.GetType().Name}: {ex.Message}"); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError); // typed predicate error — fail fast, named (Q3)
        }
        else                                                                  // conflicts_only alone — index keys only; NO body fetch
        {
            // Summaries here would each need a winner-body fetch; leaving them to the renderer (which stops at
            // max_chars) means a big limit with a small max_chars doesn't fetch bodies it will never show.
            foreach (var fk in view.ConflictKeys())
            {
                total++;
                if (keys.Count < limit) { keys.Add(fk); sources.Add(null); }   // no scoped plugin → display the winner
            }
        }
        // Unscannable accounting (Q3): name the count, the first few offenders with Mutagen's reason, and what
        // a caller can still do — these records are invisible to the body filters, not "0 matches" silence.
        // "instance(s) … where they threw" because under plugins= a FormKey is tested once per scoped plugin:
        // a copy that throws is skipped while another plugin's copy of the same FK can still match (PR #27 review).
        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned (Mutagen could not parse their content) and were skipped where they threw: "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "")
              + ". Inspect one with read_record (per-field fault isolation applies).";
        return new CrossQueryOutcome(keys, prefilled, total, total > keys.Count, null, predicate?.AccountingNote(), sources, scanNote);
    }

    // ---- writes (§8.4 Beat C: housecarl_set_field / housecarl_bulk_apply) -------------------------------

    /// <summary>Apply one-or-more edits as a single patch (housecarl_set_field = one op; housecarl_bulk_apply = many).
    /// Parses each op's FormID + field path + (optional) composition spec to the core's <see cref="WritePatchBuilder.PatchEdit"/>,
    /// resolves the output path as a NEW MO2 mod folder under ModsDir (folder-per-patch — see <see cref="ResolveOutputPath"/>),
    /// then drives the proven public cleave <see cref="WritePatchBuilder.Apply"/> (resolve winner → derive type → pre-flight
    /// ALL → override → ApplyVerb → multi-master serialize). ALL-OR-NOTHING (Q3): a single malformed op or pre-flight reject
    /// refuses the whole call with no file written. Writes go to a NEW patch by default; <paramref name="into"/> EXTENDS an
    /// existing houseCARL-owned patch (the multi-session accumulation lever). Returns null-Error outcome on success.
    /// <paramref name="fullReadback"/> additionally reads every touched record back IN FULL off the written file
    /// (the pre-enable verify loop — wishlist #3 re-scoped / HCBR-2026-06-11-02 wave (b)).</summary>
    public WritePatchBuilder.PatchOutcome ApplyEdits(IReadOnlyList<BulkOp> ops, string? patchName, string? into, bool fullReadback = false)
    {
        if (ops.Count == 0)
            return WritePatchBuilder.PatchOutcome.Fail("no operations supplied.");

        // Map every op to a core PatchEdit, collecting ALL parse problems first (all-or-nothing, like the cleave).
        // Pure parsing — runs outside the write gate so a malformed call never queues behind a real write.
        var edits = new List<WritePatchBuilder.PatchEdit>(ops.Count);
        var problems = new List<string>();
        for (int i = 0; i < ops.Count; i++)
        {
            var edit = MapEdit(ops[i], i, out var err);
            if (err is not null) problems.Add(err); else edits.Add(edit!);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"refused — {problems.Count} of {ops.Count} operation(s) malformed; NO patch written:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // hunt F2: one write at a time, resolve→commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index
            var rulebook = Rulebook;

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused write leaves no orphan
            return outcome;
        }
    }

    /// <summary>Remove WHOLE records a houseCARL patch carries (housecarl_remove_record) — literal drop-from-plugin, the
    /// companion to <see cref="ApplyEdits"/>. <paramref name="patch"/> is REQUIRED and names an existing houseCARL-owned
    /// patch (resolved + ownership-gated via the same <c>into=</c> path as an extend — refuses a folder houseCARL didn't
    /// create, Q3); removal only makes sense against a patch that already carries the record. Parses every formid (all-or-
    /// nothing on a malformed one), then drives <see cref="WritePatchBuilder.RemoveRecords"/> (present-check → mod.Remove →
    /// re-serialize, with clean-masters riding along). Originals are never touched (only the patch folder is written).</summary>
    public WritePatchBuilder.RemovalOutcome RemoveRecords(IReadOnlyList<string> formids, string? patch)
    {
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.RemovalOutcome.Fail("no formids supplied — pass the FormID(s) of the record(s) to remove.");
        if (string.IsNullOrWhiteSpace(patch))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it).");

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path). Pure — outside the gate.
        var keys = new List<FormKey>(formids.Count);
        var problems = new List<string>();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formid[{i}]: empty."); continue; }
            try { keys.Add(FormKey.Factory(raw.Trim())); }
            catch (Exception ex) { problems.Add($"formid[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING removed:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // hunt F2: removal re-serializes the patch — same gate
        {
            var resolver = Resolver;                                      // builds/refreshes the index (Overlays for the re-serialize)

            // Resolve + ownership-gate the patch path via the into= (extend) path — must exist + carry the houseCARL marker.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>Forward a NAMED plugin's version of one-or-more records into a patch as an override (housecarl_forward_record)
    /// — xEdit's "copy as override into", the inverse of <see cref="ApplyEdits"/>'s winner-override. Parses every formid
    /// (all-or-nothing on a malformed one), resolves the folder-per-patch output (fresh, or <paramref name="into"/> an
    /// existing houseCARL-owned patch), then drives <see cref="WritePatchBuilder.ForwardRecords"/> (resolve each source
    /// body from <paramref name="fromPlugin"/> → deep-copy as override → multi-master serialize). The whole source record
    /// is copied verbatim, so the SOURCE plugin (not the load-order winner) decides the content — and forwarding the
    /// ORIGIN master reverts a record to vanilla. Originals are never touched (only the patch folder is written).</summary>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into, bool fullReadback = false)
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "from_plugin is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.ForwardOutcome.Fail("no formids supplied — pass the FormID(s) to forward from the source plugin.");

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit/remove paths). Pure — outside the gate.
        var fp = fromPlugin.Trim();
        var specs = new List<WritePatchBuilder.ForwardSpec>(formids.Count);
        var problems = new List<string>();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formid[{i}]: empty."); continue; }
            try { specs.Add(new WritePatchBuilder.ForwardSpec { Target = FormKey.Factory(raw.Trim()), FromPlugin = fp }); }
            catch (Exception ex) { problems.Add($"formid[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING forwarded:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // hunt F2: one write at a time, resolve→commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index (Overlays for the source fetch + serialize)

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created); }
            catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused forward leaves no orphan
            return outcome;
        }
    }

    /// <summary>Create an EMPTY, HEADER-ONLY plugin (housecarl_create_plugin) — a valid TES4 header with ZERO records,
    /// no masters, optionally ESL-flagged, named EXACTLY <paramref name="pluginName"/>. The clean primitive for "I need
    /// plugin <c>Foo.esp</c> to exist" (a basename-bound SKSE config trigger, a placeholder ESL, a dummy master) — it
    /// authors no record, so it adds no conflict footprint (HCBR-2026-06-19-02). UNLIKE the patch-write paths, the name
    /// is used VERBATIM — never auto-suffixed — because a trigger plugin's whole job is that its basename matches the
    /// config bound to it; so a name collision REFUSES loud (Q3) rather than rename or overwrite: (a) a plugin of that
    /// basename already active in the order (creating another would shadow it), or (b) a houseCARL mod folder of that
    /// name already on disk. The core <see cref="WritePatchBuilder.CreatePlugin"/> builds + serializes + re-reads to
    /// confirm; a refused create that just made the output folder leaves no orphan (hunt F4).</summary>
    public WritePatchBuilder.CreatePluginOutcome CreatePlugin(string pluginName, bool esl = false, string? author = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                "plugin_name is required — a header-only plugin has no record to derive a name from, so name it explicitly (e.g. 'Authoria - CraftingCategories').");

        var stem = PatchStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                $"plugin_name '{pluginName}' has no usable name once path parts and the plugin extension are stripped — give a plain name like 'MyTrigger'.");

        lock (_writeGate)                                                 // hunt F2: one write at a time, resolve→commit
        {
            // Touch Resolver FIRST: in instance mode _modsDir is derived LAZILY (EnsurePathsDerived runs inside the
            // Resolver getter), so a cold first-call (create_plugin before any read warmed the index) would otherwise
            // see _modsDir="" and misreport "ModsDir '' does not exist" (a Q3-adjacent wrong reason). Capturing the view
            // here both derives the paths AND is what the collision check below needs.
            var view = Resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CreatePluginOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            // COLLISION (Q3): the basename is load-bearing for a trigger, so NEVER auto-suffix — refuse loud instead.
            // (a) an active plugin already owns this basename — a second one would shadow it (MO2 picks one by mod order).
            foreach (var ext in PluginExts)                              // .esp / .esm / .esl
                if (view.ContainsPlugin(stem + ext))
                    return WritePatchBuilder.CreatePluginOutcome.Fail(
                        $"a plugin named '{stem + ext}' is already active in your load order — a header-only trigger needs a UNIQUE basename (a second one would shadow it, MO2 picking the winner by mod order). Choose a different name.");
            // (b) a houseCARL mod folder of this exact name already exists — don't overwrite (could clobber a real patch
            //     sharing the name) and don't auto-rename (would break the basename trigger): refuse and point at it.
            var folder = Path.Combine(_modsDir, ModFolderName(stem));
            if (Directory.Exists(folder))
                return WritePatchBuilder.CreatePluginOutcome.Fail(
                    $"a houseCARL output folder '{ModFolderName(stem)}' already exists — houseCARL won't auto-rename a header-only plugin (its exact basename is what makes the trigger resolve). Remove that folder in MO2, or choose a different name.");

            Directory.CreateDirectory(folder);
            var plugin = stem + ".esp";
            WriteOwnerMeta(folder, plugin);
            var outPath = Path.Combine(folder, plugin);

            var outcome = WritePatchBuilder.CreatePlugin(outPath, esl, author, description);
            if (!outcome.Success) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused create leaves no orphan
            return outcome;
        }
    }

    /// <summary>Create a BRAND-NEW record (housecarl_create_record) — the net-new authoring capability, the sibling of
    /// <see cref="ApplyEdits"/>. Resolves <paramref name="recordType"/> (catalog name or 4-char signature) to ONE concrete
    /// catalog name (unknown/ambiguous → Q3), maps the field <paramref name="operations"/> to core <see cref="WriteRequest"/>s
    /// rooted at that type (a create op takes NO formid — it sets fields on the new record), resolves the folder-per-patch
    /// output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch), then drives
    /// <see cref="WritePatchBuilder.CreateRecords"/> (pre-flight ALL → AddNew/NestedAddNew → ApplyVerb → multi-master serialize).
    /// The new record's FormID is auto-allocated (local 0x800+) and reported; originals are never touched. A flat top-level
    /// record needs no <paramref name="parent"/>; a NESTED child (a dialogue line, a placed ref) passes <paramref name="parent"/>
    /// (an existing parent's FormKey, or a record created in a prior into= call) and, when the parent holds more than one
    /// fitting child-list, <paramref name="collection"/>. For a parent + its children in ONE call (a topic + its lines, a
    /// child's parent= naming a same-call sibling), see <see cref="CreateRecordsBatch"/>.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecords(string recordType, string editorid, IReadOnlyList<BulkOp> operations,
        string? patchName, string? into, bool fullReadback = false, string? parent = null, string? collection = null, string? grid = null)
    {
        var problems = new List<string>();
        var spec = BuildCreateSpec(recordType, editorid, operations, parent, collection, grid, where: null, problems);
        if (spec is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) creating the record; NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(new[] { spec }, patchName, into, fullReadback);
    }

    /// <summary>Create MANY new records in ONE patch (housecarl_bulk_create) — the batch sibling of
    /// <see cref="CreateRecords"/>, and the one-shot lever for a nested unit (a dialogue topic + its lines, a cell + its
    /// placed refs) where a child's <c>parent</c> names a same-call sibling by editorid. Each spec is mapped exactly as
    /// the single create (type resolution + field-op mapping + parent/collection); ALL-OR-NOTHING (Q3) — any malformed
    /// spec refuses the whole call (with per-record reasons) and the core <see cref="WritePatchBuilder.CreateRecords"/>
    /// likewise refuses the whole batch on any creatability/parent problem. One serialize for the lot.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback = false)
    {
        if (records is null || records.Count == 0)
            return WritePatchBuilder.CreateOutcome.Fail("no records to create supplied — pass one or more {record_type, editorid, operations?, parent?, collection?} specs.");

        var problems = new List<string>();
        var specs = new List<WritePatchBuilder.CreateSpec>(records.Count);
        for (int r = 0; r < records.Count; r++)
        {
            var rec = records[r];
            var spec = BuildCreateSpec(rec.RecordType, rec.Editorid, rec.Operations ?? Array.Empty<BulkOp>(), rec.Parent, rec.Collection, rec.Grid, $"record[{r}]", problems);
            if (spec is not null) specs.Add(spec);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) across {records.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(specs, patchName, into, fullReadback);
    }

    /// <summary>Build ONE core <see cref="WritePatchBuilder.CreateSpec"/> from wire parts (shared by the single create and
    /// the batch): resolve <paramref name="recordType"/> (catalog name or 4-char signature) to ONE concrete catalog name
    /// (unknown/ambiguous → a problem), require an editorid, map each field <paramref name="operations"/> op to a core
    /// <see cref="WriteRequest"/> rooted at that type, and carry <paramref name="parent"/>/<paramref name="collection"/>
    /// through (a nested child) — null ⇒ a flat top-level record. Every problem (with the optional <paramref name="where"/>
    /// label) is APPENDED to <paramref name="problems"/>; returns null iff this record contributed any (all-or-nothing).</summary>
    WritePatchBuilder.CreateSpec? BuildCreateSpec(string? recordType, string? editorid, IReadOnlyList<BulkOp> operations,
        string? parent, string? collection, string? grid, string? where, List<string> problems)
    {
        var prefix = where is null ? "" : where + ": ";
        int before = problems.Count;

        string? catalogName = null;
        if (string.IsNullOrWhiteSpace(recordType))
            problems.Add($"{prefix}record_type is required (a catalog name like 'Keyword'/'Spell'/'Weapon' or a 4-char signature like 'KYWD').");
        else
        {
            try
            {
                var types = ResolveTypeFilter(recordType.Trim());
                if (types.Count != 1)
                    problems.Add($"{prefix}record_type '{recordType}' is ambiguous ({types.Count} matches) — use a specific catalog name (e.g. one of: {string.Join(", ", types.Select(t => RecordNaming.StripGetterInterface(t.Name)))}).");
                else catalogName = RecordNaming.StripGetterInterface(types[0].Name);
            }
            catch (ArgumentException ex) { problems.Add($"{prefix}{ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(editorid))
            problems.Add($"{prefix}editorid is required — the EditorID the new record is referenced by (e.g. in SkyPatcher/SPID).");

        // Map each field op → a core WriteRequest rooted at the create type (only once the type resolved; collect ALL malformed ops).
        var edits = new List<WriteRequest>(operations.Count);
        if (catalogName is not null)
            for (int i = 0; i < operations.Count; i++)
            {
                var req = MapCreateEdit(operations[i], i, catalogName, out var err);
                if (err is not null) problems.Add($"{prefix}{err}"); else edits.Add(req!);
            }

        if (problems.Count != before) return null;
        return new WritePatchBuilder.CreateSpec
        {
            RecordType = catalogName!, EditorId = editorid!.Trim(), Edits = edits,
            ParentRef = string.IsNullOrWhiteSpace(parent) ? null : parent.Trim(),
            IntoCollection = string.IsNullOrWhiteSpace(collection) ? null : collection.Trim(),
            Grid = string.IsNullOrWhiteSpace(grid) ? null : grid.Trim(),
        };
    }

    /// <summary>Resolve the folder-per-patch output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch),
    /// then drive the core multi-record create + serialize under the write gate (hunt F2: one write at a time). A refused
    /// create that just created the output folder leaves no orphan (hunt F4). Shared by the single + batch create.</summary>
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback)
    {
        lock (_writeGate)                                                 // hunt F2: one write at a time, resolve→commit
        {
            var resolver = Resolver;
            var rulebook = Rulebook;

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created); }
            catch (Exception ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused create leaves no orphan
            // Layer B dialogue teeth + the coordinate-keyed cell teeth — all post-write verify steps (the proven
            // CreateRecords path stays untouched): unit B voice (.fuz/.lip) coverage, unit C the result-script binding,
            // then the §4-(b) structural-shell report. Each is a no-op unless the call created the relevant record kind
            // (a dialogue line / a cell); none can fail the create (the write already succeeded).
            return outcome.Success ? EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver))) : outcome;
        }
    }

    /// <summary>Layer B unit B — the on-disk voice (.fuz/.lip) presence check, run as a POST-WRITE step on a SUCCESSFUL
    /// create (the service owns the live <see cref="Assets"/> resolver; the proven core create path stays asset-free and
    /// untouched). Only fires when the call created ≥1 dialogue line (INFO): <see cref="VoiceCheck.Run"/> re-opens the
    /// written patch read-only, computes each created voiced line's expected path, and checks the VFS — the report rides
    /// back on <see cref="WritePatchBuilder.CreateOutcome.Voice"/>. NEVER fails the create (the write already succeeded);
    /// a check failure is surfaced on the report's CheckError (Q3), and even a thrown Assets-build is caught here so a
    /// dialogue create never regresses to an error outcome over a verify step. Caller holds <see cref="_writeGate"/>; the
    /// reentrant Assets getter is safe there (PlaceAssets uses it the same way).</summary>
    WritePatchBuilder.CreateOutcome EnrichWithVoiceCheck(WritePatchBuilder.CreateOutcome outcome, LoadOrderResolver resolver)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        VoiceReport report;
        try { report = VoiceCheck.Run(outcome.OutputPath, outcome.Created, resolver, Assets); }
        catch (Exception ex) { report = VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { Voice = report };
    }

    /// <summary>Layer B unit C — the per-create RESULT-SCRIPT binding check, run as a POST-WRITE step on a SUCCESSFUL
    /// create (the service owns the live <see cref="Assets"/> resolver; the proven core create path stays asset-free and
    /// untouched), exactly like <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created ≥1 dialogue line
    /// (INFO): <see cref="DialogueScriptCheck.Run"/> re-opens the written patch read-only, validates each created INFO's
    /// VMAD result-script binding and checks its compiled `.pex` on disk — the report rides back on
    /// <see cref="WritePatchBuilder.CreateOutcome.ScriptBinding"/>. NEVER fails the create (the write already succeeded);
    /// a check failure is surfaced on the report's CheckError (Q3), and even a thrown Assets-build is caught here. Needs
    /// no LoadOrderResolver — the binding lives wholly on the INFO + the on-disk `.pex` (no graph resolution).</summary>
    WritePatchBuilder.CreateOutcome EnrichWithScriptCheck(WritePatchBuilder.CreateOutcome outcome)
    {
        bool anyInfo = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal)) { anyInfo = true; break; }
        if (!anyInfo) return outcome;

        ScriptBindingReport report;
        try { report = DialogueScriptCheck.Run(outcome.OutputPath, outcome.Created, Assets); }
        catch (Exception ex) { report = ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { ScriptBinding = report };
    }

    /// <summary>The coordinate-keyed §4-(b) teeth — the structural-SHELL report, a POST-WRITE step on a SUCCESSFUL
    /// create exactly like <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created ≥1 Cell:
    /// <see cref="CellShellCheck.Run"/> re-opens the written patch read-only, reads each created cell's interior/exterior
    /// kind, and lists the world content houseCARL does NOT author (lighting / terrain / water / navmesh — Aaron
    /// 2026-06-20: no CK work) — the report rides back on <see cref="WritePatchBuilder.CreateOutcome.CellShell"/>. NEVER
    /// fails the create (the cell IS written; this only says what the author must still provide); a check failure is
    /// surfaced on the report's CheckError (Q3). Needs no resolver/assets — the kind comes off the written cell's flag.</summary>
    WritePatchBuilder.CreateOutcome EnrichWithCellShell(WritePatchBuilder.CreateOutcome outcome)
    {
        bool anyCell = false;
        foreach (var c in outcome.Created)
            if (string.Equals(c.RecordType, CellShellCheck.CellCatalogName, StringComparison.Ordinal)) { anyCell = true; break; }
        if (!anyCell) return outcome;

        CellShellReport report;
        try { report = CellShellCheck.Run(outcome.OutputPath, outcome.Created); }
        catch (Exception ex) { report = CellShellReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" }; }
        return report.IsEmpty ? outcome : outcome with { CellShell = report };
    }

    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for CREATE: RecordType is the create type (not
    /// derived), and a create op carries NO formid (it sets a field on the new record, whose id is auto-allocated) — a
    /// stray formid is refused loud (Q3) rather than silently ignored. Builds the composition <see cref="StructSpec"/> the
    /// same way <see cref="MapEdit"/> does (so a created Spell's Effects / LeveledItem's Entries compose identically).</summary>
    WriteRequest? MapCreateEdit(BulkOp op, int index, string recordType, out string? error)
    {
        error = null;
        var where = $"op[{index}]";
        if (!string.IsNullOrWhiteSpace(op.Formid))
        {
            error = $"{where}: a create operation sets a field on the NEW record, so it takes no formid (the new record's id is auto-allocated). Remove formid='{op.Formid}'.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where}: field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where}: field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec,
        };
    }

    /// <summary>Map a wire op to a core <see cref="WritePatchBuilder.PatchEdit"/>: parse the FormID, split the dotted
    /// field path, and (if present) build the composition <see cref="StructSpec"/>. RecordType is NOT taken from the wire
    /// — the cleave derives it from the resolved winner. Returns null + a named error (Q3) on any malformed input.</summary>
    WritePatchBuilder.PatchEdit? MapEdit(BulkOp op, int index, out string? error)
    {
        error = null;
        var where = $"op[{index}]";
        if (string.IsNullOrWhiteSpace(op.Formid)) { error = $"{where}: formid is required."; return null; }
        FormKey fk;
        try { fk = FormKey.Factory(op.Formid.Trim()); }
        catch (Exception ex) { error = $"{where}: bad formid '{op.Formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."; return null; }
        if (string.IsNullOrWhiteSpace(op.FieldPath)) { error = $"{where} ({op.Formid}): field_path is required."; return null; }
        var path = SplitPath(op.FieldPath);
        if (path.Length == 0) { error = $"{where} ({op.Formid}): field_path '{op.FieldPath}' is empty."; return null; }

        StructSpec? spec = null;
        if (op.Compose is not null)
        {
            spec = MapStruct(op.Compose, where, out error);
            if (error is not null) return null;
        }

        return new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec,
        };
    }

    /// <summary>Build a core composition <see cref="StructSpec"/> from the wire shape — flat <c>fields</c> (coercible
    /// sub-fields), positional <c>ctor_args</c>, and nested <c>sets</c> (each a path+verb+value applied to the built
    /// struct, e.g. a leveled-list entry's Data.Level / Data.Reference). The nested sets' RecordType carries the struct
    /// type (the validator roots them at the struct schema, so it's a label). A nested set may itself carry a
    /// <c>compose</c> (HCBR-2026-06-15-01 PR-C) — a recursive <see cref="StructSpec"/> selecting a polymorphic
    /// sub-ARM (e.g. <c>sets:[{path:'Data', compose:{type:'GetActorValueConditionData', …}}]</c>) — mapped here into
    /// the nested <see cref="WriteRequest.Struct"/> the core already applies + validates end-to-end (BuildStruct
    /// recurses on a Set/Add carrying a Struct; the rulebook's ArmLegality validates compose.type against the leaf's
    /// legal arms). Without this propagation a nested set could only set a coercible scalar, never a sub-arm. Q3 on a
    /// malformed spec. <c>internal static</c> is the harness seam (the PR-C guard drives the wire→core mapping directly,
    /// like the other engine helpers; it touches no instance state).</summary>
    internal static StructSpec? MapStruct(StructInput s, string where, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(s.Type)) { error = $"{where}: compose.type is required (the arm / element type, e.g. 'LeveledItemEntry')."; return null; }
        List<WriteRequest>? sets = null;
        if (s.Sets is { Length: > 0 })
        {
            sets = new List<WriteRequest>(s.Sets.Length);
            foreach (var ns in s.Sets)
            {
                if (string.IsNullOrWhiteSpace(ns.Path)) { error = $"{where}: each compose.sets[] needs a path."; return null; }
                StructSpec? nestedSpec = null;
                if (ns.Compose is not null)
                {
                    nestedSpec = MapStruct(ns.Compose, where, out error);
                    if (error is not null) return null;
                }
                sets.Add(new WriteRequest
                {
                    RecordType = s.Type!, Path = SplitPath(ns.Path),
                    Verb = string.IsNullOrWhiteSpace(ns.Verb) ? "Set" : ns.Verb, Key = ns.Key, Value = ns.Value,
                    Struct = nestedSpec,
                });
            }
        }
        return new StructSpec { Type = s.Type!, Fields = s.Fields, CtorArgs = s.CtorArgs, Sets = sets };
    }

    static string[] SplitPath(string dotted)
        => dotted.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resolve a patch's output path under the FOLDER-PER-PATCH model (Aaron-locked 2026-06-02): each patch is
    /// its OWN MO2 mod folder — <c>&lt;ModsDir&gt;\houseCARL - &lt;name&gt;\&lt;name&gt;.esp</c> — so every houseCARL
    /// plugin is a first-class mod the user enables / orders / removes independently. A NEW patch always creates a fresh,
    /// marker-stamped folder (name auto-suffixed _001… so a prior reviewed patch is never clobbered);
    /// <paramref name="into"/> EXTENDS an existing houseCARL-owned patch (replace / modify its own plugins).
    /// ORIGINALS UNTOUCHED is structural (CLAUDE.md §1): houseCARL only ever writes a folder that is brand-NEW or carries
    /// its own <c>meta.ini</c> marker — it REFUSES (Q3) to write a folder it didn't create (a user mod), even on a name
    /// collision. The caller name is reduced to a bare stem (no directory parts) so it can never escape ModsDir.
    /// Runs under <see cref="_gate"/> like its sibling <see cref="ResolvePatchModFolder"/> (hunt F2): the UniqueStem
    /// check-then-create is only race-free when every folder allocation is serialized on the one gate.
    /// <paramref name="createdFolder"/> reports whether THIS call created the fresh folder, so a refused write can
    /// remove it again (hunt F4 — "NO patch written" must not leave an orphan folder accreting _001/_002 on retry).</summary>
    string ResolveOutputPath(string? patchName, string? into, out bool extend, out bool createdFolder)
    {
        lock (_gate)
        {
            createdFolder = false;
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                extend = true;
                var stem = PatchStem(into);
                var folder = Path.Combine(_modsDir, ModFolderName(stem));
                if (!Directory.Exists(folder))
                    throw new InvalidOperationException(
                        $"cannot extend: no houseCARL patch named '{stem}' (mod folder '{ModFolderName(stem)}' not found). " +
                        "Omit into= to create it fresh, or check the name.");
                if (!IsHouseCarlOwned(folder))
                    throw new InvalidOperationException(
                        $"cannot extend: mod folder '{ModFolderName(stem)}' exists but was NOT created by houseCARL (no marker) — " +
                        "refusing to modify a folder houseCARL doesn't own (originals untouched, Q3). Use a different patch name.");
                var existing = Path.Combine(folder, stem + ".esp");
                if (!File.Exists(existing))
                    throw new InvalidOperationException(
                        $"cannot extend: houseCARL folder '{ModFolderName(stem)}' has no '{stem}.esp' to extend.");
                return existing;
            }

            extend = false;
            var baseStem = PatchStem(string.IsNullOrWhiteSpace(patchName) ? "houseCARL_Patch" : patchName!);
            var freeStem = UniqueStem(baseStem);
            var newFolder = Path.Combine(_modsDir, ModFolderName(freeStem));
            Directory.CreateDirectory(newFolder);
            createdFolder = true;
            var plugin = freeStem + ".esp";
            WriteOwnerMeta(newFolder, plugin);
            return Path.Combine(newFolder, plugin);
        }
    }

    /// <summary>Hunt F4: a write that was REFUSED after <see cref="ResolveOutputPath"/> created a fresh folder removes
    /// that folder again, so "NO patch written" is true of the disk too (no orphan accreting _001/_002 on retry).
    /// DELETION-SAFE by content check, not trust: only a folder holding NOTHING beyond our own meta.ini (and an empty
    /// <c>.housecarl-tmp</c> staging leftover) is removed — anything else present means the folder gained real content
    /// and stays. Best-effort: a cleanup failure never masks the write's own (already-reported) outcome.</summary>
    static void RemoveFolderCreatedThisCall(string outPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(outPath);
            if (folder is null || !Directory.Exists(folder)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var name = Path.GetFileName(entry);
                if (File.Exists(entry) && name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(entry) && name.Equals(".housecarl-tmp", StringComparison.OrdinalIgnoreCase)
                    && !Directory.EnumerateFileSystemEntries(entry).Any()) continue;
                return;                                       // real content appeared — leave the folder alone
            }
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The resolved output location for a NON-.esp rider: the directory to WRITE into, the mod-folder ROOT
    /// (what residue cleanup operates on), and whether THIS call created the folder fresh (vs reused an into= folder,
    /// which the user owns and cleanup never touches). For the .bsa/extract riders OutputDir == ModFolder; for the
    /// compile/decompile riders OutputDir is a subfolder (<c>Scripts\</c> / <c>Source\Scripts\</c>) under ModFolder.</summary>
    public readonly record struct RiderFolder(string OutputDir, string ModFolder, bool CreatedFresh);

    /// <summary>Resolve a houseCARL-owned MOD FOLDER under ModsDir for a NON-.esp output (compiled scripts, a packed .bsa,
    /// extracted loose files) — the folder-per-patch model generalised beyond the .esp write path. A fresh marker-stamped
    /// folder (<paramref name="defaultStem"/> names it when patchName is blank; auto-suffixed so a prior one is never
    /// clobbered) or <paramref name="into"/> an existing houseCARL-owned one. ORIGINALS UNTOUCHED (Q3): refuses a folder
    /// houseCARL didn't create. Derives ModsDir CHEAPLY (reads ModOrganizer.ini; NO ~10s index build). Throws the trained
    /// prompt when unconfigured. Reuses the same ownership/marker helpers as the .esp write path. The returned
    /// <see cref="RiderFolder.CreatedFresh"/> flag drives <see cref="RemoveOrNameRiderResidue"/> on a rider failure.</summary>
    public RiderFolder ResolvePatchModFolder(string? patchName, string? into, string defaultStem)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir from the instance, NO resolver build
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                var stem = PatchStem(into);
                var folder = Path.Combine(_modsDir, ModFolderName(stem));
                if (!Directory.Exists(folder))
                    throw new InvalidOperationException(
                        $"cannot extend: no houseCARL patch named '{stem}' (mod folder '{ModFolderName(stem)}' not found). Omit into= to create it fresh.");
                if (!IsHouseCarlOwned(folder))
                    throw new InvalidOperationException(
                        $"cannot extend: mod folder '{ModFolderName(stem)}' was NOT created by houseCARL (no marker) — refusing to write into a folder houseCARL doesn't own (Q3).");
                return new RiderFolder(folder, folder, CreatedFresh: false);   // reused — the user owns it; cleanup leaves it
            }

            var newStem = UniqueStem(PatchStem(string.IsNullOrWhiteSpace(patchName) ? defaultStem : patchName!));
            var newFolder = Path.Combine(_modsDir, ModFolderName(newStem));
            Directory.CreateDirectory(newFolder);
            WriteOwnerMeta(newFolder, "(houseCARL output)");   // ownership marker; this folder may hold scripts / a .bsa / loose files, not an .esp
            return new RiderFolder(newFolder, newFolder, CreatedFresh: true);
        }
    }

    /// <summary>The <c>Scripts\</c> output folder for a COMPILED .pex (the compile rider) — a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>Scripts\</c> subfolder, where MO2 deploys compiled Papyrus into the
    /// game's Data\Scripts. Carries the mod-folder root + fresh flag through for residue cleanup.</summary>
    public RiderFolder ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts");
        var scripts = Path.Combine(f.ModFolder, "Scripts");
        Directory.CreateDirectory(scripts);
        return f with { OutputDir = scripts };
    }

    /// <summary>output_dir= escape hatch (6.3): the user names WHERE the compiled .pex lands, instead of houseCARL cutting a
    /// fresh folder-per-patch mod folder. DECIDED contract (Aaron 2026-06-16): output_dir is a mod-folder ROOT and houseCARL
    /// appends Scripts\ — matching <see cref="ResolveCompiledScriptFolder"/> + MO2's deploy model so the .pex actually loads —
    /// with a DOUBLE-SCRIPTS guard (don't append a second Scripts\ if it's already there). Does NOT call
    /// <see cref="ResolvePatchModFolder"/> (no houseCARL mod folder is cut under ModsDir), and the folder is USER-OWNED — the
    /// returned <see cref="RiderFolder"/> carries CreatedFresh=false, so <see cref="RemoveOrNameRiderResidue"/> never deletes
    /// it on a failed compile (it early-returns on !CreatedFresh). <paramref name="deployWarning"/> is a Q3 note (non-null)
    /// when the final Scripts\ path is under neither the MO2 mods tree nor the game's Data — the .pex compiles but the game
    /// won't auto-load it from there, so a clean "done" is never reported for a .pex that won't deploy. Refuses loud (Q3) on
    /// an unusable output_dir (a malformed path, or a path that names an existing FILE).</summary>
    public RiderFolder ResolveExplicitScriptFolder(string outputDir, out string? deployWarning)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir/DataDir for the deployability check, NO resolver build
            string root;
            try { root = Path.GetFullPath((outputDir ?? "").Trim().Trim('"')); }
            catch (Exception ex) { throw new InvalidOperationException($"output_dir '{outputDir}' is not a usable path ({ex.Message})."); }
            if (File.Exists(root))
                throw new InvalidOperationException($"output_dir '{root}' is a file, not a folder. Give a mod-folder root — houseCARL appends Scripts\\.");

            var (scriptsDir, appended, warn) = ScriptOutputContract(root, _modsDir, _dataDir);
            // Friendly Q3 message if the folder can't be created — e.g. <output_dir>\Scripts already exists AS A FILE, or
            // the path is read-only — instead of letting the IO/access exception reach Guard.Tool's generic "internal
            // failure" (which would wrongly read as a houseCARL bug, not bad input). The File.Exists(root) guard above
            // already catches the common "output_dir itself is a file" shape; this rounds out the rest.
            try { Directory.CreateDirectory(scriptsDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"output_dir: couldn't create the output folder '{scriptsDir}' ({ex.Message}). Check the path and that it's writable."); }
            deployWarning = warn;
            // ModFolder = the mod-folder root (inert here — cleanup is bypassed by CreatedFresh=false — but kept honest):
            // when the user pointed AT a Scripts\ dir, the root is its parent; otherwise the path they gave IS the root.
            var modRoot = appended ? root : (Path.GetDirectoryName(scriptsDir.TrimEnd('\\', '/')) ?? scriptsDir);
            return new RiderFolder(scriptsDir, modRoot, CreatedFresh: false);   // user-owned: residue cleanup never touches it
        }
    }

    /// <summary>PURE (no filesystem access) resolution of the output_dir= contract, so the riskiest 6.3 change is provable in
    /// CI without an MO2 instance. Appends Scripts\ to a mod-folder root, with the DOUBLE-SCRIPTS GUARD (a root already ending
    /// in a Scripts segment — any case, trailing separator tolerated — is taken as-is, never doubled). <paramref name="outputDir"/>
    /// is expected absolute (the caller GetFullPaths it). Returns the final Scripts dir, whether Scripts\ was appended, and a
    /// Q3 deployWarning when the result is under neither <paramref name="modsDir"/> (a mod's Scripts\ is VFS-deployed) nor
    /// <paramref name="dataDir"/> (a direct game install) — the one "this won't load" case the contract can't fix by
    /// construction, so it's surfaced rather than reported as a clean success.</summary>
    internal static (string scriptsDir, bool appendedScripts, string? deployWarning) ScriptOutputContract(
        string outputDir, string modsDir, string dataDir)
    {
        var root = outputDir.TrimEnd('\\', '/');
        bool alreadyScripts = Path.GetFileName(root).Equals("Scripts", StringComparison.OrdinalIgnoreCase);
        var scriptsDir = alreadyScripts ? root : Path.Combine(root, "Scripts");
        // Deployable = the .pex will ACTUALLY auto-load. MO2 overlays a mod folder's CONTENTS onto the game Data root, so a
        // deployable mod Scripts\ is EXACTLY <mods>\<modFolder>\Scripts (mod folder a direct child of mods; Scripts directly
        // under it). A bare <mods>\Scripts (no mod folder) and a nested <mods>\X\Sub\Scripts (lands at Data\Sub\Scripts, not
        // Data\Scripts) do NOT load — so they correctly WARN (review nit: "under mods" alone was too loose). A direct game
        // install loads exactly <data>\Scripts.
        bool deployable = IsModScriptsFolder(scriptsDir, modsDir) || IsDataScriptsFolder(scriptsDir, dataDir);
        string? warn = deployable ? null :
            $"note: '{scriptsDir}' isn't a folder MO2 (or the game) auto-loads scripts from, so the compiled .pex won't " +
            "deploy on its own — it compiled fine, but you must place it where the game loads scripts yourself: a mod's " +
            "own Scripts\\ folder (<mods>\\<YourMod>\\Scripts) or the game's <Data>\\Scripts.";
        return (scriptsDir, !alreadyScripts, warn);
    }

    /// <summary>A Scripts\ folder MO2 actually deploys: <c>&lt;modsDir&gt;\&lt;modFolder&gt;\Scripts</c> exactly — the mod
    /// folder a DIRECT child of the mods root, Scripts directly under it (MO2 maps a mod folder's contents onto the Data
    /// root, so <c>&lt;mods&gt;\Scripts</c> has no mod and <c>&lt;mods&gt;\X\Sub\Scripts</c> lands at Data\Sub\Scripts). Empty
    /// mods root (unconfigured) → false. Case-insensitive, normalized.</summary>
    static bool IsModScriptsFolder(string scriptsDir, string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return false;
        var modFolder = Path.GetDirectoryName(scriptsDir.TrimEnd('\\', '/'));   // expect <mods>\<modFolder>
        return modFolder is not null && PathEquals(Path.GetDirectoryName(modFolder), modsDir);
    }

    /// <summary>A direct game install loads exactly <c>&lt;dataDir&gt;\Scripts</c> (not Data\Sub\Scripts). Empty data dir →
    /// false. Case-insensitive, normalized.</summary>
    static bool IsDataScriptsFolder(string scriptsDir, string dataDir)
    {
        if (string.IsNullOrEmpty(dataDir)) return false;
        return PathEquals(Path.GetDirectoryName(scriptsDir.TrimEnd('\\', '/')), dataDir);
    }

    /// <summary>Case-insensitive equality of two paths after full-path normalization + trailing-separator trim (no
    /// filesystem access). A null left side (no parent — e.g. a drive root) is never equal.</summary>
    static bool PathEquals(string? a, string b)
    {
        if (a is null) return false;
        return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>Source\Scripts\</c> output folder for a DECOMPILED .psc (the decompile rider) — the SE-canonical
    /// source layout, and the same default patch stem as the compile rider so decompile → edit → compile naturally
    /// accumulates in one houseCARL patch folder via <c>into=</c>. Carries the root + fresh flag through for cleanup.</summary>
    public RiderFolder ResolveDecompiledSourceFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts");
        var src = Path.Combine(f.ModFolder, "Source", "Scripts");
        Directory.CreateDirectory(src);
        return f with { OutputDir = src };
    }

    /// <summary>Hunt H2 (Aaron 2026-06-13): a NON-.esp rider (compile / decompile / repack) that FAILED after creating a
    /// fresh houseCARL mod folder cleans up after itself — the .esp F4 "a refusal leaves no orphan folder" principle,
    /// generalised to the riders. If the fresh folder is GENUINELY EMPTY (holds NOTHING but our own meta.ini marker
    /// anywhere in its tree) it is DELETED, so "no output written" is true of the disk; if real output DID land (a
    /// partial .bsa, some written .psc/.pex), the folder STAYS and its path is RETURNED so the rider can NAME it —
    /// houseCARL never deletes content it didn't recognise as its own marker. A REUSED into= folder
    /// (<see cref="RiderFolder.CreatedFresh"/> = false) is never touched: the user owns it. Returns the leftover path to
    /// name, or null (deleted, or nothing to do). Best-effort: a cleanup hiccup never masks the rider's own outcome.</summary>
    internal string? RemoveOrNameRiderResidue(RiderFolder folder)
    {
        if (!folder.CreatedFresh) return null;             // into= reuse — the user owns it, never deleted or named
        var root = folder.ModFolder;
        try
        {
            if (!Directory.Exists(root)) return null;
            bool onlyMarker = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .All(f => Path.GetFileName(f).Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
            if (onlyMarker) { Directory.Delete(root, recursive: true); return null; }   // genuinely empty → gone, nothing to name
            return root;                                   // real output landed → keep it, hand back the path to NAME
        }
        catch (IOException) { return Directory.Exists(root) ? root : null; }
        catch (UnauthorizedAccessException) { return Directory.Exists(root) ? root : null; }
    }

    // ---- Layer B unit D: write the start-game-enabled-quest .seq file (housecarl_write_seq) ----

    /// <summary>The <c>SEQ\</c> output folder for a generated <c>.seq</c> (the SEQ rider) — a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>SEQ\</c> subfolder, where MO2 deploys it into the game's
    /// <c>Data\SEQ</c>. Sibling of <see cref="ResolveCompiledScriptFolder"/>; carries the mod-folder root + fresh flag
    /// through for residue cleanup.</summary>
    public RiderFolder ResolveSeqFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_SEQ");
        var seq = Path.Combine(f.ModFolder, "SEQ");
        Directory.CreateDirectory(seq);
        return f with { OutputDir = seq };
    }

    /// <summary>If <paramref name="pluginPath"/> lives in a houseCARL-owned mod folder DIRECTLY under ModsDir, return that
    /// folder's patch STEM, so the <c>.seq</c> defaults into the SAME folder as the <c>.esp</c> — one mod to enable, and
    /// no second fresh folder the user might forget to enable (a real Q3 footgun: a .seq in an un-enabled folder leaves the
    /// quest silently dead). Only when the folder is the canonical <c>houseCARL - &lt;stem&gt;</c> for THIS plugin (so a
    /// later <c>into=&lt;stem&gt;</c> resolves to exactly this folder); otherwise null → the caller cuts a fresh folder.</summary>
    string? OwnedPluginFolderStem(string pluginPath)
    {
        var dir = Path.GetDirectoryName(pluginPath);
        if (dir is null || Path.GetDirectoryName(dir) is not { } parent || !PathEquals(parent, _modsDir)) return null;
        if (!IsHouseCarlOwned(dir)) return null;
        var stem = PatchStem(Path.GetFileName(pluginPath));
        return Path.GetFileName(dir).Equals(ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? stem : null;
    }

    /// <summary>Write a plugin's start-game-enabled-quest <c>.seq</c> (housecarl_write_seq). Opens <paramref name="plugin"/>
    /// (a path to an .esp/.esm/.esl), collects every Start-Game-Enabled quest it defines, and writes
    /// <c>&lt;ModFolder&gt;\SEQ\&lt;plugin&gt;.seq</c> — the file the engine reads to actually START those quests (the flag
    /// alone does nothing; the same gated, crash-atomic, non-destructive folder-per-patch model as the compile/asset
    /// riders). Output folder DEFAULTS to the plugin's OWN houseCARL folder when it lives in one (so the .seq deploys with
    /// the .esp); else a fresh folder, or <paramref name="into"/>/<paramref name="patchName"/> when given. A plugin with NO
    /// SGE quests writes NOTHING and cuts no folder (a .seq is only needed for SGE quests — Q3, not a silent empty file).
    /// Serialized on the write gate (one write at a time), like its sibling writers.</summary>
    public SeqOutcome WriteSeq(string plugin, string? patchName, string? into)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return SeqOutcome.Fail("no plugin given. Pass plugin= the path to the .esp/.esm/.esl whose start-game-enabled quests need a .seq.");
        plugin = plugin.Trim().Trim('"');
        if (!File.Exists(plugin))
            return SeqOutcome.Fail($"no such plugin file: '{plugin}'. Pass the full path to the plugin (the path housecarl_create_record reported, or any .esp/.esm/.esl).");
        var pluginPath = Path.GetFullPath(plugin);
        if (!PluginExts.Contains(Path.GetExtension(pluginPath), StringComparer.OrdinalIgnoreCase))
            return SeqOutcome.Fail($"'{Path.GetFileName(pluginPath)}' is not a plugin (.esp/.esm/.esl).");

        lock (_writeGate)                                                // one write at a time (hunt F2 sibling): build → resolve → commit
        {
            if (ConfigPromptOrNull() is { } cfgPrompt) return SeqOutcome.Fail(cfgPrompt);   // need ModsDir for the output folder
            lock (_gate) EnsurePathsDerived();                          // derive ModsDir for the owned-folder check (lock order: _writeGate → _gate)

            // Build the .seq from the plugin (read-only overlay, disposed inside — zero handles at rest).
            SeqFile.SeqBuild built;
            try { built = SeqFile.Build(pluginPath); }
            catch (Exception ex)
            { return SeqOutcome.Fail($"could not read '{Path.GetFileName(pluginPath)}' as a plugin: {ex.Message}"); }

            // No SGE quests → no .seq needed; write nothing, cut no folder (Q3: a clean, explicit "nothing to do").
            if (built.Quests.Count == 0)
                return new SeqOutcome(true, null, null, null, built.Quests, built.PluginFileName, false);

            // Output folder: default into the plugin's OWN houseCARL folder; else fresh / explicit into=/patch_name.
            string? autoInto = (string.IsNullOrWhiteSpace(into) && string.IsNullOrWhiteSpace(patchName))
                ? OwnedPluginFolderStem(pluginPath) : null;
            RiderFolder rf;
            try { rf = ResolveSeqFolder(patchName, autoInto ?? into); }
            catch (InvalidOperationException ex) { return SeqOutcome.Fail(ex.Message); }

            // Crash-atomic write of <plugin>.seq under SEQ\ (originals untouched — a houseCARL-owned folder only).
            var seqName = Path.GetFileNameWithoutExtension(pluginPath) + ".seq";
            var dest = Path.Combine(rf.OutputDir, seqName);
            try { AtomicFile.WriteAllBytes(dest, built.Bytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);             // nothing landed → a fresh folder is an orphan
                return SeqOutcome.Fail($"could not write '{seqName}': {ex.Message}"
                    + (residue is null ? "" : $" The freshly created folder was left at '{residue}'."));
            }

            // Integrity (Q3: THIS run wrote it; on-disk size matches the bytes we built — no false success).
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != built.Bytes.Length)
                return SeqOutcome.Fail($"wrote '{seqName}' but its on-disk size ({size}) does not match the {built.Bytes.Length} expected byte(s) — verify before relying on it.");

            return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null);
        }
    }

    // ---- decompiler class hierarchy (lazy, cached for process lifetime) ----------------------------------------

    Dictionary<string, string>? _classParents;
    string? _classParentsNote;
    readonly object _classParentsLock = new();

    /// <summary>Drop the cached hierarchy whenever <see cref="_modsDir"/> can have changed (instance switch /
    /// profile re-derive) — a stale tree's edges could suppress a cast the NEW order's hierarchy doesn't
    /// justify (a recompile-fail, not silent wrong semantics — but stale is stale). Rebuilds lazily.</summary>
    void InvalidateClassParents() { lock (_classParentsLock) { _classParents = null; _classParentsNote = null; } }

    /// <summary>The decompiler's child→parent class map: committed vanilla baseline (beside the exe) + loose .psc
    /// headers across the MO2 mods tree (mods that ship sources — SKSE, PO3, …). Built on FIRST decompile call,
    /// cached for process lifetime (a SOFT input by construction: missing pieces = explicit casts in the output,
    /// never wrong code — the note names any degraded mode, Q3). The input pex's own folder is topped up per call
    /// by the tool, not here (it varies per input). Paths derive FIRST (under the gate — the established
    /// gate→parents lock order): in instance mode ModsDir is lazy, and a decompile-first session used to build
    /// and cache the baseline-only map for process lifetime with the mods-tree harvest silently skipped
    /// (2026-06-12 adversarial hunt F1, proven — the third _modsDir-mutation site missed by the PR #47 fix).</summary>
    public (Dictionary<string, string> Edges, string? Note) ClassParentsForDecompile()
    {
        lock (_gate)
        {
            try { EnsurePathsDerived(); }
            catch { /* unusable instance: the tool's config gate reports it; here = fewer edges, never a throw */ }
        }
        lock (_classParentsLock)
        {
            if (_classParents is null)
            {
                var (edges, note) = HousecarlCore.PapyrusClassParents.LoadBaseline(
                    Path.Combine(AppContext.BaseDirectory, "vanilla-class-parents.json"));
                try
                {
                    if (!string.IsNullOrEmpty(_modsDir) && Directory.Exists(_modsDir))
                        HousecarlCore.PapyrusClassParents.AddFromPscHeaders(edges, new[] { _modsDir });
                }
                catch { /* fewer edges, never fatal — the baseline still applies */ }
                _classParents = edges;
                _classParentsNote = note;
            }
            return (_classParents, _classParentsNote);
        }
    }

    /// <summary>The MO2 mod-folder name for a patch stem. The "houseCARL - " prefix groups our patches in MO2's left
    /// pane and is the human-visible ownership signal (the meta.ini marker is the structural one).</summary>
    static string ModFolderName(string stem) => "houseCARL - " + stem;

    /// <summary>Plugin extensions stripped from a caller-supplied patch name (case-insensitive). NOT every dot — see
    /// <see cref="PatchStem"/>.</summary>
    static readonly string[] PluginExts = { ".esp", ".esm", ".esl" };

    /// <summary>Reduce a caller name to a safe bare STEM — no directory parts (so "../x" / "C:\y" can't escape ModsDir),
    /// stripping ONLY a trailing plugin extension (.esp/.esm/.esl), not every dot. A dotted patch name like
    /// "My.Cool.Patch" must survive intact: Path.GetFileNameWithoutExtension would clip it to "My.Cool" — and then an
    /// into="My.Cool.Patch" extend would look for the wrong folder, a silent name divergence. The plugin is always
    /// <c>&lt;stem&gt;.esp</c>; the mod folder is <c>houseCARL - &lt;stem&gt;</c>.</summary>
    static string PatchStem(string raw)
    {
        var name = Path.GetFileName(raw.Trim());
        foreach (var ext in PluginExts)
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { name = name[..^ext.Length]; break; }
        return string.IsNullOrEmpty(name) ? "houseCARL_Patch" : name;
    }

    /// <summary>The given stem if its mod folder is free, else the first free "<c>&lt;stem&gt;_NNN</c>" — never clobbers
    /// an existing folder (houseCARL's own OR a user's; into= is the way to grow an existing houseCARL patch).</summary>
    string UniqueStem(string stem)
    {
        if (!Directory.Exists(Path.Combine(_modsDir, ModFolderName(stem)))) return stem;
        for (int i = 1; i < 10000; i++)
        {
            var cand = $"{stem}_{i:D3}";
            if (!Directory.Exists(Path.Combine(_modsDir, ModFolderName(cand)))) return cand;
        }
        throw new InvalidOperationException($"too many patches named '{stem}' under ModsDir — clean some out.");
    }

    /// <summary>A mod folder is houseCARL-owned iff its <c>meta.ini</c> carries the <c>[houseCARL] generated=true</c>
    /// marker. The marker lives in meta.ini — the one mod-root file MO2 does NOT deploy into the game Data folder — so it
    /// never pollutes Data. FAIL-SAFE (Q3): a missing / stripped marker reads as NOT owned, so houseCARL refuses to
    /// modify the folder rather than risk touching a user mod.</summary>
    static bool IsHouseCarlOwned(string folder)
    {
        var meta = Path.Combine(folder, "meta.ini");
        if (!File.Exists(meta)) return false;
        bool inMarker = false;
        foreach (var raw in File.ReadLines(meta))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                inMarker = line.Equals("[houseCARL]", StringComparison.OrdinalIgnoreCase);
            else if (inMarker && line.Replace(" ", "").Equals("generated=true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Write the new mod folder's <c>meta.ini</c>: the <c>[houseCARL]</c> ownership marker (MO2-undeployed) plus a
    /// minimal <c>[General]</c> for MO2's display. Format grounded against real MO2 meta.ini (a minimal one is valid;
    /// the custom section is ours). A fresh folder has none, so this just writes it.</summary>
    static void WriteOwnerMeta(string folder, string plugin)
    {
        var content =
            "[General]\r\n" +
            "gameName=skyrimse\r\n" +
            "modid=0\r\n" +
            "version=1.0\r\n" +
            "category=0\r\n" +
            "comments=Generated by houseCARL - load-order patch\r\n" +
            "\r\n" +
            "[houseCARL]\r\n" +
            "generated=true\r\n" +
            $"plugin={plugin}\r\n" +
            $"created={DateTime.UtcNow:o}\r\n" +
            "\r\n" +
            "[installedFiles]\r\n" +
            "size=0\r\n";
        File.WriteAllText(Path.Combine(folder, "meta.ini"), content);
    }

    // ---- corpus-backed type resolution (signature "WEAP" / catalog name "Weapon" → getter Type(s)) -------

    Dictionary<string, List<Type>>? _typeLookup;
    Dictionary<string, List<Type>> TypeLookup => _typeLookup ??= BuildTypeLookup();

    /// <summary>Build the type-string → getter-Type(s) map from the corpus (the authoritative type catalog).
    /// Keyed by BOTH catalog name and 4-char signature; the 2 many-to-one signatures (GMST/GLOB) accumulate
    /// their variants so a signature query unions them. The 2 abstract-group BASE names (Global / GameSetting,
    /// kind="polymorphic-base") map to their concrete arms' getter Types BY CONSTRUCTION — the same arm union the
    /// GMST/GLOB signature already yields — so a query by the base name unions them too and the ambiguity branch at
    /// ResolveTypeFilter's callers names the variants. A corpus AQ name that won't load is skipped here and surfaces
    /// as "unknown type" at query time — never a silent wrong type.</summary>
    static Dictionary<string, List<Type>> BuildTypeLookup()
    {
        var lookup = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, Type t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<Type>();
            if (!list.Contains(t)) list.Add(t);
        }
        var corpus = CorpusRulebook.LoadCorpus();
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "record") continue;
            var t = Type.GetType(ts.GetterInterfaceAssemblyQualified);
            if (t is null) continue;
            Add(ts.Name, t);
            Add(ts.Signature, t);
        }
        // Abstract-group base names (Global / GameSetting) → their concrete arms' getter Types. The arms are listed
        // on the polymorphic-base's own corpus entry, so the union is derived, not hand-wired (cornerstone §3): a query
        // type='Global' resolves to the same set GLOB does, and the existing many-match guidance names the variants.
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "polymorphic-base" || ts.Arms is not { Count: > 0 } arms) continue;
            foreach (var armName in arms)
                if (corpus.Types.TryGetValue(armName, out var arm) && arm.Kind == "record"
                    && Type.GetType(arm.GetterInterfaceAssemblyQualified) is { } at)
                    Add(ts.Name, at);
        }
        return lookup;
    }

    /// <summary>A user type string → its getter Type(s). Throws (Q3) naming the bad input and what's expected.</summary>
    IReadOnlyList<Type> ResolveTypeFilter(string type)
    {
        if (TypeLookup.TryGetValue(type.Trim(), out var types)) return types;
        throw new ArgumentException(
            $"unknown record type '{type}'. Expected a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon').");
    }

    public void Dispose()
    {
        lock (_gate) { _resolver?.Dispose(); _resolver = null; _assetResolver?.Dispose(); _assetResolver = null; }
    }
}

/// <summary>The outcome of a read_record resolve+read. <see cref="Error"/> non-null ⇒ the read failed (with a
/// recoverable, named reason); otherwise <see cref="Record"/> carries the fields read off <see cref="SourcePlugin"/>.</summary>
public sealed record ReadOutcome(
    FormKey FormKey,
    RecordFields? Record,
    string? SourcePlugin,
    string? WinnerPlugin,
    int OverrideDepth,
    IReadOnlyList<string>? TouchingPlugins,
    string? Error)
{
    public static ReadOutcome Fail(FormKey fk, string error) => new(fk, null, null, null, 0, null, error);
}

/// <summary>The outcome of a cross_plugin_query. <see cref="Error"/> non-null ⇒ the query was rejected (with a
/// recoverable, named reason — bad filter combo / unknown type / plugin not in order). Otherwise <see cref="Keys"/>
/// are the matched FormKeys (at most `limit`); <see cref="Prefilled"/> (parallel to Keys) carries the in-hand
/// summaries for the type/plugins paths, or is null for the conflicts_only-alone path (the renderer fills those
/// lazily, bounded by max_chars). <see cref="Sources"/> (parallel to Keys) is the plugin whose body produced each
/// match — the scoped plugin under plugins=, or null under type=/conflicts_only (⇒ the renderer displays the
/// winner) — so the detail render shows the SAME body the scan filtered and display never contradicts filter.
/// <see cref="Total"/> is the true match count; <see cref="Capped"/> is true when Total exceeded what was returned.</summary>
public sealed record CrossQueryOutcome(
    IReadOnlyList<FormKey> Keys, IReadOnlyList<RecordSummary>? Prefilled, int Total, bool Capped, string? Error,
    string? PredicateNote = null, IReadOnlyList<string?>? Sources = null, string? ScanNote = null)
{
    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>A compact, header-only record summary (no field dump) — the per-match line cross_plugin_query emits
/// by default. <see cref="Error"/> non-null ⇒ the winner couldn't be summarised (named, recoverable — Q3).</summary>
public sealed record RecordSummary(FormKey FormKey, string Type, string? EditorId, string Winner, int OverrideDepth, string? Error);

/// <summary>The MATERIALISED conflict tree the render layer consumes — each touching plugin's name + the fields read
/// off its own body, in priority order (winner last). Built by <see cref="LoadOrderService.ResolveTree"/> with the
/// per-call session already disposed, so it carries NO live overlay (Option B — the renderer never holds a handle).</summary>
public sealed record ConflictTreeView(IReadOnlyList<ConflictNodeView> Nodes)
{
    public ConflictNodeView Winner => Nodes[^1];
}

/// <summary>One node of a <see cref="ConflictTreeView"/>: the plugin name + that plugin's record fields (already read).</summary>
public sealed record ConflictNodeView(string Plugin, RecordFields Record);

/// <summary>The data behind housecarl_load_order_status. <see cref="Composition"/> is the fresh enabled/disabled picture;
/// <see cref="ResolvedPluginCount"/> + <see cref="Warnings"/> are the resolver's actual last-build state;
/// <see cref="ProfileChanged"/> is true only when a refresh was attempted but is still pending (e.g. MO2 was mid-write) —
/// houseCARL re-reads automatically on the next tool call; no restart. <see cref="ExcludedPlugins"/> (name → reason) are
/// plugins dropped from the index this build (unopenable, or carrying a record Mutagen can't parse) — surfaced so the
/// user can fix/remove them (Q3).</summary>
public sealed record LoadOrderStatusData(
    Mo2Composition Composition,
    IReadOnlyList<string> Warnings,
    int ResolvedPluginCount,
    int MaxPlugins,
    bool ProfileChanged,
    string ProfileDir,
    string ProfileName,         // the ACTIVE profile (instance mode: MO2's selected_profile; explicit: the dir name) — captured under the gate, not re-derived at render
    string? InstanceDir,        // the resolved MO2 instance folder houseCARL is pointed at; null ⇒ explicit-paths / unconfigured mode
    IReadOnlyDictionary<string, string> ExcludedPlugins);

/// <summary>The result of <see cref="LoadOrderService.NamedProfileComposition"/> — the profiles affordance behind
/// housecarl_load_order_status' profile= param. <see cref="InstanceMode"/> is false in explicit-paths mode (no profiles
/// root — a named read refuses loud). <see cref="AvailableProfiles"/> lists the profile folders (instance mode; empty in
/// explicit mode), used both for the default-status discovery line and to name the options when a requested profile isn't
/// found. <see cref="RequestedName"/> echoes the trimmed name asked for (null if none). <see cref="Composition"/> +
/// <see cref="ResolvedProfileDir"/> are set ONLY when a requested profile was found and read; a non-null RequestedName with
/// a null Composition is the "not found" case (Q3 — AvailableProfiles names the real options, never a silent empty).
/// <see cref="Warnings"/> carries any Q3 notes from reading the inspected profile (e.g. a missing modlist.txt — so a
/// 0-enabled-mods render is never mistaken for a genuinely-empty profile); empty unless a profile was found and read.</summary>
public sealed record NamedProfileResult(
    bool InstanceMode,
    IReadOnlyList<string> AvailableProfiles,
    string? RequestedName,
    string? ResolvedProfileDir,
    Mo2Composition? Composition,
    IReadOnlyList<string> Warnings);

/// <summary>One queried asset path's resolution behind housecarl_asset_status: the resolver's <see cref="AssetHit"/>
/// (which sources have it + which wins + an ambiguity flag), or an <see cref="Error"/> when the path was rejected (a
/// drive-rooted or '..'-escaping path — per-path Q3, never fails the batch). <see cref="Hit"/> is null iff
/// <see cref="Error"/> is set.</summary>
public sealed record AssetPathResult(string RelPath, AssetHit? Hit, string? Error);

/// <summary>The data behind housecarl_asset_status: one <see cref="AssetPathResult"/> per queried path, plus the
/// build-level Q3 caveats — <see cref="BsaFailures"/> (archives that couldn't be read) and <see cref="ReadIncomplete"/>
/// (an Exists=false answer may be wrong because a BSA failed to read) — and <see cref="Warnings"/> from archive
/// discovery (e.g. a Skyrim.ini that couldn't be found, so base-game BSAs weren't scanned). <see cref="ProfileName"/>
/// names the active profile the answer describes.</summary>
public sealed record AssetStatusData(
    IReadOnlyList<AssetPathResult> Results,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>One asset to PLACE (housecarl_place_asset / bulk). <see cref="AssetPath"/> is the resolved Data-relative
/// DESTINATION (the tool computes it from a FormID+slot for FaceGen, or takes a raw path). <see cref="Source"/> is the
/// correct copy to place — a loose file path, "&lt;archive.bsa&gt;|&lt;entry&gt;", or a ".bsa" path (entry := AssetPath);
/// null/blank ⇒ auto-resolve (use the sole VFS provider; &gt;1 ambiguous and 0 absent are per-asset refusals, Q3).</summary>
public sealed record PlaceRequest(string AssetPath, string? Source);

/// <summary>One placed asset's outcome. <see cref="Placed"/> false ⇒ <see cref="Error"/> names why (recoverable, per-asset
/// Q3). <see cref="CurrentWinner"/> is the source that currently wins the VFS for this path (the sort target — the placed
/// copy does NOT win until the fresh mod is enabled + sorted above it), or null if nothing provided it before.</summary>
public sealed record PlaceResult(string AssetPath, bool Placed, long Bytes, string? SourceDesc, string? CurrentWinner, string? Error)
{
    public static PlaceResult Fail(string assetPath, string error, string? currentWinner = null)
        => new(assetPath, false, 0, null, currentWinner, error);
}

/// <summary>The outcome of place_asset / bulk_place_asset. <see cref="Error"/> non-null ⇒ the whole call was rejected
/// before any placement (unconfigured, an into= folder houseCARL doesn't own, the asset layer wouldn't build). Else
/// <see cref="Results"/> is per-asset; <see cref="ModFolder"/> is the houseCARL mod the placed files landed in (null when
/// none placed); <see cref="Warnings"/> carries the asset-discovery caveats (Q3); <see cref="LeftoverFolder"/> names a
/// fresh folder kept because it holds a partial result (no orphan is left for an all-failed fresh batch).</summary>
public sealed record PlaceOutcome(
    IReadOnlyList<PlaceResult> Results, string? ModFolder, IReadOnlyList<string> Warnings, string? LeftoverFolder, string? Error)
{
    public static PlaceOutcome Fail(string error)
        => new(Array.Empty<PlaceResult>(), null, Array.Empty<string>(), null, error);
}

/// <summary>The outcome of housecarl_write_seq. <see cref="Error"/> non-null ⇒ the call was rejected (no plugin, unreadable
/// plugin, an into= folder houseCARL doesn't own, a failed write). On success: <see cref="Quests"/> is every SGE quest
/// covered (EMPTY ⇒ the plugin had none, so <see cref="SeqPath"/> is null and nothing was written — a clean no-op, not a
/// failure); <see cref="SeqPath"/> is the written <c>.seq</c> and <see cref="ModFolder"/> the houseCARL mod it landed in;
/// <see cref="WroteIntoPluginFolder"/> is true when it defaulted into the plugin's OWN folder (so one mod enables both).
/// A write-failure residue path (a fresh folder kept because the write half-landed) is folded into <see cref="Error"/>.</summary>
public sealed record SeqOutcome(
    bool Success, string? Error, string? SeqPath, string? ModFolder,
    IReadOnlyList<HousecarlCore.SeqFile.SeqQuest> Quests, string PluginFileName, bool WroteIntoPluginFolder)
{
    public static SeqOutcome Fail(string error)
        => new(false, error, null, null, Array.Empty<HousecarlCore.SeqFile.SeqQuest>(), "", false);
}

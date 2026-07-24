using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

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
    IReadOnlyList<ActiveArchive> _activeArchives = Array.Empty<ActiveArchive>();   // the discovered active-BSA list behind the CURRENT asset build (archive → owning plugin — the native-pairing audit's provenance anchor); swapped with _assetResolver
    IReadOnlyList<string> _enabledModsAtBuild = Array.Empty<string>();             // the enabled-mod list behind the CURRENT asset build (the native-pairing loader scan walks THESE mods' Root\ folders — same capture as the view, never a second unpinned profile read); swapped with _assetResolver
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
                    _resolver = LoadOrderResolver.Build(paths, ExplainPluginAbsence);
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

    /// <summary>The injected answer to "why is this plugin filename NOT in the active order?" — handed to every
    /// <see cref="LoadOrderResolver"/> this service builds, so a refusal names the cause and its remedy instead of a
    /// flat not-found the reader has to go re-derive (#271). Returns null when nothing can be said, and the refusal
    /// then reads exactly as it did before (a did-you-mean).
    /// <para>Reads the profile FRESH on each call rather than closing over a parsed composition: the composition is a
    /// cheap three-file text parse, this runs only on a REFUSAL (never on a hot path), and a stale answer here would be
    /// the precise failure this whole issue is about — telling someone a plugin is unticked after they ticked it.</para>
    /// <para>The ROOTS are read live from the service's own fields for the same reason, NOT captured when the resolver
    /// was built: a profile switch reassigns them (RederiveIfIniChanged) but only rebuilds the resolver when the
    /// resolved PATH LIST changed, so two profiles with identical active sets and different UNTICKED lists would leave
    /// a captured closure reading the old profile's plugins.txt — answering "not registered" for a plugin that is
    /// merely unticked, which is precisely the confusion this explainer exists to end (review of PR #274).</para>
    /// <para>Vocabulary is deliberate throughout: a MOD is enabled/disabled (MO2's left pane), a PLUGIN is
    /// active/inactive (its right pane). Conflating the two is what made the old output unreadable.</para></summary>
    string? ExplainPluginAbsence(string name)
    {
        // Snapshot the roots together under the gate so the four cannot be read across a mid-switch reassignment.
        string profileDir, modsDir, dataDir, overwriteDir;
        lock (_gate) { profileDir = _profileDir; modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(profileDir)) return null;
        var fn = Path.GetFileName(name.Trim());
        if (fn.Length == 0) return null;
        Mo2Composition comp;
        try { comp = Mo2LoadOrder.ReadComposition(profileDir); }
        catch { return null; }                       // unreadable profile → say nothing rather than guess (Q3)

        bool ticked = comp.ActivePluginNames.Contains(fn);
        bool unticked = comp.InactivePluginNames.Any(x => x.Equals(fn, StringComparison.OrdinalIgnoreCase));

        // The headline case, and the reason this explainer exists: MO2's left pane says yes, its right pane says no.
        // The file is sitting right there, so a bare "not in the load order" reads as "missing" and sends the reader
        // hunting for something that is installed and one click from working.
        if (unticked)
            return $"'{fn}' IS installed, but it is UNTICKED in plugins.txt (MO2's right pane), so the game does not " +
                   "load it and houseCARL does not read it. Tick it in MO2 and re-sort — or, to read the file as-is " +
                   "without loading it, use housecarl_read_plugin_file (a raw, out-of-load-order read).";

        // Ticked but absent from the index: the file itself couldn't be resolved. Locate it to say which.
        PluginFileHit[] hits;
        try { hits = Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fn).ToArray(); }
        catch { hits = Array.Empty<PluginFileHit>(); }

        if (ticked)
            // Ticked AND provided by an enabled layer, yet not indexed — nothing honest left to say (the plugin cap
            // in probe mode reaches here). Saying "no folder provides it" would be flatly false, so say nothing.
            return hits.Any(h => h.Enabled)
                ? null
                : $"'{fn}' is ticked in plugins.txt, but no enabled mod, the overwrite folder, or the game Data folder " +
                  "provides the file — the profile is stale (trigger an MO2 refresh / re-sort so it rewrites the profile files).";

        if (hits.Length == 0) return null;           // nothing on disk by that name → a typo; let the suggester answer

        // On disk but the profile never mentions it. The remedy turns on WHICH layer holds it, read from the mod
        // list rather than guessed from the hit's Enabled flag: an UNLISTED folder is flagged not-enabled exactly
        // like a disabled one, but there is nothing in MO2 to switch on — and houseCARL's own just-written patches
        // live in an unlisted folder, so "switch the mod on" was the wrong first instruction for the single most
        // common way to reach this message (review of PR #274, round 2).
        var pick = hits.FirstOrDefault(h => !h.Enabled) ?? hits[0];
        var folder = Path.GetFileName(Path.GetDirectoryName(pick.Path) ?? "") ?? "";
        var remedy =
            pick.Enabled                                                              ? "Refresh MO2 so it registers the plugin, then tick it and sort"
            : comp.DisabledMods.Any(m => m.Equals(folder, StringComparison.OrdinalIgnoreCase))
                                                                                      ? "Switch that mod on in MO2, then tick the plugin and sort"
                                                                                      : "MO2 has not registered that folder yet — refresh MO2, then tick the plugin and sort";
        return $"'{fn}' is on disk in {pick.Where}, but MO2's load order does not list it, so it is not active. " +
               $"{remedy} — or read the file as-is with housecarl_read_plugin_file.";
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
        _activeArchives = discovery.Archives;   // kept alongside the resolver: archive filename → owning plugin (native-pairing provenance)
        _enabledModsAtBuild = comp.EnabledMods; // same build: the mod set behind this resolver (native-pairing loader scan)
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

    // ---- SKSE-plugin-layer visibility (gap 2026-06-08): inventory the DLLs + configs + winning provider (tier A) and
    //      each plugin DLL's STATICALLY declared manifest (tier C). Read-only; reuses the asset VFS + the PE reader. ----

    /// <summary>Inventory the SKSE-plugin layer as the ACTIVE load order resolves it (housecarl_skse_inventory): the FULL
    /// DEPTH of Data\SKSE\Plugins — every <c>.dll</c> plugin and every <c>.ini</c>/<c>.toml</c>/<c>.json</c>/<c>.yaml</c>
    /// config at any depth — with the mod that WINS the VFS for each (tier A), and for every plugin DLL the statically-
    /// declared manifest — name/author/version, Address Library vs version-LOCKED, target runtimes, XSE floor — via
    /// <see cref="SksePluginReader"/> (tier C). Tier E (DLL behavior) stays out of reach by design. FULL VISIBILITY, by
    /// construction: every file is accounted for — configs carry their derived subfolder <see cref="SkseFileEntry.Group"/>
    /// (the immediate folder under SKSE\Plugins — SkyPatcher / DynamicStringDistributor / OStim / …, WHATEVER the modlist
    /// ships, never a hardcoded set) so the renderer can group them compactly, and non-config content (animation data etc.)
    /// is counted in <see cref="SkseInventoryData.OtherFileCount"/>, never silently dropped. DLLs keep their SKSE-loader
    /// truth: a subfolder DLL is SEEN but flagged (SKSE scans Data\SKSE\Plugins*.dll top-level only, so it isn't loaded as a
    /// plugin). ONE asset capture pins the whole scan (list + <see cref="AssetView.ReadIncomplete"/> caveat = one build); the
    /// enumerate + resolve + PE reads run OUTSIDE the gate (the captured view is a handle-free immutable snapshot), so an
    /// inventory never serializes other tool calls behind its file I/O. Distributor INIs (SPID <c>*_DISTR</c>, KID
    /// <c>*_KID</c>) live in Data\ ROOT, not here, and are owned by their authoring skills — out of this scope by design.</summary>
    /// <param name="peekFilter">Tier D. When non-null, every DLL entry matching it (<see cref="SkseFileEntry.MatchesDll"/> —
    /// the same predicate the renderer filters on) also gets its image string-scanned into <see cref="SkseFileEntry.Peek"/>.
    /// Null = no scan. Per-DLL by design: the scan reads the WHOLE image, unlike the import walk, which rides the manifest
    /// read every DLL already gets.</param>
    public SkseInventoryData SkseInventory(string? peekFilter = null)
    {
        AssetResolver.AssetView view;
        IReadOnlyList<string> warnings;
        string profileName, profileDir;
        lock (_gate)
        {
            EnsurePathsDerived();
            view = Assets.Capture();                              // build/refresh the asset resolver under the gate, ONCE
            warnings = _assetWarnings;
            profileName = _profileName;
            profileDir = _profileDir;
        }
        // Tier D only: the plugin names a peek's embedded-reference cross-check adjudicates against. A cheap three-file
        // text parse (no index build), skipped entirely without peek= so a normal inventory pays nothing for it. The set
        // is what the game actually LOADS: plugins.txt `*` entries PLUS the force-loaded base/CC masters, which load
        // despite never appearing there — omitting the implicit ones would flag "Dawnguard.esm" ABSENT on an install
        // that has it, exactly the false alarm this cross-check exists to prevent (Q3).
        IReadOnlySet<string>? activePlugins = null;
        if (peekFilter is { Length: > 0 })
        {
            var compWarnings = new List<string>();
            activePlugins = PeekPluginSet(Mo2LoadOrder.ReadComposition(profileDir, compWarnings));
            if (compWarnings.Count > 0) warnings = [.. warnings, .. compWarnings];
        }
        // OUTSIDE the gate: the view is pinned + handle-free (AssetResolver.Dispose is a no-op; Resolve reads only the
        // captured snapshot + readonly roots), so enumerating + resolving + PE-reading here can't race a concurrent
        // refresh into wrongness and doesn't block other tools behind our file reads.
        const string pre = "SKSE\\Plugins\\";
        var dlls = new List<SkseFileEntry>();
        var configs = new List<SkseFileEntry>();
        int otherFiles = 0;
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            bool isDll = ext is ".dll";
            bool isConfig = ext is ".ini" or ".toml" or ".json" or ".yaml" or ".yml";
            if (!isDll && !isConfig) { otherFiles++; continue; }  // content/other (.hkx/.txt/.pdb/…) — ACCOUNTED FOR, not listed

            string group = SkseGroupOf(rel, pre);                 // "" = top-level; else the immediate subfolder (the derived group key)
            var place = view.ResolveForPlacement(rel);
            // The FULL conflict chain, winner-first (asset-tool parity): keep every provider + its loose/BSA kind, not just a count.
            var providers = place.Sources
                .Select(s => new SkseProvider(s.ProviderName, KindLabel(s.Kind)))   // shared kind→label helper (explicit switch, never a silent "loose")
                .ToList();
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;

            if (isDll)
            {
                SksePluginReader.SksePluginInfo? info = null;
                string? note = null;
                if (winner is { Kind: AssetKind.Loose, LooseFilePath: { } path })
                    info = SksePluginReader.Read(path);           // the winning loose copy
                else if (winner is null) note = "no active mod provides this DLL";
                else note = "provided ONLY inside a BSA — the SKSE loader scans loose Data\\SKSE\\Plugins only, so this DLL will not load";
                if (group.Length > 0 && note is null)
                    note = $"in subfolder '{group}' — NOT on SKSE's loader path (scans SKSE\\Plugins\\*.dll top-level only); a bundled/parent-loaded DLL, not a plugin SKSE loads";
                var entry = new SkseFileEntry(rel, Path.GetFileName(rel), group, providers, info, note);
                // Tier D string peek — ONLY for a filter-matched DLL with a loose winner (the copy SKSE would load; a
                // BSA-only DLL never loads, so peeking it would describe an image the game never reads).
                if (peekFilter is { Length: > 0 } && entry.MatchesDll(peekFilter)
                    && winner is { Kind: AssetKind.Loose, LooseFilePath: { } peekPath })
                    entry = entry with { Peek = SksePeek.Scan(peekPath) };
                dlls.Add(entry);
            }
            else
                configs.Add(new SkseFileEntry(rel, Path.GetFileName(rel), group, providers, null, null));
        }
        return new SkseInventoryData(dlls, configs, otherFiles, InstalledGameRuntime(), view.BsaFailures, view.ReadIncomplete,
            warnings, profileName, activePlugins, peekFilter is { Length: > 0 });
    }

    /// <summary>Why a LOOSE, loader-scoped SKSE plugin DLL statically cannot load — or null when nothing stops it. The
    /// native-pairing audit's blocker chain for the winning loose copy, in severity order; a non-null result makes the
    /// pairing PAIRED-BUT-DEAD by construction (it rides <see cref="NativePairedDll.LoadBlocker"/>, which
    /// <c>NativePairingWire.Judge</c> already treats as dead — no new verdict arm).
    ///
    /// The DEBUG-build arm is tier D's addition (Aaron-go 2026-07-17) and the reason this is a named function rather
    /// than three inline branches: it is the audit's SEVENTH blocker and the one that read as healthy, because a
    /// debug-built DLL is loose, top-level, x64, readable and usually version-INDEPENDENT — every other check passes it
    /// while the loader refuses it with error 126. Before it, this audit said [LOADS] about the same DLL
    /// <c>skse_inventory</c> called broken: two tools, one file, opposite answers.
    ///
    /// <paramref name="resolvable"/> is injected so the chain is pinnable without a live order or a live machine. That
    /// matters more here than usual: this capability gets NO empirical gate — ARR carries zero debug-built plugins, and
    /// the dev machine HAS the debug runtime (so the dead path cannot be reproduced there either). The guard is the only
    /// evidence, which is exactly why the wiring is a testable function instead of a line inside a 100-line sweep.</summary>
    internal static string? LooseDllBlocker(SksePluginReader.SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Kind == SksePluginReader.SksePluginKind.Unreadable) return $"not a readable SKSE plugin ({info.Note})";
        if (info.Is64Bit == false) return "a 32-bit image — cannot load in Skyrim SE/AE";
        return SksePluginReader.DebugCrtBlocker(info, resolvable);
    }

    /// <summary>The plugin names a tier-D peek adjudicates an embedded reference against — active PLUS the force-loaded
    /// implicit masters (which load despite never appearing in plugins.txt; omitting them would flag Dawnguard.esm
    /// ABSENT on an install that has it). Returns <c>null</c> — never a partial set — when the answer is UNKNOWABLE,
    /// because "I could not determine your order" and "your order is empty" must never render the same (Q3).
    ///
    /// The gate is <see cref="Mo2Composition.OrderedPluginNames"/>, and that exact choice is load-bearing: the implicit
    /// set is DERIVED by iterating <c>ordered</c>, so with loadorder.txt missing it collapses to empty while
    /// plugins.txt can still hand back a perfectly non-empty <c>active</c>. Gating on the MERGED set being non-empty
    /// therefore looks safe and isn't — it returns an active-only set whose force-loaded masters are silently gone, and
    /// every embedded Dawnguard.esm reads "[!] NOT in your load order" on a healthy install. Keying on the input the
    /// implicit half is derived FROM covers both states (both-files-missing is just the sub-case where active is empty
    /// too). Reachable in practice: <see cref="Mo2LoadOrder.ReadComposition"/> never throws on a missing profile file,
    /// the asset resolver needs only modlist.txt, and houseCARL already models the three profile files as
    /// independently mutable (it stats each for freshness) — a mid-re-sort or a fresh profile is enough.
    ///
    /// internal + pure so the skse-peek guard pins THIS decision rather than a copy of it — the arm that missed the
    /// original bug tested a hand-built null instead of the code that has to produce one.</summary>
    internal static IReadOnlySet<string>? PeekPluginSet(Mo2Composition comp)
    {
        if (comp.OrderedPluginNames.Count == 0) return null;   // no loadorder.txt ⇒ the implicit masters are UNKNOWABLE, not absent
        var set = new HashSet<string>(comp.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(comp.ImplicitPluginNames);
        return set.Count > 0 ? set : null;
    }

    /// <summary>The immediate subfolder under SKSE\Plugins a file sits in ("" = directly at top level) — the DERIVED,
    /// by-construction grouping key for the SKSE inventory. Whatever a modlist ships becomes a group; there is NO hardcoded
    /// framework list (a hand-maintained list would be a cornerstone violation + a Q3 silent-miscategorize for any framework
    /// not on it). e.g. <c>SKSE\Plugins\SkyPatcher\Weapons\x.ini</c> → "SkyPatcher"; <c>SKSE\Plugins\EngineFixes.toml</c> → "".</summary>
    static string SkseGroupOf(string rel, string pre)
    {
        if (!rel.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return "";
        int slash = rel.IndexOf('\\', pre.Length);
        return slash < 0 ? "" : rel.Substring(pre.Length, slash - pre.Length);
    }

    // ---- SKSE config audit (tier B, issue #199): cross-check the form references SKSE-plugin configs DECLARE against the
    //      real records of the active load order. Plan: dev/plans/SKSE_TIER_B_CONFIG_AUDIT_PLAN_2026-07-16.md. ----

    /// <summary>Per-file byte cap for the config scan: a config larger than this is a NAMED skip (Q3), not fed to the token
    /// scanner. Real distributor configs are KB-scale; a multi-MB "config" is content mislabeled or a runaway, and scanning
    /// it would waste the whole-layer walk. 16 MB is far above any real config, so the cap trips only on the pathological
    /// case it exists to name.</summary>
    const long SkseConfigSizeCap = 16L * 1024 * 1024;

    /// <summary>Audit the SKSE-plugin config layer against the load order (housecarl_skse_config_audit, tier B). For every
    /// .ini/.toml/.json/.yaml under Data\SKSE\Plugins, read the WINNING copy (VFS truth — losers are never read by the DLL),
    /// extract the form-shaped references + path-segment plugin gates it declares (<see cref="SkseConfigReferenceExtractor"/>),
    /// and resolve each against the active order into a verdict: OK, PLUGIN MISSING, DANGLING, or UNPARSEABLE. The generic,
    /// framework-AGNOSTIC half of the SkyPatcher reader (inventory + reference validity) for the other config folders — it
    /// never interprets what a reference is FOR (that's per-framework skill territory). ONE asset capture + ONE resolver
    /// index pin the whole scan (the SkseInventory discipline); the enumerate + read + resolve run OUTSIDE the gate (the
    /// captured view is handle-free, the index a pure snapshot read). "No references found" is a NORMAL per-file outcome,
    /// accounted for, never a warning (Q3).</summary>
    public SkseConfigAuditData SkseConfigAudit()
    {
        AssetResolver.AssetView view;
        LoadOrderResolver.IndexView index;
        IReadOnlyList<string> warnings;
        string profileName;
        // Capture the asset view AND the record index under ONE gate hold, so a freshness rebuild can't interleave and pair
        // a config read from asset-build-N against a record index from build-N+1 (both share _gate; the Resolver getter
        // reenters it, doing its own per-call freshness inside our hold). Both are handle-free snapshots — the enumerate +
        // read + resolve below then run OUTSIDE the gate without serializing other tools behind our file I/O.
        lock (_gate)
        {
            view = Assets.Capture();
            index = Resolver.Capture();   // pure snapshot: ContainsPlugin / ResolveWinner read only this build
            warnings = _assetWarnings;
            profileName = _profileName;
        }

        const string pre = "SKSE\\Plugins\\";
        var files = new List<SkseConfigFileAudit>();
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (ext is not (".ini" or ".toml" or ".json" or ".yaml" or ".yml")) continue;   // configs only (DLLs/content are SkseInventory's)

            string group = SkseGroupOf(rel, pre);
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;
            var providers = place.Sources.Select(s => new SkseProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

            string? readError = null;
            string text = "";
            if (winner is null)
                readError = "no active mod provides this config";   // shouldn't happen for an enumerated file — named, not assumed (Q3)
            else if (winner.Kind == AssetKind.Loose && winner.LooseFilePath is { } lp && File.Exists(lp) && new FileInfo(lp).Length > SkseConfigSizeCap)
                readError = OverCapNote(new FileInfo(lp).Length);
            else
            {
                var (bytes, err) = AssetResolver.ReadPlacementSource(winner);
                if (err is not null) readError = err;
                else if (bytes!.Length > SkseConfigSizeCap) readError = OverCapNote(bytes.Length);
                else text = DecodeConfigText(bytes);
            }

            // Path-segment gates come from the relPath, so they surface even when the file couldn't be READ (the gate is a
            // property of WHERE the file lives, not its content). Only the token scan needs the text.
            var extracted = SkseConfigReferenceExtractor.Extract(rel, readError is null ? text : "");
            var audited = new List<SkseAuditedRef>(extracted.Count);
            foreach (var r in extracted) audited.Add(Adjudicate(r, index));

            files.Add(new SkseConfigFileAudit(rel, Path.GetFileName(rel), group,
                winner?.ProviderName, providers.Count, providers, audited, readError));
        }
        return new SkseConfigAuditData(files, files.Count, view.BsaFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>Resolve one extracted reference into a verdict against the load-order index. A path-segment gate is
    /// plugin-presence only (OK / PLUGIN MISSING); a form token additionally checks the record exists (DANGLING when the
    /// plugin is present but the masked FormID resolves to nothing). Never speculates about runtime behavior (Q3).</summary>
    internal static SkseAuditedRef Adjudicate(SkseConfigRef r, LoadOrderResolver.IndexView index)   // internal: the config-audit guard drives it over a synthetic order
    {
        if (r.Unparseable is not null)
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, r.Unparseable);

        if (!index.ContainsPlugin(r.Plugin))
            return new SkseAuditedRef(r, SkseRefVerdict.PluginMissing, $"'{r.Plugin}' is not in the active load order");

        if (r.Shape == SkseRefShape.PathSegmentGate)
            return new SkseAuditedRef(r, SkseRefVerdict.Ok, null);   // gate satisfied — the plugin is present

        // Form token: the plugin is present; does the (masked) FormID resolve to a record in the order?
        if (!ModKey.TryFromNameAndExtension(r.Plugin, out var mk))
            return new SkseAuditedRef(r, SkseRefVerdict.Unparseable, $"'{r.Plugin}' is not a valid plugin name");
        var fk = new FormKey(mk, r.LocalId!.Value);
        return index.ResolveWinner(fk) is not null
            ? new SkseAuditedRef(r, SkseRefVerdict.Ok, fk.ToString())
            : new SkseAuditedRef(r, SkseRefVerdict.Dangling, $"{fk} resolves to no record in '{r.Plugin}'");
    }

    /// <summary>Decode a config file's bytes to text, honoring a BOM (UTF-8/16) when present (real shipped configs carry
    /// one), defaulting to UTF-8 otherwise — the config formats (.ini/.toml/.json/.yaml) are all UTF-8 in practice.</summary>
    static string DecodeConfigText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var sr = new StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>The over-size-cap skip note — the actual size to ONE decimal MB so a 16.4 MB file reads "16.4 MB (> 16 MB
    /// cap)", never the self-contradictory "16 MB (> 16 MB cap)" an integer-MB divide produced.</summary>
    static string OverCapNote(long len) =>
        $"config is {len / (1024.0 * 1024):0.0} MB (> {SkseConfigSizeCap / (1024 * 1024)} MB cap) — not scanned";

    // ---- Native-function pairing audit (housecarl_native_pairing_audit; plan
    //      dev/plans/SKSE_NATIVE_PAIRING_AUDIT_PLAN_2026-07-16.md): cross-check the native Papyrus functions the
    //      order's SCRIPTS declare against the DLLs that must implement them — the seam none of validate_scripts
    //      (property binding), skse_inventory (DLL layer), or skse_config_audit (config layer) sees across. ----

    /// <summary>Audit the declaration↔implementation pairing of every native Papyrus class in the active order. One
    /// pass over the winning <c>.pex</c> files (loose + BSA, the ScriptPropertyCheck resolution), extracting native-
    /// flagged declarations (<see cref="HousecarlCore.NativePairing"/>); one pass over SKSE\Plugins for the DLL
    /// candidates each mod ships; then per third-party class the evidence ladder — same-mod DLL, conflict-chain DLL,
    /// or UNPAIRED (a verify flag, never "broken": registration is runtime behavior, the tier-E ceiling).
    ///
    /// The baseline carve-out (§4b — the whole ballgame): a class whose provider CHAIN includes an OFFICIAL archive
    /// (Skyrim.ini base block or a BaseMaster-owned BSA) is ENGINE — implemented by the executable — even when a mod's
    /// loose copy WINS it (SKSE overrides Actor/Game/… with native additions; the official-archive presence still marks
    /// the class baseline, fixture-verified on ARR 2.0). A rung-3 class whose winning provider ALSO provides an
    /// ENGINE class is SKSE CORE — the skse64 scripts payload structurally co-ships ~100+ vanilla overrides with its
    /// new classes (StringUtil/UI/…), and its implementation is the game-root loader, not anything under SKSE\Plugins.
    /// Residual edges, documented not smuggled: an INI-injected third-party BSA reads official (over-baseline), a
    /// paid-CC archive isn't BaseMaster-owned (its engine-native classes read third-party → a verify flag), and a mod
    /// co-shipping a vanilla-script override with a declaration copy of an absent framework gets its copy rescued into
    /// SKSE CORE (visible in the accounting, unflagged) — all three watched at the live gate.
    ///
    /// ONE gate hold captures the asset view + the archive list + warnings (the SkseInventory discipline); the
    /// enumerate + parse + classify run OUTSIDE the gate over the pinned, handle-free view. The per-file Pex parses are
    /// parallelized (thousands of files; the view's caches are concurrency-safe by design) with deterministic output
    /// ordering. An unreadable .pex is a NAMED entry, never a silent skip (Q3).</summary>
    public NativePairingAuditData NativePairingAudit()
    {
        AssetResolver.AssetView view;
        IReadOnlyList<ActiveArchive> archives;
        IReadOnlyList<string> enabledMods;
        IReadOnlyList<string> warnings;
        string profileName, dataDir, modsDir, overwriteDir;
        lock (_gate)
        {
            view = Assets.Capture();
            archives = _activeArchives;         // the SAME build as the view (both swapped under _gate)
            enabledMods = _enabledModsAtBuild;  // ditto — the loader scan below walks the mod set the VIEW describes, never a second unpinned profile read
            warnings = _assetWarnings;
            profileName = _profileName;
            dataDir = _dataDir;
            modsDir = _modsDir;
            overwriteDir = _overwriteDir;
        }

        // ---- the official-archive set: the ENGINE anchor. Filenames, because a BSA provider's name IS the archive filename. ----
        var officialArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
        foreach (var a in archives)
            if (IsOfficialArchive(a, baseMasters))
                officialArchives.Add(Path.GetFileName(a.Path));

        // A BSA provider's NAME is the archive filename — pairing identity needs the MOD that ships the archive
        // (live-gate finding: moreHUD's scripts ride AHZmoreHUD.bsa while its DLL is loose in the SAME mod folder;
        // untranslated, the ladder saw two unrelated providers and called it UNPAIRED). The winning physical path of
        // each active archive names its shipper: mods\<mod>\X.bsa → that mod; overwrite\ → "overwrite"; Data → "Data".
        var archiveShipper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives)
            if (ShipperOfArchivePath(a.Path, modsDir, overwriteDir, dataDir) is { } shipper)
                archiveShipper[Path.GetFileName(a.Path)] = shipper;

        // ---- DLL candidates: one SKSE\Plugins pass. A mod "ships" a DLL when it appears ANYWHERE in that file's
        //      chain (the bundling case pairs through the chain); the health verdict describes the WINNING copy. A
        //      winner that PE-reads as NotSkse — loose OR BSA-packed (review finding: an unscreened packed dependency
        //      fabricated candidacy) — is a bundled dependency, not an implementation candidate. ----
        const string skseRootPre = "SKSE\\Plugins\\";
        var modDlls = new Dictionary<string, List<NativePairedDll>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetExtension(rel).Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            string group = SkseGroupOf(rel, skseRootPre);
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;

            SksePluginReader.SksePluginInfo? info = null;
            string? blocker = null;
            if (winner is null) blocker = "no active mod provides it";
            else if (winner.Kind != AssetKind.Loose)
            {
                blocker = "provided only inside a BSA — the SKSE loader scans loose DLLs only, so it will not load";
                try
                {
                    // PE-screen the packed copy too (DLLs are few — the per-entry read is fine here): a packed
                    // NotSkse dependency must not count as a candidate, or its mod gains phantom pairing evidence.
                    if (AssetResolver.TryReadArchiveEntry(winner.ArchivePath!, winner.EntryPath) is { } bytes)
                        info = SksePluginReader.ReadBytes(Path.GetFileName(rel), bytes);
                }
                catch { /* unreadable archive rides the view's BsaFailures caveat; the candidate keeps its blocker */ }
                if (info?.Kind == SksePluginReader.SksePluginKind.NotSkse) continue;
            }
            else
            {
                info = SksePluginReader.Read(winner.LooseFilePath!);
                if (info.Kind == SksePluginReader.SksePluginKind.NotSkse) continue;   // bundled dependency — not a candidate
                blocker = LooseDllBlocker(info, SksePluginReader.IsSystemDllResolvable);
            }
            if (blocker is null && group.Length > 0)
                blocker = $"in subfolder '{group}' — not on SKSE's loader path (scans SKSE\\Plugins\\*.dll top-level only)";

            var dll = new NativePairedDll(rel, Path.GetFileName(rel), group, winner?.ProviderName, info, blocker);
            foreach (var src in place.Sources)
            {
                var mod = PairingIdentity(src, archiveShipper);
                if (!modDlls.TryGetValue(mod, out var list)) modDlls[mod] = list = new();
                if (!list.Any(d => d.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase))) list.Add(dll);
            }
        }

        // ---- the .pex sweep, two phases. Phase 1 (parallel): resolve every path; parse LOOSE winners in place;
        //      defer BSA winners to a per-archive batch (review finding: per-entry TryReadArchiveEntry re-opens the
        //      archive and walks its whole table each time — O(K·M) against the ~10k-script vanilla archives, the
        //      dominant wall-clock). Phase 2: ONE table walk per archive collects all its wanted entries, then the
        //      parses run parallel over the bytes. Per-file fault isolation throughout: an unreadable .pex is a
        //      NAMED entry (Q3), never a silent skip. ----
        var pexPaths = view.EnumerateUnder("Scripts")
            .Where(p => Path.GetExtension(p).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // Only native-declaring files are kept (a ~48k-file order yields ~200 — carrying every file's providers was
        // pure garbage, review finding); their conflict chains are re-resolved on collection, which is cheap at that count.
        var natives = new System.Collections.Concurrent.ConcurrentBag<(string Rel, IReadOnlyList<HousecarlCore.NativeClassDecl> Decls)>();
        var unreadable = new System.Collections.Concurrent.ConcurrentBag<NativeUnreadablePex>();
        var bsaWanted = new System.Collections.Concurrent.ConcurrentBag<(string Rel, string ArchivePath, string EntryPath, string Provider)>();

        void ParsePex(string rel, string? provider, Func<Mutagen.Bethesda.Pex.PexFile> load)
        {
            try
            {
                var decls = HousecarlCore.NativePairing.ExtractNativeClasses(load());
                if (decls.Count > 0) natives.Add((rel, decls));
            }
            catch (Exception ex)
            {
                unreadable.Add(new NativeUnreadablePex(rel, provider, $"Mutagen cannot read it ({ex.GetType().Name}: {ex.Message}) — the known unreadable-pex class"));
            }
        }

        System.Threading.Tasks.Parallel.ForEach(pexPaths, rel =>
        {
            var place = view.ResolveForPlacement(rel);
            var winner = place.Sources.Count > 0 ? place.Sources[0] : null;
            if (winner is null) { unreadable.Add(new NativeUnreadablePex(rel, null, "enumerated but no active source provides it")); return; }
            if (winner.LooseFilePath is { } lp)
                ParsePex(rel, winner.ProviderName, () => Mutagen.Bethesda.Pex.PexFile.CreateFromFile(lp, Mutagen.Bethesda.GameCategory.Skyrim));
            else
                bsaWanted.Add((rel, winner.ArchivePath!, winner.EntryPath, winner.ProviderName));
        });

        foreach (var g in bsaWanted.GroupBy(w => w.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, byte[]> got;
            try { got = AssetResolver.TryReadArchiveEntries(g.Key, g.Select(w => w.EntryPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList()); }
            catch (Exception ex)
            {
                foreach (var w in g)
                    unreadable.Add(new NativeUnreadablePex(w.Rel, w.Provider, $"archive '{Path.GetFileName(g.Key)}' could not be read ({ex.GetType().Name}: {ex.Message})"));
                continue;
            }
            System.Threading.Tasks.Parallel.ForEach(g, w =>
            {
                if (!got.TryGetValue(w.EntryPath, out var bytes))
                    unreadable.Add(new NativeUnreadablePex(w.Rel, w.Provider, $"vanished from '{Path.GetFileName(g.Key)}' between listing and read"));
                else
                    ParsePex(w.Rel, w.Provider, () =>
                    {
                        using var ms = new MemoryStream(bytes);
                        return Mutagen.Bethesda.Pex.PexFile.CreateFromStream(ms, Mutagen.Bethesda.GameCategory.Skyrim);
                    });
            });
        }

        // ---- classify + pair (sequential — cheap set lookups over ~200 native files). Provenance and pairing key on
        //      the enum-typed PlacementSource, never the render label (review finding: "BSA" the display string must
        //      not double as the semantic discriminator). ----
        var native = natives.OrderBy(s => s.Rel, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Rel, s.Decls, Sources: view.ResolveForPlacement(s.Rel).Sources))
            .ToList();

        // Pass 1: the SKSE-CORE rescue pool — every non-official pairing identity shipping a copy of an ENGINE class.
        var engineProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in native)
            if (HasOfficialSource(s.Sources, officialArchives))
                foreach (var src in s.Sources)
                    if (!(src.Kind == AssetKind.Bsa && officialArchives.Contains(src.ProviderName)))
                        engineProviders.Add(PairingIdentity(src, archiveShipper));
        // "overwrite" is excluded from the rescue: a recompiled vanilla .pex in MO2's overwrite is routine (houseCARL's
        // own compile lane writes there), and letting it baseline-rescue every orphan declaration copy that also lands
        // in overwrite would silence exactly the flag this tool exists for (review finding). "Data" stays — the manual
        // game-folder SKSE install is the layout the rescue must cover; its wider-net residual is documented on Classify.
        engineProviders.Remove("overwrite");

        // Pass 2: build the entries (Classify carries the decision order: engine → ladder → rescue).
        var classes = new List<NativeClassEntry>();
        foreach (var s in native)
        {
            bool engine = HasOfficialSource(s.Sources, officialArchives);
            var identities = s.Sources.Select(src => PairingIdentity(src, archiveShipper)).ToList();
            var display = s.Sources.Select(src => new SkseProvider(src.ProviderName, KindLabel(src.Kind))).ToList();
            foreach (var d in s.Decls)
            {
                var (prov, rung, pairedMod, pairedDlls) = Classify(engine, identities, modDlls, engineProviders);
                classes.Add(new NativeClassEntry(s.Rel, d.ClassName, d.NativeFunctions, display,
                    prov, rung, pairedMod, pairedDlls));
            }
        }

        // SKSE-CORE sanity input: is an skse64 loader visible at all? Two places to look (§4b's optional note):
        // the game ROOT (a manual install), and each enabled mod's Root\ folder — the MO2 Root Builder layout, where
        // the loader lives at mods\<mod>\Root\skse64_loader.exe and only materializes in the game root at launch
        // (live-gate finding: ARR ships SKSE exactly this way, and the root-only check false-noted it). The mod list
        // is the SAME capture as the view (never a second unpinned profile read). Tri-state (Q3, review finding): a
        // check that THREW yields null — "could not check" — never a false "checked and absent".
        bool? loaderSeen;
        try
        {
            static bool LoaderIn(string dir) => Directory.Exists(dir)
                && (File.Exists(Path.Combine(dir, "skse64_loader.exe"))
                    || Directory.EnumerateFiles(dir, "skse64_*.dll").Any());
            var gameDir = dataDir.Length > 0 ? Path.GetDirectoryName(dataDir.TrimEnd('\\', '/')) : null;
            loaderSeen = (gameDir is { Length: > 0 } && LoaderIn(gameDir))
                || (modsDir.Length > 0 && enabledMods.Any(m => LoaderIn(Path.Combine(modsDir, m, "Root"))));
        }
        catch { loaderSeen = null; }

        return new NativePairingAuditData(classes, pexPaths.Count,
            unreadable.OrderBy(u => u.RelPath, StringComparer.OrdinalIgnoreCase).ToList(),
            loaderSeen, InstalledGameRuntime(),
            view.BsaFailures, view.ReadIncomplete, warnings, profileName);
    }

    /// <summary>The MOD a physical archive path belongs to — the pairing identity behind a BSA provider name:
    /// mods\&lt;mod&gt;\X.bsa → that mod folder; the overwrite layer → "overwrite"; the game Data folder → "Data";
    /// anywhere else → null (no translation — the archive name stands). internal for the guard.</summary>
    internal static string? ShipperOfArchivePath(string archivePath, string modsDir, string overwriteDir, string dataDir)
    {
        // Full-path-normalize both sides (the IsUnderModsDir precedent) so forward slashes / '..' segments / a
        // trailing-separator root from config can't make the under-root test disagree with the rest of the plumbing.
        static string Norm(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        archivePath = Norm(archivePath);
        static bool Under(string path, string root, out string remainder)
        {
            remainder = "";
            if (root.Length == 0) return false;
            var r = Norm(root).TrimEnd('\\', '/') + "\\";
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return false;
            remainder = path.Substring(r.Length);
            return true;
        }
        if (Under(archivePath, overwriteDir, out _)) return "overwrite";
        if (Under(archivePath, modsDir, out var rest))
        {
            int slash = rest.IndexOfAny(new[] { '\\', '/' });
            return slash > 0 ? rest[..slash] : null;   // a .bsa directly in mods\ belongs to no mod — no translation
        }
        if (Under(archivePath, dataDir, out _)) return "Data";
        return null;
    }

    /// <summary>An archive is OFFICIAL — its scripts' natives are the engine's own — when it loads from Skyrim.ini's
    /// base [Archive] block or is owned by a base master (Mutagen's implicit list, by construction — never a name
    /// list). internal: the native-pairing guard pins it over synthetic archives.</summary>
    internal static bool IsOfficialArchive(ActiveArchive a, IReadOnlyList<ModKey> baseMasters) =>
        a.OwningPlugin.Equals(ArchiveDiscovery.IniArchiveOwner, StringComparison.OrdinalIgnoreCase)   // ignore-case like every other archive-plumbing compare — the carve-out must not hinge on the marker's casing
        || (ModKey.TryFromNameAndExtension(a.OwningPlugin, out var mk) && baseMasters.Contains(mk));

    /// <summary>True when any source in a file's chain is an official archive — the ENGINE provenance test. Keys on
    /// the <see cref="AssetKind"/> ENUM + archive filename (a BSA source's provider name IS its archive filename), so
    /// a LOOSE override winning the file (SKSE's Actor.pex over Skyrim - Misc.bsa's) still leaves the class baseline,
    /// and a render-label change can never silently break the carve-out (review finding: the display string "BSA" must
    /// not double as the semantic discriminator). internal for the guard.</summary>
    internal static bool HasOfficialSource(IReadOnlyList<PlacementSource> sources, HashSet<string> officialArchives) =>
        sources.Any(s => s.Kind == AssetKind.Bsa && officialArchives.Contains(s.ProviderName));

    /// <summary>One source's PAIRING IDENTITY — the mod it means: a BSA source translates to the mod shipping the
    /// archive (via the archiveShipper map); everything else is its provider name (mod folder / overwrite / Data).
    /// internal for the guard.</summary>
    internal static string PairingIdentity(PlacementSource src, IReadOnlyDictionary<string, string> archiveShipper) =>
        src.Kind == AssetKind.Bsa && archiveShipper.TryGetValue(src.ProviderName, out var mod) ? mod : src.ProviderName;

    /// <summary>The pairing-evidence ladder (§4c) for one third-party class, over the chain's pairing identities
    /// (winner first): rung 1 — the WINNING identity ships ≥1 candidate DLL; rung 2 — an identity deeper in the chain
    /// does (the bundling case: a patch mod wins the script, the framework beneath ships the DLL); rung 3 — nobody in
    /// sight does → UNPAIRED, a verify flag. Within the walk, an identity whose candidates ALL carry a static
    /// LoadBlocker does not stop the descent when a deeper identity has a loadable candidate (review finding: a
    /// bundler shipping one dead helper DLL must not mask the real framework beneath it into a false PAIRED-BUT-DEAD);
    /// if no identity has a loadable candidate, the shallowest with ANY candidate pairs (its deadness is then the
    /// finding). Known residual (PR #210 review #2): "loadable" here means no STATIC blocker — version-locked-vs-
    /// runtime deadness is adjudicated later by the renderer (deadness has one owner, and the ladder deliberately has
    /// no runtime), so a chain-top mod shipping a locked-MISMATCHED DLL still pairs over a loadable framework beneath
    /// it and renders a false PAIRED-BUT-DEAD. Contrived (two chain members implementing the same class, the top one
    /// version-mismatched) and fails toward a false alarm, never a missed problem — accepted, not solved.
    /// Structural (file co-location + VFS chains), never semantic — which DLL implements which class is
    /// tier-E territory. internal for the guard.</summary>
    internal static (NativePairingRung Rung, string? PairedMod, IReadOnlyList<NativePairedDll> Dlls) Ladder(
        IReadOnlyList<string> identities, IReadOnlyDictionary<string, List<NativePairedDll>> modDlls)
    {
        int firstAny = -1;
        for (int i = 0; i < identities.Count; i++)
        {
            if (!modDlls.TryGetValue(identities[i], out var dlls) || dlls.Count == 0) continue;
            if (dlls.Any(d => d.LoadBlocker is null))
                return (i == 0 ? NativePairingRung.SameMod : NativePairingRung.ChainMod, identities[i], dlls);
            if (firstAny < 0) firstAny = i;
        }
        if (firstAny >= 0)
            return (firstAny == 0 ? NativePairingRung.SameMod : NativePairingRung.ChainMod, identities[firstAny], modDlls[identities[firstAny]]);
        return (NativePairingRung.Unpaired, null, Array.Empty<NativePairedDll>());
    }

    /// <summary>The full per-class decision (§4b + §4c), in order: ENGINE (official-archive presence) → the pairing
    /// ladder → the SKSE-CORE rescue for an UNPAIRED class whose WINNING identity also ships an ENGINE-class copy
    /// (skse64's payload structurally co-ships ~100+ vanilla overrides with its new classes). Pairing evidence beats
    /// the rescue — a class that pairs to a DLL stays third-party regardless of its provider's other files. Documented
    /// residual (plan §7, widened by the "Data" identity): a provider that co-ships a vanilla-script override AND a
    /// declaration copy of an absent framework gets that copy rescued into the unflagged baseline — for a mod folder
    /// that's the rare bundler; for the game Data folder it covers everything manually installed there ("overwrite" is
    /// excluded from the pool at the call site for exactly this reason). Visible in the accounting, watched at the
    /// live gate. internal for the guard.</summary>
    internal static (NativeProvenance Provenance, NativePairingRung? Rung, string? PairedMod, IReadOnlyList<NativePairedDll> Dlls) Classify(
        bool engine, IReadOnlyList<string> identities,
        IReadOnlyDictionary<string, List<NativePairedDll>> modDlls, HashSet<string> engineProviders)
    {
        if (engine) return (NativeProvenance.Engine, null, null, Array.Empty<NativePairedDll>());
        var (rung, pairedMod, dlls) = Ladder(identities, modDlls);
        if (rung == NativePairingRung.Unpaired && identities.Count > 0 && engineProviders.Contains(identities[0]))
            return (NativeProvenance.SkseCore, null, null, Array.Empty<NativePairedDll>());
        return (NativeProvenance.ThirdParty, rung, pairedMod, dlls);
    }

    // ---- SkyPatcher distributor Wave 1: the per-record TRUE post-SkyPatcher state (plan
    //      dev/plans/SKYPATCHER_DISTRIBUTOR_TOOL_PLAN_2026-07-08.md — reader-only scope, 2026-07-09). ----

    /// <summary>Cross-call INI parse cache (mtime+length keyed — see <see cref="SkyPatcherDiscovery.ParseCache"/>):
    /// repeat post-state calls over an untouched layer skip every per-file read+parse.</summary>
    readonly SkyPatcherDiscovery.ParseCache _skyPatcherParseCache = new();

    /// <summary>The in-memory scratch mod the replay copy is overridden into — never written to disk.
    /// Named per the Housecarl* scratch-mod convention (<c>HousecarlWriteProof</c> is the sibling).</summary>
    static readonly ModKey SkyPatcherScratchKey = new("HousecarlSkyPatcherScratch", ModType.Plugin);

    /// <summary>
    /// Compute one record's true post-SkyPatcher state: discover the loose SkyPatcher INI layer
    /// (<see cref="SkyPatcherDiscovery"/>), replay each applicable type folder's ordered line union onto a
    /// MUTABLE COPY of the record's load-order winner (<see cref="SkyPatcherOverlay"/> — CLEAN/COLLECTION
    /// resolved, HARD rendered as flagged directives, every gap loud), and return what applied. ONE record
    /// capture + ONE asset capture pin the whole call (the SkseInventory discipline). A record whose type
    /// SkyPatcher doesn't patch, or that no INI touches, is a NAMED outcome — never an empty guess (Q3).
    /// Where a record is fed from two folders (RACE ← race/ + raceHook/), folders apply in field-map
    /// order — a DECLARED assumption (Wave-1 empirical item; the DLL's cross-patcher order is undocumented).
    /// </summary>
    public SkyPatcherPostStateData SkyPatcherPostState(FormKey fk)
    {
        var resolver = Resolver;
        var view = resolver.Capture();
        AssetResolver.AssetView assets;
        IReadOnlyList<string> assetWarnings;
        string profileName;
        lock (_gate)
        {
            assets = Assets.Capture();
            assetWarnings = _assetWarnings;
            profileName = _profileName;
        }

        var fieldMap = SkyPatcherFieldMap.Load();
        var catalog = SkyPatcherCatalog.Load();
        using var session = resolver.OpenSession();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
        var scratch = new SkyrimMod(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
        var formResolver = new SkyPatcherServiceResolver(this, view, session);

        var r = ReplaySkyPatcher(view, session, scan, catalog, fieldMap, scratch, formResolver, fk, linesCache: null);
        if (r.Error is not null) return SkyPatcherPostStateData.Fail(fk, r.Error);
        return new SkyPatcherPostStateData(null, fk, r.EditorId, r.TypeName, r.WinnerPlugin,
            r.Folders, scan.Notes, scan.ReadIncomplete || assets.ReadIncomplete, assetWarnings, profileName);
    }

    /// <summary>The per-record SkyPatcher replay core, shared by the post-state read and the layer
    /// no-op (true-ITM) scan: resolve the winner, materialize a mutable scratch copy, apply every
    /// type folder's ordered lines in field-map order. Error = the named reason the record can't be
    /// replayed (Q3 — the caller decides whether that's a Fail or a skip-with-count).</summary>
    (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error)
        ReplaySkyPatcher(
            LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
            SkyPatcherDiscovery.LayerScan scan, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap,
            SkyrimMod scratch, SkyPatcherOverlay.IFormResolver formResolver, FormKey fk,
            Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>? linesCache)
    {
        var none = new List<SkyPatcherFolderOutcome>();
        var winner = view.ResolveWinner(fk);
        if (winner is null)
            return (null, null, null, none, $"FormID {fk} is not present in the load order ({view.PluginCount} plugins).");

        var body = view.GetRecord(session, winner.Value.WinnerPlugin, fk);
        if (body is null)
            return (null, winner.Value.WinnerPlugin, null, none, $"Winner '{winner.Value.WinnerPlugin}' did not yield {fk} on fetch — a load-order inconsistency.");

        var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
        var maps = fieldMap.ForRecordType(typeName);
        if (maps.Count == 0)
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Record type '{typeName}' is not a SkyPatcher-patchable type (or has no field map) — the SkyPatcher layer cannot touch {fk}.");

        // The running copy: the winner overridden into an in-memory scratch mod (never written to disk).
        // Nested-group types (CELL / REFR / INFO…) need the source link cache to rebuild their parent
        // chain — the same RecordNeedsSourceCache + LinkCacheFor idiom every write path uses (PR #165
        // review finding #1: without it, 2 of the 27 covered types threw unhandled instead of failing named).
        IMajorRecord copy;
        try
        {
            Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache =
                WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(winner.Value.WinnerPlugin) : null;
            copy = WriteEngine.GenericGetOrAddAsOverride(scratch, body, cache);
        }
        catch (Exception ex)
        {
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Could not materialize a mutable copy of {fk} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}");
        }

        var folders = new List<SkyPatcherFolderOutcome>();
        foreach (var m in maps)
        {
            var folder = scan.Folders.FirstOrDefault(f => f.Subfolder.Equals(m.Subfolder, StringComparison.OrdinalIgnoreCase));
            if (folder is null || folder.Catalog is null)
            {
                folders.Add(new SkyPatcherFolderOutcome(m.Subfolder, 0, 0, null, true));
                continue;
            }
            IReadOnlyList<SkyPatcherOverlay.OrderedLine> lines;
            if (linesCache is null) lines = SkyPatcherDiscovery.OrderedLines(folder);
            else if (!linesCache.TryGetValue(folder.Subfolder, out lines!))
                linesCache[folder.Subfolder] = lines = SkyPatcherDiscovery.OrderedLines(folder);
            var result = SkyPatcherOverlay.Apply(copy, fk, body.EditorID, catalog, folder.Catalog, m, lines, formResolver);
            // A toggled-off folder contributes NOTHING — reporting its files as "applied" would assert
            // as live the INIs the DLL skips wholesale (review finding #7). Enabled rides along so the
            // render can say WHY the counts are zero.
            folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                folder.PatchingEnabled));
        }

        return (typeName, winner.Value.WinnerPlugin, body.EditorID, folders, null);
    }

    /// <summary>
    /// Scan the WHOLE SkyPatcher layer (housecarl_skypatcher_layer): every loose INI as the DLL reads
    /// it (ordered union, VFS same-path collisions surfaced, gates + toggles evaluated) plus the
    /// INI-vs-INI same-field SET collisions and the three ITM classes — intra-file dead writes +
    /// cross-INI duplicates (<see cref="SkyPatcherConflicts"/>) and the no-op writes (the per-record
    /// replay below), all report-only. ONE record capture answers the filename gates + ONE asset
    /// capture pins the scan (the SkseInventory discipline); the enumerate + parse + detect run
    /// OUTSIDE the gate on the handle-free captured view. A layer with no INIs is a NAMED outcome,
    /// never an empty guess (Q3).
    /// </summary>
    public SkyPatcherLayerData SkyPatcherLayer()
    {
        var view = Resolver.Capture();
        AssetResolver.AssetView assets;
        IReadOnlyList<string> assetWarnings;
        string profileName;
        lock (_gate)
        {
            assets = Assets.Capture();
            assetWarnings = _assetWarnings;
            profileName = _profileName;
        }

        var catalog = SkyPatcherCatalog.Load();
        var fieldMap = SkyPatcherFieldMap.Load();
        var scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
        var conflicts = new List<SkyPatcherConflicts.SkyPatcherConflict>();
        var itms = new List<SkyPatcherConflicts.SkyPatcherItm>();
        var duplicates = new List<SkyPatcherConflicts.SkyPatcherDuplicate>();
        foreach (var folder in scan.Folders)
        {
            var report = SkyPatcherConflicts.Detect(folder, catalog, fieldMap);
            conflicts.AddRange(report.Conflicts);
            itms.AddRange(report.Itms);
            duplicates.AddRange(report.Duplicates);
        }

        // ---- the TRUE-ITM (no-op write) scan: replay every explicitly-targeted record through the
        //      same per-record core the post-state read uses, and flag SET-class ops whose before ==
        //      after — the line writes the value the record already has at that point in the replay
        //      (which handles chains: a set that restores an earlier INI's change is NOT a no-op).
        //      Broad (type-wide) lines are evaluated only against the explicitly-targeted records —
        //      replaying every record of a type is not attempted; the note says so (Q3). Deliberate
        //      leave-unchanged values ('none') are the author's explicit choice, not flagged. ----
        var noOps = new List<SkyPatcherNoOpWrite>();
        var noOpNotes = new List<string>();
        {
            using var session = Resolver.OpenSession();
            var formResolver = new SkyPatcherServiceResolver(this, view, session);
            var scratch = new SkyrimMod(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
            var linesCache = new Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>(StringComparer.OrdinalIgnoreCase);
            var targets = new HashSet<FormKey>();
            int broadLines = 0, unresolvedTargets = 0, failedReplays = 0;
            foreach (var folder in scan.Folders)
            {
                if (folder.Catalog is null || !folder.PatchingEnabled) continue;
                var eids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ol in SkyPatcherDiscovery.OrderedLines(folder))
                    SkyPatcherConflicts.CollectExplicitPrimaryTargets(ol.Parsed, catalog, folder.Catalog, targets, eids, ref broadLines);
                var folderTypes = fieldMap.ForSubfolder(folder.Subfolder).Select(m => m.RecordType).ToList();
                foreach (var eid in eids)
                {
                    var rfk = folderTypes.Select(t => formResolver.ResolveEditorId(eid, t)).FirstOrDefault(x => x is not null);
                    if (rfk is not null) targets.Add(rfk.Value); else unresolvedTargets++;
                }
            }
            foreach (var fk in targets)
            {
                var r = ReplaySkyPatcher(view, session, scan, catalog, fieldMap, scratch, formResolver, fk, linesCache);
                if (r.Error is not null) { failedReplays++; continue; }
                foreach (var fo in r.Folders)
                {
                    if (fo.Result is not { } res) continue;
                    var map = fieldMap.For(fo.Subfolder, r.TypeName!);
                    foreach (var a in res.Applied)
                        if (SkyPatcherConflicts.IsNoOpWrite(a, map))
                            noOps.Add(new SkyPatcherNoOpWrite(fo.Subfolder, fk.ToString(), r.EditorId,
                                a.FieldPath, a.File, a.LineNumber, a.Op, a.RawValue, a.Before!));
                }
            }
            // Stable output — targets is a hash set, so without this the findings' order varies run
            // to run, and the re-run-to-confirm workflow needs diffable output (review finding).
            noOps.Sort((x, y) =>
            {
                int c = string.Compare(x.File, y.File, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = x.Line.CompareTo(y.Line);
                return c != 0 ? c : string.Compare(x.FormKey, y.FormKey, StringComparison.OrdinalIgnoreCase);
            });
            if (broadLines > 0) noOpNotes.Add($"no-op scan: {broadLines} broad (type-wide) line(s) were evaluated only against the explicitly-targeted records, not every record of their type.");
            if (unresolvedTargets > 0) noOpNotes.Add($"no-op scan: {unresolvedTargets} explicit target(s) did not resolve (the overlay's per-record warnings name them via housecarl_skypatcher_read).");
            if (failedReplays > 0) noOpNotes.Add($"no-op scan: {failedReplays} targeted record(s) could not be replayed (not in the order / unpatchable type / copy failure).");
        }

        return new SkyPatcherLayerData(scan, conflicts, itms, duplicates, noOps, noOpNotes,
            scan.ReadIncomplete || assets.ReadIncomplete, assetWarnings, profileName);
    }

    /// <summary>The live-load-order lookups the overlay needs (<see cref="SkyPatcherOverlay.IFormResolver"/>),
    /// answered off the ONE pinned record view + open session the post-state call holds. EditorID resolution
    /// sweeps the requested type's winners ONCE into an eid→FormKey table (an INI layer typically names many
    /// EditorIDs of the same type — the old per-eid sweep walked the full order N times); a miss is null
    /// (loud upstream), never a guess.</summary>
    sealed class SkyPatcherServiceResolver : SkyPatcherOverlay.IFormResolver
    {
        readonly LoadOrderService _svc;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly Dictionary<string, Dictionary<string, FormKey>> _eidsByType = new(StringComparer.OrdinalIgnoreCase);

        public SkyPatcherServiceResolver(LoadOrderService svc, LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session)
        { _svc = svc; _view = view; _session = session; }

        public FormKey? ResolveEditorId(string editorId, string? mutagenType)
        {
            if (mutagenType is null || string.IsNullOrWhiteSpace(editorId)) return null;
            if (!_eidsByType.TryGetValue(mutagenType, out var eids))
            {
                eids = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
                var types = _svc.ResolveFormScope(mutagenType);
                if (types is not null)
                    foreach (var (candidate, _, cBody) in _view.WinnerRecordsOfType(types))
                        if (cBody.EditorID is { Length: > 0 } eid && !eids.ContainsKey(eid))   // first winner keeps the slot (the old sweep's break-on-first)
                            eids[eid] = candidate;
                _eidsByType[mutagenType] = eids;
            }
            return eids.TryGetValue(editorId, out var fk) ? fk : null;
        }

        public string? ReadWinnerLeaf(FormKey donor, string path)
        {
            var w = _view.ResolveWinner(donor);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, donor);
            if (rec is null) return null;
            var leaf = ReadEngine.ReadFields(rec, new[] { path }).Fields.FirstOrDefault();
            return leaf is { HasValue: true } ? leaf.Token : null;
        }

        public IReadOnlyList<FormKey>? KeywordsOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            var rec = _view.GetRecord(_session, w.Value.WinnerPlugin, record);
            return rec is null ? null : ReadEngine.KeywordKeys(rec);   // the ONE keyword walk (shared)
        }

        public bool PluginPresent(string pluginName) => _view.ContainsPlugin(pluginName);

        public string? WinnerPluginOf(FormKey record) => _view.ResolveWinner(record)?.WinnerPlugin;

        public string? EditorIdOf(FormKey record)
        {
            var w = _view.ResolveWinner(record);
            if (w is null) return null;
            return _view.GetRecord(_session, w.Value.WinnerPlugin, record)?.EditorID;
        }
    }

    // ---- NIF layer Wave 1: read the data values inside one or many meshes (housecarl_nif_inspect) ----

    /// <summary>Inspect the data values inside one or many Skyrim meshes (housecarl_nif_inspect): capture the asset
    /// resolver ONCE under <see cref="_gate"/>, then — per Data-relative path, OUTSIDE the gate on the pinned
    /// handle-free view — resolve through the MO2 VFS to the WINNING copy (or the <paramref name="mod"/>-named
    /// provider), read that copy's bytes IN PROCESS (a loose file, or a single entry out of a BSA via native Mutagen —
    /// no disk extraction), and hand them to <see cref="NifService.Inspect"/> for the header / block census / shapes /
    /// partitions / alpha / textures / node tree / string table. Read-only. Results come back in INPUT ORDER, one per
    /// path; a per-path failure (empty/invalid path, ABSENT, a mod= that doesn't provide it, unreadable bytes, a parse
    /// refusal) is a NAMED per-path <see cref="NifInspectData.Error"/> that never aborts the rest of the batch (Q3).
    /// The asset-tool parity is carried through per path — the full winner→loser provider chain (each tagged
    /// loose/BSA) and the ambiguity flag — while the build-level Q3 caveats (<see cref="AssetView.BsaFailures"/>,
    /// discovery warnings) ride ONCE on the batch, so an ABSENT answer is never over-trusted. The single capture is
    /// what makes a load-order-wide facegen sweep one call instead of one per mesh (issue #229); a single-path call is
    /// simply a batch of one.</summary>
    public NifInspectBatchData NifInspect(IReadOnlyList<string> relPaths, string? mod)
    {
        AssetResolver.AssetView view;
        IReadOnlyList<string> warnings;
        string profileName;
        lock (_gate)
        {
            view = Assets.Capture();                              // build/refresh the asset resolver under the gate, ONCE per batch
            warnings = _assetWarnings;
            profileName = _profileName;
        }

        // OUTSIDE the gate: the captured view is pinned + handle-free, so resolving + reading + parsing here can't race a
        // concurrent refresh into wrongness and doesn't block other tools behind our file reads.
        var results = new List<NifInspectData>(relPaths.Count);
        foreach (var raw in relPaths)
        {
            var rel = (raw ?? "").Trim();
            // The isolation contract holds by CONSTRUCTION, not by callee audit (PR #243 review): anything unexpected
            // from ONE path's resolve/read/parse becomes THAT path's named error, never the whole batch's (Q3).
            try { results.Add(NifInspectOne(view, rel, mod)); }
            catch (Exception ex) { results.Add(NifInspectData.Fail(rel, $"unexpected error inspecting this path — {ex.GetType().Name}: {ex.Message}")); }
        }
        return new NifInspectBatchData(results, view.BsaFailures, warnings, profileName);
    }

    /// <summary>One path's inspect against the already-captured view — the per-path body of <see cref="NifInspect"/>.
    /// Every failure is a NAMED per-path outcome, never a throw (Q3).</summary>
    static NifInspectData NifInspectOne(AssetResolver.AssetView view, string rel, string? mod)
    {
        if (rel.Length == 0)
            return NifInspectData.Fail("", "empty mesh path. Pass a Data-relative path, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.");

        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return NifInspectData.Fail(rel, $"invalid path — {ex.Message}"); }

        var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

        if (place.Sources.Count == 0)
            // Absent=true lets the renderer hedge this at POINT OF USE on the batch-level caveats (read failures /
            // discovery warnings) — asset_status parity; the top-of-output alarm alone scrolls away in a long batch.
            return new NifInspectData(rel, null, providers, place.Ambiguous, Absent: true, null,
                "ABSENT — no active mod or BSA provides this mesh path.");

        // Pick the copy to read: the VFS winner by default, or a specific provider when mod= names one.
        PlacementSource chosen;
        if (!string.IsNullOrWhiteSpace(mod))
        {
            var pick = place.Sources.FirstOrDefault(s => s.ProviderName.Equals(mod!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (pick is null)
                return new NifInspectData(rel, null, providers, place.Ambiguous, false, null,
                    $"mod '{mod!.Trim()}' does not provide this path. Providers (winner first): {string.Join(", ", providers.Select(p => p.Name + " (" + p.Kind + ")"))}.");
            chosen = pick;
        }
        else chosen = place.Sources[0];

        var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
        if (bytes is null)
            return new NifInspectData(rel, new NifProvider(chosen.ProviderName, KindLabel(chosen.Kind)), providers, place.Ambiguous,
                false, null, readErr ?? "could not read the resolved mesh bytes.");

        var outcome = NifService.Inspect(bytes);
        return new NifInspectData(rel, new NifProvider(chosen.ProviderName, KindLabel(chosen.Kind)), providers, place.Ambiguous,
            false, outcome.Inspect, outcome.Error);
    }

    /// <summary>Render an <see cref="AssetKind"/> as the tool-facing label ("loose" / "BSA"). An explicit switch (not a
    /// ternary) so a future AssetKind arm renders its real name, never a silent mislabel.</summary>
    static string KindLabel(AssetKind k) => k switch { AssetKind.Bsa => "BSA", AssetKind.Loose => "loose", var other => other.ToString() };

    // ---- NIF layer Wave 2: whitelist WRITES into a mesh (housecarl_nif_set) ----

    /// <summary>Apply the N2-whitelist write op(s) to a mesh (housecarl_nif_set): resolve the Data-relative
    /// <paramref name="relPath"/> to the WINNING copy (or <paramref name="mod"/>'s copy), read its bytes IN PROCESS, hand
    /// them to <see cref="NifService.Set"/> (which applies + VERIFIES with the two offset-immune gates, or refuses loud —
    /// nothing is written to disk unless it verified), then place the verified bytes. Two lanes, mirroring the record
    /// write lanes:
    ///   • DEFAULT (non-destructive): write into a NEW houseCARL-owned MO2 mod folder (same relative path) that the modder
    ///     enables + sorts ABOVE the current winner so the edited copy WINS the VFS — originals untouched. A BSA-packed
    ///     source naturally becomes a loose winning override this way.
    ///   • IN-PLACE (opt-in, <paramref name="inPlace"/>): OVERWRITE the winning LOOSE file where it sits, riding the SAME
    ///     persistent first-touch consent handshake as the record in-place lane (keyed on the resolved file path,
    ///     <see cref="UserConfigStore"/>) — no backup. A BSA-only winner can't be edited in place (there is no loose file);
    ///     that refuses with the default-lane guidance.
    /// Serialized on the write gate. Q3 honesty: for the default lane "wrote it" ≠ "it wins" — the render says to enable +
    /// sort the fresh mod; this never claims the fix took effect on write.</summary>
    public NifSetResult NifSet(string relPath, IReadOnlyList<NifSetOp> ops, string? mod, string? patchName, string? into, bool inPlace, bool acknowledge)
    {
        var rel = (relPath ?? "").Trim();
        if (rel.Length == 0) return NifSetResult.Fail("no mesh path given. Pass a Data-relative path, e.g. 'meshes\\armor\\iron\\cuirass_1.nif'.");
        if (ops is null || ops.Count == 0) return NifSetResult.Fail("no write op given — pass at least one op (e.g. set_flags, rename_shape).");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return NifSetResult.Fail("in_place and into are mutually exclusive — in_place overwrites the winning file where it sits; into= names a NEW houseCARL folder.");

        lock (_writeGate)
        {
            AssetResolver.AssetView view; IReadOnlyList<string> warnings; string profileName;
            try { lock (_gate) { view = Assets.Capture(); warnings = _assetWarnings; profileName = _profileName; } }
            catch (Exception ex) { return NifSetResult.Fail($"could not resolve the asset layer (the MO2 instance may not be readable): {ex.Message}"); }

            PlacementResolution place;
            try { place = view.ResolveForPlacement(rel); }
            catch (ArgumentException ex) { return NifSetResult.Fail($"invalid path — {ex.Message}"); }

            var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();
            if (place.Sources.Count == 0)
                return NifSetResult.Fail($"ABSENT — no active mod or BSA provides '{rel}', so there is no copy to edit.", providers, profileName);

            // pick the copy to read/edit: the VFS winner, or a specific provider when mod= names one.
            PlacementSource chosen;
            if (!string.IsNullOrWhiteSpace(mod))
            {
                var pick = place.Sources.FirstOrDefault(s => s.ProviderName.Equals(mod!.Trim(), StringComparison.OrdinalIgnoreCase));
                if (pick is null)
                    return NifSetResult.Fail($"mod '{mod!.Trim()}' does not provide this path. Providers (winner first): {string.Join(", ", providers.Select(p => p.Name + " (" + p.Kind + ")"))}.", providers, profileName);
                chosen = pick;
            }
            else chosen = place.Sources[0];

            var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
            if (bytes is null) return NifSetResult.Fail(readErr ?? "could not read the resolved mesh bytes.", providers, profileName);

            // ---- apply + verify (pure; NOTHING is written unless this returns verified bytes) ----
            var outcome = NifService.Set(bytes, ops);
            if (outcome.Error is not null) return NifSetResult.Fail(outcome.Error, providers, profileName);
            var editedBytes = outcome.WrittenBytes!;
            var report = outcome.Report!;
            var chosenProv = new NifProvider(chosen.ProviderName, KindLabel(chosen.Kind));
            // Did we edit the VFS WINNER, or a copy a mod=-named loser shadows? Drives the honest "is it live" wording.
            bool editedIsWinner = place.Sources.Count > 0 && ReferenceEquals(chosen, place.Sources[0]);

            // ---- IN-PLACE lane ----
            if (inPlace)
            {
                if (chosen.Kind != AssetKind.Loose || string.IsNullOrEmpty(chosen.LooseFilePath))
                    return NifSetResult.Fail(
                        $"in-place needs a LOOSE copy to overwrite, but '{rel}' resolves to {chosen.ProviderName} ({KindLabel(chosen.Kind)}). " +
                        "Drop in_place to write a loose winning override into a new houseCARL folder instead (the default lane).", providers, profileName);
                var targetPath = chosen.LooseFilePath!;
                var meshName = Path.GetFileName(targetPath);

                bool already = _store.IsInPlaceAcknowledged(targetPath);
                if (!already && !acknowledge)
                    return NifSetResult.NeedsAck(NifInPlaceHandshakeText(meshName, targetPath), chosenProv, providers, profileName);
                string? ackNote = null;
                if (!already && acknowledge)
                {
                    var (ok, err) = _store.RecordInPlaceAcknowledged(targetPath);
                    if (!ok) ackNote = $"the in-place acknowledgement could not be saved ({err}) — the edit proceeded, but a future session will re-prompt for this file.";
                }

                if (InPlaceParentUnwritable(targetPath, out var why)) return NifSetResult.Fail(why, providers, profileName);
                try { AtomicFile.WriteAllBytes(targetPath, editedBytes); }
                catch (Exception ex) { return NifSetResult.Fail($"could not overwrite '{targetPath}' in place: {ex.Message}. Nothing was written.", providers, profileName); }
                long sz; try { sz = new FileInfo(targetPath).Length; } catch { sz = -1; }
                if (sz != editedBytes.Length)
                    return NifSetResult.Fail($"wrote '{meshName}' but its on-disk size ({sz}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);

                return NifSetResult.OkInPlace(rel, chosenProv, providers, place.Ambiguous, editedIsWinner, report, targetPath,
                    MergeWarnings(report.Warnings, warnings, ackNote), profileName);
            }

            // ---- DEFAULT (new-folder) lane ----
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_NifEdit"); }
            catch (InvalidOperationException ex) { return NifSetResult.Fail(ex.Message, providers, profileName); }

            var dest = Path.Combine(rf.OutputDir, rel);
            try { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); AtomicFile.WriteAllBytes(dest, editedBytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);
                return NifSetResult.Fail($"could not write '{rel}' into the patch folder: {ex.Message}"
                    + (residue is null ? "" : $" The freshly created mod folder was left at '{residue}'."), providers, profileName);
            }
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != editedBytes.Length)
            {
                RemoveOrNameRiderResidue(rf);
                return NifSetResult.Fail($"wrote '{rel}' but its on-disk size ({size}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);
            }

            string? winner = providers.Count > 0 ? $"{providers[0].Name} ({providers[0].Kind})" : null;
            return NifSetResult.OkNewFolder(rel, chosenProv, providers, place.Ambiguous, report, rf.ModFolder, winner, MergeWarnings(report.Warnings, warnings, null), profileName);
        }
    }

    /// <summary>Merge the write report's notes (e.g. the unknown-block preservation disclosure) with the asset-layer
    /// discovery warnings and an optional extra note (in-place ack-save failure) — BOTH lanes surface BOTH sets (Q3: a
    /// preservation disclosure must not vanish because it rode the other lane's list).</summary>
    static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> reportWarnings, IReadOnlyList<string> assetWarnings, string? extra)
    {
        var list = new List<string>(reportWarnings.Count + assetWarnings.Count + 1);
        list.AddRange(reportWarnings);
        list.AddRange(assetWarnings);
        if (extra is not null) list.Add(extra);
        return list;
    }

    /// <summary>The mesh-specific first-touch in-place consent prompt. Distinct from the PLUGIN handshake
    /// (<see cref="InPlaceHandshakeText"/>) whose "whole plugin re-serialized / engine-reserved sub-0x800 records" wording
    /// is false for a .nif — a mesh write is a WHOLE-FILE NiflySharp re-serialization (not a byte-surgical patch), then
    /// verified. The no-backup + opt-in trade-offs carry over.</summary>
    static string NifInPlaceHandshakeText(string meshName, string path) =>
        $"in-place edit of '{meshName}' — first-time confirmation (shown once for this mesh):\n" +
        $"  • This overwrites your ORIGINAL file ({path}). It will NO LONGER be untouched, and houseCARL keeps NO backup or undo — keep your own.\n" +
        "  • The written mesh is a WHOLE-FILE re-serialization through NiflySharp's canonical writer (the way NifSkope / BodySlide rewrite a mesh on save), NOT a byte-surgical patch — then VERIFIED (only the value you edited changed; it reloads as a valid SE mesh).\n" +
        "  • It still refuses if the mesh can't be parsed or isn't a Skyrim SE stream.\n" +
        "  • The default lane (a NEW mod folder, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

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

    /// <summary>Read MO2's OWN local Nexus update cache — the modid / version / newestVersion / ignoredVersion /
    /// lastNexusUpdate fields in every managed mod's meta.ini — with NO network (MO2 already paid the API cost). The
    /// cheap local pre-filter for update triage: it names which mods MO2 already learned a newer version for, plus the
    /// raw fields so the caller can verify online. Enabled/disabled comes from the ACTIVE profile. Config-gated and uses
    /// the same lazy path derivation as the other reads; a missing mods folder is NAMED, never a silent empty (Q3).
    /// Only Nexus-linked mods (a real modid) become entries; hand-installed mods / separators are counted, not listed.</summary>
    public UpdateCacheData UpdateCache()
    {
        string modsDir, profileDir; string? instanceDir;
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();               // fresh install → the tool surfaces the trained prompt
            EnsurePathsDerived();                                  // instance mode: derive _modsDir/_profileDir from the ini (throws Q3 if unusable)
            modsDir = _modsDir; profileDir = _profileDir; instanceDir = _instanceDir;
        }

        if (string.IsNullOrEmpty(modsDir) || !Directory.Exists(modsDir))
            return new UpdateCacheData(modsDir, instanceDir, Array.Empty<ModUpdateEntry>(), new[] { $"the mods folder is missing: '{modsDir}'" }, 0);

        // Enabled/disabled from the active profile (cheap text read, OUTSIDE the gate; explicit-paths mode may have no
        // profile → every mod's state is 'unknown', which the render states rather than guessing).
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(profileDir) && Directory.Exists(profileDir))
        {
            var comp = Mo2LoadOrder.ReadComposition(profileDir);
            foreach (var e in comp.EnabledMods) enabled.Add(e);
            foreach (var d in comp.DisabledMods) disabled.Add(d);
        }

        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modsDir); }
        catch (Exception ex)
        { return new UpdateCacheData(modsDir, instanceDir, Array.Empty<ModUpdateEntry>(), new[] { $"cannot list the mods folder '{modsDir}': {ex.Message}" }, 0); }

        var entries = new List<ModUpdateEntry>();
        int untracked = 0;
        foreach (var dir in dirs)
        {
            var folder = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, "meta.ini");
            if (!File.Exists(metaPath)) { untracked++; continue; }     // separators / hand-installed mods carry no meta.ini
            var meta = Mo2ModMeta.Read(metaPath);
            if (meta is null || meta.ModId == 0) { untracked++; continue; }   // not a Nexus-linked mod → not update-checkable
            bool? state = enabled.Contains(folder) ? true : disabled.Contains(folder) ? false : (bool?)null;
            entries.Add(new ModUpdateEntry(
                folder, state, meta.ModId, meta.Version, meta.NewestVersion, meta.IgnoredVersion, meta.LastNexusUpdate,
                meta.InstalledFileIds));
        }
        entries.Sort((a, b) => string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase));
        return new UpdateCacheData(modsDir, instanceDir, entries, Array.Empty<string>(), untracked);
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
        System.Threading.Interlocked.Increment(ref _gameRootsGen);   // the game roots moved → the runtime memo re-probes (PR #210 review #1)
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
                // The rebuild must carry the explainer too, or a profile change (the very act that creates an unticked
                // plugin) would silently drop every refusal back to the flat not-found — the exact "armed the reported
                // lane, missed its twin" mistake #270 kept making.
                var rebuilt = LoadOrderResolver.Build(paths, ExplainPluginAbsence);
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
    // InstalledGameRuntime's memo: the resolved exe re-validated by a cheap mtime stat per call; a probed MISS is
    // session-stable (no re-paying the GameLocator registry/Steam walk per tool call for a permanently-null answer).
    // _gameRootsGen is the memo's INVALIDATION signal (PR #210 review finding #1): every site that re-points the game
    // roots (SetInstance, RederiveIfIniChanged's switch) bumps it, and a memo cached at an older generation re-probes —
    // otherwise an instance switch could keep adjudicating version-LOCKED plugins against the PREVIOUS install's exe
    // (a silently wrong PASS/FAIL), or stay stuck at a prior instance's null forever. A lock-free Interlocked counter,
    // NOT a locked reset: the bump sites hold _gate, and taking _runtimeGate under _gate would invert the
    // _runtimeGate → _gate order InstalledGameRuntime establishes (deadlock), so the roots side never takes _runtimeGate.
    readonly object _runtimeGate = new();
    int _gameRootsGen;
    int _runtimeGen = -1;   // generation the memo was cached at; -1 = never probed
    string? _runtimeExe, _runtimeVersion;
    DateTime _runtimeExeMtime;

    /// <summary>The INSTALLED game runtime version — the dotted file version of the SkyrimSE.exe the load order runs
    /// (e.g. "1.6.1170.0") — or null when it can't be resolved. This is what turns a version-LOCKED SKSE plugin's
    /// compat list from "verify against your game version" into PASS/FAIL (native-pairing audit §4d + skse_inventory's
    /// locked diagnostic). Candidates are exactly <see cref="CompilerGameDirHints"/> — load-order game dir first (an
    /// MO2 "Stock Game" setup launches THAT copy's exe, and downgrade patchers rewrite it in place, so its version is
    /// the truth), located install as fallback — one shared derivation, never a re-typed copy. BEST-EFFORT + NULL-SAFE
    /// end to end: a miss degrades the finding wording, never fails a tool. Memoized: the resolved exe is re-validated
    /// by mtime per call; a full miss is cached for the session. Residual, documented: if MO2 launches an exe that is
    /// neither in the load-order game dir nor the located install, the read can describe a different binary — the
    /// renders name the version they adjudicated against so a wrong baseline is visible, not silent.</summary>
    public string? InstalledGameRuntime()
    {
        lock (_runtimeGate)
        {
            int gen = System.Threading.Volatile.Read(ref _gameRootsGen);
            if (_runtimeGen == gen)                     // cached at the CURRENT roots generation (else: re-probe — the instance moved)
            {
                if (_runtimeExe is null) return null;   // generation-stable miss
                try
                {
                    if (File.Exists(_runtimeExe) && File.GetLastWriteTimeUtc(_runtimeExe) == _runtimeExeMtime)
                        return _runtimeVersion;         // unchanged exe → cached answer
                }
                catch { return _runtimeVersion; }       // stat hiccup → the cached answer beats a re-probe mid-hiccup
            }
            _runtimeGen = gen; _runtimeExe = null; _runtimeVersion = null;
            foreach (var dir in CompilerGameDirHints())
            {
                try
                {
                    var exe = Path.Combine(dir, "SkyrimSE.exe");
                    if (!File.Exists(exe)) continue;
                    var fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                    // FileVersion can carry vendor noise; the numeric parts are the truth. Prefer them when present.
                    string? v = fv.FileMajorPart > 0 || fv.FileMinorPart > 0 || fv.FileBuildPart > 0 || fv.FilePrivatePart > 0
                        ? $"{fv.FileMajorPart}.{fv.FileMinorPart}.{fv.FileBuildPart}.{fv.FilePrivatePart}"
                        : string.IsNullOrWhiteSpace(fv.FileVersion) ? null : fv.FileVersion!.Trim();
                    if (v is null) continue;
                    _runtimeExe = exe; _runtimeExeMtime = File.GetLastWriteTimeUtc(exe); _runtimeVersion = v;
                    return v;
                }
                catch { /* unreadable exe → try the next candidate (best-effort) */ }
            }
            return null;
        }
    }

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
            System.Threading.Interlocked.Increment(ref _gameRootsGen);   // new instance = possibly a different game install — the runtime memo must re-probe, never adjudicate B's locked plugins against A's exe (PR #210 review #1)
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
    public ReadOutcome ResolveRead(FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                   bool resolveNames = false, Dictionary<FormKey, ResolvedRef>? linkMemo = null,
                                   string? containerHint = ReadEngine.DepthExpandHint)
    {
        var resolver = Resolver;
        return ResolveRead(resolver, resolver.Capture(), fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint);
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
                            FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                            bool resolveNames = false, Dictionary<FormKey, ResolvedRef>? linkMemo = null,
                            string? containerHint = ReadEngine.DepthExpandHint)
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
        {
            // AbsenceClause subsumes the did-you-mean: it states the CAUSE when there is one (the plugin is installed
            // but unticked / its mod is off) and falls back to the suggester when there isn't, so a real installed
            // plugin is never answered with a spelling guess (#271).
            // ExplainAbsence, NOT AbsenceClause: the latter returns a non-empty string for a typo too (the did-you-mean),
            // so its length cannot tell "a cause was stated" from "a spelling was guessed" — and only the first should
            // change the tail below.
            var cause = view.ExplainAbsence(plugin);
            var why = cause is not null ? " " + cause : view.NameSuggestion(plugin);
            // Only ONE sentence of the legacy tail actually contradicts a stated cause: the posture line ("does not open
            // disabled plugins off disk"), which fights the explainer's raw-read pointer. Round 1 dropped the whole
            // paragraph with it — and houseCARL writes its own patches into an UNLISTED mod folder, which the explainer
            // now explains, so the freshly-written-patch case (the commonest reason to hit this refusal at all) lost the
            // full_readback verify path that is the only way to check a write without touching MO2. The write-verify
            // guidance is a fact about the tool, not a guess about the cause, so it is now unconditional; only the
            // contradicting posture line and the cause-guessing sentence are conditional (review of PR #274, round 2).
            var verify = $" To verify a write BEFORE enabling, use the write call's own read-back (full_readback=true " +
                         $"returns the whole written record). If a prior write into '{plugin}' reported success, the edits " +
                         "DID land — do not re-issue them (re-running list Adds would duplicate entries).";
            var tail = (cause is not null
                ? ""
                : " houseCARL reads load-order truth only and does not open disabled " +
                  "plugins off disk. If this is a freshly written houseCARL patch, it isn't enabled yet: enable + sort it in " +
                  "MO2, then re-read.") + verify;
            return ReadOutcome.Fail(fk,
                $"Plugin '{plugin}' is not in the load order ({view.PluginCount} plugins; names match the plugin FILENAME " +
                "incl. .esp/.esm, case-insensitively)." + why + tail);
        }

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

        var record = ReadEngine.ReadFields(rec, fields, depth, containerHint);   // materialise while the session (overlay) is open
        if (resolveNames) record = AnnotateLinks(record, view, session, linkMemo ?? new());   // P7: identity of every FormLink token, DISPLAY-ONLY (same open session)
        var touching = conflictTree ? view.TouchingPlugins(fk) : null;
        return new ReadOutcome(fk, record, source, winner.Value.WinnerPlugin, winner.Value.OverrideDepth, touching, null);
    }

    /// <summary>resolve_names (P7): annotate every field whose <see cref="FieldValue.Token"/> is a form reference (a
    /// token that round-trips to a FormKey) with its target's load-order identity, hung on <see cref="FieldValue.Link"/>
    /// — DISPLAY-ONLY, never touching the round-trip Token. Type-agnostic: a token that parses as a FormKey IS a form
    /// reference (FormLinks and condition-target FLOIs both emit a bare FormKey token; scalars never do), so this
    /// inherits coverage from the read surface with no per-type wiring. Resolution rides the SAME captured view +
    /// open session the read used, memoised so a keyword that recurs across a whole record (or batch) resolves once.
    /// An unresolvable target is a NAMED unresolved <see cref="ResolvedRef"/> (Resolved=false), never dropped (Q3) —
    /// bar the engine-implicit forms, which <see cref="ResolveRefOne"/> answers with their hardcoded identity (#230).
    /// Copy-on-first-write: a record with no form-reference leaves returns the SAME instance.</summary>
    static RecordFields AnnotateLinks(RecordFields rf, LoadOrderResolver.IndexView view,
                                      LoadOrderResolver.OverlaySession session, Dictionary<FormKey, ResolvedRef> memo)
    {
        List<FieldValue>? rebuilt = null;
        for (int i = 0; i < rf.Fields.Count; i++)
        {
            var f = rf.Fields[i];
            if (f.HasValue && f.Token is { } tok && FormKey.TryFactory(tok, out var fk) && !fk.IsNull)
            {
                rebuilt ??= new List<FieldValue>(rf.Fields);
                rebuilt[i] = f with { Link = ResolveRefOne(view, session, fk, memo) };
            }
        }
        return rebuilt is null ? rf : rf with { Fields = rebuilt };
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

    /// <summary>The best-effort display Name of a record body — reflection-generic via Mutagen's <c>INamedGetter</c>
    /// aspect, so it inherits coverage from the model (no per-record-type wiring): every named record answers, a
    /// type with no Name (KYWD, most references) returns null. A translated Name resolves to its default-language
    /// string. Used by housecarl_resolve (P3) and the resolve_names annotation (P7).</summary>
    static string? ReadDisplayName(IMajorRecordGetter body) =>
        body is INamedGetter named && !string.IsNullOrEmpty(named.Name) ? named.Name : null;

    /// <summary>Resolve ONE FormKey to its load-order identity (type/editorid/name/winner) off a captured view + open
    /// session, memoised so a target that recurs across a batch (the SAME keyword on 500 items) resolves once. A
    /// FormKey not in the order is a NAMED unresolved result (Resolved=false), never dropped or guessed (Q3) — except
    /// the engine-implicit forms (PlayerRef 000014 / Player 000007), which the index can't resolve but are real: those
    /// answer with their hardcoded identity and winner "&lt;engine&gt;", the same precise <see cref="EngineImplicit"/>
    /// exemption check_errors and the dialogue lints apply (#230). Shared by housecarl_resolve (P3) and the
    /// resolve_names field annotation (P7).</summary>
    static ResolvedRef ResolveRefOne(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     FormKey fk, Dictionary<FormKey, ResolvedRef> memo)
    {
        if (memo.TryGetValue(fk, out var hit)) return hit;
        ResolvedRef result;
        var w = view.ResolveWinner(fk);
        if (w is null)
            result = EngineImplicit.TryDescribe(fk, out var eiType, out var eiEditorId)
                ? new ResolvedRef(fk.ToString(), Resolved: true, Type: eiType, EditorId: eiEditorId, Winner: "<engine>")   // engine-implicit: hardcoded, real, defined by no plugin
                : new ResolvedRef(fk.ToString(), Resolved: false);   // valid FormKey, but no active plugin defines it (a dangling target)
        else
        {
            var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
            result = body is null
                ? new ResolvedRef(fk.ToString(), Resolved: false, Winner: w.Value.WinnerPlugin)   // winner named but the fetch didn't yield it
                : new ResolvedRef(fk.ToString(), Resolved: true, Type: RecordNaming.StripOverlay(body.GetType().Name),
                                  EditorId: body.EditorID, Name: ReadDisplayName(body), Winner: w.Value.WinnerPlugin);
        }
        memo[fk] = result;
        return result;
    }

    /// <summary>Bulk name resolution (housecarl_resolve — P3): turn a list of FormIDs into their load-order identity
    /// (type/editorid/name/winner) in ONE call over ONE captured view, memoised across the batch. A bad/absent FormID
    /// yields a per-item result carrying its reason (Error for a malformed string, Resolved=false for a valid-but-absent
    /// FormKey) without failing the whole batch (Q3, the batch_record_detail convention). Deliberately minimal — no
    /// fields/depth/conflict_tree; anything richer is housecarl_batch_record_detail's job.</summary>
    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids)
    {
        var resolver = Resolver;
        var view = resolver.Capture();                  // ONE build for the whole batch (HCBR-2026-06-11-02)
        using var session = resolver.OpenSession();
        var memo = new Dictionary<FormKey, ResolvedRef>();
        var results = new List<ResolvedRef>(formids.Count);
        foreach (var raw in formids)
        {
            var t = raw?.Trim() ?? "";
            FormKey fk;
            try { fk = FormKey.Factory(t); }
            catch (Exception ex) { results.Add(new ResolvedRef(t, Resolved: false, Error: $"bad FormID: {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); continue; }
            results.Add(ResolveRefOne(view, session, fk, memo));
        }
        return results;
    }

    // ---- pairwise record diff (housecarl_diff_record — P8c) --------------------------------------------

    /// <summary>P8c — field-level diff between TWO named plugins' versions of ONE record (housecarl_diff_record). Each
    /// pole resolves ACTIVE-order (<see cref="LoadOrderResolver.IndexView.GetRecord"/>) OR OFF-ORDER (a plugin file on
    /// disk not in the load order — the report's case diffs a DISABLED old patch against the mod that supersedes it, via
    /// the shared <see cref="LocatePluginFileOnDisk"/>). Both sides are deep-read (<see cref="ConflictDiffDepth"/>) with
    /// the SAME fields= so line sets correspond, then compared by <see cref="FieldsDiff.Compare"/> — the SAME
    /// order-insensitive, truncation-honest engine the conflict tree uses, with plugin_b as the reference label. A bad
    /// FormID, an unresolvable pole, or a plugin that doesn't define the record is a NAMED refusal (Q3).</summary>
    public DiffRecordOutcome DiffRecord(string formid, string pluginA, string pluginB, IReadOnlyList<string>? fields,
                                        string? modA = null, string? modB = null)
    {
        var fidLabel = formid?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(pluginA) || string.IsNullOrWhiteSpace(pluginB))
            return DiffRecordOutcome.Fail(fidLabel, "diff_record needs BOTH plugin_a and plugin_b — the two plugins whose versions of the record to compare.");
        FormKey fk;
        try { fk = FormKey.Factory(fidLabel); }
        catch (Exception ex) { return DiffRecordOutcome.Fail(fidLabel, $"bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); }

        var resolver = Resolver;
        var view = resolver.Capture();
        using var session = resolver.OpenSession();

        var a = ResolveDiffPole(view, session, fk, pluginA.Trim(), modA, fields);
        if (a.Error is not null) return DiffRecordOutcome.Fail(fidLabel, $"plugin_a: {a.Error}");
        var b = ResolveDiffPole(view, session, fk, pluginB.Trim(), modB, fields);
        if (b.Error is not null) return DiffRecordOutcome.Fail(fidLabel, $"plugin_b: {b.Error}");

        // Label the reference side with what the pole RESOLVED to, not the raw argument — a pole addressed by path
        // renders its plugin name in the header, and delta lines quoting the path back would read as a second,
        // different plugin.
        var diff = FieldsDiff.Compare(a.Fields!, b.Fields!, referenceLabel: b.Pole!.Plugin);
        return new DiffRecordOutcome(fidLabel, a.Pole!, b.Pole!, diff, null);
    }

    /// <summary>Resolve ONE diff pole: the named plugin's version of <paramref name="fk"/> + its deep-read fields. An
    /// ACTIVE-order plugin reads through the captured view; a plugin NOT in the order is looked up on disk (the shared
    /// <see cref="LocatePluginFileOnDisk"/> + <see cref="LoadOrderResolver.OpenOverlay"/>, materialised then DISPOSED —
    /// the RecordFields is a value snapshot, so no handle is held). A named error (never a silent miss) if the plugin
    /// isn't found, is excluded, or doesn't define/override the record.</summary>
    (RecordFields? Fields, DiffPole? Pole, string? Error) ResolveDiffPole(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, FormKey fk,
        string plugin, string? mod, IReadOnlyList<string>? fields)
    {
        // A pole may be addressed by PATH. Routing on the name table alone can't recognise the active plugin that
        // way — a path never matches a filename key, so the live, load-order-winning copy fell through to the
        // off-order branch and got stamped OUT-OF-LOAD-ORDER (#269). Resolve a path that IS the file the order
        // loads back to its plugin NAME first; everything else still takes the off-order lane below.
        if (LooksLikePath(plugin) && ActiveNameForPath(view, plugin) is { } activeName) plugin = activeName;

        if (view.ContainsPlugin(plugin))
        {
            if (view.ExcludedPlugins.TryGetValue(plugin, out var why))
                return (null, null, $"'{plugin}' was excluded from this session ({why}) — its records aren't resolvable.");
            var body = view.GetRecord(session, plugin, fk);
            if (body is null)
                return (null, null, $"'{plugin}' is in the load order but does NOT define or override {fk} — it has no version to diff.");
            return (ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                    new DiffPole(plugin, "active order", true, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null);
        }

        // OFF-ORDER: a plugin file on disk that isn't in the active order (the shared locate — cheap, no index build).
        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex) { return (null, null, $"'{plugin}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}"); }
        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, mod);
        if (loc.Error is not null) return (null, null, $"'{plugin}' is not in the load order and {loc.Error}");
        if (loc.Ambiguous is not null) return (null, null, $"'{plugin}' matches several mod folders on disk — pass an exact path to disambiguate.");
        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
        catch (Exception ex) { return (null, null, $"could not open '{loc.Path}' as a Skyrim plugin: {ex.Message}"); }
        try
        {
            IMajorRecordGetter? rec;
            try { rec = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk); }
            catch (Exception ex) { return (null, null, $"file '{plugin}' could not be read ({ex.Message})."); }
            if (rec is null)
                return (null, null, $"file '{plugin}' (OUT-OF-LOAD-ORDER, {loc.Where}) does not define or override {fk} — no version to diff.");
            return (ReadEngine.ReadFields(rec, fields, ConflictDiffDepth),   // materialised here → the overlay can close
                    // "disabled" was the wrong word twice over: a MOD is disabled, a PLUGIN is inactive — and the one
                    // word covered four causes with four different remedies. Name the cause the locate already knows.
                    new DiffPole(plugin, $"OUT-OF-LOAD-ORDER ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})", false,
                                 RecordNaming.StripOverlay(rec.GetType().Name), rec.EditorID), null);
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>If <paramref name="path"/> is the EXACT file the active order loads for its filename, the plugin name
    /// the order knows it by; else null. The full-path compare is the whole point: a backup that shares the filename
    /// is a different file and must keep reading as off-order (that same-name/different-file pair is the ordinary
    /// old-version-vs-live diff). Costs nothing — the index already carries each active plugin's path. Same junction
    /// caveat as the on-disk locate: a path reaching the file through a junction won't string-match, so it keeps the
    /// off-order lane — the pre-fix answer, never a wrong claim in the other direction.</summary>
    static string? ActiveNameForPath(LoadOrderResolver.IndexView view, string path)
    {
        string full;
        try { full = Path.GetFullPath(path.Trim()); } catch { return null; }
        var name = Path.GetFileName(full);
        if (name.Length == 0 || !view.ContainsPlugin(name)) return null;
        // An EXCLUDED plugin is still in the name table (exclusion is a separate set), and the active lane can only
        // refuse it. Reading its file DIRECTLY is the escape hatch for exactly that case — records ahead of the
        // unparseable one still come back — so a path to one must keep taking the off-order lane, not get routed
        // into a refusal it was addressed by path to avoid.
        if (view.ExcludedPlugins.ContainsKey(name)) return null;
        var active = view.PluginPath(name);
        return !string.IsNullOrEmpty(active) && SamePluginFile(active, full) ? name : null;
    }

    /// <summary>One side of a housecarl_diff_record comparison: the plugin named, WHERE its version was found (active
    /// order, or OUT-OF-LOAD-ORDER on disk), whether it's in the active order, and the record identity it carries.</summary>
    public sealed record DiffPole(string Plugin, string Where, bool InOrder, string? RecordType, string? EditorId);

    /// <summary>The outcome of a housecarl_diff_record call. <see cref="Error"/> non-null ⇒ refused (a bad FormID, an
    /// unresolvable pole, or a plugin that doesn't define the record) with no diff. Otherwise <see cref="Diff"/> is the
    /// field-level delta of <see cref="A"/> vs <see cref="B"/> (B the reference side), truncation-honest via Complete.</summary>
    public sealed record DiffRecordOutcome(string Formid, DiffPole? A, DiffPole? B, FieldsDiff.Result? Diff, string? Error)
    {
        public static DiffRecordOutcome Fail(string formid, string error) => new(formid, null, null, null, error);
    }

    // ---- batch (Q4.9) -----------------------------------------------------------------------------------

    /// <summary>Resolve+read many records in one call (housecarl_batch_record_detail). Each formid runs the same
    /// <see cref="ResolveRead"/> path, so a bad/absent formid yields a per-item recoverable error (Q3) without
    /// failing the batch. Returns one <see cref="ReadOutcome"/> per input, in order. <paramref name="plugin"/> —
    /// when set — reads every formid AS THAT NAMED PLUGIN'S version (its override, not the load-order winner), the
    /// batch twin of housecarl_read_record's plugin= (HCBR-2026-07-15): a formid that plugin doesn't touch yields
    /// its own per-item error (ResolveRead's "does not define this record"), never failing the batch.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                                   bool resolveNames = false, string? plugin = null)
    {
        var resolver = Resolver;                // build/refresh ONCE for the batch
        var view = resolver.Capture();          // ONE build for every item — the whole batch is one logical operation (HCBR-2026-06-11-02)
        var linkMemo = resolveNames ? new Dictionary<FormKey, ResolvedRef>() : null;   // P7: one link-resolution cache across the WHOLE batch
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = FormKey.Factory(raw.Trim()); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            outcomes.Add(ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo));
        }
        return outcomes;
    }

    // ---- cross-plugin query (Q4.9) ----------------------------------------------------------------------

    /// <summary>Scan the order for records matching a filter (housecarl_cross_plugin_query). A SINGLE enumeration
    /// pass with the matching record's body in hand (no per-candidate re-fetch): type= streams the WINNER body
    /// (effective truth) via typed group enumeration; plugins= streams each scoped plugin's OWN body (a content
    /// audit); conflicts_only= alone reads the index. Body filters (editorid_contains/references) test the
    /// in-hand body and so require type= or plugins= to bound them. <paramref name="references"/> is a LIST — a
    /// record matches if it references ANY target (OR), and each match records which target(s) it hit (multi-target
    /// un-merge). <paramref name="definedIn"/> keeps only matches whose FormKey ORIGINATES in a scoped plugin
    /// (definitions, not overrides) — requires plugins=, refused loud otherwise. <paramref name="groupBy"/>
    /// ("winner"|"type"|"defined_in") replaces per-match lines with a count table over ALL matches (not capped by
    /// limit=). <paramref name="offset"/> skips the first N post-filter matches before collecting (#223 pagination —
    /// scan order is deterministic for an unchanged load order, so offset=/limit= windows tile without gaps or
    /// overlap; the true total still counts ALL matches). Returns pre-built match summaries (capped at
    /// <paramref name="limit"/>, with the true total), a group table, or a recoverable Q3 error. Holds nothing.</summary>
    public CrossQueryOutcome CrossQuery(string? type, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn = false, string? groupBy = null, int offset = 0, string? whereSource = null)
    {
        var resolver = Resolver;
        var view = resolver.Capture();          // ONE build for the SCAN and every per-match fill it makes (HCBR-2026-06-11-02)
        bool hasPlugins = plugins is { Count: > 0 };
        bool hasType = type is not null;
        bool hasWhere = where is { Count: > 0 };
        bool hasReferences = references is { Count: > 0 };
        bool bodyFilter = hasReferences || !string.IsNullOrEmpty(editoridContains) || hasWhere;

        if (!hasType && !conflictsOnly && !hasPlugins && !bodyFilter)
            return CrossQueryOutcome.Fail("cross_plugin_query needs at least one of: type=, conflicts_only=true, editorid_contains=, references=, where=, or plugins=.");
        if (bodyFilter && !hasType && !hasPlugins)
            return CrossQueryOutcome.Fail("editorid_contains/references/where is a body scan and must be combined with type= or plugins= to bound it (conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). A global reverse-reference index is a future capability.");

        // defined_in= keeps only records DEFINED in the scoped plugins (origin FormKey), the catalogue-scope semantics
        // distinct from plugins='everything this plugin TOUCHES'. It needs a plugins= scope to mean anything — refused
        // loud otherwise, never silently ignored (Q3).
        if (definedIn && !hasPlugins)
            return CrossQueryOutcome.Fail("defined_in=true keeps only records DEFINED in a scoped plugin, so it requires plugins= to name that scope. Add plugins=, or drop defined_in= (a bare scan already reports each match's defining plugin via its FormID suffix).");
        HashSet<ModKey>? scopedModKeys = null;
        if (definedIn)
        {
            scopedModKeys = new();
            foreach (var p in plugins!)
                try { scopedModKeys.Add(ModKey.FromFileName(p.Trim())); }
                catch (Exception ex) { return CrossQueryOutcome.Fail($"defined_in: '{p}' is not a valid plugin filename: {ex.Message}"); }
        }

        // offset= pages the match window (#223). Validated up front (Q3): negative is meaningless, and under
        // group_by= there is no match window to page (the aggregation counts ALL matches, never limit-capped) —
        // silently ignoring it would misrepresent what the caller asked for.
        if (offset < 0)
            return CrossQueryOutcome.Fail($"offset={offset} — offset must be >= 0 (it skips that many matches before returning rows).");
        if (offset > 0 && groupBy is not null)
            return CrossQueryOutcome.Fail("group_by= aggregates ALL matches into a count table (never capped by limit=), so offset= has nothing to page — drop offset=, or drop group_by= for per-match rows.");

        // where_source= (#233) chooses which BODY the body filters (where=/references=/editorid_contains=) decide the
        // match on: 'scoped' (default) = the body the scan streams (the scoped plugin's own under plugins=, else the
        // winner); 'winner' = the live load-order WINNER regardless of scan scope. Validated up front (Q3, like
        // group_by): an unknown value refuses before any scan. It retargets the MATCH only — winner_fields=
        // independently governs DISPLAY, so 'match on the winner, show the scoped origin' stays expressible.
        bool whereWinner = false;
        if (whereSource is not null)
        {
            var ws = whereSource.Trim().ToLowerInvariant();
            if (ws is not ("scoped" or "winner"))
                return CrossQueryOutcome.Fail($"where_source='{whereSource}' is not a known source — use 'scoped' (default; the scanned body) or 'winner' (the live load-order winner).");
            whereWinner = ws == "winner";
        }
        if (whereWinner && !bodyFilter)
            return CrossQueryOutcome.Fail("where_source=winner retargets the body filters (where=/references=/editorid_contains=) onto the live load-order winner, but none of those was given — add a body filter, or drop where_source= (a bare type=/plugins= scope already reports each match's winner).");
        // Under a type=-ONLY scope the scan already streams the WINNER body, so where_source=winner is already
        // satisfied — accept it (refusing a correct request is hostile) but SAY so, never a silent no-op (Q3). Only the
        // scoped-body stream (plugins=) needs the per-match winner re-fetch.
        bool whereWinnerActive = whereWinner && hasPlugins;
        string? whereSourceNote = (whereWinner && !hasPlugins)
            ? "note: where_source=winner is redundant here — a type=-only scan already reads the load-order winner, so the match used the winner regardless."
            : null;

        // group_by= aggregates matches into a count table. Validated up front (an unknown key refuses BEFORE any scan,
        // Q3). group_by=type needs the matched body to name the type, so it requires a body-bearing scope (type= or
        // plugins=); winner/defined_in are derivable from the FormKey alone and work with conflicts_only= too.
        if (groupBy is not null)
        {
            groupBy = groupBy.Trim().ToLowerInvariant();
            if (groupBy is not ("winner" or "type" or "defined_in"))
                return CrossQueryOutcome.Fail($"group_by='{groupBy}' is not a known aggregation key — use 'winner', 'type', or 'defined_in'.");
            if (groupBy == "type" && !hasType && !hasPlugins)
                return CrossQueryOutcome.Fail("group_by=type needs each match's type, which requires a body-bearing scope — add type= or plugins= (winner/defined_in group without a body).");
        }
        var refSet = hasReferences ? new HashSet<FormKey>(references!) : null;
        bool multiTarget = references is { Count: >= 2 };

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
        List<string?>? matched = multiTarget ? new() : null;                  // parallel to keys: which target(s) each hit referenced (multi-target references= un-merge); null when 0/1 target
        List<RecordSummary>? prefilled = (hasType || hasPlugins) ? new() : null;   // parallel to keys; null = renderer fills lazily
        // OrdinalIgnoreCase so case-variant spellings of the SAME plugin — a master listed as `ccBGSSSE025-AdvDSGS.esm`
        // in one plugin's masters and `ccbgssse025-advdsgs.esm` in another's — merge into ONE group instead of splitting
        // the count (#248). Plugin filenames are case-insensitive identifiers everywhere else in houseCARL (and in the
        // game); first-seen casing becomes the display key. Harmless for group_by=type (record type names never differ
        // only by case), so this one comparer correctly covers all three keys (winner / type / defined_in).
        Dictionary<string, int>? groups = groupBy is not null ? new(StringComparer.OrdinalIgnoreCase) : null;   // group_by= aggregation (bumped per match, over ALL matches — not limit-capped)
        int total = 0;
        int unscannable = 0;                                                  // records whose body tests THREW (Mutagen-unparseable content) — excluded + accounted, never silent (Q3)
        var unscannableSamples = new List<string>();

        if (hasType || hasPlugins)                                            // a body-bearing scope: stream + filter in hand
        {
            // RecordsIn / WinnerRecordsOfType are LAZY iterators: ScopeIndices (a plugin not in the order) and
            // EnumerateMajorRecords(throwIfUnknown) throw on ENUMERATION, not on creation — so the try must wrap the
            // foreach, not just the assignment, or the clean Q3 message escapes as a generic framework error.
            var seen = new HashSet<FormKey>();
            // where_source=winner (#233): the match decides on the live WINNER body, fetched via this ONE session
            // (Option B — one session for every per-match winner fetch, not one per record). Opened only when the
            // scan streams SCOPED bodies (plugins=); a type=-only scan already yields the winner. Disposed with the scan.
            LoadOrderResolver.OverlaySession? winnerSession = whereWinnerActive ? resolver.OpenSession() : null;
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
                    // defined_in=: keep only records whose ORIGIN FormKey is a scoped plugin (a DEFINITION here, not
                    // an override this plugin merely touches). A FormKey test — no body needed, so it runs before the try.
                    if (definedIn && !scopedModKeys!.Contains(fk.ModKey)) continue;
                    // where_source=winner de-dups UP FRONT: the winner verdict is FK-intrinsic, so any scoped copy of
                    // a FK gives the same answer — resolve the winner ONCE, and the FIRST scoped copy in stream order
                    // supplies the display source for winner_fields=false. (The scoped path keeps its de-dup AFTER the
                    // filters, so its source is the first scoped plugin whose OWN body passed — a different rule, below.)
                    if (whereWinnerActive && !seen.Add(fk)) continue;
                    // PER-RECORD FAULT ISOLATION (HCBR-2026-06-09-03): the body tests lazily parse subrecord
                    // content (references= walks Effects etc. via Mutagen's EnumerateFormLinks), so ONE record
                    // Mutagen can't parse used to abort the WHOLE call as an opaque transport error — the
                    // scan-level twin of the PKCU index-build fix. Such a record is excluded and ACCOUNTED in
                    // the response (never a silent skip, never a guessed match — Q3). The winner re-fetch below
                    // is inside the try too, so a winner Mutagen can't parse is accounted the same way.
                    try
                    {
                        // The body the FILTERS decide on: the live winner (where_source=winner) or the streamed body.
                        IMajorRecordGetter filterBody = body;
                        if (whereWinnerActive)
                        {
                            var w = view.ResolveWinner(fk);
                            if (w is null) continue;                              // FK came from the order — winner must exist (defensive)
                            var wb = view.GetRecord(winnerSession!, w.Value.WinnerPlugin, fk);
                            if (wb is null)
                            {
                                unscannable++;
                                if (unscannableSamples.Count < 3)
                                    unscannableSamples.Add($"{fk} — winner '{w.Value.WinnerPlugin}' did not yield the record on winner-source re-fetch");
                                continue;
                            }
                            filterBody = wb;
                        }
                        // DELETED records carry no body to scan (#276): a Deleted major record's content is
                        // empty by engine rule, so the CONTENT filters cannot match it — references= links to
                        // nothing, where= has no field to test. So exclude it as a clean non-match here, before
                        // the scan touches its body. This is also what stops the reported crash: engine-authored
                        // deleted records (the wild repro was deleted PACKs) can leave a content-free-but-not-clean
                        // body that NullRefs when EnumerateFormLinks (references=) / the where= predicate lazily
                        // parse it — which then lands in the unscannable bucket below as an untyped skip, reading
                        // as a parser hole, so a genuine match hiding in a "skipped" record looks possible when it
                        // isn't (Q3). editorid_contains= stays live: EditorID reads from the record's early EDID
                        // subrecord, before the deep body parse that can throw — safe on a deleted record.
                        if (filterBody.IsDeleted && (refSet is not null || predicate is not null)) continue;
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (filterBody.EditorID is null || filterBody.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        // references= (LIST, OR semantics): a record matches if it links to ANY target. One
                        // EnumerateFormLinks pass collects the intersection so a multi-target lookup can be un-merged
                        // (matches=<which target(s)>) — the same single pass, membership-in-a-set instead of ==.
                        List<FormKey>? hitTargets = null;
                        if (refSet is not null)
                        {
                            if (filterBody is not IFormLinkContainerGetter flc) continue;
                            var hitSet = new HashSet<FormKey>();
                            foreach (var l in flc.EnumerateFormLinks()) if (refSet.Contains(l.FormKey)) hitSet.Add(l.FormKey);
                            if (hitSet.Count == 0) continue;
                            if (multiTarget && groups is null) hitTargets = references!.Where(hitSet.Contains).Distinct().ToList();   // in input order; only the match-line path consumes it (group_by ignores it)
                        }
                        if (predicate is not null && !predicate.Matches(filterBody))    // value filter — same in-hand body (winner under where_source=winner), no extra fetch
                        {
                            if (predicate.FatalError is not null) break;          // numeric op vs non-numeric field — abort + surface (Q3)
                            continue;
                        }
                        // De-dup (a FK can recur across scoped plugins). Under the SCOPED path this runs AFTER the
                        // filters, so under plugins=[A,B] the source recorded for a shared FK is the FIRST scoped plugin
                        // (in plugins= array order) whose body PASSED the filters. Under where_source=winner the FK was
                        // already de-duped up front (the winner verdict is FK-intrinsic), so this is a no-op there.
                        if (!whereWinnerActive && !seen.Add(fk)) continue;
                        total++;
                        if (groups is not null)                                   // group_by=: aggregate over ALL matches, no keys/prefill, no limit cap
                        {
                            var gk = groupBy == "type" ? RecordNaming.StripOverlay(filterBody.GetType().Name)
                                   : groupBy == "defined_in" ? fk.ModKey.FileName.ToString()
                                   : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";  // "winner"
                            groups[gk] = groups.GetValueOrDefault(gk) + 1;
                        }
                        else if (total > offset && keys.Count < limit)            // in-hand body → fill the summary for free (offset= skips the first N matches — total already counts this one)
                        {
                            keys.Add(fk);
                            sources.Add(source);                                  // scoped plugin (the winner_fields=false display body); null ⇒ winner. where_source=winner keeps the scoped source so 'match on winner, show origin' works.
                            matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);   // parallel to keys (multi-target only)
                            // winner= off the SAME view the scan runs on — a rebuild landing mid-scan can no longer
                            // make a row's winner reflect a newer build than the depth beside it (HCBR-2026-06-11-02).
                            // Summary type/editorid come from filterBody (the body that MATCHED — the winner under where_source=winner).
                            prefilled!.Add(new RecordSummary(fk, RecordNaming.StripOverlay(filterBody.GetType().Name), filterBody.EditorID,
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
            finally { winnerSession?.Dispose(); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError); // typed predicate error — fail fast, named (Q3)
        }
        else                                                                  // conflicts_only alone — index keys only; NO body fetch
        {
            // Summaries here would each need a winner-body fetch; leaving them to the renderer (which stops at
            // max_chars) means a big limit with a small max_chars doesn't fetch bodies it will never show.
            // group_by= here can only be winner/defined_in (type was refused up front — no body to name the type).
            foreach (var fk in view.ConflictKeys())
            {
                total++;
                if (groups is not null)
                {
                    // group_by=winner here does an index-level ResolveWinner PER conflict key — a resolve, not a body
                    // parse, and unavoidable for the aggregate (the non-group path defers winner= to the renderer, which
                    // only fetches the capped rows). Intentional under accuracy-over-perf; don't "optimize" it away.
                    var gk = groupBy == "defined_in" ? fk.ModKey.FileName.ToString() : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";
                    groups[gk] = groups.GetValueOrDefault(gk) + 1;
                }
                else if (total > offset && keys.Count < limit) { keys.Add(fk); sources.Add(null); }   // no scoped plugin → display the winner; offset= skips the first N
            }
        }
        // Unscannable accounting (Q3): name the count, the first few offenders with the reason, and what a caller can
        // still do — these records are invisible to the body filters, not "0 matches" silence. Two causes flow here:
        // Mutagen could not parse a body, OR (under where_source=winner) a winner body the index named did not
        // re-resolve on fetch — the note must not mislabel the second as a parse failure. "instance(s)" and the
        // per-copy skip because under plugins= a FormKey is tested once per scoped plugin: a failing copy is skipped
        // where it occurs while another plugin's copy of the same FK can still match (PR #27 review).
        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned and were skipped where the failure occurred "
              + "(Mutagen could not parse their content, or — under where_source=winner — a winner body the index named did not re-resolve on fetch; another plugin's copy of the same FormKey can still match): "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "")
              + ". Inspect one with read_record (per-field fault isolation applies).";
        // group_by= aggregation isn't limit-capped (cheap), so Capped is a match-line concern only.
        var groupRows = groups?.Select(kv => new GroupCount(kv.Key, kv.Value))
                              .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        // Capped = matches exist BEYOND the returned window (total > offset + rows) — the matches offset= skipped
        // were asked to be skipped, so they don't make a full window read as capped.
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote,
                                     matched, groupRows, groupBy, definedIn ? string.Join(", ", plugins!) : null, offset,
                                     whereWinner, whereSourceNote);
    }

    // ---- effect-chain resolver (housecarl_effect_chain — gap 2026-06-08) --------------------------------

    /// <summary>Resolve which SPEL/ENCH/ALCH/SCRL/INGR apply a MagicEffect, each with the magnitude/area/duration from
    /// the MATCHING effect entry (housecarl_effect_chain). Thin wiring over the core: resolve the optional type-narrow
    /// (each must be one of the five effect-bearing records — a non-member is refused loud, never a silent empty scan),
    /// then drive <see cref="EffectChain.Resolve"/> (Q3 typed-match gate → winner-body scan). All the logic + the Q3
    /// teeth live in core so the self-contained guard drives this same path on synthetic plugins.</summary>
    public EffectChainResult ResolveEffectChain(FormKey mgef, IReadOnlyList<string>? typesNarrow, int limit)
    {
        IReadOnlyList<Type> scope;
        if (typesNarrow is { Count: > 0 })
        {
            var picked = new List<Type>();
            foreach (var ts in typesNarrow)
            {
                IReadOnlyList<Type> resolved;
                try { resolved = ResolveTypeFilter(ts.Trim()); }              // unknown type → named Q3 error (same as cross_plugin_query)
                catch (ArgumentException ex) { return EffectChainResult.Fail(ex.Message); }
                foreach (var t in resolved)
                {
                    if (!EffectChain.CarrierTypes.Contains(t))
                        return EffectChainResult.Fail(
                            $"type '{ts}' is not effect-bearing — effect_chain scans only Spell/ObjectEffect/Ingestible/Scroll/Ingredient " +
                            "(SPEL/ENCH/ALCH/SCRL/INGR), the records that carry an Effects list. Drop it or pass one of those.");
                    if (!picked.Contains(t)) picked.Add(t);
                }
            }
            scope = picked;
        }
        else scope = EffectChain.CarrierTypes;

        return EffectChain.Resolve(Resolver, mgef, scope, limit);
    }

    // ---- integrity sweep (housecarl_check_errors — audit A1) --------------------------------------------

    /// <summary>Sweep the active order (or the given <paramref name="plugins"/> scope) for record integrity errors —
    /// dangling FormLinks, missing masters, and parse failures (housecarl_check_errors). Thin wiring over the
    /// core <see cref="ErrorCheck.Run"/>, which holds all the scan logic + Q3 teeth so the self-contained guard drives
    /// this same path over synthetic plugins. Read-only; composes existing primitives, no new dependency.
    /// <para>A scope name NOT in the active order is resolved on disk by the shared plugin-locate contract
    /// (<see cref="LocatePluginFileOnDisk"/> — enabled, disabled, AND unlisted mod folders) and swept OFF-ORDER: its
    /// own overlay, links resolved against the active order + the file's own records. This is the pre-enable verify
    /// lane (HCBR-2026-07-14-02 gap 3) — the pre-ship dangling-ref sweep of a patch houseCARL just wrote, BEFORE the
    /// MO2 refresh puts it in plugins.txt. A name found nowhere, or in several folders, still fails loud (Q3).</para></summary>
    public ErrorCheckResult CheckErrors(IReadOnlyList<string>? plugins, int limit)
    {
        if (plugins is { Count: > 0 })
        {
            var view = Resolver.Capture();
            var active = new List<string>();
            var offOrder = new List<(string Name, string Path)>();
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            Mo2Composition? comp = null;
            foreach (var name in plugins)
            {
                var n = name?.Trim() ?? "";
                if (n.Length == 0) return ErrorCheckResult.Fail("a blank plugin name in the scope — pass plugin filenames (e.g. 'CoolMod.esp').");
                if (view.ContainsPlugin(n)) { active.Add(n); continue; }
                comp ??= Mo2LoadOrder.ReadComposition(profileDir);
                var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, n, null);
                if (loc.Error is not null)
                    return ErrorCheckResult.Fail($"plugin not in the load order: {n} — and no on-disk copy was found either ({loc.Error})");
                if (loc.Ambiguous is not null)
                    return ErrorCheckResult.Fail(
                        $"plugin '{n}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                        $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to sweep. " +
                        "Enable the one you mean in MO2, or remove the duplicates.");
                offOrder.Add((n, loc.Path!));
            }
            return ErrorCheck.Run(Resolver, active, limit, offOrder.Count > 0 ? offOrder : null);
        }
        return ErrorCheck.Run(Resolver, plugins, limit);
    }

    // ---- script-property sweep (housecarl_validate_scripts) --------------------------------------------

    /// <summary>Sweep the active order (or the given <paramref name="plugins"/> scope) for VMAD script properties that
    /// are declared in the attached script's .pex (or an ancestor it extends) but left UNBOUND on the record — a silent
    /// <c>None</c> (housecarl_validate_scripts). Thin wiring over the core <see cref="ScriptPropertyCheck.Run"/>, which
    /// holds all the cross-check logic + Q3 teeth so the self-contained guard drives this same path over synthetic
    /// records + a planted .pex. Passes the live <see cref="Assets"/> resolver (the same one the dialogue validator and
    /// facegen use) so a script's .pex is found loose OR BSA-packed. Read-only; composes existing primitives.</summary>
    public ScriptCheckResult ValidateScripts(IReadOnlyList<string>? plugins, int limit)
        => ScriptPropertyCheck.Run(Resolver, Assets, plugins, limit);

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
    public WritePatchBuilder.PatchOutcome ApplyEdits(IReadOnlyList<BulkOp> ops, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false)
    {
        if (ops.Count == 0)
            return WritePatchBuilder.PatchOutcome.Fail("no operations supplied.");

        // In-place is the explicit, named-file opt-in (the SECOND write lane — edit an existing plugin, incl. one
        // houseCARL didn't author, instead of writing a new patch). Validate the contract up front (Q3): it REQUIRES a
        // target=, and it is mutually exclusive with into= (which EXTENDS a houseCARL patch — a different lane). target=
        // without in_place is a no-op the caller likely didn't mean — name it rather than silently ignore it.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to edit in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.PatchOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place edits an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.PatchOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to edit in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

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

            if (inPlace)
                return ApplyEditsInPlace(resolver, rulebook, edits, target!.Trim(), acknowledge, dryRun);

            // #225: a dry run resolves the would-be output path WITHOUT creating the mod folder (create:false) — the
            // one disk side effect the pre-serialize pipeline otherwise has. The fresh-lane name is a preview: the
            // real write re-picks a free stem, so a concurrent write can shift the auto-suffix.
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            // P8b: pre-resolve any CopyFrom source that is OFF-ORDER (from_plugin on disk but NOT in the active order —
            // the "copy from the disabled OLD patch" case). Active-order sources are resolved INSIDE Apply via its own
            // captured view (sharing the winner's build); only off-order files need the MO2 on-disk locate here, and
            // their overlays must stay OPEN through the serialize (CopyField deep-copies through them) — disposed after.
            Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null;
            List<IDisposable>? offOrderOverlays = null;
            var cfError = ResolveOffOrderCopySources(resolver, edits, ref copyFromSources, ref offOrderOverlays);
            if (cfError is not null)
            {
                if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose();
                if (created) RemoveFolderCreatedThisCall(outPath);   // a refused write leaves no orphan folder
                return WritePatchBuilder.PatchOutcome.Fail(cfError);
            }
            try
            {
                var outcome = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend, fullReadback, copyFromSources, dryRun);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused write leaves no orphan
                return outcome;
            }
            finally { if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose(); }
        }
    }

    /// <summary>P8b — locate every OFF-ORDER CopyFrom source (from_plugin present on disk but NOT in the active order)
    /// and fetch its version of the target record, holding each overlay OPEN (returned in <paramref name="overlays"/> for
    /// the caller to dispose AFTER the patch serialize — CopyField deep-copies through them). Active-order sources are
    /// left for <see cref="WritePatchBuilder.Apply"/> to resolve via its shared view (so they read the winner's build).
    /// Returns a Q3 refusal string if any off-order source can't be located/opened/read or doesn't define the record
    /// (all-or-nothing, before any write); null on success. Uses the SAME on-disk locate as read_plugin_file / the
    /// copy-npc-appearance donor lane, so the tools can never disagree on which file a filename names.</summary>
    string? ResolveOffOrderCopySources(LoadOrderResolver resolver, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
        ref Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? sources, ref List<IDisposable>? overlays)
    {
        if (!edits.Any(e => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))) return null;   // no CopyFrom → no source work
        var view = resolver.Capture();
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";
        Mo2Composition? comp = null;
        var problems = new List<string>();
        foreach (var e in edits)
        {
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(e.FromPlugin)) continue;
            if (view.ContainsPlugin(e.FromPlugin)) continue;   // active — Apply resolves it (shared build)
            if (comp is null)
            {
                try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
                catch (Exception ex) { return $"CopyFrom off-order source locate failed to derive the MO2 roots: {ex.Message}"; }
                comp = Mo2LoadOrder.ReadComposition(profileDir);
            }
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, e.FromPlugin!, null);
            if (loc.Error is not null) { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' is not in the load order and {loc.Error}"); continue; }
            if (loc.Ambiguous is not null) { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' matches several mod folders on disk — pass an exact path to disambiguate."); continue; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex) { problems.Add($"{e.Target}: CopyFrom source file '{e.FromPlugin}' could not be opened as a Skyrim plugin ({ex.Message})."); continue; }
            IMajorRecordGetter? body;
            try { body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == e.Target); }
            catch (Exception ex) { (ov as IDisposable)?.Dispose(); problems.Add($"{e.Target}: CopyFrom source file '{e.FromPlugin}' could not be read ({ex.Message})."); continue; }
            if (body is null) { (ov as IDisposable)?.Dispose(); problems.Add($"{e.Target}: CopyFrom source file '{e.FromPlugin}' does not define or override this record — there is no version of it there to copy."); continue; }
            (overlays ??= new()).Add((IDisposable)ov);
            (sources ??= new())[e] = body;   // distinct Path-array refs make each PatchEdit a distinct key (value equality); indexer is collision-safe regardless
        }
        return problems.Count > 0
            ? $"refused — {problems.Count} CopyFrom source problem(s); NO patch written:\n  - " + string.Join("\n  - ", problems)
            : null;
    }

    /// <summary>The in-place branch of <see cref="ApplyEdits"/> (in-place write lane, Wave 1) — runs under _writeGate.
    /// (1) resolves <paramref name="target"/> to its REAL on-disk path via the load order (NOT the houseCARL-owned
    /// folder model — the foreign-plugin resolver, the sibling of <see cref="ResolveOwnedPatchFolder"/> with the
    /// ownership gate DROPPED); (2) enforces the server-side, PERSISTENT first-touch CONSENT handshake (keyed off the
    /// resolved path, stored in <see cref="UserConfigStore"/>); (3) checks the writable parent; (4) drives
    /// <see cref="WritePatchBuilder.ApplyInPlace"/> with the touched-record verify forced ON; (5) on success stamps the
    /// distinct <c>editedInPlace=</c> marker (NEVER <c>generated=true</c> — the user mod must keep failing
    /// <see cref="IsHouseCarlOwned"/> so a later into= can't blind-overwrite it). <paramref name="acknowledge"/> waives
    /// the CONSENT axis ONLY — the verify is a corruption-axis fact no acknowledgement overrides.</summary>
    WritePatchBuilder.PatchOutcome ApplyEditsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
        string target, bool acknowledge, bool dryRun = false)
    {
        // (1) Resolve target -> real on-disk path via the load order (by plugin FILENAME — unique in an order). Refuse
        //     loud if it isn't a real active plugin (closes the coincidental-folder collision the into= lane can hit).
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place edits the file the game actually loads. Nothing was written.");

        // (2) CONSENT axis — the persistent, server-enforced first-touch handshake, keyed off the resolved path. NOT a
        //     sticky mode: each in-place write still names its own target=, so this only stops re-explaining the
        //     trade-off; it never makes an ambiguous request route to in-place.
        //     #225: a DRY RUN bypasses the handshake and NEVER persists an acknowledgement — consent gates touching
        //     your original, and a dry run touches nothing; the pending consent is surfaced as a note instead, so the
        //     report still says the REAL write will prompt.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        string? ackNote = null;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the one-time confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                return WritePatchBuilder.PatchOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath));
            if (!already && acknowledge)
            {
                var (ok, err) = _store.RecordInPlaceAcknowledged(targetPath);
                if (!ok) ackNote = $"the in-place acknowledgement could not be saved ({err}) — the edit proceeded, but a future session will re-prompt for this plugin.";
            }
        }

        // (3) Writable, same-volume parent pre-flight — refuse rather than degrade (the swap stages a sibling temp in
        //     this dir; AtomicFile.Commit already refuses cross-volume loud, this catches a read-only/locked parent up
        //     front with a clear message before any work). Kept in the dry run too: an unwritable parent is exactly
        //     what the real write would refuse on, and predicting that refusal is the dry run's job.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.PatchOutcome.Fail(why);

        // (4) The write — touched-record verify forced ON (the model-C substitute for the dropped whole-plugin floor).
        var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, targetPath, targetName, fullReadback: true, dryRun);

        // (5-dry) #225: a successful dry run stamps NOTHING (no editedInPlace marker, no .seq note — those describe a
        //     write that happened); only the core's would-grow note + the pending-consent note ride along.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // (5) On success, stamp the distinct audit marker + auto-flag a now-stale .seq (both best-effort; neither miss
        //     fails the done edit, Q3-noted). SEQ flag (Track C): an in-place edit can prune a master and shift the own
        //     records' on-disk FormIDs, staling the plugin's .seq — surfaced as a note, never auto-regen'd (Aaron 2026-07-04).
        if (outcome.Success)
        {
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note (PR #163 review #1) must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        else if (ackNote is not null)
        {
            // The acknowledgement was recorded but the write then failed — keep the (now-honest) note alongside the error.
            return outcome with { Note = ackNote };
        }
        return outcome;
    }

    /// <summary>Resolve an ACTIVE plugin's on-disk path by filename (in-place write lane) — exact match first, then a
    /// lenient retry appending each plugin extension if the caller dropped it (e.g. 'CoolWeapons' → 'CoolWeapons.esp').
    /// <paramref name="resolvedName"/> echoes the canonical filename that matched. Null ⇒ no such active plugin (the
    /// caller refuses loud). The path is the load order's WINNING path for that filename — the file the game loads.</summary>
    static string? ResolveActivePluginPath(LoadOrderResolver.IndexView view, string raw, out string resolvedName)
    {
        resolvedName = raw;
        var direct = view.PluginPath(raw);
        if (direct is not null) return direct;
        if (!PluginExts.Any(e => raw.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            foreach (var ext in PluginExts)
            {
                var cand = raw + ext;
                var p = view.PluginPath(cand);
                if (p is not null) { resolvedName = cand; return p; }
            }
        return null;
    }

    /// <summary>The first-touch in-place CONSENT prompt (server-enforced, shown once per plugin). States the trade-off
    /// plainly: the original file IS the output, there's no houseCARL undo/backup, the whole plugin is re-laid-out like
    /// xEdit/CK do on save with the touched records VERIFIED and Mutagen trusted for the rest, and the default new-patch
    /// lane stays recommended. Waives the CONSENT axis only (re-call with acknowledge=true).</summary>
    static string InPlaceHandshakeText(string pluginName, string path) =>
        $"in-place edit of '{pluginName}' — first-time confirmation (shown once for this plugin):\n" +
        $"  • This writes to your ORIGINAL file ({path}). It will NO LONGER be untouched, and houseCARL keeps NO backup or undo — keep your own.\n" +
        "  • houseCARL re-lays-out the WHOLE plugin the way xEdit/CK do on save (every record re-serialized), VERIFIES the records you edit, and trusts Mutagen for the rest.\n" +
        "  • It still refuses if the file can't be parsed, or carries engine-reserved (sub-0x800) records.\n" +
        "  • The default lane (a NEW patch, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    /// <summary>Writable-parent pre-flight for the in-place swap (§6 layer 3): the staged temp is a sibling of the
    /// target, so prove the parent is writable NOW, loud, rather than degrade to a non-atomic write later. True (with a
    /// named <paramref name="why"/>) ⇒ refuse. Probes by writing + deleting an empty sibling temp. This checks the PARENT
    /// is writable — NOT that the target file isn't EXTERNALLY locked (MO2/xEdit mid-operation); that case isn't
    /// pre-empted here but surfaces LOUD at the <c>File.Replace</c> swap with the original byte-intact (the correct Q3
    /// outcome, not a corruption path), so it needs no separate pre-flight.</summary>
    static bool InPlaceParentUnwritable(string targetPath, out string why)
    {
        why = "";
        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            why = $"in-place refused: the target's parent folder '{dir}' does not exist — nothing written.";
            return true;
        }
        try
        {
            var probe = Path.Combine(dir, ".housecarl-writeprobe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return false;
        }
        catch (Exception ex)
        {
            why = $"in-place refused: the target's folder '{dir}' is not writable ({ex.GetType().Name}: {ex.Message}) — houseCARL " +
                  "won't degrade to a non-atomic write. Make the mod folder writable (or move the plugin somewhere writable) and retry. Nothing written.";
            return true;
        }
    }

    /// <summary>Stamp the distinct <c>[houseCARL] editedInPlace=&lt;ISO&gt;</c> audit line into the target mod's
    /// <c>meta.ini</c> (the MO2-undeployed mod-root file) — a breadcrumb that houseCARL touched this user mod, WITHOUT
    /// ever writing <c>generated=true</c> (so <see cref="IsHouseCarlOwned"/> still reads FALSE and a later into= can't
    /// blind-overwrite it — the safety property). PRESERVES every existing line (merges into / creates the
    /// <c>[houseCARL]</c> section), and only for an MO2 mod folder under ModsDir (never pollutes the game Data dir for a
    /// loose plugin). Best-effort: returns a Q3 note on failure (the edit already succeeded), null on success or N/A.</summary>
    string? MergeEditedInPlaceMarker(string? modFolder)
    {
        try
        {
            if (string.IsNullOrEmpty(modFolder) || !IsUnderModsDir(modFolder)) return null;   // N/A for a non-MO2 target
            var meta = Path.Combine(modFolder, "meta.ini");
            var stamp = $"editedInPlace={DateTime.UtcNow:o}";
            var lines = File.Exists(meta) ? File.ReadAllLines(meta).ToList() : new List<string>();

            int sec = lines.FindIndex(l => l.Trim().Equals(HousecarlOwnerMeta.Section, StringComparison.OrdinalIgnoreCase));
            if (sec < 0)
            {
                if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
                lines.Add(HousecarlOwnerMeta.Section);
                lines.Add(stamp);
            }
            else
            {
                int edited = -1;
                for (int i = sec + 1; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith('[') && t.EndsWith(']')) break;                          // next section — stop
                    if (t.Replace(" ", "").StartsWith("editedInPlace=", StringComparison.OrdinalIgnoreCase)) { edited = i; break; }
                }
                if (edited >= 0) lines[edited] = stamp; else lines.Insert(sec + 1, stamp);    // update-or-insert within the section
            }
            File.WriteAllText(meta, string.Join("\r\n", lines) + "\r\n");
            return null;
        }
        catch (Exception ex)
        {
            return $"the editedInPlace audit marker could not be written to the target's meta.ini ({ex.GetType().Name}) — the edit itself succeeded.";
        }
    }

    /// <summary>True iff <paramref name="folder"/> is ModsDir itself or a folder directly/indirectly under it — the gate
    /// that keeps the editedInPlace marker out of the game Data dir for a loose (non-MO2-managed) in-place target.</summary>
    bool IsUnderModsDir(string folder)
    {
        if (string.IsNullOrEmpty(_modsDir)) return false;
        try
        {
            var full = Path.GetFullPath(folder);
            var mods = Path.GetFullPath(_modsDir);
            return full.Equals(mods, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Join any number of optional Q3 notes into one (space-separated), skipping the null/blank ones; null when
    /// none are present. Variadic so a lane can fold several best-effort side-effect notes (ack + audit marker + the SEQ
    /// staleness auto-flag) into the single <c>Note</c> the outcome carries.</summary>
    static string? JoinNotes(params string?[] notes)
    {
        var present = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    /// <summary>The in-place SEQ auto-flag (Track C / Heisen §3 gap-4, Aaron 2026-07-04: FLAG, never auto-regen). After an
    /// in-place write that re-serialized <paramref name="targetPath"/>, a master-prune may have shifted every own record's
    /// on-disk FormID and staled the plugin's <c>.seq</c> — its Start-Game-Enabled quests would then silently never start
    /// on a fresh save. Resolve the plugin's <c>.seq</c> through the SAME captured VFS view the compact SEQ gate uses
    /// (loose roots + active BSAs — not a bare folder check, which would miss a <c>write_seq</c>-filed or BSA-packed .seq),
    /// and if a LOOSE <c>.seq</c> exists but no longer lists one or more SGE quests at their CURRENT on-disk FormIDs,
    /// return a WARNING naming them + the fix (<c>housecarl_write_seq</c>). Null when there's nothing to flag (no .seq, a
    /// BSA-only .seq whose bytes we can't check here, or every SGE quest still covered — an ordinary in-place edit that
    /// changed no masters leaves the FormIDs put, so a previously-valid .seq stays covered and this stays quiet). BEST-
    /// EFFORT (Q3): any failure yields a soft advisory, never a throw — the write already succeeded, and a freshness check
    /// that couldn't run must not fail the done edit, but it says so rather than going silent.</summary>
    string? SeqStaleInPlaceNote(string targetPath, string targetName)
    {
        try
        {
            AssetResolver assetResolver;
            lock (_gate) { assetResolver = Assets; }                          // reentrant under the held _writeGate (the PlaceAssets idiom)
            var av = assetResolver.Capture();
            var seqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(targetPath)}.seq";
            var seqSource = av.ResolveForPlacement(seqRel).Sources.FirstOrDefault();
            if (seqSource?.LooseFilePath is not { } seqPath) return null;      // no .seq, or a BSA-only one (bytes uncheckable here) → nothing to flag
            var uncovered = SeqFile.UncoveredSgeQuests(targetPath, File.ReadAllBytes(seqPath));
            if (uncovered.Count == 0) return null;                            // the .seq still lists every SGE quest → not staled
            var names = string.Join(", ", uncovered.Select(q => q.EditorId ?? q.FormKey.ToString()));
            bool one = uncovered.Count == 1;
            return $"the .seq for '{targetName}' no longer lists {(one ? "its start-game-enabled quest" : $"{uncovered.Count} of its start-game-enabled quests")} "
                 + $"at {(one ? "its" : "their")} current on-disk FormID(s) ({names}), so {(one ? "it" : "they")} would silently never start on a fresh save "
                 + "(a master prune in an in-place write shifts these FormIDs; the .seq may also have been stale before this edit). Regenerate it with housecarl_write_seq.";
        }
        catch (Exception ex)
        {
            return $"could not check whether '{targetName}'s .seq is still current after this edit ({ex.GetType().Name}) — "
                 + "if it has start-game-enabled quests, run housecarl_validate_dialogue on the quest to confirm the .seq still lists them.";
        }
    }

    /// <summary>Remove WHOLE records a houseCARL patch carries (housecarl_remove_record) — literal drop-from-plugin, the
    /// companion to <see cref="ApplyEdits"/>. In the DEFAULT lane <paramref name="patch"/> is REQUIRED and names an existing
    /// houseCARL-owned patch (resolved + ownership-gated via the same <c>into=</c> path as an extend — refuses a folder
    /// houseCARL didn't create, Q3); removal only makes sense against a patch that already carries the record. In the
    /// IN-PLACE lane (Wave 2: <paramref name="target"/> + <paramref name="inPlace"/>) it drops the record from an EXISTING
    /// plugin in place (incl. one houseCARL didn't author) instead — see <see cref="RemoveRecordsInPlace"/>. Parses every
    /// formid (all-or-nothing on a malformed one), then drives <see cref="WritePatchBuilder.RemoveRecords"/> (present-check
    /// → mod.Remove → re-serialize, with clean-masters riding along). The default lane never touches originals (only the
    /// patch folder is written).</summary>
    public WritePatchBuilder.RemovalOutcome RemoveRecords(IReadOnlyList<string> formids, string? patch,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.RemovalOutcome.Fail("no formids supplied — pass the FormID(s) of the record(s) to remove.");

        // In-place is the explicit, named-file opt-in (the SECOND remove lane — drop a record from an EXISTING plugin, incl.
        // one houseCARL didn't author, instead of from a houseCARL patch). Validate the contract up front (Q3): it REQUIRES
        // a target=, and it is mutually exclusive with patch= (the houseCARL-owned lane). target= without in_place is a
        // no-op the caller likely didn't mean — name it rather than silently ignore it. (Mirrors ApplyEdits' contract.)
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to remove the record from in place. (Omit in_place to drop the record from a houseCARL patch instead — the default.)");
        if (inPlace && !string.IsNullOrWhiteSpace(patch))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "in_place=true and patch= are mutually exclusive: patch= drops a record from a houseCARL patch, while in_place removes it from an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to remove from in place). For the default lane omit target=; use patch= to name the houseCARL patch.");
        if (!inPlace && string.IsNullOrWhiteSpace(patch))
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it). To remove from an existing plugin IN PLACE instead, pass target=<plugin filename> + in_place=true.");

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

            if (inPlace)
                return RemoveRecordsInPlace(resolver, keys, target!.Trim(), acknowledge);

            // Resolve + ownership-gate the patch path via the into= (extend) path — must exist + carry the houseCARL marker.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>The in-place branch of <see cref="RemoveRecords"/> (in-place write lane, Wave 2) — runs under _writeGate.
    /// The remove counterpart of <see cref="ApplyEditsInPlace"/>, and it REUSES every Wave-1 in-place seam: the same
    /// foreign-target resolver (<see cref="ResolveActivePluginPath"/>), the same PERSISTENT first-touch CONSENT handshake
    /// (keyed off the resolved path in <see cref="UserConfigStore"/>, SHARED with the edit + create lanes — acknowledging a
    /// plugin once covers all three), the same writable-parent pre-flight, and the same distinct <c>editedInPlace=</c>
    /// marker (NEVER <c>generated=true</c> — the user mod keeps failing <see cref="IsHouseCarlOwned"/> so a later into=
    /// can't blind-overwrite it). It drives <see cref="WritePatchBuilder.RemoveRecordsInPlace"/> (drop the record from the
    /// target's OWN file, with the absence verify forced ON). <paramref name="acknowledge"/> waives the CONSENT axis ONLY —
    /// the verify is a corruption-axis fact no acknowledgement overrides. (No CorpusRulebook: a removal pre-flights nothing
    /// — the present-check that the target carries the record is the whole gate.)</summary>
    WritePatchBuilder.RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> keys, string target, bool acknowledge)
    {
        // (1) Resolve target -> real on-disk path via the load order (by plugin FILENAME). Refuse loud if it isn't a real
        //     active plugin (closes the coincidental-folder collision). Same resolver as the edit + create lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place removes from the file the game actually loads. Nothing was written.");

        // (2) CONSENT axis — the persistent, server-enforced first-touch handshake, keyed off the resolved path (shared
        //     with the edit + create lanes: acknowledging a plugin once covers editing, creating into, AND removing from
        //     it in place — it's the same "touch your original" trade-off).
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath));
        string? ackNote = null;
        if (!already && acknowledge)
        {
            var (ok, err) = _store.RecordInPlaceAcknowledged(targetPath);
            if (!ok) ackNote = $"the in-place acknowledgement could not be saved ({err}) — the removal proceeded, but a future session will re-prompt for this plugin.";
        }

        // (3) Writable, same-volume parent pre-flight — refuse rather than degrade (the swap stages a sibling temp here).
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why);

        // (4) The write — absence verify forced ON (the model-C substitute for the dropped whole-plugin floor).
        var outcome = WritePatchBuilder.RemoveRecordsInPlace(resolver, keys, targetPath, targetName);

        // (5) On success, stamp the distinct audit marker + auto-flag a now-stale .seq (both best-effort; neither miss
        //     fails the done removal, Q3-noted). SEQ flag (Track C): a removal can drop the last reference to a master
        //     (a prune) and shift on-disk FormIDs, staling the plugin's .seq — surfaced, never auto-regenerated.
        if (outcome.Success)
        {
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note (PR #163 review #1) must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        else if (ackNote is not null)
        {
            // The acknowledgement was recorded but the write then failed — keep the (now-honest) note alongside the error.
            return outcome with { Note = ackNote };
        }
        return outcome;
    }

    /// <summary>Forward a NAMED plugin's version of one-or-more records into a patch as an override (housecarl_forward_record)
    /// — xEdit's "copy as override into", the inverse of <see cref="ApplyEdits"/>'s winner-override. Parses every formid
    /// (all-or-nothing on a malformed one), resolves the folder-per-patch output (fresh, or <paramref name="into"/> an
    /// existing houseCARL-owned patch), then drives <see cref="WritePatchBuilder.ForwardRecords"/> (resolve each source
    /// body from <paramref name="fromPlugin"/> → deep-copy as override → multi-master serialize). The whole source record
    /// is copied verbatim, so the SOURCE plugin (not the load-order winner) decides the content — and forwarding the
    /// ORIGIN master reverts a record to vanilla. Originals are never touched in the default lane (only the patch folder
    /// is written); <paramref name="target"/>+<paramref name="inPlace"/> is the explicit opt-in third route (in-place
    /// write lane; HCBR-2026-07-08-01 F4) — forward INTO an existing plugin's own file, consent-gated like the sibling
    /// write tools — see <see cref="ForwardRecordsInPlace"/>.</summary>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "from_plugin is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.ForwardOutcome.Fail("no formids supplied — pass the FormID(s) to forward from the source plugin.");

        // In-place is the explicit, named-file opt-in (the same contract as the sibling write tools — Q3, name each
        // misuse rather than silently ignore a parameter): in_place REQUIRES target=, is mutually exclusive with into=
        // (the houseCARL-patch lane), and target= without in_place is a no-op the caller likely didn't mean.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to forward into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place forwards into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to forward into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

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

            if (inPlace)
                return ForwardRecordsInPlace(resolver, specs, target!.Trim(), acknowledge, dryRun);

            // #225: a dry run resolves the would-be output path WITHOUT creating the mod folder (see ApplyEdits).
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun); }
            catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, fullReadback, dryRun);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused forward leaves no orphan
            return outcome;
        }
    }

    /// <summary>The in-place branch of <see cref="ForwardRecords"/> (in-place write lane; HCBR-2026-07-08-01 F4) — runs
    /// under _writeGate. REUSES every in-place seam the edit/create/remove lanes proved: the same foreign-target resolver
    /// (<see cref="ResolveActivePluginPath"/>), the same PERSISTENT first-touch CONSENT handshake (keyed off the resolved
    /// path, SHARED across all in-place lanes — acknowledging a plugin once covers edit, create, remove AND forward), the
    /// same writable-parent pre-flight, and the same distinct <c>editedInPlace=</c> marker (NEVER <c>generated=true</c> —
    /// the user mod keeps failing <see cref="IsHouseCarlOwned"/> so a later into= can't blind-overwrite it). Drives
    /// <see cref="WritePatchBuilder.ForwardRecordsInPlace"/> (replace-or-copy into the target's OWN file, touched-record
    /// verify forced ON). <paramref name="acknowledge"/> waives the CONSENT axis ONLY — the verify is a corruption-axis
    /// fact no acknowledgement overrides.</summary>
    WritePatchBuilder.ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs, string target, bool acknowledge,
        bool dryRun = false)
    {
        // (1) Resolve target -> real on-disk path via the load order (by plugin FILENAME). Refuse loud if it isn't a
        //     real active plugin. Same resolver as the edit + create + remove lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place forwards into the file the game actually loads. Nothing was written.");

        // (2) CONSENT axis — the persistent, server-enforced first-touch handshake, keyed off the resolved path (shared
        //     with the edit/create/remove lanes: it's the same "touch your original" trade-off).
        //     #225: a DRY RUN bypasses the handshake and NEVER persists an acknowledgement (see ApplyEditsInPlace) —
        //     the pending consent is surfaced as a note instead.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        string? ackNote = null;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the one-time confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                return WritePatchBuilder.ForwardOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath));
            if (!already && acknowledge)
            {
                var (ok, err) = _store.RecordInPlaceAcknowledged(targetPath);
                if (!ok) ackNote = $"the in-place acknowledgement could not be saved ({err}) — the forward proceeded, but a future session will re-prompt for this plugin.";
            }
        }

        // (3) Writable, same-volume parent pre-flight — refuse rather than degrade. Kept in the dry run (it predicts
        //     exactly what the real write would refuse on).
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.ForwardOutcome.Fail(why);

        // (4) The write — touched-record verify forced ON (the model-C substitute for the dropped whole-plugin floor).
        var outcome = WritePatchBuilder.ForwardRecordsInPlace(resolver, specs, targetPath, targetName, fullReadback: true, dryRun);

        // (5-dry) #225: a successful dry run stamps NOTHING (no editedInPlace marker, no .seq note).
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // (5) On success, stamp the distinct audit marker + auto-flag a now-stale .seq (both best-effort; neither miss
        //     fails the done forward, Q3-noted).
        if (outcome.Success)
        {
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note (PR #163 review #1) must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        else if (ackNote is not null)
        {
            // The acknowledgement was recorded but the write then failed — keep the (now-honest) note alongside the error.
            return outcome with { Note = ackNote };
        }
        return outcome;
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

    /// <summary>COMPACT / ESL-renumber a plugin (housecarl_compact_plugin, A2) — the data-layer twin of xEdit's "Compact
    /// FormIDs for ESL". Renumbers <paramref name="pluginName"/>'s ORIGINATING records (flat AND nested — cells, placed
    /// refs, dialog INFOs) into the light range 0x800–0xFFF (the 2048-ID window; <paramref name="esl"/>=false renumbers
    /// contiguously without the light flag / ceiling), repoints every internal reference, keeps overrides at their master
    /// FormIDs, and emits the result. Output model (Aaron 2026-06-26): a NEW plugin P′ keeping the source's EXACT basename
    /// (so external masters still resolve) in a fresh houseCARL mod folder by default — original UNTOUCHED, reviewable in
    /// xEdit before you swap; <paramref name="inPlace"/>=true overwrites the original instead (the xEdit norm; rides the
    /// in-place consent, no backup).
    ///
    /// <para>The load-bearing safety (Q3, plan §2): renumbering breaks any reference from OUTSIDE the plugin (they'd point
    /// at FormIDs that no longer exist). The identify-pass finds those external referencers across the whole order. NO
    /// external referencers ⇒ the clean default path (emit P′, done). External referencers present ⇒ REFUSED LOUD with the
    /// list, UNLESS <paramref name="repointExternals"/>=true, which ALSO rewrites each of them in place to follow the
    /// renumber. Any in-place overwrite (the target in the in-place lane, and/or the external referencers) requires
    /// <paramref name="acknowledge"/>=true — a first call without it returns a CONFIRM prompt listing exactly what will be
    /// rewritten (never a silent original-file edit).</para>
    ///
    /// <para>An INACTIVE target (on disk but not in the load order — the fresh houseCARL patch pre-MO2-refresh, or a
    /// disabled mod) is resolved by filename via the shared locate contract and compacted OFF-ORDER; its declared
    /// masters must still be active (HCBR-2026-07-14-02 gap 3). An override-only target with esl=true takes the
    /// FLAG-ONLY lane (empty remap; the write sets the light flag).</para>
    /// <para>Refuses loud + writes nothing on: the plugin not found on disk / ambiguous / excluded (unparseable); MORE than the
    /// light window holds (the hard ESL ceiling — named, never truncated); a declared master not active; a serialize fault.
    /// Renumber mechanism + nested coverage: <see cref="RemapEngine.RenumberModInto"/> (remap-wave1/2). Serialized on the
    /// write gate; the identify-pass is one whole-order link walk (~25s at full scale — a deliberate, one-shot operation).</para></summary>
    public WritePatchBuilder.CompactOutcome CompactPlugin(
        string pluginName, bool esl = true, bool inPlace = false, bool repointExternals = false,
        bool acknowledge = false, string? patchName = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CompactOutcome.Fail("plugin is required — name the plugin filename to compact (e.g. 'CoolMod.esp').");

        lock (_writeGate)                                                 // one write at a time; the whole resolve→build→repoint runs under it
        {
            var resolver = Resolver;                                      // builds/refreshes; reentrant with _writeGate
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CompactOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            var name = pluginName.Trim();
            string? srcPath;
            string? offOrderNote = null;
            if (view.ContainsPlugin(name))
            {
                if (view.ExcludedPlugins.TryGetValue(name, out var excluded))
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"cannot compact '{name}': it was EXCLUDED from this session ({excluded}) — houseCARL won't renumber a plugin it can't fully parse. The file is untouched.");
                srcPath = view.PluginPath(name);
                if (srcPath is null || !File.Exists(srcPath))
                    return WritePatchBuilder.CompactOutcome.Fail($"'{name}' not found on disk at {srcPath ?? "<unresolved>"} — nothing to compact.");
            }
            else
            {
                // NOT in the active order → resolve the FILE on disk (enabled, disabled, AND unlisted mod folders — the
                // shared locate contract). This is the pre-enable finishing lane (HCBR-2026-07-14-02 gap 3): ESL-flagging
                // the patch houseCARL just wrote, BEFORE the MO2 refresh puts it in plugins.txt. The requirement that
                // actually protects correctness is unchanged: every DECLARED MASTER must be active (CompactBuild refuses
                // otherwise), and the external-referencer scan still runs over the active order — which, for a plugin
                // nothing active can master, is exactly the right (empty) answer.
                string modsDir, dataDir, overwriteDir, profileDir;
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                var comp = Mo2LoadOrder.ReadComposition(profileDir);
                var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, name, null);
                if (loc.Error is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not an active plugin in your load order, and no on-disk copy was found either ({loc.Error})");
                if (loc.Ambiguous is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                        $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to compact. " +
                        "Enable the one you mean in MO2, or remove the duplicates.");
                srcPath = loc.Path!;
                offOrderNote = $"'{name}' is not in the active load order (found: {loc.Where}) — compacted OFF-ORDER; " +
                               "masters resolved from the active order. Enable the result in MO2 to use it.";
            }

            ModKey modKey;
            try { modKey = ModKey.FromFileName(name); }
            catch (Exception ex) { return WritePatchBuilder.CompactOutcome.Fail($"'{name}' is not a valid plugin filename ({ex.Message})."); }

            // 1. originating record keys + the remap into the (light, by default) window.
            if (!WritePatchBuilder.TryReadOriginatingKeys(srcPath, modKey, out var keys, out var keyErr))
                return WritePatchBuilder.CompactOutcome.Fail(keyErr!);
            string? flagOnlyNote = null;
            uint floor = RemapEngine.EslFloor;
            IReadOnlyDictionary<FormKey, FormKey> remapDict;
            if (keys.Count == 0)
            {
                // Override-only (or empty) plugin: nothing to RENUMBER — but with esl=true the job the caller actually
                // wants (make it light) is trivially satisfiable, because the light window only constrains ORIGINATING
                // records. Proceed with an empty remap: every record copies verbatim and the write sets the light flag
                // (the flag-only lane — the all-override compatibility patch, HCBR-2026-07-14-02 gap 3's ESL endgame).
                if (!esl)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' defines no originating records to renumber (it carries only overrides, or is empty) — nothing to compact. " +
                        "(With esl=true this would still set the ESL/light header flag — always valid for an override-only plugin.)");
                remapDict = new Dictionary<FormKey, FormKey>();
                flagOnlyNote = $"'{name}' defines no originating records — nothing renumbered; every record copied verbatim with the ESL (light) flag set (always valid for an override-only plugin).";
            }
            else
            {
                uint ceiling = esl ? RemapEngine.EslCeiling : FormIdRange.ObjectIdMax;   // light window, or the full 24-bit object-ID range
                var plan = RemapEngine.BuildSequentialRemap(keys, modKey, floor, ceiling);
                if (!plan.Success) return WritePatchBuilder.CompactOutcome.Fail(plan.Error!);
                remapDict = plan.Dict;
            }

            // 2. identify-pass — which plugins OUTSIDE the target reference a record being renumbered (the break risk).
            //    Nothing being renumbered ⇒ nothing can break ⇒ the scan has nothing to ask (skip the whole-order walk).
            var targets = remapDict.Keys.ToHashSet();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var id = targets.Count == 0
                ? new RemapEngine.IdentifyResult(Array.Empty<RemapEngine.ExternalRef>(), Array.Empty<string>(), 0, 0,
                                                 Array.Empty<string>(), Array.Empty<RemapEngine.ExternalOverride>(), Array.Empty<string>())
                : RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);

            // 3. external-referencer policy (Q3 — never silently ship a compaction that dangles an external reference).
            if (id.HasExternalReferencers)
            {
                var refList = $"{string.Join(", ", id.ExternalPlugins.Take(25))}{(id.ExternalPlugins.Count > 25 ? $", … (+{id.ExternalPlugins.Count - 25} more)" : "")}";
                if (!repointExternals)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — {id.ExternalPlugins.Count} plugin(s) outside '{name}' reference records it is about to renumber; compacting it " +
                        $"WOULD BREAK those references (they would point at FormIDs that no longer exist). Referencers: {refList}. " +
                        "Re-run with repoint_externals=true AND in_place=true (+ acknowledge=true) to ALSO rewrite those plugins in place to follow " +
                        "the renumber, or handle them yourself first. Nothing was written.");
                // repoint is only COHERENT paired with in_place (PR #122 review #1): in the new-file lane the renumbered
                // records live ONLY in the not-yet-active P′, so repointing the externals now would leave them dangling
                // against the still-active original until the MO2 swap — and broken if the user rejects P′. Couple them.
                if (!inPlace)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — repoint_externals requires in_place=true. {id.ExternalPlugins.Count} plugin(s) reference records being renumbered " +
                        $"({refList}); in the new-file lane those records exist ONLY in the not-yet-active P′, so repointing the externals now would leave " +
                        "them dangling against the still-active original until you complete the MO2 swap (and broken if you reject P′). Either compact IN " +
                        "PLACE (in_place=true) so the target and its referrers move together, or handle the externals yourself after enabling P′. Nothing was written.");
            }

            // 4. consent gate — any in-place overwrite (the target, and/or the external referencers) needs acknowledge=true.
            bool willOverwriteTarget = inPlace;
            bool willRepoint = id.HasExternalReferencers && repointExternals;
            if ((willOverwriteTarget || willRepoint) && !acknowledge)
            {
                var c = new System.Text.StringBuilder();
                c.Append("CONFIRM in-place rewrite (your ORIGINAL file(s) will be rewritten — no houseCARL backup or undo; keep your own):\n");
                if (willOverwriteTarget) c.Append($"  - '{name}' will be OVERWRITTEN in place with its compacted form.\n");
                if (willRepoint)
                {
                    c.Append($"  - {id.ExternalPlugins.Count} external referencer(s) will be REWRITTEN in place to repoint to the new FormIDs:\n");
                    foreach (var pl in id.ExternalPlugins.Take(25)) c.Append($"      · {pl}\n");
                    if (id.ExternalPlugins.Count > 25) c.Append($"      · … (+{id.ExternalPlugins.Count - 25} more)\n");
                }
                c.Append("Re-call with acknowledge=true to proceed.");
                return WritePatchBuilder.CompactOutcome.Confirm(c.ToString());
            }

            // pre-flight the in-place target's parent is writable BEFORE any work — the in-place edit lane's layer-3 check,
            // a nicer early refusal than failing deep in the atomic swap (PR #122 review #2). Each external referencer gets
            // the same guarantee inside RepointInPlace's own all-or-nothing write.
            if (inPlace && InPlaceParentUnwritable(srcPath, out var unwritable))
                return WritePatchBuilder.CompactOutcome.Fail(unwritable);

            // 5. output location — in-place (overwrite the original) or a NEW file keeping the source's EXACT basename in
            //    a fresh houseCARL mod folder (so its masters still resolve; the user swaps the folder in MO2 to use it).
            string outPath; bool createdFresh = false; RiderFolder rf = default;
            if (inPlace) outPath = srcPath;
            else
            {
                try { rf = ResolvePatchModFolder(patchName, null, Path.GetFileNameWithoutExtension(name) + " compacted"); }
                catch (InvalidOperationException ex) { return WritePatchBuilder.CompactOutcome.Fail(ex.Message); }
                createdFresh = rf.CreatedFresh;
                WriteOwnerMeta(rf.ModFolder, name);                       // P′ keeps the source's exact basename
                outPath = Path.Combine(rf.OutputDir, name);
            }

            // 6. build + write the compacted plugin.
            var build = WritePatchBuilder.CompactBuild(srcPath, modKey, remapDict, view.PluginPath, outPath, esl, floor);
            if (!build.Success)
            {
                if (!inPlace && createdFresh) RemoveOrNameRiderResidue(rf);   // a refused build leaves no orphan folder
                return WritePatchBuilder.CompactOutcome.Fail(build.Error!);
            }

            // 7. opt-in: repoint each external referencer in place (per-plugin all-or-nothing; every result reported, Q3).
            var repointed = new List<WritePatchBuilder.RepointReport>();
            if (willRepoint)
                foreach (var ext in id.ExternalPlugins)
                {
                    var rep = RemapEngine.RepointInPlace(resolver, ext, remapDict);
                    repointed.Add(new WritePatchBuilder.RepointReport(ext, rep.Success, rep.Error));
                }

            // 7b. carry the FormID-keyed assets a renumber moves — FaceGen head mesh/tint (A1) + voice .fuz/.lip (A2). The
            //     records renumbered, so the asset FILES the engine looks up BY FormID must follow, or a compacted NPC mod
            //     silently dark-faces and a voiced mod goes mute (the gap this research exposed in the shipped tool). ONE
            //     captured asset view feeds both carries AND the 7c SEQ gate, so all three agree on what's in the VFS. Best-
            //     effort + REPORTED (Q3): the records are already written, so an asset we can't carry is a NAMED warning in the
            //     outcome, never a failure of the compaction — and the asset layer failing to build never fails the compact
            //     either. outDir = the P′ mod-folder root (the directory holding the plugin) in BOTH lanes (new-file: the
            //     fresh folder; in-place: the target's own folder).
            //   SEQ-gate (for 7c, refresh-only): "did the source SHIP a .seq?" is a VFS question, not a single-folder one — a
            //   prior housecarl_write_seq run files the .seq in its OWN houseCARL_SEQ mod folder, and a packed mod ships it in
            //   a BSA. So resolve SEQ\<basename>.seq through the SAME captured view (mirrors the dialogue validator's CheckSeq),
            //   never a loose File.Exists on the source folder — which would miss both and re-open the silent failure A3 closes.
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS gate result — SET the moment the view resolves, BEFORE the carries
            var srcSeqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(srcPath)}.seq";
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate (the PlaceAssets idiom)
                var assetView = assetResolver.Capture();
                seqGate = assetView.ResolveForPlacement(srcSeqRel).Sources.Count > 0;   // VFS-aware (loose roots + active BSAs)
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, remapDict, assetView, outDir);
                voiceRename = AssetRenameService.CarryVoice(outPath, remapDict, assetView, outDir);
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // The gate is the VFS answer whenever the view resolved — even if a LATER carry threw, a carry failure must NOT
            // downgrade a good gate result (re-review). Only when the view never resolved (seqGate still null — the asset layer
            // couldn't be built) fall back to the loose-only check (degraded, never worse than the pre-fix behavior).
            bool sourceHadSeq = seqGate ?? File.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, srcSeqRel));

            // 7c. REFRESH the start-game-enabled-quest .seq from the RENUMBERED plugin when the source SHIPPED one (the VFS
            //     gate above). A renumber shifts every SGE quest's master-relative on-disk FormID, so a shipped .seq is now
            //     STALE — its quests would silently never start (the failure SeqFile exists to prevent). REFRESH-ONLY
            //     (maintainer's call): if the source shipped NO .seq we do NOT invent one (xEdit-compaction parity) —
            //     RegenerateSeq returns a named advisory. The REGEN itself is off P′ (SeqFile.Build — the write_seq path) and
            //     needs no resolver; only the gate consults the view. Best-effort + REPORTED (Q3): RegenerateSeq never throws
            //     and never fails the compact; the outer guard is belt-and-suspenders.
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, sourceHadSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if '{name}' has start-game-enabled quests, run housecarl_write_seq on the compacted plugin." });
            }

            // 8. audit markers (PR #122 review #2): stamp the distinct editedInPlace= breadcrumb into the meta.ini of EVERY
            //    file rewritten IN PLACE — the target (in-place lane) + each successfully repointed external — matching the
            //    traceability the in-place EDIT lane gives. The CONSENT model deliberately stays compact's own per-call
            //    confirm (NOT the persistent _store ack the edit lane uses): a compaction can rewrite a broad surface (the
            //    target + N externals), so each one re-confirms with its exact overwrite list rather than letting a stale
            //    field-edit ack silently authorize a full renumber. Markers are best-effort (a miss never fails the done
            //    write, Q3) — any miss is surfaced in Note.
            var markerNotes = new List<string>();
            if (offOrderNote is not null) markerNotes.Add(offOrderNote);
            if (flagOnlyNote is not null) markerNotes.Add(flagOnlyNote);
            if (inPlace) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(srcPath)); if (n is not null) markerNotes.Add(n); }
            foreach (var r in repointed.Where(r => r.Success))
            {
                var rp = view.PluginPath(r.Plugin);
                if (rp is not null) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(rp)); if (n is not null) markerNotes.Add(n); }
            }

            return new WritePatchBuilder.CompactOutcome(
                true, null, false, outPath, name, inPlace, esl, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                build.Bytes, id.ExternalPlugins, repointed, id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples,
                markerNotes.Count > 0 ? string.Join(" ", markerNotes) : null, assetRename, id.ExternalOverriders, voiceRename, seqRegen);
        }
    }

    /// <summary>Merge two or more ACTIVE plugins into ONE new plugin (housecarl_merge_plugins — the A4 orchestration
    /// over the compact spine). Merge = a RECORDS operation: the donors' records combine into a fresh plugin under a
    /// NEW name (collision-only renumber — the first donor in load order keeps its object IDs; cross-donor conflicts
    /// on the same record resolve to the LOAD-ORDER WINNER and are reported; a losing donor's un-relisted nested
    /// children graft into the winner). The donors are NEVER touched — new-file lane only, no consent gate; the user
    /// reviews M in xEdit, enables its folder, and deactivates the donor PLUGINS in MO2 (the donor MOD FOLDERS stay
    /// enabled — the merged records still reference the donors' path-keyed assets, which only those folders serve;
    /// the carries cover ONLY the FormID-keyed facegen/voice/seq). External referencers AND overriders
    /// of donor records are WARNED loud and named (never a refusal: nothing breaks at write time — the donors stay
    /// active until the user swaps; the remedy is to include the patch in the merge set or repoint it before disabling
    /// the donors). The FormID-keyed assets follow per donor: EVERY donor NPC's facegen and EVERY voiced line move to
    /// the new plugin-name folders (the plugin NAME is part of those paths, so this is the full donor set, not just
    /// collisions), and a <c>.seq</c> is refreshed when any donor shipped one.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? outputName, string? patchName = null)
    {
        // ---- 0. argument shape (Q3 — every refusal names the fix) ----
        var donorsRaw = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (donorsRaw.Count < 2)
            return WritePatchBuilder.MergeOutcome.Fail(
                "merge needs at least TWO distinct donor plugins — pass plugins=[\"A.esp\", \"B.esp\", …].");
        var outName = (outputName ?? "").Trim();
        if (outName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "output is required — name the NEW merged plugin file to create (e.g. 'MyMerge.esp'). It must not already exist in your load order.");
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"'{outName}' is not a valid plugin filename ({ex.Message})."); }
        if (outKey.Type == ModType.Light)
            return WritePatchBuilder.MergeOutcome.Fail(
                $"refused — '{outName}' has the .esl extension, which the game engine force-treats as a LIGHT master regardless " +
                "of the header flag, but a merge keeps the donors' object ids in the full range (ids above 0xFFF would be misread " +
                "in game). Merge to a '.esp' instead, then run housecarl_compact_plugin on it to make it light (the tools compose). Nothing was written.");
        if (donorsRaw.Any(d => string.Equals(d, outName, StringComparison.OrdinalIgnoreCase)))
            return WritePatchBuilder.MergeOutcome.Fail($"the output '{outName}' cannot also be a donor — name a NEW plugin file.");

        lock (_writeGate)                                                 // one write at a time (the compact discipline)
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.MergeOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");
            if (view.ContainsPlugin(outName))
                return WritePatchBuilder.MergeOutcome.Fail(
                    $"'{outName}' is already an active plugin in your load order — the merge output must be a NEW plugin name " +
                    "(merging over an existing plugin would shadow it in MO2).");

            // ---- 1. validate + load-order-sort the donors (merge semantics are load-order semantics — Q3: sort, don't
            //      trust arg order). ONE name→position index serves the donor sort here and the master sort in step 4. ----
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < resolver.PluginNames.Count; i++) orderIndex[resolver.PluginNames[i]] = i;
            var donorInfos = new List<(string Name, string Path, ModKey Key, int Order)>();
            foreach (var d in donorsRaw)
            {
                if (!view.ContainsPlugin(d))
                {
                    // Same trim as read_record's: once a cause is stated it carries its own remedy, and the legacy
                    // "Enable it in MO2 first (pass the exact filename)" both conflates the vocabulary this change is
                    // fixing (a MOD is enabled; a PLUGIN is activated) and asks for a filename that has already
                    // resolved to a real installed plugin (review of PR #274, round 2).
                    var dWhy = view.ExplainAbsence(d);
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"donor '{d}' is not an active plugin in your load order." +
                        (dWhy is not null ? " " + dWhy : view.NameSuggestion(d)) +
                        " Merge reads each donor's records and conflict position from the ACTIVE order." +
                        (dWhy is not null ? "" : " Activate it in MO2 first (pass the exact plugin filename, e.g. 'CoolMod.esp')."));
                }
                if (view.ExcludedPlugins.TryGetValue(d, out var excluded))
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge '{d}': it was EXCLUDED from this session ({excluded}) — houseCARL won't merge a plugin it " +
                        "can't fully parse (it would risk dropping records it couldn't read, Q3). Nothing was written.");
                var p = view.PluginPath(d);
                if (p is null || !File.Exists(p))
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' not found on disk at {p ?? "<unresolved>"} — nothing to merge.");
                ModKey dk;
                try { dk = ModKey.FromFileName(d); }
                catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"'{d}' is not a valid plugin filename ({ex.Message})."); }
                if (!orderIndex.TryGetValue(d, out var order))            // unreachable after ContainsPlugin (same source table) — refuse loud, never mis-sort
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' has no load-order position (index inconsistency, Q3). Nothing was written.");
                donorInfos.Add((d, p, dk, order));
            }
            donorInfos.Sort((a, b) => a.Order.CompareTo(b.Order));
            var donorNames = donorInfos.Select(d => d.Name).ToList();

            // ---- 2. originating keys per donor + the collision-only remap (first donor keeps its ids — zMerge default) ----
            var donorKeys = new List<(string Donor, IReadOnlyList<FormKey> Keys)>();
            foreach (var (dName, dPath, dKey, _) in donorInfos)
            {
                if (!WritePatchBuilder.TryReadOriginatingKeys(dPath, dKey, out var keys, out var keyErr))
                    return WritePatchBuilder.MergeOutcome.Fail(keyErr!);
                donorKeys.Add((dName, keys));                             // a pure-override donor (0 originating keys) is a legit patch donor
            }
            var plan = RemapEngine.BuildMergeRemap(donorKeys, outKey, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
            if (!plan.Success) return WritePatchBuilder.MergeOutcome.Fail(plan.Error!);

            // ---- 3. identify-pass — WARN-and-proceed (the A4 posture; unlike compact this NEVER refuses: the donors stay
            //      installed and ACTIVE until the user swaps in MO2, so nothing breaks at write time. The report names each
            //      affected plugin with the remedy — include it in the merge set, or handle it before disabling the donors.) ----
            var targets = plan.Dict.Keys.ToHashSet();
            var transformSet = new HashSet<string>(donorNames, StringComparer.OrdinalIgnoreCase);
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);

            // ---- 4. masters = union(donor declared masters) − donors, load-order sorted (each donor's own header order
            //      is already load-order-consistent; the union sorts by the active order so the merged header is too) ----
            var masterSet = new List<string>();
            var seenMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dName, _, _, _) in donorInfos)
            {
                IReadOnlyList<string> declared;
                try { declared = view.DeclaredMasters(dName); }
                catch (Exception ex)
                {
                    return WritePatchBuilder.MergeOutcome.Fail($"cannot read donor '{dName}' masters ({ex.Message}) — nothing written.");
                }
                foreach (var mfn in declared)
                    if (!transformSet.Contains(mfn) && seenMasters.Add(mfn)) masterSet.Add(mfn);
            }
            masterSet.Sort((a, b) =>
                (orderIndex.TryGetValue(a, out var ia) ? ia : int.MaxValue).CompareTo(orderIndex.TryGetValue(b, out var ib) ? ib : int.MaxValue));

            // ---- 5. output folder (fresh houseCARL mod folder; the merge is ALWAYS a new file — no in-place lane) ----
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, null, Path.GetFileNameWithoutExtension(outName) + " merged"); }
            catch (InvalidOperationException ex) { return WritePatchBuilder.MergeOutcome.Fail(ex.Message); }
            WriteOwnerMeta(rf.ModFolder, outName);
            var outPath = Path.Combine(rf.OutputDir, outName);

            // ---- 6. build + write M ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath);
            if (!build.Success)
            {
                if (rf.CreatedFresh) RemoveOrNameRiderResidue(rf);        // a refused build leaves no orphan folder
                return WritePatchBuilder.MergeOutcome.Fail(build.Error!);
            }

            // ---- 7. FormID-keyed assets follow the renumber, PER DONOR (merge renames the plugin, and the plugin NAME
            //      is a segment of the facegen + voice paths — so the carry covers EVERY donor NPC/voiced line, not just
            //      the id collisions). ONE captured asset view feeds the carries AND the SEQ gate. Best-effort + REPORTED
            //      (Q3): the records are written, so an asset miss is a named warning, never a failure of the merge. ----
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS gate — SET the moment the view resolves (compact's seqGate discipline)
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate (the PlaceAssets idiom)
                var assetView = assetResolver.Capture();
                seqGate = false;                                          // the view resolved — the answer below is authoritative
                foreach (var (dName, _, _, _) in donorInfos)              // "did ANY donor ship a .seq?" — VFS-aware, per donor
                    if (assetView.ResolveForPlacement($@"SEQ\{Path.GetFileNameWithoutExtension(dName)}.seq").Sources.Count > 0)
                        { seqGate = true; break; }
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, plan.Dict, assetView, outDir);
                var voiceParts = donorInfos
                    .Select(d => AssetRenameService.CarryVoice(outPath, plan.Dict, assetView, outDir, sourcePlugin: d.Name))
                    .ToList();
                voiceRename = new VoiceCarryOutcome(
                    voiceParts.Sum(v => v.FilesScanned), voiceParts.Sum(v => v.FilesCarried), voiceParts.Sum(v => v.LinesCarried),
                    voiceParts.SelectMany(v => v.Failures).ToList(), voiceParts.Any(v => v.ReadIncomplete));
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // Only when the view never resolved (seqGate still null — the asset layer couldn't be built) fall back to a
            // loose per-donor-folder check, so an asset-layer fault can't silently downgrade a donor-shipped .seq to
            // "the donors shipped none" and skip the refresh with a factually wrong advisory (compact's ?? discipline).
            bool anyDonorSeq = seqGate ?? donorInfos.Any(d =>
                File.Exists(Path.Combine(Path.GetDirectoryName(d.Path)!, "SEQ", Path.GetFileNameWithoutExtension(d.Name) + ".seq")));

            // ---- 8. SEQ — refresh-only, off the merged plugin: rebuilt when ANY donor shipped a .seq (all their SGE
            //      quests now live in M, whose .seq must list the NEW on-disk FormIDs); donors with SGE quests but no
            //      shipped .seq get the same NAMED write_seq advisory compact gives. ----
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, anyDonorSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if the donors have start-game-enabled quests, run housecarl_write_seq on '{outName}'." });
            }

            // Q3 — surface the one behavior delta the any-donor SEQ gate can introduce: SeqFile.Build lists EVERY SGE
            // quest in M, so a quest from a donor that shipped NO .seq (and thus wasn't auto-starting) gains an entry.
            string? note = seqRegen.Written
                ? "the regenerated .seq lists EVERY start-game-enabled quest in the merge — including any from a donor that " +
                  "shipped no .seq of its own (such quests were NOT auto-starting before the merge; they will now)."
                : null;

            return new WritePatchBuilder.MergeOutcome(
                true, null, outPath, outName, donorNames, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                plan.Donors, build.Conflicts, id.ExternalPlugins, id.ExternalOverriders,
                id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples, build.Bytes, note,
                assetRename, voiceRename, seqRegen);
        }
    }

    /// <summary>The composed standalone-NPC-copy verb (housecarl_copy_npc_appearance — capability chain Stage 3).
    /// Deep-copies a donor NPC's appearance-record subtree into a houseCARL patch under NEW FormKeys (Duplicate +
    /// RemapLinks — the RemapEngine mechanism, so every field carries by construction: HDPT.Parts morph refs,
    /// TextureLighting, tints), preserves headpart EditorIDs (facegeom block-name identity), renames the facegen
    /// pair to the new key's path, and carries the donor-only textures/meshes the records + geom reference.
    /// TWO TARGET MODES: <paramref name="targetFormid"/> dresses an EXISTING NPC (appearance fields copied onto an
    /// override); <paramref name="newEditorid"/> mints a full standalone CLONE (donor NPC duplicated; every remaining
    /// donor-internal non-appearance link stripped + reported by name — Q3, the clone is donor-free LOUDLY).
    /// The donor may be ACTIVE (read via the load-order winner) or sit in a plugin FILE houseCARL locates across
    /// enabled/disabled mod folders (<paramref name="sourcePlugin"/> — the read_plugin_file lane, stamped
    /// out-of-load-order in the outcome). Never touches the donor; output = folder-per-patch or into= extend.</summary>
    public NpcCopyOutcome CopyNpcAppearance(
        string sourceFormid, string? sourcePlugin, string? sourceMod,
        string? targetFormid, string? newEditorid, string? newName,
        string? patchName, string? into)
    {
        // ---- 0. argument shape (Q3 — exactly one target mode) ----
        FormKey donorFk;
        try { donorFk = FormKey.Factory((sourceFormid ?? "").Trim()); }
        catch (Exception ex) { return NpcCopyOutcome.Fail($"bad source formid '{sourceFormid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '000D62:Vivace.esp'."); }

        bool apply = !string.IsNullOrWhiteSpace(targetFormid);
        bool clone = !string.IsNullOrWhiteSpace(newEditorid);
        if (apply == clone)
            return NpcCopyOutcome.Fail(
                "pass EXACTLY ONE target: target_formid= (copy the donor's appearance onto an EXISTING NPC) or " +
                "new_editorid= (mint a full standalone CLONE of the donor as a new NPC).");
        FormKey targetFk = default;
        if (apply)
        {
            try { targetFk = FormKey.Factory(targetFormid!.Trim()); }
            catch (Exception ex) { return NpcCopyOutcome.Fail($"bad target formid '{targetFormid}': {ex.Message}."); }
        }

        lock (_writeGate)
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return NpcCopyOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            using var session = resolver.OpenSession();

            // ---- 1. read the donor NPC — the ACTIVE lane (load-order winner) or the FILE lane (any plugin on disk,
            //         disabled included — the read_plugin_file machinery; results stamped out-of-load-order). ----
            INpcGetter donorNpc;
            NpcAppearanceCopy.DonorFetch fetch;
            // "Donor-bound" ModKeys — the plugin(s) being standalone-ized away from. A BASE-GAME/implicit master is
            // NEVER donor-bound (review finding): copying a vanilla-defined NPC's look must not classify NordRace or
            // vanilla headparts as "donor-internal" (that produced a nonsense custom-race refusal and would wholesale-
            // internalize vanilla records). With an empty set the copy is an override-style transplant, said plainly.
            var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
            var donorMods = new HashSet<ModKey>();
            if (!baseMasters.Contains(donorFk.ModKey)) donorMods.Add(donorFk.ModKey);
            string donorReadFrom; bool outOfLoadOrder;
            ISkyrimModGetter? donorOverlay = null;    // file lane only — disposed in finally
            ISkyrimModGetter? widenOverlay = null;    // file lane, auto-widened defining plugin — disposed in finally
            string? donorFilePath = null;             // file lane: the located file; active lane: the winner's path (for the donor-disk asset fallback)
            string? widenFilePath = null;             // file lane: the auto-widened defining plugin's file (self-lock check)
            string widenNote = "";                    // file lane: the auto-widen's read context (what was auto-read, or why it couldn't be) — appended to a FETCH-MISS closure refusal only, never a failure of its own
            string dataDirForAssets;
            try
            {
                if (!string.IsNullOrWhiteSpace(sourcePlugin))
                {
                    string modsDir, dataDir, overwriteDir, profileDir;
                    lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                    dataDirForAssets = dataDir;
                    var comp = Mo2LoadOrder.ReadComposition(profileDir);
                    var sp = sourcePlugin.Trim();
                    var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, sp, sourceMod);
                    if (loc.Error is not null) return NpcCopyOutcome.Fail(loc.Error);
                    if (loc.Ambiguous is not null)
                        return NpcCopyOutcome.Fail($"'{sp}' exists in {loc.Ambiguous.Count} places ({string.Join(" | ", loc.Ambiguous.Select(h => h.Where))}) — pass source_mod= to pick one.");
                    // Same vocabulary fix as the diff pole: the donor line said ", DISABLED" for an unticked or shadowed
                    // donor whose mod is perfectly enabled. State the cause the locate contract computed (#271).
                    static string Located(PluginLocateResult l) => $"{l.Where}{(l.WhyNotActive is { } why ? $"; NOT active — {why}" : "")}";
                    static NpcAppearanceCopy.DonorFetch CacheFetch(Mutagen.Bethesda.Plugins.Cache.ILinkCache c) =>
                        fk2 => { try { return c.TryResolve(fk2, out var b) ? b : null; } catch { return null; } };

                    donorFilePath = loc.Path!;
                    donorReadFrom = loc.Where == "direct path"
                        ? $"direct path '{donorFilePath}'"
                        : $"file '{sp}' ({Located(loc)})";
                    outOfLoadOrder = true;
                    ModKey? namedFileKey = null;
                    try
                    {
                        var fileKey = ModKey.FromFileName(Path.GetFileName(donorFilePath));
                        namedFileKey = fileKey;
                        if (!baseMasters.Contains(fileKey)) donorMods.Add(fileKey);
                    }
                    catch { /* a direct path with an odd name — donorFk.ModKey still governs */ }

                    donorOverlay = LoadOrderResolver.OpenOverlay(donorFilePath, string.IsNullOrEmpty(dataDir) ? null : dataDir);
                    // Lazy per-type link cache — no eager whole-file parse (review finding), and per-resolve fault
                    // isolation: one unparseable record elsewhere in the file must not abort the verb (Q3).
                    var donorCache = donorOverlay.ToImmutableLinkCache();
                    IMajorRecordGetter? donorBody;
                    try { donorBody = donorCache.TryResolve(donorFk, out var db) ? db : null; }
                    catch (Exception ex) { return NpcCopyOutcome.Fail($"'{Path.GetFileName(donorFilePath)}' could not be read around {donorFk} — a record Mutagen cannot parse: {ex.Message}"); }
                    if (donorBody is null)
                        return NpcCopyOutcome.Fail($"file '{Path.GetFileName(donorFilePath)}' does not define or override {donorFk}. This reads the FILE's own records (out of load order); for an active donor omit source_plugin=.");
                    if (donorBody is not INpcGetter fileNpc)
                        return NpcCopyOutcome.Fail($"{donorFk} in '{Path.GetFileName(donorFilePath)}' is a {RecordNaming.StripOverlay(donorBody.GetType().Name)}, not an NPC.");
                    donorNpc = fileNpc;
                    var namedFetch = CacheFetch(donorCache);
                    fetch = namedFetch;

                    // AUTO-WIDEN (Aaron 2026-07-07 — PR #156 finding 6): the named file only OVERRIDES the donor;
                    // the records a standalone copy must internalize live in the DEFINING plugin, which an override
                    // patch points at but does not contain. Locate that plugin on disk too and let the closure fall
                    // through to it — the named file stays first (its override IS the look being asked for). Said
                    // loudly in the render on success, and carried as a NOTE onto a fetch-miss closure refusal
                    // either way (what was read, or why the widen could not happen) — never a hard failure of its
                    // own: a donor whose closure never needs the defining plugin must keep working.
                    if (donorMods.Contains(donorFk.ModKey) && namedFileKey != donorFk.ModKey)
                    {
                        var defName = donorFk.ModKey.FileName.String;
                        string WidenMiss(string why) =>
                            $" NOTE: '{Path.GetFileName(donorFilePath)}' only OVERRIDES the donor — its defining plugin '{defName}' {why}";
                        var wloc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, defName, null);
                        if (wloc.Error is not null)
                            widenNote = WidenMiss($"was auto-searched for but not found: {wloc.Error}");
                        else if (wloc.Ambiguous is not null)
                            widenNote = WidenMiss($"exists in {wloc.Ambiguous.Count} places ({string.Join(" | ", wloc.Ambiguous.Select(h => h.Where))}), so it was not auto-read — enable the defining mod, or remove/rename the duplicate copy, and re-run.");
                        else
                        {
                            try
                            {
                                widenOverlay = LoadOrderResolver.OpenOverlay(wloc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir);
                                widenFilePath = wloc.Path;
                                var widenFetch = CacheFetch(widenOverlay.ToImmutableLinkCache());
                                fetch = fk2 => namedFetch(fk2) ?? widenFetch(fk2);
                                donorReadFrom += $"; AUTO-WIDENED to the donor's defining plugin '{defName}' ({Located(wloc)}) — the named file only overrides the donor, the defining plugin carries the records being standalone-ized";
                                widenNote = $" NOTE: the donor's defining plugin '{defName}' was AUTO-READ as well ({Located(wloc)}) — the missing record is in neither the named file nor it.";
                            }
                            catch (Exception ex)
                            {
                                widenNote = WidenMiss($"was found but could not be opened: {ex.Message}");
                            }
                        }
                    }
                }
                else
                {
                    var winner = view.ResolveWinner(donorFk);
                    if (winner is null)
                        return NpcCopyOutcome.Fail(
                            $"{donorFk} is not present in the active load order. If the donor plugin is DISABLED, pass source_plugin= " +
                            "(its filename — houseCARL locates it across enabled AND disabled mod folders) to read it out of load order.");
                    var body = view.GetRecord(session, winner.Value.WinnerPlugin, donorFk);
                    if (body is null)
                        return NpcCopyOutcome.Fail($"could not fetch {donorFk} from its winner '{winner.Value.WinnerPlugin}'.");
                    if (body is not INpcGetter activeNpc)
                        return NpcCopyOutcome.Fail($"{donorFk} is a {RecordNaming.StripOverlay(body.GetType().Name)}, not an NPC.");
                    donorNpc = activeNpc;
                    donorReadFrom = $"active load order (winner: {winner.Value.WinnerPlugin})";
                    outOfLoadOrder = false;
                    donorFilePath = view.PluginPath(donorFk.ModKey.FileName.ToString());
                    lock (_gate) { EnsurePathsDerived(); dataDirForAssets = _dataDir; }
                    fetch = fk2 =>
                    {
                        var w2 = view.ResolveWinner(fk2);
                        return w2 is null ? null : view.GetRecord(session, w2.Value.WinnerPlugin, fk2);
                    };
                }

                NpcAppearanceCopy.ActiveResolve active = fk2 => view.ResolveWinner(fk2) is not null;

                // ---- 2. the appearance closure (generic link walk; loud refusals — custom race, runaway, unreadable) ----
                var closure = NpcAppearanceCopy.CollectAppearanceClosure(donorNpc, donorMods, fetch, active);
                if (!closure.Success) return NpcCopyOutcome.Fail(closure.Error! + (closure.FetchMiss ? widenNote : ""));

                // ---- 3. output patch (folder-per-patch or into= extend — the record lane's resolver + ownership gate) ----
                string outPath; bool extend, created;
                var defaultStem = clone ? newEditorid!.Trim() : "houseCARL_NpcCopy";
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? defaultStem : null), into, out extend, out created); }
                catch (Exception ex) { return NpcCopyOutcome.Fail(ex.Message); }
                var patchFileName = Path.GetFileName(outPath);
                var patchModKey = ModKey.FromFileName(patchFileName);

                // ---- 4. apply lane: resolve the ACTIVE target body up front (an in-patch target — its formid names
                //         the patch itself — is resolved inside the build, off the opened patch mod). ----
                INpcGetter? targetActiveBody = null;
                if (apply && targetFk.ModKey != patchModKey)
                {
                    if (donorMods.Contains(targetFk.ModKey))
                        return NpcCopyOutcome.Fail("the target NPC lives in the DONOR's plugin — that cannot be standalone-ized onto itself; pick a target outside the donor (or use new_editorid= to clone).");
                    var tw = view.ResolveWinner(targetFk);
                    if (tw is null)
                        return NpcCopyOutcome.Fail(
                            $"target {targetFk} is not present in the active load order. The target must be an ACTIVE NPC " +
                            "(enable its plugin in MO2), or a record in the patch itself (into= that patch and use its formid).");
                    var tb = view.GetRecord(session, tw.Value.WinnerPlugin, targetFk);
                    if (tb is not INpcGetter tnpc)
                        return NpcCopyOutcome.Fail($"target {targetFk} is a {RecordNaming.StripOverlay(tb?.GetType().Name ?? "<unfetchable>")}, not an NPC.");
                    targetActiveBody = tnpc;
                }

                // ---- 4b. neither donor-side FILE may be the output patch (review finding: the overlays stay
                //          memory-mapped through the serialize — the exact self-lock class the write path guards).
                foreach (var (lockPath, what) in new (string?, string)[]
                         { (donorFilePath, "donor plugin file"), (widenFilePath, "auto-widened defining plugin") })
                    if (lockPath is not null && PathEquals(lockPath, outPath))
                    {
                        if (created) RemoveFolderCreatedThisCall(outPath);
                        return NpcCopyOutcome.Fail(
                            $"the {what} IS the output patch — reading and rewriting the same file in one call would " +
                            "deadlock on its own open handle. Copy into a DIFFERENT patch (omit into=, or name another).");
                    }

                // ---- 5. build + serialize (core — Duplicate/RemapLinks, mode lane, multi-master write) ----
                var outcome = NpcAppearanceCopy.BuildAndWrite(
                    donorNpc, donorMods, closure,
                    clone, newEditorid, newName,
                    targetFk, targetActiveBody,
                    active,
                    outPath, extend,
                    pf => { session.ReleaseOverlay(pf); return session.AllMastersExcept(pf); },   // active-patch self-lock fix
                    donorReadFrom, outOfLoadOrder);
                if (!outcome.Success)
                {
                    if (created) RemoveFolderCreatedThisCall(outPath);   // hunt F4: a refused copy leaves no orphan
                    return outcome;
                }

                // ---- 6. asset carry (facegen rename + donor-only textures/meshes) — best-effort + reported (Q3).
                //         Paths were harvested from the IN-PATCH duplicates pre-serialize (never the donor overlay,
                //         which may be released by now). Donor-disk direct lanes exist for BOTH donor-side files —
                //         the named file's folder AND the auto-widened defining plugin's (review finding: a widened
                //         copy's facegen lives in the DEFINING mod's folder, not the override patch's) — but only
                //         under an MO2 mod folder, NEVER the game Data folder, where every vanilla BSA would
                //         misclassify as "the donor's" (review finding). ----
                NpcAssetOutcome assets;
                try
                {
                    AssetResolver assetResolver;
                    lock (_gate) { assetResolver = Assets; }
                    var assetView = assetResolver.Capture();
                    var donorDisks = new List<NpcAppearanceAssets.DonorDisk>();
                    var donorFolderNames = new List<string>();
                    foreach (var sidePath in new[] { donorFilePath, widenFilePath })
                    {
                        if (sidePath is null) continue;
                        var sideDir = Path.GetDirectoryName(sidePath);
                        if (sideDir is not null && !string.IsNullOrEmpty(dataDirForAssets) && PathEquals(sideDir, dataDirForAssets)) continue;
                        if (sideDir is not null && donorDisks.Any(d => PathEquals(sideDir, d.Folder))) continue;   // named + defining in one folder = one scan
                        var disk = NpcAppearanceAssets.DonorDisk.For(sidePath);
                        donorDisks.Add(disk);
                        donorFolderNames.Add(Path.GetFileName(disk.Folder));
                    }
                    assets = NpcAppearanceAssets.CarryAll(
                        donorFk, outcome.NewNpcKey, outcome.HarvestedAssetPaths,
                        assetView, donorDisks, donorFolderNames, Path.GetDirectoryName(outPath)!);
                }
                catch (Exception ex)
                {
                    assets = new NpcAssetOutcome(Array.Empty<CarriedAsset>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                        new[] { $"asset carry skipped — the asset layer could not be built ({ex.Message}); carry the facegen pair with housecarl_place_asset and verify in-game." }, false, false);
                }
                return outcome with { Assets = assets };
            }
            finally { (donorOverlay as IDisposable)?.Dispose(); (widenOverlay as IDisposable)?.Dispose(); }
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
        string? patchName, string? into, bool fullReadback = false, string? parent = null, string? collection = null, string? grid = null,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        var problems = new List<string>();
        var spec = BuildCreateSpec(recordType, editorid, operations, parent, collection, grid, where: null, problems);
        if (spec is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) creating the record; NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(new[] { spec }, patchName, into, fullReadback, target, inPlace, acknowledge);
    }

    /// <summary>Create MANY new records in ONE patch (housecarl_bulk_create) — the batch sibling of
    /// <see cref="CreateRecords"/>, and the one-shot lever for a nested unit (a dialogue topic + its lines, a cell + its
    /// placed refs) where a child's <c>parent</c> names a same-call sibling by editorid. Each spec is mapped exactly as
    /// the single create (type resolution + field-op mapping + parent/collection); ALL-OR-NOTHING (Q3) — any malformed
    /// spec refuses the whole call (with per-record reasons) and the core <see cref="WritePatchBuilder.CreateRecords"/>
    /// likewise refuses the whole batch on any creatability/parent problem. One serialize for the lot.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback = false,
        string? target = null, bool inPlace = false, bool acknowledge = false)
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
        return CommitCreate(specs, patchName, into, fullReadback, target, inPlace, acknowledge);
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
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        // In-place is the explicit, named-file opt-in (the SECOND write lane — create into an existing plugin, incl. one
        // houseCARL didn't author, instead of writing a new patch). Validate the contract up front (Q3): it REQUIRES a
        // target=, is mutually exclusive with into= (which EXTENDS a houseCARL patch — a different lane), and target=
        // without in_place is a no-op the caller likely didn't mean — name it rather than silently ignore it. (Mirrors
        // ApplyEdits' in-place contract exactly.)
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to create into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place creates into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to create into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        lock (_writeGate)                                                 // hunt F2: one write at a time, resolve→commit
        {
            var resolver = Resolver;
            var rulebook = Rulebook;

            if (inPlace)
                return CommitCreateInPlace(resolver, rulebook, specs, target!.Trim(), acknowledge);

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

    /// <summary>The in-place branch of <see cref="CommitCreate"/> (in-place write lane, Wave 1b) — the create-side
    /// companion of <see cref="ApplyEditsInPlace"/>, and it REUSES every Wave-1 in-place seam: the same foreign-target
    /// resolver (<see cref="ResolveActivePluginPath"/>), the same PERSISTENT first-touch CONSENT handshake (keyed off the
    /// resolved path in <see cref="UserConfigStore"/>), the same writable-parent pre-flight, and the same distinct
    /// <c>editedInPlace=</c> marker (NEVER <c>generated=true</c> — the user mod keeps failing
    /// <see cref="IsHouseCarlOwned"/> so a later into= can't blind-overwrite it). THREE divergences from
    /// <see cref="ApplyEditsInPlace"/>: (1) it drives <see cref="WritePatchBuilder.CreateRecordsInPlace"/>
    /// (allocate-into-target, not edit-own-record); (2) it returns a <see cref="WritePatchBuilder.CreateOutcome"/>; and
    /// (3) because in-place create has FULL parity — it can author dialogue lines / cells under any parent, unlike
    /// <c>set_field</c> — it runs the SAME post-write voice/result-script/cell-shell coverage teeth the patch-lane create
    /// runs (<see cref="ApplyEditsInPlace"/> is marker-only). The created-record verify is forced ON (the model-C
    /// substitute for the dropped whole-plugin floor). <paramref name="acknowledge"/> waives the CONSENT axis ONLY — the
    /// verify is a corruption-axis fact no acknowledgement overrides. Runs under <c>_writeGate</c> (the caller holds it).</summary>
    WritePatchBuilder.CreateOutcome CommitCreateInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.CreateSpec> specs,
        string target, bool acknowledge)
    {
        // (1) Resolve target -> real on-disk path via the load order (by plugin FILENAME). Refuse loud if it isn't a real
        //     active plugin (closes the coincidental-folder collision the into= lane can hit). Same resolver as the edit lane.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.");

        // (2) CONSENT axis — the persistent, server-enforced first-touch handshake, keyed off the resolved path (shared with
        //     the edit lane: acknowledging a plugin once covers BOTH editing and creating into it in place — it's the same
        //     "touch your original" trade-off).
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath));
        string? ackNote = null;
        if (!already && acknowledge)
        {
            var (ok, err) = _store.RecordInPlaceAcknowledged(targetPath);
            if (!ok) ackNote = $"the in-place acknowledgement could not be saved ({err}) — the create proceeded, but a future session will re-prompt for this plugin.";
        }

        // (3) Writable, same-volume parent pre-flight — refuse rather than degrade (the swap stages a sibling temp here).
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why);

        // (4) The write — created-record verify forced ON (the model-C substitute for the dropped whole-plugin floor).
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true);

        // (5) On success: run the SAME post-write teeth the patch lane runs (the service owns the live AssetResolver) —
        //     in-place create can now author dialogue lines / cells under any parent, so voice (.fuz) + result-script +
        //     cell-shell coverage must be checked here too (each a no-op unless that record kind was created; none can
        //     fail the write, which already succeeded). Then stamp the distinct audit marker (best-effort; a marker miss
        //     never fails the done create, Q3-noted).
        if (outcome.Success)
        {
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var note = JoinNotes(ackNote, markerNote);
            return note is not null ? enriched with { Note = note } : enriched;
        }
        if (ackNote is not null)
            // The acknowledgement was recorded but the write then failed — keep the (now-honest) note alongside the error.
            return outcome with { Note = ackNote };
        return outcome;
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
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        if (string.Equals(op.Verb, "CopyFrom", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            error = $"{where}: CopyFrom / from_plugin copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
            return null;
        }

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
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
        var specs = MapComposes(op, where, spec, out error);
        if (error is not null) return null;

        var verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb;
        var fromPlugin = MapFromPlugin(op, verb, $"{where} ({op.Formid})", spec, specs, out error);
        if (error is not null) return null;

        return new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = path, Verb = verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
            FromPlugin = fromPlugin,
        };
    }

    /// <summary>P8b — validate + extract from_plugin for a CopyFrom op. from_plugin is REQUIRED with (and ONLY with)
    /// verb=CopyFrom, which copies the field FROM that plugin's version and so takes no value/values/entries/compose/
    /// composes. Both rules refuse loud (Q3), never silently ignore. Returns null for a non-CopyFrom op.</summary>
    static string? MapFromPlugin(BulkOp op, string verb, string where, StructSpec? spec, IReadOnlyList<StructSpec>? specs, out string? error)
    {
        error = null;
        if (!string.Equals(verb, "CopyFrom", StringComparison.Ordinal))   // match the engine's Ordinal verb compare — a mis-cased verb fails loud uniformly (Unknown verb … Legal: …CopyFrom)
        {
            if (!string.IsNullOrWhiteSpace(op.FromPlugin))
                error = $"{where}: from_plugin is only valid with verb=CopyFrom (got verb={verb}).";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            error = $"{where}: CopyFrom requires from_plugin — the plugin whose version of this record to copy field_path from.";
            return null;
        }
        if (op.Value is not null || op.Values is not null || op.Entries is not null || spec is not null || specs is not null)
        {
            error = $"{where}: CopyFrom copies the field from from_plugin's version — it takes no value/values/entries/compose/composes.";
            return null;
        }
        return op.FromPlugin.Trim();
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

    /// <summary>P8a — map a wire op's composes[] (MANY build-from-parts list elements) to core StructSpecs. Mutually
    /// exclusive with the singular compose (both set → refused loud, Q3, never silently merged). Each element maps via
    /// the SAME <see cref="MapStruct"/> the singular compose uses, so a composes element can never be shaped differently
    /// from a compose element; a bad element names itself (composes[i]). Returns null when no composes= is present; an
    /// explicitly EMPTY composes=[] is a named caller mistake, not a silent no-op.</summary>
    static List<StructSpec>? MapComposes(BulkOp op, string where, StructSpec? singular, out string? error)
    {
        error = null;
        if (op.Composes is null) return null;
        if (singular is not null)
        {
            error = $"{where}: pass compose= (one element) OR composes= (many), not both.";
            return null;
        }
        if (op.Composes.Length == 0)
        {
            // An EMPTY composes=[] is the CLEAR intent for a ReplaceAll (empty the modeled list — the modeled twin of
            // ReplaceAll values=[], which already clears a coercible list); for any other verb an empty batch is a
            // caller mistake worth naming.
            if (!string.Equals(op.Verb, "ReplaceAll", StringComparison.Ordinal))
            {
                error = $"{where}: composes=[] is empty — supply one or more element specs (or compose= for one); an empty composes= is only meaningful with verb=ReplaceAll, to CLEAR the list.";
                return null;
            }
            return new List<StructSpec>();   // ReplaceAll composes=[] → clear the modeled list
        }
        var specs = new List<StructSpec>(op.Composes.Length);
        for (int j = 0; j < op.Composes.Length; j++)
        {
            var s = MapStruct(op.Composes[j], $"{where} composes[{j}]", out error);
            if (error is not null) return null;
            specs.Add(s!);
        }
        return specs;
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
    string ResolveOutputPath(string? patchName, string? into, out bool extend, out bool createdFolder, bool create = true)
    {
        lock (_gate)
        {
            createdFolder = false;
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                extend = true;
                // The .esp write lane shares the 4-step EXTEND resolver with the rider/asset lane (HCBR-2026-06-23) so
                // "extend my renamed patch" behaves IDENTICALLY across records, scripts, BSAs, and assets. needEsp:true —
                // the canonical fast path only short-circuits a folder that actually holds <stem>.esp; we then pick the .esp
                // to extend inside the resolved folder: the <stem>.esp it holds, or — via the by-folder catch-all, where the
                // folder & plugin names differ — the folder's single plugin (Q3-refuse if it holds none or several).
                var folder = ResolveOwnedPatchFolder(into, needEsp: true);
                var direct = Path.Combine(folder, PatchStem(into) + ".esp");
                if (File.Exists(direct)) return direct;
                var sole = SoleEspInFolder(folder, out var why);
                if (sole is not null) return sole;
                throw new InvalidOperationException($"cannot extend: houseCARL folder '{Path.GetFileName(folder)}' {why}.");
            }

            extend = false;
            var baseStem = PatchStem(string.IsNullOrWhiteSpace(patchName) ? "Patch" : patchName!);
            var freeStem = UniqueStem(baseStem);
            var newFolder = Path.Combine(_modsDir, ModFolderName(freeStem));
            var plugin = freeStem + ".esp";
            // #225 dry run (create:false): resolve the WOULD-BE path only — no folder, no meta.ini; the disk stays
            // exactly as it was. The real write re-resolves and creates as before.
            if (create)
            {
                Directory.CreateDirectory(newFolder);
                createdFolder = true;
                WriteOwnerMeta(newFolder, plugin);
            }
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
                // The SAME shared 4-step EXTEND resolver as the .esp write path (HCBR-2026-06-23): a renamed houseCARL patch
                // folder is found by the .esp it holds OR by its new name, so compile / decompile / bsa_repack / place_asset
                // into= behaves exactly like record into= (before this, a renamed folder fell to a misleading "folder not
                // found"). needEsp:false — a rider targets the FOLDER itself (it writes scripts / a .bsa / loose files into
                // it, not an .esp), so it does NOT require a <stem>.esp to be present.
                var folder = ResolveOwnedPatchFolder(into, needEsp: false);
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
    /// <see cref="PatchStem"/>. Aliases the one shared home (<see cref="HousecarlCore.PluginFile.Extensions"/>) so this
    /// and the load-order reader / name-suggester copies can't diverge.</summary>
    static readonly string[] PluginExts = PluginFile.Extensions;

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
        return string.IsNullOrEmpty(name) ? "Patch" : name;
    }

    /// <summary>The given stem if it's free, else the first free "<c>&lt;stem&gt;_NNN</c>". "Free" means BOTH: no mod
    /// folder "<c>houseCARL - &lt;stem&gt;</c>" already exists (houseCARL's own OR a user's), AND no plugin
    /// "<c>&lt;stem&gt;.esp</c>" is already in the active load order. The load-order arm stops a GENERIC default stem
    /// (e.g. "Patch") from emitting a "Patch.esp" that DUPLICATES a foreign active plugin — the engine forbids two active
    /// plugins sharing a basename, and mod-folder uniqueness alone never sees a same-named plugin that lives in another
    /// mod (PR #192 review). into= remains the way to grow an existing houseCARL patch; this is only the fresh path.</summary>
    string UniqueStem(string stem)
    {
        var active = ActivePluginBasenames();
        if (IsStemFree(stem, active)) return stem;
        for (int i = 1; i < 10000; i++)
        {
            var cand = $"{stem}_{i:D3}";
            if (IsStemFree(cand, active)) return cand;
        }
        throw new InvalidOperationException($"too many patches named '{stem}' under ModsDir — clean some out.");
    }

    /// <summary>A stem is free to claim when no houseCARL mod folder for it exists AND its plugin "<c>&lt;stem&gt;.esp</c>"
    /// isn't already an active load-order plugin (case-insensitive — Skyrim plugin basenames are).</summary>
    bool IsStemFree(string stem, IReadOnlySet<string> activePlugins)
        => !Directory.Exists(Path.Combine(_modsDir, ModFolderName(stem))) && !activePlugins.Contains(stem + ".esp");

    /// <summary>The active load order's plugin filenames (case-insensitive) for the UniqueStem collision arm. Read from
    /// the already-built resolver if present, else the SAME cheap composition it builds from — deliberately NOT via the
    /// <see cref="Resolver"/> getter, which REFUSES a zero-plugin instance (a legitimate minimal write). BEST-EFFORT: any
    /// read failure (or no active plugins) yields an EMPTY set — folder-only uniqueness, exactly the behaviour before the
    /// load-order arm. So the collision check is a pure safety net; it never turns a previously-valid write into a failure
    /// (Q3 degrade — the write itself, if it needs the load order, still surfaces a genuine problem through its own read).</summary>
    IReadOnlySet<string> ActivePluginBasenames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            IReadOnlyList<string>? names = _resolver?.PluginNames;
            if (names is null)
                names = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir)
                    .OrderedPaths.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToList()!;
            foreach (var n in names) set.Add(n);
        }
        catch { /* unreadable / empty load order → folder-only uniqueness (the pre-guard behaviour) */ }
        return set;
    }

    /// <summary>The 4-step <c>into=</c> EXTEND resolver, SHARED by the .esp write path (<see cref="ResolveOutputPath"/>)
    /// and the rider/asset path (<see cref="ResolvePatchModFolder"/>) so "extend my renamed patch" behaves IDENTICALLY
    /// across records, scripts, BSAs, and assets (HCBR-2026-06-23 — the record lane was fixed first; this closes the
    /// sibling). Resolves <paramref name="into"/> to the houseCARL-OWNED mod FOLDER it names: (1) the canonical
    /// "houseCARL - &lt;stem&gt;" fast path; (2) by the &lt;stem&gt;.esp it HOLDS (the folder was renamed — the .esp basename
    /// is fixed by SPID/CSF/masters); (3) by the folder's OWN name (the catch-all; folder &amp; plugin names need not match);
    /// (4) loud Q3 refusals naming every place searched, a FOREIGN (un-owned) collision distinguished from a genuine miss.
    /// Ownership-gated at every step — a plugin houseCARL didn't make stays refused (no foreign-plugin door; that's the
    /// separate in-place lane). <paramref name="needEsp"/> tightens the canonical fast path for the RECORD lane (the folder
    /// must actually hold &lt;stem&gt;.esp to short-circuit, else fall through); the rider lane targets the folder itself, so
    /// it short-circuits on folder-exists+owned. Caller holds <see cref="_gate"/>.</summary>
    string ResolveOwnedPatchFolder(string into, bool needEsp)
    {
        var stem = PatchStem(into);                             // strips a trailing .esp/.esm/.esl; no directory parts (can't escape ModsDir)
        var espName = stem + ".esp";

        // (1) CANONICAL fast path — "houseCARL - <stem>" still owns the patch (common case; no scan). The record lane also
        //     requires it to HOLD <stem>.esp (needEsp); the rider lane only needs the owned folder.
        var canonical = Path.Combine(_modsDir, ModFolderName(stem));
        if (Directory.Exists(canonical) && IsHouseCarlOwned(canonical) && (!needEsp || File.Exists(Path.Combine(canonical, espName))))
            return canonical;

        // (2) by PLUGIN name — the owned folder HOLDING <stem>.esp, whatever it's now called (the renamed-folder case).
        var byEsp = OwnedFoldersHolding(espName)
            .Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (byEsp.Count == 1) return byEsp[0];
        if (byEsp.Count > 1)
            throw new InvalidOperationException(
                $"cannot extend: {byEsp.Count} houseCARL folders carry '{espName}' — ambiguous, refusing to guess. " +
                "Pass the CONTAINING mod-folder name as into= to pick one (folder & plugin names need not match): " +
                string.Join("  |  ", byEsp.Select(d => $"into=\"{Path.GetFileName(d)}\"")) + ".");

        // (3) FOLDER catch-all — into= NAMES the mod folder itself (the same-named-plugin disambiguator, and the way to
        //     point at a renamed folder by its new name).
        var named = ResolveOwnedFolderByName(into);
        if (named is not null) return named;

        // (4) Nothing matched. Distinguish a FOREIGN (un-owned) name collision — refused for the same reason as ever
        //     (originals untouched, Q3) — from a genuine miss, NAMING every place searched (the report asked the refusal
        //     to reveal all the required pieces at once, not one half per failed call).
        var bareName = Path.GetFileName(into.Trim());
        foreach (var cand in new[] { ModFolderName(stem), bareName })
        {
            var candPath = string.IsNullOrEmpty(cand) ? null : Path.Combine(_modsDir, cand);
            if (candPath is not null && Directory.Exists(candPath) && !IsHouseCarlOwned(candPath))
                throw new InvalidOperationException(
                    $"cannot extend: mod folder '{cand}' exists but was NOT created by houseCARL (no marker) — " +
                    "refusing to write into a folder houseCARL doesn't own (originals untouched, Q3). Use a different patch name.");
        }
        throw new InvalidOperationException(
            $"cannot extend: no houseCARL plugin '{espName}' in any houseCARL folder, and no houseCARL folder named " +
            $"'{ModFolderName(stem)}'" +
            (string.Equals(bareName, ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? "" : $" or '{bareName}'") +
            ". Omit into= to create it fresh, or check the name.");
    }

    /// <summary>houseCARL-OWNED mod folders under ModsDir holding a plugin file named <paramref name="espFileName"/> at
    /// their root — the decoupled resolver behind <c>into=</c>. The .esp basename is FIXED (SPID <c>_DISTR</c>, the CSF
    /// JSON, and masters all bind the patch by its filename), while the MO2 mod-FOLDER name is the user's to rename for
    /// organization; so an extend finds the patch by the plugin it holds, not by the folder's current name. Ownership-gated
    /// (the marker) so a user mod that merely shares the basename is NEVER returned (originals untouched, Q3). Full .esp paths.</summary>
    List<string> OwnedFoldersHolding(string espFileName)
    {
        var hits = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_modsDir))
        {
            var esp = Path.Combine(dir, espFileName);
            if (File.Exists(esp) && IsHouseCarlOwned(dir)) hits.Add(esp);
        }
        return hits;
    }

    /// <summary>A houseCARL-OWNED mod folder named exactly <paramref name="rawName"/> or "<c>houseCARL - &lt;rawName&gt;</c>"
    /// — the FOLDER catch-all behind <c>into=</c>: the user NAMES the containing mod folder when the plugin basename is
    /// ambiguous (or to point at a renamed folder by its new name), and the folder name need NOT match the .esp inside.
    /// Bare name only (no directory parts — can't escape ModsDir). Null when no such folder is houseCARL-owned.</summary>
    string? ResolveOwnedFolderByName(string rawName)
    {
        var bare = Path.GetFileName(rawName.Trim());
        foreach (var cand in new[] { bare, ModFolderName(PatchStem(rawName)) })
        {
            if (string.IsNullOrEmpty(cand)) continue;
            var folder = Path.Combine(_modsDir, cand);
            if (Directory.Exists(folder) && IsHouseCarlOwned(folder)) return folder;
        }
        return null;
    }

    /// <summary>The single top-level plugin (.esp/.esm/.esl) in a houseCARL folder, so <c>into=</c> a folder NAME can edit
    /// "the plugin in this folder" without re-stating its basename. Null + a named <paramref name="reason"/> when the folder
    /// holds none or more than one (Q3 — never guess which of several to extend).</summary>
    static string? SoleEspInFolder(string folder, out string reason)
    {
        var plugins = Directory.EnumerateFiles(folder)
            .Where(f => PluginExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (plugins.Count == 1) { reason = ""; return plugins[0]; }
        reason = plugins.Count == 0
            ? "holds no plugin (.esp/.esm/.esl) to extend"
            : $"holds {plugins.Count} plugins ({string.Join(", ", plugins.Select(Path.GetFileName))}) — name the one to extend by passing its filename as into=";
        return null;
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
                inMarker = line.Equals(HousecarlOwnerMeta.Section, StringComparison.OrdinalIgnoreCase);
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
            HousecarlOwnerMeta.Section + "\r\n" +
            "generated=true\r\n" +
            $"plugin={plugin}\r\n" +
            $"created={DateTime.UtcNow:o}\r\n" +
            "\r\n" +
            "[installedFiles]\r\n" +
            "size=0\r\n";
        File.WriteAllText(Path.Combine(folder, "meta.ini"), content);
    }

    // ---- housecarl_read_plugin_file : a RAW, out-of-load-order read of ONE plugin FILE (active or not) ----

    /// <summary>Read ONE plugin file directly off disk — INCLUDING a plugin DISABLED in MO2 — returning THAT FILE's
    /// own version of a record (<paramref name="formid"/>), the records of a type it defines (<paramref name="type"/>),
    /// or a record-type summary (neither). The standalone-copy chain's Stage-1 enabler: it reaches a donor you're
    /// REMOVING from the active order, which the resolver (active profile only) cannot see.
    ///
    /// <para>STRUCTURAL PURITY (why a separate method, not a flag on <see cref="ResolveRead"/>): it opens its OWN
    /// overlay via <see cref="LoadOrderResolver.OpenOverlay"/>, materialises, and DISPOSES it — it never consults the
    /// resolver index and never reports a winner/conflict, so a raw-file read cannot masquerade as load-order truth
    /// (the renderer stamps every result OUT-OF-LOAD-ORDER) and no handle is held at rest (the donor is never locked).
    /// It emits FormLink fields as FormKey tokens exactly like <see cref="ResolveRead"/> — it does NOT follow links, so
    /// it needs no master files present; declared masters that are not installed are reported as an advisory (Q3).</para>
    ///
    /// <para>Read-only. Q3: a missing/ambiguous filename, a bad or absent FormID, or a record Mutagen cannot parse is
    /// NAMED, never a silent wrong answer.</para></summary>
    public PluginFileOutcome ReadPluginFile(string plugin, string? formid, string? type, string? mod,
                                            IReadOnlyList<string>? fields, int depth, string? editoridContains, int limit,
                                            bool resolveNames = false)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return PluginFileOutcome.Fail(plugin ?? "", "plugin is required — a plugin filename (e.g. 'Vivace.esp') or an absolute path to a .esp/.esm/.esl.");
        plugin = plugin.Trim();

        bool hasFormid = !string.IsNullOrWhiteSpace(formid);
        bool hasType = !string.IsNullOrWhiteSpace(type);
        if (hasFormid && hasType)
            return PluginFileOutcome.Fail(plugin, "pass EITHER formid= (read one record) OR type= (enumerate that type), not both.");

        FormKey fk = default;
        if (hasFormid)
        {
            try { fk = FormKey.Factory(formid!.Trim()); }
            catch (Exception ex) { return PluginFileOutcome.Fail(plugin, $"bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '000D62:Vivace.esp'."); }
        }

        // Derive the MO2 roots CHEAPLY (read ModOrganizer.ini; NO ~10s index build). Never touches the resolver.
        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex) { return PluginFileOutcome.Fail(plugin, ex.Message); }

        var comp = Mo2LoadOrder.ReadComposition(profileDir);           // cheap text parse (enabled + disabled folders) — reused for locate + master advisory

        // Locate the file — the shared on-disk plugin-locate contract (also the copy-npc-appearance donor lane;
        // one home so the two tools can never find different files for the same filename).
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, mod);
        if (loc.Error is not null) return PluginFileOutcome.Fail(plugin, loc.Error);
        if (loc.Ambiguous is not null) return PluginFileOutcome.AmbiguousHits(plugin, loc.Ambiguous);
        string path = loc.Path!, where = loc.Where; bool enabled = loc.Enabled; string? whyNotActive = loc.WhyNotActive;

        // Open OUR OWN overlay (OpenOverlay wires localized-string resolution), read, then DISPOSE — zero handles at rest.
        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(path, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
        catch (Exception ex) { return PluginFileOutcome.Fail(plugin, $"could not open '{path}' as a Skyrim plugin: {ex.Message}"); }
        try
        {
            var masters = ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            // Classify each declared master by whether it will actually LOAD (Q3 — the "won't load without them" warning
            // must fire exactly when it applies). Two distinct shortfalls, because their remedies differ:
            //   • missing  — installed NOWHERE in the install (install it).
            //   • inactive — installed but NOT in the active order: it lives only in a DISABLED mod, or is unchecked
            //                (enable it). A disabled mod is not active, so counting its file as "satisfied" would be the
            //                precise false-negative this split exists to prevent (PR #148 review): it suppresses the
            //                "won't load" warning exactly when it applies.
            // "Active" is read from the PROFILE (plugins.txt actives + the force-loaded implicit masters) — the same
            // active-order notion housecarl_check_errors uses — NOT mere on-disk presence, so the two tools agree.
            var missing = new List<string>();
            var inactive = new List<string>();
            foreach (var m in masters)
            {
                if (!Mo2LoadOrder.PluginFileExists(comp, modsDir, dataDir, overwriteDir, m)) { missing.Add(m); continue; }
                bool active = comp.ActivePluginNames.Contains(m)
                              || comp.ImplicitPluginNames.Any(x => x.Equals(m, StringComparison.OrdinalIgnoreCase));
                if (!active) inactive.Add(m);
            }
            var baseOut = new PluginFileOutcome
            {
                Requested = plugin, FilePath = path, Where = where, Enabled = enabled, WhyNotActive = whyNotActive,
                Masters = masters, MissingMasters = missing, InactiveMasters = inactive,
            };

            if (hasFormid)
            {
                IMajorRecordGetter? rec;
                try { rec = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk); }
                catch (Exception ex) { return baseOut with { Mode = "error", Error = $"'{Path.GetFileName(path)}' could not be fully read — a record Mutagen cannot parse before reaching {fk}: {ex.Message}" }; }
                if (rec is null)
                    return baseOut with { Mode = "error", Error = $"file '{Path.GetFileName(path)}' does not define or override {fk}. This reads the FILE's OWN records only — it does not resolve across masters or report a load-order winner; use housecarl_read_record for the winner." };
                var rf = ReadEngine.ReadFields(rec, fields, depth <= 0 ? 1 : depth);
                if (resolveNames)
                {
                    // resolve_names on a RAW file read: the FILE's link tokens are resolved against the ACTIVE order
                    // (the only identity frame houseCARL holds) — honest, and a target the active order doesn't define
                    // is marked 'unresolved', not guessed. Opt-in only, so the deliberate no-resolver cheapness of the
                    // default path is untouched; a caller asking for identities accepts the resolver build.
                    var view = Resolver.Capture();
                    using var session = Resolver.OpenSession();
                    rf = AnnotateLinks(rf, view, session, new());
                }
                return baseOut with { Mode = "read", Record = rf };
            }

            if (hasType)
            {
                IReadOnlyList<Type> types;
                try { types = ResolveTypeFilter(type!); }
                catch (ArgumentException ex) { return baseOut with { Mode = "error", Error = ex.Message }; }
                var rows = new List<PluginRecordRow>();
                int total = 0; bool capped = false;
                try
                {
                    foreach (var rec in ov.EnumerateMajorRecords())
                    {
                        if (!types.Any(t => t.IsInstanceOfType(rec))) continue;
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (rec.EditorID is null || rec.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                        total++;
                        if (rows.Count < limit) rows.Add(new PluginRecordRow(rec.FormKey.ToString(), RecordNaming.StripOverlay(rec.GetType().Name), rec.EditorID));
                        else capped = true;
                    }
                }
                catch (Exception ex) { return baseOut with { Mode = "error", Error = $"'{Path.GetFileName(path)}' could not be fully enumerated — a record Mutagen cannot parse: {ex.Message}" }; }
                return baseOut with { Mode = "enumerate", Rows = rows, RowTotal = total, Capped = capped };
            }

            // neither → a record-type summary of what the file defines/overrides (donor discovery).
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int recTotal = 0;
            try
            {
                foreach (var rec in ov.EnumerateMajorRecords())
                {
                    recTotal++;
                    var tn = RecordNaming.StripOverlay(rec.GetType().Name);
                    counts[tn] = counts.TryGetValue(tn, out var c) ? c + 1 : 1;
                }
            }
            catch (Exception ex) { return baseOut with { Mode = "error", Error = $"'{Path.GetFileName(path)}' could not be fully enumerated — a record Mutagen cannot parse: {ex.Message}" }; }
            var typeCounts = counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                                   .Select(kv => new PluginTypeCount(kv.Key, kv.Value)).ToList();
            return baseOut with { Mode = "summary", TypeCounts = typeCounts, RecordTotal = recTotal };
        }
        catch (Exception ex) { return PluginFileOutcome.Fail(plugin, $"failed reading '{path}': {ex.Message}"); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Is a located file the copy the MO2 install SERVES for its filename — and if not, WHY not? One half of
    /// "does the game load this file"; the other is <see cref="TickStanding"/>. The two are INDEPENDENT: a copy can be
    /// both shadowed and unticked, and each has its own remedy, so collapsing them to one cause would always drop one
    /// (#271). NotAnInstallCopy is deliberately the zero value: a default-constructed result must read NOT-loaded, never
    /// accidentally loaded.</summary>
    internal enum ServedStanding
    {
        /// <summary>The path is outside every install root, or no MO2 layer provides this exact file (a backup, an
        /// arbitrary path, or a copy reached through a junction the string compare can't match).</summary>
        NotAnInstallCopy = 0,
        /// <summary>THIS file is the copy the install serves — the first hit from an enabled layer.</summary>
        Serves,
        /// <summary>This copy's own layer is enabled, but a HIGHER-priority layer provides the same filename, so the
        /// game loads that one instead. Remedy: raise this mod's priority, or address the copy that wins.</summary>
        Shadowed,
        /// <summary>This copy sits in a mod folder MO2 knows about and has switched OFF. Remedy: switch it on, re-sort.</summary>
        ModDisabled,
        /// <summary>This copy sits in a folder modlist.txt does not mention at all — MO2 has not registered it (the state
        /// of a patch houseCARL just wrote, before the refresh). Remedy: refresh MO2. DISTINCT from
        /// <see cref="ModDisabled"/> because "switch the mod on" is not an available action here — there is nothing in
        /// MO2's list to switch (review of PR #274, round 2).</summary>
        ModUnregisteredLayer,
    }

    /// <summary>Is a plugin FILENAME ticked to load — the other half of "does the game load this file". A plugin's tick
    /// state is a DIFFERENT fact from its mod folder's switch (MO2's right pane vs its left), which is the confusion
    /// this split exists to end. Unregistered is the zero value for the same conservative-default reason as
    /// <see cref="ServedStanding"/>.</summary>
    internal enum TickStanding
    {
        /// <summary>plugins.txt and loadorder.txt do not mention this filename at all — MO2 has not registered it.</summary>
        Unregistered = 0,
        /// <summary>`*`-prefixed in plugins.txt — checked.</summary>
        Ticked,
        /// <summary>A base-game/CC master: force-loaded and never listed in plugins.txt, so absence there means loaded,
        /// not unloaded.</summary>
        Implicit,
        /// <summary>Listed in plugins.txt WITHOUT the `*` — present but unchecked. The game does not load it.</summary>
        Unticked,
    }

    /// <summary>One located plugin file, or why not. Exactly one of Path / Ambiguous / Error is set.
    /// <para>The two standings are carried SEPARATELY rather than pre-collapsed into one boolean, so a renderer can
    /// EXPLAIN rather than merely classify (#271): "NOT active" names the state but not the cause, and the causes —
    /// unticked, mod switched off, shadowed, unregistered — have different remedies. <see cref="Enabled"/> keeps the
    /// single "the game loads this file" boolean every existing consumer reads, now derived rather than stored.</para></summary>
    /// <param name="CauseDetail">For <see cref="ServedStanding.Shadowed"/>, the WHERE-label of the copy that IS served
    /// (a different copy, so it never collides with <paramref name="Where"/>). For the two layer-off standings, the mod
    /// FOLDER NAME alone — never the full hit label, whose text varies per lane and carries its own remedy, which is
    /// what made the composed sentence say the same thing twice (review of PR #274).</param>
    /// <param name="WhereNamesLayer">Does <paramref name="Where"/> already identify WHICH layer holds this copy? Set by
    /// each lane from what it knows — the filename lane's Where IS the hit's label and the mod= lane's names the mod,
    /// while the direct-path lane's is the constant "direct path" and identifies nothing. Carried as a fact rather than
    /// re-derived by string-comparing the two labels: that comparison held for the filename lane and silently failed for
    /// mod= (whose label omits the state qualifier), so the duplication it was meant to stop survived in a lane nobody
    /// had armed. A flag the lane sets cannot drift the way a heuristic over two hand-built strings does.</param>
    internal readonly record struct PluginLocateResult(
        string? Path, string Where, ServedStanding Served, TickStanding Tick, string? CauseDetail,
        bool WhereNamesLayer,
        IReadOnlyList<PluginFileHit>? Ambiguous, string? Error)
    {
        /// <summary>The game loads THIS file: it is the served copy AND its plugin is ticked (implicit masters count —
        /// force-loaded, never listed). Both halves, or the same physical file answers differently depending on how it
        /// was addressed.</summary>
        public bool Enabled => Served == ServedStanding.Serves && Tick is TickStanding.Ticked or TickStanding.Implicit;

        /// <summary>WHY the game does not load this file — null when <see cref="Enabled"/>, and also when no file was
        /// located at all (the error and ambiguous results, which no renderer of this state reaches). Composed HERE, once,
        /// so the three renderers that state this (read_plugin_file's header, diff_record's off-order pole,
        /// copy_npc_appearance's donor line) cannot drift apart on the wording; the shared locate contract exists
        /// because an inline copy had already diverged on this very flag. Both clauses are emitted when both apply —
        /// a shadowed copy of an unticked plugin needs two fixes, and naming one would send the reader to do half the
        /// job. The unregistered clause is suppressed when the served half already failed: "in a DISABLED mod" explains
        /// the absence from plugins.txt, and repeating it as a second cause reads as a second problem.</summary>
        public string? WhyNotActive
        {
            get
            {
                if (Enabled || Path is null) return null;
                var name = System.IO.Path.GetFileName(Path);
                var parts = new List<string>(2);
                switch (Served)
                {
                    case ServedStanding.Shadowed:
                        // CauseDetail is always set here: JudgeServed returns Shadowed only when this copy's OWN layer
                        // is enabled, which means a served hit exists to name. That hit is a DIFFERENT copy, so naming
                        // it never duplicates Where whichever lane asked.
                        parts.Add($"this copy is SHADOWED — {CauseDetail} provides the copy the game loads");
                        break;
                    // The two layer-off standings name the folder ONLY when Where does not (WhereNamesLayer), and state
                    // the layer's condition + remedy in words rather than echoing a label — the label is what got
                    // printed twice. Their remedies are genuinely different, which is why they are separate standings:
                    // an unregistered folder has nothing in MO2's list to switch on.
                    case ServedStanding.ModDisabled:
                        parts.Add(WhereNamesLayer
                            ? "that mod folder is switched OFF in MO2 — switch it on, then re-sort"
                            : $"it is provided by mod '{CauseDetail}', which is switched OFF in MO2 — switch it on, then re-sort");
                        break;
                    case ServedStanding.ModUnregisteredLayer:
                        parts.Add(WhereNamesLayer
                            ? "MO2 has not registered that mod folder — refresh MO2, then tick the plugin and sort"
                            : $"it is provided by mod '{CauseDetail}', which MO2 has not registered — refresh MO2, then tick the plugin and sort");
                        break;
                    case ServedStanding.NotAnInstallCopy:
                        // States what was CHECKED, not a verdict on the file. This arm is also reached when the path
                        // string-compares miss (a junction, a subst drive, a UNC route to the same install), where "not
                        // a copy the install provides" would be a confident sentence that is simply false — the class
                        // of overclaim this whole change exists to delete (review of PR #274, round 2).
                        parts.Add("no MO2 layer was found providing this exact path");
                        break;
                }
                if (Tick == TickStanding.Unticked)
                    parts.Add($"'{name}' is UNTICKED in plugins.txt (MO2's right pane)");
                else if (Tick == TickStanding.Unregistered && Served == ServedStanding.Serves)
                    parts.Add($"'{name}' is not registered in MO2's load order (refresh MO2 to pick it up)");
                return parts.Count == 0 ? null : string.Join("; and ", parts);
            }
        }
    }

    /// <summary>Judge the SERVED half for one located file: is <paramref name="fullPath"/> the copy the install provides
    /// for its filename, and if not, which of the three distinct not-served states is it? Judged against the first hit
    /// from an ENABLED layer — precisely the rule <see cref="Mo2LoadOrder.BuildFilenameMap"/> uses to build the real
    /// order. NOT merely the first hit: <see cref="Mo2LoadOrder.LocatePlugin"/> also walks disabled and unlisted folders
    /// that the order never consults. Compared by FULL PATH — a backup and the live copy share a filename and are
    /// different files.</summary>
    static (ServedStanding Served, string? Detail) JudgeServed(
        Mo2Composition comp, IReadOnlyList<PluginFileHit> located, string fullPath)
    {
        var served = located.FirstOrDefault(h => h.Enabled);
        if (served is not null && SamePluginFile(served.Path, fullPath)) return (ServedStanding.Serves, null);
        var own = located.FirstOrDefault(h => SamePluginFile(h.Path, fullPath));
        if (own is null) return (ServedStanding.NotAnInstallCopy, null);          // outside the install, or unreachable by string compare
        // Its own layer is ON but something else serves the name ⇒ shadowed, and the useful pointer is the copy that
        // WINS, not this one.
        if (own.Enabled) return (ServedStanding.Shadowed, served?.Where);
        // Its own layer is off. WHICH kind decides the remedy, and it is read from the profile's own mod list rather
        // than by pattern-matching the hit's label text — the label is display prose that can be reworded, while
        // modlist.txt membership is the actual fact ("switched off" vs "never registered").
        var folder = Path.GetFileName(Path.GetDirectoryName(own.Path) ?? "") ?? "";
        bool listedOff = comp.DisabledMods.Any(m => m.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return (listedOff ? ServedStanding.ModDisabled : ServedStanding.ModUnregisteredLayer, folder);
    }

    /// <summary>Judge the TICK half for one plugin filename, from the profile text files. Kept beside
    /// <see cref="JudgeServed"/> so the two halves can never be computed by different rules in different lanes — the
    /// exact divergence that cost #270 four review rounds.</summary>
    static TickStanding JudgeTick(Mo2Composition comp, string fileName)
    {
        if (comp.ActivePluginNames.Contains(fileName)) return TickStanding.Ticked;
        foreach (var x in comp.ImplicitPluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Implicit;
        foreach (var x in comp.InactivePluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Unticked;
        return TickStanding.Unregistered;
    }

    /// <summary>THE on-disk plugin-locate contract, shared by read_plugin_file and the copy-npc-appearance donor
    /// lane (review finding: a second inline copy had already diverged — the mod= lane forgot the enabled flag). A
    /// direct PATH (rooted / has a separator) is used verbatim ("inspect ANY plugin file"); otherwise the argument
    /// is a FILENAME found across the whole install (enabled + disabled mod folders, overwrite, Data), with
    /// <paramref name="mod"/> narrowing a name several folders provide. Ambiguity comes back structured — each
    /// caller renders its own remedy.</summary>
    internal static PluginLocateResult LocatePluginFileOnDisk(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string plugin, string? mod)
    {
        // A plugin's TICK state is a DIFFERENT fact from its mod folder's switch: a plugin can sit in an enabled mod
        // and be unchecked in MO2's right pane, and the game then does not load it. Every lane below returns the pair
        // (served, tick) the renderers state as "active" / "NOT active — <why>", so BOTH halves are judged in every
        // lane, by the SAME two helpers (JudgeServed / JudgeTick) — a lane computing one of them its own way is the
        // divergence that cost #270 four review rounds. Implicit base/CC masters are force-loaded and never listed in
        // plugins.txt, so they count as ticked. This is the same test read_plugin_file already applies to a file's
        // declared MASTERS, now applied to the file itself, and carrying its cause (#271).
        if (LooksLikePath(plugin))
        {
            if (!File.Exists(plugin))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null, $"no file at path '{plugin}'.");
            var full = Path.GetFullPath(plugin);
            // The standing is COMPUTED for a direct path, never assumed. Addressing a file BY PATH says nothing about
            // whether the install provides it — a path can perfectly well name the live copy of an enabled plugin,
            // and a hardcoded `false` here stamped that copy "disabled" (#269). JudgeServed answers "is THIS file the
            // copy the install SERVES?" against the first ENABLED-layer hit; see its own doc for why neither the first
            // hit nor any matching hit is the right comparand. Two costs accepted: this pays the same folder sweep the
            // filename lane does (one stat per candidate folder), which is why a path no install root contains skips it
            // outright; and a path reaching the install through a junction/symlink won't string-match, so it reads as
            // NotAnInstallCopy — the pre-fix answer, conservative in the same direction rather than newly wrong in the
            // other. The tick half needs no path at all, so it is judged for EVERY direct path, junction or not.
            var fnPath = Path.GetFileName(full);
            var located = IsUnderAnyInstallRoot(full, modsDir, dataDir, overwriteDir)   // outside every root ⇒ can't be the install's copy; skip the scan
                ? Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fnPath)
                : Array.Empty<PluginFileHit>();
            var (servedStanding, detail) = JudgeServed(comp, located, full);
            // WhereNamesLayer: FALSE — "direct path" identifies no layer, so a layer-off cause must name the folder.
            return new(full, "direct path", servedStanding, JudgeTick(comp, fnPath), detail, false, null, null);
        }
        if (!string.IsNullOrWhiteSpace(mod))
        {
            var fn = Path.GetFileName(plugin);
            var cand = Path.Combine(modsDir, mod.Trim(), fn);
            if (!File.Exists(cand))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null,
                           $"mod folder '{mod.Trim()}' under ModsDir does not provide '{fn}'.");
            // Both halves here too, or the same physical file answers differently depending on how it was addressed.
            // "The named mod is enabled" is NOT enough: a lower-priority enabled mod's copy is shadowed, and the game
            // loads the serving copy instead.
            var (modServed, modDetail) = JudgeServed(
                comp, Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, fn), cand);
            // WhereNamesLayer: TRUE — "mod 'X'" names the folder (it carries no STATE qualifier, which is exactly why
            // the old label-equality test failed here and let the duplication through).
            return new(cand, $"mod '{mod.Trim()}'", modServed, JudgeTick(comp, fn), modDetail, true, null, null);
        }
        var hits = Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, plugin);
        if (hits.Count == 0)
            return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null,
                $"'{Path.GetFileName(plugin)}' is in no mod folder (enabled, disabled, or not-yet-listed in MO2), the overwrite folder, or the game Data folder. Check the filename, pass an absolute path, or (if it's an MO2 mod) the exact folder via mod=.");
        if (hits.Count > 1) return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, hits, null);
        var (oneServed, oneDetail) = JudgeServed(comp, hits, hits[0].Path);
        // WhereNamesLayer: TRUE — Where IS the located hit's own label, folder and state both.
        return new(hits[0].Path, hits[0].Where, oneServed, JudgeTick(comp, Path.GetFileName(plugin)), oneDetail, true, null, null);
    }

    /// <summary>Does the user's `plugin` argument denote a PATH (use verbatim — the "inspect any file" case) rather
    /// than a bare filename (locate in the MO2 folders)? True if rooted or carrying a directory separator: 'C:\..\X.esp'
    /// or 'mods\M\X.esp' is a path; a bare 'X.esp' is a filename.</summary>
    static bool LooksLikePath(string s) => Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/');

    /// <summary>Do two paths denote the SAME plugin file? A FULL-PATH compare (case-insensitive, as Windows paths
    /// are) — never a filename compare: an archived backup and the live copy share a name and are different files,
    /// which is exactly the distinction a provenance label gets wrong when it guesses.</summary>
    static bool SamePluginFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Is <paramref name="fullPath"/> inside any MO2/game root? Used ONLY to skip work — a file outside
    /// every root cannot be a copy the install provides — so the enabled/disabled CLASSIFICATION itself stays with
    /// the one shared locate, never re-derived here.</summary>
    static bool IsUnderAnyInstallRoot(string fullPath, params string[] roots)
    {
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* an unparseable root simply isn't a match — never a false 'inside' (Q3) */ }
        }
        return false;
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

    /// <summary>A form-SCOPE string → getter Type(s): a catalog name / signature (the TypeLookup fast path),
    /// OR a Mutagen link-interface group name ("Item", "Constructible", "NpcSpawn", …) — resolved as every
    /// corpus record getter assignable to <c>I{name}Getter</c>, DERIVED from the real interfaces, never a
    /// hand-kept list (PR #165 review finding #2: the SkyPatcher field map scopes multi-type ops by these
    /// group names, and ResolveTypeFilter alone made the whole inventory op family's EditorID values
    /// unresolvable). Null = the string names neither (the caller surfaces it loud).</summary>
    internal IReadOnlyList<Type>? ResolveFormScope(string type)
    {
        var t = type.Trim();
        if (TypeLookup.TryGetValue(t, out var types)) return types;
        var iface = typeof(SkyrimMod).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{t}Getter");
        if (iface is null) return null;
        var matches = TypeLookup.Values.SelectMany(v => v).Distinct().Where(iface.IsAssignableFrom).ToList();
        return matches.Count > 0 ? matches : null;
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
    string? PredicateNote = null, IReadOnlyList<string?>? Sources = null, string? ScanNote = null,
    IReadOnlyList<string?>? MatchedTargets = null, IReadOnlyList<GroupCount>? Groups = null,
    string? GroupBy = null, string? ScopeLabel = null, int Offset = 0,
    bool WhereWinner = false, string? WhereSourceNote = null)   // #233: WhereWinner ⇒ the match decided on the live winner; WhereSourceNote carries the type=-scope redundancy note
{
    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>One row of a cross_plugin_query <c>group_by=</c> aggregation: a group key (winner plugin / record type /
/// defining plugin) and how many matches fell in it. Emitted instead of per-match lines when group_by is set.</summary>
public sealed record GroupCount(string Key, int Count);

/// <summary>A compact, header-only record summary (no field dump) — the per-match line cross_plugin_query emits
/// by default. <see cref="Error"/> non-null ⇒ the winner couldn't be summarised (named, recoverable — Q3).</summary>
public sealed record RecordSummary(FormKey FormKey, string Type, string? EditorId, string Winner, int OverrideDepth, string? Error);

/// <summary>One enumerate/read row from a raw plugin-file read: a record the file DEFINES or OVERRIDES, as its
/// FormKey + type + EditorID. NOT a load-order winner — the FILE's own record.</summary>
public sealed record PluginRecordRow(string FormKey, string Type, string? EditorId);

/// <summary>One summary row: a record type the file touches and how many records of it the file defines/overrides.</summary>
public sealed record PluginTypeCount(string Type, int Count);

/// <summary>The outcome of a housecarl_read_plugin_file call — a RAW, OUT-OF-LOAD-ORDER read of ONE plugin file
/// (active or not), never resolved against the load order. <see cref="Error"/> non-null ⇒ the call failed with a
/// named, recoverable reason (<see cref="Mode"/> "error"). <see cref="Mode"/> "ambiguous" ⇒ the filename matched
/// several folders (<see cref="Ambiguous"/> lists them) and the caller must disambiguate. Otherwise <see cref="Mode"/>
/// is "read" (<see cref="Record"/>), "enumerate" (<see cref="Rows"/> + <see cref="RowTotal"/>/<see cref="Capped"/>),
/// or "summary" (<see cref="TypeCounts"/> + <see cref="RecordTotal"/>). <see cref="FilePath"/>/<see cref="Where"/>/
/// <see cref="Masters"/>/<see cref="MissingMasters"/> describe the file that was opened (present on success and on a
/// read-time error).</summary>
public sealed record PluginFileOutcome
{
    public string? Error { get; init; }
    public required string Requested { get; init; }
    public string Mode { get; init; } = "error";
    public string? FilePath { get; init; }
    public string? Where { get; init; }
    public bool Enabled { get; init; }
    /// <summary>WHY the game does not load this file — null when <see cref="Enabled"/> (and on the error/ambiguous
    /// outcomes, which carry no file). Composed once by the shared locate contract
    /// (<see cref="LoadOrderService.PluginLocateResult.WhyNotActive"/>) so every renderer of this state says the same
    /// thing; naming the CAUSE is what lets a reader act without re-deriving it (#271).</summary>
    public string? WhyNotActive { get; init; }
    public IReadOnlyList<string> Masters { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingMasters { get; init; } = Array.Empty<string>();     // declared but installed NOWHERE
    public IReadOnlyList<string> InactiveMasters { get; init; } = Array.Empty<string>();    // installed but NOT active (disabled/unchecked)
    public RecordFields? Record { get; init; }
    public IReadOnlyList<PluginRecordRow> Rows { get; init; } = Array.Empty<PluginRecordRow>();
    public int RowTotal { get; init; }
    public bool Capped { get; init; }
    public IReadOnlyList<PluginTypeCount> TypeCounts { get; init; } = Array.Empty<PluginTypeCount>();
    public int RecordTotal { get; init; }
    public IReadOnlyList<PluginFileHit> Ambiguous { get; init; } = Array.Empty<PluginFileHit>();

    public static PluginFileOutcome Fail(string requested, string error) => new() { Requested = requested, Error = error, Mode = "error" };
    public static PluginFileOutcome AmbiguousHits(string requested, IReadOnlyList<PluginFileHit> hits) =>
        new() { Requested = requested, Mode = "ambiguous", Ambiguous = hits };
}

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

/// <summary>The data behind housecarl_update_status: MO2's own local Nexus update cache read from meta.ini, with no
/// network. <see cref="Entries"/> is one row per Nexus-linked mod (installed vs newest version, modid, enabled state);
/// <see cref="UntrackedCount"/> is how many mod folders were skipped as not Nexus-linked (no meta.ini or no modid).
/// <see cref="Problems"/> carries any Q3 read faults (e.g. a missing mods folder), never a silent empty.</summary>
public sealed record UpdateCacheData(
    string ModsDir,
    string? InstanceDir,
    IReadOnlyList<ModUpdateEntry> Entries,
    IReadOnlyList<string> Problems,
    int UntrackedCount);

/// <summary>One Nexus-linked mod's update-cache row. <see cref="Newest"/> empty ⇒ MO2 never learned a newer version.
/// MO2's own "update available" rule: <see cref="Newest"/> is set, non-empty, != <see cref="Installed"/>, and !=
/// <see cref="Ignored"/>. <see cref="Enabled"/> is null when the mod isn't in the active profile (state unknown).
/// <see cref="LastUpdate"/> is unix-seconds of MO2's last Nexus check (staleness signal). <see cref="InstalledFileIds"/>
/// are the exact Nexus file id(s) MO2 installed (from meta.ini <c>[installedFiles]</c>) — the FILE-level currency join
/// key that makes a live check immune to the multi-file-page false positive; empty for a FOMOD/manual install.</summary>
public sealed record ModUpdateEntry(
    string Folder, bool? Enabled, int ModId, string? Installed, string? Newest, string? Ignored, string? LastUpdate,
    IReadOnlyList<int> InstalledFileIds);

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

/// <summary>One provider of an SKSE-layer file: the mod / "overwrite" / "Data" / BSA-filename, and whether it's a "loose" file or
/// a "BSA" entry. The winner-first-then-losers ordering lives in <see cref="SkseFileEntry.Providers"/>.</summary>
public sealed record SkseProvider(string Name, string Kind);

/// <summary>One file found under Data\SKSE\Plugins in the active load order (housecarl_skse_inventory). <see cref="Group"/> is the
/// immediate subfolder it sits in ("" = top level) — the derived render-grouping key. <see cref="Providers"/> is the FULL conflict
/// chain — every mod that ships this exact file, WINNER FIRST then the losers in precedence order (the same winner→loser
/// transparency the asset tools give), each tagged loose/BSA; empty ⇒ nothing active provides it. <see cref="Plugin"/> is the tier-C
/// static manifest, set ONLY for a <c>.dll</c> whose winning copy is loose (null for configs and for a BSA-only/unresolved DLL);
/// <see cref="Note"/> carries the Q3 reason when a DLL has no readable manifest / isn't loader-scoped.</summary>
public sealed record SkseFileEntry(
    string RelPath,
    string FileName,
    string Group,
    IReadOnlyList<SkseProvider> Providers,
    SksePluginReader.SksePluginInfo? Plugin,
    string? Note,
    SksePeekResult? Peek = null)
{
    /// <summary>The tier-D string peek of this DLL's image (<c>peek=true</c>), or null when not requested / not a loose
    /// DLL. Computed ONLY for entries the peek filter matched — the scan reads the whole image, so it is opt-in per-DLL
    /// by design. The IMPORT half of tier D needs no flag and lives on <see cref="SksePluginReader.SksePluginInfo.Imports"/>.</summary>
    public SksePeekResult? Peek { get; init; } = Peek;

    /// <summary>Whether this DLL entry matches a user <c>filter=</c> — the ONE predicate, shared by the renderer's
    /// filtered view and the service's peek gate. Shared on purpose: two hand-kept copies would drift, and a drift here
    /// means peeking a different DLL than the one rendered (the exact class the pairing review caught in its per-DLL
    /// line). Matches filename, winning provider, subfolder, or the declared plugin name/author, case-insensitively.</summary>
    public bool MatchesDll(string filter)
    {
        bool In(string? s) => s is not null && s.Contains(filter, StringComparison.OrdinalIgnoreCase);
        return In(FileName) || In(WinningProvider) || In(Group)
            || (Plugin?.Version is { } v && (In(v.Name) || In(v.Author)));
    }

    /// <summary>The VFS winner (first provider), or null if nothing active provides the file.</summary>
    public SkseProvider? Winner => Providers.Count > 0 ? Providers[0] : null;
    /// <summary>The winning provider's name (mod / overwrite / Data / BSA), or null.</summary>
    public string? WinningProvider => Winner?.Name;
    /// <summary>The winner's kind ("loose" | "BSA"), or "none" when unprovided.</summary>
    public string ProviderKind => Winner?.Kind ?? "none";
    /// <summary>How many mods ship this exact file — &gt; 1 is contention worth surfacing.</summary>
    public int ProviderCount => Providers.Count;
}

/// <summary>The data behind housecarl_skse_inventory: the SKSE-plugin layer of the active load order — <see cref="Dlls"/> (each a
/// plugin DLL with its winning provider + static tier-C manifest) and <see cref="Configs"/> (their .ini/.toml/.json/.yaml with the
/// winning provider), plus <see cref="OtherFileCount"/> (uncategorized files like .pdb/.txt, counted not listed). The build-level Q3
/// caveats <see cref="BsaFailures"/> / <see cref="ReadIncomplete"/> and discovery <see cref="Warnings"/> ride along; <see cref="ProfileName"/>
/// names the active profile the answer describes.</summary>
public sealed record SkseInventoryData(
    IReadOnlyList<SkseFileEntry> Dlls,
    IReadOnlyList<SkseFileEntry> Configs,
    int OtherFileCount,
    string? InstalledRuntime,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName,
    IReadOnlySet<string>? ActivePlugins = null,
    bool PeekRequested = false)
{
    /// <summary>The plugin filenames the game actually loads (active + force-loaded implicit) — resolved ONLY for a
    /// tier-D peek, which cross-checks a DLL's embedded plugin names against it. <c>null</c> ⇒ NOT RESOLVED (the
    /// profile's plugin lists were missing or unreadable), so a renderer must NOT call any embedded name "absent from
    /// the load order" (Q3: an unasked question has no answer). Never handed over EMPTY — see the producer.</summary>
    public IReadOnlySet<string>? ActivePlugins { get; init; } = ActivePlugins;

    /// <summary>Whether the caller asked for a tier-D peek. Distinct from "any entry HAS a peek": a filter can match
    /// only configs, or only BSA-only DLLs, and then the flag was honored with nothing to show — which the renderer
    /// must SAY rather than silently drop (Q3).</summary>
    public bool PeekRequested { get; init; } = PeekRequested;
}

/// <summary>The load-order verdict for one reference an SKSE config declares (housecarl_skse_config_audit, tier B).</summary>
public enum SkseRefVerdict
{
    /// <summary>Plugin in the active order, and (for a form token) the FormID resolves to a record in it.</summary>
    Ok,
    /// <summary>The named plugin is not in the active load order — the whole entry (or, for a path-segment gate, the whole file) is inert.</summary>
    PluginMissing,
    /// <summary>Plugin present, but no record with that (masked) FormID exists in it — a dead reference.</summary>
    Dangling,
    /// <summary>The token matched the reference SHAPE but couldn't be normalized (hex overflow, unusable plugin name) — flagged loud, never guessed.</summary>
    Unparseable,
}

/// <summary>One reference a config declares (<see cref="HousecarlCore.SkseConfigRef"/>) paired with its load-order
/// <see cref="Verdict"/> and a Q3 <see cref="Detail"/> line (the resolved FormKey for OK; the reason for a dead/unparseable verdict).</summary>
public sealed record SkseAuditedRef(HousecarlCore.SkseConfigRef Ref, SkseRefVerdict Verdict, string? Detail);

/// <summary>One config file's audit: its VFS provenance (winning provider + the full winner-first conflict chain — only the
/// WINNER is read, the losers are shown for transparency), every reference it declares with a verdict, and a Q3
/// <see cref="ReadError"/> when the winning copy couldn't be read/decoded or was over the size cap.</summary>
public sealed record SkseConfigFileAudit(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    int ProviderCount,
    IReadOnlyList<SkseProvider> Providers,
    IReadOnlyList<SkseAuditedRef> Refs,
    string? ReadError);

/// <summary>The data behind housecarl_skse_config_audit (tier B, #199): every SKSE-plugin config with the references it
/// declares resolved to OK / PLUGIN MISSING / DANGLING / UNPARSEABLE, plus the build-level Q3 caveats
/// (<see cref="BsaFailures"/> / <see cref="ReadIncomplete"/> / <see cref="Warnings"/>) and the active <see cref="ProfileName"/>.</summary>
public sealed record SkseConfigAuditData(
    IReadOnlyList<SkseConfigFileAudit> Files,
    int ConfigCount,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>Who implements a native class's declarations (housecarl_native_pairing_audit).</summary>
public enum NativeProvenance
{
    /// <summary>The class's provider chain includes an OFFICIAL archive — implemented by the game executable. Baseline;
    /// accounting only (this holds even when a mod's loose copy WINS the file — SKSE overrides vanilla classes).</summary>
    Engine,
    /// <summary>An skse64-scripts-payload class (StringUtil, UI, …) — implemented by the game-root skse64 loader, not
    /// anything under SKSE\Plugins. Detected structurally: an otherwise-unpaired class whose winning provider also
    /// provides an ENGINE class (the payload co-ships vanilla overrides with its new classes). Baseline.</summary>
    SkseCore,
    /// <summary>Anything else — the pairing ladder runs.</summary>
    ThirdParty,
}

/// <summary>The §4c pairing-evidence rung a THIRD-PARTY class landed on, by evidence strength.</summary>
public enum NativePairingRung
{
    /// <summary>The winning .pex's own provider mod ships ≥1 candidate DLL — the strong co-shipment signal.</summary>
    SameMod,
    /// <summary>A mod elsewhere in the .pex's conflict chain ships the DLL — the bundling case (a patch mod wins the
    /// script file; the framework mod beneath ships the implementation).</summary>
    ChainMod,
    /// <summary>No mod shipping this class's file (winner or chain) ships any candidate DLL. A VERIFY flag, never
    /// "broken" — a declaration copy of an absent framework lands here, but registration is runtime behavior (tier E).</summary>
    Unpaired,
}

/// <summary>One candidate DLL a paired mod ships: its VFS identity, the winning copy's tier-C manifest (loose winners
/// only), and <see cref="LoadBlocker"/> — the static reason it will NOT load (BSA-only / subfolder / 32-bit /
/// unreadable), null when no static check rules it out. version-LOCKED-vs-runtime is adjudicated at render time
/// against <see cref="NativePairingAuditData.InstalledRuntime"/> (it needs the game version, which may be unknown).</summary>
public sealed record NativePairedDll(
    string RelPath,
    string FileName,
    string Group,
    string? WinningProvider,
    SksePluginReader.SksePluginInfo? Info,
    string? LoadBlocker);

/// <summary>One script class declaring native functions, with its VFS provenance, its <see cref="Provenance"/> class,
/// and — for a third-party class — the pairing <see cref="Rung"/>, the paired mod, and that mod's candidate DLLs.
/// <see cref="Rung"/>/<see cref="PairedMod"/> are null for baseline (ENGINE / SKSE CORE) classes. The winner/count
/// facts are DERIVED from the one <see cref="Providers"/> list (the SkseFileEntry pattern — review finding: carried
/// copies of a derivable fact can drift). Deadness has exactly ONE owner — the renderer's Judge/BestFate, which also
/// adjudicates version-locked-vs-runtime — deliberately not a record property.</summary>
public sealed record NativeClassEntry(
    string RelPath,
    string ClassName,
    IReadOnlyList<string> NativeFunctions,
    IReadOnlyList<SkseProvider> Providers,
    NativeProvenance Provenance,
    NativePairingRung? Rung,
    string? PairedMod,
    IReadOnlyList<NativePairedDll> PairedDlls)
{
    /// <summary>How many native functions the class declares — always <see cref="NativeFunctions"/>' count.</summary>
    public int NativeCount => NativeFunctions.Count;
    /// <summary>The VFS winner's provider name (first in <see cref="Providers"/>), or null if nothing provides it.</summary>
    public string? WinningProvider => Providers.Count > 0 ? Providers[0].Name : null;
    /// <summary>The winner's kind ("loose" | "BSA"), or "none" when unprovided.</summary>
    public string ProviderKind => Providers.Count > 0 ? Providers[0].Kind : "none";
    /// <summary>How many sources ship this exact file — &gt; 1 is contention worth surfacing.</summary>
    public int ProviderCount => Providers.Count;
}

/// <summary>A .pex whose winning copy could not be parsed — a NAMED note (Q3), never a silent skip.</summary>
public sealed record NativeUnreadablePex(string RelPath, string? WinningProvider, string Reason);

/// <summary>The data behind housecarl_native_pairing_audit: every native-declaring class classified and (for third
/// parties) paired, the scan accounting (<see cref="PexScanned"/> total compiled scripts examined), the unreadable
/// notes, whether an skse64 loader is visible (<see cref="SkseLoaderSeen"/> — the SKSE-CORE sanity note; tri-state:
/// null = the check itself failed, "could not check", never rendered as a definite absence — Q3), the installed game
/// runtime when resolvable (<see cref="InstalledRuntime"/>, null = unknown → version-LOCKED findings degrade to
/// "verify"), and the build-level Q3 caveats.</summary>
public sealed record NativePairingAuditData(
    IReadOnlyList<NativeClassEntry> Classes,
    int PexScanned,
    IReadOnlyList<NativeUnreadablePex> Unreadable,
    bool? SkseLoaderSeen,
    string? InstalledRuntime,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind the whole-layer SkyPatcher scan (Wave 2): the ordered discovery scan, the
/// per-folder INI-vs-INI set collisions, the three ITM classes (intra-file dead writes, cross-INI
/// duplicates, no-op writes), and the build-level Q3 caveats.</summary>
public sealed record SkyPatcherLayerData(
    HousecarlCore.SkyPatcherDiscovery.LayerScan Scan,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherConflict> Conflicts,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherItm> Itms,
    IReadOnlyList<HousecarlCore.SkyPatcherConflicts.SkyPatcherDuplicate> Duplicates,
    IReadOnlyList<SkyPatcherNoOpWrite> NoOps,
    IReadOnlyList<string> NoOpNotes,
    bool ReadIncomplete,
    IReadOnlyList<string> AssetWarnings,
    string ProfileName);

/// <summary>One no-op write (the third ITM class — true ITM): a SET-class op that applied to the
/// record in the full replay but wrote the value the record already had at that point.
/// <see cref="Already"/> is that value (the overlay's before == after leaf token).</summary>
public sealed record SkyPatcherNoOpWrite(
    string Subfolder, string FormKey, string? EditorId, string FieldPath,
    string File, int Line, string Op, string Value, string Already);

/// <summary>The data behind the SkyPatcher post-state read (Wave 1): the record's identity + winner, one
/// <see cref="SkyPatcherFolderOutcome"/> per type folder that can patch it (RACE gets two — race/ + raceHook/),
/// the layer-level discovery notes, and the build's Q3 caveats. A not-in-order / not-patchable input is a
/// NAMED <see cref="Error"/> (Q3), never an empty result.</summary>
public sealed record SkyPatcherPostStateData(
    string? Error,
    Mutagen.Bethesda.Plugins.FormKey FormKey,
    string? EditorId,
    string RecordTypeName,
    string WinnerPlugin,
    IReadOnlyList<SkyPatcherFolderOutcome> Folders,
    IReadOnlyList<string> LayerNotes,
    bool ReadIncomplete,
    IReadOnlyList<string> AssetWarnings,
    string ProfileName)
{
    public static SkyPatcherPostStateData Fail(Mutagen.Bethesda.Plugins.FormKey fk, string error)
        => new(error, fk, null, "", "", Array.Empty<SkyPatcherFolderOutcome>(), Array.Empty<string>(), false, Array.Empty<string>(), "");
}

/// <summary>One SkyPatcher type folder's replay outcome for the record. <see cref="Result"/> is null when the
/// active order ships no (interpretable) INIs for the folder — a named nothing, not an empty guess.
/// <see cref="Enabled"/> false ⇒ SkyPatcher.ini toggles the whole folder off (its INIs exist but the DLL
/// skips them — counts are zero BY that fact, and the render must say so).</summary>
public sealed record SkyPatcherFolderOutcome(
    string Subfolder,
    int IniCount,
    int LineCount,
    HousecarlCore.SkyPatcherOverlay.SkyPatcherOverlayResult? Result,
    bool Enabled);

/// <summary>One provider of a mesh path: the mod / "overwrite" / "Data" / BSA-filename, and whether it's a "loose" file
/// or a "BSA" entry. Winner-first ordering lives in <see cref="NifInspectData.Providers"/>.</summary>
public sealed record NifProvider(string Name, string Kind);

/// <summary>The per-path data behind housecarl_nif_inspect: the VFS resolution of ONE mesh path joined to the
/// format-level <see cref="HousecarlCore.NifInspect"/> of the copy that was read. <see cref="Inspected"/> is the
/// provider whose bytes were parsed (the winner, or the <c>mod=</c>-named copy); <see cref="Providers"/> is the FULL
/// winner→loser chain (asset-tool parity), <see cref="Ambiguous"/> flags file-layer contention. <see cref="Absent"/>
/// marks the no-provider outcome specifically, so the renderer can hedge THAT error at point of use on the
/// batch-level scan caveats (an ABSENT is only authoritative when the scan was complete — asset_status parity).
/// Exactly one of <see cref="Inspect"/> (the mesh model) and <see cref="Error"/> (ABSENT / bad path / unreadable /
/// parse-refused — all named, Q3) is set on any given result. The batch-level Q3 caveats (BSA failures, discovery
/// warnings, profile) live on <see cref="NifInspectBatchData"/> — captured once for the whole batch.</summary>
public sealed record NifInspectData(
    string RelPath,
    NifProvider? Inspected,
    IReadOnlyList<NifProvider> Providers,
    bool Ambiguous,
    bool Absent,
    HousecarlCore.NifInspect? Inspect,
    string? Error)
{
    public static NifInspectData Fail(string relPath, string error)
        => new(relPath, null, Array.Empty<NifProvider>(), false, false, null, error);
}

/// <summary>The batch behind housecarl_nif_inspect: per-path <see cref="Results"/> in INPUT ORDER, plus the
/// build-level Q3 caveats shared by the whole batch (one asset capture pins every path): <see cref="BsaFailures"/>
/// (archives that couldn't be read this build — an ABSENT result may be incomplete), discovery
/// <see cref="Warnings"/>, and the active <see cref="ProfileName"/>.</summary>
public sealed record NifInspectBatchData(
    IReadOnlyList<NifInspectData> Results,
    IReadOnlyList<string> BsaFailures,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind housecarl_nif_set: the VFS resolution joined to the verified write outcome. Exactly one of
/// {<see cref="Report"/> (a verified write happened)}, {<see cref="Error"/> (a named refusal — NOTHING written, Q3)},
/// and {<see cref="NeedsAcknowledge"/> (the in-place first-touch consent prompt — a required confirmation, not an
/// error)} describes the result. <see cref="OutputModFolder"/> is set on the default-lane success (enable + sort it above
/// <see cref="CurrentWinner"/>); <see cref="InPlacePath"/> is set on the in-place success (the file overwritten in
/// place).</summary>
public sealed record NifSetResult(
    string RelPath,
    NifProvider? Edited,
    IReadOnlyList<NifProvider> Providers,
    bool Ambiguous,
    HousecarlCore.NifSetReport? Report,
    string? Error,
    bool NeedsAcknowledge,
    string? AckPrompt,
    bool InPlace,
    bool EditedIsWinner,
    string? OutputModFolder,
    string? InPlacePath,
    string? CurrentWinner,
    IReadOnlyList<string> Warnings,
    string ProfileName)
{
    public static NifSetResult Fail(string error, IReadOnlyList<NifProvider>? providers = null, string profileName = "")
        => new("", null, providers ?? Array.Empty<NifProvider>(), false, null, error, false, null, false, false, null, null, null, Array.Empty<string>(), profileName);

    public static NifSetResult NeedsAck(string prompt, NifProvider edited, IReadOnlyList<NifProvider> providers, string profileName)
        => new("", edited, providers, false, null, null, true, prompt, true, false, null, null, null, Array.Empty<string>(), profileName);

    public static NifSetResult OkNewFolder(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous,
        HousecarlCore.NifSetReport report, string modFolder, string? winner, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, false, true, modFolder, null, winner, warnings, profileName);

    public static NifSetResult OkInPlace(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous, bool editedIsWinner,
        HousecarlCore.NifSetReport report, string inPlacePath, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, true, editedIsWinner, null, inPlacePath, null, warnings, profileName);
}

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

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>
/// Owns the load-order resolver's lifecycle and is the single place the tools reach the core engines.
/// The index build is lazy (deferred to first use, so startup and tools/list are instant), refreshed by a cheap
/// mtime sweep on each query, and serialized on one gate because the server dispatches tool calls concurrently.
/// The order is the true active order, read statically from the MO2 profile's loadorder.txt + modlist.txt +
/// plugins.txt — masters first, highest-priority winner last, duplicate plugin names resolved by mod priority.
/// No USVFS and no live MO2 state: the server reads real plugin paths and runs standalone.
/// </summary>
public sealed class LoadOrderService : IDisposable
{
    // Three modes. INSTANCE (default): one MO2 instance folder, from which the roots and active profile are derived by
    // reading ModOrganizer.ini, and a profile switch is picked up on the next tool call. EXPLICIT (dev override): the
    // three paths are configured directly, _instanceDir stays null, no ini watch. UNCONFIGURED: the server still boots
    // and every tool returns the prompt for the MO2 path until housecarl_set_mo2_instance is called.
    string? _instanceDir;                          // INSTANCE-mode source of truth; null in explicit/unconfigured mode
    string _dataDir;                               // DERIVED (instance mode) or configured (explicit); mutable for a live profile switch
    string _modsDir;
    string _profileDir;
    string _profileName;                           // the active profile (instance mode: from selected_profile)
    string _overwriteDir = "";                     // MO2's overwrite layer (instance mode: derived; explicit mode: none)
    bool _configured;                              // false ⇒ tools return the trained prompt instead of resolving
    readonly UserConfigStore _store;               // the sole owner of houseCARL.user.json (MO2 instance dir + tool paths)
    readonly int _maxPlugins;
    readonly object _gate = new();
    // Serializes the whole resolve/stage/commit of every .esp write: tool calls are dispatched concurrently, and two
    // same-name writes would otherwise allocate the same folder and cross-commit through the fixed .housecarl-tmp
    // staging path. SetInstance takes it too, so an instance switch cannot tear a write in flight.
    // Lock order where both are held: _writeGate THEN _gate.
    readonly object _writeGate = new();
    LoadOrderResolver? _resolver;
    CorpusRulebook? _rulebook;
    IReadOnlyList<string> _orderWarnings = Array.Empty<string>();
    // The VFS-aware asset resolver, built lazily and only on an asset query so a pure-record session never pays for it.
    // Dropped and rebuilt whenever the active profile changes: an enabled-mod toggle changes the loose roots and the
    // active-archive set, not just the plugin order. It reads BSA file tables rather than the record index, so a full
    // rebuild on a profile change is cheap.
    AssetResolver? _assetResolver;
    IReadOnlyList<string> _assetWarnings = Array.Empty<string>();   // discovery warnings from the asset build (e.g. a Skyrim.ini we couldn't find → base BSAs unscanned)
    IReadOnlyList<ActiveArchive> _activeArchives = Array.Empty<ActiveArchive>();   // active BSAs behind the current asset build (archive → owning plugin); swapped with _assetResolver
    IReadOnlyList<string> _enabledModsAtBuild = Array.Empty<string>();             // enabled mods behind the current asset build; the loader scan walks these mods' Root folders, from the same capture as the view rather than a second profile read
    // Freshness baselines are last-seen mtimes compared by VALUE (!=), never wall-clock stamps compared by order:
    // MO2's "Restore Backup" restores a profile file with an OLDER mtime, which an ordered comparison never sees.
    // Each baseline is statted BEFORE the read it baselines, so a write landing during the read shows up on the
    // next check rather than being absorbed.
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

    /// <summary>Test seam: wrap a prebuilt resolver so a test can drive the service-layer query logic on synthetic
    /// plugins with no MO2 profile and no user config on disk. Freshness checks no-op here (no ini, empty profile
    /// dir) and the caller owns the resolver's lifetime. Never used by the product.</summary>
    internal static LoadOrderService ForGuard(LoadOrderResolver resolver, UserConfigStore store)
    {
        var svc = new LoadOrderService(null, "", "", "", configured: true, maxPlugins: 0, store);
        svc._resolver = resolver;
        return svc;
    }

    /// <summary>The write pre-flight rulebook (corpus.json), loaded once. CorpusPath is set absolute at startup, so
    /// this resolves regardless of the MO2-launched process's working directory.</summary>
    CorpusRulebook Rulebook => _rulebook ??= CorpusRulebook.Load();

    /// <summary>One captured index build, for a <see cref="FormIdDoor"/> that has found a runtime FormID to
    /// resolve.</summary>
    internal LoadOrderResolver.IndexView CaptureView() => Resolver.Capture();

    /// <summary>A FormID door for a tool body with no captured view of its own — see <see cref="FormIdDoor"/>.</summary>
    internal FormIdDoor OpenFormIdDoor() => FormIdDoor.For(this);

    /// <summary>The same door for a WRITE verb's tokens, which refuses a runtime FormID — see
    /// <see cref="FormIdDoor.ForWrite"/>.</summary>
    internal FormIdDoor OpenWriteFormIdDoor() => FormIdDoor.ForWrite(this);

    /// <summary>The resolver, built on first access and kept fresh on every subsequent access. Throws loudly if the
    /// configured roots yield no plugins.</summary>
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
                    // The true active order, read statically from the MO2 profile files — no VFS, no live MO2 state.
                    var profileMtimes = StatProfileFiles();      // stat BEFORE the read: a profile write during the build is caught next call, not missed
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
                    // Lazy freshness on each tool call, deferred while a write is in flight: a refresh rebuilds the
                    // index, transiently mmap-opening every plugin including the file a concurrent write is
                    // serializing, and dispose-swaps the resolver that write captured. TryEnter probes the write gate
                    // without blocking, so it cannot deadlock against the _writeGate-then-_gate order, and Monitor
                    // reentrancy keeps a write's own entry refresh working. A skipped refresh serves the last good
                    // snapshot and re-checks next call.
                    try
                    {
                        RefreshOnProfileChange();     // lazy profile-membership refresh on this call (cheap check first)
                        _resolver.RefreshIfStale();   // plugin-CONTENT freshness: cheap stat sweep; rebuilds if a plugin's bytes changed
                    }
                    finally { Monitor.Exit(_writeGate); }
                }
                return _resolver;
            }
        }
    }

    // ---- VFS asset resolution (housecarl_asset_status) --------------------------------------------------

    /// <summary>The VFS-aware asset resolver, built on first asset query and kept fresh on every subsequent one — the
    /// asset twin of <see cref="Resolver"/>. It runs the same profile-freshness driver, then its own cheap BSA-byte
    /// and loose-subtree content sweep. It deliberately does NOT force the heavy <see cref="Resolver"/> build, so an
    /// asset-only query stays cheap. The getter takes <see cref="_gate"/>, so callers need not pre-hold it.</summary>
    AssetResolver Assets
    {
        get
        {
            lock (_gate)
            {
                if (!_configured) throw NotConfigured();           // fresh install → the tool returns the prompt for the MO2 path instead
                EnsurePathsDerived();                              // derive the roots on first use (instance mode)
                // Profile freshness (switch / toggle / re-sort), shared with the record path and deferred behind an
                // in-flight write the same way. ReResolve is null-safe for _resolver, so this follows a profile change
                // without building the record index, and drops _assetResolver when the active set changed.
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

    /// <summary>The injected answer to "why is this plugin filename not in the active order?", handed to every
    /// <see cref="LoadOrderResolver"/> this service builds. Returns null when nothing can be said, and the refusal
    /// then falls back to a did-you-mean.
    /// <para>The profile and the roots are read fresh on each call rather than captured: a profile switch reassigns
    /// the roots but only rebuilds the resolver when the resolved path list changed, so two profiles with identical
    /// active sets and different unticked lists would leave a capture reading the old plugins.txt. This runs only on
    /// a refusal, never on a hot path, so the extra three-file parse is free.</para>
    /// <para>Vocabulary is deliberate: a MOD is enabled/disabled (MO2's left pane), a PLUGIN is active/inactive (its
    /// right pane).</para></summary>
    internal int AbsenceExplanations;   // how many times the explainer has parsed the profile — a test seam for the memo

    string? ExplainPluginAbsence(string name)
    {
        // Snapshot the roots together under the gate so the four cannot be read across a mid-switch reassignment.
        string profileDir, modsDir, dataDir, overwriteDir;
        lock (_gate) { profileDir = _profileDir; modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; }
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(profileDir)) return null;
        var fn = Path.GetFileName(name.Trim());
        if (fn.Length == 0) return null;
        Interlocked.Increment(ref AbsenceExplanations);
        Mo2Composition comp;
        try { comp = Mo2LoadOrder.ReadComposition(profileDir); }
        catch { return null; }                       // unreadable profile → say nothing rather than guess

        bool ticked = comp.ActivePluginNames.Contains(fn);
        bool unticked = comp.InactivePluginNames.Any(x => x.Equals(fn, StringComparison.OrdinalIgnoreCase));

        // The headline case: MO2's left pane says yes, its right pane says no. The file is sitting right there, so a
        // bare "not in the load order" reads as "missing" for something that is installed and one click from working.
        if (unticked)
            return $"'{fn}' IS installed, but it is UNTICKED in plugins.txt (MO2's right pane), so the game does not " +
                   "load it and houseCARL does not read it. Tick it in MO2 and re-sort — or, to read the file as-is " +
                   "without loading it, use " + ToolNames.Records + $" with source=\"{fn}\" and something to select — " +
                   "types=[…] to scan the file, or formids=[…] for named records (source= names the version to read, " +
                   "it is not a selection). It resolves a plugin wherever it lives, in the order or on disk, and the " +
                   "response states which arm answered.";

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

        // On disk but the profile never mentions it. The remedy turns on which layer holds it, read from the mod list
        // rather than guessed from the hit's Enabled flag: an unlisted folder is flagged not-enabled exactly like a
        // disabled one, but there is nothing in MO2 to switch on — and houseCARL's own just-written patches live in
        // an unlisted folder, which is the most common way to reach this message.
        var pick = hits.FirstOrDefault(h => !h.Enabled) ?? hits[0];
        var folder = Path.GetFileName(Path.GetDirectoryName(pick.Path) ?? "") ?? "";
        var remedy =
            pick.Enabled                                                              ? "Refresh MO2 so it registers the plugin, then tick it and sort"
            : comp.DisabledMods.Any(m => m.Equals(folder, StringComparison.OrdinalIgnoreCase))
                                                                                      ? "Switch that mod on in MO2, then tick the plugin and sort"
                                                                                      : "MO2 has not registered that folder yet — refresh MO2, then tick the plugin and sort";
        return $"'{fn}' is on disk in {pick.Where}, but MO2's load order does not list it, so it is not active. " +
               $"{remedy} — or read the file as-is with {ToolNames.Records} source=\"{fn}\" types=[…] " +
               "(source= names the version to read; the read still needs a selection).";
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

    /// <summary>The Papyrus source folders this modlist ships: every enabled mod's <c>Source\Scripts</c> /
    /// <c>Scripts\Source</c>, in MO2's own VFS precedence, so an installed framework lands on the compiler's import
    /// path without being retyped per call.
    /// <para>The order comes off <see cref="AssetResolver.LooseRoots"/> rather than being re-derived, so a shadowed
    /// script resolves through the same precedence every other asset answer uses. The cost is that a compile-only
    /// session builds the asset resolver.</para>
    /// <para>Best-effort by contract: an unconfigured or unreadable profile returns an empty list plus a warning,
    /// never an exception — losing the ergonomic default must not lose the compile. A read that THREW also sets
    /// <c>Failed</c>, because an empty root list is otherwise indistinguishable from a modlist that genuinely ships
    /// no source folders, and the caller renders the two differently.</para></summary>
    public (IReadOnlyList<PapyrusSourceRoot> Roots, string? GameDataSources, string? Warning, bool Failed) PapyrusSourceImportDirs()
    {
        IReadOnlyList<(string Name, string Dir)> roots;
        string dataDir;
        try { lock (_gate) { roots = Assets.LooseRoots; dataDir = _dataDir; } }
        catch (Exception ex)
        {
            // Says what failed and what it costs, and nothing about vanilla — this method has no way to check whether
            // the vanilla sources are on the import path. Labelled "modlist scan" rather than "auto_imports" because
            // the caller also reaches here with auto_imports off, purely to locate the vanilla fallback.
            return (Array.Empty<PapyrusSourceRoot>(), null,
                    "modlist scan: could not read the MO2 modlist to discover Papyrus source folders " +
                    $"({ex.Message}) — none of your installed mods' source folders are on the import path for this compile.",
                    true);
        }
        // The game's own Data root is split out here, where the data dir is known, rather than left to a
        // compiler-relative vanilla check: on a Stock Game setup those are different folders (the CK compiler lives
        // in the real Steam install), so that check would never fire and the base game would rank as an ordinary mod.
        // It is handed back rather than discarded — the caller uses it as the vanilla slot when the compiler-relative
        // folder doesn't resolve.
        try
        {
            var (mods, gameData) = PapyrusSourceRoots.SplitGameData(PapyrusSourceRoots.Discover(roots), dataDir);
            return (mods, gameData, null, false);
        }
        catch (Exception ex)
        {
            return (Array.Empty<PapyrusSourceRoot>(), null,
                    $"modlist scan: scanning the modlist for Papyrus source folders failed ({ex.Message}) — " +
                    "pass the dependency source folders via import_dirs=/import_set= for this compile.",
                    true);
        }
    }

    /// <summary>Resolve a batch of Data-relative asset paths through the MO2 VFS (housecarl_asset_status): for each,
    /// which source provides it and which copy wins (loose beats BSA; among BSAs the higher plugin rank). One
    /// <see cref="AssetResolver.Capture"/> for the whole batch, so every path and the build-level BsaFailures /
    /// ReadIncomplete caveat describe a single build. A drive-rooted or '..'-escaping path is a per-path recoverable
    /// error, never a batch failure.
    /// <para><paramref name="under"/> is the directory / glob SELECT form (#246): each selector names a Data-relative
    /// folder, or a glob anchored under one, and contributes every path the VFS provides beneath it
    /// (<see cref="AssetGlob"/>). Its matches follow the explicit paths, sorted, with anything already named dropped.
    /// <paramref name="limit"/> and <paramref name="offset"/> window the SELECTION, so only the window is RESOLVED —
    /// the per-path winner and provider chain, which is the expensive half. The selector ENUMERATION is not memoized:
    /// each page re-walks the loose roots and re-scans the archive tables under the prefix, so a paged sweep pays the
    /// enumeration once per page and the resolution once per rendered path.</para></summary>
    public AssetStatusData AssetStatus(
        IReadOnlyList<string> relPaths,
        IReadOnlyList<string>? under = null,
        int limit = 0,
        int offset = 0)
    {
        lock (_gate)
        {
            var view = Assets.Capture();                          // reentrant gate; build/refresh the asset resolver once for the batch
            var notes = new List<string>();
            var selected = new List<string>(relPaths);            // explicit paths first, in the order given, never deduped
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in relPaths)
                try { seen.Add(AssetResolver.ValidateRelPath((p ?? "").Trim())); } catch (ArgumentException) { /* a bad explicit path answers per-path below */ }

            foreach (var raw in under ?? Array.Empty<string>())
            {
                var sel = (raw ?? "").Trim();
                if (sel.Length == 0) { notes.Add("under: an empty selector was skipped — pass a Data-relative directory or glob."); continue; }
                try
                {
                    var matched = AssetGlob.Select(view, sel, out var namedOneFile);
                    // A selector that named a FILE is said out loud too, so the sweep's own count is explained.
                    if (namedOneFile)
                        notes.Add($"under '{sel}' names a file, not a directory — it was resolved as that one path.");
                    // A selector that matched nothing is said out loud: read as a silent no-op it looks identical to a
                    // folder no enabled mod provides, and a typo would then read as a clean sweep.
                    else if (matched.Count == 0)
                        notes.Add($"under '{sel}' matched no file in the active load order — check the spelling, or nothing enabled provides that folder.");
                    foreach (var m in matched) if (seen.Add(m)) selected.Add(m);
                }
                catch (ArgumentException ex) { notes.Add($"under '{sel}': {ex.Message}"); }
            }

            // Page over the SELECTED set and resolve only the window, so a 15k-file sweep pays for what it renders.
            var total = selected.Count;
            var start = Math.Min(Math.Max(offset, 0), total);
            var window = selected.Skip(start).Take(limit > 0 ? limit : int.MaxValue).ToList();

            var results = new List<AssetPathResult>(window.Count);
            foreach (var raw in window)
            {
                var p = (raw ?? "").Trim();
                try
                {
                    var hit = view.Resolve(p);
                    // Only on ABSENT: a path taken off a record is stored relative to its root folder (a model path
                    // to meshes\, a texture path to textures\). Both roots are tried because this lane, unlike
                    // nif_inspect, doesn't know the path's kind, and only VERIFIED prefixes are suggested — this tool
                    // legitimately answers for sound\, scripts\, interface\ and the rest.
                    var suggest = hit.Exists ? Array.Empty<string>()
                                             : AssetPathHint.VerifiedPrefixes(view, p, AssetPathHint.AssetRoots);
                    results.Add(new AssetPathResult(p, hit, null, suggest));
                }
                catch (ArgumentException ex) { results.Add(new AssetPathResult(p, null, ex.Message)); }   // bad path → per-path note, never a batch failure
            }
            return new AssetStatusData(results, view.BsaFailures, view.ReadIncomplete, _assetWarnings, _profileName,
                                       notes, total, Math.Max(offset, 0),    // the offset ASKED for, so a past-the-end page can say so
                                       Math.Max(limit, 0));                  // the limit ASKED for, so the next-page advice repeats it
        }
    }

    // ---- SKSE-plugin-layer visibility: inventory the DLLs, configs and winning provider, plus each plugin DLL's
    //      statically declared manifest. Read-only; reuses the asset VFS and the PE reader. ----

    /// <summary>Inventory the SKSE-plugin layer as the active load order resolves it: the full depth of
    /// Data\SKSE\Plugins — every <c>.dll</c> and every <c>.ini</c>/<c>.toml</c>/<c>.json</c>/<c>.yaml</c> config at any
    /// depth — with the mod that wins the VFS for each, and for every DLL the statically declared manifest via
    /// <see cref="SksePluginReader"/>. Every file is accounted for: configs carry their derived subfolder
    /// <see cref="SkseFileEntry.Group"/> (whatever the modlist ships, never a hardcoded framework list) and non-config
    /// content is counted in <see cref="SkseInventoryData.OtherFileCount"/> rather than dropped. A subfolder DLL is
    /// listed but flagged: SKSE scans Data\SKSE\Plugins\*.dll top-level only, so it is not loaded as a plugin. One
    /// asset capture pins the whole scan, and the enumerate, resolve and PE reads run outside the gate (the captured
    /// view is a handle-free immutable snapshot) so an inventory never serializes other tool calls behind its file
    /// I/O. Distributor INIs (SPID <c>*_DISTR</c>, KID <c>*_KID</c>) live in the Data root, not here.</summary>
    /// <param name="peekFilter">When non-null, every DLL entry matching it (<see cref="SkseFileEntry.MatchesDll"/>,
    /// the same predicate the renderer filters on) also gets its image string-scanned into
    /// <see cref="SkseFileEntry.Peek"/>. Null = no scan. Per-DLL because the scan reads the whole image, unlike the
    /// import walk, which rides the manifest read every DLL already gets.</param>
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
        // The plugin names a peek's embedded-reference cross-check adjudicates against. A cheap three-file text parse
        // with no index build, skipped entirely without peek= so a normal inventory pays nothing for it. The set is
        // what the game actually loads: plugins.txt `*` entries plus the force-loaded base and CC masters, which load
        // despite never appearing there — omitting the implicit ones would flag Dawnguard.esm absent on an install
        // that has it.
        IReadOnlySet<string>? activePlugins = null;
        if (peekFilter is { Length: > 0 })
        {
            var compWarnings = new List<string>();
            activePlugins = PeekPluginSet(Mo2LoadOrder.ReadComposition(profileDir, compWarnings));
            if (compWarnings.Count > 0) warnings = [.. warnings, .. compWarnings];
        }
        // Outside the gate: the view is pinned and handle-free (Resolve reads only the captured snapshot and readonly
        // roots), so enumerating, resolving and PE-reading here cannot race a concurrent refresh into wrongness and
        // does not block other tools behind these file reads.
        const string pre = "SKSE\\Plugins\\";
        var dlls = new List<SkseFileEntry>();
        var configs = new List<SkseFileEntry>();
        int otherFiles = 0;
        foreach (var rel in view.EnumerateUnder("SKSE\\Plugins").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var ext = Path.GetExtension(rel).ToLowerInvariant();
            bool isDll = ext is ".dll";
            bool isConfig = ext is ".ini" or ".toml" or ".json" or ".yaml" or ".yml";
            if (!isDll && !isConfig) { otherFiles++; continue; }  // content/other (.hkx/.txt/.pdb/…) — counted, not listed

            string group = SkseGroupOf(rel, pre);                 // "" = top-level; else the immediate subfolder (the derived group key)
            var place = view.ResolveForPlacement(rel);
            // The full conflict chain, winner-first: every provider and its loose/BSA kind, not just a count.
            var providers = place.Sources
                .Select(s => new SkseProvider(s.ProviderName, KindLabel(s.Kind)))   // shared kind→label helper (explicit switch, never a defaulted "loose")
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
                // String peek only for a filter-matched DLL with a loose winner — the copy SKSE would load. A BSA-only
                // DLL never loads, so peeking it would describe an image the game never reads.
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

    /// <summary>Why a loose, loader-scoped SKSE plugin DLL statically cannot load, or null when nothing stops it. The
    /// blocker chain for the winning loose copy, in severity order; a non-null result rides
    /// <see cref="NativePairedDll.LoadBlocker"/>, which the pairing verdict already treats as dead.
    /// The debug-build check matters because a debug-built DLL is loose, top-level, x64, readable and usually
    /// version-independent — every other check passes it while the loader refuses it with error 126.
    /// <paramref name="resolvable"/> is injected so the chain can be tested without a live order or a live machine
    /// carrying the debug runtime.</summary>
    internal static string? LooseDllBlocker(SksePluginReader.SksePluginInfo info, Func<string, bool> resolvable)
    {
        if (info.Kind == SksePluginReader.SksePluginKind.Unreadable) return $"not a readable SKSE plugin ({info.Note})";
        if (info.Is64Bit == false) return "a 32-bit image — cannot load in Skyrim SE/AE";
        return SksePluginReader.DebugCrtBlocker(info, resolvable);
    }

    /// <summary>The plugin names a peek adjudicates an embedded reference against — active plus the force-loaded
    /// implicit masters, which load despite never appearing in plugins.txt. Returns <c>null</c>, never a partial set,
    /// when the answer is unknowable, because "the order could not be determined" and "the order is empty" must not
    /// render the same.
    /// The gate is <see cref="Mo2Composition.OrderedPluginNames"/> and that choice is load-bearing: the implicit set
    /// is derived by iterating the ordered list, so with loadorder.txt missing it collapses to empty while plugins.txt
    /// can still hand back a non-empty active set. Gating on the merged set instead would return an active-only set
    /// whose force-loaded masters are silently gone. Reachable in practice — reading the composition never throws on
    /// a missing profile file, and the three profile files are independently mutable.</summary>
    internal static IReadOnlySet<string>? PeekPluginSet(Mo2Composition comp)
    {
        if (comp.OrderedPluginNames.Count == 0) return null;   // no loadorder.txt ⇒ the implicit masters are unknowable, not absent
        var set = new HashSet<string>(comp.ActivePluginNames, StringComparer.OrdinalIgnoreCase);
        set.UnionWith(comp.ImplicitPluginNames);
        return set.Count > 0 ? set : null;
    }

    /// <summary>The immediate subfolder under SKSE\Plugins a file sits in ("" = top level) — the derived grouping key
    /// for the SKSE inventory. Whatever a modlist ships becomes a group; a hardcoded framework list would break the
    /// generated-coverage cornerstone and silently miscategorize anything not on it.
    /// e.g. <c>SKSE\Plugins\SkyPatcher\Weapons\x.ini</c> → "SkyPatcher"; <c>SKSE\Plugins\EngineFixes.toml</c> → "".</summary>
    static string SkseGroupOf(string rel, string pre)
    {
        if (!rel.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return "";
        int slash = rel.IndexOf('\\', pre.Length);
        return slash < 0 ? "" : rel.Substring(pre.Length, slash - pre.Length);
    }

    // ---- SKSE config audit: cross-check the form references SKSE-plugin configs declare against the real records
    //      of the active load order. ----

    /// <summary>Per-file byte cap for the config scan: a config larger than this is a named skip, not fed to the token
    /// scanner. Real distributor configs are KB-scale, so 16 MB trips only on content mislabeled as a config.</summary>
    const long SkseConfigSizeCap = 16L * 1024 * 1024;

    /// <summary>Audit the SKSE-plugin config layer against the load order. For every .ini/.toml/.json/.yaml under
    /// Data\SKSE\Plugins, read the winning copy (the DLL never reads the losers), extract the form-shaped references
    /// and path-segment plugin gates it declares, and resolve each against the active order into a verdict: OK,
    /// PLUGIN MISSING, DANGLING, or UNPARSEABLE. Framework-agnostic — it never interprets what a reference is for.
    /// One asset capture and one resolver index pin the whole scan; the enumerate, read and resolve run outside the
    /// gate (the captured view is handle-free and the index a pure snapshot read). "No references found" is a normal
    /// per-file outcome, accounted for rather than warned about.</summary>
    public SkseConfigAuditData SkseConfigAudit()
    {
        AssetResolver.AssetView view;
        LoadOrderResolver.IndexView index;
        IReadOnlyList<string> warnings;
        string profileName;
        // Capture the asset view AND the record index under one gate hold, so a freshness rebuild cannot interleave
        // and pair a config read from one asset build against a record index from the next. Both are handle-free
        // snapshots, so the enumerate, read and resolve below run outside the gate.
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
                readError = "no active mod provides this config";   // shouldn't happen for an enumerated file — named, not assumed
            else if (winner.Kind == AssetKind.Loose && winner.LooseFilePath is { } lp && File.Exists(lp) && new FileInfo(lp).Length > SkseConfigSizeCap)
                readError = OverCapNote(new FileInfo(lp).Length);
            else
            {
                var (bytes, err) = AssetResolver.ReadPlacementSource(winner);
                if (err is not null) readError = err;
                else if (bytes!.Length > SkseConfigSizeCap) readError = OverCapNote(bytes.Length);
                else text = DecodeConfigText(bytes);
            }

            // Path-segment gates come from the relPath, so they surface even when the file could not be read — the
            // gate is a property of where the file lives, not its content. Only the token scan needs the text.
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
    /// plugin is present but the masked FormID resolves to nothing). Never speculates about runtime behavior.</summary>
    internal static SkseAuditedRef Adjudicate(SkseConfigRef r, LoadOrderResolver.IndexView index)   // internal: a test drives it over a synthetic order
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

    /// <summary>The over-size-cap skip note. The size carries one decimal so a 16.4 MB file reads "16.4 MB (> 16 MB
    /// cap)" rather than the self-contradictory "16 MB (> 16 MB cap)" an integer divide would give.</summary>
    static string OverCapNote(long len) =>
        $"config is {len / (1024.0 * 1024):0.0} MB (> {SkseConfigSizeCap / (1024 * 1024)} MB cap) — not scanned";

    // ---- Native-function pairing audit: cross-check the native Papyrus functions the order's scripts declare
    //      against the DLLs that must implement them. ----

    /// <summary>Audit the declaration-to-implementation pairing of every native Papyrus class in the active order. One
    /// pass over the winning <c>.pex</c> files extracts native-flagged declarations; one pass over SKSE\Plugins finds
    /// the DLL candidates each mod ships; then per third-party class an evidence ladder — same-mod DLL, conflict-chain
    /// DLL, or UNPAIRED (a verify flag, never "broken": registration is runtime behavior this cannot see).
    /// <para>A class whose provider chain includes an official archive (the Skyrim.ini base block or a BaseMaster-owned
    /// BSA) is ENGINE, implemented by the executable, even when a mod's loose copy wins it — SKSE overrides Actor,
    /// Game and others with native additions, and the official-archive presence still marks the class baseline. A
    /// third-party class whose winning provider also provides an ENGINE class is SKSE CORE: the skse64 scripts payload
    /// co-ships a hundred-odd vanilla overrides with its new classes, and its implementation is the game-root loader
    /// rather than anything under SKSE\Plugins. Known residual edges: an INI-injected third-party BSA reads official;
    /// a paid-CC archive is not BaseMaster-owned, so its engine-native classes read third-party and get a verify flag;
    /// and a mod co-shipping a vanilla-script override with a declaration copy of an absent framework gets its copy
    /// rescued into SKSE CORE, visible in the accounting and unflagged.</para>
    /// <para>One gate hold captures the asset view, the archive list and the warnings; the enumerate, parse and
    /// classify run outside the gate over the pinned handle-free view. The per-file Pex parses are parallelized (the
    /// view's caches are concurrency-safe) with deterministic output ordering. An unreadable .pex is a named entry,
    /// never a silent skip.</para></summary>
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
            archives = _activeArchives;         // the same build as the view (both swapped under _gate)
            enabledMods = _enabledModsAtBuild;  // ditto — the loader scan below walks the mod set the view describes, never a second unpinned profile read
            warnings = _assetWarnings;
            profileName = _profileName;
            dataDir = _dataDir;
            modsDir = _modsDir;
            overwriteDir = _overwriteDir;
        }

        // ---- the official-archive set: the ENGINE anchor. Keyed by filename, because a BSA provider's name IS the archive filename. ----
        var officialArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
        foreach (var a in archives)
            if (IsOfficialArchive(a, baseMasters))
                officialArchives.Add(Path.GetFileName(a.Path));

        // A BSA provider's name is the archive filename, but pairing identity needs the MOD that ships the archive: a
        // mod's scripts can ride its own BSA while its DLL sits loose in the same folder, and untranslated the ladder
        // would see two unrelated providers and call it UNPAIRED. The winning physical path of each active archive
        // names its shipper: mods\<mod>\X.bsa → that mod; overwrite\ → "overwrite"; Data → "Data".
        var archiveShipper = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archives)
            if (ShipperOfArchivePath(a.Path, modsDir, overwriteDir, dataDir) is { } shipper)
                archiveShipper[Path.GetFileName(a.Path)] = shipper;

        // ---- DLL candidates: one SKSE\Plugins pass. A mod "ships" a DLL when it appears anywhere in that file's
        //      chain, so the bundling case pairs through the chain; the health verdict describes the winning copy. A
        //      winner that PE-reads as NotSkse — loose or BSA-packed — is a bundled dependency, not an
        //      implementation candidate. ----
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
                    // PE-screen the packed copy too (DLLs are few, so the per-entry read is fine): a packed NotSkse
                    // dependency must not count as a candidate, or its mod gains pairing evidence it never earned.
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

        // ---- the .pex sweep, two phases. Phase 1 (parallel): resolve every path, parse loose winners in place, and
        //      defer BSA winners to a per-archive batch — a per-entry read re-opens the archive and walks its whole
        //      table each time, which is the dominant cost against the ten-thousand-script vanilla archives. Phase 2:
        //      one table walk per archive collects all its wanted entries, then the parses run parallel over the
        //      bytes. An unreadable .pex is a named entry, never a silent skip. ----
        var pexPaths = view.EnumerateUnder("Scripts")
            .Where(p => Path.GetExtension(p).Equals(".pex", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        // Only native-declaring files are kept — a large order yields a couple of hundred — and their conflict chains
        // are re-resolved on collection, which is cheap at that count.
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

        // ---- classify and pair (sequential; cheap set lookups over a few hundred native files). Provenance and
        //      pairing key on the enum-typed PlacementSource, never the render label — the display string must not
        //      double as the semantic discriminator. ----
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
        // "overwrite" is excluded from the rescue: a recompiled vanilla .pex in MO2's overwrite is routine (the
        // compile lane writes there), and letting it rescue every orphan declaration copy that also lands in overwrite
        // would silence the flag this tool exists for. "Data" stays — the manual game-folder SKSE install is the
        // layout the rescue must cover.
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

        // Is an skse64 loader visible at all? Two places to look: the game root (a manual install), and each enabled
        // mod's Root\ folder — the MO2 Root Builder layout, where the loader lives at mods\<mod>\Root\skse64_loader.exe
        // and only materializes in the game root at launch. The mod list is the same capture as the view. Tri-state:
        // a check that threw yields null, "could not check", never a false "checked and absent".
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
    /// anywhere else → null (no translation — the archive name stands).</summary>
    internal static string? ShipperOfArchivePath(string archivePath, string modsDir, string overwriteDir, string dataDir)
    {
        // Full-path-normalize both sides so forward slashes, '..' segments or a trailing-separator root from config
        // cannot make the under-root test disagree with the rest of the plumbing.
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
    /// list).</summary>
    internal static bool IsOfficialArchive(ActiveArchive a, IReadOnlyList<ModKey> baseMasters) =>
        a.OwningPlugin.Equals(ArchiveDiscovery.IniArchiveOwner, StringComparison.OrdinalIgnoreCase)   // ignore-case like every other archive compare — this must not hinge on the marker's casing
        || (ModKey.TryFromNameAndExtension(a.OwningPlugin, out var mk) && baseMasters.Contains(mk));

    /// <summary>True when any source in a file's chain is an official archive — the ENGINE provenance test. Keys on
    /// the <see cref="AssetKind"/> enum plus the archive filename (a BSA source's provider name IS its archive
    /// filename), so a loose override winning the file still leaves the class baseline, and a render-label change
    /// cannot silently break it.</summary>
    internal static bool HasOfficialSource(IReadOnlyList<PlacementSource> sources, HashSet<string> officialArchives) =>
        sources.Any(s => s.Kind == AssetKind.Bsa && officialArchives.Contains(s.ProviderName));

    /// <summary>One source's PAIRING IDENTITY — the mod it means: a BSA source translates to the mod shipping the
    /// archive (via the archiveShipper map); everything else is its provider name (mod folder / overwrite / Data).</summary>
    internal static string PairingIdentity(PlacementSource src, IReadOnlyDictionary<string, string> archiveShipper) =>
        src.Kind == AssetKind.Bsa && archiveShipper.TryGetValue(src.ProviderName, out var mod) ? mod : src.ProviderName;

    /// <summary>The pairing-evidence ladder for one third-party class, over the chain's pairing identities, winner
    /// first: rung 1, the winning identity ships at least one candidate DLL; rung 2, an identity deeper in the chain
    /// does (a patch mod wins the script while the framework beneath ships the DLL); rung 3, nobody in sight does, so
    /// UNPAIRED — a verify flag. An identity whose candidates all carry a static LoadBlocker does not stop the descent
    /// when a deeper identity has a loadable candidate, so a bundler shipping one dead helper DLL cannot mask the real
    /// framework beneath it; if no identity has a loadable candidate, the shallowest with any candidate pairs and its
    /// deadness becomes the finding. "Loadable" here means no STATIC blocker — version-locked-versus-runtime deadness
    /// is adjudicated by the renderer, which owns that decision. The evidence is structural (file co-location and VFS
    /// chains), never semantic: which DLL implements which class is out of reach.</summary>
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

    /// <summary>The full per-class decision, in order: ENGINE (official-archive presence), then the pairing ladder,
    /// then the SKSE-CORE rescue for an UNPAIRED class whose winning identity also ships an ENGINE-class copy — the
    /// skse64 payload co-ships many vanilla overrides with its new classes. Pairing evidence beats the rescue: a class
    /// that pairs to a DLL stays third-party regardless of its provider's other files. Known residual: a provider that
    /// co-ships a vanilla-script override AND a declaration copy of an absent framework gets that copy rescued into
    /// the unflagged baseline, which for the game Data folder covers everything manually installed there. "overwrite"
    /// is excluded from the pool at the call site for that reason.</summary>
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

    // ---- SkyPatcher distributor: the per-record true post-SkyPatcher state. Read-only. ----

    /// <summary>Cross-call INI parse cache (mtime+length keyed — see <see cref="SkyPatcherDiscovery.ParseCache"/>):
    /// repeat post-state calls over an untouched layer skip every per-file read+parse.</summary>
    readonly SkyPatcherDiscovery.ParseCache _skyPatcherParseCache = new();

    /// <summary>The in-memory scratch mod the replay copy is overridden into — never written to disk.
    /// Named per the Housecarl* scratch-mod convention (<c>HousecarlWriteProof</c> is the sibling).</summary>
    static readonly ModKey SkyPatcherScratchKey = new("HousecarlSkyPatcherScratch", ModType.Plugin);

    /// <summary>The per-record SkyPatcher replay core, shared by the post-state read and the layer
    /// no-op (true-ITM) scan: resolve the winner, materialize a mutable scratch copy, apply every
    /// type folder's ordered lines in field-map order. Error is the named reason the record cannot be
    /// replayed; the caller decides whether that is a failure or a skip-with-count.</summary>
    (string? TypeName, string? WinnerPlugin, string? EditorId, List<SkyPatcherFolderOutcome> Folders, string? Error, IMajorRecord? Copy)
        ReplaySkyPatcher(
            LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
            SkyPatcherDiscovery.LayerScan scan, SkyPatcherCatalog catalog, SkyPatcherFieldMap fieldMap,
            SkyrimMod scratch, SkyPatcherOverlay.IFormResolver formResolver, FormKey fk,
            Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>? linesCache)
    {
        var none = new List<SkyPatcherFolderOutcome>();
        var winner = view.ResolveWinner(fk);
        if (winner is null)
            return (null, null, null, none, UnresolvedFormId(view, fk), null);

        var body = view.GetRecord(session, winner.Value.WinnerPlugin, fk);
        if (body is null)
            return (null, winner.Value.WinnerPlugin, null, none, $"Winner '{winner.Value.WinnerPlugin}' did not yield {fk} on fetch — a load-order inconsistency.", null);

        var typeName = ReadEngine.ReadFields(body, new[] { "EditorID" }).Type;   // the same type naming every read tool reports
        var maps = fieldMap.ForRecordType(typeName);
        if (maps.Count == 0)
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"Record type '{typeName}' is not a SkyPatcher-patchable type (or has no field map) — the SkyPatcher layer cannot touch {fk}.", null);

        // The running copy: the winner overridden into an in-memory scratch mod (never written to disk).
        // Nested-group types (CELL / REFR / INFO…) need the source link cache to rebuild their parent
        // chain — the same RecordNeedsSourceCache + LinkCacheFor idiom every write path uses. Without it
        // those types throw unhandled instead of failing by name.
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
                $"Could not materialize a mutable copy of {fk} ({typeName}) for the replay — {ex.GetType().Name}: {ex.Message}", null);
        }

        // Watch this record's own EditorID lookups: only a replay that actually read from a table missing a
        // plugin's records is affected, so a record addressed purely by FormID answers normally.
        var spr = formResolver as SkyPatcherServiceResolver;
        spr?.WatchLookups();

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
            // A toggled-off folder contributes nothing: reporting its files as "applied" would assert as
            // live the INIs the DLL skips wholesale. Enabled rides along so the render can say why the
            // counts are zero.
            folders.Add(new SkyPatcherFolderOutcome(folder.Subfolder,
                folder.PatchingEnabled ? folder.Files.Count(f => f.NotApplied is null) : 0, lines.Count, result,
                folder.PatchingEnabled));
        }

        // An EditorID sweep that could not read a plugin leaves that plugin's EditorIDs out of the lookup table, so a
        // line naming one resolves to nothing and this replay would report a state the layer does not produce.
        // Named as the record's error rather than answered wrong.
        if (spr is { ConsumedIncompleteTable: true })
            return (typeName, winner.Value.WinnerPlugin, body.EditorID, none,
                $"the SkyPatcher replay of {fk} resolved an EditorID against the load order, and "
                + string.Join(" ", spr.Unreadable.Select(u => u.Message).Distinct()), null);

        return (typeName, winner.Value.WinnerPlugin, body.EditorID, folders, null, copy);
    }

    /// <summary>
    /// Scan the whole SkyPatcher layer: every loose INI as the DLL reads it (ordered union, VFS
    /// same-path collisions surfaced, gates and toggles evaluated), plus the INI-vs-INI same-field SET
    /// collisions and the three ITM classes — intra-file dead writes, cross-INI duplicates, and the
    /// no-op writes found by the per-record replay below. Report-only. One record capture answers the
    /// filename gates and one asset capture pins the scan; the enumerate, parse and detect run outside
    /// the gate on the handle-free captured view. A layer with no INIs is a named outcome, never an
    /// empty guess.
    /// </summary>
    public SkyPatcherLayerData SkyPatcherLayer()
    {
        // No epoch is stamped: the INI layer is outside the index fingerprint, so a bare index epoch would overclaim.
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
        //      Broad (type-wide) lines are evaluated only against the explicitly-targeted records;
        //      replaying every record of a type is not attempted, and the note says so. Deliberate
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
            // Stable output: targets is a hash set, so without this the findings' order varies run to
            // run and a re-run cannot be diffed against the previous one.
            noOps.Sort((x, y) =>
            {
                int c = string.Compare(x.File, y.File, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = x.Line.CompareTo(y.Line);
                return c != 0 ? c : string.Compare(x.FormKey, y.FormKey, StringComparison.OrdinalIgnoreCase);
            });
            if (broadLines > 0) noOpNotes.Add($"no-op scan: {broadLines} broad (type-wide) line(s) were evaluated only against the explicitly-targeted records, not every record of their type.");
            if (unresolvedTargets > 0) noOpNotes.Add($"no-op scan: {unresolvedTargets} explicit target(s) did not resolve (the overlay's per-record warnings name them; read one with {ToolNames.Records} formids=[\"<FormID>\"] source={{\"overlay\": \"skypatcher\", \"state\": \"post\"}}).");
            if (failedReplays > 0) noOpNotes.Add($"no-op scan: {failedReplays} targeted record(s) could not be replayed (not in the order / unpatchable type / copy failure / an EditorID lookup that could not be completed).");
            // Emitted AFTER the replays: a plugin can first turn out unreadable in a sweep the replay itself runs
            // (a value operand naming a donor by EditorID), not only in the target-collection sweep above.
            foreach (var msg in formResolver.Unreadable.Select(u => u.Message).Distinct())
                noOpNotes.Add($"no-op scan: {msg} Lines naming a record it defines could not be resolved.");
        }

        return new SkyPatcherLayerData(scan, conflicts, itms, duplicates, noOps, noOpNotes,
            scan.ReadIncomplete || assets.ReadIncomplete, assetWarnings, profileName);
    }

    /// <summary>The live-load-order lookups the overlay needs (<see cref="SkyPatcherOverlay.IFormResolver"/>),
    /// answered off the ONE pinned record view + open session the post-state call holds. EditorID resolution
    /// sweeps the requested type's winners once into an eid→FormKey table, because an INI layer typically names
    /// many EditorIDs of the same type and a per-eid sweep would walk the full order once each. A miss is null,
    /// reported loudly upstream, never a guess.</summary>
    sealed class SkyPatcherServiceResolver : SkyPatcherOverlay.IFormResolver
    {
        readonly LoadOrderService _svc;
        readonly LoadOrderResolver.IndexView _view;
        readonly LoadOrderResolver.OverlaySession _session;
        readonly Dictionary<string, Dictionary<string, FormKey>> _eidsByType = new(StringComparer.OrdinalIgnoreCase);
        readonly List<PluginUnreadableException> _unreadable = new();
        readonly HashSet<string> _incompleteTypes = new(StringComparer.OrdinalIgnoreCase);   // types whose sweep missed a plugin
        bool _consumedIncomplete;

        /// <summary>Plugins an EditorID sweep could not open. Their EditorIDs are absent from the table, so a miss
        /// here is not proof the name does not exist — the callers state the gap rather than let a lookup answer
        /// "no such record" on a plugin they never read.</summary>
        public IReadOnlyList<PluginUnreadableException> Unreadable => _unreadable;

        /// <summary>Whether a lookup since the last <see cref="WatchLookups"/> was answered from a table a plugin is
        /// missing from. The table is memoized across records, so the count of unreadable plugins does not grow on
        /// the second record that consults it — this flag is what tells a caller its OWN answer is affected.</summary>
        public bool ConsumedIncompleteTable => _consumedIncomplete;

        /// <summary>Start watching lookups for a single record's replay.</summary>
        public void WatchLookups() => _consumedIncomplete = false;

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
                {
                    int before = _unreadable.Count;
                    foreach (var (candidate, _, cBody) in _view.WinnerRecordsOfType(types, _unreadable))
                        if (cBody.EditorID is { Length: > 0 } eid && !eids.ContainsKey(eid))   // first winner keeps the slot
                            eids[eid] = candidate;
                    if (_unreadable.Count > before) _incompleteTypes.Add(mutagenType);
                }
                _eidsByType[mutagenType] = eids;
            }
            if (_incompleteTypes.Contains(mutagenType)) _consumedIncomplete = true;
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

    // ---- NIF layer: read the data values inside one or many meshes (housecarl_nif_inspect) ----

    /// <summary>Inspect the data values inside one or many Skyrim meshes: capture the asset resolver once under
    /// <see cref="_gate"/>, then, per Data-relative path and outside the gate on the pinned handle-free view, resolve
    /// through the MO2 VFS to the winning copy (or the <paramref name="mod"/>-named provider), read that copy's bytes
    /// in process (a loose file, or a single entry out of a BSA — no disk extraction), and hand them to
    /// <see cref="NifService.Inspect"/>. Read-only. Results come back in input order, one per path; a per-path failure
    /// is a named <see cref="NifInspectData.Error"/> that never aborts the rest of the batch. Each path carries its
    /// full winner-to-loser provider chain and ambiguity flag, while the build-level caveats
    /// (<see cref="AssetView.BsaFailures"/>, discovery warnings) ride once on the batch so an ABSENT answer is never
    /// over-trusted. The single capture is what makes a load-order-wide sweep one call instead of one per mesh.</summary>
    public NifInspectBatchData NifInspect(IReadOnlyList<string> relPaths, string? mod)
    {
        AssetResolver.AssetView view;
        IReadOnlyList<string> warnings;
        string profileName;
        lock (_gate)
        {
            view = Assets.Capture();                              // build/refresh the asset resolver under the gate, once per batch
            warnings = _assetWarnings;
            profileName = _profileName;
        }

        // Outside the gate: the captured view is pinned and handle-free, so resolving, reading and parsing here cannot
        // race a concurrent refresh into wrongness and does not block other tools behind these file reads.
        var results = new List<NifInspectData>(relPaths.Count);
        foreach (var raw in relPaths)
        {
            var rel = (raw ?? "").Trim();
            // Per-path isolation holds by construction rather than by trusting the callee: anything unexpected from
            // one path's resolve, read or parse becomes that path's named error, never the whole batch's.
            try { results.Add(NifInspectOne(view, rel, mod)); }
            catch (Exception ex) { results.Add(NifInspectData.Fail(rel, $"unexpected error inspecting this path — {ex.GetType().Name}: {ex.Message}")); }
        }
        return new NifInspectBatchData(results, view.BsaFailures, warnings, profileName);
    }

    /// <summary>One path's inspect against the already-captured view — the per-path body of <see cref="NifInspect"/>.
    /// Every failure is a named per-path outcome, never a throw.</summary>
    static NifInspectData NifInspectOne(AssetResolver.AssetView view, string rel, string? mod)
    {
        if (rel.Length == 0)
            return NifInspectData.Fail("", "empty mesh path. Pass a Data-relative path, e.g. 'meshes\\actors\\character\\facegendata\\facegeom\\Skyrim.esm\\00000007.nif'.");

        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return NifInspectData.Fail(rel, $"invalid path — {ex.Message}"); }

        var providers = place.Sources.Select(s => new NifProvider(s.ProviderName, KindLabel(s.Kind))).ToList();

        // Pick the copy to read: the VFS winner by default, or a specific provider when mod= names one. mod= is
        // answered FIRST, ahead of the ABSENT return: naming a mod reaches that mod whether or not MO2 ticks it, and
        // a donor outside the active set is exactly a path nothing active supplies — under ABSENT its name would
        // never be consulted and the answer would read as "the donor has no mesh" (#388 ii).
        PlacementSource chosen;
        if (!string.IsNullOrWhiteSpace(mod))
        {
            var pick = NifPick(view, place, rel, mod!.Trim());
            if (pick.Error is not null)
                return new NifInspectData(rel, null, providers, place.Ambiguous, pick.Absent, null, pick.Error);
            chosen = pick.Source!;
        }
        else
        {
            if (place.Sources.Count == 0)
            {
                // A model path taken straight off a record is stored relative to meshes\, so a flat ABSENT is a dead
                // end for the normal way one arrives at a mesh. The hint is re-resolved, never guessed: a "did you
                // mean" always names a file that exists, and the weaker fallback names only the convention.
                var hint = AssetPathHint.MeshHint(view, rel);
                // Absent=true lets the renderer hedge this at the point of use against the batch-level caveats; the
                // top-of-output warning alone scrolls away in a long batch.
                return new NifInspectData(rel, null, providers, place.Ambiguous, Absent: true, null,
                    "ABSENT — no active mod or BSA provides this mesh path." + (hint is null ? "" : " " + hint));
            }
            chosen = place.Sources[0];
        }

        var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
        if (bytes is null)
            return new NifInspectData(rel, NifProviderFor(chosen), providers, place.Ambiguous,
                false, null, readErr ?? "could not read the resolved mesh bytes.");

        var outcome = NifService.Inspect(bytes);
        return new NifInspectData(rel, NifProviderFor(chosen), providers, place.Ambiguous,
            false, outcome.Inspect, outcome.Error);
    }

    /// <summary>The provider record for a CHOSEN source, carrying the off-order provenance the chain entries never
    /// need: only the copy actually read can have come from outside what the game loads.</summary>
    static NifProvider NifProviderFor(PlacementSource s)
        => new(s.ProviderName, KindLabel(s.Kind), s.OffOrder, s.OwnerEnabled);

    /// <summary>Answer <c>mod=</c> for the NIF surface: pick the named provider's copy through the ONE source policy
    /// every asset caller rides, or hand back the refusal sentence. Shared by nif_inspect and nif_set, which are the
    /// same code twice and have drifted once before.
    ///
    /// <para>Two things follow from routing it here rather than matching the name in place. Naming a mod reaches
    /// that mod's loose files AND its own root archives, ticked or not (#388), so the refusal never reports a donor's
    /// mesh as absent. And the provider names the refusal lists are spelled by the same formatter the tool prints
    /// them with, so the token in the message is the token <c>mod=</c> takes (#340).</para></summary>
    static (PlacementSource? Source, string? Error, bool Absent) NifPick(AssetResolver.AssetView view, PlacementResolution place, string rel, string mod)
    {
        // Parse, not Named: the refusal's tail teaches the '*winner' pole, so this surface has to take it. The sigil
        // is what makes that safe — '*' cannot appear in a Windows name, so a bare token is always a provider.
        var choice = AssetSourceChoice.Parse(mod);
        var pick = AssetSourceSelection.Select(place, choice, n => view.TryResolveOffOrderProvider(n, rel));
        if (pick.Verdict == AssetSourceVerdict.Selected) return (pick.Source, null, false);
        // The winner pole over an empty universe is the ABSENT case, not a named miss; say so rather than quote
        // '*winner' back as a mod name that supplies nothing. Absent travels with it: this is the same absence the
        // no-mod= arm reports, so it earns the same scan-incomplete hedging at the point of use.
        if (choice.Pole != AssetSourcePole.Named)
            return (null, "ABSENT — no active mod or BSA provides '" + rel + "', so there is no winner to read."
                        + (AssetPathHint.MeshHint(view, rel) is { } wh ? " " + wh : ""), true);
        // A named miss is NOT an absence — it says which mod, and carries its own inline scan caveat instead, so it
        // must not also draw the ABSENT-worded hedges.
        return (null, WriteSentences.PlaceSourceNamedAbsent(
            mod, rel, pick.ProviderNames,
            pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
            AssetPathHint.MeshHint(view, rel), place.ReadIncomplete), false);
    }

    /// <summary>Render an <see cref="AssetKind"/> as the tool-facing label ("loose" / "BSA"). An explicit switch
    /// rather than a ternary, so a new AssetKind renders its real name instead of being mislabelled.</summary>
    static string KindLabel(AssetKind k) => k switch { AssetKind.Bsa => "BSA", AssetKind.Loose => "loose", var other => other.ToString() };

    // ---- NIF layer: whitelisted writes into a mesh (housecarl_nif_set) ----

    /// <summary>Apply the whitelisted write ops to a mesh: resolve the Data-relative <paramref name="relPath"/> to the
    /// winning copy (or <paramref name="mod"/>'s copy), read its bytes in process, hand them to
    /// <see cref="NifService.Set"/>, which applies and verifies or refuses loudly — nothing reaches disk unless it
    /// verified — then place the verified bytes. Two lanes, mirroring the record write lanes:
    ///   • DEFAULT (non-destructive): write into a new houseCARL-owned MO2 mod folder at the same relative path, which
    ///     the modder enables and sorts above the current winner so the edited copy wins the VFS. Originals untouched,
    ///     and a BSA-packed source becomes a loose winning override this way.
    ///   • IN-PLACE (opt-in): overwrite the winning loose file where it sits, behind the same persistent first-touch
    ///     consent handshake as the record in-place lane, keyed on the resolved file path. No backup. A BSA-only winner
    ///     has no loose file to edit and is refused with the default-lane guidance.
    /// Serialized on the write gate. For the default lane, "wrote it" is not "it wins": the render says to enable and
    /// sort the fresh mod, and this never claims the fix took effect on write.</summary>
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

            // pick the copy to read/edit: the VFS winner, or a specific provider when mod= names one. mod= is
            // answered ahead of the ABSENT return, for the same reason nif_inspect answers it there.
            PlacementSource chosen;
            if (!string.IsNullOrWhiteSpace(mod))
            {
                var pick = NifPick(view, place, rel, mod!.Trim());
                if (pick.Error is not null) return NifSetResult.Fail(pick.Error, providers, profileName);
                chosen = pick.Source!;
            }
            else
            {
                if (place.Sources.Count == 0)
                {
                    var hint = AssetPathHint.MeshHint(view, rel);   // same verified re-resolve as nif_inspect's ABSENT
                    return NifSetResult.Fail(
                        $"ABSENT — no active mod or BSA provides '{rel}', so there is no copy to edit." + (hint is null ? "" : " " + hint),
                        providers, profileName);
                }
                chosen = place.Sources[0];
            }

            var (bytes, readErr) = AssetResolver.ReadPlacementSource(chosen);
            if (bytes is null) return NifSetResult.Fail(readErr ?? "could not read the resolved mesh bytes.", providers, profileName);

            // ---- apply and verify (pure; nothing is written unless this returns verified bytes) ----
            var outcome = NifService.Set(bytes, ops);
            if (outcome.Error is not null) return NifSetResult.Fail(outcome.Error, providers, profileName);
            var editedBytes = outcome.WrittenBytes!;
            var report = outcome.Report!;
            var chosenProv = NifProviderFor(chosen);
            // Whether the edited copy is the VFS winner or a mod=-named loser. Drives the "is it live" wording.
            bool editedIsWinner = place.Sources.Count > 0 && ReferenceEquals(chosen, place.Sources[0]);

            // ---- IN-PLACE lane ----
            if (inPlace)
            {
                // The lane overwrites the WINNING file with no backup, and its consent handshake is written about
                // that file. A copy the game is not loading is not that file, so this lane declines it rather than
                // mutating an original the caller reached by naming a mod.
                if (chosen.OffOrder)
                    return NifSetResult.Fail(
                        $"in-place edits the copy the game loads, but '{chosen.ProviderName}' supplied one it does not "
                        + "(see the provenance note on a read). Drop in_place to write the edited mesh into a new houseCARL "
                        + "folder instead (the default lane).", providers, profileName);
                if (chosen.Kind != AssetKind.Loose || string.IsNullOrEmpty(chosen.LooseFilePath))
                    return NifSetResult.Fail(
                        $"in-place needs a LOOSE copy to overwrite, but '{rel}' resolves to {chosen.ProviderName} ({KindLabel(chosen.Kind)}). " +
                        "Drop in_place to write a loose winning override into a new houseCARL folder instead (the default lane).", providers, profileName);
                var targetPath = chosen.LooseFilePath!;
                var meshName = Path.GetFileName(targetPath);

                // The check gates entry here; the acknowledgement is recorded below, only once the overwrite has landed
                // and verified. The parent pre-flight and the write's own failure both refuse without changing the
                // file, and neither may spend the caller's one-time confirmation.
                bool already = _store.IsInPlaceAcknowledged(targetPath);
                if (!already && !acknowledge)
                    return NifSetResult.NeedsAck(NifInPlaceHandshakeText(meshName, targetPath), chosenProv, providers, profileName);
                bool owesConsent = !already && acknowledge;

                if (InPlaceParentUnwritable(targetPath, out var why)) return NifSetResult.Fail(why, providers, profileName);
                try { AtomicFile.WriteAllBytes(targetPath, editedBytes); }
                catch (Exception ex) { return NifSetResult.Fail($"could not overwrite '{targetPath}' in place: {ex.Message}. Nothing was written.", providers, profileName); }
                long sz; try { sz = new FileInfo(targetPath).Length; } catch { sz = -1; }
                if (sz != editedBytes.Length)
                    return NifSetResult.Fail($"wrote '{meshName}' but its on-disk size ({sz}) does not match the {editedBytes.Length} verified byte(s) — verify before relying on it.", providers, profileName);

                var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "edit", subject: "file");
                return NifSetResult.OkInPlace(rel, chosenProv, providers, place.Ambiguous, editedIsWinner, report, targetPath,
                    MergeWarnings(report.Warnings, warnings, ackNote), profileName);
            }

            // ---- DEFAULT (new-folder) lane ----
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_NifEdit", new RiderNaming("patch_name")); }
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

            string? winner = providers.Count > 0 ? providers[0].Text : null;
            return NifSetResult.OkNewFolder(rel, chosenProv, providers, place.Ambiguous, report, rf.ModFolder, winner, MergeWarnings(report.Warnings, warnings, null), profileName);
        }
    }

    /// <summary>Merge the write report's notes with the asset-layer discovery warnings and an optional extra note.
    /// Both lanes surface both sets, so a preservation disclosure cannot vanish by riding the other lane's
    /// list.</summary>
    static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> reportWarnings, IReadOnlyList<string> assetWarnings, string? extra)
    {
        var list = new List<string>(reportWarnings.Count + assetWarnings.Count + 1);
        list.AddRange(reportWarnings);
        list.AddRange(assetWarnings);
        if (extra is not null) list.Add(extra);
        return list;
    }

    /// <summary>The mesh-specific first-touch in-place consent prompt. Shares its opening lead with the plugin
    /// handshake (<see cref="InPlaceHandshakeLead"/>) and diverges after it, because that prompt's wording about
    /// re-serializing a whole plugin and engine-reserved sub-0x800 records is false for a .nif: a mesh write is a
    /// whole-file NiflySharp re-serialization, then verified.</summary>
    static string NifInPlaceHandshakeText(string meshName, string path) =>
        InPlaceHandshakeLead(meshName, path, "mesh", "overwrites") +
        "  • The written mesh is a WHOLE-FILE re-serialization through NiflySharp's canonical writer (the way NifSkope / BodySlide rewrite a mesh on save), NOT a byte-surgical patch — then VERIFIED (only the value you edited changed; it reloads as a valid SE mesh).\n" +
        "  • It still refuses if the mesh can't be parsed or isn't a Skyrim SE stream.\n" +
        "  • The default lane (a NEW mod folder, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    // ---- place assets so the correct copies win the VFS (housecarl_place) ----

    /// <summary>Place one or more assets into a new houseCARL-owned MO2 mod folder so the correct copy can win the
    /// VFS. For each request: resolve its current providers (auto-resolving a source when none was named — a sole
    /// provider is used, more than one is refused as ambiguous, none is refused with guidance), read the source bytes
    /// in process (a loose file, or a single entry out of a BSA), and write them crash-atomically under the owned
    /// folder. Originals are untouched: only a fresh or houseCARL-owned folder is ever written. On failure a fresh
    /// folder that ended up with nothing placed is removed, and a partial one is kept and named. "Wrote it" is not
    /// "it wins": the fresh mod must be enabled and sorted above the current winner, which the render says, and this
    /// never claims the fix took effect on write. Serialized on the write gate.</summary>
    public PlaceOutcome PlaceAssets(IReadOnlyList<PlaceRequest> requests, string? patchName, string? into)
    {
        if (requests is null || requests.Count == 0) return PlaceOutcome.Fail("no assets to place.");

        lock (_writeGate)                                                 // one placement batch at a time: resolve, stage, commit
        {
            // Precondition: _writeGate is held for the WHOLE method. ResolvePatchModFolder and the `Assets` getter each
            // take and release _gate, so this method straddles two _gate sections — safe only because _writeGate
            // excludes every other writer and instance switch throughout, so no profile refresh can land between them.
            // Do not call PlaceOne or Assets here outside this _writeGate hold.
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, into, "houseCARL_Assets", new RiderNaming("patch")); }   // neutral default stem; a caller with a better name passes patch
            catch (InvalidOperationException ex) { return PlaceOutcome.Fail(ex.Message); }

            // One asset build for the whole batch, reentrant on _gate. Captured rather than the live resolver, so every
            // request in the batch — and the missing-root suggestion's re-resolve — answers from the same snapshot and
            // a refresh landing mid-batch cannot make two placements describe two builds.
            AssetResolver.AssetView view; IReadOnlyList<string> warnings;
            try { lock (_gate) { view = Assets.Capture(); warnings = _assetWarnings; } }
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
                var r = PlaceOne(req, view, rf.OutputDir);
                results.Add(r);
                if (r.Placed) placed++;
            }

            // Nothing placed into a fresh folder → remove the orphan. A reused into= folder belongs to the user and is
            // never touched. A partial fresh folder is kept and its path surfaced.
            string? leftover = placed == 0 ? RemoveOrNameRiderResidue(rf) : null;
            return new PlaceOutcome(results, placed > 0 ? rf.ModFolder : null, warnings, leftover, null);
        }
    }

    /// <summary>Place one asset: validate the destination rel-path (drive-rooted and '..' paths are rejected by the
    /// resolver's own check), get the source bytes (explicit source= or auto-resolve), and write them atomically under
    /// <paramref name="outDir"/>. Reports the CURRENT VFS winner so the caller knows what to sort the fresh mod above —
    /// the placed file does NOT win until the mod is enabled + sorted (the fresh folder isn't in the active profile yet).
    /// A per-asset failure is a recoverable named error, never a thrown batch abort.</summary>
    PlaceResult PlaceOne(PlaceRequest req, AssetResolver.AssetView view, string outDir)
    {
        string rel;
        try { rel = AssetResolver.ValidateRelPath(req.AssetPath); }
        catch (ArgumentException ex) { return PlaceResult.Fail(req.AssetPath, ex.Message); }

        var res = view.ResolveForPlacement(rel);                         // rel already validated — won't throw
        var winner = res.Sources.Count > 0 ? DescribeSource(res.Sources[0]) : null;

        // ---- source bytes: an ON-DISK source= is read as named; anything else resolves through the VFS ----
        // Three source shapes reach here: an on-disk file the caller named exactly (a FULLY-QUALIFIED path, which a
        // '.bsa' must also be to count as an archive, or a '<bsa>|<entry>' pair), a DATA-RELATIVE path resolved
        // through the VFS under a pole, and no source at all — which
        // is the same VFS lane pointed at the destination path. The last two share one code path because they are
        // one question ("which provider supplies this Data-relative path") asked about different paths.
        byte[] bytes; string sourceDesc;
        // The mod folder an off-order read was served from, or null when the bytes came from the active order. Typed
        // rather than baked into sourceDesc, because the render owns the sentence and a caller cannot infer this from
        // a provider name.
        string? offOrderProvider = null;
        bool offOrderOwnerEnabled = false;                 // WHICH off-order reason — an unticked mod, or a ticked mod's unloaded archive
        // One normalization point for source=, ahead of both the classification and every consumer: whatever trimming
        // the routing decision depends on must have happened before this line, or a quoted Data-relative source
        // classifies one way and is read another.
        var explicitSrc = NormalizeSourceArg(req.Source);
        var providerSel = req.SourceProvider?.Trim();
        if (!string.IsNullOrEmpty(explicitSrc) && !IsVfsSource(explicitSrc!))
        {
            // An on-disk source already IS one exact copy, so a pole cannot apply to it — said, never dropped.
            if (!string.IsNullOrEmpty(providerSel))
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceProviderNeedsRelPath, winner);
            var (b, desc, err) = ReadExplicitSource(explicitSrc!, rel);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!; sourceDesc = desc!;
        }
        else
        {
            // The SOURCE path: the Data-relative one the caller named, else the destination (the original lane).
            bool sourceNamed = !string.IsNullOrEmpty(explicitSrc);
            string srcRel = rel;
            if (sourceNamed)
            {
                try { srcRel = AssetResolver.ValidateRelPath(explicitSrc!); }
                catch (ArgumentException ex) { return PlaceResult.Fail(rel, $"source '{explicitSrc}': {ex.Message}", winner); }
            }
            var srcRes = sourceNamed ? view.ResolveForPlacement(srcRel) : res;
            // The off-order lane is handed to the one source policy rather than spelled here: naming a mod means that
            // mod's copy whether or not MO2 ticks it, and every caller reaches that rule through this call.
            var choice = AssetSourceChoice.Parse(providerSel);
            var pick = AssetSourceSelection.Select(srcRes, choice,
                                                   n => view.TryResolveOffOrderProvider(n, srcRel));

            // Both named-provider misses render as one refusal: they are the same fact — the named provider does not
            // supply this path — differing only in which places were searched and whether there is anyone else to
            // suggest, and the sentence takes both as inputs.
            // Gated on the POLE, not on "a provider string was passed": '*winner' is a non-empty selector that parses
            // to the winner pole, and gating on the string would quote it back as if it were a mod name and claim a
            // folder of that name had been searched, which is impossible since '*' cannot appear in a folder name.
            if (choice.Pole == AssetSourcePole.Named
                && pick.Verdict is AssetSourceVerdict.NamedAbsent or AssetSourceVerdict.NoProvider)
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceNamedAbsent(
                    providerSel!, srcRel, pick.ProviderNames,
                    pick.OffOrderReason, pick.OffOrderUnreadableName, pick.OffOrderUnreadableCause,
                    // The root-prefix hint: a path taken off a record is stored relative to meshes\ or textures\, and
                    // naming a provider does not stop that being the caller's actual mistake. Verified before it is
                    // offered, like every other site that shows it.
                    AssetPathHint.AssetRootHint(view, srcRel),
                    // srcRes, not res: the caveat has to describe the scan that answered for the SOURCE path.
                    srcRes.ReadIncomplete), winner);
            if (pick.Verdict == AssetSourceVerdict.Ambiguous)
                return PlaceResult.Fail(rel, WriteSentences.PlaceSourceAmbiguous(srcRel, pick.ProviderNames), winner);
            if (pick.Verdict == AssetSourceVerdict.NoProvider && sourceNamed)
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides the source '{srcRel}'."
                    + (AssetPathHint.AssetRootHint(view, srcRel) is { } srcHint ? " " + srcHint : "")
                    + (srcRes.ReadIncomplete ? " " + WriteSentences.PlaceSourceScanIncomplete : "")
                    // The other dead end: a Data-relative source= with no source_provider=. Same sentence as the
                    // auto-resolve refusal below, so a caller who passed a source and no provider gets the same route
                    // out.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked,
                    winner);
            if (pick.Verdict == AssetSourceVerdict.NoProvider)
            {
                // A path taken off a record is stored relative to its root folder, so passing it verbatim is the
                // normal way one arrives here. Both roots are tried (this lane cannot know the path's kind) and
                // verified by re-resolving, so a suggestion always names a copy that really is provided and silence
                // is the default. Not offered on the explicit-source= arm above: placing a NEW file at a path
                // nothing provides is legitimate there.
                var hint = AssetPathHint.AssetRootHint(view, rel);
                // Order is load-bearing: the hint sits directly after the sentence about the PATH and before the
                // "Pass source=" fallback. It names a destination, not a source, and must not trail an imperative
                // about sources or it reads as one.
                return PlaceResult.Fail(rel,
                    $"nothing in the active load order provides '{rel}', so there is no copy to auto-place."
                    + (hint is null ? "" : " " + hint)
                    + " Pass source= the copy to place — a Data-relative path (resolved through the VFS, with"
                    + " source_provider= to name which mod's copy), a full loose path, '<archive.bsa>|<entry>', or a '.bsa' path."
                    // The commonest way of reaching this refusal is that the only copy lives in a mod MO2 does not
                    // load, and naming a mod reaches it, so the caller who is here is the one who needs to be told.
                    + " " + WriteSentences.PlaceSourceNameReachesUnticked
                    + (res.ReadIncomplete ? " " + WriteSentences.PlaceSourceScanIncomplete : ""),
                    winner);
            }
            var (b, desc, err) = ReadResolvedSource(pick.Source!);
            if (err is not null) return PlaceResult.Fail(rel, err, winner);
            bytes = b!;
            if (pick.Source!.OffOrder) { offOrderProvider = pick.Source.ProviderName; offOrderOwnerEnabled = pick.Source.OwnerEnabled; }
            // A source read from a different path than the destination is a rename and the render has to say so,
            // since "placed from ModX" alone hides that the bytes are another file's. Keyed on whether the two paths
            // differ, not on whether a source was named — a provider-scoped placement of the same path is not a
            // rename.
            sourceDesc = sourceNamed && !SameAssetPath(srcRel, rel) ? $"{srcRel} — {desc}" : desc!;
        }

        // ---- crash-atomic place under the owned folder (originals untouched; same-volume staging done in the core) ----
        var dest = Path.Combine(outDir, rel);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            AtomicFile.WriteAllBytes(dest, bytes);
        }
        catch (Exception ex) { return PlaceResult.Fail(rel, $"could not write '{rel}' into the patch folder: {ex.Message}", winner); }

        // ---- integrity: the on-disk size matches the source bytes, so success is never claimed falsely ----
        // Truncation / short-write detection, not a content hash: the bytes are in memory and the swap is atomic, so a
        // same-length corruption is not a reachable failure here. Defensive; AtomicFile's swap is the real guarantee.
        long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
        if (size != bytes.Length)
            return PlaceResult.Fail(rel,
                $"wrote '{rel}' but its on-disk size ({size}) does not match the {bytes.Length} source byte(s) — verify before relying on it.", winner);
        return new PlaceResult(rel, true, bytes.Length, sourceDesc, winner, null)
            { SourceOffOrderProvider = offOrderProvider, SourceOffOrderOwnerEnabled = offOrderOwnerEnabled };
    }

    /// <summary>Read an ON-DISK source= the caller named exactly. Forms: "&lt;archive.bsa&gt;|&lt;entry&gt;" (a specific
    /// BSA entry, split on the FIRST '|'); a path ending ".bsa" (the entry is the destination rel-path — the FaceGen
    /// case, where the entry inside the BSA IS the Data-relative path); a FULLY-QUALIFIED path (a loose file on disk).
    /// A Data-relative source never reaches here — <see cref="IsVfsSource"/> routes it to the VFS lane, which is the one
    /// that can say which provider's copy. Returns the bytes plus a human description, or a named error for a
    /// missing file, missing entry, or unreadable archive.</summary>
    static (byte[]? bytes, string? desc, string? error) ReadExplicitSource(string source, string destRel)
    {
        // The whole-string trim and unquote happened in NormalizeSourceArg, ahead of the routing decision. The
        // per-part trims below stay: each side of a '<archive>|<entry>' pair can carry its own quotes, which no
        // whole-string normalization can reach.
        int bar = source.IndexOf('|');
        if (bar >= 0)
            return ReadBsaEntry(source[..bar].Trim().Trim('"'), source[(bar + 1)..].Trim().Trim('"'));
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
    /// provider reads off disk; a BSA provider extracts its single entry natively. A named error if the resolved copy
    /// vanished between resolve and read, or the archive cannot be read.</summary>
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
    /// <see cref="AssetResolver.TryReadArchiveEntry"/>). Named errors for a missing archive, an entry not inside it,
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

    /// <summary>Do two Data-relative paths name the SAME asset, by the key the VFS itself resolves on? Both inputs
    /// have already been through <see cref="AssetResolver.ValidateRelPath"/>, which folds separators and any leading
    /// separator; the remaining axis is CASE, and the asset layer's own lookups are ordinal-case-insensitive. So a
    /// raw string compare would call <c>Meshes/X.NIF</c> and <c>meshes\x.nif</c> two different files and report a
    /// rename between one file and itself.</summary>
    static bool SameAssetPath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The ONE normalization of a caller's source= — trim, then strip surrounding quotes — applied before
    /// the routing decision and before every consumer, so no two of them can disagree about what the string is.
    /// Blank becomes null (the "no source" lane) rather than an empty path nobody can resolve.</summary>
    static string? NormalizeSourceArg(string? source)
    {
        var s = source?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        s = s.Trim('"').Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Whether a <c>source=</c> is one a provider pole can even apply to — nothing named (the pole then
    /// says whose copy of the DESTINATION to place), or a Data-relative path resolved through the VFS. False for an
    /// on-disk file, which already names one exact copy: <see cref="PlaceOne"/> refuses a pole there rather than
    /// dropping it, so a CALL-LEVEL pole must not be attached to such a member in the first place. Same routing
    /// decision as the placer, through the same two helpers, so the two cannot drift.</summary>
    internal static bool SourceTakesAProvider(string? source)
    {
        var s = NormalizeSourceArg(source);
        return string.IsNullOrEmpty(s) || IsVfsSource(s!);
    }

    /// <summary>Does this source= name a copy through the VFS (a Data-relative path) rather than one exact file on
    /// disk? Expects an already-<see cref="NormalizeSourceArg"/>d string. The on-disk forms are tested in the same
    /// order <see cref="ReadExplicitSource"/> routes them.
    /// <para>Fully qualified, not merely rooted: on Windows <c>Path.IsPathRooted</c> is true for a leading '\' or '/',
    /// but <c>AssetResolver.Normalize</c> trims exactly those, so <c>\meshes\…</c> is a legal Data-relative
    /// destination. A path is on-disk only when it names a volume (<c>C:\…</c>) or a UNC share.</para>
    /// <para>The qualified test runs BEFORE the extension test, because an extension says nothing about where the
    /// file is: a mod can legitimately ship <c>meshes\thing.bsa</c> as a Data-relative asset. A '.bsa' means "an
    /// archive to open" only once the caller is known to have named a file on disk.</para></summary>
    static bool IsVfsSource(string source)
    {
        if (source.IndexOf('|') >= 0) return false;                      // '<archive.bsa>|<entry>' — an entry, not a path
        if (!Path.IsPathFullyQualified(source)) return true;             // Data-relative ⇒ the VFS answers, whatever it ends in
        return false;                                                    // a volume or UNC path ⇒ one exact file (or archive) on disk
    }

    /// <summary>Whole-order stats (forces the lazy build). A test seam: the probes and tests warm the lazy index
    /// through it and read the epoch and counters off it. No shipped caller — the product reads the same numbers
    /// through <see cref="StatusData"/>.</summary>
    public (int plugins, int records, int conflicts, int maxDepth, IReadOnlyList<string> loadFailures, string epoch) Stats()
    {
        var view = Resolver.Capture();          // one build for every counter in the line
        return (view.PluginCount, view.RecordCount, view.ConflictCount, view.MaxDepth, view.LoadFailures, view.Epoch);
    }

    /// <summary>Diagnostic snapshot for housecarl_load_order_status: the current enabled/disabled composition, read
    /// fresh from the profile text files (cheap, no folder walk, so a just-toggled mod or plugin shows immediately),
    /// plus the resolver's resolved-plugin count and warnings from its last build, plus a staleness flag if the
    /// profile files changed since that build. Forces the lazy resolver build.</summary>
    public LoadOrderStatusData StatusData()
    {
        // The view AND the per-build fields beside it (warnings, staleness, profile dir) are snapshotted under ONE gate
        // hold: read outside the gate, a concurrent freshness rebuild could compose one status line from two adjacent
        // builds — the count from one, the warnings from another. The fresh composition stays outside the gate
        // deliberately: it is always current and is not judged against the resolver's build.
        LoadOrderResolver.IndexView view; IReadOnlyList<string> warnings; bool profileChanged; string profileDir; string profileName; string? instanceDir;
        lock (_gate)
        {
            view = Resolver.Capture();                             // force build/refresh; one build for count + exclusions
            warnings = _orderWarnings;
            profileChanged = ProfileFilesChanged();
            profileDir = _profileDir;
            profileName = _profileName;                            // captured under the same gate — one snapshot, never re-derived at render
            instanceDir = _instanceDir;                            // the configured MO2 instance folder; null ⇒ explicit-paths / unconfigured mode
        }
        var comp = Mo2LoadOrder.ReadComposition(profileDir);       // fresh composition (always current)
        return new LoadOrderStatusData(
            comp, warnings, view.PluginCount, _maxPlugins, profileChanged, profileDir, profileName, instanceDir, view.ExcludedPlugins,
            view.Epoch);
    }

    /// <summary>The LOCALIZED header flag of ONE plugin, for housecarl_load_order_status' lookup= (#376): a localized
    /// plugin's text lives in .STRINGS files rather than in the plugin, which is what the in-place write lanes refuse
    /// on, so a caller can see that refusal coming instead of meeting it mid-job. Null when the name is not a plugin
    /// at all (a mod folder, a typo — nothing has a header to read); otherwise the three-way read, Unreadable
    /// included — never a bool.
    /// <para>An INACTIVE plugin gets an answer too: the resolver indexes only the active order, so its path comes from
    /// the same on-disk locate every other lane uses. Reading an inactive plugin is a surface houseCARL advertises, and
    /// a plugin answer with the localized half silently missing is the worse outcome. A name several mod folders
    /// provide is answered from the copy MO2 priority serves, not called unreadable.</para></summary>
    public LocalizedFlagRead? PluginLocalizedFlag(string pluginName)
    {
        LoadOrderResolver.IndexView view;
        lock (_gate) { view = Resolver.Capture(); }
        if (view.PluginPath(pluginName) is { } activePath) return WriteEngine.PluginIsLocalized(activePath);

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch { return null; }
        Mo2Composition comp;
        try { comp = Mo2LoadOrder.ReadComposition(profileDir); }
        catch { return null; }
        // Only a name the profile lists as a plugin: a mod folder and a typo both have no header, and inventing an
        // UNKNOWN line for them would claim there is a plugin here whose flag simply could not be read.
        bool isPlugin = comp.OrderedPluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                        || comp.InactivePluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                        || comp.ImplicitPluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (!isPlugin) return null;
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, pluginName, null, offerModParam: false);
        if (loc.Path is { } path) return WriteEngine.PluginIsLocalized(path);
        // Several folders provide the name: every copy is readable and MO2 priority already decides which one serves,
        // so the served copy answers — the same rule the locate itself judges 'serves' by. UNKNOWN here would claim a
        // header read failed when none was attempted.
        if (loc.Ambiguous is { Count: > 0 } hits && hits.FirstOrDefault(h => h.Enabled) is { } served)
            return WriteEngine.PluginIsLocalized(served.Path);
        // Listed as a plugin, and no file behind the name serves it: the flag is not established, which is what the
        // third value says.
        return LocalizedFlagRead.Unreadable;
    }

    /// <summary>Read MO2's OWN local Nexus update cache — the modid / version / newestVersion / ignoredVersion /
    /// lastNexusUpdate fields in every managed mod's meta.ini — with NO network (MO2 already paid the API cost). The
    /// cheap local pre-filter for update triage: it names which mods MO2 already learned a newer version for, plus the
    /// raw fields so the caller can verify online. Enabled/disabled comes from the ACTIVE profile. Config-gated and uses
    /// the same lazy path derivation as the other reads; a missing mods folder is named, never a silent empty.
    /// Only Nexus-linked mods (a real modid) become entries; hand-installed mods / separators are counted, not listed.</summary>
    public UpdateCacheData UpdateCache()
    {
        string modsDir, profileDir; string? instanceDir;
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();               // fresh install → the tool surfaces the prompt for the MO2 path
            EnsurePathsDerived();                                  // instance mode: derive _modsDir/_profileDir from the ini (throws if unusable)
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

    /// <summary>Inspect a named profile's enabled/disabled composition without switching to it. Instance mode only.
    /// The profiles root is the parent of the active profile's dir, so MO2's base_directory redirect is honored by
    /// construction and every profile is a sibling folder there. Reads with the cheap text-only
    /// <see cref="Mo2LoadOrder.ReadComposition"/>, not <see cref="Mo2LoadOrder.Build"/>, which walks every enabled mod
    /// folder — so inspecting an inactive profile never builds the record index and never changes the active profile.
    /// Explicit-paths mode has no profiles root, so a named read refuses loudly there rather than enumerate an
    /// arbitrary folder. A <paramref name="requested"/> name matching no profile is reported with the available names,
    /// never as a silently-empty composition; a null or blank name returns just the available list. Case-insensitive
    /// name match.</summary>
    public NamedProfileResult NamedProfileComposition(string? requested)
    {
        string? instanceDir; string profilesRoot;
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();              // fresh install → the tool returns the prompt for the MO2 path
            EnsurePathsDerived();                                 // instance mode: derive the active ProfileDir (cheap ini read; throws if the instance is unusable)
            instanceDir = _instanceDir;
            profilesRoot = instanceDir is null ? "" : (Path.GetDirectoryName(_profileDir.TrimEnd('\\', '/')) ?? "");
        }

        var name = string.IsNullOrWhiteSpace(requested) ? null : requested.Trim();
        if (instanceDir is null)                                  // explicit-paths mode — no profiles root; the tool renders the instance-mode-only message
            return new NamedProfileResult(InstanceMode: false, AvailableProfiles: Array.Empty<string>(), RequestedName: name, ResolvedProfileDir: null, Composition: null, Warnings: Array.Empty<string>());

        var available = ListProfiles(profilesRoot);              // directory listing outside the gate — no lock held over I/O
        if (name is null)                                        // no name → the discovery list only
            return new NamedProfileResult(true, available, null, null, null, Array.Empty<string>());

        var match = available.FirstOrDefault(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        if (match is null)                                       // named profile not found → report it with the available names, never an empty composition
            return new NamedProfileResult(true, available, name, null, null, Array.Empty<string>());

        var dir = Path.Combine(profilesRoot, match);
        var warnings = new List<string>();                       // read notes (e.g. a missing modlist.txt), so a 0-mod profile is not mistaken for empty
        var comp = Mo2LoadOrder.ReadComposition(dir, warnings);  // cheap text parse of THAT profile's loadorder/modlist/plugins — no index build, no switch
        return new NamedProfileResult(true, available, match, dir, comp, warnings);
    }

    /// <summary>The usable profile names under <paramref name="profilesRoot"/>: each MO2 profile is one subfolder, and
    /// a profile opened at least once has a loadorder.txt. Folders without one — a never-opened profile, or a stray
    /// directory — are skipped, so the list never offers a folder that would read back as an all-zero composition.
    /// Sorted case-insensitively. Never throws: an unreadable or absent root yields an empty list, so the caller says
    /// "no profiles" rather than failing the whole status read.</summary>
    static IReadOnlyList<string> ListProfiles(string profilesRoot)
    {
        if (profilesRoot.Length == 0) return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(profilesRoot)
                .Where(d => File.Exists(Path.Combine(d, "loadorder.txt")))   // an opened MO2 profile has loadorder.txt — skip stray/never-opened folders
                .Select(d => Path.GetFileName(d.TrimEnd('\\', '/')))
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { return Array.Empty<string>(); }                  // root vanished / access denied — empty, not a thrown status read
    }

    /// <summary>True if any of the three MO2 profile files' mtimes differs from the last build's baseline — the user
    /// toggled mods or plugins, re-sorted, or restored a backup, so the resolver's set is behind the live profile.
    /// Compared by value (!=), like the resolver's own plugin sweep: a restored backup carries an OLDER mtime, which
    /// an is-newer comparison would miss. Caller holds <see cref="_gate"/>.</summary>
    bool ProfileFilesChanged()
    {
        if (_profileDir.Length == 0) return false;                 // test seam / not yet derived — nothing to compare against
        for (int i = 0; i < ProfileFileNames.Length; i++)
            if (SafeMtime(Path.Combine(_profileDir, ProfileFileNames[i])) != _profileMtimes[i]) return true;
        return false;
    }

    /// <summary>The three profile files' current mtimes, in <see cref="ProfileFileNames"/> order — the freshness
    /// baseline a build records. Stat BEFORE the read it baselines. Caller holds <see cref="_gate"/>.</summary>
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

    /// <summary>Lazy freshness, run on each tool call once the snapshot exists. Two mtime signals: in instance mode,
    /// whether the user switched profiles (ModOrganizer.ini changed), which re-derives the roots and re-resolves
    /// against the new profile; otherwise whether the active profile's files changed, which re-resolves cheaply and
    /// pays the deep re-index only when the resolved order actually changed, so a no-plugin toggle costs almost
    /// nothing. It never fires between tool calls — there is no watcher and no loop — so an actively-sorting user
    /// cannot make the server thrash. Caller holds <see cref="_gate"/>; <see cref="_resolver"/> is non-null.</summary>
    void RefreshOnProfileChange()
    {
        if (RederiveIfIniChanged()) return;                      // instance mode: a profile switch already re-derived and re-resolved
        if (!ProfileFilesChanged()) return;                      // nothing touched the active profile → nothing to do
        ReResolve();
    }

    /// <summary>Instance mode only: if ModOrganizer.ini changed since we last read it AND the user switched profiles (or
    /// moved the game path), re-derive ProfileDir/ModsDir/DataDir + the active profile and re-resolve against the new
    /// profile. This is how a mid-session profile switch is followed — lazily, on the next tool call, by the same cheap
    /// mtime model as the per-profile-file check. Returns true iff it handled a switch (caller then skips the per-file check).
    /// Tolerates a transient/invalid read (MO2 mid-write): keeps the last good set and retries next call. Caller holds the gate.</summary>
    bool RederiveIfIniChanged()
    {
        if (_instanceDir is null) return false;                  // explicit/override mode — no ini to watch
        var ini = Mo2Instance.IniPath(_instanceDir);
        if (!File.Exists(ini)) return false;                     // missing/mid-replace → keep last good, retry next call
        var iniMtime = SafeMtime(ini);                           // stat BEFORE the read: an ini write during/after TryResolve is caught next call
        if (iniMtime == _iniMtime) return false;                 // compared by value — a restored-backup ini carries an older mtime and is a change too
        if (!Mo2Instance.TryResolve(_instanceDir, out var p) || p is null) return false;   // mid-write/invalid → keep last good, retry next call
        _iniMtime = iniMtime;                                    // advance only on a clean read
        bool switched = !PathEq(p.ProfileDir, _profileDir) || !PathEq(p.ModsDir, _modsDir) || !PathEq(p.DataDir, _dataDir)
                        || !PathEq(p.OverwriteDir, _overwriteDir);
        if (!switched) return false;                             // ini touched but nothing we resolve from changed
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        System.Threading.Interlocked.Increment(ref _gameRootsGen);   // the game roots moved → the runtime memo re-probes
        InvalidateClassParents();                                // the mods tree may have moved — drop the cached hierarchy with it
        ReResolve();                                             // a new profile ⇒ the order differs ⇒ ReResolve deep-re-indexes
        return true;
    }

    /// <summary>The cheap re-read against the current profile roots: re-list the winning plugin paths from the text
    /// files, and pay the deep re-index only when the resolved set or order actually changed. Caller holds the gate;
    /// <see cref="_resolver"/> is non-null. Used by both freshness signals (active-profile change + profile switch).</summary>
    void ReResolve()
    {
        var profileMtimes = StatProfileFiles();                  // stat BEFORE the read: a write during the re-read is caught next call, not missed
        var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir);
        var paths = order.OrderedPaths;
        if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();

        if (paths.Count > 0 && !paths.SequenceEqual(_resolvedPaths, StringComparer.OrdinalIgnoreCase))
        {
            // The active set or order genuinely changed → re-take the snapshot (the deep re-index). Build FIRST so the
            // old snapshot survives if it throws, and only then dispose and swap. Guarded on `_resolver is not null`:
            // an asset-only query can drive this re-resolve before any record index exists, and must not pay the heavy
            // build here — the record getter builds fresh against these paths on its own next call.
            InvalidateAssetResolver();   // the active mod/archive set changed → the asset resolver rebuilds lazily
            if (_resolver is not null)
            {
                // The rebuild must carry the explainer too, or a profile change — the very act that creates an
                // unticked plugin — would silently drop every refusal back to the flat not-found.
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
            // The profile was touched but the resolved plugin order is identical (e.g. a no-plugin mod toggled), so no
            // deep re-index. A plugin-less toggle still changes the loose roots and active-archive set, so the asset
            // resolver is dropped to rebuild; the freshness baseline advances so the staleness flag clears.
            InvalidateAssetResolver();
            _orderWarnings = order.Warnings;
            _profileMtimes = profileMtimes;
        }
        // paths.Count == 0 is almost certainly a transient mid-write read: keep the last good snapshot and do NOT
        // advance the baseline, so the next tool call re-checks and recovers once MO2 finishes writing.
    }

    /// <summary>Instance mode: on the first resolver build, read ModOrganizer.ini and derive ProfileDir, ModsDir,
    /// DataDir and the active profile, throwing a message naming what is missing if the instance is not usable.
    /// Explicit mode and re-derives (paths already non-empty) are no-ops. Stamps the ini-read baseline so the
    /// profile-switch check has a reference point. Caller holds the gate.</summary>
    void EnsurePathsDerived()
    {
        if (_instanceDir is null) return;                        // explicit mode — roots configured directly
        if (_profileDir.Length > 0) return;                      // already derived (a prior build / SetInstance); RederiveIfIniChanged owns later updates
        var iniMtime = SafeMtime(Mo2Instance.IniPath(_instanceDir));   // stat BEFORE the read: an ini write during/after Resolve is caught next call
        var p = Mo2Instance.Resolve(_instanceDir);               // throws, naming the missing piece, if this is not a usable instance
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        _iniMtime = iniMtime;
        InvalidateClassParents();                                // _modsDir just gained a value — a cache built before derivation is baseline-only
    }

    static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether houseCARL has an MO2 location to resolve against. False on a fresh install with no config: the
    /// server still runs, and every tool asks for the path until <see cref="SetInstance"/> is called.</summary>
    public bool IsConfigured { get { lock (_gate) { return _configured; } } }

    /// <summary>The active profile name (instance mode: ModOrganizer.ini selected_profile; explicit mode: the profile folder
    /// name); "" when unconfigured. For the status surface.</summary>
    public string ProfileName { get { lock (_gate) { return _profileName; } } }

    /// <summary>The game install directory the load order points at — DataDir's parent, since DataDir is
    /// gamePath\Data — or null when it is not derivable. Used as the compile lane's auto-detect hint, because the CK
    /// installs its compiler at &lt;gamePath&gt;\Papyrus Compiler\PapyrusCompiler.exe. Null-safe by contract: this is
    /// best-effort plumbing, so a failure here must fall through to the prompt rather than throw and abort the
    /// compile. It returns null when unconfigured, when the instance is unusable, or when DataDir has not been derived
    /// yet. Works in explicit mode too, where DataDir is set directly.</summary>
    public string? GameDirOrNull()
    {
        lock (_gate)
        {
            if (!_configured) return null;
            try { EnsurePathsDerived(); }
            catch { return null; }                                  // unusable instance → no hint; the caller's own config check names the real problem
            return _dataDir.Length > 0 ? Path.GetDirectoryName(_dataDir.TrimEnd('\\', '/')) : null;
        }
    }

    /// <summary>The game directories to search for the Creation Kit's compiler, in priority order. [0] is the load
    /// order's own game dir (<see cref="GameDirOrNull"/>), correct when MO2 points straight at a real CK-equipped
    /// install; then the located real Skyrim SE install, because in an MO2 "Stock Game" setup the load order points at
    /// a copy that has neither the CK nor the vanilla script sources. De-duplicated, nulls dropped. Best-effort and
    /// null-safe end to end: the locator reads the registry and Steam, so a miss or a throw yields fewer hints and
    /// never aborts the compile.
    /// <para>Load-bearing: the compile lane derives the vanilla SOURCE folder from the RESOLVED COMPILER's own game
    /// dir, not from these hints and not from the data dir, so once the compiler resolves to the Steam install its
    /// sibling Data\Source\Scripts is used rather than the Stock Game copy's, which usually has none.</para></summary>
    // InstalledGameRuntime's memo: the resolved exe is re-validated by a cheap mtime stat per call, and a probed miss
    // is generation-stable so a permanently-null answer does not re-pay the locator walk on every tool call.
    // _gameRootsGen is the invalidation signal: every site that re-points the game roots bumps it, and a memo cached at
    // an older generation re-probes — otherwise an instance switch would keep adjudicating version-locked plugins
    // against the previous install's exe. A lock-free Interlocked counter, not a locked reset: the bump sites hold
    // _gate, and taking _runtimeGate under _gate would invert the _runtimeGate-then-_gate order below and deadlock.
    readonly object _runtimeGate = new();
    int _gameRootsGen;
    int _runtimeGen = -1;   // generation the memo was cached at; -1 = never probed
    string? _runtimeExe, _runtimeVersion;
    DateTime _runtimeExeMtime;

    /// <summary>The INSTALLED game runtime version — the dotted file version of the SkyrimSE.exe the load order runs
    /// (e.g. "1.6.1170.0") — or null when it cannot be resolved. This is what turns a version-locked SKSE plugin's
    /// compat list from "verify against your game version" into PASS/FAIL. Candidates are exactly
    /// <see cref="CompilerGameDirHints"/>, load-order game dir first: an MO2 "Stock Game" setup launches that copy's
    /// exe and downgrade patchers rewrite it in place, so its version is the truth. Best-effort and null-safe: a miss
    /// degrades the finding wording rather than failing a tool. Memoized, with the resolved exe re-validated by mtime
    /// per call. Known residual: if MO2 launches an exe that is in neither location, this can describe a different
    /// binary, so the renders name the version they adjudicated against.</summary>
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
            // The bundled GameFinder locator (Steam/GOG/Xbox) via Mutagen: finds the real Skyrim SE install, where the
            // Creation Kit and sources live, regardless of where MO2's load order points.
            if (new Mutagen.Bethesda.Installs.GameLocator().TryGetGameDirectory(
                    Mutagen.Bethesda.GameRelease.SkyrimSE, out var dir) && !string.IsNullOrWhiteSpace(dir.Path))
                hints.Add(NormalizeGameDir(dir.Path));
        }
        catch { /* locator / registry hiccup → just the load-order hint; the prompt still names it */ }
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The locator returns the game-install ROOT (the folder holding the exe + Data); defend against a future build
    /// handing back the Data folder itself by stepping up one level so the &lt;game&gt;\Papyrus Compiler\ join stays correct.</summary>
    static string NormalizeGameDir(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return Path.GetFileName(t).Equals("Data", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(t) ?? t) : t;
    }

    /// <summary>Point houseCARL at an MO2 instance folder — first-run setup and switching between instances. Validates
    /// it (<see cref="Mo2Instance.Resolve"/> throws a clear message if it is not usable, and nothing is changed or
    /// persisted on failure), re-points the live service (deriving the roots and active profile and dropping the cached
    /// resolver so the next tool call rebuilds), and persists the choice so it survives a restart. Returns the derived
    /// paths and whether the persist succeeded.</summary>
    public (Mo2InstancePaths paths, bool persisted, string? persistError, string? persistNote) SetInstance(string instanceDir)
    {
        // The ini baseline is statted BEFORE Resolve reads the instance: an MO2 ini write landing between the read and
        // the stamp would otherwise be absorbed into the baseline and its profile switch would stay invisible for the
        // process lifetime. Same discipline as every other baseline here.
        var iniMtime = SafeMtime(Mo2Instance.IniPath(instanceDir.Trim()));
        var paths = Mo2Instance.Resolve(instanceDir);            // throws if not a usable MO2 instance — the tool renders the reason
        lock (_writeGate)                                        // an instance switch waits for any in-flight write, so one can never tear across instances
        lock (_gate)
        {
            _instanceDir = paths.InstanceDir;
            _dataDir = paths.DataDir; _modsDir = paths.ModsDir; _profileDir = paths.ProfileDir; _profileName = paths.ProfileName;
            _overwriteDir = paths.OverwriteDir;
            _iniMtime = iniMtime;
            _configured = true;
            _resolver?.Dispose(); _resolver = null;              // force a rebuild against the new instance on the next query
            _assetResolver?.Dispose(); _assetResolver = null;    // the asset resolver rebuilds against the new instance too
            _resolvedPaths = Array.Empty<string>();
            _profileMtimes = new DateTime[ProfileFileNames.Length];   // unset — the next build records fresh baselines against the new profile
            _orderWarnings = Array.Empty<string>();
            InvalidateClassParents();                            // every sibling cache drops on a switch — the hierarchy too
            System.Threading.Interlocked.Increment(ref _gameRootsGen);   // a new instance may be a different game install — the runtime memo must re-probe rather than adjudicate against the old exe
        }
        var (persisted, persistError, persistNote) = PersistInstanceDir(paths.InstanceDir);
        return (paths, persisted, persistError, persistNote);
    }

    /// <summary>Persist the chosen instance dir through the shared <see cref="UserConfigStore"/> (read-modify-write) so
    /// it survives a restart and coexists with any saved tool paths — the store never clobbers the other field. A write
    /// failure is reported rather than swallowed: the session still works, but the user is told the choice will not
    /// survive a restart. <c>note</c> carries a corrupt-file recovery, rendered even on success.</summary>
    (bool ok, string? error, string? note) PersistInstanceDir(string instanceDir)
        => _store.Update(c => c.Mo2InstanceDir = instanceDir);

    /// <summary>The prompt shown while unconfigured: ask the user which MO2 instance to use rather than silently
    /// picking among several, then call the setup tool. Tools return it via <see cref="ConfigPromptOrNull"/> and the
    /// <see cref="Resolver"/> getter throws it as a backstop, so both must say the same thing — hence one
    /// string.</summary>
    const string NotConfiguredText =
        "houseCARL has no Mod Organizer 2 instance configured yet. Ask the user which MO2 instance folder to use — the " +
        "folder that contains ModOrganizer.ini (for a Wabbajack / portable list, that's the list's install folder). You " +
        "may help locate it, but do NOT silently pick one when more than one MO2 install exists: list the candidates you " +
        "found and let the user choose. State which folder you're using, then call " + ToolNames.SetMo2Instance + " with that path.";

    /// <summary>Tools call this FIRST: returns the unconfigured prompt as a normal result string when unconfigured,
    /// else null. Preferred over letting <see cref="Resolver"/> throw, because the MCP framework rewrites a thrown
    /// exception to a generic "An error occurred invoking '…'", so a throw never delivers the guidance to the
    /// client.</summary>
    public string? ConfigPromptOrNull() { lock (_gate) { return _configured ? null : NotConfiguredText; } }

    static InvalidOperationException NotConfigured() => new(NotConfiguredText);

    /// <summary>Resolve + read one record (the read_record primitive). Reads the WINNER's body by default, or a
    /// named <paramref name="plugin"/>'s version; with <paramref name="conflictTree"/> also returns the ordered
    /// touching-plugin list. Recoverable named errors — not-in-order, plugin-doesn't-touch, fetch inconsistency —
    /// never a silent empty result.</summary>
    public ReadOutcome ResolveRead(FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                   bool resolveNames = false, LinkMemo? linkMemo = null,
                                   string? containerHint = ReadEngine.DepthExpandHint)
    {
        var resolver = Resolver;
        var view = resolver.Capture();
        return ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint)
               with { Epoch = view.Epoch, Pin = new ViewPin(resolver, view) };   // stamped and pinned here, off the view actually read
    }

    /// <summary>The on-demand whole-topic dialogue-graph validator: resolve <paramref name="fk"/> to its load-order
    /// winner and, when it is a dialogue topic (DIAL), validate that topic's whole graph; when it is a quest (QUST),
    /// fan out to every topic the quest owns. Everything is judged against the resolved winners, which is what the
    /// game sees. The Skyrim-typed walk lives in the core (<see cref="DialogueValidate"/>) so this assembly stays free
    /// of Mutagen.Skyrim; here it just hands core the record resolver and the VFS asset resolver. It never throws over
    /// a verify step: a mid-run resolve or asset failure rides
    /// <see cref="DialogueValidationReport.CheckError"/>, and a not-in-order or wrong-type input is a named
    /// <see cref="DialogueValidationReport.Error"/>.</summary>
    // No epoch is stamped: the report is one build's answer, but many of its verdicts come off the ASSET substrate,
    // outside the record fingerprint — the .pex chain behind each result script, the .fuz checks, and the .seq
    // staleness verdict, which is a file-mtime comparison no record fingerprint expresses. A record fingerprint here
    // would claim freshness for verdicts it does not describe.
    public DialogueValidationReport ValidateDialogue(FormKey fk) => DialogueValidate.Run(Resolver, Assets, fk);

    /// <summary>The merged <c>check</c> surface's dialogue family: <see cref="ValidateDialogue"/> over a seed list,
    /// tallied for one section of a merged response. Deliberately thin — the family's own grammar (seed parse,
    /// cost refusal, seed budget, tally) lives in <see cref="DialogueSweep"/> rather than in this file.</summary>
    public DialogueCheckResult CheckDialogue(IReadOnlyList<string>? seeds, int limit, bool countsOnly = false)
        => DialogueSweep.Run(ValidateDialogue, OpenFormIdDoor().Parse, seeds, limit, countsOnly);

    /// <summary>The read body, answered entirely off ONE captured view: the excluded-check, the winner and the
    /// touching-plugin list all describe the same build, so a freshness rebuild landing mid-read cannot make a
    /// record's reported winner disagree with its own touching list. Every <see cref="ReadOutcome"/> — single read,
    /// batch item, cross-query detail row — carries the <see cref="ViewPin"/> it was answered from, and the render's
    /// conflict-tree fill reads through it, so one response's tree, touching list and epoch stamp all name the same
    /// build. Bodies are still fetched from disk at fill time, so a file edited mid-render surfaces as the named
    /// fetch-inconsistency error rather than a silently re-resolved winner.</summary>
    ReadOutcome ResolveRead(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                            FormKey fk, string? plugin, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                            bool resolveNames = false, LinkMemo? linkMemo = null,
                            string? containerHint = ReadEngine.DepthExpandHint)
    {
        // An explicitly-requested plugin excluded this session (unparseable or unopenable) is said so, rather than
        // falling through to a misleading "does not define this record".
        if (plugin is not null && view.ExcludedPlugins.TryGetValue(plugin, out var pWhy))
            return ReadOutcome.Fail(fk, $"Plugin '{plugin}' was excluded from this session: {pWhy}");

        // A plugin not in the order at all is its own failure mode: GetRecord returns null for it, and falling
        // through would render a false "does not define this record", which reads as "my write was lost" and invites
        // re-issuing the ops — duplicating list Adds into the patch. Name the true condition and the verify paths
        // instead. houseCARL does not read disabled plugins off disk: non-winner content presented as load-order
        // truth is the hazard.
        if (plugin is not null && !view.ContainsPlugin(plugin))
        {
            // ExplainAbsence, not AbsenceClause: the latter returns a non-empty string for a typo too (the
            // did-you-mean), so its length cannot distinguish "a cause was stated" from "a spelling was guessed",
            // and only the first should change the tail below.
            var cause = view.ExplainAbsence(plugin);
            var why = cause is not null ? " " + cause : view.NameSuggestion(plugin);
            // The write-verify guidance is a fact about the tool, not a guess about the cause, so it is
            // unconditional — the freshly-written-patch case is the commonest reason to hit this refusal, and the
            // read-back is the only way to check a write without touching MO2. Only the posture line ("does not open
            // disabled plugins off disk"), which would contradict a stated cause, is conditional.
            var verify = $" To verify a write BEFORE enabling, use the write call's own read-back (readback=true " +
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
        if (winner is null) return ReadOutcome.Fail(fk, UnresolvedFormId(view, fk));

        var source = plugin ?? winner.Value.WinnerPlugin;
        using var session = resolver.OpenSession();                       // opens the source plugin; disposed at return
        var rec = view.GetRecord(session, source, fk);                    // excluded-check pinned to the same view the winner came from
        if (rec is null)
        {
            if (plugin is null)
                return ReadOutcome.Fail(fk, $"Winner '{winner.Value.WinnerPlugin}' did not yield {fk} on fetch — a load-order inconsistency.");
            // An untouched record under a named plugin refuses by naming the actual touchers: a bare "does not
            // define" reads as "my write was lost", and the touching list is the actionable fact. The ?? is
            // defensive — the non-null winner above proves the fk is in the index — but the nullable return became
            // a real NRE on the off-order sibling, so the guard stays.
            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
            return ReadOutcome.Fail(fk,
                $"Plugin '{plugin}' does not touch {fk} — it has no version of this record. " +
                $"Touched by (load order, winner last): {string.Join(", ", touchers)}.");
        }

        var record = ReadEngine.ReadFields(rec, fields, depth, containerHint);   // materialise while the session (overlay) is open
        record = AnnotateOwnedChildContent(record, rec, view, fk, out var childFields);   // cheap tier — index only, display-only
        if (resolveNames) record = AnnotateLinks(record, view, session, linkMemo ?? new());   // identity of every FormLink token, display-only, on the same open session
        var touching = conflictTree ? view.TouchingPlugins(fk) : null;
        return new ReadOutcome(fk, record, source, winner.Value.WinnerPlugin, winner.Value.OverrideDepth, touching, null)
               { OwnedChildFields = childFields }.WithRuntime(view.RuntimeAddressOf(fk));
    }

    /// <summary>resolve_names (P7): annotate every field whose <see cref="FieldValue.Token"/> is a form reference (a
    /// token that round-trips to a FormKey) with its target's load-order identity, hung on <see cref="FieldValue.Link"/>
    /// — DISPLAY-ONLY, never touching the round-trip Token. Type-agnostic: a token that parses as a FormKey IS a form
    /// reference (FormLinks and condition-target FLOIs both emit a bare FormKey token; scalars never do), so this
    /// inherits coverage from the read surface with no per-type wiring. Resolution rides the SAME captured view +
    /// open session the read used, memoised so a keyword that recurs across a whole record (or batch) resolves once.
    /// An unresolvable target is a named unresolved <see cref="ResolvedRef"/> (Resolved=false), never dropped, bar
    /// the engine-implicit forms, which <see cref="ResolveRefOne"/> answers with their hardcoded identity.
    /// Copy-on-first-write: a record with no form-reference leaves returns the SAME instance.</summary>
    /// <remarks>The memo carries the absence cache too, so a lane annotating many dangling links into ONE absent
    /// plugin pays the explainer's profile parse and install sweep once, not once per FormKey.</remarks>
    static RecordFields AnnotateLinks(RecordFields rf, LoadOrderResolver.IndexView view,
                                      LoadOrderResolver.OverlaySession session, LinkMemo memo)
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

    /// <summary>On a read of a field that OWNS CHILD RECORDS, say that other plugins touch this record and that this
    /// read did not open them. Index only; no body is fetched.
    /// <para>Placed references, a topic's INFOs and a worldspace's cells are declared per plugin and assembled by the
    /// game from every plugin that declares them. An override touching a cell for an unrelated reason (occlusion,
    /// lighting, music) carries no references and deletes none, so reading its <c>Persistent</c>/<c>Temporary</c>
    /// reports an empty cell the game actually fills. Without this note a caller auditing "what is in this cell"
    /// through the winner gets a silently wrong answer.</para>
    /// <para>This tier deliberately claims little: naming which plugins declare children requires their bodies, and
    /// it unions nothing — "what is actually live in this parent" is separate work, since naive concatenation would
    /// multi-count children that overlapping overrides both declare. See
    /// `docs/architecture/records-owned-child-declarers.md`.</para>
    /// <para>Assembly is over the whole touching set, so a <c>plugin=</c>-scoped read of a base master is annotated
    /// too: the plugins above it declare children it cannot see.</para>
    /// <para>Display-only: it rides <see cref="FieldValue.Display"/>, never the round-trip
    /// <see cref="FieldValue.Token"/>, so it is invisible to the write surface, the read-proof oracle and the
    /// conflict diff, and reaches every render through that one carrier.</para></summary>
    static RecordFields AnnotateOwnedChildContent(RecordFields rf, IMajorRecordGetter body,
                                                  LoadOrderResolver.IndexView view, FormKey fk,
                                                  out IReadOnlyDictionary<string, OwnedChildShape>? annotated)
    {
        annotated = null;
        // Empty for all but three record types, so this is where the overwhelming majority of reads leave, before
        // any index lookup.
        var owning = OwnedChildContent.Fields(body);
        if (owning.Count == 0) return rf;

        // Which of the lines THIS read produced are those fields. A depth>=2 read emits the same summary line at the
        // bare field path before expanding its children, so the annotation lands in one place either way.
        List<int>? hits = null;
        for (int i = 0; i < rf.Fields.Count; i++)
            if (owning.ContainsKey(rf.Fields[i].Path)) (hits ??= new List<int>()).Add(i);
        if (hits is null) return rf;

        var touching = view.TouchingPlugins(fk);
        if (touching is null || touching.Count <= 1) return rf;   // sole toucher: its own body IS the whole story

        // Index only — no body is opened here. The default read states just what the index settles for free: that
        // other plugins touch this record and this read did not look at what they declare. The tree form, which has
        // already paid for every body, states which ones do.
        var note = ReadSentences.NotReadNote(touching.Count - 1);
        var rebuilt = new List<FieldValue>(rf.Fields);
        // The ANNOTATED paths and their shapes travel with the outcome, because the render decides its
        // response-level clause off the fields it actually emitted — a path that never reaches the medium (a cap
        // hit inside the field loop, a truncated json array, a manifest-only spill) must not earn a clause.
        var map = new Dictionary<string, OwnedChildShape>(hits.Count, StringComparer.Ordinal);
        foreach (var i in hits)
        {
            // These fields are containers and owned records; the only other producer of Display is the flags
            // decode, which fires on [Flags] enum leaves alone — so there is no annotation here to displace.
            rebuilt[i] = rebuilt[i] with { Display = note };
            map[rebuilt[i].Path] = owning[rebuilt[i].Path];
        }
        annotated = map;
        return rf with { Fields = rebuilt };
    }

    /// <summary>Why a FormID resolved to nothing. "Not present" has three causes and one sentence used to serve
    /// them all: the defining plugin was excluded, the plugin is not in the order, or the plugin IS in the order
    /// and defines no such record. All three are answerable from the index in hand, so every emitter states which
    /// one it is rather than leaving the caller a second call to find out.
    /// <para>The ESL clause on the third is stated only when the index says the plugin IS light-flagged
    /// (<see cref="LoadOrderResolver.IndexView.IsLightFlagged"/>). A compacted edition's 0x800+ FormIDs are a real
    /// and common cause, but 0x800 is also where a plain Mutagen-authored master's records start, so asserting
    /// compaction off the FormID alone tells a caller holding an ordinary full master a false cause. Unflagged, the
    /// sentence states the fact it has — this plugin defines no such record — and names the call that lists what it
    /// does define.</para></summary>
    static string UnresolvedFormId(LoadOrderResolver.IndexView view, FormKey fk,
                                   Dictionary<string, string>? absenceMemo = null)
    {
        var defining = fk.ModKey.FileName.ToString();
        if (view.ExcludedPlugins.TryGetValue(defining, out var why))
            return $"FormID {fk} is not resolvable: its plugin '{defining}' was excluded from this session: {why}";
        if (view.ContainsPlugin(defining))
        {
            var esl = view.IsLightFlagged(defining)
                ? $" '{defining}' IS ESL-flagged, and an ESL-flagged edition compacts its records into 0x800+, so this " +
                  "is commonly a FormID taken from a different (uncompacted) edition of the same mod."
                : "";
            return $"Plugin '{defining}' IS in the load order, but defines no record {fk.ID:X6} — and no other plugin " +
                   $"overrides it either.{esl} List what it actually defines with housecarl_records " +
                   $"plugins={{\"names\": [\"{defining}\"], \"defined_in\": true}}.";
        }
        // One clause, one explainer call, and the spelling hint only where nothing better can be said: a stated
        // cause ("installed, but UNTICKED in plugins.txt") makes "check the filename" a contradiction. The
        // explainer costs a profile parse plus an install sweep, so a batch resolving many dangling refs into the
        // SAME missing plugin pays for it once (the same memo the write lane keeps).
        // Memoised on the PLUGIN, never the FormID: the tail is the same for every record of one missing plugin,
        // and the FormID-bearing head is composed fresh below.
        if (absenceMemo is null || !absenceMemo.TryGetValue(defining, out var tail))
        {
            var absence = view.AbsenceClause(defining, out var cause);
            var hint = cause is null ? " (names match the plugin FILENAME incl. .esp/.esm, case-insensitively)" : "";
            tail = hint + "." + absence;
            if (absenceMemo is not null) absenceMemo[defining] = tail;
        }
        return $"FormID {fk} is not present in the load order ({view.PluginCount} plugins): its plugin '{defining}' " +
               $"is not in the order{tail}";
    }

    /// <summary>How deep the conflict diff reads each touching body. It must compare CONTENT rather than depth-1
    /// count summaries, which hide equal-count list deltas — deep enough to reach every modeled scalar leaf. The walk
    /// is bounded by the modeled-corpus boundary and ReadEngine's expansion cap, whose truncation sentinel the diff
    /// surfaces as Complete=false.</summary>
    internal const int ConflictDiffDepth = 16;

    /// <summary>A header-only summary for one record (winner + type + editorid, no field dump) — the compact
    /// one-line-per-match view cross_plugin_query uses by default. One winner-body fetch; holds nothing.</summary>
    public RecordSummary ResolveSummary(FormKey fk)
    {
        var resolver = Resolver;
        return ResolveSummary(resolver, resolver.Capture(), fk);   // one capture per summary: winner, depth and fetch from one build
    }

    static RecordSummary ResolveSummary(LoadOrderResolver resolver, LoadOrderResolver.IndexView view, FormKey fk)
    {
        var w = view.ResolveWinner(fk);
        if (w is null) return new RecordSummary(fk, "?", null, "?", 0, $"{fk} not in the load order");
        using var session = resolver.OpenSession();
        var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
        if (body is null)
            return new RecordSummary(fk, "?", null, w.Value.WinnerPlugin, w.Value.OverrideDepth,
                $"winner '{w.Value.WinnerPlugin}' did not yield {fk} on fetch");
        return new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                 w.Value.WinnerPlugin, w.Value.OverrideDepth, null)
               .WithRuntime(view.RuntimeAddressOf(fk));
    }

    // ---- pinned per-match fills ------------------------------------------------------------------------

    /// <summary>A pinned (resolver, view) pair, carried on <see cref="CrossQueryOutcome.Pin"/> and
    /// <see cref="ReadOutcome.Pin"/> so the render-time fills a response makes — cross-query detail bodies, lazy
    /// summaries, conflict-tree blocks — read the build the outcome's epoch names rather than a fresh capture of an
    /// adjacent build. Pure data, no handles.</summary>
    internal sealed record ViewPin(LoadOrderResolver Resolver, LoadOrderResolver.IndexView View);

    /// <summary>The cross-query detail fill, pinned to the scan's build when the outcome carries one. Without the pin
    /// each row re-gates and re-captures, so a freshness rebuild landing mid-render would fill the remaining rows from
    /// a build the header's epoch does not name; pinning also drops the per-row stat sweep. Bodies are still fetched
    /// from disk at fill time — the pin freezes winner IDENTITY, and a file that changed under a pinned fetch surfaces
    /// as the named fetch-inconsistency error. Falls back to the public path when the outcome carries no pin.</summary>
    internal ReadOutcome ResolveReadOn(CrossQueryOutcome q, FormKey fk, string? plugin, IReadOnlyList<string>? fields,
                                       bool conflictTree, int depth = 1, bool resolveNames = false,
                                       LinkMemo? linkMemo = null,
                                       string? containerHint = ReadEngine.DepthExpandHint)
        => q.Pin is { } p
            ? ResolveRead(p.Resolver, p.View, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint)
              with { Epoch = p.View.Epoch, Pin = p }
            : ResolveRead(fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint);

    /// <summary>The summary twin of <see cref="ResolveReadOn"/> — the conflicts-only lazy fill, pinned to the scan's
    /// build when the outcome carries one.</summary>
    internal RecordSummary ResolveSummaryOn(CrossQueryOutcome q, FormKey fk)
        => q.Pin is { } p ? ResolveSummary(p.Resolver, p.View, fk) : ResolveSummary(fk);

    /// <summary>The conflict-tree fill off a pinned build — used by the render whenever the outcome it is decorating
    /// carries a <see cref="ViewPin"/>, so the tree's membership and the response's epoch stamp name the same
    /// build.</summary>
    internal ConflictTreeView? ResolveTreePinned(ViewPin p, FormKey fk, IReadOnlyList<string>? fields)
    {
        using var session = p.Resolver.OpenSession();
        var tree = p.View.ResolveTree(session, fk);
        if (tree is null) return null;

        // The precise owned-child tier: which providers declare children per child-bearing field, asked of bodies
        // already open for the diff below, so it costs no extra fetch. Rationale and narrowing rules:
        // `docs/architecture/records-owned-child-declarers.md`.
        var owning = tree.Nodes.Count > 0 ? OwnedChildContent.Fields(tree.Nodes[0].Record) : null;
        var wanted = owning is null || owning.Count == 0
            ? new List<string>()
            : owning.Keys.Where(f => fields is null || fields.Contains(f, StringComparer.Ordinal))
                    .OrderBy(f => f, StringComparer.Ordinal).ToList();
        var declaring = wanted.ToDictionary(f => f, _ => new List<string>(), StringComparer.Ordinal);
        var unreadable = wanted.ToDictionary(f => f, _ => new List<string>(), StringComparer.Ordinal);

        var nodes = new List<ConflictNodeView>(tree.Nodes.Count);
        foreach (var n in tree.Nodes)
        {
            nodes.Add(new ConflictNodeView(n.Plugin, ReadEngine.ReadFields(n.Record, fields, ConflictDiffDepth)));   // materialise while open
            foreach (var f in wanted)
            {
                // Null means "could not look", never "declares nothing": a body dropped in silence would render as
                // "nobody declares content here".
                var d = OwnedChildContent.DeclaresChild(n.Record, f);
                if (d == true) declaring[f].Add(n.Plugin);
                else if (d is null) unreadable[f].Add(n.Plugin);
            }
        }
        return new ConflictTreeView(nodes,
            wanted.Select(f => new ChildDeclarers(f, owning![f], declaring[f], unreadable[f])).ToList());
    }

    /// <summary>The best-effort display Name of a record body — reflection-generic via Mutagen's <c>INamedGetter</c>
    /// aspect, so it inherits coverage from the model (no per-record-type wiring): every named record answers, a
    /// type with no Name (KYWD, most references) returns null. A translated Name resolves to its default-language
    /// string.</summary>
    static string? ReadDisplayName(IMajorRecordGetter body) =>
        body is INamedGetter named && !string.IsNullOrEmpty(named.Name) ? named.Name : null;

    /// <summary>The name-resolution caches one lane carries: a target's identity per FormKey, and the absence tail
    /// per missing plugin name. Both are per-lane, never global — they describe ONE captured build.</summary>
    public sealed class LinkMemo
    {
        /// <summary>Resolved identity per target, so a keyword recurring across a batch resolves once.</summary>
        public Dictionary<FormKey, ResolvedRef> Refs { get; } = new();

        /// <summary>The unresolved-FormID tail per missing plugin, so the absence explainer — a profile parse plus
        /// an install sweep — runs once per plugin rather than once per dangling FormKey.</summary>
        public Dictionary<string, string> Absences { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolve ONE FormKey to its load-order identity (type/editorid/name/winner) off a captured view + open
    /// session, memoised so a target that recurs across a batch (the SAME keyword on 500 items) resolves once. A
    /// FormKey not in the order is a named unresolved result (Resolved=false), never dropped or guessed — except the
    /// engine-implicit forms (PlayerRef 000014, Player 000007), which the index cannot resolve but are real: those
    /// answer with their hardcoded identity and winner "&lt;engine&gt;", the same <see cref="EngineImplicit"/>
    /// exemption the error and dialogue checks apply.</summary>
    static ResolvedRef ResolveRefOne(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     FormKey fk, LinkMemo memo)
    {
        if (memo.Refs.TryGetValue(fk, out var hit)) return hit;
        ResolvedRef result;
        var w = view.ResolveWinner(fk);
        if (w is null)
            result = EngineImplicit.TryDescribe(fk, out var eiType, out var eiEditorId)
                ? new ResolvedRef(fk.ToString(), Resolved: true, Type: eiType, EditorId: eiEditorId, Winner: "<engine>")   // engine-implicit: hardcoded, real, defined by no plugin
                // Valid FormKey, no active plugin defines it. The reason is the three-cause sentence every other
                // lane states, so the identity form's row says WHICH cause instead of a bare "not present".
                : new ResolvedRef(fk.ToString(), Resolved: false, Error: UnresolvedFormId(view, fk, memo.Absences));
        else
        {
            var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
            result = body is null
                ? new ResolvedRef(fk.ToString(), Resolved: false, Winner: w.Value.WinnerPlugin)   // winner named but the fetch didn't yield it
                : new ResolvedRef(fk.ToString(), Resolved: true, Type: RecordNaming.StripOverlay(body.GetType().Name),
                                  EditorId: body.EditorID, Name: ReadDisplayName(body), Winner: w.Value.WinnerPlugin);
        }
        memo.Refs[fk] = result;
        return result;
    }

    /// <summary>Bulk name resolution: turn a list of FormIDs into their load-order identity (type, editorid, name,
    /// winner) in one call over one captured view, memoised across the batch. A bad or absent FormID yields a per-item
    /// result carrying its reason — Error for a malformed string, Resolved=false for a valid-but-absent FormKey —
    /// without failing the whole batch. Deliberately minimal: no fields, depth or conflict tree.</summary>
    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids) => ResolveRefs(formids, out _);

    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids, out string epoch)
        => ResolveRefs(formids, null, out epoch, out _);

    /// <summary>The artifact-epoch mismatch refusal — one wording for every consuming lane, naming both epochs and
    /// the two legitimate next moves. Deliberately no stale-override parameter: re-projecting goes through the
    /// server, and reading the old file as a snapshot of its own build is the client's lane.</summary>
    internal static string ArtifactEpochMismatch(ArtifactDemand d, string current) =>
        $"artifact '{d.Path}' was captured at epoch={d.Epoch}, but the CURRENT load-order build is epoch={current} — " +
        "the load order changed since the artifact was written, so its rows may resolve differently now. " +
        "Re-run the producing query (with to_file= to re-materialize) against the current build; the old file stays " +
        "readable with your own tools as an honest snapshot of ITS build. There is deliberately no stale-override switch.";

    /// <summary>As above, also handing back the captured build's <paramref name="epoch"/> fingerprint — the batch is
    /// one capture, and the render stamps that identity into the response's accounting.
    /// <see cref="ResolvedRef"/> itself stays epoch-free: it is the per-row identity DTO, reused as the resolve_names
    /// annotation where a per-row stamp would be noise.
    /// <para><paramref name="artifactDemand"/>, when the formid list came from an artifact, is checked against THIS
    /// capture's epoch — the same build that answers — and a mismatch hands back
    /// <paramref name="artifactRefusal"/> with no rows, stamped with <paramref name="epoch"/>.</para></summary>
    public IReadOnlyList<ResolvedRef> ResolveRefs(IReadOnlyList<string> formids, ArtifactDemand? artifactDemand,
                                                  out string epoch, out string? artifactRefusal)
    {
        artifactRefusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();                  // one build for the whole batch
        epoch = view.Epoch;
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            artifactRefusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            return Array.Empty<ResolvedRef>();
        }
        using var session = resolver.OpenSession();
        var memo = new LinkMemo();
        var results = new List<ResolvedRef>(formids.Count);
        foreach (var raw in formids)
        {
            var t = raw?.Trim() ?? "";
            FormKey fk;
            try { fk = view.ParseFormId(t); }
            catch (Exception ex) { results.Add(new ResolvedRef(t, Resolved: false, Error: $"bad FormID: {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); continue; }
            results.Add(ResolveRefOne(view, session, fk, memo));
        }
        return results;
    }

    // ---- pairwise record diff --------------------------------------------------------------------------

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
        // An excluded plugin is still in the name table (exclusion is a separate set) and the active lane can only
        // refuse it. Reading its file directly is the escape hatch for that case — records ahead of the unparseable
        // one still come back — so a path to one must keep taking the off-order lane.
        if (view.ExcludedPlugins.ContainsKey(name)) return null;
        var active = view.PluginPath(name);
        return !string.IsNullOrEmpty(active) && SamePluginFile(active, full) ? name : null;
    }

    /// <summary>One side of a housecarl_diff_record comparison: the plugin named, WHERE its version was found (active
    /// order, or OUT-OF-LOAD-ORDER on disk), whether it's in the active order, and the record identity it carries.</summary>
    public sealed record DiffPole(string Plugin, string Where, bool InOrder, string? RecordType, string? EditorId)
    {
        /// <summary>What tells this pole apart from a same-named one on the other arm — the mod folder it was read
        /// out of, or "off-order" when the layer names nothing. Set on the off-order arm only: two poles can share a
        /// filename and be different files, and the active one is then the unqualified side.</summary>
        internal string? Qualifier { get; init; }

        /// <summary>The pole's label for a render that shows both sides. Qualified only when the other side carries
        /// the same filename, so the ordinary one-pole-per-name case reads unchanged.</summary>
        public string LabelVersus(string? otherPlugin) =>
            Qualifier is { } q && string.Equals(Plugin, otherPlugin, StringComparison.OrdinalIgnoreCase)
                ? $"{Plugin} ({q})" : Plugin;
    }

    // ---- batch ------------------------------------------------------------------------------------------

    /// <summary>Resolve and read many records in one call. Each formid runs the same <see cref="ResolveRead"/> path,
    /// so a bad or absent formid yields a per-item recoverable error without failing the batch. Returns one
    /// <see cref="ReadOutcome"/> per input, in order. When <paramref name="plugin"/> is set, every formid is read as
    /// that plugin's version — its override, not the load-order winner — and a formid it does not touch yields its
    /// own per-item error.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth = 1,
                                                   bool resolveNames = false, string? plugin = null,
                                                   string? containerHint = ReadEngine.DepthExpandHint)
        => ResolveBatch(formids, fields, conflictTree, depth, resolveNames, plugin, null, out _, out _, containerHint);

    /// <summary>The artifact-aware overload: <paramref name="artifactDemand"/> (a formids=@artifact input) is checked
    /// against THIS capture's epoch — the same build that would answer — and a mismatch hands back
    /// <paramref name="artifactRefusal"/> and <paramref name="refusalEpoch"/> with no rows, because a refusal that
    /// consulted a build renders stamped with it.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatch(IReadOnlyList<string> formids, IReadOnlyList<string>? fields, bool conflictTree, int depth,
                                                   bool resolveNames, string? plugin, ArtifactDemand? artifactDemand,
                                                   out string? artifactRefusal, out string? refusalEpoch,
                                                   string? containerHint = ReadEngine.DepthExpandHint)
    {
        artifactRefusal = null; refusalEpoch = null;
        var resolver = Resolver;                // build/refresh once for the batch
        var view = resolver.Capture();          // one build for every item — the whole batch is one logical operation
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            artifactRefusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);
        var linkMemo = resolveNames ? new LinkMemo() : null;   // one link-resolution cache across the whole batch
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            outcomes.Add(ResolveRead(resolver, view, fk, plugin, fields, conflictTree, depth, resolveNames, linkMemo, containerHint)
                         with { Epoch = view.Epoch, Pin = pin });   // the batch's one build, stamped and pinned per item
        }
        return outcomes;
    }

    // ---- `records`: the one-pole batch (source=named, wherever the plugin lives) -----------------------

    /// <summary>How a `records` source= pole resolved: active in the order, or an on-disk file outside it. The
    /// response always states which arm, so nothing resolves silently. An off-order file sits outside the epoch
    /// fingerprint, which <see cref="EpochCoversPole"/> carries as data for the render.</summary>
    public sealed record PoleInfo(string Plugin, string Where, bool InOrder, bool EpochCoversPole)
    {
        /// <summary>The on-disk locate result for the off-order arm (null on the active arm), carried so the
        /// consuming lane can open the file without re-running the locate.</summary>
        internal string? Path { get; init; }

        /// <summary>The layer the off-order copy came from ("mod 'X'", the overwrite folder), when the locate's own
        /// label names one — carried as a fact rather than re-derived from <see cref="Where"/>, so a render that has
        /// to tell two same-named copies apart names the folder instead of parsing a sentence.</summary>
        internal string? Layer { get; init; }

        /// <summary>The epoch of the build the arm was judged against. The caller compares it against its dispatch's
        /// own stamp, so a load-order change between probe and dispatch surfaces as a loud retry refusal instead of
        /// an arm statement about a different build.</summary>
        public string? Epoch { get; init; }
    }

    /// <summary>Resolve a `records` source= pole against ONE captured view: active in the order, else located on disk
    /// across the whole install. A non-null error means it was found in neither place (naming both), or is ambiguous
    /// across mod folders (naming them and the {file, mod} disambiguator).
    /// <para>The {file, mod} form addresses ONE on-disk copy, which is the whole reason it exists: several mod
    /// folders ship the same filename and MO2 serves one. So it does NOT short-circuit to the active copy of the
    /// filename — the locate runs first, and the named copy resolves to the active arm only when it IS the copy the
    /// game loads. A plain filename and a direct path are unchanged.</para></summary>
    (PoleInfo? Pole, string? Error) ResolvePoleArm(LoadOrderResolver.IndexView view, string plugin, string? mod)
    {
        // Judged on the argument as given: the rewrite below turns a path into a bare filename, which would flip a
        // path pole into the mod= lane and read a file the caller did not name.
        bool namesMod = !string.IsNullOrWhiteSpace(mod) && !LooksLikePath(plugin);

        // A pole addressed by path that IS the active order's file resolves back to its plugin name.
        if (LooksLikePath(plugin) && ActiveNameForPath(view, plugin) is { } activeName) plugin = activeName;

        bool activeFilename = view.ContainsPlugin(plugin);
        if (!namesMod && activeFilename)
            return (new PoleInfo(plugin, "active in the load order", InOrder: true, EpochCoversPole: true), null);

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex)
        {
            return (null, activeFilename
                ? $"source '{plugin}' names mod folder '{mod!.Trim()}', and the MO2 roots couldn't be derived to read that folder's copy: {ex.Message}"
                : $"source '{plugin}' is not active in the load order, and the MO2 roots couldn't be derived to search for it on disk: {ex.Message}");
        }
        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, mod);
        if (loc.Error is not null)
            // A pole found in neither place names both places searched. When the filename IS active, the named mod
            // folder is the only place searched, so the sentence says that instead of claiming it is not active.
            return (null, activeFilename
                ? $"source '{plugin}': {loc.Error} The filename IS active in the load order — drop mod= to read the copy the game loads."
                : $"source '{plugin}' resolves in NEITHER place the one-pole rule searches: it is not ACTIVE in " +
                  $"the load order ({view.PluginCount} plugins), and on disk {loc.Error}");
        if (loc.Ambiguous is not null)
            return (null, $"source '{plugin}' is not active in the load order and its filename is provided by SEVERAL mod " +
                          $"folders on disk: {string.Join(", ", loc.Ambiguous.Select(h => $"'{h.Where}'"))} — disambiguate " +
                          "with source={\"file\": \"" + plugin + "\", \"mod\": \"<mod folder>\"}.");
        // The named copy IS the copy the game loads, so it is read out of the order like any other active pole.
        if (activeFilename && loc.Enabled)
            return (new PoleInfo(plugin, "active in the load order", InOrder: true, EpochCoversPole: true), null);
        var poleWhere = $"OUT-OF-LOAD-ORDER ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})";
        return (new PoleInfo(plugin, poleWhere, InOrder: false, EpochCoversPole: false)
                { Path = loc.Path, Layer = loc.WhereNamesLayer ? loc.Where : null }, null);
    }

    /// <summary>The tool-layer probe: WHICH arm would this source= pole resolve to (active / off-order / neither)?
    /// Uses its own capture and stamps its epoch on the <see cref="PoleInfo"/>; the consuming ACTIVE-arm scan
    /// re-captures and compares stamps (a divergence refuses loud — retry). The OFF-ORDER arm's lane reads the
    /// file directly and consults no further build, so its arm statement is simply the probe's own build's truth.</summary>
    public PoleInfo? ProbeSourceArm(string plugin, string? mod, out string? error)
    {
        var view = Resolver.Capture();
        var (pole, err) = ResolvePoleArm(view, plugin, mod);
        error = err;
        return pole is null ? null : pole with { Epoch = view.Epoch };
    }

    /// <summary>The list-driven `records` read under a named source pole: resolve the pole once — active in the
    /// order, else a file on disk in an enabled, disabled or unlisted mod folder — and read every FormID's version
    /// from it off ONE captured build. A pole found in neither place is a whole-call refusal naming both places
    /// searched; a record the pole does not touch is a per-item refusal naming the actual touchers, never a silent
    /// drop; a bad FormID is a per-item error. Off-order reads carry winner context where the record also resolves in
    /// the active order, and the pole's file content is declared outside the epoch fingerprint.</summary>
    public IReadOnlyList<ReadOutcome> ResolveBatchFromPole(
        IReadOnlyList<string> formids, string plugin, string? mod,
        IReadOnlyList<string>? fields, int depth, bool resolveNames,
        ArtifactDemand? artifactDemand,
        out PoleInfo? pole, out string? refusal, out string? refusalEpoch,
        string? containerHint = ReadEngine.DepthExpandHint)
    {
        pole = null; refusal = null; refusalEpoch = null;
        var resolver = Resolver;
        var view = resolver.Capture();          // one build for the pole test and every read
        if (artifactDemand is not null && artifactDemand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(artifactDemand, view.Epoch);
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);

        var (arm, armErr) = ResolvePoleArm(view, plugin, mod);
        if (armErr is not null)
        {
            refusal = armErr;
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        pole = arm;
        plugin = arm!.Plugin;   // a path pole may have resolved back to its active plugin name

        if (arm.InOrder)
        {
            // Active arm: the same per-item reads ResolveBatch(plugin=) does, off the same captured view, with
            // excluded-plugin and untouched-record refusals per item and the touchers named.
            var linkMemo = resolveNames ? new LinkMemo() : null;
            var outcomes = new List<ReadOutcome>(formids.Count);
            foreach (var raw in formids)
            {
                FormKey fk;
                try { fk = view.ParseFormId(raw); }
                catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
                outcomes.Add(ResolveRead(resolver, view, fk, plugin, fields, false, depth, resolveNames, linkMemo, containerHint)
                             with { Epoch = view.Epoch, Pin = pin });
            }
            return outcomes;
        }

        // Off-order arm: the locate already ran in ResolvePoleArm, so open the overlay once and pick every requested
        // record in a single enumeration pass.
        string dataDirForOverlay;
        try { lock (_gate) { EnsurePathsDerived(); dataDirForOverlay = _dataDir; } }
        catch (Exception ex)
        {
            refusal = $"the MO2 roots couldn't be derived to open '{plugin}': {ex.Message}";
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        var poleWhere = arm.Where;

        // Parse every FormID first (per-item errors keep their input positions), then one enumeration pass.
        var parsed = new List<(int Index, FormKey Fk)>();
        var results = new ReadOutcome?[formids.Count];
        for (int i = 0; i < formids.Count; i++)
        {
            try { parsed.Add((i, view.ParseFormId(formids[i]))); }
            catch (Exception ex) { results[i] = ReadOutcome.Fail(default, $"bad FormID '{formids[i]}': {ex.Message}"); }
        }

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(arm.Path!, string.IsNullOrEmpty(dataDirForOverlay) ? null : dataDirForOverlay); }
        catch (Exception ex)
        {
            refusal = $"could not open '{arm.Path}' as a Skyrim plugin: {ex.Message}";
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        try
        {
            var wanted = parsed.Select(p => p.Fk).ToHashSet();
            var found = new Dictionary<FormKey, IMajorRecordGetter>();
            try
            {
                foreach (var r in ov.EnumerateMajorRecords())
                    if (wanted.Contains(r.FormKey) && !found.ContainsKey(r.FormKey))
                    {
                        found[r.FormKey] = r;
                        if (found.Count == wanted.Count) break;
                    }
            }
            catch (Exception ex)
            {
                refusal = $"file '{plugin}' could not be fully read — a record Mutagen cannot parse: {ex.Message}";
                refusalEpoch = view.Epoch;
                return Array.Empty<ReadOutcome>();
            }

            var linkMemo = resolveNames ? new LinkMemo() : null;
            using var session = resolveNames ? resolver.OpenSession() : null;   // resolve_names annotates against the ACTIVE order
            foreach (var (index, fk) in parsed)
            {
                if (!found.TryGetValue(fk, out var rec))
                {
                    // The untouched contract holds on this arm too: name the plugins that DO touch the record in the
                    // active order, or say plainly that nothing does. TouchingPlugins returns null rather than
                    // throwing for a FormKey outside the index — e.g. an old patch whose master is also disabled —
                    // so the ?? is load-bearing, not defensive.
                    IReadOnlyList<string> touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                    results[index] = ReadOutcome.Fail(fk,
                        $"file '{plugin}' ({poleWhere}) does not define or override {fk} — it has no version of this record. " +
                        (touchers.Count > 0
                            ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                            : "No active plugin touches it either."))
                        with { Epoch = view.Epoch, Pin = pin };
                    continue;
                }
                var record = ReadEngine.ReadFields(rec, fields, depth, containerHint);          // materialise while the overlay is open
                if (resolveNames) record = AnnotateLinks(record, view, session!, linkMemo!);
                var winner = view.ResolveWinner(fk);                             // winner CONTEXT where the record also lives in the order
                results[index] = new ReadOutcome(fk, record, plugin, winner?.WinnerPlugin,
                                                 winner?.OverrideDepth ?? 0, null, null)
                                 with { Epoch = view.Epoch, Pin = pin };
            }
            return results.Select(r => r!).ToList();
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ---- comparison poles and the delta/tree batches --------------------------------------------------

    /// <summary>Which pole a source= or versus= expression names. <see cref="Overlay"/> is the SkyPatcher runtime
    /// replay: pre is the plain winner body, post the winner body after the INI layer replays.</summary>
    public enum PoleKind { Winner, Named, PreviousProvider, Overlay }

    /// <summary>A parsed pole expression — the tool layer parses the wire spelling ("winner", a plugin filename,
    /// {file, mod}, "previous_provider", {overlay, state}) into this engine value.</summary>
    public sealed record PoleSpec(PoleKind Kind, string? Plugin = null, string? Mod = null, string? OverlayState = null)
    {
        public static readonly PoleSpec Winner = new(PoleKind.Winner);
        /// <summary>The arm statement a render leads with when the pole is uniform across the batch. PreviousProvider
        /// is per-record, so its statement is the rule rather than an arm.</summary>
        public string Label => Kind switch
        {
            PoleKind.Winner => "winner",
            PoleKind.PreviousProvider => "previous_provider (the provider immediately below the subject, per record)",
            PoleKind.Overlay => $"skypatcher overlay ({OverlayState})",
            _ => Plugin ?? "?",
        };
    }

    /// <summary>One record's delta: subject pole versus reference pole, compared by <see cref="FieldsDiff"/>.
    /// <see cref="StackAbove"/> — set only under a previous_provider reference when the subject sits mid-stack —
    /// names what outranks the subject, winner last, as neutral fact: a non-winning subject is not an anomaly, so
    /// no advice and no warning tone. <see cref="Note"/> carries per-row facts such as the two poles resolving to
    /// the same provider. A non-null Error is a per-item refusal; the batch survives.</summary>
    public sealed record DeltaRow(string Formid, DiffPole? Subject, DiffPole? Reference, FieldsDiff.Result? Diff,
                                  IReadOnlyList<string>? StackAbove, string? Note, string? Error);

    /// <summary>The project=delta batch: every pole of every record resolves against ONE captured build, so a
    /// comparison can never span two. Subject defaults to winner; reference may be winner, a named plugin (active or
    /// off-order, with off-order files declared outside the epoch fingerprint), or previous_provider, which is
    /// subject-relative. A named pole that does not touch a record is a per-item refusal naming the actual touchers,
    /// which the caller counts as not_touched. Overlay poles resolve via the SkyPatcher replay.</summary>
    public IReadOnlyList<DeltaRow> DeltaBatch(
        IReadOnlyList<string> formids, PoleSpec subject, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? subjectArm, out string? referenceArm, out bool epochCoversAll,
        out string? refusal, out string? epoch)
    {
        subjectArm = null; referenceArm = null; epochCoversAll = true; refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();          // one build for every pole of every record
        epoch = view.Epoch;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<DeltaRow>();
        }
        using var session = resolver.OpenSession();

        // Pre-parse the keys so an off-order pole's cache materializes only the requested records.
        var parsed = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wanted = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsed.Add((raw, fk0, null)); wanted.Add(fk0); }
            catch (Exception ex) { parsed.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        // Resolve the uniform arms once (named poles; winner/overlay are per-record but uniform in statement).
        var sReader = MakePoleReader(view, session, subject, fields, wanted, out subjectArm, out var sCovers, out var sErr, out var sOffOrder);
        if (sErr is not null) { refusal = "source: " + sErr; return Array.Empty<DeltaRow>(); }
        var rReader = MakePoleReader(view, session, reference, fields, wanted, out referenceArm, out var rCovers, out var rErr, out _);
        if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<DeltaRow>(); }
        epochCoversAll = sCovers && rCovers;

        // previous_provider is measured from the SUBJECT's position in the active touching stack, which an off-order
        // subject holds in no record — and its filename can be active as a DIFFERENT file. That is a fact about the
        // arm, not about any record, so it refuses the whole call here rather than deep-reading every match first.
        if (reference.Kind == PoleKind.PreviousProvider && sOffOrder is not null)
        {
            refusal = $"versus: the subject is the off-order file '{sOffOrder.Plugin}' ({sOffOrder.Where}), which holds no position in the " +
                      "active touching stack previous_provider is measured in. Name an active plugin as source=, or compare against a named versus= plugin.";
            return Array.Empty<DeltaRow>();
        }

        var rows = new List<DeltaRow>(formids.Count);
        foreach (var (raw, fkOpt, parseError) in parsed)
        {
            if (parseError is not null) { rows.Add(new DeltaRow(raw?.Trim() ?? "", null, null, null, null, null, parseError)); continue; }
            var fk = fkOpt!.Value;

            var s = sReader(fk, null);
            if (s.Error is not null) { rows.Add(new DeltaRow(fk.ToString(), s.Pole, null, null, null, null, "subject: " + s.Error)); continue; }
            // previous_provider is measured from the SUBJECT, so hand the reference reader the subject's resolved
            // plugin for this record and it anchors on the right stack position. The off-order subject is already
            // refused for the whole call above.
            var r = rReader(fk, s.Pole!.Plugin);
            if (r.Error is not null) { rows.Add(new DeltaRow(fk.ToString(), s.Pole, r.Pole, null, r.StackAbove, null, "versus: " + r.Error)); continue; }

            string? note = string.Equals(s.Pole.Plugin, r.Pole!.Plugin, StringComparison.OrdinalIgnoreCase) && s.Pole.Where == r.Pole.Where
                ? "the two poles resolved to the SAME provider — the diff is trivially empty by construction"
                : null;
            // Two copies of one filename on opposite arms: the delta line names the off-order side's mod folder, or
            // the reader cannot tell which side a value came from without the pole lines above.
            var diff = FieldsDiff.Compare(s.Fields!, r.Fields!, referenceLabel: r.Pole.LabelVersus(s.Pole.Plugin));
            rows.Add(new DeltaRow(fk.ToString(), s.Pole, r.Pole, diff, r.StackAbove, note, null));
        }
        return rows;
    }

    /// <summary>A pole reader's per-record result: the deep-read fields + the pole identity for the render, or a
    /// per-item error. <see cref="StackAbove"/> names what outranks the subject under a previous_provider
    /// reference.</summary>
    internal sealed record PoleReading(RecordFields? Fields, DiffPole? Pole, IReadOnlyList<string>? StackAbove, string? Error);

    internal delegate PoleReading PoleReader(FormKey fk, string? subjectPlugin);

    /// <summary>Build the per-record reader for one pole against the shared captured view and session. Uniform arm
    /// resolution — a named plugin's active-versus-off-order arm, or an off-order file opened and swept lazily on
    /// first use — happens here once; per-record work stays in the returned reader. <paramref name="covers"/> is
    /// false when the pole reads content outside the epoch fingerprint, such as an off-order file or the overlay's
    /// INIs. <paramref name="offOrderArm"/> is the resolved arm when it is an on-disk file outside the order, and
    /// null otherwise — a uniform fact about the whole call, so a caller can judge it once instead of per record.</summary>
    PoleReader MakePoleReader(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                              PoleSpec spec, IReadOnlyList<string>? fields, IReadOnlyCollection<FormKey>? wanted,
                              out string? armStatement, out bool covers, out string? error, out PoleInfo? offOrderArm)
    {
        error = null; covers = true; offOrderArm = null;
        switch (spec.Kind)
        {
            case PoleKind.Winner:
                armStatement = "winner";
                return (fk, _) =>
                {
                    var w = view.ResolveWinner(fk);
                    if (w is null)
                        return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                    var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the winner body of {fk} could not be read from '{w.Value.WinnerPlugin}'.");
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                                           new DiffPole(w.Value.WinnerPlugin, "winner (active order)", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                };

            case PoleKind.PreviousProvider:
                // Subject-relative: resolved per record against the touching list, anchored on the plugin the
                // subject resolved to for that record. Always active-order, since the touching list is the order's.
                armStatement = spec.Label;
                return (fk, subjectPlugin) =>
                {
                    var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                    if (touchers.Count == 0)
                        return new PoleReading(null, null, null, $"no active plugin touches {fk} — there is no provider stack to measure previous_provider in.");
                    int idx = -1;
                    for (int i = 0; i < touchers.Count; i++)
                        if (string.Equals(touchers[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                    if (idx < 0)   // the subject doesn't touch the record at all
                        return new PoleReading(null, null, null,
                            $"the subject '{subjectPlugin}' does not touch {fk}, so it has no position to measure previous_provider from. " +
                            $"Touched by (active order, winner last): {string.Join(", ", touchers)}.");
                    if (idx == 0)  // the subject IS the origin; never a silent empty diff
                        return new PoleReading(null, null, null,
                            $"no previous provider — '{subjectPlugin}' DEFINES {fk} (bottom of the touching list); there is nothing beneath it to compare against.");
                    var refPlugin = touchers[idx - 1];
                    var body = view.GetRecord(session, refPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the previous provider '{refPlugin}' of {fk} could not be read.");
                    // Mid-stack subject: what sits above is surfaced as neutral fact, never advice.
                    IReadOnlyList<string>? above = idx < touchers.Count - 1 ? touchers.Skip(idx + 1).ToList() : null;
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                                           new DiffPole(refPlugin, $"previous provider (immediately below '{subjectPlugin}')", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), above, null);
                };

            case PoleKind.Overlay:
                return MakeOverlayPoleReader(view, session, spec, fields, out armStatement, out covers, out error);

            default:   // Named — the one-pole rule: active in the order, else an on-disk file.
                var (arm, armErr) = ResolvePoleArm(view, spec.Plugin!, spec.Mod);
                if (armErr is not null) { armStatement = null; error = armErr; return (_, _) => new PoleReading(null, null, null, armErr); }
                armStatement = $"{arm!.Plugin} — {arm.Where}";
                if (arm.InOrder)
                {
                    if (view.ExcludedPlugins.TryGetValue(arm.Plugin, out var why))
                    {
                        var exclMsg = $"'{arm.Plugin}' was excluded from this session ({why}) — its records aren't resolvable.";
                        error = exclMsg;
                        return (_, _) => new PoleReading(null, null, null, exclMsg);
                    }
                    return (fk, _) =>
                    {
                        var body = view.GetRecord(session, arm.Plugin, fk);
                        if (body is null)
                        {
                            // Name the actual touchers, never a silent absence.
                            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                            return new PoleReading(null, null, null,
                                $"'{arm.Plugin}' does not define or override {fk} — it has no version of this record. " +
                                (touchers.Count > 0
                                    ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                    : "No active plugin touches it either."));
                        }
                        return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                                               new DiffPole(arm.Plugin, arm.Where, true,
                                                            RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                    };
                }
                // Off-order arm: open the overlay lazily once; per-record lookups sweep it on first use and memoise
                // every record seen on the way, so one enumeration pass serves the whole batch.
                covers = false;   // the file's content sits outside the epoch fingerprint
                offOrderArm = arm;
                var lazy = new OffOrderPoleCache(this, arm, fields, wanted);
                return (fk, _) =>
                {
                    var (rec, oerr) = lazy.Find(fk);
                    if (oerr is not null) return new PoleReading(null, null, null, oerr);
                    if (rec is null)
                    {
                        var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                        return new PoleReading(null, null, null,
                            $"file '{arm.Plugin}' ({arm.Where}) does not define or override {fk} — it has no version of this record. " +
                            (touchers.Count > 0
                                ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                : "No active plugin touches it either."));
                    }
                    return new PoleReading(rec, new DiffPole(arm.Plugin, arm.Where, false, rec.Type, rec.EditorId)
                                                { Qualifier = arm.Layer ?? "off-order" }, null, null);
                };
        }
    }

    /// <summary>The SkyPatcher-overlay pole (source={overlay:"skypatcher", state:"pre"|"post"}). <c>pre</c> IS the
    /// plain load-order winner, the body the INI layer starts from, labelled as the overlay's pre state so a
    /// pre-versus-post delta's two arms read as a pair. <c>post</c> replays the discovered INI layer onto a mutable
    /// copy of each record's winner through the same per-record core the SkyPatcher read uses. INI content sits
    /// outside the epoch fingerprint, so <paramref name="covers"/> is false on the post arm and the render declares
    /// it. A record whose type SkyPatcher cannot patch reads as its winner with that stated on the pole line: post
    /// IS pre there, which is an answer rather than an error.</summary>
    PoleReader MakeOverlayPoleReader(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     PoleSpec spec, IReadOnlyList<string>? fields,
                                     out string? armStatement, out bool covers, out string? error)
    {
        error = null;
        var state = (spec.OverlayState ?? "post").Trim().ToLowerInvariant();
        if (state is not ("pre" or "post"))
        {
            armStatement = null; covers = true;
            error = $"overlay state '{spec.OverlayState}' is not recognized — use \"pre\" (the winner before the INI layer) or \"post\" (after it; the default).";
            var msg = error;
            return (_, _) => new PoleReading(null, null, null, msg);
        }

        if (state == "pre")
        {
            covers = true;
            armStatement = "skypatcher overlay (pre) — the plain load-order winner, before the INI layer";
            return (fk, _) =>
            {
                var w = view.ResolveWinner(fk);
                if (w is null) return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                var body = view.GetRecord(session, w.Value.WinnerPlugin, fk);
                if (body is null) return new PoleReading(null, null, null, $"the winner body of {fk} could not be read from '{w.Value.WinnerPlugin}'.");
                return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                                       new DiffPole(w.Value.WinnerPlugin, "skypatcher overlay (pre) = winner", true,
                                                    RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
            };
        }

        covers = false;   // the INI layer's files are outside the index fingerprint
        armStatement = "skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays";
        // The replay context is built lazily once for the whole batch: discovery scan, catalogs, scratch mod, form
        // resolver and per-folder line cache.
        SkyPatcherFieldMap? fieldMap = null; SkyPatcherCatalog? catalog = null;
        SkyPatcherDiscovery.LayerScan? scan = null; SkyrimMod? scratch = null;
        SkyPatcherOverlay.IFormResolver? formResolver = null;
        Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>? linesCache = null;
        // Per-key memo: the scratch mod is shared across the reader's lifetime, so a repeated key's second replay
        // would re-apply every INI line onto the already-mutated copy. One replay per key.
        var postMemo = new Dictionary<FormKey, PoleReading>();
        string? setupError = null;
        return (fk, _) =>
        {
            if (setupError is not null) return new PoleReading(null, null, null, setupError);
            if (postMemo.TryGetValue(fk, out var memoized)) return memoized;
            if (scan is null)
            {
                try
                {
                    AssetResolver.AssetView assets;
                    lock (_gate) { assets = Assets.Capture(); }
                    fieldMap = SkyPatcherFieldMap.Load();
                    catalog = SkyPatcherCatalog.Load();
                    scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
                    scratch = new SkyrimMod(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
                    formResolver = new SkyPatcherServiceResolver(this, view, session);
                    linesCache = new Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>(StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    setupError = $"the SkyPatcher layer could not be discovered for the overlay pole: {ex.Message}";
                    return new PoleReading(null, null, null, setupError);
                }
            }
            var r = ReplaySkyPatcher(view, session, scan, catalog!, fieldMap!, scratch!, formResolver!, fk, linesCache);
            if (r.Error is not null)
            {
                // An unpatchable type is an answer, not a failure: the layer cannot touch it, so post IS pre.
                if (r.TypeName is not null && r.Copy is null && r.Error.Contains("not a SkyPatcher-patchable type"))
                {
                    var w = view.ResolveWinner(fk);
                    var body = w is null ? null : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    if (body is not null)
                        return postMemo[fk] = new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth),
                                               new DiffPole(w!.Value.WinnerPlugin,
                                                            "skypatcher overlay (post) = winner — type not SkyPatcher-patchable, the layer cannot touch it",
                                                            true, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                }
                return postMemo[fk] = new PoleReading(null, null, null, r.Error);
            }
            int applied = r.Folders.Where(f => f.Result is not null).Sum(f => f.Result!.Applied.Count);
            var post = ReadEngine.ReadFields(r.Copy!, fields, ConflictDiffDepth);
            return postMemo[fk] = new PoleReading(post,
                new DiffPole(r.WinnerPlugin!, $"skypatcher overlay (post) — {applied} op(s) applied onto the winner", true,
                             post.Type, r.EditorId), null, null);
        };
    }

    /// <summary>The off-order pole's lazy single-pass cache: opens the file's overlay on first lookup and sweeps it
    /// once, materialising every wanted record's deep fields as a value snapshot. The overlay is disposed at the end
    /// of the sweep, so no handle is held at rest, and a miss after the full sweep is definitive.</summary>
    sealed class OffOrderPoleCache
    {
        readonly LoadOrderService _svc;
        readonly PoleInfo _arm;
        readonly IReadOnlyList<string>? _fields;
        readonly HashSet<FormKey>? _wanted;   // materialize only the requested keys, never the whole file
        Dictionary<FormKey, RecordFields>? _all;
        string? _error;

        public OffOrderPoleCache(LoadOrderService svc, PoleInfo arm, IReadOnlyList<string>? fields,
                                 IReadOnlyCollection<FormKey>? wanted)
        { _svc = svc; _arm = arm; _fields = fields; _wanted = wanted is null ? null : new HashSet<FormKey>(wanted); }

        public (RecordFields? Fields, string? Error) Find(FormKey fk)
        {
            if (_error is not null) return (null, _error);
            if (_all is null && Sweep() is { } err) { _error = err; return (null, err); }
            return (_all!.TryGetValue(fk, out var rec) ? rec : null, null);
        }

        string? Sweep()
        {
            string dataDir;
            try { lock (_svc._gate) { _svc.EnsurePathsDerived(); dataDir = _svc._dataDir; } }
            catch (Exception ex) { return $"the MO2 roots couldn't be derived to open '{_arm.Plugin}': {ex.Message}"; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(_arm.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex) { return $"could not open '{_arm.Path}' as a Skyrim plugin: {ex.Message}"; }
            try
            {
                var all = new Dictionary<FormKey, RecordFields>();
                foreach (var r in ov.EnumerateMajorRecords())
                {
                    if (_wanted is not null && !_wanted.Contains(r.FormKey)) continue;   // one pass, only what was asked
                    if (!all.ContainsKey(r.FormKey))
                        all[r.FormKey] = ReadEngine.ReadFields(r, _fields, ConflictDiffDepth);
                    if (_wanted is not null && all.Count == _wanted.Count) break;
                }
                _all = all;
                return null;
            }
            catch (Exception ex) { return $"file '{_arm.Plugin}' could not be fully read — a record Mutagen cannot parse: {ex.Message}"; }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>The list-driven `records` read under the SkyPatcher-overlay post source: each record's winner is
    /// replayed through the discovered INI layer and the replayed body is what the projection reads, at the caller's
    /// own depth. A record whose type SkyPatcher cannot patch reads as its plain winner — the layer cannot touch it,
    /// so post IS pre there — and that rule is declared on the envelope rather than left to per-item silence. INI
    /// content sits outside the epoch fingerprint, which the caller also declares on the envelope.</summary>
    public IReadOnlyList<ReadOutcome> OverlayPostBatch(
        IReadOnlyList<string> formids, IReadOnlyList<string>? fields, int depth, bool resolveNames,
        ArtifactDemand? demand, out string? refusal, out string? refusalEpoch, out string? epoch,
        string? containerHint = ReadEngine.DepthExpandHint)
    {
        refusal = null; refusalEpoch = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Epoch;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);
        using var session = resolver.OpenSession();

        SkyPatcherFieldMap fieldMap; SkyPatcherCatalog catalog; SkyPatcherDiscovery.LayerScan scan;
        SkyrimMod scratch; SkyPatcherOverlay.IFormResolver formResolver;
        try
        {
            AssetResolver.AssetView assets;
            lock (_gate) { assets = Assets.Capture(); }
            fieldMap = SkyPatcherFieldMap.Load();
            catalog = SkyPatcherCatalog.Load();
            scan = SkyPatcherDiscovery.Scan(assets, catalog, view.ContainsPlugin, _skyPatcherParseCache);
            scratch = new SkyrimMod(SkyPatcherScratchKey, SkyrimRelease.SkyrimSE);
            formResolver = new SkyPatcherServiceResolver(this, view, session);
        }
        catch (Exception ex)
        {
            refusal = $"the SkyPatcher layer could not be discovered for the overlay source: {ex.Message}";
            refusalEpoch = view.Epoch;
            return Array.Empty<ReadOutcome>();
        }
        var linesCache = new Dictionary<string, IReadOnlyList<SkyPatcherOverlay.OrderedLine>>(StringComparer.OrdinalIgnoreCase);

        // Per-batch replay memo: the scratch mod is shared across the batch, so a duplicated key's second replay
        // would run every INI line onto the already-mutated copy — AddEntry would append twice, Mult and AddNumeric
        // would compound. One replay per key; duplicates reuse its outcome.
        var replayMemo = new Dictionary<FormKey, ReadOutcome>();
        LinkMemo? overlayLinkMemo = null;   // resolve_names cache, one per batch
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            if (replayMemo.TryGetValue(fk, out var memoized)) { outcomes.Add(memoized); continue; }
            var winner = view.ResolveWinner(fk);
            if (winner is null)
            {
                var miss = ReadOutcome.Fail(fk, UnresolvedFormId(view, fk))
                           with { Epoch = view.Epoch, Pin = pin };
                replayMemo[fk] = miss; outcomes.Add(miss);
                continue;
            }
            var r = ReplaySkyPatcher(view, session, scan, catalog, fieldMap, scratch, formResolver, fk, linesCache);
            IMajorRecordGetter? bodyToRead = r.Copy;
            if (r.Error is not null)
            {
                if (r.Error.Contains("not a SkyPatcher-patchable type"))
                    bodyToRead = view.GetRecord(session, winner.Value.WinnerPlugin, fk);   // post IS pre for an unpatchable type
                if (bodyToRead is null)
                {
                    var fail = ReadOutcome.Fail(fk, r.Error) with { Epoch = view.Epoch, Pin = pin };
                    replayMemo[fk] = fail; outcomes.Add(fail);
                    continue;
                }
            }
            var record = ReadEngine.ReadFields(bodyToRead!, fields, depth, containerHint);
            if (resolveNames) record = AnnotateLinks(record, view, session, overlayLinkMemo ??= new LinkMemo());
            var ok = (new ReadOutcome(fk, record, winner.Value.WinnerPlugin, winner.Value.WinnerPlugin,
                                      winner.Value.OverrideDepth, null, null)
                      with { Epoch = view.Epoch, Pin = pin }).WithRuntime(view.RuntimeAddressOf(fk));
            replayMemo[fk] = ok; outcomes.Add(ok);
        }
        return outcomes;
    }

    /// <summary>One provider's node in a project=tree row: its position in the touching list plus its delta against
    /// the row's reference pole. Empty deltas together with Complete means genuinely identical to the
    /// reference.</summary>
    public sealed record TreeNodeDelta(string Plugin, bool IsWinner, bool IsReference,
                                       IReadOnlyList<string> Deltas, int AgreedCount, bool Complete, string? Error);

    /// <summary>One record's project=tree row: every provider in priority order, winner last — the load order's own
    /// reading direction — each diffed against the reference pole. A non-null Error is a per-item refusal.
    /// <para><see cref="ChildDeclarers"/> is the precise owned-child answer for this record, read off the same
    /// provider bodies the deltas came from; empty for a record whose type owns no child records, and for every
    /// error row. It is a required constructor parameter rather than a defaulted one, so a new row site cannot
    /// ship it silently empty.</para></summary>
    public sealed record TreeRow(string Formid, string? Type, string? EditorId,
                                 IReadOnlyList<string> Touchers, string? ReferencePlugin,
                                 IReadOnlyList<TreeNodeDelta> Nodes, string? Error,
                                 IReadOnlyList<ChildDeclarers> ChildDeclarers);

    /// <summary>The project=tree batch: per record, the full provider stack (touching list, winner last) with each
    /// provider diffed against the reference pole — the winner by default, or a named plugin, active or off-order
    /// under the one-pole rule, with untouched records refused by naming the touchers. One captured build for
    /// everything.</summary>
    public IReadOnlyList<TreeRow> TreeBatch(
        IReadOnlyList<string> formids, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? referenceArm, out bool epochCoversAll, out string? refusal, out string? epoch)
    {
        referenceArm = null; epochCoversAll = true; refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Epoch;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<TreeRow>();
        }
        using var session = resolver.OpenSession();

        // A winner reference reads each node off the tree itself; a named reference resolves through the same pole
        // reader the delta form uses. Pre-parse for the same reason as DeltaBatch: a named off-order reference then
        // materializes only these keys.
        var parsedT = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wantedT = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsedT.Add((raw, fk0, null)); wantedT.Add(fk0); }
            catch (Exception ex) { parsedT.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        PoleReader? refReader = null;
        if (reference.Kind is not PoleKind.Winner)
        {
            refReader = MakePoleReader(view, session, reference, fields, wantedT, out referenceArm, out var rCovers, out var rErr, out _);
            if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<TreeRow>(); }
            epochCoversAll = rCovers;
        }
        else referenceArm = "winner";

        var rows = new List<TreeRow>(formids.Count);
        foreach (var (raw, fkOpt, parseError) in parsedT)
        {
            if (parseError is not null) { rows.Add(new TreeRow(raw?.Trim() ?? "", null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(), parseError, Array.Empty<ChildDeclarers>())); continue; }
            var fk = fkOpt!.Value;

            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
            if (touchers.Count == 0)
            {
                rows.Add(new TreeRow(fk.ToString(), null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(),
                                     UnresolvedFormId(view, fk), Array.Empty<ChildDeclarers>()));
                continue;
            }
            var tree = ResolveTreePinned(new ViewPin(resolver, view), fk, fields);
            if (tree is null || tree.Nodes.Count == 0)
            {
                rows.Add(new TreeRow(fk.ToString(), null, null, touchers, null, Array.Empty<TreeNodeDelta>(),
                                     $"the provider bodies of {fk} could not be read.", Array.Empty<ChildDeclarers>()));
                continue;
            }

            RecordFields? refFields; string refPlugin; DiffPole? refPole = null;
            if (refReader is null)
            {
                var winnerNode = tree.Winner;
                refFields = winnerNode.Record; refPlugin = winnerNode.Plugin;
            }
            else
            {
                var r = refReader(fk, null);
                if (r.Error is not null)
                {
                    rows.Add(new TreeRow(fk.ToString(), tree.Winner.Record.Type, tree.Winner.Record.EditorId,
                                         touchers, null, Array.Empty<TreeNodeDelta>(), "versus: " + r.Error,
                                         Array.Empty<ChildDeclarers>()));
                    continue;
                }
                refFields = r.Fields; refPlugin = r.Pole!.Plugin; refPole = r.Pole;
            }

            // A node IS the reference only when the reference resolved IN the order: an off-order pole is never one
            // of the active providers, even when its filename is also active as a different file. Where they share
            // that filename, the reference's label names its mod folder so the two are told apart.
            bool refIsActiveProvider = refPole is null || refPole.InOrder;
            string refLabel = refPole is not null && tree.Nodes.Any(n => string.Equals(n.Plugin, refPlugin, StringComparison.OrdinalIgnoreCase))
                            ? refPole.LabelVersus(refPlugin) : refPlugin;

            var nodes = new List<TreeNodeDelta>(tree.Nodes.Count);
            foreach (var node in tree.Nodes)
            {
                bool isWinner = ReferenceEquals(node, tree.Winner);
                bool isRef = refReader is null ? isWinner
                           : refIsActiveProvider && string.Equals(node.Plugin, refPlugin, StringComparison.OrdinalIgnoreCase);
                if (isRef)
                {
                    nodes.Add(new TreeNodeDelta(node.Plugin, isWinner, true, Array.Empty<string>(), 0, true, null));
                    continue;
                }
                var d = FieldsDiff.Compare(node.Record, refFields!, referenceLabel: refLabel);
                nodes.Add(new TreeNodeDelta(node.Plugin, isWinner, false, d.Deltas, d.AgreedCount, d.Complete, null));
            }
            rows.Add(new TreeRow(fk.ToString(), tree.Winner.Record.Type, tree.Winner.Record.EditorId,
                                 touchers, refLabel, nodes, null, tree.ChildDeclarers));
        }
        return rows;
    }

    // ---- the traversal construct (walk=) ---------------------------------------------------------------

    /// <summary>One record the walk reached: its identity, its provenance (<see cref="PulledBy"/> — the parent
    /// node's label) and whether the walk entered it or recorded it as a boundary. A boundary's reason — an
    /// exclusion stop, the depth cap, an unresolved link — rides in <see cref="Note"/>.</summary>
    public sealed record WalkNodeRow(string Key, string? Type, string? EditorId, int Depth,
                                     string PulledBy, string Status, string? Note);

    /// <summary>The NPC_ TemplateFlags typed interpreter — deliberately the only such interpreter until a gap
    /// report demands a second: per inheritance category, whether the seed inherits it (masking its own local data)
    /// and which record in the template chain actually provides it.</summary>
    public sealed record NpcTemplateCategory(string Category, bool InheritedAtSeed,
                                             string? ProviderKey, string? ProviderEditorId, string? Note);

    /// <summary>One seed's walk: the reached nodes in BFS order with provenance; recorded cycles, which only a
    /// named-follow chain produces since a closure walk dedupes on its visited set; the truncation note when a cap
    /// cut the walk, keeping what was proved and saying what was not; and, for an NPC_ seed under
    /// follow="Template", the per-category inheritance report.</summary>
    public sealed record WalkSeedResult(string Seed, string? Type, string? EditorId,
                                        IReadOnlyList<WalkNodeRow> Nodes, IReadOnlyList<string> Cycles,
                                        string? TruncationNote, IReadOnlyList<NpcTemplateCategory>? TemplateReport,
                                        string? Error);

    /// <summary>The forward walk over the winner link graph, per seed off ONE captured build. The edge unit is the
    /// form link; within-record navigation stays the projection's path grammar. seed_paths scope the FIRST hop
    /// (default: every link); follow scopes every later hop (default "*" is closure via the generic
    /// EnumerateFormLinks, so there is no per-type list; a named path is a restricted chain). Exclusions are data
    /// handed in by the caller: stop prunes and records a boundary, refuse fails the whole call loudly naming the
    /// seed and pull chain. Caps produce an explicit truncation note, never a silent cut.</summary>
    public IReadOnlyList<WalkSeedResult> WalkForwardBatch(
        IReadOnlyList<string> seeds, IReadOnlyList<string>? seedPaths, string? follow,
        int depth, int maxNodes, IReadOnlyList<(string Match, bool Refuse)> exclusions,
        ArtifactDemand? demand, out string? refusal, out string? epoch)
    {
        refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Epoch;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<WalkSeedResult>();
        }
        using var session = resolver.OpenSession();

        string[]? followSegs = null;
        bool closure = string.IsNullOrWhiteSpace(follow) || follow!.Trim() == "*";
        if (!closure)
        {
            followSegs = follow!.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (followSegs.Length == 0) { refusal = $"walk.follow '{follow}' is not a usable field path."; return Array.Empty<WalkSeedResult>(); }
        }

        var bodyCache = new Dictionary<FormKey, IMajorRecordGetter?>();
        IMajorRecordGetter? Fetch(FormKey k)
        {
            if (bodyCache.TryGetValue(k, out var c)) return c;
            IMajorRecordGetter? g = view.ResolveWinner(k) is { } w ? view.GetRecord(session, w.WinnerPlugin, k) : null;
            bodyCache[k] = g;
            return g;
        }
        static string TypeOf(IMajorRecordGetter b) => RecordNaming.StripOverlay(b.GetType().Name);
        List<FormKey> LinksOf(IMajorRecordGetter body, string[]? segs, out string? note)
        {
            note = null;
            if (segs is null)
            {
                var seen = new HashSet<FormKey>();
                var list = new List<FormKey>();
                if (body is Mutagen.Bethesda.Plugins.Records.IFormLinkContainerGetter flc)
                    foreach (var link in flc.EnumerateFormLinks())
                        if (!link.FormKey.IsNull && seen.Add(link.FormKey)) list.Add(link.FormKey);
                return list;
            }
            var (links, n) = ReadEngine.CollectLinksAt(body, segs);
            note = n;
            return links ?? new List<FormKey>();
        }

        var results = new List<WalkSeedResult>(seeds.Count);
        foreach (var raw in seeds)
        {
            FormKey seedFk;
            try { seedFk = view.ParseFormId(raw); }
            catch (Exception ex) { results.Add(new WalkSeedResult(raw?.Trim() ?? "", null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null, $"bad FormID '{raw}': {ex.Message}")); continue; }
            var seedBody = Fetch(seedFk);
            if (seedBody is null)
            {
                // Fetch returns null for two conditions and they need different sentences: no winner at all (the
                // three-cause unresolved sentence), or a named winner whose body did not come back on fetch.
                var seedWin = view.ResolveWinner(seedFk);
                results.Add(new WalkSeedResult(seedFk.ToString(), null, null, Array.Empty<WalkNodeRow>(), Array.Empty<string>(), null, null,
                    seedWin is null
                        ? UnresolvedFormId(view, seedFk) + " Nothing to walk from."
                        : $"the winner body of {seedFk} could not be read from '{seedWin.Value.WinnerPlugin}' — nothing to walk from."));
                continue;
            }
            var seedType = TypeOf(seedBody);
            var seedLabel = $"{seedType} {seedFk} ({seedBody.EditorID ?? "<no editorid>"})";

            var nodes = new List<WalkNodeRow>();
            var cycles = new List<string>();
            string? truncation = null;
            var visited = new HashSet<FormKey> { seedFk };
            var queue = new Queue<(FormKey Key, int Depth, string PulledBy)>();

            // First hop: seed_paths (each path's links) or every link on the seed.
            if (seedPaths is { Count: > 0 })
            {
                foreach (var p in seedPaths)
                {
                    var segs = p.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (segs.Length == 0) continue;
                    var links = LinksOf(seedBody, segs, out var note);
                    if (links.Count == 0 && note is not null)
                        nodes.Add(new WalkNodeRow($"(seed path '{p}')", null, null, 0, seedLabel, "no links", note));   // a wrong path fails loudly in the rows
                    foreach (var l in links) queue.Enqueue((l, 1, $"{seedLabel}.{p}"));
                }
            }
            else
            {
                foreach (var l in LinksOf(seedBody, null, out _)) queue.Enqueue((l, 1, seedLabel));
            }

            string? refuseError = null;
            while (queue.Count > 0 && refuseError is null)
            {
                var (key, d, pulledBy) = queue.Dequeue();
                if (key.IsNull) continue;
                if (!visited.Add(key))
                {
                    // A named-follow walk is a linear chain per seed, so a revisit IS a cycle: recorded and named,
                    // never looped and never silently stopped. Closure walks dedupe on the visited set instead.
                    if (followSegs is not null) cycles.Add($"{pulledBy} -> {key} (already on this chain)");
                    continue;
                }
                if (nodes.Count >= maxNodes)
                {
                    truncation = $"walk truncated: the {maxNodes}-node cap was reached — what is listed IS reached and proved; raise walk.max_nodes to walk further.";
                    break;
                }
                var body = Fetch(key);
                if (body is null)
                {
                    nodes.Add(new WalkNodeRow(key.ToString(), null, null, d, pulledBy, "kept",
                                              "unresolved — no active plugin defines this target (a missing endpoint)"));
                    continue;
                }
                var type = TypeOf(body);
                var excl = exclusions.FirstOrDefault(x => x.Match.Equals(type, StringComparison.OrdinalIgnoreCase));
                if (excl.Match is not null)
                {
                    if (excl.Refuse) { refuseError = $"the walk reached a {type} ({key}, via {pulledBy}) — a node class this call excludes with severity 'refuse'. Nothing is returned for this call."; break; }
                    nodes.Add(new WalkNodeRow(key.ToString(), type, body.EditorID, d, pulledBy, "kept", $"excluded ({type}, severity stop) — recorded as a boundary, not entered"));
                    continue;
                }
                bool atCap = d >= depth;
                nodes.Add(new WalkNodeRow(key.ToString(), type, body.EditorID, d, pulledBy,
                                          atCap ? "kept" : "expanded",
                                          atCap ? $"at the walk.depth cap ({depth}) — not entered" : null));
                if (atCap)
                {
                    truncation ??= $"walk reached its depth cap ({depth}) on at least one chain — nodes at the cap are recorded, not entered; raise walk.depth to walk deeper.";
                    continue;
                }
                var label = $"{type} {key} ({body.EditorID ?? "<no editorid>"})";
                foreach (var l in LinksOf(body, followSegs, out _))
                    if (!l.IsNull) queue.Enqueue((l, d + 1, label));
            }
            if (refuseError is not null)
            {
                refusal = refuseError;
                return Array.Empty<WalkSeedResult>();
            }

            IReadOnlyList<NpcTemplateCategory>? templateReport = null;
            if (followSegs is { Length: 1 } && followSegs[0].Equals("Template", StringComparison.OrdinalIgnoreCase)
                && seedBody is INpcGetter seedNpc)
                templateReport = NpcTemplateReport(Fetch, seedNpc, seedFk);

            results.Add(new WalkSeedResult(seedFk.ToString(), seedType, seedBody.EditorID, nodes, cycles, truncation, templateReport, null));
        }
        return results;
    }

    /// <summary>The NPC_ TemplateFlags interpreter: a SET flag means the category is inherited and the seed's own
    /// local data for it is masked; the provider is the first record down the template chain whose flag for that
    /// category is CLEAR, so its own data is active. A chain ending in a leveled actor resolves at runtime, and a
    /// broken or missing link is reported rather than guessed.</summary>
    static IReadOnlyList<NpcTemplateCategory> NpcTemplateReport(Func<FormKey, IMajorRecordGetter?> fetch,
                                                                INpcGetter seed, FormKey seedFk)
    {
        var report = new List<NpcTemplateCategory>();
        foreach (NpcConfiguration.TemplateFlag flag in Enum.GetValues(typeof(NpcConfiguration.TemplateFlag)))
        {
            var name = flag.ToString();
            if (!seed.Configuration.TemplateFlags.HasFlag(flag))
            {
                report.Add(new NpcTemplateCategory(name, false, seedFk.ToString(), seed.EditorID,
                                                   "local data ACTIVE (flag clear)"));
                continue;
            }
            // Walk down: the provider is the first node NOT forwarding this category.
            var cur = seed;
            string? note = null; string? provKey = null; string? provEid = null;
            var hops = new HashSet<FormKey> { seedFk };
            while (true)
            {
                var t = cur.Template;
                if (t is null || t.IsNull) { note = "flag SET but the template link is empty — the category inherits from nothing (worth a look)"; break; }
                var nextKey = t.FormKey;
                if (!hops.Add(nextKey)) { note = $"template chain CYCLES at {nextKey} — no provider is reachable"; break; }
                var body = fetch(nextKey);
                if (body is null) { note = $"template target {nextKey} is unresolved — the chain is broken here"; break; }
                if (body is ILeveledNpcGetter lvln)
                { provKey = nextKey.ToString(); provEid = lvln.EditorID; note = "a LEVELED actor — the concrete provider is rolled at runtime"; break; }
                if (body is not INpcGetter npc)
                { note = $"template target {nextKey} is a {RecordNaming.StripOverlay(body.GetType().Name)}, not an NPC or leveled actor"; break; }
                if (!npc.Configuration.TemplateFlags.HasFlag(flag))
                { provKey = nextKey.ToString(); provEid = npc.EditorID; break; }
                cur = npc;
            }
            report.Add(new NpcTemplateCategory(name, true, provKey, provEid, note));
        }
        return report;
    }

    // ---- the info_order projection form ----------------------------------------------------------------

    /// <summary>One topic's effective-INFO-order row: the merged sequence with its honesty gates (Complete,
    /// MovesComputed and BaselineTrusted ride inside <see cref="Order"/>). A non-null Error is a per-item refusal —
    /// a bad FormID, an absent record, or a non-DIAL target named by its actual type.</summary>
    public sealed record InfoOrderRow(string Formid, string? Type, string? EditorId, string? WinnerPlugin,
                                      InfoOrderView? Order, string? Error);

    /// <summary>The form='info_order' batch: per DIAL topic, the effective merged INFO order across every touching
    /// plugin — the game's own walk order — off ONE captured build. It is epoch-stamped, because this form reads
    /// plugin records through the index only, with no VFS or INI layer. A non-DIAL FormID is a per-item typed
    /// refusal: a quest's topics are selected by composition (types=["DIAL"] where=["Quest = &lt;quest formid&gt;"])
    /// rather than by silently fanning out here.</summary>
    public IReadOnlyList<InfoOrderRow> InfoOrderBatch(IReadOnlyList<string> formids, ArtifactDemand? demand,
                                                      out string? refusal, out string? epoch)
    {
        refusal = null;
        var resolver = Resolver;
        var view = resolver.Capture();
        epoch = view.Epoch;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<InfoOrderRow>();
        }
        using var session = resolver.OpenSession();

        var rows = new List<InfoOrderRow>(formids.Count);
        var dialFks = new List<FormKey>();
        // Per ROW, not per FormKey: a duplicated DIAL key in the input must attach the computed order to every
        // occurrence, and a dictionary keyed on FormKey would keep only the last row's index, leaving the earlier
        // duplicates rendering a fabricated "merge could not be computed" failure.
        var dialRows = new List<(int Index, FormKey Fk)>();
        var dialSeen = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { rows.Add(new InfoOrderRow(raw?.Trim() ?? "", null, null, null, null, $"bad FormID '{raw}': {ex.Message}")); continue; }
            var win = view.ResolveWinner(fk);
            if (win is null)
            {
                rows.Add(new InfoOrderRow(fk.ToString(), null, null, null, null, UnresolvedFormId(view, fk)));
                continue;
            }
            var body = view.GetRecord(session, win.Value.WinnerPlugin, fk);
            if (body is null)
            {
                rows.Add(new InfoOrderRow(fk.ToString(), null, null, win.Value.WinnerPlugin, null,
                                          $"the winner body of {fk} could not be read from '{win.Value.WinnerPlugin}'."));
                continue;
            }
            if (body is not Mutagen.Bethesda.Skyrim.IDialogTopicGetter)
            {
                var typeName = RecordNaming.StripOverlay(body.GetType().Name);
                rows.Add(new InfoOrderRow(fk.ToString(), typeName, body.EditorID, win.Value.WinnerPlugin, null,
                    $"{fk} is a {typeName}, and the info_order form renders the merged INFO sequence of a DIALOGUE TOPIC (DIAL). " +
                    "For a quest's topics, select them by composition: types=[\"DIAL\"] where=[\"Quest = " + fk + "\"]."));
                continue;
            }
            dialRows.Add((rows.Count, fk));
            rows.Add(new InfoOrderRow(fk.ToString(), RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                      win.Value.WinnerPlugin, null, null));
            if (dialSeen.Add(fk)) dialFks.Add(fk);
        }
        if (dialFks.Count > 0)
        {
            var orders = DialogueValidate.InfoOrders(view, session, dialFks);
            foreach (var (idx, fk) in dialRows)
                if (orders.TryGetValue(fk, out var io)) rows[idx] = rows[idx] with { Order = io };
        }
        return rows;
    }

    // ---- cross-plugin query ----------------------------------------------------------------------------

    /// <summary>Scan the order for records matching a filter, in a SINGLE enumeration pass with the matching
    /// record's body in hand so nothing is re-fetched per candidate: type= streams the winner body via typed group
    /// enumeration; plugins= streams each scoped plugin's own body; conflicts_only= alone reads the index. Body
    /// filters test the in-hand body and so need type= or plugins= to bound them. <paramref name="references"/> is a
    /// list — a record matches if it references ANY target, and each match records which targets it hit.
    /// <paramref name="definedIn"/> keeps only matches whose FormKey originates in a scoped plugin (definitions, not
    /// overrides) and requires plugins=, refused loudly otherwise. <paramref name="groupBy"/> replaces per-match
    /// lines with a count table over ALL matches, uncapped by limit=. <paramref name="offset"/> skips the first N
    /// post-filter matches; scan order is deterministic for an unchanged load order, so offset and limit windows
    /// tile without gaps or overlap, and the true total still counts all matches. Returns pre-built match summaries
    /// capped at <paramref name="limit"/> with the true total, a group table, or a recoverable error. Holds
    /// nothing.</summary>
    public CrossQueryOutcome CrossQuery(string? type, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn = false, string? groupBy = null, int offset = 0, string? whereSource = null,
                                        IReadOnlyList<ArtifactDemand>? artifactDemands = null,
                                        IReadOnlyList<FormKey>? referencesNone = null)
        => CrossQuery(type is null ? null : new[] { type }, references, editoridContains, conflictsOnly, plugins, where,
                      limit, definedIn, groupBy, offset, whereSource, artifactDemands, referencesNone: referencesNone);

    /// <summary>The formids-by-scan composition: <paramref name="formidSet"/> intersects the selection with an
    /// explicit identity set, inline or artifact-fed. With a body-bearing scope it is a cheap pre-filter on the
    /// stream; alone it IS the scan universe — each key's winner body is fetched and filtered, so a where= over a
    /// formid set needs no types= or plugins= bound.</summary>

    /// <summary>The set-valued-types overload: types= is a set, and one type is a degenerate set. Each entry
    /// resolves through the same <see cref="ResolveTypeFilter"/> the singular form used, and the scan streams the
    /// union of the resolved type groups.</summary>
    public CrossQueryOutcome CrossQuery(IReadOnlyList<string>? typeSet, IReadOnlyList<FormKey>? references, string? editoridContains,
                                        bool conflictsOnly, IReadOnlyList<string>? plugins, IReadOnlyList<string>? where, int limit,
                                        bool definedIn = false, string? groupBy = null, int offset = 0, string? whereSource = null,
                                        IReadOnlyList<ArtifactDemand>? artifactDemands = null,
                                        IReadOnlyList<FormKey>? formidSet = null,
                                        LoadOrderResolver.IndexView? pinnedView = null,
                                        IReadOnlyList<FormKey>? referencesNone = null)
    {
        var resolver = Resolver;
        // The caller's own build when its FormID door already captured one, so the tokens it parsed and the
        // records this scan matches come from ONE build; otherwise one build for the scan and every fill it makes.
        var view = pinnedView ?? resolver.Capture();
        bool hasPlugins = plugins is { Count: > 0 };
        bool hasType = typeSet is { Count: > 0 };
        bool hasWhere = where is { Count: > 0 };
        bool hasReferences = references is { Count: > 0 };
        // The negated half of references=: a record is kept only when it links to NONE of these. Same one-step
        // reverse question, inverted, so it is the same body scan and takes the same bound.
        var refNone = referencesNone is { Count: > 0 } ? new HashSet<FormKey>(referencesNone) : null;
        bool bodyFilter = hasReferences || refNone is not null || !string.IsNullOrEmpty(editoridContains) || hasWhere;
        bool hasFormidSet = formidSet is { Count: > 0 };

        if (!hasType && !conflictsOnly && !hasPlugins && !bodyFilter && !hasFormidSet)
            return CrossQueryOutcome.Fail("a scan needs at least one of: types=, plugins=, formids=, conflicts_only=true, where=, or references=.");
        // A formid set is itself a bound: the scan touches at most those keys, so a body filter over one needs no
        // types= or plugins=.
        if (bodyFilter && !hasType && !hasPlugins && !hasFormidSet)
            return CrossQueryOutcome.Fail("where=/references= is a body scan and must be combined with types=, plugins=, or a formids= set to bound it (conflicts_only= alone is not enough — an unbounded body scan over the whole order is refused). A global reverse-reference index is a future capability.");

        // defined_in= keeps only records defined in the scoped plugins (by origin FormKey), which is distinct from
        // plugins=, meaning everything a plugin touches. It needs a plugins= scope to mean anything, so it is
        // refused loudly rather than silently ignored.
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

        // offset= pages the match window, validated up front: negative is meaningless, and under group_by= there is
        // no match window to page, since the aggregation counts all matches and is never limit-capped. Silently
        // ignoring it would misrepresent what the caller asked for.
        if (offset < 0)
            return CrossQueryOutcome.Fail($"offset={offset} — offset must be >= 0 (it skips that many matches before returning rows).");
        if (offset > 0 && groupBy is not null)
            return CrossQueryOutcome.Fail("group_by= aggregates ALL matches into a count table (never capped by limit=), so offset= has nothing to page — drop offset=, or drop group_by= for per-match rows.");

        // where_source= chooses which body the body filters decide the match on: 'scoped' (default) is the body the
        // scan streams — the scoped plugin's own under plugins=, else the winner — and 'winner' is the live
        // load-order winner regardless of scan scope. Validated up front, so an unknown value refuses before any
        // scan. It retargets the MATCH only; winner_fields= independently governs display, so "match on the winner,
        // show the scoped origin" stays expressible.
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
        // Under a type=-only scope the scan already streams the winner body, so where_source=winner is already
        // satisfied: accept it, but say so rather than silently no-op. Only the scoped-body stream (plugins=) needs
        // the per-match winner re-fetch.
        bool whereWinnerActive = whereWinner && hasPlugins;
        string? whereSourceNote = (whereWinner && !hasPlugins)
            ? "note: where_source=winner is redundant here — a type=-only scan already reads the load-order winner, so the match used the winner regardless."
            : null;

        // group_by= aggregates matches into a count table, validated up front so an unknown key refuses before any
        // scan. group_by=type needs the matched body to name the type, so it requires a body-bearing scope; winner
        // and defined_in are derivable from the FormKey alone and work with conflicts_only= too.
        if (groupBy is not null)
        {
            groupBy = groupBy.Trim().ToLowerInvariant();
            if (groupBy is not ("winner" or "type" or "defined_in"))
                return CrossQueryOutcome.Fail($"group_by='{groupBy}' is not a known aggregation key — use 'winner', 'type', or 'defined_in'.");
            if (groupBy == "type" && !hasType && !hasPlugins && !hasFormidSet)
                return CrossQueryOutcome.Fail("group_by=type needs each match's type, which requires a body-bearing scope — add type= or plugins= (winner/defined_in group without a body).");
        }
        var refSet = hasReferences ? new HashSet<FormKey>(references!) : null;
        bool multiTarget = references is { Count: >= 2 };

        // where= becomes the field-value predicate set, parsed up front so a malformed predicate refuses the call
        // before any scan. The predicate reuses the read engine's path walk, so its reach is the read surface's.
        FieldPredicateSet? predicate = null;
        if (hasWhere)
        {
            var (set, perr) = FieldPredicateSet.Parse(where!, FormIdDoor.On(view).Parse);
            if (perr is not null) return CrossQueryOutcome.Fail(perr);
            predicate = set;
        }

        // Artifact re-entry: every artifact-backed list input carries the epoch its rows were captured at. Checked
        // HERE against the view this scan will answer from, not at the tool layer, where a freshness rebuild between
        // check and scan would let a stale artifact through. A mismatch refuses loudly naming both epochs, and the
        // refusal is stamped because it consulted this build to compare.
        foreach (var demand in (artifactDemands ?? Array.Empty<ArtifactDemand>()).Concat(
                     predicate?.ArtifactDemands ?? (IReadOnlyList<ArtifactDemand>)Array.Empty<ArtifactDemand>()))
            if (demand.Epoch != view.Epoch)
                return CrossQueryOutcome.Fail(ArtifactEpochMismatch(demand, view.Epoch)) with { Epoch = view.Epoch };

        IReadOnlyList<Type>? types;
        try
        {
            if (hasType)
            {
                var union = new List<Type>();
                foreach (var ts in typeSet!)
                    foreach (var t in ResolveTypeFilter(ts.Trim()))
                        if (!union.Contains(t)) union.Add(t);
                types = union;
            }
            else types = null;
        }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }   // unknown type

        if (predicate is not null && hasType && QuantifierShapeRefusal(typeSet!, predicate) is { } qerr)
            return CrossQueryOutcome.Fail(qerr) with { Epoch = view.Epoch };

        var keys = new List<FormKey>();
        var sources = new List<string?>();                                    // parallel to keys: the plugin whose body matched (null ⇒ winner), so the render displays the SAME body it filtered
        List<string?>? matched = multiTarget ? new() : null;                  // parallel to keys: which target(s) each hit referenced (multi-target references= un-merge); null when 0/1 target
        List<RecordSummary>? prefilled = (hasType || hasPlugins || hasFormidSet) ? new() : null;   // parallel to keys; null = renderer fills lazily
        // OrdinalIgnoreCase so case-variant spellings of the SAME plugin — a master listed one way in one plugin's
        // masters and another way in another's — merge into one group instead of splitting the count. Plugin
        // filenames are case-insensitive identifiers everywhere else, and first-seen casing becomes the display key.
        // Harmless for group_by=type, since record type names never differ only by case, so one comparer covers all
        // three keys.
        Dictionary<string, int>? groups = groupBy is not null ? new(StringComparer.OrdinalIgnoreCase) : null;   // group_by= aggregation (bumped per match, over ALL matches — not limit-capped)
        int total = 0;
        int unscannable = 0;                                                  // records whose body tests threw (Mutagen-unparseable content) — excluded and accounted, never silent
        var unscannableSamples = new List<string>();
        // Plugins the winner scan could not open at all — a whole-plugin coverage gap, named in the response rather
        // than left to read as a clean whole-order scan.
        var unreadablePlugins = new List<PluginUnreadableException>();

        HashSet<FormKey>? setFilter = hasFormidSet ? new HashSet<FormKey>(formidSet!) : null;
        // The set-alone branch also owns conflicts_only combined with formidSet, via its in-loop touching-count
        // test: routing that pair to the index-only else-branch would drop every parsed body filter silently.
        if (!hasType && !hasPlugins && hasFormidSet)                          // the formid set ALONE is the universe: per-key winner fetch
        {
            LoadOrderResolver.OverlaySession? setSession = null;
            try
            {
                setSession = resolver.OpenSession();
                var sess = setSession;
                predicate?.BindResolution(
                    fk => view.ResolveWinner(fk)?.WinnerPlugin,
                    predicate.NeedsBodyResolution
                        ? fk =>
                        {
                            var w = view.ResolveWinner(fk);
                            return w is null ? null : view.GetRecord(sess, w.Value.WinnerPlugin, fk);
                        }
                        : null);
                var seenSet = new HashSet<FormKey>();
                foreach (var fk in formidSet!)
                {
                    if (!seenSet.Add(fk)) continue;
                    var w = view.ResolveWinner(fk);
                    if (w is null) continue;                                  // not in the order — a clean non-match for a scan; per-item errors belong to the formids= list lane
                    try
                    {
                        var body = view.GetRecord(setSession, w.Value.WinnerPlugin, fk);
                        if (body is null)
                        {
                            unscannable++;
                            if (unscannableSamples.Count < 3)
                                unscannableSamples.Add($"{fk} — winner '{w.Value.WinnerPlugin}' did not yield the record on fetch");
                            continue;
                        }
                        if (conflictsOnly && (view.TouchingPlugins(fk)?.Count ?? 0) <= 1) continue;
                        if (DeletedRecordRule.HasNoLiveBody(body)
                            && (refSet is not null || predicate is { NeedsLiveBody: true })) continue;
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (body.EditorID is null || body.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        List<FormKey>? hitTargets = null;
                        if (refSet is not null)
                        {
                            if (body is not IFormLinkContainerGetter flc) continue;
                            var hitSet = new HashSet<FormKey>();
                            foreach (var l in flc.EnumerateFormLinks()) if (refSet.Contains(l.FormKey)) hitSet.Add(l.FormKey);
                            if (hitSet.Count == 0) continue;
                            if (multiTarget && groups is null) hitTargets = references!.Where(hitSet.Contains).Distinct().ToList();
                        }
                        if (refNone is not null && ExcludedByReference(body, refNone)) continue;
                        if (predicate is not null && !predicate.Matches(body))
                        {
                            if (predicate.FatalError is not null) break;
                            continue;
                        }
                        total++;
                        if (groups is not null)
                        {
                            var gk = groupBy == "type" ? RecordNaming.StripOverlay(body.GetType().Name)
                                   : groupBy == "defined_in" ? fk.ModKey.FileName.ToString()
                                   : w.Value.WinnerPlugin;
                            groups[gk] = groups.GetValueOrDefault(gk) + 1;
                        }
                        else if (total > offset && keys.Count < limit)
                        {
                            keys.Add(fk);
                            sources.Add(null);                                // the winner body is what matched and displays
                            matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);
                            prefilled?.Add(new RecordSummary(fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID,
                                                             w.Value.WinnerPlugin, w.Value.OverrideDepth, null)
                                           .WithRuntime(view.RuntimeAddressOf(fk)));
                        }
                    }
                    catch (Exception ex)
                    {
                        unscannable++;
                        if (unscannableSamples.Count < 3)
                            unscannableSamples.Add($"{fk} — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally { setSession?.Dispose(); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError);
        }
        else if (hasType || hasPlugins)                                       // a body-bearing scope: stream + filter in hand
        {
            // RecordsIn and WinnerRecordsOfType are lazy iterators: their throws happen on ENUMERATION, not on
            // creation, so the try must wrap the foreach rather than just the assignment, or the clean message
            // escapes as a generic framework error.
            var seen = new HashSet<FormKey>();
            // Under where_source=winner the match decides on the live winner body, fetched via this ONE session —
            // one session for every per-match winner fetch, not one per record. Opened only when the scan streams
            // scoped bodies, since a type=-only scan already yields the winner, and disposed with the scan. The
            // `->` link-step predicate shares the session for its target-body fetches.
            LoadOrderResolver.OverlaySession? winnerSession =
                (whereWinnerActive || predicate is { NeedsBodyResolution: true }) ? resolver.OpenSession() : null;
            // The `winner` provenance term and the `->` link step read the view's resolution — a winner name, or a
            // target's winner body — bound off the SAME captured view the scan answers from, so a predicate can
            // never judge against a different build than the rows.
            predicate?.BindResolution(
                fk => view.ResolveWinner(fk)?.WinnerPlugin,
                predicate.NeedsBodyResolution
                    ? fk =>
                    {
                        var w = view.ResolveWinner(fk);
                        return w is null ? null : view.GetRecord(winnerSession!, w.Value.WinnerPlugin, fk);
                    }
                    : null);
            try
            {
                // Carry the source plugin per record so the render shows the body the scan filtered rather than the
                // winner: plugins= gives the scoped plugin's filename, type= gives null, meaning the winner.
                IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string? source)> stream =
                    hasPlugins ? view.RecordsIn(plugins!, types).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)x.source))  // the scoped plugin's own body
                               : view.WinnerRecordsOfType(types!, unreadablePlugins).Select(x => (fk: x.fk, depth: x.depth, body: x.body, source: (string?)null));    // the load-order winner's body
                foreach (var (fk, depth, body, source) in stream)
                {
                    if (setFilter is not null && !setFilter.Contains(fk)) continue;   // the identity intersection, cheapest first
                    if (conflictsOnly && depth <= 1) continue;
                    // defined_in= keeps only records whose origin FormKey is a scoped plugin — a definition, not an
                    // override this plugin merely touches. A FormKey test needing no body, so it runs before the try.
                    if (definedIn && !scopedModKeys!.Contains(fk.ModKey)) continue;
                    // where_source=winner de-dups up front: the winner verdict is FormKey-intrinsic, so any scoped
                    // copy gives the same answer, and the first scoped copy in stream order supplies the display
                    // source. The scoped path instead de-dups AFTER the filters — a different rule, below.
                    if (whereWinnerActive && !seen.Add(fk)) continue;
                    // Per-record fault isolation: the body tests lazily parse subrecord content, so one record
                    // Mutagen cannot parse would otherwise abort the whole call as an opaque transport error. Such a
                    // record is excluded and accounted for in the response, never silently skipped and never guessed
                    // as a match. The winner re-fetch below is inside the try too, so an unparseable winner is
                    // accounted the same way.
                    try
                    {
                        // The body the FILTERS decide on: the live winner (where_source=winner) or the streamed body.
                        IMajorRecordGetter filterBody = body;
                        if (whereWinnerActive)
                        {
                            var w = view.ResolveWinner(fk);
                            if (w is null) continue;                              // the key came from the order, so a winner must exist; defensive
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
                        // Deleted records carry no body to scan (the rule lives in DeletedRecordRule, shared with the
                        // error check and the compact/merge scan): the content filters cannot match one, so it is
                        // excluded as a clean non-match before the scan touches its body — which on the references=
                        // arm is also what avoids crashing on an engine-authored deleted record's leftover body.
                        // editorid_contains= stays live, because EditorID reads from the record's early EDID
                        // subrecord, before the deep body parse that can throw. The check keys on whether the
                        // predicates actually READ body content: the header- and resolution-only terms must see
                        // deleted records exactly as editorid_contains= does.
                        if (DeletedRecordRule.HasNoLiveBody(filterBody)
                            && (refSet is not null || predicate is { NeedsLiveBody: true })) continue;
                        if (!string.IsNullOrEmpty(editoridContains)
                            && (filterBody.EditorID is null || filterBody.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                            continue;
                        // references= is a list with OR semantics: a record matches if it links to ANY target. One
                        // EnumerateFormLinks pass collects the intersection, so a multi-target lookup can be
                        // un-merged into which targets each row hit.
                        List<FormKey>? hitTargets = null;
                        if (refSet is not null)
                        {
                            if (filterBody is not IFormLinkContainerGetter flc) continue;
                            var hitSet = new HashSet<FormKey>();
                            foreach (var l in flc.EnumerateFormLinks()) if (refSet.Contains(l.FormKey)) hitSet.Add(l.FormKey);
                            if (hitSet.Count == 0) continue;
                            if (multiTarget && groups is null) hitTargets = references!.Where(hitSet.Contains).Distinct().ToList();   // in input order; only the match-line path consumes it
                        }
                        if (refNone is not null && ExcludedByReference(filterBody, refNone)) continue;
                        if (predicate is not null && !predicate.Matches(filterBody))    // value filter on the same in-hand body, no extra fetch
                        {
                            if (predicate.FatalError is not null) break;          // e.g. a numeric op against a non-numeric field — abort and surface it
                            continue;
                        }
                        // De-dup, since a key can recur across scoped plugins. On the scoped path this runs AFTER the
                        // filters, so the source recorded for a shared key is the first scoped plugin, in plugins=
                        // order, whose own body passed. Under where_source=winner the key was already de-duped up
                        // front, so this is a no-op there.
                        if (!whereWinnerActive && !seen.Add(fk)) continue;
                        total++;
                        if (groups is not null)                                   // group_by=: aggregate over all matches, no keys or prefill, no limit cap
                        {
                            var gk = groupBy == "type" ? RecordNaming.StripOverlay(filterBody.GetType().Name)
                                   : groupBy == "defined_in" ? fk.ModKey.FileName.ToString()
                                   : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";  // "winner"
                            groups[gk] = groups.GetValueOrDefault(gk) + 1;
                        }
                        else if (total > offset && keys.Count < limit)            // in-hand body → fill the summary for free; offset= skips the first N matches, and total already counts this one
                        {
                            keys.Add(fk);
                            sources.Add(source);                                  // the scoped plugin's display body; null means the winner. where_source=winner keeps the scoped source so "match on winner, show origin" works.
                            matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);   // parallel to keys, multi-target only
                            // The winner comes off the SAME view the scan runs on, so a rebuild landing mid-scan
                            // cannot make a row's winner reflect a newer build than the depth beside it. Type and
                            // editorid come from the body that MATCHED.
                            prefilled!.Add(new RecordSummary(fk, RecordNaming.StripOverlay(filterBody.GetType().Name), filterBody.EditorID,
                                                             view.ResolveWinner(fk)?.WinnerPlugin ?? "?", depth, null)
                                           .WithRuntime(view.RuntimeAddressOf(fk)));
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
            // Anything else escaping the stream still gets a named failure: the MCP layer's generic "An error
            // occurred invoking …" must never be the terminal diagnostic for a data failure.
            catch (Exception ex) { return CrossQueryOutcome.Fail($"scan aborted: {ex.GetType().Name}: {ex.Message}"); }
            finally { winnerSession?.Dispose(); }
            if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError); // typed predicate error — fail fast, named
        }
        else                                                                  // conflicts_only alone — index keys only, no body fetch
        {
            // Summaries here would each need a winner-body fetch; leaving them to the renderer, which stops at
            // max_chars, means a big limit with a small max_chars does not fetch bodies it will never show.
            // group_by= here can only be winner or defined_in — type was refused up front, with no body to name it.
            foreach (var fk in view.ConflictKeys())
            {
                if (setFilter is not null && !setFilter.Contains(fk)) continue;   // the identity intersection on the index-only branch
                total++;
                if (groups is not null)
                {
                    // group_by=winner here does an index-level ResolveWinner per conflict key — a resolve, not a body
                    // parse, and unavoidable for the aggregate, since the non-group path defers the winner to the
                    // renderer, which only fetches the capped rows. Deliberate: accuracy over speed.
                    var gk = groupBy == "defined_in" ? fk.ModKey.FileName.ToString() : view.ResolveWinner(fk)?.WinnerPlugin ?? "?";
                    groups[gk] = groups.GetValueOrDefault(gk) + 1;
                }
                else if (total > offset && keys.Count < limit) { keys.Add(fk); sources.Add(null); }   // no scoped plugin → display the winner; offset= skips the first N
            }
        }
        // Unscannable accounting: name the count, the first few offenders with the reason, and what a caller can
        // still do — these records are invisible to the body filters, which is not the same as "0 matches". Two
        // causes flow here: Mutagen could not parse a body, or under where_source=winner a winner body the index
        // named did not re-resolve on fetch, and the note must not mislabel the second as a parse failure. It says
        // "instance(s)" and "where the failure occurred" because under plugins= a FormKey is tested once per scoped
        // plugin, so a failing copy is skipped where it occurs while another plugin's copy can still match.
        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned and were skipped where the failure occurred "
              + "(Mutagen could not parse their content, or — under where_source=winner — a winner body the index named did not re-resolve on fetch; another plugin's copy of the same FormKey can still match): "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "")
              + $". Inspect one with {ToolNames.Records} formids=[the FormID] (per-field fault isolation applies).";
        // Whole-plugin coverage gap: the scan carried on past a plugin it could not open, so the answer covers the
        // rest of the order but not that plugin's winners. Named here so the result never reads as a clean scan.
        if (unreadablePlugins.Count > 0)
        {
            string gap = $"coverage gap: {unreadablePlugins.Count} plugin(s) could not be read, so any record they win is missing from this answer: "
                       + string.Join("; ", unreadablePlugins.Select(u => u.Message));
            scanNote = scanNote is null ? gap : scanNote + " " + gap;
        }
        // group_by= aggregation is not limit-capped, so Capped is a match-line concern only.
        var groupRows = groups?.Select(kv => new GroupCount(kv.Key, kv.Value))
                              .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        // Capped means matches exist BEYOND the returned window: the matches offset= skipped were asked to be
        // skipped, so they must not make a full window read as capped.
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote,
                                     matched, groupRows, groupBy, definedIn ? string.Join(", ", plugins!) : null, offset,
                                     whereWinner, whereSourceNote)
               { Epoch = view.Epoch, Pin = new ViewPin(resolver, view),
                 UnreadPlugins = unreadablePlugins.Select(u => u.PluginName).ToList() };
    }

    /// <summary>The schema's answer to a quantifier on a step that is not a list: a refusal sentence naming the
    /// step's real cardinality, or null. Only a NAMED type scope can be asked — the schema decides per record type,
    /// so an unscoped or mixed scan keeps the per-record accounting as its backstop — and the refusal lands only
    /// where the step is a non-list on EVERY named type, so a union arm that does hold a list still runs.</summary>
    string? QuantifierShapeRefusal(IReadOnlyList<string> typeTokens, FieldPredicateSet predicate)
    {
        var schemas = new List<TypeSchema>();
        foreach (var token in typeTokens)
            foreach (var ts in Rulebook.RecordTypesNamed(token))
                if (!schemas.Contains(ts)) schemas.Add(ts);
        if (schemas.Count == 0) return null;

        foreach (var step in predicate.QuantifiedSteps)
        {
            var whatItIs = new List<string>();
            foreach (var ts in schemas)
            {
                var card = Rulebook.StepCardinality(ts, step.Path, step.Index);
                if (card is null) return null;              // the schema cannot say — the runtime accounting answers
                if (card == "list") { whatItIs.Clear(); break; }
                whatItIs.Add($"a {card} on {ts.Name}");
            }
            if (whatItIs.Count == 0) continue;
            return $"predicate '{step.Text}': '{step.Path[step.Index]}{step.Token}' quantifies a step that is not a list — " +
                   $"it is {string.Join(", ", whatItIs.Take(3))}{(whatItIs.Count > 3 ? $", and {whatItIs.Count - 3} more" : "")}. " +
                   "Drop the quantifier, or point it at a list-valued field.";
        }
        return null;
    }

    /// <summary>The negated references= test: true when this body links to ANY excluded target, so the scan drops
    /// it. A record that carries no links at all references nothing and is kept — that is the whole point of the
    /// term, and a DELETED record (no live body to read links from) is the strongest case of it, so it is kept
    /// rather than skipped the way the positive term skips it.</summary>
    static bool ExcludedByReference(IMajorRecordGetter body, HashSet<FormKey> excluded)
    {
        if (DeletedRecordRule.HasNoLiveBody(body)) return false;
        if (body is not IFormLinkContainerGetter flc) return false;
        foreach (var l in flc.EnumerateFormLinks()) if (excluded.Contains(l.FormKey)) return true;
        return false;
    }

    // ---- the off-order scan ----------------------------------------------------------------------------

    /// <summary>The off-order scan: the file's own records are the universe, with the same filter grammar the
    /// in-order scan runs — multi-type, the full where= predicate set, references=, a plugins= scope keeping file
    /// records those active plugins also touch, defined_in for records the file itself defines, group_by, windows,
    /// and artifact-fed identity sets. The predicate's `winner` and `-&gt;` terms bind to the ACTIVE view's
    /// resolution: provenance is an active-order question even when the bodies come from the file. Returns the same
    /// outcome shape the in-order scan renders, with sources naming the file on every row; the caller declares the
    /// file's content outside the epoch fingerprint.</summary>
    public CrossQueryOutcome OffOrderQuery(PoleInfo pole, IReadOnlyList<string>? typeSet,
        IReadOnlyList<FormKey>? references, string? editoridContains, IReadOnlyList<string>? scopePlugins,
        bool definedIn, IReadOnlyList<string>? where, int limit, string? groupBy, int offset,
        IReadOnlyList<FormKey>? formidSet, IReadOnlyList<ArtifactDemand>? artifactDemands,
        LoadOrderResolver.IndexView? pinnedView = null,
        IReadOnlyList<FormKey>? referencesNone = null)
    {
        var resolver = Resolver;
        var view = pinnedView ?? resolver.Capture();   // the caller's door build when it captured one — see CrossQuery

        if (groupBy is not null)
        {
            groupBy = groupBy.Trim().ToLowerInvariant();
            if (groupBy is not ("winner" or "type" or "defined_in"))
                return CrossQueryOutcome.Fail($"group_by='{groupBy}' is not a known aggregation key — use 'winner', 'type', or 'defined_in'.");
        }
        if (offset < 0)
            return CrossQueryOutcome.Fail($"offset={offset} — offset must be >= 0.");
        if (offset > 0 && groupBy is not null)
            return CrossQueryOutcome.Fail("group_by= aggregates ALL matches into a count table, so offset= has nothing to page — drop one.");

        FieldPredicateSet? predicate = null;
        if (where is { Count: > 0 })
        {
            var (set, perr) = FieldPredicateSet.Parse(where, FormIdDoor.On(view).Parse);
            if (perr is not null) return CrossQueryOutcome.Fail(perr);
            predicate = set;
        }
        foreach (var demand in (artifactDemands ?? Array.Empty<ArtifactDemand>()).Concat(
                     predicate?.ArtifactDemands ?? (IReadOnlyList<ArtifactDemand>)Array.Empty<ArtifactDemand>()))
            if (demand.Epoch != view.Epoch)
                return CrossQueryOutcome.Fail(ArtifactEpochMismatch(demand, view.Epoch)) with { Epoch = view.Epoch };

        IReadOnlyList<Type>? types;
        try
        {
            if (typeSet is { Count: > 0 })
            {
                var union = new List<Type>();
                foreach (var ts in typeSet)
                    foreach (var t in ResolveTypeFilter(ts.Trim()))
                        if (!union.Contains(t)) union.Add(t);
                types = union;
            }
            else types = null;
        }
        catch (ArgumentException ex) { return CrossQueryOutcome.Fail(ex.Message); }

        if (predicate is not null && typeSet is { Count: > 0 } && QuantifierShapeRefusal(typeSet, predicate) is { } qerr)
            return CrossQueryOutcome.Fail(qerr) with { Epoch = view.Epoch };

        var scopeSet = scopePlugins is { Count: > 0 }
            ? new HashSet<string>(scopePlugins.Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase)
            : null;
        if (scopeSet is not null)
            foreach (var p in scopeSet)
                if (!view.ContainsPlugin(p))
                    return CrossQueryOutcome.Fail($"plugins= scope '{p}' is not in the active load order — over an out-of-load-order file the scope keeps the file's records that ACTIVE plugins also touch, so the scope names active plugins.") with { Epoch = view.Epoch };

        ModKey fileKey;
        try { fileKey = ModKey.FromFileName(pole.Plugin); }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"'{pole.Plugin}' is not a valid plugin filename: {ex.Message}"); }

        string dataDir;
        try { lock (_gate) { EnsurePathsDerived(); dataDir = _dataDir; } }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"the MO2 roots couldn't be derived to open '{pole.Plugin}': {ex.Message}") with { Epoch = view.Epoch }; }
        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(pole.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"could not open '{pole.Path}' as a Skyrim plugin: {ex.Message}") with { Epoch = view.Epoch }; }

        var refSet = references is { Count: > 0 } ? new HashSet<FormKey>(references) : null;
        var refNone = referencesNone is { Count: > 0 } ? new HashSet<FormKey>(referencesNone) : null;
        bool multiTarget = references is { Count: >= 2 };
        var setFilter = formidSet is { Count: > 0 } ? new HashSet<FormKey>(formidSet) : null;

        var keys = new List<FormKey>();
        var sources = new List<string?>();
        List<string?>? matched = multiTarget ? new() : null;
        var prefilled = new List<RecordSummary>();
        Dictionary<string, int>? groups = groupBy is not null ? new(StringComparer.OrdinalIgnoreCase) : null;
        int total = 0, unscannable = 0;
        var unscannableSamples = new List<string>();
        LoadOrderResolver.OverlaySession? session = null;
        try
        {
            if (predicate is not null)
            {
                // The provenance and link terms read the ACTIVE order's resolution: a `winner` term over an
                // off-order file asks who wins this key in the active order, and a `->` target resolves to its live
                // winner body. Same binding discipline as the in-order scan.
                session = (predicate.NeedsBodyResolution ? resolver.OpenSession() : null);
                var sess = session;
                predicate.BindResolution(
                    fk => view.ResolveWinner(fk)?.WinnerPlugin,
                    predicate.NeedsBodyResolution
                        ? fk =>
                        {
                            var w = view.ResolveWinner(fk);
                            return w is null ? null : view.GetRecord(sess!, w.Value.WinnerPlugin, fk);
                        }
                        : null);
            }
            var seen = new HashSet<FormKey>();
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                var fk = rec.FormKey;
                if (!seen.Add(fk)) continue;
                try
                {
                    if (setFilter is not null && !setFilter.Contains(fk)) continue;
                    if (definedIn && fk.ModKey != fileKey) continue;
                    if (types is not null && !types.Any(t => t.IsInstanceOfType(rec))) continue;
                    if (scopeSet is not null)
                    {
                        var touchers = view.TouchingPlugins(fk);
                        if (touchers is null || !touchers.Any(scopeSet.Contains)) continue;
                    }
                    if (DeletedRecordRule.HasNoLiveBody(rec)
                        && (refSet is not null || predicate is { NeedsLiveBody: true })) continue;
                    if (!string.IsNullOrEmpty(editoridContains)
                        && (rec.EditorID is null || rec.EditorID.IndexOf(editoridContains, StringComparison.OrdinalIgnoreCase) < 0))
                        continue;
                    List<FormKey>? hitTargets = null;
                    if (refSet is not null)
                    {
                        if (rec is not IFormLinkContainerGetter flc) continue;
                        var hitSet = new HashSet<FormKey>();
                        foreach (var l in flc.EnumerateFormLinks()) if (refSet.Contains(l.FormKey)) hitSet.Add(l.FormKey);
                        if (hitSet.Count == 0) continue;
                        if (multiTarget && groups is null) hitTargets = references!.Where(hitSet.Contains).Distinct().ToList();
                    }
                    if (refNone is not null && ExcludedByReference(rec, refNone)) continue;
                    if (predicate is not null && !predicate.Matches(rec))
                    {
                        if (predicate.FatalError is not null) break;
                        continue;
                    }
                    total++;
                    if (groups is not null)
                    {
                        var gk = groupBy == "type" ? RecordNaming.StripOverlay(rec.GetType().Name)
                               : groupBy == "defined_in" ? fk.ModKey.FileName.ToString()
                               : view.ResolveWinner(fk)?.WinnerPlugin ?? "(not in the active order)";
                        groups[gk] = groups.GetValueOrDefault(gk) + 1;
                    }
                    else if (total > offset && keys.Count < limit)
                    {
                        var w = view.ResolveWinner(fk);
                        keys.Add(fk);
                        sources.Add(pole.Plugin);
                        matched?.Add(hitTargets is not null ? string.Join(", ", hitTargets) : null);
                        prefilled.Add(new RecordSummary(fk, RecordNaming.StripOverlay(rec.GetType().Name), rec.EditorID,
                                                        w?.WinnerPlugin ?? "(not in the active order)", w?.OverrideDepth ?? 0, null)
                                      .WithRuntime(view.RuntimeAddressOf(fk)));
                    }
                }
                catch (Exception ex)
                {
                    unscannable++;
                    if (unscannableSamples.Count < 3)
                        unscannableSamples.Add($"{fk} in {pole.Plugin} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) { return CrossQueryOutcome.Fail($"file '{pole.Plugin}' could not be fully read — {ex.GetType().Name}: {ex.Message}") with { Epoch = view.Epoch }; }
        finally { session?.Dispose(); (ov as IDisposable)?.Dispose(); }
        if (predicate?.FatalError is not null) return CrossQueryOutcome.Fail(predicate.FatalError) with { Epoch = view.Epoch };

        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record(s) in '{pole.Plugin}' could not be scanned and were skipped where the failure occurred: "
              + string.Join("; ", unscannableSamples)
              + (unscannable > unscannableSamples.Count ? $"; and {unscannable - unscannableSamples.Count} more" : "") + ".";
        var groupRows = groups?.Select(kv => new GroupCount(kv.Key, kv.Value))
                              .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        return new CrossQueryOutcome(keys, prefilled, total, groups is null && total > offset + keys.Count, null,
                                     predicate?.AccountingNote(), sources, scanNote, matched, groupRows, groupBy,
                                     definedIn ? pole.Plugin : null, offset, false, null)
               { Epoch = view.Epoch, Pin = new ViewPin(resolver, view) };
    }

    // ---- effect-chain resolver -------------------------------------------------------------------------

    /// <summary>Resolve which SPEL/ENCH/ALCH/SCRL/INGR apply a MagicEffect, each with the magnitude, area and
    /// duration from the matching effect entry. Thin wiring over the core: resolve the optional type-narrow — each
    /// must be one of the five effect-bearing records, and a non-member is refused loudly rather than yielding a
    /// silent empty scan — then drive <see cref="EffectChain.Resolve"/>. All the logic lives in the core so a test
    /// can drive this same path on synthetic plugins.</summary>
    public EffectChainResult ResolveEffectChain(FormKey mgef, IReadOnlyList<string>? typesNarrow, int limit)
    {
        IReadOnlyList<Type> scope;
        if (typesNarrow is { Count: > 0 })
        {
            var picked = new List<Type>();
            foreach (var ts in typesNarrow)
            {
                IReadOnlyList<Type> resolved;
                try { resolved = ResolveTypeFilter(ts.Trim()); }              // unknown type → named error, as on the scan
                catch (ArgumentException ex) { return EffectChainResult.Fail(ex.Message); }
                foreach (var t in resolved)
                {
                    if (!EffectChain.CarrierTypes.Contains(t))
                        return EffectChainResult.Fail(
                            $"type '{ts}' is not effect-bearing — the chain form scans only Spell/ObjectEffect/Ingestible/Scroll/Ingredient " +
                            "(SPEL/ENCH/ALCH/SCRL/INGR), the records that carry an Effects list. Drop it or pass one of those.");
                    if (!picked.Contains(t)) picked.Add(t);
                }
            }
            scope = picked;
        }
        else scope = EffectChain.CarrierTypes;

        return EffectChain.Resolve(Resolver, mgef, scope, limit);
    }

    // ---- integrity sweep -------------------------------------------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for record integrity errors:
    /// dangling FormLinks, missing masters and parse failures. Thin wiring over the core
    /// <see cref="ErrorCheck.Run"/>, which holds all the scan logic so a test can drive this same path over
    /// synthetic plugins. Read-only.
    /// <para>A scope name NOT in the active order is resolved on disk by the shared plugin-locate contract —
    /// enabled, disabled and unlisted mod folders — and swept off-order against its own overlay, with links resolved
    /// against the active order plus the file's own records. That is the pre-enable verify lane: the dangling-ref
    /// sweep of a patch houseCARL just wrote, before an MO2 refresh puts it in plugins.txt. A name found nowhere, or
    /// in several folders, still fails loudly.</para>
    /// <para>The record-scope, class-filter and counts-only knobs are parsed here — a bad FormID, an unknown record
    /// type or an unrecognized finding class refuses the call before any sweep runs — and handed to the core as
    /// typed values.</para></summary>
    public ErrorCheckResult CheckErrors(IReadOnlyList<string>? plugins, int limit,
                                        IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                        string? type = null, IReadOnlyList<string>? findings = null,
                                        bool countsOnly = false, IReadOnlyList<string>? exclude = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, type);
        if (scopeErr is not null) return ErrorCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseErrorClasses(findings, out var classes, out var classErr))
            return ErrorCheckResult.Fail(classErr!);

        // One resolver and one view for the whole call: the scope check, the refusal stamps and the sweep below all
        // name the same build. Passing the property down would let the core re-gate and capture an adjacent build,
        // so a refusal could stamp one build while the sweep stamped the next.
        var resolver = Resolver;
        var viewAll = resolver.Capture();

        // The exclude= axis. The `implicit` group is a fact about the MO2 composition — the plugins the order loads
        // that plugins.txt does not list — so it is read here, where that composition lives, and the core sweep
        // receives plain filenames. Resolved before anything is swept, so a bad value refuses having done no work.
        // The check is gated on the caller having written the `implicit` token, not merely on an exclusion being
        // passed: otherwise a named-plugin exclusion over an unreadable profile is refused with a message about a
        // group the caller never named.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames() : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ErrorCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ErrorCheckResult.Fail(excludeErr);

        if (plugins is { Count: > 0 })
        {
            var view = viewAll;
            var active = new List<string>();
            var offOrder = new List<(string Name, string Path)>();
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            Mo2Composition? comp = null;
            foreach (var name in plugins)
            {
                var n = name?.Trim() ?? "";
                if (n.Length == 0) return ErrorCheckResult.Fail(SweepSharedInput.BlankPluginName);
                if (view.ContainsPlugin(n)) { active.Add(n); continue; }
                comp ??= Mo2LoadOrder.ReadComposition(profileDir);
                var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, n, null);
                // Membership and locate refusals are decided against THIS captured build and its composition, so
                // they are stamped. The parse refusals above consulted no build and stay unstamped.
                if (loc.Error is not null)
                    return ErrorCheckResult.Fail($"plugin not in the load order: {n} — and no on-disk copy was found either ({loc.Error})")
                           with { Epoch = view.Epoch };
                if (loc.Ambiguous is not null)
                    return ErrorCheckResult.Fail(
                        $"plugin '{n}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                        $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to sweep. " +
                        "Enable the one you mean in MO2, or remove the duplicates.")
                           with { Epoch = view.Epoch };
                offOrder.Add((n, loc.Path!));
            }
            return ClassifyMissingMasters(
                ErrorCheck.Run(resolver, viewAll, active, limit, offOrder.Count > 0 ? offOrder : null,
                               recordScope, classes, countsOnly, excluded));
        }
        return ClassifyMissingMasters(
            ErrorCheck.Run(resolver, viewAll, plugins, limit, null, recordScope, classes, countsOnly, excluded));
    }

    /// <summary>Fill in each report's install-vs-enable split for the masters the sweep found unsatisfied. The core
    /// sweep knows the ACTIVE ORDER and stops there; which of those masters is nonetheless sitting in the install —
    /// in a disabled mod, or unticked — is a fact about the MO2 composition, which lives at this layer. Done here so
    /// the split has one home (<see cref="Mo2LoadOrder.SplitUnsatisfiedMasters"/>) rather than a second spelling
    /// inside the core.
    /// <para>A composition that cannot be read leaves every report's subset null, not empty. Empty would say "none
    /// of these is merely disabled", a claim about an install nobody looked at; null says the split was not made,
    /// and the render falls back to the union remedy.</para></summary>
    ErrorCheckResult ClassifyMissingMasters(ErrorCheckResult r)
    {
        if (r.Error is not null || r.Reports.Count == 0) return r;
        if (!r.Reports.Any(p => p.MissingMasters.Count > 0)) return r;

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch { return r; }
        Mo2Composition comp;
        IReadOnlyCollection<string> installed;
        try
        {
            comp = Mo2LoadOrder.ReadComposition(profileDir);
            // The install's plugin-name set, read ONCE for the whole sweep rather than per report per name. The
            // expensive path is the common one: a master that is not installed short-circuits nowhere, so it walks
            // the enabled mods, the disabled mods, the unlisted folders and Data before returning false. The answer
            // does not depend on which report asked, so neither does the read.
            installed = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
        }
        catch { return r; }

        var classified = new List<PluginErrors>(r.Reports.Count);
        foreach (var p in r.Reports)
            classified.Add(p.MissingMasters.Count == 0
                ? p with { InstalledButInactiveMasters = Array.Empty<string>() }
                : p with { InstalledButInactiveMasters =
                               Mo2LoadOrder.SplitUnsatisfiedMasters(installed, p.MissingMasters).InstalledButInactive });
        return r with { Reports = classified };
    }

    /// <summary>The force-loaded plugin names — in the order, absent from plugins.txt — for
    /// <see cref="SweepExclusion.ImplicitToken"/>, or the reason they could not be read. A read that did not happen
    /// is not a set that is empty: swallowing the failure would expand the group to nothing, exclude nothing, and
    /// leave the response silent about it, while the parameter promises that a value matching nothing is
    /// refused.</summary>
    (IReadOnlyList<string> Names, string? Error) ImplicitPluginNames()
    {
        string profileDir;
        lock (_gate) { EnsurePathsDerived(); profileDir = _profileDir; }
        try { return (Mo2LoadOrder.ReadComposition(profileDir).ImplicitPluginNames, null); }
        catch (Exception ex)
        {
            return (Array.Empty<string>(),
                $"exclude= could not be resolved: the MO2 profile's plugin list at '{profileDir}' could not be read " +
                $"({ex.GetType().Name}: {ex.Message}). The '{SweepExclusion.ImplicitToken}' group is defined by which " +
                "plugins that file does NOT list, so it cannot be widened without it. Nothing was swept.");
        }
    }

    /// <summary>The record-scope parse's REFUSAL alone, for the merged surface's shared-input check
    /// (<see cref="SweepSharedInput"/>). The scope itself belongs to whichever family is about to sweep with it;
    /// what is shared is the judgement that a value is malformed, and that judgement has to be reachable without
    /// selecting a family that uses it.</summary>
    internal string? SweepScopeError(IReadOnlyList<string>? formids, string? editoridContains, string? type)
        => BuildSweepScope(formids, editoridContains, type).Error;

    /// <summary>Parse the sweep families' shared record-scope params into a <see cref="SweepScope"/>: FormID tokens,
    /// an EditorID substring, and a record type resolved through the same type lookup the scan uses. Every malformed
    /// input is a named refusal returned before the sweep starts, never a scope that silently matched nothing.
    /// Returns (null, null) when nothing was narrowed, so the unscoped path stays untouched.</summary>
    (SweepScope? Scope, string? Error) BuildSweepScope(IReadOnlyList<string>? formids, string? editoridContains, string? type)
    {
        HashSet<FormKey>? keys = null;
        if (formids is { Count: > 0 })
        {
            keys = new HashSet<FormKey>();
            var door = OpenFormIdDoor();
            foreach (var raw in formids)
            {
                var t = raw?.Trim() ?? "";
                if (t.Length == 0) return (null, "a blank entry in formids= — pass FormID tokens (e.g. '0BCC84:Skyrim.esm').");
                try { keys.Add(door.Parse(t)); }
                catch (Exception ex) { return (null, $"bad FormID '{raw}' in formids=: {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0BCC84:Skyrim.esm'."); }
            }
        }

        IReadOnlyList<Type>? types = null;
        var typeLabel = type?.Trim();
        if (!string.IsNullOrEmpty(typeLabel))
        {
            try { types = ResolveTypeFilter(typeLabel); }
            catch (ArgumentException ex) { return (null, ex.Message); }
        }

        var scope = new SweepScope(keys, editoridContains, types, typeLabel);
        return (scope.IsEmpty ? null : scope, null);
    }

    // ---- script-property sweep (housecarl_validate_scripts) --------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for VMAD script properties
    /// declared in the attached script's .pex (or an ancestor it extends) but left unbound on the record — a silent
    /// <c>None</c>. Thin wiring over the core <see cref="ScriptPropertyCheck.Run"/>, which holds all the cross-check
    /// logic so a test can drive this same path over synthetic records and a planted .pex. Passes the live
    /// <see cref="Assets"/> resolver so a script's .pex is found loose or BSA-packed. Read-only.
    /// <para>The record-scope, property-name, class-filter and counts-only knobs are parsed here, so a bad FormID,
    /// unknown record type or unrecognized finding class refuses the call before any sweep runs.</para></summary>
    public ScriptCheckResult ValidateScripts(IReadOnlyList<string>? plugins, int limit,
                                             IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                             string? type = null, string? propertyContains = null,
                                             IReadOnlyList<string>? findings = null, bool countsOnly = false,
                                             IReadOnlyList<string>? exclude = null, bool noneInScope = false)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, type);
        if (scopeErr is not null) return ScriptCheckResult.Fail(scopeErr);
        if (!SweepFindings.TryParseScriptClasses(findings, out var classes, out var classErr))
            return ScriptCheckResult.Fail(classErr!);
        // One resolver and view threaded through, same contract as CheckErrors.
        var resolver = Resolver;
        // The exclusion resolves here, where the MO2 composition lives, exactly as it does for CheckErrors: the core
        // sweep receives plain filenames, and a bad value refuses having done no work.
        bool wantsImplicit = exclude?.Any(v => (v ?? "").Trim().Equals(SweepExclusion.ImplicitToken, StringComparison.OrdinalIgnoreCase)) == true;
        var (implicitNames, implicitErr) = wantsImplicit ? ImplicitPluginNames() : (Array.Empty<string>(), null);
        if (implicitErr is not null) return ScriptCheckResult.Fail(implicitErr);
        var (excluded, excludeErr) = SweepExclusion.Resolve(exclude, implicitNames);
        if (excludeErr is not null) return ScriptCheckResult.Fail(excludeErr);
        return ScriptPropertyCheck.Run(resolver, resolver.Capture(), Assets, plugins, limit, recordScope,
                                       propertyContains, classes, countsOnly, excluded, noneInScope);
    }

    /// <summary>The active order's plugin filenames. A thin accessor for the ONE caller that has to decide which of
    /// several findings families can sweep a plugin the caller named — the errors family resolves an out-of-order
    /// file on disk and sweeps it, the scripts family has no such lane. The logic that USES this lives in the merged
    /// sweep's own file, not here.</summary>
    internal IReadOnlyList<string> ActivePluginNames => Resolver.PluginNames;

    // ---- writes ----------------------------------------------------------------------------------------

    /// <summary>Apply one or more edits as a single patch. Parses each op's FormID, field path and optional
    /// composition spec into the core's <see cref="WritePatchBuilder.PatchEdit"/>, resolves the output path as a new
    /// MO2 mod folder under ModsDir, then drives <see cref="WritePatchBuilder.Apply"/>: resolve winner, derive type,
    /// pre-flight all, override, apply the verb, serialize with the right masters. All-or-nothing — a single
    /// malformed op or pre-flight rejection refuses the whole call with no file written. Writes go to a new patch by
    /// default; <paramref name="into"/> extends an existing houseCARL-owned patch. Success is a null-Error outcome.
    /// <paramref name="fullReadback"/> additionally reads every touched record back in full off the written file,
    /// which is the pre-enable verify loop.</summary>
    public WritePatchBuilder.PatchOutcome ApplyEdits(IReadOnlyList<BulkOp> ops, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false, IReadOnlyList<string?>? fromRecords = null, IReadOnlyList<string?>? opOrigins = null)
    {
        if (ops.Count == 0)
            return WritePatchBuilder.PatchOutcome.Fail("no operations supplied.");

        // In-place is the explicit, named-file opt-in: edit an existing plugin, including one houseCARL did not
        // author, instead of writing a new patch. The contract is validated up front — it requires target=, and it
        // is mutually exclusive with into=, which extends a houseCARL patch. target= without in_place is a no-op the
        // caller likely did not mean, so it is named rather than silently ignored.
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
        // Runs outside the write gate so a malformed call never queues behind a real write. A write door, so a
        // runtime FormID is refused with the plugin form to use instead.
        var edits = new List<WritePatchBuilder.PatchEdit>(ops.Count);
        var problems = new List<string>();
        var editDoor = OpenWriteFormIdDoor();
        for (int i = 0; i < ops.Count; i++)
        {
            // fromRecords[i] is the zip's per-op source record, carried parallel to the op list because the
            // published wire shape deliberately gains no new member.
            var edit = MapEdit(editDoor, ops[i], i, out var err,
                fromRecords is not null && i < fromRecords.Count ? fromRecords[i] : null,
                opOrigins is not null && i < opOrigins.Count ? opOrigins[i] : null);
            if (err is not null) problems.Add(err); else edits.Add(edit!);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"refused — {problems.Count} of {ops.Count} operation(s) malformed; NO patch written:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index
            var rulebook = Rulebook;

            if (inPlace)
            {
                // The in-place lane resolves off-order CopyFrom sources exactly as the patch lane does. The overlays
                // must stay OPEN across the whole in-place write — CopyField deep-copies through them and the
                // re-serialize follows — so they are disposed only after ApplyEditsInPlace returns.
                Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? ipSources = null;
                List<IDisposable>? ipOverlays = null;
                var ipError = PrepareCopyFromSources(resolver, edits, ref ipSources, ref ipOverlays, out var ipEpoch);
                if (ipError is not null)
                {
                    if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose();
                    return WritePatchBuilder.PatchOutcome.Fail(ipError) with { Epoch = ipEpoch };
                }
                try { return ApplyEditsInPlace(resolver, rulebook, edits, target!.Trim(), acknowledge, dryRun, ipSources); }
                finally { if (ipOverlays is not null) foreach (var d in ipOverlays) d.Dispose(); }
            }

            // A dry run resolves the would-be output path WITHOUT creating the mod folder — the one disk side effect
            // the pre-serialize pipeline otherwise has. The fresh-lane name is only a preview: the real write
            // re-picks a free stem, so a concurrent write can shift the auto-suffix.
            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

            // Pre-resolve any CopyFrom source that is off-order — on disk but not in the active order, the "copy
            // from the disabled old patch" case. Active-order sources are resolved inside Apply via its own captured
            // view, sharing the winner's build; only off-order files need the on-disk locate here, and their
            // overlays must stay open through the serialize because CopyField deep-copies through them.
            Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null;
            List<IDisposable>? offOrderOverlays = null;
            var cfError = PrepareCopyFromSources(resolver, edits, ref copyFromSources, ref offOrderOverlays, out var cfEpoch);
            if (cfError is not null)
            {
                if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose();
                if (created) RemoveFolderCreatedThisCall(outPath);   // a refused write leaves no orphan folder
                return WritePatchBuilder.PatchOutcome.Fail(cfError) with { Epoch = cfEpoch };
            }
            try
            {
                var outcome = WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend, fullReadback, copyFromSources, dryRun);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused write leaves no orphan folder
                return outcome;
            }
            finally { if (offOrderOverlays is not null) foreach (var d in offOrderOverlays) d.Dispose(); }
        }
    }

    /// <summary>P8b — locate every OFF-ORDER CopyFrom source (from_plugin present on disk but NOT in the active order)
    /// and fetch its version of the target record, holding each overlay OPEN (returned in <paramref name="overlays"/> for
    /// the caller to dispose AFTER the patch serialize — CopyField deep-copies through them). Active-order sources are
    /// left for <see cref="WritePatchBuilder.Apply"/> to resolve via its shared view (so they read the winner's build).
    /// Returns a named refusal string if any off-order source cannot be located, opened or read, or does not define the record
    /// (all-or-nothing, before any write); null on success. Uses the SAME on-disk locate as the records source= pole
    /// and the copy-npc-appearance donor lane, so the tools can never disagree on which file a filename names.
    /// <para>This capture is its own — the engine captures again — so a body pre-fetched here is only used when the
    /// engine's build still agrees the source is off-order. A write pins one resolver instance whose name table is
    /// never rebuilt, so the two captures cannot disagree about membership.</para>
    /// <para>MUTATES <paramref name="edits"/>, after the no-CopyFrom early return and this helper's own capture and
    /// before anything reads an edit: a CopyFrom source addressed by a PATH that names the very file the order loads
    /// is re-spelled to that plugin's NAME (<see cref="RespellActiveCopySourcePaths"/>). Stated because a resolve
    /// helper rewriting its argument is a surprise; this list is the one both the pre-locate and the engine consume,
    /// which is what makes one rewrite reach both.</para></summary>
    string? PrepareCopyFromSources(LoadOrderResolver resolver, IList<WritePatchBuilder.PatchEdit> edits,
        ref Dictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? sources, ref List<IDisposable>? overlays,
        out string? epoch)
    {
        // This helper takes its OWN capture, so its refusals are decided after a build was consulted and are stamped
        // like every other post-capture outcome. Null only when no CopyFrom op exists, when nothing consults a build.
        epoch = null;
        if (!edits.Any(e => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))) return null;   // no CopyFrom → no source work
        var view = resolver.Capture();
        epoch = view.Epoch;
        RespellActiveCopySourcePaths(view, edits);   // before the predicate, and before any edit is used as a key
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";
        Mo2Composition? comp = null;
        var problems = new List<string>();
        foreach (var e in edits)
        {
            // The shared predicate, not a restatement of it: the engine consumes what this fetches through the same
            // rule, so a clause added to one can never fail to reach the other.
            if (!WritePatchBuilder.IsOffOrderCopySource(e, view)) continue;   // not a CopyFrom, or active — Apply resolves it off the shared build
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
            try { body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == e.CopySource); }
            catch (Exception ex) { (ov as IDisposable)?.Dispose(); problems.Add($"{e.Target}: CopyFrom source file '{e.FromPlugin}' could not be read ({ex.Message})."); continue; }
            if (body is null)
            {
                (ov as IDisposable)?.Dispose();
                // Name WHICH record the file is missing: the target's own version for a same-record copy, or the
                // zip's source record for a cross-record one — "this record" would point at the wrong one.
                problems.Add(e.FromTarget is null
                    ? $"{e.Target}: CopyFrom source file '{e.FromPlugin}' does not define or override this record — there is no version of it there to copy."
                    : $"{e.Target}: CopyFrom source file '{e.FromPlugin}' does not define or override the SOURCE record {e.CopySource} — there is no version of it there to copy from.");
                continue;
            }
            (overlays ??= new()).Add((IDisposable)ov);
            (sources ??= new())[e] = body;   // distinct Path-array refs make each PatchEdit a distinct key under value equality; the indexer is collision-safe regardless
        }
        return problems.Count > 0
            ? $"refused — {problems.Count} CopyFrom source problem(s); NO patch written:\n  - " + string.Join("\n  - ", problems)
            : null;
    }

    /// <summary>Re-spell every <c>CopyFrom</c> source that is a PATH to the ACTIVE copy of a plugin into that
    /// plugin's NAME, in place, so the rest of the write speaks the load order's vocabulary.
    /// <para>Off-order-ness is decided by a lookup in the plugin-NAME table, and a full path is never a key there, so
    /// a path to a plugin the order is actively serving would answer "off-order": the body would be read off the
    /// file directly, bypassing the build the rest of the call resolves against. Usually that is only a wrong label,
    /// but under a profile switch, where a filename is served by a different mod folder, it is a wrong body.</para>
    /// <para>A path to an EXCLUDED-but-active plugin deliberately still reads the file directly rather than taking
    /// the exclusion refusal: <see cref="ActiveNameForPath"/> declines excluded plugins, which is the read surface's
    /// escape hatch, and the forward lane behaves the same way.</para>
    /// <para>Applied BEFORE the pre-locate loop for two reasons: a PatchEdit is the key of the pre-fetched source
    /// dictionary, so re-spelling one afterwards would leave a key the engine can never look up; and the same list
    /// goes to the engine, so one rewrite reaches the arm decision, the winner comparison and every rendered
    /// sentence at once.</para></summary>
    static void RespellActiveCopySourcePaths(LoadOrderResolver.IndexView view, IList<WritePatchBuilder.PatchEdit> edits)
    {
        for (int i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)) continue;
            // The same LooksLikePath check the other pole-resolving sites use. Harmless without it — a bare filename
            // that is active never reaches the off-order arm anyway — but kept so the convention has no exception.
            if (string.IsNullOrWhiteSpace(e.FromPlugin) || !LooksLikePath(e.FromPlugin!)) continue;
            if (ActiveNameForPath(view, e.FromPlugin!) is { } activeName)
                edits[i] = e with { FromPlugin = activeName };
        }
    }

    /// <summary>Locate the one <c>source=</c> plugin a forward call shares when the active order does not contain it
    /// — a disabled mod, an unticked plugin, an unregistered folder, or a direct path — open it, and pre-fetch every
    /// requested record's body off its own overlay. The forward twin of <see cref="PrepareCopyFromSources"/>, and
    /// simpler: every forward in a call names the same source, so this locates once, opens once and fetches N. Uses
    /// the same on-disk locate as every other lane, so two tools cannot disagree about which file a filename names.
    /// <para>Returns null with a null <paramref name="error"/> when the source IS in the active order — the ordinary
    /// path, which pays no locate and no overlay. Returns null with <paramref name="error"/> set when the file cannot
    /// be located, opened or read, when its name is ambiguous across mod folders, or when it does not define a
    /// requested record: refused by name, all-or-nothing, before any write.</para>
    /// <para><paramref name="overlay"/> is handed back OPEN, because the bodies are deep-copied during the write, so
    /// the caller disposes it only after the serialize returns. <paramref name="epoch"/> is this helper's own
    /// capture, since it decides membership against a build; the reported outcome's stamp still names the build the
    /// write was decided from.</para>
    /// <para><paramref name="sourceName"/> is the spelling the ENGINE should resolve against: <paramref
    /// name="fromPlugin"/> unchanged, except when a caller's PATH names the very file the order loads — then it is
    /// that plugin's name, and this returns null so the in-order arm handles it. Membership cannot be decided by
    /// ContainsPlugin alone once a path is an advertised spelling: a full path never matches the name table, so the
    /// live copy of an active plugin would take the off-order arm, be described as not in the load order, have its
    /// epoch disclaimed, and lose the already-the-winner flag, reporting that it out-ranks itself.
    /// <see cref="ActiveNameForPath"/> is a full-path identity compare, so a same-named backup keeps the off-order
    /// lane, and so does a path to an excluded plugin.</para></summary>
    WritePatchBuilder.OffOrderForwardSource? ResolveOffOrderForwardSource(
        LoadOrderResolver resolver, string fromPlugin, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs,
        out IDisposable? overlay, out string? epoch, out string? error, out string sourceName)
    {
        overlay = null; error = null; sourceName = fromPlugin;
        var view = resolver.Capture();
        epoch = view.Epoch;
        if (view.ContainsPlugin(fromPlugin)) return null;      // active — the engine resolves it off the shared build
        // The same LooksLikePath check the other pole-resolving sites use. Harmless without it — a bare filename
        // already failed ContainsPlugin above — but kept so the convention has no exception.
        if (LooksLikePath(fromPlugin) && ActiveNameForPath(view, fromPlugin) is { } activeName)
        {
            sourceName = activeName;                           // a path to the ACTIVE copy — in-order after all
            return null;
        }

        string modsDir, dataDir, overwriteDir, profileDir;
        try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
        catch (Exception ex) { error = $"source plugin '{fromPlugin}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}"; return null; }

        var comp = Mo2LoadOrder.ReadComposition(profileDir);
        // offerModParam is false because this tool has no mod= parameter, and a refusal must never point at a
        // parameter the caller's tool does not expose. A direct path is this lane's disambiguator.
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, fromPlugin, null, offerModParam: false);
        if (loc.Error is not null)
        {
            // A did-you-mean, because this is the one lane where a source name is typed by hand and the locate has
            // just proven the file is in no layer at all — so a spelling suggestion is the whole remedy, and there
            // is nothing for the absence explainer to explain. It is empty when nothing is close, so a genuinely
            // unknown name is never answered with an invented guess.
            // The pool is every plugin the locate SEARCHED, drawn from the same folder sequence, rather than the
            // active order's names: this lane makes disabled plugins first-class sources, so a typo of one must
            // still get a suggestion, and "not found" and "did you mean" cannot disagree about which places count.
            // It costs a listing per mod folder, spent only on this refusal.
            var pool = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
            error = $"source plugin '{fromPlugin}' is not in the load order and {loc.Error}" +
                    PluginNameSuggest.DidYouMean(fromPlugin, pool);
            return null;
        }
        if (loc.Ambiguous is not null)
        {
            error = $"source plugin '{fromPlugin}' is not in the load order and {loc.Ambiguous.Count} mod folders provide a file " +
                    $"with that name ({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which " +
                    "version to forward. Pass the full path to the copy you mean as the source.";
            return null;
        }

        // Is the located file the order's own copy of a plugin this session excluded because Mutagen could not fully
        // parse it at index time? By NAME such a source is refused in the engine; by PATH it reaches here, because
        // ActiveNameForPath declines excluded plugins. The asymmetry is deliberate — forwarding copies one body out,
        // not the whole-file re-serialize the exclusion refusal exists to prevent — but it must be DISCLOSED rather
        // than silent. Judged by file identity, never by name: a same-named copy elsewhere is a different file.
        string? excludedWhy = null;
        var locName = Path.GetFileName(loc.Path!);
        if (view.ExcludedPlugins.TryGetValue(locName, out var exWhy)
            && view.PluginPath(locName) is { } servedPath && SamePluginFile(servedPath, loc.Path!))
            excludedWhy = exWhy;

        ISkyrimModGetter ov;
        try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
        catch (Exception ex) { error = $"source file '{fromPlugin}' ({loc.Path}) could not be opened as a Skyrim plugin ({ex.Message})."; return null; }

        // One walk of the overlay collecting every wanted key: the overlay is ours alone and the whole call shares
        // it, so there is no reason to re-enumerate per record.
        var wanted = specs.Select(s => s.Target).ToHashSet();
        var bodies = new Dictionary<FormKey, IMajorRecordGetter>();
        // In the SAME walk, the local IDs this file originates under its own ModKey. A plugin's records are keyed by
        // its FILENAME, so a parked copy renamed 'MyPatch_old.esp' declares a different ModKey and its records match
        // nothing the caller asked for — which the miss below would otherwise report as "does not define or override
        // this record", a true sentence with a misleading cause.
        var selfIds = new HashSet<uint>();
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                if (wanted.Contains(rec.FormKey)) bodies[rec.FormKey] = rec;
                if (rec.FormKey.ModKey == ov.ModKey) selfIds.Add(rec.FormKey.ID);
            }
        }
        catch (Exception ex)
        {
            (ov as IDisposable)?.Dispose();
            error = $"source file '{fromPlugin}' ({loc.Path}) could not be read ({ex.Message}).";
            return null;
        }

        var missing = specs.Select(s => s.Target).Where(k => !bodies.ContainsKey(k)).Distinct().ToList();
        if (missing.Count > 0)
        {
            // The renamed-copy diagnosis, stated only when it is a fact about this file: the ID is present under the
            // file's own ModKey. Never a guess — the ordinary miss says nothing about renaming, and a FormKey whose
            // origin is some other master is not this case either.
            var renamed = missing.Where(k => k.ModKey != ov.ModKey && selfIds.Contains(k.ID)).ToList();
            var hint = renamed.Count == 0 ? "" :
                $"\n  NOTE: this file DOES carry {(renamed.Count == 1 ? "that FormID" : "those FormIDs")} — but under its own " +
                $"name, as {string.Join(", ", renamed.Take(3).Select(k => $"{k.ID:X6}:{ov.ModKey.FileName}"))}. A plugin's records are keyed by its " +
                "FILENAME, so a copy saved under a different name is a DIFFERENT plugin. Keep the original filename and " +
                "park the copy in another folder, or name the FormIDs as this file spells them.";
            (ov as IDisposable)?.Dispose();
            error = $"refused — source file '{fromPlugin}' ({loc.Where}) does NOT define or override {missing.Count} of the " +
                    $"{specs.Count} record(s) named; there is no version of them there to forward, and NOTHING was written:\n  - " +
                    string.Join("\n  - ", missing.Select(k => k.ToString())) + hint;
            return null;
        }

        overlay = ov as IDisposable;
        return new WritePatchBuilder.OffOrderForwardSource
        {
            Plugin = fromPlugin, Path = loc.Path!, Where = loc.Where, Bodies = bodies, Overlay = ov,
            ExcludedReason = excludedWhy,
        };
    }

    /// <summary>Build a walk's ordered source universe from the caller's pole list. Each element is one pole; the
    /// chain resolves a key by trying them in order, first hit wins (<see cref="SourceChain"/> carries the
    /// fallback-never-merge boundary and the fault-versus-miss rule).
    /// <para>There is deliberately no separate single-pole path: a length-1 list is this same loop running once, so
    /// an off-order element cannot behave one way alone and another way in a chain.</para>
    /// <para>The element kinds are the ordinary poles: <c>winner</c> is the active order as one universe, and a
    /// plugin NAME is that plugin's version wherever it lives, active or an off-order file, resolved through the same
    /// <see cref="LocatePluginFileOnDisk"/> contract every other lane uses. <c>previous_provider</c> is
    /// subject-relative and a walk has no per-key subject, so it refuses loudly rather than inventing a
    /// winner-relative reading.</para>
    /// <para>Overlays opened for off-order elements are appended to <paramref name="overlays"/> OPEN: the walk holds
    /// bodies off them for its whole run, so the caller disposes them only after the write completes. A refusal
    /// disposes what it opened before returning, so a failed build leaks nothing.</para></summary>
    internal SourceChain? BuildSourceChain(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
        IReadOnlyList<string> poles, string paramName, List<IDisposable> overlays, out string? error)
    {
        error = null;
        if (poles is null || poles.Count == 0)
        {
            error = $"{paramName} is empty — name at least one source: 'winner' for the active load order's winning " +
                    "version of each record, or a plugin filename for that plugin's version.";
            return null;
        }

        var arms = new List<SourceArm>(poles.Count);
        var openedHere = new List<IDisposable>();
        Mo2Composition? comp = null;
        string modsDir = "", dataDir = "", overwriteDir = "", profileDir = "";

        string Fail(string message)
        {
            foreach (var d in openedHere) { try { d.Dispose(); } catch { /* disposing a failed build */ } }
            return message;
        }

        // Every refusal path routes through Fail, but a THROW out of the loop bypassed all of them and leaked the
        // overlays opened so far — the caller's finally only disposes what reached `overlays`, which happens on the
        // last line. A leaked overlay holds a plugin file handle open, which is exactly what MO2 and xEdit must be
        // free to move.
        try
        {
        for (int i = 0; i < poles.Count; i++)
        {
            var spelling = (poles[i] ?? "").Trim();
            var at = poles.Count == 1 ? paramName : $"{paramName}[{i}]";
            if (spelling.Length == 0)
            {
                error = Fail($"{at} is blank — every element must name a source ('winner', or a plugin filename).");
                return null;
            }

            // ---- pole: winner ----------------------------------------------------------------------------
            if (string.Equals(spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase))
            {
                arms.Add(new SourceArm(SourcePoles.Winner, SourceArmKind.ActiveOrder, "the active load order (each record's winning version)",
                    fk =>
                    {
                        var w = view.ResolveWinner(fk);
                        return w is null ? null : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    }));
                continue;
            }

            // ---- pole: previous_provider — refused, with the path to making it legal ---------------------
            if (string.Equals(spelling, SourcePoles.PreviousProvider, StringComparison.OrdinalIgnoreCase))
            {
                error = Fail(
                    $"{at}: '{SourcePoles.PreviousProvider}' cannot name a source for a walk. It is SUBJECT-relative — the provider " +
                    "immediately below a named subject plugin — and a walk reaches records through links, with no subject plugin " +
                    "for each one to be relative to. Name the plugin you mean, or 'winner' for the active order's winning version. " +
                    "If you have a case where it does have a defined meaning here, file it as a gap report — that is what would " +
                    "define it.");
                return null;
            }

            // ---- pole: named(plugin) — active, or an off-order file --------------------------------------
            // ACTIVE arm first: the plugin is in the order under this very view, so its bodies come off the shared
            // captured build rather than a second overlay of the same file.
            if (view.ContainsPlugin(spelling))
            {
                var active = spelling;
                arms.Add(new SourceArm(spelling, SourceArmKind.ActiveOrder, $"'{active}' (active in the load order)",
                    fk => view.GetRecord(session, active, fk)));
                continue;
            }
            // A path that names the order's own copy of an active plugin is that plugin, not an off-order file: a
            // full path never matches the name table, so without this the live copy of an active plugin takes the
            // off-order arm and is described as not in the load order.
            if (LooksLikePath(spelling) && ActiveNameForPath(view, spelling) is { } activeName)
            {
                arms.Add(new SourceArm(activeName, SourceArmKind.ActiveOrder, $"'{activeName}' (active in the load order; named by path)",
                    fk => view.GetRecord(session, activeName, fk)));
                continue;
            }

            if (comp is null)
            {
                try { lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; } }
                catch (Exception ex)
                {
                    error = Fail($"{at}: '{spelling}' is not in the load order and the MO2 roots couldn't be derived to find it on disk: {ex.Message}");
                    return null;
                }
                comp = Mo2LoadOrder.ReadComposition(profileDir);
            }

            // offerModParam is false because this refusal names a LIST element, and the disambiguator that works
            // here is a full path in that element, not a tool-level mod= applying to every element at once.
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, spelling, null, offerModParam: false);
            if (loc.Error is not null)
            {
                // Suggested from every plugin the locate SEARCHED, not just the active order, so a typo of a disabled
                // plugin gets a suggestion instead of silence. Empty when nothing is close.
                var pool = Mo2LoadOrder.AllPluginFileNames(comp, modsDir, dataDir, overwriteDir);
                error = Fail($"{at}: source '{spelling}' is not in the load order and {loc.Error}" +
                             PluginNameSuggest.DidYouMean(spelling, pool));
                return null;
            }
            if (loc.Ambiguous is not null)
            {
                error = Fail($"{at}: source '{spelling}' is not in the load order and {loc.Ambiguous.Count} mod folders provide a file " +
                             $"with that name ({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess " +
                             "which version to read. Put the full path to the copy you mean in this element.");
                return null;
            }

            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex)
            {
                error = Fail($"{at}: source file '{spelling}' ({loc.Path}) could not be opened as a Skyrim plugin ({ex.Message}).");
                return null;
            }
            openedHere.Add((IDisposable)ov);

            // Lazy per-type link cache, so there is no eager whole-file parse. A per-record parse fault throws out of
            // the fetch on purpose: SourceChain turns it into a fault that STOPS the chain, because substituting a
            // later arm's version of a record this arm actually carries would be a silently wrong answer.
            var cache = ov.ToImmutableLinkCache();
            var where = $"file '{Path.GetFileName(loc.Path!)}' ({loc.Where}{(loc.WhyNotActive is { } why ? $"; NOT active — {why}" : "")})";
            arms.Add(new SourceArm(spelling, SourceArmKind.File, where,
                fk => cache.TryResolve(fk, out var body) ? body : null));
        }

        overlays.AddRange(openedHere);
        return new SourceChain(arms);
        }
        catch { foreach (var d in openedHere) { try { d.Dispose(); } catch { } } throw; }
    }

    /// <summary>The closure-copy operation's service half: resolve the ordered source universe, walk the source
    /// record's seed links, then hand the result to the core to build and serialize.
    /// <para>The layer split is the write path's: the core does records and serialize, the service does lanes,
    /// folders and MO2. So this resolves poles, the walk, the output path and the active target body, and
    /// <see cref="ClosureCopy.BuildAndWrite"/> owns everything from the patch mod onward.</para>
    /// <para>Prose-free by design: inputs arrive already validated and refusals come back as typed data, because the
    /// tool layer owns every user-facing sentence.</para></summary>
    internal ClosureCopyOutcome CopyClosure(
        FormKey sourceKey, IReadOnlyList<string> sourcePoles,
        IReadOnlyList<string> seedPaths, IReadOnlyList<WalkExclusion> exclusions,
        FormKey? targetKey, string? newEditorid,
        string? patchName, string? into)
    {
        lock (_writeGate)
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            using var session = resolver.OpenSession();
            var overlays = new List<IDisposable>();
            try
            {
                var chain = BuildSourceChain(view, session, sourcePoles, "from_source", overlays, out var chainError);
                if (chain is null) return ClosureCopyOutcome.Fail(engine: chainError);
                var consulted = chain.Arms.Select(a => a.Spelling).ToList();

                var srcFetch = chain.Fetch(sourceKey, "from");
                if (srcFetch.Fault is { } f)
                    return ClosureCopyOutcome.Fail(
                        walk: new WalkRefusal(WalkRefusalKind.SourceFault, sourceKey, "from",
                            new[] { sourceKey }, f.Cause, Fault: f), sources: consulted);
                if (srcFetch.Hit is not { } srcHit)
                    return ClosureCopyOutcome.Fail(
                        walk: new WalkRefusal(WalkRefusalKind.SourceMiss, sourceKey, "from",
                            new[] { sourceKey }, "", Miss: chain.Miss(sourceKey, "from")), sources: consulted);

                // The BOUND universe: the source record's own plugin plus every FILE arm named (the plugins being
                // copied away from), never an implicit base master — copying a vanilla-defined record must not
                // classify vanilla as "the source" and wholesale-internalize it.
                var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
                var bound = new HashSet<ModKey>();
                if (!baseMasters.Contains(sourceKey.ModKey)) bound.Add(sourceKey.ModKey);
                // EVERY arm the caller named, whatever kind it resolved to. Binding only the File arms would make
                // the artifact depend on an MO2 checkbox: an enabled override's records would stay mastered links
                // while the same plugin disabled would be internalized. Naming a plugin in from_source= IS the
                // caller saying it is a source being copied away from, and that is what makes the standalone claim
                // true. `winner` stays exempt: it is the whole load order, not a plugin, and binding it would
                // internalize vanilla.
                foreach (var arm in chain.Arms)
                {
                    if (string.Equals(arm.Spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase)) continue;
                    ModKey mk;
                    try { mk = ModKey.FromFileName(Path.GetFileName(arm.Spelling)); } catch { continue; }
                    if (!baseMasters.Contains(mk)) bound.Add(mk);
                }
                bool IsBound(FormKey fk) => bound.Contains(fk.ModKey);

                // The transplant note belongs to the case where the donor-bound set is EMPTY — nothing is being
                // copied away from at all. Keying it on `from`'s own defining plugin answers a different question:
                // a base-game FormID whose bound set holds a named overhaul still internalizes and strips that
                // plugin's records, so the note would claim nothing was being removed directly above the list that
                // removed them. Empty is the only state in which the note is true.
                var nothingBound = bound.Count == 0;

                if (ClosureWalk.ResolveSeeds(srcHit.Body, seedPaths, out var seeds) is { } seedRefusal)
                    return ClosureCopyOutcome.Fail(walk: seedRefusal.Refusal, sources: consulted);

                var scope = WalkScope.StandaloneFrom(bound, fk => view.ResolveWinner(fk) is not null);
                var walk = ClosureWalk.Run(seeds, chain, scope, exclusions);
                if (!walk.Success) return ClosureCopyOutcome.Fail(walk: walk.Refusal, sources: consulted);

                string outPath; bool extend, created;
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? newEditorid?.Trim() : null), into, out extend, out created,
                                                  freshPatch: FreshPatchRemedy.CreatedByOmittingInto); }
                catch (Exception ex) { return ClosureCopyOutcome.Fail(engine: ex.Message, sources: consulted); }
                var patchModKey = ModKey.FromFileName(Path.GetFileName(outPath));

                // The ACTIVE target body is the service's to fetch (it needs the view); an IN-PATCH target is NOT,
                // and is deliberately left null here so core resolves it off the OPENED patch mod. Fetching it
                // here would mean resolving a record through a load order the patch is not part of.
                IMajorRecordGetter? targetActiveBody = null;
                if (targetKey is { } tk && tk.ModKey != patchModKey)
                {
                    var tw = view.ResolveWinner(tk);
                    targetActiveBody = tw is null ? null : view.GetRecord(session, tw.Value.WinnerPlugin, tk);
                    if (targetActiveBody is null)
                    {
                        if (created) RemoveFolderCreatedThisCall(outPath);
                        return ClosureCopyOutcome.Fail(
                            copy: new CopyRefusal(CopyRefusalKind.Transplant, "the target is not in the active load order", Key: tk),
                            sources: consulted);
                    }
                }

                // Cleanup is finally-shaped rather than success-flag-guarded: a throw out of BuildAndWrite would
                // bypass a `!outcome.Success` check and leave the fresh mod folder on disk, so the next call would
                // start suffixing _001 — the accretion RemoveFolderCreatedThisCall exists to prevent.
                var wrote = false;
                try
                {
                var outcome = ClosureCopy.BuildAndWrite(
                    outPath, extend, sourceKey, srcHit, walk, seedPaths,
                    targetKey, targetActiveBody, newEditorid, IsBound, bound, nothingBound,
                    mk => view.ContainsPlugin(mk.FileName.String),
                    pf => { session.ReleaseOverlay(pf); return session.AllMastersExcept(pf); },
                    consulted,
                    ex => WritePatchBuilder.SerializeFailure("", ex, session, ""));

                wrote = outcome.Success;
                return outcome;
                }
                finally { if (!wrote && created) RemoveFolderCreatedThisCall(outPath); }
            }
            finally { foreach (var d in overlays) { try { d.Dispose(); } catch { } } }
        }
    }

    /// <summary>Test seam for <see cref="BuildSourceChain"/>: drives the real builder over the real MO2 resolution,
    /// under the same view, session and overlay lifetime the production call gives it, and hands the result to
    /// <paramref name="body"/> while the sources are still open. A chain whose overlays are disposed resolves
    /// nothing, so a seam that returned the chain could only test its refusals.</summary>
    internal T WithSourceChainForGuard<T>(IReadOnlyList<string> poles, string paramName, Func<SourceChain?, string?, T> body)
    {
        var resolver = Resolver;
        var view = resolver.Capture();
        using var session = resolver.OpenSession();
        var overlays = new List<IDisposable>();
        try
        {
            var chain = BuildSourceChain(view, session, poles, paramName, overlays, out var error);
            return body(chain, error);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* test teardown */ } } }
    }

    /// <summary>The in-place branch of <see cref="ApplyEdits"/>, running under _writeGate. It resolves
    /// <paramref name="target"/> to its real on-disk path via the load order rather than the houseCARL-owned folder
    /// model; enforces the persistent first-touch consent handshake, keyed off the resolved path; checks the parent
    /// is writable; drives <see cref="WritePatchBuilder.ApplyInPlace"/> with the touched-record verify forced on; and
    /// on success stamps the distinct <c>editedInPlace=</c> marker — never <c>generated=true</c>, because the user's
    /// mod must keep failing <see cref="IsHouseCarlOwned"/> so a later into= cannot blind-overwrite it.
    /// <paramref name="acknowledge"/> waives the consent axis only; the verify is a corruption-axis fact no
    /// acknowledgement overrides.</summary>
    WritePatchBuilder.PatchOutcome ApplyEditsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.PatchEdit> edits,
        string target, bool acknowledge, bool dryRun = false,
        IReadOnlyDictionary<WritePatchBuilder.PatchEdit, IMajorRecordGetter>? copyFromSources = null)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename, which is unique in an
        // order. Refuse loudly if it is not a real active plugin, which closes the coincidental-folder collision.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.PatchOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place edits the file the game actually loads. Nothing was written.")
                with { Epoch = view.Epoch };

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.PatchOutcome.Fail(locRefusal)
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: a persistent, server-enforced first-touch handshake keyed off the resolved path. It is
        // not a sticky mode — each in-place write still names its own target=, so this only stops re-explaining the
        // trade-off and never routes an ambiguous request to in-place. A dry run bypasses the handshake and never
        // persists an acknowledgement, because consent gates touching the original and a dry run touches nothing;
        // the pending consent is surfaced as a note instead. The check gates entry here, while a real write's
        // acknowledgement is recorded only once the edit has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        string? ackNote = null;
        bool owesConsent = false;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                // Stamped like every other post-capture outcome: this branch is reached only after the view above
                // resolved the target, and it is the most common in-place response shape, so an unstamped one would
                // break the "every write response carries an epoch" contract where callers meet it most.
                return WritePatchBuilder.PatchOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Epoch = view.Epoch };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade: the swap stages a sibling temp in this directory,
        // so a read-only or locked parent is caught up front with a clear message before any work. Kept in the dry
        // run too, since an unwritable parent is exactly what the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.PatchOutcome.Fail(why) with { Epoch = view.Epoch };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ApplyInPlace(resolver, rulebook, edits, targetPath, targetName, fullReadback: true, dryRun, copyFromSources);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note, since those describe a
        // write that happened; only the core's would-grow note and the pending-consent note ride along.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq. Both are
        // best-effort and neither failing fails the done edit. An in-place edit can prune a master and shift the
        // plugin's own on-disk FormIDs, staling its .seq — surfaced as a note, never auto-regenerated.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "edit");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Resolve an active plugin's on-disk path by filename: exact match first, then a lenient retry
    /// appending each plugin extension if the caller dropped it. <paramref name="resolvedName"/> echoes the canonical
    /// filename that matched, and null means no such active plugin. The path is the load order's winning path for
    /// that filename — the file the game loads.</summary>
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

    /// <summary>The opening claims both first-touch prompts make — the plugin one and the mesh one — in one place.
    /// <para>The header states when the prompt stops: not "once", but once a write LANDS. A refused call records
    /// nothing (see <see cref="PersistInPlaceConsent"/>), so a caller can legitimately meet this prompt more than
    /// once, and a prompt calling itself one-time would say that cannot happen.</para>
    /// <para>The file claim is deliberately direction-neutral. Asserting a state transition would be false when the
    /// transition already happened: a write that lands and then fails its verify mutates the file while recording no
    /// consent, so the next call re-prompts against an already-modified file. Stating the durable fact instead is
    /// true either way and needs no state to distinguish them.</para></summary>
    static string InPlaceHandshakeLead(string name, string path, string subject, string verb) =>
        $"in-place edit of '{name}' — first-time confirmation (shown until an in-place write to this {subject} LANDS; " +
        "a call that is refused records nothing, so you may see this again):\n" +
        $"  • This {verb} your ORIGINAL file ({path}) — not a copy. houseCARL keeps NO backup or undo and cannot " +
        "restore what it overwrites, so keep your own.\n";

    /// <summary>The first-touch in-place CONSENT prompt for a PLUGIN (server-enforced). Opens with the shared lead
    /// (<see cref="InPlaceHandshakeLead"/> — when the prompt stops, and what it costs the original), then states the
    /// plugin-specific trade-off: the whole plugin is re-laid-out like xEdit/CK do on save with the touched records
    /// VERIFIED and Mutagen trusted for the rest, and the default new-patch lane stays recommended. Waives the CONSENT
    /// axis only (re-call with acknowledge=true).</summary>
    static string InPlaceHandshakeText(string pluginName, string path) =>
        InPlaceHandshakeLead(pluginName, path, "plugin", "writes to") +
        "  • houseCARL re-lays-out the WHOLE plugin the way xEdit/CK do on save (every record re-serialized), VERIFIES the records you edit, and trusts Mutagen for the rest.\n" +
        "  • It still refuses if the file can't be parsed, or carries engine-reserved (sub-0x800) records.\n" +
        "  • The default lane (a NEW patch, originals untouched) stays the recommended way — this is the explicit opt-in.\n" +
        "Re-call the SAME edit with acknowledge=true to proceed.";

    /// <summary>PERSIST the one-time in-place acknowledgement for <paramref name="targetPath"/> — called by every
    /// in-place lane AFTER the write it gated has actually landed, never before. <paramref name="owed"/> is the consent
    /// gate's own answer: a first touch of this file that carried <c>acknowledge=true</c>. False (already acknowledged,
    /// or a dry run, which touches nothing and so records nothing) makes this a no-op. Returns the store's error
    /// when the config write failed, for the caller's own note; null when there was nothing to record or it recorded.
    /// <para>Ordering is the point. Between the consent check and the byte that changes on disk, every lane runs a
    /// chain of refusals that leave the original untouched — the writable-parent pre-flight, then the builder's own
    /// checks: a target Mutagen cannot fully parse, a record the file does not carry, a link into a plugin that is
    /// not loaded, the localized backstop. Recording the acknowledgement ahead of them would spend the first-touch
    /// confirmation on a write that never happened, letting the next call — the first real rewrite of the original —
    /// through unprompted. Persisting last makes that whole class unreachable, including a check added later. The
    /// gate itself does not move: <c>already || acknowledge</c> still decides whether the call runs.</para>
    /// <para>Callers persist on the lane's own success, which is the conservative reading: a lane that mutated the
    /// file and then failed its post-write verify records nothing and re-prompts next time. Over-prompting costs a
    /// confirmation, under-prompting costs a file.</para>
    /// <para><paramref name="what"/> names what just happened ("edit", "removal", "create", "forward") and
    /// <paramref name="subject"/> the thing being remembered; those two words are the whole per-lane variation, and
    /// the shared sentence lives here rather than once per lane. It says "the next in-place call" rather than "a
    /// future session" because <see cref="UserConfigStore"/> caches nothing, so a failed write re-prompts
    /// immediately.</para></summary>
    string? PersistInPlaceConsent(bool owed, string targetPath, string what, string subject = "plugin")
    {
        if (!owed) return null;
        string? err;
        // The store returns its write failures rather than throwing, but its cross-process lock handling sits outside
        // that try. This runs AFTER the file changed, so a throw escaping here would report a failure for a write
        // that landed — the last step of a successful call must not be able to throw.
        try { err = _store.RecordInPlaceAcknowledged(targetPath) is { ok: false, error: var e } ? (e ?? "unknown error") : null; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; }
        return err is null ? null
            : $"the in-place acknowledgement could not be saved ({err}) — the {what} proceeded, " +
              $"but the next in-place call will ask for this {subject} again.";
    }

    /// <summary>Writable-parent pre-flight for the in-place swap: the staged temp is a sibling of the target, so
    /// prove the parent is writable now rather than degrade to a non-atomic write later. True, with a named
    /// <paramref name="why"/>, means refuse. Probes by writing and deleting an empty sibling temp. It checks the
    /// PARENT is writable, not that the target file is unlocked by another process; that case surfaces loudly at the
    /// <c>File.Replace</c> swap with the original byte-intact, so it needs no separate pre-flight.</summary>
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
    /// <c>meta.ini</c> — a breadcrumb that houseCARL touched this user mod, without ever writing
    /// <c>generated=true</c>, so <see cref="IsHouseCarlOwned"/> still reads false and a later into= cannot
    /// blind-overwrite it. Preserves every existing line, merging into or creating the <c>[houseCARL]</c> section,
    /// and only for an MO2 mod folder under ModsDir, so it never pollutes the game Data dir for a loose plugin.
    /// Best-effort: returns a note on failure, since the edit already succeeded, and null on success or N/A.</summary>
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

    /// <summary>Join any number of optional notes into one space-separated string, skipping the null and blank ones;
    /// null when none are present. Variadic so a lane can merge several best-effort side-effect notes into the
    /// single <c>Note</c> the outcome carries.</summary>
    static string? JoinNotes(params string?[] notes)
    {
        var present = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
        return present.Length == 0 ? null : string.Join(" ", present);
    }

    /// <summary>Flag, never auto-regenerate, a stale .seq after an in-place write. A master prune may have shifted
    /// every own record's on-disk FormID and staled the plugin's <c>.seq</c>, whose start-game-enabled quests would
    /// then silently never start on a fresh save. The .seq is resolved through the same captured VFS view the compact
    /// gate uses — loose roots plus active BSAs, not a bare folder check, which would miss a filed or BSA-packed one
    /// — and if a loose .seq exists but no longer lists one or more SGE quests at their current on-disk FormIDs, this
    /// returns a warning naming them and the fix. Null when there is nothing to flag: no .seq, a BSA-only one whose
    /// bytes cannot be checked here, or every SGE quest still covered. Best-effort: any failure yields a soft
    /// advisory rather than a throw, because the write already succeeded.</summary>
    string? SeqStaleInPlaceNote(string targetPath, string targetName)
    {
        try
        {
            AssetResolver assetResolver;
            lock (_gate) { assetResolver = Assets; }                          // reentrant under the held _writeGate
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
                 + "(a master prune in an in-place write shifts these FormIDs; the .seq may also have been stale before this edit). Regenerate it with " + ToolNames.WriteSeq + ".";
        }
        catch (Exception ex)
        {
            return $"could not check whether '{targetName}'s .seq is still current after this edit ({ex.GetType().Name}) — "
                 + "if it has start-game-enabled quests, run " + ToolNames.Check + " findings=[\"dialogue\"] seeds=[the quest] to confirm the .seq still lists them.";
        }
    }

    /// <summary>Remove whole records a houseCARL patch carries — a literal drop from the plugin, the companion to
    /// <see cref="ApplyEdits"/>. In the default lane <paramref name="patch"/> is required and names an existing
    /// houseCARL-owned patch, resolved and ownership-gated the same way an extend is, because a removal only makes
    /// sense against a patch that already carries the record. In the in-place lane it drops the record from an
    /// existing plugin instead, including one houseCARL did not author. Parses every formid all-or-nothing, then
    /// drives <see cref="WritePatchBuilder.RemoveRecords"/>: present-check, remove, re-serialize, with clean-masters
    /// riding along. The default lane never touches originals.</summary>
    public WritePatchBuilder.RemovalOutcome RemoveRecords(IReadOnlyList<string> formids, string? patch,
        string? target = null, bool inPlace = false, bool acknowledge = false, string? inPlaceRemedy = null)
    {
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.RemovalOutcome.Fail("no formids supplied — pass the FormID(s) of the record(s) to remove.");

        // In-place is the explicit, named-file opt-in: drop a record from an existing plugin, including one houseCARL
        // did not author, instead of from a houseCARL patch. The contract is validated up front — it requires
        // target=, and it is mutually exclusive with patch=. target= without in_place is a no-op the caller likely
        // did not mean, so it is named rather than silently ignored. Mirrors ApplyEdits' contract.
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
            // Renders the caller's own spelling, exactly like the not-found arm below: null names no lane. Hardcoding
            // one caller's sentence here would make a fact about a different file load-bearing in this one.
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it)."
                + (inPlaceRemedy is null ? "" : " " + inPlaceRemedy));

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path). Pure — outside the gate.
        var keys = new List<FormKey>(formids.Count);
        var problems = new List<string>();
        var door = OpenWriteFormIdDoor();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formid[{i}]: empty."); continue; }
            try { keys.Add(door.Parse(raw)); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formid[{i}]: ", $"formid[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING removed:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // removal re-serializes the patch — same gate
        {
            var resolver = Resolver;                                      // builds/refreshes the index and the overlays for the re-serialize

            if (inPlace)
                return RemoveRecordsInPlace(resolver, keys, target!.Trim(), acknowledge);

            // Resolve and ownership-gate the patch path the same way an extend does: it must exist and carry the
            // houseCARL marker. No fresh-patch remedy is offered, because removal cannot create a patch and this
            // tool's patch= already names an existing one, so that remedy would tell the caller to re-issue the call
            // that just failed. The lane clause instead offers what this lane can honestly offer.
            // The in-place half comes from the TOOL, because the two calling tools spell that lane differently and
            // the service cannot tell which one called it; null offers only the half true at every altitude.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _,
                                              laneClause: WriteSentences.RemoveNoFreshPatch
                                                          + (inPlaceRemedy is null ? "" : " " + inPlaceRemedy)); }
            catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

            return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
        }
    }

    /// <summary>The in-place branch of <see cref="RemoveRecords"/>, running under _writeGate. The remove counterpart
    /// of <see cref="ApplyEditsInPlace"/>, reusing every in-place seam: the same foreign-target resolver, the same
    /// persistent first-touch consent handshake keyed off the resolved path and shared with the edit and create lanes
    /// so acknowledging a plugin once covers all three, the same writable-parent pre-flight, and the same
    /// <c>editedInPlace=</c> marker rather than <c>generated=true</c>. It drives
    /// <see cref="WritePatchBuilder.RemoveRecordsInPlace"/> with the absence verify forced on.
    /// <paramref name="acknowledge"/> waives the consent axis only. There is no rulebook here: a removal pre-flights
    /// nothing, and the present-check that the target carries the record is the whole gate.</summary>
    WritePatchBuilder.RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> keys, string target, bool acknowledge)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin, which closes the coincidental-folder collision. Same resolver as the other lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.RemovalOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place removes from the file the game actually loads. Nothing was written.")
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemoveNoEquivalent) is { } locRefusal)
            return WritePatchBuilder.RemovalOutcome.Fail(locRefusal)
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // and create lanes because it is the same "touch your original" trade-off. The check gates entry here; the
        // acknowledgement is recorded only once the removal has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: the most common in-place response shape.
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Epoch = view.Epoch };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why) with { Epoch = view.Epoch };

        // The write, with the absence verify forced on.
        var outcome = WritePatchBuilder.RemoveRecordsInPlace(resolver, keys, targetPath, targetName);

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq — both
        // best-effort, and neither failing fails the done removal. A removal can drop the last reference to a master
        // and shift on-disk FormIDs, staling the plugin's .seq; that is surfaced, never auto-regenerated.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "removal");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Forward a named plugin's version of one or more records into a patch as an override — xEdit's "copy
    /// as override into", the inverse of <see cref="ApplyEdits"/>'s winner-override. Parses every formid
    /// all-or-nothing, pre-locates <paramref name="fromPlugin"/> when the active order does not contain it, resolves
    /// the folder-per-patch output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch), then drives
    /// <see cref="WritePatchBuilder.ForwardRecords"/>. The whole source record is copied verbatim, so the SOURCE
    /// plugin rather than the load-order winner decides the content — and forwarding the origin master reverts a
    /// record to vanilla. Originals are never touched in the default lane;
    /// <paramref name="target"/> with <paramref name="inPlace"/> is the explicit opt-in third route, forwarding into
    /// an existing plugin's own file under the same consent gate as the sibling write tools.</summary>
    /// <param name="sourceParam">The spelling the calling tool exposes for the source pole. Every refusal that names
    /// the parameter renders the calling surface's word, because a caller cannot fix a parameter its tool does not
    /// expose.</param>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false, string sourceParam = "from_plugin")
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"{sourceParam} is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
        if (formids is null || formids.Count == 0)
            return WritePatchBuilder.ForwardOutcome.Fail("no formids supplied — pass the FormID(s) to forward from the source plugin.");

        // In-place is the explicit, named-file opt-in, with the same contract as the sibling write tools: in_place
        // requires target=, is mutually exclusive with into=, and target= without in_place is a no-op the caller
        // likely did not mean. Each misuse is named rather than silently ignored.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to forward into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place forwards into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.ForwardOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to forward into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        // Parse every formid first, collecting all problems, like the edit and remove paths. Pure, so outside the gate.
        var fp = fromPlugin.Trim();
        var specs = new List<WritePatchBuilder.ForwardSpec>(formids.Count);
        var problems = new List<string>();
        var door = OpenWriteFormIdDoor();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formid[{i}]: empty."); continue; }
            try { specs.Add(new WritePatchBuilder.ForwardSpec { Target = door.Parse(raw), FromPlugin = fp }); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formid[{i}]: ", $"formid[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
        }
        if (problems.Count > 0)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"refused — {problems.Count} of {formids.Count} formid(s) malformed; NOTHING forwarded:\n  - " + string.Join("\n  - ", problems));

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;                                      // builds/refreshes the index and the overlays for the source fetch and serialize

            // A source the active order does not contain is located on disk and pre-fetched here, on both lanes: the
            // in-place TARGET must stay active by that lane's contract, but the SOURCE has no such need. A no-op for
            // an active source. The overlay must outlive the serialize, because the bodies are deep-copied during
            // the write, so it is disposed in the finally below.
            var offOrder = ResolveOffOrderForwardSource(resolver, fp, specs, out var offOverlay, out var offEpoch, out var offError, out var sourceName);
            if (offError is not null)
                return WritePatchBuilder.ForwardOutcome.Fail(offError) with { Epoch = offEpoch };
            // A path that named the ACTIVE copy resolves as that plugin, so re-spell every spec's source and the
            // engine can look it up in the index — a path is not a key there. That is also what makes the winner
            // comparison, the self-forward name check and the report's "copied from" speak the order's vocabulary.
            if (!string.Equals(sourceName, fp, StringComparison.Ordinal))
                specs = specs.Select(s => new WritePatchBuilder.ForwardSpec { Target = s.Target, FromPlugin = sourceName }).ToList();
            try
            {
                if (inPlace)
                    return ForwardRecordsInPlace(resolver, specs, target!.Trim(), acknowledge, dryRun, sourceParam, offOrder);

                // A dry run resolves the would-be output path without creating the mod folder.
                string outPath; bool extend, created;
                try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
                catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message); }

                var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, fullReadback, dryRun, sourceParam, offOrder);
                if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused forward leaves no orphan folder
                return outcome;
            }
            finally { offOverlay?.Dispose(); }
        }
    }

    /// <summary>The in-place branch of <see cref="ForwardRecords"/>, running under _writeGate. Reuses every in-place
    /// seam: the same foreign-target resolver, the same persistent first-touch consent handshake keyed off the
    /// resolved path and shared across all in-place lanes, the same writable-parent pre-flight, and the same
    /// <c>editedInPlace=</c> marker rather than <c>generated=true</c>. Drives
    /// <see cref="WritePatchBuilder.ForwardRecordsInPlace"/> with the touched-record verify forced on.
    /// <paramref name="acknowledge"/> waives the consent axis only.</summary>
    WritePatchBuilder.ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<WritePatchBuilder.ForwardSpec> specs, string target, bool acknowledge,
        bool dryRun = false, string sourceParam = "from_plugin",
        WritePatchBuilder.OffOrderForwardSource? offOrder = null)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin. Same resolver as the other in-place lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place forwards into the file the game actually loads. Nothing was written.")
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.ForwardOutcome.Fail(locRefusal)
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the other
        // in-place lanes because it is the same "touch your original" trade-off. A dry run bypasses the handshake and
        // never persists an acknowledgement, surfacing the pending consent as a note instead. The check gates entry
        // here; a real write's acknowledgement is recorded only once the forward has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        string? ackNote = null;
        bool owesConsent = false;
        if (dryRun)
        {
            if (!already)
                ackNote = $"in-place consent is still PENDING for '{targetName}' — the REAL write's first touch of this " +
                          "plugin will show the confirmation (re-call with acknowledge=true); a dry run neither needs nor records it.";
        }
        else
        {
            if (!already && !acknowledge)
                // Stamped for the reason the edit lane's twin states (the most common in-place response shape).
                return WritePatchBuilder.ForwardOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                    with { Epoch = view.Epoch };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade. Kept in the dry run, which predicts exactly what
        // the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.ForwardOutcome.Fail(why) with { Epoch = view.Epoch };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ForwardRecordsInPlace(resolver, specs, targetPath, targetName, fullReadback: true, dryRun, sourceParam, offOrder);

        // A successful dry run stamps nothing — no editedInPlace marker and no .seq note.
        if (dryRun)
            return JoinNotes(outcome.Note, ackNote) is { } dn ? outcome with { Note = dn } : outcome;

        // On success, record the acknowledgement, then stamp the audit marker and flag a now-stale .seq — both
        // best-effort, and neither failing fails the done forward.
        if (outcome.Success)
        {
            // ackNote is null here: the only other writer is the dry-run branch, which returned above.
            ackNote = PersistInPlaceConsent(owesConsent, targetPath, "forward");
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var seqNote = SeqStaleInPlaceNote(targetPath, targetName);
            // outcome.Note first — the core's master-grow re-sort note must survive the merge.
            var note = JoinNotes(outcome.Note, ackNote, markerNote, seqNote);
            if (note is not null) return outcome with { Note = note };
        }
        return outcome;
    }

    /// <summary>Create an empty, header-only plugin: a valid TES4 header with zero records, no masters, optionally
    /// ESL-flagged, named exactly <paramref name="pluginName"/>. The primitive for "plugin Foo.esp needs to exist" —
    /// a basename-bound SKSE config trigger, a placeholder ESL, a dummy master — and it authors no record, so it adds
    /// no conflict footprint. Unlike the patch-write paths the name is used verbatim and never auto-suffixed, because
    /// a trigger plugin's whole job is that its basename matches the config bound to it; a collision therefore
    /// refuses loudly rather than renaming or overwriting, whether a plugin of that basename is already active in the
    /// order or a houseCARL mod folder of that name is already on disk. The core
    /// <see cref="WritePatchBuilder.CreatePlugin"/> builds, serializes and re-reads to confirm, and a refused create
    /// that just made the output folder leaves no orphan.</summary>
    public WritePatchBuilder.CreatePluginOutcome CreatePlugin(string pluginName, bool esl = false, string? author = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                "plugin_name is required — a header-only plugin has no record to derive a name from, so name it explicitly (e.g. 'Authoria - CraftingCategories').");

        var stem = PatchStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                $"plugin_name '{pluginName}' has no usable name once path parts and the plugin extension are stripped — give a plain name like 'MyTrigger'.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            // Touch Resolver FIRST: in instance mode _modsDir is derived lazily inside the Resolver getter, so a cold
            // first call would otherwise see an empty _modsDir and misreport "ModsDir '' does not exist". Capturing
            // the view here both derives the paths and gives the collision check below what it needs.
            var view = Resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CreatePluginOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            // The basename is load-bearing for a trigger, so a collision is never auto-suffixed — refuse instead.
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
            if (!outcome.Success) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            return outcome;
        }
    }

    /// <summary>Compact / ESL-renumber a plugin — the data-layer twin of xEdit's "Compact FormIDs for ESL".
    /// Renumbers <paramref name="pluginName"/>'s originating records, flat and nested (cells, placed refs, dialog
    /// INFOs), into the light range 0x800–0xFFF; with <paramref name="esl"/> false it renumbers contiguously without
    /// the light flag or ceiling. It repoints every internal reference, keeps overrides at their master FormIDs, and
    /// emits the result. By default the output is a new plugin keeping the source's exact basename, so external
    /// masters still resolve, in a fresh houseCARL mod folder, leaving the original untouched and reviewable before
    /// the swap; <paramref name="inPlace"/> overwrites the original instead, under the in-place consent and with no
    /// backup.
    /// <para>The load-bearing safety: renumbering breaks any reference from OUTSIDE the plugin, which would point at
    /// FormIDs that no longer exist. The identify pass finds those external referencers across the whole order. With
    /// none, the default path just emits the new plugin; with some, the call is refused loudly with the list unless
    /// <paramref name="repointExternals"/> is set, which also rewrites each of them in place to follow the renumber.
    /// Any in-place overwrite requires <paramref name="acknowledge"/>, and a first call without it returns a confirm
    /// prompt listing exactly what will be rewritten.</para>
    /// <para>An inactive target — on disk but not in the load order, such as a fresh patch before an MO2 refresh, or
    /// a disabled mod — is resolved by filename via the shared locate contract and compacted off-order; its declared
    /// masters must still be active. An override-only target with esl=true takes the flag-only lane, with an empty
    /// remap and the write setting the light flag.</para>
    /// <para>Refuses loudly and writes nothing when the plugin is not found on disk, is ambiguous, was excluded as
    /// unparseable, needs more IDs than the light window holds, declares a master that is not active, or hits a
    /// serialize fault. Serialized on the write gate; the identify pass is one whole-order link walk, a deliberate
    /// one-shot cost.</para></summary>
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
                // Not in the active order → resolve the file on disk through the shared locate contract, covering
                // enabled, disabled and unlisted mod folders. This is the pre-enable finishing lane: ESL-flagging a
                // patch before an MO2 refresh puts it in plugins.txt. The requirement that protects correctness is
                // unchanged — every declared master must be active — and the external-referencer scan still runs
                // over the active order, which for a plugin nothing active masters is correctly empty.
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

            // A localized target refuses the in-place lane, checked as early as possible: before the identify pass,
            // the consent gate, and anything written or staged. A caller whose target also has external referencers
            // would otherwise meet the referencer refusal first, follow its repoint remedy, and only then be told the
            // operation was never possible.
            // The in-place write's own check cannot fire here: a compaction does not re-serialize the target, it
            // builds a fresh plugin and writes that over the original, so the mod handed to the write is never
            // flagged localized. What makes it refusable is what the rebuild does to a localized plugin, and both
            // outcomes are silent and land on a file with no review step and no undo: when the strings resolve, the
            // result is de-localized, with one language baked in and the mod's .STRINGS set no longer describing it;
            // when they do not, the same path bakes in blanks.
            // Keyed on the header flag, deliberately wider than the strings-resolve-nowhere case, because detecting
            // that case precisely is machinery that does not exist and neither outcome may happen silently. The
            // new-file lane is untouched: its output is a plugin the modder reviews before swapping it in, which is
            // the distinction this refusal rests on. Read once: the in-place lane refuses on it, the new-file lane
            // reports on it below.
            // Every shape refuses in place, and a source houseCARL could not READ refuses too — unreadable is not
            // not-localized. The shape decides only which sentence the caller gets, never the outcome.
            var srcShape = LocalizedStrings.Assess(srcPath, view.DataDir);
            // The decision collapses and stays fail-closed: anything that is not a read-and-clear flag refuses. The
            // WORDS do not — see CompactInPlaceRefusal. Same boolean, two jobs, only one of which may collapse.
            bool srcLocalized = srcShape.Shape != LocalizedShape.NotLocalized;
            if (inPlace && srcLocalized)
                return WritePatchBuilder.CompactOutcome.Fail(CompactInPlaceRefusal(name, srcShape));

            // The NEW-FILE lane's own refusal: a localized source whose strings houseCARL can find NOWHERE reads
            // every value EMPTY, and this lane copies that read into a plugin the caller keeps. The in-place refusal
            // above covers the wider flag, so this fires only for the new-file lane and only for that one shape.
            if (LocalizedStrings.ResolvesNowhere(srcShape.Shape))
                return WritePatchBuilder.CompactOutcome.Fail(
                    UnresolvableStringsRefusal(name, srcShape, "compact"));

            // 1. originating record keys + the remap into the (light, by default) window.
            if (!WritePatchBuilder.TryReadOriginatingKeys(srcPath, modKey, out var keys, out var keyErr))
                return WritePatchBuilder.CompactOutcome.Fail(keyErr!);
            string? flagOnlyNote = null;
            uint floor = RemapEngine.EslFloor;
            IReadOnlyDictionary<FormKey, FormKey> remapDict;
            if (keys.Count == 0)
            {
                // An override-only or empty plugin has nothing to renumber, but with esl=true the job the caller
                // wants — make it light — is trivially satisfiable, because the light window only constrains
                // originating records. Proceed with an empty remap: every record copies verbatim and the write sets
                // the light flag.
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

            // The identify pass: which plugins outside the target reference a record being renumbered — the break
            // risk. Nothing being renumbered means nothing can break, so the whole-order walk is skipped.
            var targets = remapDict.Keys.ToHashSet();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var id = targets.Count == 0
                ? new RemapEngine.IdentifyResult(Array.Empty<RemapEngine.ExternalRef>(), Array.Empty<string>(), 0, 0,
                                                 Array.Empty<string>(), Array.Empty<RemapEngine.ExternalOverride>(), Array.Empty<string>())
                : RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);

            // External-referencer policy: never silently ship a compaction that dangles an external reference.
            if (id.HasExternalReferencers)
            {
                var refList = $"{string.Join(", ", id.ExternalPlugins.Take(25))}{(id.ExternalPlugins.Count > 25 ? $", … (+{id.ExternalPlugins.Count - 25} more)" : "")}";
                if (!repointExternals)
                {
                    // This refusal's remedy is "re-run with repoint_externals", so it has to know whether that re-run
                    // would itself be refused — which happens when a referencer's strings are in a state houseCARL
                    // cannot rewrite. The caller learns that here rather than by following the instruction into a
                    // second refusal. The check runs only on the referencers already named, and only on this branch;
                    // the repoint branch below has its own, which refuses before anything is written.
                    var blocked = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                    var repointClause = blocked.Count == 0
                        ? "Re-run with repoint_externals=true AND in_place=true (+ acknowledge=true) to ALSO rewrite those plugins in place to follow "
                          + "the renumber, or handle them yourself first."
                        // Split by class: LocalizedAmong fails closed on a referencer it could not read, so its hits
                        // are not homogeneous, and one list would call every one of them localized including the
                        // file nobody managed to open.
                        : $"Re-running with repoint_externals=true will NOT work here: {BlockedReferencerCensus(blocked)}. "
                          + "houseCARL rewrites neither a localized plugin nor one it cannot read in place, so the "
                          + "repoint would refuse before touching anything. "
                          // Reasons are attributed once per class: blocked referencers can be in different shapes,
                          // so an unattributed reason reads as the reason for all of them and hands one plugin's
                          // account of where its text lives to another.
                          + BlockedReferencerReasons(blocked)
                          + " Until that is resolved, handle the references yourself instead.";
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — {id.ExternalPlugins.Count} plugin(s) outside '{name}' reference records it is about to renumber; compacting it " +
                        $"WOULD BREAK those references (they would point at FormIDs that no longer exist). Referencers: {refList}. " +
                        repointClause + " Nothing was written.");
                }
                // Repointing is only coherent paired with in_place: in the new-file lane the renumbered records live
                // only in the not-yet-active output, so repointing the externals now would leave them dangling
                // against the still-active original until the MO2 swap, and broken if the user rejects the output.
                if (!inPlace)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — repoint_externals requires in_place=true. {id.ExternalPlugins.Count} plugin(s) reference records being renumbered " +
                        $"({refList}); in the new-file lane those records exist ONLY in the not-yet-active P′, so repointing the externals now would leave " +
                        "them dangling against the still-active original until you complete the MO2 swap (and broken if you reject P′). Either compact IN " +
                        "PLACE (in_place=true) so the target and its referrers move together, or handle the externals yourself after enabling P′. Nothing was written.");
            }

            // The consent gate: any in-place overwrite — the target, the external referencers, or both — needs acknowledge.
            bool willOverwriteTarget = inPlace;
            bool willRepoint = id.HasExternalReferencers && repointExternals;

            // A plugin the identify pass could not read through REFUSES the in-place lane, before anything is
            // written and whatever acknowledge says. The referencer list is only as good as the scan behind it: an
            // unread plugin may reference records about to be renumbered, and nothing downstream would catch it —
            // the repoint pre-flight is fed only the referencers the scan DID find. In place there is no backup and
            // no review step, so a note in a prompt is the wrong instrument (and acknowledge=true on the first call
            // skips the prompt entirely). This matches the existing refusal for a referencer houseCARL cannot
            // rewrite: the better-known case already refuses, and this is the strictly less-known one.
            // The new-plugin lane keeps the note — its output is reviewed before it replaces anything.
            if ((willOverwriteTarget || willRepoint) && id.UnscannablePlugins is { Count: > 0 } unread)
            {
                var c = new System.Text.StringBuilder();
                c.Append($"refused — this is an IN-PLACE rewrite (no houseCARL backup or undo) and the external-reference pass could not read ")
                 .Append(unread.Count).Append(unread.Count == 1 ? " plugin, so houseCARL cannot tell whether it references records "
                                                               : " plugins, so houseCARL cannot tell whether they reference records ")
                 .Append($"'{name}' is about to renumber: ");
                c.Append(string.Join("; ", unread.Take(25).Select(WriteSentences.UnscannablePlugin)));
                if (unread.Count > 25) c.Append($"; … (+{unread.Count - 25} more)");
                c.Append(". NOTHING was written — ").Append($"'{name}' is untouched. ")
                 .Append("Either resolve that and run this again, or compact into a NEW plugin (in_place=false), which renumbers the same records into a ")
                 .Append("file you review and swap in yourself, leaving the original and its referencers alone.");
                return WritePatchBuilder.CompactOutcome.Fail(c.ToString());
            }

            // Before the consent gate, not after it: houseCARL cannot re-serialize a localized plugin without
            // scrambling its text, so a run whose referencer rewrites include one can never happen, and the gate
            // below would otherwise ask the modder to authorize an irreversible rewrite of their originals. Also
            // before ANY write, because the referencer rewrites run only after the compacted plugin is on disk: a
            // refusal discovered there would leave the target renumbered and its referencers on the old FormIDs,
            // which nothing downstream can undo.
            // No remedy is named. "Repoint them yourself first" is false: the new FormIDs do not exist until the
            // compaction runs and this verb never discloses the mapping, and a referencer repointed to guessed ids
            // stops matching the identify pass, so the follow-up compaction succeeds and reports a clean run over
            // links that now point nowhere.
            if (willRepoint)
            {
                var localized = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                if (localized.Count > 0)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — compacting '{name}' means rewriting the plugins that reference it, and houseCARL " +
                        // Split by class, count and label both: calling every hit localized would be false about the
                        // ones houseCARL could not open, a different problem with a different fix.
                        $"cannot rewrite all of them: {BlockedReferencerCensus(localized)}. " +
                        // The referencer's own reason, verbatim from the same decision the write would have made,
                        // attributed once per class: a caller refused here is being told about a plugin they did not
                        // name, so they need where that plugin's text is, or that it could not be opened.
                        $"{BlockedReferencerReasons(localized)} " +
                        "NOTHING was written and nothing was staged — " +
                        $"'{name}' is untouched. Following the renumber means rewriting those referencers in place, and " +
                        "houseCARL rewrites neither a localized plugin nor one it cannot read in place.");
            }
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
                // No unread-plugin note here: this lane refuses above when the scan could not read a plugin
                // through, so by the time the prompt is composed the referencer list is the whole story.
                c.Append("Re-call with acknowledge=true to proceed.");
                return WritePatchBuilder.CompactOutcome.Confirm(c.ToString());
            }

            // Pre-flight that the in-place target's parent is writable before any work — an early refusal rather than
            // a failure deep in the atomic swap. Each external referencer gets the same guarantee inside
            // RepointInPlace's own all-or-nothing write.
            if (inPlace && InPlaceParentUnwritable(srcPath, out var unwritable))
                return WritePatchBuilder.CompactOutcome.Fail(unwritable);

            // Output location: in place over the original, or a new file keeping the source's exact basename in a
            // fresh houseCARL mod folder, so its masters still resolve and the user swaps the folder in MO2.
            string outPath; bool createdFresh = false; RiderFolder rf = default;
            if (inPlace) outPath = srcPath;
            else
            {
                try { rf = ResolvePatchModFolder(patchName, null, Path.GetFileNameWithoutExtension(name) + " compacted", naming: null); }
                catch (InvalidOperationException ex) { return WritePatchBuilder.CompactOutcome.Fail(ex.Message); }
                createdFresh = rf.CreatedFresh;
                WriteOwnerMeta(rf.ModFolder, name);                       // the output keeps the source's exact basename
                outPath = Path.Combine(rf.OutputDir, name);
            }

            // Build and write the compacted plugin.
            var build = WritePatchBuilder.CompactBuild(srcPath, modKey, remapDict, view.PluginPath, outPath, esl, floor, view.DataDir);
            if (!build.Success)
            {
                if (!inPlace && createdFresh) RemoveOrNameRiderResidue(rf);   // a refused build leaves no orphan folder
                return WritePatchBuilder.CompactOutcome.Fail(build.Error!);
            }

            // Opt-in: repoint each external referencer in place, per-plugin all-or-nothing, with every result reported.
            var repointed = new List<WritePatchBuilder.RepointReport>();
            if (willRepoint)
                foreach (var ext in id.ExternalPlugins)
                {
                    var rep = RemapEngine.RepointInPlace(resolver, ext, remapDict);
                    repointed.Add(new WritePatchBuilder.RepointReport(ext, rep.Success, rep.Error));
                }

            // Carry the FormID-keyed assets a renumber moves: FaceGen head mesh and tint, and voice .fuz/.lip. The
            // records were renumbered, so the asset files the engine looks up BY FormID must follow, or a compacted
            // NPC mod silently dark-faces and a voiced mod goes mute. One captured asset view feeds both carries and
            // the SEQ check below, so all three agree on what is in the VFS. Best-
            //     effort and reported: the records are already written, so an asset that cannot be carried is a named warning in the
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
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
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
            // The check is the VFS answer whenever the view resolved: a later carry throwing must not downgrade a good
            // result. Only when the view never resolved does it fall back to the degraded loose-only check.
            bool sourceHadSeq = seqGate ?? File.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, srcSeqRel));

            // Refresh the start-game-enabled-quest .seq from the renumbered plugin when the source shipped one. A
            // renumber shifts every SGE quest's master-relative on-disk FormID, so a shipped .seq is now stale and
            // its quests would silently never start. Refresh only: if the source shipped no .seq, none is invented,
            // and RegenerateSeq returns a named advisory. The regeneration reads the new plugin and needs no
            // resolver; only the check above consults the view. Best-effort and reported: it never throws and never
            // fails the compact, and the outer try is belt and braces.
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, sourceHadSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if '{name}' has start-game-enabled quests, run {ToolNames.WriteSeq} on the compacted plugin." });
            }

            // Audit markers: stamp the editedInPlace breadcrumb into the meta.ini of every file rewritten in place —
            // the target and each successfully repointed external — matching the traceability the in-place edit lane
            // gives. The consent model deliberately stays compact's own per-call confirm rather than the persistent
            // acknowledgement the edit lane uses: a compaction can rewrite a broad surface, so each call re-confirms
            // with its exact overwrite list rather than letting a stale field-edit acknowledgement authorize a full
            // renumber. Markers are best-effort; a miss never fails the done write and is surfaced in Note.
            var markerNotes = new List<string>();
            if (offOrderNote is not null) markerNotes.Add(offOrderNote);
            // The new-file lane produces the SAME de-localized plugin the in-place lane is refused for; only where it
            // lands differs, so a caller who never meets that refusal still needs to be told. It states its own
            // behaviour only and does not claim the strings resolved, because when they resolve nowhere this same
            // path writes blanks and the sentence must stay true there too. It names no count of surviving languages:
            // what the source shipped is a fact about the source, but which of them survived into the plugin cannot
            // be read back out of a de-localized output.
            // Gated on the SHAPE, not on srcLocalized. That boolean is deliberately fail-closed for the refusal
            // above, and fail-closed is the wrong answer for a note, where the honest response to "houseCARL never
            // read the file" is to say nothing: a plain non-localized plugin briefly locked during the assessment
            // would otherwise be told its text lives in .STRINGS files that do not exist. ConfirmedLocalized asks the
            // narrower question — was the flag actually read and set.
            if (!inPlace && LocalizedStrings.ConfirmedLocalized(srcShape.Shape))
                markerNotes.Add(
                    $"'{name}' is flagged LOCALIZED — its text lives in separate .STRINGS files rather than in the "
                    + "plugin"
                    + (srcShape.Languages.Count > 0 ? " (" + string.Join(", ", srcShape.Languages) + ")" : "")
                    + ". The compacted plugin houseCARL wrote is NOT localized: it carries whatever this read of the "
                    + "source produced, written into the plugin itself, with no .STRINGS files of its own — so the "
                    + "source's .STRINGS files do not describe it, and any language it shipped that this read did not "
                    + "resolve is not in the output. Read the output before you enable it in place of the original.");
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
                markerNotes.Count > 0 ? string.Join(" ", markerNotes) : null, assetRename, id.ExternalOverriders, voiceRename, seqRegen,
                id.UnscannablePlugins);
        }
    }

    /// <summary>A blocked referencer list, split into the two classes it holds.
    /// <see cref="RemapEngine.LocalizedAmong"/> fails closed on a referencer it could not open, so its hits are a mix
    /// of "flagged LOCALIZED" and "could not be read". Both block the repoint, but they are not the same problem and
    /// do not have the same fix, so rendering them as one list would report the unreadable file as localized.</summary>
    static (IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Localized,
            IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Unread)
        SplitBlockedReferencers(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
        => (blocked.Where(b => LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList(),
            blocked.Where(b => !LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList());

    /// <summary>"2 flagged LOCALIZED (A.esp, B.esp), and 1 houseCARL could not read (C.esp)" — counts and names per
    /// class, and a class with no hits contributes nothing.</summary>
    internal static string BlockedReferencerCensus(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0) parts.Add($"{localized.Count} flagged LOCALIZED ({NameList(localized)})");
        if (unread.Count > 0) parts.Add($"{unread.Count} houseCARL could not read ({NameList(unread)})");
        return string.Join(", and ", parts);

        static string NameList(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> l)
            => string.Join(", ", l.Take(25).Select(x => x.Plugin)) + (l.Count > 25 ? $", … (+{l.Count - 25} more)" : "");
    }

    /// <summary>An attributed reason for the first of EACH class, never only the first hit overall, which would leave
    /// a whole class unmentioned and send the modder looking for .STRINGS files instead of for the file they cannot
    /// open. The lead-in differs because the two facts differ.</summary>
    internal static string BlockedReferencerReasons(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0)
        {
            parts.Add($"Where {localized[0].Plugin}'s text is: {localized[0].Why}");
            if (localized.Count > 1)
                parts.Add($"(The other {localized.Count - 1} localized referencer(s) are reported the same way if you compact them.)");
        }
        if (unread.Count > 0)
        {
            parts.Add($"Why {unread[0].Plugin} is blocked: {unread[0].Why}");
            if (unread.Count > 1)
                parts.Add($"(The other {unread.Count - 1} unreadable referencer(s) are the same.)");
        }
        return string.Join(" ", parts);
    }

    /// <summary>The refusal for a plugin that IS flagged localized and whose <c>.STRINGS</c> houseCARL can find
    /// nowhere — the one shape where the read itself is empty, so a lane that copies the read into a new file writes
    /// blanks. Shared by compact and merge, one wording: both bake the same read into a plugin the caller keeps, and
    /// neither may do it silently.
    ///
    /// <para>It says houseCARL cannot FIND the tables, never that the plugin has none: MO2's VFS merges mod folders
    /// at runtime, so a plugin's strings can sit in an archive in another mod folder that no path walked here can
    /// see.</para>
    ///
    /// <para>Three shapes arrive here and the words differ per shape — what the read WILL be, and what to do about it.
    /// Nothing found anywhere is fixed by putting the tables somewhere houseCARL can see; a folder that is there and
    /// would not list — the plugin's <c>Strings\</c> folder, or the mod folder itself — is fixed by freeing THAT
    /// folder, and telling its caller to place tables in it describes a folder they already have.</para></summary>
    /// <param name="verb">The operation, as the report names it — "compact", "merge".</param>
    internal static string UnresolvableStringsRefusal(string name, LocalizedAssessment a, string verb)
    {
        // WHERE the text is comes from the one renderer that already gets it right for both shapes: it names what
        // the Strings folder beside the plugin actually holds rather than claiming it is empty, and it drops the
        // game-Data clause when there was no Data folder to search. Hand-rolling it here asserted both.
        //
        // What the READ will be is per shape, and neither claim may be made for the other: nothing resolves a
        // plugin whose text is nowhere, so its values are empty; a folder that could not be listed was never read,
        // so what comes back from it is unknown.
        var unlistable = a.Shape is LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable;
        var consequence = unlistable
            ? "houseCARL cannot tell what its text reads as, or an empty value from a real one"
            : "every name, description and message it carries reads back EMPTY";

        // And so is the REMEDY. "Put the tables where houseCARL can see them" is the answer when nothing was found
        // anywhere; told to a plugin whose folder is already sitting there unreadable, it sends the caller to fill a
        // folder nothing could open. Those shapes need the folder freed, not populated — and the sentence names WHICH
        // folder, because the two are different places on disk and only one of them is the one to fix.
        var folder = a.Shape == LocalizedShape.StringsFolderUnreadable
            ? $"the Strings folder beside '{name}'"
            : $"the folder '{name}' sits in";
        var remedy = unlistable
            ? $"Let houseCARL read {folder} — close whatever is holding it open, or fix its permissions — and run "
              + "this again."
            : "Put this plugin's .STRINGS where houseCARL can see them — enable the mod that provides them, or place "
              + "them in a Strings folder beside the plugin — and run this again.";
        return $"refused — houseCARL did not {verb} '{name}'. "
             + LocalizedTargetUnsupportedException.WhereTheTextIs(a) + " "
             + $"So {consequence}, and a {verb} writes whatever this read produced into a NEW plugin you keep, with "
             + "nothing left in it to tell that text from a plugin that never had any. "
             + remedy + " Nothing was written.";
    }

    /// <summary>The in-place compaction's refusal, rendered per shape. The refusal decision is one fail-closed
    /// boolean, but its words cannot be: the localized arm's clauses are all about a translated plugin's
    /// <c>.STRINGS</c> files and end on the new-file lane, and told to a source houseCARL could not open that would
    /// describe tables nobody established exist and point at a lane that reads the same file and fails the same
    /// way.</summary>
    static string CompactInPlaceRefusal(string name, LocalizedAssessment a)
    {
        var head = $"houseCARL did not compact '{name}' in place — the file is unchanged and nothing was staged. "
                 + LocalizedTargetUnsupportedException.ShapeClause(a) + " ";
        return a.Shape switch
        {
            // Never opened: no claim about tables, and no lane to switch to, because the new-file lane reads the
            // same file and fails the same way.
            LocalizedShape.Unreadable =>
                head + "houseCARL does not rewrite a destination it cannot classify. Compacting into a NEW plugin is "
                     + "not the lane to switch to either — it reads the same file and fails the same way. "
                     + LocalizedTargetUnsupportedException.RemedyUnreadable,

            LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
                or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly
                or LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable
                or LocalizedShape.Nowhere =>
                head + "A compaction does not re-serialize your plugin, it builds a NEW one and writes that over the "
                     + "original, and houseCARL will not replace a translated plugin's .STRINGS files on your own copy: "
                     + "it cannot swap the plugin and its tables as one operation, and the file the game loads would "
                     + "stop being the translated plugin you have, with no backup and nothing to undo it. "
                     + $"Re-run without in_place to compact '{name}' into a NEW plugin instead: the same renumber, left "
                     + "in its own mod folder for you to check and enable yourself. That output is NOT localized — it "
                     + "carries the text that resolved when houseCARL read the source, written into the plugin itself, "
                     + "and the source's .STRINGS files do not describe it. Read it before you swap it in.",

            // NotLocalized cannot reach here — the caller's check excludes it — and a new shape has no wording, so
            // this arm says only what is certain rather than borrowing either branch above.
            _ => head + "houseCARL will not compact this plugin in place.",
        };
    }

    /// <summary>Merge one or more active plugins into one new plugin. A merge is a records operation: the donors'
    /// records combine into a fresh plugin under a new name, with a collision-only renumber — the first donor in load
    /// order keeps its object IDs, cross-donor conflicts on the same record resolve to the load-order winner and are
    /// reported, and a losing donor's un-relisted nested children graft into the winner. The donors are never
    /// touched: new-file lane only, no consent gate. The user reviews the output, enables its folder, and deactivates
    /// the donor PLUGINS in MO2 while leaving the donor mod FOLDERS enabled, because the merged records still
    /// reference the donors' path-keyed assets, which only those folders serve; the carries cover only the
    /// FormID-keyed facegen, voice and .seq. External referencers and overriders of donor records are warned about
    /// and named rather than refused, because nothing breaks at write time — the donors stay active until the user
    /// swaps — and the remedy is to include the patch in the merge set or repoint it before disabling the donors.
    /// The FormID-keyed assets follow per donor: every donor NPC's facegen and every voiced line move to the new
    /// plugin-name folders, since the plugin name is part of those paths, and a <c>.seq</c> is refreshed when any
    /// donor shipped one. With a single donor there is nothing to combine and the operation IS a rename: the same
    /// records under a new plugin identity, keeping every object id already inside the writable range, though an id
    /// below the write floor renumbers exactly as it does for the first donor of any merge and the per-donor line
    /// reports it. A rename's side effects are reported rather than refused: the output lands in a new mod folder
    /// beside the donor's, the swap instruction applies unchanged, and the existing-saves warning covers the break a
    /// changed plugin name causes.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? outputName, string? patchName = null)
    {
        // ---- argument shape; every refusal names the fix ----
        var donorsRaw = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // One donor is legitimate and IS the rename: the remap moves every donor key to the output ModKey whether or
        // not anything collided, and the facegen/voice carry and .seq refresh are per-donor because the plugin name
        // is a folder segment of those paths. So the single-donor path is the same walk with an empty collision set;
        // below-floor ids still renumber, so "nothing can collide" is not "every id is kept". The donor list stays a
        // SET, so duplicate names collapse here exactly as they do for many donors.
        if (donorsRaw.Count == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "merge needs at least ONE donor plugin — pass plugins=[\"A.esp\"] to move one plugin's records to a new " +
                "name (a rename), or plugins=[\"A.esp\", \"B.esp\", …] to combine several.");
        var outName = (outputName ?? "").Trim();
        if (outName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "output is required — name the NEW merged plugin file to create (e.g. 'MyMerge.esp'). It must not already exist in your load order.");
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"'{outName}' is not a valid plugin filename ({ex.Message})."); }
        if (outKey.Type == ModType.Light)
            return WritePatchBuilder.MergeOutcome.Fail(
                // The reason is what the merge does NOT do, and only that. Neither "the donors' ids stay in the full
                // range" nor "it keeps each donor's object ids where they already are" is true on every path: an
                // already-light donor's ids are all inside the window by definition, and BuildMergeRemap renumbers
                // collisions and below-floor ids from 0x800 up — a count the report prints.
                $"refused — '{outName}' has the .esl extension, which the game engine force-treats as a LIGHT master regardless " +
                "of the header flag, but a merge never constrains object ids to the light window: it renumbers only what it must " +
                "(cross-donor collisions, and ids below the write floor), so an id above 0xFFF would be misread in game. Merge to " +
                "a '.esp' instead: if every donor was light and every merged id landed in the window, the output is written LIGHT " +
                "already; otherwise the report says so, and " + ToolNames.CompactPlugin + " on it renumbers every id into the light " +
                "window (the tools compose). Nothing was written.");
        if (donorsRaw.Any(d => string.Equals(d, outName, StringComparison.OrdinalIgnoreCase)))
            return WritePatchBuilder.MergeOutcome.Fail($"the output '{outName}' cannot also be a donor — name a NEW plugin file.");

        lock (_writeGate)                                                 // one write at a time
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.MergeOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");
            if (view.ContainsPlugin(outName))
                return WritePatchBuilder.MergeOutcome.Fail(
                    $"'{outName}' is already an active plugin in your load order — the merge output must be a NEW plugin name " +
                    "(merging over an existing plugin would shadow it in MO2).");

            // ---- validate and load-order-sort the donors: merge semantics are load-order semantics, so sort rather
            //      than trusting argument order. One name-to-position index serves this sort and the master sort. ----
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < resolver.PluginNames.Count; i++) orderIndex[resolver.PluginNames[i]] = i;
            var donorInfos = new List<(string Name, string Path, ModKey Key, int Order)>();
            foreach (var d in donorsRaw)
            {
                if (!view.ContainsPlugin(d))
                {
                    // Once a cause is stated it carries its own remedy. An unconditional "Enable it in MO2 first
                    // (pass the exact filename)" both conflates the vocabulary — a MOD is enabled, a PLUGIN is
                    // activated — and asks for a filename that has already resolved to a real installed plugin.
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
                if (!orderIndex.TryGetValue(d, out var order))            // unreachable after ContainsPlugin (same source table) — refuse rather than mis-sort
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' has no load-order position (index inconsistency, Q3). Nothing was written.");
                donorInfos.Add((d, p, dk, order));
            }
            donorInfos.Sort((a, b) => a.Order.CompareTo(b.Order));
            var donorNames = donorInfos.Select(d => d.Name).ToList();

            // ---- the donors' strings, read once and used twice. A donor whose .STRINGS resolve NOWHERE reads every
            //      value EMPTY, and the merge would copy those blanks into M — refused here, before a rider folder
            //      exists and before anything is written. A donor that IS localized (and resolves) earns the
            //      de-localization note the report carries: M is a bare mod, so the text comes out inline and the
            //      donor's .STRINGS stop describing it. ----
            var localizedDonors = new List<string>();
            foreach (var (dName, dPath, _, _) in donorInfos)
            {
                var shape = LocalizedStrings.Assess(dPath, view.DataDir);
                if (LocalizedStrings.ResolvesNowhere(shape.Shape))
                    return WritePatchBuilder.MergeOutcome.Fail(UnresolvableStringsRefusal(dName, shape, "merge"));
                // ConfirmedLocalized, not "anything but NotLocalized": the note ASSERTS where a donor's text lives,
                // and a donor houseCARL could not read gives it nothing to assert. That donor fails loudly at the
                // open in MergeBuild instead.
                if (LocalizedStrings.ConfirmedLocalized(shape.Shape)) localizedDonors.Add(dName);
            }

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
            // readDeclaredMasters: a merge RENAMES the donors' records into a new plugin, so a dependent that only
            // lists a donor as a master loses it at the swap. Sound here because BuildMergeRemap enters every
            // originating key of every donor into the dict, so a referencer is always a declarer too and the
            // declarer-only filter cannot hide one.
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet, readDeclaredMasters: true);

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

            // ---- output folder: a fresh houseCARL mod folder, since a merge is always a new file ----
            RiderFolder rf;
            try { rf = ResolvePatchModFolder(patchName, null,
                Path.GetFileNameWithoutExtension(outName) + (donorInfos.Count == 1 ? " renamed" : " merged"), naming: null); }
            catch (InvalidOperationException ex) { return WritePatchBuilder.MergeOutcome.Fail(ex.Message); }
            WriteOwnerMeta(rf.ModFolder, outName);
            var outPath = Path.Combine(rf.OutputDir, outName);

            // ---- build and write the merged plugin ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath, view.DataDir);
            if (!build.Success)
            {
                if (rf.CreatedFresh) RemoveOrNameRiderResidue(rf);        // a refused build leaves no orphan folder
                return WritePatchBuilder.MergeOutcome.Fail(build.Error!);
            }

            // ---- FormID-keyed assets follow the renumber, per donor: a merge renames the plugin, and the plugin
            //      NAME is a segment of the facegen and voice paths, so the carry covers every donor NPC and voiced
            //      line rather than just the id collisions. One captured asset view feeds the carries and the SEQ
            //      check. Best-effort and reported: the records are written, so an asset miss is a named warning. ----
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS answer, set the moment the view resolves
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
                var assetView = assetResolver.Capture();
                seqGate = false;                                          // the view resolved — the answer below is authoritative
                foreach (var (dName, _, _, _) in donorInfos)              // did any donor ship a .seq? VFS-aware, per donor
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
            // Only when the view never resolved does this fall back to a loose per-donor-folder check, so an
            // asset-layer fault cannot silently downgrade a donor-shipped .seq to "the donors shipped none" and skip
            // the refresh with a factually wrong advisory.
            bool anyDonorSeq = seqGate ?? donorInfos.Any(d =>
                File.Exists(Path.Combine(Path.GetDirectoryName(d.Path)!, "SEQ", Path.GetFileNameWithoutExtension(d.Name) + ".seq")));

            // ---- SEQ, refresh-only, off the merged plugin: rebuilt when any donor shipped a .seq, because all their
            //      SGE quests now live in the output, whose .seq must list the new on-disk FormIDs. Donors with SGE
            //      quests but no shipped .seq get the same named advisory a compaction gives. ----
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, anyDonorSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if the donors have start-game-enabled quests, run {ToolNames.WriteSeq} on '{outName}'." });
            }

            // Surface the one behaviour change the any-donor rule can introduce: the rebuild lists EVERY SGE quest in
            // the output, so a quest from a donor that shipped no .seq — and so was not auto-starting — gains an entry.
            string? note = seqRegen.Written
                ? "the regenerated .seq lists EVERY start-game-enabled quest in the output — including quests no donor's own .seq " +
                  "listed, whether because that donor shipped none or because its .seq was trimmed. Such quests were NOT " +
                  "auto-starting before; they will now."
                : null;

            return new WritePatchBuilder.MergeOutcome(
                true, null, outPath, outName, donorNames, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                plan.Donors, build.Conflicts, id.ExternalPlugins, id.ExternalOverriders,
                id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples, build.Bytes, note,
                assetRename, voiceRename, seqRegen, build.LightDonors, build.HeaderMetaDonors, build.MasterDonors,
                id.UnscannablePlugins, localizedDonors, id.MasterDeclarers, build.LightCarried, build.OriginatingRecords);
        }
    }

    /// <summary>The composed standalone-NPC-copy verb. Deep-copies a donor NPC's appearance-record subtree into a
    /// houseCARL patch under new FormKeys, via duplicate-and-remap so every field carries by construction (headpart
    /// morph refs, texture lighting, tints); preserves headpart EditorIDs, which are the facegeom block-name
    /// identity; renames the facegen pair to the new key's path; and carries the donor-only textures and meshes the
    /// records and geometry reference. Two target modes: <paramref name="targetFormid"/> dresses an existing NPC by
    /// copying the appearance fields onto an override, while <paramref name="newEditorid"/> mints a full standalone
    /// clone, duplicating the donor NPC and stripping every remaining donor-internal non-appearance link, each
    /// reported by name so the clone is donor-free loudly. The donor may be active, read via the load-order winner,
    /// or sit in a plugin file located across enabled and disabled mod folders, stamped out-of-load-order in the
    /// outcome. The donor is never touched; the output is a fresh patch folder or an into= extend.</summary>
    public NpcCopyOutcome CopyNpcAppearance(
        string sourceFormid, string? sourcePlugin, string? sourceMod,
        string? targetFormid, string? newEditorid, string? newName,
        string? patchName, string? into)
    {
        // ---- argument shape: exactly one target mode ----
        // ONE door for both tokens: two doors would each capture their own build, and a re-sort between them
        // would read the donor off one order and the target off another.
        var door = OpenWriteFormIdDoor();
        FormKey donorFk;
        try { donorFk = door.Parse(sourceFormid); }
        catch (Exception ex) { return NpcCopyOutcome.Fail(FormIdDoor.Sentence(ex, "", $"bad source formid '{sourceFormid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '000D62:Vivace.esp'.")); }

        bool apply = !string.IsNullOrWhiteSpace(targetFormid);
        bool clone = !string.IsNullOrWhiteSpace(newEditorid);
        if (apply == clone)
            return NpcCopyOutcome.Fail(
                "pass EXACTLY ONE target: target_formid= (copy the donor's appearance onto an EXISTING NPC) or " +
                "new_editorid= (mint a full standalone CLONE of the donor as a new NPC).");
        FormKey targetFk = default;
        if (apply)
        {
            try { targetFk = door.Parse(targetFormid); }
            catch (Exception ex) { return NpcCopyOutcome.Fail(FormIdDoor.Sentence(ex, "", $"bad target formid '{targetFormid}': {ex.Message}.")); }
        }

        lock (_writeGate)
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return NpcCopyOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            using var session = resolver.OpenSession();

            // ---- read the donor NPC: the active lane via the load-order winner, or the file lane over any plugin on
            //      disk, disabled included, with results stamped out-of-load-order. ----
            INpcGetter donorNpc;
            NpcAppearanceCopy.DonorFetch fetch;
            // Donor-bound ModKeys: the plugins being standalone-ized away from. A base-game or implicit master is
            // never donor-bound — copying a vanilla-defined NPC's look must not classify vanilla races or headparts
            // as donor-internal, which would produce a nonsense custom-race refusal and wholesale-internalize vanilla
            // records. With an empty set the copy is an override-style transplant, said plainly.
            var baseMasters = Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters;
            var donorMods = new HashSet<ModKey>();
            if (!baseMasters.Contains(donorFk.ModKey)) donorMods.Add(donorFk.ModKey);
            string donorReadFrom; bool outOfLoadOrder;
            ISkyrimModGetter? donorOverlay = null;    // file lane only — disposed in finally
            ISkyrimModGetter? widenOverlay = null;    // file lane, auto-widened defining plugin — disposed in finally
            string? donorFilePath = null;             // file lane: the located file; active lane: the winner's path (for the donor-disk asset fallback)
            string? widenFilePath = null;             // file lane: the auto-widened defining plugin's file (self-lock check)
            string widenNote = "";                    // file lane: what was auto-read, or why it could not be — appended to a fetch-miss closure refusal only, never a failure of its own
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
                    // State the cause the locate contract computed: a flat "DISABLED" would be wrong for an unticked
                    // or shadowed donor whose mod is perfectly enabled.
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
                    // Lazy per-type link cache, so there is no eager whole-file parse, plus per-resolve fault
                    // isolation: one unparseable record elsewhere in the file must not abort the verb.
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

                    // Auto-widen: the named file only OVERRIDES the donor, while the records a standalone copy must
                    // internalize live in the DEFINING plugin, which an override patch points at but does not
                    // contain. Locate that plugin on disk too and let the closure fall through to it, with the named
                    // file still first because its override IS the look being asked for. Reported on success, and
                    // carried as a note onto a fetch-miss closure refusal either way — never a failure of its own,
                    // because a donor whose closure never needs the defining plugin must keep working.
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

                // ---- the appearance closure: a generic link walk, with loud refusals for a custom race, a runaway walk, or an unreadable record ----
                var closure = NpcAppearanceCopy.CollectAppearanceClosure(donorNpc, donorMods, fetch, active);
                if (!closure.Success) return NpcCopyOutcome.Fail(closure.Error! + (closure.FetchMiss ? widenNote : ""));

                // ---- output patch: a fresh folder or an into= extend, through the record lane's resolver and ownership gate ----
                string outPath; bool extend, created;
                var defaultStem = clone ? newEditorid!.Trim() : "houseCARL_NpcCopy";
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? defaultStem : null), into, out extend, out created,
                                                  freshPatch: FreshPatchRemedy.CreatedByOmittingInto); }
                catch (Exception ex) { return NpcCopyOutcome.Fail(ex.Message); }
                var patchFileName = Path.GetFileName(outPath);
                var patchModKey = ModKey.FromFileName(patchFileName);

                // ---- apply lane: resolve the active target body up front. An in-patch target, whose formid names
                //      the patch itself, is resolved inside the build off the opened patch mod. ----
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

                // ---- neither donor-side file may be the output patch: the overlays stay memory-mapped through the
                //      serialize, which is the self-lock the write path protects against. ----
                foreach (var (lockPath, what) in new (string?, string)[]
                         { (donorFilePath, "donor plugin file"), (widenFilePath, "auto-widened defining plugin") })
                    if (lockPath is not null && PathEquals(lockPath, outPath))
                    {
                        if (created) RemoveFolderCreatedThisCall(outPath);
                        return NpcCopyOutcome.Fail(
                            $"the {what} IS the output patch — reading and rewriting the same file in one call would " +
                            "deadlock on its own open handle. Copy into a DIFFERENT patch (omit into=, or name another).");
                    }

                // ---- build and serialize in the core: duplicate, remap links, pick the mode lane, write ----
                var outcome = NpcAppearanceCopy.BuildAndWrite(
                    donorNpc, donorMods, closure,
                    clone, newEditorid, newName,
                    targetFk, targetActiveBody,
                    active,
                    outPath, extend,
                    pf => { session.ReleaseOverlay(pf); return session.AllMastersExcept(pf); },   // active-patch self-lock fix
                    donorReadFrom, outOfLoadOrder,
                    // The same renderer the sibling write lanes use, so the baseline refusal substitutes here too.
                    ex => WritePatchBuilder.SerializeFailure("serialize failed — ", ex, session, " Nothing usable was written."));
                if (!outcome.Success)
                {
                    if (created) RemoveFolderCreatedThisCall(outPath);   // a refused copy leaves no orphan folder
                    return outcome;
                }

                // ---- asset carry: the facegen rename plus the donor-only textures and meshes. Best-effort and
                //      reported. Paths were harvested from the in-patch duplicates before the serialize, never from
                //      the donor overlay, which may be released by now. Donor-disk direct lanes exist for both
                //      donor-side files — the named file's folder and the auto-widened defining plugin's, since a
                //      widened copy's facegen lives in the defining mod's folder — but only under an MO2 mod folder,
                //      never the game Data folder, where every vanilla BSA would misclassify as the donor's. ----
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
                        if (sideDir is not null && donorDisks.Any(d => PathEquals(sideDir, d.Folder))) continue;   // named and defining in one folder means one scan
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
                        new[] { $"asset carry skipped — the asset layer could not be built ({ex.Message}); carry the facegen pair with {ToolNames.Place} and verify in-game." }, false, false);
                }
                return outcome with { Assets = assets };
            }
            finally { (donorOverlay as IDisposable)?.Dispose(); (widenOverlay as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Create brand-new records in one patch — the net-new authoring capability, the sibling of
    /// <see cref="ApplyEdits"/>, and the one-shot route for a nested unit (a dialogue topic and its lines, a cell and
    /// its placed refs) where a child's <c>parent</c> names a same-call sibling by editorid. Each spec resolves its
    /// record type (a catalog name or 4-char signature) to one concrete catalog name, refusing an unknown or ambiguous
    /// one; maps its field operations to core write requests rooted at that type, since a create op takes no formid
    /// and sets fields on the new record; a flat top-level record needs no parent, a nested child passes one — an
    /// existing parent's FormKey, or a record created in a prior into= call — plus a collection when the parent holds
    /// more than one fitting child list. Then it resolves the folder-per-patch output, fresh or <paramref name="into"/>
    /// an existing houseCARL-owned patch, and drives <see cref="WritePatchBuilder.CreateRecords"/>. Each new record's
    /// FormID is auto-allocated at 0x800 and above and reported, and originals are never touched. All-or-nothing: any
    /// malformed spec refuses the whole call with per-record reasons, and the core likewise refuses the whole batch on
    /// any creatability or parent problem. One serialize for the lot.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecordsBatch(IReadOnlyList<CreateOp> records, string? patchName, string? into, bool fullReadback = false,
        string? target = null, bool inPlace = false, bool acknowledge = false, IReadOnlyList<string?>? origins = null,
        CreateOpNaming? naming = null)
    {
        if (records is null || records.Count == 0)
            return WritePatchBuilder.CreateOutcome.Fail("no records to create supplied — pass one or more {record_type, editorid, operations?, parent?, collection?, grid?} specs.");

        var problems = new List<string>();
        var specs = new List<WritePatchBuilder.CreateSpec>(records.Count);
        // One write door for the whole call, as the sibling verbs open: parent= is the only token here that can be
        // a FormID, and it is a write's, so a runtime one that is not a sibling editorid is refused with the plugin
        // form to use.
        var door = OpenWriteFormIdDoor();
        // The editorids this call declares: a parent naming one of them is a sibling reference, not a FormID, even
        // when it happens to read as eight hex characters ('DEADBEEF').
        var siblings = new HashSet<string>(
            records.Where(x => !string.IsNullOrWhiteSpace(x.Editorid)).Select(x => x.Editorid!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        for (int r = 0; r < records.Count; r++)
        {
            var rec = records[r];
            // origins[r] is the caller's own spelling for this spec, carried parallel to the list for the same
            // reason ApplyEdits carries opOrigins: a refusal must never name an index shape the caller did not write.
            var where = origins is not null && r < origins.Count && origins[r] is { } o ? o : $"record[{r}]";
            var spec = BuildCreateSpec(door, rec.RecordType, rec.Editorid, rec.Operations ?? Array.Empty<BulkOp>(), rec.Parent, rec.Collection, rec.Grid, where, problems, naming ?? CreateOpNaming.Legacy, siblings);
            if (spec is not null) specs.Add(spec);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) across {records.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(specs, patchName, into, fullReadback, target, inPlace, acknowledge);
    }

    /// <summary>Build one core <see cref="WritePatchBuilder.CreateSpec"/> from wire parts, shared by the single
    /// create and the batch: resolve <paramref name="recordType"/> to one concrete catalog name, require an editorid,
    /// map each field op to a core <see cref="WriteRequest"/> rooted at that type, and carry
    /// <paramref name="parent"/> and <paramref name="collection"/> through for a nested child — null means a flat
    /// top-level record. Every problem, tagged with the optional <paramref name="where"/> label, is appended to
    /// <paramref name="problems"/>, and this returns null iff this record contributed any.</summary>
    WritePatchBuilder.CreateSpec? BuildCreateSpec(FormIdDoor door, string? recordType, string? editorid, IReadOnlyList<BulkOp> operations,
        string? parent, string? collection, string? grid, string? where, List<string> problems, CreateOpNaming naming,
        IReadOnlySet<string>? siblingEditorids = null)
    {
        var prefix = where is null ? "" : where + ": ";
        int before = problems.Count;

        // parent= takes an EditorID as well as a FormID, so only a runtime FormID is judged here; everything else
        // is left to the core's own parse. An eight-hex EditorID this call declares is a sibling reference and is
        // never read as a FormID, so the runtime refusal runs only once that lookup has missed.
        bool parentIsSibling = parent is not null && siblingEditorids is not null && siblingEditorids.Contains(parent.Trim());
        if (!parentIsSibling && door.RuntimeRefusal(parent) is { } parentRefusal) problems.Add($"{prefix}parent: {parentRefusal}");

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

        // Map each field op to a core WriteRequest rooted at the create type, only once the type resolved, and
        // collect every malformed op.
        var edits = new List<WriteRequest>(operations.Count);
        if (catalogName is not null)
            for (int i = 0; i < operations.Count; i++)
            {
                var req = MapCreateEdit(operations[i], i, catalogName, naming, out var err);
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

    /// <summary>Resolve the folder-per-patch output, fresh or <paramref name="into"/> an existing houseCARL-owned
    /// patch, then drive the core multi-record create and serialize under the write gate, one write at a time. A
    /// refused create that just made the output folder leaves no orphan. Shared by the single and batch
    /// create.</summary>
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback,
        string? target = null, bool inPlace = false, bool acknowledge = false)
    {
        // In-place is the explicit, named-file opt-in: create into an existing plugin, including one houseCARL did
        // not author, instead of writing a new patch. The contract is validated up front — it requires target=, is
        // mutually exclusive with into=, and target= without in_place is a no-op the caller likely did not mean, so
        // it is named rather than silently ignored. Mirrors ApplyEdits' in-place contract exactly.
        if (inPlace && string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true requires target=<plugin filename> — name the existing plugin to create into in place. (Omit in_place to write a new patch instead — the default, originals untouched.)");
        if (inPlace && !string.IsNullOrWhiteSpace(into))
            return WritePatchBuilder.CreateOutcome.Fail(
                "in_place=true and into= are mutually exclusive: into= EXTENDS a houseCARL patch, while in_place creates into an existing plugin in place. Use one lane or the other.");
        if (!inPlace && !string.IsNullOrWhiteSpace(target))
            return WritePatchBuilder.CreateOutcome.Fail(
                "target= is only meaningful with in_place=true (it names the plugin to create into in place). For the default patch lane omit target=; use into= to extend an existing houseCARL patch.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;
            var rulebook = Rulebook;

            if (inPlace)
                return CommitCreateInPlace(resolver, rulebook, specs, target!.Trim(), acknowledge);

            string outPath; bool extend, created;
            try { outPath = ResolveOutputPath(patchName, into, out extend, out created, freshPatch: FreshPatchRemedy.NamedByPatchParam); }
            catch (Exception ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

            var outcome = WritePatchBuilder.CreateRecords(resolver, rulebook, specs, outPath, extend, fullReadback);
            if (!outcome.Success && created) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            // Post-write verify steps, leaving the create path itself untouched: voice (.fuz/.lip) coverage, the
            // result-script binding, then the cell structural-shell report. Each is a no-op unless the call created
            // the relevant record kind, and none can fail the create, which already succeeded.
            return outcome.Success ? EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver))) : outcome;
        }
    }

    /// <summary>The in-place branch of <see cref="CommitCreate"/> — the create-side companion of
    /// <see cref="ApplyEditsInPlace"/>, reusing every in-place seam: the same foreign-target resolver, the same
    /// persistent first-touch consent handshake keyed off the resolved path, the same writable-parent pre-flight, and
    /// the same <c>editedInPlace=</c> marker rather than <c>generated=true</c>. It diverges in three ways: it drives
    /// <see cref="WritePatchBuilder.CreateRecordsInPlace"/>, which allocates into the target rather than editing an
    /// existing record; it returns a <see cref="WritePatchBuilder.CreateOutcome"/>; and because in-place create can
    /// author dialogue lines and cells under any parent, it runs the same post-write voice, result-script and
    /// cell-shell coverage checks the patch-lane create runs. The created-record verify is forced on, and
    /// <paramref name="acknowledge"/> waives the consent axis only. Runs under <c>_writeGate</c>, which the caller
    /// holds.</summary>
    WritePatchBuilder.CreateOutcome CommitCreateInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook, IReadOnlyList<WritePatchBuilder.CreateSpec> specs,
        string target, bool acknowledge)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin, which closes the coincidental-folder collision. Same resolver as the edit lane.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.")
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.CreateOutcome.Fail(locRefusal)
                with { Epoch = view.Epoch };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // lane because acknowledging a plugin once covers both editing and creating into it — the same "touch your
        // original" trade-off. The check gates entry here; the acknowledgement is recorded only once the create has
        // landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: this branch is reached only after the view above
            // resolved the target, and it is the most common in-place response shape.
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Epoch = view.Epoch };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why) with { Epoch = view.Epoch };

        // The write, with the created-record verify forced on.
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true);

        // On success, record the acknowledgement, then run the same post-write checks the patch lane runs, since the
        // service owns the live asset resolver and in-place create can author dialogue lines and cells under any
        // parent. Each is a no-op unless that record kind was created, and none can fail the write. Then stamp the
        // audit marker, best-effort: a marker miss never fails the done create.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "create");
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            var note = JoinNotes(ackNote, markerNote);
            return note is not null ? enriched with { Note = note } : enriched;
        }
        return outcome;
    }

    /// <summary>The on-disk voice (.fuz/.lip) presence check, run as a post-write step on a successful create, since
    /// the service owns the live <see cref="Assets"/> resolver and the core create path stays asset-free. Only fires
    /// when the call created at least one dialogue line: <see cref="VoiceCheck.Run"/> re-opens the written patch
    /// read-only, computes each created voiced line's expected path, and checks the VFS, with the report riding back
    /// on <see cref="WritePatchBuilder.CreateOutcome.Voice"/>. It never fails the create, which already succeeded: a
    /// check failure is surfaced on the report's CheckError, and even a thrown asset-layer build is caught here.
    /// Caller holds <see cref="_writeGate"/>, where the reentrant Assets getter is safe.</summary>
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

    /// <summary>The per-create result-script binding check, a post-write step on a successful create exactly like
    /// <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created at least one dialogue line:
    /// <see cref="DialogueScriptCheck.Run"/> re-opens the written patch read-only, validates each created INFO's VMAD
    /// result-script binding and checks its compiled `.pex` on disk, with the report riding back on
    /// <see cref="WritePatchBuilder.CreateOutcome.ScriptBinding"/>. It never fails the create; a check failure is
    /// surfaced on the report's CheckError. It needs no resolver, because the binding lives wholly on the INFO and
    /// the on-disk `.pex`.</summary>
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

    /// <summary>The cell structural-shell report, a post-write step on a successful create exactly like
    /// <see cref="EnrichWithVoiceCheck"/>. Only fires when the call created at least one cell:
    /// <see cref="CellShellCheck.Run"/> re-opens the written patch read-only, reads each created cell's
    /// interior/exterior kind, and lists the world content houseCARL does not author — lighting, terrain, water,
    /// navmesh — with the report riding back on <see cref="WritePatchBuilder.CreateOutcome.CellShell"/>. It never
    /// fails the create, since the cell IS written and this only says what the author must still provide; a check
    /// failure is surfaced on the report's CheckError. It needs no resolver or assets: the kind comes off the written
    /// cell's flag.</summary>
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

    /// <summary>The calling tool's vocabulary for a create op, threaded down so a refusal never names a spelling the
    /// caller cannot see — the same rule as <c>origins</c>, <c>sourceParam</c> and <c>offerModParam</c>.
    /// <paramref name="Element"/> is the ops-list member word; <paramref name="CopySubject"/> is how the copy refusal
    /// refers to what the caller asked for.</summary>
    public readonly record struct CreateOpNaming(string Element, string CopySubject)
    {
        /// <summary>The wording for the tools that spell the member <c>op</c> and expose <c>from_plugin</c>.</summary>
        public static readonly CreateOpNaming Legacy = new("op", "CopyFrom / from_plugin");
    }

    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for a create: RecordType is the create type
    /// rather than derived, and a create op carries no formid because it sets a field on the new record, whose id is
    /// auto-allocated — a stray formid is refused rather than silently ignored. Builds the composition
    /// <see cref="StructSpec"/> the same way <see cref="MapEdit"/> does, so a created record's nested lists compose
    /// identically.</summary>
    WriteRequest? MapCreateEdit(BulkOp op, int index, string recordType, CreateOpNaming naming, out string? error)
    {
        error = null;
        // The caller's own element word, threaded down for the same reason as `origins` one level up: a refusal must
        // name the handle the caller can act on, not one nobody wrote.
        var where = $"{naming.Element}[{index}]";
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
            // Named in the calling surface's vocabulary. This is reachable even from a tool that declares no
            // from_plugin member, because the strict reader gates undeclared members and `op` is declared, so a
            // CopyFrom verb arrives here and must not be answered with a member the caller cannot remove.
            error = $"{where}: {naming.CopySubject} copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
            return null;
        }

        return new WriteRequest
        {
            RecordType = recordType, Path = path, Verb = string.IsNullOrWhiteSpace(op.Verb) ? "Set" : op.Verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
        };
    }

    /// <summary>Map a wire op to a core <see cref="WritePatchBuilder.PatchEdit"/>: parse the FormID, split the dotted
    /// field path, and build the composition <see cref="StructSpec"/> when present. RecordType is deliberately not
    /// taken from the wire — the engine derives it from the resolved winner. Returns null and a named error on any
    /// malformed input.</summary>
    WritePatchBuilder.PatchEdit? MapEdit(FormIdDoor door, BulkOp op, int index, out string? error,
                                         string? fromRecord = null, string? origin = null)
    {
        error = null;
        // The caller's own spelling for this edit: inline ops are op[i], while zip-generated ops are named by the
        // pair and path they came from. A refusal pointing at an op index the caller never wrote sends anyone
        // fixing it to a line that does not exist.
        var where = origin ?? $"op[{index}]";
        if (string.IsNullOrWhiteSpace(op.Formid)) { error = $"{where}: formid is required."; return null; }
        FormKey fk;
        try { fk = door.Parse(op.Formid); }
        catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where}: ", $"{where}: bad formid '{op.Formid}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
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

        // The cross-record copy source. A named source record makes from_source optional, defaulting to that
        // record's load-order winner, resolved at pre-flight where the captured view lives; without one, the source
        // plugin is the only thing identifying a version to copy, so it stays required. A source equal to the target
        // is a no-op, refused by name rather than written.
        FormKey? fromKey = null;
        if (!string.IsNullOrWhiteSpace(fromRecord))
        {
            try { fromKey = door.Parse(fromRecord); }
            catch (Exception ex) { error = FormIdDoor.Sentence(ex, $"{where} ({op.Formid}): ", $"{where} ({op.Formid}): bad from '{fromRecord}' ({ex.Message}). Expected 'XXXXXX:Plugin.esp'."); return null; }
            if (fromKey == fk)
            { error = $"{where} ({op.Formid}): from names the SAME record as formid — copying a record's field onto itself is a no-op. Drop from=, and name the plugin whose version to copy in from_source=."; return null; }
        }

        var fromPlugin = MapFromPlugin(op, verb, $"{where} ({op.Formid})", spec, specs, fromKey is not null, out error);
        if (error is not null) return null;

        return new WritePatchBuilder.PatchEdit
        {
            Target = fk, Path = path, Verb = verb,
            Key = op.Key, Value = op.Value, Values = op.Values, Entries = op.Entries, Struct = spec, Structs = specs,
            FromPlugin = fromPlugin, FromTarget = fromKey,
        };
    }

    /// <summary>Validate and extract from_plugin for a CopyFrom op. It is required with, and only with, the CopyFrom
    /// verb, which copies the field from that plugin's version and so takes no value, values, entries, compose or
    /// composes. Both rules refuse loudly rather than silently ignoring. Returns null for a non-CopyFrom
    /// op.</summary>
    static string? MapFromPlugin(BulkOp op, string verb, string where, StructSpec? spec, IReadOnlyList<StructSpec>? specs,
        bool hasSourceRecord, out string? error)
    {
        error = null;
        if (!string.Equals(verb, "CopyFrom", StringComparison.Ordinal))   // match the engine's ordinal verb compare, so a mis-cased verb fails the same way everywhere
        {
            if (!string.IsNullOrWhiteSpace(op.FromPlugin))
                error = $"{where}: from_source is only valid with op=CopyFrom (got op={verb}).";
            return null;
        }
        // The "a copy carries no authored value" rule is independent of whether the pole was named, so it is checked
        // FIRST: below the from_plugin block, the cross-record shape returns early past it and an authored value is
        // silently discarded. Nothing downstream catches that — the rulebook short-circuits CopyFrom to its own
        // legality check, which never sees Value, and the apply takes the copy branch.
        if (op.Value is not null || op.Values is not null || op.Entries is not null || spec is not null || specs is not null)
        {
            error = $"{where}: CopyFrom copies the field from the source record's version — it takes no value/values/entries/compose/composes.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // A named source record identifies what to copy on its own, so the pole is optional and defaults to that
            // record's winner. Without one, the plugin is the only thing distinguishing a source version from the
            // target's own, so it is required or the op means nothing.
            if (hasSourceRecord) return null;
            error = $"{where}: CopyFrom requires from_source — the plugin whose version of this record to copy field_path from.";
            return null;
        }
        return op.FromPlugin.Trim();
    }

    /// <summary>Build a core composition <see cref="StructSpec"/> from the wire shape: flat coercible
    /// <c>fields</c>, positional <c>ctor_args</c>, and nested <c>sets</c>, each a path, verb and value applied to the
    /// built struct. The nested sets' RecordType carries the struct type as a label, since the validator roots them
    /// at the struct schema. A nested set may itself carry a <c>compose</c> — a recursive
    /// <see cref="StructSpec"/> selecting a polymorphic sub-arm — mapped here into the nested
    /// <see cref="WriteRequest.Struct"/> the core applies and validates end to end. Without that propagation a nested
    /// set could only set a coercible scalar, never a sub-arm. A malformed spec is a named error. It is
    /// <c>internal static</c> as a test seam and touches no instance state.</summary>
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

    /// <summary>Map a wire op's composes[] — many build-from-parts list elements — to core StructSpecs. Mutually
    /// exclusive with the singular compose: both set is refused rather than silently merged. Each element maps via
    /// the same <see cref="MapStruct"/> the singular compose uses, so a composes element can never be shaped
    /// differently from a compose element, and a bad element names itself. Returns null when no composes= is present;
    /// an explicitly empty composes=[] is a named caller mistake, not a silent no-op.</summary>
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
            // An empty composes=[] is the clear intent for a ReplaceAll — empty the modeled list, the twin of
            // ReplaceAll values=[] on a coercible list. For any other verb an empty batch is a caller mistake.
            if (!string.Equals(op.Verb, "ReplaceAll", StringComparison.Ordinal))
            {
                error = $"{where}: composes=[] is empty — supply one or more element specs (or compose= for one); an empty composes= is only meaningful with op=ReplaceAll, to CLEAR the list.";
                return null;
            }
            return new List<StructSpec>();   // ReplaceAll composes=[] clears the modeled list
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

    /// <summary>Resolve a patch's output path under the folder-per-patch model: each patch is its own MO2 mod folder,
    /// <c>&lt;ModsDir&gt;\houseCARL - &lt;name&gt;\&lt;name&gt;.esp</c>, so every houseCARL plugin is a first-class
    /// mod the user enables, orders and removes independently. A new patch always creates a fresh, marker-stamped
    /// folder, auto-suffixed so a prior reviewed patch is never clobbered; <paramref name="into"/> extends an
    /// existing houseCARL-owned patch. Originals-untouched is structural: houseCARL only ever writes a folder that is
    /// brand new or carries its own <c>meta.ini</c> marker, and refuses to write a folder it did not create even on a
    /// name collision. The caller's name is reduced to a bare stem with no directory parts, so it can never escape
    /// ModsDir. Runs under <see cref="_gate"/> like <see cref="ResolvePatchModFolder"/>, because the check-then-create
    /// of a unique stem is only race-free when every folder allocation is serialized on one gate.
    /// <paramref name="createdFolder"/> reports whether THIS call created the fresh folder, so a refused write can
    /// remove it again and "no patch written" leaves no orphan accreting suffixes on retry.
    /// <paramref name="freshPatch"/> and <paramref name="laneClause"/> pass through to the not-found refusal's
    /// remedy, so the calling operation states how its own fresh-write path works. Both default to claiming nothing,
    /// so a caller added later cannot inherit a sentence that is false for it.</summary>
    string ResolveOutputPath(string? patchName, string? into, out bool extend, out bool createdFolder, bool create = true,
                             FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? laneClause = null)
    {
        lock (_gate)
        {
            createdFolder = false;
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                extend = true;
                // The .esp write lane shares the extend resolver with the rider and asset lanes, so "extend my
                // renamed patch" behaves identically across records, scripts, BSAs and assets. needEsp:true because
                // the fast path only short-circuits a folder that actually holds <stem>.esp; the .esp to extend is
                // then picked inside the resolved folder — the <stem>.esp it holds, or, where the folder and plugin
                // names differ, the folder's single plugin, refusing if it holds none or several.
                var folder = ResolveOwnedPatchFolder(into, needEsp: true, freshPatch, laneClause);
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
            // A dry run (create:false) resolves the would-be path only — no folder, no meta.ini — so the disk stays
            // exactly as it was. The real write re-resolves and creates.
            if (create)
            {
                Directory.CreateDirectory(newFolder);
                createdFolder = true;
                WriteOwnerMeta(newFolder, plugin);
            }
            return Path.Combine(newFolder, plugin);
        }
    }

    /// <summary>A write refused after <see cref="ResolveOutputPath"/> created a fresh folder removes that folder
    /// again, so "no patch written" is true of the disk too and no orphan accretes suffixes on retry. Deletion is
    /// gated by a content check rather than trust: only a folder holding nothing beyond our own meta.ini and an empty
    /// <c>.housecarl-tmp</c> staging leftover is removed — anything else means the folder gained real content and
    /// stays. Best-effort: a cleanup failure never masks the write's own reported outcome.</summary>
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
    /// (what the cleanup operates on), and whether THIS call created the folder fresh, versus reusing an into= folder,
    /// which the user owns and cleanup never touches). For the .bsa/extract riders OutputDir == ModFolder; for the
    /// compile/decompile riders OutputDir is a subfolder (<c>Scripts\</c> / <c>Source\Scripts\</c>) under ModFolder.</summary>
    public readonly record struct RiderFolder(string OutputDir, string ModFolder, bool CreatedFresh);

    /// <summary>How ONE rider lane names the mod folder it creates — the calling tool's own statement, the way
    /// <see cref="FreshPatchRemedy"/> is for the record lanes. <paramref name="Param"/> is the parameter that tool
    /// actually declares for the folder's name, and <paramref name="Caveat"/> is any correction that parameter
    /// carries on it: on <c>housecarl_bsa_repack</c> a bare <c>patch=</c> binds to <c>archive_name</c>, so telling
    /// that caller to pass <c>patch=</c> would rename their archive and leave the folder defaulted. A lane whose
    /// <c>into=</c> can never be non-empty passes null, and keeps the weakest true remedy.</summary>
    public readonly record struct RiderNaming(string Param, string? Caveat = null);

    /// <summary>Resolve a houseCARL-owned mod folder under ModsDir for a non-.esp output — compiled scripts, a packed
    /// .bsa, extracted loose files — generalising the folder-per-patch model beyond the .esp write path. Either a
    /// fresh marker-stamped folder, named by <paramref name="defaultStem"/> when patchName is blank and auto-suffixed
    /// so a prior one is never clobbered, or <paramref name="into"/> an existing houseCARL-owned one. It refuses a
    /// folder houseCARL did not create. Derives ModsDir cheaply by reading ModOrganizer.ini, with no index build, and
    /// throws the unconfigured prompt when there is no instance. The returned
    /// <see cref="RiderFolder.CreatedFresh"/> flag drives the cleanup on a failure.</summary>
    public RiderFolder ResolvePatchModFolder(string? patchName, string? into, string defaultStem, RiderNaming? naming)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir from the instance, NO resolver build
            if (!Directory.Exists(_modsDir))
                throw new InvalidOperationException($"cannot write: ModsDir '{_modsDir}' does not exist.");

            if (!string.IsNullOrWhiteSpace(into))
            {
                // The same shared extend resolver as the .esp write path: a renamed houseCARL patch folder is found by
                // the .esp it holds or by its new name, so every rider's into= behaves exactly like a record into=.
                // needEsp:false because a rider targets the FOLDER itself, writing scripts, a .bsa or loose files
                // into it rather than an .esp, so no <stem>.esp need be present.
                // The fresh-patch remedy is the CALLING LANE's, not this method's: omitting into= here does create a
                // fresh folder, but which parameter names it, and what it is called when nobody names it, differ per
                // rider — so the lane hands both in and the sentence is true of it (#357). A lane that hands in
                // nothing keeps the weakest true remedy rather than a shared one that is wrong for it.
                var folder = ResolveOwnedPatchFolder(into, needEsp: false, FreshPatchRemedy.None,
                                                     riderNaming: naming, riderDefaultStem: defaultStem);
                return new RiderFolder(folder, folder, CreatedFresh: false);   // reused — the user owns it; cleanup leaves it
            }

            var newStem = UniqueStem(PatchStem(string.IsNullOrWhiteSpace(patchName) ? defaultStem : patchName!));
            var newFolder = Path.Combine(_modsDir, ModFolderName(newStem));
            Directory.CreateDirectory(newFolder);
            WriteOwnerMeta(newFolder, "(houseCARL output)");   // ownership marker; this folder may hold scripts / a .bsa / loose files, not an .esp
            return new RiderFolder(newFolder, newFolder, CreatedFresh: true);
        }
    }

    /// <summary>The <c>Scripts\</c> output folder for a compiled .pex: a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>Scripts\</c> subfolder, which MO2 deploys into the game's
    /// Data\Scripts. Carries the mod-folder root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch_name"));
        var scripts = Path.Combine(f.ModFolder, "Scripts");
        Directory.CreateDirectory(scripts);
        return f with { OutputDir = scripts };
    }

    /// <summary>The output_dir= escape hatch: the user names where the compiled .pex lands instead of houseCARL
    /// cutting a fresh folder-per-patch mod folder. output_dir is a mod-folder ROOT and houseCARL appends
    /// <c>Scripts\</c>, matching <see cref="ResolveCompiledScriptFolder"/> and MO2's deploy model so the .pex
    /// actually loads, with a guard against appending a second Scripts\ when one is already there. It cuts no
    /// houseCARL mod folder, and the folder is user-owned, so the returned <see cref="RiderFolder"/> carries
    /// CreatedFresh=false and cleanup never deletes it on a failed compile. <paramref name="deployWarning"/> is
    /// non-null when the final Scripts\ path is none of a mod's own Scripts\, the MO2 overwrite folder, or the game's
    /// Data, because the .pex compiles but the game will not auto-load it from there. Refuses loudly on an unusable
    /// output_dir — a malformed path, or one naming an existing file.</summary>
    public RiderFolder ResolveExplicitScriptFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "Scripts", ScriptOutputContract, out deployWarning);

    /// <summary>The same output_dir= contract for the SEQ rider: the user names a mod-folder root and houseCARL
    /// appends <c>SEQ\</c>. It exists because the .seq output model otherwise assumes the plugin it serves lives in a
    /// houseCARL folder, which the in-place .esp lane inverts — the .esp in the mod's own folder, the .seq in a
    /// different mod entirely — and a .seq in an un-enabled or wrong folder leaves the quest silently dead.
    /// <para>The <c>into=</c> ownership check is deliberately untouched: letting into= name a folder houseCARL did
    /// not create would put its patch-folder machinery inside a third party's mod. This lane instead writes a new
    /// sidecar file into a folder the user owns, cutting no houseCARL folder and bypassing cleanup
    /// (<see cref="RiderFolder.CreatedFresh"/> = false).</para></summary>
    public RiderFolder ResolveExplicitSeqFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "SEQ", SeqOutputContract, out deployWarning);

    /// <summary>The shared body of the output_dir= lanes, one artifact per caller: normalize the root, refuse an
    /// unusable one loudly, apply <paramref name="contract"/>, which appends <paramref name="sub"/> with the
    /// double-segment guard and decides deployability, create the folder, and hand back a user-owned RiderFolder.
    /// One body rather than one per rider, so the rules cannot drift per artifact.</summary>
    RiderFolder ResolveExplicitRiderFolder(
        string outputDir, string sub,
        Func<string, string, string, string, (string dir, bool appended, string? deployWarning)> contract,
        out string? deployWarning)
    {
        lock (_gate)
        {
            if (!_configured) throw NotConfigured();
            EnsurePathsDerived();                          // cheap: derive ModsDir/DataDir for the deployability check, NO resolver build
            string root;
            try { root = Path.GetFullPath((outputDir ?? "").Trim().Trim('"')); }
            catch (Exception ex) { throw new InvalidOperationException($"output_dir '{outputDir}' is not a usable path ({ex.Message})."); }
            if (File.Exists(root))
                throw new InvalidOperationException($"output_dir '{root}' is a file, not a folder. Give a mod-folder root — houseCARL appends {sub}\\.");

            var (outDir, appended, warn) = contract(root, _modsDir, _dataDir, _overwriteDir);
            // A plain message when the folder cannot be created — the subfolder already exists as a file, or the path
            // is read-only — instead of letting the IO or access exception reach the generic internal-failure
            // handler, which would read as a houseCARL bug rather than bad input.
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"output_dir: couldn't create the output folder '{outDir}' ({ex.Message}). Check the path and that it's writable."); }
            deployWarning = warn;
            // ModFolder is the mod-folder root — inert here, since cleanup is bypassed by CreatedFresh=false, but
            // kept accurate: when the user pointed at the subfolder, the root is its parent; otherwise the path they
            // gave IS the root.
            var modRoot = appended ? root : (Path.GetDirectoryName(outDir.TrimEnd('\\', '/')) ?? outDir);
            return new RiderFolder(outDir, modRoot, CreatedFresh: false);   // user-owned: residue cleanup never touches it
        }
    }

    /// <summary>Pure, filesystem-free resolution of the output_dir= contract, so it is testable without an MO2
    /// instance. Appends <c>Scripts\</c> to a mod-folder root, taking a root that already ends in a Scripts segment
    /// as-is — any case, trailing separator tolerated — rather than doubling it. <paramref name="outputDir"/> is
    /// expected absolute. Returns the final Scripts dir, whether Scripts\ was appended, and a deployWarning when the
    /// result is none of a mod folder under <paramref name="modsDir"/>, the overwrite folder, or
    /// <paramref name="dataDir"/>. <paramref name="overwriteDir"/> counts as deployable because MO2 maps the
    /// overwrite folder's contents onto the Data root at top priority.</summary>
    internal static (string scriptsDir, bool appendedScripts, string? deployWarning) ScriptOutputContract(
        string outputDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var (scriptsDir, appended, deployable) = SubfolderOutputContract(outputDir, "Scripts", modsDir, dataDir, overwriteDir);
        string? warn = deployable ? null :
            $"note: '{scriptsDir}' isn't a folder MO2 (or the game) auto-loads scripts from, so the compiled .pex won't " +
            "deploy on its own — it compiled fine, but you must place it where the game loads scripts yourself: a mod's " +
            "own Scripts\\ folder (<mods>\\<YourMod>\\Scripts), the MO2 overwrite folder, or the game's <Data>\\Scripts.";
        return (scriptsDir, appended, warn);
    }

    /// <summary><see cref="ScriptOutputContract"/>'s twin for the <c>.seq</c>: the same pure path contract with
    /// <c>SEQ\</c> appended, and a warning worded for what a mis-placed .seq costs. The stakes differ from the .pex's:
    /// a script that does not deploy leaves the old behaviour, while a .seq the engine never reads leaves every
    /// start-game-enabled quest in that plugin silently not starting.</summary>
    internal static (string seqDir, bool appendedSeq, string? deployWarning) SeqOutputContract(
        string outputDir, string modsDir, string dataDir, string overwriteDir = "")
    {
        var (seqDir, appended, deployable) = SubfolderOutputContract(outputDir, "SEQ", modsDir, dataDir, overwriteDir);
        string? warn = deployable ? null :
            $"note: '{seqDir}' isn't a folder MO2 (or the game) reads SEQ files from, so the game will NOT see this .seq — " +
            "the file is correct, but until it sits somewhere loaded the plugin's start-game-enabled quests stay silently " +
            "dead. Put it in a mod's own SEQ\\ folder (<mods>\\<YourMod>\\SEQ — enabled in MO2), the MO2 overwrite folder, or the game's <Data>\\SEQ.";
        return (seqDir, appended, warn);
    }

    /// <summary>The shared pure core of the output_dir= contracts: append <paramref name="sub"/> to a mod-folder
    /// root, taking a root already ending in that segment as-is rather than doubling it, and decide deployability.
    /// MO2 overlays a mod folder's CONTENTS onto the game Data root, so a deployable folder is exactly
    /// <c>&lt;mods&gt;\&lt;modFolder&gt;\&lt;sub&gt;</c>, with the mod folder a direct child of mods and the
    /// subfolder directly under it. A bare <c>&lt;mods&gt;\&lt;sub&gt;</c> has no mod folder, and a nested
    /// <c>&lt;mods&gt;\X\Sub\&lt;sub&gt;</c> lands at Data\Sub\… rather than Data\&lt;sub&gt;, so neither loads and
    /// both warn. A direct game install loads exactly <c>&lt;data&gt;\&lt;sub&gt;</c>, as does
    /// <c>&lt;overwriteDir&gt;\&lt;sub&gt;</c>. <paramref name="outputDir"/> is expected absolute. The per-artifact
    /// sentence stays with each caller: the rule is shared, the consequence is not.</summary>
    static (string dir, bool appendedSub, bool deployable) SubfolderOutputContract(
        string outputDir, string sub, string modsDir, string dataDir, string overwriteDir = "")
    {
        // A drive root keeps its separator: trimming "C:\" gives "C:", and combining that with a subfolder yields the
        // drive-RELATIVE "C:SEQ", which Windows resolves against the process's current directory on that drive — so
        // the folder is created somewhere else entirely under a name that looks absolute.
        var root = IsRoot(outputDir) ? outputDir : outputDir.TrimEnd('\\', '/');
        bool already = Path.GetFileName(root).Equals(sub, StringComparison.OrdinalIgnoreCase);
        var dir = already ? root : Path.Combine(root, sub);
        return (dir, !already,
            IsModDeployFolder(dir, modsDir) || IsDataDeployFolder(dir, dataDir) || IsDataDeployFolder(dir, overwriteDir));
    }

    /// <summary>Is this path a filesystem root whose trailing separator is part of its meaning — <c>C:\</c>, where
    /// trimming yields the drive-relative <c>C:</c>? A bare UNC share answers true as well, since
    /// <c>GetPathRoot</c> returns it unchanged, which is harmless because trimming it changes nothing. The case this
    /// helper exists for is the drive root.</summary>
    static bool IsRoot(string path)
    {
        try { return string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>A deploy folder MO2 actually serves: exactly <c>&lt;modsDir&gt;\&lt;modFolder&gt;\&lt;sub&gt;</c>,
    /// with the mod folder a direct child of the mods root and the subfolder directly under it. MO2 maps a mod
    /// folder's contents onto the Data root, so <c>&lt;mods&gt;\Scripts</c> has no mod and
    /// <c>&lt;mods&gt;\X\Sub\Scripts</c> lands at Data\Sub\Scripts. The rule is about shape, not the segment's name,
    /// so every lane shares it. An empty mods root is false. Case-insensitive and normalized.</summary>
    static bool IsModDeployFolder(string outDir, string modsDir)
    {
        if (string.IsNullOrEmpty(modsDir)) return false;
        var modFolder = Path.GetDirectoryName(outDir.TrimEnd('\\', '/'));       // expect <mods>\<modFolder>
        return modFolder is not null && PathEquals(Path.GetDirectoryName(modFolder), modsDir);
    }

    /// <summary>A direct game install loads exactly <c>&lt;dataDir&gt;\&lt;sub&gt;</c> (not Data\Sub\…). Empty data dir →
    /// false. Case-insensitive, normalized.</summary>
    static bool IsDataDeployFolder(string outDir, string dataDir)
    {
        if (string.IsNullOrEmpty(dataDir)) return false;
        return PathEquals(Path.GetDirectoryName(outDir.TrimEnd('\\', '/')), dataDir);
    }

    /// <summary>Case-insensitive equality of two paths after full-path normalization + trailing-separator trim (no
    /// filesystem access). A null left side (no parent — e.g. a drive root) is never equal.</summary>
    static bool PathEquals(string? a, string b)
    {
        if (a is null) return false;
        return Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>Source\Scripts\</c> output folder for a decompiled .psc — the SE-canonical source layout, and
    /// the same default patch stem as the compile lane so decompile, edit and compile accumulate in one folder via
    /// <c>into=</c>. Carries the root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveDecompiledSourceFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch_name"));
        var src = Path.Combine(f.ModFolder, "Source", "Scripts");
        Directory.CreateDirectory(src);
        return f with { OutputDir = src };
    }

    /// <summary>A non-.esp rider that failed after creating a fresh houseCARL mod folder cleans up after itself, the
    /// same "a refusal leaves no orphan folder" principle the .esp lane follows. If the fresh folder is genuinely
    /// empty — holding nothing but our own meta.ini marker anywhere in its tree — it is deleted, so "no output
    /// written" is true of the disk; if real output landed, the folder stays and its path is returned so the caller
    /// can name it, because houseCARL never deletes content it did not recognise as its own. A reused into= folder
    /// is never touched: the user owns it. Returns the leftover path to name, or null. Best-effort: a cleanup hiccup
    /// never masks the rider's own outcome.</summary>
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

    // ---- write the start-game-enabled-quest .seq file ----

    /// <summary>The <c>SEQ\</c> output folder for a generated <c>.seq</c>: a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>SEQ\</c> subfolder, which MO2 deploys into the game's
    /// <c>Data\SEQ</c>. Carries the mod-folder root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveSeqFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_SEQ", new RiderNaming("patch"));
        var seq = Path.Combine(f.ModFolder, "SEQ");
        Directory.CreateDirectory(seq);
        return f with { OutputDir = seq };
    }

    /// <summary>If <paramref name="pluginPath"/> lives in a houseCARL-owned mod folder directly under ModsDir, return
    /// that folder's patch stem, so the <c>.seq</c> defaults into the same folder as the <c>.esp</c>: one mod to
    /// enable, and no second folder the user might forget, which would leave the quest silently dead. Only when the
    /// folder is the canonical one for this plugin, so a later <c>into=</c> resolves to exactly it; otherwise null,
    /// and the caller cuts a fresh folder.</summary>
    string? OwnedPluginFolderStem(string pluginPath)
    {
        var dir = Path.GetDirectoryName(pluginPath);
        if (dir is null || Path.GetDirectoryName(dir) is not { } parent || !PathEquals(parent, _modsDir)) return null;
        if (!IsHouseCarlOwned(dir)) return null;
        var stem = PatchStem(Path.GetFileName(pluginPath));
        return Path.GetFileName(dir).Equals(ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? stem : null;
    }

    /// <summary>Write a plugin's start-game-enabled-quest <c>.seq</c>. Opens <paramref name="plugin"/>, collects
    /// every start-game-enabled quest it defines, and writes <c>&lt;ModFolder&gt;\SEQ\&lt;plugin&gt;.seq</c> — the
    /// file the engine reads to actually start those quests, since the flag alone does nothing — under the same
    /// crash-atomic, non-destructive folder-per-patch model as the other riders. The output folder defaults to the
    /// plugin's own houseCARL folder when it lives in one, so the .seq deploys with the .esp; else a fresh folder, or
    /// <paramref name="into"/> / <paramref name="patchName"/> when given. A plugin with no such quests writes nothing
    /// and cuts no folder, stated explicitly rather than as a silent empty file. Serialized on the write gate.
    /// <para><paramref name="outputDir"/> is the same output_dir= contract the compile lane carries: the user names a
    /// mod-folder root — typically the plugin's own mod, after an in-place .esp edit — and the .seq lands in its
    /// <c>SEQ\</c>. It wins over <paramref name="patchName"/> and <paramref name="into"/>, and cuts no houseCARL
    /// folder.</para>
    /// <para>A destination already holding exactly these bytes is reported as such and not rewritten, because the
    /// workflow regenerates the .seq after every in-place edit and the answer is byte-identical nearly every time.
    /// The no-op is stated explicitly: an unstated skip is indistinguishable from a silent failure.</para></summary>
    public SeqOutcome WriteSeq(string plugin, string? patchName, string? into, string? outputDir = null)
    {
        if (string.IsNullOrWhiteSpace(plugin))
            return SeqOutcome.Fail("no source given. Pass source= the plugin whose start-game-enabled quests need a .seq — its filename (e.g. 'MyQuestMod.esp') or an absolute path.");
        plugin = plugin.Trim().Trim('"');

        // Source resolution: a bare filename is located across the MO2 folders — enabled, disabled, not-yet-listed,
        // overwrite and game Data — through the same shared contract every other lane uses, so two tools can never
        // find different files for one name. An absolute path is used verbatim. The arm that resolved is reported
        // rather than silent: which copy was read decides which .seq you get.
        string pluginPath, resolvedFrom;
        try
        {
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            var comp = Mo2LoadOrder.ReadComposition(profileDir);        // cheap text parse — no index build
            var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, plugin, null, offerModParam: false);
            // The locate contract's refusal names what it could not find; this adds what THIS tool accepts, so the
            // caller is not left to infer that a bare filename is allowed at all.
            if (loc.Error is not null)
                return SeqOutcome.Fail($"{loc.Error} Pass source= the plugin's FILENAME (located across your MO2 mod folders, the overwrite folder and game Data) or an ABSOLUTE path to the .esp/.esm/.esl.");
            if (loc.Ambiguous is { } hits)
                return SeqOutcome.Fail($"'{Path.GetFileName(plugin)}' is provided by {hits.Count} locations — name the one you mean by absolute path: "
                                     + string.Join("; ", hits.Select(h => $"{h.Where} -> {h.Path}")));
            pluginPath = loc.Path!;
            resolvedFrom = loc.Where;
        }
        catch (Exception ex) { return SeqOutcome.Fail(ex.Message); }

        if (!PluginExts.Contains(Path.GetExtension(pluginPath), StringComparer.OrdinalIgnoreCase))
            return SeqOutcome.Fail($"'{Path.GetFileName(pluginPath)}' is not a plugin (.esp/.esm/.esl).");

        lock (_writeGate)                                                // one write at a time: build, resolve, commit
        {
            if (ConfigPromptOrNull() is { } cfgPrompt) return SeqOutcome.Fail(cfgPrompt);   // need ModsDir for the output folder
            lock (_gate) EnsurePathsDerived();                          // derive ModsDir for the owned-folder check; lock order is _writeGate then _gate

            // Build the .seq from the plugin: a read-only overlay, disposed inside, so no handle is held at rest.
            SeqFile.SeqBuild built;
            try { built = SeqFile.Build(pluginPath); }
            catch (Exception ex)
            { return SeqOutcome.Fail($"could not read '{Path.GetFileName(pluginPath)}' as a plugin: {ex.Message}"); }

            // No start-game-enabled quests means no .seq is needed: write nothing, cut no folder, and say so.
            // It still carries UserChoseOutput, because the lane the caller named is a fact about the CALL, and
            // dropping it here would make the json twin contradict its own lane note.
            if (built.Quests.Count == 0)
                return new SeqOutcome(true, null, null, null, built.Quests, built.PluginFileName, false)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, UserChoseOutput = !string.IsNullOrWhiteSpace(outputDir) };

            // Output folder: output_dir=, the user's own mod folder, wins; else the plugin's own houseCARL folder;
            // else a fresh one or an explicit into= / patch_name. The output_dir arm cuts no houseCARL folder, so
            // the owned-folder default is not consulted there — the caller named the destination outright.
            bool chosenOutput = !string.IsNullOrWhiteSpace(outputDir);
            string? autoInto = (!chosenOutput && string.IsNullOrWhiteSpace(into) && string.IsNullOrWhiteSpace(patchName))
                ? OwnedPluginFolderStem(pluginPath) : null;
            RiderFolder rf;
            string? deployWarning = null;
            try
            {
                rf = chosenOutput
                    ? ResolveExplicitSeqFolder(outputDir!, out deployWarning)
                    : ResolveSeqFolder(patchName, autoInto ?? into);
            }
            catch (InvalidOperationException ex) { return SeqOutcome.Fail(ex.Message); }

            var seqName = Path.GetFileNameWithoutExtension(pluginPath) + ".seq";
            var dest = Path.Combine(rf.OutputDir, seqName);

            // When the destination already holds exactly these bytes, report it and write nothing: the regenerate
            // loop rarely changes the answer, so the common case was a rewrite that changed nothing. Compared
            // against the bytes already built in memory, so this costs one read of a file that is typically a few
            // hundred bytes, and it is reported as its own state rather than folded into success.
            // One thing the skip must NOT take with it is the timestamp. The dialogue check's .seq staleness test
            // reads mtime, not content, so a .seq older than its plugin is reported stale even when byte-perfect,
            // and skipping the write after an in-place edit would leave a permanent advisory no tool could clear.
            // So an identical-but-older file has its timestamp refreshed. If the touch fails, fall THROUGH to the
            // real write rather than reporting a no-op that leaves the staleness test wrong.
            bool sameBytes = SameBytesOnDisk(dest, built.Bytes), identical = sameBytes, touched = false;
            if (identical && !RefreshSeqTimestamp(dest, pluginPath, out touched)) identical = false;
            if (identical)
                return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null)
                    { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, Unchanged = true, TimestampRefreshed = touched,
                      UserChoseOutput = chosenOutput, DeployWarning = deployWarning };

            // Is there something here already? An output_dir= destination is a folder houseCARL does not own, so the
            // file being replaced may be the mod's own shipped .seq, and "wrote" and "replaced yours" are different
            // facts about the disk.
            bool replaced = File.Exists(dest);
            // And whether anything was lost. The one path that reaches the write with sameBytes true is a timestamp
            // refresh that failed — a share that accepts the stamp without persisting it — and calling that
            // "overwritten, no backup is kept" would be an alarm about a file whose bytes were re-written identically.
            bool replacedSameBytes = replaced && sameBytes;

            // Crash-atomic write of <plugin>.seq under SEQ\, into a houseCARL-owned folder or the folder the caller
            // named in output_dir=.
            try { AtomicFile.WriteAllBytes(dest, built.Bytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);             // nothing landed → a fresh folder is an orphan
                return SeqOutcome.Fail($"could not write '{seqName}': {ex.Message}"
                    + (residue is null ? "" : $" The freshly created folder was left at '{residue}'.")
                    // On the output_dir lane cleanup is bypassed by design, since the folder is the user's, so the
                    // SEQ\ directory is still there and "nothing was written" is true of the file, not the disk.
                    // Worded for what is known — the folder is there and houseCARL will not remove it — because
                    // claiming this call created it would be false whenever the mod already ships a SEQ\ folder.
                    + (chosenOutput ? $" (the '{rf.OutputDir}' folder is left in place — houseCARL never removes a folder you named.)" : ""));
            }

            // Integrity: the on-disk size matches the bytes built, so success is never claimed falsely.
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != built.Bytes.Length)
                return SeqOutcome.Fail($"wrote '{seqName}' but its on-disk size ({size}) does not match the {built.Bytes.Length} expected byte(s) — verify before relying on it.");

            return new SeqOutcome(true, null, dest, rf.ModFolder, built.Quests, built.PluginFileName, autoInto is not null)
                { ResolvedFrom = resolvedFrom, PluginPath = pluginPath, Replaced = replaced,
                  ReplacedSameBytes = replacedSameBytes,
                  UserChoseOutput = chosenOutput, DeployWarning = deployWarning };
        }
    }

    /// <summary>Keep a skipped write honest against the mtime-based .seq staleness test: if
    /// <paramref name="seqPath"/> is byte-identical but older than <paramref name="pluginPath"/>, stamp it now.
    /// <paramref name="touched"/> reports whether a stamp was actually needed, so the response can say so rather than
    /// implying one silently happened. Returns false when the stamp was needed and failed, so the caller does the
    /// real write instead of reporting a no-op that leaves the dialogue check calling the file stale.</summary>
    static bool RefreshSeqTimestamp(string seqPath, string pluginPath, out bool touched)
    {
        touched = false;
        try
        {
            var seqTime = File.GetLastWriteTimeUtc(seqPath);
            var pluginTime = File.GetLastWriteTimeUtc(pluginPath);
            if (seqTime >= pluginTime) return true;                 // already newer — the staleness test is satisfied
            // Now is not necessarily enough: a plugin can be stamped in the FUTURE relative to this machine's clock —
            // a restored backup, a synced share, a dual-boot local-versus-UTC BIOS clock — and stamping the current
            // time there would mutate the file, report a refresh, and leave the comparison exactly as it was. Stamp
            // past the plugin, then verify, and let a stamp that did not achieve it fall through to the real write.
            var target = pluginTime > DateTime.UtcNow ? pluginTime.AddSeconds(1) : DateTime.UtcNow;
            File.SetLastWriteTimeUtc(seqPath, target);
            if (File.GetLastWriteTimeUtc(seqPath) < File.GetLastWriteTimeUtc(pluginPath)) return false;
            touched = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Does <paramref name="path"/> already hold exactly <paramref name="bytes"/>? Length first as the cheap
    /// discriminator, then a full compare. Any IO problem answers false: "could not prove it is identical" must fall
    /// through to the write, because a wrong true here leaves a stale .seq reported as current.</summary>
    static bool SameBytesOnDisk(string path, byte[] bytes)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != bytes.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        }
        catch { return false; }
    }

    // ---- decompiler class hierarchy (lazy, cached for process lifetime) ----------------------------------------

    Dictionary<string, string>? _classParents;
    string? _classParentsNote;
    readonly object _classParentsLock = new();

    /// <summary>Drop the cached hierarchy whenever <see cref="_modsDir"/> can have changed — an instance switch or a
    /// profile re-derive — because a stale tree's edges could suppress a cast the new order's hierarchy does not
    /// justify. Rebuilds lazily.</summary>
    void InvalidateClassParents() { lock (_classParentsLock) { _classParents = null; _classParentsNote = null; } }

    /// <summary>The decompiler's child-to-parent class map: the committed vanilla baseline beside the exe, plus
    /// loose .psc headers across the MO2 mods tree from mods that ship sources. Built on the first decompile call and
    /// cached for the process lifetime. It is a soft input by construction — missing pieces mean explicit casts in
    /// the output, never wrong code — and the note names any degraded mode. The input pex's own folder is topped up
    /// per call by the caller, since it varies per input. Paths derive FIRST, under the gate, because in instance
    /// mode ModsDir is lazy: otherwise a decompile-first session caches a baseline-only map for the process lifetime
    /// with the mods-tree harvest silently skipped. Lock order is _gate then _classParentsLock.</summary>
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

    /// <summary>The given stem if it is free, else the first free "<c>&lt;stem&gt;_NNN</c>". Free means both that no
    /// mod folder "<c>houseCARL - &lt;stem&gt;</c>" already exists, houseCARL's own or a user's, and that no plugin
    /// "<c>&lt;stem&gt;.esp</c>" is already in the active load order. The load-order half stops a generic default
    /// stem from emitting a plugin that duplicates a foreign active one: the engine forbids two active plugins
    /// sharing a basename, and mod-folder uniqueness alone never sees a same-named plugin in another mod. into=
    /// remains the way to grow an existing patch; this is only the fresh path.</summary>
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

    /// <summary>The active load order's plugin filenames, case-insensitive, for the UniqueStem collision check. Read
    /// from the already-built resolver if present, else the same cheap composition it builds from — deliberately not
    /// via the <see cref="Resolver"/> getter, which refuses a zero-plugin instance, a legitimate minimal write.
    /// Best-effort: any read failure, or no active plugins, yields an empty set and folder-only uniqueness, so the
    /// collision check is a safety net that never turns a previously-valid write into a failure.</summary>
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
        catch { /* unreadable or empty load order → folder-only uniqueness */ }
        return set;
    }

    /// <summary>The four-step <c>into=</c> extend resolver, shared by the .esp write path
    /// (<see cref="ResolveOutputPath"/>) and the rider and asset path (<see cref="ResolvePatchModFolder"/>) so
    /// "extend my renamed patch" behaves identically across records, scripts, BSAs and assets. Resolves
    /// <paramref name="into"/> to the houseCARL-owned mod folder it names: the canonical "houseCARL - &lt;stem&gt;"
    /// fast path; then by the &lt;stem&gt;.esp it holds, for a renamed folder, since the .esp basename is fixed by
    /// whatever binds it; then by the folder's own name, since folder and plugin names need not match; then loud
    /// refusals naming every place searched, distinguishing a foreign un-owned collision from a genuine miss. Every
    /// step is ownership-gated, so a plugin houseCARL did not make stays refused — editing one is the separate
    /// in-place lane. <paramref name="needEsp"/> tightens the canonical fast path for the record lane, where the
    /// folder must actually hold &lt;stem&gt;.esp to short-circuit; the rider lane targets the folder itself. Caller
    /// holds <see cref="_gate"/>.
    /// <paramref name="freshPatch"/> is the calling operation's own statement about how, or whether, it can create a
    /// patch, and decides only the not-found refusal's remedy; <paramref name="laneClause"/> is that same lane's
    /// extra next step, appended to that one arm. Both are deliberately separate from
    /// <paramref name="needEsp"/>.</summary>
    string ResolveOwnedPatchFolder(string into, bool needEsp,
                                   FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? laneClause = null,
                                   RiderNaming? riderNaming = null, string? riderDefaultStem = null)
    {
        var stem = PatchStem(into);                             // strips a trailing .esp/.esm/.esl; no directory parts (can't escape ModsDir)
        var espName = stem + ".esp";

        // Canonical fast path: "houseCARL - <stem>" still owns the patch, the common case, with no scan. The record
        // lane also requires it to hold <stem>.esp; the rider lane only needs the owned folder.
        var canonical = Path.Combine(_modsDir, ModFolderName(stem));
        if (Directory.Exists(canonical) && IsHouseCarlOwned(canonical) && (!needEsp || File.Exists(Path.Combine(canonical, espName))))
            return canonical;

        // By plugin name: the owned folder holding <stem>.esp, whatever it is now called — the renamed-folder case.
        var byEsp = OwnedFoldersHolding(espName)
            .Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (byEsp.Count == 1) return byEsp[0];
        // Known wrong for one class of caller: this arm and the folder catch-all below both say "pass into=", which a
        // tool that declares no into= parameter cannot do — such a caller gets an unknown-parameter refusal instead.
        // Unfixed here; the remedy needs the calling surface's own vocabulary, like laneClause below.
        if (byEsp.Count > 1)
            throw new InvalidOperationException(
                $"cannot extend: {byEsp.Count} houseCARL folders carry '{espName}' — ambiguous, refusing to guess. " +
                "Pass the CONTAINING mod-folder name as into= to pick one (folder & plugin names need not match): " +
                string.Join("  |  ", byEsp.Select(d => $"into=\"{Path.GetFileName(d)}\"")) + ".");

        // Folder catch-all: into= names the mod folder itself — the same-named-plugin disambiguator, and the way to
        // point at a renamed folder by its new name.
        var named = ResolveOwnedFolderByName(into);
        if (named is not null) return named;

        // Nothing matched. Distinguish a foreign, un-owned name collision — refused so originals stay untouched —
        // from a genuine miss, naming every place searched so the refusal reveals all the pieces at once.
        var bareName = Path.GetFileName(into.Trim());
        foreach (var cand in new[] { ModFolderName(stem), bareName })
        {
            var candPath = string.IsNullOrEmpty(cand) ? null : Path.Combine(_modsDir, cand);
            if (candPath is not null && Directory.Exists(candPath) && !IsHouseCarlOwned(candPath))
                throw new InvalidOperationException(
                    $"cannot extend: mod folder '{cand}' exists but was NOT created by houseCARL (no marker) — " +
                    "refusing to write into a folder houseCARL doesn't own (originals untouched, Q3). Use a different patch name.");
        }
        // The fresh-write remedy is the caller's to authorize: each operation states its OWN fresh-write path,
        // because it is not inferable here and the lane bit does not separate it. Three independent properties make
        // a shared assumption false. An operation may be unable to create a patch at all, editing an artifact that
        // must already exist, so a create remedy is false for it. It may create one but name it off something other
        // than the default — a caller-supplied identifier, or a stem its own call site fixes. Or the spelling may
        // name a DIFFERENT artifact on that operation, because it declares more than one output name. Which
        // operation is which is answered at the call sites, so a reader who wants the set greps the enum.
        // Hence the default claims no fresh-write path at all: a weaker "omit into= to create it fresh" is wrong for
        // any lane that cannot create anything, and telling such a caller to omit the lane sends them into a second
        // refusal. A caller added later without a thought about any of this gets "Check the name.", and every
        // stronger claim is one an operation makes for itself.
        // laneClause is the same statement one step further: a lane whose next step is its own hands the sentence in
        // rather than having it inferred from a semantic bit. It rides THIS arm only.
        // It is rendered BEFORE the fallback: the lane's own diagnosis is what makes the fallback the right thing
        // left to do.
        // The sentence deliberately does not predict the resulting filename. UniqueStem takes a stem only when it is
        // free on both tests — no "houseCARL - <stem>" folder exists, and no active plugin is named "<stem>.esp" —
        // and suffixes it otherwise. Either trigger is ordinary, so the qualifier scopes both names the sentence
        // mentions.
        throw new InvalidOperationException(
            $"cannot extend: no houseCARL plugin '{espName}' in any houseCARL folder, and no houseCARL folder named " +
            $"'{ModFolderName(stem)}'" +
            (string.Equals(bareName, ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? "" : $" or '{bareName}'") +
            // A rider that named its own folder parameter answers with THAT parameter and THAT lane's default stem;
            // the enum arms below are the record lanes', where both are the same on every caller (#357).
            ". " + (laneClause is null ? "" : laneClause + " ") + (riderNaming is { } rn
            ? $"Omit into= and pass {rn.Param}=\"{stem}\" to create it fresh under a name you choose, or omit "
              + $"{rn.Param} too and houseCARL names the folder \"{ModFolderName(riderDefaultStem ?? "")}\" — either name auto-suffixed "
              + "if already taken. " + (rn.Caveat is null ? "" : rn.Caveat + " ") + "Or check the name."
            : freshPatch switch
            {
                FreshPatchRemedy.NamedByPatchParam =>
                    $"Omit into= and pass patch=\"{stem}\" to create it fresh under a name you choose, or omit patch= "
                    + "too and houseCARL names it \"Patch\" — either name auto-suffixed if already taken. "
                    + "Or check the name.",
                FreshPatchRemedy.CreatedByOmittingInto => "Omit into= to create it fresh, or check the name.",
                _ => WriteSentences.ExtendCheckTheName,
            }));
    }

    /// <summary>houseCARL-owned mod folders under ModsDir holding a plugin file named <paramref name="espFileName"/>
    /// at their root. The .esp basename is fixed — SPID files, config JSON and masters all bind the patch by its
    /// filename — while the MO2 mod-folder name is the user's to rename, so an extend finds the patch by the plugin
    /// it holds rather than the folder's current name. Ownership-gated by the marker, so a user mod that merely
    /// shares the basename is never returned. Returns full .esp paths.</summary>
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

    /// <summary>A houseCARL-owned mod folder named exactly <paramref name="rawName"/> or
    /// "<c>houseCARL - &lt;rawName&gt;</c>" — the folder catch-all behind <c>into=</c>, where the user names the
    /// containing mod folder because the plugin basename is ambiguous or the folder was renamed. The folder name need
    /// not match the .esp inside. Bare name only, with no directory parts, so it cannot escape ModsDir. Null when no
    /// such folder is houseCARL-owned.</summary>
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

    /// <summary>The single top-level plugin in a houseCARL folder, so <c>into=</c> a folder name can edit "the plugin
    /// in this folder" without re-stating its basename. Null plus a named <paramref name="reason"/> when the folder
    /// holds none or more than one, rather than guessing which of several to extend.</summary>
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
    /// marker. The marker lives in meta.ini, the one mod-root file MO2 does not deploy into the game Data folder, so
    /// it never pollutes Data. Fail-safe: a missing or stripped marker reads as NOT owned, so houseCARL refuses to
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

    /// <summary>Write the new mod folder's <c>meta.ini</c>: the <c>[houseCARL]</c> ownership marker, which MO2 does
    /// not deploy, plus a minimal <c>[General]</c> for MO2's display. A minimal meta.ini is valid and the custom
    /// section is ours. A fresh folder has none, so this just writes it.</summary>
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

    /// <summary>Is a located file the copy the MO2 install serves for its filename, and if not, why not? One half of
    /// "does the game load this file"; the other is <see cref="TickStanding"/>. The two are independent — a copy can
    /// be both shadowed and unticked, each with its own remedy — so collapsing them to one cause would always drop
    /// one. NotAnInstallCopy is deliberately the zero value: a default-constructed result must read not-loaded.</summary>
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
        /// <summary>This copy sits in a folder modlist.txt does not mention at all, so MO2 has not registered it —
        /// the state of a patch houseCARL just wrote, before the refresh. Remedy: refresh MO2. Distinct from
        /// <see cref="ModDisabled"/> because "switch the mod on" is not available here: there is nothing in MO2's
        /// list to switch.</summary>
        ModUnregisteredLayer,
    }

    /// <summary>Is a plugin filename ticked to load — the other half of "does the game load this file". A plugin's
    /// tick state is a different fact from its mod folder's switch, MO2's right pane versus its left, which is the
    /// confusion this split exists to end. Unregistered is the zero value for the same conservative reason as in
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

    /// <summary>One located plugin file, or why not. Exactly one of Path, Ambiguous or Error is set.
    /// <para>The two standings are carried separately rather than pre-collapsed into one boolean, so a renderer can
    /// explain rather than merely classify: "not active" names the state but not the cause, and the causes —
    /// unticked, mod switched off, shadowed, unregistered — have different remedies. <see cref="Enabled"/> keeps the
    /// single "the game loads this file" boolean, derived rather than stored.</para></summary>
    /// <param name="CauseDetail">For <see cref="ServedStanding.Shadowed"/>, the where-label of the copy that IS
    /// served, which is a different copy so it never collides with <paramref name="Where"/>. For the two layer-off
    /// standings, the mod FOLDER NAME alone — never the full hit label, whose text varies per lane and carries its
    /// own remedy, which makes the composed sentence say the same thing twice.</param>
    /// <param name="WhereNamesLayer">Does <paramref name="Where"/> already identify which layer holds this copy? Set
    /// by each lane from what it knows: the filename lane's Where IS the hit's label and the mod= lane's names the
    /// mod, while the direct-path lane's is a constant that identifies nothing. Carried as a fact rather than
    /// re-derived by string-comparing the two labels, because that comparison holds for one lane and silently fails
    /// for another whose label omits the state qualifier.</param>
    internal readonly record struct PluginLocateResult(
        string? Path, string Where, ServedStanding Served, TickStanding Tick, string? CauseDetail,
        bool WhereNamesLayer,
        IReadOnlyList<PluginFileHit>? Ambiguous, string? Error)
    {
        /// <summary>The game loads THIS file: it is the served copy AND its plugin is ticked (implicit masters count —
        /// force-loaded, never listed). Both halves, or the same physical file answers differently depending on how it
        /// was addressed.</summary>
        public bool Enabled => Served == ServedStanding.Serves && Tick is TickStanding.Ticked or TickStanding.Implicit;

        /// <summary>Why the game does not load this file — null when <see cref="Enabled"/>, and also when no file was
        /// located at all. Composed here, once, so every renderer that states it cannot drift apart on the wording.
        /// Both clauses are emitted when both apply: a shadowed copy of an unticked plugin needs two fixes, and
        /// naming one would send the reader to do half the job. The unregistered clause is suppressed when the served
        /// half already failed, because a disabled mod already explains the absence from plugins.txt and repeating it
        /// reads as a second problem.</summary>
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
                        // CauseDetail is always set here: Shadowed is returned only when this copy's own layer is
                        // enabled, which means a served hit exists to name. That hit is a different copy, so naming
                        // it never duplicates Where whichever lane asked.
                        parts.Add($"this copy is SHADOWED — {CauseDetail} provides the copy the game loads");
                        break;
                    // The two layer-off standings name the folder only when Where does not, and state the layer's
                    // condition and remedy in words rather than echoing a label, which is what got printed twice.
                    // Their remedies genuinely differ, which is why they are separate standings: an unregistered
                    // folder has nothing in MO2's list to switch on.
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
                        // string-compares miss — a junction, a subst drive, a UNC route to the same install — where
                        // "not a copy the install provides" would be a confident sentence that is simply false.
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

    /// <summary>Judge the served half for one located file: is <paramref name="fullPath"/> the copy the install
    /// provides for its filename, and if not, which of the three not-served states is it? Judged against the first
    /// hit from an ENABLED layer, which is the rule the real order is built by — not merely the first hit, because
    /// the locate also walks disabled and unlisted folders the order never consults. Compared by full path, since a
    /// backup and the live copy share a filename and are different files.</summary>
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

    /// <summary>Judge the tick half for one plugin filename, from the profile text files. Kept beside
    /// <see cref="JudgeServed"/> so the two halves can never be computed by different rules in different
    /// lanes.</summary>
    static TickStanding JudgeTick(Mo2Composition comp, string fileName)
    {
        if (comp.ActivePluginNames.Contains(fileName)) return TickStanding.Ticked;
        foreach (var x in comp.ImplicitPluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Implicit;
        foreach (var x in comp.InactivePluginNames)
            if (x.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return TickStanding.Unticked;
        return TickStanding.Unregistered;
    }

    /// <summary>The on-disk plugin-locate contract, shared by every lane that resolves a plugin by name, so no two
    /// can diverge. A direct path — rooted or carrying a separator — is used verbatim, so any plugin file can be
    /// inspected; otherwise the argument is a filename found across the whole install (enabled and disabled mod
    /// folders, overwrite, Data), with <paramref name="mod"/> narrowing a name several folders provide. Ambiguity
    /// comes back structured, so each caller renders its own remedy.
    /// <para><paramref name="offerModParam"/> controls whether the not-found refusal offers <c>mod=</c> as the
    /// disambiguator, which is only true for callers that have that parameter: a refusal must never send someone to
    /// a parameter their tool does not expose.</para></summary>
    internal static PluginLocateResult LocatePluginFileOnDisk(
        Mo2Composition comp, string modsDir, string dataDir, string overwriteDir, string plugin, string? mod,
        bool offerModParam = true)
    {
        // A plugin's tick state is a different fact from its mod folder's switch: a plugin can sit in an enabled mod
        // and be unchecked in MO2's right pane, and the game then does not load it. Every lane below returns the
        // (served, tick) pair the renderers state as active or not-active-because, so both halves are judged in
        // every lane by the same two helpers; a lane computing one its own way is how they diverge. Implicit base
        // and CC masters are force-loaded and never listed in plugins.txt, so they count as ticked.
        if (LooksLikePath(plugin))
        {
            if (!File.Exists(plugin))
                return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null, $"no file at path '{plugin}'.");
            var full = Path.GetFullPath(plugin);
            // The standing is computed for a direct path, never assumed: addressing a file by path says nothing about
            // whether the install provides it, and a path can perfectly well name the live copy of an enabled plugin.
            // JudgeServed answers whether THIS file is the copy the install serves, against the first enabled-layer
            // hit. Two costs are accepted: this pays the same folder sweep the filename lane does, which is why a
            // path inside no install root skips it outright; and a path reaching the install through a junction or
            // symlink will not string-match, so it reads as NotAnInstallCopy — conservative rather than wrong in the
            // other direction. The tick half needs no path at all, so it is judged for every direct path.
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
            // WhereNamesLayer is true: "mod 'X'" names the folder, though it carries no state qualifier — which is
            // why a label-equality test would fail here and let the duplication through.
            return new(cand, $"mod '{mod.Trim()}'", modServed, JudgeTick(comp, fn), modDetail, true, null, null);
        }
        var hits = Mo2LoadOrder.LocatePlugin(comp, modsDir, dataDir, overwriteDir, plugin);
        if (hits.Count == 0)
            return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, null,
                $"'{Path.GetFileName(plugin)}' is in no mod folder (enabled, disabled, or not-yet-listed in MO2), the overwrite folder, or the game Data folder. Check the filename, pass an absolute path"
                + (offerModParam ? ", or (if it's an MO2 mod) the exact folder via mod=." : "."));
        if (hits.Count > 1) return new(null, "", ServedStanding.NotAnInstallCopy, TickStanding.Unregistered, null, false, hits, null);
        var (oneServed, oneDetail) = JudgeServed(comp, hits, hits[0].Path);
        // WhereNamesLayer: TRUE — Where IS the located hit's own label, folder and state both.
        return new(hits[0].Path, hits[0].Where, oneServed, JudgeTick(comp, Path.GetFileName(plugin)), oneDetail, true, null, null);
    }

    /// <summary>Does the user's `plugin` argument denote a PATH (use verbatim — the "inspect any file" case) rather
    /// than a bare filename (locate in the MO2 folders)? True if rooted or carrying a directory separator: 'C:\..\X.esp'
    /// or 'mods\M\X.esp' is a path; a bare 'X.esp' is a filename.</summary>
    static bool LooksLikePath(string s) => Path.IsPathRooted(s) || s.Contains('\\') || s.Contains('/');

    /// <summary>Do two paths denote the same plugin file? A full-path, case-insensitive compare, never a filename
    /// compare: an archived backup and the live copy share a name and are different files.</summary>
    static bool SamePluginFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Is <paramref name="fullPath"/> inside any MO2 or game root? Used only to skip work, since a file
    /// outside every root cannot be a copy the install provides, so the enabled/disabled classification itself stays
    /// with the shared locate and is never re-derived here.</summary>
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
            catch { /* an unparseable root simply isn't a match — never a false 'inside' */ }
        }
        return false;
    }

    // ---- corpus-backed type resolution (signature "WEAP" / catalog name "Weapon" → getter Type(s)) -------

    Dictionary<string, List<Type>>? _typeLookup;
    Dictionary<string, List<Type>> TypeLookup => _typeLookup ??= BuildTypeLookup();

    /// <summary>Build the type-string to getter-Type map from the corpus, the authoritative type catalog. Keyed by
    /// both catalog name and 4-char signature; a many-to-one signature accumulates its variants so a signature query
    /// unions them. An abstract-group base name maps to its concrete arms' getter Types by construction — the same
    /// union the signature yields — so a query by the base name unions them too, and the callers' ambiguity branch
    /// names the variants. A corpus type name that will not load is skipped here and surfaces as "unknown type" at
    /// query time, never as a silently wrong type.</summary>
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
        // Abstract-group base names map to their concrete arms' getter Types. The arms are listed on the
        // polymorphic-base's own corpus entry, so the union is derived rather than hand-wired — the generated-coverage
        // cornerstone — and a query by the base name resolves to the same set its signature does.
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

    /// <summary>A user type string to its getter Types. Throws, naming the bad input and what is expected.</summary>
    IReadOnlyList<Type> ResolveTypeFilter(string type)
    {
        if (TypeLookup.TryGetValue(type.Trim(), out var types)) return types;
        throw new ArgumentException(
            $"unknown record type '{type}'. Expected a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon').");
    }

    /// <summary>A form-scope string to getter Types: a catalog name or signature via the type lookup, or a Mutagen
    /// link-interface group name such as "Item" or "Constructible", resolved as every corpus record getter assignable
    /// to <c>I{name}Getter</c> — derived from the real interfaces, never a hand-kept list. The SkyPatcher field map
    /// scopes multi-type ops by these group names, so the plain type lookup alone cannot serve it. Null means the
    /// string names neither, and the caller surfaces that loudly.</summary>
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

/// <summary>How a calling operation can produce a patch that does not exist yet — the operation's own statement, and
/// the only thing the shared <c>into=</c> resolver's not-found refusal may say about creating one. It is never
/// inferred there, because the write lane and the naming semantics do not coincide: a rider's <c>patch=</c> can name
/// a .bsa, and a copy's fresh stem is an EditorID.</summary>
internal enum FreshPatchRemedy
{
    /// <summary>The default and the safe one: this operation claims no fresh-write path, so the refusal offers none.
    /// A removal needs it, because it edits a patch that must already exist, and a weaker default would tell its
    /// callers to omit the lane, which a removal itself refuses.</summary>
    None = 0,

    /// <summary>Omitting <c>into=</c> creates one, under a stem this call site chooses rather than the default —
    /// the copy lanes, whose stem is a new EditorID, and every rider lane, whose stem is its own artifact.</summary>
    CreatedByOmittingInto,

    /// <summary><c>patch=</c> on this tool names a new patch and defaults to "Patch", so the refusal can hand back a
    /// working call with the caller's own guessed name already in it.</summary>
    NamedByPatchParam,
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
    /// <summary>The captured build this outcome was answered from, stamped at the capture boundary so refusals carry
    /// it too: a "not present" is an answer ABOUT a build. Null only where no view was ever consulted, such as a
    /// malformed-FormID parse failure.</summary>
    public string? Epoch { get; init; }

    /// <summary>The RUNTIME FormID of this record in the build that answered — the eight-hex form the game, the
    /// console, Papyrus logs and crash logs print. Rendered beside the FormKey so a reader can carry the record
    /// either way. Null when the order gives the record no runtime address — the read came from a plugin outside the
    /// active order, or the tables could not be built (<see cref="LoadOrderResolver.IndexView.RuntimeFormIdOf"/> says
    /// when). Carried per outcome because the light index moves whenever the order does.</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why this record has no runtime FormID, when the order can address the plugin but not the record —
    /// today, a light-flagged plugin that was never compacted. Rendered where the form would have gone, so the
    /// answer is never a silently missing field.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this outcome — the one place the two halves are set, so a
    /// lane cannot keep one and drop the other.</summary>
    public ReadOutcome WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };

    /// <summary>The resolver and view this outcome was answered from, carried beside <see cref="Epoch"/> so the
    /// render's conflict-tree fill reads the same build the stamp names. Internal render plumbing.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    /// <summary>Which fields in <see cref="Record"/> carry the owned-child annotation, each with its
    /// <see cref="OwnedChildShape"/>, so a response render can state the clause once over the fields it actually
    /// emitted and name them. Null when this read annotated nothing.
    /// <para>Carried structurally rather than recovered by scanning the rendered prose for a marker, and carrying the
    /// paths rather than a bool: a clause that merely knows something was annotated cannot tell whether that
    /// something survived the medium's own truncation.</para></summary>
    public IReadOnlyDictionary<string, OwnedChildShape>? OwnedChildFields { get; init; }

    /// <summary>Did this read annotate anything at all — the cheap question, for callers that only need to know
    /// whether a clause is POSSIBLE (the budget reservation) rather than which fields it would name.</summary>
    public bool OwnedChildNoted => OwnedChildFields is { Count: > 0 };

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
    bool WhereWinner = false, string? WhereSourceNote = null)   // WhereWinner means the match decided on the live winner; WhereSourceNote carries the type=-scope redundancy note
{
    /// <summary>The captured build the scan ran over. The render stamps it into the in-band accounting so paged
    /// windows are checkably from the same build. Null on the pre-scan refusals.</summary>
    public string? Epoch { get; init; }

    /// <summary>Plugins the winner scan could not open, by filename. A zero-match answer with one of these is bounded
    /// by the lock, not by the filter, and the render must not tell the caller otherwise.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    /// <summary>The scan's pinned resolver and view, carried so the render's per-match fills — detail bodies,
    /// summaries, conflict trees — read off the same build the scan matched and <see cref="Epoch"/> names. Without
    /// it a freshness rebuild landing mid-render would make the response a single-build claim it does not satisfy.
    /// Pure data: an immutable snapshot reference holding no handles, and never serialized.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>One row of a cross_plugin_query <c>group_by=</c> aggregation: a group key (winner plugin / record type /
/// defining plugin) and how many matches fell in it. Emitted instead of per-match lines when group_by is set.</summary>
public sealed record GroupCount(string Key, int Count);

/// <summary>A compact, header-only record summary (no field dump) — the per-match line cross_plugin_query emits
/// by default. <see cref="Error"/> non-null ⇒ the winner couldn't be summarised (named, recoverable).</summary>
public sealed record RecordSummary(FormKey FormKey, string Type, string? EditorId, string Winner, int OverrideDepth, string? Error)
{
    /// <summary>The runtime FormID of this row's record in the build that answered — the same identity the detail
    /// lanes print, so a scan row a modder takes to the console carries the form the console wants.</summary>
    public string? RuntimeFormId { get; init; }

    /// <summary>Why the row has no runtime FormID — see <see cref="ReadOutcome.RuntimeFormIdNote"/>.</summary>
    public string? RuntimeFormIdNote { get; init; }

    /// <summary>Carry a resolved runtime address onto this row; the one place the two halves are set.</summary>
    public RecordSummary WithRuntime(RuntimeAddress a) => this with { RuntimeFormId = a.FormId, RuntimeFormIdNote = a.Note };
}

/// <summary>The MATERIALISED conflict tree the render layer consumes — each touching plugin's name + the fields read
/// off its own body, in priority order (winner last). Built by <see cref="LoadOrderService.ResolveTreePinned"/> with the
/// per-call session already disposed, so it carries NO live overlay (Option B — the renderer never holds a handle).</summary>
public sealed record ConflictTreeView(IReadOnlyList<ConflictNodeView> Nodes,
                                      IReadOnlyList<ChildDeclarers> ChildDeclarers)
{
    public ConflictNodeView Winner => Nodes[^1];
}

/// <summary>The precise owned-child answer for one child-bearing field of one record:
/// which of the record's providers declare child records there, and which could not be read.
///
/// <para><see cref="Declaring"/> empty with <see cref="Unreadable"/> empty is the answer the cheap tier can never
/// give — nobody declares anything here — and it is rendered as a sentence, never as an omitted line.</para></summary>
public sealed record ChildDeclarers(string Field, OwnedChildShape Shape,
                                    IReadOnlyList<string> Declaring, IReadOnlyList<string> Unreadable);

/// <summary>One node of a <see cref="ConflictTreeView"/>: the plugin name + that plugin's record fields (already read).</summary>
public sealed record ConflictNodeView(string Plugin, RecordFields Record);

/// <summary>The data behind housecarl_load_order_status. <see cref="Composition"/> is the fresh enabled/disabled picture;
/// <see cref="ResolvedPluginCount"/> + <see cref="Warnings"/> are the resolver's actual last-build state;
/// <see cref="ProfileChanged"/> is true only when a refresh was attempted but is still pending (e.g. MO2 was mid-write) —
/// houseCARL re-reads automatically on the next tool call; no restart. <see cref="ExcludedPlugins"/> (name → reason) are
/// plugins dropped from the index this build (unopenable, or carrying a record Mutagen can't parse) — surfaced so the
/// user can fix/remove them.</summary>
public sealed record LoadOrderStatusData(
    Mo2Composition Composition,
    IReadOnlyList<string> Warnings,
    int ResolvedPluginCount,
    int MaxPlugins,
    bool ProfileChanged,
    string ProfileDir,
    string ProfileName,         // the ACTIVE profile (instance mode: MO2's selected_profile; explicit: the dir name) — captured under the gate, not re-derived at render
    string? InstanceDir,        // the resolved MO2 instance folder houseCARL is pointed at; null ⇒ explicit-paths / unconfigured mode
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Epoch = null);      // the resolver's current build fingerprint (SPEC §2.1.1) — the status line names it so a caller can match responses/artifacts to the build; nullable like every other carrier

/// <summary>The data behind housecarl_update_status: MO2's own local Nexus update cache read from meta.ini, with no
/// network. <see cref="Entries"/> is one row per Nexus-linked mod (installed vs newest version, modid, enabled state);
/// <see cref="UntrackedCount"/> is how many mod folders were skipped as not Nexus-linked (no meta.ini or no modid).
/// <see cref="Problems"/> carries any read faults, such as a missing mods folder, never a silent empty.</summary>
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
/// a null Composition is the "not found" case (AvailableProfiles names the real options, never a silent empty).
/// <see cref="Warnings"/> carries any notes from reading the inspected profile (e.g. a missing modlist.txt — so a
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
/// drive-rooted or '..'-escaping path — named per path, never failing the batch). <see cref="Hit"/> is null iff
/// <see cref="Error"/> is set.
/// <para><see cref="PrefixSuggestions"/> — on an ABSENT answer only, the root-prefixed forms of this path that
/// a real active mod or BSA DOES provide (<see cref="AssetPathHint"/>), for the common case of a path taken straight
/// off a record and therefore missing its <c>meshes\</c> / <c>textures\</c> root. Verified by re-resolution, so a
/// suggestion always names a file that exists; empty when there is nothing honest to offer.</para></summary>
public sealed record AssetPathResult(string RelPath, AssetHit? Hit, string? Error, IReadOnlyList<string>? PrefixSuggestions = null);

/// <summary>The data behind housecarl_asset_status: one <see cref="AssetPathResult"/> per queried path, plus the
/// build-level caveats — <see cref="BsaFailures"/> (archives that couldn't be read) and <see cref="ReadIncomplete"/>
/// (an Exists=false answer may be wrong because a BSA failed to read) — and <see cref="Warnings"/> from archive
/// discovery (e.g. a Skyrim.ini that couldn't be found, so base-game BSAs weren't scanned). <see cref="ProfileName"/>
/// names the active profile the answer describes.
/// <para><see cref="SelectorNotes"/> carries what each <c>under=</c> directory / glob selector had to say for itself
/// (a selector that matched nothing, or was rejected), <see cref="Total"/> is how many paths the whole selection named
/// before paging, <see cref="Offset"/> where the rendered window starts, and <see cref="Limit"/> the window size the
/// caller asked for (0 = none), which the next-page advice repeats so a caller following it keeps paging. A negative
/// <see cref="Total"/> means nothing paged — the results ARE the selection.</para></summary>
public sealed record AssetStatusData(
    IReadOnlyList<AssetPathResult> Results,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName,
    IReadOnlyList<string>? SelectorNotes = null,
    int Total = -1,
    int Offset = 0,
    int Limit = 0)
{
    /// <summary>How many paths the selection named — <see cref="Results"/>'s own count when nothing paged.</summary>
    public int Selected => Total < 0 ? Results.Count : Total;
}

/// <summary>One provider of an SKSE-layer file: the mod / "overwrite" / "Data" / BSA-filename, and whether it's a "loose" file or
/// a "BSA" entry. The winner-first-then-losers ordering lives in <see cref="SkseFileEntry.Providers"/>.</summary>
public sealed record SkseProvider(string Name, string Kind);

/// <summary>One file found under Data\SKSE\Plugins in the active load order (housecarl_skse findings='inventory'). <see cref="Group"/> is the
/// immediate subfolder it sits in ("" = top level) — the derived render-grouping key. <see cref="Providers"/> is the FULL conflict
/// chain — every mod that ships this exact file, WINNER FIRST then the losers in precedence order (the same winner→loser
/// transparency the asset tools give), each tagged loose/BSA; empty ⇒ nothing active provides it. <see cref="Plugin"/> is the tier-C
/// static manifest, set ONLY for a <c>.dll</c> whose winning copy is loose (null for configs and for a BSA-only/unresolved DLL);
/// <see cref="Note"/> carries the reason when a DLL has no readable manifest or isn't loader-scoped.</summary>
public sealed record SkseFileEntry(
    string RelPath,
    string FileName,
    string Group,
    IReadOnlyList<SkseProvider> Providers,
    SksePluginReader.SksePluginInfo? Plugin,
    string? Note,
    SksePeekResult? Peek = null)
{
    /// <summary>The string peek of this DLL's image (<c>peek=true</c>), or null when not requested / not a loose
    /// DLL. Computed ONLY for entries the peek filter matched — the scan reads the whole image, so it is opt-in per-DLL
    /// by design. The import half needs no flag and lives on <see cref="SksePluginReader.SksePluginInfo.Imports"/>.</summary>
    public SksePeekResult? Peek { get; init; } = Peek;

    /// <summary>Whether this DLL entry matches a user <c>filter=</c> — the one predicate, shared by the renderer's
    /// filtered view and the service's peek gate. Shared on purpose: two hand-kept copies would drift, and a drift
    /// here means peeking a different DLL than the one rendered. Matches filename, winning provider, subfolder, or
    /// the declared plugin name and author, case-insensitively.</summary>
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

/// <summary>The data behind housecarl_skse findings='inventory': the SKSE-plugin layer of the active load order — <see cref="Dlls"/> (each a
/// plugin DLL with its winning provider + static manifest) and <see cref="Configs"/> (their .ini/.toml/.json/.yaml with the
/// winning provider), plus <see cref="OtherFileCount"/> (uncategorized files like .pdb/.txt, counted not listed). The build-level
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
    /// peek, which cross-checks a DLL's embedded plugin names against it. <c>null</c> ⇒ NOT RESOLVED (the
    /// profile's plugin lists were missing or unreadable), so a renderer must NOT call any embedded name "absent from
    /// the load order" (an unasked question has no answer). Never handed over EMPTY — see the producer.</summary>
    public IReadOnlySet<string>? ActivePlugins { get; init; } = ActivePlugins;

    /// <summary>Whether the caller asked for a peek. Distinct from "any entry HAS a peek": a filter can match
    /// only configs, or only BSA-only DLLs, and then the flag was honored with nothing to show — which the renderer
    /// must SAY rather than silently drop.</summary>
    public bool PeekRequested { get; init; } = PeekRequested;
}

/// <summary>The load-order verdict for one reference an SKSE config declares (housecarl_skse findings='config').</summary>
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
/// <see cref="Verdict"/> and a <see cref="Detail"/> line: the resolved FormKey for OK, the reason for a dead or unparseable verdict.</summary>
public sealed record SkseAuditedRef(HousecarlCore.SkseConfigRef Ref, SkseRefVerdict Verdict, string? Detail);

/// <summary>One config file's audit: its VFS provenance (winning provider + the full winner-first conflict chain — only the
/// WINNER is read, the losers are shown for transparency), every reference it declares with a verdict, and a named
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

/// <summary>The data behind housecarl_skse findings='config': every SKSE-plugin config with the references it
/// declares resolved to OK / PLUGIN MISSING / DANGLING / UNPARSEABLE, plus the build-level caveats
/// (<see cref="BsaFailures"/> / <see cref="ReadIncomplete"/> / <see cref="Warnings"/>) and the active <see cref="ProfileName"/>.</summary>
public sealed record SkseConfigAuditData(
    IReadOnlyList<SkseConfigFileAudit> Files,
    int ConfigCount,
    IReadOnlyList<string> BsaFailures,
    bool ReadIncomplete,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>Who implements a native class's declarations (housecarl_skse findings='pairing').</summary>
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

/// <summary>The pairing-evidence rung a THIRD-PARTY class landed on, by evidence strength.</summary>
public enum NativePairingRung
{
    /// <summary>The winning .pex's own provider mod ships ≥1 candidate DLL — the strong co-shipment signal.</summary>
    SameMod,
    /// <summary>A mod elsewhere in the .pex's conflict chain ships the DLL — the bundling case (a patch mod wins the
    /// script file; the framework mod beneath ships the implementation).</summary>
    ChainMod,
    /// <summary>No mod shipping this class's file (winner or chain) ships any candidate DLL. A VERIFY flag, never
    /// "broken" — a declaration copy of an absent framework lands here, but registration is runtime behavior.</summary>
    Unpaired,
}

/// <summary>One candidate DLL a paired mod ships: its VFS identity, the winning copy's manifest (loose winners
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
/// facts are derived from the one <see cref="Providers"/> list (hand-kept
/// copies of a derivable fact drift). Deadness has exactly one owner — the renderer's Judge/BestFate, which also
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

/// <summary>A .pex whose winning copy could not be parsed — a NAMED note, never a silent skip.</summary>
public sealed record NativeUnreadablePex(string RelPath, string? WinningProvider, string Reason);

/// <summary>The data behind housecarl_skse findings='pairing': every native-declaring class classified and (for third
/// parties) paired, the scan accounting (<see cref="PexScanned"/> total compiled scripts examined), the unreadable
/// notes, whether an skse64 loader is visible (<see cref="SkseLoaderSeen"/> — the SKSE-CORE sanity note; tri-state:
/// null = the check itself failed, "could not check", never rendered as a definite absence), the installed game
/// runtime when resolvable (<see cref="InstalledRuntime"/>, null = unknown → version-LOCKED findings degrade to
/// "verify"), and the build-level caveats.</summary>
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

/// <summary>The data behind the whole-layer SkyPatcher scan: the ordered discovery scan, the
/// per-folder INI-vs-INI set collisions, the three ITM classes (intra-file dead writes, cross-INI
/// duplicates, no-op writes), and the build-level caveats.</summary>
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
public sealed record NifProvider(string Name, string Kind, bool OffOrder = false, bool OwnerEnabled = false)
{
    /// <summary>The provenance line when these bytes came out of a copy the game is NOT loading — a mod folder MO2
    /// does not tick, or an enabled mod's root archive no active plugin binds. Null for an in-order provider. Reading
    /// such a copy is legitimate (naming the mod is the consent), but a response that did not SAY so would read as
    /// "this is what the game shows", which is the one thing it is not.</summary>
    public string? Provenance => OffOrder ? WriteSentences.PlaceSourceOffOrder(Name, OwnerEnabled) : null;

    /// <summary>The spelling every listing prints — the name inside a delimiter a Windows name cannot contain, with
    /// the kind outside it, through the one formatter the asset surface uses. The printed token is the token
    /// <c>mod=</c> accepts, so a caller can copy it back verbatim (#340).</summary>
    public string Text => HousecarlCore.AssetSourceSelection.Describe(Name, Kind);
}

/// <summary>The per-path data behind housecarl_nif_inspect: the VFS resolution of ONE mesh path joined to the
/// format-level <see cref="HousecarlCore.NifInspect"/> of the copy that was read. <see cref="Inspected"/> is the
/// provider whose bytes were parsed (the winner, or the <c>mod=</c>-named copy); <see cref="Providers"/> is the FULL
/// winner→loser chain (asset-tool parity), <see cref="Ambiguous"/> flags file-layer contention. <see cref="Absent"/>
/// marks the no-provider outcome specifically, so the renderer can hedge THAT error at point of use on the
/// batch-level scan caveats (an ABSENT is only authoritative when the scan was complete — asset_status parity).
/// Exactly one of <see cref="Inspect"/> (the mesh model) and <see cref="Error"/> (ABSENT / bad path / unreadable /
/// parse-refused — all named) is set on any given result. The batch-level caveats (BSA failures, discovery
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
/// build-level caveats shared by the whole batch (one asset capture pins every path): <see cref="BsaFailures"/>
/// (archives that couldn't be read this build — an ABSENT result may be incomplete), discovery
/// <see cref="Warnings"/>, and the active <see cref="ProfileName"/>.</summary>
public sealed record NifInspectBatchData(
    IReadOnlyList<NifInspectData> Results,
    IReadOnlyList<string> BsaFailures,
    IReadOnlyList<string> Warnings,
    string ProfileName);

/// <summary>The data behind housecarl_nif_set: the VFS resolution joined to the verified write outcome. Exactly one of
/// {<see cref="Report"/> (a verified write happened)}, {<see cref="Error"/> (a named refusal — NOTHING written)},
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

/// <summary>One asset to PLACE (housecarl_place). <see cref="AssetPath"/> is the resolved Data-relative
/// DESTINATION (the tool computes it from a FormID+slot for FaceGen, or takes a raw path). <see cref="Source"/> is the
/// copy to place — a Data-relative path resolved through the VFS, a fully-qualified loose file path,
/// "&lt;archive.bsa&gt;|&lt;entry&gt;", or a fully-qualified ".bsa" path (entry := AssetPath); null/blank ⇒ the VFS lane
/// pointed at the destination path. <see cref="SourceProvider"/> picks the pole for a VFS source: a provider NAME on
/// its own, or the sigiled winner token (<see cref="AssetSourceChoice.WinnerToken"/> — a bare name always means a
/// provider of that name, so the two spaces cannot collide); null/blank ⇒ the sole provider, with contention refused
/// per-asset. A Source naming a DIFFERENT path from AssetPath is a RENAME — the mechanism behind carrying one
/// NPC's baked facegen onto another's FormID path; the same path is not, and renders without the rename prefix.</summary>
public sealed record PlaceRequest(string AssetPath, string? Source, string? SourceProvider = null);

/// <summary>One placed asset's outcome. <see cref="Placed"/> false ⇒ <see cref="Error"/> names why (recoverable, per-asset
/// per asset). <see cref="CurrentWinner"/> is the source that currently wins the VFS for this path (the sort target — the placed
/// copy does NOT win until the fresh mod is enabled + sorted above it), or null if nothing provided it before.</summary>
public sealed record PlaceResult(string AssetPath, bool Placed, long Bytes, string? SourceDesc, string? CurrentWinner, string? Error)
{
    /// <summary>The mod folder these bytes were read out of when it is NOT one the active profile includes — the
    /// off-order source lane. Null for every read served by the active order. Non-null is a fact the response must
    /// state: the bytes are the ones the caller named, out of a mod the game is not currently loading.</summary>
    public string? SourceOffOrderProvider { get; init; }

    /// <summary>Whether that off-order mod is one MO2 TICKS — which of the two off-order reasons applies. True means
    /// the bytes came out of a root archive no active plugin binds, not out of an unticked mod, and the response has
    /// to say the one that is true.</summary>
    public bool SourceOffOrderOwnerEnabled { get; init; }

    public static PlaceResult Fail(string assetPath, string error, string? currentWinner = null)
        => new(assetPath, false, 0, null, currentWinner, error);
}

/// <summary>The outcome of housecarl_place. <see cref="Error"/> non-null ⇒ the whole call was rejected
/// before any placement (unconfigured, an into= folder houseCARL doesn't own, the asset layer wouldn't build). Else
/// <see cref="Results"/> is per-asset; <see cref="ModFolder"/> is the houseCARL mod the placed files landed in (null when
/// none placed); <see cref="Warnings"/> carries the asset-discovery caveats; <see cref="LeftoverFolder"/> names a
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
/// A leftover path (a fresh folder kept because the write half-landed) is folded into <see cref="Error"/>.</summary>
public sealed record SeqOutcome(
    bool Success, string? Error, string? SeqPath, string? ModFolder,
    IReadOnlyList<HousecarlCore.SeqFile.SeqQuest> Quests, string PluginFileName, bool WroteIntoPluginFolder)
{
    /// <summary>Where the source plugin resolved from: "direct path", or the located hit's own label (its mod folder and state). A .seq is derived from
    /// ONE file's records, so which copy was read is load-bearing — a disabled folder's older copy yields a
    /// different quest set than the served one, silently, unless the arm is stated. Null on a refusal taken before
    /// the source resolved.</summary>
    public string? ResolvedFrom { get; init; }

    /// <summary>The absolute path the source resolved TO — the second half of the arm statement (the label says
    /// which layer, this says which file).</summary>
    public string? PluginPath { get; init; }

    /// <summary>The destination already held EXACTLY these bytes, so NOTHING was written (<see cref="SeqPath"/>
    /// names the file that was already correct). A success, and a DISTINCT one: "written" and "already current" are
    /// different facts about the disk, and collapsing them would make a skipped write indistinguishable from a done
    /// one. False on every path that actually wrote.</summary>
    public bool Unchanged { get; init; }

    /// <summary>The byte-identical destination was OLDER than the plugin, so its timestamp was stamped forward
    /// without rewriting a byte. The dialogue check judges .seq staleness by mtime, so a skipped write would
    /// otherwise leave that lint permanently calling a byte-perfect file stale — two tools contradicting each other
    /// about one file. False when no stamp was needed (the file was already newer) or nothing was skipped.</summary>
    public bool TimestampRefreshed { get; init; }

    /// <summary>The write REPLACED a file that was already there, rather than creating one. On the
    /// <c>output_dir=</c> lane that file can be the mod's OWN shipped <c>.seq</c>, and houseCARL keeps no backup, so
    /// "wrote" and "replaced yours" are different facts about the disk and are reported as such.</summary>
    public bool Replaced { get; init; }

    /// <summary>The replaced file held EXACTLY the bytes just written, so nothing was lost. Only reachable when
    /// the byte-identical short-circuit was taken and its timestamp refresh then FAILED, sending an unchanged file
    /// down the write path: without this the response cries "OVERWRITTEN, no backup is kept"
    /// about a file it re-wrote identically.</summary>
    public bool ReplacedSameBytes { get; init; }

    /// <summary>The caller named <c>output_dir=</c>, so the .seq landed in a folder the USER owns and no
    /// houseCARL mod folder was cut. Drives the confirmation: "enable this houseCARL mod in MO2" is the wrong next
    /// step for a file written into the user's own mod.</summary>
    public bool UserChoseOutput { get; init; }

    /// <summary>The note for an <c>output_dir=</c> that neither MO2 nor the game reads SEQ files from. The
    /// .seq is correct; the engine will never see it, and every start-game-enabled quest in the plugin stays silently
    /// dead until it moves. Null when the destination deploys (and on every non-output_dir lane, which lands in a
    /// houseCARL mod folder by construction).</summary>
    public string? DeployWarning { get; init; }

    public static SeqOutcome Fail(string error)
        => new(false, error, null, null, Array.Empty<HousecarlCore.SeqFile.SeqQuest>(), "", false);
}

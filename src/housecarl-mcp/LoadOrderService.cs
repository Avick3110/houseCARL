using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Owns the load-order resolver's lifecycle and is the one place the tools reach the core engines; contract in docs/architecture/load-order-service.md.</summary>
public sealed partial class LoadOrderService : IDisposable, IAssetHost
{
    string? _instanceDir;                          // INSTANCE-mode source of truth; null in explicit/unconfigured mode
    string _dataDir;                               // DERIVED (instance mode) or configured (explicit); mutable for a live profile switch
    string _modsDir;
    string _profileDir;
    string _profileName;                           // the active profile (instance mode: from selected_profile)
    string _overwriteDir = "";                     // MO2's overwrite layer (instance mode: derived; explicit mode: none)
    bool _configured;                              // false ⇒ tools return the trained prompt instead of resolving
    readonly UserConfigStore _store;               // the sole owner of houseCARL.user.json (MO2 instance dir + tool paths)
    bool IAssetHost.IsInPlaceAcknowledged(string path) => _store.IsInPlaceAcknowledged(path);
    readonly int _maxPlugins;
    readonly object _gate = new();
    // Serializes every plugin write's resolve, stage and commit; contract in docs/architecture/load-order-service.md.
    readonly object _writeGate = new();
    object ILoadOrderHost.WriteGate => _writeGate;
    LoadOrderResolver? _resolver;
    CorpusRulebook? _rulebook;
    IReadOnlyList<string> _orderWarnings = Array.Empty<string>();
    // The VFS-aware asset resolver, built lazily on an asset query and dropped when the active profile changes.
    AssetResolver? _assetResolver;
    IReadOnlyList<string> _assetWarnings = Array.Empty<string>();   // discovery warnings from the asset build (e.g. a Skyrim.ini we couldn't find → base BSAs unscanned)
    IReadOnlyList<ActiveArchive> _activeArchives = Array.Empty<ActiveArchive>();   // active BSAs behind the current asset build (archive → owning plugin); swapped with _assetResolver
    IReadOnlyList<string> _enabledModsAtBuild = Array.Empty<string>();             // enabled mods behind the current asset build; the loader scan walks these mods' Root folders, from the same capture as the view rather than a second profile read
    // Freshness baselines are last-seen FileStamps compared by value; contract in docs/architecture/load-order-resolver.md.
    FileStamp[] _profileStamps = new FileStamp[ProfileFileNames.Length];   // per ProfileFileNames, recorded at each order build
    FileStamp _iniStamp;                                                   // ModOrganizer.ini (instance-mode profile-switch baseline)
    IReadOnlyList<string> _resolvedPaths = Array.Empty<string>();   // ordered paths the current snapshot was built from (the cheap "did the order actually change?" check)
    // Set when a refresh found the profile changed but could not re-read it; the two lanes' split is in
    // docs/architecture/load-order-service.md.
    ProfileUnreadableException? _profileHeld;

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

    /// <summary>INSTANCE mode (product default): derive the roots and active profile from ONE MO2 instance folder; a null or blank path is UNCONFIGURED.</summary>
    public static LoadOrderService WithInstance(string? instanceDir, int maxPlugins, UserConfigStore store)
        => new(string.IsNullOrWhiteSpace(instanceDir) ? null : instanceDir.Trim(),
               "", "", "", configured: !string.IsNullOrWhiteSpace(instanceDir), maxPlugins, store);

    /// <summary>EXPLICIT mode (dev override): the three roots are configured directly; no ModOrganizer.ini is read.</summary>
    public static LoadOrderService WithExplicitPaths(string dataDir, string modsDir, string profileDir, int maxPlugins, UserConfigStore store)
        => new(null, dataDir, modsDir, profileDir, configured: true, maxPlugins, store);

    /// <summary>Test seam: drive the service-layer query logic on a prebuilt resolver with no MO2 profile on disk. Never used by the product.</summary>
    internal static LoadOrderService ForGuard(LoadOrderResolver resolver, UserConfigStore store)
    {
        var svc = new LoadOrderService(null, "", "", "", configured: true, maxPlugins: 0, store);
        svc._resolver = resolver;
        return svc;
    }

    public void Dispose()
    {
        lock (_gate) { _resolver?.Dispose(); _resolver = null; _assetResolver?.Dispose(); _assetResolver = null; }
    }

    /// <summary>The write pre-flight rulebook (corpus.json), loaded once from an absolute CorpusPath.</summary>
    CorpusRulebook Rulebook => _rulebook ??= CorpusRulebook.Load();

    /// <summary>One captured index build, for a <see cref="FormIdDoor"/> resolving a runtime FormID.</summary>
    internal LoadOrderResolver.IndexView CaptureView() => Resolver.Capture();

    /// <summary>One captured build plus the resolver it came from, for a lane that opens an overlay session against it.</summary>
    internal ViewPin CapturePin()
    {
        var r = Resolver;
        return new ViewPin(r, r.Capture());
    }

    (ViewPin Pin, SkyPatcherAssets Assets) IAssetHost.CapturePinAndAssets(Action? afterPin)
    {
        lock (_gate)
        {
            var pin = CapturePin();
            afterPin?.Invoke();
            return (pin, new SkyPatcherAssets(AssetsNoProfileRefreshLocked().Capture(), AssetWarningsLocked(), _profileName));
        }
    }

    /// <summary>A FormID door for a tool body with no captured view of its own — see <see cref="FormIdDoor"/>.</summary>
    internal FormIdDoor OpenFormIdDoor() => FormIdDoor.For(this);

    /// <summary>The same door for a WRITE verb's tokens, which refuses a runtime FormID.</summary>
    internal FormIdDoor OpenWriteFormIdDoor() => FormIdDoor.ForWrite(this);

    /// <summary>The resolver, built on first access and kept fresh on every later one; throws if the roots yield no plugins.</summary>
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
                    var profileStamps = StatProfileFiles();      // stat BEFORE the read: a profile write during the build is caught next call, not missed
                    var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir);
                    _orderWarnings = order.Warnings;
                    var paths = order.OrderedPaths;
                    if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();
                    if (paths.Count == 0)
                        throw new InvalidOperationException(
                            $"No active plugins resolved from the MO2 profile. ProfileDir='{_profileDir}', " +
                            $"ModsDir='{_modsDir}', DataDir='{_dataDir}'. {order.Warnings.Count} warning(s). Check " +
                            "HouseCarl config and that MO2 has written loadorder.txt/modlist.txt (a refresh/re-sort in MO2).");
                    // A kept asset build must not be stranded by advancing the baseline here;
                    // contract in docs/architecture/load-order-service.md.
                    bool assetBuildIsBehind = _profileHeld is not null || _resolvedPaths.Count == 0;
                    _resolver = LoadOrderResolver.Build(paths, ExplainPluginAbsence);
                    _resolvedPaths = paths;
                    _profileStamps = profileStamps;
                    if (assetBuildIsBehind) { InvalidateAssetResolver(); _profileHeld = null; }
                }
                else if (Monitor.TryEnter(_writeGate))
                {
                    // Lazy freshness each call, deferred while a write is in flight;
                    // contract in docs/architecture/load-order-service.md.
                    try
                    {
                        RefreshOnProfileChange();     // lazy profile-membership refresh on this call (cheap check first)
                        // A profile it could not re-read is refused here rather than answered off a superseded index.
                        if (_profileHeld is { } held) throw new ProfileUnreadableException(held.ProfilePath, held);
                        _resolver.RefreshIfStale();   // plugin-CONTENT freshness: cheap stat sweep; rebuilds if a plugin's bytes changed
                    }
                    finally { Monitor.Exit(_writeGate); }
                }
                return _resolver;
            }
        }
    }

    LoadOrderResolver ILoadOrderHost.Resolver => Resolver;

    // ---- VFS asset resolution (housecarl_asset_status) --------------------------------------------------

    /// <summary>The VFS-aware asset resolver, built on first asset query and kept fresh after — the asset twin of <see cref="Resolver"/>, which it never forces. Takes <see cref="_gate"/>.</summary>
    AssetResolver Assets
    {
        get
        {
            lock (_gate)
            {
                if (!_configured) throw NotConfigured();           // fresh install → the tool returns the prompt for the MO2 path instead
                EnsurePathsDerived();                              // derive the roots on first use (instance mode)
                // Profile freshness, shared with the record path and deferred behind an in-flight write the same way.
                if (Monitor.TryEnter(_writeGate))
                {
                    try { RefreshOnProfileChange(); }
                    finally { Monitor.Exit(_writeGate); }
                }
                return AssetsNoProfileRefreshLocked();
            }
        }
    }

    /// <summary>The asset resolver for the profile already resolved, with no profile re-read; caller holds <see cref="_gate"/>.</summary>
    AssetResolver AssetsNoProfileRefreshLocked()
    {
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

    AssetCapture ILoadOrderHost.CaptureAssets()
    {
        lock (_gate) { EnsurePathsDerived(); return AssetCaptureLocked(Assets.Capture()); }
    }

    (AssetCapture Assets, LoadOrderResolver.IndexView Index) IAssetHost.CaptureAssetsAndIndex()
    {
        lock (_gate)
        {
            EnsurePathsDerived();
            var view = Assets.Capture();
            var index = Resolver.Capture();   // before the warnings: a first index build can clear the held-profile note they carry
            return (AssetCaptureLocked(view), index);
        }
    }

    /// <summary>The rest of an asset capture around a view just taken; caller holds <see cref="_gate"/>.</summary>
    AssetCapture AssetCaptureLocked(AssetResolver.AssetView view) =>
        new(view, AssetWarningsLocked(), _profileName, _profileDir, _dataDir, _modsDir, _overwriteDir, _activeArchives, _enabledModsAtBuild);

    // Rows the assets area takes from output, writes and reads, relayed here until those areas are classes (W4 plan ledger).
    RiderFolder IAssetHost.ResolvePatchModFolder(string? patchName, string? into, string defaultStem, RiderNaming? naming) => ResolvePatchModFolder(patchName, into, defaultStem, naming);
    string? IAssetHost.RemoveOrNameRiderResidue(RiderFolder folder) => RemoveOrNameRiderResidue(folder);
    string? IAssetHost.PersistInPlaceConsent(bool owed, string targetPath, string what, string subject) => PersistInPlaceConsent(owed, targetPath, what, subject);
    Dictionary<string, List<Type>> IAssetHost.TypeLookup => TypeLookup;
    string IAssetHost.UnresolvedFormId(LoadOrderResolver.IndexView view, FormKey fk) => UnresolvedFormId(view, fk);

    internal int AbsenceExplanations;   // how many times the explainer has parsed the profile — a test seam for the memo

    /// <summary>MO2's mods root as this service currently has it, or null when it has none yet; taken under the gate.</summary>
    internal string? ModsRootOrNull
    {
        get
        {
            lock (_gate)
            {
                // Instance mode derives the roots lazily, and an instance that will not resolve is not this caller's problem.
                try { EnsurePathsDerived(); } catch { }
                return string.IsNullOrWhiteSpace(_modsDir) ? null : _modsDir;
            }
        }
    }

    string? IAssetHost.ModsRootOrNull => ModsRootOrNull;

    /// <summary>The injected answer to "why is this plugin filename not in the active order?": the profile and the roots
    /// are read FRESH on each call rather than captured, and the count of those reads is <see cref="AbsenceExplanations"/>,
    /// which <c>AbsentMasterLinkTests.ResolveNamesExplainsAnAbsentMasterOncePerPluginNotOncePerDanglingLink</c> holds the
    /// caller's per-plugin memo to. Returns null when nothing can be said, and the refusal falls back to a did-you-mean.</summary>
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

        // The headline case: MO2's left pane says yes, its right pane says no.
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
            // Ticked and provided by an enabled layer yet not indexed — nothing honest left to say, so say nothing.
            return hits.Any(h => h.Enabled)
                ? null
                : $"'{fn}' is ticked in plugins.txt, but no enabled mod, the overwrite folder, or the game Data folder " +
                  "provides the file — the profile is stale (trigger an MO2 refresh / re-sort so it rewrites the profile files).";

        if (hits.Length == 0) return null;           // nothing on disk by that name → a typo; let the suggester answer

        // On disk but the profile never mentions it: the remedy turns on which layer holds it, read from the mod list.
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

    /// <summary>Build the asset resolver from the current roots: the active BSAs and the enabled-mod priority list, off one static profile read. Caller holds <see cref="_gate"/>.</summary>
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

    /// <summary>Every asset lane's warnings: the build's own, plus the kept-build sentence when a profile change could not be re-read. Caller holds <see cref="_gate"/>.</summary>
    IReadOnlyList<string> AssetWarningsLocked()
    {
        if (_profileHeld is null) return _assetWarnings;
        var said = new List<string>(_assetWarnings.Count + 1) { ProfileHeldNote(_profileHeld) };
        said.AddRange(_assetWarnings);
        return said;
    }

    /// <summary>The sentence an answer served off a kept build carries.</summary>
    static string ProfileHeldNote(ProfileUnreadableException held) =>
        $"the load order changed on disk and could not be re-read — '{Path.GetFileName(held.ProfilePath)}' is held " +
        "open by another process (MO2 holds these while it re-sorts), so this answer is off the build from BEFORE " +
        "that change. houseCARL re-reads on the next call; run this again once the file is free.";

    /// <summary>Drop the asset resolver so the next asset query rebuilds it — the active mod/archive SET changed. Caller holds <see cref="_gate"/>.</summary>
    void InvalidateAssetResolver() { _assetResolver?.Dispose(); _assetResolver = null; }

    /// <summary>The Papyrus source folders this modlist ships, in MO2's own VFS precedence off <see cref="AssetResolver.LooseRoots"/>; best-effort, and a read that threw sets <c>Failed</c>.</summary>
    public (IReadOnlyList<PapyrusSourceRoot> Roots, string? GameDataSources, string? Warning, bool Failed) PapyrusSourceImportDirs()
    {
        IReadOnlyList<(string Name, string Dir)> roots;
        string dataDir;
        try { lock (_gate) { roots = Assets.LooseRoots; dataDir = _dataDir; } }
        catch (Exception ex)
        {
            // Says what failed and what it costs, and nothing about vanilla — this method cannot check the vanilla sources.
            return (Array.Empty<PapyrusSourceRoot>(), null,
                    "modlist scan: could not read the MO2 modlist to discover Papyrus source folders " +
                    $"({ex.Message}) — none of your installed mods' source folders are on the import path for this compile.",
                    true);
        }
        // The game's own Data root is split out here, where the data dir is known: a compiler-relative check would miss a Stock Game setup.
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

    // ---- decompiler class hierarchy (lazy, cached for process lifetime) ----------------------------------------

    Dictionary<string, string>? _classParents;
    string? _classParentsNote;
    bool _classParentsToppedUp;
    string? _classParentsTopUpMissing;
    int _classParentsGen;                                        // bumped by every invalidate; a scan publishes only if it still matches
    readonly object _classParentsLock = new();

    /// <summary>Test seam: invoked in <see cref="ClassParentsForDecompile"/> after the gate is released and before the cache lock is taken; null in the product.</summary>
    internal Action? BeforeClassParentsPublishForGuard;

    /// <summary>Drop the cached hierarchy whenever <see cref="_modsDir"/> can have changed, since a stale tree's edges could suppress a cast the new order does not justify. Rebuilds lazily.</summary>
    void InvalidateClassParents()
    {
        lock (_classParentsLock)
        {
            _classParents = null; _classParentsNote = null;
            _classParentsToppedUp = false; _classParentsTopUpMissing = null;
            _classParentsGen++;
        }
    }

    /// <summary>The decompiler's child-to-parent class map: the cached vanilla baseline beside the exe, plus loose .psc headers across the MO2 mods tree. The top-up is RETRIED every call until it runs, so a baseline-only map is never cached as complete, and both degraded modes come back named with their cause. A published map is never mutated. Lock order is _gate then _classParentsLock.</summary>
    public ClassParents ClassParentsForDecompile()
    {
        bool configured;
        string? deriveError = null;
        string modsDir;
        int gen;
        lock (_gate)
        {
            configured = _configured;
            // Not fatal here, but the reason is carried out: it is why the top-up below cannot run.
            if (configured)
                try { EnsurePathsDerived(); }
                catch (Exception ex) { deriveError = ex.Message; }
            modsDir = _modsDir;
            lock (_classParentsLock) gen = _classParentsGen;     // taken with modsDir: every write of _modsDir invalidates under the gate
        }
        BeforeClassParentsPublishForGuard?.Invoke();             // test seam; null in the product
        lock (_classParentsLock)
        {
            if (_classParents is null)
            {
                var (edges, note) = HousecarlCore.PapyrusClassParents.LoadBaseline(
                    Path.Combine(AppContext.BaseDirectory, "vanilla-class-parents.json"));
                _classParents = edges;
                _classParentsNote = note;
            }
            if (!_classParentsToppedUp)
            {
                string? missing =
                    // An invalidate since the snapshot: configured, deriveError and modsDir may all be stale, so nothing is scanned or published.
                    gen != _classParentsGen ? "the MO2 instance changed during this call; decompile again to read the new mods folder"
                    : !configured ? "no MO2 instance is configured"
                    : deriveError is not null ? $"the MO2 instance does not resolve ({deriveError})"
                    : string.IsNullOrEmpty(modsDir) ? "the instance has no mods folder"
                    : !Directory.Exists(modsDir) ? $"the mods folder '{modsDir}' does not exist"
                    : null;
                if (missing is null)
                {
                    // Publish-once: the walk fills a COPY, so a concurrent reader never sees a map being written.
                    var topped = new Dictionary<string, string>(_classParents, StringComparer.OrdinalIgnoreCase);
                    var scan = HousecarlCore.PapyrusClassParents.AddFromPscHeaders(topped, new[] { modsDir });
                    if (scan.RootsUnreadable > 0)
                        // Nothing was read from the tree: the copy is dropped and the walk is retried next call.
                        missing = $"the mods folder '{modsDir}' could not be listed";
                    else
                    {
                        _classParents = topped;
                        _classParentsToppedUp = true;
                        if (scan.FilesFailed > 0)
                            missing = $"{scan.FilesFailed} of {scan.FilesSeen} .psc file(s) under '{modsDir}' could not be read";
                    }
                }
                _classParentsTopUpMissing = missing;
            }
            return new ClassParents(_classParents, _classParentsNote, _classParentsTopUpMissing);
        }
    }

    /// <summary>Diagnostic snapshot for housecarl_load_order_status: the fresh profile composition plus the resolver's count, warnings and staleness flag. Forces the lazy build.</summary>
    public LoadOrderStatusData StatusData()
    {
        // The view and the per-build fields beside it are snapshotted under ONE gate hold, so no status line mixes two builds.
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
            view.Epoch, view.ContainedRecordCount);
    }

    /// <summary>Whole-order stats (forces the lazy build). A test seam: the probes warm the lazy index through it. No shipped caller.</summary>
    public (int plugins, int records, int conflicts, int maxDepth, IReadOnlyList<string> loadFailures, string epoch) Stats()
    {
        var view = Resolver.Capture();          // one build for every counter in the line
        return (view.PluginCount, view.RecordCount, view.ConflictCount, view.MaxDepth, view.LoadFailures, view.Epoch);
    }

    /// <summary>The LOCALIZED header flag of ONE plugin, for housecarl_load_order_status' filter= (#376): null when the name is not a plugin at all, else the three-way read, Unreadable included.</summary>
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
        // Only a name the profile lists as a plugin: a mod folder and a typo both have no header to read.
        bool isPlugin = comp.OrderedPluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                        || comp.InactivePluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                        || comp.ImplicitPluginNames.Any(n => n.Equals(pluginName, StringComparison.OrdinalIgnoreCase));
        if (!isPlugin) return null;
        var loc = LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, pluginName, null, offerModParam: false);
        if (loc.Path is { } path) return WriteEngine.PluginIsLocalized(path);
        // Several folders provide the name: MO2 priority already decides which copy serves, so that copy answers.
        if (loc.Ambiguous is { Count: > 0 } hits && hits.FirstOrDefault(h => h.Enabled) is { } served)
            return WriteEngine.PluginIsLocalized(served.Path);
        // Listed as a plugin, and no file behind the name serves it: the flag is not established.
        return LocalizedFlagRead.Unreadable;
    }

    /// <summary>Read MO2's OWN local Nexus update cache — every managed mod's meta.ini Nexus fields — with NO network; only Nexus-linked mods become entries, and a missing mods folder is named.</summary>
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

        // Enabled/disabled from the active profile, outside the gate; explicit-paths mode may have no profile.
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

    /// <summary>Inspect a named profile's composition without switching to it, off the cheap text-only <see cref="Mo2LoadOrder.ReadComposition"/>. Instance mode only; an unmatched name is reported with the available ones.</summary>
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

    /// <summary>The usable profile names under <paramref name="profilesRoot"/>: one subfolder each, skipping folders with no loadorder.txt, sorted case-insensitively. Never throws.</summary>
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

    /// <summary>True if any of the three MO2 profile files' stamps differs from the last build's baseline, compared by value. Caller holds <see cref="_gate"/>.</summary>
    bool ProfileFilesChanged()
    {
        if (_profileDir.Length == 0) return false;                 // test seam / not yet derived — nothing to compare against
        for (int i = 0; i < ProfileFileNames.Length; i++)
            if (FileStamp.Of(Path.Combine(_profileDir, ProfileFileNames[i])) != _profileStamps[i]) return true;
        return false;
    }

    /// <summary>The three profile files' stamps in <see cref="ProfileFileNames"/> order — a build's baseline, statted before the read it baselines. Caller holds <see cref="_gate"/>.</summary>
    FileStamp[] StatProfileFiles()
    {
        var s = new FileStamp[ProfileFileNames.Length];
        for (int i = 0; i < ProfileFileNames.Length; i++) s[i] = FileStamp.Of(Path.Combine(_profileDir, ProfileFileNames[i]));
        return s;
    }

    /// <summary>Lazy freshness, run on each tool call once the snapshot exists: a profile switch in instance mode, else the active profile's own files. Caller holds <see cref="_gate"/>.</summary>
    void RefreshOnProfileChange()
    {
        if (RederiveIfIniChanged()) return;                      // instance mode: a profile switch already re-derived and re-resolved
        if (!ProfileFilesChanged()) { _profileHeld = null; return; }   // matches its baseline again → nothing pending, nothing to say
        ReResolve();
    }

    /// <summary>Instance mode only: re-derive the roots and re-resolve when ModOrganizer.ini changed AND something this resolves from moved; true iff it handled a switch. Caller holds the gate.</summary>
    bool RederiveIfIniChanged()
    {
        if (_instanceDir is null) return false;                  // explicit/override mode — no ini to watch
        var ini = Mo2Instance.IniPath(_instanceDir);
        if (!File.Exists(ini)) return false;                     // missing/mid-replace → keep last good, retry next call
        var iniStamp = FileStamp.Of(ini);                         // stat BEFORE the read: an ini write during/after TryResolve is caught next call
        if (iniStamp == _iniStamp) return false;                  // compared by value — a restored-backup ini carries an older mtime and is a change too
        if (!Mo2Instance.TryResolve(_instanceDir, out var p) || p is null) return false;   // mid-write/invalid → keep last good, retry next call
        _iniStamp = iniStamp;                                     // advance only on a clean read
        bool switched = !PathEq(p.ProfileDir, _profileDir) || !PathEq(p.ModsDir, _modsDir) || !PathEq(p.DataDir, _dataDir)
                        || !PathEq(p.OverwriteDir, _overwriteDir);
        if (!switched) return false;                             // ini touched but nothing we resolve from changed
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        System.Threading.Interlocked.Increment(ref _gameRootsGen);   // the game roots moved → the runtime memo re-probes
        InvalidateClassParents();                                // the mods tree may have moved — drop the cached hierarchy with it
        ReResolve();                                             // a new profile ⇒ the order differs ⇒ ReResolve deep-re-indexes
        return true;
    }

    /// <summary>The cheap re-read against the current roots: re-list the winning paths, deep-re-index only when the set or order changed. Caller holds the gate.</summary>
    void ReResolve()
    {
        var profileStamps = StatProfileFiles();                  // stat BEFORE the read: a write during the re-read is caught next call, not missed
        Mo2OrderResult order;
        // A refresh landing in MO2's profile-rewrite window keeps the built snapshot and does not advance the baseline;
        // contract in docs/architecture/load-order-service.md.
        try { order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir); }
        catch (ProfileUnreadableException ex) { _profileHeld = ex; return; }
        _profileHeld = null;                                     // the re-read got through — nothing is pending any more
        var paths = order.OrderedPaths;
        if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();

        if (paths.Count > 0 && !paths.SequenceEqual(_resolvedPaths, StringComparer.OrdinalIgnoreCase))
        {
            // The set or order genuinely changed: build FIRST so the old snapshot survives a throw, then dispose and swap.
            InvalidateAssetResolver();   // the active mod/archive set changed → the asset resolver rebuilds lazily
            if (_resolver is not null)
            {
                // The rebuild carries the explainer too, or a profile change would drop every refusal to the flat not-found.
                var rebuilt = LoadOrderResolver.Build(paths, ExplainPluginAbsence);
                // The reverse-reference index is derived from plugin bytes, not from this snapshot, so it carries over.
                rebuilt.AdoptReverseIndexFrom(_resolver);
                _resolver.Dispose();
                _resolver = rebuilt;
            }
            _resolvedPaths = paths;
            _orderWarnings = order.Warnings;
            _profileStamps = profileStamps;
        }
        else if (paths.Count > 0)
        {
            // The profile was touched but the resolved order is identical, so no deep re-index; the asset resolver still drops and the baseline advances.
            InvalidateAssetResolver();
            _orderWarnings = order.Warnings;
            _profileStamps = profileStamps;
        }
        // paths.Count == 0 is almost certainly a transient mid-write read: keep the last good snapshot and do not advance.
    }

    /// <summary>Instance mode: on the first build, derive the roots and active profile from ModOrganizer.ini, throwing a message naming what is missing. Caller holds the gate.</summary>
    void EnsurePathsDerived()
    {
        if (_instanceDir is null) return;                        // explicit mode — roots configured directly
        if (_profileDir.Length > 0) return;                      // already derived (a prior build / SetInstance); RederiveIfIniChanged owns later updates
        var iniStamp = FileStamp.Of(Mo2Instance.IniPath(_instanceDir)); // stat BEFORE the read: an ini write during/after Resolve is caught next call
        var p = Mo2Instance.Resolve(_instanceDir);               // throws, naming the missing piece, if this is not a usable instance
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        _iniStamp = iniStamp;
        InvalidateClassParents();                                // _modsDir just gained a value — a cache built before derivation is baseline-only
    }

    static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether houseCARL has an MO2 location to resolve against.</summary>
    public bool IsConfigured { get { lock (_gate) { return _configured; } } }

    /// <summary>The active profile name; "" when unconfigured. For the status surface.</summary>
    public string ProfileName { get { lock (_gate) { return _profileName; } } }

    /// <summary>The game install directory the load order points at — DataDir's parent — or null; null-safe by contract, so the compile lane's hint falls through to the prompt.</summary>
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

    // The runtime memo: a cheap mtime re-validate per call, invalidated by _gameRootsGen; lock order is _runtimeGate then _gate.
    readonly object _runtimeGate = new();
    int _gameRootsGen;
    int _runtimeGen = -1;   // generation the memo was cached at; -1 = never probed
    string? _runtimeExe, _runtimeVersion;
    DateTime _runtimeExeMtime;

    /// <summary>The INSTALLED game runtime version — the SkyrimSE.exe file version the load order runs — or null; candidates are <see cref="CompilerGameDirHints"/>, load-order dir first. Memoized.</summary>
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

    string? IAssetHost.InstalledGameRuntime() => InstalledGameRuntime();

    /// <summary>The game dirs to search for the Creation Kit's compiler, in priority order: the load order's own, then the located real Skyrim SE install. De-duplicated, best-effort.</summary>
    public IReadOnlyList<string> CompilerGameDirHints()
    {
        var hints = new List<string>();
        if (GameDirOrNull() is { } loadOrderGameDir) hints.Add(loadOrderGameDir);
        try
        {
            // The bundled GameFinder locator (Steam/GOG/Xbox) via Mutagen: the real Skyrim SE install, wherever MO2 points.
            if (new Mutagen.Bethesda.Installs.GameLocator().TryGetGameDirectory(
                    Mutagen.Bethesda.GameRelease.SkyrimSE, out var dir) && !string.IsNullOrWhiteSpace(dir.Path))
                hints.Add(NormalizeGameDir(dir.Path));
        }
        catch { /* locator / registry hiccup → just the load-order hint; the prompt still names it */ }
        return hints.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The locator returns the game-install ROOT; step up one level if a future build hands back Data itself.</summary>
    static string NormalizeGameDir(string p)
    {
        var t = p.TrimEnd('\\', '/');
        return Path.GetFileName(t).Equals("Data", StringComparison.OrdinalIgnoreCase) ? (Path.GetDirectoryName(t) ?? t) : t;
    }

    /// <summary>Point houseCARL at an MO2 instance folder: validate it, re-point the live service, and persist the choice. Nothing changes or persists on failure.</summary>
    public (Mo2InstancePaths paths, bool persisted, string? persistError, string? persistNote) SetInstance(string instanceDir)
    {
        // The ini baseline is statted BEFORE Resolve reads the instance, the same discipline as every other baseline here.
        var iniStamp = FileStamp.Of(Mo2Instance.IniPath(instanceDir.Trim()));
        var paths = Mo2Instance.Resolve(instanceDir);            // throws if not a usable MO2 instance — the tool renders the reason
        lock (_writeGate)                                        // an instance switch waits for any in-flight write, so one can never tear across instances
        lock (_gate)
        {
            _instanceDir = paths.InstanceDir;
            _dataDir = paths.DataDir; _modsDir = paths.ModsDir; _profileDir = paths.ProfileDir; _profileName = paths.ProfileName;
            _overwriteDir = paths.OverwriteDir;
            _iniStamp = iniStamp;
            _configured = true;
            _resolver?.Dispose(); _resolver = null;              // force a rebuild against the new instance on the next query
            _assetResolver?.Dispose(); _assetResolver = null;    // the asset resolver rebuilds against the new instance too
            _resolvedPaths = Array.Empty<string>();
            _profileStamps = new FileStamp[ProfileFileNames.Length];   // unset — the next build records fresh baselines against the new profile
            _orderWarnings = Array.Empty<string>();
            InvalidateClassParents();                            // every sibling cache drops on a switch — the hierarchy too
            System.Threading.Interlocked.Increment(ref _gameRootsGen);   // a new instance may be a different game install — the runtime memo must re-probe rather than adjudicate against the old exe
        }
        var (persisted, persistError, persistNote) = PersistInstanceDir(paths.InstanceDir);
        return (paths, persisted, persistError, persistNote);
    }

    /// <summary>Persist the chosen instance dir through the shared <see cref="UserConfigStore"/> (read-modify-write); a write failure is reported rather than swallowed.</summary>
    (bool ok, string? error, string? note) PersistInstanceDir(string instanceDir)
        => _store.Update(c => c.Mo2InstanceDir = instanceDir);

    /// <summary>The prompt shown while unconfigured; one string because <see cref="ConfigPromptOrNull"/> and the <see cref="Resolver"/> getter's backstop must say the same thing.</summary>
    const string NotConfiguredText =
        "houseCARL has no Mod Organizer 2 instance configured yet. Ask the user which MO2 instance folder to use — the " +
        "folder that contains ModOrganizer.ini (for a Wabbajack / portable list, that's the list's install folder). You " +
        "may help locate it, but do NOT silently pick one when more than one MO2 install exists: list the candidates you " +
        "found and let the user choose. State which folder you're using, then call " + ToolNames.SetMo2Instance + " with that path.";

    /// <summary>Tools call this FIRST: the unconfigured prompt as a normal result string, else null — the MCP framework rewrites a thrown exception to a generic message.</summary>
    public string? ConfigPromptOrNull() { lock (_gate) { return _configured ? null : NotConfiguredText; } }

    static InvalidOperationException NotConfigured() => new(NotConfiguredText);

}

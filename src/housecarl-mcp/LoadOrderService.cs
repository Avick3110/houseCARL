using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>
/// Owns the load-order resolver's lifecycle and is the single place the tools reach the core engines.
/// The index build is lazy (deferred to first use, so startup and tools/list are instant), refreshed by a cheap
/// FileStamp sweep on each query, and serialized on one gate because the server dispatches tool calls concurrently.
/// The order is the true active order, read statically from the MO2 profile's loadorder.txt + modlist.txt +
/// plugins.txt — masters first, highest-priority winner last, duplicate plugin names resolved by mod priority.
/// No USVFS and no live MO2 state: the server reads real plugin paths and runs standalone.
/// </summary>
public sealed partial class LoadOrderService : IDisposable
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
    // Freshness baselines are last-seen FileStamps — the same (last-write, length) key the resolver and the asset
    // tables use — compared by VALUE (!=), never wall-clock stamps compared by order: MO2's "Restore Backup" restores
    // a profile file with an OLDER mtime, which an ordered comparison never sees, and a same-mtime rewrite of
    // modlist.txt that changes its length would be invisible to the mtime term alone. Each baseline is statted BEFORE
    // the read it baselines, so a write landing during the read shows up on the next check rather than being absorbed.
    FileStamp[] _profileStamps = new FileStamp[ProfileFileNames.Length];   // per ProfileFileNames, recorded at each order build
    FileStamp _iniStamp;                                                   // ModOrganizer.ini (instance-mode profile-switch baseline)
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

    /// <summary>One captured build together with the resolver it came from, for a lane that has to open an overlay
    /// session against the same resolver it captured — a session from an adjacent build would fetch bodies the
    /// view's epoch does not name.</summary>
    internal ViewPin CapturePin()
    {
        var r = Resolver;
        return new ViewPin(r, r.Capture());
    }

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
                    _resolver = LoadOrderResolver.Build(paths, ExplainPluginAbsence);
                    _resolvedPaths = paths;
                    _profileStamps = profileStamps;
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

    /// <summary>MO2's mods root as this service currently has it, or null when it has none yet. Read-only, taken
    /// under the gate; a caller uses it to recognize a raw path into the mods tree, never to reach into it.</summary>
    internal string? ModsRootOrNull
    {
        get
        {
            lock (_gate)
            {
                // Instance mode derives the roots lazily, so a cold call would otherwise see nothing. An instance that
                // will not resolve is not this caller's problem — it says nothing rather than throwing on a refusal path.
                try { EnsurePathsDerived(); } catch { }
                return string.IsNullOrWhiteSpace(_modsDir) ? null : _modsDir;
            }
        }
    }

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
            view.Epoch, view.ContainedRecordCount);
    }

    /// <summary>The LOCALIZED header flag of ONE plugin, for housecarl_load_order_status' filter= (#376): a localized
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

    /// <summary>True if any of the three MO2 profile files' stamps differs from the last build's baseline — the user
    /// toggled mods or plugins, re-sorted, or restored a backup, so the resolver's set is behind the live profile.
    /// Compared by value (!=), like the resolver's own plugin sweep: a restored backup carries an OLDER mtime, which
    /// an is-newer comparison would miss. Caller holds <see cref="_gate"/>.</summary>
    bool ProfileFilesChanged()
    {
        if (_profileDir.Length == 0) return false;                 // test seam / not yet derived — nothing to compare against
        for (int i = 0; i < ProfileFileNames.Length; i++)
            if (FileStamp.Of(Path.Combine(_profileDir, ProfileFileNames[i])) != _profileStamps[i]) return true;
        return false;
    }

    /// <summary>The three profile files' current stamps, in <see cref="ProfileFileNames"/> order — the freshness
    /// baseline a build records. Stat BEFORE the read it baselines. Caller holds <see cref="_gate"/>.</summary>
    FileStamp[] StatProfileFiles()
    {
        var s = new FileStamp[ProfileFileNames.Length];
        for (int i = 0; i < ProfileFileNames.Length; i++) s[i] = FileStamp.Of(Path.Combine(_profileDir, ProfileFileNames[i]));
        return s;
    }

    /// <summary>Lazy freshness, run on each tool call once the snapshot exists. Two FileStamp signals: in instance mode,
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
    /// FileStamp model as the per-profile-file check. Returns true iff it handled a switch (caller then skips the per-file check).
    /// Tolerates a transient/invalid read (MO2 mid-write): keeps the last good set and retries next call. Caller holds the gate.</summary>
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

    /// <summary>The cheap re-read against the current profile roots: re-list the winning plugin paths from the text
    /// files, and pay the deep re-index only when the resolved set or order actually changed. Caller holds the gate;
    /// <see cref="_resolver"/> is non-null. Used by both freshness signals (active-profile change + profile switch).</summary>
    void ReResolve()
    {
        var profileStamps = StatProfileFiles();                  // stat BEFORE the read: a write during the re-read is caught next call, not missed
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
                // The reverse-reference index is derived from plugin bytes, not from this snapshot, so it carries
                // over: a tick in MO2 rebuilds the resolver, and dropping the index there would re-pay the whole
                // whole-order walk for plugins whose (path, mtime) never changed.
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
            // The profile was touched but the resolved plugin order is identical (e.g. a no-plugin mod toggled), so no
            // deep re-index. A plugin-less toggle still changes the loose roots and active-archive set, so the asset
            // resolver is dropped to rebuild; the freshness baseline advances so the staleness flag clears.
            InvalidateAssetResolver();
            _orderWarnings = order.Warnings;
            _profileStamps = profileStamps;
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
        var iniStamp = FileStamp.Of(Mo2Instance.IniPath(_instanceDir)); // stat BEFORE the read: an ini write during/after Resolve is caught next call
        var p = Mo2Instance.Resolve(_instanceDir);               // throws, naming the missing piece, if this is not a usable instance
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName; _overwriteDir = p.OverwriteDir;
        _iniStamp = iniStamp;
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

    /// <summary>The on-demand whole-topic dialogue-graph validator: resolve <paramref name="fk"/> to its load-order
    /// winner and, when it is a dialogue topic (DIAL), validate that topic's whole graph; when it is a quest (QUST),
    /// fan out to every topic the quest owns. Everything is judged against the resolved winners, which is what the
    /// game sees. The Skyrim-typed walk lives in the core (<see cref="DialogueValidate"/>) so this assembly stays free
    /// of Mutagen.Skyrim; here it just hands core the record resolver and the VFS asset resolver. It never throws over
    /// a verify step: a mid-run resolve or asset failure rides
    /// <see cref="DialogueValidationReport.CheckError"/>, and a not-in-order or wrong-type input is a named
    /// <see cref="DialogueValidationReport.Error"/>.</summary>
    public DialogueValidationReport ValidateDialogue(FormKey fk)
        => DialogueValidate.Run(Resolver, Assets, fk, null, ForceLoadedPluginNames());

    /// <summary>The force-loaded plugin names — base masters aside, the Creation Club and <c>_ResourcePack.esl</c>
    /// plugins the load-order status groups as implicit — for a check that must not blame a modder for content they
    /// did not author. Null, never an empty set, when the MO2 profile cannot be read: a check told "nothing is
    /// force-loaded" would warn on that content, and the caller says which way it then errs.</summary>
    IReadOnlyCollection<string>? ForceLoadedPluginNames()
    {
        var (names, err) = ImplicitPluginNames();
        return err is null ? names : null;
    }

    /// <summary>The merged <c>check</c> surface's dialogue family: <see cref="ValidateDialogue"/> over a seed list,
    /// tallied for one section of a merged response. Deliberately thin — the family's own grammar (seed parse,
    /// cost refusal, seed budget, tally) lives in <see cref="DialogueSweep"/> rather than in this file.</summary>
    public DialogueCheckResult CheckDialogue(IReadOnlyList<string>? seeds, int limit, bool countsOnly = false)
        // Bound LAZILY: the sweep calls this only once it has seeds to validate, so a call with no seeds= refuses
        // without building the index — the rule SweepSharedInput states, and what this family did before it stamped.
        => DialogueSweep.Run(() =>
        {
            // One resolver and one view for the whole call, the contract the sibling families keep: every seed is
            // validated against the same build, so the stamp names the build the response was actually answered from
            // rather than whichever one the last seed happened to catch.
            var resolver = Resolver;
            // The asset resolver is taken once here for the same reason, and where the scripts family takes it: the
            // property re-gates on every read and can rebuild outright, so reading it per seed would let one
            // response's .fuz and .pex verdicts come off two different asset builds under one tally and one stamp.
            var assets = Assets;
            var view = resolver.Capture();
            // The seed door is pinned to that same view: a door of its own would capture a second build on the first
            // runtime FormID, so the seeds could name records from a build other than the one the response stamps.
            // Read once for the whole sweep, for the same reason the resolver and view are: every seed's ownership
            // gate reads one composition, so one response cannot mix two answers to "who force-loads this".
            var forceLoaded = ForceLoadedPluginNames();
            return new DialogueSweep.Binding(fk => DialogueValidate.Run(resolver, assets, fk, view, forceLoaded),
                                             FormIdDoor.On(view).Parse, view.Epoch);
        }, seeds, limit, countsOnly);

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
                                        IReadOnlyList<string>? types = null, IReadOnlyList<string>? findings = null,
                                        bool countsOnly = false, IReadOnlyList<string>? exclude = null,
                                        SweepOffOrderMemo? offOrderMemo = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
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
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            // Membership and locate refusals are decided against THIS captured build and its composition, so they
            // are stamped; a blank name consulted no build and stays unstamped.
            if (SweepOffOrderScope.Split(view, plugins, modsDir, dataDir, overwriteDir, profileDir,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ErrorCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ErrorCheckResult.Fail(splitErr.Message);
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
    internal string? SweepScopeError(IReadOnlyList<string>? formids, string? editoridContains,
                                     IReadOnlyList<string>? types)
        => BuildSweepScope(formids, editoridContains, types).Error;

    /// <summary>Parse the sweep families' shared record-scope params into a <see cref="SweepScope"/>: FormID tokens,
    /// an EditorID substring, and a record type SET resolved through the same type lookup the scan uses. Every
    /// malformed input is a named refusal returned before the sweep starts, never a scope that silently matched
    /// nothing. Returns (null, null) when nothing was narrowed, so the unscoped path stays untouched.</summary>
    (SweepScope? Scope, string? Error) BuildSweepScope(IReadOnlyList<string>? formids, string? editoridContains,
                                                       IReadOnlyList<string>? typeSet)
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

        // The set is the union of its entries, resolved the way records= resolves types= — one type is a set of one.
        IReadOnlyList<Type>? types = null;
        string? typeLabel = null;
        string? armLabel = null;
        if (typeSet is { Count: > 0 })
        {
            try { types = ResolveTypeFilterSet(typeSet, out armLabel); }
            catch (ArgumentException ex) { return (null, ex.Message); }
            typeLabel = string.Join(", ", typeSet.Select(t => (t ?? "").Trim()));
        }

        var scope = new SweepScope(keys, editoridContains, types, typeLabel, armLabel);
        return (scope.IsEmpty ? null : scope, null);
    }

    // ---- script-property sweep (housecarl_validate_scripts) --------------------------------------------

    /// <summary>Sweep the active order, or the given <paramref name="plugins"/> scope, for VMAD script properties
    /// declared in the attached script's .pex (or an ancestor it extends) but left unbound on the record — a silent
    /// <c>None</c>. Thin wiring over the core <see cref="ScriptPropertyCheck.Run"/>, which holds all the cross-check
    /// logic so a test can drive this same path over synthetic records and a planted .pex. Passes the live
    /// <see cref="Assets"/> resolver so a script's .pex is found loose or BSA-packed. Read-only.
    /// <para>The record-scope, property-name, class-filter and counts-only knobs are parsed here, so a bad FormID,
    /// unknown record type or unrecognized finding class refuses the call before any sweep runs.</para>
    /// <para>A named plugin the active order does not hold is located on disk and swept OFF-ORDER, the same lane
    /// <see cref="CheckErrors"/> has and through the same split — the pre-enable verify sweep for a patch houseCARL
    /// has just written.</para></summary>
    public ScriptCheckResult ValidateScripts(IReadOnlyList<string>? plugins, int limit,
                                             IReadOnlyList<string>? formids = null, string? editoridContains = null,
                                             IReadOnlyList<string>? types = null, string? propertyContains = null,
                                             IReadOnlyList<string>? findings = null, bool countsOnly = false,
                                             IReadOnlyList<string>? exclude = null,
                                             SweepOffOrderMemo? offOrderMemo = null)
    {
        var (recordScope, scopeErr) = BuildSweepScope(formids, editoridContains, types);
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
        var view = resolver.Capture();

        // The off-order lane, resolved exactly as CheckErrors resolves it: a named plugin the active order does not
        // hold is located on disk and swept from its own file, which is how a fresh patch's script bindings can be
        // checked BEFORE it is enabled.
        if (plugins is { Count: > 0 })
        {
            string modsDir, dataDir, overwriteDir, profileDir;
            lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
            if (SweepOffOrderScope.Split(view, plugins, modsDir, dataDir, overwriteDir, profileDir,
                                         out var active, out var offOrder, offOrderMemo) is { } splitErr)
                return splitErr.Stamped
                    ? ScriptCheckResult.Fail(splitErr.Message) with { Epoch = view.Epoch }
                    : ScriptCheckResult.Fail(splitErr.Message);
            return ScriptPropertyCheck.Run(resolver, view, Assets, active, limit, recordScope,
                                           propertyContains, classes, countsOnly, excluded,
                                           offOrder.Count > 0 ? offOrder : null);
        }
        return ScriptPropertyCheck.Run(resolver, view, Assets, plugins, limit, recordScope,
                                       propertyContains, classes, countsOnly, excluded);
    }

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
                    return WritePatchBuilder.PatchOutcome.Fail(ipError) with { Stamp = ipEpoch };
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
                return WritePatchBuilder.PatchOutcome.Fail(cfError) with { Stamp = cfEpoch };
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
        out OrderStamp? epoch)
    {
        // This helper takes its OWN capture, so its refusals are decided after a build was consulted and are stamped
        // like every other post-capture outcome. Null only when no CopyFrom op exists, when nothing consults a build.
        epoch = null;
        if (!edits.Any(e => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))) return null;   // no CopyFrom → no source work
        var view = resolver.Capture();
        epoch = view.Stamp;
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
            if (loc.Error is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' is not in the load order and {loc.Error}"); continue; }
            if (loc.Ambiguous is not null) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source '{e.FromPlugin}' matches several mod folders on disk — pass an exact path to disambiguate."); continue; }
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(loc.Path!, string.IsNullOrEmpty(dataDir) ? null : dataDir); }
            catch (Exception ex) { problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be opened as a Skyrim plugin ({ex.Message})."); continue; }
            IMajorRecordGetter? body;
            try { body = ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == e.CopySource); }
            catch (Exception ex) { (ov as IDisposable)?.Dispose(); problems.Add($"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' could not be read ({ex.Message})."); continue; }
            if (body is null)
            {
                (ov as IDisposable)?.Dispose();
                // Name WHICH record the file is missing: the target's own version for a same-record copy, or the
                // zip's source record for a cross-record one — "this record" would point at the wrong one.
                problems.Add(e.FromTarget is null
                    ? $"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' does not define or override this record — there is no version of it there to copy."
                    : $"{FormIdToken.Of(e.Target)}: CopyFrom source file '{e.FromPlugin}' does not define or override the SOURCE record {FormIdToken.Of(e.CopySource)} — there is no version of it there to copy from.");
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
        out IDisposable? overlay, out OrderStamp? epoch, out string? error, out string sourceName)
    {
        overlay = null; error = null; sourceName = fromPlugin;
        var view = resolver.Capture();
        epoch = view.Stamp;
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
                $"name, as {string.Join(", ", renamed.Take(3).Select(k => FormIdToken.Of(new FormKey(ov.ModKey, k.ID))))}. A plugin's records are keyed by its " +
                "FILENAME, so a copy saved under a different name is a DIFFERENT plugin. Keep the original filename and " +
                "park the copy in another folder, or name the FormIDs as this file spells them.";
            (ov as IDisposable)?.Dispose();
            error = $"refused — source file '{fromPlugin}' ({loc.Where}) does NOT define or override {missing.Count} of the " +
                    $"{specs.Count} record(s) named; there is no version of them there to forward, and NOTHING was written:\n  - " +
                    string.Join("\n  - ", missing.Select(k => FormIdToken.Of(k))) + hint;
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

        // The MO2 layer an ACTIVE plugin's own file sits in — the folder the placement after a copy has to be given
        // for an ENABLED donor, which is otherwise the one case the readback leaves the caller to guess. Best
        // effort: roots that will not derive cost the caller the folder name, never the source.
        SourceLayer? ActiveLayer(string pluginName)
        {
            try
            {
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                return view.PluginPath(pluginName) is { } p ? InstallLayerOfPath(p, modsDir, overwriteDir, dataDir) : null;
            }
            catch { return null; }
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
                    fk => view.GetRecord(session, active, fk), ActiveLayer(active)));
                continue;
            }
            // A path that names the order's own copy of an active plugin is that plugin, not an off-order file: a
            // full path never matches the name table, so without this the live copy of an active plugin takes the
            // off-order arm and is described as not in the load order.
            if (LooksLikePath(spelling) && ActiveNameForPath(view, spelling) is { } activeName)
            {
                arms.Add(new SourceArm(activeName, SourceArmKind.ActiveOrder, $"'{activeName}' (active in the load order; named by path)",
                    fk => view.GetRecord(session, activeName, fk), ActiveLayer(activeName)));
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
            // The layer this file physically sits in, read off the path by the shared rule rather than parsed back
            // out of `where`: a mod folder's name is what a following asset placement passes as its provider, and
            // the BRANCH travels with it so the sentence never calls a mod folder the layer it merely spells like.
            var layer = InstallLayerOfPath(loc.Path!, modsDir, overwriteDir, dataDir);
            // A mod folder the profile is not loading travels as such, so a readback can say the game is not loading
            // this source instead of naming the folder as though it were live. Both off standings are carried: an
            // unregistered folder is as unloaded as a switched-off one, and its remedy is the other one.
            if (layer is { Kind: SourceLayerKind.ModFolder })
                layer = loc.Served switch
                {
                    ServedStanding.ModDisabled => layer with { Folder = ModFolderStanding.SwitchedOff },
                    ServedStanding.ModUnregisteredLayer => layer with { Folder = ModFolderStanding.Unregistered },
                    _ => layer,
                };
            arms.Add(new SourceArm(spelling, SourceArmKind.File, where,
                fk => cache.TryResolve(fk, out var body) ? body : null, layer));
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
                var consulted = chain.Arms.Select(SourceArmRef.Of).ToList();

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
                // The stem falls back to the new EditorID, but only patch= is the caller's own name: a shadow on an
                // EditorID-derived stem steps to the next suffix rather than naming a parameter they never passed.
                try { outPath = ResolveOutputPath(patchName ?? (into is null ? newEditorid?.Trim() : null), into, out extend, out created,
                                                  freshPatch: FreshPatchRemedy.CreatedByOmittingInto,
                                                  stemFromCaller: !string.IsNullOrWhiteSpace(patchName)); }
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
                with { Stamp = view.Stamp };

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.PatchOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

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
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade: the swap stages a sibling temp in this directory,
        // so a read-only or locked parent is caught up front with a clear message before any work. Kept in the dry
        // run too, since an unwritable parent is exactly what the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.PatchOutcome.Fail(why) with { Stamp = view.Stamp };

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
            var names = string.Join(", ", uncovered.Select(q => q.EditorId ?? FormIdToken.Of(q.FormKey)));
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
        string? target = null, bool inPlace = false, bool acknowledge = false)
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
            // No lane is offered: housecarl_remove answers a lane-less call in its own words before reaching here,
            // so a second spelling handed down for this arm would be one nothing renders.
            return WritePatchBuilder.RemovalOutcome.Fail(
                "patch is required — name the houseCARL patch to remove the record from (removal only targets a patch that already carries it).");

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path). Pure — outside the gate.
        var keys = new List<FormKey>(formids.Count);
        var problems = new List<string>();
        var door = OpenWriteFormIdDoor();
        for (int i = 0; i < formids.Count; i++)
        {
            var raw = formids[i];
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formids[{i}]: empty."); continue; }
            try { keys.Add(door.Parse(raw)); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formids[{i}]: ", $"formids[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
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
            // that just failed. The lane states that rule instead, on both of the resolver's refusals.
            string outPath;
            try { outPath = ResolveOutputPath(patchName: null, into: patch, out _, out _,
                                              noFreshRule: WriteSentences.RemoveNoFreshPatch); }
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
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemoveNoEquivalent) is { } locRefusal)
            return WritePatchBuilder.RemovalOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // and create lanes because it is the same "touch your original" trade-off. The check gates entry here; the
        // acknowledgement is recorded only once the removal has landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: the most common in-place response shape.
            return WritePatchBuilder.RemovalOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.RemovalOutcome.Fail(why) with { Stamp = view.Stamp };

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

    /// <summary>source= is the forward surface's own word for the source pole, handed to the shared engine refusals
    /// so they name the parameter housecarl_forward publishes.</summary>
    const string ForwardSourceParam = "source=";

    /// <summary>Forward a named plugin's version of one or more records into a patch as an override — xEdit's "copy
    /// as override into", the inverse of <see cref="ApplyEdits"/>'s winner-override. Parses every formid
    /// all-or-nothing, pre-locates <paramref name="fromPlugin"/> when the active order does not contain it, resolves
    /// the folder-per-patch output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch), then drives
    /// <see cref="WritePatchBuilder.ForwardRecords"/>. The whole source record is copied verbatim, so the SOURCE
    /// plugin rather than the load-order winner decides the content — and forwarding the origin master reverts a
    /// record to vanilla. Originals are never touched in the default lane;
    /// <paramref name="target"/> with <paramref name="inPlace"/> is the explicit opt-in third route, forwarding into
    /// an existing plugin's own file under the same consent gate as the sibling write tools.</summary>
    public WritePatchBuilder.ForwardOutcome ForwardRecords(IReadOnlyList<string> formids, string fromPlugin, string? patchName, string? into,
        bool fullReadback = false, string? target = null, bool inPlace = false, bool acknowledge = false,
        bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(fromPlugin))
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"{ForwardSourceParam} is required — name the plugin whose version of the record(s) to forward (the earlier override, or a master to revert to vanilla).");
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
            if (string.IsNullOrWhiteSpace(raw)) { problems.Add($"formids[{i}]: empty."); continue; }
            try { specs.Add(new WritePatchBuilder.ForwardSpec { Target = door.Parse(raw), FromPlugin = fp }); }
            catch (Exception ex) { problems.Add(FormIdDoor.Sentence(ex, $"formids[{i}]: ", $"formids[{i}] '{raw}': {ex.Message}. Expected 'XXXXXX:Plugin.esp'.")); }
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
                return WritePatchBuilder.ForwardOutcome.Fail(offError) with { Stamp = offEpoch };
            // A path that named the ACTIVE copy resolves as that plugin, so re-spell every spec's source and the
            // engine can look it up in the index — a path is not a key there. That is also what makes the winner
            // comparison, the self-forward name check and the report's "copied from" speak the order's vocabulary.
            if (!string.Equals(sourceName, fp, StringComparison.Ordinal))
                specs = specs.Select(s => new WritePatchBuilder.ForwardSpec { Target = s.Target, FromPlugin = sourceName }).ToList();
            try
            {
                if (inPlace)
                    return ForwardRecordsInPlace(resolver, specs, target!.Trim(), acknowledge, dryRun, offOrder);

                // A dry run resolves the would-be output path without creating the mod folder.
                string outPath; bool extend, created;
                try { outPath = ResolveOutputPath(patchName, into, out extend, out created, create: !dryRun, FreshPatchRemedy.NamedByPatchParam); }
                // Stamped like every post-capture outcome: the source resolve above already consulted the build.
                catch (Exception ex) { return WritePatchBuilder.ForwardOutcome.Fail(ex.Message) with { Stamp = offEpoch }; }

                var outcome = WritePatchBuilder.ForwardRecords(resolver, specs, outPath, extend, ForwardSourceParam, fullReadback, dryRun, offOrder);
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
        bool dryRun = false, WritePatchBuilder.OffOrderForwardSource? offOrder = null)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin. Same resolver as the other in-place lanes.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.ForwardOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place forwards into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is refused BEFORE the dry-run branch below. houseCARL cannot re-serialize a localized
        // plugin without scrambling its text, and the write's own backstop cannot serve here for two reasons: a dry
        // run, whose contract is to give exactly the answer the real call gives, would otherwise report the edit
        // landing; and the backstop's sentence names no lane, while a caller refused here needs this lane's remedy.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.ForwardOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

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
                    with { Stamp = view.Stamp };
            owesConsent = !already && acknowledge;
        }

        // Writable-parent pre-flight — refuse rather than degrade. Kept in the dry run, which predicts exactly what
        // the real write would refuse on.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.ForwardOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the touched-record verify forced on.
        var outcome = WritePatchBuilder.ForwardRecordsInPlace(resolver, specs, targetPath, targetName, ForwardSourceParam, fullReadback: true, dryRun, offOrder);

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
    /// order, a houseCARL mod folder of that name is already on disk, or a file of that basename sits somewhere the
    /// order is not loading (#561). The core
    /// <see cref="WritePatchBuilder.CreatePlugin"/> builds, serializes and re-reads to confirm, and a refused create
    /// that just made the output folder leaves no orphan.</summary>
    public WritePatchBuilder.CreatePluginOutcome CreatePlugin(string pluginName, bool esl = false, string? author = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                "patch is required — a header-only plugin has no record to derive a name from, so name it explicitly (e.g. 'Authoria - CraftingCategories').");

        var stem = PatchStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                $"patch '{pluginName}' has no usable name once path parts and the plugin extension are stripped — give a plain name like 'MyTrigger'.");

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
            // (c) a plugin of this BASENAME sits somewhere the order is NOT loading — the same shadow the fresh patch
            //     lanes take (#561), swept over all three extensions like (a) above and for (a)'s reason: the exact
            //     basename is what makes a trigger resolve, so a second file carrying it is what this tool avoids.
            var plugin = stem + ".esp";
            var active = ActivePluginBasenames();
            if (active.Count > 0 && ReadCompositionForShadow() is { } comp)
                foreach (var ext in PluginExts)                       // .esp / .esm / .esl — the basename is what binds
                    if (PatchStemShadow.Find(comp, _modsDir, _dataDir, _overwriteDir, stem + ext, active) is { } shadow)
                        return WritePatchBuilder.CreatePluginOutcome.Fail(
                            PatchStemShadow.Refusal(plugin, shadow, "patch", stem + ext,
                                                    "a header-only trigger needs a UNIQUE basename"));

            Directory.CreateDirectory(folder);
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
    /// changed plugin name causes. <paramref name="patch"/> names the output MOD FOLDER, as it does on every tool
    /// that writes one, and the merged plugin inside it takes that folder's name: patch="MyMerge" writes
    /// "houseCARL - MyMerge\MyMerge.esp" through the same fresh-write resolver the record lanes use.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? patch)
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
        var patchName = (patch ?? "").Trim();
        if (patchName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "patch is required — name the NEW mod folder to create (e.g. 'MyMerge'). The merged plugin inside it takes that name ('MyMerge.esp'), and it must not already exist in your load order.");
        // patch= names the FOLDER and the plugin takes the folder's name, the rule on every tool that writes one.
        // The requested stem is what these pre-flight refusals are about, and it is also the file finally written:
        // ResolveOutputPath refuses a collision on it rather than suffixing, because the basename is load-bearing.
        var outName = PatchStem(patchName) + ".esp";
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"patch='{patchName}' does not name a valid plugin: '{outName}' ({ex.Message})."); }
        // The .esl spelling is REFUSED rather than stripped to .esp like any other extension, because it asks for
        // something the merge cannot deliver, and silently handing back a full plugin would be a degraded mode.
        if (patchName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            return WritePatchBuilder.MergeOutcome.Fail(
                // The reason is what the merge does NOT do, and only that. Neither "the donors' ids stay in the full
                // range" nor "it keeps each donor's object ids where they already are" is true on every path: an
                // already-light donor's ids are all inside the window by definition, and BuildMergeRemap renumbers
                // collisions and below-floor ids from 0x800 up — a count the report prints.
                $"refused — patch='{patchName}' asks for the .esl extension, which the game engine force-treats as a LIGHT master regardless " +
                "of the header flag, but a merge never constrains object ids to the light window: it renumbers only what it must " +
                "(cross-donor collisions, and ids below the write floor), so an id above 0xFFF would be misread in game. Pass " +
                $"patch='{PatchStem(patchName)}' instead, which writes '{outName}': if every donor was light and every merged id landed in the window, the output is written LIGHT " +
                "already; otherwise the report says so, and " + ToolNames.CompactPlugin + " on it renumbers every id into the light " +
                "window (the tools compose). Nothing was written.");
        if (donorsRaw.Any(d => string.Equals(d, outName, StringComparison.OrdinalIgnoreCase)))
            return WritePatchBuilder.MergeOutcome.Fail($"the output '{outName}' (the plugin patch='{patchName}' names) cannot also be a donor — pass patch= a NEW name.");

        lock (_writeGate)                                                 // one write at a time
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.MergeOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");
            if (view.ContainsPlugin(outName))
                return WritePatchBuilder.MergeOutcome.Fail(
                    $"'{outName}' — the plugin patch='{patchName}' names — is already an active plugin in your load order, and the merge " +
                    "output must be a NEW plugin name (merging over an existing plugin would shadow it in MO2). Pass patch= another name.");

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
            var transformSet = new HashSet<string>(donorNames, StringComparer.OrdinalIgnoreCase);

            // ---- 2. masters = union(donor declared masters) − donors, load-order sorted (each donor's own header order
            //      is already load-order-consistent; the union sorts by the active order so the merged header is too).
            //      This reads the donor HEADERS only — the same read check's missing_masters pass makes — and it runs
            //      HERE, before the record reads and the identify pass, because a master the active order does not
            //      carry refuses the whole merge and the caller should hear that in seconds (#729). ----
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
            // The same predicate the serialize applies (MergeBuild resolves each master to an overlay and refuses when
            // one is absent), asked from the headers instead of after the walk. MergeBuild still asks it: a master can
            // go away between here and the write.
            foreach (var mfn in masterSet)
                if (view.PluginPath(mfn) is null)
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");

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

            // ---- 3. what each donor holds, one enumeration per donor: the records it defines, the donor-space records
            //      it carries without defining (injected records), and its links into donor space. ----
            var donorModKeys = donorInfos.Select(d => d.Key).ToHashSet();
            var scans = new List<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)>();
            var donorLinks = new List<(string Donor, IReadOnlyList<FormKey> Records, IReadOnlyList<(FormKey Source, FormKey Target)> Links)>();
            foreach (var (dName, dPath, dKey, _) in donorInfos)
            {
                if (!WritePatchBuilder.TryScanMergeDonor(dPath, dKey, donorModKeys, out var scan, out var keyErr))
                    return WritePatchBuilder.MergeOutcome.Fail(keyErr!);
                scans.Add((dName, scan.Originating, scan.Carried));       // a pure-override donor (0 originating keys) is a legit patch donor
                donorLinks.Add((dName, scan.Records, scan.DonorLinks));
            }
            // An injected record — one whose FormID names a donor while another plugin carries it — is renumbered with
            // the donor carrying it, instead of being copied at an identity the merge is about to remove (#715). A
            // plugin outside the merge that carries it too is warned about by the identify pass below, like any other
            // external overrider, because the key is now in the dict.
            var donorKeys = MergeInjection.Renumberable(scans);
            // ---- output folder and plugin: the same fresh-write resolver the record lanes use, so patch= names the
            //      mod folder "houseCARL - <stem>" and the merged plugin inside it is "<stem>.esp". Resolved HERE,
            //      before the remap, because the remap is keyed on the output ModKey and the stem can still be
            //      REFUSED by a mod folder of that name (an active plugin of it was refused above). ----
            // The merged plugin's basename is load-bearing — a _DISTR.ini, a _KID.ini, a config keyed by plugin name,
            // another plugin listing it as a master all bind to it — so a folder collision refuses by name here, the
            // way create_plugin does, rather than writing "<stem>_001.esp" that none of them resolve.
            string outPath;
            bool createdFolder;
            try
            {
                outPath = ResolveOutputPath(patchName, into: null, out _, out createdFolder,
                    refuseTaken: new StemRefusal(
                        "the merged plugin",
                        "Remove it in MO2, or pass patch= a name no mod folder or active plugin already carries."));
            }
            catch (InvalidOperationException ex) { return WritePatchBuilder.MergeOutcome.Fail(ex.Message); }
            outName = Path.GetFileName(outPath);
            try { outKey = ModKey.FromFileName(outName); }
            catch (Exception ex) { return FailAfterFolder($"'{outName}' is not a valid plugin filename ({ex.Message})."); }

            // A refusal past the folder allocation removes the folder again, so "nothing was written" is true of the
            // disk too and no orphan accretes suffixes on retry.
            WritePatchBuilder.MergeOutcome FailAfterFolder(string msg)
            {
                if (createdFolder) RemoveFolderCreatedThisCall(outPath);
                return WritePatchBuilder.MergeOutcome.Fail(msg);
            }

            var plan = RemapEngine.BuildMergeRemap(donorKeys, outKey, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
            if (!plan.Success) return FailAfterFolder(plan.Error!);

            // A donor reference the remap cannot carry is named HERE, before the identify pass and the build, rather
            // than surviving the renumber and failing the serialize.
            if (MergeInjection.UnremappableLink(plan.Dict, donorLinks) is { } linkRefusal)
                return FailAfterFolder(linkRefusal);

            // ---- 4. identify-pass — WARN-and-proceed (the A4 posture; unlike compact this NEVER refuses: the donors stay
            //      installed and ACTIVE until the user swaps in MO2, so nothing breaks at write time. The report names each
            //      affected plugin with the remedy — include it in the merge set, or handle it before disabling the donors.) ----
            var targets = plan.Dict.Keys.ToHashSet();
            // readDeclaredMasters: a merge RENAMES the donors' records into a new plugin, so a dependent that only
            // lists a donor as a master loses it at the swap. Sound here because BuildMergeRemap enters every
            // originating key of every donor into the dict, so a referencer is always a declarer too and the
            // declarer-only filter cannot hide one.
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet, readDeclaredMasters: true);

            // ---- build and write the merged plugin ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath, view.DataDir);
            if (!build.Success)
            {
                return FailAfterFolder(build.Error!);                      // a refused build leaves no orphan folder
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

            // Where the output has to sit, off the positions, masters and dependents this call already computed.
            var placement = MergeLoadPosition.Derive(
                donorInfos.Select(d => (d.Name, d.Order)).ToList(), build.Masters,
                p => orderIndex.TryGetValue(p, out var i) ? i : null);

            return new WritePatchBuilder.MergeOutcome(
                true, null, outPath, outName, donorNames, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                plan.Donors, build.Conflicts, id.ExternalPlugins, id.ExternalOverriders,
                id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples, build.Bytes, note,
                assetRename, voiceRename, seqRegen, build.LightDonors, build.HeaderMetaDonors, build.MasterDonors,
                id.UnscannablePlugins, localizedDonors, id.MasterDeclarers, build.LightCarried, build.OriginatingRecords,
                placement);
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
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
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
            // records[r] is the create surface's own member word, so a refusal names the spec the caller can act on.
            var where = $"records[{r}]";
            var spec = BuildCreateSpec(door, rec.RecordType, rec.Editorid, rec.Operations ?? Array.Empty<BulkOp>(), rec.Parent, rec.Collection, rec.Grid, where, problems, siblings);
            if (spec is not null) specs.Add(spec);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) across {records.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));
        return CommitCreate(specs, patchName, into, fullReadback, target, inPlace, acknowledge, replace);
    }

    /// <summary>Build one core <see cref="WritePatchBuilder.CreateSpec"/> from wire parts, shared by the single
    /// create and the batch: resolve <paramref name="recordType"/> to one concrete catalog name, require an editorid,
    /// map each field op to a core <see cref="WriteRequest"/> rooted at that type, and carry
    /// <paramref name="parent"/> and <paramref name="collection"/> through for a nested child — null means a flat
    /// top-level record. Every problem, tagged with the <paramref name="where"/> label, is appended to
    /// <paramref name="problems"/>, and this returns null iff this record contributed any.</summary>
    WritePatchBuilder.CreateSpec? BuildCreateSpec(FormIdDoor door, string? recordType, string? editorid, IReadOnlyList<BulkOp> operations,
        string? parent, string? collection, string? grid, string where, List<string> problems,
        IReadOnlySet<string>? siblingEditorids = null)
    {
        var prefix = where + ": ";
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

    /// <summary>Resolve the folder-per-patch output, fresh or <paramref name="into"/> an existing houseCARL-owned
    /// patch, then drive the core multi-record create and serialize under the write gate, one write at a time. A
    /// refused create that just made the output folder leaves no orphan. Shared by the single and batch
    /// create.</summary>
    WritePatchBuilder.CreateOutcome CommitCreate(IReadOnlyList<WritePatchBuilder.CreateSpec> specs, string? patchName, string? into, bool fullReadback,
        string? target = null, bool inPlace = false, bool acknowledge = false, bool replace = false)
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
        // replace= answers the in-place collision refusal and nothing else: a fresh patch has nothing to collide with,
        // and into= already rebuilds its own record at a stable FormKey so a re-run stays idempotent.
        if (!inPlace && replace)
            return WritePatchBuilder.CreateOutcome.Fail(
                "replace=true overwrites a record the in-place TARGET already defines under the same editorid, and is only meaningful with in_place=true. Drop it, or name the file to create into.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            var resolver = Resolver;
            var rulebook = Rulebook;

            if (inPlace)
                return CommitCreateInPlace(resolver, rulebook, specs, target!.Trim(), acknowledge, replace);

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
        string target, bool acknowledge, bool replace = false)
    {
        // Resolve target to its real on-disk path via the load order, by plugin filename. Refuse loudly if it is not
        // a real active plugin, which closes the coincidental-folder collision. Same resolver as the edit lane.
        var view = resolver.Capture();
        var targetPath = ResolveActivePluginPath(view, Path.GetFileName(target.Trim()), out var targetName);
        if (targetPath is null)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"in-place target '{target}' is not an active plugin in the load order — name a plugin enabled in MO2, by its " +
                "plugin filename (e.g. 'CoolWeapons.esp'). in-place creates into the file the game actually loads. Nothing was written.")
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // A localized target is predicted here rather than met at the write: houseCARL cannot re-serialize a
        // localized plugin without scrambling its text, and the write's own backstop names no lane, while a caller
        // refused here needs this lane's remedy clause.
        if (LocalizedStrings.RefusalFor(targetPath, targetName, view.DataDir, LocalizedTargetUnsupportedException.RemedyDefaultLane) is { } locRefusal)
            return WritePatchBuilder.CreateOutcome.Fail(locRefusal)
                with { Stamp = view.Stamp };   // decided off the capture above — stamped like every post-capture outcome

        // The consent axis: the persistent first-touch handshake keyed off the resolved path, shared with the edit
        // lane because acknowledging a plugin once covers both editing and creating into it — the same "touch your
        // original" trade-off. The check gates entry here; the acknowledgement is recorded only once the create has
        // landed.
        bool already = _store.IsInPlaceAcknowledged(targetPath);
        if (!already && !acknowledge)
            // Stamped for the reason the edit lane's twin states: this branch is reached only after the view above
            // resolved the target, and it is the most common in-place response shape.
            return WritePatchBuilder.CreateOutcome.NeedsAck(InPlaceHandshakeText(targetName, targetPath))
                with { Stamp = view.Stamp };
        bool owesConsent = !already && acknowledge;

        // Writable-parent pre-flight — refuse rather than degrade; the swap stages a sibling temp here.
        if (InPlaceParentUnwritable(targetPath, out var why))
            return WritePatchBuilder.CreateOutcome.Fail(why) with { Stamp = view.Stamp };

        // The write, with the created-record verify forced on.
        var outcome = WritePatchBuilder.CreateRecordsInPlace(resolver, rulebook, specs, targetPath, targetName, fullReadback: true,
                                                             replaceExisting: replace);

        // On success, record the acknowledgement, then run the same post-write checks the patch lane runs, since the
        // service owns the live asset resolver and in-place create can author dialogue lines and cells under any
        // parent. Each is a no-op unless that record kind was created, and none can fail the write. Then stamp the
        // audit marker, best-effort: a marker miss never fails the done create.
        if (outcome.Success)
        {
            var ackNote = PersistInPlaceConsent(owesConsent, targetPath, "create");
            var enriched = EnrichWithCellShell(EnrichWithScriptCheck(EnrichWithVoiceCheck(outcome, resolver)));
            var markerNote = MergeEditedInPlaceMarker(Path.GetDirectoryName(targetPath));
            // enriched.Note FIRST, exactly as the other three in-place lanes join: the core create lane now emits the
            // master-grow re-sort note, and dropping it here would lose the one note the caller must act on.
            var note = JoinNotes(enriched.Note, ackNote, markerNote);
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

    /// <summary>Map a wire field-op to a core <see cref="WriteRequest"/> for a create: RecordType is the create type
    /// rather than derived, and a create op carries no formid because it sets a field on the new record, whose id is
    /// auto-allocated — a stray formid is refused rather than silently ignored. Builds the composition
    /// <see cref="StructSpec"/> the same way <see cref="MapEdit"/> does, so a created record's nested lists compose
    /// identically.</summary>
    WriteRequest? MapCreateEdit(BulkOp op, int index, string recordType, out string? error)
    {
        error = null;
        // ops[i] is the create surface's own member word, so a refusal names the handle the caller can act on.
        var where = $"ops[{index}]";
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

        if (string.Equals(op.Verb, WriteVerbs.Transplanting, StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(op.FromPlugin))
        {
            // Named as the create surface spells it. This is reachable even though that surface declares no
            // from_plugin member, because the strict reader gates undeclared members and `op` is declared, so a
            // CopyFrom verb arrives here and must not be answered with a member the caller cannot remove.
            error = $"{where}: op=\"CopyFrom\" copies from an EXISTING record's other version — it isn't valid when CREATING a record (there is no other version yet). Set the new field with value= / compose= instead.";
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
        // The caller's own spelling for this edit: inline ops are ops[i], the member housecarl_apply publishes, while
        // zip-generated ops are named by the pair and path they came from. A refusal pointing at an op index the
        // caller never wrote sends anyone fixing it to a line that does not exist.
        var where = origin ?? $"ops[{index}]";
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
    /// <paramref name="freshPatch"/> and <paramref name="noFreshRule"/> pass through to the extend refusals' remedy,
    /// so the calling operation states how, or whether, its own fresh-write path works. Both default to claiming
    /// nothing, so a caller added later cannot inherit a sentence that is false for it.
    /// <paramref name="stemFromCaller"/> says whether the caller really spelled this name as <c>patch=</c>, which is
    /// what makes a shadow on it a refusal rather than a step to the next suffix. It defaults to reading that off
    /// <paramref name="patchName"/>, and a lane that COALESCES something else into that argument — the copy lane
    /// falls back to the new EditorID — passes its own <c>patch=</c> instead, so a refusal never names a parameter
    /// the caller left out.
    /// <paramref name="refuseTaken"/> is for a lane whose PLUGIN basename is load-bearing — merge, whose output name
    /// a _DISTR.ini or a dependent's master entry binds to: a taken stem refuses by name instead of auto-suffixing.</summary>
    string ResolveOutputPath(string? patchName, string? into, out bool extend, out bool createdFolder, bool create = true,
                             FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                             bool? stemFromCaller = null, StemRefusal? refuseTaken = null)
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
                var folder = ResolveOwnedPatchFolder(into, needEsp: true, freshPatch, noFreshRule);
                var direct = Path.Combine(folder, PatchStem(into) + ".esp");
                if (File.Exists(direct)) return direct;
                var sole = SoleEspInFolder(folder, out var why);
                if (sole is not null) return sole;
                throw new InvalidOperationException($"cannot extend: houseCARL folder '{Path.GetFileName(folder)}' {why}.");
            }

            extend = false;
            var baseStem = PatchStem(string.IsNullOrWhiteSpace(patchName) ? "Patch" : patchName!);
            // Every record lane that reaches here declares patch= and writes "<stem>.esp", so that is the file the
            // shadow check tests and the spelling its refusal names.
            var freeStem = UniqueStem(baseStem, stemFromCaller ?? !string.IsNullOrWhiteSpace(patchName),
                                      new PatchStemShadow.Target(s => s + ".esp", "patch"), refuseTaken);
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
    /// compile/decompile riders OutputDir is a subfolder (<c>Scripts\</c> / <c>Source\Scripts\</c>) under ModFolder.
    /// <paramref name="Stem"/> is the folder's own name without the "houseCARL - " prefix — the name a lane whose
    /// artifact takes the folder's name (the .bsa) gives that artifact.</summary>
    public readonly record struct RiderFolder(string OutputDir, string ModFolder, bool CreatedFresh, string Stem);

    /// <summary>How ONE rider lane names the mod folder it creates — the calling tool's own statement, the way
    /// <see cref="FreshPatchRemedy"/> is for the record lanes. <paramref name="Param"/> is the parameter that tool
    /// actually declares for the folder's name, which is <c>patch=</c> on every tool that writes one. A lane whose
    /// <c>into=</c> can never be non-empty passes null, and keeps the weakest true remedy.
    /// <paramref name="RefuseTaken"/> is set by a lane whose ARTIFACT takes the folder's name and whose exact
    /// basename is load-bearing — the .bsa, which the game auto-loads only under its plugin's basename: a taken stem
    /// refuses by name rather than writing an archive nothing loads. A lane whose artifact is named independently of
    /// the folder (compiled scripts, extracted files) leaves it null and keeps auto-suffixing.</summary>
    public readonly record struct RiderNaming(string Param, StemRefusal? RefuseTaken = null);

    /// <summary>Resolve a houseCARL-owned mod folder under ModsDir for a non-.esp output — compiled scripts, a packed
    /// .bsa, extracted loose files — generalising the folder-per-patch model beyond the .esp write path. Either a
    /// fresh marker-stamped folder, named by <paramref name="defaultStem"/> when patchName is blank and auto-suffixed
    /// so a prior one is never clobbered, or <paramref name="into"/> an existing houseCARL-owned one. It refuses a
    /// folder houseCARL did not create. Derives ModsDir cheaply by reading ModOrganizer.ini, with no index build, and
    /// throws the unconfigured prompt when there is no instance. The returned
    /// <see cref="RiderFolder.CreatedFresh"/> flag drives the cleanup on a failure. No lane here writes a plugin —
    /// scripts, an archive or loose files only — so none takes a plugin-shadow refusal; a plugin's own name follows
    /// its folder stem through <see cref="ResolveOutputPath"/> instead.</summary>
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
                var folder = ResolveOwnedPatchFolder(into, needEsp: false, FreshPatchRemedy.None, riderNaming: naming);
                return new RiderFolder(folder, folder, CreatedFresh: false, FolderStem(folder));   // reused — the user owns it; cleanup leaves it
            }

            var newStem = UniqueStem(PatchStem(string.IsNullOrWhiteSpace(patchName) ? defaultStem : patchName!),
                                     !string.IsNullOrWhiteSpace(patchName), writes: null, naming?.RefuseTaken);
            var newFolder = Path.Combine(_modsDir, ModFolderName(newStem));
            Directory.CreateDirectory(newFolder);
            WriteOwnerMeta(newFolder, "(houseCARL output)");   // ownership marker; this folder may hold scripts / a .bsa / loose files, not an .esp
            return new RiderFolder(newFolder, newFolder, CreatedFresh: true, newStem);
        }
    }

    /// <summary>The <c>Scripts\</c> output folder for a compiled .pex: a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>Scripts\</c> subfolder, which MO2 deploys into the game's
    /// Data\Scripts. Carries the mod-folder root and fresh flag through for cleanup.</summary>
    public RiderFolder ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
        var scripts = Path.Combine(f.ModFolder, "Scripts");
        Directory.CreateDirectory(scripts);
        return f with { OutputDir = scripts };
    }

    /// <summary>The out_path= escape hatch: the user names where the compiled .pex lands instead of houseCARL
    /// cutting a fresh folder-per-patch mod folder. out_path is a mod-folder ROOT and houseCARL appends
    /// <c>Scripts\</c>, matching <see cref="ResolveCompiledScriptFolder"/> and MO2's deploy model so the .pex
    /// actually loads, with a guard against appending a second Scripts\ when one is already there. It cuts no
    /// houseCARL mod folder, and the folder is user-owned, so the returned <see cref="RiderFolder"/> carries
    /// CreatedFresh=false and cleanup never deletes it on a failed compile. <paramref name="deployWarning"/> is
    /// non-null when the final Scripts\ path is none of a mod's own Scripts\, the MO2 overwrite folder, or the game's
    /// Data, because the .pex compiles but the game will not auto-load it from there. Refuses loudly on an unusable
    /// out_path — a malformed path, or one naming an existing file.</summary>
    public RiderFolder ResolveExplicitScriptFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "Scripts", ScriptOutputContract, out deployWarning);

    /// <summary>The same out_path= contract for the SEQ rider: the user names a mod-folder root and houseCARL
    /// appends <c>SEQ\</c>. It exists because the .seq output model otherwise assumes the plugin it serves lives in a
    /// houseCARL folder, which the in-place .esp lane inverts — the .esp in the mod's own folder, the .seq in a
    /// different mod entirely — and a .seq in an un-enabled or wrong folder leaves the quest silently dead.
    /// <para>The <c>into=</c> ownership check is deliberately untouched: letting into= name a folder houseCARL did
    /// not create would put its patch-folder machinery inside a third party's mod. This lane instead writes a new
    /// sidecar file into a folder the user owns, cutting no houseCARL folder and bypassing cleanup
    /// (<see cref="RiderFolder.CreatedFresh"/> = false).</para></summary>
    public RiderFolder ResolveExplicitSeqFolder(string outputDir, out string? deployWarning)
        => ResolveExplicitRiderFolder(outputDir, "SEQ", SeqOutputContract, out deployWarning);

    /// <summary>The shared body of the out_path= lanes, one artifact per caller: normalize the root, refuse an
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
            catch (Exception ex) { throw new InvalidOperationException($"out_path '{outputDir}' is not a usable path ({ex.Message})."); }
            if (File.Exists(root))
                throw new InvalidOperationException($"out_path '{root}' is a file, not a folder. Give a mod-folder root — houseCARL appends {sub}\\.");

            var (outDir, appended, warn) = contract(root, _modsDir, _dataDir, _overwriteDir);
            // A plain message when the folder cannot be created — the subfolder already exists as a file, or the path
            // is read-only — instead of letting the IO or access exception reach the generic internal-failure
            // handler, which would read as a houseCARL bug rather than bad input.
            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException($"out_path: couldn't create the output folder '{outDir}' ({ex.Message}). Check the path and that it's writable."); }
            deployWarning = warn;
            // ModFolder is the mod-folder root — inert here, since cleanup is bypassed by CreatedFresh=false, but
            // kept accurate: when the user pointed at the subfolder, the root is its parent; otherwise the path they
            // gave IS the root.
            var modRoot = appended ? root : (Path.GetDirectoryName(outDir.TrimEnd('\\', '/')) ?? outDir);
            return new RiderFolder(outDir, modRoot, CreatedFresh: false, FolderStem(modRoot));   // user-owned: residue cleanup never touches it
        }
    }

    /// <summary>Pure, filesystem-free resolution of the out_path= contract, so it is testable without an MO2
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

    /// <summary>The shared pure core of the out_path= contracts: append <paramref name="sub"/> to a mod-folder
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
        var f = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts", new RiderNaming("patch"));
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
    /// <para><paramref name="outputDir"/> is the same out_path= contract the compile lane carries: the user names a
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

            // Output folder: out_path=, the user's own mod folder, wins; else the plugin's own houseCARL folder;
            // else a fresh one or an explicit into= / patch. The out_path arm cuts no houseCARL folder, so
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

            // Is there something here already? An out_path= destination is a folder houseCARL does not own, so the
            // file being replaced may be the mod's own shipped .seq, and "wrote" and "replaced yours" are different
            // facts about the disk.
            bool replaced = File.Exists(dest);
            // And whether anything was lost. The one path that reaches the write with sameBytes true is a timestamp
            // refresh that failed — a share that accepts the stamp without persisting it — and calling that
            // "overwritten, no backup is kept" would be an alarm about a file whose bytes were re-written identically.
            bool replacedSameBytes = replaced && sameBytes;

            // Crash-atomic write of <plugin>.seq under SEQ\, into a houseCARL-owned folder or the folder the caller
            // named in out_path=.
            try { AtomicFile.WriteAllBytes(dest, built.Bytes); }
            catch (Exception ex)
            {
                var residue = RemoveOrNameRiderResidue(rf);             // nothing landed → a fresh folder is an orphan
                return SeqOutcome.Fail($"could not write '{seqName}': {ex.Message}"
                    + (residue is null ? "" : $" The freshly created folder was left at '{residue}'.")
                    // On the out_path lane cleanup is bypassed by design, since the folder is the user's, so the
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

    /// <summary>The stem a mod folder carries: "houseCARL - &lt;stem&gt;" without the prefix, and the folder's own
    /// name when it has none — a renamed patch folder, or a user-owned one. The inverse of
    /// <see cref="ModFolderName"/> where one applies, and the name an artifact takes from its folder.</summary>
    static string FolderStem(string folderPath)
    {
        var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string prefix = "houseCARL - ";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name[prefix.Length..] : name;
    }

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
    /// remains the way to grow an existing patch; this is only the fresh path.
    /// <para>Neither test sees a plugin the active order is NOT loading, so the FILE the claiming lane will write is
    /// also put through <see cref="PatchStemShadow"/>, which REFUSES rather than suffixing (#561): a name suffixed
    /// around a foreign inactive plugin hides the file the caller may have meant. <paramref name="writes"/> is the
    /// calling lane's own statement of the file it emits and the parameter that names it, so the refusal never sends
    /// a caller to a parameter their tool does not declare; a lane that writes no plugin passes none and takes no
    /// shadow refusal. <paramref name="stemFromCaller"/> says the base stem is the caller's own name rather than the
    /// lane's default, which is what makes a shadow on it refusable.</para>
    /// <para><paramref name="refuseTaken"/> is the statement of a lane whose ARTIFACT takes this stem and whose exact
    /// basename is load-bearing — the merged plugin, the .bsa. A suffix there writes a file under a name the caller
    /// never asked for and nothing resolves, so those lanes REFUSE a taken stem by name, the way
    /// <c>create_plugin</c> does, instead of stepping to the next suffix. One resolver with a mode on it, not a
    /// second resolver, so every lane keeps the same derivation.</para></summary>
    string UniqueStem(string stem, bool stemFromCaller, PatchStemShadow.Target? writes, StemRefusal? refuseTaken = null)
    {
        var active = ActivePluginBasenames();
        // The sweep can only tell a shadow from the active plugin the suffix loop already dodges if it knows what the
        // order loads. With no composition, or no active plugin known at all, it does not run and the pre-existing
        // folder and active-order uniqueness stand — the same best-effort degradation both reads already document,
        // so a call that worked before never fails because the extra check could not run. A lane that writes no
        // plugin takes no shadow refusal either, so it does not pay the profile parse the sweep would need.
        var comp = active.Count == 0 || writes is null ? null : ReadCompositionForShadow();
        if (Takeable(stem)) return stem;
        for (int i = 1; i < 10000; i++)
        {
            var cand = $"{stem}_{i:D3}";
            if (Takeable(cand)) return cand;
        }
        throw new InvalidOperationException($"too many patches named '{stem}' under ModsDir — clean some out.");

        // Free of a folder and of an active plugin, and shadowing nothing. A shadow on the name the CALLER passed is
        // refused; a shadow on a suffix houseCARL invented is stepped past, since the caller cannot be told to avoid
        // a name they never chose, and the file follows the stem, so the next suffix clears it.
        bool Takeable(string s)
        {
            if (StemCollision(s, active) is { } taken)
            {
                // A lane whose artifact's basename is load-bearing refuses the name the CALLER passed rather than
                // writing that artifact under an invented suffix.
                if (refuseTaken is { } r && stemFromCaller && s == stem)
                    throw new InvalidOperationException(
                        $"{taken} — houseCARL won't auto-rename {r.Artifact}, whose exact basename is load-bearing, so " +
                        $"nothing was written. {r.Remedy}");
                return false;
            }
            if (comp is null || writes is not { } w) return true;
            var file = w.PluginFor(s);
            if (PatchStemShadow.Find(comp, _modsDir, _dataDir, _overwriteDir, file, active) is not { } hit) return true;
            if (stemFromCaller && s == stem)
                throw new InvalidOperationException(PatchStemShadow.Refusal(file, hit, w.Param));
            return false;
        }
    }

    /// <summary>The profile composition the shadow sweep walks, read once per fresh write, or null when the profile
    /// cannot tell a shadow from a loaded plugin. The test is whether the composition is USABLE, not whether the read
    /// threw: <see cref="Mo2LoadOrder.ReadComposition"/> does not throw on a missing modlist.txt, it returns empty mod
    /// lists — and then every folder under ModsDir reads as UNLISTED, so an enabled mod's genuinely loaded plugin
    /// would be refused as one "the load order is not loading". A profile naming no mod at all is that state, so it
    /// yields null. Best-effort in the same way <see cref="ActivePluginBasenames"/> is: null leaves the pre-existing
    /// folder and active-order uniqueness, so a call that worked before never fails because the check could not run.
    /// A modlist.txt truncated to a PARTIAL list is not detectable here and is not claimed to be.</summary>
    Mo2Composition? ReadCompositionForShadow()
    {
        try
        {
            var comp = Mo2LoadOrder.ReadComposition(_profileDir);
            // No mod named in either list — modlist.txt missing, or truncated to nothing — is an unusable profile.
            return comp.EnabledMods.Count == 0 && comp.DisabledMods.Count == 0 ? null : comp;
        }
        catch { return null; }
    }

    /// <summary>Null when a stem is free to claim — no houseCARL mod folder for it exists AND its plugin
    /// "<c>&lt;stem&gt;.esp</c>" isn't already an active load-order plugin (case-insensitive — Skyrim plugin basenames
    /// are) — else the sentence naming WHICH of the two is in the way, so a refusing lane can say it.</summary>
    string? StemCollision(string stem, IReadOnlySet<string> activePlugins)
        => Directory.Exists(Path.Combine(_modsDir, ModFolderName(stem)))
            ? $"a mod folder '{ModFolderName(stem)}' already exists"
            : activePlugins.Contains(stem + ".esp")
                ? $"a plugin named '{stem}.esp' is already active in your load order"
                : null;

    /// <summary>One lane's statement that its artifact's exact basename is load-bearing, so a taken stem refuses
    /// instead of auto-suffixing. <paramref name="Artifact"/> names the file in the refusal ("the merged plugin");
    /// <paramref name="Remedy"/> is that lane's own way out, since which parameters it declares differ.</summary>
    public readonly record struct StemRefusal(string Artifact, string Remedy);

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
    /// patch, and decides the fresh-write half of both refusals' remedy; <paramref name="noFreshRule"/> is the same
    /// statement from a lane the enum cannot express (removal, which creates nothing), and says WHY there is no
    /// fresh route. Both are deliberately separate from <paramref name="needEsp"/>. Each refusal is ONE sentence:
    /// what went wrong, then what to try, with the nearest owned patches named inside it (#359, #380).</summary>
    string ResolveOwnedPatchFolder(string into, bool needEsp,
                                   FreshPatchRemedy freshPatch = FreshPatchRemedy.None, string? noFreshRule = null,
                                   RiderNaming? riderNaming = null)
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
        // Ambiguous: refuse and name every candidate folder as a ready-to-paste into= value.
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
                // The fresh clause deliberately does not hand the caller their own stem back — a "<stem>.esp" minted
                // beside this folder's inactive "<stem>.esp" is two plugins that cannot both be active (#359). The
                // in-place lane, the other reading of a name that lands on a foreign folder, is NOT offered here:
                // this refusal is one sentence about the extend that failed, and the consent-gated lane stays
                // discoverable on the tool that declares it (Aaron, 2026-09-05).
                throw new InvalidOperationException(ExtendRefusal(
                    $"mod folder '{cand}' exists but was NOT created by houseCARL (no marker), so writing into it "
                    + "would touch a mod houseCARL doesn't own (originals untouched, Q3)",
                    noFreshRule,
                    OwnedPatchCandidates(needEsp, stem),
                    riderNaming is { } fr
                        ? $"dropping into= and passing {fr.Param}= a name no mod folder already uses for a fresh folder"
                        : freshPatch switch
                        {
                            FreshPatchRemedy.NamedByPatchParam => "dropping into= and passing patch= a name no mod folder already uses for a fresh patch",
                            FreshPatchRemedy.CreatedByOmittingInto => "omitting into= for a fresh patch",
                            _ => null,
                        }));
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
        // refusal. A caller added later without a thought about any of this gets the owned candidates alone, and
        // every stronger claim is one an operation makes for itself.
        // The candidates close every arm, because the likeliest cause of this refusal is a typo or an auto-suffixed
        // name, and the caller who needs them is exactly the one who reached it (#380).
        // The sentence deliberately does not predict the resulting filename. UniqueStem takes a stem only when it is
        // free on both tests — no "houseCARL - <stem>" folder exists, and no active plugin is named "<stem>.esp" —
        // and suffixes it otherwise, so the fresh clause qualifies the name it offers rather than promising a file.
        // A rider that named its own folder parameter answers with THAT parameter; the enum arms are the record
        // lanes', where the spelling is the same on every caller (#357).
        // It says to DROP into= because every lane here takes the extend branch on a non-blank into= and never looks
        // at the fresh name: adding the parameter to the call that just failed returns this same refusal — a loop on
        // the rider lanes, which declare no into=/patch= exclusivity check to intercept it.
        throw new InvalidOperationException(ExtendRefusal(
            $"no houseCARL patch named '{stem}' — no owned folder holds '{espName}' and none is named "
            + $"'{ModFolderName(stem)}'"
            + (string.Equals(bareName, ModFolderName(stem), StringComparison.OrdinalIgnoreCase) ? "" : $" or '{bareName}'"),
            noFreshRule,
            OwnedPatchCandidates(needEsp, stem),
            riderNaming is { } rn
                ? $"dropping into= and passing {rn.Param}=\"{stem}\" for a fresh folder (auto-suffixed if that name is taken)"
                : freshPatch switch
                {
                    FreshPatchRemedy.NamedByPatchParam => $"dropping into= and passing patch=\"{stem}\" for a fresh patch (auto-suffixed if that name is taken)",
                    FreshPatchRemedy.CreatedByOmittingInto => "omitting into= to create it fresh",
                    _ => null,
                }));
    }

    /// <summary>The owned patches this refusal may offer: the <c>into=</c> spellings it will name, nearest first and
    /// capped, how many further spellings the cap dropped, plus how many owned patches no single spelling reaches (the
    /// count is what the sentence says when there is nothing to name, since "houseCARL owns none" would send the caller
    /// off to mint a duplicate). <paramref name="ScanFailed"/> is the third way the list comes back empty — the scan
    /// itself threw — and it is not "houseCARL owns none" either.</summary>
    readonly record struct PatchCandidates(IReadOnlyList<string> Tokens, int BeyondCap, int Unreachable, bool NeedEsp,
                                           bool ScanFailed);

    /// <summary>One extend refusal, composed: what went wrong, then what to try, in ONE sentence with the nearest
    /// owned patches named inside it (Aaron, 2026-09-05). <paramref name="noFreshRule"/> is a lane's own statement
    /// that it has no fresh-write route and why, and <paramref name="fresh"/> is that route where a lane has one;
    /// each is omitted when the lane offers neither. No in-place clause rides here: that lane is discoverable on the
    /// tool that declares it.</summary>
    static string ExtendRefusal(string wentWrong, string? noFreshRule, PatchCandidates candidates, string? fresh)
    {
        var nothingOwned = candidates.Tokens.Count == 0 && candidates.Unreachable == 0 && !candidates.ScanFailed;

        var tries = new List<string>();
        if (candidates.Tokens.Count > 0)
        {
            var t = candidates.Tokens;
            // The cap's drops are counted, so three names out of forty never read as the whole inventory.
            tries.Add((t.Count == 1 ? t[0] : string.Join(", ", t.Take(t.Count - 1)) + " or " + t[^1])
                    + (candidates.BeyondCap > 0 ? $" (+{candidates.BeyondCap} more)" : ""));
        }
        else if (candidates.Unreachable > 0)
            tries.Add($"renaming one of the {candidates.Unreachable} patch{(candidates.Unreachable == 1 ? "" : "es")} houseCARL owns "
                    + "in MO2, since no single into= spelling reaches any of them (their folder and plugin names collide)");
        // A scan that threw is the third empty list, and the one thing that must NOT be said on it is that houseCARL
        // owns nothing: a user with forty patches would be sent to mint a duplicate beside them.
        else if (candidates.ScanFailed)
            tries.Add("again once the mods folder can be read, since houseCARL could not scan it just now and so cannot "
                    + "name a patch or tell whether it owns any");
        if (fresh is not null) tries.Add(fresh);
        // A lane with no fresh route, on an install owning nothing yet, otherwise stops dead — the likeliest first-time
        // path, and the dead end #359/#380 are about. There is nothing to extend, but there is something to say: the
        // patch has to exist before this lane can touch it, and only a writing lane can make one.
        // The tools are named from the constants, so a rename cannot leave this handing out a retired spelling.
        if (tries.Count == 0 && noFreshRule is not null && nothingOwned)
            tries.Add($"making the patch first with a write that creates one ({ToolNames.Apply}, {ToolNames.Create} "
                    + $"or {ToolNames.Forward}), then naming it here");

        // Each fact is its own clause and only the LAST takes "and", so two of them cannot stack into ", and … , and …".
        var facts = new List<string> { wentWrong };
        if (noFreshRule is not null) facts.Add(noFreshRule);
        if (nothingOwned) facts.Add("houseCARL owns no patch " + (candidates.NeedEsp ? "holding a plugin yet" : "folder yet"));
        var said = facts.Count == 1 ? facts[0]
                 : string.Join(", ", facts.Take(facts.Count - 1)) + ", and " + facts[^1];
        return "cannot extend: " + said + (tries.Count == 0 ? "." : "; try " + string.Join(", or ", tries) + ".");
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

    /// <summary>One owned patch folder as this refusal saw it: the plugins are read for EVERY owned folder, not just
    /// the rendered ones, because whether a token resolves depends on which OTHER folders hold the same plugin.</summary>
    readonly record struct OwnedPatch(string Dir, string Name, IReadOnlyList<string> Plugins);

    /// <summary>The houseCARL-owned patches an extend refusal may name as <c>into=</c> spellings, so it offers
    /// candidates instead of asking the caller to guess (#380). Every spelling emitted is one that RESOLVES back to
    /// the patch it stands for: each candidate token is run through this resolver's own three arms against the
    /// folders read here, and on the record lane it must also land on a definite plugin, so a caller who takes one
    /// literally never meets a second refusal. A patch no token reaches is counted instead, and the sentence says so
    /// when there is nothing to name. Nearest <paramref name="stem"/> first — the caller who reached this refusal
    /// typed something close to what they meant — ranked by the shared name-suggester and then, for the names it
    /// vouches for none of, by an edit distance that always answers, so the order is nearest on every path rather than
    /// the alphabet wherever the suggester declines. Capped at three so the refusal stays one readable sentence, with
    /// the drops counted so the named few never read as the whole inventory. <paramref name="needEsp"/> drops the
    /// folders holding no plugin, which the record lane could not extend anyway. Best-effort: an unreadable ModsDir
    /// yields no candidates — not a partial set, which could name a spelling the next call refuses — and the scan's
    /// failure is carried out so the refusal says that rather than that houseCARL owns nothing.</summary>
    PatchCandidates OwnedPatchCandidates(bool needEsp, string stem)
    {
        const int cap = 3;
        var owned = new List<OwnedPatch>();
        var scanFailed = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_modsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsHouseCarlOwned(dir)) continue;
                var plugins = Directory.EnumerateFiles(dir)
                    .Where(f => PluginExts.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                owned.Add(new OwnedPatch(dir, Path.GetFileName(dir), plugins));
            }
        }
        // A throw partway through leaves a PARTIAL set, and reachability is decided by counting how many OTHER owned
        // folders hold the same plugin — so a dropped folder can make an ambiguous token look unambiguous and print a
        // spelling the next call refuses. No candidates is the documented degradation; half of them is a wrong answer.
        // The failure is carried out, because an empty list from a failed scan is not the same fact as an empty one.
        catch (IOException) { owned.Clear(); scanFailed = true; }
        catch (UnauthorizedAccessException) { owned.Clear(); scanFailed = true; }

        // Near-stem first (#380 asks for candidates near what the caller typed, not the alphabet's first eight).
        // Ranked by the same suggester the missed-plugin lookups use, over both the folder names and the plugin
        // names, so a typo'd folder and a typo'd plugin both float. The suggester answers EMPTY when nothing clears
        // its relevance bar, which left every folder tied and the list falling back to the alphabet while still being
        // called nearest — so the tie is broken by an edit distance that always answers, and the order is nearest
        // everywhere rather than only where the suggester vouches.
        var near = PluginNameSuggest.Nearest(stem, owned.SelectMany(o => o.Plugins.Prepend(o.Name)), max: 32);
        int NearRank(string name)
        {
            for (int i = 0; i < near.Count; i++)
                if (near[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }
        int Distance(string name) => PluginNameSuggest.StemDistance(stem, name);
        int Rank(OwnedPatch o) => o.Plugins.Prepend(o.Name).Select(NearRank).Min();
        int Near(OwnedPatch o) => o.Plugins.Prepend(o.Name).Select(Distance).Min();

        var rows = new List<string>();
        var beyondCap = 0;
        var unreachable = 0;
        foreach (var o in owned.OrderBy(Rank).ThenBy(Near))
        {
            if (needEsp && o.Plugins.Count == 0) continue;
            var tokens = ReachingTokens(owned, o, needEsp);
            if (tokens.Count == 0) { unreachable++; continue; }             // no spelling reaches it — never offer a false one
            // Nearest first inside a folder too: a folder reached only by its plugins offers several spellings, and
            // the cap means the one nearest what the caller typed is the one that has to survive.
            foreach (var t in tokens.OrderBy(NearRank).ThenBy(Distance))
                if (rows.Count < cap) rows.Add($"into=\"{t}\""); else beyondCap++;
        }
        // Two different facts reach an empty list, and only one of them is "there is nothing to extend". When every
        // owned patch was dropped because no single into= spelling reaches it, saying houseCARL owns none sends the
        // caller off to mint a fresh patch beside patches they could have extended — so the count is carried out.
        // The cap's own drops are counted too, so a named list never reads as the whole inventory. A scan that threw
        // is the third empty list, and it is not "there is nothing to extend" either.
        return new PatchCandidates(rows, beyondCap, unreachable, needEsp, scanFailed);
    }

    /// <summary>The <c>into=</c> spellings for one owned patch that this resolver actually routes back to it. The
    /// folder's own name first, since it is the most legible; where that name resolves elsewhere — another owned
    /// folder holds "&lt;folder name&gt;.esp", the by-plugin arm running before the folder catch-all — or where the
    /// folder holds several plugins and the record lane could not tell which to extend, the PLUGIN filenames stand in,
    /// one each. Bare names, quoted into <c>into=</c> by the caller. Empty when nothing reaches it.</summary>
    static List<string> ReachingTokens(List<OwnedPatch> owned, OwnedPatch target, bool needEsp)
    {
        var tokens = new List<string>();
        if (Reaches(target.Name)) tokens.Add(target.Name);
        else
            foreach (var p in target.Plugins)
                if (Reaches(p)) tokens.Add(p);
        return tokens;

        // The three resolution arms above, read off the folders already in hand rather than the disk: canonical
        // fast path, then the owned folder holding <stem>.esp, then the folder catch-all. A token also has to land
        // on ONE plugin on the record lane, which is what ResolveOutputPath does with the folder it gets back.
        bool Reaches(string token)
        {
            var s = PatchStem(token);
            var esp = s + ".esp";
            var canonicalName = ModFolderName(s);
            string? hit = null;
            var canon = owned.FirstOrDefault(o => o.Name.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));
            if (canon.Dir is not null && (!needEsp || canon.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)))
                hit = canon.Dir;
            if (hit is null)
            {
                var byEsp = owned.Where(o => o.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)).ToList();
                if (byEsp.Count > 1) return false;                          // the ambiguous arm refuses instead
                if (byEsp.Count == 1) hit = byEsp[0].Dir;
            }
            if (hit is null)
                foreach (var cand in new[] { Path.GetFileName(token.Trim()), canonicalName })
                {
                    var named = owned.FirstOrDefault(o => o.Name.Equals(cand, StringComparison.OrdinalIgnoreCase));
                    if (named.Dir is not null) { hit = named.Dir; break; }
                }
            if (hit is null || !hit.Equals(target.Dir, StringComparison.OrdinalIgnoreCase)) return false;
            return !needEsp
                || target.Plugins.Contains(esp, StringComparer.OrdinalIgnoreCase)   // the <stem>.esp fast path
                || target.Plugins.Count == 1;                                        // else the folder's sole plugin
        }
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
    static bool IsHouseCarlOwned(string folder) => HousecarlOwnerMeta.MarksOwned(folder);

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

    /// <summary>A user type SET to its getter Types: the union of each entry's resolution, in order, deduped — one
    /// grammar with the singular form, since every entry goes through <see cref="ResolveTypeFilter"/> and an unknown
    /// one throws naming itself. Null for an absent or empty set, so the unnarrowed path stays untouched.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types) => ResolveTypeFilterSet(types, out _);

    /// <summary>The display names a type SET resolves to — the same spelling a matched body's type renders as, so a
    /// caller can seat a requested type in a count table whether or not any record landed in it. Null for an absent
    /// set, and null for an unknown entry too: the surfaces that call this have already refused one by name, and a
    /// census is not the place to raise it a second time.</summary>
    public IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<string>? types)
    {
        try { return TypeDisplayNames(ResolveTypeFilterSet(types)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Seat every type the scan NAMED in a group_by=type census at zero, before a single match is counted.
    /// A requested type with no records then reads as a 0 row rather than being absent from the table, which a
    /// caller could only read by diffing the request against the response. No-op for the other count keys and for a
    /// scan that named no types.</summary>
    static void SeedRequestedTypes(Dictionary<string, int>? groups, string? groupBy, IReadOnlyList<Type>? types)
    {
        if (groups is null || groupBy != "type") return;
        foreach (var name in TypeDisplayNames(types) ?? Array.Empty<string>()) groups.TryAdd(name, 0);
    }

    /// <summary>The same names off the already-resolved getter Types.</summary>
    static IReadOnlyList<string>? TypeDisplayNames(IReadOnlyList<Type>? types) =>
        types is { Count: > 0 } ? types.Select(t => RecordNaming.StripGetterInterface(t.Name)).Distinct(StringComparer.Ordinal).ToList() : null;

    /// <summary>The same resolution, also spelling each entry with the arms it expanded to
    /// (<see cref="SweepScope.SpellTypeEntry"/>) — the label a response needs when it has to name the types that
    /// share one listing, since an entry like <c>GMST</c> names none of them by itself.</summary>
    IReadOnlyList<Type>? ResolveTypeFilterSet(IReadOnlyList<string>? types, out string? armLabel)
    {
        armLabel = null;
        if (types is not { Count: > 0 }) return null;
        var union = new List<Type>();
        var spelled = new List<string>(types.Count);
        foreach (var ts in types)
        {
            var entry = (ts ?? "").Trim();
            var arms = ResolveTypeFilter(entry);
            foreach (var t in arms)
                if (!union.Contains(t)) union.Add(t);
            spelled.Add(SweepScope.SpellTypeEntry(entry, arms));
        }
        armLabel = string.Join(", ", spelled);
        return union;
    }

    /// <summary>A user type string to its getter Types. Throws, naming the bad input and what is expected.
    /// <para>A BLANK entry is refused as blank, in this one place, so every surface that takes a type set — the
    /// records surface and the check sweep alike — states the same rule: an entry that names no type is refused
    /// saying it is blank, never quoted back as an unknown type <c>''</c>. An empty or absent SET is a different
    /// thing (no narrowing) and is handled by the callers.</para></summary>
    IReadOnlyList<Type> ResolveTypeFilter(string type)
    {
        if (type.Trim().Length == 0)
            throw new ArgumentException(
                "a blank record type — pass a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon'), " +
                "or omit the parameter to leave the types unnarrowed.");
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
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint. Reads through the stamp, so an outcome cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

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

    /// <summary>Which fields in <see cref="Record"/> carry the owned-child annotation, each with the
    /// <see cref="ChildUnion"/> the order assembles there, so a response render can state the clause once over the
    /// fields it actually emitted and name them. Null when this read annotated nothing.
    /// <para>Carried structurally rather than recovered by scanning the rendered prose for a marker, and carrying the
    /// paths rather than a bool: a clause that merely knows something was annotated cannot tell whether that
    /// something survived the medium's own truncation.</para></summary>
    public IReadOnlyDictionary<string, ChildUnion?>? OwnedChildFields { get; init; }

    /// <summary>Did this read ASSEMBLE the union, or state the index-only note? The scan lanes annotate without
    /// opening the other bodies, and the response-level clause has to say which of the two it is stating.</summary>
    public bool OwnedChildUnioned => OwnedChildFields is { } m && m.Values.Any(v => v is not null);

    /// <summary>Did this read annotate anything at all — the cheap question, for callers that only need to know
    /// whether a clause is POSSIBLE (the budget reservation) rather than which fields it would name.</summary>
    public bool OwnedChildNoted => OwnedChildFields is { Count: > 0 };

    public static ReadOutcome Fail(FormKey fk, string error) => new(fk, null, null, null, 0, null, error);
}

/// <summary>The outcome of a cross-plugin scan. <see cref="Error"/> non-null ⇒ the query was rejected (with a
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
    public OrderStamp? Stamp { get; init; }

    /// <summary>That build's fingerprint. Reads through the stamp, so an outcome cannot carry an epoch without the
    /// health of the build it names.</summary>
    public string? Epoch => Stamp?.Epoch;

    /// <summary>The reverse-reference index's accounting for this call: what a build it triggered cost, the index's
    /// own per-plugin freshness key, and the whole-order universe declaration. Null when the call did not use it.</summary>
    public string? ReverseIndexNote { get; init; }

    /// <summary>Plugins the winner scan could not open, by filename. A zero-match answer with one of these is bounded
    /// by the lock, not by the filter, and the render must not tell the caller otherwise.</summary>
    public IReadOnlyList<string> UnreadPlugins { get; init; } = Array.Empty<string>();

    /// <summary>The getter types the scan's own types= resolved to, or null when it named none. The render's bulk
    /// body gather passes them to <see cref="LoadOrderResolver.IndexView.CollectRecords"/>, which then seeks the
    /// GRUPs those types live in instead of walking the whole plugin. Never serialized.</summary>
    internal IReadOnlyList<Type>? GetterTypes { get; init; }

    /// <summary>The scan's pinned resolver and view, carried so the render's per-match fills — detail bodies,
    /// summaries, conflict trees — read off the same build the scan matched and <see cref="Epoch"/> names. Without
    /// it a freshness rebuild landing mid-render would make the response a single-build claim it does not satisfy.
    /// Pure data: an immutable snapshot reference holding no handles, and never serialized.</summary>
    internal LoadOrderService.ViewPin? Pin { get; init; }

    public static CrossQueryOutcome Fail(string error) => new(Array.Empty<FormKey>(), null, 0, false, error);
}

/// <summary>One row of a scan's <c>group_by=</c> aggregation: a group key (winner plugin / record type /
/// defining plugin) and how many matches fell in it. Emitted instead of per-match lines when group_by is set.</summary>
public sealed record GroupCount(string Key, int Count);

/// <summary>A compact, header-only record summary (no field dump) — the per-match line a scan emits
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
    string? Epoch = null,       // the resolver's current build fingerprint (SPEC §2.1.1) — the status line names it so a caller can match responses/artifacts to the build; nullable like every other carrier
    int ContainedRecordCount = 0);  // children this build recorded a containing record for — the '*parent' map's size, declared in band per SPEC §2.1 rather than left for a user to discover as memory

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
    SksePeekResult? Peek = null,
    string? ModVersion = null)
{
    /// <summary>The version the winning mod's MO2 meta.ini records, or null when the provider has no meta.ini (Stock
    /// Game, overwrite, a hand-installed mod). The THIRD number for a DLL — what the modder installed — next to the
    /// SKSE manifest's declaration and the DLL's own file version, which routinely disagree with it.</summary>
    public string? ModVersion { get; init; } = ModVersion;

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
    /// <c>source_provider=</c> accepts, so a caller can copy it back verbatim (#340).</summary>
    public string Text => HousecarlCore.AssetSourceSelection.Describe(Name, Kind);
}

/// <summary>The per-path data behind housecarl_nif_inspect: the VFS resolution of ONE mesh path joined to the
/// format-level <see cref="HousecarlCore.NifInspect"/> of the copy that was read. <see cref="Inspected"/> is the
/// provider whose bytes were parsed (the winner, or the <c>source_provider=</c>-named copy); <see cref="Providers"/> is the FULL
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
/// error)} describes the result. <see cref="OutputModFolder"/> is set on the default-lane success (enable it; on the
/// into= lane also sort it above <see cref="CurrentWinner"/>); <see cref="InPlacePath"/> is set on the in-place success
/// (the file overwritten in place).</summary>
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

    /// <summary>True when the edited mesh landed in a mod folder this call CREATED, false when into= added it to an
    /// existing one. MO2 registers an unseen folder at the highest priority, so a fresh folder out-ranks the current
    /// winner on enable while an into= folder has to be sorted above it.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder — the TOP loose root, above every mod
    /// folder, so neither enabling nor sorting takes the mesh off it and the only remedy is to move that copy.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder the edited mesh was written into — an into= re-edit
    /// of the same mesh. The edit already wins; sorting that folder above itself is not an instruction.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder. Either loses to an enabled
    /// mod's loose copy at any priority, so the edit wins on enable with no sort — on both lanes.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static NifSetResult OkNewFolder(string rel, NifProvider edited, IReadOnlyList<NifProvider> providers, bool ambiguous,
        HousecarlCore.NifSetReport report, string modFolder, bool freshFolder, string? winner, IReadOnlyList<string> warnings, string profileName)
        => new(rel, edited, providers, ambiguous, report, null, false, null, false, true, modFolder, null, winner, warnings, profileName)
            { FreshFolder = freshFolder };

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
public sealed record PlaceRequest(string AssetPath, string? Source, string? SourceProvider = null)
{
    /// <summary>This request is one half of a FaceGen pair expanded from a formid with no kind — two destinations
    /// sharing one member's source. A refusal about that source must not recommend a form the pair cannot take.</summary>
    public bool BothSlots { get; init; }
}

/// <summary>One placed asset's outcome. <see cref="Placed"/> false ⇒ <see cref="Error"/> names why (recoverable, per-asset
/// per asset). <see cref="CurrentWinner"/> is the source that currently wins the VFS for this path (the placed copy does
/// NOT win until the mod is enabled; on the into= lane it must also be sorted above this winner, while a fresh folder
/// out-ranks it on enable), or null if nothing provided it before.</summary>
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

    /// <summary>Whether <see cref="CurrentWinner"/> is MO2's overwrite folder. It is the TOP loose root, above every
    /// mod folder, so neither enabling a fresh folder nor any left-pane sort takes the path off it — the only remedy
    /// is to move or delete the overwrite copy, and the render has to say that instead.</summary>
    public bool WinnerIsOverwrite { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> IS the folder these bytes were placed into — a re-place into an
    /// enabled houseCARL patch. The placement already wins; telling the caller to sort the folder above itself is an
    /// instruction nobody can follow.</summary>
    public bool WinnerIsDestination { get; init; }

    /// <summary>Whether <see cref="CurrentWinner"/> is a BSA or the game's Data folder — the bottom of the root list.
    /// A BSA wins only when no loose copy exists and Data only when no mod provides the path, so the placed loose copy
    /// beats either the moment the mod is enabled, at any priority. No sort is owed, on either lane.</summary>
    public bool WinnerLosesOnEnable { get; init; }

    public static PlaceResult Fail(string assetPath, string error, string? currentWinner = null)
        => new(assetPath, false, 0, null, currentWinner, error);
}

/// <summary>The outcome of housecarl_place. <see cref="Error"/> non-null ⇒ the whole call was rejected
/// before any placement (unconfigured, an into= folder houseCARL doesn't own, the asset layer wouldn't build). Else
/// <see cref="Results"/> is per-asset; <see cref="ModFolder"/> is the houseCARL mod the placed files landed in (null when
/// none placed); <see cref="Warnings"/> carries the asset-discovery caveats; <see cref="LeftoverFolder"/> names a
/// fresh folder kept because it holds a partial result (no orphan is left for an all-failed fresh batch);
/// <see cref="FreshFolder"/> says which LANE ran.</summary>
public sealed record PlaceOutcome(
    IReadOnlyList<PlaceResult> Results, string? ModFolder, IReadOnlyList<string> Warnings, string? LeftoverFolder, string? Error)
{
    /// <summary>True when the files landed in a mod folder this call CREATED (the default lane), false when they were
    /// added to an existing folder (into=). MO2 registers a folder it has not seen at the highest priority, so a fresh
    /// folder out-ranks the current winner the moment it is ticked; an into= folder's priority is already fixed and has
    /// to be sorted. The two lanes therefore owe the caller different instructions.</summary>
    public bool FreshFolder { get; init; }

    /// <summary>Whether the CALL was served at all — not whether every destination placed. A served call with
    /// failed rows is a success carrying per-row errors, the way every other write outcome reads.</summary>
    public bool Success => Error is null;

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
    /// <c>out_path=</c> lane that file can be the mod's OWN shipped <c>.seq</c>, and houseCARL keeps no backup, so
    /// "wrote" and "replaced yours" are different facts about the disk and are reported as such.</summary>
    public bool Replaced { get; init; }

    /// <summary>The replaced file held EXACTLY the bytes just written, so nothing was lost. Only reachable when
    /// the byte-identical short-circuit was taken and its timestamp refresh then FAILED, sending an unchanged file
    /// down the write path: without this the response cries "OVERWRITTEN, no backup is kept"
    /// about a file it re-wrote identically.</summary>
    public bool ReplacedSameBytes { get; init; }

    /// <summary>The caller named <c>out_path=</c>, so the .seq landed in a folder the USER owns and no
    /// houseCARL mod folder was cut. Drives the confirmation: "enable this houseCARL mod in MO2" is the wrong next
    /// step for a file written into the user's own mod.</summary>
    public bool UserChoseOutput { get; init; }

    /// <summary>The note for an <c>out_path=</c> that neither MO2 nor the game reads SEQ files from. The
    /// .seq is correct; the engine will never see it, and every start-game-enabled quest in the plugin stays silently
    /// dead until it moves. Null when the destination deploys (and on every non-out_path lane, which lands in a
    /// houseCARL mod folder by construction).</summary>
    public string? DeployWarning { get; init; }

    public static SeqOutcome Fail(string error)
        => new(false, error, null, null, Array.Empty<HousecarlCore.SeqFile.SeqQuest>(), "", false);
}

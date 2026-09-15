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

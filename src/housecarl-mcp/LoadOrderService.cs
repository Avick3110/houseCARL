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
    // Set when a refresh found the profile changed but could not re-read it (something holds a profile file open), so
    // what every lane is serving is the build from BEFORE that change. Cleared the moment a refresh gets through, or
    // when the profile matches its baseline again. The asset lane says it in one sentence and keeps answering; the
    // record lane refuses on it, because its answer IS the order.
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
                        // The record index is the one answer that IS the load order, and this lane has no channel to
                        // say a refresh is pending, so a profile it could not re-read is refused rather than answered
                        // off a superseded index. The asset lane keeps serving and says so in its warnings: its answer
                        // is the VFS, which the record index has no part in.
                        if (_profileHeld is { } held) throw new ProfileUnreadableException(held.ProfilePath, held);
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

    /// <summary>The asset build's own discovery warnings, plus — when a profile change could not be re-read and this
    /// answer therefore comes off the build from before it — the one sentence saying so. Every asset lane reads its
    /// warnings through here, so no asset answer can be served off a kept build without stating it. Caller holds
    /// <see cref="_gate"/>.</summary>
    IReadOnlyList<string> AssetWarningsLocked()
    {
        if (_profileHeld is null) return _assetWarnings;
        var said = new List<string>(_assetWarnings.Count + 1) { ProfileHeldNote(_profileHeld) };
        said.AddRange(_assetWarnings);
        return said;
    }

    /// <summary>The sentence an answer served off a kept build carries: what changed, why it was not re-read, and that
    /// houseCARL re-reads on the next call by itself.</summary>
    static string ProfileHeldNote(ProfileUnreadableException held) =>
        $"the load order changed on disk and could not be re-read — '{Path.GetFileName(held.ProfilePath)}' is held " +
        "open by another process (MO2 holds these while it re-sorts), so this answer is off the build from BEFORE " +
        "that change. houseCARL re-reads on the next call; run this again once the file is free.";

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
        if (!ProfileFilesChanged()) { _profileHeld = null; return; }   // matches its baseline again → nothing pending, nothing to say
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
        Mo2OrderResult order;
        // MO2 holds the profile files while it rewrites them on a re-sort. A refresh that lands in that window keeps
        // the snapshot already built — the same answer the last call gave — and does NOT advance the baseline, so the
        // next call re-checks and follows the new profile. What is kept is REMEMBERED: every asset answer then carries
        // the sentence saying the order changed and could not be re-read, and the record lane refuses on it. The cold
        // builds have nothing to keep and report the transient instead.
        try { order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir, _overwriteDir); }
        catch (ProfileUnreadableException ex) { _profileHeld = ex; return; }
        _profileHeld = null;                                     // the re-read got through — nothing is pending any more
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

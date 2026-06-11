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
    bool _configured;                              // false ⇒ tools return the trained prompt instead of resolving
    readonly UserConfigStore _store;               // the sole owner of houseCARL.user.json (MO2 instance dir + tool paths)
    readonly int _maxPlugins;
    readonly object _gate = new();
    LoadOrderResolver? _resolver;
    CorpusRulebook? _rulebook;
    IReadOnlyList<string> _orderWarnings = Array.Empty<string>();
    DateTime _orderBuiltUtc = DateTime.MinValue;   // when the resolver last read the profile (the staleness baseline)
    DateTime _iniReadUtc = DateTime.MinValue;      // when ModOrganizer.ini was last read (instance-mode profile-switch baseline)
    IReadOnlyList<string> _resolvedPaths = Array.Empty<string>();   // ordered paths the current snapshot was built from (the cheap "did the order actually change?" check)

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
        svc._orderBuiltUtc = DateTime.UtcNow;
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
                    var builtUtc = DateTime.UtcNow;              // stamp BEFORE the read (TOCTOU): a profile write during the build is caught next call, not missed
                    var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir);
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
                    _orderBuiltUtc = builtUtc;
                }
                else
                {
                    RefreshOnProfileChange();     // to-do #6: lazy profile-membership refresh on THIS call (cheap-check first)
                    _resolver.RefreshIfStale();   // plugin-CONTENT freshness: cheap stat sweep; rebuilds if a plugin's bytes changed
                }
                return _resolver;
            }
        }
    }

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
        var view = Resolver.Capture();                             // force build/refresh; ONE build for count + exclusions (HCBR-2026-06-11-02)
        var comp = Mo2LoadOrder.ReadComposition(_profileDir);      // FRESH composition (always current)
        return new LoadOrderStatusData(
            comp, _orderWarnings, view.PluginCount, _maxPlugins, ProfileNewerThan(_orderBuiltUtc), _profileDir, view.ExcludedPlugins);
    }

    /// <summary>True if any of the three MO2 profile files has a newer mtime than the resolver's last build — i.e. the
    /// user toggled mods/plugins or re-sorted since, so the resolver's resolved set is behind the live profile.</summary>
    bool ProfileNewerThan(DateTime builtUtc)
    {
        foreach (var f in new[] { "loadorder.txt", "modlist.txt", "plugins.txt" })
        {
            var p = Path.Combine(_profileDir, f);
            if (File.Exists(p) && File.GetLastWriteTimeUtc(p) > builtUtc) return true;
        }
        return false;
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
        if (!ProfileNewerThan(_orderBuiltUtc)) return;           // nothing touched the active profile → nothing to do
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
        if (!File.Exists(ini) || File.GetLastWriteTimeUtc(ini) <= _iniReadUtc) return false;
        var iniReadUtc = DateTime.UtcNow;                        // stamp BEFORE the read (TOCTOU): an ini write during/after TryResolve is caught next call
        if (!Mo2Instance.TryResolve(_instanceDir, out var p) || p is null) return false;   // mid-write/invalid → keep last good, retry next call
        _iniReadUtc = iniReadUtc;                                // advance only on a clean read
        bool switched = !PathEq(p.ProfileDir, _profileDir) || !PathEq(p.ModsDir, _modsDir) || !PathEq(p.DataDir, _dataDir);
        if (!switched) return false;                             // ini touched but nothing we resolve from changed
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName;
        ReResolve();                                             // a new profile ⇒ the order differs ⇒ ReResolve deep-re-indexes
        return true;
    }

    /// <summary>The cheap re-read against the CURRENT profile roots: re-list the winning plugin paths from the text files,
    /// and pay the ~12s deep re-index ONLY when the resolved set/order actually changed. Caller holds the gate;
    /// <see cref="_resolver"/> is non-null. Used by both freshness signals (active-profile change + profile switch).</summary>
    void ReResolve()
    {
        var builtUtc = DateTime.UtcNow;                          // stamp BEFORE the read (TOCTOU): a write during the re-read is caught next call, not missed
        var order = Mo2LoadOrder.Build(_profileDir, _modsDir, _dataDir);
        var paths = order.OrderedPaths;
        if (_maxPlugins > 0 && paths.Count > _maxPlugins) paths = paths.Take(_maxPlugins).ToList();

        if (paths.Count > 0 && !paths.SequenceEqual(_resolvedPaths, StringComparer.OrdinalIgnoreCase))
        {
            // The active set/order genuinely changed → re-take the snapshot (the ~12s deep re-index). Build FIRST so the
            // old snapshot survives if it throws; only then dispose + swap.
            var rebuilt = LoadOrderResolver.Build(paths);
            _resolver!.Dispose();
            _resolver = rebuilt;
            _resolvedPaths = paths;
            _orderWarnings = order.Warnings;
            _orderBuiltUtc = builtUtc;
        }
        else if (paths.Count > 0)
        {
            // The profile was touched but the resolved order is identical (e.g. a no-plugin mod toggled) — no deep
            // re-index; just advance the freshness baseline so the staleness flag clears.
            _orderWarnings = order.Warnings;
            _orderBuiltUtc = builtUtc;
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
        var iniReadUtc = DateTime.UtcNow;                        // stamp BEFORE the read (TOCTOU): an ini write during/after Resolve is caught next call
        var p = Mo2Instance.Resolve(_instanceDir);               // throws (Q3) naming the missing piece if not a usable instance
        _profileDir = p.ProfileDir; _modsDir = p.ModsDir; _dataDir = p.DataDir; _profileName = p.ProfileName;
        _iniReadUtc = iniReadUtc;
    }

    static bool PathEq(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether houseCARL has an MO2 location to resolve against. False on a fresh install with no config — the
    /// server still runs; every tool returns the trained prompt until <see cref="SetInstance"/> is called.</summary>
    public bool IsConfigured { get { lock (_gate) { return _configured; } } }

    /// <summary>The active profile name (instance mode: ModOrganizer.ini selected_profile; explicit mode: the profile folder
    /// name); "" when unconfigured. For the status surface.</summary>
    public string ProfileName { get { lock (_gate) { return _profileName; } } }

    /// <summary>Point houseCARL at an MO2 instance folder — first-run setup AND switching between instances ("jump around").
    /// VALIDATES it (<see cref="Mo2Instance.Resolve"/> throws a clear Q3 message if it isn't usable — nothing is changed or
    /// persisted on failure), then re-points the live service (derives the roots + active profile, drops the cached resolver
    /// so the next tool call rebuilds against the new instance) and PERSISTS the choice to the user config file so it
    /// survives a restart. Returns the derived paths + whether the persist succeeded, for the tool's confirmation.</summary>
    public (Mo2InstancePaths paths, bool persisted, string? persistError) SetInstance(string instanceDir)
    {
        var paths = Mo2Instance.Resolve(instanceDir);            // throws (Q3) if not a usable MO2 instance — the tool renders the reason
        lock (_gate)
        {
            _instanceDir = paths.InstanceDir;
            _dataDir = paths.DataDir; _modsDir = paths.ModsDir; _profileDir = paths.ProfileDir; _profileName = paths.ProfileName;
            _iniReadUtc = DateTime.UtcNow;
            _configured = true;
            _resolver?.Dispose(); _resolver = null;              // force a rebuild against the new instance on the next query
            _resolvedPaths = Array.Empty<string>();
            _orderBuiltUtc = DateTime.MinValue;
            _orderWarnings = Array.Empty<string>();
        }
        var (persisted, persistError) = PersistInstanceDir(paths.InstanceDir);
        return (paths, persisted, persistError);
    }

    /// <summary>Persist the chosen instance dir through the shared <see cref="UserConfigStore"/> (read-modify-write), so it
    /// survives a restart AND coexists with any saved tool paths — the store never clobbers the other concern's field.
    /// Best-effort + HONEST (Q3): a write failure (e.g. a read-only data dir) is reported, not swallowed — the session
    /// still works, but the user is told the choice won't survive a restart.</summary>
    (bool ok, string? error) PersistInstanceDir(string instanceDir)
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
                $"Plugin '{plugin}' is not in the load order ({view.PluginCount} plugins indexed this session) — houseCARL " +
                "reads load-order truth only and does not open disabled plugins off disk. If this is a freshly written " +
                "houseCARL patch, it isn't enabled yet: enable + sort it in MO2, then re-read. To verify a write BEFORE " +
                "enabling, use the write call's own read-back (full_readback=true returns the whole written record). If a " +
                "prior write into this patch reported success, the edits DID land — do not re-issue them (re-running list " +
                "Adds would duplicate entries).");

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
        var rec = resolver.GetRecord(session, source, fk);
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
        var w = resolver.Capture().ResolveWinner(fk);   // one capture per summary (winner + depth from one build)
        if (w is null) return new RecordSummary(fk, "?", null, "?", 0, $"{fk} not in the load order");
        using var session = resolver.OpenSession();
        var body = resolver.GetRecord(session, w.Value.WinnerPlugin, fk);
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

        var resolver = Resolver;                                          // builds/refreshes the index
        var rulebook = Rulebook;

        // Map every op to a core PatchEdit, collecting ALL parse problems first (all-or-nothing, like the cleave).
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

        string outPath; bool extend;
        try { outPath = ResolveOutputPath(patchName, into, out extend); }
        catch (Exception ex) { return WritePatchBuilder.PatchOutcome.Fail(ex.Message); }

        return WritePatchBuilder.Apply(resolver, rulebook, edits, outPath, extend, fullReadback);
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

        var resolver = Resolver;                                          // builds/refreshes the index (Overlays for the re-serialize)

        // Parse every formid first, collecting ALL problems (all-or-nothing, like the edit path).
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

        // Resolve + ownership-gate the patch path via the into= (extend) path — must exist + carry the houseCARL marker.
        string outPath;
        try { outPath = ResolveOutputPath(patchName: null, into: patch, out _); }
        catch (Exception ex) { return WritePatchBuilder.RemovalOutcome.Fail(ex.Message); }

        return WritePatchBuilder.RemoveRecords(resolver, keys, outPath);
    }

    /// <summary>Create a BRAND-NEW record (housecarl_create_record) — the net-new authoring capability, the sibling of
    /// <see cref="ApplyEdits"/>. Resolves <paramref name="recordType"/> (catalog name or 4-char signature) to ONE concrete
    /// catalog name (unknown/ambiguous → Q3), maps the field <paramref name="operations"/> to core <see cref="WriteRequest"/>s
    /// rooted at that type (a create op takes NO formid — it sets fields on the new record), resolves the folder-per-patch
    /// output (fresh, or <paramref name="into"/> an existing houseCARL-owned patch), then drives
    /// <see cref="WritePatchBuilder.CreateRecords"/> (pre-flight ALL → AddNew → ApplyVerb → multi-master serialize). The new
    /// record's FormID is auto-allocated (local 0x800+) and reported; originals are never touched. FLAT records only — a
    /// nested/placed or abstract-group type fails loud with guidance.</summary>
    public WritePatchBuilder.CreateOutcome CreateRecords(string recordType, string editorid, IReadOnlyList<BulkOp> operations, string? patchName, string? into, bool fullReadback = false)
    {
        if (string.IsNullOrWhiteSpace(recordType))
            return WritePatchBuilder.CreateOutcome.Fail("record_type is required (a catalog name like 'Keyword'/'Spell'/'Weapon' or a 4-char signature like 'KYWD').");
        if (string.IsNullOrWhiteSpace(editorid))
            return WritePatchBuilder.CreateOutcome.Fail("editorid is required — the EditorID the new record is referenced by (e.g. in SkyPatcher/SPID).");

        var resolver = Resolver;
        var rulebook = Rulebook;

        // Resolve the type string → ONE concrete catalog name. Signature + name both work; unknown → Q3; an ambiguous
        // signature (maps to several types) → refuse and ask for the specific name.
        string catalogName;
        try
        {
            var types = ResolveTypeFilter(recordType.Trim());
            if (types.Count != 1)
                return WritePatchBuilder.CreateOutcome.Fail(
                    $"record_type '{recordType}' is ambiguous ({types.Count} matches) — use a specific catalog name (e.g. one of: {string.Join(", ", types.Select(t => RecordNaming.StripGetterInterface(t.Name)))}).");
            catalogName = RecordNaming.StripGetterInterface(types[0].Name);
        }
        catch (ArgumentException ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

        // Map each field op → a core WriteRequest rooted at the create type (all-or-nothing on a malformed one).
        var edits = new List<WriteRequest>(operations.Count);
        var problems = new List<string>();
        for (int i = 0; i < operations.Count; i++)
        {
            var req = MapCreateEdit(operations[i], i, catalogName, out var err);
            if (err is not null) problems.Add(err); else edits.Add(req!);
        }
        if (problems.Count > 0)
            return WritePatchBuilder.CreateOutcome.Fail(
                $"refused — {problems.Count} of {operations.Count} operation(s) malformed; NOTHING created:\n  - " + string.Join("\n  - ", problems));

        string outPath; bool extend;
        try { outPath = ResolveOutputPath(patchName, into, out extend); }
        catch (Exception ex) { return WritePatchBuilder.CreateOutcome.Fail(ex.Message); }

        var spec = new WritePatchBuilder.CreateSpec { RecordType = catalogName, EditorId = editorid.Trim(), Edits = edits };
        return WritePatchBuilder.CreateRecords(resolver, rulebook, new[] { spec }, outPath, extend, fullReadback);
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
    /// type (the validator roots them at the struct schema, so it's a label). Q3 on a malformed spec.</summary>
    StructSpec? MapStruct(StructInput s, string where, out string? error)
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
                sets.Add(new WriteRequest
                {
                    RecordType = s.Type!, Path = SplitPath(ns.Path),
                    Verb = string.IsNullOrWhiteSpace(ns.Verb) ? "Set" : ns.Verb, Key = ns.Key, Value = ns.Value,
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
    /// collision. The caller name is reduced to a bare stem (no directory parts) so it can never escape ModsDir.</summary>
    string ResolveOutputPath(string? patchName, string? into, out bool extend)
    {
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
        var plugin = freeStem + ".esp";
        WriteOwnerMeta(newFolder, plugin);
        return Path.Combine(newFolder, plugin);
    }

    /// <summary>Resolve a houseCARL-owned MOD FOLDER under ModsDir for a NON-.esp output (compiled scripts, a packed .bsa,
    /// extracted loose files) — the folder-per-patch model generalised beyond the .esp write path. A fresh marker-stamped
    /// folder (<paramref name="defaultStem"/> names it when patchName is blank; auto-suffixed so a prior one is never
    /// clobbered) or <paramref name="into"/> an existing houseCARL-owned one. ORIGINALS UNTOUCHED (Q3): refuses a folder
    /// houseCARL didn't create. Derives ModsDir CHEAPLY (reads ModOrganizer.ini; NO ~10s index build). Throws the trained
    /// prompt when unconfigured. Reuses the same ownership/marker helpers as the .esp write path.</summary>
    public string ResolvePatchModFolder(string? patchName, string? into, string defaultStem)
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
                return folder;
            }

            var newStem = UniqueStem(PatchStem(string.IsNullOrWhiteSpace(patchName) ? defaultStem : patchName!));
            var newFolder = Path.Combine(_modsDir, ModFolderName(newStem));
            Directory.CreateDirectory(newFolder);
            WriteOwnerMeta(newFolder, "(houseCARL output)");   // ownership marker; this folder may hold scripts / a .bsa / loose files, not an .esp
            return newFolder;
        }
    }

    /// <summary>The <c>Scripts\</c> output folder for a COMPILED .pex (the compile rider) — a houseCARL mod folder via
    /// <see cref="ResolvePatchModFolder"/> plus its <c>Scripts\</c> subfolder, where MO2 deploys compiled Papyrus into the
    /// game's Data\Scripts.</summary>
    public string ResolveCompiledScriptFolder(string? patchName, string? into)
    {
        var folder = ResolvePatchModFolder(patchName, into, "houseCARL_Scripts");
        var scripts = Path.Combine(folder, "Scripts");
        Directory.CreateDirectory(scripts);
        return scripts;
    }

    /// <summary>The MO2 mod-folder name for a patch stem. The "houseCARL - " prefix groups our patches in MO2's left
    /// pane and is the human-visible ownership signal (the meta.ini marker is the structural one).</summary>
    static string ModFolderName(string stem) => "houseCARL - " + stem;

    /// <summary>Reduce a caller name to a safe bare STEM — no extension, no directory parts (so "../x" / "C:\y" can't
    /// escape ModsDir). The plugin is always <c>&lt;stem&gt;.esp</c>; the mod folder is <c>houseCARL - &lt;stem&gt;</c>.</summary>
    static string PatchStem(string raw)
    {
        var name = Path.GetFileName(raw.Trim());
        var stem = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrEmpty(stem) ? "houseCARL_Patch" : stem;
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
    /// their variants so a signature query unions them. A corpus AQ name that won't load is skipped here and
    /// surfaces as "unknown type" at query time — never a silent wrong type.</summary>
    static Dictionary<string, List<Type>> BuildTypeLookup()
    {
        var lookup = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, Type t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<Type>();
            if (!list.Contains(t)) list.Add(t);
        }
        foreach (var ts in CorpusRulebook.LoadCorpus().Types.Values)
        {
            if (ts.Kind != "record") continue;
            var t = Type.GetType(ts.GetterInterfaceAssemblyQualified);
            if (t is null) continue;
            Add(ts.Name, t);
            Add(ts.Signature, t);
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
        lock (_gate) { _resolver?.Dispose(); _resolver = null; }
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
    IReadOnlyDictionary<string, string> ExcludedPlugins);

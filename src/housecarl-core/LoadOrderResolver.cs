using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  LoadOrderResolver — the net-new load-order resolver (MCP step §8.3, fork §6-C).
//
//  This is the read-side capability §5.2 of the PRFAQ specifies, realized:
//    held structural index (small) + on-demand targeted parse, holding no record bodies —
//    AND, under Option B (Aaron-locked 2026-06-04), holding NO PLUGIN FILE HANDLES AT REST.
//
//  SHAPE (Aaron-confirmed 2026-06-01 off the body-fetch probe; handle model proven by handle-probe 2026-06-04):
//    • Held index — PURE DATA, ZERO file handles — built by enumerating every plugin ONE AT A TIME (low→high
//      priority: open i → enumerate → DISPOSE → i+1, never all-open-at-once):
//        - _index    : FormKey → (winnerOverlay, overrideCount)  — ALL keys, the O(1) "what wins" fast path (§8.1).
//        - _overriders: FormKey → ordered overlay indices         — MULTI-override keys ONLY (the "list of touching
//                        plugins" §5.2 calls for; singletons' sole overrider IS the winner, so they need no list).
//      ~125–185 MB at full-modlist scale (within §5.2's "few hundred MB"); NO record bodies, NO mmap handles held.
//    • On-demand body fetch = open the plugin, re-enumerate + match the FormKey, then DISPOSE when the work ends.
//      A per-call OverlaySession (see OpenSession) opens each plugin a tool call touches AT MOST ONCE and disposes
//      every one when the call returns. Measured (handle-probe 2026-06-04): open/read/dispose ~0.3–0.8 ms, invisible
//      under the 200–2000 ms LLM round-trip; no leak. The write path takes its known-master set + a nested-override
//      link cache from the SAME session, so a write opens handles only for its own duration too.
//    • mtime freshness = re-stat the plugin files on demand; rebuild the index (one-at-a-time again) if any changed.
//      No live MO2 tracking; no held overlays to dispose/reopen.
//
//  WHY zero handles at rest (Option B): a Windows mmap overlay opened without FILE_SHARE_DELETE LOCKS its file
//  against delete / rename / overwrite — exactly MO2's, xEdit's, and Explorer's workflow. The prior build held
//  EVERY plugin open for the whole process (~3,400 locks), which IS the retrospective's ship-blocking
//  "cleanup-gotcha" (RETROSPECTIVE_PIVOT §37). Holding zero handles at rest makes the lock ABSENT (not merely
//  permissive) and every read always-live (no stale-view seam) — what CLAUDE.md §1 already promises ("no held
//  state… cheap mtime re-checks not live-tracking… no MO2 lock-fighting"), now true by construction.
//
//  ORDER IS INJECTED. Build takes the plugin paths already in priority order. Override COUNTS/DEPTHS and tree
//  MEMBERSHIP are order-independent and correct now; winner IDENTITY is only as correct as the injected order
//  — pinning the true active order (plugins.txt / MO2 USVFS) + xEdit-verifying it is the §8.5 gate, not this class.
//
//  Q3 (no silent failure): per-plugin open failures during the index build are COLLECTED and surfaced
//  (LoadFailures), never skipped silently; a body the index says exists but the plugin can't yield throws.
// ======================================================================

/// <summary>One plugin's version of a record in a conflict tree (the body is fetched on demand, not held).</summary>
public sealed record ConflictNode(string Plugin, IMajorRecordGetter Record);

/// <summary>A record's full conflict tree: every touching plugin's body, in priority order (winner last).</summary>
public sealed record ConflictTree(FormKey FormKey, string RecordType, IReadOnlyList<ConflictNode> Nodes)
{
    public ConflictNode Winner => Nodes[^1];
    public bool IsConflict => Nodes.Count > 1;
}

/// <summary>The winner + depth for a FormKey, without fetching any body (the O(1) fast path).</summary>
public readonly record struct WinnerInfo(FormKey FormKey, string WinnerPlugin, int OverrideDepth);

/// <summary>One record of a plugin with its whole-order conflict status (no body fetched).</summary>
public readonly record struct RecordStatus(
    FormKey FormKey, string RecordType, bool PluginWins, int OverrideDepth, IReadOnlyList<string> TouchingPlugins);

public sealed class LoadOrderResolver : IDisposable
{
    readonly string[] _paths;                          // every active plugin's path, priority order (masters → … → winner)
    readonly string[] _names;                          // index → plugin filename (e.g. "Skyrim.esm"); == Path.GetFileName(path)
    readonly Dictionary<string, int> _nameToIdx;       // plugin filename → index (last copy of a duplicate name wins = priority)
    DateTime[] _mtimes;                                // last-write at the last index build, per path (freshness baseline)

    Dictionary<FormKey, (int winner, int count)> _index;   // ALL keys — winner + depth, O(1)
    Dictionary<FormKey, int[]> _overriders;                // MULTI keys only — ordered touching overlay indices
    List<string> _loadFailures = new();                    // per-plugin open failures from the last index build (Q3)

    /// <summary>Overlay-open failures from the last index build — surfaced, never silently skipped (Q3).</summary>
    public IReadOnlyList<string> LoadFailures => _loadFailures;

    public int PluginCount => _paths.Length;
    public int RecordCount => _index.Count;            // distinct FormKeys across the order
    public int ConflictCount => _overriders.Count;     // FormKeys overridden by >1 plugin
    public int MaxDepth { get; private set; }

    /// <summary>Every plugin's filename, in priority order (PURE DATA — no handles). The known-name list the write
    /// harnesses scan to decide which masters are in the order; replaces the old held-overlay ModKey enumeration.</summary>
    public IReadOnlyList<string> PluginNames => _names;

    // ---- Per-call overlay session (Option B: open on demand, dispose at call end; ZERO handles at rest) ----

    /// <summary>Open a per-call overlay session. ONE tool invocation (or one write) opens every plugin it needs
    /// THROUGH the session — each at most once — and DISPOSES the session when the call returns, releasing every
    /// handle. Between calls the resolver holds none. A session is single-call/single-thread; the index it reads is
    /// immutable between rebuilds, so concurrent calls each take their own session and never share open overlays.</summary>
    public OverlaySession OpenSession() => new(this);

    /// <summary>The lifetime scope for the overlays one tool call needs: opens each plugin lazily, caches it for the
    /// call (so a record fetched, then read field-by-field, stays valid), and disposes them ALL on Dispose. The reader
    /// keeps results valid by holding the session open until it has materialised what it returns (the service reads
    /// fields off a fetched body before its session disposes; the write path keeps the source body + link cache valid
    /// through serialize).</summary>
    public sealed class OverlaySession : IDisposable
    {
        readonly LoadOrderResolver _r;
        readonly Dictionary<int, ISkyrimModGetter> _open = new();
        internal OverlaySession(LoadOrderResolver r) => _r = r;

        /// <summary>The plugin at <paramref name="idx"/>, opened once and cached for this call (lazy mmap overlay —
        /// records parse on access). Released when the session disposes.</summary>
        internal ISkyrimModGetter Overlay(int idx)
        {
            if (!_open.TryGetValue(idx, out var ov))
                _open[idx] = ov = SkyrimMod.CreateFromBinaryOverlay(_r._paths[idx], SkyrimRelease.SkyrimSE);
            return ov;
        }

        /// <summary>Open EVERY plugin (priority order) and return them as the FULL known-master set the multi-master
        /// write path hands the serializer (<see cref="WriteEngine.WritePatch(SkyrimMod,System.Collections.Generic.IReadOnlyList{ISkyrimModGetter},string)"/>):
        /// with every master resolvable + ordered, a cross-master patch serializes with a lean only-referenced header.
        /// Opened for THIS write only and disposed with the session (Option B). [Tier-1: the full set — byte-identical to
        /// the xEdit-proven write path. A future Tier-2 could open only the patch-referenced masters so even a write stays
        /// near-handle-free; a tracked optimization, not done here.]</summary>
        public IReadOnlyList<ISkyrimModGetter> AllMasters()
        {
            var arr = new ISkyrimModGetter[_r._paths.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = Overlay(i);
            return arr;
        }

        /// <summary>An immutable link cache over ONE named plugin (opened in this session) — built ON DEMAND for the
        /// write path to reconstruct a NESTED record's parent chain when overriding it (Cell / the Placed* family / INFO
        /// / Navmesh / Landscape; the winner overlay is where the winning nested override + its context live). COSTLY and
        /// never held past the session (a per-mod link cache is GBs to retain). null if the plugin isn't in the order.</summary>
        public ILinkCache? LinkCacheFor(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int idx) ? Overlay(idx).ToImmutableLinkCache() : null;

        public void Dispose()
        {
            foreach (var ov in _open.Values) (ov as IDisposable)?.Dispose();
            _open.Clear();
        }
    }

    LoadOrderResolver(string[] paths, string[] names, Dictionary<string, int> nameToIdx, DateTime[] mtimes)
    {
        _paths = paths; _names = names; _nameToIdx = nameToIdx; _mtimes = mtimes;
        _index = new(); _overriders = new();
        BuildIndex();
    }

    /// <summary>Take the plugin paths already in priority order and build the index — WITHOUT holding any plugin open
    /// (Option B). Names + mtimes come from the path list + a stat (no parse, no handle); the index build
    /// (<see cref="BuildIndex"/>) opens each plugin one at a time to enumerate it, then disposes it. <paramref
    /// name="orderedPluginPaths"/> = masters → … → highest priority (the order is INJECTED; §8.5 supplies the true
    /// active order). Per-plugin open failures are collected into <see cref="LoadFailures"/> at index time (Q3), never
    /// silently skipped.</summary>
    public static LoadOrderResolver Build(IReadOnlyList<string> orderedPluginPaths)
    {
        var paths = new string[orderedPluginPaths.Count];
        var names = new string[orderedPluginPaths.Count];
        var mtimes = new DateTime[orderedPluginPaths.Count];
        var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < orderedPluginPaths.Count; i++)
        {
            var p = orderedPluginPaths[i];
            paths[i] = p;
            // A plugin's ModKey filename IS its file name (how Mutagen derives the overlay's ModKey), so the name needs
            // no open — keeping Build itself handle-free. last copy of a duplicate name wins its slot (priority).
            var name = Path.GetFileName(p);
            names[i] = name;
            nameToIdx[name] = i;
            mtimes[i] = SafeMtime(p);
        }

        return new LoadOrderResolver(paths, names, nameToIdx, mtimes);
    }

    /// <summary>Enumerate every plugin once (low→high), ONE AT A TIME (open → enumerate → dispose), building the
    /// winner/count index for all keys and the ordered overrider list for multi-override keys only. Single pass, no
    /// all-keys list ever materialized, and at most ONE plugin handle open at any instant (Option B — never the floor).
    /// A plugin that won't open is recorded in <see cref="LoadFailures"/> and contributes no records (Q3).</summary>
    void BuildIndex()
    {
        var index = new Dictionary<FormKey, (int winner, int count)>();
        var overriders = new Dictionary<FormKey, List<int>>();        // multi keys only
        var failures = new List<string>();
        int maxDepth = 0;

        for (int i = 0; i < _paths.Length; i++)
        {
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(_paths[i], SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { failures.Add($"{_names[i]}: {ex.GetType().Name} {ex.Message}"); continue; }
            try
            {
                foreach (var rec in ov.EnumerateMajorRecords())
                {
                    var fk = rec.FormKey;
                    if (!index.TryGetValue(fk, out var e))
                    {
                        index[fk] = (i, 1);                                // first sighting — singleton so far, no list
                    }
                    else
                    {
                        int newCount = e.count + 1;
                        index[fk] = (i, newCount);                          // higher overlay = new winner
                        if (newCount == 2) overriders[fk] = new List<int> { e.winner, i };  // 2nd sighting promotes to multi
                        else overriders[fk].Add(i);                         // 3rd+ extends the list
                        if (newCount > maxDepth) maxDepth = newCount;
                    }
                }
            }
            finally { (ov as IDisposable)?.Dispose(); }                    // one plugin open at a time — never the whole floor
        }

        _index = index;
        _overriders = overriders.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());  // trim List overhead → int[]
        MaxDepth = maxDepth;
        _loadFailures = failures;
    }

    // ---- Queries -------------------------------------------------------

    /// <summary>O(1): the winning plugin + override depth for a FormKey. null if the FormKey isn't in the order.</summary>
    public WinnerInfo? ResolveWinner(FormKey fk)
        => _index.TryGetValue(fk, out var e) ? new WinnerInfo(fk, _names[e.winner], e.count) : null;

    /// <summary>Every FormKey overridden by more than one plugin (the whole-order conflict set).</summary>
    public IEnumerable<FormKey> ConflictKeys() => _overriders.Keys;

    /// <summary>The ordered touching-plugin names for a FormKey (priority order, winner last) — no body fetched.
    /// The atom behind every conflict-status question.</summary>
    public IReadOnlyList<string>? TouchingPlugins(FormKey fk)
    {
        if (!_index.TryGetValue(fk, out var e)) return null;
        if (e.count == 1) return new[] { _names[e.winner] };           // singleton: sole overrider = winner
        return Array.ConvertAll(_overriders[fk], i => _names[i]);
    }

    /// <summary>The full conflict tree: every touching plugin's body, in priority order (winner last). Bodies are
    /// fetched on demand into <paramref name="session"/> (which keeps the touched plugins open until the caller has
    /// materialised them, then disposes them). null if the FormKey isn't in the order.</summary>
    public ConflictTree? ResolveTree(OverlaySession session, FormKey fk)
    {
        if (!_index.TryGetValue(fk, out var e)) return null;
        var overlayIdxs = e.count == 1 ? new[] { e.winner } : _overriders[fk];
        var nodes = new ConflictNode[overlayIdxs.Length];
        string? recType = null;
        for (int n = 0; n < overlayIdxs.Length; n++)
        {
            int oi = overlayIdxs[n];
            var rec = FetchBody(session, oi, fk);
            recType ??= RecordNaming.StripOverlay(rec.GetType().Name);
            nodes[n] = new ConflictNode(_names[oi], rec);
        }
        return new ConflictTree(fk, recType ?? "?", nodes);
    }

    /// <summary>Every record in one plugin with its whole-order conflict status (no bodies fetched). Drives "what is
    /// this plugin overwriting / being overwritten on" (capabilities 1, 2, 6). Opens the plugin for the enumeration and
    /// disposes it when the enumeration ends (Option B — self-scoped, one handle); the yielded status is pure data.</summary>
    public IEnumerable<RecordStatus> PluginRecordStatus(string pluginName)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx))
            throw new ArgumentException($"plugin not in the load order: {pluginName}");
        var ov = SkyrimMod.CreateFromBinaryOverlay(_paths[idx], SkyrimRelease.SkyrimSE);
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                var fk = rec.FormKey;
                var e = _index[fk];                                        // present by construction (we enumerated it)
                var touching = e.count == 1 ? new[] { _names[e.winner] } : Array.ConvertAll(_overriders[fk], i => _names[i]);
                yield return new RecordStatus(fk, RecordNaming.StripOverlay(rec.GetType().Name),
                                              PluginWins: e.winner == idx, OverrideDepth: e.count, TouchingPlugins: touching);
            }
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Fetch one record's body from a NAMED plugin in the order (re-enum into <paramref name="session"/>).
    /// Returns null if the plugin isn't in the order or doesn't define this FormKey — the nullable, public sibling of
    /// the private <see cref="FetchBody"/> (which throws on an index inconsistency). The server's read_record uses this
    /// for an explicit-plugin read, and for the winner's body off <see cref="ResolveWinner"/>. The returned body is
    /// backed by the session's overlay — read it before the session disposes.</summary>
    public IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx)) return null;
        foreach (var rec in session.Overlay(idx).EnumerateMajorRecords())
            if (rec.FormKey == fk) return rec;
        return null;
    }

    /// <summary>Fetch one record body from one overlay by re-enumerating it (primitive B — into the session). Throws if
    /// the overlay can't yield a FormKey the index says it contains (a real inconsistency, named — Q3).</summary>
    IMajorRecordGetter FetchBody(OverlaySession session, int overlayIdx, FormKey fk)
    {
        foreach (var rec in session.Overlay(overlayIdx).EnumerateMajorRecords())
            if (rec.FormKey == fk) return rec;
        throw new InvalidOperationException(
            $"body-fetch inconsistency: {_names[overlayIdx]} is indexed as containing {fk} but did not yield it on re-enumeration.");
    }

    // ---- Cross-query scan primitives (§8.4 Beat B.2) -------------------
    //  These feed cross_plugin_query. Each is a SINGLE enumeration pass that yields the matching record's body
    //  IN HAND (no per-candidate re-fetch — the naive "get each winner body separately" was measured at ~100 s
    //  over 9k weapons because GetRecord re-enumerates a whole overlay per call). The body the SERVICE filters
    //  on (editorid/references) is this in-hand body; the resolver holds nothing past the yield. Each opens the
    //  CURRENT plugin, enumerates it, and DISPOSES it before moving to the next (Option B — one handle at a time;
    //  every yielded body is consumed by the caller before the iterator advances to the next plugin).

    /// <summary>Stream every record of the given type(s) whose instance in this overlay IS the load-order winner
    /// — i.e. the WINNER body, in hand, for each distinct typed FormKey (no re-fetch). Typed group enumeration
    /// (Mutagen seeks the GRUP). Multiple types (GMST → 4 GameSetting variants) are unioned. Yields
    /// (FormKey, override-depth, winner body). The throw-if-unknown guard is Q3 belt-and-braces (corpus-resolved
    /// types are always real).</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(IReadOnlyList<Type> getterTypes)
    {
        for (int i = 0; i < _paths.Length; i++)
        {
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(_paths[i], SkyrimRelease.SkyrimSE); }
            catch { continue; }                                            // an unopenable plugin wins nothing (surfaced at build)
            try
            {
                foreach (var t in getterTypes)
                    foreach (var rec in ov.EnumerateMajorRecords(t, throwIfUnknown: true))
                        if (_index.TryGetValue(rec.FormKey, out var e) && e.winner == i)   // this overlay's instance wins
                            yield return (rec.FormKey, e.count, rec);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Stream every record contained in the given plugins (optionally only of the given type(s)), each
    /// with that PLUGIN'S body in hand — the plugin-scoped path (the Q4.9 plugin_dump fold + a plugin-content
    /// audit). A FormKey touched by more than one scoped plugin is yielded once per scoped plugin (the SERVICE
    /// de-dupes). Yields (FormKey, whole-order override-depth, the scoped plugin's body). Holds nothing.</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
    {
        foreach (int i in ScopeIndices(plugins))
        {
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(_paths[i], SkyrimRelease.SkyrimSE); }
            catch { continue; }
            try
            {
                IEnumerable<IMajorRecordGetter> recs = getterTypes is null
                    ? ov.EnumerateMajorRecords()
                    : getterTypes.SelectMany(t => ov.EnumerateMajorRecords(t, throwIfUnknown: true));
                foreach (var rec in recs)
                    if (_index.TryGetValue(rec.FormKey, out var e))
                        yield return (rec.FormKey, e.count, rec);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Resolve a scope (plugin filenames) to overlay indices; null/empty = the whole order. Throws
    /// (Q3) on a name not in the order, naming it — never silently scans an empty/partial scope.</summary>
    IReadOnlyList<int> ScopeIndices(IReadOnlyList<string>? scopePlugins)
    {
        if (scopePlugins is null || scopePlugins.Count == 0)
            return Enumerable.Range(0, _paths.Length).ToArray();
        var idxs = new List<int>(scopePlugins.Count);
        foreach (var name in scopePlugins)
        {
            if (!_nameToIdx.TryGetValue(name, out int i))
                throw new ArgumentException($"plugin not in the load order: {name}");
            idxs.Add(i);
        }
        return idxs;
    }

    // ---- Freshness -----------------------------------------------------

    /// <summary>Re-stat the plugin files; if any last-write differs from the build-time baseline, rebuild the index
    /// (re-enumerating one plugin at a time — Option B, no held overlays to dispose/reopen), then return true. The cheap
    /// no-change path is just the stat sweep. Content edits to existing plugins are handled here; a changed plugin SET
    /// (added/removed) = a new order → the caller re-Builds. Called by the server per query-batch (§8.4).</summary>
    public bool RefreshIfStale()
    {
        bool stale = false;
        for (int i = 0; i < _paths.Length; i++)
            if (SafeMtime(_paths[i]) != _mtimes[i]) { stale = true; break; }
        if (!stale) return false;

        for (int i = 0; i < _paths.Length; i++) _mtimes[i] = SafeMtime(_paths[i]);
        BuildIndex();
        return true;
    }

    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>Option B: the resolver holds NO plugin file handles at rest (only the pure-data index), so there is
    /// nothing to release — Dispose is a no-op, kept so the service can treat a resolver as a disposable resource it
    /// builds + swaps over its lifetime (and so `using var resolver = …` call sites stay unchanged).</summary>
    public void Dispose() { }
}

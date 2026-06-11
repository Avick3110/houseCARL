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
//        - Index     : FormKey → (winnerOverlay, overrideCount)  — ALL keys, the O(1) "what wins" fast path (§8.1).
//        - Overriders: FormKey → ordered overlay indices          — MULTI-override keys ONLY (the "list of touching
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
//  Q3 (no silent failure): a plugin the index build cannot fully read — it won't OPEN, or it contains a
//  record Mutagen cannot PARSE (a strict-validation throw mid-enumeration; the common real case is a
//  malformed subrecord an upstream ESP ships and the game engine ignores) — is EXCLUDED wholesale and the
//  reason is COLLECTED + surfaced (LoadFailures / ExcludedPlugins → load_order_status), never skipped
//  silently. ONE such record used to kill the whole build (bricking EVERY tool call, since all resolve
//  through this index); now it costs only its own plugin. The throw is non-resumable, so we can't skip just
//  the bad record (Mutagen 0.53.1: the group enumerator can't advance past it) — exclusion is per-PLUGIN,
//  and atomic (a partially-read plugin never half-populates the index). A body the index says exists but the
//  plugin can't yield still throws (a real inconsistency, named).
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

    /// <summary>One index build's ENTIRE output, swapped in as a SINGLE reference write. The service refreshes the
    /// index under its own gate, but READERS run outside that gate (concurrent tool calls hold the resolver while a
    /// sibling call's freshness check may rebuild) — when the per-build state lived in five separate fields, a reader
    /// could observe a NEW index next to OLD overriders mid-swap (a KeyNotFound on a freshly-multi key, surfaced as an
    /// opaque transport error pre-Guard). Bundling the build into one immutable snapshot, captured ONCE per operation
    /// (iterators capture at enumeration start), makes a torn view impossible by construction (HCBR-2026-06-11-01
    /// hardening). The volatile field gives the swap release/acquire visibility.</summary>
    internal sealed class IndexSnapshot   // internal (not private) so IndexView's ctor can take it; never leaves the assembly
    {
        public readonly Dictionary<FormKey, (int winner, int count)> Index;   // ALL keys — winner + depth, O(1)
        public readonly Dictionary<FormKey, int[]> Overriders;                // MULTI keys only — ordered touching overlay indices
        public readonly List<string> LoadFailures;                            // per-plugin index-build failures (open OR parse), surfaced (Q3)
        public readonly HashSet<int> Excluded;                                // overlay indices excluded this build — never re-touched by any path
        public readonly Dictionary<string, string> ExcludedPlugins;           // excluded plugin name → reason (Q3)
        public readonly int MaxDepth;

        public IndexSnapshot(Dictionary<FormKey, (int winner, int count)> index, Dictionary<FormKey, int[]> overriders,
                             List<string> loadFailures, HashSet<int> excluded, Dictionary<string, string> excludedPlugins, int maxDepth)
        { Index = index; Overriders = overriders; LoadFailures = loadFailures; Excluded = excluded; ExcludedPlugins = excludedPlugins; MaxDepth = maxDepth; }
    }

    volatile IndexSnapshot _snap;

    /// <summary>Per-plugin index-build failures (couldn't open, or contains a record Mutagen can't parse) — each
    /// excluded plugin named with its reason, surfaced never silently skipped (Q3). Same content as
    /// <see cref="ExcludedPlugins"/>, formatted "name: reason" for log/harness display.</summary>
    public IReadOnlyList<string> LoadFailures => _snap.LoadFailures;

    /// <summary>Plugins EXCLUDED from this build (name → why): unopenable, or carrying a record Mutagen can't parse.
    /// Their records are not in the index and no path will re-touch them; load_order_status reports them so the user
    /// can fix/remove the upstream plugin (Q3 — the exclusion is visible, not silent).</summary>
    public IReadOnlyDictionary<string, string> ExcludedPlugins => _snap.ExcludedPlugins;

    public int PluginCount => _paths.Length;
    public int RecordCount => _snap.Index.Count;            // distinct FormKeys across the order
    public int ConflictCount => _snap.Overriders.Count;     // FormKeys overridden by >1 plugin
    public int MaxDepth => _snap.MaxDepth;

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
        /// near-handle-free; a tracked optimization, not done here.] To write INTO a patch that is itself ACTIVE in the
        /// order, the write path uses <see cref="AllMastersExcept"/> instead — mapping the write TARGET would lock it
        /// against its own overwrite (the active-patch self-lock); that exclusion is a CORRECTNESS fix, separate from the
        /// Tier-2 perf idea above.</summary>
        public IReadOnlyList<ISkyrimModGetter> AllMasters()
        {
            // Excluded plugins (BuildIndex couldn't fully parse) are INTENTIONALLY retained here — NOT filtered by
            // the snapshot's Excluded set. A clean plugin can override a record whose ORIGIN master is an excluded one, and that master
            // must still appear in the patch's output header for FormID resolution; dropping it would corrupt the
            // header. Safe: Overlay() opens lazily (no parse, no enumeration → no throw), and the serializer parses a
            // master's bodies ONLY when the patch references them — and an excluded plugin's OWN records can't be
            // referenced (they're unreadable). So this is the one read-path that touches excluded plugins on purpose,
            // and it cannot re-throw. (If a future change enumerates or link-caches over this full set, it must
            // re-introduce the Excluded skip — the per-read "single choke point" is BuildIndex, not this method.)
            var arr = new ISkyrimModGetter[_r._paths.Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = Overlay(i);
            return arr;
        }

        /// <summary>Like <see cref="AllMasters"/>, but NEVER opens an overlay on <paramref name="excludeFileName"/> — the
        /// file the caller is about to serialize to. THE ACTIVE-PATCH WRITE FIX (Heisen bug 2026-06-08): when the write
        /// target is itself active in the load order (the normal case once a patch is enabled in MO2), opening a
        /// memory-mapped overlay on it — as <see cref="AllMasters"/> does for EVERY plugin — LOCKS the file against the
        /// very overwrite that follows. Windows refuses to replace a mapped file (IOException "used by another process"),
        /// so the all-or-nothing write writes nothing, and the message misdirects diagnosis at MO2/xEdit.
        ///
        /// <para>The fix is to never OPEN that overlay: a patch is never its own master, so the target is never NEEDED in
        /// the resolve set, and SKIPPING its index leaves no handle to collide with the serialize. Proven (writelock-probe
        /// 2026-06-08): a held overlay locks the target even when it is excluded from the load-order ARGUMENT — it is the
        /// open handle, not the argument membership, that locks, so the index must be skipped (Overlay never called),
        /// NOT merely filtered from the returned list. Master derivation is unaffected: only the target itself is dropped,
        /// and a patch never links to itself, so every master its records DO reference is still opened + ordered. Excluded
        /// (unparseable) plugins are retained for the same reason <see cref="AllMasters"/> retains them (see above).</para>
        ///
        /// <para>This closes the MASTER-SET source of a target overlay. A write path can hold one from ANOTHER source —
        /// <see cref="WritePatchBuilder.Apply"/>'s Phase-1 winner fetch, when re-editing a record the active patch itself
        /// overrides (there the resolved winner IS the target) — which this can't reach; <see cref="ReleaseOverlay"/>
        /// closes that one before the serialize. Both together = no mapped handle on the target survives the write.</para></summary>
        public IReadOnlyList<ISkyrimModGetter> AllMastersExcept(string excludeFileName)
        {
            var list = new List<ISkyrimModGetter>(_r._paths.Length);
            for (int i = 0; i < _r._paths.Length; i++)
                if (!string.Equals(_r._names[i], excludeFileName, StringComparison.OrdinalIgnoreCase))
                    list.Add(Overlay(i));   // SKIP the target's index — never map the file we're about to overwrite
            return list;
        }

        /// <summary>Dispose and forget any overlay this session holds on <paramref name="fileName"/> — the file the caller
        /// is about to serialize to. The SECOND half of the active-patch write fix (with <see cref="AllMastersExcept"/>):
        /// it closes a target overlay opened from a source AllMastersExcept can't reach — notably
        /// <see cref="WritePatchBuilder.Apply"/>'s Phase-1 winner fetch (<see cref="GetRecord"/> → <see cref="Overlay"/>),
        /// which, when you re-edit a record the active patch itself overrides, opens an overlay on the target (the winner
        /// IS the target) that would otherwise still be mapped at serialize and refuse the overwrite (writelock-apply-probe).
        ///
        /// <para>SAFE to call before the write: the only consumer of a fetched winner body is
        /// <see cref="WriteEngine.GenericGetOrAddAsOverride"/>, which DEEP-COPIES it into the patch mod, so releasing the
        /// source overlay cannot strip content from the patch about to be written (proven: the edited override reads back
        /// intact after the release). A no-op when no overlay is open on the file — the common case, and Create/Remove,
        /// which never winner-fetch the target.</para></summary>
        public void ReleaseOverlay(string fileName)
        {
            var hits = _open.Keys.Where(i => string.Equals(_r._names[i], fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var i in hits) { (_open[i] as IDisposable)?.Dispose(); _open.Remove(i); }
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
        _snap = BuildIndex();
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
    /// winner/count index for all keys and the ordered overrider list for multi-override keys only. At most ONE plugin
    /// handle open at any instant (Option B — never the floor). RESILIENT (Q3): a plugin that won't OPEN, or that
    /// contains a record Mutagen can't PARSE (a throw mid-enumeration — Mutagen constructs each record's body as it
    /// enumerates, so a malformed subrecord throws here, NOT lazily on field access), is EXCLUDED wholesale and the
    /// reason recorded — it no longer takes the whole index down with it. Per plugin it's ATOMIC: records go into a
    /// per-plugin buffer and fold into the shared index only if the WHOLE plugin enumerated, so a plugin that throws
    /// part-way never leaves a half-set behind (which would mis-resolve winners for its un-enumerated records).
    /// Returns the build as ONE immutable <see cref="IndexSnapshot"/> — the caller swaps it in with a single
    /// reference write, so a concurrent reader only ever sees a complete, internally-consistent build.</summary>
    IndexSnapshot BuildIndex()
    {
        var index = new Dictionary<FormKey, (int winner, int count)>();
        var overriders = new Dictionary<FormKey, List<int>>();        // multi keys only
        var failures = new List<string>();
        var excluded = new HashSet<int>();
        var excludedPlugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int maxDepth = 0;

        for (int i = 0; i < _paths.Length; i++)
        {
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(_paths[i], SkyrimRelease.SkyrimSE); }
            catch (Exception ex)
            {
                Exclude(i, $"could not be opened — {Concise(ex)}", failures, excluded, excludedPlugins);
                continue;
            }

            // Buffer the WHOLE plugin's keys first (plugin-atomic). EnumerateMajorRecords() constructs each record
            // body as it advances, so a record Mutagen rejects (e.g. a malformed PKCU data-count) throws HERE. The
            // throw is non-resumable, so we can't skip just that record — but catching it lets us EXCLUDE this one
            // plugin and carry on with every other (vs. the old try/finally, where the throw escaped and killed the
            // entire index → every tool call failed). The buffer means a partial enumeration is discarded, not merged.
            var keys = new List<FormKey>();
            try { foreach (var rec in ov.EnumerateMajorRecords()) keys.Add(rec.FormKey); }
            catch (Exception ex)
            {
                Exclude(i, $"contains a record Mutagen cannot parse, so the whole plugin is excluded from this " +
                           $"session (its records are not resolvable; every other plugin is unaffected) — read " +
                           $"{keys.Count} record(s) before the failure: {Concise(ex)}. Fix or remove the upstream " +
                           "plugin to restore access to it.",
                        failures, excluded, excludedPlugins);
                continue;
            }
            finally { (ov as IDisposable)?.Dispose(); }                    // one plugin open at a time — never the whole floor

            foreach (var fk in keys)                                       // fold the COMPLETE plugin into the index
            {
                if (!index.TryGetValue(fk, out var e))
                {
                    index[fk] = (i, 1);                                    // first sighting — singleton so far, no list
                }
                else
                {
                    int newCount = e.count + 1;
                    index[fk] = (i, newCount);                              // higher overlay = new winner
                    if (newCount == 2) overriders[fk] = new List<int> { e.winner, i };  // 2nd sighting promotes to multi
                    else overriders[fk].Add(i);                            // 3rd+ extends the list
                    if (newCount > maxDepth) maxDepth = newCount;
                }
            }
        }

        return new IndexSnapshot(
            index,
            overriders.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),  // trim List overhead → int[]
            failures, excluded, excludedPlugins, maxDepth);
    }

    /// <summary>Record one plugin's exclusion from the index build (Q3): into the human-readable failure list, the
    /// fast-skip index set, and the name→reason map the server surfaces in load_order_status.</summary>
    void Exclude(int i, string reason, List<string> failures, HashSet<int> excluded, Dictionary<string, string> excludedPlugins)
    {
        failures.Add($"{_names[i]}: {reason}");
        excluded.Add(i);
        excludedPlugins[_names[i]] = reason;
    }

    /// <summary>One-line essence of an exception for a user-facing reason: the message lines (the Mutagen
    /// RecordException/SubrecordException family names the offending record + subrecord in its display text) up to the
    /// stack trace, newlines flattened, bounded. Keeps the WHICH-record context without dumping the whole trace.</summary>
    static string Concise(Exception ex)
    {
        var s = ex.ToString();
        // "\n   at " is the en-US stack-frame prefix; on a localized runtime it won't match and the whole ToString()
        // (stack included) gets flattened + capped instead — noisier, never WRONG, and the RecordException "which
        // record" context is front-loaded in ToString() so it survives the 300-char cap either way. (Windows/en-US
        // product → acceptable; reading the exception's record-identity properties would be the locale-proof upgrade.)
        int at = s.IndexOf("\n   at ", StringComparison.Ordinal);
        var head = (at >= 0 ? s.Substring(0, at) : s).Replace("\r", "").Replace("\n", " | ").Trim();
        return head.Length > 300 ? head.Substring(0, 300) + "…" : head;
    }

    // ---- Snapshot-scoped reads (HCBR-2026-06-11-02: one build per logical operation) --------------------

    /// <summary>Capture the CURRENT build as a pinned read view. The snapshot swap (HCBR-2026-06-11-01 hardening)
    /// made each individual read internally consistent; this is the cross-VALUE companion: a service method that
    /// issues SEVERAL resolver reads in one logical operation could still observe TWO adjacent builds if a
    /// freshness rebuild landed between them — a status line mixing counters from different builds, a record's
    /// winner disagreeing with its own touching-plugin list, a scan row's winner= reflecting a newer build than
    /// the scan that produced it. Capture ONCE per logical operation (one service method / one tool call) and
    /// answer every question in that operation off the SAME view; a rebuild mid-operation then changes nothing
    /// the operation reports. Pure data over the immutable snapshot — no handles, safe to hold for a call.</summary>
    public IndexView Capture() => new(this, _snap);

    /// <summary>A read view pinned to ONE captured index build (see <see cref="Capture"/>). Every member answers
    /// from the SAME build — winner, touching list, counters, and the scan streams can never disagree about which
    /// build they describe. Bodies are still fetched from the files on disk (Option B holds no bodies), so a
    /// mid-operation file edit surfaces as the existing named staleness errors, never as torn index values.</summary>
    public readonly struct IndexView
    {
        readonly LoadOrderResolver _r;
        readonly IndexSnapshot _s;
        internal IndexView(LoadOrderResolver r, IndexSnapshot s) { _r = r; _s = s; }   // only Capture() constructs

        public int PluginCount => _r._paths.Length;
        public int RecordCount => _s.Index.Count;               // distinct FormKeys across the order
        public int ConflictCount => _s.Overriders.Count;        // FormKeys overridden by >1 plugin
        public int MaxDepth => _s.MaxDepth;
        public IReadOnlyList<string> LoadFailures => _s.LoadFailures;
        public IReadOnlyDictionary<string, string> ExcludedPlugins => _s.ExcludedPlugins;

        /// <summary>O(1): the winning plugin + override depth for a FormKey. null if the FormKey isn't in the order.</summary>
        public WinnerInfo? ResolveWinner(FormKey fk)
            => _s.Index.TryGetValue(fk, out var e) ? new WinnerInfo(fk, _r._names[e.winner], e.count) : null;

        /// <summary>Every FormKey overridden by more than one plugin (the whole-order conflict set).</summary>
        public IEnumerable<FormKey> ConflictKeys() => _s.Overriders.Keys;

        /// <summary>The ordered touching-plugin names for a FormKey (priority order, winner last) — no body fetched.
        /// The atom behind every conflict-status question.</summary>
        public IReadOnlyList<string>? TouchingPlugins(FormKey fk)
        {
            if (!_s.Index.TryGetValue(fk, out var e)) return null;
            if (e.count == 1) return new[] { _r._names[e.winner] };    // singleton: sole overrider = winner
            var names = _r._names;                                     // local copy — a struct's lambda can't capture 'this'
            return Array.ConvertAll(_s.Overriders[fk], i => names[i]);
        }

        /// <summary>The winner-body scan stream (<see cref="LoadOrderResolver.WinnerRecordsOfType(IReadOnlyList{Type})"/>),
        /// pinned to THIS view's build — so a caller's per-match winner/depth fills agree with the scan by construction.</summary>
        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(IReadOnlyList<Type> getterTypes)
            => _r.WinnerRecordsOfType(getterTypes, _s);

        /// <summary>The plugin-scoped scan stream (<see cref="LoadOrderResolver.RecordsIn(IReadOnlyList{string}, IReadOnlyList{Type})"/>),
        /// pinned to THIS view's build.</summary>
        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
            IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
            => _r.RecordsIn(plugins, getterTypes, _s);
    }

    // ---- Queries -------------------------------------------------------
    //  Single-shot conveniences: each delegates to a fresh Capture(), so one call = one build (the pre-existing
    //  contract). A caller making SEVERAL reads in one logical operation should Capture() once and read off the
    //  view instead — that's the HCBR-2026-06-11-02 discipline the service layer follows.

    /// <summary>O(1): the winning plugin + override depth for a FormKey. null if the FormKey isn't in the order.</summary>
    public WinnerInfo? ResolveWinner(FormKey fk) => Capture().ResolveWinner(fk);

    /// <summary>Every FormKey overridden by more than one plugin (the whole-order conflict set).</summary>
    public IEnumerable<FormKey> ConflictKeys() => Capture().ConflictKeys();

    /// <summary>The ordered touching-plugin names for a FormKey (priority order, winner last) — no body fetched.
    /// The atom behind every conflict-status question.</summary>
    public IReadOnlyList<string>? TouchingPlugins(FormKey fk) => Capture().TouchingPlugins(fk);

    /// <summary>The full conflict tree: every touching plugin's body, in priority order (winner last). Bodies are
    /// fetched on demand into <paramref name="session"/> (which keeps the touched plugins open until the caller has
    /// materialised them, then disposes them). null if the FormKey isn't in the order.</summary>
    public ConflictTree? ResolveTree(OverlaySession session, FormKey fk)
    {
        var s = _snap;                                                 // ONE build — Index and Overriders can't disagree
        if (!s.Index.TryGetValue(fk, out var e)) return null;
        var overlayIdxs = e.count == 1 ? new[] { e.winner } : s.Overriders[fk];
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
        var s = _snap;                                                 // ONE build, captured for the whole enumeration
        if (!_nameToIdx.TryGetValue(pluginName, out int idx))
            throw new ArgumentException($"plugin not in the load order: {pluginName}");
        if (s.Excluded.Contains(idx))
            throw new ArgumentException($"plugin '{pluginName}' was excluded from this session: {s.ExcludedPlugins[pluginName]}");
        var ov = SkyrimMod.CreateFromBinaryOverlay(_paths[idx], SkyrimRelease.SkyrimSE);
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                var fk = rec.FormKey;
                if (!s.Index.TryGetValue(fk, out var e))               // the FILE outran the snapshot (edited after the build) — name it (Q3)
                    throw new InvalidOperationException(
                        $"index staleness: '{pluginName}' yields {fk} which the current index build does not contain — the plugin changed since the index was built; re-run (the next call's freshness check rebuilds).");
                var touching = e.count == 1 ? new[] { _names[e.winner] } : Array.ConvertAll(s.Overriders[fk], i => _names[i]);
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
        if (_snap.Excluded.Contains(idx)) return null;                     // excluded plugin — never re-enumerate it (would re-throw); the service reports the reason via ExcludedPlugins
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
        => WinnerRecordsOfType(getterTypes, _snap);                        // ONE build for the whole scan (captured here, at the call)

    IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(IReadOnlyList<Type> getterTypes, IndexSnapshot s)
    {
        for (int i = 0; i < _paths.Length; i++)
        {
            if (s.Excluded.Contains(i)) continue;                          // excluded at build (unparseable/unopenable) — wins nothing; never re-touch (would re-throw)
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(_paths[i], SkyrimRelease.SkyrimSE); }
            catch { continue; }                                            // an unopenable plugin wins nothing (surfaced at build)
            try
            {
                foreach (var t in getterTypes)
                    foreach (var rec in ov.EnumerateMajorRecords(t, throwIfUnknown: true))
                        if (s.Index.TryGetValue(rec.FormKey, out var e) && e.winner == i)   // this overlay's instance wins
                            yield return (rec.FormKey, e.count, rec);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Stream every record contained in the given plugins (optionally only of the given type(s)), each
    /// with that PLUGIN'S body in hand — the plugin-scoped path (the Q4.9 plugin_dump fold + a plugin-content
    /// audit). A FormKey touched by more than one scoped plugin is yielded once per scoped plugin (the SERVICE
    /// de-dupes). Yields (FormKey, whole-order override-depth, the scoped plugin's body, the scoped plugin's
    /// filename — so a caller can DISPLAY from the same body it filtered, not the winner). Holds nothing.</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
        => RecordsIn(plugins, getterTypes, _snap);                         // ONE build for the whole scan (captured here, at the call)

    IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes, IndexSnapshot s)
    {
        foreach (int i in ScopeIndices(plugins, s))
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
                    if (s.Index.TryGetValue(rec.FormKey, out var e))
                        yield return (rec.FormKey, e.count, rec, _names[i]);   // _names[i] = this scoped plugin's filename (the source body)
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Resolve a scope (plugin filenames) to overlay indices; null/empty = the whole order. Throws
    /// (Q3) on a name not in the order, naming it — never silently scans an empty/partial scope. Works off the
    /// caller's captured snapshot so scope and scan judge exclusion against the SAME build.</summary>
    IReadOnlyList<int> ScopeIndices(IReadOnlyList<string>? scopePlugins, IndexSnapshot s)
    {
        if (scopePlugins is null || scopePlugins.Count == 0)
            return Enumerable.Range(0, _paths.Length).Where(i => !s.Excluded.Contains(i)).ToArray();  // whole order, minus excluded
        var idxs = new List<int>(scopePlugins.Count);
        foreach (var name in scopePlugins)
        {
            if (!_nameToIdx.TryGetValue(name, out int i))
                throw new ArgumentException($"plugin not in the load order: {name}");
            if (s.Excluded.Contains(i))                                    // explicitly scoped to an excluded plugin → fail loud with the reason (Q3), don't silently scan nothing
                throw new ArgumentException($"plugin '{name}' was excluded from this session: {s.ExcludedPlugins[name]}");
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
        _snap = BuildIndex();                                              // ONE reference write — in-flight readers keep their captured build
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

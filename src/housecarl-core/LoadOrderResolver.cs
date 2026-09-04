using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

// ======================================================================
//  LoadOrderResolver — a small held structural index plus on-demand targeted parse. It holds no record bodies and
//  NO PLUGIN FILE HANDLES AT REST.
//
//  SHAPE:
//    • Held index — pure data, zero file handles — built by enumerating every plugin ONE AT A TIME (low→high
//      priority: open i → enumerate → DISPOSE → i+1, never all-open-at-once):
//        - Index     : FormKey → (winnerOverlay, overrideCount)  — ALL keys, the O(1) "what wins" fast path.
//        - Overriders: FormKey → ordered overlay indices          — MULTI-override keys ONLY; a singleton's sole
//                      overrider IS its winner, so it needs no list.
//      Roughly 125–185 MB at full-modlist scale. No record bodies, no mmap handles held.
//    • On-demand body fetch = open the plugin, re-enumerate and match the FormKey, then DISPOSE when the work ends.
//      A per-call OverlaySession (see OpenSession) opens each plugin a tool call touches AT MOST ONCE and disposes
//      every one when the call returns. The write path takes its known-master set and its nested-override link cache
//      from the SAME session, so a write also holds handles only for its own duration.
//    • Freshness = re-stat the plugin files on demand and rebuild the index if any mtime changed. No live MO2
//      tracking; no held overlays to dispose and reopen.
//
//  WHY zero handles at rest: a Windows mmap overlay opened without FILE_SHARE_DELETE LOCKS its file against delete,
//  rename and overwrite — exactly what MO2, xEdit and Explorer need to do. Holding an overlay on every plugin for the
//  process lifetime means thousands of such locks. Zero handles at rest makes the lock absent rather than merely
//  permissive, and makes every read always-live with no stale-view seam.
//
//  ORDER IS INJECTED. Build takes the plugin paths already in priority order. Override counts/depths and tree
//  membership are order-independent; winner IDENTITY is only as correct as the injected order.
//
//  A plugin the index build cannot fully read — it won't OPEN, or it contains a record Mutagen cannot PARSE (a
//  strict-validation throw mid-enumeration; the common real case is a malformed subrecord an upstream ESP ships and
//  the game engine ignores) — is EXCLUDED wholesale and the reason is collected and surfaced (LoadFailures /
//  ExcludedPlugins → load_order_status), never skipped silently. The throw is non-resumable — Mutagen's group
//  enumerator cannot advance past the bad record — so exclusion is per-PLUGIN and atomic: a partially-read plugin
//  never half-populates the index. A body the index says exists but the plugin cannot yield still throws, as a real
//  inconsistency, named.
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

/// <summary>A plugin in the load order could not be opened during a scan, although it opened when the index was
/// built — it was moved, or another program (xEdit, MO2, the running game) is holding it. Thrown from the
/// plugin-scoped record stream so a scan reports the plugin it could not read rather than skipping it silently.</summary>
public sealed class PluginUnreadableException : Exception
{
    public string PluginName { get; }
    public PluginUnreadableException(string pluginName, Exception inner)
        : base($"could not read '{pluginName}': it opened when the load order was indexed but not now — another " +
               "program is probably holding it open. Close that program and run this again. " +
               $"({inner.GetType().Name}: {inner.Message})", inner)
        => PluginName = pluginName;
}

/// <summary>A BASELINE master (Skyrim.esm / Update.esm) is active but cannot be opened. Every written plugin
/// force-includes the baselines, and that force-include is derived from the known-master list, so quietly omitting one
/// would emit a plugin missing a mandatory master. Thrown from the master-set builders — the single point every write
/// lane funnels through — so no lane can forget the check.</summary>
public sealed class UnopenableBaselineMasterException : Exception
{
    public string PluginName { get; }
    public UnopenableBaselineMasterException(string pluginName)
        // Worded to hold on BOTH write lanes: "every written plugin must list it" is the PATCH lane's reason —
        // WriteInPlace force-includes no baselines — while the fact that covers both is simply that no write can
        // resolve against a master it cannot open.
        : base($"'{pluginName}' is a BASELINE master (Skyrim.esm / Update.esm) and is ACTIVE in your load order, but " +
               "houseCARL cannot open it — see load_order_status for the reason. Nothing can be written while that is " +
               "true, for either reason alone: a new patch must list the baselines in its header (emitting one without " +
               "them produces a plugin the game treats as malformed), and no write can resolve references against a " +
               "master it cannot read. Repair or replace that plugin in MO2 and retry. Nothing was written.")
        => PluginName = pluginName;
}

/// <remarks>LAYERING NOTE: this file's master-set builders reference <see cref="WriteEngine.BaselineMasters"/> and
/// throw <see cref="UnopenableBaselineMasterException"/> — a WRITE policy in a read-side class. Deliberate: the
/// builders are the single point every write lane funnels through, so enforcing it there is what makes "no lane can
/// forget the check" true, where a per-lane check is something the next lane forgets.</remarks>
public sealed class LoadOrderResolver : IDisposable
{
    readonly string[] _paths;                          // every active plugin's path, priority order (masters → … → winner)
    readonly string[] _names;                          // index → plugin filename (e.g. "Skyrim.esm"); == Path.GetFileName(path)
    readonly Dictionary<string, int> _nameToIdx;       // plugin filename → index (last copy of a duplicate name wins = priority)
    DateTime[] _mtimes;                                // last-write at the last index build, per path (freshness baseline)
    readonly string? _dataDir;                         // real game-Data folder (Skyrim.esm's dir) — localized-strings fallback source (OpenOverlay)

    /// <summary>The real game-Data folder this order resolved, for callers OUTSIDE the session that must open a plugin
    /// through <see cref="OpenOverlay"/> and get the same strings resolution the index reads with. The in-place write's
    /// post-serialize verify needs it: it COMPARES what it reads, and a strings-less read of a localized field would
    /// otherwise look like a lost write.</summary>
    internal string? DataDir => _dataDir;

    /// <summary>Optional: given a plugin filename this index does NOT contain, a clause saying WHY it isn't in the
    /// active order — or null if the injector can't say. The commonest cause by far is a plugin that IS installed and
    /// IS in an enabled mod but sits unticked in MO2's right pane, and a flat not-found sends the reader searching for
    /// a file that is right there. The resolver cannot answer that itself and must not learn how: it is built from a
    /// bare ordered path list and knows nothing of MO2. So the answer is injected by whoever does know (the MCP
    /// service, from the MO2 profile), and null here simply restores the plain not-found wording. Direct
    /// <c>Build(paths)</c> callers get null; both service modes supply an explainer, and it is the explainer's own
    /// missing-profile check, not the wiring, that handles a profile it cannot read.</summary>
    readonly Func<string, string?>? _explainAbsence;

    /// <summary>One index build's ENTIRE output, swapped in as a SINGLE reference write. The service refreshes the
    /// index under its own gate, but READERS run outside that gate — concurrent tool calls hold the resolver while a
    /// sibling call's freshness check may rebuild. With the per-build state in separate fields a reader could observe
    /// a NEW index beside OLD overriders mid-swap, e.g. a KeyNotFound on a freshly-multi key. Keeping the build in one
    /// immutable snapshot, captured ONCE per operation (single-shot members and the scan wrappers capture at CALL
    /// time; <see cref="Capture"/> pins a whole logical operation), makes a torn view impossible. The volatile field
    /// gives the swap release/acquire visibility.</summary>
    internal sealed class IndexSnapshot   // internal (not private) so IndexView's ctor can take it; never leaves the assembly
    {
        public readonly Dictionary<FormKey, (int winner, int count)> Index;   // ALL keys — winner + depth, O(1)
        public readonly Dictionary<FormKey, int[]> Overriders;                // MULTI keys only — ordered touching overlay indices
        public readonly List<string> LoadFailures;                            // per-plugin index-build failures (open OR parse), always surfaced
        public readonly HashSet<int> Excluded;                                // overlay indices excluded this build — never re-touched by any path
        /// <summary>The SUBSET of <see cref="Excluded"/> whose file could not be OPENED at all, as opposed to opening
        /// fine and failing on a record body. The distinction is invisible to reads (both mean "never touch this
        /// plugin") and load-bearing for WRITES: the master set retains excluded plugins on purpose, which is only
        /// possible for the ones that can be opened. Kept as its own set rather than derived from the reason STRING —
        /// a message is display prose that can be reworded, membership is a fact.</summary>
        public readonly HashSet<int> Unopenable;
        public readonly Dictionary<string, string> ExcludedPlugins;           // excluded plugin name → reason
        public readonly int MaxDepth;
        public readonly string Epoch;                                         // this build's fingerprint — immutable with the snapshot

        /// <summary>Per plugin index: is it a LIGHT plugin? Read off the header while the build already had the
        /// overlay open (the .esl extension counts too — the engine force-treats it as light). This is what decides
        /// whether the game addresses the plugin through the shared 0xFE light block or through its own load
        /// index, so it is the only extra fact the runtime-FormID tables need.</summary>
        public readonly bool[] Light;

        /// <summary>Active plugins whose light flag could NOT be read, because the file could not be opened at all.
        /// Their kind decides every runtime index AFTER them, so a runtime FormID cannot be answered while one is in
        /// the order — named here so the refusal can say which plugin, rather than guessing from the extension.</summary>
        public readonly IReadOnlyList<string> LightFlagUnknown;

        /// <summary>The runtime address tables, built on first use (a load order that never sees a runtime FormID
        /// never pays for them) and thrown away with this snapshot, so they refresh with the index.</summary>
        public readonly Lazy<RuntimeSlots> Slots;

        public IndexSnapshot(Dictionary<FormKey, (int winner, int count)> index, Dictionary<FormKey, int[]> overriders,
                             List<string> loadFailures, HashSet<int> excluded, HashSet<int> unopenable,
                             Dictionary<string, string> excludedPlugins, int maxDepth, string epoch,
                             bool[] light, IReadOnlyList<string> lightFlagUnknown)
        {
            Index = index; Overriders = overriders; LoadFailures = loadFailures; Excluded = excluded; Unopenable = unopenable;
            ExcludedPlugins = excludedPlugins; MaxDepth = maxDepth; Epoch = epoch; Light = light; LightFlagUnknown = lightFlagUnknown;
            Slots = new Lazy<RuntimeSlots>(() => RuntimeSlots.Build(light));
        }
    }

    /// <summary>The runtime address tables for ONE index build: which plugin the game loads at each load index and at
    /// each light index. The engine numbers the two kinds separately — full plugins take 0x00, 0x01, … in order,
    /// light plugins all share the 0xFE index and take 0x000, 0x001, … in order among themselves — so a runtime
    /// FormID's high byte says which table to read.</summary>
    internal sealed class RuntimeSlots
    {
        /// <summary>Load index → plugin index; -1 where no active plugin occupies the slot.</summary>
        public readonly int[] FullToPlugin;
        /// <summary>Light index → plugin index; -1 where no active plugin occupies the slot.</summary>
        public readonly int[] LightToPlugin;
        /// <summary>Plugin index → its own runtime slot; -1 for a plugin past the end of its block.</summary>
        public readonly int[] SlotOfPlugin;
        public readonly int FullCount;
        public readonly int LightCount;

        RuntimeSlots(int[] fullToPlugin, int[] lightToPlugin, int[] slotOfPlugin, int fullCount, int lightCount)
        { FullToPlugin = fullToPlugin; LightToPlugin = lightToPlugin; SlotOfPlugin = slotOfPlugin; FullCount = fullCount; LightCount = lightCount; }

        /// <summary>0xFE is the light block and 0xFF the runtime-dynamic block, so a full plugin can only load at
        /// 0x00–0xFD.</summary>
        public const int MaxFullSlots = 0xFE;
        /// <summary>The light index is 12 bits.</summary>
        public const int MaxLightSlots = 0x1000;

        public static RuntimeSlots Build(bool[] light)
        {
            var full = new int[MaxFullSlots];
            var lite = new int[MaxLightSlots];
            Array.Fill(full, -1);
            Array.Fill(lite, -1);
            var slotOf = new int[light.Length];
            int nFull = 0, nLight = 0;
            for (int i = 0; i < light.Length; i++)
            {
                if (light[i]) { slotOf[i] = nLight < MaxLightSlots ? nLight : -1; if (nLight < MaxLightSlots) lite[nLight] = i; nLight++; }
                else { slotOf[i] = nFull < MaxFullSlots ? nFull : -1; if (nFull < MaxFullSlots) full[nFull] = i; nFull++; }
            }
            return new RuntimeSlots(full, lite, slotOf, nFull, nLight);
        }
    }

    volatile IndexSnapshot _snap;

    /// <summary>Per-plugin index-build failures (couldn't open, or contains a record Mutagen can't parse) — each
    /// excluded plugin named with its reason, surfaced rather than silently skipped. Same content as
    /// <see cref="ExcludedPlugins"/>, formatted "name: reason" for display.</summary>
    public IReadOnlyList<string> LoadFailures => _snap.LoadFailures;

    /// <summary>Is this plugin ACTIVE but impossible to OPEN? The resolver-level twin of
    /// <see cref="IndexView.IsUnopenable"/>, for callers holding the resolver rather than a captured view (the dry-run
    /// master preview). Reads the current snapshot, which is what a dry run should predict against.</summary>
    public bool IsUnopenable(string pluginName)
        => _nameToIdx.TryGetValue(pluginName, out int i) && _snap.Unopenable.Contains(i);

    /// <summary>Plugins EXCLUDED from this build (name → why): unopenable, or carrying a record Mutagen can't parse.
    /// Their records are not in the index and no path will re-touch them; load_order_status reports them so the user
    /// can fix or remove the upstream plugin — the exclusion is visible, never silent.</summary>
    public IReadOnlyDictionary<string, string> ExcludedPlugins => _snap.ExcludedPlugins;

    public int PluginCount => _paths.Length;
    public int RecordCount => _snap.Index.Count;            // distinct FormKeys across the order
    public int ConflictCount => _snap.Overriders.Count;     // FormKeys overridden by >1 plugin
    public int MaxDepth => _snap.MaxDepth;

    /// <summary>The CURRENT build's epoch fingerprint. Single-shot convenience — a multi-read operation should
    /// Capture() and use the view's <see cref="IndexView.Epoch"/> so the stamp names the build it actually read.</summary>
    public string Epoch => _snap.Epoch;

    /// <summary>Every plugin's filename, in priority order (pure data — no handles). The known-name list the write
    /// harnesses scan to decide which masters are in the order.</summary>
    public IReadOnlyList<string> PluginNames => _names;

    // ---- Per-call overlay session: open on demand, dispose at call end, zero handles at rest ----

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
                _open[idx] = ov = OpenOverlay(_r._paths[idx], _r._dataDir);
            return ov;
        }

        /// <summary>Open EVERY plugin (priority order) and return them as the FULL known-master set the multi-master
        /// write path hands the serializer (<see cref="WriteEngine.WritePatch(SkyrimMod,System.Collections.Generic.IReadOnlyList{ISkyrimModGetter},string)"/>):
        /// with every master resolvable and ordered, a cross-master patch serializes with a lean only-referenced header.
        /// Opened for THIS write only and disposed with the session. Opening only the patch-referenced masters would
        /// keep a write nearer handle-free; that is an open optimization, not done here. To write INTO a patch that is
        /// itself ACTIVE in the order, the write path uses <see cref="AllMastersExcept"/> instead — mapping the write
        /// TARGET would lock it against its own overwrite.</summary>
        public IReadOnlyList<ISkyrimModGetter> AllMasters()
        {
            // Excluded plugins that OPEN are INTENTIONALLY retained here — NOT filtered by the snapshot's Excluded
            // set. A clean plugin can override a record whose ORIGIN master is an excluded one, and that master must
            // still appear in the patch's output header for FormID resolution; dropping it would corrupt the header.
            // Safe for THAT class: Overlay() opens lazily (no parse, no enumeration → no throw), and the serializer
            // parses a master's bodies ONLY when the patch references them.
            //
            // The other exclusion class cannot be retained: a plugin excluded BECAUSE OpenOverlay threw makes
            // Overlay(i) throw again here, on every write, including writes that never touch it. Skipping loses
            // nothing the retention argument protects — an overlay we cannot open contributes no header entry either
            // way — and the names are recorded so a write that genuinely needed one can say so instead of dying as a
            // raw serializer fault.
            var arr = new List<ISkyrimModGetter>(_r._paths.Length);
            for (int i = 0; i < _r._paths.Length; i++)
            {
                if (SkipUnopenable(i)) continue;
                arr.Add(Overlay(i));
            }
            return arr;
        }

        /// <summary>Like <see cref="AllMasters"/>, but NEVER opens an overlay on <paramref name="excludeFileName"/> — the
        /// file the caller is about to serialize to. THE ACTIVE-PATCH WRITE FIX: when the write
        /// target is itself active in the load order (the normal case once a patch is enabled in MO2), opening a
        /// memory-mapped overlay on it — as <see cref="AllMasters"/> does for EVERY plugin — LOCKS the file against the
        /// very overwrite that follows. Windows refuses to replace a mapped file (IOException "used by another process"),
        /// so the all-or-nothing write writes nothing, and the message misdirects diagnosis at MO2/xEdit.
        ///
        /// <para>The fix is to never OPEN that overlay: a patch is never its own master, so the target is never NEEDED in
        /// the resolve set, and SKIPPING its index leaves no handle to collide with the serialize. A held overlay locks
        /// the target even when it is excluded from the load-order ARGUMENT — it is the open handle, not the argument
        /// membership, that locks, so the index must be skipped (Overlay never called), NOT merely filtered from the
        /// returned list. Master derivation is unaffected: only the target itself is dropped,
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
            {
                if (string.Equals(_r._names[i], excludeFileName, StringComparison.OrdinalIgnoreCase)) continue;  // never map the file we're about to overwrite
                if (SkipUnopenable(i)) continue;                                                                 // and never try to map one that cannot be opened
                list.Add(Overlay(i));
            }
            return list;
        }

        /// <summary>Is this index the could-not-be-OPENED exclusion class, which no master set can contain? Records the
        /// name, because the skip is not free:
        /// <list type="bullet">
        /// <item>a patch whose header needs only ONE master still writes, even when that master is the skipped plugin —
        /// Mutagen derives the entry from the record's own FormKey, not from list membership. Reachable only in an
        /// order WITHOUT the baselines (a test harness): a real order force-includes Skyrim.esm and Update.esm, so a
        /// patch-lane header always carries two or more;</item>
        /// <item>a patch whose header must be SORTED (two or more masters) does NOT: the serializer refuses with
        /// <c>MissingModException</c> naming the skipped plugin. In practice this is the case that fires.</item>
        /// </list>
        /// That second case is real and reachable, and must be named rather than surfaced as a generic "serialize or
        /// commit" fault — which is what <see cref="SkippedUnopenable"/> is for.</summary>
        bool SkipUnopenable(int i)
        {
            if (!_r._snap.Unopenable.Contains(i)) return false;
            var name = _r._names[i];
            // A BASELINE master (Skyrim.esm / Update.esm) must never be skipped. WriteEngine.WritePatch derives its
            // CK-mandated force-include FROM the list returned here, filtered to baselines present in it — a filter
            // there to tolerate a degenerate order or a single-master harness. Skipping an unopenable baseline makes a
            // REAL order look degenerate to it, and a plugin lands on disk missing a mandatory master with no warning:
            // a silent degradation in place of a loud failure. A baseline is never legitimately absent, so this
            // refuses instead — thrown from the single chokepoint every write lane funnels through, rather than
            // re-checked per lane.
            if (Array.Exists(WriteEngine.BaselineMasters, bm => string.Equals(bm.FileName.String, name, StringComparison.OrdinalIgnoreCase)))
                throw new UnopenableBaselineMasterException(name);
            _skippedUnopenable.Add(name);
            return true;
        }

        readonly SortedSet<string> _skippedUnopenable = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The plugins this session's master-set builds skipped as unopenable. Empty in the normal case.
        /// Non-empty ⇒ a serialize failure naming a missing master is very likely one of THESE, and the write lane says
        /// so instead of reporting an opaque engine fault.</summary>
        /// <remarks>Exposed as a SET, not a collection: consumers ask "is this name in it?", and the backing
        /// comparer is OrdinalIgnoreCase — a LINQ Contains over a collection would have compared ordinally and missed a
        /// case difference between the profile's spelling and a ModKey's.</remarks>
        public IReadOnlySet<string> SkippedUnopenable => _skippedUnopenable;

        /// <summary>Dispose and forget any overlay this session holds on <paramref name="fileName"/> — the file the caller
        /// is about to serialize to. The SECOND half of the active-patch write fix (with <see cref="AllMastersExcept"/>):
        /// it closes a target overlay opened from a source AllMastersExcept can't reach — notably
        /// <see cref="WritePatchBuilder.Apply"/>'s Phase-1 winner fetch (<see cref="GetRecord"/> → <see cref="Overlay"/>),
        /// which, when you re-edit a record the active patch itself overrides, opens an overlay on the target (the winner
        /// IS the target) that would otherwise still be mapped at serialize and refuse the overwrite.
        ///
        /// <para>SAFE to call before the write: the only consumer of a fetched winner body is
        /// <see cref="WriteEngine.GenericGetOrAddAsOverride"/>, which DEEP-COPIES it into the patch mod, so releasing the
        /// source overlay cannot strip content from the patch about to be written. A no-op when no overlay is open on
        /// the file — the common case, and Create/Remove, which never winner-fetch the target.</para></summary>
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

    LoadOrderResolver(string[] paths, string[] names, Dictionary<string, int> nameToIdx, DateTime[] mtimes,
                      Func<string, string?>? explainAbsence)
    {
        _paths = paths; _names = names; _nameToIdx = nameToIdx; _mtimes = mtimes;
        _explainAbsence = explainAbsence;
        _dataDir = ComputeDataDir(nameToIdx, paths);
        _snap = BuildIndex();
    }

    /// <summary>The trailing clause for a refusal naming a plugin this order does not contain. Prefers the injected
    /// EXPLANATION (<see cref="_explainAbsence"/>) — a real cause with a real remedy — and falls back to the
    /// did-you-mean suggester when nothing can be explained, which is the right split: a name that resolves to a real
    /// installed plugin should never be answered with a spelling guess, and a genuine typo has no cause to state.
    /// One home, so the refusal sites below cannot drift apart on the wording.</summary>
    internal string AbsenceClause(string pluginName)
        => ExplainAbsence(pluginName) is { } why ? " " + why : NameSuggestion(pluginName);

    /// <summary>ONLY the injected explanation, or null when there is none — the half a caller needs when the presence
    /// of a real CAUSE changes more than one sentence (read_record drops its generic posture tail once a specific cause
    /// has been stated, and the did-you-mean is not a cause). Kept separate from <see cref="AbsenceClause"/> because
    /// that one deliberately returns a non-empty string in BOTH cases, so its length says nothing about which
    /// happened.</summary>
    internal string? ExplainAbsence(string pluginName)
    {
        try { return _explainAbsence?.Invoke(pluginName); }
        catch { return null; }   /* an explainer that throws (an unreadable profile mid-call) must never turn a clean
                                    refusal into a crash — fall through to the suggester. */
    }

    /// <summary>The did-you-mean for a name nothing can explain — the fallback half of <see cref="AbsenceClause"/>,
    /// exposed so a caller that already asked for the explanation need not re-invoke the explainer to get it.</summary>
    internal string NameSuggestion(string pluginName) => PluginNameSuggest.DidYouMean(pluginName, _names);

    /// <summary>The real game-Data directory — the folder holding the vanilla BSAs (<c>Skyrim - Interface.bsa</c> et al.,
    /// which carry the base AND DLC <c>.STRINGS</c>). Derived as the folder of the resolved <c>Skyrim.esm</c>: the base
    /// master is never cleaned/relocated, so it resolves to the true game-Data root in both MO2 and explicit-paths modes.
    /// The localized-strings fallback target in <see cref="OpenOverlay"/>; null (→ unchanged folder-adjacent opens
    /// everywhere) only if the order somehow lacks Skyrim.esm.</summary>
    internal static string? ComputeDataDir(Dictionary<string, int> nameToIdx, string[] paths)
        => nameToIdx.TryGetValue("Skyrim.esm", out var i) ? Path.GetDirectoryName(paths[i]) : null;

    /// <summary>Open one plugin as a lazy binary overlay — THE single overlay-open choke point (every read/scan/index
    /// path routes through here) — wiring localized-string (FULL/DESC/…) resolution so a plugin resolved to a folder
    /// WITHOUT its own strings still reads its names. Mutagen's bare overload only scans the plugin's OWN folder for
    /// strings (loose <c>Strings\</c> + BSAs there); a localized master that MO2 resolves to a strings-less mod folder
    /// — the near-universal "Cleaned Base Game Masters" pattern, whose <c>.STRINGS</c> live in the game-Data BSAs beside
    /// Skyrim.esm — otherwise reads every localized field EMPTY: <see cref="ReadEngine.EmitToken"/> turns the
    /// unresolved <c>TranslatedString</c> into a blank token, so a name filter silently matches nothing. When the
    /// plugin's own folder carries NO strings source, point the lookup at the real game-Data
    /// folder so those archived strings resolve; otherwise leave the folder-adjacent default UNTOUCHED, so a mod whose
    /// strings sit in its own folder (loose OR in its own BSA) is never redirected away from them — no regression. A
    /// non-localized plugin needs no strings at all, so the override is simply never consulted.
    ///
    /// <para>Public because it is also the open path for <c>housecarl_read_plugin_file</c>'s raw, out-of-load-order
    /// read of an inactive/arbitrary plugin — a pure <c>(path, dataDir) → overlay</c> factory that touches no resolver
    /// index, so that tool reuses this one strings-correct choke point instead of re-deriving it.</para></summary>
    public static ISkyrimModGetter OpenOverlay(string path, string? dataDir)
    {
        if (dataDir is not null && !FolderHasOwnStrings(path))
        {
            var prm = BinaryReadParameters.Default with
            {
                StringsParam = new StringsReadParameters
                {
                    BsaFolderOverride = dataDir,
                    StringsFolderOverride = Path.Combine(dataDir, "Strings"),
                },
            };
            return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, prm);
        }
        return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
    }

    /// <summary>True if the plugin's OWN folder carries a strings source Mutagen's folder-adjacent default would find —
    /// a loose <c>Strings\</c> subfolder or any <c>.bsa</c> (which may embed strings). Cheap (one dir stat + a lazy,
    /// short-circuited <c>.bsa</c> scan; negligible beside the per-plugin overlay open it precedes). Defensive: any IO
    /// fault answers "has its own", keeping the unchanged default open — we only ever REDIRECT on a clean, empty read.</summary>
    internal static bool FolderHasOwnStrings(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            if (folder is null) return true;
            if (Directory.Exists(Path.Combine(folder, "Strings"))) return true;
            return Directory.EnumerateFiles(folder, "*.bsa").Any();
        }
        catch { return true; }
    }

    /// <summary>Take the plugin paths already in priority order and build the index, without holding any plugin open.
    /// Names and mtimes come from the path list plus a stat (no parse, no handle); the index build
    /// (<see cref="BuildIndex"/>) opens each plugin one at a time to enumerate it, then disposes it. <paramref
    /// name="orderedPluginPaths"/> = masters → … → highest priority; the order is INJECTED, never derived here.
    /// Per-plugin open failures are collected into <see cref="LoadFailures"/> at index time, never silently
    /// skipped.</summary>
    /// <param name="explainAbsence">Optional injected answer to "why is this name not in the order?" — see
    /// <see cref="_explainAbsence"/>. Omit (the default) and refusals read exactly as they did before.</param>
    public static LoadOrderResolver Build(IReadOnlyList<string> orderedPluginPaths,
                                          Func<string, string?>? explainAbsence = null)
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

        return new LoadOrderResolver(paths, names, nameToIdx, mtimes, explainAbsence);
    }

    /// <summary>Enumerate every plugin once (low→high), ONE AT A TIME (open → enumerate → dispose), building the
    /// winner/count index for all keys and the ordered overrider list for multi-override keys only. At most ONE plugin
    /// handle open at any instant, never the whole order. A plugin that won't OPEN, or that
    /// contains a record Mutagen can't PARSE (a throw mid-enumeration — Mutagen constructs each record's body as it
    /// enumerates, so a malformed subrecord throws here, NOT lazily on field access), is EXCLUDED wholesale and the
    /// reason recorded — it no longer takes the whole index down with it. Per plugin it's ATOMIC: records go into a
    /// per-plugin buffer and merge into the shared index only if the WHOLE plugin enumerated, so a plugin that throws
    /// part-way never leaves a half-set behind (which would mis-resolve winners for its un-enumerated records).
    /// Returns the build as ONE immutable <see cref="IndexSnapshot"/> — the caller swaps it in with a single
    /// reference write, so a concurrent reader only ever sees a complete, internally-consistent build.</summary>
    IndexSnapshot BuildIndex()
    {
        var index = new Dictionary<FormKey, (int winner, int count)>();
        var overriders = new Dictionary<FormKey, List<int>>();        // multi keys only
        var failures = new List<string>();
        var excluded = new HashSet<int>();
        var unopenable = new HashSet<int>();                          // the could-not-be-OPENED subset
        var excludedPlugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var light = new bool[_paths.Length];
        var lightUnknown = new List<string>();
        int maxDepth = 0;

        for (int i = 0; i < _paths.Length; i++)
        {
            ISkyrimModGetter ov;
            try { ov = OpenOverlay(_paths[i], _dataDir); }
            catch (Exception ex)
            {
                Exclude(i, $"could not be opened — {Concise(ex)}", failures, excluded, excludedPlugins);
                unopenable.Add(i);   // cannot serve as a master overlay either; the write path must skip it
                lightUnknown.Add(_names[i]);   // its kind decides every runtime index after it, and we cannot read it
                continue;
            }

            // Both ways, like the merge report reads it: the engine force-treats the .esl extension as light
            // whatever the header bit says, and an esp-fe carries the bit without the extension.
            light[i] = ov.IsSmallMaster || ov.ModKey.Type == ModType.Light;

            // Buffer the WHOLE plugin's keys first (plugin-atomic). EnumerateMajorRecords() constructs each record
            // body as it advances, so a record Mutagen rejects (e.g. a malformed PKCU data-count) throws HERE. The
            // throw is non-resumable, so we can't skip just that record — but catching it lets us EXCLUDE this one
            // plugin and carry on with every other, instead of letting the throw kill the entire index. The buffer
            // means a partial enumeration is discarded, not merged.
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

            foreach (var fk in keys)                                       // merge the COMPLETE plugin into the index
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
            failures, excluded, unopenable, excludedPlugins, maxDepth, ComputeEpoch(_names, _paths, _mtimes, excludedPlugins),
            light, lightUnknown);
    }

    /// <summary>The epoch fingerprint: a compact, deterministic identity for ONE index build, derived from the
    /// world-state the build was made from — every plugin's filename, RESOLVED PATH, and last-write time, in
    /// priority order, PLUS which plugins this build EXCLUDED. The path matters because under MO2 two enabled mods can
    /// ship the same-named plugin, and a left-pane reorder swaps WHICH file wins the slot without changing name or (if
    /// the copies share a last-write tick) mtime — names and mtimes alone would give the new build the old epoch while
    /// resolving different winners. The exclusion set matters just as much: an OPEN failure is transient (xEdit or MO2
    /// holding an exclusive handle, an AV scan), so a build that skipped a locked plugin resolves materially different
    /// winners than the healthy build over the same names/paths/mtimes — without this term the two fingerprint
    /// identically, and an artifact saved under the degraded build would pass epoch-checked re-entry against the
    /// healthy one. Two builds over an unchanged order that INDEXED the same set fingerprint identically, so a server
    /// restart invalidates nothing; any content edit, reorder, set change, or exclusion change fingerprints
    /// differently. Stamped into every bulk response's in-band accounting so cross-page drift is detectable instead of
    /// silently incoherent, and checked on artifact re-entry (a mismatch refuses loud, naming both epochs).
    /// Opaque to consumers — 16 hex chars of SHA-256, compared only for equality.
    ///
    /// <para>Known approximations: an unstattable-but-openable file collapses to
    /// <see cref="SafeMtime"/>'s MinValue sentinel (distinct world-states, one mtime term — vanishingly rare since
    /// a file that can't be statted rarely opens); and <see cref="RefreshIfStale"/> stamps mtimes BEFORE re-reading
    /// the files, so a plugin rewritten mid-rebuild can pair its new mtime with old content until its next change —
    /// the pre-existing freshness-baseline race, which this fingerprint shares by construction.</para></summary>
    static string ComputeEpoch(string[] names, string[] paths, DateTime[] mtimes, Dictionary<string, string> excludedPlugins)
    {
        var sb = new StringBuilder(names.Length * 96);
        for (int i = 0; i < names.Length; i++)
            sb.Append(names[i]).Append('|').Append(paths[i]).Append('|').Append(mtimes[i].Ticks).Append('\n');
        // Sorted, names only: the exclusion REASON often embeds exception text (message wording, paths) that can
        // vary between identical world-states — WHICH plugins were skipped is the deterministic fact that changes
        // what the index resolves.
        foreach (var name in excludedPlugins.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            sb.Append("excluded|").Append(name).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>Record one plugin's exclusion from the index build: into the human-readable failure list, the
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
        // record" context is front-loaded in ToString() so it survives the 300-char cap either way. Reading the
        // exception's record-identity properties would be the locale-proof upgrade.
        int at = s.IndexOf("\n   at ", StringComparison.Ordinal);
        var head = (at >= 0 ? s.Substring(0, at) : s).Replace("\r", "").Replace("\n", " | ").Trim();
        return head.Length > 300 ? head.Substring(0, 300) + "…" : head;
    }

    // ---- Snapshot-scoped reads: one build per logical operation --------------------

    /// <summary>Capture the CURRENT build as a pinned read view. The snapshot swap makes each individual read
    /// internally consistent; this is the cross-VALUE companion. A service method that issues SEVERAL resolver reads
    /// in one logical operation could otherwise observe TWO adjacent builds if a freshness rebuild landed between
    /// them — a status line mixing counters from different builds, a record's winner disagreeing with its own
    /// touching-plugin list, a scan row's winner reflecting a newer build than the scan that produced it. Capture
    /// ONCE per logical operation (one service method / one tool call) and
    /// answer every question in that operation off the SAME view; a rebuild mid-operation then changes nothing
    /// the operation reports. Pure data over the immutable snapshot — no handles, safe to hold for a call.</summary>
    public IndexView Capture() => new(this, _snap);

    // ---- runtime FormIDs: the eight-hex form the game, the console and the logs print --------------------

    /// <summary>Single-shot <see cref="IndexView.ParseFormId"/> for a caller holding the resolver rather than a
    /// captured view. A door parsing a LIST should capture once and parse off the view.</summary>
    public FormKey ParseFormId(string? raw) => Capture().ParseFormId(raw);

    /// <summary>Turn a runtime FormID into the FormKey it names in THIS build, or throw one plain sentence saying
    /// which index was not found and what to try instead. The high byte picks the table: 0xFE is the light block,
    /// 0xFF is a dynamic form that no plugin defines, anything else is a load index.</summary>
    FormKey RuntimeToFormKey(IndexSnapshot s, uint value)
    {
        if (RuntimeFormId.IsDynamic(value))
            throw new FormatException(
                $"{RuntimeFormId.Format(value)} is a dynamic form — the game creates FF...... FormIDs while playing and " +
                "they exist only in a save game, so no plugin defines one.");

        if (s.LightFlagUnknown.Count > 0)
            throw new FormatException(
                $"a runtime FormID cannot be resolved this session: '{s.LightFlagUnknown[0]}' could not be opened, so " +
                "whether it is light — and therefore every index after it — is unknown; address the record as " +
                "'XXXXXX:Plugin.esp' instead.");

        var slots = s.Slots.Value;
        if (RuntimeFormId.IsLight(value))
        {
            int slot = (int)RuntimeFormId.LightIndex(value);
            int p = slot < slots.LightToPlugin.Length ? slots.LightToPlugin[slot] : -1;
            if (p < 0)
                throw new FormatException(
                    $"{RuntimeFormId.Format(value)} names light index 0x{slot:X3}, which no active plugin occupies " +
                    $"(the load order has {slots.LightCount} light plugin(s)) — the plugin may be inactive; read an " +
                    "inactive plugin with 'XXXXXX:Plugin.esp'.");
            return FormKey.Factory($"{value & FormIdRange.LightObjectIdMask:X6}:{_names[p]}");
        }

        int idx = (int)RuntimeFormId.LoadIndex(value);
        int fp = idx < slots.FullToPlugin.Length ? slots.FullToPlugin[idx] : -1;
        if (fp < 0)
            throw new FormatException(
                $"{RuntimeFormId.Format(value)} names load index 0x{idx:X2}, which no active plugin occupies " +
                $"(the load order has {slots.FullCount} full plugin(s); light plugins are addressed as FExxxYYY) — " +
                "the plugin may be inactive; read an inactive plugin with 'XXXXXX:Plugin.esp'.");
        return FormKey.Factory($"{value & FormIdRange.ObjectIdMask:X6}:{_names[fp]}");
    }

    /// <summary>The runtime FormID the game, the console and the logs print for this record, or null when its plugin
    /// is not active in this build (nothing in the order addresses it) or its kind could not be read.</summary>
    string? RuntimeFormIdFor(IndexSnapshot s, FormKey fk)
    {
        if (fk.IsNull || s.LightFlagUnknown.Count > 0) return null;
        if (!_nameToIdx.TryGetValue(fk.ModKey.FileName.ToString(), out int p)) return null;
        int slot = s.Slots.Value.SlotOfPlugin[p];
        if (slot < 0) return null;
        return RuntimeFormId.Format(RuntimeFormId.Compose(s.Light[p], slot, fk.ID));
    }

    /// <summary>A read view pinned to ONE captured index build (see <see cref="Capture"/>). Every member answers
    /// from the SAME build — winner, touching list, counters, and the scan streams can never disagree about which
    /// build they describe. Bodies are still fetched from the files on disk (none are held), so a
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

        /// <summary>Is this plugin the could-not-be-OPENED exclusion class? A caller that is ABOUT to open a
        /// plugin file itself (rather than going through the master-set builders) asks this first, so an unopenable one
        /// becomes a named refusal instead of an exception escaping from a bare CreateFromBinaryOverlay.</summary>
        public bool IsUnopenable(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int i) && _s.Unopenable.Contains(i);

        /// <summary>THIS captured build's epoch fingerprint — the stamp a bulk response computed off this view
        /// must carry. Immutable with the snapshot: a concurrent rebuild changes nothing here.</summary>
        public string Epoch => _s.Epoch;

        /// <summary>The one FormID door: parse a caller's token into a FormKey. An eight-hex token with no colon is a
        /// RUNTIME FormID (<c>FExxxYYY</c> or <c>XX######</c>, with or without <c>0x</c>) and is resolved against this
        /// build's runtime address tables; anything else is the <c>XXXXXX:Plugin.esp</c> form and is unchanged. Throws
        /// one plain sentence either way, which every door already renders as its own "bad FormID" refusal.</summary>
        public FormKey ParseFormId(string? raw)
            => RuntimeFormId.TryParse(raw, out uint v) ? _r.RuntimeToFormKey(_s, v) : FormKey.Factory((raw ?? "").Trim());

        /// <summary>The runtime FormID this record prints as in the game, the console and the logs, or null when its
        /// plugin is not active in this build. Rendered beside the <c>XXXXXX:Plugin.esp</c> form so a reader can
        /// round-trip to the console and back.</summary>
        public string? RuntimeFormIdOf(FormKey fk) => _r.RuntimeFormIdFor(_s, fk);

        /// <summary>Whether a plugin filename is in the indexed load order — the same OrdinalIgnoreCase name table
        /// <see cref="LoadOrderResolver.GetRecord"/> resolves against (fixed for the resolver's lifetime, like
        /// <see cref="PluginCount"/>). False for a plugin on disk but not enabled/registered — a state the service
        /// must name DISTINCTLY from "in the order but doesn't define the record", because GetRecord returns null
        /// for both.</summary>
        public bool ContainsPlugin(string pluginName) => _r._nameToIdx.ContainsKey(pluginName);

        /// <summary>The trailing clause for a refusal naming a plugin <see cref="ContainsPlugin"/> just returned false
        /// for: WHY it isn't in the order (injected — typically "installed, but UNTICKED in plugins.txt"), else a
        /// did-you-mean. Always safe to append; returns "" when there is nothing to add. Every ContainsPlugin-false
        /// refusal should carry it — a bare not-found makes the reader re-derive a fact the tool already had.</summary>
        public string AbsenceClause(string pluginName) => _r.AbsenceClause(pluginName);

        /// <summary>Only the injected CAUSE (null when there is none) — see
        /// <see cref="LoadOrderResolver.ExplainAbsence"/>; pair with <see cref="NameSuggestion"/> to rebuild the full
        /// clause without invoking the explainer twice.</summary>
        public string? ExplainAbsence(string pluginName) => _r.ExplainAbsence(pluginName);

        /// <summary>The did-you-mean fallback — see <see cref="LoadOrderResolver.NameSuggestion"/>.</summary>
        public string NameSuggestion(string pluginName) => _r.NameSuggestion(pluginName);

        /// <summary>The on-disk PATH of the active plugin named <paramref name="pluginName"/> (a filename like
        /// "MyMod.esp"), or null if no such plugin is in the order. The minimal name→path exposure the dialogue
        /// validator's SEQ lint needs to stat the quest's defining plugin (mtime) and read its master list; the
        /// resolver otherwise exposes only filenames (see <see cref="WinnerInfo.WinnerPlugin"/>).</summary>
        public string? PluginPath(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int idx) ? _r._paths[idx] : null;

        /// <summary>The real game-Data folder this order resolved — see <see cref="LoadOrderResolver.DataDir"/>. Paired
        /// with <see cref="PluginPath"/>: a caller OUTSIDE this assembly that opens a plugin file itself must pass this
        /// to <see cref="LoadOrderResolver.OpenOverlay"/>, or a localized plugin whose own folder carries no strings
        /// source reads every TranslatedString EMPTY. The resolver's own DataDir is
        /// internal, so the write lanes that hold a captured view — merge and compact, which open donor/source plugins
        /// directly — reach it here.</summary>
        public string? DataDir => _r.DataDir;

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

        /// <summary>The winner-body scan stream (<see cref="LoadOrderResolver.WinnerRecordsOfType(IReadOnlyList{Type}, ICollection{PluginUnreadableException}?)"/>),
        /// pinned to THIS view's build — so a caller's per-match winner/depth fills agree with the scan by construction.</summary>
        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(
            IReadOnlyList<Type> getterTypes, ICollection<PluginUnreadableException>? unreadable = null)
            => _r.WinnerRecordsOfType(getterTypes, _s, unreadable);

        /// <summary>The plugin-scoped scan stream (<see cref="LoadOrderResolver.RecordsIn(IReadOnlyList{string}, IReadOnlyList{Type})"/>),
        /// pinned to THIS view's build.</summary>
        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
            IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
            => _r.RecordsIn(plugins, getterTypes, _s);

        /// <summary>One record's body from a named plugin (<see cref="LoadOrderResolver.GetRecord"/>), with the
        /// excluded-plugin check judged against THIS view's build — so a winner this view resolved and the body
        /// fetched for it can never be vetted by two different builds. The write path resolves every edit of one call
        /// off ONE view; reads pin their fetch to the same view they resolved with.</summary>
        public IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk)
            => _r.GetRecord(session, pluginName, fk, _s);

        /// <summary>The master filenames a plugin DECLARES in its header (the master table), in declared order — opens
        /// the overlay, reads the header, disposes — the header parses without enumerating records. Throws
        /// on a name not in the order or excluded this build. The integrity sweep diffs this against the masters a
        /// plugin's records actually reference; judged against THIS view's build (same exclusion set as the scan).</summary>
        public IReadOnlyList<string> DeclaredMasters(string pluginName) => _r.DeclaredMasters(pluginName, _s);

        /// <summary>The full conflict tree (<see cref="LoadOrderResolver.ResolveTree(OverlaySession, FormKey)"/>),
        /// pinned to THIS view's build — so a render that stamps this view's epoch fills its trees from the same
        /// build the stamp names.</summary>
        public ConflictTree? ResolveTree(OverlaySession session, FormKey fk) => _r.ResolveTree(session, fk, _s);
    }

    // ---- Queries -------------------------------------------------------
    //  Single-shot conveniences: each delegates to a fresh Capture(), so one call = one build. A caller making
    //  SEVERAL reads in one logical operation should Capture() once and read off the view instead — the discipline
    //  the service layer follows.

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
    public ConflictTree? ResolveTree(OverlaySession session, FormKey fk) => ResolveTree(session, fk, _snap);

    /// <summary>The snapshot-pinned body of <see cref="ResolveTree(OverlaySession, FormKey)"/> — taken by
    /// <see cref="IndexView.ResolveTree"/> so a caller that already pinned a build (a cross-query render filling
    /// conflict trees per match) reads the tree off the SAME build its epoch stamp names.</summary>
    internal ConflictTree? ResolveTree(OverlaySession session, FormKey fk, IndexSnapshot s)
    {
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
    /// this plugin overwriting or being overwritten on". Opens the plugin for the enumeration and disposes it when the
    /// enumeration ends — one handle, self-scoped; the yielded status is pure data.</summary>
    public IEnumerable<RecordStatus> PluginRecordStatus(string pluginName)
    {
        var s = _snap;                                                 // ONE build, captured for the whole enumeration
        if (!_nameToIdx.TryGetValue(pluginName, out int idx))
            throw new ArgumentException($"plugin not in the load order: {pluginName}.{AbsenceClause(pluginName)}");
        if (s.Excluded.Contains(idx))
            throw new ArgumentException($"plugin '{pluginName}' was excluded from this session: {s.ExcludedPlugins[pluginName]}");
        var ov = OpenOverlay(_paths[idx], _dataDir);
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                var fk = rec.FormKey;
                if (!s.Index.TryGetValue(fk, out var e))               // the FILE outran the snapshot (edited after the build) — name it, never skip
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
        => GetRecord(session, pluginName, fk, _snap);                      // single-shot: this call = its own build (the IndexView overload pins a whole operation)

    IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk, IndexSnapshot s)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx)) return null;
        if (s.Excluded.Contains(idx)) return null;                         // excluded plugin — never re-enumerate it (would re-throw); the service reports the reason via ExcludedPlugins
        foreach (var rec in session.Overlay(idx).EnumerateMajorRecords())
            if (rec.FormKey == fk) return rec;
        return null;
    }

    /// <summary>The master filenames a plugin declares in its header (the master TABLE). Opens the overlay, reads
    /// <c>ModHeader.MasterReferences</c>, disposes — the header parses without enumerating records. Throws
    /// on a name not in the order or excluded this build, mirroring <see cref="PluginRecordStatus"/>. The
    /// integrity sweep (housecarl_check_errors) diffs this against the masters a plugin's records actually reference.</summary>
    public IReadOnlyList<string> DeclaredMasters(string pluginName) => DeclaredMasters(pluginName, _snap);

    IReadOnlyList<string> DeclaredMasters(string pluginName, IndexSnapshot s)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx))
            throw new ArgumentException($"plugin not in the load order: {pluginName}.{AbsenceClause(pluginName)}");
        if (s.Excluded.Contains(idx))
            throw new ArgumentException($"plugin '{pluginName}' was excluded from this session: {s.ExcludedPlugins[pluginName]}");
        var ov = OpenOverlay(_paths[idx], _dataDir);
        try { return ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Fetch one record body from one overlay by re-enumerating it into the session. Throws if the overlay
    /// can't yield a FormKey the index says it contains — a real inconsistency, named rather than swallowed.</summary>
    IMajorRecordGetter FetchBody(OverlaySession session, int overlayIdx, FormKey fk)
    {
        foreach (var rec in session.Overlay(overlayIdx).EnumerateMajorRecords())
            if (rec.FormKey == fk) return rec;
        throw new InvalidOperationException(
            $"body-fetch inconsistency: {_names[overlayIdx]} is indexed as containing {fk} but did not yield it on re-enumeration.");
    }

    // ---- Cross-query scan primitives -------------------
    //  These feed cross_plugin_query. Each is a SINGLE enumeration pass that yields the matching record's body
    //  IN HAND: fetching each winner body separately instead costs a whole-overlay re-enumeration per candidate,
    //  which is minutes over a large type. The body the service filters on is this in-hand body; the resolver holds
    //  nothing past the yield. Each opens the CURRENT plugin, enumerates it, and DISPOSES it before moving to the
    //  next — one handle at a time, and every yielded body is consumed by the caller before the iterator advances.

    /// <summary>Stream every record of the given type(s) whose instance in this overlay IS the load-order winner
    /// — i.e. the WINNER body, in hand, for each distinct typed FormKey (no re-fetch). Typed group enumeration
    /// (Mutagen seeks the GRUP). Multiple types (GMST → 4 GameSetting variants) are unioned. Yields
    /// (FormKey, override-depth, winner body). throwIfUnknown is belt-and-braces — resolved types are always
    /// real.
    ///
    /// A plugin that opened at index-build time but cannot be opened now wins records this scan would otherwise drop
    /// without a word. Pass <paramref name="unreadable"/> to collect such a plugin's
    /// <see cref="PluginUnreadableException"/> and have the scan carry on to the plugins after it — the shape every
    /// whole-order caller wants, since a throw mid-stream would lose them. With no collector the stream THROWS
    /// instead, so a caller that has nowhere to report the gap fails loudly rather than returning a partial scan as a
    /// clean one.</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(
        IReadOnlyList<Type> getterTypes, ICollection<PluginUnreadableException>? unreadable = null)
        => WinnerRecordsOfType(getterTypes, _snap, unreadable);            // ONE build for the whole scan (captured here, at the call)

    IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(
        IReadOnlyList<Type> getterTypes, IndexSnapshot s, ICollection<PluginUnreadableException>? unreadable)
    {
        for (int i = 0; i < _paths.Length; i++)
        {
            if (s.Excluded.Contains(i)) continue;                          // excluded at build (unparseable/unopenable) — wins nothing; never re-touch (would re-throw)
            ISkyrimModGetter ov;
            try { ov = OpenOverlay(_paths[i], _dataDir); }
            catch (Exception ex)
            {
                // It opened when the index was built, so it became unreadable after that — moved, or held open by
                // another program. Collected and skipped when the caller can report the gap; thrown otherwise.
                var fault = new PluginUnreadableException(_names[i], ex);
                if (unreadable is null) throw fault;
                unreadable.Add(fault);
                continue;
            }
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
    /// with that PLUGIN'S body in hand — the plugin-scoped path behind a plugin dump or content audit. A FormKey
    /// touched by more than one scoped plugin is yielded once per scoped plugin; the SERVICE de-dupes.
    /// Yields (FormKey, whole-order override-depth, the scoped plugin's body, the scoped plugin's
    /// filename — so a caller can DISPLAY from the same body it filtered, not the winner). Holds nothing.
    /// Throws <see cref="PluginUnreadableException"/> on a scoped plugin that opened at index-build time but cannot be
    /// opened now, ending the stream — a caller must report that plugin rather than treat the scan as complete. With a
    /// multi-plugin scope the throw ends the whole stream, so the plugins after it are unread too.</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
        => RecordsIn(plugins, getterTypes, _snap);                         // ONE build for the whole scan (captured here, at the call)

    IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes, IndexSnapshot s)
    {
        foreach (int i in ScopeIndices(plugins, s))
        {
            ISkyrimModGetter ov;
            // A plugin that opened at build time but not now became unreadable after it — moved, or held open by
            // another program. Surfaced with the name so a caller reports a partial scan instead of a clean one.
            try { ov = OpenOverlay(_paths[i], _dataDir); }
            catch (Exception ex) { throw new PluginUnreadableException(_names[i], ex); }
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

    /// <summary>Resolve a scope (plugin filenames) to overlay indices; null/empty = the whole order. Throws on a name
    /// not in the order, naming it — never silently scans an empty or partial scope. Works off the caller's captured
    /// snapshot so scope and scan judge exclusion against the SAME build.</summary>
    IReadOnlyList<int> ScopeIndices(IReadOnlyList<string>? scopePlugins, IndexSnapshot s)
    {
        if (scopePlugins is null || scopePlugins.Count == 0)
            return Enumerable.Range(0, _paths.Length).Where(i => !s.Excluded.Contains(i)).ToArray();  // whole order, minus excluded
        var idxs = new List<int>(scopePlugins.Count);
        foreach (var name in scopePlugins)
        {
            if (!_nameToIdx.TryGetValue(name, out int i))
                throw new ArgumentException($"plugin not in the load order: {name}.{AbsenceClause(name)}");
            if (s.Excluded.Contains(i))                                    // explicitly scoped to an excluded plugin → fail loud with the reason, don't silently scan nothing
                throw new ArgumentException($"plugin '{name}' was excluded from this session: {s.ExcludedPlugins[name]}");
            idxs.Add(i);
        }
        return idxs;
    }

    // ---- Freshness -----------------------------------------------------

    /// <summary>Re-stat the plugin files; if any last-write differs from the build-time baseline, rebuild the index
    /// (re-enumerating one plugin at a time; there are no held overlays to dispose and reopen), then return true. The
    /// cheap no-change path is just the stat sweep. Content edits to existing plugins are handled here; a changed
    /// plugin SET (added or removed) is a new order, and the caller re-Builds.</summary>
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

    /// <summary>The resolver holds NO plugin file handles at rest (only the pure-data index), so there is nothing to
    /// release — Dispose is a no-op, kept so the service can treat a resolver as a disposable resource it builds and
    /// swaps over its lifetime, and so `using var resolver = …` call sites stay valid.</summary>
    public void Dispose() { }
}

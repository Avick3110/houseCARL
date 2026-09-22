using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlCore;

// LoadOrderResolver — a held structural index over the active order plus on-demand targeted parse: no record bodies and
// no plugin file handles at rest, the order injected. Contracts in docs/architecture/load-order-resolver.md.

public sealed record ConflictNode(string Plugin, IMajorRecordGetter Record);

public sealed record ConflictTree(FormKey FormKey, string RecordType, IReadOnlyList<ConflictNode> Nodes)
{
    public ConflictNode Winner => Nodes[^1];
    public bool IsConflict => Nodes.Count > 1;
}

public readonly record struct WinnerInfo(FormKey FormKey, string WinnerPlugin, int OverrideDepth);

public readonly record struct RecordStatus(
    FormKey FormKey, string RecordType, bool PluginWins, int OverrideDepth, IReadOnlyList<string> TouchingPlugins);

/// <summary>A plugin could not be opened during a scan, although it opened when the index was built; thrown from the record stream so a scan reports it rather than skipping it.</summary>
public class PluginUnreadableException : Exception
{
    public string PluginName { get; }
    public PluginUnreadableException(string pluginName, Exception inner)
        : this(pluginName, inner,
               $"could not read '{pluginName}': it opened when the load order was indexed but not now — another " +
               "program is probably holding it open. Close that program and run this again. " +
               $"({inner.GetType().Name}: {inner.Message})") { }

    protected PluginUnreadableException(string pluginName, Exception inner, string message)
        : base(message, inner) => PluginName = pluginName;
}

/// <summary>A plugin OPENED but its records could not be walked to the end — a different fault from <see cref="PluginUnreadableException"/>, worded as one, reported the same way.</summary>
public sealed class PluginUnscannableException : PluginUnreadableException
{
    public PluginUnscannableException(string pluginName, Exception inner)
        : base(pluginName, inner,
               $"could not finish reading '{pluginName}': it opened, but its records could not be walked to the end " +
               "— the file has probably changed since the load order was indexed. Run this again, and check the " +
               $"plugin in xEdit if it repeats. ({inner.GetType().Name}: {inner.Message})") { }
}

/// <summary>A BASELINE master (Skyrim.esm / Update.esm) is active but cannot be opened; thrown from the master-set builders, the point every write lane funnels through.</summary>
public sealed class UnopenableBaselineMasterException : Exception
{
    public string PluginName { get; }
    public UnopenableBaselineMasterException(string pluginName)
        : base($"'{pluginName}' is a BASELINE master (Skyrim.esm / Update.esm) and is ACTIVE in your load order, but " +
               "houseCARL cannot open it — see load_order_status for the reason. Nothing can be written while that is " +
               "true, for either reason alone: a new patch must list the baselines in its header (emitting one without " +
               "them produces a plugin the game treats as malformed), and no write can resolve references against a " +
               "master it cannot read. Repair or replace that plugin in MO2 and retry. Nothing was written.")
        => PluginName = pluginName;
}

/// <remarks>The master-set builders enforce a WRITE policy in this read-side class on purpose; contract in docs/architecture/load-order-resolver.md.</remarks>
public sealed class LoadOrderResolver : IDisposable
{
    readonly string[] _paths;                          // every active plugin's path, priority order (masters → … → winner)
    readonly string[] _names;                          // index → plugin filename (e.g. "Skyrim.esm"); == Path.GetFileName(path)
    readonly Dictionary<string, int> _nameToIdx;       // plugin filename → index (last copy of a duplicate name wins = priority)
    FileStamp[] _stamps;                               // last-write AND length at the last index build, per path (freshness baseline)
    readonly string? _dataDir;                         // real game-Data folder (Skyrim.esm's dir) — localized-strings fallback source (OpenOverlay)
    ReverseReferenceIndex? _reverse;                   // lazy, held BESIDE the snapshot so a snapshot swap does not drop it
    readonly object _reverseGate = new();              // one build at a time; a second caller waits rather than walking the order twice

    /// <summary>The real game-Data folder this order resolved, for callers OUTSIDE the session that open a plugin through <see cref="OpenOverlay"/> and need the same strings resolution.</summary>
    internal string? DataDir => _dataDir;

    /// <summary>Optional: why a plugin filename this index does NOT contain isn't in the active order, or null. Injected, because the resolver knows nothing of MO2;
    /// contract in docs/architecture/load-order-service.md.</summary>
    readonly Func<string, string?>? _explainAbsence;

    /// <summary>One index build's ENTIRE output, swapped in as a SINGLE reference write so no reader sees a torn view; contract in docs/architecture/load-order-resolver.md.</summary>
    internal sealed class IndexSnapshot   // internal (not private) so IndexView's ctor can take it; never leaves the assembly
    {
        public readonly Dictionary<FormKey, (int winner, int count)> Index;   // ALL keys — winner + depth, O(1)
        public readonly Dictionary<FormKey, int[]> Overriders;                // MULTI keys only — ordered touching overlay indices
        public readonly List<string> LoadFailures;                            // per-plugin index-build failures (open OR parse), always surfaced
        public readonly HashSet<int> Excluded;                                // overlay indices excluded this build — never re-touched by any path
        /// <summary>The SUBSET of <see cref="Excluded"/> whose file could not be OPENED at all; its own set, not derived from the reason string — prose can be reworded, membership is a fact.</summary>
        public readonly HashSet<int> Unopenable;
        public readonly Dictionary<string, string> ExcludedPlugins;           // excluded plugin name → reason
        public readonly int MaxDepth;
        public readonly string Epoch;                                         // this build's fingerprint — immutable with the snapshot

        public readonly OrderStamp Stamp;
        public readonly ContainmentIndex Containment;                         // child → parent, off the same one-pass walk

        /// <summary>Per plugin index: is it a LIGHT plugin? Read off the header while the build had the overlay open, and the .esl extension counts too.</summary>
        public readonly bool[] Light;

        /// <summary>Per plugin index: is it in the MASTER BLOCK (a header Master flag, or a .esm/.esl filename)? A separate fact from <see cref="Light"/> — an esp-fe is light but regular in the order.</summary>
        public readonly bool[] MasterBlock;

        /// <summary>The FIRST active plugin whose kind could not be read, as a POSITION not a flag: a runtime FormID at or after it cannot be answered, one before it can. -1 when all are known.</summary>
        public readonly int FirstUnknownKind;

        public readonly string? FirstUnknownKindName;

        public readonly Lazy<RuntimeSlots> Slots;

        public IndexSnapshot(Dictionary<FormKey, (int winner, int count)> index, Dictionary<FormKey, int[]> overriders,
                             List<string> loadFailures, HashSet<int> excluded, HashSet<int> unopenable,
                             Dictionary<string, string> excludedPlugins, int maxDepth, string epoch,
                             bool[] light, int firstUnknownKind, string? firstUnknownKindName,
                             ContainmentIndex containment, bool[] masterBlock)
        {
            Index = index; Overriders = overriders; LoadFailures = loadFailures; Excluded = excluded; Unopenable = unopenable;
            ExcludedPlugins = excludedPlugins; MaxDepth = maxDepth; Epoch = epoch; Light = light; Containment = containment;
            MasterBlock = masterBlock;
            FirstUnknownKind = firstUnknownKind; FirstUnknownKindName = firstUnknownKindName;
            Slots = new Lazy<RuntimeSlots>(() => RuntimeSlots.Build(light));
            Stamp = OrderStamp.For(epoch, excludedPlugins.Keys);
        }
    }

    /// <summary>The runtime address tables for ONE build: which plugin loads at each load index and at each light index. The engine numbers the two kinds separately.</summary>
    internal sealed class RuntimeSlots
    {
        public readonly int[] FullToPlugin;
        public readonly int[] LightToPlugin;
        public readonly int[] SlotOfPlugin;
        public readonly int FullCount;
        public readonly int LightCount;

        RuntimeSlots(int[] fullToPlugin, int[] lightToPlugin, int[] slotOfPlugin, int fullCount, int lightCount)
        { FullToPlugin = fullToPlugin; LightToPlugin = lightToPlugin; SlotOfPlugin = slotOfPlugin; FullCount = fullCount; LightCount = lightCount; }

        /// <summary>0xFE is the light block and 0xFF the runtime-dynamic block, so a full plugin loads at 0x00–0xFD.</summary>
        public const int MaxFullSlots = 0xFE;
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

    /// <summary>Per-plugin index-build failures, each excluded plugin named with its reason — the display form of <see cref="ExcludedPlugins"/>.</summary>
    public IReadOnlyList<string> LoadFailures => _snap.LoadFailures;

    public bool IsUnopenable(string pluginName)
        => _nameToIdx.TryGetValue(pluginName, out int i) && _snap.Unopenable.Contains(i);

    /// <summary>Plugins EXCLUDED from this build (name → why): unopenable, or carrying a record Mutagen cannot parse. load_order_status reports them, so the exclusion is visible.</summary>
    public IReadOnlyDictionary<string, string> ExcludedPlugins => _snap.ExcludedPlugins;

    public int PluginCount => _paths.Length;
    public int RecordCount => _snap.Index.Count;            // distinct FormKeys across the order
    public int ConflictCount => _snap.Overriders.Count;     // FormKeys overridden by >1 plugin
    public int MaxDepth => _snap.MaxDepth;

    public string Epoch => _snap.Epoch;

    public OrderStamp Stamp => _snap.Stamp;

    public IReadOnlyList<string> PluginNames => _names;

    // ---- Per-call overlay session: open on demand, dispose at call end, zero handles at rest ----

    /// <summary>Open a per-call overlay session: one call opens every plugin it needs THROUGH it, each at most once, and disposes them all at return;
    /// contract in docs/architecture/load-order-resolver.md.</summary>
    public OverlaySession OpenSession() => new(this);

    /// <summary>How many overlay OPENS sessions have paid in this process — what a test can hold a session-reuse claim to.</summary>
    internal static long SessionOverlayOpens;

    internal static long BodySeeks;

    internal static long CollectPasses;

    public sealed class OverlaySession : IDisposable
    {
        readonly LoadOrderResolver _r;
        readonly Dictionary<int, ISkyrimModGetter> _open = new();
        internal OverlaySession(LoadOrderResolver r) => _r = r;

        internal ISkyrimModGetter Overlay(int idx)
        {
            if (!_open.TryGetValue(idx, out var ov))
            {
                System.Threading.Interlocked.Increment(ref SessionOverlayOpens);
                _open[idx] = ov = OpenOverlay(_r._paths[idx], _r._dataDir);
            }
            return ov;
        }

        /// <summary>Open EVERY plugin (priority order) as the FULL known-master set the multi-master write path hands the serializer; a write into an active patch uses <see cref="AllMastersExcept"/>.</summary>
        public IReadOnlyList<ISkyrimModGetter> AllMasters()
        {
            // Excluded plugins that OPEN are retained on purpose; the unopenable ones cannot be, and are skipped and named.
            // Contract in docs/architecture/load-order-resolver.md.
            var arr = new List<ISkyrimModGetter>(_r._paths.Length);
            for (int i = 0; i < _r._paths.Length; i++)
            {
                if (SkipUnopenable(i)) continue;
                arr.Add(Overlay(i));
            }
            return arr;
        }

        /// <summary>Like <see cref="AllMasters"/>, but never OPENS an overlay on <paramref name="excludeFileName"/> — the file about to be serialized, which a mapped handle would lock;
        /// contract in docs/architecture/load-order-resolver.md.</summary>
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

        /// <summary>Is this index the could-not-be-OPENED exclusion class, which no master set can contain? Records the name, because a sorted multi-master header then refuses naming it.</summary>
        bool SkipUnopenable(int i)
        {
            if (!_r._snap.Unopenable.Contains(i)) return false;
            var name = _r._names[i];
            // A BASELINE master must never be skipped: that would silently emit a plugin missing a mandatory master, so this refuses.
            // Contract in docs/architecture/load-order-resolver.md.
            if (Array.Exists(WriteEngine.BaselineMasters, bm => string.Equals(bm.FileName.String, name, StringComparison.OrdinalIgnoreCase)))
                throw new UnopenableBaselineMasterException(name);
            _skippedUnopenable.Add(name);
            return true;
        }

        readonly SortedSet<string> _skippedUnopenable = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The plugins this session's master-set builds skipped as unopenable; a SET with an OrdinalIgnoreCase comparer, so a serialize failure can be attributed to one of them.</summary>
        public IReadOnlySet<string> SkippedUnopenable => _skippedUnopenable;

        /// <summary>Dispose and forget any overlay this session holds on <paramref name="fileName"/> — the second half of the active-patch write fix. A no-op when none is open.</summary>
        public void ReleaseOverlay(string fileName)
        {
            var hits = _open.Keys.Where(i => string.Equals(_r._names[i], fileName, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var i in hits) { (_open[i] as IDisposable)?.Dispose(); _open.Remove(i); }
        }

        /// <summary>An immutable link cache over ONE named plugin in this session, for the write path's nested-parent reconstruction. Costly, never held past the session; null if not in the order.</summary>
        public ILinkCache? LinkCacheFor(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int idx) ? Overlay(idx).ToImmutableLinkCache() : null;

        public void Dispose()
        {
            foreach (var ov in _open.Values) (ov as IDisposable)?.Dispose();
            _open.Clear();
        }
    }

    LoadOrderResolver(string[] paths, string[] names, Dictionary<string, int> nameToIdx, FileStamp[] stamps,
                      Func<string, string?>? explainAbsence)
    {
        _paths = paths; _names = names; _nameToIdx = nameToIdx; _stamps = stamps;
        _explainAbsence = explainAbsence;
        _dataDir = ComputeDataDir(nameToIdx, paths);
        _snap = BuildIndex();
        // Settle the heap ONCE here, on the first build only; a re-index does not repay it. Measured in #728, landed in #802.
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>The trailing clause for a refusal naming a plugin this order does not contain: the injected explanation when there is one, else the did-you-mean. One home.</summary>
    internal string AbsenceClause(string pluginName) => AbsenceClause(pluginName, out _);

    /// <summary>The same clause, also handing back the CAUSE it found (null when it fell through to the did-you-mean); one explainer invocation serves both.</summary>
    internal string AbsenceClause(string pluginName, out string? cause)
    {
        cause = ExplainAbsence(pluginName);
        return cause is { } why ? " " + why : NameSuggestion(pluginName);
    }

    /// <summary>ONLY the injected explanation, or null — the half a caller needs when the presence of a real CAUSE changes more than one sentence.</summary>
    internal string? ExplainAbsence(string pluginName)
    {
        try { return _explainAbsence?.Invoke(pluginName); }
        catch { return null; }   /* an explainer that throws (an unreadable profile mid-call) must never turn a clean
                                    refusal into a crash — fall through to the suggester. */
    }

    internal string NameSuggestion(string pluginName) => PluginNameSuggest.DidYouMean(pluginName, _names);

    /// <summary>The real game-Data directory, derived as the folder of the resolved <c>Skyrim.esm</c> — the localized-strings fallback target in <see cref="OpenOverlay"/>.</summary>
    internal static string? ComputeDataDir(Dictionary<string, int> nameToIdx, string[] paths)
        => nameToIdx.TryGetValue("Skyrim.esm", out var i) ? Path.GetDirectoryName(paths[i]) : null;

    /// <summary>Open one plugin as a lazy binary overlay — THE single overlay-open choke point, wiring localized-string resolution; contract in docs/architecture/load-order-service.md.
    /// Public because the off-order and donor read lanes reuse it as a pure (path, dataDir) factory that touches no resolver index.</summary>
    public static ISkyrimModGetter OpenOverlay(string path, string? dataDir)
    {
        if (dataDir is not null && !FolderHasOwnStrings(path))
        {
            var prm = PluginTextEncoding.ReadWith(path, new StringsReadParameters
            {
                BsaFolderOverride = dataDir,
                StringsFolderOverride = Path.Combine(dataDir, "Strings"),
            });
            return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, prm);
        }
        return SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
    }

    /// <summary>True if the plugin's OWN folder carries a strings source FOR THIS PLUGIN; one home, <see cref="LocalizedStrings.OwnFolderCarriesStringsFor"/>, so read and refusal cannot disagree.</summary>
    internal static bool FolderHasOwnStrings(string path) => LocalizedStrings.OwnFolderCarriesStringsFor(path);

    /// <summary>Take the plugin paths already in priority order and build the index without holding any plugin open; the order is INJECTED, and open failures land in <see cref="LoadFailures"/>.</summary>
    public static LoadOrderResolver Build(IReadOnlyList<string> orderedPluginPaths,
                                          Func<string, string?>? explainAbsence = null)
    {
        var paths = new string[orderedPluginPaths.Count];
        var names = new string[orderedPluginPaths.Count];
        var stamps = new FileStamp[orderedPluginPaths.Count];
        var nameToIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < orderedPluginPaths.Count; i++)
        {
            var p = orderedPluginPaths[i];
            paths[i] = p;
            // A plugin's ModKey filename IS its file name, so the name needs no open; the last copy of a duplicate wins.
            var name = Path.GetFileName(p);
            names[i] = name;
            nameToIdx[name] = i;
            stamps[i] = FileStamp.Of(p);
        }

        // These names are the canonical spelling of every plugin in the order — every FormID token prints through them.
        FormIdToken.Publish(names);

        return new LoadOrderResolver(paths, names, nameToIdx, stamps, explainAbsence);
    }

    /// <summary>Enumerate every plugin once (low→high), ONE AT A TIME, building the winner/count index for all keys and the ordered overrider list for multi-override keys only, and
    /// returning the build as one immutable <see cref="IndexSnapshot"/>; the exclusion contracts are in docs/architecture/load-order-resolver.md.</summary>
    IndexSnapshot BuildIndex()
    {
        var index = new Dictionary<FormKey, (int winner, int count)>();
        var overriders = new Dictionary<FormKey, List<int>>();        // multi keys only
        var failures = new List<string>();
        var excluded = new HashSet<int>();
        var unopenable = new HashSet<int>();                          // the could-not-be-OPENED subset
        var excludedPlugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var light = new bool[_paths.Length];
        var masterBlock = new bool[_paths.Length];
        var containment = new ContainmentIndex();                     // child → parent, off the same walk
        int firstUnknownKind = -1;
        string? firstUnknownKindName = null;
        int maxDepth = 0;

        for (int i = 0; i < _paths.Length; i++)
        {
            ISkyrimModGetter ov;
            try { ov = OpenOverlay(_paths[i], _dataDir); }
            catch (Exception ex)
            {
                Exclude(i, $"could not be opened — {Concise(ex)}", failures, excluded, excludedPlugins);
                unopenable.Add(i);   // cannot serve as a master overlay either; the write path must skip it
                // The header is unreadable, so only the extension can settle whether this one loads into the light block.
                light[i] = ModKey.FromFileName(_names[i]).Type == ModType.Light;
                // Same fallback for the block: with no header, only the filename settles it.
                masterBlock[i] = ModKey.FromFileName(_names[i]).Type is ModType.Master or ModType.Light;
                if (!light[i] && firstUnknownKind < 0) { firstUnknownKind = i; firstUnknownKindName = _names[i]; }
                continue;
            }

            // Both ways: the engine force-treats the .esl extension as light, and an esp-fe carries the bit without it.
            light[i] = ov.IsSmallMaster || ov.ModKey.Type == ModType.Light;
            // The master BLOCK is a different question from the light FormID space; see IndexSnapshot.MasterBlock.
            masterBlock[i] = ov.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master)
                          || ov.ModKey.Type is ModType.Master or ModType.Light;

            // Buffer the WHOLE plugin's keys first (plugin-atomic); the CONTEXT walk carries each record's containing parent.
            var keys = new List<FormKey>();
            var edges = new List<(FormKey Child, FormKey Parent)>();
            try
            {
                foreach (var ctx in ov.EnumerateMajorRecordContexts())
                {
                    keys.Add(ctx.Record.FormKey);
                    ContainmentIndex.Stage(ctx, edges);
                }
            }
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

            containment.Merge(edges);                                      // later plugin wins, like the winner index
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

        // The winner index is readonly from here on and was built at about half fill, so its spare buckets are trimmed. #728.
        index.TrimExcess();
        return new IndexSnapshot(
            index,
            overriders.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()),  // trim List overhead → int[]
            failures, excluded, unopenable, excludedPlugins, maxDepth, ComputeEpoch(_names, _paths, _stamps, excludedPlugins),
            light, firstUnknownKind, firstUnknownKindName, containment, masterBlock);
    }

    /// <summary>The epoch fingerprint: a deterministic identity for ONE build, over every plugin's filename, RESOLVED PATH and freshness stamp in priority order plus which plugins it
    /// EXCLUDED; the contract and its known approximations are in docs/architecture/load-order-resolver.md.</summary>
    static string ComputeEpoch(string[] names, string[] paths, FileStamp[] stamps, Dictionary<string, string> excludedPlugins)
    {
        var sb = new StringBuilder(names.Length * 96);
        for (int i = 0; i < names.Length; i++)
            sb.Append(names[i]).Append('|').Append(paths[i]).Append('|').Append(stamps[i].Mtime.Ticks)
              .Append('|').Append(stamps[i].Size).Append('\n');
        // Sorted, names only: the exclusion REASON embeds exception text that varies between identical world-states.
        foreach (var name in excludedPlugins.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            sb.Append("excluded|").Append(name).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return EpochFormat + "-" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    /// <summary>The epoch string's FORMAT tag, bumped when <see cref="ComputeEpoch"/>'s input changes shape: an epoch with another tag cannot be compared at all.</summary>
    internal const string EpochFormat = "e2";

    /// <summary>Whether an epoch string was written in the format THIS build computes — i.e. whether comparing it against a current epoch means anything.</summary>
    public static bool IsCurrentEpochFormat(string epoch) =>
        epoch.StartsWith(EpochFormat + "-", StringComparison.Ordinal);

    /// <summary>Record one plugin's exclusion: into the failure list, the fast-skip index set, and the name→reason map.</summary>
    void Exclude(int i, string reason, List<string> failures, HashSet<int> excluded, Dictionary<string, string> excludedPlugins)
    {
        failures.Add($"{_names[i]}: {reason}");
        excluded.Add(i);
        excludedPlugins[_names[i]] = reason;
    }

    /// <summary>One-line essence of an exception for a user-facing reason: the message lines up to the stack trace, newlines flattened, bounded, keeping the WHICH-record context.</summary>
    static string Concise(Exception ex)
    {
        var s = ex.ToString();
        // The en-US stack-frame prefix; on a localized runtime the whole ToString() is flattened and capped instead.
        int at = s.IndexOf("\n   at ", StringComparison.Ordinal);
        var head = (at >= 0 ? s.Substring(0, at) : s).Replace("\r", "").Replace("\n", " | ").Trim();
        return head.Length > 300 ? head.Substring(0, 300) + "…" : head;
    }

    // ---- Snapshot-scoped reads: one build per logical operation --------------------

    /// <summary>Capture the CURRENT build as a pinned read view: one logical operation answers every question off ONE build. Pure data, safe to hold for a call.</summary>
    public IndexView Capture() => new(this, _snap);

    // ---- runtime FormIDs: the eight-hex form the game, the console and the logs print --------------------

    public FormKey ParseFormId(string? raw) => Capture().ParseFormId(raw);

    /// <summary>Turn a runtime FormID into the FormKey it names in THIS build, or throw one plain sentence: 0xFE is the light block, 0xFF a dynamic form, anything else a load index.</summary>
    FormKey RuntimeToFormKey(IndexSnapshot s, uint value)
    {
        if (RuntimeFormId.IsDynamic(value))
            throw new FormatException(
                $"{RuntimeFormId.Format(value)} is a dynamic form — the game creates FF...... FormIDs while playing and " +
                "they exist only in a save game, so no plugin defines one.");

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
            EnsureKindKnownUpTo(s, p, value);
            return FormKey.Factory($"{FormIdRange.LocalObjectId(value):X6}:{_names[p]}");
        }

        int idx = (int)RuntimeFormId.LoadIndex(value);
        int fp = idx < slots.FullToPlugin.Length ? slots.FullToPlugin[idx] : -1;
        if (fp < 0)
            throw new FormatException(
                $"{RuntimeFormId.Format(value)} names load index 0x{idx:X2}, which no active plugin occupies " +
                $"(the load order has {slots.FullCount} full plugin(s); light plugins are addressed as FExxxYYY) — " +
                "the plugin may be inactive; read an inactive plugin with 'XXXXXX:Plugin.esp'.");
        EnsureKindKnownUpTo(s, fp, value);
        return FormKey.Factory($"{FormIdRange.LocalObjectId(value):X6}:{_names[fp]}");
    }

    /// <summary>Refuse when a plugin whose kind could not be read sits at or before the slot just resolved; a slot before it answers normally.</summary>
    static void EnsureKindKnownUpTo(IndexSnapshot s, int pluginIndex, uint value)
    {
        if (s.FirstUnknownKind < 0 || pluginIndex < s.FirstUnknownKind) return;
        throw new FormatException(
            $"{RuntimeFormId.Format(value)} cannot be resolved this session: '{s.FirstUnknownKindName}' could not be " +
            "opened, so whether the game loads it as a light plugin — and therefore where everything from it onward " +
            "sits — is unknown; address the record as 'XXXXXX:Plugin.esp' instead.");
    }

    /// <summary>The runtime FormID the game, the console and the logs print for this record; both halves are null when the order gives it no address, and the note half says why.</summary>
    RuntimeAddress RuntimeAddressFor(IndexSnapshot s, FormKey fk)
    {
        if (fk.IsNull) return default;
        var name = fk.ModKey.FileName.ToString();
        if (!_nameToIdx.TryGetValue(name, out int p)) return default;
        if (s.FirstUnknownKind >= 0 && p >= s.FirstUnknownKind) return default;
        int slot = s.Slots.Value.SlotOfPlugin[p];
        if (slot < 0) return default;
        return RuntimeFormId.TryCompose(s.Light[p], slot, fk.ID, out uint value)
            ? new RuntimeAddress(RuntimeFormId.Format(value), null)
            : new RuntimeAddress(null, RuntimeFormId.OutOfWindowNote(name, fk.ID));
    }

    /// <summary>A read view pinned to ONE captured build (see <see cref="Capture"/>): every member answers from the SAME build, while bodies are still fetched from disk.</summary>
    public readonly struct IndexView
    {
        readonly LoadOrderResolver _r;
        readonly IndexSnapshot _s;
        internal IndexView(LoadOrderResolver r, IndexSnapshot s) { _r = r; _s = s; }   // only Capture() constructs

        public int PluginCount => _r._paths.Length;

        /// <summary>Every plugin this build indexed, in load order, MINUS the ones it excluded — the scope a whole-order walk can name plugin by plugin.</summary>
        public IReadOnlyList<string> ScannablePluginNames
        {
            get
            {
                var names = new List<string>(_r._names.Length);
                for (int i = 0; i < _r._names.Length; i++) if (!_s.Excluded.Contains(i)) names.Add(_r._names[i]);
                return names;
            }
        }

        public int RecordCount => _s.Index.Count;               // distinct FormKeys across the order
        public int ConflictCount => _s.Overriders.Count;        // FormKeys overridden by >1 plugin
        public int MaxDepth => _s.MaxDepth;
        public IReadOnlyList<string> LoadFailures => _s.LoadFailures;
        public IReadOnlyDictionary<string, string> ExcludedPlugins => _s.ExcludedPlugins;

        public bool IsUnopenable(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int i) && _s.Unopenable.Contains(i);

        public string Epoch => _s.Epoch;

        public OrderStamp Stamp => _s.Stamp;

        /// <summary>The one FormID door: an eight-hex token with no colon is a RUNTIME FormID resolved against this build's tables, anything else the <c>XXXXXX:Plugin.esp</c> form, a HYBRID refused.</summary>
        public FormKey ParseFormId(string? raw)
            => RuntimeFormId.HybridNote(raw) is { } hybrid ? throw new FormatException(hybrid)
             : RuntimeFormId.TryParse(raw, out uint v) ? _r.RuntimeToFormKey(_s, v)
             : FormKey.Factory((raw ?? "").Trim());

        public RuntimeAddress RuntimeAddressOf(FormKey fk) => _r.RuntimeAddressFor(_s, fk);

        /// <summary>Whether a plugin filename is in the indexed load order — a state the service must name DISTINCTLY from "in the order but defines no such record".</summary>
        public bool ContainsPlugin(string pluginName) => _r._nameToIdx.ContainsKey(pluginName);

        /// <summary>This plugin's position in the priority order — higher loads later and therefore WINS — or -1 when it is not in the order.</summary>
        public int OrderIndexOf(string pluginName) => _r._nameToIdx.TryGetValue(pluginName, out int i) ? i : -1;

        /// <summary>The plugin AT an order index, the inverse of <see cref="OrderIndexOf"/>; null outside the order, and deliberately not <see cref="ScannablePluginNames"/>, whose indices differ.</summary>
        public string? PluginNameAt(int orderIndex)
            => orderIndex >= 0 && orderIndex < _r._names.Length ? _r._names[orderIndex] : null;

        /// <summary>The trailing clause for a refusal naming a plugin <see cref="ContainsPlugin"/> just returned false for; always safe to append, and "" when there is nothing to add.</summary>
        public string AbsenceClause(string pluginName) => _r.AbsenceClause(pluginName);

        public string AbsenceClause(string pluginName, out string? cause) => _r.AbsenceClause(pluginName, out cause);

        /// <summary>Is this plugin LIGHT — ESL-flagged in its header, or a <c>.esl</c> — in THIS build? A refusal about ESL compaction asks the index rather than inferring it from a FormID.</summary>
        public bool IsLightFlagged(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int i) && _s.Light[i];

        /// <summary>Is this plugin in the MASTER BLOCK (a header Master flag, or a .esm/.esl filename)? See <see cref="IndexSnapshot.MasterBlock"/> for why it is not the light flag.</summary>
        public bool IsMasterBlock(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int i) && _s.MasterBlock[i];

        public string? ExplainAbsence(string pluginName) => _r.ExplainAbsence(pluginName);

        public string NameSuggestion(string pluginName) => _r.NameSuggestion(pluginName);

        /// <summary>The on-disk PATH of the active plugin named <paramref name="pluginName"/>, or null — the minimal name→path exposure the dialogue validator's SEQ lint needs.</summary>
        public string? PluginPath(string pluginName)
            => _r._nameToIdx.TryGetValue(pluginName, out int idx) ? _r._paths[idx] : null;

        /// <summary>The real game-Data folder this order resolved; a caller outside this assembly that opens a plugin file itself must pass it to <see cref="LoadOrderResolver.OpenOverlay"/>.</summary>
        public string? DataDir => _r.DataDir;

        public WinnerInfo? ResolveWinner(FormKey fk)
            => _s.Index.TryGetValue(fk, out var e) ? new WinnerInfo(fk, _r._names[e.winner], e.count) : null;

        /// <summary>O(1): the record that CONTAINS this one in THIS build — a DIAL over its INFO, a CELL over its placed references and navmeshes, a WRLD over its cells.</summary>
        public FormKey? ParentOf(FormKey fk) => _s.Containment.ParentOf(fk);

        public int ContainedRecordCount => _s.Containment.Count;

        public IEnumerable<FormKey> RecordKeys() => _s.Index.Keys;

        /// <summary>Build the reverse-reference index if nothing has needed it yet and refresh the partitions whose plugin bytes changed — see <see cref="LoadOrderResolver.EnsureReverseIndex"/>.</summary>
        public ReverseReferenceIndex.Refreshed EnsureReverseIndex() => _r.EnsureReverseIndex(_s);

        public ReverseReferenceIndex? ReverseIndex => _r.ReverseIndex;

        public IEnumerable<FormKey> ConflictKeys() => _s.Overriders.Keys;

        public IReadOnlyList<string>? TouchingPlugins(FormKey fk)
        {
            if (!_s.Index.TryGetValue(fk, out var e)) return null;
            if (e.count == 1) return new[] { _r._names[e.winner] };    // singleton: sole overrider = winner
            var names = _r._names;                                     // local copy — a struct's lambda can't capture 'this'
            return Array.ConvertAll(_s.Overriders[fk], i => names[i]);
        }

        // The resolver's own streams and fetches, each judged against THIS view's build.
        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body)> WinnerRecordsOfType(
            IReadOnlyList<Type> getterTypes, ICollection<PluginUnreadableException>? unreadable = null)
            => _r.WinnerRecordsOfType(getterTypes, _s, unreadable);

        public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
            IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
            => _r.RecordsIn(plugins, getterTypes, _s);

        /// <summary>One record's body from a named plugin, with the excluded-plugin check judged against THIS view's build, so a winner and its body are never vetted by two builds.</summary>
        public IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk, Type? getterType = null)
            => _r.GetRecord(session, pluginName, fk, _s, getterType);

        /// <summary>One record's body from a plugin the index says HOLDS it — the per-record fallback a chunked gather needs; it THROWS the inconsistency rather than answering null.</summary>
        public IMajorRecordGetter FetchRecord(OverlaySession session, string pluginName, FormKey fk, Type? getterType = null)
            => _r.FetchRecord(session, pluginName, fk, _s, getterType);

        public void CollectRecords(OverlaySession session, string pluginName, IReadOnlyCollection<FormKey> wanted,
                                   IReadOnlyList<Type>? getterTypes, IDictionary<FormKey, IMajorRecordGetter> sink)
            => _r.CollectRecords(session, pluginName, wanted, getterTypes, sink, _s);

        /// <summary>The master filenames a plugin DECLARES in its header, in declared order; throws on a name not in the order or excluded this build.</summary>
        public IReadOnlyList<string> DeclaredMasters(string pluginName) => _r.DeclaredMasters(pluginName, _s);

        public ConflictTree? ResolveTree(OverlaySession session, FormKey fk) => _r.ResolveTree(session, fk, _s);

        public IEnumerable<ConflictNode>? StreamTree(OverlaySession session, FormKey fk, bool winnerFirst = false)
            => _r.StreamTree(session, fk, _s, winnerFirst);
    }

    // ---- Queries: single-shot conveniences, each delegating to a fresh Capture() — one call, one build ----

    public WinnerInfo? ResolveWinner(FormKey fk) => Capture().ResolveWinner(fk);

    public IEnumerable<FormKey> ConflictKeys() => Capture().ConflictKeys();

    public IReadOnlyList<string>? TouchingPlugins(FormKey fk) => Capture().TouchingPlugins(fk);

    /// <summary>The full conflict tree: every touching plugin's body, in priority order (winner last), fetched on demand into <paramref name="session"/>. null if the FormKey isn't in the order.</summary>
    public ConflictTree? ResolveTree(OverlaySession session, FormKey fk) => ResolveTree(session, fk, _snap);

    /// <summary>The snapshot-pinned body of <see cref="ResolveTree(OverlaySession, FormKey)"/>, so a caller that already pinned a build reads the tree off the build its epoch stamp names.</summary>
    internal ConflictTree? ResolveTree(OverlaySession session, FormKey fk, IndexSnapshot s)
    {
        if (!s.Index.TryGetValue(fk, out var e)) return null;
        var overlayIdxs = e.count == 1 ? new[] { e.winner } : s.Overriders[fk];
        var nodes = new ConflictNode[overlayIdxs.Length];
        string? recType = null;
        // The FIRST body is fetched blind; every one after it seeks by the type that body turned out to be.
        Type? getterType = null;
        for (int n = 0; n < overlayIdxs.Length; n++)
        {
            int oi = overlayIdxs[n];
            var rec = FetchBody(session, oi, fk, getterType);
            recType ??= RecordNaming.StripOverlay(rec.GetType().Name);
            getterType ??= WriteEngine.SeekTypeFor(rec);
            nodes[n] = new ConflictNode(_names[oi], rec);
        }
        return new ConflictTree(fk, recType ?? "?", nodes);
    }

    /// <summary>The same walk as <see cref="ResolveTree(OverlaySession, FormKey, IndexSnapshot)"/>, yielding one provider's body at a time and holding none, so a tree over a much-touched
    /// record pins one body rather than hundreds of GRUP byte arrays. <paramref name="winnerFirst"/> walks the same providers in reverse.</summary>
    internal IEnumerable<ConflictNode>? StreamTree(OverlaySession session, FormKey fk, IndexSnapshot s, bool winnerFirst = false)
    {
        if (!s.Index.TryGetValue(fk, out var e)) return null;
        return Walk(e.count == 1 ? new[] { e.winner } : s.Overriders[fk]);

        IEnumerable<ConflictNode> Walk(int[] overlayIdxs)
        {
            // The FIRST body is fetched blind; every one after it seeks by that type, the same as the eager walk.
            Type? getterType = null;
            for (int n = 0; n < overlayIdxs.Length; n++)
            {
                int oi = overlayIdxs[winnerFirst ? overlayIdxs.Length - 1 - n : n];
                var rec = FetchBody(session, oi, fk, getterType);
                getterType ??= WriteEngine.SeekTypeFor(rec);
                yield return new ConflictNode(_names[oi], rec);
            }
        }
    }

    /// <summary>Every record in one plugin with its whole-order conflict status (no bodies fetched); opens the plugin for the enumeration and disposes it when the enumeration ends.</summary>
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
                        $"index staleness: '{pluginName}' yields {FormIdToken.Of(fk)} which the current index build does not contain — the plugin changed since the index was built; re-run (the next call's freshness check rebuilds).");
                var touching = e.count == 1 ? new[] { _names[e.winner] } : Array.ConvertAll(s.Overriders[fk], i => _names[i]);
                yield return new RecordStatus(fk, RecordNaming.StripOverlay(rec.GetType().Name),
                                              PluginWins: e.winner == idx, OverrideDepth: e.count, TouchingPlugins: touching);
            }
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Fetch one record's body from a NAMED plugin in the order; null if the plugin isn't in the order or doesn't define this FormKey. Read it before the session disposes.</summary>
    public IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk, Type? getterType = null)
        => GetRecord(session, pluginName, fk, _snap, getterType);          // single-shot: this call = its own build (the IndexView overload pins a whole operation)

    IMajorRecordGetter? GetRecord(OverlaySession session, string pluginName, FormKey fk, IndexSnapshot s, Type? getterType)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx)) return null;
        if (s.Excluded.Contains(idx)) return null;                         // excluded plugin — never re-enumerate it (would re-throw); the service reports the reason via ExcludedPlugins
        return SeekBody(session.Overlay(idx), fk, getterType);
    }

    /// <summary>Find one record in one open overlay: with <paramref name="getterType"/> in hand a TYPED group seek (#354), without one the flat scan. A typed walk that yields nothing falls
    /// back to the flat one, because a type Mutagen does not route would otherwise turn "this plugin has the record" into a silent "it does not".</summary>
    static IMajorRecordGetter? SeekBody(ISkyrimModGetter ov, FormKey fk, Type? getterType)
    {
        System.Threading.Interlocked.Increment(ref BodySeeks);
        if (getterType is not null)
            foreach (var rec in ov.EnumerateMajorRecords(getterType, throwIfUnknown: false))
                if (rec.FormKey == fk) return rec;
        foreach (var rec in ov.EnumerateMajorRecords())
            if (rec.FormKey == fk) return rec;
        return null;
    }

    /// <summary>The master filenames a plugin declares in its header: opens the overlay, reads <c>ModHeader.MasterReferences</c>, disposes. Throws on a name not in the order or excluded this
    /// build, and <see cref="PluginUnreadableException"/> on a plugin that opened at index time but cannot be opened now.</summary>
    public IReadOnlyList<string> DeclaredMasters(string pluginName) => DeclaredMasters(pluginName, _snap);

    IReadOnlyList<string> DeclaredMasters(string pluginName, IndexSnapshot s)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx))
            throw new ArgumentException($"plugin not in the load order: {pluginName}.{AbsenceClause(pluginName)}");
        if (s.Excluded.Contains(idx))
            throw new ArgumentException($"plugin '{pluginName}' was excluded from this session: {s.ExcludedPlugins[pluginName]}");
        ISkyrimModGetter ov;
        // Named as unopenable, exactly as RecordsIn names it: one held-open file must reach both callers as one fault.
        try { ov = OpenOverlay(_paths[idx], _dataDir); }
        catch (Exception ex) { throw new PluginUnreadableException(_names[idx], ex); }
        try { return ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>The bodies ONE plugin holds for a KNOWN set of FormKeys, gathered in a SINGLE enumeration into <paramref name="sink"/> — one walk however many are wanted (#251). A key this
    /// plugin does not hold is simply absent; only the OPEN faults as <see cref="PluginUnreadableException"/>, and a walk fault is left as it came.</summary>
    public void CollectRecords(OverlaySession session, string pluginName, IReadOnlyCollection<FormKey> wanted,
                               IReadOnlyList<Type>? getterTypes, IDictionary<FormKey, IMajorRecordGetter> sink)
        => CollectRecords(session, pluginName, wanted, getterTypes, sink, _snap);

    void CollectRecords(OverlaySession session, string pluginName, IReadOnlyCollection<FormKey> wanted,
                        IReadOnlyList<Type>? getterTypes, IDictionary<FormKey, IMajorRecordGetter> sink, IndexSnapshot s)
    {
        if (wanted.Count == 0) return;
        if (!_nameToIdx.TryGetValue(pluginName, out int idx)) return;
        if (s.Excluded.Contains(idx)) return;                              // excluded plugin — never re-enumerate it (would re-throw)
        ISkyrimModGetter ov;
        // Only the OPEN is named as unopenable; a fault from the walk below is a different problem.
        try { ov = session.Overlay(idx); }
        catch (Exception ex) { throw new PluginUnreadableException(_names[idx], ex); }
        var want = wanted as HashSet<FormKey> ?? new HashSet<FormKey>(wanted);
        int got = 0;
        foreach (var fk in want) if (sink.ContainsKey(fk)) got++;          // already in hand from an earlier call
        if (got == want.Count) return;
        System.Threading.Interlocked.Increment(ref CollectPasses);         // past here the plugin is actually walked
        if (getterTypes is { Count: > 0 })
        {
            foreach (var t in getterTypes)
                foreach (var rec in ov.EnumerateMajorRecords(t, throwIfUnknown: false))
                    if (want.Contains(rec.FormKey) && !sink.ContainsKey(rec.FormKey))
                    {
                        sink[rec.FormKey] = rec;
                        if (++got == want.Count) return;
                    }
            // Fall through per key for whatever is STILL missing: a routed type's hits must not declare an unrouted type's records absent.
        }
        foreach (var rec in ov.EnumerateMajorRecords())
            if (want.Contains(rec.FormKey) && !sink.ContainsKey(rec.FormKey))
            {
                sink[rec.FormKey] = rec;
                if (++got == want.Count) return;
            }
    }

    internal IMajorRecordGetter FetchRecord(OverlaySession session, string pluginName, FormKey fk, IndexSnapshot s, Type? getterType)
    {
        if (!_nameToIdx.TryGetValue(pluginName, out int idx) || s.Excluded.Contains(idx))
            throw new InvalidOperationException(
                $"body-fetch inconsistency: {pluginName} is indexed as containing {FormIdToken.Of(fk)} but is not readable on this build.");
        return FetchBody(session, idx, fk, getterType);
    }

    /// <summary>Fetch one record body from one overlay by re-enumerating it into the session; throws if the overlay cannot yield a FormKey the index says it contains.</summary>
    IMajorRecordGetter FetchBody(OverlaySession session, int overlayIdx, FormKey fk, Type? getterType = null)
    {
        if (SeekBody(session.Overlay(overlayIdx), fk, getterType) is { } rec) return rec;
        throw new InvalidOperationException(
            $"body-fetch inconsistency: {_names[overlayIdx]} is indexed as containing {FormIdToken.Of(fk)} but did not yield it on re-enumeration.");
    }

    // ---- Cross-query scan primitives: one enumeration pass each, the body yielded IN HAND, one plugin handle at a time, nothing held past the yield ----

    /// <summary>Stream every record of the given type(s) whose instance in this overlay IS the load-order winner — the winner body in hand per distinct typed FormKey, types unioned. Pass
    /// <paramref name="unreadable"/> to collect a plugin that opened at index time but not now and carry on; with no collector the stream THROWS.</summary>
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
                // It opened when the index was built, so it became unreadable after that: collected and skipped, or thrown.
                var fault = new PluginUnreadableException(_names[i], ex);
                if (unreadable is null) throw fault;
                unreadable.Add(fault);
                continue;
            }
            try
            {
                // RecordArms is the shared arm re-check: a typed enumeration seeks the GRUP, which for an abstract group holds every arm.
                foreach (var rec in RecordArms.OfTypes(ov, getterTypes))
                    if (s.Index.TryGetValue(rec.FormKey, out var e) && e.winner == i)   // this overlay's instance wins
                        yield return (rec.FormKey, e.count, rec);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Stream every record contained in the given plugins (optionally only of the given type(s)), each with that PLUGIN'S body in hand; a FormKey several scoped plugins touch is
    /// yielded once per plugin and the SERVICE de-dupes. Throws <see cref="PluginUnreadableException"/> on a scoped plugin that cannot be opened now, ending the stream.</summary>
    public IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes)
        => RecordsIn(plugins, getterTypes, _snap);                         // ONE build for the whole scan (captured here, at the call)

    IEnumerable<(FormKey fk, int depth, IMajorRecordGetter body, string source)> RecordsIn(
        IReadOnlyList<string> plugins, IReadOnlyList<Type>? getterTypes, IndexSnapshot s)
    {
        foreach (int i in ScopeIndices(plugins, s))
        {
            ISkyrimModGetter ov;
            // A plugin that opened at build time but not now became unreadable after it; surfaced with the name.
            try { ov = OpenOverlay(_paths[i], _dataDir); }
            catch (Exception ex) { throw new PluginUnreadableException(_names[i], ex); }
            try
            {
                IEnumerable<IMajorRecordGetter> recs = getterTypes is null
                    ? ov.EnumerateMajorRecords()
                    : RecordArms.OfTypes(ov, getterTypes);   // the shared arm re-check
                foreach (var rec in recs)
                    if (s.Index.TryGetValue(rec.FormKey, out var e))
                        yield return (rec.FormKey, e.count, rec, _names[i]);   // _names[i] = this scoped plugin's filename (the source body)
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>Resolve a scope (plugin filenames) to overlay indices; null or empty = the whole order. Throws on a name not in the order, naming it, off the caller's captured snapshot.</summary>
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

    /// <summary>Re-stat the plugin files and rebuild the index if any last-write OR length differs from the baseline, then return true. A changed plugin SET is a new order; the caller re-Builds.</summary>
    public bool RefreshIfStale()
    {
        bool stale = false;
        for (int i = 0; i < _paths.Length; i++)
            if (FileStamp.Of(_paths[i]) != _stamps[i]) { stale = true; break; }
        if (!stale) return false;

        for (int i = 0; i < _paths.Length; i++) _stamps[i] = FileStamp.Of(_paths[i]);
        _snap = BuildIndex();                                              // ONE reference write — in-flight readers keep their captured build
        return true;
    }

    // ---- The reverse-reference index: lazy, per-plugin, beside the snapshot ----------------

    /// <summary>Build the reverse-reference index on first need and refresh its per-plugin partitions. Held on the RESOLVER, so a snapshot swap keeps every partition whose (path, mtime) is unchanged.</summary>
    internal ReverseReferenceIndex.Refreshed EnsureReverseIndex(IndexSnapshot s)
    {
        lock (_reverseGate)
        {
            _reverse ??= new ReverseReferenceIndex();
            return _reverse.Refresh(_names, _paths, i => OpenOverlay(_paths[i], _dataDir), s.Excluded);
        }
    }

    internal ReverseReferenceIndex? ReverseIndex { get { lock (_reverseGate) return _reverse; } }

    /// <summary>Take over the reverse index of the resolver this one replaces; partitions are keyed on (path, mtime), so the next refresh builds only the plugins this order added.</summary>
    public void AdoptReverseIndexFrom(LoadOrderResolver previous)
    {
        var carried = previous.ReverseIndex;
        if (carried is null) return;
        lock (_reverseGate) _reverse ??= carried;
    }

    public void Dispose() { }
}

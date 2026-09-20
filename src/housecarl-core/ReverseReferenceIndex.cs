using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The reverse edge, whole-order: a target FormKey to the records that carry a FormLink to it; contracts in docs/architecture/select-and-walk.md.</summary>
public sealed class ReverseReferenceIndex
{
    sealed class Partition
    {
        public string Path = "";
        public string Name = "";
        public DateTime Mtime;
        public Dictionary<ulong, ulong[]> ByTarget = new();
        public long Pairs;
        public int Unscannable;                        // records whose own link walk threw — excluded and counted
        public int Lenient;                            // records whose links were read leniently — indexed, with a named gap
        public string? Unreadable;                     // set ⇒ this plugin contributed nothing, and why
    }

    /// <summary>One published, immutable state of the index; a read takes the field once and is consistent for its whole run.</summary>
    sealed class Generation
    {
        // Keyed on the plugin PATH, not its filename: two entries can share a filename, and a filename key would drop the lower-priority copy's edges.
        public Dictionary<string, Partition> ByPath = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Order = new();             // partition paths in priority order — what makes a candidate list deterministic
        public Dictionary<ModKey, int> ModToIdx = new();
        public List<ModKey> IdxToMod = new();
        public string Key = "";

        // Built in the constructor, not on first ask: a '??=' on a shared field is a check-then-assign.
        readonly Lazy<HashSet<ulong>> _referenced;

        public Generation() =>
            _referenced = new Lazy<HashSet<ulong>>(BuildReferenced, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Every target something in the order links, as one set; built on first ask and thrown away with the generation.</summary>
        public HashSet<ulong> Referenced => _referenced.Value;

        HashSet<ulong> BuildReferenced()
        {
            var set = new HashSet<ulong>();
            foreach (var path in Order)
                if (ByPath.TryGetValue(path, out var p))
                    foreach (var t in p.ByTarget.Keys) set.Add(t);
            return set;
        }

        public bool TryPack(FormKey k, out ulong packed)
        {
            if (!ModToIdx.TryGetValue(k.ModKey, out int i)) { packed = 0; return false; }
            packed = ((ulong)(uint)i << 32) | k.ID;
            return true;
        }

        public FormKey Unpack(ulong packed) => new(IdxToMod[(int)(packed >> 32)], (uint)packed);
    }

    volatile Generation _gen = new();

    /// <summary>Plugins with a partition in this index.</summary>
    public int PartitionCount => _gen.ByPath.Count;

    /// <summary>Distinct (target, plugin) slots — one per target a plugin links at all.</summary>
    public int TargetSlotCount => _gen.ByPath.Values.Sum(p => p.ByTarget.Count);

    /// <summary>Distinct (target, referencing record) pairs held.</summary>
    public long PairCount => _gen.ByPath.Values.Sum(p => p.Pairs);

    /// <summary>What the index holds, to the nearest useful order of magnitude. Reported, not enforced — there is no ceiling knob.</summary>
    public long ApproxBytes => PairCount * 8 + (long)TargetSlotCount * 40;

    /// <summary>Does anything in the order link to this record? The whole of the orphan question, in one lookup.</summary>
    public bool HasAnyReferencer(FormKey target)
    {
        var g = _gen;
        return g.TryPack(target, out var pt) && g.Referenced.Contains(pt);
    }

    /// <summary>Which of these records nothing in the order links — the orphan sweep; the generation is taken ONCE for the whole pass.</summary>
    public IReadOnlyList<FormKey> Orphans(IEnumerable<FormKey> candidates)
    {
        var g = _gen;
        var referenced = g.Referenced;
        var outp = new List<FormKey>();
        foreach (var k in candidates)
            if (!g.TryPack(k, out var pt) || !referenced.Contains(pt)) outp.Add(k);
        return outp;
    }

    /// <summary>Every record that links to ANY of these targets, deduped, in load order then plugin-enumeration order; the answer is a CANDIDATE set the caller still judges on the body it means.</summary>
    public IReadOnlyList<FormKey> ReferencersOf(IReadOnlyList<FormKey> targets)
    {
        var g = _gen;
        var packed = new List<ulong>(targets.Count);
        foreach (var t in targets) if (g.TryPack(t, out var pt)) packed.Add(pt);
        var seen = new HashSet<ulong>();
        var outp = new List<FormKey>();
        foreach (var path in g.Order)
        {
            if (!g.ByPath.TryGetValue(path, out var p)) continue;
            foreach (var pt in packed)
                if (p.ByTarget.TryGetValue(pt, out var arr))
                    foreach (var r in arr)
                        if (seen.Add(r)) outp.Add(g.Unpack(r));
        }
        return outp;
    }

    /// <summary>What one refresh did, for the response's in-band accounting.</summary>
    public sealed record Refreshed(int Partitions, int Rebuilt, long ElapsedMs, int TargetSlots, long Pairs,
                                   long ApproxBytes, IReadOnlyList<string> Unreadable, int UnscannableRecords,
                                   string Key, int LenientRecords = 0)
    {
        /// <summary>The one accounting line; the BUILD clause is true only of the call that paid it, the freshness key and the coverage disclosures of every answer.</summary>
        public string Note => NoteFor(orphanSweep: false);

        /// <summary>The same line, told for the lane that asked: a missing plugin's edges make the positive question short and the orphan sweep over-inclusive.</summary>
        public string NoteFor(bool orphanSweep)
        {
            var sb = new StringBuilder("reverse-reference index: ");
            sb.Append(Rebuilt > 0 ? $"built {Rebuilt} plugin partition(s) in {ElapsedMs} ms"
                                  : $"unchanged, {Partitions} plugin partition(s) held");
            sb.Append($" ({Pairs} target→referencer pairs over {TargetSlots} target slots, ~{ApproxBytes / (1024 * 1024)} MB held), ");
            sb.Append($"key={Key} (per plugin, path+mtime — beside the order-wide epoch, not riding it).");
            if (Unreadable.Count > 0)
                sb.Append($" {Unreadable.Count} plugin(s) contributed nothing because the walk could not read them: ")
                  .Append(string.Join(", ", Unreadable))
                  .Append(orphanSweep
                      ? " — the sweep is OVER-inclusive by whatever they reference: a record only they link is listed here as an orphan."
                      : " — the answer is short by whatever they reference.");
            if (UnscannableRecords > 0)
                sb.Append($" {UnscannableRecords} record(s) Mutagen could not parse were excluded from the walk.");
            // Says WHICH records it counts, because a reverse walk prints its own lenient line over a different universe.
            if (LenientRecords > 0)
                sb.Append($" Of the plugin copies walked at build time, {LenientRecords} record(s) were read leniently — part of their content is encoded in a way Mutagen refuses, so their edges are the ones houseCARL could still decode.");
            return sb.ToString();
        }
    }

    /// <summary>Bring every partition up to date against the files on disk, staged in fresh collections and published in one assignment; the opener is the resolver's, so none is held at rest.</summary>
    internal Refreshed Refresh(IReadOnlyList<string> names, IReadOnlyList<string> paths,
                               Func<int, ISkyrimModGetter> open, ICollection<int> excluded)
    {
        var sw = Stopwatch.StartNew();
        var prev = _gen;
        var next = new Generation
        {
            // The mod-key interning carries forward, so a partition retained from the previous generation keeps meaning what it meant.
            IdxToMod = new List<ModKey>(prev.IdxToMod),
            ModToIdx = new Dictionary<ModKey, int>(prev.ModToIdx),
        };
        int rebuilt = 0;
        var unreadable = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            if (excluded.Contains(i))
            {
                // Excluded from the snapshot's own index, so the answer is short by whatever it references — said on every answer.
                unreadable.Add(names[i]);
                continue;
            }
            var path = paths[i];
            if (next.ByPath.ContainsKey(path)) continue;           // the same file twice in the order is one partition
            next.Order.Add(path);
            var mtime = SafeMtime(path);
            if (prev.ByPath.TryGetValue(path, out var have) && have.Mtime == mtime)
            {
                next.ByPath[path] = have;                          // this plugin's bytes are the ones it was built from
                if (have.Unreadable is not null) unreadable.Add(have.Name);
                continue;
            }
            int pos = i;
            var built = BuildPartition(path, names[i], mtime, () => open(pos), next);
            next.ByPath[path] = built;
            rebuilt++;
            if (built.Unreadable is not null) unreadable.Add(built.Name);
        }
        next.Key = FreshnessKey(next);
        _gen = next;                                               // one assignment: the generation a reader sees is whole
        sw.Stop();
        return new Refreshed(next.ByPath.Count, rebuilt, sw.ElapsedMilliseconds,
                             next.ByPath.Values.Sum(p => p.ByTarget.Count), next.ByPath.Values.Sum(p => p.Pairs),
                             ApproxBytes, unreadable, next.ByPath.Values.Sum(p => p.Unscannable), next.Key,
                             next.ByPath.Values.Sum(p => p.Lenient));
    }

    /// <summary>One plugin's reverse edges, staged whole and committed only if the enumeration finished.</summary>
    Partition BuildPartition(string path, string name, DateTime mtime, Func<ISkyrimModGetter> open, Generation into)
    {
        var part = new Partition { Path = path, Name = name, Mtime = mtime };
        ISkyrimModGetter ov;
        try { ov = open(); }
        catch (Exception ex) { part.Unreadable = ex.GetType().Name; return part; }
        var acc = new Dictionary<ulong, List<ulong>>();
        int unscannable = 0, lenient = 0;
        var edges = new EdgeVisitor(acc, into);
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                try
                {
                    // A deleted record's content is not live, so none of its links is a real reference.
                    if (DeletedRecordRule.HasNoLiveBody(rec)) continue;
                    if (rec is not IFormLinkContainerGetter) continue;
                    // The SAME link walk the scan lanes make, so a record missing here is missing from every lane downstream (#301).
                    edges.Source = Pack(into, rec.FormKey);
                    if (RecordLinks.Walk(rec, ref edges) is not null) lenient++;
                }
                catch { unscannable++; }
            }
        }
        catch (Exception ex)
        {
            // Non-resumable: the enumeration itself died, so what was staged is a fragment. Drop it and say so.
            part.Unreadable = ex.GetType().Name;
            return part;
        }
        finally { (ov as IDisposable)?.Dispose(); }
        part.Unscannable = unscannable;
        part.Lenient = lenient;
        part.ByTarget = new Dictionary<ulong, ulong[]>(acc.Count);
        long pairs = 0;
        foreach (var (k, v) in acc) { part.ByTarget[k] = v.ToArray(); pairs += v.Count; }
        part.Pairs = pairs;
        return part;
    }

    /// <summary>One record's reverse edges, staged into the partition's accumulator; a struct so the link walk allocates neither a closure nor a delegate per record.</summary>
    struct EdgeVisitor : RecordLinks.IVisitor
    {
        readonly Dictionary<ulong, List<ulong>> _acc;
        readonly Generation _gen;
        public ulong Source;

        public EdgeVisitor(Dictionary<ulong, List<ulong>> acc, Generation gen)
        { _acc = acc; _gen = gen; Source = 0; }

        public RecordLinks.Step Link(FormKey target)
        {
            if (target.IsNull) return RecordLinks.Step.Continue;
            ulong pt = Pack(_gen, target);
            if (!_acc.TryGetValue(pt, out var list)) _acc[pt] = list = new List<ulong>(1);
            // One record's links arrive together, so a repeat is the tail of this list — deduped without a per-target set.
            if (list.Count == 0 || list[^1] != Source) list.Add(Source);
            return RecordLinks.Step.Continue;
        }
    }

    /// <summary>The index's own freshness key: a digest over every partition's (plugin, mtime), distinct from the order-wide epoch by construction.</summary>
    static string FreshnessKey(Generation g)
    {
        var sb = new StringBuilder();
        foreach (var path in g.Order)
            if (g.ByPath.TryGetValue(path, out var p))
                sb.Append(p.Path).Append('|').Append(p.Mtime.Ticks).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    // Interning happens only on the generation being built, which no reader can see yet.
    static ulong Pack(Generation g, FormKey k)
    {
        if (!g.ModToIdx.TryGetValue(k.ModKey, out int i))
        {
            g.ModToIdx[k.ModKey] = i = g.IdxToMod.Count;
            g.IdxToMod.Add(k.ModKey);
        }
        return ((ulong)(uint)i << 32) | k.ID;
    }
}

/// <summary>What an UNBOUNDED reverse selection scans: the index gives a candidate set, and the scan that follows is the existing one.</summary>
public static class ReverseSelection
{
    /// <summary>The scan universe for an unbounded reverse selection: the referencers of the positive targets, or — with none — the ORPHAN sweep.</summary>
    public static IReadOnlyList<FormKey> Universe(LoadOrderResolver.IndexView view, ReverseReferenceIndex index,
                                                  IReadOnlyList<FormKey>? references)
    {
        if (references is { Count: > 0 }) return index.ReferencersOf(references);
        return index.Orphans(view.RecordKeys());
    }

    /// <summary>One hop of a transitive reverse walk, first arrival wins; an EMPTY hop is kept as a fact, and <paramref name="Cut"/> says the node budget ended this hop.</summary>
    public sealed record Hop(int Depth, IReadOnlyList<FormKey> Reached, bool Cut);

    /// <summary>The most candidates a <c>prepare</c> block covers when the budget leaves room for them.</summary>
    const int PrepareBlock = 2000;

    /// <summary>The fewest a block covers however little budget is left, so a drop-heavy tail cannot fall back to a gather per candidate.</summary>
    const int MinPrepareBlock = 256;

    /// <summary>The transitive reverse walk: who references the seeds, then who references those, hop after hop; the budget, verify and prepare contracts are in docs/architecture/select-and-walk.md.</summary>
    public static IReadOnlyList<Hop> Transitive(ReverseReferenceIndex index, IReadOnlyList<FormKey> seeds,
                                                int depth, int maxNodes,
                                                Func<FormKey, IReadOnlySet<FormKey>, bool>? verify, out bool capped,
                                                Action<IReadOnlyList<FormKey>>? prepare = null)
    {
        capped = false;
        var hops = new List<Hop>();
        var visited = new HashSet<FormKey>(seeds);
        IReadOnlyList<FormKey> frontier = seeds;
        int reached = 0;
        for (int d = 1; d <= depth; d++)
        {
            var frontierSet = new HashSet<FormKey>(frontier);
            var next = new List<FormKey>();
            bool cut = false;
            var candidates = index.ReferencersOf(frontier);
            int prepared = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var k = candidates[i];
                if (visited.Contains(k)) continue;
                // The budget is spent before the candidate is verified, so a spent budget stops the body reads too.
                if (reached >= maxNodes) { capped = true; cut = true; break; }
                if (prepare is not null && i >= prepared)
                {
                    // The block follows the budget still left, floored and capped.
                    int span = Math.Clamp(maxNodes - reached, MinPrepareBlock, PrepareBlock);
                    int end = Math.Min(candidates.Count, i + span);
                    var block = new List<FormKey>(end - i);
                    for (int j = i; j < end; j++) if (!visited.Contains(candidates[j])) block.Add(candidates[j]);
                    prepare(block);
                    prepared = end;
                }
                if (verify is not null && !verify(k, frontierSet)) continue;
                visited.Add(k);
                next.Add(k);
                reached++;
            }
            hops.Add(new Hop(d, next, cut));
            if (capped || next.Count == 0) break;
            frontier = next;
        }
        return hops;
    }

    /// <summary>The sentence a caller gets for the negated-only unbounded form: its universe is the orphan set. Declared, never discovered.</summary>
    public static string? UniverseNote(IReadOnlyList<FormKey>? references, int universe)
        => references is { Count: > 0 } ? null
            : $"a negated references= with no types=/plugins= scope is the ORPHAN sweep: its universe is the "
              + $"{universe} record(s) nothing in the order references, and the named target(s) then exclude any of "
              + "those that link them. A bounded negated references= asks the narrower question instead — records "
              + "in that scope that do not link the target — so add types= or plugins= if that is what you meant.";
}

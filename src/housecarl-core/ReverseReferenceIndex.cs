using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The reverse edge, whole-order: a target FormKey → the records that carry a FormLink to it. It is what makes
/// "who references X" answerable without a bounding <c>types=</c> / <c>plugins=</c> scope — the forward direction
/// resolves one link, the reverse direction has to have been walked already.
///
/// <para><b>Lazy.</b> Nothing builds it at startup. The first call that needs it pays the whole-order link-walk —
/// the same walk the dangling sweep already runs — and the cost is reported in that response's accounting rather
/// than discovered.</para>
///
/// <para><b>Partitioned by plugin, keyed on (path, mtime).</b> This is the one place the resolver's existing
/// freshness machinery does not fit: <c>RefreshIfStale</c> is all-or-nothing and <c>Epoch</c> is order-wide, so an
/// index hung off the snapshot would die wholesale on every MO2 touch and pay the full walk again. The index is
/// held BESIDE the snapshot and carried across a snapshot swap AND across a resolver swap; a refresh drops and
/// rebuilds only the partitions whose own key changed. Stale-and-silent is what the partition key makes
/// structurally impossible.</para>
///
/// <para><b>Generational, so a read is never torn.</b> A refresh builds whole new collections and publishes them
/// with one field assignment; a reader takes the current generation once and works off it. No reader ever sees a
/// half-rebuilt order, and no reader takes the build lock.</para>
///
/// <para><b>Plugin-atomic.</b> A plugin whose enumeration throws part-way contributes NO partition content and is
/// named in the report, so a half-read plugin never leaves partial reverse edges behind and a caller can say the
/// answer is short rather than call it complete.</para>
///
/// <para><b>Packed.</b> Entries are ulong ((interned mod index &lt;&lt; 32) | FormID), not FormKey: 8 bytes a pair
/// instead of 24. Pure derived data — no bodies, no file handles at rest. It is resident until the resolver it
/// hangs off is dropped: there is no eviction policy and no ceiling knob, which is the accepted ruling.</para>
///
/// <para><b>What it is not.</b> Not the position-seek index (a different question), and not reverse over the
/// runtime layers (SkyPatcher, SPID): plugin FormLinks only, like the sweep it rides.</para></summary>
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
        public string? Unreadable;                     // set ⇒ this plugin contributed nothing, and why
    }

    /// <summary>One published, immutable state of the index. Everything a read touches lives here, so a read takes
    /// the field once and is consistent for its whole run even while a refresh builds the next one.</summary>
    sealed class Generation
    {
        // Keyed on the plugin PATH, not its filename: the resolver's order is addressed by position and tolerates
        // two entries sharing a filename (last copy wins = priority), so a filename key would silently drop the
        // lower-priority copy's edges and re-walk both plugins on every call.
        public Dictionary<string, Partition> ByPath = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Order = new();             // partition paths in priority order — what makes a candidate list deterministic
        public Dictionary<ModKey, int> ModToIdx = new();
        public List<ModKey> IdxToMod = new();
        public string Key = "";

        // Built in the constructor, not on first ask: a `??=` on a shared field is a check-then-assign, so two
        // concurrent sweeps could each construct one and each hold a ~1.6M-entry set at once.
        readonly Lazy<HashSet<ulong>> _referenced;

        public Generation() =>
            _referenced = new Lazy<HashSet<ulong>>(BuildReferenced, LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>Every target something in the order links, as one set — the orphan question answered in a
        /// lookup rather than a scan over every partition. Built on first ask and thrown away with the
        /// generation.</summary>
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

    /// <summary>What the index holds, to the nearest useful order of magnitude: eight bytes a pair plus the
    /// per-target slot overhead of the dictionaries. Reported, not enforced — there is no ceiling knob.</summary>
    public long ApproxBytes => PairCount * 8 + (long)TargetSlotCount * 40;

    /// <summary>Does anything in the order link to this record? The whole of the orphan question, in one lookup.</summary>
    public bool HasAnyReferencer(FormKey target)
    {
        var g = _gen;
        return g.TryPack(target, out var pt) && g.Referenced.Contains(pt);
    }

    /// <summary>Which of these records nothing in the order links — the orphan sweep. The generation is taken ONCE
    /// for the whole pass: asked key by key, a refresh landing mid-sweep would judge the early keys against the old
    /// edges and the late ones against the new, and the freshness key the response cites would name neither
    /// answer. It also keeps the memoised referenced-set for the whole run instead of rebuilding it at the
    /// crossing.</summary>
    public IReadOnlyList<FormKey> Orphans(IEnumerable<FormKey> candidates)
    {
        var g = _gen;
        var referenced = g.Referenced;
        var outp = new List<FormKey>();
        foreach (var k in candidates)
            if (!g.TryPack(k, out var pt) || !referenced.Contains(pt)) outp.Add(k);
        return outp;
    }

    /// <summary>Every record that links to ANY of these targets, deduped, in load order then plugin-enumeration
    /// order — deterministic for an unchanged order, which is what lets a caller's offset/limit windows tile. The
    /// answer is a CANDIDATE set: the index says some plugin's copy of the record carries the link, and the caller
    /// still decides on the body it means to judge (a later override may have dropped it).</summary>
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

    /// <summary>What one refresh did, for the response's in-band accounting: the build cost when this call is the
    /// one that paid it, the per-plugin freshness key, and every plugin the walk could not read.</summary>
    public sealed record Refreshed(int Partitions, int Rebuilt, long ElapsedMs, int TargetSlots, long Pairs,
                                   long ApproxBytes, IReadOnlyList<string> Unreadable, int UnscannableRecords,
                                   string Key)
    {
        /// <summary>The one accounting line, for the question that asks who references a target. The BUILD clause
        /// is only true of the call that paid it; the freshness key and the coverage disclosures are true of every
        /// answer the index serves, so they are unconditional — a cached call that dropped them would read as
        /// complete when it is short.</summary>
        public string Note => NoteFor(orphanSweep: false);

        /// <summary>The same line, told for the lane that asked. A missing plugin's edges cut BOTH ways and the
        /// two readings are opposites: the positive question loses referencers, so its answer is short; the orphan
        /// sweep loses the very edges that would disqualify an orphan, so its answer is over-inclusive — a record
        /// only the unreadable plugin links is listed as referenced by nothing. Saying "short" there would tell a
        /// caller the confirmed orphans are confirmed.</summary>
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
            return sb.ToString();
        }
    }

    /// <summary>Bring every partition up to date against the files on disk: keep the ones whose (path, mtime) is
    /// unchanged, rebuild the ones whose is not, drop the ones whose plugin left the order. The whole result is
    /// staged in fresh collections and published in one assignment, so a concurrent read sees the old generation
    /// or the new one and never a mixture. The opener is the resolver's, so one plugin is open at a time and none
    /// is held at rest.</summary>
    internal Refreshed Refresh(IReadOnlyList<string> names, IReadOnlyList<string> paths,
                               Func<int, ISkyrimModGetter> open, ICollection<int> excluded)
    {
        var sw = Stopwatch.StartNew();
        var prev = _gen;
        var next = new Generation
        {
            // The mod-key interning carries forward, so a partition retained from the previous generation keeps
            // meaning what it meant: an index into this list, which only ever grows at the tail.
            IdxToMod = new List<ModKey>(prev.IdxToMod),
            ModToIdx = new Dictionary<ModKey, int>(prev.ModToIdx),
        };
        int rebuilt = 0;
        var unreadable = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            if (excluded.Contains(i))
            {
                // Excluded from the snapshot's own index because it could not be opened or parsed. The reverse
                // walk cannot see it either, so the answer is short by whatever it references — said out loud on
                // every answer, not only on the call that discovered it.
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
                             ApproxBytes, unreadable, next.ByPath.Values.Sum(p => p.Unscannable), next.Key);
    }

    /// <summary>One plugin's reverse edges, staged whole and committed only if the enumeration finished.</summary>
    Partition BuildPartition(string path, string name, DateTime mtime, Func<ISkyrimModGetter> open, Generation into)
    {
        var part = new Partition { Path = path, Name = name, Mtime = mtime };
        ISkyrimModGetter ov;
        try { ov = open(); }
        catch (Exception ex) { part.Unreadable = ex.GetType().Name; return part; }
        var acc = new Dictionary<ulong, List<ulong>>();
        int unscannable = 0;
        try
        {
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                try
                {
                    // A deleted record's content is not live, so none of its links is a real reference — the same
                    // exclusion the dangling sweep makes, before the walk that would throw on such a body.
                    if (DeletedRecordRule.HasNoLiveBody(rec)) continue;
                    if (rec is not IFormLinkContainerGetter flc) continue;
                    ulong src = Pack(into, rec.FormKey);
                    foreach (var link in flc.EnumerateFormLinks())
                    {
                        var target = link.FormKey;
                        if (target.IsNull) continue;
                        ulong pt = Pack(into, target);
                        if (!acc.TryGetValue(pt, out var list)) acc[pt] = list = new List<ulong>(1);
                        // One record's links arrive together, so the same record linking a target twice is the
                        // tail of this list — deduped without a per-target set.
                        if (list.Count == 0 || list[^1] != src) list.Add(src);
                    }
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
        part.ByTarget = new Dictionary<ulong, ulong[]>(acc.Count);
        long pairs = 0;
        foreach (var (k, v) in acc) { part.ByTarget[k] = v.ToArray(); pairs += v.Count; }
        part.Pairs = pairs;
        return part;
    }

    /// <summary>The index's own freshness key: a digest over every partition's (plugin, mtime). Distinct from the
    /// order-wide epoch by construction — it names which plugin BODIES the reverse edges were computed from.</summary>
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

/// <summary>What an UNBOUNDED reverse selection scans. The index turns "who references X" from a refusal into a
/// candidate set; the scan that follows is the existing one, so the verdict still comes from the body the caller
/// means to judge and a later override that dropped the link is not counted.</summary>
public static class ReverseSelection
{
    /// <summary>The scan universe for an unbounded reverse selection. With positive targets it is every record
    /// some plugin links to one of them. With none — the negated-only form — it is the ORPHAN sweep: every record
    /// nothing in the order references, which is the unbounded question SPEC §2.2's and §3.2's 2026-09-05
    /// amendments name for this spelling. The named negated targets still apply after it, as the AND term they are
    /// everywhere else.</summary>
    public static IReadOnlyList<FormKey> Universe(LoadOrderResolver.IndexView view, ReverseReferenceIndex index,
                                                  IReadOnlyList<FormKey>? references)
    {
        if (references is { Count: > 0 }) return index.ReferencersOf(references);
        return index.Orphans(view.RecordKeys());
    }

    /// <summary>The sentence a caller gets for the negated-only unbounded form: its universe is the orphan set, not
    /// the whole order and not the same question a bounded negated <c>references=</c> asks. Declared, never
    /// discovered.</summary>
    public static string? UniverseNote(IReadOnlyList<FormKey>? references, int universe)
        => references is { Count: > 0 } ? null
            : $"a negated references= with no types=/plugins= scope is the ORPHAN sweep: its universe is the "
              + $"{universe} record(s) nothing in the order references, and the named target(s) then exclude any of "
              + "those that link them. A bounded negated references= asks the narrower question instead — records "
              + "in that scope that do not link the target — so add types= or plugins= if that is what you meant.";
}

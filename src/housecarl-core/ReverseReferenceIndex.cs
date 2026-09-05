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
/// held BESIDE the snapshot and carried across a snapshot swap; a refresh drops and rebuilds only the partitions
/// whose own key changed. Stale-and-silent is what the partition key makes structurally impossible.</para>
///
/// <para><b>Plugin-atomic.</b> A plugin whose enumeration throws part-way contributes NO partition content and is
/// named in the report, so a half-read plugin never leaves partial reverse edges behind and a caller can say the
/// answer is short rather than call it complete.</para>
///
/// <para><b>Packed.</b> Entries are ulong ((interned mod index &lt;&lt; 32) | FormID), not FormKey: 8 bytes a pair
/// instead of 24. Pure derived data — no bodies, no file handles at rest.</para>
///
/// <para><b>What it is not.</b> Not the position-seek index (a different question), and not reverse over the
/// runtime layers (SkyPatcher, SPID): plugin FormLinks only, like the sweep it rides.</para></summary>
public sealed class ReverseReferenceIndex
{
    sealed class Partition
    {
        public string Path = "";
        public DateTime Mtime;
        public Dictionary<ulong, ulong[]> ByTarget = new();
        public long Pairs;
        public int Unscannable;                        // records whose own link walk threw — excluded and counted
        public string? Unreadable;                     // set ⇒ this plugin contributed nothing, and why
    }

    readonly Dictionary<string, Partition> _byPlugin = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _order = new();              // plugin names in priority order — what makes a candidate list deterministic
    readonly Dictionary<ModKey, int> _modToIdx = new();
    readonly List<ModKey> _idxToMod = new();

    /// <summary>Plugins with a partition in this index.</summary>
    public int PartitionCount => _byPlugin.Count;

    /// <summary>Distinct (target, plugin) slots — one per target a plugin links at all.</summary>
    public int TargetSlotCount => _byPlugin.Values.Sum(p => p.ByTarget.Count);

    /// <summary>Distinct (target, referencing record) pairs held.</summary>
    public long PairCount => _byPlugin.Values.Sum(p => p.Pairs);

    /// <summary>What the index holds, to the nearest useful order of magnitude: eight bytes a pair plus the
    /// per-target slot overhead of the dictionaries. Reported, not enforced — there is no ceiling knob.</summary>
    public long ApproxBytes => PairCount * 8 + (long)TargetSlotCount * 40;

    /// <summary>Does anything in the order link to this record? The whole of the orphan question.</summary>
    public bool HasAnyReferencer(FormKey target)
    {
        if (!TryPack(target, out var pt)) return false;
        foreach (var name in _order)
            if (_byPlugin.TryGetValue(name, out var p) && p.ByTarget.ContainsKey(pt)) return true;
        return false;
    }

    /// <summary>Every record that links to ANY of these targets, deduped, in load order then plugin-enumeration
    /// order — deterministic for an unchanged order, which is what lets a caller's offset/limit windows tile. The
    /// answer is a CANDIDATE set: the index says some plugin's copy of the record carries the link, and the caller
    /// still decides on the body it means to judge (a later override may have dropped it).</summary>
    public IReadOnlyList<FormKey> ReferencersOf(IReadOnlyList<FormKey> targets)
    {
        var packed = new List<ulong>(targets.Count);
        foreach (var t in targets) if (TryPack(t, out var pt)) packed.Add(pt);
        var seen = new HashSet<ulong>();
        var outp = new List<FormKey>();
        foreach (var name in _order)
        {
            if (!_byPlugin.TryGetValue(name, out var p)) continue;
            foreach (var pt in packed)
                if (p.ByTarget.TryGetValue(pt, out var arr))
                    foreach (var r in arr)
                        if (seen.Add(r)) outp.Add(Unpack(r));
        }
        return outp;
    }

    /// <summary>What one refresh did, for the response's in-band accounting: the build cost when this call is the
    /// one that paid it, the per-plugin freshness key, and every plugin the walk could not read.</summary>
    public sealed record Refreshed(int Partitions, int Rebuilt, long ElapsedMs, int TargetSlots, long Pairs,
                                   long ApproxBytes, IReadOnlyList<string> Unreadable, int UnscannableRecords,
                                   string Key)
    {
        /// <summary>The one accounting line, or null when this call changed nothing and so paid nothing.</summary>
        public string? Note => Rebuilt == 0 ? null
            : $"reverse-reference index: built {Rebuilt} plugin partition(s) in {ElapsedMs} ms "
              + $"({Pairs} target→referencer pairs over {TargetSlots} target slots, ~{ApproxBytes / (1024 * 1024)} MB held), "
              + $"key={Key} (per plugin, path+mtime — beside the order-wide epoch, not riding it)."
              + (Unreadable.Count > 0
                     ? $" {Unreadable.Count} plugin(s) contributed nothing because the walk could not read them: {string.Join(", ", Unreadable)} — the answer is short by whatever they reference."
                     : "")
              + (UnscannableRecords > 0
                     ? $" {UnscannableRecords} record(s) Mutagen could not parse were excluded from the walk."
                     : "");
    }

    /// <summary>Bring every partition up to date against the files on disk: keep the ones whose (path, mtime) is
    /// unchanged, rebuild the ones whose is not, drop the ones whose plugin left the order. The opener is the
    /// resolver's, so one plugin is open at a time and none is held at rest.</summary>
    internal Refreshed Refresh(IReadOnlyList<string> names, IReadOnlyList<string> paths,
                               Func<int, ISkyrimModGetter> open, ICollection<int> excluded)
    {
        var sw = Stopwatch.StartNew();
        int rebuilt = 0;
        var unreadable = new List<string>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _order.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            if (excluded.Contains(i)) continue;                    // excluded from the index build; excluded here too
            var name = names[i];
            live.Add(name);
            _order.Add(name);
            var mtime = SafeMtime(paths[i]);
            if (_byPlugin.TryGetValue(name, out var have) && have.Path == paths[i] && have.Mtime == mtime)
            {
                if (have.Unreadable is not null) unreadable.Add(name);
                continue;                                          // this plugin's bytes are the ones it was built from
            }
            var built = BuildPartition(paths[i], mtime, () => open(i));
            _byPlugin[name] = built;
            rebuilt++;
            if (built.Unreadable is not null) unreadable.Add(name);
        }
        foreach (var gone in _byPlugin.Keys.Where(k => !live.Contains(k)).ToList()) _byPlugin.Remove(gone);
        sw.Stop();
        return new Refreshed(_byPlugin.Count, rebuilt, sw.ElapsedMilliseconds, TargetSlotCount, PairCount,
                             ApproxBytes, unreadable, _byPlugin.Values.Sum(p => p.Unscannable), FreshnessKey());
    }

    /// <summary>One plugin's reverse edges, staged whole and committed only if the enumeration finished.</summary>
    Partition BuildPartition(string path, DateTime mtime, Func<ISkyrimModGetter> open)
    {
        var part = new Partition { Path = path, Mtime = mtime };
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
                    ulong src = Pack(rec.FormKey);
                    foreach (var link in flc.EnumerateFormLinks())
                    {
                        var target = link.FormKey;
                        if (target.IsNull) continue;
                        ulong pt = Pack(target);
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
    string FreshnessKey()
    {
        var sb = new StringBuilder();
        foreach (var name in _order)
            if (_byPlugin.TryGetValue(name, out var p))
                sb.Append(name).Append('|').Append(p.Mtime.Ticks).Append('\n');
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    ulong Pack(FormKey k)
    {
        if (!_modToIdx.TryGetValue(k.ModKey, out int i))
        {
            _modToIdx[k.ModKey] = i = _idxToMod.Count;
            _idxToMod.Add(k.ModKey);
        }
        return ((ulong)(uint)i << 32) | k.ID;
    }

    bool TryPack(FormKey k, out ulong packed)
    {
        if (!_modToIdx.TryGetValue(k.ModKey, out int i)) { packed = 0; return false; }
        packed = ((ulong)(uint)i << 32) | k.ID;
        return true;
    }

    FormKey Unpack(ulong packed) => new(_idxToMod[(int)(packed >> 32)], (uint)packed);
}

/// <summary>What an UNBOUNDED reverse selection scans. The index turns "who references X" from a refusal into a
/// candidate set; the scan that follows is the existing one, so the verdict still comes from the body the caller
/// means to judge and a later override that dropped the link is not counted.</summary>
public static class ReverseSelection
{
    /// <summary>Which question a NEGATED, unbounded <c>references=</c> asks. Two readings are live and Aaron has
    /// not ruled between them: <c>false</c> is what the bounded term ships today — "does not reference X", whose
    /// unbounded universe is therefore every record in the order — and <c>true</c> is the orphan sweep, "referenced
    /// by nothing anywhere", which the index answers directly and for which the named target is advisory. The index
    /// carries both; this constant is the switch.</summary>
    public const bool NegatedMeansUnreferencedByAnything = false;

    /// <summary>The scan universe for an unbounded reverse selection. With targets it is every record some plugin
    /// links to one of them; with none — the negated-only form — it is the whole order under the shipped reading,
    /// or only the unreferenced records under the other one.</summary>
    public static IReadOnlyList<FormKey> Universe(LoadOrderResolver.IndexView view, ReverseReferenceIndex index,
                                                  IReadOnlyList<FormKey>? references)
    {
        if (references is { Count: > 0 }) return index.ReferencersOf(references);
        return NegatedMeansUnreferencedByAnything
            ? view.RecordKeys().Where(k => !index.HasAnyReferencer(k)).ToList()
            : view.RecordKeys().ToList();
    }

    /// <summary>The sentence a caller gets when the unbounded reverse universe is the whole order: it is the
    /// shipped reading's honest cost, declared rather than discovered.</summary>
    public static string? WholeOrderNote(IReadOnlyList<FormKey>? references, int universe)
        => references is { Count: > 0 } || NegatedMeansUnreferencedByAnything ? null
            : $"a negated references= with no types=/plugins= scope asks which records do NOT link the target, so "
              + $"its universe is the whole order — {universe} records, each winner body read. Add types= or "
              + "plugins= if you meant a narrower question.";
}

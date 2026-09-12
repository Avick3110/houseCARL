using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a <c>*parent</c> predicate COSTS per candidate, on the predicate set's own counters
/// (<see cref="FieldPredicateSet.ParentBodiesHeld"/>, <c>ParentBodyHighWater</c>, <c>ParentBodyFetches</c>).
/// Whether the hop released a containing record's body or kept one per distinct parent for the whole call is
/// invisible in the answer and visible only in memory — a Mutagen getter is a slice over the whole GRUP it was read
/// from and pins that array — which is the argument these counters are added on, the same one
/// <c>LoadOrderResolver.BodySeeks</c> and the walk's high-water counters were added on. #720: ~0.8 MB held per
/// parent evaluated took a 32 GB machine down over a REFR-sized scope.
///
/// <para>The verdicts themselves are covered in <see cref="WhereContainmentTests"/>; these tests re-check them only
/// where the memo that replaced the body cache could carry one verdict to the wrong question.</para></summary>
[Trait("tier", "unit")]
public sealed class WhereContainmentCostTests
{
    readonly SkyrimMod _mod = new(new ModKey("ParentCost", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly Dictionary<FormKey, FormKey> _parents = new();
    readonly Dictionary<FormKey, IMajorRecordGetter> _bodies = new();
    int _fetches;

    Cell NewCell(string eid)
    {
        var c = new Cell(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = eid };
        _bodies[c.FormKey] = c;
        return c;
    }

    PlacedObject NewPlaced(string eid, IMajorRecordGetter parent)
    {
        var r = new PlacedObject(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = eid };
        _bodies[r.FormKey] = r;
        _parents[r.FormKey] = parent.FormKey;
        return r;
    }

    /// <summary>Parse the clauses, bind them to the hand-built map, and run them over every candidate in order —
    /// handing back the set (for its counters) and how many candidates matched.</summary>
    (FieldPredicateSet Set, int Matched) Scan(string[] clauses, IEnumerable<IMajorRecordGetter> candidates)
    {
        var (set, err) = FieldPredicateSet.Parse(clauses);
        Assert.Null(err);
        set!.BindResolution(fk => _bodies.ContainsKey(fk) ? "ParentCost.esm" : null,
                            fk => { _fetches++; return _bodies.GetValueOrDefault(fk); },
                            fk => _parents.TryGetValue(fk, out var p) ? p : null);
        int matched = 0;
        foreach (var c in candidates) if (set.Matches(c)) matched++;
        Assert.Null(set.FatalError);
        return (set, matched);
    }

    /// <summary>The #720 invariant: a candidate's containing record is read, judged and RELEASED — never one
    /// getter per distinct parent held for the length of the call. Fails on the old code, where the hop shared the
    /// link step's target cache and every parent it ever touched was still held when the scan returned.</summary>
    [Fact]
    public void AParentPredicateHoldsNoParentBodyPastTheCandidateThatReadIt()
    {
        var placed = Enumerable.Range(0, 200).Select(i => NewPlaced($"PcRef{i}", NewCell($"PcCell{i}"))).ToList();

        var (set, matched) = Scan(new[] { "*parent.EditorID = PcCell7" }, placed);

        Assert.Equal(1, matched);
        Assert.Equal(0, set.ParentBodiesHeld);      // nothing retained past the candidate that read it
        Assert.Equal(1, set.ParentBodyHighWater);   // and never more than the one being judged
        Assert.Equal(200, set.ParentBodyFetches);   // 200 distinct parents, one body each
    }

    /// <summary>The memo the body cache was replaced by keeps the read-once-per-call guarantee it used to buy:
    /// every child under one containing record decides the same way, so that record is read ONCE however many
    /// children the scan streams under it.</summary>
    [Fact]
    public void AParentPredicateReadsOneBodyPerContainingRecordNotOnePerCandidate()
    {
        var cells = Enumerable.Range(0, 4).Select(i => NewCell($"PcShared{i}")).ToList();
        var placed = Enumerable.Range(0, 200).Select(i => NewPlaced($"PcRef{i}", cells[i % 4])).ToList();

        var (set, matched) = Scan(new[] { "*parent.EditorID = PcShared2" }, placed);

        Assert.Equal(50, matched);
        Assert.Equal(4, set.ParentBodyFetches);
        Assert.Equal(4, _fetches);
        Assert.Equal(0, set.ParentBodiesHeld);
        Assert.Equal(1, set.ParentBodyHighWater);
    }

    /// <summary>A chain reads only the record its TERMS run on: the containment map answers in keys, so the cell in
    /// the middle of <c>*parent.*parent</c> is climbed through without its body ever being fetched.</summary>
    [Fact]
    public void AParentChainReadsOnlyTheRecordItsTermsRunOn()
    {
        var wrld = new Worldspace(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PcTamriel" };
        _bodies[wrld.FormKey] = wrld;
        var placed = new List<IMajorRecordGetter>();
        for (int i = 0; i < 50; i++)
        {
            var cell = NewCell($"PcChainCell{i}");
            _parents[cell.FormKey] = wrld.FormKey;
            placed.Add(NewPlaced($"PcChainRef{i}", cell));
        }

        var (set, matched) = Scan(new[] { "*parent.*parent.EditorID = PcTamriel" }, placed);

        Assert.Equal(50, matched);
        Assert.Equal(1, set.ParentBodyFetches);   // the worldspace, and not one cell body on the way up
        Assert.Equal(1, _fetches);
        Assert.Equal(0, set.ParentBodiesHeld);
        Assert.Equal(1, set.ParentBodyHighWater);
    }

    /// <summary>A verdict belongs to the predicate that reached it: two clauses hopping to the SAME containing
    /// record ask different questions of it, and each gets its own answer.</summary>
    [Fact]
    public void TwoClausesHoppingToOneParentKeepTheirOwnVerdicts()
    {
        var cell = NewCell("PcWhiterun");
        var placed = Enumerable.Range(0, 20).Select(i => (IMajorRecordGetter)NewPlaced($"PcRef{i}", cell)).ToList();

        Assert.Equal(20, Scan(new[] { "*parent.EditorID startswith Pc", "*parent.EditorID = PcWhiterun" }, placed).Matched);

        var (set, matched) = Scan(new[] { "*parent.EditorID startswith Pc", "*parent.EditorID = PcSomewhereElse" }, placed);
        Assert.Equal(0, matched);
        Assert.Equal(1, set.ParentBodyFetches);   // one parent, one read, two verdicts
    }

    /// <summary>Both SIDES of a link predicate can hop, and to the same containing record: the left path's links
    /// are collected on it, while the right side's terms are read on the parent of a TARGET. Those are different
    /// questions and neither may answer the other.</summary>
    [Fact]
    public void AHopOnEachSideOfALinkStepKeepsItsOwnVerdict()
    {
        var topic = new DialogTopic(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PcGreetings" };
        var quest = new Quest(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "PcQuest" };
        topic.Quest.SetTo(quest.FormKey);
        _bodies[topic.FormKey] = topic;
        _bodies[quest.FormKey] = quest;
        var infos = new List<IMajorRecordGetter>();
        for (int i = 0; i < 10; i++)
        {
            var info = new DialogResponses(_mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"PcLine{i}" };
            _bodies[info.FormKey] = info;
            _parents[info.FormKey] = topic.FormKey;
            infos.Add(info);
        }
        _parents[quest.FormKey] = topic.FormKey;   // the topic contains the INFOs AND is the quest's parent here

        // Left hop: the INFO's topic, whose Quest link resolves. Right hop: that quest's own containing record —
        // the same topic — read for the terms below. One record, two verdicts, and only the true one matches.
        Assert.Equal(10, Scan(new[] { "*parent.Quest->*parent.EditorID = PcGreetings" }, infos).Matched);
        Assert.Equal(0, Scan(new[] { "*parent.Quest->*parent.EditorID = PcQuest" }, infos).Matched);
    }
}

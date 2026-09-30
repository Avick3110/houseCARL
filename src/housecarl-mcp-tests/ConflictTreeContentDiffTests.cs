using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The conflict tree's winner-relative content diff: a list that differs in content but not count is a
/// delta, a pure reorder is a note, and absent-versus-present is its own state. Faction subjects in a master, each
/// overridden by one plugin, drive the pinned tree read and <see cref="FieldsDiff.Compare"/>.</summary>
[Trait("tier", "integration")]
public sealed class ConflictTreeContentDiffTests : IClassFixture<ConflictTreeContentDiffTests.World>
{
    /// <summary>Master: four relation targets, a crime FormList, and one subject faction per arm. Override: each
    /// subject reshaped per arm, and the winner.</summary>
    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        public LoadOrderService Svc { get; }
        public FormKey[] Targets { get; } = new FormKey[4];
        public FormKey CrimeList { get; }
        public FormKey Content { get; }
        public FormKey Reorder { get; }
        public FormKey Count { get; }
        public FormKey Scalar { get; }
        public FormKey Absent { get; }
        public FormKey Itm { get; }
        public FormKey WinnerCleared { get; }

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hc-conflict-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var masterPath = Path.Combine(_dir, "hcDiffMaster.esp");
            var overPath = Path.Combine(_dir, "hcDiffOver.esp");

            var master = new SkyrimMod(ModKey.FromNameAndExtension("hcDiffMaster.esp"), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < 4; i++) { var tf = master.Factions.AddNew(); tf.EditorID = $"hcDiffTarget{i + 1}"; Targets[i] = tf.FormKey; }
            var t = Targets;
            var fA = master.Factions.AddNew(); fA.EditorID = "hcDiffContent"; fA.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]), Rel(t[2]) });
            var fB = master.Factions.AddNew(); fB.EditorID = "hcDiffReorder"; fB.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]), Rel(t[2]) });
            var fC = master.Factions.AddNew(); fC.EditorID = "hcDiffCount"; fC.Relations.AddRange(new[] { Rel(t[0]), Rel(t[1]) });
            var fD = master.Factions.AddNew(); fD.EditorID = "hcDiffScalar"; fD.Flags = Faction.FactionFlag.HiddenFromPC;
            var fl = master.FormLists.AddNew(); fl.EditorID = "hcDiffCrimeList"; CrimeList = fl.FormKey;
            var fI = master.Factions.AddNew(); fI.EditorID = "hcDiffAbsent";
            var fJ = master.Factions.AddNew(); fJ.EditorID = "hcDiffITM"; fJ.SharedCrimeFactionList.SetTo(fl.FormKey);
            var fK = master.Factions.AddNew(); fK.EditorID = "hcDiffWinnerCleared"; fK.SharedCrimeFactionList.SetTo(fl.FormKey);
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            (Content, Reorder, Count, Scalar, Absent, Itm, WinnerCleared) = (fA.FormKey, fB.FormKey, fC.FormKey, fD.FormKey, fI.FormKey, fJ.FormKey, fK.FormKey);

            var over = new SkyrimMod(ModKey.FromNameAndExtension("hcDiffOver.esp"), SkyrimRelease.SkyrimSE);
            var oA = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fA);
            oA.Relations.Clear(); oA.Relations.AddRange(new[] { Rel(t[3]), Rel(t[0]), Rel(t[1]) });   // t3 dropped, t4 added
            var oB = (IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fB);
            oB.Relations.Clear(); oB.Relations.AddRange(new[] { Rel(t[2]), Rel(t[0]), Rel(t[1]) });   // same three, reordered
            ((IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fC)).Relations.Add(Rel(t[2]));      // 2 -> 3
            ((IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fD)).Flags = Faction.FactionFlag.SpecialCombat;
            ((IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fI)).SharedCrimeFactionList.SetTo(fl.FormKey);
            ((IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fJ)).SharedCrimeFactionList.SetTo(fl.FormKey);
            ((IFaction)WriteEngine.GenericGetOrAddAsOverride(over, fK)).SharedCrimeFactionList.SetToNull();
            over.BeginWrite.ToPath(overPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            Resolver = LoadOrderResolver.Build(new[] { masterPath, overPath });
            Svc = LoadOrderService.ForGuard(Resolver, new UserConfigStore(Path.Combine(_dir, "houseCARL.user.json")));
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    readonly World _w;
    public ConflictTreeContentDiffTests(World w) => _w = w;

    static Relation Rel(FormKey target)
    {
        var rel = new Relation { Reaction = CombatReaction.Ally };
        rel.Target.SetTo(target);
        return rel;
    }

    /// <summary>The master node against the winner, off the pinned deep tree.</summary>
    FieldsDiff.Result DiffOf(FormKey fk)
    {
        var tree = _w.Svc.ReadArea.ResolveTreePinned(new LoadOrderService.ViewPin(_w.Resolver, _w.Resolver.Capture()), fk, null);
        Assert.NotNull(tree);
        Assert.Equal(2, tree!.Nodes.Count);
        return FieldsDiff.Compare(tree.Nodes[0].Record, tree.Winner.Record);
    }

    static RecordFields Fields(params (string path, string? token, string? note)[] lines) =>
        new("Faction", "000000:hcDiffGuard.esp", "hcDiffCase",
            lines.Select(l => new FieldValue(l.path, l.token is not null, l.token, l.note)).ToList());

    const string Truncated = "(expansion truncated at 2000 lines — narrow with a field path or a lower depth)";

    // Probe A: "equal-count content delta IS reported (the masked case)", naming the master-only and the winner-only element.
    [Fact]
    public void EqualCountDifferentContent_IsADeltaNamingEachSidesOwnElement()
    {
        var d = DiffOf(_w.Content);
        var rel = Assert.Single(d.Deltas, x => x.StartsWith("Relations:", StringComparison.Ordinal));
        Assert.Contains("contents differ", rel);
        Assert.Contains(_w.Targets[2].ToString(), rel);
        Assert.Contains(_w.Targets[3].ToString(), rel);
    }

    // Probe B: "a reordered-only list reports an ORDER-DIFFERS note (#275), not silence and not element deltas".
    [Fact]
    public void ReorderOnly_IsOneOrderDiffersNote()
    {
        var d = DiffOf(_w.Reorder);
        Assert.True(d.Complete);
        Assert.StartsWith("Relations: same 3 item(s), ORDER DIFFERS", Assert.Single(d.Deltas), StringComparison.Ordinal);
    }

    // Probe C: "count delta still reported".
    [Fact]
    public void DifferentCounts_IsACountDelta() =>
        Assert.Contains(DiffOf(_w.Count).Deltas, x => x.StartsWith("Relations: 2 vs winner 3", StringComparison.Ordinal));

    // Probe D: "scalar delta still reported".
    [Fact]
    public void ScalarChange_IsAValueDeltaAgainstTheWinner() =>
        Assert.Contains(DiffOf(_w.Scalar).Deltas, x => x.StartsWith("Flags=", StringComparison.Ordinal) && x.Contains("winner"));

    // Probe I: "an absent nullable formlink renders as a FIRST-CLASS ABSENT state, not a phantom value delta", naming the
    // winner's value, and "AgreedCount counts the restated VALUE leaves but NOT the absent field".
    [Fact]
    public void AbsentNullableLinkOnTheContributor_IsAFirstClassAbsentState()
    {
        var d = DiffOf(_w.Absent);
        Assert.True(d.Complete);
        var absent = Assert.Single(d.Deltas, x => x.StartsWith("SharedCrimeFactionList: ABSENT here (winner has ", StringComparison.Ordinal));
        Assert.Contains(_w.CrimeList.ToString(), absent);
        Assert.DoesNotContain(d.Deltas, x => x.Contains("=(absent)", StringComparison.Ordinal));
        Assert.True(d.AgreedCount > 0);
        Assert.DoesNotContain(d.AgreedSample, x => x.Contains("SharedCrimeFactionList"));
    }

    // Probe J: "a present-==-winner (ITM) override carries NO delta" and "AgreedCount > arm-I".
    [Fact]
    public void RestatedValueEqualToTheWinner_HasNoDeltaAndAgreesOnMoreThanTheAbsentCase()
    {
        var itm = DiffOf(_w.Itm);
        Assert.True(itm.Complete);
        Assert.Empty(itm.Deltas);
        Assert.True(itm.AgreedCount > DiffOf(_w.Absent).AgreedCount);
        Assert.NotEmpty(itm.AgreedSample);
    }

    // Probe K: "a field the winner CLEARED renders as the contributor's value + 'winner has … ABSENT'", never a phantom value delta.
    [Fact]
    public void LinkTheWinnerCleared_RendersTheContributorsValueAndWinnerAbsent()
    {
        var d = DiffOf(_w.WinnerCleared);
        Assert.True(d.Complete);
        Assert.Contains(d.Deltas, x => x.StartsWith("SharedCrimeFactionList=", StringComparison.Ordinal)
                                       && x.Contains("(winner has SharedCrimeFactionList ABSENT)", StringComparison.Ordinal)
                                       && x.Contains(_w.CrimeList.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(d.Deltas, x => x.Contains("(winner (absent))", StringComparison.Ordinal)
                                             || x.Contains("(winner (null link))", StringComparison.Ordinal));
    }

    // Probe E: "a capped read yields Complete=false (no false 'identical' claim)".
    [Fact]
    public void ACappedRead_IsIncompleteNotIdentical()
    {
        var big = new SkyrimMod(new ModKey("hcDiffBig", ModType.Plugin), SkyrimRelease.SkyrimSE).Factions.AddNew();
        for (int i = 0; i < 900; i++) big.Relations.Add(Rel(_w.Targets[i % 4]));
        var read = ReadEngine.ReadFields(big, new[] { "Relations" }, RecordReads.ConflictDiffDepth);
        var d = FieldsDiff.Compare(read, read);
        Assert.False(d.Complete);
        Assert.Empty(d.Deltas);
    }

    // Probe E2: "truncation fabricates NO list/one-sided deltas; both-sides mismatch kept".
    [Fact]
    public void ATruncatedSide_FabricatesNoListDeltasButKeepsABothSidesMismatch()
    {
        var truncA = Fields(("Flags", "A", null), ("Relations", null, "[2 item(s)]"),
                            ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null), ("…", null, Truncated));
        var fullB = Fields(("Flags", "B", null), ("Relations", null, "[2 item(s)]"),
                           ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "AAAAAA:m.esm", null),
                           ("Relations[1]", null, "[Relation]"), ("Relations[1].Target", "BBBBBB:m.esm", null));
        var d = FieldsDiff.Compare(truncA, fullB);
        Assert.False(d.Complete);
        Assert.StartsWith("Flags=A", Assert.Single(d.Deltas), StringComparison.Ordinal);
    }

    // Probe G: "numeric-keyed dict key rebinding IS a delta (exact-path, not positional)".
    [Fact]
    public void NumericKeyedDictRebinding_IsADelta()
    {
        var a = Fields(("Data", null, "[Dictionary`2: 2 pair(s)]"), ("Data[0]", null, "[PackageDataBool]"), ("Data[0].Name", "TopicData", null),
                       ("Data[3]", null, "[PackageDataBool]"), ("Data[3].Name", "Repeatable", null));
        var b = Fields(("Data", null, "[Dictionary`2: 2 pair(s)]"), ("Data[0]", null, "[PackageDataBool]"), ("Data[0].Name", "Repeatable", null),
                       ("Data[3]", null, "[PackageDataBool]"), ("Data[3].Name", "TopicData", null));
        var d = FieldsDiff.Compare(a, b);
        Assert.True(d.Complete);
        Assert.Equal(2, d.Deltas.Count);
    }

    // Probe G2: "a real numeric-keyed dict renders the 'pair(s)' marker".
    [Fact]
    public void ARealNumericKeyedDict_RendersThePairsMarker()
    {
        var pack = new SkyrimMod(new ModKey("hcDiffPack", ModType.Plugin), SkyrimRelease.SkyrimSE).Packages.AddNew();
        pack.Data.Add(3, new PackageDataBool { Name = "hcDiffPackDatum" });
        var read = ReadEngine.ReadFields(pack, new[] { "Data" }, 4);
        Assert.Contains(read.Fields, fv => fv.Path == "Data" && (fv.Note ?? "").Contains(" pair(s)]", StringComparison.Ordinal));
    }

    // Probe E3: "truncated comparison still reports a root COUNT delta".
    [Fact]
    public void TruncatedBothSides_StillReportTheRootCountDelta()
    {
        var c = Fields(("Relations", null, "[ExtendedList`1: 600 item(s)]"), ("Relations[0]", null, "[Relation]"),
                       ("Relations[0].Target", "AAAAAA:m.esm", null), ("…", null, Truncated));
        var d = Fields(("Relations", null, "[ExtendedList`1: 601 item(s)]"), ("Relations[0]", null, "[Relation]"),
                       ("Relations[0].Target", "AAAAAA:m.esm", null), ("…", null, Truncated));
        var r = FieldsDiff.Compare(c, d);
        Assert.False(r.Complete);
        Assert.StartsWith("Relations=", Assert.Single(r.Deltas), StringComparison.Ordinal);
    }

    // Probe H: "bracketed fields= read without a root summary compares exact-path".
    [Fact]
    public void ABracketedReadWithoutARootSummary_ComparesExactPath()
    {
        var a = Fields(("Data[0].Name", "TopicData", null), ("Data[3].Name", "Repeatable", null));
        var b = Fields(("Data[0].Name", "Repeatable", null), ("Data[3].Name", "TopicData", null));
        var d = FieldsDiff.Compare(a, b);
        Assert.True(d.Complete);
        Assert.Equal(2, d.Deltas.Count);
    }

    // Probe F: "FormKey case drift is NOT a content delta".
    [Fact]
    public void FormKeyCaseDrift_IsNotADelta()
    {
        var a = Fields(("Relations", null, "[3 item(s)]"), ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "17DDC4:ccBGSSSE001-Fish.esm", null));
        var b = Fields(("Relations", null, "[3 item(s)]"), ("Relations[0]", null, "[Relation]"), ("Relations[0].Target", "17ddc4:ccbgssse001-fish.esm", null));
        var d = FieldsDiff.Compare(a, b);
        Assert.True(d.Complete);
        Assert.Empty(d.Deltas);
    }
}

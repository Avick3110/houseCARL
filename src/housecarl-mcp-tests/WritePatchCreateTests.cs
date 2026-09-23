using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The create lane: an in-place create is not an extend, its requested ops are verified beside the CK-parity fills,
/// a same-call <c>@editorid</c> lands as the sibling's FormKey wherever the op carries it, and each op reports the value
/// it set. Stryker rows T48, T49, T52 and T53 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchCreateTests : IClassFixture<WritePathCorpus>, IDisposable
{
    const string TargetName = "HcWpCreateTarget.esp";

    readonly WritePathCorpus _corpus;
    readonly WritePathRig _rig = new();
    readonly LoadOrderResolver _order;
    readonly string _targetPath;

    public WritePatchCreateTests(WritePathCorpus corpus)
    {
        _corpus = corpus;
        var master = new SkyrimMod(new ModKey("HcWpCreateMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        master.Keywords.AddNew().EditorID = "HcWpCreateMasterKeyword";
        var masterPath = _rig.Write(master);
        var target = new SkyrimMod(ModKey.FromFileName(TargetName), SkyrimRelease.SkyrimSE);
        target.Keywords.AddNew().EditorID = "HcWpCreateTargetKeyword";
        _targetPath = _rig.Write(target, "target", master);
        _order = _rig.Order(masterPath, _targetPath);
    }

    static WritePatchBuilder.CreateSpec Spec(string type, string edid, params WriteRequest[] edits)
        => new() { RecordType = type, EditorId = edid, Edits = edits };

    WritePatchBuilder.CreateOutcome Create(string outName, params WritePatchBuilder.CreateSpec[] specs)
        => WritePatchBuilder.CreateRecords(_order, _corpus.Rulebook(), specs, _rig.Out(outName), extend: false);

    // T48
    [Fact]
    public void AnInPlaceCreateIsNotMarkedExtended()
    {
        var o = WritePatchBuilder.CreateRecordsInPlace(_order, _corpus.Rulebook(),
            new[] { Spec("Keyword", "HcWpCreateInPlace") }, _targetPath, TargetName);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.False(o.Extended);
    }

    // T49: a quest gets CK-parity fills after its one requested op; the requested op is still checked against the file.
    [Fact]
    public void ARequestedOpBesideAFillIsVerifiedAgainstTheFile()
    {
        var o = Create("HcWpCreateQuest.esp", Spec("Quest", "HcWpCreateQuest", WritePathRig.Req("Quest", "Priority", "Set", "5")));
        Assert.True(o.Success, o.Error);
        var ops = o.Created[0].Ops;
        Assert.Contains(ops, op => op.AfterIsNote);
        Assert.Equal("5", ops[0].LandedOnDisk);
    }

    // T52: '@editorid' resolved in a value, in a values list, in a compose and in a composes list.
    [Fact]
    public void ASiblingReferenceInAValueLandsTheSiblingsFormKey()
    {
        var o = Create("HcWpCreateSibValue.esp",
            Spec("Keyword", "HcWpSibKw"),
            Spec("Weapon", "HcWpSibSword", WritePathRig.Req("Weapon", "Keywords", "Add", "@HcWpSibKw")));
        Assert.True(o.Success, o.Error);
        Assert.Equal(o.Created[0].FormKey, Written(o).Weapons.Single().Keywords!.Single().FormKey);
    }

    [Fact]
    public void ASiblingReferenceInAValuesListLandsTheSiblingsFormKey()
    {
        var o = Create("HcWpCreateSibValues.esp",
            Spec("Keyword", "HcWpSibKw"),
            Spec("Weapon", "HcWpSibSword", new WriteRequest
            {
                RecordType = "Weapon", Path = new[] { "Keywords" }, Verb = "ReplaceAll", Values = new[] { "@HcWpSibKw" },
            }));
        Assert.True(o.Success, o.Error);
        Assert.Equal(o.Created[0].FormKey, Written(o).Weapons.Single().Keywords!.Single().FormKey);
    }

    static StructSpec Entry(string reference) => new()
    {
        Type = "LeveledItemEntry",
        Sets = new List<WriteRequest>
        {
            WritePathRig.Req("LeveledItemEntry", "Data.Level", "Set", "1"),
            WritePathRig.Req("LeveledItemEntry", "Data.Count", "Set", "1"),
            WritePathRig.Req("LeveledItemEntry", "Data.Reference", "Set", reference),
        },
    };

    [Fact]
    public void ASiblingReferenceInACompositionLandsTheSiblingsFormKey()
    {
        var o = Create("HcWpCreateSibCompose.esp",
            Spec("Weapon", "HcWpSibSword"),
            Spec("LeveledItem", "HcWpSibList", new WriteRequest
            {
                RecordType = "LeveledItem", Path = new[] { "Entries" }, Verb = "Add", Struct = Entry("@HcWpSibSword"),
            }));
        Assert.True(o.Success, o.Error);
        Assert.Equal(o.Created[0].FormKey, Written(o).LeveledItems.Single().Entries!.Single().Data!.Reference.FormKey);
    }

    [Fact]
    public void ASiblingReferenceInAComposesListLandsTheSiblingsFormKey()
    {
        var o = Create("HcWpCreateSibComposes.esp",
            Spec("Weapon", "HcWpSibSword"),
            Spec("LeveledItem", "HcWpSibList", new WriteRequest
            {
                RecordType = "LeveledItem", Path = new[] { "Entries" }, Verb = "Add",
                Structs = new[] { Entry("@HcWpSibSword"), Entry("@HcWpSibSword") },
            }));
        Assert.True(o.Success, o.Error);
        Assert.All(Written(o).LeveledItems.Single().Entries!, e => Assert.Equal(o.Created[0].FormKey, e.Data!.Reference.FormKey));
    }

    ISkyrimModGetter Written(WritePatchBuilder.CreateOutcome o) => _rig.Open(o.OutputPath);

    // T53: each op reports the value it set — a scalar's token, a list's count reading.
    [Fact]
    public void ACreateOpReportsTheValueItSet()
    {
        var o = Create("HcWpCreateAfter.esp",
            Spec("Keyword", "HcWpAfterKw"),
            Spec("Weapon", "HcWpAfterSword",
                WritePathRig.Req("Weapon", "BasicStats.Damage", "Set", "42"),
                WritePathRig.Req("Weapon", "Keywords", "Add", "@HcWpAfterKw")));
        Assert.True(o.Success, o.Error);
        var ops = o.Created[1].Ops;
        Assert.Equal("42", ops[0].After);
        Assert.Equal("[list: 1 item(s)]", ops[1].After);
    }

    public void Dispose() => _rig.Dispose();
}

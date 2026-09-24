using System.Reflection;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// What a patch write reports about each op from the file it wrote: the op's label, which op a later one supersedes,
/// what landed for a list op, a record found wherever it sits in the file, and what a walk that did not finish can and
/// cannot say. Stryker rows T50, T51, T54, T55 and T56 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchVerifyTests : IDisposable
{
    const string Master = "HcWpVerifyMaster.esm";

    readonly WritePathRig _rig = new();
    readonly SkyrimMod _master;
    readonly LoadOrderResolver _order;
    readonly FormKey _sword, _axe, _list, _kw1, _kw2;

    public WritePatchVerifyTests()
    {
        _master = new SkyrimMod(ModKey.FromFileName(Master), SkyrimRelease.SkyrimSE);
        var k1 = _master.Keywords.AddNew(); k1.EditorID = "HcWpVerifyKw1"; _kw1 = k1.FormKey;
        var k2 = _master.Keywords.AddNew(); k2.EditorID = "HcWpVerifyKw2"; _kw2 = k2.FormKey;
        _sword = Weapon("HcWpVerifySword").FormKey;
        _axe = Weapon("HcWpVerifyAxe").FormKey;
        var ll = _master.LeveledItems.AddNew();
        ll.EditorID = "HcWpVerifyList";
        ll.Entries = new Noggog.ExtendedList<LeveledItemEntry> { Entry(1), Entry(2) };
        _list = ll.FormKey;
        _order = _rig.Order(_rig.Write(_master));

        Weapon Weapon(string edid)
        {
            var w = _master.Weapons.AddNew();
            w.EditorID = edid;
            w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
            w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { k1.ToLink() };
            return w;
        }
    }

    LeveledItemEntry Entry(short level) => new()
    {
        Data = new LeveledItemEntryData { Level = level, Count = 1, Reference = new FormLink<IItemGetter>(_sword) },
    };

    StructSpec EntrySpec(string level) => new()
    {
        Type = "LeveledItemEntry",
        Sets = new List<WriteRequest>
        {
            WritePathRig.Req("LeveledItemEntry", "Data.Level", "Set", level),
            WritePathRig.Req("LeveledItemEntry", "Data.Count", "Set", "1"),
            WritePathRig.Req("LeveledItemEntry", "Data.Reference", "Set", _sword.ToString()),
        },
    };

    WritePatchBuilder.PatchOutcome Apply(params WritePatchBuilder.PatchEdit[] edits)
    {
        var o = WritePatchBuilder.Apply(_order, TestCorpus.Rulebook(), edits, _rig.Out("HcWpVerifyOut.esp"), extend: false);
        Assert.True(o.Success, o.Error);
        return o;
    }

    static WritePatchBuilder.PatchEdit Edit(FormKey target, string path, string verb, string? value = null, string? key = null,
        StructSpec? compose = null, IReadOnlyList<StructSpec>? composes = null)
        => new() { Target = target, Path = path.Split('.'), Verb = verb, Value = value, Key = key, Struct = compose, Structs = composes };

    // ---- T51: the op label ----

    [Fact]
    public void AnOpLabelReadsVerbPathKeyAndValue()
    {
        var o = Apply(
            Edit(_sword, "BasicStats.Damage", "Set", "42"),
            Edit(_sword, "Keywords", "SetAtIndex", _kw2.ToString(), key: "0"),
            Edit(_axe, "Keywords", "Remove", key: "0"));

        Assert.Equal(new[]
        {
            "Set BasicStats.Damage = 42",
            $"SetAtIndex Keywords[0] = {_kw2}",
            "Remove Keywords[0]",
        }, o.Ops.Select(op => op.Label));
    }

    // ---- T55: which op a later one supersedes ----

    [Fact]
    public void ASetOfAFieldIsSupersededByALaterSetInsideIt()
    {
        var o = Apply(
            Edit(_sword, "BasicStats", "Set", compose: new StructSpec
            {
                Type = "WeaponBasicStats", Fields = new Dictionary<string, string> { ["Damage"] = "5", ["Weight"] = "1" },
            }),
            Edit(_sword, "BasicStats.Damage", "Set", "42"));

        Assert.True(o.Ops[0].SupersededInCall);
        Assert.False(o.Ops[1].SupersededInCall);
    }

    [Fact]
    public void TwoIndexedElementsOfOneListStayIndependent()
    {
        var o = Apply(
            Edit(_list, "Entries[0].Data.Level", "Set", "3"),
            Edit(_list, "Entries[1].Data.Level", "Set", "4"));

        Assert.False(o.Ops[0].SupersededInCall);
        Assert.False(o.Ops[1].SupersededInCall);
    }

    [Fact]
    public void AWholeListOpIsSupersededByALaterOpOnOneOfItsElements()
    {
        var o = Apply(
            Edit(_list, "Entries", "Add", compose: EntrySpec("5")),
            Edit(_list, "Entries[0].Data.Level", "Set", "7"));

        Assert.True(o.Ops[0].SupersededInCall);
        Assert.False(o.Ops[1].SupersededInCall);
    }

    // ---- T56: what landed ----

    [Fact]
    public void ABatchAddReportsHowManyElementsLanded()
    {
        var o = Apply(Edit(_list, "Entries", "Add", composes: new[] { EntrySpec("5"), EntrySpec("6"), EntrySpec("7") }));
        Assert.Contains("(+3)", o.Ops[0].LandedOnDisk);
    }

    [Fact]
    public void AKeyedListOpReportsTheElementItTouched()
    {
        var o = Apply(Edit(_sword, "Keywords", "SetAtIndex", _kw2.ToString(), key: "0"));
        Assert.Contains("set [0]", o.Ops[0].LandedOnDisk);
    }

    /// <summary>A substruct is neither a scalar with a token nor a list with a touched element; it still reports
    /// what landed, the substruct's own summary.</summary>
    [Fact]
    public void ASubstructSetStillReportsWhatLanded()
    {
        var o = Apply(Edit(_sword, "BasicStats", "Set", compose: new StructSpec
        {
            Type = "WeaponBasicStats", Fields = new Dictionary<string, string> { ["Damage"] = "5", ["Weight"] = "1" },
        }));
        Assert.NotNull(o.Ops[0].Landed);
    }

    // ---- T54: a record found wherever it sits in the file ----

    [Fact]
    public void EditingTheSecondRecordOfAPatchVerifiesIt()
    {
        // A patch already carrying both weapons, the sword first; the extend edits only the axe.
        var patch = new SkyrimMod(ModKey.FromFileName("HcWpVerifyTwo.esp"), SkyrimRelease.SkyrimSE);
        patch.Weapons.GetOrAddAsOverride(_master.Weapons[_sword]);
        patch.Weapons.GetOrAddAsOverride(_master.Weapons[_axe]);
        var path = _rig.Out("HcWpVerifyTwo.esp");
        patch.BeginWrite.ToPath(path).WithLoadOrder(_master).Write();

        var o = WritePatchBuilder.Apply(_order, TestCorpus.Rulebook(),
            new[] { Edit(_axe, "BasicStats.Damage", "Set", "42") }, path, extend: true);

        Assert.True(o.Success, o.Error);
        Assert.False(o.Ops[0].RecordAbsentFromFile);
        Assert.Equal("42", o.Ops[0].LandedOnDisk);
    }

    // ---- T50: a walk that did not finish ----

    /// <summary>A plugin of three weapons whose last record's declared size runs past the end of the file: a walk
    /// yields the first two and then throws.</summary>
    (string path, FormKey first, FormKey second, FormKey broken) TruncatedLastRecord()
    {
        var mod = new SkyrimMod(ModKey.FromFileName("HcWpVerifyCut.esp"), SkyrimRelease.SkyrimSE);
        var keys = Enumerable.Range(0, 3).Select(i =>
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = "HcWpVerifyCut" + i;
            w.BasicStats = new WeaponBasicStats { Damage = 1, Weight = 1 };
            return w.FormKey;
        }).ToArray();
        var path = _rig.Write(mod, "cut");
        var bytes = File.ReadAllBytes(path);
        int last = -1;
        for (int i = 8; i + 8 <= bytes.Length; i++)
            if (bytes.AsSpan(i, 4).SequenceEqual("WEAP"u8) && Encoding.ASCII.GetString(bytes, i - 8, 4) != "GRUP") last = i;
        Assert.True(last > 0, "no WEAP record header");
        BitConverter.GetBytes(0xFFFFu).CopyTo(bytes, last + 4);
        File.WriteAllBytes(path, bytes);
        return (path, keys[0], keys[1], keys[2]);
    }

    [Fact]
    public void AWalkThatThrowsPartWayDoesNotFinish()
    {
        var (path, _, second, broken) = TruncatedLastRecord();
        var walk = WritePatchBuilder.WalkWrittenFileFor(_rig.Open(path), new[] { second, broken });
        Assert.Contains(second, walk.Found.Keys);
        Assert.False(walk.Finished);
    }

    [Fact]
    public void AFinishedWalkIsFinishedWithoutFindingAMissingRecord()
    {
        var missing = new FormKey(ModKey.FromFileName(Master), 0xABC);
        var walk = WritePatchBuilder.WalkWrittenFileFor(_rig.Open(_rig.Write(_master, "intact")), new[] { _sword, missing });
        Assert.True(walk.Finished);
        Assert.DoesNotContain(missing, walk.Found.Keys);
    }

    /// <summary>The create lane's per-record verdict off such a walk: the record it reached was checked, and the one it
    /// never reached is not called absent. The verdict is a private step of the create lane, reached here by
    /// reflection with the damaged file standing in for a written one.</summary>
    [Fact]
    public void ACreatedRecordAWalkReachedIsCheckedAndOneItDidNotIsNotAbsent()
    {
        var (path, _, second, broken) = TruncatedLastRecord();
        var created = new List<WritePatchBuilder.CreatedRecord>
        {
            new(second, "Weapon", "HcWpVerifyCut1", Array.Empty<WritePatchBuilder.OpResult>()),
            new(broken, "Weapon", "HcWpVerifyCut2", Array.Empty<WritePatchBuilder.OpResult>()),
        };
        var verify = typeof(WritePatchBuilder).GetMethod("VerifyCreatedAgainstFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var reported = (List<WritePatchBuilder.CreatedRecord>)verify.Invoke(null, new object[]
        {
            _rig.Open(path), created, new List<List<WriteRequest?>> { new(), new() },
        })!;

        Assert.True(reported[0].VerifyAttempted);
        Assert.False(reported[0].AbsentFromFile);
        Assert.False(reported[1].VerifyAttempted);
        Assert.False(reported[1].AbsentFromFile);
    }

    public void Dispose() => _rig.Dispose();
}

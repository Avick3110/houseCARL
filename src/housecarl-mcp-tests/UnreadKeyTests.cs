using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>key= on a verb that does not read it is refused before any work, on apply and create, every lane and the dry
/// run, rather than dropped while the element lands at the end (#1055).</summary>
[Trait("tier", "integration")]
public sealed class UnreadKeyTests : IClassFixture<ComposesBatchWorld>
{
    readonly ComposesBatchWorld _w;
    public UnreadKeyTests(ComposesBatchWorld w) => _w = w;

    StructInput Entry(int level) => new()
    {
        Type = "LeveledItemEntry",
        Sets = new[]
        {
            new NestedSet { Path = "Data.Level", Value = level.ToString() },
            new NestedSet { Path = "Data.Count", Value = "1" },
            new NestedSet { Path = "Data.Reference", Value = _w.WeaponFid },
        },
    };

    // The world's keyword is the master's first record, allocated just before the weapon.
    string KwFid => $"{_w.ListKey.ID - 2:X6}:{_w.ListKey.ModKey.FileName}";

    BulkOp KeywordAdd(string verb = "Add", string? key = "0") => new()
    {
        Formid = _w.WeaponFid, FieldPath = "Keywords", Verb = verb, Key = key, Value = KwFid,
    };

    BulkOp ComposesAdd(string? key = "0") => new()
    {
        Formid = _w.ListFid, FieldPath = "Entries", Verb = "Add", Key = key, Composes = new[] { Entry(1), Entry(2) },
    };

    void NothingWritten(WritePatchBuilder.PatchOutcome o, string patch, string expected)
    {
        Assert.False(o.Success);
        Assert.Contains(expected, o.Error);
        Assert.True(string.IsNullOrEmpty(o.OutputPath));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains(patch));
    }

    const string ListAddRefusal = "appends at the end, so it takes no key";
    const string ComposesRefusal = "composes= with Add appends each element at the end of the list, so it takes no key=";

    [Fact]
    public void ApplyListAddWithKeyIsRefusedAndWritesNothing()
        => NothingWritten(_w.Svc.ApplyEdits(new[] { KeywordAdd() }, "HcKeyListAdd", null), "HcKeyListAdd", ListAddRefusal);

    // The issue's case: composes= with Add and key= appended at the end with no word.
    [Fact]
    public void ApplyComposesAddWithKeyIsRefusedAndWritesNothing()
        => NothingWritten(_w.Svc.ApplyEdits(new[] { ComposesAdd() }, "HcKeyComposes", null), "HcKeyComposes", ComposesRefusal);

    [Fact]
    public void ApplyReplaceAllWithKeyIsRefused()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Keywords", Verb = "ReplaceAll", Key = "0", Values = new[] { KwFid } };
        NothingWritten(_w.Svc.ApplyEdits(new[] { op }, "HcKeyReplaceAll", null), "HcKeyReplaceAll", "ReplaceAll replaces the whole field, so it takes no key=");
    }

    [Fact]
    public void ApplyRemoveOfAWholeFieldWithKeyIsRefused()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Name", Verb = "Remove", Key = "0" };
        NothingWritten(_w.Svc.ApplyEdits(new[] { op }, "HcKeyRemoveWhole", null), "HcKeyRemoveWhole", "clears the whole field, so it takes no key");
    }

    [Fact]
    public void TheDryRunRefusesAListAddWithKey()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd() }, "HcKeyDry", null, dryRun: true);
        Assert.False(o.Success);
        Assert.Contains(ListAddRefusal, o.Error);
        Assert.DoesNotContain("would become", o.Error);
    }

    [Fact]
    public void TheDryRunRefusesComposesAddWithKey()
    {
        var o = _w.Svc.ApplyEdits(new[] { ComposesAdd() }, "HcKeyDryComposes", null, dryRun: true);
        Assert.False(o.Success);
        Assert.Contains(ComposesRefusal, o.Error);
        Assert.DoesNotContain("would become", o.Error);
    }

    [Fact]
    public void TheIntoLaneRefusesComposesAddWithKeyBeforeLookingForThePatch()
        => Assert.Contains(ComposesRefusal, _w.Svc.ApplyEdits(new[] { ComposesAdd() }, null, "HcKeyIntoMissing").Error);

    [Fact]
    public void TheIntoLaneRefusesAListAddWithKeyAndLeavesThePatchAlone()
    {
        var seed = _w.Svc.ApplyEdits(new[] { new BulkOp { Formid = _w.WeaponFid, FieldPath = "BasicStats.Damage", Value = "12" } }, "HcKeyIntoSeed", null);
        Assert.True(seed.Success, seed.Error);
        var before = File.ReadAllBytes(seed.OutputPath);
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd() }, null, Path.GetFileName(seed.OutputPath));
        Assert.False(o.Success);
        Assert.Contains(ListAddRefusal, o.Error);
        Assert.Equal(before, File.ReadAllBytes(seed.OutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheInPlaceLaneRefusesBeforeTouchingTheTarget(bool composes)
    {
        var o = _w.Svc.ApplyEdits(new[] { composes ? ComposesAdd() : KeywordAdd() }, null, null,
            target: "HcW3Master.esm", inPlace: true, acknowledge: true, dryRun: true);
        Assert.False(o.Success);
        Assert.Contains(composes ? ComposesRefusal : ListAddRefusal, o.Error);
    }

    WritePatchBuilder.CreateOutcome Create(string patch, string type, BulkOp op) => _w.Svc.CreateRecordsBatch(new[]
    {
        new CreateOp { RecordType = type, Editorid = patch + "Rec", Operations = new[] { op } },
    }, patch, null);

    void NothingCreated(WritePatchBuilder.CreateOutcome o, string patch, string expected)
    {
        Assert.False(o.Success);
        Assert.Contains(expected, o.Error);
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains(patch));
    }

    [Fact]
    public void CreateListAddWithKeyIsRefused()
        => NothingCreated(Create("HcKeyCreateList", "Weapon",
            new BulkOp { FieldPath = "Keywords", Verb = "Add", Key = "0", Value = KwFid }), "HcKeyCreateList", ListAddRefusal);

    [Fact]
    public void CreateComposesAddWithKeyIsRefused()
        => NothingCreated(Create("HcKeyCreateComposes", "LeveledItem",
            new BulkOp { FieldPath = "Entries", Verb = "Add", Key = "0", Composes = new[] { Entry(1) } }), "HcKeyCreateComposes", ComposesRefusal);

    [Fact]
    public void CreateSingularComposeAddWithKeyIsRefused()
        => NothingCreated(Create("HcKeyCreateCompose", "LeveledItem",
            new BulkOp { FieldPath = "Entries", Verb = "Add", Key = "0", Compose = Entry(1) }), "HcKeyCreateCompose", ListAddRefusal);

    // A verb that reads key= still takes it: InsertAtIndex puts the element at the index.
    [Fact]
    public void InsertAtIndexStillReadsKey()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd("InsertAtIndex", "0") }, "HcKeyInsert", null);
        Assert.True(o.Success, o.Error);
        Assert.True(File.Exists(o.OutputPath));
    }

    [Fact]
    public void AListAddWithoutKeyStillWrites()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd(key: null) }, "HcKeyNoKey", null);
        Assert.True(o.Success, o.Error);
    }
}

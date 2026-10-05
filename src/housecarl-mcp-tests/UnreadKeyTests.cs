using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
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

    bool FolderLeft(string patch) =>
        Directory.EnumerateDirectories(_w.ModsDir).Any(d => Path.GetFileName(d) == OutputLocations.ModFolderName(patch));

    void NothingWritten(WritePatchBuilder.PatchOutcome o, string patch, string expected)
    {
        Assert.False(o.Success);
        Assert.Contains(expected, o.Error);
        Assert.True(string.IsNullOrEmpty(o.OutputPath));
        Assert.False(FolderLeft(patch));
    }

    List<short> LevelsIn(string espPath)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var ll = ov.LeveledItems.First(x => x.FormKey == _w.ListKey);
        return (ll.Entries ?? Enumerable.Empty<ILeveledItemEntryGetter>()).Select(e => e.Data!.Level).ToList();
    }

    const string ListAddRefusal = "appends at the end, so it takes no key";
    const string ComposesRefusal = "composes= with Add appends each element at the end of the list, so it takes no key";

    [Fact]
    public void ApplyListAddWithKeyIsRefusedAndWritesNothing()
        => NothingWritten(_w.Svc.ApplyEdits(new[] { KeywordAdd() }, "HcKeyListAdd", null), "HcKeyListAdd", ListAddRefusal);

    // The remedy is built from the field's shape: a formlink list places one value at an index with value= + key=.
    [Fact]
    public void TheListAddRemedyNamesTheCallThatPlacesAValueAtAnIndex()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd() }, "HcKeyListAddRemedy", null);
        Assert.Contains("InsertAtIndex (value= + key=)", o.Error);
    }

    // The issue's case: composes= with Add and key= appended at the end with no word.
    [Fact]
    public void ApplyComposesAddWithKeyIsRefusedAndWritesNothing()
        => NothingWritten(_w.Svc.ApplyEdits(new[] { ComposesAdd() }, "HcKeyComposes", null), "HcKeyComposes", ComposesRefusal);

    [Fact]
    public void ApplyReplaceAllWithKeyIsRefused()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Keywords", Verb = "ReplaceAll", Key = "0", Values = new[] { KwFid } };
        NothingWritten(_w.Svc.ApplyEdits(new[] { op }, "HcKeyReplaceAll", null), "HcKeyReplaceAll", "replaces the whole field, so it takes no key");
    }

    [Fact]
    public void ApplyRemoveOfAWholeFieldWithKeyIsRefused()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Name", Verb = "Remove", Key = "0" };
        NothingWritten(_w.Svc.ApplyEdits(new[] { op }, "HcKeyRemoveWhole", null), "HcKeyRemoveWhole", "clears the whole field, so it takes no key");
    }

    // The field's shape is answered before the key: Merge on a list says it needs a dict, not that it takes no key.
    [Fact]
    public void MergeWithKeyOnAListGetsTheShapeRefusalFirst()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Keywords", Verb = "Merge", Key = "0", Entries = new() { ["0"] = KwFid } };
        var o = _w.Svc.ApplyEdits(new[] { op }, "HcKeyMergeList", null);
        NothingWritten(o, "HcKeyMergeList", "Merge is only valid on dict");
        Assert.DoesNotContain("takes no key", o.Error);
    }

    // …and composes= with a key on a list of formlinks says composes= has nothing to build there.
    [Fact]
    public void ComposesWithKeyOnAFormLinkListGetsTheShapeRefusalFirst()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "Keywords", Verb = "Add", Key = "0", Composes = new[] { Entry(1) } };
        var o = _w.Svc.ApplyEdits(new[] { op }, "HcKeyComposesKw", null);
        NothingWritten(o, "HcKeyComposesKw", "composes= has nothing to build");
        Assert.DoesNotContain("takes no key", o.Error);
    }

    [Fact]
    public void ApplyCopyFromWithKeyIsRefused()
    {
        var op = new BulkOp { Formid = _w.WeaponFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", Key = "0", FromPlugin = "HcW3Master.esm" };
        NothingWritten(_w.Svc.ApplyEdits(new[] { op }, "HcKeyCopyFrom", null), "HcKeyCopyFrom", "CopyFrom copies the whole field 'Damage', so it takes no key");
    }

    // A blank key is no key: a client that fills empty optional parameters with "" still appends.
    [Fact]
    public void AListAddWithABlankKeyAppends()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd(key: "") }, "HcKeyBlank", null);
        Assert.True(o.Success, o.Error);
        using var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(2, ov.Weapons.Single().Keywords!.Count);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheIntoLaneRefusesAndLeavesThePatchAlone(bool composes)
    {
        var seed = _w.Svc.ApplyEdits(new[] { new BulkOp { Formid = _w.WeaponFid, FieldPath = "BasicStats.Damage", Value = "12" } },
            composes ? "HcKeyIntoSeedC" : "HcKeyIntoSeed", null);
        Assert.True(seed.Success, seed.Error);
        var before = File.ReadAllBytes(seed.OutputPath);
        var o = _w.Svc.ApplyEdits(new[] { composes ? ComposesAdd() : KeywordAdd() }, null, Path.GetFileName(seed.OutputPath));
        Assert.False(o.Success);
        Assert.Contains(composes ? ComposesRefusal : ListAddRefusal, o.Error);
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

    WritePatchBuilder.CreateOutcome Create(string patch, string type, params BulkOp[] ops) => _w.Svc.CreateRecordsBatch(new[]
    {
        new CreateOp { RecordType = type, Editorid = patch + "Rec", Operations = ops },
    }, patch, null);

    void NothingCreated(WritePatchBuilder.CreateOutcome o, string patch, string expected)
    {
        Assert.False(o.Success);
        Assert.Contains(expected, o.Error);
        Assert.False(FolderLeft(patch));
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

    [Fact]
    public void CreateMergeWithKeyOnADictIsRefused()
        => NothingCreated(Create("HcKeyCreateMerge", "Class",
            new BulkOp { FieldPath = "SkillWeights", Verb = "Merge", Key = "OneHanded", Entries = new() { ["OneHanded"] = "5" } }),
            "HcKeyCreateMerge", "Merge on dict 'SkillWeights' sets each pair in entries=, so it takes no key");

    // A verb that reads key= still takes it: InsertAtIndex puts the element at the index.
    [Fact]
    public void InsertAtIndexStillReadsKey()
    {
        var o = _w.Svc.ApplyEdits(new[]
        {
            ComposesAdd(key: null),
            new BulkOp { Formid = _w.ListFid, FieldPath = "Entries", Verb = "InsertAtIndex", Key = "0", Compose = Entry(7) },
        }, "HcKeyInsert", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(new short[] { 7, 1, 2 }, LevelsIn(o.OutputPath));
    }

    [Fact]
    public void AListRemoveStillReadsTheIndex()
    {
        var o = _w.Svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = _w.ListFid, FieldPath = "Entries", Verb = "Add", Composes = new[] { Entry(1), Entry(2), Entry(3) } },
            new BulkOp { Formid = _w.ListFid, FieldPath = "Entries", Verb = "Remove", Key = "1" },
        }, "HcKeyListRemove", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(new short[] { 1, 3 }, LevelsIn(o.OutputPath));
    }

    [Fact]
    public void ADictAddAndRemoveStillReadTheKey()
    {
        var o = Create("HcKeyDict", "Class",
            new BulkOp { FieldPath = "SkillWeights", Verb = "Add", Key = "OneHanded", Value = "5" },
            new BulkOp { FieldPath = "SkillWeights", Verb = "Add", Key = "Archery", Value = "3" },
            new BulkOp { FieldPath = "SkillWeights", Verb = "Remove", Key = "OneHanded" });
        Assert.True(o.Success, o.Error);
        using var ov = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        var weights = ov.Classes.Single().SkillWeights;
        Assert.Equal((byte)3, weights[Skill.Archery]);
        // The record stores every skill's weight, so a removed entry reads back as 0.
        Assert.Equal((byte)0, weights[Skill.OneHanded]);
    }

    [Fact]
    public void AListAddWithoutKeyStillWrites()
    {
        var o = _w.Svc.ApplyEdits(new[] { KeywordAdd(key: null) }, "HcKeyNoKey", null);
        Assert.True(o.Success, o.Error);
    }

    // The bigger problem is answered first: a keyed Remove on a field that cannot be written gets the writability refusal.
    [Fact]
    public void AKeyedRemoveOnAnUnwritableFieldGetsTheWritabilityRefusal()
    {
        // No real field is both nullable and unwritable, so the test corpus is copied with Weapon.Name made unwritable.
        var corpus = CorpusRulebook.LoadCorpus(TestCorpus.Path);
        var name = corpus.Types["Weapon"].Fields.Single(f => f.Name == "Name");
        Assert.True(name.Nullable);
        name.Writable = false;
        var dir = Path.Combine(Path.GetTempPath(), "hc-unread-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "corpus.json");
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(corpus));
            var book = CorpusRulebook.Load(path);
            WriteRequest Remove(string? key) => new() { RecordType = "Weapon", Path = new[] { "Name" }, Verb = "Remove", Key = key };
            var unkeyed = book.Validate(Remove(null));
            Assert.Contains("not writable", unkeyed);
            Assert.Equal(unkeyed, book.Validate(Remove("0")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

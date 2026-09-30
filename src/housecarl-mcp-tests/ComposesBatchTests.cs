using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>composes= builds many modeled elements in one op: Add appends each, ReplaceAll clears then appends each,
/// and the batch is all-or-nothing with named refusals. Migrated from the bulk-primitives-wave3 probe's P8a arm.</summary>
[Trait("tier", "integration")]
public sealed class ComposesBatchTests : IClassFixture<ComposesBatchWorld>
{
    readonly ComposesBatchWorld _w;
    public ComposesBatchTests(ComposesBatchWorld w) => _w = w;

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

    WritePatchBuilder.PatchOutcome Apply(string verb, StructInput[]? composes, string? patch, string? into = null,
        StructInput? compose = null, string? formid = null, string field = "Entries", string? key = null)
        => _w.Svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = formid ?? _w.ListFid, FieldPath = field, Verb = verb, Key = key, Compose = compose, Composes = composes },
        }, patch, into);

    List<short> LevelsIn(string espPath)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var ll = ov.LeveledItems.First(x => x.FormKey == _w.ListKey);
        return (ll.Entries ?? Enumerable.Empty<ILeveledItemEntryGetter>()).Select(e => e.Data!.Level).ToList();
    }

    static string Refused(WritePatchBuilder.PatchOutcome o)
    {
        Assert.False(o.Success);
        Assert.NotNull(o.Error);
        return o.Error!;
    }

    // probe: Add composes: whole call succeeds / appended 3 entries
    [Fact]
    public void AddComposesAppendsEachElement()
    {
        var o = Apply("Add", new[] { Entry(1), Entry(2), Entry(3) }, "P8aAdd");
        Assert.True(o.Success, o.Error);
        Assert.Equal(new short[] { 1, 2, 3 }, LevelsIn(o.OutputPath));
    }

    // probe: ReplaceAll setup: seed patch carries 3
    // probe: ReplaceAll composes: whole call succeeds / CLEARED the 3 seeds then appended 2 → count==2
    [Fact]
    public void ReplaceAllComposesClearsThenAppends()
    {
        var seed = Apply("Add", new[] { Entry(1), Entry(2), Entry(3) }, "P8aRepl");
        Assert.True(seed.Success, seed.Error);
        Assert.Equal(3, LevelsIn(seed.OutputPath).Count);

        var repl = Apply("ReplaceAll", new[] { Entry(5), Entry(6) }, null, into: "P8aRepl");
        Assert.True(repl.Success, repl.Error);
        Assert.Equal(new short[] { 5, 6 }, LevelsIn(repl.OutputPath));
    }

    // probe: ReplaceAll composes=[] CLEARS the modeled list → count 0
    [Fact]
    public void ReplaceAllWithEmptyComposesClearsTheList()
    {
        var seed = Apply("Add", new[] { Entry(1), Entry(2), Entry(3) }, "P8aClr");
        Assert.True(seed.Success, seed.Error);

        var clr = Apply("ReplaceAll", Array.Empty<StructInput>(), null, into: "P8aClr");
        Assert.True(clr.Success, clr.Error);
        Assert.Empty(LevelsIn(clr.OutputPath));
    }

    // probe: Add composes=[] (empty) still refused (only ReplaceAll clears)
    // probe: empty composes=[] → refused ('empty')
    [Fact]
    public void AddWithEmptyComposesIsRefusedNamingReplaceAll()
    {
        var e = Refused(Apply("Add", Array.Empty<StructInput>(), "P8aAddEmpty"));
        Assert.Contains("empty", e);
        Assert.Contains("ReplaceAll", e);
    }

    // probe: all-or-nothing: a bad composes element refuses the whole call (names composes[1], nothing written)
    [Fact]
    public void OneBadElementRefusesTheWholeBatchAndWritesNothing()
    {
        var o = Apply("Add", new[] { Entry(1), new StructInput { Type = "NotARealElementType" } }, "P8aBad");
        Assert.Contains("composes[1]", Refused(o));
        Assert.True(string.IsNullOrEmpty(o.OutputPath));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_w.ModsDir), d => Path.GetFileName(d).Contains("P8aBad"));
    }

    // probe: mutual exclusion: compose= AND composes= → refused ('not both')
    [Fact]
    public void ComposeAndComposesTogetherAreRefused()
        => Assert.Contains("not both", Refused(Apply("Add", new[] { Entry(2) }, "P8aBoth", compose: Entry(1))));

    // probe: formlink-list Keywords + composes → refused (use values=/value=)
    [Fact]
    public void ComposesOnAFormLinkListIsRefusedPointingAtValues()
    {
        var e = Refused(Apply("Add", new[] { new StructInput { Type = "Keyword" } }, "P8aCoer", formid: _w.WeaponFid, field: "Keywords"));
        Assert.Contains("formlink", e);
        Assert.Contains("values=", e);
    }

    // probe: non-list BasicStats.Damage + composes → refused ('builds a LIST')
    [Fact]
    public void ComposesOnAScalarIsRefused()
        => Assert.Contains("builds a LIST",
            Refused(Apply("Add", new[] { Entry(1) }, "P8aScal", formid: _w.WeaponFid, field: "BasicStats.Damage")));

    // probe: wrong verb SetAtIndex + composes → refused (Add or ReplaceAll)
    [Fact]
    public void ComposesWithSetAtIndexIsRefusedNamingTheTwoVerbs()
    {
        var e = Refused(Apply("SetAtIndex", new[] { Entry(1) }, "P8aVerb", key: "0"));
        Assert.Contains("Add", e);
        Assert.Contains("ReplaceAll", e);
        Assert.Contains("not SetAtIndex", e);
    }
}

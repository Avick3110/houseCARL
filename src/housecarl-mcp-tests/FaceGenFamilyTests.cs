using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The facegen family driven end to end over <see cref="FaceGenWorld"/>: real plugins, real loose files,
/// one assertion per class the join has to tell apart.</summary>
[Trait("tier", "integration")]
public sealed class FaceGenFamilyTests : IClassFixture<FaceGenWorld>
{
    readonly FaceGenWorld _w;
    public FaceGenFamilyTests(FaceGenWorld w) => _w = w;

    string Sweep(params string[] findings)
        => CheckTools.CheckTool(_w.Svc, findings: findings, max_chars: 60000);

    static string RowFor(string response, string editorId)
    {
        var at = response.IndexOf("'" + editorId + "'", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no row for {editorId} in:\n{response}");
        var start = response.LastIndexOf('[', at);
        var end = response.IndexOf("\n\n", at, StringComparison.Ordinal);
        return response[start..(end < 0 ? response.Length : end)];
    }

    [Fact]
    public void EachMismatchShapeLandsInItsOwnClass()
    {
        var text = Sweep("facegen");
        Assert.Contains("[TINT_ABSENT]", RowFor(text, "HcFgTintAbsent"));
        Assert.Contains("[MESH_ABSENT]", RowFor(text, "HcFgMeshAbsent"));
        Assert.Contains("[BAKE_ABSENT]", RowFor(text, "HcFgBakeAbsent"));
        Assert.Contains("[SPLIT_BAKE]", RowFor(text, "HcFgSplit"));
        Assert.Contains("[STALE_BAKE]", RowFor(text, "HcFgStale"));
    }

    [Fact]
    public void AMatchedPairWhoseRecordAgreesIsNotReported()
        => Assert.DoesNotContain("'HcFgClean'", Sweep("facegen"), StringComparison.Ordinal);

    [Fact]
    public void AStaleRowNamesTheFacegenOwnersPluginAsThePoleAndTheFieldThatDisagrees()
    {
        var row = RowFor(Sweep("facegen"), "HcFgStale");
        Assert.Contains(FaceGenWorld.MasterName, row, StringComparison.Ordinal);
        Assert.Contains("TextureLighting", row, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplatedNpcAndOneOnARaceThatBakesNoHeadAreExcludedAndCounted()
    {
        var text = Sweep("facegen");
        Assert.DoesNotContain("'HcFgTemplated'", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'HcFgBeast'", text, StringComparison.Ordinal);
        Assert.Contains("1 Template+Traits", text, StringComparison.Ordinal);
        Assert.Contains("1 on a race with no FaceGenHead flag", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBenignFamilySplitIsCountedButNotListedUntilItsClassIsNamed()
    {
        var whole = Sweep("facegen");
        Assert.DoesNotContain("'HcFgFamily'", whole, StringComparison.Ordinal);
        Assert.Contains("family_split=1", whole, StringComparison.Ordinal);
        Assert.Contains("[FAMILY_SPLIT]", RowFor(Sweep("family_split"), "HcFgFamily"));
    }

    [Fact]
    public void AFileNoActorReadsIsInertAndAForeignIndexByteIsItsOwnClass()
    {
        var text = Sweep("facegen");
        Assert.Contains("00099999.nif", text, StringComparison.Ordinal);          // no record defines it
        Assert.Contains(FaceGenWorld.OffOrderFolder, text, StringComparison.Ordinal);
        Assert.Contains("notahexname.nif", text, StringComparison.Ordinal);
        Assert.Contains("[FOREIGN_INDEX]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassTokenNarrowsTheFamilyAndAnUnknownOneIsRefusedWithTheWholeVocabulary()
    {
        var narrowed = Sweep("tint_absent");
        Assert.Contains("'HcFgTintAbsent'", narrowed, StringComparison.Ordinal);
        Assert.DoesNotContain("'HcFgSplit'", narrowed, StringComparison.Ordinal);
        Assert.Contains("'stale_bake'", Sweep("facegen_nonsense"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFamilyIsNotInTheDefaultSetAndTheResponseSpellsWhatAddsIt()
    {
        var defaulted = CheckTools.CheckTool(_w.Svc, max_chars: 60000);
        Assert.DoesNotContain("[facegen]", defaulted, StringComparison.Ordinal);
        Assert.Contains("findings=[\"facegen\"]", defaulted, StringComparison.Ordinal);
    }

    [Fact]
    public void CountsOnlyGivesTheHistogramsAndNoRows()
    {
        var counts = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" }, counts_only: true, max_chars: 60000);
        Assert.Contains("facegen findings by class", counts, StringComparison.Ordinal);
        Assert.Contains("facegen findings by owning mod", counts, StringComparison.Ordinal);
        Assert.DoesNotContain("[STALE_BAKE]", counts, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBoundaryStatesWhatTheFamilyDoesNotClaim()
    {
        var text = Sweep("facegen");
        Assert.Contains("purple or white face", text, StringComparison.Ordinal);
        Assert.Contains("never the render", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonLaneCarriesTheSameRowsAsData()
    {
        using var doc = JsonDocument.Parse(CheckTools.CheckTool(
            _w.Svc, findings: new[] { "facegen" }, format: "json", max_chars: 60000));
        var family = doc.RootElement.GetProperty("families").GetProperty("facegen");
        Assert.True(family.GetProperty("npcs_excluded_no_facegen_race").GetInt32() == 1);
        var classes = family.GetProperty("findings").EnumerateArray()
                            .Select(r => r.GetProperty("class").GetString()).ToList();
        Assert.Contains("tint_absent", classes);
        Assert.Contains("stale_bake", classes);
        Assert.DoesNotContain("family_split", classes);
    }

    [Fact]
    public void ToFileWritesEveryFindingAsAJsonlArtifactAndRendersTheManifest()
    {
        var path = Path.Combine(_w.Root, "facegen.jsonl");
        var text = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" }, to_file: path, max_chars: 60000);
        Assert.Contains("spilled:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[STALE_BAKE]", text, StringComparison.Ordinal);

        var lines = File.ReadAllLines(path);
        using var manifest = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, manifest.RootElement.GetProperty("housecarl_artifact").GetInt32());
        Assert.Contains("mesh_winner", manifest.RootElement.GetProperty("row_schema").EnumerateArray()
                                               .Select(e => e.GetString()));
        var rows = lines.Skip(1).Where(l => l.Length > 0)
                        .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.All(rows, r => Assert.Equal("facegen", r.GetProperty("family").GetString()));
        Assert.Contains(rows, r => r.GetProperty("class").GetString() == "stale_bake");
    }

    [Fact]
    public void ToFileBesideCountsOnlyIsRefusedByName()
    {
        var refusal = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" }, counts_only: true,
                                           to_file: Path.Combine(_w.Root, "never.jsonl"));
        Assert.Contains("counts_only=", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_w.Root, "never.jsonl")));
    }

    [Fact]
    public void APluginScopeSaysTheFileHalfOfThePopulationDidNotRun()
    {
        var scoped = CheckTools.CheckTool(_w.Svc, plugins: new[] { FaceGenWorld.OverhaulName },
                                          findings: new[] { "facegen" }, max_chars: 60000);
        Assert.Contains("only on an UNSCOPED sweep", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("00099999.nif", scoped, StringComparison.Ordinal);
        Assert.Contains("'HcFgStale'", scoped, StringComparison.Ordinal);
    }
}

/// <summary>The pieces that need no world: the path key parse and the class vocabulary.</summary>
[Trait("tier", "unit")]
public sealed class FaceGenClassVocabularyTests
{
    [Theory]
    [InlineData("00013BBF.nif", 0x00, 0x013BBFu)]
    [InlineData("050008AB.nif", 0x05, 0x0008ABu)]
    public void AKeyedFilenameParsesIntoItsIndexByteAndLocalId(string name, byte index, uint local)
    {
        var parsed = FaceGenCheck.ParseKeyFile(name);
        Assert.NotNull(parsed);
        Assert.Equal(index, parsed!.Value.Index);
        Assert.Equal(local, parsed.Value.Local);
    }

    [Theory]
    [InlineData("0013BBF.nif")]
    [InlineData("notahexname.nif")]
    [InlineData("00013BBG.nif")]
    public void AFilenameThatIsNotTheEightHexFormParsesToNothing(string name)
        => Assert.Null(FaceGenCheck.ParseKeyFile(name));

    [Fact]
    public void EveryRegisteredClassRoundTripsThroughItsTokenAndCarriesAFix()
    {
        foreach (var c in FaceGenCheck.Registered)
        {
            Assert.Equal(c, FaceGenCheck.ClassFor(FaceGenCheck.Token(c)));
            Assert.NotEqual("", FaceGenCheck.Fix(c));
            Assert.Contains("'" + FaceGenCheck.Token(c) + "'", FaceGenCheck.Vocabulary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AClassTokenSelectsTheFacegenFamilyNarrowedToThatClass()
    {
        Assert.True(SweepFamilySelection.TryParse(new[] { "split_bake" }, out var sel, out var err));
        Assert.Null(err);
        Assert.Equal(new[] { SweepFamily.Facegen }, sel.Ran);
        Assert.Equal(FaceGenFindingClass.SplitBake, sel.FaceGenClasses);
    }

    [Fact]
    public void TheFamilyTokenSelectsEveryClass()
    {
        Assert.True(SweepFamilySelection.TryParse(new[] { "facegen" }, out var sel, out _));
        Assert.Equal(FaceGenFindingClass.All, sel.FaceGenClasses);
    }
}

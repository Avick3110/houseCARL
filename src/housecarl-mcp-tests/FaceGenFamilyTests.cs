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
        // The benign class the RESPONSE withholds is still in the file: complete findings, with the class column
        // telling them apart, and a total that matches so a consumer cannot read the omission as a cut listing.
        Assert.Contains(rows, r => r.GetProperty("class").GetString() == "family_split");
        Assert.Equal(rows.Count, manifest.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void AWithheldBenignRowDoesNotMakeACompleteListingClaimTheBudgetRanOut()
    {
        var text = Sweep("facegen");
        Assert.DoesNotContain("were listed", text, StringComparison.Ordinal);
        Assert.Contains("family_split=1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludeNarrowsAnUnscopedSweepAndSaysSo()
    {
        // Excluding the master drops every NPC only it touches; HcFgStale stays, because the overhaul the caller
        // kept touches it too — exclude= narrows the selection, not the judgement.
        var text = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" },
                                        exclude: new[] { FaceGenWorld.MasterName }, max_chars: 60000);
        Assert.Contains("exclude= left out 1 plugin(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("'HcFgTintAbsent'", text, StringComparison.Ordinal);
        Assert.Contains("'HcFgStale'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExcludeNamingNothingIsRefusedByName()
    {
        var refusal = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" },
                                           exclude: new[] { "HcFgNoSuch.esp" }, max_chars: 60000);
        Assert.Contains("HcFgNoSuch.esp", refusal, StringComparison.Ordinal);
        Assert.Contains("not in the scope this facegen sweep would cover", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordScopeAlsoTurnsTheFileHalfOff()
    {
        var scoped = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" },
                                          editorid_contains: "HcFgStale", max_chars: 60000);
        Assert.Contains("only on an UNSCOPED sweep", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("00099999.nif", scoped, StringComparison.Ordinal);
        Assert.DoesNotContain("notahexname.nif", scoped, StringComparison.Ordinal);
        Assert.Contains("'HcFgStale'", scoped, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeToFilePathIsRefusedBeforeAnythingIsWritten()
    {
        var refusal = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" }, to_file: "facegen.jsonl");
        Assert.Contains("must be an ABSOLUTE path", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists("facegen.jsonl"));
    }

    [Fact]
    public void AToFileSweepWhoseOnlyFamilyRefusedSaysWhyAndWritesNothing()
    {
        var path = Path.Combine(_w.Root, "refused.jsonl");
        var refusal = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen" },
                                           exclude: new[] { "HcFgNoSuch.esp" }, to_file: path, max_chars: 60000);
        Assert.Contains("not in the scope this facegen sweep would cover", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AToFileSweepStatesARefusedFamilysGroundBesideTheFamiliesThatRan()
    {
        // The dialogue family refuses on cost with no seeds=; the facegen family beside it still answers, so the
        // artifact keeps its rows and the manifest render carries the refusal rather than only the boundary.
        var path = Path.Combine(_w.Root, "mixed.jsonl");
        var text = CheckTools.CheckTool(_w.Svc, findings: new[] { "facegen", "dialogue" },
                                        to_file: path, max_chars: 60000);
        Assert.Contains("spilled:", text, StringComparison.Ordinal);
        Assert.Contains("seeds=", text, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Contains(File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0),
                        l => JsonDocument.Parse(l).RootElement.GetProperty("family").GetString() == "facegen");
    }

    [Fact]
    public void TheDefaultSweepPointsAtTheSpellingThatListsTheWithheldBenignRows()
    {
        var whole = Sweep("facegen");
        Assert.Contains("are NOT listed", whole, StringComparison.Ordinal);
        Assert.Contains("findings=[\"family_split\"]", whole, StringComparison.Ordinal);
        // Named explicitly, the rows are listed and the note becomes the inference caveat instead.
        var named = Sweep("family_split");
        Assert.DoesNotContain("are NOT listed", named, StringComparison.Ordinal);
        Assert.Contains("NAME-BASED inference", named, StringComparison.Ordinal);
    }

    [Fact]
    public void AForeignIndexMeshIsNotVouchedForByACanonicalTintOfTheSameLocalId()
    {
        // facegeom\...\05<id>.nif beside facetint\...\00<id>.dds: two trees, one master folder name. The .dds is
        // not the .nif's canonical file, so the mesh is still a foreign-index bake.
        var name = "05" + _w.Npcs["HcFgMeshAbsent"].ID.ToString("X6") + ".nif";
        var text = Sweep("foreign_index");
        Assert.Contains(name, text, StringComparison.Ordinal);
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

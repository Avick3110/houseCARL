using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A collapsed container's hint names the depth that reaches every leaf under it, measured on the live
/// value, and its <c>[*]</c> path when its children are elements; past the expansion cap it says so instead.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsDepthHintTests : RecordsTestBase
{
    public RecordsDepthHintTests(RecordsFixture f) : base(f) { }

    static readonly Regex HintedDepth = new(@"pass project\.depth=(\d+)(?: with format=text/json)? to reach every leaf under it");

    string Read(string path, int? depth = null, string format = "text") =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, format: format,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { path }, depth = depth });

    static int HintOf(string response)
    {
        var m = HintedDepth.Match(response);
        Assert.True(m.Success, "no measured depth in:\n" + response);
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>The field lines of a text read, without the envelope lines that echo the call's own depth.</summary>
    static string[] Lines(string response) =>
        response.Split('\n').Where(l => l.TrimStart().StartsWith("Effects", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void TheHintedDepthPassedBackReachesEveryLeafAndOneLessDoesNot()
    {
        int n = HintOf(Read("Effects"));
        Assert.True(n >= 3, $"Effects -> element -> Data -> Magnitude is at least three levels; hinted {n}");

        var atN = Lines(Read("Effects", n));
        Assert.Equal(Lines(Read("Effects", n + 4)), atN);
        Assert.NotEqual(Lines(Read("Effects", n - 1)), atN);
        Assert.Contains(atN, l => l.Contains("Effects[0].Data.Magnitude", StringComparison.Ordinal));
    }

    [Fact]
    public void AListsHintNamesItsOwnPathWithTheStarAndThatPathReadsOneRowPerElement()
    {
        Served(Read("Effects"), "name 'Effects[*]' for one row per element");
        var rows = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, format: "dense",
                                        project: Fields("Effects[*].Data.Magnitude"));
        Assert.DoesNotContain("error", rows);
        Assert.Contains("Effects[*].Data.Magnitude", rows);
    }

    [Fact]
    public void ASubstructsHintNamesOnlyTheDepth_ItHasNoElementsToStar()
    {
        var r = Read("Effects[0].Data");
        Assert.Equal(2, HintOf(r));
        Assert.DoesNotContain("[*]", r);
    }

    [Fact]
    public void PastTheExpansionCapTheHintSaysSoRatherThanGuessADepth()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Fields("Items"));
        Served(r, "name 'Items[*]' for one row per element",
               $"no project.depth=N reaches every leaf under it within the {ReadEngine.MaxExpandNodes}-line expansion cap");
        Assert.DoesNotMatch(HintedDepth, r);
    }

    [Fact]
    public void JsonDenseAndToFileCarryTheSameMeasuredDepth()
    {
        int n = HintOf(Read("Effects"));
        Assert.Equal(n, HintOf(Read("Effects", format: "json")));
        Assert.Equal(n, HintOf(Read("Effects", format: "dense")));

        var art = W.Scratch("depth-hint.jsonl");
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Fields("Effects"), to_file: art);
        Assert.Equal(n, HintOf(File.ReadAllText(art)));
    }

    static readonly Regex MainsScanCell = new(@"^\[list: \d+ item\(s\)\] — pass project\.depth=2 to expand$");

    [Fact]
    public void AScansCellsKeepMainsConstantHintInEveryFormat()
    {
        var spel = new[] { "SPEL" };
        var text = RecordsTools.Records(Svc, types: spel, project: Fields("Effects"));
        var cells = text.Split('\n').Where(l => l.TrimStart().StartsWith("Effects = ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(cells);
        Assert.All(cells, l => Assert.Matches(MainsScanCell, l.TrimStart()["Effects = ".Length..].TrimEnd('\r')));

        using var json = JsonDocument.Parse(RecordsTools.Records(Svc, types: spel, format: "json", project: Fields("Effects")));
        var notes = json.RootElement.GetProperty("matches").EnumerateArray()
            .SelectMany(m => m.GetProperty("fields").EnumerateArray())
            .Where(f => f.GetProperty("path").GetString() == "Effects")
            .Select(f => f.GetProperty("note").GetString()!).ToArray();
        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.Matches(MainsScanCell, n));

        var dense = RecordsTools.Records(Svc, types: spel, format: "dense", project: Fields("Effects"));
        Assert.Contains("] — pass project.depth=2 with format=text/json to expand (dense cells are positional)", dense);
        Assert.DoesNotContain("reach every leaf", dense);

        var art = W.Scratch("depth-hint-scan.jsonl");
        RecordsTools.Records(Svc, types: spel, project: Fields("Effects"), to_file: art);
        var file = File.ReadAllText(art);
        Assert.Contains("] — pass project.depth=2 to expand", file);
        Assert.DoesNotContain("reach every leaf", file);
    }

    [Fact]
    public void ANamedRecordsReadKeepsTheMeasuredHintAndAScanOfTheSameRecordDoesNot()
    {
        Assert.Contains("reach every leaf under it", Read("Effects"));
        Assert.DoesNotContain("reach every leaf under it", RecordsTools.Records(Svc, types: new[] { "SPEL" }, project: Fields("Effects")));
    }

    [Fact]
    public void AMeasurementThatThrowsSaysTheConstantHintNeverNothing()
    {
        Assert.Equal(" — pass project.depth=2 to expand",
                     LeverNames.Records.NamedContainerHint.For("Effects", () => throw new InvalidOperationException(), ReadEngine.MaxExpandNodes));
        Assert.Equal(" — pass project.depth=2 with format=text/json to expand (dense cells are positional)",
                     LeverNames.Records.NamedDenseContainerHint.For("Effects", () => throw new InvalidOperationException(), ReadEngine.MaxExpandNodes));
    }

    [Fact]
    public void TheDefaultDepthStillPrintsOnlyTheCollapsedLine()
    {
        var lines = Lines(Read("Effects"));
        Assert.Single(lines);
        Assert.StartsWith("Effects = [list: ", lines[0].TrimStart());
    }
}

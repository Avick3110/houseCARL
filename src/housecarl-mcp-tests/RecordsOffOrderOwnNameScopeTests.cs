using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Over an out-of-load-order source= file, a plugins= name equal to the file's own filename means that file
/// and keeps every record it holds (#1389). It used to mean the active copy of that name, so a shadowed copy's
/// own records dropped out of the selection silently, and a swapped delta reported no removed records.</summary>
[Trait("tier", "integration")]
public sealed class RecordsOffOrderOwnNameScopeTests : IClassFixture<ShadowedCopyFixture>
{
    readonly ShadowedCopyWorld _w;
    public RecordsOffOrderOwnNameScopeTests(ShadowedCopyFixture f) => _w = f.W;

    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();
    JsonElement Loser => Je("{\"file\": \"" + _w.FileName + "\", \"mod\": \"" + _w.LoserMod + "\"}");
    JsonElement Served => Je("\"" + _w.FileName + "\"");
    static RecordsTools.RecordsScope Scope(params string[] names) => new() { names = names };
    static RecordsTools.RecordsProject Delta => new() { form = "delta", fields = new[] { "EditorID" } };

    static void Ok(string r) => Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);

    [Fact]
    public void TheFilesOwnNameSelectsEveryRecordOfTheShadowedCopy()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Loser, counts_only: true);
        Ok(r);
        Assert.Contains($"scan: {ShadowedCopyWorld.LoserCount} matches", r);
    }

    [Fact]
    public void TheOwnNameIsMatchedWithoutRegardToCase()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName.ToUpperInvariant()), source: Loser, counts_only: true);
        Assert.Contains($"scan: {ShadowedCopyWorld.LoserCount} matches", r);
    }

    /// <summary>The issue's call: the old copy as source, the served copy as versus. errors is the records only the old
    /// copy holds.</summary>
    [Fact]
    public void TheSwappedDeltaCountsTheRecordOnlyTheShadowedCopyHoldsAsAnError()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Loser, versus: Served,
                                     project: Delta, counts_only: true);
        Ok(r);
        Assert.Contains($"{ShadowedCopyWorld.LoserCount} match(es) selected from the file", r);
        Assert.Contains($"count={ShadowedCopyWorld.LoserCount} ", r);
        Assert.Contains("errors=1", r);
    }

    [Fact]
    public void TheSwappedDeltaRowNamesTheRecordOnlyTheShadowedCopyHolds()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Loser, versus: Served, project: Delta);
        var line = Assert.Single(r.Split('\n'), l => l.Contains("does not define or override", StringComparison.Ordinal));
        Assert.Contains(_w.LoserOnlyFid, line);
    }

    [Fact]
    public void TheAggregateOverTheOwnNameCountsEveryRecord()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Loser,
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
        Ok(r);
        Assert.Contains($"{ShadowedCopyWorld.LoserCount} matches", r);
    }

    [Fact]
    public void ToFileOverTheOwnNameWritesEveryRecordOfTheFile()
    {
        var dir = Path.Combine(_w.Root, "out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "own-name.jsonl");
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Loser, to_file: path);
        Ok(r);
        Assert.Contains(_w.LoserOnlyFid, File.ReadAllText(path));
    }

    [Fact]
    public void TheOwnNameBesideAnotherPluginStillSelectsTheWholeFile()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName, _w.MasterName), source: Loser, counts_only: true);
        Assert.Contains($"scan: {ShadowedCopyWorld.LoserCount} matches", r);
    }

    [Fact]
    public void TheOwnNameWithDefinedInKeepsOnlyTheRecordsTheFileDefines()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: new RecordsTools.RecordsScope { names = new[] { _w.FileName }, defined_in = true },
                                     source: Loser, counts_only: true);
        Assert.Contains("scan: 1 match ", r);
    }

    /// <summary>Pins unchanged behaviour: another active plugin's name keeps only the file's records it touches.</summary>
    [Fact]
    public void AnotherActivePluginsNameStillKeepsOnlyTheRecordsItTouches()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.MasterName), source: Loser, counts_only: true);
        Assert.Contains("scan: 2 matches", r);
    }

    /// <summary>Pins unchanged behaviour: the served copy as source and the old copy as versus; errors is the record
    /// only the served copy holds.</summary>
    [Fact]
    public void TheServedCopyAsSourceCountsTheRecordOnlyItHoldsAsAnError()
    {
        var r = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Served, versus: Loser,
                                     project: Delta, counts_only: true);
        Ok(r);
        Assert.Contains($"count={ShadowedCopyWorld.WinnerCount} ", r);
        Assert.Contains("errors=1", r);
        var rows = RecordsTools.Records(_w.Svc, plugins: Scope(_w.FileName), source: Served, versus: Loser, project: Delta);
        var line = Assert.Single(rows.Split('\n'), l => l.Contains("does not define or override", StringComparison.Ordinal));
        Assert.Contains(_w.WinnerOnlyFid, line);
    }

    /// <summary>Pins unchanged behaviour: types= alone reads the whole file.</summary>
    [Fact]
    public void TypesAloneStillSelectsTheWholeFile()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, source: Loser, counts_only: true);
        Assert.Contains($"scan: {ShadowedCopyWorld.LoserCount} matches", r);
    }

    static string ParamText(string name) =>
        typeof(RecordsTools).GetMethod(nameof(RecordsTools.Records), BindingFlags.Public | BindingFlags.Static)!
            .GetParameters().Single(p => p.Name == name).GetCustomAttribute<DescriptionAttribute>()!.Description;

    [Fact]
    public void ThePluginsTextSaysWhatTheScopeMeansOverAnOffOrderFile()
    {
        var text = ParamText("plugins");
        Assert.Contains("out-of-load-order source= file", text);
        Assert.Contains("ACTIVE plugins also touch", text);
        Assert.Contains("the file's own filename keeps every record the file holds", text);
    }

    [Fact]
    public void TheVersusTextSaysErrorsHoldsTheRecordsTheReferenceLacksAndTheSwap()
    {
        var text = ParamText("versus");
        Assert.Contains("the reference has no version of is an errors row", text);
        Assert.Contains("source= and versus= swapped", text);
    }
}

/// <summary>The own name over a file whose mod is switched off: the order does not carry the name at all.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsOffOrderOwnNameDisabledModTests : RecordsTestBase
{
    public RecordsOffOrderOwnNameDisabledModTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void TheOwnNameOfAFileInADisabledModAnswersTheFilesRecords()
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.OldName), source: Plugin(W.OldName), counts_only: true);
        Assert.DoesNotContain("names nothing the load order carries", r);
        Assert.Contains("scan: 2 matches", r);
    }

    /// <summary>The rule sentence a missing name carries names the own-name exception too.</summary>
    [Fact]
    public void TheMissingNameSentenceNamesTheOwnNameRoute()
    {
        var r = RecordsTools.Records(Svc, plugins: Scope(W.MasterName, "HcNotInstalled.esp"), types: new[] { "WEAP" },
                                     source: Plugin(W.OldName));
        Assert.Contains("or the file's own name ('" + W.OldName + "') to keep every record it holds", r);
    }
}

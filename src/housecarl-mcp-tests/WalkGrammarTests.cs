using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One walk grammar (#1317): the records walk and the asset walk take the same walk values under the same
/// names and refuse them in the same words, every walk answers a seed that is not a FormID with its own row, and the
/// asset walk's depth, budget, through and exclusions move what it enters. The shared cases run against both walks.</summary>
[Trait("tier", "unit")]
public sealed class WalkGrammarTests : IClassFixture<AssetWalkDoorWorld>
{
    readonly AssetWalkDoorWorld _w;
    public WalkGrammarTests(AssetWalkDoorWorld w) => _w = w;

    static readonly string[] BothPlugins = { AssetWalkDoorWorld.A.FileName.String, AssetWalkDoorWorld.B.FileName.String };
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    string Id(string edid) => FormIdToken.Of(_w.Keys[edid]);

    string RecordsWalk(string walkJson, string seed = "HcDoorA1", string? format = null)
        => RecordsTools.Records(_w.Svc, formids: new[] { Id(seed) }, walk: JsonSerializer.Deserialize<RecordsTools.RecordsWalk>(walkJson, Wire),
                                counts_only: true, format: format);

    string AssetWalk(string walkJson, string[]? records = null, string[]? formids = null, string? format = "json", bool countsOnly = false)
        => AssetTools.AssetStatus(_w.Svc, records: records ?? new[] { Id("HcDoorA1") }, formids: formids, walk: Walks.Of(walkJson),
                                  from_source: BothPlugins, format: format, counts_only: countsOnly, max_chars: 400_000);

    /// <summary>Each walk value's refusal, the same sentence from both walks; the records half is the records walk's
    /// words on main, unchanged.</summary>
    public static TheoryData<string, string> SharedRefusals => new()
    {
        { "{\"depth\": 0}", "error: walk.depth=0 — depth must be >= 1 (hops from the seed)." },
        { "{\"max_nodes\": 0}", "error: walk.max_nodes=0 — the node budget must be >= 1." },
        { "{\"through\": []}", "error: walk.through is empty — name the record types the walk expands through (e.g. [\"LeveledItem\", \"Outfit\"]), or omit it to walk every type." },
        { "{\"through\": [\"TextureSets\"]}", "error: walk.through 'TextureSets' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI')." },
        { "{\"exclusions\": [{\"match\": \"TextureSets\", \"severity\": \"stop\"}]}", "error: walk.exclusions 'TextureSets' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI')." },
        { "{\"exclusions\": [{\"match\": \" \", \"severity\": \"stop\"}]}", "error: a walk.exclusions entry needs match= — the record type name a read reports (e.g. 'Race')." },
        { "{\"exclusions\": [{\"match\": \"Race\", \"severity\": \"skip\"}]}", "error: walk.exclusions 'Race': severity='skip' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud)." },
        { "{\"exclusions\": [{\"match\": \"Race\"}]}", "error: walk.exclusions 'Race': severity='' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud)." },
    };

    [Theory, MemberData(nameof(SharedRefusals))]
    public void TheRecordsWalkRefusesAWalkValueInItsWords(string walk, string sentence) => Assert.Equal(sentence, RecordsWalk(walk));

    [Theory, MemberData(nameof(SharedRefusals))]
    public void TheAssetWalkRefusesAWalkValueInTheRecordsWalksWords(string walk, string sentence) => Assert.Equal(sentence, AssetWalk(walk, format: null));

    /// <summary>The per-seed ceiling is the forward walk's; the asset walk's budget is shared, so it takes the value.</summary>
    [Fact]
    public void OnlyAPerSeedBudgetHasACeiling()
    {
        Assert.Equal("error: walk.max_nodes=250001 — the node budget's hard upper bound is 250000; pass that or less, which is above the whole closure a single seed reaches on a large order.",
                     RecordsWalk("{\"max_nodes\": 250001}"));
        Assert.DoesNotContain("error:", AssetWalk("{\"max_nodes\": 250001}", countsOnly: true));
    }

    /// <summary>The records walk's defaults stand: an unset depth walks 16 hops.</summary>
    [Fact]
    public void TheRecordsWalksDefaultDepthIsSixteen()
        => Assert.Contains("\"walk\": \"forward (closure) depth=16\"",
                           RecordsTools.Records(_w.Svc, formids: new[] { Id("HcDoorA1") }, walk: new(), project: new() { form = "chain" }, format: "json"));

    static string[] Notes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!).ToArray();
    }

    static (string Path, string Edge, string PulledBy, string? Error)[] Nodes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("nodes").EnumerateArray()
                  .Select(n => (n.GetProperty("path").GetString()!, n.GetProperty("edge").GetString()!, n.GetProperty("pulled_by").GetString()!,
                                n.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null))
                  .ToArray();
    }

    /// <summary>walk.depth=1 enters the seed's first hop only, and the note names the value and says to raise it.</summary>
    [Fact]
    public void AssetWalkDepthOneStopsAtHopOne()
        => Assert.Equal(new[] { "1 record(s) of a from_source plugin, or of a seed's own plugin when 'winner' read it, sit more than walk.depth=1 form-link hops from a seed and were not entered; raise walk.depth to walk them." },
                        Notes(AssetWalk("{\"depth\": 1}", records: new[] { Id("HcDoorChain2") }, countsOnly: true)));

    /// <summary>walk.max_nodes counts every record entered, seeds included, one budget across both seeds.</summary>
    [Fact]
    public void AssetWalkMaxNodesIsOneBudgetCountingSeeds()
    {
        var json = AssetWalk("{\"max_nodes\": 3}", records: new[] { Id("HcDoorChain0"), Id("HcDoorChain3") }, countsOnly: true);
        // Two seeds and Chain1 are entered; Chain4, the next, is the fourth.
        Assert.Equal(new[] { "the walk stopped entering records at walk.max_nodes=3 (every record entered counts, seeds included); raise walk.max_nodes to walk further." },
                     Notes(json));
        Assert.Contains("\"truncated\":true", json.Replace(" ", ""));
    }

    /// <summary>walk.through enters only the listed types and counts the rest per type; the seed NPC is still entered.</summary>
    [Fact]
    public void AssetWalkThroughLeavesOutOtherTypesAndCountsThem()
    {
        var json = AssetWalk("{\"through\": [\"HeadPart\", \"TextureSet\"]}");
        Assert.Contains("left out, not in walk.through: 1 Race, 1 VoiceType", Notes(json));
        var nodes = Nodes(json);
        Assert.Contains(nodes, n => n.Edge == "facegen" && n.PulledBy.StartsWith("Npc HcDoorA1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, n => n.PulledBy.StartsWith("Race ", StringComparison.Ordinal) || n.PulledBy.StartsWith("VoiceType ", StringComparison.Ordinal));
    }

    /// <summary>A stop exclusion keeps the race out, counted as a boundary; a refuse fails the call in the walks' words.</summary>
    [Fact]
    public void AssetWalkExclusionsStopAndRefuse()
    {
        var stop = AssetWalk("{\"exclusions\": [{\"match\": \"Race\", \"severity\": \"stop\"}]}");
        Assert.Contains("1 reached record(s) matched a stop exclusion and were kept as boundaries, not entered (no files, no links)", Notes(stop));
        Assert.DoesNotContain(Nodes(stop), n => n.PulledBy.StartsWith("Race ", StringComparison.Ordinal));

        var refuse = AssetWalk("{\"exclusions\": [{\"match\": \"RACE\", \"severity\": \"refuse\"}]}", format: null);
        Assert.StartsWith("error: the walk reached a Race (", refuse);
        Assert.EndsWith("a node class this call excludes with severity 'refuse'. Nothing is returned for this call.", refuse);
    }

    /// <summary>The records walk's shaping values are refused by name on the asset walk, never dropped.</summary>
    [Theory]
    [InlineData("{\"seed_paths\": [\"HeadParts\"]}", "error: walk.seed_paths is a records-walk value")]
    [InlineData("{\"follow\": \"Template\"}", "error: walk.follow is a records-walk value")]
    [InlineData("{\"direction\": \"forward\"}", "error: walk.direction is a records-walk value")]
    public void AssetWalkRefusesTheRecordsWalksShapingValues(string walk, string lead)
        => Assert.StartsWith(lead, AssetWalk(walk, format: null));

    /// <summary>walk=true and an empty walk object answer the same walk.</summary>
    [Fact]
    public void AssetWalkTrueIsTheDefaults() => Assert.Equal(AssetWalk("true"), AssetWalk("{}"));

    /// <summary>Walk values with no records= seed that parses are said unused; walk=true and a records= seed say nothing.</summary>
    [Fact]
    public void AssetWalkValuesWithNoRecordSeedAreSaidUnused()
    {
        const string unused = ReadSentences.WalkValuesWithoutRecordSeed;
        Assert.Contains(unused, Notes(AssetWalk("{\"through\": [\"HeadPart\"]}", records: Array.Empty<string>(), formids: new[] { Id("HcDoorA1") })));
        Assert.Contains(unused, Notes(AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { "meshes/hc/none.nif" }, walk: Walks.Of("{\"depth\": 1}"), format: "json")));
        Assert.DoesNotContain(unused, Notes(AssetWalk("true", records: Array.Empty<string>(), formids: new[] { Id("HcDoorA1") })));
        Assert.DoesNotContain(unused, Notes(AssetWalk("{\"depth\": 1}")));
    }

    /// <summary>from_source= with only a bad records= seed reads nothing through it, and says so.</summary>
    [Fact]
    public void AssetWalkFromSourceWithOnlyABadSeedIsSaidUnread()
    {
        Assert.Contains(ReadSentences.FromSourceWithoutRecordSeed, Notes(AssetWalk("true", records: new[] { "ZZZ" })));
        Assert.DoesNotContain(ReadSentences.FromSourceWithoutRecordSeed, Notes(AssetWalk("true")));
    }

    /// <summary>A seed that is not a FormID is one error node at its token, edge 'seed'; the good seed walks.</summary>
    [Fact]
    public void AssetWalkGivesABadRecordsSeedItsOwnNode()
    {
        var nodes = Nodes(AssetWalk("true", records: new[] { "ZZZ", Id("HcDoorA1") }));
        var bad = Assert.Single(nodes, n => n.Error is not null);
        Assert.Equal(("ZZZ", "seed"), (bad.Path, bad.Edge));
        Assert.StartsWith("bad FormID 'ZZZ'", bad.Error);
        Assert.EndsWith(" Expected 'XXXXXX:Plugin.esp'.", bad.Error);
        Assert.Contains(nodes, n => n.Edge == "facegen" && n.PulledBy.StartsWith("Npc HcDoorA1 ", StringComparison.Ordinal));
    }

    [Fact]
    public void AssetWalkGivesABadFormidsSeedItsOwnNode()
    {
        var nodes = Nodes(AssetWalk("true", records: Array.Empty<string>(), formids: new[] { "ZZZ", Id("HcDoorA1") }));
        var bad = Assert.Single(nodes, n => n.Error is not null);
        Assert.Equal(("ZZZ", "seed"), (bad.Path, bad.Edge));
        Assert.EndsWith(" Expected 'XXXXXX:Plugin.esp'.", bad.Error);
        Assert.Contains(nodes, n => n.Edge == "seed" && n.Error is null);
    }

    /// <summary>The transitive reverse walk lists the bad seed as one error row and the good seed's referrers.</summary>
    [Fact]
    public void TheReverseWalkGivesABadSeedItsOwnRow()
    {
        var text = RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { direction = "reverse", depth = 1 });
        Assert.Contains("bad FormID 'ZZZ'", text);
        Assert.Contains("HcDoorPlacedA1", text);
        Assert.Contains("errors=1", RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { direction = "reverse", depth = 1 }, counts_only: true));
    }

    static string ReverseCounts(string text)
        => System.Text.RegularExpressions.Regex.Match(text, @"selection = \d+ record\(s\) \(\d+ referrer\(s\), seeds included\)").Value;

    /// <summary>The transitive reverse header counts records only, and an all-bad seed list walks nothing for want of a
    /// FormID, not of expansion.</summary>
    [Fact]
    public void TheReverseHeaderDoesNotCountABadSeed()
    {
        string Run(params string[] ids) => RecordsTools.Records(_w.Svc, formids: ids, walk: new() { direction = "reverse" });
        var good = ReverseCounts(Run(Id("HcDoorA1")));
        Assert.NotEqual("", good);
        Assert.Equal(good, ReverseCounts(Run("ZZZ", Id("HcDoorA1"))));

        var bad = Run("ZZZ");
        Assert.Contains("bad FormID 'ZZZ'", bad);
        Assert.Contains("selection = 0 record(s) (0 referrer(s), seeds included)", bad);
        Assert.DoesNotContain("nothing left to expand", bad);
    }

    /// <summary>The forward walk's reading form lists the bad seed as one error row, in text and json.</summary>
    [Fact]
    public void TheForwardReadingFormGivesABadSeedItsOwnRow()
    {
        var counts = RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { depth = 1 }, counts_only: true);
        Assert.Contains("errors=1", counts);
        Assert.Contains("bad FormID 'ZZZ'", RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { depth = 1 }));
        using var doc = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { depth = 1 }, format: "json"));
        var rows = doc.RootElement.GetProperty("records").EnumerateArray().ToList();
        Assert.Single(rows, r => r.TryGetProperty("error", out var e) && e.GetString()!.StartsWith("bad FormID 'ZZZ'", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.GetProperty("formid").GetString() == Id("HcDoorA1"));
    }

    static string WalkEnvelope(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("walk").GetString()!;
    }

    /// <summary>The forward reading form's header counts records only: the bad seed is a row here, not a reached record
    /// and not a seed error left to form='chain'.</summary>
    [Fact]
    public void TheForwardReadingFormDoesNotCountABadSeed()
    {
        string Run(string? format, params string[] ids) => RecordsTools.Records(_w.Svc, formids: ids, walk: new() { depth = 1 }, format: format);
        var good = Run(null, Id("HcDoorA1"));
        var line = good.Split('\n').Single(l => l.StartsWith("walk: selection = ", StringComparison.Ordinal));
        Assert.Contains(line, Run(null, "ZZZ", Id("HcDoorA1")).Split('\n'));
        var env = WalkEnvelope(Run("json", "ZZZ", Id("HcDoorA1")));
        Assert.Equal(WalkEnvelope(Run("json", Id("HcDoorA1"))), env);
        Assert.DoesNotContain("form='chain'", env);
    }

    /// <summary>All seeds malformed: only their error rows, not the 'reached nothing readable' refusal.</summary>
    [Fact]
    public void AllBadSeedsAreOnlyErrorRows()
        => Assert.Contains("errors=1", RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ" }, walk: new() { depth = 1 }, counts_only: true));

    /// <summary>Pins unchanged behaviour: the chain form keeps its own per-seed error row.</summary>
    [Fact]
    public void TheChainFormKeepsItsPerSeedRow()
        => Assert.Contains("ZZZ  error=bad FormID 'ZZZ'", RecordsTools.Records(_w.Svc, formids: new[] { "ZZZ", Id("HcDoorA1") }, walk: new() { depth = 1 }, project: new() { form = "chain" }));
}

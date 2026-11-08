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

    string AssetWalk(string walkJson, string[]? records = null, string[]? formids = null, string? format = "json", bool countsOnly = false, string[]? fromSource = null)
        => AssetTools.AssetStatus(_w.Svc, records: records ?? new[] { Id("HcDoorA1") }, formids: formids, walk: Walks.Of(walkJson),
                                  from_source: fromSource ?? BothPlugins, format: format, counts_only: countsOnly, max_chars: 400_000);

    /// <summary>Each walk value's refusal, the same sentence from both walks; the records half is the records walk's
    /// words on main, unchanged.</summary>
    public static TheoryData<string, string> SharedRefusals => new()
    {
        { "{\"depth\": 0}", "error: walk.depth=0 — depth must be >= 1 (hops from the seed)." },
        { "{\"max_nodes\": 0}", "error: walk.max_nodes=0 — the node budget must be >= 1." },
        { "{\"exclusions\": [{\"match\": \"TextureSets\", \"severity\": \"stop\"}]}", "error: walk.exclusions 'TextureSets' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI')." },
        { "{\"exclusions\": [{\"match\": \" \", \"severity\": \"stop\"}]}", "error: a walk.exclusions entry needs match= — the record type name a read reports (e.g. 'Race')." },
        { "{\"exclusions\": [{\"match\": \"Race\", \"severity\": \"skip\"}]}", "error: walk.exclusions 'Race': severity='skip' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud)." },
        { "{\"exclusions\": [{\"match\": \"Race\"}]}", "error: walk.exclusions 'Race': severity='' — use 'stop' (prune, record the boundary) or 'refuse' (the whole walk fails loud)." },
    };

    [Theory, MemberData(nameof(SharedRefusals))]
    public void TheRecordsWalkRefusesAWalkValueInItsWords(string walk, string sentence) => Assert.Equal(sentence, RecordsWalk(walk));

    [Theory, MemberData(nameof(SharedRefusals))]
    public void TheAssetWalkRefusesAWalkValueInTheRecordsWalksWords(string walk, string sentence) => Assert.Equal(sentence, AssetWalk(walk, format: null));

    /// <summary>The asset walk's empty-through and edge-kind exclusion refusals name the edge kinds (#1278, r1 #31,
    /// #43); the records walk's and copy's words for the same input are main's, unchanged.</summary>
    [Fact]
    public void TheAssetWalksRefusalsNameTheEdgeKinds()
    {
        Assert.Equal("error: walk.through is empty — name the record types the walk expands through (e.g. [\"LeveledItem\", \"Outfit\"]), or omit it to walk every type.",
                     RecordsWalk("{\"through\": []}"));
        Assert.Equal("error: walk.through is empty — name the record types the walk expands through (e.g. [\"LeveledItem\", \"Outfit\"]), or the edge kinds whose rows to show (e.g. [\"texture\", \"script\"]), or omit it to walk every type but an NPC reached past a seed, kept as a boundary.",
                     AssetWalk("{\"through\": []}", format: null));
        const string Voice = "{\"exclusions\": [{\"match\": \"voice\", \"severity\": \"stop\"}]}";
        Assert.Equal("error: walk.exclusions 'voice' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').",
                     RecordsWalk(Voice));
        Assert.Equal("error: exclusions 'voice' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').",
                     WalkGrammar.ParseExclusions(_w.Svc.Types, Walks.Excl("voice:stop"), "exclusions", out _));
        Assert.Equal("error: walk.exclusions 'voice' is an edge kind — exclusions take record types; name the edge kinds whose rows to show in walk.through (e.g. [\"texture\", \"script\"]).",
                     AssetWalk(Voice, format: null));
    }

    /// <summary>walk.through's tokens: the records walk refuses a token that is no type in its words on main, and an edge
    /// kind by name; the asset walk refuses a token that is neither kind of value naming both, and 'seed', which is always
    /// shown (#1278).</summary>
    [Fact]
    public void ThroughRefusesATokenNamingWhatItTakes()
    {
        Assert.Equal("error: walk.through 'TextureSets' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').",
                     RecordsWalk("{\"through\": [\"TextureSets\"]}"));
        Assert.Equal("error: walk.through 'seed' is not a record type — pass a catalog name (e.g. 'Npc', 'LeveledItem') or a signature (e.g. 'NPC_', 'LVLI').",
                     RecordsWalk("{\"through\": [\"seed\"]}"));
        Assert.Equal($"error: walk.through 'Texture' is an asset walk edge kind — the records walk follows form links only, so its through takes record types (e.g. 'Npc', 'LVLI'); name the file edge kinds on {ToolNames.AssetStatus} walk=.",
                     RecordsWalk("{\"through\": [\"Npc\", \"Texture\"]}"));
        Assert.Equal("error: walk.through 'TextureSets' is neither a record type nor an edge kind — pass a catalog name (e.g. 'Npc', 'LeveledItem'), a signature (e.g. 'NPC_', 'LVLI'), or an edge kind: listed, script, seq, inferred, fragment, facegen, voice, lip, texture, smp, bodytri, parent.",
                     AssetWalk("{\"through\": [\"TextureSets\"]}", format: null));
        Assert.Equal("error: walk.through 'seed' — the seed rows are always shown; name the edge kinds whose rows to show beside them (e.g. [\"texture\", \"script\"]).",
                     AssetWalk("{\"through\": [\"texture\", \"SEED\"]}", format: null));
    }

    /// <summary>One home for the edge kinds (#1278): every kind but seed is a through token, each spelled as the walk
    /// writes it, a mesh link's kind is the same token links= writes, and the tool text names every one.</summary>
    [Fact]
    public void ThroughTakesEveryEdgeKindTheWalkEmits()
    {
        var tokens = Enum.GetValues<WalkEdgeKind>().Where(k => k != WalkEdgeKind.Seed).Select(k => k.Token()).ToList();
        Assert.Equal(tokens, WalkGrammar.EdgeKindList.Split(", "));
        foreach (var token in tokens)
        {
            Assert.Null(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of($"{{\"through\": [\"{token}\"]}}"), out var v));
            Assert.Equal(token, Assert.Single(v!.Edges!).Token());
            Assert.Null(v.Through);
        }
        foreach (var kind in Enum.GetValues<NifLinkKind>()) Assert.Equal(kind.Token(), kind.Edge().Token());
        var text = typeof(AssetTools).GetMethod(nameof(AssetTools.AssetStatus))!.GetParameters().Single(p => p.Name == "walk")
                                     .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Cast<System.ComponentModel.DescriptionAttribute>().Single().Description;
        Assert.Contains("(" + WalkGrammar.EdgeKindList + ")", text);
    }

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
        => Assert.Equal(new[] { "1 record(s) sit more than walk.depth=1 hops from a seed and were not entered; raise walk.depth to walk them (an NPC among them is then a boundary unless walk.through names Npc)." },
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

    /// <summary>walk.through enters only the listed types and counts the rest per type; the seed NPC is still entered.
    /// Under walk.follow "*", which reaches the race and voice type.</summary>
    [Fact]
    public void AssetWalkThroughLeavesOutOtherTypesAndCountsThem()
    {
        var json = AssetWalk("{\"follow\": \"*\", \"through\": [\"HeadPart\", \"TextureSet\"]}");
        Assert.Contains("left out, not in walk.through: 1 Race, 1 VoiceType", Notes(json));
        var nodes = Nodes(json);
        Assert.Contains(nodes, n => n.Edge == "facegen" && n.PulledBy.StartsWith("Npc HcDoorA1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(nodes, n => n.PulledBy.StartsWith("Race ", StringComparison.Ordinal) || n.PulledBy.StartsWith("VoiceType ", StringComparison.Ordinal));
    }

    /// <summary>through's edge kinds count the rows they hide on the record types' one line, in its place (#1278): before
    /// the links-not-followed note, as with the types alone, and every other note the same.</summary>
    [Fact]
    public void AssetWalkEdgeKindsCountOnTheOneLeftOutLine()
    {
        string[] Run(string walk) => Notes(AssetWalk(walk, records: new[] { Id("HcDoorSeed") }, countsOnly: true, fromSource: new[] { BothPlugins[1] }));
        var types = Run("{\"through\": [\"Npc\"]}");
        var both = Run("{\"through\": [\"Npc\", \"facegen\"]}");
        int at = Array.FindIndex(types, n => n.StartsWith("left out, not in walk.through: "));
        Assert.True(at >= 0 && at < Array.FindIndex(types, n => n.Contains("sit outside the part fields")), string.Join("\n", types));
        Assert.Equal(at, Array.FindIndex(both, n => n.StartsWith("left out, not in walk.through: ")));
        Assert.Equal(types.Where((_, i) => i != at), both.Where((_, i) => i != at));
    }

    /// <summary>A stop exclusion keeps the race out, counted as a boundary; a refuse fails the call in the walks' words.
    /// Under walk.follow "*", which reaches the race.</summary>
    [Fact]
    public void AssetWalkExclusionsStopAndRefuse()
    {
        var stop = AssetWalk("{\"follow\": \"*\", \"exclusions\": [{\"match\": \"Race\", \"severity\": \"stop\"}]}");
        Assert.Contains("1 reached record(s) matched a stop exclusion and were kept as boundaries, not entered (no files, no links).", Notes(stop));
        Assert.DoesNotContain(Nodes(stop), n => n.PulledBy.StartsWith("Race ", StringComparison.Ordinal));

        var refuse = AssetWalk("{\"follow\": \"*\", \"exclusions\": [{\"match\": \"RACE\", \"severity\": \"refuse\"}]}", format: null);
        Assert.StartsWith("error: the walk reached a Race (", refuse);
        Assert.EndsWith("a node class this call excludes with severity 'refuse'. Nothing is returned for this call.", refuse);
    }

    const string PastDepthOne = "sit more than walk.depth=1 hops";

    /// <summary>A type walk.through leaves out is not entered: its own links are not followed past it.</summary>
    [Fact]
    public void AssetWalkThroughDoesNotEnterALeftOutRecord()
    {
        var notes = Notes(AssetWalk("{\"depth\": 1, \"through\": [\"Npc\"]}", records: new[] { Id("HcDoorChain0") }, countsOnly: true));
        Assert.Contains("left out, not in walk.through: 1 FormList", notes);
        Assert.DoesNotContain(notes, n => n.Contains(PastDepthOne, StringComparison.Ordinal));
    }

    /// <summary>A stop exclusion's record is not entered: its own links are not followed past it.</summary>
    [Fact]
    public void AssetWalkStopDoesNotEnterTheBoundary()
    {
        var notes = Notes(AssetWalk("{\"depth\": 1, \"exclusions\": [{\"match\": \"FormList\", \"severity\": \"stop\"}]}", records: new[] { Id("HcDoorChain0") }, countsOnly: true));
        Assert.Contains("1 reached record(s) matched a stop exclusion and were kept as boundaries, not entered (no files, no links).", notes);
        Assert.DoesNotContain(notes, n => n.Contains(PastDepthOne, StringComparison.Ordinal));
    }

    /// <summary>A seed is never judged: an exclusion naming the seed's own type still walks it.</summary>
    [Fact]
    public void AssetWalkNeverJudgesASeed()
    {
        var json = AssetWalk("{\"exclusions\": [{\"match\": \"Npc\", \"severity\": \"stop\"}]}");
        Assert.Contains(Nodes(json), n => n.Edge == "facegen" && n.PulledBy.StartsWith("Npc HcDoorA1 ", StringComparison.Ordinal));
        Assert.DoesNotContain(Notes(json), n => n.Contains("matched a stop exclusion", StringComparison.Ordinal));
    }

    /// <summary>A record a stop exclusion keeps out is not entered, so it does not spend walk.max_nodes.</summary>
    [Fact]
    public void AssetWalkStopSpendsNoBudget()
    {
        var json = AssetWalk("{\"max_nodes\": 2, \"exclusions\": [{\"match\": \"FormList\", \"severity\": \"stop\"}]}",
                             records: new[] { Id("HcDoorChain0"), Id("HcDoorChain3") }, countsOnly: true);
        Assert.Contains("2 reached record(s) matched a stop exclusion and were kept as boundaries, not entered (no files, no links).", Notes(json));
        Assert.DoesNotContain(Notes(json), n => n.Contains("walk.max_nodes=2", StringComparison.Ordinal));
    }

    /// <summary>An all-bad records= seed list walks to its error node, not the 'no seed' refusal.</summary>
    [Fact]
    public void AssetWalkWithOnlyABadSeedIsItsErrorNode()
    {
        var bad = Assert.Single(Nodes(AssetWalk("true", records: new[] { "ZZZ" })));
        Assert.Equal(("ZZZ", "seed"), (bad.Path, bad.Edge));
        Assert.NotNull(bad.Error);
    }

    /// <summary>The carrier walk reads max_nodes per seed, so its ceiling holds; the transitive reverse shares one budget.</summary>
    [Fact]
    public void TheCarrierWalkBudgetHasTheCeiling()
    {
        string Run(string follow) => RecordsTools.Records(_w.Svc, formids: new[] { Id("HcDoorA1") },
            walk: new() { direction = "reverse", follow = follow, max_nodes = 250001 }, counts_only: true);
        Assert.StartsWith("error: walk.max_nodes=250001 — the node budget's hard upper bound is 250000", Run("Effects[].BaseEffect"));
        Assert.DoesNotContain("hard upper bound", Run("*"));
    }

    /// <summary>Both walks' unset node budget is 2000.</summary>
    [Fact]
    public void TheUnsetBudgetIsTwoThousandOnBothWalks()
    {
        Assert.Null(WalkGrammar.ReadAssetWalk(_w.Svc.Types, Walks.Of("{}"), out var asset));
        Assert.Equal(2000, asset!.MaxNodes);
        Assert.Null(WalkGrammar.Parse(_w.Svc.Types, null, null, null, null, WalkGrammar.RecordsDefaultDepth, WalkGrammar.Budget.PerSeed, out var records));
        Assert.Equal(2000, records.MaxNodes);
    }

    /// <summary>walk=false is no walk: the call answers as with walk unset.</summary>
    [Fact]
    public void AssetWalkFalseIsNoWalk()
    {
        string Run(System.Text.Json.JsonElement? walk) => AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { "meshes/hc/none.nif" }, walk: walk);
        var plain = Run(null);
        Assert.DoesNotContain("error:", plain);
        Assert.Equal(plain, Run(Walks.Of("false")));
    }

    /// <summary>The records walk's shaping values are refused by name on the asset walk, never dropped.</summary>
    [Theory]
    [InlineData("{\"seed_paths\": [\"HeadParts\"]}", "error: walk.seed_paths is a records-walk value")]
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
        Assert.Contains(unused, Notes(AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { "meshes/hc/none.nif" }, walk: Walks.Of("{\"follow\": \"*\"}"), format: "json")));
        Assert.DoesNotContain(unused, Notes(AssetWalk("true", records: Array.Empty<string>(), formids: new[] { Id("HcDoorA1") })));
        Assert.DoesNotContain(unused, Notes(AssetWalk("{\"depth\": 1}")));
        // through's edge kinds choose the rows of a path-only walk too, so they are not unused (#1278).
        Assert.DoesNotContain(unused, Notes(AssetTools.AssetStatus(_w.Svc, asset_paths: new[] { "meshes/hc/none.nif" }, walk: Walks.Of("{\"through\": [\"texture\"]}"), format: "json")));
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

    /// <summary>The forward reading form's reached count is the number of record rows it lists, with and without a bad
    /// seed beside the good one.</summary>
    [Fact]
    public void TheForwardReadingFormCountsEachRecordRow()
    {
        foreach (var ids in new[] { new[] { Id("HcDoorA1") }, new[] { "ZZZ", Id("HcDoorA1") } })
        {
            using var doc = JsonDocument.Parse(RecordsTools.Records(_w.Svc, formids: ids, walk: new() { depth = 1 }, format: "json"));
            int records = doc.RootElement.GetProperty("records").EnumerateArray().Count(r => !r.TryGetProperty("error", out _));
            Assert.True(records > 1);
            Assert.Contains($"selection = the {records} record(s) the walk reached", doc.RootElement.GetProperty("walk").GetString()!);
        }
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

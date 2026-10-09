using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The scan compositions that used to refuse: a plugins= scope with a named source= under the summary and
/// count forms, and the SkyPatcher overlay pole over a scan, whose limit= window is replayed.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsScanPoleTests : RecordsTestBase
{
    public RecordsScanPoleTests(RecordsFixture f) : base(f) { }

    const string CountsNote = "source= does not change these counts";
    const string WhereNote = "where= judged each record before the SkyPatcher layer replays";
    const string CensusNote = "these counts are of the plugin records, before the SkyPatcher layer";

    static readonly string[] Armo = { "ARMO" };
    static readonly string[] Weap = { "WEAP" };
    static RecordsTools.RecordsProject Aggregate => new() { form = "aggregate", group_by = "winner" };

    /// <summary>The override renames one armor and drops another's EditorID; the master's copies keep both names.</summary>
    string PoleScan(string? format = null, RecordsTools.RecordsProject? project = null, bool counts = false, bool poled = true) =>
        RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName),
                             source: poled ? Plugin(W.MasterName) : null, format: format, project: project, counts_only: counts);

    /// <summary>A draft INI that raises HcRecW0's damage to 123, folded into the layer as the post-state pole.</summary>
    JsonElement DraftPost(string dir)
    {
        var path = W.Scratch(dir, "weapon", "ScanDraft.ini");
        File.WriteAllText(path, "filterByWeapons=HcRecW0:attackDamage=123\r\n");
        return Je("{\"overlay\": \"skypatcher\", \"state\": \"post\", \"ini\": " + JsonSerializer.Serialize(path) + "}");
    }

    long ReplayOpens(Func<string> call, out string response)
    {
        long before = Svc.Counters.ReplayOpens;
        response = call();
        return Svc.Counters.ReplayOpens - before;
    }

    // ---- a plugins= scope with a named source= ------------------------------------------------------

    /// <summary>Was ScopeVsPolePlusSummaryRefusesByName: the summary rows now carry the pole's copy.</summary>
    [Fact]
    public void ScopePlusPoleSummary_TheRowsCarryThePolesEditorIdNotTheWinners()
    {
        var r = PoleScan();
        Served(r, RecordsWorld.RenamedArmorOldEid, RecordsWorld.DroppedEidArmorOldEid);
        Assert.DoesNotContain(RecordsWorld.RenamedArmorNewEid, r);
        Assert.Contains(RecordsWorld.RenamedArmorNewEid, PoleScan(poled: false));
    }

    [Fact]
    public void ScopePlusPoleSummary_Json() =>
        Served(PoleScan("json"), $"\"editorid\": \"{RecordsWorld.RenamedArmorOldEid}\"");

    [Fact]
    public void ScopePlusPoleSummary_Dense()
    {
        var r = PoleScan("dense");
        Served(r, RecordsWorld.RenamedArmorOldEid);
        Assert.DoesNotContain(RecordsWorld.RenamedArmorNewEid, r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    public void ScopePlusPoleAggregate_CountsTheScopeAsTheUnpoledCallDoesAndSaysSo(string? format)
    {
        var poled = PoleScan(format, Aggregate);
        Served(poled, CountsNote);
        Assert.Equal(Counts(PoleScan(format, Aggregate, poled: false)), Counts(poled));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void ScopePlusPoleCountsOnly_CountsTheScopeAsTheUnpoledCallDoesAndSaysSo(string? format)
    {
        var poled = PoleScan(format, counts: true);
        Served(poled, CountsNote);
        Assert.Equal(Counts(PoleScan(format, counts: true, poled: false)), Counts(poled));
    }

    /// <summary>The response with its source statement and notes removed: what the counts say.</summary>
    static string Counts(string response)
    {
        string counts;
        if (response.TrimStart().StartsWith('{'))
        {
            var doc = System.Text.Json.Nodes.JsonNode.Parse(response)!.AsObject();
            Assert.Equal(2, doc["total"]!.GetValue<int>());   // the scope's two armors
            foreach (var k in doc.Select(p => p.Key).Where(k => k.Contains("source") || k == "notes").ToList()) doc.Remove(k);
            counts = doc.ToJsonString();
        }
        else
        {
            counts = string.Join("\n", response.Split('\n').Where(l => !l.Contains("source")));
            Assert.Contains(" 2 matches ", counts);   // the scan line's count: the scope's two armors
        }
        return counts;
    }

    /// <summary>A delta's counts depend on the pole, so its own source statement stays, not the census one.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    public void ScopePlusPoleDeltaCountsOnly_KeepsTheDeltasSourceStatement(string? format)
    {
        var r = RecordsTools.Records(Svc, types: Armo, plugins: Scope(W.OverrideName), source: Plugin(W.MasterName),
                                     versus: Je("\"winner\""), format: format, project: Form("delta"), counts_only: true);
        Served(r, "differing", $"{W.MasterName} — active in the load order");
        Assert.DoesNotContain("the plugins= scope selects", r);
        Assert.DoesNotContain(CountsNote, r);
    }

    // ---- the overlay pole on a scan -----------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void OverlayScanFields_ReadsThePostState(string? format)
    {
        var r = RecordsTools.Records(Svc, types: Weap, source: DraftPost("scan-fields-" + (format ?? "text")), format: format,
                                     project: Fields("BasicStats.Damage"));
        Served(r, "skypatcher overlay (post)");
        Assert.Equal("123", DamageOfW0(r, format));
    }

    /// <summary>HcRecW0's BasicStats.Damage as the response renders it, read off its own row.</summary>
    string? DamageOfW0(string r, string? format)
    {
        var fid = Fid(W.Weapons[0]);
        if (format is null)
        {
            var block = r.Split("\n\n").Single(b => b.Contains($"formid={fid}"));
            return block.Split('\n').SingleOrDefault(l => l.StartsWith("  BasicStats.Damage = "))?["  BasicStats.Damage = ".Length..];
        }
        var root = JsonDocument.Parse(r).RootElement;
        if (format == "dense")
            return root.GetProperty("rows").EnumerateArray().Single(row => row[0].GetString() == fid)[3].GetString();
        return root.GetProperty("records").EnumerateArray().Single(m => m.GetProperty("formid").GetString() == fid)
                   .GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "BasicStats.Damage")
                   .GetProperty("value").GetString();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void OverlayScanSummary_IsServed(string? format) =>
        Served(RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), format: format), "HcRecW1", "skypatcher overlay (post)");

    /// <summary>The window is what replays: one replay context for the call, and one body per windowed row.</summary>
    [Fact]
    public void OverlayScan_TheLimitWindowBoundsTheReplays()
    {
        var opens = ReplayOpens(() => RecordsTools.Records(Svc, types: Weap, source: DraftPost("scan-window"), limit: 1,
                                                           project: Fields("BasicStats.Damage")), out var r);
        Assert.Equal(1, opens);
        Served(r, "bodies for the 1-row window", "read 1 record body in");
    }

    /// <summary>where= matches on the plugin records: HcRecW0's winner has 99, so a 100 floor drops it though the
    /// draft would raise it to 123.</summary>
    [Fact]
    public void OverlayScanWhere_JudgesThePreSkyPatcherRecordAndSaysSo()
    {
        var r = RecordsTools.Records(Svc, types: Weap, source: DraftPost("scan-where"), where: new[] { "BasicStats.Damage >= 100" },
                                     project: Fields("BasicStats.Damage"));
        Served(r, WhereNote);
        Assert.DoesNotContain("123", r);
    }

    /// <summary>The note rides the scan's existing note channel, not a key of its own.</summary>
    [Theory]
    [InlineData("json")]
    [InlineData("dense")]
    public void OverlayScanWhere_TheNoteRidesTheScanNote(string format)
    {
        var r = RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), where: new[] { "BasicStats.Damage >= 1" }, format: format);
        Served(r, "\"scan_note\"", WhereNote);
        Assert.DoesNotContain("source_note", r);
    }

    /// <summary>references= is matched on the plugin records too, so the same note names it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void OverlayScanReferences_SaysReferencesJudgedThePreSkyPatcherRecord(string? format) =>
        Served(RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), references: new[] { "!" + Fid(W.MgefA) }, format: format),
               "references= judged each record before the SkyPatcher layer replays", "HcRecW1");

    [Fact]
    public void OverlayScanWhereAndReferences_OneNoteNamesBoth() =>
        Served(RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), where: new[] { "BasicStats.Damage >= 1" },
                                    references: new[] { "!" + Fid(W.MgefA) }),
               "where= and references= judged each record before the SkyPatcher layer replays");

    /// <summary>A walk reads every record it reaches, so the overlay on a scan-seeded walk refuses as main did.</summary>
    [Theory]
    [InlineData("post")]
    [InlineData("pre")]
    public void OverlayScanWalk_IsRefused(string state)
    {
        var opens = ReplayOpens(() => RecordsTools.Records(Svc, types: Weap, source: Overlay(state), walk: new RecordsTools.RecordsWalk()), out var r);
        Refused(r, "a walk reads every record it reaches", "formids=");
        Assert.Equal(0, opens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("json")]
    [InlineData("dense")]
    public void OverlayScanCensus_IsANoteNotAReplay(string? format)
    {
        var opens = ReplayOpens(() => RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), format: format, counts_only: true), out var r);
        Served(r, CensusNote);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void OverlayScanAggregate_IsANoteNotAReplay()
    {
        var opens = ReplayOpens(() => RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), project: Aggregate), out var r);
        Served(r, CensusNote);
        Assert.Equal(0, opens);
    }

    [Fact]
    public void OverlayScanPre_IsTheWinnerAndSaysSo() =>
        Served(RecordsTools.Records(Svc, types: Weap, source: Overlay("pre"), project: Fields("BasicStats.Damage")),
               "skypatcher overlay (pre) = winner", "BasicStats.Damage = 99");

    [Fact]
    public void OverlayScanToFile_StaysRefused() =>
        Refused(RecordsTools.Records(Svc, types: Weap, source: Overlay("post"), to_file: W.Scratch("results", "overlay-scan.jsonl")),
                "to_file=", "formids=");
}

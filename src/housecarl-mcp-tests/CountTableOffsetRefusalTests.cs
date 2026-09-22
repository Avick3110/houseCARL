using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What a lane says when <c>offset=</c> is asked of a count table (#810, SPEC §2.1): one sentence, the same
/// one everywhere — the table covers the complete selection, it caps with <c>limit=</c>, it does not page. The four
/// lanes used to spell that rule four ways, and <c>records</c> under <c>counts_only=</c> did not spell it at all: it
/// accepted <c>offset=</c> and windowed a render the census writes no rows into.</summary>
static class CountTableRefusal
{
    /// <summary>The distinguishing clauses of the shared sentence, with the knob that asked for the answer named.
    /// <paramref name="hasTable"/> false where the lane renders no table: the limit= clause must be ABSENT there,
    /// because there is nothing for limit= to cap.</summary>
    internal static void SaysTheOneSentence(string text, string knob, bool hasTable = true)
    {
        Assert.Contains(knob + " answers over the COMPLETE selection, so offset= has nothing to page", text);
        Assert.Contains("drop offset=", text);
        Assert.Contains("spill the rows behind it with to_file=", text);
        if (hasTable) Assert.Contains("the count table it returns caps with limit= instead", text);
        else Assert.DoesNotContain("caps with limit=", text);
    }
}

/// <summary>The <c>housecarl_records</c> lanes: the aggregate form, and <c>counts_only=</c> on the scan and the list
/// lane.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsCountTableOffsetTests : RecordsTestBase
{
    public RecordsCountTableOffsetTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void TheAggregateFormRefusesOffsetWithTheSharedSentence() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, offset: 1,
                                 project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }),
            "the aggregate form");

    /// <summary>This lane renders no table at all — its census is the count line — so the sentence must not name
    /// limit= as the knob that caps one.</summary>
    [Fact]
    public void CountsOnlyOnTheScanLaneRefusesOffsetWithNoLimitClause() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, types: new[] { "WEAP" }, counts_only: true, offset: 1), "counts_only=",
            hasTable: false);

    [Fact]
    public void CountsOnlyOnTheListLaneRefusesOffsetWithNoLimitClause() =>
        CountTableRefusal.SaysTheOneSentence(
            RecordsTools.Records(Svc, formids: AllWeaponIds, counts_only: true, offset: 1), "counts_only=",
            hasTable: false);

    /// <summary>The scan's own <c>group_by=</c>, refused in the service rather than at the tool door.</summary>
    [Fact]
    public void TheScansGroupByRefusesOffsetWithTheSharedSentence()
    {
        var outcome = Svc.CrossQuery("Weapon", null, null, false, null, null, 500, groupBy: "winner", offset: 5);
        Assert.NotNull(outcome.Error);
        CountTableRefusal.SaysTheOneSentence(outcome.Error!, "group_by=");
    }
}

/// <summary>The other half of the ruling: a count table CAPS with <c>limit=</c>. The `records` aggregate tables used
/// to ignore `limit=` entirely and be cut only by `max_chars`, so the refusal above named a remedy that did nothing.
/// These arms pin the behaviour, not the sentence: N rows, a marker counting the rest, and a header total that stays
/// the whole tally.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsCountTableLimitTests : RecordsTestBase
{
    public RecordsCountTableLimitTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject ByWinner => new() { form = "aggregate", group_by = "winner" };
    static RecordsTools.RecordsProject ByType => new() { form = "aggregate", group_by = "type" };

    /// <summary>One id per record type, so the list lane's table has more rows than the limit under test.</summary>
    string[] ManyTypedIds => new[]
    {
        Fid(W.Weapons[0]), Fid(W.Armor), Fid(W.MgefA), Fid(W.SpellA),
        Fid(W.BigList), Fid(W.Package), Fid(W.NpcParent),
    };

    static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Rows of the SCAN lane's table, whose row is "  &lt;key&gt; = &lt;count&gt;".</summary>
    static int ScanRows(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\n  \S[^\n]* = \d+").Count;

    /// <summary>Rows of the LIST lane's table, whose row is a count padded to six columns then the key.</summary>
    static int ListRows(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\n {2,}\d+  \S").Count;

    [Fact]
    public void TheScanTableCapsAtLimitAndCountsWhatItHeldBack()
    {
        var whole = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: ByWinner);
        int groups = ScanRows(whole);
        Assert.True(groups > 1, "the fixture has one group — nothing for limit= to cap");

        var capped = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: ByWinner, limit: 1);

        Assert.Equal(1, ScanRows(capped));
        Assert.Contains((groups - 1) + " more group(s) — raise limit= to see them", capped);
        // The tally above the table is the WHOLE one: limit= caps the rows, it does not narrow what was counted.
        Assert.Contains("across " + groups + " group", capped);
        Assert.Contains("the total above is exact", capped);
    }

    [Fact]
    public void TheScanTablesJsonNamesLimitAsWhatCutItAndKeepsTheWholeTotal()
    {
        var whole = Doc(RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: ByWinner, format: "json"));
        int groups = whole.GetProperty("groups_total").GetInt32();
        Assert.True(groups > 1);
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("cut_by").ValueKind);   // uncapped: nothing cut it

        var capped = Doc(RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: ByWinner,
                                              format: "json", limit: 1));

        Assert.Equal(1, capped.GetProperty("groups").GetArrayLength());
        Assert.Equal(1, capped.GetProperty("rendered").GetInt32());
        Assert.Equal(groups, capped.GetProperty("groups_total").GetInt32());
        Assert.Equal("limit", capped.GetProperty("cut_by").GetString());
        Assert.True(capped.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void TheListTableCapsAtLimitAndCountsWhatItHeldBack()
    {
        var whole = RecordsTools.Records(Svc, formids: ManyTypedIds, project: ByType);
        int groups = ListRows(whole);
        Assert.True(groups > 2, "the fixture has too few groups for limit=2 to cap");

        var capped = RecordsTools.Records(Svc, formids: ManyTypedIds, project: ByType, limit: 2);

        Assert.Equal(2, ListRows(capped));
        Assert.Contains((groups - 2) + " more group(s) — raise limit= to see them", capped);
        Assert.Contains("the counts above are exact", capped);
    }

    [Fact]
    public void TheListTablesJsonNamesLimitAsWhatCutItAndKeepsTheWholeTotal()
    {
        var whole = Doc(RecordsTools.Records(Svc, formids: ManyTypedIds, project: ByType, format: "json"));
        int groups = whole.GetProperty("groups_total").GetInt32();
        Assert.Equal(JsonValueKind.Null, whole.GetProperty("cut_by").ValueKind);

        var capped = Doc(RecordsTools.Records(Svc, formids: ManyTypedIds, project: ByType,
                                              format: "json", limit: 2));

        Assert.Equal(2, capped.GetProperty("groups").GetArrayLength());
        Assert.Equal(groups, capped.GetProperty("groups_total").GetInt32());
        Assert.Equal("limit", capped.GetProperty("cut_by").GetString());
    }
}

/// <summary>The OFF-ORDER scan's own <c>group_by=</c> — its own world, because the shared records world carries no
/// off-order file this lane can read.</summary>
[Collection("render-cost")]
[Trait("tier", "integration")]
public sealed class OffOrderCountTableOffsetTests
{
    readonly RenderCostWorld _w;
    public OffOrderCountTableOffsetTests(RenderCostFixture f) => _w = f.W;

    [Fact]
    public void TheOffOrderScansGroupByRefusesOffsetWithTheSharedSentence()
    {
        var pole = _w.Svc.ProbeSourceArm(_w.OffOrderName, null, out var perr);
        Assert.Null(perr);

        var outcome = _w.Svc.OffOrderQuery(pole!, new[] { "WEAP" }, null, null, null, false, null, 100,
                                          groupBy: "winner", offset: 5, formidSet: null, artifactDemands: null);

        Assert.NotNull(outcome.Error);
        CountTableRefusal.SaysTheOneSentence(outcome.Error!, "group_by=");
    }
}

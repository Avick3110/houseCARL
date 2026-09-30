using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The formids= (list) lane: identity form, the one-pole source cases, the touchers-named refusals,
/// aggregate and census.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsListLaneTests : RecordsTestBase
{
    public RecordsListLaneTests(RecordsFixture f) : base(f) { }

    string Identity(params string[] ids) => RecordsTools.Records(Svc, formids: ids, project: Form("identity"));

    [Fact]
    public void IdentityForm_LabelsTheListStatesTheFormAndStampsTheEpoch()
    {
        var r = Identity(Fid(W.Weapons[0]), Fid(W.MgefA));
        Served(r, "form=identity", "HcRecW0", $"epoch={W.Epoch0}");
    }

    [Fact]
    public void IdentityForm_PlusANamedSourceRefusesByContract_IdentityIsTheResolutionFrame() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) },
                                     source: Plugin(W.OverrideName), project: Form("identity")),
                "labeling frame");

    string ActiveSummary() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]), Fid(W.Weapons[1]) },
                             source: Plugin(W.OverrideName));

    [Fact]
    public void OnePoleActiveArm_TheResponseStatesTheArm() =>
        Served(ActiveSummary(), "form=summary", "active in the load order");

    [Fact]
    public void OnePoleActiveArm_AnUntouchedRecordIsAPerItemRefusalNamingTheActualTouchers() =>
        Served(ActiveSummary(), "does not touch", W.MasterName, W.OverrideName);

    [Fact]
    public void OnePoleActiveArm_SummaryRowsCarryIdentityFactsNotFieldDumps() =>
        Assert.DoesNotContain("Damage", ActiveSummary());

    string OffOrderSummary() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, source: Plugin(W.OldName));

    [Fact]
    public void OnePoleOffOrderArm_ADisabledModsPluginResolvesAndTheResponseStatesTheArm() =>
        Served(OffOrderSummary(), "OUT-OF-LOAD-ORDER", "form=summary");

    [Fact]
    public void OnePoleOffOrderArm_CarriesTheEpochCoverageQualifier() =>
        Assert.Contains("OUTSIDE the epoch fingerprint", OffOrderSummary());

    [Fact]
    public void OnePoleOffOrderArm_TheRowStillCarriesTheActiveWinnerContext() =>
        Assert.Contains($"winner={W.MasterName}", OffOrderSummary());

    [Fact]
    public void OffOrderFieldsForm_ReadsTheFilesOwnVersionNotTheWinners() =>
        Assert.Contains("55", RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) },
                                                   source: Plugin(W.OldName),
                                                   project: Fields("BasicStats.Damage")));

    [Fact]
    public void APoleFoundInNeitherPlaceRefusesNamingBothPlacesSearched() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin("NoSuchPlugin.esp")),
                "NEITHER place", "not ACTIVE", "on disk");

    [Fact]
    public void ListLaneAggregate_CountsByWinnerOverTheResolvedRows() =>
        Served(RecordsTools.Records(Svc, formids: AllWeaponIds,
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }),
               "group_by=winner", W.MasterName, W.OverrideName);

    [Fact]
    public void CountsOnly_IsTheCheapCensusWithNoRows() =>
        Served(RecordsTools.Records(Svc, formids: AllWeaponIds, counts_only: true), "count=3", "ok=3");

    /// <summary>One id per record type, so the count table has more group rows than a small max_chars can hold.</summary>
    string[] ManyTypedIds => new[]
    {
        Fid(W.Weapons[0]), Fid(W.Armor), Fid(W.MgefA), Fid(W.SpellA),
        Fid(W.BigList), Fid(W.Package), Fid(W.NpcParent),
    };

    [Fact]
    public void ListLaneAggregate_HoldsMaxCharsAndSaysWhatItCutOff()
    {
        var project = new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" };
        var whole = RecordsTools.Records(Svc, formids: ManyTypedIds, project: project);
        Served(whole, "group_by=type");
        string Call(int c) => RecordsTools.Records(Svc, formids: ManyTypedIds, project: project, max_chars: c);
        // This selection is served cut only in a band a few chars wide (#986: whole first, refused below the floor); the
        // cut through the tool is WideCutLaneTests.AListAggregateServedCutSaysWhatItCutOff, over forty plugins' groups.
        // Below the whole answer the ceiling holds: the call fits or is refused naming a cap it fits.
        RenderFloorAssert.FitsOrRefuses(Call(whole.Length - 40), whole.Length - 40, Call);
    }

    [Fact]
    public void ListLaneAggregateInJson_SaysHowManyGroupsItWasCutFrom()
    {
        var project = new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" };
        var whole = RecordsTools.Records(Svc, formids: ManyTypedIds, project: project, format: "json");
        using var wholeDoc = System.Text.Json.JsonDocument.Parse(whole);
        int groups = wholeDoc.RootElement.GetProperty("groups_total").GetInt32();
        Assert.Equal(groups, wholeDoc.RootElement.GetProperty("rendered").GetInt32());
        Assert.False(wholeDoc.RootElement.GetProperty("truncated").GetBoolean());

        using var cut = System.Text.Json.JsonDocument.Parse(
            RecordsTools.Records(Svc, formids: ManyTypedIds, project: project, format: "json", max_chars: whole.Length / 2));
        Assert.True(cut.RootElement.GetProperty("truncated").GetBoolean());
        // The cut document sizes its own retry: how many groups there are, and how many of them it laid.
        Assert.Equal(groups, cut.RootElement.GetProperty("groups_total").GetInt32());
        Assert.True(cut.RootElement.GetProperty("rendered").GetInt32() < groups);
    }

    [Fact]
    public void ListLaneAggregateInJson_NamesTheRequestedTypesWithNoRecordsApartFromTheCountedGroups()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                 walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" },
                                 types: new[] { "SPEL", "SCRL" }, format: "json",
                                 project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" }));
        var empty = doc.RootElement.GetProperty("empty_groups").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Scroll" }, empty);
        // The counted table carries only groups that counted something, so no zero row can be cut from it.
        Assert.All(doc.RootElement.GetProperty("groups").EnumerateArray(),
                   g => Assert.True(g.GetProperty("count").GetInt32() > 0));
    }

    [Fact]
    public void ListLaneAggregate_ARequestedTypeWithNoCarriersIsA0Row()
    {
        // The one list-lane shape that takes types=: the typed MGEF carrier walk, where types= narrows the carrier
        // types. MgefA has spell carriers and no scroll ones, so SCRL is the requested type with nothing in it.
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) },
                                     walk: new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" },
                                     types: new[] { "SPEL", "SCRL" },
                                     project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
        Served(r, "group_by=type", "Spell");
        Assert.Contains("no records: Scroll", r);
    }

    [Fact]
    public void ListLaneAggregate_TheEmptyTypeLineIsChargedAheadOfTheCountedRows()
    {
        // The zero answer sorts last among the counts, so it is what a cap would take first: it is stated on its
        // own line and charged with the notice, and survives a table too small for all its rows.
        var project = new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" };
        var types = new[] { "SPEL", "SCRL" };
        var walk = new RecordsTools.RecordsWalk { direction = "reverse", follow = "Effects[].BaseEffect" };
        var whole = RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefA) }, walk: walk, types: types, project: project);
        Served(whole, "no records: Scroll");
        // This selection's table is narrower than its floor, so a call is served it whole or refused (#986): the text
        // render is driven directly over a thirty-row table, at the first cap it lays cut inside.
        var rows = Enumerable.Range(1, 30).Select(i => new KeyValuePair<string, int>($"Type{i:D2}", 100 - i)).ToList();
        var (_, cut) = RenderFloorAssert.ServedCut(
            c => RecordsTools.RenderListAggregateText(rows, new[] { "Scroll" }, "type", 1_000, 0, null, "records  form=aggregate",
                                                      (1_000, 5), c, 0),
            t => t.Contains("truncated: rendered", StringComparison.Ordinal));
        Served(cut, "no records: Scroll");
    }
}

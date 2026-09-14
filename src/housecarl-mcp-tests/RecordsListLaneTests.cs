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
        int cap = whole.Length - 40;   // derived, not pinned to a number that would only hold on one machine

        var cut = RecordsTools.Records(Svc, formids: ManyTypedIds, project: project, max_chars: cap);
        Served(cut, "truncated: rendered", "groups before hitting max_chars=" + cap);
        Assert.True(CountOf(cut, "\n  ") < CountOf(whole, "\n  "), "the capped render laid as many rows as the uncapped one");
        // The ceiling holds, or the fixed part the response owes whatever the budget names its own overrun.
        if (cut.Length > cap)
            Assert.Contains($"over the max_chars={cap} it was given", cut);
    }
}

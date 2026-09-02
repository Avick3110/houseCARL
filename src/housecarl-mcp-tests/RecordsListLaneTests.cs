using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// SPEC §4.2 / §6.1 — the formids= (list) lane: identity form, the one-pole source arms, the
/// touchers-named refusals, aggregate and census. (RecordsGuardProbe arm 3.)
/// </summary>
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

    // MERGED (2 assertions, 1 test): both arms need a duplicate plugin filename on disk, which mutates the
    // shared world. The mutation is created and removed inside one test rather than leaking across the class.
    [Fact]
    public void ADuplicateFilenameRefusesNamingTheModFolders_AndTheStructuredFileModPoleDisambiguatesIt()
    {
        var dupDir = Path.Combine(W.ModsDir, "OldModCopy");
        Directory.CreateDirectory(dupDir);
        File.Copy(W.OldFile, Path.Combine(dupDir, W.OldName));
        try
        {
            var ambiguous = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, source: Plugin(W.OldName));
            Refused(ambiguous, "SEVERAL mod folders", "\"mod\"");

            var disamb = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) },
                                              source: Je($"{{\"file\": \"{W.OldName}\", \"mod\": \"OldMod\"}}"));
            Served(disamb, "OUT-OF-LOAD-ORDER");
        }
        finally { Directory.Delete(dupDir, true); }
    }

    [Fact]
    public void ListLaneAggregate_CountsByWinnerOverTheResolvedRows() =>
        Served(RecordsTools.Records(Svc, formids: AllWeaponIds,
                                    project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "winner" }),
               "group_by=winner", W.MasterName, W.OverrideName);

    [Fact]
    public void CountsOnly_IsTheCheapCensusWithNoRows() =>
        Served(RecordsTools.Records(Svc, formids: AllWeaponIds, counts_only: true), "count=3", "ok=3");
}

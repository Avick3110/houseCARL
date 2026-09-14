using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_place's render: the §2.1 accounting is in band on every response, and the two things a
/// caller cannot act without — how much was left out, and that a placed file does not win until the mod is enabled
/// (and, on the into= lane, sorted) — survive a cap that cuts the list. place is the batch skeleton's third caller,
/// so the cut marker is the same one asset_status and nif_inspect print. The lane decides which of those two
/// instructions is true, so it is asserted on both arms.</summary>
[Trait("tier", "unit")]
public class PlaceRenderTests
{
    /// <summary>A place outcome on the DEFAULT lane unless <paramref name="fresh"/> says otherwise — the lane a caller
    /// who names nothing gets, so the cap cases exercise it rather than into=.</summary>
    static PlaceOutcome Outcome(int ok, int failed, bool fresh = true, bool contended = true,
                                bool overwriteWinner = false, bool destinationWinner = false) => new(
        Enumerable.Range(0, ok)
            .Select(i => new PlaceResult($"meshes/hc/ok{i}.nif", true, 42, "SomeMod (loose)",
                                         !contended ? null
                                         : overwriteWinner ? "overwrite (loose)"
                                         : destinationWinner ? "houseCARL - MyFixes (loose)"
                                         : "OtherMod (loose)", null)
                       { WinnerIsOverwrite = contended && overwriteWinner,
                         WinnerIsDestination = contended && destinationWinner })
            .Concat(Enumerable.Range(0, failed)
                .Select(i => new PlaceResult($"meshes/hc/bad{i}.nif", false, 0, null, null, "nothing supplies this path")))
            .ToList(),
        @"C:\mods\houseCARL - MyFixes", Array.Empty<string>(), null, null) { FreshFolder = fresh };

    [Fact]
    public void EveryResponseCarriesTheAccountingInBand()
    {
        var text = PlaceWire.Render(Outcome(ok: 2, failed: 1), 80_000);

        Assert.Contains("placed 2 of 3 asset(s) (1 failed)", text);
        Assert.Contains("mod folder: houseCARL - MyFixes", text);
        Assert.Contains("total=3 rendered=3 placed=2 failed=1 truncated=false", text);
    }

    [Fact]
    public void ACutListStillNamesWhatItDroppedAndWhatTheCallerMustDoNext()
    {
        // Tight enough that the list is cut after the first row, but wide enough to carry the trailer, which is now
        // charged inside max_chars rather than appended past it.
        var text = PlaceWire.Render(Outcome(ok: 40, failed: 0), 1_200);

        Assert.True(text.Length <= 1_200, $"returned {text.Length} chars at max_chars=1200");
        Assert.Contains("meshes/hc/ok0.nif", text);
        Assert.DoesNotContain("meshes/hc/ok39.nif", text);
        Assert.Contains("more asset(s) omitted at max_chars=1200", text);
        // The accounting and the enable+sort instruction are still written: a truncated response that dropped either
        // would be a render that read like a finished job.
        Assert.Contains("total=40 rendered=", text);
        Assert.Contains("truncated=true", text);
        Assert.Contains("\"wrote it\" is not \"it wins\"", text);
    }

    [Fact]
    public void ACallThatPlacedNothingDoesNotClaimAnEnableAndSortIsPending()
    {
        var text = PlaceWire.Render(Outcome(ok: 0, failed: 2), 80_000);

        Assert.Contains("total=2 rendered=2 placed=0 failed=2", text);
        Assert.DoesNotContain("\"wrote it\" is not \"it wins\"", text);
    }

    [Fact]
    public void AFreshFolderOverAnExistingProviderIsToldToEnableOnly_MO2RanksAnUnseenFolderHighest()
    {
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: true), 80_000);

        Assert.Contains("Enable the mod 'houseCARL - MyFixes' in MO2", text);
        // The sort is the bug: a folder MO2 has not seen already out-ranks the winner once it is ticked.
        Assert.DoesNotContain("SORT it (left pane)", text);
        Assert.DoesNotContain("sort the new mod ABOVE it", text);
        Assert.Contains("out-ranks the current winner(s) with no sorting", text);
        // The tail stays: a mod added LATER can still outrank this one.
        Assert.Contains("sort it above any mod you later add", text);
        // The per-row line says the same thing, and still names the winner it out-ranks.
        Assert.Contains("currently wins the VFS: OtherMod (loose)", text);
        Assert.Contains("'houseCARL - MyFixes' out-ranks it once enabled", text);
    }

    [Fact]
    public void AnIntoPlacementOverAnExistingProviderIsStillToldToSortAboveIt()
    {
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: false), 80_000);

        Assert.Contains("Enable the mod 'houseCARL - MyFixes' in MO2", text);
        Assert.Contains("SORT it (left pane) ABOVE the current winner(s) listed above", text);
        Assert.Contains("sort 'houseCARL - MyFixes' ABOVE it", text);
        Assert.DoesNotContain("with no sorting", text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithNoContentionBothLanesSayTheSameThing_EnableAndItWins(bool fresh)
    {
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: fresh, contended: false), 80_000);

        Assert.Contains("Nothing else provided these path(s), so once enabled the placed copy wins", text);
        Assert.Contains("sort it above any mod you later add", text);
        Assert.DoesNotContain("SORT it (left pane)", text);
    }

    [Fact]
    public void TheJsonTwinCarriesTheSameLanedInstruction_AJsonCallerActsOnNextStepAlone()
    {
        var fresh = Doc(JsonWire.RenderPlaceOutcome(Outcome(ok: 1, failed: 0, fresh: true), 80_000));
        var into = Doc(JsonWire.RenderPlaceOutcome(Outcome(ok: 1, failed: 0, fresh: false), 80_000));

        Assert.Contains("out-ranks the current winner(s) with no sorting", fresh.NextStep);
        Assert.DoesNotContain("SORT it (left pane)", fresh.NextStep);
        Assert.Contains("out-ranks it once enabled", fresh.WinnerNote);

        Assert.Contains("SORT it (left pane) ABOVE", into.NextStep);
        Assert.Contains("sort 'houseCARL - MyFixes' ABOVE it", into.WinnerNote);
    }

    [Fact]
    public void AnOverwriteWinnerIsNotOutRankedByEnablingAnything_TheRenderSaysToMoveThatCopy()
    {
        // MO2's overwrite folder is the TOP loose root, above every mod, so neither lane's instruction reaches it.
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: true, overwriteWinner: true), 80_000);

        Assert.Contains("currently wins the VFS: overwrite (loose)", text);
        Assert.Contains("move or delete the overwrite copy of this path", text);
        Assert.Contains("MO2's overwrite folder sits ABOVE every mod in the VFS", text);
        Assert.DoesNotContain("out-ranks it once enabled", text);
        Assert.DoesNotContain("with no sorting", text);
        // Nor may it fall through to the no-contention arm: something DOES provide the path.
        Assert.DoesNotContain("Nothing else provided these path(s)", text);
    }

    [Fact]
    public void AnOverwriteWinnerOnTheIntoLaneIsNotAnsweredWithASortEither()
    {
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: false, overwriteWinner: true), 80_000);

        Assert.Contains("move or delete the overwrite copy of this path", text);
        Assert.DoesNotContain("SORT it (left pane)", text);
    }

    [Fact]
    public void ARePlaceIntoAFolderThatAlreadyWinsIsNotToldToSortItAboveItself()
    {
        var text = PlaceWire.Render(Outcome(ok: 1, failed: 0, fresh: false, destinationWinner: true), 80_000);

        Assert.Contains("'houseCARL - MyFixes' already provided this path", text);
        Assert.DoesNotContain("sort 'houseCARL - MyFixes' ABOVE it", text);
        Assert.DoesNotContain("SORT it (left pane)", text);
    }

    /// <summary>The two laned strings out of a json place document, read as data rather than matched against the
    /// wire's escaping.</summary>
    static (string NextStep, string WinnerNote) Doc(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("next_step").GetString()!,
                doc.RootElement.GetProperty("results")[0].GetProperty("winner_note").GetString()!);
    }
}

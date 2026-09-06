using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_place's render: the §2.1 accounting is in band on every response, and the two things a
/// caller cannot act without — how much was left out, and that a placed file does not win until the mod is enabled
/// and sorted — survive a cap that cuts the list. place is the batch skeleton's third caller, so the cut marker is
/// the same one asset_status and nif_inspect print.</summary>
[Trait("tier", "unit")]
public class PlaceRenderTests
{
    static PlaceOutcome Outcome(int ok, int failed) => new(
        Enumerable.Range(0, ok)
            .Select(i => new PlaceResult($"meshes/hc/ok{i}.nif", true, 42, "SomeMod (loose)", "OtherMod (loose)", null))
            .Concat(Enumerable.Range(0, failed)
                .Select(i => new PlaceResult($"meshes/hc/bad{i}.nif", false, 0, null, null, "nothing supplies this path")))
            .ToList(),
        @"C:\mods\houseCARL - MyFixes", Array.Empty<string>(), null, null);

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
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="RenderCap.Hold"/> and <see cref="Artifacts.CeilingText"/> on renders shaped to reach the edges
/// no fixture's render reaches: a floor that never settles, and a failed spill whose warning is wider than its block.</summary>
[Trait("tier", "unit")]
public sealed class RenderFloorHoldTests
{
    /// <summary>A render that grows with every cap it is given never settles: the call is refused saying so, never
    /// shipped over the cap it was given.</summary>
    [Fact]
    public void AFloorThatNeverSettlesIsRefusedNotShippedOverTheCap()
    {
        static string Render(int cap) => new('x', cap + 1);

        var r = RenderCap.Hold(Render(100), 100, Render, out bool refused);

        Assert.True(refused);
        Assert.StartsWith("error: max_chars=100 ", r);
        Assert.Contains("did not settle", r);
        Assert.DoesNotContain("\n", r);
    }

    /// <summary>A floor that prints the cap back grows a digit when re-rendered at its own length: the cap named is the
    /// one the render was measured to fit, not the first length it came back at.</summary>
    [Fact]
    public void AFloorThatGrowsWithThePrintedCapNamesACapTheRenderFits()
    {
        static string Render(int cap) => new string('x', 995) + " max_chars=" + cap;

        var r = RenderCap.Hold(Render(100), 100, Render, out _);

        int named = RenderFloorAssert.Named(r);
        Assert.True(Render(named).Length <= named, $"{r} names a cap its render is {Render(named).Length} chars at");
    }

    /// <summary>A spill that could not be written is stated, never refused away: where its warning does not fit the cap
    /// the reply with a written spill fitted, the failure itself is the refusal, and it names no cap.</summary>
    [Fact]
    public void AFailedSpillWhoseWarningDoesNotFitIsTheRefusalAndNamesNoCap()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-floorhold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A file where the artifact's folder should be, so the write cannot land.
            var blocker = Path.Combine(root, "blocked");
            File.WriteAllText(blocker, "x");
            var target = ArtifactTarget.Named(Path.Combine(blocker, "spill.jsonl"));
            // Whole it is 5,000 chars; cut, a 100-char head plus its spill state, the failure warning 2,000 chars wider.
            static string At(int cap, SpillState? sp, WholePass? _, out bool cut)
            {
                cut = cap < 5_000;
                if (!cut) return new string('w', 5_000);
                return new string('h', 100) + Wire.SpillText(sp) + (sp?.Failure is null ? "" : new string('f', 2_000));
            }

            var r = Artifacts.CeilingText(1_500, At, new Artifacts.SpillTo(() => target.Path, () => target),
                t => Artifacts.WriteResolve(Array.Empty<ResolvedRef>(), "", t, "ceiling", Array.Empty<KeyValuePair<string, string>>()));

            Assert.StartsWith("error: ", r);
            Assert.Contains("could not be written", r);
            Assert.DoesNotContain("fits max_chars=", r);
        }
        finally { Directory.Delete(root, true); }
    }
}

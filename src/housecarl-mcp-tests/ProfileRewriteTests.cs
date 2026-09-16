using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2 holds loadorder.txt and plugins.txt while it rewrites them on a re-sort (#794). A tool call landing in
/// that window used to die with "an internal houseCARL failure", because the shared load-order accessor read those
/// files unguarded. Each test owns its world: it locks a profile file, which no shared fixture can survive.</summary>
[Trait("tier", "integration")]
public sealed class ProfileRewriteTests
{
    static string LoadOrderPath(AssetSelectWorld w) => Path.Combine(w.ProfileDir, "loadorder.txt");

    /// <summary>Nothing is built yet, so there is no answer to serve: the call says the profile is being rewritten and
    /// to run it again, in place of the internal-failure sentence.</summary>
    [Fact]
    public void AColdAssetCallDuringARewriteNamesTheRewriteInsteadOfAnInternalFailure()
    {
        using var w = new AssetSelectWorld();
        using var hold = HeldOpen.Hold(LoadOrderPath(w));

        var response = AssetTools.AssetStatus(w.Svc, new[] { w.Rel("0001.nif") });

        Assert.Contains("being rewritten", response, StringComparison.Ordinal);
        Assert.DoesNotContain("internal houseCARL failure", response, StringComparison.Ordinal);
    }

    /// <summary>An asset answer does not depend on the record index, and the resolver from the last call is still
    /// good: a profile the refresh cannot re-read leaves that build in place and the call answers off it.</summary>
    [Fact]
    public void AWarmAssetCallAnswersOffTheBuildItHoldsWhileTheProfileIsUnreadable()
    {
        using var w = new AssetSelectWorld();
        var before = w.Svc.AssetStatus(new[] { w.Rel("0002.nif") });
        Assert.Equal("FaceHigher", before.Results.Single().Hit!.Winner!.Source);

        // Changing the file is what makes the next call attempt a refresh at all — the freshness check is on the
        // profile files' stamps, and a locked file with an unchanged stamp is never read.
        File.WriteAllText(LoadOrderPath(w), "# header\r\nHcArch.esp\r\n# re-sorted\r\n");
        using var hold = HeldOpen.Hold(LoadOrderPath(w));

        var during = w.Svc.AssetStatus(new[] { w.Rel("0002.nif") });

        Assert.Equal("FaceHigher", during.Results.Single().Hit!.Winner!.Source);
    }

    /// <summary>The vacuity check on both tests above: with the hold released the same cold call resolves normally, so
    /// neither is asserting against a world that was broken to begin with.</summary>
    [Fact]
    public void OnceTheRewriteFinishesTheSameColdCallResolvesNormally()
    {
        using var w = new AssetSelectWorld();
        using (HeldOpen.Hold(LoadOrderPath(w)))
            Assert.Contains("being rewritten", AssetTools.AssetStatus(w.Svc, new[] { w.Rel("0001.nif") }), StringComparison.Ordinal);

        Assert.Equal("FaceBase", w.Svc.AssetStatus(new[] { w.Rel("0001.nif") }).Results.Single().Hit!.Winner!.Source);
    }

    /// <summary>The read itself names the transient, so every lane that reads the profile gets the same sentence
    /// rather than each tool catching a raw IOException of its own.</summary>
    [Fact]
    public void ReadingAProfileWhoseFileIsHeldThrowsTheNamedTransient()
    {
        using var w = new AssetSelectWorld();
        using var hold = HeldOpen.Hold(LoadOrderPath(w));

        var ex = Assert.Throws<ProfileUnreadableException>(() => Mo2LoadOrder.ReadComposition(w.ProfileDir));

        Assert.Equal(LoadOrderPath(w), ex.ProfilePath);
    }
}

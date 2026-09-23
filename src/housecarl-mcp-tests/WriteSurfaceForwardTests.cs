using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.WriteSurfaceReads;

namespace HousecarlMcpTests;

/// <summary>housecarl_forward: source= decides the content, the out-ranked winner is named, dry_run writes nothing, and
/// a bad source is refused by name. Migrated from write-surface-guard's forward arm.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceForwardTests : IClassFixture<WriteSurfaceWorld>
{
    readonly WriteSurfaceWorld _w;
    public WriteSurfaceForwardTests(WriteSurfaceWorld w) => _w = w;

    // probe: source= decides the content: the MASTER's version lands, not the load-order winner's
    [Fact]
    public void SourceDecidesTheContent()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2Fwd");
        var path = _w.ArtifactPathFrom(r);
        Assert.NotNull(path);
        Assert.Equal((ushort)10, DamageIn(path!, _w.SubjectKey));
    }

    // probe: the render names the winner the forward will out-rank once enabled
    [Fact]
    public void RenderNamesTheOutRankedWinner()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2FwdWin");
        Assert.Contains("out-ranks the current winner", r);
        Assert.Contains(_w.ReplacerName, r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: forwarding the version that ALREADY wins is flagged redundant, never silently a no-op
    [Fact]
    public void ForwardingTheWinnerIsFlaggedRedundant()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerName, patch: "W2FwdR");
        Assert.Contains("already the load-order winner", r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: dry_run=true: the DRY RUN render leads with nothing-written, and no mod folder is cut
    [Fact]
    public void DryRunWritesNoModFolder()
    {
        int before = Directory.GetDirectories(_w.ModsDir).Length;
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2FwdDry", dry_run: true);
        Assert.StartsWith("DRY RUN", r);
        Assert.Equal(before, Directory.GetDirectories(_w.ModsDir).Length);
    }

    // probe: a source= in NEITHER the load order nor on disk is refused by name, naming both places searched
    // probe: …and a name nothing resembles gets NO invented suggestion
    [Fact]
    public void AbsentSourceIsRefusedWithNoSuggestion()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: "NotInTheOrder.esp", patch: "W2FwdBad");
        Assert.StartsWith("error:", r);
        Assert.Contains("NotInTheOrder.esp", r);
        Assert.Contains("not in the load order", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Did you mean", r);
    }

    // probe: a MISTYPED source= still gets the did-you-mean, naming the plugin it resembles
    [Fact]
    public void MistypedSourceGetsDidYouMean()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: "HcW2Mastre.esm", patch: "W2FwdTypo");
        Assert.StartsWith("error:", r);
        Assert.Contains("Did you mean", r);
        Assert.Contains(_w.MasterName, r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: forward without source= is refused naming the parameter and what it means
    [Fact]
    public void MissingSourceIsRefused()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, patch: "W2FwdNo");
        Assert.StartsWith("error:", r);
        Assert.Contains("source=", r);
    }

    // probe: forward: source= equal to the into= patch refuses naming source=, never from_plugin
    [Fact]
    public void SelfForwardIntoThePatchNamesSource()
    {
        var made = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.MasterName, patch: "W2FwdSelf");
        var file = Path.GetFileName(_w.ArtifactPathFrom(made));
        Assert.NotNull(file);
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: file, into: file);
        Assert.Contains("is the output patch itself", r);
        Assert.Contains("source=", r);
        Assert.DoesNotContain("from_plugin", r);
    }

    // probe: a typo of a DISABLED plugin's name gets the did-you-mean too, not just the active half
    [Fact]
    public void TypoOfADisabledPluginGetsDidYouMean()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: "HcW2Of.esp", patch: "W2TypoOff");
        Assert.StartsWith("error:", r);
        Assert.Contains("Did you mean", r);
        Assert.Contains(_w.OffName, r, StringComparison.OrdinalIgnoreCase);
    }

    // probe: a forwarded body referencing an INACTIVE plugin is refused NAMING it, not as a serialize/commit fault
    [Fact]
    public void BodyLinkingAnInactivePluginIsRefusedByName()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ChainName, patch: "W2Chain");
        Assert.StartsWith("error:", r);
        Assert.Contains("NOT active in the load order", r);
        Assert.Contains("Enable that plugin in MO2", r);
        Assert.DoesNotContain("serialize or commit", r);
    }
}

/// <summary>The in-place self-forward refusal; its own world because the call acknowledges the replacer.</summary>
[Trait("tier", "integration")]
public sealed class WriteSurfaceForwardSelfInPlaceTests : IDisposable
{
    readonly WriteSurfaceWorld _w = new();
    public void Dispose() => _w.Dispose();

    // probe: forward: source= equal to the in_place= target refuses naming source=, never from_plugin
    [Fact]
    public void SelfForwardInPlaceNamesSource()
    {
        var r = ForwardTools.Forward(_w.Svc, formids: new[] { _w.SubjectFid }, source: _w.ReplacerName,
            in_place: _w.ReplacerName, acknowledge: true);
        Assert.Contains("is the in-place target itself", r);
        Assert.Contains("source=", r);
        Assert.DoesNotContain("from_plugin", r);
    }
}

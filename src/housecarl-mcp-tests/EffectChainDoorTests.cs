using Xunit;

namespace HousecarlMcpTests;

/// <summary>The MGEF chain's type check reads its seeds through the read door.</summary>
[Trait("tier", "integration")]
public sealed class EffectChainDoorTests
{
    /// <summary>A seed whose winner plugin is held open is a per-seed failure in one sentence; the other seed answers.</summary>
    [Fact]
    public void ASeedInAPluginThatWillNotOpenIsAPerSeedFailureNotAThrow()
    {
        using var w = new SmallDoorWorld();
        using var hold = HeldOpen.Hold(w.HeldPath);

        var r = w.Svc.ResolveEffectChains(new[] { w.HeldEffect, w.EffectB }, null, 50);

        Assert.StartsWith($"{w.HeldEffect} could not be read: could not read '{w.Held.FileName}'", r[0].Error);
        Assert.EndsWith("Close that program and run this again.", r[0].Error!.Split(" (")[0]);
        Assert.Null(r[1].Error);
        Assert.Equal(2, r[1].Total);
    }
}

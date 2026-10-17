using Xunit;

namespace HousecarlMcpTests;

/// <summary>The info_order type check reads its seeds through the read door.</summary>
[Trait("tier", "integration")]
public sealed class InfoOrderDoorTests
{
    /// <summary>A seed whose winner plugin is held open is a row that says it could not be read; the other row answers.</summary>
    [Fact]
    public void ASeedInAPluginThatWillNotOpenIsARowErrorNotAThrow()
    {
        using var w = new SmallDoorWorld();
        using var hold = HeldOpen.Hold(w.HeldPath);

        var rows = w.Svc.InfoOrderBatch(new[] { ScratchMo2.Fid(w.HeldTopic), ScratchMo2.Fid(w.BaseTopic) }, null, out var refusal, out _);

        Assert.Null(refusal);
        Assert.Contains("could not be read", rows[0].Error);
        Assert.Null(rows[1].Error);
        Assert.Equal("HcSdBaseTopic", rows[1].EditorId);
    }
}

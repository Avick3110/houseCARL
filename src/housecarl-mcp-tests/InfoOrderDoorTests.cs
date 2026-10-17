using HousecarlMcp;
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
        Assert.StartsWith($"the winner body of {ScratchMo2.Fid(w.HeldTopic)} could not be read: could not read '{w.Held.FileName}': ", rows[0].Error);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(rows[0].Error!, System.Text.RegularExpressions.Regex.Escape($"'{w.Held.FileName}'")));
        Assert.Contains("Close that program and run this again.", rows[0].Error);
        Assert.Null(rows[1].Error);
        Assert.Equal("HcSdBaseTopic", rows[1].EditorId);
    }
    /// <summary>Two topics in one plugin are type-checked off one read of its DialogTopics group, not one per topic.</summary>
    [Fact]
    public void TwoTopicsInOnePluginReadItsTopicGroupOnce()
    {
        using var w = new SmallDoorWorld();

        var d = GroupReadDelta.Of(w.Svc, () => RecordsTools.Records(w.Svc, formids: new[] { ScratchMo2.Fid(w.BaseTopic), ScratchMo2.Fid(w.BaseTopic2) },
                                                                     project: new RecordsTools.RecordsProject { form = "info_order" }),
                                  "HcSdBaseTopic", "HcSdBaseTopic2");

        // One read for the type check, one for the merge (DialogueValidate's own pass); per topic it would be three.
        Assert.Equal(new Dictionary<(string, string), int> { [(w.Base.FileName.String, "DialogTopics")] = 2 }, d);
    }
}

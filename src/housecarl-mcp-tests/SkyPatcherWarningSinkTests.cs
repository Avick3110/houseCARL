using System.Diagnostics;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The overlay replay's warning collector. A warning names the record it was raised for, so one INI line
/// the layer cannot apply raises a DIFFERENT warning for every record in the batch — and the collector runs once
/// per record.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherWarningSinkTests
{
    /// <summary>The warnings a record-per-line batch actually produces: one long shared prefix (the same file, the
    /// same line number) and a per-record tail, which is the worst case for a membership test that compares
    /// strings.</summary>
    static string Warning(int i) => new string('x', 180) + i;

    [Fact]
    public void TheKeptWarningsStopAtTheCapAndTheRestAreCounted()
    {
        var sink = new SkyPatcherOverlay.WarningSink();
        for (int i = 0; i < SkyPatcherOverlay.WarningSink.Cap + 7; i++) sink.Add(Warning(i));

        Assert.Equal(SkyPatcherOverlay.WarningSink.Cap, sink.Kept.Count);
        Assert.Equal(Warning(0), sink.Kept[0]);
        Assert.Equal(7, sink.Overflow);
    }

    [Fact]
    public void ARepeatedWarningIsKeptOnce()
    {
        var sink = new SkyPatcherOverlay.WarningSink();
        sink.Add(Warning(1));
        sink.Add(Warning(1));
        sink.Add(Warning(2));

        Assert.Equal(new[] { Warning(1), Warning(2) }, sink.Kept);
        Assert.Equal(0, sink.Overflow);
    }

    /// <summary>Collecting costs the same per warning however many came before it: a batch of 50,000 records under
    /// an overlay post source hands the sink 50,000 distinct warnings, which is a hash lookup each. Comparing every
    /// new warning against everything collected so far is 1.25e9 comparisons over a 180-character shared prefix,
    /// which measures at about nine seconds — this bound is set well under that and well over the hashed cost.</summary>
    [Fact]
    public void CollectingOneWarningPerRecordDoesNotGetDearerAsTheBatchGrows()
    {
        const int batch = 50_000;
        var warnings = new string[batch];
        for (int i = 0; i < batch; i++) warnings[i] = Warning(i);
        var sink = new SkyPatcherOverlay.WarningSink();

        var sw = Stopwatch.StartNew();
        foreach (var w in warnings) sink.Add(w);
        sw.Stop();

        Assert.Equal(batch - SkyPatcherOverlay.WarningSink.Cap, sink.Overflow);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
                    $"collecting {batch} distinct warnings took {sw.Elapsed.TotalSeconds:0.##}s — the membership test is growing with the batch");
    }
}

using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>WriteSeq</c> against a destination that already holds a .seq (migrated from the <c>seq-write-guard</c> probe, #312):
/// identical bytes are left alone and reported as unchanged, different bytes are rewritten and reported as replaced,
/// and an identical file older than its plugin has its timestamp stamped forward.
/// </summary>
[Trait("tier", "integration")]
public sealed class SeqWriteUnchangedTests : IDisposable
{
    readonly SeqWriteWorld W = new();
    public void Dispose() => W.Dispose();

    /// <summary>Write the .seq once, then make it newer than the plugin and stamp it with the sentinel.</summary>
    void WriteCurrentSeqStampedWithSentinel()
    {
        Assert.True(W.WriteToUserMod().Success);
        File.SetLastWriteTimeUtc(W.SvcPlugin, SeqWriteWorld.Old);
        File.SetLastWriteTimeUtc(W.UserSeq, SeqWriteWorld.Sentinel);
    }

    // Probe UNCHANGED: "byte-identical destination → nothing written (mtime untouched), reported as its own state".
    [Fact]
    public void AByteIdenticalDestinationIsLeftAloneAndReportedUnchanged()
    {
        WriteCurrentSeqStampedWithSentinel();
        var o = W.WriteToUserMod();
        Assert.True(o.Unchanged);
        Assert.False(o.TimestampRefreshed);
        Assert.Equal(SeqWriteWorld.Sentinel, File.GetLastWriteTimeUtc(W.UserSeq));
        Assert.Equal(W.UserSeq, o.SeqPath, ignoreCase: true);
    }

    // Probe UNCHANGED-DIFFERS: "a stale destination IS rewritten (the short-circuit compares BYTES, not existence)".
    [Fact]
    public void ADestinationWithOtherBytesIsRewritten()
    {
        WriteCurrentSeqStampedWithSentinel();
        File.WriteAllBytes(W.UserSeq, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        File.SetLastWriteTimeUtc(W.UserSeq, SeqWriteWorld.Sentinel);
        var o = W.WriteToUserMod();
        Assert.False(o.Unchanged);
        Assert.NotEqual(SeqWriteWorld.Sentinel, File.GetLastWriteTimeUtc(W.UserSeq));
        Assert.NotEqual(0xDE, File.ReadAllBytes(W.UserSeq)[0]);
    }

    // Probe MTIME-REFRESH: "a byte-identical but OLDER .seq is stamped forward, not rewritten".
    [Fact]
    public void AnIdenticalSeqOlderThanItsPluginIsStampedForward()
    {
        Assert.True(W.WriteToUserMod().Success);
        File.SetLastWriteTimeUtc(W.UserSeq, SeqWriteWorld.Old);
        var pluginStamp = new DateTime(2020, 6, 6, 6, 6, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(W.SvcPlugin, pluginStamp);
        var o = W.WriteToUserMod();
        Assert.True(o.Unchanged);
        Assert.True(o.TimestampRefreshed);
        Assert.True(File.GetLastWriteTimeUtc(W.UserSeq) > pluginStamp);
    }

    // Probe MTIME-REFRESH-RENDER: "the stamp is STATED and its scope is bounded" — the sentence names the check that
    // reads the two mtimes and bounds itself to the copy the load order serves, and the render reads that sentence.
    [Fact]
    public void TheStampIsStatedAndBoundedToTheServedCopy()
    {
        Assert.True(W.WriteToUserMod().Success);
        File.SetLastWriteTimeUtc(W.UserSeq, SeqWriteWorld.Old);
        File.SetLastWriteTimeUtc(W.SvcPlugin, new DateTime(2020, 6, 6, 6, 6, 6, DateTimeKind.Utc));
        var render = SeqTools.Render(W.WriteToUserMod());
        Assert.Contains(ToolNames.Check + " findings=[\"dialogue\"] with the quest in seeds=", WriteSentences.Twins.SeqTimestampRefreshed);
        Assert.Contains("the copy the load order actually serves", WriteSentences.Twins.SeqTimestampRefreshed);
        Assert.Contains(WriteSentences.Twins.SeqTimestampRefreshed, render);
    }

    // Probe MTIME-FUTURE: "a future-stamped plugin still ends up OLDER than its .seq (the refresh is verified, not assumed)".
    [Fact]
    public void AFutureStampedPluginStillEndsUpOlderThanItsSeq()
    {
        Assert.True(W.WriteToUserMod().Success);
        File.SetLastWriteTimeUtc(W.SvcPlugin, DateTime.UtcNow.AddDays(30));
        File.SetLastWriteTimeUtc(W.UserSeq, SeqWriteWorld.Old);
        Assert.True(W.WriteToUserMod().Success);
        Assert.True(File.GetLastWriteTimeUtc(W.UserSeq) >= File.GetLastWriteTimeUtc(W.SvcPlugin));
    }

    // Probe REPLACED: "overwriting an existing .seq reports 'replaced', not 'wrote'", with the no-backup sentence.
    [Fact]
    public void OverwritingADifferentSeqIsReportedReplacedWithTheNoBackupSentence()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(W.UserSeq)!);
        File.WriteAllBytes(W.UserSeq, new byte[] { 1, 2, 3, 4, 5, 6 });
        var o = W.WriteToUserMod();
        var render = SeqTools.Render(o);
        Assert.True(o.Replaced);
        Assert.False(o.Unchanged);
        Assert.StartsWith("replaced ", render);
        Assert.Contains("keeps no backup", WriteSentences.Twins.SeqReplacedUserFolder);
        Assert.Contains(WriteSentences.Twins.SeqReplacedUserFolder, render);
    }

    // Probe REPLACED-NEG: "a first write to an empty destination is 'wrote', not 'replaced'".
    [Fact]
    public void AFirstWriteIsWroteNotReplaced()
    {
        var o = W.WriteToUserMod();
        Assert.False(o.Replaced);
        Assert.StartsWith("wrote ", SeqTools.Render(o));
    }
}

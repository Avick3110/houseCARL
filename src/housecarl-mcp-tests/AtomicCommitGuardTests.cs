using HousecarlCore;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// <c>AtomicFile.Commit</c>, the swap every final write funnels through (migrated from the <c>atomic-commit-guard</c>
/// probe). It must land a fresh file, replace an existing one byte-exact through <c>File.Replace</c>, use up the
/// staged file, and throw on a missing source or a held target with the prior file left intact. Crash-atomicity
/// itself cannot be shown in-process and is not claimed.
/// </summary>
[Trait("tier", "unit")]
public sealed class AtomicCommitGuardTests : IDisposable
{
    static readonly DateTime OldCreate = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-atomic-commit-tests-" + Guid.NewGuid().ToString("N"));

    readonly ITestOutputHelper _out;

    public AtomicCommitGuardTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a lingering lock fails its own test */ }
    }

    string At(string name) => Path.Combine(_root, name);

    // Probe A: "fresh target written byte-exact (rename branch)" and "staged temp consumed (fresh case)".
    [Fact]
    public void AFreshTargetIsWrittenByteExactAndTheStagedFileIsUsedUp()
    {
        var staged = At("a.tmp");
        var final = At("a.dat");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(staged, bytes);

        AtomicFile.Commit(staged, final);

        Assert.Equal(bytes, File.ReadAllBytes(final));
        Assert.False(File.Exists(staged));
    }

    // Probe B: "overwrite content is byte-exact the new staged bytes" and "staged temp consumed (overwrite case)".
    [Fact]
    public void AnOverwriteIsByteExactAndTheStagedFileIsUsedUp()
    {
        var staged = At("b.tmp");
        var final = At("b.dat");
        File.WriteAllBytes(final, new byte[] { 9, 9, 9 });
        var newBytes = new byte[] { 7, 7, 7, 7 };
        File.WriteAllBytes(staged, newBytes);

        AtomicFile.Commit(staged, final);

        Assert.Equal(newBytes, File.ReadAllBytes(final));
        Assert.False(File.Exists(staged));
    }

    // Probe B: "destination creation time preserved - File.Replace, not File.Move(overwrite)". On a host whose file
    // system tunneling keeps the creation time through File.Move too, the two cannot be told apart; the control
    // detects that and the test stops there, as the probe did.
    [Fact]
    public void AnOverwriteKeepsTheTargetsCreationTime()
    {
        var ctlStaged = At("ctl.tmp");
        var ctlFinal = At("ctl.dat");
        File.WriteAllBytes(ctlFinal, new byte[] { 0 });
        File.SetCreationTimeUtc(ctlFinal, OldCreate);
        File.WriteAllBytes(ctlStaged, new byte[] { 1 });
        File.Move(ctlStaged, ctlFinal, overwrite: true);
        if (File.GetCreationTimeUtc(ctlFinal) == OldCreate)
        {
            _out.WriteLine("Skipped: file system tunneling keeps the creation time through File.Move on this host.");
            return;
        }

        var staged = At("b2.tmp");
        var final = At("b2.dat");
        File.WriteAllBytes(final, new byte[] { 9, 9, 9 });
        File.SetCreationTimeUtc(final, OldCreate);
        File.WriteAllBytes(staged, new byte[] { 7, 7 });

        AtomicFile.Commit(staged, final);

        Assert.Equal(OldCreate, File.GetCreationTimeUtc(final));
    }

    // Probe C: "a missing staged source THROWS" and "prior target survives the failed pre-swap commit byte-for-byte".
    [Fact]
    public void AMissingStagedSourceThrowsAndThePriorFileSurvives()
    {
        var final = At("c.dat");
        var prior = new byte[] { 4, 2 };
        File.WriteAllBytes(final, prior);

        Assert.ThrowsAny<IOException>(() => AtomicFile.Commit(At("does-not-exist.tmp"), final));

        Assert.Equal(prior, File.ReadAllBytes(final));
    }

    // Probe C2: "a mid-swap failure on a locked target THROWS" and "prior target survives the failed mid-swap".
    [Fact]
    public void AHeldTargetThrowsAndThePriorFileSurvives()
    {
        var staged = At("c2.tmp");
        var final = At("c2.dat");
        var prior = new byte[] { 5, 5, 5, 5, 5 };
        File.WriteAllBytes(final, prior);
        File.WriteAllBytes(staged, new byte[] { 8, 8 });

        using (new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => AtomicFile.Commit(staged, final));

        Assert.Equal(prior, File.ReadAllBytes(final));
    }
}

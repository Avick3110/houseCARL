using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The pack contract with a real failing packer: a copy of Windows' where.exe stands in for BSArch, so the
/// pack shells out, the run errors, exits non-zero and writes nothing. A stuck stale scratch refuses before the run,
/// a failing run never moves a stale scratch over the target, and an unknown format token maps to no flag.</summary>
[Trait("tier", "unit")]
public sealed class BsaPackContractTests : IDisposable
{
    const string Prior = "PRIOR ARCHIVE — must survive";

    readonly string _root;
    readonly string _stub;
    readonly string _packDir;
    readonly string _target;
    readonly string _stale;

    public BsaPackContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-bsa-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _stub = Path.Combine(_root, "bsarch.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), _stub);
        _packDir = Path.Combine(_root, "pack");
        Directory.CreateDirectory(_packDir);
        _target = Path.Combine(_packDir, "Out.bsa");
        File.WriteAllText(_target, Prior);
        _stale = Path.Combine(_packDir, "Out.houseCARL-tmp.bsa");
        File.WriteAllText(_stale, "STALE PREVIOUS-RUN BYTES");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    // Probe: "stuck stale scratch → loud refusal naming it (nothing packed)" and "the prior archive is byte-untouched".
    [Fact]
    public void AStuckStaleScratchRefusesBeforeRunningAndLeavesThePriorArchive()
    {
        BsaPackResult rp;
        using (File.Open(_stale, FileMode.Open, FileAccess.Read, FileShare.None))
            rp = BsaArchive.Pack(_stub, _packDir, _target, "-sse", compress: false, timeoutMs: 30_000);

        Assert.False(rp.Success);
        Assert.False(rp.Ran);
        Assert.Contains("stale", rp.RunError);
        Assert.Equal(Prior, File.ReadAllText(_target));
    }

    // Probe: "deletable stale + failing BSArch = refusal naming the exit code (no stale shipped)" and "the prior archive
    // survives that path too". Strengthened: the stale scratch is gone afterwards, so it cannot ship on a later run.
    [Fact]
    public void AFailingPackerAfterADeletableStaleScratchRefusesNamingTheExitCode()
    {
        var rp = BsaArchive.Pack(_stub, _packDir, _target, "-sse", compress: false, timeoutMs: 30_000);

        Assert.False(rp.Success);
        Assert.Contains("exited with code", rp.RunError);
        Assert.Equal(Prior, File.ReadAllText(_target));
        Assert.False(File.Exists(_stale));
    }

    // Probe: "sse family + empty default to -sse".
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sse")]
    [InlineData("AE")]
    [InlineData("skyrimse")]
    public void TheSseFamilyAndNoTokenMapToSse(string? token) => Assert.Equal("-sse", BsaArchive.TryFormatFlag(token));

    // Probe: "legal tokens map to their flags".
    [Theory]
    [InlineData("tes5", "-tes5")]
    [InlineData("fo4dds", "-fo4dds")]
    [InlineData("oblivion", "-tes4")]
    [InlineData("starfield", "-sf1")]
    public void LegalTokensMapToTheirFlags(string token, string flag) => Assert.Equal(flag, BsaArchive.TryFormatFlag(token));

    // Probe: "unknown tokens REFUSE (null) — no silent -sse from a typo".
    [Theory]
    [InlineData("fo4dd")]
    [InlineData("garbage")]
    public void UnknownTokensMapToNoFlag(string token) => Assert.Null(BsaArchive.TryFormatFlag(token));
}

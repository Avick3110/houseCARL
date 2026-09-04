using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The repack read-back, against a real archive: a pack reads the produced .bsa's own header count and refuses
/// when it disagrees with the source. The suite has no BSArch, so a substituted packer writes an archive authored by
/// <see cref="BsaBuilder"/> — the header the read-back parses is a real one.</summary>
[Trait("tier", "unit")]
public sealed class BsaPackReadBackTests : IDisposable
{
    readonly string _root;

    public BsaPackReadBackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-bsa-readback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    static readonly (string Folder, (string Name, byte[] Data)[] Files)[] Contents =
    {
        ("meshes", new[] { ("a.nif", BsaBuilder.Bytes("NIF-a", 64)), ("b.nif", BsaBuilder.Bytes("NIF-b", 12)) }),
        ("scripts", new[] { ("c.pex", BsaBuilder.Bytes("PEX-c", 300)) }),
    };

    /// <summary>A source folder holding the same three files the authored archive carries.</summary>
    string ThreeFileSource()
    {
        var src = Path.Combine(_root, "src");
        foreach (var (folder, files) in Contents)
        {
            Directory.CreateDirectory(Path.Combine(src, folder));
            foreach (var (name, data) in files) File.WriteAllBytes(Path.Combine(src, folder, name), data);
        }
        return src;
    }

    /// <summary>A packer that ignores BSArch and writes <paramref name="archive"/> at the scratch path, reporting
    /// <paramref name="exit"/> as the run's exit code.</summary>
    static BsaPacker Writes(byte[] archive, int exit = 0, string stderr = "") => (_, _, tmpArchive, _, _, _) =>
    {
        File.WriteAllBytes(tmpArchive, archive);
        return (exit, "", stderr, null);
    };

    [Fact]
    public void PackReadsTheProducedArchiveCountBack()
    {
        var src = ThreeFileSource();
        var target = Path.Combine(_root, "Out.bsa");
        var archive = BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, Contents);

        var result = BsaArchive.Pack("bsarch.exe", src, target, "-sse", compress: false, packer: Writes(archive));

        Assert.True(result.Success);
        Assert.Null(result.CountError);
        Assert.Equal(3, result.Packed);
        Assert.Equal(3, result.Expected);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void PackRefusesAnArchiveThatCountsShort()
    {
        var src = ThreeFileSource();
        var target = Path.Combine(_root, "Out.bsa");
        File.WriteAllText(target, "PRIOR ARCHIVE");
        var archive = BsaBuilder.WithDeclaredFileCount(
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, Contents), 2);

        var result = BsaArchive.Pack("bsarch.exe", src, target, "-sse", compress: false, packer: Writes(archive));

        Assert.False(result.Success);
        Assert.Equal(2, result.Packed);
        Assert.Equal(3, result.Expected);
        var refusal = Assert.IsType<string>(result.CountError);
        Assert.Contains("2 file(s)", refusal);
        Assert.Contains("offers 3", refusal);
        Assert.Equal("PRIOR ARCHIVE", File.ReadAllText(target));
    }

    /// <summary>A packer that exits non-zero has failed, whatever it left on disk — even a scratch whose header count
    /// agrees with the source. The refusal names the exit code and what the packer printed.</summary>
    [Fact]
    public void PackRefusesAScratchFromANonZeroExit()
    {
        var src = ThreeFileSource();
        var target = Path.Combine(_root, "Out.bsa");
        File.WriteAllText(target, "PRIOR ARCHIVE");
        var archive = BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, Contents);

        var result = BsaArchive.Pack("bsarch.exe", src, target, "-sse", compress: false,
            packer: Writes(archive, exit: 1, stderr: "aborted at meshes/b.nif"));

        Assert.False(result.Success);
        var refusal = Assert.IsType<string>(result.RunError);
        Assert.Contains("code 1", refusal);
        Assert.Contains("aborted at meshes/b.nif", refusal);
        Assert.Equal("PRIOR ARCHIVE", File.ReadAllText(target));
    }

    /// <summary>A scratch written the instant the packer is called passes the pack's provenance gate. The gate used to
    /// compare an NTFS write stamp against a precise-clock baseline, so an immediate write read as stale better than
    /// half the time (#522) — hence the loop.</summary>
    [Fact]
    public void PackAcceptsAScratchWrittenImmediately()
    {
        var src = ThreeFileSource();
        var archive = BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, Contents);

        for (var i = 0; i < 20; i++)
        {
            var target = Path.Combine(_root, $"Loop{i}.bsa");
            var result = BsaArchive.Pack("bsarch.exe", src, target, "-sse", compress: false, packer: Writes(archive));
            Assert.True(result.Success, $"run {i} was refused: {result.RunError ?? result.Raw}");
        }
    }
}

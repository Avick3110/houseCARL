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

    /// <summary>A packer that ignores BSArch and writes <paramref name="archive"/> at the scratch path. The write time is
    /// stamped explicitly to get past the pack's own provenance gate: NTFS records a last-write time coarser than the
    /// clock that gate's baseline is read from, so an instant write reads as stale (#522). Drop the stamp once that is
    /// fixed — these tests are about the count read-back, not the gate.</summary>
    static BsaPacker Writes(byte[] archive) => (_, _, tmpArchive, _, _, _) =>
    {
        File.WriteAllBytes(tmpArchive, archive);
        File.SetLastWriteTimeUtc(tmpArchive, DateTime.UtcNow);
        return ("", "", null);
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
}

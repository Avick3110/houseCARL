using HousecarlGenerator;

namespace HousecarlMcpTests;

/// <summary>Two small SSE archives authored in memory for the resolver and asset-status tests: A holds a FaceGen
/// path and a shared path, B holds only the shared path, so the shared path's winner among archives is decided by
/// rank alone.</summary>
static class ResolverArchives
{
    /// <summary>A FaceGen head path, in archive A only.</summary>
    public const string FacegenRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";
    /// <summary>A path in both archives.</summary>
    public const string RankRel = @"meshes\rank\only-in-bsas.nif";

    const uint Named = BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames;

    static (string, (string, byte[])[]) Entry(string rel, string tag)
        => (Path.GetDirectoryName(rel)!, new[] { (Path.GetFileName(rel), BsaBuilder.Bytes(tag, 16)) });

    public static byte[] A() => BsaBuilder.Build(105, Named, new[] { Entry(FacegenRel, "A-FACE"), Entry(RankRel, "A-RANK") });

    public static byte[] B() => BsaBuilder.Build(105, Named, new[] { Entry(RankRel, "B-RANK") });

    /// <summary>Archive A plus one more path, for a repack that changes the table.</summary>
    public static byte[] AWith(string rel) =>
        BsaBuilder.Build(105, Named, new[] { Entry(FacegenRel, "A-FACE"), Entry(RankRel, "A-RANK"), Entry(rel, "A-ADDED") });

    /// <summary>The first third of archive A: the table read fails.</summary>
    public static byte[] Truncated() { var a = A(); return a[..(a.Length / 3)]; }

    public static string Write(string dir, string name, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    public static void Loose(string baseDir, string rel, string body = "x")
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, body);
    }
}

using HousecarlGenerator;

namespace HousecarlMcpTests;

/// <summary>The in-memory archives the extract tests unpack: three files in two folders, authored by
/// <see cref="BsaBuilder"/>, plus the checks that read what an unpack left on disk.</summary>
static class BsaExtractArchives
{
    public const uint Named = BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames;

    /// <summary>Three files: two scripts of different sizes and a one-byte voice file, the smallest edge.</summary>
    public static readonly (string Folder, (string Name, byte[] Data)[] Files)[] ThreeFiles =
    {
        ("scripts", new[]
        {
            ("main.pex", BsaBuilder.Bytes("PEX-main", 40)),
            ("helper.pex", BsaBuilder.Bytes("PEX-help", 4096)),
        }),
        (@"sound\voice\test.esp\femalecommoner", new[]
        {
            ("hello_000012ab_1.fuz", BsaBuilder.Bytes("FUZ-audio", 1)),
        }),
    };

    /// <summary>An archive whose one folder is <c>..</c>, so its entry resolves one level above the destination.</summary>
    public static byte[] Escaping() =>
        BsaBuilder.Build(105, Named, new (string, (string, byte[])[])[]
        {
            ("..", new[] { ("escape.txt", BsaBuilder.Bytes("nope", 8)) }),
        });

    public static string Write(string dir, string name, byte[] bytes)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>True when every file of <paramref name="folders"/> sits at its folder path under
    /// <paramref name="dest"/> with exactly its authored bytes.</summary>
    public static bool AllBytesCorrect(string dest, (string Folder, (string Name, byte[] Data)[] Files)[] folders)
    {
        foreach (var (folder, files) in folders)
            foreach (var (name, data) in files)
            {
                var path = Path.Combine(dest, folder.Replace('\\', Path.DirectorySeparatorChar), name);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return false;
            }
        return true;
    }
}

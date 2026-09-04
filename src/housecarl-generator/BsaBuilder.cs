using System.Text;

namespace HousecarlGenerator;

/// <summary>Authors byte-exact uncompressed BSA archives in memory, so a probe or a test can exercise the real read
/// path without BSArch and without a checked-in binary. Shared by bsa-extract-guard and the pack read-back tests.</summary>
public static class BsaBuilder
{
    public const uint HasFolderNames = 0x0001;
    public const uint HasFileNames = 0x0002;

    /// <summary>Deterministic per-file body (varying content + length), so a mis-mapped name/offset is caught.</summary>
    public static byte[] Bytes(string tag, int len)
    {
        var seed = Encoding.ASCII.GetBytes(tag);
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(seed[i % seed.Length] ^ (i * 31 + 7));
        return b;
    }

    /// <summary>Author a byte-exact uncompressed BSA (folder+file names present, little-endian) — the standard
    /// header -> folder records -> folder blocks -> file-name block -> file data layout Mutagen's reader parses.</summary>
    public static byte[] Build(uint version, uint archiveFlags, (string Folder, (string Name, byte[] Data)[] Files)[] folders)
    {
        int frSize = version == 105 ? 24 : 16;
        uint folderCount = (uint)folders.Length;
        uint fileCount = (uint)folders.Sum(f => f.Files.Length);
        uint totalFolderNameLen = (uint)folders.Sum(f => f.Folder.Length + 1);
        uint totalFileNameLen = (uint)folders.SelectMany(f => f.Files).Sum(x => x.Name.Length + 1);

        long folderRecordsEnd = 36 + (long)folderCount * frSize;
        var blockSizes = folders.Select(f => 1L + (f.Folder.Length + 1) + f.Files.Length * 16L).ToArray();
        long nameBlockStart = folderRecordsEnd + blockSizes.Sum();
        long fileDataStart = nameBlockStart + totalFileNameLen;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII);

        bw.Write(0x00415342u);          // "BSA\0"
        bw.Write(version);
        bw.Write(36u);                  // folder-records offset
        bw.Write(archiveFlags);
        bw.Write(folderCount);
        bw.Write(fileCount);
        bw.Write(totalFolderNameLen);
        bw.Write(totalFileNameLen);
        bw.Write(0u);                   // file (content-type) flags

        long blockOffset = folderRecordsEnd;
        for (int i = 0; i < folders.Length; i++)
        {
            bw.Write(0UL);                                           // hash
            bw.Write((uint)folders[i].Files.Length);
            if (frSize == 24) { bw.Write(0u); bw.Write((ulong)(blockOffset + totalFileNameLen)); }
            else bw.Write((uint)(blockOffset + totalFileNameLen));
            blockOffset += blockSizes[i];
        }

        long dataCursor = fileDataStart;
        var dataOrder = new List<byte[]>();
        foreach (var (folder, files) in folders)
        {
            var fn = Encoding.ASCII.GetBytes(folder);
            bw.Write((byte)(fn.Length + 1));   // bzstring length INCLUDING the null
            bw.Write(fn);
            bw.Write((byte)0);
            foreach (var (_, data) in files)
            {
                bw.Write(0UL);                 // file name hash
                bw.Write((uint)data.Length);   // size field — uncompressed, no toggle bit
                bw.Write((uint)dataCursor);    // absolute data offset
                dataCursor += data.Length;
                dataOrder.Add(data);
            }
        }

        foreach (var (_, files) in folders)
            foreach (var (name, _) in files) { bw.Write(Encoding.ASCII.GetBytes(name)); bw.Write((byte)0); }

        foreach (var d in dataOrder) bw.Write(d);

        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Rewrite the header's declared file count (offset 20) so an archive can be made to lie about itself.</summary>
    public static byte[] WithDeclaredFileCount(byte[] archive, uint fileCount)
    {
        var copy = (byte[])archive.Clone();
        BitConverter.GetBytes(fileCount).CopyTo(copy, 20);
        return copy;
    }
}

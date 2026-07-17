using System.Text;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// BSA extract guard — self-contained, no BSArch and no real archive needed. It hand-authors valid uncompressed BSA
/// archives in memory (Mutagen reads them, verified) and drives the shipped <see cref="BsaArchive.Unpack"/> (which reads
/// via Mutagen's in-process BSA reader). Pins the read path the housecarl_bsa_extract tool rides on:
///   1. v105 + v104 uncompressed round-trip: every file extracted byte-correct at the right path.
///   2. Content-aware idempotence: a second extract writes nothing and reports all-already-present.
///   3. Path-traversal safety: an archive entry that resolves outside the dest refuses (Q3) and writes nothing out-of-tree.
///   4. Loud failure: an unreadable/non-archive file fails with a named reason, never a silent empty success.
/// The real-BSArch parity (incl. compressed archives) is the separate, opt-in bsa-probe.
/// </summary>
internal static class BsaExtractProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" bsa extract guard (Mutagen read path) — no BSArch needed");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var folders = new (string Folder, (string Name, byte[] Data)[] Files)[]
        {
            ("scripts", new[]
            {
                ("main.pex",   Bytes("PEX-main", 40)),
                ("helper.pex", Bytes("PEX-help", 4096)),
            }),
            (@"sound\voice\test.esp\femalecommoner", new[]
            {
                ("hello_000012ab_1.fuz", Bytes("FUZ-audio", 1)),   // 1-byte body — smallest edge
            }),
        };

        var work = Path.Combine(Path.GetTempPath(), "hc-bsa-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // ---- 1 + 2) round-trip + idempotence, v105 and v104 ----
            Console.WriteLine("--- 1: uncompressed round-trip via BsaArchive.Unpack (Mutagen) ---");
            foreach (uint version in new uint[] { 105, 104 })
            {
                var bsa = Write(work, $"round-{version}.bsa", BuildBsa(version, FHasFolderNames | FHasFileNames, folders));
                var dest = Path.Combine(work, $"round-{version}");
                var r = BsaArchive.Unpack(bsa, dest);
                Check(r.Ran && r.Success, $"v{version}: extract Success ({r.Raw})");
                Check(AllBytesCorrect(dest, folders), $"v{version}: every file extracted byte-correct at the right path");
                Check(r.Raw.Contains("extracted 3", StringComparison.OrdinalIgnoreCase), $"v{version}: reports 3 written");

                var r2 = BsaArchive.Unpack(bsa, dest);   // re-extract into the populated dest
                Check(r2.Ran && r2.Success && r2.Raw.Contains("already present", StringComparison.OrdinalIgnoreCase),
                      $"v{version}: re-extract is a content-aware no-op ({r2.Raw})");
            }

            // ---- 3) path-traversal safety ----
            Console.WriteLine();
            Console.WriteLine("--- 3: path traversal ---");
            var evil = BuildBsa(105, FHasFolderNames | FHasFileNames, new (string, (string, byte[])[])[]
            {
                ("..", new[] { ("escape.txt", Bytes("nope", 8)) }),
            });
            var evilDest = Path.Combine(work, "evil-dest");
            var er = BsaArchive.Unpack(Write(work, "evil.bsa", evil), evilDest);
            Check(!er.Success, $"'..' entry -> extract refuses ({(er.Ran ? er.Raw : er.RunError)})");
            Check(!File.Exists(Path.Combine(work, "escape.txt")), "nothing written outside the destination");

            // ---- 4) loud failure on a non-archive ----
            Console.WriteLine();
            Console.WriteLine("--- 4: unreadable input ---");
            var junk = Write(work, "junk.bsa", new byte[] { 0x42, 0x53, 0x41, 0x00, 1, 2, 3, 4 });
            var jr = BsaArchive.Unpack(junk, Path.Combine(work, "junk-out"));
            Check(!jr.Ran && !string.IsNullOrWhiteSpace(jr.RunError), $"garbage archive -> loud open error ({jr.RunError})");
            var listJr = BsaArchive.List(junk);
            Check(!listJr.Ran && !string.IsNullOrWhiteSpace(listJr.RunError), "list of a garbage archive -> loud error too");
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }

    const uint FHasFolderNames = 0x0001;
    const uint FHasFileNames = 0x0002;

    /// <summary>Deterministic per-file body (varying content + length), so a mis-mapped name/offset is caught.</summary>
    static byte[] Bytes(string tag, int len)
    {
        var seed = Encoding.ASCII.GetBytes(tag);
        var b = new byte[len];
        for (int i = 0; i < len; i++) b[i] = (byte)(seed[i % seed.Length] ^ (i * 31 + 7));
        return b;
    }

    static string Write(string dir, string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }

    static bool AllBytesCorrect(string dest, (string Folder, (string Name, byte[] Data)[] Files)[] folders)
    {
        foreach (var (folder, files) in folders)
            foreach (var (name, data) in files)
            {
                var rel = (folder.Length == 0 ? name : folder + "\\" + name).Replace('\\', Path.DirectorySeparatorChar);
                var path = Path.Combine(dest, rel);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) return false;
            }
        return true;
    }

    /// <summary>Author a byte-exact uncompressed BSA (folder+file names present, little-endian) — the standard
    /// header -> folder records -> folder blocks -> file-name block -> file data layout Mutagen's reader parses.</summary>
    static byte[] BuildBsa(uint version, uint archiveFlags, (string Folder, (string Name, byte[] Data)[] Files)[] folders)
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
}

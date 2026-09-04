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
    [CiProbe("bsa-extract-guard")]
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
                ("main.pex",   BsaBuilder.Bytes("PEX-main", 40)),
                ("helper.pex", BsaBuilder.Bytes("PEX-help", 4096)),
            }),
            (@"sound\voice\test.esp\femalecommoner", new[]
            {
                ("hello_000012ab_1.fuz", BsaBuilder.Bytes("FUZ-audio", 1)),   // 1-byte body — smallest edge
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
                var bsa = Write(work, $"round-{version}.bsa", BsaBuilder.Build(version, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, folders));
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
            var evil = BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, new (string, (string, byte[])[])[]
            {
                ("..", new[] { ("escape.txt", BsaBuilder.Bytes("nope", 8)) }),
            });
            var evilDest = Path.Combine(work, "evil-dest");
            var er = BsaArchive.Unpack(Write(work, "evil.bsa", evil), evilDest);
            Check(er.Ran && !er.Success && er.Raw.Contains("path traversal", StringComparison.OrdinalIgnoreCase),
                  $"'..' entry -> the IsUnder guard refuses (not an upstream reject) ({(er.Ran ? er.Raw : er.RunError)})");
            Check(!File.Exists(Path.Combine(work, "escape.txt")), "nothing written outside the destination");

            // ---- 3b) header/reader file-count mismatch -> loud (the #217 silent-empty/short-extract guard) ----
            var good = BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, folders);   // 3 real files
            var lie = BsaBuilder.WithDeclaredFileCount(good, 1);                   // header @20 now lies: "1 file"
            var lr = BsaArchive.Unpack(Write(work, "count-lie.bsa", lie), Path.Combine(work, "count-out"));
            Check(!lr.Success, $"lying header count -> no silent success ({(lr.Ran ? lr.Raw : lr.RunError)})");

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

}

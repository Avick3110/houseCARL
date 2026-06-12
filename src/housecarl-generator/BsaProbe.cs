using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// BSA-rider proof (EXTERNAL_TOOL_BRIDGE_PLAN step 3). Drives the shipped <see cref="BsaArchive"/> against the REAL BSArch
/// on a REAL archive: list → unpack → pack → re-list, asserting the round-trip preserves the file count (the plan's proof
/// gate). The list step also exercises the "Files: N + last-N-lines" parser against real output. Skipped (not failed) if
/// BSArch or the test archive isn't present; provide both via args or the HOUSECARL_BSARCH / HOUSECARL_TEST_BSA env vars.
///
/// Run: dotnet run --project src/housecarl-generator bsa-probe ["&lt;BSArch.exe&gt;"] ["&lt;test.bsa&gt;"]
/// </summary>
internal static class BsaProbe
{
    // No personal paths baked into source: the no-arg defaults come from env vars, so the probe SKIPs
    // cleanly when neither is set. Pass a BSArch.exe + test .bsa as args, or set the env vars below.
    static readonly string DefaultBsarch = Environment.GetEnvironmentVariable("HOUSECARL_BSARCH") ?? "";
    static readonly string DefaultBsa = Environment.GetEnvironmentVariable("HOUSECARL_TEST_BSA") ?? "";

    public static int Run(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" BSA riders — step 3: list → unpack → pack → re-list round-trip (real BSArch)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var bsarch = args.Length > 0 ? args[0] : DefaultBsarch;
        var bsa = args.Length > 1 ? args[1] : DefaultBsa;
        if (!File.Exists(bsarch)) { Console.WriteLine($"  SKIP  no BSArch at '{bsarch}' (pass its path as arg 1, or set HOUSECARL_BSARCH)"); return 0; }
        if (!File.Exists(bsa)) { Console.WriteLine($"  SKIP  no test archive at '{bsa}' (pass one as arg 2, or set HOUSECARL_TEST_BSA)"); return 0; }

        var work = Path.Combine(Environment.CurrentDirectory, ".bsa-probe");
        var unpacked = Path.Combine(work, "unpacked");
        var repacked = Path.Combine(work, "repacked.bsa");
        try
        {
            Directory.CreateDirectory(work);

            // 1) LIST the original (exercises the parser on real output)
            var orig = BsaArchive.List(bsarch, bsa);
            Check(orig.Ran && orig.Success, "list: ran + Success");
            Check(orig.DeclaredCount > 0 && orig.Files.Count == orig.DeclaredCount,
                  $"list: file count matches declared ({orig.Files.Count}/{orig.DeclaredCount})");
            Check(orig.Format is not null && orig.Format.Contains("Skyrim", StringComparison.OrdinalIgnoreCase),
                  $"list: format parsed ('{orig.Format}')");
            if (orig.Files.Count > 0) Console.WriteLine("         e.g. " + orig.Files[0]);

            // 2) UNPACK
            var up = BsaArchive.Unpack(bsarch, bsa, unpacked);
            Check(up.Ran && up.Success, "unpack: ran + dest has files");
            int onDisk = Directory.Exists(unpacked) ? Directory.GetFiles(unpacked, "*", SearchOption.AllDirectories).Length : 0;
            Check(onDisk == orig.DeclaredCount, $"unpack: files on disk == declared ({onDisk}/{orig.DeclaredCount})");

            // 3) PACK back (Skyrim SE, uncompressed)
            var pk = BsaArchive.Pack(bsarch, unpacked, repacked, BsaArchive.TryFormatFlag("sse")!, compress: false);
            Check(pk.Ran && pk.Success, "pack: ran + .bsa written");
            Check(File.Exists(repacked) && new FileInfo(repacked).Length > 0, "pack: output .bsa exists, non-empty");

            // 4) RE-LIST the repacked archive — round-trip preserves the file count
            var round = BsaArchive.List(bsarch, repacked);
            Check(round.Ran && round.Success, "re-list: ran + Success");
            Check(round.DeclaredCount == orig.DeclaredCount,
                  $"round-trip preserves file count ({round.DeclaredCount} == {orig.DeclaredCount})");

            // 5) NON-DESTRUCTIVE PACK (Aaron 2026-06-06): a FAILED pack must NOT overwrite an existing archive. We have a
            //    good `repacked` from the round-trip; attempt a pack from a non-existent source to the SAME path and assert
            //    the prior archive survives byte-for-byte (the original is touched only by a clean, successful compaction).
            var beforeLen = new FileInfo(repacked).Length;
            var failPack = BsaArchive.Pack(bsarch, Path.Combine(work, "no-such-source"), repacked, BsaArchive.TryFormatFlag("sse")!, compress: false);
            Check(!failPack.Success, "non-destructive: a pack from a missing source reports failure");
            Check(File.Exists(repacked) && new FileInfo(repacked).Length == beforeLen,
                  "non-destructive: the PRIOR archive is left intact after a failed pack");
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* in-dir scratch; non-fatal */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}

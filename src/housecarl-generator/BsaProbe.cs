using System.Diagnostics;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// BSA read/write round-trip proof on a REAL archive: list → unpack (both via Mutagen's in-process reader) → pack (via
/// BSArch) → re-list, plus the load-bearing gate — Mutagen's unpack is BYTE-FOR-BYTE identical to BSArch's own unpack of
/// the same archive (the independent-implementation check that the self-contained bsa-mutagen-extract-guard can't give).
/// Skipped (not failed) if BSArch or the test archive isn't present; provide both via args or the HOUSECARL_BSARCH /
/// HOUSECARL_TEST_BSA env vars. BSArch is needed only for the pack step + the parity oracle — reads no longer use it.
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
        Console.WriteLine(" BSA riders — list/unpack (Mutagen) vs BSArch parity + pack round-trip");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var bsarch = args.Length > 0 ? args[0] : DefaultBsarch;
        var bsa = args.Length > 1 ? args[1] : DefaultBsa;
        if (!File.Exists(bsarch)) { Console.WriteLine($"  SKIP  no BSArch at '{bsarch}' (pass its path as arg 1, or set HOUSECARL_BSARCH)"); return 0; }
        if (!File.Exists(bsa)) { Console.WriteLine($"  SKIP  no test archive at '{bsa}' (pass one as arg 2, or set HOUSECARL_TEST_BSA)"); return 0; }

        var work = Path.Combine(Environment.CurrentDirectory, ".bsa-probe");
        var unpacked = Path.Combine(work, "unpacked");            // Mutagen unpack
        var bsarchDir = Path.Combine(work, "bsarch-unpacked");    // BSArch unpack (the parity oracle)
        var repacked = Path.Combine(work, "repacked.bsa");
        try
        {
            Directory.CreateDirectory(work);

            // 1) LIST via Mutagen
            var orig = BsaArchive.List(bsa);
            Check(orig.Ran && orig.Success, "list (Mutagen): ran + Success");
            Check(orig.DeclaredCount > 0 && orig.Files.Count == orig.DeclaredCount,
                  $"list: file count self-consistent ({orig.Files.Count})");
            Check(orig.Format is not null && orig.Format.Contains("BSA v", StringComparison.OrdinalIgnoreCase),
                  $"list: version label read ('{orig.Format}')");
            if (orig.Files.Count > 0) Console.WriteLine("         e.g. " + orig.Files[0]);

            // 2) UNPACK via Mutagen
            var up = BsaArchive.Unpack(bsa, unpacked);
            Check(up.Ran && up.Success, $"unpack (Mutagen): ran + Success ({up.Raw})");
            int onDisk = CountFiles(unpacked);
            Check(onDisk == orig.DeclaredCount, $"unpack: files on disk == listed ({onDisk}/{orig.DeclaredCount})");

            // 2b) THE GATE — Mutagen's unpack is byte-for-byte identical to BSArch's own unpack of the same archive.
            //     This is the independent-implementation oracle (handles compressed archives too — BSArch decompresses,
            //     and so must Mutagen, or the bytes won't match).
            Directory.CreateDirectory(bsarchDir);
            bool bsarchOk = BsarchUnpack(bsarch, bsa, bsarchDir);
            Check(bsarchOk, "oracle: BSArch unpacked the same archive");
            Check(bsarchOk && TreesByteIdentical(unpacked, bsarchDir), "Mutagen unpack == BSArch unpack (BYTE-FOR-BYTE)");

            // 3) PACK back via BSArch (Skyrim SE, uncompressed)
            var pk = BsaArchive.Pack(bsarch, unpacked, repacked, BsaArchive.TryFormatFlag("sse")!, compress: false);
            Check(pk.Ran && pk.Success, "pack (BSArch): ran + .bsa written");
            Check(File.Exists(repacked) && new FileInfo(repacked).Length > 0, "pack: output .bsa exists, non-empty");

            // 4) RE-LIST the repacked archive via Mutagen — round-trip preserves the file count
            var round = BsaArchive.List(repacked);
            Check(round.Ran && round.Success, "re-list (Mutagen): ran + Success");
            Check(round.DeclaredCount == orig.DeclaredCount,
                  $"round-trip preserves file count ({round.DeclaredCount} == {orig.DeclaredCount})");

            // 5) NON-DESTRUCTIVE PACK (Aaron 2026-06-06): a FAILED pack must NOT overwrite an existing archive.
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

    static int CountFiles(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length : 0;

    /// <summary>Drive BSArch's own unpack directly (the parity oracle) — NOT through BsaArchive, whose unpack is Mutagen.</summary>
    static bool BsarchUnpack(string bsarch, string archive, string dest)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = bsarch, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(bsarch) };
            foreach (var a in new[] { "unpack", archive, dest, "-mt" }) psi.ArgumentList.Add(a);
            var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            return CountFiles(dest) > 0;
        }
        catch { return false; }
    }

    /// <summary>Two extracted trees hold exactly the same relative paths, each byte-identical.</summary>
    static bool TreesByteIdentical(string a, string b)
    {
        string[] Rel(string root) => Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray()
            : Array.Empty<string>();
        var ra = Rel(a); var rb = Rel(b);
        if (!ra.SequenceEqual(rb, StringComparer.OrdinalIgnoreCase)) return false;
        foreach (var rel in ra)
            if (!File.ReadAllBytes(Path.Combine(a, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(b, rel))))
                return false;
        return true;
    }
}

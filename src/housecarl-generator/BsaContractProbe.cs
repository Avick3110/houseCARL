using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// BSA PACK contract guard — self-contained (a copy of Windows' own where.exe stands in for a failing BSArch: it runs,
/// errors, exits non-zero, and writes NOTHING). Locks the pack-provenance + format contracts the 2026-06-12 adversarial
/// hunt proved broken. (The unpack/list provenance arms this guard used to carry are gone: reads now go through Mutagen's
/// in-process reader, not BSArch, so there is no external-tool success to misread — bsa-extract-guard covers the read
/// path, and bsa-probe covers real-BSArch pack + Mutagen/BSArch byte parity.)
///
/// What it locks:
///   1. PACK PROVENANCE — "tmp exists and is non-empty" proved nothing about WHO made it: a stale scratch from a previous
///      run could ship as this run's archive. A stuck (undeletable) stale scratch now REFUSES loud before running; a
///      fresh-run mtime baseline gates the move; a failing pack leaves any prior archive byte-untouched.
///   2. FORMAT REFUSAL — an unknown format token used to coerce silently to -sse; TryFormatFlag now returns null for
///      refusal (legal tokens keep their flags; the sse family defaults).
/// </summary>
internal static class BsaContractProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" bsa contract guard — pack provenance + format refusal");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var work = Path.Combine(Path.GetTempPath(), "hc-bsa-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // the failing-BSArch stand-in: runs, errors to stderr, exits non-zero, writes nothing
            var stub = Path.Combine(work, "bsarch.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "where.exe"), stub);

            // ---- 1) pack: stuck stale scratch refuses; target untouched ----
            Console.WriteLine("--- 1: pack provenance ---");
            var packDir = Path.Combine(work, "pack");
            Directory.CreateDirectory(packDir);
            var target = Path.Combine(packDir, "Out.bsa");
            File.WriteAllText(target, "PRIOR ARCHIVE — must survive");
            var stale = Path.Combine(packDir, "Out.houseCARL-tmp.bsa");
            File.WriteAllText(stale, "STALE PREVIOUS-RUN BYTES");
            using (File.Open(stale, FileMode.Open, FileAccess.Read, FileShare.None))   // lock it: the delete must fail
            {
                var rp = BsaArchive.Pack(stub, packDir, target, "-sse", compress: false, timeoutMs: 30_000);
                Check(!rp.Success && !rp.Ran && (rp.RunError?.Contains("stale", StringComparison.OrdinalIgnoreCase) ?? false),
                      "stuck stale scratch → loud refusal naming it (nothing packed)");
            }
            Check(File.ReadAllText(target) == "PRIOR ARCHIVE — must survive", "the prior archive is byte-untouched");
            // unlocked stale: delete succeeds, stub packs nothing → honest failure, stale gone, target untouched
            var rp2 = BsaArchive.Pack(stub, packDir, target, "-sse", compress: false, timeoutMs: 30_000);
            Check(rp2.Ran && !rp2.Success, "deletable stale + failing BSArch = honest failure (no stale shipped)");
            Check(File.ReadAllText(target) == "PRIOR ARCHIVE — must survive", "the prior archive survives that path too");

            // ---- 2) format refusal ----
            Console.WriteLine();
            Console.WriteLine("--- 2: format tokens ---");
            Check(BsaArchive.TryFormatFlag(null) == "-sse" && BsaArchive.TryFormatFlag("sse") == "-sse"
                  && BsaArchive.TryFormatFlag("AE") == "-sse", "sse family + empty default to -sse");
            Check(BsaArchive.TryFormatFlag("tes5") == "-tes5" && BsaArchive.TryFormatFlag("fo4dds") == "-fo4dds",
                  "legal tokens map to their flags");
            Check(BsaArchive.TryFormatFlag("fo4dd") is null && BsaArchive.TryFormatFlag("garbage") is null,
                  "unknown tokens REFUSE (null) — no silent -sse from a typo");
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* temp scratch */ } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "================ ALL PASS ================" : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}

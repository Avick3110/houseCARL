using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// BSA bridge CONTRACT guard — self-contained (no BSArch needed): locks in the success/provenance
/// contracts the 2026-06-12 adversarial hunt proved broken. The stand-in for a failing BSArch is a
/// copy of Windows' own where.exe (present on every runner): it runs, errors, exits non-zero, and
/// extracts/packs NOTHING — exactly the failure shape the old success tests mistook for success.
///
/// What it locks (each was a real finding):
///   1. UNPACK PROVENANCE — the managed flow pre-seeds the dest folder (meta.ini ownership marker
///      is written BEFORE BSArch runs), and the old success test was "folder non-empty afterwards":
///      the marker alone satisfied it, so EVERY BSArch failure rendered as a successful extract
///      (MUST-FIX; independently proven by two hunters). Success now = THIS RUN added or changed
///      entries. Arms: pre-seeded dest must FAIL; non-empty caller dest must FAIL; the snapshot
///      seam answers new-path / changed-mtime / changed-size / no-change correctly.
///   2. PACK PROVENANCE — "tmp exists and is non-empty" proved nothing about WHO made it: a stale
///      scratch from a previous run could ship as this run's archive. A stuck (undeletable) stale
///      scratch now REFUSES loud before running; a fresh-run mtime baseline gates the move.
///   3. FORMAT REFUSAL — an unknown format token used to coerce silently to -sse; TryFormatFlag
///      now returns null for refusal (legal tokens keep their flags; the sse family defaults).
///
/// The real-BSArch behaviors (actual extraction, list output shapes) stay covered by bsa-probe,
/// which self-skips without BSArch — THIS guard needs none.
/// </summary>
internal static class BsaContractProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" bsa contract guard — unpack/pack provenance + format refusal");
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
            var fakeArchive = Path.Combine(work, "fake.bsa");
            File.WriteAllBytes(fakeArchive, new byte[] { 0x42, 0x53, 0x41, 0x00 });

            // ---- 1a) managed-flow shape: dest pre-seeded with the ownership marker ----
            Console.WriteLine("--- 1: unpack provenance ---");
            var seeded = Path.Combine(work, "seeded");
            Directory.CreateDirectory(seeded);
            File.WriteAllText(Path.Combine(seeded, "meta.ini"), "[houseCARL]\ngenerated=true\n");
            var r1 = BsaArchive.Unpack(stub, fakeArchive, seeded, timeoutMs: 30_000);
            Check(r1.Ran, "stub ran (the failure is BSArch's, not a run error)");
            Check(!r1.Success, "pre-seeded dest + failing BSArch = FAILURE (the marker alone no longer reads as success)");

            // ---- 1b) caller-dest shape: any non-empty folder ----
            var lived = Path.Combine(work, "lived-in");
            Directory.CreateDirectory(lived);
            File.WriteAllText(Path.Combine(lived, "existing.txt"), "user content");
            var r2 = BsaArchive.Unpack(stub, fakeArchive, lived, timeoutMs: 30_000);
            Check(r2.Ran && !r2.Success, "non-empty caller dest + failing BSArch = FAILURE");

            // ---- 1c) the snapshot seam: new / changed / unchanged ----
            var seam = Path.Combine(work, "seam");
            Directory.CreateDirectory(seam);
            File.WriteAllText(Path.Combine(seam, "a.txt"), "one");
            var snap = BsaArchive.SnapshotEntries(seam);
            Check(!BsaArchive.AnyNewOrChangedEntries(seam, snap), "no change → no entries this run");
            File.WriteAllText(Path.Combine(seam, "b.txt"), "two");
            Check(BsaArchive.AnyNewOrChangedEntries(seam, snap), "a NEW path counts (timestamp-independent)");
            File.Delete(Path.Combine(seam, "b.txt"));
            File.WriteAllText(Path.Combine(seam, "a.txt"), "ONE+");
            Check(BsaArchive.AnyNewOrChangedEntries(seam, snap), "a CHANGED existing file counts (size/mtime)");

            // ---- 2) pack: stuck stale scratch refuses; target untouched ----
            Console.WriteLine();
            Console.WriteLine("--- 2: pack provenance ---");
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

            // ---- 3) format refusal ----
            Console.WriteLine();
            Console.WriteLine("--- 3: format tokens ---");
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

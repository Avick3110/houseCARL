using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// atomic-commit guard — locks in AtomicFile.Commit, the crash-atomic primitive every houseCARL FINAL-SWAP write (the
/// commit of a staged temp over the target) now funnels through (the .esp commit WriteEngine.CommitStagedPatch, the BSA
/// repack BsaArchive.Pack, the config save UserConfig.Update), replacing the product-wide File.Move(overwrite:true) the
/// in-place-write-lane review named as not crash-atomic (MoveFileEx can unlink the destination before the rename
/// commits, and resets its metadata). (Fresh-CREATE writes — DecompileTools' FileMode.CreateNew, the meta.ini
/// File.WriteAllText — do NOT funnel through it, by design: there is no original to lose.)
///
/// A single process cannot power-cut mid-write, so this proves what IS observable, honestly (Q3):
///   A  fresh file (target absent) — Commit lands the bytes via the rename branch (and must not throw — a clean FAIL,
///      not an uncaught crash, if a File.Replace-only regression dropped the fallback).
///   B  overwrite (target exists) — Commit replaces content BYTE-EXACT and the staged temp is consumed; AND, where the
///      host's file-system tunneling does not mask it, the destination's CREATION TIME is PRESERVED → File.Replace, not
///      File.Move(overwrite) which resets it. A self-calibrating CONTROL detects a tunneling host and SELF-SKIPS that
///      one sub-check with a loud note rather than false-pass (the distinction is unprovable in-process there).
///   C  PRE-swap failure is loud + non-destructive (Q3) — a Commit with a MISSING staged source THROWS (never a silent
///      no-op) and the prior target survives byte-for-byte.
///   C2 MID-swap failure is loud + non-destructive (Q3) — the operationally-relevant case the staging exists to defend:
///      a valid staged source but the target HELD by an external holder (MO2/xEdit/the running game) → Commit THROWS and
///      the prior target survives byte-for-byte.
///
/// The crash-atomicity itself (no missing-file window on a power cut) is NOT demonstrable in-process and is NOT claimed
/// here — only that the code takes File.Replace's atomic-replace path, not File.Move's. Self-contained: temp files
/// only, no game data, no BSArch. Run: dotnet run --project src/housecarl-generator atomic-commit-guard
/// </summary>
internal static class AtomicCommitProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" atomic-commit guard — crash-atomic File.Replace swap primitive");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var oldCreate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var root = Path.Combine(Path.GetTempPath(), "hc-atomic-commit-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ---- A: fresh file (target absent) — Commit renames the staged file into place ----
            Console.WriteLine("--- A: fresh file (target absent) ---");
            {
                var staged = Path.Combine(root, "a.tmp");
                var final = Path.Combine(root, "a.dat");
                var bytes = new byte[] { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(staged, bytes);
                try
                {
                    AtomicFile.Commit(staged, final);
                    Check(File.Exists(final) && File.ReadAllBytes(final).SequenceEqual(bytes),
                          "fresh target written byte-exact (rename branch)");
                    Check(!File.Exists(staged), "staged temp consumed (fresh case)");
                }
                catch (Exception ex)
                {
                    // A File.Replace-ONLY regression (rename fallback dropped) would throw on the absent target — report
                    // a clean FAIL so arms B/C/C2 still run, rather than dying with an uncaught stack trace.
                    Check(false, $"fresh-file Commit must not throw (rename fallback missing?) — {ex.GetType().Name}: {ex.Message}");
                }
            }

            // ---- CONTROL: is file-system tunneling masking the creation-time signal on THIS host? A File.Move(overwrite)
            //      with a distinctive OLD destination creation time: if the result KEEPS it, tunneling is active and the
            //      creation-time arm cannot tell File.Replace from File.Move here. ----
            bool tunnelingMasks;
            {
                var staged = Path.Combine(root, "ctl.tmp");
                var final = Path.Combine(root, "ctl.dat");
                File.WriteAllBytes(final, new byte[] { 0 });
                File.SetCreationTimeUtc(final, oldCreate);
                File.WriteAllBytes(staged, new byte[] { 1 });
                File.Move(staged, final, overwrite: true);
                tunnelingMasks = File.GetCreationTimeUtc(final) == oldCreate;
                Console.WriteLine($"--- control: file-system tunneling {(tunnelingMasks ? "ACTIVE — creation-time arm self-skips (Q3)" : "off — creation-time arm has teeth")} ---");
            }

            // ---- B: overwrite (target exists) — byte-exact replace, staged consumed, creation time preserved (= File.Replace) ----
            Console.WriteLine("--- B: overwrite (target exists) ---");
            {
                var staged = Path.Combine(root, "b.tmp");
                var final = Path.Combine(root, "b.dat");
                File.WriteAllBytes(final, new byte[] { 9, 9, 9 });
                File.SetCreationTimeUtc(final, oldCreate);
                var newBytes = new byte[] { 7, 7, 7, 7 };
                File.WriteAllBytes(staged, newBytes);
                AtomicFile.Commit(staged, final);
                Check(File.ReadAllBytes(final).SequenceEqual(newBytes), "overwrite content is byte-exact the new staged bytes");
                Check(!File.Exists(staged), "staged temp consumed (overwrite case)");
                if (tunnelingMasks)
                    Console.WriteLine("  SKIP  destination creation-time preserved — UNPROVABLE on a tunneling host (Q3, not a pass)");
                else
                    Check(File.GetCreationTimeUtc(final) == oldCreate,
                          "destination creation time preserved — File.Replace, not File.Move(overwrite)  [RED arm]");
            }

            // ---- C: PRE-swap failure is loud + non-destructive (Q3) — missing staged source throws; prior intact ----
            Console.WriteLine("--- C: pre-swap failure is loud + non-destructive (Q3) ---");
            {
                var staged = Path.Combine(root, "does-not-exist.tmp");
                var final = Path.Combine(root, "c.dat");
                var prior = new byte[] { 4, 2 };
                File.WriteAllBytes(final, prior);
                bool threw = false;
                try { AtomicFile.Commit(staged, final); } catch { threw = true; }
                Check(threw, "a missing staged source THROWS (never a silent no-op)");
                Check(File.Exists(final) && File.ReadAllBytes(final).SequenceEqual(prior),
                      "prior target survives the failed pre-swap commit byte-for-byte");
            }

            // ---- C2: MID-swap failure is loud + non-destructive (Q3) — the threat staging exists to defend: a VALID
            //      staged source, but the target is HELD by an external holder (MO2/xEdit/the running game). File.Replace
            //      opens the destination first → can't (FileShare.None) → throws (IOException, NOT FileNotFoundException,
            //      so Commit does NOT fall through to a rename) → Commit propagates loud, prior target byte-intact. ----
            Console.WriteLine("--- C2: mid-swap failure on an externally-locked target (Q3) ---");
            {
                var staged = Path.Combine(root, "c2.tmp");
                var final = Path.Combine(root, "c2.dat");
                var prior = new byte[] { 5, 5, 5, 5, 5 };
                File.WriteAllBytes(final, prior);
                File.WriteAllBytes(staged, new byte[] { 8, 8 });
                bool threw = false;
                using (new FileStream(final, FileMode.Open, FileAccess.Read, FileShare.None))   // an external holder
                {
                    try { AtomicFile.Commit(staged, final); } catch { threw = true; }
                }
                Check(threw, "a mid-swap failure on a locked target THROWS (never a silent no-op)");
                Check(File.Exists(final) && File.ReadAllBytes(final).SequenceEqual(prior),
                      "prior target survives the failed mid-swap byte-for-byte");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* a lingering lock would itself be telling */ }
        }

        Console.WriteLine();
        if (fail == 0) Console.WriteLine("================ ALL CHECKS PASSED ================");
        else Console.WriteLine($"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}

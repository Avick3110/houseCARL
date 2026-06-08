using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Exploratory probe for the active-patch write self-lock (Heisen bug report 2026-06-08): houseCARL fails to write
/// into a patch that is ACTIVE in the resolved load order, because <c>OverlaySession.AllMasters()</c> opens a
/// memory-mapped overlay on EVERY active plugin — INCLUDING the write target — and Mutagen then serializes directly
/// onto that same path while the map is still alive. Windows refuses the overwrite (IOException "used by another
/// process"), so the all-or-nothing write writes nothing.
///
/// This maps the Windows file-sharing semantics precisely, to decide the fix. The honest open question from the
/// triage: the report recommends "serialize to a temp + File.Replace" as a COMPLETE fix on its own — but on Windows
/// the final swap must still rename/delete the original, which the same non-shareable map may ALSO block. So we test:
///
///   A  direct overwrite while an overlay on the target is HELD          (= the current bug)            → expect FAIL
///   B  temp write + File.Replace  while the overlay is HELD             (report's "#1 alone")          → ?
///   B2 temp write + File.Move(overwrite) while the overlay is HELD      (the Move variant)             → ?
///   C  Dispose the overlay, THEN direct overwrite                       (release-then-write)           → expect OK
///   D  temp write (overlay HELD) → Dispose overlay → File.Replace       (temp + release-then-swap)     → expect OK
///   E  CreateFromBinary(target) [the extend read] → direct overwrite    (does the extend read lock?)   → expect OK
///   F  overlay HELD but EXCLUDED from WithLoadOrder, direct overwrite   (is the HANDLE the lock, not   → expect FAIL
///                                                                        merely its presence in WLO?)
///
/// F is the load-bearing distinction for the fix: if a held-but-excluded handle still blocks the write, the fix must
/// NOT OPEN an overlay on the target at all (skip its index in AllMasters), not merely drop it from the returned set.
///
/// Self-contained — synthesizes its own .esp in TEMP; no game data. Run: dotnet run --project src/housecarl-generator writelock-probe
/// </summary>
public static class WriteLockProbe
{
    public static int RunProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL writelock-probe — active-patch write self-lock semantics");
        Console.WriteLine("================================================================");

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-writelock-probe");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var modKey = new ModKey("HcWriteLockProbe", ModType.Plugin);
        string target = Path.Combine(tmpDir, modKey.FileName.String);

        // Establish an existing patch on disk (no overlay held afterward — a clean baseline file to overwrite).
        Serialize(BuildPatch(modKey, "Init"), Array.Empty<ISkyrimModGetter>(), target);
        Console.WriteLine($"baseline patch on disk: {target} ({new FileInfo(target).Length} bytes)");
        Console.WriteLine();

        // ---- A: direct overwrite while an overlay on the target is HELD (the real AllMasters() situation) ----
        Console.WriteLine("A  direct overwrite, overlay on target HELD (= the bug):");
        {
            var ov = OpenAndFault(target);
            bool ok = Try(() => Serialize(BuildPatch(modKey, "A"), new[] { ov }, target), out var err);
            Dispose(ov);
            Report(ok, err, expectFail: true);
        }

        // ---- B: temp write + File.Replace while the overlay is HELD (report's "#1 alone") ----
        Console.WriteLine("B  temp write + File.Replace, overlay on target HELD (report's #1 alone):");
        {
            var ov = OpenAndFault(target);
            string tmp = SwapPath(tmpDir, modKey, "B");   // same .esp filename in a sub-dir (keeps ModKey==filename)
            bool ok = Try(() =>
            {
                Serialize(BuildPatch(modKey, "B"), new[] { ov }, tmp);
                File.Replace(tmp, target, null);
            }, out var err);
            Dispose(ov);
            CleanTmp(tmp);
            Report(ok, err, expectFail: false);   // unknown — measuring
        }

        // ---- B2: temp write + File.Move(overwrite:true) while the overlay is HELD ----
        Console.WriteLine("B2 temp write + File.Move(overwrite), overlay on target HELD:");
        {
            var ov = OpenAndFault(target);
            string tmp = SwapPath(tmpDir, modKey, "B2");
            bool ok = Try(() =>
            {
                Serialize(BuildPatch(modKey, "B2"), new[] { ov }, tmp);
                File.Move(tmp, target, overwrite: true);
            }, out var err);
            Dispose(ov);
            CleanTmp(tmp);
            Report(ok, err, expectFail: false);   // unknown — measuring
        }

        // ---- C: Dispose the overlay, THEN direct overwrite (release-then-write = the "don't hold the map" fix) ----
        Console.WriteLine("C  Dispose overlay, THEN direct overwrite (release-then-write):");
        {
            var ov = OpenAndFault(target);
            Dispose(ov);
            bool ok = Try(() => Serialize(BuildPatch(modKey, "C"), Array.Empty<ISkyrimModGetter>(), target), out var err);
            Report(ok, err, expectFail: false);
        }

        // ---- D: temp write (overlay HELD) → Dispose overlay → File.Replace (temp + release-then-swap, crash-safe) ----
        Console.WriteLine("D  temp write (overlay HELD) → Dispose overlay → File.Replace:");
        {
            var ov = OpenAndFault(target);
            string tmp = SwapPath(tmpDir, modKey, "D");
            bool ok = Try(() =>
            {
                Serialize(BuildPatch(modKey, "D"), new[] { ov }, tmp);
                Dispose(ov);
                File.Replace(tmp, target, null);
            }, out var err);
            Dispose(ov);   // idempotent if already disposed
            CleanTmp(tmp);
            Report(ok, err, expectFail: false);
        }

        // ---- E: CreateFromBinary(target) [the extend read], then direct overwrite (does the extend read lock?) ----
        Console.WriteLine("E  CreateFromBinary(target) [extend read] → direct overwrite (no overlay held):");
        {
            var loaded = SkyrimMod.CreateFromBinary(target, SkyrimRelease.SkyrimSE);   // eager full parse (the extend path)
            int n = loaded.EnumerateMajorRecords().Count();
            bool ok = Try(() => Serialize(BuildPatch(modKey, "E"), Array.Empty<ISkyrimModGetter>(), target), out var err);
            Report(ok, err, expectFail: false, note: $"(extend read saw {n} record(s))");
        }

        // ---- F: overlay HELD but EXCLUDED from WithLoadOrder, direct overwrite (is the open HANDLE the lock?) ----
        Console.WriteLine("F  overlay on target HELD but EXCLUDED from the write's master set, direct overwrite:");
        {
            var ov = OpenAndFault(target);
            bool ok = Try(() => Serialize(BuildPatch(modKey, "F"), Array.Empty<ISkyrimModGetter>(), target), out var err);
            Dispose(ov);
            Report(ok, err, expectFail: true,
                   note: "(if FAIL: the OPEN HANDLE locks regardless of WLO → fix must not OPEN the target overlay)");
        }

        Console.WriteLine();
        Console.WriteLine("(see A vs F for the lock mechanism; B/B2 vs D for whether temp-swap needs the map released first)");
        try { Directory.Delete(tmpDir, recursive: true); } catch { /* a lingering lock would itself be telling */ }
        return 0;
    }

    /// <summary>
    /// SELF-CONTAINED CI REGRESSION GUARD for the active-patch write self-lock (Heisen bug 2026-06-08), in the pattern of
    /// pkcu-regression / depth-leak-guard. Drives a REAL product write path (<see cref="WritePatchBuilder.RemoveRecords"/>)
    /// writing INTO a patch that is ACTIVE in the resolver's load order — the exact scenario that locks — and asserts the
    /// write SUCCEEDS. RemoveRecords is used because it is the one product write method that needs NO CorpusRulebook, so the
    /// guard stays corpus-free (CI never generates corpus.json); Apply / CreateRecords share the SAME serialize seam
    /// (AllMastersExcept), so proving the seam here covers all three.
    ///
    /// Two assertions, both required (a GREEN must mean "the fix works", never "the lock just doesn't happen here"):
    ///   CONTROL — hold an overlay on a throwaway COPY of the patch and attempt the OLD full-master-set serialize over it;
    ///             assert it FAILS with the lock. Proves the environment still reproduces the bug (Mutagen still maps
    ///             without FILE_SHARE_DELETE). If this stops failing, the guard says so — the fix may be moot / Mutagen changed.
    ///   FIX     — RemoveRecords drops one record from the ACTIVE patch (the serialize now routes through AllMastersExcept,
    ///             so the target is never mapped); assert Success + the patch is rewritten carrying the remaining record.
    ///
    /// RED before the fix (the serialize throws the IOException, Success=false), GREEN after. Self-contained: synthesizes
    /// its own masterless .esp in TEMP; no game data. Run: dotnet run --project src/housecarl-generator writelock-guard
    /// </summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — active-patch write self-lock (Heisen 2026-06-08)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-writelock-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var modKey = new ModKey("HcWriteLockGuard", ModType.Plugin);
        string target = Path.Combine(tmpDir, modKey.FileName.String);

        // --- Setup: write a patch carrying TWO records straight through Mutagen (no rulebook/corpus — keeps the guard
        //     self-contained for CI). Masterless, which is irrelevant to the lock (the lock is purely about the target path). ---
        FormKey removeFk;
        {
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var kwA = mod.Keywords.AddNew(); kwA.EditorID = "HcWriteLockGuard_A";
            var kwB = mod.Keywords.AddNew(); kwB.EditorID = "HcWriteLockGuard_B";
            removeFk = kwB.FormKey;
            mod.BeginWrite.ToPath(target).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        Console.WriteLine($"-- setup: wrote {modKey.FileName} with {CountRecords(target)} record(s) --");

        // --- CONTROL: prove the lock reproduces here — overlay a COPY of the patch, attempt the OLD direct serialize over
        //     the SAME path with that overlay in the master set, assert it FAILS (so a GREEN below is meaningful). ---
        bool controlLocked; string controlErr;
        {
            var ctlDir = Path.Combine(tmpDir, "control");
            Directory.CreateDirectory(ctlDir);
            string ctlTarget = Path.Combine(ctlDir, modKey.FileName.String);   // same filename (== ModKey) in a sub-dir
            File.Copy(target, ctlTarget);
            var ov = SkyrimMod.CreateFromBinaryOverlay(ctlTarget, SkyrimRelease.SkyrimSE);
            _ = ov.EnumerateMajorRecords().FirstOrDefault();                    // force the mmap to fault in
            var ctlPatch = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            ctlPatch.Keywords.AddNew().EditorID = "HcWriteLockGuard_Ctrl";
            controlLocked = !Try(() => ctlPatch.BeginWrite.ToPath(ctlTarget)
                                               .WithLoadOrder(new ISkyrimModGetter[] { ov }).Write(), out controlErr);
            Dispose(ov);
        }
        Console.WriteLine($"   CONTROL (bug reproduces here)   : {(controlLocked ? "PASS — direct serialize onto a mapped target FAILED as expected" : "FAIL — NO lock; can't prove the fix on this platform")}  [{controlErr}]");

        // --- FIX: the product path — RemoveRecords writes INTO a patch that is ACTIVE in the resolver's order (target is in
        //     the order), routing the serialize through AllMastersExcept so the target is never mapped. Assert it SUCCEEDS. ---
        bool fixWrote; int remaining; string fixErr;
        using (var r1 = LoadOrderResolver.Build(new[] { target }))            // the patch is ACTIVE in the order now
        {
            var o1 = WritePatchBuilder.RemoveRecords(r1, new[] { removeFk }, target);
            fixWrote = o1.Success; remaining = o1.RemainingRecords; fixErr = o1.Error ?? "ok";
        }
        int afterFix = CountRecords(target);
        Console.WriteLine($"   FIX (write into active patch)   : {(fixWrote ? "PASS — RemoveRecords wrote into the active patch" : "FAIL — write into the active patch was refused")}  [{fixErr}]");
        Console.WriteLine($"   patch rewritten on disk (==1)   : {(afterFix == 1 ? "PASS" : $"FAIL (count={afterFix})")}");
        Console.WriteLine();

        bool pass = controlLocked && fixWrote && remaining == 1 && afterFix == 1;
        Console.WriteLine($"=== writelock-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { /* a lingering lock would itself be telling */ }
        return pass ? 0 : 1;
    }

    static int CountRecords(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords().Count(); }
        catch { return -1; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // Build a fresh in-memory patch carrying one MGEF (self-contained, references nothing → masterless, like a created record).
    static SkyrimMod BuildPatch(ModKey mk, string tag)
    {
        var mod = new SkyrimMod(mk, SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.EditorID = $"HcWriteLock_{tag}";
        return mod;
    }

    // The exact serialize incantation WriteEngine.WritePatch uses (BeginWrite → ToPath → WithLoadOrder → Write).
    static void Serialize(SkyrimMod mod, ISkyrimModGetter[] masters, string outPath)
        => mod.BeginWrite.ToPath(outPath).WithLoadOrder(masters).Write();

    // A temp swap target that KEEPS the .esp filename (== the ModKey) so a temp serialize doesn't trip Mutagen's
    // filename↔ModKey coupling — only the directory differs, so it's a clean stand-in for the real swap target.
    static string SwapPath(string tmpDir, ModKey mk, string tag)
    {
        var dir = Path.Combine(tmpDir, "swap-" + tag);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, mk.FileName.String);
    }

    static ISkyrimModGetter OpenAndFault(string path)
    {
        var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        _ = ov.EnumerateMajorRecords().FirstOrDefault();   // force the mmap to fault in (a header-only open may not map)
        return ov;
    }

    static void Dispose(ISkyrimModGetter ov) => (ov as IDisposable)?.Dispose();
    static void CleanTmp(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    static bool Try(Action op, out string err)
    {
        try { op(); err = "ok"; return true; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {Flatten(ex.Message)}"; return false; }
    }

    static string Flatten(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();

    static void Report(bool ok, string err, bool expectFail, string? note = null)
    {
        string verdict = ok
            ? (expectFail ? "WROTE  (UNEXPECTED — expected a lock)" : "WROTE")
            : (expectFail ? "FAILED (as expected)" : "FAILED (UNEXPECTED)");
        Console.WriteLine($"     → {verdict}   [{err}]{(note is null ? "" : "  " + note)}");
        Console.WriteLine();
    }
}

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
    /// SELF-CONTAINED CI REGRESSION GUARD for the active-patch write self-lock (Heisen bug 2026-06-08 + PR #24 review), in
    /// the pattern of pkcu-regression / depth-leak-guard. Drives REAL product write paths INTO a patch that is ACTIVE in
    /// the resolver's load order — the exact scenario that locks — and asserts the writes SUCCEED. Self-contained (synthesizes
    /// its own .esp in TEMP, and generates the validator corpus BY CONSTRUCTION in-process for the Apply arm; no game data,
    /// no checked-in corpus.json). Run: dotnet run --project src/housecarl-generator writelock-guard
    ///
    /// Arms (ALL required — a GREEN must mean "the fix works", never "the lock just doesn't happen here"):
    ///   CONTROL — hold an overlay on a throwaway COPY and attempt the OLD full-master-set serialize over it; assert it
    ///             FAILS with the lock. Proves the environment still reproduces the bug (Mutagen still maps without
    ///             FILE_SHARE_DELETE). If this stops failing, the guard says so — the fix may be moot / Mutagen changed.
    ///   REMOVE  — <see cref="WritePatchBuilder.RemoveRecords"/> drops a record from the ACTIVE patch; covers the MASTER-SET
    ///             overlay source (closed by AllMastersExcept). Corpus-free.
    ///   APPLY   — <see cref="WritePatchBuilder.Apply"/> RE-EDITS a record the active patch itself overrides (the winner IS
    ///             the target), so Apply's Phase-1 winner fetch opens a SECOND overlay on the target — the source
    ///             AllMastersExcept can't reach (PR #24 review). Covers the fix's ReleaseOverlay half; assert Success AND
    ///             the edited value lands. A RemoveRecords-only guard CANNOT catch this (Remove reads eagerly, no winner
    ///             fetch) — which is exactly why this arm exists.
    ///
    /// RED before the fix (the serialize throws the IOException, Success=false), GREEN after — verified for BOTH the master-set
    /// (revert AllMastersExcept → REMOVE+APPLY red) and the winner-fetch (revert ReleaseOverlay → APPLY red) halves.
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

        // --- APPLY ARM (the PR #24 review finding): drive the REAL WritePatchBuilder.Apply re-editing a record the ACTIVE
        //     patch ITSELF overrides — there the resolved winner IS the target, so Apply's Phase-1 winner fetch opens an
        //     overlay on the target (a source AllMastersExcept can't reach; ReleaseOverlay must close it before serialize).
        //     Apply pre-flights the edit through the CorpusRulebook, so we generate the corpus BY CONSTRUCTION in-process
        //     (no checked-in corpus.json, no game data — the guard stays self-contained, just slower). ---
        bool applyWrote = false; string applyErr = "ok"; int dmgBack = -1;
        {
            var rulebook = CorpusRulebook.Load(GenerateCorpus(tmpDir));
            var mKey = new ModKey("HcWriteLockGuardMaster", ModType.Master);
            var qKey = new ModKey("HcWriteLockGuardPatch", ModType.Plugin);
            string mPath = Path.Combine(tmpDir, mKey.FileName.String);
            string qPath = Path.Combine(tmpDir, qKey.FileName.String);

            // a master carrying a weapon, then an ACTIVE patch that OVERRIDES it (so re-editing the weapon hits Q's own override)
            FormKey wfk;
            {
                var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
                var w = m.Weapons.AddNew(); w.EditorID = "HcGuardWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
                wfk = w.FormKey;
                m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            using (var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE))
            {
                var q = new SkyrimMod(qKey, SkyrimRelease.SkyrimSE);
                var qW = q.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == wfk));
                qW.BasicStats!.Damage = 20;
                q.BeginWrite.ToPath(qPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
            }

            using var r2 = LoadOrderResolver.Build(new[] { mPath, qPath });   // Q ACTIVE + highest priority → the weapon's winner is Q (the target)
            var edit = new WritePatchBuilder.PatchEdit { Target = wfk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "777" };
            var oa = WritePatchBuilder.Apply(r2, rulebook, new[] { edit }, qPath, extend: true);
            applyWrote = oa.Success; applyErr = oa.Error ?? "ok";
            dmgBack = ReadWeaponDamage(qPath, wfk);
        }
        Console.WriteLine($"   APPLY re-edit own override       : {(applyWrote ? "PASS — Apply wrote into the active patch" : "FAIL — Apply was refused")}  [{applyErr}]");
        Console.WriteLine($"   edited value landed (damage==777): {(dmgBack == 777 ? "PASS" : $"FAIL (damage={dmgBack})")}");
        Console.WriteLine();

        bool pass = controlLocked && fixWrote && remaining == 1 && afterFix == 1 && applyWrote && dmgBack == 777;
        Console.WriteLine($"=== writelock-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { /* a lingering lock would itself be telling */ }
        return pass ? 0 : 1;
    }

    // Generate the validator corpus BY CONSTRUCTION (reflect the linked Mutagen assembly) into a temp dir; return the
    // corpus.json path — so the Apply arm can pre-flight edits without a checked-in corpus.json (keeps the guard self-contained).
    static string GenerateCorpus(string tmpDir)
    {
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    static int ReadWeaponDamage(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.BasicStats?.Damage ?? -1; }
        catch { return -1; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>
    /// REAL-DATA proof (PR #24 review residual): the writelock-guard's Apply arm uses a FLAT record (Weapon). This proves
    /// the same re-edit-own-override case for a NESTED record (a PlacedObject — lives in a Cell, so its override goes
    /// through Apply's link-cache CONTEXT path, where the parent chain is reconstructed). That is the one place the new
    /// "ReleaseOverlay disposes the target overlay BEFORE serialize" invariant is least obviously safe: the context path
    /// builds its link cache OVER the target overlay, then the fix disposes it. If the nested override weren't fully
    /// deep-copied into the patch mod first, releasing the overlay would break the write.
    ///
    /// Needs a real master (Skyrim.esm) for a genuine nested record + parent chain — NOT a CI guard (no game data on the
    /// runner), the same posture as apply-proof / the nested proofs. Step 1 overrides a real PlacedObject into a fresh Q
    /// (winner = Skyrim.esm); step 2 re-edits it with Q ACTIVE (winner = Q = target → the nested winner-fetch). GREEN =
    /// the re-edit succeeds and the new Scale reads back. Revert ReleaseOverlay → step 2 goes RED (teeth on the nested path).
    /// Run: dotnet run --project src/housecarl-generator writelock-nested-proof ["&lt;Data dir with Skyrim.esm&gt;"]
    /// </summary>
    public static int RunNestedProof(string[] args)
    {
        Console.WriteLine("=== writelock-nested-proof — Apply re-edit of a NESTED own-override into an active patch (real data) ===");
        string dataDir = args.Length > 0 ? args[0] : @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data";
        string skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (!File.Exists(skyrim)) { Console.Error.WriteLine($"need Skyrim.esm; not found at {skyrim} (pass the Data dir as arg 1)"); return 1; }

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-writelock-nested");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var rulebook = CorpusRulebook.Load(GenerateCorpus(tmpDir));

        // a REAL nested record: the first PlacedObject (REFR) in Skyrim.esm — lives in a Cell, so overriding it takes
        // Apply's source-cache CONTEXT path (RecordNeedsSourceCache == true), the path the review flagged.
        FormKey refrFk;
        using (var r0 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            refrFk = r0.WinnerRecordsOfType(new[] { typeof(IPlacedObjectGetter) }).Select(x => x.fk).FirstOrDefault();
            if (refrFk.IsNull) { Console.Error.WriteLine("no PlacedObject found in Skyrim.esm"); return 1; }
        }
        Console.WriteLine($"-- real nested record: PlacedObject {refrFk} --");
        string qPath = Path.Combine(tmpDir, "HcNestedProof.esp");

        // STEP 1 — override it into a FRESH patch Q (winner == Skyrim.esm; the normal nested-override path, Q not yet active)
        using (var r1 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            var o = WritePatchBuilder.Apply(r1, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "1.5" } },
                qPath, extend: false);
            Console.WriteLine($"   step 1  override into fresh Q (winner=Skyrim.esm) : {(o.Success ? "OK" : "FAIL — " + o.Error)}");
            if (!o.Success) return 1;
        }

        // STEP 2 — THE TEST: re-edit the SAME nested record now that Q is ACTIVE (winner == Q == target → nested winner-fetch)
        bool ok; string err;
        using (var r2 = LoadOrderResolver.Build(new[] { skyrim, qPath }))
        {
            var winner = r2.ResolveWinner(refrFk);
            var o = WritePatchBuilder.Apply(r2, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "2.5" } },
                qPath, extend: true);
            ok = o.Success; err = o.Error ?? "ok";
            Console.WriteLine($"   step 2  re-edit own NESTED override (winner={winner?.WinnerPlugin}) : {(ok ? "OK" : "FAIL — " + err)}");
        }
        float? scaleBack = ReadPlacedScale(qPath, refrFk);
        bool landed = scaleBack.HasValue && Math.Abs(scaleBack.Value - 2.5f) < 0.001f;
        Console.WriteLine($"   nested edit landed (Scale==2.5) : {(landed ? "PASS" : $"FAIL (scale={scaleBack?.ToString() ?? "null"})")}");

        bool pass = ok && landed;
        Console.WriteLine($"=== writelock-nested-proof: {(pass ? "PASS — nested re-edit survives ReleaseOverlay-before-serialize" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    static float? ReadPlacedScale(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == fk)?.Scale; }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>
    /// EXPLORATORY follow-up (PR #24 review, 2026-06-08): the AllMastersExcept fix closes the master-set overlay on the
    /// target, but <see cref="WritePatchBuilder.Apply"/> has a SECOND overlay source — its Phase-1 winner fetch
    /// (<see cref="LoadOrderResolver.GetRecord"/> → <c>session.Overlay(idx)</c> on the WINNER plugin). When you re-edit a
    /// record an ACTIVE patch already overrides, the winner IS the target patch, so GetRecord opens an overlay on the
    /// target that survives AllMastersExcept (exp F: it's the open HANDLE, not master-set membership, that locks) and the
    /// serialize still fails. This reproduces it on the REAL path (resolver.ResolveWinner + resolver.GetRecord +
    /// WriteEngine.WritePatch + the shipped AllMastersExcept), corpus-free.
    ///
    /// RED = the residual is real with the current fix; the second block previews the fix (release the winner-fetch
    /// overlay before serialize → success). Run: dotnet run --project src/housecarl-generator writelock-apply-probe
    /// </summary>
    public static int RunApplyResidualProbe(string[] args)
    {
        Console.WriteLine("=== writelock-apply-probe — Apply winner-fetch residual (PR #24 review) ===");
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-writelock-apply");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        var modKey = new ModKey("HcWriteLockApply", ModType.Plugin);
        string target = Path.Combine(tmpDir, modKey.FileName.String);

        // The active patch carries an override (a self-contained Weapon stands in for "a record the patch overrides").
        FormKey fk;
        {
            var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);
            var w = mod.Weapons.AddNew(); w.EditorID = "HcResidual_W";
            fk = w.FormKey;
            mod.BeginWrite.ToPath(target).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        using var resolver = LoadOrderResolver.Build(new[] { target });   // the target is ACTIVE in the order
        var winner = resolver.ResolveWinner(fk);
        Console.WriteLine($"-- winner of {fk} = {winner?.WinnerPlugin} (the active target itself — re-editing its own override) --");

        // RED — Apply's winner fetch opens an overlay on the target (Phase 1), still held when AllMastersExcept serializes (Phase 4).
        {
            using var session = resolver.OpenSession();
            _ = resolver.GetRecord(session, winner!.Value.WinnerPlugin, fk);              // opens the TARGET overlay (winner == target)
            var patchMod = SkyrimMod.CreateFromBinary(target, SkyrimRelease.SkyrimSE);
            bool ok = Try(() => WriteEngine.WritePatch(patchMod, session.AllMastersExcept(modKey.FileName.String), target), out var err);
            Console.WriteLine($"   CURRENT fix (AllMastersExcept), winner-fetch overlay HELD : {(ok ? "WROTE — no residual?!" : "FAILED — RESIDUAL CONFIRMED")}");
            Console.WriteLine($"      [{err}]");
        }

        // GREEN preview — the FULL Apply mechanism with the fix: winner-fetch → override+edit (Phase 3) → RELEASE the
        // winner-fetch overlay → serialize. Proves both that the lock clears AND that the deep-copied, edited override
        // survives releasing its source overlay (so "release before serialize" can't strip content from the patch).
        {
            SkyrimMod patchMod;
            using (var session = resolver.OpenSession())
            {
                var body = resolver.GetRecord(session, winner!.Value.WinnerPlugin, fk)!;   // Phase 1: winner fetch (opens TARGET overlay)
                patchMod = SkyrimMod.CreateFromBinary(target, SkyrimRelease.SkyrimSE);      // Phase 2: extend copy
                var ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, body, null);       // Phase 3: deep-copy override into the patch
                ov.EditorID = "HcResidual_W_EDITED";                                        //          ... and edit it
            }                                                                              // RELEASE the winner-fetch overlay before serialize
            bool ok = Try(() => WriteEngine.WritePatch(patchMod, Array.Empty<ISkyrimModGetter>(), target), out var err);
            string edid = ReadEditorId(target, fk);
            Console.WriteLine($"   FIX PREVIEW (release first; edit survives) : write={(ok ? "OK" : "FAILED " + err)}  edid-read-back=\"{edid}\" (expect HcResidual_W_EDITED)");
        }

        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return 0;
    }

    static string ReadEditorId(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk)?.EditorID ?? "(not found)"; }
        catch (Exception ex) { return "(read failed: " + ex.GetType().Name + ")"; }
        finally { (ov as IDisposable)?.Dispose(); }
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

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
public static class WriteLockHarness
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

    // Generate the validator corpus BY CONSTRUCTION (reflect the linked Mutagen assembly) into a temp dir; return the
    // corpus.json path — so the nested proof can pre-flight edits without a checked-in corpus.json.
    static string GenerateCorpus(string tmpDir)
    {
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    /// <summary>
    /// REAL-DATA proof (PR #24 review residual): ActivePatchWriteLockTests' Apply test uses a FLAT record (Weapon). This proves
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


    // Build a fresh in-memory patch carrying one MGEF (self-contained, references nothing → masterless, like a created record).
    static SkyrimMod BuildPatch(ModKey mk, string tag)
    {
        var mod = new SkyrimMod(mk, SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.EditorID = $"HcWriteLock_{tag}";
        return mod;
    }

    // The core serialize incantation WriteEngine.WritePatch uses (BeginWrite → ToPath → WithLoadOrder → Write; the
    // product path adds WithExtraIncludedMasters + NoNextFormIDProcessing, neither of which touches lock semantics).
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

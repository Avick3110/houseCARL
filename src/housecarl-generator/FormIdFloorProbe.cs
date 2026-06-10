using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Exploratory probe for the FormID-allocation-from-zero bug (HCBR-2026-06-09-04): a patch FIRST created by
/// <c>bulk_apply</c> (overrides only) persists <c>HEDR.NextObjectID = 0</c>, and a later <c>create_record into=</c>
/// rehydrates that counter and allocates new records from object ID 0x000000 — the NULL-reference bit pattern —
/// while reporting success.
///
/// This pins the Mutagen 0.53.1 semantics the fix is designed from, all self-contained (synthesized plugins in TEMP):
///
///   S1  fresh-mod state        — what does <c>new SkyrimMod</c> initialize <c>NextFormID</c> to, and what does
///                                <c>GetDefaultInitialNextFormID</c> report (null vs forceLower:false)?
///   S2  the bug's seed         — serialize an OVERRIDE-ONLY patch via the raw default-params incantation (what
///                                WritePatch did PRE-FIX); what NextObjectID lands on disk? (expect 0 — Iterate
///                                recompute over no originating records, ignoring the in-memory 0x800)
///   S3  the bug itself         — CreateFromBinary that patch, AddNew; what object ID is allocated? (expect 0x000000)
///   S4  fix mechanics: clamp   — set NextFormID = max(current, 0x800) BEFORE AddNew; allocation lands at 0x800?
///   S5  fix mechanics: persist — serialize with <c>.NoNextFormIDProcessing()</c>; does the on-disk counter become
///                                the in-memory value verbatim (override-only AND with-creations cases)?
///   S6  backstop semantics     — does <c>.WithForcedLowerFormIdRangeUsage(false)</c> make a serialize carrying an
///                                ORIGINATING sub-0x800 record fail loud, while a patch whose only sub-0x800 FormIDs
///                                are OVERRIDES of a master still writes fine? (decides whether it can be a Q3 guard)
///
/// Run: dotnet run --project src/housecarl-generator formid-floor-probe
/// </summary>
public static class FormIdFloorProbe
{
    public static int RunProbe(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" houseCARL formid-floor-probe — NextFormID semantics (HCBR-2026-06-09-04)");
        Console.WriteLine("================================================================");

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-formid-floor-probe");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // ---- S1: fresh-mod in-memory state ----
        Console.WriteLine("S1 fresh new SkyrimMod(SkyrimSE):");
        {
            var fresh = new SkyrimMod(new ModKey("HcFidProbe", ModType.Plugin), SkyrimRelease.SkyrimSE);
            Console.WriteLine($"     NextFormID                                = 0x{fresh.ModHeader.Stats.NextFormID:X6}");
            Console.WriteLine($"     GetDefaultInitialNextFormID(null)         = 0x{fresh.GetDefaultInitialNextFormID(null):X6}");
            Console.WriteLine($"     GetDefaultInitialNextFormID(forceLower:false) = 0x{fresh.GetDefaultInitialNextFormID(false):X6}");
            Console.WriteLine($"     header Version                            = {fresh.ModHeader.Stats.Version}");
        }
        Console.WriteLine();

        // ---- Synthesize a master to override (a weapon), like the real bulk_apply situation. ----
        var mKey = new ModKey("HcFidMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey weapFk;
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var w = m.Weapons.AddNew(); w.EditorID = "HcFidWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            weapFk = w.FormKey;
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        // ---- S2: the bug's seed — an OVERRIDE-ONLY patch through the raw default-params incantation (the chain
        //      WritePatch used PRE-FIX; raw so this probe pins MUTAGEN's semantics regardless of the product fix) ----
        var pKey = new ModKey("HcFidPatch", ModType.Plugin);
        string pPath = Path.Combine(tmpDir, pKey.FileName.String);
        Console.WriteLine("S2 override-only patch serialized with default write params:");
        {
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;
            var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
            var ov = p.Weapons.GetOrAddAsOverride(mGet.Weapons.First(x => x.FormKey == weapFk));
            ov.BasicStats!.Damage = 20;
            Console.WriteLine($"     in-memory NextFormID before serialize     = 0x{p.ModHeader.Stats.NextFormID:X6}");
            p.BeginWrite.ToPath(pPath).WithLoadOrder(new[] { mGet }).Write();
            Console.WriteLine($"     on-disk HEDR.NextObjectID after serialize = 0x{ReadDiskNextFormId(pPath):X6}   (expect 0x000000 — the seed)");
        }
        Console.WriteLine();

        // ---- S3: the bug — rehydrate + AddNew ----
        Console.WriteLine("S3 CreateFromBinary(the override-only patch) → AddNew:");
        string p3Path = Path.Combine(tmpDir, "s3", pKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p3Path)!);
        File.Copy(pPath, p3Path);
        {
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;
            var p = SkyrimMod.CreateFromBinary(p3Path, SkyrimRelease.SkyrimSE);
            Console.WriteLine($"     rehydrated NextFormID                     = 0x{p.ModHeader.Stats.NextFormID:X6}");
            var kw = p.Keywords.AddNew(); kw.EditorID = "HcFidKw_Bug";
            Console.WriteLine($"     AddNew allocated                          = {kw.FormKey}   (object ID 0x{kw.FormKey.ID:X6}; 0x000000 = THE BUG)");
            p.BeginWrite.ToPath(p3Path).WithLoadOrder(new[] { mGet }).Write();
            Console.WriteLine($"     on-disk HEDR.NextObjectID after serialize = 0x{ReadDiskNextFormId(p3Path):X6}");
        }
        Console.WriteLine();

        // ---- S4: fix mechanics — clamp the in-memory counter BEFORE AddNew ----
        Console.WriteLine("S4 clamp NextFormID to >= 0x800 before AddNew (the allocator-side fix):");
        string p4Path = Path.Combine(tmpDir, "s4", pKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p4Path)!);
        File.Copy(pPath, p4Path);
        {
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;
            var p = SkyrimMod.CreateFromBinary(p4Path, SkyrimRelease.SkyrimSE);
            if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
            var kw = p.Keywords.AddNew(); kw.EditorID = "HcFidKw_Clamped";
            Console.WriteLine($"     AddNew after clamp allocated              = {kw.FormKey}   (expect object ID 0x000800)");
            var kw2 = p.Keywords.AddNew(); kw2.EditorID = "HcFidKw_Clamped2";
            Console.WriteLine($"     second AddNew allocated                   = {kw2.FormKey}   (expect 0x000801)");
            p.BeginWrite.ToPath(p4Path).WithLoadOrder(new[] { mGet }).Write();
            Console.WriteLine($"     on-disk HEDR.NextObjectID after serialize = 0x{ReadDiskNextFormId(p4Path):X6}   (Iterate recompute: expect 0x000802)");
        }
        Console.WriteLine();

        // ---- S5: fix mechanics — persist the in-memory counter with NoNextFormIDProcessing ----
        Console.WriteLine("S5 serialize with .NoNextFormIDProcessing() (the persist-side fix):");
        {
            // 5a: override-only (no creations) — does the clamped counter persist as 0x800?
            string p5a = Path.Combine(tmpDir, "s5a", pKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p5a)!);
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;
            var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
            var ov = p.Weapons.GetOrAddAsOverride(mGet.Weapons.First(x => x.FormKey == weapFk));
            ov.BasicStats!.Damage = 30;
            if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
            p.BeginWrite.ToPath(p5a).WithLoadOrder(new[] { mGet }).NoNextFormIDProcessing().Write();
            Console.WriteLine($"     override-only, counter 0x{p.ModHeader.Stats.NextFormID:X6} in memory → on disk = 0x{ReadDiskNextFormId(p5a):X6}   (expect 0x000800)");

            // 5b: with a creation — does the incremented counter persist verbatim?
            string p5b = Path.Combine(tmpDir, "s5b", pKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p5b)!);
            var kw = p.Keywords.AddNew(); kw.EditorID = "HcFidKw_S5";
            p.BeginWrite.ToPath(p5b).WithLoadOrder(new[] { mGet }).NoNextFormIDProcessing().Write();
            Console.WriteLine($"     after one AddNew ({kw.FormKey}), counter 0x{p.ModHeader.Stats.NextFormID:X6} in memory → on disk = 0x{ReadDiskNextFormId(p5b):X6}   (expect 0x000801)");
        }
        Console.WriteLine();

        // ---- S6: backstop semantics — WithForcedLowerFormIdRangeUsage(false) ----
        Console.WriteLine("S6 .WithForcedLowerFormIdRangeUsage(false) as a write-time guard:");
        {
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;

            // 6a: a patch CARRYING an originating sub-0x800 record (the bug's product) — does the serialize throw?
            string p6a = Path.Combine(tmpDir, "s6a", pKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p6a)!);
            {
                var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
                p.ModHeader.Stats.NextFormID = 0;                   // simulate the rehydrated broken counter
                var kw = p.Keywords.AddNew(); kw.EditorID = "HcFidKw_Low";
                bool wrote = Try(() => p.BeginWrite.ToPath(p6a).WithLoadOrder(new[] { mGet })
                                        .WithForcedLowerFormIdRangeUsage(false).Write(), out var err);
                Console.WriteLine($"     originating record {kw.FormKey}: {(wrote ? "WROTE (no guard value)" : "THREW — loud backstop available")}");
                Console.WriteLine($"       [{err}]");
            }

            // 6b: a patch whose only sub-0x800 FormIDs are OVERRIDES of a master — must still write fine.
            string p6b = Path.Combine(tmpDir, "s6b", pKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p6b)!);
            {
                // master record with a LOW object id (constructed explicitly — masters legitimately own sub-0x800 IDs).
                // NOTE: even this SETUP write can throw LowerFormKeyRangeDisallowedException under DEFAULT params
                // (measured 2026-06-10) — itself evidence the lower-range machinery is too unpredictable for a guard.
                var mLowKey = new ModKey("HcFidMasterLow", ModType.Master);
                string mLowPath = Path.Combine(tmpDir, mLowKey.FileName.String);
                var lowFk = new FormKey(mLowKey, 0x000123);
                bool setupWrote = Try(() =>
                {
                    var ml = new SkyrimMod(mLowKey, SkyrimRelease.SkyrimSE);
                    var w = new Weapon(lowFk, SkyrimRelease.SkyrimSE) { EditorID = "HcFidLowWeap" };
                    ml.Weapons.Add(w);
                    ml.BeginWrite.ToPath(mLowPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }, out var setupErr);
                if (!setupWrote)
                {
                    Console.WriteLine($"     setup write of low-ID master ITSELF threw under DEFAULT params — 6b unmeasurable, and the");
                    Console.WriteLine($"     flag is disqualified as a guard either way: [{setupErr}]");
                }
                else
                {
                    using var mlOv = SkyrimMod.CreateFromBinaryOverlay(mLowPath, SkyrimRelease.SkyrimSE) as IDisposable;
                    var mlGet = (ISkyrimModGetter)mlOv!;
                    var p = new SkyrimMod(pKey, SkyrimRelease.SkyrimSE);
                    var ov = p.Weapons.GetOrAddAsOverride(mlGet.Weapons.First(x => x.FormKey == lowFk));
                    ov.EditorID = "HcFidLowWeap_Edited";
                    bool wrote = Try(() => p.BeginWrite.ToPath(p6b).WithLoadOrder(new[] { mlGet })
                                            .WithForcedLowerFormIdRangeUsage(false).Write(), out var err);
                    Console.WriteLine($"     override of master record {lowFk}: {(wrote ? "WROTE — overrides exempt (guard usable)" : "THREW — guard would refuse legit override patches (NOT usable)")}");
                    Console.WriteLine($"       [{err}]");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("(S2/S3 = the bug; S4/S5 = the fix mechanics; S6 = whether a loud write-time backstop is safe)");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return 0;
    }

    /// <summary>
    /// SELF-CONTAINED CI REGRESSION GUARD for the FormID allocation floor (HCBR-2026-06-09-04), in the pattern of
    /// writelock-guard / pkcu-regression. Drives the REAL product write paths through the report's exact workflow —
    /// a patch BORN from <see cref="WritePatchBuilder.Apply"/> (the bulk_apply path), then grown with
    /// <see cref="WritePatchBuilder.CreateRecords"/> <c>into=</c> — and asserts the 0x800+ contract the tool documents.
    /// Self-contained (synthesizes its own master in TEMP; generates the validator corpus by construction in-process).
    /// Run: dotnet run --project src/housecarl-generator formid-floor-guard
    ///
    /// Arms (ALL required — a GREEN must mean "the fix works", never "the scenario doesn't arise here"):
    ///   CONTROL — raw Mutagen default-params serialize of an override-only patch persists NextObjectID = 0 (the
    ///             Iterate recompute, formid-floor-probe S2). Proves the seed still exists upstream; if this stops
    ///             reproducing, Mutagen changed and the fix may be moot — the guard says so.
    ///   APPLY   — a patch BORN from Apply (override-only) must persist HEDR.NextObjectID &gt;= 0x800.  RED pre-fix (0).
    ///   CREATE  — CreateRecords into= that patch must allocate object ID 0x800 (not 0x000000 — THE BUG), and the
    ///             persisted counter must advance past it; a second create allocates 0x801.       RED pre-fix (0/1).
    ///   REMOVE  — RemoveRecords of the second created record must keep the persisted counter at its high-water
    ///             (0x802 — no floor regression, no freed-ID reuse).                              RED pre-fix (1→ regress).
    ///   FRESH   — CreateRecords into a FRESH patch still allocates 0x800 (the already-good path, unregressed).
    ///   LEGACY-CREATE / LEGACY-APPLY — the fix's "patches already on disk with a zeroed counter heal" half (PR #30
    ///             review): both reuse the CONTROL artifact, a GENUINE zero-counter legacy patch by construction.
    ///             CREATE into= it must allocate 0x800 — this pins GenericAddNew's floor specifically (every other
    ///             arm only ever rehydrates an already-healed counter); an override-only Apply extend of a second
    ///             copy must PERSIST 0x800 with no allocation happening at all — pinning WritePatch's floor
    ///             independently. Either chokepoint reverted alone goes RED here.
    /// </summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — FormID allocation floor (HCBR-2026-06-09-04)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-formid-floor-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: a master carrying a weapon (the record the patch will override), + the validator corpus. ---
        var mKey = new ModKey("HcFidGuardMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        FormKey weapFk;
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var w = m.Weapons.AddNew(); w.EditorID = "HcFidGuardWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            weapFk = w.FormKey;
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        Console.WriteLine($"-- setup: master {mKey.FileName} with weapon {weapFk}; corpus generated --");

        // --- CONTROL: the raw-Mutagen seed (default params, override-only → NextObjectID 0) still reproduces. The
        //     artifact doubles as the LEGACY arms' input below — a genuine zero-counter on-disk patch, by construction. ---
        bool controlSeed;
        string ctlPath = Path.Combine(tmpDir, "control", "HcFidGuardCtl.esp");
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ctlPath)!);
            using var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE) as IDisposable;
            var mGet = (ISkyrimModGetter)mOv!;
            var ctl = new SkyrimMod(new ModKey("HcFidGuardCtl", ModType.Plugin), SkyrimRelease.SkyrimSE);
            ctl.Weapons.GetOrAddAsOverride(mGet.Weapons.First(x => x.FormKey == weapFk)).BasicStats!.Damage = 99;
            ctl.BeginWrite.ToPath(ctlPath).WithLoadOrder(new[] { mGet }).Write();
            uint ctlNext = ReadDiskNextFormId(ctlPath);
            controlSeed = ctlNext == 0;
            Console.WriteLine($"   CONTROL (raw Mutagen seed)        : {(controlSeed ? "PASS — default-params override-only persists 0" : $"FAIL — persisted 0x{ctlNext:X6}; Mutagen changed upstream, the fix may be moot")}");
        }

        string pPath = Path.Combine(tmpDir, "HcFidGuardPatch.esp");

        // --- APPLY: a patch BORN from the real Apply path (= bulk_apply) must persist a floored counter. ---
        bool applyOk, applyFloor; uint applyNext;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var edit = new WritePatchBuilder.PatchEdit { Target = weapFk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "20" };
            var o = WritePatchBuilder.Apply(r, rulebook, new[] { edit }, pPath, extend: false);
            applyOk = o.Success;
            applyNext = applyOk ? ReadDiskNextFormId(pPath) : 0;
            applyFloor = applyNext >= 0x800;
            Console.WriteLine($"   APPLY-born patch floored counter  : {(applyOk ? (applyFloor ? $"PASS — persisted 0x{applyNext:X6}" : $"FAIL — persisted 0x{applyNext:X6} (below 0x800; the bug's seed)") : $"FAIL — Apply refused: {o.Error}")}");
        }

        // --- CREATE: create_record into= the Apply-born patch — THE BUG's exact trigger. ---
        bool create1Ok = false, create2Ok = false; FormKey fk1 = default, fk2 = default; uint next1 = 0, next2 = 0;
        using (var r = LoadOrderResolver.Build(new[] { mPath }))
        {
            var spec1 = new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcFidGuardKw1", Edits = Array.Empty<WriteRequest>() };
            var o1 = WritePatchBuilder.CreateRecords(r, rulebook, new[] { spec1 }, pPath, extend: true);
            if (o1.Success) { fk1 = o1.Created[0].FormKey; next1 = ReadDiskNextFormId(pPath); create1Ok = fk1.ID == 0x800 && next1 == 0x801; }
            Console.WriteLine($"   CREATE into= allocates 0x800      : {(o1.Success ? (create1Ok ? $"PASS — {fk1}, counter 0x{next1:X6}" : $"FAIL — allocated {fk1} (object ID 0x{fk1.ID:X6}), counter 0x{next1:X6}") : $"FAIL — refused: {o1.Error}")}");

            var spec2 = new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcFidGuardKw2", Edits = Array.Empty<WriteRequest>() };
            var o2 = WritePatchBuilder.CreateRecords(r, rulebook, new[] { spec2 }, pPath, extend: true);
            if (o2.Success) { fk2 = o2.Created[0].FormKey; next2 = ReadDiskNextFormId(pPath); create2Ok = fk2.ID == 0x801 && next2 == 0x802; }
            Console.WriteLine($"   second CREATE allocates 0x801     : {(o2.Success ? (create2Ok ? $"PASS — {fk2}, counter 0x{next2:X6}" : $"FAIL — allocated {fk2} (object ID 0x{fk2.ID:X6}), counter 0x{next2:X6}") : $"FAIL — refused: {o2.Error}")}");
        }

        // --- REMOVE: dropping the second created record must keep the counter at its high-water (no reuse). ---
        bool removeOk = false; uint nextAfterRemove = 0;
        if (create2Ok)
        {
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var o = WritePatchBuilder.RemoveRecords(r, new[] { fk2 }, pPath);
            if (o.Success) { nextAfterRemove = ReadDiskNextFormId(pPath); removeOk = nextAfterRemove == 0x802; }
            Console.WriteLine($"   REMOVE keeps high-water (0x802)   : {(o.Success ? (removeOk ? $"PASS — counter 0x{nextAfterRemove:X6}" : $"FAIL — counter 0x{nextAfterRemove:X6} (regressed; freed ID would be reused)") : $"FAIL — refused: {o.Error}")}");
        }
        else Console.WriteLine("   REMOVE keeps high-water (0x802)   : SKIP — CREATE arm failed upstream");

        // --- FRESH: the already-good path (patch born from CreateRecords) stays at 0x800. ---
        bool freshOk = false;
        {
            string qPath = Path.Combine(tmpDir, "HcFidGuardFresh.esp");
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var spec = new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcFidGuardKwF", Edits = Array.Empty<WriteRequest>() };
            var o = WritePatchBuilder.CreateRecords(r, rulebook, new[] { spec }, qPath, extend: false);
            if (o.Success) freshOk = o.Created[0].FormKey.ID == 0x800 && ReadDiskNextFormId(qPath) == 0x801;
            Console.WriteLine($"   FRESH create stays at 0x800       : {(o.Success ? (freshOk ? $"PASS — {o.Created[0].FormKey}" : $"FAIL — {o.Created[0].FormKey}, counter 0x{ReadDiskNextFormId(qPath):X6}") : $"FAIL — refused: {o.Error}")}");
        }

        // --- LEGACY-CREATE: create INTO the zero-counter CONTROL artifact — the report's exact in-the-wild shape
        //     (a patch written PRE-FIX). Pins GenericAddNew's floor: WritePatch's floor can't save a record that was
        //     already allocated at 000000 (it only floors the counter), so this arm goes RED if the allocation-side
        //     call is reverted, even with the persist-side intact. (PR #30 review.) ---
        bool legacyCreateOk = false;
        {
            string lcPath = Path.Combine(tmpDir, "legacy-create", "HcFidGuardCtl.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(lcPath)!);
            File.Copy(ctlPath, lcPath);
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var spec = new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcFidGuardKwL", Edits = Array.Empty<WriteRequest>() };
            var o = WritePatchBuilder.CreateRecords(r, rulebook, new[] { spec }, lcPath, extend: true);
            if (o.Success) legacyCreateOk = o.Created[0].FormKey.ID == 0x800 && ReadDiskNextFormId(lcPath) == 0x801;
            Console.WriteLine($"   LEGACY create heals zero counter  : {(o.Success ? (legacyCreateOk ? $"PASS — {o.Created[0].FormKey}, counter 0x{ReadDiskNextFormId(lcPath):X6}" : $"FAIL — allocated {o.Created[0].FormKey}, counter 0x{ReadDiskNextFormId(lcPath):X6}") : $"FAIL — refused: {o.Error}")}");
        }

        // --- LEGACY-APPLY: an override-only Apply extend of the zero-counter artifact — NO allocation happens, so
        //     GenericAddNew never runs; only WritePatch's floor can lift the persisted counter. Goes RED if the
        //     persist-side call is reverted, even with the allocation-side intact. (PR #30 review.) ---
        bool legacyApplyOk = false;
        {
            string laPath = Path.Combine(tmpDir, "legacy-apply", "HcFidGuardCtl.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(laPath)!);
            File.Copy(ctlPath, laPath);
            using var r = LoadOrderResolver.Build(new[] { mPath });
            var edit = new WritePatchBuilder.PatchEdit { Target = weapFk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "21" };
            var o = WritePatchBuilder.Apply(r, rulebook, new[] { edit }, laPath, extend: true);
            if (o.Success) legacyApplyOk = ReadDiskNextFormId(laPath) == 0x800;
            Console.WriteLine($"   LEGACY apply persists the floor   : {(o.Success ? (legacyApplyOk ? $"PASS — counter 0x{ReadDiskNextFormId(laPath):X6} with zero allocations" : $"FAIL — counter 0x{ReadDiskNextFormId(laPath):X6} (the zero survived the rewrite)") : $"FAIL — Apply refused: {o.Error}")}");
        }

        Console.WriteLine();
        bool pass = controlSeed && applyOk && applyFloor && create1Ok && create2Ok && removeOk && freshOk && legacyCreateOk && legacyApplyOk;
        Console.WriteLine($"=== formid-floor-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    /// <summary>Read the persisted HEDR.NextObjectID by reopening the file as a binary overlay (the header parses
    /// on open; equivalent to the bug report's byte inspection, without hand-parsing offsets).</summary>
    static uint ReadDiskNextFormId(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.ModHeader.Stats.NextFormID; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static bool Try(Action op, out string err)
    {
        try { op(); err = "ok"; return true; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message.Replace("\r", " ").Replace("\n", " ").Trim()}"; return false; }
    }
}

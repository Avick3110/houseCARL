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

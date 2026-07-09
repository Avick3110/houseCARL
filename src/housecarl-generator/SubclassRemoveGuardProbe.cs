using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for HCBR-2026-07-08-01 F3: removing a record whose concrete class is a
/// SUBCLASS of its flat group's T (a <c>GlobalShort</c> under <c>SkyrimGroup&lt;Global&gt;</c>, a
/// <c>GameSettingString</c> under <c>SkyrimGroup&lt;GameSetting&gt;</c>) silently no-op'd: Mutagen's typed
/// <c>Remove(FormKey, Type, throwIfUnknown:true)</c> does NOT throw for such a type — it just removes nothing —
/// so the remove lanes rewrote the whole file to no effect and only the post-write verify caught it ("record
/// still present"). Diagnosis (2026-07-09, this probe's original diag arms) separated the hypotheses:
///
///   H-TYPE   (CONFIRMED) — Remove with typeof(GlobalShort) leaves the record in memory; typeof(Global) and
///                          typeof(IGlobal) remove it. throwIfUnknown:true throws for NEITHER.
///   H-RETAIN (REFUTED)   — the report's "fails when the origin master must be retained" theory: a Weapon
///                          override removed while its origin master stays referenced works fine (arm C).
///
/// The fix, RED-proven here (every arm fails on the pre-fix code):
///   1. <see cref="WriteEngine.RemovalTypeFor"/> — both remove lanes route the typed Remove via the record's
///      FLAT GROUP's T (GlobalShort → Global), not the concrete runtime type; nested records keep the runtime
///      type (the remove-record-probe's proven shape).
///   2. <c>RemoveSurvivors</c> — both lanes verify IN MEMORY that every target is gone BEFORE serializing, so
///      any future engine no-op fails loud with the file UNTOUCHED instead of after a pointless rewrite.
///
/// Arms:
///   A  in-place: GlobalShort override removed while its origin master stays referenced (the report's EXACT
///      failing shape — GLOB anchor out, banter master retained by the alias).
///   B  in-place: GlobalShort override as the file's only record (master pruned) — proves A isn't retention-dependent.
///   C  in-place: Weapon override removed, origin master retained — the H-RETAIN control (passed even pre-fix).
///   D  default patch lane: GlobalShort removed from a houseCARL patch — the same routing bug lived there too.
///   E  in-place: GameSettingString — the OTHER abstract flat group, by construction not a Global special-case.
///   F  primitive documentation: Remove(typeof(GlobalShort)) still no-ops in Mutagen (if an upgrade fixes it,
///      this arm flags so the routing indirection can be re-evaluated) AND typeof(Global) removes.
///
/// Run: dotnet run --project src/housecarl-generator subclass-remove-guard
/// </summary>
public static class SubclassRemoveGuardProbe
{
    const string MasterName = "HcSubRmMaster.esm";
    const string UserName = "HcSubRmUser.esp";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — subclass-typed remove (HCBR-2026-07-08-01 F3)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-subclass-remove-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // ---- Setup: master with a GlobalShort + a GameSettingString + two weapons; a FULL user override plugin
        //      (G + GMST + W1 + W2) and a G-ONLY user plugin. ----
        string masterPath = Path.Combine(tmpDir, MasterName);
        string fullPristine = Path.Combine(tmpDir, "pristine-full", UserName);
        string gOnlyPristine = Path.Combine(tmpDir, "pristine-gonly", UserName);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPristine)!);
        Directory.CreateDirectory(Path.GetDirectoryName(gOnlyPristine)!);

        var masterKey = new ModKey("HcSubRmMaster", ModType.Master);
        FormKey gfk, gmstFk, w1fk, w2fk;
        {
            var m = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var g = new GlobalShort(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcSR_Glob", Data = 0 };
            m.Globals.Add(g);
            var gmst = new GameSettingString(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "sHcSR_Gmst", Data = "base" };
            m.GameSettings.Add(gmst);
            var w1 = m.Weapons.AddNew(); w1.EditorID = "HcSR_Weap1"; w1.BasicStats = new WeaponBasicStats { Damage = 10 };
            var w2 = m.Weapons.AddNew(); w2.EditorID = "HcSR_Weap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
            gfk = g.FormKey; gmstFk = gmst.FormKey; w1fk = w1.FormKey; w2fk = w2.FormKey;
            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE))
        {
            var full = new SkyrimMod(new ModKey("HcSubRmUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
            ((GlobalShort)full.Globals.GetOrAddAsOverride(mOv.Globals.First(x => x.FormKey == gfk))).Data = 1;
            ((GameSettingString)full.GameSettings.GetOrAddAsOverride(mOv.GameSettings.First(x => x.FormKey == gmstFk))).Data = "over";
            full.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == w1fk)).BasicStats!.Damage = 20;
            full.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == w2fk)).BasicStats!.Damage = 25;
            full.BeginWrite.ToPath(fullPristine).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();

            var gOnly = new SkyrimMod(new ModKey("HcSubRmUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
            ((GlobalShort)gOnly.Globals.GetOrAddAsOverride(mOv.Globals.First(x => x.FormKey == gfk))).Data = 1;
            gOnly.BeginWrite.ToPath(gOnlyPristine).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        }
        Console.WriteLine($"-- setup: master (GLOB {gfk}, GMST {gmstFk}, weapons); full user (G+GMST+W1+W2), G-only user --");
        Console.WriteLine();

        var results = new List<(string name, bool pass, string detail)>();

        // ===== A — in-place: GlobalShort override out, origin master RETAINED (the report's exact shape) =====
        {
            var path = Fresh(tmpDir, "A", fullPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, path });
            var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { gfk }, path, UserName);
            bool gone = !RecordPresent(path, gfk);
            bool masterKept = ReadMasters(path).Any(m => m.Equals(MasterName, StringComparison.OrdinalIgnoreCase));
            bool weaponKept = RecordPresent(path, w1fk);
            bool pass = o.Success && gone && masterKept && weaponKept;
            results.Add(("A in-place GlobalShort remove, origin master retained", pass,
                $"success={o.Success} gone={gone} masterKept={masterKept} weaponKept={weaponKept}  [{o.Error ?? "ok"}]"));
        }

        // ===== B — in-place: GlobalShort override is the ONLY record (master pruned) =====
        {
            var path = Fresh(tmpDir, "B", gOnlyPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, path });
            var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { gfk }, path, UserName);
            bool gone = !RecordPresent(path, gfk);
            bool pruned = !ReadMasters(path).Any(m => m.Equals(MasterName, StringComparison.OrdinalIgnoreCase));
            bool pass = o.Success && gone && pruned && o.RemainingRecords == 0;
            results.Add(("B in-place GlobalShort remove, master pruned (inert shell)", pass,
                $"success={o.Success} gone={gone} pruned={pruned} remaining={o.RemainingRecords}(want 0)  [{o.Error ?? "ok"}]"));
        }

        // ===== C — in-place: WEAPON override out, origin master retained (H-RETAIN control) =====
        {
            var path = Fresh(tmpDir, "C", fullPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, path });
            var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { w2fk }, path, UserName);
            bool gone = !RecordPresent(path, w2fk);
            bool masterKept = ReadMasters(path).Any(m => m.Equals(MasterName, StringComparison.OrdinalIgnoreCase));
            bool pass = o.Success && gone && masterKept;
            results.Add(("C in-place Weapon remove, origin master retained (retention control)", pass,
                $"success={o.Success} gone={gone} masterKept={masterKept}  [{o.Error ?? "ok"}]"));
        }

        // ===== D — DEFAULT patch lane: GlobalShort removed from a patch (same routing, other lane) =====
        {
            var path = Fresh(tmpDir, "D", fullPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, path });
            var o = WritePatchBuilder.RemoveRecords(r, new[] { gfk }, path);
            bool gone = !RecordPresent(path, gfk);
            bool pass = o.Success && gone;
            results.Add(("D default-lane GlobalShort remove from a patch", pass,
                $"success={o.Success} gone={gone}  [{o.Error ?? "ok"}]"));
        }

        // ===== E — in-place: GameSettingString (the OTHER abstract flat group, by construction) =====
        {
            var path = Fresh(tmpDir, "E", fullPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, path });
            var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { gmstFk }, path, UserName);
            bool gone = !RecordPresent(path, gmstFk);
            bool pass = o.Success && gone;
            results.Add(("E in-place GameSettingString remove (second abstract family)", pass,
                $"success={o.Success} gone={gone}  [{o.Error ?? "ok"}]"));
        }

        // ===== F — the Mutagen PRIMITIVE, documented: subclass type still no-ops; the group's T removes =====
        {
            var path = Fresh(tmpDir, "F", fullPristine);
            var mod = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
            bool subclassThrew = false;
            try { ((IMajorRecordEnumerable)mod).Remove(gfk, typeof(GlobalShort), throwIfUnknown: true); }
            catch { subclassThrew = true; }
            bool subclassNoOp = !subclassThrew && mod.EnumerateMajorRecords().Any(x => x.FormKey == gfk);
            ((IMajorRecordEnumerable)mod).Remove(gfk, typeof(Global), throwIfUnknown: true);
            bool groupTypeRemoved = !mod.EnumerateMajorRecords().Any(x => x.FormKey == gfk);
            bool routedType = WriteEngine.RemovalTypeFor(mod.EnumerateMajorRecords().First(x => x.FormKey == w1fk)) == typeof(Weapon)
                           && groupTypeRemoved;
            bool pass = subclassNoOp && groupTypeRemoved && routedType;
            results.Add(("F primitive: typeof(GlobalShort) no-ops (silently), typeof(Global) removes, RemovalTypeFor routes", pass,
                $"subclassNoOp={subclassNoOp}(threw={subclassThrew}) groupTypeRemoved={groupTypeRemoved} routedType={routedType}" +
                (subclassNoOp ? "" : "  << Mutagen's routing CHANGED — re-evaluate RemovalTypeFor")));
        }

        Console.WriteLine();
        int failed = 0;
        Console.WriteLine("================  RESULTS  ================");
        foreach (var (name, pass, detail) in results)
        {
            if (!pass) failed++;
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}\n        {detail}");
        }
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "ALL GREEN." : $"{failed} arm(s) FAILED.");
        return failed == 0 ? 0 : 1;
    }

    static string Fresh(string tmpDir, string tag, string pristine)
    {
        var dir = Path.Combine(tmpDir, tag);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, UserName);
        File.Copy(pristine, path, overwrite: true);
        return path;
    }

    static bool RecordPresent(string path, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.EnumerateMajorRecords().Any(x => x.FormKey == fk);
    }

    static List<string> ReadMasters(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
    }
}

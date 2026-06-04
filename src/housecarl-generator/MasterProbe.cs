using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Master-baseline scout (2026-06-02) — Aaron flagged a real bug: a plugin houseCARL writes must ALWAYS carry Skyrim.esm
/// as a master (the CK stamps it on every plugin; a masterless plugin is malformed by Skyrim convention). Our WritePatch
/// DERIVES masters from references and STRIPS unreferenced ones (proven: RemoveCreateProbe Q3a), so a self-contained
/// created record yields a MASTERLESS plugin. This probe finds HOW to force Skyrim.esm in given that strip behavior —
/// before wiring the fix (measure, don't assume; the AddNew-is-an-extension surprise is why we scout).
///
///   M0 — DISCOVER: dump the write-builder API (BeginWrite + each fluent stage) and any "masters list content" option enum,
///        so the fix uses the REAL method/enum names instead of a guess.
///   M1 — REPRODUCE: a self-contained created Keyword, written via the current WritePatch (Iterate) → masterless (the bug).
///
/// Run: dotnet run --project src/housecarl-generator master-probe
/// </summary>
public static class MasterProbe
{
    const string SkyrimEsm = @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    public static int RunProbe(string[] args)
    {
        var src = args.FirstOrDefault(a => a.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)) ?? SkyrimEsm;
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source master not found: {src}"); return 1; }

        Console.WriteLine("================================================================");
        Console.WriteLine(" master-baseline scout: how to FORCE Skyrim.esm onto every written plugin");
        Console.WriteLine("================================================================");

        var skyrim = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var tmp = Path.Combine(Path.GetTempPath(), "hc-master-probe");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        // ---------------------------------------------------------- M0: discover the write-builder API + option enums
        Console.WriteLine("--- M0: write-builder API surface (so the fix uses real names) ---");
        var probeMod = new SkyrimMod(new ModKey("HCMasterM0", ModType.Plugin), SkyrimRelease.SkyrimSE);
        DumpType("BeginWrite", probeMod.BeginWrite.GetType());
        // Call ToPath directly (typed — it takes a ModPath, not a string) and dump the next stage: that's where
        // WithLoadOrder + the master-list options live (the methods the fix will call).
        var afterPath = probeMod.BeginWrite.ToPath(Path.Combine(tmp, probeMod.ModKey.FileName));
        DumpType("…ToPath(...)", afterPath.GetType());
        var afterLo = afterPath.WithLoadOrder(skyrim);
        DumpType("…WithLoadOrder(...)", afterLo.GetType());
        // Find any enum whose name mentions masters-list content + dump its values.
        Console.WriteLine("  master-list option enums in Mutagen assemblies:");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
            foreach (var t in SafeTypes(asm).Where(t => t.IsEnum && (t.Name.Contains("Master", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("MastersList", StringComparison.OrdinalIgnoreCase))))
                Console.WriteLine($"    {t.FullName} {{ {string.Join(", ", Enum.GetNames(t))} }}");
        Console.WriteLine();

        // ---------------------------------------------------------- M1: reproduce the masterless plugin (the bug)
        Console.WriteLine("--- M1: a self-contained created Keyword via the current WritePatch → masterless (the bug) ---");
        {
            var pc = new SkyrimMod(new ModKey("HCMasterM1", ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.GenericAddNew(pc, "Keyword", "HC_Master_M1");
            var outM1 = Path.Combine(tmp, pc.ModKey.FileName);
            WriteEngine.WritePatch(pc, skyrim, outM1);
            using var back = SkyrimMod.CreateFromBinaryOverlay(outM1, SkyrimRelease.SkyrimSE);
            var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            Console.WriteLine($"   masters: {(masters.Count == 0 ? "(none)" : string.Join(", ", masters))}  -> {(masters.Count == 0 ? "REPRODUCED the bug (masterless)" : "unexpectedly has masters")}");
        }
        Console.WriteLine();

        var skyrimKey = new ModKey("Skyrim", ModType.Master);   // Skyrim.esm

        // ---------------------------------------------------------- M2: FORCE Skyrim.esm onto a self-contained create
        Console.WriteLine("--- M2: self-contained Keyword + WithExtraIncludedMasters(Skyrim.esm) → masters=[Skyrim.esm], record intact ---");
        {
            var pc = new SkyrimMod(new ModKey("HCMasterM2", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var kw = WriteEngine.GenericAddNew(pc, "Keyword", "HC_Master_M2");
            var fk = kw.FormKey;
            var outP = Path.Combine(tmp, pc.ModKey.FileName);
            pc.BeginWrite.ToPath(outP).WithLoadOrder(skyrim).WithExtraIncludedMasters(skyrimKey).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(outP, SkyrimRelease.SkyrimSE);
            var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            var present = back.Keywords.Any(k => k.FormKey == fk && k.EditorID == "HC_Master_M2");
            Console.WriteLine($"   masters={Fmt(masters)}  record-present={present}  fk={fk}");
            Console.WriteLine($"   => {(masters.Count == 1 && masters[0].Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) && present ? "FORCED + intact (the fix works)" : "!! UNEXPECTED")}");
        }
        Console.WriteLine();

        // ---------------------------------------------------------- M3: already-references-Skyrim → no duplicate, ref intact
        Console.WriteLine("--- M3: FormList referencing a real Skyrim keyword + the same extra-master → Skyrim.esm ONCE, ref resolves ---");
        {
            var refKw = skyrim.Keywords.First();
            var pc = new SkyrimMod(new ModKey("HCMasterM3", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var flst = WriteEngine.GenericAddNew(pc, "FormList", "HC_Master_M3_FL");
            WriteEngine.ApplyVerb(flst, new WriteRequest { RecordType = "FormList", Path = new[] { "Items" }, Verb = "Add", Value = refKw.FormKey.ToString() });
            var outP = Path.Combine(tmp, pc.ModKey.FileName);
            pc.BeginWrite.ToPath(outP).WithLoadOrder(skyrim).WithExtraIncludedMasters(skyrimKey).Write();
            using var back = SkyrimMod.CreateFromBinaryOverlay(outP, SkyrimRelease.SkyrimSE);
            var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            var fl = back.FormLists.FirstOrDefault(x => x.EditorID == "HC_Master_M3_FL");
            var refOk = fl?.Items.Any(i => i.FormKey == refKw.FormKey) ?? false;
            var noDup = masters.Count(m => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) == 1;
            Console.WriteLine($"   masters={Fmt(masters)}  ref-resolves={refOk}  no-duplicate={noDup}");
            Console.WriteLine($"   => {(masters.Count == 1 && noDup && refOk ? "clean (extra-master is idempotent when already referenced)" : "!! UNEXPECTED")}");
        }
        Console.WriteLine();

        Console.WriteLine("=== If M2 forced [Skyrim.esm] intact and M3 stayed single + ref-resolved, WithExtraIncludedMasters is the fix. ===");
        return 0;

        static string Fmt(IEnumerable<string> xs) { var l = xs.ToList(); return l.Count == 0 ? "(none)" : "[" + string.Join(", ", l) + "]"; }

        static void DumpType(string label, Type t)
        {
            Console.WriteLine($"  {label} -> {t.FullName}");
            foreach (var name in t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => m.DeclaringType == t && !m.IsSpecialName)
                         .Select(m => m.Name).Distinct().OrderBy(x => x))
                Console.WriteLine($"      .{name}");
        }

        static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        }
    }
}

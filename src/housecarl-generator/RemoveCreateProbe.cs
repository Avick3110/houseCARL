using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Capability-arc scout (Remove + Create, 2026-06-02) — answers the two NET-NEW unknowns before either capability is
/// built (the wave-3/4/5 scout-first precedent). Recon only: writes to a temp dir, touches no tracked file, reports
/// loud (Q3 — no silent answer).
///
///   Q4 (Create) — does Mutagen's <c>group.AddNew()</c> allocate a fresh LOCAL FormID (the new plugin's own range),
///     can the SAME <see cref="WriteEngine.ApplyVerb"/> path set fields on the new record, and does a new record that
///     references EXISTING master content serialize with the right DERIVED master header?
///   Q3 (clean-masters) — does the write path (<c>BeginWrite.WithLoadOrder</c>) DERIVE the master list from the
///     records' ACTUAL references, so a listed-but-unreferenced (stale) master is DROPPED on write? If yes,
///     "clean unused masters" is automatic — a side effect of any write, including a removal that drops the last
///     reference to a master (Q3b). If no, it needs an explicit prune (ModHeader.MasterReferences is writable).
/// </summary>
public static class RemoveCreateProbe
{
    const string SkyrimEsm = @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    public static int RunProbe(string[] args)
    {
        var src = args.FirstOrDefault(a => a.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)) ?? SkyrimEsm;
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source master not found: {src}"); return 1; }

        Console.WriteLine("================================================================");
        Console.WriteLine(" capability-arc scout: Create (AddNew/FormID) + clean-masters (write derivation)");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Source master: {src}");

        var skyrim = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var sample = skyrim.Keywords.First();
        var sampleFk = sample.FormKey;                       // a real Skyrim.esm form to reference
        Console.WriteLine($"Sample existing form to reference: {sampleFk} ({sample.EditorID})");
        Console.WriteLine();

        var tmp = Path.Combine(Path.GetTempPath(), "hc-rc-probe");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        // ------------------------------------------------------------ Q4a: AddNew allocates a fresh LOCAL FormID
        Console.WriteLine("--- Q4a: group.AddNew() allocates a fresh LOCAL FormID; ApplyVerb sets fields on it ---");
        var pc = new SkyrimMod(new ModKey("HousecarlCreateProbe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var kwA = pc.Keywords.AddNew();
        var kwB = pc.Keywords.AddNew("HC_Probe_Keyword");
        Console.WriteLine($"   AddNew()      -> {kwA.FormKey}  (id=0x{kwA.FormKey.ID:X6}, master={kwA.FormKey.ModKey.FileName})");
        Console.WriteLine($"   AddNew(edid)  -> {kwB.FormKey}  EditorID={kwB.EditorID}");
        WriteEngine.ApplyVerb(kwB, new WriteRequest { RecordType = "Keyword", Path = new[] { "EditorID" }, Verb = "Set", Value = "HC_Probe_Keyword_Renamed" });
        Console.WriteLine($"   ApplyVerb Set EditorID on the new record -> {kwB.EditorID}  (create -> edit reuses the proven write path)");
        Console.WriteLine();

        // ------------------------------------------------------------ Q4b: new record referencing existing -> derived masters
        Console.WriteLine("--- Q4b: a NEW record referencing an EXISTING master form -> derived master header ---");
        var flst = pc.FormLists.AddNew("HC_Probe_FList");
        WriteEngine.ApplyVerb(flst, new WriteRequest { RecordType = "FormList", Path = new[] { "Items" }, Verb = "Add", Value = sampleFk.ToString() });
        var outCreate = Path.Combine(tmp, "create", pc.ModKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outCreate)!);
        pc.BeginWrite.ToPath(outCreate).WithLoadOrder(new[] { skyrim }).Write();
        var backCreate = SkyrimMod.CreateFromBinaryOverlay(outCreate, SkyrimRelease.SkyrimSE);
        var mCreate = backCreate.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        var newKwBack = backCreate.Keywords.FirstOrDefault(k => k.EditorID == "HC_Probe_Keyword_Renamed");
        Console.WriteLine($"   wrote {pc.ModKey.FileName} ({new FileInfo(outCreate).Length}B); masters: {Fmt(mCreate)}");
        Console.WriteLine($"   new keyword read back from disk: {(newKwBack is null ? "(MISSING)" : newKwBack.FormKey.ToString())}  (created records persist)");
        Console.WriteLine($"   EXPECT masters = Skyrim.esm (the new FormList references it): {(mCreate.Any(IsSkyrim) ? "OK" : "!! UNEXPECTED")}");
        (backCreate as IDisposable)?.Dispose();
        Console.WriteLine();

        // ------------------------------------------------------------ Q3a: a STALE (unreferenced) master is dropped on write
        Console.WriteLine("--- Q3a: a listed-but-UNREFERENCED master is DROPPED on write (= clean-masters is automatic) ---");
        var pm = new SkyrimMod(new ModKey("HousecarlMasterProbe", ModType.Plugin), SkyrimRelease.SkyrimSE);
        pm.Keywords.AddNew("HC_Probe_Local");                // references nothing external -> the mod needs NO masters
        WriteEngine.ApplyVerb(pm.ModHeader, new WriteRequest { RecordType = "SkyrimModHeader", Path = new[] { "MasterReferences" }, Verb = "Add",
            Struct = new StructSpec { Type = "MasterReference", Fields = new() { ["Master"] = "Skyrim.esm" } } });
        Console.WriteLine($"   header BEFORE write (stale master injected): {Fmt(pm.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()))}");
        var outStale = Path.Combine(tmp, "stale", pm.ModKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outStale)!);
        pm.BeginWrite.ToPath(outStale).WithLoadOrder(new[] { skyrim }).Write();
        var backStale = SkyrimMod.CreateFromBinaryOverlay(outStale, SkyrimRelease.SkyrimSE);
        var mStale = backStale.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        Console.WriteLine($"   masters AFTER write: {Fmt(mStale)}");
        Console.WriteLine($"   EXPECT (none): {(mStale.Any(IsSkyrim) ? "!! PRESERVED — clean-masters would need an explicit prune" : "OK — DROPPED; masters derive from references, not the header list")}");
        (backStale as IDisposable)?.Dispose();
        Console.WriteLine();

        // ------------------------------------------------------------ Q3b: removing the last reference drops the master on re-write (into= path)
        Console.WriteLine("--- Q3b: REMOVING the last reference DROPS the master on re-write (the removal + clean-masters pairing) ---");
        var reopen = SkyrimMod.CreateFromBinary(outCreate, SkyrimRelease.SkyrimSE);   // the into= path: open the just-written patch mutably
        var flstBack = reopen.FormLists.First(fl => fl.EditorID == "HC_Probe_FList");
        WriteEngine.ApplyVerb(flstBack, new WriteRequest { RecordType = "FormList", Path = new[] { "Items" }, Verb = "Remove", Key = "0" }); // RemoveAt(0): drop its only ref
        var outRemoved = Path.Combine(tmp, "removed", reopen.ModKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outRemoved)!);
        reopen.BeginWrite.ToPath(outRemoved).WithLoadOrder(new[] { skyrim }).Write();
        var backRemoved = SkyrimMod.CreateFromBinaryOverlay(outRemoved, SkyrimRelease.SkyrimSE);
        var mRemoved = backRemoved.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
        Console.WriteLine($"   re-opened {reopen.ModKey.FileName}, removed the FormList's only Skyrim.esm ref, re-wrote.");
        Console.WriteLine($"   masters AFTER removal + rewrite: {Fmt(mRemoved)}");
        Console.WriteLine($"   EXPECT (none): {(mRemoved.Any(IsSkyrim) ? "!! PRESERVED — removal does NOT auto-clean; explicit prune needed" : "OK — DROPPED; removal auto-cleans the now-unused master")}");
        (backRemoved as IDisposable)?.Dispose();
        Console.WriteLine();

        Console.WriteLine("=== capability-arc scout done — read the EXPECT lines (Q4a/Q4b = Create; Q3a/Q3b = clean-masters). ===");
        return 0;

        static bool IsSkyrim(string m) => m.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase);
        static string Fmt(IEnumerable<string> xs) { var l = xs.ToList(); return l.Count == 0 ? "(none)" : string.Join(", ", l); }
    }
}

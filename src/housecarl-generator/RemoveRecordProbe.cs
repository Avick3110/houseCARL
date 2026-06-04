using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// Capability-arc scout #2 — the ONE net-new unknown for the remove-record build: Mutagen's whole-record removal
/// API. The session-1 scout (<see cref="RemoveCreateProbe"/>) proved Create (AddNew) + clean-masters + remove-ENTRY,
/// but NEVER exercised whole-record removal — the plan only ASSERTED "<c>group.Remove(formKey)</c>". This probe
/// measures the real mechanism before the build (the wave-3/4/5 scout-first precedent; "measure, don't assume").
///
/// The XML doc surfaced a better primitive than the plan's flat-group <c>group.Remove</c>:
/// <c>IMajorRecordEnumerable.Remove(FormKey)</c> on the MOD — "removes any records WITHIN matching the FormKey" —
/// which should reach NESTED-group records (Cell/Placed*/INFO/Navmesh/Landscape) by Mutagen's own traversal, the
/// piece the handoff flagged "needs care". This probe confirms or refutes that, plus the not-found return semantics
/// the tool needs to fail loud (Q3 — never a silent no-op removal).
///
///   Q1 (return semantics) — what does <c>mod.Remove(FormKey)</c> return? Does it signal found-vs-not-found so the
///       tool can refuse loud when the patch doesn't carry the record?
///   Q2 (flat, created)    — remove an AddNew'd flat record (a Keyword) → gone from the re-read plugin.
///   Q3 (flat, override)   — override a real Skyrim.esm flat record (Armor) into a patch, write, RE-OPEN from disk
///       (the real into= flow), remove, re-write → the patch no longer carries it (winner reverts by absence).
///   Q4 (NESTED, override) — override a nested-group record (RecordNeedsSourceCache=true), remove → gone. The
///       load-bearing test: does the mod-level Remove reach nested records, or do we still need parent-chain nav?
///   Q5 (clean-masters)    — removing the override that was the last reference to a master → master dropped on
///       re-write (the record-level pairing of the Q3b entry-level finding).
///   Q6 (literal, not flag) — the removed record leaves NO deleted-stub (Aaron locked literal drop-from-plugin).
/// </summary>
public static class RemoveRecordProbe
{
    const string SkyrimEsm = @"C:\Program Files (x86)\Steam\steamapps\common\Skyrim Special Edition\Data\Skyrim.esm";

    public static int RunProbe(string[] args)
    {
        var src = args.FirstOrDefault(a => a.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)) ?? SkyrimEsm;
        if (!File.Exists(src)) { Console.Error.WriteLine($"error: source master not found: {src}"); return 1; }

        Console.WriteLine("================================================================");
        Console.WriteLine(" capability-arc scout #2: whole-record removal (mod.Remove(FormKey))");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Source master: {src}");
        Console.WriteLine();

        var skyrim = SkyrimMod.CreateFromBinaryOverlay(src, SkyrimRelease.SkyrimSE);
        var cache = skyrim.ToImmutableLinkCache();

        var tmp = Path.Combine(Path.GetTempPath(), "hc-remove-record-probe");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);

        var shaBefore = Sha(src);
        bool allPass = true;

        // ---------------------------------------------------------- Q1: the Remove(FormKey) method + return semantics
        Console.WriteLine("--- Q1: locate mod.Remove(FormKey) + its return type (so the tool can fail loud on not-found) ---");
        var removeMethod = typeof(SkyrimMod).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Remove" && !m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 1 } ps && ps[0].ParameterType == typeof(FormKey));
        if (removeMethod is null)
        {
            // May be an explicit interface implementation — reflect off the interface map.
            removeMethod = typeof(IMajorRecordEnumerable).GetMethod("Remove", new[] { typeof(FormKey) });
        }
        Console.WriteLine(removeMethod is null
            ? "   !! Remove(FormKey) NOT found on SkyrimMod / IMajorRecordEnumerable — the plan's mechanism is wrong"
            : $"   found: {removeMethod.DeclaringType?.Name}.Remove(FormKey) -> returns {removeMethod.ReturnType.Name}");
        if (removeMethod is null) return 1;
        bool returnsBool = removeMethod.ReturnType == typeof(bool);
        Console.WriteLine($"   signals found-vs-not-found via return value: {(returnsBool ? "YES (bool)" : "NO — need a present-check before removing")}");
        Console.WriteLine();

        // ---------------------------------------------------------- Q2: flat, created (AddNew) → removed
        Console.WriteLine("--- Q2: remove an AddNew'd FLAT record (Keyword) → gone from the re-read plugin ---");
        {
            var pc = new SkyrimMod(new ModKey("HCRemoveQ2", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var kw = pc.Keywords.AddNew("HC_Q2_Keyword");
            var fk = kw.FormKey;
            Console.WriteLine($"   created {fk} ({kw.EditorID}); group count before = {pc.Keywords.Count}");
            var ret = InvokeRemove(removeMethod, pc, fk);
            Console.WriteLine($"   mod.Remove({fk}) returned: {Show(ret)}; group count after = {pc.Keywords.Count}");
            var outP = WriteRead(pc, tmp, "q2", skyrim, out var back);
            var stillThere = back.EnumerateMajorRecords().Any(r => r.FormKey == fk);
            Console.WriteLine($"   re-read {Path.GetFileName(outP)}: record present = {stillThere}");
            Pass(ref allPass, "Q2 flat-created removal", !stillThere && pc.Keywords.Count == 0);
            (back as IDisposable)?.Dispose();
        }
        Console.WriteLine();

        // ---------------------------------------------------------- Q3: flat, override, via DISK re-open (the into= flow)
        Console.WriteLine("--- Q3: override a real FLAT Armor, write, RE-OPEN from disk, remove, re-write → patch drops it ---");
        FormKey armorFk = default; string? masterOfArmor = null;
        {
            var armor = skyrim.Armors.FirstOrDefault(a => a.EditorID == "ArmorIronCuirass") ?? skyrim.Armors.First();
            armorFk = armor.FormKey;
            var pc = new SkyrimMod(new ModKey("HCRemoveQ3", ModType.Plugin), SkyrimRelease.SkyrimSE);
            WriteEngine.GenericGetOrAddAsOverride(pc, armor, cache);
            var outP = Path.Combine(tmp, "q3", pc.ModKey.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outP)!);
            WriteEngine.WritePatch(pc, skyrim, outP);
            using (var firstBack = SkyrimMod.CreateFromBinaryOverlay(outP, SkyrimRelease.SkyrimSE))
            {
                masterOfArmor = firstBack.ModHeader.MasterReferences.FirstOrDefault()?.Master.FileName.ToString();
                Console.WriteLine($"   patch carries override of {armorFk} ({armor.EditorID}); masters = {masterOfArmor ?? "(none)"}");
            }
            // The REAL into= flow: re-open the written patch MUTABLY, remove, re-write.
            var reopen = SkyrimMod.CreateFromBinary(outP, SkyrimRelease.SkyrimSE);
            var ret = InvokeRemove(removeMethod, reopen, armorFk);
            var outP2 = Path.Combine(tmp, "q3b", reopen.ModKey.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outP2)!);
            WriteEngine.WritePatch(reopen, skyrim, outP2);
            var back = SkyrimMod.CreateFromBinaryOverlay(outP2, SkyrimRelease.SkyrimSE);
            var stillThere = back.EnumerateMajorRecords().Any(r => r.FormKey == armorFk);
            var mastersAfter = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            Console.WriteLine($"   reopened from disk, mod.Remove({armorFk}) returned: {Show(ret)}");
            Console.WriteLine($"   re-read after removal: override present = {stillThere}; masters = {Fmt(mastersAfter)}");
            Pass(ref allPass, "Q3 flat-override removal (disk re-open)", !stillThere);
            // Q5 rides on Q3: the Armor was the ONLY record, and it referenced Skyrim.esm → removing it should drop the master.
            Pass(ref allPass, "Q5 clean-masters: removing last referencer drops the master", mastersAfter.Count == 0);
            (back as IDisposable)?.Dispose();
        }
        Console.WriteLine();

        // ---------------------------------------------------------- Q4: NESTED-group override → removed (the load-bearing test)
        Console.WriteLine("--- Q4: override a NESTED-group record (RecordNeedsSourceCache=true), remove → gone (no parent-chain nav?) ---");
        {
            IMajorRecordGetter? nested = null;
            foreach (var rec in skyrim.EnumerateMajorRecords())
                if (WriteEngine.RecordNeedsSourceCache(rec)) { nested = rec; break; }
            if (nested is null)
            {
                Console.WriteLine("   !! no nested-group record found in source — cannot test Q4");
                allPass = false;
            }
            else
            {
                var fk = nested.FormKey;
                Console.WriteLine($"   nested target: {fk} ({RecordNaming.StripOverlay(nested.GetType().Name)} {nested.EditorID ?? "<no edid>"})");
                var pc = new SkyrimMod(new ModKey("HCRemoveQ4", ModType.Plugin), SkyrimRelease.SkyrimSE);
                WriteEngine.GenericGetOrAddAsOverride(pc, nested, cache);
                var presentBefore = pc.EnumerateMajorRecords().Any(r => r.FormKey == fk);
                var ret = InvokeRemove(removeMethod, pc, fk);
                var presentAfterInMem = pc.EnumerateMajorRecords().Any(r => r.FormKey == fk);
                Console.WriteLine($"   override present before remove = {presentBefore}; mod.Remove returned {Show(ret)}; present after (in-mem) = {presentAfterInMem}");
                var outP = WriteRead(pc, tmp, "q4", skyrim, out var back);
                var stillOnDisk = back.EnumerateMajorRecords().Any(r => r.FormKey == fk);
                Console.WriteLine($"   re-read {Path.GetFileName(outP)}: nested record present = {stillOnDisk}");
                Pass(ref allPass, "Q4 NESTED-group removal via mod.Remove (no parent-chain nav)", presentBefore && !presentAfterInMem && !stillOnDisk);
                (back as IDisposable)?.Dispose();
            }
        }
        Console.WriteLine();

        // ---------------------------------------------------------- Q1b: NOT-FOUND semantics (the fail-loud signal)
        Console.WriteLine("--- Q1b: mod.Remove(FormKey) for a record NOT in the patch → return value / throw? (Q3 fail-loud) ---");
        {
            var pc = new SkyrimMod(new ModKey("HCRemoveQ1b", ModType.Plugin), SkyrimRelease.SkyrimSE);
            pc.Keywords.AddNew("HC_Q1b_Decoy");                  // carries ONE record, but not the one we ask to remove
            var absent = armorFk;                                 // a real Skyrim.esm key the patch does NOT carry
            object? ret = null; string? threw = null;
            try { ret = InvokeRemove(removeMethod, pc, absent); }
            catch (Exception ex) { threw = ex.GetType().Name + ": " + ex.Message; }
            Console.WriteLine(threw is not null
                ? $"   threw: {threw}"
                : $"   no throw; returned: {Show(ret)}  (false/no-op => the tool must present-check or trust the bool to refuse loud)");
            // We don't fail the probe on either outcome — we just need to KNOW it to design the tool's fail-loud.
            Console.WriteLine($"   => tool design: {(returnsBool ? "trust the bool (false = not carried → refuse)" : "present-check before Remove, then refuse if absent")}");
        }
        Console.WriteLine();

        // ---------------------------------------------------------- Q7: the TYPED overload (CS0618 steers here) — does it reach NESTED?
        // The bare Remove(FormKey) is [Obsolete] ("not as optimized... use as a last resort"); Mutagen steers to the
        // typed Remove(FormKey, Type, throwIfUnknown). The typed one goes "to a specific group" — so it may MISS nested
        // records (no flat group), a silent void no-op (Q3 violation). Measure before choosing which overload to ship.
        Console.WriteLine("--- Q7: typed Remove(FormKey, Type, throwIfUnknown) — does it reach FLAT and NESTED, or silently miss nested? ---");
        var typedRemove = typeof(SkyrimMod).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Remove" && !m.IsGenericMethodDefinition
                && m.GetParameters() is { Length: 3 } ps
                && ps[0].ParameterType == typeof(FormKey) && ps[1].ParameterType == typeof(Type) && ps[2].ParameterType == typeof(bool));
        if (typedRemove is null)
            typedRemove = typeof(IMajorRecordEnumerable).GetMethod("Remove", new[] { typeof(FormKey), typeof(Type), typeof(bool) });
        Console.WriteLine($"   typed overload: {(typedRemove is null ? "NOT FOUND" : typedRemove.DeclaringType?.Name + ".Remove(FormKey,Type,bool) -> " + typedRemove.ReturnType.Name)}");
        if (typedRemove is not null)
        {
            // FLAT: created keyword, removed by concrete type + by getter interface.
            foreach (var useGetter in new[] { false, true })
            {
                var pc = new SkyrimMod(new ModKey($"HCQ7Flat{(useGetter ? "G" : "C")}", ModType.Plugin), SkyrimRelease.SkyrimSE);
                var kw = pc.Keywords.AddNew("HC_Q7_Flat");
                var t = useGetter ? typeof(IKeywordGetter) : typeof(Keyword);
                string outcome;
                try { typedRemove.Invoke(pc, new object[] { kw.FormKey, t, true }); outcome = pc.Keywords.Any(k => k.FormKey == kw.FormKey) ? "STILL PRESENT" : "removed"; }
                catch (Exception ex) { outcome = "threw " + (ex.InnerException ?? ex).GetType().Name; }
                Console.WriteLine($"   FLAT Keyword via type={t.Name,-14} -> {outcome}");
            }
            // NESTED: override a nested record, remove by concrete type + by getter interface — the load-bearing test.
            IMajorRecordGetter? nestedT = null;
            foreach (var rec in skyrim.EnumerateMajorRecords()) if (WriteEngine.RecordNeedsSourceCache(rec)) { nestedT = rec; break; }
            if (nestedT is not null)
            {
                foreach (var useGetter in new[] { false, true })
                {
                    var pc = new SkyrimMod(new ModKey($"HCQ7Nest{(useGetter ? "G" : "C")}", ModType.Plugin), SkyrimRelease.SkyrimSE);
                    var ov = WriteEngine.GenericGetOrAddAsOverride(pc, nestedT, cache);
                    var t = useGetter ? (PrimaryGetterOf(ov.GetType()) ?? ov.GetType()) : ov.GetType();
                    string outcome;
                    try { typedRemove.Invoke(pc, new object[] { nestedT.FormKey, t, true }); outcome = pc.EnumerateMajorRecords().Any(r => r.FormKey == nestedT.FormKey) ? "!! STILL PRESENT (silent miss)" : "removed"; }
                    catch (Exception ex) { outcome = "threw " + (ex.InnerException ?? ex).GetType().Name + ": " + (ex.InnerException ?? ex).Message; }
                    Console.WriteLine($"   NESTED {RecordNaming.StripOverlay(nestedT.GetType().Name)} via type={t.Name,-20} -> {outcome}");
                }
            }
            Console.WriteLine("   => if NESTED 'STILL PRESENT' or 'threw', the typed overload can't safely serve nested → ship the bare overload (correctness > the perf the [Obsolete] steers toward).");
        }
        Console.WriteLine();

        var sourceUnchanged = shaBefore == Sha(src);
        Console.WriteLine($"Source unchanged: {(sourceUnchanged ? "YES" : "NO")}");
        Console.WriteLine();
        var pass = allPass && sourceUnchanged;
        Console.WriteLine(pass
            ? "=== PASS: whole-record removal mechanism PROVEN (flat + nested via mod.Remove; clean-masters rides along). ==="
            : "=== FAIL — read the per-Q lines above. ===");
        return pass ? 0 : 1;

        static string Sha(string p) { using var s = File.OpenRead(p); using var h = System.Security.Cryptography.SHA256.Create(); return Convert.ToHexString(h.ComputeHash(s)); }
        static string Fmt(IEnumerable<string> xs) { var l = xs.ToList(); return l.Count == 0 ? "(none)" : string.Join(", ", l); }
        static string Show(object? o) => o is null ? "(void)" : o.ToString() ?? "(null)";
        static void Pass(ref bool all, string label, bool ok) { Console.WriteLine($"   [{(ok ? "PASS" : "!! FAIL")}] {label}"); if (!ok) all = false; }
    }

    /// <summary>Invoke the reflected Remove(FormKey); returns its result (null if void).</summary>
    static object? InvokeRemove(MethodInfo remove, SkyrimMod mod, FormKey fk)
        => remove.Invoke(mod, new object[] { fk });

    /// <summary>The getter interface for a concrete record type (Loqui convention: Cell -> ICellGetter).</summary>
    static Type? PrimaryGetterOf(Type concrete) => concrete.GetInterface("I" + concrete.Name + "Getter");

    /// <summary>Write the patch with masters derived from the source, re-open as an overlay, hand back both.</summary>
    static string WriteRead(SkyrimMod patch, string tmp, string sub, ISkyrimModGetter source, out ISkyrimModGetter back)
    {
        var outP = Path.Combine(tmp, sub, patch.ModKey.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(outP)!);
        WriteEngine.WritePatch(patch, source, outP);
        back = SkyrimMod.CreateFromBinaryOverlay(outP, SkyrimRelease.SkyrimSE);
        return outP;
    }
}

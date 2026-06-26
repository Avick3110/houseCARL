using System.Diagnostics;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE — WAVE 1 mechanism pin (the FIRST, load-bearing unknown the build plan rests on).
///
/// Wave-0 proved <c>mod.RemapLinks(dict)</c> repoints OUTGOING FormLinks (a record's references). The compact build
/// plan §4 step 1 ALSO assumes a way to RENUMBER a record's OWN FormID (move it into the ESL 0x800–0xFFF range) — and
/// that half was NEVER tested. This probe settles, self-contained (synthetic, TEMP, no MO2/Skyrim.esm), exactly:
///   1. Does <c>RemapLinks(dict)</c> change a record's OWN identity, or ONLY its outgoing references?
///   2. Is <c>MajorRecord.FormKey</c> settable (so we can renumber identity directly)? — reflection, no compile dep.
///   3. What mod-/record-level renumber affordances does Mutagen actually expose? — reflect "Remap"/"Duplicate"/"Compact".
///
/// Then it RUNS the candidate that the reflection says exists and reports the on-disk truth after a write+reread:
/// a Weapon A and a FormList L→[A]; renumber A; re-read; report (a) where A's identity landed and (b) where L's entry
/// points. That four-way truth table (identity moved? link moved?) tells us EXACTLY which mechanism compact must use.
///
/// Run: dotnet run --project src/housecarl-generator remap-wave1-mech
/// </summary>
public static class RemapWave1Probe
{
    public static int RunMechanism(string[] args)
    {
        Console.WriteLine("################  COMPACT/MERGE WAVE 1 — renumber-identity mechanism pin  ################");
        Console.WriteLine();

        // ---- 2 + 3: reflection — what does Mutagen expose for renumbering, without a compile dependency? ----
        var weapT = typeof(Weapon);
        var fkProp = weapT.GetProperty("FormKey");
        Console.WriteLine("REFLECTION:");
        Console.WriteLine($"   Weapon.FormKey  CanRead={fkProp?.CanRead} CanWrite={fkProp?.CanWrite} setter={(fkProp?.SetMethod is { } sm ? (sm.IsPublic ? "public" : "non-public") : "<none>")}");

        var modT = typeof(SkyrimMod);
        foreach (var needle in new[] { "Remap", "Duplicate", "Compact", "FormID", "FormKey" })
        {
            var ms = modT.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                .Distinct().ToList();
            if (ms.Count > 0) Console.WriteLine($"   SkyrimMod methods ~ '{needle}': {string.Join("; ", ms)}");
        }
        foreach (var needle in new[] { "Remap", "Duplicate" })
        {
            var ms = weapT.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .Select(m => $"{m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})")
                .Distinct().ToList();
            if (ms.Count > 0) Console.WriteLine($"   Weapon methods ~ '{needle}': {string.Join("; ", ms)}");
        }
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-remap-wave1-mech");
        if (Directory.Exists(tmpDir)) { try { Directory.Delete(tmpDir, true); } catch { } }
        Directory.CreateDirectory(tmpDir);

        var donorKey = new ModKey("HcRemapDonor", ModType.Plugin);
        var aOld = new FormKey(donorKey, 0x800);
        var aNew = new FormKey(donorKey, 0x900);
        var lKey = new FormKey(donorKey, 0x801);

        // ---- Experiment 1: RemapLinks alone — does it touch identity, or only the FormList's outgoing link? ----
        Console.WriteLine("EXPERIMENT 1 — mod.RemapLinks({A_old -> A_new}) ALONE:");
        RunRenumberExperiment(tmpDir, "remaplinks", donorKey, aOld, aNew, lKey,
            mod => mod.RemapLinks(new Dictionary<FormKey, FormKey> { [aOld] = aNew }));

        // ---- Experiment 2: set the record's FormKey directly (if reflection said it's writable). ----
        if (fkProp?.CanWrite == true)
        {
            Console.WriteLine("EXPERIMENT 2 — set A.FormKey = A_new directly (no RemapLinks):");
            RunRenumberExperiment(tmpDir, "setformkey", donorKey, aOld, aNew, lKey,
                mod => { foreach (var w in mod.Weapons.Where(w => w.FormKey == aOld)) fkProp.SetValue(w, aNew); });

            Console.WriteLine("EXPERIMENT 3 — set A.FormKey = A_new  AND  RemapLinks (the plan §4 step1+step2 combo):");
            RunRenumberExperiment(tmpDir, "both", donorKey, aOld, aNew, lKey,
                mod =>
                {
                    foreach (var w in mod.Weapons.Where(w => w.FormKey == aOld)) fkProp.SetValue(w, aNew);
                    mod.RemapLinks(new Dictionary<FormKey, FormKey> { [aOld] = aNew });
                });
        }
        else Console.WriteLine("EXPERIMENT 2/3 — SKIPPED: Weapon.FormKey is not writable (compact must renumber some other way).");

        Console.WriteLine();

        // ---- 4: in-memory GROUP CONSISTENCY after a FormKey-set. The group is a FormKey-keyed cache; if setting the
        //         record's FormKey does NOT re-key the cache, the group is internally inconsistent (record says new,
        //         cache still keyed by old) — a silent-corruption risk RemapEngine must not build on. ----
        if (fkProp?.CanWrite == true)
        {
            Console.WriteLine("GROUP CONSISTENCY — after setting A.FormKey = A_new in memory (no write):");
            var mod = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            mod.Weapons.Add(new Weapon(aOld, SkyrimRelease.SkyrimSE) { EditorID = "HcRemapWeapA" });
            var grp = mod.Weapons;
            bool hadOld = grp.ContainsKey(aOld);
            fkProp.SetValue(grp.First(), aNew);
            bool hasOldKey = grp.ContainsKey(aOld), hasNewKey = grp.ContainsKey(aNew);
            FormKey? lookupOld = grp.TryGetValue(aOld, out var ro) ? ro.FormKey : (FormKey?)null;
            FormKey? lookupNew = grp.TryGetValue(aNew, out var rn) ? rn.FormKey : (FormKey?)null;
            var enumKeys = grp.Select(w => w.FormKey).ToList();
            Console.WriteLine($"   before: ContainsKey(old)={hadOld}");
            Console.WriteLine($"   after : ContainsKey(old)={hasOldKey}  ContainsKey(new)={hasNewKey}  enumerated record FormKeys=[{string.Join(",", enumKeys)}]");
            Console.WriteLine($"   after : TryGetValue(old)->{lookupOld?.ToString() ?? "<none>"}   TryGetValue(new)->{lookupNew?.ToString() ?? "<none>"}");
            bool consistent = !hasOldKey && hasNewKey && lookupNew == aNew && lookupOld is null;
            Console.WriteLine($"   => group re-keyed consistently to A_new? {consistent}  {(consistent ? "" : "(!!) cache key did NOT follow the record — RemapEngine must re-key explicitly")}");
        }
        Console.WriteLine();

        // ---- 5: is there a PUBLIC Mutagen renumber/duplicate affordance (vs the non-public FormKey setter)? Scan the
        //         Mutagen assemblies' static classes for Duplicate/DeepCopy extensions taking a record (the OverrideMethod
        //         finder pattern), and check the record interfaces' own members. ----
        Console.WriteLine("PUBLIC RENUMBER/DUPLICATE AFFORDANCES (Mutagen static extensions + interface members):");
        var hits = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => (a.GetName().Name ?? "").StartsWith("Mutagen")))
        {
            Type[] types; try { types = asm.GetTypes(); } catch { continue; }
            foreach (var t in types.Where(t => t is { IsAbstract: true, IsSealed: true, IsPublic: true })) // static classes
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if ((m.Name is "Duplicate" or "DeepCopy" || m.Name.StartsWith("Duplicate"))
                        && m.GetParameters().Length >= 1
                        && typeof(IMajorRecordGetter).IsAssignableFrom(m.GetParameters()[0].ParameterType))
                        hits.Add($"{t.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
        }
        foreach (var h in hits.Distinct().Take(20)) Console.WriteLine($"   {h}");
        if (hits.Count == 0) Console.WriteLine("   (none found — no public Duplicate(record,...) extension; renumber-in-place via the FormKey setter is the path)");
        Console.WriteLine();

        // ---- 6: THE CHOSEN MECHANISM — copy records into a FRESH mod under chosen keys via the PUBLIC Duplicate(key),
        //         then RemapLinks. This is what RemapEngine will do (no non-public setter, no group corruption, no
        //         collisions since the target starts empty). Renumber A 0x800->0x900 AND L 0x801->0x901; L->[A] must
        //         repoint to 0x900. Assert: identities at new keys, group consistent (ContainsKey new / not old), link repointed. ----
        Console.WriteLine("CHOSEN MECHANISM — Duplicate(record, newKey) into a fresh mod + RemapLinks:");
        try
        {
            string path = Path.Combine(tmpDir, "chosen", donorKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lOld = lKey;                       // 0x801
            var lNew = new FormKey(donorKey, 0x901);
            var dict = new Dictionary<FormKey, FormKey> { [aOld] = aNew, [lOld] = lNew };

            // source
            var src = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            src.Weapons.Add(new Weapon(aOld, SkyrimRelease.SkyrimSE) { EditorID = "HcRemapWeapA" });
            var srcL = new FormList(lOld, SkyrimRelease.SkyrimSE) { EditorID = "HcRemapList" };
            srcL.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(aOld));
            src.FormLists.Add(srcL);

            // fresh target, same ModKey; Duplicate each originating record under its NEW key, place in its group.
            var tgt = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            tgt.Weapons.Add((Weapon)src.Weapons.First().Duplicate(aNew));
            tgt.FormLists.Add((FormList)src.FormLists.First().Duplicate(lNew));
            if (tgt.ModHeader.Stats.NextFormID < 0x800) tgt.ModHeader.Stats.NextFormID = 0x800;
            tgt.RemapLinks(dict);

            bool memOk = tgt.Weapons.ContainsKey(aNew) && !tgt.Weapons.ContainsKey(aOld)
                       && tgt.FormLists.ContainsKey(lNew) && !tgt.FormLists.ContainsKey(lOld);

            tgt.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            using var rb = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var weapKeys = rb.Weapons.Select(w => w.FormKey).ToList();
            var listKeys = rb.FormLists.Select(l => l.FormKey).ToList();
            var listEntry = rb.FormLists.First().Items.FirstOrDefault()?.FormKey;
            bool diskOk = weapKeys.SequenceEqual(new[] { aNew }) && listKeys.SequenceEqual(new[] { lNew }) && listEntry == aNew;
            Console.WriteLine($"   in-memory group consistent (new keys, not old)? {memOk}");
            Console.WriteLine($"   on-disk weapons=[{string.Join(",", weapKeys)}] formlists=[{string.Join(",", listKeys)}] list entry -> {listEntry}");
            Console.WriteLine($"   => CHOSEN MECHANISM correct? {memOk && diskOk}");
        }
        catch (Exception ex) { Console.WriteLine($"   THREW {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine();

        Console.WriteLine("=== remap-wave1-mech: DONE — read the identity/link truth table above ===");
        try { Directory.Delete(tmpDir, true); } catch { }
        return 0;
    }

    // ======================================================================
    //  GUARD — the Wave 1 gate: a self-contained, CI-able end-to-end compact of a
    //  synthetic MULTI-plugin fixture, exercising every RemapEngine primitive (plan §9).
    // ======================================================================

    /// <summary>
    /// Self-contained regression guard for the compact/merge foundation (RemapEngine). Synthesizes a multi-plugin
    /// fixture in TEMP (no MO2, no Skyrim.esm) and drives the FULL cycle through the REAL engine, then asserts the
    /// on-disk truth. Arms (ALL required — a GREEN must mean "the foundation compacts end-to-end correctly"):
    ///   HAPPY    — Donor.esp {Weapon WA@0xAAA, FormList FL@0xCCC->[WA]} compacts into a NEW P′ (records renumbered to
    ///              the ESL window 0x800,0x801; FL's INTERNAL ref to WA repointed); the identify-pass over the order
    ///              finds External.esp (which references WA) as an EXTERNAL referencer and NOT Donor itself; the in-place
    ///              repoint rewrites External's reference to the new key. The whole compact, proven on disk.
    ///   CAPACITY — BuildSequentialRemap refuses LOUD when the record count overflows the target window (the ESL
    ///              "too many records to fit the light range" ceiling) — named, never truncated (Q3).
    ///   NESTED   — RenumberRecordsInto refuses LOUD on a nested-only record (a Cell has no flat group to place the
    ///              duplicate) — the honest coverage boundary, never a silent drop (Q3).
    /// Run: dotnet run --project src/housecarl-generator remap-wave1-guard
    /// </summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT/MERGE WAVE 1 — RemapEngine end-to-end guard  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-remap-wave1-guard");
        if (Directory.Exists(tmpDir)) { try { Directory.Delete(tmpDir, true); } catch { } }
        Directory.CreateDirectory(tmpDir);

        var donorKey = new ModKey("HcRemapDonor", ModType.Plugin);
        var extKey = new ModKey("HcRemapExternal", ModType.Plugin);
        var waOld = new FormKey(donorKey, 0xAAA);     // weapon, will renumber -> 0x800
        var flOld = new FormKey(donorKey, 0xCCC);     // formlist -> [WA] (internal), will renumber -> 0x801
        var exlKey = new FormKey(extKey, 0x800);      // External's formlist -> [WA] (EXTERNAL ref into Donor)

        string donorPath = Path.Combine(tmpDir, donorKey.FileName.String);
        string extPath = Path.Combine(tmpDir, extKey.FileName.String);

        // --- synthesize Donor.esp {WA@0xAAA, FL@0xCCC -> [WA]} ---
        {
            var d = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            d.Weapons.Add(new Weapon(waOld, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapA", BasicStats = new WeaponBasicStats { Damage = 10 } });
            var fl = new FormList(flOld, SkyrimRelease.SkyrimSE) { EditorID = "HcList" };
            fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(waOld));
            d.FormLists.Add(fl);
            d.ModHeader.Stats.NextFormID = 0xCCD;
            d.BeginWrite.ToPath(donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
        }
        // --- synthesize External.esp {EXL@0x800 -> [WA(donor)]}, master = Donor ---
        {
            using var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE);
            var e = new SkyrimMod(extKey, SkyrimRelease.SkyrimSE);
            var exl = new FormList(exlKey, SkyrimRelease.SkyrimSE) { EditorID = "HcExtList" };
            exl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(waOld));
            e.FormLists.Add(exl);
            e.ModHeader.Stats.NextFormID = 0x801;
            e.BeginWrite.ToPath(extPath).WithLoadOrder(new[] { donorOv }).NoNextFormIDProcessing().Write();
        }

        bool happyOk;
        string happyDetail;
        {
            // 1. collect Donor's originating records (doc order) + build the remap into the ESL window.
            RemapEngine.RemapPlan plan;
            string pPrimePath = Path.Combine(tmpDir, "pprime", donorKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(pPrimePath)!);
            RemapEngine.RenumberResult ren;
            using (var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE))
            {
                var srcKeys = donorOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == donorKey).Select(r => r.FormKey).ToList();
                plan = RemapEngine.BuildSequentialRemap(srcKeys, donorKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
                var pPrime = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
                ren = RemapEngine.RenumberRecordsInto(pPrime, donorOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == donorKey), plan.Dict);
                pPrime.ModHeader.Stats.NextFormID = 0x802;
                if (ren.Success) WriteEngine.WriteInPlace(pPrime, Array.Empty<ISkyrimModGetter>(), pPrimePath);
            }

            // 2. re-read P′: weapons at 0x800, formlists at 0x801, the FL's internal ref repointed to 0x800.
            FormKey waNew = plan.Success ? plan.Dict[waOld] : default;
            FormKey flNew = plan.Success ? plan.Dict[flOld] : default;
            List<FormKey> pWeap = new(), pList = new(); FormKey? pInternal = null;
            if (ren.Success)
            {
                using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
                pWeap = pp.Weapons.Select(w => w.FormKey).ToList();
                pList = pp.FormLists.Select(l => l.FormKey).ToList();
                pInternal = pp.FormLists.First().Items.FirstOrDefault()?.FormKey;
            }
            bool pPrimeOk = ren.Success && pWeap.SequenceEqual(new[] { waNew }) && pList.SequenceEqual(new[] { flNew }) && pInternal == waNew;

            // 3. identify-pass over the ORIGINAL order {Donor, External}: External references WA, Donor is in the set.
            bool identifyOk; bool repointOk; FormKey? extAfter = null;
            using (var resolver = LoadOrderResolver.Build(new[] { donorPath, extPath }))
            {
                var targets = plan.Success ? plan.Dict.Keys.ToHashSet() : new HashSet<FormKey>();
                var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { donorKey.FileName.String };
                var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);
                identifyOk = id.HasExternalReferencers
                             && id.ExternalPlugins.Contains(extKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                             && !id.ExternalPlugins.Contains(donorKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                             && id.Refs.Any(r => r.Target == waOld && string.Equals(r.Plugin, extKey.FileName.String, StringComparison.OrdinalIgnoreCase))
                             && id.UnscannableRecords == 0;

                // 4. opt-in in-place repoint of the external referencer; its ref to WA rewrites to the new key.
                var rep = RemapEngine.RepointInPlace(resolver, extKey.FileName.String, plan.Success ? plan.Dict : new Dictionary<FormKey, FormKey>());
                repointOk = rep.Success;
            }
            if (repointOk)
            {
                using var ee = SkyrimMod.CreateFromBinaryOverlay(extPath, SkyrimRelease.SkyrimSE);
                extAfter = ee.FormLists.First().Items.FirstOrDefault()?.FormKey;
            }
            bool extRepointOk = repointOk && extAfter == waNew;

            happyOk = plan.Success && ren.Success && pPrimeOk && identifyOk && extRepointOk;
            happyDetail = $"plan={plan.Success}(err:{plan.Error}), renumber={ren.Success}(copied {ren.RecordsCopied}, renum {ren.RecordsRenumbered}; err:{ren.Error}), " +
                          $"P′ weap=[{string.Join(",", pWeap)}] list=[{string.Join(",", pList)}] internal->{pInternal}, " +
                          $"identify={identifyOk}, external repointed {extAfter}->expect {waNew} = {extRepointOk}";
        }
        Console.WriteLine($"   HAPPY  full compact end-to-end : {(happyOk ? "PASS" : "FAIL")}");
        Console.WriteLine($"          {happyDetail}");

        // --- CAPACITY: overflow the window (3 distinct keys into a 2-ID window) → loud refusal. ---
        bool capacityOk;
        {
            var keys = new[] { new FormKey(donorKey, 1), new FormKey(donorKey, 2), new FormKey(donorKey, 3) };
            var plan = RemapEngine.BuildSequentialRemap(keys, donorKey, 0x800, 0x801);   // window holds 2
            capacityOk = !plan.Success && plan.Error is not null && plan.Error.Contains("overflow", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   CAPACITY window overflow refused: {(capacityOk ? "PASS" : "FAIL")}  ({(plan.Success ? "did NOT refuse" : plan.Error)})");
        }

        // --- NESTED: a Cell (nested-only family) has no flat group → RenumberRecordsInto refuses loud. ---
        bool nestedOk;
        {
            var cellKey = new FormKey(donorKey, 0xD00);
            var cell = new Cell(cellKey, SkyrimRelease.SkyrimSE) { EditorID = "HcCell" };
            var target = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            var dict = new Dictionary<FormKey, FormKey> { [cellKey] = new FormKey(donorKey, 0x800) };
            var ren = RemapEngine.RenumberRecordsInto(target, new IMajorRecordGetter[] { cell }, dict);
            nestedOk = !ren.Success && ren.Error is not null && ren.Error.Contains("NESTED", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"   NESTED  nested-only record refused: {(nestedOk ? "PASS" : "FAIL")}  ({(ren.Success ? "did NOT refuse" : ren.Error)})");
        }

        // --- ABSTRACT: a Global (abstract SkyrimGroup<Global>) renumbers + places via TryAddToFlatGroup's abstract-group
        //     arm — Global.IsInstanceOfType(GlobalFloat) matches and Add(Global) accepts the concrete arm. Globals/GMSTs
        //     are real compaction targets and this is the most novel reflection in the engine, so it gets its own arm. ---
        bool abstractOk;
        {
            var gOld = new FormKey(donorKey, 0xBBB);
            var gNew = new FormKey(donorKey, 0x800);
            var gf = new GlobalFloat(gOld, SkyrimRelease.SkyrimSE) { EditorID = "HcGlobalF", Data = 2.5f };
            var target = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            var ren = RemapEngine.RenumberRecordsInto(target, new IMajorRecordGetter[] { gf }, new Dictionary<FormKey, FormKey> { [gOld] = gNew });
            bool placed = target.Globals.ContainsKey(gNew) && !target.Globals.ContainsKey(gOld) && target.Globals.FirstOrDefault() is GlobalFloat;
            abstractOk = ren.Success && ren.RecordsRenumbered == 1 && placed;
            Console.WriteLine($"   ABSTRACT global placed via abstract-group arm: {(abstractOk ? "PASS" : "FAIL")}  (globals=[{string.Join(",", target.Globals.Select(g => g.FormKey))}], renum {ren.RecordsRenumbered}{(ren.Success ? "" : "; " + ren.Error)})");
        }

        // --- OVERRIDE: a compaction renumbers a plugin's ORIGINATING records but leaves an OVERRIDE at its master's key
        //     (the override isn't in the remap dict → RenumberRecordsInto copies it at its OWN key). Base.esp {WB@0x801};
        //     Over.esp overrides WB + originates WO@0xAAA; compacting Over must yield WO@0x800 AND WB@0x801:Base intact. ---
        bool overrideOk;
        {
            var baseKey = new ModKey("HcRemapBase", ModType.Master);
            var wbKey = new FormKey(baseKey, 0x801);
            string basePath = Path.Combine(tmpDir, baseKey.FileName.String);
            {
                var b = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
                b.Weapons.Add(new Weapon(wbKey, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapB", BasicStats = new WeaponBasicStats { Damage = 7 } });
                b.ModHeader.Stats.NextFormID = 0x802;
                b.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }
            var overKey = new ModKey("HcRemapOver", ModType.Plugin);
            var woOld = new FormKey(overKey, 0xAAA);
            string overPath = Path.Combine(tmpDir, overKey.FileName.String);
            using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE))
            {
                var o = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
                o.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == wbKey));
                o.Weapons.Add(new Weapon(woOld, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapO", BasicStats = new WeaponBasicStats { Damage = 9 } });
                o.ModHeader.Stats.NextFormID = 0xAAB;
                o.BeginWrite.ToPath(overPath).WithLoadOrder(new[] { baseOv }).NoNextFormIDProcessing().Write();
            }
            using (var overOv = SkyrimMod.CreateFromBinaryOverlay(overPath, SkyrimRelease.SkyrimSE))
            {
                var origKeys = overOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == overKey).Select(r => r.FormKey).ToList();
                var plan = RemapEngine.BuildSequentialRemap(origKeys, overKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
                var pPrime = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
                var ren = RemapEngine.RenumberRecordsInto(pPrime, overOv.EnumerateMajorRecords(), plan.Dict);
                bool woRenum = plan.Success && pPrime.Weapons.ContainsKey(plan.Dict[woOld]);   // originating WO -> 0x800:Over
                bool wbPreserved = pPrime.Weapons.ContainsKey(wbKey);                          // override WB stays at 0x801:Base
                overrideOk = ren.Success && ren.RecordsCopied == 2 && ren.RecordsRenumbered == 1 && woRenum && wbPreserved;
                Console.WriteLine($"   OVERRIDE override copied at master key: {(overrideOk ? "PASS" : "FAIL")}  (P′ weapons=[{string.Join(",", pPrime.Weapons.Select(w => w.FormKey))}], copied {ren.RecordsCopied}/renum {ren.RecordsRenumbered}{(ren.Success ? "" : "; " + ren.Error)})");
            }
        }

        // --- REFUSAL: RepointInPlace fails LOUD on bad input — a name not in the order, and an empty remap dict — with
        //     the file untouched (Q3). The opt-in rewrite's guardrails, pinned. ---
        bool refusalOk;
        {
            using var resolver = LoadOrderResolver.Build(new[] { donorPath, extPath });
            var someDict = new Dictionary<FormKey, FormKey> { [waOld] = new FormKey(donorKey, 0x800) };
            var notActive = RemapEngine.RepointInPlace(resolver, "HcDoesNotExist.esp", someDict);
            var emptyDict = RemapEngine.RepointInPlace(resolver, extKey.FileName.String, new Dictionary<FormKey, FormKey>());
            refusalOk = !notActive.Success && (notActive.Error?.Contains("not an active plugin", StringComparison.OrdinalIgnoreCase) ?? false)
                     && !emptyDict.Success && (emptyDict.Error?.Contains("no remap", StringComparison.OrdinalIgnoreCase) ?? false);
            Console.WriteLine($"   REFUSAL RepointInPlace loud on bad input  : {(refusalOk ? "PASS" : "FAIL")}  (not-active: {notActive.Error?.Split('.')[0]}; empty-dict: {emptyDict.Error})");
        }

        Console.WriteLine();
        bool pass = happyOk && capacityOk && nestedOk && abstractOk && overrideOk && refusalOk;
        Console.WriteLine($"=== remap-wave1-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, true); } catch { }
        return pass ? 0 : 1;
    }

    // ======================================================================
    //  REAL — manual --mo2 run: ESL-compact a real plugin to a NEW P′ for Aaron to xEdit-verify,
    //  and measure the identify-pass over the live order (plan §9 Wave 1 real-data gate).
    // ======================================================================

    /// <summary>
    /// MANUAL real-data run (needs <c>--mo2 &lt;instanceDir&gt; --plugin &lt;Name.esp&gt;</c>; SKIPs without). ESL-compacts a
    /// REAL plugin's originating records into the 0x800–0xFFF window, emits a NEW P′ (originals untouched — written to
    /// <c>--out</c> or a temp dir) for Aaron to load in xEdit, and runs + TIMES the identify-pass over the whole live
    /// order, REPORTING (not rewriting) any external referencers. Read-only on the load order except the P′ it writes
    /// into its own output dir. Refuses LOUD on the real boundaries (too many records for the light range; a nested-only
    /// record; an unparseable plugin) — the same honest limits the guard pins, now against real data.
    /// Run: dotnet run --project src/housecarl-generator remap-wave1-real -- --mo2 &lt;inst&gt; --plugin &lt;Name.esp&gt; [--out &lt;dir&gt;]
    /// </summary>
    public static int RunReal(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var instanceDir = f.GetValueOrDefault("mo2");
        var pluginName = f.GetValueOrDefault("plugin");
        if (instanceDir is null || !Directory.Exists(instanceDir) || string.IsNullOrWhiteSpace(pluginName))
        {
            Console.WriteLine("SKIP: needs --mo2 <instanceDir> --plugin <Name.esp>. A real ESL-compaction + identify-pass can only");
            Console.WriteLine("      be measured/verified against a real load order (a synthetic fixture is the guard's job).");
            return 0;
        }
        string outDir = f.GetValueOrDefault("out") ?? Path.Combine(Path.GetTempPath(), "hc-remap-wave1-real");
        Directory.CreateDirectory(outDir);

        Console.WriteLine("################  COMPACT/MERGE WAVE 1 — real ESL-compact + identify-pass  ################");
        Console.WriteLine($"   instance: {instanceDir}");
        Console.WriteLine($"   plugin  : {pluginName}");
        Console.WriteLine($"   out     : {outDir}");
        Console.WriteLine();

        var p = Mo2Instance.Resolve(instanceDir);
        var order = Mo2LoadOrder.Build(p.ProfileDir, p.ModsDir, p.DataDir, p.OverwriteDir);
        var orderedPaths = order.OrderedPaths.ToList();
        var srcPath = orderedPaths.FirstOrDefault(op => string.Equals(Path.GetFileName(op), pluginName, StringComparison.OrdinalIgnoreCase));
        if (srcPath is null) { Console.WriteLine($"ABORT: '{pluginName}' is not in the active order."); return 1; }

        var modKey = ModKey.FromFileName(pluginName);

        // Collect the plugin's ORIGINATING record keys (doc order) + build the ESL remap.
        List<FormKey> srcKeys;
        try
        {
            using var ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
            srcKeys = ov.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == modKey).Select(r => r.FormKey).ToList();
        }
        catch (Exception ex) { Console.WriteLine($"ABORT: cannot parse '{pluginName}' ({WriteEngine.Describe(ex)})."); return 1; }
        Console.WriteLine($"   originating records: {srcKeys.Count}  (ESL window capacity = {RemapEngine.EslCeiling - RemapEngine.EslFloor + 1})");

        var plan = RemapEngine.BuildSequentialRemap(srcKeys, modKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        if (!plan.Success) { Console.WriteLine($"   REFUSE (Q3): {plan.Error}"); /* still measure identify below */ }

        // Build P′ (compacted, light-flagged) and write it to the out dir — originals untouched.
        string pPrimePath = Path.Combine(outDir, pluginName);
        bool wroteP = false;
        if (plan.Success)
        {
            var masterOverlays = new List<IDisposable>();
            try
            {
                using var ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
                var pPrime = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
                // Wave 2: the STRUCTURAL renumber (flat + nested — cells/placed/INFO), so a cell-bearing real mod compacts
                // too (the flat RenumberRecordsInto would refuse it loud). Same path the housecarl_compact_plugin tool uses.
                var ren = RemapEngine.RenumberModInto(pPrime, ov, plan.Dict);
                if (!ren.Success) { Console.WriteLine($"   REFUSE (Q3): {ren.Error}"); }
                else
                {
                    pPrime.ModHeader.Stats.NextFormID = Math.Max(0x800u, (uint)plan.Dict.Count + 0x800u);
                    // resolve the plugin's own declared masters (in load order) for a faithful write.
                    var byName = orderedPaths.ToDictionary(Path.GetFileName, x => x, StringComparer.OrdinalIgnoreCase);
                    var resolved = new List<ISkyrimModGetter>();
                    bool missing = false;
                    foreach (var mr in ov.ModHeader.MasterReferences)
                    {
                        if (!byName.TryGetValue(mr.Master.FileName.String, out var mp)) { missing = true; Console.WriteLine($"   REFUSE: declared master '{mr.Master.FileName}' not in the order."); break; }
                        var mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE); masterOverlays.Add((IDisposable)mov); resolved.Add(mov);
                    }
                    if (!missing)
                    {
                        try { WriteEngine.WriteInPlace(pPrime, resolved, pPrimePath); wroteP = true; Console.WriteLine($"   WROTE compacted P′ → {pPrimePath}  ({ren.RecordsCopied} records, {ren.RecordsRenumbered} renumbered into 0x800+)"); }
                        catch (Exception ex) { Console.WriteLine($"   WRITE FAILED ({WriteEngine.Describe(ex)}) — note a sub-0x800 originating record is rejected by the light floor."); }
                    }
                }
            }
            finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { } } }
        }

        // Identify-pass over the WHOLE live order — time it (the ~25s number the plan composes from).
        Console.WriteLine();
        Console.WriteLine("   identify-pass (external referencers of the compacted records) over the whole order…");
        using (var resolver = LoadOrderResolver.Build(orderedPaths))
        {
            var targets = (plan.Success ? plan.Dict.Keys : (IEnumerable<FormKey>)srcKeys).ToHashSet();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pluginName };
            var sw = Stopwatch.StartNew();
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);
            sw.Stop();
            Console.WriteLine($"   scanned {id.PluginsScanned} plugins in {sw.Elapsed.TotalSeconds:N1}s; unscannable records: {id.UnscannableRecords}");
            Console.WriteLine($"   EXTERNAL referencers: {id.ExternalPlugins.Count} plugin(s){(id.ExternalPlugins.Count == 0 ? " — the clean default path (emit P′, originals untouched, done)." : ":")}");
            foreach (var pl in id.ExternalPlugins.Take(25)) Console.WriteLine($"      - {pl}  ({id.Refs.Count(r => string.Equals(r.Plugin, pl, StringComparison.OrdinalIgnoreCase))} ref(s))");
            if (id.UnscannableRecords > 0) foreach (var s in id.UnscannableSamples) Console.WriteLine($"      [unscannable] {s}");
            if (id.HasExternalReferencers)
                Console.WriteLine("   (these would each need the opt-in in-place repoint — REPORTED here, NOT rewritten, in this read-only run.)");
        }

        Console.WriteLine();
        Console.WriteLine(wroteP
            ? $"=== remap-wave1-real: DONE — load {pPrimePath} in xEdit to verify (FormIDs in 0x800–0xFFF, light flag, refs intact). ==="
            : "=== remap-wave1-real: DONE (P′ not written — see the refusal above) ===");
        return 0;
    }

    /// <summary>Build Donor.esp = {Weapon A@aOld, FormList L@lKey -> [A]}, apply <paramref name="renumber"/>, write,
    /// re-read, and print the on-disk truth: where A's identity landed + where L's entry points.</summary>
    static void RunRenumberExperiment(string tmpDir, string tag, ModKey donorKey, FormKey aOld, FormKey aNew, FormKey lKey,
        Action<SkyrimMod> renumber)
    {
        string path = Path.Combine(tmpDir, tag, donorKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var mod = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
            mod.Weapons.Add(new Weapon(aOld, SkyrimRelease.SkyrimSE) { EditorID = "HcRemapWeapA" });
            var flst = new FormList(lKey, SkyrimRelease.SkyrimSE) { EditorID = "HcRemapList" };
            flst.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(aOld));
            mod.FormLists.Add(flst);
            if (mod.ModHeader.Stats.NextFormID < 0x800) mod.ModHeader.Stats.NextFormID = 0x800;

            renumber(mod);

            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            using var rb = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var weapKeys = rb.Weapons.Select(w => w.FormKey).ToList();
            var listEntry = rb.FormLists.First().Items.FirstOrDefault()?.FormKey;
            bool identityMoved = weapKeys.Contains(aNew) && !weapKeys.Contains(aOld);
            bool linkMoved = listEntry == aNew;
            Console.WriteLine($"   [{tag}] weapon identities=[{string.Join(",", weapKeys)}]  list entry -> {listEntry}");
            Console.WriteLine($"   [{tag}] => identity moved to A_new? {identityMoved}   link moved to A_new? {linkMoved}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   [{tag}] THREW {ex.GetType().Name}: {ex.Message}");
        }
    }
}

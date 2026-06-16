using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for creating a CONCRETE SUBTYPE of an ABSTRACT record group (HCBR-2026-06-15-01
/// item 2.2 / PR-D), in the pattern of upsert-guard / nested-create-guard. Drives the REAL product paths
/// (WritePatchBuilder.CreateRecords / WriteEngine.GenericAddNew / GenericUpsertNew / ApplyVerb) against a synthesized
/// master in TEMP. The two abstract record groups are SkyrimGroup&lt;Global&gt; and SkyrimGroup&lt;GameSetting&gt;; a
/// Global is stored as a GlobalFloat/Int/Short, a GameSetting as a GameSettingBool/Float/Int/String — the bare base is
/// never instantiable. Mutagen's generic AddNew&lt;T&gt; cannot be closed with the abstract base (it throws), and a
/// SkyrimGroup&lt;T&gt; is NOT an IList, so the fix constructs the concrete arm + Add(T)s it via the group's own
/// instance method.  Run: dotnet run --project src/housecarl-generator -- create-abstract-group-guard
///
/// Arms (ALL required — a GREEN must mean "the contract holds", never "the scenario doesn't arise here"):
///   G1  GLOBALFLOAT  — create_record RecordType='GlobalFloat' succeeds: a GlobalFloat, FormKey id >= 0x800, master ==
///                      the patch itself (a local new record). RED before the fix (CanCreateType refused the arm:
///                      "no top-level group … subtype of an abstract group").
///   G2  GAMESETTING  — the SAME for RecordType='GameSettingFloat'. The BY-CONSTRUCTION generality proof: the fix is
///                      keyed off the runtime hierarchy (abstract group → its concrete arms), NOT a per-type GLOB
///                      special-case, so GMST — a DIFFERENT abstract group discovered the same way — must work
///                      identically off the same branch. RED before the fix for the same reason as G1.
///   G3  FIELD        — set Data=12.5 on the created GlobalFloat through the proven ApplyVerb path, read it back off
///                      the live record (create→edit reuses the write surface).
///   G4  ROUNDTRIP    — serialize the patch, re-open from disk: the GlobalFloat persists with its FormKey + EditorID +
///                      Data, as a GlobalFloat (the group.Add path is otherwise unproven through WritePatch).
///   G5  UPSERT       — re-run the same create with extend=true: the record is REPLACED in place (1 copy, stable
///                      FormKey, surfaced as ReplacedExisting), not appended — the abstract-group upsert path
///                      (InvokeRemove + re-construct + Add(T), not InvokeAddNewWithFormKey).
///   G6  BASEREFUSE   — create RecordType='Global' (the bare abstract base) is REFUSED loud, the message NAMES the
///                      concrete arms, NOTHING written. (Green today as a regression guard, not a fix-proof — it keeps
///                      the loud-fail boundary loud after the fix opened the concrete-arm path beside it.)
///   G7  READMAP      — the READ-SIDE base→arm mapping (LoadOrderService.BuildTypeLookup): a cross_plugin_query
///                      type='Global' over an order holding a GlobalFloat AND a GlobalInt resolves (no "unknown type")
///                      and returns BOTH — proving the abstract base NAME unions its concrete arms (the read-side twin
///                      of the create branch). The union spanning two DISTINCT arms is the observable form of the
///                      "4 arms" by-construction claim. type='GameSetting' returns its GameSettingFloat (generality).
///                      RED before the fix (BuildTypeLookup skipped polymorphic-base names → "unknown record type").
/// </summary>
public static class CreateGlobalProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — create concrete subtype of an abstract record group (GLOB / GMST, PR-D)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-create-abstract-group-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // --- Setup: an empty master + the validator corpus. The created records reference nothing external. ---
        var mKey = new ModKey("HcAbsGrpMaster", ModType.Master);
        string mPath = Path.Combine(tmpDir, mKey.FileName.String);
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var k = m.Keywords.AddNew(); k.EditorID = "HcAbsGrpMasterKw";   // a record so the master isn't empty-bytes
            m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        var rulebook = CorpusRulebook.Load(Path.Combine(genDir, "corpus.json"));
        Console.WriteLine($"-- setup: master {mKey.FileName}; corpus generated --");

        string pPath = Path.Combine(tmpDir, "HcAbsGrpGuard.esp");

        // --- G1 + G2 + G3 + G4: create a GlobalFloat (with a Data edit) and a GameSettingFloat in one call, serialize,
        //     re-read. The two arms in one spec set prove the create branch + the generality in one serialize pass. ---
        bool g1 = false, g2 = false, g3 = false, g4 = false;
        FormKey globFk = default, gmstFk = default;
        {
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "GlobalFloat", EditorId = "HcAbsGrpGlobal", Edits = new[]
                {
                    new WriteRequest { RecordType = "GlobalFloat", Path = new[] { "Data" }, Verb = "Set", Value = "12.5" },
                } },
                new WritePatchBuilder.CreateSpec { RecordType = "GameSettingFloat", EditorId = "fHcAbsGrpGmst", Edits = Array.Empty<WriteRequest>() },
            };

            bool createOk = false; string? createErr = null;
            string? globType = null, gmstType = null;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: false);
                createOk = o.Success; createErr = o.Error;
                if (o.Success)
                {
                    var glob = o.Created.FirstOrDefault(c => c.EditorId == "HcAbsGrpGlobal");
                    var gmst = o.Created.FirstOrDefault(c => c.EditorId == "fHcAbsGrpGmst");
                    if (glob is not null) globFk = glob.FormKey;
                    if (gmst is not null) gmstFk = gmst.FormKey;
                }
            }

            // Re-open from disk and confirm concrete type + fields + master.
            float? globData = null; string? globEdid = null, gmstEdid = null;
            bool globLocal = false, gmstLocal = false;
            if (createOk)
            {
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE);
                    var g = ov.Globals.FirstOrDefault(x => x.FormKey == globFk);
                    if (g is not null)
                    {
                        // The overlay reader returns concrete-arm subclasses (GlobalFloatBinaryOverlay); the load-bearing
                        // assertion is the getter-interface (the arm's identity), not the class name.
                        globType = g is IGlobalFloatGetter ? "GlobalFloat" : g.GetType().Name;
                        globEdid = g.EditorID;
                        globData = (g as IGlobalFloatGetter)?.Data;
                        globLocal = g.FormKey.ModKey == ov.ModKey;
                    }
                    var s = ov.GameSettings.FirstOrDefault(x => x.FormKey == gmstFk);
                    if (s is not null)
                    {
                        gmstType = s is IGameSettingFloatGetter ? "GameSettingFloat" : s.GetType().Name;
                        gmstEdid = s.EditorID;
                        gmstLocal = s.FormKey.ModKey == ov.ModKey;
                    }
                }
                finally { (ov as IDisposable)?.Dispose(); }
            }

            g1 = createOk && globType == "GlobalFloat" && globFk.ID >= 0x800 && globLocal;
            g2 = createOk && gmstType == "GameSettingFloat" && gmstFk.ID >= 0x800 && gmstLocal;
            g3 = globData.HasValue && Math.Abs(globData.Value - 12.5f) < 0.001f;
            g4 = createOk && globType == "GlobalFloat" && globEdid == "HcAbsGrpGlobal"
                 && gmstType == "GameSettingFloat" && gmstEdid is not null;   // GMST editorid is engine-prefixed ('f…')

            Console.WriteLine($"   G1 create GlobalFloat (arm of abstract Global)     : {(g1 ? $"PASS — {globType} {globFk} (id 0x{globFk.ID:X6}, local)" : $"FAIL — ok={createOk} type={globType} id=0x{globFk.ID:X6} local={globLocal} err=[{createErr}]")}");
            Console.WriteLine($"   G2 create GameSettingFloat (generality, NOT GLOB)  : {(g2 ? $"PASS — {gmstType} {gmstFk} (id 0x{gmstFk.ID:X6}, local)" : $"FAIL — ok={createOk} type={gmstType} id=0x{gmstFk.ID:X6} local={gmstLocal} err=[{createErr}]")}");
            Console.WriteLine($"   G3 set Data=12.5 via ApplyVerb, read back          : {(g3 ? $"PASS — Data={globData}" : $"FAIL — Data={globData}")}");
            Console.WriteLine($"   G4 serialize + re-read round-trip (FormKey/edid)   : {(g4 ? $"PASS — GlobalFloat '{globEdid}', GameSettingFloat '{gmstEdid}'" : $"FAIL — globType={globType} globEdid={globEdid} gmstType={gmstType} gmstEdid={gmstEdid}")}");
        }

        // --- G5: re-run the SAME create with extend=true → REPLACE in place (1 copy, stable FormKey, surfaced). ---
        bool g5 = false;
        {
            bool rerunOk = false, flaggedReplaced = false; FormKey globFk2 = default;
            var specs = new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "GlobalFloat", EditorId = "HcAbsGrpGlobal", Edits = new[]
                {
                    new WriteRequest { RecordType = "GlobalFloat", Path = new[] { "Data" }, Verb = "Set", Value = "99.0" },
                } },
            };
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var o = WritePatchBuilder.CreateRecords(r, rulebook, specs, pPath, extend: true);
                rerunOk = o.Success;
                if (o.Success)
                {
                    var glob = o.Created.FirstOrDefault(c => c.EditorId == "HcAbsGrpGlobal");
                    if (glob is not null) { globFk2 = glob.FormKey; flaggedReplaced = glob.ReplacedExisting; }
                }
            }
            int globCount = 0; float? data2 = null;
            if (rerunOk)
            {
                ISkyrimModGetter? ov = null;
                try
                {
                    ov = SkyrimMod.CreateFromBinaryOverlay(pPath, SkyrimRelease.SkyrimSE);
                    foreach (var g in ov.Globals) if (g.EditorID == "HcAbsGrpGlobal") { globCount++; data2 = (g as IGlobalFloatGetter)?.Data; }
                }
                finally { (ov as IDisposable)?.Dispose(); }
            }
            g5 = rerunOk && flaggedReplaced && globCount == 1 && globFk2 == globFk
                 && data2.HasValue && Math.Abs(data2.Value - 99.0f) < 0.001f;
            Console.WriteLine($"   G5 re-run replaces in place (stable, surfaced)     : {(g5 ? $"PASS — 1 copy, FormKey {globFk2} stable, REPLACED flagged, Data={data2}" : $"FAIL — ok={rerunOk} flagged={flaggedReplaced} count={globCount} stable={globFk2 == globFk} data={data2}")}");
        }

        // --- G6: the bare abstract base 'Global' refuses loud naming the arms; nothing written. ---
        bool g6 = false;
        {
            string basePath = Path.Combine(tmpDir, "HcAbsGrpBaseRefuse.esp");
            bool refused; string? error;
            using (var r = LoadOrderResolver.Build(new[] { mPath }))
            {
                var spec = new[] { new WritePatchBuilder.CreateSpec { RecordType = "Global", EditorId = "HcAbsGrpBase", Edits = Array.Empty<WriteRequest>() } };
                var o = WritePatchBuilder.CreateRecords(r, rulebook, spec, basePath, extend: false);
                refused = !o.Success; error = o.Error;
            }
            bool namesArms = error is not null
                && error.Contains("GlobalFloat", StringComparison.OrdinalIgnoreCase)
                && error.Contains("GlobalInt", StringComparison.OrdinalIgnoreCase)
                && error.Contains("GlobalShort", StringComparison.OrdinalIgnoreCase);
            bool noFile = !File.Exists(basePath);
            g6 = refused && namesArms && noFile;
            Console.WriteLine($"   G6 bare abstract base 'Global' refused, arms named : {(g6 ? "PASS — refused loud, arms named, no file" : $"FAIL — refused={refused} namesArms={namesArms} noFile={noFile} error=[{error}]")}");
        }

        // --- G7: the READ-SIDE base→arm mapping (LoadOrderService.BuildTypeLookup, driven through the real CrossQuery).
        //     A synthetic MO2 instance (the bulk-create-guard synth pattern) holding a master with a GlobalFloat, a
        //     GlobalInt, and a GameSettingFloat. type='Global' must RESOLVE (not "unknown record type") and return BOTH
        //     globals — proving the abstract base NAME unions its concrete arms (two distinct arms = the observable form
        //     of the "4 arms" claim); type='GameSetting' returns its GameSettingFloat (the generality, second group). ---
        bool g7 = false;
        {
            var g7Root = Path.Combine(tmpDir, "readmap");
            string instance = Path.Combine(g7Root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(g7Root, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(g7Root, "game").Replace(@"\", @"\\") + ")\r\n");

            var mapKey = new ModKey("HcAbsGrpReadMap", ModType.Master);
            var modDir = Path.Combine(mods, "ReadMapMod");
            Directory.CreateDirectory(modDir);
            FormKey globFloatFk, globIntFk, gmstFloatFk;
            {
                // Build the fixture records through the product's own abstract-group create (the only path that can
                // populate these groups — the bare Mutagen AddNew<T> extension can't, which is the whole reason for PR-D).
                var m = new SkyrimMod(mapKey, SkyrimRelease.SkyrimSE);
                globFloatFk = WriteEngine.GenericAddNew(m, "GlobalFloat", "HcRmGlobalFloat").FormKey;
                globIntFk = WriteEngine.GenericAddNew(m, "GlobalInt", "HcRmGlobalInt").FormKey;
                gmstFloatFk = WriteEngine.GenericAddNew(m, "GameSettingFloat", "fHcRmGmstFloat").FormKey;
                m.BeginWrite.ToPath(Path.Combine(modDir, mapKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mapKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mapKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+ReadMapMod\r\n");

            // BuildTypeLookup reads the corpus via CorpusRulebook.CorpusPath (the same one the rulebook loaded from).
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");
            var store = new UserConfigStore(Path.Combine(g7Root, "houseCARL.user.json"));

            bool globResolved = false, globBothArms = false, gmstResolved = false, gmstArm = false;
            string? globErr = null, gmstErr = null;
            try
            {
                using var svc = LoadOrderService.WithInstance(instance, 0, store);
                svc.Stats();   // warm the lazy index once

                var glob = svc.CrossQuery("Global", null, null, false, null, null, 50);
                globErr = glob.Error;
                globResolved = glob.Error is null;
                if (globResolved)
                {
                    var keys = glob.Keys.ToHashSet();
                    // BOTH distinct arms surface under the single base-name query (the union spans >1 arm).
                    globBothArms = keys.Contains(globFloatFk) && keys.Contains(globIntFk);
                }

                var gmst = svc.CrossQuery("GameSetting", null, null, false, null, null, 50);
                gmstErr = gmst.Error;
                gmstResolved = gmst.Error is null;
                if (gmstResolved) gmstArm = gmst.Keys.Contains(gmstFloatFk);
            }
            catch (Exception ex) { globErr ??= $"{ex.GetType().Name}: {ex.Message}"; }

            g7 = globResolved && globBothArms && gmstResolved && gmstArm;
            Console.WriteLine($"   G7 read-side base→arm mapping (cross_plugin_query): {(g7 ? "PASS — type='Global' unions GlobalFloat+GlobalInt; type='GameSetting' returns its arm" : $"FAIL — globResolved={globResolved} bothArms={globBothArms} gmstResolved={gmstResolved} gmstArm={gmstArm} globErr=[{globErr}] gmstErr=[{gmstErr}]")}");
        }

        Console.WriteLine();
        bool pass = g1 && g2 && g3 && g4 && g5 && g6 && g7;
        Console.WriteLine($"=== create-abstract-group-guard: {(pass ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }
}

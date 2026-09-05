using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for bulk-primitives WAVE 3 — the WRITE BATCH + DIFF
/// surface (PLAN.md P8a/P8b/P8c):
///   • P8a <c>composes=</c> — a LIST of build-from-parts elements in ONE op: verb=Add APPENDS each, verb=ReplaceAll
///     CLEARS then appends each (the modeled-list replace the singular compose block defers). All-or-nothing with
///     per-element (composes[i]) reasons; list-of-modeled-elements only (refusals for dict/scalar/coercible/wrong-verb).
///   • P8b <c>CopyFrom</c> — (added in its commit) reflection-generic field transplant from another plugin's version.
///   • P8c <c>housecarl_diff_record</c> — (added in its commit) pairwise field diff between two versions of a record.
///
/// Drives the REAL end-to-end tool path — a synthetic MO2 instance in temp (the ExtendResolveProbe/ReadPluginFileProbe
/// pattern) + <see cref="LoadOrderService"/> — so the wire mapping, corpus pre-flight, and apply engine are all exercised
/// together, exactly as a caller hits them. Self-contained: a corpus is generated in-process if none is configured.
///
/// Run: <c>dotnet run --project src/housecarl-generator bulk-primitives-wave3-guard</c>
/// </summary>
public static class BulkPrimitivesWave3Probe
{
    static int _pass, _fail;
    static void Check(string label, bool ok)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (ok) _pass++; else _fail++;
    }

    // The CountOf helper this file carried went with B12/B13's rendered-banner arms (#486): the counting it existed
    // for now runs in RecordsOffOrderPathTests against the live off-order label. RecordsTestBase has its own.

    [CiProbe("bulk-primitives-wave3-guard")]
    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — bulk-primitives Wave 3 (write batch + diff: composes / CopyFrom / diff_record)  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_bulk_primitives_wave3_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ComposesArm(Path.Combine(root, "p8a"));   // P8a
            CopyFromArm(Path.Combine(root, "p8b"));    // P8b (active-order + off-order source)
            DiffArm(Path.Combine(root, "p8c"));        // P8c

            Console.WriteLine();
            Console.WriteLine($"=== bulk-primitives-wave3-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ================= P8a — composes= (batch struct-list Add append / ReplaceAll clear+append) =================
    static void ComposesArm(string dir)
    {
        Console.WriteLine("── P8a: composes= — Add appends each, ReplaceAll clears+appends each; all-or-nothing + refusals ──");

        // ---- synthetic MO2 instance with one master (LeveledItem with an empty Entries list + a weapon to reference) ----
        string instance = Path.Combine(dir, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcW3Master", ModType.Master);
        var masterPath = Path.Combine(mods, "MasterMod", mKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
        FormKey llFk, weapFk, kwFk;
        {
            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var kw = m.Keywords.AddNew(); kw.EditorID = "HcW3Kw"; kwFk = kw.FormKey;
            var w = m.Weapons.AddNew(); w.EditorID = "HcW3Weap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kwFk) };
            weapFk = w.FormKey;
            var ll = m.LeveledItems.AddNew(); ll.EditorID = "HcW3LL";   // empty Entries — the Add arm materializes + appends
            llFk = ll.FormKey;
            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

        var genDir = Path.Combine(dir, "corpus-gen");
        try { _ = CorpusRulebook.LoadCorpus(); }
        catch
        {
            CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");
        }

        var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
        using var svc = LoadOrderService.WithInstance(instance, 0, store);
        svc.Stats();   // warm the lazy index once, off the clock

        string llFid = $"{llFk.ID:X6}:{llFk.ModKey.FileName}";
        string weapFid = $"{weapFk.ID:X6}:{weapFk.ModKey.FileName}";

        // one leveled-list entry spec (the plan's canonical composable element)
        StructInput Entry(int level) => new()
        {
            Type = "LeveledItemEntry",
            Sets = new[]
            {
                new NestedSet { Path = "Data.Level", Value = level.ToString() },
                new NestedSet { Path = "Data.Count", Value = "1" },
                new NestedSet { Path = "Data.Reference", Value = weapFid },
            },
        };

        // count Entries off a written patch (reflection-light; the append-vs-clear distinction is the load-bearing check)
        int? CountEntries(string espPath)
        {
            ISkyrimModGetter? ov = null;
            try
            {
                ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
                var ll = ov.LeveledItems.FirstOrDefault(x => x.FormKey == llFk);
                return ll?.Entries?.Count ?? 0;
            }
            catch { return null; }
            finally { (ov as IDisposable)?.Dispose(); }
        }

        // ---- ADD composes: append 3 onto the (empty) master LL ----
        var add = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add",
                         Composes = new[] { Entry(1), Entry(2), Entry(3) } },
        }, "P8aAdd", null);
        Check("Add composes: whole call succeeds", add.Success);
        if (add.Success)
            Check($"Add composes: appended 3 entries (count={CountEntries(add.OutputPath)})", CountEntries(add.OutputPath) == 3);

        // ---- ReplaceAll composes: seed 3 (into a patch), then ReplaceAll 2 into the SAME patch → clear proves count==2 (not 5) ----
        var seed = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add",
                         Composes = new[] { Entry(1), Entry(2), Entry(3) } },
        }, "P8aRepl", null);
        Check($"ReplaceAll setup: seed patch carries 3 (count={(seed.Success ? CountEntries(seed.OutputPath) : null)})",
              seed.Success && CountEntries(seed.OutputPath) == 3);
        var repl = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "ReplaceAll",
                         Composes = new[] { Entry(5), Entry(6) } },
        }, null, "P8aRepl");
        Check("ReplaceAll composes: whole call succeeds", repl.Success);
        if (repl.Success)
            Check($"ReplaceAll composes: CLEARED the 3 seeds then appended 2 → count==2 (count={CountEntries(repl.OutputPath)})",
                  CountEntries(repl.OutputPath) == 2);

        // PR #186 review #3: ReplaceAll composes=[] (empty) CLEARS the modeled list — the modeled twin of ReplaceAll
        // values=[] (which already clears a coercible list). Seed 3, then ReplaceAll to empty → count 0.
        var seedC = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add", Composes = new[] { Entry(1), Entry(2), Entry(3) } },
        }, "P8aClr", null);
        var clr = seedC.Success
            ? svc.ApplyEdits(new[] { new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "ReplaceAll", Composes = Array.Empty<StructInput>() } }, null, "P8aClr")
            : seedC;
        Check($"ReplaceAll composes=[] CLEARS the modeled list → count 0 (count={(clr.Success ? CountEntries(clr.OutputPath) : null)})",
              clr.Success && CountEntries(clr.OutputPath) == 0);
        // but Add composes=[] (empty) is still refused — appending nothing is a caller mistake
        var addEmpty = svc.ApplyEdits(new[] { new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add", Composes = Array.Empty<StructInput>() } }, "P8aAddEmpty", null);
        Check("Add composes=[] (empty) still refused (only ReplaceAll clears)",
              !addEmpty.Success && addEmpty.Error is { } eAE && eAE.Contains("ReplaceAll"));

        // ---- all-or-nothing: one bad element refuses the WHOLE call, names composes[1], writes nothing ----
        var bad = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add",
                         Composes = new[] { Entry(1), new StructInput { Type = "NotARealElementType" } } },
        }, "P8aBad", null);
        Check("all-or-nothing: a bad composes element refuses the whole call (names composes[1], nothing written)",
              !bad.Success && bad.Error is { } eBad && eBad.Contains("composes[1]") && string.IsNullOrEmpty(bad.OutputPath));

        // ---- mutual exclusion: compose= AND composes= both set ----
        var both = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add", Compose = Entry(1), Composes = new[] { Entry(2) } },
        }, "P8aBoth", null);
        Check("mutual exclusion: compose= AND composes= → refused ('not both')",
              !both.Success && both.Error is { } eBoth && eBoth.Contains("not both"));

        // ---- empty composes=[] is a named mistake, not a silent no-op ----
        var empty = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "Add", Composes = Array.Empty<StructInput>() },
        }, "P8aEmpty", null);
        Check("empty composes=[] → refused ('empty')",
              !empty.Success && empty.Error is { } eEmpty && eEmpty.Contains("empty"));

        // ---- coercible/formlink list (Weapon.Keywords) + composes → refused, points at values=/value= ----
        var coer = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = weapFid, FieldPath = "Keywords", Verb = "Add", Composes = new[] { new StructInput { Type = "Keyword" } } },
        }, "P8aCoer", null);
        Check("formlink-list Keywords + composes → refused (use values=/value=)",
              !coer.Success && coer.Error is { } eCoer && eCoer.Contains("formlink") && eCoer.Contains("values="));

        // ---- non-list leaf (a scalar) + composes → refused ('builds a LIST') ----
        var scal = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = weapFid, FieldPath = "BasicStats.Damage", Verb = "Add", Composes = new[] { Entry(1) } },
        }, "P8aScal", null);
        Check("non-list BasicStats.Damage + composes → refused ('builds a LIST')",
              !scal.Success && scal.Error is { } eScal && eScal.Contains("builds a LIST"));

        // ---- wrong verb (SetAtIndex) + composes → refused (Add or ReplaceAll) ----
        var wrongVerb = svc.ApplyEdits(new[]
        {
            new BulkOp { Formid = llFid, FieldPath = "Entries", Verb = "SetAtIndex", Key = "0", Composes = new[] { Entry(1) } },
        }, "P8aVerb", null);
        Check("wrong verb SetAtIndex + composes → refused (Add or ReplaceAll)",
              !wrongVerb.Success && wrongVerb.Error is { } eVerb && eVerb.Contains("Add") && eVerb.Contains("ReplaceAll"));
    }

    // ================= P8b — CopyFrom (reflection-generic field transplant from another plugin's version) =================
    static void CopyFromArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("── P8b: CopyFrom — deep-copy a field from another plugin's version (copy-then-readback across kinds) + refusals ──");

        // Master defines a weapon W (populated) + a REPLACER overrides W with DIFFERENT values (so the REPLACER wins).
        // CopyFrom from_plugin=MASTER reverts a field on the patch to the master's value → the readback proves the copy
        // took the SOURCE plugin's value (not the winner's). Also W2 (master-only) + W3 (no BasicStats) for refusals.
        string instance = Path.Combine(dir, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcW3CfMaster", ModType.Master);
        var rKey = new ModKey("HcW3CfRepl", ModType.Plugin);
        var masterPath = Path.Combine(mods, "CfMaster", mKey.FileName.String);
        var replPath = Path.Combine(mods, "CfRepl", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(replPath)!);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var k1 = m.Keywords.AddNew(); k1.EditorID = "CfKw1"; var kw1 = k1.FormKey;
        var k2 = m.Keywords.AddNew(); k2.EditorID = "CfKw2"; var kw2 = k2.FormKey;
        var w = m.Weapons.AddNew(); w.EditorID = "CfW";
        w.Name = "Base Sword";
        w.BasicStats = new WeaponBasicStats { Damage = 10 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kw1), new FormLink<IKeywordGetter>(kw2) };
        var wFk = w.FormKey;
        var w2 = m.Weapons.AddNew(); w2.EditorID = "CfW2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 }; var w2Fk = w2.FormKey;  // master-only
        w.Template.SetTo(w2Fk);   // a single get-only FormLink to exercise the SetTo transplant path (winner clears it)
        var w3 = m.Weapons.AddNew(); w3.EditorID = "CfW3"; w3.Name = "No Stats"; var w3Fk = w3.FormKey;                                  // NO BasicStats
        var mg = m.MagicEffects.AddNew(); mg.EditorID = "CfMgef"; var mgFk = mg.FormKey;
        var pot = m.Ingestibles.AddNew(); pot.EditorID = "CfPotion";     // a modeled-list field (Effects) for the element-DeepCopy arm
        var seedEff = new Effect { Data = new EffectData { Magnitude = 5 } };
        seedEff.BaseEffect.SetTo(mgFk);
        pot.Effects.Add(seedEff);
        var potFk = pot.FormKey;
        m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, w);
        rw.Name = "Winner Sword";
        rw.BasicStats = new WeaponBasicStats { Damage = 99 };
        rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();   // winner CLEARS the keywords
        rw.Template.SetTo(FormKey.Null);                                            // winner CLEARS the Template link
        ((IIngestible)WriteEngine.GenericGetOrAddAsOverride(r, pot)).Effects.Clear();  // winner CLEARS the effects
        r.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // an OFF-ORDER donor: overrides W with Damage=77, on disk in a DISABLED mod folder (NOT in the active order).
        var dKey = new ModKey("DonorOld", ModType.Plugin);
        var donorPath = Path.Combine(mods, "DonorOld", dKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(donorPath)!);
        var dmod = new SkyrimMod(dKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(dmod, w)).BasicStats = new WeaponBasicStats { Damage = 77 };
        dmod.BeginWrite.ToPath(donorPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // master loads FIRST, replacer LAST → the replacer wins W's record. master+replacer enabled; DonorOld DISABLED (off-order).
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+CfRepl\r\n+CfMaster\r\n-DonorOld\r\n");

        var genDir = Path.Combine(dir, "corpus-gen");
        try { _ = CorpusRulebook.LoadCorpus(); }
        catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

        var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
        using var svc = LoadOrderService.WithInstance(instance, 0, store);
        svc.Stats();

        string wFid = $"{wFk.ID:X6}:{wFk.ModKey.FileName}";
        string w2Fid = $"{w2Fk.ID:X6}:{w2Fk.ModKey.FileName}";
        string w3Fid = $"{w3Fk.ID:X6}:{w3Fk.ModKey.FileName}";
        string masterName = mKey.FileName.String;
        string replName = rKey.FileName.String;

        (ushort? dmg, string? name, int? kwCount) ReadW(string espPath, FormKey fk)
        {
            ISkyrimModGetter? ov = null;
            try
            {
                ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
                var wr = ov.Weapons.FirstOrDefault(x => x.FormKey == fk);
                return (wr?.BasicStats?.Damage, wr?.Name?.String, wr?.Keywords?.Count);
            }
            catch { return (null, null, null); }
            finally { (ov as IDisposable)?.Dispose(); }
        }

        BulkOp Copy(string path) => new() { Formid = wFid, FieldPath = path, Verb = "CopyFrom", FromPlugin = masterName };

        // sanity: the REPLACER wins W (Damage 99) — so a copy from the master genuinely changes the value
        var winner = svc.ResolveRefs(new[] { wFid });
        Check($"fixture: the replacer WINS W (winner={winner[0].Winner})", winner[0].Winner == replName);

        // scalar-in-substruct: BasicStats.Damage  (winner 99 → master 10)
        var d = svc.ApplyEdits(new[] { Copy("BasicStats.Damage") }, "CfDmg", null);
        Check($"CopyFrom scalar BasicStats.Damage: winner 99 → source 10 (got {(d.Success ? ReadW(d.OutputPath, wFk).dmg : null)})",
              d.Success && ReadW(d.OutputPath, wFk).dmg == 10);

        // whole loqui sub-struct: BasicStats  (DeepCopy)
        var bs = svc.ApplyEdits(new[] { Copy("BasicStats") }, "CfBs", null);
        Check($"CopyFrom sub-struct BasicStats (whole): Damage 10 (got {(bs.Success ? ReadW(bs.OutputPath, wFk).dmg : null)})",
              bs.Success && ReadW(bs.OutputPath, wFk).dmg == 10);

        // TranslatedString: Name  (winner "Winner Sword" → master "Base Sword")
        var nm = svc.ApplyEdits(new[] { Copy("Name") }, "CfName", null);
        Check($"CopyFrom TranslatedString Name: → \"Base Sword\" (got \"{(nm.Success ? ReadW(nm.OutputPath, wFk).name : null)}\")",
              nm.Success && ReadW(nm.OutputPath, wFk).name == "Base Sword");

        // formlink list: Keywords  (winner [] → master [kw1,kw2])  — BuildCopiedList
        var kwc = svc.ApplyEdits(new[] { Copy("Keywords") }, "CfKw", null);
        Check($"CopyFrom formlink-list Keywords: winner 0 → source 2 (got {(kwc.Success ? ReadW(kwc.OutputPath, wFk).kwCount : null)})",
              kwc.Success && ReadW(kwc.OutputPath, wFk).kwCount == 2);

        // modeled list (per-element DeepCopy): Ingestible.Effects (winner 0 → master 1) — the "copy Perks/Effects" headline
        string potFid = $"{potFk.ID:X6}:{potFk.ModKey.FileName}";
        int? EffCount(string espPath)
        {
            ISkyrimModGetter? ov = null;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE); return ov.Ingestibles.FirstOrDefault(x => x.FormKey == potFk)?.Effects?.Count; }
            catch { return null; }
            finally { (ov as IDisposable)?.Dispose(); }
        }
        var effc = svc.ApplyEdits(new[] { new BulkOp { Formid = potFid, FieldPath = "Effects", Verb = "CopyFrom", FromPlugin = masterName } }, "CfEff", null);
        Check($"CopyFrom modeled-list Effects (element DeepCopy): winner 0 → source 1 (got {(effc.Success ? EffCount(effc.OutputPath) : null)})",
              effc.Success && EffCount(effc.OutputPath) == 1);

        // single get-only FormLink (the SetTo path): Template (winner null → master → w2)
        FormKey? ReadTemplate(string espPath)
        {
            ISkyrimModGetter? ov = null;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE); return ov.Weapons.FirstOrDefault(x => x.FormKey == wFk)?.Template.FormKey; }
            catch { return null; }
            finally { (ov as IDisposable)?.Dispose(); }
        }
        var tmpl = svc.ApplyEdits(new[] { Copy("Template") }, "CfTmpl", null);
        Check($"CopyFrom single formlink Template (SetTo): winner null → source w2 (got {(tmpl.Success ? ReadTemplate(tmpl.OutputPath) : null)})",
              tmpl.Success && ReadTemplate(tmpl.OutputPath) == w2Fk);

        // OFF-ORDER source: copy from a DISABLED plugin on disk (not in the active order) — the "copy from the OLD patch" headline
        var offOrder = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = "DonorOld.esp" } }, "CfOff", null);
        Check($"CopyFrom OFF-ORDER source (disabled DonorOld.esp): winner 99 → off-order 77 (got {(offOrder.Success ? ReadW(offOrder.OutputPath, wFk).dmg : null)})",
              offOrder.Success && ReadW(offOrder.OutputPath, wFk).dmg == 77);

        // ---- refusals ----
        var noFrom = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom" } }, "CfNoFrom", null);
        Check("refusal: CopyFrom without the source pole → refused ('requires from_source')",
              !noFrom.Success && noFrom.Error is { } e1 && e1.Contains("requires from_source"));

        var strayFrom = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "5", FromPlugin = masterName } }, "CfStray", null);
        Check("refusal: the source pole on a non-CopyFrom op → refused ('only valid with op=CopyFrom')",
              !strayFrom.Success && strayFrom.Error is { } e2 && e2.Contains("only valid with op=CopyFrom"));

        // PR #186 review #2: the mapper is case-SENSITIVE like the engine — a mis-cased 'copyfrom' is NOT CopyFrom, so
        // with from_plugin set it fails loud at the mapper (not opaquely at pre-flight with a stray off-order source).
        var miscased = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "copyfrom", FromPlugin = masterName } }, "CfCase", null);
        Check("refusal: mis-cased op 'copyfrom' + the source pole → refused at the mapper ('only valid with op=CopyFrom')",
              !miscased.Success && miscased.Error is { } eCase && eCase.Contains("only valid with op=CopyFrom"));

        var withVal = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = masterName, Value = "5" } }, "CfVal", null);
        Check("refusal: CopyFrom + value → refused ('takes no value')",
              !withVal.Success && withVal.Error is { } e3 && e3.Contains("takes no value"));

        var notInOrder = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = "Nope.esp" } }, "CfNope", null);
        Check("refusal: from_plugin not in the load order → refused ('not in the load order')",
              !notInOrder.Success && notInOrder.Error is { } e4 && e4.Contains("not in the load order"));

        var noDefine = svc.ApplyEdits(new[] { new BulkOp { Formid = w2Fid, FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = replName } }, "CfNoDef", null);
        Check("refusal: from_plugin doesn't define/override the record → refused ('does NOT define or override')",
              !noDefine.Success && noDefine.Error is { } e5 && e5.Contains("does NOT define or override"));

        var absentField = svc.ApplyEdits(new[] { new BulkOp { Formid = w3Fid, FieldPath = "BasicStats", Verb = "CopyFrom", FromPlugin = masterName } }, "CfAbsent", null);
        Check("refusal: source field unset (W3 has no BasicStats) → refused ('nothing to copy')",
              !absentField.Success && absentField.Error is { } e6 && e6.Contains("nothing to copy"));

        // owned-child record collection is refused at PRE-FLIGHT (rulebook), by name — via CorpusRulebook.Validate directly
        var rulebook = CorpusRulebook.Load();
        var ownedReject = rulebook.Validate(new WriteRequest { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "CopyFrom" });
        Check($"refusal: owned-child collection Cell.Persistent + CopyFrom → refused by name (\"{ownedReject}\")",
              ownedReject is { } orr && orr.Contains("owned child records"));

        // create-context: CopyFrom isn't valid when creating a record
        var createCopy = svc.CreateOne("Weapon", "CfCreated",
            new[] { new BulkOp { FieldPath = "BasicStats.Damage", Verb = "CopyFrom", FromPlugin = masterName } },
            "CfCreate", null);
        Check("refusal: CopyFrom in a CREATE op → refused (isn't valid when creating)",
              !createCopy.Success && createCopy.Error is { } e7 && e7.Contains("isn't valid when CREATING"));
    }

    // ================= P8c — housecarl_diff_record (pairwise field diff, active + off-order poles) =================
    static void DiffArm(string dir)
    {
        Console.WriteLine();
        Console.WriteLine("── P8c: housecarl_diff_record — pairwise field diff (active + off-order poles) + refusals ──");

        string instance = Path.Combine(dir, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
        Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcW3DiffMaster", ModType.Master);
        var rKey = new ModKey("HcW3DiffRepl", ModType.Plugin);
        var masterPath = Path.Combine(mods, "DiffMaster", mKey.FileName.String);
        var replPath = Path.Combine(mods, "DiffRepl", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(replPath)!);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var k1 = m.Keywords.AddNew(); k1.EditorID = "DfKw1"; var dkw1 = k1.FormKey;
        var k2 = m.Keywords.AddNew(); k2.EditorID = "DfKw2"; var dkw2 = k2.FormKey;
        var w = m.Weapons.AddNew(); w.EditorID = "DfW"; w.Name = "Base"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(dkw1), new FormLink<IKeywordGetter>(dkw2) };
        var wFk = w.FormKey;
        var w2 = m.Weapons.AddNew(); w2.EditorID = "DfW2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };  // master-only
        m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, w);
        rw.Name = "Winner"; rw.BasicStats = new WeaponBasicStats { Damage = 99 };
        rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(dkw1) };   // dropped kw2
        r.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // OFF-ORDER donor: overrides W with Damage=77, on disk in a DISABLED mod folder (not in the active order).
        var dKey = new ModKey("DiffDonor", ModType.Plugin);
        var donorPath = Path.Combine(mods, "DiffDonor", dKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(donorPath)!);
        var dmod = new SkyrimMod(dKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(dmod, w)).BasicStats = new WeaponBasicStats { Damage = 77 };
        dmod.BeginWrite.ToPath(donorPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // SHADOWED copy: the same filename in a LOWER-priority ENABLED mod (66). Its folder is enabled, but a
        // higher-priority folder provides the name, so the game never loads THIS file — "the mod is enabled" and
        // "this file is what loads" are different questions, and only the second one is honest to report.
        var shadowPath = Path.Combine(mods, "DiffReplShadow", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(shadowPath)!);
        var smod = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(smod, w)).BasicStats = new WeaponBasicStats { Damage = 66 };
        smod.BeginWrite.ToPath(shadowPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // DATA-SERVED plugin, with a DISABLED mod folder holding the same filename. The real order
        // (Mo2LoadOrder.BuildFilenameMap) walks overwrite → enabled mods → game Data and never looks at disabled
        // folders, so Data serves this file — but LocatePlugin DOES walk disabled folders and lists that copy FIRST.
        // Judging against the first hit rather than the first ENABLED hit stamps this LIVE plugin inactive: #269's
        // symptom again, reached from the other side. Deliberately in NO enabled mod, or Data would never serve it.
        var dsKey = new ModKey("HcW3DataServed", ModType.Master);
        var dataServedPath = Path.Combine(dir, "game", "Data", dsKey.FileName.String);
        var dsMod = new SkyrimMod(dsKey, SkyrimRelease.SkyrimSE);
        var dsw = dsMod.Weapons.AddNew(); dsw.EditorID = "DataServedW"; dsw.BasicStats = new WeaponBasicStats { Damage = 11 };
        var dswFk = dsw.FormKey;
        dsMod.BeginWrite.ToPath(dataServedPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var decoyPath = Path.Combine(mods, "DataServedDecoy", dsKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(decoyPath)!);
        File.Copy(dataServedPath, decoyPath, overwrite: true);

        // The same disabled-decoy-ahead-of-Data ordering, on a name the ACTIVE ORDER DOES NOT CARRY, so the locate
        // actually runs: a pole for a plugin in the order resolves active before any folder is searched, which is
        // why the live HcW3DataServed arms below can no longer reach this rule.
        var dofKey = new ModKey("HcW3DataOff", ModType.Plugin);
        var dataOffPath = Path.Combine(dir, "game", "Data", dofKey.FileName.String);
        var dofMod = new SkyrimMod(dofKey, SkyrimRelease.SkyrimSE);
        var dofw = dofMod.Weapons.AddNew(); dofw.EditorID = "DataOffW"; dofw.BasicStats = new WeaponBasicStats { Damage = 12 };
        dofMod.BeginWrite.ToPath(dataOffPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.Copy(dataOffPath, Path.Combine(mods, "DataServedDecoy", dofKey.FileName.String), overwrite: true);

        // UNTICKED plugin: sole copy, in an ENABLED mod folder, but listed in plugins.txt WITHOUT the `*`. MO2's left
        // pane says yes, its right pane says no, and the game does not load it — the exact state a mod-folder-only
        // flag reports backwards.
        var unKey = new ModKey("HcW3Unticked", ModType.Plugin);
        var untickedPath = Path.Combine(mods, "DiffUnticked", unKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(untickedPath)!);
        var unMod = new SkyrimMod(unKey, SkyrimRelease.SkyrimSE);
        var uw = unMod.Weapons.AddNew(); uw.EditorID = "UntickedW"; uw.BasicStats = new WeaponBasicStats { Damage = 22 };
        var uwFk = uw.FormKey;
        unMod.BeginWrite.ToPath(untickedPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // UNREGISTERED plugin: sole copy, in an ENABLED mod folder, and therefore the SERVED copy — but MO2 has not
        // written it into loadorder.txt/plugins.txt at all (a mod installed, or a patch written, before the refresh).
        // Serves + Unregistered is a distinct pair from Serves + Unticked: nothing to tick, the remedy is a refresh.
        var unregKey = new ModKey("HcW3Unregistered", ModType.Plugin);
        var unregPath = Path.Combine(mods, "DiffUnregistered", unregKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(unregPath)!);
        var unregMod = new SkyrimMod(unregKey, SkyrimRelease.SkyrimSE);
        var urw = unregMod.Weapons.AddNew(); urw.EditorID = "UnregW"; urw.BasicStats = new WeaponBasicStats { Damage = 33 };
        var urwFk = urw.FormKey;
        unregMod.BeginWrite.ToPath(unregPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // UNLISTED folder: on disk under ModsDir but mentioned NOWHERE in modlist.txt. This is the state of a patch
        // houseCARL has just written, before the MO2 refresh — by far the most common way a real session reaches a
        // "not in the load order" refusal, and the one the fixtures never modelled (review of PR #274, round 2).
        // Its remedy is a refresh; "switch the mod on" names an action MO2 cannot offer for it.
        var unlKey = new ModKey("HcW3Unlisted", ModType.Plugin);
        var unlistedPath = Path.Combine(mods, "DiffUnlistedFresh", unlKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(unlistedPath)!);
        var unlMod = new SkyrimMod(unlKey, SkyrimRelease.SkyrimSE);
        var ulw = unlMod.Weapons.AddNew(); ulw.EditorID = "UnlistedW"; ulw.BasicStats = new WeaponBasicStats { Damage = 44 };
        var ulwFk = ulw.FormKey;
        unlMod.BeginWrite.ToPath(unlistedPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // ARCHIVE backup: the SAME filename as the active replacer (55), parked OUTSIDE every MO2/game root — the
        // old-version-vs-live diff (#269's reporter's actual job). Same name, different file: it must stay off-order.
        var archivePath = Path.Combine(dir, "archive", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        var amod = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(amod, w)).BasicStats = new WeaponBasicStats { Damage = 55 };
        amod.BeginWrite.ToPath(archivePath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // The Data-served plugin is CHECKED and in the order: its arm isolates WHICH COPY is served, so its tick state
        // must not be the thing that decides it (leave it unticked and the tick gate answers first, and the
        // served-copy rule goes untested).
        // HcW3Ghost.esp is TICKED and in the order but exists in NO folder — the stale-profile state (MO2 rewrote the
        // profile, then the mod was removed). It is the one case the explainer must NOT answer with "unticked".
        const string ghostName = "HcW3Ghost.esp";
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
            "# header\r\n" + mKey.FileName + "\r\n" + dsKey.FileName + "\r\n" + rKey.FileName + "\r\n" + ghostName + "\r\n");
        // plugins.txt: master + Data-served + replacer + ghost CHECKED; HcW3Unticked listed WITHOUT the `*` (present but
        // unchecked). HcW3Unregistered is in NEITHER file — its mod folder is enabled, but MO2 has never seen the plugin.
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
            "*" + mKey.FileName + "\r\n*" + dsKey.FileName + "\r\n*" + rKey.FileName + "\r\n" + unKey.FileName + "\r\n*" + ghostName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+DiffRepl\r\n+DiffReplShadow\r\n+DiffMaster\r\n+DiffUnticked\r\n+DiffUnregistered\r\n-DiffDonor\r\n-DataServedDecoy\r\n");

        var genDir = Path.Combine(dir, "corpus-gen");
        try { _ = CorpusRulebook.LoadCorpus(); }
        catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

        var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
        using var svc = LoadOrderService.WithInstance(instance, 0, store);
        svc.Stats();

        string wFid = $"{wFk.ID:X6}:{wFk.ModKey.FileName}";
        string replName = rKey.FileName.String;

        // #486: LoadOrderService.DiffRecord (the deleted 1.x pairwise-diff service) is gone; every arm above this
        // point that called it (active-vs-active B1, off-order-vs-active B2, active-by-path B3 — all already
        // covered by RecordsComparisonFormTests/RecordsScanLaneTests/RecordsListLaneTests through housecarl_records
        // project=delta — plus the two NOT-yet-covered off-order-label facts, a disabled mod addressed by path
        // (B4) and a same-named backup outside every install root (B5)) moves onto that same surface.
        // RecordsOffOrderPathTests.FactB4_ADisabledModsPluginAddressedByPathStaysOffOrderAndNamesTheCause and
        // .FactB5_ASameNamedCopyOutsideEveryInstallRootStaysOffOrder carry B4/B5, driven on RecordsWorld's own
        // OldFile (already a disabled-mod path) plus a scratch copy outside the install for B5.

        // The same computed provenance, read through the records source= pole (svc.ProbeSourceArm — what
        // housecarl_records resolves source= with). Its Where is the composed label: "active in the load order",
        // or "OUT-OF-LOAD-ORDER (<where>; NOT active — <cause>)". These arms were always about the locate
        // contract; LoadOrderService.ReadPluginFile, the entry point they used to reach it through, had no
        // shipped caller and was deleted (#497).
        string Arm(string plugin, string? mod = null)
        {
            var pole = svc.ProbeSourceArm(plugin, mod, out var err);
            return err ?? pole!.Where;
        }
        // A path as the records source= pole takes it.
        static System.Text.Json.JsonElement PolePath(string path) =>
            System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(path)).RootElement.Clone();
        // The cause on its own, so two address forms can be compared on the FACT rather than on the label that
        // introduces it — each lane's label names a different thing, by design.
        string? Cause(string plugin, string? mod = null)
        {
            const string marker = "; NOT active — ";
            var label = Arm(plugin, mod);
            var at = label.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? null : label[(at + marker.Length)..].TrimEnd(')');
        }

        // The path-identity rule, not the locate: a path that IS the active order's own copy resolves back to the
        // plugin name and takes the active arm before any folder is searched.
        Check("source pole: an ENABLED plugin addressed by path resolves to the ACTIVE arm, not a not-active one",
              Arm(replPath) == "active in the load order");
        Check("source pole: the same-named backup OUTSIDE the install says no layer provides it — not 'disabled'",
              Cause(archivePath) is { } wA && wA.Contains("no MO2 layer was found providing this exact path"));
        // The judgement tracks WHICH COPY the install provides, not merely "is this folder enabled" — a shadowed
        // copy in a lower-priority ENABLED mod is not what loads, and calling it live would be worse than the
        // pre-#269 hardcoded false (which was accidentally right here). Its remedy is the OPPOSITE of an unticked
        // one's, so the two must never render alike: what is wrong is WHICH copy, and the pointer is the winner.
        Check("source pole: a SHADOWED copy in a lower-priority enabled mod says SHADOWED and names the serving mod",
              Cause(shadowPath) is { } wS && wS.Contains("SHADOWED") && wS.Contains("DiffRepl") && !wS.Contains("UNTICKED"));
        // And it still READS off that pole — unreachable to the game, not to the tool. Through the records lane,
        // because locating a file and opening it are two different steps and only the second proves this.
        var shadowRead = RecordsTools.Records(svc, formids: new[] { wFid }, source: PolePath(shadowPath),
                                              project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } });
        Check("source pole: the shadowed copy still reads its own value (66) off the file",
              shadowRead.Contains("BasicStats.Damage = 66"));
        // Same rule, again on path identity: the live copy of a plugin the order carries takes the active arm.
        Check("source pole: a game-Data-served plugin listed BEHIND a disabled copy still resolves ACTIVE",
              Arm(dataServedPath) == "active in the load order");
        // The served copy is the first ENABLED-layer hit, not the first hit: a DISABLED folder holding the same
        // filename is walked ahead of game Data, and must not decide which copy serves. Driven on a name the order
        // does NOT carry, so the locate actually runs — judge against the first hit and this copy reads SHADOWED.
        Check("source pole: game Data serves the copy a disabled decoy is walked ahead of — the decoy never decides",
              Cause(dataOffPath) is { } wDo && wDo.Contains("not registered in MO2's load order")
              && !wDo.Contains("SHADOWED") && !wDo.Contains("switched OFF"));
        // A copy in a switched-off mod: the remedy is the LEFT pane, and the cause must say so rather than blame
        // the plugin's tick (the decoy is in plugins.txt nowhere, so a naive renderer would say "unticked"). The
        // PATH lane's own where identifies no layer, so this cause must still name the mod.
        Check("source pole: that disabled copy blames the MOD FOLDER by name, and never calls the plugin unticked",
              Cause(decoyPath) is { } wD && wD.Contains("DataServedDecoy") && wD.Contains("switched OFF") && !wD.Contains("UNTICKED"));
        // UNTICKED: the served copy, in an ENABLED mod, but unchecked in plugins.txt — the game does not load it.
        // The mod's switch and the plugin's tick are separate facts and the cause has to carry both (#271).
        Check("source pole: a plugin in an ENABLED mod but UNTICKED in plugins.txt says so and names plugins.txt",
              Cause(untickedPath) is { } wU && wU.Contains("UNTICKED") && wU.Contains("plugins.txt")
              && wU.Contains(unKey.FileName.String));
        Check("source pole: the same plugin BY FILENAME gives the SAME cause — one fact, however addressed",
              Cause(Path.GetFileName(untickedPath)) == Cause(untickedPath));
        Check("source pole: and via mod= too — the address lanes agree on the cause, not just on the state",
              Cause(unKey.FileName.String, "DiffUnticked") == Cause(untickedPath));
        // The FILENAME lane's where is the located hit's OWN label, so a layer-off cause that restates the layer
        // says the same thing twice in one sentence — output strictly worse than the "NOT active" it replaced.
        // The path-lane arms above cannot catch that: their where is a constant that names no layer at all.
        // The two layer-off causes also carry DIFFERENT remedies and must never render alike — an UNLISTED folder
        // has nothing in MO2's list to switch on, and both are not-served, so a fix reading that alone cannot tell
        // them apart; the standing is decided from modlist.txt membership instead.
        Check("source pole: a DISABLED mod says switch it on; an UNLISTED folder says refresh — never swapped",
              Cause(dKey.FileName.String) is { } wDis && wDis.Contains("switched OFF") && wDis.Contains("switch it on")
              && Cause(unlKey.FileName.String) is { } wUn && wUn.Contains("not registered") && !wUn.Contains("switch it on"));
        // B12 (the providing mod named EXACTLY ONCE in the composed label) and B13 (the remedy stated exactly once)
        // were counting arms over the rendered read_plugin_file banner, which #486 deleted. The counting lives on
        // housecarl_records, which composes its off-order label from the same where + cause:
        // RecordsOffOrderPathTests.FactB4 counts them for the path form and
        // RecordsPluginFileSourceTests.APluginNamedOutOfASwitchedOffModSaysThatModFolderIsOff for the filename form.
        // #497: the mod= arms for a copy of an ACTIVE plugin name (the shadowed and the serving copy of
        // HcW3DiffRepl.esp) have no home on this surface — source= is ONE pole, and a plugin active in the order
        // resolves there before any folder is searched. Stated rather than quietly dropped.

        // ---- #271 item 2: the REFUSAL sweep. A tool that reads THROUGH the load order still refuses on an unticked
        //      plugin (correctly — Q3: a plugin the game does not load must never masquerade as load-order truth), but
        //      the refusal has to say WHY. This is a second mechanism from the flag above: these paths never call the
        //      locate contract at all, they just miss the index's name table. ----
        var readUnticked = svc.ResolveRead(uwFk, unKey.FileName.String, null, false);
        Check("#271 refusal: read_record on an UNTICKED plugin explains it is installed-but-unticked, not 'not found'",
              readUnticked.Error is { } eU && eU.Contains("not in the load order") && eU.Contains("UNTICKED")
              && eU.Contains("plugins.txt"));
        // The escape hatch was housecarl_read_plugin_file until the 1.x cut deleted it; the same read is the
        // named-plugin SOURCE pole on housecarl_records. Pinned through the constant, so the arm follows a rename
        // of the tool rather than a spelling somebody remembered to update here.
        Check("#271 refusal: and points at the raw-read escape hatch rather than leaving a dead end",
              readUnticked.Error is { } eU2 && eU2.Contains(ToolNames.Records) && eU2.Contains("source="));
        var readDisabledMod = svc.ResolveRead(wFk, dKey.FileName.String, null, false);
        Check("#271 refusal: a plugin whose MOD is switched off says so — a different cause, a different remedy",
              readDisabledMod.Error is { } eD && eD.Contains("DiffDonor") && eD.Contains("not active"));
        // The fallback must survive: a name that explains nothing still gets the did-you-mean it always got. The
        // explainer REPLACES the suggester only when it has something real to say.
        var readTypo = svc.ResolveRead(wFk, "HcW3DiffRep.esp", null, false);   // a real near-miss: one character dropped
        Check("#271 refusal: a genuine typo still gets the did-you-mean (the explainer adds, never removes)",
              readTypo.Error is { } eT && eT.Contains("Did you mean") && eT.Contains(replName));
        // Once a concrete cause IS stated, the legacy "houseCARL does not open disabled plugins off disk" tail
        // contradicts the escape-hatch sentence right before it; it survives only where nothing could be explained.
        Check("#271 refusal: the explained case drops the generic posture tail, the unexplained one keeps it",
              readUnticked.Error is { } eU3 && !eU3.Contains("does not open disabled")
              && readTypo.Error is { } eT2 && eT2.Contains("does not open disabled"));

        // Serves + Unregistered: the served copy of a plugin MO2 has never written into its profile. Distinct from
        // unticked (there is nothing to untick) and from a switched-off mod (the folder is on), so it must say neither.
        Check("#271 why: the SERVED copy of an unregistered plugin says so, blaming neither the tick nor the mod",
              Cause(unregKey.FileName.String) is { } wR
              && wR.Contains("not registered in MO2's load order") && !wR.Contains("UNTICKED")
              && !wR.Contains("which the game does not load"));
        // The explainer's stale-profile branch: TICKED, but no layer provides the file. "Unticked" would be a lie and
        // "not on disk anywhere" is the actual remedy-bearing fact.
        var readGhost = svc.ResolveRead(wFk, ghostName, null, false);
        Check("#271 refusal: a ticked-but-missing plugin is called stale-profile, never unticked",
              readGhost.Error is { } eG && eG.Contains("ticked in plugins.txt") && eG.Contains("stale")
              && !eG.Contains("UNTICKED"));

        // The overclaim SWEEP. "the game does not load this file" is an assertion about the FILE that had lodged in
        // places describing the READ — and fixing the banner alone left it live in read_plugin_file's own tool
        // DESCRIPTION (where the model meets it before any output) and in copy_npc_appearance's donor bracket, both
        // found by review. That is the "fixed the reported site, missed the rule's other lanes" shape #270 kept
        // repeating, so this arm sweeps the WHOLE shipped surface by reflection rather than the two sites that were
        // reported. A tool description added tomorrow with the same sentence turns CI red.
        // Case-SENSITIVE, deliberately: the shipped banner legitimately writes "the game does NOT load this file: <why>"
        // as a per-file report, and an ordinal-ignore-case match would read that correct sentence as the defect. What is
        // banned is the flat lower-case assertion (review of PR #274, round 2).
        const string overclaim = "the game does not load this file";
        var mcpTypes = HousecarlMcp.ToolSurface.Assembly.GetTypes();
        const System.Reflection.BindingFlags AllDeclared =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;
        static IEnumerable<string> DescsOf(System.Reflection.ICustomAttributeProvider p) =>
            p.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
             .Cast<System.ComponentModel.DescriptionAttribute>().Select(d => d.Description ?? "");
        var descs = mcpTypes.SelectMany(t => DescsOf(t)                                   // type-level too, not just methods
                .Concat(t.GetMethods(AllDeclared).SelectMany(m => DescsOf(m)
                    .Concat(m.GetParameters().SelectMany(pp => DescsOf(pp))))))
            .ToList();
        Check($"#271 sweep: no MCP type/tool/parameter description asserts the overclaim ({descs.Count} descriptions swept)",
              descs.Count > 50 && !descs.Any(d => d.Contains(overclaim, StringComparison.Ordinal)));
        // The shipped prose that describes this banner lives outside the assembly; sweep it in the same breath so a doc
        // re-asserting what the code stopped claiming cannot drift back in unnoticed.
        foreach (var doc in new[] { Path.Combine("plugin", "README.md"), Path.Combine("plugin", "codex", "housecarl", "SKILL.md") })
        {
            // Repo-relative from the run CWD, the same convention codex-umbrella-coverage-guard uses. A MISSING file
            // fails rather than silently passing — a sweep that quietly checks nothing is worse than no sweep (Q3).
            if (!File.Exists(doc)) { Check($"#271 sweep: shipped doc present to sweep ({doc})", false); continue; }
            Check($"#271 sweep: {doc} does not assert the overclaim either",
                  !File.ReadAllText(doc).Contains(overclaim, StringComparison.Ordinal));
        }

        // The issue's third renderer was copy_npc_appearance's donor bracket. That tool is gone, and with it the
        // only render that asserted the overclaim outside a description — the sweeps above cover what is left.

        // FINDING 1: the fresh-patch refusal. houseCARL writes patches into an unlisted folder, so this is the refusal
        // a real session hits most, and the explainer now answers it — which is exactly why the "cause stated ⇒ drop
        // the legacy tail" rule silently took the readback verify path away from it. That guidance is a fact about
        // the tool, not a guess about the cause, so it must survive whether or not a cause was stated.
        var readFreshPatch = svc.ResolveRead(ulwFk, unlKey.FileName.String, null, false);
        Check("#271 refusal: a just-written (unlisted) patch keeps the readback verify path",
              readFreshPatch.Error is { } eF && eF.Contains("readback=true"));
        Check("#271 refusal: ...and is told to REFRESH MO2, not to switch on a mod MO2 has never listed",
              readFreshPatch.Error is { } eF2 && eF2.Contains("refresh MO2", StringComparison.OrdinalIgnoreCase)
              && !eF2.Contains("Switch that mod on"));
        Check("#271 refusal: the retained verify sentence names the plugin, so it has a subject standing alone",
              readFreshPatch.Error is { } eF3 && eF3.Contains($"prior write into '{unlKey.FileName}'"));
        // ...and the unexplained case keeps BOTH halves, so nothing was lost for it either.
        Check("#271 refusal: an unexplained name keeps the posture line AND the verify path",
              readTypo.Error is { } eT3 && eT3.Contains("does not open disabled") && eT3.Contains("readback=true"));

        // A SECOND refusal lane. All the arms above ride ResolveRead, and every other site got the same clause with no
        // coverage — which is why a dropped space in merge_plugins' refusal shipped unnoticed (review of PR #274).
        // Rendering the whole message, not just the clause, is the point: this arm exists to read the sentence.
        // Two donors minimum; the unticked one is named first so its refusal is the one that fires.
        var mergeUnticked = svc.MergePlugins(new[] { unKey.FileName.String, replName }, "HcW3MergeOut.esp");
        Check("#271 refusal: merge_plugins explains an UNTICKED donor rather than a flat not-active",
              !mergeUnticked.Success && mergeUnticked.Error is { } eM && eM.Contains("UNTICKED")
              && eM.Contains("plugins.txt"));
        Check("#271 refusal: and the merge refusal reads as whole words (no lost space at the splice)",
              mergeUnticked.Error is { } eM2 && eM2.Contains("records and conflict position from the ACTIVE order")
              && !eM2.Contains("conflictposition"));

        // IDENTICAL (both poles resolving to the same provider), fields=-narrowing, and the three refusals
        // (bad formid, plugin not found, a plugin that does not define the record) all called the deleted
        // LoadOrderService.DiffRecord. The first and the two named-plugin refusals are already covered on
        // housecarl_records project=delta (RecordsComparisonFormTests.BothPolesResolvingToOneProviderIsSaid_NeverSilent,
        // RecordsBulkSelectTests' identity-form bad-formid equivalent, RecordsListLaneTests' touchers-named
        // refusal, RecordsComparisonFormTests.DeltaP4_...RefusesNamingTheActualTouchers). fields= narrowing a
        // delta to exactly the named path (B7) is NOT yet covered and moves to
        // RecordsOffOrderPathTests.FactB7_FieldsNarrowsADeltaToExactlyTheNamedPath.
        //
        // The two TOOL-layer render arms that stood here drove housecarl_diff_record, which the 1.x cut
        // deleted. The delta form's text and json renders are tested against housecarl_records in
        // src/housecarl-mcp-tests. Every cell above calls the service directly and is untouched.
    }
}

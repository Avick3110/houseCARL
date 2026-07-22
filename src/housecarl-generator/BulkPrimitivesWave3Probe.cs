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
        Check("refusal: CopyFrom without from_plugin → refused ('requires from_plugin')",
              !noFrom.Success && noFrom.Error is { } e1 && e1.Contains("requires from_plugin"));

        var strayFrom = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "5", FromPlugin = masterName } }, "CfStray", null);
        Check("refusal: from_plugin on a non-CopyFrom verb → refused ('only valid with verb=CopyFrom')",
              !strayFrom.Success && strayFrom.Error is { } e2 && e2.Contains("only valid with verb=CopyFrom"));

        // PR #186 review #2: the mapper is case-SENSITIVE like the engine — a mis-cased 'copyfrom' is NOT CopyFrom, so
        // with from_plugin set it fails loud at the mapper (not opaquely at pre-flight with a stray off-order source).
        var miscased = svc.ApplyEdits(new[] { new BulkOp { Formid = wFid, FieldPath = "BasicStats.Damage", Verb = "copyfrom", FromPlugin = masterName } }, "CfCase", null);
        Check("refusal: mis-cased verb 'copyfrom' + from_plugin → refused at the mapper ('only valid with verb=CopyFrom')",
              !miscased.Success && miscased.Error is { } eCase && eCase.Contains("only valid with verb=CopyFrom"));

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
        var createCopy = svc.CreateRecords("Weapon", "CfCreated",
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
        var w2 = m.Weapons.AddNew(); w2.EditorID = "DfW2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 }; var w2Fk = w2.FormKey;  // master-only
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

        // ARCHIVE backup: the SAME filename as the active replacer (55), parked OUTSIDE every MO2/game root — the
        // old-version-vs-live diff (#269's reporter's actual job). Same name, different file: it must stay off-order.
        var archivePath = Path.Combine(dir, "archive", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        var amod = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(amod, w)).BasicStats = new WeaponBasicStats { Damage = 55 };
        amod.BeginWrite.ToPath(archivePath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+DiffRepl\r\n+DiffMaster\r\n-DiffDonor\r\n");

        var genDir = Path.Combine(dir, "corpus-gen");
        try { _ = CorpusRulebook.LoadCorpus(); }
        catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

        var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
        using var svc = LoadOrderService.WithInstance(instance, 0, store);
        svc.Stats();

        string wFid = $"{wFk.ID:X6}:{wFk.ModKey.FileName}";
        string w2Fid = $"{w2Fk.ID:X6}:{w2Fk.ModKey.FileName}";
        string masterName = mKey.FileName.String, replName = rKey.FileName.String;

        // ACTIVE vs ACTIVE — master's W (10/Base/2 kw) vs the replacer's (99/Winner/1 kw)
        var dAvB = svc.DiffRecord(wFid, masterName, replName, null);
        Check("diff master vs repl: succeeds with differences", dAvB.Error is null && dAvB.Diff!.Deltas.Count > 0);
        Check("diff: Damage delta shows master's 10 with the replacer's value labeled by its filename (99)",
              dAvB.Error is null && dAvB.Diff!.Deltas.Any(x => x.Contains("BasicStats.Damage=10") && x.Contains(replName) && x.Contains("99")));
        Check("diff: both poles report active order", dAvB.Error is null && dAvB.A!.InOrder && dAvB.B!.InOrder);

        // OFF-ORDER pole vs active — the disabled DiffDonor (77) vs the replacer (99)
        var dOff = svc.DiffRecord(wFid, "DiffDonor.esp", replName, new[] { "BasicStats.Damage" });
        Check("diff OFF-ORDER (disabled DiffDonor) vs repl: 77 vs 99, pole a OUT-OF-LOAD-ORDER",
              dOff.Error is null && !dOff.A!.InOrder && dOff.A.Where.Contains("OUT-OF-LOAD-ORDER")
              && dOff.Diff!.Deltas.Any(x => x.Contains("77") && x.Contains("99")));

        // BY PATH (#269) — provenance is COMPUTED from the file, not assumed from how it was addressed.
        // (a) the ACTIVE plugin's own file, passed as a path: it IS what the order loads → active pole, never
        //     "OUT-OF-LOAD-ORDER … disabled". (b) the same-named archive backup: a different file → still off-order.
        var dPathActive = svc.DiffRecord(wFid, "DiffDonor.esp", replPath, new[] { "BasicStats.Damage" });
        Check("diff: the ACTIVE plugin passed BY PATH reports the active order (not OUT-OF-LOAD-ORDER/disabled)",
              dPathActive.Error is null && dPathActive.B!.InOrder && dPathActive.B.Where == "active order"
              && !dPathActive.B.Where.Contains("disabled"));
        Check("diff: an active-by-path pole resolves back to its PLUGIN NAME, and still diffs (77 vs 99)",
              dPathActive.Error is null && dPathActive.B!.Plugin == replName
              && dPathActive.Diff!.Deltas.Any(x => x.Contains("77") && x.Contains("99")));

        var dPathArchive = svc.DiffRecord(wFid, archivePath, replPath, new[] { "BasicStats.Damage" });
        Check("diff: a same-named backup OUTSIDE the install stays OUT-OF-LOAD-ORDER (55 vs 99) — name never decides",
              dPathArchive.Error is null && !dPathArchive.A!.InOrder && dPathArchive.A.Where.Contains("OUT-OF-LOAD-ORDER")
              && dPathArchive.Diff!.Deltas.Any(x => x.Contains("55") && x.Contains("99")));

        // The same computed provenance through read_plugin_file: the enabled plugin's file, addressed by path, is
        // NOT flagged inactive (the second consumer of the shared locate's enabled flag — #269).
        var rpfPath = svc.ReadPluginFile(replPath, wFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: an ENABLED plugin addressed by path reports enabled=true (still OUT-OF-LOAD-ORDER by contract)",
              rpfPath.Error is null && rpfPath.Where == "direct path" && rpfPath.Enabled);
        var rpfArchive = svc.ReadPluginFile(archivePath, wFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: the same-named backup outside the install reports enabled=false",
              rpfArchive.Error is null && rpfArchive.Where == "direct path" && !rpfArchive.Enabled);

        // IDENTICAL — same plugin on both sides
        var dSame = svc.DiffRecord(wFid, replName, replName, null);
        Check("diff same plugin both sides → identical (0 deltas, complete)", dSame.Error is null && dSame.Diff!.Deltas.Count == 0 && dSame.Diff.Complete);

        // fields= narrows the comparison
        var dNarrow = svc.DiffRecord(wFid, masterName, replName, new[] { "BasicStats.Damage" });
        Check("diff fields=[BasicStats.Damage] → exactly the Damage delta",
              dNarrow.Error is null && dNarrow.Diff!.Deltas.Count == 1 && dNarrow.Diff.Deltas[0].Contains("BasicStats.Damage"));

        // refusals
        Check("refusal: bad formid", svc.DiffRecord("not-a-formid", masterName, replName, null).Error is { } de1 && de1.Contains("bad FormID"));
        Check("refusal: plugin_a not found on disk or in order", svc.DiffRecord(wFid, "Nope.esp", replName, null).Error is { } de2 && de2.Contains("plugin_a") && de2.Contains("not in the load order"));
        Check("refusal: a plugin doesn't define the record (W2 master-only, via repl)",
              svc.DiffRecord(w2Fid, masterName, replName, null).Error is { } de3 && de3.Contains("plugin_b") && de3.Contains("does NOT define or override"));

        // render via the TOOL layer (text + json)
        var textR = ReadTools.DiffRecord(svc, wFid, masterName, replName, fields: null, format: "text", mod_a: null, mod_b: null, max_chars: 0);
        Check("render(text): header + Damage delta + reference label",
              textR.Contains("diff " + wFid) && textR.Contains("BasicStats.Damage=10") && textR.Contains(replName));
        var jsonR = ReadTools.DiffRecord(svc, wFid, masterName, replName, fields: null, format: "json", mod_a: null, mod_b: null, max_chars: 0);
        bool jsonOk = false;
        try { using var doc = System.Text.Json.JsonDocument.Parse(jsonR); jsonOk = doc.RootElement.TryGetProperty("deltas", out _) && doc.RootElement.TryGetProperty("complete", out _); }
        catch { }
        Check("render(json): valid JSON carrying deltas + complete", jsonOk);
    }
}

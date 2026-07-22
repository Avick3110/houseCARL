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

    /// <summary>Non-overlapping occurrences of <paramref name="needle"/> — a rendered label appearing TWICE is a real
    /// output defect that a Contains() check reads as a pass.</summary>
    static int CountOf(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
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
        // COVERAGE NOTE (Q3 — name the gap rather than imply completeness): the path-to-active routing also has to
        // NOT capture a plugin EXCLUDED from the index (Mutagen can't parse it) — reading its file directly is the
        // escape hatch, so it must stay on the off-order lane. That branch is uncovered HERE — not because a
        // malformed plugin is expensive to synthesize (it isn't), but because arming it means putting an
        // index-excluded plugin into THIS fixture's active order, which perturbs every other arm above that reads
        // through the same view. It is a plain ExcludedPlugins lookup on the same OrdinalIgnoreCase table the armed
        // checks use; if it earns a test it wants its own fixture, modelled on pkcu-regression's malformed plugin
        // (forward-from-plugin-guard leaves the same branch unarmed and records the same lookup rationale).
        // (a) the ACTIVE plugin's own file, passed as a path: it IS what the order loads → active pole, never
        //     "OUT-OF-LOAD-ORDER … disabled". (b) the same-named archive backup: a different file → still off-order.
        var dPathActive = svc.DiffRecord(wFid, "DiffDonor.esp", replPath, new[] { "BasicStats.Damage" });
        Check("diff: the ACTIVE plugin passed BY PATH reports the active order (not OUT-OF-LOAD-ORDER/disabled)",
              dPathActive.Error is null && dPathActive.B!.InOrder && dPathActive.B.Where == "active order");
        Check("diff: an active-by-path pole resolves back to its PLUGIN NAME, and still diffs (77 vs 99)",
              dPathActive.Error is null && dPathActive.B!.Plugin == replName
              && dPathActive.Diff!.Deltas.Any(x => x.Contains("77") && x.Contains("99")));

        // (c) a DISABLED mod's copy, addressed by path: INSIDE an install root, but its mod is off. The flag has to
        //     come from the located copy — "is it under a root at all" would call this one enabled.
        var dPathDisabled = svc.DiffRecord(wFid, donorPath, replName, new[] { "BasicStats.Damage" });
        Check("diff: a DISABLED mod's plugin addressed BY PATH stays OUT-OF-LOAD-ORDER and NOT active",
              dPathDisabled.Error is null && !dPathDisabled.A!.InOrder
              && dPathDisabled.A.Where.Contains("OUT-OF-LOAD-ORDER") && dPathDisabled.A.Where.Contains("NOT active"));
        // #271: the pole must state the CAUSE, not just the state. "disabled" was one word for four situations with
        // four different remedies — and the wrong word for a plugin (a MOD is disabled; a PLUGIN is inactive).
        Check("diff: the off-order pole NAMES the cause — the DISABLED mod folder that provides the copy",
              dPathDisabled.Error is null && dPathDisabled.A!.Where.Contains("DiffDonor")
              && dPathDisabled.A.Where.Contains("switched OFF"));

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
        // The flag tracks WHICH COPY the install provides, not merely "is this folder enabled" — a shadowed copy in
        // a lower-priority ENABLED mod is not what loads, and reporting it enabled would be worse than the pre-#269
        // hardcoded false (which was accidentally right here).
        var rpfShadow = svc.ReadPluginFile(shadowPath, wFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: a SHADOWED copy in a lower-priority enabled mod reports enabled=false",
              rpfShadow.Error is null && rpfShadow.Where == "direct path" && !rpfShadow.Enabled);
        Check("read_plugin_file: the shadowed copy still READS (66) — it is unreachable to the game, not to the tool",
              rpfShadow.Error is null && rpfShadow.Record is not null);
        // The served copy is the first ENABLED-layer hit, not the first hit: a DISABLED folder holding the same
        // filename is listed ahead of game Data, and must not decide the live plugin's provenance.
        string dswFid = $"{dswFk.ID:X6}:{dswFk.ModKey.FileName}";
        var rpfData = svc.ReadPluginFile(dataServedPath, dswFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: a game-Data-served plugin listed BEHIND a disabled copy still reports enabled=true",
              rpfData.Error is null && rpfData.Where == "direct path" && rpfData.Enabled);
        var rpfDecoy = svc.ReadPluginFile(decoyPath, dswFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: that disabled copy itself reports enabled=false",
              rpfDecoy.Error is null && rpfDecoy.Where == "direct path" && !rpfDecoy.Enabled);
        string uwFid = $"{uwFk.ID:X6}:{uwFk.ModKey.FileName}";
        // UNTICKED: the served copy, in an ENABLED mod, but unchecked in plugins.txt — the game does not load it.
        // The mod's switch and the plugin's tick are separate facts and the flag has to carry both (#271).
        var rpfUnticked = svc.ReadPluginFile(untickedPath, uwFid, null, null, new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file: a plugin in an ENABLED mod but UNTICKED in plugins.txt reports enabled=false",
              rpfUnticked.Error is null && rpfUnticked.Where == "direct path" && !rpfUnticked.Enabled);
        var rpfUntickedByName = svc.ReadPluginFile(Path.GetFileName(untickedPath), uwFid, null, null, null, 1, null, 10);
        Check("read_plugin_file: the same plugin BY FILENAME agrees — the lanes state one fact, not two",
              rpfUntickedByName.Error is null && !rpfUntickedByName.Enabled);
        // The mod= lane is the third address form for one file, and it must answer identically. The shadow mod is
        // ENABLED, so a lane testing only "is this mod switched on" would call its shadowed copy live.
        var rpfModShadow = svc.ReadPluginFile(rKey.FileName.String, wFid, null, "DiffReplShadow", new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file mod=: a shadowed copy in an ENABLED mod reports enabled=false, as the path lane does",
              rpfModShadow.Error is null && !rpfModShadow.Enabled);
        var rpfModServing = svc.ReadPluginFile(rKey.FileName.String, wFid, null, "DiffRepl", new[] { "BasicStats.Damage" }, 1, null, 10);
        Check("read_plugin_file mod=: the SERVING mod's copy of the same name reports enabled=true",
              rpfModServing.Error is null && rpfModServing.Enabled);
        var rpfModUnticked = svc.ReadPluginFile(unKey.FileName.String, uwFid, null, "DiffUnticked", null, 1, null, 10);
        Check("read_plugin_file mod=: the serving mod's copy of an UNTICKED plugin still reports enabled=false",
              rpfModUnticked.Error is null && !rpfModUnticked.Enabled);

        // ---- #271: the flag now EXPLAINS. Every not-active lane above must name its own cause, and the three causes
        //      must be distinguishable — an agent that reads "NOT active" and cannot tell unticked from shadowed from
        //      switched-off burns a search rediscovering what the tool already computed. Armed on EVERY address form,
        //      because "armed the reported lane, missed its twin" is the mistake that cost #270 four review rounds. ----
        Check("#271 why: an UNTICKED plugin by PATH says UNTICKED and names plugins.txt (not its mod's switch)",
              rpfUnticked.WhyNotActive is { } wU && wU.Contains("UNTICKED") && wU.Contains("plugins.txt")
              && wU.Contains(unKey.FileName.String));
        Check("#271 why: the same plugin BY FILENAME gives the SAME cause — one fact, however addressed",
              rpfUntickedByName.WhyNotActive == rpfUnticked.WhyNotActive);
        Check("#271 why: and via mod= too — all three address lanes agree on the cause, not just on the boolean",
              rpfModUnticked.WhyNotActive == rpfUnticked.WhyNotActive);
        // A shadowed copy's remedy is the OPPOSITE of an unticked one's, so the two must never render alike: this mod
        // is enabled and this plugin is ticked — what's wrong is WHICH copy, and the useful pointer is the winner.
        Check("#271 why: a SHADOWED copy says SHADOWED and points at the mod that serves the winning copy",
              rpfShadow.WhyNotActive is { } wS && wS.Contains("SHADOWED") && wS.Contains("DiffRepl")
              && !wS.Contains("UNTICKED"));
        Check("#271 why: mod= reaches the same shadowed copy and states the same cause",
              rpfModShadow.WhyNotActive is { } wMS && wMS.Contains("SHADOWED"));
        // A copy in a switched-off mod: the remedy is the LEFT pane, and the label must say so rather than blame the
        // plugin's tick (the decoy is not listed in plugins.txt at all, so a naive renderer would say "unticked").
        Check("#271 why: a copy in a DISABLED mod blames the MOD FOLDER, and never claims the plugin is unticked",
              rpfDecoy.WhyNotActive is { } wD && wD.Contains("DataServedDecoy") && wD.Contains("switched OFF")
              && !wD.Contains("UNTICKED"));
        Check("#271 why: a same-named backup OUTSIDE the install says it is not an install copy — not 'disabled'",
              rpfArchive.WhyNotActive is { } wA && wA.Contains("no MO2 layer was found providing this exact path"));
        // The other direction, and the one #269 was actually about: when the game DOES load the file, there must be
        // no cause at all — a leftover explanation would re-assert the very falsehood this issue set out to remove.
        Check("#271 why: the three ACTIVE lanes carry NO cause (null), so nothing contradicts the live file",
              rpfPath.WhyNotActive is null && rpfData.WhyNotActive is null && rpfModServing.WhyNotActive is null);

        // The RENDERED header, end to end — the banner's old parenthetical ("the game does not load this file") was
        // false for exactly this call, where the file passed IS the live plugin (#269/#271 item 3), and the
        // "[mod 'X' (enabled); NOT active]" pairing named a folder and a file in one breath without saying which was
        // which (item 4). Assert on the rendered string, since the wording IS the deliverable here.
        var renderLive = Wire.RenderPluginFile(rpfPath, 8000);
        Check("#271 banner: the stamp describes the READ, never claiming the game does not load the file",
              renderLive.Contains("OUT-OF-LOAD-ORDER") && renderLive.Contains("not resolved through the load order")
              && !renderLive.Contains("the game does not load this file"));
        Check("#271 render: a live file addressed by path says so positively — #269's symptom, inverted",
              renderLive.Contains("the game loads this file"));
        var renderUnticked = Wire.RenderPluginFile(rpfUntickedByName, 8000);
        Check("#271 render: the mod-vs-plugin pairing is disambiguated in words, and carries the cause",
              renderUnticked.Contains("mod 'DiffUnticked' (enabled)")
              && renderUnticked.Contains("the game does NOT load this file")
              && renderUnticked.Contains("UNTICKED in plugins.txt"));
        // The FILENAME lane's Where is the located hit's OWN label, so a LayerOff cause that restates the layer says
        // the same thing twice in one sentence — output strictly worse than the "NOT active" it replaced. The path-lane
        // arms above cannot catch it: their Where is the constant "direct path", where the restatement is the only way
        // the layer gets named at all. Armed on the lane where the bug can actually appear (review of PR #274).
        Console.WriteLine("DBG byname=[" + (svc.ReadPluginFile(dKey.FileName.String, wFid, null, null, null, 1, null, 10).WhyNotActive ?? "<null>") + "]");
        Console.WriteLine("DBG bymod=[" + (svc.ReadPluginFile(dKey.FileName.String, wFid, null, "DiffDonor", null, 1, null, 10).WhyNotActive ?? "<null>") + "]");
        Console.WriteLine("DBG bypath=[" + (rpfDecoy.WhyNotActive ?? "<null>") + "]");
        Console.WriteLine("DBG archive=[" + (rpfArchive.WhyNotActive ?? "<null>") + "]");
        Console.WriteLine("DBG json=[" + JsonWire.RenderPluginFile(rpfUntickedByName, 8000) + "]");
        // ALL THREE address forms, not the one that was reported. Round 1 flagged this duplication, the fix was tested
        // by comparing the two labels for equality, and that test silently failed in the mod= lane — whose Where names
        // the mod but omits its state — so the identical defect survived a round of review in an unarmed lane. Each
        // lane is now armed for the SAME cause: the mod name must appear exactly once in every rendered form.
        var rpfDisabledByName = svc.ReadPluginFile(dKey.FileName.String, wFid, null, null, null, 1, null, 10);
        var rpfDisabledByMod = svc.ReadPluginFile(dKey.FileName.String, wFid, null, "DiffDonor", null, 1, null, 10);
        var renderDisabledByName = Wire.RenderPluginFile(rpfDisabledByName, 8000);
        var renderDisabledByMod = Wire.RenderPluginFile(rpfDisabledByMod, 8000);
        Check("#271 render: a disabled-mod copy BY FILENAME names its mod exactly once",
              rpfDisabledByName.Error is null && CountOf(renderDisabledByName, "mod 'DiffDonor'") == 1
              && renderDisabledByName.Contains("the game does NOT load this file"));
        Check("#271 render: BY MOD= too — the lane whose label omits the state, where the equality test failed",
              rpfDisabledByMod.Error is null && CountOf(renderDisabledByMod, "mod 'DiffDonor'") == 1
              && renderDisabledByMod.Contains("the game does NOT load this file"));
        // ...while the PATH lane, whose Where identifies nothing, must STILL name the mod — suppressing it everywhere
        // would lose the only mention of which mod provides the file.
        Check("#271 render: the same copy BY PATH still names the mod, since its Where cannot",
              rpfDecoy.WhyNotActive is { } wD2 && wD2.Contains("DataServedDecoy"));
        // The two layer-off causes carry DIFFERENT remedies and must never render alike: an UNLISTED folder has nothing
        // in MO2's list to switch on. Both are flagged not-enabled by the locate, so a fix reading that flag alone
        // cannot tell them apart — the standing is decided from modlist.txt membership instead.
        string ulwFid = $"{ulwFk.ID:X6}:{ulwFk.ModKey.FileName}";
        var rpfUnlisted = svc.ReadPluginFile(unlKey.FileName.String, ulwFid, null, null, null, 1, null, 10);
        Check("#271 why: a DISABLED mod says switch it on; an UNLISTED folder says refresh — never swapped",
              rpfDisabledByName.WhyNotActive is { } wDis && wDis.Contains("switched OFF") && wDis.Contains("switch it on")
              && rpfUnlisted.Error is null && rpfUnlisted.WhyNotActive is { } wUn
              && wUn.Contains("not registered") && !wUn.Contains("switch it on"));
        // The JSON lane carries the cause too — advertised in the changelog, previously unarmed.
        var jsonUnticked = JsonWire.RenderPluginFile(rpfUntickedByName, 8000);
        var jsonLive = JsonWire.RenderPluginFile(rpfPath, 8000);
        Check("#271 json: why_not_active rides beside enabled, and is explicitly null when the game loads the file",
              jsonUnticked.Contains("\"why_not_active\"") && jsonUnticked.Contains("UNTICKED")
              && jsonLive.Contains("\"why_not_active\": null"));

        // ---- #271 item 2: the REFUSAL sweep. A tool that reads THROUGH the load order still refuses on an unticked
        //      plugin (correctly — Q3: a plugin the game does not load must never masquerade as load-order truth), but
        //      the refusal has to say WHY. This is a second mechanism from the flag above: these paths never call the
        //      locate contract at all, they just miss the index's name table. ----
        var readUnticked = svc.ResolveRead(uwFk, unKey.FileName.String, null, false);
        Check("#271 refusal: read_record on an UNTICKED plugin explains it is installed-but-unticked, not 'not found'",
              readUnticked.Error is { } eU && eU.Contains("not in the load order") && eU.Contains("UNTICKED")
              && eU.Contains("plugins.txt"));
        Check("#271 refusal: and points at the raw-read escape hatch rather than leaving a dead end",
              readUnticked.Error is { } eU2 && eU2.Contains("housecarl_read_plugin_file"));
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
        string urwFid = $"{urwFk.ID:X6}:{urwFk.ModKey.FileName}";
        var rpfUnreg = svc.ReadPluginFile(unregKey.FileName.String, urwFid, null, null, null, 1, null, 10);
        Check("#271 why: the SERVED copy of an unregistered plugin says so, blaming neither the tick nor the mod",
              rpfUnreg.Error is null && !rpfUnreg.Enabled && rpfUnreg.WhyNotActive is { } wR
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
        var mcpTypes = typeof(HousecarlMcp.ReadTools).Assembly.GetTypes();
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

        // The third renderer named by the issue. Its bracket is gated on a flag the file lane sets UNCONDITIONALLY, so
        // it asserted the overclaim even for a donor the game does load — armed here through the real render.
        var npcRender = NpcCopyTools.Render(new NpcCopyOutcome(
            true, null, "apply", wFk, "file 'X.esp' (mod 'M' (enabled))", /*DonorOutOfLoadOrder:*/ true, wFk,
            "X.esp", false, Array.Empty<InternalizedRecord>(), Array.Empty<string>(), 0, Array.Empty<string>(),
            Array.Empty<NpcAppearanceCopy.StripReport>(), Array.Empty<string>(), false, false,
            Array.Empty<string>(), null, 0, null));
        Check("#271 sweep: copy_npc_appearance's donor bracket states the READ, not a claim about the file",
              !npcRender.Contains(overclaim, StringComparison.OrdinalIgnoreCase)
              && npcRender.Contains("OUT-OF-LOAD-ORDER"));

        // FINDING 1: the fresh-patch refusal. houseCARL writes patches into an unlisted folder, so this is the refusal
        // a real session hits most, and the explainer now answers it — which is exactly why the "cause stated ⇒ drop
        // the legacy tail" rule silently took the full_readback verify path away from it. That guidance is a fact about
        // the tool, not a guess about the cause, so it must survive whether or not a cause was stated.
        var readFreshPatch = svc.ResolveRead(ulwFk, unlKey.FileName.String, null, false);
        Check("#271 refusal: a just-written (unlisted) patch keeps the full_readback verify path",
              readFreshPatch.Error is { } eF && eF.Contains("full_readback=true"));
        Check("#271 refusal: ...and is told to REFRESH MO2, not to switch on a mod MO2 has never listed",
              readFreshPatch.Error is { } eF2 && eF2.Contains("refresh MO2", StringComparison.OrdinalIgnoreCase)
              && !eF2.Contains("Switch that mod on"));
        Check("#271 refusal: the retained verify sentence names the plugin, so it has a subject standing alone",
              readFreshPatch.Error is { } eF3 && eF3.Contains($"prior write into '{unlKey.FileName}'"));
        // ...and the unexplained case keeps BOTH halves, so nothing was lost for it either.
        Check("#271 refusal: an unexplained name keeps the posture line AND the verify path",
              readTypo.Error is { } eT3 && eT3.Contains("does not open disabled") && eT3.Contains("full_readback=true"));

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

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
            // P8b CopyFrom + P8c diff_record arms land in their own commits.

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
}

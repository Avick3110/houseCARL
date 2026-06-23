using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the IN-PLACE WRITE LANE, Wave 1 (dev/plans/IN_PLACE_WRITE_LANE_PLAN_2026-06-13.md
/// §10 CI teeth). The in-place lane (set_field/bulk_apply with target=+in_place=true) edits an EXISTING plugin the user
/// owns — incl. one houseCARL didn't author — back over itself, instead of writing a new patch. It is the one write lane
/// that touches the user's ORIGINAL file, so the safety properties are load-bearing and each is RED-provable here:
///
///   CONTENT-SOURCE (§4.1)  — the edit's body is the TARGET's OWN record, NEVER the load-order winner; a record the
///                            target doesn't define is REFUSED (no foreign content injected). RED if sourced from winner.
///   COUNTER PRESERVED      — the author's HEDR.NextObjectID survives verbatim (WriteInPlace skips EnsureFormIdFloor). RED
///                            if the patch-lane floor ran (a sub-0x800 author counter would jump to 0x800).
///   MASTERS PRESERVED      — no Skyrim.esm/Update.esm baseline force-include (WriteInPlace ≠ WritePatch). RED if baseline added.
///   FLAT LOCK (winner==target) — re-editing a record the ACTIVE target itself owns: Phase-1 fetch opens the target
///                            overlay, ReleaseOverlay must close it before the File.Replace swap. RED if it stays mapped.
///   CONSENT HANDSHAKE      — first in-place touch of a plugin REFUSES-and-explains (writes nothing); acknowledge=true
///                            proceeds; a second edit does NOT re-prompt (persisted, cross-session via UserConfig).
///   RESOLVER / CONTRACT    — target= resolves the REAL active-plugin path (a non-load-order name REFUSES, never
///                            retargets); in_place needs target=, is mutually exclusive with into=; opt-in defaults OFF.
///
/// Self-contained: synthesizes a master + a user override + a higher override in TEMP and generates the validator corpus
/// BY CONSTRUCTION in-process (no game data, no checked-in corpus.json). Drives the REAL WritePatchBuilder.ApplyInPlace
/// (builder arms) and the REAL LoadOrderService.ApplyEdits in-place branch (service arms, via the ForGuard seam).
/// Run: dotnet run --project src/housecarl-generator inplace-guard
///
/// The NESTED lock arm (the LinkCacheFor-on-a-foreign-target path) needs a real nested record + master, so it lives in
/// <see cref="RunNestedProof"/> (real Skyrim.esm; self-skips on the CI runner), the same posture as writelock-nested-proof.
/// </summary>
public static class InPlaceProbe
{
    const string MasterName = "HcInPlaceMaster.esm";
    const string UserName = "HcInPlaceUser.esp";
    const string HighName = "HcInPlaceHigh.esp";

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — in-place write lane, Wave 1  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-inplace-guard");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);

        // Corpus BY CONSTRUCTION (the edits pre-flight through the CorpusRulebook); point the SERVICE's lazy Load() at it too.
        var corpusPath = GenerateCorpus(tmpDir);
        CorpusRulebook.CorpusPath = corpusPath;
        var rulebook = CorpusRulebook.Load(corpusPath);

        // ---- Setup: a master weapon, a USER override (Damage=20, Name="UserSword", a deliberately sub-0x800 author
        //      counter 0x123), and a HIGHER override (Damage=99, Name="HighSword"). A SECOND master-only weapon the user
        //      never overrides feeds the refuse-if-undefined arm. ----
        string masterPath = Path.Combine(tmpDir, MasterName);
        string userPristine = Path.Combine(tmpDir, "pristine", UserName);
        string highPath = Path.Combine(tmpDir, HighName);
        Directory.CreateDirectory(Path.GetDirectoryName(userPristine)!);

        var masterKey = new ModKey("HcInPlaceMaster", ModType.Master);
        FormKey wfk, w2fk;
        {
            var m = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var w = m.Weapons.AddNew(); w.EditorID = "HcIP_Weap"; w.BasicStats = new WeaponBasicStats { Damage = 10 }; w.Name = "MasterSword";
            var w2 = m.Weapons.AddNew(); w2.EditorID = "HcIP_Weap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
            wfk = w.FormKey; w2fk = w2.FormKey;
            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE))
        {
            var u = new SkyrimMod(new ModKey("HcInPlaceUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var uw = u.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == wfk));
            uw.BasicStats!.Damage = 20; uw.Name = "UserSword";
            u.ModHeader.Stats.NextFormID = 0x123;                       // a distinctive, deliberately sub-0x800 author counter
            u.BeginWrite.ToPath(userPristine).WithLoadOrder(new ISkyrimModGetter[] { mOv }).NoNextFormIDProcessing().Write();

            var h = new SkyrimMod(new ModKey("HcInPlaceHigh", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var hw = h.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == wfk));
            hw.BasicStats!.Damage = 99; hw.Name = "HighSword";
            h.BeginWrite.ToPath(highPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        }
        Console.WriteLine($"-- setup: master {MasterName} (weapon {wfk}), user override (dmg 20, 'UserSword', counter 0x123), higher override (dmg 99, 'HighSword') --");
        Console.WriteLine();

        string fmtWfk = $"{wfk.ID:X6}:{MasterName}";       // the FormID the tools take (6 hex : defining master)
        string fmtW2fk = $"{w2fk.ID:X6}:{MasterName}";
        var results = new List<(string name, bool pass, string detail)>();

        // ===== A — CONTENT-SOURCE / winner-injection: edit the USER's body, NOT the winner's =====
        // Order [master, user(20/UserSword), high(99/HighSword)]; winner of the weapon is HIGH. Edit ONLY Damage on the
        // user. A correct in-place sources from the TARGET, so the user's Name stays "UserSword"; if it wrongly sourced
        // from the winner, Name would become "HighSword". The high override must stay byte-untouched.
        {
            var userA = FreshUser(tmpDir, "A", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userA, highPath });
            var o = WritePatchBuilder.ApplyInPlace(r, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = wfk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "55" } },
                userA, UserName);
            int dmg = ReadDamage(userA, wfk); string nm = ReadName(userA, wfk);
            int highDmg = ReadDamage(highPath, wfk);
            bool pass = o.Success && o.InPlace && dmg == 55 && nm == "UserSword" && highDmg == 99;
            results.Add(("A content-source (edit user body, not winner)", pass,
                $"success={o.Success} inPlace={o.InPlace} userDmg={dmg}(want 55) userName='{nm}'(want UserSword — NOT HighSword) highDmg={highDmg}(want 99 untouched)  [{o.Error ?? "ok"}]"));

            // C rides on A's written file — COUNTER + MASTERS preserved (no floor, no baseline).
            uint nextId = ReadNextFormId(userA);
            var masters = ReadMasters(userA);
            bool cPass = nextId == 0x123 && masters.Count == 1 && masters[0].Equals(MasterName, StringComparison.OrdinalIgnoreCase);
            results.Add(("C counter+masters preserved (no floor, no baseline)", cPass,
                $"NextObjectID=0x{nextId:X}(want 0x123 — a patch-lane floor would force 0x800) masters=[{string.Join(",", masters)}](want only {MasterName})"));
        }

        // ===== B — CONTENT-SOURCE GUARD: refuse a FormKey the target doesn't define =====
        // W2 lives only in the master; the user never overrides it. In-place must REFUSE (it edits only what the file owns).
        {
            var userB = FreshUser(tmpDir, "B", userPristine);
            var before = File.ReadAllBytes(userB);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userB, highPath });
            var o = WritePatchBuilder.ApplyInPlace(r, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = w2fk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "7" } },
                userB, UserName);
            bool untouched = File.ReadAllBytes(userB).AsSpan().SequenceEqual(before);
            bool pass = !o.Success && (o.Error?.Contains("does not define") ?? false) && untouched;
            results.Add(("B refuse-if-undefined (edit only what the file owns)", pass,
                $"refused={!o.Success} untouched={untouched}  [{o.Error ?? "(wrote — WRONG)"}]"));
        }

        // ===== D — FLAT LOCK: re-edit a record the ACTIVE target itself owns (winner == target) =====
        // Order [master, user]; the user IS the weapon's winner. Phase-1 fetch opens the target overlay; ReleaseOverlay
        // must close it before File.Replace. Success + value-landed proves the self-lock discipline on a foreign target.
        {
            var userD = FreshUser(tmpDir, "D", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userD });   // no higher override → winner == target
            var winner = r.ResolveWinner(wfk);
            var o = WritePatchBuilder.ApplyInPlace(r, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = wfk, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "42" } },
                userD, UserName);
            int dmg = ReadDamage(userD, wfk);
            bool pass = o.Success && dmg == 42;
            results.Add(("D flat lock (winner==target; ReleaseOverlay before swap)", pass,
                $"winner={winner?.WinnerPlugin}(==target) success={o.Success} dmg={dmg}(want 42)  [{o.Error ?? "ok"}]"));
        }

        // ===== E — CONTRACT validation (refusals that fire BEFORE any resolve/rulebook) =====
        {
            var userE = FreshUser(tmpDir, "E", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userE, highPath });
            var svc = LoadOrderService.ForGuard(r, new UserConfigStore(Path.Combine(tmpDir, "E.user.json")));
            var op = new[] { new BulkOp { Formid = fmtWfk, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "1" } };

            var noTarget = svc.ApplyEdits(op, null, null, inPlace: true);                                   // in_place w/o target
            var withInto = svc.ApplyEdits(op, null, "somepatch", fullReadback: false, target: UserName, inPlace: true); // in_place + into=
            var targetNoFlag = svc.ApplyEdits(op, null, null, fullReadback: false, target: UserName, inPlace: false);   // target w/o in_place
            bool pass = !noTarget.Success && (noTarget.Error?.Contains("requires target=") ?? false)
                     && !withInto.Success && (withInto.Error?.Contains("mutually exclusive") ?? false)
                     && !targetNoFlag.Success && (targetNoFlag.Error?.Contains("only meaningful with in_place") ?? false);
            results.Add(("E contract (in_place⇔target, ⊥ into=)", pass,
                $"noTarget={Trim(noTarget.Error)} | into={Trim(withInto.Error)} | noFlag={Trim(targetNoFlag.Error)}"));
        }

        // ===== F — RESOLVER: a non-load-order target REFUSES (never retargets) =====
        {
            var userF = FreshUser(tmpDir, "F", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userF, highPath });
            var svc = LoadOrderService.ForGuard(r, new UserConfigStore(Path.Combine(tmpDir, "F.user.json")));
            var o = svc.ApplyEdits(new[] { new BulkOp { Formid = fmtWfk, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "1" } },
                null, null, fullReadback: false, target: "NotAReal.esp", inPlace: true, acknowledge: true);
            bool pass = !o.Success && (o.Error?.Contains("not an active plugin") ?? false);
            results.Add(("F resolver refuses a non-load-order target", pass, Trim(o.Error)));
        }

        // ===== G — CONSENT HANDSHAKE: RED (first touch refuses) → GREEN (acknowledge writes) → no re-prompt → persists =====
        {
            var userG = FreshUser(tmpDir, "G", userPristine);
            string storePath = Path.Combine(tmpDir, "G.user.json");
            string userGPath = userG;
            BulkOp[] Edit(string v) => new[] { new BulkOp { Formid = fmtWfk, FieldPath = "BasicStats.Damage", Verb = "Set", Value = v } };

            byte[] before = File.ReadAllBytes(userGPath);
            bool red, untouched, green, greenLanded, noReprompt, reLanded, persisted;
            using (var r = LoadOrderResolver.Build(new[] { masterPath, userGPath }))
            {
                var svc = LoadOrderService.ForGuard(r, new UserConfigStore(storePath));
                var first = svc.ApplyEdits(Edit("31"), null, null, fullReadback: false, target: UserName, inPlace: true, acknowledge: false);
                red = first.NeedsAcknowledge && !first.Success;
                untouched = File.ReadAllBytes(userGPath).AsSpan().SequenceEqual(before);

                var ack = svc.ApplyEdits(Edit("31"), null, null, fullReadback: false, target: UserName, inPlace: true, acknowledge: true);
                green = ack.Success && ack.InPlace; greenLanded = ReadDamage(userGPath, wfk) == 31;

                var again = svc.ApplyEdits(Edit("32"), null, null, fullReadback: false, target: UserName, inPlace: true, acknowledge: false);
                noReprompt = again.Success && !again.NeedsAcknowledge; reLanded = ReadDamage(userGPath, wfk) == 32;
            }
            // PERSISTS cross-session: a brand-new store on the SAME json already knows this plugin is acknowledged.
            persisted = new UserConfigStore(storePath).IsInPlaceAcknowledged(userGPath);
            bool pass = red && untouched && green && greenLanded && noReprompt && reLanded && persisted;
            results.Add(("G handshake RED→GREEN→no-reprompt→persists", pass,
                $"firstRefused={red} untouched={untouched} ackWrote={green} landed31={greenLanded} secondNoPrompt={noReprompt} landed32={reLanded} persistedAcrossStores={persisted}"));
        }

        // ===== H — OPT-IN BY CONSTRUCTION: the tool params default OFF (assert the schema, not by omission) =====
        {
            var sf = typeof(WriteTools).GetMethod(nameof(WriteTools.SetField))!;
            var ba = typeof(WriteTools).GetMethod(nameof(WriteTools.BulkApply))!;
            bool DefOff(System.Reflection.MethodInfo m) =>
                m.GetParameters().First(p => p.Name == "in_place").DefaultValue is false
                && m.GetParameters().First(p => p.Name == "target").DefaultValue is null
                && m.GetParameters().First(p => p.Name == "acknowledge").DefaultValue is false;
            bool pass = DefOff(sf) && DefOff(ba);
            results.Add(("H opt-in by construction (in_place/target/acknowledge default OFF)", pass,
                $"set_field={DefOff(sf)} bulk_apply={DefOff(ba)}"));
        }

        Console.WriteLine("── ARMS ──");
        bool all = true;
        foreach (var (name, pass, detail) in results)
        {
            Console.WriteLine($"   {(pass ? "PASS" : "FAIL")}  {name}");
            Console.WriteLine($"         {detail}");
            all &= pass;
        }
        Console.WriteLine();
        Console.WriteLine($"=== inplace-guard: {(all ? "PASS" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return all ? 0 : 1;
    }

    /// <summary>
    /// REAL-DATA proof (the one arm the self-contained guard can't cover): the in-place re-edit of a NESTED own-override
    /// (a PlacedObject — lives in a Cell, so Phase-3 builds the source LinkCacheFor OVER the target overlay). The
    /// ReleaseOverlay-before-swap discipline must dispose BOTH the flat (GetRecord) AND the nested (LinkCacheFor) session
    /// overlays on the FOREIGN target before File.Replace. Mirrors writelock-nested-proof but through ApplyInPlace.
    /// Needs a real master (Skyrim.esm) for a genuine nested record — self-SKIPs on the CI runner (no game data).
    /// Run: dotnet run --project src/housecarl-generator inplace-nested-proof ["&lt;Data dir with Skyrim.esm&gt;"]
    /// </summary>
    public static int RunNestedProof(string[] args)
    {
        Console.WriteLine("=== inplace-nested-proof — in-place re-edit of a NESTED own-override (real data) ===");
        string dataDir = args.Length > 0 ? args[0] : @"E:\Skyrim Modding\ARR 2.0\Stock Game\Data";
        string skyrim = Path.Combine(dataDir, "Skyrim.esm");
        if (!File.Exists(skyrim))
        {
            Console.WriteLine($"SKIP: need Skyrim.esm; not found at {skyrim} (pass the Data dir as arg 1). A real nested record + master");
            Console.WriteLine("      can't be synthesized for the LinkCacheFor-on-a-foreign-target arm — the same posture as writelock-nested-proof.");
            return 0;
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-inplace-nested");
        if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
        Directory.CreateDirectory(tmpDir);
        var rulebook = CorpusRulebook.Load(GenerateCorpus(tmpDir));

        FormKey refrFk;
        using (var r0 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            refrFk = r0.WinnerRecordsOfType(new[] { typeof(IPlacedObjectGetter) }).Select(x => x.fk).FirstOrDefault();
            if (refrFk.IsNull) { Console.Error.WriteLine("no PlacedObject in Skyrim.esm"); return 1; }
        }
        Console.WriteLine($"-- real nested record: PlacedObject {refrFk} --");
        string userPath = Path.Combine(tmpDir, "HcInPlaceNested.esp");

        // STEP 1 — author a foreign-style user mod that OVERRIDES the nested record (winner=Skyrim.esm; the patch lane).
        using (var r1 = LoadOrderResolver.Build(new[] { skyrim }))
        {
            var o = WritePatchBuilder.Apply(r1, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "1.5" } },
                userPath, extend: false);
            Console.WriteLine($"   step 1  author the nested override (winner=Skyrim.esm) : {(o.Success ? "OK" : "FAIL — " + o.Error)}");
            if (!o.Success) return 1;
        }

        // STEP 2 — THE TEST: edit that nested record IN PLACE (winner == the user mod == target → flat + nested overlay on it).
        bool ok; string err;
        using (var r2 = LoadOrderResolver.Build(new[] { skyrim, userPath }))
        {
            var o = WritePatchBuilder.ApplyInPlace(r2, rulebook,
                new[] { new WritePatchBuilder.PatchEdit { Target = refrFk, Path = new[] { "Scale" }, Verb = "Set", Value = "2.5" } },
                userPath, Path.GetFileName(userPath));
            ok = o.Success; err = o.Error ?? "ok";
            Console.WriteLine($"   step 2  re-edit the NESTED record IN PLACE : {(ok ? "OK" : "FAIL — " + err)}");
        }
        float? scale = ReadPlacedScale(userPath, refrFk);
        bool landed = scale.HasValue && Math.Abs(scale.Value - 2.5f) < 0.001f;
        Console.WriteLine($"   nested in-place edit landed (Scale==2.5) : {(landed ? "PASS" : $"FAIL (scale={scale?.ToString() ?? "null"})")}");

        bool pass = ok && landed;
        Console.WriteLine($"=== inplace-nested-proof: {(pass ? "PASS — nested in-place survives ReleaseOverlay-before-serialize on a foreign target" : "FAIL")} ===");
        try { Directory.Delete(tmpDir, recursive: true); } catch { }
        return pass ? 0 : 1;
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    static string FreshUser(string tmpDir, string arm, string pristine)
    {
        var dir = Path.Combine(tmpDir, "arm-" + arm);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, UserName);   // KEEP the filename (== ModKey); only the dir differs
        File.Copy(pristine, path, overwrite: true);
        return path;
    }

    static string GenerateCorpus(string tmpDir)
    {
        var genDir = Path.Combine(tmpDir, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "corpus-ref"));
        return Path.Combine(genDir, "corpus.json");
    }

    static int ReadDamage(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.BasicStats?.Damage ?? -1; }
        catch { return -1; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static string ReadName(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.Name?.String ?? "(none)"; }
        catch { return "(read failed)"; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static uint ReadNextFormId(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.ModHeader.Stats.NextFormID; }
        catch { return 0xDEAD; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static List<string> ReadMasters(string path)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList(); }
        catch { return new List<string> { "(read failed)" }; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static float? ReadPlacedScale(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.FormKey == fk)?.Scale; }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static string Trim(string? s) => (s ?? "(null)").Replace("\r", " ").Replace("\n", " ").Trim();
}

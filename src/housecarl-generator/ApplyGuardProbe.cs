using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for <c>housecarl_apply</c> — the 2.0 S1 field-write
/// surface (tool-surface-2.0 W3; SPEC §2.2 ACT, §4.5, §5.1/§5.2, §6.1).
///
/// Drives the REAL end-to-end tool path — a synthetic MO2 instance in temp + <see cref="LoadOrderService"/> +
/// <see cref="ApplyTools.Apply"/> — so the wire reader, the LANE grammar, the alias-visible vocabulary, the corpus
/// pre-flight and the apply engine are exercised exactly as a caller hits them. Five arms:
/// <list type="number">
/// <item><b>ops grammar</b> — the 2.0 vocabulary (op=, one op is a set of one), the @file spelling that retired
/// from_file=, and the strict element reader's NAMED refusals (undeclared member, mixed inline/@file).</item>
/// <item><b>LANE</b> — the three destinations are exclusive and a dropped one is refused BY NAME (the class 1.x
/// silently ignored), in_place is the FILE'S NAME with its consent handshake, into= extends.</item>
/// <item><b>the §4.5 zip</b> — bundle × assignments as a cross-RECORD copy: a real transplant, the winner default
/// for from_source, composition with ops=, and every malformed-pair refusal (missing half, self-pair, cross-type).</item>
/// <item><b>in-place CopyFrom</b> — the capability the lane never had (it died as an engine-inconsistency wrapper).</item>
/// <item><b>TRANSPORT</b> — format=json is valid JSON carrying the same data, refusals are documents too, and every
/// response (both renders) carries the §2.1.1 epoch.</item>
/// </list>
///
/// Run: <c>dotnet run --project src/housecarl-generator apply-guard</c>
/// </summary>
public static class ApplyGuardProbe
{
    static int _pass, _fail;
    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {Trim(got)}");
        if (ok) _pass++; else _fail++;
    }
    static string Trim(string s) => s.Length <= 400 ? s.Replace("\n", " | ") : s[..400].Replace("\n", " | ") + " …";

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — housecarl_apply (the 2.0 S1 write surface)  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_apply_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // `using`, not a trailing Dispose(): an arm that throws lands in the catch below, and a bare
            // Dispose() call there would be skipped — leaving the LoadOrderService and its plugin overlays
            // OPEN, which then makes the finally's Directory.Delete fail silently. Inside ci-all (one process,
            // many probes) that is a leaked service plus file handles for every failing run.
            using var fx = Fixture.Build(Path.Combine(root, "fx"));
            OpsGrammarArm(fx, root);
            LaneArm(fx);
            ZipArm(fx);
            InPlaceCopyArm(fx);
            TransportArm(fx);

            Console.WriteLine();
            Console.WriteLine($"=== apply-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
            return _fail == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ================= the shared synthetic order =================

    /// <summary>Master + replacer + an OFF-ORDER donor. The replacer WINS the subject weapon (so a copy from the
    /// master genuinely changes a value, and a winner-default source is distinguishable from a named one); a second
    /// weapon is the zip's cross-record SOURCE; an armor is the cross-TYPE refusal's source.</summary>
    sealed class Fixture : IDisposable
    {
        public required LoadOrderService Svc { get; init; }
        public required string SubjectFid { get; init; }      // the weapon the replacer wins (Damage 99, no keywords)
        public required string DonorWeaponFid { get; init; }  // a DIFFERENT weapon (Damage 42, 2 keywords) — the zip source
        public required string ArmorFid { get; init; }        // a different record TYPE — the cross-type refusal source
        public required string ModsDir { get; init; }
        public required string ReplacerPath { get; init; }   // the in-place target's real on-disk file
        public required FormKey DonorKey { get; init; }
        public required string KeywordFid { get; init; }     // a resolvable link value for values=/Add arms
        public required string PotionAFid { get; init; }     // same-file COPY SOURCE for the aliasing arm
        public required string PotionBFid { get; init; }     // its target
        public required FormKey PotionAKey { get; init; }
        public required FormKey PotionBKey { get; init; }
        public required string MasterName { get; init; }
        public required string ReplacerName { get; init; }
        public required FormKey SubjectKey { get; init; }

        public void Dispose() => Svc.Dispose();

        public static Fixture Build(string dir)
        {
            string instance = Path.Combine(dir, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(dir, "game", "Data"));
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(dir, "game").Replace(@"\", @"\\") + ")\r\n");

            var mKey = new ModKey("HcApplyMaster", ModType.Master);
            var rKey = new ModKey("HcApplyRepl", ModType.Plugin);
            var masterPath = Path.Combine(mods, "ApplyMaster", mKey.FileName.String);
            var replPath = Path.Combine(mods, "ApplyRepl", rKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(replPath)!);

            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var k1 = m.Keywords.AddNew(); k1.EditorID = "ApKw1";
            var k2 = m.Keywords.AddNew(); k2.EditorID = "ApKw2";

            var subject = m.Weapons.AddNew();
            subject.EditorID = "ApSubject";
            subject.Name = "Master Sword";
            subject.BasicStats = new WeaponBasicStats { Damage = 10 };

            var donor = m.Weapons.AddNew();
            donor.EditorID = "ApDonor";
            donor.Name = "Donor Sword";
            donor.BasicStats = new WeaponBasicStats { Damage = 42 };
            donor.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
                { new FormLink<IKeywordGetter>(k1.FormKey), new FormLink<IKeywordGetter>(k2.FormKey) };

            // Two potions with modeled Effects lists — the aliasing arm copies A's Effects onto B, then edits B's
            // copy; a shared element would show up as A changing too. Both are overridden by the replacer below so
            // the in-place lane (which edits only what the file OWNS) can touch them.
            var mg = m.MagicEffects.AddNew(); mg.EditorID = "ApMgef";
            var potA = m.Ingestibles.AddNew(); potA.EditorID = "ApPotionA";
            var eA = new Effect { Data = new EffectData { Magnitude = 5 } }; eA.BaseEffect.SetTo(mg.FormKey);
            potA.Effects.Add(eA);
            var potB = m.Ingestibles.AddNew(); potB.EditorID = "ApPotionB";
            var eB = new Effect { Data = new EffectData { Magnitude = 1 } }; eB.BaseEffect.SetTo(mg.FormKey);
            potB.Effects.Add(eB);

            var armor = m.Armors.AddNew();
            armor.EditorID = "ApArmor";
            armor.Name = "Some Cuirass";

            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // the replacer WINS the subject: Damage 99, and no keywords at all
            var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
            var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject);
            rw.Name = "Winner Sword";
            rw.BasicStats = new WeaponBasicStats { Damage = 99 };
            rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();

            // The replacer ALSO overrides the DONOR, at a distinct Damage. Without this the donor's winner IS the
            // master, so "the pole defaulted to the source's winner" and "from_source named the master" would assert
            // the SAME observable and neither would prove anything (review [medium]): a regression that ignored a
            // named from_source, or defaulted to the wrong plugin, would leave both arms green. 7 vs 42 separates them.
            var rd = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, donor);
            rd.BasicStats = new WeaponBasicStats { Damage = 7 };   // keywords carry over from the master (still 2)
            WriteEngine.GenericGetOrAddAsOverride(r, potA);        // the replacer OWNS both potions, so in-place may edit them
            WriteEngine.GenericGetOrAddAsOverride(r, potB);
            r.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+ApplyRepl\r\n+ApplyMaster\r\n");

            var genDir = Path.Combine(dir, "corpus-gen");
            try { _ = CorpusRulebook.LoadCorpus(); }
            catch { CorpusGenerator.GenerateAll(genDir, Path.Combine(dir, "corpus-ref")); CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json"); }

            var store = new UserConfigStore(Path.Combine(dir, "houseCARL.user.json"));
            var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            return new Fixture
            {
                Svc = svc,
                SubjectFid = $"{subject.FormKey.ID:X6}:{mKey.FileName}",
                DonorWeaponFid = $"{donor.FormKey.ID:X6}:{mKey.FileName}",
                ArmorFid = $"{armor.FormKey.ID:X6}:{mKey.FileName}",
                ModsDir = mods,
                ReplacerPath = replPath,
                DonorKey = donor.FormKey,
                KeywordFid = $"{k1.FormKey.ID:X6}:{mKey.FileName}",
                PotionAFid = $"{potA.FormKey.ID:X6}:{mKey.FileName}",
                PotionBFid = $"{potB.FormKey.ID:X6}:{mKey.FileName}",
                PotionAKey = potA.FormKey,
                PotionBKey = potB.FormKey,
                MasterName = mKey.FileName.String,
                ReplacerName = rKey.FileName.String,
                SubjectKey = subject.FormKey,
            };
        }
    }

    /// <summary>Read the subject weapon back off a written patch: (damage, name, keyword count).</summary>
    static (ushort? Dmg, string? Name, int? Kw) ReadSubject(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            var w = ov.Weapons.FirstOrDefault(x => x.FormKey == fk);
            return (w?.BasicStats?.Damage, w?.Name?.String, w?.Keywords?.Count);
        }
        catch { return (null, null, null); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>The written patch's path, parsed out of the text render's first line ("wrote X.esp …"). The render is
    /// what a caller actually gets, so reading the path from it keeps the guard honest about the reported artifact.</summary>
    static string? PatchPathFrom(Fixture fx, string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal) && !render.StartsWith("extended ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        var mod = render.Contains("mod folder: ", StringComparison.Ordinal)
            ? render[(render.IndexOf("mod folder: ", StringComparison.Ordinal) + 12)..].Split('\n')[0].Split("  ")[0].Trim()
            : null;
        return mod is null ? null : Path.Combine(fx.ModsDir, mod, file);
    }

    // ================= ARM 1 — the ops grammar =================
    static void OpsGrammarArm(Fixture fx, string root)
    {
        Console.WriteLine("── ARM 1: the ops grammar — the 2.0 vocabulary, @file, and the strict element reader ──");

        // one op is a set of one — the old set_field call, in the new spelling
        var one = ApplyTools.Apply(fx.Svc, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","value":"55"}]"""),
            patch: "ApOne");
        var onePath = PatchPathFrom(fx, one);
        Check("one op is a set of one: BasicStats.Damage=55 lands in a new patch",
            onePath is not null && ReadSubject(onePath, fx.SubjectKey).Dmg == 55, one);

        // op= is the verb name (§5.1) — an Add on a list, on the winner that has NO keywords
        var kwFid = $"{fx.DonorWeaponFid}";   // any FormID resolvable as a link value is enough for the Add to be legal
        var add = ApplyTools.Apply(fx.Svc, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Keywords","op":"Add","value":"{{kwFid}}"}]"""),
            patch: "ApAdd");
        var addPath = PatchPathFrom(fx, add);
        Check("op= carries the verb: Add on Keywords appends to the winner's empty list",
            addPath is not null && ReadSubject(addPath, fx.SubjectKey).Kw == 1, add);

        // the @file spelling that retired from_file=
        var manifest = Path.Combine(root, "ops.json");
        File.WriteAllText(manifest, $$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","value":"77"}]""");
        var viaFile = ApplyTools.Apply(fx.Svc, ops: Json($"\"@{manifest.Replace("\\", "\\\\")}\""), patch: "ApFile");
        var viaFilePath = PatchPathFrom(fx, viaFile);
        Check("ops=\"@<path>\" reads the SAME array from disk (from_file= retired into the @file convention)",
            viaFilePath is not null && ReadSubject(viaFilePath, fx.SubjectKey).Dmg == 77, viaFile);

        // the one-element ["@path"] spelling, matching how formids= spells it
        var viaFileArr = ApplyTools.Apply(fx.Svc, ops: Json($"[\"@{manifest.Replace("\\", "\\\\")}\"]"), patch: "ApFileArr");
        Check("ops=[\"@<path>\"] — the same convention in the one-element array form formids= uses",
            PatchPathFrom(fx, viaFileArr) is not null, viaFileArr);

        // a mixed inline/@file array has no meaning — refused, not half-honored
        var mixed = ApplyTools.Apply(fx.Svc, ops: Json($$"""["@{{manifest.Replace("\\", "\\\\")}}", {"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"x"}]"""));
        Check("a MIXED inline/@file ops array is refused by name, never half-honored",
            mixed.StartsWith("error:") && mixed.Contains("cannot be mixed with inline elements"), mixed);

        // an undeclared op member is refused BY NAME (the SDK binder would silently DROP it) and carries the §5.3 correction
        var strayVerb = ApplyTools.Apply(fx.Svc, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","verb":"Set","value":"1"}]"""));
        Check("a 1.x 'verb' member inside an op is refused BY NAME (not silently dropped)",
            strayVerb.StartsWith("error:") && strayVerb.Contains("verb"), strayVerb);
        Check("...and the refusal carries the §5.3 correction — the alias layer cannot reach an op's members",
            strayVerb.Contains("the verb member is now op"), strayVerb);

        // nothing to apply at all
        var empty = ApplyTools.Apply(fx.Svc);
        Check("no ops and no zip: refused naming BOTH ways to give work",
            empty.StartsWith("error:") && empty.Contains("ops=") && empty.Contains("bundle="), empty);

        // a bad field path still refuses all-or-nothing with nothing written (the pre-flight contract, unchanged)
        var badPath = ApplyTools.Apply(fx.Svc, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"NoSuchField","value":"1"}]"""), patch: "ApBad");
        Check("a bad field path refuses the whole call with nothing written (all-or-nothing preserved)",
            badPath.StartsWith("error:"), badPath);

        // ROUND-4 FOLD [medium] — the COMPOSED payloads through the NEW strict reader. ReadListParam is a
        // brand-new deserialization path replacing BOTH the SDK binder (1.x inline) and the old from_file=
        // reader, and UnmappedMemberHandling.Disallow applies RECURSIVELY down StructInput -> NestedSet ->
        // StructInput. Nothing above sends one, so the recursive shape was schema-pinned but never executed —
        // and migrating a bulk_apply payload onto apply is exactly what the CHANGELOG instructs.
        // composes= + ReplaceAll over a modeled list, with a NESTED compose arm inside sets[] (a Condition's
        // polymorphic Data), inline:
        string composeOps =
            $$"""[{"formid":"{{fx.PotionAFid}}","field_path":"Effects","op":"ReplaceAll","composes":[""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"11"}]},""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"22"}]},""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"33"}]}]}]""";
        var composed = ApplyTools.Apply(fx.Svc, ops: Json(composeOps), patch: "ApCompose");
        var composedPath = PatchPathFrom(fx, composed);
        Check("composes= + ReplaceAll builds a modeled list through the new strict reader (StructInput -> NestedSet recursion)",
            composedPath is not null && CountEffects(composedPath, fx.PotionAKey) == 3, composed);

        // the SAME payload via @file — the two lanes share one reader, so the file lane must accept it identically
        var composeManifest = Path.Combine(root, "compose-ops.json");
        File.WriteAllText(composeManifest, composeOps);
        var composedFile = ApplyTools.Apply(fx.Svc, ops: Json($"\"@{composeManifest.Replace("\\", "\\\\")}\""), patch: "ApComposeFile");
        var composedFilePath = PatchPathFrom(fx, composedFile);
        Check("...and the IDENTICAL composed payload via ops=\"@<path>\" (ONE reader, both lanes)",
            composedFilePath is not null && CountEffects(composedFilePath, fx.PotionAKey) == 3, composedFile);

        // an undeclared member DEEP inside the recursion (compose.sets[0]) must refuse by name, not be dropped
        var deepStray = ApplyTools.Apply(fx.Svc, ops: Json(
            $$"""[{"formid":"{{fx.PotionAFid}}","field_path":"Effects","op":"Add","compose":""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","value":"1","nosuchmember":"x"}]}}]"""));
        Check("an undeclared member NESTED in compose.sets[0] is refused BY NAME (Disallow reaches the recursion)",
            deepStray.StartsWith("error:") && deepStray.Contains("nosuchmember"), deepStray);

        // values= / entries= — the other two payload members, likewise unexercised until now
        var valuesOp = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Keywords","op":"ReplaceAll","values":["{{fx.KeywordFid}}"]}]"""),
            patch: "ApValues");
        var valuesPath = PatchPathFrom(fx, valuesOp);
        Check("values= drives a list ReplaceAll through the new reader",
            valuesPath is not null && ReadSubject(valuesPath, fx.SubjectKey).Kw == 1, valuesOp);
    }

    /// <summary>Effect count on a potion, off a written patch.</summary>
    static int? CountEffects(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.Ingestibles.FirstOrDefault(x => x.FormKey == fk)?.Effects?.Count;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ================= ARM 2 — the LANE grammar =================
    static void LaneArm(Fixture fx)
    {
        Console.WriteLine("── ARM 2: LANE — three exclusive destinations, each dropped one refused BY NAME (§5.2) ──");

        string OneOp(string v) => $$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","value":"{{v}}"}]""";

        var bothPatchInto = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("1")), patch: "X", into: "Y.esp");
        Check("patch= + into= refused BY NAME (1.x silently IGNORED patch_name under into=)",
            bothPatchInto.StartsWith("error:") && bothPatchInto.Contains("patch=") && bothPatchInto.Contains("into="), bothPatchInto);

        var bothPatchInPlace = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("1")), patch: "X", in_place: fx.ReplacerName);
        Check("patch= + in_place= refused BY NAME",
            bothPatchInPlace.StartsWith("error:") && bothPatchInPlace.Contains("exclusive"), bothPatchInPlace);

        var bothLanes = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("1")), into: "Y.esp", in_place: fx.ReplacerName);
        Check("into= + in_place= refused BY NAME (different lanes, not a fallback)",
            bothLanes.StartsWith("error:") && bothLanes.Contains("Name one"), bothLanes);

        var strayAck = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("1")), acknowledge: true);
        Check("acknowledge= without in_place= refused BY NAME, not ignored",
            strayAck.StartsWith("error:") && strayAck.Contains("meaningless without in_place"), strayAck);

        // in_place is the FILE'S NAME (§5.2) — first touch returns the CONSENT prompt, not an error and not a write
        var consent = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("21")), in_place: fx.ReplacerName);
        Check("in_place=\"X.esp\" enters the lane and the FIRST touch returns the consent prompt (a confirmation, not an error)",
            !consent.StartsWith("error:") && consent.Contains("acknowledge", StringComparison.OrdinalIgnoreCase), consent);

        var done = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("21")), in_place: fx.ReplacerName, acknowledge: true);
        Check("in_place + acknowledge writes the target's OWN file in place",
            done.StartsWith("edited ") && done.Contains(fx.ReplacerName), done);

        // into= extends an existing patch
        var fresh = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("31")), patch: "ApExtend");
        var freshFile = fresh.StartsWith("wrote ") ? fresh[6..].Split(' ')[0] : null;
        var extended = freshFile is null ? "" : ApplyTools.Apply(fx.Svc, ops: Json(OneOp("32")), into: freshFile);
        Check("into= EXTENDS the existing patch rather than writing a fresh one",
            extended.StartsWith("extended "), extended);

        // dry_run on the default lane writes nothing and says so first
        var dry = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("41")), patch: "ApDry", dry_run: true);
        Check("dry_run reports what WOULD change and writes nothing",
            dry.StartsWith("DRY RUN") && dry.Contains("NOTHING was written"), dry);
    }

    // ================= ARM 3 — the §4.5 zip =================
    static void ZipArm(Fixture fx)
    {
        Console.WriteLine("── ARM 3: the §4.5 zip — bundle x assignments as a cross-RECORD copy ──");

        // the load-bearing case: copy a DIFFERENT record's fields onto the subject. The subject's winner has
        // Damage 99 and no keywords; the donor's WINNER (the replacer's override) has Damage 7 and two keywords,
        // while the MASTER's version of the donor has 42 — the two poles are observably different.
        var zip = ApplyTools.Apply(fx.Svc,
            bundle: new[] { "BasicStats.Damage", "Keywords" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}"}]"""),
            patch: "ApZip");
        var zipPath = PatchPathFrom(fx, zip);
        (ushort? Dmg, string? Name, int? Kw) after = zipPath is null ? (null, null, null) : ReadSubject(zipPath, fx.SubjectKey);
        Check($"the zip copies a bundle BETWEEN records: Damage 99 -> 7 and 0 -> 2 keywords (got {after.Dmg}/{after.Kw})",
            after.Dmg == 7 && after.Kw == 2, zip);
        Check("...and leaves everything OUTSIDE the bundle untouched (Name is still the winner's)",
            after.Name == "Winner Sword", zip);

        // from_source omitted ⇒ the SOURCE RECORD's load-order winner (§4.5) — 7, the replacer's override, NOT the
        // master's 42 and NOT the target's own plugin. This is the arm that pins the default's meaning.
        Check($"from_source is optional and defaults to the SOURCE record's winner (7, the replacer's override — not the master's 42) (got {after.Dmg})",
            after.Dmg == 7, zip);

        // a named from_source reads THAT plugin's version instead — 42, distinguishable from the default above
        var poled = ApplyTools.Apply(fx.Svc,
            bundle: new[] { "BasicStats.Damage" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}","from_source":"{{fx.MasterName}}"}]"""),
            patch: "ApZipPole");
        var poledPath = PatchPathFrom(fx, poled);
        var poledDmg = poledPath is null ? null : ReadSubject(poledPath, fx.SubjectKey).Dmg;
        Check($"a named from_source reads THAT plugin's version of the source record (42, the master's — not the winner's 7) (got {poledDmg})",
            poledDmg == 42, poled);

        // half a zip is not a zip
        var bundleOnly = ApplyTools.Apply(fx.Svc, bundle: new[] { "BasicStats.Damage" });
        Check("bundle= without assignments= refused BY NAME",
            bundleOnly.StartsWith("error:") && bundleOnly.Contains("assignments="), bundleOnly);
        var assignOnly = ApplyTools.Apply(fx.Svc, assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}"}]"""));
        Check("assignments= without bundle= refused BY NAME",
            assignOnly.StartsWith("error:") && assignOnly.Contains("bundle="), assignOnly);

        // a self-pair is a no-op, and the refusal teaches the from_source form that DOES mean something
        var selfPair = ApplyTools.Apply(fx.Svc, bundle: new[] { "BasicStats.Damage" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.SubjectFid}}"}]"""));
        Check("target == from refused as a no-op, pointing at from_source= for the version case",
            selfPair.StartsWith("error:") && selfPair.Contains("from_source"), selfPair);

        // the §4.5 same-runtime-record-type gate
        var crossType = ApplyTools.Apply(fx.Svc, bundle: new[] { "Name" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.ArmorFid}}"}]"""), patch: "ApCross");
        Check("a CROSS-TYPE pair (Armor -> Weapon) is refused by name at pre-flight, naming both types",
            crossType.StartsWith("error:") && crossType.Contains("Armor") && crossType.Contains("Weapon"), crossType);

        // an incomplete assignment names its element
        var noFrom = ApplyTools.Apply(fx.Svc, bundle: new[] { "Name" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}"}]"""));
        Check("an assignment missing from= is refused at its own index",
            noFrom.StartsWith("error:") && noFrom.Contains("assignments[0]"), noFrom);

        // REVIEW FOLD [medium]: a zip-generated op must never be refused at an op[i] the caller never wrote.
        // Two real ops here, so a naive index would say "op[2]" — a line that does not exist in the call.
        var badPair = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"a"},{"formid":"{{fx.SubjectFid}}","field_path":"Value","value":"1"}]"""),
            bundle: new[] { "Name" },
            assignments: Json($$"""[{"target":"NOTAFORMID","from":"{{fx.DonorWeaponFid}}"}]"""));
        Check("a bad FormID in an assignment is refused NAMING THE ASSIGNMENT, not a phantom op index",
            badPair.StartsWith("error:") && badPair.Contains("assignments[0]") && !badPair.Contains("op[2]"), badPair);

        // REVIEW FOLD [low]: a mixed inline/@file bundle is named like the other two list inputs, not silently
        // treated as a literal field path.
        var mixedBundle = ApplyTools.Apply(fx.Svc, bundle: new[] { "@C:\\jobs\\paths.json", "Keywords" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}"}]"""));
        Check("a MIXED inline/@file bundle= is refused by name (parity with ops=/assignments=)",
            mixedBundle.StartsWith("error:") && mixedBundle.Contains("cannot be mixed with inline elements"), mixedBundle);

        // REVIEW FOLD [high]: a copy carries no authored value — and that rule must not depend on whether the
        // POLE was named. The from=-without-from_source= shape used to return early past this check and DISCARD
        // the value silently; both spellings must refuse alike.
        foreach (var (label, extra) in new[] { ("without from_source", ""), ("with from_source", $",\"from_source\":\"{fx.MasterName}\"") })
        {
            var valued = ApplyTools.Apply(fx.Svc,
                ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from":"{{fx.DonorWeaponFid}}"{{extra}},"value":"55"}]"""));
            Check($"CopyFrom + value= is refused {label} (the value is never silently discarded)",
                valued.StartsWith("error:") && valued.Contains("takes no value"), valued);
        }

        // the zip COMPOSES with ops= in one call — one patch, both edit sources
        var composed = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"Renamed"}]"""),
            bundle: new[] { "BasicStats.Damage" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}"}]"""),
            patch: "ApBoth");
        var composedPath = PatchPathFrom(fx, composed);
        (ushort? Dmg, string? Name, int? Kw) both = composedPath is null ? (null, null, null) : ReadSubject(composedPath, fx.SubjectKey);
        Check($"ops= and the zip compose in ONE call (got name={both.Name}, dmg={both.Dmg})",
            both.Name == "Renamed" && both.Dmg == 7, composed);
    }

    // ================= ARM 4 — in-place CopyFrom parity =================
    static void InPlaceCopyArm(Fixture fx)
    {
        Console.WriteLine("── ARM 4: CopyFrom on the IN-PLACE lane — the capability the lane never had ──");

        // The replacer OWNS the subject's override, so in-place may edit it. Copying the MASTER's Damage (10) onto
        // the replacer's own record (99) proves the source resolved and the transplant ran. Before W3 this call
        // died as "pre-flight ACCEPTED it but the apply threw" — a capability gap reported as an internal fault.
        var copy = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from_source":"{{fx.MasterName}}"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("CopyFrom composes with in_place= (it previously died as an engine-inconsistency wrapper)",
            copy.StartsWith("edited "), copy);
        Check("...and it is NOT the old internal-fault wording",
            !copy.Contains("pre-flight ACCEPTED it but the apply threw"), copy);

        // the target as its own copy source is a no-op, named as such
        var selfSource = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from_source":"{{fx.ReplacerName}}"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("copying the in-place target's own field onto itself is refused as a no-op",
            selfSource.StartsWith("error:") && selfSource.Contains("no-op"), selfSource);

        // REVIEW FOLD [high] — the LIFETIME case. A CROSS-record copy whose source lives in the in-place target's
        // OWN file: legitimate (not the self-record no-op above), and the source body must come from the mutable
        // targetMod, never the session overlay Phase 4 releases before serialize. Both records are defined by the
        // master but the REPLACER overrides both, so in-place on the replacer owns them: copy the donor's Damage
        // (7, the replacer's own value) onto the subject, in place, and the write must complete AND land 7 —
        // reading through a disposed overlay would yield garbage or throw at serialize.
        var sameFile = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","op":"CopyFrom","from":"{{fx.DonorWeaponFid}}","from_source":"{{fx.ReplacerName}}"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("a CROSS-record copy sourced from the in-place target's OWN file completes (source read from the mutable mod, not the released overlay)",
            sameFile.StartsWith("edited "), sameFile);
        var landed = ReadSubject(fx.ReplacerPath, fx.SubjectKey);
        Check($"...and the copied value actually landed intact in the rewritten file (Damage 7, got {landed.Dmg})",
            landed.Dmg == 7, sameFile);

        // RE-REVIEW FOLD [high] — ALIASING. A same-file copy of a modeled LIST, then an edit to the target's copy.
        // CopyElement shares an element the target's type already accepts; with a live source out of targetMod the
        // two records would share the very same Effect object, so op 2 would silently mutate the SOURCE as well.
        // The source is snapshotted, so it must be untouched. (Both potions are the replacer's own records.)
        var alias = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.PotionBFid}}","field_path":"Effects","op":"CopyFrom","from":"{{fx.PotionAFid}}","from_source":"{{fx.ReplacerName}}"}, {"formid":"{{fx.PotionBFid}}","field_path":"Effects[0].Data.Magnitude","value":"99"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("a same-file LIST copy followed by an edit to the target's copy completes", alias.StartsWith("edited "), alias);
        var (magA, magB) = ReadPotionMagnitudes(fx);
        Check($"...the TARGET took the edit (B magnitude 99, got {magB})", magB == 99, alias);
        Check($"...and the SOURCE record is UNTOUCHED — no shared element aliasing (A magnitude still 5, got {magA})",
            magA == 5, alias);

        // RE-REVIEW FOLD [medium] — ORDERING. A swap on one file: each op must read PRE-CALL state, exactly as the
        // patch lane does. Reading the live mutable mod would leave both records holding the same value.
        // Seed both sides to known, distinct values first so the exchange is unambiguous.
        ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","value":"111"}, {"formid":"{{fx.DonorWeaponFid}}","field_path":"BasicStats.Damage","value":"222"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        var swap = ApplyTools.Apply(fx.Svc,
            bundle: new[] { "BasicStats.Damage" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}","from_source":"{{fx.ReplacerName}}"}, {"target":"{{fx.DonorWeaponFid}}","from":"{{fx.SubjectFid}}","from_source":"{{fx.ReplacerName}}"}]"""),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("an A<->B swap on ONE file completes", swap.StartsWith("edited "), swap);
        var subjAfter = ReadSubject(fx.ReplacerPath, fx.SubjectKey).Dmg;
        var donorAfter = ReadSubject(fx.ReplacerPath, fx.DonorKey).Dmg;
        // Seeded 111 / 222 → a correct swap yields 222 / 111. Reading mid-call state yields 222 / 222 (the first
        // op overwrites the subject, the second then reads the already-overwritten value back onto the donor).
        Check($"...and each record took the OTHER's PRE-CALL value, not a mid-call one (subject={subjAfter} expect 222, donor={donorAfter} expect 111)",
            subjAfter == 222 && donorAfter == 111, swap);
    }

    /// <summary>The two potions' first-effect magnitudes, read off the rewritten in-place file.</summary>
    static (ushort? A, ushort? B) ReadPotionMagnitudes(Fixture fx)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(fx.ReplacerPath, SkyrimRelease.SkyrimSE);
            ushort? Mag(FormKey fk) => (ushort?)ov.Ingestibles.FirstOrDefault(x => x.FormKey == fk)?
                .Effects.FirstOrDefault()?.Data?.Magnitude;
            return (Mag(fx.PotionAKey), Mag(fx.PotionBKey));
        }
        catch { return (null, null); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ================= ARM 5 — TRANSPORT =================
    static void TransportArm(Fixture fx)
    {
        Console.WriteLine("── ARM 5: TRANSPORT — json parity, refusals-as-documents, and the §2.1.1 epoch ──");

        string OneOp(string v) => $$"""[{"formid":"{{fx.SubjectFid}}","field_path":"BasicStats.Damage","value":"{{v}}"}]""";

        var text = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("61")), patch: "ApEpoch");
        Check("every TEXT write render carries epoch=<hex> — the build the winners resolved from (§2.1.1)",
            text.Contains("\nepoch="), text);

        var jsonOut = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("62")), patch: "ApJson", format: "json");
        JsonElement doc = default;
        bool parsed = true;
        try { doc = Json(jsonOut); } catch { parsed = false; }
        Check("format=json emits VALID JSON", parsed, jsonOut);
        if (parsed)
        {
            Check("...ok=true, lane=patch, and the epoch rides in-band (never a silently degraded mode)",
                doc.TryGetProperty("ok", out var ok) && ok.GetBoolean() &&
                doc.TryGetProperty("lane", out var lane) && lane.GetString() == "patch" &&
                doc.TryGetProperty("epoch", out var ep) && ep.ValueKind == JsonValueKind.String, jsonOut);
            Check("...and the read-back's provenance is DATA, not prose (the written file, not load-order truth)",
                !doc.TryGetProperty("readback", out _) || doc.TryGetProperty("readback_source", out _), jsonOut);
        }

        // a REFUSAL is a document too — a json caller must never parse "error: …" out of a string
        var jsonRefusal = ApplyTools.Apply(fx.Svc,
            ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"NoSuchField","value":"1"}]"""),
            patch: "ApJsonBad", format: "json");
        bool refusalIsDoc = false;
        try
        {
            var rd = Json(jsonRefusal);
            refusalIsDoc = rd.TryGetProperty("ok", out var rok) && !rok.GetBoolean() && rd.TryGetProperty("error", out _);
        }
        catch { }
        Check("a json REFUSAL is a document with ok:false + error, not a bare error string", refusalIsDoc, jsonRefusal);

        // the consent prompt is its own flag — a required confirmation is not a failure (Q3)
        var jsonConsent = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("63")), in_place: fx.MasterName, format: "json");
        bool consentFlagged = false;
        try
        {
            var cd = Json(jsonConsent);
            consentFlagged = cd.TryGetProperty("needs_acknowledge", out var na) && na.GetBoolean()
                          && cd.TryGetProperty("confirmation", out _);
        }
        catch { }
        Check("the in-place CONSENT prompt is its own json flag + 'confirmation' key, never an 'error'", consentFlagged, jsonConsent);

        // REVIEW FOLD [medium] — the epoch contract says EVERY outcome decided after the capture carries one:
        // success, refusal, dry run, AND the consent prompt. The prompt and the service-side in-place refusals are
        // decided in LoadOrderService (before the core runs), and were the ones going out unstamped — while
        // "first in_place call" is the single most common shape a caller meets. Both renders are pinned here,
        // because checking only the success renders is exactly how this got through the first time.
        bool consentEpoch = false;
        try { consentEpoch = Json(jsonConsent).TryGetProperty("epoch", out var ce) && ce.ValueKind == JsonValueKind.String; }
        catch { }
        Check("the json CONSENT prompt carries the epoch (decided after a capture ⇒ stamped)", consentEpoch, jsonConsent);

        var textConsent = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("65")), in_place: fx.MasterName);
        Check("the TEXT consent prompt carries the epoch too", textConsent.Contains("\nepoch="), textConsent);

        // ...and the service-side "not an active plugin" refusal, decided off the same capture.
        var notActive = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("66")), in_place: "NotInTheOrder.esp");
        Check("the service-side in-place 'not an active plugin' refusal carries the epoch",
            notActive.StartsWith("error:") && notActive.Contains("\nepoch="), notActive);

        var badFormat = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("64")), format: "yaml");
        Check("an unrecognized format= is refused by name, never a silent fall-through to text",
            badFormat.StartsWith("error:") && badFormat.Contains("format="), badFormat);

        // ROUND-4 FOLD [low] — a json caller must never parse "error: …" out of a string, and the sites hit most
        // are the PRE-ENGINE ones (no outcome exists yet to render). Every refusal after format= is parsed now
        // answers in the requested format; format= itself cannot, since its value is what failed to parse.
        foreach (var (label, call) in new (string, string)[]
        {
            ("a LANE conflict", ApplyTools.Apply(fx.Svc, ops: Json(OneOp("70")), patch: "X", into: "Y.esp", format: "json")),
            ("an undeclared op member", ApplyTools.Apply(fx.Svc, ops: Json($$"""[{"formid":"{{fx.SubjectFid}}","field_path":"Name","verb":"Set","value":"x"}]"""), format: "json")),
            ("half a zip", ApplyTools.Apply(fx.Svc, bundle: new[] { "Name" }, format: "json")),
            ("nothing to apply", ApplyTools.Apply(fx.Svc, format: "json")),
        })
        {
            bool isDocument = false;
            try { isDocument = Json(call).TryGetProperty("error", out _); } catch { }
            Check($"a PRE-ENGINE refusal answers in json when asked — {label}", isDocument, call);
        }

        // ROUND-4 FOLD [low] — an explicitly EMPTY bundle= is a supplied parameter, not an absent one. Reading it
        // as absent is the accepted-and-silently-dropped class; ops=[] already refused by name, so the two agree.
        var emptyBundle = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("71")), bundle: Array.Empty<string>(), patch: "ApEmptyBundle");
        Check("an EMPTY bundle= is refused by name, not silently dropped (parity with ops=[])",
            emptyBundle.StartsWith("error:") && emptyBundle.Contains("bundle="), emptyBundle);

        // ...and the same emptiness rule governs a lane string, so the exclusivity check and what gets written
        // cannot disagree about whether patch= was named.
        var blankPatch = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("72")), patch: "   ", into: "NoSuchPatch.esp");
        Check("a whitespace-only patch= counts as ABSENT for exclusivity, exactly as it does for the write",
            !blankPatch.Contains("the two lanes are exclusive"), blankPatch);
    }
}

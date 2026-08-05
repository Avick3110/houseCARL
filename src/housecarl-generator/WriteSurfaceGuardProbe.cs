using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument, self-contained) for the REST of the 2.0 S1 write surface —
/// <c>housecarl_create</c>, <c>housecarl_remove</c>, <c>housecarl_forward</c> and the migrated
/// <c>housecarl_write_seq</c> (tool-surface-2.0 W3 PR 2; SPEC §2.2 ACT, §5.1/§5.2, §6.1). Sibling of
/// <c>apply-guard</c>, same posture: the REAL end-to-end tool path — a synthetic MO2 instance in temp +
/// <see cref="LoadOrderService"/> + the tool methods themselves — so the wire readers, the LANE grammar, the
/// alias-visible vocabulary and the engines are exercised exactly as a caller hits them. Five arms:
/// <list type="number">
/// <item><b>create grammar</b> — one record is a set of one, the nested one-shot (a same-call sibling parent +
/// an '@editorid' link value), the @file spelling, and the strict element reader's NAMED refusals with the
/// corrections for the members a create cannot have.</item>
/// <item><b>LANE</b> — the destinations are exclusive and a dropped one is refused BY NAME on every tool;
/// removal (which creates no artifact) refuses a call that names NO lane; in_place is the file's NAME with its
/// consent handshake.</item>
/// <item><b>remove, plural</b> — the recovered engine capability: many records dropped in ONE re-serialize, and
/// all-or-nothing when one target isn't carried (NOTHING removed).</item>
/// <item><b>forward</b> — source= (the renamed pole) decides the content, the prior winner is named, dry_run
/// writes nothing, and a non-active source is refused by name rather than read as "doesn't define it".</item>
/// <item><b>TRANSPORT</b> — format=json is valid JSON carrying the same data, a REFUSAL is a document too, and
/// every response carries the §2.1.1 epoch — including write_seq, whose ABSENT epoch is stated as a fact with
/// its reason rather than left as a missing field.</item>
/// </list>
///
/// Run: <c>dotnet run --project src/housecarl-generator write-surface-guard</c>
/// </summary>
public static class WriteSurfaceGuardProbe
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
        Console.WriteLine("################  REGRESSION GUARD — create / remove / forward / write_seq (the 2.0 S1 write surface, PR 2)  ################");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc_write2_guard_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // `using`, not a trailing Dispose(): an arm that throws lands in the catch below, and a bare Dispose()
            // there would be skipped — leaking the service and its overlays, which then makes the finally's
            // Directory.Delete fail silently (apply-guard's own scar).
            using var fx = Fixture.Build(Path.Combine(root, "fx"));
            CreateGrammarArm(fx, root);
            LaneArm(fx);
            RemovePluralArm(fx);
            ForwardArm(fx);
            TransportArm(fx);

            Console.WriteLine();
            Console.WriteLine($"=== write-surface-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
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

    /// <summary>Master + replacer. The replacer WINS the subject weapon at a DIFFERENT damage, so a forward from
    /// the master genuinely changes the content (and "forwarded the master's version" is distinguishable from
    /// "copied the winner"). The replacer is also the in-place target: it owns the records it overrides.</summary>
    sealed class Fixture : IDisposable
    {
        public required LoadOrderService Svc { get; init; }
        public required string SubjectFid { get; init; }     // the weapon: master Damage 10, replacer Damage 99
        public required FormKey SubjectKey { get; init; }
        public required string ModsDir { get; init; }
        public required string MasterName { get; init; }
        public required string ReplacerName { get; init; }

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

            var mKey = new ModKey("HcW2Master", ModType.Master);
            var rKey = new ModKey("HcW2Repl", ModType.Plugin);
            var masterPath = Path.Combine(mods, "W2Master", mKey.FileName.String);
            var replPath = Path.Combine(mods, "W2Repl", rKey.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(masterPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(replPath)!);

            var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
            var subject = m.Weapons.AddNew();
            subject.EditorID = "W2Subject";
            subject.Name = "Master Sword";
            subject.BasicStats = new WeaponBasicStats { Damage = 10 };
            m.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
            var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject);
            rw.Name = "Winner Sword";
            rw.BasicStats = new WeaponBasicStats { Damage = 99 };
            r.BeginWrite.ToPath(replPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n*" + rKey.FileName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+W2Repl\r\n+W2Master\r\n");

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
                SubjectKey = subject.FormKey,
                ModsDir = mods,
                MasterName = mKey.FileName.String,
                ReplacerName = rKey.FileName.String,
            };
        }
    }

    /// <summary>The written artifact's path, parsed out of the text render (the render is what a caller actually
    /// gets, so reading the path from it keeps the guard honest about the reported artifact).</summary>
    static string? ArtifactPathFrom(Fixture fx, string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal) && !render.StartsWith("extended ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        var mod = render.Contains("mod folder: ", StringComparison.Ordinal)
            ? render[(render.IndexOf("mod folder: ", StringComparison.Ordinal) + 12)..].Split('\n')[0].Split("  ")[0].Trim()
            : null;
        return mod is null ? null : Path.Combine(fx.ModsDir, mod, file);
    }

    /// <summary>Every EditorID the written plugin carries (flat + nested), for the created/removed assertions.</summary>
    static List<string> EditorIdsIn(string espPath)
    {
        var found = new List<string>();
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            foreach (var rec in ov.EnumerateMajorRecords())
                if (rec.EditorID is { Length: > 0 } e) found.Add(e);
        }
        catch { /* the caller asserts on the contents, and an unreadable file fails those */ }
        finally { (ov as IDisposable)?.Dispose(); }
        return found;
    }

    static ushort? DamageIn(string espPath, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.Weapons.FirstOrDefault(w => w.FormKey == fk)?.BasicStats?.Damage;
        }
        catch { return null; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    // ================= ARM 1 — the create grammar =================
    static void CreateGrammarArm(Fixture fx, string root)
    {
        Console.WriteLine("── ARM 1: the records grammar — a set of one, the nested one-shot, @file, and the strict reader ──");

        // ONE record is a set of one — the whole reason the scalar create tool dissolves.
        var one = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2KwOne"}]"""), patch: "W2One");
        var onePath = ArtifactPathFrom(fx, one);
        Check("one record is a set of one: a single Keyword lands in a new patch with its editorid",
            onePath is not null && EditorIdsIn(onePath).Contains("W2KwOne"), one);

        // MANY records in one call, with ops= setting fields (the op shape minus formid).
        var many = CreateTools.Create(fx.Svc,
            records: Json("""
                [{"record_type":"Keyword","editorid":"W2KwA"},
                 {"record_type":"Weapon","editorid":"W2WeapA","ops":[{"field_path":"Name","value":"Guard Blade"},
                                                                     {"field_path":"BasicStats.Damage","value":"33"}]}]
                """), patch: "W2Many");
        var manyPath = ArtifactPathFrom(fx, many);
        var manyIds = manyPath is null ? new List<string>() : EditorIdsIn(manyPath);
        Check("many records in ONE call, with ops= setting the new record's fields",
            manyPath is not null && manyIds.Contains("W2KwA") && manyIds.Contains("W2WeapA")
            && many.Contains("Guard Blade", StringComparison.Ordinal), many);

        // The NESTED one-shot: a child whose parent= names an EARLIER sibling's editorid, plus an '@editorid'
        // FormLink value pointing at that same sibling — the two same-call reference forms, in one call.
        var nested = CreateTools.Create(fx.Svc,
            records: Json("""
                [{"record_type":"DialogTopic","editorid":"W2Topic"},
                 {"record_type":"DialogResponses","editorid":"W2Topic_L1","parent":"W2Topic",
                  "ops":[{"field_path":"Topic","value":"@W2Topic"}]}]
                """), patch: "W2Nested");
        var nestedPath = ArtifactPathFrom(fx, nested);
        var nestedIds = nestedPath is null ? new List<string>() : EditorIdsIn(nestedPath);
        Check("the nested one-shot: a child parented on a same-call sibling, with an '@editorid' link value",
            nestedPath is not null && nestedIds.Contains("W2Topic") && nestedIds.Contains("W2Topic_L1"), nested);

        // The @file spelling — the same array from disk (SPEC §5.1's one list-input convention).
        var manifest = Path.Combine(root, "records.json");
        File.WriteAllText(manifest, """[{"record_type":"Keyword","editorid":"W2KwFromFile"}]""");
        var viaFile = CreateTools.Create(fx.Svc, records: Json($"\"@{manifest.Replace("\\", "\\\\")}\""), patch: "W2File");
        var viaFilePath = ArtifactPathFrom(fx, viaFile);
        Check("records=\"@<path>\" reads the SAME array from a JSON manifest on disk",
            viaFilePath is not null && EditorIdsIn(viaFilePath).Contains("W2KwFromFile"), viaFile);

        // A MIXED inline/@file array has no meaning — refused, never half-honored.
        var mixed = CreateTools.Create(fx.Svc,
            records: Json($$"""["@{{manifest.Replace("\\", "\\\\")}}", {"record_type":"Keyword","editorid":"W2Mixed"}]"""));
        Check("a MIXED inline/@file records array is refused by name, never half-honored",
            mixed.StartsWith("error:") && mixed.Contains("cannot be mixed with inline elements"), mixed);

        // records=[] is a SUPPLIED parameter, not an absent one (the accepted-and-dropped class this surface closes).
        var empty = CreateTools.Create(fx.Svc, records: Json("[]"));
        Check("records=[] is refused by name, not read as absent",
            empty.StartsWith("error:") && empty.Contains("empty array"), empty);

        // No records= at all — its own refusal, spelling the parameter.
        var none = CreateTools.Create(fx.Svc, patch: "W2None");
        Check("no records= at all: refused naming the parameter and the @file alternative",
            none.StartsWith("error:") && none.Contains("records=[{record_type"), none);

        // The 1.x element vocabulary: `operations` inside a record spec is refused BY NAME with the rename.
        var oldOps = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Old","operations":[{"field_path":"Name","value":"x"}]}]"""));
        Check("an element member the shape doesn't declare (operations) is refused BY NAME with the ops= correction",
            oldOps.StartsWith("error:") && oldOps.Contains("operations") && oldOps.Contains("ops"), oldOps);

        // A create op cannot carry formid= — and the correction says WHY (the id is allocated), not just "unknown".
        var opFormid = CreateTools.Create(fx.Svc,
            records: Json($$"""[{"record_type":"Keyword","editorid":"W2Bad","ops":[{"formid":"{{fx.SubjectFid}}","field_path":"Name","value":"x"}]}]"""));
        Check("formid= inside a create op is refused BY NAME, corrected with why a create has none",
            opFormid.StartsWith("error:") && opFormid.Contains("formid")
            && opFormid.Contains("auto-allocated", StringComparison.Ordinal), opFormid);

        // A copy pole inside a create op: refused with the "create first, then apply" route (not a bare unknown).
        var opCopy = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Bad2","ops":[{"field_path":"Name","from_source":"HcW2Master.esm"}]}]"""));
        Check("from_source= inside a create op is refused BY NAME with the housecarl_apply route",
            opCopy.StartsWith("error:") && opCopy.Contains("from_source") && opCopy.Contains("housecarl_apply"), opCopy);

        // An engine-level problem names the caller's OWN spelling for the element — records[i], not record[i].
        var badType = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2Ok"},{"record_type":"NotARealType","editorid":"W2Nope"}]"""));
        Check("a per-record refusal names the caller's own spelling: records[1], never the 1.x record[1]",
            badType.StartsWith("error:") && badType.Contains("records[1]") && !badType.Contains("record[1]:"), badType);
    }

    // ================= ARM 2 — the LANE grammar =================
    static void LaneArm(Fixture fx)
    {
        Console.WriteLine("── ARM 2: LANE — exclusive destinations, refused BY NAME, and in_place as the file's name ──");

        var recs = Json("""[{"record_type":"Keyword","editorid":"W2LaneKw"}]""");

        var patchAndInto = CreateTools.Create(fx.Svc, records: recs, patch: "W2A", into: "W2B.esp");
        Check("create: patch= + into= is refused BY NAME (both lanes quoted), never silently ignoring one",
            patchAndInto.StartsWith("error:") && patchAndInto.Contains("patch='W2A'") && patchAndInto.Contains("into='W2B.esp'"), patchAndInto);

        var intoAndInPlace = CreateTools.Create(fx.Svc, records: recs, into: "W2B.esp", in_place: fx.ReplacerName);
        Check("create: into= + in_place= is refused BY NAME — they are different lanes",
            intoAndInPlace.StartsWith("error:") && intoAndInPlace.Contains("into=") && intoAndInPlace.Contains("in_place="), intoAndInPlace);

        var ackAlone = CreateTools.Create(fx.Svc, records: recs, patch: "W2A", acknowledge: true);
        Check("create: acknowledge= without in_place= is refused, not accepted-and-ignored",
            ackAlone.StartsWith("error:") && ackAlone.Contains("acknowledge="), ackAlone);

        var fwdBothLanes = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2F", in_place: fx.ReplacerName);
        Check("forward: patch= + in_place= is refused BY NAME",
            fwdBothLanes.StartsWith("error:") && fwdBothLanes.Contains("patch='W2F'") && fwdBothLanes.Contains("in_place="), fwdBothLanes);

        // Removal creates no artifact, so naming NO lane is a real mistake — and the refusal spells BOTH lanes
        // rather than defaulting to one. (RED pre-fix: a tool that defaulted to a fresh patch here would write an
        // empty artifact and report success.)
        var noLane = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid });
        Check("remove: naming NO lane is refused, spelling both into= and in_place=",
            noLane.StartsWith("error:") && noLane.Contains("into=") && noLane.Contains("in_place="), noLane);

        var twoLanes = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "W2B.esp", in_place: fx.ReplacerName);
        Check("remove: into= + in_place= is refused BY NAME",
            twoLanes.StartsWith("error:") && twoLanes.Contains("Name one"), twoLanes);

        // in_place is the FILE'S NAME, and the first touch of a plugin is a CONSENT prompt — not an error, not a
        // write. Every one of these is decided AFTER the service captured a build to resolve the target, so each
        // carries the §2.1.1 epoch (PR #310's lesson, and its round-1 finding: the consent prompt is the shape a
        // caller meets most often, so an unstamped one makes the contract false exactly there).
        // ORDER MATTERS: consent is persistent per plugin path, and only an acknowledge=true call records it — so
        // the two prompt arms run BEFORE the acknowledged write below, on the same plugin.
        var rmConsent = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, in_place: fx.ReplacerName);
        Check("remove in place: the FIRST touch returns the one-time CONSENT prompt, epoch-stamped",
            !rmConsent.StartsWith("error:") && rmConsent.Contains("acknowledge=true")
            && rmConsent.Contains("\nepoch=", StringComparison.Ordinal), rmConsent);

        var consent = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2InPlaceKw"}]"""), in_place: fx.ReplacerName);
        Check("create in place: the FIRST touch returns the one-time CONSENT prompt, not an error and not a write",
            !consent.StartsWith("error:") && consent.Contains("acknowledge=true"), consent);
        Check("the consent prompt carries the §2.1.1 epoch, like every other outcome",
            consent.Contains("\nepoch=", StringComparison.Ordinal), consent);

        // The forward lane's service-side refusal (a non-active in_place target) is the third site the same stamp
        // covers — a distinct observable, so all three lanes are pinned rather than one standing in for three.
        var fwdBadTarget = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: "NotAPlugin.esp");
        Check("forward in place: a non-active target is refused post-capture, and the refusal carries its epoch",
            fwdBadTarget.StartsWith("error:") && fwdBadTarget.Contains("NotAPlugin.esp")
            && fwdBadTarget.Contains("\nepoch=", StringComparison.Ordinal), fwdBadTarget);

        var wrote = CreateTools.Create(fx.Svc,
            records: Json("""[{"record_type":"Keyword","editorid":"W2InPlaceKw"}]"""), in_place: fx.ReplacerName, acknowledge: true);
        Check("create in place: acknowledge=true writes into the ORIGINAL file, reported as the in-place lane",
            wrote.Contains("IN PLACE", StringComparison.Ordinal)
            && EditorIdsIn(Path.Combine(fx.ModsDir, "W2Repl", fx.ReplacerName)).Contains("W2InPlaceKw"), wrote);
    }

    // ================= ARM 3 — remove, plural =================
    static void RemovePluralArm(Fixture fx)
    {
        Console.WriteLine("── ARM 3: remove — the plural capability the 1.x surface could not reach ──");

        // Author three records into ONE patch, then drop TWO of them in ONE call.
        var made = CreateTools.Create(fx.Svc, patch: "W2Rm", records: Json("""
            [{"record_type":"Keyword","editorid":"W2RmA"},
             {"record_type":"Keyword","editorid":"W2RmB"},
             {"record_type":"Keyword","editorid":"W2RmC"}]
            """));
        var path = ArtifactPathFrom(fx, made);
        if (path is null) { Check("remove arm fixture: three records authored into one patch", false, made); return; }
        var file = Path.GetFileName(path);

        // The created FormIDs come out of the render — the caller's own handle on a record whose id is allocated.
        var ids = FormIdsFrom(made);
        Check("remove arm fixture: three records authored, their allocated FormIDs reported back",
            ids.Count == 3 && EditorIdsIn(path).Count(e => e.StartsWith("W2Rm", StringComparison.Ordinal)) == 3, made);
        if (ids.Count != 3) return;

        var plural = RemoveTools.Remove(fx.Svc, formids: new[] { ids[0], ids[1] }, into: file);
        var left = EditorIdsIn(path);
        Check("MANY records drop in ONE re-serialize (the recovered engine capability): 2 gone, the third stands",
            plural.StartsWith("removed 2 records", StringComparison.Ordinal)
            && !left.Contains("W2RmA") && !left.Contains("W2RmB") && left.Contains("W2RmC"), plural);

        // All-or-nothing: one target the patch doesn't carry refuses the WHOLE call — the survivor stays.
        var notCarried = RemoveTools.Remove(fx.Svc, formids: new[] { ids[2], fx.SubjectFid }, into: file);
        Check("all-or-nothing: one not-carried target refuses the whole call and NOTHING is removed",
            notCarried.StartsWith("error:") && notCarried.Contains("not carried by patch")
            && EditorIdsIn(path).Contains("W2RmC"), notCarried);

        var emptyList = RemoveTools.Remove(fx.Svc, formids: Array.Empty<string>(), into: file);
        Check("formids=[] is refused by name (a set of one is the minimum, not zero)",
            emptyList.StartsWith("error:") && emptyList.Contains("formids="), emptyList);
    }

    /// <summary>The allocated FormIDs out of a create render's per-record lines ("  Keyword 000800:Patch.esp  Edid").</summary>
    static List<string> FormIdsFrom(string render)
    {
        var ids = new List<string>();
        foreach (var line in render.Split('\n'))
        {
            var t = line.Trim();
            var parts = t.Split("  ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var head = parts[0].Split(' ');
            if (head.Length == 2 && head[1].Contains(':') && head[1].Contains(".es", StringComparison.OrdinalIgnoreCase))
                ids.Add(head[1]);
        }
        return ids;
    }

    // ================= ARM 4 — forward =================
    static void ForwardArm(Fixture fx)
    {
        Console.WriteLine("── ARM 4: forward — source= decides the content, and the winner it out-ranks is named ──");

        // The replacer WINS the subject at Damage 99; forwarding the MASTER's version must land Damage 10 — which
        // is what tells "copied source's body" apart from "copied the winner".
        var fwd = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2Fwd");
        var fwdPath = ArtifactPathFrom(fx, fwd);
        Check("source= decides the content: the MASTER's version lands, not the load-order winner's",
            fwdPath is not null && DamageIn(fwdPath, fx.SubjectKey) == 10, fwd);
        Check("the render names the winner the forward will out-rank once enabled",
            fwd.Contains("out-ranks the current winner", StringComparison.Ordinal)
            && fwd.Contains(fx.ReplacerName, StringComparison.OrdinalIgnoreCase), fwd);

        // A forward whose version is ALREADY winning is reported redundant, never a silent no-op.
        var redundant = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.ReplacerName, patch: "W2FwdR");
        Check("forwarding the version that ALREADY wins is flagged redundant, never silently a no-op",
            redundant.Contains("already the load-order winner", StringComparison.OrdinalIgnoreCase), redundant);

        // dry_run runs the real pipeline and stops before disk.
        int before = Directory.GetDirectories(fx.ModsDir).Length;
        var dry = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName, patch: "W2FwdDry", dry_run: true);
        Check("dry_run=true: the DRY RUN render leads with nothing-written, and no mod folder is cut",
            dry.StartsWith("DRY RUN", StringComparison.Ordinal) && Directory.GetDirectories(fx.ModsDir).Length == before, dry);

        // A source that isn't active is refused BY NAME — not silently read as "doesn't define the record".
        var badSource = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: "NotInTheOrder.esp", patch: "W2FwdBad");
        Check("a non-active source= is refused by name (the declared bound of this lane's pole)",
            badSource.StartsWith("error:") && badSource.Contains("NotInTheOrder.esp"), badSource);

        var noSource = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, patch: "W2FwdNo");
        Check("forward without source= is refused naming the parameter and what it means",
            noSource.StartsWith("error:") && noSource.Contains("source="), noSource);
    }

    // ================= ARM 5 — TRANSPORT =================
    static void TransportArm(Fixture fx)
    {
        Console.WriteLine("── ARM 5: TRANSPORT — json documents (refusals included) and the §2.1.1 epoch everywhere ──");

        // A SUCCESS renders as valid json carrying the same facts.
        var createJson = CreateTools.Create(fx.Svc, patch: "W2Json",
            records: Json("""[{"record_type":"Keyword","editorid":"W2JsonKw"}]"""), format: "json");
        Check("create format=json: a valid document with ok/lane/created and the epoch",
            TryJson(createJson, out var cdoc)
            && cdoc!.RootElement.GetProperty("ok").GetBoolean()
            && cdoc.RootElement.GetProperty("created")[0].GetProperty("editorid").GetString() == "W2JsonKw"
            && cdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, createJson);

        // A REFUSAL is a document too — a json caller must never have to parse "error: …" out of a string. (This
        // is the pre-engine refusal path, which is exactly where PR #306/#310 found an EMPTY string twice.)
        // NOTE the shape: a PRE-ENGINE refusal renders through JsonWire.RenderError, which carries {error, epoch}
        // and NOT the `ok` discriminant the outcome-borne renders emit. That asymmetry is the known, reviewer-
        // scoped-out gap filed in dev/BACKLOG.md (a ~39-call-site sweep, W3 PR 3) — so this arm asserts the
        // REASON is present and machine-readable, and deliberately does not assert `ok` on this path. When the
        // sweep lands, these two asserts tighten to ok:false.
        var createRefusal = CreateTools.Create(fx.Svc, records: Json("[]"), format: "json");
        Check("create format=json: a REFUSAL is a document carrying the reason, never an empty string",
            createRefusal.Length > 0 && TryJson(createRefusal, out var rdoc)
            && rdoc!.RootElement.GetProperty("error").GetString()!.Contains("empty array"), createRefusal);

        var removeRefusal = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, format: "json");
        Check("remove format=json: the no-lane refusal is a document too",
            removeRefusal.Length > 0 && TryJson(removeRefusal, out var rmdoc)
            && rmdoc!.RootElement.GetProperty("error").GetString()!.Contains("in_place="), removeRefusal);

        var forwardJson = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2FwdJson", format: "json");
        Check("forward format=json: forwarded rows carry source, prior_winner and the two per-record flags",
            TryJson(forwardJson, out var fdoc)
            && fdoc!.RootElement.GetProperty("forwarded")[0].GetProperty("source").GetString() == fx.MasterName
            && fdoc.RootElement.GetProperty("forwarded")[0].TryGetProperty("was_already_winner", out _)
            && fdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, forwardJson);

        // An OUTCOME-borne refusal (past the tool layer, into the service) renders through the outcome renderer,
        // so it DOES carry ok:false. The epoch splits by WHERE the refusal was decided, which is the §2.1.1
        // contract stated positively rather than "epoch everywhere":
        //   * a LANE-resolution refusal (no such patch to extend) is decided off the mod FOLDERS, before any
        //     build is consulted — epoch NULL, because stamping it would claim evidence never read;
        var noSuchPatch = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "NoSuchPatch.esp", format: "json");
        Check("a lane-resolution refusal is a document with ok:false and a NULL epoch (it consulted no build)",
            TryJson(noSuchPatch, out var nsdoc)
            && !nsdoc!.RootElement.GetProperty("ok").GetBoolean()
            && nsdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.Null, noSuchPatch);

        //   * a refusal decided INSIDE the engine, after the capture, carries that build's stamp. (Authored here
        //     rather than reusing arm 3's patch so the two arms cannot pass on one observable.)
        var made = CreateTools.Create(fx.Svc, patch: "W2Epoch", records: Json("""[{"record_type":"Keyword","editorid":"W2EpochKw"}]"""));
        var madePath = ArtifactPathFrom(fx, made);
        var notCarried = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid },
            into: madePath is null ? "W2Epoch.esp" : Path.GetFileName(madePath), format: "json");
        Check("a refusal decided AFTER the engine's capture carries that build's epoch",
            TryJson(notCarried, out var ncdoc)
            && !ncdoc!.RootElement.GetProperty("ok").GetBoolean()
            && ncdoc.RootElement.GetProperty("error").GetString()!.Contains("not carried by patch")
            && ncdoc.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.String, notCarried);

        // write_seq: the ABSENT epoch is a stated fact with its reason, not a dropped field.
        var seqJson = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, format: "json");
        Check("write_seq format=json: epoch is explicitly null AND carries why (no build is consulted at all)",
            TryJson(seqJson, out var sdoc)
            && sdoc!.RootElement.GetProperty("epoch").ValueKind == JsonValueKind.Null
            && sdoc.RootElement.GetProperty("epoch_note").GetString()!.Contains("load-order-independent"), seqJson);

        // write_seq text: a plugin with no SGE quests reports the clean no-op AND names which copy it read.
        var seqText = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName);
        Check("write_seq text: the no-SGE-quests no-op names the file AND the copy it was read from",
            seqText.Contains("no start-game-enabled quests", StringComparison.Ordinal)
            && seqText.Contains("read from", StringComparison.Ordinal), seqText);
    }

    static bool TryJson(string s, out JsonDocument? doc)
    {
        try { doc = JsonDocument.Parse(s); return true; }
        catch { doc = null; return false; }
    }
}

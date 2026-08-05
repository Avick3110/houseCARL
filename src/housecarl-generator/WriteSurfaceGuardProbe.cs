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

    /// <summary>Pull the read-back call a truncated create render emits back apart into (source file, types) so the
    /// arm can RUN it. Returns (null, null) when the render carries no such call — which is itself the failure the
    /// caller reports, since the whole point of the notice is to name a call that works.</summary>
    static (string? file, string[]? types) ParseReadBackCall(string render)
    {
        const string marker = "housecarl_records source=\"";
        int at = render.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return (null, null);
        var tail = render[(at + marker.Length)..];
        int quote = tail.IndexOf('"');
        int open = tail.IndexOf("types=[", StringComparison.Ordinal);
        int close = open < 0 ? -1 : tail.IndexOf(']', open);
        if (quote < 0 || open < 0 || close < 0) return (null, null);
        var types = tail[(open + 7)..close].Split(',')
                        .Select(t => t.Trim().Trim('"')).Where(t => t.Length > 0).ToArray();
        return (tail[..quote], types.Length > 0 ? types : null);
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

        // The closing "keep going" line must teach THIS tool's spelling (PR #311 round-2 review [medium]): the 2.0
        // tools declare a single string in_place= and no target=, so the 1.x pair would send a caller to an
        // undeclared parameter plus a boolean-into-a-string. Asserted as a positive AND a negative — the positive
        // alone would still pass if the old sentence were merely appended.
        Check("the in-place follow-up hint teaches in_place=\"X.esp\", never the 1.x target= + in_place=true pair",
            wrote.Contains($"pass in_place=\"{fx.ReplacerName}\" again", StringComparison.Ordinal)
            && !wrote.Contains("target=", StringComparison.Ordinal), wrote);
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

        // `lane` names the lane the CALL asked for, on a refusal as much as on a success (PR #311 review
        // [medium]): Fail/NeedsAck leave InPlace/Extended at their defaults, so a lane DERIVED from the outcome
        // reported "patch" for a consent prompt that exists only because the caller named in_place=, and for an
        // into= refusal on a call that named no patch= at all.
        // The CONSENT PROMPT is the reviewer's own example, and the sharpest case: it exists ONLY because the
        // caller named in_place=. The master has not been acknowledged (ARM 2 acknowledged the replacer), so this
        // is a real first touch.
        var lanePrompt = CreateTools.Create(fx.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw2"}]"""),
            in_place: fx.MasterName, format: "json");
        Check("json lane on the in-place CONSENT PROMPT says in_place, not the patch lane the caller never named",
            TryJson(lanePrompt, out var lpdoc)
            && lpdoc!.RootElement.GetProperty("needs_acknowledge").GetBoolean()
            && lpdoc.RootElement.GetProperty("lane").GetString() == "in_place", lanePrompt);

        var laneRefusal = RemoveTools.Remove(fx.Svc, formids: new[] { fx.SubjectFid }, into: "NoSuchPatch2.esp", format: "json");
        Check("json lane on an into= REFUSAL says into, not the patch lane (Fail leaves the outcome flags at default)",
            TryJson(laneRefusal, out var ldoc) && ldoc!.RootElement.GetProperty("lane").GetString() == "into", laneRefusal);

        var laneInPlace = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            in_place: "NotAPlugin.esp", format: "json");
        Check("json lane on a service-side in-place refusal says in_place too",
            TryJson(laneInPlace, out var lidoc) && lidoc!.RootElement.GetProperty("lane").GetString() == "in_place", laneInPlace);

        // ONE lane vocabulary across all four tools (PR #311 round-2 review [low]): the value NAMES the parameter
        // that selected the lane, so an into= call answers "into" everywhere — a json client that learned the
        // words from apply must not fall into its unknown branch on remove (or the reverse, as it did when apply
        // said "extend" and remove said "into" for the same lane).
        var laneCreateInto = CreateTools.Create(fx.Svc, records: Json("""[{"record_type":"Keyword","editorid":"W2LaneKw3"}]"""),
            into: "NoSuchPatch3.esp", format: "json");
        var laneFwdInto = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            into: "NoSuchPatch3.esp", format: "json");
        Check("the into= lane is spelled the same on create / forward / remove — the parameter's own name",
            TryJson(laneCreateInto, out var lcdoc) && lcdoc!.RootElement.GetProperty("lane").GetString() == "into"
            && TryJson(laneFwdInto, out var lfdoc) && lfdoc!.RootElement.GetProperty("lane").GetString() == "into",
            laneCreateInto + " || " + laneFwdInto);

        // The three post-write REPORTS are inside the json budget (PR #311 round-2 review [low-medium]). Rendered
        // DIRECTLY off a synthetic outcome rather than through a create call: a report big enough to blow a
        // ceiling means dozens of voiced lines, and building those in the fixture would pin the budget behind a
        // pile of unrelated dialogue plumbing. The claim under test is the RENDERER's — the reports were outside
        // the cap and the document still closed with truncated:false.
        var voiced = Enumerable.Range(0, 40).Select(i => new VoiceLine(
            default, "W2VoiceTopic", i,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.fuz", false, null, false,
            $@"sound\voice\W2.esp\MaleNord\W2VoiceTopic_{i:D4}.lip", false,
            false)).ToList();
        var synthetic = new WritePatchBuilder.CreateOutcome(
            true, null, @"C:\mods\W2Rep\W2Rep.esp", false,
            new[] { new WritePatchBuilder.CreatedRecord(default, "DialogResponses", "W2RepL1", Array.Empty<WritePatchBuilder.OpResult>()) },
            Array.Empty<string>(), 512)
            { Epoch = "deadbeefdeadbeef", Voice = new VoiceReport(voiced, Array.Empty<VoiceUndetermined>()) };

        var reportFull = JsonWire.RenderCreateOutcome(synthetic, 0, false, "patch");
        Check("json create: the voice-coverage report is emitted in full when the budget allows (truncated:false)",
            TryJson(reportFull, out var rfdoc)
            && rfdoc!.RootElement.GetProperty("voice_coverage").GetProperty("lines").GetArrayLength() == 40
            && !rfdoc.RootElement.GetProperty("truncated").GetBoolean(), reportFull);

        var reportCapped = JsonWire.RenderCreateOutcome(synthetic, 1200, false, "patch");
        Check("json create: a ceiling the REPORTS blow past is reported as truncated:true, with the rows dropped",
            TryJson(reportCapped, out var rcdoc)
            && rcdoc!.RootElement.GetProperty("truncated").GetBoolean()
            && rcdoc.RootElement.GetProperty("voice_coverage").GetProperty("lines").GetArrayLength() < 40, reportCapped);

        // The create hazard does not care which transport asked (PR #311 review 4 [medium]): the text twin was moved
        // off "raise max_chars to see the rest" one fold earlier and the json document kept it, so a json client
        // could raise the ceiling, re-issue, and allocate the records a second time. D2 — same remedy, both renders.
        Check("json create: truncated_note points at the read-back call, never at raising max_chars",
            rcdoc is not null
            && rcdoc.RootElement.GetProperty("truncated_note").GetString() is { } jnote
            && jnote.Contains("housecarl_records source=", StringComparison.Ordinal)
            && jnote.Contains("types=[", StringComparison.Ordinal)
            && jnote.Contains("allocates the records AGAIN", StringComparison.Ordinal)
            && !jnote.Contains("raise max_chars", StringComparison.Ordinal), reportCapped);

        // max_chars reaches the TEXT render too, not only json (PR #311 review [medium] / [low-medium]): the
        // parameter's own description promises trailing rows drop with an explicit notice, and removal is
        // set-valued, so the unbounded list is the expected case rather than an edge.
        var capMade = CreateTools.Create(fx.Svc, patch: "W2Cap", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CapA"},
             {"record_type":"Keyword","editorid":"W2CapB"},
             {"record_type":"Keyword","editorid":"W2CapC"}]
            """));
        var capPath = ArtifactPathFrom(fx, capMade);
        var capIds = FormIdsFrom(capMade);
        if (capPath is not null && capIds.Count == 3)
        {
            var capped = RemoveTools.Remove(fx.Svc, formids: capIds.ToArray(), into: Path.GetFileName(capPath), max_chars: 100);
            Check("remove text render: max_chars= drops trailing rows with an explicit notice (never a silent host cut)",
                capped.Contains("[truncated:", StringComparison.Ordinal)
                && capped.Contains("max_chars=100", StringComparison.Ordinal)
                && capped.Contains("every one WAS removed", StringComparison.Ordinal), capped);
        }
        else Check("remove text render: fixture for the max_chars arm", false, capMade);

        // The SAME budget on the two remaining set-valued row blocks (PR #311 review 3 [medium] x2): create's
        // created-records block is the render's largest, forward's rows are the longest, and both json twins
        // already truncate the identical arrays — so an unbounded text lane made the two renders disagree about
        // the same call, with text taking the silent host-side cut.
        var createCapped = CreateTools.Create(fx.Svc, patch: "W2CreCap", max_chars: 130, records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreCapA"},
             {"record_type":"Keyword","editorid":"W2CreCapB"},
             {"record_type":"Keyword","editorid":"W2CreCapC"}]
            """));
        Check("create text render: max_chars= drops trailing created rows with an explicit notice",
            createCapped.Contains("[truncated:", StringComparison.Ordinal)
            && createCapped.Contains("every one WAS created", StringComparison.Ordinal)
            && !createCapped.Contains("W2CreCapC", StringComparison.Ordinal), createCapped);

        // The remedy must be a READ, never "re-run this call" (PR #311 review 3 round-2 [medium]): repeating a
        // truncated CREATE allocates the records a second time — a second auto-suffixed patch, or under into= a
        // re-create at the same FormID with the prior contents discarded. Asserted as a positive AND the absence
        // of the sibling renders' wording, which is what made this dangerous here.
        // …and the remedy must be a call records ACCEPTS (PR #311 review 4 [medium]): source= is the SOURCE pole,
        // not a SELECT term, so the first spelling of this notice named a call that dies on "select something" —
        // leaving re-issuing the create as the only obvious route, i.e. straight back into the trap. The SELECT
        // term is asserted by name here and EXERCISED two arms below.
        Check("create's truncation notice points at a READ, never at raising max_chars (a repeat would re-create)",
            createCapped.Contains("housecarl_records source=", StringComparison.Ordinal)
            && createCapped.Contains("types=[\"Keyword\"]", StringComparison.Ordinal)
            && createCapped.Contains("would create them AGAIN", StringComparison.Ordinal)
            && !createCapped.Contains("raise max_chars", StringComparison.Ordinal), createCapped);

        // The remedy EXERCISED — parsed OUT of the notice this render just produced and RUN, rather than compared
        // against a literal. That is the difference that matters here: the arm this replaces asserted the string
        // "housecarl_records source=", which is precisely why CI vouched for a call records refuses. An arm that
        // executes the emitted call cannot go stale against a reworded remedy.
        var (remedyFile, remedyTypes) = ParseReadBackCall(createCapped);
        if (remedyFile is not null && remedyTypes is { Length: > 0 })
        {
            var remedy = RecordsTools.Records(fx.Svc, source: Json($"\"{remedyFile}\""), types: remedyTypes);
            Check("create's truncation remedy, RUN as emitted, resolves and returns the row the render cut",
                !remedy.StartsWith("error:", StringComparison.Ordinal)
                && remedy.Contains("W2CreCapC", StringComparison.Ordinal), remedy);

            // …and the SELECT term is load-bearing, not decoration: the same call with source= alone — the shape
            // the notice used to name — dies on the lane decision. This is the fact the old remedy walked into.
            var bareSource = RecordsTools.Records(fx.Svc, source: Json($"\"{remedyFile}\""));
            Check("records: source= ALONE selects nothing, so a remedy without a SELECT term is a dead end",
                bareSource.StartsWith("error:", StringComparison.Ordinal)
                && bareSource.Contains("select something", StringComparison.Ordinal), bareSource);
        }
        else Check("create's truncation notice emits a parseable source=+types= read-back call", false, createCapped);

        // …and with a cap so small that NO row renders, the closing line must not point at "the new FormID above".
        var createAllCut = CreateTools.Create(fx.Svc, patch: "W2CreCut", max_chars: 1, records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreCutA"},
             {"record_type":"Keyword","editorid":"W2CreCutB"}]
            """));
        Check("create text render: with EVERY row cut, the render stops claiming a FormID it never printed",
            createAllCut.Contains("truncated: 0 of 2", StringComparison.Ordinal)
            && !createAllCut.Contains("the new FormID above", StringComparison.Ordinal)
            && createAllCut.Contains("all 2 WERE created", StringComparison.Ordinal), createAllCut);

        var createUncapped = CreateTools.Create(fx.Svc, patch: "W2CreFull", records: Json("""
            [{"record_type":"Keyword","editorid":"W2CreFullA"},
             {"record_type":"Keyword","editorid":"W2CreFullB"},
             {"record_type":"Keyword","editorid":"W2CreFullC"}]
            """));
        Check("create text render: without a cap every created row is listed",
            createUncapped.Contains("W2CreFullC", StringComparison.Ordinal)
            && !createUncapped.Contains("[truncated:", StringComparison.Ordinal), createUncapped);

        var fwdCapped = ForwardTools.Forward(fx.Svc, formids: new[] { fx.SubjectFid }, source: fx.MasterName,
            patch: "W2FwdCap", max_chars: 120);
        Check("forward text render: max_chars= drops trailing forwarded rows with an explicit notice",
            fwdCapped.Contains("[truncated:", StringComparison.Ordinal)
            && fwdCapped.Contains("every one WAS forwarded", StringComparison.Ordinal), fwdCapped);

        // write_seq's text lane, same contract — asserted on a REAL quest list rather than the fixture's
        // no-SGE plugin, because an arm that never renders a row cannot pin a row budget (the happy-path-only
        // scar). The render is exercised directly over a synthetic outcome: three quests, a cap that fits one.
        var seqOutcome = new SeqOutcome(true, null, @"C:\mods\HcSeq\SEQ\HcSeq.seq", "HcSeq",
            new[]
            {
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestAlpha",   0x01000800),
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestBravo",   0x01000801),
                new HousecarlCore.SeqFile.SeqQuest(default, "HcSeqQuestCharlie", 0x01000802),
            },
            "HcSeq.esp", false);
        var seqCapped = SeqTools.Render(seqOutcome, maxChars: 80);
        Check("write_seq text render: max_chars= drops trailing quest rows with an explicit notice",
            seqCapped.Contains("[truncated:", StringComparison.Ordinal)
            && seqCapped.Contains("max_chars=80", StringComparison.Ordinal)
            && seqCapped.Contains("the .seq itself carries ALL of them", StringComparison.Ordinal)
            && !seqCapped.Contains("HcSeqQuestCharlie", StringComparison.Ordinal), seqCapped);
        // …and the notice must not prescribe a re-run: widening the ceiling re-runs a WRITE, which with no lane
        // named for a plugin outside a houseCARL folder cuts a SECOND auto-suffixed folder holding a duplicate
        // .seq. Nothing is missing from the file, so the notice says that and prices the re-run instead.
        Check("write_seq's truncation notice prices the re-run instead of prescribing 'raise max_chars'",
            seqCapped.Contains("nothing is missing from the FILE", StringComparison.Ordinal)
            && seqCapped.Contains("writes the .seq again", StringComparison.Ordinal)
            && !seqCapped.Contains("raise max_chars", StringComparison.Ordinal), seqCapped);

        var seqUncapped = SeqTools.Render(seqOutcome);
        Check("write_seq text render: without a cap every quest row is listed (the notice is not a permanent cut)",
            seqUncapped.Contains("HcSeqQuestCharlie", StringComparison.Ordinal)
            && !seqUncapped.Contains("[truncated:", StringComparison.Ordinal), seqUncapped);

        // LANE exclusivity on write_seq (PR #311 review 4 [low]): both spellings are labelled LANE: by this PR, and
        // ResolvePatchModFolder returns from the into= branch before patch= is read — so the pair used to land the
        // .seq in into='s folder with patch= silently dropped. Refused BY NAME like every sibling, and in BOTH
        // transports: a json caller getting prose here is the same class one layer up.
        var seqBothLanes = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqNew", into: "HcSeqExisting.esp");
        Check("write_seq: patch= and into= together are refused BY NAME, never silently resolved to into=",
            seqBothLanes.StartsWith("error:", StringComparison.Ordinal)
            && seqBothLanes.Contains("HcSeqNew", StringComparison.Ordinal)
            && seqBothLanes.Contains("HcSeqExisting.esp", StringComparison.Ordinal)
            && seqBothLanes.Contains("exclusive", StringComparison.Ordinal), seqBothLanes);

        // Same pre-engine RenderError shape as the create/remove refusal arms above — {error, epoch}, no `ok`
        // discriminant (the reviewer-scoped-out W3 PR 3 sweep), so this asserts the REASON is machine-readable and
        // tightens to ok:false when that lands.
        var seqBothLanesJson = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqNew",
            into: "HcSeqExisting.esp", format: "json");
        // TryGetProperty, not GetProperty: without the fix this document is a SUCCESS (patch= silently dropped),
        // which carries no `error` at all — an arm that throws there reports a crashed probe instead of the finding.
        Check("write_seq format=json: the LANE refusal is a DOCUMENT carrying the reason, not prose",
            seqBothLanesJson.Length > 0 && TryJson(seqBothLanesJson, out var sldoc)
            && sldoc!.RootElement.TryGetProperty("error", out var slerr)
            && slerr.GetString() is { } slmsg && slmsg.Contains("exclusive", StringComparison.Ordinal),
            seqBothLanesJson);

        // …and a single lane still reaches the engine — the refusal must be the PAIR, not "patch= is refused".
        var seqOneLane = SeqTools.WriteSeq(fx.Svc, source: fx.MasterName, patch: "HcSeqOnlyNew");
        Check("write_seq: patch= ALONE is still honored (the refusal is the pair, not the parameter)",
            !seqOneLane.StartsWith("error:", StringComparison.Ordinal), seqOneLane);

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

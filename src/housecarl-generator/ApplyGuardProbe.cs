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
/// pre-flight and the apply engine are exercised exactly as a caller hits them. EIGHT arms:
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
/// <item><b>the in-place verify's own honesty</b> (#308) — the per-op "what landed" clause is re-derived from the
/// WRITTEN FILE (json: <c>landed_on_disk</c> + <c>landed_source</c>), a compose with nothing to serialize
/// is refused before the file is touched instead of reported as landed, and the memory-vs-file comparator's own
/// semantics are pinned as a unit.</item>
/// <item><b>keyed multi-op</b> (#308) — two COUNT-CHANGING key-addressed ops in one in-place call both land and
/// neither is reported as not landed (the arm that would have caught the over-broad key exemption).</item>
/// <item><b>the verify's wiring</b> (#308) — the keyed exemption's own purpose (two SetAtIndex on different elements
/// each keep their verification) and the comparator's WIRE into the verify pass, both of which a review found
/// deletable with every probe still green.</item>
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
            EmptyComposeArm(fx);
            KeyedMultiOpArm(fx);
            VerifyWiringArm(fx);

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

    /// <summary>ARM 6 (#308) — the in-place verify's per-op clause is the WRITTEN FILE's answer, a compose with
    /// nothing to serialize is refused instead of reported as landed, and a MULTI-OP call is not slandered.
    /// <para>The last of those is here because an earlier draft of this doc claimed the divergence render had "no
    /// synthesizable producer left" — false, and a review found it by synthesizing one in this very fixture: two
    /// Adds to one list, where op 1's mid-sequence reading was compared against the file's final state and a correct
    /// write was reported as NOT landed. A guard that argues its way out of covering the one thing a caller sees is
    /// how that shipped, so the multi-op case is now driven end-to-end here.</para>
    /// <para>What stays unit-pinned is the comparator itself. And what it does NOT claim, since a review proved the
    /// second probe inert: an element that lands but serializes with fewer fields than supplied is not reported —
    /// telling that from the format representing a value its own way is what produced the earlier false alarms. The
    /// unit checks below pin both the two rules it keeps and the two shapes it must stay silent about.</para></summary>
    static void EmptyComposeArm(Fixture fx)
    {
        Console.WriteLine("── ARM 6: #308 — the verify's clause comes off the FILE, and an empty compose is refused ──");

        // The SIZE alone proves nothing here, and that is the whole point of the bug: pre-fix the call re-serialized
        // the target and the file came out byte-identical, because the op contributed nothing. The discriminating
        // observable is whether the file was WRITTEN AT ALL — so the timestamp is what this arm watches.
        var before = (new FileInfo(fx.ReplacerPath).Length, File.GetLastWriteTimeUtc(fx.ReplacerPath));
        var empty = ApplyTools.Apply(fx.Svc,
            ops: Json(ComposeRankOp(fx.FactionFid, null)),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("a compose with NO fields whose struct serializes to nothing is REFUSED, not reported as landed",
            empty.StartsWith("error:") && empty.Contains("no serializable content", StringComparison.Ordinal), empty);
        Check("…and the refusal names the settable fields from the TYPE, so the caller knows what to set",
            empty.Contains("Settable fields on Rank:", StringComparison.Ordinal)
            && empty.Contains("Number", StringComparison.Ordinal), empty);
        var after = (new FileInfo(fx.ReplacerPath).Length, File.GetLastWriteTimeUtc(fx.ReplacerPath));
        Check("…and it is a PRE-SERIALIZE refusal: the in-place target was never rewritten (size AND mtime)",
            after == before, $"{before} -> {after}");

        // The same compose WITH content lands — the refusal is about emptiness, not about composing Ranks.
        var withField = ApplyTools.Apply(fx.Svc,
            ops: Json(ComposeRankOp(fx.FactionFid, "\"fields\":{\"Number\":\"0\"}")),
            in_place: fx.ReplacerName, acknowledge: true, format: "json");
        JsonElement op0 = default;
        try
        {
            var doc = Json(withField);
            if (doc.TryGetProperty("ops", out var opsArr) && opsArr.GetArrayLength() == 1) op0 = opsArr[0];
        }
        catch { /* op0 stays Undefined — every check below then fails with the render as evidence */ }
        Check("the same compose WITH a field lands, and its clause is the FILE's (landed_on_disk present)",
            op0.ValueKind == JsonValueKind.Object
            && op0.TryGetProperty("landed_on_disk", out var lod) && lod.ValueKind == JsonValueKind.String, withField);
        Check("…and it is SOURCED as the file's reading rather than the applied edit's",
            op0.ValueKind == JsonValueKind.Object
            && op0.TryGetProperty("landed_source", out var lv) && lv.GetString() == "written_file", withField);

        // The per-op clause REPORTS its source instead of judging (Aaron, 2026-08-11): the comparator that decided
        // "this op did not land" is gone, with the eight unit checks that pinned its rules and the three render
        // checks that pinned its sentence. What remains pinned is what survives it — the clause comes off the
        // written FILE, and where it could not, the response says which reading it is showing instead.

        // THE READ-SURFACE HALF, pinned because reverting it left all 117 probes green (review [low]): a substruct
        // leaf read off a BINARY OVERLAY must render the modelled type name, not Mutagen's implementation class. This
        // is what the verify prints for a `Set <substruct>`, and it is read straight off the written file — so
        // without the strip the same response says "[WeaponBasicStats]" in its edit list and
        // "[WeaponBasicStatsBinaryOverlay]" two lines below. Driven through the real reader on a real overlay.
        ISkyrimModGetter? ovr = null;
        try
        {
            ovr = SkyrimMod.CreateFromBinaryOverlay(fx.ReplacerPath, SkyrimRelease.SkyrimSE);
            var subj = ovr.Weapons.FirstOrDefault(w => w.FormKey == fx.SubjectKey);
            var token = subj is null ? null
                : ReadEngine.ReadFields(subj, new[] { "BasicStats" }).Fields.FirstOrDefault()?.Note;
            // Asserted on the TYPE NAME, not the whole note: the reader appends its own "pass depth=2" hint, and
            // pinning that too would make this arm fail on an unrelated wording change.
            Check("a SUBSTRUCT leaf read off the written file renders the modelled type, not the overlay class",
                token is not null && token.StartsWith("[WeaponBasicStats]", StringComparison.Ordinal)
                && !token.Contains("BinaryOverlay", StringComparison.Ordinal), token ?? "(no read)");
        }
        finally { (ovr as IDisposable)?.Dispose(); }

        // MULTI-OP, end-to-end: two Adds to ONE list in ONE in-place call. Both land; the file carries both. The
        // earlier op's reading was taken between them, so comparing it against the final file state accused a correct
        // write of not landing — the defect a review reproduced in this fixture after the arm's own doc claimed it
        // could not be built. Asserted on the RENDER, which is what a caller reads.
        var twoAdds = ApplyTools.Apply(fx.Svc,
            ops: Json("[" + ComposeRankOp(fx.FactionFid, "\"fields\":{\"Number\":\"1\"}")[1..^1] + ","
                          + ComposeRankOp(fx.FactionFid, "\"fields\":{\"Number\":\"2\"}")[1..^1] + "]"),
            in_place: fx.ReplacerName, acknowledge: true);
        Check("two Adds to ONE list in one call: both land and NOTHING is reported as not landed",
            twoAdds.StartsWith("edited ", StringComparison.Ordinal)
            && !twoAdds.Contains("NOT landed", StringComparison.Ordinal), twoAdds);
        Check("…and the superseded op says so — its clause is the applied edit's, not the later op's file reading",
            twoAdds.Contains("a later op in this call wrote the same field", StringComparison.Ordinal), twoAdds);
        Check("…and the list really carries BOTH ranks on disk (the write the arm is defending was correct)",
            RanksIn(fx.ReplacerPath, fx.FactionFid) >= 2, $"ranks={RanksIn(fx.ReplacerPath, fx.FactionFid)}\n{twoAdds}");
    }

    /// <summary>#308 — two COUNT-CHANGING key-addressed ops in ONE in-place call. The multi-op checks in arm 6 use
    /// two Adds, whose Key is null, so they take the superseded path and never exercised the key exemption; a review
    /// then reproduced a false "NOT landed" here, on a call where BOTH removes landed and where the remedy that
    /// advises would delete a third element. Driven end-to-end, and the disk count is asserted too, so the arm proves
    /// the write was right rather than merely that the render stayed quiet.</summary>
    static void KeyedMultiOpArm(Fixture fx)
    {
        Console.WriteLine("── ARM 7: #308 — two key-addressed REMOVES in one call are not slandered ──");

        // Seed three ranks in one call, so the arm owns its own state regardless of what ran before it.
        var seed = ApplyTools.Apply(fx.Svc, in_place: fx.ReplacerName, acknowledge: true,
            ops: Json("[" + string.Join(",", new[] { "7", "8", "9" }
                .Select(n => ComposeRankOp(fx.FactionFid, "\"fields\":{\"Number\":\"" + n + "\"}")[1..^1])) + "]"));
        int before = RanksIn(fx.ReplacerPath, fx.FactionFid);
        Check("seeded three ranks for the keyed-op arm",
            seed.StartsWith("edited ", StringComparison.Ordinal) && before >= 3, $"ranks={before}\n{seed}");

        // Two removes BY INDEX, high index first so the second index stays valid.
        string RemoveRank(string key) =>
            "{\"formid\":\"" + fx.FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"Remove\",\"key\":\"" + key + "\"}";
        var twoRemoves = ApplyTools.Apply(fx.Svc, in_place: fx.ReplacerName, acknowledge: true,
            ops: Json("[" + RemoveRank("2") + "," + RemoveRank("0") + "]"));
        int after = RanksIn(fx.ReplacerPath, fx.FactionFid);
        Check("two key-addressed Removes in one call: BOTH land (the count drops by two)",
            after == before - 2, $"{before} -> {after}\n{twoRemoves}");
        Check("…and NEITHER is reported as not landed — the earlier op's count is behind the file's, not wrong",
            !twoRemoves.Contains("NOT landed", StringComparison.Ordinal)
            && !twoRemoves.Contains("NOT carried by the written file", StringComparison.Ordinal), twoRemoves);
    }

    /// <summary>ARM 8 (#308) — the two seams a review found INERT: nothing pinned the keyed exemption (deleting it
    /// left every probe green, because arm 7 drives count-CHANGING removes which fall through anyway), and nothing
    /// pinned the verify's WIRE to the written file. Both are driven here against the real file. (The second half was
    /// written when the pass also produced a divergence VERDICT; that verdict is gone, so it now pins the fact the
    /// verdict rested on — that the clause is the file's reading and not the applied edit's passed through.)</summary>
    static void VerifyWiringArm(Fixture fx)
    {
        Console.WriteLine("── ARM 8: #308 — the keyed exemption, and the comparator's wire into the verify ──");

        // (a) THE EXEMPTION'S OWN PURPOSE: two SetAtIndex ops on DIFFERENT indices are independent, so each keeps its
        //     own file verification rather than one being written off as superseded by the other.
        var ranks = RanksIn(fx.ReplacerPath, fx.FactionFid);
        if (ranks < 2)
            ApplyTools.Apply(fx.Svc, in_place: fx.ReplacerName, acknowledge: true,
                ops: Json("[" + string.Join(",", new[] { "1", "2" }
                    .Select(n => ComposeRankOp(fx.FactionFid, "\"fields\":{\"Number\":\"" + n + "\"}")[1..^1])) + "]"));
        // KEY-addressed SetAtIndex, not a bracketed path: the bracketed form takes the segment rule, and it was the
        // KEY exemption a review found unpinned. `key=` is where the element lives for this verb, so this is the
        // shape CountNeutralKeyedVerb actually decides. SetAtIndex on a struct list takes a compose spec.
        string SetRankNumber(string idx, string val) =>
            "{\"formid\":\"" + fx.FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"SetAtIndex\",\"key\":\"" + idx
            + "\",\"compose\":{\"type\":\"Rank\",\"fields\":{\"Number\":\"" + val + "\"}}}";
        var twoSets = ApplyTools.Apply(fx.Svc, in_place: fx.ReplacerName, acknowledge: true, format: "json",
            ops: Json("[" + SetRankNumber("0", "41") + "," + SetRankNumber("1", "42") + "]"));
        var states = new List<string>();
        try
        {
            var doc = Json(twoSets);
            if (doc.TryGetProperty("ops", out var arr))
                foreach (var op in arr.EnumerateArray())
                    states.Add(op.TryGetProperty("landed_source", out var v) ? v.GetString() ?? "?" : "?");
        }
        catch { /* states stays empty — the check below fails with the render as evidence */ }
        Check("two ops on DIFFERENT elements each keep their own file reading (neither is written off)",
            states.Count == 2 && states.All(v => v == "written_file"),
            string.Join(",", states) + "\n" + twoSets);

        // (b) THE WIRE: drive the REAL verify pass against the REAL written file and require that the op comes back
        //     carrying the FILE's reading — not the claim it was handed. This is what proves DescribeApplied is
        //     actually consulted off the re-opened file rather than the clause being passed through from memory,
        //     which is the whole of #308 now that the verdict is gone.
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(fx.ReplacerPath, SkyrimRelease.SkyrimSE);
            var fk = FormKey.Factory(fx.FactionFid);
            var req = new WriteRequest { RecordType = "Faction", Path = new[] { "Ranks" }, Verb = "Add" };
            var claim = new WritePatchBuilder.OpResult(fk, "Faction", "Add Ranks", true, null,
                                                      "[list: 5 item(s)]", "now 5 item(s)");
            var verified = WritePatchBuilder.VerifyLandedAgainstFile(back, new[] { (fk, req) }, new[] { claim });
            Check("the verify pass reads the FILE: the op comes back with the file's own count, not the claim's",
                verified.Count == 1 && verified[0].LandedOnDisk is { } lod2 && !lod2.Contains("5", StringComparison.Ordinal),
                verified.Count == 1 ? verified[0].LandedOnDisk ?? "(null)" : "(no result)");
            Check("...and the claim's own in-memory reading is preserved beside it, unchanged",
                verified.Count == 1 && verified[0].Landed == "now 5 item(s)",
                verified.Count == 1 ? verified[0].Landed ?? "(null)" : "(no result)");
        }
        finally { (back as IDisposable)?.Dispose(); }

        // Arm (a2) runs LAST in this method, after (b) has read the file: it is the only arm here that CHANGES the
        // rank count, and (b) asserts the file disagrees with a fabricated "5 item(s)" claim. Adding two ranks
        // before (b) made the fabricated claim true and turned a real arm green-then-red for a reason that had
        // nothing to do with what it tests.
        // (a2) THE EXEMPTION'S BOUND, from the other side (#302). InsertAtIndex is key-addressed like SetAtIndex, so
        //      a classifier that asked about the KEYS alone would exempt it — and it must not: an insert ADDS an
        //      element and shifts every index at or after it, so the earlier op's in-memory count is a step behind
        //      the file's final one and every index it quoted has moved.
        //      What admitting it would actually cost, stated as the CURRENT code behaves rather than as the history:
        //      the earlier op would be handed the file's FINAL reading as though it were its own answer. (An earlier
        //      draft of this comment described the harm as a false "treat this op as NOT landed" — that was the
        //      pre-existing count-comparison's harm, and that comparison was removed; see WritePatchBuilder's own
        //      note above VerifyLandedAgainstFile's single leaf comparison. Folding the old wording forward would
        //      have pointed a reader at a failure mode the code can no longer produce.)
        //      So: both inserts LAND, and the EARLIER op falls back to "superseded" — silent about itself, which is
        //      correct — rather than being handed a reading that is not its own.
        int ranksBeforeInsert = RanksIn(fx.ReplacerPath, fx.FactionFid);
        string InsertRank(string idx, string val) =>
            "{\"formid\":\"" + fx.FactionFid + "\",\"field_path\":\"Ranks\",\"op\":\"InsertAtIndex\",\"key\":\"" + idx
            + "\",\"compose\":{\"type\":\"Rank\",\"fields\":{\"Number\":\"" + val + "\"}}}";
        var twoInserts = ApplyTools.Apply(fx.Svc, in_place: fx.ReplacerName, acknowledge: true, format: "json",
            ops: Json("[" + InsertRank("0", "51") + "," + InsertRank("1", "52") + "]"));
        var insertStates = new List<string>();
        try
        {
            var idoc = Json(twoInserts);
            if (idoc.TryGetProperty("ops", out var iarr))
                foreach (var op in iarr.EnumerateArray())
                    insertStates.Add(op.TryGetProperty("landed_source", out var v) ? v.GetString() ?? "?" : "?");
        }
        catch { /* stays empty — the checks below fail with the render as evidence */ }
        int ranksAfterInsert = RanksIn(fx.ReplacerPath, fx.FactionFid);
        Check("two key-addressed InsertAtIndex ops in one call: BOTH land (the count rises by two)",
            ranksBeforeInsert > 0 && ranksAfterInsert == ranksBeforeInsert + 2,
            $"{ranksBeforeInsert} -> {ranksAfterInsert}");
        Check("…and InsertAtIndex is NOT admitted to the count-neutral keyed exemption: the earlier op reads 'superseded'",
            insertStates.Count == 2 && insertStates[0] == "superseded" && insertStates[1] == "written_file",
            string.Join(",", insertStates));
        // The RENDER, driven through the real response rather than asserted about the switch: the change summary has
        // an arm per list verb, and without one of its own an insert fell through to the bare "now N item(s)" — true,
        // and silent about the index, which is the one thing the caller issued the op to control.
        Check("…and the change summary says INSERTED at the index, not just a new count",
            twoInserts.Contains("inserted [0]", StringComparison.Ordinal)
            && twoInserts.Contains("inserted [1]", StringComparison.Ordinal), twoInserts);
    }

    /// <summary>How many Ranks the written faction carries on disk — the ground truth behind the multi-op check.</summary>
    static int RanksIn(string espPath, string factionFid)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            var fk = FormKey.Factory(factionFid);
            ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
            return ov.Factions.FirstOrDefault(f => f.FormKey == fk)?.Ranks.Count ?? -1;
        }
        catch { return -1; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>One <c>Add Ranks</c> compose op, built by concatenation: the JSON's brace runs make a raw interpolated
    /// literal ambiguous (the compiler says so), and escaping around that is less readable than this.</summary>
    static string ComposeRankOp(string formid, string? extra) =>
        "[{\"formid\":\"" + formid + "\",\"field_path\":\"Ranks\",\"op\":\"Add\",\"compose\":{\"type\":\"Rank\""
        + (extra is null ? "" : "," + extra) + "}}]";

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
        public required string FactionFid { get; init; }      // #308: empty Ranks list, owned by the replacer
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

            // #308's fixture: a FACTION with an empty Ranks list. A Rank composed with NO fields is the canonical
            // "exists in memory, serializes to nothing" struct — the shape the in-place verify used to report as
            // landed under a banner claiming the line came off the written file.
            var faction = m.Factions.AddNew();
            faction.EditorID = "ApFaction";

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
            WriteEngine.GenericGetOrAddAsOverride(r, faction);     // …and the faction, for the same reason (#308)
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
                FactionFid = $"{faction.FormKey.ID:X6}:{mKey.FileName}",
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

        // ROUND-5 FOLD [low] — the vocabulary hint is gated on the DECLARING TYPE, not the member name alone.
        // The same word is a different mistake per shape, so a name-only match answers one caller with another
        // caller's correction. `op` is the sharp case: legal on an op, wrong two different ways elsewhere.
        var opInNested = ApplyTools.Apply(fx.Svc, ops: Json(
            $$"""[{"formid":"{{fx.PotionAFid}}","field_path":"Effects","op":"Add","compose":""" +
            """{"type":"Effect","sets":[{"path":"Data.Magnitude","op":"Set","value":"1"}]}}]"""));
        Check("a stray `op` in a NESTED SET is corrected toward verb (the nested shape's own word)",
            opInNested.StartsWith("error:") && opInNested.Contains("still spells its verb"), opInNested);

        var opInAssignment = ApplyTools.Apply(fx.Svc, bundle: new[] { "Name" },
            assignments: Json($$"""[{"target":"{{fx.SubjectFid}}","from":"{{fx.DonorWeaponFid}}","op":"CopyFrom"}]"""));
        Check("...while the SAME stray in an ASSIGNMENT gets the assignment's own correction, not a compose= lecture",
            opInAssignment.StartsWith("error:") && opInAssignment.Contains("carries no verb")
                && !opInAssignment.Contains("compose="), opInAssignment);

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
            dry.StartsWith(WriteSentences.DryRunHeader, StringComparison.Ordinal), dry);
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
        // Asserted POSITIVELY (round-5 nit): the earlier form only checked that the exclusivity refusal was
        // ABSENT, which would stay green if the whole LANE block were deleted. A blank patch= must not merely
        // fail to trip exclusivity — the call must actually TAKE the into= lane, which this proves by landing on
        // into='s own "no such patch to extend" refusal.
        var blankPatch = ApplyTools.Apply(fx.Svc, ops: Json(OneOp("72")), patch: "   ", into: "NoSuchPatch.esp");
        Check("a whitespace-only patch= counts as ABSENT and the call TAKES the into= lane (its own refusal, not the exclusivity one)",
            blankPatch.StartsWith("error:") && !blankPatch.Contains("the two lanes are exclusive")
                && blankPatch.Contains("NoSuchPatch.esp"), blankPatch);
    }
}

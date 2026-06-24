using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the IN-PLACE WRITE LANE, Waves 1 + 1b (dev/plans/IN_PLACE_WRITE_LANE_PLAN_2026-06-13.md
/// §10 CI teeth). The in-place EDIT lane (set_field/bulk_apply, Wave 1) edits an EXISTING plugin the user owns — incl. one
/// houseCARL didn't author — back over itself; the in-place CREATE lane (create_record/bulk_create, Wave 1b) allocates
/// BRAND-NEW records into it the same way. Both touch the user's ORIGINAL file, so the safety properties are load-bearing
/// and each is RED-provable here:
///
///   CONTENT-SOURCE (§4.1)  — the EDIT's body is the TARGET's OWN record, NEVER the load-order winner; a record the
///                            target doesn't define is REFUSED (no foreign content injected). RED if sourced from winner.
///   COUNTER (edit)         — the author's HEDR.NextObjectID survives verbatim (WriteInPlace skips EnsureFormIdFloor). RED
///                            if the patch-lane floor ran (a sub-0x800 author counter would jump to 0x800).
///   COUNTER (create)       — a new record allocates a fresh FormID in the TARGET's OWN range; the allocation path floors
///                            the target's counter and ADVANCES it (the opposite of edit's preserve). RED if it kept 0x123.
///   MASTERS (edit)         — no Skyrim.esm/Update.esm baseline force-include (WriteInPlace ≠ WritePatch). RED if baseline added.
///   MASTERS (create)       — a new record's cross-mod reference resolves + pulls its origin into the lean derived header
///                            (the WHOLE known-master set is the serialize's resolution context). RED if own-masters-only.
///   FLAT LOCK (winner==target) — re-editing a record the ACTIVE target itself owns: Phase-1 fetch opens the target
///                            overlay, ReleaseOverlay must close it before the File.Replace swap. RED if it stays mapped.
///   CONSENT HANDSHAKE      — first in-place touch of a plugin REFUSES-and-explains (writes nothing); acknowledge=true
///                            proceeds; a second touch does NOT re-prompt (persisted, cross-session via UserConfig). The
///                            acknowledgement is SHARED across the edit + create lanes (keyed off the resolved path).
///   NESTED (create)        — a new child under ANY parent works in place (full create parity): a parent the target OWNS is
///                            sourced from the target; a FOREIGN parent is overridden IN to host the child, as xEdit/the patch lane do.
///   RESOLVER / CONTRACT    — target= resolves the REAL active-plugin path (a non-load-order name REFUSES, never
///                            retargets); in_place needs target=, is mutually exclusive with into=; opt-in defaults OFF.
///
/// Self-contained: synthesizes a master + a user override + a higher override in TEMP and generates the validator corpus
/// BY CONSTRUCTION in-process (no game data, no checked-in corpus.json). Drives the REAL WritePatchBuilder.ApplyInPlace /
/// CreateRecordsInPlace (builder arms) and the REAL LoadOrderService in-place branches (service arms, via the ForGuard seam).
/// Arms A–I cover the EDIT lane; J–Q cover the CREATE lane (J create+counter, O cross-master, M nested-under-a-foreign-parent,
/// P same-call nested unit, Q exterior-cell-under-a-foreign-worldspace, K handshake, L contract, N opt-in) and arm I also
/// proves the create lane shares the marker + handshake.
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
        Console.WriteLine("################  REGRESSION GUARD — in-place write lane, Waves 1 + 1b  ################");
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
        FormKey wfk, w2fk, tfk, wsfk;
        {
            var m = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);
            var w = m.Weapons.AddNew(); w.EditorID = "HcIP_Weap"; w.BasicStats = new WeaponBasicStats { Damage = 10 }; w.Name = "MasterSword";
            var w2 = m.Weapons.AddNew(); w2.EditorID = "HcIP_Weap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
            var t = m.DialogTopics.AddNew(); t.EditorID = "HcIP_Topic";   // a FOREIGN parent for the nested-in-place arm (M)
            var ws = m.Worldspaces.AddNew(); ws.EditorID = "HcIP_World";  // a FOREIGN worldspace for the exterior-cell-in-place arm (Q)
            wfk = w.FormKey; w2fk = w2.FormKey; tfk = t.FormKey; wsfk = ws.FormKey;
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
        string fmtTfk = $"{tfk.ID:X6}:{MasterName}";       // the foreign DialogTopic parent (arm M)
        string fmtWsfk = $"{wsfk.ID:X6}:{MasterName}";     // the foreign Worldspace parent (arm Q)
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

        // ===== I — MARKER + into= BOUNDARY: the editedInPlace marker is stamped but NEVER generated=true, so the user
        // mod keeps failing IsHouseCarlOwned and a later into= can't blind-overwrite it. The marker only fires for a
        // target under ModsDir (IsUnderModsDir), so this arm drives the REAL service over a synthetic MO2 instance with
        // the user mod as a NON-houseCARL mod folder. The discriminating check: into= STILL refuses the edited user mod
        // — it would SUCCEED (extend it) if the marker had wrongly written generated=true. =====
        {
            string inst = Path.Combine(tmpDir, "instance");
            string profiles = Path.Combine(inst, "profiles", "Default");
            string mods = Path.Combine(inst, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(tmpDir, "game", "Data"));
            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(tmpDir, "game").Replace(@"\", @"\\") + ")\r\n");

            var mMod = Path.Combine(mods, "MasterMod"); Directory.CreateDirectory(mMod);
            File.Copy(masterPath, Path.Combine(mMod, MasterName));
            var uMod = Path.Combine(mods, "UserMod"); Directory.CreateDirectory(uMod);
            File.Copy(userPristine, Path.Combine(uMod, UserName));
            string userMeta = Path.Combine(uMod, "meta.ini");
            File.WriteAllText(userMeta, "[General]\r\ngameName=skyrimse\r\ncomments=USER SENTINEL\r\n");   // a NON-houseCARL meta.ini

            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + UserName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterName + "\r\n*" + UserName + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+UserMod\r\n+MasterMod\r\n");

            var store = new UserConfigStore(Path.Combine(tmpDir, "I.user.json"));
            using var svc = LoadOrderService.WithInstance(inst, 0, store);
            svc.Stats();   // warm the lazy index once

            var edit = new[] { new BulkOp { Formid = fmtWfk, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "61" } };
            var wrote = svc.ApplyEdits(edit, null, null, fullReadback: false, target: UserName, inPlace: true, acknowledge: true);
            string meta = File.Exists(userMeta) ? File.ReadAllText(userMeta) : "";
            bool markerWritten = meta.Contains("editedInPlace=");
            bool noGenerated = !meta.Replace(" ", "").Contains("generated=true", StringComparison.OrdinalIgnoreCase);
            bool sentinelKept = meta.Contains("USER SENTINEL");
            // The boundary, end-to-end: an into= extend of the now-edited user mod STILL refuses it (un-owned). With the
            // bug (marker writes generated=true) this would instead SUCCEED — so a refusal is the discriminating proof.
            var intoTry = svc.ApplyEdits(edit, null, UserName, fullReadback: false);
            bool ownedStaysFalse = !intoTry.Success;
            bool landed = ReadDamage(Path.Combine(uMod, UserName), wfk) == 61;

            // The CREATE lane shares this user mod's acknowledgement (keyed off the resolved path, recorded by the edit
            // above) AND stamps the SAME editedInPlace marker via the SAME helper: a create-in-place here must NOT re-prompt
            // (edit's ack covers it — the cross-lane handshake share, end-to-end) and must keep generated=true absent.
            var createIP = svc.CreateRecords("Keyword", "HcIP_InstKw", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: UserName, inPlace: true, acknowledge: false);
            bool createShared = createIP.Success && createIP.InPlace && !createIP.NeedsAcknowledge;
            string meta2 = File.Exists(userMeta) ? File.ReadAllText(userMeta) : "";
            bool createNoGenerated = !meta2.Replace(" ", "").Contains("generated=true", StringComparison.OrdinalIgnoreCase) && meta2.Contains("editedInPlace=");

            bool pass = wrote.Success && wrote.InPlace && landed && markerWritten && noGenerated && sentinelKept && ownedStaysFalse
                     && createShared && createNoGenerated;
            results.Add(("I editedInPlace marker + into= boundary + create-lane share (never generated=true)", pass,
                $"wrote={wrote.Success} landed61={landed} marker={markerWritten} noGenerated={noGenerated} userSentinelKept={sentinelKept} into=StillRefusesUnowned={ownedStaysFalse} createSharesAck={createShared} createKeepsMarkerSafe={createNoGenerated}  [{Trim(wrote.Error)}]"));
        }

        // ===== J — CREATE-INTO-TARGET: a new flat record allocates a fresh FormID IN THE TARGET; the counter ADVANCES =====
        // The create-side counter behaviour is the OPPOSITE of the edit lane's preserve: the allocation path floors the
        // target's OWN counter (the author's sub-0x800 0x123 → 0x800) and advances it. A new Keyword lands in the USER
        // mod's own range (FormKey.ModKey == the user mod), the counter jumps to keywordId+1, and the user's existing
        // weapon override stays intact. RED if nothing was allocated, it allocated in the wrong plugin, or kept 0x123.
        {
            var userJ = FreshUser(tmpDir, "J", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userJ, highPath });
            var o = WritePatchBuilder.CreateRecordsInPlace(r, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcIP_NewKw", Edits = Array.Empty<WriteRequest>() } },
                userJ, UserName);
            var newFk = o.Created.Count > 0 ? o.Created[0].FormKey : default;
            bool inTarget = newFk.ModKey.FileName.String.Equals(UserName, StringComparison.OrdinalIgnoreCase) && newFk.ID >= 0x800;
            uint nextId = ReadNextFormId(userJ);
            string newEdid = ReadEditorIdAt(userJ, newFk);
            int wDmg = ReadDamage(userJ, wfk); string wName = ReadName(userJ, wfk);
            bool pass = o.Success && o.InPlace && inTarget && nextId == newFk.ID + 1 && newEdid == "HcIP_NewKw" && wDmg == 20 && wName == "UserSword";
            results.Add(("J create-into-target (fresh FormID in target, counter advances)", pass,
                $"success={o.Success} inPlace={o.InPlace} newFk={newFk}(want ID>=0x800 in {UserName}) counter=0x{nextId:X}(want 0x{newFk.ID + 1:X}, NOT 0x123) edid='{newEdid}' weaponOverride=dmg{wDmg}/'{wName}'(want 20/UserSword)  [{o.Error ?? "ok"}]"));
        }

        // ===== O — CROSS-MASTER REFERENCE: a new record referencing another plugin ADDS that plugin as a master =====
        // The model-C create serialize hands WriteInPlace the WHOLE known-master set (AllMastersExcept the target), exactly
        // as xEdit/the patch lane do, so a created record's reference resolves and its origin is pulled into the lean
        // derived header. Into a BARE user mod with NO masters: a new FormList whose Items reference the master weapon must
        // make HcInPlaceMaster.esm a master. RED if the serialize used own-masters-only (a bare mod has none → the
        // reference can't resolve → the write throws or the master is absent).
        {
            var bareDir = Path.Combine(tmpDir, "arm-O"); Directory.CreateDirectory(bareDir);
            var bare = Path.Combine(bareDir, "HcInPlaceBare.esp");
            new SkyrimMod(new ModKey("HcInPlaceBare", ModType.Plugin), SkyrimRelease.SkyrimSE)
                .BeginWrite.ToPath(bare).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var r = LoadOrderResolver.Build(new[] { masterPath, bare });
            var o = WritePatchBuilder.CreateRecordsInPlace(r, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "FormList", EditorId = "HcIP_RefFlst",
                    Edits = new[] { new WriteRequest { RecordType = "FormList", Path = new[] { "Items" }, Verb = "Add", Value = fmtWfk } } } },
                bare, "HcInPlaceBare.esp");
            var masters = ReadMasters(bare);
            bool added = masters.Any(m => m.Equals(MasterName, StringComparison.OrdinalIgnoreCase));
            bool pass = o.Success && o.InPlace && added;
            results.Add(("O cross-master reference adds the master (xEdit-parity serialize)", pass,
                $"success={o.Success} inPlace={o.InPlace} masters=[{string.Join(",", masters)}](want incl. {MasterName})  [{o.Error ?? "ok"}]"));
        }

        // ===== M — NESTED UNDER A FOREIGN PARENT works in place (full create parity — the corrected scope) =====
        // Add a NEW dialogue line (DialogResponses/INFO) under the MASTER's topic — a parent the user mod does NOT own —
        // straight into the user mod in place. This is normal authoring (xEdit/the patch lane do exactly it): the foreign
        // parent is OVERRIDDEN INTO the user mod to host the child, the child gets a fresh FormID in the user's range, and
        // the user's existing records stay intact. RED if it refused, or didn't override the parent in / land the child.
        {
            var userM = FreshUser(tmpDir, "M", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userM, highPath });
            var o = WritePatchBuilder.CreateRecordsInPlace(r, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcIP_NewLine", Edits = Array.Empty<WriteRequest>(), ParentRef = fmtTfk } },
                userM, UserName);
            var infoFk = o.Created.Count > 0 ? o.Created[0].FormKey : default;
            bool childInTarget = infoFk.ModKey.FileName.String.Equals(UserName, StringComparison.OrdinalIgnoreCase) && infoFk.ID >= 0x800;
            bool parentOverriddenIn = RecordPresent(userM, tfk);   // the foreign topic is now carried (as an override) by the user mod
            int wDmg = ReadDamage(userM, wfk);                     // the user's existing weapon override stays intact
            bool pass = o.Success && o.InPlace && childInTarget && parentOverriddenIn && wDmg == 20;
            results.Add(("M nested under a FOREIGN parent works in place (parent overridden in + child)", pass,
                $"success={o.Success} inPlace={o.InPlace} childFk={infoFk}(in {UserName}) foreignTopicOverriddenIn={parentOverriddenIn} weaponIntact={wDmg == 20}  [{o.Error ?? "ok"}]"));
        }

        // ===== K — CONSENT HANDSHAKE for create: RED (first refuses) → GREEN (acknowledge writes) → no re-prompt =====
        {
            var userK = FreshUser(tmpDir, "K", userPristine);
            byte[] before = File.ReadAllBytes(userK);
            bool red, untouched, green, noReprompt;
            using (var r = LoadOrderResolver.Build(new[] { masterPath, userK }))
            {
                var svc = LoadOrderService.ForGuard(r, new UserConfigStore(Path.Combine(tmpDir, "K.user.json")));
                var first = svc.CreateRecords("Keyword", "HcIP_KwA", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: UserName, inPlace: true, acknowledge: false);
                red = first.NeedsAcknowledge && !first.Success;
                untouched = File.ReadAllBytes(userK).AsSpan().SequenceEqual(before);
                var ack = svc.CreateRecords("Keyword", "HcIP_KwA", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: UserName, inPlace: true, acknowledge: true);
                green = ack.Success && ack.InPlace;
                var again = svc.CreateRecords("Keyword", "HcIP_KwB", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: UserName, inPlace: true, acknowledge: false);
                noReprompt = again.Success && !again.NeedsAcknowledge;
            }
            bool pass = red && untouched && green && noReprompt;
            results.Add(("K create handshake RED->GREEN->no-reprompt", pass,
                $"firstRefused={red} untouched={untouched} ackWrote={green} secondNoPrompt={noReprompt}"));
        }

        // ===== L — CONTRACT validation for create (mirror the edit lane's E) =====
        {
            var userL = FreshUser(tmpDir, "L", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userL, highPath });
            var svc = LoadOrderService.ForGuard(r, new UserConfigStore(Path.Combine(tmpDir, "L.user.json")));
            var noTarget = svc.CreateRecords("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: null, inPlace: true, acknowledge: true);
            var withInto = svc.CreateRecords("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, "somepatch", false, null, null, null, target: UserName, inPlace: true, acknowledge: true);
            var targetNoFlag = svc.CreateRecords("Keyword", "HcIP_K", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: UserName, inPlace: false, acknowledge: false);
            bool pass = !noTarget.Success && (noTarget.Error?.Contains("requires target=") ?? false)
                     && !withInto.Success && (withInto.Error?.Contains("mutually exclusive") ?? false)
                     && !targetNoFlag.Success && (targetNoFlag.Error?.Contains("only meaningful with in_place") ?? false);
            results.Add(("L contract (in_place<->target, _|_ into=) — create", pass,
                $"noTarget={Trim(noTarget.Error)} | into={Trim(withInto.Error)} | noFlag={Trim(targetNoFlag.Error)}"));
        }

        // ===== N — OPT-IN BY CONSTRUCTION (create): create_record + bulk_create default the three params OFF =====
        {
            var cr = typeof(WriteTools).GetMethod(nameof(WriteTools.CreateRecord))!;
            var bc = typeof(WriteTools).GetMethod(nameof(WriteTools.BulkCreate))!;
            bool DefOff(System.Reflection.MethodInfo m) =>
                m.GetParameters().First(p => p.Name == "in_place").DefaultValue is false
                && m.GetParameters().First(p => p.Name == "target").DefaultValue is null
                && m.GetParameters().First(p => p.Name == "acknowledge").DefaultValue is false;
            bool pass = DefOff(cr) && DefOff(bc);
            results.Add(("N opt-in by construction (create_record/bulk_create default OFF)", pass,
                $"create_record={DefOff(cr)} bulk_create={DefOff(bc)}"));
        }

        // ===== P — SAME-CALL nested unit in place (a NEW topic + its NEW line, both into the user mod) =====
        // The headline "author a new dialogue unit straight into my mod" case: bulk-create a topic AND a line under it
        // (a same-call SIBLING parent), both net-new, in place. RED if either didn't land or the sibling-parent wiring
        // broke under the in-place lane.
        {
            var userP = FreshUser(tmpDir, "P", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userP });
            var o = WritePatchBuilder.CreateRecordsInPlace(r, rulebook, new[]
            {
                new WritePatchBuilder.CreateSpec { RecordType = "DialogTopic", EditorId = "HcIP_NewTopic", Edits = Array.Empty<WriteRequest>() },
                new WritePatchBuilder.CreateSpec { RecordType = "DialogResponses", EditorId = "HcIP_TopicLine", Edits = Array.Empty<WriteRequest>(), ParentRef = "HcIP_NewTopic" },
            }, userP, UserName);
            bool bothInTarget = o.Created.Count == 2 && o.Created.All(c => c.FormKey.ModKey.FileName.String.Equals(UserName, StringComparison.OrdinalIgnoreCase));
            bool topicPresent = o.Created.Count > 0 && RecordPresent(userP, o.Created[0].FormKey);
            bool linePresent = o.Created.Count > 1 && RecordPresent(userP, o.Created[1].FormKey);
            bool pass = o.Success && o.InPlace && bothInTarget && topicPresent && linePresent;
            results.Add(("P same-call nested unit (new topic + line) in place", pass,
                $"success={o.Success} inPlace={o.InPlace} created={o.Created.Count}(want 2) bothInTarget={bothInTarget} topic={topicPresent} line={linePresent}  [{o.Error ?? "ok"}]"));
        }

        // ===== Q — EXTERIOR CELL create in place under a FOREIGN worldspace (closes the disclosed cell gap) =====
        // Create a new EXTERIOR cell (grid=) under the MASTER's worldspace — a foreign parent that NEEDS a source link
        // cache (LinkCacheFor on the foreign winner, the one nesting path arm M's topic parent doesn't exercise) — straight
        // into the user mod in place. RED if it refused, didn't place the cell, or didn't override the worldspace in.
        {
            var userQ = FreshUser(tmpDir, "Q", userPristine);
            using var r = LoadOrderResolver.Build(new[] { masterPath, userQ });
            var o = WritePatchBuilder.CreateRecordsInPlace(r, rulebook,
                new[] { new WritePatchBuilder.CreateSpec { RecordType = "Cell", EditorId = "HcIP_ExtCell", Edits = Array.Empty<WriteRequest>(), ParentRef = fmtWsfk, Grid = "5,-12" } },
                userQ, UserName);
            var cellFk = o.Created.Count > 0 ? o.Created[0].FormKey : default;
            bool cellInTarget = cellFk.ModKey.FileName.String.Equals(UserName, StringComparison.OrdinalIgnoreCase) && cellFk.ID >= 0x800;
            bool worldOverriddenIn = RecordPresent(userQ, wsfk);   // the foreign worldspace is now carried (as an override) by the user mod
            bool pass = o.Success && o.InPlace && cellInTarget && worldOverriddenIn;
            results.Add(("Q exterior cell in place under a foreign worldspace (LinkCacheFor + placement)", pass,
                $"success={o.Success} inPlace={o.InPlace} cellFk={cellFk}(in {UserName}) worldOverriddenIn={worldOverriddenIn}  [{o.Error ?? "ok"}]"));
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

    static string ReadEditorIdAt(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk)?.EditorID ?? "(none)"; }
        catch { return "(read failed)"; }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    static bool RecordPresent(string path, FormKey fk)
    {
        ISkyrimModGetter? ov = null;
        try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); return ov.EnumerateMajorRecords().Any(r => r.FormKey == fk); }
        catch { return false; }
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

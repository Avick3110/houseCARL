using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE Wave A4 — SERVICE guard for housecarl_merge_plugins (LoadOrderService.MergePlugins) over a synthetic
/// MO2 instance. The fixture is the real merge shape: a base master, donor A (records + a patch surface: DIAL with two
/// INFOs, an NPC with facegen, an SGE quest with a shipped .seq, a voiced line), donor B — a PATCH of A later in the
/// load order (overrides A's DIAL re-listing only ONE modified INFO; overrides the base weapon; collides on an object
/// id; references A cross-donor) — plus an external referencer and an external overrider OUTSIDE the merge set.
///   MERGE     — one call merges A+B (arg order scrambled — LOAD order must govern): records land under the new ModKey,
///               A keeps its ids, B's collision renumbers, the cross-donor ref repoints, masters = base only (donors gone).
///   WINNER    — cross-donor conflicts resolve to the LOAD-ORDER WINNER: B's DIAL body + B's INFO text + B's base-weapon
///               override are in M; every conflict is REPORTED with winner/loser named.
///   GRAFT     — A's second INFO (which B's patch DIAL does NOT re-list) is GRAFTED into the winning topic — the arm that
///               fails if the graft is dropped (a mod merged with its patch would silently lose the base mod's lines).
///   WARN      — the external referencer AND overrider are named WARNs; the merge SUCCEEDS (the A4 posture — donors stay
///               active until the MO2 swap, so nothing breaks at write time); RenderMerge carries both to user output.
///   ASSETS    — facegen + voice land under the MERGED plugin-name folders (the folder segment IS the plugin name — the
///               carry every merge needs even with zero id collisions); the .seq regenerates (A shipped one).
///   RENAME    — the SINGLE-donor arm (#345): donor A alone into a new name is a rename — every A key remaps to the
///               output ModKey with its object id kept, facegen/voice/seq carry to the NEW plugin-name folders, the
///               donor file is byte-untouched, and the plugins that reference/override A (including B, now outside
///               the set) are still WARNed. The headline says RENAME; the multi-donor headline does not. A donor name
///               repeated is still ONE donor — the list is a set, in both arms. One arm runs against BuildMergeRemap
///               directly: "nothing can collide" is not "every id is kept", and a below-floor donor cannot be written
///               here to prove it through the service (Mutagen rejects sub-0x800 originating records on write).
///   SHAPE     — the render classifies rename-vs-combine ONCE and every sentence that varies consumes it: the
///               headline (derived from the accounting, so a PURE-OVERRIDE donor claims no re-keying), the per-donor
///               renumber cause, the two external-referencer remedy orderings, and the mod-folder default. Each has
///               arms on BOTH sides — the multi-donor checks above are the negatives.
///   HEADER    — nothing in a donor's header comes into the output: light (ESL) status, MASTER status, and
///               Author/Description. Light and master are each counted BY FLAG OR BY EXTENSION (the engine treats
///               .esl/.esm that way whatever the bit says), which is why one donor exists per spelling. Measured on
///               the written file, then REPORTED: the ESL note carries the compact remedy's own cost (it renumbers
///               from the floor) and suppresses the flat closing pointer so the costed advice is the one read last;
///               master and header-text name no remedy because the surface has none. All three are keyed on what the
///               DONORS carried, never on the donor count — a mixed multi-donor merge raises them too.
///   REFUSE    — ZERO donors / unknown donor / output already active / output == donor / .esl output, all loud,
///               nothing written.
///   UNTOUCHED — the donor files are byte-identical after the merge (new-file lane only).
///   LOCALIZED — (#362) donors whose .STRINGS live in the game-Data folder rather than their own mod folder keep their
///               FULL+DESC through the merge, in BOTH the rename and the combine shape (one fix, no donor-count
///               conditional). Its own instance: the fixture turns on a game-Data Skyrim.esm, which the order above
///               has none of. A baseline arm reads the donor with the BARE overlay first and requires it to come back
///               EMPTY — without that, every arm here would pass on a fixture that was never localized. A further arm
///               pins that the OUTPUT is written non-localized with its strings inline, which is what makes the
///               read-backs a read of the written bytes.
///   NOWHERE   — (#371) a donor whose strings are in NEITHER place: no dataDir resolves it, so every value reads
///               blank — and the merge REFUSES, named, with nothing written, rather than baking those blanks into a
///               plugin the caller keeps.
///   DECLARER  — a plugin outside the merge that lists a donor as a master and references none of its records. Its
///               own instance. The unit tests cover the detection; this arm covers the JOIN — the service reads it,
///               carries it into the outcome, and the rendered report names the plugin and what it declares.
/// Run: dotnet run --project src/housecarl-generator merge-service-guard
/// </summary>
public static class MergeServiceGuardProbe
{
    [CiProbe("merge-service-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  MERGE Wave A4 — service guard (housecarl_merge_plugins)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-merge-service-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            string data = Path.Combine(root, "game", "Data");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");

            // ---- fixture mods ----
            // Base master: one weapon both donors will override.
            var baseKey = new ModKey("HcMgBase", ModType.Master);
            var baseWeap = new FormKey(baseKey, 0xA01);
            var baseDir = Path.Combine(mods, "BaseMod"); Directory.CreateDirectory(baseDir);
            {
                var m = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
                m.Weapons.Add(new Weapon(baseWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBaseWeap", BasicStats = new WeaponBasicStats { Damage = 5 } });
                m.BeginWrite.ToPath(Path.Combine(baseDir, baseKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // Donor A: weapon + DIAL(2 INFOs) + NPC (facegen on disk) + SGE quest (shipped .seq) + base-weapon override
            // + a voiced line's .fuz on disk. The "mod" side of the mod+patch merge.
            var aKey = new ModKey("HcMgA", ModType.Plugin);
            var aWeap = new FormKey(aKey, 0xA01);            // same OBJECT id as baseWeap — different plugin, no collision
            var aDial = new FormKey(aKey, 0xA10);
            var aInfo1 = new FormKey(aKey, 0xA11);
            var aInfo2 = new FormKey(aKey, 0xA12);
            var aNpc = new FormKey(aKey, 0xA20);
            var aQuest = new FormKey(aKey, 0xA30);
            var aRef = new FormKey(aKey, 0xA40);             // the MOVED-REF shape: A originates X in A's cell; B's OWN cell overrides X
            var aCell = new FormKey(aKey, 0xA41);
            var aDir = Path.Combine(mods, "AMod"); Directory.CreateDirectory(aDir);
            {
                using var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(baseDir, baseKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(aKey, SkyrimRelease.SkyrimSE);
                m.Weapons.Add(new Weapon(aWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcMgAWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
                var topic = new DialogTopic(aDial, SkyrimRelease.SkyrimSE) { EditorID = "HcMgTopic" };
                var i1 = new DialogResponses(aInfo1, SkyrimRelease.SkyrimSE);
                i1.Responses.Add(new DialogResponse { Text = "A11 base" });
                var i2 = new DialogResponses(aInfo2, SkyrimRelease.SkyrimSE);
                i2.Responses.Add(new DialogResponse { Text = "A12 base" });
                topic.Responses.Add(i1); topic.Responses.Add(i2);
                m.DialogTopics.Add(topic);
                m.Npcs.Add(new Npc(aNpc, SkyrimRelease.SkyrimSE) { EditorID = "HcMgNpc" });
                m.Quests.Add(new Quest(aQuest, SkyrimRelease.SkyrimSE) { EditorID = "HcMgQuest", Flags = Quest.Flag.StartGameEnabled });
                var c1 = new Cell(aCell, SkyrimRelease.SkyrimSE) { EditorID = "HcMgACell", Flags = Cell.Flag.IsInteriorCell };
                c1.Temporary.Add(new PlacedObject(aRef, SkyrimRelease.SkyrimSE) { EditorID = "HcMgRefBase" });
                FileInterior(m, c1);
                m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == baseWeap)).BasicStats!.Damage = 10;   // A's override (the LOSER)
                m.BeginWrite.ToPath(Path.Combine(aDir, aKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { baseOv }).Write();
            }
            // A's on-disk FormID-keyed assets: the facegen pair, one voiced .fuz, the shipped .seq.
            foreach (var (_, rel) in FaceGenPath.Both(aNpc))
            {
                var p = Path.Combine(aDir, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllBytes(p, new byte[] { 0xFA, 0xCE });
            }
            var aVoiceRel = Path.Combine("Sound", "Voice", aKey.FileName.String, "MaleEvenToned", "HcQ_HcT_00000A11_1.fuz");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(aDir, aVoiceRel))!);
            File.WriteAllBytes(Path.Combine(aDir, aVoiceRel), new byte[] { 0xF0, 0x02 });
            Directory.CreateDirectory(Path.Combine(aDir, "SEQ"));
            File.WriteAllBytes(Path.Combine(aDir, "SEQ", "HcMgA.seq"), new byte[] { 0x30, 0x0A, 0x00, 0x00 });

            // Donor B — the PATCH of A, later in the load order (the WINNER): overrides A's DIAL re-listing ONLY its
            // modified copy of INFO1 (INFO2 deliberately NOT re-listed — the graft target), overrides the base weapon,
            // collides with A on object id 0xA01, and references A's weapon cross-donor.
            var bKey = new ModKey("HcMgB", ModType.Plugin);
            var bColl = new FormKey(bKey, 0xA01);            // COLLIDES with A's kept 0xA01 → must renumber
            var bWeap = new FormKey(bKey, 0xB01);
            var bList = new FormKey(bKey, 0xB02);
            var bCell = new FormKey(bKey, 0xB10);            // B's OWN cell carrying the MOVED override of A's placed ref
            var bDir = Path.Combine(mods, "BMod"); Directory.CreateDirectory(bDir);
            {
                using var baseOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(baseDir, baseKey.FileName.String), SkyrimRelease.SkyrimSE);
                using var aOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(aDir, aKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(bKey, SkyrimRelease.SkyrimSE);
                m.Weapons.Add(new Weapon(bColl, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBColl", BasicStats = new WeaponBasicStats { Damage = 3 } });
                m.Weapons.Add(new Weapon(bWeap, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBWeap", BasicStats = new WeaponBasicStats { Damage = 8 } });
                var fl = new FormList(bList, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBList" };
                fl.Items.Add(aWeap.ToLink<ISkyrimMajorRecordGetter>());
                m.FormLists.Add(fl);
                var patchTopic = new DialogTopic(aDial, SkyrimRelease.SkyrimSE) { EditorID = "HcMgTopicPatched" };   // override of A's DIAL
                var pi1 = new DialogResponses(aInfo1, SkyrimRelease.SkyrimSE);                                        // override of A's INFO1
                pi1.Responses.Add(new DialogResponse { Text = "A11 patched" });
                patchTopic.Responses.Add(pi1);                                                                        // INFO2 NOT re-listed
                m.DialogTopics.Add(patchTopic);
                var c2 = new Cell(bCell, SkyrimRelease.SkyrimSE) { EditorID = "HcMgBCell", Flags = Cell.Flag.IsInteriorCell };
                c2.Temporary.Add(new PlacedObject(aRef, SkyrimRelease.SkyrimSE) { EditorID = "HcMgRefMoved" });       // the MOVED reference (override under a DIFFERENT parent)
                FileInterior(m, c2);
                m.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == baseWeap)).BasicStats!.Damage = 20;   // B's override (the WINNER)
                m.BeginWrite.ToPath(Path.Combine(bDir, bKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { baseOv, aOv }).Write();
            }

            // OUTSIDE the merge set: a referencer of A (WARN arm) and an overrider of A (WARN arm).
            var depKey = new ModKey("HcMgDep", ModType.Plugin);
            var depDir = Path.Combine(mods, "DepMod"); Directory.CreateDirectory(depDir);
            {
                using var aOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(aDir, aKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(depKey, SkyrimRelease.SkyrimSE);
                var fl = new FormList(new FormKey(depKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDepList" };
                fl.Items.Add(aWeap.ToLink<ISkyrimMajorRecordGetter>());
                m.FormLists.Add(fl);
                m.BeginWrite.ToPath(Path.Combine(depDir, depKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { aOv }).Write();
            }
            var ovrKey = new ModKey("HcMgOvr", ModType.Plugin);
            var ovrDir = Path.Combine(mods, "OvrMod"); Directory.CreateDirectory(ovrDir);
            {
                using var aOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(aDir, aKey.FileName.String), SkyrimRelease.SkyrimSE);
                var m = new SkyrimMod(ovrKey, SkyrimRelease.SkyrimSE);
                m.Weapons.GetOrAddAsOverride(aOv.Weapons.First(w => w.FormKey == aWeap)).BasicStats!.Damage = 99;
                m.BeginWrite.ToPath(Path.Combine(ovrDir, ovrKey.FileName.String)).WithLoadOrder(new ISkyrimModGetter[] { aOv }).Write();
            }

            // A donor whose HEADER carries things the merged output is built without: the light (ESL) flag, an Author
            // and a Description. Renaming it is how the two header-loss NOTE lines are measured; A and B carry none of
            // it, so the multi-donor merge is the negative arm for both.
            var eslKey = new ModKey("HcMgEsl", ModType.Plugin);
            var eslDir = Path.Combine(mods, "EslMod"); Directory.CreateDirectory(eslDir);
            {
                var m = new SkyrimMod(eslKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
                m.ModHeader.Author = "HcMgAuthor";
                m.ModHeader.Description = "HcMgDescription";
                m.Weapons.Add(new Weapon(new FormKey(eslKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEslWeap" });
                m.BeginWrite.ToPath(Path.Combine(eslDir, eslKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // Light and master status each arrive TWO ways, and the loss is the same either way: the header bit, or
            // the extension (the engine force-treats .esl as light and .esm as a master regardless of the bit — the
            // model this tool's own .esl-output refusal states). One donor per spelling, so an arm can tell a
            // flag-only reading from the real one.
            var eslExtKey = new ModKey("HcMgEslExt", ModType.Light);      // .esl extension, header bit NOT set
            var eslExtDir = Path.Combine(mods, "EslExtMod"); Directory.CreateDirectory(eslExtDir);
            {
                var m = new SkyrimMod(eslExtKey, SkyrimRelease.SkyrimSE);
                m.Weapons.Add(new Weapon(new FormKey(eslExtKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEslExtWeap" });
                m.BeginWrite.ToPath(Path.Combine(eslExtDir, eslExtKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            // A donor with NO records at all — what housecarl_create_plugin produces, and what this report's own swap
            // instruction tells the caller to make so a donor .bsa keeps loading. Renaming one is the third thing the
            // headline's accounting can be asked to describe.
            var emptyKey = new ModKey("HcMgEmpty", ModType.Plugin);
            var emptyDir = Path.Combine(mods, "EmptyMod"); Directory.CreateDirectory(emptyDir);
            {
                var m = new SkyrimMod(emptyKey, SkyrimRelease.SkyrimSE);
                m.BeginWrite.ToPath(Path.Combine(emptyDir, emptyKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
            var esmKey = new ModKey("HcMgEsm", ModType.Master);           // .esm extension + the master flag
            var esmDir = Path.Combine(mods, "EsmMod"); Directory.CreateDirectory(esmDir);
            {
                var m = new SkyrimMod(esmKey, SkyrimRelease.SkyrimSE);
                m.Weapons.Add(new Weapon(new FormKey(esmKey, 0xD01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgEsmWeap" });
                m.BeginWrite.ToPath(Path.Combine(esmDir, esmKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }

            // ---- profile files (load order: Base, A, B, Dep, Ovr, Esl, EslExt, Esm — B AFTER A: the patch wins) ----
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
                "# header\r\n" + string.Join("\r\n", baseKey.FileName, aKey.FileName, bKey.FileName, depKey.FileName, ovrKey.FileName, eslKey.FileName, eslExtKey.FileName, esmKey.FileName, emptyKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
                string.Join("\r\n", "*" + baseKey.FileName, "*" + aKey.FileName, "*" + bKey.FileName, "*" + depKey.FileName, "*" + ovrKey.FileName, "*" + eslKey.FileName, "*" + eslExtKey.FileName, "*" + esmKey.FileName, "*" + emptyKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
                "# header\r\n" + string.Join("\r\n", "+EmptyMod", "+EsmMod", "+EslExtMod", "+EslMod", "+OvrMod", "+DepMod", "+BMod", "+AMod", "+BaseMod") + "\r\n");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);
            svc.Stats();   // warm the lazy index once

            byte[] aBytesBefore = File.ReadAllBytes(Path.Combine(aDir, aKey.FileName.String));
            byte[] bBytesBefore = File.ReadAllBytes(Path.Combine(bDir, bKey.FileName.String));

            // ---- MERGE + WINNER + GRAFT + WARN + ASSETS: the one full-shape call (donor args deliberately scrambled) ----
            var o = svc.MergePlugins(new[] { "HcMgB.esp", "HcMgA.esp" }, "HcMgMerged.esp");
            var mergedKey = new ModKey("HcMgMerged", ModType.Plugin);
            {
                Check(o.Success, $"MERGE succeeds ({(o.Success ? o.OutputPath : "ERR " + o.Error)})");
                bool idsOk = false, refOk = false, winnerOk = false, graftOk = false, mastersOk = false, collOk = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using var mm = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                    // A kept its ids under the merged key; B's collision renumbered to the first free id (0x800).
                    idsOk = mm.Weapons.Any(w => w.EditorID == "HcMgAWeap" && w.FormKey == new FormKey(mergedKey, 0xA01))
                         && mm.Weapons.Any(w => w.EditorID == "HcMgBWeap" && w.FormKey == new FormKey(mergedKey, 0xB01));
                    collOk = mm.Weapons.Any(w => w.EditorID == "HcMgBColl" && w.FormKey == new FormKey(mergedKey, 0x800));
                    // B's cross-donor reference into A repointed to the merged key.
                    refOk = mm.FormLists.FirstOrDefault(f => f.EditorID == "HcMgBList")?.Items.FirstOrDefault()?.FormKey
                            == new FormKey(mergedKey, 0xA01);
                    // Load-order winner: B's base-weapon override (damage 20) + B's DIAL body + B's INFO1 text.
                    var baseOverride = mm.Weapons.FirstOrDefault(w => w.FormKey == baseWeap);
                    var topic = mm.DialogTopics.FirstOrDefault(t => t.FormKey == new FormKey(mergedKey, 0xA10));
                    var info1 = topic?.Responses.FirstOrDefault(r => r.FormKey == new FormKey(mergedKey, 0xA11));
                    winnerOk = baseOverride?.BasicStats?.Damage == 20
                            && topic?.EditorID == "HcMgTopicPatched"
                            && info1?.Responses.FirstOrDefault()?.Text.String == "A11 patched";
                    // THE GRAFT: A's INFO2 (not re-listed by the patch) is present under the winning topic.
                    var info2 = topic?.Responses.FirstOrDefault(r => r.FormKey == new FormKey(mergedKey, 0xA12));
                    graftOk = info2?.Responses.FirstOrDefault()?.Text.String == "A12 base";
                    // Masters: the base only — never a donor.
                    var masters = mm.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
                    mastersOk = masters.Count == 1 && string.Equals(masters[0], baseKey.FileName.String, StringComparison.OrdinalIgnoreCase);
                }
                Check(idsOk, "MERGE A + B keep their non-colliding object ids under the merged key");
                Check(collOk, "MERGE B's colliding id renumbered to the first free id (0x800)");
                Check(refOk, "MERGE B's cross-donor reference into A repointed to the merged key");
                Check(winnerOk, "WINNER load-order winner everywhere (B's base-weapon override + DIAL body + INFO1 text)");
                Check(graftOk, "GRAFT A's un-relisted INFO2 grafted into the winning topic");
                // MOVED-REF: B's OWN cell carries an override of A's placed ref (a moved reference — the same record
                // under two DIFFERENT parents across donors). Exactly ONE copy may survive, under the WINNER's parent,
                // and it must be a reported conflict — the review's C-1 duplicate-FormKey hole, pinned here.
                {
                    bool movedOk = false;
                    if (o.Success && File.Exists(o.OutputPath))
                    {
                        using var mm2 = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
                        var mRefKey = new FormKey(mergedKey, 0xA40);
                        var copies = mm2.EnumerateMajorRecords().Where(r => r.FormKey == mRefKey).ToList();
                        var winnerCell = mm2.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.FormKey == new FormKey(mergedKey, 0xB10));
                        var loserCell = mm2.EnumerateMajorRecords<ICellGetter>().FirstOrDefault(c => c.FormKey == new FormKey(mergedKey, 0xA41));
                        movedOk = copies.Count == 1 && copies[0].EditorID == "HcMgRefMoved"
                               && winnerCell is not null && winnerCell.Temporary.Any(p => p.FormKey == mRefKey)
                               && loserCell is not null && !loserCell.Temporary.Any(p => p.FormKey == mRefKey)
                               && o.Conflicts.Any(c => c.Key == aRef);
                        Check(movedOk, $"MOVED-REF one copy at the merged key, under the WINNER's cell, conflict reported (copies {copies.Count})");
                    }
                    else Check(false, "MOVED-REF (merge failed)");
                }
                Check(mastersOk, $"MERGE masters = base only, donors gone ({string.Join(",", o.Masters)})");
                Check(o.RecordsCopied == 13 && o.RecordsRenumbered == 12,
                    $"MERGE record accounting (copied {o.RecordsCopied} expected 13, renumbered {o.RecordsRenumbered} expected 12)");
                // Conflicts: DIAL (B over A), INFO1 (B over A), base-weapon override (B over A), moved ref (B over A) — each named.
                bool confOk = o.Conflicts.Count == 4
                    && o.Conflicts.All(c => c.WinnerDonor == "HcMgB.esp" && c.LoserDonor == "HcMgA.esp")
                    && o.Conflicts.Any(c => c.Key == aDial) && o.Conflicts.Any(c => c.Key == aInfo1)
                    && o.Conflicts.Any(c => c.Key == baseWeap) && o.Conflicts.Any(c => c.Key == aRef);
                Check(confOk, $"WINNER all 4 cross-donor conflicts reported with winner/loser named (got {o.Conflicts.Count})");
                // Per-donor remap accounting: A keeps 8; B keeps 3, renumbers 1.
                var ra = o.DonorRemaps.FirstOrDefault(d => d.Donor == "HcMgA.esp");
                var rb = o.DonorRemaps.FirstOrDefault(d => d.Donor == "HcMgB.esp");
                Check(ra is { Kept: 8, Renumbered: 0 } && rb is { Kept: 3, Renumbered: 1 },
                    $"MERGE per-donor id accounting (A {ra?.Kept}/{ra?.Renumbered}, B {rb?.Kept}/{rb?.Renumbered})");
                // WARN posture: external referencer + overrider NAMED, merge still succeeded.
                Check(o.ExternalPlugins.Contains("HcMgDep.esp") && o.ExternalOverriders.Contains("HcMgOvr.esp"),
                    $"WARN external referencer + overrider named, merge NOT refused (refs [{string.Join(",", o.ExternalPlugins)}], ovr [{string.Join(",", o.ExternalOverriders)}])");
                var rendered = WriteTools.RenderMerge(o);
                Check(rendered.Contains("WARNING") && rendered.Contains("HcMgDep.esp") && rendered.Contains("HcMgOvr.esp"),
                    "WARN both warnings reach the rendered user output");
                // The pass's own coverage, stated by the pass. Declared masters are read now; runtime config files are
                // not, and the sentence has to name what is left out whether the lists came back empty or full.
                Check(rendered.Contains("it reads record links, record identity and declared masters, NOT runtime config files")
                      && rendered.Contains("only names a donor in such a file"),
                    "WARN the identify-pass line states what the pass does and does NOT read");
                // "Not read" is not the same fact as "and here is what that costs" — the loss is stated as well as the
                // gap in coverage, on every merge rather than only when the referencer list came back populated.
                //
                // PRESENCE, against the shared constant — not an absence assertion over the open space of things the
                // sentence might wrongly say. The arms this replaces asserted !Contains("<plugin>|<FormID>"), which
                // only ever detected the one literal the previous commit wrote: a reviewer quoted a REAL SkyPatcher
                // spelling and they stayed green. An absence arm over an unbounded string space cannot fail
                // meaningfully (the winnerTokenFree class). What keeps the sentence honest is the claim rule at its
                // definition and WriteSurfaceGuardProbe's [MustState] walk; what this arm owes is that the render
                // actually carries it.
                Check(rendered.Contains(WriteSentences.MergeRuntimeConfigs),
                    "WARN the runtime-config loss reaches user output, verbatim from the shared sentence");
                // The swap instruction must stay PLUGIN-level (PR #158 independent review #1): "disable the donor MODS"
                // (compact's instruction) would yank the donors' path-referenced assets out of the VFS — the merged
                // records still load meshes/textures/scripts from the donor folders.
                Check(rendered.Contains("deactivate the donor PLUGINS") && rendered.Contains("KEEP the donor mod folders enabled")
                      && !rendered.Contains("DISABLE the donor mods"),
                    "SWAP instruction is plugin-level (donor mod folders stay enabled)");
                // The OTHER direction of the #345 headline conditional: many donors must keep the combining form and
                // must NOT claim a rename. Pinned here so the arm below can't pass by the branch collapsing to one text.
                Check(rendered.Contains("from 2 donors") && !rendered.Contains("a RENAME of"),
                    "MERGE headline is the multi-donor form, never the rename arm");
                // The NEGATIVE side of every shape-varying sentence and of both header-loss notes. Each has its
                // positive twin in the RENAME block; a branch that collapsed to one text would fail one side or other.
                Check(rendered.Contains("renumbered (id collisions / below-floor)"),
                    "MERGE per-donor line names BOTH renumber causes (donors can collide)");
                Check(rendered.Contains("include them in the merge set (re-run with them added), or re-point them"),
                    "MERGE external-referencer remedy leads with include-in-set");
                Check(rendered.Contains("Include them in the merge set, or rebuild them against"),
                    "MERGE external-overrider remedy leads with include-in-set");
                Check(!rendered.Contains("LIGHT (ESL) status") && !rendered.Contains("MASTER status")
                      && !rendered.Contains("Author/Description"),
                    "MERGE no header-loss notes when no donor carried any of those header properties");
                Check(rendered.Contains("Want it light?"),
                    "MERGE keeps the closing compact pointer when no qualified ESL note replaced it");
                Check(Path.GetFileName(Path.GetDirectoryName(o.OutputPath))!.EndsWith(" merged"),
                    $"MERGE mod folder defaults to '<output> merged' ({Path.GetFileName(Path.GetDirectoryName(o.OutputPath))})");
                // ASSETS: facegen pair + voice under the MERGED plugin-name folders; .seq regenerated (A shipped one).
                var outDir = Path.GetDirectoryName(o.OutputPath)!;
                var newFace = FaceGenPath.Both(new FormKey(mergedKey, 0xA20)).ToList();
                bool faceOk = newFace.Count == 2 && newFace.All(x => File.Exists(Path.Combine(outDir, x.Item2)));
                bool voiceOk = File.Exists(Path.Combine(outDir, "Sound", "Voice", mergedKey.FileName.String, "MaleEvenToned", "HcQ_HcT_00000A11_1.fuz"));
                bool seqOk = o.SeqRegen is { Written: true } && File.Exists(Path.Combine(outDir, "SEQ", "HcMgMerged.seq"));
                Check(faceOk && o.AssetRename?.FacegenFilesCarried == 2, $"ASSETS facegen pair carried to the merged-name folder (files {o.AssetRename?.FacegenFilesCarried})");
                Check(voiceOk && o.VoiceRename?.FilesCarried == 1, $"ASSETS voice carried to the merged-name folder (files {o.VoiceRename?.FilesCarried})");
                Check(seqOk, $"ASSETS .seq regenerated for the merged plugin (written {o.SeqRegen?.Written})");
            }

            // ---- UNTOUCHED: the donors are byte-identical (new-file lane only) ----
            Check(aBytesBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(aDir, aKey.FileName.String)))
               && bBytesBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(bDir, bKey.FileName.String))),
                "UNTOUCHED donor files byte-identical after the merge");

            // ---- REFUSE arms (each loud, nothing written) ----
            {
                // ZERO donors still refuses — the guard's boundary moved from 2 to 1, it did not disappear. Both the
                // empty list and a list that is only blanks land here (the trim/drop runs before the count).
                var r = svc.MergePlugins(Array.Empty<string>(), "HcMgX.esp");
                Check(!r.Success && (r.Error?.Contains("at least ONE", StringComparison.OrdinalIgnoreCase) ?? false)
                                 && (r.Error?.Contains("rename", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE zero donors, remedy names both shapes ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(new[] { "  ", "" }, "HcMgX.esp");
                Check(!r.Success && (r.Error?.Contains("at least ONE", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE blank-only donor list ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(null, "HcMgX.esp");
                Check(!r.Success && (r.Error?.Contains("at least ONE", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE null donor list ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgNope.esp" }, "HcMgX.esp");
                Check(!r.Success && (r.Error?.Contains("not an active plugin", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE unknown donor ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgDep.esp");
                Check(!r.Success && (r.Error?.Contains("already an active plugin", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE output already in the load order ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgA.esp");
                Check(!r.Success && (r.Error?.Contains("cannot also be a donor", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE output == donor ({r.Error?.Split('—')[0].Trim()})");
                r = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgLight.esl");
                Check(!r.Success && (r.Error?.Contains(".esl", StringComparison.OrdinalIgnoreCase) ?? false)
                                 && (r.Error?.Contains("compact", StringComparison.OrdinalIgnoreCase) ?? false),
                    $"REFUSE .esl output with the compact-after remedy ({r.Error?.Split('—')[0].Trim()})");
                // The REASON, not just the refusal. It may only claim what holds on every path: a merge does not
                // constrain ids to the light window. Saying it keeps each donor's ids where they are is false —
                // BuildMergeRemap renumbers collisions and below-floor ids from 0x800 up, and the report prints that
                // count — and a single already-light donor's ids are all inside the window anyway.
                Check((r.Error?.Contains("never constrains object ids to the light window", StringComparison.Ordinal) ?? false)
                      && !(r.Error?.Contains("keeps each donor's object ids", StringComparison.Ordinal) ?? true)
                      && !(r.Error?.Contains("where they already are", StringComparison.Ordinal) ?? true),
                    $"…and its reason is the claim that holds on every path [{r.Error}]");
            }

            // ---- RENAME: donor A ALONE into a new name (#345). Same walk, empty collision set. ----
            {
                var renamedKey = new ModKey("HcMgRenamed", ModType.Plugin);
                var o1 = svc.MergePlugins(new[] { "HcMgA.esp" }, "HcMgRenamed.esp");
                Check(o1.Success && o1.Donors.Count == 1,
                    $"RENAME single donor accepted ({(o1.Success ? $"{o1.Donors.Count} donor" : "ERR " + o1.Error)})");

                bool keysOk = false, mastersOk1 = false;
                if (o1.Success && File.Exists(o1.OutputPath))
                {
                    using var rm = SkyrimMod.CreateFromBinaryOverlay(o1.OutputPath, SkyrimRelease.SkyrimSE);
                    // REMAP-TO-OUTPUT: EVERY one of A's originating keys, nested children included, is present under
                    // the OUTPUT ModKey at its own object id. The positive set is what pins this — the negative below
                    // is equally satisfied by a record that was DROPPED, and the nested INFOs (0xA11/0xA12) and the
                    // celled placed ref (0xA40 under 0xA41) are exactly where a walk regression loses one.
                    var expectedKeys = new[] { 0xA01u, 0xA10u, 0xA11u, 0xA12u, 0xA20u, 0xA30u, 0xA40u, 0xA41u }
                        .Select(id => new FormKey(renamedKey, id)).ToHashSet();
                    var presentKeys = rm.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
                    keysOk = expectedKeys.All(presentKeys.Contains)
                          && rm.Weapons.Any(w => w.EditorID == "HcMgAWeap" && w.FormKey == new FormKey(renamedKey, 0xA01))
                          && !rm.EnumerateMajorRecords().Any(x => x.FormKey.ModKey == aKey);
                    var ms = rm.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
                    mastersOk1 = ms.Count == 1 && string.Equals(ms[0], baseKey.FileName.String, StringComparison.OrdinalIgnoreCase);
                }
                Check(keysOk, "RENAME every A record remaps to the output ModKey at its own object id, none left on HcMgA.esp");
                Check(mastersOk1, $"RENAME masters = base only, the donor is not a master of its own rename ({string.Join(",", o1.Masters)})");

                var r1 = o1.DonorRemaps.FirstOrDefault(d => d.Donor == "HcMgA.esp");
                Check(r1 is { Kept: 8, Renumbered: 0 }, $"RENAME no collisions with one donor: A's in-window ids are all kept (kept {r1?.Kept}, renumbered {r1?.Renumbered})");
                Check(o1.RecordsCopied == 9 && o1.RecordsRenumbered == 8,
                    $"RENAME accounting: 8 originating + 1 base override (copied {o1.RecordsCopied} expected 9, renumbered {o1.RecordsRenumbered} expected 8)");
                // Every arm below is gated on ok1 and short-circuits, so a regression that refuses the rename turns them
                // all RED with their own reason instead of throwing on the first null OutputPath and taking the rest of
                // the block with it. A check that cannot report is not a check.
                bool ok1 = o1.Success && !string.IsNullOrEmpty(o1.OutputPath);
                // (No arm asserts "one donor reports zero conflicts": two distinct source records would have to remap
                // onto one key for a single donor to conflict, which BuildMergeRemap makes impossible — gutting the
                // conflict machinery entirely leaves such an arm green, so it would pin nothing.)

                // ASSETS carry on the plugin-NAME folder segment, which is exactly what a rename changes — this is the
                // arm that fails if anyone ever makes the carry conditional on a collision.
                var outDir1 = ok1 ? Path.GetDirectoryName(o1.OutputPath)! : "";
                var face1 = FaceGenPath.Both(new FormKey(renamedKey, 0xA20)).ToList();
                Check(ok1 && face1.Count == 2 && face1.All(x => File.Exists(Path.Combine(outDir1, x.Item2))) && o1.AssetRename?.FacegenFilesCarried == 2,
                    $"RENAME facegen pair carried to the renamed-name folder (files {o1.AssetRename?.FacegenFilesCarried})");
                Check(ok1 && File.Exists(Path.Combine(outDir1, "Sound", "Voice", renamedKey.FileName.String, "MaleEvenToned", "HcQ_HcT_00000A11_1.fuz"))
                      && o1.VoiceRename?.FilesCarried == 1,
                    $"RENAME voice carried to the renamed-name folder (files {o1.VoiceRename?.FilesCarried})");
                Check(ok1 && o1.SeqRegen is { Written: true } && File.Exists(Path.Combine(outDir1, "SEQ", "HcMgRenamed.seq")),
                    $"RENAME .seq regenerated under the new name (written {o1.SeqRegen?.Written})");

                // The side effects are REPORTED, not refused: B patches A and is now OUTSIDE the set, so it joins the
                // external referencer AND overrider warnings alongside Dep/Ovr. Renaming a patched plugin warns about
                // its patch — the existing WARN-and-proceed posture, unchanged.
                Check(o1.ExternalPlugins.Contains("HcMgDep.esp") && o1.ExternalPlugins.Contains("HcMgB.esp"),
                    $"RENAME external referencers named, including the donor's own patch (refs [{string.Join(",", o1.ExternalPlugins)}])");
                Check(o1.ExternalOverriders.Contains("HcMgOvr.esp") && o1.ExternalOverriders.Contains("HcMgB.esp"),
                    $"RENAME external overriders named, including the donor's own patch (ovr [{string.Join(",", o1.ExternalOverriders)}])");

                var rendered1 = WriteTools.RenderMerge(o1);
                Check(rendered1.Contains("a RENAME of HcMgA.esp") && !rendered1.Contains("1 donors"),
                    "RENAME headline names the operation and never says '1 donors'");
                // The headline is DERIVED from the accounting: A originates 8, so it must claim 8 records moving.
                Check(rendered1.Contains("8 records move to the new plugin's identity"),
                    "RENAME headline's record claim is derived from the accounting, not asserted beside it");
                // Shape-varying sentences, positive side. One donor cannot collide, so only one cause may be named…
                Check(rendered1.Contains("renumbered (below-floor)") && !rendered1.Contains("id collisions / below-floor"),
                    "RENAME per-donor line names ONLY the cause that can apply to one donor");
                // …and the remedy that PRESERVES the rename must lead, with combining offered second.
                Check(rendered1.Contains("re-point them at 'HcMgRenamed.esp' before the swap")
                      && rendered1.IndexOf("re-point them at", StringComparison.Ordinal)
                         < rendered1.IndexOf("re-run with them added as donors", StringComparison.Ordinal),
                    "RENAME external-referencer remedy leads with re-point, combining offered second");
                Check(rendered1.Contains("Rebuild them against 'HcMgRenamed.esp'"),
                    "RENAME external-overrider remedy leads with rebuild");
                Check(Path.GetFileName(Path.GetDirectoryName(o1.OutputPath))!.EndsWith(" renamed"),
                    $"RENAME mod folder defaults to '<output> renamed' ({Path.GetFileName(Path.GetDirectoryName(o1.OutputPath))})");
                Check(rendered1.Contains("existing SAVES") && rendered1.Contains("deactivate the donor PLUGINS"),
                    "RENAME still carries the saves warning and the MO2 swap instruction");

                Check(aBytesBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(aDir, aKey.FileName.String))),
                    "RENAME donor file byte-identical (new-file lane, the rename is a COPY under a new name)");

                // The donor list is a SET in both arms: a name repeated is one donor, so this is a rename too. Pinned
                // deliberately (#345) — the Distinct() above the count guard decides it, and it must not drift silently.
                // "Nothing can collide" is NOT "every id is kept": BuildMergeRemap keeps an id only while it sits
                // inside the write window, so a lone donor's below-floor id is renumbered with no collision anywhere.
                // Measured on the PLANNER directly, because Mutagen refuses to write a donor carrying a sub-0x800
                // originating record at all (LowerFormKeyRangeDisallowedException — verified while trying to fixture
                // one); such plugins come from other tools, and houseCARL reads them fine. This is the arm the prose
                // rests on — every written-fixture id is >= 0xA01, so no arm over them could tell a correct claim from
                // an over-broad one.
                {
                    var soloKey = new ModKey("HcMgSolo", ModType.Plugin);
                    var soloOut = new ModKey("HcMgSoloRenamed", ModType.Plugin);
                    var soloDonors = new List<(string Donor, IReadOnlyList<FormKey> Keys)>
                    {
                        ("HcMgSolo.esp", new[] { new FormKey(soloKey, 0xC01), new FormKey(soloKey, 0x123) })
                    };
                    var solo = RemapEngine.BuildMergeRemap(soloDonors, soloOut, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
                    var dSolo = solo.Donors.FirstOrDefault();
                    bool floorOk = solo.Success
                        && solo.Dict[new FormKey(soloKey, 0xC01)] == new FormKey(soloOut, 0xC01)      // in-window: id kept
                        && solo.Dict[new FormKey(soloKey, 0x123)].ID >= RemapEngine.EslFloor          // below floor: moved up
                        && solo.Dict[new FormKey(soloKey, 0x123)].ModKey == soloOut
                        && dSolo is { Kept: 1, Renumbered: 1 };
                    Check(floorOk,
                        $"RENAME a below-floor id renumbers even with nothing to collide with (kept {dSolo?.Kept}, renumbered {dSolo?.Renumbered})");
                }

                // HEADER LOSS, positive side: the merged plugin is built as a bare mod, so a donor's light flag and
                // its Author/Description do not come along. Both are stated rather than refused, and the ESL note's
                // compact remedy carries the consequence that makes it honest — compact renumbers from the floor.
                var oEsl = svc.MergePlugins(new[] { "HcMgEsl.esp" }, "HcMgEslRenamed.esp");
                bool eslDropped = false;
                if (oEsl.Success && !string.IsNullOrEmpty(oEsl.OutputPath) && File.Exists(oEsl.OutputPath))
                {
                    using var em = SkyrimMod.CreateFromBinaryOverlay(oEsl.OutputPath, SkyrimRelease.SkyrimSE);
                    eslDropped = !em.IsSmallMaster                                        // the loss is REAL, not just claimed
                              && string.IsNullOrEmpty(em.ModHeader.Author)
                              && string.IsNullOrEmpty(em.ModHeader.Description);
                }
                var renderedEsl = oEsl.Success ? WriteTools.RenderMerge(oEsl) : "";
                Check(eslDropped, $"RENAME the donor's light flag and header text really are absent from the output (success {oEsl.Success})");
                Check(renderedEsl.Contains("HcMgEsl.esp carried the LIGHT (ESL) status")
                      && renderedEsl.Contains("takes a full load-order slot")
                      && renderedEsl.Contains("renumbers object ids from 0x800 upward"),
                    "RENAME the light-flag loss is REPORTED, with the compact remedy's own cost stated");
                Check(renderedEsl.Contains("header Author/Description carried by HcMgEsl.esp")
                      && !renderedEsl.Contains("housecarl_create_plugin on"),
                    "RENAME the header-text loss is stated bare — no remedy invented for it");
                // ONE home for the compact recommendation: where the qualified note fired, the flat closing tail must
                // not repeat it un-costed. Its negative is the plain merge, which has no note and so keeps the tail.
                Check(!renderedEsl.Contains("Want it light?"),
                    "RENAME the flat 'want it light' tail steps aside when the qualified ESL note fired");

                // Light and master each arrive by EXTENSION as well as by flag; a flag-only reading misses these two
                // donors entirely, which is the whole reason they exist.
                var oEslExt = svc.MergePlugins(new[] { "HcMgEslExt.esl" }, "HcMgEslExtRenamed.esp");
                var renderedEslExt = oEslExt.Success ? WriteTools.RenderMerge(oEslExt) : "";
                Check(oEslExt.Success && renderedEslExt.Contains("HcMgEslExt.esl carried the LIGHT (ESL) status"),
                    $"RENAME a .esl-EXTENSION donor counts as light even with the header bit unset (success {oEslExt.Success})");
                var oEsm = svc.MergePlugins(new[] { "HcMgEsm.esm" }, "HcMgEsmRenamed.esp");
                var renderedEsm = oEsm.Success ? WriteTools.RenderMerge(oEsm) : "";
                Check(oEsm.Success && renderedEsm.Contains("HcMgEsm.esm carried MASTER status")
                      && renderedEsm.Contains("is NOT flagged as a master")
                      && !renderedEsm.Contains("run housecarl_"),
                    $"RENAME the master-status loss is stated, with no remedy invented (success {oEsm.Success})");

                // The residual the advisor flagged: both notes are keyed on what the DONORS carried, never on the
                // donor count. A flagged donor merged WITH a plain one must still raise them, or the property-keying
                // has quietly become count-keying (PR #346 R5 / PR #360 F2 class).
                var oMixed = svc.MergePlugins(new[] { "HcMgEsl.esp", "HcMgEsm.esm" }, "HcMgMixed.esp");
                var renderedMixed = oMixed.Success ? WriteTools.RenderMerge(oMixed) : "";
                Check(oMixed.Success && oMixed.Donors.Count == 2
                      && renderedMixed.Contains("carried the LIGHT (ESL) status")
                      && renderedMixed.Contains("carried MASTER status")
                      && renderedMixed.Contains("header Author/Description carried by")
                      && renderedMixed.Contains("from 2 donors"),
                    $"MERGE all three header-loss notes fire on the MULTI-donor path too (donors {oMixed.Donors.Count})");

                // A PURE-OVERRIDE donor originates nothing, so the headline must not claim records took a new
                // identity — the mis-named patch this capability exists for is exactly this shape.
                var oOvr = svc.MergePlugins(new[] { "HcMgOvr.esp" }, "HcMgOvrRenamed.esp");
                var renderedOvr = oOvr.Success ? WriteTools.RenderMerge(oOvr) : "";
                Check(oOvr.Success && oOvr.RecordsRenumbered == 0 && oOvr.RecordsCopied == 1
                      && renderedOvr.Contains("its 1 override is now served by a plugin under a new name")
                      && !renderedOvr.Contains("records move to the new plugin's identity"),
                    $"RENAME a pure-override donor states the override COUNT it read (copied {oOvr.RecordsCopied}, renumbered {oOvr.RecordsRenumbered})");
                // The runtime-config sentence carries no per-record claim at all, so it reads identically for a donor
                // that originates nothing — which is the point. Scoping it to "a line that names a donor" already
                // excludes a record the donor merely OVERRODE, because such a line names that record's MASTER. An
                // earlier draft spelled the exemption out and, in doing so, claimed the output owned records this
                // very donor does not have; the accounting line above is the one that speaks about records.
                Check(renderedOvr.Contains(WriteSentences.MergeRuntimeConfigs),
                    "RENAME the runtime-config sentence is unchanged by a donor that originates nothing");

                // The third thing the accounting can say. An empty donor takes neither of the arms above, and the
                // middle arm used to claim overrides it never read — which was wrong for exactly this plugin.
                var oEmpty = svc.MergePlugins(new[] { "HcMgEmpty.esp" }, "HcMgEmptyRenamed.esp");
                var renderedEmpty = oEmpty.Success ? WriteTools.RenderMerge(oEmpty) : "";
                Check(oEmpty.Success && oEmpty.RecordsCopied == 0 && oEmpty.RecordsRenumbered == 0
                      && renderedEmpty.Contains("it carries no records at all, so nothing moved and nothing is overridden")
                      && !renderedEmpty.Contains("override is now served") && !renderedEmpty.Contains("overrides are now served"),
                    $"RENAME an EMPTY donor claims neither moves nor overrides (copied {oEmpty.RecordsCopied}, success {oEmpty.Success})");

                var oDup = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgA.esp" }, "HcMgDup.esp");
                Check(oDup.Success && oDup.Donors.Count == 1,
                    $"RENAME a donor named twice is ONE donor, still a rename ({(oDup.Success ? $"{oDup.Donors.Count} donor" : "ERR " + oDup.Error)})");

                // Two IDENTICAL strings collapse under ANY comparer, so the arm above cannot see the comparer — and the
                // comparer IS the set rule. Only a case-differing duplicate pins it: drop OrdinalIgnoreCase and this
                // call merges a plugin with ITSELF, reporting nine self-conflicts and announcing "2 donors".
                var oCase = svc.MergePlugins(new[] { "HcMgA.esp", "HCMGA.ESP" }, "HcMgCase.esp");
                Check(oCase.Success && oCase.Donors.Count == 1 && oCase.Conflicts.Count == 0
                      && WriteTools.RenderMerge(oCase).Contains("a RENAME of"),
                    $"RENAME donor names differing only by CASE are ONE donor ({(oCase.Success ? $"{oCase.Donors.Count} donor, {oCase.Conflicts.Count} conflicts" : "ERR " + oCase.Error)})");
            }

            // ---- LOCALIZED (#362): donors whose .STRINGS live in the game-Data folder, not their own mod folder ----
            //      Its own instance, because the fixture's load-bearing part is a game-Data Skyrim.esm (see
            //      LocalizedStringsFixture) and the order above deliberately has none.
            {
                var locRoot = Path.Combine(root, "loc");
                var la = new LocalizedStringsFixture.Spec("LocA", new ModKey("HcMgLocA", ModType.Plugin), "LOC A NAME", "LOC A DESC");
                var lb = new LocalizedStringsFixture.Spec("LocB", new ModKey("HcMgLocB", ModType.Plugin), "LOC B NAME", "LOC B DESC");
                var fx = LocalizedStringsFixture.Build(locRoot, new[] { la, lb });
                var locStore = new UserConfigStore(Path.Combine(locRoot, "houseCARL.user.json"));
                using var locSvc = LoadOrderService.WithInstance(fx.Instance, 0, locStore);
                locSvc.Stats();

                // The fixture really is the blanking shape: read the donor ON DISK with the bare overlay and it comes
                // back empty. Without this arm every arm below would pass just as well on a non-localized fixture, or
                // on one whose strings never left the mod folder — the two ways this fixture could go quietly vacuous.
                var donorPath = Path.Combine(fx.Mods, la.ModFolder, la.Key.FileName.String);
                var bare = LocalizedStringsFixture.ReadBackBare(donorPath, LocalizedStringsFixture.WeaponEdid(la));
                Check(string.IsNullOrEmpty(bare.Name) && string.IsNullOrEmpty(bare.Desc),
                    $"LOCALIZED fixture: the donor read with the BARE overlay is blank (Name='{bare.Name}' Desc='{bare.Desc}')");

                // RENAME — the single-donor shape (#345). Every localized value in the plugin rides on this one open,
                // so a bare open here blanks the whole renamed plugin.
                {
                    var lo = locSvc.MergePlugins(new[] { la.Key.FileName.String }, "HcMgLocRen.esp");
                    var rb = lo.Success && File.Exists(lo.OutputPath)
                        ? LocalizedStringsFixture.ReadBackBare(lo.OutputPath, LocalizedStringsFixture.WeaponEdid(la))
                        : (Name: null, Desc: null);
                    Check(lo.Success && rb.Name == la.Name && rb.Desc == la.Desc,
                        $"LOCALIZED single-donor merge (RENAME) carries FULL+DESC (Name='{rb.Name}' Desc='{rb.Desc}'{(lo.Success ? "" : ", ERR " + lo.Error)})");
                }

                // COMBINE — the multi-donor shape, both donors localized: neither is blanked, and the fix is not
                // keyed on donor count (settled decision 4 — one fix, no shape conditional).
                {
                    var lo = locSvc.MergePlugins(new[] { la.Key.FileName.String, lb.Key.FileName.String }, "HcMgLocComb.esp");
                    var rbA = lo.Success && File.Exists(lo.OutputPath)
                        ? LocalizedStringsFixture.ReadBackBare(lo.OutputPath, LocalizedStringsFixture.WeaponEdid(la))
                        : (Name: null, Desc: null);
                    var rbB = lo.Success && File.Exists(lo.OutputPath)
                        ? LocalizedStringsFixture.ReadBackBare(lo.OutputPath, LocalizedStringsFixture.WeaponEdid(lb))
                        : (Name: null, Desc: null);
                    Check(lo.Success && rbA.Name == la.Name && rbA.Desc == la.Desc && rbB.Name == lb.Name && rbB.Desc == lb.Desc,
                        $"LOCALIZED multi-donor merge carries FULL+DESC for EVERY donor (A='{rbA.Name}'/'{rbA.Desc}' B='{rbB.Name}'/'{rbB.Desc}'{(lo.Success ? "" : ", ERR " + lo.Error)})");
                }

                // OUTPUT-NOT-LOCALIZED — the written merge is a bare SkyrimMod, so it carries no localized header flag
                // and its strings are inline. This is what makes every read-back above legitimate with no dataDir and
                // no Strings folder beside the output: if the output were flagged localized without a strings writer,
                // the reads would be measuring a folder-adjacent lookup instead of the bytes, and a merge would be
                // shipping a plugin whose strings live nowhere.
                {
                    var lo = locSvc.MergePlugins(new[] { la.Key.FileName.String }, "HcMgLocFlag.esp");
                    bool flagOk = false, noStringsFolder = false;
                    if (lo.Success && File.Exists(lo.OutputPath))
                    {
                        using var ov = SkyrimMod.CreateFromBinaryOverlay(lo.OutputPath, SkyrimRelease.SkyrimSE);
                        flagOk = !ov.UsingLocalization;
                        noStringsFolder = !Directory.Exists(Path.Combine(Path.GetDirectoryName(lo.OutputPath)!, "Strings"));
                    }
                    Check(flagOk && noStringsFolder,
                        $"LOCALIZED output is written NON-localized with strings inline (flagClear={flagOk} noStringsFolder={noStringsFolder})");

                    // #435 — the de-localization above is a change in the output's NATURE, and the report has to say
                    // so. The donor must be NAMED: a generic note would not tell a caller with ten donors which of
                    // them stopped being a translated plugin.
                    var rendered = lo.Success ? WriteTools.RenderMerge(lo) : "";
                    Check(rendered.Contains("flagged LOCALIZED") && rendered.Contains(la.Key.FileName.String)
                          && rendered.Contains("is NOT localized"),
                        "LOCALIZED de-localization is STATED in the merge report, naming the donor");
                }
            }

            // ---- NOWHERE (#371): a localized donor whose strings exist NEITHER beside it NOR in game-Data. There is
            //      no dataDir that resolves this one, so every value reads blank — and the merge REFUSES rather than
            //      writing those blanks into a plugin the caller keeps. The arm pins the refusal AND that nothing was
            //      written: a refusal that still left an output would be the worse failure.
            {
                var resRoot = Path.Combine(root, "res");
                var lr = new LocalizedStringsFixture.Spec("LocGone", new ModKey("HcMgLocGone", ModType.Plugin),
                    "GONE NAME", "GONE DESC", StringsNowhere: true);
                var fx = LocalizedStringsFixture.Build(resRoot, new[] { lr });
                var resStore = new UserConfigStore(Path.Combine(resRoot, "houseCARL.user.json"));
                using var resSvc = LoadOrderService.WithInstance(fx.Instance, 0, resStore);
                resSvc.Stats();

                var lo = resSvc.MergePlugins(new[] { lr.Key.FileName.String }, "HcMgResRen.esp");
                // The refusal must NAME the cause: a generic failure would pass this arm while telling the modder
                // nothing about where their text went.
                bool named = !lo.Success && lo.Error is not null
                             && lo.Error.Contains("LOCALIZED") && lo.Error.Contains(".STRINGS");
                bool nothingWritten = string.IsNullOrEmpty(lo.OutputPath) || !File.Exists(lo.OutputPath);
                Check(named && nothingWritten,
                    $"NOWHERE-resolving strings REFUSE the merge, named, nothing written (success={lo.Success} named={named} nothingWritten={nothingWritten} err='{(lo.Error ?? "").Split('.')[0]}')");
            }

            // ---- DECLARER: a plugin OUTSIDE the merge that lists a donor as a master and references none of its
            //      records. Its own instance, so the counts and lists every arm above asserts stay as they are. The
            //      detection has unit coverage; what this arm owes is the JOIN — the service reads declared masters,
            //      carries them into the outcome, and the rendered report a caller actually reads names the plugin and
            //      what it declares. A break anywhere along that path is a warning nobody ever sees.
            {
                var decRoot = Path.Combine(root, "decl");
                string decInstance = Path.Combine(decRoot, "instance");
                string decProfiles = Path.Combine(decInstance, "profiles", "Default");
                string decMods = Path.Combine(decInstance, "mods");
                Directory.CreateDirectory(decProfiles); Directory.CreateDirectory(decMods);
                Directory.CreateDirectory(Path.Combine(decRoot, "game", "Data"));
                File.WriteAllText(Path.Combine(decInstance, "ModOrganizer.ini"),
                    "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                    + Path.Combine(decRoot, "game").Replace(@"\", @"\\") + ")\r\n");

                var donorKey = new ModKey("HcMgDeclDonor", ModType.Plugin);
                var donorDir = Path.Combine(decMods, "DeclDonorMod"); Directory.CreateDirectory(donorDir);
                var donorPath = Path.Combine(donorDir, donorKey.FileName.String);
                {
                    var m = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
                    m.Weapons.Add(new Weapon(new FormKey(donorKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDeclDonorWeap" });
                    m.BeginWrite.ToPath(donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
                }

                // The declarer: its own record, no link into the donor, and the donor carried in the master table
                // anyway — the trimmed compat patch, which no walk over links or record identity can see.
                var declKey = new ModKey("HcMgDeclOnly", ModType.Plugin);
                var declDir = Path.Combine(decMods, "DeclOnlyMod"); Directory.CreateDirectory(declDir);
                {
                    using var donorOv = SkyrimMod.CreateFromBinaryOverlay(donorPath, SkyrimRelease.SkyrimSE);
                    var m = new SkyrimMod(declKey, SkyrimRelease.SkyrimSE);
                    m.Weapons.Add(new Weapon(new FormKey(declKey, 0xB01), SkyrimRelease.SkyrimSE) { EditorID = "HcMgDeclOwnWeap" });
                    m.BeginWrite.ToPath(Path.Combine(declDir, declKey.FileName.String))
                        .WithLoadOrder(new ISkyrimModGetter[] { donorOv }).WithExtraIncludedMasters(donorKey).Write();
                }

                File.WriteAllText(Path.Combine(decProfiles, "loadorder.txt"),
                    "# header\r\n" + string.Join("\r\n", donorKey.FileName, declKey.FileName) + "\r\n");
                File.WriteAllText(Path.Combine(decProfiles, "plugins.txt"),
                    string.Join("\r\n", "*" + donorKey.FileName, "*" + declKey.FileName) + "\r\n");
                File.WriteAllText(Path.Combine(decProfiles, "modlist.txt"),
                    "# header\r\n" + string.Join("\r\n", "+DeclOnlyMod", "+DeclDonorMod") + "\r\n");

                var decStore = new UserConfigStore(Path.Combine(decRoot, "houseCARL.user.json"));
                using var decSvc = LoadOrderService.WithInstance(decInstance, 0, decStore);
                decSvc.Stats();

                var dm = decSvc.MergePlugins(new[] { donorKey.FileName.String }, "HcMgDeclOut.esp");
                // In NEITHER existing list — that is what makes this a third category rather than a second heading
                // over the same plugins, and an arm reading only the rendered text would pass on a report that had
                // quietly folded it into the referencers.
                bool inOutcome = dm.Success && dm.MasterDeclarers is { Count: 1 }
                                 && string.Equals(dm.MasterDeclarers[0].Plugin, declKey.FileName.String, StringComparison.OrdinalIgnoreCase)
                                 && !dm.ExternalPlugins.Contains(declKey.FileName.String, StringComparer.OrdinalIgnoreCase)
                                 && !dm.ExternalOverriders.Contains(declKey.FileName.String, StringComparer.OrdinalIgnoreCase);
                var decRendered = dm.Success ? WriteTools.RenderMerge(dm) : "";
                bool warned = decRendered.Contains("DECLARE a donor as a MASTER")
                              && decRendered.Contains(declKey.FileName.String)
                              && decRendered.Contains("declares " + donorKey.FileName.String);
                Check(inOutcome && warned,
                    "DECLARER a real declarer-only dependent reaches the rendered merge report, named with what it "
                    + $"declares (inOutcome={inOutcome} warned={warned}{(dm.Success ? "" : ", ERR " + dm.Error)})");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== merge-service-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>File an interior cell into a mod's Cells block tree by its FormID digits (mirrors WriteEngine.AddInteriorCell).</summary>
    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var records = mod.Cells.Records;
        var block = records.FirstOrDefault(b => b.BlockNumber == blockN);
        if (block is null) { block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock }; records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subN);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }
}

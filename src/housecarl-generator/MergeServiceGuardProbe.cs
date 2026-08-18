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
///   REFUSE    — ZERO donors / unknown donor / output already active / output == donor / .esl output, all loud,
///               nothing written.
///   UNTOUCHED — the donor files are byte-identical after the merge (new-file lane only).
/// Run: dotnet run --project src/housecarl-generator merge-service-guard
/// </summary>
public static class MergeServiceGuardProbe
{
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

            // ---- profile files (load order: Base, A, B, Dep, Ovr — B AFTER A: the patch wins) ----
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"),
                "# header\r\n" + string.Join("\r\n", baseKey.FileName, aKey.FileName, bKey.FileName, depKey.FileName, ovrKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"),
                string.Join("\r\n", "*" + baseKey.FileName, "*" + aKey.FileName, "*" + bKey.FileName, "*" + depKey.FileName, "*" + ovrKey.FileName) + "\r\n");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
                "# header\r\n" + string.Join("\r\n", "+OvrMod", "+DepMod", "+BMod", "+AMod", "+BaseMod") + "\r\n");

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
                    // REMAP-TO-OUTPUT: every one of A's originating records is under the OUTPUT ModKey, at its OWN
                    // object id — the rename claim in one assertion. A's records are NOT still keyed to HcMgA.esp.
                    keysOk = rm.Weapons.Any(w => w.EditorID == "HcMgAWeap" && w.FormKey == new FormKey(renamedKey, 0xA01))
                          && rm.DialogTopics.Any(t => t.FormKey == new FormKey(renamedKey, 0xA10))
                          && rm.Npcs.Any(n => n.FormKey == new FormKey(renamedKey, 0xA20))
                          && rm.Quests.Any(q => q.FormKey == new FormKey(renamedKey, 0xA30))
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

                var oDup = svc.MergePlugins(new[] { "HcMgA.esp", "HcMgA.esp" }, "HcMgDup.esp");
                Check(oDup.Success && oDup.Donors.Count == 1,
                    $"RENAME a donor named twice is ONE donor, still a rename ({(oDup.Success ? $"{oDup.Donors.Count} donor" : "ERR " + oDup.Error)})");
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

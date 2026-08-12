using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the SEQ writer (nested/dialogue plan Layer B unit D — housecarl_write_seq).
/// Pins the start-game-enabled-quest .seq contract end-to-end: the CORE encoding (<see cref="SeqFile"/>) AND the SERVICE
/// wire (<see cref="LoadOrderService.WriteSeq"/>) over a synthetic MO2 instance. No Skyrim.esm — synthesizes its own
/// masters + patch.
///
/// The load-bearing claim it defends: a .seq lists each SGE quest as its plugin-LOCAL, master-INDEX on-disk FormID
/// (own/new records at the slot AFTER the last master, i.e. high byte = master count; an override at the overridden
/// master's index), little-endian, NEVER the runtime 0xFE / load-order address — so the file is load-order-independent
/// and needs no runtime-FormID bridge. The ON-DISK-MATCH arm proves the computed FormIDs equal what Mutagen ACTUALLY
/// wrote into the plugin's record headers (a raw byte parse), so a future write-path/Mutagen change that shifted the
/// encoding to FE-space or a different index would turn this RED.
///
/// Run: dotnet run --project src/housecarl-generator -- seq-write-guard
///
/// Arms (ALL required):
///   SERIALIZE-LE   — Serialize([0x0500AA02]) == bytes 02 AA 00 05 (4-byte little-endian, no header).
///   ENCODE-OWN     — OnDiskFormId(own FormKey, [m]) == (1&lt;&lt;24)|id (own record sits at master COUNT, not a constant 0).
///   ENCODE-OVERRIDE— OnDiskFormId(master FormKey, [m]) == (0&lt;&lt;24)|id (an override carries the overridden master's index).
///   SGE-ONLY       — Build lists exactly the non-deleted Start-Game-Enabled quests (a RunOnce-only quest is EXCLUDED).
///   OVERRIDE-SGE   — an override that NEWLY flags SGE is included, at the overridden master's index (high byte != 0xFE).
///   DELETED-SKIP   — a deleted SGE quest is NOT in the .seq (the same skip the dialogue validator applies to deleted lines).
///   HIGH-BYTE-COUNT— the own SGE quest's high byte == the plugin's master count (1 here, 2 in the 2-master setup — tracks
///                    the count, never a fixed value or a global load-order index).
///   ESL-NEVER-FE   — an ESL (light) patch's own SGE quest ALSO encodes master-index (high byte == master count, never 0xFE).
///   ON-DISK-MATCH  — every built FormID is present among the QUST records' ACTUAL on-disk FormIDs (raw byte parse) — the
///                    computed master-index encoding == what Mutagen wrote; RED to an FE-space / wrong-index regression.
///   SERVICE-WRITE  — LoadOrderService.WriteSeq lands &lt;ModFolder&gt;\SEQ\&lt;plugin&gt;.seq with the expected bytes.
///   SAME-FOLDER    — a plugin inside a houseCARL-owned folder defaults the .seq INTO that folder (WroteIntoPluginFolder).
///   EMPTY-NOOP     — a plugin with NO SGE quests writes nothing, cuts no folder (no orphan), reports the clean no-op.
///   REFUSE-NOFILE  — WriteSeq on a missing plugin path refuses, named (Q3).
///
/// #312 (output_dir= + the byte-identical no-op):
///   PURE-SEQ-CONTRACT / PURE-SEQ-DEPLOY — the pure path contract: SEQ\ appended once (double-SEQ guard), and the
///                    deployability rule with a warning worded for a .seq's own consequence (silently dead quests).
///   OUTPUT-DIR / -DEPLOYS / -OUTSIDE    — the .seq lands in the USER's folder, no houseCARL folder cut and no owner
///                    marker stamped; a deployable folder carries no warning, an off-tree one does.
///   USER-OWNED-SURVIVES — residue cleanup never deletes a folder the user named.
///   UNCHANGED / -DIFFERS — a byte-identical destination is left ALONE (proved by mtime, not by the flag), while a
///                    stale one is rewritten (the compare is on BYTES, never existence).
///   TOOL-LANE      — output_dir= wins over patch=/into= and the ignored lane is STATED (Q3).
///   RENDER-UNCHANGED / JSON-UNCHANGED — the no-op renders as its own state on both transports (written=false).
/// </summary>
internal static class SeqWriteGuardProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — SEQ writer (housecarl_write_seq, Layer B unit D)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-seq-write-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);

            // ====================== PURE CORE: serialize + encode ======================
            {
                var bytes = SeqFile.Serialize(new uint[] { 0x0500AA02u });
                Check(bytes.Length == 4 && bytes[0] == 0x02 && bytes[1] == 0xAA && bytes[2] == 0x00 && bytes[3] == 0x05,
                    $"SERIALIZE-LE 0x0500AA02 → 02 AA 00 05 — got {BitConverter.ToString(bytes)}");

                var pKey = new ModKey("HcSeqEnc", ModType.Plugin);
                var mKey = new ModKey("HcSeqEncMaster", ModType.Master);
                var masters = new List<ModKey> { mKey };
                uint ownEnc = SeqFile.OnDiskFormId(new FormKey(pKey, 0x000800), masters);    // own: ModKey not in masters → slot = count(1)
                uint ovEnc = SeqFile.OnDiskFormId(new FormKey(mKey, 0x00AA02), masters);     // override of a master → slot = its index(0)
                Check(ownEnc == 0x01000800u, $"ENCODE-OWN own record at master COUNT — got 0x{ownEnc:X8} (expect 0x01000800)");
                Check(ovEnc == 0x0000AA02u, $"ENCODE-OVERRIDE override at master index — got 0x{ovEnc:X8} (expect 0x0000AA02)");
            }

            // ====================== CORE BUILD: SETUP A (1 master) ======================
            // master M with a (non-SGE) quest; patch P overrides it (→ SGE, forces M as a master) + own SGE + own RunOnce +
            // own deleted-SGE. Expect the .seq = { own-SGE @ high 0x01, override @ high 0x00 }.
            string aMaster = Path.Combine(root, "A", "HcSeqAMaster.esm");
            string aPatch = Path.Combine(root, "A", "HcSeqAPatch.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(aMaster)!);
            FormKey mQuestFk;
            {
                var m = new SkyrimMod(new ModKey("HcSeqAMaster", ModType.Master), SkyrimRelease.SkyrimSE);
                var qm = m.Quests.AddNew(); qm.EditorID = "HcSeqAMasterQuest"; qm.Flags = Quest.Flag.RunOnce; // master copy: NOT SGE
                mQuestFk = qm.FormKey;
                m.BeginWrite.ToPath(aMaster).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }
            FormKey ownFk, plainFk, delFk;
            {
                using var mOv = SkyrimMod.CreateFromBinaryOverlay(aMaster, SkyrimRelease.SkyrimSE);
                var p = new SkyrimMod(new ModKey("HcSeqAPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                var ov = p.Quests.GetOrAddAsOverride(mOv.Quests.First(x => x.FormKey == mQuestFk));
                ov.Flags = Quest.Flag.StartGameEnabled;                                   // override NEWLY flags it SGE
                var own = p.Quests.AddNew(); own.EditorID = "HcSeqAOwn"; own.Flags = Quest.Flag.StartGameEnabled; ownFk = own.FormKey;
                var plain = p.Quests.AddNew(); plain.EditorID = "HcSeqAPlain"; plain.Flags = Quest.Flag.RunOnce; plainFk = plain.FormKey;
                var del = p.Quests.AddNew(); del.EditorID = "HcSeqADel"; del.Flags = Quest.Flag.StartGameEnabled; del.IsDeleted = true; delFk = del.FormKey;
                p.BeginWrite.ToPath(aPatch).WithLoadOrder(new[] { (ISkyrimModGetter)mOv }).NoNextFormIDProcessing().Write();
            }

            var builtA = SeqFile.Build(aPatch);
            var aFks = builtA.Quests.Select(q => q.FormKey).ToHashSet();
            // master list of the written patch (for the high-byte assertions)
            List<ModKey> aMasters;
            using (var pOv = SkyrimMod.CreateFromBinaryOverlay(aPatch, SkyrimRelease.SkyrimSE))
                aMasters = pOv.ModHeader.MasterReferences.Select(mr => mr.Master).ToList();

            Check(aFks.Contains(ownFk), $"SGE-ONLY own SGE quest included — {ownFk} ∈ {{{string.Join(",", aFks)}}}");
            Check(aFks.Contains(mQuestFk), $"OVERRIDE-SGE override that newly flags SGE included — {mQuestFk} present");
            Check(!aFks.Contains(plainFk), $"SGE-ONLY RunOnce-only quest EXCLUDED — {plainFk} absent");
            Check(!aFks.Contains(delFk), $"DELETED-SKIP deleted SGE quest EXCLUDED — {delFk} absent");

            var ownSeq = builtA.Quests.Single(q => q.FormKey == ownFk);    // present (asserted above) — Single fails loud if a reorder ever breaks that
            var ovSeq = builtA.Quests.Single(q => q.FormKey == mQuestFk);
            byte ownHi = (byte)((ownSeq.OnDiskFormId >> 24) & 0xFF);
            byte ovHi = (byte)((ovSeq.OnDiskFormId >> 24) & 0xFF);
            Check(ownHi == aMasters.Count, $"HIGH-BYTE-COUNT own quest high byte == master count — 0x{ownHi:X2} (masters={aMasters.Count})");
            Check(ovHi == aMasters.IndexOf(mQuestFk.ModKey) && ovHi != 0xFE,
                $"OVERRIDE high byte == overridden master index (never 0xFE) — 0x{ovHi:X2} (index={aMasters.IndexOf(mQuestFk.ModKey)})");

            // ON-DISK-MATCH: every built FormID must appear among the patch's ACTUAL on-disk QUST record FormIDs.
            var rawOnDisk = AllRecordFormIds(aPatch, "QUST").ToHashSet();
            bool onDiskMatch = builtA.Quests.All(q => rawOnDisk.Contains(q.OnDiskFormId))
                               && builtA.Quests.All(q => ((q.OnDiskFormId >> 24) & 0xFF) != 0xFE);
            Check(onDiskMatch,
                $"ON-DISK-MATCH built FormIDs == actual on-disk record FormIDs (master-index, never FE) — built [{string.Join(",", builtA.Quests.Select(q => $"0x{q.OnDiskFormId:X8}"))}] raw [{string.Join(",", rawOnDisk.Select(x => $"0x{x:X8}"))}]");

            // ====================== CORE BUILD: SETUP B (2 masters) — high byte tracks COUNT ======================
            string bM1 = Path.Combine(root, "B", "HcSeqBM1.esm");
            string bM2 = Path.Combine(root, "B", "HcSeqBM2.esm");
            string bPatch = Path.Combine(root, "B", "HcSeqBPatch.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(bM1)!);
            FormKey b1Fk, b2Fk;
            { var m = new SkyrimMod(new ModKey("HcSeqBM1", ModType.Master), SkyrimRelease.SkyrimSE); var q = m.Quests.AddNew(); q.EditorID = "B1Q"; q.Flags = Quest.Flag.RunOnce; b1Fk = q.FormKey; m.BeginWrite.ToPath(bM1).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write(); }
            { var m = new SkyrimMod(new ModKey("HcSeqBM2", ModType.Master), SkyrimRelease.SkyrimSE); var q = m.Quests.AddNew(); q.EditorID = "B2Q"; q.Flags = Quest.Flag.RunOnce; b2Fk = q.FormKey; m.BeginWrite.ToPath(bM2).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write(); }
            FormKey bOwnFk;
            {
                using var m1 = SkyrimMod.CreateFromBinaryOverlay(bM1, SkyrimRelease.SkyrimSE);
                using var m2 = SkyrimMod.CreateFromBinaryOverlay(bM2, SkyrimRelease.SkyrimSE);
                var p = new SkyrimMod(new ModKey("HcSeqBPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                p.Quests.GetOrAddAsOverride(m1.Quests.First(x => x.FormKey == b1Fk));      // force M1 as a master
                p.Quests.GetOrAddAsOverride(m2.Quests.First(x => x.FormKey == b2Fk));      // force M2 as a master
                var own = p.Quests.AddNew(); own.EditorID = "BOwn"; own.Flags = Quest.Flag.StartGameEnabled; bOwnFk = own.FormKey;
                p.BeginWrite.ToPath(bPatch).WithLoadOrder(new[] { (ISkyrimModGetter)m1, (ISkyrimModGetter)m2 }).NoNextFormIDProcessing().Write();
            }
            var builtB = SeqFile.Build(bPatch);
            var bOwn = builtB.Quests.Single(q => q.FormKey == bOwnFk);
            byte bOwnHi = (byte)((bOwn.OnDiskFormId >> 24) & 0xFF);
            List<ModKey> bMasters;
            using (var pOv = SkyrimMod.CreateFromBinaryOverlay(bPatch, SkyrimRelease.SkyrimSE))
                bMasters = pOv.ModHeader.MasterReferences.Select(mr => mr.Master).ToList();
            Check(bMasters.Count == 2 && bOwnHi == 2,
                $"HIGH-BYTE-COUNT(2 masters) own high byte == 2 — 0x{bOwnHi:X2} (masters={bMasters.Count}) — proves it tracks the count");

            // ====================== CORE BUILD: SETUP E (ESL / light patch) — the most-doubted "never 0xFE" case ======================
            // An ESL-flagged plugin's OWN records STILL use master-INDEX encoding on disk (high byte = master count), NOT the
            // runtime 0xFE light-space (that's computed at load time). master M (a quest); a LIGHT patch overrides it (forces M)
            // + an own SGE quest at 0x800 (legal ESL object-id) → own high byte == 1, never 0xFE.
            string eMaster = Path.Combine(root, "E", "HcSeqEMaster.esm");
            string ePatch = Path.Combine(root, "E", "HcSeqEPatch.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(eMaster)!);
            FormKey emQuestFk;
            { var m = new SkyrimMod(new ModKey("HcSeqEMaster", ModType.Master), SkyrimRelease.SkyrimSE); var q = m.Quests.AddNew(); q.EditorID = "EMQ"; q.Flags = Quest.Flag.RunOnce; emQuestFk = q.FormKey; m.BeginWrite.ToPath(eMaster).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write(); }
            FormKey eOwnFk;
            {
                using var mOv = SkyrimMod.CreateFromBinaryOverlay(eMaster, SkyrimRelease.SkyrimSE);
                var p = new SkyrimMod(new ModKey("HcSeqEPatch", ModType.Plugin), SkyrimRelease.SkyrimSE) { IsSmallMaster = true }; // ESL-flagged patch
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                p.Quests.GetOrAddAsOverride(mOv.Quests.First(x => x.FormKey == emQuestFk));   // forces M as a master
                var own = p.Quests.AddNew(); own.EditorID = "EOwn"; own.Flags = Quest.Flag.StartGameEnabled; eOwnFk = own.FormKey;
                p.BeginWrite.ToPath(ePatch).WithLoadOrder(new[] { (ISkyrimModGetter)mOv }).NoNextFormIDProcessing().Write();
            }
            var builtE = SeqFile.Build(ePatch);
            var eOwn = builtE.Quests.Single(q => q.FormKey == eOwnFk);
            byte eOwnHi = (byte)((eOwn.OnDiskFormId >> 24) & 0xFF);
            var eRaw = AllRecordFormIds(ePatch, "QUST").ToHashSet();
            bool eslOk = eOwnHi == 1
                         && builtE.Quests.All(q => ((q.OnDiskFormId >> 24) & 0xFF) != 0xFE)
                         && builtE.Quests.All(q => eRaw.Contains(q.OnDiskFormId));
            Check(eslOk,
                $"ESL-NEVER-FE light patch's SGE quest encodes master-index (own high 0x{eOwnHi:X2}==1, never 0xFE), ON-DISK-MATCH holds — built [{string.Join(",", builtE.Quests.Select(q => $"0x{q.OnDiskFormId:X8}"))}]");

            // ====================== SERVICE WIRE over a synthetic MO2 instance ======================
            string instance = Path.Combine(root, "instance");
            string profiles = Path.Combine(instance, "profiles", "Default");
            string mods = Path.Combine(instance, "mods");
            Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(root, "game", "Data"));   // Mo2Instance.Resolve requires <gamePath>\Data
            File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
            File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n");
            File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "");
            File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n");

            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var svc = LoadOrderService.WithInstance(instance, 0, store);

            // SERVICE-WRITE + SAME-FOLDER: a houseCARL-OWNED patch folder under mods, holding an SGE-quest plugin.
            string ownedFolder = Path.Combine(mods, "houseCARL - HcSeqSvc");
            Directory.CreateDirectory(ownedFolder);
            File.WriteAllText(Path.Combine(ownedFolder, "meta.ini"), $"[General]\r\ngameName=skyrimse\r\n\r\n{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\n");
            string svcPlugin = Path.Combine(ownedFolder, "HcSeqSvc.esp");
            {
                var p = new SkyrimMod(new ModKey("HcSeqSvc", ModType.Plugin), SkyrimRelease.SkyrimSE);
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                var q = p.Quests.AddNew(); q.EditorID = "SvcOwn"; q.Flags = Quest.Flag.StartGameEnabled;
                p.BeginWrite.ToPath(svcPlugin).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }
            var oSvc = svc.WriteSeq(svcPlugin, null, null);
            string expectedSeq = Path.Combine(ownedFolder, "SEQ", "HcSeqSvc.seq");
            bool svcOk = oSvc.Success && oSvc.SeqPath is not null
                         && File.Exists(expectedSeq)
                         && new FileInfo(expectedSeq).Length == 4
                         && oSvc.Quests.Count == 1;
            Check(svcOk, $"SERVICE-WRITE WriteSeq lands SEQ\\<plugin>.seq with the SGE quest — success={oSvc.Success} path=[{oSvc.SeqPath}] err=[{oSvc.Error}]");
            Check(oSvc.WroteIntoPluginFolder && PathUnder(oSvc.SeqPath, ownedFolder),
                $"SAME-FOLDER .seq defaults into the plugin's OWN houseCARL folder — intoPlugin={oSvc.WroteIntoPluginFolder} path=[{oSvc.SeqPath}]");

            // EMPTY-NOOP: a plugin with NO SGE quests writes nothing + cuts no folder.
            string emptyPlugin = Path.Combine(root, "empty", "HcSeqEmpty.esp");
            Directory.CreateDirectory(Path.GetDirectoryName(emptyPlugin)!);
            {
                var p = new SkyrimMod(new ModKey("HcSeqEmpty", ModType.Plugin), SkyrimRelease.SkyrimSE);
                if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
                var q = p.Quests.AddNew(); q.EditorID = "EmptyPlain"; q.Flags = Quest.Flag.RunOnce; // NOT SGE
                p.BeginWrite.ToPath(emptyPlugin).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
            }
            int foldersBefore = Directory.GetDirectories(mods).Length;
            var oEmpty = svc.WriteSeq(emptyPlugin, null, null);
            int foldersAfter = Directory.GetDirectories(mods).Length;
            Check(oEmpty.Success && oEmpty.SeqPath is null && oEmpty.Quests.Count == 0 && foldersAfter == foldersBefore,
                $"EMPTY-NOOP no SGE quests → nothing written, no folder cut — success={oEmpty.Success} path=[{oEmpty.SeqPath}] folders {foldersBefore}->{foldersAfter}");

            // REFUSE-NOFILE: a missing plugin path is refused, named — and (W3 PR 2) the refusal states BOTH
            // accepted source= spellings, since a filename is now resolvable and "pass the full path" alone would
            // send the caller down the narrower of the two lanes.
            var oMiss = svc.WriteSeq(Path.Combine(root, "does-not-exist.esp"), null, null);
            Check(!oMiss.Success && oMiss.Error is not null
                  && oMiss.Error.Contains("no file at path", StringComparison.OrdinalIgnoreCase)
                  && oMiss.Error.Contains("FILENAME", StringComparison.Ordinal)
                  && oMiss.Error.Contains("ABSOLUTE path", StringComparison.Ordinal),
                $"REFUSE-NOFILE missing plugin path refused + named, both spellings offered — err=[{oMiss.Error}]");

            // REFUSE-UNFINDABLE (W3 PR 2): a bare FILENAME that no mod folder, the overwrite folder or Data
            // provides is refused by name too — the filename lane must not degrade into "treat it as a relative
            // path and fail somewhere else".
            var oNoName = svc.WriteSeq("HcSeqNoSuchPlugin.esp", null, null);
            Check(!oNoName.Success && oNoName.Error is not null
                  && oNoName.Error.Contains("HcSeqNoSuchPlugin.esp", StringComparison.OrdinalIgnoreCase)
                  && oNoName.Error.Contains("mod folder", StringComparison.OrdinalIgnoreCase),
                $"REFUSE-UNFINDABLE unlocatable filename refused + named — err=[{oNoName.Error}]");

            // FILENAME-LANE (W3 PR 2): the plugin that SERVICE-WRITE reached by absolute path is reachable by its
            // bare filename too, and the outcome states which copy it read (SPEC §4.2 — the arm is never silent).
            var oByName = svc.WriteSeq(Path.GetFileName(svcPlugin), null, null);
            Check(oByName.Success && oByName.SeqPath is not null
                  && oByName.ResolvedFrom is { Length: > 0 }
                  && string.Equals(oByName.PluginPath, Path.GetFullPath(svcPlugin), StringComparison.OrdinalIgnoreCase),
                $"FILENAME-LANE source= by filename resolves to the same file and states its arm — from=[{oByName.ResolvedFrom}] path=[{oByName.PluginPath}] err=[{oByName.Error}]");

            // ====================== #312 — output_dir= and the byte-identical no-op ======================

            // PURE-CONTRACT: the SEQ twin of the compile lane's path contract. Pure, so it is provable without an
            // instance, and it is the piece the resolve arms below cannot distinguish from a lucky Path.Combine.
            const string pcMods = @"C:\MO2\mods", pcData = @"C:\Game\Skyrim Special Edition\Data";
            var pcBare = LoadOrderService.SeqOutputContract(@"C:\MyMod", pcMods, pcData);
            var pcAlready = LoadOrderService.SeqOutputContract(@"C:\MyMod\SEQ", pcMods, pcData);
            var pcLower = LoadOrderService.SeqOutputContract(@"C:\MyMod\seq\", pcMods, pcData);
            Check(pcBare.seqDir == @"C:\MyMod\SEQ" && pcBare.appendedSeq
                  && pcAlready.seqDir == @"C:\MyMod\SEQ" && !pcAlready.appendedSeq
                  && pcLower.seqDir == @"C:\MyMod\seq" && !pcLower.appendedSeq,
                $"PURE-SEQ-CONTRACT SEQ\\ appended once, double-SEQ guard case-insensitive + trailing-separator tolerant — [{pcBare.seqDir}] [{pcAlready.seqDir}] [{pcLower.seqDir}]");
            // Deployability is the .pex rule one segment over — and the WARNING is worded for what a mis-placed .seq
            // costs (silently dead quests), not the milder "won't deploy on its own" the compile lane can afford.
            // …including MO2's OVERWRITE folder, which is VFS-mapped onto Data at top priority and is where xEdit /
            // CK / Synthesis output normally lands. Omitting it made the feature's own headline case — an in-place
            // edit of a plugin living in overwrite — warn "the game will NOT see this .seq" about a folder the game
            // does read (review round 1).
            const string pcOver = @"C:\MO2\overwrite";
            Check(LoadOrderService.SeqOutputContract(pcOver, pcMods, pcData, pcOver).deployWarning is null
                  && LoadOrderService.SeqOutputContract(pcOver, pcMods, pcData).deployWarning is not null,
                "PURE-SEQ-OVERWRITE <overwrite>\\SEQ deploys when the overwrite root is known, and is judged only on that root (not by name)");
            // …and a drive ROOT keeps its separator: "C:\" trimmed to "C:" yields the drive-RELATIVE "C:SEQ".
            Check(LoadOrderService.SeqOutputContract(@"C:\", pcMods, pcData).seqDir == @"C:\SEQ",
                $"PURE-SEQ-ROOT a drive root appends correctly — got [{LoadOrderService.SeqOutputContract(@"C:\", pcMods, pcData).seqDir}] (want C:\\SEQ, never the drive-relative C:SEQ)");
            Check(LoadOrderService.SeqOutputContract(@"C:\MO2\mods\MyMod", pcMods, pcData).deployWarning is null
                  && LoadOrderService.SeqOutputContract(pcData, pcMods, pcData).deployWarning is null
                  && LoadOrderService.SeqOutputContract(@"C:\MO2\mods", pcMods, pcData).deployWarning is not null
                  && LoadOrderService.SeqOutputContract(@"C:\MO2\mods\X\Sub", pcMods, pcData).deployWarning is not null
                  && LoadOrderService.SeqOutputContract(@"C:\Elsewhere\MyMod", pcMods, pcData).deployWarning is { } w312
                     && w312.Contains("silently", StringComparison.Ordinal),
                "PURE-SEQ-DEPLOY <mods>\\<mod>\\SEQ and <data>\\SEQ deploy; the mods root, a nested path and an outside path warn — and the warning names the silent-death consequence");

            // OUTPUT-DIR: a third-party mod's OWN folder — the #312 case (in-place .esp edit, .seq belongs beside it).
            // The folder is USER-owned: no houseCARL mod folder is cut, and no ownership marker is stamped into it.
            string userMod = Path.Combine(mods, "SomeoneElsesQuestMod");
            Directory.CreateDirectory(userMod);
            int cutBefore = Directory.GetDirectories(mods).Length;
            var oOut = svc.WriteSeq(svcPlugin, null, null, userMod);
            string expectedUserSeq = Path.Combine(userMod, "SEQ", "HcSeqSvc.seq");
            Check(oOut.Success && File.Exists(expectedUserSeq)
                  && string.Equals(oOut.SeqPath, expectedUserSeq, StringComparison.OrdinalIgnoreCase)
                  && oOut.UserChoseOutput && !oOut.WroteIntoPluginFolder
                  && Directory.GetDirectories(mods).Length == cutBefore
                  && !File.Exists(Path.Combine(userMod, "meta.ini")),
                $"OUTPUT-DIR .seq lands in <output_dir>\\SEQ, no houseCARL folder cut, no ownership marker stamped — path=[{oOut.SeqPath}] chose={oOut.UserChoseOutput} err=[{oOut.Error}]");
            // …and a mod folder directly under mods\ DEPLOYS, so no warning. (The arm above is the case a user hits;
            // this is the half that proves the warning is a real discriminator rather than always-on.)
            // …and it asserts WHERE THE FILE WENT alongside the null warning: on its own, "no warning" is also what
            // every houseCARL-folder lane produces, so the arm passed unchanged with output_dir= routing disabled
            // (its own RED check found that, twice — the flag alone was no better, since it reports what the CALLER
            // asked for, not which lane ran). The path is the only witness that cannot be faked by intent.
            Check(PathUnder(oOut.SeqPath, userMod) && oOut.DeployWarning is null,
                $"OUTPUT-DIR-DEPLOYS the .seq is IN the named folder AND <mods>\\<mod>\\SEQ carries no deploy warning — path=[{oOut.SeqPath}] got [{oOut.DeployWarning}]");

            // OUTPUT-DIR-OUTSIDE: the same call to a folder the game never reads is written and WARNED, never a clean
            // "done" — a .seq the engine can't see leaves every SGE quest silently dead (Q3).
            string offTree = Path.Combine(root, "elsewhere", "NotAMod");
            var oOff = svc.WriteSeq(svcPlugin, null, null, offTree);
            Check(oOff.Success && File.Exists(Path.Combine(offTree, "SEQ", "HcSeqSvc.seq"))
                  && oOff.DeployWarning is not null,
                $"OUTPUT-DIR-OUTSIDE written but WARNED (never a clean done for a .seq the game won't read) — warn=[{oOff.DeployWarning}]");

            // USER-OWNED-SURVIVES: residue cleanup must never delete a folder the user named (CreatedFresh=false).
            var userRf = svc.ResolveExplicitSeqFolder(userMod, out _);
            Check(!userRf.CreatedFresh && svc.RemoveOrNameRiderResidue(userRf) is null && Directory.Exists(userRf.OutputDir),
                "USER-OWNED-SURVIVES residue cleanup never deletes an output_dir folder (CreatedFresh=false; the folder survives)");

            // UNCHANGED: re-running against a destination that already holds these bytes writes NOTHING. Proved by the
            // file's TIMESTAMP, not by the flag alone — the flag is what a broken short-circuit would set while still
            // rewriting the file, so a mtime sentinel is what makes this arm mean "no write happened".
            // The PLUGIN is stamped OLDER than the sentinel deliberately: an identical .seq that is older than its
            // plugin is stamped forward (the MTIME-REFRESH arm below), so "no write" is only observable as a preserved
            // mtime when the file is already the newer of the two.
            var sentinel = new DateTime(2005, 5, 5, 5, 5, 5, DateTimeKind.Utc);
            // Stamped only if the arm above actually produced the file: a missing destination is a FAILURE of that
            // arm, and stamping it blind turns it into an exception that takes the rest of this section with it
            // (which is exactly what the OUTPUT-DIR RED check produced).
            File.SetLastWriteTimeUtc(svcPlugin, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            if (File.Exists(expectedUserSeq)) File.SetLastWriteTimeUtc(expectedUserSeq, sentinel);
            else Check(false, "UNCHANGED prerequisite: the OUTPUT-DIR arm left no file to re-run against");
            var oSame = svc.WriteSeq(svcPlugin, null, null, userMod);
            Check(oSame.Success && oSame.Unchanged && !oSame.TimestampRefreshed
                  && File.GetLastWriteTimeUtc(expectedUserSeq) == sentinel
                  && string.Equals(oSame.SeqPath, expectedUserSeq, StringComparison.OrdinalIgnoreCase),
                $"UNCHANGED byte-identical destination → nothing written (mtime untouched), reported as its own state — unchanged={oSame.Unchanged} touched={oSame.TimestampRefreshed} err=[{oSame.Error}]");

            // UNCHANGED-DIFFERS: the negative half. A destination holding DIFFERENT bytes is rewritten — a
            // short-circuit that fired on mere existence would leave the stale file and report success.
            Directory.CreateDirectory(Path.GetDirectoryName(expectedUserSeq)!);
            File.WriteAllBytes(expectedUserSeq, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            File.SetLastWriteTimeUtc(expectedUserSeq, sentinel);
            var oDiff = svc.WriteSeq(svcPlugin, null, null, userMod);
            Check(oDiff.Success && !oDiff.Unchanged
                  && File.GetLastWriteTimeUtc(expectedUserSeq) != sentinel
                  && File.ReadAllBytes(expectedUserSeq).Length == 4
                  && File.ReadAllBytes(expectedUserSeq)[0] != 0xDE,
                $"UNCHANGED-DIFFERS a stale destination IS rewritten (the short-circuit compares BYTES, not existence) — unchanged={oDiff.Unchanged}");

            // MTIME-REFRESH: the no-op must not leave the file looking STALE to the sibling lint, which reads MTIME
            // and not content (validate_dialogue's SeqNewerThanPlugin). Without this, the exact loop the feature
            // exists for — edit in place, regenerate — would leave a permanent "your .seq is older than the plugin"
            // advisory on a byte-perfect file, with two houseCARL tools contradicting each other (review round 1).
            File.SetLastWriteTimeUtc(expectedUserSeq, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var pluginStamp = new DateTime(2020, 6, 6, 6, 6, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(svcPlugin, pluginStamp);
            var oStale = svc.WriteSeq(svcPlugin, null, null, userMod);
            Check(oStale.Success && oStale.Unchanged && oStale.TimestampRefreshed
                  && File.GetLastWriteTimeUtc(expectedUserSeq) > pluginStamp
                  && File.ReadAllBytes(expectedUserSeq).Length == 4,
                $"MTIME-REFRESH a byte-identical but OLDER .seq is stamped forward, not rewritten — unchanged={oStale.Unchanged} touched={oStale.TimestampRefreshed} seq={File.GetLastWriteTimeUtc(expectedUserSeq):O} plugin={pluginStamp:O}");
            // A CONSTRUCTION pin, not a fragment one: the stamp sentence has one source, so this asserts (a) the
            // source itself still carries the two claims that matter — it names validate_dialogue's check, and it
            // BOUNDS the promise to the copy the load order serves (review round 2: the lint reads the .seq the VFS
            // SERVES, which is this file only when this folder wins the SEQ\ conflict) — and (b) the render is
            // reading that source. A fragment check could only ever have pinned one render's spelling of it.
            var renderStale = SeqTools.Render(oStale);
            Check(WriteSentences.Twins.SeqTimestampRefreshed.Contains("validate_dialogue", StringComparison.Ordinal)
                  && WriteSentences.Twins.SeqTimestampRefreshed.Contains("the copy the load order actually serves", StringComparison.Ordinal)
                  && renderStale.Contains(WriteSentences.Twins.SeqTimestampRefreshed, StringComparison.Ordinal),
                $"MTIME-REFRESH-RENDER the stamp is STATED and its scope is bounded — render=[{Trim(renderStale)}]");

            // MTIME-FUTURE: a plugin stamped in the FUTURE (restored backup, skewed clock) cannot be beaten by
            // stamping UtcNow — the old code would have mutated the file, claimed a refresh, and left the comparison
            // exactly as it was. The stamp now targets past the plugin and VERIFIES, so the file really is newer.
            var future = DateTime.UtcNow.AddDays(30);
            File.SetLastWriteTimeUtc(svcPlugin, future);
            File.SetLastWriteTimeUtc(expectedUserSeq, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var oFuture = svc.WriteSeq(svcPlugin, null, null, userMod);
            Check(oFuture.Success
                  && File.GetLastWriteTimeUtc(expectedUserSeq) >= File.GetLastWriteTimeUtc(svcPlugin),
                $"MTIME-FUTURE a future-stamped plugin still ends up OLDER than its .seq (the refresh is verified, not assumed) — seq={File.GetLastWriteTimeUtc(expectedUserSeq):O} plugin={future:O} unchanged={oFuture.Unchanged} touched={oFuture.TimestampRefreshed}");
            File.SetLastWriteTimeUtc(svcPlugin, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));   // leave the fixture sane

            // TOOL-LANE: output_dir= WINS over patch=/into=, and says so (Q3 — an ignored parameter is stated, never
            // silently dropped). Asserted through the TOOL, because the note is composed there.
            var toolBoth = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin), patch: "HcSeqIgnored", output_dir: userMod);
            Check(toolBoth.Contains("output_dir= was given", StringComparison.Ordinal)
                  && toolBoth.Contains("ignored", StringComparison.Ordinal)
                  && !Directory.EnumerateDirectories(mods, "houseCARL - HcSeqIgnored*").Any(),
                $"TOOL-LANE output_dir= wins over patch= and the ignored lane is STATED, with no folder cut for it — render=[{Trim(toolBoth)}]");

            // RENDER-UNCHANGED: the no-op's text render leads with it. "wrote" on a call that wrote nothing is the
            // silent-success shape Q3 refuses, and the first line is what a caller reads.
            var toolSame = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin), output_dir: userMod);
            Check(toolSame.StartsWith("unchanged", StringComparison.Ordinal)
                  && toolSame.Contains("NOTHING was written", StringComparison.Ordinal)
                  && !toolSame.Contains("houseCARL mod folder — enable it", StringComparison.Ordinal),
                $"RENDER-UNCHANGED the no-op renders as 'unchanged … NOTHING was written', and an output_dir destination is NOT called a houseCARL mod folder — render=[{Trim(toolSame)}]");

            // JSON-UNCHANGED: written=false with unchanged=true and a seq_path — the machine twin of the same fact
            // (the pre-#312 `written` was "a path exists", which would now report a write that never happened).
            // REPLACED: the write that overwrites an existing .seq says so. On the output_dir lane that file can be
            // the mod's OWN shipped copy and houseCARL keeps no backup — the same "two different facts about the disk"
            // argument that split the no-op out (review round 1).
            File.WriteAllBytes(expectedUserSeq, new byte[] { 1, 2, 3, 4, 5, 6 });
            var oRepl = svc.WriteSeq(svcPlugin, null, null, userMod);
            var renderRepl = SeqTools.Render(oRepl);
            Check(oRepl.Success && !oRepl.Unchanged && oRepl.Replaced
                  && renderRepl.StartsWith("replaced ", StringComparison.Ordinal)
                  // The no-backup alarm is a property of the SENTENCE (one source, both transports), so pin it there
                  // and pin that this render reads it — rather than pinning one lane's spelling of the alarm.
                  && WriteSentences.Twins.SeqReplacedUserFolder.Contains("keeps no backup", StringComparison.Ordinal)
                  && renderRepl.Contains(WriteSentences.Twins.SeqReplacedUserFolder, StringComparison.Ordinal),
                $"REPLACED overwriting an existing .seq reports 'replaced', not 'wrote' — replaced={oRepl.Replaced} render=[{Trim(renderRepl)}]");
            // …and the fresh-file case is NOT reported as a replacement (the flag has to discriminate).
            File.Delete(expectedUserSeq);
            var oFresh = svc.WriteSeq(svcPlugin, null, null, userMod);
            Check(oFresh.Success && !oFresh.Replaced && SeqTools.Render(oFresh).StartsWith("wrote ", StringComparison.Ordinal),
                $"REPLACED-NEG a first write to an empty destination is 'wrote', not 'replaced' — replaced={oFresh.Replaced}");
            // …and the json twin of the replaced state, which had no arm (PR #318 review [nit]).
            File.WriteAllBytes(expectedUserSeq, new byte[] { 9, 9, 9, 9, 9, 9 });
            var jsonRepl = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin), output_dir: userMod, format: "json");
            Check(jsonRepl.Contains("\"replaced\": true", StringComparison.Ordinal)
                  && jsonRepl.Contains("\"replaced_same_bytes\": false", StringComparison.Ordinal)
                  && jsonRepl.Contains("replaced_note", StringComparison.Ordinal)
                  && jsonRepl.Contains("no backup", StringComparison.Ordinal),
                $"REPLACED-JSON the replaced state and its note are on the json transport too — render=[{Trim(jsonRepl)}]");

            // LANE-ORDER: output_dir= + patch= + into= together is NOT refused. The pair-exclusivity check used to run
            // first, so naming all three was rejected over two parameters output_dir='s own contract promises to
            // ignore — a tool contradicting itself (review round 1).
            var toolAllThree = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin),
                patch: "HcSeqIgnoredA", into: "HcSeqIgnoredB.esp", output_dir: userMod);
            Check(!toolAllThree.StartsWith("error:", StringComparison.Ordinal)
                  && toolAllThree.Contains("output_dir= was given", StringComparison.Ordinal),
                $"LANE-ORDER output_dir= + patch= + into= is accepted with the ignored-lane note, not refused — render=[{Trim(toolAllThree)}]");
            // …while the pair WITHOUT output_dir is still the refusal it was.
            var toolPair = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin),
                patch: "HcSeqIgnoredA", into: "HcSeqIgnoredB.esp");
            Check(toolPair.StartsWith("error:", StringComparison.Ordinal)
                  && toolPair.Contains("exclusive", StringComparison.Ordinal),
                $"LANE-ORDER-NEG patch= + into= alone is still refused BY NAME — render=[{Trim(toolPair)}]");

            // NOOP-LANE-NOTE: the no-SGE-quest early return carries the ignored-lane note too. It used to drop it
            // while the json twin emitted one — the D2 divergence this file's epoch fold exists to close.
            var toolEmptyNote = SeqTools.WriteSeq(svc, source: emptyPlugin, patch: "HcSeqIgnoredC", output_dir: userMod);
            Check(toolEmptyNote.Contains("no start-game-enabled quests", StringComparison.Ordinal)
                  && toolEmptyNote.Contains("output_dir= was given", StringComparison.Ordinal),
                $"NOOP-LANE-NOTE the nothing-to-do render states the ignored lane too — render=[{Trim(toolEmptyNote)}]");

            // WRITE-FAIL-FOLDER: the output_dir lane's write-failure clause, which had no arm (PR #318 review [nit]).
            // A directory sitting where the .seq must go makes the write throw with the folder already resolved, so
            // the message has to say the folder is left alone — cleanup is bypassed on a user-owned destination.
            var blocked = Path.Combine(root, "blocked");
            Directory.CreateDirectory(Path.Combine(blocked, "SEQ", "HcSeqSvc.seq"));   // a DIRECTORY where the file goes
            var oBlocked = svc.WriteSeq(svcPlugin, null, null, blocked);
            Check(!oBlocked.Success && oBlocked.Error is { } be
                  && be.Contains("could not write", StringComparison.Ordinal)
                  && be.Contains("is left in place", StringComparison.Ordinal)
                  && be.Contains("never removes a folder you named", StringComparison.Ordinal)
                  && Directory.Exists(Path.Combine(blocked, "SEQ")),
                $"WRITE-FAIL-FOLDER a failed output_dir write names the folder it leaves behind, and leaves it — err=[{oBlocked.Error}]");

            // LANE-NOTE-ON-REFUSAL: an ignored lane stays stated when the call FAILS. A refusal is exactly when a
            // caller re-reads their parameters, and "patch= was ignored" is still true (review round 2).
            var failNote = SeqTools.WriteSeq(svc, source: "HcSeqNoSuchPlugin.esp", patch: "HcSeqIgnoredD", output_dir: userMod);
            Check(failNote.StartsWith("error:", StringComparison.Ordinal)
                  && failNote.Contains("output_dir= was given", StringComparison.Ordinal),
                $"LANE-NOTE-ON-REFUSAL a failed call still states the ignored lane — render=[{Trim(failNote)}]");
            var failNoteJson = SeqTools.WriteSeq(svc, source: "HcSeqNoSuchPlugin.esp", patch: "HcSeqIgnoredD",
                output_dir: userMod, format: "json");
            Check(failNoteJson.Contains("\"lane_note\"", StringComparison.Ordinal)
                  && failNoteJson.Contains("output_dir= was given", StringComparison.Ordinal),
                $"LANE-NOTE-ON-REFUSAL-JSON …on the json transport too (D2) — render=[{Trim(failNoteJson)}]");

            var jsonSame = SeqTools.WriteSeq(svc, source: Path.GetFileName(svcPlugin), output_dir: userMod, format: "json");
            Check(jsonSame.Contains("\"written\": false", StringComparison.Ordinal)
                  && jsonSame.Contains("\"unchanged\": true", StringComparison.Ordinal)
                  && jsonSame.Contains("\"user_chose_output_dir\": true", StringComparison.Ordinal)
                  && jsonSame.Contains("\"seq_path\"", StringComparison.Ordinal),
                $"JSON-UNCHANGED json reports written=false + unchanged=true + the path — render=[{Trim(jsonSame)}]");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  guard threw: " + ex);
            fail++;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL SEQ-WRITE GUARD ARMS PASSED" : $"SEQ-WRITE GUARD: {fail} ARM(S) FAILED");
        return fail == 0 ? 0 : 1;
    }

    static bool PathUnder(string? path, string folder)
        => path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>One-line, bounded echo of a render for a FAIL line (the arms below assert on multi-line output).</summary>
    static string Trim(string s)
        => (s.Length <= 300 ? s : s[..300] + " …").Replace("\r", "").Replace("\n", " | ");

    // ---- raw on-disk FormID parse (independent of Mutagen's overlay decode) — to verify the computed encoding ----

    static List<uint> AllRecordFormIds(string path, string targetSig)
        => WalkRecords(File.ReadAllBytes(path)).Where(x => x.sig == targetSig).Select(x => x.formId).ToList();

    static List<(string sig, uint formId)> WalkRecords(byte[] buf)
    {
        var outp = new List<(string, uint)>();
        if (buf.Length < 24) return outp;
        uint tes4Size = BitConverter.ToUInt32(buf, 4);          // skip the TES4 header record (24-byte header + data)
        Scan(buf, 24 + (int)tes4Size, buf.Length, outp);
        return outp;
    }

    static void Scan(byte[] buf, int start, int end, List<(string, uint)> outp)
    {
        int p = start;
        while (p + 24 <= end)
        {
            string sig = System.Text.Encoding.ASCII.GetString(buf, p, 4);
            uint size = BitConverter.ToUInt32(buf, p + 4);
            long next;
            if (sig == "GRUP") { next = (long)p + size; Scan(buf, p + 24, (int)Math.Min(next, end), outp); }  // GRUP size INCLUDES its 24-byte header
            else { outp.Add((sig, BitConverter.ToUInt32(buf, p + 12))); next = (long)p + 24 + size; }          // major record: FormID at +12
            if (next <= p) break;                                                                              // forward-progress guard (Q3)
            p = (int)Math.Min(next, (long)end);
        }
    }
}

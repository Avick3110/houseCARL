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

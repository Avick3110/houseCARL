using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE Wave A1 — FACEGEN-CARRY guard for housecarl_compact_plugin (the asset-rename spine).
/// Proves the gap the merge research (dev/plans/MERGE_REFERENCE_RESEARCH_2026-06-26 §3/§8) exposed in the SHIPPED
/// compact tool is CLOSED: compacting an NPC mod renumbers its records AND carries the FormID-keyed FaceGen files
/// (head mesh + face tint) to the NEW FormID, so the mod no longer SILENTLY dark-faces (a Q3 degraded mode in the
/// pre-A1 tool, which renumbered the record but left the facegen at the old FormID the engine no longer looks up).
/// Drives the REAL <see cref="LoadOrderService.CompactPlugin"/> over a synthetic MO2 instance (the CompactServiceGuard
/// + PlaceAsset probe pattern), so it pins the END-TO-END wiring, not just the service in isolation.
///   NEW-FILE   — compact to a new file carries the NPC's facegen (mesh+tint) to the new FormID under the FRESH mod
///                folder, byte-exact; the outcome reports 2 files / 1 NPC; the OLD-FormID facegen is left untouched.
///   IN-PLACE   — compact in place carries the facegen into the target's OWN folder at the new FormID (old left orphan).
///   NO-FACEGEN — an NPC with no facegen carries nothing and is NOT a failure (NpcCount &gt; 0, files 0, zero WARN).
/// Run: dotnet run --project src/housecarl-generator facegen-carry-guard
/// </summary>
public static class FacegenCarryProbe
{
    [CiProbe("facegen-carry-guard")]
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave A1 — facegen-carry guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-facegen-carry-guard-" + Guid.NewGuid().ToString("N"));
        var meshBytes = new byte[] { 0x4E, 0x49, 0x46, 0x01, 0x02, 0x03 };   // distinctive "head mesh" bytes
        var tintBytes = new byte[] { 0xDD, 0x5B, 0xEE, 0xFF, 0x10 };         // distinctive "face tint" bytes
        try
        {
            // ================= NEW-FILE: carry facegen to the new FormID in a fresh folder, byte-exact =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "newfile"));
                var faceKey = new ModKey("FaceMod", ModType.Plugin);
                var npcOld = new FormKey(faceKey, 0xA10);
                WriteMod(mods, "FaceMod", faceKey, m => m.Npcs.Add(new Npc(npcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcFaceNpc" }));
                WriteLoose(Path.Combine(mods, "FaceMod"), FaceGenPath.For(npcOld, FaceGenSlot.Mesh), meshBytes);
                WriteLoose(Path.Combine(mods, "FaceMod"), FaceGenPath.For(npcOld, FaceGenSlot.Tint), tintBytes);
                WriteProfile(prof, new[] { faceKey.FileName.String }, new[] { "*" + faceKey.FileName }, new[] { "+FaceMod" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "newfile"), 0, new UserConfigStore(Path.Combine(root, "user-nf.json")));
                svc.Stats();

                var o = svc.CompactPlugin("FaceMod.esp");
                FormKey? npcNew = null; bool meshOk = false, tintOk = false, oldUntouched = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    using (var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
                        npcNew = pp.Npcs.FirstOrDefault(n => n.EditorID == "HcFaceNpc")?.FormKey;
                    var modRoot = Path.GetDirectoryName(o.OutputPath)!;
                    if (npcNew is { } nk)
                    {
                        var newMesh = Path.Combine(modRoot, FaceGenPath.For(nk, FaceGenSlot.Mesh));
                        var newTint = Path.Combine(modRoot, FaceGenPath.For(nk, FaceGenSlot.Tint));
                        meshOk = File.Exists(newMesh) && File.ReadAllBytes(newMesh).SequenceEqual(meshBytes);
                        tintOk = File.Exists(newTint) && File.ReadAllBytes(newTint).SequenceEqual(tintBytes);
                    }
                    var oldMesh = Path.Combine(mods, "FaceMod", FaceGenPath.For(npcOld, FaceGenSlot.Mesh));
                    oldUntouched = File.Exists(oldMesh) && File.ReadAllBytes(oldMesh).SequenceEqual(meshBytes);
                }
                var ar = o.AssetRename;
                Check(o.Success && npcNew is { } k && k.ID >= RemapEngine.EslFloor && k.ID <= RemapEngine.EslCeiling
                      && meshOk && tintOk
                      && ar is { NpcCount: 1, FacegenNpcsCarried: 1, FacegenFilesCarried: 2 } && ar.Failures.Count == 0,
                      $"NEW-FILE facegen carried to new FormID {npcNew?.ID:X6} (mesh {meshOk}, tint {tintOk}, report {ar?.FacegenFilesCarried}f/{ar?.FacegenNpcsCarried}npc/{ar?.NpcCount}total{(o.Success ? "" : "; ERR " + o.Error)})");
                Check(oldUntouched, "NEW-FILE the OLD-FormID facegen is left untouched (non-destructive)");
            }

            // ================= IN-PLACE: carry facegen into the target's OWN folder at the new FormID =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "inplace"));
                var faceKey = new ModKey("FaceIp", ModType.Plugin);
                var npcOld = new FormKey(faceKey, 0xB20);
                WriteMod(mods, "FaceIp", faceKey, m => m.Npcs.Add(new Npc(npcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcIpNpc" }));
                WriteLoose(Path.Combine(mods, "FaceIp"), FaceGenPath.For(npcOld, FaceGenSlot.Mesh), meshBytes);
                WriteLoose(Path.Combine(mods, "FaceIp"), FaceGenPath.For(npcOld, FaceGenSlot.Tint), tintBytes);
                WriteProfile(prof, new[] { faceKey.FileName.String }, new[] { "*" + faceKey.FileName }, new[] { "+FaceIp" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "inplace"), 0, new UserConfigStore(Path.Combine(root, "user-ip.json")));
                svc.Stats();

                var o = svc.CompactPlugin("FaceIp.esp", inPlace: true, acknowledge: true);
                FormKey? npcNew = null; bool meshOk = false, oldOrphan = false;
                if (o.Success)
                {
                    using (var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
                        npcNew = pp.Npcs.FirstOrDefault(n => n.EditorID == "HcIpNpc")?.FormKey;
                    if (npcNew is { } nk)
                    {
                        var newMesh = Path.Combine(mods, "FaceIp", FaceGenPath.For(nk, FaceGenSlot.Mesh));
                        meshOk = File.Exists(newMesh) && File.ReadAllBytes(newMesh).SequenceEqual(meshBytes);
                    }
                    // the OLD-FormID facegen remains as a harmless orphan (non-destructive — never auto-deleted)
                    oldOrphan = File.Exists(Path.Combine(mods, "FaceIp", FaceGenPath.For(npcOld, FaceGenSlot.Mesh)));
                }
                var ar = o.AssetRename;
                Check(o.Success && o.InPlace && meshOk && ar is { FacegenFilesCarried: 2, FacegenNpcsCarried: 1 },
                      $"IN-PLACE facegen carried into the target folder at new FormID {npcNew?.ID:X6} (mesh {meshOk}, report {ar?.FacegenFilesCarried}f{(o.Success ? "" : "; ERR " + o.Error)})");
                Check(oldOrphan, "IN-PLACE the OLD-FormID facegen is left as a harmless orphan (non-destructive)");
            }

            // ===== IN-PLACE ALIASING (PR #123 review blocker): 2 NPCs whose NEW-id window overlaps their OLD ids =====
            // NpcP (old 0x900, enumerated FIRST) renumbers to new 0x800 — which is NpcQ's OLD id. A naive single-phase
            // carry processes P first and writes P's new-0x800 facegen OVER NpcQ's not-yet-read old-0x800 file, so Q then
            // reads P's bytes and inherits P's face (a Q3 silent wrong answer). Two-phase staging must keep them distinct.
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "alias"));
                var faceKey = new ModKey("FaceAlias", ModType.Plugin);
                var pOld = new FormKey(faceKey, 0x900);                  // enumerated first → renumbers to 0x800
                var qOld = new FormKey(faceKey, 0x800);                  // enumerated second → its OLD 0x800 == P's NEW id
                var meshP = new byte[] { 0x50, 0x01 }; var tintP = new byte[] { 0x50, 0x02 };
                var meshQ = new byte[] { 0x51, 0x01 }; var tintQ = new byte[] { 0x51, 0x02 };
                WriteMod(mods, "FaceAlias", faceKey, m =>
                {
                    m.Npcs.Add(new Npc(pOld, SkyrimRelease.SkyrimSE) { EditorID = "NpcP" });   // added FIRST
                    m.Npcs.Add(new Npc(qOld, SkyrimRelease.SkyrimSE) { EditorID = "NpcQ" });   // added SECOND
                });
                WriteLoose(Path.Combine(mods, "FaceAlias"), FaceGenPath.For(pOld, FaceGenSlot.Mesh), meshP);
                WriteLoose(Path.Combine(mods, "FaceAlias"), FaceGenPath.For(pOld, FaceGenSlot.Tint), tintP);
                WriteLoose(Path.Combine(mods, "FaceAlias"), FaceGenPath.For(qOld, FaceGenSlot.Mesh), meshQ);
                WriteLoose(Path.Combine(mods, "FaceAlias"), FaceGenPath.For(qOld, FaceGenSlot.Tint), tintQ);
                WriteProfile(prof, new[] { faceKey.FileName.String }, new[] { "*" + faceKey.FileName }, new[] { "+FaceAlias" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "alias"), 0, new UserConfigStore(Path.Combine(root, "user-alias.json")));
                svc.Stats();

                var o = svc.CompactPlugin("FaceAlias.esp", inPlace: true, acknowledge: true);
                bool pOk = false, qOk = false;
                if (o.Success)
                {
                    FormKey? pNew = null, qNew = null;
                    using (var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
                    {
                        pNew = pp.Npcs.FirstOrDefault(n => n.EditorID == "NpcP")?.FormKey;
                        qNew = pp.Npcs.FirstOrDefault(n => n.EditorID == "NpcQ")?.FormKey;
                    }
                    var modRoot = Path.GetDirectoryName(o.OutputPath)!;
                    if (pNew is { } pk) pOk = File.ReadAllBytes(Path.Combine(modRoot, FaceGenPath.For(pk, FaceGenSlot.Mesh))).SequenceEqual(meshP);
                    if (qNew is { } qk) qOk = File.ReadAllBytes(Path.Combine(modRoot, FaceGenPath.For(qk, FaceGenSlot.Mesh))).SequenceEqual(meshQ);
                }
                Check(o.Success && pOk && qOk,
                      $"IN-PLACE ALIASING each NPC keeps its OWN face across overlapping old/new IDs (P {pOk}, Q {qOk}{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= NO-FACEGEN: an NPC with no facegen carries nothing and is NOT a failure =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "nofg"));
                var faceKey = new ModKey("NoFg", ModType.Plugin);
                var npcOld = new FormKey(faceKey, 0xC30);
                WriteMod(mods, "NoFg", faceKey, m => m.Npcs.Add(new Npc(npcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcNoFgNpc" }));
                WriteProfile(prof, new[] { faceKey.FileName.String }, new[] { "*" + faceKey.FileName }, new[] { "+NoFg" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "nofg"), 0, new UserConfigStore(Path.Combine(root, "user-no.json")));
                svc.Stats();

                var o = svc.CompactPlugin("NoFg.esp");
                var ar = o.AssetRename;
                Check(o.Success && ar is { NpcCount: 1, FacegenFilesCarried: 0, FacegenNpcsCarried: 0 } && ar.Failures.Count == 0,
                      $"NO-FACEGEN an NPC without facegen carries nothing and is NOT a failure (report {ar?.FacegenFilesCarried}f/{ar?.NpcCount}npc, warns {ar?.Failures.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== facegen-carry-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    // ---- synthetic MO2 layout helpers (the CompactServiceGuard / PlaceAsset probe pattern) ----

    static (string mods, string prof) MakeInstance(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        return (mods, prof);
    }

    static void WriteMod(string mods, string folder, ModKey key, Action<SkyrimMod> build)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        build(m);
        m.BeginWrite.ToPath(Path.Combine(dir, key.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    static void WriteProfile(string prof, string[] loadorder, string[] plugins, string[] modlist)
    {
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
    }

    static void WriteSkyrimIni(string prof) =>
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

    static void WriteLoose(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }
}

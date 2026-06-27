using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COMPACT/MERGE Wave A2 — VOICE-CARRY guard for housecarl_compact_plugin (the asset-rename spine, second category).
/// Proves the OTHER half of the gap the merge research (MERGE_REFERENCE_RESEARCH_2026-06-26 §3/§8) exposed in the
/// SHIPPED compact tool is CLOSED: compacting a VOICED mod renumbers its dialogue lines (INFOs) AND carries the
/// FormID-keyed voice files (.fuz spoken audio + .lip lip-sync) to the NEW INFO FormID, so the mod no longer SILENTLY
/// goes mute (a Q3 degraded mode — the pre-A2 tool renumbered the INFO but left the voice at the old FormID the engine
/// no longer looks up, and only PRINTED a "verify voice yourself" reminder). CarryVoice DISCOVERS by scanning the
/// plugin's Sound\Voice\&lt;plugin&gt;\ prefix and rewriting the embedded id segment of every file whose FormID was
/// renumbered (strategy b — catches radiant/quest-alias lines a graph re-derivation would miss). Drives the REAL
/// <see cref="LoadOrderService.CompactPlugin"/> over a synthetic MO2 instance (the FacegenCarry / CompactServiceGuard
/// pattern), so it pins the END-TO-END wiring, not the service in isolation.
///   NEW-FILE   — compact to a new file carries an INFO's .fuz + .lip to the new FormID under the FRESH mod folder,
///                byte-exact; the outcome reports 2 files / 1 line; the OLD-FormID voice is left untouched.
///   IN-PLACE   — compact in place carries the voice into the target's OWN folder at the new FormID (old left orphan).
///   MULTI-LINE — two INFOs with distinct audio each keep their OWN .fuz across the renumber (the shared two-phase
///                carry; the dedicated OVERLAPPING-window aliasing torture test lives in facegen-carry-guard, which
///                rides the SAME CarryItems helper, so the aliasing fix is proven once where it's cleanest to build).
///   NO-VOICE   — a voiced-free plugin carries nothing and is NOT a failure (FilesScanned 0, carried 0, zero WARN).
/// Run: dotnet run --project src/housecarl-generator voice-carry-guard
/// </summary>
public static class VoiceCarryProbe
{
    // The voice-path coordinates the fixture's audio is filed under — only the id segment moves on a compact, so old
    // and new paths share these (CarryVoice rewrites the id, nothing else). VoiceType is the on-disk folder name.
    const string VoiceType = "FemaleEventoned";
    const int RespNum = 1;

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COMPACT Wave A2 — voice-carry guard (housecarl_compact_plugin)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-voice-carry-guard-" + Guid.NewGuid().ToString("N"));
        var fuzBytes = new byte[] { 0x46, 0x55, 0x5A, 0x01, 0x02, 0x03 };   // distinctive "spoken audio" bytes
        var lipBytes = new byte[] { 0x4C, 0x49, 0x50, 0x10, 0x11 };         // distinctive "lip-sync" bytes
        try
        {
            // ================= NEW-FILE: carry .fuz + .lip to the new INFO FormID in a fresh folder, byte-exact =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "newfile"));
                var key = new ModKey("VoiceNf", ModType.Plugin);
                var topicOld = new FormKey(key, 0x810);
                var infoOld = new FormKey(key, 0x811);
                WriteMod(mods, "VoiceNf", key, m => AddTopic(m, topicOld, infoOld, "HcVoiceTopic"));
                WriteLoose(Path.Combine(mods, "VoiceNf"), VoicePath.For(infoOld, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz), fuzBytes);
                WriteLoose(Path.Combine(mods, "VoiceNf"), VoicePath.For(infoOld, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Lip), lipBytes);
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+VoiceNf" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "newfile"), 0, new UserConfigStore(Path.Combine(root, "user-nf.json")));
                svc.Stats();

                var o = svc.CompactPlugin("VoiceNf.esp");
                FormKey? infoNew = null; bool fuzOk = false, lipOk = false, oldUntouched = false;
                if (o.Success && File.Exists(o.OutputPath))
                {
                    infoNew = ReadInfoKey(o.OutputPath, "HcVoiceTopic");
                    var modRoot = Path.GetDirectoryName(o.OutputPath)!;
                    if (infoNew is { } ik)
                    {
                        var newFuz = Path.Combine(modRoot, VoicePath.For(ik, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz));
                        var newLip = Path.Combine(modRoot, VoicePath.For(ik, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Lip));
                        fuzOk = File.Exists(newFuz) && File.ReadAllBytes(newFuz).SequenceEqual(fuzBytes);
                        lipOk = File.Exists(newLip) && File.ReadAllBytes(newLip).SequenceEqual(lipBytes);
                    }
                    var oldFuz = Path.Combine(mods, "VoiceNf", VoicePath.For(infoOld, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz));
                    oldUntouched = File.Exists(oldFuz) && File.ReadAllBytes(oldFuz).SequenceEqual(fuzBytes);
                }
                var vr = o.VoiceRename;
                Check(o.Success && infoNew is { } k && k.ID >= RemapEngine.EslFloor && k.ID <= RemapEngine.EslCeiling
                      && fuzOk && lipOk
                      && vr is { FilesScanned: 2, FilesCarried: 2, LinesCarried: 1 } && vr.Failures.Count == 0,
                      $"NEW-FILE voice carried to new FormID {infoNew?.ID:X6} (fuz {fuzOk}, lip {lipOk}, report {vr?.FilesCarried}f/{vr?.LinesCarried}line/{vr?.FilesScanned}scanned{(o.Success ? "" : "; ERR " + o.Error)})");
                Check(oldUntouched, "NEW-FILE the OLD-FormID voice is left untouched (non-destructive)");
            }

            // ================= IN-PLACE: carry the voice into the target's OWN folder at the new FormID =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "inplace"));
                var key = new ModKey("VoiceIp", ModType.Plugin);
                var topicOld = new FormKey(key, 0x820);
                var infoOld = new FormKey(key, 0x821);
                WriteMod(mods, "VoiceIp", key, m => AddTopic(m, topicOld, infoOld, "HcVoiceTopic"));
                WriteLoose(Path.Combine(mods, "VoiceIp"), VoicePath.For(infoOld, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz), fuzBytes);
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+VoiceIp" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "inplace"), 0, new UserConfigStore(Path.Combine(root, "user-ip.json")));
                svc.Stats();

                var o = svc.CompactPlugin("VoiceIp.esp", inPlace: true, acknowledge: true);
                FormKey? infoNew = null; bool fuzOk = false, oldOrphan = false;
                if (o.Success)
                {
                    infoNew = ReadInfoKey(o.OutputPath, "HcVoiceTopic");
                    if (infoNew is { } ik)
                    {
                        var newFuz = Path.Combine(mods, "VoiceIp", VoicePath.For(ik, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz));
                        fuzOk = File.Exists(newFuz) && File.ReadAllBytes(newFuz).SequenceEqual(fuzBytes);
                    }
                    // the OLD-FormID voice remains as a harmless orphan (non-destructive — never auto-deleted)
                    oldOrphan = File.Exists(Path.Combine(mods, "VoiceIp", VoicePath.For(infoOld, VoiceType, "", "HcVoiceTopic", RespNum, VoiceFile.Fuz)));
                }
                var vr = o.VoiceRename;
                Check(o.Success && o.InPlace && fuzOk && vr is { FilesCarried: 1, LinesCarried: 1 },
                      $"IN-PLACE voice carried into the target folder at new FormID {infoNew?.ID:X6} (fuz {fuzOk}, report {vr?.FilesCarried}f{(o.Success ? "" : "; ERR " + o.Error)})");
                Check(oldOrphan, "IN-PLACE the OLD-FormID voice is left as a harmless orphan (non-destructive)");
            }

            // ===== MULTI-LINE: two INFOs with DISTINCT audio each keep their OWN .fuz across the renumber (shared two-phase) =====
            // The OVERLAPPING old/new-id aliasing torture test is facegen-carry-guard's (it rides the SAME CarryItems helper),
            // so here we pin the per-line correctness of a multi-file voice carry: line A must not inherit line B's audio.
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "multi"));
                var key = new ModKey("VoiceMl", ModType.Plugin);
                var tA = new FormKey(key, 0x830); var iA = new FormKey(key, 0x831);
                var tB = new FormKey(key, 0x832); var iB = new FormKey(key, 0x833);
                var fuzA = new byte[] { 0xA0, 0x01, 0x02 };
                var fuzB = new byte[] { 0xB0, 0x01, 0x02 };
                WriteMod(mods, "VoiceMl", key, m => { AddTopic(m, tA, iA, "HcTopicA"); AddTopic(m, tB, iB, "HcTopicB"); });
                WriteLoose(Path.Combine(mods, "VoiceMl"), VoicePath.For(iA, VoiceType, "", "HcTopicA", RespNum, VoiceFile.Fuz), fuzA);
                WriteLoose(Path.Combine(mods, "VoiceMl"), VoicePath.For(iB, VoiceType, "", "HcTopicB", RespNum, VoiceFile.Fuz), fuzB);
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+VoiceMl" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "multi"), 0, new UserConfigStore(Path.Combine(root, "user-ml.json")));
                svc.Stats();

                var o = svc.CompactPlugin("VoiceMl.esp", inPlace: true, acknowledge: true);
                bool aOk = false, bOk = false;
                if (o.Success)
                {
                    var iaNew = ReadInfoKey(o.OutputPath, "HcTopicA");
                    var ibNew = ReadInfoKey(o.OutputPath, "HcTopicB");
                    if (iaNew is { } ak) aOk = File.ReadAllBytes(Path.Combine(mods, "VoiceMl", VoicePath.For(ak, VoiceType, "", "HcTopicA", RespNum, VoiceFile.Fuz))).SequenceEqual(fuzA);
                    if (ibNew is { } bk) bOk = File.ReadAllBytes(Path.Combine(mods, "VoiceMl", VoicePath.For(bk, VoiceType, "", "HcTopicB", RespNum, VoiceFile.Fuz))).SequenceEqual(fuzB);
                }
                var vr = o.VoiceRename;
                Check(o.Success && aOk && bOk && vr is { FilesCarried: 2, LinesCarried: 2 },
                      $"MULTI-LINE each dialogue line keeps its OWN audio (A {aOk}, B {bOk}, report {vr?.FilesCarried}f/{vr?.LinesCarried}lines{(o.Success ? "" : "; ERR " + o.Error)})");
            }

            // ================= NO-VOICE: a plugin with no voice files carries nothing and is NOT a failure =================
            {
                var (mods, prof) = MakeInstance(Path.Combine(root, "novoice"));
                var key = new ModKey("VoiceNone", ModType.Plugin);
                var topicOld = new FormKey(key, 0x840);
                var infoOld = new FormKey(key, 0x841);
                WriteMod(mods, "VoiceNone", key, m => AddTopic(m, topicOld, infoOld, "HcVoiceTopic"));
                WriteProfile(prof, new[] { key.FileName.String }, new[] { "*" + key.FileName }, new[] { "+VoiceNone" });
                WriteSkyrimIni(prof);

                using var svc = LoadOrderService.WithInstance(Path.Combine(root, "novoice"), 0, new UserConfigStore(Path.Combine(root, "user-nv.json")));
                svc.Stats();

                var o = svc.CompactPlugin("VoiceNone.esp");
                var vr = o.VoiceRename;
                Check(o.Success && vr is { FilesScanned: 0, FilesCarried: 0, LinesCarried: 0 } && vr.Failures.Count == 0,
                      $"NO-VOICE a voiced-free plugin carries nothing and is NOT a failure (report {vr?.FilesCarried}f/{vr?.FilesScanned}scanned, warns {vr?.Failures.Count}{(o.Success ? "" : "; ERR " + o.Error)})");
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== voice-carry-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    // ---- fixture builders ----

    /// <summary>Add a DialogTopic (with EditorID, so the readback can find it) holding ONE INFO line — the minimal voiced
    /// dialogue shape compact renumbers (topic→INFO nesting via RenumberModInto).</summary>
    static void AddTopic(SkyrimMod m, FormKey topicKey, FormKey infoKey, string topicEdid)
    {
        var topic = new DialogTopic(topicKey, SkyrimRelease.SkyrimSE) { EditorID = topicEdid };
        topic.Responses.Add(new DialogResponses(infoKey, SkyrimRelease.SkyrimSE));
        m.DialogTopics.Add(topic);
    }

    /// <summary>Read P′ back and return the (renumbered) FormKey of the single INFO under the topic with <paramref name="topicEdid"/>.</summary>
    static FormKey? ReadInfoKey(string pPrimePath, string topicEdid)
    {
        using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
        var topic = pp.DialogTopics.FirstOrDefault(t => t.EditorID == topicEdid);
        return topic?.Responses.FirstOrDefault()?.FormKey;
    }

    // ---- synthetic MO2 layout helpers (the FacegenCarry / CompactServiceGuard probe pattern) ----

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

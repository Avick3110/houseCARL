using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// COPY SERVICE guard — the closure-copy operation driven through the REAL
/// <c>LoadOrderService.CopyClosure</c> over a synthetic MO2 instance, so the wiring between the pieces is under
/// test rather than the pieces themselves (those have their own guards). Pins:
///   ATTACH LANE   — an ACTIVE target is overridden into the patch with the internalized keys attached, the source
///                   is NOT among the written patch's masters, and the walk's per-node arm attribution survives
///                   all the way into the outcome the render will consume.
///   IN-PATCH E4   — a target whose FormID names the PATCH resolves off the OPENED patch mod, never the load
///                   order. The patch is not in the order, so an active resolve finds nothing: sabotaging the lane
///                   to resolve actively fails this arm rather than silently attaching to a stale body.
///   CLONE LANE    — a clone is minted under a fresh key with the new EditorID, its copied links remapped, and
///                   every remaining link into the source universe stripped.
///   DISABLED SRC  — the whole operation runs with the source plugin DISABLED, through the off-order file arm,
///                   which is the case the verb exists for.
///
/// GUARD-NOTE (branch rule): an arm downstream of a refusal reads it null-SAFELY (`?.`), never with the
/// null-forgiving `!`. An arm that THROWS under sabotage hides every arm behind it, so the sabotage's real
/// blast radius becomes unmeasurable. The grep is part of the rule, not a one-time cleanup.
/// Run: dotnet run --project src/housecarl-generator -- copy-service-guard
/// </summary>
public static class CopyServiceProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  COPY SERVICE guard (closure copy, wired)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var root = Path.Combine(Path.GetTempPath(), "hc-copy-service-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inst = Path.Combine(root, "inst");
            var mods = Path.Combine(inst, "mods");
            var data = Path.Combine(inst, "game", "Data");
            var prof = Path.Combine(inst, "profiles", "Default");
            foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

            // Base master (active, shared) + a follower mod holding the attach target + a DISABLED source.
            var baseKey = new ModKey("HcBase", ModType.Master);
            var raceFk = new FormKey(baseKey, 0x800);
            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            baseMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcRace" });
            WriteModDirect(mods, "BaseMod", baseMod);

            var folKey = new ModKey("Follower", ModType.Plugin);
            var targetFk = new FormKey(folKey, 0x800);
            var folMod = new SkyrimMod(folKey, SkyrimRelease.SkyrimSE);
            var tgt = new Npc(targetFk, SkyrimRelease.SkyrimSE) { EditorID = "TargetNpc" };
            tgt.Race.SetTo(raceFk);
            folMod.Npcs.Add(tgt);
            WriteModDirect(mods, "FollowerMod", folMod, baseMod);

            var srcKey = new ModKey("Src", ModType.Plugin);
            var txstFk = new FormKey(srcKey, 0x800);
            var hpFk = new FormKey(srcKey, 0x801);
            var factionFk = new FormKey(srcKey, 0x802);
            var srcNpcFk = new FormKey(srcKey, 0x803);
            var srcMod = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
            srcMod.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
            var srcHp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
            srcHp.TextureSet.SetTo(txstFk);
            srcMod.HeadParts.Add(srcHp);
            srcMod.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcFaction" });
            var srcNpc = new Npc(srcNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
            srcNpc.Race.SetTo(raceFk);
            srcNpc.HeadParts.Add(hpFk);
            srcNpc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(factionFk), Rank = 0 });
            srcMod.Npcs.Add(srcNpc);
            WriteModDirect(mods, "SrcMod", srcMod, baseMod);

            // SrcMod is switched OFF: the whole run goes through the off-order file arm.
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+FollowerMod\r\n+BaseMod\r\n-SrcMod\r\n");
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\nFollower.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n*Follower.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

            using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
            var seedPaths = new[] { "HeadParts" };
            var noExcl = Array.Empty<WalkExclusion>();

            // ---- 1. ATTACH onto an ACTIVE target, source DISABLED --------------------------------------------
            var att = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, seedPaths, noExcl,
                targetFk, null, "AttachPatch", null);
            Check(att.Success, $"attach onto an ACTIVE target with a DISABLED source succeeds ({att.EngineError ?? att.WalkRefusal?.Detail ?? att.CopyRefusal?.Detail})");
            Check(att.Mode == "attach" && att.NewKey == targetFk, "…the outcome names the attach lane and the target");
            Check(att.Copied.Count == 2, $"…the source subtree is internalized ({att.Copied.Count} records)");
            Check(att.Copied.All(c => c.NewKey.ModKey.FileName.String.Equals("AttachPatch.esp", StringComparison.OrdinalIgnoreCase)),
                "…under the patch's own keys");
            Check(att.Copied.All(c => c.ArmSpelling == "Src.esp"),
                "ATTRIBUTION survives the whole wired lane — every copy names the arm that produced it");
            Check(!att.SourceAmongMasters && !att.Masters.Any(m => m.Equals("Src.esp", StringComparison.OrdinalIgnoreCase)),
                "…and the DISABLED source is NOT a master of the written patch");
            Check(att.Attached.Any(a => a.Field == "HeadParts"), "…the seed field is reported as attached");

            if (att.OutPath is { } p1 && File.Exists(p1))
            {
                using var dispose = OpenDisposable(p1, out var back);
                var npc = back.Npcs.FirstOrDefault(n => n.FormKey == targetFk);
                Check(npc is not null, "the written patch overrides the target NPC");
                Check(npc is not null && npc.HeadParts.Count == 1
                      && npc.HeadParts[0].FormKey.ModKey == back.ModKey,
                    "…and its head part points at the INTERNALIZED copy, not the source");
            }
            else Check(false, "the attach patch was written to disk");

            // ---- 2. CLONE lane -------------------------------------------------------------------------------
            var clone = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, seedPaths, noExcl,
                null, "MyClone", "ClonePatch", null);
            Check(clone.Success, $"clone succeeds ({clone.EngineError ?? clone.WalkRefusal?.Detail ?? clone.CopyRefusal?.Detail})");
            Check(clone.Mode == "clone" && clone.NewKey.ModKey.FileName.String.Equals("ClonePatch.esp", StringComparison.OrdinalIgnoreCase),
                "…the clone is minted under the patch's own key");
            Check(clone.Stripped.Any(s => s.Field.StartsWith("Factions", StringComparison.Ordinal)),
                "…and its link into the source universe is STRIPPED and named");
            Check(!clone.SourceAmongMasters, "…the source is not a master of the clone patch");

            if (clone.OutPath is { } p2 && File.Exists(p2))
            {
                using var dispose2 = OpenDisposable(p2, out var back2);
                var cl = back2.Npcs.FirstOrDefault(n => n.FormKey == clone.NewKey);
                Check(cl?.EditorID == "MyClone", "the written clone carries the new EditorID");
                Check(cl is not null && cl.HeadParts.Count == 1 && cl.HeadParts[0].FormKey.ModKey == back2.ModKey,
                    "…and its head part is the internalized copy");
                Check(cl is not null && cl.Factions.Count == 0, "…with the source faction gone from the file");
            }
            else Check(false, "the clone patch was written to disk");

            // ---- 3. E4 — an IN-PATCH target resolves off the OPENED patch mod --------------------------------
            // Extend the clone patch and attach onto the clone itself, whose FormID names the PATCH. The patch is
            // not in the load order, so an implementation resolving the target actively finds nothing and refuses.
            var intoName = Path.GetFileName(clone.OutPath!);
            var inPatch = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, seedPaths, noExcl,
                clone.NewKey, null, null, intoName);
            Check(inPatch.Success,
                $"E4: an IN-PATCH target resolves off the OPENED patch mod ({inPatch.EngineError ?? inPatch.CopyRefusal?.Detail ?? inPatch.WalkRefusal?.Detail})");
            Check(inPatch.Mode == "attach" && inPatch.NewKey == clone.NewKey, "…naming that record as the target");
            Check(inPatch.Extended, "…and it EXTENDED the existing patch rather than minting a new one");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  guard threw: " + ex);
            fail++;
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }

    static void WriteModDirect(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }

    static IDisposable OpenDisposable(string path, out ISkyrimModGetter mod)
    {
        var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        mod = m;
        return (m as IDisposable) ?? new Noop();
    }

    sealed class Noop : IDisposable { public void Dispose() { } }
}

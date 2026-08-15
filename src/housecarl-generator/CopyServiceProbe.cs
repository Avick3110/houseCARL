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
            baseMod.Keywords.Add(new Keyword(new FormKey(baseKey, 0x801), SkyrimRelease.SkyrimSE) { EditorID = "HcKeyword" });
            baseMod.Armors.Add(new Armor(new FormKey(baseKey, 0x802), SkyrimRelease.SkyrimSE) { EditorID = "HcArmor" });
            WriteModDirect(mods, "BaseMod", baseMod);

            var folKey = new ModKey("Follower", ModType.Plugin);
            var targetFk = new FormKey(folKey, 0x800);
            var folMod = new SkyrimMod(folKey, SkyrimRelease.SkyrimSE);
            var kwFk = new FormKey(baseKey, 0x801);
            var tgt = new Npc(targetFk, SkyrimRelease.SkyrimSE) { EditorID = "TargetNpc" };
            tgt.Race.SetTo(raceFk);
            // The target carries a WornArmor and a Keyword the donor does NOT. Both are the R3 "unset ASSIGNS"
            // case, and the fixture is built so the pre-ruling behaviour (leave the target's own value) fails:
            // a copy that leaves them behind produces a face assembled from two records.
            var armorFk = new FormKey(baseKey, 0x802);
            tgt.WornArmor.SetTo(armorFk);
            tgt.Keywords = new Noggog.ExtendedList<Mutagen.Bethesda.Plugins.IFormLinkGetter<Mutagen.Bethesda.Skyrim.IKeywordGetter>>
                { new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IKeywordGetter>(kwFk) };
            folMod.Npcs.Add(tgt);
            WriteModDirect(mods, "FollowerMod", folMod, baseMod);

            // A record whose DEFINING plugin is neither active nor named as a source. Src.esp merely OVERRIDES it,
            // so it is outside the bound set and its only route into the closure is the scope predicate's second
            // disjunct ("…or it does not resolve in the active order"). Without that disjunct the link is kept and
            // the artifact declares a master that does not exist anywhere.
            var ghostKey = new ModKey("Ghost", ModType.Plugin);
            var ghostTexFk = new FormKey(ghostKey, 0x800);
            var ghostMod = new SkyrimMod(ghostKey, SkyrimRelease.SkyrimSE);
            ghostMod.TextureSets.Add(new TextureSet(ghostTexFk, SkyrimRelease.SkyrimSE) { EditorID = "GhostTex" });
            WriteModDirect(mods, "GhostMod", ghostMod, baseMod);   // on disk, DISABLED, and never a named source arm

            // A SECOND disabled source with a DISTINCT ModKey. Every earlier fixture had one file arm whose ModKey
            // already equalled `from`'s, which made the bound-universe loop over File arms a no-op everywhere —
            // deleting it left the whole suite AND the differential green (review round 1).
            var extraKey = new ModKey("Extra", ModType.Plugin);
            var extraHpFk = new FormKey(extraKey, 0x800);
            var extraMod = new SkyrimMod(extraKey, SkyrimRelease.SkyrimSE);
            var extraHp = new HeadPart(extraHpFk, SkyrimRelease.SkyrimSE) { EditorID = "ExtraBrow" };
            extraMod.HeadParts.Add(extraHp);
            WriteModDirect(mods, "ExtraMod", extraMod, baseMod);

            // …and an ACTIVE plugin that OVERRIDES one of that second source's records. This is what makes the
            // bound-universe loop over File arms falsifiable at all: without the override the record does not
            // resolve actively, so the scope predicate's SECOND disjunct expands it anyway and deleting the loop
            // changes nothing — measured, the first version of this fixture left that deletion green. With the
            // override present the record DOES resolve actively, so only being BOUND puts it in the closure.
            var shadowKey = new ModKey("Shadow", ModType.Plugin);
            var shadowMod = new SkyrimMod(shadowKey, SkyrimRelease.SkyrimSE);
            shadowMod.HeadParts.GetOrAddAsOverride(extraHp);
            // Shadow also DEFINES a head part and an NPC of its own, so there is an ON-ORDER record that a named
            // source binds — the case the post-attach leak check needs now that an off-order prune refuses first.
            var shadowHpFk = new FormKey(shadowKey, 0x800);
            shadowMod.HeadParts.Add(new HeadPart(shadowHpFk, SkyrimRelease.SkyrimSE) { EditorID = "ShadowBrow" });
            var shadowNpcFk = new FormKey(shadowKey, 0x801);
            var shadowNpc = new Npc(shadowNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "ShadowNpc" };
            shadowNpc.Race.SetTo(raceFk);
            shadowNpc.HeadParts.Add(shadowHpFk);
            shadowMod.Npcs.Add(shadowNpc);
            WriteModDirect(mods, "ShadowMod", shadowMod, baseMod, extraMod);

            var srcKey = new ModKey("Src", ModType.Plugin);
            var txstFk = new FormKey(srcKey, 0x800);
            var hpFk = new FormKey(srcKey, 0x801);
            var factionFk = new FormKey(srcKey, 0x802);
            var srcNpcFk = new FormKey(srcKey, 0x803);
            var srcMod = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
            srcMod.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
            var srcHp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
            srcHp.TextureSet.SetTo(txstFk);
            // An asset link, so inventory I1's readback has something to enumerate inside ci-all rather than only
            // in the on-demand differential — whose ancestor half dies at 2.0, taking I1's only test with it.
            srcHp.Model = new Model { File = @"actors\character\hair\srchair.nif" };
            srcMod.HeadParts.Add(srcHp);
            srcMod.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcFaction" });
            // Src.esp carries an OVERRIDE of a Ghost.esp record — Ghost is never active and never a named source.
            var ghostOverride = new TextureSet(ghostTexFk, SkyrimRelease.SkyrimSE) { EditorID = "GhostTex" };
            srcMod.TextureSets.Add(ghostOverride);
            var srcNpc = new Npc(srcNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
            srcNpc.Race.SetTo(raceFk);
            srcNpc.HeadParts.Add(hpFk);
            srcNpc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(factionFk), Rank = 0 });
            srcMod.Npcs.Add(srcNpc);
            // A SECOND donor, carrying only the links arms 4-8 need. Kept off the first donor so the lanes those
            // arms were written for keep testing what they were written for.
            var wideNpcFk = new FormKey(srcKey, 0x804);
            var wideNpc = new Npc(wideNpcFk, SkyrimRelease.SkyrimSE) { EditorID = "WideNpc" };
            wideNpc.Race.SetTo(raceFk);
            wideNpc.HeadParts.Add(hpFk);
            wideNpc.HeadParts.Add(extraHpFk);         // only the SECOND arm has this one
            wideNpc.HeadTexture.SetTo(ghostTexFk);    // only reachable via the not-active-anywhere disjunct
            srcMod.Npcs.Add(wideNpc);
            WriteModDirect(mods, "SrcMod", srcMod, baseMod, extraMod, ghostMod);

            // SrcMod is switched OFF: the whole run goes through the off-order file arm.
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+ShadowMod\r\n+FollowerMod\r\n+BaseMod\r\n-SrcMod\r\n-ExtraMod\r\n-GhostMod\r\n");
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\nFollower.esp\r\nShadow.esp\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n*Follower.esp\r\n*Shadow.esp\r\n");
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

            // ---- 4. A TWO-ARM chain with DISTINCT ModKeys ----------------------------------------------------
            // Both source plugins are bound, so BOTH plugins' records internalize and NEITHER ends up a master.
            // With one file arm this arm is unfalsifiable — the arm's ModKey already equals `from`'s.
            var twoArm = svc.CopyClosure(wideNpcFk, new[] { "Src.esp", "Extra.esp" }, new[] { "HeadParts", "HeadTexture" },
                noExcl, null, "TwoArmClone", "TwoArmPatch", null);
            Check(twoArm.Success, $"a TWO-ARM chain with distinct ModKeys copies ({twoArm.EngineError ?? twoArm.WalkRefusal?.Detail ?? twoArm.CopyRefusal?.Detail})");
            Check(twoArm.Copied.Any(c => c.EditorId == "ExtraBrow" && c.ArmSpelling == "Extra.esp"),
                "…the SECOND arm's record is internalized and attributed to it — an ACTIVE plugin also provides " +
                "that key, so only being BOUND puts it in the closure");
            Check(!twoArm.Masters.Any(m => m.Equals("Extra.esp", StringComparison.OrdinalIgnoreCase)),
                "…and the SECOND arm's plugin is NOT a master — the bound universe covers every file arm, not just from's");
            Check(!twoArm.SourceAmongMasters, "…so the standalone claim is computed over BOTH source plugins");

            // ---- 5. The scope predicate's SECOND disjunct — a record active nowhere -------------------------
            Check(twoArm.Copied.Any(c => c.EditorId == "GhostTex"),
                "a record whose defining plugin is active NOWHERE is internalized (the missing-master disjunct)");
            Check(!twoArm.Masters.Any(m => m.Equals("Ghost.esp", StringComparison.OrdinalIgnoreCase)),
                "…so the artifact does not declare a master that exists nowhere");

            // ---- 6. A path that NAMES an active plugin is that plugin ---------------------------------------
            var activePath = Path.Combine(mods, "FollowerMod", "Follower.esp");
            var byPath = svc.WithSourceChainForGuard(new[] { activePath }, "from_source",
                (chain, err) => chain is null ? "ERR:" + err : chain.Arms[0].Kind + "|" + chain.Arms[0].Where);
            Check(byPath.StartsWith("ActiveOrder"), $"a full PATH naming an ACTIVE plugin resolves as the active arm ({byPath})");
            Check(!byPath.Contains("NOT active"), "…and is not described as off-order");

            // ---- 7a. 'stop' against an OFF-ORDER record refuses UP FRONT ------------------------------------
            // The pruned link is kept, so the patch would have to master a plugin the game does not load — which
            // the serializer cannot do at all. It used to throw a raw MissingModException from inside the write,
            // in the verb's headline lane (a disabled donor), with no next step for the caller.
            var stopExcl = new[] { new WalkExclusion("HeadPart", ExclusionSeverity.Stop, "pruned for the test") };
            var stopOff = svc.CopyClosure(wideNpcFk, new[] { "Src.esp", "Extra.esp" }, new[] { "HeadParts" },
                stopExcl, targetFk, null, "StopOffPatch", null);
            Check(!stopOff.Success && stopOff.CopyRefusal?.Kind == CopyRefusalKind.StopOffOrder,
                $"'stop' on an OFF-ORDER record refuses up front ({(stopOff.Success ? "IT DID NOT" : stopOff.CopyRefusal?.Kind.ToString())})");
            Check(stopOff.CopyRefusal?.Detail is "Src.esp" or "Extra.esp",
                $"…naming the plugin the game does not load ({stopOff.CopyRefusal?.Detail})");
            Check(!Directory.EnumerateDirectories(mods, "*StopOffPatch*").Any(), "…and writes nothing (C11)");

            // ---- 7b. …while an ON-ORDER pruned link still reaches the post-attach leak check ----------------
            // Shadow.esp is ACTIVE and NAMED, so ruling 2 binds it; its own head part is pruned, kept, attached
            // unmapped, and the leak check is the only thing between that and a patch mastering its own source.
            var leak = svc.CopyClosure(shadowNpcFk, new[] { "Shadow.esp" }, new[] { "HeadParts" },
                stopExcl, targetFk, null, "LeakPatch", null);
            Check(!leak.Success && leak.CopyRefusal?.Kind == CopyRefusalKind.DonorLeak,
                $"an ON-ORDER pruned link is CAUGHT by the post-attach leak check ({(leak.Success ? "IT WAS NOT" : leak.CopyRefusal?.Kind.ToString())})");
            Check(leak.CopyRefusal?.Field == ClosureCopy.ExclusionLeakMarker,
                "…and the refusal carries WHY, so the render blames the exclusion rather than the caller's target");

            // ---- 7c. RULING 2 — an ACTIVE named source is BOUND ---------------------------------------------
            // Same call without the exclusion: Shadow.esp is enabled, so it used to take the ActiveOrder arm, stay
            // unbound, and be MASTERED — while the response asserted "standalone: the source is NOT a master".
            var activeNamed = svc.CopyClosure(shadowNpcFk, new[] { "Shadow.esp" }, new[] { "HeadParts" },
                noExcl, null, "ActiveNamedClone", "ActiveNamedPatch", null);
            Check(activeNamed.Success, $"an ENABLED plugin named in from_source= copies ({activeNamed.EngineError ?? activeNamed.WalkRefusal?.Detail ?? activeNamed.CopyRefusal?.Detail})");
            Check(activeNamed.Copied.Any(c => c.EditorId == "ShadowBrow"),
                "…its records are INTERNALIZED, not kept as mastered links — naming a plugin binds it whatever its kind");
            Check(!activeNamed.Masters.Any(m => m.Equals("Shadow.esp", StringComparison.OrdinalIgnoreCase)),
                "…so it is NOT among the masters…");
            Check(!activeNamed.SourceAmongMasters,
                "…and the standalone claim is computed over the same corrected set");

            // ---- 7d. THE SHAPE CLASSIFIER — the four misbehaviours round 2 measured, as arms ----------------
            // Each of these was a site deciding the shape for itself off the runtime VALUE. Mutagen declares these
            // list properties NON-nullable and the binary overlay hands back null when the subrecord is absent, so
            // the value and the model disagree and only the model can answer "what shape is this field".

            // (a) clone lane: a struct-element list refuses BY NAME even when the donor carries none of it. This
            //     is the one that was silently skipped — the walk never reached a shape test at all.
            var clonePerks = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, new[] { "HeadParts", "Perks" },
                noExcl, null, "PerkClone", "PerkPatch", null);
            Check(!clonePerks.Success && clonePerks.WalkRefusal?.Kind == WalkRefusalKind.UnsupportedSeedShape,
                $"(a) a struct-element seed the donor carries NONE of still refuses by shape ({(clonePerks.Success ? "IT WAS SKIPPED" : clonePerks.WalkRefusal?.Kind.ToString())})");
            Check(clonePerks.WalkRefusal?.Detail?.Contains("Perks") == true,
                "…naming the field rather than passing over it");

            // (b) attach, null SOURCE link: the target's is CLEARED and the readback says which happened.
            var clearLink = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, new[] { "HeadParts", "WornArmor" },
                noExcl, targetFk, null, "ClearLinkPatch", null);
            Check(clearLink.Success, $"(b) an UNSET source link copies ({clearLink.EngineError ?? clearLink.WalkRefusal?.Detail ?? clearLink.CopyRefusal?.Detail})");
            Check(clearLink.Attached.Any(a => a.Field == "WornArmor" && a.Cleared),
                "…and CLEARS the target's rather than leaving a mixture, reported as cleared");
            if (clearLink.OutPath is { } cp && File.Exists(cp))
            {
                using var d3 = OpenDisposable(cp, out var back3);
                var t3 = back3.Npcs.FirstOrDefault(n => n.FormKey == targetFk);
                Check(t3 is not null && t3.WornArmor.IsNull, "…in the WRITTEN FILE, not just in the outcome object");
            }
            else Check(false, "…and the patch was written");

            // (c) + (d) attach, null SOURCE list on a field seed_paths fully supports. Round 2 measured this
            //     refused as "neither a record link nor a list of record links" and routed to housecarl_apply.
            var clearList = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, new[] { "HeadParts", "Keywords" },
                noExcl, targetFk, null, "ClearListPatch", null);
            Check(clearList.Success, $"(c) an UNSET source LIST copies ({clearList.EngineError ?? clearList.WalkRefusal?.Detail ?? clearList.CopyRefusal?.Detail})");
            Check(clearList.Attached.Any(a => a.Field == "Keywords" && a.Cleared),
                "…clearing the target's list, which is a state of the shape and not a different shape");
            Check(CopyTools.Render(clearList).Contains("housecarl_apply", StringComparison.Ordinal) == false,
                "(d) …and a SUPPORTED field is never routed to housecarl_apply's zip");

            // ---- 7e. INVENTORY I1 in ci-all, independent of the differential --------------------------------
            var withAssets = svc.CopyClosure(srcNpcFk, new[] { "Src.esp" }, new[] { "HeadParts" },
                noExcl, null, "AssetClone", "AssetPatch", null);
            Check(withAssets.Success && withAssets.AssetPaths.Count > 0,
                $"I1: the copy REPORTS the asset paths its copied records reference ({withAssets.AssetPaths.Count})");
            Check(withAssets.AssetPaths.Any(a => a.Contains("srchair.nif", StringComparison.OrdinalIgnoreCase)),
                "…harvested from the in-patch duplicates, naming the actual path");

            // ---- 8. A refusal after the folder exists leaves NO folder --------------------------------------
            var before = Directory.GetDirectories(mods).Length;
            var refused = svc.CopyClosure(wideNpcFk, new[] { "Src.esp", "Extra.esp" }, new[] { "HeadParts" },
                stopExcl, targetFk, null, "OrphanCheck", null);
            Check(!refused.Success, "a refusal that happens AFTER the output folder is resolved…");
            Check(Directory.GetDirectories(mods).Length == before
                  && !Directory.EnumerateDirectories(mods, "*OrphanCheck*").Any(),
                "…removes the folder it created this call — no orphan left behind");
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

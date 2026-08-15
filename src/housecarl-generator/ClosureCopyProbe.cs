using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// CLOSURE COPY guard — the ACT consumer of a walk (SPEC §3.3): internalize under fresh keys, strip what is left
/// pointing at the source universe, and prove the artifact does not master it. Every arm here sits on a spot where
/// the ancestor had a real bug or a real trap, so each one DISCRIMINATES rather than asserts:
///   INTERNALIZE   — fresh local keys off the patch's own counter; an EXTENDED patch keeps counting; internal
///                   references among the copies resolve to the NEW keys.
///   REMAP SCOPING — a patch record the user already put there, deliberately referencing a key being renumbered,
///                   survives UNTOUCHED. Whole-mod RemapLinks would rewrite it; the scratch-mod step is what stops
///                   that, and this arm fails if the step is removed.
///   NULLABILITY   — judged on the record model's IFormLinkNullable, never on SetToNull's presence. The fixture's
///                   required link is on a type that HAS SetToNull, so an implementation deciding by method
///                   presence nulls a required field and fails — the exact shipped bug (Class=00000000).
///   REQUIRED LINK — a loud refusal, RED-checkable in both directions: suppressing the refusal keeps a silent
///                   master, nulling anyway invents data, and each fails a distinct arm.
///   LEAK SCOPING  — a surviving bound link IS a leak (true positive); a target's PRE-EXISTING dangling link is
///                   NOT (false positive). Two fixtures, two arms — the scoping is the load-bearing half.
///   PROVENANCE    — the walk's per-node arm attribution survives into the copy report.
///
/// GUARD-NOTE (branch rule): an arm downstream of a refusal reads it null-SAFELY (`?.`), never with the
/// null-forgiving `!`. An arm that THROWS under sabotage hides every arm behind it, so the sabotage's real
/// blast radius becomes unmeasurable — a RED check on this branch reported 1 failure where the true number
/// was 6. An arm that can throw under sabotage is an arm that lies about severity. The grep is part of the rule,
/// not a one-time cleanup: the sweep that introduced this note also introduced a FRESH `!` one line below a
/// deref it was fixing, caught only by re-running the grep before commit.
/// Run: dotnet run --project src/housecarl-generator -- closure-copy-guard
/// </summary>
public static class ClosureCopyProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  CLOSURE COPY guard (internalize · strip · leak)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var srcKey = new ModKey("Source", ModType.Plugin);      // the bound universe — copied away FROM
        var keepKey = new ModKey("Keep", ModType.Master);       // active shared resource — stays a link
        var patchKey = new ModKey("Patch", ModType.Plugin);

        // ---- the source graph: hp -> txst, plus a CLFM in the shared master ---------------------------------
        var txstFk = new FormKey(srcKey, 0x800);
        var hpFk = new FormKey(srcKey, 0x801);
        var factionFk = new FormKey(srcKey, 0x802);
        var outfitFk = new FormKey(srcKey, 0x803);
        var classFk = new FormKey(srcKey, 0x804);
        var npcFk = new FormKey(srcKey, 0x805);
        var keptClfmFk = new FormKey(keepKey, 0x900);

        var src = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
        src.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
        var hp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
        hp.TextureSet.SetTo(txstFk);
        src.HeadParts.Add(hp);
        src.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcFaction" });
        src.Outfits.Add(new Outfit(outfitFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcOutfit" });
        src.Classes.Add(new Class(classFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcClass" });

        var bodies = src.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r);
        var chain = SourceChain.Single(new SourceArm("Source.esp", SourceArmKind.File, "file 'Source.esp'",
            fk => bodies.TryGetValue(fk, out var b) ? b : null));
        var bound = new HashSet<ModKey> { srcKey };
        bool IsBound(FormKey fk) => bound.Contains(fk.ModKey);
        var scope = WalkScope.StandaloneFrom(bound, fk => fk.ModKey == keepKey);

        var seeds = new[] { new WalkSeed(hpFk, "HeadParts", "Npc.HeadParts") };
        var walk = ClosureWalk.Run(seeds, chain, scope, Array.Empty<WalkExclusion>());
        Check(walk.Success && walk.Reached.Count == 2, $"the walk reaches the source subtree ({walk.Reached.Count})");

        // ---- 1. INTERNALIZE + REMAP SCOPING ------------------------------------------------------------------
        // The patch is EXTENDED and already holds a record the user put there that deliberately references a
        // source key this copy is about to renumber. A whole-mod RemapLinks would silently repoint it.
        var patch = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
        var priorFk = new FormKey(patchKey, 0x800);
        var prior = new HeadPart(priorFk, SkyrimRelease.SkyrimSE) { EditorID = "UserPrior" };
        prior.TextureSet.SetTo(txstFk);            // the DELIBERATE reference into the still-active source
        patch.HeadParts.Add(prior);
        patch.ModHeader.Stats.NextFormID = 0x801;

        var copy = ClosureCopy.Internalize(patch, walk.Reached);
        Check(copy.Success, $"internalize succeeds ({copy.Refusal?.Detail})");
        Check(copy.Copied.Count == 2 && copy.Copied.All(c => c.NewKey.ModKey == patchKey),
            "every copy lands under the patch's OWN new keys");
        Check(copy.Copied.All(c => c.NewKey.ID >= 0x800), "…allocated from the patch's own counter (0x800+)");
        Check(copy.Copied.Select(c => c.NewKey).Distinct().Count() == 2, "…each under a distinct key");

        var newHp = patch.HeadParts.First(h => h.FormKey == copy.Map[hpFk]);
        Check(newHp.EditorID == "SrcHair", "EditorIDs are preserved by the whole-record duplicate");
        Check(newHp.TextureSet.FormKey == copy.Map[txstFk],
            "an internal reference among the copies is REMAPPED to the new key");

        var priorAfter = patch.HeadParts.First(h => h.FormKey == priorFk);
        Check(priorAfter.TextureSet.FormKey == txstFk,
            "REMAP SCOPING: the patch's PRE-EXISTING deliberate reference survives UNTOUCHED (a whole-mod remap would rewrite it)");

        Check(copy.Copied.All(c => c.ArmSpelling == "Source.esp" && c.ArmIndex == 0),
            "PROVENANCE: the walk's per-node arm attribution survives into the copy report");
        Check(copy.Copied.Any(c => c.PulledBy.Length > 0), "…as does what pulled each record in");

        // ---- 2. STRIP: nullability judged on the MODEL, not on SetToNull's presence -------------------------
        // The clone carries: a nullable link into the source (DefaultOutfit), a LIST element carrying one
        // (Factions), and a REQUIRED link into the source (Class). Npc.Class is a required FormLink<IClassGetter>
        // — and it HAS a SetToNull method, which is precisely why method-presence is the wrong test.
        var clone = new Npc(new FormKey(patchKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Clone" };
        clone.DefaultOutfit.SetTo(outfitFk);
        clone.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(factionFk), Rank = 0 });
        clone.HeadParts.Add(copy.Map[hpFk]);       // already internalized — must NOT be stripped
        clone.Class.SetTo(classFk);                // REQUIRED, and bound

        Check(clone.Class.GetType().GetMethod("SetToNull", Type.EmptyTypes) is not null,
            "FIXTURE PRECONDITION: the REQUIRED link's type DOES expose SetToNull (so method-presence would 'succeed')");

        // Dependent arms below read the refusal with `?.` on purpose: a sabotage that DELETES the refusal must
        // make them FAIL, not throw — a probe that crashes under RED hides every arm behind the crash, so the
        // sabotage's true blast radius becomes unmeasurable.
        var stripReq = ClosureCopy.StripBoundLinks(clone, IsBound);
        Check(!stripReq.Success && stripReq.Refusal?.Kind == CopyRefusalKind.RequiredForeignLink,
            "a REQUIRED bound link REFUSES — never nulled (that shipped Class=00000000 once)");
        Check(stripReq.Refusal?.Field == "Class" && stripReq.Refusal?.Key == classFk,
            "…naming the field and the key, so the caller can act on it");
        Check(!clone.Class.IsNull && clone.Class.FormKey == classFk,
            "…and the required link is STILL SET — the refusal did not null what it refused to null");
        // The refusal must leave the record EXACTLY as it found it, not merely leave the named field alone. A
        // single mutating pass strips the clearable shapes first and refuses on reaching the required link, so the
        // caller gets a half-stripped record back with a failure. This lane's guard caught precisely that.
        Check(clone.Factions.Count == 1 && !clone.DefaultOutfit.IsNull,
            "…and NOTHING ELSE was stripped either — a refusal leaves the record untouched, not half-done");

        // Now the same clone WITHOUT the required-link trap: the clearable shapes strip and are each named.
        clone.Class.SetTo(new FormKey(keepKey, 0x901));   // a class outside the bound universe
        var strip = ClosureCopy.StripBoundLinks(clone, IsBound);
        Check(strip.Success, $"the strip succeeds once no REQUIRED bound link remains ({strip.Refusal?.Detail})");
        Check(strip.Stripped.Any(s => s.Field == "DefaultOutfit" && s.Removed.Contains("803")),
            "a NULLABLE bound link is nulled and NAMED");
        Check(clone.DefaultOutfit.IsNull, "…and is actually gone from the record");
        Check(strip.Stripped.Any(s => s.Field.StartsWith("Factions[")), "a LIST ELEMENT carrying a bound link is dropped and NAMED");
        Check(clone.Factions.Count == 0, "…and is actually gone");
        Check(clone.HeadParts.Count == 1 && clone.HeadParts[0].FormKey == copy.Map[hpFk],
            "an ALREADY-INTERNALIZED link is untouched — the strip removes what was not copied, not what was");

        // ---- 3. LEAK CHECK — both directions -----------------------------------------------------------------
        // TRUE POSITIVE: a bound link that survived the remap is a leak.
        var leaky = new Npc(new FormKey(patchKey, 0x901), SkyrimRelease.SkyrimSE) { EditorID = "Leaky" };
        leaky.DefaultOutfit.SetTo(outfitFk);
        Check(ClosureCopy.FindBoundLeak(leaky, IsBound) == outfitFk,
            "LEAK (true positive): a surviving bound link is found and named");

        // FALSE POSITIVE: a target's PRE-EXISTING dangling link — points at a plugin that is neither bound nor
        // resolvable (mod-update dirt). Not this operation's defect, and it must not block a legitimate copy.
        var dirty = new Npc(new FormKey(patchKey, 0x902), SkyrimRelease.SkyrimSE) { EditorID = "Dirty" };
        dirty.DefaultOutfit.SetTo(new FormKey(new ModKey("GoneAway", ModType.Plugin), 0xABC));
        Check(ClosureCopy.FindBoundLeak(dirty, IsBound) is null,
            "LEAK (false positive): a PRE-EXISTING dangling link is NOT a leak — the check is scoped to bound keys");
        Check(ClosureCopy.FindBoundLeak(clone, IsBound) is null,
            "…and a properly stripped record is clean");

        // ---- 4. ATTACH — seed fields onto a target, with the internalized keys substituted -------------------
        // End-to-end at the core level: the SAME extended patch that already holds the user's deliberate
        // donor-pointing record, a real internalize, then a real attach. The unit arm above proved the scratch-mod
        // mechanism; this proves the wiring around it does not undo the property. (The service-level version of
        // this arm lands with the tool wiring — there is no `copy` service method to drive yet, and an arm that
        // drives the core while claiming to test the wire is exactly what PR #339 taught us not to write.)
        var attachTarget = new Npc(new FormKey(patchKey, 0x950), SkyrimRelease.SkyrimSE) { EditorID = "AttachTarget" };
        attachTarget.HeadParts.Add(new FormKey(keepKey, 0x9FF));   // a pre-existing head part, to be replaced
        patch.Npcs.Add(attachTarget);

        var srcNpc = new Npc(npcFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
        srcNpc.HeadParts.Add(hpFk);                                 // the walked seed, at its DONOR key
        srcNpc.WornArmor.SetTo(new FormKey(keepKey, 0x902));        // a single-link seed outside the bound universe

        var attach = ClosureCopy.AttachSeedFields(
            attachTarget, srcNpc, new[] { "HeadParts", "WornArmor" }, copy.Map, IsBound);
        Check(attach.Success, $"attach succeeds ({attach.Refusal?.Detail})");
        Check(attachTarget.HeadParts.Count == 1 && attachTarget.HeadParts[0].FormKey == copy.Map[hpFk],
            "ATTACH: a list seed is set to the INTERNALIZED key, not the donor's");
        Check(attachTarget.WornArmor.FormKey == new FormKey(keepKey, 0x902),
            "…a single-link seed outside the copied set keeps its own key (nothing to substitute)");
        Check(ClosureCopy.FindBoundLeak(attachTarget, IsBound) is null,
            "…and the attached target carries NO link into the source universe");

        var priorAfterAttach = patch.HeadParts.First(h => h.FormKey == priorFk);
        Check(priorAfterAttach.TextureSet.FormKey == txstFk,
            "ATTACH SCOPING (end-to-end): after internalize AND attach, the patch's pre-existing deliberate reference is STILL untouched");

        // Attribution must survive the whole lane — this is the last layer before the readback, so if it flattens
        // here the "which source produced this body" promise dies silently rather than loudly.
        Check(copy.Copied.All(c => c.ArmSpelling == "Source.esp") && copy.Copied.Count == 2,
            "ATTRIBUTION: after attach, the copy result still answers 'which arm produced this body' per node");

        // E1 — a target INSIDE the source universe refuses: you cannot standalone-ize a record onto itself.
        var selfTarget = new Npc(new FormKey(srcKey, 0x950), SkyrimRelease.SkyrimSE) { EditorID = "SelfTarget" };
        var selfAttach = ClosureCopy.AttachSeedFields(
            selfTarget, srcNpc, new[] { "HeadParts" }, copy.Map, IsBound);
        Check(!selfAttach.Success && selfAttach.Refusal?.Kind == CopyRefusalKind.DonorLeak,
            "a target INSIDE the source universe REFUSES — no standalone-izing a record onto itself");
        Check(selfAttach.Refusal?.Key == selfTarget.FormKey, "…naming the offending target");
        Check(selfTarget.HeadParts.Count == 0, "…and the refusal changed nothing on it");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }
}

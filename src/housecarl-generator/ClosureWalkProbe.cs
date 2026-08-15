using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// CLOSURE WALK guard (SPEC §3 — the generic link walk PR 3b's `copy` is built on). Pins the components §3.1
/// declares, on in-memory records so the walk is tested apart from MO2 entirely:
///   GENERIC       — expansion is EnumerateFormLinks, so a link on a type the walk has never heard of is still
///                   followed; NO appearance vocabulary reaches this file or the walk.
///   SEEDS AS DATA — the seed PATHS are supplied by the caller; a typo REFUSES by name rather than seeding
///                   nothing (a silent empty walk is how an ACT consumer blanks its target).
///   SCOPE         — expand-vs-keep: in-scope records join the reached set, out-of-scope ones are KEPT as named
///                   boundaries rather than being absent.
///   EXCLUSIONS    — supplied as DATA with two severities: Stop prunes and records a boundary, Refuse fails the
///                   whole walk. No record type is special-cased in the walk.
///   CAP (ACT)     — a breach REFUSES with the last pull AND its full chain, and yields nothing usable. The
///                   chain is asserted by CONTENT, not by presence — see the RED note below.
///   CYCLES        — a genuine cycle is RECORDED and reported; a DIAMOND (two paths to one record) is not. This
///                   behavior has no ancestor in copy_npc_appearance, so this arm is its only evidence: the
///                   fixture carries both shapes at once and asserts the report tells them apart.
///   PROVENANCE    — per NODE, not per walk: each reached record names WHICH source arm produced its body, so a
///                   walk over an ordered universe can say this record came from the override and that one fell
///                   through to the defining plugin.
///
/// GUARD-NOTE (branch rule): an arm downstream of a refusal reads it null-SAFELY (`?.`), never with the
/// null-forgiving `!`. An arm that THROWS under sabotage hides every arm behind it, so the sabotage's real
/// blast radius becomes unmeasurable — a RED check on this branch reported 1 failure where the true number
/// was 6. An arm that can throw under sabotage is an arm that lies about severity. The grep is part of the rule,
/// not a one-time cleanup: the sweep that introduced this note also introduced a FRESH `!` one line below a
/// deref it was fixing, caught only by re-running the grep before commit.
/// Run: dotnet run --project src/housecarl-generator -- closure-walk-guard
/// </summary>
public static class ClosureWalkProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  CLOSURE WALK guard (SPEC §3 — the generic walk)  ################");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var modKey = new ModKey("WalkFix", ModType.Plugin);
        var outside = new ModKey("Outside", ModType.Master);
        var mod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);

        // ---- fixture: an NPC seeding two link fields into a small graph ------------------------------------
        //   npc.HeadParts -> hpA -> txst                     hpA.ExtraParts -> hpB -> txst (DIAMOND, not a cycle)
        //                              hpB.ExtraParts -> hpC -> txst2   (a 4-long chain: the walk must carry the
        //                                                                WHOLE path, and depth 3 exists to cap)
        //   npc.WornArmor -> armo -> arma -> armo            (CYCLE: arma points back at its own parent)
        //   npc.HairColor -> clfmOutside                     (OUT OF SCOPE: kept as a boundary)
        var txstFk = new FormKey(modKey, 0x800);
        var hpAFk = new FormKey(modKey, 0x801);
        var hpBFk = new FormKey(modKey, 0x802);
        var armoFk = new FormKey(modKey, 0x803);
        var armaFk = new FormKey(modKey, 0x804);
        var npcFk = new FormKey(modKey, 0x805);
        var raceFk = new FormKey(modKey, 0x806);
        var clfmOutFk = new FormKey(outside, 0x900);

        var txst = new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SharedTex" };
        mod.TextureSets.Add(txst);

        // hpD closes a LATE diamond: it is reached at depth 3, long after txst has been dequeued, so its edge
        // back to txst genuinely reaches the repeat-link branch and the ancestry test has to rule on it. The
        // shallow diamond (hpB -> txst) does NOT: `seen` fills at dequeue, so that edge is deduped by the queue
        // before the branch runs. A RED check found this — the cycle arm passed without ever exercising the
        // discrimination it claims to test.
        var hpDFk = new FormKey(modKey, 0x809);
        var txst2Fk = new FormKey(modKey, 0x807);
        mod.TextureSets.Add(new TextureSet(txst2Fk, SkyrimRelease.SkyrimSE) { EditorID = "DeepTex" });
        var hpCFk = new FormKey(modKey, 0x808);
        var hpD = new HeadPart(hpDFk, SkyrimRelease.SkyrimSE) { EditorID = "HpD" };
        hpD.TextureSet.SetTo(txstFk);          // the LATE re-convergence on the shared texture set
        mod.HeadParts.Add(hpD);

        var hpC = new HeadPart(hpCFk, SkyrimRelease.SkyrimSE) { EditorID = "HpC" };
        hpC.TextureSet.SetTo(txst2Fk);
        hpC.ExtraParts.Add(hpDFk);
        mod.HeadParts.Add(hpC);

        var hpB = new HeadPart(hpBFk, SkyrimRelease.SkyrimSE) { EditorID = "HpB" };
        hpB.TextureSet.SetTo(txstFk);
        hpB.ExtraParts.Add(hpCFk);
        mod.HeadParts.Add(hpB);

        var hpA = new HeadPart(hpAFk, SkyrimRelease.SkyrimSE) { EditorID = "HpA" };
        hpA.TextureSet.SetTo(txstFk);
        hpA.ExtraParts.Add(hpBFk);
        mod.HeadParts.Add(hpA);

        var armo = new Armor(armoFk, SkyrimRelease.SkyrimSE) { EditorID = "Armo" };
        armo.Armature.Add(armaFk);
        mod.Armors.Add(armo);
        var arma = new ArmorAddon(armaFk, SkyrimRelease.SkyrimSE) { EditorID = "Arma" };
        arma.Race.SetTo(armoFk);          // the back-edge that closes the cycle (a link, not a real Race — the
                                          // walk is type-blind and that is the point)
        mod.ArmorAddons.Add(arma);

        mod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "InScopeRace" });

        var npc = new Npc(npcFk, SkyrimRelease.SkyrimSE) { EditorID = "Seed" };
        npc.HeadParts.Add(hpAFk);
        npc.WornArmor.SetTo(armoFk);
        npc.HairColor.SetTo(clfmOutFk);
        mod.Npcs.Add(npc);

        var bodies = mod.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r);
        SourceChain OneArm(string name = "WalkFix.esp") => SourceChain.Single(new SourceArm(
            name, SourceArmKind.File, $"file '{name}'",
            fk => bodies.TryGetValue(fk, out var b) ? b : null));

        // In scope = defined in the fixture plugin. The Outside master is not, so it is a boundary.
        var bound = new HashSet<ModKey> { modKey };
        var scope = WalkScope.StandaloneFrom(bound, fk => fk.ModKey == outside);
        var noExcl = Array.Empty<WalkExclusion>();

        // ---- 1. SEEDS AS DATA ------------------------------------------------------------------------------
        var seedFail = ClosureWalk.ResolveSeeds(npc, new[] { "HeadParts", "WornArmor", "HairColor" }, out var seeds);
        Check(seedFail is null && seeds.Count == 3, $"seed paths resolve to their links ({seeds.Count})");
        Check(seeds.Any(s => s.Label == "Npc.HeadParts"), "each seed carries its own provenance label ('Npc.HeadParts')");

        var typo = ClosureWalk.ResolveSeeds(npc, new[] { "HeadParts", "HeadPart" }, out _);
        Check(typo is { Success: false } && typo.Refusal?.Kind == WalkRefusalKind.UnknownSeedPath,
            "a TYPO'd seed path REFUSES — it does not quietly seed nothing");
        Check(typo?.Refusal?.Detail?.Contains("HeadPart") == true, "…and the refusal names the path that was wrong");
        var notLinks = ClosureWalk.ResolveSeeds(npc, new[] { "Height" }, out _);
        Check(notLinks is { Success: false }, "a seed path that carries no record links REFUSES too (same class as a typo)");

        // ---- 2. GENERIC EXPANSION + SCOPE + PROVENANCE -----------------------------------------------------
        var r = ClosureWalk.Run(seeds, OneArm(), scope, noExcl);
        Check(r.Success, $"the walk succeeds ({r.Refusal?.Detail})");
        var reachedKeys = r.Reached.Select(n => n.Key).ToHashSet();
        Check(reachedKeys.SetEquals(new[] { hpAFk, hpBFk, hpCFk, hpDFk, txstFk, txst2Fk, armoFk, armaFk }),
            $"reaches the whole in-scope closure through EnumerateFormLinks — no per-type list ({r.Reached.Count} nodes)");
        Check(r.Kept.Count == 1 && r.Kept[0].Key == clfmOutFk,
            "an out-of-scope link is KEPT as a named boundary, not silently absent");
        Check(r.Reached.All(n => n.ArmSpelling == "WalkFix.esp" && n.ArmIndex == 0),
            "PROVENANCE: every reached node names WHICH source arm produced its body");
        // The DEEP node, deliberately: a 1-hop node's chain cannot tell a full chain from a parent label.
        var deep = r.Reached.First(n => n.Key == txst2Fk);
        Check(deep.Chain.Count == 4 && deep.Chain[0] == hpAFk && deep.Chain[1] == hpBFk
              && deep.Chain[2] == hpCFk && deep.Chain[^1] == txst2Fk,
            $"each node carries its FULL pull chain from the seed link onward, not one hop ({deep.Chain.Count} long)");
        Check(deep.Depth == 3, $"…and its depth ({deep.Depth})");

        // Provenance is PER NODE, not per walk: a two-arm universe where the second arm holds one record must
        // attribute that record to arm 1 and everything else to arm 0.
        var split = new SourceChain(new[]
        {
            new SourceArm("Override.esp", SourceArmKind.File, "file 'Override.esp'",
                fk => fk == txstFk ? null : (bodies.TryGetValue(fk, out var b) ? b : null)),
            new SourceArm("Defining.esp", SourceArmKind.File, "file 'Defining.esp'",
                fk => bodies.TryGetValue(fk, out var b) ? b : null),
        });
        var rs = ClosureWalk.Run(seeds, split, scope, noExcl);
        Check(rs.Success && rs.Reached.Single(n => n.Key == txstFk).ArmSpelling == "Defining.esp",
            "the record only the SECOND arm has is attributed to arm 1…");
        Check(rs.Success && rs.Reached.Where(n => n.Key != txstFk).All(n => n.ArmSpelling == "Override.esp"),
            "…while every other node is attributed to arm 0 — per NODE, never flattened to one walk-level answer");

        // ---- 3. CYCLES: recorded, and told apart from a diamond --------------------------------------------
        // The fixture carries BOTH shapes at once, so an implementation that reports every repeat link would
        // fail on the diamond and one that reports none would fail on the cycle. A presence check ("some cycle
        // was reported") could not tell those apart, which is why this asserts the CONTENT.
        Check(r.Cycles.Count == 1, $"exactly ONE cycle reported — neither diamond on {txstFk.ID:X} is one ({r.Cycles.Count})");
        Check(r.Cycles.Count == 1 && r.Cycles[0].Back == armoFk,
            "the reported cycle names the key that was pointed back at (Armo)");
        Check(r.Cycles.Count == 1 && r.Cycles[0].Path.Contains(armaFk) && r.Cycles[0].Path.Contains(armoFk),
            "…and its PATH carries the records that form the loop");
        Check(r.Reached.Count(n => n.Key == txstFk) == 1,
            "the diamond's shared record is reached ONCE (dedupe still works — a cycle report is not a re-walk)");

        // ---- 4. EXCLUSIONS AS DATA -------------------------------------------------------------------------
        var stopExcl = new[] { new WalkExclusion("HeadPart", ExclusionSeverity.Stop, "pruned for the test") };
        var rStop = ClosureWalk.Run(seeds, OneArm(), scope, stopExcl);
        Check(rStop.Success, "a STOP exclusion does not fail the walk");
        Check(rStop.Reached.All(n => n.TypeName != "HeadPart"), "…the excluded type is not expanded…");
        Check(rStop.Kept.Any(b => b.Key == hpAFk && b.Why.Contains("excluded")), "…and it is recorded as a named boundary");
        Check(!rStop.Reached.Any(n => n.Key == txstFk) || rStop.Reached.Any(n => n.Key == armoFk),
            "…and pruning stops the subtree BELOW it, while other seeds keep walking");

        var refuseExcl = new[] { new WalkExclusion("Armor", ExclusionSeverity.Refuse, "an Armor is not internalizable here") };
        var rRefuse = ClosureWalk.Run(seeds, OneArm(), scope, refuseExcl);
        Check(!rRefuse.Success && rRefuse.Refusal?.Kind == WalkRefusalKind.Excluded, "a REFUSE exclusion fails the whole walk");
        Check(rRefuse.Refusal?.Exclusion?.Reason == "an Armor is not internalizable here",
            "…carrying the CALLER's own reason, not a sentence invented by the walk");
        Check(rRefuse.Reached.Count == 0 && rRefuse.Kept.Count == 0,
            "…and yields NOTHING usable (the ACT posture — a partial closure is a broken artifact)");
        Check(!ClosureWalk.Run(seeds, OneArm(), scope, refuseExcl).Success
              && ClosureWalk.Run(seeds, OneArm(), scope, noExcl).Success,
            "the walk itself special-cases NO record type — the same graph passes without the exclusion datum");

        // ---- 5. CAP BREACH — the ACT posture, asserted by CHAIN CONTENT ------------------------------------
        var rCap = ClosureWalk.Run(seeds, OneArm(), scope, noExcl, nodeCap: 2);
        Check(!rCap.Success && rCap.Refusal?.Kind == WalkRefusalKind.NodeCap, "a node-cap breach REFUSES");
        Check(rCap.Refusal?.Cap == 2, "…naming the cap that was breached");
        Check(rCap.Refusal?.Key != default && rCap.Refusal?.PulledBy.Length > 0,
            "…naming the LAST PULL (the record it was reaching for, and what pulled it)");
        Check(rCap.Refusal?.Chain.Count >= 2 && rCap.Refusal?.Chain[^1] == rCap.Refusal?.Key,
            $"…and its FULL CHAIN, ending at that record ({rCap.Refusal?.Chain.Count} long) — not just that a cap fired");
        Check(rCap.Reached.Count == 0 && rCap.Kept.Count == 0 && rCap.Cycles.Count == 0,
            "…and NOTHING usable comes back (a write must not truncate silently)");

        // depthCap 1 breaches at hpC (depth 2) — the fixture has to BE deep enough for this arm to mean anything.
        var rDepth = ClosureWalk.Run(seeds, OneArm(), scope, noExcl, depthCap: 1);
        Check(!rDepth.Success && rDepth.Refusal?.Kind == WalkRefusalKind.DepthCap && rDepth.Refusal?.Cap == 1,
            "a depth-cap breach refuses as its own named kind");
        Check(rDepth.Refusal?.Chain.Count >= 2, "…with its chain too");

        // ---- 6. SOURCE FAILURES surface as walk refusals, distinctly ---------------------------------------
        var emptyArm = SourceChain.Single(new SourceArm("Empty.esp", SourceArmKind.File, "file 'Empty.esp'", _ => null));
        var rMiss = ClosureWalk.Run(seeds, emptyArm, scope, noExcl);
        Check(!rMiss.Success && rMiss.Refusal?.Kind == WalkRefusalKind.SourceMiss, "a record no source has REFUSES as a miss");
        Check(rMiss.Refusal?.Miss?.Consulted.Count == 1, "…carrying every source consulted, for the render to name");

        var faultArm = SourceChain.Single(new SourceArm("Bad.esp", SourceArmKind.File, "file 'Bad.esp'",
            _ => throw new InvalidOperationException("a record Mutagen cannot parse")));
        var rFault = ClosureWalk.Run(seeds, faultArm, scope, noExcl);
        Check(!rFault.Success && rFault.Refusal?.Kind == WalkRefusalKind.SourceFault,
            "an UNREADABLE record refuses as a FAULT, a different kind from a miss (different remedies)");
        Check(rFault.Refusal?.Fault?.Arm.Spelling == "Bad.esp", "…naming which source could not read it");

        // ---- 7. NO SEEDS -----------------------------------------------------------------------------------
        var rNone = ClosureWalk.Run(Array.Empty<WalkSeed>(), OneArm(), scope, noExcl);
        Check(!rNone.Success && rNone.Refusal?.Kind == WalkRefusalKind.NoSeeds,
            "a walk with NO seed links refuses — it must never 'succeed' having copied nothing");

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILURE(S)");
        return fail == 0 ? 0 : 1;
    }
}

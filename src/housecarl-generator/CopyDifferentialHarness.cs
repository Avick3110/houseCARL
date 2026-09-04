using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// The acceptance differential: <c>housecarl_copy_npc_appearance</c> (the ancestor) against its 2.0 successor
/// (<c>housecarl_copy</c> + <c>housecarl_apply</c>'s copy zip + <c>housecarl_bulk_place_asset</c>), driven over
/// CONSTRUCTED MO2 instances, with the artifacts on disk compared.
///
/// <para><b>It compares two TOOL CALLS, not two service methods.</b> The claim under test is that the successor
/// reproduces the ancestor's behavior, and "behavior" is what a caller gets from the surface — so both sides go
/// through the tool layer and the comparison is over the plugin bytes and the placed files, never over an outcome
/// object either side happens to return.</para>
///
/// <para><b>The projection is allocation-order-independent, deliberately and only that far.</b> FormID allocation
/// order is inventoried as not-a-behavior, so each patch is projected to text keyed by (record type, EditorID) with
/// every patch-local FormKey rewritten to its own <c>&lt;local Type:EditorID&gt;</c> label. Everything else — field
/// values, link topology after remap, the masters list — is compared verbatim. A difference this projection hides
/// would be a difference the inventory says is not one; a difference it shows is either matched or an inventoried
/// intentional divergence, and anything else is a defect in the successor.</para>
///
/// <para><b>Fixture 3 must fail informatively</b>, so its contested-ness is CONSTRUCTED and ASSERTED here rather
/// than inferred from a tool: two providers for one facegen path (a loose replacer out-sorting a real BSA), asserted
/// to hold different bytes, with the loose copy asserted to win. Only once that is measured does a clean diff there
/// stop the run — and it stops it rather than being reported as a pass.</para>
///
/// <para><b>What that stop means, stated honestly.</b> This harness passes <c>source_provider</c> to
/// <c>bulk_place_asset</c> ITSELF, standing in for the skill, and it does not change <c>place_asset</c>. So the arm
/// is a FLOW-level claim — "the composed replacement reads the named donor where the ancestor read the winner" —
/// not proof that a named-donor read was wired here. A clean diff is a reason to find out which of the three moved
/// (the fixture, the flow, or place_asset's source selection); it does not by itself identify one.</para>
///
/// <para><b>Fixture 2 compares assets too.</b> Its contested path and its donor-only paths are both MEASURED before
/// anything is compared, so a match cannot be a coincidence and a divergence cannot be a fixture accident.</para>
///
/// Run: dotnet run --project src/housecarl-generator -- copy-differential
/// </summary>
public static class CopyDifferentialHarness
{
    const string SeedHead = "HeadParts", SeedHair = "HairColor", SeedTex = "HeadTexture", SeedWorn = "WornArmor";
    static readonly string[] Seeds = { SeedHead, SeedHair, SeedTex, SeedWorn };
    static readonly WalkExclusion[] NoRace =
        { new("Race", ExclusionSeverity.Refuse, "a race is not an appearance subtree") };
    static readonly string[] InlineBundle =
        { "FaceMorph", "FaceParts", "TintLayers", "TextureLighting", "Weight", "Height" };

    static int _fail, _stop;
    static void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) _fail++; }
    static void Stop(string why)
    {
        Console.WriteLine();
        Console.WriteLine("  ####  STOP AND ASK  ####  " + why);
        Console.WriteLine("  This is a failure of the RUN, not a diff to fold. Surface it before the PR opens.");
        _stop++; _fail++;
    }

    public static int Run(string[] args)
    {
        Console.WriteLine("################  PR 3b ACCEPTANCE DIFFERENTIAL  ################");
        Console.WriteLine("old: housecarl_copy_npc_appearance   new: housecarl_copy (+ apply zip, + bulk_place_asset)");
        Console.WriteLine();

        var root = Path.Combine(Path.GetTempPath(), "hc-copy-differential-" + Guid.NewGuid().ToString("N"));
        try
        {
            Fixture1(Path.Combine(root, "f1"));
            Fixture2(Path.Combine(root, "f2"));
            Fixture3(Path.Combine(root, "f3"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL  the harness threw: " + ex);
            _fail++;
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine("================================================================");
        if (_stop > 0) Console.WriteLine($" DIFFERENTIAL STOPPED — {_stop} stop-rule breach(es). Do not fold; surface.");
        else Console.WriteLine(_fail == 0 ? " DIFFERENTIAL CLEAN — every diff line matched or is an inventoried divergence."
                                          : $" {_fail} FAILURE(S)");
        return _fail == 0 ? 0 : 1;
    }

    // ==================================================================================================
    //  FIXTURE 1 — a donor-defined NPC in an ACTIVE mod. The baseline: expect no diff beyond allocation.
    //  Runs BOTH lanes, because they are different operations in the successor and the same verb in the
    //  ancestor: the clone lane is copy-alone, and the attach lane is copy + the apply zip carrying the
    //  inline fields the walk has no reason to touch.
    // ==================================================================================================
    static void Fixture1(string root)
    {
        Console.WriteLine("---- FIXTURE 1 — donor-defined NPC in an ACTIVE mod (baseline) ----------------------");
        var inst = BuildInstance(root, donorEnabled: true, out var donorKey, out var targetKey, out _, out _);
        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
        var mods = Path.Combine(inst, "mods");

        // ---- clone lane -----------------------------------------------------------------------------
        var oldClone = NpcCopyTools.CopyNpcAppearance(svc, donorKey.ToString(), null, null, null, "TheClone", null, "F1OldClone", null);
        var newClone = CopyTools.Copy(svc, donorKey.ToString(), null, Seeds, new[] { "Race:refuse" }, null, "TheClone", "F1NewClone", null);
        Check(!oldClone.StartsWith("error"), $"clone: the ANCESTOR succeeds ({First(oldClone)})");
        Check(!newClone.StartsWith("error"), $"clone: the SUCCESSOR succeeds ({First(newClone)})");
        // INVENTORY I1 — the readback the appearance skill's Step 3 reads its candidate paths out of.
        Check(newClone.Contains("asset paths the copied records reference", StringComparison.Ordinal),
            "the successor REPORTS the asset paths its copied records reference (I1)");
        Check(newClone.Contains("donorhair.nif", StringComparison.OrdinalIgnoreCase),
            "…naming the actual path, harvested from the in-patch duplicates");
        Check(newClone.Contains("does NOT place them", StringComparison.Ordinal),
            "…and says plainly that it did not place them — the decision leaves the tool");
        Check(newClone.Contains("the source record was read from", StringComparison.Ordinal),
            "…and the record the caller ASKED for names the arm that produced it (R2)");
        DiffPatches("clone lane", FindPatch(mods, "F1OldClone"), FindPatch(mods, "F1NewClone"));

        // The skill's Step 1 output, printed in full ONCE: a skill step that describes a tool's output must be RUN
        // before it ships, or its claims are text nobody executed. Printing it here makes them checkable.
        Console.WriteLine("  ---- the successor's Step-1 readback, verbatim ----");
        foreach (var line in newClone.Split('\n')) Console.WriteLine("  | " + line.TrimEnd());
        Console.WriteLine("  ---------------------------------------------------");

        // ---- attach lane ----------------------------------------------------------------------------
        // Donor and target deliberately differ in RACE and SEX, so the two conditional bundle members the
        // inventory splits out (a Race copy, and the single Female BIT rather than the whole flags field) are
        // exercised rather than being no-ops that pass for free.
        var oldAttach = NpcCopyTools.CopyNpcAppearance(svc, donorKey.ToString(), null, null, targetKey.ToString(), null, null, "F1OldAttach", null);
        Check(!oldAttach.StartsWith("error"), $"attach: the ANCESTOR succeeds ({First(oldAttach)})");

        var newAttachCopy = CopyTools.Copy(svc, donorKey.ToString(), null, Seeds, new[] { "Race:refuse" }, targetKey.ToString(), null, "F1NewAttach", null);
        Check(!newAttachCopy.StartsWith("error"), $"attach: the SUCCESSOR's copy step succeeds ({First(newAttachCopy)})");
        var patchFile = Path.GetFileName(FindPatch(mods, "F1NewAttach"));

        // THE BUNDLE IS PARTITIONED, and this is the skill's flow rather than a harness convenience. The ancestor
        // assigns each bundle member unconditionally, so a donor field that is UNSET clears the target's copy of it.
        // apply's CopyFrom refuses an unset source instead ("nothing to copy — use Remove to clear the target"), so
        // reproducing the ancestor means: copy what the donor HAS, and REMOVE what it lacks. Skipping the absent
        // members would leave the target's own morphs sitting under the donor's head parts, which is the desync the
        // whole operation exists to avoid.
        var present = InlineBundle.Where(f => DonorHas(mods, donorKey, f)).ToList();
        var absent = InlineBundle.Where(f => !present.Contains(f)).ToList();
        present.Add("Race");                                     // races differ → the conditional member is IN
        Console.WriteLine($"  bundle partition — copy: [{string.Join(", ", present)}]  clear: [{string.Join(", ", absent)}]");
        var assignments = $"[{{\"target\":\"{targetKey}\",\"from\":\"{donorKey}\"}}]";
        var ops = new List<string>
        {
            $"{{\"formid\":\"{targetKey}\",\"field_path\":\"Configuration.Flags\",\"op\":\"Add\",\"value\":\"Female\"}}",
        };
        ops.AddRange(absent.Select(f => $"{{\"formid\":\"{targetKey}\",\"field_path\":\"{f}\",\"op\":\"Remove\"}}"));
        var newBundle = ApplyTools.Apply(svc, Json("[" + string.Join(",", ops) + "]"), present.ToArray(), Json(assignments), null, patchFile);
        Check(!newBundle.StartsWith("error"), $"attach: the SUCCESSOR's bundle step succeeds ({First(newBundle)})");
        DiffPatches("attach lane", FindPatch(mods, "F1OldAttach"), FindPatch(mods, "F1NewAttach"));

        // ---- the seed-shape boundary REFUSES rather than going quiet ------------------
        // The ancestor has a fixed seed set, so there is no old-vs-new pair to diff here; what the run asserts is
        // that the successor's answer to an unsupported shape is a REFUSAL that names the route, not a silent
        // no-op and not a wiped list.
        var shape = CopyTools.Copy(svc, donorKey.ToString(), null, new[] { "HeadParts", "Factions" },
                                   new[] { "Race:refuse" }, targetKey.ToString(), null, "F1Shape", null);
        Check(shape.StartsWith("error") && shape.Contains("RankPlacement", StringComparison.Ordinal),
            "an unsupported list seed REFUSES by shape, naming what its entries ARE, rather than emptying the target's list");
        Check(shape.Contains("housecarl_apply", StringComparison.Ordinal),
            "…naming the lane that does support it");
        Check(!Directory.EnumerateDirectories(mods, "*F1Shape*").Any(),
            "…and writes nothing, leaving no folder behind");
        Console.WriteLine();
    }

    // ==================================================================================================
    //  FIXTURE 2 — a DISABLED donor: the off-order read, and the ordered source list read through an override,
    //  plus the asset half below.
    // ==================================================================================================
    static void Fixture2(string root)
    {
        Console.WriteLine("---- FIXTURE 2 — DISABLED donor (records AND assets — EL-2) -------------------------");
        var inst = BuildInstance(root, donorEnabled: false, withAssets: true,
                                 out var donorKey, out _, out _, out var overrideName);
        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
        var mods = Path.Combine(inst, "mods");

        var oldOut = NpcCopyTools.CopyNpcAppearance(svc, donorKey.ToString(), "Donor.esp", null, null, "TheClone", null, "F2Old", null);
        // The ordered source list is what replaces the ancestor's implicit auto-widen: the override first, the
        // plugin that DEFINES the record second — and the second name is the plugin half of `from=`, never a
        // discovery step.
        var newOut = CopyTools.Copy(svc, donorKey.ToString(), new[] { overrideName, "Donor.esp" },
                                    Seeds, new[] { "Race:refuse" }, null, "TheClone", "F2New", null);
        Check(!oldOut.StartsWith("error"), $"the ANCESTOR reads the disabled donor ({First(oldOut)})");
        Check(!newOut.StartsWith("error"), $"the SUCCESSOR reads it through the ordered source list ({First(newOut)})");
        var oldPatch = FindPatch(mods, "F2Old");
        var newPatch = FindPatch(mods, "F2New");
        DiffPatches("disabled donor", oldPatch, newPatch);

        // ================================================================================================
        //  THE ASSET HALF. It runs only while the ancestor still exists to be compared against.
        // ================================================================================================
        Console.WriteLine();
        Console.WriteLine("  -- EL-2: the deferred asset comparison, per destination path --");

        // The fixture's PREMISE, measured rather than assumed. If the donor's files were visible to the
        // active order, or the contested path were not contested, every line below would be true for the
        // wrong reason — the same discipline fixture 3 applies to its own contest.
        var meshRel = FaceGenPath.For(donorKey, FaceGenSlot.Mesh);
        var tintRel = FaceGenPath.For(donorKey, FaceGenSlot.Tint);
        var status = svc.AssetStatus(new[] { meshRel, tintRel, HarvestedRel }).Results;
        var meshHit = status[0].Hit;
        Check(meshHit is { Exists: true } && meshHit.Winner?.Source == "ReplacerMod",
            $"the FaceGen MESH path is won by an ENABLED replacer, not the donor — winner={meshHit?.Winner?.Source ?? "(none)"}");
        Check(status[1].Hit is { Exists: false } && status[2].Hit is { Exists: false },
            $"…while the tint and the head-part model exist ONLY in the switched-off donor (tint={status[1].Hit?.Exists}, hair={status[2].Hit?.Exists})");
        if (meshHit?.Winner?.Source != "ReplacerMod" || status[1].Hit is not { Exists: false } || status[2].Hit is not { Exists: false })
        {
            Stop("the disabled-donor asset fixture is not what it claims — one contested path and two the donor " +
                 "alone provides. Without both shapes the comparison cannot tell a match from a coincidence.");
            return;
        }

        var oldKey = NewCloneKey(oldPatch, "TheClone");
        var newKey = NewCloneKey(newPatch, "TheClone");
        Check(oldKey is not null && newKey is not null, "both clones' FormKeys are readable off their patches");
        if (oldKey is null || newKey is null) return;

        // The ancestor carries assets ITSELF; the successor's carry is a separate call, which this harness
        // makes standing in for the skill — the same stand-in fixture 3 declares, and the reason both are
        // FLOW-level claims. What F1 changed is what that call can now reach.
        var placed = PlaceAssetTools.BulkPlaceAsset(svc, new[]
        {
            new PlaceAssetSpec { Formid = newKey.Value.ToString(), Kind = "mesh", Source = meshRel, SourceProvider = "DonorMod" },
            new PlaceAssetSpec { Formid = newKey.Value.ToString(), Kind = "tint", Source = tintRel, SourceProvider = "DonorMod" },
            new PlaceAssetSpec { AssetPath = HarvestedRel, Source = HarvestedRel, SourceProvider = "DonorMod" },
        }, null, Path.GetFileName(newPatch!));
        Check(!placed.StartsWith("error") && !placed.Contains("FAIL", StringComparison.Ordinal),
            $"the successor's carry places all three from the switched-off donor — {First(placed)}");
        Check(placed.Contains("NOT enabled in MO2", StringComparison.Ordinal),
            "…and SAYS the bytes came from a mod MO2 does not load (Q3 — the ancestor says it about the record, this says it about the files)");

        var oldFolder = FindPatchFolder(mods, "F2Old");
        var newFolder = FindPatchFolder(mods, "F2New");
        // CarriedFiles drops meta.ini as MO2 bookkeeping that names its own folder. Checked, not assumed: a side
        // that stopped writing one would otherwise be invisible behind the exclusion.
        Check(oldFolder is not null && newFolder is not null
              && File.Exists(Path.Combine(oldFolder, "meta.ini")) && File.Exists(Path.Combine(newFolder, "meta.ini")),
            "both patch folders carry the MO2 meta.ini the comparison excludes");

        var oldFiles = CarriedFiles(oldFolder, oldKey.Value);
        var newFiles = CarriedFiles(newFolder, newKey.Value);
        Check(oldFiles.Count == 3 && newFiles.Count == 3,
            $"each side carried exactly the three files this fixture puts in the donor (ancestor={oldFiles.Count}, successor={newFiles.Count})");

        foreach (var key in oldFiles.Keys.Concat(newFiles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            oldFiles.TryGetValue(key, out var ob);
            newFiles.TryGetValue(key, out var nb);
            var same = ob is not null && nb is not null && ob.SequenceEqual(nb);
            Console.WriteLine($"    {key,-24}  ancestor={(ob is null ? "(none)" : Show(ob)),-40}  successor={(nb is null ? "(none)" : Show(nb))}");

            if (key == "<clone facegeom>")
            {
                // THE INVENTORIED DIVERGENCE: on a contested path the ancestor reads the VFS winner and the
                // successor reads the mod the caller named. Its ABSENCE is the failure here — a clean diff would
                // mean one of the two stopped doing what it is documented to do.
                Check(ob is not null && ob.SequenceEqual(ReplacerMeshBytes),
                    "  the ANCESTOR carried the VFS WINNER's mesh (the replacer's) — the recorded behaviour");
                Check(nb is not null && nb.SequenceEqual(DonorMeshBytes),
                    "  the SUCCESSOR carried the NAMED donor's mesh — the recorded divergence, inventoried as intentional (§12)");
                if (same) Stop("the contested FaceGen mesh carried IDENTICAL bytes on both sides. The fixture was " +
                               "measured contested above, so one of the two lanes has moved — which is the thing to go find out.");
                continue;
            }
            // Everything else is an EQUALITY claim: the donor alone provides it, so both lanes must reach the
            // same bytes, and a divergence here is a successor defect rather than a recorded difference.
            Check(same, $"  '{key}' carries IDENTICAL bytes on both sides (donor-only path — no divergence is inventoried here)");
            if (!same)
                Stop($"'{key}' diverges on a path only the switched-off donor provides, and the inventory records no " +
                     "divergence there. That is a defect in the successor, not a diff to fold.");
        }

        Console.WriteLine("  SCOPE  the comparison covers the donor folder's LOOSE copies. The root-ARCHIVE half of the");
        Console.WriteLine("         same lane is guarded per-side instead — place-asset-guard M3 (archive-only hit) and");
        Console.WriteLine("         M14 (loose beats the folder's archive) — because this fixture's records have no link");
        Console.WriteLine("         to a path the committed archive actually contains, and inventing one would move what");
        Console.WriteLine("         fixture 1 measures off the same tree.");
        Console.WriteLine();
    }

    // ==================================================================================================
    //  FIXTURE 3 — a contested vanilla facegen path. THE fixture, and the only one with a stop rule.
    // ==================================================================================================
    static void Fixture3(string root)
    {
        Console.WriteLine("---- FIXTURE 3 — CONTESTED facegen path (vanilla donor; the recorded divergence) -----");

        var fixA = Path.GetFullPath(Path.Combine("src", "housecarl-generator", "fixtures", "asset-resolver", "FixtureA.bsa"));
        if (!File.Exists(fixA))
        {
            Stop($"the committed BSA fixture is missing at {fixA} — a true BSA-vs-loose contest cannot be built, " +
                 "and weaker evidence is not a substitute. (Run from the repo root.)");
            return;
        }

        // The BSA fixture carries exactly one facegen entry, for Dawnguard.esm's 0001A51A. The instance is built
        // around that entry rather than the entry around the instance: a synthetic BSA written here would be a
        // BSA we also assert about, and the point of using a real archive is that the read is Mutagen's.
        const string faceRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";
        var inst = Path.Combine(root, "inst");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        WriteMo2Ini(inst);

        var dgKey = new ModKey("Dawnguard", ModType.Master);
        var npcFk = new FormKey(dgKey, 0x1A51A);
        var hpFk = new FormKey(dgKey, 0x1A51B);
        var dg = new SkyrimMod(dgKey, SkyrimRelease.SkyrimSE);
        var race = new Race(new FormKey(dgKey, 0x1A519), SkyrimRelease.SkyrimSE) { EditorID = "DgRace" };
        dg.Races.Add(race);
        dg.HeadParts.Add(new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "DgHair" });
        var npc = new Npc(npcFk, SkyrimRelease.SkyrimSE) { EditorID = "DgNpc" };
        npc.Race.SetTo(race.FormKey);
        npc.HeadParts.Add(hpFk);
        dg.Npcs.Add(npc);
        var baseDir = Path.Combine(mods, "BaseGame");
        WriteMod(mods, "BaseGame", dg);
        // The archive is bound to the plugin by NAME, which is the engine's own rule — so the fixture archive
        // becomes Dawnguard.bsa beside Dawnguard.esm and is discovered exactly as a real one would be.
        File.Copy(fixA, Path.Combine(baseDir, "Dawnguard.bsa"));

        // The replacer: a LOOSE copy of the same path, in a higher-priority mod, with DIFFERENT bytes.
        var repDir = Path.Combine(mods, "Replacer");
        var loosePath = Path.Combine(repDir, faceRel);
        Directory.CreateDirectory(Path.GetDirectoryName(loosePath)!);
        var looseBytes = Encoding.ASCII.GetBytes("REPLACER-FACEGEN-BYTES-NOT-THE-DONORS");
        File.WriteAllBytes(loosePath, looseBytes);

        // modlist.txt is highest-priority FIRST, so Replacer out-sorts BaseGame.
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+Replacer\r\n+BaseGame\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "Dawnguard.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*Dawnguard.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));

        // ---- the contest, CONSTRUCTED and ASSERTED here (not inferred from a tool) -------------------
        var bsaPath = Path.Combine(baseDir, "Dawnguard.bsa");
        var status = svc.AssetStatus(new[] { faceRel });
        var hit = status.Results.Count > 0 ? status.Results[0].Hit : null;
        var providers = hit?.Providers ?? (IReadOnlyList<AssetProvider>)Array.Empty<AssetProvider>();
        Check(providers.Count >= 2,
            $"the facegen path has MORE THAN ONE active provider — {providers.Count} ({string.Join(", ", providers.Select(p => $"{p.Source}/{p.Kind}"))})");
        var bsaProv = providers.FirstOrDefault(p => p.Kind == AssetKind.Bsa);
        var looseProv = providers.FirstOrDefault(p => p.Kind == AssetKind.Loose);
        Check(bsaProv is not null && looseProv is not null, "…one from a real BSA and one loose — a TRUE BSA-vs-loose contest");
        Check(hit?.Winner is { Kind: AssetKind.Loose }, $"…and the LOOSE copy out-sorts the BSA (winner: {hit?.Winner?.Source ?? "(none)"}/{hit?.Winner?.Kind})");
        if (providers.Count < 2 || bsaProv is null || looseProv is null || hit?.Winner is not { Kind: AssetKind.Loose })
        {
            Stop("the harness could not reproduce a TRUE VFS contest (BSA-vs-loose) on a facegen path. " +
                 "The tripwire says that is a stop-and-surface, not a fallback to weaker evidence.");
            return;
        }
        // The two providers must actually hold different bytes, or "different bytes at the destination" would be
        // untestable no matter which one each tool read.
        var bsaBytes = AssetResolver.TryReadArchiveEntry(bsaPath, faceRel);
        Check(bsaBytes is not null && !bsaBytes.SequenceEqual(looseBytes),
            $"…and the two providers hold DIFFERENT bytes (bsa={(bsaBytes is null ? "unreadable" : bsaBytes.Length + "B")}, loose={looseBytes.Length}B)");
        if (bsaBytes is null || bsaBytes.SequenceEqual(looseBytes))
        {
            Stop("the two providers do not hold different bytes, so a byte diff at the destination could not " +
                 "discriminate which provider either tool read.");
            return;
        }

        // ---- both tools, clone lane -----------------------------------------------------------------
        var oldOut = NpcCopyTools.CopyNpcAppearance(svc, npcFk.ToString(), null, null, null, "TheClone", null, "F3Old", null);
        Check(!oldOut.StartsWith("error"), $"the ANCESTOR clones the vanilla donor and carries its facegen ({First(oldOut)})");

        var newOut = CopyTools.Copy(svc, npcFk.ToString(), null, Seeds, new[] { "Race:refuse" }, null, "TheClone", "F3New", null);
        Check(!newOut.StartsWith("error"), $"the SUCCESSOR clones it ({First(newOut)})");
        var newPatch = FindPatch(mods, "F3New");
        var newKey = NewCloneKey(newPatch, "TheClone");
        Check(newKey is not null, "…and the clone's new FormKey is readable off the written patch");
        if (newKey is null) return;

        // The successor names the DONOR's provider. That is the whole divergence: the ancestor reads the VFS
        // winner, and on a contested path the winner's bytes are not the ones the copied records were baked from.
        var placed = PlaceAssetTools.BulkPlaceAsset(svc, new[]
        {
            new PlaceAssetSpec { Formid = newKey.Value.ToString(), Kind = "mesh", AssetPath = null,
                                 Source = faceRel, SourceProvider = bsaProv.Source },
        }, null, Path.GetFileName(newPatch));
        Check(!placed.StartsWith("error"), $"…and places the facegen from the NAMED donor ({First(placed)})");

        // ---- records: a vanilla donor is a transplant in BOTH tools, so the patches should still match ----
        DiffPatches("contested vanilla (records)", FindPatch(mods, "F3Old"), newPatch);

        // ---- the RESPONSE half, which Project() is blind to by construction --------------
        // Project() compares plugin bytes and the masters list and never the two tools' prose, so a render-level
        // divergence — a base-master donor wrongly called a standalone-ization — is invisible to it while every
        // fixture reports NO DIFF. This donor lives in Dawnguard.esm, so the two renders are compared directly.
        //
        // Still the transplant case: this call passes no from_source=, so the universe is ['winner'], and 'winner'
        // is exempt from binding — the whole load order is not a plugin to copy away from. Name a MOD here and the
        // bound set stops being empty and the standalone claim becomes the right one, which copy-service-guard
        // covers rather than this harness.
        Check(oldOut.Contains("appearance transplant", StringComparison.Ordinal),
            "the ANCESTOR calls a base-master donor an appearance transplant rather than a standalone-ization");
        Check(newOut.Contains("appearance transplant", StringComparison.Ordinal),
            "…and so does the SUCCESSOR — the render arm the inventory codes F24, which shipped missing");
        Check(!newOut.Contains("the source is NOT a master", StringComparison.Ordinal),
            "…so neither response claims standalone from a set base masters are deliberately excluded from");

        // ---- the divergence itself -------------------------------------------------------------------
        var oldFace = FindPlacedFacegen(FindPatchFolder(mods, "F3Old"));
        var newFace = FindPlacedFacegen(FindPatchFolder(mods, "F3New"));
        Check(oldFace is not null, "the ANCESTOR placed a facegen mesh for the new key");
        Check(newFace is not null, "the SUCCESSOR placed a facegen mesh for the new key");
        if (oldFace is null || newFace is null)
        {
            Stop("one side placed no facegen at all, so there is nothing to diff on the contested path.");
            return;
        }

        var ob = File.ReadAllBytes(oldFace);
        var nb = File.ReadAllBytes(newFace);
        Console.WriteLine($"  ancestor carried  {ob.Length,4}B  == replacer(loose)? {ob.SequenceEqual(looseBytes)}");
        Console.WriteLine($"  successor carried {nb.Length,4}B  == donor(bsa)?      {nb.SequenceEqual(bsaBytes)}");
        if (ob.SequenceEqual(nb))
        {
            Stop("the carried bytes on the CONTESTED facegen path are IDENTICAL. The fixture was measured to be " +
                 "genuinely contested above, so one of the flow, the fixture, or place_asset's source selection " +
                 "has moved — which of the three is what to go and find out.");
            return;
        }
        Check(true, "THE RECORDED DIVERGENCE: the two tools carry DIFFERENT bytes on the contested facegen path");
        Check(ob.SequenceEqual(looseBytes), "…the ANCESTOR carried the VFS WINNER's bytes (the replacer's)");
        Check(nb.SequenceEqual(bsaBytes), "…the SUCCESSOR carried the NAMED DONOR's bytes (the base archive's)");
        Console.WriteLine("  This divergence is inventoried as intentional (the successor reads the named donor, not");
        Console.WriteLine("  the winner). Its appearing is the test working. Aaron's live-install pass is the second");
        Console.WriteLine("  half of this fixture's evidence and is NOT run here.");
        Console.WriteLine();
    }

    // ==================================================================================================
    //  The comparison
    // ==================================================================================================

    /// <summary>Project both patches and print every differing line. A projection difference is reported, never
    /// summarised away — the judgement rule needs the line to classify it.</summary>
    static void DiffPatches(string label, string? oldPath, string? newPath)
    {
        if (oldPath is null || newPath is null)
        {
            Check(false, $"{label}: both patches were written (old={oldPath ?? "MISSING"}, new={newPath ?? "MISSING"})");
            return;
        }
        var a = Project(oldPath);
        var b = Project(newPath);
        // MULTISET, not set. Except() collapses duplicates, so a patch carrying the SAME projected line twice —
        // what a double-internalize produces — would diff clean against one carrying it once. The count is the
        // half that catches a stray duplicate.
        var only = Excess(a, b);
        var extra = Excess(b, a);
        if (only.Count == 0 && extra.Count == 0)
        {
            Check(true, $"{label}: NO DIFF beyond FormID allocation — {a.Count} projected line(s) identical");
            return;
        }
        Check(false, $"{label}: {only.Count} line(s) only in the ANCESTOR, {extra.Count} only in the SUCCESSOR");
        foreach (var l in only.Take(40)) Console.WriteLine("    - " + l);
        foreach (var l in extra.Take(40)) Console.WriteLine("    + " + l);
        if (only.Count > 40 || extra.Count > 40) Console.WriteLine($"    … ({only.Count + extra.Count} total; truncated at 80)");
    }

    /// <summary>The lines <paramref name="a"/> carries MORE times than <paramref name="b"/> does, each repeated by
    /// how many times over — set difference cannot see a duplicate, which is the one thing a copy can emit by
    /// accident.</summary>
    static List<string> Excess(List<string> a, List<string> b)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var l in b) counts[l] = counts.TryGetValue(l, out var n) ? n + 1 : 1;
        var excess = new List<string>();
        foreach (var l in a)
        {
            if (counts.TryGetValue(l, out var n) && n > 0) counts[l] = n - 1;
            else excess.Add(l);
        }
        return excess;
    }

    /// <summary>One patch as allocation-order-independent text.</summary>
    static List<string> Project(string patchPath)
    {
        var lines = new List<string>();
        var mod = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        try
        {
            var recs = mod.EnumerateMajorRecords().ToList();
            var label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in recs)
            {
                var t = RecordNaming.StripOverlay(r.GetType().Name);
                label[r.FormKey.ToString()] = $"<local {t}:{r.EditorID ?? "<no-editorid>"}>";
            }
            // The patch's own filename appears inside FormKey renderings, so it is neutralised too — the two runs
            // deliberately write to different patch names and that is not a behavior either.
            var patchName = Path.GetFileName(patchPath);

            foreach (var m in mod.ModHeader.MasterReferences.Select(x => x.Master.FileName.ToString()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                lines.Add("MASTER " + m);

            foreach (var r in recs.OrderBy(r => RecordNaming.StripOverlay(r.GetType().Name), StringComparer.Ordinal)
                                  .ThenBy(r => r.EditorID ?? "", StringComparer.Ordinal))
            {
                var t = RecordNaming.StripOverlay(r.GetType().Name);
                var key = $"{t}:{r.EditorID ?? "<no-editorid>"}";
                lines.Add("REC " + key);
                RecordFields rf;
                try { rf = ReadEngine.ReadFields(r, null, depth: 6); }
                catch (Exception ex) { lines.Add($"  {key} !! unreadable: {ex.Message}"); continue; }
                foreach (var f in rf.Fields.OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    if (!f.HasValue) continue;                       // absent optionals carry no value to compare
                    lines.Add($"  {key}.{f.Path} = {Canon(f.Token ?? "", label, patchName)}");
                }
            }
        }
        finally { (mod as IDisposable)?.Dispose(); }
        return lines;
    }

    /// <summary>Rewrite every patch-local FormKey to its identity label. Longest-first so a substring of one key
    /// cannot eat another.</summary>
    static string Canon(string token, IReadOnlyDictionary<string, string> label, string patchName)
    {
        foreach (var kv in label.OrderByDescending(k => k.Key.Length))
            token = token.Replace(kv.Key, kv.Value, StringComparison.OrdinalIgnoreCase);
        return token.Replace(patchName, "<patch>", StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================================================
    //  Fixture construction + small readers
    // ==================================================================================================

    /// <summary>The shared fixture for 1 and 2: a base master, a donor mod (enabled or not), an override patch over
    /// the donor's NPC, and a follower mod holding the attach target.</summary>
    static string BuildInstance(string root, bool donorEnabled,
        out FormKey donorKey, out FormKey targetKey, out string donorPlugin, out string overrideName)
        => BuildInstance(root, donorEnabled, false, out donorKey, out targetKey, out donorPlugin, out overrideName);

    /// <summary>As above, and with <paramref name="withAssets"/> the donor mod also gets FILES: its FaceGen pair and
    /// its head part's model, all LOOSE in the donor's own folder — plus an ENABLED replacer supplying the FaceGen
    /// MESH path with different bytes, which is what makes the recorded winner-vs-named divergence reachable on a
    /// DISABLED donor. No archive here: see the SCOPE note at the end of <c>Fixture2</c> for why, and for where the
    /// root-archive half of the lane is guarded instead.
    /// <para>Opt-in rather than always-on because fixture 1 asserts against this same tree, and growing it would move
    /// what a dozen of its arms measure for the sake of a fixture only fixture 2 needs.</para></summary>
    static string BuildInstance(string root, bool donorEnabled, bool withAssets,
        out FormKey donorKey, out FormKey targetKey, out string donorPlugin, out string overrideName)
    {
        var inst = Path.Combine(root, "inst");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        WriteMo2Ini(inst);

        var baseKey = new ModKey("HcBase", ModType.Master);
        var raceA = new FormKey(baseKey, 0x800);
        var raceB = new FormKey(baseKey, 0x801);
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        baseMod.Races.Add(new Race(raceA, SkyrimRelease.SkyrimSE) { EditorID = "RaceA" });
        baseMod.Races.Add(new Race(raceB, SkyrimRelease.SkyrimSE) { EditorID = "RaceB" });
        // A shared, ACTIVE armor. It exists so the attach TARGET can carry a WornArmor the donor does NOT — the
        // case where the ancestor's unconditional assignment CLEARS the target's field. A successor that instead
        // left the target's value in place would build a face out of two records, and no fixture where the donor
        // and the target are unset together can see that.
        var armorFk = new FormKey(baseKey, 0x802);
        baseMod.Armors.Add(new Armor(armorFk, SkyrimRelease.SkyrimSE) { EditorID = "SharedArmor" });
        WriteMod(mods, "BaseMod", baseMod);

        var donorMk = new ModKey("Donor", ModType.Plugin);
        var texFk = new FormKey(donorMk, 0x800);
        var hairFk = new FormKey(donorMk, 0x801);
        var clfmFk = new FormKey(donorMk, 0x802);
        var headTexFk = new FormKey(donorMk, 0x803);
        var factionFk = new FormKey(donorMk, 0x804);
        var donorFk = new FormKey(donorMk, 0x805);
        var donor = new SkyrimMod(donorMk, SkyrimRelease.SkyrimSE);
        donor.TextureSets.Add(new TextureSet(texFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorHairTex" });
        var hp = new HeadPart(hairFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorHair" };
        hp.TextureSet.SetTo(texFk);
        // A real asset link, so inventory I1's readback has something to enumerate. Without one the block renders
        // empty and the skill's Step 3 would be describing output nobody had produced — which is the exact class
        // of claim this branch got caught making.
        hp.Model = new Model { File = @"actors\character\character assets\hair\donorhair.nif" };
        donor.HeadParts.Add(hp);
        donor.Colors.Add(new ColorRecord(clfmFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorHairColor" });
        donor.TextureSets.Add(new TextureSet(headTexFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorHeadTex" });
        donor.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorFaction" });
        // A realistic appearance donor: tints, a morph, the skin-lighting colour, weight and height all set —
        // and FaceParts deliberately NOT set, because real donors carry some of the bundle and not the rest, and
        // that asymmetry is exactly where the two tools can disagree.
        var dnpc = new Npc(donorFk, SkyrimRelease.SkyrimSE) { EditorID = "DonorNpc", Weight = 42f, Height = 1.05f };
        dnpc.FaceMorph = new NpcFaceMorph { NoseLongVsShort = 0.25f, BrowsInVsOut = -0.5f };
        dnpc.TintLayers.Add(new TintLayer { Index = 3, Color = System.Drawing.Color.FromArgb(200, 140, 120, 110), InterpolationValue = 0.75f });
        dnpc.TextureLighting = System.Drawing.Color.FromArgb(255, 90, 80, 70);
        dnpc.Race.SetTo(raceA);
        dnpc.HeadParts.Add(hairFk);
        dnpc.HairColor.SetTo(clfmFk);
        dnpc.HeadTexture.SetTo(headTexFk);
        dnpc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(factionFk), Rank = 0 });
        dnpc.Configuration.Flags |= NpcConfiguration.Flag.Female;
        donor.Npcs.Add(dnpc);
        WriteMod(mods, "DonorMod", donor, baseMod);

        // An override patch carrying the donor's NPC — the shape the ordered source list exists for.
        var ovMk = new ModKey("Overhaul", ModType.Plugin);
        var ov = new SkyrimMod(ovMk, SkyrimRelease.SkyrimSE);
        ov.Npcs.GetOrAddAsOverride(dnpc);
        WriteMod(mods, "OverhaulMod", ov, baseMod, donor);

        // The attach target: a DIFFERENT race and NOT female, so both conditional bundle members are live.
        var folMk = new ModKey("Follower", ModType.Plugin);
        var tgtFk = new FormKey(folMk, 0x800);
        var fol = new SkyrimMod(folMk, SkyrimRelease.SkyrimSE);
        var tgt = new Npc(tgtFk, SkyrimRelease.SkyrimSE) { EditorID = "TargetNpc", Weight = 7f, Height = 0.9f };
        // The target HAS the one bundle member the donor lacks. The ancestor deep-copies an unset donor field, which
        // CLEARS the target's — so this is the arm that catches a successor which merely skips what the donor lacks.
        tgt.FaceParts = new NpcFaceParts { Nose = 4, Eyes = 5, Mouth = 6 };
        tgt.WornArmor.SetTo(armorFk);        // the donor leaves WornArmor unset: the ancestor clears this
        tgt.Race.SetTo(raceB);
        fol.Npcs.Add(tgt);
        WriteMod(mods, "FollowerMod", fol, baseMod);

        if (withAssets) WriteDonorAssets(mods, donorFk);

        var donorLine = donorEnabled ? "+DonorMod" : "-DonorMod";
        var replacerLine = withAssets ? "+ReplacerMod\r\n" : "";
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), $"{replacerLine}+OverhaulMod\r\n+FollowerMod\r\n{donorLine}\r\n+BaseMod\r\n");
        var order = donorEnabled
            ? "HcBase.esm\r\nDonor.esp\r\nOverhaul.esp\r\nFollower.esp\r\n"
            : "HcBase.esm\r\nFollower.esp\r\n";
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), order);
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("", order.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(p => "*" + p + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        donorKey = donorFk; targetKey = tgtFk; donorPlugin = "Donor.esp"; overrideName = "Overhaul.esp";
        return inst;
    }

    // ---- the EL-2 asset fixture ---------------------------------------------------------------------
    // Data-relative destinations, and the bytes are distinct per file so a mix-up shows as a mix-up rather
    // than as a pass. None of them contains a 'textures\…dds' run: the ancestor SCRAPES the carried geom for
    // embedded texture paths, and a fixture that accidentally looked like a NIF would harvest itself.
    internal const string HarvestedRel = @"Meshes\actors\character\character assets\hair\donorhair.nif";
    static readonly byte[] DonorMeshBytes    = Encoding.ASCII.GetBytes("DONOR-FACEGEOM-0001");
    static readonly byte[] DonorTintBytes    = Encoding.ASCII.GetBytes("DONOR-FACETINT-0002");
    static readonly byte[] DonorHairBytes    = Encoding.ASCII.GetBytes("DONOR-HAIRMESH-0003");
    static readonly byte[] ReplacerMeshBytes = Encoding.ASCII.GetBytes("REPLACER-FACEGEOM-NOT-THE-DONORS");

    /// <summary>The donor's own files, in the donor's own mod folder — plus an ENABLED replacer over the FaceGen MESH
    /// path only. The asymmetry is the point: the tint and the head-part model are the donor's alone (both tools must
    /// reach the same bytes), while the mesh is contested (the ancestor reads whatever wins the VFS, the successor
    /// reads the mod the caller named), which is the divergence the inventory records as intentional.</summary>
    static void WriteDonorAssets(string mods, FormKey donorFk)
    {
        var donorDir = Path.Combine(mods, "DonorMod");
        WriteFile(donorDir, FaceGenPath.For(donorFk, FaceGenSlot.Mesh), DonorMeshBytes);
        WriteFile(donorDir, FaceGenPath.For(donorFk, FaceGenSlot.Tint), DonorTintBytes);
        WriteFile(donorDir, HarvestedRel, DonorHairBytes);
        WriteFile(Path.Combine(mods, "ReplacerMod"), FaceGenPath.For(donorFk, FaceGenSlot.Mesh), ReplacerMeshBytes);
    }

    static void WriteFile(string baseDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(baseDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>Every file a patch mod folder holds EXCEPT its plugin, keyed by destination path with the two
    /// per-side identities normalized away: the FaceGen pair's path carries the patch's own plugin name and the
    /// clone's own FormID, both of which differ between the two runs by construction and neither of which is a
    /// behaviour. Everything else is compared under its literal path.
    /// <para><c>meta.ini</c> is excluded and its exclusion is checked rather than assumed. It is MO2 bookkeeping
    /// houseCARL writes for the folder it just made, and it NAMES that folder — so the two runs differ in it for the
    /// same reason the patch filename differs, which <see cref="Project"/> already normalizes away on the record
    /// side. The caller asserts both sides HAVE one, so dropping it cannot hide a side that stopped writing
    /// it.</para></summary>
    static Dictionary<string, byte[]> CarriedFiles(string? patchFolder, FormKey cloneKey)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (patchFolder is null) return map;
        var mesh = FaceGenPath.For(cloneKey, FaceGenSlot.Mesh);
        var tint = FaceGenPath.For(cloneKey, FaceGenSlot.Tint);
        foreach (var f in Directory.EnumerateFiles(patchFolder, "*", SearchOption.AllDirectories))
        {
            if (f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(Path.GetFileName(f), "meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = f.Substring(patchFolder.Length).TrimStart('\\', '/');
            var key = string.Equals(rel, mesh, StringComparison.OrdinalIgnoreCase) ? "<clone facegeom>"
                    : string.Equals(rel, tint, StringComparison.OrdinalIgnoreCase) ? "<clone facetint>"
                    : rel;
            map[key] = File.ReadAllBytes(f);
        }
        return map;
    }

    static string Show(byte[] b) => $"{b.Length}B {Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 32))}";

    static void WriteMo2Ini(string inst) =>
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

    static void WriteMod(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }

    /// <summary>The houseCARL mod folder a patch stem landed in. The prefix is the service's own folder-per-patch
    /// naming ("houseCARL - &lt;stem&gt;"), matched here rather than assumed: a harness that guessed the folder would
    /// report MISSING for a patch that was written perfectly well.</summary>
    static string? FindPatchFolder(string mods, string stem) =>
        Directory.EnumerateDirectories(mods, "houseCARL - " + stem + "*")
                 .OrderBy(d => d, StringComparer.Ordinal).FirstOrDefault();

    static string? FindPatch(string mods, string stem)
    {
        var dir = FindPatchFolder(mods, stem);
        return dir is null ? null : Directory.EnumerateFiles(dir, "*.esp").FirstOrDefault();
    }

    static string? FindPlacedFacegen(string? patchFolder) =>
        patchFolder is null ? null
        : Directory.EnumerateFiles(patchFolder, "*.nif", SearchOption.AllDirectories)
                   .FirstOrDefault(f => f.Contains("facegeom", StringComparison.OrdinalIgnoreCase));

    /// <summary>The clone's new FormKey, read off the written patch by its EditorID — the identity the projection
    /// keys on, so the harness never has to guess an allocation.</summary>
    static FormKey? NewCloneKey(string? patchPath, string editorId)
    {
        if (patchPath is null) return null;
        var mod = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        try
        {
            return mod.Npcs.FirstOrDefault(n => string.Equals(n.EditorID, editorId, StringComparison.OrdinalIgnoreCase))?.FormKey;
        }
        finally { (mod as IDisposable)?.Dispose(); }
    }

    /// <summary>Does the donor actually carry this bundle field? The judgement the skill makes before composing the
    /// zip, made here the same way — off the donor's own record, not off knowledge of how the fixture was built.</summary>
    static bool DonorHas(string mods, FormKey donorKey, string path)
    {
        var file = Path.Combine(mods, "DonorMod", donorKey.ModKey.FileName.String);
        if (!File.Exists(file)) return false;
        var mod = SkyrimMod.CreateFromBinaryOverlay(file, SkyrimRelease.SkyrimSE);
        try
        {
            var rec = mod.Npcs.FirstOrDefault(n => n.FormKey == donorKey);
            if (rec is null) return false;
            var rf = ReadEngine.ReadFields(rec, new[] { path }, depth: 1);
            // PRESENT, not HasValue: a substruct or a list reads back as a container with no leaf token of its own,
            // so a HasValue test would call every FaceMorph absent and clear the very fields it meant to copy.
            return rf.Fields.Any(f => f.Path == path && f.Present);
        }
        finally { (mod as IDisposable)?.Dispose(); }
    }

    static System.Text.Json.JsonElement Json(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();

    static string First(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).Trim();
    }
}

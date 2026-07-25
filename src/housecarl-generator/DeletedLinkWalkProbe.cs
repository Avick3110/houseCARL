using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD (<c>deleted-link-walk-guard</c>) for #279 — the DELETED-record rule in the two
/// link walkers that <c>deleted-record-scan-guard</c> (#276, cross_plugin_query) does NOT cover:
///   • <see cref="ErrorCheck.Run"/> — housecarl_check_errors' dangling-ref sweep.
///   • <see cref="RemapEngine.IdentifyExternalReferencers"/> — the compact/merge dependency scan.
/// All three now route through <see cref="DeletedRecordRule.HasNoLiveBody"/>; this pins the two new ones so the set
/// can't drift back apart.
///
/// TWO FAILURE MODES PER WALKER, so a GREEN means the whole rule holds, not just the crash half:
///   SEMANTIC — a deleted record with a PERFECTLY INTACT body that carries a link. Its content is not live, so that
///     link is not a dangling reference and not a dependency to repoint. Before the fix it was reported as both.
///     This arm needs no corruption at all: the discriminator is a wrong FINDING, not an exception.
///   CRASH-CLASS — a deleted record with a residual body whose LAZY parse throws (the wild #276 shape). Before the
///     fix it landed in the unscannable bucket with a raw exception cause — a deleted record reading as a parse hole,
///     so a genuine finding hiding in a "skipped" record looks possible when it isn't (Q3).
///
/// FIXTURES — Mutagen cannot author either shape (it writes only well-formed records, and serialises a model-deleted
/// record with an EMPTY body — the clean case, not the wild one), so both are written normally and then patched on
/// disk via <see cref="ProbeBytes"/>: the Deleted header flag OR-ed on, and (crash arm) one EPFT byte corrupted.
///
///   check_errors — HcDlwGhost.esm (on disk, NOT loaded → every ref into it fails to resolve) + HcDlwErr.esp:
///     LiveDangler (NPC, Race → the ghost race)  — CONTROL: a live dangling ref the sweep must STILL report.
///     DeadDangler (NPC, same link, Deleted)     — SEMANTIC arm: must NOT be reported dangling.
///     DeadThrower (PERK, EPFT corrupt, Deleted) — CRASH arm: must NOT be accounted unscannable.
///
///   remap — HcDlwTarget.esp (defines the weapon being renumbered) + HcDlwDep.esp, outside the transform set:
///     LiveRef  (FormList → the target weapon)          — CONTROL: still detected as an external referencer.
///     DeadRef  (FormList → same target, Deleted)       — SEMANTIC arm: must NOT be listed as a referencer.
///     DeadOverride (Weapon at the TARGET's FormKey, Deleted) — SCOPE arm: must STILL be listed as an external
///       OVERRIDER. The overrider test is identity-only (the record's own FormKey, read from the header), so the
///       guard belongs behind it, not at the top of the try — this arm is what fails if a future edit moves it.
///
/// CONTROLS make each GREEN meaningful (the #276 lesson: an arm that passes with the fix reverted proves nothing).
/// Before asserting anything, the probe reads each patched record back through a bare overlay and requires it to
/// (a) report IsDeleted, and (b) STILL exhibit the pre-fix hazard — the intact bodies must still YIELD their link
/// from EnumerateFormLinks, and the corrupted body must still THROW from it.
///
/// Run: dotnet run --project src/housecarl-generator -- deleted-link-walk-guard
/// </summary>
public static class DeletedLinkWalkProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — a DELETED record links to nothing, in check_errors and the compact/merge scan (#279)  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-deleted-link-walk-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        int fail = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) fail++;
        }

        try
        {
            // Sequenced deliberately: `fail += Arms(...)` would read `fail` BEFORE the call and then overwrite
            // every increment the Check closure made inside it — a probe that reports PASS while arms print FAIL.
            int errAbort = CheckErrorsArms(tmpDir, Check);
            fail += errAbort;
            Console.WriteLine();
            int remapAbort = RemapArms(tmpDir, Check);
            fail += remapAbort;
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }

        Console.WriteLine();
        Console.WriteLine($"=== deleted-link-walk-guard: {(fail == 0 ? "PASS" : $"FAIL ({fail})")} ===");
        return fail == 0 ? 0 : 1;
    }

    // =====================================================================================
    //  housecarl_check_errors — ErrorCheck.Run
    // =====================================================================================
    static int CheckErrorsArms(string tmpDir, Action<string, bool, string?> Check)
    {
        Console.WriteLine("-- housecarl_check_errors (ErrorCheck.Run) --");

        string ghostPath = Path.Combine(tmpDir, "HcDlwGhost.esm");
        string errPath = Path.Combine(tmpDir, "HcDlwErr.esp");
        var errKey = new ModKey("HcDlwErr", ModType.Plugin);
        FormKey ghostRaceFk, liveNpcFk, deadNpcFk, deadPerkFk;
        {
            var ghost = new SkyrimMod(new ModKey("HcDlwGhost", ModType.Master), SkyrimRelease.SkyrimSE);
            var gRace = ghost.Races.AddNew(); gRace.EditorID = "HcDlwGhostRace"; ghostRaceFk = gRace.FormKey;
            ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var err = new SkyrimMod(errKey, SkyrimRelease.SkyrimSE);
            var live = err.Npcs.AddNew(); live.EditorID = "HcDlwLiveDangler"; live.Race.SetTo(ghostRaceFk); liveNpcFk = live.FormKey;
            var dead = err.Npcs.AddNew(); dead.EditorID = "HcDlwDeadDangler"; dead.Race.SetTo(ghostRaceFk); deadNpcFk = dead.FormKey;
            var thrower = err.Perks.AddNew(); thrower.EditorID = "HcDlwDeadThrower"; deadPerkFk = thrower.FormKey;
            thrower.Effects.Add(new PerkEntryPointModifyActorValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
                ActorValue = ActorValue.OneHanded,
                Value = 1f,
                Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
            });
            // Declares HcDlwGhost.esm as its one master (the NPCs link into it), so this plugin's OWN records sit at
            // master index 1 on disk — the raw FormID the byte patcher matches, not the bare object id.
            err.BeginWrite.ToPath(errPath).WithLoadOrder(new ISkyrimModGetter[] { ghost }).Write();
        }
        const uint ownIndex = 1u << 24;      // one declared master ⇒ own records are index 1
        int corrupted = ProbeBytes.CorruptEpftBytes(errPath);
        int delNpc = ProbeBytes.SetDeletedFlag(errPath, "NPC_", ownIndex | deadNpcFk.ID);
        int delPerk = ProbeBytes.SetDeletedFlag(errPath, "PERK", ownIndex | deadPerkFk.ID);
        Console.WriteLine($"   setup: corrupted {corrupted} EPFT byte(s); flagged {delNpc} NPC_ + {delPerk} PERK Deleted on disk");
        if (corrupted != 1 || delNpc != 1 || delPerk != 1)
        {
            Console.WriteLine($"  FAIL  SETUP — expected 1 EPFT + 1 NPC_ + 1 PERK patched, got {corrupted}/{delNpc}/{delPerk} (fixture layout assumption wrong)");
            return 1;
        }

        // CONTROL — the fixture must still exhibit the PRE-FIX hazard, or a GREEN below is vacuous.
        bool npcDeleted = false, npcStillLinks = false, perkDeleted = false, perkThrows = false;
        string throwMsg = "(no throw)";
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(errPath, SkyrimRelease.SkyrimSE))
        {
            var deadNpc = ov.Npcs.First(n => n.FormKey == deadNpcFk);
            npcDeleted = deadNpc.IsDeleted;
            npcStillLinks = ((IFormLinkContainerGetter)deadNpc).EnumerateFormLinks().Any(l => l.FormKey == ghostRaceFk);
            var deadPerk = ov.Perks.First(p => p.FormKey == deadPerkFk);
            perkDeleted = deadPerk.IsDeleted;
            try { _ = ((IFormLinkContainerGetter)deadPerk).EnumerateFormLinks().Count(); }
            catch (Exception ex) { perkThrows = true; throwMsg = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        Check("CONTROL — the deleted NPC reads as Deleted and its intact body STILL yields the ghost link", npcDeleted && npcStillLinks,
              $"IsDeleted={npcDeleted}, link still enumerated={npcStillLinks}");
        Check("CONTROL — the deleted perk reads as Deleted and STILL throws from EnumerateFormLinks", perkDeleted && perkThrows,
              $"IsDeleted={perkDeleted}, throws={perkThrows} [{throwMsg}]");

        // Drive the REAL sweep. HcDlwGhost.esm is on disk but NOT in the order, so every ref into it fails to resolve.
        using var resolver = LoadOrderResolver.Build(new[] { errPath });
        var r = ErrorCheck.Run(resolver, null, limit: 100);

        var dangling = r.Reports.SelectMany(p => p.Dangling).ToList();
        Check("sweep completed with no Q3 error", r.Success, r.Error);
        Check("CONTROL — the LIVE dangling ref is still reported (no false clean)",
              dangling.Any(d => d.Source == liveNpcFk && d.Target == ghostRaceFk),
              $"dangling = [{string.Join(", ", dangling.Select(d => $"{d.SourceEditorId}→{d.Target}"))}]");
        // SEMANTIC arm — RED before the fix (the deleted NPC's intact body was walked, so TotalDangling was 2).
        Check("SEMANTIC — the DELETED record's link is NOT reported dangling (#279)",
              r.TotalDangling == 1 && dangling.All(d => d.Source != deadNpcFk),
              $"TotalDangling={r.TotalDangling}, sources = [{string.Join(", ", dangling.Select(d => d.SourceEditorId))}]");
        // CRASH-CLASS arm — RED before the fix (the deleted perk threw and was accounted as an untyped skip).
        var samples = r.Reports.SelectMany(p => p.UnscannableSamples).ToList();
        Check("CRASH-CLASS — the DELETED throwing record is NOT accounted unscannable (#279)",
              r.TotalUnscannableRecords == 0 && !samples.Any(s => s.Contains(deadPerkFk.ToString(), StringComparison.OrdinalIgnoreCase)),
              $"TotalUnscannableRecords={r.TotalUnscannableRecords}, samples = [{string.Join(" | ", samples)}]");

        return 0;   // every assertion above reports through Check (which owns the failure count); this returns only the hard SETUP abort
    }

    // =====================================================================================
    //  compact/merge dependency scan — RemapEngine.IdentifyExternalReferencers
    // =====================================================================================
    static int RemapArms(string tmpDir, Action<string, bool, string?> Check)
    {
        Console.WriteLine("-- compact/merge dependency scan (RemapEngine.IdentifyExternalReferencers) --");

        var tKey = new ModKey("HcDlwTarget", ModType.Plugin);
        var dKey = new ModKey("HcDlwDep", ModType.Plugin);
        string targetPath = Path.Combine(tmpDir, tKey.FileName.String);
        string depPath = Path.Combine(tmpDir, dKey.FileName.String);
        var targetWeapFk = new FormKey(tKey, 0xA01);          // the record about to be renumbered
        var liveRefFk = new FormKey(dKey, 0xB01);
        var deadRefFk = new FormKey(dKey, 0xB02);
        var deadPerkFk = new FormKey(dKey, 0xB03);

        {
            var t = new SkyrimMod(tKey, SkyrimRelease.SkyrimSE);
            t.Weapons.Add(new Weapon(targetWeapFk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwTargetWeap", BasicStats = new WeaponBasicStats { Damage = 7 } });
            t.BeginWrite.ToPath(targetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

            using var tOv = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            var d = new SkyrimMod(dKey, SkyrimRelease.SkyrimSE);
            var liveList = new FormList(liveRefFk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwLiveRef" };
            liveList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(targetWeapFk));
            d.FormLists.Add(liveList);
            var deadList = new FormList(deadRefFk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadRef" };
            deadList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(targetWeapFk));
            d.FormLists.Add(deadList);
            // A pure OVERRIDE of the target's weapon — same FormKey, no outgoing link into the transform set.
            d.Weapons.Add(new Weapon(targetWeapFk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadOverride", BasicStats = new WeaponBasicStats { Damage = 9 } });
            // The CRASH-CLASS fixture for this walker: a perk whose lazy Effects parse throws, flagged Deleted.
            var thrower = new Perk(deadPerkFk, SkyrimRelease.SkyrimSE) { EditorID = "HcDlwDeadThrower" };
            thrower.Effects.Add(new PerkEntryPointModifyActorValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.CalculateWeaponDamage,
                ActorValue = ActorValue.OneHanded,
                Value = 1f,
                Modification = PerkEntryPointModifyActorValue.ModificationType.AddAVMult,
            });
            d.Perks.Add(thrower);
            d.ModHeader.Stats.NextFormID = 0xB04;
            d.BeginWrite.ToPath(depPath).WithLoadOrder(new[] { tOv }).NoNextFormIDProcessing().Write();
        }
        // HcDlwDep declares HcDlwTarget as its one master, so its OWN records sit at index 1 on disk while the
        // OVERRIDE keeps the master's index 0 — the two raw FormIDs the byte patcher must match.
        int corrupted = ProbeBytes.CorruptEpftBytes(depPath);
        int delRef = ProbeBytes.SetDeletedFlag(depPath, "FLST", (1u << 24) | deadRefFk.ID);
        int delPerk = ProbeBytes.SetDeletedFlag(depPath, "PERK", (1u << 24) | deadPerkFk.ID);
        int delOvr = ProbeBytes.SetDeletedFlag(depPath, "WEAP", targetWeapFk.ID);
        Console.WriteLine($"   setup: corrupted {corrupted} EPFT byte(s); flagged {delRef} FLST + {delPerk} PERK + {delOvr} WEAP Deleted on disk");
        if (corrupted != 1 || delRef != 1 || delPerk != 1 || delOvr != 1)
        {
            Console.WriteLine($"  FAIL  SETUP — expected 1 EPFT + 1 FLST + 1 PERK + 1 WEAP patched, got {corrupted}/{delRef}/{delPerk}/{delOvr} (fixture layout assumption wrong)");
            return 1;
        }

        // CONTROL — the deleted FormList must read as Deleted AND still yield its link, or the SEMANTIC arm is vacuous.
        bool refDeleted = false, refStillLinks = false, ovrDeleted = false, perkDeleted = false, perkThrows = false;
        string throwMsg = "(no throw)";
        using (var ov = SkyrimMod.CreateFromBinaryOverlay(depPath, SkyrimRelease.SkyrimSE))
        {
            var deadList = ov.FormLists.First(f => f.FormKey == deadRefFk);
            refDeleted = deadList.IsDeleted;
            refStillLinks = ((IFormLinkContainerGetter)deadList).EnumerateFormLinks().Any(l => l.FormKey == targetWeapFk);
            ovrDeleted = ov.Weapons.First(w => w.FormKey == targetWeapFk).IsDeleted;
            var deadPerk = ov.Perks.First(p => p.FormKey == deadPerkFk);
            perkDeleted = deadPerk.IsDeleted;
            try { _ = ((IFormLinkContainerGetter)deadPerk).EnumerateFormLinks().Count(); }
            catch (Exception ex) { perkThrows = true; throwMsg = $"{ex.GetType().Name}: {ex.Message}"; }
        }
        Check("CONTROL — the deleted FormList reads as Deleted and its intact body STILL yields the target link", refDeleted && refStillLinks,
              $"IsDeleted={refDeleted}, link still enumerated={refStillLinks}");
        Check("CONTROL — the external override reads as Deleted (the scope arm's premise)", ovrDeleted, $"IsDeleted={ovrDeleted}");
        Check("CONTROL — the deleted perk reads as Deleted and STILL throws from EnumerateFormLinks", perkDeleted && perkThrows,
              $"IsDeleted={perkDeleted}, throws={perkThrows} [{throwMsg}]");

        using var resolver = LoadOrderResolver.Build(new[] { targetPath, depPath });
        var r = RemapEngine.IdentifyExternalReferencers(
            resolver,
            new HashSet<FormKey> { targetWeapFk },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tKey.FileName.String });

        Check("CONTROL — the LIVE referencer is still detected (no false clean)",
              r.Refs.Any(x => x.Source == liveRefFk) && r.HasExternalReferencers,
              $"refs = [{string.Join(", ", r.Refs.Select(x => x.Source.ToString()))}]");
        // SEMANTIC arm — RED before the fix (the deleted FormList's intact body was walked, so Refs held 2).
        Check("SEMANTIC — the DELETED record is NOT listed as an external referencer (#279)",
              r.Refs.Count == 1 && r.Refs.All(x => x.Source != deadRefFk),
              $"Refs.Count={r.Refs.Count}, sources = [{string.Join(", ", r.Refs.Select(x => x.Source.ToString()))}]");
        // SCOPE arm — the guard must sit BEHIND the identity-only overrider test, not at the top of the try.
        Check("SCOPE — a DELETED external OVERRIDE is still warned (identity test unaffected by the link-walk guard)",
              r.Overrides.Any(x => x.Record == targetWeapFk) && r.HasExternalOverriders,
              $"overrides = [{string.Join(", ", r.Overrides.Select(x => $"{x.Plugin}:{x.Record}"))}]");
        // CRASH-CLASS arm — RED before the fix (the deleted perk threw and was accounted as an untyped skip).
        Check("CRASH-CLASS — the DELETED throwing record is NOT accounted unscannable (#279)", r.UnscannableRecords == 0,
              $"UnscannableRecords={r.UnscannableRecords}, samples = [{string.Join(" | ", r.UnscannableSamples)}]");

        return 0;   // as above — Check owns the count; a non-zero return here means the fixture never got built
    }
}

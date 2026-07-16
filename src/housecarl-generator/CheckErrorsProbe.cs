using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the LOAD-ORDER INTEGRITY SWEEP (housecarl_check_errors — audit A1). Drives the
/// REAL product path (<see cref="ErrorCheck.Run"/> — what housecarl_check_errors calls through the thin service wrapper)
/// against a SYNTHESIZED 4-plugin order in TEMP — NO Skyrim.esm, so it runs in CI.
///
/// THE GAP (reproduced by construction): no general "validate every record" verb existed — validate_dialogue is
/// DIAL/QUST-only. The sweep walks every record's FormLinks and reports dangling refs, missing masters, and parse
/// failures, the data-layer twin of the CK's "Check For Errors".
///
/// FIXTURE — four plugins, two of them masters, one omitted from the loaded order ON PURPOSE:
///   • HcCeMaster.esm  — defines a Race (HcCeMasterRace). Present in the order.
///   • HcCeGhost.esm   — defines a Race (HcCeGhostRace). DECLARED by HcCeBad as a master, but NOT loaded → the
///                       missing-master fixture, and every ref into it is therefore also dangling.
///   • HcCeClean.esp   — masters [HcCeMaster]; one NPC whose Race → HcCeMasterRace (a VALID ref). The clean control:
///                       a Mutagen-written plugin whose masters all resolve must produce ZERO findings.
///   • HcCeBad.esp     — masters [HcCeMaster, HcCeGhost]; NPC1 Race → HcCeGhostRace (missing-master + dangling), NPC2
///                       Race → 0F0F0F:HcCeMaster.esm (dangling, master PRESENT — isolates "dangling" from "missing").
/// The order built for the resolver is [Master, Clean, Bad] — Ghost on disk but NOT in the order.
///
/// Arms (ALL required — a GREEN must mean "the contract holds"):
///   CLEAN-CONTROL   — HcCeClean.esp produces NO report (valid ref + a fresh NPC's default-null links are not flagged:
///                     the no-false-positive teeth, and the proof a null FormLink is treated as a legal optional).
///   DANGLING-GHOST  — HcCeBad's dangling set contains the ref to HcCeGhostRace (a ref into an absent master).
///   DANGLING-DEAD   — HcCeBad's dangling set contains the ref to 0F0F0F:HcCeMaster.esm (master present, target absent).
///   DANGLING-TOTAL  — exactly 2 dangling refs across the sweep (no stray links from the fresh NPCs).
///   SOURCE-ATTRIB   — each dangling ref names its SOURCE record (editorid + "Npc" type), not just the target.
///   MISSING-MASTER  — HcCeBad lists HcCeGhost.esm as a missing master; HcCeMaster.esm (present) is NOT listed.
///   MISSING-ISOLATE — no plugin reports HcCeMaster.esm missing (a present master is never a false missing-master).
///   SCANNED         — the whole-order sweep scanned 3 plugins (Master, Clean, Bad — Ghost is not in the order).
///   SCOPE           — scope=[HcCeBad.esp] reports only Bad (1 plugin scanned), Clean absent.
///   SCOPE-Q3        — scope=[a name not in the order] fails LOUD ("not in the load order"), no reports (never a silent skip).
///   CAP             — limit=1 over Bad's 2 dangling refs returns 1 but reports the TRUE total (2) and Capped.
///   OFF-ORDER-SELF   — an off-order file's link to a record IT DEFINES is not dangling (the patch-links-its-own-new-record case).
///   OFF-ORDER-DANGLE — its truly-dead refs DO dangle and its absent declared master IS a missing-master finding.
///   OFF-ORDER-STAMP  — the result stamps the off-order names (OffOrderScanned) and counts them in PluginsScanned;
///                      mixing an active scope name with an off-order file reports both (HCBR-2026-07-14-02 gap 3:
///                      the pre-enable verify sweep of a patch houseCARL just wrote).
///   PLAYERREF-WHITELIST — engine-implicit refs (000014 PlayerRef, 000007 Player, in Skyrim.esm) are NOT flagged dangling
///                     (HCBR checkerrors-playerref-dangling-false-positive: check_errors was reporting 531/531 false → 000014).
///   PLAYERREF-CONTROL   — a DIFFERENT sub-0x800 form (000015:Skyrim.esm) IS still flagged and the plugin totals 1 dangling:
///                     the exemption is a PRECISE 2-form set, not the whole reserved range, so a real typo'd low FormID surfaces.
///   OWNER-RANK          — a faction-owned container item's COED RequiredRank -1 (0xFFFFFFFF → id FFFFFF) is NOT a dangling ref
///                     (#207): when the owner faction lives in a master, a bare overlay mis-types the owner to UntypedOwner and
///                     exposes the rank word AS a FormLink; the sweep exempts that word (ErrorCheck.UntypedOwnerVariableData).
///   OWNER-DATA-CHECKED  — the owner FORM itself is still swept — a faction owner in an ABSENT master DOES dangle + is a missing
///                     master (the exemption drops ONLY the ambiguous rank word, never the owner reference).
///   OWNER-RANK-TOTAL    — exactly 1 dangling across the owner order (both chests' rank words exempt; only the broken owner form) —
///                     without the fix the same order totals 3 (two FFFFFF rank artifacts + the ghost owner).
///
/// OWNER FIXTURE (#207, its own order): HcCeMaster.esm also defines a Faction; HcCeOwner.esp masters [HcCeMaster, HcCeGhost]
///   and carries two owned containers — one owned by the PRESENT master's faction (rank -1 → must not dangle) and one owned by a
///   faction id in the ABSENT ghost master (rank -1 → the owner form dangles, the rank word does not). Built as [Master, Owner].
///
/// PLAYERREF FIXTURE (its own order, engine-implicit whitelist): a stub Skyrim.esm base master, on disk but NOT loaded
///   (the absent-master shape again, so every ref into it fails ResolveWinner), and HcCePlayer.esp mastering [Skyrim] with
///   three NPCs whose Race points at 000014 (PlayerRef, whitelisted), 000007 (Player, whitelisted), and 000015 (a
///   non-whitelisted sub-0x800 control). Built as [HcCePlayer] alone — Skyrim.esm is the missing master.
///
/// COVERAGE NOTE (Q3 — name what this guard LEANS ON rather than re-proves): PARSE failures are not synthesized here.
///   Whole-plugin exclusion rides on the index build's ExcludedPlugins machinery (exercised across the suite), and the
///   per-record link-walk fault isolation is the SAME try/catch idiom proven by effect-chain-guard + the cross_plugin_query
///   scan. The sweep surfaces both verbatim (ExcludedPlugins + the unscannable accounting), it does not re-implement them.
///
/// Run: dotnet run --project src/housecarl-generator -- check-errors-guard
/// </summary>
public static class CheckErrorsProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("check-errors-guard — load-order integrity sweep (housecarl_check_errors, audit A1)");
        Console.WriteLine();
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-check-errors-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        string masterPath = Path.Combine(tmpDir, "HcCeMaster.esm");
        string ghostPath  = Path.Combine(tmpDir, "HcCeGhost.esm");
        string cleanPath  = Path.Combine(tmpDir, "HcCeClean.esp");
        string badPath    = Path.Combine(tmpDir, "HcCeBad.esp");
        string skyrimPath = Path.Combine(tmpDir, "Skyrim.esm");     // stub base master for the PlayerRef arm — on disk, NOT loaded
        string playerPath = Path.Combine(tmpDir, "HcCePlayer.esp");
        var deadFk = FormKey.Factory("0F0F0F:HcCeMaster.esm");   // an object id HcCeMaster.esm does NOT define (master present, target absent)
        var playerRefFk  = FormKey.Factory("000014:Skyrim.esm");  // PlayerRef — engine-implicit, whitelisted (must NOT dangle)
        var playerBaseFk = FormKey.Factory("000007:Skyrim.esm");  // Player base NPC_ — engine-implicit, whitelisted (must NOT dangle)
        var sub800Fk     = FormKey.Factory("000015:Skyrim.esm");  // a DIFFERENT sub-0x800 form — NOT whitelisted (MUST dangle: proves precision)
        var ghostOwnerFactionFk = FormKey.Factory("000D0F:HcCeGhost.esm");  // #207: an owner-faction id in the ABSENT master (proves OwnerData is still swept)
        FormKey masterRaceFk, ghostRaceFk, ownerFactionFk;
        try
        {
            var master = new SkyrimMod(new ModKey("HcCeMaster", ModType.Master), SkyrimRelease.SkyrimSE);
            var mRace = master.Races.AddNew(); mRace.EditorID = "HcCeMasterRace"; masterRaceFk = mRace.FormKey;
            var mFac = master.Factions.AddNew(); mFac.EditorID = "HcCeMasterFaction"; ownerFactionFk = mFac.FormKey;  // #207 owner-target fixture: a faction that lives in a MASTER
            master.BeginWrite.ToPath(masterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var ghost = new SkyrimMod(new ModKey("HcCeGhost", ModType.Master), SkyrimRelease.SkyrimSE);
            var gRace = ghost.Races.AddNew(); gRace.EditorID = "HcCeGhostRace"; ghostRaceFk = gRace.FormKey;
            ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            // Clean dependent: one NPC, Race → a VALID master race. WithLoadOrder([master]) declares HcCeMaster only.
            var clean = new SkyrimMod(new ModKey("HcCeClean", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var cNpc = clean.Npcs.AddNew(); cNpc.EditorID = "HcCeCleanNpc"; cNpc.Race.SetTo(masterRaceFk);
            clean.BeginWrite.ToPath(cleanPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

            // Broken dependent: NPC1 → Ghost's race (missing master + dangling); NPC2 → a dead id in the present master.
            // Referencing both masters makes Mutagen declare [HcCeMaster, HcCeGhost] in the header.
            var bad = new SkyrimMod(new ModKey("HcCeBad", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var bGhost = bad.Npcs.AddNew(); bGhost.EditorID = "HcCeBadGhostNpc"; bGhost.Race.SetTo(ghostRaceFk);
            var bDead  = bad.Npcs.AddNew(); bDead.EditorID  = "HcCeBadDeadNpc";  bDead.Race.SetTo(deadFk);
            bad.BeginWrite.ToPath(badPath).WithLoadOrder(new ISkyrimModGetter[] { master, ghost }).Write();

            // PlayerRef whitelist fixture (HCBR checkerrors-playerref-dangling-false-positive). A stub Skyrim.esm base
            // master, written but NOT loaded into the order below — so every ref into it fails ResolveWinner, the same
            // absent-master shape as Ghost. HcCePlayer masters [Skyrim] and points three NPC Race links at the two
            // whitelisted engine-implicit forms (0x14, 0x07) and one non-whitelisted control (0x15). Only 0x15 must dangle.
            var skyrim = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
            skyrim.Races.AddNew();   // one throwaway record so the stub is a valid, non-empty master
            skyrim.BeginWrite.ToPath(skyrimPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var player = new SkyrimMod(new ModKey("HcCePlayer", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var pRef  = player.Npcs.AddNew(); pRef.EditorID  = "HcCePlayerRefNpc";  pRef.Race.SetTo(playerRefFk);   // 0x14 — whitelisted
            var pBase = player.Npcs.AddNew(); pBase.EditorID = "HcCePlayerBaseNpc"; pBase.Race.SetTo(playerBaseFk);  // 0x07 — whitelisted
            var pDead = player.Npcs.AddNew(); pDead.EditorID = "HcCePlayerDeadNpc"; pDead.Race.SetTo(sub800Fk);      // 0x15 — must dangle
            player.BeginWrite.ToPath(playerPath).WithLoadOrder(new ISkyrimModGetter[] { skyrim }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        // Build the order WITHOUT Ghost — it is the missing master.
        using var r = LoadOrderResolver.Build(new[] { masterPath, cleanPath, badPath });

        var all = ErrorCheck.Run(r, null, 1000);
        if (!all.Success) { Console.Error.WriteLine($"error: whole-order sweep failed: {all.Error}"); return 1; }

        PluginErrors? Bad() => all.Reports.FirstOrDefault(p => p.Plugin == "HcCeBad.esp");
        var bad2 = Bad();

        Check("CLEAN-CONTROL: HcCeClean.esp produces NO report (valid ref + fresh NPC's null links are not flagged)",
            all.Reports.All(p => p.Plugin != "HcCeClean.esp"),
            $"clean report present={all.Reports.Any(p => p.Plugin == "HcCeClean.esp")}");

        Check("DANGLING-GHOST: HcCeBad's dangling set contains the ref into the absent master (HcCeGhostRace)",
            bad2 is not null && bad2.Dangling.Any(d => d.Target == ghostRaceFk),
            $"bad={(bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => d.Target.ToString())))}");

        Check("DANGLING-DEAD: HcCeBad's dangling set contains the ref to a dead id in the present master (0F0F0F:HcCeMaster.esm)",
            bad2 is not null && bad2.Dangling.Any(d => d.Target == deadFk),
            $"bad={(bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => d.Target.ToString())))}");

        Check("DANGLING-TOTAL: exactly 2 dangling refs across the sweep (no stray links from the fresh NPCs)",
            all.TotalDangling == 2, $"total={all.TotalDangling}");

        Check("SOURCE-ATTRIB: each dangling ref names its SOURCE record (editorid + 'Npc' type)",
            bad2 is not null
            && bad2.Dangling.Any(d => d.SourceEditorId == "HcCeBadGhostNpc" && d.SourceType == "Npc")
            && bad2.Dangling.Any(d => d.SourceEditorId == "HcCeBadDeadNpc"  && d.SourceType == "Npc"),
            bad2 is null ? "<no report>" : string.Join(",", bad2.Dangling.Select(d => $"{d.SourceEditorId}/{d.SourceType}")));

        Check("MISSING-MASTER: HcCeBad lists HcCeGhost.esm as missing; HcCeMaster.esm (present) is NOT listed",
            bad2 is not null
            && bad2.MissingMasters.Contains("HcCeGhost.esm", StringComparer.OrdinalIgnoreCase)
            && !bad2.MissingMasters.Contains("HcCeMaster.esm", StringComparer.OrdinalIgnoreCase),
            bad2 is null ? "<no report>" : string.Join(",", bad2.MissingMasters));

        Check("MISSING-ISOLATE: no plugin reports HcCeMaster.esm missing (a present master is never a false missing-master)",
            all.Reports.All(p => !p.MissingMasters.Contains("HcCeMaster.esm", StringComparer.OrdinalIgnoreCase)),
            string.Join(" | ", all.Reports.Select(p => $"{p.Plugin}:[{string.Join(",", p.MissingMasters)}]")));

        Check("SCANNED: the whole-order sweep scanned 3 plugins (Master, Clean, Bad — Ghost not in the order)",
            all.PluginsScanned == 3, $"scanned={all.PluginsScanned}");

        // ---- SCOPE: only the named plugin. ----
        var scoped = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000);
        Check("SCOPE: scope=[HcCeBad.esp] reports only Bad (1 plugin scanned), Clean absent",
            scoped.Success && scoped.PluginsScanned == 1 && scoped.Reports.Count == 1
            && scoped.Reports[0].Plugin == "HcCeBad.esp",
            $"success={scoped.Success} scanned={scoped.PluginsScanned} reports={scoped.Reports.Count}");

        var q3 = ErrorCheck.Run(r, new[] { "HcCeNotReal.esp" }, 1000);
        Check("SCOPE-Q3: an unknown scope name fails LOUD ('not in the load order'), no reports",
            !q3.Success && q3.Reports.Count == 0 && q3.Error is not null
            && q3.Error.Contains("not in the load order", StringComparison.Ordinal),
            $"success={q3.Success} reports={q3.Reports.Count} err=[{q3.Error}]");

        // ---- CAP: limit=1 over Bad's 2 dangling refs. ----
        var capped = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1);
        Check("CAP: limit=1 returns 1 dangling ref but reports the TRUE total (2) and Capped",
            capped.TotalDangling == 2 && capped.Capped
            && capped.Reports.Count == 1 && capped.Reports[0].Dangling.Count == 1,
            $"total={capped.TotalDangling} capped={capped.Capped} collected={(capped.Reports.Count > 0 ? capped.Reports[0].Dangling.Count : -1)}");

        // ---- OFF-ORDER: a plugin FILE not in the order, swept via the offOrder lane (the pre-enable verify sweep of a
        //      patch houseCARL just wrote — HCBR-2026-07-14-02 gap 3). The fixture patch masters [Master, Ghost] and
        //      carries: a NEW race of its own; an NPC → that own race (self-link, must NOT dangle); an NPC → the dead id
        //      (must dangle); an NPC → Ghost's race (missing master + dangling). ----
        string patchPath = Path.Combine(tmpDir, "HcCePatch.esp");
        FormKey patchRaceFk;
        try
        {
            using var masterOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE);
            using var ghostOv = SkyrimMod.CreateFromBinaryOverlay(ghostPath, SkyrimRelease.SkyrimSE);
            var patch = new SkyrimMod(new ModKey("HcCePatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var ownRace = patch.Races.AddNew(); ownRace.EditorID = "HcCePatchRace"; patchRaceFk = ownRace.FormKey;
            var selfNpc = patch.Npcs.AddNew(); selfNpc.EditorID = "HcCePatchSelfNpc"; selfNpc.Race.SetTo(patchRaceFk);
            var deadNpc = patch.Npcs.AddNew(); deadNpc.EditorID = "HcCePatchDeadNpc"; deadNpc.Race.SetTo(deadFk);
            var ghostNpc = patch.Npcs.AddNew(); ghostNpc.EditorID = "HcCePatchGhostNpc"; ghostNpc.Race.SetTo(ghostRaceFk);
            patch.BeginWrite.ToPath(patchPath).WithLoadOrder(new ISkyrimModGetter[] { masterOv, ghostOv }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the off-order fixture: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        var off = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, new[] { ("HcCePatch.esp", patchPath) });
        var patch2 = off.Reports.FirstOrDefault(p => p.Plugin == "HcCePatch.esp");

        Check("OFF-ORDER-SELF: the off-order file's link to a record IT DEFINES is not dangling",
            off.Success && patch2 is not null && !patch2.Dangling.Any(d => d.Target == patchRaceFk),
            patch2 is null ? "<no report>" : string.Join(",", patch2.Dangling.Select(d => d.Target.ToString())));

        Check("OFF-ORDER-DANGLE: its dead ref + absent-master ref DO dangle; the absent declared master IS missing (present master is not)",
            patch2 is not null
            && patch2.Dangling.Any(d => d.Target == deadFk) && patch2.Dangling.Any(d => d.Target == ghostRaceFk)
            && patch2.MissingMasters.Contains("HcCeGhost.esm", StringComparer.OrdinalIgnoreCase)
            && !patch2.MissingMasters.Contains("HcCeMaster.esm", StringComparer.OrdinalIgnoreCase),
            patch2 is null ? "<no report>" : $"dangling=[{string.Join(",", patch2.Dangling.Select(d => d.Target.ToString()))}] missing=[{string.Join(",", patch2.MissingMasters)}]");

        Check("OFF-ORDER-STAMP: OffOrderScanned names the file; PluginsScanned counts active scope + off-order; Bad's report is ALSO present (mixed scope)",
            off.OffOrderScanned is { Count: 1 } oos && oos[0] == "HcCePatch.esp"
            && off.PluginsScanned == 2 && off.Reports.Any(p => p.Plugin == "HcCeBad.esp"),
            $"offOrder=[{string.Join(",", off.OffOrderScanned ?? Array.Empty<string>())}] scanned={off.PluginsScanned} reports=[{string.Join(",", off.Reports.Select(p => p.Plugin))}]");

        // ---- PLAYERREF: engine-implicit whitelist (its own order; Skyrim.esm on disk but NOT loaded). ----
        using var rp = LoadOrderResolver.Build(new[] { playerPath });
        var pl = ErrorCheck.Run(rp, null, 1000);
        if (!pl.Success) { Console.Error.WriteLine($"error: PlayerRef sweep failed: {pl.Error}"); return 1; }
        var player2 = pl.Reports.FirstOrDefault(p => p.Plugin == "HcCePlayer.esp");

        Check("PLAYERREF-WHITELIST: engine-implicit refs (000014 PlayerRef, 000007 Player) are NOT flagged dangling",
            player2 is not null
            && !player2.Dangling.Any(d => d.Target == playerRefFk)
            && !player2.Dangling.Any(d => d.Target == playerBaseFk),
            player2 is null ? "<no report>" : string.Join(",", player2.Dangling.Select(d => d.Target.ToString())));

        Check("PLAYERREF-CONTROL: a non-whitelisted sub-0x800 form (000015) IS still flagged; the plugin totals 1 dangling (exemption is precise, not the whole range)",
            player2 is not null && player2.Dangling.Any(d => d.Target == sub800Fk) && pl.TotalDangling == 1,
            player2 is null ? "<no report>" : $"total={pl.TotalDangling} targets=[{string.Join(",", player2.Dangling.Select(d => d.Target.ToString()))}]");

        // ---- OWNER-RANK (#207): a container item owned by a FACTION carries a COED "required rank" word. When the
        //      owner faction lives in a MASTER (every override), a bare overlay cannot type the owner arm and Mutagen
        //      falls back to UntypedOwner, exposing that rank word AS a FormLink — so a rank of -1 (0xFFFFFFFF → id
        //      FFFFFF) was reported as a false dangling ref. HcCeOwner.esp carries two owned chests:
        //        • HcCeOwnedChest      — owner = a faction in the PRESENT master, rank -1 → must produce NO dangling.
        //        • HcCeGhostOwnedChest — owner = a faction in the ABSENT ghost master, rank -1 → the OWNER FORM must
        //          STILL dangle (only the rank word is exempt) + HcCeGhost is a missing master.
        //      Without the fix the order totals 3 dangling (two FFFFFF rank artifacts + the ghost owner); with it, 1. ----
        string ownerPath = Path.Combine(tmpDir, "HcCeOwner.esp");
        try
        {
            using var masterOv = SkyrimMod.CreateFromBinaryOverlay(masterPath, SkyrimRelease.SkyrimSE);
            using var ghostOv = SkyrimMod.CreateFromBinaryOverlay(ghostPath, SkyrimRelease.SkyrimSE);
            var ownerMod = new SkyrimMod(new ModKey("HcCeOwner", ModType.Plugin), SkyrimRelease.SkyrimSE);

            var goodChest = ownerMod.Containers.AddNew(); goodChest.EditorID = "HcCeOwnedChest";
            goodChest.Items = new()
            {
                new ContainerEntry
                {
                    Item = new ContainerItem { Count = 1 },
                    Data = new ExtraData { ItemCondition = 1f, Owner = new FactionOwner { Faction = new FormLink<IFactionGetter>(ownerFactionFk), RequiredRank = -1 } },
                },
            };

            var ghostChest = ownerMod.Containers.AddNew(); ghostChest.EditorID = "HcCeGhostOwnedChest";
            ghostChest.Items = new()
            {
                new ContainerEntry
                {
                    Item = new ContainerItem { Count = 1 },
                    Data = new ExtraData { ItemCondition = 1f, Owner = new FactionOwner { Faction = new FormLink<IFactionGetter>(ghostOwnerFactionFk), RequiredRank = -1 } },
                },
            };

            ownerMod.BeginWrite.ToPath(ownerPath).WithLoadOrder(new ISkyrimModGetter[] { masterOv, ghostOv }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the owner-target fixture: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var ro = LoadOrderResolver.Build(new[] { masterPath, ownerPath });   // ghost NOT loaded — the missing master again
        var ownerRes = ErrorCheck.Run(ro, null, 1000);
        if (!ownerRes.Success) { Console.Error.WriteLine($"error: owner-target sweep failed: {ownerRes.Error}"); return 1; }
        var ownerRep = ownerRes.Reports.FirstOrDefault(p => p.Plugin == "HcCeOwner.esp");

        Check("OWNER-RANK: a faction-owned item's RequiredRank -1 (0xFFFFFFFF) is NOT reported as a dangling ref (#207)",
            ownerRep is null || !ownerRep.Dangling.Any(d => d.Target.ID == 0xFFFFFF),
            ownerRep is null ? "<no report>" : $"targets=[{string.Join(",", ownerRep.Dangling.Select(d => d.Target.ToString()))}]");

        Check("OWNER-DATA-CHECKED: the owner FORM is still swept — a faction owner in an absent master DOES dangle + is a missing master",
            ownerRep is not null && ownerRep.Dangling.Any(d => d.Target == ghostOwnerFactionFk)
            && ownerRep.MissingMasters.Contains("HcCeGhost.esm", StringComparer.OrdinalIgnoreCase),
            ownerRep is null ? "<no report>" : $"dangling=[{string.Join(",", ownerRep.Dangling.Select(d => d.Target.ToString()))}] missing=[{string.Join(",", ownerRep.MissingMasters)}]");

        Check("OWNER-RANK-TOTAL: exactly 1 dangling across the owner order (both RequiredRank words exempt; only the broken owner form)",
            ownerRes.TotalDangling == 1, $"total={ownerRes.TotalDangling}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "check-errors-guard: ALL PASS" : $"check-errors-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}

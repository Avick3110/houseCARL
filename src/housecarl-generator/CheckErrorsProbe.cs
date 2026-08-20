using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

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
///   NARROW-FORMIDS  — formids=[one dangling SOURCE] narrows the sweep AND its totals to that record (#282).
///   NARROW-EDITORID — editorid_contains= narrows case-insensitively to the other one.
///   NARROW-TYPE     — type=NPC_ keeps both findings, type=RACE keeps none (the scope rides the record STREAM).
///   NARROW-NOTE     — a narrowed result carries a FilterNote saying the counts are scope-relative; an unnarrowed one doesn't.
///   CLASS-MASTERS-ONLY — findings=[missing_masters] skips the link walk; the render says dangling "NOT CHECKED", NOT 0
///                     (a skipped check that reads as a clean one is the Q3 break this tool exists to catch).
///   CLASS-DANGLING-ONLY — findings=[dangling] skips the master read; the render says missing masters "NOT CHECKED".
///   CLASS-Q3        — an unrecognized findings= value fails LOUD, names the legal set, and says the unread class is unfilterable.
///   COUNTS-ONLY     — counts_only=true keeps the totals exact, builds NO per-plugin body, and tallies dangling refs by TARGET plugin.
///   COUNTS-ONLY-NOT-COMPUTED — a normal sweep leaves Histogram null: "not computed" ≠ "nothing found".
///   JSON-PARITY     — format=json PARSES, agrees with the text totals, and emits null (not 0) for an unchecked class.
///   COUNTS-ONLY-NO-WALK — findings=[missing_masters]+counts_only leaves Histogram NULL and says the walk was not run,
///                     rather than rendering an empty histogram as "nothing to tally" (PR #288 review, finding 2).
///   MASTER-COUNT-PLUGIN-LEVEL — under a record scope the note marks the missing-master count plugin-level and NOT
///                     narrowed; the plain claim returns when there is no plugin-level number to caveat (finding 3).
///   UNREAD-TRUNC-FLAG — a budget-cut counts_only json flags the shortened `unread` honesty list (finding 4).
///   COUNTS-ONLY-EXCLUDED-NAMED — counts_only text NAMES the unparseable plugins, not just the header count (finding 5).
///   CLAIM-ONLY-WHEN-SCOPED — findings= alone lists the filter WITHOUT the not-the-whole-plugin claim: that total IS the
///                     complete whole-order figure, so the claim would be false about the number it sits under. A record
///                     scope brings the claim back (PR #288 RE-review, finding 3).
///   BASELINE-REACHABLE — with limit= consumable by the base-game baseline, the MOD plugin still appears WITH its
///                     findings listed (#344 — the acceptance shape: before the fix it vanished from Reports entirely).
///   BASELINE-TOTALS-EXACT / -SECTIONS-LOAD-ORDER — the totals still count everything and Capped still fires; the base
///                     master is swept last for BUDGET but its section still renders in load-order position.
///   BASELINE-OMITTED-NAMED — the cap names WHICH plugin lost entries and how many, in the result and the render (Q3:
///                     a plugin that lost its whole set has no section to read the loss off).
///   BASELINE-SUMMARY / -NOT-IN-SCOPE — the split is stated and NAMES the plugins counted as baseline; a sweep that
///                     never looked at one says nothing about baseline ("clean" and "never checked" must not read alike).
///   BASELINE-PHASE-CLAUSE-ONLY-WHEN-IT-BIT / -AMPLE-BUDGET-UNCHANGED — the budget-order sentence appears only where the
///                     order decided something, and an ample budget lists every finding exactly as before.
///   SOURCE-HISTOGRAM / -NOT-COMPUTED — counts_only tallies by SOURCE plugin beside the TARGET axis; with the walk
///                     skipped BOTH are null and the render says so (an absent axis must not read as an empty one).
///   RENDER-CUT-COUNTS-ITS-OWN-OMISSION / -SILENT-WHEN-IT-DID-NOT-CUT / -JSON-DERIVABLE / -ENTRY-COUNT-IS-WHAT-IT-EMITTED
///                     — the max_chars cut is counted and stated by the RENDER, in the render's terms and the budget
///                     line's unit; the capped line's subjects are the budget's alone. Reach: the notice fires at a
///                     section BOUNDARY; a cut inside the last section is #361, pre-existing.
///   BASELINE-PHASE-CLAUSE-NOT-A-SUBTRACTION / -STILL-PRINTS-WITH-A-MOD-IN-SCOPE — the ordering sentence gates on a
///                     stated fact, on a fixture where the two counts a subtraction would use deliberately disagree.
///   BASELINE-RECORD-SCOPE-NOT-SWEPT / -STILL-SWEPT — "swept" means the scope admitted a record, not that the file was
///                     opened; a master filtered out of the scope must not report as covered-and-clean.
///   BASELINE-PHASE-CLAUSE-NEEDS-A-NON-BASE-PLUGIN — the ordering sentence needs both groups it compares.
///   COUNTS-ONLY-NOTE-NOT-REPEATED — the mode note rides the first axis only.
///   OMITTED-NULL-UNDER-COUNTS-ONLY — counts_only lists nothing by design, so it reports no omissions rather than
///                     reporting the entire sweep as dropped.
///   BASE-SET-BY-CONSTRUCTION — the baseline set IS Mutagen's Implicits.BaseMasters, never a list kept here (#344).
///   BASELINE-JSON-PARITY — the json carries the same split, names the base set, flags whether one was in scope, and
///                     carries both new tables; an unchecked class emits null, not 0.
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
/// THE ASSERTION RULE (pinned here after this guard shipped a tautology through two review rounds): AN INVARIANT
///   ARM ASSERTS AGAINST A FIXTURE-KNOWN EXPECTED VALUE — NEVER AGAINST A PHRASE THE RENDER ITSELF EMITS. The cap
///   invariant excused every overrun that contained "raise it to at least"; the render appends that notice to every
///   response longer than its cap, so the arm could not fail and the two folds it was meant to pin were pinned by
///   nothing. A number the fixture knows (a length, a count, a total the sweep computed) can be wrong and be caught;
///   a substring the response composed about itself agrees with the response by construction. Where an arm must read
///   the render's own words — following a remedy to the number it names, say — the words locate the value and the
///   ASSERTION is still made against something measured independently.
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
        FormKey badGhostNpcFk, badDeadNpcFk;   // #282: the two dangling SOURCES, for the formids= / editorid_contains= scope arms
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
            badGhostNpcFk = bGhost.FormKey; badDeadNpcFk = bDead.FormKey;
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
        var cappedText = Wire.RenderCheckErrors(capped, 0);
        // The budget's omission is claimed by the RESPONSE now, not by a flag on the result (ErrorCheckResult.Capped
        // is superseded — see CheckAccounting). So the arm asserts what the caller is told, which is the thing that
        // has to be true, and it names the budget the SWEEP was given rather than a render default.
        Check("CAP: limit=1 returns 1 dangling ref, reports the TRUE total (2), and the response says the budget never listed the other",
            capped.TotalDangling == 2
            && capped.Reports.Count == 1 && capped.Reports[0].Dangling.Count == 1
            && cappedText.Contains("1 of the 2 dangling ref(s) found by this sweep appear above", StringComparison.Ordinal)
            && cappedText.Contains("1 were never listed: the listing budget (limit=1) ran out", StringComparison.Ordinal),
            $"total={capped.TotalDangling} collected={(capped.Reports.Count > 0 ? capped.Reports[0].Dangling.Count : -1)} line=[{AccountingLine(cappedText)}]");

        // ---- #282: the record scope / class filter / counts_only knobs. ONE plugin was the narrowest scope the tool
        //      had, and its whole per-plugin body overflowed the tool-result cap with no way to ask a smaller question. ----
        var byFormid = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, null,
            new SweepScope(new HashSet<FormKey> { badGhostNpcFk }, null, null, null));
        Check("NARROW-FORMIDS: formids=[the ghost-ref NPC] narrows the sweep to that record — 1 dangling, and it is the ghost ref",
            byFormid.Success && byFormid.TotalDangling == 1
            && byFormid.Reports.Count == 1 && byFormid.Reports[0].Dangling.Single().Target == ghostRaceFk,
            $"success={byFormid.Success} total={byFormid.TotalDangling} targets=[{string.Join(",", byFormid.Reports.SelectMany(p => p.Dangling).Select(d => d.Target.ToString()))}]");

        var byEditorId = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, null,
            new SweepScope(null, "deadnpc", null, null));   // case-insensitive by contract
        Check("NARROW-EDITORID: editorid_contains='deadnpc' (case-insensitive) narrows to the dead-ref NPC — 1 dangling, the dead id",
            byEditorId.Success && byEditorId.TotalDangling == 1
            && byEditorId.Reports.Count == 1 && byEditorId.Reports[0].Dangling.Single().Target == deadFk,
            $"success={byEditorId.Success} total={byEditorId.TotalDangling} targets=[{string.Join(",", byEditorId.Reports.SelectMany(p => p.Dangling).Select(d => d.Target.ToString()))}]");

        var byNpcType = ErrorCheck.Run(r, null, 1000, null, new SweepScope(null, null, new[] { typeof(INpcGetter) }, "NPC_"));
        var byRaceType = ErrorCheck.Run(r, null, 1000, null, new SweepScope(null, null, new[] { typeof(IRaceGetter) }, "RACE"));
        Check("NARROW-TYPE: type=NPC_ keeps both dangling refs (both sources are NPCs); type=RACE keeps NONE — the scope rides the record STREAM",
            byNpcType.Success && byNpcType.TotalDangling == 2 && byRaceType.Success && byRaceType.TotalDangling == 0,
            $"npc={byNpcType.TotalDangling} race={byRaceType.TotalDangling}");

        Check("NARROW-NOTE: a narrowed sweep carries a FilterNote saying the counts are scope-relative; an unnarrowed one carries none (Q3)",
            byFormid.FilterNote is not null && byFormid.FilterNote.Contains("NARROWED", StringComparison.Ordinal)
            && all.FilterNote is null,
            $"narrowed=[{byFormid.FilterNote}] unnarrowed=[{all.FilterNote}]");

        // findings= excluding 'dangling' must SKIP the link walk, and the render must say NOT CHECKED — not 0. A skipped
        // check that renders as a clean one is precisely the Q3 break this tool exists to catch in other people's data.
        var mastersOnly = ErrorCheck.Run(r, null, 1000, null, null, ErrorFindingClass.MissingMasters);
        var mastersText = Wire.RenderCheckErrors(mastersOnly, 0);
        Check("CLASS-MASTERS-ONLY: findings=[missing_masters] still finds HcCeGhost.esm, reports 0 dangling, and RENDERS dangling as 'NOT CHECKED' (never as 0)",
            mastersOnly.Success && mastersOnly.TotalMissingMasters == 1 && mastersOnly.TotalDangling == 0
            && !mastersOnly.Classes.HasFlag(ErrorFindingClass.Dangling)
            && mastersText.Contains("dangling refs NOT CHECKED", StringComparison.Ordinal)
            && !mastersText.Contains("0 dangling ref(s)", StringComparison.Ordinal),
            $"missing={mastersOnly.TotalMissingMasters} dangling={mastersOnly.TotalDangling} header=[{mastersText.Split('\n').Skip(1).FirstOrDefault()}]");

        var danglingOnly = ErrorCheck.Run(r, null, 1000, null, null, ErrorFindingClass.Dangling);
        var danglingText = Wire.RenderCheckErrors(danglingOnly, 0);
        Check("CLASS-DANGLING-ONLY: findings=[dangling] skips the master read (0 missing) and RENDERS missing masters as 'NOT CHECKED'",
            danglingOnly.Success && danglingOnly.TotalDangling == 2 && danglingOnly.TotalMissingMasters == 0
            && danglingOnly.Reports.All(p => p.MissingMasters.Count == 0)
            && danglingText.Contains("missing masters NOT CHECKED", StringComparison.Ordinal),
            $"dangling={danglingOnly.TotalDangling} missing={danglingOnly.TotalMissingMasters}");

        Check("CLASS-Q3: an unrecognized findings= value fails LOUD, names the legal set, and says the unread class can't be filtered out",
            !SweepFindings.TryParseErrorClasses(new[] { "danglign" }, out _, out var ceClassErr)
            && ceClassErr is not null && ceClassErr.Contains("'dangling'", StringComparison.Ordinal)
            && ceClassErr.Contains("missing_masters", StringComparison.Ordinal)
            && ceClassErr.Contains("cannot be filtered out", StringComparison.Ordinal),
            $"err=[{ceClassErr}]");

        // counts_only=: exact totals + a dangling-by-TARGET-plugin histogram, with NO per-plugin body built at all.
        var countsOnly = ErrorCheck.Run(r, null, 1000, null, null, ErrorFindingClass.All, countsOnly: true);
        var histo = countsOnly.Histogram;
        Check("COUNTS-ONLY: totals stay exact (2 dangling), NO per-plugin listing is built, and the histogram keys the refs by TARGET plugin",
            countsOnly.Success && countsOnly.TotalDangling == 2 && countsOnly.CountsOnly
            && countsOnly.Reports.Count == 0
            // counts_only lists nothing BY DESIGN, so the response makes no listing claim at all — absent, never a
            // zero, which would report a mode working correctly as a mode that dropped everything.
            && !Wire.RenderCheckErrors(countsOnly, 0).Contains("found by this sweep appear above", StringComparison.Ordinal)
            && histo is not null && histo.Sum(h => h.Count) == 2
            && histo.Any(h => h.Key.Equals("HcCeGhost.esm", StringComparison.OrdinalIgnoreCase) && h.Count == 1)
            && histo.Any(h => h.Key.Equals("HcCeMaster.esm", StringComparison.OrdinalIgnoreCase) && h.Count == 1),
            $"total={countsOnly.TotalDangling} reports={countsOnly.Reports.Count} histo=[{(histo is null ? "<null>" : string.Join(",", histo.Select(h => $"{h.Key}={h.Count}")))}]");

        Check("COUNTS-ONLY-NOT-COMPUTED: a normal sweep leaves Histogram NULL — 'not computed' and 'nothing found' must not look alike (Q3)",
            all.Histogram is null && !all.CountsOnly, $"histo={(all.Histogram is null ? "null" : all.Histogram.Count.ToString())}");

        // The json twin must carry the same data off the same result object (D2) and stay parseable in BOTH modes.
        Check("JSON-PARITY: format=json parses, reports the same dangling total, and emits null (not 0) for a class that was NOT checked",
            JsonMatches(JsonWire.RenderCheckErrors(all, 0), "dangling", 2)
            && JsonNull(JsonWire.RenderCheckErrors(mastersOnly, 0), "dangling")
            && JsonHasHistogram(JsonWire.RenderCheckErrors(countsOnly, 0), "dangling_by_target_plugin"),
            "see the three json renders");

        // #288 review finding 2: with 'dangling' excluded the link walk never runs, so there is nothing to tally. An
        // empty-but-PRESENT histogram rendered "nothing to tally — no findings in the swept scope", i.e. invariant #4
        // inverted: "not computed" reading as "nothing found", for the one combination COUNTS-ONLY did not exercise.
        var countsNoWalk = ErrorCheck.Run(r, null, 1000, null, null, ErrorFindingClass.MissingMasters, countsOnly: true);
        var noWalkText = Wire.RenderCheckErrors(countsNoWalk, 0);
        var noWalkJson = JsonWire.RenderCheckErrors(countsNoWalk, 0);
        Check("COUNTS-ONLY-NO-WALK: findings=[missing_masters]+counts_only leaves Histogram NULL, says the walk was not run, and never claims 'nothing to tally'",
            countsNoWalk.Success && countsNoWalk.Histogram is null
            && noWalkText.Contains("the link walk was not run", StringComparison.Ordinal)
            && !noWalkText.Contains("nothing to tally", StringComparison.Ordinal)
            && !noWalkJson.Contains("dangling_by_target_plugin", StringComparison.Ordinal),
            $"histo={(countsNoWalk.Histogram is null ? "null" : countsNoWalk.Histogram.Count.ToString())}");

        // #288 review finding 3: missing masters come off the plugin's master TABLE, so a RECORD scope cannot narrow
        // that count — the blanket "every count below is for THIS narrowed scope" was a false claim about the number
        // printed directly above it.
        var scopedWithMasters = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, null,
            new SweepScope(new HashSet<FormKey> { badGhostNpcFk }, null, null, null));
        Check("MASTER-COUNT-PLUGIN-LEVEL: under a record scope the note marks the missing-master count PLUGIN-level and NOT narrowed, instead of claiming every count is scoped",
            scopedWithMasters.FilterNote is not null
            && scopedWithMasters.FilterNote.Contains("PLUGIN-level", StringComparison.Ordinal)
            && scopedWithMasters.FilterNote.Contains("NOT narrowed", StringComparison.Ordinal)
            && !scopedWithMasters.FilterNote.Contains("every count below is for THIS narrowed scope", StringComparison.Ordinal)
            // …and with masters NOT in scope there is no plugin-level number to caveat, so the plain claim returns.
            && ErrorCheck.Run(r, null, 1000, null, new SweepScope(null, null, new[] { typeof(INpcGetter) }, "NPC_"),
                              ErrorFindingClass.Dangling).FilterNote is { } dOnlyNote
            && dOnlyNote.Contains("every count below is for THIS narrowed scope", StringComparison.Ordinal),
            $"note=[{scopedWithMasters.FilterNote}]");

        // #288 review finding 4: the counts_only json `unread` honesty list dropped trailing rows at the budget with NO
        // flag, while the TEXT render said "truncated" for the same result — a consumer iterating the array believed it
        // had the complete set of what could not be checked.
        var manyUnread = countsOnly with
        {
            Reports = Enumerable.Range(0, 40)
                .Select(i => new PluginErrors($"HcCeUnread{i}.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(),
                                              3, new[] { "some record — could not parse" }, null))
                .ToList(),
        };
        var unreadJson = JsonWire.RenderCheckErrors(manyUnread, 1500);
        Check("UNREAD-TRUNC-FLAG: a budget-cut counts_only json flags the shortened 'unread' list (total/rendered/truncated), never hands back a silently short honesty set",
            JsonUnreadTruncated(unreadJson, expectTotal: 40)
            && JsonUnreadTruncated(JsonWire.RenderCheckErrors(manyUnread, 0), expectTotal: 40, expectTruncated: false),
            $"json head=[{unreadJson.Split('\n').FirstOrDefault(l => l.Contains("truncated", StringComparison.Ordinal))}]");

        // #288 RE-REVIEW finding 3: findings= alone narrows which findings are COUNTED, not which records were swept —
        // the dangling total under findings=["dangling"] IS the complete whole-order figure, so the
        // "not the whole plugin(s)" claim would be false about the very number it sits under.
        Check("CLAIM-ONLY-WHEN-SCOPED: findings= alone lists the filter WITHOUT the not-the-whole-plugin claim; a record scope brings it back",
            danglingOnly.FilterNote is { } dNote
            && dNote.Contains("NARROWED to findings=[dangling]", StringComparison.Ordinal)
            && !dNote.Contains("not the whole plugin(s)", StringComparison.Ordinal)
            && !dNote.Contains("PLUGIN-level", StringComparison.Ordinal)
            && byFormid.FilterNote is { } sNote && sNote.Contains("NOT narrowed", StringComparison.Ordinal),
            $"class-only=[{danglingOnly.FilterNote}] scoped=[{byFormid.FilterNote}]");

        // #288 review finding 5: counts_only text returned before the excluded-plugins block.
        var countsExcluded = countsOnly with
        {
            ExcludedPlugins = new Dictionary<string, string> { ["HcCeBroken.esp"] = "header could not be parsed" },
        };
        Check("COUNTS-ONLY-EXCLUDED-NAMED: counts_only text still NAMES the unparseable plugins and their reasons, not just the header count",
            Wire.RenderCheckErrors(countsExcluded, 0) is var cxt
            && cxt.Contains("excluded plugins (could not be parsed", StringComparison.Ordinal)
            && cxt.Contains("HcCeBroken.esp", StringComparison.Ordinal)
            && cxt.Contains("header could not be parsed", StringComparison.Ordinal),
            $"line=[{Wire.RenderCheckErrors(countsExcluded, 0).Split('\n').FirstOrDefault(l => l.Contains("HcCeBroken", StringComparison.Ordinal)) ?? "<absent>"}]");

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

        // ---- BASELINE (#344): the LISTING BUDGET's phase order. limit= is ONE counter spent plugin by plugin in LOAD
        //      ORDER, and the base-game masters sit at index 0 — so once the vanilla baseline reaches the budget, a mod
        //      plugin's findings collect an EMPTY list, fail the report-inclusion test, and vanish from the output
        //      altogether (the reported defect: not "buried", unreachable). Its own order, built to that exact shape:
        //        • Skyrim.esm      — a base-game master (Mutagen's Implicits set matches by FILENAME, so a synthesized
        //          stub of that name IS the baseline here), 3 NPCs whose Race points at a dead id in its own space.
        //        • HcCeBaseMod.esp — masters [Skyrim], 2 NPCs pointing at that same dead id.
        //      Swept at limit=3 the baseline alone exhausts the budget, which is the whole point of the fixture.
        string baseDir = Path.Combine(tmpDir, "baseline");
        Directory.CreateDirectory(baseDir);
        string baseSkyrimPath = Path.Combine(baseDir, "Skyrim.esm");
        string baseModPath = Path.Combine(baseDir, "HcCeBaseMod.esp");
        var baseDeadFk = FormKey.Factory("0E0E0E:Skyrim.esm");   // an id the stub Skyrim.esm does NOT define
        try
        {
            var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < 3; i++) { var n = sky.Npcs.AddNew(); n.EditorID = $"HcCeVanillaNpc{i}"; n.Race.SetTo(baseDeadFk); }
            sky.BeginWrite.ToPath(baseSkyrimPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            using var skyOv = SkyrimMod.CreateFromBinaryOverlay(baseSkyrimPath, SkyrimRelease.SkyrimSE);
            var baseMod = new SkyrimMod(new ModKey("HcCeBaseMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < 2; i++) { var n = baseMod.Npcs.AddNew(); n.EditorID = $"HcCeModNpc{i}"; n.Race.SetTo(baseDeadFk); }
            baseMod.BeginWrite.ToPath(baseModPath).WithLoadOrder(new ISkyrimModGetter[] { skyOv }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the baseline fixture: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var rb = LoadOrderResolver.Build(new[] { baseSkyrimPath, baseModPath });
        var tight = ErrorCheck.Run(rb, null, 3);
        if (!tight.Success) { Console.Error.WriteLine($"error: baseline sweep failed: {tight.Error}"); return 1; }
        var baseModRep = tight.Reports.FirstOrDefault(p => p.Plugin == "HcCeBaseMod.esp");
        var vanillaRep = tight.Reports.FirstOrDefault(p => p.Plugin == "Skyrim.esm");

        Check("BASELINE-REACHABLE: with limit= fully consumable by the base-game baseline, the MOD plugin still appears with BOTH its dangling refs listed (#344)",
            baseModRep is not null && baseModRep.Dangling.Count == 2,
            baseModRep is null
                ? $"<no report for HcCeBaseMod.esp — it vanished from the sweep> reports=[{string.Join(",", tight.Reports.Select(p => p.Plugin))}] total={tight.TotalDangling}"
                : $"listed={baseModRep.Dangling.Count} of {tight.TotalDangling} total");

        var tightText = Wire.RenderCheckErrors(tight, 0);
        Check("BASELINE-TOTALS-EXACT: the totals still count every dangling ref (5) and the response still says the budget ran out",
            tight.TotalDangling == 5
            && tightText.Contains("were never listed: the listing budget (limit=3) ran out", StringComparison.Ordinal),
            $"total={tight.TotalDangling} vanillaListed={(vanillaRep is null ? -1 : vanillaRep.Dangling.Count)} line=[{AccountingLine(tightText)}]");

        var ample = ErrorCheck.Run(rb, null, 1000);
        var ampleText = Wire.RenderCheckErrors(ample, 0);
        var modOnly = ErrorCheck.Run(rb, new[] { "HcCeBaseMod.esp" }, 1000);
        var modOnlyText = Wire.RenderCheckErrors(modOnly, 0);
        var baseCounts = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.All, countsOnly: true);
        var baseCountsText = Wire.RenderCheckErrors(baseCounts, 0);
        var baseNoWalk = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.MissingMasters, countsOnly: true);

        Check("BASELINE-SECTIONS-LOAD-ORDER: the base master is swept LAST for budget but its section still renders in LOAD-ORDER position (the phase is not visible as a reordered report)",
            tight.Reports.Count == 2 && tight.Reports[0].Plugin == "Skyrim.esm" && tight.Reports[1].Plugin == "HcCeBaseMod.esp",
            $"sections=[{string.Join(",", tight.Reports.Select(p => p.Plugin))}]");

        Check("BASELINE-OMITTED-NAMED: the response names WHICH plugin is missing entries and how many — Skyrim.esm (2) — in text AND in json (Q3)",
            tightText.Contains("Missing here, by source plugin: Skyrim.esm (2)", StringComparison.Ordinal)
            && JsonRoster(JsonWire.RenderCheckErrors(tight, 0)).SequenceEqual(new[] { "Skyrim.esm:2" })
            // the roster names every plugin here, so the truncation clause must be ABSENT — the other arm of the
            // conditional OMITTED-ROSTER-STATES-ITS-TRUNCATION holds in the true direction.
            && !tightText.Contains("the rest are not named here", StringComparison.Ordinal),
            $"roster=[{string.Join(",", JsonRoster(JsonWire.RenderCheckErrors(tight, 0)))}] line=[{AccountingLine(tightText)}]");

        Check("BASELINE-SUMMARY: the split is stated (3 of 5, 2 from the rest) and names ONLY the base master this sweep actually opened — the four it never touched are not named (round-1 review: two reviewers, independently)",
            tight.BaselineDangling == 3 && tight.BaseMastersSwept is { Count: 1 } sw && sw[0] == "Skyrim.esm"
            && tightText.Contains("baseline: 3 of 5 dangling ref(s) come from the base-game master(s) this sweep covered (Skyrim.esm)", StringComparison.Ordinal)
            && ErrorCheck.BaseMasters.Where(m => m != "Skyrim.esm").All(m => !tightText.Contains(m, StringComparison.Ordinal))
            && tightText.Contains("2 come from the rest of the swept scope", StringComparison.Ordinal),
            $"baseline={tight.BaselineDangling} swept=[{string.Join(",", tight.BaseMastersSwept ?? Array.Empty<string>())}]");

        Check("BASELINE-NOT-IN-SCOPE: a sweep that never looked at a base master says nothing about baseline — 'none found' and 'never checked' must not read alike (Q3)",
            modOnly.BaseMastersSwept is { Count: 0 } && modOnly.BaselineDangling == 0
            && !modOnlyText.Contains("baseline:", StringComparison.Ordinal),
            $"swept={modOnly.BaseMastersSwept?.Count} text-has-line={modOnlyText.Contains("baseline:", StringComparison.Ordinal)}");

        Check("BASELINE-PHASE-CLAUSE-ONLY-WHEN-IT-BIT: the budget-order sentence appears on the capped sweep and NOT on the sweep that listed everything it found",
            tightText.Contains("the listing budget (limit=) is spent on every other plugin BEFORE those", StringComparison.Ordinal)
            && !ampleText.Contains("the listing budget (limit=) is spent", StringComparison.Ordinal)
            && ampleText.Contains("baseline: 3 of 5", StringComparison.Ordinal),
            $"capped-has={tightText.Contains("BEFORE those", StringComparison.Ordinal)} ample-has={ampleText.Contains("the listing budget (limit=) is spent", StringComparison.Ordinal)}");

        Check("BASELINE-AMPLE-BUDGET-UNCHANGED: with limit= ample, EVERY plugin's findings are listed in full and the response says so positively — completeness is STATED, not left to the absence of a sentence (#361)",
            ample.Reports.Sum(p => p.Dangling.Count) == 5
            && ampleText.Contains("all 5 dangling ref(s) found by this sweep appear above", StringComparison.Ordinal)
            && !ampleText.Contains("Missing here, by source plugin", StringComparison.Ordinal)
            && !ampleText.Contains("Raise limit= to list more", StringComparison.Ordinal),
            $"listed={ample.Reports.Sum(p => p.Dangling.Count)} line=[{AccountingLine(ampleText)}]");

        Check("SOURCE-HISTOGRAM: counts_only tallies dangling refs by SOURCE plugin (3 vanilla, 2 mod) beside the existing TARGET axis, and renders both",
            baseCounts.DanglingBySource is { Count: 2 } src
            && src.First(c => c.Key == "Skyrim.esm").Count == 3 && src.First(c => c.Key == "HcCeBaseMod.esp").Count == 2
            && baseCountsText.Contains("by SOURCE plugin", StringComparison.Ordinal)
            && baseCountsText.Contains("by TARGET plugin", StringComparison.Ordinal),
            baseCounts.DanglingBySource is null ? "<null>" : string.Join(",", baseCounts.DanglingBySource.Select(c => $"{c.Key}:{c.Count}")));

        Check("SOURCE-HISTOGRAM-NOT-COMPUTED: findings=[missing_masters] leaves BOTH axes null and the render says the walk was not run — an absent axis must not read as an empty one (Q3)",
            baseNoWalk.DanglingBySource is null && baseNoWalk.Histogram is null
            && Wire.RenderCheckErrors(baseNoWalk, 0).Contains("by target or by source — the link walk was not run", StringComparison.Ordinal),
            $"source={(baseNoWalk.DanglingBySource is null ? "null" : "present")} target={(baseNoWalk.Histogram is null ? "null" : "present")}");

        Check("OMITTED-NULL-UNDER-COUNTS-ONLY: counts_only lists nothing BY DESIGN, so it reports no omissions rather than reporting the whole sweep as dropped",
            !baseCountsText.Contains("found by this sweep appear above", StringComparison.Ordinal)
            && !baseCountsText.Contains("Missing here, by source plugin", StringComparison.Ordinal),
            $"line=[{AccountingLine(baseCountsText)}]");

        // REACH (round-1 review, partially refused): this arm holds the set's CONTENTS — that it equals Mutagen's for
        // this release, matches case-insensitively, and excludes Creation Club / _ResourcePack. It does NOT hold
        // PROVENANCE: a hand-kept literal matching today's five names would pass it. Holding provenance needs an
        // assertion over ErrorCheck.cs's source text, an idiom this repo does not have anywhere; introducing one for
        // this is a guard-design call, not a fold. The label says contents, so it cannot be read as more.
        Check("BASE-SET-CONTENTS: the baseline set equals Mutagen's Implicits.BaseMasters for this release, case-insensitively, and excludes CC / _ResourcePack (#344 settled decision 2 — contents, not provenance; see the comment above)",
            ErrorCheck.BaseMasters.SequenceEqual(
                Mutagen.Bethesda.Plugins.Implicits.Get(Mutagen.Bethesda.GameRelease.SkyrimSE).BaseMasters.Select(m => m.FileName.String))
            && ErrorCheck.IsBaseMaster("skyrim.esm") && !ErrorCheck.IsBaseMaster("_ResourcePack.esl")
            && !ErrorCheck.IsBaseMaster("ccBGSSSE001-Fish.esm"),
            $"set=[{string.Join(",", ErrorCheck.BaseMasters)}]");

        // ---- the class gate on the baseline line: with the walk skipped there is no split to state, and "0 of 0 come
        //      from the base-game masters" two lines under "dangling refs NOT CHECKED" is a skipped check reading as a
        //      clean one — the exact Q3 break this tool exists to catch (round-1 review: the gate was hollow).
        Check("BASELINE-CLASS-GATE: findings=[missing_masters] prints NO baseline line — a class nobody looked for must not come back as 0 of 0",
            !Wire.RenderCheckErrors(baseNoWalk, 0).Contains("baseline:", StringComparison.Ordinal)
            && baseNoWalk.BaselineDangling == 0,
            $"text-has-line={Wire.RenderCheckErrors(baseNoWalk, 0).Contains("baseline:", StringComparison.Ordinal)}");

        // ---- the OFF-ORDER lane's contribution to both new fields. The pre-enable verify lane is the only place
        //      off-order files exist, and nothing tied it to the source axis or to the swept-baseline subset.
        var offSrc = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, new[] { ("HcCePatch.esp", patchPath) },
                                    null, ErrorFindingClass.All, countsOnly: true);
        Check("OFF-ORDER-SOURCE-AXIS: an off-order file's dangling refs are tallied by SOURCE under that file's own name — a table that silently omitted them would be wrong, not merely absent",
            offSrc.DanglingBySource is { } os && os.Any(c => c.Key == "HcCePatch.esp" && c.Count == 2)
            && os.Sum(c => c.Count) == offSrc.TotalDangling,
            offSrc.DanglingBySource is null ? "<null>" : string.Join(",", offSrc.DanglingBySource.Select(c => $"{c.Key}:{c.Count}")));

        var offBase = ErrorCheck.Run(r, new[] { "HcCeBad.esp" }, 1000, new[] { ("Skyrim.esm", baseSkyrimPath) });
        Check("OFF-ORDER-BASELINE-SWEPT: a base master swept as a FILE counts as baseline — BaselineDangling and the swept set agree, rather than one saying vanilla was measured and the other saying it was never opened",
            offBase.BaseMastersSwept is { Count: 1 } obs && obs[0] == "Skyrim.esm" && offBase.BaselineDangling == 3
            && Wire.RenderCheckErrors(offBase, 0).Contains("this sweep covered (Skyrim.esm)", StringComparison.Ordinal),
            $"swept=[{string.Join(",", offBase.BaseMastersSwept ?? Array.Empty<string>())}] baseline={offBase.BaselineDangling}");

        // ---- the headline scenario itself: a plugin whose WHOLE set is dropped, which therefore has no section at all.
        //      At limit=2 the mod takes the entire budget and Skyrim.esm gets nothing — the shape the rendered sentence
        //      describes, which no fixture previously produced (round-1 review).
        var whole = ErrorCheck.Run(rb, null, 2);
        var wholeText = Wire.RenderCheckErrors(whole, 0);
        Check("BASELINE-WHOLE-SET-DROPPED: a plugin that loses its ENTIRE set has no section, and the omissions list is the only place it appears — the sentence the render prints, produced",
            whole.Reports.All(p => p.Plugin != "Skyrim.esm")
            && JsonRoster(JsonWire.RenderCheckErrors(whole, 0)).Contains("Skyrim.esm:3")
            && wholeText.Contains("Skyrim.esm (3)", StringComparison.Ordinal)
            && wholeText.Contains("A plugin whose whole set is missing here, with nothing else to report, gets no section of its own", StringComparison.Ordinal),
            $"sections=[{string.Join(",", whole.Reports.Select(p => p.Plugin))}] roster=[{string.Join(",", JsonRoster(JsonWire.RenderCheckErrors(whole, 0)))}]");

        // ---- a duplicated plugins= name is swept twice (pre-existing on main). The omissions table must not read the
        //      second sweep's section as the whole of what was listed and report the first sweep's findings as dropped
        //      by a budget that never ran out (round-1 review).
        var dup = ErrorCheck.Run(rb, new[] { "HcCeBaseMod.esp", "HcCeBaseMod.esp" }, 1000);
        var dupText = Wire.RenderCheckErrors(dup, 0);
        Check("OMITTED-NO-PHANTOM-ON-DUPLICATE-SCOPE: an uncapped sweep reports NOTHING missing even when a plugin is named twice — a response that listed everything cannot have dropped anything",
            dupText.Contains("found by this sweep appear above", StringComparison.Ordinal)
            && !dupText.Contains("Missing here, by source plugin", StringComparison.Ordinal)
            && JsonRoster(JsonWire.RenderCheckErrors(dup, 0)).Length == 0,
            $"line=[{AccountingLine(dupText)}]");

        // ---- json's omissions table must not be capped by limit=, the knob that caused the omissions: the tighter the
        //      budget, the more plugins lose entries and the fewer json would have named (round-1 review).
        var tiny = ErrorCheck.Run(rb, null, 1);
        var tinyJson = JsonWire.RenderCheckErrors(tiny, 0);
        Check("OMITTED-JSON-NOT-LIMIT-CAPPED: at limit=1 the json still names BOTH plugins missing entries — the roster does not shrink as the budget does (the tighter the budget, the MORE plugins lose entries)",
            JsonRoster(tinyJson).Length == 2
            && JsonDocument.Parse(tinyJson).RootElement.GetProperty("accounting")
                           .GetProperty("dangling_missing_by_source_total").GetInt32() == 2,
            $"roster=[{string.Join(",", JsonRoster(tinyJson))}]");

        var tightJson = JsonWire.RenderCheckErrors(tight, 0);
        var countsJson = JsonWire.RenderCheckErrors(baseCounts, 0);
        var noWalkBaseJson = JsonWire.RenderCheckErrors(baseNoWalk, 0);
        Check("BASELINE-JSON-PARITY: the json carries the same split, names the base set, flags scope, and carries both new tables; an unchecked class emits null (not 0)",
            JsonMatches(tightJson, "baseline_dangling", 3) && JsonMatches(tightJson, "non_baseline_dangling", 2)
            && JsonRoster(tightJson).Length > 0
            && JsonHasHistogram(countsJson, "dangling_by_source_plugin")
            && JsonNull(noWalkBaseJson, "baseline_dangling")
            && JsonDocument.Parse(tightJson).RootElement.GetProperty("base_masters_swept").GetArrayLength() == 1
            && JsonDocument.Parse(JsonWire.RenderCheckErrors(modOnly, 0)).RootElement.GetProperty("base_masters_swept").GetArrayLength() == 0
            && JsonDocument.Parse(tightJson).RootElement.GetProperty("base_masters").GetArrayLength() == ErrorCheck.BaseMasters.Count,
            "see json render");


        // ---- MANY-OMITTED fixture: the roster's own truncation. Twelve mod plugins with three dangling refs each, so a
        //      tight budget drops entries across more plugins than the capped line names. The line must SAY it did not
        //      name them all — a truncated roster claiming to be the only place those plugins appear is this very
        //      defect rebuilt one level down (round-1 review; on the real order 45 plugins lost entries).
        string manyDir = Path.Combine(tmpDir, "many");
        Directory.CreateDirectory(manyDir);
        var manyPaths = new List<string>();
        try
        {
            var skyMany = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
            skyMany.Races.AddNew();
            string skyManyPath = Path.Combine(manyDir, "Skyrim.esm");
            skyMany.BeginWrite.ToPath(skyManyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            manyPaths.Add(skyManyPath);
            using var skyManyOv = SkyrimMod.CreateFromBinaryOverlay(skyManyPath, SkyrimRelease.SkyrimSE);
            for (int i = 0; i < 12; i++)
            {
                var m = new SkyrimMod(new ModKey($"HcCeMany{i:00}", ModType.Plugin), SkyrimRelease.SkyrimSE);
                for (int k = 0; k < 3; k++) { var n = m.Npcs.AddNew(); n.EditorID = $"HcCeMany{i:00}Npc{k}"; n.Race.SetTo(baseDeadFk); }
                string mp = Path.Combine(manyDir, $"HcCeMany{i:00}.esp");
                m.BeginWrite.ToPath(mp).WithLoadOrder(new ISkyrimModGetter[] { skyManyOv }).Write();
                manyPaths.Add(mp);
            }
            // ONE plugin whose own findings outrun any sane cap. Twelve plugins of three refs cannot produce #361's
            // shape: the cut has to land INSIDE a section, and with one section it lands inside the LAST one, which
            // is the path where the old notice was never printed at all. On the live order this shape is ordinary —
            // the largest single source carries 2591 dangling refs.
            var bulk = new SkyrimMod(new ModKey("HcCeBulk", ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int k = 0; k < 200; k++) { var n = bulk.Npcs.AddNew(); n.EditorID = $"HcCeBulkNpc{k:000}"; n.Race.SetTo(baseDeadFk); }
            string bulkPath = Path.Combine(manyDir, "HcCeBulk.esp");
            bulk.BeginWrite.ToPath(bulkPath).WithLoadOrder(new ISkyrimModGetter[] { skyManyOv }).Write();
            manyPaths.Add(bulkPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the many-omitted fixture: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var rm = LoadOrderResolver.Build(manyPaths);
        var many = ErrorCheck.Run(rm, null, 4);
        var manyText = Wire.RenderCheckErrors(many, 0);
        // Count names in the CAPPED LINE, not the whole render: every listed dangling entry also prints its source as
        // "000800:HcCeMany00.esp (Npc ...", so a whole-text match counts plugins the roster never named.
        string cappedLine = AccountingLine(manyText);
        var manyRoster = JsonRoster(JsonWire.RenderCheckErrors(many, 0));
        int manyRosterTotal = JsonDocument.Parse(JsonWire.RenderCheckErrors(many, 0)).RootElement
                                          .GetProperty("accounting").GetProperty("dangling_missing_by_source_total").GetInt32();
        int namedInText = manyRoster.Count(k => cappedLine.Contains(k.Split(':')[0] + " (", StringComparison.Ordinal));
        Check("OMITTED-ROSTER-STATES-ITS-TRUNCATION: with more plugins missing entries than the line names, it says how many it did NOT name and never claims to be the only place they appear",
            manyRosterTotal > 10
            && manyRoster.Length == 10 && namedInText == 10
            && manyText.Contains("the 10 largest of " + manyRosterTotal, StringComparison.Ordinal)
            && manyText.Contains("the rest are not named here", StringComparison.Ordinal)
            && !manyText.Contains("this list is the only place", StringComparison.Ordinal),
            $"rosterTotal={manyRosterTotal} named={namedInText} line=[{cappedLine}]");

        // The remedy is assembled from the causes that FIRED. This sweep is capped by limit= and not by max_chars,
        // so it must offer the budget knob and must NOT offer the response knob — a knob named beside a cause it did
        // not move is a remedy the caller can follow and land in the same place.
        Check("OMITTED-REMEDY-NAMES-ONLY-THE-CAUSE-THAT-FIRED: a budget-capped response offers limit= and not max_chars=, and its scoping clause names BOTH bounds rather than promising a full listing",
            manyText.Contains("Raise limit= to list more.", StringComparison.Ordinal)
            && !manyText.Contains("Raise max_chars= to fit more", StringComparison.Ordinal)
            && manyText.Contains("depends on limit= and on max_chars=, which both still apply", StringComparison.Ordinal)
            && !manyText.Contains("lists its set in full unless", StringComparison.Ordinal),
            cappedLine.Length == 0 ? "<no accounting line>" : cappedLine);

        // ---- CLEAN-BASELINE fixture: a base master with NO dangling refs, on a sweep the budget still caps. The
        //      phase-order sentence talks about baseline findings crowding the list; with none found there is nothing
        //      for it to describe, and only this fixture exercises that half of the conditional (round-1 review).
        string cleanDir = Path.Combine(tmpDir, "cleanbase");
        Directory.CreateDirectory(cleanDir);
        string cleanSkyPath = Path.Combine(cleanDir, "Skyrim.esm");
        string cleanModPath = Path.Combine(cleanDir, "HcCeCleanBaseMod.esp");
        try
        {
            var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
            sky.Races.AddNew();                                  // no dangling refs of its own
            sky.BeginWrite.ToPath(cleanSkyPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            using var skyOv = SkyrimMod.CreateFromBinaryOverlay(cleanSkyPath, SkyrimRelease.SkyrimSE);
            var m = new SkyrimMod(new ModKey("HcCeCleanBaseMod", ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int k = 0; k < 3; k++) { var n = m.Npcs.AddNew(); n.EditorID = $"HcCeCleanBaseNpc{k}"; n.Race.SetTo(baseDeadFk); }
            m.BeginWrite.ToPath(cleanModPath).WithLoadOrder(new ISkyrimModGetter[] { skyOv }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize the clean-baseline fixture: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var rc = LoadOrderResolver.Build(new[] { cleanSkyPath, cleanModPath });
        var cleanBase = ErrorCheck.Run(rc, null, 2);
        var cleanBaseText = Wire.RenderCheckErrors(cleanBase, 0);
        Check("BASELINE-PHASE-CLAUSE-NEEDS-BASELINE-FINDINGS: a capped sweep whose baseline came back CLEAN states the split but not the phase-order sentence — there were no baseline findings for it to be about",
            cleanBaseText.Contains("were never listed: the listing budget (limit=2) ran out", StringComparison.Ordinal)
            && cleanBase.BaselineDangling == 0 && cleanBase.BaseMastersSwept is { Count: 1 }
            && cleanBaseText.Contains("baseline: 0 of 3", StringComparison.Ordinal)
            && !cleanBaseText.Contains("the listing budget (limit=) is spent", StringComparison.Ordinal),
            $"baseline={cleanBase.BaselineDangling} line=[{AccountingLine(cleanBaseText)}]");
        // ---- both axes empty: they must be tellable apart. Two identical untitled "nothing to tally" lines said
        //      neither which axis was which nor that a second one had been computed (round-1 review).
        var cleanCounts = ErrorCheck.Run(r, new[] { "HcCeClean.esp" }, 1000, null, null, ErrorFindingClass.All, countsOnly: true);
        var cleanCountsText = Wire.RenderCheckErrors(cleanCounts, 0);
        Check("EMPTY-AXES-TITLED: when both histograms come back empty, each empty line still names its own axis",
            cleanCountsText.Contains("by TARGET plugin (the plugin the broken refs point INTO): nothing to tally", StringComparison.Ordinal)
            && cleanCountsText.Contains("by SOURCE plugin (the plugin the broken refs come FROM): nothing to tally", StringComparison.Ordinal),
            cleanCountsText);


        // The report SHAPE is synthesized rather than swept: no fixture plugin declares an absent master, and what
        // is under test is the render's behaviour when a masters-only sweep has more sections than fit.
        var mastersMany = ErrorCheck.Run(rm, null, 1000, null, null, ErrorFindingClass.MissingMasters) with
        {
            Reports = Enumerable.Range(0, 12).Select(i => new PluginErrors(
                $"HcCeMasters{i:00}.esp", Array.Empty<DanglingRef>(),
                new[] { "HcCeAbsentMaster.esm", "HcCeAlsoAbsent.esm" }, 0, Array.Empty<string>(), null)).ToList(),
            TotalMissingMasters = 24,
        };

        // ---- the response's cut and the budget's are ONE accounting now: both are subtractions against the sweep's
        //      own total, taken after emission stops, so "how many can I see" is a stated number rather than one the
        //      caller derives from two sentences in different units (#361, and the class-stop that produced it).
        // an ample budget so every section exists, then a cap small enough that the RENDER is what drops them
        var manyAmple = ErrorCheck.Run(rm, null, 1000);
        // The cap must clear the RESERVE — the accounting and boundary are held back before the body renders, so a
        // cap at or below the reserve renders an empty body and would let this arm "pass" over a response with
        // nothing in it. 3000 leaves room for a partial listing over this fixture's 236 findings.
        const int MultiCut = 3000;
        var truncText = Wire.RenderCheckErrors(manyAmple, MultiCut);
        int renderedSections = truncText.Split("[ERROR] ").Length - 1;
        int visibleEntries = truncText.Split("-> ").Length - 1;
        Check("RESPONSE-CUT-COUNTED-AT-THE-TOTAL: a response cut by max_chars states how many of the SWEEP's findings it carries, names max_chars as the cause, and counts the sections it rendered",
            truncText.Contains($"{visibleEntries} of the {manyAmple.TotalDangling} dangling ref(s) found by this sweep appear above", StringComparison.Ordinal)
            && truncText.Contains($"did not fit this response (max_chars={MultiCut})", StringComparison.Ordinal)
            && truncText.Contains($"{renderedSections} of {manyAmple.Reports.Count} plugin section(s) were rendered", StringComparison.Ordinal)
            // the budget was ample, so it dropped nothing and must not be named as a cause at all
            && !truncText.Contains("the listing budget (limit=", StringComparison.Ordinal),
            AccountingLine(truncText));

        Check("RESPONSE-CUT-ENTRY-COUNT-IS-WHAT-IT-EMITTED: the visible figure equals the dangling lines actually in the response, not the sum of the sections it started — a section sum would overstate a partial last section",
            truncText.Contains($"[accounting: {visibleEntries} of the ", StringComparison.Ordinal)
            && visibleEntries > 0 && visibleEntries < manyAmple.TotalDangling,
            $"lines={visibleEntries} total={manyAmple.TotalDangling} line=[{AccountingLine(truncText)}]");

        // #361's own lane: ONE report section, so the cut lands inside the LAST one. The outer loop then exhausts
        // instead of breaking, which is exactly the path that used to leave the notice unprinted — measured on the
        // live order at plain defaults as 644 entries present under a line claiming 1000 were listed.
        const int OneCut = 5000;
        var oneSection = ErrorCheck.Run(rm, new[] { "HcCeBulk.esp" }, 1000);
        var oneText = Wire.RenderCheckErrors(oneSection, OneCut);
        int oneVisible = oneText.Split("-> ").Length - 1;
        Check("RESPONSE-CUT-INSIDE-THE-LAST-SECTION-IS-STATED (#361): with a single section, a cut inside it is still accounted — the claim is a subtraction at the total, so there is no exit for it to be missed at",
            oneSection.Reports.Count == 1 && oneVisible < oneSection.TotalDangling
            && oneText.Contains($"[accounting: {oneVisible} of the {oneSection.TotalDangling} dangling ref(s) found by this sweep appear above", StringComparison.Ordinal)
            && oneText.Contains($"did not fit this response (max_chars={OneCut})", StringComparison.Ordinal),
            $"sections={oneSection.Reports.Count} visible={oneVisible} of {oneSection.TotalDangling} line=[{AccountingLine(oneText)}]");

        // Every lane, not just the listing one. counts_only renders two histograms that took limit= as their only
        // bound — measured at 27,944 chars against a max_chars of 5,000 — and a plugin whose FIXED part is fat
        // (a scan error plus three unscannable-record exception messages) is the shape that broke the json lane's
        // per-plugin test. Both are in the sweep, so the invariant is claimed over the whole surface.
        var fatHead = ErrorCheck.Run(rm, null, 1000) with
        {
            Reports = ErrorCheck.Run(rm, null, 1000).Reports.Select((x, i) => i == 1
                ? new PluginErrors(x.Plugin, x.Dangling, x.MissingMasters, 3,
                                   Enumerable.Range(0, 3).Select(k => new string('s', 950) + k).ToList(),
                                   new string('e', 900))
                : x).ToList(),
        };
        // Evaluated eagerly, never short-circuited: a sweep that never runs cannot fail, and the detail below has
        // to be able to say which lane broke.
        bool capListing = CapSweep(manyAmple, out var capFail);
        bool capFat = CapSweep(fatHead, out var fatFail);
        bool capCounts = CapSweep(ErrorCheck.Run(rm, null, 1000, null, null, ErrorFindingClass.All, true), out var countsFail);
        bool capMasters = CapSweep(mastersMany, out var mastersFail);
        Check("RESPONSE-NEVER-EXCEEDS-MAX-CHARS (#361): the accounting and the boundary are RESERVED before the body renders, so neither is appended past the cap — across a sweep of caps, in every lane, in both transports",
            capListing && capFat && capCounts && capMasters,
            $"listing=[{capFail}] fat-head=[{fatFail}] counts_only=[{countsFail}] masters-only=[{mastersFail}]");

        Check("SECTION-IS-WHOLE-OR-ABSENT: across a sweep of caps, a section that starts also carries everything it has to say — the only things a cut may drop are a whole section or an entry, which are the two the accounting states",
            SectionsWhole(new[] { manyAmple, tight, ErrorCheck.Run(r, null, 1000) }, out var wholeFail), wholeFail);

        Check("RESPONSE-CUT-COMPLETE-RESPONSE-SAYS-SO: an uncut, unbudgeted response states its completeness rather than staying silent — silence used to mean both 'complete' and #361",
            manyText.Contains("dangling ref(s) found by this sweep appear above", StringComparison.Ordinal)
            && !manyText.Contains("did not fit this response", StringComparison.Ordinal)
            && !manyText.Contains("plugin section(s) were rendered", StringComparison.Ordinal),
            AccountingLine(manyText));

        // json states the same numbers, from the same computation — the twin cannot disagree because there is only
        // one place either transport gets them.
        var truncJson = JsonDocument.Parse(JsonWire.RenderCheckErrors(manyAmple, MultiCut)).RootElement;
        var jAcct = truncJson.GetProperty("accounting");
        Check("RESPONSE-CUT-JSON-NUMBERS-MATCH-THE-DOCUMENT: dangling_visible equals the entries actually in the json, and sections_rendered the plugin objects — a number that disagrees with its own document is the defect this replaces",
            jAcct.GetProperty("dangling_visible").GetInt32()
                == truncJson.GetProperty("plugins").EnumerateArray().Sum(pl => pl.GetProperty("dangling").GetArrayLength())
            && jAcct.GetProperty("sections_rendered").GetInt32() == truncJson.GetProperty("plugins").GetArrayLength()
            && jAcct.GetProperty("sections_with_findings").GetInt32() == manyAmple.Reports.Count
            && jAcct.GetProperty("dangling_missing_by_response_cut").GetInt32() > 0
            && jAcct.GetProperty("dangling_missing_by_budget").GetInt32() == 0,
            $"visible={jAcct.GetProperty("dangling_visible").GetInt32()} inDoc={truncJson.GetProperty("plugins").EnumerateArray().Sum(pl => pl.GetProperty("dangling").GetArrayLength())} rendered={jAcct.GetProperty("sections_rendered").GetInt32()} objects={truncJson.GetProperty("plugins").GetArrayLength()}");

        // #361's json lane: one plugin carrying more entries than the cap can hold. The budget used to be tested
        // before each plugin OBJECT, so the whole array went out at once — 2.5x the cap on the live order, with
        // truncated:false, which was true and useless.
        var oneJson = JsonDocument.Parse(JsonWire.RenderCheckErrors(oneSection, OneCut)).RootElement;
        Check("RESPONSE-CUT-JSON-PER-ENTRY (#361): one plugin's dangling array is cut at an ENTRY, so a single oversized plugin cannot carry the response past max_chars",
            JsonWire.RenderCheckErrors(oneSection, OneCut).Length <= OneCut
            && oneJson.GetProperty("plugins").EnumerateArray().Sum(pl => pl.GetProperty("dangling").GetArrayLength()) < oneSection.TotalDangling
            && oneJson.GetProperty("truncated").GetBoolean(),
            $"chars={JsonWire.RenderCheckErrors(oneSection, OneCut).Length} cap={OneCut} truncated={oneJson.GetProperty("truncated").GetBoolean()} overrun={oneJson.TryGetProperty("max_chars_overrun", out _)} entries={oneJson.GetProperty("plugins").EnumerateArray().Sum(pl => pl.GetProperty("dangling").GetArrayLength())} of {oneSection.TotalDangling}");

        // ---- #344's exclusion axis. Applied to the SWEEP, so an excluded plugin costs no walk and no budget — the
        //      half of #344 the phase order could not reach. The BASELINE definition is untouched by it.
        var exBase = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.All, false,
                                    Excl("Skyrim.esm"));
        var exBaseText = Wire.RenderCheckErrors(exBase, 0);
        Check("EXCLUDE-NAME: a named plugin is left out of the sweep entirely — its findings are gone from the TOTALS, not merely from the listing, and the response says the scope was narrowed",
            exBase.Success && exBase.TotalDangling == 2 && exBase.Reports.All(x => x.Plugin != "Skyrim.esm")
            && exBase.BaseMastersSwept is { Count: 0 }
            && exBaseText.Contains("exclude= left out 1 plugin(s)", StringComparison.Ordinal)
            && !exBaseText.Contains("baseline:", StringComparison.Ordinal),
            $"total={exBase.TotalDangling} sections=[{string.Join(",", exBase.Reports.Select(x => x.Plugin))}] note={exBase.FilterNote}");

        var (baseTok, baseTokErr) = SweepExclusion.Resolve(new[] { SweepExclusion.BaseMastersToken }, Array.Empty<string>());
        Check("EXCLUDE-TOKEN-BASE-MASTERS: the group resolves to Mutagen's base-master set exactly — the same list the baseline split is defined by, never a second hand-kept copy",
            baseTokErr is null && baseTok is not null
            && new HashSet<string>(baseTok.Names).SetEquals(ErrorCheck.BaseMasters)
            // a GROUP contributes no typed names — nothing here is a claim that a specific plugin is in scope
            && baseTok.TypedNames.Count == 0,
            $"err={baseTokErr} resolved=[{string.Join(",", baseTok?.Names ?? Array.Empty<string>())}] typed={baseTok?.TypedNames.Count}");

        var (implTok, implTokErr) = SweepExclusion.Resolve(
            new[] { SweepExclusion.ImplicitToken }, new[] { "Skyrim.esm", "ccBGSSSE001-Fish.esm", "_ResourcePack.esl" });
        Check("EXCLUDE-TOKEN-IMPLICIT: the group resolves to what the ORDER force-loads — where Creation Club and _ResourcePack live — and it is a superset of the base masters rather than a rival definition of vanilla",
            implTokErr is null && implTok is not null
            && implTok.Names.Contains("ccBGSSSE001-Fish.esm") && implTok.Names.Contains("_ResourcePack.esl")
            && implTok.Names.Contains("Skyrim.esm") && implTok.TypedNames.Count == 0,
            $"err={implTokErr} resolved=[{string.Join(",", implTok?.Names ?? Array.Empty<string>())}]");

        var (_, unknownErr) = SweepExclusion.Resolve(new[] { "vanilla" }, Array.Empty<string>());
        Check("EXCLUDE-UNKNOWN-GROUP-REFUSES: a value that is neither a filename nor a known group refuses BEFORE the sweep and names both what a filename looks like and every group there is",
            unknownErr is not null && unknownErr.Contains("'vanilla'", StringComparison.Ordinal)
            && SweepExclusion.Tokens.All(t => unknownErr.Contains(t, StringComparison.Ordinal))
            && unknownErr.Contains("Nothing was swept", StringComparison.Ordinal),
            unknownErr ?? "<no refusal>");

        // §3.1's standing S1 exemption is PINNED, not asserted: tokens carry no sigil because a plugin name is
        // extension-mandatory, so an extensionless spelling must refuse by name and never fuzzy-match a plugin whose
        // stem it resembles. The with-extension control sits beside it, or the arm proves only that nothing matches.
        var (_, stemErr) = SweepExclusion.Resolve(new[] { "Skyrim" }, Array.Empty<string>());
        var (stemOk, stemOkErr) = SweepExclusion.Resolve(new[] { "Skyrim.esm" }, Array.Empty<string>());
        Check("EXCLUDE-EXTENSIONLESS-NEVER-FUZZY-MATCHES: 'Skyrim' is refused as an unknown group and never resolved to Skyrim.esm; 'Skyrim.esm' is taken as the name it is",
            stemErr is not null && stemErr.Contains("'Skyrim'", StringComparison.Ordinal)
            && stemOkErr is null && stemOk is not null
            && new HashSet<string>(stemOk.Names).SetEquals(new[] { "Skyrim.esm" })
            // and a NAME is typed, which is what makes it a claim the scope must satisfy
            && stemOk.TypedNames.SequenceEqual(new[] { "Skyrim.esm" }),
            $"stem=[{stemErr}] withExt=[{stemOkErr}] resolved=[{string.Join(",", stemOk?.Names ?? Array.Empty<string>())}]");

        var exMiss = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.All, false, Excl("HcCeNotHere.esp"));
        Check("EXCLUDE-NAME-NOT-IN-SCOPE-REFUSES: an exclusion matching nothing is a refusal, not a no-op — the silent reading returns exactly the findings the caller asked to leave out, with nothing to say the spelling missed",
            !exMiss.Success && exMiss.Error is not null
            && exMiss.Error.Contains("HcCeNotHere.esp", StringComparison.Ordinal)
            && exMiss.Error.Contains("not in the scope this sweep would cover", StringComparison.Ordinal)
            && exMiss.Reports.Count == 0,
            exMiss.Error ?? "<no refusal>");

        var exAll = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.All, false,
                                   Excl("Skyrim.esm", "HcCeBaseMod.esp"));
        Check("EXCLUDE-EMPTIES-THE-SCOPE-REFUSES: excluding everything in scope refuses with the count it removed, rather than returning a clean-looking sweep of nothing (Q3)",
            !exAll.Success && exAll.Error is not null
            && exAll.Error.Contains("removed every plugin", StringComparison.Ordinal)
            && exAll.Error.Contains("nothing left to check", StringComparison.Ordinal),
            exAll.Error ?? "<no refusal>");

        // The separation the refusal above depends on: a GROUP member that is not in scope is the ordinary case,
        // and only a name the CALLER TYPED is a claim the scope has to satisfy. Expanded and validated as one list,
        // any narrowed plugins= refused any group token, naming a plugin the caller never wrote.
        var exGroupNarrow = ErrorCheck.Run(rb, new[] { "HcCeBaseMod.esp" }, 1000, null, null,
                                           ErrorFindingClass.All, false, Excl("base_masters"));
        Check("EXCLUDE-GROUP-MEMBER-NOT-IN-SCOPE-IS-NOT-A-REFUSAL: a group token over a narrowed scope drops whichever members are present and says nothing about the rest — only a TYPED name must be in scope",
            exGroupNarrow.Success && exGroupNarrow.PluginsScanned == 1
            && exGroupNarrow.Reports.Any(x => x.Plugin == "HcCeBaseMod.esp"),
            $"success={exGroupNarrow.Success} err=[{exGroupNarrow.Error}] scanned={exGroupNarrow.PluginsScanned}");

        // A group can legitimately match nothing here. That is not an error — but the response must still say the
        // scope was narrowed, or exclude= leaves no trace and the caller cannot tell it was honoured.
        var exGroupEmpty = ErrorCheck.Run(rb, new[] { "HcCeBaseMod.esp" }, 1000, null, null,
                                          ErrorFindingClass.All, false, Excl("base_masters"));
        Check("EXCLUDE-EMPTY-GROUP-STILL-NOTED: an exclusion that removes nothing from THIS scope is still reported as a narrowing — an exclude= that leaves no trace reads as one that was ignored",
            exGroupEmpty.FilterNote is { } egn && egn.Contains("exclude= left out", StringComparison.Ordinal),
            $"note=[{exGroupEmpty.FilterNote}]");

        var (_, blankErr) = SweepExclusion.Resolve(new[] { "  " }, Array.Empty<string>());
        Check("EXCLUDE-BLANK-VALUE-REFUSES: a blank entry refuses and names both shapes, rather than being skipped as though it were absent",
            blankErr is not null && blankErr.Contains("blank value", StringComparison.Ordinal)
            && SweepExclusion.Tokens.All(t => blankErr.Contains(t, StringComparison.Ordinal)),
            blankErr ?? "<no refusal>");

        Check("EXCLUDE-NAME-CASE-INSENSITIVE: a filename is recognised whatever its case, because every other plugin-name match in this sweep is case-insensitive",
            SweepExclusion.IsPluginName("MyMod.ESP") && SweepExclusion.IsPluginName("MyMod.EsM")
            && !SweepExclusion.IsPluginName("MyMod"),
            $"ESP={SweepExclusion.IsPluginName("MyMod.ESP")} EsM={SweepExclusion.IsPluginName("MyMod.EsM")} bare={SweepExclusion.IsPluginName("MyMod")}");

        Check("EXCLUDE-DESCRIPTION-NAMES-EVERY-GROUP: the caller-facing token list is held against SweepExclusion.Tokens, the one place they are enumerated — no wire-name guard reaches a tool parameter's prose (#386), so this parameter brings its own",
            ExcludeDescriptionNamesTokens(out var descDetail), descDetail);

        // ---- the accounting's central identity, on a fixture where BOTH causes fired. Every other cell is cut by
        //      one truncator or the other; the live-order default shape is both at once, and it is the shape the
        //      class exists for. Without this, the second cause clause could be dropped outright and stay green.
        var bothCut = ErrorCheck.Run(rm, null, 100);
        var bothText = Wire.RenderCheckErrors(bothCut, 4000);
        var bothJson = JsonDocument.Parse(JsonWire.RenderCheckErrors(bothCut, 4000)).RootElement.GetProperty("accounting");
        int bothVisible = bothText.Split("-> ").Length - 1;
        Check("BOTH-CAUSES-SUM-TO-THE-WHOLE: with the budget AND the response both cutting, each cause is stated and the two sum EXACTLY to found minus visible — the identity the accounting is built on",
            bothJson.GetProperty("dangling_missing_by_budget").GetInt32() > 0
            && bothJson.GetProperty("dangling_missing_by_response_cut").GetInt32() > 0
            && bothJson.GetProperty("dangling_missing_by_budget").GetInt32()
               + bothJson.GetProperty("dangling_missing_by_response_cut").GetInt32()
               == bothJson.GetProperty("dangling_found").GetInt32() - bothJson.GetProperty("dangling_visible").GetInt32()
            && bothText.Contains("were never listed: the listing budget (limit=100) ran out", StringComparison.Ordinal)
            && bothText.Contains("did not fit this response (max_chars=4000)", StringComparison.Ordinal)
            && bothText.Contains($"[accounting: {bothVisible} of the {bothCut.TotalDangling} ", StringComparison.Ordinal),
            $"budget={bothJson.GetProperty("dangling_missing_by_budget").GetInt32()} cut={bothJson.GetProperty("dangling_missing_by_response_cut").GetInt32()} found={bothCut.TotalDangling} visible={bothVisible}");

        // ---- the roster's one advance over the split it replaces: a plugin whose entries the BUDGET listed and the
        //      RESPONSE then dropped. Every other roster cell runs on a budget cut, so a roster that only ever
        //      reported the budget's omissions would pass all of them.
        var cutOnly = ErrorCheck.Run(rm, null, 1000);
        var cutOnlyJson = JsonDocument.Parse(JsonWire.RenderCheckErrors(cutOnly, 3000)).RootElement;
        Check("ROSTER-IS-RENDER-AWARE: on a sweep the budget never capped, the by-source roster still names the plugins THIS RESPONSE is missing entries for — under the superseded split those plugins appeared in no sentence at all",
            cutOnlyJson.GetProperty("accounting").GetProperty("dangling_missing_by_budget").GetInt32() == 0
            && cutOnlyJson.GetProperty("accounting").GetProperty("dangling_missing_by_response_cut").GetInt32() > 0
            && JsonRoster(JsonWire.RenderCheckErrors(cutOnly, 3000)).Length > 0,
            $"budget={cutOnlyJson.GetProperty("accounting").GetProperty("dangling_missing_by_budget").GetInt32()} roster=[{string.Join(",", JsonRoster(JsonWire.RenderCheckErrors(cutOnly, 3000)))}]");

        // ---- #361's OTHER lane: findings= excluding 'dangling'. The sweep lists nothing, so the listing accounting
        //      is absent by design — but the render still cuts SECTIONS, and that must still be stated. Gated on the
        //      listing lane, this arm's shape was silent: the cheap "is any master missing anywhere" sweep returned
        //      a truncated answer that read as whole.
        var mastersManyText = Wire.RenderCheckErrors(mastersMany, 1200);
        int mastersManySections = mastersManyText.Split("[ERROR] ").Length - 1;
        Check("SECTIONS-CUT-STATED-WITH-NO-LISTING-LANE (#361): findings=[missing_masters] lists no dangling refs, so there is no listing accounting to make — and a response that dropped sections still says how many of how many it rendered",
            mastersMany.Reports.Count > mastersManySections && mastersManySections > 0
            && mastersManyText.Contains($"{mastersManySections} of {mastersMany.Reports.Count} plugin section(s) were rendered", StringComparison.Ordinal)
            && mastersManyText.Contains("Raise max_chars= to fit more", StringComparison.Ordinal)
            // and it makes NO listing claim, because nothing was listed
            && !mastersManyText.Contains("found by this sweep appear above", StringComparison.Ordinal),
            $"rendered={mastersManySections} of {mastersMany.Reports.Count} line=[{AccountingLine(mastersManyText)}]");

        Check("SECTIONS-COMPLETE-MAKES-NO-CLAIM: the same lane, uncut, states nothing at all — a lane that lists nothing has no completeness to assert, and an accounting line there would be noise",
            Wire.RenderCheckErrors(mastersMany, 0) is var mastersManyWhole
            && !mastersManyWhole.Contains("[accounting:", StringComparison.Ordinal),
            AccountingLine(Wire.RenderCheckErrors(mastersMany, 0)));

        // ---- the overrun notice, in BOTH directions and followed to the letter.
        Check("OVERRUN-NOTICE-ONLY-WHEN-OVER: a response inside its cap never claims to be longer than it — the notice is measured off the finished response, not predicted from the reserve",
            Wire.RenderCheckErrors(manyAmple, 0) is var wholeResp
            && !wholeResp.Contains("raise it to at least", StringComparison.Ordinal)
            && Wire.RenderCheckErrors(manyAmple, 20000) is var roomy
            && roomy.Length <= 20000 && !roomy.Contains("raise it to at least", StringComparison.Ordinal),
            $"unbounded={Wire.RenderCheckErrors(manyAmple, 0).Length} at20k={Wire.RenderCheckErrors(manyAmple, 20000).Length}");

        Check("OVERRUN-REMEDY-CLEARS-IT: raising max_chars to the number the notice names actually removes the notice — a remedy is a claim about a call that has not happened, so the arm makes the call",
            RemedyClearsTheNotice(manyAmple, out var remedyFail), remedyFail);

        // ---- the two honesty-layer row counts, both directions. They can each be a no-op and print
        //      "0 of 1 plugin(s) that could not be parsed are named above" over a response that named it.
        var withExcluded = ErrorCheck.Run(rm, null, 1000) with
        {
            ExcludedPlugins = new Dictionary<string, string> { ["HcCeBroken.esp"] = "header could not be parsed" },
        };
        Check("EXCLUDED-ROWS-COUNTED-WHEN-NAMED: a response that names every unparseable plugin makes NO claim about them — the clause exists only where rows were dropped",
            Wire.RenderCheckErrors(withExcluded, 0) is var exWhole
            && exWhole.Contains("HcCeBroken.esp", StringComparison.Ordinal)
            && !exWhole.Contains("that could not be parsed are named above", StringComparison.Ordinal)
            && JsonDocument.Parse(JsonWire.RenderCheckErrors(withExcluded, 0)).RootElement
                 .GetProperty("accounting").GetProperty("excluded_plugins_named").GetInt32() == 1,
            AccountingLine(Wire.RenderCheckErrors(withExcluded, 0)));

        Check("EXCLUDED-ROWS-COUNTED-WHEN-DROPPED: when the cap leaves no room for the roster, the accounting says how many of how many were named — and json's own count agrees with the text",
            Wire.RenderCheckErrors(withExcluded, 3000) is var exCut
            && (exCut.Contains("0 of 1 plugin(s) that could not be parsed are named above", StringComparison.Ordinal)
                || !exCut.Contains("HcCeBroken.esp", StringComparison.Ordinal) == false),
            AccountingLine(Wire.RenderCheckErrors(withExcluded, 3000)));

        // ---- json's §2.1 in-band fields, in both directions and in the lane that has them.
        var jsonWhole = JsonDocument.Parse(JsonWire.RenderCheckErrors(ErrorCheck.Run(rm, null, 1000), 0)).RootElement;
        Check("JSON-INBAND-FIELDS-BOTH-DIRECTIONS: capped and truncated are FALSE on a complete response and TRUE on a cut one, and plugins_with_findings/rendered agree with the document",
            !jsonWhole.GetProperty("capped").GetBoolean() && !jsonWhole.GetProperty("truncated").GetBoolean()
            && jsonWhole.GetProperty("rendered").GetInt32() == jsonWhole.GetProperty("plugins").GetArrayLength()
            && JsonDocument.Parse(JsonWire.RenderCheckErrors(ErrorCheck.Run(rm, null, 100), 0)).RootElement
                 .GetProperty("capped").GetBoolean(),
            $"capped={jsonWhole.GetProperty("capped").GetBoolean()} truncated={jsonWhole.GetProperty("truncated").GetBoolean()}");

        // counts_only has no listing, so the four listing fields are ABSENT rather than zero — written
        // unconditionally they reported a 240-finding sweep as plugins_with_findings 0.
        var countsJsonDoc = JsonDocument.Parse(JsonWire.RenderCheckErrors(baseCounts, 0)).RootElement;
        Check("JSON-INBAND-FIELDS-ABSENT-UNDER-COUNTS-ONLY: the listing lane's four fields are not written where there is no listing — a field named for one lane's subject must be absent in the other, never 0",
            !countsJsonDoc.TryGetProperty("plugins_with_findings", out _)
            && !countsJsonDoc.TryGetProperty("rendered", out _)
            && !countsJsonDoc.TryGetProperty("truncated", out _)
            && !countsJsonDoc.TryGetProperty("capped", out _)
            && countsJsonDoc.GetProperty("accounting").GetProperty("listing").GetBoolean() == false,
            "counts_only json");

        // ---- a section's OTHER findings ride the whole-or-absent rule too. No listing-lane fixture carried a scan
        //      error or an unscannable record, so those two appends could be deleted outright and stay green.
        var withScanError = ErrorCheck.Run(rm, null, 1000);
        var scarred = withScanError with
        {
            Reports = withScanError.Reports.Select((x, i) => i == 0
                ? new PluginErrors(x.Plugin, x.Dangling, x.MissingMasters, 4,
                                   new[] { "0BCC84:HcCeMany00.esp — InvalidDataException: body could not be parsed" },
                                   "record enumeration aborted partway: InvalidDataException: truncated group")
                : x).ToList(),
        };
        var scarredWhole = Wire.RenderCheckErrors(scarred, 0);
        bool scarredSections = SectionsWhole(new[] { scarred }, out var scarredFail);
        Check("SECTION-CARRIES-ITS-OTHER-FINDINGS: a rendered section shows its scan error and its unscannable-record count, and both ride the whole-or-absent rule rather than being dropped a line at a time",
            scarredWhole.Contains("scan error: record enumeration aborted partway", StringComparison.Ordinal)
            && scarredWhole.Contains("4 record(s) could not be scanned", StringComparison.Ordinal)
            && scarredWhole.Contains("body could not be parsed", StringComparison.Ordinal)
            && scarredSections,
            $"scanError={scarredWhole.Contains("scan error:", StringComparison.Ordinal)} sections=[{scarredFail}]");

        var countsForHisto = ErrorCheck.Run(rm, null, 1000, null, null, ErrorFindingClass.All, true);
        var hCut = Wire.RenderCheckErrors(countsForHisto, 1500);      // stopped by the response
        var hRows = Wire.RenderCheckErrors(countsForHisto, 0, 3);     // stopped by the row limit
        Check("HISTOGRAM-REMEDY-NAMES-ITS-CAUSE: a histogram cut by the response offers max_chars=, one cut by the row limit offers limit= — the knob that stopped it, not the other one",
            hCut.Contains("more row(s) — raise max_chars= to see them", StringComparison.Ordinal)
            && hRows.Contains("more row(s) — raise limit= to see them", StringComparison.Ordinal),
            $"budget-cut={hCut.Contains("raise max_chars=", StringComparison.Ordinal)} row-cut={hRows.Contains("raise limit=", StringComparison.Ordinal)}");

        // ---- a record scope can admit nothing from a base master the sweep opened. "Swept" has to mean examined.
        var modOnlyScope = ErrorCheck.Run(rb, null, 1000, null, new SweepScope(null, "HcCeModNpc", null, null));
        var modOnlyScopeText = Wire.RenderCheckErrors(modOnlyScope, 0);
        Check("BASELINE-RECORD-SCOPE-NOT-SWEPT: a record scope that admits no base-master record leaves base_masters_swept EMPTY and prints no baseline line — a filtered-out master must not report as covered-and-clean",
            modOnlyScope.BaseMastersSwept is { Count: 0 } && modOnlyScope.BaselineDangling == 0
            && !modOnlyScopeText.Contains("baseline:", StringComparison.Ordinal)
            && modOnlyScope.TotalDangling > 0,
            $"swept={modOnlyScope.BaseMastersSwept?.Count} baseline={modOnlyScope.BaselineDangling} total={modOnlyScope.TotalDangling}");

        var bothScope = ErrorCheck.Run(rb, null, 1000, null, new SweepScope(null, "Npc", null, null));
        Check("BASELINE-RECORD-SCOPE-STILL-SWEPT: a record scope that DOES admit base-master records still counts them as swept — the arm above must not pass by disabling the baseline line outright",
            bothScope.BaseMastersSwept is { Count: 1 } && bothScope.BaselineDangling > 0
            && Wire.RenderCheckErrors(bothScope, 0).Contains("this sweep covered (Skyrim.esm)", StringComparison.Ordinal),
            $"swept={bothScope.BaseMastersSwept?.Count} baseline={bothScope.BaselineDangling}");

        // ---- the phase sentence compares two groups, so it needs both to exist.
        var baseOnly = ErrorCheck.Run(rb, new[] { "Skyrim.esm" }, 2);
        var baseOnlyText = Wire.RenderCheckErrors(baseOnly, 0);
        Check("BASELINE-PHASE-CLAUSE-NEEDS-A-NON-BASE-PLUGIN: a sweep scoped to base masters alone states the split but not the ordering sentence — there is no 'every other plugin' for the budget to reach first",
            baseOnlyText.Contains("were never listed: the listing budget (limit=2) ran out", StringComparison.Ordinal)
            && baseOnly.BaselineDangling > 0 && !baseOnly.NonBaseInScope
            && baseOnlyText.Contains("baseline: 3 of 3", StringComparison.Ordinal)
            && !baseOnlyText.Contains("the listing budget (limit=) is spent on every other plugin", StringComparison.Ordinal),
            $"baseline={baseOnly.BaselineDangling} nonBase={baseOnly.NonBaseInScope}");

        // The arm above cannot tell a correct gate from a subtraction that happens to agree: scoped to one name, the
        // scanned count and the swept-base count coincide. This fixture SEPARATES them — the same plugin named twice is
        // swept twice, so PluginsScanned is 2 while exactly one base master was examined, and any gate phrased as
        // "scanned > swept-base" prints the ordering sentence over a scope with no other plugin in it (Aaron's review).
        var dupBase = ErrorCheck.Run(rb, new[] { "Skyrim.esm", "Skyrim.esm" }, 2);
        var dupBaseText = Wire.RenderCheckErrors(dupBase, 0);
        Check("BASELINE-PHASE-CLAUSE-NOT-A-SUBTRACTION: with the scanned count (2) and the swept-base count (1) deliberately apart, the ordering sentence still does not print — the gate reads a stated fact, not a difference between two counts of different things",
            dupBaseText.Contains("were never listed: the listing budget (limit=2) ran out", StringComparison.Ordinal)
            && dupBase.BaselineDangling > 0
            && dupBase.PluginsScanned > (dupBase.BaseMastersSwept?.Count ?? 0)     // the old gate's test is TRUE here
            && !dupBase.NonBaseInScope                                             // and the fact says otherwise
            && !dupBaseText.Contains("the listing budget (limit=) is spent on every other plugin", StringComparison.Ordinal),
            $"scanned={dupBase.PluginsScanned} swept={dupBase.BaseMastersSwept?.Count} nonBase={dupBase.NonBaseInScope}");

        // and the positive direction: a scope that DOES hold a non-base plugin still gets the sentence
        Check("BASELINE-PHASE-CLAUSE-STILL-PRINTS-WITH-A-MOD-IN-SCOPE: the gate above must not pass by suppressing the sentence everywhere",
            tight.NonBaseInScope
            && tightText.Contains("the listing budget (limit=) is spent on every other plugin", StringComparison.Ordinal),
            $"nonBase={tight.NonBaseInScope}");

        // ---- the counts_only note belongs to the FIRST axis only; the second must not repeat it.
        var twoAxes = ErrorCheck.Run(rb, null, 1000, null, null, ErrorFindingClass.All, countsOnly: true);
        var twoAxesText = Wire.RenderCheckErrors(twoAxes, 0);
        Check("COUNTS-ONLY-NOTE-NOT-REPEATED: the counts_only=true note is printed once, above the TARGET axis, and not again above the SOURCE axis",
            twoAxesText.Split("counts_only=true — totals above are exact", StringSplitOptions.None).Length - 1 == 1
            && twoAxesText.Contains("by SOURCE plugin", StringComparison.Ordinal),
            $"note occurrences={twoAxesText.Split("counts_only=true — totals above are exact", StringSplitOptions.None).Length - 1}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "check-errors-guard: ALL PASS" : $"check-errors-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    // ---- #282 json-parity helpers: parse the emitted document, so a malformed render fails the guard rather than
    //      passing a substring match. Shared with script-property-check-guard.
    internal static bool JsonMatches(string json, string prop, int expected)
    {
        try { return JsonDocument.Parse(json).RootElement.TryGetProperty(prop, out var v) && v.GetInt32() == expected; }
        catch { return false; }
    }

    internal static bool JsonNull(string json, string prop)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.TryGetProperty(prop, out var v)
                && v.ValueKind == JsonValueKind.Null;
        }
        catch { return false; }
    }

    /// <summary>The counts_only json's <c>unread</c> must be the wrapped, budget-flagged shape — total + rows + rendered
    /// + truncated — not a bare array that can silently shorten (#288 review finding 4).</summary>
    static bool JsonUnreadTruncated(string json, int expectTotal, bool expectTruncated = true)
    {
        try
        {
            var u = JsonDocument.Parse(json).RootElement.GetProperty("unread");
            return u.GetProperty("total").GetInt32() == expectTotal
                && u.GetProperty("truncated").GetBoolean() == expectTruncated
                && u.GetProperty("rendered").GetInt32() == u.GetProperty("rows").GetArrayLength()
                && (!expectTruncated || u.GetProperty("rendered").GetInt32() < expectTotal);
        }
        catch { return false; }
    }

    /// <summary>The accounting line out of a rendered response — the one line every omission claim now lives on.
    /// Arms read it rather than the whole render because a listed dangling entry also prints its own source plugin,
    /// so a whole-text match counts plugins the roster never named (the trap the line it replaces already paid for).
    /// </summary>
    static string AccountingLine(string text)
        => text.Split('\n').FirstOrDefault(l => l.Contains("[accounting:", StringComparison.Ordinal)) ?? "";

    /// <summary>The json roster as "plugin:count" keys, in the order it was written. The json twin of the text
    /// line's roster, so an arm can hold the two transports against each other rather than trusting either.</summary>
    static string[] JsonRoster(string json)
    {
        var acct = JsonDocument.Parse(json).RootElement.GetProperty("accounting");
        return acct.GetProperty("dangling_missing_by_source").EnumerateArray()
                   .Select(e => e.GetProperty("plugin").GetString() + ":" + e.GetProperty("count").GetInt32())
                   .ToArray();
    }

    /// <summary>The whole-or-absent rule, swept over caps. A section says more than its dangling entries — a scan
    /// error, the missing masters it declares, how many records could not be read — and none of those is an entry,
    /// so no accounting subject covers them one at a time. They are emitted with the section head or the section is
    /// not started, and this holds the render to that: every head has its dangling header, every header has its
    /// head, and the head count is what the accounting reports as rendered.
    ///
    /// <para>The tool's own parameter text promises master-table findings are "always listed in full". Under a
    /// per-line drop that sentence was false at any cap tight enough to reach it.</para></summary>
    static bool SectionsWhole(IReadOnlyList<ErrorCheckResult> results, out string detail)
    {
        var bad = new List<string>();
        foreach (var r in results)
            foreach (int cap in new[] { 400, 900, 1500, 2500, 4000, 9000, 30000 })
            {
                var text = Wire.RenderCheckErrors(r, cap);
                int heads = text.Split("[ERROR] ").Length - 1;
                int headers = text.Split("  dangling reference(s) (").Length - 1;
                int withDangling = 0, rendered = 0;
                foreach (var pl in r.Reports)
                {
                    if (!text.Contains("[ERROR] " + pl.Plugin + "\n", StringComparison.Ordinal)) continue;
                    rendered++;
                    if (pl.Dangling.Count > 0) withDangling++;
                    // a section that declares missing masters must show them where it appears at all
                    if (pl.MissingMasters.Count > 0
                        && !text.Contains("  missing master(s): " + string.Join(", ", pl.MissingMasters), StringComparison.Ordinal))
                        bad.Add($"@{cap} {pl.Plugin} rendered without its missing-master line");
                }
                if (headers != withDangling)
                    bad.Add($"@{cap} {headers} dangling header(s) for {withDangling} rendered section(s) that have refs");
                if (heads != rendered)
                    bad.Add($"@{cap} {heads} head(s) but {rendered} section(s) matched by name");
                var acctLine = AccountingLine(text);
                if (rendered < r.Reports.Count && !acctLine.Contains($"{rendered} of {r.Reports.Count} plugin section(s) were rendered", StringComparison.Ordinal))
                    bad.Add($"@{cap} rendered {rendered} of {r.Reports.Count} but the accounting does not say so");
            }
        detail = bad.Count == 0 ? "every section whole at every cap" : string.Join("; ", bad.Take(4));
        return bad.Count == 0;
    }

    /// <summary>The exclude= parameter's [Description] must name every token SweepExclusion accepts. A tool
    /// parameter's prose is a SECOND spelling of an accepted vocabulary, on a surface no existing guard reads
    /// (#386), so a token added to the set and not to the description would be undiscoverable.
    ///
    /// <para>The converse — a word in the description that is NOT an accepted token — is deliberately not checked:
    /// the description is prose, and every ordinary word in it would have to be exempted. What protects that
    /// direction is EXCLUDE-UNKNOWN-GROUP-REFUSES, which proves an unaccepted value refuses by name and lists the
    /// real set, so a caller who believes a wrong word in the docs is told immediately rather than served
    /// silently.</para></summary>
    static bool ExcludeDescriptionNamesTokens(out string detail)
    {
        var param = typeof(ReadTools).GetMethod(nameof(ReadTools.CheckErrorsTool))?
            .GetParameters().FirstOrDefault(x => x.Name == "exclude");
        if (param is null) { detail = "no exclude= parameter on CheckErrorsTool"; return false; }
        var text = param.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
                        .Cast<System.ComponentModel.DescriptionAttribute>().FirstOrDefault()?.Description;
        if (text is null) { detail = "exclude= carries no [Description]"; return false; }
        var missing = SweepExclusion.Tokens.Where(t => !text.Contains(t, StringComparison.Ordinal)).ToList();
        detail = missing.Count == 0 ? $"names all {SweepExclusion.Tokens.Length} token(s)"
                                    : "not named in the description: " + string.Join(", ", missing);
        return missing.Count == 0;
    }

    /// <summary>Resolve an exclude= the way the service does, so an arm states the caller's spelling rather than
    /// the expanded set.</summary>
    static SweepExclusion.Resolved? Excl(params string[] values)
    {
        var (set, err) = SweepExclusion.Resolve(values, new[] { "Skyrim.esm" });
        if (err is not null) throw new InvalidOperationException("fixture exclude= did not resolve: " + err);
        return set;
    }

    /// <summary>Follow the overrun notice's own remedy: read the number it tells the caller to raise max_chars to,
    /// re-render at exactly that, and see whether the notice is gone. A remedy is a claim about a call that has not
    /// happened, so the only honest way to hold it is to make the call (CLAUDE.md §5 #11).</summary>
    static bool RemedyClearsTheNotice(ErrorCheckResult r, out string detail)
    {
        var bad = new List<string>();
        foreach (int cap in new[] { 120, 250, 400, 700, 1200, 2000, 3000 })
        {
            var text = Wire.RenderCheckErrors(r, cap);
            int at = text.IndexOf("raise it to at least ", StringComparison.Ordinal);
            if (at < 0) { if (text.Length > cap) bad.Add($"text@{cap} over by {text.Length - cap} with no notice"); continue; }
            var digits = new string(text[(at + 21)..].TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int raised)) { bad.Add($"text@{cap} notice names no number"); continue; }
            var again = Wire.RenderCheckErrors(r, raised);
            if (again.Contains("raise it to at least", StringComparison.Ordinal))
                bad.Add($"text@{cap} said raise to {raised}; at {raised} the notice fired again (len {again.Length})");
            if (again.Length > raised) bad.Add($"text@{cap}->{raised} still over by {again.Length - raised}");
        }
        detail = bad.Count == 0 ? "every overrun notice cleared at the number it named" : string.Join("; ", bad.Take(3));
        return bad.Count == 0;
    }

    /// <summary>The caps this invariant is swept over. It used to step 3000 -> 8000, straight over the 4000-7000 band
    /// the fat-head fixture's plugin object actually bites in: the arm's one real defect lived in a gap in its own
    /// ladder. The band is now walked rather than jumped.</summary>
    static readonly int[] CapLadder = { 120, 300, 700, 900, 1500, 3000, 4000, 4500, 5000, 5270, 6000, 7000, 8000, 40000 };

    /// <summary>The cap invariant, swept: for a range of max_chars values, NEITHER transport may return more than it
    /// was given — with ONE legitimate exception, recognised by a FIXTURE-KNOWN LENGTH rather than by a phrase the
    /// response emits about itself.
    ///
    /// <para><b>What this arm used to be, and why it could never fail.</b> It excused any overrun in a response
    /// containing "raise it to at least". But <c>CheckAccounting.CapTooSmall</c> returns non-null for EVERY response
    /// longer than its cap and both renders append the notice whenever it is non-null — so an over-cap response
    /// always carries that phrase, the second conjunct was dead, and the flagship #361 cell was a tautology for two
    /// whole review rounds. The json twin had the identical shape with <c>max_chars_overrun</c>.</para>
    ///
    /// <para><b>The FLOOR is the expected value.</b> The only response that may legitimately run over is the one with
    /// no body in it at all — header, accounting and boundary, which are reserved and never dropped. That length is a
    /// property of the FIXTURE: render it at a cap no body can fit under (1) and measure. Every response is then held
    /// to <c>max(cap, floor + slack)</c>, so a single emitted body unit puts an over-cap response past the floor and
    /// the arm goes red. The slack covers the one thing that moves the floor between caps — max_chars is printed
    /// inside the accounting and again inside the overrun notice, so a wider cap widens the floor by its own digits.
    /// It is bounded by digit width (<see cref="FloorSlack"/>), not by plausibility.</para>
    ///
    /// <para><b>What it does NOT bound, stated rather than implied:</b> anything the render emits UNCONDITIONALLY is
    /// inside the floor it is measured against, so this arm cannot see it. HISTOGRAM-FRAMING-IS-BOUNDED is the arm
    /// that holds that surface.</para></summary>
    static bool CapSweep(ErrorCheckResult r, out string detail)
    {
        var bad = new List<string>();
        // The irreducible response, per transport, measured off the fixture once.
        int textFloor = Wire.RenderCheckErrors(r, 1).Length;
        int jsonFloor = JsonWire.RenderCheckErrors(r, 1).Length;
        foreach (int cap in CapLadder)
        {
            var text = Wire.RenderCheckErrors(r, cap);
            var json = JsonWire.RenderCheckErrors(r, cap);
            int textAllowed = Math.Max(cap, textFloor + FloorSlack(cap));
            int jsonAllowed = Math.Max(cap, jsonFloor + FloorSlack(cap));
            if (text.Length > textAllowed)
                bad.Add($"text@{cap}={text.Length} over the allowed {textAllowed} (floor {textFloor})");
            if (json.Length > jsonAllowed)
                bad.Add($"json@{cap}={json.Length} over the allowed {jsonAllowed} (floor {jsonFloor})");
            try { JsonDocument.Parse(json); }
            catch (Exception ex) { bad.Add($"json@{cap} is not valid json: {ex.GetType().Name}"); }
        }
        detail = bad.Count == 0 ? $"every cap honoured, or bounded by the floor (text {textFloor} / json {jsonFloor})"
                                : string.Join("; ", bad);
        return bad.Count == 0;
    }

    /// <summary>How much the floor may grow between the cap it was measured at and the cap under test. max_chars is
    /// printed inside the accounting's by-cut clause and inside the overrun notice (which also prints the number to
    /// raise to and the length reached) — four printed numbers, each bounded by the cap's own digit width. Four
    /// digits of headroom per place, not a round number chosen for comfort.</summary>
    static int FloorSlack(int cap) => 4 * cap.ToString().Length;

    internal static bool JsonHasHistogram(string json, string prop)
    {
        try
        {
            return JsonDocument.Parse(json).RootElement.TryGetProperty(prop, out var h)
                && h.TryGetProperty("rows", out var rows) && rows.GetArrayLength() > 0;
        }
        catch { return false; }
    }
}

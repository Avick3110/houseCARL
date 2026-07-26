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
        Check("CAP: limit=1 returns 1 dangling ref but reports the TRUE total (2) and Capped",
            capped.TotalDangling == 2 && capped.Capped
            && capped.Reports.Count == 1 && capped.Reports[0].Dangling.Count == 1,
            $"total={capped.TotalDangling} capped={capped.Capped} collected={(capped.Reports.Count > 0 ? capped.Reports[0].Dangling.Count : -1)}");

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
            && countsOnly.Reports.Count == 0 && !countsOnly.Capped
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

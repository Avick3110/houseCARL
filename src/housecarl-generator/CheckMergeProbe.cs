using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// SELF-CONTAINED CI REGRESSION GUARD for the MERGED derived-findings sweep (housecarl_check — SPEC §6.1). Drives
/// the real renders (<see cref="Wire.RenderCheck"/> / <see cref="JsonWire.RenderCheck"/>) over a synthesized order
/// whose one plugin carries BOTH sweep families' findings at once: NPCs whose Race links into an absent master
/// (dangling), and weapons whose VMAD binds none of the properties their .pex declares (unbound). The DIALOGUE
/// family's fixture is a result rather than a plugin, built through the real <c>DialogueSweep.Run</c> with a stub
/// validator — that family selects RECORDS by seed, so what its arms are about is the seed grammar, the section and
/// the accounting; <c>dialogue-validate-guard</c> owns the validation itself.
///
/// THE ASSERTION RULE, inherited from check-errors-guard and restated because it is what makes these arms worth
/// anything: AN INVARIANT ARM ASSERTS AGAINST A FIXTURE-KNOWN EXPECTED VALUE — NEVER AGAINST A PHRASE THE RENDER
/// ITSELF EMITS. Where an arm must read the render's own words (following a remedy, counting what an accounting
/// states), the words LOCATE the value and the assertion is still made against something counted independently.
///
/// THE ARMS:
///   SECTION-PER-FAMILY        — one header, one section per selected family in Registered order, one boundary line
///                               each. The two families claim different things, so one boundary for both would be a
///                               claim neither of them makes.
///   ACCOUNTING-PER-FAMILY     — one accounting line per family, under its own section, and each states ITS family's
///                               numbers. 4a's invariant is one source per sentence across TRANSPORTS, not one
///                               accounting across families.
///   EXCLUDED-ROSTER-ONCE      — the excluded-plugin roster is a SCOPE fact: its rows appear once in the response and
///                               exactly ONE accounting states the cut. Two would subtract the same rows from the
///                               same total twice — the double-count class.
///   ALLOCATION-SECOND-FAMILY-STILL-RENDERS — at a cap that bites, BOTH families render rows. Spent in series the
///                               second family inherits whatever the first left; measured on the live order at plain
///                               defaults that was 400 characters of an 80,000 budget.
///   SCOPE-SENTENCE-*          — the default narrows only because the response says so: which families ran, which
///                               registered ones did not, and the exact findings= spelling that adds them — in BOTH
///                               transports, as one complete sentence rather than a lead each finishes its own way.
///   OFF-ORDER-STATED-PER-FAMILY — the scripts family has no off-order lane, and the response says which files it did
///                               not sweep, inside that family's own section where its zero counts are.
///   PLAN-LEAVES-EMPTY-SUBJECTS-OUT — a subject with no rows is not in the allocation plan; a share held for rows
///                               that do not exist is the equal-split waste the ruled rule was chosen over.
///   CLASS-TOKEN-ROUND-TRIP    — every flag combination the merged parser produces spells tokens the family parsers
///                               read back as the same flags. The merged tool hands each family its classes through
///                               that round trip, so a disagreement would silently widen or narrow a sweep.
///   REGISTERED-IS-THE-MEMBERSHIP — every family the refusal OFFERS is one the parser ACCEPTS. Both read the one
///                               Registered list, so a family added there cannot be named in a spelling that then
///                               fails to parse.
///   DIALOGUE-REFUSED-WITHOUT-SEEDS — the seeded family's cost-refusal (F1.2) is FAMILY-LOCAL: it fills its own
///                               section, spells the seeds= that works, asserts no completeness about a validation
///                               that never ran, and does not refuse a call another family answered.
///   DIALOGUE-NOT-PLUGIN-SCOPED — the section states that the plugin-scope parameters do not narrow this family and
///                               that it has no off-order lane. Unstated, a seeded answer reads as a scoped one.
///   DIALOGUE-CLASS-8-ABSENT   — the effective merged INFO order is NOT rendered here (it is records' surface), and
///                               the boundary names where it lives. The fixture's topics carry one, so the arm pins
///                               the gate rather than the absence of data to gate.
///   DIALOGUE-UNREACHABLE-SEEDS-NAMED — a seed that resolved to nothing is named with why, in the listing lane AND
///                               under counts_only: it bounds the answer rather than sitting inside it.
///   DIALOGUE-SEED-BUDGET      — limit= means SEEDS for this family, and the accounting names how many it never
///                               reached. A seed never looked at and a topic that did not fit are different
///                               absences and must not read alike.
///   DIALOGUE-BOUNDARY-UNREFUSABLE — the standing-limits claim is this family's boundary, so it is reserved and
///                               written at every cap, including ones that admit no findings at all.
///   DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE / -UNFINISHED-SEED-KEEPS-ITS-SHARE — both arms of one conditional.
///                               A subject's ceiling is fixed on its first unit against the siblings still pending,
///                               so a one-seed call must be told the seed subject is finished or half the family's
///                               room is held for heads that will never be written. Asked at a cap sized from the
///                               fixture's own floor and block width, because at a cap nothing bites under the arm
///                               passes either way — which is what the first spelling of it did.
///   ROSTER-STILL-ONE-WITH-THREE-FAMILIES — the dialogue family reports no excluded-plugin roster of its own, so the
///                               roster stays owned by exactly one accounting however many families run. Asked of a
///                               DIALOGUE-ONLY response as well, because that is the half a sabotage can reach.
///   CAP-LADDER                — every integer cap 1..12000 plus one far above: neither transport returns more than
///                               it was given, bar the floor (the response with no body in it at all), and the json
///                               parses at every one. Run twice: over two families, and over all three.
///
/// Run: dotnet run --project src/housecarl-generator -- check-guard
/// </summary>
public static class CheckMergeProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("check-guard — the merged derived-findings sweep (housecarl_check)");
        Console.WriteLine();
        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-check-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try { return RunChecks(tmpDir); }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { /* best-effort */ } }
    }

    // The fixture's own numbers, declared here so every arm below asserts against a value this file knows rather
    // than against one the render printed.
    const int Npcs = 40;        // each with a Race link into the absent master ⇒ 40 dangling refs
    const int Weapons = 40;     // each with an unbound-property VMAD ⇒ 40 record sections
    const int Topics = 12;      // the dialogue seed's owned topics ⇒ 12 topic blocks
    const int IssuesPerTopic = 2;
    const int SilentPerTopic = 1;
    const int UnreachableSeeds = 2;   // seeds named that resolve to nothing — this family's own excluded scope
    // What the dialogue fixture FOUND, arithmetic this file does rather than a number the render prints:
    // per topic its issues plus its silent line, over every topic.
    const int DialogueFindings = Topics * (IssuesPerTopic + SilentPerTopic);

    static int RunChecks(string tmpDir)
    {
        int failures = 0;
        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        var scriptsDir = Path.Combine(tmpDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        string ghostPath = Path.Combine(tmpDir, "HcCmGhost.esm");
        string mainPath = Path.Combine(tmpDir, "HcCm.esp");
        try
        {
            ScriptPropertyCheckProbe.WritePex(Path.Combine(scriptsDir, "HcCmScript.pex"), "HcCmScript", parent: null,
                ScriptPropertyCheckProbe.AutoObj("HcCmSpell", "Spell"),
                ScriptPropertyCheckProbe.AutoObj("HcCmOther", "Spell"),
                ScriptPropertyCheckProbe.AutoScalar("HcCmChance", "Int", initInt: null));

            // The absent master: written so its FormKeys are real, then deliberately NOT loaded.
            var ghost = new SkyrimMod(new ModKey("HcCmGhost", ModType.Master), SkyrimRelease.SkyrimSE);
            var gRace = ghost.Races.AddNew(); gRace.EditorID = "HcCmGhostRace";
            var ghostRaceFk = gRace.FormKey;
            ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var mod = new SkyrimMod(new ModKey("HcCm", ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < Npcs; i++)
            {
                var npc = mod.Npcs.AddNew();
                npc.EditorID = $"HcCmNpc{i:D2}";
                npc.Race.SetTo(ghostRaceFk);          // dangling: the master is not in the order
            }
            for (int i = 0; i < Weapons; i++)
            {
                var w = mod.Weapons.AddNew();
                w.EditorID = $"HcCmWeapon{i:D2}";
                w.VirtualMachineAdapter = ScriptPropertyCheckProbe.Vmad("HcCmScript");   // binds nothing ⇒ all declared unbound
            }
            mod.BeginWrite.ToPath(mainPath).WithLoadOrder(new ISkyrimModGetter[] { ghost }).Write();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: could not synthesize fixtures: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
            return 1;
        }

        using var resolver = LoadOrderResolver.Build(new[] { mainPath });   // Ghost deliberately absent
        using var assets = AssetResolver.Build("", "", tmpDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());

        var errors = ErrorCheck.Run(resolver, null, 1000);
        var scripts = ScriptPropertyCheck.Run(resolver, assets, null, 1000);
        if (!errors.Success || !scripts.Success)
        {
            Console.Error.WriteLine($"error: fixture sweep failed: {errors.Error ?? scripts.Error}");
            return 1;
        }

        Check($"FIXTURE: the one plugin carries BOTH families' findings ({Npcs} dangling refs, {Weapons} record sections)",
            errors.TotalDangling == Npcs && scripts.Reports.Count == Weapons,
            $"dangling={errors.TotalDangling} (want {Npcs}) records={scripts.Reports.Count} (want {Weapons})");

        // The DIALOGUE family's fixture. Built as a result rather than as a plugin: this family consumes a seed
        // list, and DialogueSweep.Run is driven with a stub validator below so the seed grammar — the parse, the
        // cost-refusal, the budget — is exercised on the real code path rather than mimed here.
        var dialogue = DialogueFixture();
        Check($"FIXTURE (dialogue): one seed owning {Topics} topics with {DialogueFindings} findings, plus {UnreachableSeeds} seeds that resolve to nothing",
            dialogue.TopicsFound == Topics && dialogue.ProblemsFound == DialogueFindings
            && dialogue.Unresolved.Count == UnreachableSeeds && dialogue.Resolved.Count() == 1,
            $"topics={dialogue.TopicsFound} findings={dialogue.ProblemsFound} unreachable={dialogue.Unresolved.Count}");

        var both = new CheckSweep(Sel("errors", "scripts"), errors, scripts);
        var text = Wire.RenderCheck(both, 0);
        var json = JsonWire.RenderCheck(both, 0);
        // Every registered family at once — what the ALL sentence and the cap ladder are asked about.
        var all = new CheckSweep(Sel("errors", "scripts", "dialogue"), errors, scripts, null, dialogue);
        var allText = Wire.RenderCheck(all, 0);
        var allJson = JsonWire.RenderCheck(all, 0);

        // ---- SECTION-PER-FAMILY -------------------------------------------------------------------
        Check("SECTION-PER-FAMILY: one header, a section head per selected family in Registered order, and a boundary line for EACH — the two families claim different things",
            Count(text, "\n[errors] load-order integrity sweep\n") == 1
            && Count(text, "\n[scripts] VMAD script-property binding sweep\n") == 1
            && text.IndexOf("[errors]", StringComparison.Ordinal) < text.IndexOf("[scripts]", StringComparison.Ordinal)
            && Count(text, "boundary (errors): ") == 1 && Count(text, "boundary (scripts): ") == 1
            && Count(text, ReadSentences.SweepMergedTitle) == 1,
            $"errorsHead={Count(text, "[errors] ")} scriptsHead={Count(text, "[scripts] ")} boundaries={Count(text, "boundary (")}");

        // ---- ACCOUNTING-PER-FAMILY ----------------------------------------------------------------
        // Each family's accounting states ITS OWN family's totals — the fixture knows both.
        Check($"ACCOUNTING-PER-FAMILY: two accounting lines, each stating its own family's totals ({Npcs} dangling, {Weapons} record sections)",
            Count(text, ReadSentences.SweepAccountingLead) == 2
            && text.Contains($"all {Npcs} dangling ref(s) found by this sweep appear above.", StringComparison.Ordinal)
            && text.Contains($"all {Weapons} record section(s) found by this sweep appear above.", StringComparison.Ordinal),
            $"accountings={Count(text, ReadSentences.SweepAccountingLead)}");

        using (var doc = JsonDocument.Parse(json))
        {
            var fams = doc.RootElement.GetProperty("families");
            Check("ACCOUNTING-PER-FAMILY (json): each family object carries its OWN accounting and its OWN boundary",
                fams.GetProperty("errors").GetProperty("accounting").GetProperty("dangling_found").GetInt32() == Npcs
                && fams.GetProperty("scripts").GetProperty("accounting").GetProperty("record_sections_with_findings").GetInt32() == Weapons
                && fams.GetProperty("errors").GetProperty("boundary").GetString() != fams.GetProperty("scripts").GetProperty("boundary").GetString(),
                Trim(json));
        }

        // ---- EXCLUDED-ROSTER-ONCE -----------------------------------------------------------------
        // Both results carry the SAME roster, because both read it off the same captured build. The response must
        // emit it once and exactly one accounting must state its cut: two would subtract the same rows from the same
        // total twice.
        var roster = new Dictionary<string, string> { ["HcCmBroken.esp"] = "header could not be parsed" };
        var withRoster = new CheckSweep(Sel("errors", "scripts"),
                                        errors with { ExcludedPlugins = roster }, scripts with { ExcludedPlugins = roster });
        var rosterText = Wire.RenderCheck(withRoster, 0);
        Check("EXCLUDED-ROSTER-ONCE: the roster is a SCOPE fact — its rows appear once, however many families ran",
            Count(rosterText, "  HcCmBroken.esp: header could not be parsed\n") == 1
            && Count(rosterText, "excluded plugins (could not be parsed") == 1,
            $"rows={Count(rosterText, "  HcCmBroken.esp: ")}");

        // The declaring half, asserted at the ACCOUNTING rather than at the roster: at a cap that cuts the roster,
        // exactly one accounting states the cut. Walked over the band, because which cap cuts it is a fixture fact.
        var doubleCounted = new List<string>();
        bool sawRosterCut = false;
        for (int cap = 300; cap <= 8000; cap += 20)
        {
            var t = Wire.RenderCheck(withRoster, cap);
            int stated = Count(t, " plugin(s) that could not be parsed are named above.");
            if (stated > 0) sawRosterCut = true;
            if (stated > 1) doubleCounted.Add($"@{cap}: {stated} accountings state the roster cut");
        }
        if (!sawRosterCut) doubleCounted.Add("no cap in 300..8000 cut the roster — the arm never saw the case it is for");
        Check("EXCLUDED-ROSTER-ONCE-DECLARED: at every cap that cuts the roster, exactly ONE family's accounting states it — two would report one cut twice",
            doubleCounted.Count == 0,
            doubleCounted.Count == 0 ? "one accounting, at every cap that cut" : string.Join("; ", doubleCounted.Take(3)));

        // ---- ALLOCATION-SECOND-FAMILY-STILL-RENDERS -----------------------------------------------
        // #394 at the family level. Every cap in the band where the response is CUT (neither family whole) must
        // still carry rows from BOTH — the counts read off each accounting, held against the fixture's own totals.
        // #394 AT THE FAMILY LEVEL, stated as the arithmetic rather than as "both families render". Equal shares are
        // equal ROOM, not equal output: a scripts record section is wider than a dangling line, so at a body budget
        // of a few hundred characters the errors family fits a line and the scripts family does not fit a section.
        // That is the rule working. What #394 IS, is the second family waiting for the first to finish — so the
        // claim is about WHEN the scripts family first renders anything, held against what a serial walk would have
        // needed. Both quantities are measured off this fixture.
        int mergedFloor = Wire.RenderCheck(both, 1).Length;                       // the response with no body at all
        int errorsWholeBody = Wire.RenderCheckErrors(errors, 0).Length            // what the errors family's body
                            - Wire.RenderCheckErrors(errors, 1).Length;          // comes to when nothing is cut
        int firstScriptsCap = -1;
        var asymmetric = new List<string>();
        for (int cap = mergedFloor; cap <= 20000; cap += 20)
        {
            var t = Wire.RenderCheck(both, cap);
            int dangling = StatedPair(t, " dangling ref(s) found by this sweep appear above.");
            int recs = StatedPair(t, " record section(s) found by this sweep appear above.");
            if (dangling < 0 || recs < 0) { asymmetric.Add($"@{cap}: an accounting states no count (dangling={dangling} records={recs})"); continue; }
            if (recs > 0 && firstScriptsCap < 0) firstScriptsCap = cap;
            // Once the scripts family has started rendering it must never stop as the cap WIDENS — a later cap
            // giving it less is a share that moved with a sibling's spending.
            if (firstScriptsCap >= 0 && recs == 0) asymmetric.Add($"@{cap}: the scripts family rendered nothing at a cap wider than {firstScriptsCap}, where it rendered");
        }
        // A serial walk hands the second family what the first left over, so it could not render its first section
        // before the errors family's whole body had been spent. Anything at or above that is indistinguishable from
        // the rule this replaces.
        int serialWouldNeed = mergedFloor + errorsWholeBody;
        if (firstScriptsCap < 0) asymmetric.Add("the scripts family never rendered at any cap up to 20000");
        else if (firstScriptsCap >= serialWouldNeed)
            asymmetric.Add($"the scripts family first rendered at max_chars={firstScriptsCap}, which a SERIAL walk would also have reached ({serialWouldNeed})");
        Check("ALLOCATION-SECOND-FAMILY-DOES-NOT-WAIT-ITS-TURN (#394): the scripts family renders its first section far below the cap a serial walk would have needed — and never renders less as the cap widens",
            asymmetric.Count == 0,
            asymmetric.Count == 0
                ? $"first scripts section at max_chars={firstScriptsCap}; a serial walk needed {serialWouldNeed} (floor {mergedFloor} + the errors family's whole {errorsWholeBody})"
                : string.Join("; ", asymmetric.Take(3)) + $" ({asymmetric.Count} total)");

        // ---- THE THREE HARD PROPERTIES OF THE WATER-FILL (ruling pins 3(i), 3(ii), 3(iv)) ----------
        // Each is a claim the rule makes BY CONSTRUCTION, so each gets an arm that can fail if the construction
        // stops holding — a guarantee nothing asks about is a guarantee nobody knows they lost.

        // (i) MONOTONE IN max_chars. lambda is non-decreasing in the row budget and every subject is allocated
        // min(demand, lambda), so widening the cap can never render LESS of anything. Asked of what each subject
        // actually SPENT, at every integer cap across a band that starts below the fixture's floor and ends above
        // its whole — not of a phrase the response printed. The old rule failed this: at max_chars=3206 the
        // response carried a scripts section and at 3,226 it carried none, because the errors family's newly
        // affordable dangling entry cost the scripts family its whole section.
        Check("ALLOCATION-MONOTONE-IN-MAX-CHARS (#394 pin 3(i)): across every integer cap in a 9,000-wide band, no subject of a three-family response ever spends FEWER characters than it did at a narrower cap — in both transports. A wider cap returning less is what makes the response's own printed remedy false",
            Monotone(all, out var monoDetail), monoDetail);

        // (ii) NO STRANDING — Defect 1's own fixture, made permanent. Its total demand fits inside the default
        // budget, so everything renders and nothing claims a cut. Measured before the rebuild: the errors family
        // alone came to 8,998 characters and the scripts family alone to 38,253, so both whole was 47,251 inside an
        // 80,000 default — and the merged response stopped at 49,440 with 5 of its 40 record sections cut, said
        // truncated, told the caller to raise max_chars=, and left 30,560 characters unspent.
        Check("ALLOCATION-NO-STRANDING (#394 pin 3(ii)): a merged call whose whole demand fits inside its budget renders every unit it has and claims no cut — the same counts as the same sweep with no cap at all, in both transports, at the DEFAULT max_chars nobody tightened",
            NothingStranded(both, out var strandDetail), strandDetail);

        // (iv) DEMAND EXACTNESS. With nothing cut, a subject's allocation IS its measured demand, so allocation and
        // spend are the same number to the byte or the measurement is not measuring the write. This is the arm that
        // refuses the upper-bound concession: a cost that over-counts allocates a subject room it will not spend
        // (Defect 1 one level down), and one that under-counts is a response over its own cap — the json lane
        // returned 6,246 characters against an allowed 5,709 while its rows were measured two nesting levels
        // shallower than they are written.
        Check("ALLOCATION-EQUALS-SPEND (#394 pin 3(iv)): on a full three-family response with nothing cut, every governed subject spends EXACTLY what it was allocated — in both transports, which measure their units in different ways and must both be exact",
            AllocationEqualsSpend(all, out var exactDetail), exactDetail);

        // ---- SCOPE-SENTENCE ------------------------------------------------------------------------
        var defaulted = new CheckSweep(Sel(), errors);
        var defText = Wire.RenderCheck(defaulted, 0);
        var defJson = JsonWire.RenderCheck(defaulted, 0);
        string spelling = SweepFamilySelection.Spelling(SweepFamily.Scripts);
        string describes = SweepFamilySelection.Describe(SweepFamily.Scripts);
        Check("SCOPE-SENTENCE-DEFAULT: findings= omitted states that it ran the default family ONLY, names the family it did not run, and spells the findings= that adds it",
            defText.Contains("findings= was not given", StringComparison.Ordinal)
            && defText.Contains("did NOT run", StringComparison.Ordinal)
            && defText.Contains(describes, StringComparison.Ordinal)
            && defText.Contains(spelling, StringComparison.Ordinal),
            FirstLineWith(defText, "findings="));

        using (var doc = JsonDocument.Parse(defJson))
        {
            var root = doc.RootElement;
            var notRun = root.GetProperty("families_not_run");
            Check("SCOPE-SENTENCE-DEFAULT (json): the SAME complete sentence, plus the same fact as data — a pin on a lead each transport finishes its own way vouches for neither",
                root.GetProperty("findings_scope").GetString() == defaulted.ScopeSentence()
                && root.GetProperty("findings_scope").GetString()!.Contains("did NOT run", StringComparison.Ordinal)
                && root.GetProperty("findings_defaulted").GetBoolean()
                // TWO registered families the default does not run — the fixture-known count, not Registered.Count-1:
                // a fourth family landing must turn this red so somebody decides what the default means, rather than
                // sliding through on arithmetic that agrees with whatever the list happens to hold.
                && notRun.GetArrayLength() == 2
                && notRun[0].GetProperty("family").GetString() == "scripts"
                && notRun[1].GetProperty("family").GetString() == "dialogue"
                && notRun[0].GetProperty("findings").GetString() == spelling
                && !root.GetProperty("families").TryGetProperty("scripts", out _)
                && !root.GetProperty("families").TryGetProperty("dialogue", out _),
                Trim(defJson));
        }

        var chosen = new CheckSweep(Sel("errors"), errors);
        var chosenText = Wire.RenderCheck(chosen, 0);
        Check("SCOPE-SENTENCE-CHOSEN: a caller who NAMED the family is not told they omitted findings= — the two are different sentences",
            chosenText.Contains("findings= selected:", StringComparison.Ordinal)
            && !chosenText.Contains("findings= was not given", StringComparison.Ordinal)
            && chosenText.Contains(spelling, StringComparison.Ordinal),
            FirstLineWith(chosenText, "findings="));

        Check("SCOPE-SENTENCE-ALL: with every registered family run there is nothing to name as absent, and the sentence says THAT rather than going quiet",
            allText.Contains("ran every findings family", StringComparison.Ordinal)
            && !allText.Contains("did NOT run", StringComparison.Ordinal)
            // …and a call running only two of the three is NOT that sentence: it names the third.
            && !text.Contains("ran every findings family", StringComparison.Ordinal)
            && text.Contains("did NOT run", StringComparison.Ordinal),
            FirstLineWith(allText, "findings="));

        // ---- OFF-ORDER-STATED-PER-FAMILY -----------------------------------------------------------
        var offOrder = new CheckSweep(Sel("errors", "scripts"), errors, scripts, new[] { "FreshPatch.esp" });
        var offText = Wire.RenderCheck(offOrder, 0);
        var offJson = JsonWire.RenderCheck(offOrder, 0);
        int scriptsHeadAt = offText.IndexOf("[scripts] ", StringComparison.Ordinal);
        int skippedAt = offText.IndexOf("did NOT sweep FreshPatch.esp", StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(offJson))
            Check("OFF-ORDER-STATED-PER-FAMILY: the family with no off-order lane names the file it did not sweep, inside its OWN section where its zero counts are — in both transports",
                skippedAt > scriptsHeadAt && scriptsHeadAt >= 0
                && offText.Contains("only the errors family has an off-order lane", StringComparison.Ordinal)
                && doc.RootElement.GetProperty("families").GetProperty("scripts")
                      .GetProperty("off_order_not_swept").GetString()!.Contains("FreshPatch.esp", StringComparison.Ordinal)
                && !doc.RootElement.GetProperty("families").GetProperty("errors").TryGetProperty("off_order_not_swept", out _),
                $"scriptsHead@{scriptsHeadAt} skipped@{skippedAt}");

        // ---- PLAN-LEAVES-EMPTY-SUBJECTS-OUT --------------------------------------------------------
        var noFindings = scripts with { Reports = Array.Empty<RecordScriptFindings>() };
        var emptyPlan = new CheckSweep(Sel("errors", "scripts"), errors, noFindings).Plan();
        Check("PLAN-LEAVES-EMPTY-SUBJECTS-OUT: a family whose subjects have no rows is not in the plan at all — a share held for rows that do not exist is the waste the ruled rule was chosen over",
            emptyPlan.Count == 1 && emptyPlan[0].Family == SweepFamily.Errors
            && emptyPlan[0].Subjects.Contains(SweepSubject.PluginSections)
            && emptyPlan[0].Subjects.Contains(SweepSubject.DanglingEntries),
            $"plan=[{string.Join(", ", emptyPlan.Select(p => p.Family + ":" + p.Subjects.Count))}]");

        // ---- CLASS-TOKEN-ROUND-TRIP ----------------------------------------------------------------
        var roundTripBad = new List<string>();
        foreach (var c in new[] { ErrorFindingClass.Dangling, ErrorFindingClass.MissingMasters, ErrorFindingClass.All })
        {
            if (!SweepFindings.TryParseErrorClasses(SweepFindings.Tokens(c).ToList(), out var back, out _) || back != c)
                roundTripBad.Add($"errors:{c} -> {string.Join("+", SweepFindings.Tokens(c))} -> {back}");
        }
        foreach (var c in new[] { ScriptFindingClass.UnboundObject, ScriptFindingClass.UnboundScalar,
                                  ScriptFindingClass.BoundNull, ScriptFindingClass.UnboundObject | ScriptFindingClass.UnboundScalar,
                                  ScriptFindingClass.UnboundObject | ScriptFindingClass.BoundNull, ScriptFindingClass.All })
        {
            if (!SweepFindings.TryParseScriptClasses(SweepFindings.Tokens(c).ToList(), out var back, out _) || back != c)
                roundTripBad.Add($"scripts:{c} -> {string.Join("+", SweepFindings.Tokens(c))} -> {back}");
        }
        Check("CLASS-TOKEN-ROUND-TRIP: every class set the merged parser produces spells tokens the family parsers read back as the same set — the merged tool hands each family its classes through exactly this trip",
            roundTripBad.Count == 0, string.Join("; ", roundTripBad));

        // ---- REGISTERED-IS-THE-MEMBERSHIP ----------------------------------------------------------
        // The seam this fold closed: Vocabulary offers every REGISTERED family's token, and TryParse used to accept
        // a hand-written case per family. A family in the list without a case was one the refusal named and the
        // parser rejected — a response spelling a call that does not work.
        var unaskable = new List<string>();
        foreach (var f in SweepFamilySelection.Registered)
        {
            string tok = SweepFamilySelection.Token(f);
            if (!SweepFamilySelection.Vocabulary.Contains("'" + tok + "'", StringComparison.Ordinal))
                unaskable.Add($"{tok}: the refusal does not offer it");
            if (!SweepFamilySelection.TryParse(new[] { tok }, out var sel, out var perr) || !sel.Ran.Contains(f))
                unaskable.Add($"{tok}: the refusal offers it and the parser rejects it ({perr ?? "not selected"})");
        }
        Check("REGISTERED-IS-THE-MEMBERSHIP: every family the refusal OFFERS is one the parser ACCEPTS — the two read one list, so they cannot name different sets",
            unaskable.Count == 0, string.Join("; ", unaskable));

        // ---- DIALOGUE-REFUSED-WITHOUT-SEEDS --------------------------------------------------------
        // F1.2's cost-refusal, and the thing that makes it family-LOCAL: it must not refuse a call another family
        // answered. Driven through DialogueSweep.Run, the real parse path, not a hand-built result.
        var unseeded = DialogueSweep_Run(null, 1000);
        var mixed = new CheckSweep(Sel("errors", "dialogue"), errors, null, null, unseeded);
        var mixedText = Wire.RenderCheck(mixed, 0);
        var mixedJson = JsonWire.RenderCheck(mixed, 0);
        if (Environment.GetEnvironmentVariable("HC_DUMP") is not null)
            Console.WriteLine("=== MIXED ===\n" + mixedText + "\n=== END ===");
        using (var doc = JsonDocument.Parse(mixedJson))
        {
            var fams = doc.RootElement.GetProperty("families");
            Check("DIALOGUE-REFUSED-WITHOUT-SEEDS: an unseeded dialogue family refuses on cost IN ITS OWN SECTION, spells the seeds= that works, and does not refuse the errors family's answer",
                unseeded.Error is not null
                && mixedText.Contains("[dialogue] ", StringComparison.Ordinal)
                && mixedText.Contains("will NOT sweep the whole load order", StringComparison.Ordinal)
                && mixedText.Contains("82,343", StringComparison.Ordinal)
                && mixedText.Contains("seeds=[\"XXXXXX:Plugin.esp\"]", StringComparison.Ordinal)
                // …and a refused family asserts NO completeness: "every one of the 0 topic(s) these seeds own is
                // listed" over a validation that never ran is the "looked, found none" reading this whole surface
                // exists to prevent.
                && !mixedText.Contains("topic(s) these seeds own", StringComparison.Ordinal)
                && !fams.GetProperty("dialogue").GetProperty("accounting").TryGetProperty("dialogue_topics_found", out _)
                && !fams.GetProperty("dialogue").GetProperty("accounting").TryGetProperty("seeds_validated", out _)
                && !fams.GetProperty("dialogue").GetProperty("accounting").GetProperty("listing").GetBoolean()
                // the errors family still answered, in full
                && StatedPair(mixedText, " dangling ref(s) found by this sweep appear above") == Npcs
                && !mixedText.StartsWith("error:", StringComparison.Ordinal)
                && fams.GetProperty("dialogue").GetProperty("refused").GetString()!.Contains("seeds=", StringComparison.Ordinal)
                && fams.TryGetProperty("errors", out _),
                Trim(mixedText));
        }

        // ---- REFUSED-FAMILY-DECLARES-NO-SUBJECT-COUNTS ---------------------------------------------
        // BOTH DIRECTIONS of the three conditionals the json accounting gained, asked of ONE response so the two
        // halves cannot be satisfied by different fixtures: the refused family has none of those subjects and must
        // carry none of their counts; the errors family BESIDE IT has the dangling subject and must still carry its
        // roster. Asked of both transports, because the two disagreeing IS the defect — the text lane wrote no
        // accounting line at all for a refused family (CanStateAccounting) while the json lane wrote
        // excluded_plugins_total: 0 and unread_plugins_total: 0 about a plugin scope this family does not have (it is
        // seeded, not swept) and an empty dangling_missing_by_source, which is the ERRORS family's roster.
        using (var doc = JsonDocument.Parse(mixedJson))
        {
            var fams = doc.RootElement.GetProperty("families");
            var dlgAcct = fams.GetProperty("dialogue").GetProperty("accounting");
            var errAcct = fams.GetProperty("errors").GetProperty("accounting");
            Check("REFUSED-FAMILY-DECLARES-NO-SUBJECT-COUNTS: a family that never ran states its refusal and the cap it was given — never zeros about subjects it does not have — while the family beside it that HAS one still states it, and the text lane writes ONE accounting line for the two",
                !dlgAcct.TryGetProperty("excluded_plugins_total", out _)
                && !dlgAcct.TryGetProperty("excluded_plugins_named", out _)
                && !dlgAcct.TryGetProperty("unread_plugins_total", out _)
                && !dlgAcct.TryGetProperty("unread_plugins_named", out _)
                && !dlgAcct.TryGetProperty("dangling_missing_by_source", out _)
                && !dlgAcct.TryGetProperty("dangling_missing_by_source_total", out _)
                // What IS true of every lane is still stated: the cap this call was given, and that it listed nothing.
                // Asked as PRESENCE-then-value, never GetProperty alone: a gate put on one of these makes the field
                // ABSENT, and GetProperty on an absent key throws — which leaves the guard crashing instead of
                // failing, and a sweep reading FAIL lines then records the cell as green. The sabotage sweep caught
                // exactly that on the max_chars cell.
                && dlgAcct.TryGetProperty("max_chars", out var dlgCap) && dlgCap.GetInt32() == Wire.DefaultMaxChars
                && dlgAcct.TryGetProperty("listing", out var dlgListing) && !dlgListing.GetBoolean()
                // The other direction, in the same response: the errors family HAS the dangling subject, so it still
                // carries the roster's two fields — and its own counts are untouched, which is the half a gate put
                // on the wrong predicate would break. (The roster's VALUE is the count of source plugins that lost
                // findings — 0 in a response that cut nothing — and what it is worth is pinned by the roster arms;
                // what this arm asks is that a lane WITH the subject still declares it.)
                && errAcct.TryGetProperty("dangling_missing_by_source", out _)
                && errAcct.TryGetProperty("dangling_missing_by_source_total", out _)
                && errAcct.GetProperty("dangling_found").GetInt32() == Npcs
                // The transports agree: one accounting line for the family that can state one, none for the other.
                && Count(mixedText, ReadSentences.SweepAccountingLead) == 1,
                $"dialogueAcct={Trim(dlgAcct.GetRawText())} textAccountingLines={Count(mixedText, ReadSentences.SweepAccountingLead)}");
        }

        // ---- DIALOGUE-NOT-PLUGIN-SCOPED ------------------------------------------------------------
        var dlgOnly = new CheckSweep(Sel("dialogue"), null, null, null, dialogue);
        var dlgText = Wire.RenderCheck(dlgOnly, 0);
        var dlgJson = JsonWire.RenderCheck(dlgOnly, 0);
        using (var doc = JsonDocument.Parse(dlgJson))
        {
            var fam = doc.RootElement.GetProperty("families").GetProperty("dialogue");
            Check("DIALOGUE-NOT-PLUGIN-SCOPED: the section states that plugins=/exclude= do not narrow this family and that it has no off-order lane — in both transports, beside its own counts",
                dlgText.Contains("seeded, not swept", StringComparison.Ordinal)
                && dlgText.Contains("do NOT scope it", StringComparison.Ordinal)
                && dlgText.Contains("no off-order lane", StringComparison.Ordinal)
                && fam.GetProperty("scope").GetString() == string.Format(ReadSentences.DialogueScopeNote, dialogue.SeedsNamed)
                && fam.GetProperty("seeded_not_swept").GetBoolean(),
                FirstLineWith(dlgText, "scope:"));

            Check($"DIALOGUE-COUNTS: the section states what the validation FOUND ({Topics} topics, {DialogueFindings} findings) above anything a budget can refuse, in both transports",
                dlgText.Contains($"1 seed(s) validated, {Topics} topic(s), {DialogueFindings} finding(s)", StringComparison.Ordinal)
                && fam.GetProperty("topics_validated").GetInt32() == Topics
                && fam.GetProperty("findings_found").GetInt32() == DialogueFindings
                && fam.GetProperty("seeds_named").GetInt32() == 1 + UnreachableSeeds,
                FirstLineWith(dlgText, "seed(s) validated"));
        }

        // ---- DIALOGUE-CLASS-8-ABSENT ---------------------------------------------------------------
        // The split (SPEC §6.1): the effective merged INFO order is records' surface, not this one. The fixture's
        // topics carry an InfoOrder, so a render that forgot the gate would print it — and the boundary has to say
        // where the answer went, or a caller reads a clean dialogue section as having looked.
        Check("DIALOGUE-CLASS-8-ABSENT: no effective-INFO-order render in the dialogue section, and the boundary names the surface that carries it",
            !dlgText.Contains("effective INFO order", StringComparison.Ordinal)
            && !dlgText.Contains("INFO order:", StringComparison.Ordinal)
            && !dlgJson.Contains("info_order\":", StringComparison.Ordinal)
            && dlgText.Contains("records project=info_order", StringComparison.Ordinal)
            && dlgJson.Contains("records project=info_order", StringComparison.Ordinal),
            FirstLineWith(dlgText, "boundary (dialogue)"));

        // ---- DIALOGUE-UNREACHABLE-SEEDS-NAMED ------------------------------------------------------
        var dlgCounts = dialogue with { CountsOnly = true };
        var countsText = Wire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, dlgCounts), 0);
        Check($"DIALOGUE-UNREACHABLE-SEEDS-NAMED: all {UnreachableSeeds} seeds that resolved to nothing are named with why — in the listing lane AND under counts_only, which silences the blocks and not the boundary of the answer",
            Count(dlgText, "NOT validated:") == UnreachableSeeds
            && Count(countsText, "NOT validated:") == UnreachableSeeds
            && countsText.Contains("no per-topic blocks", StringComparison.Ordinal)
            && !countsText.Contains("HcCmTopic00", StringComparison.Ordinal)
            && dlgText.Contains("HcCmTopic00", StringComparison.Ordinal),
            $"listing={Count(dlgText, "NOT validated:")} counts_only={Count(countsText, "NOT validated:")}");

        // ---- DIALOGUE-SEED-BUDGET ------------------------------------------------------------------
        // limit= means SEEDS for this family. The arm asserts the fixture's own arithmetic: five named, two
        // expanded, three never reached — and the accounting has to say so rather than let them read as clean.
        var budgeted = DialogueSweep_Run(new[] { "000001:A.esp", "000002:A.esp", "000003:A.esp", "000004:A.esp", "000005:A.esp" }, 2);
        var budgetText = Wire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, budgeted), 0);
        Check("DIALOGUE-SEED-BUDGET: limit= caps how many SEEDS a call expands, and the accounting names how many it never reached and which knob moves them",
            budgeted.SeedsNamed == 5 && budgeted.Seeds.Count == 2
            && budgetText.Contains("2 of the 5 seed(s) named were validated; 3 were NOT validated", StringComparison.Ordinal)
            && budgetText.Contains("limit=", StringComparison.Ordinal),
            FirstLineWith(budgetText, "seed(s) named were validated"));

        // ---- DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE -------------------------------------------
        // A subject's ceiling is fixed on its FIRST unit against the siblings still pending, so the topic blocks of
        // a ONE-SEED call are capped at half the family's share unless the seed subject says it is finished. The arm
        // measures the topic count at a cap wide enough to bite, against the fixture's own arithmetic: with the
        // share handed back the response carries MORE THAN HALF the topics it owns; without it, at most half.
        //
        // Both directions are fixtured. The multi-seed case is the other arm of the conditional: with two seeds
        // still to write, the seed subject is NOT finished when the first topic renders, and holding its share is
        // the correct answer rather than the missed one.
        // The arm has to be asked at a cap where the SHARE is what decides. At a cap nothing bites under, a
        // one-seed call renders every topic whether or not the share came back — the first spelling of this arm
        // asserted exactly that and the sabotage sweep found it GREEN in both transports.
        //
        // So the cap is chosen from the fixture's own measurements: the response FLOOR (rendered where no body
        // fits) plus room for two thirds of the topics at their measured width. With the share handed back, that
        // many land; with half the room held for seed heads that will never be written, at most half that many can.
        // The expected value is the arithmetic, not a phrase the render prints.
        var oneSeed = DialogueSweep_Run(new[] { "000A01:HcCm.esp" }, 1000);
        var twoSeeds = DialogueSweep_Run(new[] { "000A01:A.esp", "000B02:A.esp" }, 1000);
        var oneSeedSweep = new CheckSweep(Sel("dialogue"), null, null, null, oneSeed);
        int floor = Wire.RenderCheck(oneSeedSweep, 1).Length;
        int blockWidth = TopicBlockWidth(oneSeed);
        int shareCap = floor + blockWidth * (Topics * 2 / 3);
        int atShareCap = Count(Wire.RenderCheck(oneSeedSweep, shareCap), "  topic ");
        int halfShareHolds = (shareCap - floor) / (2 * blockWidth);
        Check($"DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE: at a cap sized for two thirds of the topics, MORE than a half share can hold actually land — the seed subject's unspent room went to the blocks",
            atShareCap > halfShareHolds && atShareCap <= Topics,
            $"cap={shareCap} floor={floor} block={blockWidth} rendered={atShareCap} halfShareHolds={halfShareHolds}");

        // The SAME question of the json lane, whose row width and floor are its own. Threaded because the hand-back
        // is written in both transports and a pin on one of them vouches for nothing about the other — the sabotage
        // sweep found this half green while the text half was red, which is exactly the drift the response layer's
        // one-source rule exists to catch one level up.
        int jsonFloor = JsonWire.RenderCheck(oneSeedSweep, 1).Length;
        int jsonRow = (JsonWire.RenderCheck(oneSeedSweep, 0).Length
                       - JsonWire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, WithTopics(oneSeed, 1)), 0).Length)
                      / (Topics - 1);
        int jsonCap = jsonFloor + jsonRow * (Topics * 2 / 3);
        int jsonRendered = Count(JsonWire.RenderCheck(oneSeedSweep, jsonCap), "\"topic\":");
        int jsonHalfHolds = (jsonCap - jsonFloor) / (2 * jsonRow);
        Check("DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE (json): the same, in the transport's own units — a pin on one lane vouches for nothing about the other",
            jsonRendered > jsonHalfHolds && jsonRendered <= Topics,
            $"cap={jsonCap} floor={jsonFloor} row={jsonRow} rendered={jsonRendered} halfShareHolds={jsonHalfHolds}");

        // …and the other arm of the same conditional: with a second seed head still to write, the subject is NOT
        // finished when the first topic renders, and holding its share is the right answer rather than the missed
        // one. Both seeds' heads and every topic land at a cap wide enough for them.
        int twoSeedTopics = Count(Wire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, twoSeeds), 0), "  topic ");
        Check($"DIALOGUE-UNFINISHED-SEED-KEEPS-ITS-SHARE: a two-seed call still writes both heads and all {Topics * 2} topics — handing the share back early would be as wrong as never handing it back",
            twoSeedTopics == Topics * 2
            && Count(Wire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, twoSeeds), 0), "\nseed ") == 2,
            $"topics={twoSeedTopics}/{Topics * 2}");

        // ---- DIALOGUE-BOUNDARY-UNREFUSABLE ---------------------------------------------------------
        // The whole point of making the standing-limits footer this family's BOUNDARY: it is reserved, so the
        // pressure that cuts the findings it qualifies cannot cut it. Swept, because a disclosure present at the
        // caps a fixture happens to try is not a disclosure that is always present.
        var boundaryMissing = new List<int>();
        foreach (int cap in new[] { 1, 2, 5, 10, 50, 200, 800, 2000, 6000, 12000, 40000 })
        {
            if (!Wire.RenderCheck(dlgOnly, cap).Contains("does NOT mean the dialogue will play as intended", StringComparison.Ordinal))
                boundaryMissing.Add(cap);
        }
        Check("DIALOGUE-BOUNDARY-UNREFUSABLE: the standing-limits claim is this family's boundary, so it is written at every cap — including the ones that admit no findings at all",
            boundaryMissing.Count == 0, $"absent at caps: {string.Join(", ", boundaryMissing)}");

        // ---- ROSTER-STILL-ONE-WITH-THREE -----------------------------------------------------------
        // The claim has two halves and only one of them is observable through a three-family response: with the
        // errors family first and holding a roster, it owns the roster whatever the dialogue family answers. What
        // IS observable is the dialogue-ONLY response — this family reports no unparseable-plugin roster at all,
        // because a seeded validation does not produce one — so the arm asks THAT question, where a wrong answer
        // shows up, as well as the ownership one.
        Check("ROSTER-STILL-ONE-WITH-THREE-FAMILIES: the dialogue family reports no excluded-plugin roster of its own, so the roster stays owned by exactly one accounting however many families run",
            all.RosterOwner != SweepFamily.Dialogue
            && Count(allText, ReadSentences.SweepRosterLead) <= 1
            && dlgOnly.RosterOwner is null
            && dlgOnly.ExcludedPlugins.Count == 0,
            $"owner={all.RosterOwner} rosterLeads={Count(allText, ReadSentences.SweepRosterLead)} "
          + $"dialogueOnlyOwner={dlgOnly.RosterOwner?.ToString() ?? "none"} dialogueOnlyRows={dlgOnly.ExcludedPlugins.Count}");

        // ---- CAP-LADDER ----------------------------------------------------------------------------
        Check("CAP-LADDER: at every integer cap from 1 to 12000 (and one far above) neither transport returns more than it was given, bar the floor, and the json parses",
            CapSweep(both, out var capDetail), capDetail);
        Check("CAP-LADDER (dialogue-inclusive): the same sweep over a response carrying ALL THREE families — a third section, a third accounting and a third boundary to hold inside one cap",
            CapSweep(all, out var allCapDetail), allCapDetail);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "check-guard: ALL PASS" : $"check-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>The caps this invariant is swept over: EVERY INTEGER from 1 to 12000, plus one far above anything
    /// the fixture needs. Enumerated rather than sampled for the reason check-errors-guard enumerates its own — the
    /// defects it exists to catch are BANDS a few hundred characters wide, and which band a fixture bites in moves
    /// whenever the header does.</summary>
    static readonly int[] CapLadder = Enumerable.Range(1, 12000).Append(40000).ToArray();

    /// <summary>The cap invariant, swept, with the FLOOR as the expected value — a property of the FIXTURE (render
    /// it at a cap no body can fit under and measure), never a phrase the response emits about itself. A single
    /// emitted body unit puts an over-cap response past the floor and the arm goes red.</summary>
    static bool CapSweep(CheckSweep s, out string detail)
    {
        var bad = new List<string>();
        int textFloor = Wire.RenderCheck(s, 1).Length;
        int jsonFloor = JsonWire.RenderCheck(s, 1).Length;
        foreach (int cap in CapLadder)
        {
            var text = Wire.RenderCheck(s, cap);
            var json = JsonWire.RenderCheck(s, cap);
            int textAllowed = Math.Max(cap, textFloor + FloorSlack(cap));
            int jsonAllowed = Math.Max(cap, jsonFloor + FloorSlack(cap));
            if (text.Length > textAllowed) bad.Add($"text@{cap}={text.Length} over the allowed {textAllowed} (floor {textFloor})");
            if (json.Length > jsonAllowed) bad.Add($"json@{cap}={json.Length} over the allowed {jsonAllowed} (floor {jsonFloor})");
            try { JsonDocument.Parse(json); }
            catch (Exception ex) { bad.Add($"json@{cap} is not valid json: {ex.GetType().Name}"); }
            if (bad.Count > 8) break;
        }
        detail = bad.Count == 0 ? $"every cap honoured, or bounded by the floor (text {textFloor} / json {jsonFloor})"
                                : string.Join("; ", bad);
        return bad.Count == 0;
    }

    /// <summary>How much the floor may grow between the cap it was measured at and the cap under test: max_chars is
    /// printed inside each family's accounting and inside the overrun notice, each bounded by the cap's own digit
    /// width. Two families print it twice, so the headroom is per family rather than a round number.</summary>
    static int FloorSlack(int cap) => 8 * cap.ToString().Length;

    /// <summary>The dialogue family's fixture, built through <c>DialogueSweep.Run</c> — the real seed grammar — with
    /// a stub validator standing in for the load order. One seed resolves to a quest owning <see cref="Topics"/>
    /// topics; <see cref="UnreachableSeeds"/> more resolve to nothing, which is this family's own excluded scope.
    ///
    /// <para>A result rather than a plugin because this family selects RECORDS, not plugins: what its arms are about
    /// is the seed grammar, the section and the accounting, and a synthesized DIAL/QUST tree would test Mutagen's
    /// writer on the way to the same three. <c>dialogue-validate-guard</c> owns the validation itself.</para></summary>
    static DialogueCheckResult DialogueFixture()
        => DialogueSweep_Run(new[] { "000A01:HcCm.esp", "000B02:HcCm.esp", "not-a-formid" }, 1000);

    /// <summary>Drive the real <c>DialogueSweep.Run</c> with a stub validator: <c>000A01</c> is a quest owning
    /// <see cref="Topics"/> topics, and every other resolvable seed is a named miss.</summary>
    static DialogueCheckResult DialogueSweep_Run(IReadOnlyList<string>? seeds, int limit)
        => DialogueSweep.Run(fk => fk.ID == 0x000A01 || fk.ModKey.Name == "A" ? QuestReport(fk)
                                 : DialogueValidationReport.ForError(fk, "no DIAL, QUST, DLVW or DLBR with this FormID is in the active order"),
                             seeds, limit);

    /// <summary>One quest report whose numbers this file KNOWS: <see cref="Topics"/> topics, each carrying
    /// <see cref="IssuesPerTopic"/> graph issues and <see cref="SilentPerTopic"/> silent voiced line. Each topic also
    /// carries an INFO ORDER, so a render that forgot to gate class 8 would print one and DIALOGUE-CLASS-8-ABSENT
    /// goes red — the arm proves the gate rather than the absence of data to gate.</summary>
    static DialogueValidationReport QuestReport(FormKey seed)
    {
        var topics = new List<TopicValidation>();
        for (int i = 0; i < Topics; i++)
        {
            var topicFk = new FormKey(seed.ModKey, (uint)(0x000C00 + i));
            var infoFk = new FormKey(seed.ModKey, (uint)(0x000D00 + i));
            var issues = Enumerable.Range(0, IssuesPerTopic)
                .Select(n => new DialogueIssue(DialogueIssueSeverity.Problem,
                    $"LinkTo target {topicFk} is not defined by any plugin in the active order (issue {n})"))
                .ToArray();
            var voice = Enumerable.Range(0, SilentPerTopic)
                .Select(n => new VoiceLine(infoFk, $"HcCmTopic{i:D2}", n + 1,
                    $"Sound\\Voice\\HcCm.esp\\MaleNord\\{infoFk.ID:X8}_{n + 1}.fuz", false, null, false,
                    $"Sound\\Voice\\HcCm.esp\\MaleNord\\{infoFk.ID:X8}_{n + 1}.lip", false, false))
                .ToArray();
            topics.Add(new TopicValidation(
                topicFk, $"HcCmTopic{i:D2}", "HcCm.esp",
                InfoCount: 3, ConditionedInfoCount: 2, DeletedInfoCount: 0, FragmentInfoCount: 1,
                Category: "Topic", Subtype: "CUST", SubtypeName: "Custom",
                Issues: issues, VoiceLines: voice,
                VoiceUndetermined: Array.Empty<VoiceUndetermined>(),
                ScriptFindings: Array.Empty<ScriptBindingFinding>())
            {
                InfoOrder = new InfoOrderView(
                    new[] { new InfoOrderEntry(infoFk, 0, "HcCm.esp", InfoPlacement.Tail, 0, false, false) },
                    new[] { "HcCm.esp", "HcCmOther.esp" },
                    Array.Empty<InfoOrderEntry>(), null),
            });
        }
        return new DialogueValidationReport(seed, "quest", "HcCmQuest", "HcCm.esp", topics);
    }

    /// <summary>The same fixture with its seed trimmed to <paramref name="n"/> topics — so a row's width in either
    /// transport can be MEASURED as the difference two renders make, rather than modelled.</summary>
    static DialogueCheckResult WithTopics(DialogueCheckResult r, int n)
    {
        var seed = r.Resolved.First();
        var trimmed = seed with { Report = seed.Report! with { Topics = seed.Report!.Topics.Take(n).ToArray() } };
        var seeds = r.Seeds.Select(s => ReferenceEquals(s, seed) ? trimmed : s).ToArray();
        return r with { Seeds = seeds, TopicsFound = n };
    }

    /// <summary>One topic block's width in the text lane, MEASURED off the fixture's own data through the same
    /// composer the render uses — never named as a constant, because the block's width moves whenever any line
    /// inside it changes and a stale number would quietly stop sizing the cap the share arm needs.</summary>
    static int TopicBlockWidth(DialogueCheckResult r)
    {
        var sb = new System.Text.StringBuilder();
        DialogueWire.AppendTopic(sb, r.Topics.First().Topic, indent: true, int.MaxValue, includeInfoOrder: false);
        return sb.Length;
    }

    /// <summary>Pin 3(i): no subject ever spends less at a wider cap, in either transport. Read off the allocation
    /// the render built rather than off the response's prose — what a subject SPENT is the quantity "renders less"
    /// is about, and counting phrases would only pin the phrases.</summary>
    static bool Monotone(CheckSweep s, out string detail)
    {
        var bad = new List<string>();
        var subjects = s.Plan().SelectMany(p => p.Subjects).Distinct().ToArray();
        foreach (var lane in new[] { "text", "json" })
        {
            var previous = new Dictionary<SweepSubject, (int Cap, int Spent)>();
            for (int cap = 1; cap <= 9000; cap++)
            {
                BoundedBody? body;
                if (lane == "text") Wire.RenderCheck(s, cap, 1000, out body);
                else JsonWire.RenderCheck(s, cap, 1000, out body);
                if (body is null) { bad.Add($"{lane}@{cap}: the render built no allocation"); break; }
                foreach (var subject in subjects)
                {
                    int spent = body.SpentOn(subject);
                    if (previous.TryGetValue(subject, out var was) && spent < was.Spent && bad.Count < 4)
                        bad.Add($"{lane} {subject}: {spent} chars at cap {cap}, {was.Spent} at the narrower {was.Cap}");
                    previous[subject] = (cap, spent);
                }
            }
        }
        detail = bad.Count == 0 ? $"{subjects.Length} subjects, 9,000 caps, both transports, never once less"
                                : string.Join("; ", bad);
        return bad.Count == 0;
    }

    /// <summary>Pin 3(ii): the whole demand fits, so everything renders and nothing claims a cut. Held against the
    /// SAME sweep rendered with no cap at all — the counts a response with unlimited room produces are the counts
    /// a response with enough room owes.</summary>
    static bool NothingStranded(CheckSweep s, out string detail)
    {
        const int Default = 80000;   // what max_chars= defaults to; the cap Defect 1 stranded 30,560 characters of
        var bad = new List<string>();

        string uncapped = Wire.RenderCheck(s, 0);
        string capped = Wire.RenderCheck(s, Default, 1000, out var body);
        // The fixture has to FIT, or the arm is asking nothing.
        if (uncapped.Length >= Default) bad.Add($"the uncapped response is {uncapped.Length} chars — the fixture no longer fits the default and this arm proves nothing");
        foreach (var unit in new[] { "[ERROR] ", "[UNBOUND] ", "  dangling ref " })
        {
            int whole = Count(uncapped, unit), got = Count(capped, unit);
            if (got != whole) bad.Add($"text '{unit.Trim()}': {got} rendered inside the default, {whole} with no cap at all");
        }
        // …and nothing may CLAIM a cut it did not make.
        foreach (var claim in new[] { "did not fit this response", "were rendered.", "Raise max_chars=" })
            if (capped.Contains(claim, StringComparison.Ordinal))
                bad.Add($"text claims a cut it did not make: '{claim}'");
        if (body is not null)
            foreach (var subject in s.Plan().SelectMany(p => p.Subjects).Distinct())
                if (body.SpentOn(subject) != body.AllocationOf(subject))
                    bad.Add($"text {subject}: allocated {body.AllocationOf(subject)}, spent {body.SpentOn(subject)} — room left standing");

        var jsonWhole = JsonDocument.Parse(JsonWire.RenderCheck(s, 0)).RootElement;
        var jsonCapped = JsonDocument.Parse(JsonWire.RenderCheck(s, Default)).RootElement;
        foreach (var (family, array) in new[] { ("errors", "plugins"), ("scripts", "records"), ("dialogue", "seeds") })
        {
            int whole = ArrayLength(jsonWhole, family, array), got = ArrayLength(jsonCapped, family, array);
            if (got != whole) bad.Add($"json {family}.{array}: {got} rendered inside the default, {whole} with no cap at all");
        }
        foreach (var family in new[] { "errors", "scripts", "dialogue" })
            if (jsonCapped.GetProperty("families").TryGetProperty(family, out var f)
                && f.TryGetProperty("accounting", out var a)
                && a.TryGetProperty("truncated", out var t) && t.GetBoolean())
                bad.Add($"json {family} reports truncated:true inside a cap its whole answer fits");

        detail = bad.Count == 0
            ? $"the whole response is {uncapped.Length} of the {Default} default, and every unit of it renders"
            : string.Join("; ", bad.Take(4));
        return bad.Count == 0;
    }

    /// <summary>How many elements one family's row array carries, or -1 where the family or the array is absent.</summary>
    static int ArrayLength(JsonElement root, string family, string array)
        => root.GetProperty("families").TryGetProperty(family, out var f) && f.TryGetProperty(array, out var rows)
            ? rows.GetArrayLength() : -1;

    /// <summary>Pin 3(iv): with nothing cut, allocation IS demand, so allocation must equal spend exactly. Asked at
    /// a cap far above the fixture's whole response, in both transports.</summary>
    static bool AllocationEqualsSpend(CheckSweep s, out string detail)
    {
        var bad = new List<string>();
        var seen = new List<string>();
        foreach (var lane in new[] { "text", "json" })
        {
            BoundedBody? body;
            if (lane == "text") Wire.RenderCheck(s, 4000000, 1000, out body);
            else JsonWire.RenderCheck(s, 4000000, 1000, out body);
            if (body is null) { bad.Add($"{lane}: the render built no allocation"); continue; }
            foreach (var subject in s.Plan().SelectMany(p => p.Subjects).Distinct())
            {
                int allocated = body.AllocationOf(subject), spent = body.SpentOn(subject);
                if (spent == 0) bad.Add($"{lane} {subject}: spent nothing at a cap nothing could cut — the arm would pass on a subject that never rendered");
                else if (allocated != spent) bad.Add($"{lane} {subject}: allocated {allocated}, spent {spent} (off by {allocated - spent})");
                else seen.Add($"{lane}:{subject}={spent}");
            }
        }
        detail = bad.Count == 0 ? string.Join(" ", seen) : string.Join("; ", bad.Take(6));
        return bad.Count == 0;
    }

    static SweepFamilySelection Sel(params string[] tokens)
    {
        SweepFamilySelection.TryParse(tokens.Length == 0 ? null : tokens, out var sel, out var err);
        if (err is not null) throw new InvalidOperationException(err);
        return sel;
    }

    /// <summary>The FIRST number of a "{shown} of the {total}" / "all {n}" pair ending in <paramref name="tail"/>, or
    /// -1 where the sentence is absent. Both accounting leads put the count in the same place.</summary>
    static int StatedPair(string text, string tail)
    {
        int at = text.IndexOf(tail, StringComparison.Ordinal);
        if (at < 0) return -1;
        var head = text[..at];
        var last = new string(head.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (!int.TryParse(last, out var total)) return -1;
        var rest = head[..(head.Length - last.Length)];
        if (rest.EndsWith(" all ", StringComparison.Ordinal)) return total;
        if (!rest.EndsWith(" of the ", StringComparison.Ordinal)) return -1;
        var shown = new string(rest[..^8].Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(shown, out var n) ? n : -1;
    }

    static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    static string FirstLineWith(string text, string needle)
        => text.Split('\n').FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal)) ?? "<absent>";

    static string Trim(string s) => s.Length <= 400 ? s.Replace('\n', '|') : s[..400].Replace('\n', '|') + "…";
}

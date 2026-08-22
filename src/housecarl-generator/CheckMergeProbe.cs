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
///   DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE / -BOTH-SEED-HEADS-KEEP-THEIR-SHARE — the two halves of the
///                               seed subject's share. A one-seed call must be told the seed subject is finished or
///                               half the family's room is held for heads that will never be written; a two-seed
///                               call at a biting cap must still write BOTH heads, because one seed's blocks cannot
///                               spend the room the other seed's head needs. Both asked at a cap sized from the
///                               fixture's own floor and block width, because at a cap nothing bites under either
///                               passes whatever the rule does — which is what the first spellings of them did.
///   ROSTER-STILL-ONE-WITH-THREE-FAMILIES — with all three families running over a build that DID exclude a plugin,
///                               the roster is emitted once, its rows appear once, and one accounting states its
///                               cut. Asked of a DIALOGUE-ONLY response as well, because that is where a dialogue
///                               family claiming a roster of its own would show.
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
            var fams = Obj(doc.RootElement, "families");
            Check("ACCOUNTING-PER-FAMILY (json): each family object carries its OWN accounting and its OWN boundary",
                Num(Obj(Obj(fams, "errors"), "accounting"), "dangling_found") == Npcs
                && Num(Obj(Obj(fams, "scripts"), "accounting"), "record_sections_with_findings") == Weapons
                && Str(Obj(fams, "errors"), "boundary") is { } eb
                && Str(Obj(fams, "scripts"), "boundary") is { } sb2 && eb != sb2,
                Trim(json));
        }

        // ---- EXCLUDED-ROSTER-ONCE -----------------------------------------------------------------
        // Both results carry the SAME roster, because both read it off the same captured build. The response must
        // emit it once and exactly one accounting must state its cut: two would subtract the same rows from the same
        // total twice.
        // TWO rows, not one. With a single row every "the head is written once" claim below is also satisfied by a
        // render repeating the head on EVERY row — sabotaging ComposeExcludedRow to do exactly that left the roster
        // arms green, because one row has one head either way.
        var roster = new Dictionary<string, string>
        {
            ["HcCmBroken.esp"] = "header could not be parsed",
            ["HcCmAlsoBroken.esp"] = "header could not be parsed",
        };
        var withRoster = new CheckSweep(Sel("errors", "scripts"),
                                        errors with { ExcludedPlugins = roster }, scripts with { ExcludedPlugins = roster });
        var rosterText = Wire.RenderCheck(withRoster, 0);
        Check($"EXCLUDED-ROSTER-ONCE: the roster is a SCOPE fact — each of its {roster.Count} rows appears once, under ONE head, however many families ran",
            Count(rosterText, "  HcCmBroken.esp: header could not be parsed\n") == 1
            && Count(rosterText, "  HcCmAlsoBroken.esp: header could not be parsed\n") == 1
            && Count(rosterText, "excluded plugins (could not be parsed") == 1,
            $"rows={Count(rosterText, ": header could not be parsed\n")} heads={Count(rosterText, "excluded plugins (could not be parsed")}");

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

        // ---- EXCLUDED-ROSTER-CUT-IS-REPORTED-ONLY-WHEN-IT-HAPPENED ---------------------------------
        // An accounting can only report what has ALREADY been emitted, and every family's accounting is composed
        // inside the section loop. With the roster emitted after that loop, its rows registered after every
        // accounting had spoken: each one read nought of one roster row rendered, said "1 plugin(s) that could not
        // be parsed are named above" was a CUT, and set truncated on a response carrying the whole roster — in both
        // transports, on every merged call with an unparseable plugin (round-1 review, found by two reviewers).
        // Asked at a cap nothing can bite at, so a claim here is false by construction rather than arguable.
        var rosterWhole = Wire.RenderCheck(withRoster, 0);
        var rosterWholeJson = JsonWire.RenderCheck(withRoster, 0);
        var rosterLies = new List<string>();
        if (Count(rosterWhole, "  HcCmBroken.esp: header could not be parsed\n") != 1)
            rosterLies.Add("the roster row is not in the uncapped response at all — the arm would prove nothing");
        if (rosterWhole.Contains(" plugin(s) that could not be parsed are named above.", StringComparison.Ordinal))
            rosterLies.Add("text claims a roster cut on a response that carried the whole roster");
        using (var doc = JsonDocument.Parse(rosterWholeJson))
        {
            var fams = Obj(doc.RootElement, "families");
            foreach (var family in new[] { "errors", "scripts" })
            {
                var acct = Obj(Obj(fams, family), "accounting");
                if (acct is null) { rosterLies.Add($"json {family} carries no accounting at all"); continue; }
                int? tot = Num(acct, "excluded_plugins_total"), named = Num(acct, "excluded_plugins_named");
                if (tot is not null && named is not null && named != tot)
                    rosterLies.Add($"json {family} says {named} of {tot} roster rows named");
                if (Bool(acct, "truncated") == true)
                    rosterLies.Add($"json {family} reports truncated on an uncapped response");
            }
        }
        Check("EXCLUDED-ROSTER-CUT-IS-REPORTED-ONLY-WHEN-IT-HAPPENED: with room for everything, no accounting says the roster was cut and neither family reports truncated — an accounting composed before the rows it counts reports a cut that did not happen",
            rosterLies.Count == 0,
            rosterLies.Count == 0 ? "roster whole, no cut claimed by either family in either transport"
                                  : string.Join("; ", rosterLies));

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
            var notRun = Arr(root, "families_not_selected");
            Check("SCOPE-SENTENCE-DEFAULT (json): the SAME complete sentence, plus the same fact as data — a pin on a lead each transport finishes its own way vouches for neither",
                // Compared against the TEXT LANE'S OWN LINE, not against the composer. Asked against
                // CheckOutcome.ScopeSentence() this was a tautology: the json render calls that method, so the
                // comparison held whatever either transport actually printed, and the cross-transport claim in the
                // arm's own name was vouched for by nothing (round-2 finding C7).
                Str(root, "findings_scope") == FirstLineWith(defText, "findings=")
                && Str(root, "findings_scope")?.Contains("did NOT run", StringComparison.Ordinal) == true
                && Bool(root, "findings_defaulted") == true
                // TWO registered families the default does not run — the fixture-known count, not Registered.Count-1:
                // a fourth family landing must turn this red so somebody decides what the default means, rather than
                // sliding through on arithmetic that agrees with whatever the list happens to hold.
                && notRun?.GetArrayLength() == 2
                && Str(At(notRun, 0), "family") == "scripts"
                && Str(At(notRun, 1), "family") == "dialogue"
                && Str(At(notRun, 0), "findings") == spelling
                && Obj(root, "families") is { } defFams
                && !defFams.TryGetProperty("scripts", out _)
                && !defFams.TryGetProperty("dialogue", out _),
                Trim(defJson));
        }

        var chosen = new CheckSweep(Sel("errors"), errors);
        var chosenText = Wire.RenderCheck(chosen, 0);
        Check("SCOPE-SENTENCE-CHOSEN: a caller who NAMED the family is not told they omitted findings= — the two are different sentences",
            chosenText.Contains("findings= selected, and this response answers for:", StringComparison.Ordinal)
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
        {
            // PRESENCE BEFORE VALUE on the field this arm is ABOUT. Read with GetProperty, a render that stops
            // writing off_order_not_swept made this arm THROW rather than fail — and a guard that throws prints no
            // FAIL line, so the sabotage cell for that very deletion came back green. Found by the second sweep,
            // after the same defect had already been fixed once in GROUNDS-ARE-ONE: the pattern, not the arm.
            bool offPerFamily = doc.RootElement.TryGetProperty("families", out var offFams)
                          && offFams.TryGetProperty("scripts", out var offScripts)
                          && offScripts.TryGetProperty("off_order_not_swept", out var offVal)
                          && offVal.GetString()!.Contains("FreshPatch.esp", StringComparison.Ordinal)
                          && offFams.TryGetProperty("errors", out var offErrors)
                          && !offErrors.TryGetProperty("off_order_not_swept", out _);
            Check("OFF-ORDER-STATED-PER-FAMILY: the family with no off-order lane names the file it did not sweep, inside its OWN section where its zero counts are — in both transports",
                skippedAt > scriptsHeadAt && scriptsHeadAt >= 0
                && offText.Contains("only the errors family has an off-order lane", StringComparison.Ordinal)
                && offPerFamily,
                $"scriptsHead@{scriptsHeadAt} skipped@{skippedAt} json={offPerFamily}");
        }

        // ---- PLAN-LEAVES-EMPTY-SUBJECTS-OUT --------------------------------------------------------
        var noFindings = scripts with { Reports = Array.Empty<RecordScriptFindings>() };
        var emptyPlan = CheckOutcome.For(new CheckSweep(Sel("errors", "scripts"), errors, noFindings)).Plan();
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
            // Read through TryGetProperty rather than GetProperty, because the thing this arm is ABOUT is the
            // response not being an error document — and an error document has no `families` to index. Indexed
            // blindly this threw instead of failing, and a guard that throws stops every arm after it: one
            // sabotage of the family-local rule hid twenty later cells behind a stack trace (found by the
            // orchestration sabotage sweep, 2026-08-22).
            bool merged = doc.RootElement.TryGetProperty("families", out var fams);
            Check("DIALOGUE-REFUSED-WITHOUT-SEEDS: an unseeded dialogue family refuses on cost IN ITS OWN SECTION, spells the seeds= that works, and does not refuse the errors family's answer",
                merged
                && unseeded.Error is not null
                && mixedText.Contains("[dialogue] ", StringComparison.Ordinal)
                && mixedText.Contains("will NOT sweep the whole load order", StringComparison.Ordinal)
                && mixedText.Contains("82,343", StringComparison.Ordinal)
                && mixedText.Contains("seeds=[\"XXXXXX:Plugin.esp\"]", StringComparison.Ordinal)
                // …and a refused family asserts NO completeness: "every one of the 0 topic(s) these seeds own is
                // listed" over a validation that never ran is the "looked, found none" reading this whole surface
                // exists to prevent.
                && !mixedText.Contains("topic(s) these seeds own", StringComparison.Ordinal)
                && Obj(Obj(fams, "dialogue"), "accounting") is { } refusedAcct
                && !refusedAcct.TryGetProperty("dialogue_topics_found", out _)
                && !refusedAcct.TryGetProperty("seeds_validated", out _)
                && Bool(refusedAcct, "listing") == false
                // the errors family still answered, in full
                && StatedPair(mixedText, " dangling ref(s) found by this sweep appear above") == Npcs
                && !mixedText.StartsWith("error:", StringComparison.Ordinal)
                && Str(Obj(fams, "dialogue"), "refused")?.Contains("seeds=", StringComparison.Ordinal) == true
                && fams.TryGetProperty("errors", out _),
                Trim(mixedText));
        }

        // ---- SCOPE-SENTENCE-NAMES-A-REFUSED-FAMILY -------------------------------------------------
        // The response-level sentence is composed from what ANSWERED, not from what was selected. Off the
        // selection, this exact call — the errors family answering beside a dialogue family whose whole section is
        // a cost refusal — led with a sentence naming both as run, and `families_ran` listed a family with no
        // findings in it (round-2 finding B2). The arm asks the sentence AND the data, in both transports.
        var scopeBad = new List<string>();
        string dlgDescribes = SweepFamilySelection.Describe(SweepFamily.Dialogue);
        string mixedScope = FirstLineWith(mixedText, "findings=");
        if (!mixedScope.Contains("did NOT answer for", StringComparison.Ordinal)
            || !mixedScope.Contains(dlgDescribes, StringComparison.Ordinal))
            scopeBad.Add($"the scope sentence does not name the refused family: [{mixedScope}]");
        if (mixedScope.Contains("answers for: " + SweepFamilySelection.Describe(SweepFamily.Errors) + ", " + dlgDescribes,
                                StringComparison.Ordinal))
            scopeBad.Add("the scope sentence still lists the refused family as one it answers for");
        using (var doc = JsonDocument.Parse(mixedJson))
        {
            var root = doc.RootElement;
            var ranNames = Strings(Arr(root, "families_ran"));
            if (ranNames.Contains("dialogue")) scopeBad.Add("families_ran names the refused family");
            if (!ranNames.Contains("errors")) scopeBad.Add("families_ran does not name the family that answered");
            var refusedNames = Arr(root, "families_refused") is { } rf
                ? rf.EnumerateArray().Select(x => Str(x, "family") ?? "<no-family-key>").ToArray()
                : Array.Empty<string>();
            if (!refusedNames.SequenceEqual(new[] { "dialogue" }))
                scopeBad.Add($"families_refused is [{string.Join(",", refusedNames)}], want [dialogue]");
            if (Str(root, "findings_scope") != mixedScope)
                scopeBad.Add("json states a different scope sentence from the text lane");
        }
        Check("SCOPE-SENTENCE-NAMES-A-REFUSED-FAMILY: the response-level sentence and families_ran say what ANSWERED — a family that refused is named as refused, not listed among the ones this response answers for",
            scopeBad.Count == 0, scopeBad.Count == 0 ? mixedScope : string.Join("; ", scopeBad.Take(3)));

        // ---- OFF-ORDER-STATED-EVEN-WHEN-THE-FAMILY-REFUSED ----------------------------------------
        // The other direction of the conditional the off-order sentence used to sit inside. Gated on the scripts
        // family having ANSWERED, the sentence vanished from the call that needs it most: the caller named two
        // plugins, the scripts family refused over one of them, and nothing said why the other was never in its
        // scope either (round-2 finding B4).
        var scriptsRefused = ScriptCheckResult.Fail("exclude= removed every plugin this sweep would have covered.");
        var offRefused = new CheckSweep(Sel("errors", "scripts"), errors, scriptsRefused, new[] { "FreshPatch.esp" });
        var offRefusedText = Wire.RenderCheck(offRefused, 0);
        var offRefusedJson = JsonWire.RenderCheck(offRefused, 0);
        bool offInJson;
        using (var doc = JsonDocument.Parse(offRefusedJson))
            offInJson = doc.RootElement.TryGetProperty("families", out var ofams)
                     && ofams.TryGetProperty("scripts", out var sf)
                     && sf.TryGetProperty("off_order_not_swept", out var ov)
                     && ov.GetString()!.Contains("FreshPatch.esp", StringComparison.Ordinal)
                     && sf.TryGetProperty("refused", out _);
            Check("OFF-ORDER-STATED-EVEN-WHEN-THE-FAMILY-REFUSED: a family with no off-order lane names the file it did not sweep whether it answered or refused — the refusal is about a different plugin, and silence about this one reads as clean",
            offRefusedText.Contains("did NOT sweep FreshPatch.esp", StringComparison.Ordinal)
            && offRefusedText.Contains("[scripts] ", StringComparison.Ordinal)
            && offInJson,
            $"text=[{FirstLineWith(offRefusedText, "did NOT sweep")}] json={offInJson}");

        // ---- GROUNDS-ARE-ONE ------------------------------------------------------------------------
        // The advisor's standing probe, reproduced and then ruled (Aaron-go 2026-08-22). A whole call collapses to
        // ONE error exactly when the grounds are ONE. Two families refusing for DIFFERENT reasons are two answers,
        // and returning the first threw the other away: the caller fixed what they were told about, retried, and met
        // the second ground — each answer true, the response one ground short of what it held. The rule is uniform,
        // with no special case for a single selected family: one family has one ground, so it collapses.
        var errRefused = ErrorCheckResult.Fail("errors-ground: exclude= removed every plugin this sweep would have covered.");
        var distinct = new CheckSweep(Sel("errors", "dialogue"), errRefused, null, null, unseeded);
        var distinctText = Wire.RenderCheck(distinct, 0);
        var distinctJson = JsonWire.RenderCheck(distinct, 0);
        // The CONTROL, in the same shape: both families refusing on the SAME ground is one answer, and one error is
        // what it must return. Without this half the arm would pass on a render that had simply stopped collapsing.
        var sameGround = new CheckSweep(Sel("errors", "dialogue"), ErrorCheckResult.Fail(unseeded.Error!), null, null, unseeded);
        var sameText = Wire.RenderCheck(sameGround, 0);
        var groundsBad = new List<string>();
        if (!distinctText.Contains("errors-ground:", StringComparison.Ordinal))
            groundsBad.Add("the errors family's ground is not in the response at all");
        if (!distinctText.Contains("will NOT sweep the whole load order", StringComparison.Ordinal))
            groundsBad.Add("the dialogue family's ground is not in the response at all");
        if (distinctText.StartsWith("error:", StringComparison.Ordinal))
            groundsBad.Add("two distinct grounds still collapsed to one error string");
        if (!distinctText.Contains("answered for NO family", StringComparison.Ordinal))
            groundsBad.Add("the scope sentence does not say that no family answered");
        using (var doc = JsonDocument.Parse(distinctJson))
        {
            // PRESENCE BEFORE VALUE, every step of the way. What this arm is ABOUT is the response not being an
            // error document, and an error document has none of these keys — read with GetProperty the arm THREW
            // instead of failing, and a guard that throws prints no FAIL line, so a sabotage sweep reading FAIL
            // lines records the cell as green. That is exactly what happened to this arm's own cell: the sweep
            // called it green until the verdict stopped treating a crash as a pass.
            var root = doc.RootElement;
            if (!root.TryGetProperty("families", out var fams)) groundsBad.Add("json collapsed to an error document");
            else
            {
                if (!fams.TryGetProperty("errors", out var ef) || !ef.TryGetProperty("refused", out var eg)
                    || !eg.GetString()!.Contains("errors-ground:", StringComparison.Ordinal))
                    groundsBad.Add("json carries no errors refusal section");
                if (!fams.TryGetProperty("dialogue", out var df) || !df.TryGetProperty("refused", out _))
                    groundsBad.Add("json carries no dialogue refusal section");
            }
            if (!root.TryGetProperty("families_ran", out var ran)) groundsBad.Add("json states no families_ran");
            else if (ran.GetArrayLength() != 0) groundsBad.Add("families_ran names a family that did not answer");
            if (!root.TryGetProperty("families_refused", out var refusedArr))
                groundsBad.Add("json states no families_refused");
            else if (refusedArr.GetArrayLength() != 2)
                groundsBad.Add($"families_refused names {refusedArr.GetArrayLength()} families, want 2");
        }
        if (!sameText.StartsWith("error:", StringComparison.Ordinal))
            groundsBad.Add("ONE shared ground did not collapse to one error — the control failed");
        Check("GROUNDS-ARE-ONE: a call collapses to one error exactly when every refusing family gives the SAME ground; distinct grounds render as sections, each carrying its own, and the response says no family answered",
            groundsBad.Count == 0,
            groundsBad.Count == 0 ? "distinct grounds sectioned, one shared ground collapsed" : string.Join("; ", groundsBad.Take(4)));

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
            // Presence before value at the TOP of the chain too, for the reason the comment below already gives
            // about the leaves: a response that is an error document has no `families`, and indexing it throws
            // rather than failing this arm.
            JsonElement dlgAcct = default, errAcct = default;
            bool merged = doc.RootElement.TryGetProperty("families", out var fams)
                       && fams.TryGetProperty("dialogue", out var dlgFam) && dlgFam.TryGetProperty("accounting", out dlgAcct)
                       && fams.TryGetProperty("errors", out var errFam) && errFam.TryGetProperty("accounting", out errAcct);
            Check("REFUSED-FAMILY-DECLARES-NO-SUBJECT-COUNTS: a family that never ran states its refusal and the cap it was given — never zeros about subjects it does not have — while the family beside it that HAS one still states it, and the text lane writes ONE accounting line for the two",
                merged
                && !dlgAcct.TryGetProperty("excluded_plugins_total", out _)
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
                && Num(errAcct, "dangling_found") == Npcs
                // The transports agree: one accounting line for the family that can state one, none for the other.
                && Count(mixedText, ReadSentences.SweepAccountingLead) == 1,
                merged ? $"dialogueAcct={Trim(dlgAcct.GetRawText())} textAccountingLines={Count(mixedText, ReadSentences.SweepAccountingLead)}"
                       : $"the response is not a merged document: {Trim(mixedJson)}");
        }

        // ---- DIALOGUE-NOT-PLUGIN-SCOPED ------------------------------------------------------------
        var dlgOnly = new CheckSweep(Sel("dialogue"), null, null, null, dialogue);
        var dlgText = Wire.RenderCheck(dlgOnly, 0);
        var dlgJson = JsonWire.RenderCheck(dlgOnly, 0);
        using (var doc = JsonDocument.Parse(dlgJson))
        {
            var fam = Obj(Obj(doc.RootElement, "families"), "dialogue");
            Check("DIALOGUE-NOT-PLUGIN-SCOPED: the section states that plugins=/exclude= do not narrow this family and that it has no off-order lane — in both transports, beside its own counts",
                dlgText.Contains("seeded, not swept", StringComparison.Ordinal)
                && dlgText.Contains("do NOT scope it", StringComparison.Ordinal)
                && dlgText.Contains("no off-order lane", StringComparison.Ordinal)
                && Str(fam, "scope") == FirstLineWith(dlgText, "scope:")
                && Bool(fam, "seeded_not_swept") == true,
                FirstLineWith(dlgText, "scope:"));

            // The counts line states VALIDATED against REACHED, in the outcome's vocabulary — and the head carries
            // all four seed quantities, so a caller reading any one of them knows which population it counts. The
            // expected values are the FIXTURE's arithmetic: one seed produced a report, every named seed was tried.
            Check($"DIALOGUE-COUNTS: the section states what the validation FOUND ({Topics} topics, {DialogueFindings} findings) above anything a budget can refuse, in both transports, with validated stated against reached rather than as a bare number",
                dlgText.Contains($"1 of the {1 + UnreachableSeeds} seed(s) reached were validated, {Topics} topic(s), {DialogueFindings} finding(s)", StringComparison.Ordinal)
                && Num(fam, "topics_found") == Topics
                && Num(fam, "findings_found") == DialogueFindings
                && Num(fam, "seeds_named") == 1 + UnreachableSeeds
                && Num(fam, "seeds_reached") == 1 + UnreachableSeeds
                && Num(fam, "seeds_validated") == 1
                && Num(fam, "seeds_unreachable_total") == UnreachableSeeds,
                FirstLineWith(dlgText, "reached were validated"));
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
            && budgetText.Contains("2 of the 5 seed(s) named were reached; 3 were NOT reached", StringComparison.Ordinal)
            && budgetText.Contains("limit=", StringComparison.Ordinal),
            FirstLineWith(budgetText, "seed(s) named were reached"));

        // ---- DIALOGUE-SCOPE-COUNTS-WHAT-IT-REACHED / -CUT-IS-IN-SEEDS ------------------------------
        // Two round-1 findings on the same response, so they are asked of the same one. The scope sentence said
        // "it validated exactly the 5 seed(s) given in seeds=" from the number NAMED, three lines above an
        // accounting stating that three of them were never reached — a completeness claim its own section
        // contradicted. And the seed subject's own cut borrowed the ERRORS family's sentence, telling the caller
        // how many "plugin section(s)" a family that never opens a plugin had rendered.
        var budgetJson = JsonWire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, budgeted), 0);
        string budgetScope = FirstLineWith(budgetText, "scope:");
        var scopeLies = new List<string>();
        if (!budgetScope.Contains("It reached 2 of the 5 seed(s)", StringComparison.Ordinal))
            scopeLies.Add($"the scope sentence does not state what it reached: [{budgetScope}]");
        if (budgetScope.Contains("all 5 seed(s)", StringComparison.Ordinal))
            scopeLies.Add("the scope sentence still claims it reached every seed named");
        // THE WORD, not just the number. Every seed the call TRIED is counted here, refusals included, so under
        // "validated" this sentence claims a completeness the [X] rows in the same section deny — round 1 found
        // that, its fold reproduced it, and round 2 found it again (finding B1).
        if (budgetScope.Contains("It validated", StringComparison.Ordinal))
            scopeLies.Add("the scope sentence calls the seeds it REACHED validated");
        if (!budgetScope.Contains("limit=", StringComparison.Ordinal))
            scopeLies.Add("the scope sentence names no knob for the seeds it did not reach");
        using (var doc = JsonDocument.Parse(budgetJson))
            if (Str(Obj(Obj(doc.RootElement, "families"), "dialogue"), "scope") is var js
                && js != budgetScope)
                scopeLies.Add($"json states a different scope sentence: [{js}]");
        Check("DIALOGUE-SCOPE-COUNTS-WHAT-IT-REACHED: with limit= below the seed count the scope sentence states what it VALIDATED and what was named, and names the knob — printed from the named figure it claimed a completeness the accounting three lines down denied, in both transports",
            scopeLies.Count == 0,
            scopeLies.Count == 0 ? budgetScope : string.Join("; ", scopeLies));

        // A cap that cuts the seed SECTIONS, so the subject's own cut sentence is written at all. The fixture's
        // arithmetic decides the expected units, never the sentence: this family's rows are seeds, not plugins.
        var seedCutBad = new List<string>();
        bool sawSeedCut = false;
        for (int cap = 400; cap <= 6000; cap += 20)
        {
            var t = Wire.RenderCheck(new CheckSweep(Sel("dialogue"), null, null, null, budgeted), cap);
            if (t.Contains("seed section(s) were rendered", StringComparison.Ordinal)) sawSeedCut = true;
            if (t.Contains("plugin section(s) were rendered", StringComparison.Ordinal))
                seedCutBad.Add($"@{cap}: the dialogue family reports its cut in plugin sections");
        }
        if (!sawSeedCut) seedCutBad.Add("no cap in 400..6000 cut the seed sections — the arm never saw the case it is for");
        Check("DIALOGUE-CUT-IS-STATED-IN-SEEDS: where a cap cuts this family's rows the accounting counts SEED sections — it borrowed the errors family's sentence and told the caller about plugin sections a seeded family never looks at",
            seedCutBad.Count == 0,
            seedCutBad.Count == 0 ? "every cap that cut stated seeds" : string.Join("; ", seedCutBad.Take(3)));

        // ---- DIALOGUE-SEED-FACTS-IN-BOTH-LANES / -ONE-NAME-ONE-VALUE -------------------------------
        // counts_only silences the topic BLOCKS, not the boundary of the answer. The json accounting's whole
        // dialogue block was gated on the topic subject, which counts_only does not declare, so a counts_only call
        // whose seed budget had cut it said nothing at all about that — while the text lane said it (round-1
        // review). And `seeds_validated` was written twice in one family object with two different meanings: the
        // seeds that produced a REPORT at family level, the seeds the budget REACHED in the accounting.
        var budgetCounts = budgeted with { CountsOnly = true };
        var budgetCountsSweep = new CheckSweep(Sel("dialogue"), null, null, null, budgetCounts);
        var budgetCountsText = Wire.RenderCheck(budgetCountsSweep, 0);
        var budgetCountsJson = JsonWire.RenderCheck(budgetCountsSweep, 0);
        var seedFacts = new List<string>();
        // The text lane is the control: it has always stated the seed cut under counts_only.
        if (!budgetCountsText.Contains("2 of the 5 seed(s) named were reached; 3 were NOT reached", StringComparison.Ordinal))
            seedFacts.Add("the TEXT lane does not state the seed cut under counts_only — the arm has no control");
        // BOTH LANES state the same four seed quantities in the family HEAD — that is where the outcome's numbers
        // live — and the accounting states the budget's own cut beside them. The counts_only lane is the one that
        // used to go silent about a seed budget the text lane had already reported.
        foreach (var (lane, body) in new[] { ("counts_only", budgetCountsJson), ("listing", budgetJson) })
            using (var doc = JsonDocument.Parse(body))
            {
                var fam = Obj(Obj(doc.RootElement, "families"), "dialogue");
                void Want(JsonElement? obj, string where, string name, int expect)
                {
                    if (obj is null) { seedFacts.Add($"json {lane}: no {where} object at all"); return; }
                    if (Num(obj, name) is not { } v) seedFacts.Add($"json {lane}: no {where}.{name}");
                    else if (v != expect) seedFacts.Add($"json {lane}: {where}.{name}={v}, want {expect}");
                }
                Want(fam, "head", "seeds_named", 5);
                Want(fam, "head", "seeds_reached", 2);
                Want(fam, "head", "seeds_validated", budgeted.Resolved.Count());
                Want(fam, "head", "seeds_unreachable_total", budgeted.Unresolved.Count);
                Want(Obj(fam, "accounting"), "accounting", "seeds_not_reached_by_budget", 3);
                // …and the topic fields, which the counts_only lane genuinely does not have, stay absent there.
                if (lane == "counts_only"
                    && Obj(fam, "accounting") is { } countsAcct
                    && countsAcct.TryGetProperty("dialogue_topics_rendered", out _))
                    seedFacts.Add("json counts_only states a rendered topic count for a lane that lists no topics");
            }
        Check("DIALOGUE-SEED-FACTS-IN-BOTH-LANES: how many seeds were named, reached, validated and never reached is a fact of the CALL, so counts_only states it too — every one of them in the family head, with the accounting stating only the budget's cut",
            seedFacts.Count == 0,
            seedFacts.Count == 0 ? "named/reached/validated/unreachable in the head of both lanes, the cut in the accounting"
                                 : string.Join("; ", seedFacts.Take(4)));

        // ---- DIALOGUE-FOUR-POPULATIONS-ARE-FOUR-NUMBERS -------------------------------------------
        // B1's property, asked on the ONLY fixture shape that can see it. Every other dialogue fixture in this
        // guard makes two of the four populations equal — `budgeted` reaches five seeds that all resolve, so
        // validated == reached; `dialogue` reaches every seed it names, so named == reached — and on those a
        // sentence printing the wrong one of the pair is indistinguishable from one printing the right one.
        // Measured: swapping reached for validated in the scope note, and reached for named in the counts line,
        // left every dialogue arm green. So this fixture separates all four: five seeds NAMED, the budget REACHES
        // two, one of those VALIDATES and the other is UNREACHABLE.
        var fourPop = DialogueSweep_Run(new[] { "000A01:A.esp", "000B02:B.esp", "000C03:A.esp",
                                                "000D04:A.esp", "000E05:A.esp" }, 2);
        var fourPopSweep = new CheckSweep(Sel("dialogue"), null, null, null, fourPop);
        var fourText = Wire.RenderCheck(fourPopSweep, 0);
        var fourJson = JsonWire.RenderCheck(fourPopSweep, 0);
        var pops = new List<string>();
        if (fourPop.SeedsNamed != 5 || fourPop.Seeds.Count != 2
            || fourPop.Resolved.Count() != 1 || fourPop.Unresolved.Count != 1)
            pops.Add($"FIXTURE: named={fourPop.SeedsNamed} reached={fourPop.Seeds.Count} "
                   + $"validated={fourPop.Resolved.Count()} unreachable={fourPop.Unresolved.Count} — want 5/2/1/1");
        // The scope sentence states REACHED against NAMED. Printed from validated it would say 1 of 5; from named,
        // it would not fire the short arm at all.
        if (!fourText.Contains("It reached 2 of the 5 seed(s)", StringComparison.Ordinal))
            pops.Add($"scope: [{FirstLineWith(fourText, "scope:")}]");
        // The counts line states VALIDATED against REACHED. Printed against named it would say 1 of the 5.
        if (!fourText.Contains("1 of the 2 seed(s) reached were validated", StringComparison.Ordinal))
            pops.Add($"counts: [{FirstLineWith(fourText, "reached were validated")}]");
        using (var doc = JsonDocument.Parse(fourJson))
        {
            var fam = Obj(Obj(doc.RootElement, "families"), "dialogue");
            foreach (var (name, want) in new[] { ("seeds_named", 5), ("seeds_reached", 2),
                                                 ("seeds_validated", 1), ("seeds_unreachable_total", 1) })
                if (Num(fam, name) is not { } v) pops.Add($"json states no {name}");
                else if (v != want) pops.Add($"json {name}={v}, want {want}");
        }
        Check("DIALOGUE-FOUR-POPULATIONS-ARE-FOUR-NUMBERS: on a call whose seeds are named 5, reached 2, validated 1 and unreachable 1, no two of those numbers are interchangeable — the scope sentence states reached against named, the counts line states validated against reached, and the head carries all four",
            pops.Count == 0, pops.Count == 0 ? "5 named / 2 reached / 1 validated / 1 unreachable, each stated as itself"
                                             : string.Join("; ", pops.Take(4)));

        // ---- COUNTS-ONLY-CARRIES-NO-SEEDS-ARRAY ---------------------------------------------------
        // A field named for a subject is present exactly where that subject is. The seeds array was opened outside
        // the lane gate, so the one mode whose whole claim is that it renders no rows carried `"seeds": []` beside
        // a non-zero seeds_validated (round-2 finding B3). Both directions, so a gate that always refuses fails too.
        var seedsArray = new List<string>();
        using (var doc = JsonDocument.Parse(budgetCountsJson))
            if (Arr(Obj(Obj(doc.RootElement, "families"), "dialogue"), "seeds") is not null)
                seedsArray.Add("a counts_only response carries a seeds array");
        using (var doc = JsonDocument.Parse(budgetJson))
        {
            var fam = Obj(Obj(doc.RootElement, "families"), "dialogue");
            if (Arr(fam, "seeds") is not { } listed) seedsArray.Add("a LISTING response carries no seeds array");
            else if (listed.GetArrayLength() == 0) seedsArray.Add("the listing response's seeds array is empty");
            // …and the unreachable roster is in BOTH, by design: a seed nobody could reach bounds the answer.
            if (Arr(fam, "seeds_unreachable") is null) seedsArray.Add("the listing response names no unreachable seeds");
        }
        using (var doc = JsonDocument.Parse(budgetCountsJson))
            if (Arr(Obj(Obj(doc.RootElement, "families"), "dialogue"), "seeds_unreachable") is null)
                seedsArray.Add("the counts_only response names no unreachable seeds");
        Check("COUNTS-ONLY-CARRIES-NO-SEEDS-ARRAY: the seed rows are present exactly where the mode renders them, and the unreachable-seed roster is present in both — it bounds the answer rather than sitting inside it",
            seedsArray.Count == 0,
            seedsArray.Count == 0 ? "seeds in the listing lane only, seeds_unreachable in both" : string.Join("; ", seedsArray));

        // ---- NO-FAMILY-OBJECT-CARRIES-A-DUPLICATE-KEY ---------------------------------------------
        // B5's own property, asked STRUCTURALLY rather than by naming the field that was duplicated: `topics_validated`
        // was written by the family head AND by the accounting, both direct members of families.dialogue. Values
        // agreed, so nothing was WRONG yet — but which one a consumer sees is its parser's choice, and a document
        // whose meaning depends on that is not a document. Asked of every object in every family, in every response
        // this guard has built, so the next duplicate fails here whichever field and whichever family it lands in.
        var dupes = new List<string>();
        foreach (var (label, body) in new[]
                 {
                     ("errors+scripts", json), ("all three", allJson), ("dialogue only", dlgJson),
                     ("dialogue counts_only", budgetCountsJson), ("dialogue listing", budgetJson),
                     ("mixed refusal", mixedJson), ("off-order", offJson),
                 })
            using (var doc = JsonDocument.Parse(body))
                CollectDuplicateKeys(doc.RootElement, label, dupes);
        Check("NO-FAMILY-OBJECT-CARRIES-A-DUPLICATE-KEY: no object in any merged response names one key twice — a document whose meaning depends on the consumer's parser is not a document",
            dupes.Count == 0, dupes.Count == 0 ? "every object's keys are distinct" : string.Join("; ", dupes.Take(4)));

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
        // THE FLOOR IS MEASURED WHERE THE RESPONSE CARRIES NO NOTICE. At cap 1 the response overruns and the
        // overrun sentence is part of what it returns, so a floor taken there overstates this response's fixed
        // part by the length of a sentence the cap under test does not print — which made shareCap wider than it
        // was meant to be and the arm easier than it looked (round-2 finding C8).
        int floor = QuietFloor(oneSeedSweep, (x, c, h) => Wire.RenderCheck(x, c, h), "  topic ");
        int blockWidth = TopicBlockWidth(oneSeed);
        int shareCap = floor + blockWidth * (Topics * 2 / 3);
        int atShareCap = Count(Wire.RenderCheck(oneSeedSweep, shareCap), "  topic ");
        // AND THE CLAIM IS STATED AS ITSELF: the topic blocks spent MORE THAN HALF the row budget this cap left.
        // It used to be written as `atShareCap > (shareCap - floor) / (2 * blockWidth)`, which reduces
        // algebraically to Topics/3 — a constant wearing a measurement's clothes, and one whose terms cancelled
        // whatever the fixture's widths actually were.
        int rowBudget = shareCap - floor;
        Check($"DIALOGUE-FINISHED-SEED-HANDS-BACK-ITS-SHARE: at a cap sized for two thirds of the topics, the topic blocks spend MORE than half the row budget it leaves — the seed subject's unspent room went to the blocks rather than being held for heads that will never be written",
            floor > 0 && atShareCap * blockWidth > rowBudget / 2 && atShareCap <= Topics,
            $"cap={shareCap} floor={floor} rowBudget={rowBudget} block={blockWidth} rendered={atShareCap} "
          + $"spent={atShareCap * blockWidth} half={rowBudget / 2}");

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

        // …and the SECOND SEED's head, at a cap that bites. This replaces an arm asked at the default over a fixture
        // that fits with thousands of characters to spare, where no allocation policy could change the answer — and
        // which was documented as the other arm of a conditional this branch deleted (round-2 review). What is still
        // worth asking, and can now fail, is that the seed subject's share is ITS OWN: the first seed's topic blocks
        // cannot spend the room the second seed's head needs, however tight the cap. The cap is sized from the
        // fixture's own floor and block width, so the cut is real and this file knows it.
        var twoSeedSweep = new CheckSweep(Sel("dialogue"), null, null, null, twoSeeds);
        int twoFloor = Wire.RenderCheck(twoSeedSweep, 1).Length;
        int twoCap = twoFloor + TopicBlockWidth(twoSeeds) * (Topics * 2 / 3);
        var twoText = Wire.RenderCheck(twoSeedSweep, twoCap);
        int twoSeedTopics = Count(twoText, "  topic ");
        Check($"DIALOGUE-BOTH-SEED-HEADS-KEEP-THEIR-SHARE: at a cap that cuts the topic blocks, a two-seed call still writes BOTH seed heads — the seed subject's share is its own, so one seed's blocks cannot spend the room the other seed's head needs",
            Count(twoText, "\nseed ") == 2
            && twoSeedTopics > 0 && twoSeedTopics < Topics * 2,
            $"cap={twoCap} floor={twoFloor} heads={Count(twoText, "\nseed ")} topics={twoSeedTopics}/{Topics * 2}");

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
        // Asked of a three-family response that HAS a roster, and of the roster it is named for. Asked of `all` —
        // whose results carry no ExcludedPlugins at all — every conjunct held whatever the ownership rule returned,
        // and the middle one counted the dangling-by-SOURCE lead rather than the excluded-plugin one, so the cell
        // was about a different roster from the one in its name (round-2 review).
        var allWithRoster = new CheckSweep(Sel("errors", "scripts", "dialogue"),
                                           errors with { ExcludedPlugins = roster },
                                           scripts with { ExcludedPlugins = roster }, null, dialogue);
        var allRosterText = Wire.RenderCheck(allWithRoster, 0);
        Check("ROSTER-STILL-ONE-WITH-THREE-FAMILIES: with all three families running over a build that DID exclude a plugin, the roster is emitted once and owned by the first family that has one — the dialogue family reports none of its own, because a seeded validation produces no such list",
            CheckOutcome.For(allWithRoster).RosterOwner == SweepFamily.Errors
            && Count(allRosterText, "excluded plugins (could not be parsed") == 1
            && Count(allRosterText, ": header could not be parsed\n") == roster.Count
            && Count(allRosterText, " plugin(s) that could not be parsed are named above.") <= 1
            && CheckOutcome.For(dlgOnly).RosterOwner is null
            && CheckOutcome.For(dlgOnly).ExcludedPlugins.Count == 0,
            $"owner={CheckOutcome.For(allWithRoster).RosterOwner} rosterHeads={Count(allRosterText, "excluded plugins (could not be parsed")} "
          + $"rows={Count(allRosterText, ": header could not be parsed\n")}/{roster.Count} "
          + $"dialogueOnlyOwner={CheckOutcome.For(dlgOnly).RosterOwner?.ToString() ?? "none"} dialogueOnlyRows={CheckOutcome.For(dlgOnly).ExcludedPlugins.Count}");

        // ---- RESERVE-COVERS-WHAT-IT-RESERVES-FOR --------------------------------------------------
        // The reserve is a promise about a specific sentence, measured before the body renders; what actually gets
        // written through it is BoundedBody.ReservedWritten. Asked of the real document rather than of the
        // measurement, so it catches BOTH of round 2's reserve findings at once and neither can hide behind slack:
        // the json accounting was measured in a bare root object and written inside families.<token> two levels
        // deeper, and an indented document pays two spaces a line per level (A3); and the measuring constructor
        // copied none of the dialogue quantities, so the worst case wrote none of the fields they gate (B6).
        // Both were absorbed by JsonGlue, which is the posture that produced four unbounded write sites in a row.
        var reserveBad = new List<string>();
        foreach (var (label, sweep) in new[]
                 {
                     ("three families", all), ("three families + roster", allWithRoster),
                     ("dialogue, seed budget cut", new CheckSweep(Sel("dialogue"), null, null, null, budgeted)),
                     ("dialogue, counts_only", budgetCountsSweep),
                     ("errors + refused dialogue", mixed),
                 })
        {
            var oc = CheckOutcome.For(sweep);
            var probeAccts = oc.Accountings(Wire.DefaultMaxChars);
            int jsonReserve = probeAccts.Sum(a => a.JsonAccountingReserve);
            JsonWire.RenderCheck(sweep, 0, 1000, out var jsonBody);
            if (jsonBody is not null && jsonBody.ReservedWritten > jsonReserve)
                reserveBad.Add($"json/{label}: wrote {jsonBody.ReservedWritten} through a reserve of {jsonReserve}");
            int textReserve = probeAccts.Sum(a => a.TextAccountingReserve + a.Boundary.Length + Wire.BoundaryWrap)
                            + oc.Sections.Sum(f => string.Format(ReadSentences.SweepBoundaryLabelFor,
                                                                 SweepFamilySelection.Token(f)).Length);
            Wire.RenderCheck(sweep, 0, 1000, out var textBody);
            if (textBody is not null && textBody.ReservedWritten > textReserve)
                reserveBad.Add($"text/{label}: wrote {textBody.ReservedWritten} through a reserve of {textReserve}");
        }
        Check("RESERVE-COVERS-WHAT-IT-RESERVES-FOR: across five shapes in both transports, what each response actually writes through its reserve fits inside the room that reserve held — measured in the document, so a reserve taken at the wrong depth or missing a lane's fields cannot hide behind slack",
            reserveBad.Count == 0,
            reserveBad.Count == 0 ? "every reserve covered its own writes" : string.Join("; ", reserveBad.Take(4)));


        // ---- RESERVE-DECLARED-IS-RESERVE-DEMANDED --------------------------------------------------
        // The demand pass subtracts a reserve from the row budget BEFORE anything renders, and the render then
        // holds one through BoundedBody.Reserve. They are the same promise measured twice, so they are ONE number.
        // What makes that a checkable property of the real document rather than an argument about two call sites
        // is that `Reserve(` has exactly two callers, one per lane (JsonWire.WriteHistograms, ReadTools
        // .AppendHistograms) — so the render's whole reserve is what those two declared, and the demand pass's
        // whole reserve is what its two matching sites added.
        //
        // A4 is the asymmetry this catches: the json demand pass added a histogram FRAME's cost for EVERY axis,
        // while WriteHistograms reserves one only where `a.Rows is not null` and WriteHistogram returns without
        // writing at all for a null-rows axis. So a counts_only response whose axes are absent had room
        // subtracted from its row budget for objects it never opened. The text lane was already symmetric —
        // `a.TextFixed` is 0 for a null-rows axis, so its unconditional add and its unconditional Reserve agree —
        // and it is swept here anyway, because the property is about both lanes and a lane that is correct today
        // is the one no arm would notice going wrong.
        //
        // The shapes carry a genuinely ABSENT axis, which is what the defect needs: `counts_only=true` with
        // findings= excluding the dangling classes leaves BOTH errors axes null, and the by-SOURCE axis alone is
        // null wherever the link walk built no source tally. Controls with every axis populated are swept beside
        // them so a fixture that quietly lost its null axis shows up as the arm going quiet rather than green.
        var noAxes = errors with { CountsOnly = true, Histogram = null, DanglingBySource = null };
        var targetOnly = errors with
        {
            CountsOnly = true,
            Histogram = new[] { new SweepCount("HcCmGhost.esm", 40) },
            DanglingBySource = null,
        };
        var bothAxes = targetOnly with { DanglingBySource = new[] { new SweepCount("HcCm.esp", 33) } };
        var scNoAxis = scripts with { CountsOnly = true, Histogram = null };
        var scAxis = scripts with { CountsOnly = true, Histogram = new[] { new SweepCount("HcCmSpell", 40) } };

        var reserveMismatch = new List<string>();
        foreach (var (label, sweep) in new[]
                 {
                     ("errors counts_only, both axes absent", new CheckSweep(Sel("errors"), noAxes, null)),
                     ("errors counts_only, by-source absent", new CheckSweep(Sel("errors"), targetOnly, null)),
                     ("errors counts_only, both axes present", new CheckSweep(Sel("errors"), bothAxes, null)),
                     ("scripts counts_only, axis absent", new CheckSweep(Sel("scripts"), null, scNoAxis)),
                     ("scripts counts_only, axis present", new CheckSweep(Sel("scripts"), null, scAxis)),
                     ("both families counts_only, three axes absent", new CheckSweep(Sel("errors", "scripts"), noAxes, scNoAxis)),
                     ("both families counts_only, every axis present", new CheckSweep(Sel("errors", "scripts"), bothAxes, scAxis)),
                     ("three families, listing lane", all),
                 })
        {
            JsonWire.RenderCheck(sweep, 0, 1000, out var jb);
            if (jb is not null && jb.ReserveDeclared != jb.ReserveDemanded)
                reserveMismatch.Add($"json/{label}: the demand pass held back {jb.ReserveDemanded}, the render reserved {jb.ReserveDeclared}");
            Wire.RenderCheck(sweep, 0, 1000, out var tb);
            if (tb is not null && tb.ReserveDeclared != tb.ReserveDemanded)
                reserveMismatch.Add($"text/{label}: the demand pass held back {tb.ReserveDemanded}, the render reserved {tb.ReserveDeclared}");
        }
        Check("RESERVE-DECLARED-IS-RESERVE-DEMANDED: across eight shapes in both transports — including the counts_only ones whose histogram axes are genuinely absent — the room the demand pass subtracted from the row budget is exactly the room the render holds back, so neither lane reserves for an object it never writes",
            reserveMismatch.Count == 0,
            reserveMismatch.Count == 0
                ? "every shape's demanded reserve equalled its declared reserve"
                : string.Join("; ", reserveMismatch.Take(4)));


        // ---- CAP-LADDER ----------------------------------------------------------------------------
        Check("CAP-LADDER: at every integer cap from 1 to 12000 (and one far above) neither transport returns more than it was given, bar the floor, and the json parses",
            CapSweep(both, out var capDetail), capDetail);
        Check("CAP-LADDER (dialogue-inclusive): the same sweep over a response carrying ALL THREE families — a third section, a third accounting and a third boundary to hold inside one cap",
            CapSweep(all, out var allCapDetail), allCapDetail);


        // ---- THE MERGED TOOL'S OWN ORCHESTRATION ---------------------------------------------------
        // Everything above renders a CheckSweep this file built by hand. These drive CheckTools.CheckTool over a
        // synthetic MO2 instance, which is the only place the layer BETWEEN the caller and the render is asked
        // anything at all.
        OrchestrationChecks(tmpDir, Check);

        // ---- THE REMEDY ACROSS A POWER OF TEN ------------------------------------------------------
        // Asked of the arithmetic rather than through a render, for the reason the overrun DISCRIMINATOR is asked
        // that way one guard over: the case is a floor sitting a handful of characters below a power of ten, and no
        // shape the matrix carries lands there — sabotaging the bound away leaves every rendered arm green. Pinned
        // where its logic lives instead of behind a cell that cannot fail.
        //
        // The claim is SELF-CONSISTENCY: the cap the notice names must already cover what the raise to THAT cap
        // adds back, which is one character per place the response prints the cap, per digit the cap gains. A
        // remedy short of its own requirement is one the caller follows and gets the notice again.
        var remedyInconsistent = new List<string>();
        foreach (var (cap, floorLen, sites) in new[]
                 {
                     (5, 9995, 3),      // the crossing: 9,995 + 3x4 is 10,007, and 3x3 would name 10,004
                     (5, 9995, 1),      // the same crossing with one printing site
                     (2000, 5361, 3),   // the measured three-family json case, which does not cross
                     (7, 5361, 3),      // …and its own floor from a one-digit cap
                 })
        {
            var acct = new CheckAccounting(errors, cap);
            var notice = acct.CapTooSmall(floorLen, floorLen, 0, sites);
            if (notice is null) { remedyInconsistent.Add($"cap={cap} floor={floorLen}: no notice on a response {floorLen - cap} chars over its cap"); continue; }
            int at = notice.IndexOf("raise it to at least ", StringComparison.Ordinal);
            int end = at < 0 ? -1 : notice.IndexOf('.', at);
            if (at < 0 || end < 0 || !int.TryParse(notice[(at + 21)..end], out var raiseTo))
            { remedyInconsistent.Add($"cap={cap} floor={floorLen}: the notice names no cap [{notice}]"); continue; }
            int owed = floorLen + sites * (raiseTo.ToString().Length - cap.ToString().Length);
            if (raiseTo < owed)
                remedyInconsistent.Add($"cap={cap} floor={floorLen} sites={sites}: names {raiseTo}, which a response printing the cap {sites} time(s) needs {owed} for");
        }
        Check("REMEDY-SURVIVES-A-POWER-OF-TEN: the cap the overrun notice names already covers what raising to THAT cap adds back — one character per place the response prints the cap, per digit the cap gains — including where the growth itself carries the answer past the next power of ten",
            remedyInconsistent.Count == 0,
            remedyInconsistent.Count == 0 ? "four floors, crossing and not, each remedy self-consistent"
                                          : string.Join("; ", remedyInconsistent));

        // ---- THE SHAPE MATRIX ----------------------------------------------------------------------
        // Everything above asks its question of the shape its own finding was about. The matrix asks the
        // allocation, cap and remedy properties of the INVENTORY of shapes this surface produces — which is what
        // round 2's class (iii) turned out to be: arms written one per finding, so whole shapes had no fixture.
        CheckShapeMatrix.Run(CheckShapeMatrix.Build(errors, scripts, dialogue, unseeded, twoSeeds), Check);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "check-guard: ALL PASS" : $"check-guard: {failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>THE MERGED TOOL'S OWN ORCHESTRATION, driven through <see cref="CheckTools.CheckTool"/> over a
    /// SYNTHETIC MO2 INSTANCE — the surface a caller actually reaches, which until now had ZERO coverage.
    ///
    /// <para><b>Why this exists, measured rather than asserted.</b> Every other cell in this guard builds a
    /// <see cref="CheckSweep"/> by hand and renders it, so everything BETWEEN the caller and the render was covered
    /// by nothing: which families run, how classes are routed to each, whether <c>counts_only</c> and
    /// <c>exclude=</c> reach the families at all, which plugins each family is handed. Two independent sabotages of
    /// that layer came back <c>ci-all</c> ALL PASS — making the dialogue family ignore <c>counts_only</c>, and
    /// deleting the off-order "did NOT sweep" sentence from every response. An orchestration two sabotages cannot
    /// redden has no meaningful green, so the arms land before any review round resumes (advisor ruling,
    /// 2026-08-21).</para>
    ///
    /// <para>The fixture is the established synth-instance pattern: a real ModOrganizer.ini, a profile, mod
    /// folders, and a load order that leaves one plugin ON DISK but OUT of it — which is what makes the off-order
    /// asymmetry reachable at all. Every expected value is fixture-known arithmetic, never a phrase the render
    /// emitted.</para></summary>
    static void OrchestrationChecks(string root, Action<string, bool, string?> Arm)
    {
        const int OrchNpcs = 6;       // NPCs whose Race links into the absent master  ⇒ 6 dangling refs
        const int OrchWeapons = 4;    // weapons whose VMAD binds nothing              ⇒ 4 record sections
        const int OffOrderNpcs = 3;   // the same, in the plugin that is on disk but not in the order
        const int SecondWeapons = 2;  // a SECOND active plugin's, so exclude= has something left to sweep

        string instance = Path.Combine(root, "orch");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string mods = Path.Combine(instance, "mods");
        string game = Path.Combine(root, "orchgame");
        string data = Path.Combine(game, "Data");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(mods); Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + game.Replace(@"\", @"\\") + ")\r\n");

        string modDir = Path.Combine(mods, "OrchMod");
        string twoDir = Path.Combine(mods, "OrchTwo");
        string offDir = Path.Combine(mods, "OrchOff");
        string scripts = Path.Combine(modDir, "Scripts");
        Directory.CreateDirectory(modDir); Directory.CreateDirectory(twoDir);
        Directory.CreateDirectory(offDir); Directory.CreateDirectory(scripts);
        ScriptPropertyCheckProbe.WritePex(Path.Combine(scripts, "HcOrchScript.pex"), "HcOrchScript", parent: null,
            ScriptPropertyCheckProbe.AutoObj("HcOrchSpell", "Spell"),
            ScriptPropertyCheckProbe.AutoScalar("HcOrchChance", "Int", initInt: null));

        // The absent master, written so its FormKeys are real and then left out of the order entirely: every NPC
        // pointing at it is a dangling ref, and every plugin mastering it also reports a MISSING MASTER. Two error
        // CLASSES from one fixture, which is what the class-routing cells need.
        string ghostPath = Path.Combine(root, "HcOrchGhost.esm");
        var ghost = new SkyrimMod(new ModKey("HcOrchGhost", ModType.Master), SkyrimRelease.SkyrimSE);
        var ghostRace = ghost.Races.AddNew(); ghostRace.EditorID = "HcOrchGhostRace";
        var ghostFk = ghostRace.FormKey;
        ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        void WriteMod(string path, string name, int npcs, int weapons)
        {
            var m = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
            for (int i = 0; i < npcs; i++)
            { var n = m.Npcs.AddNew(); n.EditorID = $"{name}Npc{i:D2}"; n.Race.SetTo(ghostFk); }
            for (int i = 0; i < weapons; i++)
            { var w = m.Weapons.AddNew(); w.EditorID = $"{name}Weap{i:D2}"; w.VirtualMachineAdapter = ScriptPropertyCheckProbe.Vmad("HcOrchScript"); }
            using var g = SkyrimMod.CreateFromBinaryOverlay(ghostPath, SkyrimRelease.SkyrimSE);
            m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { g }).Write();
        }
        WriteMod(Path.Combine(modDir, "HcOrch.esp"), "HcOrch", OrchNpcs, OrchWeapons);
        // The second active plugin carries SCRIPT findings only. exclude= aimed at the first must leave these
        // behind: a cell that excluded the ONLY plugin in the order is answered by a refusal rather than by two
        // empty families, which is how the first spelling of the exclude cell passed while seeing nothing.
        WriteMod(Path.Combine(twoDir, "HcOrchTwo.esp"), "HcOrchTwo", 0, SecondWeapons);
        WriteMod(Path.Combine(offDir, "HcOrchOff.esp"), "HcOrchOff", OffOrderNpcs, 0);

        // HcOrchOff.esp is in NO load-order file: on disk, in an enabled mod folder, out of the active order. That
        // is precisely the shape the two families answer differently.
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\nHcOrch.esp\r\nHcOrchTwo.esp\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*HcOrch.esp\r\n*HcOrchTwo.esp\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+OrchOff\r\n+OrchTwo\r\n+OrchMod\r\n");

        var store = new UserConfigStore(Path.Combine(root, "houseCARL.orch.json"));
        using var svc = LoadOrderService.WithInstance(instance, 0, store);

        // THE CONTROL. Without it every cell below could pass on a tool that swept nothing at all.
        var control = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" });
        Arm($"ORCH-CONTROL: the merged TOOL sweeps the synthetic instance and both families find what the fixture planted — {OrchNpcs} dangling refs over 2 active plugins, and {OrchWeapons + SecondWeapons} record sections",
            control.Contains($"{OrchNpcs} dangling ref(s)", StringComparison.Ordinal)
            && control.Contains($"all {OrchWeapons + SecondWeapons} record section(s) found by this sweep appear above.", StringComparison.Ordinal)
            && control.Contains("scanned 2 plugins", StringComparison.Ordinal),
            Trim(control));

        // ---- WHICH FAMILIES RUN. The default, one named family, and both.
        var defaulted = CheckTools.CheckTool(svc);
        var scriptsOnly = CheckTools.CheckTool(svc, findings: new[] { "scripts" });
        Arm("ORCH-FAMILY-SELECTION-ROUTES: findings= omitted runs the ERRORS family alone; findings=['scripts'] runs the scripts family alone; naming both runs both — asserted on which SECTIONS the tool's own response carries",
            Count(defaulted, "\n[errors] ") == 1 && Count(defaulted, "\n[scripts] ") == 0
            && Count(scriptsOnly, "\n[scripts] ") == 1 && Count(scriptsOnly, "\n[errors] ") == 0
            && Count(control, "\n[errors] ") == 1 && Count(control, "\n[scripts] ") == 1,
            $"default errors={Count(defaulted, "\n[errors] ")} scripts={Count(defaulted, "\n[scripts] ")}; "
          + $"scriptsOnly errors={Count(scriptsOnly, "\n[errors] ")} scripts={Count(scriptsOnly, "\n[scripts] ")}");

        // ---- CLASS ROUTING. A class token runs its family NARROWED, and the class nobody asked for reads NOT
        //      CHECKED rather than 0 — the whole point of the vocabulary reaching the family it names.
        var mastersOnly = CheckTools.CheckTool(svc, findings: new[] { "missing_masters" });
        var scalarOnly = CheckTools.CheckTool(svc, findings: new[] { "unbound_scalar" });
        Arm("ORCH-CLASS-ROUTING-REACHES-THE-FAMILY: findings=['missing_masters'] runs the errors family with the dangling walk OFF (no dangling total, the master finding still listed), and findings=['unbound_scalar'] narrows the scripts family the same way — a class token that stopped at the tool would leave both totals intact",
            !mastersOnly.Contains($"{OrchNpcs} dangling ref(s)", StringComparison.Ordinal)
            && mastersOnly.Contains("missing master", StringComparison.OrdinalIgnoreCase)
            && Count(scalarOnly, "\n[scripts] ") == 1
            && scalarOnly.Contains("NOT CHECKED", StringComparison.Ordinal),
            $"masters=[{Trim(mastersOnly)}] scalar=[{Trim(scalarOnly)}]");

        // ---- counts_only, which must reach EVERY family it is handed to. Sabotaging one family to ignore it was
        //      one of the two changes that left ci-all green.
        var counts = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" }, counts_only: true);
        Arm("ORCH-COUNTS-ONLY-REACHES-EVERY-FAMILY: counts_only=true silences BOTH families' listings and leaves both histograms — a family that never received the flag would still be listing its sections",
            !counts.Contains("[ERROR] ", StringComparison.Ordinal)
            && !counts.Contains("[UNBOUND] ", StringComparison.Ordinal)
            && !counts.Contains("[CHECK] ", StringComparison.Ordinal)
            && counts.Contains("dangling ref(s) by ", StringComparison.Ordinal),
            Trim(counts));

        // ---- THE OFF-ORDER ASYMMETRY, end to end. The errors family resolves the name on disk and sweeps it; the
        //      scripts family has no such lane and the response says so, in that family's own section.
        var off = CheckTools.CheckTool(svc, plugins: new[] { "HcOrch.esp", "HcOrchOff.esp" },
                                       findings: new[] { "errors", "scripts" });
        Arm($"ORCH-OFF-ORDER-ASYMMETRY-THROUGH-THE-TOOL: a plugin on disk but OUT of the active order is swept by the errors family ({OrchNpcs + OffOrderNpcs} dangling refs, not {OrchNpcs}) and named as not swept by the scripts family — the sentence a sabotage deleted from every response with nothing noticing",
            off.Contains($"{OrchNpcs + OffOrderNpcs} dangling ref(s)", StringComparison.Ordinal)
            && off.Contains("HcOrchOff.esp", StringComparison.Ordinal)
            && off.Contains("did NOT sweep", StringComparison.Ordinal),
            Trim(off));

        // ---- noneInScope. Every named plugin off-order leaves the scripts family an EMPTY scope, and passing null
        //      instead would widen it to the whole order — a sweep the caller did not ask for.
        var noneInScope = CheckTools.CheckTool(svc, plugins: new[] { "HcOrchOff.esp" },
                                               findings: new[] { "errors", "scripts" });
        Arm($"ORCH-NONE-IN-SCOPE-DOES-NOT-WIDEN: with every named plugin off-order the scripts family sweeps NOTHING rather than the whole order — 0 record sections, not the {OrchWeapons + SecondWeapons} the order holds — and says which file it did not sweep",
            noneInScope.Contains("scanned 0 plugin", StringComparison.Ordinal)
            && !noneInScope.Contains("[UNBOUND] ", StringComparison.Ordinal)
            && noneInScope.Contains("did NOT sweep", StringComparison.Ordinal)
            && noneInScope.Contains($"{OffOrderNpcs} dangling ref(s)", StringComparison.Ordinal),
            Trim(noneInScope));

        // ---- exclude=, which the tool's own description says applies in every SWEPT family. #344's pole is armed
        //      end-to-end for the errors family in check-errors-guard; this is the scripts family's half.
        var excluded = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" },
                                            exclude: new[] { "HcOrch.esp" });
        Arm($"ORCH-EXCLUDE-REACHES-THE-SCRIPTS-FAMILY: exclude= given to the merged tool removes the plugin from the SCRIPTS sweep as well as the errors sweep — one plugin scanned rather than two, {SecondWeapons} record sections rather than {OrchWeapons + SecondWeapons}, and the excluded plugin named nowhere",
            excluded.Contains("scanned 1 plugin ", StringComparison.Ordinal)
            && excluded.Contains($"all {SecondWeapons} record section(s) found by this sweep appear above.", StringComparison.Ordinal)
            && !excluded.Contains("HcOrch.esp", StringComparison.Ordinal)
            && !excluded.Contains($"{OrchNpcs} dangling ref(s)", StringComparison.Ordinal),
            Trim(excluded));

        // ---- THE SCRIPTS FAMILY'S OWN exclude= PATHS (round-2 finding C5). This branch gave that family an
        //      exclude= it never had, and only the ERRORS family's copies of these three paths were armed. Each is
        //      asked of the scripts family alone, so nothing here can be satisfied by its sibling answering.

        // 1. A GROUP member that is not in scope is the ordinary case, never a typo. base_masters expands to the
        //    five vanilla plugins, none of which this synthetic order carries; the typed name beside it IS here.
        //    Validated as one list, this call refused naming a plugin the caller never wrote.
        var groupExclude = CheckTools.CheckTool(svc, findings: new[] { "scripts" },
                                                exclude: new[] { "base_masters", "HcOrch.esp" });
        Arm($"ORCH-EXCLUDE-GROUP-MEMBER-NOT-IN-SCOPE-IS-NOT-A-TYPO: a group token whose members are absent from this order does not refuse the scripts sweep — it drops the ones that ARE here, leaving {SecondWeapons} record sections, while the TYPED name beside it is honoured",
            !groupExclude.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && groupExclude.Contains("scanned 1 plugin ", StringComparison.Ordinal)
            && groupExclude.Contains($"all {SecondWeapons} record section(s) found by this sweep appear above.", StringComparison.Ordinal)
            && !groupExclude.Contains("HcOrch.esp", StringComparison.Ordinal),
            Trim(groupExclude));

        // 2. A TYPED name that matches nothing IS a typo, and refuses rather than sweeping the findings the caller
        //    asked to leave out. Asked of the scripts family alone: one family, one ground, so the refusal is the
        //    whole answer and there is no sibling response to mistake it for.
        var typoExclude = CheckTools.CheckTool(svc, findings: new[] { "scripts" },
                                               exclude: new[] { "HcOrchNoSuch.esp" });
        Arm("ORCH-EXCLUDE-TYPED-NAME-NOT-IN-SCOPE-REFUSES: a plugin NAME in exclude= that this sweep's scope does not contain refuses and names it — an exclusion matching nothing would return exactly the findings the caller asked to leave out",
            typoExclude.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && typoExclude.Contains("HcOrchNoSuch.esp", StringComparison.Ordinal)
            && typoExclude.Contains("not in the scope this sweep would cover", StringComparison.Ordinal),
            Trim(typoExclude));

        // 3. An exclude= that left plugins out SAYS SO in this family's own head. An exclusion that leaves no trace
        //    in the response reads as one that was ignored.
        var noteExclude = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, exclude: new[] { "HcOrch.esp" });
        Arm("ORCH-EXCLUDE-FILTER-NOTE-IS-STATED: the scripts family's head names how many plugins exclude= left out — an exclusion with no trace in the response reads as one the sweep ignored",
            noteExclude.Contains("exclude= left out 1 plugin(s)", StringComparison.Ordinal),
            Trim(noteExclude));

        // ---- a FAMILY-LOCAL scope refusal must not discard the family beside it that answered. The shape the
        //      round-1 reviewers reached by code-read and could not fixture: exclude= is validated against each
        //      family's OWN scope, and the scripts family is handed the ACTIVE subset of plugins=. Naming an
        //      off-order file and excluding the active one empties the SCRIPTS family's scope while leaving the
        //      errors family a file to sweep — and the whole call came back "exclude= removed every plugin this
        //      sweep would have covered", discarding a completed errors sweep and printing a remedy that was
        //      false for the family that had answered.
        var localRefusal = CheckTools.CheckTool(svc, plugins: new[] { "HcOrch.esp", "HcOrchOff.esp" },
                                                findings: new[] { "errors", "scripts" }, exclude: new[] { "HcOrch.esp" });
        var localRefusalJson = CheckTools.CheckTool(svc, plugins: new[] { "HcOrch.esp", "HcOrchOff.esp" },
                                                    findings: new[] { "errors", "scripts" }, exclude: new[] { "HcOrch.esp" },
                                                    format: "json");
        bool jsonLocal;
        try
        {
            using var d = JsonDocument.Parse(localRefusalJson);
            jsonLocal = d.RootElement.TryGetProperty("families", out var fams)
                     && fams.TryGetProperty("scripts", out var sc) && sc.TryGetProperty("refused", out _)
                     && fams.TryGetProperty("errors", out var er) && !er.TryGetProperty("refused", out _);
        }
        catch { jsonLocal = false; }
        // …and the refused family asserts NO completeness. This writer is reachable with a failed result for the
        // first time, so "all 0 record section(s) found by this sweep appear above" over a sweep that never ran is
        // newly possible — the same claim-over-nothing the dialogue accounting was already guarded against.
        bool refusedDeclaresNothing = !localRefusal.Contains("record section(s) found by this sweep appear above", StringComparison.Ordinal);
        try
        {
            using var d = JsonDocument.Parse(localRefusalJson);
            refusedDeclaresNothing &= Obj(Obj(Obj(d.RootElement, "families"), "scripts"), "accounting")
                                      is { } refusedScriptsAcct
                                      && !refusedScriptsAcct.TryGetProperty("record_sections_with_findings", out _);
        }
        catch { refusedDeclaresNothing = false; }
        Arm($"ORCH-A-FAMILY-LOCAL-REFUSAL-DOES-NOT-REFUSE-THE-CALL: exclude= emptying the SCRIPTS family's own scope refuses that family in its own section, asserts no completeness there, and leaves the errors family's {OffOrderNpcs} off-order dangling refs standing — raised to response level it threw away a sweep that had answered and told the caller to narrow exclude=",
            !localRefusal.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && localRefusal.Contains($"{OffOrderNpcs} dangling ref(s)", StringComparison.Ordinal)
            && localRefusal.Contains("exclude= removed every plugin", StringComparison.Ordinal)
            && refusedDeclaresNothing
            && Count(localRefusal, "\n[errors] ") == 1 && Count(localRefusal, "\n[scripts] ") == 1
            && jsonLocal,
            Trim(localRefusal));

        // …and the OTHER direction of the same rule: an input error off the SHARED trio refuses every family, so it
        // is still the whole call's answer rather than the same sentence printed twice.
        var sharedRefusal = CheckTools.CheckTool(svc, findings: new[] { "errors", "scripts" }, type: "NOSUCHTYPE");
        Arm("ORCH-A-SHARED-INPUT-REFUSAL-STILL-REFUSES-THE-CALL: an unknown type= is malformed input for every family that could have run, so the response is ONE error NAMING THE TYPE IT REFUSED — the arm that keeps the cell above from passing by making every refusal family-local",
            sharedRefusal.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            // The GROUND, not just that something began with "error". Asked without it, this cell passed on a tree
            // with no generated/corpus.json at all: the guard's own internal-failure string starts with "error" and
            // carries no section head either, so both conjuncts held over a call that never reached the refusal
            // (round-2 review, observed rather than reasoned).
            && sharedRefusal.Contains("NOSUCHTYPE", StringComparison.Ordinal)
            && Count(sharedRefusal, "\n[errors] ") == 0,
            Trim(sharedRefusal));

        // ---- THE SHARED INPUTS, ON A CALL WHOSE FAMILY NONE OF THEM SCOPE. type= / formids= / exclude= were parsed
        //      inside the two SWEEP families' service entries, which the merged tool calls only where its family
        //      was selected — so findings=['dialogue'] ran with nothing ever looking at them and a typo'd
        //      narrowing came back as an ordinary dialogue answer (Aaron's review of PR #399, finding 3).
        var dlgSeeds = new[] { "0F1AC1:HcOrch.esp" };
        var dlgControl = CheckTools.CheckTool(svc, findings: new[] { "dialogue" }, seeds: dlgSeeds);
        var dlgBadType = CheckTools.CheckTool(svc, findings: new[] { "dialogue" }, seeds: dlgSeeds, type: "NOSUCHTYPE");
        var dlgBadFormid = CheckTools.CheckTool(svc, findings: new[] { "dialogue" }, seeds: dlgSeeds,
                                                formids: new[] { "not-a-formid" });
        var dlgBadExclude = CheckTools.CheckTool(svc, findings: new[] { "dialogue" }, seeds: dlgSeeds,
                                                 exclude: new[] { "base_master" });
        // …and the refusal is a DOCUMENT in json, not a bare string — the whole reason it renders through the
        // normal refusal path instead of returning early from the tool.
        var dlgBadTypeJson = CheckTools.CheckTool(svc, findings: new[] { "dialogue" }, seeds: dlgSeeds,
                                                  type: "NOSUCHTYPE", format: "json");
        bool refusalIsADocument;
        try
        {
            using var d = JsonDocument.Parse(dlgBadTypeJson);
            refusalIsADocument = Str(d.RootElement, "error") is { } e && e.Contains("NOSUCHTYPE", StringComparison.Ordinal);
        }
        catch { refusalIsADocument = false; }
        Arm("ORCH-SHARED-INPUT-IS-CHECKED-BEFORE-FAMILY-DISPATCH: on findings=['dialogue'] — the one family none of them scope — a bad type=, a malformed formids= token and an exclude= value that is neither a filename nor a group each refuse the WHOLE call by name, in both transports, while the same call without them answers. Parsed inside the sweep families' own entries, none of the three was ever looked at",
            dlgBadType.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && dlgBadType.Contains("NOSUCHTYPE", StringComparison.Ordinal)
            && dlgBadFormid.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && dlgBadFormid.Contains("not-a-formid", StringComparison.Ordinal)
            && dlgBadExclude.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && dlgBadExclude.Contains("base_master", StringComparison.Ordinal)
            && refusalIsADocument
            // THE CONTROL, and it is the one this cell turns on: the same call WITHOUT a malformed value
            // answers on the dialogue family's own terms — this fixture plants no DIAL, so that answer is
            // its unresolved-seed ground — and names none of the three values. So each refusal above is
            // the value being refused, not the call shape.
            && dlgControl.Contains("0F1AC1:HcOrch.esp", StringComparison.Ordinal)
            && !dlgControl.Contains("NOSUCHTYPE", StringComparison.Ordinal)
            && !dlgControl.Contains("not-a-formid", StringComparison.Ordinal)
            && !dlgControl.Contains("base_master", StringComparison.Ordinal),
            $"type=[{Trim(dlgBadType)}] formid=[{Trim(dlgBadFormid)}] exclude=[{Trim(dlgBadExclude)}] control=[{Trim(dlgControl)}]");

        // ---- A BLANK plugins= ENTRY. It was filtered out before `noneInScope` was computed, so the caller's scope
        //      silently became "the whole order" — measured at ~468 s on a 3800-plugin order, with plugins=
        //      discarded and nothing saying so (round-3 finding C1). Both ancestors refuse the same input.
        var blankScope = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, plugins: new[] { "  " });
        var namedScope = CheckTools.CheckTool(svc, findings: new[] { "scripts" }, plugins: new[] { "HcOrch.esp" });
        Arm($"ORCH-A-BLANK-PLUGIN-NAME-REFUSES-RATHER-THAN-SWEEPING-THE-ORDER: plugins=['  '] refuses the call by name and sweeps NOTHING — no scripts section, none of the {OrchWeapons + SecondWeapons} record sections the order holds — while the same call naming a real plugin sweeps its {OrchWeapons}",
            blankScope.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && blankScope.Contains("blank plugin name", StringComparison.Ordinal)
            && Count(blankScope, "\n[scripts] ") == 0
            && !blankScope.Contains("record section(s)", StringComparison.Ordinal)
            && namedScope.Contains($"all {OrchWeapons} record section(s) found by this sweep appear above.", StringComparison.Ordinal),
            $"blank=[{Trim(blankScope)}] named=[{Trim(namedScope)}]");

        // ---- THE SPLIT, both halves, on the one call that separates them: every named plugin resolves OFF-ORDER,
        //      so the scripts family's scope is empty BY CONSTRUCTION and its own exclude= pass is skipped
        //      (ScriptPropertyCheck: "exclude= removed every plugin" would be a refusal about a narrowing that did
        //      nothing). Round-3 finding C3 is that the SYNTAX refusal went with it. It no longer does — and the
        //      SCOPE-MATCHING half still stays family-local, which is the settled grounds-are-one design and the
        //      thing a fold that moved both halves up front would have broken.
        var emptyScopeBadToken = CheckTools.CheckTool(svc, findings: new[] { "scripts" },
                                                      plugins: new[] { "HcOrchOff.esp" }, exclude: new[] { "NotAToken" });
        var emptyScopeRealName = CheckTools.CheckTool(svc, findings: new[] { "scripts" },
                                                      plugins: new[] { "HcOrchOff.esp" }, exclude: new[] { "HcOrchTwo.esp" });
        Arm("ORCH-EXCLUDE-SYNTAX-REFUSES-WITH-THE-SCOPE-EMPTY-BY-CONSTRUCTION, AND SCOPE-MATCHING STILL DOES NOT: with every named plugin off-order the scripts family's own exclude= pass is skipped, and an exclude= value that is neither a filename nor a group used to be skipped with it. It refuses by name now. A real filename that this empty scope does not contain is still the family's question, not the call's — the half that would have refused a call another family can answer",
            emptyScopeBadToken.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && emptyScopeBadToken.Contains("NotAToken", StringComparison.Ordinal)
            && !emptyScopeRealName.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && Count(emptyScopeRealName, "\n[scripts] ") == 1,
            $"token=[{Trim(emptyScopeBadToken)}] name=[{Trim(emptyScopeRealName)}]");

        // ---- the dialogue family's cost-refusal, FAMILY-LOCAL, through the tool that composes it beside a family
        //      that answered perfectly well.
        var dlgRefused = CheckTools.CheckTool(svc, findings: new[] { "errors", "dialogue" });
        Arm("ORCH-DIALOGUE-REFUSAL-IS-FAMILY-LOCAL-THROUGH-THE-TOOL: findings=['errors','dialogue'] with no seeds= refuses the dialogue family IN ITS OWN SECTION and still answers the errors family — raised to response level it would refuse a call the errors family answered",
            Count(dlgRefused, "\n[dialogue] ") == 1
            && dlgRefused.Contains("seeds=", StringComparison.Ordinal)
            && dlgRefused.Contains($"{OrchNpcs} dangling ref(s)", StringComparison.Ordinal)
            && !dlgRefused.StartsWith("error:", StringComparison.Ordinal),
            Trim(dlgRefused));

        // ---- format=, which routes the whole response and must refuse a typo rather than fall through to text.
        var asJson = CheckTools.CheckTool(svc, findings: new[] { "errors" }, format: "json");
        var badFormat = CheckTools.CheckTool(svc, findings: new[] { "errors" }, format: "jsonn");
        bool parses;
        try { using var d = JsonDocument.Parse(asJson); parses = d.RootElement.TryGetProperty("families", out _); }
        catch { parses = false; }
        Arm("ORCH-FORMAT-ROUTES-AND-A-TYPO-IS-REFUSED: format='json' returns the merged document and an unknown format is refused by name — never a silent fall-through to text (Q3)",
            parses && badFormat.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            && badFormat.Contains("jsonn", StringComparison.Ordinal),
            $"parses={parses} bad=[{Trim(badFormat)}]");
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
            // …AND IT IS STILL A MERGED DOCUMENT. Without this, every cap above is satisfied by a response that is
            // a bare error string: short, valid json, under any cap. A sweep whose whole claim is "the render
            // honours max_chars" passes most loudly on a render that stopped rendering (round-2 finding C9).
            if (!text.Contains(ReadSentences.SweepMergedTitle, StringComparison.Ordinal))
                bad.Add($"text@{cap} is not a merged response: {Trim(text)}");
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("families", out _))
                    bad.Add($"json@{cap} is not a merged document: {Trim(json)}");
            }
            catch { /* the parse failure above already recorded it */ }
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

    /// <summary>THE RESPONSE'S FIXED PART, measured where the response is not also carrying an overrun notice — the
    /// smallest cap at which this sweep renders none of <paramref name="unit"/> AND prints no remedy.
    ///
    /// <para>Taken at <c>max_chars=1</c> instead, the number includes the overrun sentence, which is characters the
    /// response does not carry at any cap it fits in. Anything sized off that floor — a cap chosen as "the floor
    /// plus room for N units" — is wider than it was meant to be, and the arm using it is easier than it reads
    /// (round-2 finding C8).</para>
    ///
    /// <para>Returns <b>0</b> where no such cap exists, and the caller fails its arm on that rather than carrying
    /// on with a floor quietly substituted behind it — silently falling back to the noisy number is the same shape
    /// as the defect this exists to remove.</para></summary>
    static int QuietFloor(CheckSweep s, Func<CheckSweep, int, int, string> render, string unit, int ceiling = 40_000)
    {
        int noisy = render(s, 1, 1000).Length;
        for (int cap = noisy; cap < ceiling; cap += 16)
        {
            var body = render(s, cap, 1000);
            if (body.Contains("raise it to at least ", StringComparison.Ordinal)) continue;
            if (Count(body, unit) > 0) break;   // past the window: units are landing now
            return body.Length;
        }
        return 0;
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
        var subjects = CheckOutcome.For(s).Plan().SelectMany(p => p.Subjects).Distinct().ToArray();
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
    /// a response with enough room owes.
    ///
    /// <para>Asked at TWO caps, because the default alone cannot see half of what strands. At the default the
    /// fixture has thousands of characters to spare, so a row budget that is a few hundred too SMALL still renders
    /// everything and the cell passes on a response that was quietly short-changed — proven, by sabotaging the
    /// fixed part to be subtracted twice and watching this arm stay green. The second cap is the TIGHT one: the
    /// smallest cap at which nothing is cut, which must sit within a fixture-known margin of the response the
    /// sweep actually returns. Every character the row budget gives away moves that threshold up by one.</para></summary>
    static bool NothingStranded(CheckSweep s, out string detail)
    {
        const int Default = 80000;   // what max_chars= defaults to; the cap Defect 1 stranded 30,560 characters of
        // What the two lanes may legitimately need above their own length before nothing is cut: the reserve each
        // holds for an accounting and a boundary it has not written yet, plus json's one-entry slack. Fixture-known
        // and generous — the point is that it is BOUNDED, so a fixed part counted twice (measured: +1,873 in text,
        // +2,041 in json) lands outside it.
        const int TextSlack = 1500;
        const int JsonSlack = 1500;
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
            foreach (var subject in CheckOutcome.For(s).Plan().SelectMany(p => p.Subjects).Distinct())
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
            if (Bool(Obj(Obj(Obj(jsonCapped, "families"), family), "accounting"), "truncated") == true)
                bad.Add($"json {family} reports truncated:true inside a cap its whole answer fits");

        // THE TIGHT FIT. Monotone in max_chars (pin 3(i)), so the threshold can be found by bisection.
        int textFloor = SmallestWholeCap(c => TextUnits(Wire.RenderCheck(s, c)), TextUnits(uncapped), uncapped.Length);
        string jsonUncapped = JsonWire.RenderCheck(s, 0);
        int jsonFloor = SmallestWholeCap(c => JsonUnits(JsonWire.RenderCheck(s, c)), JsonUnits(jsonUncapped), jsonUncapped.Length);
        if (textFloor < 0) bad.Add("text: no cap up to three times the response renders it whole");
        else if (textFloor - uncapped.Length > TextSlack)
            bad.Add($"text needs {textFloor} to render whole, {textFloor - uncapped.Length} above the {uncapped.Length} it returns (allowed {TextSlack})");
        if (jsonFloor < 0) bad.Add("json: no cap up to three times the response renders it whole");
        else if (jsonFloor - jsonUncapped.Length > JsonSlack)
            bad.Add($"json needs {jsonFloor} to render whole, {jsonFloor - jsonUncapped.Length} above the {jsonUncapped.Length} it returns (allowed {JsonSlack})");

        detail = bad.Count == 0
            ? $"whole at the {Default} default; tight fit text {textFloor} vs {uncapped.Length} returned (+{textFloor - uncapped.Length}), "
            + $"json {jsonFloor} vs {jsonUncapped.Length} (+{jsonFloor - jsonUncapped.Length})"
            : string.Join("; ", bad.Take(4));
        return bad.Count == 0;
    }

    /// <summary>The smallest cap at which the render carries every unit the uncapped one does, by bisection over a
    /// band starting at the uncapped length. Bisection is only sound because the allocation is monotone in
    /// max_chars — the property ALLOCATION-MONOTONE-IN-MAX-CHARS holds independently, so this is a use of that
    /// guarantee and not a second assumption.</summary>
    static int SmallestWholeCap(Func<int, string> unitsAt, string whole, int from)
    {
        int lo = Math.Max(1, from), hi = Math.Max(lo + 1, from * 3);
        if (unitsAt(hi) != whole) return -1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (unitsAt(mid) == whole) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>A response's rendered-unit fingerprint in the text lane — what "renders everything" means, counted
    /// rather than read off a sentence the response prints about itself.</summary>
    static string TextUnits(string t)
        => $"{Count(t, "[ERROR] ")}/{Count(t, "[UNBOUND] ")}/{Count(t, "  dangling ref ")}/{Count(t, "\nseed ")}/{Count(t, "  topic ")}";

    /// <summary>The same fingerprint in json, off the arrays themselves.</summary>
    static string JsonUnits(string j)
    {
        var root = JsonDocument.Parse(j).RootElement;
        return $"{ArrayLength(root, "errors", "plugins")}/{ArrayLength(root, "scripts", "records")}/{ArrayLength(root, "dialogue", "seeds")}";
    }

    /// <summary>How many elements one family's row array carries, or -1 where the family or the array is absent.</summary>
    static int ArrayLength(JsonElement root, string family, string array)
        => Arr(Obj(Obj(root, "families"), family), array) is { } rows ? rows.GetArrayLength() : -1;

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
            foreach (var subject in CheckOutcome.For(s).Plan().SelectMany(p => p.Subjects).Distinct())
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

    /// <summary>READ A FIELD THAT MIGHT NOT BE THERE, without throwing — the three shapes an arm needs.
    ///
    /// <para><b>Why these exist rather than <c>GetProperty</c>.</b> An arm asserting that a field carries a value
    /// is exactly the arm a sabotage DELETING that field must redden. Read with <c>GetProperty</c> it does not
    /// redden, it THROWS — and a guard that throws prints no FAIL line, so a sabotage sweep scanning for FAIL lines
    /// records the cell as a pass and every arm after it is skipped. This session hit that three times in the first
    /// sweep and once more in the second, in four different arms; it is a pattern, not an arm. An absent field is
    /// <c>null</c> here, which fails the comparison it is written into.</para></summary>
    static int? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    /// <summary>NAVIGATE to a nested object or array that might not be there. The leaf readers above only help an
    /// arm that already holds the object the leaf sits in — and the arms here reach their leaves through
    /// <c>families.&lt;token&gt;.accounting</c>, so a sabotage deleting a family or an accounting throws one level
    /// ABOVE the reader that was made safe. These close that: an absent, wrong-kinded or null link is
    /// <c>null</c>, and the <c>JsonElement?</c> overloads let a whole chain be written without a step that can
    /// throw. The chain fails the comparison it is written into, which is what an arm about a missing field is
    /// supposed to do.</summary>
    static JsonElement? Obj(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Object ? v : null;

    static JsonElement? Arr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Array ? v : null;

    static JsonElement? Obj(JsonElement? e, string name) => e is { } v ? Obj(v, name) : null;
    static JsonElement? Arr(JsonElement? e, string name) => e is { } v ? Arr(v, name) : null;
    static int? Num(JsonElement? e, string name) => e is { } v ? Num(v, name) : null;
    static string? Str(JsonElement? e, string name) => e is { } v ? Str(v, name) : null;
    static bool? Bool(JsonElement? e, string name) => e is { } v ? Bool(v, name) : null;

    /// <summary>One element of an array that might not be there, or might be shorter than the arm expects.</summary>
    static JsonElement? At(JsonElement? e, int i)
        => e is { ValueKind: JsonValueKind.Array } a && i >= 0 && i < a.GetArrayLength() ? a[i] : null;

    /// <summary>The strings of an array that might not be there — empty where it is absent, so an arm comparing
    /// the SET of names fails rather than throws.</summary>
    static string[] Strings(JsonElement? e)
        => e is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "<not-a-string>").ToArray()
            : Array.Empty<string>();

    /// <summary>Every object in <paramref name="e"/> that names one key twice, walked whole.
    ///
    /// <para><c>JsonDocument</c> KEEPS both properties — <c>EnumerateObject</c> yields each, and
    /// <c>GetProperty</c> hands back the first — which is exactly why a duplicate is invisible to an arm that reads
    /// fields by name and has to be looked for structurally.</para></summary>
    static void CollectDuplicateKeys(JsonElement e, string where, List<string> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var g in e.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
                if (g.Count() > 1) into.Add($"{where}: '{g.Key}' written {g.Count()} times in one object");
            foreach (var p in e.EnumerateObject()) CollectDuplicateKeys(p.Value, where + "." + p.Name, into);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in e.EnumerateArray()) CollectDuplicateKeys(item, $"{where}[{i++}]", into);
        }
    }

    static string Trim(string s) => s.Length <= 400 ? s.Replace('\n', '|') : s[..400].Replace('\n', '|') + "…";
}

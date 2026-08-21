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
/// whose one plugin carries BOTH families' findings at once: NPCs whose Race links into an absent master (dangling),
/// and weapons whose VMAD binds none of the properties their .pex declares (unbound).
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
///   CAP-LADDER                — every integer cap 1..12000 plus one far above: neither transport returns more than
///                               it was given, bar the floor (the response with no body in it at all), and the json
///                               parses at every one.
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

        var both = new CheckSweep(Sel("errors", "scripts"), errors, scripts);
        var text = Wire.RenderCheck(both, 0);
        var json = JsonWire.RenderCheck(both, 0);

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
                && notRun.GetArrayLength() == 1
                && notRun[0].GetProperty("family").GetString() == "scripts"
                && notRun[0].GetProperty("findings").GetString() == spelling
                && !root.GetProperty("families").TryGetProperty("scripts", out _),
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
            text.Contains("ran every findings family", StringComparison.Ordinal)
            && !text.Contains("did NOT run", StringComparison.Ordinal),
            FirstLineWith(text, "findings="));

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

        // ---- CAP-LADDER ----------------------------------------------------------------------------
        Check("CAP-LADDER: at every integer cap from 1 to 12000 (and one far above) neither transport returns more than it was given, bar the floor, and the json parses",
            CapSweep(both, out var capDetail), capDetail);

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

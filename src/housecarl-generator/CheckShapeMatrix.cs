using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// THE SHAPE MATRIX for the merged <c>check</c> response — an INVENTORY of the shapes this surface can produce,
/// with the allocation, cap and remedy properties driven over EVERY one of them.
///
/// <para><b>Why it exists.</b> Arms written one per finding leave whole shapes with no fixture at all: a merged
/// <c>counts_only</c> render at a biting cap had none, so <c>HistogramByTarget</c>, <c>HistogramBySource</c>,
/// <c>HistogramByProperty</c>, <c>UnreadRows</c> and <c>ScriptScanRows</c> were never allocated, never charged and
/// never asked for monotonicity, no-stranding or exactness. Build the shapes once and drive the properties over
/// all of them, so coverage follows from the inventory rather than from what anyone thought to add.</para>
///
/// <para><b>The inventory.</b> Every subset of the three families, each in the LISTING and the <c>counts_only</c>
/// lane, plus the states a family can be in that are not "ran": REFUSED (its own section is a refusal) and
/// OFF-ORDER (a file swept from disk by both swept families, each naming it in its own section). Crossed with the
/// excluded-plugin ROSTER, which is
/// a response-level subject competing with every family in the response. Both transports and the cap band are swept
/// INSIDE each property rather than being rows of the inventory — a property that holds in one transport and not
/// the other is the drift the response layer's one-source rule exists to catch.</para>
///
/// <para><b>The fixture is TRIMMED on purpose.</b> Each shape's whole response is a few thousand characters, so
/// every property below can be asked at EVERY INTEGER CAP from 1 to above the whole response rather than at a
/// sample. What these arms are about is shape coverage, not scale; <c>check-measure</c> owns scale, on the live
/// order.</para>
///
/// <para><b>Its known-RED case.</b> <c>MATRIX-INSIDE-ITS-CAP</c> reproduces a merged response with a large roster
/// landing OVER <c>max_chars</c> — 4,494 chars against a 4,000 cap, found by hand. A matrix that cannot see a
/// defect found by hand is a broken matrix.</para>
/// </summary>
internal static class CheckShapeMatrix
{
    /// <summary>One shape: a named <see cref="CheckSweep"/> the merged surface can be asked to render.</summary>
    internal readonly record struct Shape(string Name, CheckSweep Sweep);

    // ---- the fixture ------------------------------------------------------------------------------------

    /// <summary>Build the whole inventory from the guard's own results, TRIMMED so a step-1 cap sweep is affordable.
    /// The trims are structural (fewer rows of the same units), never different units: what each shape renders is
    /// what the guard's fixture renders, in smaller quantity.</summary>
    internal static IReadOnlyList<Shape> Build(ErrorCheckResult errors, ScriptCheckResult scripts,
                                               DialogueCheckResult dialogue, DialogueCheckResult refusedDialogue,
                                               DialogueCheckResult multiSeedDialogue)
    {
        var e = TrimErrors(errors, sections: 1, dangling: 4);
        var sc = TrimScripts(scripts, records: 3);
        var d = TrimDialogue(dialogue, topics: 3);
        // A SECOND resolved seed, so the response opens a per-seed `topics` array more than once. Nested arrays
        // that scale with a subject's units are the shape a fixed part measured from one unit of each subject can
        // still be short on, and one-seed fixtures cannot see it.
        var multi = TrimDialogue(multiSeedDialogue, topics: 3);

        var eCounts = CountsOnlyErrors(e);
        var scCounts = CountsOnlyScripts(sc);
        var dCounts = d with { CountsOnly = true };

        // A roster big enough to be worth a share of a tight cap — and, before A1 was fixed, big enough to take the
        // whole body budget and push the fixed part past the cap. Its rows are the same unit the render writes.
        var roster = new Dictionary<string, string>();
        for (int i = 0; i < RosterRows; i++)
            roster["HcMxBroken" + i.ToString("D2") + ".esp"] = "header could not be parsed";

        var shapes = new List<Shape>();
        void Add(string name, CheckSweep s) => shapes.Add(new Shape(name, s));

        // ---- every family subset, in both lanes -----------------------------------------------------
        foreach (var (lane, err, scr, dlg) in new[]
                 {
                     ("listing", e, sc, d),
                     ("counts_only", eCounts, scCounts, dCounts),
                 })
        {
            Add("errors, " + lane, new CheckSweep(Sel("errors"), err));
            Add("scripts, " + lane, new CheckSweep(Sel("scripts"), null, scr));
            Add("dialogue, " + lane, new CheckSweep(Sel("dialogue"), null, null, dlg));
            Add("errors+scripts, " + lane, new CheckSweep(Sel("errors", "scripts"), err, scr));
            Add("errors+dialogue, " + lane, new CheckSweep(Sel("errors", "dialogue"), err, null, dlg));
            Add("scripts+dialogue, " + lane, new CheckSweep(Sel("scripts", "dialogue"), null, scr, dlg));
            Add("all three, " + lane, new CheckSweep(Sel("errors", "scripts", "dialogue"), err, scr, dlg));
        }

        // ---- the ROSTER, a response-level subject competing with every family in the response --------
        Add("errors, listing, roster", new CheckSweep(Sel("errors"), e with { ExcludedPlugins = roster }));
        Add("all three, listing, roster",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           e with { ExcludedPlugins = roster }, sc with { ExcludedPlugins = roster }, d));
        Add("all three, counts_only, roster",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           eCounts with { ExcludedPlugins = roster }, scCounts with { ExcludedPlugins = roster }, dCounts));
        Add("scripts+dialogue, counts_only, roster",
            new CheckSweep(Sel("scripts", "dialogue"), null, scCounts with { ExcludedPlugins = roster }, dCounts));

        // ---- a family that REFUSED, beside families that answered -----------------------------------
        var refusedScripts = ScriptCheckResult.Fail("exclude= removed every plugin this sweep would have covered");
        Add("all three, listing, dialogue refused",
            new CheckSweep(Sel("errors", "scripts", "dialogue"), e, sc, refusedDialogue));
        Add("all three, counts_only, dialogue refused",
            new CheckSweep(Sel("errors", "scripts", "dialogue"), eCounts, scCounts, refusedDialogue));
        Add("errors+scripts, listing, scripts refused",
            new CheckSweep(Sel("errors", "scripts"), e, refusedScripts));
        Add("all three, listing, scripts refused",
            new CheckSweep(Sel("errors", "scripts", "dialogue"), e, refusedScripts, d));
        Add("all three, listing, two refused, roster",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           e with { ExcludedPlugins = roster }, refusedScripts, refusedDialogue));

        // ---- MORE THAN ONE SEED: nested per-seed arrays, which a one-seed shape cannot show --------
        Add("dialogue, listing, two seeds", new CheckSweep(Sel("dialogue"), null, null, multi));
        Add("all three, listing, two seeds",
            new CheckSweep(Sel("errors", "scripts", "dialogue"), e, sc, multi));
        Add("all three, listing, two seeds, roster",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           e with { ExcludedPlugins = roster }, sc with { ExcludedPlugins = roster }, multi));

        // ---- OFF-ORDER: a file swept from disk, in each swept family's own section -------------------
        // Both swept families have the lane, so both carry the roster, the coverage tail and the qualified epoch
        // stamp — two more lines of fixed part than any shape above, plus a collapse line the scripts family adds
        // when one script class's unverifiable note repeated. The fixed-part pass has to measure all of it.
        var eOff = e with { OffOrderScanned = new[] { OffOrderFile } };
        var scOff = sc with { OffOrderScanned = new[] { OffOrderFile }, TotalUnverifiable = 7, UnverifiableCollapsed = 5 };
        Add("errors+scripts, listing, off-order",
            new CheckSweep(Sel("errors", "scripts"), eOff, scOff));
        // …and the same file named on a call where the scripts family REFUSED: a refusal carries no roster of its
        // own, so the errors family's has to still be there beside it.
        Add("errors+scripts, listing, off-order, scripts refused",
            new CheckSweep(Sel("errors", "scripts"), eOff, refusedScripts));
        // ---- NO FAMILY ANSWERED, on DISTINCT grounds ------------------------------------------------
        // The grounds-are-one rule's other side: two families refusing for different reasons render as two refusal
        // sections rather than collapsing to one error, and the scope sentence takes its no-family-answered arm —
        // fixed-part text no other shape in this inventory reaches.
        Add("errors+scripts, both refused on distinct grounds",
            new CheckSweep(Sel("errors", "scripts"),
                           ErrorCheckResult.Fail("the errors family's own ground, which is not the scripts family's"),
                           refusedScripts));
        Add("all three, counts_only, off-order",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           eCounts with { OffOrderScanned = new[] { OffOrderFile } },
                           scCounts with { OffOrderScanned = new[] { OffOrderFile } }, dCounts));
        Add("all three, listing, off-order, roster",
            new CheckSweep(Sel("errors", "scripts", "dialogue"),
                           eOff with { ExcludedPlugins = roster }, scOff with { ExcludedPlugins = roster }, d));

        return shapes;
    }

    /// <summary>How many unparseable plugins the roster shapes carry. Large enough that the roster's demand is a
    /// real fraction of a tight cap's body budget — which is the condition A1 needed, and the condition under which
    /// the roster's share has to be governed rather than taken first.</summary>
    const int RosterRows = 40;

    /// <summary>The file the off-order shapes were swept from disk — a name no other shape in the inventory prints,
    /// so a roster that went missing cannot be satisfied by some other line.</summary>
    const string OffOrderFile = "HcMxFresh.esp";

    static ErrorCheckResult TrimErrors(ErrorCheckResult r, int sections, int dangling)
        => r with
        {
            Reports = r.Reports.Take(sections)
                       .Select(p => p with { Dangling = p.Dangling.Take(dangling).ToArray() })
                       .ToArray(),
        };

    /// <summary>The scripts family trimmed to <paramref name="records"/> sections, the LAST of which carries no
    /// unbound property.
    ///
    /// <para><b>Why one section is reshaped rather than merely taken.</b> A record whose findings are all
    /// bound-but-null or unverifiable opens with <c>[CHECK]</c> instead of <c>[UNBOUND]</c>, and every record in
    /// the guard's own fixture has an unbound property — so that head was a fingerprint term nothing in the
    /// inventory could reach, and the whole-render predicates were blind to it exactly as they were to the
    /// dangling entry (MATRIX-EVERY-FINGERPRINT-TERM-IS-REACHED). It is the same unit at a different width, which
    /// is what the trims here are allowed to be.</para></summary>
    static ScriptCheckResult TrimScripts(ScriptCheckResult r, int records)
    {
        var kept = r.Reports.Take(records).ToArray();
        if (kept.Length > 0)
            kept[^1] = kept[^1] with { Unbound = Array.Empty<UnboundProperty>() };
        return r with { Reports = kept };
    }

    /// <summary>The same fixture with EVERY resolved seed trimmed to <paramref name="topics"/> topics, so a
    /// multi-seed shape stays as small as a one-seed one and the cap band over it stays affordable.</summary>
    static DialogueCheckResult TrimDialogue(DialogueCheckResult r, int topics)
    {
        var trimmed = r.Seeds
            .Select(s => s.Report is { Topics.Count: > 0 }
                       ? s with { Report = s.Report with { Topics = s.Report.Topics.Take(topics).ToArray() } }
                       : s)
            .ToArray();
        return r with { Seeds = trimmed, TopicsFound = topics * r.Resolved.Count() };
    }

    /// <summary>The errors family's <c>counts_only</c> shape: both histogram axes with rows, and the unread rows
    /// that are its honesty layer — the three subjects the listing lane does not have. Built as a result because
    /// what the matrix is about is the RENDER's shapes; <c>check-errors-guard</c> owns the tally itself.</summary>
    static ErrorCheckResult CountsOnlyErrors(ErrorCheckResult r) => r with
    {
        CountsOnly = true,
        Histogram = new[] { new SweepCount("HcCmGhost.esm", 40), new SweepCount("HcCmOther.esm", 7) },
        DanglingBySource = new[] { new SweepCount("HcCm.esp", 33), new SweepCount("HcCmTwo.esp", 14) },
        // Under counts_only the reports list carries the HONESTY layer only: the plugins whose records could not be
        // read. Two of them, so UnreadRows is a subject with more than one unit to allocate.
        Reports = new[]
        {
            new PluginErrors("HcMxUnread01.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(), 0,
                             Array.Empty<string>(), "record enumeration faulted"),
            new PluginErrors("HcMxUnread02.esp", Array.Empty<DanglingRef>(), Array.Empty<string>(), 0,
                             Array.Empty<string>(), "record enumeration faulted"),
        },
    };

    /// <summary>The scripts family's <c>counts_only</c> shape: the by-NAME axis, and the scan-error rows that are
    /// its own honesty layer.</summary>
    static ScriptCheckResult CountsOnlyScripts(ScriptCheckResult r) => r with
    {
        CountsOnly = true,
        Histogram = new[]
        {
            new SweepCount("HcCmSpell", 40), new SweepCount("HcCmOther", 40), new SweepCount("HcCmChance", 40),
        },
        Reports = r.Reports.Take(2)
                   .Select(rec => rec with { ScanError = "the record's VMAD could not be parsed" })
                   .ToArray(),
    };

    static SweepFamilySelection Sel(params string[] tokens)
    {
        SweepFamilySelection.TryParse(tokens.Length == 0 ? null : tokens, out var sel, out var err);
        if (err is not null) throw new InvalidOperationException(err);
        return sel;
    }

    // ---- the properties, driven over every shape ---------------------------------------------------------

    /// <summary>Drive the allocation, cap and remedy properties over the whole inventory. Every arm names the
    /// SHAPE and the CAP it failed at, because a property that fails on one shape of twenty is a shape-coverage
    /// answer and a bare "false" is not.</summary>
    internal static void Run(IReadOnlyList<Shape> shapes, Action<string, bool, string?> Arm)
    {
        var overCap = new List<string>();
        var nonMonotone = new List<string>();
        var remedyBad = new List<string>();
        var unparseable = new List<string>();
        var neverRendered = new List<string>();
        var everRendered = new HashSet<SweepSubject>();
        var oneBudget = new List<string>();
        var honestyBad = new List<string>();
        // WHICH FINGERPRINT TERMS ANY SHAPE EVER REACHES. A term that is 0 on every shape of every cap is a marker
        // nothing emits, and the whole-render predicates built on it are blind to that unit — which is what
        // "  dangling ref " was, on the ONE subject the errors family accounts for a unit at a time. Counted here
        // rather than asserted per marker, so a marker mistyped later is caught by the same rule.
        var termsReached = new HashSet<(string Lane, int Term)>();
        var termCount = new Dictionary<string, int>();
        int noticesFollowed = 0, capsSwept = 0, honestyCells = 0;

        foreach (var shape in shapes)
        {
            foreach (var lane in Lanes)
            {
                int floor = lane.Render(shape.Sweep, 1, out _).Length;
                int whole = lane.Render(shape.Sweep, 0, out _).Length;
                var previous = new Dictionary<SweepSubject, (int Cap, int Spent, int Allocated)>();
                var rendered = new HashSet<SweepSubject>();
                var subjects = Subjects(shape);

                for (int cap = 1; cap <= whole + BandMargin; cap++)
                {
                    string response = lane.Render(shape.Sweep, cap, out var body);
                    capsSwept++;

                    // (1) INSIDE ITS CAP. The one response that may exceed max_chars is the one whose FIXED PART
                    // does not fit, and it says so — so the allowance is the floor, never "whatever it returned".
                    int allowed = Math.Max(cap, floor + FloorSlack(cap));
                    if (response.Length > allowed && overCap.Count < 6)
                        overCap.Add($"{shape.Name} [{lane.Name}] @{cap}: {response.Length} chars, allowed {allowed} (floor {floor})");

                    if (lane.Name == "json")
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(response);
                            // (6) THE TWO HONESTY LAYERS ARE ONE SHAPE. `unread` (errors) and `scan_errors`
                            // (scripts) answer the same kind of question — what this sweep could NOT read — and a
                            // merged response carries both in one document, so a consumer has to parse them the
                            // same way — one keeping a wrapper while the other is a bare array is a defect. Asked
                            // at EVERY cap, because what the wrapper is FOR is saying it was cut, and the cut is
                            // what the band produces.
                            honestyCells += HonestyLayers(doc.RootElement, shape.Name, cap, honestyBad);
                        }
                        catch (Exception ex)
                        {
                            if (unparseable.Count < 4) unparseable.Add($"{shape.Name} @{cap}: {ex.GetType().Name}");
                        }
                    }

                    // (5) ONE BUDGET. The allocation divides what is left after the fixed part and the reserves;
                    // the response-wide emission test holds the SAME number against every unit. Both halves are
                    // asked here, because the two drifting apart is what makes a wider cap render less: the units
                    // never spend more than the room the allocation divided, and the response never turns out to
                    // owe more outside its units than was measured before it started.
                    if (body is not null)
                    {
                        if (body.BodyTotal > body.RowBudget && oneBudget.Count < 6)
                            oneBudget.Add($"{shape.Name} [{lane.Name}] @{cap}: the units spent {body.BodyTotal} of a {body.RowBudget} row budget");
                        if (body.OutstandingHigh > body.ReservedForRows && oneBudget.Count < 6)
                            oneBudget.Add($"{shape.Name} [{lane.Name}] @{cap}: the response owed {body.OutstandingHigh} outside its units, {body.OutstandingHigh - body.ReservedForRows} more than the {body.ReservedForRows} measured before it rendered");
                    }

                    // (2) MONOTONE IN max_chars, read off what each subject SPENT rather than off the prose.
                    if (body is not null)
                        foreach (var subject in subjects)
                        {
                            int spent = body.SpentOn(subject);
                            if (previous.TryGetValue(subject, out var was) && spent < was.Spent && nonMonotone.Count < 6)
                                nonMonotone.Add($"{shape.Name} [{lane.Name}] {subject}: spent {spent} of {body.AllocationOf(subject)} allocated at cap {cap}, {was.Spent} of {was.Allocated} at the narrower {was.Cap}");
                            previous[subject] = (cap, spent, body.AllocationOf(subject));
                            // (4) …and the subject GOT room somewhere in the band. A subject the allocation never
                            // reaches is one every property above is asked of vacuously — which is what class (iii)
                            // turned out to be: five counts_only subjects planned by nothing and charged nowhere.
                            if (spent > 0) rendered.Add(subject);
                        }

                    // (3) THE REMEDY, FOLLOWED. Where the overrun notice fires, the cap it names must clear it in
                    // ONE step — a remedy that has to be followed twice is one the caller cannot act on, and the
                    // roster regression's own remedy looped 6000 -> 6123 -> 6201 -> 6279 indefinitely. Sampled
                    // across the band rather than at every cap: the follow-up is a second render, and the notice
                    // fires at hundreds of consecutive caps.
                    if (cap % RemedyStride == 0 && lane.Notice(response) is { } notice)
                    {
                        noticesFollowed++;
                        if (StatedLength(notice) is not { } stated)
                            remedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: the notice states no length");
                        else if (stated != response.Length && remedyBad.Count < 6)
                            remedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: the notice says {stated} chars, the response is {response.Length}");
                        if (RaiseTo(notice) is not { } raiseTo)
                            remedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: the notice names no cap to raise to");
                        else
                        {
                            if (raiseTo <= cap && remedyBad.Count < 6)
                                remedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: the remedy says raise to {raiseTo}, which is not above the cap it was given");
                            string again = lane.Render(shape.Sweep, raiseTo, out _);
                            if (lane.Notice(again) is not null && remedyBad.Count < 6)
                                remedyBad.Add($"{shape.Name} [{lane.Name}] @{cap}: following the remedy to {raiseTo} left the notice standing ({again.Length} chars)");
                        }
                    }
                }

                foreach (var subject in subjects)
                    if (!rendered.Contains(subject) && neverRendered.Count < 6)
                        neverRendered.Add($"{shape.Name} [{lane.Name}] {subject}: planned, and never given room at any cap in its band");
                everRendered.UnionWith(rendered);

                var terms = Fingerprint(lane, lane.Render(shape.Sweep, 0, out _)).Split('/');
                termCount[lane.Name] = terms.Length;
                for (int i = 0; i < terms.Length; i++)
                    if (int.TryParse(terms[i], out var n) && n > 0) termsReached.Add((lane.Name, i));
            }
        }

        Arm($"MATRIX-INSIDE-ITS-CAP: across {shapes.Count} shapes of the merged response and every integer cap from 1 to above its whole answer, in BOTH transports, no response is longer than the cap it was given — bar the floor, the one arm that says so in band. A roster admitted against the whole body budget before the family heads were written put 4,494 chars against a 4,000 cap",
            overCap.Count == 0,
            overCap.Count == 0 ? $"{capsSwept:N0} cap/shape/transport cells, none over" : string.Join("; ", overCap));

        Arm($"MATRIX-MONOTONE-IN-MAX-CHARS: over the same {shapes.Count} shapes, no subject of any of them ever spends FEWER characters at a wider cap — the property that makes the response's own 'raise max_chars=' remedy true rather than true at the caps somebody tried",
            nonMonotone.Count == 0,
            nonMonotone.Count == 0 ? $"{capsSwept:N0} cells, never once less" : string.Join("; ", nonMonotone));

        Arm($"MATRIX-ONE-BUDGET: over the same {shapes.Count} shapes and every cap, the units never spend more than the row budget the allocation divided, and the response never owes more outside its units than was MEASURED before it rendered — the two drifting apart is what let a subject render less at a wider cap, and what let the roster spend room the family heads then needed",
            oneBudget.Count == 0,
            oneBudget.Count == 0 ? $"{capsSwept:N0} cells, the allocation and the response-wide test one budget throughout" : string.Join("; ", oneBudget));

        Arm("MATRIX-JSON-PARSES-AT-EVERY-CAP: every json render in the matrix is a well-formed document, including the ones the cap cut mid-family",
            unparseable.Count == 0, unparseable.Count == 0 ? "every json cell parsed" : string.Join("; ", unparseable));

        Arm($"MATRIX-HONESTY-LAYERS-AGREE: the two honesty layers a merged counts_only response carries — the errors family's `unread` and the scripts family's `scan_errors` — are ONE json shape ({{total, rows, rendered, truncated}}), and each one's flags equal its OWN rows at every cap in every shape's band. One kept the 1.x wrapper while the other was rebuilt as a bare array, so a consumer reading the same kind of fact one family over had to parse it two ways",
            honestyBad.Count == 0 && honestyCells > 0,
            honestyBad.Count > 0 ? string.Join("; ", honestyBad.Take(6))
                                 : honestyCells > 0 ? $"{honestyCells:N0} honesty-layer cells, both layers one shape throughout"
                                                    : "no shape in the matrix carried an honesty layer — the arm never saw the case it is for");

        Arm($"MATRIX-REMEDY-CLEARS-IN-ONE-STEP: wherever the merged response's overrun notice fires, the length it states is the response's OWN and the cap it names clears the notice in ONE step — the regression's remedy went 6000 -> 6123 -> 6201 -> 6279, short by a constant every time",
            remedyBad.Count == 0 && noticesFollowed > 0,
            remedyBad.Count > 0 ? string.Join("; ", remedyBad)
                                : noticesFollowed > 0 ? $"{noticesFollowed} notices followed, each cleared in one step"
                                                      : "the notice never fired anywhere in the matrix — the arm never saw the case it is for");

        // The three properties above are only worth their names over subjects the allocation actually reaches, so
        // this asks the membership question directly: every subject any shape PLANS gets room somewhere in its
        // band, and the five subjects a merged counts_only render carries are among them. Without this, an
        // unallocated subject leaves every arm above to be asked of it vacuously.
        var countsOnlySubjects = new[]
        {
            SweepSubject.HistogramByTarget, SweepSubject.HistogramBySource, SweepSubject.HistogramByProperty,
            SweepSubject.UnreadRows, SweepSubject.ScriptScanRows,
        };
        var ungoverned = new List<string>(neverRendered);
        foreach (var subject in countsOnlySubjects)
            if (!everRendered.Contains(subject))
                ungoverned.Add($"{subject} is charged by no shape in the matrix — the shape it belongs to has no fixture");
        Arm($"MATRIX-EVERY-PLANNED-SUBJECT-IS-GOVERNED (#394's own shape): every subject any of the {shapes.Count} shapes plans is allocated room and charged for what it wrote somewhere in its cap band, in both transports — INCLUDING the five a merged counts_only render carries (both dangling axes, the unbound-by-name axis, the unread rows, the scan-error rows), which the properties above would otherwise be asked of vacuously",
            ungoverned.Count == 0,
            ungoverned.Count == 0 ? $"{everRendered.Count} distinct subjects charged across the matrix, all five counts_only ones among them"
                                  : string.Join("; ", ungoverned.Take(6)));

        // EVERY TERM OF THE WHOLE-RENDER FINGERPRINT IS REACHED BY SOME SHAPE. The predicates above — nothing
        // stranded, the smallest cap that renders whole — compare fingerprints, so a term that is always 0 makes
        // them blind to that unit rather than making them fail: "  dangling ref " matched nothing any composer
        // emits, so the tight-fit search accepted caps that were still cutting dangling entries (measured at up to
        // 219 characters below the true one). The marker is corrected; this is the rule that keeps it correct.
        var deadTerms = new List<string>();
        foreach (var (lane, count) in termCount)
            for (int i = 0; i < count; i++)
                if (!termsReached.Contains((lane, i)))
                    deadTerms.Add($"{lane} fingerprint term {i} is 0 on every shape — its marker is a string no composer writes, so every whole-render predicate is blind to that unit");
        Arm($"MATRIX-EVERY-FINGERPRINT-TERM-IS-REACHED: every term of the whole-render fingerprint is non-zero on at least one of the {shapes.Count} shapes, in both transports — a term that is always 0 does not fail a comparison, it removes that unit from every comparison, which is how a dangling-entry cut went unseen",
            deadTerms.Count == 0,
            deadTerms.Count == 0 ? string.Join(", ", termCount.Select(kv => $"{kv.Key}: {kv.Value} terms, all reached"))
                                 : string.Join("; ", deadTerms.Take(4)));

        NoStranding(shapes, Arm);
        AllocationEqualsSpend(shapes, Arm);
    }

    /// <summary>Check both wrapped honesty layers of one merged json response, and return how many were there to
    /// check. A layer is <c>{total, rows, rendered, truncated}</c>: <c>rendered</c> is the rows the response
    /// carries, and <c>truncated</c> is exactly "rows is short of total". Read structurally rather than by the one
    /// family that happens to be in the shape, so a layer that changed TYPE fails here rather than reading as an
    /// absent family.</summary>
    static int HonestyLayers(System.Text.Json.JsonElement root, string shape, int cap, List<string> bad)
    {
        int seen = 0;
        foreach (var (family, member) in new[] { ("errors", "unread"), ("scripts", "scan_errors") })
        {
            if (!root.TryGetProperty("families", out var fams) || fams.ValueKind != System.Text.Json.JsonValueKind.Object) return seen;
            if (!fams.TryGetProperty(family, out var f) || f.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            if (!f.TryGetProperty(member, out var layer)) continue;
            seen++;
            if (layer.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                if (bad.Count < 6) bad.Add($"{shape} @{cap}: families.{family}.{member} is {layer.ValueKind}, not the wrapper object its sibling is");
                continue;
            }
            int? total = layer.TryGetProperty("total", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? a.GetInt32() : null;
            int? stated = layer.TryGetProperty("rendered", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number ? b.GetInt32() : null;
            bool? cut = layer.TryGetProperty("truncated", out var c)
                        && c.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False ? c.GetBoolean() : null;
            int? carried = layer.TryGetProperty("rows", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.Array ? d.GetArrayLength() : null;
            if (total is null || stated is null || cut is null || carried is null)
            {
                if (bad.Count < 6) bad.Add($"{shape} @{cap}: families.{family}.{member} is missing one of total/rows/rendered/truncated");
                continue;
            }
            if (stated != carried && bad.Count < 6)
                bad.Add($"{shape} @{cap}: families.{family}.{member} states {stated} rendered, carries {carried} rows");
            if (cut != (carried < total) && bad.Count < 6)
                bad.Add($"{shape} @{cap}: families.{family}.{member} says truncated={cut} with {carried} of {total} rows");
        }
        return seen;
    }

    /// <summary>Every integer cap from 1 to the whole response plus this — so the band starts below the floor and
    /// ends above the smallest cap that renders the shape WHOLE, which is where both the cut and the no-cut cases
    /// live. It is <see cref="TightSlack"/> plus a margin rather than a round number, because the two are the same
    /// question: a response's own length is not a cap it fits in, since the accounting and boundary each family
    /// holds room for are not part of what it returned. A band that stopped at the response's length left the json
    /// errors lane's plugin sections never rendering anywhere in it.</summary>
    const int BandMargin = TightSlack + 200;

    /// <summary>How often across the band the remedy is actually FOLLOWED. The notice fires at hundreds of
    /// consecutive caps and following it doubles the renders there, so it is followed at every Nth cap and its
    /// stated length is checked at the same ones. A stride, not a sample of convenience: the notice's content
    /// changes only with the cap's digit width and the response's length, both of which move across the band.</summary>
    const int RemedyStride = 7;

    /// <summary>How much the floor may grow between the cap it was measured at and the cap under test: max_chars is
    /// printed inside each family's accounting and three times inside the overrun notice, each bounded by the cap's
    /// own digit width. The same allowance <c>CAP-LADDER</c> uses, for the same reason.</summary>
    static int FloorSlack(int cap) => 8 * cap.ToString().Length;

    /// <summary>The two transports, so every property above is asked of both from one loop. A property pinned in
    /// one lane vouches for nothing about the other — the two measure their units in different ways.</summary>
    static readonly Lane[] Lanes =
    {
        new("text", (CheckSweep s, int cap, out BoundedBody? b) => Wire.RenderCheck(s, cap, 1000, out b), TextNotice),
        new("json", (CheckSweep s, int cap, out BoundedBody? b) => JsonWire.RenderCheck(s, cap, 1000, out b), JsonNotice),
    };

    delegate string RenderLane(CheckSweep s, int cap, out BoundedBody? body);

    sealed record Lane(string Name, RenderLane Render, Func<string, string?> Notice);

    /// <summary>The text lane carries the overrun notice as its own last sentence. Located by the opener of the
    /// lead both overrun sentences share — the words before the first substitution, so the marker is the shared
    /// constant's own text rather than a phrase copied out of it.</summary>
    static string? TextNotice(string response)
    {
        int at = response.IndexOf(NoticeOpener, StringComparison.Ordinal);
        return at < 0 ? null : response[at..];
    }

    /// <summary>The overrun lead up to its first substitution — <c>" This response is "</c>. Taken FROM the shared
    /// constant rather than spelled again here, so a reworded lead moves this marker with it instead of leaving
    /// the matrix quietly unable to find a notice that is being printed.</summary>
    static readonly string NoticeOpener =
        ReadSentences.SweepFixedPartLead[..ReadSentences.SweepFixedPartLead.IndexOf('{')];

    /// <summary>The json lane carries the SAME sentence as a <c>max_chars_overrun</c> member.</summary>
    static string? JsonNotice(string response)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("max_chars_overrun", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>The length the notice states this response is, read out of its own sentence. The notice is part of
    /// the response whose length it states, so the two must agree exactly — a settle loop that stopped one
    /// iteration early states a length the caller can measure and find wrong.</summary>
    static int? StatedLength(string notice) => Number(notice, NoticeOpener, " chars");

    /// <summary>The cap the notice tells the caller to raise to.</summary>
    static int? RaiseTo(string notice) => Number(notice, "raise it to at least ", ".");

    static int? Number(string s, string after, string before)
    {
        int at = s.IndexOf(after, StringComparison.Ordinal);
        if (at < 0) return null;
        int from = at + after.Length;
        int to = s.IndexOf(before, from, StringComparison.Ordinal);
        if (to < 0) return null;
        return int.TryParse(s[from..to].Replace(",", ""), out var n) ? n : null;
    }

    // ---- the two allocation properties, over every shape --------------------------------------------------

    /// <summary>Pin 3(ii) over the whole inventory: a call whose whole demand fits its budget renders every unit it
    /// has and claims no cut. Asked at the TIGHT fit as well as at a wide cap, because at a wide cap a row budget a
    /// few hundred characters too small still renders everything and the arm passes on a response that was quietly
    /// short-changed. The tight fit is found by bisection, which is sound only because
    /// <c>MATRIX-MONOTONE-IN-MAX-CHARS</c> holds independently — a use of that guarantee, not a second one.</summary>
    static void NoStranding(IReadOnlyList<Shape> shapes, Action<string, bool, string?> Arm)
    {
        var bad = new List<string>();
        foreach (var shape in shapes)
        {
            var subjects = Subjects(shape);
            foreach (var lane in Lanes)
            {
                string whole = lane.Render(shape.Sweep, 0, out _);
                string wide = lane.Render(shape.Sweep, whole.Length * 3 + 4000, out var body);
                if (Fingerprint(lane, wide) != Fingerprint(lane, whole) && bad.Count < 6)
                    bad.Add($"{shape.Name} [{lane.Name}]: renders {Fingerprint(lane, wide)} inside a cap three times its own length, {Fingerprint(lane, whole)} with no cap at all");
                if (lane.Notice(wide) is not null && bad.Count < 6)
                    bad.Add($"{shape.Name} [{lane.Name}]: claims an overrun inside a cap three times its own length");
                if (body is not null)
                    foreach (var subject in subjects)
                        if (body.SpentOn(subject) != body.AllocationOf(subject) && bad.Count < 6)
                            bad.Add($"{shape.Name} [{lane.Name}] {subject}: allocated {body.AllocationOf(subject)}, spent {body.SpentOn(subject)} — room left standing");

                // THE TIGHT FIT: the smallest cap rendering every unit must sit within a bounded margin of the
                // response that cap returns. Every character the row budget gives away moves that threshold up.
                int tight = SmallestWholeCap(c => Fingerprint(lane, lane.Render(shape.Sweep, c, out _)),
                                             Fingerprint(lane, whole), whole.Length);
                if (tight < 0) bad.Add($"{shape.Name} [{lane.Name}]: no cap up to three times the response renders it whole");
                else if (tight - whole.Length > TightSlack && bad.Count < 6)
                    bad.Add($"{shape.Name} [{lane.Name}]: needs {tight} to render whole, {tight - whole.Length} above the {whole.Length} it returns (allowed {TightSlack})");
            }
        }
        Arm($"MATRIX-NO-STRANDING: over {shapes.Count} shapes in both transports, a merged call whose whole demand fits renders every unit it has, claims no cut, leaves no subject's allocation unspent — and the SMALLEST cap that renders it whole sits within a bounded margin of the response that cap returns",
            bad.Count == 0, bad.Count == 0 ? $"{shapes.Count} shapes, both transports, nothing stranded" : string.Join("; ", bad));
    }

    /// <summary>What the two lanes may legitimately need above their own length before nothing is cut: the reserve
    /// each holds for an accounting and a boundary it has not written yet, per family. Fixture-known and generous —
    /// the point is that it is BOUNDED, so a fixed part counted twice lands outside it.</summary>
    const int TightSlack = 2200;

    static int SmallestWholeCap(Func<int, string> unitsAt, string whole, int from)
    {
        int lo = Math.Max(1, from), hi = Math.Max(lo + 1, from * 3 + 4000);
        if (unitsAt(hi) != whole) return -1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (unitsAt(mid) == whole) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>Pin 3(iv) over the whole inventory: with nothing cut, a subject's allocation IS its measured demand,
    /// so allocation and spend are the same number TO THE BYTE or the measurement is not measuring the write. This
    /// is the arm that refuses an upper bound: a cost that over-counts allocates room the subject will not spend,
    /// and one that under-counts is a response over its own cap.</summary>
    static void AllocationEqualsSpend(IReadOnlyList<Shape> shapes, Action<string, bool, string?> Arm)
    {
        var bad = new List<string>();
        int checkedSubjects = 0;
        foreach (var shape in shapes)
        {
            var subjects = Subjects(shape);
            foreach (var lane in Lanes)
            {
                lane.Render(shape.Sweep, 4000000, out var body);
                if (body is null) { bad.Add($"{shape.Name} [{lane.Name}]: the render built no allocation"); continue; }
                foreach (var subject in subjects)
                {
                    int allocated = body.AllocationOf(subject), spent = body.SpentOn(subject);
                    checkedSubjects++;
                    if (spent == 0 && bad.Count < 6)
                        bad.Add($"{shape.Name} [{lane.Name}] {subject}: spent nothing at a cap nothing could cut — the arm would pass on a subject that never rendered");
                    else if (allocated != spent && bad.Count < 6)
                        bad.Add($"{shape.Name} [{lane.Name}] {subject}: allocated {allocated}, spent {spent} (off by {allocated - spent})");
                }
            }
        }
        Arm($"MATRIX-ALLOCATION-EQUALS-SPEND: on every shape rendered with nothing cut, every governed subject spends EXACTLY what it was allocated — in both transports, which measure their units in different ways and must both be exact",
            bad.Count == 0 && checkedSubjects > 0,
            bad.Count > 0 ? string.Join("; ", bad) : $"{checkedSubjects} subject/shape/transport cells, every one exact");
    }

    /// <summary>EVERY subject a shape can render — its families' planned subjects AND the response's own, which
    /// belong to no family. The roster is the second kind, and leaving it out lets a break that stops measuring its
    /// demand pass green through the whole sweep: it is allocated, charged and cut like any other subject, so every
    /// property here has to be asked of it.</summary>
    static SweepSubject[] Subjects(Shape shape)
        => Subjects(CheckOutcome.For(shape.Sweep));

    static SweepSubject[] Subjects(CheckOutcome o)
        => o.Plan().SelectMany(p => p.Subjects).Concat(o.ResponseSubjects).Distinct().ToArray();

    /// <summary>A response's rendered-unit fingerprint — what "renders everything" means, COUNTED rather than read
    /// off a sentence the response prints about itself. Per transport, because the two carry their units
    /// differently: text as marked lines, json as array elements.</summary>
    static string Fingerprint(Lane lane, string response)
        => lane.Name == "text" ? TextFingerprint(response) : JsonFingerprint(response);

    /// <summary>The text lane's units, each located by the marker its own composer writes: plugin sections, script
    /// record sections, scan-error rows, unread rows, dangling entries, seed heads, topic blocks, unreachable-seed
    /// rows, roster rows, and every counts_only histogram row.
    ///
    /// <para><b>Every marker must be a string a composer really emits.</b> A marker no composer writes makes its
    /// term 0 in every response, and the fingerprint then goes blind to that subject instead of failing — which is
    /// what <c>"  dangling ref "</c> did, since <see cref="ReadTools.ComposeDanglingLine"/> ends its line in the
    /// bracketed reason below. Take each marker from the composer's own text.</para></summary>
    static string TextFingerprint(string t)
        => string.Join("/", new[]
           {
               Count(t, "\n[ERROR] "), Count(t, "\n[UNBOUND] "), Count(t, "\n[CHECK] "),
               Count(t, "\n[SCAN ERROR] "), Count(t, "\n[UNREAD] "), Count(t, "   [target not defined by any active plugin]"),
               Count(t, "\nseed "), Count(t, "  topic "), Count(t, "NOT validated:"),
               Count(t, "  HcMxBroken"), HistogramRows(t),
           });

    /// <summary>How many counts_only histogram rows a text response carries, counted off the row's own shape — two
    /// spaces, a count right-aligned in six, two spaces, the key. Counted rather than searched for by key, so an
    /// axis whose keys the fixture changes is still counted.</summary>
    static int HistogramRows(string t)
    {
        int n = 0;
        foreach (var line in t.Split('\n'))
            if (line.Length > 10 && line.StartsWith("  ", StringComparison.Ordinal)
                && line[8..10] == "  " && int.TryParse(line[2..8].Trim(), out _)) n++;
        return n;
    }

    /// <summary>The json lane's units, off the arrays and the member names the writer emits — the same units,
    /// counted the way this transport carries them.</summary>
    static string JsonFingerprint(string response)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(response);
            var root = doc.RootElement;
            // The excluded roster is a RESPONSE-level array, not a member of any family object, so it is counted
            // once below by RootArrayLength. A fourth pair reading families.errors.excluded_plugins returned -1 on
            // every shape — a constant, which contributes nothing to a comparison and reads as a covered subject
            // (found by MATRIX-EVERY-FINGERPRINT-TERM-IS-REACHED, the same class as the dangling marker).
            return string.Join("/", new[] { ("errors", "plugins"), ("scripts", "records"), ("dialogue", "seeds") }
                                    .Select(fa => ArrayLength(root, fa.Item1, fa.Item2))
                                    .Append(RootArrayLength(root, "excluded_plugins"))
                                    .Append(Count(response, "\"count\":"))
                                    .Append(Count(response, "\"target\":"))
                                    .Append(Count(response, "\"topic\":"))
                                    .Append(Count(response, "\"scan_error\":")));
        }
        catch { return "unparseable"; }
    }

    static int ArrayLength(System.Text.Json.JsonElement root, string family, string array)
        => root.TryGetProperty("families", out var fams) && fams.TryGetProperty(family, out var f)
           && f.TryGetProperty(array, out var rows) ? rows.GetArrayLength() : -1;

    static int RootArrayLength(System.Text.Json.JsonElement root, string array)
        => root.TryGetProperty(array, out var rows) && rows.ValueKind == System.Text.Json.JsonValueKind.Array
            ? rows.GetArrayLength() : -1;

    static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

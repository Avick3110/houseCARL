using Mutagen.Bethesda.Skyrim; // IArmorGetter (assembly anchor)

namespace HousecarlGenerator;

/// <summary>
/// arm-classification-guard — pins <see cref="CorpusGenerator.ClassifyArm"/>, the #397 split.
///
/// Before #397, a concrete union implementer was accepted as an arm by
/// <c>MutableInterfaceFor(GetterInterfaceFor(t) ?? t) != null</c>. That expression answers "excluded" for two
/// structurally different reasons — "is a read-only projection" and "has no getter interface at all" — and only
/// the first is a correct reason to exclude. A type in the second class carries a writable surface, is dropped
/// from its polymorphic base anyway, and (on the arm path) was dropped with no output of any kind. This guard
/// exists because that fix is a CLASSIFIER change whose observable effect on Skyrim is nil: the shards are
/// byte-identical across it, so a byte-diff can never tell "the split works" from "the split is dead code".
///
/// THERE IS NO BEHAVIOUR-NEUTRALITY ARM, AND THE LETTER A IS RETIRED. There was one: a sweep asserting
/// <c>ClassifyArm(t) == Authorable</c> against the pre-#397 predicate over every class the walk visited. It
/// could not fail. The predicate it compared against was the same expression <c>ClassifyArm</c> evaluates on
/// its first line, so both sides moved together on the same two helpers and the disagreement set was empty by
/// construction — including through a change that really would move the accepted-arm set, such as fixing #424.
/// It was deleted rather than re-spelled: an independently-written twin of <c>MutableInterfaceFor</c> and
/// <c>GetterInterfaceFor</c> is a hand-maintained copy of two non-trivial reflection routines, which is a
/// drift generator rather than a pin. Where its two claims live now:
///     - the TRANSITION claim (the #397 split changed no classification) was proven once, on this branch, by
///       round 1's fold and its RED checks. It is historical; nothing standing has to re-prove it.
///     - the STANDING claim (no classification change ships silently) is held BY CONSTRUCTION by
///       emit-match-guard and the #351 CI emit-match step, not by anything in this file: a classification
///       change alters emission, emission is diffed against the COMMITTED shards on every CI run, and a
///       difference fails the build — including when nobody regenerates. That, not a sweep here, is what
///       makes the shard-level byte-identity a checked consequence.
/// The letters below start at B and stay where they are, so this file still reads against PR #425 and the
/// review that found the tautology.
///
///   B — ALL THREE CLASSES ARE REACHABLE. Each of Authorable / ReadOnlyProjection / WritableButUnextractable is
///       exhibited by a REAL type in the live assembly and asserted by name. The third is the arm this guard is
///       really for: without it, <c>WritableButUnextractable</c> is a branch no live type enters, and a later
///       refactor could delete or invert it with every guard still green.
///   C — THE LINE CLAIMS ITS MEASUREMENT AND NOTHING MORE. #397 asked the output to stop conflating a real
///       coverage gap with a correct exclusion; it did not license a third diagnosis. So C asserts the honest
///       content (the marker, the FULL type name, a non-zero authorable count carrying the predicate that
///       produced it, and the one shared check that settles it, present in BOTH arms) AND the
///       ABSENCE of the two sentences that were removed for asserting what the predicate cannot establish —
///       "a real coverage gap, not a read-only projection", and "the fix belongs upstream in Mutagen". The
///       second was provably false for this guard's own exhibit. Assert the absence or nothing stops it coming
///       back, and a guard checking only which enum value returned would never notice.
///   D — THE LINE IS ACTUALLY EMITTED. A/B/C pin the wording of a sentence; none of them requires anyone to
///       PRINT it. Measured: no-opping both call sites left every other arm green, left emit-match-guard and
///       corpus-hygiene-guard green, and made the whole coverage-anomaly section vanish. So D drives a real emit
///       and reads what the generator reported — from the COVERAGE ANOMALIES channel, which is printed in full,
///       never sharing the unbounded warning stream's print cap. It pins the RECORD path only — the arm path has
///       no live exhibit and is stated as unpinned at the check itself, rather than left for a reader to assume.
///
/// The types in B/C are named deliberately, and a rename or a Mutagen bump that changes their shape SHOULD break
/// this guard — that is the tripwire working. #397's whole point is that a bump is what arms this class; a guard
/// that survived the shape changing would be pinning nothing.
/// </summary>
public static class ArmClassificationProbe
{
    // Real types from the live assembly, each chosen as the exhibit of one class. Verified by direct reflection
    // before being written down here; the counts are asserted below rather than trusted.
    // FULL names, and Find() matches on FullName: ~350 distinct nested types in this assembly share one simple
    // name, so a simple-name FirstOrDefault picks an arbitrary one of a colliding set and silently exhibits the
    // wrong type. Each of these four is unique by simple name TODAY, which is exactly why the resolution has to
    // be pinned now rather than after a bump introduces the collision.
    const string AuthorableExhibit = "Mutagen.Bethesda.Skyrim.Armor";                   // getter interface + mutable twin
    const string ProjectionExhibit = "Mutagen.Bethesda.Skyrim.SkyrimMultiModOverlay";   // none resolved by name, 0 authorable
    const string ProjectionExhibit2 = "Mutagen.Bethesda.Skyrim.MergedCellBlock";        // none resolved by name, 0 authorable
    // Resolves no I{Name}Getter BY NAME while implementing IGenderedItemGetter&lt;bool&gt;, and its data IS
    // catalogued, as GenderedItem&lt;Boolean&gt;. That is precisely why this line reports rather than diagnoses,
    // and why the name-resolution defect it exposes is filed as #424 instead of worked around here.
    const string UnextractableExhibit = "Mutagen.Bethesda.Skyrim.ArmorAddonWeightSliderContainer"; // none by name, 2/2 authorable

    public static int RunGuard(string[] args)
    {
        var asm = typeof(IArmorGetter).Assembly;
        int failures = 0;

        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("arm-classification-guard — the #397 unextractable-by-name split");
        Console.WriteLine($"  assembly: {asm.GetName().Name} {asm.GetName().Version}");
        Console.WriteLine();

        // Drive a real emit FIRST — section D reads what the generator actually reported, and that cannot be
        // faked by anything the checks below do.
        var reported = EmitAndCaptureCoverageAnomalies(out var anomalyCount);

        // ---------------------------------------------------------------- B: all three classes reachable
        Type Find(string fullName) =>
            asm.GetTypes().FirstOrDefault(t => t.FullName == fullName)
            ?? throw new InvalidOperationException(
                $"{fullName} not found in {asm.GetName().Name} — this guard's exhibit no longer exists. That is a real " +
                $"signal about the classifier's inputs, not a guard to relax: pick a new exhibit of the same shape.");

        var authorable = Find(AuthorableExhibit);
        var projection = Find(ProjectionExhibit);
        var projection2 = Find(ProjectionExhibit2);
        var unextractable = Find(UnextractableExhibit);

        Check($"B1. {AuthorableExhibit} classifies Authorable",
            CorpusGenerator.ClassifyArm(authorable) == CorpusGenerator.ArmClass.Authorable,
            $"got {CorpusGenerator.ClassifyArm(authorable)}");

        Check($"B2. {ProjectionExhibit} classifies ReadOnlyProjection",
            CorpusGenerator.ClassifyArm(projection) == CorpusGenerator.ArmClass.ReadOnlyProjection,
            $"got {CorpusGenerator.ClassifyArm(projection)}");

        Check($"B3. {ProjectionExhibit2} classifies ReadOnlyProjection",
            CorpusGenerator.ClassifyArm(projection2) == CorpusGenerator.ArmClass.ReadOnlyProjection,
            $"got {CorpusGenerator.ClassifyArm(projection2)}");

        // The one that matters. Nothing in the live Skyrim assembly reaches this branch through a real union walk (the issue's
        // "latent, not live"), so without driving the classifier directly the branch is untested code.
        Check($"B4. {UnextractableExhibit} classifies WritableButUnextractable",
            CorpusGenerator.ClassifyArm(unextractable) == CorpusGenerator.ArmClass.WritableButUnextractable,
            $"got {CorpusGenerator.ClassifyArm(unextractable)} — the writable-but-unextractable branch is not " +
            $"being entered by a type that exhibits its shape (no getter interface + a writable surface)");

        // Both non-authorable classes must actually be REACHED by distinct types — a classifier that answered
        // ReadOnlyProjection for everything would pass B2/B3 alone.
        Check("B5. the two exclusion classes are distinct verdicts, not one answer",
            CorpusGenerator.ClassifyArm(projection) != CorpusGenerator.ClassifyArm(unextractable));

        // ---------------------------------------------------------------- C: the lines cannot be confused
        var gapLine = CorpusGenerator.UnextractableWarning("union arm", unextractable);
        var okLine = CorpusGenerator.UnextractableWarning("union arm", projection);

        Check("C1. the unextractable line is marked UNEXTRACTABLE BY NAME",
            gapLine.Contains("UNEXTRACTABLE BY NAME", StringComparison.Ordinal), gapLine);

        Check("C2. it names the type by its FULL name",
            gapLine.Contains(UnextractableExhibit, StringComparison.Ordinal), gapLine);

        // "2 of 2" — asserted, not assumed. If a bump changes this type's authorable surface the guard says so.
        // The label is pinned WITH its predicate: the count is neither CanWrite nor plain public-settable, and a
        // line that prints the number without naming which measure produced it is the over-claim this arm blocks.
        Check("C3. it carries a non-zero authorable count, labelled with the predicate that produced it",
            System.Text.RegularExpressions.Regex.IsMatch(
                gapLine, @"and [1-9][0-9]* of [0-9]+ properties are authorable \(public settable, or a mutable collection\)"),
            gapLine);

        // THE CONTENT PINS. #397 asked the output to stop conflating two cases; it did not license the output to
        // diagnose a third thing the predicate cannot measure. Both sentences below were in this line and were
        // removed for asserting what "no I{Name}Getter resolved by name" does not establish — and the upstream
        // one is provably false for this very exhibit, whose data ships today as GenderedItem<Boolean>, 2/2
        // writable. Assert their ABSENCE: nothing else stops a later edit reintroducing the diagnosis, and a
        // guard that checked only which enum value came back would not notice.
        Check("C4. it does NOT diagnose a real coverage gap",
            !gapLine.Contains("coverage gap, not a read-only projection", StringComparison.Ordinal)
            && !gapLine.Contains("is a real coverage gap", StringComparison.Ordinal), gapLine);

        Check("C5. it does NOT assign the fix upstream to Mutagen",
            !gapLine.Contains("belongs upstream", StringComparison.Ordinal), gapLine);

        Check("C6. it names the check that settles it",
            gapLine.Contains(CorpusGenerator.ReachabilityCheck, StringComparison.Ordinal), gapLine);

        Check("C7. the zero-authorable line is NOT marked unextractable-by-name",
            !okLine.Contains("UNEXTRACTABLE BY NAME", StringComparison.Ordinal), okLine);

        // The arms cannot diverge again. The zero-count arm used to close by inferring that nothing is lost by
        // excluding the type — reachability, which its measurement does not establish any more than the non-zero
        // arm's does. Both arms now close on ONE shared const, and this pins that shared text present in BOTH:
        // a content-level assertion, so re-splitting them into different closing advice is a RED, not a silent
        // regression that only a reader of the output would ever notice.
        Check("C8. both arms close on the SAME shared reachability check, neither inferring more than it measured",
            okLine.Contains(CorpusGenerator.ReachabilityCheck, StringComparison.Ordinal)
            && gapLine.Contains(CorpusGenerator.ReachabilityCheck, StringComparison.Ordinal),
            $"zero-authorable arm: {okLine}  ||  unextractable arm: {gapLine}");

        // ---------------------------------------------------------------- D: the line is actually EMITTED
        // Everything above pins the WORDING of a sentence. None of it requires anyone to print that sentence.
        // Measured: replacing both UnextractableWarning call sites with no-ops left every arm above green, left
        // emit-match-guard and corpus-hygiene-guard green, and made the generator's whole coverage-anomaly
        // section disappear along with the two live MergedWorldspace lines. A wording pin on an unprinted line
        // is theatre, so drive a real emit and read what it actually reported.
        //
        // This asserts the CONTRACT (every record class the seed loop emits an anomaly for is named in the
        // report), not a fixed list of type names — so it survives a Mutagen bump that changes which types
        // exhibit the shape, while still failing if the emission is removed. The emit itself ran at the top.
        //
        // WHAT D DOES NOT COVER, stated because measuring it is the only way anyone learns it: D pins the
        // RECORD-path emission (CorpusGenerator's seed loop) and NOT the union-arm-path emission
        // (FindUnionArms). Sabotage confirms the asymmetry — no-opping the record call site turns D0 red, while
        // no-opping the arm call site leaves this guard fully green. The arm branch cannot be driven by any live
        // type: FindUnionArms only enumerates the getter interface's OWN assembly, and no unextractable
        // candidate is assignable to a closed getter interface declared alongside it. So the arm-path emission
        // is UNPINNED today, and will stay unpinned until a real type exhibits the shape — which is the same
        // bump that arms #397 in the first place. Do not read D as covering both paths.
        // The expected set is derived from the SAME enumeration the record-path emission walks — BuildCorpus's
        // seed loop over the Skyrim assembly, with that loop's own filters and no others. It was previously
        // derived from a second, similar-looking sweep that spanned all three assemblies the walk visits and
        // carried an extra ClassifyArm filter the seed loop does not apply. Both directions of that mismatch
        // were real: a Core or CSharpExt type would be EXPECTED in a report the record path never writes it
        // to, and a Skyrim record class that resolves no getter interface yet still classifies Authorable IS
        // emitted while being filtered out of the expectation. Deriving from the one enumeration is what makes
        // D1 a claim about the emission rather than about a walk that merely resembles it.
        var expected = asm.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !CorpusGenerator.IsOverlayTwin(t))
            .Where(t => typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter).IsAssignableFrom(t))
            .Where(t => CorpusGenerator.GetterInterfaceFor(t) == null)
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Check($"D0. the emit produced a coverage-anomaly report ({anomalyCount} line(s))",
            reported != null,
            "no COVERAGE ANOMALIES section was printed at all — the report is not reaching the caller");

        // The vacuity floor. D1 iterates a DERIVED list, so if that list is ever empty
        // it passes while asserting nothing — and D0 alone does not save it, because D0 only requires that SOME
        // anomaly section exists. Today the two lines D1 checks are the only anomalies, which makes D0 look
        // load-bearing; add one unrelated warning and it stops being. An empty expectation is a signal to
        // re-examine this guard, not a pass.
        Check($"D0b. the walk produced at least one type for D1 to expect ({expected.Count})",
            expected.Count > 0,
            "no no-getter-interface record class was found, so D1 asserts nothing — either the classifier " +
            "changed or the walk did; both are worth looking at before trusting this arm again");

        // Read against the UNTRUNCATED channel. This assertion previously read a section that shared one print
        // cap with the unbounded warning stream, so a name could go missing for two structurally different
        // reasons while the failure text named only one of them. The channel it reads is now printed in full,
        // which is what lets the text below state its cause: the other cause was removed from the code, rather
        // than judged the less likely of the two.
        var missing = expected.Where(n => reported?.Contains(n, StringComparison.Ordinal) != true).ToList();
        Check($"D1. every no-getter-interface RECORD class the seed loop enumerates is NAMED in the " +
              $"untruncated coverage-anomaly report " +
              $"({expected.Count} expected; the arm-path emission is not pinned \u2014 see the note above)",
            missing.Count == 0,
            missing.Count == 0 ? null
                : $"emitted report does not name: {string.Join(", ", missing.Take(10))} — the anomaly is " +
                  $"computed but never printed, which is the failure mode this arm exists for");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "arm-classification-guard: PASS (all arms green)"
            : $"arm-classification-guard: FAIL ({failures} arm(s) red)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Run a real emit into a throwaway directory and return the text of the generator's COVERAGE ANOMALIES
    /// section, or null if it printed none. The corpus itself is process-memoized, so this costs an emit rather
    /// than a second reflection walk; the output goes to a temp dir so the working tree is untouched.
    ///
    /// It captures that section and NOT the warning stream that follows it. The two are separate channels
    /// precisely because the warning stream is unbounded and printed under a cap, so a name read from it could
    /// be absent for a reason that has nothing to do with what D1 asserts. The section is cut at the warning
    /// heading rather than run to end-of-output for that same reason.
    /// </summary>
    static string? EmitAndCaptureCoverageAnomalies(out int lineCount)
    {
        lineCount = 0;
        var tmp = Path.Combine(Path.GetTempPath(), "housecarl-arm-emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var captured = new StringWriter();
        var realOut = Console.Out;
        try
        {
            Console.SetOut(captured);
            CorpusGenerator.GenerateAll(Path.Combine(tmp, "generated"), Path.Combine(tmp, "refs"));
        }
        // The same policy as EmitMatchProbe.RunGuard, deliberately spelled the same way so the two read as one
        // policy: when the emit throws, the capture IS the diagnosis, and a bare try/finally discards it with
        // everything the generator printed up to the failure. This guard sits BEFORE emit-match-guard in
        // CiAll.Probes, so on a bump that makes BuildCorpus throw it is the first probe to hit it — and CiAll
        // reports the exception alone, which would leave the banner, per-kind counts, spotlights and any
        // anomalies already accumulated unrecoverable.
        catch
        {
            Console.SetOut(realOut);
            Console.Error.WriteLine("  the generator threw during this guard's emit — its output up to the failure follows:");
            Console.Error.WriteLine(captured.ToString());
            throw;
        }
        finally
        {
            Console.SetOut(realOut);
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }

        var text = captured.ToString();
        var start = text.IndexOf("COVERAGE ANOMALIES", StringComparison.Ordinal);
        if (start < 0) return null;
        var section = text[start..];
        var end = section.IndexOf("ANOMALIES / things to inspect", StringComparison.Ordinal);
        if (end >= 0) section = section[..end];
        lineCount = section.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        return section;
    }
}

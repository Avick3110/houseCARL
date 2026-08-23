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
///   A — BEHAVIOR NEUTRALITY, over the assemblies the walk ACTUALLY VISITED, derived from the walk rather than
///       listed here. For every concrete non-overlay class in that set, <c>ClassifyArm(t) == Authorable</c> must
///       equal the ORIGINAL predicate, type for type — which is what makes the shard-level byte-identity a
///       consequence rather than a coincidence. A hand-written assembly list was wrong twice running: first
///       naming Skyrim alone (missing two Core types a first cut of ClassifyArm flipped), then Skyrim and Core
///       while the walk also reaches Noggog.CSharpExt via IReadOnlyArray2d&lt;T&gt;. Nobody enumerates them by hand
///       again. A0 fails if the walk recorded nothing, so A cannot pass vacuously over an empty universe.
///       NOTE what A does NOT cover: it compares the ACCEPT/REJECT split only, so it is deliberately blind to
///       WHICH reject reason is reported. The #397 split itself is pinned by B4, never by A.
///   B — ALL THREE CLASSES ARE REACHABLE. Each of Authorable / ReadOnlyProjection / WritableButUnextractable is
///       exhibited by a REAL type in the live assembly and asserted by name. The third is the arm this guard is
///       really for: without it, <c>WritableButUnextractable</c> is a branch no live type enters, and a later
///       refactor could delete or invert it with every guard still green.
///   C — THE LINE CLAIMS ITS MEASUREMENT AND NOTHING MORE. #397 asked the output to stop conflating a real
///       coverage gap with a correct exclusion; it did not license a third diagnosis. So C asserts the honest
///       content (the marker, the FULL type name, a non-zero settable count, the check that settles it) AND the
///       ABSENCE of the two sentences that were removed for asserting what the predicate cannot establish —
///       "a real coverage gap, not a read-only projection", and "the fix belongs upstream in Mutagen". The
///       second was provably false for this guard's own exhibit. Assert the absence or nothing stops it coming
///       back, and a guard checking only which enum value returned would never notice.
///   D — THE LINE IS ACTUALLY EMITTED. A/B/C pin the wording of a sentence; none of them requires anyone to
///       PRINT it. Measured: no-opping both call sites left every other arm green, left emit-match-guard and
///       corpus-hygiene-guard green, and made the whole ANOMALIES section vanish. So D drives a real emit and
///       reads what the generator reported. It pins the RECORD path only — the arm path has no live exhibit and
///       is stated as unpinned at the check itself, rather than left for a reader to assume.
///
/// The types in B/C are named deliberately, and a rename or a Mutagen bump that changes their shape SHOULD break
/// this guard — that is the tripwire working. #397's whole point is that a bump is what arms this class; a guard
/// that survived the shape changing would be pinning nothing.
/// </summary>
public static class ArmClassificationProbe
{
    // Real types from the live assembly, each chosen as the exhibit of one class. Verified by direct reflection
    // before being written down here; the counts are asserted below rather than trusted.
    const string AuthorableExhibit = "Armor";                              // getter interface + mutable twin
    const string ProjectionExhibit = "SkyrimMultiModOverlay";              // none resolved by name, 0 settable
    const string ProjectionExhibit2 = "MergedCellBlock";                   // none resolved by name, 0 settable
    // Resolves no I{Name}Getter BY NAME while implementing IGenderedItemGetter&lt;bool&gt;, and its data IS
    // catalogued, as GenderedItem&lt;Boolean&gt;. That is precisely why this line reports rather than diagnoses,
    // and why the name-resolution defect it exposes is filed as #424 instead of worked around here.
    const string UnextractableExhibit = "ArmorAddonWeightSliderContainer"; // none resolved by name, 2/2 settable

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

        // Drive a real emit FIRST. Two things depend on it and neither can be faked: the walk has to have run
        // before AssembliesWalked describes anything (check A's universe is derived from it), and section D
        // reads what the generator actually reported. Doing it here rather than at D means A cannot pass
        // vacuously over an empty universe — which it did, once, until A0 caught it.
        var reported = EmitAndCaptureAnomalies(out var anomalyCount);

        // ---------------------------------------------------------------- A: behavior neutrality
        // The predicate as it stood before #397, reproduced literally.
        static bool OriginalPredicate(Type t) =>
            CorpusGenerator.MutableInterfaceFor(CorpusGenerator.GetterInterfaceFor(t) ?? t) != null;

        // The universe is DERIVED from the walk, not declared here. FindUnionArms records every assembly it
        // enumerates; this asserts over exactly that set. A hand-written list was wrong twice running — first
        // Skyrim only, then Skyrim + Core, while the walk also reaches Noggog.CSharpExt via IReadOnlyArray2d<T>.
        // The claim narrows deliberately, from "every type" to "every type the walk actually visited", which is
        // the claim that is true; the false half was never the neutrality, it was "and the walk is these two
        // assemblies". Not circular: the walk fixes the universe, the ORIGINAL PREDICATE fixes the expected
        // answer, and the two are independent — a walk that visited nothing would fail the arm below rather
        // than vacuously pass it.
        var walked = CorpusGenerator.AssembliesWalked.ToList();
        Check("A0. the walk recorded at least one assembly (else A asserts nothing)", walked.Count > 0);

        var candidates = walked
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && !CorpusGenerator.IsOverlayTwin(t))
            .ToList();

        var disagreements = candidates
            .Where(t => OriginalPredicate(t) != (CorpusGenerator.ClassifyArm(t) == CorpusGenerator.ArmClass.Authorable))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Check($"A. accepted-arm set unchanged across {candidates.Count} concrete non-overlay classes in the " +
              $"{walked.Count} assembly/assemblies the walk visited " +
              $"({string.Join(", ", walked.Select(a => a.GetName().Name).OrderBy(n => n, StringComparer.Ordinal))})",
            disagreements.Count == 0,
            disagreements.Count == 0 ? null
                : $"{disagreements.Count} type(s) classify differently than the original predicate: " +
                  string.Join(", ", disagreements.Take(10)));

        // ---------------------------------------------------------------- B: all three classes reachable
        Type Find(string name) =>
            asm.GetTypes().FirstOrDefault(t => t.Name == name)
            ?? throw new InvalidOperationException(
                $"{name} not found in {asm.GetName().Name} — this guard's exhibit no longer exists. That is a real " +
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
            gapLine.Contains("Mutagen.Bethesda.Skyrim." + UnextractableExhibit, StringComparison.Ordinal), gapLine);

        // "2 of 2" — asserted, not assumed. If a bump changes this type's settable surface the guard says so.
        Check("C3. it carries a non-zero settable count",
            System.Text.RegularExpressions.Regex.IsMatch(gapLine, @"and [1-9][0-9]* of [0-9]+ public settable"), gapLine);

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
            gapLine.Contains("reachable elsewhere in the catalogue", StringComparison.Ordinal), gapLine);

        Check("C7. the nothing-lost line is NOT marked unextractable-by-name",
            !okLine.Contains("UNEXTRACTABLE BY NAME", StringComparison.Ordinal), okLine);

        Check("C8. the nothing-lost line says nothing is lost",
            okLine.Contains("nothing is lost by excluding it", StringComparison.Ordinal), okLine);

        // ---------------------------------------------------------------- D: the line is actually EMITTED
        // Everything above pins the WORDING of a sentence. None of it requires anyone to print that sentence.
        // Measured: replacing both UnextractableWarning call sites with no-ops left every arm above green, left
        // emit-match-guard and corpus-hygiene-guard green, and made the generator's whole ANOMALIES section
        // disappear along with the two live MergedWorldspace lines. A wording pin on an unprinted line is
        // theatre, so drive a real emit and read what it actually reported.
        //
        // This asserts the CONTRACT (every non-authorable no-getter-interface type the walk reached is named in
        // the report), not a fixed list of type names — so it survives a Mutagen bump that changes which types
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
        var expected = candidates
            .Where(t => CorpusGenerator.ClassifyArm(t) != CorpusGenerator.ArmClass.Authorable
                        && CorpusGenerator.GetterInterfaceFor(t) == null
                        && typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter).IsAssignableFrom(t))
            .Select(t => t.FullName ?? t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Check($"D0. the emit produced an anomaly report ({anomalyCount} line(s))",
            reported != null,
            "no ANOMALIES section was printed at all — the report is not reaching the caller");

        // The vacuity floor, the same one A0 gives A. D1 iterates a DERIVED list, so if that list is ever empty
        // it passes while asserting nothing — and D0 alone does not save it, because D0 only requires that SOME
        // anomaly section exists. Today the two lines D1 checks are the only anomalies, which makes D0 look
        // load-bearing; add one unrelated warning and it stops being. An empty expectation is a signal to
        // re-examine this guard, not a pass.
        Check($"D0b. the walk produced at least one type for D1 to expect ({expected.Count})",
            expected.Count > 0,
            "no no-getter-interface record class was found, so D1 asserts nothing — either the classifier " +
            "changed or the walk did; both are worth looking at before trusting this arm again");

        var missing = expected.Where(n => reported?.Contains(n, StringComparison.Ordinal) != true).ToList();
        Check($"D1. every no-getter-interface RECORD class the walk reached is NAMED in the report " +
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
    /// Run a real emit into a throwaway directory and return the text of the generator's ANOMALIES section,
    /// or null if it printed none. The corpus itself is process-memoized, so this costs an emit rather than a
    /// second reflection walk; the output goes to a temp dir so the working tree is untouched.
    /// </summary>
    static string? EmitAndCaptureAnomalies(out int lineCount)
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
        finally
        {
            Console.SetOut(realOut);
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }

        var text = captured.ToString();
        var start = text.IndexOf("ANOMALIES", StringComparison.Ordinal);
        if (start < 0) return null;
        var section = text[start..];
        lineCount = section.Split('\n').Count(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal));
        return section;
    }
}

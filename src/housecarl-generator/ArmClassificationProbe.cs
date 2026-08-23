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
///   A — BEHAVIOR NEUTRALITY, over BOTH assemblies the corpus walks (Mutagen.Bethesda.Skyrim AND
///       Mutagen.Bethesda.Core — BuildCorpus seeds IPexFileGetter from Core, and FindUnionArms walks whichever
///       assembly a getter interface lives in). For every concrete non-overlay class in either,
///       <c>ClassifyArm(t) == Authorable</c> must equal the ORIGINAL predicate, type for type. This is what makes
///       the shard-level byte-identity a consequence rather than a coincidence: the accepted arm set is provably
///       unchanged, for every type, not just for the ones the corpus happens to walk.
///       A Skyrim-only sweep was NOT enough: it asserted this over part of its own domain and missed two Core
///       types (FormLinkNullableGetter`1, FormLinkOrIndexGetter`1) that a first cut of ClassifyArm flipped.
///       NOTE what A does NOT cover: it compares the ACCEPT/REJECT split only, so it is deliberately blind to
///       WHICH of the two reject reasons is reported. The #397 split itself is pinned by B4, never by A.
///   B — ALL THREE CLASSES ARE REACHABLE. Each of Authorable / ReadOnlyProjection / WritableButUnextractable is
///       exhibited by a REAL type in the live assembly and asserted by name. The third is the arm this guard is
///       really for: without it, <c>WritableButUnextractable</c> is a branch no live type enters, and a
///       later refactor could delete or invert it with every guard still green.
///   C — THE TWO ANOMALY LINES CANNOT BE READ AS EACH OTHER. #397's part 1 is that the output must DISTINGUISH a
///       real coverage gap from a correct exclusion. So: the writable case's line says WRITABLE BUT UNEXTRACTABLE
///       and names the type and its writable count; the read-only case's line says neither of those things.
///
/// The types in B/C are named deliberately, and a rename or a Mutagen bump that changes their shape SHOULD break
/// this guard — that is the tripwire working. #397's whole point is that a bump is what arms this class; a guard
/// that survived the shape changing would be pinning nothing.
/// </summary>
public static class ArmClassificationProbe
{
    // Real types from the live assembly, each chosen as the exhibit of one class. Verified by direct reflection before
    // being written down here; the counts are asserted below rather than trusted.
    const string AuthorableExhibit = "Armor";                            // getter interface + mutable twin
    const string ProjectionExhibit = "SkyrimMultiModOverlay";            // no getter interface, 0 writable
    const string ProjectionExhibit2 = "MergedCellBlock";                 // no getter interface, 0 writable
    const string UnextractableExhibit = "ArmorAddonWeightSliderContainer"; // no getter interface, 2/2 writable

    public static int RunGuard(string[] args)
    {
        var asm = typeof(IArmorGetter).Assembly;
        int failures = 0;

        void Check(string label, bool ok, string? detail = null)
        {
            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(ok || detail is null ? "" : $"\n        -> {detail}")}");
            if (!ok) failures++;
        }

        Console.WriteLine("arm-classification-guard — the #397 writable-but-unextractable split");
        Console.WriteLine($"  assembly: {asm.GetName().Name} {asm.GetName().Version}");
        Console.WriteLine();

        // ---------------------------------------------------------------- A: behavior neutrality
        // The predicate as it stood before #397, reproduced literally.
        static bool OriginalPredicate(Type t) =>
            CorpusGenerator.MutableInterfaceFor(CorpusGenerator.GetterInterfaceFor(t) ?? t) != null;

        // BOTH assemblies the corpus walks, not just Skyrim. BuildCorpus seeds IPexFileGetter from
        // Mutagen.Bethesda.Core and FindUnionArms walks whichever assembly a getter interface lives in, so a
        // Skyrim-only sweep asserts this invariant over part of its own domain. It is also not a hypothetical
        // gap: the two types that made this check necessary (FormLinkNullableGetter`1, FormLinkOrIndexGetter`1)
        // live in Core and are invisible from Skyrim.
        var candidates = new[] { asm, typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter).Assembly }
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && !CorpusGenerator.IsOverlayTwin(t))
            .ToList();

        var disagreements = candidates
            .Where(t => OriginalPredicate(t) != (CorpusGenerator.ClassifyArm(t) == CorpusGenerator.ArmClass.Authorable))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Check($"A. accepted-arm set unchanged across {candidates.Count} concrete non-overlay classes " +
              $"in Mutagen.Bethesda.Skyrim + Mutagen.Bethesda.Core",
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

        Check("C1. the coverage-gap line is marked WRITABLE BUT UNEXTRACTABLE",
            gapLine.Contains("WRITABLE BUT UNEXTRACTABLE", StringComparison.Ordinal), gapLine);

        Check("C2. the coverage-gap line names the type",
            gapLine.Contains(UnextractableExhibit, StringComparison.Ordinal), gapLine);

        // "2/2" — asserted, not assumed. If a bump changes this type's writable surface the guard says so.
        Check("C3. the coverage-gap line carries a non-zero writable count",
            System.Text.RegularExpressions.Regex.IsMatch(gapLine, @"\byet ([1-9]\d*)/\d+\b"), gapLine);

        Check("C4. the correct-exclusion line is NOT marked as a coverage gap",
            !okLine.Contains("WRITABLE BUT UNEXTRACTABLE", StringComparison.Ordinal), okLine);

        Check("C5. the correct-exclusion line says read-only projection",
            okLine.Contains("read-only projection", StringComparison.Ordinal), okLine);

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "arm-classification-guard: PASS (all arms green)"
            : $"arm-classification-guard: FAIL ({failures} arm(s) red)");
        return failures == 0 ? 0 : 1;
    }
}

using Xunit;

namespace HousecarlMcpTests;

/// <summary>No banned consent phrase in shipped literals, a capped exemption table, verb recitals that are real and
/// complete, pinned WriteVerbs homes, default marker and tail gloss, and scan roots that match what ships. One test
/// per arm of the description-vocab-guard probe this replaces; the arm ids are the probe's own labels.</summary>
[Trait("tier", "unit")]
public sealed class DescriptionVocabularyScanTests
{
    static void Arm(string id)
    {
        var hits = DescriptionVocabularyScan.All.Where(r => r.Id == id).ToList();
        Assert.True(hits.Count > 0, $"the scan recorded no arm with id '{id}'");
        foreach (var r in hits)
            Assert.True(r.Ok, r.Label + "\n  - " + string.Join("\n  - ", r.Detail.Take(20)));
    }

    // Every check the scan records passes, including INV2 (every declared exemption still fires) and any added later.
    [Fact]
    public void EveryRecordedCheckPasses()
    {
        var failed = DescriptionVocabularyScan.All.Where(r => !r.Ok)
            .Select(r => r.Label + "\n  - " + string.Join("\n  - ", r.Detail.Take(20))).ToList();
        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }

    // ---- the two-reader net over the shipped source ----

    // GREEN-LINEBREAK: every run a Line call already closed stayed two sentences.
    [Fact] public void ARunALineCallClosedStaysTwoSentences() => Arm("GREEN-LINEBREAK");

    // GREEN-ROOTS: the trees scanned are exactly the independently written shipped-tree list.
    [Fact] public void TheScannedTreesAreExactlyTheShippedTrees() => Arm("GREEN-ROOTS");

    // SHIP-DERIVED: the trees build-plugin.ps1 publishes are exactly the published list.
    [Fact] public void ThePackagingScriptPublishesExactlyTheShippedTrees() => Arm("SHIP-DERIVED");

    // MANIFEST-SET: the files scanned are exactly the files the shipped assemblies' PDBs say were compiled.
    [Fact] public void TheScannedFilesAreExactlyTheCompiledFiles() => Arm("MANIFEST-SET");

    // INV6-PARSE: every scanned file parses as C#.
    [Fact] public void EveryScannedFileParses() => Arm("INV6-PARSE");

    // INV6-AGREE: the two independently written readers agree about every literal in every file.
    [Fact] public void TheTwoReadersAgreeAboutEveryLiteral() => Arm("INV6-AGREE");

    // INV6-DIRECTIVES: no file the readers disagree about carries conditional compilation.
    [Fact] public void NoDisagreedFileCarriesConditionalCompilation() => Arm("INV6-DIRECTIVES");

    // ---- consent vocabulary ----

    // INV1: every consent phrase is absent from the shipped literals, or carries its correction clause.
    [Fact] public void NoBannedConsentPhraseIsInAShippedLiteral() => Arm("INV1");

    // INV2-DEGEN: the exemption table cannot absorb an arbitrary miss (capped, scoped, grounded).
    [Fact] public void TheExemptionTableIsCappedScopedAndGrounded() => Arm("INV2-DEGEN");

    // ---- reach ----

    // INV5-DESCRIPTIONS: every compiled [Description] is covered by a scanned source literal.
    [Fact] public void EveryCompiledDescriptionIsCoveredByTheScan() => Arm("INV5-DESCRIPTIONS");

    // INV5-CONSTS: every compile-time string const in the shipped assemblies is covered.
    [Fact] public void EveryCompiledStringConstIsCoveredByTheScan() => Arm("INV5-CONSTS");

    // ---- verb recitals and the WriteVerbs homes ----

    // INV3-TOKENS: every verb recited on the surface is a real verb.
    [Fact] public void EveryRecitedVerbIsARealVerb() => Arm("INV3-TOKENS");

    // INV3-UNION: the recitals name the whole published vocabulary.
    [Fact] public void TheRecitalsNameEveryPublishedVerb() => Arm("INV3-UNION");

    // INV4-HOMES: WriteVerbs.All and AllRecital agree with each other and the independent vocabulary.
    [Fact] public void TheAllVerbHomesAgreeWithTheIndependentVocabulary() => Arm("INV4-HOMES");

    // INV4-CREATEHOMES: WriteVerbs.OnCreate and OnCreateRecital agree with each other and the create vocabulary.
    [Fact] public void TheCreateVerbHomesAgreeWithTheIndependentVocabulary() => Arm("INV4-CREATEHOMES");

    // INV4-COMPOSEHOMES: WriteVerbs.InCompose and InComposeRecital agree with each other and the compose vocabulary.
    [Fact] public void TheComposeVerbHomesAgreeWithTheIndependentVocabulary() => Arm("INV4-COMPOSEHOMES");

    // INV4-MARK: AllRecital marks exactly one verb (default), and it is Set.
    [Fact] public void AllRecitalMarksExactlySetAsTheDefault() => Arm("INV4-MARK");

    // INV4-MARKCOVER: the marker pattern and the character walk agree about which parentheticals are markers.
    [Fact] public void TheTwoMarkerReadersAgree() => Arm("INV4-MARKCOVER");

    // INV4-DEFAULT: every marked (default) agrees with its slot's declared default, and every marked verb is Set.
    [Fact] public void EveryMarkedDefaultAgreesWithItsSlot() => Arm("INV4-DEFAULT");

    // INV4-TAILGLOSS: the verb AllRecital ends with is the one the glued gloss describes.
    [Fact] public void TheRecitalTailIsTheVerbTheGluedGlossDescribes() => Arm("INV4-TAILGLOSS");

    // ---- the checkers, driven with synthetic input ----

    // RED-BANNED: a synthetic sentence saying a banned phrase is reported.
    [Fact] public void ABannedPhraseIsReported() => Arm("RED-BANNED");

    // RED-COMPANION: a companioned phrase with no correction clause is reported.
    [Fact] public void ACompanionedPhraseWithoutItsClauseIsReported() => Arm("RED-COMPANION");

    // GREEN-COMPANION: the same sentence with the correction clause is not reported.
    [Fact] public void ACompanionedPhraseWithItsClauseIsNotReported() => Arm("GREEN-COMPANION");

    // GREEN-EXEMPT: a declared exemption whose site matches suppresses the violation.
    [Fact] public void AMatchingExemptionSuppresses() => Arm("GREEN-EXEMPT");

    // RED-EXEMPT: an exemption declared for a different site does not suppress it.
    [Fact] public void AnExemptionForAnotherSiteDoesNotSuppress() => Arm("RED-EXEMPT");

    // GREEN-DEADEXEMPT: an exemption that fired is not reported dead.
    [Fact] public void AFiredExemptionIsNotDead() => Arm("GREEN-DEADEXEMPT");

    // RED-DEADEXEMPT: an exemption that matched nothing is reported.
    [Fact] public void AnUnusedExemptionIsReportedDead() => Arm("RED-DEADEXEMPT");

    // RED-DEGEN: an over-cap table, an unscoped row and a groundless row are each reported.
    [Fact] public void ADegenerateExemptionTableIsReported() => Arm("RED-DEGEN");

    // RED-VERB: a recital carrying a non-verb token is read, and the token is visible.
    [Fact] public void ARecitalWithANonVerbTokenIsRead() => Arm("RED-VERB");

    // RED-NOTVERB: a separator-joined run with no verb is not read as a recital.
    [Fact] public void ARunWithNoVerbIsNotARecital() => Arm("RED-NOTVERB");

    // RED-MARK: a recital marking the wrong verb (default) is read as marking that verb.
    [Fact] public void AMovedDefaultMarkerIsRead() => Arm("RED-MARK");

    // RED-HOMES: the vocabulary-homes comparison reports a disagreement.
    [Fact] public void TheHomesComparisonReportsADisagreement() => Arm("RED-HOMES");

    // RED-NESTEDTYPE: a nested type's [Description] is enumerated once and its members are still reached.
    [Fact] public void ANestedTypesDescriptionIsCountedOnce() => Arm("RED-NESTEDTYPE");

    // RED-MARKBOUND: a marker cannot start mid-token, and each rejection states its own reason.
    [Fact] public void AMarkerCannotStartMidToken() => Arm("RED-MARKBOUND");

    // RED-MARKSLOT: a marked default that disagrees with the slot's declared default is reported.
    [Fact] public void AMarkedDefaultDisagreeingWithItsSlotIsReported() => Arm("RED-MARKSLOT");

    // RED-RENDER: a slot's declared default is rendered for every constant type; an unspellable one is refused.
    [Fact] public void ASlotDefaultIsRenderedOrRefusedByName() => Arm("RED-RENDER");

    // RED-DIRECTIVES: a conditional-compilation directive is named with its line, and an ordinary one is not.
    [Fact] public void AConditionalDirectiveIsNamed() => Arm("RED-DIRECTIVES");

    // RED-GLOSSWORD: a gloss names a verb as a word, so ordinary English is not read as a verb.
    [Fact] public void AGlossNamesAVerbOnlyAsAWholeWord() => Arm("RED-GLOSSWORD");

    // RED-TAILGLOSS: appending a ninth verb moves the glued gloss onto it, and that is reported.
    [Fact] public void ANinthVerbMovesTheGlossAndIsReported() => Arm("RED-TAILGLOSS");

    // RED-APPENDRUN: consecutive Append/Write literals on one receiver merge; the shapes that are not one run do not.
    [Fact] public void AnAppendRunMergesAndNonRunsDoNot() => Arm("RED-APPENDRUN");

    // RED-COVER: INV5's coverage predicate reports a compiled string no literal accounts for.
    [Fact] public void TheCoveragePredicateReportsAnUncoveredString() => Arm("RED-COVER");

    // RED-ROOTS: a missing shipped tree and an extra scanned tree are each reported.
    [Fact] public void ARootSetMismatchIsReportedBothWays() => Arm("RED-ROOTS");

    // RED-MANIFEST: a compiled file never scanned and a scanned file never compiled are each reported.
    [Fact] public void AManifestMismatchIsReportedBothWays() => Arm("RED-MANIFEST");

    // RED-SHIPDERIVE: the packaging derivation follows ProjectReferences and names every call it could not resolve.
    [Fact] public void ThePackagingDerivationNamesWhatItCannotResolve() => Arm("RED-SHIPDERIVE");

    // GREEN-AGREE: two readers that found the same literals are not reported as disagreeing.
    [Fact] public void IdenticalReaderOutputAgrees() => Arm("GREEN-AGREE");

    // RED-AGREE: a missed literal, a wrong hole depth and a duplicate are each reported.
    [Fact] public void AReaderDisagreementIsReported() => Arm("RED-AGREE");

    // GREEN-FIXTURE-PARSES: the reader fixture is valid C#.
    [Fact] public void TheReaderFixtureParses() => Arm("GREEN-FIXTURE-PARSES");

    // GREEN-READERS-AGREE: both readers agree over every shape in the fixture.
    [Fact] public void BothReadersAgreeOverTheFixture() => Arm("GREEN-READERS-AGREE");

    // GREEN-SHAPES: every fixture shape reaches a scannable sentence, and comment text reaches none.
    [Fact] public void EveryFixtureShapeReachesASentence() => Arm("GREEN-SHAPES");

    // RED-HOLES: every phrase planted behind an interpolation hole is reported.
    [Fact] public void APhraseBehindAnInterpolationHoleIsReported() => Arm("RED-HOLES");

    // RED-COMMENTS: a phrase planted in a comment is not reported.
    [Fact] public void APhraseInACommentIsNotReported() => Arm("RED-COMMENTS");
}

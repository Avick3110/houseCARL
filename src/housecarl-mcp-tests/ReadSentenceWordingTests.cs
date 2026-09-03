using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// EVERY SENTENCE ON THE READ CATALOGUE, PINNED ONCE AT ITS HOME.
///
/// <para>Wording is proven here and nowhere else. A fact test asserts structure keyed by the record it is
/// about (<see cref="Facts"/>); where it must say a sentence arrived, it asserts the catalogue member's
/// IDENTITY (<see cref="Facts.States"/>), never its wording. So this file is the one place a spelling is
/// written down, and it is written down ONCE.</para>
///
/// <para><b>The pin is a second spelling, not a construction.</b> Every expected value below was typed out
/// beside the sentence rather than read off the symbol. An assertion that reads the constant it is checking
/// cannot fail on the constant being emptied — <c>MustStateAttribute</c>'s own doc comment records why, and
/// a commit that gutted three write-surface sentences to placeholders passed the whole suite green. That is
/// the reason this file is long: the length IS the pin.</para>
///
/// <para><b>The population is DERIVED</b> (<see cref="SentenceCatalogue"/>), so a sentence added to the
/// catalogue with no second spelling fails <see cref="EveryCatalogueMemberHasExactlyOneSecondSpelling"/> by
/// name, and a member shape the deriver cannot pin fails there too rather than being filtered away.</para>
///
/// <para><b>The two nets are complementary.</b> The second spelling catches a REWORD that changes meaning;
/// <c>[MustState]</c> catches a GUTTING that a wholesale rewrite of both copies would slip past. The
/// <c>[MustState]</c>/<c>[NoClaims]</c> check moved here from <c>OwnedChildContentProbe</c>, which dies with
/// its conversion — without the move all 73 consts lose their claim check at once.</para>
/// </summary>
[SentenceCatalogue]
[Trait("tier", "unit")]
public sealed class ReadSentenceWordingTests
{
    static Type Catalogue => typeof(ReadSentences);

    // ---- the second spellings ---------------------------------------------------------------------------
    //
    // Written by hand, one per derived member, and the only hand-written thing in this file. A few are
    // referenced by others below, because the catalogue itself composes them that way — the spelling is
    // still this file's own, never the product's symbol.

    const string ToolRecords = "housecarl_records";
    const string ToolLoadOrderStatus = "housecarl_load_order_status";

    const string NotReadS = "other plugin(s) touch this record; their declarations for this field were not read";

    const string NotReadFramingS =
        "note: this response annotates field(s) that hold CHILD RECORDS ({0}). Child records are declared per " +
        "plugin — so what one plugin's body carries is not the whole story for that field. This read did not open " +
        "the other plugins' bodies to see what they declare. To get a read that does, and names them: " +
        ToolRecords + " with project={{\"form\": \"tree\"}} — the same formids, every touching plugin's " +
        "declaration, in text or json, and it spills to to_file like any other form.";

    const string DeclaredByS = "declared by";
    const string CarriedByS = "carried by";
    const string CouldNotReadS = "could NOT be read";
    const string NoDeclarersS = "none of the provider bodies read declares child records in this field";

    const string DeclarersLeadS =
        "child records — declared per plugin, read off the provider bodies this tree already fetched. A MANY-child " +
        "field (\"" + DeclaredByS + " …\") is assembled by the game from every plugin that declares any; a ONE-child " +
        "field (\"" + CarriedByS + " N\") is ONE record those providers override, resolved by load order:";

    const string FixedPartLeadS =
        " This response is {2} chars, longer than the max_chars={0} it was given: what it must carry whatever the " +
        "budget — its header, the accounting above, the closing line for anything it cut short, the boundary — ";

    const string PropLabelS = " matching '{0}'";

    /// <summary>The map the completeness arm below proves covers the derived value-shaped members exactly.</summary>
    static readonly IReadOnlyDictionary<string, object?> Spelled = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        // ---- the cheap tier ----------------------------------------------------------------------------
        [nameof(ReadSentences.NotRead)] = NotReadS,
        [nameof(ReadSentences.NotReadFraming)] = NotReadFramingS,

        // ---- the precise tier --------------------------------------------------------------------------
        [nameof(ReadSentences.DeclaredBy)] = DeclaredByS,
        [nameof(ReadSentences.CarriedBy)] = CarriedByS,
        [nameof(ReadSentences.CouldNotRead)] = CouldNotReadS,
        [nameof(ReadSentences.NoDeclarers)] = NoDeclarersS,
        [nameof(ReadSentences.DeclarersLead)] = DeclarersLeadS,
        [nameof(ReadSentences.DeclarersHeader)] =
            "child records — declared per plugin (see above for what the shapes mean):",
        [nameof(ReadSentences.DeclarerNameCap)] = 3,
        [nameof(ReadSentences.DeclarersOverflowRemedy)] = " — format=json for the full list",
        [nameof(ReadSentences.ClauseFieldsMaxChars)] = 120,

        // ---- the sweep accounting ----------------------------------------------------------------------
        [nameof(ReadSentences.SweepAccountingLead)] = "[accounting:",
        [nameof(ReadSentences.SweepAllVisible)] =
            " all {0} dangling ref(s) found by this sweep appear above.",
        [nameof(ReadSentences.SweepVisible)] =
            " {0} of the {1} dangling ref(s) found by this sweep appear above.",
        [nameof(ReadSentences.SweepOmittedByBudget)] =
            " {0} were never listed: the listing budget (limit={1}) ran out",
        [nameof(ReadSentences.SweepOmittedByCut)] =
            " {0} did not fit this response (max_chars={1})",
        [nameof(ReadSentences.SweepSections)] = " {0} of {1} plugin section(s) were rendered.",
        [nameof(ReadSentences.SweepDialogueSeedSections)] = " {0} of {1} seed section(s) were rendered.",
        [nameof(ReadSentences.SweepExcludedCut)] =
            " {0} of {1} plugin(s) that could not be parsed are named above.",
        [nameof(ReadSentences.SweepUnreadCut)] =
            " {0} of {1} plugin(s) whose records could not be read are named above.",
        [nameof(ReadSentences.SweepRosterLead)] = " Missing here, by source plugin: ",
        [nameof(ReadSentences.SweepRosterCut)] = " (the {0} largest of {1}; the rest are not named here)",
        [nameof(ReadSentences.SweepNoSectionRule)] =
            " A plugin whose whole set is missing here, with nothing else to report, gets no section of its own.",
        [nameof(ReadSentences.SweepRemedyLimit)] = " Raise limit= to list more.",
        [nameof(ReadSentences.SweepRemedyMaxChars)] = " Raise max_chars= to fit more of what was found.",
        [nameof(ReadSentences.SweepRemedyScope)] =
            " Scoping plugins= to one of these re-spends the whole listing budget on that plugin; whether you then see " +
            "its set in full depends on limit= and on max_chars=, which both still apply.",
        [nameof(ReadSentences.SweepRemedyCountsOnly)] =
            " counts_only=true returns the by-source tally for every plugin, capped only in how many ROWS it prints.",
        [nameof(ReadSentences.SweepClose)] = "]",
        [nameof(ReadSentences.SweepFixedPartLead)] = FixedPartLeadS,
        [nameof(ReadSentences.SweepCapTooSmall)] =
            FixedPartLeadS + "does not fit in that many chars, so raise it to at least {1}.",
        [nameof(ReadSentences.SweepCapOvershot)] =
            FixedPartLeadS + "does fit, but one body unit was written before its size could be measured and ran " +
            "past what was left, so raise it to at least {1}.",
        [nameof(ReadSentences.SweepBoundary)] =
            "checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain spatial " +
            "integrity (CRC/grid), flag required-but-null fields, list unused-master cleanup, or link-check an owned " +
            "item's ownership 'variable' word (a rank/global Mutagen can't type on an override); a null FormLink is a " +
            "legal optional.",
        [nameof(ReadSentences.SweepRosterRows)] = 10,

        // ---- the scripts family ------------------------------------------------------------------------
        [nameof(ReadSentences.SweepScriptPropLabelFormat)] = PropLabelS,
        [nameof(ReadSentences.SweepScriptUnboundNotChecked)] =
            "unbound NOT CHECKED (findings= excluded both unbound classes)",
        [nameof(ReadSentences.SweepScriptObjectOnly)] = " (object only — unbound_scalar NOT CHECKED)",
        [nameof(ReadSentences.SweepScriptScalarOnly)] = " (scalar only — unbound_object NOT CHECKED)",
        [nameof(ReadSentences.SweepScriptNullNotChecked)] =
            "bound-but-null NOT CHECKED (findings= excluded 'bound_null')",
        [nameof(ReadSentences.SweepScriptAllVisible)] =
            " all {0} record section(s) found by this sweep appear above.",
        [nameof(ReadSentences.SweepScriptVisible)] =
            " {0} of the {1} record section(s) found by this sweep appear above.",
        [nameof(ReadSentences.SweepScriptFindings)] =
            " {0} of the {1} property finding(s) this sweep found were listed: the listing budget (limit={2}) ran out. " +
            "True totals: {3}.",
        [nameof(ReadSentences.SweepScriptBoundary)] =
            "checks Auto (CK-editable) properties across the extends chain — not code-driven full properties. An " +
            "unbound object property is the silent-None footgun, but CAN be intentional (filled at runtime) — a " +
            "finding is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed " +
            "clean.",

        // ---- which families answered -------------------------------------------------------------------
        [nameof(ReadSentences.SweepFamiliesDefaulted)] =
            "findings= was not given, so this sweep ran the default family only: {0}.",
        [nameof(ReadSentences.SweepFamiliesChosen)] =
            "findings= selected, and this response answers for: {0}.",
        [nameof(ReadSentences.SweepFamiliesAll)] =
            "findings= ran every findings family this surface registers: {0}.",
        [nameof(ReadSentences.SweepFamiliesNoneAnswered)] =
            "findings= answered for NO family: every family this call selected refused, and each states its own ground " +
            "in its own section below.",
        [nameof(ReadSentences.SweepFamiliesRefused)] =
            " It did NOT answer for: {0} — that family's own section states why, and its findings are absent rather " +
            "than clean.",
        [nameof(ReadSentences.SweepFamiliesAbsent)] =
            " It did NOT run: {0} — ask for it with the findings= spelling named beside each.",
        [nameof(ReadSentences.SweepFamilyNotRun)] = "{0} ({1})",
        [nameof(ReadSentences.SweepFamilyOffOrderSkipped)] =
            "the {0} family did NOT sweep {1}: that plugin is on disk but not in the active load order, and only the " +
            "errors family has an off-order lane. Its findings for that file are absent, not clean.",
        [nameof(ReadSentences.SweepBoundaryLabelFor)] = "boundary ({0}): ",
        [nameof(ReadSentences.SweepMergedTitle)] = "check — derived-findings sweep",
        [nameof(ReadSentences.SweepFamilySectionHead)] = "[{0}] {1}",

        // ---- the dialogue family -----------------------------------------------------------------------
        [nameof(ReadSentences.DialogueNeedsSeeds)] =
            "findings=[\"dialogue\"] needs seeds=. This family validates the topics and quests you NAME, and it will " +
            "NOT sweep the whole load order — that is a declared cost bound, not a missing feature: a whole-order pass " +
            "is a per-topic graph walk across every plugin that touches each topic, and the order this bound was " +
            "measured on carries 82,343 dialogue topics (one quest's 235 owned topics alone took 13.6 s). " +
            "Name what to validate: seeds=[\"XXXXXX:Plugin.esp\"] takes a dialogue topic (DIAL), a quest (QUST) — which " +
            "expands to every topic that quest owns — a dialogue view (DLVW), or a dialogue branch (DLBR).",
        [nameof(ReadSentences.DialogueNoSeedResolved)] =
            "findings=[\"dialogue\"] validated NOTHING: not one of the {0} seed(s) named resolved. {1} A seed is a DIAL, " +
            "QUST, DLVW or DLBR FormID spelled 'XXXXXX:Plugin.esp' and is resolved against the ACTIVE load order — a " +
            "record only a disabled plugin defines is not reachable here.",
        [nameof(ReadSentences.DialogueSeedRefused)] = "  [X] {0} — NOT validated: {1}\n",
        [nameof(ReadSentences.DialogueScopeNote)] =
            "scope: the dialogue family is seeded, not swept — plugins=, type=, formids=, editorid_contains= and " +
            "exclude= scope the sweep families and do NOT scope it. {0} It has no off-order lane: a seed is a record, " +
            "and it must resolve in the ACTIVE load order.",
        [nameof(ReadSentences.DialogueScopeAllSeeds)] = "It reached all {0} seed(s) given in seeds=.",
        [nameof(ReadSentences.DialogueScopeSomeSeeds)] =
            "It reached {0} of the {1} seed(s) given in seeds= — limit= stopped it there.",
        [nameof(ReadSentences.DialogueBoundary)] =
            "validates the dialogue graph at the data layer — quest and branch wiring, LinkTo and previous-link targets " +
            "(an EMPTY previous-link is the vanilla norm and is never flagged), each voiced line's .fuz on disk, each " +
            "result script bound and compiled, the CK-parity subrecords, and a subset of MALFORMED conditions. It " +
            "cannot EVALUATE whether a WELL-FORMED condition passes — only the running game can{0} — and it does not " +
            "check lip-sync or audio content, so a clean pass here does NOT mean the dialogue will play as intended. " +
            "The per-line checks audit the WINNING topic's INFO list only. The effective merged INFO order — which line " +
            "the game reaches FIRST — is not a finding and is not here: ask records project=info_order for it.{1}",
        [nameof(ReadSentences.DialogueBoundaryRecordLevel)] =
            "validates the CK-parity subrecords the Creation Kit always writes on the record you named, and nothing " +
            "else: every seed this call reached was a dialogue view (DLVW) or branch (DLBR), which own no INFO list, " +
            "so this is a record-level CK-parity check only — no dialogue graph, voice file, result script or " +
            "condition was checked here. Validate the owning topics (DIAL) or quest (QUST) for those.{0}{1}",
        [nameof(ReadSentences.DialogueRecordLevelScope)] =
            "scope: this is a record-level CK-parity check only — it does not validate any dialogue graph, voice, " +
            "script, or condition surface. Validate the owning topics (DIAL) or quest (QUST) for those.",
        [nameof(ReadSentences.DialogueViewParityOk)] =
            "  CK-parity: OK — the DNAM and ENAM byte subrecords the Creation Kit always writes are both present.\n",
        [nameof(ReadSentences.DialogueBranchParityOk)] =
            "  CK-parity: OK — the TNAM (Category) and DNAM (Flags) subrecords the Creation Kit always writes are " +
            "both present.\n",
        [nameof(ReadSentences.DialogueConditioned)] =
            " — {0} line(s) here carry conditions, checked for malformedness but not evaluated",
        [nameof(ReadSentences.DialogueReadIncomplete)] =
            " A BSA failed to read this build, so an \"absent\" voice file or .pex above may merely be unscanned — see " +
            ToolLoadOrderStatus + ".",
        [nameof(ReadSentences.SweepDialogueAllVisible)] =
            " every one of the {0} topic(s) these seeds own is listed.",
        [nameof(ReadSentences.SweepDialogueVisible)] =
            " {0} of the {1} topic(s) these seeds own are listed; the rest did not fit this response's max_chars.",
        [nameof(ReadSentences.SweepDialogueSeedsCut)] =
            " {0} of the {1} seed(s) named were reached; {2} were NOT reached because the seed budget (limit={3}) " +
            "ran out.",
        [nameof(ReadSentences.SweepDialogueProblems)] =
            " True totals: {0} finding(s) across {1} topic(s).",
        [nameof(ReadSentences.SweepDialogueRefusalsCut)] =
            " {0} of the {1} seed(s) that could not be validated are named above.",
        [nameof(ReadSentences.DialogueSeedHead)] = "seed {0} — {1} {2}, winner {3}, {4} topic(s)\n",
        [nameof(ReadSentences.DialogueSeedNoTopics)] =
            "  this quest owns NO dialogue topics in the active load order — nothing to validate. If you expected some, " +
            "check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n",
        [nameof(ReadSentences.DialogueQuestParityOk)] =
            "  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its " +
            "Flags (FNAM).\n",
        [nameof(ReadSentences.DialogueCounts)] =
            "{0} of the {1} seed(s) reached were validated, {2} topic(s), {3} finding(s) across them.\n",
        [nameof(ReadSentences.DialogueCountsOnly)] =
            "counts_only=true: the totals above and the unreachable seeds below, and no per-topic blocks. Drop " +
            "counts_only= to see each topic's findings.\n",
    };

    // ---- the composers --------------------------------------------------------------------------------
    //
    // A composer's arguments cannot be derived from its signature, so the argument row is hand-written — and
    // the derivation covers it in the direction that matters: a composer with NO row fails the completeness
    // arm, so a new one cannot arrive unpinned. Each row picks arguments that exercise the composer's
    // interesting branch (a cap overflow, an unreadable provider, a scope label) rather than its easiest one.

    /// <summary>A findings result with both unbound classes, a bound-null count, and a
    /// <c>property_contains=</c> scope in force — the three script composers' interesting branch.</summary>
    static ScriptCheckResult Findings => new(
        Array.Empty<RecordScriptFindings>(), 0, 0,
        TotalUnbound: 7, TotalNullObject: 3, TotalUnverifiable: 0,
        Capped: false, ReadIncomplete: false,
        ExcludedPlugins: new Dictionary<string, string>(), Error: null,
        TotalUnboundObject: 5, TotalUnboundScalar: 2, PropertyContains: "Ash");

    static readonly string[] TwoFields = { "Persistent", "Temporary" };
    static readonly string[] FourProviders = { "A.esp", "B.esp", "C.esp", "D.esp" };
    static readonly string[] OneUnreadable = { "E.esp" };

    static readonly IReadOnlyDictionary<string, (object?[] Args, object? Expected)> Composed =
        new Dictionary<string, (object?[], object?)>(StringComparer.Ordinal)
        {
            [nameof(ReadSentences.FieldList)] = (new object?[] { TwoFields }, "Persistent, Temporary"),

            [nameof(ReadSentences.NotReadClause)] =
                (new object?[] { TwoFields }, string.Format(NotReadFramingS, "Persistent, Temporary")),

            [nameof(ReadSentences.NotReadNote)] = (new object?[] { 7 }, "7 " + NotReadS),

            // A COLLECTION field with more declarers than the cap, plus one body that could not be read:
            // the names are cut at three with a count of the rest, and the unreadable half is stated BESIDE
            // the declarers rather than folded into them.
            [nameof(ReadSentences.DeclarersNote)] =
                (new object?[] { OwnedChildShape.Collection, FourProviders, OneUnreadable },
                 DeclaredByS + " A.esp, B.esp, C.esp (+1 more); 1 provider(s) " + CouldNotReadS + " (E.esp)"),

            // The clause's worst case: the framing's own length, plus the field list's char budget, plus the
            // glue (the "{0}" it loses, the ", …" an over-long list gains, and the two newlines the text lane
            // wraps it in). Written off this file's OWN spelling of the framing, never off the constant.
            [nameof(ReadSentences.ClauseReserve)] = (new object?[] { true }, NotReadFramingS.Length + 120 + 8),

            [nameof(ReadSentences.ScriptUnboundTotal)] =
                (new object?[] { Findings, true, true }, "7 unbound matching 'Ash'"),

            [nameof(ReadSentences.ScriptNullTotal)] =
                (new object?[] { Findings, true }, "3 bound-but-null matching 'Ash'"),

            [nameof(ReadSentences.ScriptTotals)] =
                (new object?[] { Findings }, "7 unbound matching 'Ash' + 3 bound-but-null matching 'Ash'"),
        };

    // ---- the arms --------------------------------------------------------------------------------------

    public static TheoryData<string> Values
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var m in SentenceCatalogue.Members(typeof(ReadSentences)))
                if (m.Kind == SentenceCatalogue.Shape.Value) d.Add(m.Name);
            return d;
        }
    }

    public static TheoryData<string> Composers
    {
        get
        {
            var d = new TheoryData<string>();
            foreach (var m in SentenceCatalogue.Members(typeof(ReadSentences)))
                if (m.Kind == SentenceCatalogue.Shape.Composer) d.Add(m.Name);
            return d;
        }
    }

    /// <summary>
    /// The completeness claim, in both directions: every derived member has exactly one row, and every row
    /// names a member that exists. A sentence added to the catalogue with no second spelling fails HERE, by
    /// name — which is the whole reason the population is reflected rather than listed.
    /// </summary>
    [Fact]
    public void EveryCatalogueMemberHasExactlyOneSecondSpelling()
    {
        var derived = SentenceCatalogue.MemberNames(Catalogue).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var written = Spelled.Keys.Concat(Composed.Keys).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        var unpinned = derived.Except(written, StringComparer.Ordinal).ToArray();
        var orphaned = written.Except(derived, StringComparer.Ordinal).ToArray();

        Assert.True(unpinned.Length == 0,
            "These catalogue members have no second spelling in this file:\n  " + string.Join("\n  ", unpinned) +
            "\nA sentence with no pin is a sentence that can be reworded, emptied or deleted with the suite green. " +
            "Write one row per member — a value's spelling in Spelled, a composer's arguments and composed result " +
            "in Composed.");

        Assert.True(orphaned.Length == 0,
            "These rows name members the catalogue no longer has:\n  " + string.Join("\n  ", orphaned) +
            "\nDelete the row with the member.");

        Assert.Equal(derived.Length, written.Length);   // no duplicate rows across the two maps
    }

    [Theory, MemberData(nameof(Values))]
    public void TheSentenceStillSaysWhatItSaid(string name) =>
        Assert.Equal(Spelled[name], SentenceCatalogue.Value(Catalogue, name));

    [Theory, MemberData(nameof(Composers))]
    public void TheComposerStillComposesWhatItComposed(string name) =>
        Assert.Equal(Composed[name].Expected, SentenceCatalogue.Invoke(Catalogue, name, Composed[name].Args));

    // ---- the content net, moved off OwnedChildContentProbe ---------------------------------------------
    //
    // Moved, not copied: the probe keeps running until its own conversion PR deletes it, and this is where the
    // check lives once it does. It answers a DIFFERENT question from the spellings above — "has this sentence
    // been emptied of its claim" rather than "has it been reworded" — and the two nets are why a wholesale
    // rewrite of the sentence AND its second spelling still fails.

    [Fact]
    public void EveryStringMemberDecides_EitherItDeclaresPhrasesOrItStatesWhyItHasNone()
    {
        var undecided = new List<string>();

        foreach (var f in SentenceCatalogue.SentenceFields(Catalogue))
        {
            var must = f.GetCustomAttribute<MustStateAttribute>();
            var none = f.GetCustomAttribute<NoClaimsAttribute>();

            if (must is not null && none is not null)
                undecided.Add($"{f.Name}: declares BOTH [MustState] and [NoClaims] — pick one");
            else if (must is null && none is null)
                undecided.Add($"{f.Name}: declares neither [MustState] phrases nor [NoClaims] with a reason");
            else if (none is not null && none.Reason.Trim().Length == 0)
                undecided.Add($"{f.Name}: [NoClaims] with no stated reason");
        }

        Assert.True(undecided.Count == 0,
            "These catalogue sentences carry no decision:\n  " + string.Join("\n  ", undecided) +
            "\nEvery sentence either declares the phrases whose loss changes what the caller is told, or states " +
            "why it makes no claim. An undecorated const is a sentence nothing is watching.");
    }

    [Fact]
    public void EveryDeclaredPhraseIsStillInTheSentenceThatDeclaresIt()
    {
        var lost = new List<string>();

        foreach (var f in SentenceCatalogue.SentenceFields(Catalogue))
        {
            var must = f.GetCustomAttribute<MustStateAttribute>();
            if (must is null) continue;

            var text = (string?)f.GetValue(null) ?? "";
            foreach (var phrase in must.Phrases)
                if (!text.Contains(phrase, StringComparison.Ordinal))
                    lost.Add($"{f.Name}: no longer states \"{phrase}\"");
        }

        Assert.True(lost.Count == 0,
            "These catalogue sentences no longer state a claim they declare:\n  " + string.Join("\n  ", lost) +
            "\nA phrase is adjusted to its sentence only when the sentence's change was itself ruled — the phrase " +
            "is the second copy of the CLAIM, and quietly editing it to match is how the claim goes.");
    }

    /// <summary>
    /// The vacuity canary for the two arms above. A binding-flag mistake, a renamed attribute or a moved
    /// catalogue would leave both green over an empty population, which is the net failing toward green.
    /// </summary>
    [Fact]
    public void TheContentNetIsMeasuringSomething_NotAnEmptyPopulation()
    {
        var fields = SentenceCatalogue.SentenceFields(Catalogue);
        var decorated = fields.Count(f => f.GetCustomAttribute<MustStateAttribute>() is not null
                                       || f.GetCustomAttribute<NoClaimsAttribute>() is not null);

        Assert.True(fields.Count > 0 && decorated == fields.Count,
            $"The content net walked {fields.Count} string member(s), {decorated} of them decorated. Both arms " +
            "above are vacuous unless this population is real and wholly decided.");
    }
}

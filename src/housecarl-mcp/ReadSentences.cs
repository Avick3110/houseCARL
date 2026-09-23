using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One source per sentence for the read surface's user-facing prose, the <see cref="WriteSentences"/>
/// pattern on the other surface.</summary>
internal static class ReadSentences
{
    // ---- what the game assembles: the additive union across every touching plugin --------------------

    [NoClaims("a word shared by the notes below; each states its own claim")]
    internal const string ChildContent = "child record";

    [NoClaims("a label; the claim — what a union is and why it differs from the value — is UnionFraming's")]
    internal const string UnionLabel = "additive union";

    [MustState("no plugin", "declares child record")]
    internal const string NoUnionMembers = UnionLabel + ": no plugin touching this record declares " + ChildContent + "s here";

    [MustState("declared per plugin", UnionLabel, "own list", ToolNames.Records)]
    internal const string UnionFraming =
        "note: this response annotates field(s) that hold CHILD RECORDS ({0}). Child records are declared per " +
        "plugin and the game assembles the parent's from every plugin that declares any, so one body's list is " +
        "not the whole content. The VALUE shown for such a field is this body's own list, in this body's own " +
        "order — the addresses a write uses; the " + UnionLabel + " beside it is the whole set the game " +
        "assembles, keyed by FormID so a child two plugins both declare counts once. To see which plugin " +
        // This string is a string.Format TEMPLATE ({0} is the field list), so every literal brace is doubled.
        "declares what: " + ToolNames.Records + " with project={{\"form\": \"tree\"}}.";

    /// <summary>The response-level clause over the fields <paramref name="fields"/> the response actually emitted
    /// an annotation for.</summary>
    internal static string UnionClause(IReadOnlyCollection<string> fields) => string.Format(UnionFraming, FieldList(fields));

    // ---- the index-only tier: what the SCAN lanes state, where a union per row is not affordable ----

    [MustState("were not read")]
    internal const string NotRead = "other plugin(s) touch this record; their declarations for this " + ChildContent + " field were not read";

    [MustState("declared per plugin", "were not read", ToolNames.Records)]
    internal const string NotReadFraming =
        "note: this response annotates field(s) that hold CHILD RECORDS ({0}). Child records are declared per " +
        "plugin and the game assembles the parent's from every plugin that declares any, so one body's list is " +
        "not the whole content. A scan answers many rows, so it did not open the other plugins' bodies and their " +
        "declarations were not read. For the " + UnionLabel + " on a record you name: " +
        ToolNames.Records + " with formids= and the field named WITHOUT a quantifier — a '[*count]' column renders " +
        "one number and takes this tier too — which states it on every child-bearing field it emits.";

    /// <summary>The index-only tier's per-field line: the count the index knows, and the honest limit.</summary>
    internal static string NotReadNote(int others) => $"{others} {NotRead}";

    /// <summary>The response-level clause for whichever tier the response actually stated.</summary>
    internal static string OwnedChildClause(IReadOnlyCollection<string> fields, bool unioned) =>
        string.Format(unioned ? UnionFraming : NotReadFraming, FieldList(fields));

    /// <summary>One clause per tier the response actually stated, each over its OWN fields.</summary>
    internal static IReadOnlyList<string> OwnedChildClauses(IReadOnlyCollection<string> unioned,
                                                            IReadOnlyCollection<string> indexOnly)
    {
        var said = new List<string>(2);
        if (unioned.Count > 0) said.Add(OwnedChildClause(unioned, true));
        if (indexOnly.Count > 0) said.Add(OwnedChildClause(indexOnly, false));
        return said;
    }

    /// <summary>The fields a map of field → tier holds on one side of it.</summary>
    internal static IReadOnlyCollection<string> Tier(IEnumerable<KeyValuePair<string, bool>> fields, bool unioned) =>
        fields.Where(kv => kv.Value == unioned).Select(kv => kv.Key).ToList();

    /// <summary>The clause framing a tier uses, for the reserve and for a test that has to find the clause line.</summary>
    internal static string ClauseFraming(bool unioned) => unioned ? UnionFraming : NotReadFraming;

    /// <summary>How many contributing plugins a union names before it summarises the rest as a count.</summary>
    internal const int UnionDeclarerCap = 3;

    /// <summary>The per-field line: what the game assembles here, who contributes it, and how much of it this body's
    /// own list carries.</summary>
    internal static string UnionNote(ChildUnion u)
    {
        string head;
        if (u.Shape == OwnedChildShape.Singular)
            head = u.LivePlugin is null
                ? $"no plugin touching this record declares this {ChildContent}"
                : $"one {ChildContent}: {u.Declarers.Count} plugin(s) hold a copy and OVERRIDE each other; " +
                  $"the live copy is {u.LivePlugin}'s";
        else if (u.Total == 0) head = NoUnionMembers;
        else
            head = $"{UnionLabel}: {u.Total} {ChildContent}(s) across {u.Declarers.Count} plugin(s) — "
                 + string.Join(", ", u.Declarers.Take(UnionDeclarerCap).Select(d => $"{d.Plugin} {d.Count}"))
                 + (u.Declarers.Count > UnionDeclarerCap ? $" (+{u.Declarers.Count - UnionDeclarerCap} more)" : "")
                 + "; " + OwnShare(u);
        return u.Unreadable.Count == 0 ? head
            : head + $"; {u.Unreadable.Count} plugin(s) {CouldNotRead} "
              + $"({string.Join(", ", u.Unreadable.Take(UnionDeclarerCap))}"
              + (u.Unreadable.Count > UnionDeclarerCap ? ", …" : "") + ")";
    }

    /// <summary>How much of the union THIS body declares, in the unit the value beside it is in.</summary>
    static string OwnShare(ChildUnion u) =>
        u.CountsTheRenderedUnit
            ? $"this body's own list carries {u.OwnCount}"
            : $"this body declares {u.OwnCount} of them — the value beside this counts the CONTAINERS holding " +
              $"them, not the {ChildContent}s themselves";

    // ---- the precise tier: WHICH providers declare, off bodies the tree has already fetched ----------

    /// <summary>The per-field label for a collection child; its meaning is stated once by <see
    /// cref="DeclarersLead"/>.</summary>
    [NoClaims("a label; the claim is the plugin names it introduces, and their meaning is DeclarersLead")]
    internal const string DeclaredBy = "declared by";

    [NoClaims("a label; the claim is DeclarersLead's, and the count is evidence rather than an assertion")]
    internal const string CarriedBy = "carried by";

    [MustState("could NOT be read")]
    internal const string CouldNotRead = "could NOT be read";

    [MustState("none of the provider bodies read", "declares child records")]
    internal const string NoDeclarers = "none of the provider bodies read declares child records in this field";

    [MustState("declared per plugin", "assembled by the game", "override")]
    internal const string DeclarersLead =
        "child records — declared per plugin, read off the provider bodies this tree already fetched. A MANY-child " +
        "field (\"" + DeclaredBy + " …\") is assembled by the game from every plugin that declares any; a ONE-child " +
        "field (\"" + CarriedBy + " N\") is ONE record those providers override, resolved by load order:";

    [NoClaims("a label; the claim — what the two shapes mean — is DeclarersLead's, stated once already")]
    internal const string DeclarersHeader = "child records — declared per plugin (see above for what the shapes mean):";

    /// <summary>How many declaring plugins a collection field names before it summarises the rest as a count. It
    /// rides every child-bearing field of every row, so the cap is small.</summary>
    internal const int DeclarerNameCap = 3;

    /// <summary>Points a text reader at the medium carrying the names <see cref="DeclarerNameCap"/> elided; json's
    /// <c>declaring</c> array is never capped.</summary>
    [NoClaims("a remedy fragment; the caller appends it, never DeclarersNote")]
    internal const string DeclarersOverflowRemedy = " — format=json for the full list";

    /// <summary>The precise tier's per-field line, in the voice of the field's shape.</summary>
    internal static string DeclarersNote(OwnedChildShape shape, IReadOnlyList<string> declaring, IReadOnlyList<string> unreadable)
    {
        string head = shape == OwnedChildShape.Singular
            ? $"{CarriedBy} {declaring.Count} provider(s)"
            : declaring.Count == 0 ? NoDeclarers
                : $"{DeclaredBy} {string.Join(", ", declaring.Take(DeclarerNameCap))}"
                  + (declaring.Count > DeclarerNameCap ? $" (+{declaring.Count - DeclarerNameCap} more)" : "");
        return unreadable.Count == 0 ? head
            : head + $"; {unreadable.Count} provider(s) {CouldNotRead} "
              + $"({string.Join(", ", unreadable.Take(DeclarerNameCap))}"
              + (unreadable.Count > DeclarerNameCap ? ", …" : "") + ")";
    }

    /// <summary>The field names a response-level clause is about, derived from what the response annotated rather
    /// than written out in prose.</summary>
    internal static string FieldList(IReadOnlyCollection<string> fields)
    {
        var joined = string.Join(", ", fields);
        return joined.Length <= ClauseFieldsMaxChars ? joined : joined[..ClauseFieldsMaxChars].TrimEnd(',', ' ') + ", …";
    }

    /// <summary>The char budget the derived field list gets inside a clause.</summary>
    internal const int ClauseFieldsMaxChars = 120;

    /// <summary>Slack over a framing's own length.</summary>
    const int ClauseGlue = 8;

    /// <summary>The worst-case chars the response-level clause can cost, reserved out of <c>max_chars</c> before the
    /// body renders.</summary>
    /// <param name="clauses">How many clauses the response may still state: one where only one tier is in play, two
    /// where a record carries both (a named list unioned beside a '[*count]' column on the index-only tier).</param>
    internal static int ClauseReserve(int clauses) =>
        // The longer of the two tiers where only one will be said: the reserve is taken before the fields render,
        // and which tier the response will state is not settled until one of them is emitted.
        clauses <= 0 ? 0
        : clauses == 1 ? Math.Max(UnionFraming.Length, NotReadFraming.Length) + ClauseFieldsMaxChars + ClauseGlue
        : UnionFraming.Length + NotReadFraming.Length + 2 * (ClauseFieldsMaxChars + ClauseGlue);

    // ---- a count table does not page ----------------------------------------------------------------

    /// <summary>The one sentence every lane refuses <c>offset=</c> against a whole-selection answer with (Aaron
    /// 2026-09-22, #810; SPEC §2.1). <c>{0}</c> is the knob that asked for it, so the lanes name their own without
    /// spelling the rule four ways, and <c>{1}</c> is the table clause, present only where there IS a table. What
    /// <c>to_file=</c> spills is the ROWS behind the answer, never the table, which every lane refuses it beside.
    /// It is a string.Format template: every literal brace would be doubled.</summary>
    [MustState("COMPLETE selection", "offset= has nothing to page", "drop offset=", "to_file=")]
    internal const string CountTableNoOffset =
        "{0} answers over the COMPLETE selection, so offset= has nothing to page{1} — drop offset=, or drop {0} " +
        "and spill the rows behind it with to_file=.";

    /// <summary>The clause for a lane whose answer IS a table: what caps it, instead of paging.</summary>
    [MustState("count table", "caps with limit=")]
    internal const string CountTableCapClause = " and the count table it returns caps with limit= instead";

    /// <summary>The sentence with the asking knob named. <paramref name="hasTable"/> false on a lane whose census
    /// renders no table at all, where the limit= clause would name a knob with nothing to cap.</summary>
    internal static string NoOffsetOnCountTable(string knob, bool hasTable = true) =>
        string.Format(CountTableNoOffset, knob, hasTable ? CountTableCapClause : "");

    // ---- the sweep response's omission accounting ---- The prose half of CheckAccounting; the arithmetic is there,
    // and every number below arrives already computed from what the render emitted.

    [NoClaims("a label; the claims it introduces are the accounting's own clauses")]
    internal const string SweepAccountingLead = "[accounting:";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepAllVisible =
        " all {0} dangling ref(s) found by this sweep appear above.";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepVisible =
        " {0} of the {1} dangling ref(s) found by this sweep appear above.";

    /// <summary>Stated only when its own count is non-zero, or it would read as the budget being involved.</summary>
    [MustState("limit=")]
    internal const string SweepOmittedByBudget = " {0} were never listed: the listing budget (limit={1}) ran out";

    [MustState("max_chars=")]
    internal const string SweepOmittedByCut = " {0} did not fit this response (max_chars={1})";

    [MustState("plugin section(s)")]
    internal const string SweepSections = " {0} of {1} plugin section(s) were rendered.";

    [MustState("seed section(s)")]
    internal const string SweepDialogueSeedSections = " {0} of {1} seed section(s) were rendered.";

    [MustState("could not be parsed")]
    internal const string SweepExcludedCut = " {0} of {1} plugin(s) that could not be parsed are named above.";

    [MustState("could not be read")]
    internal const string SweepUnreadCut = " {0} of {1} plugin(s) whose records could not be read are named above.";

    [NoClaims("a label introducing the roster rows; every claim it carries is in the rows and the clauses around it")]
    internal const string SweepRosterLead = " Missing here, by source plugin: ";

    [MustState("not named here")]
    internal const string SweepRosterCut = " (the {0} largest of {1}; the rest are not named here)";

    /// <summary>The type-scope rule, stated wherever a MULTI-TYPE scope was in force and the listing above came out
    /// short.</summary>
    [MustState("ONE listing", "unlisted", "not clean")]
    internal const string SweepTypeScopeRule =
        " There is ONE listing for every type in the scope (types=[{0}]), filled plugin by plugin (non-base plugins " +
        "first, base masters last) and type by type inside each, and {1} cut it short — so ANY of those types can be " +
        "missing from it: absent there means unlisted, not clean. Sweep that type alone, or raise {1}.";

    [MustState("limit=")]
    internal const string SweepKnobLimit = "limit=";

    /// <inheritdoc cref="SweepKnobLimit"/>
    [MustState("max_chars=")]
    internal const string SweepKnobMaxChars = "max_chars=";

    /// <inheritdoc cref="SweepKnobLimit"/>
    [MustState("limit=", "max_chars=")]
    internal const string SweepKnobBoth = "limit= and max_chars=";

    /// <summary>A rule about the listing rather than a claim about this response's contents, stated wherever a
    /// roster is: it tells the reader that a plugin with no section of its own is still in that roster.</summary>
    [MustState("no section of its own")]
    internal const string SweepNoSectionRule =
        " A plugin whose whole set is missing here, with nothing else to report, gets no section of its own.";

    // The remedy is assembled from the causes that actually fired.

    /// <summary>Offered only where the listing budget actually dropped something.</summary>
    [MustState("limit=")]
    internal const string SweepRemedyLimit = " Raise limit= to list more.";

    /// <summary>Offered only where this response could not fit what the budget admitted.</summary>
    [MustState("max_chars=")]
    internal const string SweepRemedyMaxChars = " Raise max_chars= to fit more of what was found.";

    [MustState("plugins=", "limit=", "max_chars=")]
    internal const string SweepRemedyScope =
        " Scoping plugins= to one of these re-spends the whole listing budget on that plugin; whether you then see " +
        "its set in full depends on limit= and on max_chars=, which both still apply.";

    /// <summary>Offered wherever a by-source roster exists, since that is the tally it points at.</summary>
    [MustState("counts_only=true")]
    internal const string SweepRemedyCountsOnly =
        " counts_only=true returns the by-source tally for every plugin, capped only in how many ROWS it prints.";

    [NoClaims("punctuation closing the accounting line")]
    internal const string SweepClose = "]";

    /// <summary>The lead both overrun sentences share, enumerating what a response carries whatever the budget
    /// says. Its three numbers are worded exactly as <see cref="RenderCap.Overran"/> words them — the merged sweep's
    /// notice and every other json document's <c>max_chars_overrun</c> are one member, so they are read with one
    /// parser; what differs is the clause BETWEEN the numbers, which names which of the two overruns happened.</summary>
    [MustState("response is", "over the max_chars=", "its header", "the accounting above", "cut short", "the boundary")]
    internal const string SweepFixedPartLead =
        " This response is {2} chars, over the max_chars={0} it was given: what it must carry whatever the " +
        "budget — its header, the accounting above, the closing line for anything it cut short, the boundary — ";

    /// <summary>The one arm where the response may exceed max_chars, and it says so.</summary>
    [MustState("max_chars=", "raise max_chars to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapTooSmall =
        SweepFixedPartLead + "does not fit in that many chars, so raise max_chars to at least {1}.";

    /// <summary>The other way a response ends up over its cap.</summary>
    [MustState("max_chars=", "raise max_chars to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapOvershot =
        SweepFixedPartLead + "does fit, but one body unit was written before its size could be measured and ran " +
        "past what was left, so raise max_chars to at least {1}.";

    /// <summary>The sweep's honest scope boundary, stated to both transports from here so the two cannot
    /// drift.</summary>
    [MustState("Does NOT verify navmesh/terrain", "required-but-null", "unused-master cleanup", "legal optional")]
    internal const string SweepBoundary =
        "checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain spatial " +
        "integrity (CRC/grid), flag required-but-null fields, list unused-master cleanup, or link-check an owned " +
        "item's ownership 'variable' word (a rank/global Mutagen can't type on an override); a null FormLink is a " +
        "legal optional.";

    /// <summary>The unbound total, spelled so it can never claim a class nobody checked.</summary>
    internal static string ScriptUnboundTotal(ScriptCheckResult r, bool didObject, bool didScalar)
        => !didObject && !didScalar ? SweepScriptUnboundNotChecked
         : didObject && didScalar   ? $"{r.TotalUnbound} unbound{ScriptPropLabel(r)}"
         : didObject                ? $"{r.TotalUnboundObject} unbound{ScriptPropLabel(r)}{SweepScriptObjectOnly}"
                                    : $"{r.TotalUnboundScalar} unbound{ScriptPropLabel(r)}{SweepScriptScalarOnly}";

    /// <summary>The bound-but-null total, same contract as <see cref="ScriptUnboundTotal"/>.</summary>
    internal static string ScriptNullTotal(ScriptCheckResult r, bool didNull)
        => didNull ? $"{r.TotalNullObject} bound-but-null{ScriptPropLabel(r)}" : SweepScriptNullNotChecked;

    /// <summary>Both totals as the accounting restates them, in one string.</summary>
    internal static string ScriptTotals(ScriptCheckResult r)
        => ScriptUnboundTotal(r, r.Classes.HasFlag(ScriptFindingClass.UnboundObject),
                                 r.Classes.HasFlag(ScriptFindingClass.UnboundScalar))
         + " + " + ScriptNullTotal(r, r.Classes.HasFlag(ScriptFindingClass.BoundNull));

    /// <summary>The per-number <c>property_contains=</c> label, on exactly the two counts that filter narrows.
    /// Absent from records-with-scripts and unverifiable, which it does not narrow.</summary>
    [NoClaims("a scope label; the claim is the count it qualifies")]
    internal const string SweepScriptPropLabelFormat = " matching '{0}'";

    static string ScriptPropLabel(ScriptCheckResult r)
        => r.PropertyContains is null ? "" : string.Format(SweepScriptPropLabelFormat, r.PropertyContains);

    /// <summary>Both unbound classes excluded: the total reads NOT CHECKED, never 0, which would say "looked,
    /// found none" about a class nobody looked for.</summary>
    [MustState("NOT CHECKED", "findings=")]
    internal const string SweepScriptUnboundNotChecked = "unbound NOT CHECKED (findings= excluded both unbound classes)";

    [MustState("NOT CHECKED", "unbound_scalar")]
    internal const string SweepScriptObjectOnly = " (object only — unbound_scalar NOT CHECKED)";

    [MustState("NOT CHECKED", "unbound_object")]
    internal const string SweepScriptScalarOnly = " (scalar only — unbound_object NOT CHECKED)";

    [MustState("NOT CHECKED", "bound_null")]
    internal const string SweepScriptNullNotChecked = "bound-but-null NOT CHECKED (findings= excluded 'bound_null')";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptAllVisible =
        " all {0} record section(s) found by this sweep appear above.";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptVisible =
        " {0} of the {1} record section(s) found by this sweep appear above.";

    [MustState("limit=", "True totals")]
    internal const string SweepScriptFindings =
        " {0} of the {1} property finding(s) this sweep found were listed: the listing budget (limit={2}) ran out. " +
        "True totals: {3}.";

    /// <summary>The scripts family's honest scope boundary, stated to both transports from here for the same
    /// reason as <see cref="SweepBoundary"/>.</summary>
    [MustState("Auto (CK-editable)", "not code-driven full properties", "flag to VERIFY", "never passed clean")]
    internal const string SweepScriptBoundary =
        "checks Auto (CK-editable) properties across the extends chain — not code-driven full properties. An " +
        "unbound object property is the silent-None footgun, but CAN be intentional (filled at runtime) — a " +
        "finding is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed " +
        "clean.";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepFaceGenAllVisible =
        " all {0} facegen row(s) found by this sweep appear above.";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepFaceGenVisible =
        " {0} of the {1} facegen row(s) found by this sweep appear above.";

    [MustState("limit=", "listing budget")]
    internal const string SweepFaceGenFindings =
        " {0} of the {1} finding(s) this sweep found were listed: the listing budget (limit={2}) ran out.";

    [NoClaims("an address, not a sentence; the claims about the facegen family are the boundary's own")]
    internal const string FaceGenDocUrl = "https://github.com/Avick3110/houseCARL/blob/main/docs/facegen.md";

    [NoClaims("an address, not a sentence; the claims about the dialogue family are the boundary's own")]
    internal const string DialogueDocUrl = "https://github.com/Avick3110/houseCARL/blob/main/docs/dialogue.md";

    [MustState("provenance", "never the render", "purple or white face", "player-only grey", "brown weight face")]
    internal const string SweepFaceGenBoundary =
        "reports provenance — which mod wins each half of the bake and which plugin wins the record — and never " +
        "the render: it cannot read a .dds's pixels, cannot bake geometry (that is the Creation Kit's Ctrl+F4), " +
        "and a clean row is not a promise the face looks right. NOT this family: a purple or white face (a missing " +
        "texture), player-only grey (RaceMenu/SKEE runtime state), a brown weight face (save-baked weight), or an " +
        "appearance distributed at runtime by SPID. See " + FaceGenDocUrl + " for the causes behind each class.";

    /// <summary>Which families this response answers for, composed from the outcome rather than the
    /// selection.</summary>
    [MustState("findings=", "the default family only")]
    internal const string SweepFamiliesDefaulted =
        "findings= was not given, so this sweep ran the default family only: {0}.";

    [MustState("findings=", "answers for")]
    internal const string SweepFamiliesChosen =
        "findings= selected, and this response answers for: {0}.";

    [MustState("findings=", "every findings family")]
    internal const string SweepFamiliesAll =
        "findings= ran every findings family this surface registers: {0}.";

    /// <summary>No family answered: every family selected refused, and for different reasons, so the response is a
    /// document of refusal sections rather than one error string.</summary>
    [MustState("findings=", "NO family", "refused")]
    internal const string SweepFamiliesNoneAnswered =
        "findings= answered for NO family: every family this call selected refused, and each states its own ground " +
        "in its own section below.";

    [MustState("did NOT answer for", "absent rather than clean")]
    internal const string SweepFamiliesRefused =
        " It did NOT answer for: {0} — that family's own section states why, and its findings are absent rather " +
        "than clean.";

    [MustState("did NOT run", "ask for it with")]
    internal const string SweepFamiliesAbsent =
        " It did NOT run: {0} — ask for it with the findings= spelling named beside each.";

    [NoClaims("a list item; the claims are the family description it quotes and the spelling it prints")]
    internal const string SweepFamilyNotRun = "{0} ({1})";

    [MustState("swept OFF-ORDER", "not in the active load order")]
    internal const string SweepOffOrderScanned =
        "swept OFF-ORDER (on disk, not in the active load order): {0}   [{1}]";

    [MustState("the file's own records", "links resolved against the active order")]
    internal const string SweepOffOrderErrorsCoverage =
        "the file's own records; links resolved against the active order + the file's own definitions";

    [MustState("the file's own records", ".pex read from the ACTIVE order", "UNVERIFIABLE, not clean")]
    internal const string SweepOffOrderScriptsCoverage =
        "the file's own records; each attached script's .pex read from the ACTIVE order, so a script that ships " +
        "only inside the not-yet-enabled mod reads UNVERIFIABLE, not clean";

    /// <summary>How many further records carried an unverifiable note already reported for the same script
    /// class.</summary>
    [MustState("collapsed", "already reported")]
    internal const string SweepScriptUnverifiableCollapsed =
        "unverifiable notes: {0} further record(s) carry a note already reported for the same script class — " +
        "collapsed, so the listing is not a wall of one sentence. The unverifiable total above counts them all.";

    [NoClaims("a label; the claim it introduces is the named family's own boundary")]
    internal const string SweepBoundaryLabelFor = "boundary ({0}): ";

    [NoClaims("a title; the response's claims are its families' own")]
    internal const string SweepMergedTitle = "check — derived-findings sweep";

    [NoClaims("a section label; the claims below it are the family's own")]
    internal const string SweepFamilySectionHead = "[{0}] {1}";

    // ---- the dialogue family ----

    [MustState("seeds=", "will NOT sweep the whole load order", "82,343")]
    internal const string DialogueNeedsSeeds =
        "findings=[\"dialogue\"] needs seeds=. This family validates the topics and quests you NAME, and it will " +
        "NOT sweep the whole load order — that is a declared cost bound, not a missing feature: a whole-order pass " +
        "is a per-topic graph walk across every plugin that touches each topic, and the order this bound was " +
        "measured on carries 82,343 dialogue topics (one quest's 235 owned topics alone took 13.6 s). " +
        "Name what to validate: seeds=[\"XXXXXX:Plugin.esp\"] takes a dialogue topic (DIAL), a quest (QUST) — which " +
        "expands to every topic that quest owns — a dialogue view (DLVW), or a dialogue branch (DLBR).";

    [MustState("validated NOTHING", "ACTIVE load order")]
    internal const string DialogueNoSeedResolved =
        "findings=[\"dialogue\"] validated NOTHING: not one of the {0} seed(s) named resolved. {1} A seed is a DIAL, " +
        "QUST, DLVW or DLBR FormID spelled 'XXXXXX:Plugin.esp' and is resolved against the ACTIVE load order{2}.";

    [NoClaims("a tail clause of DialogueNoSeedResolved, which carries the claim; this only says which world it searched")]
    internal const string DialogueNoSeedResolvedPlain =
        " — a record only a disabled plugin defines is not reachable here (source= folds one such plugin in)";

    [NoClaims("the same tail clause under a fold; DialogueNoSeedResolved carries the claim and the fold frame names the file")]
    internal const string DialogueNoSeedResolvedFolded =
        " PLUS the folded copy named above — a record neither of those carries is not reachable here";

    [MustState("NOT validated")]
    internal const string DialogueSeedRefused = "  [X] {0} — NOT validated: {1}\n";

    /// <summary>Stated in this family's own section beside its counts.</summary>
    [MustState("seeded, not swept", "do NOT scope it", "off-order lane is source=")]
    internal const string DialogueScopeNote =
        "scope: the dialogue family is seeded, not swept — plugins=, type=, formids=, editorid_contains= and " +
        "exclude= scope the sweep families and do NOT scope it. {0} Its off-order lane is source=: one plugin that " +
        "is NOT in the active order, folded in where MO2 would load it — LAST for a regular plugin, after the last " +
        "master for a .esm/.esl or an ESM-flagged one, and in that plugin's OWN slot when the order already " +
        "carries the filename — and every seed is then validated against it.";

    /// <summary>Printed once at the top of the family's section.</summary>
    [MustState("folded", "NOT active", "enable it and re-run")]
    internal const string DialogueFolded =
        "folded: '{0}' is NOT active — {1} — and is {2}. Every " +
        "finding below was read against the active order's winners PLUS that file, so it is what the check WOULD " +
        "say once the file is enabled: enable it and re-run for the live answer. Its own .fuz/.pex/.seq files are " +
        "resolved through the VFS, which serves only the mod folders MO2 has enabled, so a file shipped beside it " +
        "in a folder that is off reads as absent here.\n";

    [MustState("does NOT drop", "only the active copy")]
    internal const string DialogueFoldedShadowBound =
        "  bound: the folded copy is read AT that slot for the records it carries; this projection does NOT drop " +
        "the records only the active copy holds, which enabling the folded mod folder WOULD (it replaces the whole " +
        "file). A record the folded copy leaves out still reads from the active one here.\n";

    [MustState("seed(s) given in seeds=", "reached")]
    internal const string DialogueScopeAllSeeds = "It reached all {0} seed(s) given in seeds=.";

    /// <summary>…and when it did not. The knob is named so a caller reading a short answer knows which parameter
    /// moves it; the accounting states the same cut off the same computation.</summary>
    [MustState("seed(s) given in seeds=", "limit=", "reached")]
    internal const string DialogueScopeSomeSeeds =
        "It reached {0} of the {1} seed(s) given in seeds= — limit= stopped it there.";

    /// <summary>The dialogue family's honest boundary.</summary>
    [MustState("does NOT mean the dialogue will play as intended", "cannot EVALUATE",
               "records project=info_order")]
    internal const string DialogueBoundary =
        "validates the dialogue graph at the data layer — quest and branch wiring, LinkTo and previous-link targets " +
        "(an EMPTY previous-link is the vanilla norm and is never flagged), each voiced line's .fuz on disk, each " +
        "result script bound and compiled, the CK-parity subrecords, and a subset of MALFORMED conditions. It " +
        "cannot EVALUATE whether a WELL-FORMED condition passes — only the running game can{0} — and it does not " +
        "check lip-sync or audio content, so a clean pass here does NOT mean the dialogue will play as intended. " +
        "The per-line checks audit the WINNING topic's INFO list only. The effective merged INFO order — which line " +
        "the game reaches FIRST — is not a finding and is not here: ask records project=info_order for it.{1}";

    [MustState("record-level CK-parity check only", "no dialogue graph", "Validate the owning topics")]
    internal const string DialogueBoundaryRecordLevel =
        "validates the CK-parity subrecords the Creation Kit always writes on the record you named, and nothing " +
        "else: every seed this call reached was a dialogue view (DLVW) or branch (DLBR), which own no INFO list, " +
        "so this is a record-level CK-parity check only — no dialogue graph, voice file, result script or " +
        "condition was checked here. Validate the owning topics (DIAL) or quest (QUST) for those.{0}{1}";

    /// <summary>The scope note under one view/branch seed in a response that also carries seeds owning an INFO
    /// list.</summary>
    [MustState("record-level CK-parity check only", "Validate the owning topics")]
    internal const string DialogueRecordLevelScope =
        "scope: this is a record-level CK-parity check only — it does not validate any dialogue graph, voice, " +
        "script, or condition surface. Validate the owning topics (DIAL) or quest (QUST) for those.";

    [MustState("CK-parity: OK", "DNAM and ENAM")]
    internal const string DialogueViewParityOk =
        "  CK-parity: OK — the DNAM and ENAM byte subrecords the Creation Kit always writes are both present.\n";

    [MustState("CK-parity: OK", "TNAM (Category) and DNAM (Flags)")]
    internal const string DialogueBranchParityOk =
        "  CK-parity: OK — the TNAM (Category) and DNAM (Flags) subrecords the Creation Kit always writes are " +
        "both present.\n";

    /// <summary>The conditioned-line clause of the boundary above, present only where there are conditioned lines to
    /// count.</summary>
    [NoClaims("a clause of DialogueBoundary; the claim is that sentence's cannot-EVALUATE")]
    internal const string DialogueConditioned =
        " — {0} line(s) here carry conditions, checked for malformedness but not evaluated";

    [MustState("may merely be unscanned")]
    internal const string DialogueReadIncomplete =
        " A BSA or a loose mod folder failed to read this build, so an \"absent\" voice file or .pex above may merely be unscanned — " +
        "any loose folder that failed is named among this response's root read failures, and any archive that failed is " +
        "named by " + ToolNames.AssetStatus + ".";

    [MustState("every one of the")]
    internal const string SweepDialogueAllVisible =
        " every one of the {0} topic(s) these seeds own is listed.";

    [MustState("max_chars")]
    internal const string SweepDialogueVisible =
        " {0} of the {1} topic(s) these seeds own are listed; the rest did not fit this response's max_chars.";

    [MustState("limit=", "were NOT reached")]
    internal const string SweepDialogueSeedsCut =
        " {0} of the {1} seed(s) named were reached; {2} were NOT reached because the seed budget (limit={3}) " +
        "ran out.";

    [MustState("True totals")]
    internal const string SweepDialogueProblems =
        " True totals: {0} finding(s) across {1} topic(s).";

    [MustState("could not be validated")]
    internal const string SweepDialogueRefusalsCut =
        " {0} of the {1} seed(s) that could not be validated are named above.";

    [NoClaims("a seed head; the claims are the findings under it and this family's boundary")]
    internal const string DialogueSeedHead = "seed {0} — {1} {2}, winner {3}, {4} topic(s)\n";

    [MustState("owns NO dialogue topics")]
    internal const string DialogueSeedNoTopics =
        "  this quest owns NO dialogue topics in the active load order — nothing to validate. If you expected some, " +
        "check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n";

    [MustState("could read", "not the whole load order")]
    internal const string DialogueSeedNoTopicsRead =
        "  no dialogue topics of this quest were found in the plugins this sweep could read — that is not the whole " +
        "load order: the coverage gap below names the plugin(s) left out, and any topic they own is missing here.\n";

    /// <summary>A plugin the fan-out could not read, printed as what bounds the report rather than as a finding
    /// against the quest.</summary>
    [NoClaims("a label around the core's own gap sentence; that sentence carries the claim")]
    internal const string DialogueSeedScanGap = "  coverage gap: {0}\n";

    [MustState("quest CK-parity: OK", "NextAliasID (ANAM)", "Flags (FNAM)")]
    internal const string DialogueQuestParityOk =
        "  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its " +
        "Flags (FNAM).\n";

    /// <summary>The dialogue family's counts line, above everything a budget can refuse, so a caller whose topic
    /// blocks were all cut still learns the totals.</summary>
    [MustState("finding(s) across", "reached were validated")]
    internal const string DialogueCounts =
        "{0} of the {1} seed(s) reached were validated, {2} topic(s), {3} finding(s) across them.\n";

    [MustState("epoch=", "does not cover")]
    internal const string DialogueEpochBound =
        "epoch={0}{1} — the record build these verdicts were read from; it does not cover: {2}.\n";

    [MustState("epoch=", "covers every verdict")]
    internal const string DialogueEpochWhole =
        "epoch={0}{1} — the record build these verdicts were read from; it covers every verdict here.\n";

    [MustState(".fuz")]
    internal const string DialogueUncoveredVoice = "voiced lines (.fuz on disk)";

    [MustState(".pex")]
    internal const string DialogueUncoveredScripts = "result-script .pex chain";

    [MustState(".seq")]
    internal const string DialogueUncoveredSeq = ".seq coverage and staleness";

    /// <summary>What <c>counts_only=true</c> leaves out for this family, stated where the listing would have been:
    /// a mode that renders no blocks must say so rather than look like a validation that found nothing.</summary>
    [MustState("counts_only=true", "no per-topic blocks")]
    internal const string DialogueCountsOnly =
        "counts_only=true: the totals above and the unreachable seeds below, and no per-topic blocks. Drop " +
        "counts_only= to see each topic's findings.\n";

    /// <summary>How many source plugins the roster names before it says how many it did not.</summary>
    internal const int SweepRosterRows = 10;
}

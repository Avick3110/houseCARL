using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// One source per sentence for the read surface's user-facing prose, the <see cref="WriteSentences"/> pattern on
/// the other surface. A sentence born as a render literal gets copied into the other transport and then drifts,
/// so they live here, each declaring the phrases whose loss would change what the caller is told
/// (<see cref="MustStateAttribute"/>).
///
/// <para>The owned-child consts come in two tiers and two shapes; the background is in
/// `docs/architecture/records-owned-child-declarers.md`. A response-level clause is stated once per response
/// rather than per field, because carried per field it is ~275 identical chars on every row and crowds out real
/// rows. Such a clause NAMES the fields it is about rather than pointing at them, so no ordering or medium can
/// falsify it, and a caller states it only over fields that medium actually emitted. Fitting and stating are
/// independent: <see cref="ClauseReserve"/> decides the clause fits, emission decides it is said.</para>
/// </summary>
internal static class ReadSentences
{
    // ---- what the game assembles: the additive union across every touching plugin --------------------

    /// <summary>The word every per-field owned-child note carries, whichever shape it is in. It is what a caller
    /// (and a test) can look for to know the field's content is assembled from more than the body it read.</summary>
    [NoClaims("a word shared by the notes below; each states its own claim")]
    internal const string ChildContent = "child record";

    /// <summary>The per-field label for the MANY-child shape. The value beside it is the body's OWN list; this is
    /// the whole set, so the two must never read as the same quantity.</summary>
    [NoClaims("a label; the claim — what a union is and why it differs from the value — is UnionFraming's")]
    internal const string UnionLabel = "additive union";

    /// <summary>The negative, stated rather than omitted: nobody touching this record declares children here. Its
    /// absence would read as the union never having been assembled.</summary>
    [MustState("no plugin", "declares child record")]
    internal const string NoUnionMembers = UnionLabel + ": no plugin touching this record declares " + ChildContent + "s here";

    /// <summary>The response-level clause. It must say that the value and the union are DIFFERENT quantities, or a
    /// reader takes the union for the field's contents and a write addressed by index lands on the wrong child.
    /// <c>{0}</c> is filled with the fields the response actually annotated (<see cref="FieldList"/>).</summary>
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

    /// <summary>The per-field line on a lane that did not assemble the union. It may claim only what the index
    /// knows: other plugins touch this record and this read did not look at what they declare — never that they
    /// do or do not.</summary>
    [MustState("were not read")]
    internal const string NotRead = "other plugin(s) touch this record; their declarations for this " + ChildContent + " field were not read";

    /// <summary>The response-level half of the index-only tier, naming the lane that assembles the union. The
    /// remedy must name the tool and the form, not a bare parameter: this clause ships from every scan lane that
    /// renders record fields, including artifact manifests, and a spelling the recipient's surface rejects is a
    /// remedy nobody can run. <c>{0}</c> is filled with the fields the response actually annotated.</summary>
    [MustState("declared per plugin", "were not read", ToolNames.Records)]
    internal const string NotReadFraming =
        "note: this response annotates field(s) that hold CHILD RECORDS ({0}). Child records are declared per " +
        "plugin and the game assembles the parent's from every plugin that declares any, so one body's list is " +
        "not the whole content. A scan answers many rows, so it did not open the other plugins' bodies and their " +
        "declarations were not read. For the " + UnionLabel + " on a record you name: " +
        ToolNames.Records + " with formids=, which states it on every child-bearing field it emits.";

    /// <summary>The index-only tier's per-field line: the count the index knows, and the honest limit.</summary>
    internal static string NotReadNote(int others) => $"{others} {NotRead}";

    /// <summary>The response-level clause for whichever tier the response actually stated. One door, so a lane
    /// cannot state a union clause over index-only notes or the reverse.</summary>
    internal static string OwnedChildClause(IReadOnlyCollection<string> fields, bool unioned) =>
        string.Format(unioned ? UnionFraming : NotReadFraming, FieldList(fields));

    /// <summary>The clause framing a tier uses, for the reserve and for a test that has to find the clause line.</summary>
    internal static string ClauseFraming(bool unioned) => unioned ? UnionFraming : NotReadFraming;

    /// <summary>How many contributing plugins a union names before it summarises the rest as a count.</summary>
    internal const int UnionDeclarerCap = 3;

    /// <summary>The per-field line: what the game assembles here, who contributes it, and how much of it this
    /// body's own list carries. A SINGULAR child is not a union — its declarers override one record — so it says
    /// which plugin's copy is live instead of adding counts that would be a fiction.</summary>
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
                 + $"; this body's own list carries {u.OwnCount}";
        return u.Unreadable.Count == 0 ? head
            : head + $"; {u.Unreadable.Count} plugin(s) {CouldNotRead} "
              + $"({string.Join(", ", u.Unreadable.Take(UnionDeclarerCap))}"
              + (u.Unreadable.Count > UnionDeclarerCap ? ", …" : "") + ")";
    }

    // ---- the precise tier: WHICH providers declare, off bodies the tree has already fetched ----------

    /// <summary>The per-field label for a collection child; its meaning is stated once by
    /// <see cref="DeclarersLead"/>. Never "also declared by" — that is false when the winner carries none and a
    /// base plugin carries them all, which is the case this exists for.</summary>
    [NoClaims("a label; the claim is the plugin names it introduces, and their meaning is DeclarersLead")]
    internal const string DeclaredBy = "declared by";

    /// <summary>The per-field label for a singular child: a count, not a name list, since hundreds of overriders
    /// is noise.</summary>
    [NoClaims("a label; the claim is DeclarersLead's, and the count is evidence rather than an assertion")]
    internal const string CarriedBy = "carried by";

    /// <summary>A provider whose body or field could not be read. Stated beside <see cref="NoDeclarers"/> and
    /// never absorbed into it, which would misreport "could not look" as "nothing there".</summary>
    [MustState("could NOT be read")]
    internal const string CouldNotRead = "could NOT be read";

    /// <summary>The half no cheap tier can state: nobody declares here. It claims only over bodies actually read —
    /// an unreadable one belongs to <see cref="CouldNotRead"/> — and is never omitted, or its absence would read
    /// as the tier not having run.</summary>
    [MustState("none of the provider bodies read", "declares child records")]
    internal const string NoDeclarers = "none of the provider bodies read declares child records in this field";

    /// <summary>The block's one framing line, saying what the per-field lines below it mean — once per record, not
    /// once per field. Both shapes are named here rather than repeated on every line, which on a record with
    /// several child-bearing fields would be ~180 chars of framing each.</summary>
    [MustState("declared per plugin", "assembled by the game", "override")]
    internal const string DeclarersLead =
        "child records — declared per plugin, read off the provider bodies this tree already fetched. A MANY-child " +
        "field (\"" + DeclaredBy + " …\") is assembled by the game from every plugin that declares any; a ONE-child " +
        "field (\"" + CarriedBy + " N\") is ONE record those providers override, resolved by load order:";

    /// <summary>Every row after the one carrying <see cref="DeclarersLead"/> gets this short label instead. Text
    /// has no per-row structure to hang the block on the way json's <c>child_declarers</c> key does, so without it
    /// the block sits flush against the toucher list above with nothing saying what it is.</summary>
    [NoClaims("a label; the claim — what the two shapes mean — is DeclarersLead's, stated once already")]
    internal const string DeclarersHeader = "child records — declared per plugin (see above for what the shapes mean):";

    /// <summary>How many declaring plugins a collection field names before it summarises the rest as a count. It
    /// rides every child-bearing field of every row, so the cap is small.</summary>
    internal const int DeclarerNameCap = 3;

    /// <summary>Points a text reader at the medium carrying the names <see cref="DeclarerNameCap"/> elided; json's
    /// <c>declaring</c> array is never capped. Text-only, so the text caller appends it — baking it into
    /// <see cref="DeclarersNote"/> would put the hint in the media that already answer it.</summary>
    [NoClaims("a remedy fragment; the caller appends it, never DeclarersNote")]
    internal const string DeclarersOverflowRemedy = " — format=json for the full list";

    /// <summary>The precise tier's per-field line, in the voice of the field's shape. It always returns a
    /// sentence, never null: a collection field's empty answer is <see cref="NoDeclarers"/>, and a singular
    /// field's stays in its own voice rather than borrowing the collection wording's plural. It never appends
    /// <see cref="DeclarersOverflowRemedy"/>, which only a text caller needs.</summary>
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
    /// than written out in prose: the child-bearing set grows on a Mutagen bump with no edit here, and a
    /// hand-written list would silently describe the wrong fields. The joined list is cut at
    /// <see cref="ClauseFieldsMaxChars"/> so a clause's worst-case length is constant, which is what lets
    /// <see cref="ClauseReserve"/> subtract it from <c>max_chars</c> before the body renders.</summary>
    internal static string FieldList(IReadOnlyCollection<string> fields)
    {
        var joined = string.Join(", ", fields);
        return joined.Length <= ClauseFieldsMaxChars ? joined : joined[..ClauseFieldsMaxChars].TrimEnd(',', ' ') + ", …";
    }

    /// <summary>The char budget the derived field list gets inside a clause. Small on purpose: the whole
    /// child-bearing set is a handful of names.</summary>
    internal const int ClauseFieldsMaxChars = 120;

    /// <summary>Slack over a framing's own length: the "{0}" it loses (3), the ", …" an over-long list gains (3),
    /// and the newlines the text lane wraps each clause in (2).</summary>
    const int ClauseGlue = 8;

    /// <summary>The worst-case chars the response-level clause can cost, reserved out of <c>max_chars</c> before
    /// the body renders. The clause is load-bearing, so it cannot be dropped at the cap; appending it past the cap
    /// instead would overrun invisibly to the <c>truncated</c> flag the auto-spill trigger reads.</summary>
    internal static int ClauseReserve(bool mayState) =>
        // The longer of the two tiers: the reserve is taken before the fields render, and which tier the response
        // will state is not settled until one of them is emitted.
        mayState ? Math.Max(UnionFraming.Length, NotReadFraming.Length) + ClauseFieldsMaxChars + ClauseGlue : 0;

    // ---- the sweep response's omission accounting ----
    //
    // The prose half of CheckAccounting; the arithmetic is there, and every number below arrives already computed
    // from what the render emitted. They live here so the content net that walks this class's consts covers them.

    /// <summary>The accounting's own framing. It is not about any subject, so it must be stated outside every
    /// subject gate, or a lane with no listing emits clauses with no opener.</summary>
    [NoClaims("a label; the claims it introduces are the accounting's own clauses")]
    internal const string SweepAccountingLead = "[accounting:";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepAllVisible =
        " all {0} dangling ref(s) found by this sweep appear above.";

    /// <summary>The lead on a response that is missing findings: one number for the question a caller has — how
    /// many of these can I see — rather than two the reader has to combine.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepVisible =
        " {0} of the {1} dangling ref(s) found by this sweep appear above.";

    /// <summary>The listing budget's share of what is missing. Stated only when its own count is non-zero: a
    /// response cut by max_chars alone must not report the budget dropping 0, which reads as the budget being
    /// involved when it was not.</summary>
    [MustState("limit=")]
    internal const string SweepOmittedByBudget = " {0} were never listed: the listing budget (limit={1}) ran out";

    /// <summary>The response cut's share. Its subject is this response and it names max_chars, the knob that
    /// moves it: one computation, but each cause named by the layer that caused it.</summary>
    [MustState("max_chars=")]
    internal const string SweepOmittedByCut = " {0} did not fit this response (max_chars={1})";

    /// <summary>How many plugin sections reached the response. Answers what the entry count cannot: a plugin whose
    /// whole set is missing has no section to read the loss off, so the section count is what tells a reader the
    /// listing is a window rather than the whole order.</summary>
    [MustState("plugin section(s)")]
    internal const string SweepSections = " {0} of {1} plugin section(s) were rendered.";

    /// <summary>The dialogue family's own version, in its units. It cannot borrow the sentence above: that family
    /// never looks at a plugin, so "plugin section(s)" would be the wrong unit.</summary>
    [MustState("seed section(s)")]
    internal const string SweepDialogueSeedSections = " {0} of {1} seed section(s) were rendered.";

    /// <summary>The excluded-plugin roster's own cut. Its rows are the plugins houseCARL could not parse at all,
    /// so losing them silently hides the honesty layer rather than a finding.</summary>
    [MustState("could not be parsed")]
    internal const string SweepExcludedCut = " {0} of {1} plugin(s) that could not be parsed are named above.";

    /// <summary>The counts_only lane's unread rows, same rule as the excluded roster.</summary>
    [MustState("could not be read")]
    internal const string SweepUnreadCut = " {0} of {1} plugin(s) whose records could not be read are named above.";

    /// <summary>The by-source roster: which plugins are missing entries, and how many each. It is the response's
    /// roster, not the budget's, so a plugin whose entries the budget listed and the response cut belongs
    /// here.</summary>
    [NoClaims("a label introducing the roster rows; every claim it carries is in the rows and the clauses around it")]
    internal const string SweepRosterLead = " Missing here, by source plugin: ";

    /// <summary>The roster's own truncation. A roster that silently stops rebuilds the hole this accounting
    /// closes one level down, so the count it did not name is stated.</summary>
    [MustState("not named here")]
    internal const string SweepRosterCut = " (the {0} largest of {1}; the rest are not named here)";

    /// <summary>A rule about the listing rather than a claim about this response's contents, stated wherever a
    /// roster is: it tells the reader that a plugin with no section of its own is still in that roster.</summary>
    [MustState("no section of its own")]
    internal const string SweepNoSectionRule =
        " A plugin whose whole set is missing here, with nothing else to report, gets no section of its own.";

    // The remedy is assembled from the causes that actually fired. A knob named beside a cause it did not move is
    // a remedy the caller can follow and land in the same place.

    /// <summary>Offered only where the listing budget actually dropped something.</summary>
    [MustState("limit=")]
    internal const string SweepRemedyLimit = " Raise limit= to list more.";

    /// <summary>Offered only where this response could not fit what the budget admitted.</summary>
    [MustState("max_chars=")]
    internal const string SweepRemedyMaxChars = " Raise max_chars= to fit more of what was found.";

    /// <summary>The scoping clause. It names both bounds: re-spending the listing budget on one plugin says
    /// nothing about whether the response can carry the result, so max_chars still applies.</summary>
    [MustState("plugins=", "limit=", "max_chars=")]
    internal const string SweepRemedyScope =
        " Scoping plugins= to one of these re-spends the whole listing budget on that plugin; whether you then see " +
        "its set in full depends on limit= and on max_chars=, which both still apply.";

    /// <summary>Offered wherever a by-source roster exists, since that is the tally it points at.</summary>
    [MustState("counts_only=true")]
    internal const string SweepRemedyCountsOnly =
        " counts_only=true returns the by-source tally for every plugin, capped only in how many ROWS it prints.";

    /// <summary>The bracket that closes the line, remedies or not.</summary>
    [NoClaims("punctuation closing the accounting line")]
    internal const string SweepClose = "]";

    /// <summary>The lead both overrun sentences share, enumerating what a response carries whatever the budget
    /// says. One constant, because the two sentences differ only in what happened next and a second spelling
    /// drifts. The closing-line member is conditional ("for anything it cut short") since the listing lane owes no
    /// closing line and must not be told it carries one.</summary>
    [MustState("its header", "the accounting above", "cut short", "the boundary")]
    internal const string SweepFixedPartLead =
        " This response is {2} chars, longer than the max_chars={0} it was given: what it must carry whatever the " +
        "budget — its header, the accounting above, the closing line for anything it cut short, the boundary — ";

    /// <summary>The one arm where the response may exceed max_chars, and it says so: a cap smaller than the
    /// accounting itself leaves no honest response, so the accounting ships and the overrun is named with the
    /// number that clears it. The condition is measured against the rendered length, never predicted from the
    /// reserve, which is sized for a worst case this response may not be.</summary>
    [MustState("max_chars=", "raise it to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapTooSmall =
        SweepFixedPartLead + "does not fit in that many chars, so raise it to at least {1}.";

    /// <summary>The other way a response ends up over its cap: a body unit whose size cannot be known before it is
    /// written lands past what the budget had left, while the fixed part itself fits. It is a separate sentence
    /// because the one above would send the caller chasing a cap that is not the problem. The discriminator is
    /// <c>needed &gt; max_chars</c>, and both spellings end in the same remedy clause.</summary>
    [MustState("max_chars=", "raise it to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapOvershot =
        SweepFixedPartLead + "does fit, but one body unit was written before its size could be measured and ran " +
        "past what was left, so raise it to at least {1}.";

    /// <summary>The sweep's honest scope boundary, stated to both transports from here so the two cannot drift.
    /// The label is separate because only text wants it: json carries the same claim under a key already called
    /// <c>boundary</c>.</summary>
    [MustState("Does NOT verify navmesh/terrain", "required-but-null", "unused-master cleanup", "legal optional")]
    internal const string SweepBoundary =
        "checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain spatial " +
        "integrity (CRC/grid), flag required-but-null fields, list unused-master cleanup, or link-check an owned " +
        "item's ownership 'variable' word (a rank/global Mutagen can't type on an override); a null FormLink is a " +
        "legal optional.";

    /// <summary>The unbound total, spelled so it can never claim a class nobody checked. One definition, used by
    /// the header and by the accounting's listing-budget clause, so the two cannot disagree. It carries the
    /// <c>property_contains=</c> label when one is in force, since that filter narrows this count and not the
    /// sweep's others.</summary>
    internal static string ScriptUnboundTotal(ScriptCheckResult r, bool didObject, bool didScalar)
        => !didObject && !didScalar ? SweepScriptUnboundNotChecked
         : didObject && didScalar   ? $"{r.TotalUnbound} unbound{ScriptPropLabel(r)}"
         : didObject                ? $"{r.TotalUnboundObject} unbound{ScriptPropLabel(r)}{SweepScriptObjectOnly}"
                                    : $"{r.TotalUnboundScalar} unbound{ScriptPropLabel(r)}{SweepScriptScalarOnly}";

    /// <summary>The bound-but-null total, same contract as <see cref="ScriptUnboundTotal"/>.</summary>
    internal static string ScriptNullTotal(ScriptCheckResult r, bool didNull)
        => didNull ? $"{r.TotalNullObject} bound-but-null{ScriptPropLabel(r)}" : SweepScriptNullNotChecked;

    /// <summary>Both totals as the accounting restates them, in one string — so the clause that names the listing
    /// budget also says what the true totals are, per class, right where the cut is reported.</summary>
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

    /// <summary>One unbound class excluded: the number is real, and says which half it is not about.</summary>
    [MustState("NOT CHECKED", "unbound_scalar")]
    internal const string SweepScriptObjectOnly = " (object only — unbound_scalar NOT CHECKED)";

    /// <summary>The other half, same rule.</summary>
    [MustState("NOT CHECKED", "unbound_object")]
    internal const string SweepScriptScalarOnly = " (scalar only — unbound_object NOT CHECKED)";

    /// <summary>The advisory class excluded, same rule again.</summary>
    [MustState("NOT CHECKED", "bound_null")]
    internal const string SweepScriptNullNotChecked = "bound-but-null NOT CHECKED (findings= excluded 'bound_null')";

    /// <summary>The scripts family's lead when its listing is whole. Without it, silence would mean both "this
    /// response carries everything" and "something was dropped", which must never read alike.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptAllVisible =
        " all {0} record section(s) found by this sweep appear above.";

    /// <summary>The scripts family's lead when it is not. A record section and a plugin section are different
    /// units, so this is its own sentence rather than <see cref="SweepVisible"/> reworded.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptVisible =
        " {0} of the {1} record section(s) found by this sweep appear above.";

    /// <summary>The scripts family's listing budget, decomposed the way the errors family's is: a subtraction
    /// against the sweep's own totals rather than a bare "capped" flag. It restates the true totals per class from
    /// <see cref="ScriptTotals"/>, because a <c>findings=</c> filter narrows the population this clause counts and
    /// a bare count would read as a claim about classes nobody looked for.</summary>
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

    /// <summary>Which families this response answers for, composed from the outcome rather than the selection.
    /// With <c>findings=</c> omitted the sweep runs the errors family alone — an unscoped scripts sweep takes
    /// minutes and an unscoped dialogue sweep is refused outright — so the narrowed default has to be stated
    /// rather than silent. It is one complete sentence stated whole by both transports, not a lead each finishes
    /// its own way; the clauses below are separate consts because each appears only when its list is
    /// non-empty.</summary>
    [MustState("findings=", "the default family only")]
    internal const string SweepFamiliesDefaulted =
        "findings= was not given, so this sweep ran the default family only: {0}.";

    /// <summary>The same fact when the caller did choose: a default and a selection are different sentences, and
    /// one wording for both tells the second caller something false. It says what the response answers for, which
    /// is not the same list as what was selected.</summary>
    [MustState("findings=", "answers for")]
    internal const string SweepFamiliesChosen =
        "findings= selected, and this response answers for: {0}.";

    /// <summary>Every registered family ran and answered, so there is nothing to name as absent or refused — and
    /// it says so rather than going quiet, which would read like a dropped sentence. Chosen off what came back,
    /// never off the selection: a family that only refused must not land here.</summary>
    [MustState("findings=", "every findings family")]
    internal const string SweepFamiliesAll =
        "findings= ran every findings family this surface registers: {0}.";

    /// <summary>No family answered: every family selected refused, and for different reasons, so the response is a
    /// document of refusal sections rather than one error string. Said outright rather than left as a lead with an
    /// empty list after it.</summary>
    [MustState("findings=", "NO family", "refused")]
    internal const string SweepFamiliesNoneAnswered =
        "findings= answered for NO family: every family this call selected refused, and each states its own ground " +
        "in its own section below.";

    /// <summary>The families that were selected and refused. Their findings are absent, not clean: a caller
    /// reading only the lead would otherwise take a family named there as one that looked.</summary>
    [MustState("did NOT answer for", "absent rather than clean")]
    internal const string SweepFamiliesRefused =
        " It did NOT answer for: {0} — that family's own section states why, and its findings are absent rather " +
        "than clean.";

    /// <summary>The registered families this call never asked for, with the spelling that adds each.</summary>
    [MustState("did NOT run", "ask for it with")]
    internal const string SweepFamiliesAbsent =
        " It did NOT run: {0} — ask for it with the findings= spelling named beside each.";

    /// <summary>One entry in the did-NOT-run list: what asking for it would buy, and the exact spelling that asks.
    /// A remedy that names a knob without spelling it is one the caller has to guess at.</summary>
    [NoClaims("a list item; the claims are the family description it quotes and the spelling it prints")]
    internal const string SweepFamilyNotRun = "{0} ({1})";

    /// <summary>The off-order roster a swept family prints: which files it swept from disk rather than from the
    /// active order, and — in the bracketed tail — what that sweep did and did not read for them. One source,
    /// because the coverage tail is the caveat that makes the lane's verdict readable and a second copy of it is
    /// how the two families come to claim different things about the same lane.</summary>
    [MustState("swept OFF-ORDER", "not in the active load order")]
    internal const string SweepOffOrderScanned =
        "swept OFF-ORDER (on disk, not in the active load order): {0}   [{1}]";

    /// <summary>The errors family's coverage tail for <see cref="SweepOffOrderScanned"/>.</summary>
    [MustState("the file's own records", "links resolved against the active order")]
    internal const string SweepOffOrderErrorsCoverage =
        "the file's own records; links resolved against the active order + the file's own definitions";

    /// <summary>The scripts family's coverage tail: the <c>.pex</c> chain comes from the ACTIVE order, so a script
    /// shipping only inside the not-yet-enabled mod is outside it and lands in the unverifiable count. Stated to
    /// both transports, because that count is unreadable without it.</summary>
    [MustState("the file's own records", ".pex read from the ACTIVE order", "UNVERIFIABLE, not clean")]
    internal const string SweepOffOrderScriptsCoverage =
        "the file's own records; each attached script's .pex read from the ACTIVE order, so a script that ships " +
        "only inside the not-yet-enabled mod reads UNVERIFIABLE, not clean";

    /// <summary>How many further records carried an unverifiable note already reported for the same script class.
    /// Unverifiable notes are outside the finding budget, and a disabled mod puts every one of its script classes
    /// out of the VFS at once — so the repeats are collapsed and the collapse is stated rather than the listing
    /// quietly filling with one sentence.</summary>
    [MustState("collapsed", "already reported")]
    internal const string SweepScriptUnverifiableCollapsed =
        "unverifiable notes: {0} further record(s) carry a note already reported for the same script class — " +
        "collapsed, so the listing is not a wall of one sentence. The unverifiable total above counts them all.";

    /// <summary>The merged response's boundary label. Each family states its own boundary and they claim different
    /// things, so the label is parameterised to name whose claim follows it.</summary>
    [NoClaims("a label; the claim it introduces is the named family's own boundary")]
    internal const string SweepBoundaryLabelFor = "boundary ({0}): ";

    /// <summary>The merged response's own title.</summary>
    [NoClaims("a title; the response's claims are its families' own")]
    internal const string SweepMergedTitle = "check — derived-findings sweep";

    /// <summary>One family's section head in a merged response: the token a caller spells in <c>findings=</c>, and
    /// the title that family's ancestor tool used for a whole response.</summary>
    [NoClaims("a section label; the claims below it are the family's own")]
    internal const string SweepFamilySectionHead = "[{0}] {1}";

    // ---- the dialogue family ----

    /// <summary>The cost refusal. The dialogue family is seeded, not swept: a call naming it with no <c>seeds=</c>
    /// has given it an empty scope, and resolving that to the whole order is the sweep this bound refuses. So it
    /// refuses, states its own behaviour, and spells the call that works, carrying the measured numbers rather
    /// than an adjective.</summary>
    [MustState("seeds=", "will NOT sweep the whole load order", "82,343")]
    internal const string DialogueNeedsSeeds =
        "findings=[\"dialogue\"] needs seeds=. This family validates the topics and quests you NAME, and it will " +
        "NOT sweep the whole load order — that is a declared cost bound, not a missing feature: a whole-order pass " +
        "is a per-topic graph walk across every plugin that touches each topic, and the order this bound was " +
        "measured on carries 82,343 dialogue topics (one quest's 235 owned topics alone took 13.6 s). " +
        "Name what to validate: seeds=[\"XXXXXX:Plugin.esp\"] takes a dialogue topic (DIAL), a quest (QUST) — which " +
        "expands to every topic that quest owns — a dialogue view (DLVW), or a dialogue branch (DLBR).";

    /// <summary>Every seed named failed to resolve, so the family validated nothing. A section of nothing under a
    /// heading would read as "looked, found none".</summary>
    [MustState("validated NOTHING", "ACTIVE load order")]
    internal const string DialogueNoSeedResolved =
        "findings=[\"dialogue\"] validated NOTHING: not one of the {0} seed(s) named resolved. {1} A seed is a DIAL, " +
        "QUST, DLVW or DLBR FormID spelled 'XXXXXX:Plugin.esp' and is resolved against the ACTIVE load order — a " +
        "record only a disabled plugin defines is not reachable here.";

    /// <summary>One unresolved seed, named with why. Carried rather than dropped: this family's scope is its seed
    /// list, so a discarded seed is a silently narrowed scope the caller reads as a clean answer.</summary>
    [MustState("NOT validated")]
    internal const string DialogueSeedRefused = "  [X] {0} — NOT validated: {1}\n";

    /// <summary>The scope asymmetry, stated in this family's own section beside its counts: the sweep families
    /// take plugin scope, this one takes seeds, so the scope parameters passed alongside it did not narrow it. It
    /// has no off-order lane either, because a seed is a record and must resolve in the active order. How many
    /// seeds it reached is a separate sentence, so a call cut short by <c>limit=</c> cannot claim
    /// completeness.</summary>
    [MustState("seeded, not swept", "do NOT scope it", "no off-order lane")]
    internal const string DialogueScopeNote =
        "scope: the dialogue family is seeded, not swept — plugins=, type=, formids=, editorid_contains= and " +
        "exclude= scope the sweep families and do NOT scope it. {0} It has no off-order lane: a seed is a record, " +
        "and it must resolve in the ACTIVE load order.";

    /// <summary>The seed count when the seed budget reached every one of them. The word is "reached", never
    /// "validated": this number counts every seed the call tried, refusals included, so "validated" would
    /// contradict the NOT validated rows below it.</summary>
    [MustState("seed(s) given in seeds=", "reached")]
    internal const string DialogueScopeAllSeeds = "It reached all {0} seed(s) given in seeds=.";

    /// <summary>…and when it did not. The knob is named so a caller reading a short answer knows which parameter
    /// moves it; the accounting states the same cut off the same computation.</summary>
    [MustState("seed(s) given in seeds=", "limit=", "reached")]
    internal const string DialogueScopeSomeSeeds =
        "It reached {0} of the {1} seed(s) given in seeds= — limit= stopped it there.";

    /// <summary>The dialogue family's honest boundary. It is a boundary rather than a body line, so it is reserved
    /// and no budget may drop it: a clean structural pass is not "this will play", and the sentence saying so has
    /// to survive. Its last clause discloses that the effective merged INFO order is not a finding and is not
    /// here, or a caller chasing "why does the wrong line play" reads a clean section as having looked.</summary>
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

    /// <summary>The boundary where no seed this call reached owns an INFO list — every one was a DLVW or DLBR, so
    /// the only check that ran is the record's own CK parity. The boundary forks because the wide sentence above
    /// asserts checks (link targets, .fuz files, result scripts, conditions) that have nothing to run against on
    /// those records, and the family's boundary is composed from what its seeds actually ran.</summary>
    [MustState("record-level CK-parity check only", "no dialogue graph", "Validate the owning topics")]
    internal const string DialogueBoundaryRecordLevel =
        "validates the CK-parity subrecords the Creation Kit always writes on the record you named, and nothing " +
        "else: every seed this call reached was a dialogue view (DLVW) or branch (DLBR), which own no INFO list, " +
        "so this is a record-level CK-parity check only — no dialogue graph, voice file, result script or " +
        "condition was checked here. Validate the owning topics (DIAL) or quest (QUST) for those.{0}{1}";

    /// <summary>The scope note under one view/branch seed in a response that also carries seeds owning an INFO
    /// list: the family boundary takes the wide arm on a mixed call, which is true of the response but not of this
    /// seed. Written without indent or trailing newline — the render pads it into the seed's body.</summary>
    [MustState("record-level CK-parity check only", "Validate the owning topics")]
    internal const string DialogueRecordLevelScope =
        "scope: this is a record-level CK-parity check only — it does not validate any dialogue graph, voice, " +
        "script, or condition surface. Validate the owning topics (DIAL) or quest (QUST) for those.";

    /// <summary>A dialogue view (DLVW) whose CK-parity subrecords are present. Stated rather than left silent: a
    /// sub-check that ran and passed is a different answer from one nobody ran, and an absence cannot tell them
    /// apart. A bare DLVW crashes the CK's Dialogue Views editor, so this is the whole verdict for that
    /// seed.</summary>
    [MustState("CK-parity: OK", "DNAM and ENAM")]
    internal const string DialogueViewParityOk =
        "  CK-parity: OK — the DNAM and ENAM byte subrecords the Creation Kit always writes are both present.\n";

    /// <summary>The same for a dialogue branch (DLBR): different subrecords, so a different sentence rather than
    /// one with the names substituted in — what the check looked at is the answer.</summary>
    [MustState("CK-parity: OK", "TNAM (Category) and DNAM (Flags)")]
    internal const string DialogueBranchParityOk =
        "  CK-parity: OK — the TNAM (Category) and DNAM (Flags) subrecords the Creation Kit always writes are " +
        "both present.\n";

    /// <summary>The conditioned-line clause of the boundary above, present only where there are conditioned lines
    /// to count. Summed over every topic the validation found, not the rendered ones: it is a note about what
    /// could not be evaluated, never a description of the listing.</summary>
    [NoClaims("a clause of DialogueBoundary; the claim is that sentence's cannot-EVALUATE")]
    internal const string DialogueConditioned =
        " — {0} line(s) here carry conditions, checked for malformedness but not evaluated";

    /// <summary>The asset-layer caveat, riding the boundary where a BSA failed to read: an "absent" .fuz or .pex
    /// above may merely be unscanned.</summary>
    [MustState("may merely be unscanned")]
    internal const string DialogueReadIncomplete =
        " A BSA failed to read this build, so an \"absent\" voice file or .pex above may merely be unscanned — see " +
        ToolNames.LoadOrderStatus + ".";

    /// <summary>The dialogue family's completeness assertion when every topic it found is in the response.</summary>
    [MustState("every one of the")]
    internal const string SweepDialogueAllVisible =
        " every one of the {0} topic(s) these seeds own is listed.";

    /// <summary>…and when the response could not carry them all.</summary>
    [MustState("max_chars")]
    internal const string SweepDialogueVisible =
        " {0} of the {1} topic(s) these seeds own are listed; the rest did not fit this response's max_chars.";

    /// <summary>The seed budget's share of what is absent: seeds the call never validated at all, a different
    /// absence from topics that did not fit, and the two must not read alike.</summary>
    [MustState("limit=", "were NOT reached")]
    internal const string SweepDialogueSeedsCut =
        " {0} of the {1} seed(s) named were reached; {2} were NOT reached because the seed budget (limit={3}) " +
        "ran out.";

    /// <summary>What the validation found, restated where the listing is short: the totals are never capped, and a
    /// short listing with no total beside it reads as the whole answer.</summary>
    [MustState("True totals")]
    internal const string SweepDialogueProblems =
        " True totals: {0} finding(s) across {1} topic(s).";

    /// <summary>The unresolved-seed roster's own cut. Its rows are seeds that could not be reached, so a silent
    /// cut there hides the boundary of the answer rather than a finding inside it.</summary>
    [MustState("could not be validated")]
    internal const string SweepDialogueRefusalsCut =
        " {0} of the {1} seed(s) that could not be validated are named above.";

    /// <summary>One seed's head inside the merged response: what it is, what it resolved to, and how far it fans
    /// out. Its own sentence, because a section of a merged response must not name a different tool.</summary>
    [NoClaims("a seed head; the claims are the findings under it and this family's boundary")]
    internal const string DialogueSeedHead = "seed {0} — {1} {2}, winner {3}, {4} topic(s)\n";

    /// <summary>A quest seed that owns no dialogue at all. Said rather than left as a head with nothing under it:
    /// "owns no topics" and "the topics did not fit" are different answers.</summary>
    [MustState("owns NO dialogue topics")]
    internal const string DialogueSeedNoTopics =
        "  this quest owns NO dialogue topics in the active load order — nothing to validate. If you expected some, " +
        "check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n";

    /// <summary>The same seed, where the fan-out could not read a plugin: what it found is bounded by what it could
    /// open, so the answer is about the plugins read, never about the quest.</summary>
    [MustState("could read", "not the whole load order")]
    internal const string DialogueSeedNoTopicsRead =
        "  no dialogue topics of this quest were found in the plugins this sweep could read — that is not the whole " +
        "load order: the coverage gap below names the plugin(s) left out, and any topic they own is missing here.\n";

    /// <summary>A plugin the fan-out could not read, printed as what bounds the report rather than as a finding
    /// against the quest.</summary>
    [NoClaims("a label around the core's own gap sentence; that sentence carries the claim")]
    internal const string DialogueSeedScanGap = "  coverage gap: {0}\n";

    /// <summary>A quest seed whose CK-parity subrecords are present and correct. Stated rather than left silent,
    /// for the reason the per-topic "graph: OK" line exists: a sub-check that ran and passed is a different answer
    /// from one nobody ran, and an absence cannot tell them apart.</summary>
    [MustState("quest CK-parity: OK", "NextAliasID (ANAM)", "Flags (FNAM)")]
    internal const string DialogueQuestParityOk =
        "  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its " +
        "Flags (FNAM).\n";

    /// <summary>The dialogue family's counts line, above everything a budget can refuse, so a caller whose topic
    /// blocks were all cut still learns the totals. It states validated against reached, in
    /// <see cref="DialogueOutcome"/>'s sense: a bare validated count has no denominator and is indistinguishable
    /// from the seeds the caller named.</summary>
    [MustState("finding(s) across", "reached were validated")]
    internal const string DialogueCounts =
        "{0} of the {1} seed(s) reached were validated, {2} topic(s), {3} finding(s) across them.\n";

    /// <summary>What <c>counts_only=true</c> leaves out for this family, stated where the listing would have been:
    /// a mode that renders no blocks must say so rather than look like a validation that found nothing.</summary>
    [MustState("counts_only=true", "no per-topic blocks")]
    internal const string DialogueCountsOnly =
        "counts_only=true: the totals above and the unreachable seeds below, and no per-topic blocks. Drop " +
        "counts_only= to see each topic's findings.\n";

    /// <summary>How many source plugins the roster names before it says how many it did not — the count of the
    /// rest is what keeps the roster from becoming its own silent cut.</summary>
    internal const int SweepRosterRows = 10;
}

using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// ONE SOURCE PER SENTENCE for the READ surface's user-facing prose — the <see cref="WriteSentences"/> pattern,
/// on the other surface.
///
/// <para><b>Why it starts here.</b> The read surface's response-layer migration is its own scheduled piece of
/// work; this class is not it. It exists because the first read sentences that carry CLAIMS a caller acts on
/// arrived (#342's owned-child annotation), and the write surface's lesson is that a sentence born as a render
/// literal is a sentence that gets copied and then drifts. So they are born here, with the same two nets the
/// write sentences have: the CONTENT net (<see cref="MustStateAttribute"/> — the phrases whose loss changes what
/// the caller is told, declared beside the sentence) and a REACH net in the probe that owns the feature.</para>
///
/// <para><b>Two tiers, because the cheap question and the precise one cost differently.</b> Naming WHICH other
/// plugins declare children means reading their bodies, and the resolver fetches a body by enumerating a whole
/// overlay — measured on a real load order at 588 ms for one Dawnstar cell read and 2.5 s for a worldspace, with
/// an unbounded artifact job never finishing. So the precise answer is stated only by the lane that has ALREADY
/// fetched those bodies (<c>conflict_tree=true</c>), and every other read states the cheaper fact the index alone
/// settles: how many other plugins touch the record, and that their declarations were not read.</para>
///
/// <para><b>Two shapes, because one sentence was false for two of its own fields.</b> A COLLECTION child field is
/// assembled additively; a SINGULAR one (Cell.Landscape, Worldspace.TopCell) is one record several plugins
/// OVERRIDE. "Not the merged total" is true of the first and false of the second — see
/// <see cref="OwnedChildShape"/>. Each arm states what is true of its shape.</para>
///
/// <para><b>The response/field split.</b> The invariant half is a fact about the response, not about one field,
/// so it is stated ONCE per response. Carried per field it cost 288 chars per annotated field per record, ~275 of
/// them identical on every row — a bulk response spent its budget restating one sentence and truncated real rows
/// to make room.</para>
///
/// <para><b>What a response-level clause may claim.</b> The clause made two claims every transport had to satisfy
/// independently — a POSITIONAL one ("an annotated field above") and a PRESENCE one (that such a field is in the
/// medium at all) — and each new medium falsified one of them. Both are now structural rather than promised: the
/// clause NAMES its fields instead of pointing at them, so no ordering can invert it; and the caller states it
/// only over fields the medium actually EMITTED, so no truncation, spill or manifest split can strand it over a
/// body that does not show the field. The two conditions are independent — <see cref="ClauseReserve"/> decides the
/// clause FITS, emission decides it is STATED — and each is pinned on its own.</para>
/// </summary>
internal static class ReadSentences
{
    // ---- tier 1: the cheap fact every read can state from the index alone ----------------------------

    /// <summary>The per-field half of the cheap tier. It claims ONLY what the index knows — that other plugins
    /// touch this record and this read did not look at what they declare. It must never imply they DO declare
    /// content (unknown without reading them) nor that they don't.</summary>
    [MustState("were not read")]
    internal const string NotRead = "other plugin(s) touch this record; their declarations for this field were not read";

    /// <summary>The response-level half of the cheap tier, naming the lane that answers precisely.
    ///
    /// <para><b>The remedy names the tool AND the format, because a bare parameter name is not runnable from most
    /// of the surfaces that receive this sentence.</b> The clause ships from every lane that renders record fields.
    /// <c>housecarl_records</c> has no <c>conflict_tree</c> parameter at all (and its
    /// <c>project={"form":"tree"}</c> carries no declarer names either); <c>format=json</c> and <c>format=dense</c>
    /// refuse <c>conflict_tree=true</c> as a text-only diff view; so does <c>to_file=</c>, which means every
    /// artifact manifest carries this clause to a caller who cannot use a bare "pass conflict_tree=true" without a
    /// refusal. Naming the two tools and the text mode is what makes the sentence executable from where it is
    /// actually read — the remedy-never-run mistake is naming a spelling the recipient's surface rejects.</para>
    ///
    /// <para><b>It NAMES its fields; it never points at them.</b> <c>{0}</c> is filled with the fields the response
    /// actually annotated (<see cref="FieldList"/>). A clause that said "an annotated field ABOVE" made a
    /// POSITIONAL claim every medium had to satisfy independently, and three of them falsified it: the artifact
    /// manifest is line 1 with its rows below, the single-read json object wrote the clause ahead of its own
    /// <c>fields</c> array, and a cap hit inside the text field loop truncated away the very field being pointed
    /// at. Naming the fields is true from any position, in any medium.</para></summary>
    [MustState("declared per plugin", "conflict_tree=true", "housecarl_read_record", "text mode")]
    internal const string NotReadFraming =
        "note: this response annotates field(s) that hold CHILD RECORDS ({0}). Child records are declared per " +
        "plugin — so what one plugin's body carries is not the whole story for that field. This read did not open " +
        "the other plugins' bodies to see what they declare. To get a read that does, and names them: " +
        "housecarl_read_record or housecarl_batch_record_detail in text mode (the default) with conflict_tree=true " +
        "— it is a text-only view, so json, dense and to_file reads refuse it.";

    /// <summary>The cheap tier's response-level clause over the fields <paramref name="fields"/> the response
    /// actually emitted an annotation for.</summary>
    internal static string NotReadClause(IReadOnlyCollection<string> fields) => string.Format(NotReadFraming, FieldList(fields));

    /// <summary>The cheap tier's per-field line: the count the index knows, and the honest limit.</summary>
    internal static string NotReadNote(int others) => $"{others} {NotRead}";

    // ---- tier 2: the precise answer, off bodies the conflict-tree lane already fetched ----------------

    /// <summary>The per-field label for a COLLECTION child. A pure label: on its own it asserts nothing a caller
    /// acts on — the claim is the plugin names it introduces, and their meaning is
    /// <see cref="MergeCollection"/>.
    ///
    /// <para><b>No leading "also".</b> "also declared by" asserts that the body being read declares content TOO,
    /// which is false in exactly the case this feature exists for: a winner carrying 0 references beside a base
    /// carrying 201 rendered <c>Temporary = [list: 0 item(s)]   (also declared by HcOcBase.esm)</c>, which reads as
    /// "this body declares some, and so do these" — the confident wrong reading the annotation was built to stop.
    /// "declared by" is true whether or not this body declares, so it is true in both directions.</para></summary>
    [NoClaims("a label; the claim is the plugin names it introduces, and their meaning is MergeCollection")]
    internal const string DeclaredBy = "declared by";

    /// <summary>The per-field line for a SINGULAR child: a COUNT, deliberately not a name list. Landscape and
    /// TopCell are overridden by hundreds of plugins on a real order ("+483 more" is noise, not information), and
    /// which one wins is a load-order question the conflict tree answers properly. No leading "also", for the
    /// reason <see cref="DeclaredBy"/> states — the singular flagship is a body that carries NOTHING.</summary>
    [NoClaims("a label; the claim is SingleResolved, and the count is evidence rather than an assertion")]
    internal const string CarriedBy = "carried by";

    /// <summary>The Q3 half: a plugin touching this record whose body or field could not be read. Stated, never
    /// dropped — an unreadable body silently missing from the list would read as "nobody else declares", which is
    /// the same wrong answer this annotation exists to prevent, one level down.</summary>
    [MustState("could NOT be read")]
    internal const string CouldNotRead = "could NOT be read";

    /// <summary>The response-level fact for COLLECTION children: assembled additively, so one body's list is not
    /// the total.
    ///
    /// <para>Deliberately names NO remedy for the total. The read that would answer "what is actually live in this
    /// parent" (a FormKey-keyed union with each child at its own winner) does not exist yet, and re-reading with
    /// <c>plugin=</c> is not load-order truth either — that body's own children can themselves be overridden
    /// further up.</para></summary>
    [MustState("declared per plugin", "not the merged total")]
    internal const string MergeCollectionFraming =
        "note: this response annotates field(s) that hold MANY child records ({0}) — a cell's placed references, a " +
        "topic's INFO lines, a worldspace's cells. Those are declared per plugin and the game assembles them from " +
        "every plugin that declares them, so the value shown is one plugin's own declaration, not the merged total.";

    /// <summary>The COLLECTION clause over the fields the response actually emitted an annotation for.</summary>
    internal static string MergeCollection(IReadOnlyCollection<string> fields) => string.Format(MergeCollectionFraming, FieldList(fields));

    /// <summary>The response-level fact for SINGULAR children, which is a DIFFERENT fact. One record, one FormKey:
    /// the plugins that carry it are overriding each other, and the one the game uses is decided by load order.
    /// What the annotation tells a caller here is that the child EXISTS despite this body not carrying it — not
    /// that anything is being merged.</summary>
    [MustState("ONE child record", "does not remove it")]
    internal const string SingleResolvedFraming =
        "note: this response annotates field(s) that hold ONE child record with its own FormID ({0}). The plugins " +
        "that carry it are overriding each other and load order decides which version the game uses — so this body " +
        "not carrying it does not remove it, and the count in that annotation is how many other plugins carry one, " +
        "not a total.";

    /// <summary>The SINGULAR clause over the fields the response actually emitted an annotation for.</summary>
    internal static string SingleResolved(IReadOnlyCollection<string> fields) => string.Format(SingleResolvedFraming, FieldList(fields));

    /// <summary>The field names a response-level clause is ABOUT — derived from what the response annotated, never
    /// a prose list.
    ///
    /// <para><b>Why derived.</b> The set these clauses describe is <see cref="OwnedChildContent.Fields"/>, i.e.
    /// <c>WriteEngine.ChildBearingProperties</c> split by shape, and that set grows on a Mutagen bump with no edit
    /// here — <see cref="OwnedChildContent"/> promises exactly that. A hand-written list ("Persistent / Temporary /
    /// NavigationMeshes / Responses / SubCells") stays green under every net while describing fields that are not
    /// the annotated one: a new collection-shaped owned child would be annotated correctly and then named
    /// incorrectly. Naming what the response annotated cannot drift, because there is no second list to drift
    /// from.</para>
    ///
    /// <para><b>Bounded on purpose.</b> The joined list is cut at <see cref="ClauseFieldsMaxChars"/> so a clause's
    /// worst-case length is a CONSTANT — which is what lets <see cref="ClauseReserve"/> subtract it from the
    /// caller's <c>max_chars</c> before the body renders instead of adding it on top afterwards.</para></summary>
    internal static string FieldList(IReadOnlyCollection<string> fields)
    {
        var joined = string.Join(", ", fields);
        return joined.Length <= ClauseFieldsMaxChars ? joined : joined[..ClauseFieldsMaxChars].TrimEnd(',', ' ') + ", …";
    }

    /// <summary>The char budget the derived field list gets inside a clause. Small on purpose: the list exists to
    /// say WHICH fields, and against Mutagen 0.53.1 the whole child-bearing set is seven names.</summary>
    internal const int ClauseFieldsMaxChars = 120;

    /// <summary>Slack over a framing's own length: the "{0}" it loses (3), the ", …" an over-long list gains (3),
    /// and the newlines the text lane wraps each clause in (2).</summary>
    const int ClauseGlue = 8;

    /// <summary>The worst-case chars the response-level clauses can cost, for the clause KINDS a response may still
    /// state. Reserved out of <c>max_chars</c> before the body renders (#342 review, finding 6): these clauses are
    /// load-bearing, so dropping them at the cap would be the wrong repair, and appending them past it made a
    /// 2000-char batch return ~3100 with the overrun invisible to the <c>truncated</c> flag the auto-spill trigger
    /// reads.</summary>
    internal static int ClauseReserve(bool notRead, bool collection, bool singular) =>
        (notRead ? NotReadFraming.Length + ClauseFieldsMaxChars + ClauseGlue : 0)
        + (collection ? MergeCollectionFraming.Length + ClauseFieldsMaxChars + ClauseGlue : 0)
        + (singular ? SingleResolvedFraming.Length + ClauseFieldsMaxChars + ClauseGlue : 0);

    // ---- the sweep response's omission accounting (#344 / #361) -------------------------------------
    //
    // These framings are the PROSE half of CheckAccounting; the arithmetic is there, in one place, and every
    // number below arrives already computed from what the render EMITTED. They live in this class rather than
    // beside that logic for the same reason ReadSentences exists at all: a sentence born as a render literal is
    // a sentence that gets copied into the other transport and then drifts. Here they are inside the content net
    // that walks this class's consts, so emptying one fails a guard instead of a review round.

    /// <summary>The accounting's own framing. It used to be baked into the two listing leads below, so a lane with
    /// no listing emitted its clauses with no opener and then the closer on its own — the accounting's framing had
    /// exactly the shape it exists to forbid. It is not about any subject, so it is stated outside every subject
    /// gate.</summary>
    [NoClaims("a label; the claims it introduces are the accounting's own clauses")]
    internal const string SweepAccountingLead = "[accounting:";

    [MustState("appear above", "found by this sweep")]
    internal const string SweepAllVisible =
        " all {0} dangling ref(s) found by this sweep appear above.";

    /// <summary>The lead, on a response that is missing findings. ONE number for the question a caller actually
    /// has - how many of these can I see - measured on the live order at plain defaults, where the two sentences
    /// this replaces said "3996 not listed" and "554 of the 1000 budget-listed appear above" and left the reader
    /// to work out that 554 of 4996 was the answer.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepVisible =
        " {0} of the {1} dangling ref(s) found by this sweep appear above.";

    /// <summary>The listing budget's share of what is missing. A cause clause is stated only when its own count is
    /// non-zero - a response cut by max_chars alone must not report the budget dropping 0, which reads as the
    /// budget being involved when it was not.</summary>
    [MustState("limit=")]
    internal const string SweepOmittedByBudget = " {0} were never listed: the listing budget (limit={1}) ran out";

    /// <summary>The response cut's share. Its subject is THIS RESPONSE, and it names max_chars because that is the
    /// knob that moves it - the layer rule kept as the accounting's internal discipline: one computation, but each
    /// cause named by the layer that caused it.</summary>
    [MustState("max_chars=")]
    internal const string SweepOmittedByCut = " {0} did not fit this response (max_chars={1})";

    /// <summary>How many plugin sections reached the response. Answers what the entry count cannot: a plugin whose
    /// whole set is missing has no section to read the loss off, so the section count is what tells a reader the
    /// listing is a window rather than the whole order.</summary>
    [MustState("plugin section(s)")]
    internal const string SweepSections = " {0} of {1} plugin section(s) were rendered.";

    /// <summary>The DIALOGUE family's own version, in ITS units. It used to borrow the sentence above, so a merged
    /// response told the caller how many "plugin section(s)" a family that never looks at a plugin had rendered
    /// (round-1 review).</summary>
    [MustState("seed section(s)")]
    internal const string SweepDialogueSeedSections = " {0} of {1} seed section(s) were rendered.";

    /// <summary>The excluded-plugin roster's own cut. Its rows are the plugins houseCARL could not parse at all,
    /// so losing them silently hides the honesty layer rather than a finding.</summary>
    [MustState("could not be parsed")]
    internal const string SweepExcludedCut = " {0} of {1} plugin(s) that could not be parsed are named above.";

    /// <summary>The counts_only lane's unread rows, same rule as the excluded roster.</summary>
    [MustState("could not be read")]
    internal const string SweepUnreadCut = " {0} of {1} plugin(s) whose records could not be read are named above.";

    /// <summary>The by-source roster: WHICH plugins are missing entries, and how many each. Under the render-aware
    /// accounting this is the RESPONSE's roster, not the budget's - a plugin whose entries the budget listed and
    /// the cut then dropped belongs here, and under the two-layer split it appeared in neither sentence.</summary>
    [NoClaims("a label introducing the roster rows; every claim it carries is in the rows and the clauses around it")]
    internal const string SweepRosterLead = " Missing here, by source plugin: ";

    /// <summary>The roster's own truncation. A roster that silently stops rebuilds the hole this accounting closes,
    /// one level down (#344 round-1 review), so the count it did not name is stated rather than implied.</summary>
    [MustState("not named here")]
    internal const string SweepRosterCut = " (the {0} largest of {1}; the rest are not named here)";

    /// <summary>A RULE about the listing rather than a claim about this response's contents. It is stated wherever
    /// a roster is, which is the only place it means anything — it exists to tell a reader that the roster above is
    /// where a plugin with no section of its own can still be found.</summary>
    [MustState("no section of its own")]
    internal const string SweepNoSectionRule =
        " A plugin whose whole set is missing here, with nothing else to report, gets no section of its own.";

    // THE REMEDY IS ASSEMBLED FROM THE CAUSES THAT ACTUALLY FIRED. One fixed sentence naming every knob was
    // measured wrong twice over: it opened with "Raise limit= to list more" on responses where the budget had
    // dropped nothing, and it promised scoping "lists its set in full unless that set is itself larger than limit="
    // on responses whose cause was max_chars — driven on a real scope, that promise returned 16 of 40. A knob named
    // beside a cause it did not move is a remedy the caller can follow and land in the same place.

    /// <summary>Offered only where the listing budget actually dropped something.</summary>
    [MustState("limit=")]
    internal const string SweepRemedyLimit = " Raise limit= to list more.";

    /// <summary>Offered only where THIS RESPONSE could not fit what the budget admitted.</summary>
    [MustState("max_chars=")]
    internal const string SweepRemedyMaxChars = " Raise max_chars= to fit more of what was found.";

    /// <summary>The scoping clause, and it names BOTH bounds rather than one. Re-spending the budget on one plugin
    /// says nothing about whether the response can carry the result: on the live order the largest single source
    /// runs to 2591 refs against a default limit of 1000, and a scoped sweep of it still met max_chars.</summary>
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

    /// <summary>The lead both overrun sentences share, and the ENUMERATION of what a response carries whatever the
    /// budget says. One constant because the two sentences differ only in what happened next — spelled twice, the
    /// list drifted: it said "its header, the accounting above, the boundary" for a whole branch after this lane
    /// gained a third member, each cut axis's own closing disclosure, reserved out of <c>max_chars</c> before the
    /// body renders. On a two-200-row-axis counts_only overrun those disclosures are ~289 chars of what did not
    /// fit, and the sentence explaining the overrun named everything except them — leaving a reader to blame a
    /// short header and a boundary they can measure.
    ///
    /// <para>The third member is stated CONDITIONALLY ("for anything it cut short") because the listing lane has
    /// no histogram axes and owes no closing line — an unconditional member would be a list naming something that
    /// lane does not carry, which is the same class of wrong the addition fixes.</para></summary>
    [MustState("its header", "the accounting above", "cut short", "the boundary")]
    internal const string SweepFixedPartLead =
        " This response is {2} chars, longer than the max_chars={0} it was given: what it must carry whatever the " +
        "budget — its header, the accounting above, the closing line for anything it cut short, the boundary — ";

    /// <summary>The one arm where the response is allowed to exceed max_chars, and it says so. A cap smaller than
    /// the accounting itself leaves no honest response: dropping the accounting restores exactly the silence #361
    /// IS, and refusing turns a call that answers today into one that does not. So the accounting ships and the
    /// overrun is NAMED with the number that clears it.
    ///
    /// <para><b>It is MEASURED, never predicted.</b> The first cut of this fired on
    /// <c>headerLength + reserve &gt; max_chars</c> — a statement about the worst case the reserve is sized for, not
    /// about this response. It printed "this response is longer than the max_chars you gave it" over responses that
    /// were comfortably shorter: 1032 caps out of a 200–6000 sweep in the default text lane alone, and every cap
    /// from 800 up in the missing-masters lane, where the reserve is held for a line that lane never emitted. A
    /// sentence about a length is asked of the length.</para></summary>
    /// <summary>Pinned on the COMPLETED sentence as well as on the lead, because sharing the lead is a
    /// construction and a construction can be undone: sabotaged to spell its own opener with a shortened list,
    /// this sentence passed everything. The phrases are the enumeration's members, so a sentence that stops
    /// carrying one fails by name whether it dropped the member or dropped the lead.</summary>
    [MustState("max_chars=", "raise it to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapTooSmall =
        SweepFixedPartLead + "does not fit in that many chars, so raise it to at least {1}.";

    /// <summary>The OTHER way a response ends up over its cap, and the reason there are two sentences rather than one
    /// with a cause that is only sometimes true. A body unit whose size cannot be known before it is written — a json
    /// entry, whose cost the emitter deliberately leaves at zero — can land past what the budget had left. The fixed
    /// part FITS in that case: the notice above would tell the caller their header and accounting do not fit in a cap
    /// that holds them with room to spare, and send them chasing a cap that is not the problem.
    ///
    /// <para>Which sentence applies is not new state: the overrun arm is already handed what the fixed part needs, so
    /// <c>needed &gt; max_chars</c> IS the discriminator. Measured on a json entry carrying a 4,000-character EditorID:
    /// 7,008 chars against a 4,000 cap, over a fixture whose irreducible response is 2,618.</para>
    ///
    /// <para>Both spellings end in the same remedy clause, because the remedy is the same and two spellings of it is
    /// how the cap sweep stops finding the number it follows.</para></summary>
    [MustState("max_chars=", "raise it to at least", "its header", "the accounting above",
               "the closing line for anything it cut short", "the boundary")]
    internal const string SweepCapOvershot =
        SweepFixedPartLead + "does fit, but one body unit was written before its size could be measured and ran " +
        "past what was left, so raise it to at least {1}.";

    /// <summary>The sweep's honest scope boundary, stated to BOTH transports from here. It was two hand-copied
    /// twins — the text render's and the json writer's — and they had already drifted: the text one qualified the
    /// ownership word with "(a rank/global Mutagen can't type on an override)" and the json one did not. The fuller
    /// reading is the one kept, per the response-layer plan's rule that a twin disagreement resolves to one reading
    /// and the choice is named in the PR body.
    ///
    /// <para>The label is separate because only one transport wants it: text prints "boundary: " ahead of the claim,
    /// json carries the same claim as the value of a key already called <c>boundary</c>, and repeating the word
    /// inside the value is how a twin starts.</para></summary>
    [MustState("Does NOT verify navmesh/terrain", "required-but-null", "unused-master cleanup", "legal optional")]
    internal const string SweepBoundary =
        "checks FormLink resolution, missing masters, and parse failures. Does NOT verify navmesh/terrain spatial " +
        "integrity (CRC/grid), flag required-but-null fields, list unused-master cleanup, or link-check an owned " +
        "item's ownership 'variable' word (a rank/global Mutagen can't type on an override); a null FormLink is a " +
        "legal optional.";

    /// <summary>The unbound total, spelled so it can never claim a class nobody checked. ONE definition, used by
    /// the header AND by the accounting's listing-budget clause — the accounting restating the numbers in its own
    /// words is how "0 unbound" survived the header fix once already (PR #288 re-review, finding 2).
    ///
    /// <para>It also carries the <c>property_contains=</c> label when one is in force, so this number states its own
    /// scope rather than leaning on a blanket claim the sweep's other counts would not satisfy (round-3 review).</para>
    ///
    /// <para>It lives here rather than in the render because it is a SENTENCE with claims in it, and a sentence born
    /// as a render literal is one the content net cannot see. The four spellings below are its claims.</para></summary>
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
    /// Absent from records-with-scripts and unverifiable, which it does not narrow — that asymmetry is the whole
    /// point.</summary>
    [NoClaims("a scope label; the claim is the count it qualifies")]
    internal const string SweepScriptPropLabelFormat = " matching '{0}'";

    static string ScriptPropLabel(ScriptCheckResult r)
        => r.PropertyContains is null ? "" : string.Format(SweepScriptPropLabelFormat, r.PropertyContains);

    /// <summary>Both unbound classes excluded: the total reads NOT CHECKED, never 0. A 0 would say "looked, found
    /// none" about the HIGH silent-None class nobody looked for.</summary>
    [MustState("NOT CHECKED", "findings=")]
    internal const string SweepScriptUnboundNotChecked = "unbound NOT CHECKED (findings= excluded both unbound classes)";

    /// <summary>One unbound class excluded — the number is real, and says which half it is not about.</summary>
    [MustState("NOT CHECKED", "unbound_scalar")]
    internal const string SweepScriptObjectOnly = " (object only — unbound_scalar NOT CHECKED)";

    /// <summary>The other half, same rule.</summary>
    [MustState("NOT CHECKED", "unbound_object")]
    internal const string SweepScriptScalarOnly = " (scalar only — unbound_object NOT CHECKED)";

    /// <summary>The advisory class excluded, same rule again.</summary>
    [MustState("NOT CHECKED", "bound_null")]
    internal const string SweepScriptNullNotChecked = "bound-but-null NOT CHECKED (findings= excluded 'bound_null')";

    /// <summary>The scripts family's lead when its listing is whole. It exists for the reason
    /// <see cref="SweepAllVisible"/> does: without it, silence means both "this response carries everything" and
    /// "something was dropped and nothing said so", and those two must never read alike.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptAllVisible =
        " all {0} record section(s) found by this sweep appear above.";

    /// <summary>The scripts family's lead when it is not. A "record section" and a "plugin section" are different
    /// units, so this is its own sentence rather than <see cref="SweepVisible"/> reworded — one sentence for both
    /// would misname whichever family it was not written for.</summary>
    [MustState("appear above", "found by this sweep")]
    internal const string SweepScriptVisible =
        " {0} of the {1} record section(s) found by this sweep appear above.";

    /// <summary>The scripts family's listing budget, decomposed the same way the errors family's is: a subtraction
    /// against the sweep's own totals rather than a bare "capped" flag.
    ///
    /// <para>It restates the TRUE TOTALS, per class, from <see cref="ScriptTotals"/> — the same one definition the
    /// header uses. A <c>findings=</c> filter narrows the population this clause counts, so a bare "property
    /// finding(s)" here would read as a claim about classes nobody looked for; restating the totals in the
    /// header's own NOT CHECKED wording is what the marker this clause replaces did, and it carries.</para></summary>
    [MustState("limit=", "True totals")]
    internal const string SweepScriptFindings =
        " {0} of the {1} property finding(s) this sweep found were listed: the listing budget (limit={2}) ran out. " +
        "True totals: {3}.";

    /// <summary>The scripts family's honest scope boundary, stated to BOTH transports from here. It was two
    /// hand-copied twins that had already drifted by a comma; the fuller reading is kept, per the same rule
    /// <see cref="SweepBoundary"/> was unified under.</summary>
    [MustState("Auto (CK-editable)", "not code-driven full properties", "flag to VERIFY", "never passed clean")]
    internal const string SweepScriptBoundary =
        "checks Auto (CK-editable) properties across the extends chain — not code-driven full properties. An " +
        "unbound object property is the silent-None footgun, but CAN be intentional (filled at runtime) — a " +
        "finding is a flag to VERIFY. A script whose .pex is not on disk is reported unverifiable, never passed " +
        "clean.";

    /// <summary>WHICH FAMILIES THIS RESPONSE ANSWERS FOR, WHICH SELECTED ONES REFUSED, AND WHICH REGISTERED ONES IT
    /// NEVER ASKED — the sentence that makes the narrowed default honest (ruling item 2, 2026-08-21), composed from
    /// the OUTCOME rather than the selection (round-2 finding B2).
    ///
    /// <para><b>Why the default narrows at all.</b> <c>findings=</c> omitted runs the errors family alone. It
    /// cannot be every family: an unscoped scripts sweep is 468 seconds on a 3800-plugin order, measured, and an
    /// unscoped dialogue sweep is a declared cost-refusal (SPEC §6.1 F1.2). A default that narrowed silently would
    /// be the sweep answering a question the caller did not ask and not saying which one — so the response states
    /// what answered, what refused, what was never asked, and the exact <c>findings=</c> spelling that adds it.
    /// </para>
    ///
    /// <para><b>It is ONE COMPLETE SENTENCE, stated whole by BOTH transports.</b> Not a lead each transport
    /// finishes its own way: that shape has failed twice (#337, f907350), because a pin on a shared lead vouches
    /// for nothing about what either transport actually says. The pin is on these strings, and these strings are
    /// what the text render prints and what the json document carries — so a transport that stops saying it fails
    /// by name. The clauses are separate consts because they are separate facts, each present exactly where its
    /// list is non-empty: baked into the lead, the "did NOT run" tail was written on every response that had one
    /// absent family and nothing at all said which families had REFUSED.</para></summary>
    [MustState("findings=", "the default family only")]
    internal const string SweepFamiliesDefaulted =
        "findings= was not given, so this sweep ran the default family only: {0}.";

    /// <summary>The same fact when the caller DID choose. "You did not ask for these" and "you asked for these and
    /// not those" are different sentences, and one wording for both tells the second caller something false about
    /// their own call. It says what the response ANSWERS FOR, which is not the same list as what was selected.
    /// </summary>
    [MustState("findings=", "answers for")]
    internal const string SweepFamiliesChosen =
        "findings= selected, and this response answers for: {0}.";

    /// <summary>Every registered family ran AND answered, so there is nothing to name as absent or refused — and the
    /// sentence says THAT, rather than going quiet. Silence would read the same as a response whose sentence had
    /// been dropped. It is chosen off what came back: picked off the SELECTION, a three-family call whose dialogue
    /// section was nothing but a cost refusal still led with this one.</summary>
    [MustState("findings=", "every findings family")]
    internal const string SweepFamiliesAll =
        "findings= ran every findings family this surface registers: {0}.";

    /// <summary>NO family answered — every family this call selected refused, and for DIFFERENT reasons, so the
    /// response is a document of refusal sections rather than one error string (the grounds-are-one rule,
    /// <see cref="CheckOutcome"/>). Said, rather than left as a lead with an empty list after it.</summary>
    [MustState("findings=", "NO family", "refused")]
    internal const string SweepFamiliesNoneAnswered =
        "findings= answered for NO family: every family this call selected refused, and each states its own ground " +
        "in its own section below.";

    /// <summary>The families that were SELECTED and refused. Their findings are absent, not clean — which is the
    /// same Q3 reading the off-order sentence exists to stop, one level up: a caller who reads only the lead would
    /// take a family named there as one that looked.</summary>
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

    /// <summary>The scripts family has no off-order lane: <c>check_errors</c> resolves a plugin that is on disk but
    /// not in the active order and sweeps it anyway, and the script sweep refuses such a name outright. On one
    /// <c>plugins=</c> list feeding several families that asymmetry has to be STATED rather than resolved by
    /// silently widening one family or refusing the whole call — extending the off-order lane to another family is
    /// capability growth, and it belongs in an issue rather than in a merge.</summary>
    [MustState("off-order", "did NOT sweep")]
    internal const string SweepFamilyOffOrderSkipped =
        "the {0} family did NOT sweep {1}: that plugin is on disk but not in the active load order, and only the " +
        "errors family has an off-order lane. Its findings for that file are absent, not clean.";

    /// <summary>The merged response's boundary label. Each family states its OWN boundary — the two claim different
    /// things — so the label names which family's claim follows it. The single-family tools keep
    /// <see cref="SweepBoundaryLabel"/>, whose response has only one.</summary>
    [NoClaims("a label; the claim it introduces is the named family's own boundary")]
    internal const string SweepBoundaryLabelFor = "boundary ({0}): ";

    /// <summary>The merged response's own title.</summary>
    [NoClaims("a title; the response's claims are its families' own")]
    internal const string SweepMergedTitle = "check — derived-findings sweep";

    /// <summary>One family's section head in a merged response: the token a caller spells in <c>findings=</c>, and
    /// the title that family's ancestor tool used for a whole response.</summary>
    [NoClaims("a section label; the claims below it are the family's own")]
    internal const string SweepFamilySectionHead = "[{0}] {1}";

    /// <summary>The text transport's label for the claim above.</summary>
    [NoClaims("a label; the claim it introduces is SweepBoundary")]
    internal const string SweepBoundaryLabel = "boundary: ";

    // ---- the dialogue family (SPEC §6.1, classes 1-7) ------------------------------------------------

    /// <summary>THE COST-REFUSAL (SPEC §6.1 F1.2). The dialogue family is SEEDED, not swept (F1.1): a call naming it
    /// with no <c>seeds=</c> has given it an empty scope, and resolving that to "the whole order" would be the
    /// whole-order dialogue sweep this rule refuses. So it refuses, states its own behaviour, and spells the call
    /// that works.
    ///
    /// <para><b>The numbers are measured, and named with the order they were measured on.</b> §2 rule 1 category (c)
    /// asks a declared cost-refusal to carry its bound and its numbers rather than an adjective. On ARR 2.0
    /// (2026-08-21): the active order carries 82,343 dialogue topics, and validating ONE quest's 235 owned topics
    /// took 13.6 seconds — which is the shape of the cost, not an extrapolation of the total.</para></summary>
    [MustState("seeds=", "will NOT sweep the whole load order", "82,343")]
    internal const string DialogueNeedsSeeds =
        "findings=[\"dialogue\"] needs seeds=. This family validates the topics and quests you NAME, and it will " +
        "NOT sweep the whole load order — that is a declared cost bound, not a missing feature: a whole-order pass " +
        "is a per-topic graph walk across every plugin that touches each topic, and the order this bound was " +
        "measured on carries 82,343 dialogue topics (one quest's 235 owned topics alone took 13.6 s). " +
        "Name what to validate: seeds=[\"XXXXXX:Plugin.esp\"] takes a dialogue topic (DIAL), a quest (QUST) — which " +
        "expands to every topic that quest owns — a dialogue view (DLVW), or a dialogue branch (DLBR).";

    /// <summary>Every seed named failed to resolve, so the family validated nothing. A section of nothing under a
    /// heading would read as "looked, found none" — the same misreading the off-order sentence exists to stop, one
    /// family over.</summary>
    [MustState("validated NOTHING", "ACTIVE load order")]
    internal const string DialogueNoSeedResolved =
        "findings=[\"dialogue\"] validated NOTHING: not one of the {0} seed(s) named resolved. {1} A seed is a DIAL, " +
        "QUST, DLVW or DLBR FormID spelled 'XXXXXX:Plugin.esp' and is resolved against the ACTIVE load order — a " +
        "record only a disabled plugin defines is not reachable here.";

    /// <summary>ONE unresolved seed, named with why. Carried rather than dropped: this family's scope IS its seed
    /// list, so a discarded seed is a silently narrowed scope the caller reads as a clean answer (Q3).</summary>
    [MustState("NOT validated")]
    internal const string DialogueSeedRefused = "  [X] {0} — NOT validated: {1}\n";

    /// <summary>THE SCOPE ASYMMETRY, stated in this family's own section beside its own counts. The sweep families
    /// take plugin scope; this one takes seeds (F1.1), so the scope parameters a caller passed alongside it did not
    /// narrow it — and it has no off-order lane either, for a different reason than the scripts family: a seed is a
    /// RECORD, resolved against the active order, and there is no on-disk lane for a record.
    ///
    /// <para>Unstated, a caller who wrote <c>plugins=["MyMod.esp"] findings=["errors","dialogue"]</c> would read the
    /// dialogue section as scoped to their plugin when it is scoped to their seeds — the two answers differ and
    /// nothing in the response would say which one they were reading.</para>
    /// <para>What it says about HOW MANY is two sentences, not one, and that is the point: "it validated exactly
    /// the N seed(s) given in seeds=" was printed from the number NAMED, so a call whose <c>limit=</c> stopped it
    /// short claimed a completeness the accounting in the same section immediately contradicted (round-1
    /// review).</para></summary>
    [MustState("seeded, not swept", "do NOT scope it", "no off-order lane")]
    internal const string DialogueScopeNote =
        "scope: the dialogue family is seeded, not swept — plugins=, type=, formids=, editorid_contains= and " +
        "exclude= scope the sweep families and do NOT scope it. {0} It has no off-order lane: a seed is a record, " +
        "and it must resolve in the ACTIVE load order.";

    /// <summary>The seed count, when the seed budget REACHED every one of them.
    ///
    /// <para><b>Reached, never validated</b> (<see cref="DialogueOutcome"/>'s vocabulary, round-2 finding B1). This
    /// sentence's number counts every seed the call tried, including the ones that produced a named refusal — so
    /// under the word "validated" it claimed a completeness the <c>[X] … NOT validated</c> rows three lines below it
    /// deny. Round 1 found that, the fold for it reproduced it, and round 2 found it again.</para></summary>
    [MustState("seed(s) given in seeds=", "reached")]
    internal const string DialogueScopeAllSeeds = "It reached all {0} seed(s) given in seeds=.";

    /// <summary>…and when it did not. The knob is named, because a caller reading a short answer needs to know
    /// which parameter moves it — the accounting states the same cut, and the two come off one computation.</summary>
    [MustState("seed(s) given in seeds=", "limit=", "reached")]
    internal const string DialogueScopeSomeSeeds =
        "It reached {0} of the {1} seed(s) given in seeds= — limit= stopped it there.";

    /// <summary>The dialogue family's honest boundary — <c>validate_dialogue</c>'s always-printed standing-limits
    /// footer, carried whole into the merged surface as this family's closing claim. It is the family's BOUNDARY
    /// rather than a body line, so it is reserved and unrefusable: a validator whose whole point is that a clean
    /// structural pass is not "this will play" cannot let a budget drop the sentence that says so.
    ///
    /// <para>Its last clause is the F1 SPLIT, disclosed rather than left to be discovered: class 8, the effective
    /// merged INFO order, is not a finding and did not fold here. A caller chasing "why does the wrong line play"
    /// would otherwise read a clean dialogue section as having looked.</para></summary>
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

    /// <summary>THE BOUNDARY WHERE NO SEED THIS CALL REACHED OWNS AN INFO LIST — every one of them was a DLVW or a
    /// DLBR, so the only check that ran is the record's own CK parity.
    ///
    /// <para><b>Why the boundary forks at all.</b> The sentence above asserts LinkTo and previous-link targets,
    /// each voiced line's .fuz, each result script, and a subset of malformed conditions. None of those has
    /// anything to run against on a view or a branch — those records own no INFO list — so on a call whose every
    /// seed was one, that boundary named checks that never ran (round-3 finding A1). The ancestor never had this
    /// problem: it renders a whole response per seed and takes a record-level arm for those two kinds. Here the
    /// boundary is the FAMILY's, so it is composed from what the family's seeds actually ran.</para></summary>
    [MustState("record-level CK-parity check only", "no dialogue graph", "Validate the owning topics")]
    internal const string DialogueBoundaryRecordLevel =
        "validates the CK-parity subrecords the Creation Kit always writes on the record you named, and nothing " +
        "else: every seed this call reached was a dialogue view (DLVW) or branch (DLBR), which own no INFO list, " +
        "so this is a record-level CK-parity check only — no dialogue graph, voice file, result script or " +
        "condition was checked here. Validate the owning topics (DIAL) or quest (QUST) for those.{0}{1}";

    /// <summary>The SCOPE note under one view/branch seed, in a response that also carries seeds which DO own an
    /// INFO list. The family boundary can only take one arm, and on a mixed call it takes the wide one — true of
    /// the response and not of this seed. Carried from the ancestor, which states it for the same reason.
    ///
    /// <para>Written without indent or newline so the ancestor appends it exactly as it always has; the merged
    /// render pads it into the seed's own body.</para></summary>
    [MustState("record-level CK-parity check only", "Validate the owning topics")]
    internal const string DialogueRecordLevelScope =
        "scope: this is a record-level CK-parity check only — it does not validate any dialogue graph, voice, " +
        "script, or condition surface. Validate the owning topics (DIAL) or quest (QUST) for those.";

    /// <summary>A DIALOGUE VIEW (DLVW) whose own CK-parity subrecords are present. Stated rather than left silent,
    /// for the reason the quest line above is: a sub-check that RAN and PASSED is a different answer from one
    /// nobody ran, and a caller cannot tell those apart from an absence (Q3). A bare DLVW crashes the CK's Dialogue
    /// Views editor, so this is the whole verdict for that kind of seed.</summary>
    [MustState("CK-parity: OK", "DNAM and ENAM")]
    internal const string DialogueViewParityOk =
        "  CK-parity: OK — the DNAM and ENAM byte subrecords the Creation Kit always writes are both present.\n";

    /// <summary>The same for a DIALOGUE BRANCH (DLBR) — different subrecords, so a different sentence rather than
    /// one sentence with the names substituted in: what the check looked at is the answer.</summary>
    [MustState("CK-parity: OK", "TNAM (Category) and DNAM (Flags)")]
    internal const string DialogueBranchParityOk =
        "  CK-parity: OK — the TNAM (Category) and DNAM (Flags) subrecords the Creation Kit always writes are " +
        "both present.\n";

    /// <summary>The conditioned-line clause of the boundary above, present only where there are conditioned lines to
    /// count. Summed over EVERY topic the validation found, not the rendered ones: it is a global honesty note about
    /// what could not be evaluated, never a description of the listing.</summary>
    [NoClaims("a clause of DialogueBoundary; the claim is that sentence's cannot-EVALUATE")]
    internal const string DialogueConditioned =
        " — {0} line(s) here carry conditions, checked for malformedness but not evaluated";

    /// <summary>The asset-layer caveat, riding the boundary where a BSA failed to read: an "absent" .fuz or .pex
    /// above may merely be unscanned.</summary>
    [MustState("may merely be unscanned")]
    internal const string DialogueReadIncomplete =
        " A BSA failed to read this build, so an \"absent\" voice file or .pex above may merely be unscanned — see " +
        "housecarl_load_order_status.";

    /// <summary>The dialogue family's completeness assertion when every topic it found is in the response.</summary>
    [MustState("every one of the")]
    internal const string SweepDialogueAllVisible =
        " every one of the {0} topic(s) these seeds own is listed.";

    /// <summary>…and when the response could not carry them all.</summary>
    [MustState("max_chars")]
    internal const string SweepDialogueVisible =
        " {0} of the {1} topic(s) these seeds own are listed; the rest did not fit this response's max_chars.";

    /// <summary>The seed budget's own share of what is absent — seeds the call never validated at all, which is a
    /// different absence from topics that did not fit and must not read alike.</summary>
    [MustState("limit=", "were NOT reached")]
    internal const string SweepDialogueSeedsCut =
        " {0} of the {1} seed(s) named were reached; {2} were NOT reached because the seed budget (limit={3}) " +
        "ran out.";

    /// <summary>What the validation FOUND, restated where the listing is short — the totals are never capped, and a
    /// short listing with no total beside it reads as the whole answer.</summary>
    [MustState("True totals")]
    internal const string SweepDialogueProblems =
        " True totals: {0} finding(s) across {1} topic(s).";

    /// <summary>The unresolved-seed roster's own cut. Its rows are seeds houseCARL could NOT reach, so a silent cut
    /// there hides the boundary of the answer rather than a finding inside it.</summary>
    [MustState("could not be validated")]
    internal const string SweepDialogueRefusalsCut =
        " {0} of the {1} seed(s) that could not be validated are named above.";

    /// <summary>ONE SEED's head inside the merged response: what it is, what it resolved to, and how far it fans
    /// out. Its own sentence rather than <c>validate_dialogue</c>'s opening line, which names that tool — a section
    /// of a merged response naming a different tool is a caller reading the wrong surface's answer.</summary>
    [NoClaims("a seed head; the claims are the findings under it and this family's boundary")]
    internal const string DialogueSeedHead = "seed {0} — {1} {2}, winner {3}, {4} topic(s)\n";

    /// <summary>A QUEST seed that owns no dialogue at all. Said, rather than left as a head with nothing under it:
    /// "this quest owns no topics" and "the topics did not fit" are different answers (Q3).</summary>
    [MustState("owns NO dialogue topics")]
    internal const string DialogueSeedNoTopics =
        "  this quest owns NO dialogue topics in the active load order — nothing to validate. If you expected some, " +
        "check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n";

    /// <summary>A QUEST seed whose own CK-parity subrecords are present and correct. Stated rather than left
    /// silent, for the reason the per-topic "graph: OK" line exists: a sub-check that RAN and PASSED is a
    /// different answer from one nobody ran, and a caller cannot tell those apart from an absence (Q3).
    ///
    /// <para>Shared because the ancestor states it too. It was inline there and absent here, so the merged
    /// surface went quiet on a passing sub-check the ancestor names — found by the dialogue differential's
    /// line-set comparison, which is what that cell is for.</para></summary>
    [MustState("quest CK-parity: OK", "NextAliasID (ANAM)", "Flags (FNAM)")]
    internal const string DialogueQuestParityOk =
        "  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its " +
        "Flags (FNAM).\n";

    /// <summary>The dialogue family's counts line: what the validation found, above everything a budget can refuse.
    /// A caller whose topic blocks were all cut still learns the totals.
    ///
    /// <para>It states VALIDATED against REACHED, and the two words mean what <see cref="DialogueOutcome"/> says
    /// they mean. "{0} seed(s) validated" alone was a number with no denominator beside it, so a caller could not
    /// tell it from the seeds they NAMED — while the scope sentence one line up printed a third quantity under the
    /// same word.</para></summary>
    [MustState("finding(s) across", "reached were validated")]
    internal const string DialogueCounts =
        "{0} of the {1} seed(s) reached were validated, {2} topic(s), {3} finding(s) across them.\n";

    /// <summary>What <c>counts_only=true</c> leaves out for this family, stated where the listing would have been —
    /// a mode that renders no blocks must say that is what it did, never look like a validation that found nothing
    /// to show.</summary>
    [MustState("counts_only=true", "no per-topic blocks")]
    internal const string DialogueCountsOnly =
        "counts_only=true: the totals above and the unreachable seeds below, and no per-topic blocks. Drop " +
        "counts_only= to see each topic's findings.\n";

    /// <summary>How many source plugins the roster names before it says how many it did not. Ten is enough to act
    /// on; the count of the rest is what keeps the roster from becoming its own silent cut.</summary>
    internal const int SweepRosterRows = 10;

    /// <summary>How many declaring plugins a COLLECTION field names before it summarises the rest. Three names is
    /// enough to go look at; the rest are a count, because this rides EVERY annotated field of every row.</summary>
    internal const int DeclarerNameCap = 3;

    /// <summary>The precise tier's per-field line, in the voice of the field's SHAPE. Returns null when there is
    /// nothing to say, so the caller has one place to decide rather than two.</summary>
    internal static string? DeclarersNote(OwnedChildShape shape, IReadOnlyList<string> declaring, IReadOnlyList<string> unreadable)
    {
        if (declaring.Count == 0 && unreadable.Count == 0) return null;
        string? head = declaring.Count == 0 ? null
            : shape == OwnedChildShape.Singular
                ? $"{CarriedBy} {declaring.Count} other plugin(s)"
                : $"{DeclaredBy} {string.Join(", ", declaring.Take(DeclarerNameCap))}"
                  + (declaring.Count > DeclarerNameCap ? $" (+{declaring.Count - DeclarerNameCap} more)" : "");
        string? tail = unreadable.Count == 0 ? null
            : $"{unreadable.Count} other plugin(s) touching this record {CouldNotRead} "
              + $"({string.Join(", ", unreadable.Take(DeclarerNameCap))}"
              + (unreadable.Count > DeclarerNameCap ? ", …" : "") + ")";
        return head is null ? tail : tail is null ? head : $"{head}; {tail}";
    }
}

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

    /// <summary>The lead, on a response that carries every finding the sweep found. Stated rather than implied by
    /// silence: "no accounting line" used to mean both "complete" and "the cut landed somewhere that never got to
    /// print one" (#361), and a reader could not tell those apart. Now the line is unconditional over the listing
    /// lane, so its absence means the lane did not run, never that the response is whole.</summary>
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
    [MustState("max_chars=", "raise it to at least")]
    internal const string SweepCapTooSmall =
        " This response is {2} chars, longer than the max_chars={0} it was given: what it must carry whatever the " +
        "budget - its header, the accounting above, the boundary - does not fit in that many chars, so raise it to " +
        "at least {1}.";

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
    [MustState("max_chars=", "raise it to at least")]
    internal const string SweepCapOvershot =
        " This response is {2} chars, longer than the max_chars={0} it was given: what it must carry whatever the " +
        "budget - its header, the accounting above, the boundary - does fit, but one body unit was written before " +
        "its size could be measured and ran past what was left, so raise it to at least {1}.";

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

    /// <summary>The text transport's label for the claim above.</summary>
    [NoClaims("a label; the claim it introduces is SweepBoundary")]
    internal const string SweepBoundaryLabel = "boundary: ";

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

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

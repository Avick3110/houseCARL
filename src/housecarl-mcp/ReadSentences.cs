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
/// </summary>
internal static class ReadSentences
{
    // ---- tier 1: the cheap fact every read can state from the index alone ----------------------------

    /// <summary>The per-field half of the cheap tier. It claims ONLY what the index knows — that other plugins
    /// touch this record and this read did not look at what they declare. It must never imply they DO declare
    /// content (unknown without reading them) nor that they don't.</summary>
    [MustState("were not read")]
    internal const string NotRead = "other plugin(s) touch this record; their declarations for this field were not read";

    /// <summary>The response-level half of the cheap tier, naming the lane that answers precisely. The remedy is
    /// named only because it was RUN: <c>conflict_tree=true</c> fetches every touching body and states which
    /// plugins declare content for these fields, measured on the same order the cost figures came from.</summary>
    [MustState("declared per plugin", "conflict_tree=true")]
    internal const string NotReadClause =
        "note: an annotated field above holds CHILD RECORDS, which are declared per plugin — so what one plugin's " +
        "body carries is not the whole story for that field. This read did not open the other plugins' bodies to " +
        "see what they declare; pass conflict_tree=true for a read that does, and names them.";

    /// <summary>The cheap tier's per-field line: the count the index knows, and the honest limit.</summary>
    internal static string NotReadNote(int others) => $"{others} {NotRead}";

    // ---- tier 2: the precise answer, off bodies the conflict-tree lane already fetched ----------------

    /// <summary>The per-field label for a COLLECTION child. A pure label: on its own it asserts nothing a caller
    /// acts on — the claim is the plugin names it introduces, and their meaning is
    /// <see cref="MergeCollection"/>.</summary>
    [NoClaims("a label; the claim is the plugin names it introduces, and their meaning is MergeCollection")]
    internal const string DeclaredBy = "also declared by";

    /// <summary>The per-field line for a SINGULAR child: a COUNT, deliberately not a name list. Landscape and
    /// TopCell are overridden by hundreds of plugins on a real order ("+483 more" is noise, not information), and
    /// which one wins is a load-order question the conflict tree answers properly.</summary>
    [NoClaims("a label; the claim is SingleResolved, and the count is evidence rather than an assertion")]
    internal const string CarriedBy = "also carried by";

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
    internal const string MergeCollection =
        "note: an annotated Persistent / Temporary / NavigationMeshes / Responses / SubCells field holds MANY child " +
        "records — a cell's placed references, a topic's INFO lines, a worldspace's cells. Those are declared per " +
        "plugin and the game assembles them from every plugin that declares them, so the value shown is one " +
        "plugin's own declaration, not the merged total.";

    /// <summary>The response-level fact for SINGULAR children, which is a DIFFERENT fact. One record, one FormKey:
    /// the plugins that also carry it are overriding each other, and the one the game uses is decided by load
    /// order. What the annotation tells a caller here is that the child EXISTS despite this body not carrying it —
    /// not that anything is being merged.</summary>
    [MustState("ONE child record", "does not remove it")]
    internal const string SingleResolved =
        "note: an annotated Landscape / TopCell field holds ONE child record with its own FormID. Plugins that also " +
        "carry it are overriding each other and load order decides which version the game uses — so this body not " +
        "carrying it does not remove it, and the count above is how many other plugins carry one, not a total.";

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

using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One source per sentence for the read surface's user-facing prose, the <see cref="WriteSentences"/>
/// pattern on the other surface.</summary>
internal static partial class ReadSentences
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

    [NoClaims("an address, not a sentence; the claims about the facegen family are the boundary's own")]
    internal const string FaceGenDocUrl = "https://github.com/Avick3110/houseCARL/blob/main/docs/facegen.md";

    [NoClaims("an address, not a sentence; the claims about the dialogue family are the boundary's own")]
    internal const string DialogueDocUrl = "https://github.com/Avick3110/houseCARL/blob/main/docs/dialogue.md";
}

namespace HousecarlMcp;

/// <summary>WHICH CHECKS A SEED'S KIND ACTUALLY RUNS — one fact, read by the seed's own verdict line and by the
/// family's boundary claim.</summary>
[Flags]
internal enum DialogueChecks
{
    /// <summary>Nothing ran — no seed reached a report.</summary>
    None = 0,

    /// <summary>The CK-parity subrecords of the SEED RECORD itself: a quest's NextAliasID and objective Flags, a
    /// view's DNAM/ENAM, a branch's TNAM/DNAM. Checkable on the record alone.</summary>
    RecordParity = 1,

    /// <summary>Everything that needs an INFO LIST to run against: branch and quest wiring, LinkTo and previous-link
    /// targets, each voiced line's .fuz, each result script, the malformed-condition subset, and the .seq. A DLVW
    /// and a DLBR own no INFO list, so none of it runs for them.</summary>
    TopicGraph = 2,
}

/// <summary>
/// THE PER-KIND CHECK SET, and the sentences that state it.
///
/// <para><b>Why it is a value rather than an <c>InputKind == "quest"</c> test at each site.</b> A DLVW or DLBR seed
/// rendered its head and nothing else — its ONLY check went unstated when it passed, so a caller could not tell it
/// from one that never ran — while the family's boundary went on to assert LinkTo, <c>.fuz</c>, result-script and
/// condition checks that had nothing to run against (round-3 finding A1). Both sites gated on the same literal, and
/// patching either one alone leaves the other saying something the response denies. This is the fact both read:
/// <see cref="For"/> for one seed, and <see cref="CheckOutcome"/>'s union over the seeds a call reached for the
/// family-level claim.</para>
/// </summary>
internal static class DialogueKindChecks
{
    /// <summary>What running this surface on a seed of <paramref name="inputKind"/> checks. An unrecognised kind
    /// claims NOTHING rather than defaulting to the widest set — a kind nobody taught this table about must not
    /// have its boundary assert checks on its behalf.</summary>
    internal static DialogueChecks For(string inputKind) => inputKind switch
    {
        "quest" => DialogueChecks.RecordParity | DialogueChecks.TopicGraph,
        // A DIAL is itself the topic: its parity is per-INFO and is stated inside the topic block, so the seed
        // record carries no parity verdict of its own.
        "topic" => DialogueChecks.TopicGraph,
        "view" or "branch" => DialogueChecks.RecordParity,
        _ => DialogueChecks.None,
    };

    /// <summary>The same fact as DATA, for the json transport — the tokens for the checks a kind runs, in a fixed
    /// order. The text lane says which check ran by PRINTING its verdict; a machine consumer reading an empty
    /// <c>input_issues</c> cannot tell a check that ran and passed from one that never ran, which is the same Q3
    /// gap one transport over. Both come off <see cref="For"/>.</summary>
    internal static string[] Names(DialogueChecks checks)
    {
        var names = new List<string>(2);
        if (checks.HasFlag(DialogueChecks.RecordParity)) names.Add("record_parity");
        if (checks.HasFlag(DialogueChecks.TopicGraph)) names.Add("topic_graph");
        return names.ToArray();
    }

    /// <summary>This kind's verdict line where its record-level parity PASSED, or null for a kind that has no
    /// record-level parity to state. Each kind gets its own sentence rather than one sentence with the subrecord
    /// names substituted in: what the check looked at is the answer, not a detail of it.</summary>
    internal static string? ParityOkLine(string inputKind) => inputKind switch
    {
        "quest" => ReadSentences.DialogueQuestParityOk,
        "view" => ReadSentences.DialogueViewParityOk,
        "branch" => ReadSentences.DialogueBranchParityOk,
        _ => null,
    };
}

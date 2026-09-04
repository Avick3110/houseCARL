namespace HousecarlMcp;

/// <summary>Which checks a seed's kind actually runs. Read both by the seed's own verdict line and by the
/// family's boundary claim, so the two cannot disagree.</summary>
[Flags]
internal enum DialogueChecks
{
    /// <summary>Nothing ran — no seed reached a report.</summary>
    None = 0,

    /// <summary>The CK-parity subrecords of the seed record itself: a quest's NextAliasID and objective Flags, a
    /// view's DNAM/ENAM, a branch's TNAM/DNAM. Checkable on the record alone.</summary>
    RecordParity = 1,

    /// <summary>Everything that needs an INFO list to run against: branch and quest wiring, LinkTo and previous-link
    /// targets, each voiced line's .fuz, each result script, the malformed-condition subset, and the .seq. A DLVW
    /// and a DLBR own no INFO list, so none of it runs for them.</summary>
    TopicGraph = 2,
}

/// <summary>The per-kind check set, and the sentences that state it. One table both sites read — <see cref="For"/>
/// for a single seed, and <see cref="CheckOutcome"/>'s union across the seeds a call reached for the family-level
/// claim — so the seed's verdict and the boundary claim cannot contradict each other.</summary>
internal static class DialogueKindChecks
{
    /// <summary>What running this surface on a seed of <paramref name="inputKind"/> checks. An unrecognised kind
    /// claims nothing rather than defaulting to the widest set — a kind absent from this table must not have the
    /// boundary assert checks on its behalf.</summary>
    internal static DialogueChecks For(string inputKind) => inputKind switch
    {
        "quest" => DialogueChecks.RecordParity | DialogueChecks.TopicGraph,
        // A DIAL is itself the topic: its parity is per-INFO and is stated inside the topic block, so the seed
        // record carries no parity verdict of its own.
        "topic" => DialogueChecks.TopicGraph,
        "view" or "branch" => DialogueChecks.RecordParity,
        _ => DialogueChecks.None,
    };

    /// <summary>The same fact as data for the json transport: the tokens for the checks a kind runs, in a fixed
    /// order. Without it a consumer reading an empty <c>input_issues</c> could not tell a check that ran and passed
    /// from one that never ran. Comes off <see cref="For"/>.</summary>
    internal static string[] Names(DialogueChecks checks)
    {
        var names = new List<string>(2);
        if (checks.HasFlag(DialogueChecks.RecordParity)) names.Add("record_parity");
        if (checks.HasFlag(DialogueChecks.TopicGraph)) names.Add("topic_graph");
        return names.ToArray();
    }

    /// <summary>This kind's verdict line for a passing record-level parity check, or null for a kind that has no
    /// record-level parity to state.</summary>
    internal static string? ParityOkLine(string inputKind) => inputKind switch
    {
        "quest" => ReadSentences.DialogueQuestParityOk,
        "view" => ReadSentences.DialogueViewParityOk,
        "branch" => ReadSentences.DialogueBranchParityOk,
        _ => null,
    };
}

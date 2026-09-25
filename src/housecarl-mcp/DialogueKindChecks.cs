namespace HousecarlMcp;

/// <summary>Which checks a seed's kind actually runs; contract in docs/architecture/dialogue-validation.md.</summary>
[Flags]
internal enum DialogueChecks
{
    /// <summary>Nothing ran — no seed reached a report.</summary>
    None = 0,

    /// <summary>The CK-parity subrecords of the seed record itself, checkable on the record alone.</summary>
    RecordParity = 1,

    /// <summary>Everything that needs an INFO list: branch and quest wiring, link targets, .fuz, result scripts,
    /// the malformed-condition subset, and the .seq.</summary>
    TopicGraph = 2,
}

/// <summary>The per-kind check set, and the sentences that state it.</summary>
internal static class DialogueKindChecks
{
    /// <summary>What this surface checks on a seed of <paramref name="inputKind"/>; an unrecognised kind claims
    /// nothing rather than defaulting to the widest set.</summary>
    internal static DialogueChecks For(string inputKind) => inputKind switch
    {
        "quest" => DialogueChecks.RecordParity | DialogueChecks.TopicGraph,
        // A DIAL's parity is per-INFO and is stated inside the topic block, not on the seed record.
        "topic" => DialogueChecks.TopicGraph,
        "view" or "branch" => DialogueChecks.RecordParity,
        _ => DialogueChecks.None,
    };

    /// <summary>The same fact as data for the json transport: the tokens for the checks a kind runs, in a fixed
    /// order, off <see cref="For"/>.</summary>
    internal static string[] Names(DialogueChecks checks)
    {
        var names = new List<string>(2);
        if (checks.HasFlag(DialogueChecks.RecordParity)) names.Add("record_parity");
        if (checks.HasFlag(DialogueChecks.TopicGraph)) names.Add("topic_graph");
        return names.ToArray();
    }

    /// <summary>This kind's verdict line for a passing record-level parity check, or null where it has none.</summary>
    internal static string? ParityOkLine(string inputKind) => inputKind switch
    {
        "quest" => CheckSentences.DialogueQuestParityOk,
        "view" => CheckSentences.DialogueViewParityOk,
        "branch" => CheckSentences.DialogueBranchParityOk,
        _ => null,
    };
}

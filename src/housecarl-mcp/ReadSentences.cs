namespace HousecarlMcp;

/// <summary>
/// ONE SOURCE PER SENTENCE for the READ surface's user-facing prose — the <see cref="WriteSentences"/> pattern,
/// on the other surface.
///
/// <para><b>Why it starts here.</b> The read surface's response-layer migration is its own scheduled piece of
/// work; this class is not it. It exists because the first read sentence that carries a CLAIM a caller acts on
/// arrived (#342's owned-child annotation), and the write surface's lesson is that a sentence born as a render
/// literal is a sentence that gets copied and then drifts. So it is born here instead, with the same two nets
/// the write sentences have: the CONTENT net (<see cref="MustStateAttribute"/> — the phrases whose loss changes
/// what the caller is told, declared beside the sentence) and a REACH net in the probe that owns the feature
/// (every const observed coming out of a real render, on both transports).</para>
///
/// <para><b>The response/field split.</b> The invariant half — what a child record IS — is a fact about the
/// response, not about one field, so it is stated ONCE per response. Carrying it per field cost 288 chars per
/// annotated field per record, ~275 of them identical on every row: a 500-row cell query spent ~280 KB of an
/// ~80 k budget restating the same sentence, and the rows that got truncated to make room were real data. The
/// per-field annotation therefore carries only what differs per field — WHICH other plugins declare content —
/// and the response carries the meaning.</para>
/// </summary>
internal static class ReadSentences
{
    /// <summary>The response-level fact behind #342's annotation, and the part a caller acts on: an annotated
    /// field shows ONE plugin's declaration of a parent's children, and the game does not treat it as the whole
    /// set.
    ///
    /// <para>Deliberately names NO remedy. The read that would answer "what is actually live in this parent"
    /// (a FormKey-keyed union with each child taken at its own winner) does not exist yet, and the obvious
    /// workaround — re-reading with <c>plugin=</c> — is not load-order truth either: that body's own children can
    /// themselves be overridden further up. A sentence that pointed at either would be promising capability the
    /// surface does not have, so this one states the fact and stops.</para></summary>
    [MustState("declared per plugin", "not the merged total")]
    internal const string OwnedChildMerge =
        "note: an annotated field above holds CHILD RECORDS — a cell's placed references, a topic's INFO lines, " +
        "a worldspace's cells. Those are declared per plugin and the game assembles them from every plugin that " +
        "declares them, so the value shown is one plugin's own declaration, not the merged total.";

    /// <summary>The per-field half's label. A pure label: on its own it asserts nothing a caller acts on — the
    /// claim is the plugin names that follow it, and its meaning is <see cref="OwnedChildMerge"/>.</summary>
    [NoClaims("a label; the claim is the plugin names it introduces, and their meaning is OwnedChildMerge")]
    internal const string DeclaredBy = "also declared by";

    /// <summary>The Q3 half: a plugin touching this record whose body or field could not be read. Stated, never
    /// dropped — an unreadable body silently missing from the list would read as "nobody else declares", which is
    /// the same wrong answer this annotation exists to prevent, one level down.</summary>
    [MustState("could NOT be read")]
    internal const string CouldNotRead = "could NOT be read";

    /// <summary>How many declaring plugins the per-field annotation names before it summarises the rest. Three
    /// names is enough to go look at; the rest are a count, because this rides EVERY annotated field of every row
    /// in a bulk response.</summary>
    internal const int DeclarerNameCap = 3;

    /// <summary>The per-field annotation: which OTHER plugins touching this record declare child content for this
    /// field, capped, plus any that could not be read. Returns null when there is nothing to say — no declarers
    /// and nothing unreadable — so the caller has one place to decide, not two.</summary>
    internal static string? OwnedChildDeclarers(IReadOnlyList<string> declarers, IReadOnlyList<string> unreadable)
    {
        if (declarers.Count == 0 && unreadable.Count == 0) return null;
        var head = declarers.Count == 0
            ? null
            : $"{DeclaredBy} {string.Join(", ", declarers.Take(DeclarerNameCap))}"
              + (declarers.Count > DeclarerNameCap ? $" (+{declarers.Count - DeclarerNameCap} more)" : "");
        var tail = unreadable.Count == 0
            ? null
            : $"{unreadable.Count} other plugin(s) touching this record {CouldNotRead} "
              + $"({string.Join(", ", unreadable.Take(DeclarerNameCap))}"
              + (unreadable.Count > DeclarerNameCap ? ", …" : "") + ")";
        return head is null ? tail : tail is null ? head : $"{head}; {tail}";
    }
}

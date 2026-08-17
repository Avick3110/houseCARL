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
/// (the sentence is observed coming out of a real render, on both transports).</para>
///
/// <para>There is no <c>Twins</c> nested class here and no lane bookkeeping: a read annotation reaches both
/// transports through ONE carrier — <see cref="HousecarlCore.FieldValue.Display"/>, which the text render, the
/// json fields array and the dense cells all read off the same field — so "both lanes state it" is a property of
/// the carrier rather than a claim two independent renders have to keep agreeing on.</para>
/// </summary>
internal static class ReadSentences
{
    /// <summary>The engine fact behind #342's annotation, and the part a caller acts on: what they are looking at
    /// is ONE plugin's declaration of a parent's children, and the game does not treat it as the whole set.
    ///
    /// <para>Deliberately names NO remedy. The read that would answer "what is actually live in this parent"
    /// (a FormKey-keyed union with each child taken at its own winner) does not exist yet, and the obvious
    /// workaround — re-reading with <c>plugin=</c> — is not load-order truth either: that body's own children can
    /// themselves be overridden further up. A sentence that pointed at either would be promising capability the
    /// surface does not have, so this one states the fact and stops.</para></summary>
    [MustState("declared per plugin", "not the merged total")]
    internal const string OwnedChildMerge =
        "child records are declared per plugin and the game assembles them from every plugin that declares them, " +
        "so the value shown here is one plugin's own declaration, not the merged total";

    /// <summary>The #342 annotation: the fact above, in front of the counted evidence for THIS field on THIS
    /// record. <paramref name="others"/> is how many other plugins touching the record declare any content for the
    /// field; <paramref name="most"/>/<paramref name="mostPlugin"/> the largest such declaration — the number that
    /// makes a false-empty read obvious at a glance (a winner reading 0 beside "most: 201 in Skyrim.esm").</summary>
    internal static string OwnedChildContentNote(string field, int others, int most, string mostPlugin) =>
        $"{others} other plugin(s) touching this record also declare {field} content " +
        $"(most: {most} in {mostPlugin}) — {OwnedChildMerge}";
}

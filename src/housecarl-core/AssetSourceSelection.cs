namespace HousecarlCore;

// AssetSourceSelection — the one policy for "which provider do I read an asset from". The source grammar
// has three poles: the VFS winner, a named mod, and the sole provider; every caller that reads bytes out of
// the asset layer passes one of them here rather than spelling the pick itself.
//
// This type decides which provider and nothing else: it reads no bytes, renders no sentence, and knows no
// tool. Every non-Selected verdict carries the provider NAMES, never their on-disk paths — a refusal that
// names a machine path teaches the caller to round-trip absolute paths that go stale between resolve and read.
//
// The named pole is widened rather than joined by a fourth: a name the active universe cannot answer is
// looked for as a mod folder on disk, through a lookup the caller passes in. That widening lives at this one
// decision point, so no caller spells the off-order lane for itself.

/// <summary>Which pole of the source grammar a read is made under.</summary>
public enum AssetSourcePole
{
    /// <summary>Use the only provider; more than one is a refusal (the caller chooses).</summary>
    SoleProvider,
    /// <summary>Use whichever copy currently wins the VFS — "what the game shows right now".</summary>
    Winner,
    /// <summary>Use a NAMED provider's copy, whatever else contends — and, where the caller supplies the off-order
    /// lookup, wherever that provider lives. Absent from that provider is a refusal.</summary>
    Named,
}

/// <summary>A caller's source-pole choice. <see cref="Spelling"/> carries the provider name for the
/// <see cref="AssetSourcePole.Named"/> pole; null for an in-process choice and for the winner pole.</summary>
public sealed record AssetSourceChoice(AssetSourcePole Pole, string? Spelling)
{
    /// <summary>The reserved wire token selecting the VFS-winner pole. Sigiled: '*' is illegal in a Windows file or
    /// folder name, so the pole space and the provider-name space are disjoint by construction and a bare "winner"
    /// always means a provider actually called winner. Same idiom as '@file' in ops=.</summary>
    public const string WinnerToken = "*winner";

    /// <summary>The sole-provider pole (no selector given).</summary>
    public static readonly AssetSourceChoice SoleProvider = new(AssetSourcePole.SoleProvider, null);

    /// <summary>The winner pole, chosen in-process (no wire spelling ⇒ no collision test).</summary>
    public static readonly AssetSourceChoice Winner = new(AssetSourcePole.Winner, null);

    /// <summary>A named provider, chosen in-process.</summary>
    public static AssetSourceChoice Named(string providerName) => new(AssetSourcePole.Named, providerName);

    /// <summary>Parse a caller's selector. Exactly two arms: the reserved <see cref="WinnerToken"/> ⇒ the winner
    /// pole; anything else ⇒ that provider name, matched exactly. Blank ⇒ sole-provider (no selector given). No
    /// ambiguity is possible, so nothing here can refuse — the sigil is what makes the parse total.</summary>
    public static AssetSourceChoice Parse(string? selector)
    {
        var s = selector?.Trim();
        if (string.IsNullOrEmpty(s)) return SoleProvider;
        return s.Equals(WinnerToken, StringComparison.OrdinalIgnoreCase)
            ? Winner
            : new AssetSourceChoice(AssetSourcePole.Named, s);
    }
}

/// <summary>Why a pick did or didn't land on a provider.</summary>
public enum AssetSourceVerdict
{
    /// <summary>A provider was picked — <see cref="AssetSourcePick.Source"/> is non-null.</summary>
    Selected,
    /// <summary>Nothing active provides the path at all.</summary>
    NoProvider,
    /// <summary>Sole-provider pole, but more than one provider contends — the caller must choose.</summary>
    Ambiguous,
    /// <summary>Named pole, and that provider does not supply this path (others may).</summary>
    NamedAbsent,
}

/// <summary>The outcome of a pick. <see cref="Source"/> is non-null iff <see cref="Verdict"/> is
/// <see cref="AssetSourceVerdict.Selected"/>. <see cref="ProviderNames"/> is every contending provider as
/// <see cref="AssetSourceSelection.Describe"/> spells it — NAMES, never on-disk paths — so a caller's refusal can
/// list the real choices without teaching path round-tripping.</summary>
public sealed record AssetSourcePick(
    AssetSourceVerdict Verdict,
    PlacementSource? Source,
    IReadOnlyList<string> ProviderNames)
{
    /// <summary>WHY the off-order lane ended where it did — the typed outcome a refusal keys its sentence to.
    /// No consumer re-derives this from anything else.</summary>
    public OffOrderReason OffOrderReason { get; init; } = OffOrderReason.NotConsulted;

    /// <summary>The NAME of the folder or archive that would not read, and a concise cause — the unreadable
    /// outcome's data, so an absent answer is never rendered as an authoritative "that mod does not have it".
    /// Both null unless <see cref="OffOrderReason"/> is <see cref="HousecarlCore.OffOrderReason.FolderUnreadable"/>.</summary>
    public string? OffOrderUnreadableName { get; init; }
    public string? OffOrderUnreadableCause { get; init; }
}

public static class AssetSourceSelection
{
    /// <summary>The ONE formatter for a provider name in any list a caller reads a selector out of. Every listing
    /// site goes through here for the same reason the sentences have one source: two spellings of "how a name is
    /// shown" is two chances for the shown form to stop being the accepted form.
    /// <para>DOUBLE quotes, with the kind outside them. The boundary has to be a character a provider name cannot
    /// contain, or it is not a boundary: single quotes failed that test — <c>JK's Skyrim</c> is a real and widely
    /// installed mod, and it dissolved the delimiter mid-name. Windows forbids '"' in a file or folder name, so
    /// double quotes cannot occur inside one. Same construction as the '*' sigil on the pole token.</para>
    /// <para>The name must be copyable verbatim out of a message and back into a selector, so the kind stays outside
    /// the quotes; a CI round-trip over hostile names feeds these strings back through the real tool.</para></summary>
    public static string Describe(PlacementSource s) => $"\"{s.ProviderName}\" ({(s.Kind == AssetKind.Bsa ? "BSA" : "loose")})";

    /// <summary>Pick the provider to read from, under <paramref name="choice"/>'s pole. Every refusal verdict carries
    /// the provider names so the caller can render its own.
    ///
    /// <para><paramref name="offOrderLookup"/> is the off-order lane: a NAMED provider the active universe has no
    /// answer for is looked for as a mod folder on disk. Passing it is what makes a named source reachable regardless
    /// of the MO2 tick; omitting it leaves the pure, universe-only policy every other caller wants. It is consulted
    /// ONLY under the Named pole — naming is the consent, so an omitted provider, the winner pole and the contention
    /// listing all stay strictly inside the built universe, and a mod nobody named can never contend silently.</para>
    ///
    /// <para>The lookup itself is the caller's I/O; this type still decides WHICH provider and renders nothing.</para></summary>
    public static AssetSourcePick Select(PlacementResolution res, AssetSourceChoice choice,
                                         Func<string?, OffOrderLookup>? offOrderLookup = null)
    {
        var names = new List<string>(res.Sources.Count);
        foreach (var s in res.Sources) names.Add(Describe(s));

        // The NAMED pole must be answered FIRST, ahead of the empty-universe return below: its answer does not depend
        // on how many providers the active universe has, and the disabled-mod case is exactly a path nothing enabled
        // supplies — under that return the caller's name would never be consulted at all.
        if (choice.Pole == AssetSourcePole.Named)
        {
            foreach (var s in res.Sources)
                if (string.Equals(s.ProviderName, choice.Spelling ?? "", StringComparison.OrdinalIgnoreCase))
                    return new AssetSourcePick(AssetSourceVerdict.Selected, s, names);
            var off = offOrderLookup?.Invoke(choice.Spelling) ?? OffOrderLookup.NotConsulted;
            if (off.Source is { } offOrder)
                return new AssetSourcePick(AssetSourceVerdict.Selected, offOrder, names);
            // Which refusal turns on one question — does anything else supply the path — so the caller's remedy can
            // only offer names it actually has. What the lookup did rides along, because the refusal has to say which
            // places were searched and cannot infer that from an absent source.
            return new AssetSourcePick(
                res.Sources.Count == 0 ? AssetSourceVerdict.NoProvider : AssetSourceVerdict.NamedAbsent, null, names)
            {
                OffOrderReason = off.Reason,
                OffOrderUnreadableName = off.UnreadableName,
                OffOrderUnreadableCause = off.UnreadableCause,
            };
        }

        if (res.Sources.Count == 0)
            return new AssetSourcePick(AssetSourceVerdict.NoProvider, null, names);

        switch (choice.Pole)
        {
            case AssetSourcePole.Winner:
                // No collision test, and none possible: the pole token carries a '*', which no provider name can.
                return new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names);

            default:
                // Sole-provider: contention is the caller's call, not ours. Counted off Sources rather than read off
                // PlacementResolution.Ambiguous so the pick turns on one fact — how many providers there are — and
                // cannot drift if that flag's meaning changes.
                return res.Sources.Count == 1
                    ? new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names)
                    : new AssetSourcePick(AssetSourceVerdict.Ambiguous, null, names);
        }
    }
}

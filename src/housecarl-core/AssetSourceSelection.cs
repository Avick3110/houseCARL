namespace HousecarlCore;

// AssetSourceSelection — the one policy for which provider an asset is read from: three poles, the named one
// widened by the off-order lane; the grammar and its rules are in docs/architecture/assets.md.

public enum AssetSourcePole
{
    /// <summary>Use the only provider; more than one is a refusal (the caller chooses).</summary>
    SoleProvider,
    /// <summary>Use whichever copy currently wins the VFS — "what the game shows right now".</summary>
    Winner,
    /// <summary>Use a NAMED provider's copy wherever it lives. Absent from that provider is a refusal.</summary>
    Named,
}

/// <summary>A caller's source-pole choice; <see cref="Spelling"/> carries the provider name for the named pole.</summary>
public sealed record AssetSourceChoice(AssetSourcePole Pole, string? Spelling)
{
    /// <summary>The reserved wire token selecting the VFS-winner pole — sigiled, so pole and provider names are disjoint.</summary>
    public const string WinnerToken = "*winner";

    public static readonly AssetSourceChoice SoleProvider = new(AssetSourcePole.SoleProvider, null);

    public static readonly AssetSourceChoice Winner = new(AssetSourcePole.Winner, null);

    public static AssetSourceChoice Named(string providerName) => new(AssetSourcePole.Named, providerName);

    /// <summary>Parse a caller's selector: the reserved <see cref="WinnerToken"/>, else that provider name; blank is sole-provider.</summary>
    public static AssetSourceChoice Parse(string? selector)
    {
        var s = selector?.Trim();
        if (string.IsNullOrEmpty(s)) return SoleProvider;
        return s.Equals(WinnerToken, StringComparison.OrdinalIgnoreCase)
            ? Winner
            : new AssetSourceChoice(AssetSourcePole.Named, s);
    }
}

public enum AssetSourceVerdict
{
    Selected,
    /// <summary>Nothing active provides the path at all.</summary>
    NoProvider,
    /// <summary>Sole-provider pole, but more than one provider contends — the caller must choose.</summary>
    Ambiguous,
    /// <summary>Named pole, and that provider does not supply this path (others may).</summary>
    NamedAbsent,
}

/// <summary>The outcome of a pick. <see cref="Source"/> is non-null iff Selected; <see cref="ProviderNames"/> is
/// every contender as <see cref="AssetSourceSelection.Describe"/> spells it — names, never on-disk paths.</summary>
public sealed record AssetSourcePick(
    AssetSourceVerdict Verdict,
    PlacementSource? Source,
    IReadOnlyList<string> ProviderNames)
{
    /// <summary>WHY the off-order lane ended where it did — the typed outcome a refusal keys its sentence to.</summary>
    public OffOrderReason OffOrderReason { get; init; } = OffOrderReason.NotConsulted;

    /// <summary>The name of the folder or archive that would not read, and a concise cause; both null unless the reason is FolderUnreadable.</summary>
    public string? OffOrderUnreadableName { get; init; }
    public string? OffOrderUnreadableCause { get; init; }
}

public static class AssetSourceSelection
{
    /// <summary>The ONE formatter for a provider name in any list a caller reads a selector out of: the name in
    /// DOUBLE quotes with the kind outside them, so the printed token is the token a selector accepts.</summary>
    public static string Describe(PlacementSource s) => Describe(s.ProviderName, s.Kind == AssetKind.Bsa ? "BSA" : "loose");

    /// <summary>The same formatter for a caller that already holds the name and a rendered kind label.</summary>
    public static string Describe(string providerName, string kindLabel) => $"\"{providerName}\" ({kindLabel})";

    /// <summary>Does <paramref name="name"/> address this source — its provider name, or for a BSA the MO2 layer the archive lives in?</summary>
    public static bool NameMatches(PlacementSource s, string name)
        => string.Equals(s.ProviderName, name, StringComparison.OrdinalIgnoreCase)
        || (s.OwningMod is { } owner && string.Equals(owner, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pick the provider to read from under <paramref name="choice"/>'s pole. <paramref name="offOrderLookup"/>
    /// is the off-order lane, consulted ONLY under the Named pole; this type renders nothing.</summary>
    public static AssetSourcePick Select(PlacementResolution res, AssetSourceChoice choice,
                                         Func<string?, OffOrderLookup>? offOrderLookup = null)
    {
        var names = new List<string>(res.Sources.Count);
        foreach (var s in res.Sources) names.Add(Describe(s));

        // The NAMED pole is answered FIRST, ahead of the empty-universe return: a disabled mod's copy is exactly a path nothing enabled supplies.
        if (choice.Pole == AssetSourcePole.Named)
        {
            // Winner-first order decides among a mod's own copies: its loose file before its own archive.
            foreach (var s in res.Sources)
                if (NameMatches(s, choice.Spelling ?? ""))
                    return new AssetSourcePick(AssetSourceVerdict.Selected, s, names);
            var off = offOrderLookup?.Invoke(choice.Spelling) ?? OffOrderLookup.NotConsulted;
            if (off.Source is { } offOrder)
                return new AssetSourcePick(AssetSourceVerdict.Selected, offOrder, names);
            // Which refusal turns on whether anything else supplies the path, and what the lookup did rides along.
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
                return new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names);

            default:
                // Sole-provider: contention is the caller's call. Counted off Sources, so the pick turns on one fact.
                return res.Sources.Count == 1
                    ? new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names)
                    : new AssetSourcePick(AssetSourceVerdict.Ambiguous, null, names);
        }
    }
}

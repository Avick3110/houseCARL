namespace HousecarlCore;

// ======================================================================
//  AssetSourceSelection — the ONE policy for "which provider do I read an asset from".
//
//  S2's SOURCE grammar has three poles (SPEC §1): the VFS winner, a NAMED mod, and the
//  sole provider. Every caller that reads bytes out of the asset layer picks one of them,
//  and before this type each caller spelled the pick itself — NpcAppearanceAssets took
//  Sources[0] inline, place_asset took Sources[0] behind its own ambiguity test. Two
//  spellings of "how to read a contended source" is the drift class one layer up from the
//  sentence twins, so the pick lives here and the callers pass a pole.
//
//  This type decides WHICH provider, and nothing else: it reads no bytes, renders no
//  sentence, and knows no tool. The verdict enum is the whole vocabulary a caller needs to
//  render its own refusal — every non-Selected verdict carries the provider NAMES (never
//  their on-disk paths; naming a machine path in a refusal is how the caller learns to
//  round-trip absolute paths that go stale between resolve and read).
// ======================================================================

/// <summary>Which pole of S2's SOURCE grammar a read is made under.</summary>
public enum AssetSourcePole
{
    /// <summary>Use the only provider; more than one is a refusal (the caller chooses).</summary>
    SoleProvider,
    /// <summary>Use whichever copy currently wins the VFS — "what the game shows right now".</summary>
    Winner,
    /// <summary>Use a NAMED provider's copy, whatever else contends. Absent from that provider is a refusal.</summary>
    Named,
}

/// <summary>A caller's source-pole choice. <see cref="Spelling"/> is the caller's own token when the choice came
/// from wire input (used only to detect the winner-token collision below); null for an in-process choice.</summary>
public sealed record AssetSourceChoice(AssetSourcePole Pole, string? Spelling)
{
    /// <summary>The reserved wire token selecting the VFS-winner pole.</summary>
    public const string WinnerToken = "winner";

    /// <summary>The sole-provider pole (no selector given).</summary>
    public static readonly AssetSourceChoice SoleProvider = new(AssetSourcePole.SoleProvider, null);

    /// <summary>The winner pole, chosen in-process (no wire spelling ⇒ no collision test).</summary>
    public static readonly AssetSourceChoice Winner = new(AssetSourcePole.Winner, null);

    /// <summary>A named provider, chosen in-process.</summary>
    public static AssetSourceChoice Named(string providerName) => new(AssetSourcePole.Named, providerName);

    /// <summary>Parse a caller's selector: blank ⇒ sole-provider, the reserved <see cref="WinnerToken"/> ⇒ winner,
    /// anything else ⇒ that provider name. The spelling is KEPT so <see cref="AssetSourceSelection.Select"/> can
    /// refuse rather than guess when a real provider is itself named "winner".</summary>
    public static AssetSourceChoice Parse(string? selector)
    {
        var s = selector?.Trim();
        if (string.IsNullOrEmpty(s)) return SoleProvider;
        return s.Equals(WinnerToken, StringComparison.OrdinalIgnoreCase)
            ? new AssetSourceChoice(AssetSourcePole.Winner, s)
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
    /// <summary>The caller typed the reserved winner token AND a real provider is named "winner" — refuse, don't guess.</summary>
    WinnerTokenCollision,
}

/// <summary>The outcome of a pick. <see cref="Source"/> is non-null iff <see cref="Verdict"/> is
/// <see cref="AssetSourceVerdict.Selected"/>. <see cref="ProviderNames"/> is every contending provider as
/// <c>asset_status</c> renders it ("ModX (loose)" / "Y.bsa (BSA)") — NAMES, never on-disk paths — so a caller's
/// refusal can list the real choices without teaching path round-tripping.</summary>
public sealed record AssetSourcePick(
    AssetSourceVerdict Verdict,
    PlacementSource? Source,
    IReadOnlyList<string> ProviderNames);

public static class AssetSourceSelection
{
    /// <summary>How one provider is NAMED to a caller — the same spelling <c>asset_status</c> renders, because a
    /// refusal that lists providers is telling the caller what to pass back as the selector.</summary>
    public static string Describe(PlacementSource s) => $"{s.ProviderName} ({(s.Kind == AssetKind.Bsa ? "BSA" : "loose")})";

    /// <summary>Pick the provider to read from, under <paramref name="choice"/>'s pole. Pure: no I/O, no bytes, no
    /// message. Every refusal verdict carries the provider names so the caller can render its own.</summary>
    public static AssetSourcePick Select(PlacementResolution res, AssetSourceChoice choice)
    {
        var names = new List<string>(res.Sources.Count);
        foreach (var s in res.Sources) names.Add(Describe(s));

        if (res.Sources.Count == 0)
            return new AssetSourcePick(AssetSourceVerdict.NoProvider, null, names);

        switch (choice.Pole)
        {
            case AssetSourcePole.Winner:
                // The token collision: only reachable when the pole came off the wire as the literal "winner" AND a
                // provider really carries that name. Refusing is the Q3 call — either reading would be a silent guess
                // at which of the two the caller meant.
                if (choice.Spelling is not null)
                    foreach (var s in res.Sources)
                        if (string.Equals(s.ProviderName, choice.Spelling, StringComparison.OrdinalIgnoreCase))
                            return new AssetSourcePick(AssetSourceVerdict.WinnerTokenCollision, null, names);
                return new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names);

            case AssetSourcePole.Named:
                foreach (var s in res.Sources)
                    if (string.Equals(s.ProviderName, choice.Spelling ?? "", StringComparison.OrdinalIgnoreCase))
                        return new AssetSourcePick(AssetSourceVerdict.Selected, s, names);
                return new AssetSourcePick(AssetSourceVerdict.NamedAbsent, null, names);

            default:
                // Sole-provider: contention is the caller's call, not ours. Counted off Sources rather than read off
                // PlacementResolution.Ambiguous so the pick depends on one fact (how many providers there are) —
                // the flag means the same thing today, and this way it cannot drift into meaning something else.
                return res.Sources.Count == 1
                    ? new AssetSourcePick(AssetSourceVerdict.Selected, res.Sources[0], names)
                    : new AssetSourcePick(AssetSourceVerdict.Ambiguous, null, names);
        }
    }
}

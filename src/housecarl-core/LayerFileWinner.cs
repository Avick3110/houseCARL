namespace HousecarlCore;

/// <summary>Which copy of a runtime-layer config file the game reads, off one pinned asset view: the MO2 winner, that
/// winner's loose path, the loose copies it shadows in precedence order, and why a DLL that reads the filesystem only
/// does not read it. One rule for every format's discovery.</summary>
public readonly record struct LayerFileWinner(
    string? Provider,
    string? LooseFilePath,
    IReadOnlyList<string> Shadowed,
    string? NotApplied)
{
    /// <summary>The winner of <paramref name="rel"/>; <paramref name="loader"/> names the DLL in the not-applied sentence.</summary>
    public static LayerFileWinner Of(AssetResolver.AssetView view, string rel, string loader)
    {
        var sources = view.ResolveForPlacement(rel).Sources;
        var winner = sources.Count > 0 ? sources[0] : null;
        return new LayerFileWinner(
            winner?.ProviderName,
            winner is { Kind: AssetKind.Loose } ? winner.LooseFilePath : null,
            sources.Where(s => s.Kind == AssetKind.Loose).Skip(1).Select(s => s.ProviderName).ToList(),
            winner is { Kind: AssetKind.Loose }
                ? null
                : $"present only inside a BSA — {loader} reads loose INIs off the filesystem only");
    }
}

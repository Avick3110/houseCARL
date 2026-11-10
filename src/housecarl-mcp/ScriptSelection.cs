using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A script selection over one asset view: the Data paths in order (the explicit paths first, one each as
/// given, then the <c>under=</c> or whole-provider matches that are <c>.pex</c>), how many matched files were not
/// scripts, the selector notes, and the refusal under which nothing was selected.</summary>
internal sealed record ScriptSelectionData(AssetResolver.AssetView View, IReadOnlyList<string> Scripts, int NotScripts,
                                           IReadOnlyList<string> Notes, string? Refusal = null);

/// <summary>The copy of one selected script a decompile reads: the source, the winner's description when the source
/// is not the winner, or why there is no copy to read.</summary>
internal readonly record struct ScriptPick(PlacementSource? Source, string? Winner, string? Error);

internal sealed partial class AssetLayers
{
    /// <summary>The scripts the shared selection names (<see cref="FileSelection.Select"/>, asset_status's selectors
    /// and refusals), over one captured view: explicit paths as given, then each match that is a <c>.pex</c>; a
    /// match that is not is counted, not listed. The path bound is asset_status's; a stop is refused here in decompile's words.</summary>
    internal ScriptSelectionData SelectScripts(IReadOnlyList<string> relPaths, IReadOnlyList<string>? under, string? provider)
    {
        var capture = CaptureForSelection(null, null);
        var view = capture.Captured.View;
        var chosen = FileSelection.Select(capture, relPaths, under, provider, MaxAssetPaths);
        ScriptSelectionData Refused(string why) => new(view, Array.Empty<string>(), 0, chosen.Notes, why);
        if (chosen.Refusal is { } unknown) return Refused(unknown);
        if (chosen.StoppedAt is { } stoppedAt)
            return Refused($"error: the selection names at least {stoppedAt:N0} paths, past the {MaxAssetPaths:N0}-path bound one " +
                           "call is given, so nothing was decompiled — narrow it with a tighter under= selector " +
                           "(e.g. 'scripts/**/*.pex') or fewer paths= entries.");
        var scripts = new List<string>(chosen.Entries.Count);
        int notScripts = 0;
        for (int i = 0; i < chosen.Entries.Count; i++)
        {
            // The first relPaths.Count entries are the explicit paths, one each, in the order given; only matches are filtered.
            var path = chosen.Entries[i].Path;
            if (i >= relPaths.Count && !path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) { notScripts++; continue; }
            scripts.Add(path);
        }
        return new ScriptSelectionData(view, scripts, notScripts, chosen.Notes);
    }

    /// <summary>The copy of <paramref name="rel"/> to decompile: the winner, or the one <paramref name="sourceProvider"/>
    /// names through the one source policy (a disabled mod included), or the sentence saying why there is none.</summary>
    internal static ScriptPick PickScript(AssetResolver.AssetView view, string rel, string? sourceProvider)
    {
        PlacementResolution place;
        try { place = view.ResolveForPlacement(rel); }
        catch (ArgumentException ex) { return new(null, null, $"invalid path — {ex.Message}"); }
        string? WinnerUnlessIs(PlacementSource s) =>
            place.Sources.Count > 0 && !ReferenceEquals(place.Sources[0], s) ? AssetSourceSelection.Describe(place.Sources[0]) : null;
        if (!string.IsNullOrWhiteSpace(sourceProvider))
        {
            var choice = AssetSourceChoice.Parse(sourceProvider.Trim());
            var pick = AssetSourceSelection.Select(place, choice, n => view.TryResolveOffOrderProvider(n, rel));
            if (pick.Verdict == AssetSourceVerdict.Selected) return new(pick.Source, WinnerUnlessIs(pick.Source!), null);
            if (choice.Pole == AssetSourcePole.Named)
                return new(null, null, WriteSentences.PlaceSourceNamedAbsent(
                    sourceProvider.Trim(), rel, pick.ProviderNames, pick.OffOrderReason, pick.OffOrderUnreadableName,
                    pick.OffOrderUnreadableCause, null, place.ReadIncomplete, pick.OffOrderHidden));
        }
        if (place.Sources.Count == 0)
            return new(null, null, "ABSENT — " + AbsentWords.Why(place.Hidden, $"'{rel}'") + ", so there is no copy to decompile.");
        return new(place.Sources[0], null, null);
    }
}

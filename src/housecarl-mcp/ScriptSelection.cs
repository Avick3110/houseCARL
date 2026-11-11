using HousecarlCore;

namespace HousecarlMcp;

/// <summary>A script selection over one asset view: the entries in order (the explicit paths first, one each as
/// given unless <c>exclude=</c> removed it, then the <c>under=</c> or whole-provider matches that are <c>.pex</c>), how
/// many matched files were not scripts, the selector notes, and the refusal under which nothing was selected.</summary>
internal sealed record ScriptSelectionData(AssetResolver.AssetView View, IReadOnlyList<SelectedFile> Scripts, int NotScripts,
                                           IReadOnlyList<string> Notes, string? Refusal = null);

/// <summary>The copy of one selected script a decompile reads: the source, the winner's description when the source
/// is not the winner, or why there is no copy to read.</summary>
internal readonly record struct ScriptPick(PlacementSource? Source, string? Winner, string? Error);

internal sealed partial class AssetLayers
{
    /// <summary>The scripts the shared selection names (<see cref="FileSelection.Select"/>, asset_status's selectors
    /// and refusals), over one captured view: explicit paths as given, then each match that is a <c>.pex</c>; a
    /// match that is not is counted, not listed. The path bound is asset_status's; a stop is refused here in decompile's words.</summary>
    internal ScriptSelectionData SelectScripts(IReadOnlyList<string> relPaths, IReadOnlyList<string>? under, string? provider,
                                               FileExclusion? exclude = null)
    {
        var capture = CaptureForSelection(null, null);
        var view = capture.Captured.View;
        var chosen = FileSelection.Select(capture, relPaths, under, provider, MaxAssetPaths, exclude: exclude);
        ScriptSelectionData Refused(string why) => new(view, Array.Empty<SelectedFile>(), 0, chosen.Notes, why);
        if (chosen.Refusal is { } unknown) return Refused(unknown);
        if (chosen.StoppedAt is { } stoppedAt)
            return Refused(FileSelection.BoundRefusal(stoppedAt, MaxAssetPaths, "decompiled", "scripts/**/*.pex"));
        var scripts = new List<SelectedFile>(chosen.Entries.Count);
        int notScripts = 0;
        foreach (var f in chosen.Entries)
        {
            // The explicit paths are kept as given; only matches are filtered.
            if (f.Given < 0 && !f.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase)) { notScripts++; continue; }
            scripts.Add(f);
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

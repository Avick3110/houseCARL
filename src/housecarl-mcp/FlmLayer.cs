using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The FLM layer: its _FLM.ini files read by the one distributor reader with FLM's split, passes and fill order, after
// BOS's, so the one EditorID table serves every format the call reads.
internal sealed partial class AssetLayers
{
    /// <summary>Read FLM's layer in an open replay context (<see cref="DistributorLayer"/>): FLM's names cross files,
    /// which the one read of every line gives it, and each kept line's adds are placed in FLM's fill order.</summary>
    internal DistributorLayerData FlmLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, string? filter,
                                           Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Flm;
        var ctx = new FlmLines.Context(replay.Forms, view.WinnerClassOf, replay.ListItemsOf, format);
        return DistributorLayer(format, replay, filter, wanted, namedPlugins, formIdsGiven, ct,
            (text, file) => FlmLines.Parse(text, format, file),
            lines => FlmLines.ReadAll(lines, ctx),
            files => FlmLines.Rows(files, ctx));
    }
}

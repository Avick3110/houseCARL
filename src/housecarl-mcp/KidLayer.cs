using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The KID layer: its _KID.ini files read by the one distributor reader with KID's split and read, after SPID's, so the
// one EditorID table serves SkyPatcher's, SPID's and KID's lines.
internal sealed partial class AssetLayers
{
    /// <summary>Read KID's layer in an open replay context (<see cref="DistributorLayer"/>): KID creates every line's
    /// keyword before it reads any filter, which the one read of every line gives it.</summary>
    internal DistributorLayerData KidLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, string? filter,
                                           Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Kid;
        return DistributorLayer(format, replay, filter, wanted, namedPlugins, formIdsGiven, ct,
            (text, _) => KidLines.Parse(text, format),
            lines => KidLines.ReadAll(lines, new KidLines.Context(replay.Forms, view.WinnerClassOf, format)));
    }
}

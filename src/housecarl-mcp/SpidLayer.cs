using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The SPID layer: its _DISTR.ini files read by the one distributor reader with SPID's split, read and first-wins order.
internal sealed partial class AssetLayers
{
    /// <summary>Read SPID's layer in an open replay context (<see cref="DistributorLayer"/>), each first-wins line
    /// placed in its type's order.</summary>
    internal DistributorLayerData SpidLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, string? filter,
                                            Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Spid;
        var g = format.Lines!;
        return DistributorLayer(format, replay, filter, wanted, namedPlugins, formIdsGiven, ct,
            text => SpidLines.Parse(text, g),
            lines => SpidLines.ReadAll(lines, new SpidLines.Context(replay.Forms, view.WinnerClassOf, format)),
            files => SpidLines.Places(files, g).ToDictionary(kv => kv.Key, kv => SpidWire.PlaceRow(kv.Value)));
    }
}

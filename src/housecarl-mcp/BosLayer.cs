using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

// The BOS layer: its _SWAP.ini files read by the one distributor reader with BOS's split, read and last-wins order,
// after KID's, so the one EditorID table serves every format the call reads.
internal sealed partial class AssetLayers
{
    /// <summary>Read BOS's layer in an open replay context (<see cref="DistributorLayer"/>), each kept line placed in
    /// BOS's order for its base.</summary>
    internal DistributorLayerData BosLayer(SkyPatcherReplay replay, LoadOrderResolver.IndexView view, string? filter,
                                           Func<FormKey, bool>? wanted, IReadOnlyList<string>? namedPlugins, bool formIdsGiven, CancellationToken ct)
    {
        var format = RuntimeFormats.Bos;
        return DistributorLayer(format, replay, filter, wanted, namedPlugins, formIdsGiven, ct,
            (text, file) => BosLines.Parse(text, format, file),
            lines => BosLines.ReadAll(lines, new BosLines.Context(replay.Forms, view.WinnerClassOf, format)),
            files => BosLines.Places(files).ToDictionary(kv => kv.Key, kv => BosWire.PlaceRow(kv.Value)));
    }
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer below its floor (the header, the folder-cut and sections-omitted lines, the notes
/// marker, the caveats) refuses naming the max_chars that clears it, instead of shipping over the cap (#986).</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherLayerFloorTests
{
    static SkyPatcherDiscovery.IniFile Ini(string sub, int i) =>
        new(RelPath: sub + "\\" + $"Mod{i:D3}.ini", Subfolder: sub, SortKey: $"Mod{i:D3}.ini", WinningProvider: $"Provider {i:D3}",
            LooseFilePath: "C:\\mods\\p\\" + $"Mod{i:D3}.ini", ShadowedProviders: Array.Empty<string>(), GatePlugin: null, NotApplied: null,
            Lines: Enumerable.Range(1, 4).Select(n => new SkyPatcherLine($"filterByNpcs=Skyrim.esm|{n:X5}:health={n * 10}",
                SkyPatcherLineKind.Patch, Array.Empty<SkyPatcherSegment>(), null)).ToArray());

    /// <summary>Two type folders, every report section, six 110-char scan notes and, optionally, 40 warnings.</summary>
    static SkyPatcherLayerData Layer(bool warnings)
    {
        var entries = Enumerable.Range(1, 2)
            .Select(n => new SkyPatcherConflicts.SkyPatcherConflictEntry($"C:\\mods\\p\\Mod{n:D3}.ini", n, "health", (n * 7).ToString(), false))
            .ToArray();
        var folders = new[] { "npc", "weapon" }.Select(sub => new SkyPatcherDiscovery.FolderScan(sub, Catalog: null, PatchingEnabled: true,
            Files: Enumerable.Range(1, 5).Select(i => Ini(sub, i)).ToArray())).ToArray();
        return new SkyPatcherLayerData(
            new SkyPatcherDiscovery.LayerScan(folders, Enumerable.Range(1, 6).Select(i => $"scan note {i} ".PadRight(110, 'n')).ToArray(),
                ReadIncomplete: false, new Dictionary<string, bool>()),
            Conflicts: Enumerable.Range(1, 3).Select(i => new SkyPatcherConflicts.SkyPatcherConflict("npc", "health", $"Skyrim.esm|{i:X6}", entries)).ToArray(),
            Itms: Enumerable.Range(1, 3).Select(i => new SkyPatcherConflicts.SkyPatcherItm("npc", "health", $"C:\\mods\\p\\Mod{i:D3}.ini",
                new[] { new SkyPatcherConflicts.SkyPatcherItmEntry(1, "health", "5", "Skyrim.esm|0001A696", false, new[] { 2 }) })).ToArray(),
            Duplicates: Enumerable.Range(1, 3).Select(i => new SkyPatcherConflicts.SkyPatcherDuplicate("npc", "health", $"Skyrim.esm|{i:X6}", entries)).ToArray(),
            NoOps: Enumerable.Range(1, 3).Select(i => new SkyPatcherNoOpWrite("npc", $"{i:X6}:Skyrim.esm", $"EditorId{i}", "Health",
                $"C:\\mods\\p\\Mod{i:D3}.ini", i, "health", "10", "10")).ToArray(),
            NoOpNotes: Array.Empty<string>(), RootFailures: Array.Empty<string>(), ReadIncomplete: false,
            AssetWarnings: warnings ? Enumerable.Range(1, 40).Select(i => $"warning {i}: " + new string('w', 120)).ToList() : Array.Empty<string>(),
            ProfileName: "Default");
    }

    /// <summary>The issue's three shapes (unfiltered, filter=npc, a filter matching nothing), with and without warnings: 200
    /// refuses, and the same call at the cap it names is served inside it.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("npc", false)]
    [InlineData("zzz", false)]
    [InlineData(null, true)]
    [InlineData("npc", true)]
    [InlineData("zzz", true)]
    public void BelowTheFloorRefusesAndTheNamedCapServes(string? filter, bool warnings)
    {
        var d = Layer(warnings);

        RenderFloorAssert.RefusesAndTheNamedCapFits(SkyPatcherWire.RenderLayer(d, filter, 200), 200,
                                                    n => SkyPatcherWire.RenderLayer(d, filter, n));
    }

    /// <summary>A filtered refusal offers dropping the filter where the unfiltered layer fits the cap it was given.</summary>
    [Fact]
    public void AFilteredRefusalOffersOmittingTheFilterWhereTheUnfilteredLayerFits()
    {
        var d = Layer(warnings: false);
        int unfilteredFits = RenderFloorAssert.Named(SkyPatcherWire.RenderLayer(d, null, 200));

        var text = SkyPatcherWire.RenderLayer(d, "npc", unfilteredFits);

        Assert.True(RenderFloorAssert.IsFloorRefusal(text), text);
        Assert.Contains("omit filter=", text);
    }

    /// <summary>...and does not offer it where the unfiltered layer is refused at that cap too.</summary>
    [Fact]
    public void AFilteredRefusalDoesNotOfferOmittingTheFilterWhereTheUnfilteredLayerIsRefusedToo()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(warnings: false), "npc", 200);

        Assert.True(RenderFloorAssert.IsFloorRefusal(text), text);
        Assert.DoesNotContain("omit filter=", text);
    }
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer's filter=. A filter that matches no INI must say so; returning the
/// unfiltered overview instead is indistinguishable from a call that passed no filter at all.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherLayerFilterTests
{
    static SkyPatcherDiscovery.IniFile Ini(string subfolder, string name, string provider) =>
        new(RelPath: subfolder + "\\" + name, Subfolder: subfolder, SortKey: name, WinningProvider: provider,
            LooseFilePath: "C:\\mods\\" + provider + "\\" + name, ShadowedProviders: Array.Empty<string>(),
            GatePlugin: null, NotApplied: null,
            Lines: new[] { new SkyPatcherLine("filterByNpcs=Skyrim.esm|1A696:health=200", SkyPatcherLineKind.Patch,
                                              Array.Empty<SkyPatcherSegment>(), null) });

    static SkyPatcherLayerData Layer(params SkyPatcherDiscovery.FolderScan[] folders) =>
        new(new SkyPatcherDiscovery.LayerScan(folders, Array.Empty<string>(), ReadIncomplete: false,
                new Dictionary<string, bool>()),
            Conflicts: Array.Empty<SkyPatcherConflicts.SkyPatcherConflict>(),
            Itms: Array.Empty<SkyPatcherConflicts.SkyPatcherItm>(),
            Duplicates: Array.Empty<SkyPatcherConflicts.SkyPatcherDuplicate>(),
            NoOps: Array.Empty<SkyPatcherNoOpWrite>(), NoOpNotes: Array.Empty<string>(),
            ReadIncomplete: false, AssetWarnings: Array.Empty<string>(), ProfileName: "Default");

    static SkyPatcherLayerData OneNpcFolder() =>
        Layer(new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true,
                  Files: new[] { Ini("npc", "Bandits.ini", "Bandit Overhaul") }));

    [Fact]
    public void AFilterMatchingNoIniSaysSoInsteadOfTheOverview()
    {
        var text = SkyPatcherWire.RenderLayer(OneNpcFolder(), "weapon", 80_000);

        Assert.Contains("0 of 1 INI(s) match", text);
        Assert.Contains("'weapon'", text);
        Assert.Contains("npc", text);                        // the folders that ARE there
        Assert.DoesNotContain("Bandits.ini", text);          // not the overview under another name
    }

    [Fact]
    public void AFilterMatchingNothingInAnEmptyLayerSaysTheLayerIsEmpty()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(), "weapon", 80_000);

        Assert.Contains("0 of 0 INI(s) match", text);
        Assert.Contains("no SkyPatcher INIs at all", text);
    }

    [Fact]
    public void AFilterThatMatchesStillExpandsTheFile()
    {
        var text = SkyPatcherWire.RenderLayer(OneNpcFolder(), "npc", 80_000);

        Assert.Contains("Bandits.ini", text);
        Assert.Contains("health=200", text);
        Assert.DoesNotContain("nothing matched", text);
    }
}

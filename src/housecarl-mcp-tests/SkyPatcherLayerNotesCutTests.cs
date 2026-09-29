using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer's scan notes after the report sections: once the body spends max_chars, a cut
/// note is counted with the zero-match block's marker, never dropped without a word (#894).</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherLayerNotesCutTests
{
    const string Target = "Skyrim.esm|1A696";

    static SkyPatcherConflicts.SkyPatcherConflictEntry Entry(int i, string value) =>
        new("C:\\mods\\Provider\\npc\\Patch" + i.ToString("D3") + ".ini", i, "health", value, Conditional: false);

    /// <summary>A layer with <paramref name="items"/> items in each of the four report sections and five scan notes.</summary>
    static SkyPatcherLayerData Layer(int items)
    {
        var ini = new SkyPatcherDiscovery.IniFile(RelPath: "npc\\Patch.ini", Subfolder: "npc", SortKey: "Patch.ini",
            WinningProvider: "Provider", LooseFilePath: "C:\\mods\\Provider\\npc\\Patch.ini",
            ShadowedProviders: Array.Empty<string>(), GatePlugin: null, NotApplied: null,
            Lines: new[] { new SkyPatcherLine("filterByNpcs=" + Target + ":health=200", SkyPatcherLineKind.Patch,
                                              Array.Empty<SkyPatcherSegment>(), null) });
        var folder = new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true, Files: new[] { ini });
        var n = Enumerable.Range(1, items).ToList();
        return new SkyPatcherLayerData(
            new SkyPatcherDiscovery.LayerScan(new[] { folder },
                Enumerable.Range(1, 5).Select(i => $"scan note {i}: npc\\Copy{i}.ini shadows a same-path copy").ToArray(),
                ReadIncomplete: false, new Dictionary<string, bool>()),
            Conflicts: n.Select(i => new SkyPatcherConflicts.SkyPatcherConflict("npc", "health", Target + i,
                new[] { Entry(i, "100"), Entry(i + 1, "200") })).ToList(),
            Itms: n.Select(i => new SkyPatcherConflicts.SkyPatcherItm("npc", "health", "Patch" + i + ".ini",
                new[] { new SkyPatcherConflicts.SkyPatcherItmEntry(i, "health", "100", Target, false, new[] { i + 1 }) })).ToList(),
            Duplicates: n.Select(i => new SkyPatcherConflicts.SkyPatcherDuplicate("npc", "health", Target + i,
                new[] { Entry(i, "100"), Entry(i + 1, "100") })).ToList(),
            NoOps: n.Select(i => new SkyPatcherNoOpWrite("npc", Target + i, "EditorId" + i, "Health",
                "Patch" + i + ".ini", i, "health", "100", "100")).ToList(),
            NoOpNotes: Array.Empty<string>(),
            RootFailures: Array.Empty<string>(), ReadIncomplete: false, AssetWarnings: Array.Empty<string>(),
            ProfileName: "Default");
    }

    [Fact]
    public void ScanNotesCutAfterTheReportSectionsAreCounted()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(60), null, 9_000);

        Assert.Matches(@"\.\.\. \[showing \d+ of 5 note\(s\); raise max_chars\]", text);
        Assert.True(text.Length <= 9_000, $"{text.Length} chars against max_chars=9000.");
        Assert.DoesNotContain("over the max_chars", text);   // RenderCap.Settle held without its overrun notice
    }

    [Fact]
    public void ReplayNotesAndScanNotesAreCountedAsOneList()
    {
        var d = Layer(60) with { NoOpNotes = new[] { "replay note 1", "replay note 2" } };

        var text = SkyPatcherWire.RenderLayer(d, null, 9_000);

        Assert.Matches(@"\.\.\. \[showing \d+ of 7 note\(s\); raise max_chars\]", text);
    }

    [Fact]
    public void ScanNotesThatFitAreAllShownWithNoMarker()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(60), null, 80_000);

        for (int i = 1; i <= 5; i++) Assert.Contains($"[!] scan note {i}: ", text);
        Assert.DoesNotContain("note(s); raise max_chars", text);
    }
}

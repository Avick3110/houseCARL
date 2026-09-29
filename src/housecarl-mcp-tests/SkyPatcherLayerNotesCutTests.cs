using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer's notes after the report sections: they take a bounded share of max_chars charged
/// before the body, a list that fits it is shown whole, and a cut note is counted, never dropped without a word (#894).</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherLayerNotesCutTests
{
    const string Target = "Skyrim.esm|1A696";

    /// <summary>Each note line is 450 chars with its "[!] " lead and newline, so five of them are 2250: a quarter of 9000.</summary>
    const int NoteLine = 450;

    static string Note(string tag) => (tag + ": npc\\Copy.ini shadows a same-path copy ").PadRight(NoteLine - 5, 'x');

    static SkyPatcherConflicts.SkyPatcherConflictEntry Entry(int i, string value) =>
        new("C:\\mods\\Provider\\npc\\Patch" + i.ToString("D3") + ".ini", i, "health", value, Conditional: false);

    /// <summary>A layer with 60 items in each of the four report sections and five scan notes.</summary>
    static SkyPatcherLayerData Layer()
    {
        var ini = new SkyPatcherDiscovery.IniFile(RelPath: "npc\\Patch.ini", Subfolder: "npc", SortKey: "Patch.ini",
            WinningProvider: "Provider", LooseFilePath: "C:\\mods\\Provider\\npc\\Patch.ini",
            ShadowedProviders: Array.Empty<string>(), GatePlugin: null, NotApplied: null,
            Lines: new[] { new SkyPatcherLine("filterByNpcs=" + Target + ":health=200", SkyPatcherLineKind.Patch,
                                              Array.Empty<SkyPatcherSegment>(), null) });
        var folder = new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true, Files: new[] { ini });
        var n = Enumerable.Range(1, 60).ToList();
        return new SkyPatcherLayerData(
            new SkyPatcherDiscovery.LayerScan(new[] { folder },
                Enumerable.Range(1, 5).Select(i => Note("scan note " + i)).ToArray(),
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
    public void NotesThatFitTheirShareAreShownWholeUnderAFullBody()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(), null, 4 * 5 * NoteLine);   // the share is exactly the notes' width

        for (int i = 1; i <= 5; i++) Assert.Contains($"[!] scan note {i}: ", text);
        Assert.DoesNotContain("note(s); raise max_chars", text);
        Assert.True(text.Length <= 4 * 5 * NoteLine, $"{text.Length} chars against max_chars={4 * 5 * NoteLine}.");
    }

    [Fact]
    public void NotesPastTheirShareAreCounted()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(), null, 8_000);

        Assert.Contains("... [showing 4 of 5 note(s); raise max_chars]", text);
        Assert.True(text.Length <= 8_000, $"{text.Length} chars against max_chars=8000.");
        Assert.DoesNotContain("over the max_chars", text);   // RenderCap.Settle held without its overrun notice
    }

    [Fact]
    public void ReplayNotesAndScanNotesAreCountedAsOneList()
    {
        var d = Layer() with { NoOpNotes = new[] { "replay note 1", "replay note 2" } };

        var text = SkyPatcherWire.RenderLayer(d, null, 4 * 5 * NoteLine);

        Assert.Matches(@"\.\.\. \[showing \d+ of 7 note\(s\); raise max_chars\]", text);
    }

    [Fact]
    public void ZeroMatchShowsTheLastNoteWhereTheWholeListFits()
    {
        var full = SkyPatcherWire.RenderLayer(Layer(), "no-such-folder", 1_000_000);

        var text = SkyPatcherWire.RenderLayer(Layer(), "no-such-folder", full.Length);

        Assert.Contains("[!] scan note 5: ", text);
        Assert.DoesNotContain("note(s); raise max_chars", text);
    }

    [Fact]
    public void ANoteWiderThanItsShareIsCountedInsideTheCap()
    {
        var text = SkyPatcherWire.RenderLayer(Layer(), null, 1_200);   // a quarter is 300 chars, under one 450-char note

        Assert.Contains("... [showing 0 of 5 note(s); raise max_chars]", text);
        Assert.True(text.Length <= 1_200, $"{text.Length} chars against max_chars=1200.");
    }

    [Fact]
    public void ZeroMatchCountsANoteWiderThanItsRoomInsideTheCap()
    {
        var bare = SkyPatcherWire.RenderLayer(Layer() with { Scan = Layer().Scan with { Notes = Array.Empty<string>() } },
            "no-such-folder", 1_000_000);
        int cap = bare.Length + 300;   // room for the marker, not for one 450-char note

        var text = SkyPatcherWire.RenderLayer(Layer(), "no-such-folder", cap);

        Assert.Contains("... [showing 0 of 5 note(s); raise max_chars]", text);
        Assert.True(text.Length <= cap, $"{text.Length} chars against max_chars={cap}.");
    }
}

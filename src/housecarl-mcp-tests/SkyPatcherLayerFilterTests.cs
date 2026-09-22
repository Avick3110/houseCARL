using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_skypatcher_layer's filter=. A filter that matches no INI must say so; returning the
/// unfiltered overview instead is indistinguishable from a call that passed no filter at all.</summary>
[Trait("tier", "unit")]
public sealed class SkyPatcherLayerFilterTests
{
    static SkyPatcherDiscovery.IniFile Ini(string subfolder, string name, string provider, string value = "200") =>
        new(RelPath: subfolder + "\\" + name, Subfolder: subfolder, SortKey: name, WinningProvider: provider,
            LooseFilePath: "C:\\mods\\" + provider + "\\" + name, ShadowedProviders: Array.Empty<string>(),
            GatePlugin: null, NotApplied: null,
            Lines: new[] { new SkyPatcherLine("filterByNpcs=Skyrim.esm|1A696:health=" + value, SkyPatcherLineKind.Patch,
                                              Array.Empty<SkyPatcherSegment>(), null) });

    static SkyPatcherLayerData Layer(params SkyPatcherDiscovery.FolderScan[] folders) =>
        new(new SkyPatcherDiscovery.LayerScan(folders, Array.Empty<string>(), ReadIncomplete: false,
                new Dictionary<string, bool>()),
            Conflicts: Array.Empty<SkyPatcherConflicts.SkyPatcherConflict>(),
            Itms: Array.Empty<SkyPatcherConflicts.SkyPatcherItm>(),
            Duplicates: Array.Empty<SkyPatcherConflicts.SkyPatcherDuplicate>(),
            NoOps: Array.Empty<SkyPatcherNoOpWrite>(), NoOpNotes: Array.Empty<string>(),
            RootFailures: Array.Empty<string>(), ReadIncomplete: false, AssetWarnings: Array.Empty<string>(),
            ProfileName: "Default");

    /// <summary>The same layer with loose roots that would not read — what the caveat block has to carry.</summary>
    static SkyPatcherLayerData WithRootFailures(SkyPatcherLayerData d, int n) =>
        d with
        {
            ReadIncomplete = true,
            RootFailures = Enumerable.Range(1, n)
                .Select(i => $"BlockedMod{i:D2}: could not read 'SKSE\\Plugins\\SkyPatcher\\npc' — " + new string('r', 160))
                .ToList(),
        };

    /// <summary>A layer with enough INIs that its body alone would spend a small max_chars.</summary>
    static SkyPatcherLayerData BigLayer(int inis) =>
        Layer(new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true,
                  Files: Enumerable.Range(1, inis).Select(i => Ini("npc", $"Mod{i:D3}.ini", $"Provider Number {i:D3}")).ToArray()));

    /// <summary>The named roots are cut and COUNTED, never appended past max_chars: the count line is the difference
    /// between a list that was trimmed and a list that was short.</summary>
    [Fact]
    public void TheRootFailureListIsCutAndCountedAtMaxChars()
    {
        var text = SkyPatcherWire.RenderLayer(WithRootFailures(OneNpcFolder(), 20), null, 2_000);

        Assert.Contains("BlockedMod01", text);                               // the ones that fit are named
        Assert.Matches(@"showing \d+ of 20 loose root read failure\(s\)", text);
        Assert.DoesNotContain("BlockedMod20", text);                         // and the rest are counted, not written
    }

    /// <summary>The filtered no-match render is the one that most needs the hedge — "nothing matched" over a layer
    /// that did not fully read — and it goes through its own path, which used to charge the block a second time and
    /// name nothing. Same fixture, same cap, one filter that matches no INI.</summary>
    [Fact]
    public void AFilteredNoMatchNamesTheRootsToo()
    {
        var text = SkyPatcherWire.RenderLayer(WithRootFailures(OneNpcFolder(), 20), "nosuchthing", 2_000);

        Assert.Contains("0 of 1 INI(s) match", text);                        // it IS the zero-match render
        Assert.Contains("BlockedMod01", text);
        Assert.Matches(@"showing \d+ of 20 loose root read failure\(s\)", text);
        Assert.DoesNotContain("showing 0 of 20", text);
        // The block is composed ONCE for both paths, so the filtered render names as many roots as the unfiltered one.
        // Measured at a cap wide enough for several: charging it twice would spend a quarter of what is left after the
        // first block, and name fewer folders for the same failures.
        Assert.Equal(NamedRootCount(SkyPatcherWire.RenderLayer(WithRootFailures(OneNpcFolder(), 20), null, 8_000)),
                     NamedRootCount(SkyPatcherWire.RenderLayer(WithRootFailures(OneNpcFolder(), 20), "nosuchthing", 8_000)));
    }

    /// <summary>How many roots a render actually named, off the lines themselves rather than the marker.</summary>
    static int NamedRootCount(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\[!\] loose root read failure: ").Count;

    /// <summary>Enough failed roots to outgrow their share: the block is bounded to a quarter of max_chars and counted,
    /// so the LAYER — the thing a layer read is for — is still in the answer, and the answer is still inside the cap.
    /// Before the bound, twenty roots erased the folder listing and pushed the render 767 chars over.</summary>
    [Fact]
    public void ManyFailedRootsTakeAShareOfMaxCharsAndNotTheLayer()
    {
        var text = SkyPatcherWire.RenderLayer(WithRootFailures(BigLayer(400), 20), null, 4_000);

        Assert.Contains("npc: 400 INI(s)", text);                            // the folder header survived
        Assert.Contains("Mod001.ini", text);                                 // and so did the listing under it
        Assert.Contains("BlockedMod01", text);                               // with roots still named
        Assert.Matches(@"showing \d+ of 20 loose root read failure\(s\)", text);
        Assert.True(text.Length <= 4_000 + TrailerSlack,
                    $"{text.Length} chars against max_chars=4000 — the caveat block was not bounded.");
    }

    /// <summary>A short list is not marked as cut — a marker on a complete list would read as a missing name.</summary>
    [Fact]
    public void ARootFailureListThatFitsCarriesNoCutMarker()
    {
        var text = SkyPatcherWire.RenderLayer(WithRootFailures(OneNpcFolder(), 2), null, 80_000);

        Assert.Contains("BlockedMod01", text);
        Assert.Contains("BlockedMod02", text);
        Assert.DoesNotContain("loose root read failure(s); raise max_chars", text);
    }

    /// <summary>The caveats close the render, so their room is held back BEFORE the body is laid: on a layer whose
    /// INIs would spend the whole budget, the roots are still named rather than being the first thing dropped.</summary>
    [Fact]
    public void TheCaveatBlockIsPaidForInsideMaxCharsRatherThanCutByTheBody()
    {
        var text = SkyPatcherWire.RenderLayer(WithRootFailures(BigLayer(400), 3), null, 4_000);

        Assert.Contains("max_chars", text);                                  // the body did have to cut
        Assert.Contains("BlockedMod01", text);
        Assert.Contains("BlockedMod03", text);
        Assert.DoesNotContain("showing 0 of 3 loose root read failure(s)", text);
        // Paid for INSIDE max_chars, not appended past it: the trailer the render always writes is the only overrun.
        Assert.True(text.Length <= 4_000 + TrailerSlack,
                    $"{text.Length} chars against max_chars=4000 — the caveat block was not reserved.");
    }

    /// <summary>The "→ housecarl_records …" hint every layer render closes with, the one thing written past the cap.</summary>
    const int TrailerSlack = 400;

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

    static SkyPatcherLayerData TwoFolders() =>
        Layer(new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true,
                  Files: new[] { Ini("npc", "Bandits.ini", "Bandit Overhaul") }),
              new SkyPatcherDiscovery.FolderScan("weapon", Catalog: null, PatchingEnabled: true,
                  Files: new[] { Ini("weapon", "Blades.ini", "Weapon Overhaul") }));

    [Fact]
    public void AFilterListsOnlyTheMatchingFolder()
    {
        var text = SkyPatcherWire.RenderLayer(TwoFolders(), "weapon", 80_000);

        Assert.Contains("Blades.ini", text);
        Assert.DoesNotContain("Bandits.ini", text);           // filter= selects at the folder level, it does not merely expand
        Assert.Contains("1 of 2 INI(s) match", text);
        Assert.Contains("in 1 of 2 type folder(s)", text);
    }

    /// <summary>One folder, one matching file, a sibling on each side of it in apply order.</summary>
    static SkyPatcherLayerData FolderWithNeighbours() =>
        Layer(new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true,
                  Files: new[]
                  {
                      Ini("npc", "aa_Before.ini", "Bandit Overhaul", "111"),
                      Ini("npc", "zz_MyPatch.ini", "My Patch", "222"),
                      Ini("npc", "zzz_After.ini", "Late Overhaul", "333"),
                  }));

    [Fact]
    public void AMatchedFilesSiblingsAreStillListedUnexpanded()
    {
        var text = SkyPatcherWire.RenderLayer(FolderWithNeighbours(), "zz_MyPatch.ini", 80_000);

        Assert.Contains("aa_Before.ini", text);               // where the match sorts is the answer the filter is for
        Assert.Contains("zz_MyPatch.ini", text);
        Assert.Contains("zzz_After.ini", text);
        Assert.Contains("health=222", text);                  // only the match expands to its lines
        Assert.DoesNotContain("health=111", text);
        Assert.DoesNotContain("health=333", text);
        Assert.True(text.IndexOf("  - aa_Before.ini", StringComparison.Ordinal)
                    < text.IndexOf("  - zz_MyPatch.ini", StringComparison.Ordinal));   // apply order, not match-first
    }

    [Fact]
    public void TheFolderHeaderCountsWhatIsExpandedUnderIt()
    {
        var text = SkyPatcherWire.RenderLayer(FolderWithNeighbours(), "zz_MyPatch.ini", 80_000);

        Assert.Contains("npc: 3 INI(s) (1 matching, expanded), 3 patch line(s)", text);
    }

    [Fact]
    public void AnUnfilteredFolderHeaderCarriesNoMatchCount()
    {
        var text = SkyPatcherWire.RenderLayer(FolderWithNeighbours(), null, 80_000);

        Assert.Contains("npc: 3 INI(s), 3 patch line(s)", text);
        Assert.DoesNotContain("matching, expanded", text);
    }

    /// <summary>A layer whose matching folder sorts after enough inventory to be cut by a modest max_chars.</summary>
    static SkyPatcherLayerData LateMatchLayer() =>
        Layer(new SkyPatcherDiscovery.FolderScan("npc", Catalog: null, PatchingEnabled: true,
                  Files: Enumerable.Range(1, 10).Select(i => Ini("npc", $"Bandits{i}.ini", "Bandit Overhaul")).ToArray()),
              new SkyPatcherDiscovery.FolderScan("weapon", Catalog: null, PatchingEnabled: true,
                  Files: new[] { Ini("weapon", "Blades.ini", "Weapon Overhaul") }));

    /// <summary>A cap landing exactly on the second folder's boundary in the unfiltered render.</summary>
    static int CapAtTheWeaponFolder() =>
        SkyPatcherWire.RenderLayer(LateMatchLayer(), null, 1_000_000).IndexOf("\nweapon:", StringComparison.Ordinal);

    [Fact]
    public void AMatchingFolderIsNotLostToTheCapAndTheCutNoticeDoesNotSuggestAFilter()
    {
        var text = SkyPatcherWire.RenderLayer(LateMatchLayer(), "weapon", CapAtTheWeaponFolder());

        Assert.Contains("Blades.ini", text);                  // selection puts the late match inside the same cap
        Assert.DoesNotContain("pass filter=", text);          // never recommend what the caller already passed
    }

    [Fact]
    public void AnUnfilteredCutStillSuggestsAFilter()
    {
        var text = SkyPatcherWire.RenderLayer(LateMatchLayer(), null, CapAtTheWeaponFolder());

        Assert.DoesNotContain("Blades.ini", text);
        Assert.Contains("pass filter=", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankFilterIsNoFilterAtBothSites(string filter)
    {
        var text = SkyPatcherWire.RenderLayer(TwoFolders(), filter, 80_000);

        Assert.Contains("Bandits.ini", text);                 // not a zero match
        Assert.Contains("Blades.ini", text);
        Assert.DoesNotContain("health=200", text);            // and not a whole-layer expansion either
        Assert.DoesNotContain("INI(s) match", text);
    }

    [Fact]
    public void ZeroMatchStillRendersTheScanNotes()
    {
        var d = OneNpcFolder();
        var withNotes = d with
        {
            Scan = new SkyPatcherDiscovery.LayerScan(d.Scan.Folders,
                new[] { "subfolder 'npcs' is not a documented SkyPatcher record type" },
                ReadIncomplete: false, new Dictionary<string, bool>()),
            NoOpNotes = new[] { "a replay note" },
        };

        var text = SkyPatcherWire.RenderLayer(withNotes, "weapon", 80_000);

        Assert.Contains("not a documented SkyPatcher record type", text);
        Assert.Contains("a replay note", text);
    }

    [Fact]
    public void ZeroMatchCutsItsNotesAtMaxCharsWithANotice()
    {
        var d = OneNpcFolder();
        var withNotes = d with
        {
            Scan = new SkyPatcherDiscovery.LayerScan(d.Scan.Folders,
                Enumerable.Range(1, 40).Select(i => $"scan note {i} " + new string('x', 200)).ToArray(),
                ReadIncomplete: false, new Dictionary<string, bool>()),
        };

        var full = SkyPatcherWire.RenderLayer(withNotes, "weapon", 1_000_000);
        var text = SkyPatcherWire.RenderLayer(withNotes, "weapon", 2_000);

        Assert.True(full.Length > 5_000);                     // the unbounded answer really is far over the cap
        Assert.True(text.Length < full.Length);               // max_chars is honoured on the zero-match path too
        Assert.Contains("of 40 note(s); raise max_chars", text);
        Assert.DoesNotContain("scan note 40 ", text);
    }

    [Fact]
    public void ZeroMatchAlwaysRendersTheCaveatsEvenPastTheCap()
    {
        var d = OneNpcFolder();
        var withNotes = d with
        {
            Scan = new SkyPatcherDiscovery.LayerScan(d.Scan.Folders,
                Enumerable.Range(1, 40).Select(i => $"scan note {i} " + new string('x', 200)).ToArray(),
                ReadIncomplete: false, new Dictionary<string, bool>()),
            ReadIncomplete = true,
        };

        var text = SkyPatcherWire.RenderLayer(withNotes, "weapon", 2_000);

        Assert.Contains("failed to read this build", text);   // a cut must never swallow the incomplete-read warning
    }

    [Fact]
    public void AnEmptyLayerWithAnIncompleteReadIsNotCalledEmpty()
    {
        var d = Layer() with { ReadIncomplete = true };

        var text = SkyPatcherWire.RenderLayer(d, "weapon", 80_000);

        Assert.DoesNotContain("no SkyPatcher INIs at all", text);
        Assert.Contains("the read was incomplete", text);
    }
}

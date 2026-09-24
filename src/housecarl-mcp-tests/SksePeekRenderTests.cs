using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The peek in the <c>housecarl_skse</c> inventory render: the bare-peek refusal, the embedded-plugin cross-check against
/// the load order (present, absent, and no answer when the order is unknown), the service's empty-composition-to-null
/// rule, the per-entry "not peeked" notice, and both Debug-CRT wordings. Synthetic inventory data into the pure render.
/// Migrated from the skse-peek-guard probe, part 3 (arms H to J).
/// </summary>
[Trait("tier", "unit")]
public sealed class SksePeekRenderTests
{
    static readonly SksePeekResult Peek =
        new(["Data\\SKSE\\Plugins\\Thing\\x.json"], ["Dawnguard.esm", "GhostMod.esp"], 40, 4096, null);

    static SksePluginReader.SksePluginInfo Info(IReadOnlyList<string>? imports) =>
        new("x.dll", SksePluginReader.SksePluginKind.Modern, true,
            new SksePluginReader.SkseVersionInfo("Test", "Tester", "", "1.0.0", true, false, false, false, [], null),
            null, imports);

    static SkseFileEntry Entry(string file, SksePeekResult? peek, SksePluginReader.SksePluginInfo info) =>
        new(file, file, "", [new SkseProvider("TestMod", "loose")], info, null, peek);

    static SkseInventoryData Data(SkseFileEntry dll, IEnumerable<string>? active) =>
        new([dll], [], 0, "1.6.1170.0", [], [], false, [], "TestProfile",
            active is null ? null : new HashSet<string>(active, StringComparer.OrdinalIgnoreCase));

    static string Render(SkseInventoryData d, string? filter) => SkseInventoryWire.Render(d, filter, 80_000);

    static string LineOf(string text, string needle) =>
        text.Split('\n').FirstOrDefault(l => l.Contains(needle)) ?? throw new Xunit.Sdk.XunitException($"no line with '{needle}' in:\n{text}");

    // ---- H: the peek= argument check ----

    // probe H: "a bare peek=true FAILS LOUD"; "…a blank filter too"
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void APeekWithoutAFilterIsRefusedNamingFilter(string? filter) =>
        Assert.Contains("filter=", SkseInventoryWire.PeekArgError(peek: true, filter: filter));

    // probe H: "peek + filter is valid"; "no peek, no filter = the normal inventory"
    [Theory]
    [InlineData(true, "SkyPatcher")]
    [InlineData(false, null)]
    public void APeekWithAFilterOrNoPeekAtAllIsAccepted(bool peek, string? filter) =>
        Assert.Null(SkseInventoryWire.PeekArgError(peek, filter));

    // ---- I: the embedded-plugin cross-check ----

    // probe I: "an embedded name present in the order renders as present"
    [Fact]
    public void AnEmbeddedNameInTheOrderRendersAsPresent()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Assert.Contains("(in your load order)", LineOf(text, "Dawnguard.esm"));
    }

    // probe I: "an embedded name ABSENT from the order is flagged (the verify signal)"
    [Fact]
    public void AnEmbeddedNameAbsentFromTheOrderIsFlagged()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Assert.Contains("NOT in your load order", LineOf(text, "GhostMod.esp"));
    }

    // probe I: "the embedded config surface renders"
    [Fact]
    public void TheEmbeddedConfigSurfaceRenders()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Assert.Contains("Data\\SKSE\\Plugins\\Thing\\x.json", text);
    }

    // probe I: "the framing line always rides a peek (image contents ≠ behavior; absence proves nothing)"
    [Fact]
    public void TheFramingLineRidesEveryPeek()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Assert.Contains("CONTAINS", text);
        Assert.Contains("Absence proves nothing", text);
    }

    // probe I: "the scan accounting states the cut (filter, not the whole haystack)"
    [Fact]
    public void TheScanAccountingStatesTheRunsScanned()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: ["Dawnguard.esm"]), "Thing");
        Assert.Contains("40 string run", text);
    }

    // probe I: "with no resolved order, NO name is called absent — an unasked question has no answer"
    [Fact]
    public void WithNoResolvedOrderNoNameIsCalledAbsent()
    {
        var text = Render(Data(Entry("Thing.dll", Peek, Info(["kernel32.dll"])), active: null), "Thing");
        Assert.Contains("GhostMod.esp", text);
        Assert.DoesNotContain("NOT in your load order", text);
    }

    // ---- I2: the service hands the renderer null, never an empty set ----

    // probe I2: "a profile with no plugins.txt/loadorder.txt yields an EMPTY composition"; "…and the read SURFACES why";
    // "…so the peek path hands the renderer NULL, never an empty set that would flag every name ABSENT"
    [Fact]
    public void AProfileWithNoPluginListsGivesTheRendererNull()
    {
        string prof = Path.Combine(Path.GetTempPath(), "hc-peek-prof-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+SomeMod\n");   // readable; no plugins.txt or loadorder.txt
            var warn = new List<string>();
            var comp = Mo2LoadOrder.ReadComposition(prof, warn);
            Assert.Empty(comp.ActivePluginNames);
            Assert.Empty(comp.ImplicitPluginNames);
            Assert.NotEmpty(warn);
            Assert.Null(AssetLayers.PeekPluginSet(comp));
        }
        finally { try { Directory.Delete(prof, true); } catch { /* temp scratch */ } }
    }

    // probe I2: "a real composition resolves, implicit force-loaded masters included (Dawnguard.esm is never ABSENT)"
    [Fact]
    public void ARealCompositionResolvesWithItsImplicitMasters()
    {
        var healthy = new Mo2Composition([], [], ["Skyrim.esm", "Dawnguard.esm"],
            new HashSet<string>(["Skyrim.esm"], StringComparer.OrdinalIgnoreCase), [], ["Dawnguard.esm"]);
        var set = AssetLayers.PeekPluginSet(healthy);
        Assert.NotNull(set);
        Assert.Contains("Skyrim.esm", set);
        Assert.Contains("Dawnguard.esm", set);
    }

    // probe I2: "no loadorder.txt + a NON-EMPTY plugins.txt ⇒ still null — the implicit masters are UNKNOWABLE, not absent"
    [Fact]
    public void NoLoadOrderFileWithActivePluginsIsStillNull()
    {
        var noOrderFile = new Mo2Composition([], [], [],
            new HashSet<string>(["SomeMod.esp"], StringComparer.OrdinalIgnoreCase), [], []);
        Assert.Null(AssetLayers.PeekPluginSet(noOrderFile));
    }

    // ---- I3: a peek that matched nothing peekable says so ----

    static readonly SkseFileEntry BsaOnly = new("Bsa.dll", "Bsa.dll", "", [new SkseProvider("Archive.bsa", "BSA")], null, "BSA-only");

    // probe I3: "a matched-but-unpeekable DLL SAYS it wasn't peeked, on its own entry (never a silent no-op)"
    [Fact]
    public void AMatchedButUnpeekableDllSaysItWasNotPeeked()
    {
        var text = SkseInventoryWire.Render(
            new SkseInventoryData([BsaOnly], [], 0, "1.6.1170.0", [], [], false, [], "TestProfile", null, PeekRequested: true),
            "Bsa", 80_000);
        Assert.Contains("not peeked", text);
    }

    // probe I3: "a MIXED match renders the peek AND names the entry that had no image to read"
    [Fact]
    public void AMixedMatchRendersThePeekAndTheNotPeekedEntry()
    {
        var text = SkseInventoryWire.Render(
            new SkseInventoryData([BsaOnly, Entry("Ok.dll", Peek, Info(["kernel32.dll"]))], [], 0, "1.6.1170.0", [], [], false,
                [], "TestProfile", null, PeekRequested: true), ".dll", 80_000);
        Assert.Contains("not peeked", text);
        Assert.Contains("── peek (what the image contains) ──", text);
    }

    // probe I3: "a config-only filter with peek=true says no DLL matched"
    [Fact]
    public void AConfigOnlyMatchWithPeekSaysNoDllMatched()
    {
        var text = SkseInventoryWire.Render(
            new SkseInventoryData([], [new SkseFileEntry("a.ini", "a.ini", "Grp", [new SkseProvider("M", "loose")], null, null)],
                0, "1.6.1170.0", [], [], false, [], "TestProfile", null, PeekRequested: true), "Grp", 80_000);
        Assert.Contains("matched no DLL at all", text);
    }

    // ---- I4 and J: the Debug-CRT wording ----

    // probe I4: "layer line, runtime ABSENT ⇒ 'will NOT load'"
    [Fact]
    public void TheLayerLineWithTheRuntimeAbsentSaysWillNotLoad() =>
        Assert.Contains("will NOT load", SkseInventoryWire.DebugCrtLayerVerdict(["vcruntime140d.dll"], _ => false));

    // probe I4: "layer line, runtime PRESENT ⇒ the author-facing wording"
    [Fact]
    public void TheLayerLineWithTheRuntimePresentSaysItLoadsOnThisMachine() =>
        Assert.Contains("loads on THIS machine", SkseInventoryWire.DebugCrtLayerVerdict(["vcruntime140d.dll"], _ => true));

    // probe J: "a debug-CRT import is flagged with the culprit named"; "the verdict matches THIS machine"; "…and names the actual loader failure (error 126)"
    [Fact]
    public void ADebugCrtImportIsFlaggedWithTheVerdictForThisMachine()
    {
        var text = Render(Data(Entry("Debug.dll", Peek, Info(["kernel32.dll", "vcruntime140d.dll"])), active: ["Dawnguard.esm"]), "Debug");
        Assert.Contains("DEBUG BUILD", text);
        Assert.Contains("vcruntime140d.dll", LineOf(text, "DEBUG BUILD"));
        bool present = SksePluginReader.IsSystemDllResolvable("vcruntime140d.dll");
        Assert.Contains(present ? "loads on THIS machine" : "will NOT load", text);
        Assert.Contains("error 126", text);
    }

    // probe J: "debug runtime ABSENT ⇒ the flat 'will NOT load' verdict"
    [Fact]
    public void TheDetailVerdictWithTheRuntimeAbsentSaysWillNotLoad()
    {
        var text = SkseInventoryWire.DebugCrtVerdict(["vcruntime140d.dll"], _ => false);
        Assert.Contains("will NOT load", text);
        Assert.Contains("error 126", text);
    }

    // probe J: "debug runtime PRESENT ⇒ the author-facing verdict"; "…and the present case NEVER makes the flat claim"
    [Fact]
    public void TheDetailVerdictWithTheRuntimePresentNeverSaysWillNotLoad()
    {
        var text = SkseInventoryWire.DebugCrtVerdict(["vcruntime140d.dll"], _ => true);
        Assert.Contains("loads on THIS machine", text);
        Assert.Contains("for anyone who doesn't", text);
        Assert.DoesNotContain("will NOT load", text);
    }

    // probe J: "a debug build surfaces on the UNFILTERED inventory (§8.3 escalation)"
    [Fact]
    public void ADebugBuildSurfacesOnTheUnfilteredInventory()
    {
        var text = Render(Data(Entry("Debug.dll", Peek, Info(["kernel32.dll", "vcruntime140d.dll"])), active: null), filter: null);
        Assert.Contains("DEBUG-BUILD plugins", text);
    }

    // probe J: "…and a clean layer says nothing about debug builds"
    [Fact]
    public void ACleanLayerSaysNothingAboutDebugBuilds()
    {
        var text = Render(Data(Entry("Fine.dll", null, Info(["kernel32.dll"])), active: null), filter: null);
        Assert.DoesNotContain("DEBUG-BUILD plugins", text);
    }
}

using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The TRANSPORT lane of <c>housecarl_skse</c> (SPEC §2.1): the in-band accounting every answer ends on, the
/// <c>limit=</c>/<c>offset=</c> window over each family's row list, and the <c>format='json'</c> twin. Driven over
/// synthetic data straight into the three family renders, which are pure over it — no live MO2 instance, and no
/// dependence on what a real layer happens to contain.
/// </summary>
[Trait("tier", "unit")]
public sealed class SkseTransportTests
{
    // ---- synthetic layers ------------------------------------------------------------------------

    static SkseProvider Mod(string name) => new(name, "loose");

    static SksePluginReader.SkseVersionInfo Version(string name) =>
        new(name, "author", "", "1.0.0", UsesAddressLibrary: true, UsesSignatureScanning: false,
            UsesUpdatedStructs: false, DeclaresNoStructs: false, new[] { "1.6.1170.0" }, null);

    static SkseFileEntry Dll(int i) =>
        new($"SKSE/Plugins/p{i}.dll", $"p{i}.dll", "", new[] { Mod($"Mod{i}") },
            new SksePluginReader.SksePluginInfo($"p{i}.dll", SksePluginReader.SksePluginKind.Modern, true, Version($"Plugin {i}"), null,
                                                new[] { "kernel32.dll" }),
            null);

    static SkseFileEntry Config(int i, string group = "Group") =>
        new($"SKSE/Plugins/{group}/c{i}.ini", $"c{i}.ini", group, new[] { Mod($"Mod{i}") }, null, null);

    /// <summary>A caveat block big enough to be visible against a tight max_chars — what a reserve has to hold room
    /// for, and what an unreserved tail overshoots the cap by.</summary>
    static string[] Warnings(int n) => Enumerable.Range(1, n).Select(i => $"warning {i}: " + new string('w', 180)).ToArray();

    static SkseInventoryData Inventory(int dlls, int configs = 0, int folders = 1, string[]? warnings = null) =>
        new(Enumerable.Range(1, dlls).Select(Dll).ToList(),
            Enumerable.Range(1, configs).Select(i => Config(i, $"Group{i % folders}")).ToList(),
            OtherFileCount: 0, InstalledRuntime: "1.6.1170.0", BsaFailures: Array.Empty<string>(),
            ReadIncomplete: false, Warnings: warnings ?? Array.Empty<string>(), ProfileName: "Default");

    static NativeClassEntry Cls(int i) =>
        new($"scripts/k{i}.pex", $"Klass{i}", new[] { "Fn" }, new[] { Mod($"Mod{i}") },
            NativeProvenance.ThirdParty, NativePairingRung.SameMod, $"Mod{i}",
            new[] { new NativePairedDll($"SKSE/Plugins/p{i}.dll", $"p{i}.dll", "", $"Mod{i}",
                                        new SksePluginReader.SksePluginInfo($"p{i}.dll", SksePluginReader.SksePluginKind.Modern, true, Version($"Plugin {i}"), null,
                                                                            new[] { "kernel32.dll" }), null) });

    /// <summary>A class whose only candidate DLL is version-locked to a runtime that is not the installed one — the
    /// PAIRED BUT DEAD finding.</summary>
    static NativeClassEntry DeadCls(int i) =>
        new($"scripts/d{i}.pex", $"Dead{i}", new[] { "Fn" }, new[] { Mod($"Other{i}") },
            NativeProvenance.ThirdParty, NativePairingRung.SameMod, $"Other{i}",
            new[] { new NativePairedDll($"SKSE/Plugins/d{i}.dll", $"d{i}.dll", "", $"Other{i}",
                        new SksePluginReader.SksePluginInfo($"d{i}.dll", SksePluginReader.SksePluginKind.Modern, true,
                            new SksePluginReader.SkseVersionInfo($"Dead {i}", "author", "", "1.0.0",
                                UsesAddressLibrary: false, UsesSignatureScanning: false, UsesUpdatedStructs: false,
                                DeclaresNoStructs: false, new[] { "1.5.97.0" }, null), null,
                            new[] { "kernel32.dll" }), null) });

    /// <summary>A baseline class — carried by an official archive, implemented by the game executable. It is accounted
    /// for by a count on the "accounted for" line rather than by a section row of its own.</summary>
    static NativeClassEntry EngineCls(int i) =>
        new($"scripts/e{i}.pex", $"Engine{i}", new[] { "Fn" }, new[] { Mod("Skyrim.esm") },
            NativeProvenance.Engine, null, null, Array.Empty<NativePairedDll>());

    /// <summary>A layer whose first <paramref name="engine"/> classes are baseline and whose rest are healthy
    /// third-party pairings — the two populations the "accounted for" block reconciles.</summary>
    static NativePairingAuditData EngineThenThirdParty(int engine, int third) =>
        new(Enumerable.Range(1, engine).Select(EngineCls)
                .Concat(Enumerable.Range(1, third).Select(Cls)).ToList(),
            PexScanned: engine + third, Unreadable: Array.Empty<NativeUnreadablePex>(),
            SkseLoaderSeen: true, InstalledRuntime: "1.6.1170.0", BsaFailures: Array.Empty<string>(),
            ReadIncomplete: false, Warnings: Array.Empty<string>(), ProfileName: "Default");

    static NativePairingAuditData Pairing(int classes, int unreadable = 0, string[]? warnings = null) =>
        new(Enumerable.Range(1, classes).Select(Cls).ToList(), PexScanned: classes,
            Unreadable: Enumerable.Range(1, unreadable)
                .Select(i => new NativeUnreadablePex($"scripts/bad{i}.pex", $"Mod{i}", "not a valid .pex header")).ToList(),
            SkseLoaderSeen: true, InstalledRuntime: "1.6.1170.0",
            BsaFailures: Array.Empty<string>(), ReadIncomplete: false, Warnings: warnings ?? Array.Empty<string>(), ProfileName: "Default");

    static SkseAuditedRef Ref(int i, int n) =>
        new(new HousecarlCore.SkseConfigRef($"0x{0x800 + n:X6}|Ghost{i}.esp", HousecarlCore.SkseRefShape.FormToken,
                                            $"Ghost{i}.esp", 0x800u + (uint)n, $"0x{0x800 + n:X6}", n, null),
            SkseRefVerdict.PluginMissing, $"Ghost{i}.esp is not in the active load order");

    static SkseConfigFileAudit ConfigFile(int i, int refs = 1) =>
        new($"SKSE/Plugins/Group/f{i}.ini", $"f{i}.ini", "Group", $"Mod{i}", 1, new[] { Mod($"Mod{i}") },
            Enumerable.Range(1, refs).Select(n => Ref(i, n)).ToList(), ReadError: null);

    static SkseConfigAuditData ConfigAudit(int files, int refs = 1, string[]? warnings = null) =>
        new(Enumerable.Range(1, files).Select(i => ConfigFile(i, refs)).ToList(), ConfigCount: files,
            BsaFailures: Array.Empty<string>(), ReadIncomplete: false, Warnings: warnings ?? Array.Empty<string>(), ProfileName: "Default");

    static string Text(string family, int rows, RowWindow window = default, string? filter = null) => family switch
    {
        "inventory" => SkseInventoryWire.Render(Inventory(rows), filter, 80_000, window),
        "pairing" => NativePairingWire.Render(Pairing(rows), filter, 80_000, window),
        _ => SkseConfigAuditWire.Render(ConfigAudit(rows), filter, 80_000, window),
    };

    static JsonDocument Json(string family, int rows, RowWindow window = default, string? filter = null, int cap = 200_000) => family switch
    {
        "inventory" => JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(rows), filter, cap, window)),
        "pairing" => JsonDocument.Parse(NativePairingWire.RenderJson(Pairing(rows), filter, cap, window)),
        _ => JsonDocument.Parse(SkseConfigAuditWire.RenderJson(ConfigAudit(rows), filter, cap, window)),
    };

    static readonly string[] Families = { "inventory", "pairing", "config" };

    // ---- the accounting block --------------------------------------------------------------------

    /// <summary>SPEC §2.1 makes the accounting required output, so it rides EVERY answer — not only a windowed one.
    /// Without this a family could quietly go back to counting nothing.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void EveryFamilysTextAnswerEndsOnTheAccountingBlock(string family)
    {
        var text = Text(family, 4);

        Assert.Contains("[accounting] total=4 rendered=4 skipped=0 capped=0 truncated=0 offset=0 remaining=0 notes=0", text);
        // Nothing was left out, so nothing advises a next page.
        Assert.DoesNotContain("re-call with limit=", text);
    }

    /// <summary>The filter= view answers over its MATCHES, so its accounting counts those — a filtered response that
    /// reported the whole layer's total would tell the caller to page through rows the filter already excluded.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void TheFilteredViewAccountsForItsMatchesNotTheWholeLayer(string family)
    {
        var text = Text(family, 6, filter: "Mod3");

        Assert.Contains("[accounting] total=1 rendered=1", text);
    }

    /// <summary>A filter that matches nothing is still an answer, and still says so in numbers.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void AFilterThatMatchesNothingStillCarriesTheAccounting(string family)
        => Assert.Contains("[accounting] total=0 rendered=0", Text(family, 4, filter: "NoSuchThing"));

    // ---- the paging window -----------------------------------------------------------------------

    /// <summary>offset= steps over rows BEFORE the window and limit= bounds it, and the two omissions are counted
    /// apart: skipped is what offset passed, capped is what limit left behind.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void LimitAndOffsetWindowTheRowsAndAreCountedApart(string family)
    {
        var text = Text(family, 10, new RowWindow(Offset: 2, Limit: 3));

        Assert.Contains("[accounting] total=10 rendered=3 skipped=2 capped=5 truncated=0 offset=2 remaining=5", text);
        // The next page starts at the first row this response did not show, and keeps the same window size.
        Assert.Contains("re-call with limit=3 offset=5 for the next page.", text);
    }

    /// <summary>The census above the rows states the WHOLE layer even when the window shows three of it — the
    /// AssetStatus contract. A windowed response that renamed the total would read as a smaller load order.</summary>
    [Fact]
    public void TheCensusStatesTheWholeLayerWhileTheRowsAreTheWindow()
    {
        var text = Text("inventory", 10, new RowWindow(Offset: 0, Limit: 3));

        Assert.Contains("10 DLL(s)", text);
        Assert.Contains("plugins: 10 with static metadata", text);
        Assert.Contains("plugins with metadata (3)", text);   // the section lists the window
    }

    /// <summary>An offset past the end is answered with the fact, not with advice to re-call at the offset that
    /// already overshot.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void AnOffsetPastTheEndSaysSoInsteadOfAdvisingTheSameOffsetAgain(string family)
    {
        var text = Text(family, 3, new RowWindow(Offset: 9, Limit: 0));

        Assert.Contains("offset=9 is past the end of the selection (3 ", text);
        Assert.DoesNotContain("re-call with limit=", text);
    }

    /// <summary>Windows tile: paging the whole list one page at a time visits every row exactly once.</summary>
    [Fact]
    public void ConsecutiveWindowsTileTheWholeRowList()
    {
        var first = Text("pairing", 6, new RowWindow(0, 4));
        var second = Text("pairing", 6, new RowWindow(4, 4));

        Assert.Contains("Klass1", first);
        Assert.Contains("Klass4", first);
        Assert.DoesNotContain("Klass5", first);
        Assert.Contains("Klass5", second);
        Assert.Contains("Klass6", second);
        Assert.Contains("[accounting] total=6 rendered=2 skipped=4 capped=0", second);
    }

    /// <summary>Both knobs are one refusal, because a caller who got one wrong usually typed the other in the same
    /// call. It is one plain sentence naming what to pass instead.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -5)]
    public void ANegativeLimitOrOffsetIsOneRefusalNamingBothKnobs(int limit, int offset)
    {
        var error = new RowWindow(offset, limit).Error;

        Assert.NotNull(error);
        Assert.StartsWith("error: ", error);
        Assert.Contains("limit=0 for no limit", error);
        Assert.Contains("offset=0", error);
    }

    // ---- the json twin ---------------------------------------------------------------------------

    /// <summary>Every family's json answer is a valid document that names the family it ran and the two it did not
    /// — the in-band twin of the text footer, which a json response cannot carry as prose.</summary>
    [Theory]
    [InlineData("inventory", "pairing", "config")]
    [InlineData("pairing", "inventory", "config")]
    [InlineData("config", "inventory", "pairing")]
    public void TheJsonTwinNamesTheFamilyThatRanAndTheTwoThatDidNot(string family, string a, string b)
    {
        using var doc = Json(family, 3);
        var root = doc.RootElement;

        Assert.Equal(family, root.GetProperty("family").GetString());
        Assert.Equal(new[] { a, b }, root.GetProperty("not_run").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("Default", root.GetProperty("profile").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("filter").ValueKind);
    }

    /// <summary>The accounting rides IN the json document, in named fields — so a json consumer reads the numbers
    /// off the shape rather than parsing the text line, and json is never the degraded lane.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void TheJsonTwinCarriesTheSameAccountingInNamedFields(string family)
    {
        using var doc = Json(family, 10, new RowWindow(Offset: 2, Limit: 3));
        var a = doc.RootElement.GetProperty("accounting");

        Assert.Equal(10, a.GetProperty("total").GetInt32());
        Assert.Equal(3, a.GetProperty("rendered").GetInt32());
        Assert.Equal(2, a.GetProperty("skipped").GetInt32());
        Assert.Equal(5, a.GetProperty("capped").GetInt32());
        Assert.Equal(0, a.GetProperty("truncated").GetInt32());
        Assert.Equal(2, a.GetProperty("offset").GetInt32());
        Assert.Equal(5, a.GetProperty("remaining").GetInt32());
        Assert.Equal(0, a.GetProperty("notes").GetInt32());
    }

    /// <summary>The twin carries the SAME rows the text render lists, so the two lanes can differ only in
    /// formatting.</summary>
    [Fact]
    public void TheJsonTwinCarriesTheWindowedRows()
    {
        using var doc = Json("pairing", 6, new RowWindow(4, 4));
        var names = doc.RootElement.GetProperty("classes").EnumerateArray()
                       .Select(c => c.GetProperty("class_name").GetString()).ToArray();

        Assert.Equal(new[] { "Klass5", "Klass6" }, names);
        Assert.Equal(6, doc.RootElement.GetProperty("totals").GetProperty("classes").GetInt32());
    }

    /// <summary>Over max_chars the twin drops trailing ROWS and counts them — never a cut of the serialized string
    /// at a byte budget, which would hand the caller malformed json.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void OverMaxCharsTheJsonTwinDropsRowsAndStaysValid(string family)
    {
        // Parsing at all is half the assertion: a byte-budget cut would throw here.
        using var doc = Json(family, 40, cap: 1_500);
        var a = doc.RootElement.GetProperty("accounting");

        Assert.Equal(40, a.GetProperty("total").GetInt32());
        Assert.True(a.GetProperty("rendered").GetInt32() < 40, "the cap should have dropped rows");
        Assert.Equal(40 - a.GetProperty("rendered").GetInt32(), a.GetProperty("truncated").GetInt32());
    }

    // ---- a window over two concatenated populations ----------------------------------------------

    /// <summary>A limit the FIRST population spends leaves nothing for the second. The inventory filter's row list is
    /// its DLL matches then its config matches, so a limit=2 that the two DLL matches use up must render no config —
    /// a spent budget is not "no budget", and an accounting that says the window was honoured while every config
    /// rendered is the silently wrong answer.</summary>
    [Fact]
    public void ALimitTheFirstPopulationSpendsLeavesNothingForTheSecond()
    {
        var text = SkseInventoryWire.Render(Inventory(2, 30), "Mod", 80_000, new RowWindow(0, 2));

        Assert.Contains("2 DLL + 30 config match(es)", text);           // the census still states the whole match set
        Assert.Contains("[accounting] total=32 rendered=2 skipped=0 capped=30 truncated=0", text);
        Assert.DoesNotContain("matching configs (", text);
        Assert.Contains("re-call with limit=2 offset=2 for the next page.", text);
    }

    /// <summary>The json twin windows the two populations the same way — the two lanes may differ only in
    /// formatting.</summary>
    [Fact]
    public void TheJsonTwinAlsoLeavesNothingForTheSecondPopulationOnASpentLimit()
    {
        using var doc = JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(2, 30), "Mod", 200_000, new RowWindow(0, 2)));

        Assert.Equal(2, doc.RootElement.GetProperty("dlls").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("configs").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("accounting").GetProperty("rendered").GetInt32());
        Assert.Equal(30, doc.RootElement.GetProperty("accounting").GetProperty("capped").GetInt32());
    }

    /// <summary>A limit the first population only PARTLY spends still continues into the second — the fix for the
    /// spent budget must not turn every continuation into an empty one.</summary>
    [Fact]
    public void ALimitTheFirstPopulationOnlyPartlySpendsContinuesIntoTheSecond()
    {
        using var doc = JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(2, 30), "Mod", 200_000, new RowWindow(0, 5)));

        Assert.Equal(2, doc.RootElement.GetProperty("dlls").GetArrayLength());
        Assert.Equal(3, doc.RootElement.GetProperty("configs").GetArrayLength());
        Assert.Equal(5, doc.RootElement.GetProperty("accounting").GetProperty("rendered").GetInt32());
    }

    /// <summary>An offset that lands INSIDE the second population still renders it — the continuation window's offset
    /// arithmetic is untouched by the spent-limit marker.</summary>
    [Fact]
    public void AnOffsetLandingInsideTheSecondPopulationStillRendersIt()
    {
        using var doc = JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(2, 30), "Mod", 200_000, new RowWindow(4, 3)));

        Assert.Equal(0, doc.RootElement.GetProperty("dlls").GetArrayLength());
        Assert.Equal(3, doc.RootElement.GetProperty("configs").GetArrayLength());
        Assert.Equal(new[] { "c3.ini", "c4.ini", "c5.ini" },
                     doc.RootElement.GetProperty("configs").EnumerateArray()
                        .Select(c => c.GetProperty("file_name").GetString()).ToArray());
    }

    // ---- the json census under filter= -----------------------------------------------------------

    /// <summary>The config twin's <c>totals</c> under a filter counts the FILTER'S references, not the whole audit's.
    /// A caller scoping to one folder and reading <c>totals.broken</c> was being told what the whole layer carries,
    /// under a name that reads as the folder's — while <c>files</c> and <c>accounting</c> beside it named the
    /// matches.</summary>
    [Fact]
    public void TheConfigTwinsCensusCountsTheFiltersReferencesNotTheWholeAudit()
    {
        using var doc = JsonDocument.Parse(SkseConfigAuditWire.RenderJson(ConfigAudit(5, refs: 2), "Mod3", 200_000));
        var t = doc.RootElement.GetProperty("totals");

        Assert.Equal(1, doc.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(1, doc.RootElement.GetProperty("accounting").GetProperty("total").GetInt32());
        Assert.Equal(1, t.GetProperty("configs_scanned").GetInt32());
        Assert.Equal(1, t.GetProperty("files_with_references").GetInt32());
        Assert.Equal(2, t.GetProperty("references_checked").GetInt32());
        Assert.Equal(2, t.GetProperty("inert").GetInt32());
        Assert.Equal(0, t.GetProperty("read_errors").GetInt32());
    }

    /// <summary>The pairing twin's <c>totals</c> under a filter classifies the FILTER'S classes: a caller scoping to
    /// one mod must not read other mods' dead pairings as its own. The scan counts that a filter cannot scope —
    /// <c>pex_scanned</c>, <c>unreadable_pex</c> — are not stated rather than stated out of scope.</summary>
    [Fact]
    public void ThePairingTwinsCensusClassifiesTheFiltersClassesNotTheWholeAudit()
    {
        var d = Pairing(1) with
        {
            Classes = new[] { Cls(3) }.Concat(Enumerable.Range(1, 3).Select(DeadCls)).ToList(),
            PexScanned = 4,
        };

        using var doc = JsonDocument.Parse(NativePairingWire.RenderJson(d, "Mod3", 200_000));
        var t = doc.RootElement.GetProperty("totals");

        Assert.Equal(1, doc.RootElement.GetProperty("classes").GetArrayLength());
        Assert.Equal(1, t.GetProperty("classes").GetInt32());
        Assert.Equal(1, t.GetProperty("healthy").GetInt32());
        Assert.Equal(0, t.GetProperty("dead").GetInt32());          // the three dead ones belong to other mods
        Assert.False(t.TryGetProperty("pex_scanned", out _));
        Assert.False(t.TryGetProperty("unreadable_pex", out _));

        // Unfiltered, the same audit states all four and both scan counts.
        using var whole = JsonDocument.Parse(NativePairingWire.RenderJson(d, null, 200_000));
        var w = whole.RootElement.GetProperty("totals");
        Assert.Equal(4, w.GetProperty("classes").GetInt32());
        Assert.Equal(3, w.GetProperty("dead").GetInt32());
        Assert.Equal(4, w.GetProperty("pex_scanned").GetInt32());
    }

    /// <summary>The inventory twin's <c>totals</c> under a filter counts its matches, and the two members a filter
    /// cannot scope are absent rather than layer-wide: <c>other_files</c> is counted-not-listed, and the folder table
    /// is omitted instead of written empty — <c>[]</c> reads as "this layer has no config folders" rather than "this
    /// view does not list them".</summary>
    [Fact]
    public void TheInventoryTwinsCensusCountsItsMatchesAndOmitsWhatAFilterCannotScope()
    {
        var layer = Inventory(5, configs: 3) with { OtherFileCount = 7 };

        using var doc = JsonDocument.Parse(SkseInventoryWire.RenderJson(layer, "p3", 200_000));
        var t = doc.RootElement.GetProperty("totals");

        Assert.Equal(1, doc.RootElement.GetProperty("dlls").GetArrayLength());
        Assert.Equal(1, t.GetProperty("dlls").GetInt32());
        Assert.Equal(1, t.GetProperty("modern").GetInt32());
        Assert.Equal(0, t.GetProperty("configs").GetInt32());
        Assert.Equal(0, t.GetProperty("config_folders").GetInt32());
        Assert.False(t.TryGetProperty("other_files", out _));
        Assert.False(doc.RootElement.TryGetProperty("config_folders", out _));

        // Unfiltered, the whole layer — including the folder table and the uncategorized-file count.
        using var whole = JsonDocument.Parse(SkseInventoryWire.RenderJson(layer, null, 200_000));
        var w = whole.RootElement.GetProperty("totals");
        Assert.Equal(5, w.GetProperty("dlls").GetInt32());
        Assert.Equal(3, w.GetProperty("configs").GetInt32());
        Assert.Equal(7, w.GetProperty("other_files").GetInt32());
        Assert.Equal(1, whole.RootElement.GetProperty("config_folders").GetArrayLength());
    }

    // ---- the pairing family's accounted-for baseline ---------------------------------------------

    /// <summary>The "accounted for" block reconciles THIS PAGE: every row the window held that is not a finding. Its
    /// engine and SKSE-core counts used to come from the whole audit while the healthy roster beside them came from
    /// the window, so two different populations sat on adjacent lines with nothing saying which was which.</summary>
    [Fact]
    public void TheAccountedForBaselineCountsThePagesRowsNotTheWholeAudit()
    {
        var first = NativePairingWire.Render(EngineThenThirdParty(6, 4), null, 80_000, new RowWindow(0, 8));

        Assert.Contains("accounted for: 6 engine class(es)", first);
        Assert.Contains("paired healthy (2 class(es))", first);

        // The sharp case: a page holding none of the baseline classes must not reprint their count as if it did.
        var second = NativePairingWire.Render(EngineThenThirdParty(6, 4), null, 80_000, new RowWindow(6, 4));

        Assert.Contains("accounted for: 0 engine class(es)", second);
        Assert.Contains("paired healthy (4 class(es))", second);
        // The summary above the sections still states the whole audit, which is where the layer's numbers live.
        Assert.Contains("10 class(es) declare native functions", second);
    }

    /// <summary>The missing-loader alarm is a build-level fact, not a row: a window holding none of the SKSE-core
    /// classes must not silence it.</summary>
    [Fact]
    public void TheMissingLoaderAlarmRidesTheWholeAuditNotTheWindow()
    {
        var d = EngineThenThirdParty(2, 4) with
        {
            Classes = new[] { new NativeClassEntry("scripts/su.pex", "StringUtil", new[] { "Fn" }, new[] { Mod("SKSE") },
                                                   NativeProvenance.SkseCore, null, null, Array.Empty<NativePairedDll>()) }
                .Concat(Enumerable.Range(1, 4).Select(Cls)).ToList(),
            SkseLoaderSeen = false,
        };

        // The window starts past the one SKSE-core class, so the page carries none of them.
        var text = NativePairingWire.Render(d, null, 80_000, new RowWindow(1, 4));

        Assert.Contains("0 SKSE-core class(es)", text);   // the page holds none
        Assert.Contains("no skse64 loader is visible", text);   // the audit does, so the alarm still rides
    }

    // ---- the json lane's own reserve --------------------------------------------------------------

    /// <summary>The json tail — the caveats object and the accounting object — is paid for INSIDE max_chars, the way
    /// every text render's reserve pays for its accounting line. What can still overrun is one row, because the cap is
    /// tested before each row and not after; the tail here is ~12 KB of caveats inside a 20 KB cap, so an unreserved
    /// one is unmissable.</summary>
    [Theory]
    [InlineData("inventory")]
    [InlineData("pairing")]
    [InlineData("config")]
    public void TheJsonTailIsPaidForInsideMaxCharsRatherThanAppendedPastIt(string family)
    {
        const int cap = 20_000;
        var warnings = Warnings(60);
        string json = family switch
        {
            "inventory" => SkseInventoryWire.RenderJson(Inventory(40, warnings: warnings), null, cap),
            "pairing" => NativePairingWire.RenderJson(Pairing(40, warnings: warnings), null, cap),
            _ => SkseConfigAuditWire.RenderJson(ConfigAudit(40, warnings: warnings), null, cap),
        };

        using var doc = JsonDocument.Parse(json);   // still a document, never a byte-budget cut
        int tail = warnings.Sum(x => x.Length);
        Assert.True(json.Length < cap + OneRowSlack,
                    $"{family}: {json.Length} chars against max_chars={cap} — the ~{tail}-char tail was not reserved.");
    }

    /// <summary>The one row the cap can overrun by. Every family's row is comfortably under this, and every family's
    /// caveat tail in the test above is comfortably over it, so the bound tells the two apart.</summary>
    const int OneRowSlack = 2_000;

    /// <summary>One config can declare tens of thousands of form tokens — a large SkyPatcher INI is exactly that — so
    /// max_chars bounds the reference array inside a file too, not only the file rows. The text render already bounds
    /// references per line; an unbounded twin writes the whole array in one pass past the cap.</summary>
    [Fact]
    public void MaxCharsBoundsTheReferenceArrayInsideOneConfigFileToo()
    {
        const int cap = 3_000;
        string json = SkseConfigAuditWire.RenderJson(ConfigAudit(1, refs: 4_000), null, cap);

        using var doc = JsonDocument.Parse(json);
        Assert.True(json.Length < cap + OneRowSlack, $"{json.Length} chars against max_chars={cap}");
        var file = doc.RootElement.GetProperty("files").EnumerateArray().Single();
        // The cut is stated on the file that carries it: the accounting counts files, not references.
        Assert.Equal(4_000 - file.GetProperty("references").GetArrayLength(),
                     file.GetProperty("references_truncated").GetInt32());
    }

    /// <summary>The two json arrays that are NOT row-list rows — the inventory's config folders and the pairing's
    /// unreadable .pex list — say what the cap cut, the way their text twins do. The accounting cannot: it counts
    /// rows, and these are not rows.</summary>
    [Fact]
    public void TheNonRowJsonArraysMarkWhatMaxCharsCutFromThem()
    {
        using var inv = JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(1, configs: 400, folders: 400), null, 2_500));
        var folders = inv.RootElement.GetProperty("config_folders").GetArrayLength();
        Assert.True(folders < 400, "the cap should have cut folders");
        Assert.Equal(400 - folders, inv.RootElement.GetProperty("config_folders_truncated").GetInt32());

        using var pair = JsonDocument.Parse(NativePairingWire.RenderJson(Pairing(1, unreadable: 400), null, 2_500));
        var shown = pair.RootElement.GetProperty("unreadable_pex").GetArrayLength();
        Assert.True(shown < 400, "the cap should have cut unreadable .pex entries");
        Assert.Equal(400 - shown, pair.RootElement.GetProperty("unreadable_pex_truncated").GetInt32());
    }

    /// <summary>Nothing cut, no marker: the two arrays carry the notice only when there is a cut to name.</summary>
    [Fact]
    public void TheNonRowJsonArraysCarryNoCutMarkerWhenNothingWasCut()
    {
        using var inv = JsonDocument.Parse(SkseInventoryWire.RenderJson(Inventory(1, configs: 4, folders: 4), null, 200_000));
        Assert.False(inv.RootElement.TryGetProperty("config_folders_truncated", out _));

        using var pair = JsonDocument.Parse(NativePairingWire.RenderJson(Pairing(1, unreadable: 4), null, 200_000));
        Assert.False(pair.RootElement.TryGetProperty("unreadable_pex_truncated", out _));

        using var cfg = JsonDocument.Parse(SkseConfigAuditWire.RenderJson(ConfigAudit(1, refs: 4), null, 200_000));
        Assert.False(cfg.RootElement.GetProperty("files").EnumerateArray().Single()
                        .TryGetProperty("references_truncated", out _));
    }
}

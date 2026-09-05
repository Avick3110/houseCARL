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

    static SkseFileEntry Config(int i) =>
        new($"SKSE/Plugins/Group/c{i}.ini", $"c{i}.ini", "Group", new[] { Mod($"Mod{i}") }, null, null);

    static SkseInventoryData Inventory(int dlls, int configs = 0) =>
        new(Enumerable.Range(1, dlls).Select(Dll).ToList(),
            Enumerable.Range(1, configs).Select(Config).ToList(),
            OtherFileCount: 0, InstalledRuntime: "1.6.1170.0", BsaFailures: Array.Empty<string>(),
            ReadIncomplete: false, Warnings: Array.Empty<string>(), ProfileName: "Default");

    static NativeClassEntry Cls(int i) =>
        new($"scripts/k{i}.pex", $"Klass{i}", new[] { "Fn" }, new[] { Mod($"Mod{i}") },
            NativeProvenance.ThirdParty, NativePairingRung.SameMod, $"Mod{i}",
            new[] { new NativePairedDll($"SKSE/Plugins/p{i}.dll", $"p{i}.dll", "", $"Mod{i}",
                                        new SksePluginReader.SksePluginInfo($"p{i}.dll", SksePluginReader.SksePluginKind.Modern, true, Version($"Plugin {i}"), null,
                                                                            new[] { "kernel32.dll" }), null) });

    static NativePairingAuditData Pairing(int classes) =>
        new(Enumerable.Range(1, classes).Select(Cls).ToList(), PexScanned: classes,
            Unreadable: Array.Empty<NativeUnreadablePex>(), SkseLoaderSeen: true, InstalledRuntime: "1.6.1170.0",
            BsaFailures: Array.Empty<string>(), ReadIncomplete: false, Warnings: Array.Empty<string>(), ProfileName: "Default");

    static SkseConfigFileAudit ConfigFile(int i) =>
        new($"SKSE/Plugins/Group/f{i}.ini", $"f{i}.ini", "Group", $"Mod{i}", 1, new[] { Mod($"Mod{i}") },
            new[] { new SkseAuditedRef(new HousecarlCore.SkseConfigRef($"0x00080{i}|Ghost.esp",
                                                                       HousecarlCore.SkseRefShape.FormToken, "Ghost.esp", 0x800u + (uint)i, "0x00080" + i, 3, null),
                                       SkseRefVerdict.PluginMissing, "Ghost.esp is not in the active load order") },
            ReadError: null);

    static SkseConfigAuditData ConfigAudit(int files) =>
        new(Enumerable.Range(1, files).Select(ConfigFile).ToList(), ConfigCount: files,
            BsaFailures: Array.Empty<string>(), ReadIncomplete: false, Warnings: Array.Empty<string>(), ProfileName: "Default");

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
}

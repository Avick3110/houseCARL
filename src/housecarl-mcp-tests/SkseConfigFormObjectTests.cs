using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The SKSE config audit reads a JSON file's <c>{"id","plugin"}</c> form objects, the shape IED writes (#1101).</summary>
[Trait("tier", "integration")]
public sealed class SkseConfigFormObjectTests(SkseConfigVerdictOrder order) : IClassFixture<SkseConfigVerdictOrder>
{
    const string IedPath = @"SKSE\Plugins\IED\DefaultConfigUser.json";

    // IED's own layout: one line, a form under an odd-named entry, a deny list, and empty id-0 forms with and without a plugin.
    string IedConfig() =>
        "{\"data\":{\"custom\":{\"data\":{\"default_player\":{\"data\":{\"Carcass - Chicken\":{\"f\":{" +
        $"\"item\":{{\"id\":{order.FullFactionId},\"plugin\":\"hcAudit.esp\"}}," +
        "\"bflt\":{\"r\":{\"deny\":[{\"id\":11259375,\"plugin\":\"hcAudit.esp\"},{\"id\":2059,\"plugin\":\"NotInstalled.esp\"}]}}," +
        "\"vsrc\":{\"flags\":0,\"form\":{\"id\":0},\"src\":0},\"leqp\":{\"form\":{\"id\":0,\"plugin\":\"hcAudit.esp\"}}}}}}}}},\"version\":1}";

    List<SkseAuditedRef> Audit(string relPath, string text) =>
        SkseConfigReferenceExtractor.Extract(relPath, text).Select(r => AssetLayers.Adjudicate(r, order.Resolver.Capture())).ToList();

    [Fact]
    public void AnIedConfigGivesAGoodADanglingAndAMissingReferenceEachAtItsJsonPathAndSkipsTheEmptyForm()
    {
        var refs = Audit(IedPath, IedConfig());

        Assert.Equal(new[]
        {
            (SkseRefVerdict.Ok, "$.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.item"),
            (SkseRefVerdict.Dangling, "$.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.bflt.r.deny[0]"),
            (SkseRefVerdict.PluginMissing, "$.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.bflt.r.deny[1]"),
        }, refs.Select(r => (r.Verdict, r.Ref.Locator)).ToArray());
        Assert.All(refs, r => Assert.Equal((SkseRefShape.FormObject, 1), (r.Ref.Shape, r.Ref.Line)));   // the real line, even in a one-line file
        Assert.Equal(("hcAudit.esp", (uint?)0xABCDEF), (refs[1].Ref.Plugin, refs[1].Ref.LocalId));
        Assert.Equal("{\"id\":2059,\"plugin\":\"NotInstalled.esp\"}", refs[2].Ref.Raw);
    }

    [Fact]
    public void AFormObjectInAnIndentedFileCarriesItsLineAndCommentsDoNotStopTheRead()
    {
        var text = "{\n  // gear\n  \"slot\": [\n    {\"id\": 2059, \"plugin\": \"NotInstalled.esp\"},\n  ],\n}";
        var r = Assert.Single(SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json", text));
        Assert.Equal((4, "$.slot[0]", (string?)null), (r.Line, r.Locator, r.Unparseable));
    }

    [Fact]
    public void AStringFormTokenBesideAFormObjectIsStillReadAsBefore()
    {
        var refs = SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\Foo\x.json",
            "{\"a\": \"0x4FDAF|Skyrim.esm\", \"b\": {\"id\": 5, \"plugin\": \"Skyrim.esm\"}}");
        Assert.Equal(new[] { (SkseRefShape.FormToken, (uint?)0x4FDAF, (string?)null), (SkseRefShape.FormObject, (uint?)5, (string?)"$.b") },
            refs.Select(r => (r.Shape, r.LocalId, r.Locator)).ToArray());
    }

    [Fact]
    public void AnIdThatIsNotADecimalNumberIsNamedUnparseableNotGuessed()
    {
        var r = Assert.Single(SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json", "{\"f\": {\"id\": \"0x800\", \"plugin\": \"Skyrim.esm\"}}"));
        Assert.NotNull(r.Unparseable);
    }

    [Fact]
    public void AJsonFileThatBreaksBeforeAFormObjectIsOneUnparseableReferenceAfterTheFormsReadBeforeTheBreak()
    {
        var refs = SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json",
            "{\"a\": {\"id\": 5, \"plugin\": \"Skyrim.esm\"},\n\"b\": oops, \"c\": {\"id\": 6, \"plugin\": \"Skyrim.esm\"}}");
        Assert.Equal(new[] { ((uint?)5, (string?)null, 1), (null, "not valid JSON — form objects past this line are not read", 2) },
            refs.Select(r => (r.LocalId, r.Unparseable, r.Line)).ToArray());
    }

    [Fact]
    public void ANameThatStartsWithADigitIsBracketedInThePath()
        => Assert.Equal("$.slot[\"1h_axe\"]", Assert.Single(SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json",
            "{\"slot\": {\"1h_axe\": {\"id\": 5, \"plugin\": \"Skyrim.esm\"}}}")).Locator);

    [Fact]
    public void AnIniFileIsNotWalkedAsJson()
        => Assert.Empty(SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\Foo\x.ini", "{\"id\": 5, \"plugin\": \"Skyrim.esm\"}"));

    [Fact]
    public void TheFilteredAuditShowsEachFormObjectAtItsPathAndTheOverviewCountsAMissingOne()
    {
        var file = new SkseConfigFileAudit(IedPath, "DefaultConfigUser.json", "IED", "IEDMod", 1, new[] { new SkseProvider("IEDMod", "loose") },
            Audit(IedPath, IedConfig()), ReadError: null, MultiLine: false);
        var data = new SkseConfigAuditData(new[] { file }, 1, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "Default");

        var filtered = SkseConfigAuditWire.Render(data, "IED", 40_000);
        Assert.Contains("[DANGLING] '{\"id\":11259375,\"plugin\":\"hcAudit.esp\"}' (at $.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.bflt.r.deny[0])", filtered);

        var overview = SkseConfigAuditWire.Render(data, null, 40_000);
        Assert.Contains("NotInstalled.esp: 1 ref(s)", overview);
        Assert.Contains(IedPath + " $.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.bflt.r.deny[0]", overview);

        var json = SkseConfigAuditWire.RenderJson(data, "IED", 40_000);
        var row = System.Text.Json.JsonDocument.Parse(json).RootElement.EnumerateObject().SelectMany(p => Rows(p.Value))
            .Single(e => e.TryGetProperty("local_id", out var id) && id.GetString() == "0xABCDEF");
        Assert.Equal(("form_object", 1, "$.data.custom.data.default_player.data[\"Carcass - Chicken\"].f.bflt.r.deny[0]"),
            (row.GetProperty("shape").GetString(), row.GetProperty("line").GetInt32(), row.GetProperty("path").GetString()));
    }

    // Every object in the document that carries a "verdict", at any depth.
    static IEnumerable<System.Text.Json.JsonElement> Rows(System.Text.Json.JsonElement e) => e.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => (e.TryGetProperty("verdict", out _) ? new[] { e } : Array.Empty<System.Text.Json.JsonElement>())
            .Concat(e.EnumerateObject().SelectMany(p => Rows(p.Value))),
        System.Text.Json.JsonValueKind.Array => e.EnumerateArray().SelectMany(Rows),
        _ => Array.Empty<System.Text.Json.JsonElement>(),
    };

    [Fact]
    public void AFormObjectInAMultiLineFileShowsItsLineBesideItsPath()
    {
        const string rel = @"SKSE\Plugins\IED\x.json";
        var text = "{\n  \"slot\": [\n    {\"id\": 11259375, \"plugin\": \"hcAudit.esp\"}\n  ]\n}";
        var file = new SkseConfigFileAudit(rel, "x.json", "IED", "IEDMod", 1, new[] { new SkseProvider("IEDMod", "loose") },
            Audit(rel, text), ReadError: null);
        var data = new SkseConfigAuditData(new[] { file }, 1, Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "Default");

        Assert.Contains("(line 3, at $.slot[0])", SkseConfigAuditWire.Render(data, "IED", 40_000));
        Assert.Contains(rel + ":3 $.slot[0]", SkseConfigAuditWire.Render(data, null, 40_000));
    }

    [Fact]
    public void AnIdThatIsNotANumberOrStringKeepsItsRawTextAndIsUnparseable()
    {
        var refs = SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json",
            "{\"a\": {\"id\": null, \"plugin\": \"Skyrim.esm\"}, \"b\": {\"id\": true, \"plugin\": \"Skyrim.esm\"}, \"c\": {\"id\": [1, 2], \"plugin\": \"Skyrim.esm\"}}");
        Assert.Equal(new[] { "{\"id\":null,\"plugin\":\"Skyrim.esm\"}", "{\"id\":true,\"plugin\":\"Skyrim.esm\"}", "{\"id\":[1, 2],\"plugin\":\"Skyrim.esm\"}" },
            refs.Select(r => r.Raw).ToArray());
        Assert.All(refs, r => Assert.NotNull(r.Unparseable));
    }
}

/// <summary>The engine's hardcoded PlayerRef (000014:Skyrim.esm) is no record in Skyrim.esm, and the config audit reads it OK, not DANGLING.</summary>
[Trait("tier", "integration")]
public sealed class SkseConfigEngineImplicitTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-skse-implicit-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderResolver _resolver;

    public SkseConfigEngineImplicitTests()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "Skyrim.esm");
        var mod = new SkyrimMod(ModKey.FromNameAndExtension("Skyrim.esm"), SkyrimRelease.SkyrimSE);
        mod.Factions.AddNew().EditorID = "hcImplicitFac";
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _resolver = LoadOrderResolver.Build(new[] { path });
    }

    public void Dispose()
    {
        _resolver.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void PlayerRefInAConfigIsOkAndAnotherAbsentSkyrimFormIsStillDangling()
    {
        var verdicts = SkseConfigReferenceExtractor.Extract(@"SKSE\Plugins\IED\x.json",
                "{\"a\": {\"id\": 20, \"plugin\": \"Skyrim.esm\"}, \"b\": {\"id\": 21, \"plugin\": \"Skyrim.esm\"}}")
            .Select(r => AssetLayers.Adjudicate(r, _resolver.Capture()).Verdict).ToArray();
        Assert.Equal(new[] { SkseRefVerdict.Ok, SkseRefVerdict.Dangling }, verdicts);
    }
}

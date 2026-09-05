using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The S2 tools' TRANSPORT lane (#542, SPEC §2.1): asset_status and place both answer format='json' with
/// the same data the text render carries and the accounting in band. A surface missing one of the uniform axes is a
/// bug, so these arms hold each tool's json document against the same facts its text twin states.</summary>
[Trait("tier", "integration")]
public sealed class AssetStatusJsonLaneTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public AssetStatusJsonLaneTests(AssetSelectWorld w) => _w = w;

    static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    /// <summary>The document carries the winner and the whole provider chain as data — the provider NAME on its own,
    /// which is the token place's source_provider= takes, rather than the text lane's printed "name" (kind).</summary>
    [Fact]
    public void TheJsonLaneCarriesTheWinnerAndProviderChainAsData()
    {
        var contested = _w.Rel("0002.nif");
        var root = Parse(AssetTools.AssetStatus(_w.Svc, new[] { contested }, format: "json"));

        var row = root.GetProperty("results")[0];
        Assert.Equal(contested, row.GetProperty("path").GetString());
        Assert.True(row.GetProperty("exists").GetBoolean());
        var data = _w.Svc.AssetStatus(new[] { contested });
        var hit = Assert.Single(data.Results).Hit!;
        Assert.Equal(hit.Winner!.Source, row.GetProperty("winner").GetProperty("name").GetString());
        Assert.Equal(hit.Providers.Count, row.GetProperty("providers").GetArrayLength());
        foreach (var (p, i) in hit.Providers.Select((p, i) => (p, i)))
            Assert.Equal(p.Source, row.GetProperty("providers")[i].GetProperty("name").GetString());
        // The name alone, never the display token: a consumer must not have to parse " (loose)" back off it.
        Assert.DoesNotContain("(loose)", row.GetProperty("winner").GetProperty("name").GetString());
    }

    /// <summary>The same eight counters the text accounting line states, so a json consumer pages by the document
    /// instead of by prose it might miss.</summary>
    [Fact]
    public void TheJsonLaneCarriesTheSameAccountingTheTextLaneStates()
    {
        var root = Parse(AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                                limit: 2, format: "json"));

        Assert.Equal(AssetSelectWorld.FaceGeomFiles, root.GetProperty("total").GetInt32());
        Assert.Equal(2, root.GetProperty("rendered").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetInt32());
        Assert.Equal(AssetSelectWorld.FaceGeomFiles - 2, root.GetProperty("capped").GetInt32());
        Assert.Equal(0, root.GetProperty("truncated_rows").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        // The next page is measured off what was rendered, and carries the limit, exactly as the text advice does.
        Assert.Equal(2, root.GetProperty("next_limit").GetInt32());
        Assert.Equal(2, root.GetProperty("next_offset").GetInt32());
    }

    /// <summary>A cut document stays valid JSON and says it was cut — the text lane's answer is a cut StringBuilder,
    /// which is the one thing the json lane must not do.</summary>
    [Fact]
    public void ACutJsonDocumentIsStillValidJsonAndSaysItWasCut()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          format: "json", max_chars: 300);

        var root = Parse(text);   // parses, or the render cut mid-document
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("truncated_rows").GetInt32() > 0);
        Assert.Contains("max_chars=300", root.GetProperty("truncated_note").GetString());
    }

    /// <summary>A whole-call refusal answers a json caller as a document, not as a bare sentence — otherwise the
    /// json lane is a degraded mode exactly where the caller most needs to branch.</summary>
    [Fact]
    public void ARefusalAnswersAJsonCallerAsADocument()
    {
        var root = Parse(AssetTools.AssetStatus(_w.Svc, Array.Empty<string>(), format: "json"));

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Contains("both empty", root.GetProperty("error").GetString());
    }

    /// <summary>An unrecognized format is named rather than falling through to text on a typo.</summary>
    [Fact]
    public void AnUnrecognizedFormatIsRefusedByName()
    {
        var text = AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, format: "jsonn");

        Assert.Contains("format='jsonn' is not recognized", text);
    }

    /// <summary>place's own refusal takes the same shape through the same door.</summary>
    [Fact]
    public void PlaceRefusesAJsonCallerWithADocumentToo()
    {
        var root = Parse(PlaceTools.Place(_w.Svc, new[] { new PlaceTarget() }, format: "json"));

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Contains("exactly one of formid or path", root.GetProperty("error").GetString());
    }
}

/// <summary>place's json document at the render seam, against the same outcomes its text twin is held to: the
/// accounting in band, and the two things a caller cannot act without surviving a cap that cuts the list.</summary>
[Trait("tier", "unit")]
public class PlaceJsonRenderTests
{
    static PlaceOutcome Outcome(int ok, int failed) => new(
        Enumerable.Range(0, ok)
            .Select(i => new PlaceResult($"meshes/hc/ok{i}.nif", true, 42, "SomeMod (loose)", "OtherMod (loose)", null))
            .Concat(Enumerable.Range(0, failed)
                .Select(i => new PlaceResult($"meshes/hc/bad{i}.nif", false, 0, null, null, "nothing supplies this path")))
            .ToList(),
        @"C:\mods\houseCARL - MyFixes", Array.Empty<string>(), null, null);

    static JsonElement Parse(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void EveryDocumentCarriesTheAccountingAndTheEnableAndSortStep()
    {
        var root = Parse(JsonWire.RenderPlaceOutcome(Outcome(ok: 2, failed: 1), 80_000));

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal("houseCARL - MyFixes", root.GetProperty("mod_folder").GetString());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.Equal(3, root.GetProperty("rendered").GetInt32());
        Assert.Equal(2, root.GetProperty("placed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("\"wrote it\" is not \"it wins\"", root.GetProperty("next_step").GetString());
        // A failed row is a per-row error, never the document's discriminant.
        var bad = root.GetProperty("results")[2];
        Assert.False(bad.GetProperty("placed").GetBoolean());
        Assert.Equal("nothing supplies this path", bad.GetProperty("error").GetString());
    }

    [Fact]
    public void ACutDocumentStaysValidJsonAndKeepsWhatTheCallerMustDoNext()
    {
        var root = Parse(JsonWire.RenderPlaceOutcome(Outcome(ok: 4, failed: 0), 300));

        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("rendered").GetInt32() < 4);
        Assert.Equal(4, root.GetProperty("total").GetInt32());
        Assert.Contains("the WRITE is unaffected", root.GetProperty("truncated_note").GetString());
        Assert.Contains("\"wrote it\" is not \"it wins\"", root.GetProperty("next_step").GetString());
    }

    [Fact]
    public void ACallThatPlacedNothingClaimsNoEnableAndSortIsPending()
    {
        var root = Parse(JsonWire.RenderPlaceOutcome(Outcome(ok: 0, failed: 2), 80_000));

        Assert.Equal(0, root.GetProperty("placed").GetInt32());
        Assert.False(root.TryGetProperty("next_step", out _));
    }

    /// <summary>The document and its text twin say the same thing about the same outcome — the sentence has one home,
    /// so a json caller is never the one who is not told to enable the mod.</summary>
    [Fact]
    public void TheTwoLanesCarryTheSameNextStepSentence()
    {
        var o = Outcome(ok: 2, failed: 0);

        var json = Parse(JsonWire.RenderPlaceOutcome(o, 80_000)).GetProperty("next_step").GetString();

        Assert.Contains(json!, PlaceWire.Render(o, 80_000));
    }
}

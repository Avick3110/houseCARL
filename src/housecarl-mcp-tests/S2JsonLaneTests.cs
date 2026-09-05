using System.Text.Json;
using HousecarlCore;
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

        // Under ONE accounting object, the shape every §2.1 json lane states them in.
        var acct = root.GetProperty("accounting");
        Assert.Equal(AssetSelectWorld.FaceGeomFiles, acct.GetProperty("total").GetInt32());
        Assert.Equal(2, acct.GetProperty("rendered").GetInt32());
        Assert.Equal(0, acct.GetProperty("skipped").GetInt32());
        Assert.Equal(AssetSelectWorld.FaceGeomFiles - 2, acct.GetProperty("capped").GetInt32());
        Assert.Equal(0, acct.GetProperty("truncated").GetInt32());
        // The four omissions have four distinct causes and sum to the total, on this lane as on the text one.
        Assert.Equal(acct.GetProperty("total").GetInt32(),
                     acct.GetProperty("skipped").GetInt32() + acct.GetProperty("rendered").GetInt32()
                     + acct.GetProperty("truncated").GetInt32() + acct.GetProperty("capped").GetInt32());
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
        Assert.True(root.GetProperty("accounting").GetProperty("truncated").GetInt32() > 0);
        Assert.Contains("max_chars=300", root.GetProperty("truncated_note").GetString());
    }

    /// <summary>The one path the caller asked about is answered however small max_chars is — the text lane's
    /// "the FIRST item always renders its core answer" rule, which a json lane that checked its budget before the
    /// first row would drop, handing back an empty results array for a question it had already resolved.</summary>
    [Fact]
    public void TheFirstRowRendersHoweverSmallMaxCharsIs()
    {
        var root = Parse(AssetTools.AssetStatus(_w.Svc, new[] { _w.Rel("0001.nif") }, format: "json", max_chars: 120));

        var row = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal(_w.Rel("0001.nif"), row.GetProperty("path").GetString());
        Assert.Equal(1, root.GetProperty("accounting").GetProperty("rendered").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    /// <summary>The build-level caveats are bounded by the same max_chars the rows are. Unbounded they are the one
    /// block max_chars does not reach: a sweep of many selectors that match nothing writes them all before the row
    /// loop takes its first reading, and every row the caller asked about is then cut.</summary>
    [Fact]
    public void TheCaveatBlocksAreCappedByMaxCharsToo()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"meshes/hcnothing{i}/**/*.nif").ToArray();

        var capped = AssetTools.AssetStatus(_w.Svc, under: many, format: "json", max_chars: 2_000);
        var whole = AssetTools.AssetStatus(_w.Svc, under: many, format: "json");

        var root = Parse(capped);
        var notes = root.GetProperty("selector_notes");
        Assert.True(notes.GetArrayLength() < 400, "the note list was written past max_chars");
        // The cut is a sibling COUNT, and the array stays pure data — a consumer iterating it never has to
        // substring-match a prose marker out of the entries, and the two numbers still add up to the whole.
        int omitted = root.GetProperty("selector_notes_omitted").GetInt32();
        Assert.Equal(400, notes.GetArrayLength() + omitted);
        foreach (var n in notes.EnumerateArray()) Assert.DoesNotContain("omitted at max_chars", n.GetString());
        // The cut is real, not cosmetic: the same call unbounded is many times the document.
        Assert.True(capped.Length < whole.Length / 4, $"capped={capped.Length} whole={whole.Length}");
        // Every selector is still COUNTED, whatever the render could show of them.
        Assert.Equal(400, root.GetProperty("accounting").GetProperty("notes").GetInt32());
        // A document nothing was dropped from says so too.
        Assert.Equal(0, Parse(whole).GetProperty("selector_notes_omitted").GetInt32());
    }

    /// <summary>A document whose CAVEATS were cut reports itself truncated. The accounting counters count rows only,
    /// so a call that resolved every row it selected but lost caveat entries to the budget would otherwise say
    /// truncated=false, and a consumer branching on that flag to re-call would conclude nothing was lost.</summary>
    [Fact]
    public void ADocumentWhoseCaveatsWereCutSaysItWasTruncated()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"meshes/hcnothing{i}/**/*.nif").ToArray();

        var root = Parse(AssetTools.AssetStatus(_w.Svc, under: many, format: "json", max_chars: 2_000));

        // No row was dropped — none was selected — and yet the document is not complete.
        Assert.Equal(0, root.GetProperty("accounting").GetProperty("truncated").GetInt32());
        Assert.True(root.GetProperty("selector_notes_omitted").GetInt32() > 0);
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Contains("max_chars=2000", root.GetProperty("truncated_note").GetString());
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

    /// <summary>The accounting is paid for INSIDE max_chars, as the text twin pays for its accounting line: room for
    /// the tail is held back before the rows write, so a document that rendered more than its first row fits the cap
    /// the caller passed rather than overrunning it by the whole accounting block.</summary>
    [Fact]
    public void TheDocumentFitsTheMaxCharsItWasGiven()
    {
        var text = AssetTools.AssetStatus(_w.Svc, under: new[] { AssetSelectWorld.FaceGeomDir },
                                          format: "json", max_chars: 2_000);

        var root = Parse(text);
        Assert.True(root.GetProperty("accounting").GetProperty("rendered").GetInt32() > 1);
        // The tail BEGINS inside the cap — that is what the reserve buys. The document can still end a little past
        // the cap, on the row that was in flight when the budget ran out (the same one-item overshoot the text lane
        // makes), but never past it by the whole accounting block, which is what an unreserved tail costs.
        int tailAt = text.IndexOf("\"accounting\"", StringComparison.Ordinal);
        Assert.True(tailAt > 0 && tailAt <= 2_000, $"the accounting starts at {tailAt} on max_chars=2000");
        Assert.True(text.Length - 2_000 < text.Length - tailAt,
                    $"the document is {text.Length} chars — over by more than its own tail");
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

/// <summary>place's json lane driven END TO END through the tool, not at the render seam: that the tool routes
/// format='json' to the json renderer at all, and that the withheld set-level pole reaches the document. Its own
/// fixture instance, because the arm WRITES a mod folder into the world it runs against.</summary>
[Trait("tier", "integration")]
public sealed class PlaceJsonServedLaneTests : IClassFixture<AssetSelectWorld>
{
    readonly AssetSelectWorld _w;
    public PlaceJsonServedLaneTests(AssetSelectWorld w) => _w = w;

    /// <summary>A real placement answered as a document: the source is one exact file on disk, which the set-level
    /// source_provider= cannot apply to — so the pole is withheld, and the row SAYS it was, on this lane as on the
    /// text one. A caller reading a placed row with the pole silently dropped would believe it was honoured.</summary>
    [Fact]
    public void AServedPlaceAnswersAsADocumentAndSaysTheSetPoleWasWithheld()
    {
        var onDisk = Path.Combine(_w.Root, "instance", "mods", "FaceBase", _w.Rel("0001.nif"));

        var text = PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Path = @"meshes\hcjson\served.nif", Source = onDisk } },
            source_provider: "FaceHigher", format: "json", patch: "JsonServed");
        var root = JsonDocument.Parse(text).RootElement;   // parses, or format='json' never reached the json renderer

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(1, root.GetProperty("placed").GetInt32());
        var row = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.True(row.GetProperty("placed").GetBoolean());
        Assert.True(row.GetProperty("set_provider_withheld").GetBoolean());
        // Nothing else provides this destination, so the enable-and-sort step says so rather than naming a winner.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("current_winner").ValueKind);
        Assert.Contains("\"wrote it\" is not \"it wins\"", root.GetProperty("next_step").GetString());
    }
}

/// <summary>asset_status's json document at the render seam, where a build's caveats can be posed directly: the two
/// ABSENT hedges are two distinct facts with two distinct remedies, and a json consumer must be able to tell which
/// one applies without re-deriving it.</summary>
[Trait("tier", "unit")]
public class AssetStatusJsonHedgeTests
{
    static AssetStatusData Absent(bool readIncomplete, bool discoveryIncomplete) => new(
        new[] { new AssetPathResult("meshes/gone.nif", new AssetHit("meshes/gone.nif", false, null, Array.Empty<AssetProvider>(), false), null) },
        readIncomplete ? new[] { "HcArch.bsa — unreadable" } : Array.Empty<string>(),
        readIncomplete,
        discoveryIncomplete ? new[] { "no Skyrim.ini found; base archives were not scanned" } : Array.Empty<string>(),
        "Default");

    static JsonElement Row(AssetStatusData d)
        => JsonDocument.Parse(JsonWire.RenderAssetStatus(d, 80_000)).RootElement.GetProperty("results")[0];

    [Fact]
    public void TheTwoAbsentHedgesAreStatedApart()
    {
        var readOnly_ = Row(Absent(readIncomplete: true, discoveryIncomplete: false));
        Assert.True(readOnly_.GetProperty("absent_may_be_incomplete_read_failure").GetBoolean());
        Assert.False(readOnly_.GetProperty("absent_may_be_incomplete_undiscovered_archives").GetBoolean());

        var discoveryOnly = Row(Absent(readIncomplete: false, discoveryIncomplete: true));
        Assert.False(discoveryOnly.GetProperty("absent_may_be_incomplete_read_failure").GetBoolean());
        Assert.True(discoveryOnly.GetProperty("absent_may_be_incomplete_undiscovered_archives").GetBoolean());

        var neither = Row(Absent(readIncomplete: false, discoveryIncomplete: false));
        Assert.False(neither.GetProperty("absent_may_be_incomplete_read_failure").GetBoolean());
        Assert.False(neither.GetProperty("absent_may_be_incomplete_undiscovered_archives").GetBoolean());
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

    /// <summary>However small max_chars is, one row renders: on this document that row is the only place
    /// current_winner — the mod the caller has to sort the new one above — is stated.</summary>
    [Fact]
    public void TheFirstDestinationRowRendersHoweverSmallMaxCharsIs()
    {
        var root = Parse(JsonWire.RenderPlaceOutcome(Outcome(ok: 3, failed: 0), 1));

        var row = Assert.Single(root.GetProperty("results").EnumerateArray());
        Assert.Equal("OtherMod (loose)", row.GetProperty("current_winner").GetString());
        Assert.Equal(1, root.GetProperty("rendered").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
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

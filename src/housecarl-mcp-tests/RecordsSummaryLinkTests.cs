using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// resolve_names on a line whose FormID is rendered by a container element's SUMMARY rather than by a round-trip
/// token — "Effects[0] = [Effect] BaseEffect=033975:Skyrim.esm" at depth 2. The annotation follows the FormID the
/// read shows, not the carrier it happens to sit on.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsSummaryLinkTests : RecordsTestBase
{
    public RecordsSummaryLinkTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Effects(bool names) =>
        new() { form = "fields", fields = new[] { "Effects" }, depth = 2, resolve_names = names };

    string Spell(bool names, string? format = null) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Effects(names), format: format);

    [Fact]
    public void AFormIdRenderedInAnElementSummaryIsAnnotatedLikeALeafToken()
    {
        var r = Spell(names: true);
        Served(r, $"Effects[0] = [Effect] BaseEffect={Fid(W.MgefA)}");   // the summary itself, unchanged
        Assert.Contains($"BaseEffect={Fid(W.MgefA)}   (→ HcRecMgefFire)", r);
    }

    [Fact]
    public void ADeeperReadNamesTheSummaryAndItsLeafBecauseBothShowTheFormId()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects" }, depth = 3, resolve_names = true });
        // depth=3 prints the element's own leaf under its summary, and the fields lane spells the FormID on both.
        // The name follows the FormID, so it is on both too — the repetition is the read's, not the annotation's.
        Assert.Contains($"Effects[0] = [Effect] BaseEffect={Fid(W.MgefA)}   (→ HcRecMgefFire)", r);
        Assert.Contains($"Effects[0].BaseEffect = {Fid(W.MgefA)}   (→ HcRecMgefFire)", r);
    }

    [Fact]
    public void WithoutResolveNamesTheSummaryStandsAlone()
    {
        var r = Spell(names: false);
        Served(r, $"Effects[0] = [Effect] BaseEffect={Fid(W.MgefA)}");
        Assert.DoesNotContain("HcRecMgefFire", r);
    }

    JsonElement JsonEffectZero(bool names = true) =>
        Je(Spell(names, format: "json")).GetProperty("records")[0].GetProperty("fields")
          .EnumerateArray().Single(f => f.GetProperty("path").GetString() == "Effects[0]");

    [Fact]
    public void TheJsonSummaryLineCarriesTheIdentityAsALinkSiblingOfItsNote()
    {
        var field = JsonEffectZero();
        // The note is prose and stays prose; the identity is structure beside it.
        Assert.Equal($"[Effect] BaseEffect={Fid(W.MgefA)}", field.GetProperty("note").GetString());
        var link = field.GetProperty("link");
        Assert.True(link.GetProperty("resolved").GetBoolean());
        Assert.Equal("HcRecMgefFire", link.GetProperty("editorid").GetString());
    }

    [Fact]
    public void TheJsonSummaryLineCarriesTheFormIdItRenderedBesideTheProse()
    {
        // The FormID inside the note is the answer to "which record is this element", and a consumer reads it
        // back through the same formids= door — so it rides as structure, not only inside the prose. It is the
        // line's own shape, not the annotation's: it is there whether or not resolve_names was asked for.
        Assert.Equal(Fid(W.MgefA), JsonEffectZero().GetProperty("note_ref").GetString());
        Assert.Equal(Fid(W.MgefA), JsonEffectZero(names: false).GetProperty("note_ref").GetString());
    }
}

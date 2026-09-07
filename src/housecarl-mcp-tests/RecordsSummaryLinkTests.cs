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
    public void WithoutResolveNamesTheSummaryStandsAlone()
    {
        var r = Spell(names: false);
        Served(r, $"Effects[0] = [Effect] BaseEffect={Fid(W.MgefA)}");
        Assert.DoesNotContain("HcRecMgefFire", r);
    }

    [Fact]
    public void TheJsonSummaryLineCarriesTheIdentityAsALinkSiblingOfItsNote()
    {
        var field = Je(Spell(names: true, format: "json")).GetProperty("records")[0].GetProperty("fields")
                     .EnumerateArray().Single(f => f.GetProperty("path").GetString() == "Effects[0]");
        // The note is prose and stays prose; the identity is structure beside it.
        Assert.Equal($"[Effect] BaseEffect={Fid(W.MgefA)}", field.GetProperty("note").GetString());
        var link = field.GetProperty("link");
        Assert.True(link.GetProperty("resolved").GetBoolean());
        Assert.Equal("HcRecMgefFire", link.GetProperty("editorid").GetString());
    }
}

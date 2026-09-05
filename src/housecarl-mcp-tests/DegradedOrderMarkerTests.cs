using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The degraded-order marker (#353) on a build that LOST a plugin to a load failure. Driven on
/// <see cref="EpochWorld"/>, whose HcEpBad.esp is enabled and unparseable, so the index build excludes it and every
/// read after answers off the narrowed order. The read here never NAMES the failed plugin — that path already
/// refuses — so without the marker the response is indistinguishable from one off a healthy, legitimately reordered
/// build.</summary>
[Collection("epoch")]
[Trait("tier", "integration")]
public sealed class DegradedOrderMarkerTests
{
    readonly EpochWorld W;
    public DegradedOrderMarkerTests(EpochFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    string Fid => $"{W.Weapon.ID:X6}:{W.Weapon.ModKey.FileName}";

    static RecordsTools.RecordsProject Eid => new() { form = "fields", fields = new[] { "EditorID" } };

    [Fact]
    public void ADegradedBuildsJsonResponseCarriesTheMarkerAndNamesWhatIsMissing()
    {
        var json = RecordsTools.Records(Svc, formids: new[] { Fid }, project: Eid, format: "json");
        var root = JsonDocument.Parse(json).RootElement;

        Assert.True(root.GetProperty("order_degraded").GetBoolean());
        var note = root.GetProperty("order_degraded_note").GetString()!;
        Assert.Contains(EpochWorld.BadName, note);                  // which plugin is missing
        Assert.Contains("load FAILURE", note);                      // failure, not a change the caller made
        Assert.Contains("housecarl_load_order_status", note);       // where the reason is
    }

    [Fact]
    public void ADegradedBuildsTextHeadCarriesTheClauseBesideTheEpoch()
    {
        var text = RecordsTools.Records(Svc, formids: new[] { Fid }, project: Eid);

        Assert.Contains($"epoch={Svc.Stats().epoch} · 1 plugin(s) excluded (load failure)", text);
    }
}

/// <summary>The same read on a HEALTHY order: the marker is absent from both lanes, so a normal session gains no
/// noise. Its own class because the healthy world is a different collection.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class HealthyOrderMarkerTests
{
    readonly RecordsWorld W;
    public HealthyOrderMarkerTests(RecordsFixture f) => W = f.W;

    [Fact]
    public void AHealthyBuildCarriesNoMarkerOnEitherLane()
    {
        var fk = W.Weapons[0];
        var fid = $"{fk.ID:X6}:{fk.ModKey.FileName}";
        var project = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "EditorID" } };

        var json = RecordsTools.Records(W.Svc, formids: new[] { fid }, project: project, format: "json");
        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("order_degraded", out _));
        Assert.False(root.TryGetProperty("order_degraded_note", out _));

        var text = RecordsTools.Records(W.Svc, formids: new[] { fid }, project: project);
        Assert.DoesNotContain("excluded (load failure)", text);
    }
}

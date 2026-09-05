using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The degraded-order marker (#353) on a build that LOST a plugin to a load failure. Driven on
/// <see cref="EpochWorld"/>, whose HcEpBad.esp is enabled and unparseable, so the index build excludes it and every
/// read after answers off the narrowed order. The reads here never NAME the failed plugin — that path already
/// refuses — so without the marker the response is indistinguishable from one off a healthy, legitimately reordered
/// build.
///
/// <para>The PR's claim is about every stamped lane, so there is a case per lane: the record read, the scan, the
/// merged check's per-family heads — the dialogue family's among them, on a seed that resolves — the check ROOT
/// (which a dialogue-only call is the only lane to reach), and the write lane's dry run.</para></summary>
[Collection("epoch")]
[Trait("tier", "integration")]
public sealed class DegradedOrderMarkerTests
{
    readonly EpochWorld W;
    public DegradedOrderMarkerTests(EpochFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    string Fid => $"{W.Weapon.ID:X6}:{W.Weapon.ModKey.FileName}";

    static RecordsTools.RecordsProject Eid => new() { form = "fields", fields = new[] { "EditorID" } };

    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    const string Clause = "1 plugin(s) excluded (load failure)";

    /// <summary>The json marker: the flag, and the sentence saying which plugin, that it is a failure, and where the
    /// reason is.</summary>
    static void AssertMarked(JsonElement o)
    {
        Assert.True(o.GetProperty("order_degraded").GetBoolean());
        var note = o.GetProperty("order_degraded_note").GetString()!;
        Assert.Contains(EpochWorld.BadName, note);                  // which plugin is missing
        Assert.Contains("load FAILURE", note);                      // a failed load, not a reorder
        Assert.DoesNotContain("not a change you made", note);       // the open-failure class often IS one
        Assert.Contains("housecarl_load_order_status", note);       // where the reason is
    }

    [Fact]
    public void ADegradedBuildsJsonResponseCarriesTheMarkerAndNamesWhatIsMissing()
    {
        var json = RecordsTools.Records(Svc, formids: new[] { Fid }, project: Eid, format: "json");
        AssertMarked(JsonDocument.Parse(json).RootElement);
    }

    [Fact]
    public void ADegradedBuildsTextHeadCarriesTheClauseBesideTheEpoch()
    {
        var text = RecordsTools.Records(Svc, formids: new[] { Fid }, project: Eid);

        Assert.Contains($"epoch={Svc.Stats().epoch} · {Clause}", text);
    }

    /// <summary>The scan lane: its stamp is the one paged windows tile within, and it is written by the same shared
    /// writer, so it carries the marker too.</summary>
    [Fact]
    public void TheScanLaneCarriesTheMarkerOnBothTransports()
    {
        var json = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Eid, format: "json");
        AssertMarked(JsonDocument.Parse(json).RootElement);

        var text = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Eid);
        Assert.Contains(Clause, text);
    }

    /// <summary>The merged check: the errors family's own head, and the response ROOT. The root is the lane a
    /// consumer following the changelog reads, and the one a family stamp cannot supply.</summary>
    [Fact]
    public void TheCheckDocumentCarriesTheMarkerAtItsRootAndOnTheErrorsFamily()
    {
        var json = CheckTools.CheckTool(Svc, format: "json");
        var root = JsonDocument.Parse(json).RootElement;

        AssertMarked(root);

        // The family head says the flag and the COUNT, matching its text head; the sentence is the root's alone,
        // so one document does not carry it three times.
        var errors = root.GetProperty("families").GetProperty("errors");
        Assert.True(errors.GetProperty("order_degraded").GetBoolean());
        Assert.Equal(1, errors.GetProperty("order_degraded_plugins").GetInt32());
        Assert.False(errors.TryGetProperty("order_degraded_note", out _));
    }

    [Fact]
    public void TheCheckTextResponseSaysTheOrderLostPluginsAboveItsFamilies()
    {
        var text = CheckTools.CheckTool(Svc);

        Assert.Contains(EpochWorld.BadName, text);
        Assert.Contains("load FAILURE", text);
    }

    /// <summary>A DIALOGUE-ONLY check. Before the root marker existed this response was silent about an order missing
    /// a plugin on both transports — the failure #353 exists to end. Its seeds resolve to nothing here, which is a
    /// family refusal, not a reason to drop the marker.</summary>
    [Fact]
    public void ADialogueOnlyCheckStillSaysTheOrderLostPlugins()
    {
        var json = CheckTools.CheckTool(Svc, findings: new[] { "dialogue" }, seeds: new[] { Fid }, format: "json");
        AssertMarked(JsonDocument.Parse(json).RootElement);

        var text = CheckTools.CheckTool(Svc, findings: new[] { "dialogue" }, seeds: new[] { Fid });
        Assert.Contains("load FAILURE", text);
    }

    /// <summary>A dialogue call whose seed RESOLVES, so the family renders a head instead of refusing. The head is the
    /// lane the sibling families are asserted on above, and the one the refusal case cannot reach: it stamps its own
    /// `epoch`, so it has to carry the flag and the count beside it on json and the clause beside `epoch=` on text.
    /// </summary>
    [Fact]
    public void TheDialogueFamilyHeadCarriesTheMarkerBesideItsOwnStamp()
    {
        var seed = $"{W.View.ID:X6}:{W.View.ModKey.FileName}";

        var json = CheckTools.CheckTool(Svc, findings: new[] { "dialogue" }, seeds: new[] { seed }, format: "json");
        var fam = JsonDocument.Parse(json).RootElement.GetProperty("families").GetProperty("dialogue");
        Assert.False(string.IsNullOrEmpty(fam.GetProperty("epoch").GetString()));
        Assert.True(fam.GetProperty("order_degraded").GetBoolean());
        Assert.Equal(1, fam.GetProperty("order_degraded_plugins").GetInt32());
        // The sentence stays the root's alone, as it does on the errors family.
        Assert.False(fam.TryGetProperty("order_degraded_note", out _));

        var text = CheckTools.CheckTool(Svc, findings: new[] { "dialogue" }, seeds: new[] { seed });
        Assert.Contains($"epoch={Svc.Stats().epoch} · {Clause}", text);
    }

    /// <summary>The write lane, on a dry run so the shared world is not touched. A write's stamp is what tells a
    /// caller whether the winner it edited is the winner it read, so a degraded build has to say so here too.
    /// </summary>
    [Fact]
    public void TheWriteLaneCarriesTheClauseBesideItsStamp()
    {
        var text = ApplyTools.Apply(Svc,
            ops: Je($@"[{{""formid"":""{Fid}"",""field_path"":""BasicStats.Damage"",""op"":""Set"",""value"":""12""}}]"),
            dry_run: true);

        Assert.Contains(Clause, text);
    }
}

/// <summary>The same reads on a HEALTHY order: the marker is absent from every lane, so a normal session gains no
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

    [Fact]
    public void AHealthyOrdersCheckSaysNothingAboutLostPlugins()
    {
        var json = CheckTools.CheckTool(W.Svc, format: "json");
        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(root.TryGetProperty("order_degraded", out _));
        Assert.False(root.GetProperty("families").GetProperty("errors").TryGetProperty("order_degraded", out _));

        Assert.DoesNotContain("load FAILURE", CheckTools.CheckTool(W.Svc));
    }
}

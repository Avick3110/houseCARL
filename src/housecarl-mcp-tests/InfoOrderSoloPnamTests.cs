using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>One plugin defining two topics nothing else touches: in one a PNAM places a line away from its file
/// position, in the other the PNAM chain matches the file order.</summary>
public sealed class SoloPnamWorld : IDisposable
{
    public const string PluginName = "HcSoloPnam.esp";
    /// <summary>An unticked draft, in a disabled mod, that alone defines a topic whose PNAM disagrees with its file order.</summary>
    public const string DraftName = "HcSoloDraft.esp";
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public FormKey Disagree { get; }
    public FormKey Agree { get; }
    /// <summary>The disagreeing topic's lines in FILE order: C's PNAM names A, so the merge places A, C, B.</summary>
    public FormKey A { get; }
    public FormKey B { get; }
    public FormKey C { get; }
    public FormKey DraftTopic { get; }

    public SoloPnamWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-solo-pnam-" + Guid.NewGuid().ToString("N"));
        var instance = SyntheticInstance.Create(Root);

        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        SyntheticInstance.WriteMod(instance, "VanillaStub", sky);

        var mod = new SkyrimMod(ModKey.FromNameAndExtension(PluginName), SkyrimRelease.SkyrimSE);
        DialogResponses Line(string edid) => new(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = edid };

        var disagree = mod.DialogTopics.AddNew(); disagree.EditorID = "HcSoloDisagree";
        var a = Line("HcSoloA"); var b = Line("HcSoloB"); var c = Line("HcSoloC");
        c.PreviousDialog.SetTo(a.FormKey);
        disagree.Responses.Add(a); disagree.Responses.Add(b); disagree.Responses.Add(c);

        var agree = mod.DialogTopics.AddNew(); agree.EditorID = "HcSoloAgree";
        var x = Line("HcSoloX"); var y = Line("HcSoloY"); var z = Line("HcSoloZ");
        y.PreviousDialog.SetTo(x.FormKey);
        z.PreviousDialog.SetTo(y.FormKey);
        agree.Responses.Add(x); agree.Responses.Add(y); agree.Responses.Add(z);

        SyntheticInstance.WriteMod(instance, "SoloPnam", mod);

        var draft = new SkyrimMod(ModKey.FromNameAndExtension(DraftName), SkyrimRelease.SkyrimSE);
        var draftTopic = draft.DialogTopics.AddNew(); draftTopic.EditorID = "HcSoloDraftTopic";
        var p = new DialogResponses(draft.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcSoloDraftP" };
        var q = new DialogResponses(draft.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcSoloDraftQ" };
        var t = new DialogResponses(draft.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcSoloDraftT" };
        t.PreviousDialog.SetTo(p.FormKey);
        draftTopic.Responses.Add(p); draftTopic.Responses.Add(q); draftTopic.Responses.Add(t);
        SyntheticInstance.WriteMod(instance, "SoloDraft", draft);
        DraftTopic = draftTopic.FormKey;

        SyntheticInstance.WriteProfile(instance,
            new[] { "# header", "-SoloDraft", "+SoloPnam", "+VanillaStub" },
            new[] { "# header", "Skyrim.esm", PluginName },
            new[] { "*" + PluginName });

        (Disagree, Agree, A, B, C) = (disagree.FormKey, agree.FormKey, a.FormKey, b.FormKey, c.FormKey);
        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>#1148: on a topic only one plugin touches, info_order shows the PNAM-applied order and marks the lines
/// whose file position differs, in text and json; a topic whose PNAM chain matches its file order is unchanged.
/// format='dense' refuses the info_order form, so it has no listing to change.</summary>
[Trait("tier", "integration")]
public sealed class InfoOrderSoloPnamTests : IClassFixture<SoloPnamWorld>
{
    readonly SoloPnamWorld W;
    public InfoOrderSoloPnamTests(SoloPnamWorld w) => W = w;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    string InfoOrder(FormKey topic, string? format = null, string? source = null) =>
        RecordsTools.Records(W.Svc, formids: new[] { Fid(topic) }, format: format,
                             project: new RecordsTools.RecordsProject { form = "info_order" },
                             source: source is null ? null : JsonDocument.Parse($"\"{source}\"").RootElement.Clone());

    [Fact]
    public void APnamThatDisagreesWithFileOrderIsListedInPlaceOrderAndMarked()
    {
        var r = InfoOrder(W.Disagree);
        Assert.Contains($"#1  {Fid(W.A)}  placed by", r);
        Assert.Contains($"#2  {Fid(W.C)}  placed by", r);
        Assert.Contains($"#3  {Fid(W.B)}  MOVED from #2  placed by", r);
        Assert.Contains("untested", r);
        Assert.DoesNotContain("IS that plugin's own list", r);
        // The lead says relative order changed, as Moved measures it, not that a line sits at another position.
        Assert.Contains($"1 line changed order against {SoloPnamWorld.PluginName}'s own file order", r);
        Assert.DoesNotContain("different position", r);
    }

    /// <summary>An unticked draft that alone defines the topic, folded with source=: the fold note no longer claims
    /// the order is its own list when its PNAM moved a line; it says only that the lines are its own.</summary>
    [Fact]
    public void AFoldedSoloDraftWhosePnamMovesALineDoesNotCallTheOrderItsOwnList()
    {
        var r = InfoOrder(W.DraftTopic, source: SoloPnamWorld.DraftName);
        Assert.DoesNotContain("error=", r);
        Assert.Contains("MOVED from #2", r);
        Assert.Contains($"1 line changed order against {SoloPnamWorld.DraftName}'s own file order", r);
        Assert.Contains("the only plugin listing lines here, so every line shown is its own", r);
        Assert.DoesNotContain("its own list", r);
    }

    [Fact]
    public void APnamChainThatMatchesFileOrderKeepsTheOneLineAnswer()
    {
        var r = InfoOrder(W.Agree);
        Assert.Contains("IS that plugin's own list", r);
        Assert.DoesNotContain("MOVED", r);
        Assert.DoesNotContain("untested", r);
    }

    [Fact]
    public void JsonCarriesThePlaceOrderAndTheFilePositionOfTheMovedLine()
    {
        using var doc = JsonDocument.Parse(InfoOrder(W.Disagree, "json"));
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.False(row.GetProperty("contested").GetBoolean());
        var order = row.GetProperty("order").EnumerateArray().ToList();
        Assert.Equal(new[] { Fid(W.A), Fid(W.C), Fid(W.B) }, order.Select(e => e.GetProperty("info").GetString()));
        var moved = Assert.Single(order, e => e.TryGetProperty("moved", out _));
        Assert.Equal(Fid(W.B), moved.GetProperty("info").GetString());
        Assert.Equal(2, moved.GetProperty("origin_position").GetInt32());
    }
}

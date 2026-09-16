using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The info_order off-order fold (#694): <c>source=</c> on <c>project={"form":"info_order"}</c> names ONE plugin
/// that is not in the active order, and the merge answers as it WOULD be with that file enabled at the END of the
/// order. The facts asserted here are the ones a caller acts on — where a folded line lands, that the row says
/// which file put it there, and that the projection is never presented as the live order.
/// </summary>
[Collection("dialogue")]
[Trait("tier", "integration")]
public sealed class RecordsInfoOrderFoldTests
{
    readonly DialogueWorld W;
    public RecordsInfoOrderFoldTests(DialogueFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    string Folded(FormKey topic, string? source = null) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(topic) },
                             project: new RecordsTools.RecordsProject { form = "info_order" },
                             source: source is null ? null : Je(source));

    /// <summary>The line the patch re-lists with NO PNAM is evicted from its position and appended to the BOTTOM —
    /// the same tail arm an enabled plugin takes — and the line it re-lists WITH a PNAM lands immediately after the
    /// line that PNAM names. Both rows name the file that placed them. Read beside the live order, which the same
    /// topic still gives without the fold.</summary>
    [Fact]
    public void AFoldedLineLandsWhereItsPnamPutsItAndAtTheBottomWithout()
    {
        var live = Folded(W.Topic);
        Assert.Contains($"#8  {Fid(W.MovedLine)}", live);
        Assert.DoesNotContain(DialogueWorld.PatchName, live);

        var r = Folded(W.Topic, $"\"{DialogueWorld.PatchName}\"");

        // PNAM names INFO 1, so the re-listed INFO 5 sits immediately after it, at position 2.
        Assert.Contains($"#2  {Fid(W.Info[5])}", r);
        // No PNAM: INFO 3 is evicted from the middle and appended last, behind every line that was beneath it.
        Assert.Contains($"#8  {Fid(W.Info[3])}", r);
        foreach (var line in new[] { W.Info[5], W.Info[3] })
        {
            var row = r.Split('\n').Single(l => l.Contains(Fid(line)));
            Assert.Contains($"placed by {DialogueWorld.PatchName}", row);
            Assert.Contains("FOLDED", row);
        }
    }

    /// <summary>The response says the order is a projection and where the file was placed, so a merged order read
    /// before the enable can never be reported as the live one.</summary>
    [Fact]
    public void AFoldedResponseSaysTheFileIsNotActiveAndWasPlacedLast()
    {
        var r = Folded(W.Topic, $"\"{DialogueWorld.PatchName}\"");

        Assert.Contains("projection", r);
        Assert.Contains($"'{DialogueWorld.PatchName}' is NOT in the load order", r);
        Assert.Contains("OUTSIDE the epoch fingerprint", r);
    }

    /// <summary>A topic only the folded file defines has no winner in the active order — without the fold the read
    /// is a per-item miss — and with it the fold is the topic's whole merge, served rather than refused.</summary>
    [Fact]
    public void ATopicOnlyTheFoldedFileDefinesIsServedFromTheFold()
    {
        Assert.Contains("error=", Folded(W.PatchOwnTopic));

        var r = Folded(W.PatchOwnTopic, $"\"{DialogueWorld.PatchName}\"");

        Assert.DoesNotContain("error=", r);
        Assert.Contains($"2 lines, from a single plugin ({DialogueWorld.PatchName})", r);
        Assert.Contains("the only plugin listing lines here", r);
    }

    /// <summary>The json render carries the same two facts in band: which file was folded, and which rows it
    /// placed — a consumer reading rows cannot miss the projection.</summary>
    [Fact]
    public void TheJsonRenderCarriesTheFoldedPluginAndMarksItsRows()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Topic) },
                                     project: new RecordsTools.RecordsProject { form = "info_order" },
                                     source: Je($"\"{DialogueWorld.PatchName}\""), format: "json");

        var row = JsonDocument.Parse(r).RootElement.GetProperty("rows")[0];
        Assert.Equal(DialogueWorld.PatchName, row.GetProperty("folded_plugin").GetString());
        Assert.True(row.GetProperty("folded_contributed").GetBoolean());
        var placed = row.GetProperty("order").EnumerateArray()
                        .Where(e => e.TryGetProperty("folded", out var f) && f.GetBoolean())
                        .Select(e => e.GetProperty("info").GetString()).ToList();
        Assert.Equal(new[] { Fid(W.Info[5]), Fid(W.Info[3]) }.OrderBy(x => x), placed.OrderBy(x => x));
    }

    /// <summary>Two files have no order between them until MO2 sorts them, so there is no one merge to project:
    /// the fold takes one file per call and says so.</summary>
    [Fact]
    public void TwoOffOrderFilesAreRefused()
    {
        var r = Folded(W.Topic, $"[\"{DialogueWorld.PatchName}\", \"Other.esp\"]");

        Assert.Contains("error:", r);
        Assert.Contains("folds ONE off-order file", r);
    }

    /// <summary>A plugin that IS active is already in the merge: folding it again would project a second copy of a
    /// plugin the order carries once, so it is refused with what to do instead.</summary>
    [Fact]
    public void AnActivePluginIsRefusedWithWhatToDoInstead()
    {
        var r = Folded(W.Topic, $"\"{DialogueWorld.LastName}\"");

        Assert.Contains("error:", r);
        Assert.Contains("ACTIVE in the load order", r);
        Assert.Contains("Drop source=", r);
    }

    /// <summary>The overlay pole still has no seat on this form, and the refusal now names the one pole that does.</summary>
    [Fact]
    public void AnOverlayPoleIsRefusedNamingTheOffOrderFoldInstead()
    {
        var r = Folded(W.Topic, "{\"overlay\": \"skypatcher\", \"state\": \"post\"}");

        Assert.Contains("error:", r);
        Assert.Contains("OFF-ORDER plugin filename", r);
    }
}

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
        Assert.Contains($"the folded file is '{DialogueWorld.PatchName}'", r);
        Assert.Contains("NOT active", r);
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

    /// <summary>MO2 does not put a MASTER on the end of the order: an .esm lands after the last master, ahead of
    /// every regular plugin below it. This order lists its .esl AFTER three regular plugins, so the position has to
    /// be read off the index — a rule that assumed the master block is a prefix would put the fold somewhere else
    /// — and the response names the plugin it landed after.</summary>
    [Fact]
    public void AMasterFoldLandsAfterTheLastMasterInTheOrder()
    {
        var r = Folded(W.MasterBlockTopic, $"\"{DialogueWorld.PatchEsmName}\"");

        // Base master [0,1,2]; HcDvLast re-lists 0 -> [1,2,0]; the fold, placed after the .esl and so above the
        // tail plugin, re-lists 1 -> [2,0,1]; the tail plugin then re-lists 0 -> [2,1,0]. Folded at the END it
        // would have been the last re-list and line 1 would sit at the bottom.
        Assert.Contains($"#1  {Fid(W.MasterBlockInfo[2])}", r);
        Assert.Contains($"#2  {Fid(W.MasterBlockInfo[1])}", r);
        Assert.Contains($"#3  {Fid(W.MasterBlockInfo[0])}", r);
        Assert.Contains("END OF THE MASTER BLOCK", r);
        Assert.Contains($"immediately after '{DialogueWorld.CcName}'", r);
        Assert.Contains("it is a .esm", r);
    }

    /// <summary>A master folded in AHEAD of the topic's defining plugin does not become the baseline the MOVED
    /// annotations are measured against: that is the definer's own list, and taking the projection's would call
    /// the definer's lines late additions and half the topic moved.</summary>
    [Fact]
    public void AMasterFoldDoesNotBecomeTheMoveBaseline()
    {
        // This topic's definer sits BELOW the last master, so the fold contributes before it does.
        var r = Folded(W.TailTopic, $"\"{DialogueWorld.PatchEsmName}\"");

        // The definer's list is [0,1,2] and it re-places every line after the fold, so nothing moved and nothing
        // was added late. Measured against the FOLD's one-line list instead, two of the three lines have no
        // origin at all and render as a later plugin's additions.
        Assert.DoesNotContain("added by a later plugin", r);
        Assert.DoesNotContain("MOVED from", r);
        Assert.Contains($"#1  {Fid(W.TailInfo[0])}", r);
        Assert.Contains($"#3  {Fid(W.TailInfo[2])}", r);
    }

    /// <summary>And a plain .esp says the other thing, because that is where MO2 puts it — naming the plugin it
    /// landed after, so "last" is a position rather than a claim.</summary>
    [Fact]
    public void ARegularFoldSaysItWasPlacedLast()
    {
        var r = Folded(W.Topic, $"\"{DialogueWorld.PatchName}\"");

        Assert.Contains("folded in LAST, after", r);
        Assert.Contains("where MO2 puts a newly enabled regular plugin", r);
        Assert.DoesNotContain("MASTER BLOCK", r);
    }

    /// <summary>The {file, mod} arm over a copy whose FILENAME is active: the rows carry a label of their own, the
    /// envelope names that same label, and the banner does not call an active filename absent.</summary>
    [Fact]
    public void AShadowedCopyIsLabelledApartAndTheBannerSaysTheFilenameIsActive()
    {
        var src = $"{{\"file\": \"{DialogueWorld.MidName}\", \"mod\": \"{DialogueWorld.ShadowModFolder}\"}}";
        var r = Folded(W.Topic, src);

        var row = r.Split('\n').Single(l => l.Contains(Fid(W.Info[2])));
        Assert.Contains($"placed by {DialogueWorld.MidName} [off-order copy]", row);
        Assert.Contains($"The FILENAME '{DialogueWorld.MidName}' IS in the order", r);
        Assert.DoesNotContain($"'{DialogueWorld.MidName}' is NOT in the load order", r);
        // It takes that plugin's OWN slot — enabling the mod folder swaps the bytes at a position the order
        // already has — so the winner below it still re-lists after it: the fold's line sits at #7, not the end.
        Assert.Contains($"OWN slot in the load order", r);
        Assert.Contains($"#7  {Fid(W.Info[2])}", r);
        Assert.Contains($"#8  {Fid(W.MovedLine)}", r);

        var j = RecordsTools.Records(Svc, formids: new[] { Fid(W.Topic) },
                                     project: new RecordsTools.RecordsProject { form = "info_order" },
                                     source: Je(src), format: "json");
        var doc = JsonDocument.Parse(j).RootElement;
        Assert.Equal(doc.GetProperty("folded").GetString(),
                     doc.GetProperty("rows")[0].GetProperty("folded_plugin").GetString());
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

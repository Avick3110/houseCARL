using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The dialogue family over an off-order plugin (#615): <c>source=</c> on <c>check(findings=["dialogue"])</c> names
/// one plugin the active order does not carry, and every seed is validated against the order's winners plus that
/// file. The measurement that matters is the comparison arm — the same patch written INTO the order says the same
/// thing — so the fold is checked against the real answer rather than against itself.
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckDialogueFoldTests
{
    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";
    static JsonElement Je(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>The patch's own topic carries no Quest, which the validator warns on. Folded, the check says
    /// exactly what it says once the plugin is enabled — measured against a world that enables it.</summary>
    [Fact]
    public void AFoldedSeedProducesTheFindingTheEnabledPluginProduces()
    {
        const string unowned = "DialogTopic.Quest is unset";

        using var offOrder = new DialogueWorld();
        var folded = CheckTools.CheckTool(offOrder.Svc, findings: new[] { "dialogue" },
                                          seeds: new[] { Fid(offOrder.PatchOwnTopic) },
                                          source: Je($"\"{DialogueWorld.PatchName}\""));

        using var enabled = new DialogueWorld(patchActive: true);
        var live = CheckTools.CheckTool(enabled.Svc, findings: new[] { "dialogue" },
                                        seeds: new[] { Fid(enabled.PatchOwnTopic) });

        Assert.Contains(unowned, live);
        Assert.Contains(unowned, folded);
    }

    /// <summary>Without the fold the same seed is a record the order does not have, and the family says so — the
    /// gap this lane closes, kept beside the arm that closes it.</summary>
    [Fact]
    public void TheSameSeedWithoutTheFoldIsStillNotInTheOrder()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.PatchOwnTopic) });

        Assert.Contains("not in the active load order", r);
    }

    /// <summary>The section states the fold once, at the top, so no finding under it can be read as the live
    /// answer — including the one thing the fold does not move, the VFS the file's assets resolve through.</summary>
    [Fact]
    public void AFoldedSectionSaysSoAtTheTop()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.PatchOwnTopic) },
                                     source: Je($"\"{DialogueWorld.PatchName}\""));

        Assert.Contains($"folded: '{DialogueWorld.PatchName}' is NOT active", r);
        Assert.Contains("enable it and re-run", r);
        Assert.Contains("VFS", r);
    }

    /// <summary>A seed the order DOES carry is validated against the projection too: the folded file's re-list is
    /// in the topic's merged order, which the folded read shows and the plain read does not.</summary>
    [Fact]
    public void AnActiveSeedIsValidatedAgainstTheProjectedOrder()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.Topic) },
                                     source: Je($"\"{DialogueWorld.PatchName}\""));

        Assert.Contains($"folded: '{DialogueWorld.PatchName}' is NOT active", r);
        Assert.DoesNotContain("not in the active load order", r);
    }

    /// <summary>Only the dialogue family has this arm, and the refusal says which family takes it and what the
    /// swept families use instead.</summary>
    [Fact]
    public void ASweptFamilyOverAnOffOrderSourceIsRefusedNamingTheOneFamilyThatTakesIt()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "errors" },
                                     source: Je($"\"{DialogueWorld.PatchName}\""));

        Assert.Contains("error:", r);
        Assert.Contains("DIALOGUE family", r);
        Assert.Contains("plugins=", r);
    }

    /// <summary>And a mixed call is refused the same way rather than folding a file into one family while the
    /// others sweep the order beside it under one response.</summary>
    [Fact]
    public void AMixedCallWithAFoldIsRefused()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue", "errors" },
                                     seeds: new[] { Fid(w.Topic) },
                                     source: Je($"\"{DialogueWorld.PatchName}\""));

        Assert.Contains("error:", r);
        Assert.Contains("only family with that arm", r);
    }

    /// <summary>An ACTIVE plugin is what the family already reads, so folding it is refused with what to do.</summary>
    [Fact]
    public void AnActivePluginIsRefused()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.Topic) },
                                     source: Je($"\"{DialogueWorld.LastName}\""));

        Assert.Contains("error:", r);
        Assert.Contains("ACTIVE in the load order", r);
        Assert.Contains("Drop source=", r);
    }

    /// <summary>A folded copy whose FILENAME is also active must not soften its own findings: the .seq lint
    /// compares the winning plugin's name against the defining one, so a display label there would make a file
    /// read as an override of itself and turn a dormant quest into an ambiguity.</summary>
    [Fact]
    public void AShadowedFoldMakesTheSameSeqFindingAsAPlainOne()
    {
        using var w = new DialogueWorld();
        var shadow = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                          seeds: new[] { Fid(w.ShadowSeqQuest) },
                                          source: Je($"{{\"file\": \"{DialogueWorld.MidName}\", \"mod\": \"{DialogueWorld.ShadowModFolder}\"}}"),
                                          max_chars: 40000);
        var plain = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                         seeds: new[] { Fid(w.PatchSeqQuest) },
                                         source: Je($"\"{DialogueWorld.PatchName}\""), max_chars: 40000);

        Assert.Contains("stays DORMANT", plain);
        Assert.Contains("stays DORMANT", shadow);
        Assert.DoesNotContain("WINNING override", shadow);
    }

    /// <summary>A folded call that spills its findings to a file still carries the frame: the manifest render is
    /// the only render it gets, and the artifact's own notes say the rows are a projection.</summary>
    [Fact]
    public void AFoldedToFileCallCarriesTheFrameInTheResponseAndTheManifest()
    {
        using var w = new DialogueWorld();
        var path = Path.Combine(w.Root, "fold-findings.jsonl");

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.PatchOwnTopic) },
                                     source: Je($"\"{DialogueWorld.PatchName}\""), to_file: path);

        Assert.Contains($"folded: '{DialogueWorld.PatchName}' is NOT active", r);
        var manifest = JsonDocument.Parse(File.ReadLines(path).First()).RootElement;
        Assert.Contains(manifest.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!),
                        n => n.Contains("PROJECTION") && n.Contains(DialogueWorld.PatchName));
    }

    /// <summary>A folded MASTER does not win a record a regular plugin overrides: it lands in the master block,
    /// so the active plugin below it is still what the game reads, and the check says so.</summary>
    [Fact]
    public void AMasterFoldDoesNotWinWhatARegularPluginOverrides()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" },
                                     seeds: new[] { Fid(w.MasterBlockTopic) },
                                     source: Je($"\"{DialogueWorld.PatchEsmName}\""), max_chars: 40000);

        // The fold lands after the last master in the order; the regular plugin below that still wins the record.
        Assert.Contains($"winner {DialogueWorld.TailName}", r);
        Assert.DoesNotContain($"winner {DialogueWorld.PatchEsmName}", r);
        Assert.Contains("END OF THE MASTER BLOCK", r);
    }

    /// <summary>The family's own scope sentence now names the lane, so a caller reading the section learns it
    /// exists rather than being told there is none.</summary>
    [Fact]
    public void TheScopeNoteNamesTheOffOrderLane()
    {
        using var w = new DialogueWorld();

        var r = CheckTools.CheckTool(w.Svc, findings: new[] { "dialogue" }, seeds: new[] { Fid(w.Topic) });

        Assert.Contains("off-order lane is source=", r);
    }
}

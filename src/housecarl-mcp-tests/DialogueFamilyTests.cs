using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The dialogue family's facts <c>DialogueInfoOrderProbe.cs</c> and <c>DialogueValidateGuardProbe.cs</c>
/// asserted through the deleted <c>DialogueWire.Render</c> (#486's whole-report renderer), re-asserted here
/// against the LIVE surfaces that render them today: <c>housecarl_records project=info_order</c> for D1-D3,
/// and <c>housecarl_check findings=["dialogue"]</c> for D4/V1. Numbered D1-D5/V1 per
/// <c>dev/session-handoffs/render-halves-scratch/PHASE-1-record.md</c> §5's phase-4 fact list (D5, the
/// <c>RenderOrderOnly</c> helper's honesty gates over synthesized views, is not re-tested here — it was fixed
/// IN PLACE in <c>DialogueInfoOrderProbe.cs</c> by rewriting the helper onto the surviving
/// <c>DialogueWire.AppendInfoOrderView</c>, so the ~8 arms built on it (UNREAD-RENDER, RENDER-BIG-TOPIC,
/// RENDER-CLAIM-GATES, RENDER-HEAD-SPLIT) kept running unmoved in the probe itself).
///
/// <para>D1/D2 and V1 are driven on the shared <see cref="DialogueWorld"/>. The three lock facts (D3, D4a,
/// D4b) each construct their OWN world via <c>new()</c> — never the shared one, per <see cref="DialogueWorld"/>'s
/// own doc and the <see cref="HeldOpen"/> harness's contract (a held file is unreadable to anything else in
/// the process).</para>
/// </summary>
[Collection("dialogue")]
[Trait("tier", "integration")]
public sealed class DialogueFamilyTests
{
    readonly DialogueWorld W;
    public DialogueFamilyTests(DialogueFixture f) => W = f.W;

    LoadOrderService Svc => W.Svc;

    static string Fid(Mutagen.Bethesda.Plugins.FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    string InfoOrder(Mutagen.Bethesda.Plugins.FormKey topic) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(topic) },
                             project: new RecordsTools.RecordsProject { form = "info_order" });

    static SweepFamilySelection DialogueSel()
    {
        Assert.True(SweepFamilySelection.TryParse(new[] { "dialogue" }, out var sel, out var err), err);
        return sel!;
    }

    string CheckDialogue(LoadOrderService svc, params Mutagen.Bethesda.Plugins.FormKey[] seeds) =>
        Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: svc.CheckDialogue(seeds.Select(Fid).ToArray(), 1000)), 20000);

    // ---- fact D1 --------------------------------------------------------------------------------------
    // The shipped INFO-order render states the merge model (effective INFO order, per-row MOVED from #N) and
    // never claims lines are "dropped in game".

    [Fact]
    public void FactD1_TheShippedRenderStatesTheMergeModel()
    {
        var r = InfoOrder(W.Topic);

        Served(r, "effective INFO order", "merged across 3 plugins that touch this topic",
                  "plays the FIRST line whose conditions pass");
        // The whole composed row, anchored to the record that moved: "MOVED from #1" on its own is a fragment any
        // row could carry, and this fixture has eight of them.
        Assert.Contains($"#8  {Fid(W.MovedLine)}  MOVED from #1  placed by {DialogueWorld.LastName}", r);
        Assert.DoesNotContain("dropped in game", r);
    }

    // ---- fact D2 --------------------------------------------------------------------------------------
    // The retracted PNAM-zero caveat stays absent.

    [Fact]
    public void FactD2_TheRetractedCaveatStaysAbsent()
    {
        var r = InfoOrder(W.Topic);

        Assert.DoesNotContain("I am first", r);
        Assert.DoesNotContain("placed LAST where the game places it FIRST", r);
    }

    // ---- fact D3 --------------------------------------------------------------------------------------
    // UNREAD-WIRED: a locked contributor yields an INCOMPLETE render naming the unread plugin — the same fix,
    // end to end through the production path, over a world this test constructs itself.

    [Fact]
    public void FactD3_UnreadWired()
    {
        using var w = new DialogueWorld();
        w.Svc.Stats();   // force the index build while every plugin is still readable

        using var hold = HeldOpen.Hold(w.MidPath);
        var r = RecordsTools.Records(w.Svc, formids: new[] { Fid(w.Topic) },
                                     project: new RecordsTools.RecordsProject { form = "info_order" });

        Served(r, "INCOMPLETE", "read from 2 of 3 plugin(s) that touch this topic");
        Assert.Contains($"1 plugin(s) that TOUCH this topic could not be read ({DialogueWorld.MidName})", r);
    }

    // ---- fact D4 (a) ------------------------------------------------------------------------------------
    // DEFINER-LOCK-LOUD: an unreadable definer is loud — a plugin can only override a record by declaring the
    // defining plugin as a master, so opening the override REQUIRES the definer, and the fetch throws before
    // any order code runs. The merged surface says "the check did not finish — {CheckError}", not the retired
    // "could NOT complete" wording.

    [Fact]
    public void FactD4a_DefinerLockIsLoud()
    {
        using var w = new DialogueWorld();
        w.Svc.Stats();

        using var hold = HeldOpen.Hold(w.MasterPath);
        var result = w.Svc.CheckDialogue(new[] { Fid(w.Topic) }, 1000);
        var text = Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: result), 20000);

        Assert.NotNull(result.Error);
        // Read PAST the seed. The sweep composes the refusal as "{seed}: {refusal}." and the seed is
        // "<id>:HcDvMaster.esp" because the topic is DEFINED in the master — so a whole-response
        // Contains(MasterName) is satisfied by the seed's own echo whichever plugin actually failed, and holding
        // LastPath instead left it green (pre-green review 1b, finding 2). The refusal itself has to name the
        // locked plugin, and only the locked one.
        var refusal = AfterSeed(text, Fid(w.Topic));
        Assert.Contains("the check did not finish", refusal);
        Assert.Contains("IOException", refusal);
        Assert.Contains(DialogueWorld.MasterName, refusal);
        Assert.DoesNotContain(DialogueWorld.LastName, refusal);
        Assert.DoesNotContain(DialogueWorld.MidName, refusal);
    }

    // ---- fact D4 (b) ------------------------------------------------------------------------------------
    // WINNER-LOCK-LOUD: the TOTAL-drop case is already loud, by a different mechanism — the winner body is
    // fetched through GetRecord, which throws rather than swallowing.

    [Fact]
    public void FactD4b_WinnerLockIsLoud()
    {
        using var w = new DialogueWorld();
        w.Svc.Stats();

        using var hold = HeldOpen.Hold(w.LastPath);
        var result = w.Svc.CheckDialogue(new[] { Fid(w.Topic) }, 1000);
        var text = Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: result), 20000);

        Assert.NotNull(result.Error);
        // Read past the seed for the same reason as D4a, though here the seed names a DIFFERENT plugin from the
        // locked one — the same shape gets the same treatment so the two facts stay comparable.
        var refusal = AfterSeed(text, Fid(w.Topic));
        Assert.Contains("the check did not finish", refusal);
        Assert.Contains("IOException", refusal);
        Assert.Contains(DialogueWorld.LastName, refusal);
        Assert.DoesNotContain(DialogueWorld.MidName, refusal);
    }

    // ---- fact V1 --------------------------------------------------------------------------------------
    // Each seed kind's "CK-parity: OK" prose names EVERY subrecord its Missing*Defaults check covers, with
    // the signature set DERIVED from the check rather than hand-listed.

    static string[] GapSigs(IReadOnlyList<CkParityGap> gaps) =>
        gaps.SelectMany(g => Regex.Matches(g.Subrecord, @"\b[A-Z]{4}\b").Select(m => m.Value)).Distinct().ToArray();

    [Fact]
    public void FactV1_ParityOkProseNamesEveryCoveredSubrecord()
    {
        // The authoritative signature sets, derived from a BARE record's own gap list — never hand-listed.
        var pinView = GapSigs(DialogueCkParity.MissingViewDefaults(
            new DialogView(Mutagen.Bethesda.Plugins.FormKey.Factory("000900:HcDvPin.esm"), SkyrimRelease.SkyrimSE)));
        var pinBranch = GapSigs(DialogueCkParity.MissingBranchDefaults(
            new DialogBranch(Mutagen.Bethesda.Plugins.FormKey.Factory("000901:HcDvPin.esm"), SkyrimRelease.SkyrimSE)));
        var pinQuest2 = new Quest(Mutagen.Bethesda.Plugins.FormKey.Factory("000902:HcDvPin.esm"), SkyrimRelease.SkyrimSE);
        pinQuest2.Objectives.Add(new QuestObjective { Index = 1 });
        var pinQuest = GapSigs(DialogueCkParity.MissingQuestDefaults(pinQuest2));

        Assert.NotEmpty(pinView);
        Assert.NotEmpty(pinBranch);
        Assert.NotEmpty(pinQuest);

        var r = CheckDialogue(Svc, W.ViewOk, W.BranchOk, W.QuestOk);

        var byViewLine = LineAfter(r, "dialogue view (DLVW)");
        Assert.Contains("CK-parity: OK", byViewLine);
        foreach (var sig in pinView) Assert.Contains(sig, byViewLine);

        var byBranchLine = LineAfter(r, "dialogue branch (DLBR)");
        Assert.Contains("CK-parity: OK", byBranchLine);
        foreach (var sig in pinBranch) Assert.Contains(sig, byBranchLine);

        var byQuestLine = LineAfter(r, "quest (QUST)");
        Assert.Contains("quest CK-parity: OK", byQuestLine);
        foreach (var sig in pinQuest) Assert.Contains(sig, byQuestLine);
    }

    /// <summary>Everything AFTER the seed prefix the sweep echoes — the composed refusal itself. The sweep writes
    /// "{seed}: {refusal}.", and the seed is "&lt;id&gt;:&lt;definer&gt;", so a plugin name found anywhere in the
    /// whole response may be the seed's own echo rather than the failure's subject.</summary>
    static string AfterSeed(string text, string seed)
    {
        int i = text.IndexOf(seed + ":", StringComparison.Ordinal);
        Assert.True(i >= 0, $"no seed '{seed}' in the response: {text}");
        return text[(i + seed.Length + 1)..];
    }

    /// <summary>The block of text starting at the line containing <paramref name="marker"/>, running to the
    /// next blank line — enough to read one seed's own CK-parity report without picking up a sibling's.</summary>
    static string LineAfter(string response, string marker)
    {
        var lines = response.Split('\n');
        int start = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no line containing '{marker}': {response}");
        int end = Array.FindIndex(lines, start + 1, l => l.Trim().Length == 0);
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + response.Split('\n').First());
        foreach (var s in mustName) Assert.Contains(s, response);
    }
}

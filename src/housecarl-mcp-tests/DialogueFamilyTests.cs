using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The dialogue family's facts, driven against the surfaces that render them: <c>housecarl_records
/// project=info_order</c> for D1-D3, and <c>housecarl_check findings=["dialogue"]</c> for D4/V1.
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
    // The PNAM-zero caveat stays absent.

    [Fact]
    public void FactD2_TheRetractedCaveatStaysAbsent()
    {
        var r = InfoOrder(W.Topic);

        Assert.DoesNotContain("I am first", r);
        Assert.DoesNotContain("placed LAST where the game places it FIRST", r);
    }

    // ---- fact D3 --------------------------------------------------------------------------------------
    // UNREAD-WIRED: a locked contributor yields an INCOMPLETE render naming the unread plugin, over a world
    // this test constructs itself.

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
    // any order code runs.

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
        // "<id>:HcDvMaster.esp" because the topic is DEFINED in the master, so a whole-response
        // Contains(MasterName) is satisfied by the seed's own echo whichever plugin actually failed. The
        // refusal itself has to name the locked plugin, and only the locked one.
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
        // locked one.
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

        var byViewLine = SeedBlock(r, "dialogue view (DLVW)");
        Assert.Contains("CK-parity: OK", byViewLine);
        foreach (var sig in pinView) Assert.Contains(sig, byViewLine);

        var byBranchLine = SeedBlock(r, "dialogue branch (DLBR)");
        Assert.Contains("CK-parity: OK", byBranchLine);
        foreach (var sig in pinBranch) Assert.Contains(sig, byBranchLine);

        var byQuestLine = SeedBlock(r, "quest (QUST)");
        Assert.Contains("quest CK-parity: OK", byQuestLine);
        foreach (var sig in pinQuest) Assert.Contains(sig, byQuestLine);
    }

    // ---- fact V2 --------------------------------------------------------------------------------------
    // The family stamps the record build every seed was validated against, and DECLARES the bound rather than
    // omitting the stamp: both transports name the three verdict classes the record fingerprint does not describe.

    [Fact]
    public void FactV2_TheStampDeclaresItsBound()
    {
        var result = Svc.CheckDialogue(new[] { Fid(W.Topic) }, 1000);
        Assert.Null(result.Error);
        Assert.False(string.IsNullOrEmpty(result.Epoch));

        var text = Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: result), 20000);
        Served(text, $"epoch={result.Epoch}", "does not cover");
        foreach (var cls in DialogueSweepRender.EpochUncovered) Assert.Contains(cls, text);

        var fam = JsonDocument.Parse(JsonWire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: result), 20000))
                              .RootElement.GetProperty("families")
                              .GetProperty(SweepFamilySelection.Token(SweepFamily.Dialogue));
        Assert.Equal(result.Epoch, fam.GetProperty("epoch").GetString());
        // The stamp is a record-build claim only, so the coverage flag is false and the uncovered set says over what.
        Assert.False(fam.GetProperty("epoch_covers_all_inputs").GetBoolean());
        Assert.Equal(DialogueSweepRender.EpochUncovered,
                     fam.GetProperty("epoch_uncovered").EnumerateArray().Select(e => e.GetString()).ToArray());

        // A refusal reached AFTER the build was read carries the bare stamp, as the sibling families' post-capture
        // refusals do (EpochCheckSweepTests.FactE5_6): there is a build to name, and nothing was covered by it.
        var refused = Svc.CheckDialogue(new[] { "not-a-formid" }, 1000);
        Assert.NotNull(refused.Error);
        Assert.Equal(result.Epoch, refused.Epoch);
        Assert.Contains($"epoch={refused.Epoch}",
                        Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: refused), 20000));
        var refusedJson = JsonWire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: refused), 20000);
        Assert.Equal(refused.Epoch, JsonDocument.Parse(refusedJson).RootElement.GetProperty("epoch").GetString());
        Assert.DoesNotContain("epoch_covers_all_inputs", refusedJson);

        // …and a refusal decided on the ARGUMENTS alone names no build, which is the split the siblings keep: it is
        // answered without reading one, so there is nothing honest to stamp.
        var unseeded = Svc.CheckDialogue(Array.Empty<string>(), 1000);
        Assert.NotNull(unseeded.Error);
        Assert.Null(unseeded.Epoch);
        Assert.DoesNotContain("epoch=", Wire.RenderCheck(new CheckSweep(DialogueSel(), Dialogue: unseeded), 20000));
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

    /// <summary>One seed's own block: the head line containing <paramref name="marker"/> and the indented findings
    /// under it, stopping at the NEXT seed's head — or at the family's accounting or boundary, whichever comes first.
    ///
    /// <para>The terminator must be the next head, not the next blank line: the seed heads are contiguous
    /// (<c>ReadSentences.DialogueSeedHead</c> ends in one newline and the next head follows it directly), so the
    /// first blank line comes after the LAST seed and every block would span every seed below it — a seed's
    /// verdict could then be satisfied by a sibling's.</para>
    ///
    /// <para>The start is anchored to a HEAD line rather than the first line containing the marker: a kind label
    /// also appears in a seed's scope sentence, so a marker search would resolve the quest block to the DLVW
    /// seed.</para></summary>
    static string SeedBlock(string response, string marker)
    {
        var lines = response.Split('\n');
        int start = Array.FindIndex(lines,
            l => l.StartsWith("seed ", StringComparison.Ordinal) && l.Contains(marker, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no seed head containing '{marker}': {response}");
        int end = Array.FindIndex(lines, start + 1,
            l => l.StartsWith("seed ", StringComparison.Ordinal)
                 || l.StartsWith("[accounting:", StringComparison.Ordinal)
                 || l.StartsWith("boundary (", StringComparison.Ordinal));
        return string.Join("\n", lines[start..(end < 0 ? lines.Length : end)]);
    }

    static void Served(string response, params string[] mustName)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + response.Split('\n').First());
        foreach (var s in mustName) Assert.Contains(s, response);
    }
}

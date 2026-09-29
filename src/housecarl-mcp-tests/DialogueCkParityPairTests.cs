using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <see cref="DialogueCkParity"/>'s fill and gap pairs on hand-built records, moved from the retired
/// <c>dialogue-ckparity-guard</c> probe. Each <c>Missing…Defaults</c> (the validator's check) and
/// <c>Apply…Defaults</c> (the create fill) read one presence predicate, so a bare record reports exactly its gaps
/// and none once filled. The QUST fills (the ANAM derive, objective and alias FNAM, reference-alias VTCK) and each
/// <c>*-WINS</c> arm are one test apiece, with the arm name in it.
/// </summary>
[Trait("tier", "unit")]
public sealed class DialogueCkParityPairTests
{
    static FormKey Fk(uint id) => new(new ModKey("HcCkpGapProbe", ModType.Master), id);

    static bool Names(CkParityGap g, string sig) => g.Subrecord.Contains(sig, StringComparison.Ordinal);

    // CONST-SHAPE: the DLVW defaults are 00 / 00000000, as a CK-authored DialogView carries them.
    [Fact]
    public void ConstShape_ViewDefaultsAreTheCkBytes()
    {
        Assert.Equal("00", DialogueCkParity.ViewDnamHex);
        Assert.Equal("00000000", DialogueCkParity.ViewEnamHex);
    }

    // CONST-SHAPE S2: DIAL Priority seed is 50, DLBR Category default is Player.
    [Fact]
    public void ConstShape_S2SeedsArePriority50AndCategoryPlayer()
    {
        Assert.Equal(50f, DialogueCkParity.TopicPrioritySeed);
        Assert.Equal(DialogBranch.CategoryType.Player, DialogueCkParity.BranchCategoryDefault);
    }

    // INFO-GAP-PROBE: a bare INFO -> exactly the CNAM + ENAM gaps; none after ApplyInfoDefaults.
    [Fact]
    public void InfoGapProbe_BareInfoReportsCnamAndEnam_NoneAfterFill()
    {
        var info = new DialogResponses(Fk(0x800), SkyrimRelease.SkyrimSE);

        var before = DialogueCkParity.MissingInfoDefaults(info);
        Assert.Equal(2, before.Count);
        Assert.Contains(before, g => Names(g, "CNAM"));
        Assert.Contains(before, g => Names(g, "ENAM"));

        DialogueCkParity.ApplyInfoDefaults(info);
        Assert.Empty(DialogueCkParity.MissingInfoDefaults(info));
    }

    // DLVW-GAP-PROBE: a bare DialogView -> exactly the DNAM + ENAM gaps; none after ApplyViewDefaults.
    [Fact]
    public void DlvwGapProbe_BareViewReportsDnamAndEnam_NoneAfterFill()
    {
        var view = new DialogView(Fk(0x801), SkyrimRelease.SkyrimSE);

        var before = DialogueCkParity.MissingViewDefaults(view);
        Assert.Equal(2, before.Count);
        Assert.Contains(before, g => Names(g, "DNAM"));
        Assert.Contains(before, g => Names(g, "ENAM"));

        DialogueCkParity.ApplyViewDefaults(view);
        Assert.Empty(DialogueCkParity.MissingViewDefaults(view));
    }

    // DLVW-GAP-REMEDY: every ops= remedy a DLVW gap prints carries each member housecarl_apply's ops= element
    // requires (the members its published shape writes without a trailing '?'), and names the view's own formid.
    [Fact]
    public void DlvwGapRemedy_EachRemedyCarriesEveryRequiredOpMember()
    {
        var view = new DialogView(Fk(0x801), SkyrimRelease.SkyrimSE);
        var opsDesc = typeof(ApplyTools).GetMethod("Apply")!.GetParameters()
            .First(p => p.Name == "ops").GetCustomAttribute<DescriptionAttribute>()!.Description;
        var required = Regex.Match(opsDesc, @"\{([^}]*)\}").Groups[1].Value.Split(',').Select(t => t.Trim())
            .Where(t => t.Length > 0 && !t.EndsWith("?", StringComparison.Ordinal)).ToList();

        var remedies = DialogueCkParity.MissingViewDefaults(view).Where(g => g.Detail.Contains("ops=[{", StringComparison.Ordinal)).ToList();

        Assert.Contains("formid", required);
        Assert.Equal(2, remedies.Count);
        foreach (var g in remedies)
        {
            foreach (var m in required) Assert.Contains(m + ":", g.Detail);
            Assert.Contains(view.FormKey.ToString(), g.Detail);
        }
    }

    // DLBR-GAP-PROBE: a bare DialogBranch -> exactly the TNAM + DNAM gaps; DNAM alone after ApplyBranchDefaults
    // (no fill sets Flags).
    [Fact]
    public void DlbrGapProbe_BareBranchReportsTnamAndDnam_DnamAloneAfterFill()
    {
        var br = new DialogBranch(Fk(0x802), SkyrimRelease.SkyrimSE);

        var before = DialogueCkParity.MissingBranchDefaults(br);
        Assert.Equal(2, before.Count);
        Assert.Contains(before, g => Names(g, "TNAM"));
        Assert.Contains(before, g => Names(g, "DNAM"));

        DialogueCkParity.ApplyBranchDefaults(br);
        var after = Assert.Single(DialogueCkParity.MissingBranchDefaults(br));
        Assert.Contains("DNAM", after.Subrecord);
    }

    // DLBR-GAP-PROBE: BranchFlagsRefusal on a built branch with no Flags refuses naming the editorid and both values
    // (TopLevel, 0).
    [Fact]
    public void DlbrGapProbe_FlaglessBuiltBranchIsRefusedNamingBothValues()
    {
        var br = new DialogBranch(Fk(0x802), SkyrimRelease.SkyrimSE);

        var refusal = DialogueCkParity.BranchFlagsRefusal(br, "HcCkpGapProbeBr");
        Assert.NotNull(refusal);
        Assert.Contains("HcCkpGapProbeBr", refusal);
        Assert.Contains("TopLevel", refusal);
        Assert.Contains(" 0 ", refusal);
    }

    // DLBR-GAP-PROBE: once Flags is set, no gap and no refusal; an explicit 0 counts as set.
    [Theory]
    [InlineData(DialogBranch.Flag.TopLevel)]
    [InlineData((DialogBranch.Flag)0)]
    public void DlbrGapProbe_BranchWithFlagsIsCleanAndNotRefused(DialogBranch.Flag flags)
    {
        var br = new DialogBranch(Fk(0x802), SkyrimRelease.SkyrimSE);
        DialogueCkParity.ApplyBranchDefaults(br);
        br.Flags = flags;

        Assert.Empty(DialogueCkParity.MissingBranchDefaults(br));
        Assert.Null(DialogueCkParity.BranchFlagsRefusal(br, "HcCkpGapProbeBr"));
    }

    // QUST-GAP-PROBE: a bare quest with a Flags-less objective and a bare alias -> exactly ANAM + objective FNAM +
    // alias FNAM + alias VTCK; none after ApplyQuestDefaults.
    [Fact]
    public void QustGapProbe_BareQuestReportsFourGaps_NoneAfterFill()
    {
        var q = new Quest(Fk(0x803), SkyrimRelease.SkyrimSE);
        q.Objectives.Add(new QuestObjective { Index = 10 });
        q.Aliases.Add(new QuestAlias { ID = 0 });

        var before = DialogueCkParity.MissingQuestDefaults(q);
        Assert.Equal(4, before.Count);
        Assert.Contains(before, g => Names(g, "ANAM"));
        Assert.Contains(before, g => Names(g, "FNAM") && Names(g, "Objectives[0]"));
        Assert.Contains(before, g => Names(g, "FNAM") && Names(g, "Aliases[0]"));
        Assert.Contains(before, g => Names(g, "VTCK") && Names(g, "Aliases[0]"));

        DialogueCkParity.ApplyQuestDefaults(q);
        Assert.Empty(DialogueCkParity.MissingQuestDefaults(q));
    }

    // QUST-ANAM-DERIVE: alias IDs {0,3} -> NextAliasID=4 (max+1, not count), reported.
    [Fact]
    public void QustAnamDerive_SparseAliasIdsGiveMaxPlusOne_Reported()
    {
        var q = new Quest(Fk(0x804), SkyrimRelease.SkyrimSE);
        q.Aliases.Add(new QuestAlias { ID = 0 });
        q.Aliases.Add(new QuestAlias { ID = 3 });

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(4u, q.NextAliasID);
        Assert.Contains(fills, f => f.Label.Contains("NextAliasID (ANAM", StringComparison.Ordinal));
    }

    // QUST-ANAM-WINS: an explicit NextAliasID=9 is kept, not derived to 1, and no ANAM fill is reported.
    [Fact]
    public void QustAnamWins_ExplicitNextAliasIdIsKept()
    {
        var q = new Quest(Fk(0x805), SkyrimRelease.SkyrimSE) { NextAliasID = 9 };
        q.Aliases.Add(new QuestAlias { ID = 0 });

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(9u, q.NextAliasID);
        Assert.DoesNotContain(fills, f => f.Label.Contains("NextAliasID (ANAM", StringComparison.Ordinal));
    }

    // QUST-FNAM-AUTOFILL: a Flags-less objective -> Flags=0, reported once; an explicit OrWithPrevious is not reset.
    [Fact]
    public void QustFnamAutofill_NullObjectiveFlagsBecomeZero_ExplicitKept()
    {
        var q = new Quest(Fk(0x806), SkyrimRelease.SkyrimSE) { NextAliasID = 0 };
        q.Objectives.Add(new QuestObjective { Index = 10 });
        q.Objectives.Add(new QuestObjective { Index = 20, Flags = QuestObjective.Flag.OrWithPrevious });

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(default(QuestObjective.Flag), q.Objectives[0].Flags);
        Assert.Equal(QuestObjective.Flag.OrWithPrevious, q.Objectives[1].Flags);
        var fill = Assert.Single(fills, f => f.Label.Contains("Flags (FNAM", StringComparison.Ordinal));
        Assert.Contains("Objectives[0]", fill.Label);
    }

    // QUST-ALIAS-AUTOFILL: a bare alias -> FNAM (Flags=0) and VTCK (the null link) filled and reported, and both
    // survive a disk round-trip (a present-but-null VTCK is written, not skipped).
    [Fact]
    public void QustAliasAutofill_BareAliasGetsFnamAndVtck_BothSurviveDisk()
    {
        var m = new SkyrimMod(new ModKey("HcCkpAlias", ModType.Master), SkyrimRelease.SkyrimSE);
        var q = m.Quests.AddNew(); q.EditorID = "HcCkpAliasQ";
        q.NextAliasID = 0;
        q.Aliases.Add(new QuestAlias { ID = 0, Name = "PlayerAlias" });

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(default(QuestAlias.Flag), q.Aliases[0].Flags);
        Assert.NotNull(q.Aliases[0].VoiceTypes.FormKeyNullable);
        Assert.Contains(fills, f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal) && f.Label.Contains("FNAM", StringComparison.Ordinal));
        Assert.Contains(fills, f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal) && f.Label.Contains("VTCK", StringComparison.Ordinal));

        var dir = Path.Combine(Path.GetTempPath(), "hc-dial-ckparity-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "HcCkpAlias.esm");
            m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            try
            {
                var alias = ov.Quests.Single(x => x.FormKey == q.FormKey).Aliases.Single(x => x.ID == 0);
                Assert.NotNull(alias.Flags);
                Assert.NotNull(alias.VoiceTypes.FormKeyNullable);
            }
            finally { (ov as IDisposable)?.Dispose(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { /* temp cleanup best-effort */ } }
    }

    // QUST-ALIAS-WINS: an alias with explicit Flags=Optional and an explicit VoiceTypes is kept; no alias fill.
    [Fact]
    public void QustAliasWins_ExplicitAliasFlagsAndVoiceTypesAreKept()
    {
        var q = new Quest(Fk(0x807), SkyrimRelease.SkyrimSE) { NextAliasID = 0 };
        var voice = FormKey.Factory("02F7C3:Skyrim.esm");
        var alias = new QuestAlias { ID = 0, Flags = QuestAlias.Flag.Optional };
        alias.VoiceTypes.SetTo(voice);
        q.Aliases.Add(alias);

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(QuestAlias.Flag.Optional, q.Aliases[0].Flags);
        Assert.Equal(voice, q.Aliases[0].VoiceTypes.FormKeyNullable);
        Assert.DoesNotContain(fills, f => f.Label.Contains("Aliases[0]", StringComparison.Ordinal));
    }

    // QUST-ALIAS-LOCATION: a Location alias gets FNAM but not VTCK: a FNAM gap and no VTCK gap before, Flags filled
    // and VoiceTypes left unset after, no VTCK fill, and no gap left.
    [Fact]
    public void QustAliasLocation_LocationAliasGetsFnamButNotVtck()
    {
        var q = new Quest(Fk(0x808), SkyrimRelease.SkyrimSE) { NextAliasID = 0 };
        q.Aliases.Add(new QuestAlias { ID = 0, Type = QuestAlias.TypeEnum.Location });

        var before = DialogueCkParity.MissingQuestDefaults(q);
        Assert.Contains(before, g => Names(g, "FNAM") && Names(g, "Aliases[0]"));
        Assert.DoesNotContain(before, g => Names(g, "VTCK"));

        var fills = DialogueCkParity.ApplyQuestDefaults(q);

        Assert.Equal(default(QuestAlias.Flag), q.Aliases[0].Flags);
        Assert.Null(q.Aliases[0].VoiceTypes.FormKeyNullable);
        Assert.DoesNotContain(fills, f => f.Label.Contains("VTCK", StringComparison.Ordinal));
        Assert.Empty(DialogueCkParity.MissingQuestDefaults(q));
    }
}

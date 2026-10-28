using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue check warns when a topic's Quest differs from its branch's Quest (#1149): wording by the
/// branch's TopLevel flag, judged off the fold's branch where the fold wins it, and silent where either quest is unset.</summary>
[Trait("tier", "integration")]
public sealed class DialogueBranchQuestTests : IDisposable
{
    const string Mismatch = "belongs to quest";
    const string TwiceWording = "show twice in game";
    const string MapsWording = "two quests' branch maps";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-dialogue-branchquest-" + Guid.NewGuid().ToString("N"));

    public DialogueBranchQuestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    /// <summary>One master: quests QA and QB, a branch owned by <paramref name="branchQuest"/>, and a topic owned by
    /// <paramref name="topicQuest"/> filed under it. A null quest leaves that link unset.</summary>
    sealed class World
    {
        public required SkyrimMod Mod;
        public required Quest QA, QB;
        public required DialogBranch Branch;
        public required DialogTopic Topic;
        public required DialogView View;
        public required string Path;
    }

    World Build(string name, Func<World, FormKey?> topicQuest, Func<World, FormKey?> branchQuest,
                DialogBranch.Flag? flags = DialogBranch.Flag.TopLevel, Action<World>? tweak = null)
    {
        var m = new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE);
        var qA = m.Quests.AddNew(); qA.EditorID = name + "QA"; DialogueCkParity.ApplyQuestDefaults(qA);
        var qB = m.Quests.AddNew(); qB.EditorID = name + "QB"; DialogueCkParity.ApplyQuestDefaults(qB);
        var br = m.DialogBranches.AddNew(); br.EditorID = name + "Branch"; DialogueCkParity.ApplyBranchDefaults(br);
        br.Flags = flags;
        var t = m.DialogTopics.AddNew(); t.EditorID = name + "Topic"; t.SubtypeName = new RecordType("CUST");
        t.Branch.SetTo(br.FormKey);
        var info = new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = name + "TopicI" };
        DialogueCkParity.ApplyInfoDefaults(info); t.Responses.Add(info);
        br.StartingTopic.SetTo(t.FormKey);
        var v = m.DialogViews.AddNew(); v.EditorID = name + "View"; DialogueCkParity.ApplyViewDefaults(v);
        v.Branches.Add(br.FormKey);
        var w = new World { Mod = m, QA = qA, QB = qB, Branch = br, Topic = t, View = v,
                            Path = System.IO.Path.Combine(_dir, m.ModKey.FileName.String) };
        if (topicQuest(w) is { } tq) t.Quest.SetTo(tq);
        if (branchQuest(w) is { } bq) br.Quest.SetTo(bq);
        v.Quest.SetTo(qA.FormKey);
        tweak?.Invoke(w);
        m.BeginWrite.ToPath(w.Path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return w;
    }

    DialogueValidationReport Run(World w, FormKey seed, DialogueFold? fold = null)
    {
        using var resolver = LoadOrderResolver.Build(new[] { w.Path });
        using var assets = AssetResolver.Build("", "", _dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        var r = DialogueValidate.Run(resolver, assets, seed, fold: fold);
        Assert.Null(r.Error);
        Assert.Null(r.CheckError);
        return r;
    }

    static IReadOnlyList<DialogueIssue> TopicIssues(DialogueValidationReport r) => r.Topics.Single().Issues;

    [Fact]
    public void ATopicInATopLevelBranchOfAnotherQuestWarnsOfADuplicateMenuEntry()
    {
        var w = Build("HcBqTop", w => w.QA.FormKey, w => w.QB.FormKey);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Equal(DialogueIssueSeverity.Warning, issue.Severity);
        Assert.Contains($"DialogTopic.Quest is {FormIdToken.Of(w.QA.FormKey)}", issue.Message);
        Assert.Contains($"{Mismatch} {FormIdToken.Of(w.QB.FormKey)}", issue.Message);
        Assert.Contains($"HcBqTopBranch ({FormIdToken.Of(w.Branch.FormKey)})", issue.Message);
        Assert.Contains(TwiceWording, issue.Message);
        Assert.DoesNotContain(MapsWording, issue.Message);
    }

    [Fact]
    public void ANonTopLevelBranchOfAnotherQuestWarnsWithoutTheDuplicateMenuWording()
    {
        var w = Build("HcBqNorm", w => w.QA.FormKey, w => w.QB.FormKey, flags: DialogBranch.Flag.Blocking);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Equal(DialogueIssueSeverity.Warning, issue.Severity);
        Assert.Contains(MapsWording, issue.Message);
        Assert.DoesNotContain(TwiceWording, issue.Message);
    }

    [Fact]
    public void ABranchWithNoFlagsSubrecordReadsAsTopLevel()
    {
        var w = Build("HcBqNoDnam", w => w.QA.FormKey, w => w.QB.FormKey, flags: null);
        Assert.Contains(TwiceWording, Assert.Single(TopicIssues(Run(w, w.Topic.FormKey))).Message);
    }

    [Fact]
    public void ABranchWithNoEditorIdIsNamedByItsFormId()
    {
        var w = Build("HcBqNoEdid", w => w.QA.FormKey, w => w.QB.FormKey, tweak: w => w.Branch.EditorID = null);
        Assert.Contains($"its Branch {FormIdToken.Of(w.Branch.FormKey)} {Mismatch}",
            Assert.Single(TopicIssues(Run(w, w.Topic.FormKey))).Message);
    }

    [Fact]
    public void ValidatingTheTopicsQuestCarriesTheSameFinding()
    {
        var w = Build("HcBqFan", w => w.QA.FormKey, w => w.QB.FormKey);
        var r = Run(w, w.QA.FormKey);
        Assert.Equal("quest", r.InputKind);
        Assert.Contains(TwiceWording, Assert.Single(TopicIssues(r)).Message);
    }

    [Fact]
    public void TheBranchsQuestFanOutStillListsOnlyTopicsWhoseQuestNamesIt()
    {
        // Known limit, unchanged: fan-out membership is the topic's QNAM, so the foreign topic is not listed.
        var w = Build("HcBqFanB", w => w.QA.FormKey, w => w.QB.FormKey);
        Assert.Empty(Run(w, w.QB.FormKey).Topics);
    }

    [Fact]
    public void ATopicAndBranchOfTheSameQuestAreNotFlagged()
    {
        var w = Build("HcBqSame", w => w.QA.FormKey, w => w.QA.FormKey);
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));
    }

    [Fact]
    public void ABranchWithNoQuestIsNotFlagged()
    {
        var w = Build("HcBqNoBq", w => w.QA.FormKey, _ => null);
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));
    }

    [Fact]
    public void ATopicWithNoQuestGetsOnlyTheUnsetQuestWarning()
    {
        var w = Build("HcBqNoTq", _ => null, w => w.QB.FormKey);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Contains("DialogTopic.Quest is unset", issue.Message);
    }

    [Fact]
    public void ATopicWhoseQuestIsUnresolvedGetsOnlyThatProblem()
    {
        var w = Build("HcBqBadTq", w => new FormKey(w.Mod.ModKey, 0x00ABCDEF), w => w.QB.FormKey);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Equal(DialogueIssueSeverity.Problem, issue.Severity);
        Assert.Contains("the owning quest is unresolved", issue.Message);
    }

    [Fact]
    public void ADanglingBranchGetsOnlyTheExistingProblem()
    {
        var w = Build("HcBqBadBr", w => w.QA.FormKey, w => w.QB.FormKey,
                      tweak: w => w.Topic.Branch.SetTo(new FormKey(w.Mod.ModKey, 0x00ABCDEF)));
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Equal(DialogueIssueSeverity.Problem, issue.Severity);
        Assert.Contains("the branch wiring is broken", issue.Message);
    }

    [Fact]
    public void ABranchWhoseQuestIsInNoPluginIsNotFlagged()
    {
        var w = Build("HcBqDangBq", w => w.QA.FormKey, w => new FormKey(w.Mod.ModKey, 0x00ABCDEF));
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Empty(TopicIssues(Run(w, w.QA.FormKey)));
    }

    [Fact]
    public void ABranchWhoseQuestIsNotAQuestIsNotFlagged()
    {
        var w = Build("HcBqWeapBq", w => w.QA.FormKey, _ => null, tweak: w =>
        {
            var weap = w.Mod.Weapons.AddNew(); weap.EditorID = "HcBqWeapBqWeap";
            w.Branch.Quest.SetTo(weap.FormKey);
        });
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));
        Assert.Empty(TopicIssues(Run(w, w.QA.FormKey)));
    }

    [Fact]
    public void AFoldThatSuppliesTheBranchsQuestCountsItAsResolved()
    {
        // The branch's Quest is in no plugin of the order; only the fold defines it, as a QUST.
        var w = Build("HcBqFoldQ", w => w.QA.FormKey, w => new FormKey(w.Mod.ModKey, 0x00ABCDEF));
        var foldQuest = new FormKey(w.Mod.ModKey, 0x00ABCDEF);
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));

        var f = new SkyrimMod(new ModKey("HcBqFoldQf", ModType.Plugin), SkyrimRelease.SkyrimSE);
        f.Quests.Add(new Quest(foldQuest, SkyrimRelease.SkyrimSE) { EditorID = "HcBqFoldQfQuest" });
        string fPath = Path.Combine(_dir, f.ModKey.FileName.String);
        f.BeginWrite.ToPath(fPath).WithLoadOrder(new ISkyrimModGetter[] { w.Mod }).Write();

        using var fold = DialogueFold.Open(f.ModKey.FileName.String, "test", fPath, null);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey, fold)));
        Assert.Contains($"{Mismatch} {FormIdToken.Of(foldQuest)}", issue.Message);
    }

    [Fact]
    public void AViewHoldingABranchOfAnotherQuestIsNotFlagged()
    {
        // Vanilla views hold other quests' branches legitimately (the CK's "Current quest only" off).
        var w = Build("HcBqView", w => w.QB.FormKey, w => w.QB.FormKey);
        var r = Run(w, w.View.FormKey);
        Assert.Equal("view", r.InputKind);
        Assert.Empty(r.InputIssues);
    }

    [Fact]
    public void AFoldThatMovesTheBranchToAnotherQuestIsJudgedByTheFoldsBranch()
    {
        var w = Build("HcBqFoldM", w => w.QA.FormKey, w => w.QA.FormKey);
        Assert.Empty(TopicIssues(Run(w, w.Topic.FormKey)));

        var f = new SkyrimMod(new ModKey("HcBqFold", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var moved = (DialogBranch)w.Branch.DeepCopy();
        moved.Quest.SetTo(w.QB.FormKey);
        f.DialogBranches.Add(moved);
        string fPath = Path.Combine(_dir, f.ModKey.FileName.String);
        f.BeginWrite.ToPath(fPath).WithLoadOrder(new ISkyrimModGetter[] { w.Mod }).Write();

        using var fold = DialogueFold.Open(f.ModKey.FileName.String, "test", fPath, null);
        var issue = Assert.Single(TopicIssues(Run(w, w.Topic.FormKey, fold)));
        Assert.Contains($"{Mismatch} {FormIdToken.Of(w.QB.FormKey)}", issue.Message);
    }
}

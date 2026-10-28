using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue check judges a Branch, PNAM or LinkTo target's type off the index (the fold's own record
/// where the fold wins it), so no target body is fetched, right type or wrong.</summary>
[Trait("tier", "integration")]
public sealed class DialogueTypeCheckTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-dialogue-typecheck-" + Guid.NewGuid().ToString("N"));

    public DialogueTypeCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    string Write(SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        string path = Path.Combine(_dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    static List<string> LinkIssues(DialogueValidationReport report)
    {
        Assert.Null(report.Error);
        Assert.Null(report.CheckError);
        return report.Topics.Single().Issues.Select(i => i.Message)
            .Where(m => m.Contains("PNAM") || m.Contains("LinkTo") || m.Contains("DialogTopic.Branch points at")).ToList();
    }

    static string LinkTo(FormKey info, FormKey target, string which)
        => $"INFO {FormIdToken.Of(info)} links (LinkTo) to {FormIdToken.Of(target)}, which {which} — the conversation chain is broken.";

    [Fact]
    public void TargetsInAHeldOpenPluginFetchNoBodyAndWrongTypesAreStillReported()
    {
        // The wrong-type Branch, PNAM and LinkTo targets sit in one plugin, held open while the check runs: any body
        // fetch from it fails, so a clean answer proves none was made. The Quest stays in the seed: its body is read
        // once for its aliases. A right-type branch's body is read for its Quest, so the branch target is wrong-type.
        var t = new SkyrimMod(new ModKey("HcDtTargets", ModType.Master), SkyrimRelease.SkyrimSE);
        var lines = new List<FormKey>();
        var topics = new List<FormKey>();
        for (int n = 0; n < 20; n++)
        {
            var dt = t.DialogTopics.AddNew(); dt.EditorID = $"HcDtTarget{n}";
            var i = new DialogResponses(t.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcDtTarget{n}I" };
            dt.Responses.Add(i);
            topics.Add(dt.FormKey); lines.Add(i.FormKey);
        }
        var weap = t.Weapons.AddNew(); weap.EditorID = "HcDtWeap";
        string tPath = Write(t);

        var s = new SkyrimMod(new ModKey("HcDtSeed", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var quest = s.Quests.AddNew(); quest.EditorID = "HcDtQuest";
        var topic = s.DialogTopics.AddNew(); topic.EditorID = "HcDtSeedTopic";
        topic.Quest.SetTo(quest.FormKey); topic.Branch.SetTo(weap.FormKey); topic.SubtypeName = new RecordType("CUST");
        for (int n = 0; n < 20; n++)
        {
            var i = new DialogResponses(s.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcDtSeedI{n}" };
            DialogueCkParity.ApplyInfoDefaults(i);
            i.PreviousDialog.SetTo(lines[n]);
            i.LinkTo.Add(new FormLink<IDialogTopicGetter>(topics[n]));
            topic.Responses.Add(i);
        }
        var bad = new DialogResponses(s.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDtSeedBad" };
        DialogueCkParity.ApplyInfoDefaults(bad);
        bad.PreviousDialog.SetTo(weap.FormKey);
        bad.LinkTo.Add(new FormLink<IDialogTopicGetter>(weap.FormKey));
        topic.Responses.Add(bad);
        string sPath = Write(s, t);

        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", _dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        resolver.Capture();

        using var hold = HeldOpen.Hold(tPath);
        var issues = LinkIssues(DialogueValidate.Run(resolver, assets, topic.FormKey));

        Assert.Equal(new[]
        {
            $"DialogTopic.Branch points at {weap.FormKey}, which resolves to a Weapon, not a dialogue branch (DLBR) — the branch wiring is broken.",
            $"INFO {FormIdToken.Of(bad.FormKey)} has a previous-link (PNAM -> {FormIdToken.Of(weap.FormKey)}) that resolves to a Weapon, not a dialogue line (INFO).",
            LinkTo(bad.FormKey, weap.FormKey, "resolves to a Weapon, not a dialogue topic (DIAL)"),
        }, issues);
    }

    [Fact]
    public void ARightTypedBranchIsNotFetchedWhenTheTopicsQuestIsUnsetOrUnresolved()
    {
        // The branch sits in a held-open plugin: its body is read only to compare quests, which needs the topic's.
        var t = new SkyrimMod(new ModKey("HcDtbTargets", ModType.Master), SkyrimRelease.SkyrimSE);
        var qB = t.Quests.AddNew(); qB.EditorID = "HcDtbQuestB";
        var br = t.DialogBranches.AddNew(); br.EditorID = "HcDtbBranch"; DialogueCkParity.ApplyBranchDefaults(br);
        br.Quest.SetTo(qB.FormKey);
        string tPath = Write(t);

        var sKey = new ModKey("HcDtbSeed", ModType.Plugin);
        var s = new SkyrimMod(sKey, SkyrimRelease.SkyrimSE);
        var unset = s.DialogTopics.AddNew(); unset.EditorID = "HcDtbUnset";
        var unresolved = s.DialogTopics.AddNew(); unresolved.EditorID = "HcDtbUnresolved";
        unresolved.Quest.SetTo(new FormKey(sKey, 0x00ABCDEF));
        foreach (var topic in new[] { unset, unresolved })
        {
            topic.Branch.SetTo(br.FormKey); topic.SubtypeName = new RecordType("CUST");
        }
        string sPath = Write(s, t);

        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", _dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        resolver.Capture();

        using var hold = HeldOpen.Hold(tPath);
        Assert.Empty(LinkIssues(DialogueValidate.Run(resolver, assets, unset.FormKey)));
        Assert.Empty(LinkIssues(DialogueValidate.Run(resolver, assets, unresolved.FormKey)));
    }

    [Fact]
    public void AFoldIsJudgedByItsOwnTypesAndARecordOnlyItHoldsIsPresent()
    {
        var t = new SkyrimMod(new ModKey("HcDtfTargets", ModType.Master), SkyrimRelease.SkyrimSE);
        var target = t.DialogTopics.AddNew(); target.EditorID = "HcDtfTarget";
        string tPath = Write(t);

        var sKey = new ModKey("HcDtfSeed", ModType.Plugin);
        var s = new SkyrimMod(sKey, SkyrimRelease.SkyrimSE);
        var quest = s.Quests.AddNew(); quest.EditorID = "HcDtfQuest";
        var topic = s.DialogTopics.AddNew(); topic.EditorID = "HcDtfSeedTopic";
        topic.Quest.SetTo(quest.FormKey); topic.SubtypeName = new RecordType("CUST");
        // A topic no plugin in the order defines: only the fold carries it.
        var foldOnly = new FormKey(sKey, 0xABC);
        var info = new DialogResponses(s.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDtfSeedI" };
        DialogueCkParity.ApplyInfoDefaults(info);
        info.LinkTo.Add(new FormLink<IDialogTopicGetter>(target.FormKey));
        info.LinkTo.Add(new FormLink<IDialogTopicGetter>(foldOnly));
        topic.Responses.Add(info);
        string sPath = Write(s, t);

        // The off-order file carries the target's FormKey as a WEAP, and the topic only it holds.
        var f = new SkyrimMod(new ModKey("HcDtfFold", ModType.Plugin), SkyrimRelease.SkyrimSE);
        f.Weapons.Add(new Weapon(target.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcDtfWeap" });
        f.DialogTopics.Add(new DialogTopic(foldOnly, SkyrimRelease.SkyrimSE) { EditorID = "HcDtfFoldOnly" });
        string fPath = Write(f, t, s);

        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", _dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());

        Assert.Equal(new[]
        {
            LinkTo(info.FormKey, foldOnly, "is not in the active load order (a dialogue topic (DIAL) missing or disabled)"),
        }, LinkIssues(DialogueValidate.Run(resolver, assets, topic.FormKey)));

        using var fold = DialogueFold.Open(f.ModKey.FileName.String, "test", fPath, null);
        Assert.Equal(new[]
        {
            LinkTo(info.FormKey, target.FormKey, "resolves to a Weapon, not a dialogue topic (DIAL)"),
        }, LinkIssues(DialogueValidate.Run(resolver, assets, topic.FormKey, fold: fold)));
    }
}

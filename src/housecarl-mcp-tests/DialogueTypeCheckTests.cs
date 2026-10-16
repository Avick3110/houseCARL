using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The dialogue check judges a PNAM or LinkTo target's type off the index, so a right-typed target's
/// body is never fetched. The targets sit in their own plugin, held open while the check runs: any body fetch
/// from it fails, so a clean answer proves none was made.</summary>
[Trait("tier", "integration")]
public sealed class DialogueTypeCheckTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-dialogue-typecheck-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    [Fact]
    public void ManyRightTypedTargetsInOnePluginFetchNoBodyAndAWrongTypeIsStillReported()
    {
        Directory.CreateDirectory(_dir);
        var tKey = new ModKey("HcDtTargets", ModType.Master);
        var t = new SkyrimMod(tKey, SkyrimRelease.SkyrimSE);
        var lines = new List<FormKey>();
        var topics = new List<FormKey>();
        for (int n = 0; n < 20; n++)
        {
            var dt = t.DialogTopics.AddNew(); dt.EditorID = $"HcDtTarget{n}";
            var i = new DialogResponses(t.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"HcDtTarget{n}I" };
            dt.Responses.Add(i);
            topics.Add(dt.FormKey); lines.Add(i.FormKey);
        }
        string tPath = Path.Combine(_dir, tKey.FileName.String);
        t.BeginWrite.ToPath(tPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var sKey = new ModKey("HcDtSeed", ModType.Plugin);
        var s = new SkyrimMod(sKey, SkyrimRelease.SkyrimSE);
        var quest = s.Quests.AddNew(); quest.EditorID = "HcDtQuest";
        var branch = s.DialogBranches.AddNew(); branch.EditorID = "HcDtBranch";
        var weap = s.Weapons.AddNew(); weap.EditorID = "HcDtWeap";
        var topic = s.DialogTopics.AddNew(); topic.EditorID = "HcDtSeedTopic";
        topic.Quest.SetTo(quest.FormKey); topic.Branch.SetTo(branch.FormKey); topic.SubtypeName = new RecordType("CUST");
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
        bad.LinkTo.Add(new FormLink<IDialogTopicGetter>(weap.FormKey));
        topic.Responses.Add(bad);
        string sPath = Path.Combine(_dir, sKey.FileName.String);
        s.BeginWrite.ToPath(sPath).WithLoadOrder(new[] { t }).Write();

        var dataDir = Path.Combine(_dir, "data"); Directory.CreateDirectory(dataDir);
        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        resolver.Capture();

        using var hold = HeldOpen.Hold(tPath);
        var report = DialogueValidate.Run(resolver, assets, topic.FormKey);

        Assert.Null(report.Error);
        Assert.Null(report.CheckError);
        var links = report.Topics.Single().Issues
            .Where(i => i.Message.Contains("PNAM") || i.Message.Contains("LinkTo")).ToList();
        var only = Assert.Single(links);
        Assert.Contains(FormIdToken.Of(weap.FormKey), only.Message);
        Assert.Contains("a Weapon, not a dialogue topic", only.Message);
    }
}

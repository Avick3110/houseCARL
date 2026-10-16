using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A fold that wins a link target as a DIFFERENT record type is judged by its own type, not the order's:
/// the order's copy is the right type, so reading the index alone would pass a link the fold breaks.</summary>
[Trait("tier", "integration")]
public sealed class DialogueTypeCheckFoldTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-dialogue-typefold-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best-effort */ } }

    [Fact]
    public void AFoldThatWinsALinkTargetAsAWeaponIsReportedThoughTheOrderHasATopic()
    {
        Directory.CreateDirectory(_dir);
        var tKey = new ModKey("HcDtfTargets", ModType.Master);
        var t = new SkyrimMod(tKey, SkyrimRelease.SkyrimSE);
        var target = t.DialogTopics.AddNew(); target.EditorID = "HcDtfTarget";
        string tPath = Path.Combine(_dir, tKey.FileName.String);
        t.BeginWrite.ToPath(tPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var sKey = new ModKey("HcDtfSeed", ModType.Plugin);
        var s = new SkyrimMod(sKey, SkyrimRelease.SkyrimSE);
        var quest = s.Quests.AddNew(); quest.EditorID = "HcDtfQuest";
        var topic = s.DialogTopics.AddNew(); topic.EditorID = "HcDtfSeedTopic";
        topic.Quest.SetTo(quest.FormKey); topic.SubtypeName = new RecordType("CUST");
        var info = new DialogResponses(s.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcDtfSeedI" };
        DialogueCkParity.ApplyInfoDefaults(info);
        info.LinkTo.Add(new FormLink<IDialogTopicGetter>(target.FormKey));
        topic.Responses.Add(info);
        string sPath = Path.Combine(_dir, sKey.FileName.String);
        s.BeginWrite.ToPath(sPath).WithLoadOrder(new[] { t }).Write();

        // The off-order file carries the target's FormKey as a WEAP.
        var fKey = new ModKey("HcDtfFold", ModType.Plugin);
        var f = new SkyrimMod(fKey, SkyrimRelease.SkyrimSE);
        f.Weapons.Add(new Weapon(target.FormKey, SkyrimRelease.SkyrimSE) { EditorID = "HcDtfWeap" });
        string fPath = Path.Combine(_dir, fKey.FileName.String);
        f.BeginWrite.ToPath(fPath).WithLoadOrder(new[] { t }).Write();

        var dataDir = Path.Combine(_dir, "data"); Directory.CreateDirectory(dataDir);
        using var resolver = LoadOrderResolver.Build(new[] { tPath, sPath });
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        using var fold = DialogueFold.Open(fKey.FileName.String, "test", fPath, null);

        var report = DialogueValidate.Run(resolver, assets, topic.FormKey, fold: fold);

        Assert.Null(report.Error);
        Assert.Null(report.CheckError);
        var link = Assert.Single(report.Topics.Single().Issues, i => i.Message.Contains("LinkTo"));
        Assert.Contains("a Weapon, not a dialogue topic", link.Message);
    }
}

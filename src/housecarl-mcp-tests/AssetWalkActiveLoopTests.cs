using HousecarlCore;
using HousecarlMcp;
using Xunit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>A Traits template loop in an enabled plugin of the active order, in its own instance so the asset walk
/// world's voice tests keep theirs: the walk's loop check reads the active order, not only the from_source= plugins.</summary>
[Trait("tier", "unit")]
public sealed class AssetWalkActiveLoopTests : IDisposable
{
    const string Plugin = "HcLoopActive.esp";
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-asset-walk-loop-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;
    readonly FormKey _line, _loopA, _loopB;

    public AssetWalkActiveLoopTests()
    {
        var instance = Path.Combine(_root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var pluginMod = Path.Combine(instance, "mods", "HcLoopPlugin");
        foreach (var d in new[] { profile, pluginMod, Path.Combine(_root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(Plugin), SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew("HcLoopRace");
        var speaker = mod.Npcs.AddNew("HcLoopSpeaker");
        speaker.Race.SetTo(race);
        speaker.Voice.SetTo(mod.VoiceTypes.AddNew("HcLoopVoice"));
        var topic = mod.DialogTopics.AddNew("HcLoopTopic");
        topic.Quest.SetTo(mod.Quests.AddNew("HcLoopQuest"));
        var line = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE);
        line.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "HcLoopLine" });
        line.Speaker.SetTo(speaker);
        topic.Responses.Add(line);
        _line = line.FormKey;
        var a = mod.Npcs.AddNew("HcLoopA");
        var b = mod.Npcs.AddNew("HcLoopB");
        foreach (var (n, to) in new[] { (a, b), (b, a) })
        {
            n.Race.SetTo(race);
            n.Template.SetTo(to);
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        }
        (_loopA, _loopB) = (a.FormKey, b.FormKey);
        mod.BeginWrite.ToPath(Path.Combine(pluginMod, Plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
           .NoCheckIfLowerRangeDisallowed().Write();

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+HcLoopPlugin\r\n");
        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>A line seeded in an order whose enabled plugin holds a looping Traits pair gives a note naming the loop.</summary>
    [Fact]
    public void ALoopInTheActiveOrderNamesIt()
    {
        var d = _svc.AssetWalk(Array.Empty<string>(), new[] { _line }, null, null);
        Assert.Contains(d.Notes, n => n.Contains("Traits template loop") && n.Contains(FormIdToken.Of(_loopA)) && n.Contains(FormIdToken.Of(_loopB)));
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

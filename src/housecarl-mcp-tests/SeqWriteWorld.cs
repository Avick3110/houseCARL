using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>
/// A synthetic MO2 instance for the <c>WriteSeq</c> tests (from the <c>seq-write-guard</c> probe), built fresh per test
/// because the tests rewrite and restamp the .seq. It holds a houseCARL-owned folder <c>houseCARL - HcSeqSvc</c> with
/// <c>HcSeqSvc.esp</c> (one start-game-enabled quest), a user-owned mod folder <c>SomeoneElsesQuestMod</c>, and, outside
/// the mods folder, <c>HcSeqEmpty.esp</c> with no start-game-enabled quest. Everything lives under its own temp folder.
/// </summary>
public sealed class SeqWriteWorld : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-seq-write-tests-" + Guid.NewGuid().ToString("N"));
    public string Mods { get; }
    public string OwnedFolder { get; }
    public string SvcPlugin { get; }
    public string EmptyPlugin { get; }
    public string UserMod { get; }
    public LoadOrderService Svc { get; }

    /// <summary>Where the out_path= lane puts the .seq for <see cref="SvcPlugin"/> when out_path= is <see cref="UserMod"/>.</summary>
    public string UserSeq => Path.Combine(UserMod, "SEQ", "HcSeqSvc.seq");

    public SeqWriteWorld()
    {
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        Mods = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Mods);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n");

        OwnedFolder = Path.Combine(Mods, "houseCARL - HcSeqSvc");
        Directory.CreateDirectory(OwnedFolder);
        File.WriteAllText(Path.Combine(OwnedFolder, "meta.ini"),
            $"[General]\r\ngameName=skyrimse\r\n\r\n{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\n");
        SvcPlugin = Path.Combine(OwnedFolder, "HcSeqSvc.esp");
        WritePlugin(SvcPlugin, "HcSeqSvc", "SvcOwn", Quest.Flag.StartGameEnabled);

        EmptyPlugin = Path.Combine(Root, "empty", "HcSeqEmpty.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(EmptyPlugin)!);
        WritePlugin(EmptyPlugin, "HcSeqEmpty", "EmptyPlain", Quest.Flag.RunOnce);

        UserMod = Path.Combine(Mods, "SomeoneElsesQuestMod");
        Directory.CreateDirectory(UserMod);

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { /* non-fatal */ }
    }

    static void WritePlugin(string path, string name, string edid, Quest.Flag flags)
    {
        var p = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
        var q = p.Quests.AddNew(); q.EditorID = edid; q.Flags = flags;
        p.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
    }

    /// <summary>Write the out_path= .seq once, so a test can re-run against a destination that already holds the bytes.</summary>
    public SeqOutcome WriteToUserMod() => Svc.WriteSeq(SvcPlugin, null, null, UserMod);

    public static bool PathUnder(string? path, string folder)
        => path is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase);

    public static readonly DateTime Old = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Sentinel = new(2005, 5, 5, 5, 5, 5, DateTimeKind.Utc);
}

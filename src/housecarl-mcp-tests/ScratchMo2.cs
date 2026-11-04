using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A bare MO2 instance under one temp root: the ini, the Default profile, the mods folder and game Data.</summary>
sealed class ScratchMo2
{
    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string Profiles => Path.Combine(Instance, "profiles", "Default");
    public string ModsDir => Path.Combine(Instance, "mods");
    public string DataDir => Path.Combine(Root, "game", "Data");

    public ScratchMo2(string prefix)
    {
        _ = TestCorpus.Path;   // the service's rulebook reads CorpusRulebook.CorpusPath, which this sets
        Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(DataDir);
        SelectProfile("Default");
    }

    /// <summary>Point the ini's selected profile at <paramref name="name"/>, as MO2 does on a profile switch.</summary>
    public void SelectProfile(string name) => File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
        "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(" + name + ")\r\ngamePath=@ByteArray("
        + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

    /// <summary>The path a plugin takes inside a mod folder, with the folder created.</summary>
    public string InMod(string folder, ModKey key)
    {
        var p = Path.Combine(ModsDir, folder, key.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    /// <summary>Write a profile's three files; Default unless <paramref name="name"/> names another, which is created.</summary>
    public void Profile(string loadorder, string plugins, string modlist, string name = "Default")
    {
        var dir = Path.Combine(Instance, "profiles", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "loadorder.txt"), "# header\r\n" + loadorder);
        File.WriteAllText(Path.Combine(dir, "plugins.txt"), plugins);
        File.WriteAllText(Path.Combine(dir, "modlist.txt"), "# header\r\n" + modlist);
    }

    public LoadOrderService Open()
    {
        var svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        svc.Stats();
        return svc;
    }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public void Delete() { try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ } }
}

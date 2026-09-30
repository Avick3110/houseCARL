using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>A bare MO2 instance under a temp root: the ini pointing at an empty game folder, mod folders written by
/// Mutagen, and the three profile lists written verbatim. For a test that needs its own small load order.</summary>
static class SyntheticInstance
{
    /// <summary>Create the instance folder under <paramref name="root"/> with its ini and an empty game Data folder, and
    /// return the instance path.</summary>
    public static string Create(string root)
    {
        var instance = Path.Combine(root, "instance");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(instance, "profiles", "Default"));
        Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
        return instance;
    }

    /// <summary>Write <paramref name="mod"/> into the mod folder <paramref name="folder"/> and return the plugin's path.</summary>
    public static string WriteMod(string instance, string folder, SkyrimMod mod, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(instance, "mods", folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    /// <summary>Write the Default profile's modlist, loadorder and plugins lists, one entry per line.</summary>
    public static void WriteProfile(string instance, IEnumerable<string> modlist, IEnumerable<string> loadorder, IEnumerable<string> plugins)
    {
        var prof = Path.Combine(instance, "profiles", "Default");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), string.Concat(modlist.Select(l => l + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), string.Concat(loadorder.Select(l => l + "\r\n")));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Concat(plugins.Select(l => l + "\r\n")));
    }
}

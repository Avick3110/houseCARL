using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Three plugins carrying game settings: <c>iShared</c> in the master and the middle plugin, <c>fTriple</c>
/// in all three, <c>iUnique</c> in the master alone, and <c>iOver</c> new in the master and the middle plugin with the
/// last plugin overriding the master's, so that FormID's winner is not its defining plugin. Its own world, so no other
/// world's GMST counts move.</summary>
public sealed class SharedGmstWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string MasterName = "HcGmstMaster.esm";
    public const string MidName = "HcGmstMid.esp";
    public const string LastName = "HcGmstLast.esp";
    public const string BrokenName = "HcGmstBroken.esp";

    public FormKey SharedInMaster { get; }
    public FormKey SharedInMid { get; }
    public FormKey TripleInMaster { get; }
    public FormKey TripleInMid { get; }
    public FormKey TripleInLast { get; }
    public FormKey Unique { get; }
    public FormKey OverInMaster { get; }
    public FormKey OverInMid { get; }

    /// <param name="withBrokenPlugin">Also activate, last, a file that is not a plugin, so the index excludes it.</param>
    public SharedGmstWorld(bool withBrokenPlugin = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-sharedgmst-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        var instance = Path.Combine(Root, "inst");

        var master = NewMod(MasterName, ModType.Master);
        SharedInMaster = AddInt(master, "iShared", 1);
        TripleInMaster = AddFloat(master, "fTriple", 1f);
        Unique = AddInt(master, "iUnique", 9);
        OverInMaster = AddInt(master, "iOver", 1);
        var mid = NewMod(MidName, ModType.Plugin);
        SharedInMid = AddInt(mid, "iShared", 2);
        TripleInMid = AddFloat(mid, "fTriple", 2f);
        OverInMid = AddInt(mid, "iOver", 2);
        var last = NewMod(LastName, ModType.Plugin);
        TripleInLast = AddFloat(last, "fTriple", 3f);
        last.GameSettings.Add(new GameSettingInt(OverInMaster, SkyrimRelease.SkyrimSE) { EditorID = "iOver", Data = 3 });

        foreach (var m in new[] { master, mid, last })
        {
            var dir = Path.Combine(instance, "mods", Path.GetFileNameWithoutExtension(m.ModKey.FileName.String) + "Mod");
            Directory.CreateDirectory(dir);
            m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        // A broken plugin loads last, so it could set any of the names the others share.
        var broken = withBrokenPlugin ? BrokenName : null;
        if (broken is not null)
        {
            var dir = Path.Combine(instance, "mods", "HcGmstBrokenMod");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, broken), "not a plugin");
        }
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), $"# header\r\n{MasterName}\r\n{MidName}\r\n{LastName}\r\n"
                                                               + (broken is null ? "" : $"{broken}\r\n"));
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), $"*{MasterName}\r\n*{MidName}\r\n*{LastName}\r\n"
                                                             + (broken is null ? "" : $"*{broken}\r\n"));
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + (broken is null ? "" : "+HcGmstBrokenMod\r\n")
                                                             + "+HcGmstLastMod\r\n+HcGmstMidMod\r\n+HcGmstMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    static SkyrimMod NewMod(string fileName, ModType type) =>
        new(new ModKey(Path.GetFileNameWithoutExtension(fileName), type), SkyrimRelease.SkyrimSE);

    static FormKey AddInt(SkyrimMod m, string editorId, int value)
    {
        var g = new GameSettingInt(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId, Data = value };
        m.GameSettings.Add(g);
        return g.FormKey;
    }

    static FormKey AddFloat(SkyrimMod m, string editorId, float value)
    {
        var g = new GameSettingFloat(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId, Data = value };
        m.GameSettings.Add(g);
        return g.FormKey;
    }

    public static string Token(FormKey fk) => FormIdToken.Of(fk);

    /// <summary>Where one of the three plugins sits on disk.</summary>
    public string PluginPath(string fileName) =>
        Path.Combine(Root, "inst", "mods", Path.GetFileNameWithoutExtension(fileName) + "Mod", fileName);

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class SharedGmstFixture : IDisposable
{
    public SharedGmstWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

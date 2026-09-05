using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world whose one active plugin points every record it holds into a master that is NOT installed, so
/// each of those links dangles into the SAME absent plugin — the shape that makes the absence explainer's cost
/// (an MO2 profile parse plus a mod-folder sweep) visible when it is paid per FormKey instead of per plugin.</summary>
public sealed class AbsentMasterWorld : IDisposable
{
    public const string PatchName = "HcAbsPatch.esp";
    public const string GoneName = "HcAbsGone.esm";

    /// <summary>How many records point into the absent master — one dangling link each, all distinct targets.</summary>
    public const int Danglers = 6;

    public string Root { get; }
    public LoadOrderService Svc { get; }
    public IReadOnlyList<FormKey> Npcs { get; }

    public AbsentMasterWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-absent-master-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "inst");
        var mods = Path.Combine(instance, "mods");
        var patchDir = Path.Combine(mods, "PatchMod");
        var outside = Path.Combine(Root, "not-installed");
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        // Written outside the instance: declared as a master, installed nowhere, so every ref into it dangles.
        var gone = new SkyrimMod(new ModKey("HcAbsGone", ModType.Master), SkyrimRelease.SkyrimSE);
        var races = new List<IRaceGetter>();
        for (int i = 0; i < Danglers; i++)
        { var rc = gone.Races.AddNew(); rc.EditorID = $"HcAbsRace{i}"; races.Add(rc); }
        gone.BeginWrite.ToPath(Path.Combine(outside, GoneName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patch = new SkyrimMod(new ModKey("HcAbsPatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npcs = new List<FormKey>();
        for (int i = 0; i < Danglers; i++)
        {
            var n = patch.Npcs.AddNew();
            n.EditorID = $"HcAbsNpc{i}";
            n.Race.SetTo(races[i].FormKey);       // a DISTINCT dangling target per record, all in one absent plugin
            npcs.Add(n.FormKey);
        }
        Npcs = npcs;
        patch.BeginWrite.ToPath(Path.Combine(patchDir, PatchName))
             .WithLoadOrder(new ISkyrimModGetter[] { gone }).Write();

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+PatchMod\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>resolve_names annotates every dangling link with the same three-cause sentence, and that sentence's
/// tail comes from an explainer that parses the MO2 profile and sweeps the mod folders. It describes the PLUGIN,
/// not the FormID, so a batch of dangling links into one absent master must pay for it once.</summary>
[Trait("tier", "integration")]
public sealed class AbsentMasterLinkTests : IDisposable
{
    readonly AbsentMasterWorld _w = new();

    public void Dispose() => _w.Dispose();

    static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    [Fact]
    public void ResolveNamesExplainsAnAbsentMasterOncePerPluginNotOncePerDanglingLink()
    {
        var ids = _w.Npcs.Select(Fid).ToArray();
        var before = _w.Svc.AbsenceExplanations;
        var r = RecordsTools.Records(_w.Svc, formids: ids,
                                     project: new RecordsTools.RecordsProject
                                     { form = "fields", fields = new[] { "Race" }, resolve_names = true });

        Assert.Contains(AbsentMasterWorld.GoneName, r);              // the links really did dangle into the absent master
        Assert.Equal(1, _w.Svc.AbsenceExplanations - before);
    }
}

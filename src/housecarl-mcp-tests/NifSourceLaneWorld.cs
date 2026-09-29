using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The synthetic MO2 instance the nif_inspect / nif_set source_provider= tests drive: one facegeom mesh with a
/// loose provider and an archive provider under deliberately hostile mod names, plus a second mesh present only inside
/// the root archive of a mod MO2 is not loading.
///
/// <list type="bullet">
/// <item><see cref="LooseMod"/> — an apostrophe in the name; a loose copy of <see cref="FaceRel"/>.</item>
/// <item><see cref="BsaOnlyMod"/> — a space and parentheses; its ONLY copy of <see cref="FaceRel"/> is inside its own
///   <c>Test.bsa</c>, bound to the active <c>Test.esp</c>.</item>
/// <item><see cref="OffMod"/> — unticked; <see cref="OffRel"/> lives only inside its own <c>Off.bsa</c>.</item>
/// </list></summary>
public sealed class NifSourceLaneWorld : IDisposable
{
    public const string BsaOnlyMod = "Donor Mod (SE)";
    public const string LooseMod = "JK's Skyrim";
    public const string OffMod = "Unticked Donor";

    /// <summary>The facegeom path for <see cref="NpcFormId"/>, so the same file is reachable as a path AND as npc=.</summary>
    public const string FaceRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000001.nif";
    public const string NpcFormId = "000001:Test.esp";
    public const string OffRel = @"meshes\actors\character\facegendata\facegeom\Test.esp\00000002.nif";

    public string Root { get; }
    public LoadOrderService Svc { get; }

    public NifSourceLaneWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-nif-source-lane-" + Guid.NewGuid().ToString("N"));
        var inst = Path.Combine(Root, "inst");
        var (mods, prof) = NifSourceLaneInstance.Make(inst);
        var mesh = NifInspectFixtures.BuildSyntheticSe();

        var bsaMod = Path.Combine(mods, BsaOnlyMod);
        Directory.CreateDirectory(bsaMod);
        File.WriteAllText(Path.Combine(bsaMod, "Test.esp"), "x");
        File.WriteAllBytes(Path.Combine(bsaMod, "Test.bsa"), NifSourceLaneInstance.Archive(FaceRel, mesh));

        NifSourceLaneInstance.Loose(Path.Combine(mods, LooseMod), FaceRel, mesh);

        var offDir = Path.Combine(mods, OffMod);
        Directory.CreateDirectory(offDir);
        File.WriteAllBytes(Path.Combine(offDir, "Off.bsa"), NifSourceLaneInstance.Archive(OffRel, mesh));

        NifSourceLaneInstance.Profile(prof, new[] { "Test.esp" }, new[] { "*Test.esp" },
                                      new[] { "+" + LooseMod, "+" + BsaOnlyMod, "-" + OffMod });
        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "u.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The #545 instance: an enabled mod whose bound archive will not read (so the build's scan is incomplete),
/// and the only copy of <see cref="SoleRel"/> loose inside a mod MO2 is not loading, so nothing active provides it.</summary>
public sealed class NifSourceSoleWorld : IDisposable
{
    public const string SoleMod = "Sole Donor";
    public const string SoleRel = @"meshes\hcprobe\sole-donor.nif";

    public string Root { get; }
    public LoadOrderService Svc { get; }

    public NifSourceSoleWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-nif-source-sole-" + Guid.NewGuid().ToString("N"));
        var inst = Path.Combine(Root, "inst");
        var (mods, prof) = NifSourceLaneInstance.Make(inst);

        var broken = Path.Combine(mods, "Broken Archive");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "Broken.esp"), "x");
        File.WriteAllBytes(Path.Combine(broken, "Broken.bsa"), Enumerable.Repeat((byte)0xFF, 64).ToArray());

        NifSourceLaneInstance.Loose(Path.Combine(mods, SoleMod), SoleRel, NifInspectFixtures.BuildSyntheticSe());

        NifSourceLaneInstance.Profile(prof, new[] { "Broken.esp" }, new[] { "*Broken.esp" },
                                      new[] { "+Broken Archive", "-" + SoleMod });
        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "u.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>The instance scaffolding both worlds above share: ModOrganizer.ini, the profile files, a loose file, a
/// one-entry archive.</summary>
static class NifSourceLaneInstance
{
    public static (string mods, string prof) Make(string inst)
    {
        var mods = Path.Combine(inst, "mods");
        var data = Path.Combine(inst, "game", "Data");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, data, prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");
        return (mods, prof);
    }

    public static void Profile(string prof, string[] loadorder, string[] plugins, string[] modlist)
    {
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", loadorder) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n" + string.Join("\r\n", modlist) + "\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
    }

    public static void Loose(string modDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    /// <summary>A one-entry uncompressed SE archive carrying <paramref name="rel"/>.</summary>
    public static byte[] Archive(string rel, byte[] bytes) => BsaBuilder.Build(105,
        BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
        new[] { (Path.GetDirectoryName(rel)!, new[] { (Path.GetFileName(rel), bytes) }) });
}

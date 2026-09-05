using HousecarlGenerator;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The synthetic MO2 instance the asset_status directory / glob tests are driven over: one master's facegen
/// set spread across two loose mods and one BSA, which is the shape #246 is about — a sweep must union every provider
/// the VFS loads, not just the loose ones, and must still call the winner per file.
///
/// <para>What it carries:</para>
/// <list type="bullet">
/// <item><c>FaceHigher</c> — the higher-priority loose mod: <c>0002.nif</c> (contending with FaceBase) and
///   <c>0004.nif</c>.</item>
/// <item><c>FaceBase</c> — the lower-priority loose mod: <c>0001.nif</c>, <c>0002.nif</c>, <c>0003.nif</c>, and one
///   facetint <c>.dds</c> under textures\ so a glob has something to narrow away.</item>
/// <item><c>ArchiveMod</c> — <c>HcArch.bsa</c>, authored by <see cref="BsaBuilder"/> and bound to the active
///   <c>HcArch.esp</c>, carrying <c>0005.nif</c> — reachable only through the archive lane.</item>
/// <item><c>Face Extras (SE)</c> — a mod whose NAME carries a parenthetical, providing one file outside every sweep
///   target above, so a rendered provider token can be read for where the name ends (#340).</item>
/// </list>
///
/// <para>No .esp is written to disk: asset resolution is decoupled from the record index, so the profile naming a
/// plugin is all the archive binding needs.</para></summary>
public sealed class AssetSelectWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The defining master whose facegen folder the sweep is aimed at.</summary>
    public const string Master = "HcMaster.esm";
    /// <summary>The facegen mesh folder — the #246 sweep target, one call per defining master.</summary>
    public const string FaceGeomDir = @"meshes\actors\character\facegendata\facegeom\" + Master;
    /// <summary>The facetint texture folder, so a sweep can be aimed at more than one root.</summary>
    public const string FaceTintDir = @"textures\actors\character\facegendata\facetint\" + Master;

    /// <summary>Distinct files the facegeom folder holds across every provider: three from FaceBase, one more from
    /// FaceHigher, one from the BSA (FaceHigher's 0002 is a contender, not a sixth file).</summary>
    public const int FaceGeomFiles = 5;

    /// <summary>A mod folder whose own name carries a parenthetical, which is legal on Windows and common in the
    /// wild ("SkyUI (SE)"). Its only file sits outside every sweep target above, so it changes no other count.</summary>
    public const string ParenMod = "Face Extras (SE)";
    /// <summary>The one path <see cref="ParenMod"/> provides.</summary>
    public const string ParenPath = @"textures\hcextras\extra.dds";

    public string Rel(string leaf) => FaceGeomDir + "\\" + leaf;

    public AssetSelectWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-asset-select-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var data = Path.Combine(Root, "game", "Data");
        var faceBase = Path.Combine(mods, "FaceBase");
        var faceHigher = Path.Combine(mods, "FaceHigher");
        var archiveMod = Path.Combine(mods, "ArchiveMod");
        var parenMod = Path.Combine(mods, ParenMod);
        foreach (var d in new[] { profile, data, faceBase, faceHigher, archiveMod, parenMod }) Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        Loose(faceBase, Rel("0001.nif"));
        Loose(faceBase, Rel("0002.nif"));
        Loose(faceBase, Rel("0003.nif"));
        Loose(faceBase, FaceTintDir + @"\0001.dds");
        Loose(faceHigher, Rel("0002.nif"));                    // contends with FaceBase and wins on priority
        Loose(faceHigher, Rel("0004.nif"));
        Loose(parenMod, ParenPath);                            // a provider name that contains its own parenthetical

        File.WriteAllBytes(Path.Combine(archiveMod, "HcArch.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[] { (FaceGeomDir, new[] { ("0005.nif", BsaBuilder.Bytes("NIF-0005", 48)) }) }));

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcArch.esp\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcArch.esp\r\n");
        // Listed first = higher priority, the MO2 modlist.txt order.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+ArchiveMod\r\n+FaceHigher\r\n+FaceBase\r\n+" + ParenMod + "\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static void Loose(string modDir, string rel)
    {
        var p = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

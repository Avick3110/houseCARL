using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;

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
/// <item>Three canonically-named FaceGen PAIRS under a second master (<see cref="PairMaster"/>), which is what the
///   <c>formids=</c> SELECT derives: <see cref="SplitFormId"/> (head from FaceHigher and from the BSA, tint from
///   FaceBase — the two halves win from different mods), <see cref="MatchedFormId"/> (both halves FaceBase) and
///   <see cref="TintAbsentFormId"/> (a winning head whose tint has no provider at all).</item>
/// </list>
///
/// <para>No .esp is written to disk: asset resolution is decoupled from the record index, so the profile naming a
/// plugin is all the archive binding needs.</para></summary>
public sealed class AssetSelectWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    /// <summary>MO2's mods root, so a test can build the raw on-disk path into a mod folder that the tools refuse.</summary>
    public string ModsDir { get; }

    /// <summary>The defining master whose facegen folder the sweep is aimed at.</summary>
    public const string Master = "HcMaster.esm";
    /// <summary>The facegen mesh folder — the #246 sweep target, one call per defining master.</summary>
    public const string FaceGeomDir = @"meshes\actors\character\facegendata\facegeom\" + Master;
    /// <summary>The facetint texture folder, so a sweep can be aimed at more than one root.</summary>
    public const string FaceTintDir = @"textures\actors\character\facegendata\facetint\" + Master;

    /// <summary>Distinct files the facegeom folder holds across every provider: three from FaceBase, one more from
    /// FaceHigher, one from the BSA (FaceHigher's 0002 is a contender, not a sixth file).</summary>
    public const int FaceGeomFiles = 5;

    /// <summary>A SECOND defining master, holding the canonically-named FaceGen pairs the <c>formids=</c> SELECT
    /// derives. Its own master so the pairs sit outside <see cref="FaceGeomDir"/> and leave every count the
    /// directory-sweep tests assert exactly as it was.</summary>
    public const string PairMaster = "HcPair.esm";

    /// <summary>An NPC whose two halves win from DIFFERENT mods — the dark-face split. Its head is also in the BSA,
    /// so the head is loose-over-BSA as well.</summary>
    public const string SplitFormId = "0B0B0B:" + PairMaster;

    /// <summary>An NPC whose two halves win from the same mod — the clean pair.</summary>
    public const string MatchedFormId = "0C0C0C:" + PairMaster;

    /// <summary>An NPC whose head wins and whose tint has no provider anywhere — the "winning mesh names a tint that
    /// exists nowhere" case the whole-order sweep counts.</summary>
    public const string TintAbsentFormId = "0D0D0D:" + PairMaster;

    /// <summary>The Data-relative FaceGen path for one of the FormIDs above, computed by the very transform the tool
    /// uses — so the world and the tool cannot disagree about where a bake lives.</summary>
    public static string Face(string formid, FaceGenSlot slot) => FaceGenPath.For(FormKey.Factory(formid), slot);

    /// <summary>The three head meshes the pairs above add under <c>facegeom</c>, outside
    /// <see cref="FaceGeomDir"/>.</summary>
    public const int PairFaceGeomNifs = 3;

    /// <summary>Every <c>.nif</c> under <c>meshes\actors</c> in this world — what a '**' sweep from the actors root
    /// finds, as against <see cref="FaceGeomFiles"/> under one master's folder.</summary>
    public const int AllFaceGeomNifs = FaceGeomFiles + PairFaceGeomNifs;

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

        // The FaceGen pairs, keyed by FormID the way the game keys a bake. Split's head is in FaceHigher AND in the
        // BSA (loose beats BSA) while its tint is in FaceBase, so the two halves win from different mods; Matched is
        // a clean same-source pair; TintAbsent has a winning head and no tint anywhere.
        Loose(faceHigher, Face(SplitFormId, FaceGenSlot.Mesh));
        Loose(faceBase, Face(SplitFormId, FaceGenSlot.Tint));
        Loose(faceBase, Face(MatchedFormId, FaceGenSlot.Mesh));
        Loose(faceBase, Face(MatchedFormId, FaceGenSlot.Tint));
        Loose(faceBase, Face(TintAbsentFormId, FaceGenSlot.Mesh));

        var pairGeomDir = FaceGenPath.Root(FaceGenSlot.Mesh).TrimEnd('\\') + "\\" + PairMaster;
        File.WriteAllBytes(Path.Combine(archiveMod, "HcArch.bsa"),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
                new[]
                {
                    (FaceGeomDir, new[] { ("0005.nif", BsaBuilder.Bytes("NIF-0005", 48)) }),
                    (pairGeomDir, new[] { (Path.GetFileName(Face(SplitFormId, FaceGenSlot.Mesh)), BsaBuilder.Bytes("NIF-SPLIT", 48)) }),
                }));

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcArch.esp\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcArch.esp\r\n");
        // Listed first = higher priority, the MO2 modlist.txt order.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+ArchiveMod\r\n+FaceHigher\r\n+FaceBase\r\n+" + ParenMod + "\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        ModsDir = mods;
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

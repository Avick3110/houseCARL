using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>
/// The synthetic MO2 instance the facegen family's tests are driven over: real plugins written by Mutagen, real
/// loose facegen files in real mod folders, so the sweep answers off the two precedences it exists to join rather
/// than off a hand-built result.
///
/// <para>What it carries, one NPC per class the family must tell apart:</para>
/// <list type="bullet">
/// <item><c>Clean</c> — both halves in <c>FgBase</c>, which also ships the defining master, so the record and the
///   files agree and NOTHING is reported.</item>
/// <item><c>TintAbsent</c> / <c>MeshAbsent</c> — one half present in <c>FgBase</c>, the other nowhere.</item>
/// <item><c>BakeAbsent</c> — neither half anywhere.</item>
/// <item><c>Split</c> — mesh from <c>FgBase</c>, tint from <c>FgOther</c>: two unrelated products.</item>
/// <item><c>Family</c> — mesh from <c>FgBase</c>, tint from <c>FgBase - Update</c>: one product's two folders.</item>
/// <item><c>Stale</c> — a clean pair in <c>FgBase</c> whose record <c>HcFgOverhaul.esp</c> wins with a different
///   TextureLighting, so the bake no longer matches the winner.</item>
/// <item><c>Templated</c> — <c>Template</c> + <c>Traits</c>: excluded, never flagged.</item>
/// <item><c>Beast</c> — a race with no <c>FaceGenHead</c> flag: excluded, never flagged.</item>
/// </list>
///
/// <para>Plus four files that belong to no NPC: one for a FormID nothing defines, one under a folder named for a
/// plugin the order does not load, one carrying a foreign load-order index byte, and one whose name is not the
/// eight-hex form at all.</para>
/// </summary>
public sealed class FaceGenWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    public const string MasterName = "HcFgMaster.esm";
    public const string OverhaulName = "HcFgOverhaul.esp";
    public const string BaseMod = "FgBase";
    public const string UpdateMod = "FgBase - Update";
    public const string OtherMod = "FgOther";
    public const string OverhaulMod = "FgOverhaul";

    /// <summary>A plugin the order does not load, whose orphaned facegen folder is one of the inert rows.</summary>
    public const string OffOrderFolder = "HcFgNotLoaded.esp";

    /// <summary>The NPCs, by the EditorID the rows print, so a test names the NPC rather than a FormID.</summary>
    public IReadOnlyDictionary<string, FormKey> Npcs { get; }

    public FaceGenWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-facegen-world-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var baseDir = Path.Combine(mods, BaseMod);
        var updateDir = Path.Combine(mods, UpdateMod);
        var otherDir = Path.Combine(mods, OtherMod);
        var overhaulDir = Path.Combine(mods, OverhaulMod);
        foreach (var d in new[] { profile, baseDir, updateDir, otherDir, overhaulDir,
                                  Path.Combine(Root, "game", "Data") })
            Directory.CreateDirectory(d);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var master = new SkyrimMod(new ModKey("HcFgMaster", ModType.Master), SkyrimRelease.SkyrimSE);

        var manRace = master.Races.AddNew();
        manRace.EditorID = "HcFgManRace";
        manRace.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;      // bakes a head
        var beastRace = master.Races.AddNew();
        beastRace.EditorID = "HcFgBeastRace";
        beastRace.Flags = Race.Flag.Walks;                               // no FaceGenHead: no bake of any kind

        var keys = new Dictionary<string, FormKey>(StringComparer.Ordinal);
        Npc Add(string editorId, IRaceGetter race)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = editorId;
            n.Race.SetTo(race);
            n.TextureLighting = System.Drawing.Color.FromArgb(0, 10, 20, 30);
            keys[editorId] = n.FormKey;
            return n;
        }

        Add("HcFgClean", manRace);
        Add("HcFgTintAbsent", manRace);
        Add("HcFgMeshAbsent", manRace);
        Add("HcFgBakeAbsent", manRace);
        Add("HcFgSplit", manRace);
        Add("HcFgFamily", manRace);
        var stale = Add("HcFgStale", manRace);
        Add("HcFgBeast", beastRace);
        var templateTarget = Add("HcFgTemplateSource", manRace);
        var templated = Add("HcFgTemplated", manRace);
        templated.Template.SetTo(templateTarget);
        templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;

        master.BeginWrite.ToPath(Path.Combine(baseDir, MasterName))
              .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The overhaul wins HcFgStale's record with a different face colour while FgBase still wins its files.
        var overhaul = new SkyrimMod(new ModKey("HcFgOverhaul", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var over = overhaul.Npcs.GetOrAddAsOverride(stale);
        over.TextureLighting = System.Drawing.Color.FromArgb(0, 200, 100, 50);
        overhaul.BeginWrite.ToPath(Path.Combine(overhaulDir, OverhaulName))
                .WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        Npcs = keys;

        Loose(baseDir, Mesh(keys["HcFgClean"]));
        Loose(baseDir, Tint(keys["HcFgClean"]));
        Loose(baseDir, Mesh(keys["HcFgTintAbsent"]));
        Loose(baseDir, Tint(keys["HcFgMeshAbsent"]));
        Loose(baseDir, Mesh(keys["HcFgSplit"]));
        Loose(otherDir, Tint(keys["HcFgSplit"]));
        Loose(baseDir, Mesh(keys["HcFgFamily"]));
        Loose(updateDir, Tint(keys["HcFgFamily"]));
        Loose(baseDir, Mesh(keys["HcFgStale"]));
        Loose(baseDir, Tint(keys["HcFgStale"]));

        // Files no NPC reads.
        Loose(baseDir, GeomDir(MasterName) + @"\00099999.nif");                 // no record defines this FormID
        Loose(baseDir, GeomDir(OffOrderFolder) + @"\00000801.nif");             // a folder for a plugin not loaded
        Loose(baseDir, GeomDir(MasterName) + @"\notahexname.nif");              // not the eight-hex form
        Loose(baseDir, GeomDir(MasterName) + "\\05" + keys["HcFgBakeAbsent"].ID.ToString("X6") + ".nif");
        // A foreign-index MESH whose local id has a canonical file in the TINT tree only. The two trees share a
        // master folder name, so a canonical set keyed by folder alone would let the .dds vouch for this .nif and
        // the bake would surface as mesh_absent — whose fix is the wrong repair for a file that needs renaming.
        Loose(baseDir, GeomDir(MasterName) + "\\05" + keys["HcFgMeshAbsent"].ID.ToString("X6") + ".nif");

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"),
            "# header\r\n" + MasterName + "\r\n" + OverhaulName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + MasterName + "\r\n*" + OverhaulName + "\r\n");
        // Listed first = higher priority.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + OverhaulMod + "\r\n+" + OtherMod + "\r\n+" + UpdateMod + "\r\n+" + BaseMod + "\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    static string GeomDir(string master) => @"meshes\actors\character\facegendata\facegeom\" + master;
    static string Mesh(FormKey fk) => HousecarlCore.FaceGenPath.For(fk, HousecarlCore.FaceGenSlot.Mesh);
    static string Tint(FormKey fk) => HousecarlCore.FaceGenPath.For(fk, HousecarlCore.FaceGenSlot.Tint);

    static void Loose(string modDir, string rel)
    {
        var p = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

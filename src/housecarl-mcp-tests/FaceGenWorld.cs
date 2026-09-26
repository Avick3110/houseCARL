using System.Security.AccessControl;
using System.Security.Principal;
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
/// <item><c>NoPlugin</c> — a clean pair in <c>FgBakesOnly</c>, which ships no plugin: untested for a stale bake.</item>
/// <item><c>Unlisted</c> — a clean pair in <c>FgUnlisted</c>, whose folder denies LISTING (not traverse), so the
///   bake resolves but the folder's plugins cannot be read: untested for a different reason.</item>
/// <item><c>CharGenPreset</c> and the Player (<c>000007:Skyrim.esm</c>, in a second master) — no files anywhere:
///   <c>never_baked</c>. Every other NPC carries one head part unless named here.</item>
/// <item><c>NoHeadParts</c> — no head parts or face data, no files: <c>bake_absent</c> with the dummy-actor hint.</item>
/// <item><c>NoHeadPartsFaceData</c> — no head parts but a FaceMorph, and no files: <c>bake_absent</c>, no hint.</item>
/// <item><c>CharGenPresetBaked</c> / <c>NoHeadPartsBaked</c> — the same two kinds with a mesh on disk and no tint:
///   still <c>tint_absent</c>.</item>
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
    public const string BakesOnlyMod = "FgBakesOnly";
    public const string UnlistedMod = "FgUnlisted";
    public const string VanillaName = "Skyrim.esm";
    public const string VanillaMod = "FgVanilla";

    /// <summary>Whether the listing deny on <see cref="UnlistedMod"/> bit on this host; false off Windows.</summary>
    public bool UnlistedStaged { get; }

    readonly string _unlistedDir;
    readonly FileSystemAccessRule? _unlistedDeny;

    /// <summary>A plugin the order does not load, whose orphaned facegen folder is one of the inert rows.</summary>
    public const string OffOrderFolder = "HcFgNotLoaded.esp";

    /// <summary>The NPCs, by the EditorID the rows print, so a test names the NPC rather than a FormID.</summary>
    public IReadOnlyDictionary<string, FormKey> Npcs { get; }

    public FaceGenWorld() : this(degradedAssets: false) { }

    /// <param name="degradedAssets">an empty base-archive list (a discovery warning) and four unreadable archives
    /// paired with the two plugins (read failures); for a test that builds its own instance.</param>
    /// <param name="playerBaked">Give the Player a mesh on disk and no tint, so it classifies as tint_absent.</param>
    internal FaceGenWorld(bool degradedAssets = false, bool playerBaked = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-facegen-world-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        var baseDir = Path.Combine(mods, BaseMod);
        var updateDir = Path.Combine(mods, UpdateMod);
        var otherDir = Path.Combine(mods, OtherMod);
        var overhaulDir = Path.Combine(mods, OverhaulMod);
        var bakesOnlyDir = Path.Combine(mods, BakesOnlyMod);
        _unlistedDir = Path.Combine(mods, UnlistedMod);
        var vanillaDir = Path.Combine(mods, VanillaMod);
        foreach (var d in new[] { profile, baseDir, updateDir, otherDir, overhaulDir, bakesOnlyDir, _unlistedDir, vanillaDir,
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
        var head = master.HeadParts.AddNew();
        head.EditorID = "HcFgHead";

        var keys = new Dictionary<string, FormKey>(StringComparer.Ordinal);
        Npc Add(string editorId, IRaceGetter race)
        {
            var n = master.Npcs.AddNew();
            n.EditorID = editorId;
            n.Race.SetTo(race);
            n.HeadParts.Add(head);
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
        Add("HcFgNoPlugin", manRace);
        Add("HcFgUnlisted", manRace);
        var templateTarget = Add("HcFgTemplateSource", manRace);
        var templated = Add("HcFgTemplated", manRace);
        templated.Template.SetTo(templateTarget);
        templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        Add("HcFgCharGenPreset", manRace).Configuration.Flags |= NpcConfiguration.Flag.IsCharGenFacePreset;
        Add("HcFgNoHeadParts", manRace).HeadParts.Clear();
        Add("HcFgCharGenPresetBaked", manRace).Configuration.Flags |= NpcConfiguration.Flag.IsCharGenFacePreset;
        Add("HcFgNoHeadPartsBaked", manRace).HeadParts.Clear();
        var faceData = Add("HcFgNoHeadPartsFaceData", manRace);
        faceData.HeadParts.Clear();
        faceData.FaceMorph = new NpcFaceMorph { NoseLongVsShort = 0.5f };

        // The Player at its real FormKey, with a baking race and a head part so only the FormKey singles it out.
        var sky = new SkyrimMod(new ModKey("Skyrim", ModType.Master), SkyrimRelease.SkyrimSE);
        var nordRace = sky.Races.AddNew();
        nordRace.EditorID = "HcFgNordRace";
        nordRace.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var skyHead = sky.HeadParts.AddNew();
        skyHead.EditorID = "HcFgPlayerHead";
        var playerNpc = new Npc(HousecarlCore.FaceGenCheck.PlayerFormKey, SkyrimRelease.SkyrimSE) { EditorID = "Player" };
        playerNpc.Race.SetTo(nordRace);
        playerNpc.HeadParts.Add(skyHead);
        sky.Npcs.Add(playerNpc);
        keys["Player"] = playerNpc.FormKey;
        sky.BeginWrite.ToPath(Path.Combine(vanillaDir, VanillaName))
           .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoCheckIfLowerRangeDisallowed().Write();

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
        Loose(bakesOnlyDir, Mesh(keys["HcFgNoPlugin"]));
        Loose(bakesOnlyDir, Tint(keys["HcFgNoPlugin"]));
        Loose(_unlistedDir, Mesh(keys["HcFgUnlisted"]));
        Loose(_unlistedDir, Tint(keys["HcFgUnlisted"]));
        Loose(baseDir, Mesh(keys["HcFgCharGenPresetBaked"]));
        Loose(baseDir, Mesh(keys["HcFgNoHeadPartsBaked"]));
        if (playerBaked) Loose(vanillaDir, Mesh(keys["Player"]));

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
            "# header\r\n" + VanillaName + "\r\n" + MasterName + "\r\n" + OverhaulName + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + MasterName + "\r\n*" + OverhaulName + "\r\n");
        // Listed first = higher priority.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "# header\r\n+" + OverhaulMod + "\r\n+" + OtherMod + "\r\n+" + UpdateMod + "\r\n+" + BaseMod
            + "\r\n+" + BakesOnlyMod + "\r\n+" + UnlistedMod + "\r\n+" + VanillaMod + "\r\n");
        // A base-archive list naming an archive not on disk reads clean; an empty one makes the asset build warn.
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"),
            "[Archive]\r\nsResourceArchiveList=" + (degradedAssets ? "" : "Skyrim - Meshes0.bsa") + "\r\n");
        if (degradedAssets)
            foreach (var (bsaDir, bsa) in new[] { (baseDir, "HcFgMaster.bsa"), (baseDir, "HcFgMaster - Textures.bsa"),
                                                  (overhaulDir, "HcFgOverhaul.bsa"), (overhaulDir, "HcFgOverhaul - Textures.bsa") })
                File.WriteAllBytes(Path.Combine(bsaDir, bsa), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        // Deny listing only, not inherited (as LocalizedModFolderUnreadableTests does), so the bake still resolves by traverse.
        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(_unlistedDir);
            var acl = dir.GetAccessControl();
            _unlistedDeny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory,
                                                     AccessControlType.Deny);
            acl.AddAccessRule(_unlistedDeny);
            dir.SetAccessControl(acl);
            try { Directory.EnumerateFiles(_unlistedDir).ToList(); }
            catch (UnauthorizedAccessException) { UnlistedStaged = true; }
            catch (IOException) { UnlistedStaged = true; }
        }

        try { Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json"))); }
        catch { LiftDeny(); throw; }   // xUnit skips Dispose when the constructor throws, so the deny comes off here
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

    /// <summary>Take the listing deny off; a failure throws, so a denied folder never leaks into temp unreported.</summary>
    void LiftDeny()
    {
        if (_unlistedDeny is null || !OperatingSystem.IsWindows()) return;
        var dir = new DirectoryInfo(_unlistedDir);
        var acl = dir.GetAccessControl();
        acl.RemoveAccessRule(_unlistedDeny);
        dir.SetAccessControl(acl);
    }

    public void Dispose()
    {
        LiftDeny();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

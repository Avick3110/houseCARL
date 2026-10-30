using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlMcpTests;

/// <summary>The synthetic instance the asset walk tests drive (#1270, #1081): one active plugin of NPCs, head parts,
/// quests and races, a DISABLED donor mod that ships the hair mesh, its SMP XML and one texture, and an enabled base
/// mod that ships a second texture and the FaceGen of the NPCs whose face is drawn.
/// <list type="bullet">
/// <item><c>HcWalkHair</c> (HDPT, model <see cref="HairNif"/>) on <c>HcWalkHero</c>, a FaceGenHead-race NPC with an
///   own script; <see cref="HairNif"/> names <see cref="HairXml"/> on its root, <see cref="DonorTex"/> (donor),
///   <see cref="BaseTex"/> (base mod) and <see cref="MissingTex"/> (nowhere).</item>
/// <item><c>HcWalkBeast</c>, whose race has no FaceGenHead: no FaceGen node.</item>
/// <item><c>HcWalkTemplated</c> (Traits) → <c>HcWalkMid</c> (Traits) → <c>HcWalkHero</c>; <c>HcWalkLeveled</c>
///   (Traits) → a leveled list of <c>HcWalkHero</c> and <c>HcWalkBeast2</c>; <c>HcWalkLoopA</c> ↔ <c>HcWalkLoopB</c>.</item>
/// <item><c>HcWalkSge</c>, a start-game-enabled quest whose <c>Seq\HcWalk.seq</c> is absent; <c>HcWalkPlain</c>, not.</item>
/// <item><c>Cape</c>, an enabled mod with <see cref="CapeNif"/> naming <see cref="CapeXml"/>, which it also ships.</item>
/// </list></summary>
public sealed class AssetWalkWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public string ModsDir { get; }

    public const string Plugin = "HcWalk.esp";
    public const string DonorMod = "HcWalkDonor";
    public const string BaseMod = "HcWalkBase";
    public const string HairNif = @"meshes\hcwalk\hair.nif";
    public const string HairXml = @"SKSE\Plugins\hdtSkinnedMeshConfigs\hcwalk\hair.xml";
    public const string DonorTex = @"textures\hcwalk\hair.dds";
    public const string BaseTex = @"textures\hcwalk\hair_n.dds";
    public const string MissingTex = @"textures\hcwalk\hair_s.dds";
    public const string CapeNif = @"meshes\hcwalk\cape.nif";
    public const string CapeXml = @"SKSE\Plugins\hdtSkinnedMeshConfigs\hcwalk\cape.xml";
    public const string OwnScript = "HcWalkOwnScript";

    public Dictionary<string, FormKey> Keys { get; } = new(StringComparer.Ordinal);
    public string Id(string editorId) => FormIdToken.Of(Keys[editorId]);

    public AssetWalkWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-asset-walk-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        ModsDir = Path.Combine(instance, "mods");
        var pluginMod = Path.Combine(ModsDir, "HcWalkPlugin");
        var donor = Path.Combine(ModsDir, DonorMod);
        var baseMod = Path.Combine(ModsDir, BaseMod);
        var cape = Path.Combine(ModsDir, "Cape");
        foreach (var d in new[] { profile, pluginMod, donor, baseMod, cape, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mod = new SkyrimMod(ModKey.FromFileName(Plugin), SkyrimRelease.SkyrimSE);
        var race = mod.Races.AddNew("HcWalkRace");
        race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
        var beastRace = mod.Races.AddNew("HcWalkBeastRace");
        beastRace.Flags = Race.Flag.Walks;
        var hair = mod.HeadParts.AddNew("HcWalkHair");
        hair.Model = new Model { File = HairNif };

        Npc Add(string edid, IRaceGetter r)
        {
            var n = mod.Npcs.AddNew(edid);
            n.Race.SetTo(r);
            Keys[edid] = n.FormKey;
            return n;
        }
        var hero = Add("HcWalkHero", race);
        hero.HeadParts.Add(hair);
        hero.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = OwnScript } } };
        Add("HcWalkBeast", beastRace);
        var beast2 = Add("HcWalkBeast2", race);
        var mid = Add("HcWalkMid", race);
        mid.Template.SetTo(hero);
        mid.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var templated = Add("HcWalkTemplated", race);
        templated.Template.SetTo(mid);
        templated.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var lvl = mod.LeveledNpcs.AddNew("HcWalkLvl");
        lvl.Entries = new();
        foreach (var m in new[] { hero, beast2 })
            lvl.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = m.ToLink<INpcSpawnGetter>() } });
        var leveled = Add("HcWalkLeveled", race);
        leveled.Template.SetTo(lvl);
        leveled.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var loopA = Add("HcWalkLoopA", race);
        var loopB = Add("HcWalkLoopB", race);
        loopA.Template.SetTo(loopB); loopA.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        loopB.Template.SetTo(loopA); loopB.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;

        var sge = mod.Quests.AddNew("HcWalkSge");
        sge.Flags = Quest.Flag.StartGameEnabled;
        Keys["HcWalkSge"] = sge.FormKey;
        var plain = mod.Quests.AddNew("HcWalkPlain");
        Keys["HcWalkPlain"] = plain.FormKey;

        mod.BeginWrite.ToPath(Path.Combine(pluginMod, Plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
           .NoCheckIfLowerRangeDisallowed().Write();

        Write(donor, HairNif, Mesh(new[] { DonorTex, BaseTex, MissingTex }, HairXml));
        Write(donor, HairXml, new byte[] { 1 });
        Write(donor, DonorTex, new byte[] { 1 });
        Write(baseMod, BaseTex, new byte[] { 1 });
        foreach (var npc in new[] { "HcWalkHero", "HcWalkBeast2" })
            foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
                Write(baseMod, FaceGenPath.For(Keys[npc], slot), new byte[] { 1 });
        Write(cape, CapeNif, Mesh(new[] { @"data\textures\hcwalk\cape.dds" }, CapeXml));
        Write(cape, CapeXml, new byte[] { 1 });
        Write(cape, @"textures\hcwalk\cape.dds", new byte[] { 1 });

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + Plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + Plugin + "\r\n");
        // The donor is listed but switched off: its files are reachable only by naming it.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+Cape\r\n+HcWalkPlugin\r\n+" + BaseMod + "\r\n-" + DonorMod + "\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=Skyrim - Meshes0.bsa\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    /// <summary>A one-shape SE mesh whose texture set holds <paramref name="slots"/> and whose root carries the SMP
    /// pointer <paramref name="smp"/>.</summary>
    public static byte[] Mesh(IReadOnlyList<string> slots, string? smp)
    {
        var f = new NifFile();
        f.Create(new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 }, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("WalkRoot"); root.Flags_ui = 0xE;
        var texSet = new BSShaderTextureSet { Textures = slots.Select(s => new NiString4(s, false)).ToList() };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        var shader = new BSLightingShaderProperty { Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK };
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(texSet));
        var shape = new BSTriShape { Name = new NiStringRef("WalkShape"), Flags_ui = 0xE, Scale = 1f };
        shape.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));
        root.Children.AddBlockRef(f.AddBlock(shape));
        if (smp is not null)
        {
            var sd = new NiStringExtraData { Name = new NiStringRef(NifLinks.SmpKey), StringData = new NiStringRef(smp) };
            root.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
            root.ExtraDataList.AddBlockRef(f.AddBlock(sd));
            root.NumExtraDataList = (uint)root.ExtraDataList.Count;
        }
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("authoring the walk mesh failed to save");
        return ms.ToArray();
    }

    static void Write(string modDir, string rel, byte[] bytes)
    {
        var p = Path.Combine(modDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

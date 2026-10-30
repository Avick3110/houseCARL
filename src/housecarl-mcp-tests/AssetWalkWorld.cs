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
    public const string OtherPlugin = "HcWalkOther.esp";
    public const string OverridePlugin = "HcWalkOverride.esp";
    public const string OverrideMod = "HcWalkOverrideMod";
    public const string OvHairNif = @"meshes\hcwalk\ovhair.nif";
    public const string OvHairTex = @"textures\hcwalk\ovhair.dds";

    /// <summary>Sixty textures only the donor ships: a place set whose array alone passes a small max_chars.</summary>
    public static readonly string[] ManyTex = Enumerable.Range(0, 60).Select(i => $@"textures\hcwalk\many\t{i:D2}.dds").ToArray();

    /// <summary>A key in the walk plugin that no record holds.</summary>
    public static readonly FormKey Missing = new(ModKey.FromFileName(Plugin), 0xABCDE);

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
        var overrideMod = Path.Combine(ModsDir, OverrideMod);
        foreach (var d in new[] { profile, pluginMod, donor, baseMod, cape, overrideMod, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
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
        LeveledNpc Lvl(string edid, params FormKey[] members)
        {
            var l = mod.LeveledNpcs.AddNew(edid);
            l.Entries = new();
            foreach (var m in members)
                l.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = 1, Count = 1, Reference = new FormLink<INpcSpawnGetter>(m) } });
            return l;
        }
        Npc Templated(string edid, FormKey template)
        {
            var n = Add(edid, race);
            n.Template.SetTo(template);
            n.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
            return n;
        }
        // A leveled template nesting another; one with an entry that resolves to nothing; a template that resolves to nothing.
        var inner = Lvl("HcWalkLvlInner", beast2.FormKey);
        Templated("HcWalkNested", Lvl("HcWalkLvlOuter", hero.FormKey, inner.FormKey).FormKey);
        Templated("HcWalkPartial", Lvl("HcWalkLvlBad", hero.FormKey, beast2.FormKey, Missing).FormKey);
        Templated("HcWalkDangling", Missing);
        Add("HcWalkTraitsAlone", race).Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var noRace = mod.Npcs.AddNew("HcWalkNoRace");
        noRace.Race.SetTo(Missing);
        Keys["HcWalkNoRace"] = noRace.FormKey;

        var loopA = Add("HcWalkLoopA", race);
        var loopB = Add("HcWalkLoopB", race);
        loopA.Template.SetTo(loopB); loopA.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        loopB.Template.SetTo(loopA); loopB.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;

        var sge = mod.Quests.AddNew("HcWalkSge");
        sge.Flags = Quest.Flag.StartGameEnabled;
        Keys["HcWalkSge"] = sge.FormKey;
        var plain = mod.Quests.AddNew("HcWalkPlain");
        Keys["HcWalkPlain"] = plain.FormKey;

        var other = new SkyrimMod(ModKey.FromFileName(OtherPlugin), SkyrimRelease.SkyrimSE);
        var otherVoice = other.VoiceTypes.AddNew("HcWalkOtherVoice");
        other.BeginWrite.ToPath(Path.Combine(pluginMod, OtherPlugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
             .NoCheckIfLowerRangeDisallowed().Write();
        mod.BeginWrite.ToPath(Path.Combine(pluginMod, Plugin)).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
           .NoCheckIfLowerRangeDisallowed().Write();

        // A disabled override donor: HcWalkBeast2 given a head part the override plugin defines, and a link into a third plugin.
        var ov = new SkyrimMod(ModKey.FromFileName(OverridePlugin), SkyrimRelease.SkyrimSE);
        var ovHair = ov.HeadParts.AddNew("HcWalkOvHair");
        ovHair.Model = new Model { File = OvHairNif };
        var ovNpc = beast2.DeepCopy();
        ovNpc.HeadParts.Add(ovHair);
        ovNpc.Voice.SetTo(otherVoice);
        ov.Npcs.Add(ovNpc);
        ov.BeginWrite.ToPath(Path.Combine(overrideMod, OverridePlugin)).WithLoadOrder(new ISkyrimModGetter[] { other, mod, ov })
          .NoCheckIfLowerRangeDisallowed().Write();
        Write(overrideMod, OvHairNif, Mesh(new[] { OvHairTex }, null));

        Write(donor, HairNif, Mesh(new[] { DonorTex, BaseTex, MissingTex }, HairXml));
        Write(donor, HairXml, new byte[] { 1 });
        Write(donor, DonorTex, new byte[] { 1 });
        Write(baseMod, BaseTex, new byte[] { 1 });
        foreach (var p in ManyTex) Write(donor, p, new byte[] { 1 });
        foreach (var npc in new[] { "HcWalkHero", "HcWalkBeast2", "HcWalkTraitsAlone" })
            foreach (var slot in new[] { FaceGenSlot.Mesh, FaceGenSlot.Tint })
                Write(baseMod, FaceGenPath.For(Keys[npc], slot), new byte[] { 1 });
        Write(cape, CapeNif, Mesh(new[] { @"data\textures\hcwalk\cape.dds" }, CapeXml));
        Write(cape, CapeXml, new byte[] { 1 });
        Write(cape, @"textures\hcwalk\cape.dds", new byte[] { 1 });

        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n" + OtherPlugin + "\r\n" + Plugin + "\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + OtherPlugin + "\r\n*" + Plugin + "\r\n");
        // The donors are listed but switched off: their files are reachable only by naming them.
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+Cape\r\n+HcWalkPlugin\r\n+" + BaseMod + "\r\n-" + DonorMod
                                                                + "\r\n-" + OverrideMod + "\r\n");
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

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The closure-copy universe the copy-service tests drive <c>LoadOrderService.CopyClosure</c> over, in its
/// own temp folder. Active: HcBase.esm, Dawnguard.esm, Follower.esp, Shadow.esp. On disk but switched off: Src.esp
/// (the donor), Extra.esp (a second source with its own ModKey), Ghost.esp (never active, never a named source).
/// Built fresh per test, because every copy writes a patch folder under mods.</summary>
public sealed class CopyServiceWorld : IDisposable
{
    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }

    /// <summary>Follower.esp's NPC: ACTIVE, carrying a WornArmor and a Keyword no donor has.</summary>
    public FormKey TargetNpc { get; }
    /// <summary>Src.esp's NPC: one head part (with a texture set and a model path) and one Src.esp faction.</summary>
    public FormKey SrcNpc { get; }
    /// <summary>Src.esp's NPC with a head part from Src.esp, one from Extra.esp, and a HeadTexture into Ghost.esp.</summary>
    public FormKey WideNpc { get; }
    /// <summary>Src.esp's NPC with a head part from Src.esp and one DEFINED by the active Shadow.esp.</summary>
    public FormKey R5Npc { get; }
    /// <summary>Shadow.esp's own NPC, whose head part Shadow.esp also defines.</summary>
    public FormKey ShadowNpc { get; }
    /// <summary>Dawnguard.esm's NPC, overridden by Shadow.esp to point at Shadow's head part.</summary>
    public FormKey DawnguardNpc { get; }

    public static readonly string[] HeadParts = { "HeadParts" };
    public static readonly WalkExclusion[] NoExclusions = Array.Empty<WalkExclusion>();
    public static readonly WalkExclusion[] StopHeadParts =
        { new WalkExclusion("HeadPart", ExclusionSeverity.Stop, "pruned for the test") };

    public CopyServiceWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-copy-service-" + Guid.NewGuid().ToString("N"));
        var inst = Path.Combine(Root, "inst");
        ModsDir = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { ModsDir, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseKey = new ModKey("HcBase", ModType.Master);
        var raceFk = new FormKey(baseKey, 0x800);
        var kwFk = new FormKey(baseKey, 0x801);
        var armorFk = new FormKey(baseKey, 0x802);
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        baseMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcRace" });
        baseMod.Keywords.Add(new Keyword(kwFk, SkyrimRelease.SkyrimSE) { EditorID = "HcKeyword" });
        baseMod.Armors.Add(new Armor(armorFk, SkyrimRelease.SkyrimSE) { EditorID = "HcArmor" });
        Write(baseMod, "BaseMod");

        var folKey = new ModKey("Follower", ModType.Plugin);
        TargetNpc = new FormKey(folKey, 0x800);
        var folMod = new SkyrimMod(folKey, SkyrimRelease.SkyrimSE);
        var tgt = new Npc(TargetNpc, SkyrimRelease.SkyrimSE) { EditorID = "TargetNpc" };
        tgt.Race.SetTo(raceFk);
        tgt.WornArmor.SetTo(armorFk);
        tgt.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kwFk) };
        folMod.Npcs.Add(tgt);
        Write(folMod, "FollowerMod", baseMod);

        var ghostKey = new ModKey("Ghost", ModType.Plugin);
        var ghostTexFk = new FormKey(ghostKey, 0x800);
        var ghostMod = new SkyrimMod(ghostKey, SkyrimRelease.SkyrimSE);
        ghostMod.TextureSets.Add(new TextureSet(ghostTexFk, SkyrimRelease.SkyrimSE) { EditorID = "GhostTex" });
        Write(ghostMod, "GhostMod", baseMod);

        var extraKey = new ModKey("Extra", ModType.Plugin);
        var extraHpFk = new FormKey(extraKey, 0x800);
        var extraMod = new SkyrimMod(extraKey, SkyrimRelease.SkyrimSE);
        var extraHp = new HeadPart(extraHpFk, SkyrimRelease.SkyrimSE) { EditorID = "ExtraBrow" };
        extraMod.HeadParts.Add(extraHp);
        Write(extraMod, "ExtraMod", baseMod);

        // Named Dawnguard.esm because Mutagen's base-master set is what the copy path consults.
        var dgKey = new ModKey("Dawnguard", ModType.Master);
        var dgHpFk = new FormKey(dgKey, 0x800);
        DawnguardNpc = new FormKey(dgKey, 0x801);
        var dgMod = new SkyrimMod(dgKey, SkyrimRelease.SkyrimSE);
        dgMod.HeadParts.Add(new HeadPart(dgHpFk, SkyrimRelease.SkyrimSE) { EditorID = "DgBrow" });
        var dgNpc = new Npc(DawnguardNpc, SkyrimRelease.SkyrimSE) { EditorID = "DgNpc" };
        dgNpc.Race.SetTo(raceFk);
        dgNpc.HeadParts.Add(dgHpFk);
        dgMod.Npcs.Add(dgNpc);
        Write(dgMod, "DgMod", baseMod);

        // Active, overriding Extra's head part (so only being BOUND internalizes it), defining its own head part and
        // NPC, and overriding the Dawnguard NPC onto its own head part.
        var shadowKey = new ModKey("Shadow", ModType.Plugin);
        var shadowMod = new SkyrimMod(shadowKey, SkyrimRelease.SkyrimSE);
        shadowMod.HeadParts.GetOrAddAsOverride(extraHp);
        var shadowHpFk = new FormKey(shadowKey, 0x800);
        shadowMod.HeadParts.Add(new HeadPart(shadowHpFk, SkyrimRelease.SkyrimSE) { EditorID = "ShadowBrow" });
        ShadowNpc = new FormKey(shadowKey, 0x801);
        var shadowNpc = new Npc(ShadowNpc, SkyrimRelease.SkyrimSE) { EditorID = "ShadowNpc" };
        shadowNpc.Race.SetTo(raceFk);
        shadowNpc.HeadParts.Add(shadowHpFk);
        shadowMod.Npcs.Add(shadowNpc);
        var dgOverride = shadowMod.Npcs.GetOrAddAsOverride(dgNpc);
        dgOverride.HeadParts.Clear();
        dgOverride.HeadParts.Add(shadowHpFk);
        Write(shadowMod, "ShadowMod", baseMod, extraMod, dgMod);

        var srcKey = new ModKey("Src", ModType.Plugin);
        var txstFk = new FormKey(srcKey, 0x800);
        var hpFk = new FormKey(srcKey, 0x801);
        var factionFk = new FormKey(srcKey, 0x802);
        SrcNpc = new FormKey(srcKey, 0x803);
        WideNpc = new FormKey(srcKey, 0x804);
        R5Npc = new FormKey(srcKey, 0x805);
        var srcMod = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
        srcMod.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
        var srcHp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
        srcHp.TextureSet.SetTo(txstFk);
        srcHp.Model = new Model { File = @"actors\character\hair\srchair.nif" };
        srcMod.HeadParts.Add(srcHp);
        srcMod.Factions.Add(new Faction(factionFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcFaction" });
        srcMod.TextureSets.Add(new TextureSet(ghostTexFk, SkyrimRelease.SkyrimSE) { EditorID = "GhostTex" });
        var srcNpc = new Npc(SrcNpc, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
        srcNpc.Race.SetTo(raceFk);
        srcNpc.HeadParts.Add(hpFk);
        srcNpc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(factionFk), Rank = 0 });
        srcMod.Npcs.Add(srcNpc);
        var r5Npc = new Npc(R5Npc, SkyrimRelease.SkyrimSE) { EditorID = "R5Npc" };
        r5Npc.Race.SetTo(raceFk);
        r5Npc.HeadParts.Add(hpFk);
        r5Npc.HeadParts.Add(shadowHpFk);
        srcMod.Npcs.Add(r5Npc);
        var wideNpc = new Npc(WideNpc, SkyrimRelease.SkyrimSE) { EditorID = "WideNpc" };
        wideNpc.Race.SetTo(raceFk);
        wideNpc.HeadParts.Add(hpFk);
        wideNpc.HeadParts.Add(extraHpFk);
        wideNpc.HeadTexture.SetTo(ghostTexFk);
        srcMod.Npcs.Add(wideNpc);
        Write(srcMod, "SrcMod", baseMod, extraMod, ghostMod, shadowMod);

        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            "+ShadowMod\r\n+DgMod\r\n+FollowerMod\r\n+BaseMod\r\n-SrcMod\r\n-ExtraMod\r\n-GhostMod\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\nDawnguard.esm\r\nFollower.esp\r\nShadow.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n*Dawnguard.esm\r\n*Follower.esp\r\n*Shadow.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    /// <summary>The full path of Follower.esp's own copy, the one the active order loads.</summary>
    public string FollowerPath => Path.Combine(ModsDir, "FollowerMod", "Follower.esp");

    public ClosureCopyOutcome Copy(FormKey from, string[] sources, string[] seeds, WalkExclusion[] exclusions,
                                   FormKey? target, string? newEditorId, string? patch, string? into)
        => Svc.CopyClosure(from, sources, seeds, exclusions, target, newEditorId, patch, into);

    public bool AnyFolderNamed(string fragment) => Directory.EnumerateDirectories(ModsDir, "*" + fragment + "*").Any();

    /// <summary>Opens a written patch and hands it to <paramref name="read"/>, disposing the overlay after.</summary>
    public static T ReadBack<T>(string path, Func<ISkyrimModGetter, T> read)
    {
        var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        try { return read(m); }
        finally { (m as IDisposable)?.Dispose(); }
    }

    static string Detail(ClosureCopyOutcome o) =>
        o.EngineError ?? o.WalkRefusal?.Detail ?? o.CopyRefusal?.Detail ?? "";

    /// <summary>Asserts a success, naming the refusal when it is not one.</summary>
    public static void Succeeded(ClosureCopyOutcome o) => Xunit.Assert.True(o.Success, "refused: " + Detail(o));

    void Write(SkyrimMod mod, string folder, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(ModsDir, folder);
        Directory.CreateDirectory(dir);
        mod.BeginWrite.ToPath(Path.Combine(dir, mod.ModKey.FileName)).WithLoadOrder(masters).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The smallest copy universe that tells an exclusion severity apart by result: HcBase.esm (a race) and
/// Src.esp, whose NPC seeds HeadParts → SrcHair → SrcTex. Both reached records live in Src.esp, so both are in scope;
/// the NPC's race lives in the base master, so the clone lane's required-link refusal never fires. The NPC's Factions
/// is left empty on purpose. Each call writes its own named patch, so one instance serves a test class.</summary>
public sealed class CopyParserWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    /// <summary>Src.esp's NPC, as the wire spells it.</summary>
    public string From { get; }

    public CopyParserWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-copy-parser-" + Guid.NewGuid().ToString("N"));
        var inst = Path.Combine(Root, "inst");
        var mods = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { mods, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseKey = new ModKey("HcBase", ModType.Master);
        var raceFk = new FormKey(baseKey, 0x800);
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        baseMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcRace" });
        Write(mods, "BaseMod", baseMod);

        var srcKey = new ModKey("Src", ModType.Plugin);
        var txstFk = new FormKey(srcKey, 0x800);
        var hpFk = new FormKey(srcKey, 0x801);
        var npcFk = new FormKey(srcKey, 0x802);
        var srcMod = new SkyrimMod(srcKey, SkyrimRelease.SkyrimSE);
        srcMod.TextureSets.Add(new TextureSet(txstFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
        var hp = new HeadPart(hpFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
        hp.TextureSet.SetTo(txstFk);
        srcMod.HeadParts.Add(hp);
        var npc = new Npc(npcFk, SkyrimRelease.SkyrimSE) { EditorID = "SrcNpc" };
        npc.Race.SetTo(raceFk);
        npc.HeadParts.Add(hpFk);
        srcMod.Npcs.Add(npc);
        Write(mods, "SrcMod", srcMod, baseMod);

        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+SrcMod\r\n+BaseMod\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\nSrc.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n*Src.esp\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        From = npcFk.ToString();
        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    static void Write(string mods, string folder, SkyrimMod m, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(mods, folder);
        Directory.CreateDirectory(dir);
        m.BeginWrite.ToPath(Path.Combine(dir, m.ModKey.FileName.String)).WithLoadOrder(masters).Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

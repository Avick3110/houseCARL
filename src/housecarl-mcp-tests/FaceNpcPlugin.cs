using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>A plugin that defines plain NPCs at given local ids, each on one FaceGenHead race, so the face door names
/// their own pair: what a world needs once formids=, npc= and place read the NPC record.</summary>
public static class FaceNpcPlugin
{
    /// <summary>The local id of the race every NPC here uses, past any id a world names.</summary>
    public const uint RaceId = 0xFFF00;

    /// <summary>Write <paramref name="fileName"/> into <paramref name="dir"/>, defining an NPC at each of <paramref name="npcIds"/>.</summary>
    public static string Write(string dir, string fileName, params uint[] npcIds)
    {
        var key = ModKey.FromFileName(fileName);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        if (key.Type == ModType.Master) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        var race = new Race(new FormKey(key, RaceId), SkyrimRelease.SkyrimSE) { EditorID = "HcFaceRace", Flags = Race.Flag.FaceGenHead };
        mod.Races.Add(race);
        foreach (var id in npcIds)
        {
            var npc = new Npc(new FormKey(key, id), SkyrimRelease.SkyrimSE) { EditorID = $"HcFaceNpc{id:X6}" };
            npc.Race.SetTo(race);
            mod.Npcs.Add(npc);
        }
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        mod.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        return path;
    }
}

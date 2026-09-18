using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>SEQ-file builder — the <c>Data\SEQ\&lt;plugin&gt;.seq</c> the engine reads to actually START a plugin's
/// Start-Game-Enabled quests; the SGE flag alone does nothing without it. Format: a flat array of 4-byte
/// little-endian plugin-local, master-relative on-disk FormIDs, no header or footer, one per SGE quest — the
/// master-INDEX encoding Mutagen writes into the record header, never the runtime 0xFE light-space address.</summary>
public static class SeqFile
{
    /// <summary>The plugin-local, master-relative on-disk FormID for <paramref name="fk"/> given the defining plugin's
    /// ORDERED master list: high byte is the master's index in that list, or the master COUNT for the plugin's own
    /// records.</summary>
    public static uint OnDiskFormId(FormKey fk, IReadOnlyList<ModKey> masters)
    {
        int slot = -1;
        for (int i = 0; i < masters.Count; i++)
            if (masters[i] == fk.ModKey) { slot = i; break; }
        if (slot < 0) slot = masters.Count;                 // own/new record: its ModKey is the plugin's, never in its masters
        return ((uint)slot << 24) | (fk.ID & FormIdRange.ObjectIdMask);
    }

    /// <summary>Serialize SEQ FormIDs to the on-disk byte layout: each as a 4-byte little-endian uint, concatenated,
    /// with no header or footer. An empty sequence yields zero bytes.</summary>
    public static byte[] Serialize(IReadOnlyList<uint> formIds)
    {
        var bytes = new byte[formIds.Count * 4];
        for (int i = 0; i < formIds.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), formIds[i]);
        return bytes;
    }

    /// <summary>One Start-Game-Enabled quest destined for the .seq: its identity, its EditorID for the report, and the
    /// on-disk FormID actually written into the file.</summary>
    public readonly record struct SeqQuest(FormKey FormKey, string? EditorId, uint OnDiskFormId);

    /// <summary>The built .seq: the bytes to write, the SGE quests it covers, and the defining plugin's filename, which
    /// the .seq is named after.</summary>
    public readonly record struct SeqBuild(byte[] Bytes, IReadOnlyList<SeqQuest> Quests, string PluginFileName);

    /// <summary>Open <paramref name="pluginPath"/> as a binary overlay and build the .seq bytes from every
    /// Start-Game-Enabled quest it contains, deleted quests excluded, in the plugin's own record order. Read-only,
    /// holds no handle past the <c>using</c>, and THROWS on an unreadable plugin rather than yielding an empty
    /// .seq.</summary>
    public static SeqBuild Build(string pluginPath)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath));
        var masters = mod.ModHeader.MasterReferences.Select(m => m.Master).ToList();
        var quests = new List<SeqQuest>();
        foreach (var q in mod.Quests)
        {
            if (q.IsDeleted) continue;                                       // a deleted quest never starts → never in the .seq
            if (!q.Flags.HasFlag(Quest.Flag.StartGameEnabled)) continue;    // SGE flag is the sole inclusion test (CK/xEdit rule)
            quests.Add(new SeqQuest(q.FormKey, q.EditorID, OnDiskFormId(q.FormKey, masters)));
        }
        var bytes = Serialize(quests.Select(x => x.OnDiskFormId).ToList());
        return new SeqBuild(bytes, quests, mod.ModKey.FileName);
    }

    /// <summary>The on-disk FormID for <paramref name="fk"/> as written into <paramref name="pluginPath"/>, read off
    /// that plugin's own ordered master list. Read-only; THROWS on an unreadable plugin.</summary>
    public static uint OnDiskFormIdFromPlugin(string pluginPath, FormKey fk)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(pluginPath));
        var masters = mod.ModHeader.MasterReferences.Select(m => m.Master).ToList();
        return OnDiskFormId(fk, masters);
    }

    /// <summary>Whether a .seq's raw bytes contain <paramref name="onDiskFormId"/>. A trailing partial chunk is
    /// ignored, never guessed.</summary>
    public static bool SeqContains(ReadOnlySpan<byte> seqBytes, uint onDiskFormId)
    {
        for (int i = 0; i + 4 <= seqBytes.Length; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(seqBytes.Slice(i)) == onDiskFormId) return true;
        return false;
    }

    /// <summary>Post-write staleness check: every Start-Game-Enabled quest in <paramref name="pluginPath"/> whose
    /// current on-disk FormID the existing <c>.seq</c> does not list, through the same <see cref="Build"/> and
    /// <see cref="SeqContains"/> a regen uses. Read-only; never mutates the <c>.seq</c>.</summary>
    public static IReadOnlyList<SeqQuest> UncoveredSgeQuests(string pluginPath, ReadOnlySpan<byte> existingSeqBytes)
    {
        var build = Build(pluginPath);                       // SGE quests + their current on-disk FormIDs, off the written plugin
        var uncovered = new List<SeqQuest>();
        foreach (var q in build.Quests)
            if (!SeqContains(existingSeqBytes, q.OnDiskFormId)) uncovered.Add(q);
        return uncovered;
    }
}

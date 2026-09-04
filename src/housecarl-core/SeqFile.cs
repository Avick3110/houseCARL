using System.Buffers.Binary;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// SEQ-file builder — the start-game-enabled-quest "sequence" file (<c>Data\SEQ\&lt;plugin&gt;.seq</c>) the engine reads to
/// actually START a plugin's Start-Game-Enabled quests. Ticking the SGE flag alone does NOTHING: without the .seq the quest
/// — and any dialogue or world change gated on it — silently never runs. The CK writes this file on save; xEdit's
/// "Create SEQ file" does the same; this is houseCARL's data-layer equivalent.
///
/// Format: a FLAT array of 4-byte LITTLE-ENDIAN FormIDs, no header or footer, one per SGE quest. Each FormID is the
/// plugin-LOCAL, master-relative ON-DISK form: high byte = the quest's slot in the plugin's master list (a plugin's
/// OWN/new records sit at the slot AFTER its last master, i.e. high byte = master count), low 3 bytes = the object id.
/// That is the SAME master-INDEX encoding Mutagen writes into the record header on disk — NEVER the runtime 0xFE
/// light-space / load-order address. So the file is LOAD-ORDER-INDEPENDENT: computable wholly at author time with no
/// runtime-FormID bridge, and it ships with the mod. houseCARL writes the plugin and its .seq together, so the encoding
/// is never stale — the one way real .seq files DO go wrong (a master added/removed, or an ESL compaction/merge, after
/// the .seq was generated, which shifts the slot) cannot happen when both are emitted in the same act.
/// </summary>
public static class SeqFile
{
    /// <summary>The plugin-local, master-relative ON-DISK FormID for <paramref name="fk"/> given the defining plugin's
    /// ORDERED master list (the order they appear in the file header). The high byte is the master's index in that list,
    /// or the master COUNT when the FormKey belongs to the plugin ITSELF (an own/new record — the plugin's own ModKey is
    /// never among its own masters, so it sits at the slot after the last master). This is exactly the value Mutagen
    /// writes into the record header on disk; it is NOT the runtime 0xFE light-space / load-order address (those are
    /// computed by the engine at load time from each master's header flag + position and are never stored on disk).</summary>
    public static uint OnDiskFormId(FormKey fk, IReadOnlyList<ModKey> masters)
    {
        int slot = -1;
        for (int i = 0; i < masters.Count; i++)
            if (masters[i] == fk.ModKey) { slot = i; break; }
        if (slot < 0) slot = masters.Count;                 // own/new record: its ModKey is the plugin's, never in its masters
        return ((uint)slot << 24) | (fk.ID & FormIdRange.ObjectIdMask);
    }

    /// <summary>Serialize SEQ FormIDs to the on-disk byte layout: each as a 4-byte LITTLE-ENDIAN uint, concatenated, with
    /// NO header or footer. An empty sequence yields zero bytes (the caller decides whether an empty .seq is worth
    /// writing — for SEQ the answer is no: a plugin with no SGE quests needs no .seq at all).</summary>
    public static byte[] Serialize(IReadOnlyList<uint> formIds)
    {
        var bytes = new byte[formIds.Count * 4];
        for (int i = 0; i < formIds.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), formIds[i]);
        return bytes;
    }

    /// <summary>One Start-Game-Enabled quest destined for the .seq: its identity (<paramref name="FormKey"/>), its EditorID
    /// for the human-readable report, and the <paramref name="OnDiskFormId"/> actually written into the file.</summary>
    public readonly record struct SeqQuest(FormKey FormKey, string? EditorId, uint OnDiskFormId);

    /// <summary>The built .seq: the bytes to write, the SGE quests it covers (for the report), and the defining plugin's
    /// filename (e.g. <c>MyMod.esp</c>) — the .seq is named after it (<c>MyMod.seq</c>).</summary>
    public readonly record struct SeqBuild(byte[] Bytes, IReadOnlyList<SeqQuest> Quests, string PluginFileName);

    /// <summary>Open <paramref name="pluginPath"/> as a binary overlay, find every Start-Game-Enabled quest it contains
    /// (DELETED quests excluded — a removed record never starts, the same skip the dialogue validator applies to deleted
    /// lines), and build the .seq bytes from their on-disk FormIDs in the plugin's own record order. Read-only; holds NO
    /// handle past the <c>using</c> (the at-rest discipline every houseCARL overlay open follows). THROWS on an unreadable
    /// plugin — the caller surfaces it; never a silent empty .seq for a plugin that didn't open.</summary>
    public static SeqBuild Build(string pluginPath)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
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

    /// <summary>The plugin-local, master-relative ON-DISK FormID for <paramref name="fk"/> as written into
    /// <paramref name="pluginPath"/> — opens the plugin read-only, reads its ordered master list, and returns
    /// <see cref="OnDiskFormId"/>. Read-only; holds no handle past the <c>using</c>. THROWS on an unreadable plugin
    /// (the caller surfaces it). The SEQ staleness lint uses it to know which 4-byte value a .seq must contain for a
    /// given quest, without re-deriving the master-index encoding.</summary>
    public static uint OnDiskFormIdFromPlugin(string pluginPath, FormKey fk)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        var masters = mod.ModHeader.MasterReferences.Select(m => m.Master).ToList();
        return OnDiskFormId(fk, masters);
    }

    /// <summary>Whether a .seq's raw bytes (a flat array of 4-byte little-endian on-disk FormIDs — the layout
    /// <see cref="Serialize"/> writes) contain <paramref name="onDiskFormId"/> — the membership check the staleness
    /// lint runs against an EXISTING .seq. A trailing partial chunk (a truncated/malformed .seq) is ignored, never
    /// guessed.</summary>
    public static bool SeqContains(ReadOnlySpan<byte> seqBytes, uint onDiskFormId)
    {
        for (int i = 0; i + 4 <= seqBytes.Length; i += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(seqBytes.Slice(i)) == onDiskFormId) return true;
        return false;
    }

    /// <summary>Post-write staleness check (the in-place SEQ auto-flag): given a freshly-written <paramref name="pluginPath"/>
    /// and the bytes of an EXISTING <c>.seq</c> resolved for it, return every Start-Game-Enabled quest whose CURRENT on-disk
    /// FormID is NOT listed in that <c>.seq</c> — the quests the stale <c>.seq</c> would silently fail to start on a fresh
    /// save. EMPTY ⇒ the <c>.seq</c> is consistent with the plugin (or the plugin has no SGE quests). This is the exact
    /// condition an in-place master-prune creates: dropping a master shifts every own record's master-index slot, so each
    /// SGE quest's on-disk FormID moves and the shipped <c>.seq</c> no longer lists it (e.g. Update.esm pruned →
    /// 0x02000806 becomes 0x01000806). Reuses <see cref="Build"/> (the same SGE enumeration + on-disk-FormID encoding the
    /// author-time <c>.seq</c> write uses) and <see cref="SeqContains"/>, so the write-time flag and a <c>write_seq</c>
    /// regen can never disagree about which quests a <c>.seq</c> must list. Read-only; opens + disposes the overlay.
    /// The CALLER decides to warn — this never mutates the <c>.seq</c>.</summary>
    public static IReadOnlyList<SeqQuest> UncoveredSgeQuests(string pluginPath, ReadOnlySpan<byte> existingSeqBytes)
    {
        var build = Build(pluginPath);                       // SGE quests + their current on-disk FormIDs, off the written plugin
        var uncovered = new List<SeqQuest>();
        foreach (var q in build.Quests)
            if (!SeqContains(existingSeqBytes, q.OnDiskFormId)) uncovered.Add(q);
        return uncovered;
    }
}

using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Three synthetic patches for the <c>SeqFile</c> encoding tests (from the <c>seq-write-guard</c> probe): A has one
/// master, an override that newly flags SGE, an own SGE quest, a RunOnce-only quest and a deleted SGE quest; B has two
/// masters and an own SGE quest; E is an ESL-flagged patch with one master and an own SGE quest.</summary>
public sealed class SeqFileEncodingFixture : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-seq-file-encoding-tests-" + Guid.NewGuid().ToString("N"));

    public string APatch { get; }
    public FormKey AMasterQuest { get; }
    public FormKey AOwn { get; }
    public FormKey APlain { get; }
    public FormKey ADeleted { get; }
    public IReadOnlyList<ModKey> AMasters { get; }

    public string BPatch { get; }
    public FormKey BOwn { get; }
    public IReadOnlyList<ModKey> BMasters { get; }

    public string EPatch { get; }
    public FormKey EOwn { get; }

    public SeqFileEncodingFixture()
    {
        Directory.CreateDirectory(_root);

        var aMaster = Path.Combine(_root, "HcSeqAMaster.esm");
        AMasterQuest = WriteMasterWithQuest(aMaster, "HcSeqAMaster", "HcSeqAMasterQuest");
        APatch = Path.Combine(_root, "HcSeqAPatch.esp");
        using (var m = SkyrimMod.CreateFromBinaryOverlay(aMaster, SkyrimRelease.SkyrimSE))
        {
            var p = NewPatch("HcSeqAPatch");
            p.Quests.GetOrAddAsOverride(m.Quests.First(q => q.FormKey == AMasterQuest)).Flags = Quest.Flag.StartGameEnabled;
            AOwn = AddQuest(p, "HcSeqAOwn", Quest.Flag.StartGameEnabled);
            APlain = AddQuest(p, "HcSeqAPlain", Quest.Flag.RunOnce);
            var del = p.Quests.AddNew(); del.EditorID = "HcSeqADel"; del.Flags = Quest.Flag.StartGameEnabled; del.IsDeleted = true;
            ADeleted = del.FormKey;
            p.BeginWrite.ToPath(APatch).WithLoadOrder(new[] { (ISkyrimModGetter)m }).NoNextFormIDProcessing().Write();
        }
        AMasters = MastersOf(APatch);

        var b1 = Path.Combine(_root, "HcSeqBM1.esm");
        var b2 = Path.Combine(_root, "HcSeqBM2.esm");
        var b1Q = WriteMasterWithQuest(b1, "HcSeqBM1", "B1Q");
        var b2Q = WriteMasterWithQuest(b2, "HcSeqBM2", "B2Q");
        BPatch = Path.Combine(_root, "HcSeqBPatch.esp");
        using (var m1 = SkyrimMod.CreateFromBinaryOverlay(b1, SkyrimRelease.SkyrimSE))
        using (var m2 = SkyrimMod.CreateFromBinaryOverlay(b2, SkyrimRelease.SkyrimSE))
        {
            var p = NewPatch("HcSeqBPatch");
            p.Quests.GetOrAddAsOverride(m1.Quests.First(q => q.FormKey == b1Q));
            p.Quests.GetOrAddAsOverride(m2.Quests.First(q => q.FormKey == b2Q));
            BOwn = AddQuest(p, "BOwn", Quest.Flag.StartGameEnabled);
            p.BeginWrite.ToPath(BPatch).WithLoadOrder(new[] { (ISkyrimModGetter)m1, m2 }).NoNextFormIDProcessing().Write();
        }
        BMasters = MastersOf(BPatch);

        var eMaster = Path.Combine(_root, "HcSeqEMaster.esm");
        var eQ = WriteMasterWithQuest(eMaster, "HcSeqEMaster", "EMQ");
        EPatch = Path.Combine(_root, "HcSeqEPatch.esp");
        using (var m = SkyrimMod.CreateFromBinaryOverlay(eMaster, SkyrimRelease.SkyrimSE))
        {
            var p = NewPatch("HcSeqEPatch");
            p.IsSmallMaster = true;
            p.Quests.GetOrAddAsOverride(m.Quests.First(q => q.FormKey == eQ));
            EOwn = AddQuest(p, "EOwn", Quest.Flag.StartGameEnabled);
            p.BeginWrite.ToPath(EPatch).WithLoadOrder(new[] { (ISkyrimModGetter)m }).NoNextFormIDProcessing().Write();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    static SkyrimMod NewPatch(string name)
    {
        var p = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        if (p.ModHeader.Stats.NextFormID < 0x800) p.ModHeader.Stats.NextFormID = 0x800;
        return p;
    }

    static FormKey AddQuest(SkyrimMod p, string edid, Quest.Flag flags)
    {
        var q = p.Quests.AddNew(); q.EditorID = edid; q.Flags = flags;
        return q.FormKey;
    }

    static FormKey WriteMasterWithQuest(string path, string name, string edid)
    {
        var m = new SkyrimMod(new ModKey(name, ModType.Master), SkyrimRelease.SkyrimSE);
        var fk = AddQuest(m, edid, Quest.Flag.RunOnce);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();
        return fk;
    }

    static List<ModKey> MastersOf(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.ModHeader.MasterReferences.Select(mr => mr.Master).ToList();
    }

    /// <summary>Every QUST record's FormID as it sits in the file's record headers, read from the raw bytes rather than
    /// through Mutagen, so the encoding is checked against what was written.</summary>
    public static HashSet<uint> RawQuestFormIds(string path)
    {
        var buf = File.ReadAllBytes(path);
        var found = new HashSet<uint>();
        if (buf.Length >= 24) Scan(buf, 24 + (int)BitConverter.ToUInt32(buf, 4), buf.Length, found);
        return found;
    }

    static void Scan(byte[] buf, int start, int end, HashSet<uint> found)
    {
        int p = start;
        while (p + 24 <= end)
        {
            var sig = System.Text.Encoding.ASCII.GetString(buf, p, 4);
            uint size = BitConverter.ToUInt32(buf, p + 4);
            long next;
            if (sig == "GRUP") { next = (long)p + size; Scan(buf, p + 24, (int)Math.Min(next, end), found); }
            else { if (sig == "QUST") found.Add(BitConverter.ToUInt32(buf, p + 12)); next = (long)p + 24 + size; }
            if (next <= p) break;
            p = (int)Math.Min(next, (long)end);
        }
    }
}

/// <summary>
/// <c>SeqFile</c>'s encoding (migrated from the <c>seq-write-guard</c> probe): a .seq lists each start-game-enabled quest
/// as its plugin-local, master-index on-disk FormID, little-endian, never the runtime 0xFE address.
/// </summary>
[Trait("tier", "unit")]
public sealed class SeqFileEncodingTests : IClassFixture<SeqFileEncodingFixture>
{
    readonly SeqFileEncodingFixture F;
    public SeqFileEncodingTests(SeqFileEncodingFixture f) => F = f;

    static byte HighByte(uint formId) => (byte)(formId >> 24);

    // Probe SERIALIZE-LE: "Serialize([0x0500AA02]) == bytes 02 AA 00 05 (4-byte little-endian, no header)".
    [Fact]
    public void SerializeIsFourByteLittleEndianWithNoHeader()
    {
        Assert.Equal(new byte[] { 0x02, 0xAA, 0x00, 0x05 }, SeqFile.Serialize(new uint[] { 0x0500AA02u }));
    }

    // Probe ENCODE-OWN: "own record at master COUNT" (0x01000800 with one master, not a constant 0).
    [Fact]
    public void AnOwnRecordSitsAtTheMasterCount()
    {
        var master = new ModKey("HcSeqEncMaster", ModType.Master);
        var own = new FormKey(new ModKey("HcSeqEnc", ModType.Plugin), 0x000800);
        Assert.Equal(0x01000800u, SeqFile.OnDiskFormId(own, new[] { master }));
    }

    // Probe ENCODE-OVERRIDE: "override at master index" (0x0000AA02, the overridden master's index).
    [Fact]
    public void AnOverrideSitsAtItsMastersIndex()
    {
        var master = new ModKey("HcSeqEncMaster", ModType.Master);
        Assert.Equal(0x0000AA02u, SeqFile.OnDiskFormId(new FormKey(master, 0x00AA02), new[] { master }));
    }

    // Probe SGE-ONLY: "own SGE quest included".
    [Fact]
    public void AnOwnStartGameEnabledQuestIsListed()
    {
        Assert.Contains(SeqFile.Build(F.APatch).Quests, q => q.FormKey == F.AOwn);
    }

    // Probe SGE-ONLY: "RunOnce-only quest EXCLUDED".
    [Fact]
    public void ARunOnceOnlyQuestIsNotListed()
    {
        Assert.DoesNotContain(SeqFile.Build(F.APatch).Quests, q => q.FormKey == F.APlain);
    }

    // Probe OVERRIDE-SGE: "override that newly flags SGE included"; and "OVERRIDE high byte == overridden master index (never 0xFE)".
    [Fact]
    public void AnOverrideThatNewlyFlagsSgeIsListedAtItsMastersIndex()
    {
        var ov = Assert.Single(SeqFile.Build(F.APatch).Quests, q => q.FormKey == F.AMasterQuest);
        Assert.Equal(F.AMasters.ToList().IndexOf(F.AMasterQuest.ModKey), HighByte(ov.OnDiskFormId));
    }

    // Probe DELETED-SKIP: "deleted SGE quest EXCLUDED".
    [Fact]
    public void ADeletedSgeQuestIsNotListed()
    {
        Assert.DoesNotContain(SeqFile.Build(F.APatch).Quests, q => q.FormKey == F.ADeleted);
    }

    // Probe HIGH-BYTE-COUNT: "own quest high byte == master count" (1 master), and "(2 masters) own high byte == 2 — proves it tracks the count".
    [Fact]
    public void AnOwnQuestsHighByteTracksTheMasterCount()
    {
        var a = Assert.Single(SeqFile.Build(F.APatch).Quests, q => q.FormKey == F.AOwn);
        var b = Assert.Single(SeqFile.Build(F.BPatch).Quests, q => q.FormKey == F.BOwn);
        Assert.Single(F.AMasters);
        Assert.Equal(2, F.BMasters.Count);
        Assert.Equal(1, HighByte(a.OnDiskFormId));
        Assert.Equal(2, HighByte(b.OnDiskFormId));
    }

    // Probe ESL-NEVER-FE: "light patch's SGE quest encodes master-index (own high 0x01, never 0xFE)".
    [Fact]
    public void AnEslPatchsOwnQuestUsesTheMasterIndexNotFe()
    {
        var built = SeqFile.Build(F.EPatch);
        Assert.Equal(1, HighByte(Assert.Single(built.Quests, q => q.FormKey == F.EOwn).OnDiskFormId));
        Assert.DoesNotContain(built.Quests, q => HighByte(q.OnDiskFormId) == 0xFE);
    }

    // Probe ON-DISK-MATCH: "built FormIDs == actual on-disk record FormIDs (master-index, never FE)", on the 1-master
    // patch and (with ESL-NEVER-FE) on the light patch.
    [Theory]
    [InlineData("A")]
    [InlineData("E")]
    public void EveryListedFormIdIsTheOneWrittenInTheRecordHeader(string which)
    {
        var path = which == "A" ? F.APatch : F.EPatch;
        var raw = SeqFileEncodingFixture.RawQuestFormIds(path);
        var built = SeqFile.Build(path);
        Assert.NotEmpty(built.Quests);
        Assert.All(built.Quests, q => Assert.Contains(q.OnDiskFormId, raw));
    }
}

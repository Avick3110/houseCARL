using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>RemapEngine.RenumberModInto</c>'s nested compact, end to end over files on disk. HcW2Donor.esp carries
/// one of every nesting shape at 0xA00-0xA08: a weapon, a FormList naming it, an interior cell whose placed object's
/// Base is the weapon, a worldspace with an exterior cell and placed object, and a topic with an INFO. HcW2External.esp
/// names the weapon. The constructor renumbers the donor into the light window and writes P'; each test reads one
/// claim back. Each test gets its own temp folder.</summary>
[Trait("tier", "integration")]
public sealed class RemapNestedCompactTests : IDisposable
{
    static readonly ModKey DonorKey = new("HcW2Donor", ModType.Plugin);
    static readonly ModKey ExtKey = new("HcW2External", ModType.Plugin);
    static readonly FormKey WaOld = new(DonorKey, 0xA00);
    static readonly FormKey FlOld = new(DonorKey, 0xA01);
    static readonly FormKey IcOld = new(DonorKey, 0xA02);
    static readonly FormKey IpOld = new(DonorKey, 0xA03);
    static readonly FormKey WsOld = new(DonorKey, 0xA04);
    static readonly FormKey EcOld = new(DonorKey, 0xA05);
    static readonly FormKey EpOld = new(DonorKey, 0xA06);
    static readonly FormKey DtOld = new(DonorKey, 0xA07);
    static readonly FormKey InOld = new(DonorKey, 0xA08);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-remap-nested-" + Guid.NewGuid().ToString("N"));
    readonly string _donorPath;
    readonly string _extPath;
    readonly string _pPrimePath;
    readonly RemapEngine.RemapPlan _plan;
    readonly RemapEngine.RenumberResult _ren;

    public RemapNestedCompactTests()
    {
        Directory.CreateDirectory(_dir);
        _donorPath = Path.Combine(_dir, DonorKey.FileName.String);
        _extPath = Path.Combine(_dir, ExtKey.FileName.String);
        _pPrimePath = Path.Combine(_dir, "pprime", DonorKey.FileName.String);

        var d = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE);
        d.Weapons.Add(new Weapon(WaOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Weap", BasicStats = new WeaponBasicStats { Damage = 10 } });
        var fl = new FormList(FlOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2List" };
        fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(WaOld));
        d.FormLists.Add(fl);
        var ic = new Cell(IcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2IntCell", Flags = Cell.Flag.IsInteriorCell };
        var ip = new PlacedObject(IpOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2IntRef" };
        ip.Base.SetTo(WaOld);
        ic.Temporary.Add(ip);
        FileInterior(d, ic);
        var ws = new Worldspace(WsOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2WS" };
        var ec = new Cell(EcOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtCell", Grid = new CellGrid { Point = new P2Int(3, -4) } };
        ec.Temporary.Add(new PlacedObject(EpOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtRef" });
        FileExterior(ws, ec, 3, -4);
        d.Worldspaces.Add(ws);
        var dt = new DialogTopic(DtOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Topic" };
        dt.Responses.Add(new DialogResponses(InOld, SkyrimRelease.SkyrimSE) { EditorID = "HcW2Info" });
        d.DialogTopics.Add(dt);
        d.ModHeader.Stats.NextFormID = 0xA09;
        d.BeginWrite.ToPath(_donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        using (var donorOv = SkyrimMod.CreateFromBinaryOverlay(_donorPath, SkyrimRelease.SkyrimSE))
        {
            var e = new SkyrimMod(ExtKey, SkyrimRelease.SkyrimSE);
            var exl = new FormList(new FormKey(ExtKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcW2ExtList" };
            exl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(WaOld));
            e.FormLists.Add(exl);
            e.ModHeader.Stats.NextFormID = 0x801;
            e.BeginWrite.ToPath(_extPath).WithLoadOrder(new[] { donorOv }).NoNextFormIDProcessing().Write();

            var srcKeys = donorOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == DonorKey).Select(r => r.FormKey).ToList();
            _plan = RemapEngine.BuildSequentialRemap(srcKeys, DonorKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
            var pPrime = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
            _ren = RemapEngine.RenumberModInto(pPrime, donorOv, _plan.Dict);
            pPrime.ModHeader.Stats.NextFormID = (uint)(RemapEngine.EslFloor + _plan.Dict.Count);
            Directory.CreateDirectory(Path.GetDirectoryName(_pPrimePath)!);
            if (_ren.Success) WriteEngine.WriteInPlace(pPrime, Array.Empty<ISkyrimModGetter>(), _pPrimePath, dataDir: null);
        }
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    FormKey New(FormKey old) => _plan.Dict[old];

    T ReadBack<T>(Func<ISkyrimModGetter, T> read)
    {
        Assert.True(_plan.Success, _plan.Error);
        Assert.True(_ren.Success, _ren.Error);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(_pPrimePath, SkyrimRelease.SkyrimSE);
        return read(pp);
    }

    // NESTED: all 9 originating records are copied and renumbered, none an override
    [Fact]
    public void AllNineOriginatingRecordsAreCopiedAndRenumbered()
    {
        Assert.True(_ren.Success, _ren.Error);
        Assert.Equal(9, _ren.RecordsCopied);
        Assert.Equal(9, _ren.RecordsRenumbered);
    }

    // NESTED: the flat records land at their remapped keys
    [Fact]
    public void TheFlatRecordsLandAtTheirNewKeys()
        => ReadBack(pp =>
        {
            Assert.Equal(New(WaOld), pp.Weapons.Single(w => w.EditorID == "HcW2Weap").FormKey);
            Assert.Equal(New(FlOld), pp.FormLists.Single(l => l.EditorID == "HcW2List").FormKey);
            return 0;
        });

    // NESTED: interior cell -> placed is preserved, both at their new keys
    [Fact]
    public void TheInteriorCellAndItsPlacedRefLandAtTheirNewKeys()
        => ReadBack(pp =>
        {
            var cell = pp.EnumerateMajorRecords<ICellGetter>().Single(c => c.EditorID == "HcW2IntCell");
            Assert.Equal(New(IcOld), cell.FormKey);
            Assert.Equal(New(IpOld), cell.Temporary.Single(p => p.EditorID == "HcW2IntRef").FormKey);
            return 0;
        });

    // NESTED: worldspace -> exterior cell -> placed is preserved, all at their new keys
    [Fact]
    public void TheWorldspaceChainLandsAtItsNewKeys()
        => ReadBack(pp =>
        {
            var ws = pp.Worldspaces.Single(w => w.EditorID == "HcW2WS");
            Assert.Equal(New(WsOld), ws.FormKey);
            var cell = ws.EnumerateMajorRecords<ICellGetter>().Single(c => c.EditorID == "HcW2ExtCell");
            Assert.Equal(New(EcOld), cell.FormKey);
            Assert.Equal(New(EpOld), cell.Temporary.Single(p => p.EditorID == "HcW2ExtRef").FormKey);
            return 0;
        });

    // NESTED: topic -> INFO is preserved, both at their new keys
    [Fact]
    public void TheTopicAndItsInfoLandAtTheirNewKeys()
        => ReadBack(pp =>
        {
            var topic = pp.DialogTopics.Single(t => t.EditorID == "HcW2Topic");
            Assert.Equal(New(DtOld), topic.FormKey);
            Assert.Equal(New(InOld), topic.Responses.Single().FormKey);
            return 0;
        });

    // NESTED: INTERNAL refs (FormList -> weapon, the placed object's Base -> weapon) are repointed to the new key
    [Fact]
    public void InternalRefsFlatAndNestedAreRepointed()
        => ReadBack(pp =>
        {
            Assert.Equal(New(WaOld), pp.FormLists.Single().Items.Single().FormKey);
            var cell = pp.EnumerateMajorRecords<ICellGetter>().Single(c => c.EditorID == "HcW2IntCell");
            Assert.Equal(New(WaOld), ((IPlacedObjectGetter)cell.Temporary.Single()).Base.FormKey);
            return 0;
        });

    // NESTED: every record lands in the ESL window, and each key MOVED (the fixture's keys sit inside the window too,
    // so an identity remap would pass a window check alone)
    [Fact]
    public void EveryRecordMovedIntoTheWindow()
    {
        Assert.True(_plan.Success, _plan.Error);
        Assert.Equal(9, _plan.Dict.Count);
        Assert.All(_plan.Dict, kv =>
        {
            Assert.NotEqual(kv.Key, kv.Value);
            Assert.InRange(kv.Value.ID, (uint)RemapEngine.EslFloor, (uint)RemapEngine.EslCeiling);
        });
        Assert.Equal(Enumerable.Range(0x800, 9).Select(i => (uint)i).ToHashSet(), _plan.Dict.Values.Select(k => k.ID).ToHashSet());
    }

    // EXTERNAL: the identify pass finds External (not Donor) as an external referencer, with nothing unscannable
    [Fact]
    public void TheIdentifyPassFindsTheExternalReferencer()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _donorPath, _extPath });
        var id = RemapEngine.IdentifyExternalReferencers(resolver, _plan.Dict.Keys.ToHashSet(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DonorKey.FileName.String });
        Assert.True(id.HasExternalReferencers);
        Assert.Contains(ExtKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(DonorKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(0, id.UnscannableRecords);
    }

    // EXTERNAL: RepointInPlace rewrites External's reference to the new weapon key
    [Fact]
    public void RepointInPlaceRewritesTheExternalReference()
    {
        using (var resolver = LoadOrderResolver.Build(new[] { _donorPath, _extPath }))
        {
            var rep = RemapEngine.RepointInPlace(resolver, ExtKey.FileName.String, _plan.Dict);
            Assert.True(rep.Success, rep.Error);
        }
        using var ee = SkyrimMod.CreateFromBinaryOverlay(_extPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(New(WaOld), ee.FormLists.First().Items.First().FormKey);
    }

    static void FileInterior(SkyrimMod mod, Cell cell)
    {
        uint id = cell.FormKey.ID;
        int blockN = (int)(id % 10), subN = (int)((id / 10) % 10);
        var block = new CellBlock { BlockNumber = blockN, GroupType = GroupTypeEnum.InteriorCellBlock };
        var sub = new CellSubBlock { BlockNumber = subN, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        block.SubBlocks.Add(sub);
        mod.Cells.Records.Add(block);
    }

    static void FileExterior(Worldspace ws, Cell cell, int gridX, int gridY)
    {
        var block = new WorldspaceBlock
        {
            BlockNumberX = (short)Math.Floor(gridX / 32.0), BlockNumberY = (short)Math.Floor(gridY / 32.0),
            GroupType = GroupTypeEnum.ExteriorCellBlock,
        };
        var sub = new WorldspaceSubBlock
        {
            BlockNumberX = (short)Math.Floor(gridX / 8.0), BlockNumberY = (short)Math.Floor(gridY / 8.0),
            GroupType = GroupTypeEnum.ExteriorCellSubBlock,
        };
        sub.Items.Add(cell);
        block.Items.Add(sub);
        ws.SubCells.Add(block);
    }
}

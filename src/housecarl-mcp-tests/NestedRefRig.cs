using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;

namespace HousecarlMcpTests;

/// <summary>A master and a user plugin that each hold one interior cell of placed references (Scale 1), an empty
/// target plugin to forward into, and a load order over the three, on a <see cref="WritePathRig"/>.</summary>
public sealed class NestedRefRig : IDisposable
{
    public const string MasterName = "HcNestedLcMaster.esm";
    public const string UserName = "HcNestedLcUser.esp";
    public const string TargetName = "HcNestedLcTarget.esp";

    public WritePathRig Rig { get; } = new();
    public LoadOrderResolver Order { get; }
    public IReadOnlyList<FormKey> MasterRefs { get; }
    public IReadOnlyList<FormKey> UserRefs { get; }
    public string UserPath { get; }
    public string TargetPath { get; }

    public NestedRefRig(int refs)
    {
        var master = CellOfRefs(MasterName, refs, out var masterRefs);
        var user = CellOfRefs(UserName, refs, out var userRefs);
        MasterRefs = masterRefs;
        UserRefs = userRefs;
        var masterPath = Rig.Write(master);
        UserPath = Rig.Write(user);
        TargetPath = Rig.Write(new SkyrimMod(ModKey.FromFileName(TargetName), SkyrimRelease.SkyrimSE));
        Order = Rig.Order(masterPath, UserPath, TargetPath);
    }

    static SkyrimMod CellOfRefs(string name, int refs, out List<FormKey> keys)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        var stem = Path.GetFileNameWithoutExtension(name);
        var cell = new Cell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = stem + "Cell" };
        keys = new List<FormKey>(refs);
        for (int i = 0; i < refs; i++)
        {
            var r = new PlacedObject(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = stem + "Ref" + i, Scale = 1.0f };
            cell.Temporary.Add(r);
            keys.Add(r.FormKey);
        }
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        mod.Cells.Records.Add(block);
        return mod;
    }

    /// <summary>The Scale of each of <paramref name="keys"/> in the plugin at <paramref name="path"/>, 0 when absent.</summary>
    public float[] Scales(string path, IEnumerable<FormKey> keys)
    {
        var byKey = Rig.Open(path).EnumerateMajorRecords<IPlacedObjectGetter>().ToDictionary(r => r.FormKey, r => r.Scale ?? 0f);
        return keys.Select(k => byKey.TryGetValue(k, out var s) ? s : 0f).ToArray();
    }

    public void Dispose() => Rig.Dispose();
}

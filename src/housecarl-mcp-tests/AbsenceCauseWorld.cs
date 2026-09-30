using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The not-in-the-order order the bulk-primitives-wave3 probe's diff arm built. A master defines W; the
/// replacer overrides it (99) and a lower-priority enabled mod ships a shadowed copy of the replacer (66); a disabled
/// mod overrides W (77); a backup of the replacer sits outside every root (55). Game Data serves one registered and
/// one unregistered plugin, each with a copy in a disabled decoy folder. Beside them: an unticked plugin, an
/// unregistered one in an enabled mod, an unlisted folder, and a ticked plugin no folder provides.</summary>
public sealed class AbsenceCauseWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-absence-cause-");
    public LoadOrderService Svc { get; }
    public const string GhostName = "HcW3Ghost.esp";
    public string ReplacerName { get; }
    public string ReplacerPath { get; }
    public string ShadowPath { get; }
    public string ArchivePath { get; }
    public string DataServedPath { get; }
    public string DataOffPath { get; }
    public string DecoyPath { get; }
    public string UntickedPath { get; }
    public string UntickedName { get; }
    public string DonorName { get; }
    public string UnlistedName { get; }
    public string UnregisteredName { get; }
    public FormKey WKey { get; }
    public FormKey UntickedWKey { get; }
    public FormKey UnlistedWKey { get; }
    public string WFid => ScratchMo2.Fid(WKey);

    public AbsenceCauseWorld()
    {
        var mKey = new ModKey("HcW3DiffMaster", ModType.Master);
        var rKey = new ModKey("HcW3DiffRepl", ModType.Plugin);
        ReplacerName = rKey.FileName.String;

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var k1 = m.Keywords.AddNew(); k1.EditorID = "DfKw1";
        var k2 = m.Keywords.AddNew(); k2.EditorID = "DfKw2";
        var w = m.Weapons.AddNew(); w.EditorID = "DfW"; w.Name = "Base"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(k1.FormKey), new FormLink<IKeywordGetter>(k2.FormKey) };
        WKey = w.FormKey;
        var w2 = m.Weapons.AddNew(); w2.EditorID = "DfW2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
        m.BeginWrite.ToPath(_mo2.InMod("DiffMaster", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var masters = new ISkyrimModGetter[] { m };

        void Override(ModKey key, string path, ushort damage, Action<IWeapon>? more = null)
        {
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var ow = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(mod, w);
            ow.BasicStats = new WeaponBasicStats { Damage = damage };
            more?.Invoke(ow);
            mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        }
        static FormKey Own(ModKey key, string path, string edid, ushort damage)
        {
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            var ow = mod.Weapons.AddNew(); ow.EditorID = edid; ow.BasicStats = new WeaponBasicStats { Damage = damage };
            mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            return ow.FormKey;
        }

        ReplacerPath = _mo2.InMod("DiffRepl", rKey);
        Override(rKey, ReplacerPath, 99, rw =>
        {
            rw.Name = "Winner";
            rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(k1.FormKey) };
        });

        var dKey = new ModKey("DiffDonor", ModType.Plugin);
        DonorName = dKey.FileName.String;
        Override(dKey, _mo2.InMod("DiffDonor", dKey), 77);

        // The replacer's filename again, in a LOWER-priority enabled mod: its folder is on, but it is not what loads.
        ShadowPath = _mo2.InMod("DiffReplShadow", rKey);
        Override(rKey, ShadowPath, 66);

        // Served from game Data and registered, with a disabled folder holding the same filename.
        var dsKey = new ModKey("HcW3DataServed", ModType.Master);
        DataServedPath = Path.Combine(_mo2.DataDir, dsKey.FileName.String);
        Own(dsKey, DataServedPath, "DataServedW", 11);
        DecoyPath = _mo2.InMod("DataServedDecoy", dsKey);
        File.Copy(DataServedPath, DecoyPath, overwrite: true);

        // The same decoy-ahead-of-Data layout on a name the order does not carry, so the locate actually runs.
        var dofKey = new ModKey("HcW3DataOff", ModType.Plugin);
        DataOffPath = Path.Combine(_mo2.DataDir, dofKey.FileName.String);
        Own(dofKey, DataOffPath, "DataOffW", 12);
        File.Copy(DataOffPath, _mo2.InMod("DataServedDecoy", dofKey), overwrite: true);

        var unKey = new ModKey("HcW3Unticked", ModType.Plugin);
        UntickedName = unKey.FileName.String;
        UntickedPath = _mo2.InMod("DiffUnticked", unKey);
        UntickedWKey = Own(unKey, UntickedPath, "UntickedW", 22);

        var unregKey = new ModKey("HcW3Unregistered", ModType.Plugin);
        UnregisteredName = unregKey.FileName.String;
        Own(unregKey, _mo2.InMod("DiffUnregistered", unregKey), "UnregW", 33);

        // On disk under the mods folder but named nowhere in modlist.txt: a just-written patch before the MO2 refresh.
        var unlKey = new ModKey("HcW3Unlisted", ModType.Plugin);
        UnlistedName = unlKey.FileName.String;
        UnlistedWKey = Own(unlKey, _mo2.InMod("DiffUnlistedFresh", unlKey), "UnlistedW", 44);

        ArchivePath = Path.Combine(_mo2.Root, "archive", rKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(ArchivePath)!);
        Override(rKey, ArchivePath, 55);

        _mo2.Profile(
            mKey.FileName + "\r\n" + dsKey.FileName + "\r\n" + rKey.FileName + "\r\n" + GhostName + "\r\n",
            "*" + mKey.FileName + "\r\n*" + dsKey.FileName + "\r\n*" + rKey.FileName + "\r\n" + unKey.FileName + "\r\n*" + GhostName + "\r\n",
            "+DiffRepl\r\n+DiffReplShadow\r\n+DiffMaster\r\n+DiffUnticked\r\n+DiffUnregistered\r\n-DiffDonor\r\n-DataServedDecoy\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

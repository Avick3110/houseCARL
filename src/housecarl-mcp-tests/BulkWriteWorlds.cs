using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>A bare MO2 instance under one temp root: the ini, the Default profile, the mods folder and game Data.</summary>
sealed class ScratchMo2
{
    public string Root { get; }
    public string Instance => Path.Combine(Root, "instance");
    public string Profiles => Path.Combine(Instance, "profiles", "Default");
    public string ModsDir => Path.Combine(Instance, "mods");
    public string DataDir => Path.Combine(Root, "game", "Data");

    public ScratchMo2(string prefix)
    {
        _ = TestCorpus.Path;   // the service's rulebook reads CorpusRulebook.CorpusPath, which this sets
        Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(Instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
    }

    /// <summary>The path a plugin takes inside a mod folder, with the folder created.</summary>
    public string InMod(string folder, ModKey key)
    {
        var p = Path.Combine(ModsDir, folder, key.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        return p;
    }

    public void Profile(string loadorder, string plugins, string modlist)
    {
        File.WriteAllText(Path.Combine(Profiles, "loadorder.txt"), "# header\r\n" + loadorder);
        File.WriteAllText(Path.Combine(Profiles, "plugins.txt"), plugins);
        File.WriteAllText(Path.Combine(Profiles, "modlist.txt"), "# header\r\n" + modlist);
    }

    public LoadOrderService Open()
    {
        var svc = LoadOrderService.WithInstance(Instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        svc.Stats();
        return svc;
    }

    public static string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public void Delete() { try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ } }
}

/// <summary>The composes= order the bulk-primitives-wave3 probe built: one master with a keyword, a weapon carrying
/// it, and a leveled item with no entries.</summary>
public sealed class ComposesBatchWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-composes-batch-");
    public LoadOrderService Svc { get; }
    public string ModsDir => _mo2.ModsDir;
    public FormKey ListKey { get; }
    public string ListFid { get; }
    public string WeaponFid { get; }

    public ComposesBatchWorld()
    {
        var mKey = new ModKey("HcW3Master", ModType.Master);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var kw = m.Keywords.AddNew(); kw.EditorID = "HcW3Kw";
        var w = m.Weapons.AddNew(); w.EditorID = "HcW3Weap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(kw.FormKey) };
        var ll = m.LeveledItems.AddNew(); ll.EditorID = "HcW3LL";
        m.BeginWrite.ToPath(_mo2.InMod("MasterMod", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(mKey.FileName + "\r\n", "*" + mKey.FileName + "\r\n", "+MasterMod\r\n");

        Svc = _mo2.Open();
        ListKey = ll.FormKey;
        ListFid = ScratchMo2.Fid(ll.FormKey);
        WeaponFid = ScratchMo2.Fid(w.FormKey);
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

/// <summary>The CopyFrom order the bulk-primitives-wave3 probe built. The master's weapon W carries a name, damage 10,
/// two keywords and a template link; a replacer wins W at damage 99 with no keywords and no template, and clears a
/// potion's one effect; a disabled mod overrides W at 77. W2 is master-only and W3 has no BasicStats.</summary>
public sealed class CopyFromWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-copy-from-");
    public LoadOrderService Svc { get; }
    public string ModsDir => _mo2.ModsDir;
    public string MasterName { get; }
    public string ReplacerName { get; }
    public FormKey WKey { get; }
    public FormKey W2Key { get; }
    public FormKey Kw1 { get; }
    public FormKey Kw2 { get; }
    public FormKey MgefKey { get; }
    public FormKey PotionKey { get; }
    public string WFid => ScratchMo2.Fid(WKey);
    public string W2Fid => ScratchMo2.Fid(W2Key);
    public string W3Fid { get; }
    public string PotionFid => ScratchMo2.Fid(PotionKey);

    public CopyFromWorld()
    {
        var mKey = new ModKey("HcW3CfMaster", ModType.Master);
        var rKey = new ModKey("HcW3CfRepl", ModType.Plugin);
        MasterName = mKey.FileName.String;
        ReplacerName = rKey.FileName.String;

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var k1 = m.Keywords.AddNew(); k1.EditorID = "CfKw1"; Kw1 = k1.FormKey;
        var k2 = m.Keywords.AddNew(); k2.EditorID = "CfKw2"; Kw2 = k2.FormKey;
        var w = m.Weapons.AddNew(); w.EditorID = "CfW";
        w.Name = "Base Sword";
        w.BasicStats = new WeaponBasicStats { Damage = 10 };
        w.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(Kw1), new FormLink<IKeywordGetter>(Kw2) };
        WKey = w.FormKey;
        var w2 = m.Weapons.AddNew(); w2.EditorID = "CfW2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 }; W2Key = w2.FormKey;
        w.Template.SetTo(W2Key);
        var w3 = m.Weapons.AddNew(); w3.EditorID = "CfW3"; w3.Name = "No Stats";
        W3Fid = ScratchMo2.Fid(w3.FormKey);
        var mg = m.MagicEffects.AddNew(); mg.EditorID = "CfMgef"; MgefKey = mg.FormKey;
        var pot = m.Ingestibles.AddNew(); pot.EditorID = "CfPotion";
        var seedEff = new Effect { Data = new EffectData { Magnitude = 5 } };
        seedEff.BaseEffect.SetTo(MgefKey);
        pot.Effects.Add(seedEff);
        PotionKey = pot.FormKey;
        m.BeginWrite.ToPath(_mo2.InMod("CfMaster", mKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, w);
        rw.Name = "Winner Sword";
        rw.BasicStats = new WeaponBasicStats { Damage = 99 };
        rw.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>();
        rw.Template.SetTo(FormKey.Null);
        ((IIngestible)WriteEngine.GenericGetOrAddAsOverride(r, pot)).Effects.Clear();
        r.BeginWrite.ToPath(_mo2.InMod("CfRepl", rKey)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        var dKey = new ModKey("DonorOld", ModType.Plugin);
        var d = new SkyrimMod(dKey, SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(d, w)).BasicStats = new WeaponBasicStats { Damage = 77 };
        d.BeginWrite.ToPath(_mo2.InMod("DonorOld", dKey)).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        _mo2.Profile(MasterName + "\r\n" + ReplacerName + "\r\n",
            "*" + MasterName + "\r\n*" + ReplacerName + "\r\n",
            "+CfRepl\r\n+CfMaster\r\n-DonorOld\r\n");
        Svc = _mo2.Open();
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

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

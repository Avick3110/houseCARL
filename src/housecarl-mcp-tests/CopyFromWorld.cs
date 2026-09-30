using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

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

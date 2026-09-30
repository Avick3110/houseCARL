using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

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

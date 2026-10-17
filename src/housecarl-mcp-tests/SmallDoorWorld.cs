using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Base defines five magic effects, two topics, three weapons and a spell carrying effect A; Top overrides the
/// three weapons and adds a spell carrying A, B, A, an enchantment carrying B and a topic; Held, last, adds a topic,
/// a magic effect and a spell carrying A, for a test to hold open.</summary>
public sealed class SmallDoorWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-small-door-");
    public readonly ModKey Base = new("HcSdBase", ModType.Master), Top = new("HcSdTop", ModType.Plugin), Held = new("HcSdHeld", ModType.Plugin);
    public LoadOrderService Svc { get; }
    public string HeldPath { get; }
    public FormKey EffectA { get; }
    public FormKey EffectB { get; }
    public FormKey BaseTopic { get; }
    public FormKey HeldTopic { get; }
    public FormKey BaseTopic2 { get; }
    public FormKey HeldEffect { get; }
    public FormKey[] Effects { get; }
    readonly SkyrimMod _base, _top;
    readonly string _topPath;
    public FormKey Weapon1 { get; }

    public SmallDoorWorld()
    {
        var b = new SkyrimMod(Base, SkyrimRelease.SkyrimSE);
        var a = b.MagicEffects.AddNew(); a.EditorID = "HcSdEffectA";
        var bb = b.MagicEffects.AddNew(); bb.EditorID = "HcSdEffectB";
        EffectA = a.FormKey; EffectB = bb.FormKey;
        Effects = new[] { EffectA, EffectB }.Concat("CDE".Select(c => { var m = b.MagicEffects.AddNew(); m.EditorID = "HcSdEffect" + c; return m.FormKey; })).ToArray();
        var topic = b.DialogTopics.AddNew(); topic.EditorID = "HcSdBaseTopic";
        BaseTopic = topic.FormKey;
        var topic2 = b.DialogTopics.AddNew(); topic2.EditorID = "HcSdBaseTopic2";
        BaseTopic2 = topic2.FormKey;
        var weapons = Enumerable.Range(1, 3).Select(i => { var w = b.Weapons.AddNew(); w.EditorID = "HcSdWeapon" + i; return w; }).ToList();
        Weapon1 = weapons[0].FormKey;
        Carrier(b.Spells.AddNew(), "HcSdBaseSpell", EffectA);
        b.BeginWrite.ToPath(_mo2.InMod("SdBase", Base)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var t = new SkyrimMod(Top, SkyrimRelease.SkyrimSE);
        foreach (var w in weapons) t.Weapons.Add(new Weapon(w.FormKey, SkyrimRelease.SkyrimSE) { EditorID = w.EditorID + "Top" });
        Carrier(t.Spells.AddNew(), "HcSdTopSpell", EffectA, EffectB, EffectA);
        var ench = t.ObjectEffects.AddNew(); ench.EditorID = "HcSdTopEnch";
        ench.Effects.Add(Effect(EffectB, 7f));
        t.DialogTopics.AddNew().EditorID = "HcSdTopTopic";
        _base = b; _top = t; _topPath = _mo2.InMod("SdTop", Top);
        t.BeginWrite.ToPath(_topPath).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        var h = new SkyrimMod(Held, SkyrimRelease.SkyrimSE);
        var heldTopic = h.DialogTopics.AddNew(); heldTopic.EditorID = "HcSdHeldTopic";
        HeldTopic = heldTopic.FormKey;
        var heldEffect = h.MagicEffects.AddNew(); heldEffect.EditorID = "HcSdHeldEffect";
        HeldEffect = heldEffect.FormKey;
        Carrier(h.Spells.AddNew(), "HcSdHeldSpell", EffectA);
        HeldPath = _mo2.InMod("SdHeld", Held);
        h.BeginWrite.ToPath(HeldPath).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        var names = new[] { Base, Top, Held }.Select(k => k.FileName.String).ToList();
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                     "+SdHeld\r\n+SdTop\r\n+SdBase\r\n");
        Svc = _mo2.Open();
    }

    /// <summary>Rewrite Top with the same FormKeys, each weapon's EditorID ending in <paramref name="suffix"/>.</summary>
    public void RewriteTop(string suffix)
    {
        foreach (var w in _top.Weapons) w.EditorID = w.EditorID![.."HcSdWeaponN".Length] + suffix;
        _top.BeginWrite.ToPath(_topPath).WithLoadOrder(new ISkyrimModGetter[] { _base }).Write();
    }

    static void Carrier(Spell s, string eid, params FormKey[] effects)
    {
        s.EditorID = eid;
        for (int i = 0; i < effects.Length; i++) s.Effects.Add(Effect(effects[i], i + 1));
    }

    static Effect Effect(FormKey mgef, float magnitude)
    {
        var e = new Effect { Data = new EffectData { Magnitude = magnitude, Area = 0, Duration = 5 } };
        e.BaseEffect.SetTo(mgef);
        return e;
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>From a magic effect to every spell, enchantment, potion, scroll and ingredient that applies it, at the
/// authored magnitude, area and duration, one row per matching entry; a non-effect or absent id fails by name and
/// an unused effect is a clean zero.</summary>
[Trait("tier", "integration")]
public sealed class EffectChainResolveTests : IClassFixture<EffectChainResolveTests.World>
{
    /// <summary>One master: effects Target, Other, Unused; one carrier of each type applying Target with its own
    /// magnitude, area and duration; a spell applying Other then Target twice; a spell of Other only; a spell with an
    /// unset base effect; and a weapon.</summary>
    public sealed class World : IDisposable
    {
        readonly string _dir;
        public LoadOrderResolver Resolver { get; }
        public FormKey Target, Unused, Spell1, Ench1, Alch1, Scroll1, Ingr1, Multi, NonMatch, NullBase, Weapon;
        public ISpell Spell1Body = null!, NullBaseBody = null!;
        public IWeapon WeaponBody = null!;

        public World()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hc-effect-chain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "HcEcMaster.esm");
            var m = new SkyrimMod(new ModKey("HcEcMaster", ModType.Master), SkyrimRelease.SkyrimSE);
            var t = m.MagicEffects.AddNew(); t.EditorID = "HcEcTarget";
            var u = m.MagicEffects.AddNew(); u.EditorID = "HcEcOther";
            var v = m.MagicEffects.AddNew(); v.EditorID = "HcEcUnused";
            Target = t.FormKey; Unused = v.FormKey;

            var s1 = m.Spells.AddNew(); s1.EditorID = "HcEcSpell1"; s1.Effects.Add(Eff(Target, 11f, 1, 101));
            var e1 = m.ObjectEffects.AddNew(); e1.EditorID = "HcEcEnch1"; e1.Effects.Add(Eff(Target, 22f, 2, 102));
            var a1 = m.Ingestibles.AddNew(); a1.EditorID = "HcEcAlch1"; a1.Effects.Add(Eff(Target, 33f, 3, 103));
            var c1 = m.Scrolls.AddNew(); c1.EditorID = "HcEcScroll1"; c1.Effects.Add(Eff(Target, 44f, 4, 104));
            var i1 = m.Ingredients.AddNew(); i1.EditorID = "HcEcIngr1"; i1.Effects.Add(Eff(Target, 55f, 5, 105));
            var s2 = m.Spells.AddNew(); s2.EditorID = "HcEcMulti";
            s2.Effects.Add(Eff(u.FormKey, 100f)); s2.Effects.Add(Eff(Target, 66f)); s2.Effects.Add(Eff(Target, 77f));
            var s3 = m.Spells.AddNew(); s3.EditorID = "HcEcNonMatch"; s3.Effects.Add(Eff(u.FormKey, 88f));
            var s4 = m.Spells.AddNew(); s4.EditorID = "HcEcNullBase"; s4.Effects.Add(Eff(null, 99f));
            var w = m.Weapons.AddNew(); w.EditorID = "HcEcWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
            (Spell1, Ench1, Alch1, Scroll1, Ingr1, Multi, NonMatch, NullBase, Weapon) =
                (s1.FormKey, e1.FormKey, a1.FormKey, c1.FormKey, i1.FormKey, s2.FormKey, s3.FormKey, s4.FormKey, w.FormKey);
            (Spell1Body, NullBaseBody, WeaponBody) = (s1, s4, w);
            m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            Resolver = LoadOrderResolver.Build(new[] { path });
        }

        static Effect Eff(FormKey? baseEffect, float magnitude, int area = 0, int duration = 0)
        {
            var e = new Effect();
            if (baseEffect is { } b) e.BaseEffect.SetTo(b);
            e.Data = new EffectData { Magnitude = magnitude, Area = area, Duration = duration };
            return e;
        }

        public void Dispose()
        {
            Resolver.Dispose();
            try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    readonly World _w;
    readonly EffectChainResult _all;

    public EffectChainResolveTests(World w)
    {
        _w = w;
        _all = EffectChain.Resolve(w.Resolver, w.Target, EffectChain.CarrierTypes, 500);
    }

    EffectChainRow Row(FormKey carrier, int index) => Assert.Single(_all.Rows, x => x.Carrier == carrier && x.EffectIndex == index);

    // Probe GATE-OK: "Resolve(target) succeeds and the header carries the MGEF's editorid".
    [Fact]
    public void ResolvingTheEffect_SucceedsAndNamesItsEditorId()
    {
        Assert.True(_all.Success, _all.Error);
        Assert.Equal("HcEcTarget", _all.MgefEditorId);
    }

    // Probe MATCH-ALL-FIVE: "SPEL/ENCH/ALCH/SCRL/INGR each return the authored magnitude/area/duration + catalog type".
    [Fact]
    public void EachOfTheFiveCarrierTypes_ReturnsItsAuthoredMagnitudeAreaDurationAndType()
    {
        foreach (var (fk, mag, type) in new[] { (_w.Spell1, 11f, "Spell"), (_w.Ench1, 22f, "ObjectEffect"), (_w.Alch1, 33f, "Ingestible"),
                                                 (_w.Scroll1, 44f, "Scroll"), (_w.Ingr1, 55f, "Ingredient") })
        {
            var r = Row(fk, 0);
            int n = (int)(mag / 11f);
            Assert.Equal((mag, n, 100 + n, type, 1), (r.Magnitude, r.Area, r.Duration, r.Type, r.EffectCount));
        }
    }

    // Probe MULTI-ENTRY: "a carrier applying the MGEF twice yields one row per entry (66 @1/3, 77 @2/3); its U-entry @0 excluded".
    [Fact]
    public void ACarrierApplyingTheEffectTwice_YieldsOneRowPerEntry()
    {
        Assert.Equal(2, _all.Rows.Count(x => x.Carrier == _w.Multi));
        Assert.Equal((66f, 3), (Row(_w.Multi, 1).Magnitude, Row(_w.Multi, 1).EffectCount));
        Assert.Equal((77f, 3), (Row(_w.Multi, 2).Magnitude, Row(_w.Multi, 2).EffectCount));
    }

    // Probe TOTAL: "exactly 7 carrier rows (5 single + 2 multi), not capped".
    [Fact]
    public void TheTotal_IsSevenRowsNotCapped()
    {
        Assert.Equal(7, _all.Total);
        Assert.Equal(7, _all.Rows.Count);
        Assert.False(_all.Capped);
    }

    // Probe NON-MATCH: "a carrier of a DIFFERENT MGEF (and the multi's U-entry) is never returned".
    // Probe NULL-BASE: "an effect with an unset BaseEffect is skipped (not matched, no throw / no scan note)".
    [Fact]
    public void OtherEffectsAndAnUnsetBaseEffect_AreNeverReturned()
    {
        Assert.DoesNotContain(_all.Rows, x => x.Carrier == _w.NonMatch || x.Carrier == _w.NullBase);
        Assert.DoesNotContain(_all.Rows, x => x.Magnitude is 88f or 100f or 99f);
        Assert.Null(_all.ScanNote);
    }

    // Probe TYPES-NARROW: "scope=[Spell] returns only spell carriers (S1 + multi×2 = 3)".
    [Fact]
    public void NarrowingToSpells_ReturnsOnlySpellCarriers()
    {
        var spells = EffectChain.Resolve(_w.Resolver, _w.Target, new[] { typeof(ISpellGetter) }, 500);
        Assert.True(spells.Success);
        Assert.Equal(3, spells.Total);
        Assert.All(spells.Rows, x => Assert.Equal("Spell", x.Type));
        Assert.Contains(spells.Rows, x => x.Carrier == _w.Spell1);
        Assert.Equal(2, spells.Rows.Count(x => x.Carrier == _w.Multi));
    }

    // Probe PURE: "EffectsOf returns the list for a Spell, null for a Weapon" and "Match returns the single hit (mag 11 @0)
    // on the spell, empty on the weapon + the null-base spell". Strengthened: each of the five carrier types reaches its list.
    [Fact]
    public void EffectsOfAndMatch_WorkOnInMemoryBodies()
    {
        Assert.Single(EffectChain.EffectsOf(_w.Spell1Body)!);
        Assert.Null(EffectChain.EffectsOf(_w.WeaponBody));
        var m = new SkyrimMod(new ModKey("HcEcPure", ModType.Plugin), SkyrimRelease.SkyrimSE);
        Assert.NotNull(EffectChain.EffectsOf(m.ObjectEffects.AddNew()));
        Assert.NotNull(EffectChain.EffectsOf(m.Ingestibles.AddNew()));
        Assert.NotNull(EffectChain.EffectsOf(m.Scrolls.AddNew()));
        Assert.NotNull(EffectChain.EffectsOf(m.Ingredients.AddNew()));

        var hit = Assert.Single(EffectChain.Match(_w.Spell1Body, _w.Target));
        Assert.Equal((11f, 0), (hit.Magnitude, hit.Index));
        Assert.Empty(EffectChain.Match(_w.WeaponBody, _w.Target));
        Assert.Empty(EffectChain.Match(_w.NullBaseBody, _w.Target));
    }

    // Probe Q3-NONMGEF: "a WEAP FormID fails LOUD ('not a MagicEffect'), no rows".
    // Probe Q3-ABSENT: "a FormID no plugin defines fails LOUD ('… in the load order'), no rows".
    [Fact]
    public void AWeaponIdAndAnAbsentId_FailByName()
    {
        var weap = EffectChain.Resolve(_w.Resolver, _w.Weapon, EffectChain.CarrierTypes, 500);
        Assert.False(weap.Success);
        Assert.Empty(weap.Rows);
        Assert.Contains("not a MagicEffect", weap.Error);
        var absent = EffectChain.Resolve(_w.Resolver, FormKey.Factory("ABCDEF:HcEcNotReal.esp"), EffectChain.CarrierTypes, 500);
        Assert.False(absent.Success);
        Assert.Empty(absent.Rows);
        Assert.Contains("in the load order", absent.Error);
    }

    // Probe Q3-UNUSED: "a valid-but-unreferenced MGEF returns a CLEAN zero (Success, 0 rows, no error, eid in header)".
    [Fact]
    public void AnUnusedEffect_IsACleanZero()
    {
        var r = EffectChain.Resolve(_w.Resolver, _w.Unused, EffectChain.CarrierTypes, 500);
        Assert.Null(r.Error);
        Assert.Equal(0, r.Total);
        Assert.Empty(r.Rows);
        Assert.Equal("HcEcUnused", r.MgefEditorId);
    }

    // Probe CAP: "limit=3 returns 3 rows but reports the TRUE total (7) and Capped".
    [Fact]
    public void ALimitBelowTheTotal_ReturnsTheLimitAndTheTrueTotalCapped()
    {
        var r = EffectChain.Resolve(_w.Resolver, _w.Target, EffectChain.CarrierTypes, 3);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(7, r.Total);
        Assert.True(r.Capped);
    }
}

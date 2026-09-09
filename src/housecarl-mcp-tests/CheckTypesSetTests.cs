using System.Text.Json;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>housecarl_check</c>'s record-type scope is a SET: a sweep over N types is the sweep over each, findings
/// merged, and one type is a set of one. The two subjects are the master's NPC_ and WEAP, one dangling ref each.
/// </summary>
[Collection("type-arms")]
[Trait("tier", "integration")]
public sealed class CheckTypesSetTests
{
    readonly TypeArmWorld _w;
    public CheckTypesSetTests(TypeArmFixture f) => _w = f.W;

    /// <summary>The set is the union: both types' findings come back from one call.</summary>
    [Fact]
    public void ASweepOverTwoTypesReportsBothTypesFindings()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" });

        Assert.Null(r.Error);
        Assert.Equal(2, r.TotalDangling);
        var ids = r.Reports.SelectMany(p => p.Dangling).Select(d => d.SourceEditorId).ToList();
        Assert.Contains(TypeArmWorld.SweepNpc, ids);
        Assert.Contains(TypeArmWorld.SweepWeapon, ids);
    }

    /// <summary>One type is a set of one, and it still answers over that type alone — the arm that keeps the union
    /// above from passing on a scope that stopped narrowing.</summary>
    [Fact]
    public void ASweepOverOneTypeStillAnswersOverThatTypeAlone()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc" });

        Assert.Null(r.Error);
        Assert.Equal(1, r.TotalDangling);
        var ids = r.Reports.SelectMany(p => p.Dangling).Select(d => d.SourceEditorId).ToList();
        Assert.Contains(TypeArmWorld.SweepNpc, ids);
        Assert.DoesNotContain(TypeArmWorld.SweepWeapon, ids);
    }

    /// <summary>The applied narrowing is spelled with both types, so a count can never read as the wider claim.</summary>
    [Fact]
    public void TheRenderNamesEveryTypeInTheSet()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" });

        Assert.Contains("types=[Npc, Weapon]", r.FilterNote);
    }

    /// <summary>An unknown entry refuses the whole sweep by name, the way the records surface refuses one.</summary>
    [Fact]
    public void AnUnknownTypeInTheSetRefusesTheSweepByName()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "NOSUCHTYPE" });

        Assert.Contains("NOSUCHTYPE", r.Error);
        Assert.Empty(r.Reports);
    }

    /// <summary>A BLANK entry names no type, so it is refused SAYING it is blank rather than quoted back as an
    /// unknown type ''. An empty set is a different thing (no narrowing) and is not this.</summary>
    [Fact]
    public void ABlankTypeInTheSetIsRefusedAsBlank()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "" });

        Assert.Contains("blank record type", r.Error);
        Assert.DoesNotContain("unknown record type", r.Error);
        Assert.Empty(r.Reports);
    }

    /// <summary>One rule, not two: the records surface refuses the same blank entry with the same sentence, so a
    /// caller cannot learn one grammar on one surface and meet another on the other.</summary>
    [Fact]
    public void TheRecordsSurfaceRefusesABlankTypeTheSameWay()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "" });

        Assert.Contains("blank record type", r, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown record type", r, StringComparison.Ordinal);
    }

    /// <summary>The scope's types share ONE listing, filled plugin by plugin and type by type inside each — so a
    /// two-type sweep with room for one finding can be missing either type. The response says that, and it does NOT
    /// claim the budget went in the order the types were named: the sweep runs plugin-major, so it never does.</summary>
    [Fact]
    public void ACutMultiTypeListingSaysTheTypesShareOneListing()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "Npc", "Weapon" });
        var text = CheckErrorsFixtures.Text(r, 80_000);

        Assert.Equal(2, r.TotalDangling);                                  // the totals are never capped
        Assert.Single(r.Reports.SelectMany(p => p.Dangling));              // the listing carried one of them
        Assert.Contains("ONE listing for every type in the scope (types=[Npc, Weapon])", text, StringComparison.Ordinal);
        Assert.Contains("plugin by plugin (non-base plugins first, base masters last)", text, StringComparison.Ordinal);
        Assert.Contains("unlisted, not clean", text, StringComparison.Ordinal);
        Assert.DoesNotContain("in the order", text, StringComparison.Ordinal);
    }

    /// <summary>The knob the rule points at is the one that actually cut the listing: here the budget, so it names
    /// limit= and not max_chars=, which dropped nothing.</summary>
    [Fact]
    public void ABudgetCutNamesLimitAsTheKnobThatFired()
    {
        var text = CheckErrorsFixtures.Text(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "Npc", "Weapon" }), 80_000);

        Assert.Contains("and limit= cut it short", text, StringComparison.Ordinal);
    }

    /// <summary>max_chars cuts the same one listing in the same order the budget does, so it hides a whole type the
    /// same way and fires the same rule — naming max_chars=, the knob that actually fired, with limit= innocent at
    /// 1000. The cap is searched for rather than pinned: only a PARTIAL cut is the shape under test.</summary>
    [Fact]
    public void AMaxCharsCutFiresTheRuleAndNamesMaxCharsAsTheKnob()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" });
        Assert.Equal(2, r.TotalDangling);

        // Each lane reserves its own room, so the cap that cuts one need not cut the other: each is searched for
        // the cut on its own terms and then asked what it says about it.
        string? cutText = null;
        for (int cap = 400; cap <= 20_000; cap += 25)
        {
            var t = CheckErrorsFixtures.Text(r, cap);
            if (!t.Contains("did not fit this response", StringComparison.Ordinal)) continue;
            cutText = t;
            break;
        }
        Assert.True(cutText is not null, "no cap in 400..20000 cut this listing by max_chars alone");
        Assert.DoesNotContain("the listing budget (limit=", cutText!, StringComparison.Ordinal);
        Assert.Contains("and max_chars= cut it short", cutText!, StringComparison.Ordinal);
        Assert.Contains("unlisted, not clean", cutText!, StringComparison.Ordinal);

        JsonElement acct = default;
        for (int cap = 400; cap <= 20_000; cap += 25)
        {
            var fam = CheckErrorsFixtures.ErrorsFamily(CheckErrorsFixtures.Json(r, cap));
            if (!fam.TryGetProperty("plugins", out var pl) || pl.GetArrayLength() == 0) continue;
            var a = fam.GetProperty("accounting");
            if (a.GetProperty("dangling_missing_by_response_cut").GetInt32() == 0) continue;
            acct = a;
            break;
        }
        Assert.Equal(0, acct.GetProperty("dangling_missing_by_budget").GetInt32());
        Assert.Equal("Npc, Weapon", acct.GetProperty("listing_short_across_types").GetString());
        Assert.Equal("max_chars=", acct.GetProperty("listing_short_by").GetString());
    }

    /// <summary>A listing that dropped nothing is complete for every type in the scope, so the rule is not stated —
    /// a warning about a hole that is not there reads as one that is.</summary>
    [Fact]
    public void AnUncutMultiTypeListingStatesNoTypeScopeRule()
    {
        var text = CheckErrorsFixtures.Text(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1000, types: new[] { "Npc", "Weapon" }), 80_000);

        Assert.DoesNotContain("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>ONE concrete type in scope shares its listing with nothing, so a cut listing states no type-scope
    /// rule either — the rule is about the set, not about the cut.</summary>
    [Fact]
    public void ACutSingleTypeListingStatesNoTypeScopeRule()
    {
        var r = _w.Svc.CheckErrors(new[] { _w.MasterName }, 0, types: new[] { "Npc" });
        var text = CheckErrorsFixtures.Text(r, 80_000);

        Assert.Equal(1, r.TotalDangling);
        Assert.Empty(r.Reports.SelectMany(p => p.Dangling));                // the budget listed none of it
        Assert.DoesNotContain("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>An entry that EXPANDS is spelled with the arms it expanded to, so the rule names types the reader
    /// can act on: types=[GMST] alone names none of the things that can be short.</summary>
    [Fact]
    public void AnExpandingTypeEntryIsSpelledWithItsArms()
    {
        // GMST carries no links of its own, so the two link-bearing types are what the budget of 1 cuts.
        var text = CheckErrorsFixtures.Text(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "GMST", "Npc", "Weapon" }), 80_000);

        Assert.Contains("GMST → GameSetting", text, StringComparison.Ordinal);   // the arms all share that prefix
        Assert.Contains("GameSettingInt", text, StringComparison.Ordinal);
        Assert.Contains("GameSettingFloat", text, StringComparison.Ordinal);
        Assert.Contains("unlisted, not clean", text, StringComparison.Ordinal);
    }

    /// <summary>ONE entry is enough where that entry is a polymorphic base: it resolves to several types that share
    /// the listing, so the scope label is spelled and the rule can fire. One CONCRETE entry resolves to one type and
    /// is not labelled at all — the gate is on the types, not on the entries.</summary>
    [Fact]
    public void AOneEntryPolymorphicBaseIsLabelledWithItsArms()
    {
        var arms = new[] { typeof(IGameSettingIntGetter), typeof(IGameSettingFloatGetter) };
        var expanded = new SweepScope(null, null, arms, "GMST", SweepScope.SpellTypeEntry("GMST", arms));
        Assert.Equal("GMST → GameSettingInt, GameSettingFloat", expanded.TypeScopeLabel);

        var one = new[] { typeof(INpcGetter) };
        var concrete = new SweepScope(null, null, one, "Npc", SweepScope.SpellTypeEntry("Npc", one));
        Assert.Null(concrete.TypeScopeLabel);
    }

    /// <summary>The json twin carries the same two facts under the same test, so the two transports cannot disagree
    /// about which listing was short or which knob cut it.</summary>
    [Fact]
    public void TheJsonTwinCarriesTheTypesSharingTheListingAndTheKnob()
    {
        var json = CheckErrorsFixtures.Json(
            _w.Svc.CheckErrors(new[] { _w.MasterName }, 1, types: new[] { "Npc", "Weapon" }), 80_000);
        var acct = CheckErrorsFixtures.ErrorsFamily(json).GetProperty("accounting");

        Assert.Equal("Npc, Weapon", acct.GetProperty("listing_short_across_types").GetString());
        Assert.Equal("limit=", acct.GetProperty("listing_short_by").GetString());
    }
}

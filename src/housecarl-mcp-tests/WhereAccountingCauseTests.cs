using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The where-accounting's soft note names WHY a predicate read no value. A null field and a field the record's
/// type does not carry are not read faults, so the note must not word them as one: an agent counting unreadable
/// fields as errors otherwise gets the wrong picture of a healthy scan.
/// </summary>
[Trait("tier", "unit")]
public sealed class WhereAccountingCauseTests
{
    static string? NoteOver(string clause, IEnumerable<IMajorRecordGetter> bodies)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        foreach (var b in bodies) set!.Matches(b);
        return set!.AccountingNote();
    }

    /// <summary>Four spells and one weapon under a weapon-only path: the spells simply have no such field.</summary>
    [Fact]
    public void AFieldTheRecordTypeDoesNotCarryIsNamedAsThat_NotAsAReadFault()
    {
        var mod = new SkyrimMod(new ModKey("HcAcct", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < 4; i++) mod.Spells.AddNew().EditorID = $"HcAcctS{i}";
        var w = mod.Weapons.AddNew();
        w.EditorID = "HcAcctW";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };

        var bodies = mod.Spells.Select(s => (IMajorRecordGetter)s).Append(w).ToList();
        var note = NoteOver("BasicStats.Damage >= 0", bodies);

        Assert.NotNull(note);
        Assert.Contains("not a field on the record read (4)", note);
        Assert.DoesNotContain("read fault", note);
        Assert.DoesNotContain("readable", note);
    }

    /// <summary>Three weapons with no enchantment and one with one: the three are unset, not unreadable.</summary>
    [Fact]
    public void ANullFieldIsNamedUnset_NotAsAReadFault()
    {
        var mod = new SkyrimMod(new ModKey("HcAcctNull", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var ench = mod.ObjectEffects.AddNew();
        for (int i = 0; i < 3; i++) mod.Weapons.AddNew().EditorID = $"HcAcctNullW{i}";
        var enchanted = mod.Weapons.AddNew();
        enchanted.EditorID = "HcAcctNullWE";
        enchanted.ObjectEffect.SetTo(ench.FormKey);

        var bodies = mod.Weapons.Select(x => (IMajorRecordGetter)x).ToList();
        var note = NoteOver($"ObjectEffect = {ench.FormKey.ID:X6}:{ench.FormKey.ModKey.FileName}", bodies);

        Assert.NotNull(note);
        Assert.Contains("unset — null or absent (3)", note);
        Assert.DoesNotContain("read fault", note);
        Assert.DoesNotContain("readable", note);
    }
}

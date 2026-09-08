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
    static string? NoteOver(string clause, IEnumerable<IMajorRecordGetter> bodies,
                            Func<FormKey, IMajorRecordGetter?>? fetchWinnerBody = null)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        if (fetchWinnerBody is not null) set!.BindResolution(_ => null, fetchWinnerBody);
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

    /// <summary>A link step whose targets all read the field UNSET is an unset path, not a parse failure — the
    /// same rule one hop down. The targets resolve and are read; there is simply no value on them.</summary>
    [Fact]
    public void ALinkStepWhoseTargetsAreAllUnsetIsNamedUnset_NotAsAReadFault()
    {
        var mod = new SkyrimMod(new ModKey("HcAcctLink", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var bodies = new List<IMajorRecordGetter>();
        var targets = new Dictionary<FormKey, IMajorRecordGetter>();
        for (int i = 0; i < 3; i++)
        {
            var ench = mod.ObjectEffects.AddNew();       // no Name on any of them
            targets[ench.FormKey] = ench;
            var w = mod.Weapons.AddNew();
            w.EditorID = $"HcAcctLinkW{i}";
            w.ObjectEffect.SetTo(ench.FormKey);
            bodies.Add(w);
        }

        var note = NoteOver("ObjectEffect->Name = Frostbite", bodies,
                            fk => targets.TryGetValue(fk, out var t) ? t : null);

        Assert.NotNull(note);
        Assert.Contains("UNSET", note);
        Assert.DoesNotContain("read FAULT", note);
    }

    /// <summary>A link target that does not resolve at all (its plugin is not in the order) read nothing, so
    /// nothing faulted — it must not be reported as a Mutagen parse failure either.</summary>
    [Fact]
    public void ALinkStepWhoseTargetsDoNotResolveIsNotNamedAReadFault()
    {
        var mod = new SkyrimMod(new ModKey("HcAcctGone", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var absent = FormKey.Factory("000800:HcAcctMissing.esp");
        var bodies = new List<IMajorRecordGetter>();
        for (int i = 0; i < 3; i++)
        {
            var w = mod.Weapons.AddNew();
            w.EditorID = $"HcAcctGoneW{i}";
            w.ObjectEffect.SetTo(absent);
            bodies.Add(w);
        }

        var note = NoteOver("ObjectEffect->Name = Frostbite", bodies, _ => null);

        Assert.NotNull(note);
        Assert.DoesNotContain("read FAULT", note);
        Assert.DoesNotContain("read fault", note);
    }
}

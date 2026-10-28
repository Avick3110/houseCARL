using HousecarlCore;
using Mutagen.Bethesda.Plugins.Records;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The unplanned read's no-such-field note says WHICH of the two dead-end causes it is. A field the record type
/// does not model (Mutagen models VirtualMachineAdapter on some record types and not others, so a scripted ALCH
/// is invisible) and a mistyped name both used to read as a bare "(no field X)", and they need opposite next
/// moves: nothing to fix in the path, versus fix the spelling. The records tool plans its paths before reading
/// (RecordsProjectionPlanTests); these pin the note the other readers (write read-back, FaceGen, SkyPatcher
/// replay) still get.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsNoFieldNoteTests : RecordsTestBase
{
    public RecordsNoFieldNoteTests(RecordsFixture f) : base(f) { }

    IMajorRecordGetter SpellBody => W.SpellBodies.First(b => b.FormKey == W.SpellA);

    IMajorRecordGetter WeaponBody => W.WeaponBodies.First(b => b.FormKey == W.Weapons[0]);

    static string Note(IMajorRecordGetter body, string path) => ReadEngine.ReadFields(body, new[] { path }).Fields.Single().Note ?? "";

    string Spell(string path) => Note(SpellBody, path);

    /// <summary>Issue #528's own case, on the record type the fixture has: Mutagen models VirtualMachineAdapter on
    /// many types and not on Spell, so the name is right and the path is not the thing to fix.</summary>
    [Fact]
    public void AFieldTheTypeDoesNotModelIsToldTheNameIsNotMistyped()
    {
        var r = Spell("VirtualMachineAdapter");
        Assert.Contains("not a mistyped name", r);
        Assert.Contains("just not on Spell", r);
    }

    /// <summary>A name no modeled type carries is the other cause, and is named as such.</summary>
    [Fact]
    public void AMistypedFieldIsToldItIsMistyped()
    {
        var r = Spell("Effcts");
        Assert.Contains("a mistyped name", r);
        Assert.Contains("did you mean 'Effects'?", r);
        Assert.DoesNotContain("not a mistyped name", r);
    }

    /// <summary>Field names are case-sensitive, so a case-only slip is a miss — and the exact spelling is the one
    /// thing to say about it.</summary>
    [Fact]
    public void ACaseOnlySlipIsOfferedTheExactSpelling() =>
        Assert.Contains("did you mean 'EditorID'?", Spell("editorid"));

    /// <summary>A case-only slip whose OTHER casing is a real field somewhere else is still a case-only slip. The
    /// corpus models 'DATA' on DialogResponses and nowhere else, and a Weapon spells the same subrecord 'Data' —
    /// so a caller who typed xEdit's own spelling at a WEAP must be given 'Data', not told the name is fine.</summary>
    [Fact]
    public void ACaseOnlySlipBeatsTheSameCasingModeledElsewhere()
    {
        var r = Note(WeaponBody, "DATA");
        Assert.Contains("did you mean 'Data'?", r);
        Assert.DoesNotContain("not a mistyped name", r);
    }

    /// <summary>A name the corpus models on other types can still be this record's typo. A Spell spells it
    /// 'Effects' and the corpus carries the exact name 'Effect' on one other type, so the owner's own near field
    /// is what the caller needs — not "not a mistyped name" over a one-character fix.</summary>
    [Fact]
    public void AnOwnerNearFieldBeatsTheModeledElsewhereClaim()
    {
        var r = Spell("Effect");
        Assert.Contains("did you mean 'Effects'?", r);
        Assert.DoesNotContain("not a mistyped name", r);
    }

    /// <summary>The walk lands on values the catalog does not model at all — a FormLink is one — and the index can
    /// only speak about types it carries. Off one of those the note stays bare: a verdict there would name a
    /// schema that does not exist and tell the caller the name is right when the whole path is wrong.</summary>
    [Fact]
    public void AnOwnerTheCatalogDoesNotModelGetsTheBareNote()
    {
        var r = Spell("HalfCostPerk.Name");
        Assert.Contains("(no field Name)", r);
        Assert.DoesNotContain("mistyped name", r);
    }

    /// <summary>The types the field IS modeled on are named, not just counted, so a caller can see the name is
    /// real. ArmorRating lives on exactly one type, which is the tightest form of that claim.</summary>
    [Fact]
    public void TheTypesThatDoModelTheFieldAreNamed()
    {
        var r = Note(WeaponBody, "ArmorRating");
        Assert.Contains("1 other type(s) (Armor)", r);
        Assert.Contains("just not on Weapon", r);
    }

    /// <summary>The verdict costs a schema lookup and a nearest-name sweep over the owner type's whole field list,
    /// and every record of a type dead-ends the same way — so it is computed once per (corpus, owner type, name),
    /// not once per record read. The name is unique to this run so the memo is cold when it runs.</summary>
    [Fact]
    public void ManyReadsComputeOneVerdict()
    {
        var probe = "NoFieldMemoProbe" + Guid.NewGuid().ToString("N");
        int Computed() => ModeledFieldIndex.ComputationsOf("Spell", probe);
        var before = Computed();
        Assert.True(W.SpellBodies.Count > 1, "the reads must cross more than one record for this to say anything");
        foreach (var b in W.SpellBodies) Assert.Contains("a mistyped name", Note(b, probe));
        Assert.Equal(1, Computed() - before);
    }
}

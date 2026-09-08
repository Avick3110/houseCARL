using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The projection lane's no-such-field note says WHICH of the two dead-end causes it is. A field the record type
/// does not model (Mutagen models VirtualMachineAdapter on some record types and not others, so a scripted ALCH
/// is invisible) and a mistyped name both used to read as a bare "(no field X)", and they need opposite next
/// moves: nothing to fix in the path, versus fix the spelling.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsNoFieldNoteTests : RecordsTestBase
{
    public RecordsNoFieldNoteTests(RecordsFixture f) : base(f) { }

    string Spell(params string[] paths) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Fields(paths));

    /// <summary>Issue #528's own case, on the record type the fixture has: Mutagen models VirtualMachineAdapter on
    /// many types and not on Spell, so the name is right and the path is not the thing to fix.</summary>
    [Fact]
    public void AFieldTheTypeDoesNotModelIsToldTheNameIsNotMistyped()
    {
        var r = Spell("VirtualMachineAdapter");
        Served(r, "not a mistyped name", "just not on Spell");
    }

    /// <summary>A name no modeled type carries is the other cause, and is named as such.</summary>
    [Fact]
    public void AMistypedFieldIsToldItIsMistyped()
    {
        var r = Spell("Effcts");
        Served(r, "a mistyped name", "did you mean 'Effects'?");
        Assert.DoesNotContain("not a mistyped name", r);
    }

    /// <summary>Field names are case-sensitive, so a case-only slip is a miss — and the exact spelling is the one
    /// thing to say about it.</summary>
    [Fact]
    public void ACaseOnlySlipIsOfferedTheExactSpelling() =>
        Served(Spell("editorid"), "did you mean 'EditorID'?");

    /// <summary>The types the field IS modeled on are named, not just counted, so a caller can see the name is
    /// real. ArmorRating lives on exactly one type, which is the tightest form of that claim.</summary>
    [Fact]
    public void TheTypesThatDoModelTheFieldAreNamed() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: Fields("ArmorRating")),
               "1 other type(s) (Armor)", "just not on Weapon");

    /// <summary>The verdict costs a schema lookup and a nearest-name sweep over the owner type's whole field list,
    /// and every record in a scan dead-ends the same way — so it is computed once per (corpus, owner type, name),
    /// not once per scanned record. The name is unique to this test so the memo is cold when it runs.</summary>
    [Fact]
    public void AScanComputesOneVerdictForTheWholeScan()
    {
        var before = HousecarlCore.ModeledFieldIndex.VerdictComputations;
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, project: Fields("NoFieldMemoProbe"));
        Served(r, "a mistyped name");
        Assert.True(W.SpellBodies.Count > 1, "the scan must cross more than one record for this to say anything");
        Assert.Equal(1, HousecarlCore.ModeledFieldIndex.VerdictComputations - before);
    }
}

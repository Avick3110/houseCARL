using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using static HousecarlMcpTests.ClosureCopySourceGraph;

namespace HousecarlMcpTests;

/// <summary>Strip removes a clone's links into the source universe, one rule per shape, with nullability judged on
/// the record model rather than on SetToNull's presence, and refuses a required link leaving the record untouched.
/// Migrated from <c>closure-copy-guard</c> (NULLABILITY, REQUIRED LINK).</summary>
[Trait("tier", "unit")]
public sealed class ClosureCopyStripTests
{
    /// <summary>A clone carrying a nullable source link, a list element carrying one, an internalized head part,
    /// and a REQUIRED Class link set to <paramref name="classKey"/>.</summary>
    static (Npc Clone, FormKey InternalHp) Clone(FormKey classKey)
    {
        var g = new ClosureCopySourceGraph();
        var clone = new Npc(new FormKey(PatchKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "Clone" };
        clone.DefaultOutfit.SetTo(OutfitKey);
        clone.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(FactionKey), Rank = 0 });
        clone.HeadParts.Add(g.Copy.Map[Hp]);
        clone.Class.SetTo(classKey);
        return (clone, g.Copy.Map[Hp]);
    }

    // FIXTURE PRECONDITION: the REQUIRED link's type DOES expose SetToNull (so method-presence would 'succeed')
    [Fact]
    public void TheRequiredClassLinkExposesSetToNull()
    {
        var (clone, _) = Clone(ClassKey);
        Assert.NotNull(clone.Class.GetType().GetMethod("SetToNull", Type.EmptyTypes));
    }

    // a REQUIRED bound link REFUSES, never nulled (that shipped Class=00000000 once)
    [Fact]
    public void ARequiredSourceLinkRefuses()
    {
        var (clone, _) = Clone(ClassKey);
        var r = ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.False(r.Success);
        Assert.Equal(CopyRefusalKind.RequiredForeignLink, r.Refusal?.Kind);
    }

    // ...naming the field and the key, so the caller can act on it
    [Fact]
    public void TheRequiredLinkRefusalNamesTheFieldAndTheKey()
    {
        var (clone, _) = Clone(ClassKey);
        var r = ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Equal(("Class", ClassKey), (r.Refusal?.Field, r.Refusal?.Key ?? default));
    }

    // ...and the required link is STILL SET: the refusal did not null what it refused to null
    [Fact]
    public void TheRefusedRequiredLinkIsStillSet()
    {
        var (clone, _) = Clone(ClassKey);
        ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Equal(ClassKey, clone.Class.FormKey);
    }

    // ...and NOTHING ELSE was stripped either: a refusal leaves the record untouched, not half-done
    [Fact]
    public void ARefusalLeavesTheNullableLinkAndTheListElementInPlace()
    {
        var (clone, _) = Clone(ClassKey);
        ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Single(clone.Factions);
        Assert.Equal(OutfitKey, clone.DefaultOutfit.FormKey);
    }

    // the strip succeeds once no REQUIRED bound link remains
    [Fact]
    public void WithTheRequiredLinkOutsideTheSourceTheStripSucceeds()
    {
        var (clone, _) = Clone(new FormKey(Keep, 0x901));
        var r = ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.True(r.Success, r.Refusal?.Detail);
    }

    // a NULLABLE bound link is nulled and NAMED; ...and is actually gone from the record
    [Fact]
    public void ANullableSourceLinkIsNulledAndNamed()
    {
        var (clone, _) = Clone(new FormKey(Keep, 0x901));
        var r = ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Contains("803", r.Stripped.Single(s => s.Field == "DefaultOutfit").Removed);
        Assert.True(clone.DefaultOutfit.IsNull);
    }

    // a LIST ELEMENT carrying a bound link is dropped and NAMED; ...and is actually gone
    [Fact]
    public void AListElementCarryingASourceLinkIsDroppedAndNamed()
    {
        var (clone, _) = Clone(new FormKey(Keep, 0x901));
        var r = ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Contains(r.Stripped, s => s.Field == "Factions[0]");
        Assert.Empty(clone.Factions);
    }

    // an ALREADY-INTERNALIZED link is untouched: the strip removes what was not copied, not what was
    [Fact]
    public void AnInternalizedLinkIsLeftAlone()
    {
        var (clone, internalHp) = Clone(new FormKey(Keep, 0x901));
        ClosureCopy.StripBoundLinks(clone, IsBound);
        Assert.Equal(internalHp, Assert.Single(clone.HeadParts).FormKey);
    }
}

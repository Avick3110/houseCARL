using HousecarlCore;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.NifSourceLaneWorld;

namespace HousecarlMcpTests;

/// <summary>#388: naming a mod in source_provider= reaches that mod's loose files AND its own root archives, ticked or
/// not; an off-order read says so; a name with no folder behind it refuses by name. nif_set answers through the same
/// lane.</summary>
[Trait("tier", "integration")]
public sealed class NifSourceLaneReachTests : IClassFixture<NifSourceLaneWorld>
{
    readonly NifSourceLaneWorld _w;
    public NifSourceLaneReachTests(NifSourceLaneWorld w) => _w = w;

    static NifSetOp[] Flags() => new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x800000E) };

    // Probe: "an ENABLED mod whose only copy is in its own .bsa is reached by naming the MOD".
    [Fact]
    public void AnEnabledModWhoseOnlyCopyIsInItsOwnBsaIsReachedByNamingTheMod()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: W.BsaOnlyMod);

        Assert.Contains("read from:", text);
        Assert.DoesNotContain("does not supply", text);
    }

    // Probe: "the second mesh is ABSENT with no source_provider= (nothing active provides it)". The per-mesh line, not
    // the discovery hedge, which quotes the word too.
    [Fact]
    public void TheUntickedModsMeshIsAbsentWithoutSourceProvider()
        => Assert.Contains("ABSENT — no active mod or BSA provides", NifTools.NifInspect(_w.Svc, new[] { W.OffRel }));

    // Probe: "naming an UNTICKED mod reads out of its own root archive, and never reports the mesh ABSENT".
    [Fact]
    public void NamingAnUntickedModReadsOutOfItsOwnRootArchive()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { W.OffRel }, source_provider: W.OffMod);

        Assert.Contains("read from:", text);
        Assert.DoesNotContain("ABSENT", text);
    }

    // Probe: "…and SAYS the game is not loading that copy, naming the mod".
    [Fact]
    public void AnUntickedModReadSaysTheGameIsNotLoadingThatCopy()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { W.OffRel }, source_provider: W.OffMod);

        Assert.Contains("NOT enabled in MO2", text);
        Assert.Contains(W.OffMod, text);
    }

    // Probe: "an ENGINE-LOADED archive reached by its mod's name carries NO off-order note".
    [Fact]
    public void AnEngineLoadedArchiveReachedByItsModsNameCarriesNoOffOrderNote()
        => Assert.DoesNotContain("[!] read from", NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: W.BsaOnlyMod));

    // Probe: "in_place refuses a copy the game is not loading".
    [Fact]
    public void InPlaceRefusesACopyTheGameIsNotLoading()
    {
        var r = _w.Svc.NifSet(W.OffRel, Flags(), W.OffMod, null, null, inPlace: true, acknowledge: true);

        Assert.Contains("in-place edits the copy the game loads", r.Error);
        Assert.False(r.InPlace);
    }

    // Probe: "a name with no folder behind it refuses by NAME and says where it looked".
    [Fact]
    public void ANameWithNoFolderBehindItRefusesByNameAndSaysWhereItLooked()
    {
        var text = NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, source_provider: "NoSuchMod");

        Assert.Contains("'NoSuchMod' does not supply", text);
        Assert.Contains("no MO2 mod folder of that name", text);
        Assert.DoesNotContain("ABSENT", text);
    }

    // Probe: "nif_set's refusal is the same sentence".
    [Fact]
    public void NifSetsRefusalForAnUnknownNameIsTheSameSentence()
    {
        var r = _w.Svc.NifSet(W.FaceRel, Flags(), "NoSuchMod", null, null, inPlace: false, acknowledge: false);

        Assert.Contains("'NoSuchMod' does not supply", r.Error);
        Assert.DoesNotContain("ABSENT", r.Error);
    }

    // Probe: "nif_set reaches the same copy by the same name". It writes a mod folder, so it gets its own instance.
    [Fact]
    public void NifSetReachesTheSameCopyByTheSameName()
    {
        using var own = new NifSourceLaneWorld();

        var r = own.Svc.NifSet(W.FaceRel, Flags(), W.BsaOnlyMod, "NifLane", null, inPlace: false, acknowledge: false);

        Assert.Null(r.Error);
        // The provider is still the ARCHIVE: naming the mod is how it was addressed, not what supplied the bytes.
        Assert.Equal("BSA", r.Edited?.Kind);
    }
}

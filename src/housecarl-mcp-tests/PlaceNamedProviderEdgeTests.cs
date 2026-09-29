using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.PlaceInstance;

namespace HousecarlMcpTests;

/// <summary>The named-provider lane's edge cases, each on its own instance: an active archive's filename is a name the
/// active order answers, an incomplete scan says so, an unreadable copy or folder is an unknown and never an absence,
/// and which of the two off-order reasons the response states.</summary>
[Trait("tier", "integration")]
public sealed class PlaceNamedProviderEdgeTests
{
    // Probe M26: "the fixture has an ACTIVE archive named Dummy.bsa", "…and a mods\Dummy.bsa FOLDER really holds a path
    // nothing else supplies", "an ACTIVE ARCHIVE's name is answered by the universe and never falls through to a folder
    // of that name". Strengthened: the refusal is asserted to be the reserved-name one.
    [Fact]
    public void AnActiveArchivesNameNeverFallsThroughToAFolderOfThatName()
    {
        using var p = new PlaceInstance();
        File.WriteAllBytes(Path.Combine(p.Mod("ArchiveHost"), "Dummy.bsa"), FaceArchive());
        const string folderOnly = @"meshes\hcprobe\m26-folder-only.nif";
        var collide = p.Mod("Dummy.bsa");
        Loose(collide, folderOnly, new byte[] { 0x26, 0x26, 0x26 });
        p.ProfileWithDummy("ArchiveHost", "+ArchiveHost");
        var svc = p.Open();
        var providers = svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Providers ?? Array.Empty<AssetProvider>();
        Assert.Contains(providers, pr => pr.Kind == AssetKind.Bsa && pr.Source == "Dummy.bsa");
        Assert.True(File.Exists(Path.Combine(collide, folderOnly)));

        var r = svc.PlaceAssets(new[] { new PlaceRequest(folderOnly, folderOnly, "Dummy.bsa") }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Null(r.SourceOffOrderProvider);
        Assert.Contains(WriteSentences.PlaceSourceReservedName, r.Error);
    }

    const string Wanted = @"meshes\hcprobe\m28-only-in-the-broken-archive.nif";

    // Probe M28: "the fixture's scan really is INCOMPLETE" and "a named-pole refusal from an incomplete scan carries the
    // caveat (with source= / no source=)".
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ANamedPoleRefusalFromAnIncompleteScanSaysSo(bool withSource)
    {
        using var p = new PlaceInstance();
        File.WriteAllBytes(Path.Combine(p.Mod("HostMod"), "Dummy.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        p.ProfileWithDummy("HostMod", "+HostMod");
        var svc = p.Open();
        Assert.True(svc.AssetStatus(new[] { Wanted }).ReadIncomplete);

        var r = svc.PlaceAssets(new[] { new PlaceRequest(Wanted, withSource ? Wanted : null, "HostMod") }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceScanIncomplete, r.Error);
    }

    // Probe M28: "…and a COMPLETE scan's refusal does NOT carry it".
    [Fact]
    public void ACompleteScansRefusalDoesNotCarryTheCaveat()
    {
        using var p = new PlaceInstance();
        p.ProfileWithDummy("CleanMod", "+CleanMod");

        var r = p.Open().PlaceAssets(new[] { new PlaceRequest(Wanted, Wanted, "CleanMod") }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.DoesNotContain(WriteSentences.PlaceSourceScanIncomplete, r.Error);
    }

    // Probe M29 / M30: "<an unreadable SUBTREE under the mod folder | an unreadable MOD FOLDER> refuses as UNREADABLE",
    // "…carrying the unknown-not-absent caveat with its cause", "…and NEVER as an absence", "…and the cause names WHY
    // without leaking a machine path". A host where the deny does not bite fails the test rather than passing it.
    [Theory]
    [InlineData("DeniedSubtree", true)]
    [InlineData("DeniedFolder", false)]
    public void AnUnreadableCopyOrModFolderIsAnUnknownNeverAnAbsence(string provider, bool denySubtree)
    {
        using var p = new PlaceInstance();
        const string rel = @"meshes\hcprobe\denied.nif";
        var mod = p.Mod(provider);
        Loose(mod, rel, new byte[] { 0x29, 0x29 });
        p.ProfileWithDummy("Anchor", "+Anchor");
        var svc = p.Open();
        var target = denySubtree ? Path.Combine(mod, "meshes") : mod;
        Assert.True(DenyAce.TryDeny(target), "this host would not apply a deny ACE, so the cell is unproven here");
        try
        {
            var r = svc.PlaceAssets(new[] { new PlaceRequest(rel, rel, provider) }, null, null).Results[0];

            Assert.False(r.Placed);
            Assert.Contains(WriteSentences.PlaceSourceFolderUnreadable, r.Error);
            Assert.Contains("unscanned rather than absent", r.Error);
            Assert.DoesNotContain(WriteSentences.PlaceSourceNoSuchFolder, r.Error);
            Assert.DoesNotContain(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
            Assert.DoesNotContain(p.Root, r.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@":\", r.Error);
        }
        finally { DenyAce.Undeny(target); }
    }

    // Probe M31: "a folder that cannot be listed refuses as UNREADABLE", "…and NEVER as \"there is no mod folder of that
    // name\" for a folder that is right there", "…carrying the unknown-not-absent caveat".
    [Fact]
    public void AFolderThatCannotBeListedIsUnreadableNotMissing()
    {
        using var p = new PlaceInstance();
        var listDenied = p.Mod("ListDenied");
        File.WriteAllBytes(Path.Combine(listDenied, "Root.bsa"), FaceArchive());
        p.ProfileWithDummy("Anchor", "+Anchor");
        var svc = p.Open();
        Assert.True(TryDenyList(listDenied), "this host would not apply a list-deny ACE, so the cell is unproven here");
        try
        {
            const string rel = @"meshes\hcprobe\m31.nif";
            var r = svc.PlaceAssets(new[] { new PlaceRequest(rel, rel, "ListDenied") }, null, null).Results[0];

            Assert.False(r.Placed);
            Assert.Contains(WriteSentences.PlaceSourceFolderUnreadable, r.Error);
            Assert.DoesNotContain(WriteSentences.PlaceSourceNoSuchFolder, r.Error);
            Assert.Contains("unscanned rather than absent", r.Error);
        }
        finally { UndenyList(listDenied); }
    }

    const string LooseRel = @"meshes\hcprobe\enabled-loose.nif";

    /// <summary>EnabledArch is ticked, ships a loose file, and a root archive no active plugin binds; NHost ships the
    /// one active plugin.</summary>
    static PlaceInstance EnabledArchive()
    {
        var p = new PlaceInstance();
        var ea = p.Mod("EnabledArch");
        File.WriteAllBytes(Path.Combine(ea, "EnabledArch.bsa"), FaceArchive());
        Loose(ea, LooseRel, new byte[] { 0x4E, 0x4E });
        p.ProfileWithDummy("NHost", "+EnabledArch", "+NHost");
        return p;
    }

    // Probe N1: "the enabled mod's unbound archive contributes nothing to the active universe", "…while the same mod's
    // LOOSE tree does", "an ENABLED mod's unbound archive is described as an archive the engine skips", "…and NOT as a mod
    // that is unticked".
    [Fact]
    public void AnEnabledModsUnboundArchiveIsDescribedAsSkippedNotUnticked()
    {
        using var p = EnabledArchive();
        var svc = p.Open();
        Assert.False(svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit!.Exists);
        Assert.True(svc.AssetStatus(new[] { LooseRel }).Results[0].Hit!.Exists);

        var text = PlaceTools.Place(svc, new[] { new PlaceTarget { Path = FacegenRel, Source = FacegenRel } },
            source_provider: "EnabledArch", patch: "NOwnerEnabled");

        Assert.Contains("root archive the engine does NOT load", text);
        Assert.DoesNotContain("NOT enabled in MO2", text);
    }

    // Probe N2: "the lane finds an enabled mod's loose copy", "…and does NOT mark it OwnerEnabled", "…while the archive
    // find in the SAME enabled folder does".
    [Fact]
    public void OnlyTheArchiveFindInAnEnabledFolderIsMarkedOwnerEnabled()
    {
        using var p = EnabledArchive();
        var res = AssetResolver.Build("", p.Mods, "", new[] { "EnabledArch", "NHost" }, Array.Empty<ActiveArchive>());

        var looseLook = res.TryResolveOffOrderProvider("EnabledArch", LooseRel);
        var archLook = res.TryResolveOffOrderProvider("EnabledArch", FacegenRel);

        Assert.Equal(AssetKind.Loose, looseLook.Source?.Kind);
        Assert.True(looseLook.Source!.OffOrder);
        Assert.False(looseLook.Source.OwnerEnabled);
        Assert.Equal(AssetKind.Bsa, archLook.Source?.Kind);
        Assert.True(archLook.Source!.OwnerEnabled);
    }
}

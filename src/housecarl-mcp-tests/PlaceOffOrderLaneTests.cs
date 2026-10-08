using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.PlaceInstance;

namespace HousecarlMcpTests;

/// <summary>Two enabled mods, two unticked ones holding copies nothing enabled has (Disabled1 loose and a root archive,
/// Disabled2 a root archive only), and a mods\Data folder colliding with the game Data folder's name.</summary>
public sealed class PlaceOffOrderWorld : IDisposable
{
    public const string OnlyDisabled = @"meshes\hcprobe\only-disabled.nif";
    public const string Shared = @"meshes\hcprobe\shared.nif";
    public const string Contended = @"meshes\hcprobe\contended.nif";
    public const string DataCollision = @"meshes\hcprobe\data-collision.nif";
    public const string DataName = "Data";

    public static readonly byte[] EShared = { 0xE1, 0xE1, 0xE1, 0xE1 };
    public static readonly byte[] DShared = { 0xD1, 0xD1 };
    public static readonly byte[] DOnly = { 0xD0, 0xD0, 0xD0 };
    public static readonly byte[] DFaceLoose = { 0xDF, 0xDF, 0xDF, 0xDF, 0xDF };
    public static readonly byte[] DataGame = { 0x6A, 0x6A, 0x6A };
    public static readonly byte[] DataFolder = { 0x7B, 0x7B };

    public PlaceInstance P { get; } = new();
    public LoadOrderService Svc { get; }

    public PlaceOffOrderWorld()
    {
        Build(P);
        Svc = P.Open();
    }

    /// <summary>Lay the off-order fixture into <paramref name="p"/>; a test that changes it under a live service builds
    /// its own.</summary>
    public static void Build(PlaceInstance p)
    {
        var e1 = p.Mod("Enabled1");
        Loose(e1, Shared, EShared);
        Loose(e1, Contended, new byte[] { 0xC1 });
        var e2 = p.Mod("Enabled2");
        Loose(e2, Contended, new byte[] { 0xC2, 0xC2 });

        var d1 = p.Mod("Disabled1");
        Loose(d1, OnlyDisabled, DOnly);
        Loose(d1, Shared, DShared);
        Loose(d1, Contended, new byte[] { 0xDC });
        Loose(d1, FacegenRel, DFaceLoose);
        File.WriteAllBytes(Path.Combine(d1, "Disabled1.bsa"), FaceArchive());
        File.WriteAllBytes(Path.Combine(p.Mod("Disabled2"), "Disabled2.bsa"), FaceArchive());

        Loose(p.Mod(DataName), DataCollision, DataFolder);
        Loose(p.Data, DataCollision, DataGame);

        File.WriteAllText(Path.Combine(e1, "Dummy.esp"), "x");
        p.Profile(new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+Enabled1", "+Enabled2", "-Disabled1", "-Disabled2" });
    }

    public void Dispose() => P.Dispose();
}

/// <summary>source_provider= naming a mod MO2 does not tick: its loose copy, then its root archives, served off disk
/// and said so; and every pole that must not widen with it — the auto-resolve, *winner, the contention list, a name
/// the active order already answers, and a name that is really a path.</summary>
[Trait("tier", "integration")]
public sealed class PlaceOffOrderLaneTests : IClassFixture<PlaceOffOrderWorld>
{
    const string OnlyDisabled = PlaceOffOrderWorld.OnlyDisabled;
    const string Shared = PlaceOffOrderWorld.Shared;
    const string Contended = PlaceOffOrderWorld.Contended;

    readonly PlaceOffOrderWorld _w;
    public PlaceOffOrderLaneTests(PlaceOffOrderWorld w) => _w = w;

    PlaceOutcome Place(string dest, string? source, string? provider) =>
        _w.Svc.PlaceAssets(new[] { new PlaceRequest(dest, source, provider) }, null, null);

    static byte[]? Bytes(PlaceOutcome o, string rel) => PlacedAt(o, rel) is { } p ? File.ReadAllBytes(p) : null;

    // Probe M premise: "the off-order copies are INVISIBLE to the active universe", "…and the enabled universe supplies
    // shared once and contended twice", "…and Disabled1's loose facegen differs from the copy in its own archive".
    [Fact]
    public void TheUntickedCopiesAreInvisibleToTheActiveOrder()
    {
        var pre = _w.Svc.AssetStatus(new[] { OnlyDisabled, FacegenRel, Shared, Contended }).Results;

        Assert.False(pre[0].Hit!.Exists);
        Assert.False(pre[1].Hit!.Exists);
        Assert.Single(pre[2].Hit!.Providers);
        Assert.Equal(2, pre[3].Hit!.Providers.Count);
        Assert.NotEqual(ArchiveFaceBytes, PlaceOffOrderWorld.DFaceLoose);
    }

    // Probe M1: "no source=: naming an unticked mod places ITS copy of the destination path".
    [Fact]
    public void NamingAnUntickedModWithNoSourcePlacesItsCopy()
    {
        var o = Place(OnlyDisabled, null, "Disabled1");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.DOnly, Bytes(o, OnlyDisabled));
    }

    // Probe M2: "source= a different path: the unticked mod's bytes land under the DESTINATION name".
    [Fact]
    public void NamingAnUntickedModRenamesItsCopyToTheDestination()
    {
        const string dest = @"meshes\hcprobe\renamed.nif";

        var o = Place(dest, OnlyDisabled, "Disabled1");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.DOnly, Bytes(o, dest));
    }

    // Probe M3: "a copy that exists ONLY inside the unticked mod's root .bsa is extracted".
    [Fact]
    public void ACopyOnlyInTheUntickedModsRootArchiveIsExtracted()
    {
        var o = Place(FacegenRel, FacegenRel, "Disabled2");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(ArchiveFaceBytes, Bytes(o, FacegenRel));
    }

    // Probe M4: "the place TOOL, no source=: the unticked mod's copy" and M5: "the place TOOL, with source=: same".
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePlaceToolThreadsAMembersPoleToTheUntickedMod(bool withSource)
    {
        var text = PlaceTools.Place(_w.Svc, new[]
            { new PlaceTarget { Path = OnlyDisabled, Source = withSource ? OnlyDisabled : null, SourceProvider = "Disabled1" } },
            patch: withSource ? "MBulkSrc" : "MBulkNoSrc");

        Assert.Equal(PlaceOffOrderWorld.DOnly, File.ReadAllBytes(_w.P.PlacedFileFrom(text, OnlyDisabled)!));
    }

    // Probe M6: "the render SAYS the source mod is not enabled — exactly once", "…naming the mod it read from", "…and
    // the destination's own enable+sort instruction is still said exactly once, not duplicated".
    [Fact]
    public void AnOffOrderReadIsSaidOnceNamingTheModAndTheEnableInstructionIsNotDuplicated()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = OnlyDisabled } }, source_provider: "Disabled1", patch: "MProv");

        Assert.Single(Regex.Matches(text, "NOT enabled in MO2"));
        Assert.Contains("Disabled1", text);
        Assert.Single(Regex.Matches(text, "\"wrote it\" is not \"it wins\""));
    }

    // Probe M6: "…and a read served by the ACTIVE universe PLACES and says no such thing".
    [Fact]
    public void AnActiveReadPlacesAndSaysNothingAboutEnabling()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = Shared } }, source_provider: "Enabled1", patch: "MProvEnabled");

        Assert.Equal(PlaceOffOrderWorld.EShared, File.ReadAllBytes(_w.P.PlacedFileFrom(text, Shared)!));
        Assert.DoesNotContain("NOT enabled in MO2", text);
    }

    // Probe M7: "a name the built universe knows is served by the UNIVERSE, not by a mods\ folder of that name", "…and
    // specifically not the folder's copy", "…and it is not reported as an off-order read".
    [Fact]
    public void ANameTheActiveOrderKnowsIsServedByItNotByAModsFolderOfThatName()
    {
        const string dest = @"meshes\hcprobe\data-copy.nif";

        var o = Place(dest, PlaceOffOrderWorld.DataCollision, PlaceOffOrderWorld.DataName);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.DataGame, Bytes(o, dest));
        Assert.NotEqual(PlaceOffOrderWorld.DataFolder, Bytes(o, dest));
        Assert.Null(o.Results[0].SourceOffOrderProvider);
    }

    // Probe M8: "a name with no folder anywhere is refused NAMING it, and says there is no such folder" and "…and offers
    // NO candidate list, because there are no candidates to offer".
    [Fact]
    public void ANameWithNoFolderAnywhereIsRefusedWithNoCandidateList()
    {
        var r = Place(OnlyDisabled, OnlyDisabled, "NoSuchModAnywhere").Results[0];

        Assert.False(r.Placed);
        Assert.Contains("NoSuchModAnywhere", r.Error);
        Assert.Contains(WriteSentences.PlaceSourceNoSuchFolder, r.Error);
        Assert.DoesNotContain("pass one of these names", r.Error);
    }

    // Probe M9: "the same miss on a path OTHERS supply lists them as the remedy" and "…naming exactly the enabled providers".
    [Fact]
    public void TheSameMissOnASuppliedPathListsTheSuppliers()
    {
        var r = Place(Contended, Contended, "NoSuchModAnywhere").Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceNoSuchFolder, r.Error);
        Assert.Contains("pass one of these names", r.Error);
        Assert.Contains("\"Enabled1\"", r.Error);
        Assert.Contains("\"Enabled2\"", r.Error);
    }

    // Probe M10: "with NO source_provider=, an unticked mod's copy is NOT auto-resolved".
    [Fact]
    public void AnUntickedModsCopyIsNotAutoResolved()
    {
        var r = Place(OnlyDisabled, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains("no copy to auto-place", r.Error);
    }

    // Probe M11: "*winner places the ACTIVE winner's bytes on a path an unticked mod also supplies" and "…and
    // specifically not the unticked mod's".
    [Fact]
    public void TheWinnerPoleIsTheActiveWinner()
    {
        const string dest = @"meshes\hcprobe\winner-copy.nif";

        var o = Place(dest, Shared, AssetSourceChoice.WinnerToken);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.EShared, Bytes(o, dest));
        Assert.NotEqual(PlaceOffOrderWorld.DShared, Bytes(o, dest));
    }

    // Probe M12: "the ambiguity refusal counts the ACTIVE providers only" and "…and never names the unticked mod".
    [Fact]
    public void TheContentionListIsTheActiveOrders()
    {
        var r = Place(Contended, Contended, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains("2 providers", r.Error);
        Assert.DoesNotContain("Disabled1", r.Error);
    }

    // Probe M14: "the folder's LOOSE copy beats its own root archive" and "…and specifically not the archive's".
    [Fact]
    public void TheUntickedFoldersLooseCopyBeatsItsOwnArchive()
    {
        var o = Place(FacegenRel, FacegenRel, "Disabled1");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.DFaceLoose, Bytes(o, FacegenRel));
        Assert.NotEqual(ArchiveFaceBytes, Bytes(o, FacegenRel));
    }

    // Probe M15: "naming the unticked mod reads ITS copy though an enabled mod supplies the path too", "…and
    // specifically not the enabled mod's", "…and the result carries the off-order provenance the render prints".
    [Fact]
    public void TheNameBindsEvenWhenAnEnabledModSuppliesThePath()
    {
        const string dest = @"meshes\hcprobe\bound.nif";

        var o = Place(dest, Shared, "Disabled1");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceOffOrderWorld.DShared, Bytes(o, dest));
        Assert.NotEqual(PlaceOffOrderWorld.EShared, Bytes(o, dest));
        Assert.Equal("Disabled1", o.Results[0].SourceOffOrderProvider);
    }

    // Probe M17: "naming an ENABLED mod that lacks the path says its folder AND its own archives were searched" and "…and
    // does NOT claim the name was answered by the active order without a look".
    [Fact]
    public void NamingAnEnabledModThatLacksThePathSaysItsFolderWasSearched()
    {
        var r = Place(OnlyDisabled, OnlyDisabled, "Enabled1").Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
        Assert.DoesNotContain(WriteSentences.PlaceSourceReservedName, r.Error);
    }

    // Probe M18: "an archive-served off-order read places the archive's bytes" and "…and SAYS so, naming the mod".
    [Fact]
    public void AnArchiveServedOffOrderReadIsSaidToo()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = FacegenRel, Source = FacegenRel } },
            source_provider: "Disabled2", patch: "MArchProv");

        Assert.Equal(ArchiveFaceBytes, File.ReadAllBytes(_w.P.PlacedFileFrom(text, FacegenRel)!));
        Assert.Contains("NOT enabled in MO2", text);
        Assert.Contains("Disabled2", text);
    }

    // Probe M19: "an existing off-order folder whose archive lacks the path is REFUSED, saying it was SEARCHED", "…and
    // NOT that the folder is missing", "…and the folder really exists, with no unreadable-archive caveat".
    [Fact]
    public void AnUntickedFolderWhoseArchiveLacksThePathIsACleanSearchedMiss()
    {
        var r = Place(OnlyDisabled, OnlyDisabled, "Disabled2").Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
        Assert.DoesNotContain(WriteSentences.PlaceSourceNoSuchFolder, r.Error);
        Assert.True(Directory.Exists(Path.Combine(_w.P.Mods, "Disabled2")));
        Assert.DoesNotContain("unscanned", r.Error);
    }

    // Probe M22: "a trailing-dot spelling ('<name>.') never reaches disk" and "…and SAYS the name is path-shaped rather
    // than one the order already provides", for 'Data.' on a path the Data folder holds and 'Enabled1.' on a path
    // Enabled1 holds.
    [Theory]
    [InlineData("Data.", PlaceOffOrderWorld.DataCollision)]
    [InlineData("Enabled1.", PlaceOffOrderWorld.Shared)]
    public void ATrailingDotNameNeverReachesDisk(string spelling, string rel)
    {
        var r = Place(rel, rel, spelling).Results[0];

        Assert.False(r.Placed, "placed off-order=" + r.SourceOffOrderProvider);
        Assert.Contains(WriteSentences.PlaceSourceNotAFolderName, r.Error);
    }

    // A destination whose last segment is only dots fails loud rather than writing a FILE named for its parent folder.
    [Fact]
    public void ADestinationEndingInADotsOnlySegmentIsRefused()
    {
        var o = Place(@"textures\hcprobe\...", Shared, null);

        Assert.False(o.Results[0].Placed);
        Assert.Contains(@"textures\hcprobe\...", o.Results[0].Error);
        Assert.Contains("only dots or spaces", o.Results[0].Error);
        Assert.Null(o.ModFolder);
    }

    // Probe M23: "*winner (no source= / with source=) is never refused as a named PROVIDER" and "…and the pole is not
    // quoted back as though it were a mod name". Strengthened: the probe held negatives only; each shape now also
    // asserts the refusal it does get, the ordinary nothing-provides-it one.
    [Theory]
    [InlineData(false, "no copy to auto-place")]
    [InlineData(true, "provides the source")]
    public void TheWinnerPoleIsNeverRefusedAsANamedProvider(bool withSource, string expected)
    {
        var r = Place(OnlyDisabled, withSource ? OnlyDisabled : null, AssetSourceChoice.WinnerToken).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(expected, r.Error);
        Assert.DoesNotContain(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
        Assert.DoesNotContain(WriteSentences.PlaceSourceReservedName, r.Error);
        Assert.DoesNotContain($"'{AssetSourceChoice.WinnerToken}' does not supply", r.Error);
    }

    // Probe M24: "a named-provider miss still offers the verified root-prefix hint".
    [Fact]
    public void ANamedProviderMissStillOffersTheRootPrefixHint()
    {
        var rootless = Shared.Substring(@"meshes\".Length);

        var r = Place(rootless, rootless, "NoSuchModAnywhere").Results[0];

        Assert.False(r.Placed);
        Assert.Contains("Did you mean", r.Error);
        Assert.Contains(Shared, r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // Probe M25: "the winner line names the DESTINATION folder, not a bare \"the mod\"" and "…so it cannot be read as the
    // off-order source mod the line above says need not be enabled".
    [Fact]
    public void TheWinnerLineNamesTheDestinationFolder()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = OnlyDisabled } },
            source_provider: "Disabled1", patch: "MAdjacency");
        var folder = text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("mod folder:", StringComparison.Ordinal))
                         ?.Trim()["mod folder:".Length..].Trim();

        Assert.False(string.IsNullOrEmpty(folder));
        Assert.Contains($"once '{folder}' is enabled", text);
        Assert.DoesNotContain("once the mod is enabled", text);
    }

    // Probe M27: "the Named pole with NO lookup supplied reports NotConsulted", "…and its refusal makes NO claim about
    // the disk, having looked at none", "…while still saying the one thing that is true".
    [Fact]
    public void ANamedPoleWithNoLookupIsNotConsultedAndItsRefusalClaimsNoDiskLook()
    {
        var res = new PlacementResolution(Contended, new[]
        {
            new PlacementSource("Enabled1", AssetKind.Loose, @"C:\x\a.nif", null, Contended),
            new PlacementSource("Enabled2", AssetKind.Loose, @"C:\x\b.nif", null, Contended),
        }, Ambiguous: true, ReadIncomplete: false);

        var pick = AssetSourceSelection.Select(res, AssetSourceChoice.Named("NoSuchModAnywhere"));
        var sentence = WriteSentences.PlaceSourceNamedAbsent("NoSuchModAnywhere", Contended, pick.ProviderNames, pick.OffOrderReason);

        Assert.Equal(OffOrderReason.NotConsulted, pick.OffOrderReason);
        foreach (var claim in new[] { WriteSentences.PlaceSourceReservedName, WriteSentences.PlaceSourceNotAFolderName,
                                      WriteSentences.PlaceSourceNoSuchFolder, WriteSentences.PlaceSourceDiskFolderSearched,
                                      WriteSentences.PlaceSourceFolderUnreadable })
            Assert.DoesNotContain(claim, sentence);
        Assert.Contains("does not supply", sentence);
    }
}

/// <summary>The off-order cells that add to the fixture under a live service, each on its own instance.</summary>
[Trait("tier", "integration")]
public sealed class PlaceOffOrderGuardTests
{
    const string OnlyDisabled = PlaceOffOrderWorld.OnlyDisabled;

    static (PlaceInstance P, LoadOrderService Svc) Build()
    {
        var p = new PlaceInstance();
        PlaceOffOrderWorld.Build(p);
        return (p, p.Open());
    }

    // Probe M13: "a provider name that is a PATH ('<name>') places nothing" and "…and is refused AS a path-shaped name,
    // not as a universe name", for every spelling, each of which has a copy to reach.
    [Theory]
    [InlineData(@"..\outsider")]
    [InlineData("../outsider")]
    [InlineData(@"Disabled1\..\Disabled1")]
    [InlineData("<absolute outsider>")]
    [InlineData(".")]
    [InlineData("..")]
    public void AProviderNameThatIsAPathPlacesNothing(string bad)
    {
        var (p, svc) = Build();
        using var _p = p;
        var outsider = Path.Combine(p.Inst, "outsider");
        Loose(outsider, OnlyDisabled, new byte[] { 0x66, 0x66, 0x66, 0x66, 0x66, 0x66 });
        Loose(p.Inst, OnlyDisabled, new byte[] { 0x67, 0x67, 0x67, 0x67, 0x67 });
        Loose(p.Mods, OnlyDisabled, new byte[] { 0x68, 0x68, 0x68, 0x68 });
        p.TouchModlist();
        if (bad == "<absolute outsider>") bad = outsider;

        var r = svc.PlaceAssets(new[] { new PlaceRequest(OnlyDisabled, OnlyDisabled, bad) }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceNotAFolderName, r.Error);
    }

    // Probe M16: "…the mods\Data copy really is on disk", "a name the universe knows NEVER falls through to a mods\
    // folder of that name", "…and SAYS the name is one the active order already provides, claiming no folder search".
    [Fact]
    public void ANameTheActiveOrderKnowsNeverFallsThroughToAFolderOfThatName()
    {
        var (p, svc) = Build();
        using var _p = p;
        const string folderOnly = @"meshes\hcprobe\data-folder-only.nif";
        Loose(Path.Combine(p.Mods, PlaceOffOrderWorld.DataName), folderOnly, new byte[] { 0x5C, 0x5C });
        p.TouchModlist();
        Assert.True(File.Exists(Path.Combine(p.Mods, PlaceOffOrderWorld.DataName, folderOnly)));

        var r = svc.PlaceAssets(new[] { new PlaceRequest(folderOnly, folderOnly, PlaceOffOrderWorld.DataName) }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceReservedName, r.Error);
        Assert.DoesNotContain(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
    }

    // Probe M20: "an archive NESTED in the mod folder is not read — root archives only" and "…and that nested archive
    // really does hold the path". Strengthened: the refusal is asserted to be the searched-folder miss, not any refusal.
    [Fact]
    public void AnArchiveNestedInTheModFolderIsNotRead()
    {
        var (p, svc) = Build();
        using var _p = p;
        var nested = Path.Combine(p.Mods, "DisabledNested", "sub", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "Nested.bsa"), FaceArchive());
        p.TouchModlist();
        Assert.True(AssetResolver.ArchiveHasEntry(Path.Combine(nested, "Nested.bsa"), FacegenRel));

        var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "DisabledNested") }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceDiskFolderSearched, r.Error);
    }

    // Probe M21: "an unreadable archive in the named folder is reported as UNKNOWN, not as absent" and "…naming the
    // archive that could not be read".
    [Fact]
    public void AnUnreadableArchiveInTheNamedFolderIsAnUnknown()
    {
        var (p, svc) = Build();
        using var _p = p;
        File.WriteAllBytes(Path.Combine(p.Mod("DisabledBroken"), "Broken.bsa"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        p.TouchModlist();

        var r = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "DisabledBroken") }, null, null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains("unscanned rather than absent", r.Error);
        Assert.Contains(WriteSentences.PlaceSourceFolderUnreadable, r.Error);
        Assert.Contains("Broken.bsa", r.Error);
    }
}

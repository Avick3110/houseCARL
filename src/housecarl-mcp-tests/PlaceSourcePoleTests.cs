using System.Text.RegularExpressions;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;
using static HousecarlMcpTests.PlaceInstance;

namespace HousecarlMcpTests;

/// <summary>Two enabled mods supplying the same FaceGen head with different bytes, named hostilely: an apostrophe, and
/// a mod literally called "winner". Which one wins is read from the resolver, never assumed.</summary>
public sealed class PlaceSourcePoleWorld : IDisposable
{
    public const string ModA = "JK's Skyrim";
    public const string ModB = "winner";
    public static readonly byte[] ABytes = { 0xA1, 0xA1, 0xA1 };
    public static readonly byte[] BBytes = { 0xB2, 0xB2 };

    public PlaceInstance P { get; } = new();
    public LoadOrderService Svc { get; }
    public string? WinnerName { get; }
    public string LoserName { get; }
    public byte[] WinnerBytes { get; }
    public byte[] LoserBytes { get; }

    public PlaceSourcePoleWorld()
    {
        Loose(P.Mod(ModA), FacegenRel, ABytes);
        Loose(P.Mod(ModB), FacegenRel, BBytes);
        P.ProfileWithDummy(ModA, "+" + ModA, "+" + ModB);
        Svc = P.Open();
        WinnerName = Svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit?.Winner?.Source;
        LoserName = WinnerName == ModA ? ModB : ModA;
        WinnerBytes = WinnerName == ModA ? ABytes : BBytes;
        LoserBytes = WinnerName == ModA ? BBytes : ABytes;
    }

    public void Dispose() => P.Dispose();
}

/// <summary>source= as a Data-relative path, and source_provider= choosing whose copy: a named provider is read even when
/// another copy wins, *winner reads the winner, a miss is refused with nothing substituted, a different source path is a
/// rename, and the refusal's own provider tokens are accepted back.</summary>
[Trait("tier", "integration")]
public sealed class PlaceSourcePoleTests : IClassFixture<PlaceSourcePoleWorld>
{
    readonly PlaceSourcePoleWorld _w;
    public PlaceSourcePoleTests(PlaceSourcePoleWorld w) => _w = w;

    PlaceOutcome Place(string dest, string? source, string? provider) =>
        _w.Svc.PlaceAssets(new[] { new PlaceRequest(dest, source, provider) }, null, null);

    static byte[]? Bytes(PlaceOutcome o, string rel) => PlacedAt(o, rel) is { } p ? File.ReadAllBytes(p) : null;

    // Probe I: "the fixture is genuinely contended and one copy wins".
    [Fact]
    public void TheFixtureIsContendedAndOneCopyWins()
    {
        Assert.Contains(_w.WinnerName, new[] { PlaceSourcePoleWorld.ModA, PlaceSourcePoleWorld.ModB });
    }

    // Probe I: "a provider literally named 'winner' is reachable by its own name — the sigil un-reserved it".
    [Fact]
    public void AModNamedWinnerIsReachedByItsOwnName()
    {
        var o = Place(FacegenRel, FacegenRel, "winner");

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(PlaceSourcePoleWorld.BBytes, Bytes(o, FacegenRel));
    }

    // Probe I1: "source_provider='<loser>' places THAT provider's bytes though '<winner>' wins the VFS" and "…and
    // specifically NOT the winner's bytes".
    [Fact]
    public void ANamedProviderThatLosesTheVfsIsReadAnyway()
    {
        var o = Place(FacegenRel, FacegenRel, _w.LoserName);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(_w.LoserBytes, Bytes(o, FacegenRel));
        Assert.NotEqual(_w.WinnerBytes, Bytes(o, FacegenRel));
    }

    // Probe I1b: "a MEMBER's source_provider= carries through to the read (the loser's bytes, not the winner's)".
    [Fact]
    public void AMembersPoleReachesTheReadThroughTheTool()
    {
        var text = PlaceTools.Place(_w.Svc, new[]
            { new PlaceTarget { Path = FacegenRel, Source = FacegenRel, SourceProvider = _w.LoserName } }, patch: "WireMember");

        Assert.Equal(_w.LoserBytes, File.ReadAllBytes(_w.P.PlacedFileFrom(text, FacegenRel)!));
    }

    // Probe I1b: "the SET-level source_provider= fans onto a member that names none".
    [Fact]
    public void TheSetLevelPoleReachesAMemberThatNamesNone()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = FacegenRel, Source = FacegenRel } },
            source_provider: _w.LoserName, patch: "WireSet");

        Assert.Equal(_w.LoserBytes, File.ReadAllBytes(_w.P.PlacedFileFrom(text, FacegenRel)!));
    }

    // Probe I1c: "the refusal offers exactly the two provider names as delimited tokens, apostrophe and all" and "a
    // token copied verbatim out of the refusal is accepted by source_provider=".
    [Fact]
    public void EveryProviderTokenInTheAmbiguityRefusalIsAcceptedBack()
    {
        var refusal = Place(FacegenRel, FacegenRel, null).Results[0].Error!;
        var tokens = Regex.Matches(refusal, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.Equal(new[] { PlaceSourcePoleWorld.ModA, PlaceSourcePoleWorld.ModB }.Order(), tokens.Order());
        foreach (var token in tokens)
        {
            var r = Place(FacegenRel, FacegenRel, token).Results[0];
            Assert.True(r.Placed, $"'{token}': {r.Error}");
        }
    }

    // Probe I2: "source_provider=*winner places the CURRENT winner's bytes".
    [Fact]
    public void TheWinnerPoleReadsTheCurrentWinner()
    {
        var o = Place(FacegenRel, FacegenRel, AssetSourceChoice.WinnerToken);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(_w.WinnerBytes, Bytes(o, FacegenRel));
    }

    // Probe I3: "a named provider that does not supply the path is REFUSED with nothing substituted" and "…and the
    // providers that DO supply it are named".
    [Fact]
    public void ANamedProviderThatDoesNotSupplyThePathIsRefusedAndTheSuppliersNamed()
    {
        var r = Place(FacegenRel, FacegenRel, "NoSuchMod").Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceNoSubstitute, r.Error);
        Assert.Contains(PlaceSourcePoleWorld.ModA, r.Error);
        Assert.Contains(PlaceSourcePoleWorld.ModB, r.Error);
    }

    // Probe I4: "the rename fixture's destination really is a different path from its source", "the source file's bytes
    // land under the DESTINATION's name — a rename", "the render LEADS with the source file", and "the SOURCE path is
    // not also written — a rename places one file, not two".
    [Fact]
    public void ADifferentSourcePathIsARenameThatLeadsWithTheSource()
    {
        var destRel = FaceGenPath.For(FormKey.Factory("000FFF:Dummy.esp"), FaceGenSlot.Mesh);
        Assert.NotEqual(FacegenRel, destRel);

        var o = Place(destRel, FacegenRel, _w.LoserName);

        var r = o.Results[0];
        Assert.True(r.Placed, r.Error);
        Assert.Equal(_w.LoserBytes, Bytes(o, destRel));
        Assert.StartsWith(FacegenRel, r.SourceDesc, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(o.ModFolder!, FacegenRel)));
    }

    // Probe I5: "an absent Data-relative SOURCE is refused naming that path".
    [Fact]
    public void AnAbsentDataRelativeSourceIsRefusedNamingIt()
    {
        var r = Place(FacegenRel, @"meshes\nope\absent.nif", null).Results[0];

        Assert.False(r.Placed);
        Assert.Contains("provides the source", r.Error);
        Assert.Contains(@"meshes\nope\absent.nif", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // Probe I6: "source_provider= with an ON-DISK source is refused, never silently ignored".
    [Fact]
    public void APoleOnAnOnDiskSourceIsRefused()
    {
        var onDisk = _w.P.Scratch("i6-source.nif", new byte[] { 7, 7 });

        var r = Place(FacegenRel, onDisk, PlaceSourcePoleWorld.ModA).Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourceProviderNeedsRelPath, r.Error);
    }

    // Probe I7: "source= '<spelling>' resolves through the VFS under the named pole", for a leading backslash, a
    // leading slash and a quoted path.
    [Theory]
    [InlineData("backslash")]
    [InlineData("slash")]
    [InlineData("quoted")]
    public void ALeadingSeparatorOrQuotedSourceResolvesThroughTheVfs(string shape)
    {
        var spelling = shape switch
        {
            "backslash" => @"\" + FacegenRel,
            "slash" => "/" + FacegenRel.Replace('\\', '/'),
            _ => "\"" + FacegenRel + "\"",
        };

        var o = Place(FacegenRel, spelling, _w.LoserName);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(_w.LoserBytes, Bytes(o, FacegenRel));
    }

    // Probe I8: "source_provider= with NO source= reads the destination path from the named provider" and "…and it is
    // NOT reported as a rename".
    [Fact]
    public void APoleWithNoSourceReadsTheDestinationFromThatProvider()
    {
        var o = Place(FacegenRel, null, _w.LoserName);

        var r = o.Results[0];
        Assert.True(r.Placed, r.Error);
        Assert.Equal(_w.LoserBytes, Bytes(o, FacegenRel));
        Assert.False(r.SourceDesc!.StartsWith(FacegenRel, StringComparison.OrdinalIgnoreCase), r.SourceDesc);
    }

    // Probe I8: "source= '<spelling>' equals the destination, so no rename prefix", for the same path, upper-cased, and
    // with forward slashes.
    [Theory]
    [InlineData("same")]
    [InlineData("upper")]
    [InlineData("slash")]
    public void ASourceEqualToTheDestinationIsNotARename(string shape)
    {
        var spelling = shape switch
        {
            "same" => FacegenRel,
            "upper" => FacegenRel.ToUpperInvariant(),
            _ => FacegenRel.Replace('\\', '/'),
        };

        var r = Place(FacegenRel, spelling, _w.LoserName).Results[0];

        Assert.True(r.Placed, r.Error);
        Assert.False(r.SourceDesc!.StartsWith(spelling, StringComparison.OrdinalIgnoreCase), r.SourceDesc);
        Assert.False(r.SourceDesc.StartsWith(FacegenRel, StringComparison.OrdinalIgnoreCase), r.SourceDesc);
    }

    // Probe I9: "the winner token is case-insensitive ('*WINNER')" and "…and so is a provider name".
    [Fact]
    public void ThePoleTokenAndProviderNamesMatchCaseInsensitively()
    {
        var o = Place(FacegenRel, FacegenRel, "*WINNER");
        var up = Place(FacegenRel, FacegenRel, _w.LoserName.ToUpperInvariant());

        Assert.Equal(_w.WinnerBytes, Bytes(o, FacegenRel));
        Assert.Equal(_w.LoserBytes, Bytes(up, FacegenRel));
    }

    // Probe I10: "a named-provider miss states how the winner pole is spelled".
    [Fact]
    public void ANamedProviderMissSaysHowTheWinnerPoleIsSpelled()
    {
        var r = Place(FacegenRel, FacegenRel, "NoSuchModAtAll").Results[0];

        Assert.False(r.Placed);
        Assert.Contains(WriteSentences.PlaceSourcePoleSpelling, r.Error);
    }
}

/// <summary>A base archive named as the source pole, and a Data-relative source that merely ends in '.bsa'.</summary>
[Trait("tier", "integration")]
public sealed class PlaceArchivePoleTests
{
    static readonly byte[] LooseBytes = { 0x4C, 0x4C };

    /// <summary>A base archive, FixtureA.bsa in Data and listed in Skyrim.ini, contending with a loose copy that wins.</summary>
    static (PlaceInstance P, LoadOrderService Svc, string Loose) Build()
    {
        var p = new PlaceInstance();
        File.WriteAllBytes(Path.Combine(p.Data, "FixtureA.bsa"), FaceArchive());
        var loose = p.Mod("LooseFace");
        Loose(loose, FacegenRel, LooseBytes);
        File.WriteAllText(Path.Combine(loose, "Dummy.esp"), "x");
        p.Profile(new[] { "Dummy.esp" }, new[] { "*Dummy.esp" }, new[] { "+LooseFace" }, archiveList: "FixtureA.bsa");
        return (p, p.Open(), loose);
    }

    // Probe K: "the fixture really does have a BSA provider in the VFS", "…and the LOOSE copy wins, so naming the BSA is
    // naming the loser", and "source_provider= a BSA filename extracts THAT archive's entry, not the winning loose copy".
    [Fact]
    public void NamingABaseArchiveExtractsItsEntryThoughALooseCopyWins()
    {
        var (p, svc, _) = Build();
        using var _p = p;
        var hit = svc.AssetStatus(new[] { FacegenRel }).Results[0].Hit;
        Assert.Contains(hit!.Providers, pr => pr.Kind == AssetKind.Bsa);
        Assert.Equal(AssetKind.Loose, hit.Winner?.Kind);

        var o = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "FixtureA.bsa") }, null, null);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(ArchiveFaceBytes, File.ReadAllBytes(PlacedAt(o, FacegenRel)!));
    }

    // Probe K: "the BSA provider is listed as a delimited name with its kind outside the delimiters".
    [Fact]
    public void TheMissRefusalListsTheArchiveAsADelimitedName()
    {
        var (p, svc, _) = Build();
        using var _p = p;

        var refusal = svc.PlaceAssets(new[] { new PlaceRequest(FacegenRel, FacegenRel, "NopeMod") }, null, null).Results[0].Error!;

        Assert.Contains("\"FixtureA.bsa\" (BSA)", refusal);
    }

    // Probe K: "a Data-relative source ending '.bsa' resolves through the VFS, not against the process CWD".
    [Fact]
    public void ADataRelativeSourceEndingInBsaResolvesThroughTheVfs()
    {
        var (p, svc, loose) = Build();
        using var _p = p;
        const string relBsa = @"meshes\hcprobe\thing.bsa";
        Loose(loose, relBsa, new byte[] { 0x7A, 0x7A });
        p.TouchModlist();

        var o = svc.PlaceAssets(new[] { new PlaceRequest(@"meshes\hcprobe\copy.nif", relBsa, "LooseFace") }, null, null);

        Assert.True(o.Results[0].Placed, o.Results[0].Error);
        Assert.Equal(new byte[] { 0x7A, 0x7A }, File.ReadAllBytes(PlacedAt(o, @"meshes\hcprobe\copy.nif")!));
    }
}

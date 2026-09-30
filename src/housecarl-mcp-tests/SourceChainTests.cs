using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using W = HousecarlMcpTests.SourceChainWorld;

namespace HousecarlMcpTests;

/// <summary>
/// The ordered source universe a walk reads through: first hit wins and says which arm produced it, a miss names every
/// arm consulted, a faulting arm stops the chain, the poles and plugin names never collide, and a bad element refuses
/// by name. Drives the real chain builder over <see cref="SourceChainWorld"/>. Migrated from the source-chain-guard probe.
/// </summary>
[Trait("tier", "integration")]
public sealed class SourceChainTests : IClassFixture<SourceChainWorld>
{
    readonly SourceChainWorld _w;
    public SourceChainTests(SourceChainWorld w) => _w = w;

    SourceChain Built(params string[] poles) => _w.Chain(poles, (chain, err) =>
    {
        Assert.Null(err);
        return chain!;
    });

    string? Refusal(params string[] poles) => _w.Chain(poles, (chain, err) =>
    {
        Assert.Null(chain);
        return err;
    });

    string? NameVia(FormKey fk, params string[] poles) => _w.Chain(poles, (chain, err) =>
    {
        Assert.Null(err);
        return W.NameOf(chain!.Fetch(fk));
    });

    // length-1 ['winner'] builds; is the degenerate single-pole chain
    [Fact]
    public void TheWinnerPoleAloneIsTheSinglePoleChain()
        => Assert.True(_w.Chain(new[] { SourcePoles.Winner }, (c, e) => e is null && c is { IsSinglePole: true }));

    // ['winner'] resolves the ACTIVE ORDER's winning version (Over.esp's 'O', not Base's 'B')
    [Fact]
    public void TheWinnerPoleResolvesTheActiveOrdersWinner()
        => Assert.Equal("O", NameVia(W.Shared, SourcePoles.Winner));

    // ['winner'] resolves a record only the base master has
    [Fact]
    public void TheWinnerPoleResolvesARecordOnlyTheMasterHas()
        => Assert.Equal("B", NameVia(W.BaseOnly, SourcePoles.Winner));

    // ['winner'] MISSES a record no ACTIVE plugin carries (the disabled donor's own record)
    [Fact]
    public void TheWinnerPoleMissesADisabledPluginsOwnRecord()
        => Assert.True(_w.Chain(new[] { SourcePoles.Winner }, (c, e) => c!.Fetch(W.DonorOnly).IsMiss));

    // length-1 ['Donor.esp'] builds — a DISABLED plugin is a legal source; resolved through the off-order FILE arm
    [Fact]
    public void ADisabledPluginIsASourceThroughTheFileArm()
        => Assert.Equal(SourceArmKind.File, _w.Chain(new[] { "Donor.esp" }, (c, e) =>
        {
            Assert.Null(e);
            return c!.Arms[0].Kind;
        }));

    // ['Donor.esp'] resolves THAT FILE's own version ('D'), not the active winner's
    [Fact]
    public void ANamedFileResolvesItsOwnVersion()
        => Assert.Equal("D", NameVia(W.Shared, "Donor.esp"));

    // ['Donor.esp'] resolves a record only that file defines
    [Fact]
    public void ANamedFileResolvesARecordOnlyItDefines()
        => Assert.Equal("D", NameVia(W.DonorOnly, "Donor.esp"));

    // a 2-element chain is not the single-pole shape
    [Fact]
    public void ATwoElementChainIsNotTheSinglePoleShape()
        => Assert.False(Built("Over.esp", "Donor.esp").IsSinglePole);

    // FIRST HIT WINS: a record BOTH arms carry resolves to arm 0's version ('O');
    // the hit NAMES which arm produced it (index 0, 'Over.esp') — the readback's provenance
    [Fact]
    public void TheFirstHitWinsAndNamesItsArm()
        => _w.Chain(new[] { "Over.esp", "Donor.esp" }, (c, e) =>
        {
            var hit = c!.Fetch(W.Shared);
            Assert.Equal("O", W.NameOf(hit));
            Assert.Equal(0, hit.Hit!.ArmIndex);
            Assert.Equal("Over.esp", hit.Hit.Arm.Spelling);
            return 0;
        });

    // FALLBACK: a record only arm 1 carries resolves to arm 1, and says so
    [Fact]
    public void ARecordOnlyALaterArmHasFallsThroughToIt()
        => _w.Chain(new[] { "Over.esp", "Donor.esp" }, (c, e) =>
        {
            var fell = c!.Fetch(W.DonorOnly);
            Assert.Equal("D", W.NameOf(fell));
            Assert.Equal(1, fell.Hit!.ArmIndex);
            return 0;
        });

    // a record NO arm carries is a miss, not a fault; the MISS names EVERY arm consulted, in order
    [Fact]
    public void AMissNamesEveryArmConsultedInOrder()
        => _w.Chain(new[] { "Over.esp", "Donor.esp" }, (c, e) =>
        {
            var nowhere = new FormKey(W.BaseKey, 0x8FF);
            Assert.True(c!.Fetch(nowhere).IsMiss);
            Assert.Equal(new[] { "Over.esp", "Donor.esp" }, c.Miss(nowhere, "Npc.HeadParts").Consulted.Select(a => a.Spelling));
            return 0;
        });

    // ORDER IS THE SEMANTICS: reversing the arms reverses which version wins ('D')
    [Fact]
    public void ReversingTheArmsReversesTheWinner()
        => Assert.Equal("D", NameVia(W.Shared, "Donor.esp", "Over.esp"));

    // TRIPWIRE: the off-order file resolves the SAME body alone / first / second — §14
    [Fact]
    public void TheOffOrderFileResolvesTheSameBodyAtAnyPosition()
    {
        Assert.Equal("D", NameVia(W.Shared, "Donor.esp"));
        Assert.Equal("D", NameVia(W.Shared, "Donor.esp", "Over.esp"));
        Assert.Equal("D", _w.Chain(new[] { "Over.esp", "Donor.esp" }, (c, e) => (c!.Arms[1].Fetch(W.Shared) as INpcGetter)?.Name?.String));
    }

    static SourceChain Faulting(INpcGetter fallback) => new(new[]
    {
        new SourceArm("Faulty.esp", SourceArmKind.File, "file 'Faulty.esp'", _ => throw new InvalidOperationException("a record Mutagen cannot parse")),
        new SourceArm("Fallback.esp", SourceArmKind.File, "file 'Fallback.esp'", _ => fallback),
    });

    // a FAULTING arm produces a fault, and NOT a hit from the arm behind it (no silent substitution);
    // a fault is NOT reported as a miss — the two have different remedies
    [Fact]
    public void AFaultingArmStopsTheChainInsteadOfSubstituting()
    {
        var f = Faulting(_w.BaseShared).Fetch(W.Shared, "Npc.HeadParts");
        Assert.NotNull(f.Fault);
        Assert.Null(f.Hit);
        Assert.False(f.IsMiss);
    }

    // the fault names the ARM and the CAUSE
    [Fact]
    public void TheFaultNamesTheArmAndTheCause()
    {
        var fault = Faulting(_w.BaseShared).Fetch(W.Shared, "Npc.HeadParts").Fault!;
        Assert.Equal(0, fault.ArmIndex);
        Assert.Equal("Faulty.esp", fault.Arm.Spelling);
        Assert.Contains("cannot parse", fault.Cause);
    }

    // previous_provider as an element REFUSES; the refusal names the token and WHY it has no meaning on a walk;
    // the refusal names the gap-report path; the refusal names WHICH element was bad
    [Fact]
    public void PreviousProviderIsRefusedByNameWithTheGapReportPath()
    {
        var err = Refusal("Over.esp", SourcePoles.PreviousProvider);
        Assert.Contains(SourcePoles.PreviousProvider, err);
        Assert.Contains("SUBJECT-relative", err);
        Assert.Contains("gap report", err);
        Assert.Contains("from_source[1]", err);
    }

    // a name nothing provides REFUSES; the refusal quotes the caller's own spelling
    [Fact]
    public void ANameNothingProvidesIsRefusedInTheCallersSpelling()
        => Assert.Contains("NoSuchPlugin.esp", Refusal("NoSuchPlugin.esp"));

    // CONTROL: a plugin actually named 'winner.esp' is a legal source; resolves that PLUGIN's own version ('W'),
    // not the pole's winner ('OW'); resolved as named(plugin), NOT as the winner pole
    [Fact]
    public void APluginNamedWinnerEspResolvesAsItself()
        => _w.Chain(new[] { "winner.esp" }, (c, e) =>
        {
            Assert.Null(e);
            Assert.Equal("W", W.NameOf(c!.Fetch(W.WinnerOnly)));
            Assert.Equal(SourceArmKind.ActiveOrder, c.Arms[0].Kind);
            Assert.Equal("winner.esp", c.Arms[0].Spelling);
            return 0;
        });

    // bare 'winner' means the POLE even with a plugin named winner.esp installed and active; it resolves the
    // load-order winner, including on winner.esp's OWN record ('OW', not 'W')
    [Fact]
    public void TheBareWinnerTokenIsStillThePole()
        => _w.Chain(new[] { SourcePoles.Winner }, (c, e) =>
        {
            Assert.Equal(SourcePoles.Winner, c!.Arms[0].Spelling);
            Assert.Equal("O", W.NameOf(c.Fetch(W.Shared)));
            Assert.Equal("OW", W.NameOf(c.Fetch(W.WinnerOnly)));
            return 0;
        });

    // an extensionless plugin spelling ('Donor') REFUSES; the refusal names the spelling that failed
    // (its control, 'Donor.esp' resolving, is ANamedFileResolvesARecordOnlyItDefines)
    [Fact]
    public void AnExtensionlessPluginSpellingIsRefused()
        => Assert.Contains("source 'Donor' is not in the load order", Refusal("Donor"));

    // an EMPTY source list refuses at the caller layer; the empty-list refusal names both element kinds
    [Fact]
    public void AnEmptySourceListIsRefusedNamingBothKinds()
    {
        var err = Refusal();
        Assert.Contains("winner", err);
        Assert.Contains("plugin filename", err);
    }

    // a BLANK element refuses naming its index, never silently dropped
    [Fact]
    public void ABlankElementIsRefusedNamingItsIndex()
        => Assert.Contains("from_source[1]", Refusal("Over.esp", "  "));
}

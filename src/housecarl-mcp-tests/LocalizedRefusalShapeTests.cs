using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The in-place refusal for a localized plugin (Stryker row T27): every arrangement has its own wording and none
/// falls to the generic arm; an unreadable file ends on the retry remedy; a lane's clause is appended; and the
/// archive arrangement names a second location only when one exists.
/// </summary>
[Trait("tier", "unit")]
public sealed class LocalizedRefusalShapeTests
{
    static LocalizedAssessment Assessment(LocalizedShape shape, string[]? languages = null, string[]? gameData = null) =>
        new(shape, languages ?? Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>(), gameData ?? Array.Empty<string>(),
            shape == LocalizedShape.BsaEmbedded ? "HcLoc - Main.bsa" : null, false, false);

    public static TheoryData<LocalizedShape> EveryShape()
    {
        var data = new TheoryData<LocalizedShape>();
        foreach (var s in Enum.GetValues<LocalizedShape>()) data.Add(s);
        return data;
    }

    [Theory, MemberData(nameof(EveryShape))]
    public void EveryShapeHasItsOwnWording(LocalizedShape shape)
    {
        var body = LocalizedTargetUnsupportedException.ShapeBody(Assessment(shape));
        Assert.DoesNotContain("has no wording", body);
        Assert.DoesNotContain("has no account", body);
    }

    [Fact]
    public void AnUnreadableFileEndsOnTheRetryRemedy()
        => Assert.EndsWith(LocalizedTargetUnsupportedException.RemedyUnreadable,
            LocalizedTargetUnsupportedException.Shaped("HcLoc.esp", Assessment(LocalizedShape.Unreadable)));

    [Fact]
    public void ALaneClauseIsAppended()
        => Assert.EndsWith(" HcLaneClause",
            LocalizedTargetUnsupportedException.Shaped("HcLoc.esp", Assessment(LocalizedShape.LooseComplete), "HcLaneClause"));

    [Fact]
    public void AnArchiveWithLooseTablesBesideThePluginNamesTheStringsFolder()
        => Assert.Contains("Strings folder beside the plugin",
            LocalizedTargetUnsupportedException.WhereTheTextIs(Assessment(LocalizedShape.BsaEmbedded, languages: new[] { "English" })));

    [Fact]
    public void AnArchiveWithATableSetInGameDataNamesThatFolder()
        => Assert.Contains(@"Data\Strings folder",
            LocalizedTargetUnsupportedException.WhereTheTextIs(Assessment(LocalizedShape.BsaEmbedded, gameData: new[] { "English" })));

    [Fact]
    public void AnArchiveAloneNamesNoSecondLocation()
        => Assert.EndsWith("beside the plugin.",
            LocalizedTargetUnsupportedException.WhereTheTextIs(Assessment(LocalizedShape.BsaEmbedded)));
}

using Xunit;
using A = HousecarlMcpTests.LocalizedWriteGuardFixture.Arrangement;
using F = HousecarlMcpTests.LocalizedWriteGuardFixture;

namespace HousecarlMcpTests;

/// <summary>A destination houseCARL cannot classify — locked, absent, not localized on disk, or with a Strings folder
/// that will not list — refuses rather than being read as not-localized, and a file it never opened is not described
/// in a localized plugin's words. Moved from the <c>localized-write-guard</c> probe (undecidable destination,
/// unreadable says nothing about localization, unlistable Strings folder).</summary>
[Trait("tier", "integration")]
public sealed class LocalizedUnclassifiableDestinationTests
{
    /// <summary>The phrases that assert a localization state about the file.</summary>
    static readonly string[] LocalizedVocabulary =
    {
        "flagged LOCALIZED",
        "A localized plugin's text is not in the plugin",
        "does not edit a localized plugin in place",
        "its text lives in separate .STRINGS files",
    };

    static FileStream Hold(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    // Probe: a locked destination classifies as Unreadable, NOT as not-localized; the shared pre-flight refuses it
    // saying it could not read it; the in-place write at a LOCKED destination refuses; the plugin and its tables are
    // byte-identical afterwards; nothing was staged.
    [Fact]
    public void ALockedDestinationRefusesAndIsLeftAlone()
    {
        using var f = new F(A.LooseComplete);
        Assert.Equal(LocalizedShape.LooseComplete, LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape);
        var before = f.Snapshot();
        LocalizedShape locked;
        string? preflight, write;
        using (Hold(f.Plugin))
        {
            locked = LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape;
            preflight = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
            write = f.WriteInPlace(F.LocalizedModFor(f.Plugin), f.Plugin);
        }
        Assert.Equal(LocalizedShape.Unreadable, locked);
        Assert.Contains("could not read the file at that path", preflight);
        Assert.NotNull(write);
        Assert.Equal(7, before.Count);
        Assert.Equal(before, f.Snapshot());
        Assert.False(Directory.Exists(f.StagingDir));
    }

    // Probe: a readable NON-localized plugin still returns no refusal, so the pre-flight is not refusing everything.
    [Fact]
    public void AReadablePlainPluginGetsNoRefusal()
    {
        using var f = new F(A.LooseComplete);
        var plain = f.WritePlain("ZPlain.esp");
        Assert.Equal(LocalizedShape.NotLocalized, LocalizedStrings.Assess(plain, f.DataDir).Shape);
        Assert.Null(LocalizedStrings.RefusalFor(plain, "ZPlain.esp", f.DataDir));
    }

    // Probe: a localized in-place write to a path with no file at it refuses, saying so, and writes nothing there.
    [Fact]
    public void ALocalizedWriteToAnAbsentPathRefuses()
    {
        using var f = new F(A.LooseComplete);
        var missing = Path.Combine(f.ModDir, "ZAbsent.esp");
        var msg = f.WriteInPlace(F.LocalizedModFor(missing), missing);
        Assert.Contains("could not read the file at that path", msg);
        Assert.False(File.Exists(missing));
    }

    // Probe: a localized mod written over a NON-localized file refuses, naming that mismatch; the file is untouched.
    // Strengthened: untouched is checked by bytes, not length.
    [Fact]
    public void ALocalizedWriteOverAPlainFileRefusesNamingTheMismatch()
    {
        using var f = new F(A.LooseComplete);
        var plain = f.WritePlain("ZPlain.esp");
        var before = File.ReadAllBytes(plain);
        var msg = f.WriteInPlace(F.LocalizedModFor(plain), plain);
        Assert.Contains("does not read as a localized plugin", msg);
        Assert.Equal(before, File.ReadAllBytes(plain));
    }

    // Probe: an unreadable destination's refusal claims no localization state, and says it is a destination houseCARL
    // cannot classify; the SAME plugin unlocked IS described in that vocabulary. The remove lane's dead-end clause is
    // NOT appended to a target never read and IS appended to a localized one; the unreadable target gets the
    // check-what-holds-the-file remedy.
    [Fact]
    public void AFileNeverOpenedIsNotDescribedAsLocalized()
    {
        using var f = new F(A.LooseComplete);
        string? unreadable, unreadableRemove;
        using (Hold(f.Plugin))
        {
            unreadable = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
            unreadableRemove = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir,
                                                           LocalizedTargetUnsupportedException.RemoveNoEquivalent);
        }
        var localized = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir,
                                                    LocalizedTargetUnsupportedException.RemoveNoEquivalent);

        Assert.NotNull(unreadable);
        foreach (var v in LocalizedVocabulary) Assert.DoesNotContain(v, unreadable);
        Assert.Contains("cannot classify", unreadable);

        Assert.NotNull(localized);
        Assert.Contains(LocalizedVocabulary, v => localized.Contains(v, StringComparison.Ordinal));

        Assert.NotNull(unreadableRemove);
        Assert.DoesNotContain("no way to remove", unreadableRemove);
        Assert.Contains("no way to remove", localized);
        Assert.Contains("has the file open", unreadableRemove);
    }

    public static TheoryData<LocalizedShape> EveryShape()
    {
        var data = new TheoryData<LocalizedShape>();
        foreach (var s in Enum.GetValues<LocalizedShape>()) data.Add(s);
        return data;
    }

    /// <summary>May this shape's refusal assert that a plugin is localized? Exhaustive, so a shape added later answers false until it is decided here.</summary>
    static bool MayAssertLocalization(LocalizedShape shape) => shape switch
    {
        // The file's own header was read and the flag was set.
        LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
            or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly or LocalizedShape.Nowhere => true,
        // Reached only from the write, where the mod in hand is localized; no arrangement is described.
        LocalizedShape.NotLocalized => true,
        // The plugin's flag was read; only a folder could not be listed.
        LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable => true,
        // Nothing was read.
        LocalizedShape.Unreadable => false,
        _ => false,
    };

    // Probe: <shape> renders a body, and carries localized vocabulary only if its LOCALIZED flag was read. Walked
    // over the enum, so a shape added later that inherits another's words fails here.
    [Theory, MemberData(nameof(EveryShape))]
    public void OnlyAShapeWhoseFlagWasReadCarriesLocalizedVocabulary(LocalizedShape shape)
    {
        var a = new LocalizedAssessment(shape, Array.Empty<string>(), new Dictionary<string, IReadOnlyList<string>>(),
                                        Array.Empty<string>(), shape == LocalizedShape.BsaEmbedded ? "Z.bsa" : null,
                                        false, false);
        var body = LocalizedTargetUnsupportedException.ShapeBody(a);
        Assert.NotEmpty(body);
        Assert.Equal(MayAssertLocalization(shape), LocalizedVocabulary.Any(v => body.Contains(v, StringComparison.Ordinal)));
    }

    // Probe: an unlistable Strings folder classifies as its own shape, NOT as Nowhere; its refusal asserts no absence
    // over a folder houseCARL could not read and says the folder could not be read; with the deny lifted the same
    // folder classifies as the loose set it is.
    [Fact]
    public void AnUnlistableStringsFolderIsNotAFolderFoundEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;   // the deny ACE that makes a folder unlistable is Windows-only
        using var f = new F(A.LooseComplete);
        var strings = Path.Combine(f.ModDir, "Strings");
        var listed = LocalizedStrings.Assess(f.Plugin, f.DataDir);
        Assert.Equal(LocalizedShape.LooseComplete, listed.Shape);
        Assert.Equal(2, listed.Languages.Count);

        Assert.True(f.DenyListing(strings), "the deny-listing ACE did not take on this host — the fixture cannot be built");
        Assert.Equal(LocalizedShape.StringsFolderUnreadable, LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape);
        var msg = LocalizedStrings.RefusalFor(f.Plugin, "ZRef.esp", f.DataDir);
        Assert.NotNull(msg);
        Assert.DoesNotContain("no .STRINGS files beside it", msg);
        Assert.DoesNotContain("cannot find its text", msg);
        Assert.Contains("could not read the Strings folder", msg);
        f.LiftDeny(strings);

        Assert.Equal(LocalizedShape.LooseComplete, LocalizedStrings.Assess(f.Plugin, f.DataDir).Shape);
    }
}

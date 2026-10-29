using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2's file visibility on the loose layer: a file MO2 hides (a skip suffix such as .mohidden, or a path
/// under a skip directory such as .git) is not in the Data view, for a folder sweep, a census and a path asked by
/// name; the skip lists are read off ModOrganizer.ini; hidden FOLDERS, root\ and archives are unchanged.</summary>
[Trait("tier", "integration")]
public sealed class AssetVisibilityTests : IDisposable
{
    const string Shown = @"meshes\vis\shown.nif";
    const string Hidden = @"meshes\vis\hidden.nif.mohidden";
    const string Off = @"meshes\vis\other.nif.off";
    const string GitHead = @".git\HEAD";
    const string SvnEntry = @".svn\entries";
    const string HiddenFolderFile = @"interface\icons.mohidden\a.svg";
    const string RootFile = @"root\a.exe";

    readonly PlaceInstance _p = new();
    readonly string _mod;

    public AssetVisibilityTests()
    {
        _mod = _p.Mod("VisMod");
        foreach (var rel in new[] { Shown, Hidden, Off, GitHead, SvnEntry, HiddenFolderFile, RootFile })
            PlaceInstance.Loose(_mod, rel, "x"u8.ToArray());
        _p.Profile(Array.Empty<string>(), Array.Empty<string>(), new[] { "+VisMod" });
    }

    public void Dispose() => _p.Dispose();

    void Settings(string lines) => File.AppendAllText(Path.Combine(_p.Inst, "ModOrganizer.ini"), "[Settings]\r\n" + lines + "\r\n");

    static IEnumerable<string> Under(HousecarlMcp.LoadOrderService svc, string folder)
        => svc.AssetStatus(Array.Empty<string>(), new[] { folder }).Results.Select(r => r.RelPath);

    static bool Exists(HousecarlMcp.LoadOrderService svc, string rel) => svc.AssetStatus(new[] { rel }).Results[0].Hit!.Exists;

    [Fact]
    public void AHiddenFileAndAGitFolderAreNotListedUnderTheirFolders()
    {
        var svc = _p.Open();

        Assert.Equal(new[] { Off, Shown }, Under(svc, "meshes"));
        Assert.Empty(Under(svc, ".git"));
    }

    [Fact]
    public void TheCensusDoesNotCountAHiddenFile()
    {
        var census = AssetTools.AssetStatus(_p.Open(), under: new[] { "meshes/vis" }, counts_only: true);

        Assert.Contains("counted=2 present=2", census);
    }

    [Fact]
    public void AHiddenFileAskedByNameIsAbsent()
    {
        var svc = _p.Open();

        Assert.False(Exists(svc, Hidden));
        Assert.True(Exists(svc, Shown));
    }

    [Fact]
    public void ACustomSuffixListReplacesTheDefault()
    {
        Settings("skip_file_suffixes=.off");
        var svc = _p.Open();

        Assert.False(Exists(svc, Off));
        Assert.True(Exists(svc, Hidden));
    }

    [Fact]
    public void ACustomDirectoryListReplacesTheDefault()
    {
        Settings("skip_directories=.svn, .hg");
        var svc = _p.Open();

        Assert.Empty(Under(svc, ".svn"));
        Assert.Equal(new[] { GitHead }, Under(svc, ".git"));
    }

    [Fact]
    public void AnEmptyItemHidesNothing()
    {
        Settings("skip_file_suffixes=.off,");
        var svc = _p.Open();

        Assert.True(Exists(svc, Shown));
        Assert.False(Exists(svc, Off));
    }

    [Fact]
    public void AnEmptyListSkipsNothing()
    {
        Settings("skip_file_suffixes=@Invalid()");
        var svc = _p.Open();

        Assert.True(Exists(svc, Hidden));
    }

    [Fact]
    public void AnUnreadableListKeepsTheDefaultAndSaysSo()
    {
        Settings("skip_file_suffixes=@Variant(\\0\\0\\0\\x7f)");
        var data = _p.Open().AssetStatus(new[] { Hidden });

        Assert.False(data.Results[0].Hit!.Exists);
        Assert.Contains(data.Warnings, w => w.Contains("skip_file_suffixes", StringComparison.Ordinal));
    }

    [Fact]
    public void HidingAndUnhidingBetweenTwoCallsIsSeen()
    {
        var svc = _p.Open();
        Assert.True(Exists(svc, Shown));

        File.Move(Path.Combine(_mod, Shown), Path.Combine(_mod, Shown + ".mohidden"));
        Assert.False(Exists(svc, Shown));

        File.Move(Path.Combine(_mod, Shown + ".mohidden"), Path.Combine(_mod, Shown));
        Assert.True(Exists(svc, Shown));
    }

    // Unchanged: USVFS skips file suffixes and directory names only, so a folder named *.mohidden and Root Builder's
    // root\ are real VFS paths.
    [Fact]
    public void AHiddenFolderAndRootStayListed()
    {
        var svc = _p.Open();

        Assert.Equal(new[] { HiddenFolderFile }, Under(svc, "interface"));
        Assert.Equal(new[] { RootFile }, Under(svc, "root"));
        Assert.True(Exists(svc, HiddenFolderFile));
    }
}

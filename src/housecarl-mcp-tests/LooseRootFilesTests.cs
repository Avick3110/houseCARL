using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What <see cref="AssetResolver.AssetView.LooseRootFiles"/> answers for each kind of root name.</summary>
[Trait("tier", "unit")]
public sealed class LooseRootFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-looseroot-" + Guid.NewGuid().ToString("N"));
    readonly AssetResolver _r;

    public LooseRootFilesTests()
    {
        string overwrite = Path.Combine(_root, "overwrite"), mods = Path.Combine(_root, "mods"), data = Path.Combine(_root, "Data");
        foreach (var d in new[] { overwrite, Path.Combine(mods, "ModA"), Path.Combine(mods, "Data"), data })
            Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(mods, "ModA", "A.esp"), "x");
        File.WriteAllText(Path.Combine(mods, "Data", "Shadow.esp"), "x");
        File.WriteAllText(Path.Combine(data, "Game.esm"), "x");
        // "Gone" is enabled but has no folder on disk.
        _r = AssetResolver.Build(overwrite, mods, data, new[] { "ModA", "Data", "Gone" }, Array.Empty<ActiveArchive>());
    }

    public void Dispose()
    {
        _r.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AModRootListsItsOwnTopLevelFilesWithoutCountingAWarmListing()
    {
        var before = _r.WarmListingCount;
        Assert.Equal(new[] { "A.esp" }, _r.Capture().LooseRootFiles("ModA"));
        Assert.Equal(before, _r.WarmListingCount);
    }

    [Fact]
    public void TheDataNameIsTheGameRootEvenWhenAModFolderIsNamedData()
        => Assert.Equal(new[] { "Game.esm" }, _r.Capture().LooseRootFiles("Data"));

    [Fact]
    public void AnEnabledModWhoseFolderIsProvedAbsentShipsNothingAndIsNotAReadFailure()
    {
        var view = _r.Capture();
        Assert.Empty(view.LooseRootFiles("Gone")!);
        Assert.Empty(view.RootFailures);
    }

    [Fact]
    public void ANameThatIsNoLooseRootShipsNothing()
        => Assert.Empty(_r.Capture().LooseRootFiles("NoSuchMod")!);
}

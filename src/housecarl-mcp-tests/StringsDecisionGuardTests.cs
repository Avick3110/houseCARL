using Xunit;

namespace HousecarlMcpTests;

/// <summary>The strings redirect decision: which folder shapes carry this plugin's own strings, and the game-Data
/// fallback being Skyrim.esm's folder (HCBR 2026-06-24). Migrated from <c>strings-decision-guard</c>; the shapes
/// <c>LocalizedStringsSourceTests</c> already pins (bare folder, own loose table, neighbour's table, unreadable
/// .bsa) are not repeated.</summary>
[Trait("tier", "unit")]
public sealed class StringsDecisionGuardTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-strings-decision-" + Guid.NewGuid().ToString("N"));

    public StringsDecisionGuardTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    string Plugin(string folder, string name)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    // empty Strings folder -> no own strings (redirect).
    [Fact]
    public void AnEmptyStringsFolderIsNotAStringsSource()
    {
        var plugin = Plugin("loose-empty", "Mod.esp");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(plugin)!, "Strings"));
        Assert.False(LoadOrderResolver.FolderHasOwnStrings(plugin));
    }

    // Strings folder + .bsa -> has own strings (no redirect).
    [Fact]
    public void AnOwnTableBesideAnArchiveIsAStringsSource()
    {
        var plugin = Plugin("both", "M.esp");
        var dir = Path.GetDirectoryName(plugin)!;
        Directory.CreateDirectory(Path.Combine(dir, "Strings"));
        File.WriteAllText(Path.Combine(dir, "Strings", "M_English.STRINGS"), "x");
        File.WriteAllText(Path.Combine(dir, "M.bsa"), "x");
        Assert.True(LoadOrderResolver.FolderHasOwnStrings(plugin));
    }

    // nonexistent folder -> defensive 'has own' (no redirect on a bad read).
    [Fact]
    public void AMissingFolderKeepsTheUnchangedOpen()
        => Assert.True(LoadOrderResolver.FolderHasOwnStrings(Path.Combine(_root, "does-not-exist", "X.esp")));

    // ComputeDataDir -> resolved Skyrim.esm's dir.
    [Fact]
    public void TheDataFolderIsSkyrimEsmsFolder()
    {
        var data = Path.Combine(_root, "Data");
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Update.esm"] = 0, ["Skyrim.esm"] = 1 };
        var paths = new[] { Path.Combine(_root, "x", "Update.esm"), Path.Combine(data, "Skyrim.esm") };
        Assert.Equal(data, LoadOrderResolver.ComputeDataDir(names, paths), ignoreCase: true);
    }

    // ComputeDataDir -> case-insensitive Skyrim.esm key.
    [Fact]
    public void ALowerCaseSkyrimKeyIsStillFound()
    {
        var data = Path.Combine(_root, "Data");
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["skyrim.esm"] = 0 };
        Assert.Equal(data, LoadOrderResolver.ComputeDataDir(names, new[] { Path.Combine(data, "Skyrim.esm") }), ignoreCase: true);
    }

    // ComputeDataDir -> null when no Skyrim.esm.
    [Fact]
    public void WithoutSkyrimEsmThereIsNoDataFolder()
    {
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Dragonborn.esm"] = 0 };
        Assert.Null(LoadOrderResolver.ComputeDataDir(names, new[] { Path.Combine(_root, "Data", "Dragonborn.esm") }));
    }
}

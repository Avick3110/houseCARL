using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>CompileTools.BuildImports</c>, <c>VanillaSourceDir</c> and <c>NeedsModlistScan</c> on a fabricated game layout (migrated
/// from the <c>import-order-guard</c> probe, arm 1): the script's own folder first, caller dirs next, auto-discovered modlist
/// dirs after those, vanilla last, case-insensitive dedup and quote-trim.
/// </summary>
[Trait("tier", "unit")]
public sealed class ImportOrderBuildImportsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-import-order-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _compiler, _noSrcCompiler, _vanilla, _scriptDir, _userA, _userB, _autoA, _autoB;

    public ImportOrderBuildImportsTests()
    {
        _compiler = Path.Combine(_root, "game", "Papyrus Compiler", "PapyrusCompiler.exe");
        _noSrcCompiler = Path.Combine(_root, "nogame", "Papyrus Compiler", "PapyrusCompiler.exe");
        _vanilla = Path.Combine(_root, "game", "Data", "Source", "Scripts");
        _scriptDir = Path.Combine(_root, "work");
        _userA = Path.Combine(_root, "skse src");   // spaced path on purpose
        _userB = Path.Combine(_root, "other");
        _autoA = Path.Combine(_root, "mods", "SKSE", "Source", "Scripts");
        _autoB = Path.Combine(_root, "mods", "PapyrusUtil", "Source", "Scripts");
        foreach (var d in new[] { Path.GetDirectoryName(_compiler)!, Path.GetDirectoryName(_noSrcCompiler)!,
                                  _vanilla, _scriptDir, _userA, _userB, _autoA, _autoB })
            Directory.CreateDirectory(d);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    static void Order(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Assert.Equal(expected, actual, StringComparer.OrdinalIgnoreCase);

    // Probe: "4 dirs assembled", "the script's own folder is FIRST", "quoted spaced import dir survives (quote-trim)",
    // "vanilla auto-import is LAST", "every caller dir outranks the vanilla auto-import".
    [Fact]
    public void OwnFolderFirstThenTheQuoteTrimmedCallerDirsThenVanillaLast() =>
        Order(new[] { _scriptDir, _userA, _userB, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, $"\"{_userA}\";{_userB}"));

    // Probe: "case-insensitive dedup holds".
    [Fact]
    public void TheOwnFolderPassedAgainInAnotherCaseIsDedupedCaseInsensitively() =>
        Order(new[] { _scriptDir, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, _scriptDir.ToUpperInvariant() + ";" + _vanilla));

    // Probe: "re-passed vanilla stays LAST; the caller's real dir keeps the caller slot" (PR #46 review pin).
    [Fact]
    public void AReEnteredVanillaDirStaysLastBehindTheCallersRealDir() =>
        Order(new[] { _scriptDir, _userB, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, _vanilla + ";" + _userB));

    // Probe: "no import_dirs → own folder + vanilla, vanilla last".
    [Fact]
    public void NoImportDirsGivesTheOwnFolderAndVanilla() =>
        Order(new[] { _scriptDir, _vanilla }, CompileTools.BuildImports(_scriptDir, _compiler, null));

    // Probe: "script inside the vanilla folder keeps its own-folder slot first".
    [Fact]
    public void AScriptInsideTheVanillaFolderKeepsTheOwnFolderSlot() =>
        Order(new[] { _vanilla, _userB }, CompileTools.BuildImports(_vanilla, _compiler, _userB));

    // Probe: "5 dirs assembled with 2 auto dirs", "own folder still FIRST with auto dirs in play", "the CALLER's import_dirs
    // outrank the auto-discovered modlist dirs", "auto dirs keep the order they were given", "the auto dirs outrank vanilla,
    // and vanilla is still LAST".
    [Fact]
    public void AutoDirsRankBetweenTheCallerAndVanillaInTheGivenOrder() =>
        Order(new[] { _scriptDir, _userB, _autoA, _autoB, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, _userB, new[] { _autoA, _autoB }));

    // Probe: "an auto dir that IS the vanilla folder (the Data loose root) is re-slotted to LAST, behind the mods".
    [Fact]
    public void AnAutoDirThatIsTheVanillaFolderIsReSlottedLast() =>
        Order(new[] { _scriptDir, _autoA, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, null, new[] { _vanilla, _autoA }));

    // Probe: "a dir passed BOTH by the caller and by the scan keeps the caller slot".
    [Fact]
    public void ADirPassedByTheCallerAndTheScanKeepsTheCallerSlot() =>
        Order(new[] { _scriptDir, _autoA, _autoB, _vanilla },
              CompileTools.BuildImports(_scriptDir, _compiler, _autoA, new[] { _autoB, _autoA }));

    // Probe: "no auto dirs (null or empty) → the original three-rank list, unchanged".
    [Fact]
    public void NullOrEmptyAutoDirsGiveTheThreeRankList()
    {
        var expected = new[] { _scriptDir, _userB, _vanilla };
        Order(expected, CompileTools.BuildImports(_scriptDir, _compiler, _userB, null));
        Order(expected, CompileTools.BuildImports(_scriptDir, _compiler, _userB, Array.Empty<string>()));
    }

    // Probe: "VanillaSourceDir resolves <game>\Data\Source\Scripts".
    [Fact]
    public void VanillaSourceDirIsTheGamesDataSourceScripts() =>
        Assert.Equal(_vanilla, CompileTools.VanillaSourceDir(_compiler));

    // Probe: "VanillaSourceDir returns null when the folder isn't there (no phantom import dir)"; and the "control: this
    // compiler has no vanilla sources beside it" of the rescue arm.
    [Fact]
    public void VanillaSourceDirIsNullWhenTheFolderIsMissing()
    {
        Assert.Null(CompileTools.VanillaSourceDir(_noSrcCompiler));
        Order(new[] { _scriptDir, _userB }, CompileTools.BuildImports(_scriptDir, _noSrcCompiler, _userB));
    }

    // Probe: "BuildImports takes the caller's RESOLVED vanilla dir rather than re-deriving its own". The auto list leaves
    // vanilla out, so the resolved dir is the only way it can reach the path.
    [Fact]
    public void AResolvedVanillaDirIsAppendedLastWhenTheCompilerHasNone() =>
        Order(new[] { _scriptDir, _autoA, _vanilla },
              CompileTools.BuildImports(_scriptDir, _noSrcCompiler, null, new[] { _autoA }, _vanilla));

    // Probe: "auto_imports=true scans", "…scans regardless of whether the compiler has vanilla beside it", "auto_imports=false
    // with the compiler's vanilla present does NOT touch the modlist", "auto_imports=false WITHOUT compiler vanilla still
    // reads the modlist".
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void TheModlistIsReadWhenAskedOrWhenTheCompilerHasNoVanilla(bool autoImports, bool compilerHasVanilla, bool reads) =>
        Assert.Equal(reads, CompileTools.NeedsModlistScan(autoImports, compilerHasVanilla ? _compiler : _noSrcCompiler));
}

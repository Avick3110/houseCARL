using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>CompileTools.PlanImports</c> on a fabricated game layout (migrated from the <c>import-order-guard</c> probe, arm 1): the
/// narrowing of the scanned folders, the provenance label each dir on the path gets, the counts derived from those labels,
/// and the vanilla slot when the compiler has no sources beside it.
/// </summary>
[Trait("tier", "unit")]
public sealed class ImportOrderPlanTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-import-plan-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _compiler, _noSrcCompiler, _vanilla, _scriptDir, _userB, _autoA, _autoB, _target;

    // The target names HCSkseHelper, which only autoA provides; autoB's script is named by nobody.
    public ImportOrderPlanTests()
    {
        _compiler = Path.Combine(_root, "game", "Papyrus Compiler", "PapyrusCompiler.exe");
        _noSrcCompiler = Path.Combine(_root, "nogame", "Papyrus Compiler", "PapyrusCompiler.exe");
        _vanilla = Path.Combine(_root, "game", "Data", "Source", "Scripts");
        _scriptDir = Path.Combine(_root, "work");
        _userB = Path.Combine(_root, "other");
        _autoA = Path.Combine(_root, "mods", "SKSE", "Source", "Scripts");
        _autoB = Path.Combine(_root, "mods", "PapyrusUtil", "Source", "Scripts");
        foreach (var d in new[] { Path.GetDirectoryName(_compiler)!, Path.GetDirectoryName(_noSrcCompiler)!,
                                  _vanilla, _scriptDir, _userB, _autoA, _autoB })
            Directory.CreateDirectory(d);
        _target = Path.Combine(_scriptDir, "HCPlanTarget.psc");
        File.WriteAllText(_target, "Scriptname HCPlanTarget extends Quest\n\nFunction Go()\n    HCSkseHelper.Ping()\nEndFunction\n");
        File.WriteAllText(Path.Combine(_autoA, "HCSkseHelper.psc"), "Scriptname HCSkseHelper\n");
        File.WriteAllText(Path.Combine(_autoB, "HCUnrelated.psc"), "Scriptname HCUnrelated\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    static PapyrusSourceRoot Root(string provider, string dir) => new(provider, dir, @"Source\Scripts");

    static string LabelOf(CompileTools.ImportPlan plan, string dir) =>
        plan.Entries.First(e => e.Dir.Equals(dir, StringComparison.OrdinalIgnoreCase)).Origin;

    CompileTools.ImportPlan Plan(string compiler, IReadOnlyList<string> caller, IReadOnlyList<PapyrusSourceRoot> roots,
                                 bool autoEnabled = true, string? gameDataSources = null) =>
        CompileTools.PlanImports(_target, _scriptDir, compiler, caller, roots, autoEnabled, importSetName: null, warning: null,
                                 gameDataSources: gameDataSources);

    // The own folder and the Data root both come back from the scan too; that is the collision the labels must settle.
    CompileTools.ImportPlan Labelled() =>
        Plan(_compiler, new[] { _userB },
             new[] { Root("Data", _vanilla), Root("SKSE", _autoA), Root("PapyrusUtil", _autoB), Root("Me", _scriptDir) });

    // Probe: "a scanned folder the script REFERENCES is kept", "a scanned folder the script never references is DROPPED".
    [Fact]
    public void AReferencedScannedFolderIsKeptAndAnUnreferencedOneIsDropped()
    {
        var dirs = Labelled().Dirs;
        Assert.Contains(_autoA, dirs, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(_autoB, dirs, StringComparer.OrdinalIgnoreCase);
    }

    // Probe: "the plan reports BOTH numbers — 3 folders scanned, 1 kept".
    [Fact]
    public void ThePlanReportsThreeScannedAndOneKept()
    {
        var plan = Labelled();
        Assert.Equal(3, plan.AutoScanned);
        Assert.Single(plan.AutoProviders);
    }

    // Probe: "a dir that is BOTH the script's own folder and a scanned mod is labelled the own folder", "the game's Data root
    // comes back from the scan as a mod, but is labelled VANILLA", "a scanned mod is labelled with its providing mod folder",
    // "a caller dir is labelled import_dirs=".
    [Fact]
    public void EachDirIsLabelledByTheSlotItHolds()
    {
        var plan = Labelled();
        Assert.Equal(CompileTools.ImportPlan.OwnFolder, LabelOf(plan, _scriptDir));
        Assert.Equal(CompileTools.ImportPlan.Vanilla, LabelOf(plan, _vanilla));
        Assert.Equal(CompileTools.ImportPlan.AutoPrefix + "SKSE", LabelOf(plan, _autoA));
        Assert.Equal(CompileTools.ImportPlan.CallerDirs, LabelOf(plan, _userB));
    }

    // Probe: "PlanImports' dirs ARE BuildImports' output over the KEPT folders". The probe compared against a second
    // BuildImports call, which PlanImports itself makes; the test states the path outright.
    [Fact]
    public void ThePlannedPathIsOwnFolderCallerKeptModThenVanilla() =>
        Assert.Equal(new[] { _scriptDir, _userB, _autoA, _vanilla }, Labelled().Dirs, StringComparer.OrdinalIgnoreCase);

    // Probe: "the summary counts are derived from the FINAL entries: 1 caller, 1 auto".
    [Fact]
    public void TheCountsCountOneCallerAndOneModNotDataOrTheOwnFolder()
    {
        var plan = Labelled();
        Assert.Equal(1, plan.CallerCount);
        Assert.Equal(new[] { "SKSE" }, plan.AutoProviders);
    }

    // Probe: "a DROPPED candidate that the caller passed is labelled import_dirs=, not MO2:<mod>", "…and the counts follow:
    // 2 caller dirs, 1 from the scan" (PR #296 review).
    [Fact]
    public void ADroppedCandidateTheCallerPassedIsTheCallers()
    {
        var plan = Plan(_compiler, new[] { _userB, _autoB }, new[] { Root("SKSE", _autoA), Root("PapyrusUtil", _autoB) });
        Assert.Equal(CompileTools.ImportPlan.CallerDirs, LabelOf(plan, _autoB));
        Assert.Equal(2, plan.CallerCount);
        Assert.Equal(new[] { "SKSE" }, plan.AutoProviders);
    }

    // Probe: "a KEPT candidate the caller also passed counts as the caller's".
    [Fact]
    public void AKeptCandidateTheCallerAlsoPassedIsTheCallers()
    {
        var plan = Plan(_compiler, new[] { _autoA }, new[] { Root("SKSE", _autoA) });
        Assert.Equal(1, plan.CallerCount);
        Assert.Empty(plan.AutoProviders);
    }

    // Probe: "a folder the walk matched stays in the REFERENCED count even when the caller also passed it", "…while the SLOT
    // attribution still credits the caller", "…and the summary quotes the walk's number, not the slot's" (PR #296 re-review).
    [Fact]
    public void AMatchedFolderTheCallerPassedIsReferencedButCreditedToTheCaller()
    {
        var plan = Plan(_compiler, new[] { _autoA }, new[] { Root("SKSE", _autoA), Root("PapyrusUtil", _autoB) });
        Assert.Equal(new[] { "SKSE" }, plan.Referenced);
        Assert.Empty(plan.AutoProviders);
        Assert.Equal(1, plan.CallerCount);
        Assert.Contains("matched 1 of 2", CompileTools.ImportSummary(plan));
    }

    // Probe: "the modlist's own Data sources fill the vanilla slot when the compiler-relative folder is missing", "…in the
    // vanilla slot: LAST, and labelled vanilla", "…and the plan does not claim vanilla is missing", "VanillaMissing agrees
    // with the assembled path".
    [Fact]
    public void TheModlistsDataSourcesFillTheVanillaSlotWhenTheCompilerHasNone()
    {
        var plan = Plan(_noSrcCompiler, Array.Empty<string>(), new[] { Root("SKSE", _autoA) }, gameDataSources: _vanilla);
        Assert.Equal(_vanilla, plan.Entries[^1].Dir, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CompileTools.ImportPlan.Vanilla, plan.Entries[^1].Origin);
        Assert.False(plan.VanillaMissing);
    }

    // Probe: "the auto_imports=false line states where the mods ARE NOT (the path), not what houseCARL did or didn't read".
    [Fact]
    public void TheAutoImportsOffSummarySaysTheModsAreNotOnThePath()
    {
        var text = CompileTools.ImportSummary(Plan(_compiler, Array.Empty<string>(), Array.Empty<PapyrusSourceRoot>(), autoEnabled: false));
        Assert.Contains("NOT on the import path", text);
        Assert.DoesNotContain("NOT scanned", text);
    }

    // Probe: "with vanilla sources genuinely nowhere, the plan says so rather than shipping a silently vanilla-less path",
    // "…and the render leads with it". The probe's second half (the Data folder absent from the path) could not fail, as
    // nothing handed that folder in; the test pins the whole path instead, so nothing stands in for vanilla.
    [Fact]
    public void WithVanillaNowhereThePlanAndTheSummarySaySo()
    {
        var plan = Plan(_noSrcCompiler, Array.Empty<string>(), new[] { Root("SKSE", _autoA) });
        Assert.True(plan.VanillaMissing);
        Assert.Equal(new[] { _scriptDir, _autoA }, plan.Dirs, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("NO vanilla Papyrus sources", CompileTools.ImportSummary(plan));
    }
}

using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Synthetic compile results and import plans for the <c>CompileTools.Render</c> tests (from the <c>compile-ergonomics-guard</c> probe); shaped like a real plan: own folder, caller dirs, auto mods, vanilla last.</summary>
static class CompileRenderFixtures
{
    public static readonly CompileResult Ok = new(
        Success: true, ObjectName: "MyScript", PexPath: @"C:\out\Scripts\MyScript.pex",
        Diagnostics: Array.Empty<PapyrusDiagnostic>(), Stdout: "", Stderr: "", ExitCode: 0, RunError: null);

    public static PapyrusDiagnostic Diag(string msg) => new(@"C:\mod\Scripts\HCMissingImports.psc", 8, 1, msg);

    /// <summary>The real captured missing-import avalanche: six unresolved-symbol diagnostics.</summary>
    public static readonly CompileResult MissingImports = new(
        Success: false, ObjectName: "HCMissingImports", PexPath: null,
        Diagnostics: new[]
        {
            Diag("unknown type po3_sksefunctions"),
            Diag("unknown type ski_configbase"),
            Diag("variable PO3_SKSEFunctions is undefined"),
            Diag("none is not a known user-defined type"),
            Diag("variable JValue is undefined"),
            Diag("variable JMap is undefined"),
        },
        Stdout: "", Stderr: "", ExitCode: 0, RunError: null);

    /// <summary>The real captured broken-script wording: four syntax diagnostics, none unresolved.</summary>
    public static readonly CompileResult SyntaxFail = new(
        Success: false, ObjectName: "HCBad", PexPath: null,
        Diagnostics: new[]
        {
            Diag("no viable alternative at character '@'"),
            Diag("no viable alternative at input 'papyrus'"),
            Diag("Unknown user flag papyrus"),
            Diag("missing EOF at 'EndFunction'"),
        },
        Stdout: "", Stderr: "", ExitCode: 0, RunError: null);

    /// <summary>A failed compile from N unresolved and M syntax diagnostics, to pin the banner gate's boundary.</summary>
    public static CompileResult Fail(int unresolved, int syntax)
    {
        var ds = new List<PapyrusDiagnostic>();
        for (int i = 0; i < unresolved; i++) ds.Add(Diag($"unknown type frameworktype{i}"));
        for (int i = 0; i < syntax; i++) ds.Add(Diag($"no viable alternative at input 'tok{i}'"));
        return new CompileResult(false, "HCBoundary", null, ds, "", "", 0, null);
    }

    public static PapyrusDependencyScan ExhaustedScan() =>
        new(Array.Empty<string>(), Indexed: 13235, FilesRead: PapyrusDependencyFilter.MaxFilesRead, BudgetExhausted: true);

    public static PapyrusDependencyScan UnreadableScan() =>
        new(Array.Empty<string>(), Indexed: 13235, FilesRead: 0, BudgetExhausted: false, TargetUnreadable: true);

    public const string FailWarning =
        "modlist scan: could not read the MO2 modlist to discover Papyrus source folders (boom) — " +
        "none of your installed mods' source folders are on the import path for this compile.";

    /// <summary>A plan whose modlist read threw: only the own folder, vanilla missing, the scan flagged failed.</summary>
    public static CompileTools.ImportPlan FailedScan() => new(
        new[] { (@"C:\work", CompileTools.ImportPlan.OwnFolder) },
        AutoEnabled: true, ImportSetName: null, Warning: FailWarning,
        AutoScanned: 0, Scan: null, ReferencedProviders: null, VanillaMissing: true, ScanFailed: true);

    public static CompileTools.ImportPlan Plan(bool autoEnabled = true, int autoCount = 0, int callerCount = 0,
                                              string? setName = null, string? warning = null, int scanned = -1,
                                              PapyrusDependencyScan? scan = null)
    {
        var e = new List<(string Dir, string Origin)> { (@"C:\work", CompileTools.ImportPlan.OwnFolder) };
        for (int i = 0; i < callerCount; i++) e.Add(($@"C:\caller{i}", CompileTools.ImportPlan.CallerDirs));
        var referenced = new List<string>();
        for (int i = 0; i < autoCount; i++)
        {
            var mod = "mod" + (char)('A' + i);
            e.Add(($@"C:\MO2\mods\{mod}\Source\Scripts", CompileTools.ImportPlan.AutoPrefix + mod));
            referenced.Add(mod);
        }
        e.Add((@"C:\Game\Data\Source\Scripts", CompileTools.ImportPlan.Vanilla));
        return new CompileTools.ImportPlan(e, autoEnabled, setName, warning, scanned < 0 ? autoCount : scanned, scan, referenced);
    }

    public static string Render(CompileResult r, CompileTools.ImportPlan plan, bool userChoseOutputDir = false) =>
        CompileTools.Render(r, plan, userChoseOutputDir);
}

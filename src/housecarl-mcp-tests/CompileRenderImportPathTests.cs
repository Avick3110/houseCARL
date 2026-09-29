using HousecarlCore;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.CompileRenderFixtures;

namespace HousecarlMcpTests;

/// <summary>
/// The import path <c>CompileTools.Render</c> reports (migrated from the <c>compile-ergonomics-guard</c> probe, part E):
/// a summary on success, the full ordered path on failure, and every caveat about a scan that did not run or finish.
/// </summary>
[Trait("tier", "unit")]
public sealed class CompileRenderImportPathTests
{
    const string Conclusion = "referenced by this script";

    // Probe E1: "success renders the import summary with the TOTAL (own + 1 caller + 3 auto + vanilla = 6)".
    [Fact]
    public void ASuccessStatesTheTotalDirsSearched()
    {
        Assert.Contains("imports: 6 dir(s) searched", Render(Ok, Plan(autoCount: 3, callerCount: 1, scanned: 501)));
    }

    // Probe E1: "the summary splits caller dirs from the scan, and reports BOTH scan numbers (kept AND scanned)".
    [Fact]
    public void TheSummarySplitsCallerDirsFromTheScanAndGivesBothScanNumbers()
    {
        var msg = Render(Ok, Plan(autoCount: 3, callerCount: 1, scanned: 501));
        Assert.Contains("1 from import_dirs=", msg);
        Assert.Contains("matched 3 of 501", msg);
    }

    // Probe E1: "the summary NAMES the providing mods".
    [Fact]
    public void TheSummaryNamesTheProvidingMods()
    {
        var msg = Render(Ok, Plan(autoCount: 3, scanned: 501));
        Assert.Contains("modA", msg);
        Assert.Contains("modC", msg);
    }

    // Probe E1: "the summary states that vanilla ranks last".
    [Fact]
    public void TheSummaryStatesVanillaRanksLast()
    {
        Assert.Contains("vanilla sources last", Render(Ok, Plan(autoCount: 3, scanned: 501)));
    }

    // Probe E2: "auto_imports=false is stated, and no scan is claimed".
    [Fact]
    public void AutoImportsOffIsStatedAndNoScanIsClaimed()
    {
        var msg = Render(Ok, Plan(autoEnabled: false));
        Assert.Contains("auto_imports=false", msg);
        Assert.DoesNotContain("scanned mod source folder", msg);
    }

    // Probe E3: "over 8 providers: the full COUNT is stated and the tail is named '+4 more' (no silent truncation)".
    [Fact]
    public void ACappedProviderListStatesTheCountAndNamesTheTail()
    {
        var msg = Render(Ok, Plan(autoCount: 12));
        Assert.Contains("matched 12 of 12", msg);
        Assert.Contains("+4 more", msg);
    }

    // Probe E3b: "an exhausted reference-walk budget is surfaced with its ceiling" and "…a completed walk says nothing".
    [Fact]
    public void AnExhaustedWalkIsDisclosedWithItsCeiling()
    {
        var capped = Render(Ok, Plan(autoCount: 2, scan: ExhaustedScan()));
        Assert.Contains("reference walk stopped", capped);
        Assert.Contains(PapyrusDependencyFilter.MaxFilesRead.ToString(), capped);
        Assert.DoesNotContain("reference walk stopped", Render(Ok, Plan(autoCount: 3, scanned: 501)));
    }

    // Probe E3c: "the truncation caveat reaches the FAILED render too" and "…the banner stops asserting a complete
    // narrowing, pointing at the caveat instead".
    [Fact]
    public void TheTruncationCaveatReachesTheFailedRenderAndTheBannerDefersToIt()
    {
        var msg = Render(MissingImports, Plan(autoCount: 2, scanned: 501, scan: ExhaustedScan()));
        Assert.Contains("reference walk stopped", msg);
        Assert.DoesNotContain("REFERENCES BY NAME", msg);
        Assert.Contains("START WITH THE ⚠ NOTE BELOW", msg);
    }

    // Probe E3d: "an unreadable target is SAID, and the summary drops the 'referenced by this script' claim" and "…on the failed render too".
    [Fact]
    public void AnUnreadableTargetIsSaidAndDrawsNoConclusion()
    {
        var plan = Plan(autoCount: 0, scanned: 501, scan: UnreadableScan());
        var ok = Render(Ok, plan);
        Assert.Contains("could not be READ", ok);
        Assert.DoesNotContain(Conclusion, ok);
        Assert.Contains("could not be READ", Render(MissingImports, plan));
    }

    // Probe E4: "a failure prints the full ordered import path" and "failure detail lists EVERY dir on the path".
    [Fact]
    public void AFailurePrintsEveryDirOnThePath()
    {
        var plan = Plan(autoCount: 2, callerCount: 1);
        var msg = Render(SyntaxFail, plan);
        Assert.Contains("import path searched, in order", msg);
        Assert.All(plan.Entries, e => Assert.Contains(e.Dir, msg));
    }

    // Probe E4: "every entry carries its provenance label" and "the printed order IS the search order: own > import_dirs= > MO2 > vanilla".
    [Fact]
    public void TheFailurePathIsLabelledAndPrintedInSearchOrder()
    {
        var msg = Render(SyntaxFail, Plan(autoCount: 2, callerCount: 1));
        var at = new[] { "[the script's own folder]", "[import_dirs=]", "[MO2: modA]", "[vanilla sources]" }
            .Select(label => msg.IndexOf(label, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, at);
        Assert.Equal(at.OrderBy(i => i), at);
    }

    // Probe E5: "auto_imports ON: the banner names what the scan found AND kept, plus the causes it cannot fix".
    [Fact]
    public void WithTheScanOnTheBannerNamesWhatItFoundAndWhatItCannotFix()
    {
        var msg = Render(MissingImports, Plan(autoCount: 3, scanned: 501));
        Assert.Contains("inside a BSA", msg);
        Assert.Contains("found 501 mod source folder(s) and matched the 3", msg);
    }

    // Probe E5: "auto_imports OFF: the banner's first remedy is to turn the scan ON" and "the two remedies are exclusive".
    [Fact]
    public void WithTheScanOffTheBannerSaysTurnItOn()
    {
        var msg = Render(MissingImports, Plan(autoEnabled: false));
        Assert.Contains("re-run with auto_imports=true", msg);
        Assert.DoesNotContain("inside a BSA", msg);
    }

    // Probe E6: "an auto-discovery warning is surfaced on a SUCCESSFUL compile too" and "…and on a failure WITH diagnostics".
    // Probe E6b: "the discovery warning survives the no-parseable-diagnostics branch too".
    [Fact]
    public void ADiscoveryWarningRidesEveryRenderBranch()
    {
        var plan = Plan(warning: "auto_imports: could not read the MO2 modlist (boom)");
        var raw = new CompileResult(false, "HCRaw", null, Array.Empty<PapyrusDiagnostic>(),
                                    "something the parser cannot split", "", 1, null);
        var rawMsg = Render(raw, plan);
        Assert.Contains("no per-line diagnostics were parsed", rawMsg);   // control: the raw-output branch
        Assert.All(new[] { Render(Ok, plan), Render(SyntaxFail, plan), rawMsg },
                   m => Assert.Contains("could not read the MO2 modlist", m));
    }

    // Probe E7: "a failed modlist read draws NO scan conclusion" and "…it says the read failed instead".
    [Fact]
    public void AFailedModlistReadDrawsNoScanConclusion()
    {
        var msg = Render(Ok, FailedScan());
        Assert.DoesNotContain(Conclusion, msg);
        Assert.DoesNotContain("matched 0 of 0", msg);
        Assert.Contains("modlist could NOT be read", msg);
    }

    // Probe E7: "the vanilla caveat stops claiming houseCARL LOOKED under the data folder".
    [Fact]
    public void AFailedModlistReadDoesNotClaimTheDataFolderWasSearched()
    {
        var msg = Render(Ok, FailedScan());
        Assert.Contains("could not read your MO2 modlist to look under the data folder", msg);
        Assert.DoesNotContain("and none under your MO2 data folder", msg);
    }

    // Probe E7: "…and no tail asserts a vanilla slot two lines under a caveat saying there is none".
    [Fact]
    public void AFailedModlistReadClaimsNoVanillaSlot()
    {
        Assert.DoesNotContain("; vanilla sources last", Render(Ok, FailedScan()));
    }

    // Probe E7: "the warning is labelled 'modlist scan', not 'auto_imports'" (read off the service that writes it, not a fixture).
    [Fact]
    public void AFailedModlistReadIsLabelledModlistScan()
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-modlist-read-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var svc = LoadOrderService.WithInstance(Path.Combine(root, "no-such-instance"), 0, new UserConfigStore(Path.Combine(root, "user.json")));
            var (_, _, warning, failed) = svc.PapyrusSourceImportDirs();
            Assert.True(failed);
            Assert.StartsWith("modlist scan:", warning);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { /* non-fatal */ } }
    }

    // Probe E7: "the missing-imports banner leads with the failed read, not with causes that presuppose a scan happened".
    [Fact]
    public void TheBannerLeadsWithAFailedModlistRead()
    {
        var msg = Render(MissingImports, FailedScan());
        Assert.Contains("The modlist could NOT be read", msg);
        Assert.DoesNotContain("REFERENCES BY NAME", msg);
    }

    // Probe E7 control: "a scan that ran and matched nothing DOES report that conclusion (no false alarm)".
    [Fact]
    public void AScanThatRanAndMatchedNothingSaysSo()
    {
        var msg = Render(Ok, Plan(autoCount: 0, scanned: 501));
        Assert.Contains("matched 0 of 501", msg);
        Assert.Contains(Conclusion, msg);
    }

    /// <summary>Every bool on the plan and the scan, classified: suppress the conclusion phrase, or exempt with a reason.</summary>
    static readonly Dictionary<string, (bool Suppress, Func<CompileTools.ImportPlan>? Sample)> Classification = new()
    {
        // the modlist read threw: no conclusion exists
        ["ScanFailed"] = (true, FailedScan),
        // the target's source was never opened
        ["TargetUnreadable"] = (true, () => Plan(autoCount: 0, scanned: 501, scan: UnreadableScan())),
        // the walk did match what it names; only completeness is in doubt, and the ⚠ says so
        ["BudgetExhausted"] = (false, () => Plan(autoCount: 2, scanned: 501, scan: ExhaustedScan())),
        // a different axis: the vanilla slot, not the scan's answer
        ["VanillaMissing"] = (false, null),
        // what the caller asked for; its own summary branch replaces the phrase
        ["AutoEnabled"] = (false, null),
    };

    // Probe closure: "closure: bool state '<name>' is CLASSIFIED suppress-or-exempt — a new flag cannot reach the render undecided".
    [Fact]
    public void EveryRenderStateFlagIsClassified()
    {
        var flags = typeof(CompileTools.ImportPlan).GetProperties()
            .Concat(typeof(PapyrusDependencyScan).GetProperties())
            .Where(pi => pi.PropertyType == typeof(bool))
            .Select(pi => pi.Name).Distinct().ToList();
        Assert.True(flags.Count >= 5, string.Join(", ", flags));
        Assert.All(flags, name => Assert.True(Classification.ContainsKey(name), name));
    }

    // Probe closure: "[name] SUPPRESS: the conclusion phrase is withheld on the success render", "…and the banner does not
    // assert it either", "[name] EXEMPT: the conclusion phrase still prints, deliberately", "[name]: carries a ⚠ caveat either way".
    public static IEnumerable<object[]> ClassifiedWithASample() =>
        Classification.Where(e => e.Value.Sample is not null).Select(e => new object[] { e.Key });

    [Theory]
    [MemberData(nameof(ClassifiedWithASample))]
    public void EachDegradedStateRendersAsClassified(string name)
    {
        var (suppress, sample) = Classification[name];
        var plan = sample!();
        var ok = Render(Ok, plan);
        Assert.Contains("⚠", ok);
        if (suppress)
        {
            Assert.DoesNotContain(Conclusion, ok);
            Assert.DoesNotContain("REFERENCES BY NAME", Render(MissingImports, plan));
        }
        else Assert.Contains(Conclusion, ok);
    }
}

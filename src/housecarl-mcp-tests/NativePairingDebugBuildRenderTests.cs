using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.NativePairingRenderFixtures;

namespace HousecarlMcpTests;

/// <summary>Migrated from the native-pairing-guard probe, part 3 arms A2-A4: a DLL built against the debug CRT. Where
/// the debug runtime is absent the service's loose-DLL chain blocks it and the class is PAIRED BUT DEAD; where it is
/// present the class still loads, and the render names the debug build for everyone else (#417).</summary>
[Trait("tier", "unit")]
public sealed class NativePairingDebugBuildRenderTests
{
    static readonly SksePluginReader.SksePluginInfo Dbg = DebugBuild("Dbg.dll");

    [Fact] // probe A2a: "the service's loose-DLL chain blocks a debug build (the production wiring, not a fixture)"
    public void TheLooseDllChainBlocksADebugBuildWithoutTheDebugRuntime() =>
        Assert.False(string.IsNullOrEmpty(AssetLayers.LooseDllBlocker(Dbg, _ => false)));

    [Fact] // probe A2b / A3a: "on a box WITH the debug runtime the same chain blocks nothing (it truly loads there)"
    public void TheLooseDllChainBlocksNothingWhereTheDebugRuntimeResolves() =>
        Assert.Null(AssetLayers.LooseDllBlocker(Dbg, _ => true));

    [Fact] // probe A2: "a DEBUG-built version-INDEPENDENT DLL → 'PAIRED BUT DEAD', not healthy (the tier-D blind spot)"
    public void ADebugBuildBlockedByTheChainRendersPairedButDead()
    {
        var blocker = AssetLayers.LooseDllBlocker(Dbg, _ => false);
        var s = Render(Data(Paired("DbgUtil", "DbgMod", Dll("Dbg.dll", "DbgMod", Dbg, blocker)), "1.6.1170.0"));
        Assert.Contains("PAIRED BUT DEAD", s);
        Assert.Contains("DEBUG build", s);
        Assert.Contains("error 126", s);
        Assert.DoesNotContain("paired healthy (1", s);
    }

    static NativePairingAuditData DebugBuildThatLoadsHere() =>
        Data(Paired("DbgUtil", "DbgMod", Dll("Dbg.dll", "DbgMod", Dbg)), "1.6.1170.0");

    [Fact] // probe A3b: "it still renders [LOADS] — the verdict is untouched, because it does load here"
    public void ADebugBuildThatLoadsHereStillRendersLoads()
    {
        var s = Render(DebugBuildThatLoadsHere(), "DbgUtil");
        Assert.Contains("[LOADS]", s);
        Assert.DoesNotContain("[DEAD]", s);
    }

    [Fact] // probe A3c: "…and the line now names the debug CRT and error 126 for everyone else (#417)"
    public void ADebugBuildThatLoadsHereNamesTheDebugCrtUnderFilter()
    {
        var s = Render(DebugBuildThatLoadsHere(), "DbgUtil");
        Assert.Contains("vcruntime140d.dll", s);
        Assert.Contains("error 126", s);
        Assert.Contains("debug CRT", s);
    }

    [Fact] // probe A3e: "the default (unfiltered) view names the debug build as a finding, not only under filter="
    public void TheDefaultViewNamesTheDebugBuildAsAFinding()
    {
        var s = Render(DebugBuildThatLoadsHere());
        Assert.Contains("DEBUG BUILD", s);
        Assert.Contains("vcruntime140d.dll", s);
        Assert.Contains("error 126", s);
    }

    [Fact] // probe A3f: "…and it no longer claims the clean bill of health over that same file"
    public void TheDefaultViewDropsTheCleanBillOfHealthOverADebugBuild()
    {
        var s = Render(DebugBuildThatLoadsHere());
        Assert.DoesNotContain("nothing dead, nothing unpaired", s);
        Assert.DoesNotContain("paired healthy (1 class", s);
    }

    static NativePairingAuditData CleanDll() =>
        Data(Paired("CleanUtil", "CleanMod", Dll("PapyrusUtil.dll", "CleanMod", Modern("PapyrusUtil.dll", independent: true))), "1.6.1170.0");

    [Fact] // probe A3d: "a clean version-independent DLL gets no debug note"
    public void ACleanDllGetsNoDebugNote() => Assert.DoesNotContain("debug CRT", Render(CleanDll(), "CleanUtil"));

    [Fact] // probe A3g: "…and it still earns the clean bill of health in the default view"
    public void ACleanDllStillEarnsTheCleanBillOfHealth()
    {
        var s = Render(CleanDll());
        Assert.Contains("nothing dead, nothing unpaired", s);
        Assert.DoesNotContain("DEBUG BUILD", s);
    }

    static NativePairingAuditData MixedRelease() =>
        Data(Paired("MixedUtil", "MixedMod",
            Dll("PapyrusUtil.dll", "MixedMod", Modern("PapyrusUtil.dll", independent: true)),
            Dll("Dbg.dll", "MixedMod", Dbg)), "1.6.1170.0");

    [Fact] // probe A3h: "a class with a clean LOADING DLL beside a debug sibling stays healthy, not debug-only"
    public void ACleanLoadingDllBesideADebugSiblingKeepsTheClassHealthy()
    {
        var s = Render(MixedRelease());
        Assert.Contains("paired healthy (1 class(es))", s);
        Assert.Contains("MixedMod: MixedUtil", s);
        Assert.DoesNotContain("paired only to a DEBUG BUILD", s);
        Assert.DoesNotContain("DEBUG BUILD —", s);
    }

    [Fact] // probe A3i: "…and the debug sibling's own line still carries the debug-CRT clause under filter="
    public void TheDebugSiblingsLineKeepsTheClauseUnderFilter() =>
        Assert.Contains("debug CRT", Render(MixedRelease(), "MixedUtil"));

    static NativePairingAuditData MixedLockedOnUnknownRuntime() =>
        Data(Paired("LockedMixedUtil", "MixedMod",
            Dll("Dbg.dll", "MixedMod", Dbg),
            Dll("Clean.dll", "MixedMod", Modern("Clean.dll", independent: false, compat: new[] { "1.5.97" }))), runtime: null);

    [Fact] // probe A3j: "a clean version-LOCKED sibling on VERIFY also keeps the class off the debug-only finding"
    public void ACleanLockedSiblingOnVerifyKeepsTheClassOffTheDebugOnlyFinding()
    {
        var s = Render(MixedLockedOnUnknownRuntime());
        Assert.Contains("paired healthy (1 class(es))", s);
        Assert.Contains("MixedMod: LockedMixedUtil", s);
        Assert.DoesNotContain("paired only to a DEBUG BUILD", s);
        Assert.DoesNotContain("DEBUG BUILD —", s);
    }

    [Fact] // probe A3k: "…and that sibling's debug line still carries the clause under filter="
    public void TheLockedMixDebugLineKeepsTheClauseUnderFilter() =>
        Assert.Contains("debug CRT", Render(MixedLockedOnUnknownRuntime(), "LockedMixedUtil"));

    static NativePairingAuditData LockedDebugBuildOnUnknownRuntime() =>
        Data(Paired("LockedDbgUtil", "LockedDbgMod",
            Dll("LockedDbg.dll", "LockedDbgMod", DebugBuild("LockedDbg.dll", independent: false, compat: new[] { "1.5.97" }))), runtime: null);

    [Fact] // probe A4a: "a version-locked debug build still reads [VERIFY] — the verdict is untouched by the note"
    public void ALockedDebugBuildOnAnUnknownRuntimeStillReadsVerify()
    {
        var s = Render(LockedDebugBuildOnUnknownRuntime());
        Assert.Contains("[VERIFY]", s);
        Assert.Contains("need a version check", s);
        Assert.DoesNotContain("[DEAD]", s);
    }

    [Fact] // probe A4b: "…and the VERIFY line names the debug CRT and error 126, as the LOADS line does"
    public void TheVerifyLineNamesTheDebugCrt()
    {
        var s = Render(LockedDebugBuildOnUnknownRuntime());
        Assert.Contains("vcruntime140d.dll", s);
        Assert.Contains("debug CRT", s);
        Assert.Contains("error 126", s);
    }

    [Fact] // probe A4c: "…in the filter= view too, from the same Judge"
    public void TheVerifyLineNamesTheDebugCrtUnderFilter() =>
        Assert.Contains("debug CRT", Render(LockedDebugBuildOnUnknownRuntime(), "LockedDbgUtil"));
}

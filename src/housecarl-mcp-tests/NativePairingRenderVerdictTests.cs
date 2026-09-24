using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.NativePairingRenderFixtures;

namespace HousecarlMcpTests;

/// <summary>Migrated from the native-pairing-guard probe, part 3: the pairing renderer's verdicts over synthetic audit
/// data. A dead claim only when the runtime is known or the blocker is static, UNPAIRED framed as a verify flag, the
/// baseline accounting and loader note, the all-clear branch, and filter= with its did-you-mean.</summary>
[Trait("tier", "unit")]
public sealed class NativePairingRenderVerdictTests
{
    static NativeClassEntry LockedOldUtil() =>
        Paired("OldUtil", "OldMod", Dll("OldPlugin.dll", "OldMod", Modern("OldPlugin.dll", independent: false, compat: new[] { "1.5.97" })));

    [Fact] // probe A: "locked-mismatch + known runtime → 'PAIRED BUT DEAD' + 'will NOT load' + both versions named"
    public void ALockedMismatchOnAKnownRuntimeIsPairedButDead()
    {
        var s = Render(Data(LockedOldUtil(), "1.6.1170.0"));
        Assert.Contains("PAIRED BUT DEAD", s);
        Assert.Contains("will NOT load", s);
        Assert.Contains("1.5.97", s);
        Assert.Contains("1.6.1170.0", s);
    }

    [Fact] // probe B: "runtime unknown → degrades to 'verify', never claims dead"
    public void ALockedPluginOnAnUnknownRuntimeDegradesToVerify()
    {
        var s = Render(Data(LockedOldUtil(), runtime: null));
        Assert.DoesNotContain("PAIRED BUT DEAD", s);
        Assert.Contains("verify", s);
        Assert.Contains("could not be resolved", s);
    }

    [Fact] // probe C: "static blocker (BSA-only) → DEAD even with runtime unknown"
    public void AStaticBlockerIsDeadEvenWithTheRuntimeUnknown()
    {
        var dll = Dll("X.dll", "XMod", null, "provided only inside a BSA — the SKSE loader scans loose DLLs only, so it will not load");
        var s = Render(Data(Paired("XUtil", "XMod", dll), runtime: null));
        Assert.Contains("PAIRED BUT DEAD", s);
        Assert.Contains("[DEAD]", s);
        Assert.Contains("loose DLLs only", s);
    }

    static NativeClassEntry LegacyOldSeUtil() =>
        Paired("OldSeUtil", "OldSeMod", Dll("OldSe.dll", "OldSeMod",
            new SksePluginReader.SksePluginInfo("OldSe.dll", SksePluginReader.SksePluginKind.LegacyQuery, true, null, "legacy")));

    [Fact] // probe C2: "LegacyQuery + AE runtime → PAIRED BUT DEAD (query-only plugins don't load on AE)"
    public void ALegacyQueryPluginOnAnAeRuntimeIsDead()
    {
        var s = Render(Data(LegacyOldSeUtil(), "1.6.1170.0"));
        Assert.Contains("PAIRED BUT DEAD", s);
        Assert.Contains("query-only", s);
    }

    [Fact] // probe C2: "LegacyQuery + SE runtime → healthy (loads on SE)"
    public void ALegacyQueryPluginOnAnSeRuntimeIsHealthy()
    {
        var s = Render(Data(LegacyOldSeUtil(), "1.5.97.0"));
        Assert.Contains("✓", s);
        Assert.Contains("OldSeMod: OldSeUtil", s);
    }

    [Fact] // probe C2: "LegacyQuery + unknown runtime → verify, never a dead claim"
    public void ALegacyQueryPluginOnAnUnknownRuntimeIsVerify()
    {
        var s = Render(Data(LegacyOldSeUtil(), runtime: null));
        Assert.DoesNotContain("PAIRED BUT DEAD", s);
        Assert.Contains("verify", s);
    }

    static string MixedBaselineRender()
    {
        var okDll = Dll("PapyrusUtil.dll", "PapyrusUtil AE", Modern("PapyrusUtil.dll", independent: true));
        return Render(Data(new[]
        {
            Cls("Actor", NativeProvenance.Engine, null, null, winner: "Skyrim Script Extender (SKSE64)"),
            Cls("StringUtil", NativeProvenance.SkseCore, null, null, winner: "Skyrim Script Extender (SKSE64)"),
            Cls("StorageUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "PapyrusUtil AE", new[] { okDll }, winner: "PapyrusUtil AE"),
            Cls("OrphanUtil", NativeProvenance.ThirdParty, NativePairingRung.Unpaired, null, winner: "Scripts Only Mod"),
        }, "1.6.1170.0", loaderSeen: false));
    }

    [Fact] // probe D: "UNPAIRED framed as verify (explicitly NOT 'broken'), with the declaration-copy explanation"
    public void UnpairedIsFramedAsAVerifyFlagNotBroken()
    {
        var s = MixedBaselineRender();
        Assert.Contains("UNPAIRED", s);
        Assert.Contains("VERIFY flag", s);
        Assert.Contains("not 'broken'", s);
        Assert.Contains("declaration copy", s);
    }

    [Fact] // probe D: "baseline accounting (1 engine + 1 SKSE-core) + paired-healthy group"
    public void TheBaselineIsCountedAndTheHealthyPairIsGrouped()
    {
        var s = MixedBaselineRender();
        Assert.Contains("1 engine class(es)", s);
        Assert.Contains("1 SKSE-core class(es)", s);
        Assert.Contains("PapyrusUtil AE: StorageUtil", s);
    }

    [Fact] // probe D: "no-loader sanity note fires when SKSE-core classes exist without a visible loader"
    public void TheNoLoaderNoteFiresForSkseCoreClassesWithoutALoader() =>
        Assert.Contains("no skse64 loader is visible", MixedBaselineRender());

    static string AllClearWithAnUnreadablePex()
    {
        var okDll = Dll("PapyrusUtil.dll", "PapyrusUtil AE", Modern("PapyrusUtil.dll", independent: true));
        return Render(Data(new[] { Paired("StorageUtil", "PapyrusUtil AE", okDll) }, "1.6.1170.0",
            unreadable: new[] { new NativeUnreadablePex(@"Scripts\Broken.pex", "BadMod", "Mutagen cannot read it (EndOfStreamException)") }));
    }

    [Fact] // probe E: "all third-party healthy → the ✓ all-clear headline"
    public void AllThirdPartyHealthyGivesTheAllClearHeadline() =>
        Assert.Contains("✓ every third-party native class pairs", AllClearWithAnUnreadablePex());

    [Fact] // probe E: "unreadable .pex is a NAMED note, not counted clean"
    public void AnUnreadablePexIsANamedNoteNotCountedClean()
    {
        var s = AllClearWithAnUnreadablePex();
        Assert.Contains("Broken.pex", s);
        Assert.Contains("NOT counted as native-free", s);
    }

    [Fact] // probe E: "the all-clear headline is QUALIFIED by the unexamined unreadables (review finding)"
    public void TheAllClearHeadlineIsQualifiedByTheUnreadables() =>
        Assert.Contains("1 unreadable .pex NOT examined", AllClearWithAnUnreadablePex());

    static NativeClassEntry SkseCoreStringUtil() =>
        Cls("StringUtil", NativeProvenance.SkseCore, null, null, winner: "Skyrim Script Extender (SKSE64)");

    [Fact] // probe G: "loaderSeen=false → the definite no-loader note"
    public void ALoaderCheckedAndAbsentGivesTheDefiniteNote() =>
        Assert.Contains("no skse64 loader is visible", Render(Data(new[] { SkseCoreStringUtil() }, "1.6.1170.0", loaderSeen: false)));

    [Fact] // probe G: "loaderSeen=null → 'could not be checked', never the definite absence claim"
    public void ALoaderThatCouldNotBeCheckedIsNeverClaimedAbsent()
    {
        var s = Render(Data(new[] { SkseCoreStringUtil() }, "1.6.1170.0", loaderSeen: null));
        Assert.DoesNotContain("no skse64 loader is visible", s);
        Assert.Contains("could not be checked", s);
    }

    static NativePairingAuditData StorageUtilPairedToItsOwnProvider()
    {
        var okDll = Dll("PapyrusUtil.dll", "PapyrusUtil AE", Modern("PapyrusUtil.dll", independent: true));
        return Data(new[] { Cls("StorageUtil", NativeProvenance.ThirdParty, NativePairingRung.SameMod, "PapyrusUtil AE", new[] { okDll }, winner: "PapyrusUtil AE") }, "1.6.1170.0");
    }

    [Fact] // probe F: "filter= shows full detail (functions, rung, DLL verdict)"
    public void AFilterShowsFunctionsRungAndDllVerdict()
    {
        var s = Render(StorageUtilPairedToItsOwnProvider(), "storageutil");
        Assert.Contains("FnA, FnB", s);
        Assert.Contains("paired to its own provider", s);
        Assert.Contains("[LOADS]", s);
    }

    [Fact] // probe F: "typo'd filter → did-you-mean from the multi-axis pool"
    public void ATypoedFilterSuggestsTheClass()
    {
        var s = Render(StorageUtilPairedToItsOwnProvider(), "StorageUtl");
        Assert.Contains("no native-declaring class matched", s);
        Assert.Contains("did you mean", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("StorageUtil", s, StringComparison.OrdinalIgnoreCase);
    }
}

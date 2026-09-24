using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Migrated from the native-pairing-guard probe, part 2: who implements a native class. The official-archive
/// test, the engine test over a chain, a BSA's pairing identity, the evidence ladder, the Classify order, and the
/// archive-to-mod translation.</summary>
[Trait("tier", "unit")]
public sealed class NativePairingProvenanceTests
{
    static readonly IReadOnlyList<ModKey> BaseMasters = Implicits.Get(GameRelease.SkyrimSE).BaseMasters;
    static readonly HashSet<string> Official = new(StringComparer.OrdinalIgnoreCase) { "Skyrim - Misc.bsa" };

    static PlacementSource Loose(string mod) => new(mod, AssetKind.Loose, @"D:\x", null, "");
    static PlacementSource Bsa(string archive) => new(archive, AssetKind.Bsa, null, @"D:\x.bsa", "");

    [Fact] // probe: "ini-base archive (Skyrim - Misc.bsa) is OFFICIAL"
    public void AnIniBaseArchiveIsOfficial() =>
        Assert.True(AssetLayers.IsOfficialArchive(new ActiveArchive(@"D:\g\Data\Skyrim - Misc.bsa", ArchiveDiscovery.IniArchiveOwner, 0), BaseMasters));

    [Fact] // probe: "Dawnguard.esm-owned archive is OFFICIAL (BaseMasters, by construction)"
    public void ABaseMasterOwnedArchiveIsOfficial() =>
        Assert.True(AssetLayers.IsOfficialArchive(new ActiveArchive(@"D:\g\Data\Dawnguard.bsa", "Dawnguard.esm", 5), BaseMasters));

    [Fact] // probe: "a mod plugin's archive is NOT official"
    public void AModPluginsArchiveIsNotOfficial() =>
        Assert.False(AssetLayers.IsOfficialArchive(new ActiveArchive(@"D:\m\Campfire.bsa", "Campfire.esm", 40), BaseMasters));

    [Fact] // probe: "loose override winning over an official BSA copy → still ENGINE (chain presence)"
    public void ALooseOverrideWinningOverAnOfficialBsaIsStillEngine() =>
        Assert.True(AssetLayers.HasOfficialSource(new[] { Loose("Skyrim Script Extender (SKSE64)"), Bsa("Skyrim - Misc.bsa") }, Official));

    [Fact] // probe: "loose-only chain (StringUtil.pex) → NOT engine"
    public void ALooseOnlyChainIsNotEngine() =>
        Assert.False(AssetLayers.HasOfficialSource(new[] { Loose("Skyrim Script Extender (SKSE64)") }, Official));

    [Fact] // probe: "a mod BSA in the chain is not mistaken for official"
    public void AModBsaInTheChainIsNotOfficial() =>
        Assert.False(AssetLayers.HasOfficialSource(new[] { Bsa("Campfire.bsa") }, Official));

    static readonly Dictionary<string, string> Shipper = new(StringComparer.OrdinalIgnoreCase) { ["AHZmoreHUD.bsa"] = "moreHUD SE" };

    [Fact] // probe: "BSA source translates to its shipping mod (pairing identity)"
    public void ABsaSourceTranslatesToItsShippingMod() =>
        Assert.Equal("moreHUD SE", AssetLayers.PairingIdentity(Bsa("AHZmoreHUD.bsa"), Shipper));

    [Fact] // probe: "loose source's identity is its provider name"
    public void ALooseSourcesIdentityIsItsProvider() =>
        Assert.Equal("PapyrusUtil AE", AssetLayers.PairingIdentity(Loose("PapyrusUtil AE"), Shipper));

    [Fact] // probe: "an untranslatable archive keeps its own name"
    public void AnUntranslatableArchiveKeepsItsOwnName() =>
        Assert.Equal("Unknown.bsa", AssetLayers.PairingIdentity(Bsa("Unknown.bsa"), Shipper));

    static readonly NativePairedDll OkDll = new(@"SKSE\Plugins\PapyrusUtil.dll", "PapyrusUtil.dll", "", "PapyrusUtil AE", null, null);
    static readonly NativePairedDll DeadDll = new(@"SKSE\Plugins\Helper\junk.dll", "junk.dll", "Helper", "Bundler",
        null, "in subfolder 'Helper' — not on SKSE's loader path");
    static readonly Dictionary<string, List<NativePairedDll>> ModDlls = new(StringComparer.OrdinalIgnoreCase)
    { ["PapyrusUtil AE"] = new() { OkDll }, ["Bundler"] = new() { DeadDll } };

    [Fact] // probe: "rung 1: winning identity ships the DLL → SameMod"
    public void TheWinningIdentityShippingTheDllIsSameMod()
    {
        var (rung, mod, dlls) = AssetLayers.Ladder(new[] { "PapyrusUtil AE" }, ModDlls);
        Assert.Equal(NativePairingRung.SameMod, rung);
        Assert.Equal("PapyrusUtil AE", mod);
        Assert.Single(dlls);
    }

    [Fact] // probe: "rung 2: framework beneath the winner in the chain → ChainMod (the bundling case)"
    public void AFrameworkBeneathTheWinnerIsChainMod()
    {
        var (rung, mod, _) = AssetLayers.Ladder(new[] { "Campfire", "PapyrusUtil AE" }, ModDlls);
        Assert.Equal(NativePairingRung.ChainMod, rung);
        Assert.Equal("PapyrusUtil AE", mod);
    }

    [Fact] // probe: "rung 3: nobody in the chain ships a DLL → Unpaired"
    public void NobodyShippingADllIsUnpaired()
    {
        var (rung, mod, dlls) = AssetLayers.Ladder(new[] { "Some Scripts-Only Mod" }, ModDlls);
        Assert.Equal(NativePairingRung.Unpaired, rung);
        Assert.Null(mod);
        Assert.Empty(dlls);
    }

    [Fact] // probe: "dead-candidate bundler does NOT mask the loadable framework beneath → ChainMod to the framework"
    public void ADeadCandidateBundlerDoesNotMaskTheLoadableFrameworkBeneath()
    {
        var (rung, mod, _) = AssetLayers.Ladder(new[] { "Bundler", "PapyrusUtil AE" }, ModDlls);
        Assert.Equal(NativePairingRung.ChainMod, rung);
        Assert.Equal("PapyrusUtil AE", mod);
    }

    [Fact] // probe: "no loadable candidate anywhere → the shallowest with ANY candidate pairs (its deadness is the finding)"
    public void WithNoLoadableCandidateTheShallowestWithAnyCandidatePairs()
    {
        var (rung, mod, _) = AssetLayers.Ladder(new[] { "Bundler", "Scriptless" }, ModDlls);
        Assert.Equal(NativePairingRung.SameMod, rung);
        Assert.Equal("Bundler", mod);
    }

    static readonly HashSet<string> EnginePool = new(StringComparer.OrdinalIgnoreCase) { "Skyrim Script Extender (SKSE64)" };

    [Fact] // probe: "Classify: engine short-circuits everything"
    public void ClassifyEngineShortCircuitsEverything() =>
        Assert.Equal(NativeProvenance.Engine, AssetLayers.Classify(true, new[] { "PapyrusUtil AE" }, ModDlls, EnginePool).Provenance);

    [Fact] // probe: "Classify: pairing evidence beats the rescue" (the winner is put in the pool here, so the rescue is on offer)
    public void ClassifyPairingEvidenceBeatsTheSkseCoreRescue()
    {
        var pool = new HashSet<string>(EnginePool, StringComparer.OrdinalIgnoreCase) { "PapyrusUtil AE" };
        var c = AssetLayers.Classify(false, new[] { "PapyrusUtil AE" }, ModDlls, pool);
        Assert.Equal(NativeProvenance.ThirdParty, c.Provenance);
        Assert.Equal(NativePairingRung.SameMod, c.Rung);
    }

    [Fact] // probe: "Classify: unpaired + winner in the engine-provider pool → SKSE CORE (the StringUtil rescue)"
    public void ClassifyUnpairedWithAWinnerInTheEnginePoolIsSkseCore() =>
        Assert.Equal(NativeProvenance.SkseCore, AssetLayers.Classify(false, new[] { "Skyrim Script Extender (SKSE64)" }, ModDlls, EnginePool).Provenance);

    [Fact] // probe: "Classify: unpaired + winner NOT in the pool → stays UNPAIRED third-party"
    public void ClassifyUnpairedWithAWinnerOutsideThePoolStaysThirdParty()
    {
        var c = AssetLayers.Classify(false, new[] { "Scripts Only Mod" }, ModDlls, EnginePool);
        Assert.Equal(NativeProvenance.ThirdParty, c.Provenance);
        Assert.Equal(NativePairingRung.Unpaired, c.Rung);
    }

    static string? Layer(string archivePath) =>
        AssetLayers.LayerOfInstallPath(archivePath, @"E:\mo2\mods", @"E:\mo2\overwrite", @"D:\g\Data");

    [Fact] // probe: "archive under mods\<mod>\ → that mod"
    public void AnArchiveUnderAModFolderIsThatMod() => Assert.Equal("moreHUD SE", Layer(@"E:\mo2\mods\moreHUD SE\AHZmoreHUD.bsa"));

    [Fact] // probe: "archive in the overwrite layer → 'overwrite'"
    public void AnArchiveInOverwriteIsOverwrite() => Assert.Equal("overwrite", Layer(@"E:\mo2\overwrite\X.bsa"));

    [Fact] // probe: "archive in game Data → 'Data'"
    public void AnArchiveInGameDataIsData() => Assert.Equal("Data", Layer(@"D:\g\Data\Skyrim - Misc.bsa"));

    [Fact] // probe: "archive nowhere under the roots → null (no translation)"
    public void AnArchiveOutsideEveryRootHasNoLayer() => Assert.Null(Layer(@"C:\elsewhere\X.bsa"));
}

using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A two-plugin order for the config-audit verdicts: a full plugin with one faction, and a light plugin with
/// its faction at 0x800 so an FE-prefixed reference has a real target.</summary>
public sealed class SkseConfigVerdictOrder : IDisposable
{
    public const uint EslId = 0x800;
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-skse-verdict-" + Guid.NewGuid().ToString("N"));
    public LoadOrderResolver Resolver { get; }
    public uint FullFactionId { get; }

    public SkseConfigVerdictOrder()
    {
        Directory.CreateDirectory(_dir);
        var fullPath = Path.Combine(_dir, "hcAudit.esp");
        var full = new SkyrimMod(ModKey.FromNameAndExtension("hcAudit.esp"), SkyrimRelease.SkyrimSE);
        var fac = full.Factions.AddNew();
        fac.EditorID = "hcAuditFac";
        FullFactionId = fac.FormKey.ID;
        full.BeginWrite.ToPath(fullPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var eslKey = ModKey.FromNameAndExtension("hcAuditEsl.esl");
        var eslPath = Path.Combine(_dir, "hcAuditEsl.esl");
        var esl = new SkyrimMod(eslKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        esl.Factions.Add(new Faction(new FormKey(eslKey, EslId), SkyrimRelease.SkyrimSE) { EditorID = "hcAuditEslFac" });
        esl.BeginWrite.ToPath(eslPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        Resolver = LoadOrderResolver.Build(new[] { fullPath, eslPath });
    }

    public void Dispose()
    {
        Resolver.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}

/// <summary>Migrated from the skse-config-audit-guard probe's verdict arms: an extracted reference adjudicated by
/// <see cref="AssetLayers.Adjudicate"/> against a real synthetic order.</summary>
[Trait("tier", "integration")]
public sealed class SkseConfigVerdictTests(SkseConfigVerdictOrder order) : IClassFixture<SkseConfigVerdictOrder>
{
    SkseRefVerdict V(string relPath, string text)
        => AssetLayers.Adjudicate(Assert.Single(SkseConfigReferenceExtractor.Extract(relPath, text)), order.Resolver.Capture()).Verdict;

    [Fact] // OK: 0x<real>|hcAudit.esp → OK
    public void ARealRecordInAFullPluginIsOk()
        => Assert.Equal(SkseRefVerdict.Ok, V(@"SKSE\Plugins\Foo\x.json", $"\"form\": \"0x{order.FullFactionId:X}|hcAudit.esp\""));

    [Fact] // OK: FE007800|hcAuditEsl.esl (ESL FE-prefix mask) → OK
    public void AnFePrefixedReferenceResolvesToTheLightRecord()
        => Assert.Equal(SkseRefVerdict.Ok,
            V(@"SKSE\Plugins\DynamicStringDistributor\x.json", $"\"form_id\": \"FE007{SkseConfigVerdictOrder.EslId:X3}|hcAuditEsl.esl\""));

    [Fact] // DANGLING: 0xABCDEF|hcAudit.esp (no such record) → DANGLING
    public void ANoSuchRecordInAPresentPluginIsDangling()
        => Assert.Equal(SkseRefVerdict.Dangling, V(@"SKSE\Plugins\Foo\x.ini", "target = 0xABCDEF|hcAudit.esp"));

    [Fact] // MISSING: 0x1|NotInstalled.esp → PLUGIN MISSING
    public void ATokenNamingAnAbsentPluginIsPluginMissing()
        => Assert.Equal(SkseRefVerdict.PluginMissing, V(@"SKSE\Plugins\Foo\x.ini", "target = 0x1|NotInstalled.esp"));

    [Fact] // GATE OK: \hcAudit.esp\ folder (present) → OK
    public void AFolderGateOnAPresentPluginIsOk()
        => Assert.Equal(SkseRefVerdict.Ok, V(@"SKSE\Plugins\DynamicStringDistributor\hcAudit.esp\names.json", "{}"));

    [Fact] // GATE MISSING: \NotInstalled.esp\ folder (absent) → PLUGIN MISSING
    public void AFolderGateOnAnAbsentPluginIsPluginMissing()
        => Assert.Equal(SkseRefVerdict.PluginMissing, V(@"SKSE\Plugins\DynamicStringDistributor\NotInstalled.esp\names.json", "{}"));

    [Fact] // UNPARSEABLE: overflow token → UNPARSEABLE verdict
    public void AnOverflowTokenIsUnparseable()
        => Assert.Equal(SkseRefVerdict.Unparseable, V(@"SKSE\Plugins\Foo\x.ini", "x = 0x1FFFFFFFF|hcAudit.esp"));
}

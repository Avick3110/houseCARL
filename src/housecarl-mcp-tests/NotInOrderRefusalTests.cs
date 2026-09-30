using System.ComponentModel;
using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A read or a merge naming a plugin that is not in the order refuses with the reason it is not: unticked,
/// mod switched off, stale profile, or an unlisted fresh patch, each with its own remedy; a name that explains
/// nothing keeps the did-you-mean and the generic tail. Migrated from the bulk-primitives-wave3 probe's #271 arms.</summary>
[Trait("tier", "integration")]
public sealed class NotInOrderRefusalTests : IClassFixture<AbsenceCauseWorld>
{
    readonly AbsenceCauseWorld _w;
    public NotInOrderRefusalTests(AbsenceCauseWorld w) => _w = w;

    string ReadRefusal(Mutagen.Bethesda.Plugins.FormKey fk, string plugin)
    {
        var o = _w.Svc.ReadArea.ResolveRead(fk, plugin, null, false);
        Assert.NotNull(o.Error);
        return o.Error!;
    }

    string Unticked => ReadRefusal(_w.UntickedWKey, _w.UntickedName);
    string Typo => ReadRefusal(_w.WKey, "HcW3DiffRep.esp");   // one character dropped from the replacer's name
    string FreshPatch => ReadRefusal(_w.UnlistedWKey, _w.UnlistedName);

    // probe: #271 refusal: read_record on an UNTICKED plugin explains it is installed-but-unticked, not 'not found'
    [Fact]
    public void AnUntickedPluginIsExplainedAsUnticked()
    {
        var e = Unticked;
        Assert.Contains("not in the load order", e);
        Assert.Contains("UNTICKED", e);
        Assert.Contains("plugins.txt", e);
    }

    // probe: #271 refusal: and points at the raw-read escape hatch rather than leaving a dead end
    [Fact]
    public void TheUntickedRefusalPointsAtTheRecordsSourcePole()
    {
        var e = Unticked;
        Assert.Contains(ToolNames.Records, e);
        Assert.Contains("source=", e);
    }

    // probe: #271 refusal: a plugin whose MOD is switched off says so — a different cause, a different remedy
    // The plugin is DiffDonor.esp, so "DiffDonor" alone is carried by the filename; the remedy is what names the mod's state.
    [Fact]
    public void APluginInADisabledModNamesTheMod()
    {
        var e = ReadRefusal(_w.WKey, _w.DonorName);
        Assert.Contains("DiffDonor", e);
        Assert.Contains("not active", e);
        Assert.Contains("Switch that mod on", e);
    }

    // probe: #271 refusal: a genuine typo still gets the did-you-mean (the explainer adds, never removes)
    [Fact]
    public void ATypoStillGetsTheDidYouMean()
    {
        var e = Typo;
        Assert.Contains("Did you mean", e);
        Assert.Contains(_w.ReplacerName, e);
    }

    // probe: #271 refusal: the explained case drops the generic posture tail, the unexplained one keeps it
    [Fact]
    public void OnlyTheUnexplainedCaseKeepsThePostureTail()
    {
        Assert.DoesNotContain("does not open disabled", Unticked);
        Assert.Contains("does not open disabled", Typo);
    }

    // probe: #271 refusal: a ticked-but-missing plugin is called stale-profile, never unticked
    [Fact]
    public void ATickedButMissingPluginIsCalledStale()
    {
        var e = ReadRefusal(_w.WKey, AbsenceCauseWorld.GhostName);
        Assert.Contains("ticked in plugins.txt", e);
        Assert.Contains("stale", e);
        Assert.DoesNotContain("UNTICKED", e);
    }

    // probe: #271 refusal: a just-written (unlisted) patch keeps the readback verify path
    [Fact]
    public void AFreshPatchKeepsTheReadbackPath()
        => Assert.Contains("readback=true", FreshPatch);

    // probe: #271 refusal: ...and is told to REFRESH MO2, not to switch on a mod MO2 has never listed
    [Fact]
    public void AFreshPatchIsToldToRefreshNotToSwitchOnAMod()
    {
        var e = FreshPatch;
        Assert.Contains("refresh MO2", e, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Switch that mod on", e);
    }

    // probe: #271 refusal: the retained verify sentence names the plugin, so it has a subject standing alone
    [Fact]
    public void TheVerifySentenceNamesThePlugin()
        => Assert.Contains($"prior write into '{_w.UnlistedName}'", FreshPatch);

    // probe: #271 refusal: an unexplained name keeps the posture line AND the verify path
    [Fact]
    public void AnUnexplainedNameKeepsBothHalves()
    {
        var e = Typo;
        Assert.Contains("does not open disabled", e);
        Assert.Contains("readback=true", e);
    }

    // probe: #271 refusal: merge_plugins explains an UNTICKED donor rather than a flat not-active
    // probe: #271 refusal: and the merge refusal reads as whole words (no lost space at the splice)
    [Fact]
    public void AMergeWithAnUntickedDonorExplainsItInWholeWords()
    {
        var o = _w.Svc.MergePlugins(new[] { _w.UntickedName, _w.ReplacerName }, "HcW3MergeOut.esp");
        Assert.False(o.Success);
        Assert.Contains("UNTICKED", o.Error);
        Assert.Contains("plugins.txt", o.Error);
        Assert.Contains("records and conflict position from the ACTIVE order", o.Error);
        Assert.DoesNotContain("conflictposition", o.Error);
    }
}

/// <summary>No shipped description or doc asserts that the game does not load a file as a flat claim about the read.
/// Case-sensitive: the per-file report "the game does NOT load this file: why" is correct and allowed. Migrated from
/// the bulk-primitives-wave3 probe's #271 sweep.</summary>
[Trait("tier", "unit")]
public sealed class LoadClaimWordingTests
{
    const string Overclaim = "the game does not load this file";

    static IEnumerable<string> DescriptionsOf(ICustomAttributeProvider p) =>
        p.GetCustomAttributes(typeof(DescriptionAttribute), false).Cast<DescriptionAttribute>().Select(d => d.Description ?? "");

    // probe: #271 sweep: no MCP type/tool/parameter description asserts the overclaim
    // The probe swept types, methods and parameters; wire-shape properties (RecordsScope's members) carry descriptions too.
    [Fact]
    public void NoToolDescriptionAssertsTheOverclaim()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                 | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var descs = ToolSurface.Assembly.GetTypes().SelectMany(t => DescriptionsOf(t)
                .Concat(t.GetProperties(all).SelectMany(p => DescriptionsOf(p)))
                .Concat(t.GetMethods(all).SelectMany(m => DescriptionsOf(m)
                    .Concat(m.GetParameters().SelectMany(p => DescriptionsOf(p))))))
            .ToList();
        Assert.True(descs.Count > 50, $"only {descs.Count} descriptions found");
        Assert.DoesNotContain(descs, d => d.Contains(Overclaim, StringComparison.Ordinal));
    }

    // probe: #271 sweep: shipped doc present to sweep / does not assert the overclaim either
    [Theory]
    [InlineData("plugin/README.md")]
    [InlineData("plugin/codex/housecarl/SKILL.md")]
    public void NoShippedDocAssertsTheOverclaim(string doc)
    {
        var path = Path.Combine(HarnessPaths.RepoRoot, doc);
        Assert.True(File.Exists(path), path);
        Assert.DoesNotContain(Overclaim, File.ReadAllText(path), StringComparison.Ordinal);
    }
}

using System.ComponentModel;
using System.Reflection;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

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

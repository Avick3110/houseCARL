using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The <c>ToolNames</c> constants, the tool names the source DECLARES (<c>[McpServerTool]</c> on the
/// method), and the names the SDK REGISTERS (<c>WithToolsFromAssembly()</c> also needs
/// <c>[McpServerToolType]</c> on the declaring type) must be one set. Nothing here is regenerated and no side
/// is a hand list — all three come from reflection, so a new tool without a constant, or a declared tool no
/// caller can reach, fails here. Rationale:
/// <c>docs/decisions/0004-tool-names-are-compile-time-constants.md</c>.</summary>
public sealed class ToolNameRegistryTests
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
                               | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>The shipped tool assembly — the one the SDK scans, read off the one home for that fact rather
    /// than by naming a type expected to live there.</summary>
    static Assembly Surface => HousecarlMcp.ToolSurface.Assembly;

    /// <summary>Every value of a public string constant on <see cref="ToolNames"/>.</summary>
    static HashSet<string> Constants() => new(
        typeof(ToolNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!),
        StringComparer.Ordinal);

    /// <summary>Names carried by an <c>[McpServerTool]</c> attribute, whether or not the SDK can see them.</summary>
    static HashSet<string> Declared() => new(
        Surface.GetTypes()
            .SelectMany(t => t.GetMethods(Members))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!),
        StringComparer.Ordinal);

    /// <summary>The SDK's own predicate: the type attribute AND the method attribute.</summary>
    static HashSet<string> Registered() => new(
        Surface.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is not null)
            .SelectMany(t => t.GetMethods(Members))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!),
        StringComparer.Ordinal);

    [Fact]
    [Trait("tier", "unit")]
    public void EveryDeclaredToolHasAConstant_OtherwiseANewToolLeftTheRegistryStale()
    {
        var missing = Declared().Except(Constants()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            $"declared with no ToolNames constant: [{string.Join(", ", missing)}]. " +
            "Add one line to src/housecarl-core/ToolNames.cs — the sweep scripts are a one-shot migration " +
            "record and are not re-run, so this is the check that a new tool did not leave the registry behind.");
    }

    [Fact]
    [Trait("tier", "unit")]
    public void EveryConstantNamesADeclaredTool_OtherwiseAConstantOutlivedItsTool()
    {
        var spurious = Constants().Except(Declared()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(spurious.Length == 0,
            $"ToolNames constants naming no declared tool: [{string.Join(", ", spurious)}]. " +
            "A deleted tool's constant is deleted with it; a retired spelling gets no constant at all.");
    }

    [Fact]
    [Trait("tier", "unit")]
    public void DeclaredEqualsRegistered_TheClassThatMade470SilentForTwelveDays()
    {
        var unregistered = Declared().Except(Registered()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.True(unregistered.Length == 0,
            $"declared but NOT registered: [{string.Join(", ", unregistered)}]. " +
            "WithToolsFromAssembly() discovers only types carrying [McpServerToolType], so these tools ship " +
            "in source and are unreachable by any caller. This is #470's class.");
    }

    [Fact]
    [Trait("tier", "unit")]
    public void TheThreeSetsAreOneSet_AndTheCountIsNotZero()
    {
        var constants = Constants();
        Assert.NotEmpty(constants);
        Assert.Equal(Declared().OrderBy(x => x, StringComparer.Ordinal),
                     constants.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Registered().OrderBy(x => x, StringComparer.Ordinal),
                     constants.OrderBy(x => x, StringComparer.Ordinal));
    }
}

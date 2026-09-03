using System.Reflection;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Two repairs the 1.x cut forced on <c>housecarl_records</c>' scan lane, each with an arm per branch.
///
/// <para>The 1.x scan tool refused <c>format='dense'</c> paired with a depth greater than 1, by name. That
/// refusal lived inside the tool's own body, so deleting the tool deleted the refusal and left this tool
/// accepting the pairing and silently answering at depth 1 — while its own description says depth expansion
/// is inexpressible in dense. Both directions are held here: the pairing refuses, and dense without it is
/// still served.</para>
///
/// <para>The scan's no-scope refusal listed the 1.x tool's parameter spellings. The class of defect is not
/// "one wrong word" but "a remedy naming a parameter the tool does not declare", so the arm derives the
/// declared set off the tool method and holds every <c>xxx=</c> the sentence names against it.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsScanRefusalRepairTests : IDisposable
{
    readonly RecordsWorld _w = new();
    public void Dispose() => _w.Dispose();

    static RecordsTools.RecordsProject FieldsAt(int depth) =>
        new() { form = "fields", fields = new[] { "Keywords" }, depth = depth };

    [Fact]
    public void DenseWithADepthGreaterThanOne_IsRefusedByNameInsteadOfAnsweringAtDepthOne()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "dense", project: FieldsAt(2));

        Assert.StartsWith("error:", r);
        Assert.Contains("project.depth=2", r);
        Assert.Contains("format='dense'", r);
    }

    /// <summary>The other branch. Without this the refusal above could be widened to reject dense outright
    /// and nothing would notice.</summary>
    [Fact]
    public void DenseAtTheDefaultDepth_IsStillServed()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "dense", project: FieldsAt(1));

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>Depth 2 in text is served, so the refusal above is about the TRANSPORT and not about depth.</summary>
    [Fact]
    public void TheSameDepthInTextIsServed_SoTheRefusalIsAboutTheTransport()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "text", project: FieldsAt(2));

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>Every parameter the tool DECLARES, off the method the SDK builds the schema from. Derived so a
    /// renamed or dropped parameter moves this set with it.</summary>
    static HashSet<string> DeclaredParameters()
    {
        var m = typeof(RecordsTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is { } a
                      && a.Name == ToolNames.Records);
        return m.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void TheNoScopeRefusalNamesOnlyParametersThisToolDeclares()
    {
        var r = RecordsTools.Records(_w.Svc);
        Assert.StartsWith("error:", r);

        var declared = DeclaredParameters();
        var named = Regex.Matches(r, @"\b([a-z][a-z0-9_]*)=")
                         .Select(x => x.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal)
                         .ToArray();

        // Vacuity canary: a refusal that names no parameter would satisfy the claim below without testing it.
        Assert.NotEmpty(named);

        var undeclared = named.Where(n => !declared.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(undeclared.Length == 0,
            "the no-scope refusal tells the caller to pass [" + string.Join(", ", undeclared) +
            "], which " + ToolNames.Records + " does not declare. Refusal: " + r);
    }
}

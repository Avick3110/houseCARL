using System.Reflection;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Two refusals on <c>housecarl_records</c>' scan lane, each held in both directions.
///
/// <para>Depth expansion is inexpressible in <c>format='dense'</c>, so the pairing must refuse by name rather
/// than silently answer at depth 1 — and dense without a depth must still be served.</para>
///
/// <para>A refusal must not name a parameter the tool does not declare, so the declared set is reflected off
/// the tool method and every <c>xxx=</c> a refusal names is held against it.</para></summary>
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

    /// <summary>Without this the refusal above could be widened to reject dense outright and nothing would
    /// notice.</summary>
    [Fact]
    public void DenseAtTheDefaultDepth_IsStillServed()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "dense", project: FieldsAt(1));

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>The list lane. The dense+depth refusal above ends "or drop project.depth for the dense summary
    /// cells" — followable only in the scan lane, because a <c>formids=</c> read refuses dense outright. So that
    /// refusal is gated to the scan lane and the list lane answers with its own complete sentence.</summary>
    [Fact]
    public void DenseWithADepthInTheFormidsLane_GetsTheListLanesOwnRefusal_NotTheDepthOne()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { RecordsWorld.Fid(_w.Weapons[0]) },
                                     format: "dense", project: FieldsAt(2));

        Assert.StartsWith("error:", r);
        Assert.Contains("the scan lane's columnar form", r);
        Assert.DoesNotContain("drop project.depth", r);
    }

    /// <summary>The remedy that sentence does name is followable: the same read in text is served. Without this
    /// the list lane's refusal could name a transport that is also refused.</summary>
    [Fact]
    public void TheRemedyTheListLanesDenseRefusalNames_IsServed()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { RecordsWorld.Fid(_w.Weapons[0]) },
                                     format: "text", project: FieldsAt(2));

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>Depth 2 in text is served, so the refusal above is about the TRANSPORT and not about depth.</summary>
    [Fact]
    public void TheSameDepthInTextIsServed_SoTheRefusalIsAboutTheTransport()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "WEAP" }, format: "text", project: FieldsAt(2));

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
    }

    /// <summary>Every spelling the tool declares: its own parameters plus the members of the structured ones — a
    /// refusal legitimately says <c>project.group_by=</c>, and <c>group_by</c> is a member of the project object
    /// rather than a top-level argument. Both halves come off the method the SDK builds the schema from, so a
    /// rename moves the subject instead of needing an edit here.</summary>
    static HashSet<string> DeclaredParameters()
    {
        var m = typeof(RecordsTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is { } a
                      && a.Name == ToolNames.Records);

        var names = m.GetParameters().Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
        foreach (var p in m.GetParameters())
        {
            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (t.IsPrimitive || t == typeof(string) || t.IsArray || !t.IsClass) continue;
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                names.Add(prop.Name);
        }
        return names;
    }

    /// <summary>One row per declared parameter of <c>housecarl_records</c>, each supplying a plausible value for
    /// that parameter and nothing else, plus one row supplying nothing at all — derived from the tool's declared
    /// surface, so a parameter added later gets a row without an edit. A served call is skipped and counted; a
    /// refused one has its sentence held against the declared set. Refusals needing a COMBINATION of parameters
    /// are out of reach of this population.</summary>
    public static IEnumerable<object[]> UnderSpecifiedCalls()
    {
        yield return new object[] { "<nothing>" };
        foreach (var p in ToolParameterNames()) yield return new object[] { p };
    }

    /// <summary>The declared parameter names, off the same reflected method the declared-spelling set uses, so
    /// the two halves cannot drift apart.</summary>
    static IEnumerable<string> ToolParameterNames() =>
        ToolMethod().GetParameters()
                    .Select(p => p.Name!)
                    .Where(n => n != "svc")
                    .OrderBy(n => n, StringComparer.Ordinal);

    static MethodInfo ToolMethod() =>
        typeof(RecordsTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(x => x.GetCustomAttribute<ModelContextProtocol.Server.McpServerToolAttribute>() is { } a
                      && a.Name == ToolNames.Records);

    /// <summary>A plausible lone value for one declared parameter. Unknown parameter types fall through to the
    /// bare call, which is a refusal too — never a silent skip.</summary>
    string Refuse(string which) => which switch
    {
        "<nothing>"          => RecordsTools.Records(_w.Svc),
        "formids"            => RecordsTools.Records(_w.Svc, formids: Array.Empty<string>()),
        "types"              => RecordsTools.Records(_w.Svc, types: Array.Empty<string>()),
        "where"              => RecordsTools.Records(_w.Svc, where: new[] { "editorid contains Hc" }),
        "references"         => RecordsTools.Records(_w.Svc, references: new[] { "000800:HcRecMaster.esm" }),
        "project"            => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" },
                                    project: new RecordsTools.RecordsProject { form = "aggregate" }),
        "walk"               => RecordsTools.Records(_w.Svc,
                                    project: new RecordsTools.RecordsProject { form = "chain" },
                                    walk: new RecordsTools.RecordsWalk { direction = "reverse" }),
        "format"             => RecordsTools.Records(_w.Svc, format: "dense"),
        "versus"             => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" },
                                    project: new RecordsTools.RecordsProject { form = "delta" }),
        "offset"             => RecordsTools.Records(_w.Svc, offset: -1),
        "to_file"            => RecordsTools.Records(_w.Svc, to_file: "relative.jsonl"),
        _                    => RecordsTools.Records(_w.Svc),
    };

    /// <summary>The population's own coverage, as a number: a derived population that stops refusing anything
    /// would pass quietly, so this fails if fewer rows refuse than the shapes known to refuse today.</summary>
    [Fact]
    public void TheDerivedPopulationActuallyReachesRefusals()
    {
        var rows = UnderSpecifiedCalls().Select(r => (string)r[0]).ToList();
        var refused = rows.Count(w => Refuse(w).StartsWith("error:", StringComparison.Ordinal));

        Assert.True(rows.Count >= 10, $"only {rows.Count} declared parameters produced rows");
        Assert.True(refused >= 6, $"only {refused} of {rows.Count} rows refused — the population went vacuous");
    }

    [Theory]
    [MemberData(nameof(UnderSpecifiedCalls))]
    public void AnUnderSpecifiedCallsRefusalNamesOnlyParametersThisToolDeclares(string which)
    {
        var r = Refuse(which);
        if (!r.StartsWith("error:", StringComparison.Ordinal)) return;   // served: nothing to hold, and no silence

        var declared = DeclaredParameters();
        var named = Regex.Matches(r, @"\b([a-z][a-z0-9_]*)=")
                         .Select(x => x.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal)
                         .ToArray();

        // A refusal naming no parameter at all would satisfy the claim below without testing it.
        Assert.True(named.Length > 0, $"the refusal for '{which}' names no parameter at all: {r}");

        var undeclared = named.Where(n => !declared.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(undeclared.Length == 0,
            $"the refusal for '{which}' tells the caller to pass [" + string.Join(", ", undeclared) +
            "], which " + ToolNames.Records + " does not declare. Refusal: " + r);
    }
}

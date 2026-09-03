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

    /// <summary>The LIST lane's arm. The dense+depth refusal above ends "or drop project.depth for the dense
    /// summary cells" — a remedy that is only followable in the scan lane, because a <c>formids=</c> read
    /// refuses dense outright. Fired in the list lane it would hand the caller a second refusal, so it is
    /// gated to the scan lane and the list lane answers with its own complete sentence.</summary>
    [Fact]
    public void DenseWithADepthInTheFormidsLane_GetsTheListLanesOwnRefusal_NotTheDepthOne()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { RecordsWorld.Fid(_w.Weapons[0]) },
                                     format: "dense", project: FieldsAt(2));

        Assert.StartsWith("error:", r);
        Assert.Contains("the scan lane's columnar form", r);
        Assert.DoesNotContain("drop project.depth", r);
    }

    /// <summary>And the remedy that sentence DOES name is followable: the same read in text is served. Without
    /// this the list lane's refusal could name a transport that is also refused.</summary>
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

    /// <summary>Every spelling the tool DECLARES: its own parameters, plus the members of the structured ones —
    /// a refusal legitimately says <c>project.group_by=</c>, and <c>group_by</c> is a member of the project object
    /// rather than a top-level argument. Both halves are reflected off the method the SDK builds the schema from,
    /// so a renamed parameter or member moves the subject with it instead of needing an edit here.</summary>
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

    /// <summary>Under-specified calls, each of which this tool answers with a refusal that tells the caller which
    /// parameter to reach for. One theory row each, so a RED names the call that produced the bad sentence.</summary>
    public static IEnumerable<object[]> UnderSpecifiedCalls() => new[]
    {
        new object[] { "nothing selected" },
        new object[] { "a body predicate with no bounding scope" },
        new object[] { "a reverse-reference scan with no bounding scope" },
        new object[] { "an aggregate form with no group key" },
    };

    string Refuse(string which) => which switch
    {
        "nothing selected" => RecordsTools.Records(_w.Svc),
        "a body predicate with no bounding scope" =>
            RecordsTools.Records(_w.Svc, where: new[] { "editorid contains Hc" }),
        "a reverse-reference scan with no bounding scope" =>
            RecordsTools.Records(_w.Svc, references: new[] { "000800:HcRecMaster.esm" }),
        _ => RecordsTools.Records(_w.Svc, types: new[] { "WEAP" },
                                  project: new RecordsTools.RecordsProject { form = "aggregate" }),
    };

    /// <summary>
    /// The class this arm exists for: a remedy naming a parameter the tool does not declare. The instance that
    /// prompted it was the scan's no-scope refusal in the service, which told the caller to pass <c>type=</c> and
    /// <c>editorid_contains=</c> — the 1.x scan tool's spellings, which this tool has as <c>types=</c> and the
    /// <c>where=</c> grammar's editorid term.
    ///
    /// <para>That exact sentence is NOT what this arm drives, and saying so is the point: measured, this tool
    /// refuses an unscoped call with its own sentence first, so the service's copy is unreachable from here. It
    /// was corrected anyway; what is PINNED is the reachable family — the refusals these four calls actually
    /// produce. The declared set is reflected off the tool method and the members of its structured parameters,
    /// so a rename moves the subject rather than needing an edit here.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(UnderSpecifiedCalls))]
    public void AnUnderSpecifiedCallsRefusalNamesOnlyParametersThisToolDeclares(string which)
    {
        var r = Refuse(which);
        Assert.StartsWith("error:", r);

        var declared = DeclaredParameters();
        var named = Regex.Matches(r, @"\b([a-z][a-z0-9_]*)=")
                         .Select(x => x.Groups[1].Value)
                         .Distinct(StringComparer.Ordinal)
                         .ToArray();

        // Vacuity canary: a refusal naming no parameter would satisfy the claim below without testing it.
        Assert.True(named.Length > 0, $"the refusal for '{which}' names no parameter at all: {r}");

        var undeclared = named.Where(n => !declared.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.True(undeclared.Length == 0,
            $"the refusal for '{which}' tells the caller to pass [" + string.Join(", ", undeclared) +
            "], which " + ToolNames.Records + " does not declare. Refusal: " + r);
    }
}

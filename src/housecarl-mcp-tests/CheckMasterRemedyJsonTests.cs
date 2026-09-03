using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The json twin of the missing-master remedy: <c>format='json'</c> carries the SAME install-vs-enable split the
/// text render prints, as a member of the plugin object rather than as a sentence.
///
/// <para><b>Why it has to.</b> The text lane says "NOT installed anywhere … [install them]" for one master and
/// "installed but NOT ACTIVE … [enable them]" for another. The json lane carried one undifferentiated
/// <c>missing_masters</c> array, so the identical call in the other format returned two names with nothing to
/// tell them apart, and a json caller could not pick the remedy that would work. Two transports saying different
/// things about one sweep is the divergence the merged surface exists to make impossible.</para>
///
/// <para><b>null is not empty.</b> <c>installed_but_inactive_masters</c> is an array where the split was made and
/// <c>null</c> where it was not — the rule <see cref="PluginErrors"/>'s own summary states. Empty would claim
/// "none of these is merely disabled", which is an assertion about an install nobody looked at.</para>
///
/// <para>Driven over the same synthetic MO2 instance as <see cref="CheckMasterRemedyTests"/>, through the method
/// the MCP server publishes, for the reason that class states.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class CheckMasterRemedyJsonTests : IClassFixture<CheckMasterRemedyFixture>
{
    readonly CheckMasterRemedyWorld W;
    public CheckMasterRemedyJsonTests(CheckMasterRemedyFixture f) => W = f.W;

    /// <summary>The errors family's plugin objects in a json <c>check</c> response.</summary>
    static JsonElement ErrorsPlugins(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("families").GetProperty("errors").GetProperty("plugins");

    static string[] Strings(JsonElement a) => a.EnumerateArray().Select(e => e.GetString()!).ToArray();

    /// <summary>Both shortfalls in <c>missing_masters</c> — the count and the list are unchanged — and exactly the
    /// one whose file is in a disabled mod in <c>installed_but_inactive_masters</c>. The absent master must NOT be
    /// in the subset: telling a caller to enable a file that is not in the install is the wrong remedy delivered
    /// confidently, which is worse than the union sentence this replaced.</summary>
    [Fact]
    public void TheJsonPluginObjectCarriesTheInstalledButInactiveSubsetAlongsideTheUnionList()
    {
        var json = CheckTools.CheckTool(Svc, plugins: new[] { W.PatchName },
                                        findings: new[] { "missing_masters" }, format: "json");

        var plugin = Assert.Single(ErrorsPlugins(json).EnumerateArray().ToList());
        Assert.Equal(new[] { W.AbsentName, W.GhostName },
                     Strings(plugin.GetProperty("missing_masters")).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        var subset = plugin.GetProperty("installed_but_inactive_masters");
        Assert.Equal(JsonValueKind.Array, subset.ValueKind);
        Assert.Equal(new[] { W.GhostName }, Strings(subset));
    }

    /// <summary>The two transports name the same master on the same side of the split. Read off ONE call each over
    /// one install, so a divergence is the two renders disagreeing rather than two fixtures differing.</summary>
    [Fact]
    public void TheJsonSubsetAndTheTextRemedyPutTheSameMasterOnTheEnableSide()
    {
        var text = CheckTools.CheckTool(Svc, plugins: new[] { W.PatchName }, findings: new[] { "missing_masters" });
        var json = CheckTools.CheckTool(Svc, plugins: new[] { W.PatchName },
                                        findings: new[] { "missing_masters" }, format: "json");

        var enableLine = text.Split('\n').Select(l => l.Trim())
            .Single(l => l.StartsWith("missing master(s) installed but NOT ACTIVE", StringComparison.Ordinal));
        var subset = Strings(ErrorsPlugins(json).EnumerateArray().Single().GetProperty("installed_but_inactive_masters"));

        var named = Assert.Single(subset);
        Assert.Contains(named, enableLine);
        Assert.DoesNotContain(W.AbsentName, enableLine);
    }

    /// <summary>The null case. A report the MO2 layer never classified — what the core sweep alone produces, and
    /// what <c>ClassifyMissingMasters</c> returns unchanged when the composition cannot be read — leaves the member
    /// <c>null</c>, not <c>[]</c>.
    ///
    /// <para>Driven at the render rather than end to end, and deliberately: reaching that catch needs an MO2
    /// profile whose composition read THROWS, which a fixture can only produce by making the instance
    /// unreadable mid-call. The unclassified report is the same input the catch hands the render, and the text
    /// lane's twin arm (<c>AnUnclassifiedReportKeepsTheUnionRemedy_NullIsNotAnEmptySubset</c>) is driven the
    /// same way for the same reason.</para></summary>
    [Fact]
    public void AnUnclassifiedReportLeavesTheJsonSubsetNull_NotAnEmptyArray()
    {
        var unclassified = new PluginErrors(W.PatchName, Array.Empty<DanglingRef>(),
                                            new[] { W.AbsentName, W.GhostName }, 0, Array.Empty<string>(), null);
        var result = new ErrorCheckResult(new[] { unclassified }, 1, 0, 2, 0,
                                          new Dictionary<string, string>(), null)
                     { Classes = ErrorFindingClass.MissingMasters };
        Assert.True(SweepFamilySelection.TryParse(new[] { "missing_masters" }, out var selection, out _));
        var sweep = new CheckSweep(selection!, Errors: result);

        var plugin = ErrorsPlugins(JsonWire.RenderCheck(sweep, 80_000)).EnumerateArray().Single();

        Assert.Equal(JsonValueKind.Null, plugin.GetProperty("installed_but_inactive_masters").ValueKind);
        Assert.Equal(new[] { W.AbsentName, W.GhostName }, Strings(plugin.GetProperty("missing_masters")));
    }

    LoadOrderService Svc => W.Svc;
}

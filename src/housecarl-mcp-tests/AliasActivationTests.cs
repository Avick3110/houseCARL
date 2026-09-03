// Converted-from: BindingShimProbe
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The SPEC §5.3 alias table, held against the REAL published schemas — the executable form of "dormant by
/// construction". (BindingShimProbe's CENSUS arm.)
///
/// <para>The probe computed each row's activation set from the served schemas and diffed it against a
/// 144-row eyeballed literal. The literal caught two classes: a row that silently fails to fire (a mistyped
/// candidate) and a row that fires somewhere its author never checked. It also, being a literal, said nothing
/// about whether an activation WORKS — the census never sent a call.</para>
///
/// <para>Here the population is one cell per activation, derived from the shipped
/// <see cref="ToolCallShim.ResolveAliases"/> run over the surface's own generated schemas, and every cell is
/// then DRIVEN OVER THE WIRE. So a row that computes as active but dead-ends at the caller is a named RED,
/// which the literal could not see, and a row's activation set is an output of the surface rather than of who
/// remembered to re-eyeball a list. A row that fires nowhere produces no cell — the same silence the literal
/// gave it — which is correct for the reverse edges of the synonym pairs, dormant by construction.</para>
///
/// <para><b>Not carried, deliberately (OWED):</b> the exact-set half — "no row fires anywhere unintended".
/// "Unintended" was the human judgement the literal encoded; an oracle for it is either the activation
/// arithmetic compared against itself, or a checked-in expectation somebody maintains, which is the shape
/// this surface has spent the wave removing. Closing it honestly wants a generated-and-reviewed snapshot
/// (write the derived set to a data file on an update run, diff it in review), which is guard growth rather
/// than a conversion fold.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class AliasActivationTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public AliasActivationTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    // ---- the derivation: one cell per activation, off the surface's own schemas ------------------------

    /// <summary>The tool schemas as the generator emits them. MemberData must be static, so it cannot read
    /// the injected fixture; each test re-checks its cell against the LIVE published schema, which is what
    /// makes a disagreement between the two a failure rather than a silently different population.</summary>
    static IEnumerable<(string Tool, JsonElement Schema)> Surface()
    {
        foreach (var t in PreFlattenSchemas.Read())
            yield return (t.Name, JsonSerializer.Deserialize<JsonElement>(t.Schema.ToJsonString()));
    }

    /// <summary>A JSON literal of a kind the declared parameter can take — the content is irrelevant; the
    /// KIND is what the table's kind gate judges, so a sample of the wrong kind would under-report.</summary>
    static JsonElement SampleFor(JsonElement propertySchema)
    {
        string?[] types = propertySchema.TryGetProperty("type", out var t)
            ? (t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(e => e.GetString()).ToArray()
                : new[] { t.GetString() })
            : Array.Empty<string?>();

        var raw = types.FirstOrDefault(x => x is not null and not "null") switch
        {
            "array" => """["zzz"]""",
            "boolean" => "true",
            "integer" or "number" => "1",
            "object" => "{}",
            _ => "\"zzz\"",
        };
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    /// <summary>Run the shim's alias pass over one single-argument call and report where the key landed.</summary>
    static string? LandsOn(string tool, string old, JsonElement value, JsonElement schema)
    {
        var request = new CallToolRequestParams
        {
            Name = tool,
            Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { [old] = value },
        };
        ToolCallShim.ResolveAliases(request, schema);
        var key = request.Arguments!.Keys.Single();
        return key == old ? null : key;
    }

    /// <summary>Every (tool, rename row) pair the shim actually renames, as "tool: old -&gt; target".</summary>
    internal static IEnumerable<(string Tool, string Old, string Target)> RenameActivations()
    {
        foreach (var (tool, schema) in Surface())
        {
            if (!schema.TryGetProperty("properties", out var props)) continue;
            var declared = props.EnumerateObject().ToArray();

            foreach (var row in AliasTable.AllRenames)
            {
                if (declared.Any(p => ToolCallShim.Normalize(p.Name) == row.Old)) continue;   // declared here: not an alias

                // Try each candidate's own declared schema: a row whose only landing is an array parameter
                // must not be judged on a string sample, and vice versa.
                foreach (var candidate in row.Candidates)
                {
                    var target = declared.FirstOrDefault(p => ToolCallShim.Normalize(p.Name) == candidate);
                    if (target.Value.ValueKind == JsonValueKind.Undefined) continue;
                    if (LandsOn(tool, row.Old, SampleFor(target.Value), schema) is not { } landed) continue;
                    yield return (tool, row.Old, landed);
                    break;
                }
            }
        }
    }

    /// <summary>Every (tool, dissolution row) pair whose gate parameters this tool declares.</summary>
    internal static IEnumerable<(string Tool, string Old, string Hint)> HintActivations()
    {
        foreach (var (tool, schema) in Surface())
        {
            if (!schema.TryGetProperty("properties", out var props)) continue;
            var declared = props.EnumerateObject()
                                .Select(p => ToolCallShim.Normalize(p.Name))
                                .ToHashSet(StringComparer.Ordinal);

            foreach (var row in AliasTable.AllDissolutions)
            {
                if (declared.Contains(row.Old)) continue;                   // declared here: nothing dissolved
                if (!row.GateParams.All(declared.Contains)) continue;
                yield return (tool, row.Old, row.Hint);
            }
        }
    }

    public static IEnumerable<object[]> Renames() =>
        RenameActivations().Select(a => new object[] { a.Tool, a.Old, a.Target });

    public static IEnumerable<object[]> Hints() =>
        HintActivations().Select(a => new object[] { a.Tool, a.Old });

    // ---- the arms ---------------------------------------------------------------------------------------

    /// <summary>
    /// One cell per rename activation: the old spelling, sent to that tool over the wire, must not come back
    /// as an unknown parameter — the correction the row exists to skip — and the shim must land it on the
    /// SAME declared parameter against the schema the server actually published.
    ///
    /// <para>Both halves matter. The wire says the row works end-to-end; the target check says it works onto
    /// the pole its own row names, which a wire arm cannot see whenever the target takes any JSON (a
    /// re-review finding on this table pointed a write tool's row at a read-only pole and the wire stayed
    /// green).</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Renames))]
    public void EveryAliasActivationRenamesOntoItsOwnTargetAndIsNotRefusedOverTheWire(
        string tool, string old, string target)
    {
        var published = _s.PublishedTools[tool].GetProperty("inputSchema");
        var propertySchema = published.GetProperty("properties").GetProperty(target);

        Assert.Equal(target, LandsOn(tool, old, SampleFor(propertySchema), published));

        var r = _s.Call(tool, $$"""{"{{old}}": {{SampleFor(propertySchema).GetRawText()}}}""");
        Assert.DoesNotContain($"unknown parameter: {old}", r.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ServerFixture.GenericError, r.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// One cell per dissolution activation: the retired spelling is refused BY NAME and the refusal carries
    /// that row's own hint. The census computed which gates were satisfied; it never checked that anything
    /// reached the caller, which is the row's entire purpose.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hints))]
    public void EveryDissolutionActivationReachesTheCallerWithItsOwnHint(string tool, string old)
    {
        var hint = AliasTable.AllDissolutions.First(d => d.Old == old).Hint;
        var r = _s.Call(tool, $$"""{"{{old}}":"zzz"}""");

        Assert.True(r.IsError, r.Describe());
        Assert.Contains($"unknown parameter: {old} ({hint})", r.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The vacuity canary, the number a reviewer reads, AND the direction a derived population cannot see on
    /// its own.
    ///
    /// <para>The population comes from the alias table, so a row that stops firing takes its own subject away
    /// with it: mistype a live candidate and the activation and its cell vanish together, silently. That is one
    /// of the two classes the retired literal caught, and it is the reason the counts are now PINNED as well as
    /// printed. A count is not the eyeballed 144-row literal — it is one number per sweep, it moves only when
    /// the surface or the table moves, and the printed listing beside it says which row changed.</para>
    /// </summary>
    [Fact]
    public void BothActivationSweepsHaveSubjects()
    {
        var renames = RenameActivations().ToArray();
        var hints = HintActivations().ToArray();

        _out.WriteLine($"rename activations: {renames.Length}");
        foreach (var a in renames.OrderBy(a => $"{a.Tool}:{a.Old}", StringComparer.Ordinal))
            _out.WriteLine($"  {a.Tool}: {a.Old} -> {a.Target}");
        _out.WriteLine($"dissolution activations: {hints.Length}");
        foreach (var a in hints.OrderBy(a => $"{a.Tool}:{a.Old}", StringComparer.Ordinal))
            _out.WriteLine($"  {a.Tool}: {a.Old} => hint");

        Assert.NotEmpty(renames);
        Assert.NotEmpty(hints);

        // The dormant-row direction. Measured on this surface at the 1.x cut; a change here is either a real
        // surface change (update the number, and the listing above names the row) or a row that went dead.
        Assert.Equal(91, renames.Length);
        Assert.Equal(23, hints.Length);
    }
}

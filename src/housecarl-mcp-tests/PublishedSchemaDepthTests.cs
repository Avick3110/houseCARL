using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The optional depth cut on the published schemas (<c>HOUSECARL_MAX_SCHEMA_DEPTH</c>), measured on the surface a
/// client actually receives. A provider that enforces a nesting cap refuses the WHOLE server at
/// <c>tools/list</c>, naming no tool, so the subject is the served document rather than the pass's own return.
///
/// <para>DEPTH here is raw JSON container nesting — the measure issue #730 reported with and the refusing
/// providers use — read by this class's own walk rather than by calling the pass, so a change to the pass that
/// also changed its measure would still be caught.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedSchemaDepthTests : IDisposable
{
    const int Cap = 10;

    /// <summary>The shared server — the plain environment, which is what "unset" means.</summary>
    readonly ServerFixture _plain;

    /// <summary>This class's own server, started with the cap. Never the shared one: the variable it sets is the
    /// subject, and every other stdio test reads the uncapped surface.</summary>
    readonly ServerFixture _capped =
        new(new Dictionary<string, string> { [SchemaDepthCap.Variable] = Cap.ToString() });

    public PublishedSchemaDepthTests(ServerFixture plain) { _plain = plain; }

    public void Dispose() => _capped.Dispose();

    /// <summary>Raw JSON container nesting: an object or array is one level plus its deepest member, a scalar is
    /// none.</summary>
    static int Depth(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => 1 + node.EnumerateObject().Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
        JsonValueKind.Array => 1 + node.EnumerateArray().Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    static JsonElement Schema(ServerFixture server, string tool) =>
        server.PublishedTools[tool].GetProperty("inputSchema");

    // ---- unset is today's surface, byte for byte -----------------------------------------------------------

    /// <summary>The whole promise of the default: a server with the variable unset publishes exactly what it
    /// published before the cut existed. Asserted as the pass's own no-op over every served schema — with no cap
    /// configured it must report no change AND leave the document byte-identical, so nothing downstream of it
    /// (the re-serialize in <c>PublishSchemas</c> included) can move a byte.</summary>
    [Fact]
    public void WithTheVariableUnsetTheCutChangesNoPublishedSchemaByAByte()
    {
        foreach (var name in _plain.PublishedNames)
        {
            var before = Schema(_plain, name).GetRawText();
            var document = (JsonObject)JsonNode.Parse(before)!;

            Assert.False(SchemaDepthCap.Cut(document, null), $"{name}: the uncapped cut reported a change.");
            Assert.Equal(before, document.ToJsonString());
        }
    }

    /// <summary>The two tools #730 named still publish deeper than the cap when it is unset — the capped
    /// assertions below would pass vacuously on a surface that had shallowed out on its own.</summary>
    [Fact]
    public void UncappedTheWriteSurfaceStillPublishesDeeperThanTheCap()
    {
        Assert.True(Depth(Schema(_plain, ToolNames.Create)) > Cap);
        Assert.True(Depth(Schema(_plain, ToolNames.Apply)) > Cap);
    }

    // ---- the cut --------------------------------------------------------------------------------------------

    /// <summary>Every published schema, not just the two that needed it, fits the configured cap.</summary>
    [Fact]
    public void EveryPublishedSchemaFitsTheConfiguredDepth()
    {
        var over = _capped.PublishedNames
            .Where(n => Depth(Schema(_capped, n)) > Cap)
            .Select(n => $"{n}: {Depth(Schema(_capped, n))}")
            .ToArray();

        Assert.Equal(Array.Empty<string>(), over);
    }

    /// <summary>The cut takes no tool off the surface: a capped server publishes the same names in the same
    /// order as an uncapped one.</summary>
    [Fact]
    public void TheCutPublishesEveryToolItPublishedUncapped()
    {
        Assert.Equal(_plain.PublishedNames, _capped.PublishedNames);
    }

    /// <summary>The zero-<c>$ref</c> invariant still holds under the cut. A terminator is a leaf, so it cannot
    /// introduce one — asserted rather than reasoned, with the same detector the uncapped surface is held to.</summary>
    [Fact]
    public void NoCutSchemaCarriesARefMemberInAnySpelling()
    {
        var refs = new List<string>();
        foreach (var name in _capped.PublishedNames)
        {
            if (!_capped.PublishedTools[name].TryGetProperty("inputSchema", out var schema)) continue;
            foreach (var (path, value) in PreFlattenSchemas.CollectRefMembers(schema, "#"))
                refs.Add($"{name}: {path} = {PreFlattenSchemas.Trunc(value)}");
        }

        Assert.Equal(Array.Empty<string>(), refs.ToArray());
    }

    /// <summary>
    /// Every cut schema is still a schema. The trap #730's reporter hit: <c>properties</c>,
    /// <c>patternProperties</c> and <c>$defs</c> are name-to-schema DICTIONARIES, and a cut that replaced one
    /// with a terminator would publish a property named <c>type</c> and a property named <c>description</c> —
    /// a document that no longer validates, which a strict provider reports as an <c>anyOf</c> failure rather
    /// than as a depth error.
    ///
    /// <para>Checked structurally over every node the walk can place: a schema position holds a schema (an
    /// object or a boolean), a dictionary position holds a dictionary OF schemas, and every <c>type</c> names
    /// JSON Schema types.</para>
    /// </summary>
    [Fact]
    public void EveryCutSchemaIsStillAValidSchemaDocument()
    {
        var faults = new List<string>();
        foreach (var name in _capped.PublishedNames)
            SchemaShape.Check(Schema(_capped, name), $"{name}#", faults);

        Assert.Equal(Array.Empty<string>(), faults.ToArray());
    }

    /// <summary>The same walk over the UNCAPPED surface, so a green result above says the cut kept the document
    /// valid rather than that the check passes anything.</summary>
    [Fact]
    public void TheUncappedSurfaceIsValidUnderTheSameWalk()
    {
        var faults = new List<string>();
        foreach (var name in _plain.PublishedNames)
            SchemaShape.Check(Schema(_plain, name), $"{name}#", faults);

        Assert.Equal(Array.Empty<string>(), faults.ToArray());
    }

    /// <summary>Only the PUBLISHED schema is bounded. A nested compose the cut no longer spells out is still
    /// accepted by <c>tools/call</c>: it binds and reaches the tool body, which answers in the unconfigured
    /// server's own words.</summary>
    [Fact]
    public void ACallNestedDeeperThanTheCutStillReachesTheToolBody()
    {
        var r = _capped.Call(ToolNames.Apply, """
            {"ops":[{"formid":"013989:Skyrim.esm","field_path":"Conditions","op":"Add",
              "compose":{"type":"Condition","sets":[{"path":"Data","compose":{"type":"FunctionConditionData",
                "sets":[{"path":"Function","value":"GetItemCount"}]}}]}}]}
            """);

        Assert.False(r.IsError, r.Describe());
        Assert.True(r.BodyRan, r.Describe());
    }

    // ---- a value that is not a depth ------------------------------------------------------------------------

    /// <summary>A value that is not a whole number of 1 or more is REFUSED at startup, in one sentence on
    /// stderr. Ignoring it would boot a server publishing the schemas the caller set the variable to avoid, and
    /// the provider's refusal names neither houseCARL nor the variable.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("10.5")]
    [InlineData("ten")]
    [InlineData("10 levels")]
    public void AnInvalidDepthIsRefusedAtStartupInOneSentence(string value)
    {
        var (exit, stderr) = Boot(value);

        Assert.NotEqual(0, exit);
        var said = stderr.Trim();
        Assert.Contains(SchemaDepthCap.Variable, said, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', said);
        Assert.EndsWith(".", said, StringComparison.Ordinal);
    }

    /// <summary>Start the real exe with the variable set, feed it nothing, and collect what it said and how it
    /// left. A refusal exits before the transport, so there is no handshake to drive.</summary>
    static (int Exit, string Stderr) Boot(string value)
    {
        var exe = Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp", "bin",
                               HarnessPaths.Configuration, "net9.0", "housecarl-mcp.exe");
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment[SchemaDepthCap.Variable] = value;

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardInput.Close();
        if (!proc.WaitForExit(30_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Assert.Fail($"The server did not exit within 30s with {SchemaDepthCap.Variable}=\"{value}\" — an " +
                        "unusable value must refuse the start, not boot and serve.");
        }
        return (proc.ExitCode, stderr.GetAwaiter().GetResult());
    }
}

/// <summary>A structural read of a JSON Schema document: where a schema is expected, and where a dictionary of
/// schemas is. Written here rather than taken off the pass under test, so the two cannot share a blind spot.</summary>
static class SchemaShape
{
    static readonly HashSet<string> Dictionaries =
        new(StringComparer.Ordinal) { "properties", "patternProperties", "$defs", "definitions" };

    static readonly HashSet<string> SubSchemas =
        new(StringComparer.Ordinal)
        {
            "items", "additionalProperties", "not", "contains", "propertyNames",
            "if", "then", "else", "unevaluatedItems", "unevaluatedProperties",
        };

    static readonly HashSet<string> SchemaLists =
        new(StringComparer.Ordinal) { "anyOf", "oneOf", "allOf", "prefixItems" };

    static readonly HashSet<string> Types =
        new(StringComparer.Ordinal) { "object", "array", "string", "number", "integer", "boolean", "null" };

    internal static void Check(JsonElement node, string path, List<string> faults)
    {
        if (node.ValueKind is JsonValueKind.True or JsonValueKind.False) return;   // a boolean schema is legal
        if (node.ValueKind != JsonValueKind.Object)
        {
            faults.Add($"{path}: a schema position holds {node.ValueKind}, not an object or boolean.");
            return;
        }

        foreach (var member in node.EnumerateObject())
        {
            var at = $"{path}/{member.Name}";
            if (Dictionaries.Contains(member.Name))
            {
                if (member.Value.ValueKind != JsonValueKind.Object)
                {
                    faults.Add($"{at}: a name-to-schema dictionary holds {member.Value.ValueKind}.");
                    continue;
                }
                foreach (var entry in member.Value.EnumerateObject())
                    Check(entry.Value, $"{at}/{entry.Name}", faults);
            }
            else if (SubSchemas.Contains(member.Name))
            {
                Check(member.Value, at, faults);
            }
            else if (SchemaLists.Contains(member.Name))
            {
                if (member.Value.ValueKind != JsonValueKind.Array)
                {
                    faults.Add($"{at}: a list of schemas holds {member.Value.ValueKind}.");
                    continue;
                }
                var i = 0;
                foreach (var arm in member.Value.EnumerateArray()) Check(arm, $"{at}/{i++}", faults);
            }
            else if (member.Name == "type")
            {
                foreach (var (name, where) in Spellings(member.Value, at))
                    if (!Types.Contains(name)) faults.Add($"{where}: \"{name}\" is not a JSON Schema type.");
            }
            else if (member.Name == "required" && member.Value.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var entry in member.Value.EnumerateArray())
                    if (entry.ValueKind != JsonValueKind.String)
                        faults.Add($"{at}/{i++}: required names {entry.ValueKind}, not a string.");
            }
        }
    }

    /// <summary>The type names on a <c>type</c> member, whichever of its two legal spellings it uses.</summary>
    static IEnumerable<(string Name, string Where)> Spellings(JsonElement type, string at)
    {
        switch (type.ValueKind)
        {
            case JsonValueKind.String:
                yield return (type.GetString()!, at);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var entry in type.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String) yield return (entry.GetString()!, $"{at}/{i}");
                    else yield return ($"<{entry.ValueKind}>", $"{at}/{i}");
                    i++;
                }
                break;
            default:
                yield return ($"<{type.ValueKind}>", at);
                break;
        }
    }
}

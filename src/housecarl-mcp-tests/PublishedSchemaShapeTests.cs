using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HousecarlMcp;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// The schemas the server actually SERVES: the <c>@file</c> union on the JsonElement-typed list parameters, and
/// the no-<c>$ref</c> invariant every published schema must satisfy. Read off the served surface rather than the
/// generator's output because a strict provider rejects a recursive schema by refusing the WHOLE server at
/// <c>tools/list</c>, naming no tool.
///
/// <para>Every subject set here is DERIVED, never named by hand: the union rows come off
/// <see cref="ToolSchemas.FileListParams"/> and their expected members off each row's own element type, and the
/// <c>$ref</c> sites come off the pre-flatten surface.</para>
/// </summary>
[Collection("server")]
[Trait("tier", "stdio")]
public sealed class PublishedSchemaShapeTests
{
    readonly ServerFixture _s;
    readonly ITestOutputHelper _out;
    public PublishedSchemaShapeTests(ServerFixture s, ITestOutputHelper output) { _s = s; _out = output; }

    // ---- the @file unions -------------------------------------------------------------------------------

    public static IEnumerable<object[]> FileListUnions() =>
        ToolSchemas.FileListParams.Select(p => new object[] { p.Tool, p.Parameter });

    static ToolSchemas.FileListParam Row(string tool, string parameter) =>
        ToolSchemas.FileListParams.Single(p => p.Tool == tool && p.Parameter == parameter);

    /// <summary>The wire member names of a union row's element type — <c>[JsonPropertyName]</c> where it is
    /// spelled, the property name otherwise, and never a <c>[JsonIgnore]</c> member (which is deliberately
    /// off the published schema and off the strict reader's member set).</summary>
    static string[] WireMembersOf(Type elementArrayType)
    {
        var element = elementArrayType.GetElementType()
            ?? throw new InvalidOperationException($"{elementArrayType} is not an array type.");
        return element.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                      .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                      .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name)
                      .OrderBy(n => n, StringComparer.Ordinal)
                      .ToArray();
    }

    [Theory]
    [MemberData(nameof(FileListUnions))]
    public void EveryToolNamedInFileListParamsIsPublished(string tool, string parameter)
    {
        Assert.True(_s.PublishedTools.ContainsKey(tool),
            $"{tool} carries a FileListParams row for {parameter}= but tools/list does not publish it. A stale " +
            "row degrades that parameter back to the bare {} its declared JsonElement gives — which has no refs " +
            "to dangle, so the generic sweep passes over it silently.");
    }

    /// <summary>No C# type expresses "an array of ops OR the string '@path'", so the generator publishes
    /// <c>{}</c> for the declared <c>JsonElement</c> and the publication pass republishes the union. Asserted
    /// once per row.</summary>
    [Theory]
    [MemberData(nameof(FileListUnions))]
    public void EveryFileListUnionPublishesAnyOfGeneratedArrayOrString(string tool, string parameter)
    {
        var node = _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("properties")
                     .GetProperty(parameter);
        var arms = node.GetProperty("anyOf");

        Assert.Equal(2, arms.GetArrayLength());
        Assert.Equal("array", arms[0].GetProperty("type").GetString());
        Assert.Equal("string", arms[1].GetProperty("type").GetString());
    }

    /// <summary>The array arm must be the GENERATED element schema, not an empty placeholder. The expected
    /// member set is reflected off the row's own element type, so adding a member to the DTO tracks here by
    /// construction.</summary>
    [Theory]
    [MemberData(nameof(FileListUnions))]
    public void EveryFileListUnionsArrayArmCarriesItsGeneratedElementMembers(string tool, string parameter)
    {
        var published = _s.PublishedTools[tool].GetProperty("inputSchema").GetProperty("properties")
                          .GetProperty(parameter).GetProperty("anyOf")[0]
                          .GetProperty("items").GetProperty("properties")
                          .EnumerateObject().Select(p => p.Name)
                          .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(WireMembersOf(Row(tool, parameter).ElementArrayType), published);
    }

    // ---- the $ref invariant -----------------------------------------------------------------------------

    /// <summary>
    /// Zero <c>$ref</c> members anywhere in any published schema, whatever they hold.
    ///
    /// <para>The predicate is deliberately WIDER than the flattener's and shares no code with it: the
    /// publication pass gates inlining on a same-document pointer because that is what it knows how to resolve,
    /// while this asks only whether the MEMBER is there. Spelled the same way, a ref the pass cannot see would
    /// also be one the detector cannot see. Whether a survivor resolves is reported as detail, since that says
    /// WHICH failure it is.</para>
    /// </summary>
    [Fact]
    public void NoPublishedToolSchemaCarriesARefMemberInAnySpelling()
    {
        var refs = new List<string>();
        foreach (var name in _s.PublishedNames)
        {
            if (!_s.PublishedTools[name].TryGetProperty("inputSchema", out var schema)) continue;
            foreach (var (path, value) in PreFlattenSchemas.CollectRefMembers(schema, "#"))
            {
                var note = value.ValueKind != JsonValueKind.String ? "(NON-STRING)"
                    : PreFlattenSchemas.PointerResolves(schema, value.GetString()!) ? "(resolves)" : "(DANGLING)";
                refs.Add($"{name}: {path} = {PreFlattenSchemas.Trunc(value)} {note}");
            }
        }

        Assert.Equal(Array.Empty<string>(), refs.ToArray());
    }

    // ---- anti-amputation, over subjects derived from the pre-flatten surface ----------------------------
    //
    // The invariant above is satisfied just as well by DELETING every recursive branch, which would narrow the
    // published contract silently. So every site that carried a $ref BEFORE the pass must still be spelled out
    // after it.
    //
    // The @file union pass is replayed on the pre-flatten document because the published one has it and the
    // pointers must line up. The flatten replaces each ref node IN PLACE, so a pre-flatten ref path is the
    // same path in the published document — that is what makes these subjects portable across the two.

    static IReadOnlyList<(string Tool, int Sites, int Derived, string? Step, string? Cycle,
                          List<(string Path, JsonObject Node)> Nodes)>? _carriers;

    static IReadOnlyList<(string Tool, int Sites, int Derived, string? Step, string? Cycle,
                          List<(string Path, JsonObject Node)> Nodes)> Carriers()
    {
        if (_carriers is not null) return _carriers;

        var list = new List<(string, int, int, string?, string?, List<(string, JsonObject)>)>();
        foreach (var tool in PreFlattenSchemas.Read())
        {
            var pre = (JsonObject)tool.Schema.DeepClone();
            var unions = ToolSchemas.FileListParams.Where(p => p.Tool == tool.Name).ToList();
            if (unions.Count > 0) ToolSchemas.RewriteFileListUnions(pre, unions);

            var sites = PreFlattenSchemas.RefNodes(pre);
            if (sites.Count == 0) continue;

            var derived = PreFlattenSchemas.DeriveRecursions(sites);
            list.Add((tool.Name, sites.Count, derived.Count,
                      derived.Count == 1 ? derived[0].Step : null,
                      derived.Count == 1 ? derived[0].Cycle : null,
                      sites));
        }
        return _carriers = list;
    }

    public static IEnumerable<object[]> RefCarryingTools() =>
        Carriers().Select(c => new object[] { c.Tool });

    public static IEnumerable<object[]> RefSites() =>
        Carriers().SelectMany(c => c.Nodes.Select(n => new object[] { c.Tool, n.Path }));

    public static IEnumerable<object[]> RecursiveSites() =>
        Carriers().Where(c => c.Cycle is not null)
                  .SelectMany(c => c.Nodes.Where(n => PreFlattenSchemas.RefPointer(n.Node) == c.Cycle)
                                          .Select(n => new object[] { c.Tool, n.Path }));

    [Theory]
    [MemberData(nameof(RefCarryingTools))]
    public void EveryToolCarryingPreFlattenRefSitesIsPublished(string tool) =>
        Assert.True(_s.PublishedTools.ContainsKey(tool),
                    $"{tool} carries pre-flatten $ref sites but tools/list does not publish it.");

    /// <summary>EXACTLY one recursion step must be derivable from a tool's own refs. A tool carrying two
    /// distinct cycles is refused here rather than measured on whichever the walk saw last, which would leave
    /// the tests below claiming a whole population while checking a narrower subset of it.</summary>
    [Theory]
    [MemberData(nameof(RefCarryingTools))]
    public void ExactlyOneRecursionStepIsDerivableFromACarriersOwnRefSites(string tool)
    {
        var c = Carriers().Single(x => x.Tool == tool);
        Assert.True(c.Derived == 1,
            c.Derived == 0
                ? $"{tool}: no $ref points at one of its own ancestors on a segment boundary; the expansion " +
                  "arms have nothing to measure."
                : $"{tool}: {c.Derived} distinct cycles — " + string.Join(" | ",
                    PreFlattenSchemas.DeriveRecursions(c.Nodes).Select(d => $"{d.Cycle} +{d.Step}")));
    }

    [Theory]
    [MemberData(nameof(RefSites))]
    public void EveryPreFlattenRefSiteSurvivesTheFlattenSpelledOut_NotAmputatedToAnOpenNode(
        string tool, string path)
    {
        var published = _s.PublishedTools[tool].GetProperty("inputSchema");
        var node = PreFlattenSchemas.Pointer(published, path);

        Assert.True(node is { ValueKind: JsonValueKind.Object } n && PreFlattenSchemas.SpellsOutStructure(n),
            $"{tool} {PreFlattenSchemas.Tail(path)}: " +
            (node is { } got ? PreFlattenSchemas.Trunc(got) : "<path does not resolve in the published schema>"));
    }

    /// <summary>The bound is spelled here independently of <c>ToolSchemas.MaxSelfExpansions</c> on purpose:
    /// reading the constant would make this agree with any bound, including the 0 that open-nodes the non-cyclic
    /// leaves too. Changing the bound is a published-contract change, and it has to fail here.</summary>
    [Theory]
    [MemberData(nameof(RecursiveSites))]
    public void EveryRecursiveSiteExpandsExactlyOneLevelBeforeClosing(string tool, string path)
    {
        var c = Carriers().Single(x => x.Tool == tool);
        var (levels, _, detail) = PreFlattenSchemas.WalkRecursion(
            _s.PublishedTools[tool].GetProperty("inputSchema"), path, c.Step!);

        Assert.Equal(1, levels);
        _out.WriteLine(detail);
    }

    [Theory]
    [MemberData(nameof(RecursiveSites))]
    public void EveryRecursiveSiteClosesOnAnOpenNodeSayingNestingContinues(string tool, string path)
    {
        var c = Carriers().Single(x => x.Tool == tool);
        var (_, closedOpen, detail) = PreFlattenSchemas.WalkRecursion(
            _s.PublishedTools[tool].GetProperty("inputSchema"), path, c.Step!);

        Assert.True(closedOpen, $"{tool} {PreFlattenSchemas.Tail(path)}: {detail}");
    }

    /// <summary>Without this, every derived test above passes vacuously the moment the pre-flatten read stops
    /// returning what it is supposed to.</summary>
    [Fact]
    public void TheDerivedSubjectSetIsNonEmpty()
    {
        var carriers = Carriers();
        _out.WriteLine(string.Join(" ", carriers.Select(c => $"{c.Tool}:{c.Sites}")));

        Assert.NotEmpty(carriers);
        Assert.True(carriers.Sum(c => c.Sites) > 0, "the pre-flatten surface yielded no $ref sites at all");
    }

    [Fact]
    public void AtLeastOneDerivedSubjectIsRecursive()
    {
        var recursive = RecursiveSites().Count();
        _out.WriteLine($"{recursive} recursive of {Carriers().Sum(c => c.Sites)} sites");

        Assert.True(recursive > 0, "no site re-enters its own pointer; nothing measured the bound");
    }

    // ---- the derivation's own fixtures -------------------------------------------------------------------
    //
    // Neither shape is producible by the DTOs as they stand, so the derivation rules they pin have nothing
    // end to end that would fail if the rules broke. They are pinned here instead.

    const string R = "#/properties/operations/items/properties";

    static (string, JsonObject) Site(string path, string pointer) =>
        (path, new JsonObject { ["$ref"] = pointer });

    /// <summary>Both pairs must come back; reporting only one would let a tool's coverage be claimed while a
    /// single cycle of it was checked.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ASurfaceCarryingTwoDistinctCyclesDerivesBothSoTheCallerCanRefuseIt()
    {
        var derived = PreFlattenSchemas.DeriveRecursions(new[]
        {
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/sets",
                 $"{R}/compose/properties/sets"),
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/alt/items",
                 $"{R}/compose/properties/sets/items"),
        });

        Assert.Equal(2, derived.Count);
    }

    /// <summary>A sibling whose wire name EXTENDS the ref target's is not an ancestor, and today's near-miss
    /// (<c>composes</c> against <c>compose</c>) is not one either. Exactly the real cycle survives.</summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ASiblingWhoseWireNameExtendsTheRefTargetIsNotReadAsAnAncestor()
    {
        var derived = PreFlattenSchemas.DeriveRecursions(new[]
        {
            Site($"{R}/compose/properties/sets/items/properties/compose/properties/sets",
                 $"{R}/compose/properties/sets"),
            Site($"{R}/compose/properties/setsx", $"{R}/compose/properties/sets"),
            Site($"{R}/composes/items/properties/sets", $"{R}/compose/properties/sets"),
        });

        Assert.Equal(new[] { "/items/properties/compose/properties/sets" },
                     derived.Select(d => d.Step).ToArray());
    }
}

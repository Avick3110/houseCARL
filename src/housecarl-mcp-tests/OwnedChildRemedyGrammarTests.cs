using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;
using Xunit.Abstractions;

namespace HousecarlMcpTests;

/// <summary>
/// #498 — the wrong-lever grammar grid over the TREE form, with its subject set derived from the record
/// types the records surface can render a declarers block for.
///
/// <para><b>The gap this closes.</b> <see cref="RemedyHarvest"/> reaches the tree form only through
/// <c>RecordsWorld</c>, whose whole record population is weapons, spells, an armour, magic effects, a form
/// list and two NPCs — not one child-bearing type among them. So every cut notice the declarers block emits
/// was outside the grid, and the grid stayed green across the gap. That is the hand-enumerated-population
/// failure at a guard: the subject set came from whatever a shared fixture happened to contain rather than
/// from the surface the guard claims to cover.</para>
///
/// <para><b>What is derived here.</b> The owner set is
/// <c>WriteEngine.ChildBearingProperties</c> over every concrete record type Mutagen models — the same
/// by-construction set <c>OwnedChildContent.Fields</c> splits by shape and <c>ReadSentences.FieldList</c>
/// consumes. The SUBJECTS are then found by asking the fixture's own load order for a record of each owner
/// type, so nothing here names a FormKey. And the completeness of that is itself asserted: a shape the
/// surface renders that no subject covers fails
/// <see cref="TheSubjectSetCoversEveryShapeTheSurfaceRendersADeclarersBlockFor"/> by name. The grid's own
/// theory cases still run, narrowed past the missing subject — the class goes red either way, but it is the
/// completeness arm that says so, not a refusal that stops the run.</para>
///
/// <para><c>RecordsWorld</c> stays frozen, and <see cref="RecordsRemedyGrammarTests"/>' arms are untouched.
/// The lever vocabulary, the remedy discriminant, the per-lane harvest and the lane list all come from
/// <see cref="RemedyHarvest"/>, so there is one home for each. The first cut of this grid spelled its own
/// two-lane list and so covered two of the four lanes the surface renders, which is the
/// hand-enumerated-population failure #498 was filed about arriving one level above the subjects.</para>
/// </summary>
[Trait("tier", "integration")]
public sealed class OwnedChildRemedyGrammarTests : IClassFixture<OwnedChildFixture>
{
    readonly OwnedChildWorld _w;
    readonly ITestOutputHelper _out;

    public OwnedChildRemedyGrammarTests(OwnedChildFixture f, ITestOutputHelper output)
    {
        _w = f.W;
        _out = output;
    }

    // ---- the surface's own child-bearing set ------------------------------------------------------------

    /// <summary>
    /// Every concrete record type Mutagen models. The twin of
    /// <c>WriteSurfaceGuardProbe.ConcreteRecordTypes</c>, which is internal to a project this one cannot see;
    /// the two converge when that probe converts. The rule is a Mutagen fact, not a houseCARL one: a class in
    /// Mutagen's own assembly, not abstract, not a binary overlay, that is an <c>IMajorRecord</c>.
    /// </summary>
    static IReadOnlyList<Type> ConcreteRecordTypes() =>
        typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                     && typeof(IMajorRecord).IsAssignableFrom(t))
            .ToList();

    /// <summary>The record types the records surface can render a declarers block for, each with the shapes
    /// its child-bearing fields take — derived, never listed.</summary>
    public static IReadOnlyDictionary<Type, IReadOnlyList<OwnedChildShape>> SurfaceOwners()
    {
        var owners = new Dictionary<Type, IReadOnlyList<OwnedChildShape>>();

        foreach (var t in ConcreteRecordTypes())
        {
            if (WriteEngine.ChildBearingProperties(t).Count == 0) continue;

            IMajorRecordGetter blank;
            try
            {
                // Every IMajorRecord is an IMajorRecordGetter, so the cast cannot yield null and a null check
                // here would be an arm that cannot fail. What CAN happen is that the type has no
                // (FormKey, SkyrimRelease) constructor, and that throws — which is the case this explains.
                blank = (IMajorRecordGetter)System.Activator.CreateInstance(t, FormKey.Null, SkyrimRelease.SkyrimSE)!;
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{t.Name} is child-bearing and this guard cannot make a blank one to read its shapes " +
                    "off. The shape split comes from OwnedChildContent.Fields, which takes a body; a type the " +
                    "guard cannot instantiate takes itself out of the population, which is the gap #498 is " +
                    $"about.\n{ex.GetType().Name}: {ex.Message}");
            }

            owners[t] = OwnedChildContent.Fields(blank).Values.Distinct().OrderBy(s => s).ToList();
        }

        Assert.True(owners.Count > 0,
            "No concrete record type reports a child-bearing property, so every claim below is vacuous. " +
            "WriteEngine.ChildBearingProperties answering nothing is this guard's subject, not a reason to pass.");

        return owners;
    }

    // ---- the subjects, found in the fixture rather than named ------------------------------------------

    Dictionary<Type, string>? _subjects;

    /// <summary>
    /// One subject per child-bearing owner type the fixture actually carries, FOUND rather than named: every
    /// plugin in the world is walked, and each record is mapped to its concrete type through the product's own
    /// getter mapping (<c>WriteEngine.PrimaryGetter</c> → <c>ConcreteOf</c>, the same two steps
    /// <c>OwnedChildContent.Fields</c> takes, and for the same reason — an overlay body is not the type it was
    /// written against, and asking the runtime type directly loses exactly the SINGULAR owned children).
    ///
    /// <para>Not a <c>types=</c> scan: that lane needs the generated corpus, which this world does not stand
    /// up. Walking the plugins needs nothing but the files the fixture already wrote.</para>
    /// </summary>
    IReadOnlyDictionary<Type, string> Subjects()
    {
        if (_subjects is not null) return _subjects;

        _subjects = new Dictionary<Type, string>();
        foreach (var path in Directory.EnumerateFiles(_w.Root, "*.*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase)
                              || p.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                var getter = WriteEngine.PrimaryGetter(rec.GetType());
                var concrete = getter is null ? null : WriteEngine.ConcreteOf(getter);
                if (concrete is null || _subjects.ContainsKey(concrete)) continue;
                if (WriteEngine.ChildBearingProperties(concrete).Count == 0) continue;
                _subjects[concrete] = $"{rec.FormKey.ID:X6}:{rec.FormKey.ModKey.FileName}";
            }
        }
        return _subjects;
    }

    string? SubjectOf(Type type) => Subjects().TryGetValue(type, out var fid) ? fid : null;

    /// <summary>
    /// THE COMPLETENESS REFUSAL. The grid below is only worth its runtime if its subjects cover the shapes
    /// the surface renders; a subject set that has drifted short of the surface must stop the run rather
    /// than shrink the grid in silence.
    /// </summary>
    [Fact]
    public void TheSubjectSetCoversEveryShapeTheSurfaceRendersADeclarersBlockFor()
    {
        var owners = SurfaceOwners();
        var missing = new List<string>();
        var shapesCovered = new HashSet<OwnedChildShape>();

        foreach (var (type, shapes) in owners.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            var subject = SubjectOf(type);
            if (subject is null)
            {
                missing.Add($"{type.Name} ({string.Join(", ", shapes)}) — no record of this type in the fixture");
                continue;
            }
            foreach (var s in shapes) shapesCovered.Add(s);
        }

        _out.WriteLine($"child-bearing owner types: {owners.Count} — " +
                       string.Join(", ", owners.Select(kv => $"{kv.Key.Name}[{string.Join("/", kv.Value)}]")
                                               .OrderBy(x => x, StringComparer.Ordinal)) +
                       $" · shapes covered: {string.Join(", ", shapesCovered.OrderBy(s => s))}");

        Assert.True(missing.Count == 0,
            "This guard's subject set does not cover the record types the records surface renders a declarers " +
            "block for:\n  " + string.Join("\n  ", missing) +
            "\nThe grid would run green over the gap, which is exactly what it did while the tree form was " +
            "driven from RecordsWorld. Either the fixture gains a record of each named type, or this guard " +
            "refuses — it does not narrow itself to what a fixture happens to hold.");

        var surfaceShapes = owners.Values.SelectMany(v => v).Distinct().ToArray();
        var uncovered = surfaceShapes.Except(shapesCovered).OrderBy(s => s).ToArray();

        Assert.True(uncovered.Length == 0,
            "The surface renders these owned-child SHAPES and no subject exercises them: " +
            string.Join(", ", uncovered) +
            ". A collection field and a singular field render different sentences, so covering one is not " +
            "covering the other.");
    }

    // ---- the grid ---------------------------------------------------------------------------------------

    /// <summary>The tree form, on every child-bearing owner type, in every lane the harvest knows — the
    /// population <see cref="RemedyHarvest"/> could not reach.</summary>
    public static TheoryData<string, string> LanesAndLevers()
    {
        var data = new TheoryData<string, string>();
        foreach (var lane in RemedyHarvest.Lanes)
            foreach (var (pattern, _) in RemedyHarvest.WrongLevers)
                data.Add(lane, pattern);
        return data;
    }

    [Theory, MemberData(nameof(LanesAndLevers))]
    public void NoTreeSentenceOnAChildBearingRecordNamesALeverTheRecordsCallerLacks(string lane, string pattern)
    {
        var claim = RemedyHarvest.WrongLevers.Single(w => w.Pattern == pattern).Claim;
        var offenders = Harvest(lane)
            .Where(s => System.Text.RegularExpressions.Regex.IsMatch(
                            s, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"The {lane} lane's tree-form sentences name a lever housecarl_records does not carry — {claim}:\n  " +
            string.Join("\n  ", offenders));
    }

    public static TheoryData<string> EveryLane()
    {
        var data = new TheoryData<string>();
        foreach (var lane in RemedyHarvest.Lanes) data.Add(lane);
        return data;
    }

    /// <summary>
    /// Per lane, the grid is measuring something: the lane either rendered sentences this harvest could read,
    /// or the product REFUSED the tree form on that lane by name. Both are real outcomes and the second is a
    /// fact about the surface — <c>format='dense'</c> renders positional columns against a fixed field set
    /// and the tree form's rows are variable-length, so the product refuses the pair outright. A lane that
    /// rendered nothing AND refused nothing is a lane this grid is running blind over.
    /// </summary>
    [Theory, MemberData(nameof(EveryLane))]
    public void EveryLaneEitherRendersSentencesThisGridReads_OrRefusesTheTreeFormByName(string lane)
    {
        var harvested = Harvest(lane).Count;
        var refusals = Responses(lane).Count(r => r.StartsWith("error:", StringComparison.Ordinal));
        var responses = Responses(lane).Count;

        _out.WriteLine($"{lane}: {responses} response(s) · {harvested} sentence(s) · {refusals} refusal(s)");

        Assert.True(responses > 0, $"The {lane} lane drove no call at all, so its grid rows say nothing.");

        Assert.True(harvested > 0 || refusals == responses,
            $"The {lane} lane rendered no sentence this grid can read and did not refuse the tree form " +
            $"either — {responses} response(s), {refusals} of them refusals. The grid's rows for this lane " +
            "are running over nothing, which is the vacuity the derived lane list was supposed to expose " +
            "rather than create.");
    }

    /// <summary>
    /// The cut notices are reached SOMEWHERE. Held across the lanes rather than per lane, because a lane can
    /// legitimately never cut: the artifact lane spills the complete result by definition, so its rows carry
    /// the declarers block and never a "raise max_chars" tail.
    /// </summary>
    [Fact]
    public void TheTreeFormsCutNoticesAreReachedOverAChildBearingRecord()
    {
        var byLane = RemedyHarvest.Lanes.ToDictionary(
            l => l, l => Harvest(l).Count(s => RemedyHarvest.RemedyLine.IsMatch(s)), StringComparer.Ordinal);

        _out.WriteLine("remedy sentences per lane: " +
                       string.Join(" · ", byLane.Select(kv => $"{kv.Key} {kv.Value}")));

        Assert.True(byLane.Values.Sum() > 0,
            "No lane produced a remedy sentence at all over the child-bearing subjects, so the wrong-lever " +
            "grid is vacuous everywhere. A cut tree renders a notice naming what to raise; none arriving means " +
            "the harvest is not reaching the cut, not that the notices are clean.");
    }

    // ---- driving ----------------------------------------------------------------------------------------

    readonly Dictionary<string, IReadOnlyList<string>> _harvest = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<string>> _responses = new(StringComparer.Ordinal);

    /// <summary>The raw responses one lane produced — what makes "this lane rendered nothing" separable from
    /// "this lane refused the form".</summary>
    IReadOnlyList<string> Responses(string lane)
    {
        Harvest(lane);
        return _responses[lane];
    }

    /// <summary>
    /// Every string one lane emits for the tree form over one subject per child-bearing owner type, at a cap
    /// low enough to make the declarers block and the node walk render their cut notices.
    /// </summary>
    IReadOnlyList<string> Harvest(string lane)
    {
        if (_harvest.TryGetValue(lane, out var got)) return got;

        var sentences = new List<string>();
        var responses = new List<string>();
        var tree = new RecordsTools.RecordsProject { form = "tree" };

        foreach (var type in SurfaceOwners().Keys.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var subject = SubjectOf(type);
            if (subject is null) continue;   // the completeness arm above is what fails on this

            foreach (var cap in new[] { 0, 900, 400 })
            {
                // The artifact lane is not a format= value: it is a to_file spill, harvested off the file the
                // call left behind. Everything else is a format the product's own vocabulary declares, with
                // text spelled as the absent default.
                var artifact = lane == RemedyHarvest.ArtifactLane
                    ? Path.Combine(_w.Root, $"owned-child-remedy-{type.Name}-{cap}.jsonl")
                    : null;
                var format = lane is "text" or RemedyHarvest.ArtifactLane ? null : lane;

                var r = RecordsTools.Records(_w.Svc, formids: new[] { subject }, project: tree,
                                             format: format, to_file: artifact, max_chars: cap);

                responses.Add(r);
                sentences.AddRange(RemedyHarvest.HarvestLane(lane, r, artifact));
            }
        }

        _responses[lane] = responses;
        return _harvest[lane] = sentences;
    }
}

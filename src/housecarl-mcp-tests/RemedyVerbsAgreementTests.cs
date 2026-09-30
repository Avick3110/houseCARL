using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The shape-indexed verb table (<see cref="WriteVerbs.On"/>) measured against the real gate over every collection
/// field in the corpus: every shape it can describe is populated (bar the one pinned dormant shape), the schema and
/// runtime routes agree on every field, every verb it names is accepted and every verb it omits is refused. Migrated
/// from the remedy-verbs-guard probe's population, routes and agreement arms.
/// </summary>
[Trait("tier", "unit")]
public sealed class RemedyVerbsAgreementTests
{
    static readonly Lazy<Corpus> LazyCorpus = new(() => CorpusRulebook.LoadCorpus(TestCorpus.Path));
    static Corpus Corp => LazyCorpus.Value;

    /// <summary>Every writable collection field declared directly on a record-kind type, so the request path is one hop.</summary>
    static IEnumerable<(string Record, FieldSchema Field)> CollectionFields() =>
        from t in Corp.Types.Values
        where t.Kind == "record"
        from f in t.Fields
        where f.Writable && f.Cardinality is "list" or "dict"
        select (t.Name, f);

    static string Show(CollectionShape? s) => s is { } v ? $"{v.Kind}/{v.Element}" : "(declined)";

    // SHAPE-POPULATION — exactly the pinned dormant shape (Dict/OwnedRecord) is uninstantiated; every other shape the
    // table describes has corpus fields behind it, so the agreement sweep is non-vacuous
    [Fact]
    public void OnlyThePinnedDormantShapeHasNoCorpusField()
    {
        var populated = CollectionFields()
            .Select(cf => WriteVerbs.OfField(cf.Field, Corp))
            .OfType<CollectionShape>()
            .Select(s => Show(s))
            .ToHashSet(StringComparer.Ordinal);
        var empty = (from kind in new[] { CollectionKind.List, CollectionKind.Dict }
                     from elem in new[] { ElementPlacement.Coerced, ElementPlacement.Composed, ElementPlacement.OwnedRecord }
                     let k = $"{kind}/{elem}"
                     where !populated.Contains(k)
                     select k).ToList();
        Assert.Equal(new[] { "Dict/OwnedRecord" }, empty);
    }

    // SHAPE-ROUTES — schema-derived and runtime-derived shapes agree on every collection field in the corpus
    [Fact]
    public void TheSchemaAndRuntimeRoutesAgreeOnEveryCollectionField()
    {
        int agreed = 0;
        var disagreements = new List<string>();
        foreach (var (rec, f) in CollectionFields())
        {
            if (WriteEngine.ResolveType(f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified) is not { } rt) continue;
            var bySchema = WriteVerbs.OfField(f, Corp);
            var byRuntime = WriteVerbs.OfRuntimeType(rt);
            if (Equals(bySchema, byRuntime)) agreed++;
            else disagreements.Add($"{rec}.{f.Name}: schema={Show(bySchema)} runtime={Show(byRuntime)}");
        }
        Assert.Empty(disagreements);
        Assert.True(agreed > 0);
    }

    // VERB-ACCEPTED — every verb the table names for a shape is ACCEPTED by the gate, on every corpus field of that
    // shape, with at least one measured accept per shape
    [Fact]
    public void EveryVerbTheTableNamesIsAccepted()
    {
        var fails = new List<string>();
        var accepts = new Dictionary<string, int>(StringComparer.Ordinal);
        var populated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rec, f) in CollectionFields())
        {
            if (WriteVerbs.OfField(f, Corp) is not { } shape) continue;
            var sh = Show(shape);
            populated.Add(sh);
            foreach (var use in WriteVerbs.On(shape))
            {
                if (BuildRequest(rec, f, use.Verb, use.Input, use.NeedsKey) is not { } req) continue;
                if (TestCorpus.Rulebook.Validate(req) is { } err) fails.Add($"{rec}.{f.Name} [{sh}] NAMED {use.Verb} -> {err}");
                else accepts[sh] = accepts.GetValueOrDefault(sh) + 1;
            }
        }
        Assert.Empty(fails);
        Assert.All(populated, sh => Assert.True(accepts.GetValueOrDefault(sh) > 0, $"no measured accept for {sh}"));
    }

    // VERB-REFUSED — every verb the table omits for a shape is REFUSED by the gate, on every corpus field of that
    // shape, with at least one measured refusal per shape
    [Fact]
    public void EveryVerbTheTableOmitsIsRefused()
    {
        var fails = new List<string>();
        var refuses = new Dictionary<string, int>(StringComparer.Ordinal);
        var populated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rec, f) in CollectionFields())
        {
            if (WriteVerbs.OfField(f, Corp) is not { } shape) continue;
            var sh = Show(shape);
            populated.Add(sh);
            var named = WriteVerbs.On(shape);
            foreach (var verb in WriteVerbs.All.Where(v => !named.Any(u => u.Verb == v)))
            {
                var (input, needsKey) = LikelyInput(shape, verb);
                if (BuildRequest(rec, f, verb, input, needsKey) is not { } req) continue;
                if (TestCorpus.Rulebook.Validate(req) is null) fails.Add($"{rec}.{f.Name} [{sh}] OMITTED {verb} -> ACCEPTED");
                else refuses[sh] = refuses.GetValueOrDefault(sh) + 1;
            }
        }
        Assert.Empty(fails);
        Assert.All(populated, sh => Assert.True(refuses.GetValueOrDefault(sh) > 0, $"no measured refusal for {sh}"));
    }

    /// <summary>The slot an omitted verb would take on this shape, so the refuse direction tests the verb rather than a
    /// malformed request.</summary>
    static (VerbInput Input, bool NeedsKey) LikelyInput(CollectionShape shape, string verb)
    {
        bool composed = shape.Element is ElementPlacement.Composed or ElementPlacement.OwnedRecord;
        var one = composed ? VerbInput.Compose : VerbInput.Value;
        return verb switch
        {
            "Set" => (one, shape.Kind == CollectionKind.Dict),
            "Add" => (one, shape.Kind == CollectionKind.Dict),
            "Remove" => (VerbInput.None, true),
            "SetAtIndex" or "InsertAtIndex" => (one, true),
            "ReplaceAll" => shape.Kind == CollectionKind.List
                ? (composed ? VerbInput.Composes : VerbInput.Values, false)
                : (VerbInput.Entries, false),
            "Merge" => (VerbInput.Entries, false),
            _ => (VerbInput.None, false),
        };
    }

    /// <summary>A well-formed request for <paramref name="verb"/> in slot <paramref name="input"/>, or null when the
    /// element type has no sample value (dropped, never faked).</summary>
    static WriteRequest? BuildRequest(string rec, FieldSchema f, string verb, VerbInput input, bool needsKey)
    {
        string? key = null;
        if (needsKey)
        {
            key = f.Cardinality == "list" ? "0" : SampleKey(f);
            if (key is null) return null;
        }
        string? value = null;
        string[]? values = null;
        Dictionary<string, string>? entries = null;
        StructSpec? spec = null;
        IReadOnlyList<StructSpec>? specs = null;
        switch (input)
        {
            case VerbInput.Value:
                if (SampleElement(f) is not { } v1) return null;
                value = v1; break;
            case VerbInput.Values:
                values = SampleElement(f) is { } v2 ? new[] { v2 } : Array.Empty<string>(); break;
            case VerbInput.Entries:
                entries = SampleKey(f) is { } ek && SampleElement(f) is { } ev
                    ? new Dictionary<string, string> { [ek] = ev }
                    : new Dictionary<string, string>();
                break;
            case VerbInput.Compose:
                if (SampleSpec(f) is not { } s1) return null;
                spec = s1; break;
            case VerbInput.Composes:
                if (SampleSpec(f) is not { } s2) return null;
                specs = new[] { s2 }; break;
        }
        return new WriteRequest
        {
            RecordType = rec, Path = new[] { f.Name }, Verb = verb,
            Key = key, Value = value, Values = values, Entries = entries, Struct = spec, Structs = specs,
        };
    }

    static string? SampleKey(FieldSchema f)
    {
        if (WriteEngine.ResolveType(f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified) is not { } rt) return null;
        if (WriteEngine.ClosedInterface(rt, typeof(IDictionary<,>)) is not { } di) return null;
        return SampleOf(di.GetGenericArguments()[0]);
    }

    static string? SampleElement(FieldSchema f)
    {
        if (f.FormLinkTarget is not null) return "Null";
        var aq = f.ElementTypeAssemblyQualified;
        return aq is null ? null : WriteEngine.ResolveType(aq) is { } rt ? SampleOf(rt) : null;
    }

    static string? SampleOf(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u.IsEnum) return Enum.GetNames(u).FirstOrDefault();
        if (u == typeof(string)) return "x";
        if (u == typeof(bool)) return "true";
        if (u == typeof(float) || u == typeof(double)) return "0";
        if (u == typeof(int) || u == typeof(uint) || u == typeof(short) || u == typeof(ushort)
            || u == typeof(long) || u == typeof(ulong) || u == typeof(byte) || u == typeof(sbyte)) return "0";
        if (typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter).IsAssignableFrom(u)) return "Null";
        if (u == typeof(Mutagen.Bethesda.Plugins.FormKey)) return "Null";
        if (u.IsGenericType && u.Name.StartsWith("AssetLink", StringComparison.Ordinal)) return "x";
        if (u.IsGenericType && u.Name.StartsWith("IAssetLink", StringComparison.Ordinal)) return "x";
        if (u.IsGenericType && u.Name.StartsWith("MemorySlice", StringComparison.Ordinal)) return "00";
        if (u.IsGenericType && u.Name.StartsWith("ReadOnlyMemorySlice", StringComparison.Ordinal)) return "00";
        return null;
    }

    /// <summary>A compose naming a concrete type the element accepts: its own modeled type, or a polymorphic base's
    /// first arm. Record-kind elements are included: that is what a caller who does not know the element is an owned
    /// child record sends.</summary>
    static StructSpec? SampleSpec(FieldSchema f)
    {
        if (f.ElementTypeRef is not { } er) return null;
        var t = Corp.Types.GetValueOrDefault(er);
        if (t is null) return null;
        if (t.Kind is "struct" or "arm" or "record") return new StructSpec { Type = er };
        if (t.Kind == "polymorphic-base")
        {
            var arm = (t.Arms ?? new()).FirstOrDefault(a => a != er
                && Corp.Types.GetValueOrDefault(a)?.Kind is "struct" or "arm" or "record");
            return arm is null ? null : new StructSpec { Type = arm };
        }
        return null;
    }
}

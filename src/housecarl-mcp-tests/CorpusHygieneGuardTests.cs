using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A freshly regenerated corpus, built once for the corpus-hygiene tests.</summary>
public sealed class CorpusHygieneCorpusFixture : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-corpus-hygiene-" + Guid.NewGuid().ToString("N"));
    internal Corpus Corpus { get; }

    public CorpusHygieneCorpusFixture()
    {
        var gen = Path.Combine(_root, "gen");
        CorpusGenerator.GenerateAll(gen, Path.Combine(_root, "ref"));
        Corpus = CorpusRulebook.LoadCorpus(Path.Combine(gen, "corpus.json"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
}

/// <summary>
/// Migrated from the corpus-hygiene-guard probe: the regenerated corpus carries no self-listing arm, no indexer
/// field, no non-Mutagen getter, no read-only projection arm, no zero-field struct, and catalogues every owned child
/// record as ownership. Each check has a red arm that feeds the same checker a violating corpus.
/// </summary>
[Trait("tier", "integration")]
public sealed class CorpusHygieneGuardTests : IClassFixture<CorpusHygieneCorpusFixture>
{
    readonly Corpus _corpus;
    public CorpusHygieneGuardTests(CorpusHygieneCorpusFixture f) => _corpus = f.Corpus;

    static void NoneFound(List<string> v) => Assert.True(v.Count == 0, string.Join("\n", v.Take(12)));

    // INV1-GREEN no self-listing arm (real corpus)
    [Fact]
    public void TheCorpusHasNoSelfListingArm() => NoneFound(CorpusHygieneChecks.SelfListingArm(_corpus));

    // INV1-RED a poly-base injected into its own arms is caught + named
    [Fact]
    public void ABaseListedAmongItsOwnArmsIsCaught()
    {
        var hit = CorpusHygieneChecks.SelfListingArm(CorpusHygieneChecks.NewCorpus(
            CorpusHygieneChecks.Poly("Faker", new() { "FakerArmA", "Faker" }), CorpusHygieneChecks.Arm("FakerArmA", "Faker")));
        Assert.Contains(hit, s => s.Contains("Faker") && s.Contains("itself"));
    }

    // INV1-RED2 a self-listing Arms list under a NON-poly-base Kind is caught + named
    [Fact]
    public void ASelfListingArmsListUnderAnyKindIsCaught()
    {
        var hit = CorpusHygieneChecks.SelfListingArm(CorpusHygieneChecks.NewCorpus(new TypeSchema
        {
            Name = "MistaggedBase", Kind = "struct", GetterInterface = "Mutagen.Bethesda.Skyrim.IMistaggedBaseGetter",
            Arms = new() { "MistaggedBase", "RealArm" },
        }));
        Assert.Contains(hit, s => s.Contains("MistaggedBase") && s.Contains("itself"));
    }

    // INV2-GREEN no emitted field is a C# indexer (real corpus)
    [Fact]
    public void TheCorpusHasNoIndexerField() => NoneFound(CorpusHygieneChecks.IndexerLeak(_corpus));

    // INV2-RED a leaked indexer field on a live indexer-bearing type is caught + named
    [Fact]
    public void ALeakedIndexerFieldIsCaught()
    {
        var bearer = CorpusHygieneChecks.FindIndexerBearingType(_corpus, out var idx);
        Assert.True(bearer != null, "no live indexer-bearing type in the corpus to prove against");
        var synth = new TypeSchema { Name = bearer!.Name, Kind = "struct", GetterInterface = bearer.Name,
            GetterInterfaceAssemblyQualified = bearer.GetterInterfaceAssemblyQualified };
        synth.Fields.Add(new FieldSchema { Name = idx!, Type = "float", Cardinality = "scalar", Writable = true });
        var hit = CorpusHygieneChecks.IndexerLeak(CorpusHygieneChecks.NewCorpus(synth));
        Assert.Contains(hit, s => s.Contains(bearer.Name) && s.Contains(idx!));
    }

    // INV3-GREEN every getter is under a modeled root Mutagen.Bethesda/Noggog (real corpus)
    [Fact]
    public void EveryGetterIsUnderAModeledRoot() => NoneFound(CorpusHygieneChecks.NonModeledType(_corpus));

    // INV3-RED a System.* (phantom-Enumerable) getter is caught + named
    [Fact]
    public void ASystemGetterIsCaught()
    {
        var hit = CorpusHygieneChecks.NonModeledType(CorpusHygieneChecks.NewCorpus(new TypeSchema
        {
            Name = "Enumerable", Kind = "struct", GetterInterface = "System.Linq.Enumerable",
            Fields = { new FieldSchema { Name = "X", Type = "int", Cardinality = "scalar", Writable = true } },
        }));
        Assert.Contains(hit, s => s.Contains("Enumerable") && s.Contains("System.Linq"));
    }

    // INV4-GREEN no read-only projection in the catalog (real corpus)
    [Fact]
    public void TheCorpusHasNoReadOnlyProjection() => NoneFound(CorpusHygieneChecks.ReadOnlyProjection(_corpus));

    // INV4-RED a read-only projection (concrete getter + no mutable) is caught + named
    [Fact]
    public void AConcreteGetterProjectionArmIsCaught()
    {
        var overlay = typeof(IArmorGetter).Assembly.GetType("Mutagen.Bethesda.Skyrim.SkyrimMultiModOverlay");
        Assert.NotNull(overlay);
        var hit = CorpusHygieneChecks.ReadOnlyProjection(CorpusHygieneChecks.NewCorpus(new TypeSchema
        {
            Name = "SkyrimMultiModOverlay", Kind = "arm", GetterInterface = overlay!.FullName!,
            GetterInterfaceAssemblyQualified = overlay.AssemblyQualifiedName!, MutableInterface = null, AbstractBase = "SkyrimMod",
        }));
        Assert.Contains(hit, s => s.Contains("SkyrimMultiModOverlay") && s.Contains("projection"));
    }

    // INV4-RED2 a no-mutable ARM with an interface getter is caught + named
    [Fact]
    public void ANoMutableArmWithAnInterfaceGetterIsCaught()
    {
        var hit = CorpusHygieneChecks.ReadOnlyProjection(CorpusHygieneChecks.NewCorpus(new TypeSchema
        {
            Name = "GhostArm", Kind = "arm", GetterInterface = typeof(IArmorGetter).FullName!,
            GetterInterfaceAssemblyQualified = typeof(IArmorGetter).AssemblyQualifiedName!, MutableInterface = null, AbstractBase = "Ghost",
        }));
        Assert.Contains(hit, s => s.Contains("GhostArm") && s.Contains("projection"));
    }

    // INV5-GREEN no field-less struct/record/header (real corpus)
    [Fact]
    public void TheCorpusHasNoFieldlessContentType() => NoneFound(CorpusHygieneChecks.DegenerateType(_corpus));

    // INV5-RED a field-less struct is caught + named
    [Fact]
    public void AFieldlessStructIsCaught()
    {
        var hit = CorpusHygieneChecks.DegenerateType(CorpusHygieneChecks.NewCorpus(new TypeSchema
            { Name = "Hollow", Kind = "struct", GetterInterface = "Mutagen.Bethesda.Skyrim.IHollowGetter" }));
        Assert.Contains(hit, s => s.Contains("Hollow") && s.Contains("zero fields"));
    }

    // INV6-GREEN every child-OWNING property is cataloged as ownership, not a link (real corpus)
    [Fact]
    public void EveryOwnedChildIsCataloguedAsOwnership() => NoneFound(CorpusHygieneChecks.OwnedChildAsLink(_corpus));

    // INV6-RED an owned child record cataloged as a formlink is caught + named
    [Fact]
    public void AnOwnedChildCataloguedAsAFormLinkIsCaught()
    {
        var hit = CorpusHygieneChecks.OwnedChildAsLink(CorpusHygieneChecks.NewCorpus(
            CorpusHygieneChecks.CellWith(new FieldSchema { Name = "Landscape", Type = "FormLink", Cardinality = "formlink", Writable = true }),
            CorpusHygieneChecks.RecordStub("NavigationMesh"), CorpusHygieneChecks.RecordStub("Placed")));
        Assert.Contains(hit, s => s.Contains("Cell.Landscape") && s.Contains("formlink"));
    }

    // INV6-RED2 an ownership cardinality whose ref reaches no record is caught + named
    [Fact]
    public void AnOwnershipRefThatReachesNoRecordIsCaught()
    {
        var hit = CorpusHygieneChecks.OwnedChildAsLink(CorpusHygieneChecks.NewCorpus(
            CorpusHygieneChecks.CellWith(new FieldSchema { Name = "Landscape", Type = "Landscape", Cardinality = "substruct", TypeRef = "Decal", Writable = true }),
            new TypeSchema { Name = "Decal", Kind = "struct", GetterInterface = "Mutagen.Bethesda.Skyrim.IDecalGetter",
                Fields = { new FieldSchema { Name = "MinWidth", Type = "float", Cardinality = "scalar", Writable = true } } },
            CorpusHygieneChecks.RecordStub("NavigationMesh"), CorpusHygieneChecks.RecordStub("Placed")));
        Assert.Contains(hit, s => s.Contains("Cell.Landscape") && s.Contains("reaches no record"));
    }

    // INV6-CLASSIFIER a major-record getter classifies as an owned child record (substruct → its record entry)
    [Fact]
    public void AMajorRecordGetterClassifiesAsAnOwnedChild()
    {
        var f = CorpusGenerator.ClassifyField(typeof(ILandscapeGetter), new List<CorpusGenerator.RefItem>(), "Cell", "Landscape");
        Assert.Equal("substruct", f.Cardinality);
        Assert.Equal("Landscape", f.TypeRef);
    }

    // INV6-CLASSIFIER …and a bare FormLink type still classifies as a formlink
    [Fact]
    public void ABareFormLinkStillClassifiesAsAFormLink()
    {
        var f = CorpusGenerator.ClassifyField(typeof(IFormLinkGetter), new List<CorpusGenerator.RefItem>(), "guard", "BareLink");
        Assert.Equal("formlink", f.Cardinality);
        Assert.Equal("FormLink", f.Type);
    }

    static FieldSchema PolyField() => new()
    {
        Name = "Anchor", Type = "IPlacedGetter", Cardinality = "polymorphic", TypeRef = "Placed",
        Arms = new List<string> { "PlacedObject", "PlacedNpc" }, Writable = true,
    };

    // INV6-SHAPE a SINGULAR polymorphic field whose arms are records reads as an owned child record
    [Fact]
    public void ASingularPolymorphicFieldWithRecordArmsIsAnOwnedChild()
    {
        var c = CorpusHygieneChecks.NewCorpus(
            new TypeSchema { Name = "Placed", Kind = "polymorphic-base", GetterInterface = "Mutagen.Bethesda.Skyrim.IPlacedGetter",
                Arms = new List<string> { "PlacedObject", "PlacedNpc" } },
            CorpusHygieneChecks.RecordStub("PlacedObject"), CorpusHygieneChecks.RecordStub("PlacedNpc"));
        Assert.True(SchemaClassifier.IsOwnedChildRecord(PolyField(), c));
    }

    // INV6-SHAPE …and a polymorphic field whose arms are STRUCTS does not (the carve-out is records, not polymorphism)
    [Fact]
    public void APolymorphicFieldWithStructArmsIsNotAnOwnedChild()
    {
        var c = CorpusHygieneChecks.NewCorpus(
            new TypeSchema { Name = "ScriptProperty", Kind = "polymorphic-base", GetterInterface = "Mutagen.Bethesda.Skyrim.IScriptPropertyGetter",
                Arms = new List<string> { "ScriptObjectProperty" } },
            new TypeSchema { Name = "ScriptObjectProperty", Kind = "arm", GetterInterface = "Mutagen.Bethesda.Skyrim.IScriptObjectPropertyGetter",
                Fields = { new FieldSchema { Name = "Object", Type = "FormLink<ISkyrimMajorRecordGetter>", Cardinality = "formlink", Writable = true } } });
        Assert.False(SchemaClassifier.IsOwnedChildRecord(new FieldSchema { Name = "P", Type = "IScriptPropertyGetter",
            Cardinality = "polymorphic", TypeRef = "ScriptProperty", Arms = new List<string> { "ScriptObjectProperty" }, Writable = true }, c));
    }

    // INV6-SHAPE …and the reach walk follows a polymorphic FIELD's arms to a record
    [Fact]
    public void TheReachWalkFollowsAPolymorphicFieldsArms()
    {
        var c = CorpusHygieneChecks.NewCorpus(
            new TypeSchema { Name = "Holder", Kind = "struct", GetterInterface = "Mutagen.Bethesda.Skyrim.IHolderGetter", Fields = { PolyField() } },
            new TypeSchema { Name = "Placed", Kind = "polymorphic-base", GetterInterface = "Mutagen.Bethesda.Skyrim.IPlacedGetter",
                Arms = new List<string> { "PlacedObject" } },
            CorpusHygieneChecks.RecordStub("PlacedObject"));
        Assert.True(CorpusHygieneChecks.OwnershipReachesRecord(c,
            new FieldSchema { Name = "Held", Type = "IHolderGetter", Cardinality = "substruct", TypeRef = "Holder", Writable = true }));
    }
}

/// <summary>The corpus-hygiene checkers, each returning the violations it found.</summary>
static class CorpusHygieneChecks
{
    static readonly string[] ModeledRoots = { "Mutagen.Bethesda", "Noggog" };
    static readonly string[] MustHaveFieldsKinds = { "struct", "record", "header" };

    internal static List<string> SelfListingArm(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Arms is { } a && a.Contains(t.Name))
                v.Add($"type '{t.Name}' (kind {t.Kind}) lists itself among its arms ({string.Join(",", a)})");
            foreach (var f in t.Fields)
            {
                if (f.Arms is { } fa && f.TypeRef is { } tr && fa.Contains(tr))
                    v.Add($"{t.Name}.{f.Name} field-arms list the base '{tr}' itself");
                if (f.ElementArms is { } ea && f.ElementTypeRef is { } er && ea.Contains(er))
                    v.Add($"{t.Name}.{f.Name} element-arms list the base '{er}' itself");
            }
        }
        return v;
    }

    // An indexer-only name surfacing as a field means the generator's indexer skip regressed.
    internal static List<string> IndexerLeak(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Kind == "enum") continue;
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            if (getter == null) continue;
            var (indexers, named) = LiveProps(getter);
            var fields = t.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var n in indexers.Where(n => !named.Contains(n)))
                if (fields.Contains(n))
                    v.Add($"{t.Name} emits field '{n}', a C# indexer on {getter.Name}");
        }
        return v;
    }

    internal static List<string> NonModeledType(Corpus c) =>
        c.Types.Values
            .Where(t => !ModeledRoots.Any(r => (t.GetterInterface ?? "").StartsWith(r + ".", StringComparison.Ordinal)))
            .Select(t => $"type '{t.Name}' getter '{t.GetterInterface}' is outside the modeled roots")
            .ToList();

    internal static List<string> ReadOnlyProjection(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Kind == "enum" || t.MutableInterface != null) continue;
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            bool concreteGetter = getter != null && !getter.IsInterface;
            if (t.Kind == "arm" || concreteGetter)
                v.Add($"type '{t.Name}' (kind {t.Kind}) is an unauthorable read-only projection");
        }
        return v;
    }

    internal static List<string> DegenerateType(Corpus c) =>
        c.Types.Values
            .Where(t => MustHaveFieldsKinds.Contains(t.Kind) && t.Fields.Count == 0)
            .Select(t => $"type '{t.Name}' (kind {t.Kind}) has zero fields")
            .ToList();

    // The owning side is the write engine's child-property sweep over every concrete record type.
    internal static List<string> OwnedChildAsLink(Corpus c)
    {
        var v = new List<string>();
        var records = typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                        && typeof(IMajorRecord).IsAssignableFrom(t));
        foreach (var t in records)
        {
            var owning = WriteEngine.ChildBearingProperties(t);
            if (owning.Count == 0 || !c.Types.TryGetValue(t.Name, out var schema)) continue;
            foreach (var p in owning)
            {
                var f = schema.Fields.FirstOrDefault(x => x.Name == p.Name);
                if (f is null) v.Add($"{t.Name}.{p.Name} owns child records but is absent from the corpus");
                else if (f.Cardinality is not ("substruct" or "polymorphic" or "list" or "dict"))
                    v.Add($"{t.Name}.{p.Name} owns child records but is cataloged '{f.Cardinality}'");
                else if (!OwnershipReachesRecord(c, f))
                    v.Add($"{t.Name}.{p.Name} owns child records but its cataloged shape reaches no record entry");
            }
        }
        return v;
    }

    internal static bool OwnershipReachesRecord(Corpus c, FieldSchema f, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var arm in f.ElementArms ?? f.Arms ?? new List<string>())
            if (EntryReachesRecord(c, arm, seen)) return true;
        return (f.ElementTypeRef ?? f.TypeRef) is { } tr && EntryReachesRecord(c, tr, seen);
    }

    static bool EntryReachesRecord(Corpus c, string name, HashSet<string> seen)
    {
        if (!seen.Add(name) || !c.Types.TryGetValue(name, out var t)) return false;
        if (t.Kind == "record") return true;
        foreach (var arm in t.Arms ?? new List<string>())
            if (EntryReachesRecord(c, arm, seen)) return true;
        foreach (var f in t.Fields)
            if (f.Cardinality is "substruct" or "polymorphic" or "list" or "dict" && OwnershipReachesRecord(c, f, seen)) return true;
        return false;
    }

    internal static TypeSchema? FindIndexerBearingType(Corpus c, out string? idxName)
    {
        foreach (var t in c.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (t.Kind == "enum") continue;
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            if (getter == null) continue;
            var (indexers, named) = LiveProps(getter);
            var leak = indexers.Where(n => !named.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (leak.Count > 0) { idxName = leak[0]; return t; }
        }
        idxName = null;
        return null;
    }

    static Type? ResolveType(string? aq)
    {
        if (string.IsNullOrEmpty(aq)) return null;
        try { return Type.GetType(aq); } catch { return null; }
    }

    // The getter and its interfaces, split into indexer names and named-property names.
    static (HashSet<string> indexers, HashSet<string> named) LiveProps(Type getter)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        var indexers = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(getter);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!seen.Add(cur)) continue;
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                (p.GetIndexParameters().Length != 0 ? indexers : named).Add(p.Name);
            foreach (var i in cur.GetInterfaces()) queue.Enqueue(i);
        }
        return (indexers, named);
    }

    internal static Corpus NewCorpus(params TypeSchema[] types)
    {
        var c = new Corpus();
        foreach (var t in types) c.Types[t.Name] = t;
        return c;
    }

    internal static TypeSchema Poly(string name, List<string> arms) =>
        new() { Name = name, Kind = "polymorphic-base", GetterInterface = $"Mutagen.Bethesda.Skyrim.I{name}Getter", Arms = arms };

    internal static TypeSchema Arm(string name, string @base) =>
        new() { Name = name, Kind = "arm", AbstractBase = @base, GetterInterface = $"Mutagen.Bethesda.Skyrim.I{name}Getter" };

    internal static TypeSchema RecordStub(string name) => new()
    {
        Name = name, Kind = "record", GetterInterface = $"Mutagen.Bethesda.Skyrim.I{name}Getter",
        Fields = { new FieldSchema { Name = "EditorID", Type = "string", Cardinality = "scalar", Writable = true } },
    };

    // A Cell with the field under test and its three sibling child-bearing properties cataloged correctly.
    internal static TypeSchema CellWith(FieldSchema landscape) => new()
    {
        Name = "Cell", Kind = "record",
        GetterInterface = typeof(ICellGetter).FullName!,
        GetterInterfaceAssemblyQualified = typeof(ICellGetter).AssemblyQualifiedName!,
        MutableInterface = typeof(ICell).FullName!,
        Fields =
        {
            landscape,
            new FieldSchema { Name = "NavigationMeshes", Type = "List<INavigationMeshGetter>", Cardinality = "list",
                ElementType = "INavigationMeshGetter", ElementTypeRef = "NavigationMesh", Writable = true },
            new FieldSchema { Name = "Persistent", Type = "List<IPlacedGetter>", Cardinality = "list",
                ElementType = "IPlacedGetter", ElementTypeRef = "Placed", Writable = true },
            new FieldSchema { Name = "Temporary", Type = "List<IPlacedGetter>", Cardinality = "list",
                ElementType = "IPlacedGetter", ElementTypeRef = "Placed", Writable = true },
        },
    };
}

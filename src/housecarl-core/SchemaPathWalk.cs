using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The where= path walk over the generated schema, carrying the set of types a step can be on; it decides before any read whether a path names a field.</summary>
public sealed class SchemaPathWalk
{
    readonly CorpusRulebook _book;
    readonly Dictionary<string, TypeSchema> _byGetter = new(StringComparer.Ordinal);
    readonly Dictionary<TypeSchema, Type> _classOf = new();
    readonly ConcurrentDictionary<Type, TypeSchema?> _recordOf = new();
    readonly ConcurrentDictionary<string, IReadOnlyList<TypeSchema>> _parents = new();
    readonly ConcurrentDictionary<string, IReadOnlyList<TypeSchema>> _targets = new();

    /// <summary>Every record type the schema models.</summary>
    public IReadOnlyList<TypeSchema> Records { get; }

    public SchemaPathWalk(CorpusRulebook book, IEnumerable<TypeSchema> types)
    {
        _book = book;
        Records = types.Where(t => t.Kind == "record").ToList();
        foreach (var r in Records) _byGetter[r.GetterInterface] = r;
        foreach (var c in typeof(Weapon).Assembly.GetTypes())
            if (c is { IsClass: true, IsAbstract: false } && !c.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                && typeof(IMajorRecord).IsAssignableFrom(c) && RecordOf(c) is { } ts)
                _classOf[ts] = c;
    }

    /// <summary>The record type a body of this runtime class is, or null when the schema does not model it.</summary>
    public TypeSchema? RecordOf(Type runtime) => _recordOf.GetOrAdd(runtime, t =>
        WriteEngine.PrimaryGetter(t)?.FullName is { } g && _byGetter.TryGetValue(g, out var ts) ? ts : null);

    /// <summary>Where one step of a path can land: a schema field (its element when <see cref="Element"/>), a CLR type below a value field, or neither (can't tell).</summary>
    public readonly record struct Leaf(FieldSchema? Field, bool Element, Type? Clr, TypeSchema? Owner)
    {
        public bool CantTell => Field is null && Clr is null;
        public bool Container => !Element && Field is { Cardinality: "list" or "dict" };
    }

    /// <summary>The leaves a path can end on, or the one clause saying why it names no field on any type it can be on.</summary>
    public sealed record Outcome(IReadOnlyList<Leaf> Leaves, string? Failure);

    readonly record struct Pos(TypeSchema? In, Leaf At);

    /// <summary>Walk <paramref name="segs"/> from <paramref name="roots"/> after <paramref name="hops"/> containment steps; <paramref name="where"/> names the roots for the sentence.</summary>
    public Outcome Walk(IReadOnlyList<TypeSchema> roots, int hops, IReadOnlyList<string> segs, IReadOnlyList<PathFold>? folds, string where)
    {
        for (int h = 0; h < hops; h++)
        {
            var up = roots.SelectMany(Parents).Distinct().ToList();
            if (up.Count == 0)
                return new(Array.Empty<Leaf>(), $"no record contains a {Names(roots)}, so '{ContainmentIndex.ParentToken}' has nowhere to go; " +
                                                $"containment runs from these properties only: {ContainmentIndex.ChildBearingSurface()}");
            roots = up;
            where = "on the containing record";
        }
        var at = roots.Select(r => new Pos(r, default)).ToList();
        for (int i = 0; i < segs.Count; i++)
        {
            string name; string? key;
            try { (name, key) = WriteEngine.ParseSegment(segs[i]); }
            catch (Exception ex) { return new(Array.Empty<Leaf>(), ex.Message); }
            var fold = folds is not null && i < folds.Count ? folds[i] : PathFold.None;
            var next = new List<Pos>();
            var fails = new List<(int Rank, string Clause)>();
            var lacking = new List<TypeSchema>();
            foreach (var pos in at) Step(pos, name, key, fold, segs.Skip(i + 1).ToArray(), next, fails, lacking);
            if (next.Count > 0) { at = next.Distinct().ToList(); continue; }
            if (lacking.Count > 0) fails.Add((0, NoFieldClause(name, lacking.Distinct().ToList(), i == 0 ? where : $"on {Names(lacking)}")));
            return new(Array.Empty<Leaf>(), fails.OrderByDescending(f => f.Rank).First().Clause);
        }
        return new(at.Select(p => p.At).ToList(), null);
    }

    /// <summary>One step from one position; what it reaches goes to <paramref name="next"/>, why it cannot to <paramref name="fails"/>.</summary>
    void Step(Pos pos, string name, string? key, PathFold fold, string[] rest, List<Pos> next, List<(int, string)> fails, List<TypeSchema> lacking)
    {
        if (pos.In is null)
        {
            var leaf = pos.At;
            if (leaf.CantTell) { next.Add(pos); return; }
            if (leaf.Field is not { } f) { ClrStep(leaf.Clr!, name, key, fold, next, fails); return; }
            if (leaf.Container) { fails.Add((2, ListHopClause(f, name, rest))); return; }
            var inner = Inner(leaf);
            if (inner.In is not null || inner.At.CantTell || inner.At.Clr is not null) { Step(inner, name, key, fold, rest, next, fails, lacking); return; }
            fails.Add((1, f.Cardinality == "formlink" || f.FormLinkTarget is not null
                ? $"'{f.Name}' is a link, so it has no field '{name}' of its own — read the linked record with '{f.Name}->{name}'"
                : $"'{f.Name}' is a {(leaf.Element ? "plain element" : f.Cardinality)} value, so it has no field '{name}'"));
            return;
        }
        var hits = FieldsNamed(pos.In, name);
        if (hits.Count == 0) { lacking.Add(pos.In); return; }
        foreach (var (f, owner) in hits)
        {
            bool list = f.Cardinality is "list" or "dict";
            if (fold != PathFold.None)
            {
                if (f.Cardinality == "list") next.Add(new Pos(null, new Leaf(f, fold != PathFold.Count, null, owner)));
                else fails.Add((3, $"'{name}{PathFoldGrammar.Token(fold)}' quantifies a step that is not a list — it is {What(f)} on {owner.Name}. " +
                                   "Drop the quantifier, or point it at a list-valued field"));
            }
            else if (key is null) next.Add(new Pos(null, new Leaf(f, false, null, owner)));
            else if (f.Cardinality == "list" && WriteEngine.ListKeyShapeError(name, key, out _) is { } bad)
                fails.Add((4, bad + FieldPredicateSet.StarHint(name, key)));
            else if (list) next.Add(new Pos(null, new Leaf(f, true, null, owner)));
            else if (f.TypeRef is { } tr && tr.StartsWith("GenderedItem<", StringComparison.Ordinal) && _book.Type(tr) is { } g)
            {
                if (key is "0" or "1" && g.Fields.FirstOrDefault(x => x.Name == WriteEngine.GenderedArmNames[key[0] - '0']) is { } arm)
                    next.Add(new Pos(null, new Leaf(arm, false, null, g)));
                else fails.Add((4, $"'{name}' is gendered, indexed by [0] (male) or [1] (female); got '{key}'"));
            }
            else fails.Add((3, $"'{name}[{key}]' brackets a step that is not a list — it is {What(f)} on {owner.Name}"));
        }
    }

    /// <summary>What a field is, for a sentence about a step that wanted a list.</summary>
    static string What(FieldSchema f) =>
        f.Cardinality == "value" && (f.Type.Contains("Slice", StringComparison.Ordinal) || f.Type.Contains("Byte[]", StringComparison.Ordinal)) ? "a raw block of bytes"
        : f.Cardinality == "dict" ? "a dict of keyed entries" : $"a single {RecordNaming.StripGetterInterface(f.TypeRef ?? f.Type)} value, a {f.Cardinality}";

    /// <summary>A step below a CLR value type, judged by reflection; an untyped or raw value is can't-tell.</summary>
    static void ClrStep(Type t, string name, string? key, PathFold fold, List<Pos> next, List<(int, string)> fails)
    {
        if (key is not null || fold != PathFold.None) { next.Add(new Pos(null, default)); return; }
        if (WriteEngine.ResolveProperty(t, name) is { } p) next.Add(new Pos(null, ClrLeaf(p.PropertyType)));
        else fails.Add((1, $"'{name}' is not a member of {RecordNaming.StripGetterInterface(t.Name)}"));
    }

    static Leaf ClrLeaf(Type? t)
    {
        if (t is null) return default;
        var u = Nullable.GetUnderlyingType(t) ?? t;
        return u == typeof(object) || u.Name.Contains("MemorySlice", StringComparison.Ordinal) || u == typeof(byte[]) ? default : new Leaf(null, false, u, null);
    }

    /// <summary>What a field leaf holds, as the position the next dotted step starts from.</summary>
    Pos Inner(Leaf leaf)
    {
        var f = leaf.Field!;
        if (leaf.Element)
            return f.ElementTypeRef is { } er && _book.Type(er) is { } et ? new Pos(et, default)
                 : f.FormLinkTarget is not null ? new Pos(null, leaf)
                 : new Pos(null, ClrLeaf(f.ElementTypeAssemblyQualified is { } ea ? WriteEngine.ResolveType(ea) : null));
        return f.Cardinality switch
        {
            "substruct" or "polymorphic" when f.TypeRef is { } tr && _book.Type(tr) is { } t => new Pos(t, default),
            "value" => new Pos(null, ClrLeaf(WriteEngine.ResolveType(f.GetterTypeAssemblyQualified))),
            _ => new Pos(null, leaf),
        };
    }

    /// <summary>The fields named <paramref name="name"/> on a type, or on any arm of a polymorphic base.</summary>
    List<(FieldSchema Field, TypeSchema Owner)> FieldsNamed(TypeSchema t, string name)
    {
        var hits = new List<(FieldSchema, TypeSchema)>();
        foreach (var owner in WithArms(t))
            if (owner.Fields.FirstOrDefault(f => f.Name == name) is { } f) hits.Add((f, owner));
        return hits;
    }

    IEnumerable<TypeSchema> WithArms(TypeSchema t)
    {
        yield return t;
        if (t is { Kind: "polymorphic-base", Arms: { } arms })
            foreach (var a in arms) if (a != t.Name && _book.Type(a) is { } at) yield return at;
    }

    /// <summary>The record types a link side's leaves can point at: the declared target where there is one, else every record.</summary>
    public IReadOnlyList<TypeSchema> LinkTargets(IReadOnlyList<Leaf> leaves)
    {
        var all = new HashSet<TypeSchema>();
        foreach (var l in leaves)
        {
            if (l.Field?.FormLinkTargetAssemblyQualified is not { } aq) return Records;
            foreach (var r in _targets.GetOrAdd(aq, a => WriteEngine.ResolveType(a) is { } target
                         ? Records.Where(r => WriteEngine.ResolveType(r.GetterInterfaceAssemblyQualified) is { } g && target.IsAssignableFrom(g)).ToList()
                         : Records))
                all.Add(r);
        }
        return all.ToList();
    }

    /// <summary>Whether a link side's leaf holds no link at all (a scalar, an enum, a plain element).</summary>
    public static bool BearsNoLink(Leaf l) =>
        l.Field is { } f && f.FormLinkTarget is null
        && (l.Element ? f.ElementTypeRef is null : f.Cardinality is "scalar" or "enum" or "value");

    /// <summary>The record types that can contain a record of type <paramref name="child"/>, from the child-bearing properties.</summary>
    IReadOnlyList<TypeSchema> Parents(TypeSchema child) => _parents.GetOrAdd(child.Name, _ =>
    {
        if (!_classOf.TryGetValue(child, out var cc)) return Array.Empty<TypeSchema>();
        return _classOf.Where(kv => WriteEngine.ChildBearingProperties(kv.Value)
                           .Any(p => WriteEngine.OwnedRecordTypeOf(p.PropertyType) is { } o && o.IsAssignableFrom(cc)))
                       .Select(kv => kv.Key).ToList();
    });

    /// <summary>The no-field clause, with the case slip, the nearest name, or the types that do model it.</summary>
    string NoFieldClause(string name, IReadOnlyList<TypeSchema> owners, string where)
    {
        var fields = owners.SelectMany(WithArms).SelectMany(t => t.Fields.Select(f => f.Name)).Distinct().ToList();
        string hint;
        var slips = owners.Where(o => WithArms(o).Any(t => t.Fields.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))).ToList();
        var exact = fields.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            hint = owners.Count == 1 ? $"; field names are case-sensitive — did you mean '{exact}'?"
                 : $"; field names are case-sensitive: '{exact}' is a field of {slips.Count} of them ({Names(slips)})";
        else if (PluginNameSuggest.Nearest(name, fields, 1) is { Count: > 0 } near) hint = $"; did you mean '{near[0]}'?";
        else if (ModeledFieldIndex.Diagnose(owners[0].Name, name) is { ModeledOn.Count: > 0 } v)
            hint = $"; Mutagen models '{name}' on {v.ModeledOn.Count:N0} other type(s) ({string.Join(", ", v.ModeledOn.Take(3))}{(v.ModeledOn.Count > 3 ? ", …" : "")}), not on these";
        else hint = "; check the field name against the record's schema (the mutagen-reference skill)";
        return $"'{name}' is not a field {where}{hint}";
    }

    /// <summary>A dotted step through a list or dict, with the bracketed spelling it most likely meant.</summary>
    string ListHopClause(FieldSchema list, string seg, string[] rest)
    {
        var tail = rest.Length > 0 ? "." + string.Join(".", rest) : "";
        var head = $"the path steps THROUGH '{list.Name}', which is a {list.Cardinality}, with a dotted segment, which dead-ends";
        if (seg.All(char.IsDigit)) return $"{head}; index an element with brackets, e.g. '{list.Name}[{seg}]{tail}', not '{list.Name}.{seg}{tail}'";
        var elem = list.ElementTypeRef is { } er ? _book.Type(er) : null;
        if (elem is null || FieldsNamed(elem, seg).Count > 0)
            return $"{head}; index an element with brackets: '{list.Name}[0].{seg}{tail}', not '{list.Name}.{seg}{tail}'; to ask about EVERY element, " +
                   $"quantify the step ('{list.Name}[*any].{seg}{tail}'). For list->FormID membership use references=";
        var near = PluginNameSuggest.Nearest(seg, WithArms(elem).SelectMany(t => t.Fields.Select(f => f.Name)).Distinct(), 1).FirstOrDefault();
        return near is not null
            ? $"{head}; '{seg}' is not a field on its element type {elem.Name} — did you mean '{list.Name}[0].{near}{tail}'?"
            : $"{head}; '{seg}' is not a field on its element type {elem.Name}; index an element with brackets ('{list.Name}[0].<field>') and name a field {elem.Name} has";
    }

    static string Names(IReadOnlyCollection<TypeSchema> types) =>
        string.Join(", ", types.Select(t => t.Name).Take(3)) + (types.Count > 3 ? $", and {types.Count - 3} more" : "");
}

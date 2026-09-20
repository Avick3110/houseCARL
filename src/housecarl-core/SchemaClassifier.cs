namespace HousecarlCore;

/// <summary>How a collection's ELEMENT is written — derived by construction from the corpus plus the engine's coercion recogniser, never hand-listed per record.</summary>
public enum ElementKind
{
    /// <summary>No modeled element ref; the element coerces from a scalar value (the writable-today element case).</summary>
    ScalarCoercible,
    /// <summary>No modeled element ref, but the engine cannot coerce the element's value type (a coercion-deferred element).</summary>
    ScalarUncoercible,
    /// <summary>A build-from-parts modeled struct element (Add takes a StructSpec).</summary>
    Struct,
    /// <summary>An owned child-record element — resolved via the record axis, not composed.</summary>
    Record,
    /// <summary>A polymorphic-union element (condition data, script properties, …).</summary>
    Arm,
    /// <summary>A whole-coercible element set as one value (an AssetLink path), not built from parts.</summary>
    WholeCoercible,
    /// <summary>A modeled element of some other kind (enum/value catalog entry) — surfaced, never silently bucketed.</summary>
    Unknown,
}

/// <summary>The ONE schema-side classifier for element and leaf write-kind and coercibility; it deliberately does not cover the engine's runtime write-time branch, which is schema-blind by design.</summary>
public static class SchemaClassifier
{
    /// <summary>A scalar/enum/value/formlink/substruct-whole leaf is settable-today iff the engine can coerce its WHOLE type; enums always coerce.</summary>
    public static bool CoercibleLeaf(FieldSchema f)
    {
        if (f.Cardinality == "enum") return true; // enums always coerce (Enum.Parse)
        var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
        return aq is not null && WriteEngine.ResolveType(aq) is { } rt && WriteEngine.CanCoerce(rt);
    }

    /// <summary>A list/dict is settable-today iff its ELEMENT coerces from a scalar value; a modeled element needs composition or record resolution instead.</summary>
    public static bool CoercibleElement(FieldSchema f)
    {
        if (f.ElementTypeRef is not null) return false; // element is a modeled type → needs composition/resolution
        var aq = f.ElementTypeAssemblyQualified;
        return aq is not null && WriteEngine.ResolveType(aq) is { } rt && WriteEngine.CanCoerce(rt);
    }

    /// <summary>Classify how a list/dict field's ELEMENT is written — the single brain the boolean conveniences and the coverage waves derive from.</summary>
    public static ElementKind ClassifyElement(FieldSchema f, Corpus corpus)
    {
        // No modeled element ref → a scalar/value element: coercible-today, or coercion-deferred.
        if (f.ElementTypeRef is not { } er)
            return CoercibleElement(f) ? ElementKind.ScalarCoercible : ElementKind.ScalarUncoercible;
        // A whole-coercible element (an AssetLink path) is set as one value, recognised by the engine's shared predicate.
        if (WriteEngine.IsWholeCoercibleElement(er, f.ElementTypeAssemblyQualified))
            return ElementKind.WholeCoercible;
        return corpus.Types.GetValueOrDefault(er)?.Kind switch
        {
            "struct" => ElementKind.Struct,
            "record" => ElementKind.Record,
            "arm" => ElementKind.Arm,
            "polymorphic-base" => PolyBaseElementKind(er, corpus),
            _ => ElementKind.Unknown,
        };
    }

    /// <summary>A polymorphic-base element family is ARM only when its arms are modeled STRUCTS; record arms live on the record axis, and a mixed or unresolvable arm set is Unknown.</summary>
    static ElementKind PolyBaseElementKind(string baseName, Corpus corpus)
    {
        var b = corpus.Types.GetValueOrDefault(baseName);
        var armKinds = (b?.Arms ?? new())
            .Where(a => a != baseName)
            .Select(a => corpus.Types.GetValueOrDefault(a)?.Kind)
            .Distinct().ToList();
        if (armKinds.Count > 0 && armKinds.All(k => k is "arm" or "struct" or "polymorphic-base")) return ElementKind.Arm;
        if (armKinds.Count > 0 && armKinds.All(k => k == "record")) return ElementKind.Record;
        return ElementKind.Unknown;
    }

    /// <summary>True iff the field is a collection whose ELEMENT is a build-from-parts modeled struct; defined via <see cref="ClassifyElement"/> so it cannot drift from it.</summary>
    public static bool IsStructElement(FieldSchema f, Corpus corpus) =>
        ClassifyElement(f, corpus) == ElementKind.Struct;

    /// <summary>True iff a SINGULAR leaf holds an OWNED CHILD RECORD rather than a link to one; keyed on the TypeRef's Kind across both singular ownership cardinalities.</summary>
    public static bool IsOwnedChildRecord(FieldSchema f, Corpus corpus)
    {
        if (f.Cardinality is not ("substruct" or "polymorphic") || f.TypeRef is not { } tr) return false;
        var kind = corpus.Types.GetValueOrDefault(tr)?.Kind;
        return kind == "record"
            || (kind == "polymorphic-base" && PolyBaseElementKind(tr, corpus) == ElementKind.Record);
    }

    /// <summary>True iff a scalar SUBSTRUCT leaf can be Set by composing its whole value FROM PARTS — the leaf twin of <see cref="IsStructElement"/>, keyed on the leaf's own TypeRef.</summary>
    public static bool IsComposableSubstructLeaf(FieldSchema f, Corpus corpus)
    {
        if (f.Cardinality != "substruct" || f.TypeRef is not { } tr) return false;
        if (corpus.Types.GetValueOrDefault(tr)?.Kind is not ("struct" or "arm")) return false;
        if (CoercibleLeaf(f)) return false;                       // coercible substruct → keeps its plain-value Set
        return WriteEngine.IsPlainComposableStruct(tr);           // excludes GenderedItem/Array2d (no paramless ctor)
    }
}

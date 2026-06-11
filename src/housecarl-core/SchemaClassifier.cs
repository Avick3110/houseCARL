namespace HousecarlCore;

/// <summary>
/// How a collection's ELEMENT is written — the one classification the schema-side instruments
/// (rulebook pre-flight, write-census reachability, write-proof denominator) must agree on. Derived
/// BY CONSTRUCTION from the corpus + the engine's coercion recogniser; never hand-listed per record.
/// Wave 4 (arm-breadth) switches on <see cref="Arm"/>; wave 3 (nested-group) on <see cref="Record"/>.
/// </summary>
public enum ElementKind
{
    /// <summary>No modeled element ref; the element coerces from a scalar value (the writable-today element case).</summary>
    ScalarCoercible,
    /// <summary>No modeled element ref, but the engine cannot coerce the element's value type (a coercion-deferred element).</summary>
    ScalarUncoercible,
    /// <summary>A build-from-parts modeled struct element (Add takes a StructSpec) — wave-1 half B.</summary>
    Struct,
    /// <summary>An owned child-record element — resolved via the record axis (nested-group wave 3), not composed.</summary>
    Record,
    /// <summary>A polymorphic-union element (condition data, script properties, …) — arm-breadth wave 4.</summary>
    Arm,
    /// <summary>A whole-coercible element set as one value (an AssetLink path), not built from parts.</summary>
    WholeCoercible,
    /// <summary>A modeled element of some other kind (enum/value catalog entry) — surfaced, never silently bucketed.</summary>
    Unknown,
}

/// <summary>
/// The ONE schema-side classifier for element/leaf write-kind + coercibility. Before this existed the same
/// predicates were forked across <c>CorpusRulebook</c>, <c>WriteProof</c>, and <c>WriteCensus</c> — the harness
/// deriving against the deserialized corpus while the engine derived against live reflection — so a future
/// editor had to remember to update 2–3 look-alike sites or the census and the proof would silently disagree
/// about the denominator (baseline review S2). Now there is one definition; every schema-side instrument calls it.
///
/// It deliberately does NOT cover the engine's <i>runtime</i> write-time branch (<c>ApplyListVerb</c> decides by
/// <c>req.Struct is not null</c>, with no FieldSchema in scope): the engine is schema-blind by design (review
/// bullet ①), and threading the corpus into the verb path would break that property. The fork unified here is the
/// schema-side derivation only.
///
/// Coercibility delegates to the engine's own recogniser (<see cref="WriteEngine.CanCoerce"/> /
/// <see cref="WriteEngine.ResolveType"/> / <see cref="WriteEngine.IsWholeCoercibleElement"/>) so "is this
/// coercible" can never drift between recognise-side and execute-side (review bullet ②).
/// </summary>
public static class SchemaClassifier
{
    /// <summary>A scalar/enum/value/formlink/substruct-whole leaf is settable-today iff the engine can coerce its
    /// WHOLE type. Enums always coerce (Enum.Parse). FormLinkOrIndex, owned-record links, and type-erased
    /// <c>object</c> are correctly excluded (the coerce-audit deferred surface), unifying this proof's denominator
    /// with the census's writable-today set.</summary>
    public static bool CoercibleLeaf(FieldSchema f)
    {
        if (f.Cardinality == "enum") return true; // enums always coerce (Enum.Parse)
        var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
        return aq is not null && WriteEngine.ResolveType(aq) is { } rt && WriteEngine.CanCoerce(rt);
    }

    /// <summary>A list/dict is settable-today iff its ELEMENT coerces from a scalar value (scalar/enum/formlink
    /// element with NO modeled element ref). A modeled-struct/record/arm element (ElementTypeRef set) needs
    /// composition or record resolution — deferred. Corpus-free by construction (the AQ + the ref are enough).</summary>
    public static bool CoercibleElement(FieldSchema f)
    {
        if (f.ElementTypeRef is not null) return false; // element is a modeled type → needs composition/resolution
        var aq = f.ElementTypeAssemblyQualified;
        return aq is not null && WriteEngine.ResolveType(aq) is { } rt && WriteEngine.CanCoerce(rt);
    }

    /// <summary>Classify how a list/dict field's ELEMENT is written. The single brain the boolean conveniences and
    /// the later coverage waves all derive from, so the partition can never be defined two ways.</summary>
    public static ElementKind ClassifyElement(FieldSchema f, Corpus corpus)
    {
        // No modeled element ref → a scalar/value element: coercible-today, or coercion-deferred.
        if (f.ElementTypeRef is not { } er)
            return CoercibleElement(f) ? ElementKind.ScalarCoercible : ElementKind.ScalarUncoercible;
        // Whole-coercible element (an AssetLink path) — set as one value, NOT built from parts. Recognised by the
        // engine's shared predicate so the AssetLink carve-out lives in exactly one place.
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

    /// <summary>A polymorphic-base element family is ARM (composable by concrete arm type) only when its arms are
    /// modeled STRUCTS — the VMAD shape (ScriptProperty → ScriptObjectProperty…). A base whose arms are RECORDS
    /// (GameSetting → GameSettingBool/Float/Int/String, the typed record-group families) lives on the FormKey /
    /// record axis: its elements are allocated as records, never built from a StructSpec — classify
    /// <see cref="ElementKind.Record"/> so the composition surface can never admit them (PR review: pre-flight
    /// accepting a record-family compose was an accept-then-throw). A mixed or unresolvable arm set surfaces as
    /// <see cref="ElementKind.Unknown"/> — never silently bucketed either way.</summary>
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

    /// <summary>True iff the field is a collection whose ELEMENT is a BUILD-FROM-PARTS modeled struct (so Add takes a
    /// StructSpec). Excludes record-elements (nested-group wave 3), arm-elements (arm wave 4), and whole-coercible
    /// AssetLink-path elements (set as one value). Defined via <see cref="ClassifyElement"/> so it cannot drift from it.</summary>
    public static bool IsStructElement(FieldSchema f, Corpus corpus) =>
        ClassifyElement(f, corpus) == ElementKind.Struct;
}

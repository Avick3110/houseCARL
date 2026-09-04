using System.Text.Json.Serialization;

namespace HousecarlCore;

/// <summary>
/// One entry per Mutagen-modeled type in the corpus. The catalog is <b>flat</b>: a
/// field that targets another modeled type references it by name
/// (<see cref="FieldSchema.TypeRef"/> / <see cref="FieldSchema.ElementTypeRef"/> /
/// <see cref="FieldSchema.Arms"/>), never inlined. That is what makes full-depth
/// coverage tractable — a cycle (Quest -> Alias -> Quest) is just a name reference,
/// and there is no depth limit because the schema is flat rather than nested.
/// </summary>
public sealed class TypeSchema
{
    /// <summary>Catalog key, e.g. "Armor", "BodyTemplate", "LightEffectArchetype".</summary>
    public string Name { get; set; } = "";

    /// <summary>record | header | struct | arm | polymorphic-base</summary>
    public string Kind { get; set; } = "";

    public string GetterInterface { get; set; } = "";

    /// <summary>Null means Mutagen exposes this type as read-only (no mutable interface).</summary>
    public string? MutableInterface { get; set; }

    public string GetterInterfaceAssemblyQualified { get; set; } = "";
    public string? MutableInterfaceAssemblyQualified { get; set; }

    /// <summary>For an arm: the polymorphic base (catalog name) it satisfies.</summary>
    public string? AbstractBase { get; set; }

    /// <summary>For a polymorphic-base: the catalog names of its permitted arms.</summary>
    public List<string>? Arms { get; set; }

    /// <summary>For a record: its xEdit 4-char signature (Armor -> "ARMO"), read from the registration's
    /// TriggeringRecordType. The sig is one-to-many onto catalog names (GMST -> 4 GameSetting* variants),
    /// so the index carries one row per name and disambiguation happens at lookup, not here.</summary>
    public string? Signature { get; set; }

    /// <summary>For an enum-kind entry: the legal value names, listed once here and referenced by name from
    /// every field of this enum type, rather than inlining e.g. ActorValue's 156 values per field.</summary>
    public List<string>? EnumValues { get; set; }

    public int FieldCount { get; set; }
    public int WritableCount { get; set; }

    public List<FieldSchema> Fields { get; set; } = new();
}

public sealed class FieldSchema
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";

    /// <summary>scalar | enum | formlink | list | dict | substruct | polymorphic | value</summary>
    public string Cardinality { get; set; } = "";

    public bool Writable { get; set; }
    public bool Nullable { get; set; }

    /// <summary>FormKey / ModKey — emitted, but flagged as record identity, not a free-edit content field.</summary>
    public bool IsIdentity { get; set; }

    /// <summary>For a substruct or polymorphic field: the catalog entry it points to.</summary>
    public string? TypeRef { get; set; }

    /// <summary>For a polymorphic field: the catalog names of the permitted arms.</summary>
    public List<string>? Arms { get; set; }

    /// <summary>For a list: the element's display type.</summary>
    public string? ElementType { get; set; }

    /// <summary>For a list whose element is itself a modeled struct: the catalog entry the element points to.</summary>
    public string? ElementTypeRef { get; set; }

    /// <summary>For a list/dict whose element is a polymorphic union: the catalog names of the permitted arms.
    /// The element's catalog entry is also a polymorphic-base carrying the same arms — this is a convenience
    /// echo at the field level so a consumer doesn't have to follow the ref to learn the element is polymorphic.</summary>
    public List<string>? ElementArms { get; set; }

    /// <summary>For a dict-cardinality field: the key type's display name (the value lives in ElementType/ElementTypeRef).</summary>
    public string? KeyType { get; set; }

    /// <summary>For a formlink (or list of formlinks): the linked record type.</summary>
    public string? FormLinkTarget { get; set; }

    // ---- Assembly-qualified names: consumed by the write surface to resolve types at runtime. ----
    public string GetterTypeAssemblyQualified { get; set; } = "";
    public string? MutableTypeAssemblyQualified { get; set; }
    public string? ElementTypeAssemblyQualified { get; set; }
    public string? FormLinkTargetAssemblyQualified { get; set; }
}

/// <summary>Top-level emitted artifact: the whole flat catalog plus corpus-level counts.</summary>
public sealed class Corpus
{
    public string MutagenAssembly { get; set; } = "";
    public int RecordTypes { get; set; }
    public int TotalTypes { get; set; }
    public Dictionary<string, int> KindCounts { get; set; } = new();

    /// <summary>Flat catalog, keyed by <see cref="TypeSchema.Name"/>, sorted for deterministic output.</summary>
    public SortedDictionary<string, TypeSchema> Types { get; set; } = new(StringComparer.Ordinal);
}

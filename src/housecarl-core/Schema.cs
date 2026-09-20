using System.Text.Json.Serialization;

namespace HousecarlCore;

/// <summary>One entry per Mutagen-modeled type in the corpus; the catalog is FLAT — a field references another modeled type by name, never inlined, so there is no depth limit.</summary>
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

    /// <summary>For a record: its xEdit 4-char signature, read from the registration's TriggeringRecordType; one signature maps onto many catalog names, and disambiguation happens at lookup.</summary>
    public string? Signature { get; set; }

    /// <summary>For an enum-kind entry: the legal value names, listed once here and referenced by name from every field of this enum type.</summary>
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

    /// <summary>For a list/dict whose element is a polymorphic union: the catalog names of the permitted arms — a field-level echo of the element's own entry.</summary>
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

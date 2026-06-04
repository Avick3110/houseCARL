namespace HousecarlCore;

/// <summary>
/// The shared runtime name-surgery primitives — the <c>I…Getter → X</c> and <c>…BinaryOverlay</c> strips that
/// map a runtime reflection type back toward its corpus catalog name. Before this, the same two folk-rules were
/// copied by hand across <c>WriteEngine</c> (ConcreteOf, RunShow) and <c>WriteProof</c> (CatalogName,
/// PrimaryRecordCatalog, StripOverlay) — a name-resolution bug fixed in one copy wouldn't be caught by the others
/// (baseline review S4). Now the string surgery lives once.
///
/// This is deliberately NOT unified with the emit-time <c>CorpusGenerator.CatalogName</c>/<c>Normalize</c>: the
/// generator qualifies nested enums by DeclaringType and runs before the catalog exists, so its needs differ
/// legitimately. Only the runtime string primitives are shared here.
/// </summary>
public static class RecordNaming
{
    /// <summary>A binary overlay loads <c>AttackBinaryOverlay</c> for catalog <c>Attack</c>; strip the suffix.
    /// Leaves a non-overlay name unchanged.</summary>
    public static string StripOverlay(string name) =>
        name.EndsWith("BinaryOverlay", StringComparison.Ordinal) ? name[..^"BinaryOverlay".Length] : name;

    /// <summary>The Loqui getter-interface convention: <c>INpcGetter → Npc</c>. Strips the leading <c>I</c> and the
    /// trailing <c>Getter</c> only when BOTH are present; any other name passes through unchanged (so callers that
    /// already guarded on the shape, and callers that pass a non-interface name, both behave as before).</summary>
    public static string StripGetterInterface(string name) =>
        name.StartsWith("I", StringComparison.Ordinal) && name.EndsWith("Getter", StringComparison.Ordinal)
            ? name[1..^6] : name;

    /// <summary>Map a getter/interface name to its concrete CLASS name: <c>INpcGetter → Npc</c>, and also the
    /// non-Getter interface case <c>IFoo → Foo</c> (strip the leading <c>I</c> alone). A non-interface name passes
    /// through unchanged. A superset of <see cref="StripGetterInterface"/> — it ALSO drops the leading I on a plain
    /// <c>IFoo</c>, which the catalog-name strip deliberately does not. Used by interface→concrete resolution.</summary>
    public static string StripInterfaceToConcrete(string name) =>
        name.StartsWith("I", StringComparison.Ordinal)
            ? (name.EndsWith("Getter", StringComparison.Ordinal) ? name[1..^6] : name[1..])
            : name;

    /// <summary>Getter interface → SETTER interface, keeping the leading <c>I</c>: <c>INpcGetter → INpc</c> (NOT the
    /// catalog name — this is the mutable interface, distinct from <see cref="StripGetterInterface"/>'s <c>Npc</c>).
    /// Strips the trailing <c>Getter</c> only when present; any other name passes through unchanged.</summary>
    public static string GetterToSetterInterface(string getterInterfaceName) =>
        getterInterfaceName.EndsWith("Getter", StringComparison.Ordinal)
            ? getterInterfaceName[..^"Getter".Length] : getterInterfaceName;
}

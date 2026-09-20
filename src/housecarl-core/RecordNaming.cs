namespace HousecarlCore;

/// <summary>The shared runtime name-surgery primitives that map a reflection type back toward its corpus catalog
/// name. Deliberately not unified with the emit-time <c>CorpusGenerator.CatalogName</c>.</summary>
public static class RecordNaming
{
    public static string StripOverlay(string name) =>
        name.EndsWith("BinaryOverlay", StringComparison.Ordinal) ? name[..^"BinaryOverlay".Length] : name;

    public static string StripGetterInterface(string name) =>
        name.StartsWith("I", StringComparison.Ordinal) && name.EndsWith("Getter", StringComparison.Ordinal)
            ? name[1..^6] : name;

    /// <summary>A getter/interface name to its concrete CLASS name — a superset of <see cref="StripGetterInterface"/>
    /// that also drops the leading I on a plain <c>IFoo</c>.</summary>
    public static string StripInterfaceToConcrete(string name) =>
        name.StartsWith("I", StringComparison.Ordinal)
            ? (name.EndsWith("Getter", StringComparison.Ordinal) ? name[1..^6] : name[1..])
            : name;

    /// <summary>Getter interface → SETTER interface, keeping the leading <c>I</c>: <c>INpcGetter → INpc</c>.</summary>
    public static string GetterToSetterInterface(string getterInterfaceName) =>
        getterInterfaceName.EndsWith("Getter", StringComparison.Ordinal)
            ? getterInterfaceName[..^"Getter".Length] : getterInterfaceName;
}

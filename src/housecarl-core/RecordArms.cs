using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Every record of the named types, each yielded once under its own arm — the one typed enumeration a
/// scan goes through, because an abstract group's GRUP holds every arm; see docs/architecture/read-engine.md.</summary>
public static class RecordArms
{
    public static IEnumerable<IMajorRecordGetter> OfTypes(ISkyrimModGetter ov, IReadOnlyList<Type> getterTypes)
        => OfTypes(getterTypes, t => ov.EnumerateMajorRecords(t, throwIfUnknown: true));

    /// <summary>The same arms over a caller's own typed walk, for the resolver's counted walks.</summary>
    public static IEnumerable<IMajorRecordGetter> OfTypes(IReadOnlyList<Type> getterTypes, Func<Type, IEnumerable<IMajorRecordGetter>> walk)
        => getterTypes.SelectMany(t => walk(t).Where(t.IsInstanceOfType));
}

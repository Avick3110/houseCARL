using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Every record of the named types, each yielded once under its own arm — the one typed enumeration a
/// scan goes through, because an abstract group's GRUP holds every arm; see docs/architecture/read-engine.md.</summary>
public static class RecordArms
{
    public static IEnumerable<IMajorRecordGetter> OfTypes(ISkyrimModGetter ov, IReadOnlyList<Type> getterTypes)
        => getterTypes.SelectMany(t => ov.EnumerateMajorRecords(t, throwIfUnknown: true).Where(t.IsInstanceOfType));
}

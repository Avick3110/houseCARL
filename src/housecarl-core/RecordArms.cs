using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The one typed record enumeration every scan and sweep lane goes through.
///
/// <para>Mutagen's <c>EnumerateMajorRecords(Type)</c> SEEKS THE GRUP the type lives in, and an abstract group
/// (GLOB → GlobalShort / GlobalInt / GlobalFloat; GMST → its own arms) is one GRUP holding every arm of it. So a
/// filter naming one arm gets handed the whole group, and a filter naming several arms gets each record once per
/// arm. Either way the answer is wider than the filter and nothing says so — the failure this class exists to make
/// impossible to reintroduce lane by lane. The arm is re-checked per record, in this one place.</para>
/// </summary>
public static class RecordArms
{
    /// <summary>Every record in <paramref name="ov"/> of any of <paramref name="getterTypes"/>, each yielded once,
    /// under its own arm. throwIfUnknown is belt-and-braces — resolved types are always real.</summary>
    public static IEnumerable<IMajorRecordGetter> OfTypes(ISkyrimModGetter ov, IReadOnlyList<Type> getterTypes)
        => getterTypes.SelectMany(t => ov.EnumerateMajorRecords(t, throwIfUnknown: true).Where(t.IsInstanceOfType));
}

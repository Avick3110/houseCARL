using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>Every concrete major-record type Mutagen models for Skyrim, read off the assembly so a sweep over them is
/// closed by construction. Read by parent-in-hand.</summary>
internal static class ConcreteRecordTypes
{
    internal static List<Type> All() => typeof(Weapon).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                    && typeof(IMajorRecord).IsAssignableFrom(t))
        .ToList();
}

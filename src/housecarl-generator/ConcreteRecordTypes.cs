using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlGenerator;

/// <summary>Every concrete major-record type Mutagen models for Skyrim, read off the assembly so a sweep over them is
/// closed by construction. Shared by corpus-hygiene-guard's INV6 and parent-in-hand so the two cannot drift apart.</summary>
internal static class ConcreteRecordTypes
{
    internal static List<Type> All() => typeof(Weapon).Assembly.GetTypes()
        .Where(t => t.IsClass && !t.IsAbstract && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                    && typeof(IMajorRecord).IsAssignableFrom(t))
        .ToList();
}

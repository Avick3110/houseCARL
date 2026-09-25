using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Corpus-backed type resolution (signature "WEAP" / catalog name "Weapon" → getter Type(s)); the head holds one per service as <c>Types</c>.</summary>
internal sealed class TypeLookup
{
    // Built on the first resolution that needs it, never at construction; a failed build is not kept, so the next call retries.
    Dictionary<string, List<Type>>? _lookup;
    object? _lookupLock;

    Dictionary<string, List<Type>> Lookup => LazyInitializer.EnsureInitialized(ref _lookup, ref _lookupLock, BuildLookup);

    /// <summary>Build the type-string to getter-Type map from the corpus, keyed by both catalog name and signature, with a many-to-one signature and an abstract-group base name each accumulating their variants. A corpus type that will not load is skipped and surfaces as "unknown type" at query time, never as a silently wrong one.</summary>
    static Dictionary<string, List<Type>> BuildLookup()
    {
        var lookup = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
        void Add(string? key, Type t)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!lookup.TryGetValue(key, out var list)) lookup[key] = list = new List<Type>();
            if (!list.Contains(t)) list.Add(t);
        }
        var corpus = CorpusRulebook.LoadCorpus();
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "record") continue;
            var t = Type.GetType(ts.GetterInterfaceAssemblyQualified);
            if (t is null) continue;
            Add(ts.Name, t);
            Add(ts.Signature, t);
        }
        // The arms come off the polymorphic base's own corpus entry: derived, not hand-wired (the coverage cornerstone).
        foreach (var ts in corpus.Types.Values)
        {
            if (ts.Kind != "polymorphic-base" || ts.Arms is not { Count: > 0 } arms) continue;
            foreach (var armName in arms)
                if (corpus.Types.TryGetValue(armName, out var arm) && arm.Kind == "record"
                    && Type.GetType(arm.GetterInterfaceAssemblyQualified) is { } at)
                    Add(ts.Name, at);
        }
        return lookup;
    }

    /// <summary>The getter Types one exact key (catalog name or signature, case-insensitive) maps to.</summary>
    internal bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out List<Type> types) => Lookup.TryGetValue(key, out types);

    /// <summary>Every key's getter Types, one list per key.</summary>
    internal IEnumerable<List<Type>> Values => Lookup.Values;

    /// <summary>A user type SET to its getter Types: each entry's resolution unioned in order and deduped, through the same <see cref="Resolve"/> the singular form uses. Null for an absent or empty set.</summary>
    internal IReadOnlyList<Type>? ResolveSet(IReadOnlyList<string>? types) => ResolveSet(types, out _);

    /// <summary>The display names a type SET resolves to — the same spelling a matched body's type renders as. Null for an absent set and for an unknown entry, which the calling surfaces have already refused by name.</summary>
    internal IReadOnlyList<string>? DisplayNames(IReadOnlyList<string>? types)
    {
        try { return DisplayNames(ResolveSet(types)); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>The same names off the already-resolved getter Types.</summary>
    internal static IReadOnlyList<string>? DisplayNames(IReadOnlyList<Type>? types) =>
        types is { Count: > 0 } ? types.Select(t => RecordNaming.StripGetterInterface(t.Name)).Distinct(StringComparer.Ordinal).ToList() : null;

    /// <summary>The same resolution, also spelling each entry with the arms it expanded to — the label a response needs to name the types sharing one listing, since an entry like <c>GMST</c> names none of them itself.</summary>
    internal IReadOnlyList<Type>? ResolveSet(IReadOnlyList<string>? types, out string? armLabel)
    {
        armLabel = null;
        if (types is not { Count: > 0 }) return null;
        var union = new List<Type>();
        var spelled = new List<string>(types.Count);
        foreach (var ts in types)
        {
            var entry = (ts ?? "").Trim();
            var arms = Resolve(entry);
            foreach (var t in arms)
                if (!union.Contains(t)) union.Add(t);
            spelled.Add(SweepScope.SpellTypeEntry(entry, arms));
        }
        armLabel = string.Join(", ", spelled);
        return union;
    }

    /// <summary>A user type string to its getter Types, throwing and naming the bad input. A BLANK entry is refused as blank here, in one place, never quoted back as an unknown type; an empty or absent SET is a different thing and is handled by the callers.</summary>
    internal IReadOnlyList<Type> Resolve(string type)
    {
        if (type.Trim().Length == 0)
            throw new ArgumentException(
                "a blank record type — pass a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon'), " +
                "or omit the parameter to leave the types unnarrowed.");
        if (Lookup.TryGetValue(type.Trim(), out var types)) return types;
        throw new ArgumentException(
            $"unknown record type '{type}'. Expected a 4-char signature (e.g. 'WEAP') or a catalog name (e.g. 'Weapon').");
    }
}

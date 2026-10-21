using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// OwnerRanks — a faction owner Mutagen could not type is retyped before a serialize, so its rank word goes back out as the int it is. Contract in docs/architecture/write-path.md.
public static class OwnerRanks
{
    /// <summary>Retype every <see cref="UntypedOwner"/> in <paramref name="mod"/> whose owner <paramref name="isFaction"/>
    /// names into a <see cref="FactionOwner"/>, keeping the rank word's value. Mutagen reads an owner in another plugin
    /// untyped when no link cache is passed, and then writes its rank word as a FormID: remapped through the new master
    /// list (-1 becomes 0x07FFFFFF) and adding the plugin it seems to point into as a master. An NPC owner's second word
    /// is a real Global link and stays untyped; an owner the order cannot name is left as read.</summary>
    public static void Settle(IMod mod, Func<FormKey, bool> isFaction)
    {
        foreach (var rec in mod.EnumerateMajorRecords())
            foreach (var ed in ExtraDataIn(rec))
                if (ed.Owner is UntypedOwner uo && isFaction(uo.OwnerData.FormKey))
                    ed.Owner = new FactionOwner
                    {
                        Faction = new FormLink<IFactionGetter>(uo.OwnerData.FormKey),
                        RequiredRank = (int)(uo.VariableData.FormKey.ID << 8) >> 8,   // the rank's low 24 bits, sign-extended: -1 read as id FFFFFF in whatever plugin index FF mapped to
                    };
    }

    /// <summary>The one faction test, for the write and the dry-run preview alike: the order's winning record for the key is a FACT, wherever it lives.</summary>
    public static Func<FormKey, bool> FactionIn(LoadOrderResolver.IndexView view) =>
        fk => !fk.IsNull && view.WinnerClassOf(fk) is { } cls && typeof(IFactionGetter).IsAssignableFrom(cls);

    /// <summary>Every <see cref="IExtraData"/> reachable from <paramref name="node"/>, by the properties whose declared type can lead to one.</summary>
    static IEnumerable<IExtraData> ExtraDataIn(object? node)
    {
        switch (node)
        {
            case null: yield break;
            case IExtraData ed: yield return ed; yield break;
            case IEnumerable list:
                foreach (var e in list) foreach (var ed in ExtraDataIn(e)) yield return ed;
                yield break;
        }
        foreach (var p in Leading(node.GetType()))
            foreach (var ed in ExtraDataIn(p.GetValue(node))) yield return ed;
    }

    static readonly ConcurrentDictionary<Type, PropertyInfo[]> LeadingCache = new();

    /// <summary>The properties of <paramref name="t"/> that can lead to an ExtraData, discovered from Mutagen's model and kept per type.</summary>
    static PropertyInfo[] Leading(Type t) => LeadingCache.GetOrAdd(t, _ => Discover(t, new HashSet<Type>()));

    // The cycle set is the current path: a type reached again through a sibling property is walked again.
    static PropertyInfo[] Discover(Type t, HashSet<Type> path)
    {
        if (!path.Add(t)) return Array.Empty<PropertyInfo>();
        try
        {
            return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetIndexParameters().Length == 0 && CanLead(p.PropertyType, path))
                    .ToArray();
        }
        finally { path.Remove(t); }
    }

    static bool CanLead(Type t, HashSet<Type> path)
    {
        if (typeof(IExtraDataGetter).IsAssignableFrom(t)) return true;
        if (t != typeof(string) && t.GetInterfaces().Append(t).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } seq)
            return CanLead(seq.GetGenericArguments()[0], path);
        // Only Mutagen's own Skyrim model can hold one; a major record nested under another is reached by EnumerateMajorRecords instead.
        if (t.Assembly != typeof(ExtraData).Assembly || typeof(IMajorRecordGetter).IsAssignableFrom(t)) return false;
        return Discover(t, path).Length > 0;
    }
}

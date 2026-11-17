using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The reach evaluators' tables, shipped as data (reach-fields.json): which template category supplies each
/// NPC field a SPID filter reads, which item field a KID filter form of each type is compared with, and the names KID
/// reads an exact string as an archetype or actor value. A table the code reads that the data lacks fails the load.</summary>
public sealed class ReachFields
{
    /// <summary>The NPC fields SPID's filters read, each named once in the data.</summary>
    public enum NpcReader { Name, Unique, Summonable, Keywords, Race, Sex, Voice, Skin, Level, Skills, Class, Factions, Spells, Perks, CombatStyle, Outfit }

    public IReadOnlyDictionary<NpcReader, NpcConfiguration.TemplateFlag> NpcTemplate { get; }
    /// <summary>By filter record type, then item record type: the item's field paths.</summary>
    public IReadOnlyDictionary<(string Filter, string Item), IReadOnlyList<string>> KidForms { get; }
    public IReadOnlySet<string> KidArchetypes { get; }
    public IReadOnlySet<string> KidActorValues { get; }

    ReachFields(IReadOnlyDictionary<NpcReader, NpcConfiguration.TemplateFlag> t, IReadOnlyDictionary<(string, string), IReadOnlyList<string>> k,
                IReadOnlySet<string> arch, IReadOnlySet<string> av)
    { NpcTemplate = t; KidForms = k; KidArchetypes = arch; KidActorValues = av; }

    static readonly Lazy<ReachFields> Shared = new(() => Parse(EmbeddedJson.Read("reach-fields.json", "reach fields")));

    /// <summary>The embedded tables, read once.</summary>
    public static ReachFields Load() => Shared.Value;

    /// <summary>Read the tables from JSON text; throws naming the first entry the code cannot use.</summary>
    public static ReachFields Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var t = new Dictionary<NpcReader, NpcConfiguration.TemplateFlag>();
        foreach (var p in root.GetProperty("npcTemplate").GetProperty("readers").EnumerateObject())
        {
            if (!Enum.TryParse<NpcReader>(p.Name, out var r)) throw new InvalidOperationException($"reach-fields: npcTemplate reader '{p.Name}' is not one SPID's filters read.");
            if (!Enum.TryParse<NpcConfiguration.TemplateFlag>(p.Value.GetString(), out var f) || !Enum.IsDefined(f))
                throw new InvalidOperationException($"reach-fields: npcTemplate '{p.Name}' names '{p.Value.GetString()}', no template category.");
            t[r] = f;
        }
        foreach (var r in Enum.GetValues<NpcReader>())
            if (!t.ContainsKey(r)) throw new InvalidOperationException($"reach-fields: npcTemplate names no category for '{r}'.");
        var k = new Dictionary<(string, string), IReadOnlyList<string>>();
        foreach (var e in root.GetProperty("kidForms").GetProperty("fields").EnumerateArray())
        {
            var filter = e.GetProperty("filter").GetString()!;
            var item = e.GetProperty("item").GetString()!;
            foreach (var n in new[] { filter, item })
                if (SpidGrammar.Getter(n) is null) throw new InvalidOperationException($"reach-fields: kidForms names '{n}', no record type.");
            k[(filter, item)] = e.GetProperty("paths").EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        var s = root.GetProperty("kidStrings");
        // KID looks the names up before it lowers the filter, so they match in their own case.
        HashSet<string> Names(string key) => new(s.GetProperty(key).EnumerateArray().Select(x => x.GetString()!), StringComparer.Ordinal);
        return new ReachFields(t, k, Names("archetypes"), Names("actorValues"));
    }

    // ---- field paths: 'A.B', 'A[*].B', read by property name on the record's own runtime type ----

    static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Props = new();

    /// <summary>Every FormKey a field path holds on a record: a link's target, each element of a list; a null link holds none.</summary>
    public static IEnumerable<FormKey> Links(object record, string path)
    {
        IEnumerable<object?> at = new[] { record };
        foreach (var seg in path.Split('.'))
        {
            bool each = seg.EndsWith("[*]", StringComparison.Ordinal);
            var name = each ? seg[..^3] : seg;
            at = at.SelectMany(o => Step(o, name, each));
        }
        foreach (var o in at)
            if (o is IFormLinkGetter l && !l.IsNull) yield return l.FormKey;
    }

    static IEnumerable<object?> Step(object? o, string name, bool each)
    {
        if (o is null) yield break;
        var prop = Props.GetOrAdd((o.GetType(), name), k => k.Item1.GetProperty(k.Item2, BindingFlags.Public | BindingFlags.Instance)
            ?? k.Item1.GetInterfaces().Select(i => i.GetProperty(k.Item2)).FirstOrDefault(p => p is not null));
        if (prop is null) yield break;
        var v = prop.GetValue(o);
        if (!each) { yield return v; yield break; }
        if (v is IEnumerable list && v is not string)
            foreach (var x in list) yield return x;
    }
}

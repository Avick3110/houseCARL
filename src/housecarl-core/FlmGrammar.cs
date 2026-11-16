using System.Text.Json;

namespace HousecarlCore;

/// <summary>FLM's 'lines' block of the runtime-format source: the keys, the delimiters, the simplified keys' fixed lists,
/// the order FLM fills its lists in and the filter's plugin extensions. A value the code has no rule for fails the load,
/// naming the field.</summary>
public static partial class RuntimeFormats
{
    /// <summary>FLM's row of the embedded source, with its lines block (memoized).</summary>
    public static DistributorFormat Flm => Distributor("flm") ?? throw new InvalidOperationException("runtime-format source: no flm row.");

    /// <summary>FLM's lines block; null when its row has none.</summary>
    static FlmGrammar? FlmLinesBlock(JsonElement row, string ctx)
    {
        if (!row.TryGetProperty("lines", out var l)) return null;
        ctx += ".lines";
        Known(l, "section", ctx, "unnamedOnly");
        Known(l, "lineOrder", ctx, "keyIgnoreCaseThenLine");
        Known(l, "passes", ctx, "filtersCollectionsThenAliasesGroupsThenRest");
        Known(l, "conflict", ctx, "skipsDuplicates");
        var keys = l.GetProperty("keys").EnumerateArray().Select(k =>
        {
            var key = Str(k, "key", ctx + ".keys");
            var kind = Str(k, "kind", ctx + ".keys." + key) switch
            {
                "alias" => FlmKind.Alias,
                "group" => FlmKind.Group,
                "collection" => FlmKind.Collection,
                "filter" => FlmKind.Filter,
                "modEvent" => FlmKind.ModEvent,
                "remove" => FlmKind.Remove,
                "formList" => FlmKind.FormList,
                "formsPair" => FlmKind.FormsPair,
                "pair" => FlmKind.Pair,
                "list" => FlmKind.List,
                var v => throw Unknown(ctx + ".keys." + key, "kind", v),
            };
            var lists = k.TryGetProperty("lists", out _) ? Strs(k, "lists", ctx + ".keys." + key) : Array.Empty<string>();
            var names = k.TryGetProperty("names", out _) ? Strs(k, "names", ctx + ".keys." + key) : Array.Empty<string>();
            // A pair names two forms into two lists, a list key its forms into one; another shape is a rule the code does not have.
            if ((kind == FlmKind.Pair && (lists.Length != 2 || names.Length != 2)) || (kind == FlmKind.List && lists.Length != 1))
                throw Unknown(ctx + ".keys." + key, "lists", string.Join(",", lists));
            return new FlmKey(key, kind, lists, names);
        }).ToList();
        var order = Strs(l, "addOrder", ctx);
        foreach (var o in order)
            if (!keys.Any(k => k.Key == o && k.Kind is FlmKind.Pair or FlmKind.List or FlmKind.FormList)) throw Unknown(ctx, "addOrder", o);
        return new FlmGrammar(
            Strs(l, "reads", ctx),
            Strs(l, "commentLeads", ctx).Select(c => Single(c, ctx + ".commentLeads")).ToArray(),
            Single(Str(l, "assign", ctx), ctx + ".assign"), Single(Str(l, "fields", ctx), ctx + ".fields"),
            Single(Str(l, "item", ctx), ctx + ".item"), Single(Str(l, "name", ctx), ctx + ".name"),
            Single(Str(l, "listForms", ctx), ctx + ".listForms"), Single(Str(l, "not", ctx), ctx + ".not"),
            Single(Str(l, "require", ctx), ctx + ".require"), Strs(l, "filterExtensions", ctx),
            keys, order, Strs(l, "collectionTypes", ctx),
            RecordTypes(l, "seedTypes", ctx), RecordTypes(l, "plantTypes", ctx),
            RecordType(l, "listRecord", ctx), RecordType(l, "keywordRecord", ctx));
    }

    /// <summary>One Mutagen record name, checked as <c>RecordTypes</c> checks a list.</summary>
    static string RecordType(JsonElement l, string prop, string ctx)
    {
        var name = Str(l, prop, ctx);
        return SpidGrammar.Getter(name) is null ? throw Unknown(ctx, prop, name) : name;
    }
}

/// <summary>What an FLM key does: defines a name (alias, group, collection, filter), adds forms to a list it names or to
/// a fixed list (pair, list), or is a key FLM reads and this build does not resolve (mod events) or that does nothing at
/// data load (remove, forms pair).</summary>
public enum FlmKind { Alias, Group, Collection, Filter, ModEvent, Remove, FormList, FormsPair, Pair, List }

/// <summary>One of FLM's keys: its spelling, its kind, and for a simplified key the fixed lists it adds to and the names of its two halves.</summary>
public sealed record FlmKey(string Key, FlmKind Kind, IReadOnlyList<string> Lists, IReadOnlyList<string> Names);

/// <summary>FLM's line grammar, as its row states it.</summary>
public sealed record FlmGrammar(
    IReadOnlyList<string> Reads,
    IReadOnlyList<char> CommentLeads,
    char Assign, char Fields, char Item, char Name, char ListForms, char Not, char Require,
    IReadOnlyList<string> FilterExtensions,
    IReadOnlyList<FlmKey> Keys,
    IReadOnlyList<string> AddOrder,
    IReadOnlyList<string> CollectionTypes,
    IReadOnlyList<string> SeedTypes,
    IReadOnlyList<string> PlantTypes,
    string ListRecord,
    string KeywordRecord)
{
    /// <summary>A key as FLM matches it, lowercased; null for a key FLM does not know.</summary>
    public FlmKey? Key(string written) => Keys.FirstOrDefault(k => k.Key.Equals(written, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether FLM's fill step adds a key's lines at all.</summary>
    public bool Adds(FlmKey k) => AddOrder.Contains(k.Key);
}

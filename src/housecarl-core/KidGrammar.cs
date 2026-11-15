using System.Text.Json;

namespace HousecarlCore;

/// <summary>KID's 'lines' block of the runtime-format source: the five sections, the type table with each type's pass and
/// whether it takes traits, the delimiters and the filter whitelist the server reads a <c>_KID.ini</c> line by and the
/// generator prints into the kid-authoring skill. A value the code has no rule for fails the load, naming the field.</summary>
public static partial class RuntimeFormats
{
    /// <summary>KID's row of the embedded source, with its lines block (memoized).</summary>
    public static DistributorFormat Kid => Distributor("kid") ?? throw new InvalidOperationException("runtime-format source: no kid row.");

    /// <summary>KID's lines block; null when its row has none.</summary>
    static KidGrammar? KidLinesBlock(JsonElement row, string ctx)
    {
        if (!row.TryGetProperty("lines", out var l)) return null;
        ctx += ".lines";
        Known(l, "section", ctx, "unnamedOnly");
        Known(l, "lineOrder", ctx, "keyIgnoreCaseThenLine");
        if (l.GetProperty("trailingComment").GetBoolean()) throw Unknown(ctx, "trailingComment", "true");
        // The reader takes the five sections at these places; another order is a rule the code does not have.
        var fields = Strs(l, "fields", ctx);
        if (!fields.SequenceEqual(new[] { "keyword", "type", "filters", "traits", "chance" })) throw Unknown(ctx, "fields", string.Join(",", fields));
        var groupFields = Strs(l, "groupFields", ctx);
        if (!groupFields.SequenceEqual(new[] { "name", "members" })) throw Unknown(ctx, "groupFields", string.Join(",", groupFields));

        var types = l.GetProperty("types").EnumerateArray().Select(t =>
        {
            var record = Str(t, "record", ctx + ".types");
            if (SpidGrammar.Getter(record) is null) throw Unknown(ctx + ".types", "record", record);
            var runs = Str(t, "runs", ctx + ".types");
            if (!int.TryParse(runs, out var n) || n <= 0) throw Unknown(ctx + ".types", "runs", runs);
            return new KidType(Str(t, "key", ctx + ".types"), record, n, t.GetProperty("traits").GetBoolean());
        }).ToList();
        if (types.Select(t => t.Runs).Distinct().Count() != types.Count) throw new InvalidOperationException($"runtime-format source [{ctx}.types]: two types share a pass.");

        var keyword = Str(l, "keywordRecord", ctx);
        if (SpidGrammar.Getter(keyword) is null) throw Unknown(ctx, "keywordRecord", keyword);
        return new KidGrammar(
            Strs(l, "reads", ctx),
            Strs(l, "commentLeads", ctx).Select(c => Single(c, ctx + ".commentLeads")).ToArray(),
            Single(Str(l, "assign", ctx), ctx + ".assign"), Single(Str(l, "sections", ctx), ctx + ".sections"),
            Single(Str(l, "item", ctx), ctx + ".item"), Single(Str(l, "all", ctx), ctx + ".all"),
            Single(Str(l, "not", ctx), ctx + ".not"), Single(Str(l, "partial", ctx), ctx + ".partial"),
            Str(l, "unset", ctx), Str(l, "groupKey", ctx), Str(l, "modelEnding", ctx), keyword, types,
            RecordTypes(l, "filterTypes", ctx));
    }
}

/// <summary>One KID item type: its type string, the Mutagen record it tags, its place in KID's pass order, and whether a
/// traits section is read on its lines.</summary>
public sealed record KidType(string Key, string Record, int Runs, bool Traits);

/// <summary>KID's line grammar, as its row states it.</summary>
public sealed record KidGrammar(
    IReadOnlyList<string> Reads,
    IReadOnlyList<char> CommentLeads,
    char Assign, char Sections, char Item, char All, char Not, char Partial,
    string Unset,
    string GroupKey,
    string ModelEnding,
    string KeywordRecord,
    IReadOnlyList<KidType> Types,
    IReadOnlyList<string> FilterTypes)
{
    /// <summary>A type by its string, exact as KID's own find is; null for one KID does not take.</summary>
    public KidType? Type(string key) => Types.FirstOrDefault(t => t.Key == key);

    /// <summary>Whether a section is set: KID skips an empty section or the unset word in any case.</summary>
    public bool Set(string section) => section.Length > 0 && !section.Equals(Unset, StringComparison.OrdinalIgnoreCase);
}

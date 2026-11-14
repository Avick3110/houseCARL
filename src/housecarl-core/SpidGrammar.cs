using System.Text.Json;

namespace HousecarlCore;

/// <summary>SPID's 'lines' block of the runtime-format source: the line kinds, the type table, the delimiters and the
/// order the server reads a <c>_DISTR.ini</c> line by and the generator prints into the spid-authoring skill. A value
/// the code has no rule for fails the load, naming the field.</summary>
public static partial class RuntimeFormats
{
    /// <summary>SPID's row of the embedded source, with its lines block (memoized).</summary>
    public static DistributorFormat Spid => Distributor("spid") ?? throw new InvalidOperationException("runtime-format source: no spid row.");

    /// <summary>A row's lines block; null for a row with none. Only SPID's reader has landed, so a lines block on another row has no rule.</summary>
    static SpidGrammar? Lines(JsonElement row, string key, string ctx)
    {
        if (!row.TryGetProperty("lines", out var l)) return null;
        if (key != "spid") throw Unknown(ctx, "lines", "a lines block");
        ctx += ".lines";
        Known(l, "section", ctx, "unnamedOnly");
        Known(l, "lineOrder", ctx, "keyIgnoreCaseThenLine");
        if (l.GetProperty("trailingComment").GetBoolean()) throw Unknown(ctx, "trailingComment", "true");

        var kinds = l.GetProperty("kinds").EnumerateArray().Select(k =>
        {
            var kind = Str(k, "kind", ctx + ".kinds") switch
            {
                "exclusiveGroup" => SpidLineKind.ExclusiveGroup,
                "linked" => SpidLineKind.Linked,
                "death" => SpidLineKind.Death,
                "regular" => SpidLineKind.Regular,
                var v => throw Unknown(ctx + ".kinds", "kind", v),
            };
            var fields = Strs(k, "fields", ctx + ".kinds").Select(f => f switch
            {
                "value" => SpidField.Value, "strings" => SpidField.Strings, "forms" => SpidField.Forms,
                "level" => SpidField.Level, "traits" => SpidField.Traits, "countOrIndex" => SpidField.CountOrIndex,
                "chance" => SpidField.Chance, "name" => SpidField.Name, "members" => SpidField.Members,
                "parents" => SpidField.Parents,
                var v => throw Unknown(ctx + ".kinds.fields", kind.ToString(), v),
            }).ToList();
            return new SpidKind(kind, OptStr(k, "key"), OptStr(k, "prefix"), OptStr(k, "global"), fields);
        }).ToList();
        foreach (var need in Enum.GetValues<SpidLineKind>())
            if (kinds.All(k => k.Kind != need)) throw new InvalidOperationException($"runtime-format source [{ctx}.kinds]: no '{need}' kind.");

        var types = l.GetProperty("types").EnumerateArray().Select(t => new SpidType(
            Str(t, "key", ctx + ".types"), Str(t, "name", ctx + ".types"),
            Str(t, "runs", ctx + ".types") switch
            {
                "ownPass" => SpidType.OwnPass,
                "inferred" => SpidType.Inferred,
                var v when int.TryParse(v, out var n) && n > 0 => n,
                var v => throw Unknown(ctx + ".types", "runs", v),
            },
            Str(t, "conflict", ctx + ".types") switch
            {
                "everyLine" => SpidConflict.EveryLine,
                "sums" => SpidConflict.Sums,
                "firstWins" => SpidConflict.FirstWins,
                "inferred" => SpidConflict.Inferred,
                var v => throw Unknown(ctx + ".types", "conflict", v),
            },
            Str(t, "missingValue", ctx + ".types") switch
            {
                "createsKeyword" => SpidMissingValue.CreatesKeyword,
                "createsKeywordHandsNothing" => SpidMissingValue.CreatesKeywordHandsNothing,
                "dropsLine" => SpidMissingValue.DropsLine,
                var v => throw Unknown(ctx + ".types", "missingValue", v),
            },
            OptStr(t, "record"))).ToList();
        if (types.Count(t => t.Runs == SpidType.Inferred) != 1) throw new InvalidOperationException($"runtime-format source [{ctx}.types]: exactly one type must be 'inferred'.");

        var fin = l.GetProperty("final");
        return new SpidGrammar(
            Strs(l, "reads", ctx),
            Strs(l, "commentLeads", ctx).Select(c => Single(c, ctx + ".commentLeads")).ToArray(),
            Single(Str(l, "assign", ctx), ctx + ".assign"), Single(Str(l, "sections", ctx), ctx + ".sections"),
            Single(Str(l, "item", ctx), ctx + ".item"), Single(Str(l, "all", ctx), ctx + ".all"),
            Single(Str(l, "not", ctx), ctx + ".not"), Single(Str(l, "partial", ctx), ctx + ".partial"),
            Str(l, "unset", ctx), kinds, Str(fin, "prefix", ctx + ".final"), Strs(fin, "types", ctx + ".final"), types,
            RecordTypes(l, "filterTypes", ctx), RecordTypes(l, "targetTypes", ctx), RecordTypes(l, "stringLookups", ctx));
    }

    /// <summary>A list of Mutagen record names, each checked to name a Skyrim record getter so a typo fails the load.</summary>
    static string[] RecordTypes(JsonElement l, string prop, string ctx)
    {
        var names = Strs(l, prop, ctx);
        foreach (var n in names)
            if (SpidGrammar.Getter(n) is null) throw Unknown(ctx, prop, n);
        return names;
    }
}

/// <summary>A SPID line's kind, by its key's shape.</summary>
public enum SpidLineKind { Regular, Death, Linked, ExclusiveGroup }

/// <summary>A field of a SPID line, by its place in the line's '|' sections.</summary>
public enum SpidField { Value, Strings, Forms, Level, Traits, CountOrIndex, Chance, Name, Members, Parents }

/// <summary>What SPID does with the lines of one type that pass an NPC: every line gives, item counts add up, the first line wins, or a <c>Form</c> line's inferred type decides.</summary>
public enum SpidConflict { EveryLine, Sums, FirstWins, Inferred }

/// <summary>What SPID does with a value name no record holds, by the line's type.</summary>
public enum SpidMissingValue { CreatesKeyword, CreatesKeywordHandsNothing, DropsLine }

/// <summary>One line kind: the exact key or key prefix it is spelt with, and its fields in section order.</summary>
public sealed record SpidKind(SpidLineKind Kind, string? Key, string? Prefix, string? Global, IReadOnlyList<SpidField> Fields);

/// <summary>One distributable type: its key, its plural name, when it runs, what passing lines do, what a value name no
/// record holds does, and the record a <c>Form</c> line's value must be to join this type's order.</summary>
public sealed record SpidType(string Key, string Name, int Runs, SpidConflict Conflict, SpidMissingValue MissingValue, string? Record)
{
    /// <summary><see cref="Runs"/> for a type distributed in a pass of its own, after the numbered ones.</summary>
    public const int OwnPass = int.MaxValue - 1;

    /// <summary><see cref="Runs"/> for <c>Form</c>, whose lines join the type their value is.</summary>
    public const int Inferred = int.MaxValue;
}

/// <summary>SPID's line grammar, as its row states it.</summary>
public sealed record SpidGrammar(
    IReadOnlyList<string> Reads,
    IReadOnlyList<char> CommentLeads,
    char Assign, char Sections, char Item, char All, char Not, char Partial,
    string Unset,
    IReadOnlyList<SpidKind> Kinds,
    string FinalPrefix, IReadOnlyList<string> FinalTypes,
    IReadOnlyList<SpidType> Types,
    IReadOnlyList<string> FilterTypes, IReadOnlyList<string> TargetTypes, IReadOnlyList<string> StringLookups)
{
    /// <summary>One line kind.</summary>
    public SpidKind Kind(SpidLineKind k) => Kinds.First(x => x.Kind == k);

    /// <summary>A type by its key, case-sensitive as SPID's own find is; null for a key SPID does not take.</summary>
    public SpidType? Type(string key) => Types.FirstOrDefault(t => t.Key == key);

    /// <summary>The Skyrim getter interface a Mutagen record name stands for; null for a name that is none.</summary>
    public static Type? Getter(string name)
        => _getters.GetOrAdd(name, n => typeof(Mutagen.Bethesda.Skyrim.SkyrimMod).Assembly.GetType($"Mutagen.Bethesda.Skyrim.I{n}Getter"));

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> _getters = new(StringComparer.Ordinal);

    /// <summary>Whether a record class is one of <paramref name="names"/>.</summary>
    public static bool IsOneOf(Type? cls, IEnumerable<string> names)
        => cls is not null && names.Any(n => Getter(n) is { } g && g.IsAssignableFrom(cls));
}

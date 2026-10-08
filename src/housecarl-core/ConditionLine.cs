using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>A condition element's one-line summary, e.g. <c>[HasPerk(058200:Skyrim.esm) == 1 on Subject OR]</c>.</summary>
static class ConditionLine
{
    /// <summary>The line for a condition; <paramref name="refToken"/> is its first FormID. Throws when a part cannot be read.</summary>
    internal static string Of(IConditionGetter cond, out string? refToken)
    {
        string? firstRef = null;
        var data = cond.Data;
        var args = new List<string>();
        foreach (var p in OwnParameters(data.GetType()))
            args.Add(Token(p.GetValue(data), p.PropertyType, data, ref firstRef));

        var line = $"{data.Function}({string.Join(", ", args)}) {Operator(cond.CompareOperator)} {Comparand(cond, ref firstRef)} on {RunOn(data, ref firstRef)}";
        var flags = FlagWords(cond.Flags);
        refToken = firstRef;
        return $"[{line}{(flags.Length == 0 ? "" : " " + flags)}]";
    }

    /// <summary>The parameters the concrete Data arm declares itself, minus the ones Mutagen names unused.</summary>
    static IReadOnlyList<PropertyInfo> OwnParameters(Type armType) => _parameters.GetOrAdd(armType, t =>
    {
        var arm = t.GetInterfaces()
            .Where(i => typeof(IConditionDataGetter).IsAssignableFrom(i) && i != typeof(IConditionDataGetter) && i.Name.EndsWith("Getter", StringComparison.Ordinal))
            .OrderByDescending(i => i.GetInterfaces().Length)
            .FirstOrDefault();
        if (arm is null) return Array.Empty<PropertyInfo>();
        return arm.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetIndexParameters().Length == 0 && !p.Name.Contains("Unused", StringComparison.Ordinal))
            .OrderBy(p => p.MetadataToken)
            .ToArray();
    });

    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> _parameters = new();

    /// <summary>One value as the read emits it, its note when it has no token; the first FormID is kept.</summary>
    static string Token(object? v, Type declared, object parent, ref string? firstRef)
    {
        var leaf = ReadEngine.EmitToken(v, declared, parent);
        if (!leaf.HasValue) return leaf.Note ?? "";
        if (firstRef is null && FormKey.TryFactory(leaf.Token, out _)) firstRef = leaf.Token;
        return leaf.Token;
    }

    static string Comparand(IConditionGetter cond, ref string? firstRef) => cond switch
    {
        IConditionFloatGetter f => Token(f.ComparisonValue, typeof(float), cond, ref firstRef),
        IConditionGlobalGetter g => Token(g.ComparisonValue, typeof(IFormLinkGetter<IGlobalGetter>), cond, ref firstRef),
        _ => throw new NotSupportedException($"no comparand reading for {cond.GetType().Name}"),
    };

    /// <summary>The run-on target; Reference carries its form and the alias and package-data kinds their index.</summary>
    static string RunOn(IConditionDataGetter data, ref string? firstRef) => data.RunOnType switch
    {
        Condition.RunOnType.Reference => $"Reference {Token(data.Reference, typeof(IFormLinkGetter<ISkyrimMajorRecordGetter>), data, ref firstRef)}",
        Condition.RunOnType.QuestAlias or Condition.RunOnType.PackageData => $"{data.RunOnType} {data.RunOnTypeIndex}",
        _ => data.RunOnType.ToString(),
    };

    static string Operator(CompareOperator op) => op switch
    {
        CompareOperator.EqualTo => "==",
        CompareOperator.NotEqualTo => "!=",
        CompareOperator.GreaterThan => ">",
        CompareOperator.GreaterThanOrEqualTo => ">=",
        CompareOperator.LessThan => "<",
        CompareOperator.LessThanOrEqualTo => "<=",
        _ => op.ToString(),
    };

    /// <summary>The set flag names as the read decodes them, OR moved last.</summary>
    static string FlagWords(Condition.Flag flags)
    {
        var words = ReadEngine.FlagParts(new ReadEngine.FlagBits((ulong)flags, typeof(Condition.Flag)), out _);
        if (words.Remove(nameof(Condition.Flag.OR))) words.Add(nameof(Condition.Flag.OR));
        return string.Join(" ", words);
    }
}

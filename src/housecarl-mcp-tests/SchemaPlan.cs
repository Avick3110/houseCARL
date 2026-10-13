using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The schema's plan for one where= clause, as the scan makes it before reading any record.</summary>
static class SchemaPlan
{
    /// <summary>The plan's refusal, or null; no types plans over every record type.</summary>
    public static string? Of(string clause, params string[] types) => Set(clause, out _, types);

    /// <summary>The plan's refusal, with the set it bound for a scan after it.</summary>
    public static string? Set(string clause, out FieldPredicateSet set, params string[] types)
    {
        var (parsed, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        set = parsed!;
        var roots = types.SelectMany(TestCorpus.Rulebook.RecordTypesNamed).ToList();
        return set.Plan(TestCorpus.Rulebook.PathWalk, roots.Count == 0 ? null : roots);
    }
}

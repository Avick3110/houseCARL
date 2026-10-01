using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Every writable scalar, enum, value and form-link leaf in the generated corpus (and every whole-coercible
/// list element) resolves to a runtime type the engine can coerce, or sits in a named deferred bucket. The same
/// corpus-wide audit the coerce-audit probe ran, over the test corpus.</summary>
[Trait("tier", "unit")]
public sealed class CoerceAuditTests
{
    sealed record Audit(int HardTargets, int FloiHandled, List<string> Unresolved, List<string> Uncoercible, List<string> WholeElements);

    static Audit Run()
    {
        var corpus = CorpusRulebook.LoadCorpus(TestCorpus.Path);
        var unresolved = new List<string>();
        var uncoercible = new List<string>();
        var wholeElements = new List<string>();
        int hard = 0, floi = 0;
        foreach (var ts in corpus.Types.Values)
        foreach (var f in ts.Fields)
        {
            if (!f.Writable || f.IsIdentity) continue;
            var site = $"{ts.Name}.{f.Name}";
            string? aq;
            switch (f.Cardinality)
            {
                case "scalar": case "enum": case "value": case "formlink":
                    aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                    break;
                case "list": case "dict":
                    if (f.ElementTypeRef is null && f.ElementTypeAssemblyQualified is { } eaq) aq = eaq;
                    else if (WriteEngine.IsWholeCoercibleElement(f.ElementTypeRef, f.ElementTypeAssemblyQualified)
                             && f.ElementTypeAssemblyQualified is { } weaq) { aq = weaq; wholeElements.Add(site); }
                    else continue;
                    break;
                default:
                    continue;
            }
            if (string.IsNullOrEmpty(aq)) continue;
            hard++;
            var rt = WriteEngine.ResolveType(aq);
            if (rt is null) { unresolved.Add($"{site}: {aq}"); continue; }
            if (WriteEngine.IsFormLinkOrIndex(rt)) { floi++; continue; }
            if (WriteEngine.CanCoerce(rt)) continue;
            var u = Nullable.GetUnderlyingType(rt) ?? rt;
            // The two deferred buckets: a type-erased object condition parameter, and an owned-child record.
            if (u == typeof(object)) continue;
            if (corpus.Types.TryGetValue(u.Name, out var ut) && ut.Kind == "record") continue;
            uncoercible.Add($"{site}: {u.FullName}");
        }
        return new Audit(hard, floi, unresolved, uncoercible, wholeElements);
    }

    static readonly Lazy<Audit> Result = new(Run);

    // coerce-audit: no assembly-qualified type name FAILED to resolve (corpus/runtime mismatch)
    [Fact]
    public void EveryWritableLeafTypeNameResolves() => Assert.Empty(Result.Value.Unresolved);

    // coerce-audit: UNCOERCIBLE value types (real gaps): none
    [Fact]
    public void EveryResolvedWritableLeafCoercesOrIsDeferredByName() => Assert.Empty(Result.Value.Uncoercible);

    // The audit walks real targets: the corpus has coercion targets, form-or-index condition targets, and both
    // asset-link list elements route as whole-coercible.
    [Fact]
    public void TheAuditWalksRealTargets()
    {
        Assert.True(Result.Value.HardTargets > 1000, $"only {Result.Value.HardTargets} targets");
        Assert.True(Result.Value.FloiHandled > 0);
        Assert.Contains("SoundDescriptor.SoundFiles", Result.Value.WholeElements);
        Assert.Contains("Weather.CloudTextures", Result.Value.WholeElements);
    }
}

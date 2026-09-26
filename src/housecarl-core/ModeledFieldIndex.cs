using System.Collections.Concurrent;

namespace HousecarlCore;

/// <summary>Which catalog types model a field NAME — the generated schema read by field instead of by type, so a mistyped name and a real field this record type does not carry can be told apart. Lookups are ORDINAL.</summary>
public static class ModeledFieldIndex
{
    /// <summary>What the schema says about a name that would not resolve on the owner type; <see cref="OnOwner"/> true is a defect to name, and <see cref="NearIsCaseSlip"/> a certainty rather than a guess.</summary>
    public readonly record struct Verdict(bool OnOwner, IReadOnlyList<string> ModeledOn, string? Near, bool NearIsCaseSlip);

    /// <summary>One slot, keyed by the corpus path it was built from and published as ONE immutable reference, so a reader takes both dictionaries and the path together without the gate.</summary>
    sealed record Snapshot(string Path, Dictionary<string, string[]> ByField, Dictionary<string, string[]> ByType);
    static volatile Snapshot? _cache;
    static readonly object Gate = new();

    /// <summary>Memoised per (corpus path, owner type, field name), so the nearest-name sweep does not run once per scanned record.</summary>
    static readonly ConcurrentDictionary<(string, string, string), Verdict> Verdicts = new();

    /// <summary>How many times each memo key's verdict has actually been computed.</summary>
    static readonly ConcurrentDictionary<(string, string, string), int> Computations = new();

    /// <summary>How many times the verdict for (corpus path, owner type, field name) has been computed.</summary>
    internal static int ComputationsOf(string corpusPath, string ownerTypeName, string fieldName) =>
        Computations.TryGetValue((corpusPath, ownerTypeName, fieldName), out var n) ? n : 0;

    /// <summary>What the schema knows about <paramref name="fieldName"/> not resolving on <paramref name="ownerTypeName"/>; null when the corpus is absent or the catalog does not carry the owner.</summary>
    public static Verdict? Diagnose(string ownerTypeName, string fieldName)
    {
        if (Index() is not { } idx) return null;
        // A read walk lands on plenty the catalog does not model, and a verdict about such an owner names a schema that does not exist.
        if (!idx.ByType.ContainsKey(ownerTypeName)) return null;
        // The path comes from the snapshot, never a second read of the settable global.
        var key = (idx.Path, ownerTypeName, fieldName);
        // The memo answers before anything is allocated — a scan dead-ends on every record it crosses.
        if (Verdicts.TryGetValue(key, out var memo)) return memo;
        return Verdicts.GetOrAdd(key, k =>
        {
            Computations.AddOrUpdate(k, 1, (_, n) => n + 1);
            var on = idx.ByField.TryGetValue(fieldName, out var types) ? types : Array.Empty<string>();
            bool onOwner = on.Contains(ownerTypeName, StringComparer.Ordinal);
            var others = onOwner
                ? on.Where(t => !string.Equals(t, ownerTypeName, StringComparison.Ordinal)).ToArray()
                : on;
            var (near, caseSlip) = onOwner ? default : NearestOn(idx.ByType, ownerTypeName, fieldName);
            return new Verdict(onOwner, others, near, caseSlip);
        });
    }

    /// <summary>The owner type's own field a miss most likely meant, and whether it is the CASE-only slip — checked first and answered exactly, because field names are case-sensitive.</summary>
    static (string? Near, bool CaseSlip) NearestOn(Dictionary<string, string[]> byType, string ownerTypeName, string fieldName)
    {
        if (!byType.TryGetValue(ownerTypeName, out var fields)) return default;
        foreach (var f in fields)
            if (string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase)) return (f, true);
        var near = PluginNameSuggest.Nearest(fieldName, fields, 1);
        return near.Count > 0 ? (near[0], false) : default;
    }

    static Snapshot? Index()
    {
        var path = CorpusRulebook.CorpusPath;
        // The built snapshot never changes, so the standing one answers without the gate; only a rebuild takes it.
        if (_cache is { } cur && cur.Path == path) return cur;
        lock (Gate)
        {
            if (_cache is { } c && c.Path == path) return c;
            Corpus corpus;
            try { corpus = CorpusRulebook.LoadCorpus(path); }
            catch { return null; }   // corpus not built / unparseable — say nothing rather than guess
            var byField = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var byType = new Dictionary<string, string[]>(StringComparer.Ordinal);
            // corpus.Types is ordinal-sorted, so both lists come out sorted without a sort here.
            foreach (var (typeName, schema) in corpus.Types)
            {
                byType[typeName] = schema.Fields.Select(f => f.Name).ToArray();
                foreach (var f in schema.Fields)
                {
                    if (!byField.TryGetValue(f.Name, out var owners)) byField[f.Name] = owners = new List<string>();
                    if (owners.Count == 0 || owners[^1] != typeName) owners.Add(typeName);
                }
            }
            var built = byField.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
            var snap = new Snapshot(path, built, byType);
            _cache = snap;
            Verdicts.Clear();   // verdicts carry their corpus in the key; dropping the old one's keeps the memo bounded
            return snap;
        }
    }
}

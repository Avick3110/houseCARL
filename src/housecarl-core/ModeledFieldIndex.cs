using System.Collections.Concurrent;

namespace HousecarlCore;

/// <summary>
/// Which catalog types model a field NAME — the generated schema read the other way round, by field instead of
/// by type.
///
/// A path that dead-ends on a record has two causes that need opposite next moves, and the dead-end itself
/// cannot tell them apart: the name is MISTYPED (fix the spelling), or the name is a real Mutagen field that
/// this record type does not carry (Mutagen models <c>VirtualMachineAdapter</c> on some record types and not
/// others, so a scripted ALCH is invisible and there is nothing to fix in the path). Only the schema can
/// separate them, and it separates them by construction — the set of names here is the set Mutagen models,
/// never a hand list.
///
/// Lookups are ORDINAL, matching <c>WriteEngine.ResolveProperty</c>'s own case-sensitive resolve, so the index
/// and the resolver cannot disagree about what counts as a field.
/// </summary>
public static class ModeledFieldIndex
{
    /// <summary>What the schema says about a name that would not resolve on <c>OwnerType</c>.
    /// <para><see cref="OnOwner"/> true means the catalog DOES list the field on the owner type — the walk and the
    /// schema disagree, which is a defect to name rather than a diagnosis to give.</para>
    /// <para><see cref="NearIsCaseSlip"/> true means <see cref="Near"/> is the owner's own spelling of the very
    /// name asked for, differing only in case — a certainty about the owner, not the nearest-name guess.</para></summary>
    public readonly record struct Verdict(bool OnOwner, IReadOnlyList<string> ModeledOn, string? Near, bool NearIsCaseSlip);

    /// <summary>One slot, keyed by the corpus path it was built from. <c>CorpusRulebook.CorpusPath</c> is a
    /// process-global the probes and test worlds repoint at their own generated corpus, so a flat cache would
    /// answer from the previous world's schema; a slot per path grows without bound across a probe run. One
    /// slot, rebuilt when the path changes, is both correct and bounded.
    /// <para>Published as ONE immutable reference so a reader takes the two dictionaries and the path they were
    /// built from together, and reads them without the gate — the build is the only thing that has to serialise.</para></summary>
    sealed record Snapshot(string Path, Dictionary<string, string[]> ByField, Dictionary<string, string[]> ByType);
    static volatile Snapshot? _cache;
    static readonly object Gate = new();

    /// <summary>Memoised per (corpus path, owner type, field name): a scan hits the same dead-end once per scanned
    /// record and only the first note is kept, so the nearest-name sweep must not run per record.</summary>
    static readonly ConcurrentDictionary<(string, string, string), Verdict> Verdicts = new();

    /// <summary>How many verdicts have actually been computed — the memo's own counter, so a test can say a scan
    /// over many records diagnoses the dead-end once.</summary>
    internal static int VerdictComputations;

    /// <summary>What the schema knows about <paramref name="fieldName"/> not resolving on
    /// <paramref name="ownerTypeName"/> (a catalog name — <c>Ingestible</c>, not <c>IIngestibleGetter</c>).
    /// Null when the corpus is not built or will not parse, and null for an owner type the catalog does not carry:
    /// the caller then says only what it knows, never a guessed verdict.</summary>
    public static Verdict? Diagnose(string ownerTypeName, string fieldName)
    {
        if (Index() is not { } idx) return null;
        // A read walk lands on plenty the catalog does not model — a FormLink, a string. There is no schema there
        // to weigh the name against, so any verdict about such an owner names a schema that does not exist.
        if (!idx.ByType.ContainsKey(ownerTypeName)) return null;
        // The path comes from the snapshot, never a second read of the settable global: a repoint between the two
        // reads would file a verdict under a corpus it was not computed from.
        var key = (idx.Path, ownerTypeName, fieldName);
        // The memo answers before anything is allocated — a scan dead-ends on every record it crosses.
        if (Verdicts.TryGetValue(key, out var memo)) return memo;
        return Verdicts.GetOrAdd(key, _ =>
        {
            Interlocked.Increment(ref VerdictComputations);
            var on = idx.ByField.TryGetValue(fieldName, out var types) ? types : Array.Empty<string>();
            bool onOwner = on.Contains(ownerTypeName, StringComparer.Ordinal);
            var others = onOwner
                ? on.Where(t => !string.Equals(t, ownerTypeName, StringComparison.Ordinal)).ToArray()
                : on;
            var (near, caseSlip) = onOwner ? default : NearestOn(idx.ByType, ownerTypeName, fieldName);
            return new Verdict(onOwner, others, near, caseSlip);
        });
    }

    /// <summary>The owner type's own field that a miss most likely meant, and whether it is the CASE-only slip.
    /// That case is checked first and answered exactly: field names are case-sensitive, and
    /// <see cref="PluginNameSuggest.Nearest"/> is case-insensitive, so it declines the very match that explains
    /// the miss.</summary>
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

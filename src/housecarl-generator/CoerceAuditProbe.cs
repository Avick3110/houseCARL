using HousecarlCore;
using static HousecarlCore.WriteEngine;

namespace HousecarlGenerator;

/// <summary>The <c>coerce-audit</c> probe: every writable leaf in the corpus coerces, or sits in a named deferred bucket.</summary>
public static class CoerceAuditProbe
{
    // ---- COERCE-AUDIT ----
    //  Walks every WRITABLE leaf in corpus.json, resolves the CLR type a Set/Add must coerce to, and asserts
    //  CanCoerce holds. Any uncoercible type is the exact gap to add to TryValueType. Reports, never skips.
    [CiProbe("coerce-audit")]
    public static int RunCoerceAudit(string[] args)
    {
        var corpusPath = args.Length > 0 ? args[0] : CorpusRulebook.CorpusPath;
        Corpus corpus;
        try { corpus = CorpusRulebook.LoadCorpus(corpusPath); }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }

        var uncoercible = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var typeErased = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var ownedRecord = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var unresolved = new SortedDictionary<string, (int count, List<string> examples)>(StringComparer.Ordinal);
        var substructWhole = new SortedDictionary<string, bool>(StringComparer.Ordinal); // type -> coercible-as-whole
        int hardTargets = 0, navOrBuild = 0, floiHandled = 0;

        static void Bump(SortedDictionary<string, (int, List<string>)> bag, string key, string example)
        {
            var e = bag.TryGetValue(key, out var v) ? v : (0, new List<string>());
            e.Item1++;
            if (e.Item2.Count < 4) e.Item2.Add(example);
            bag[key] = e;
        }

        foreach (var ts in corpus.Types.Values)
        foreach (var f in ts.Fields)
        {
            if (!f.Writable) continue;
            if (f.IsIdentity) continue; // record identity (FormKey/ModKey) -> flat-reject upstream, never coerced
            var site = $"{ts.Name}({ts.Kind}).{f.Name}";

            string? aq;
            switch (f.Cardinality)
            {
                case "scalar":
                case "enum":
                case "value":
                case "formlink":
                    aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                    break;
                case "list":
                case "dict":
                    // Scalar/enum/formlink elements are coercion targets and struct/arm/record elements are
                    // build-cases — EXCEPT a WHOLE-COERCIBLE element, routed to the same CanCoerce path by the same
                    // predicate the rulebook uses, through ResolveType, so a shape that stops resolving goes red.
                    if (f.ElementTypeRef is null && f.ElementTypeAssemblyQualified is { } eaq) aq = eaq;
                    else if (IsWholeCoercibleElement(f.ElementTypeRef, f.ElementTypeAssemblyQualified)
                             && f.ElementTypeAssemblyQualified is { } weaq) aq = weaq;
                    else { navOrBuild++; continue; }
                    break;
                case "substruct":
                {
                    // navigate-into; record whole-coercibility (the TranslatedString-style case) but don't gate on it.
                    var saq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
                    if (ResolveType(saq) is { } sst) substructWhole[sst.FullName ?? saq] = CanCoerce(sst);
                    navOrBuild++;
                    continue;
                }
                default: // polymorphic, etc. -> arm-build, not scalar coercion
                    navOrBuild++;
                    continue;
            }

            if (string.IsNullOrEmpty(aq)) { navOrBuild++; continue; }
            hardTargets++;
            var rt = ResolveType(aq);
            if (rt is null) { Bump(unresolved, aq, site); continue; }
            // FormLinkOrIndex condition targets are writable through the parent-aware SetFloi branch; counted below.
            if (IsFormLinkOrIndex(rt)) { floiHandled++; continue; }
            if (!CanCoerce(rt))
            {
                var u = Nullable.GetUnderlyingType(rt) ?? rt;
                var ex = $"{site} [{f.Cardinality}]";
                // Partition by principle, not a hand-list; each deferred bucket names its own trigger in the report.
                if (u == typeof(object)) Bump(typeErased, u.FullName ?? u.Name, ex);
                else if (corpus.Types.TryGetValue(u.Name, out var ut) && ut.Kind == "record") Bump(ownedRecord, u.FullName ?? u.Name, ex);
                else Bump(uncoercible, u.FullName ?? u.Name, ex);
            }
        }

        Console.WriteLine($"=== coerce-audit over {corpus.TotalTypes} types ===");
        Console.WriteLine($"Hard coercion targets (writable scalar/enum/value/formlink leaves + scalar list/dict elements): {hardTargets}");
        Console.WriteLine($"Navigate/build leaves skipped (substruct/polymorphic/struct-element): {navOrBuild}");
        Console.WriteLine();

        if (unresolved.Count > 0)
        {
            Console.WriteLine($"!! {unresolved.Count} assembly-qualified type name(s) FAILED to resolve (corpus/runtime mismatch):");
            foreach (var (k, v) in unresolved)
                Console.WriteLine($"   {v.count,5}x  {k}\n            e.g. {string.Join(", ", v.examples)}");
            Console.WriteLine();
        }

        void Dump(string header, SortedDictionary<string, (int count, List<string> examples)> bag)
        {
            var sites = bag.Values.Sum(v => v.count);
            Console.WriteLine($"{header}: {bag.Count} distinct, {sites} field-site(s)" + (bag.Count == 0 ? "." : ":"));
            foreach (var (k, v) in bag.OrderByDescending(kv => kv.Value.count))
                Console.WriteLine($"   {v.count,5}x  {k}\n            e.g. {string.Join("; ", v.examples)}");
        }

        if (uncoercible.Count == 0)
            Console.WriteLine("UNCOERCIBLE value types (real gaps): none — every coercible writable value-leaf is covered.");
        else
            Dump("UNCOERCIBLE value types (REAL GAPS — extend TryValueType)", uncoercible);
        Console.WriteLine();

        // Expected non-coercible-from-string — an honest loud reject, not a gap. Each names its wire-when trigger.
        Dump("DEFERRED — type-erased `object` condition params. WIRE-WHEN: a typed-value wire format exists (the value " +
             "carries its own type), i.e. the step-8 MCP API", typeErased);
        Console.WriteLine();
        Console.WriteLine($"HANDLED (wave 4) — FormLinkOrIndex condition targets, via the parent-aware SetFloi branch " +
                          $"(auto-infers form-vs-index from the value): {floiHandled} site(s) — was the deferred bucket.");
        Console.WriteLine();
        Dump("DEFERRED — owned-child records (whole-record assignment, not a string). WIRE-WHEN: record creation/composition lands", ownedRecord);
        Console.WriteLine();

        var coercibleSubstructs = substructWhole.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
        Console.WriteLine("Substruct types coercible-as-whole (informational — e.g. TranslatedString): " +
                          (coercibleSubstructs.Count == 0 ? "none" : string.Join(", ", coercibleSubstructs)));
        Console.WriteLine();

        var honestReject = typeErased.Values.Sum(v => v.count) + ownedRecord.Values.Sum(v => v.count);
        var pass = uncoercible.Count == 0 && unresolved.Count == 0;
        Console.WriteLine(pass
            ? $"=== PASS: coercion surface complete by construction ({honestReject} site(s) honestly non-coercible, reported above) ==="
            : "=== INCOMPLETE: extend TryValueType with the REAL GAPS above, then re-run ===");
        return pass ? 0 : 1;
    }
}

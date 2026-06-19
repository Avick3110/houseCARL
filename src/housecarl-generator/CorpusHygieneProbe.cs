using System.Reflection;
using HousecarlCore;
using Mutagen.Bethesda.Skyrim;   // IArmorGetter — public assembly anchor (resolve internal projection types by name)

namespace HousecarlGenerator;

/// <summary>
/// REGRESSION GUARD (standing CI instrument) — CORPUS STRUCTURAL HYGIENE.
///
/// The corpus DISPLAY (generated/corpus.json + the shipped mutagen-reference jsonl shards, both from ONE
/// CorpusGenerator walk) is a by-construction, load-bearing artifact: it is what Claude reads to author
/// every write, and what the write GATE (CorpusRulebook) consumes. A recurring class of defect is the
/// reflection walk emitting something into DISPLAY that is NOT actually authorable — a base listed as its
/// own arm, a C# indexer surfaced as a field, an infrastructure (System.*) type cataloged as a modeled
/// type, a read-only overlay/projection presented as a legal union arm, or a degenerate field-less struct.
/// Each such defect is individually low-stakes (the GATE/APPLY refuse it anyway), but the DISPLAY is wrong
/// — a Q3-adjacent silently-wrong ANSWER in a shipped reference. Every instance to date was found by ad-hoc
/// human eye, never by an automated check; this guard converts "found by eye" into "caught by construction"
/// and locks it against a future Mutagen-bump re-leak.
///
/// Self-contained, in the pattern of the corpus-based guards (vmad-poly / sameshape-agree / nullarm):
/// regenerates the corpus into a TEMP dir (no game files, runs on CI) and asserts five shape invariants,
/// each one a contract a past defect violated. Every invariant is RED-PROVEN in-code: a GREEN arm asserts
/// zero violations over the freshly-regenerated REAL corpus, and a paired RED arm feeds the SAME checker a
/// synthetic violating corpus (or, for the live-reflection invariants, a real type known to exhibit the
/// shape) and asserts it FIRES and names the violator — so a GREEN can never mean "the scenario didn't
/// arise", only "the contract holds".
///
///   INV1 — NO SELF-LISTING ARM. No polymorphic-base lists its own catalog name among its arms, at all three
///          emission points (type-level Arms, field-level Arms, element-level ElementArms). Locks PR #84's
///          EmittedArmNames strip + the G8 runtime gate's static twin. (RED: a base injected into its arms.)
///   INV2 — NO INDEXER-NAMED LEAK. No emitted field corresponds to a C# indexer (GetIndexParameters != 0) on
///          the type's live getter — the "Weather Item" leak. Locks CorpusGenerator's CollectAllProperties
///          indexer skip. (RED: a real indexer-bearing type whose synthetic field-set re-includes the indexer.)
///   INV3 — NO NON-MODELED TYPE. Every cataloged type's getter is under a known modeled root (Mutagen.Bethesda
///          or Noggog) — never System.* / BCL infra. Locks the phantom-Enumerable removal; fails LOUD if a
///          NEW namespace ever enters the catalog (a coverage surface to surface, not absorb). (RED: a
///          System.Linq.Enumerable getter.)
///   INV4 — NO READ-ONLY PROJECTION ARM. No type with no mutable interface is presented as an arm — the true
///          structural twin of CorpusGenerator's IsAuthorableArm filter (Unit A): flagged when it is kind "arm"
///          OR its live getter is a concrete class (Mutagen's multi-mod overlay, the merged-cell view, the lazy
///          BinaryOverlay twins). Does NOT trip a 0-field MARKER arm (which keeps its mutable interface) nor a
///          read-only value struct (AssetType / ReadOnlyArray2d — interface getter, kind "struct"). (RED: a real
///          overlay's AQ as a no-mutable arm, AND an interface-getter no-mutable arm.)
///   INV5 — NO DEGENERATE TYPE. No struct / record / header has zero fields (a poly-base or arm legitimately
///          can — its data lives in arms / it is a pure marker). Catches a content type that lost all its
///          fields to a reflection miss. (RED: a synthetic zero-field struct.)
///
/// Run: dotnet run --project src/housecarl-generator -- corpus-hygiene-guard
/// </summary>
public static class CorpusHygieneProbe
{
    static int _pass, _fail;

    static readonly string[] ModeledRoots = { "Mutagen.Bethesda", "Noggog" };
    static readonly string[] MustHaveFieldsKinds = { "struct", "record", "header" };

    public static int RunGuard(string[] args)
    {
        Console.WriteLine("################  REGRESSION GUARD — corpus structural hygiene  ################");
        Console.WriteLine();

        var tmpDir = Path.Combine(Path.GetTempPath(), "hc-corpus-hygiene-guard");
        if (Directory.Exists(tmpDir)) { try { Directory.Delete(tmpDir, recursive: true); } catch { } }
        Directory.CreateDirectory(tmpDir);
        try
        {
            var genDir = Path.Combine(tmpDir, "gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(tmpDir, "ref"));
            var corpus = CorpusRulebook.LoadCorpus(Path.Combine(genDir, "corpus.json"));
            Console.WriteLine($"   regenerated corpus: {corpus.TotalTypes} types  [{string.Join(", ", corpus.KindCounts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}]");
            Console.WriteLine();

            // ---------- INV1 — no self-listing arm ----------
            {
                var real = CheckSelfListingArm(corpus);
                Check("INV1-GREEN no self-listing arm (real corpus)", real.Count == 0, real);

                var synth = NewCorpus(
                    Poly("Faker", arms: new() { "FakerArmA", "Faker" }),   // base lists ITSELF
                    Arm("FakerArmA", "Faker"));
                var hit = CheckSelfListingArm(synth);
                Check("INV1-RED   a poly-base injected into its own arms is caught + named",
                    hit.Any(s => s.Contains("Faker") && s.Contains("itself")), hit);

                // ungated: a self-listing Arms list under ANY Kind (a generator that decoupled Kind from Arms) is caught
                var synth2 = NewCorpus(new TypeSchema { Name = "MistaggedBase", Kind = "struct",
                    GetterInterface = "Mutagen.Bethesda.Skyrim.IMistaggedBaseGetter", Arms = new() { "MistaggedBase", "RealArm" } });
                var hit2 = CheckSelfListingArm(synth2);
                Check("INV1-RED2  a self-listing Arms list under a NON-poly-base Kind is caught + named",
                    hit2.Any(s => s.Contains("MistaggedBase") && s.Contains("itself")), hit2);
            }

            // ---------- INV2 — no indexer-named leak ----------
            {
                var real = CheckIndexerLeak(corpus);
                Check("INV2-GREEN no emitted field is a C# indexer (real corpus)", real.Count == 0, real);

                // RED: find a REAL indexer-bearing type in the catalog (the Weather timed structs), then build a
                // synthetic schema for it whose field-set re-includes the indexer name — the checker must catch it.
                var bearer = FindIndexerBearingType(corpus, out var idxName);
                if (bearer == null)
                {
                    Check("INV2-RED   (an indexer-bearing type exists to prove against)", false,
                        new List<string> { "no live indexer-bearing type found in the catalog — the indexer skip cannot be RED-proven; investigate (the Weather timed structs should qualify)" });
                }
                else
                {
                    var synth = NewCorpus(StructWithFields(bearer.Name, bearer.GetterInterfaceAssemblyQualified, idxName!));
                    var hit = CheckIndexerLeak(synth);
                    Check($"INV2-RED   a leaked indexer field ('{idxName}' on live indexer-type '{bearer.Name}') is caught + named",
                        hit.Any(s => s.Contains(bearer.Name) && s.Contains(idxName!)), hit);
                }
            }

            // ---------- INV3 — no non-modeled (infra) type ----------
            {
                var real = CheckNonModeledType(corpus);
                Check("INV3-GREEN every getter is under a modeled root Mutagen.Bethesda/Noggog (real corpus)", real.Count == 0, real);

                var synth = NewCorpus(new TypeSchema { Name = "Enumerable", Kind = "struct",
                    GetterInterface = "System.Linq.Enumerable", Fields = { new FieldSchema { Name = "X", Type = "int", Cardinality = "scalar", Writable = true } } });
                var hit = CheckNonModeledType(synth);
                Check("INV3-RED   a System.* (phantom-Enumerable) getter is caught + named",
                    hit.Any(s => s.Contains("Enumerable") && s.Contains("System.Linq")), hit);
            }

            // ---------- INV4 — no read-only projection arm ----------
            {
                var real = CheckReadOnlyProjection(corpus);
                Check("INV4-GREEN no read-only projection in the catalog (real corpus)", real.Count == 0, real);

                // RED: reconstruct the exact pre-Unit-A leak — a real overlay type's AQ in a no-mutable arm entry.
                // SkyrimMultiModOverlay is internal in Mutagen, so resolve it by name off the assembly (reflection
                // bypasses accessibility) rather than referencing the type at compile time.
                var overlay = typeof(IArmorGetter).Assembly.GetType("Mutagen.Bethesda.Skyrim.SkyrimMultiModOverlay");
                if (overlay == null)
                {
                    Check("INV4-RED   (a real read-only projection type exists to prove against)", false,
                        new List<string> { "could not resolve Mutagen.Bethesda.Skyrim.SkyrimMultiModOverlay — INV4 cannot be RED-proven; investigate" });
                }
                else
                {
                    var synth = NewCorpus(new TypeSchema { Name = "SkyrimMultiModOverlay", Kind = "arm",
                        GetterInterface = overlay.FullName!, GetterInterfaceAssemblyQualified = overlay.AssemblyQualifiedName!,
                        MutableInterface = null, AbstractBase = "SkyrimMod" });
                    var hit = CheckReadOnlyProjection(synth);
                    Check("INV4-RED   a read-only projection (concrete getter + no mutable) is caught + named",
                        hit.Any(s => s.Contains("SkyrimMultiModOverlay") && s.Contains("projection")), hit);
                }
                // RED2: an arm with an INTERFACE getter but no mutable twin — the broadened clause (an arm must be
                // authorable) catches it even though its getter is not a concrete class.
                var ifaceArm = NewCorpus(new TypeSchema { Name = "GhostArm", Kind = "arm",
                    GetterInterface = typeof(IArmorGetter).FullName!, GetterInterfaceAssemblyQualified = typeof(IArmorGetter).AssemblyQualifiedName!,
                    MutableInterface = null, AbstractBase = "Ghost" });
                var hitIface = CheckReadOnlyProjection(ifaceArm);
                Check("INV4-RED2  a no-mutable ARM with an interface getter is caught + named",
                    hitIface.Any(s => s.Contains("GhostArm") && s.Contains("projection")), hitIface);
            }

            // ---------- INV5 — no degenerate (field-less) struct/record/header ----------
            {
                var real = CheckDegenerateType(corpus);
                Check("INV5-GREEN no field-less struct/record/header (real corpus)", real.Count == 0, real);

                var synth = NewCorpus(new TypeSchema { Name = "Hollow", Kind = "struct",
                    GetterInterface = "Mutagen.Bethesda.Skyrim.IHollowGetter" });   // zero fields
                var hit = CheckDegenerateType(synth);
                Check("INV5-RED   a field-less struct is caught + named",
                    hit.Any(s => s.Contains("Hollow") && s.Contains("zero fields")), hit);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   FAIL (unexpected): {ex.GetType().Name}: {ex.Message}");
            _fail++;
        }
        finally { try { Directory.Delete(tmpDir, recursive: true); } catch { } }

        Console.WriteLine();
        Console.WriteLine($"=== corpus-hygiene-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>One arm: print PASS/FAIL, tally, and on FAIL dump the (un)expected violations so a red CI log
    /// is self-diagnosing (Q3 — no silent failure). For a GREEN arm <paramref name="detail"/> is the unexpected
    /// violations found; for a RED arm it is what the checker caught (printed only if the catch was wrong/empty).</summary>
    static void Check(string label, bool ok, List<string> detail)
    {
        Console.WriteLine($"   {label,-70}: {(ok ? "PASS" : "FAIL")}");
        if (!ok)
        {
            if (detail.Count == 0) Console.WriteLine("        - (the checker reported NO violations)");
            foreach (var d in detail.Take(12)) Console.WriteLine($"        - {d}");
            if (detail.Count > 12) Console.WriteLine($"        - … +{detail.Count - 12} more");
        }
        if (ok) _pass++; else _fail++;
    }

    // ============================================================ invariant checkers
    // Each returns the (possibly empty) list of violations. A checker is pure over the Corpus model
    // (plus, for INV2/INV4, live reflection off the emitted assembly-qualified getter name).

    /// <summary>INV1: a base must never list its own catalog name among its arms — at the type level (Arms),
    /// the field level (Arms, base = TypeRef) or the element level (ElementArms, base = ElementTypeRef). The
    /// type-level check is kind-INDEPENDENT (not gated on polymorphic-base): a self-listing Arms list on a type
    /// emitted under any other Kind is itself a defect — it would mean the generator decoupled Kind from Arms.</summary>
    static List<string> CheckSelfListingArm(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Arms is { } a && a.Contains(t.Name))
                v.Add($"type '{t.Name}' (kind {t.Kind}) lists itself among its arms ({string.Join(",", a)})");
            foreach (var f in t.Fields)
            {
                if (f.Arms is { } fa && f.TypeRef is { } tr && fa.Contains(tr))
                    v.Add($"{t.Name}.{f.Name} field-arms list the base '{tr}' itself");
                if (f.ElementArms is { } ea && f.ElementTypeRef is { } er && ea.Contains(er))
                    v.Add($"{t.Name}.{f.Name} element-arms list the base '{er}' itself");
            }
        }
        return v;
    }

    /// <summary>INV2: no emitted field name corresponds to a C# indexer (GetIndexParameters != 0) on the
    /// type's live getter interface (walked the same way CorpusGenerator.CollectAllProperties walks it).</summary>
    static List<string> CheckIndexerLeak(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Kind == "enum") continue;
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            if (getter == null) continue;   // unresolvable getter is INV3/INV4's concern, not INV2's
            var (indexers, named) = LiveProps(getter);
            // An indexer-ONLY name (an indexer that is NOT also a legitimate named property somewhere in the
            // interface set). The generator skips indexers but KEEPS named properties, so an emitted field whose
            // name also exists as a named property is legitimate; only an indexer-only name surfacing as a field
            // means the skip regressed. This mirrors CollectAllProperties' per-PropertyInfo structural skip, so the
            // probe can't false-positive on a type that has both a named 'Item' field and an 'Item' indexer.
            var leak = indexers.Where(n => !named.Contains(n)).ToHashSet(StringComparer.Ordinal);
            if (leak.Count == 0) continue;
            var fields = t.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var n in leak)
                if (fields.Contains(n))
                    v.Add($"{t.Name} emits field '{n}', a C# indexer on {getter.Name} (GetIndexParameters != 0) — the indexer skip regressed");
        }
        return v;
    }

    /// <summary>INV3: every cataloged type's getter lives under a known modeled root (Mutagen.Bethesda or
    /// Noggog). System.* / other infra namespaces are a leak (phantom Enumerable) OR a new coverage surface
    /// to surface — either way, fail loud.</summary>
    static List<string> CheckNonModeledType(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            var g = t.GetterInterface ?? "";
            if (!ModeledRoots.Any(r => g.StartsWith(r + ".", StringComparison.Ordinal)))
                v.Add($"type '{t.Name}' getter '{g}' is outside the modeled roots ({string.Join("/", ModeledRoots)})");
        }
        return v;
    }

    /// <summary>INV4: an unauthorable read-only PROJECTION must not appear in the catalog as an arm. The true
    /// structural twin of CorpusGenerator's IsAuthorableArm filter (Unit A, which drops a union implementer with
    /// no mutable interface): flag any type whose MutableInterface == null that is EITHER kind "arm" (an arm must
    /// be authorable) OR whose live getter is a concrete class (the overlay shape — SkyrimMultiModOverlay,
    /// MergedCellBlock, the lazy BinaryOverlay twins). Keys on the mutable interface, NEVER on writable==0 (a
    /// marker arm like PackageTargetSelf is 0-writable but DOES expose a mutable interface, so it is not tripped).
    /// A legitimate read-only VALUE struct (AssetType, ReadOnlyArray2d) is kind "struct" with an INTERFACE getter,
    /// so neither clause fires.</summary>
    static List<string> CheckReadOnlyProjection(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
        {
            if (t.Kind == "enum") continue;
            if (t.MutableInterface != null) continue;        // authorable (has a setter twin) -> fine
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            // If the getter AQ fails to resolve, concreteGetter stays false — but a re-leaked projection is always
            // an arm, so the kind clause still catches it; getter resolution only matters for the rarer case of a
            // concrete-getter projection emitted under a non-"arm" kind.
            bool concreteGetter = getter != null && !getter.IsInterface;
            if (t.Kind == "arm" || concreteGetter)
                v.Add($"type '{t.Name}' (kind {t.Kind}) is an unauthorable read-only projection: " +
                      $"{(concreteGetter ? $"concrete-class getter {getter!.Name}" : "getter interface only")} + no mutable interface — not authorable, must not be a catalog arm");
        }
        return v;
    }

    /// <summary>INV5: a struct / record / header with zero fields is degenerate (a content type that lost
    /// its fields to a reflection miss). A poly-base (data lives in arms) or arm (marker) may be field-less.</summary>
    static List<string> CheckDegenerateType(Corpus c)
    {
        var v = new List<string>();
        foreach (var t in c.Types.Values)
            if (MustHaveFieldsKinds.Contains(t.Kind) && t.Fields.Count == 0)
                v.Add($"type '{t.Name}' (kind {t.Kind}) has zero fields — degenerate (possible lost setter / reflection miss)");
        return v;
    }

    // ============================================================ live-reflection helpers

    static Type? ResolveType(string? aq)
    {
        if (string.IsNullOrEmpty(aq)) return null;
        try { return Type.GetType(aq); } catch { return null; }
    }

    /// <summary>BFS the getter + its (transitive) interfaces, partitioning property names into INDEXER names
    /// (GetIndexParameters != 0) and NAMED-property names — the exact walk CorpusGenerator.CollectAllProperties
    /// does (same BindingFlags + DeclaredOnly + interface-queue), but keeping both halves so INV2 can subtract
    /// the named set from the indexer set (an 'Item' that is BOTH an indexer and a named property is legitimate).</summary>
    static (HashSet<string> indexers, HashSet<string> named) LiveProps(Type getter)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        var indexers = new HashSet<string>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        queue.Enqueue(getter);
        foreach (var i in getter.GetInterfaces()) queue.Enqueue(i);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!seen.Add(cur)) continue;
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                (p.GetIndexParameters().Length != 0 ? indexers : named).Add(p.Name);
            foreach (var i in cur.GetInterfaces()) queue.Enqueue(i);
        }
        return (indexers, named);
    }

    /// <summary>Find a cataloged type whose live getter exposes an indexer-ONLY name (the GenderedItem timed/
    /// gendered structs) — used to RED-prove INV2 against a REAL indexer the generator must skip, not a
    /// fabricated one. Returns the first such type + its indexer name, or null if none exists (which would make
    /// INV2 vacuous and is itself reported as a FAIL).</summary>
    static TypeSchema? FindIndexerBearingType(Corpus c, out string? idxName)
    {
        foreach (var t in c.Types.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (t.Kind == "enum") continue;
            var getter = ResolveType(t.GetterInterfaceAssemblyQualified);
            if (getter == null) continue;
            var (indexers, named) = LiveProps(getter);
            var leak = indexers.Where(n => !named.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (leak.Count > 0) { idxName = leak[0]; return t; }
        }
        idxName = null;
        return null;
    }

    // ============================================================ synthetic-corpus builders (RED arms)

    static Corpus NewCorpus(params TypeSchema[] types)
    {
        var c = new Corpus();
        foreach (var t in types) c.Types[t.Name] = t;
        return c;
    }

    static TypeSchema Poly(string name, List<string> arms) => new()
    {
        Name = name, Kind = "polymorphic-base",
        GetterInterface = $"Mutagen.Bethesda.Skyrim.I{name}Getter", Arms = arms,
    };

    static TypeSchema Arm(string name, string @base) => new()
    {
        Name = name, Kind = "arm", AbstractBase = @base,
        GetterInterface = $"Mutagen.Bethesda.Skyrim.I{name}Getter",
    };

    static TypeSchema StructWithFields(string name, string getterAq, params string[] fieldNames)
    {
        var t = new TypeSchema { Name = name, Kind = "struct",
            GetterInterface = name, GetterInterfaceAssemblyQualified = getterAq };
        foreach (var fn in fieldNames)
            t.Fields.Add(new FieldSchema { Name = fn, Type = "float", Cardinality = "scalar", Writable = true });
        return t;
    }
}

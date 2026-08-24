using System.Globalization;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>
/// CI REGRESSION GUARD for <see cref="WriteVerbs"/> — the one derivation of "which verbs work on this collection
/// shape", and the six emitted messages that consume it.
///
/// <para><b>The claim under test.</b> <see cref="WriteVerbs.On"/> is a SHAPE-indexed table; the gate
/// (<see cref="CorpusRulebook.Validate"/>) is a VERB-indexed switch. They must agree, and a guard that re-derived
/// the table's own logic to check it would prove only that the copy was faithful — the check-A tautology. So this
/// guard measures instead: it buckets every collection field in the corpus by shape, and for each populated bucket
/// replays, through the REAL gate, a well-formed request for every verb the table NAMES (each must be ACCEPTED) and
/// for every verb it OMITS (each must be REFUSED). Both directions, so the arm can fail either way — a table that
/// over-claims goes red on the accept sweep, one that under-claims on the refuse sweep.</para>
///
/// <para><b>Arms.</b>
/// <list type="bullet">
///   <item>SHAPE-POPULATION — every shape the table can describe, with how many corpus fields carry it, plus the
///         two DECLINED element kinds. A shape with zero instances is reported, never silently skipped: the accept/
///         refuse sweep over an empty bucket proves nothing, and this is the arm that says so out loud.</item>
///   <item>SHAPE-ROUTES — the schema route (<see cref="WriteVerbs.OfField"/>) and the runtime route
///         (<see cref="WriteVerbs.OfRuntimeType"/>) must return the SAME shape for every collection field. The
///         engine cannot see the corpus, so a second route is structural; this is what stops it becoming a second
///         opinion.</item>
///   <item>VERB-ACCEPTED / VERB-REFUSED — the measured agreement, per field, both directions.</item>
///   <item>The per-site message arms — one per cardinality per consuming site, asserting that the emitted text
///         carries this shape's verbs and NOT the other cardinality's.</item>
/// </list></para>
///
/// Run: <c>dotnet run --project src/housecarl-generator remedy-verbs-guard</c>
/// </summary>
public static class RemedyVerbsGuardProbe
{
    static int _pass, _fail;
    static void Check(string label, bool ok, string? got = null)
    {
        Console.WriteLine($"   [{(ok ? "PASS" : "FAIL")}] {label}");
        if (!ok && got is not null) Console.WriteLine($"          got: {Trim(got)}");
        if (ok) _pass++; else _fail++;
    }
    static string Trim(string s) => s.Length <= 500 ? s.Replace("\n", " | ") : s[..500].Replace("\n", " | ") + " …";

    static CorpusRulebook Rules => _rules ??= CorpusRulebook.Load();
    static CorpusRulebook? _rules;
    static Corpus Corp => _corpus ??= CorpusRulebook.LoadCorpus();
    static Corpus? _corpus;

    public static int RunGuard(string[] args)
    {
        _pass = _fail = 0;
        Console.WriteLine("################  REGRESSION GUARD — derived remedy verbs (WriteVerbs)  ################");
        Console.WriteLine();

        foreach (var (name, arm) in new (string Name, Action Run)[]
                 { ("population", PopulationArm), ("routes", RoutesArm), ("agreement", AgreementArm),
                   ("sites", SitesArm) })
        {
            try { arm(); }
            catch (Exception ex)
            {
                Console.WriteLine($"   [FAIL] the {name} arm threw: {ex.GetType().Name}: {(ex.InnerException ?? ex).Message}");
                _fail++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== remedy-verbs-guard: {_pass} passed, {_fail} failed -> {(_fail == 0 ? "PASS" : "FAIL")} ===");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- the corpus sweep

    /// <summary>Every WRITABLE collection field declared directly on a RECORD-kind type, so the request path is a
    /// single hop and the replay exercises the gate rather than the navigator.</summary>
    static IEnumerable<(string Record, FieldSchema Field)> CollectionFields() =>
        from t in Corp.Types.Values
        where t.Kind == "record"
        from f in t.Fields
        where f.Writable && f.Cardinality is "list" or "dict"
        select (t.Name, f);

    static void PopulationArm()
    {
        Console.WriteLine("── POPULATION: which shapes the table can describe, and how many corpus fields carry each ──");
        var buckets = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int declined = 0;
        var declinedKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (rec, f) in CollectionFields())
        {
            if (WriteVerbs.OfField(f, Corp) is { } shape)
            {
                var k = $"{shape.Kind}/{shape.Element}";
                buckets[k] = buckets.GetValueOrDefault(k) + 1;
            }
            else
            {
                declined++;
                var k = SchemaClassifier.ClassifyElement(f, Corp).ToString();
                declinedKinds[k] = declinedKinds.GetValueOrDefault(k) + 1;
                Console.WriteLine($"        declined: {rec}.{f.Name} ({k})");
            }
        }
        foreach (var (k, n) in buckets) Console.WriteLine($"        {k}: {n} field(s)");
        Console.WriteLine($"        declined (no verb names printed): {declined} field(s) — {string.Join(", ", declinedKinds.Select(kv => $"{kv.Key}x{kv.Value}"))}");

        // The vacuity floor. A shape with no corpus field is a shape the agreement sweep below measures NOTHING
        // for, so which shapes those are is pinned by name rather than left to be discovered as a silent hole.
        // Dict/OwnedRecord is the one: Mutagen models no dictionary whose VALUES are owned child records
        // (measured 2026-08-24, not assumed). Its row in the table is therefore unexercised — if a Mutagen bump
        // ever lights it up, this arm goes red and the claim gets measured before it is trusted.
        var dormant = new SortedSet<string>(StringComparer.Ordinal) { "Dict/OwnedRecord" };
        var actuallyEmpty = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kind in new[] { CollectionKind.List, CollectionKind.Dict })
            foreach (var elem in new[] { ElementPlacement.Coerced, ElementPlacement.Composed, ElementPlacement.OwnedRecord })
                if (buckets.GetValueOrDefault($"{kind}/{elem}") == 0) actuallyEmpty.Add($"{kind}/{elem}");
        Check("SHAPE-POPULATION — exactly the pinned dormant shape (Dict/OwnedRecord) is uninstantiated; every other shape the table describes has corpus fields behind it, so the agreement sweep is non-vacuous",
            actuallyEmpty.SetEquals(dormant),
            $"empty=[{string.Join(",", actuallyEmpty)}] pinned=[{string.Join(",", dormant)}]");
    }

    static void RoutesArm()
    {
        Console.WriteLine();
        Console.WriteLine("── ROUTES: the schema route and the runtime route must answer identically on every field ──");
        int agreed = 0, unresolved = 0;
        var disagreements = new List<string>();
        foreach (var (rec, f) in CollectionFields())
        {
            var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
            if (WriteEngine.ResolveType(aq) is not { } rt) { unresolved++; continue; }
            var bySchema = WriteVerbs.OfField(f, Corp);
            var byRuntime = WriteVerbs.OfRuntimeType(rt);
            if (Equals(bySchema, byRuntime)) agreed++;
            else disagreements.Add($"{rec}.{f.Name}: schema={Show(bySchema)} runtime={Show(byRuntime)}");
        }
        Console.WriteLine($"        agreed on {agreed} field(s); {unresolved} type(s) did not resolve; {disagreements.Count} disagreement(s)");
        foreach (var d in disagreements.Take(20)) Console.WriteLine($"        ! {d}");
        Check("SHAPE-ROUTES — schema-derived and runtime-derived shapes agree on every collection field in the corpus",
            disagreements.Count == 0 && agreed > 0, $"{disagreements.Count} disagreements over {agreed} agreements");
    }

    static string Show(CollectionShape? s) => s is { } v ? $"{v.Kind}/{v.Element}" : "(declined)";

    // ---------------------------------------------------------------- the measured agreement

    static void AgreementArm()
    {
        Console.WriteLine();
        Console.WriteLine("── AGREEMENT: every named verb ACCEPTED and every omitted verb REFUSED, replayed through the real gate ──");
        var acceptFails = new List<string>();
        var refuseFails = new List<string>();
        // Per SHAPE, not just in total: a whole shape whose every request failed to construct would otherwise
        // ride the other shapes' counts to green. The dropped requests are reported by (shape, verb) for the same
        // reason — a silent skip reads as coverage it is not.
        var accepts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var refuses = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (var (rec, f) in CollectionFields())
        {
            if (WriteVerbs.OfField(f, Corp) is not { } shape) continue;
            var sh = $"{shape.Kind}/{shape.Element}";
            var named = WriteVerbs.On(shape);
            foreach (var use in named)
            {
                if (BuildRequest(rec, f, use.Verb, use.Input, use.NeedsKey) is not { } req)
                { dropped[$"{sh} {use.Verb}"] = dropped.GetValueOrDefault($"{sh} {use.Verb}") + 1; continue; }
                var err = Rules.Validate(req);
                if (err is null) accepts[sh] = accepts.GetValueOrDefault(sh) + 1;
                else acceptFails.Add($"{rec}.{f.Name} [{sh}] NAMED {use.Verb} -> {Trim(err)}");
            }
            var omitted = WriteVerbs.All.Where(v => !named.Any(u => u.Verb == v));
            foreach (var verb in omitted)
            {
                // The omitted verb is replayed in its most-likely-to-be-accepted form: whatever slot the shape
                // would have handed the verb had the table named it. If the gate still refuses, the omission is
                // correct; if it accepts, the table under-claims and the caller is being told less than is true.
                var use = LikelyInput(shape, verb);
                if (BuildRequest(rec, f, verb, use.Input, use.NeedsKey) is not { } req)
                { dropped[$"{sh} {verb}"] = dropped.GetValueOrDefault($"{sh} {verb}") + 1; continue; }
                if (Rules.Validate(req) is not null) refuses[sh] = refuses.GetValueOrDefault(sh) + 1;
                else refuseFails.Add($"{rec}.{f.Name} [{sh}] OMITTED {verb} -> ACCEPTED");
            }
        }

        foreach (var sh in accepts.Keys.Concat(refuses.Keys).Distinct().OrderBy(s => s, StringComparer.Ordinal))
            Console.WriteLine($"        {sh}: {accepts.GetValueOrDefault(sh)} accept(s), {refuses.GetValueOrDefault(sh)} refusal(s)");
        Console.WriteLine(dropped.Count == 0
            ? "        not constructible: none — every (shape, verb) cell was replayed"
            : "        not constructible (no sample value for the element type): "
              + string.Join(", ", dropped.Select(kv => $"{kv.Key}x{kv.Value}"))
              + $" | element types with no sample: {string.Join(", ", _noSample)}");
        foreach (var d in acceptFails.Take(25)) Console.WriteLine($"        ! {d}");
        foreach (var d in refuseFails.Take(25)) Console.WriteLine($"        ! {d}");

        // Every POPULATED shape must have been measured in both directions — the per-shape counts are the arm's
        // subject, not a footnote under a global total.
        var populated = accepts.Keys.Concat(refuses.Keys).Concat(dropped.Keys.Select(k => k.Split(' ')[0]))
            .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();
        Check("VERB-ACCEPTED — every verb the table names for a shape is ACCEPTED by the gate, on every corpus field of that shape, with at least one measured accept per shape",
            acceptFails.Count == 0 && populated.All(s => accepts.GetValueOrDefault(s) > 0),
            $"{acceptFails.Count} over-claims; per-shape accepts: {string.Join(", ", populated.Select(s => $"{s}={accepts.GetValueOrDefault(s)}"))}");
        Check("VERB-REFUSED — every verb the table omits for a shape is REFUSED by the gate, on every corpus field of that shape, with at least one measured refusal per shape",
            refuseFails.Count == 0 && populated.All(s => refuses.GetValueOrDefault(s) > 0),
            $"{refuseFails.Count} under-claims; per-shape refusals: {string.Join(", ", populated.Select(s => $"{s}={refuses.GetValueOrDefault(s)}"))}");
    }

    /// <summary>The slot an omitted verb would take on this shape, so the refuse direction tests the verb rather
    /// than a malformed request.</summary>
    static (VerbInput Input, bool NeedsKey) LikelyInput(CollectionShape shape, string verb)
    {
        // An OWNED-RECORD element has no plain-value form either, so the form a caller actually sends when trying
        // to place one is a compose — which is the request the record-axis redirect is written to answer.
        bool composed = shape.Element is ElementPlacement.Composed or ElementPlacement.OwnedRecord;
        var one = composed ? VerbInput.Compose : VerbInput.Value;
        return verb switch
        {
            "Set" => (one, shape.Kind == CollectionKind.Dict),
            "Add" => (one, shape.Kind == CollectionKind.Dict),
            "Remove" => (VerbInput.None, true),
            "SetAtIndex" or "InsertAtIndex" => (one, true),
            "ReplaceAll" => shape.Kind == CollectionKind.List
                ? (composed ? VerbInput.Composes : VerbInput.Values, false)
                : (VerbInput.Entries, false),
            "Merge" => (VerbInput.Entries, false),
            _ => (VerbInput.None, false),
        };
    }

    // ---------------------------------------------------------------- the consuming sites

    /// <summary>The list-exclusive verbs — the ones a dict caller must never be offered.</summary>
    static readonly string[] ListOnly = { "SetAtIndex", "InsertAtIndex" };
    /// <summary>The dict-exclusive verb — the one a list caller must never be offered.</summary>
    static readonly string[] DictOnly = { "Merge" };

    /// <summary>An emitted message is right for a cardinality when it carries that shape's verbs AND carries none
    /// of the other cardinality's. Asserting only the first half is what let the class-A medium ship: the message
    /// DID name a working verb, alongside three that refuse.</summary>
    static void CheckShaped(string label, string? msg, CollectionShape shape, IEnumerable<VerbUse> expected)
    {
        var want = expected.Select(u => u.Verb).Distinct().ToArray();
        var forbidden = (shape.Kind == CollectionKind.List ? DictOnly : ListOnly)
            .Where(v => !want.Contains(v)).ToArray();
        var missing = msg is null ? want : want.Where(v => !msg.Contains(v, StringComparison.Ordinal)).ToArray();
        var leaked = msg is null ? Array.Empty<string>()
            : forbidden.Where(v => msg.Contains(v, StringComparison.Ordinal)).ToArray();
        Check(label, msg is not null && missing.Length == 0 && leaked.Length == 0,
            $"missing=[{string.Join(",", missing)}] leaked=[{string.Join(",", leaked)}] :: {msg ?? "(accepted / no throw)"}");
    }

    static readonly CollectionShape ListCoerced = new(CollectionKind.List, ElementPlacement.Coerced);
    static readonly CollectionShape ListComposed = new(CollectionKind.List, ElementPlacement.Composed);
    static readonly CollectionShape ListRecord = new(CollectionKind.List, ElementPlacement.OwnedRecord);
    static readonly CollectionShape DictCoerced = new(CollectionKind.Dict, ElementPlacement.Coerced);
    static readonly CollectionShape DictComposed = new(CollectionKind.Dict, ElementPlacement.Composed);

    static IEnumerable<VerbUse> Keyed(CollectionShape s) => WriteVerbs.On(s).Where(u => u.NeedsKey);
    static IEnumerable<VerbUse> Placing(CollectionShape s) => WriteVerbs.On(s).Where(u => u.Places);

    static void SitesArm()
    {
        Console.WriteLine();
        Console.WriteLine("── SITES: each consuming message, one arm per cardinality, verbs present AND the other half absent ──");

        // (1) the gate's leaf-bracket remedy.
        CheckShaped("SITE-BRACKET-GATE-LIST — a bracketed LIST leaf is answered with the index verbs and no dict verb",
            Rules.Validate(new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames[0]" }, Verb = "Set", Value = "x" }),
            ListCoerced, Keyed(ListCoerced));
        CheckShaped("SITE-BRACKET-GATE-DICT — a bracketed DICT leaf is answered with the keyed verbs and NO SetAtIndex/InsertAtIndex",
            Rules.Validate(new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights[Alteration]" }, Verb = "Set", Value = "1" }),
            DictCoerced, Keyed(DictCoerced));

        // (2) the engine's twin, reached by a direct/CLI call that never met the gate.
        CheckShaped("SITE-BRACKET-ENGINE-LIST — the engine twin answers a bracketed LIST leaf with the index verbs",
            Throws(() => WriteEngine.ApplyVerb(new Mutagen.Bethesda.Skyrim.Race(NextFk(), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE),
                new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames[0]" }, Verb = "Set", Value = "x" })),
            ListCoerced, Keyed(ListCoerced));
        CheckShaped("SITE-BRACKET-ENGINE-DICT — the engine twin answers a bracketed DICT leaf with the keyed verbs and no index verb",
            Throws(() => WriteEngine.ApplyVerb(new Mutagen.Bethesda.Skyrim.Class(NextFk(), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE),
                new WriteRequest { RecordType = "Class", Path = new[] { "SkillWeights[Alteration]" }, Verb = "Set", Value = "1" })),
            DictCoerced, Keyed(DictCoerced));

        // (3) Set-on-list, once per element placement — the arm that used to name two of five verbs.
        CheckShaped("SITE-SET-ON-LIST-COERCED — the remedy names every placing verb a plain-value list takes",
            Rules.Validate(new WriteRequest { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Set", Value = "x" }),
            ListCoerced, Placing(ListCoerced));
        CheckShaped("SITE-SET-ON-LIST-COMPOSED — a modeled list gets the compose-carrying placing verbs",
            Rules.Validate(new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Set", Value = "1" }),
            ListComposed, Placing(ListComposed));
        // The shape with NO placing verbs. A verb list would be empty here, so the remedy has to say why rather
        // than trail off — and it must not name a write verb, since every one of them redirects.
        var setOnRecordList = Rules.Validate(new WriteRequest
        { RecordType = "Cell", Path = new[] { "Persistent" }, Verb = "Set", Value = "1" });
        Check("SITE-SET-ON-LIST-OWNEDRECORD — a list of owned child records has no placing verb, so the remedy names the record axis instead of trailing off",
            setOnRecordList is not null
                && setOnRecordList.Contains("housecarl_create_record", StringComparison.Ordinal)
                && !ListOnly.Any(v => setOnRecordList.Contains(v, StringComparison.Ordinal)),
            setOnRecordList ?? "(accepted)");

        // (4) the unknown-verb vocabulary — every verb the surface has, from its one home.
        var unknown = Rules.Validate(new WriteRequest
        { RecordType = "Race", Path = new[] { "MovementTypeNames" }, Verb = "Nope", Key = "0", Value = "x" });
        Check("SITE-UNKNOWN-VERB — the legal list is the whole published vocabulary, drawn from WriteVerbs.All rather than retyped",
            unknown is not null && WriteVerbs.All.All(v => unknown.Contains(v, StringComparison.Ordinal)),
            unknown ?? "(accepted)");

        // (5) the composes= verb refusal, whose parenthetical fires BEFORE the cardinality check below it.
        CheckShaped("SITE-COMPOSES-LIST — the singular-path parenthetical on a LIST names the index verbs",
            Rules.Validate(new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "Set",
                Structs = new[] { new StructSpec { Type = "ConditionFloat" } } }),
            ListComposed, Placing(ListComposed));
        CheckShaped("SITE-COMPOSES-DICT — the SAME refusal on a DICT names keyed verbs, not SetAtIndex/InsertAtIndex",
            Rules.Validate(new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Set",
                Structs = new[] { new StructSpec { Type = "PackageDataBool" } } }),
            DictComposed, Placing(DictComposed));

        // (6) THE class-A medium: the modeled-elements message, emitted for a list OR a dict.
        CheckShaped("SITE-MODELED-LIST — a values= ReplaceAll on a modeled LIST is answered with the list's own placing verbs",
            Rules.Validate(new WriteRequest { RecordType = "Faction", Path = new[] { "Conditions" }, Verb = "ReplaceAll",
                Values = new[] { "1" } }),
            ListComposed, Placing(ListComposed));
        CheckShaped("SITE-MODELED-DICT — the medium: a Package.Data Merge no longer offers InsertAtIndex, which that caller cannot use",
            Rules.Validate(new WriteRequest { RecordType = "Package", Path = new[] { "Data" }, Verb = "Merge",
                Entries = new Dictionary<string, string>() }),
            DictComposed, Placing(DictComposed));

        // (7) the array refusal — an enumeration of the collection verbs rather than a remedy, derived all the same.
        var arr = Throws(() => WriteEngine.ApplyVerb(
            new Mutagen.Bethesda.Skyrim.Weather(NextFk(), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE),
            new WriteRequest { RecordType = "Weather", Path = new[] { "CloudTextures" }, Verb = "Add", Value = "x" }));
        var arrShape = new CollectionShape(CollectionKind.List, ElementPlacement.Coerced);
        CheckShaped("SITE-ARRAY-REFUSAL — the unsupported set is the shape's collection verbs, so a verb cannot fall out of it and read as supported",
            arr, arrShape, WriteVerbs.On(arrShape).Where(u => u.Places || u.NeedsKey));
        Check("SITE-ARRAY-REFUSAL-NOT-COPYFROM — CopyFrom is left out by the purpose filter, not by hand: it is neither a placing nor a keyed verb",
            arr is not null && !arr.Contains("CopyFrom", StringComparison.Ordinal), arr ?? "(no throw)");
    }

    static readonly Mutagen.Bethesda.Plugins.ModKey MKey =
        new("HcRemedyGuard", Mutagen.Bethesda.Plugins.ModType.Master);
    static uint _next = 0x800;
    static Mutagen.Bethesda.Plugins.FormKey NextFk() => new(MKey, _next++);

    static string? Throws(Action act)
    {
        try { act(); return null; }
        catch (Exception ex) { return (ex.InnerException ?? ex).Message; }
    }

    // ---------------------------------------------------------------- request construction

    static WriteRequest? BuildRequest(string rec, FieldSchema f, string verb, VerbInput input, bool needsKey)
    {
        string? key = null;
        if (needsKey)
        {
            key = f.Cardinality == "list" ? "0" : SampleKey(f);
            if (key is null) return null;
        }
        string? value = null;
        string[]? values = null;
        Dictionary<string, string>? entries = null;
        StructSpec? spec = null;
        IReadOnlyList<StructSpec>? specs = null;
        switch (input)
        {
            // The singular value has no honest empty form — a null one is refused for BEING null ("requires an
            // element value"), which would read as a verb refusal in the refuse direction and as a false red in
            // the accept one. So a field whose element type has no sample is DROPPED and reported, never faked.
            case VerbInput.Value:
                if (SampleElement(f) is not { } v1) return null;
                value = v1; break;
            // The batch slots do have one: an empty values=/entries= is a well-formed "clear", so a verb refused
            // with one is refused for being the wrong VERB, which is what the refuse direction is asking about.
            case VerbInput.Values:
                values = SampleElement(f) is { } v2 ? new[] { v2 } : Array.Empty<string>(); break;
            case VerbInput.Entries:
                entries = SampleKey(f) is { } ek && SampleElement(f) is { } ev
                    ? new Dictionary<string, string> { [ek] = ev }
                    : new Dictionary<string, string>();
                break;
            case VerbInput.Compose:
                if (SampleSpec(f) is not { } s1) return null;
                spec = s1; break;
            case VerbInput.Composes:
                if (SampleSpec(f) is not { } s2) return null;
                specs = new[] { s2 }; break;
        }
        return new WriteRequest
        {
            RecordType = rec, Path = new[] { f.Name }, Verb = verb,
            Key = key, Value = value, Values = values, Entries = entries, Struct = spec, Structs = specs,
        };
    }

    /// <summary>A key the gate will accept for this dict, derived from the key's real CLR type — the same type
    /// apply keys on. Null when no sample can be made (reported as "not constructible", never as a pass).</summary>
    static string? SampleKey(FieldSchema f)
    {
        var aq = f.MutableTypeAssemblyQualified ?? f.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return null;
        if (WriteEngine.ClosedInterface(rt, typeof(IDictionary<,>)) is not { } di) return null;
        return SampleOf(di.GetGenericArguments()[0]);
    }

    /// <summary>Element type display names no sample could be made for — reported, so a dropped cell is a stated
    /// gap in the sweep rather than an invisible one.</summary>
    static readonly SortedSet<string> _noSample = new(StringComparer.Ordinal);

    /// <summary>A value the gate will accept as one element of this collection.</summary>
    static string? SampleElement(FieldSchema f)
    {
        if (f.FormLinkTarget is not null) return "Null";   // the legal null-clear form IsValidFormLinkValue accepts
        var aq = f.ElementTypeAssemblyQualified;
        var s = aq is null ? null : WriteEngine.ResolveType(aq) is { } rt ? SampleOf(rt) : null;
        if (s is null) _noSample.Add(f.ElementTypeRef ?? f.ElementType ?? aq ?? "(none)");
        return s;
    }

    static string? SampleOf(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u.IsEnum) return Enum.GetNames(u).FirstOrDefault();
        if (u == typeof(string)) return "x";
        if (u == typeof(bool)) return "true";
        if (u == typeof(float) || u == typeof(double)) return "0";
        if (u == typeof(int) || u == typeof(uint) || u == typeof(short) || u == typeof(ushort)
            || u == typeof(long) || u == typeof(ulong) || u == typeof(byte) || u == typeof(sbyte)) return "0";
        if (typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter).IsAssignableFrom(u)) return "Null";
        if (u == typeof(Mutagen.Bethesda.Plugins.FormKey)) return "Null";
        // The two whole-coercible element families a Skyrim collection actually carries: an asset link takes a
        // path string, a byte blob takes hex. Both are Coerce's own recognised forms, so a sample here is the form
        // a caller would really send rather than a shape invented for the sweep.
        if (u.IsGenericType && u.Name.StartsWith("AssetLink", StringComparison.Ordinal)) return "x";
        if (u.IsGenericType && u.Name.StartsWith("IAssetLink", StringComparison.Ordinal)) return "x";
        if (u.IsGenericType && u.Name.StartsWith("MemorySlice", StringComparison.Ordinal)) return "00";
        if (u.IsGenericType && u.Name.StartsWith("ReadOnlyMemorySlice", StringComparison.Ordinal)) return "00";
        return null;
    }

    /// <summary>A compose spec naming a concrete type the element accepts — the element's own modeled type, or the
    /// first arm of a polymorphic-base element. RECORD-kind elements are included deliberately: a caller who does
    /// not know the element is an owned child record WILL send exactly this, and it is the request the record-axis
    /// redirect exists to answer, so it must be the request the refuse direction sends.</summary>
    static StructSpec? SampleSpec(FieldSchema f)
    {
        if (f.ElementTypeRef is not { } er) return null;
        var t = Corp.Types.GetValueOrDefault(er);
        if (t is null) return null;
        if (t.Kind is "struct" or "arm" or "record") return new StructSpec { Type = er };
        if (t.Kind == "polymorphic-base")
        {
            var arm = (t.Arms ?? new()).FirstOrDefault(a => a != er
                && Corp.Types.GetValueOrDefault(a)?.Kind is "struct" or "arm" or "record");
            return arm is null ? null : new StructSpec { Type = arm };
        }
        return null;
    }
}

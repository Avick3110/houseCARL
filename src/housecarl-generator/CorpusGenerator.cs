using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Plugins;          // FormKey, ModKey
using Mutagen.Bethesda.Plugins.Records;  // IMajorRecordGetter
using Mutagen.Bethesda.Skyrim;           // IArmorGetter (assembly anchor)

namespace HousecarlGenerator;

/// <summary>
/// First-wave step 2 — the cornerstone in code. Walks the entire
/// Mutagen.Bethesda.Skyrim type universe via reflection and emits a flat type
/// catalog covering <b>literally every type Mutagen models</b>: the mod header,
/// every major record (top-level and nested), every reachable sub-struct, and
/// every polymorphic arm — at full depth, by construction. Coverage == Mutagen's
/// coverage; no category filter, no depth limit, no subset.
/// </summary>
public static class CorpusGenerator
{
    /// <summary>Internal (not private) so corpus-hygiene-guard's INV6 can drive <see cref="ClassifyField"/>
    /// directly on a real Mutagen type — the classifier's own branches, checked without going through a whole
    /// emit. Same assembly; nothing outside the generator sees it.</summary>
    internal record RefItem(Type Getter, string Kind, string? AbstractBase);

    static readonly List<string> Warnings = new();

    /// <summary>
    /// Types already reported as <see cref="ArmClass.WritableButUnextractable"/>. A single such class is
    /// typically a candidate arm of several polymorphic bases, and <see cref="FindUnionArms"/> re-walks the
    /// whole assembly per base — without this, one upstream gap would emit the same anomaly many times over
    /// and bury the rest of the list.
    /// </summary>
    static readonly HashSet<Type> ReportedUnextractable = new();

    /// <summary>
    /// Every assembly <see cref="FindUnionArms"/> actually enumerated during the walk, recorded as it goes.
    /// DERIVED, never declared: arm-classification-guard asserts its neutrality invariant over exactly this
    /// set instead of a hand-written list of assembly names. A hand-written list was wrong twice in a row —
    /// first naming only Mutagen.Bethesda.Skyrim, then Skyrim and Core while the walk also reaches
    /// Noggog.CSharpExt through IReadOnlyArray2d&lt;T&gt;. Nobody enumerates these by hand again.
    /// </summary>
    static readonly HashSet<Assembly> WalkedAssemblies = new();

    /// <summary>The assemblies the last completed walk visited. Empty until a corpus has been built;
    /// callers force <see cref="CachedCorpus"/> first.</summary>
    internal static IReadOnlyCollection<Assembly> AssembliesWalked => WalkedAssemblies;

    // The reflection walk over Mutagen's whole type library is the dominant CI cost (~11.5s) and is
    // PROCESS-DETERMINISTIC (same assembly -> same corpus). Memoize it via Lazy (ExecutionAndPublication): the
    // FIRST GenerateAll in a process walks Mutagen exactly once — thread-safe even under concurrent first-callers
    // — and every later call reuses the cached Corpus and only re-emits outputs to the caller's dir. Transparent:
    // a standalone probe process walks once (unchanged); the in-process CI runner (ci-all) calls GenerateAll ~21x
    // and still reflects ONCE.
    static readonly Lazy<Corpus> CachedCorpus = new(BuildCorpus, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int GenerateAll(string outputDir, string refDir) => EmitCorpus(CachedCorpus.Value, outputDir, refDir);

    static Corpus BuildCorpus()
    {
        Warnings.Clear();
        ReportedUnextractable.Clear();
        WalkedAssemblies.Clear();
        var asm = typeof(IArmorGetter).Assembly; // Mutagen.Bethesda.Skyrim
        Console.WriteLine($"Walking the full Mutagen type corpus via reflection...");
        Console.WriteLine($"  Assembly: {asm.GetName().Name} {asm.GetName().Version}");
        Console.WriteLine();

        // ---- 1. Seeds: the mod header + every concrete major-record type's getter interface. ----
        var seeds = new List<RefItem>();

        var headerGetter = asm.GetType("Mutagen.Bethesda.Skyrim.ISkyrimModHeaderGetter");
        if (headerGetter != null) seeds.Add(new RefItem(headerGetter, "header", null));
        else Warnings.Add("ISkyrimModHeaderGetter not found — header omitted (investigate).");

        var seenRecordGetters = new HashSet<Type>();
        var sigByName = new Dictionary<string, string>(StringComparer.Ordinal); // record catalog name -> xEdit 4-char signature
        foreach (var c in asm.GetTypes())
        {
            if (!c.IsClass || c.IsAbstract) continue;
            if (IsOverlayTwin(c)) continue; // Mutagen's lazy-read overlay of the real class; not a distinct type
            if (!typeof(IMajorRecordGetter).IsAssignableFrom(c)) continue;
            var gi = GetterInterfaceFor(c);
            if (gi == null) { Warnings.Add(UnextractableWarning("record class", c)); continue; }
            if (seenRecordGetters.Add(gi))
            {
                seeds.Add(new RefItem(gi, "record", null));
                // Capture the signature here, where the concrete class is in hand (the BFS below works from
                // getter interfaces). Verified reachable + correct for all 133 records (Probe.RunSig).
                var sig = RecordSignature(c);
                if (sig != null) sigByName[CatalogName(gi)] = sig;
                else Warnings.Add($"No xEdit signature resolved for record {c.Name}.");
            }
        }

        // The mod CONTAINER itself (Aaron-go 2026-05-30: in scope). It is neither the header nor a major record,
        // so record-reachability never reaches it — seed it explicitly. Seeding walks the SkyrimGroup<T> /
        // SkyrimListGroup<T> record-group surface (now handled as collections in IsList), the only path to types
        // like CellBlock. ISkyrimModGetter has two implementers — the writable SkyrimMod and the read-only
        // SkyrimMultiModOverlay projection (a multi-mod view); the projection is filtered as non-authorable
        // (ClassifyArm), so the container classifies as a plain struct, not a union. Route it through
        // EnqueueModeledRef like any other modeled reference.
        var modGetter = asm.GetType("Mutagen.Bethesda.Skyrim.ISkyrimModGetter");
        if (modGetter != null) EnqueueModeledRef(modGetter, seeds);
        else Warnings.Add("ISkyrimModGetter not found — mod container omitted (investigate).");

        // The PEX subsystem (Aaron-go 2026-05-30: in scope; tools prefer .psc source + Papyrus compile, PEX is the
        // sourceless fallback). Compiled-Papyrus types live in Mutagen.Bethesda.Core, a different assembly than the
        // Skyrim records, so seed the PEX root (IPexFileGetter) to bring the 16 PEX types into the catalog.
        var pexGetter = typeof(IMajorRecordGetter).Assembly.GetType("Mutagen.Bethesda.Pex.IPexFileGetter");
        if (pexGetter != null) EnqueueModeledRef(pexGetter, seeds);
        else Warnings.Add("IPexFileGetter not found — PEX subsystem omitted (investigate).");

        // ---- 2. Transitive closure -> flat catalog. ----
        var catalog = new SortedDictionary<string, TypeSchema>(StringComparer.Ordinal);
        var queue = new Queue<RefItem>();
        var enqueued = new HashSet<Type>();
        foreach (var s in seeds) if (enqueued.Add(s.Getter)) queue.Enqueue(s);

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();

            // Enums are catalogued as their own kind (decision #6): listed once with their legal values and
            // referenced by name from every field, instead of inlining e.g. ActorValue's 156 values on each
            // of the dozens of fields that use it. An enum's catalog name is its raw type name — enum names
            // don't need the I-/Getter- normalization records and structs go through.
            if (item.Kind == "enum")
            {
                var ename = EnumCatalogName(item.Getter);
                var efull = item.Getter.FullName ?? item.Getter.Name;
                if (catalog.TryGetValue(ename, out var existing))
                {
                    // Same key + same underlying type = the normal dedup (one enum referenced by many fields).
                    // Same key + a DIFFERENT type = a residual collision the qualified name failed to resolve —
                    // fail LOUD (the old code silently skipped enum-vs-enum clashes, the root of the Flag x90 bug).
                    if (existing.Kind != "enum" || existing.GetterInterface != efull)
                        Warnings.Add($"Name collision: enum '{ename}' ({efull}) clashes with existing {existing.Kind} '{existing.GetterInterface}' — disambiguate the catalog key.");
                    continue;
                }
                catalog[ename] = new TypeSchema
                {
                    Name = ename,
                    Kind = "enum",
                    GetterInterface = efull,
                    EnumValues = Enum.GetNames(item.Getter).ToList(),
                };
                continue;
            }

            var name = CatalogName(item.Getter);
            if (catalog.ContainsKey(name)) continue;

            var (schema, referenced) = ExtractType(item.Getter, item.Kind, item.AbstractBase, name);
            catalog[name] = schema;

            foreach (var r in referenced)
                if (enqueued.Add(r.Getter)) queue.Enqueue(r);
        }

        // Attach each record's xEdit signature (captured at seed time, where the concrete class was in hand).
        foreach (var (cname, sig) in sigByName)
            if (catalog.TryGetValue(cname, out var ts)) ts.Signature = sig;

        // ---- 3. Assemble + emit. ----
        var corpus = new Corpus
        {
            MutagenAssembly = $"{asm.GetName().Name} {asm.GetName().Version}",
            RecordTypes = seeds.Count(s => s.Kind == "record"),
            TotalTypes = catalog.Count,
            Types = catalog,
        };
        foreach (var t in catalog.Values)
            corpus.KindCounts[t.Kind] = corpus.KindCounts.GetValueOrDefault(t.Kind) + 1;

        // Regression guard: confirm the read-only field surface stays within the four understood
        // categories. Anything outside them is flagged in Report's anomaly list (a possible lost
        // setter — a content field silently dropping out of the writable surface).
        AuditWritability(corpus);
        return corpus;
    }

    /// <summary>Write the corpus' artifacts (corpus.json + summary + the slim reference tree) into the
    /// requested dirs and print the report. Split out of <see cref="GenerateAll"/> so a memoized (already-walked)
    /// corpus re-emits to a fresh caller's dir without re-reflecting. corpus.json (full, for the write tool) and
    /// the skill's read view come out of one walk, so they physically can't disagree about field names or types.</summary>
    static int EmitCorpus(Corpus corpus, string outputDir, string refDir)
    {
        Directory.CreateDirectory(outputDir);
        var jsonPath = Path.Combine(outputDir, "corpus.json");
        var sumPath = Path.Combine(outputDir, "corpus.summary.md");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(corpus, JsonOpts));
        File.WriteAllText(sumPath, BuildSummary(corpus));
        ReferenceEmitter.Emit(corpus, refDir);
        Report(corpus, jsonPath, sumPath, refDir);
        return 0;
    }

    // ---------------------------------------------------------------- extraction

    static (TypeSchema, List<RefItem>) ExtractType(Type getterType, string kind, string? abstractBase, string catalogName)
    {
        var mutableType = MutableInterfaceFor(getterType);
        if (mutableType == null && kind is "record" or "header")
            Warnings.Add($"No mutable interface resolved for {kind} '{catalogName}' ({getterType.Name}).");

        var getterProps = CollectAllProperties(getterType).ToList();
        var mutableByName = mutableType == null
            ? new Dictionary<string, PropertyInfo>()
            : CollectAllProperties(mutableType).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());

        var referenced = new List<RefItem>();
        var fields = new List<FieldSchema>();
        var nullCtx = new System.Reflection.NullabilityInfoContext();

        foreach (var p in getterProps.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var hasMutable = mutableByName.TryGetValue(p.Name, out var mp);
            var writable = hasMutable && (mp!.CanWrite || IsMutableCollection(mp.PropertyType));

            var f = ClassifyField(p.PropertyType, referenced, catalogName, p.Name);
            f.Name = p.Name;
            f.Writable = writable;
            // SUBSTRUCT / POLYMORPHIC nullability. A substruct or a polymorphic-union field is a reference-typed interface
            // property (IVmadGetter?, ITranslatedStringGetter?, …), NOT a Nullable<T> value type — so ClassifyField (which
            // reads only Nullable.GetUnderlyingType) always leaves it non-nullable, and Mutagen's real "?" annotation is
            // lost. Read it back from the getter property's NRT metadata via NullabilityInfoContext. A nullable such field
            // is then Remove-able BY CONSTRUCTION: VerbLegality already allows Remove on a nullable leaf, and
            // ApplyScalarVerb's Remove sets the property null — so correcting the schema's nullability completes the
            // "clear/un-fragment a sub-object" capability (INFO.VirtualMachineAdapter; a poly arm like
            // DialogResponsesAdapter.ScriptFragments or Npc.Sound) with no new write path. Same Mutagen reflection that
            // builds the write surface, so schema and engine can't disagree. value/enum/formlink already carry correct
            // nullability (Nullable<T> + the FormLinkNullable name-check in ClassifyField).
            // NOT A REQUIRED-ARM SIGNAL: this poly nullability is a faithful "can this field be absent?", but it is NOT a
            // "required arm at serialize" predicate, and NO pre-flight gate keys on it. NpcConfiguration.Level reads
            // Nullable=false yet a null Level serializes fine (nullarm-guard B2), while Condition.Data (also Nullable=false)
            // throws — same flag, opposite serialize behavior. A gate on the flag would over-reject a legitimately-absent
            // field or need a hand-curated required-arm list (cornerstone §3), so a genuinely-missing required arm stays
            // caught at the serialize boundary (WriteEngine.WritePatch's NullArmSerializeException), the honest failure point.
            if ((f.Cardinality is "substruct" or "polymorphic") && !f.Nullable
                && nullCtx.Create(p).ReadState == System.Reflection.NullabilityState.Nullable)
                f.Nullable = true;
            f.GetterTypeAssemblyQualified = p.PropertyType.AssemblyQualifiedName ?? p.PropertyType.FullName ?? p.PropertyType.Name;
            f.MutableTypeAssemblyQualified = hasMutable ? (mp!.PropertyType.AssemblyQualifiedName ?? mp.PropertyType.FullName) : null;
            fields.Add(f);
        }

        var arms = abstractBase == null && getterType.IsInterface ? FindUnionArms(getterType) : new List<Type>();

        var schema = new TypeSchema
        {
            Name = catalogName,
            Kind = kind,
            GetterInterface = getterType.FullName ?? getterType.Name,
            MutableInterface = mutableType?.FullName,
            GetterInterfaceAssemblyQualified = getterType.AssemblyQualifiedName ?? getterType.FullName ?? getterType.Name,
            MutableInterfaceAssemblyQualified = mutableType?.AssemblyQualifiedName,
            AbstractBase = abstractBase,
            Arms = kind == "polymorphic-base" && arms.Count > 0
                ? EmittedArmNames(arms, catalogName)   // raw count gates detection above; self-listing stripped at emit
                : null,
            Fields = fields,
            FieldCount = fields.Count,
            WritableCount = fields.Count(x => x.Writable),
        };
        return (schema, referenced);
    }

    internal static FieldSchema ClassifyField(Type t, List<RefItem> referenced, string ownerName, string fieldName)
    {
        var f = new FieldSchema();
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying != null) { t = underlying; f.Nullable = true; }

        // Identity value-types: emitted + flagged, never recursed.
        if (t == typeof(FormKey) || t == typeof(ModKey))
        {
            f.Cardinality = "value"; f.Type = t.Name; f.IsIdentity = true; return f;
        }
        if (t.IsEnum)
        {
            // Reference the enum's catalog entry by name (decision #6); its values live once on that entry
            // and are resolved on demand, not copied onto every field of this enum type. The catalog key is the
            // enum's UNIQUE qualified name (DeclaringType.Name + "." + Name for nested enums), not the bare name:
            // ~90 distinct per-record enums are all named "Flag", and a bare-name catalog silently collapses them
            // to one entry, mislabeling every other field's legal values (project_enum_simple_name_collision).
            var en = EnumCatalogName(t);
            f.Cardinality = "enum"; f.Type = en; f.TypeRef = en;
            referenced.Add(new RefItem(t, "enum", null));
            return f;
        }
        if (t == typeof(string)) { f.Cardinality = "scalar"; f.Type = "string"; return f; }
        if (t.IsPrimitive) { f.Cardinality = "scalar"; f.Type = SimpleName(t); return f; }
        if (t == typeof(decimal)) { f.Cardinality = "scalar"; f.Type = "decimal"; return f; }
        if (t == typeof(DateTime)) { f.Cardinality = "scalar"; f.Type = "DateTime"; return f; }

        if (t.IsGenericType)
        {
            var genName = t.GetGenericTypeDefinition().FullName ?? t.GetGenericTypeDefinition().Name;
            var args = t.GetGenericArguments();

            if (IsFormLink(genName))
            {
                var target = args[0];
                var formNullable = f.Nullable || genName.Contains("Nullable");
                f.Cardinality = "formlink";
                f.Type = $"FormLink<{target.Name}>" + (formNullable ? "?" : "");
                f.Nullable = formNullable;
                f.FormLinkTarget = target.Name;
                f.FormLinkTargetAssemblyQualified = target.AssemblyQualifiedName;
                return f;
            }

            if (IsList(genName))
            {
                var elem = args[0];
                var elemBare = Nullable.GetUnderlyingType(elem) ?? elem;
                f.Cardinality = "list";
                f.ElementTypeAssemblyQualified = elem.AssemblyQualifiedName;

                if (elemBare.IsGenericType && IsFormLink(elemBare.GetGenericTypeDefinition().FullName ?? ""))
                {
                    var target = elemBare.GetGenericArguments()[0];
                    f.Type = $"List<FormLink<{target.Name}>>";
                    f.ElementType = $"FormLink<{target.Name}>";
                    f.FormLinkTarget = target.Name;
                    f.FormLinkTargetAssemblyQualified = target.AssemblyQualifiedName;
                }
                else if (IsModeledStruct(elemBare, out var elemGetter))
                {
                    f.Type = $"List<{elemBare.Name}>";
                    f.ElementType = elemBare.Name;
                    f.ElementTypeRef = CatalogName(elemGetter!);
                    f.ElementArms = EnqueueModeledRef(elemGetter!, referenced); // polymorphic list element -> arms catalogued
                }
                else
                {
                    f.Type = $"List<{elemBare.Name}>";
                    f.ElementType = elemBare.Name;
                }
                return f;
            }

            if (IsDictionary(genName))
            {
                // Mutagen models a few fields as dictionaries (e.g. Package.Data: Dictionary<SByte, IAPackageDataGetter>).
                // The VALUE type can itself be a polymorphic union; route it through the same arm-detection a list
                // element gets, so its arms are catalogued instead of collapsing to the opaque dictionary shell (Mode B).
                var keyT = args[0];
                var valBare = Nullable.GetUnderlyingType(args[1]) ?? args[1];
                f.Cardinality = "dict";
                f.KeyType = SimpleName(keyT);
                f.Type = $"Dictionary<{SimpleName(keyT)},{valBare.Name}>"; // SimpleName for the key matches KeyType (sbyte, not SByte)
                f.ElementType = valBare.Name;
                f.ElementTypeAssemblyQualified = args[1].AssemblyQualifiedName;
                if (IsModeledStruct(valBare, out var valGetter))
                {
                    f.ElementTypeRef = CatalogName(valGetter!);
                    f.ElementArms = EnqueueModeledRef(valGetter!, referenced);
                }
                return f;
            }
            // Other generics (e.g. IGenderedItem<T>) fall through to substruct handling on the closed type.
        }

        // Substruct — modeled by Mutagen, possibly polymorphic.
        var getterIfc = ResolveGetterInterface(t);
        if (getterIfc == null)
        {
            // Not a Loqui-modeled type (no getter interface). Mutagen treats it as an atomic
            // value (Color, P3Float, Percent, RecordType, TimeOnly, raw bytes, ...). Mirror that
            // granularity: a typed value, not a recursed struct, not a gap. A real navigable
            // struct is always a Loqui *class*; if a non-value reference type lands here, flag it.
            f.Cardinality = "value"; f.Type = ValueTypeName(t);
            // object / System.Type are Mutagen's deliberate loose typing for polymorphic
            // condition parameters (the value's real type depends on the function) — expected
            // atomic. Any *other* non-value reference type landing here is worth a look.
            if (!t.IsValueType && t != typeof(object) && t != typeof(Type))
                Warnings.Add($"{ownerName}.{fieldName}: non-Loqui reference type '{t.Name}' emitted as atomic value — verify.");
            return f;
        }

        // FormLink machinery reaching here as the bare IFormLinkIdentifier (the generic
        // IFormLink<T> is handled above) -> mirror as a form reference, not a struct.
        //
        // A MAJOR RECORD is cut from that rule first (#335). IMajorRecordGetter itself carries
        // IFormLinkIdentifier — a record identifies itself (FormKey + Type) exactly the way a link identifies
        // its target — so the bare-link test cannot tell "points at a record" from "IS a record". Every field
        // whose type is a major record is an OWNED CHILD (Cell.Landscape, Worldspace.TopCell): the parent holds
        // the record itself, and calling it a link told a caller to Set a FormID on it, which the gate accepted
        // and the engine then threw on. Falling through leaves it to the substruct branch below, which is where
        // the catalog already models owned children — its TypeRef is the child's own record entry, the shape the
        // write surface reads as "owned child record, navigate in, never Set wholesale" (WriteEngine's
        // record-substruct arm) and the shape a LIST of owned children (Cell.Persistent, DialogTopic.Responses)
        // has always had. Same predicate WriteEngine.ChildBearingProperties uses, so the reference and the walk
        // that preserves children across a forward agree by construction; corpus-hygiene-guard INV6 pins it.
        if (!typeof(IMajorRecordGetter).IsAssignableFrom(getterIfc)
            && ImplementsByName(getterIfc, "IFormLinkIdentifier"))
        {
            f.Cardinality = "formlink"; f.Type = "FormLink"; return f;
        }
        // Mutagen asset-path link (model / texture file reference) -> a typed value path.
        if (ImplementsByName(getterIfc, "IAssetLinkGetter"))
        {
            f.Cardinality = "value"; f.Type = "AssetLink"; return f;
        }

        // Direct sub-struct field. Route through the shared helper so a polymorphic union discovered here is
        // expanded identically to one discovered as a list element or dictionary value (see EnqueueModeledRef).
        f.Type = t.Name;
        f.TypeRef = CatalogName(getterIfc);
        var directArms = EnqueueModeledRef(getterIfc, referenced);
        f.Cardinality = directArms != null ? "polymorphic" : "substruct";
        f.Arms = directArms;
        return f;
    }

    /// <summary>
    /// THE single place arm-detection happens. Given the getter interface of a modeled type discovered in ANY
    /// container position — direct field, list element, dictionary value, or group element — enqueue it for the
    /// catalog: if it is a polymorphic union (a getter interface with &gt;1 concrete arm) enqueue the base as
    /// "polymorphic-base" plus every arm as "arm" and return the arm catalog names; otherwise enqueue it as a
    /// plain "struct" and return null. Hand-wiring this into only the direct-substruct branch was the step-2
    /// cornerstone defect (arms silently dropped wherever a union sat inside a list/dict/group); routing every
    /// position through this helper makes arm coverage container-agnostic, by construction.
    /// </summary>
    static List<string>? EnqueueModeledRef(Type getterIfc, List<RefItem> referenced)
    {
        var arms = getterIfc.IsInterface ? FindUnionArms(getterIfc) : new List<Type>();
        if (arms.Count > 1)
        {
            referenced.Add(new RefItem(getterIfc, "polymorphic-base", null));
            foreach (var a in arms)
            {
                var ag = GetterInterfaceFor(a) ?? a;
                referenced.Add(new RefItem(ag, "arm", CatalogName(getterIfc)));
            }
            // The base self-lists when concrete (FindUnionArms keeps it); the >1 count above already
            // classified the union, so strip the self-entry from the emitted arm list (never null here —
            // a >1 raw count leaves at least one real arm after the strip). The self-arm RefItem queued
            // just above is harmless: it dedups against the polymorphic-base entry of the same getter.
            return EmittedArmNames(arms, CatalogName(getterIfc));
        }
        referenced.Add(new RefItem(getterIfc, "struct", null));
        return null;
    }

    /// <summary>
    /// Catalog names of a polymorphic base's <paramref name="arms"/> with the base's own self-entry
    /// removed — the rendering used for the EMITTED <c>Arms</c>/<c>ElementArms</c> lists (corpus.json
    /// and the mutagen-reference skill). A CONCRETE base (e.g. APackageData, ScriptFragments) is
    /// assignable to its own getter interface, so <see cref="FindUnionArms"/> lists it among its own
    /// arms; an ABSTRACT base (Condition, Global) isn't, hence the long-standing asymmetry. That
    /// self-entry is load-bearing for arm DETECTION — it is part of the &gt;1 count that classifies the
    /// union, and dropping it BEFORE the count would demote two-entry unions like ScriptFragments
    /// (<c>[ScriptFragments, SceneScriptFragments]</c>) / SimpleModel (<c>[SimpleModel, Model]</c>)
    /// back to plain structs, silently losing their real second arm (WRITE_PREFLIGHT_GAP_AUDIT_2026-06-18
    /// §9). But a base is not a legal arm of ITSELF (the runtime G8 gate already rejects composing one),
    /// so it must never appear in the emitted list. Strip it here, at emit only, keyed on the base's own
    /// catalog name — detection upstream still counts the raw arm set, so coverage is unchanged.
    /// </summary>
    static List<string> EmittedArmNames(IEnumerable<Type> arms, string baseCatalogName) =>
        arms.Select(a => CatalogName(GetterInterfaceFor(a) ?? a))
            .Where(name => name != baseCatalogName)
            .ToList();

    // ---------------------------------------------------------------- reflection helpers
    // CollectAllProperties + IsInfrastructureProperty carry forward the spike's three
    // quirk fixes: the Loqui infrastructure filter (namespace whitelist + name fallback),
    // the writable-preference dedup, and ExtendedList detection (in IsList/IsMutableCollection).

    static IEnumerable<PropertyInfo> CollectAllProperties(Type t)
    {
        var all = new List<PropertyInfo>();
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(t);
        foreach (var i in t.GetInterfaces()) queue.Enqueue(i);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!seen.Add(current)) continue;
            foreach (var p in current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length != 0) continue; // C# indexer (this[...]) — not a named field (the Weather* "Item" leak)
                if (IsInfrastructureProperty(p)) continue;
                all.Add(p);
            }
            foreach (var i in current.GetInterfaces()) queue.Enqueue(i);
        }

        // Writable-preference dedup: same name on multiple interfaces -> keep the writable declaration.
        return all.GroupBy(p => p.Name).Select(g => g.OrderByDescending(p => p.CanWrite).First());
    }

    static bool IsInfrastructureProperty(PropertyInfo p)
    {
        var ns = p.DeclaringType?.Namespace ?? "";
        bool inWhitelist = ns.StartsWith("Mutagen.Bethesda") || ns.StartsWith("Noggog");
        if (!inWhitelist) return true;
        // Mutagen's fluent write-builder API (e.g. ISkyrimModGetter.BeginWrite -> BinaryModdedWriteBuilderTargetChoice<T>)
        // is the WRITE entrypoint, not editable data — surfaced once the mod container is seeded. Filter by both the
        // builder type family and the BeginWrite accessor name.
        if (p.PropertyType.Name.StartsWith("BinaryModdedWriteBuilder", StringComparison.Ordinal)) return true;
        return p.Name switch
        {
            "BinaryWriteTranslator" => true,
            "Registration" => true,
            "BeginWrite" => true,
            "Type" when p.PropertyType == typeof(Type) => true,
            _ => false,
        };
    }

    static List<Type> FindUnionArms(Type getterIfc)
    {
        if (!getterIfc.IsInterface) return new List<Type>();
        WalkedAssemblies.Add(getterIfc.Assembly);   // derived, for the guard's neutrality universe
        var arms = new List<Type>();
        foreach (var t in getterIfc.Assembly.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || IsOverlayTwin(t) || !getterIfc.IsAssignableFrom(t)) continue;
            switch (ClassifyArm(t))
            {
                case ArmClass.Authorable:
                    arms.Add(t);
                    break;
                case ArmClass.WritableButUnextractable:
                    // #397's Q3 half. This candidate is dropped from the union with the same silence as a
                    // genuine read-only projection, but it is NOT one: it carries a writable surface and its
                    // absence is a real coverage hole. Name it. The drop itself is unchanged — the schema is
                    // deliberately NOT extracted from the concrete class (see ClassifyArm), so the catalog
                    // gains a named hole rather than one entry whose writability was computed differently
                    // from every other entry's.
                    if (ReportedUnextractable.Add(t))
                        Warnings.Add(UnextractableWarning("union arm", t));
                    break;
                case ArmClass.ReadOnlyProjection:
                    // Correctly excluded and already documented (SkyrimMultiModOverlay, MergedCellBlock).
                    // Deliberately silent: these recur on every single run, and warning on them would bury
                    // the WritableButUnextractable case above in permanent noise.
                    break;
            }
        }
        return arms;
    }

    /// <summary>
    /// Why a concrete union implementer is not an authorable arm — the distinction #397 was filed for.
    /// Both non-authorable answers are excluded identically (behavior is unchanged by this split); they are
    /// separated so the emitted anomaly list can tell a correct exclusion from a real coverage hole.
    /// </summary>
    internal enum ArmClass
    {
        /// <summary>Exposes a mutable interface — a legal arm.</summary>
        Authorable,
        /// <summary>A read-only PROJECTION of a real writable type (Mutagen's multi-mod overlay, the
        /// merged-cell view). Nothing is lost by excluding it — it can never be composed.</summary>
        ReadOnlyProjection,
        /// <summary>Has NO <c>I{Name}Getter</c> interface at all, yet carries a writable surface. Excluded
        /// for a reason that does not apply to it, so its exclusion is a real coverage gap — upstream's to
        /// close (a missing getter interface), ours to NAME.</summary>
        WritableButUnextractable,
    }

    /// <summary>
    /// Classify a concrete union implementer. <see cref="ArmClass.Authorable"/> means it exposes a mutable
    /// interface — the same computation as the emitted <see cref="TypeSchema.MutableInterface"/>: a getter
    /// interface whose mutable twin exists. Everything else is excluded from the union, exactly as before;
    /// this only tells the two REASONS for exclusion apart.
    ///
    /// A read-only PROJECTION of a real writable type — Mutagen's multi-mod overlay (SkyrimMultiModOverlay,
    /// an arm of SkyrimMod) and merged-cell view (MergedCellBlock, an arm of CellBlock) — implements only
    /// the getter side and exposes NO dedicated getter interface. Such a projection can never be composed,
    /// so it must not be presented as a legal arm. Filtering it drops the union below the &gt;1 threshold and
    /// the container reclassifies to a plain struct (SkyrimMod, CellBlock) — correct, since both are
    /// containers, not authorable unions.
    ///
    /// This is the STRUCTURAL twin of <see cref="IsOverlayTwin"/>'s name match: that catches the lazy
    /// "*BinaryOverlay" record twins by name; this catches every read-only projection by shape, regardless
    /// of name (the multi-mod overlay does NOT end in "Overlay"-by-itself; the merged-cell view has no
    /// "Overlay" in its name at all). It does NOT touch a 0-field MARKER arm (PackageTargetSelf,
    /// BookTeachesNothing) — a marker DOES implement its mutable interface, so it stays authorable. It also
    /// leaves a CONCRETE self-listing poly-base (APackageData, ScriptFragments, SimpleModel) in the raw arm
    /// set — those are authorable, so the &gt;1 count is preserved and the self-entry is stripped at emit by
    /// <see cref="EmittedArmNames"/>, exactly as before.
    ///
    /// THE #397 SPLIT. When a getter interface EXISTS, authorability is whether its mutable twin does; no
    /// twin means a read-only projection, the correct exclusion. When NO getter interface exists, the former
    /// <c>MutableInterfaceFor(GetterInterfaceFor(t) ?? t)</c> handed the concrete class to
    /// <see cref="MutableInterfaceFor"/>, whose <c>EndsWith("Getter")</c> test a concrete class never passes —
    /// so the answer was always "excluded", and "has no getter interface" became indistinguishable in the
    /// output from "is a read-only projection", though only the second is a correct reason to exclude. That
    /// conflation is the bug. Split here on whether the class actually carries writable state.
    /// </summary>
    internal static ArmClass ClassifyArm(Type t)
    {
        var gi = GetterInterfaceFor(t);

        // The pre-#397 predicate, reproduced LITERALLY — including the `?? t` fallback, which is load-bearing and
        // not the dead branch it looks like. A concrete class whose OWN name ends in "Getter"
        // (FormLinkNullableGetter`1, FormLinkOrIndexGetter`1 in Mutagen.Bethesda.Core) passes
        // MutableInterfaceFor's arity-stripped EndsWith test and resolves a mutable twin through the fallback,
        // so it was authorable before and must stay authorable. Computing this from `gi` alone silently flipped
        // both types; they are in the walk, because BuildCorpus seeds IPexFileGetter from that same assembly.
        if (MutableInterfaceFor(gi ?? t) != null) return ArmClass.Authorable;

        // Not authorable. Everything below only decides WHICH exclusion to report — never whether to exclude.
        if (gi != null) return ArmClass.ReadOnlyProjection;
        return HasWritableSurface(t) ? ArmClass.WritableButUnextractable : ArmClass.ReadOnlyProjection;
    }

    /// <summary>
    /// Whether a concrete class with no getter interface carries settable state.
    ///
    /// DIAGNOSTIC ONLY, and it decides only which of two ANOMALY LINES is printed — never whether a type is
    /// excluded. It counts against the CONCRETE CLASS, whereas every cataloged entry's writability is computed
    /// against a MUTABLE INTERFACE (see <see cref="ExtractType"/>). That difference is exactly why #397 rules
    /// out extracting such a type's schema. The count never reaches the catalog; it only sizes the anomaly.
    /// </summary>
    static bool HasWritableSurface(Type t) => WritableSurfaceCount(t).writable > 0;

    /// <summary>
    /// Public settable instance properties, INHERITED INCLUDED, over a concrete class.
    ///
    /// Deliberately NOT <see cref="CollectAllProperties"/>, which the emit path uses (<see cref="ExtractType"/>)
    /// and which this must not perturb: it reads DeclaredOnly and never walks BaseType, so a class inheriting
    /// its state would count zero here and take the deliberately-silent ReadOnlyProjection branch — the silent
    /// drop #397 exists to remove, in the direction that prints nothing. Keeping the fix classifier-local is why
    /// this walks itself instead of tightening the shared helper.
    ///
    /// "Public settable" is narrower than <c>PropertyInfo.CanWrite</c>, which is true for a private or init-only
    /// setter — neither of which can ever author anything, so counting them would inflate a number the reader is
    /// asked to act on.
    /// </summary>
    static (int writable, int total) WritableSurfaceCount(Type t)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int writable = 0, total = 0;
        for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var p in cur.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length != 0) continue;   // an indexer is not a named field
                if (IsInfrastructureProperty(p)) continue;
                if (!seen.Add(p.Name)) continue;                    // a derived override shadows its base
                total++;
                var setter = p.SetMethod;
                bool publicSettable = setter is { IsPublic: true } && !IsInitOnly(setter);
                if (publicSettable || IsMutableCollection(p.PropertyType)) writable++;
            }
        }
        return (writable, total);
    }

    /// <summary>An <c>init</c> accessor is a setter that can only run during object construction; it is encoded
    /// as a required custom modifier on the setter's return type.</summary>
    static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
              .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    /// <summary>
    /// The anomaly line for a type with no getter interface (#397 part 1).
    ///
    /// It states the MEASUREMENT and nothing else. It deliberately does NOT say the type is a real coverage
    /// gap, and does NOT say the fix belongs upstream in Mutagen — neither is established by "no
    /// <c>I{Name}Getter</c> resolved by name", and the second is provably false for at least one type that
    /// reaches this line: ArmorAddonWeightSliderContainer implements <c>IGenderedItemGetter&lt;bool&gt;</c> and
    /// its data ships in the reference today as <c>GenderedItem&lt;Boolean&gt;</c>, 2/2 writable. The name probe
    /// is <c>I{Name}Getter</c> built from <c>Type.Name</c>, which carries the generic arity, so a type whose
    /// getter interface is named differently — or is generic — resolves null here while being perfectly
    /// extractable. That is a real defect in <see cref="GetterInterfaceFor"/>, filed separately; until it is
    /// fixed this line must not diagnose, only report, and hand the reader the one check that settles it.
    ///
    /// Shared by the record path and the union-arm path so both name the same thing the same way.
    /// </summary>
    internal static string UnextractableWarning(string role, Type t)
    {
        var (w, total) = WritableSurfaceCount(t);
        var name = t.FullName ?? t.Name;   // FullName: ~350 distinct nested types share the simple name "ErrorMask"
        if (w == 0)
            return $"No I{t.Name}Getter resolved by name for {role} {name} — 0 of {total} public settable " +
                   $"instance properties, so nothing is lost by excluding it.";
        return $"UNEXTRACTABLE BY NAME: {role} {name} — no I{t.Name}Getter resolved by name, and {w} of {total} " +
               $"public settable instance properties. It is excluded from the catalog. Verify whether this " +
               $"type's data is reachable elsewhere in the catalogue (it may implement a differently-named " +
               $"getter interface) before treating this as a coverage gap.";
    }

    /// <summary>
    /// True for Mutagen's lazy-read overlay class (e.g. ArmorBinaryOverlay), which
    /// implements only the getter interface of its real twin. Not a distinct modeled
    /// type — excluded from record enumeration and from polymorphic-arm detection so
    /// it doesn't masquerade as a second "arm" of an otherwise non-polymorphic struct.
    /// </summary>
    internal static bool IsOverlayTwin(Type t) => t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal);

    /// <summary>
    /// A record's own xEdit 4-char signature (Armor -> "ARMO"), read from its registration's
    /// TriggeringRecordType static field. Verified identical to the concrete class's GrupRecordType
    /// across all 133 records (Probe.RunSig) — GrupRecordType is the defensive fallback if the
    /// registration shape ever shifts on a Mutagen bump.
    /// </summary>
    static string? RecordSignature(Type concrete)
    {
        try
        {
            var reg = concrete.GetProperty("StaticRegistration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var f = reg?.GetType().GetField("TriggeringRecordType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (f != null && f.GetValue(reg) is RecordType rt) return rt.ToString();
        }
        catch { /* fall through to GrupRecordType */ }
        var gf = concrete.GetField("GrupRecordType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return gf?.GetValue(null) is RecordType g ? g.ToString() : null;
    }

    /// <summary>The "I{Name}Getter" interface for a concrete Mutagen class.</summary>
    internal static Type? GetterInterfaceFor(Type concrete)
    {
        if (concrete.IsInterface) return concrete;
        var direct = concrete.Assembly.GetType($"{concrete.Namespace}.I{concrete.Name}Getter");
        if (direct != null) return direct;
        return concrete.GetInterfaces()
            .FirstOrDefault(i => i.Name == $"I{concrete.Name}Getter")
            ?? concrete.GetInterfaces().FirstOrDefault(i => i.Name.EndsWith("Getter") && Normalize(i.Name) == Normalize(concrete.Name));
    }

    /// <summary>The mutable twin of a getter interface (strip the "Getter" suffix).</summary>
    internal static Type? MutableInterfaceFor(Type getterIfc)
    {
        // Strip the arity suffix before the Getter check: a generic getter interface is
        // named "IFooGetter`1", which does NOT end in "Getter". Missing this silently
        // dropped the mutable interface for every generic type (e.g. IGenderedItem<T>),
        // marking its genuinely-settable fields read-only.
        var simpleName = getterIfc.Name.Split('`')[0];
        if (!simpleName.EndsWith("Getter")) return null;
        if (getterIfc.IsGenericType)
        {
            var def = getterIfc.GetGenericTypeDefinition();
            var defMutableName = StripGetter(def.Name);
            var defMutable = def.Assembly.GetType($"{def.Namespace}.{defMutableName}");
            if (defMutable == null) return null;
            // Prefer the closed mutable type (best for step 4's runtime resolution); if the
            // getter's type args don't satisfy the mutable definition's constraints, fall back
            // to the open definition — its properties carry the same names + CanWrite flags,
            // and writability is invariant to the type argument.
            try { return defMutable.MakeGenericType(getterIfc.GetGenericArguments()); }
            catch { return defMutable; }
        }
        var mutableName = StripGetter(getterIfc.Name);
        return getterIfc.Assembly.GetType($"{getterIfc.Namespace}.{mutableName}");
    }

    static Type? ResolveGetterInterface(Type t)
    {
        if (t.IsInterface) return t;            // already a getter (or other) interface
        return GetterInterfaceFor(t);           // concrete class -> its getter interface
    }

    static bool IsModeledStruct(Type t, out Type? getterIfc)
    {
        getterIfc = null;
        var ns = (t.Namespace ?? "");
        if (!(ns.StartsWith("Mutagen.Bethesda") || ns.StartsWith("Noggog"))) return false;
        if (t.IsEnum || t.IsPrimitive || t == typeof(string)) return false;
        var gi = ResolveGetterInterface(t);
        if (gi == null) return false;
        getterIfc = gi;
        return true;
    }

    // ---------------------------------------------------------------- naming

    /// <summary>The UNIQUE catalog key for an enum: nested enums (Activator+Flag) qualify with their declaring
    /// type (→ "Activator.Flag") so the ~90 distinct per-record "Flag" enums don't collapse to one bare-name
    /// entry; top-level enums (ActorValue) keep their bare name. Unwraps Nullable so a Skill? field and a Skill
    /// field resolve to the same key (project_enum_simple_name_collision).</summary>
    static string EnumCatalogName(Type enumType)
    {
        var u = Nullable.GetUnderlyingType(enumType) ?? enumType;
        return u.DeclaringType is { } dt ? $"{dt.Name}.{u.Name}" : u.Name;
    }

    static string CatalogName(Type t)
    {
        if (t.IsGenericType)
        {
            var basePart = Normalize(t.GetGenericTypeDefinition().Name);
            var args = string.Join(",", t.GetGenericArguments().Select(CatalogName));
            return $"{basePart}<{args}>";
        }
        return Normalize(t.Name);
    }

    static string Normalize(string name)
    {
        var bare = name.Split('`')[0];
        if (bare.Length > 1 && bare[0] == 'I' && char.IsUpper(bare[1])) bare = bare[1..];
        if (bare.EndsWith("Getter")) bare = bare[..^"Getter".Length];
        return bare;
    }

    static string StripGetter(string name)
    {
        var tick = name.IndexOf('`');
        var simple = tick >= 0 ? name[..tick] : name;
        if (simple.EndsWith("Getter")) simple = simple[..^"Getter".Length];
        return tick >= 0 ? simple + name[tick..] : simple;
    }

    static bool IsFormLink(string fullName) =>
        fullName.StartsWith("Mutagen.Bethesda.Plugins.IFormLink") ||
        fullName.StartsWith("Mutagen.Bethesda.Plugins.FormLink");

    static bool IsList(string fullName) =>
        fullName == "System.Collections.Generic.IReadOnlyList`1" ||
        fullName == "System.Collections.Generic.IList`1" ||
        fullName == "System.Collections.Generic.List`1" ||
        fullName == "System.Collections.Generic.IEnumerable`1" || // read-only sequence (e.g. AssetType.FileExtensions: IEnumerable<string>) — list, not a navigable struct
        fullName == "Noggog.IExtendedList`1" ||
        fullName == "Noggog.ExtendedList`1" ||
        // GRUP record-group containers (walked once the mod container is seeded). A group is modeled as a
        // list of its records — match the getter / mutable / concrete forms, since a getter property exposes
        // the GETTER interface (ISkyrimGroupGetter<IXGetter>); matching only the concrete form let the group
        // fall through to substruct handling and catalogue its internal plumbing (caches, registration, source-mod).
        fullName == "Mutagen.Bethesda.Skyrim.SkyrimGroup`1" ||
        fullName == "Mutagen.Bethesda.Skyrim.ISkyrimGroupGetter`1" ||
        fullName == "Mutagen.Bethesda.Skyrim.ISkyrimGroup`1" ||
        fullName == "Mutagen.Bethesda.Skyrim.SkyrimListGroup`1" ||
        fullName == "Mutagen.Bethesda.Skyrim.ISkyrimListGroupGetter`1" ||
        fullName == "Mutagen.Bethesda.Skyrim.ISkyrimListGroup`1";

    static bool IsDictionary(string fullName) =>
        fullName == "System.Collections.Generic.IReadOnlyDictionary`2" ||
        fullName == "System.Collections.Generic.IDictionary`2" ||
        fullName == "System.Collections.Generic.Dictionary`2";

    /// <summary>
    /// True if a get-only property of this type is still writable because the type is
    /// mutable in place — you change its contents (Add/Remove/index-set/SetTo) without
    /// replacing the property. The whitelist is the COMPLETE set of mutable-in-place
    /// shapes Mutagen uses on its mutable interfaces, extracted via the `vocab` probe
    /// over the whole corpus (so it is exhaustive for this Mutagen version, not guessed).
    /// The only get-only shapes deliberately excluded are the genuinely read-only ones:
    /// IReadOnlyList / IReadOnlyCache and computed get-only scalars.
    /// </summary>
    static readonly HashSet<string> MutableInPlaceDefs = new()
    {
        "Noggog.IExtendedList`1", "Noggog.ExtendedList`1",
        "Noggog.SliceList`1",
        "Noggog.IArray2d`1", "Noggog.Array2d`1",
        "Noggog.ICache`2", "Noggog.Cache`2",
        "System.Collections.Generic.IList`1", "System.Collections.Generic.List`1",
        "System.Collections.Generic.IDictionary`2", "System.Collections.Generic.Dictionary`2",
        "Mutagen.Bethesda.Plugins.IFormLink`1", "Mutagen.Bethesda.Plugins.IFormLinkNullable`1",
        "Mutagen.Bethesda.Skyrim.SkyrimGroup`1", "Mutagen.Bethesda.Skyrim.SkyrimListGroup`1",
    };

    static bool IsMutableCollection(Type t)
    {
        if (t.IsArray) return true; // fixed-size but element-mutable (e.g. CloudLayer[]); SetAtIndex applies
        if (!t.IsGenericType) return false;
        var name = t.GetGenericTypeDefinition().FullName ?? "";
        return MutableInPlaceDefs.Contains(name);
    }

    // ---------------------------------------------------------------- writability guard
    //
    // Companion to Probe's `vocab` check (which guards the mutable-COLLECTION-shape whitelist).
    // This guards the field-WRITABILITY surface: every field Mutagen models read-only falls into
    // one of the categories below, verified field-by-field 2026-05-30 against Mutagen 0.53.1 (8424/9087
    // writable; all 663 read-only accounted for, 0 residue) — a dated measurement, not a current count;
    // the guard below is dynamic and re-derives the residue on whatever version is referenced.
    // A non-writable field fitting NONE of them is a possible
    // regression — a content field that lost its setter via a Mutagen bump or a reflection miss (the
    // failure mode the generic-getter `1 bug was, CorpusGenerator §MutableInterfaceFor) — and is
    // surfaced loud in Report's anomaly list, never passed over silently (CLAUDE.md §3 fail-loud).
    //
    //   R0 identity          — FormKey / ModKey: record identity, read-only by design, any type.
    //   R1 no-mutable-iface  — the type exposes no mutable interface at all (read-only construct:
    //                          ReadOnlyArray2d<T> views, the AssetType descriptor). The read-only
    //                          projections (the multi-mod overlay, the merged-cell view) are filtered out
    //                          of arm detection upstream (ClassifyArm), so they never reach this audit.
    //   R2 poly-discriminator— a polymorphic-union arm's identity fixes the field, so it is get-only
    //                          (Condition.Function, archetype Type / AssociationKey, condition
    //                          Parameter{1,2}Type). Allowed on arm / polymorphic-base only, so the same
    //                          field name regressing to read-only on a normal record still trips.
    //   R3 variant-discrim.  — the same idea modeled as concrete structs/records: Region*.DataType,
    //                          Global*.TypeChar.
    //   R4 computed-carrier  — derived / computed properties on otherwise-writable carrier types:
    //                          SkyrimMod capability flags, TranslatedString runtime props, AssetLink
    //                          projections.
    static readonly HashSet<string> PolyDiscriminators = new(StringComparer.Ordinal)
    {
        "Function", "Type", "Parameter1Type", "Parameter2Type", "AssociationKey",
    };

    static bool IsExpectedReadOnly(TypeSchema t, FieldSchema f)
    {
        if (f.IsIdentity) return true;                                                       // R0
        if (t.MutableInterface == null) return true;                                         // R1
        if (PolyDiscriminators.Contains(f.Name) && t.Kind is "arm" or "polymorphic-base")
            return true;                                                                     // R2
        if (f.Name == "DataType" && t.Name.StartsWith("Region", StringComparison.Ordinal))
            return true;                                                                     // R3
        if (f.Name == "TypeChar" && t.Name.StartsWith("Global", StringComparison.Ordinal))
            return true;                                                                     // R3
        if (t.Name is "SkyrimMod" or "TranslatedString"
            || t.Name.StartsWith("AssetLink", StringComparison.Ordinal))
            return true;                                                                     // R4
        return false;
    }

    static void AuditWritability(Corpus c)
    {
        foreach (var t in c.Types.Values)
        {
            if (t.Kind == "enum") continue;
            foreach (var f in t.Fields)
            {
                if (f.Writable || IsExpectedReadOnly(t, f)) continue;
                Warnings.Add(
                    $"Unexpected read-only field {t.Name}.{f.Name} ({t.Kind}: {f.Cardinality} {f.Type}) — " +
                    "outside the known read-only categories (possible lost setter / reflection miss). " +
                    "If Mutagen legitimately added it, extend the IsExpectedReadOnly categories.");
            }
        }
    }

    static string SimpleName(Type t) => t.Name switch
    {
        "Boolean" => "bool",
        "Byte" => "byte",
        "SByte" => "sbyte",
        "Int16" => "short",
        "UInt16" => "ushort",
        "Int32" => "int",
        "UInt32" => "uint",
        "Int64" => "long",
        "UInt64" => "ulong",
        "Single" => "float",
        "Double" => "double",
        "Char" => "char",
        _ => t.Name,
    };

    /// <summary>Readable name for an atomic value type (mirrors Mutagen's own granularity).</summary>
    static string ValueTypeName(Type t)
    {
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition().Name.Split('`')[0];
            if (def is "ReadOnlyMemorySlice" or "MemorySlice") return "bytes";
            var args = string.Join(",", t.GetGenericArguments().Select(a => a.Name));
            return $"{def}<{args}>";
        }
        return t.Name;
    }

    static bool ImplementsByName(Type t, string ifaceName) =>
        t.Name == ifaceName || t.GetInterfaces().Any(i => i.Name == ifaceName);

    // ---------------------------------------------------------------- output

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static string BuildSummary(Corpus c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Mutagen Skyrim corpus — reflection-extracted schema");
        sb.AppendLine();
        sb.AppendLine($"- Assembly: `{c.MutagenAssembly}`");
        sb.AppendLine($"- Record types: **{c.RecordTypes}**");
        sb.AppendLine($"- Total types in catalog: **{c.TotalTypes}** (records + header + sub-structs + arms + enums)");
        sb.AppendLine();
        sb.AppendLine("## Catalog by kind");
        sb.AppendLine();
        foreach (var kv in c.KindCounts.OrderBy(k => k.Key)) sb.AppendLine($"- **{kv.Key}**: {kv.Value}");
        sb.AppendLine();

        // Field-level cardinality + writability rollup across the whole corpus.
        var allFields = c.Types.Values.SelectMany(t => t.Fields).ToList();
        sb.AppendLine("## Field cardinality (whole corpus)");
        sb.AppendLine();
        foreach (var g in allFields.GroupBy(f => f.Cardinality).OrderBy(g => g.Key))
            sb.AppendLine($"- **{g.Key}**: {g.Count()}");
        sb.AppendLine();
        sb.AppendLine($"- Writable fields: {allFields.Count(f => f.Writable)} / {allFields.Count}");
        sb.AppendLine();

        sb.AppendLine("## Types");
        sb.AppendLine();
        foreach (var t in c.Types.Values)
        {
            sb.AppendLine($"### {t.Name}  _({t.Kind})_");
            sb.AppendLine();

            // Enum entries (decision #6): listed once with their legal values; fields reference them by name.
            if (t.Kind == "enum")
            {
                sb.AppendLine($"- Values ({t.EnumValues?.Count ?? 0}): {string.Join(", ", t.EnumValues ?? new())}");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"- Getter: `{t.GetterInterface}`");
            if (t.MutableInterface != null) sb.AppendLine($"- Mutable: `{t.MutableInterface}`");
            else sb.AppendLine($"- Mutable: _(none — Mutagen exposes this type read-only)_");
            if (t.Signature != null) sb.AppendLine($"- Signature: `{t.Signature}`");
            if (t.AbstractBase != null) sb.AppendLine($"- Arm of: `{t.AbstractBase}`");
            if (t.Arms != null) sb.AppendLine($"- Arms: {string.Join(", ", t.Arms)}");
            sb.AppendLine($"- Writable: {t.WritableCount} / {t.FieldCount}");
            sb.AppendLine();
            sb.AppendLine("| Field | Type | Cardinality | Writable | Ref / Notes |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var f in t.Fields)
            {
                string notes =
                    f.Arms != null ? "arms: " + string.Join(", ", f.Arms) :
                    f.ElementArms != null ? "elem arms: " + string.Join(", ", f.ElementArms) :
                    f.TypeRef != null ? "-> " + f.TypeRef :
                    f.ElementTypeRef != null ? "elem -> " + f.ElementTypeRef :
                    f.FormLinkTarget != null ? "target: " + f.FormLinkTarget :
                    f.ElementType != null ? "elem: " + f.ElementType :
                    f.IsIdentity ? "identity" : "";
                var nul = f.Nullable ? "?" : "";
                sb.AppendLine($"| {f.Name} | {f.Type}{nul} | {f.Cardinality} | {(f.Writable ? "yes" : "no")} | {notes} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static void Report(Corpus c, string jsonPath, string sumPath, string refDir)
    {
        Console.WriteLine($"Wrote {jsonPath}");
        Console.WriteLine($"Wrote {sumPath}  (Aaron-readable spot-check summary)");
        Console.WriteLine($"Wrote {refDir}  (mutagen-reference skill tree: index.jsonl + per-kind shards)");
        Console.WriteLine();
        Console.WriteLine($"Record types : {c.RecordTypes}");
        Console.WriteLine($"Total types  : {c.TotalTypes}  (records + header + sub-structs + arms + enums)");
        Console.WriteLine();
        Console.WriteLine("By kind:");
        foreach (var kv in c.KindCounts.OrderBy(k => k.Key)) Console.WriteLine($"  {kv.Key,-18}: {kv.Value}");
        Console.WriteLine();

        var allFields = c.Types.Values.SelectMany(t => t.Fields).ToList();
        Console.WriteLine("Field cardinality (whole corpus):");
        foreach (var g in allFields.GroupBy(f => f.Cardinality).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key,-12}: {g.Count()}");
        Console.WriteLine($"  writable    : {allFields.Count(f => f.Writable)} / {allFields.Count}");
        Console.WriteLine();

        // Auto spot-checks for the hard cases.
        foreach (var name in new[] { "SkyrimModHeader", "MagicEffect", "Quest", "Armor" })
            Spotlight(c, name);

        if (Warnings.Count > 0)
        {
            Console.WriteLine($"ANOMALIES / things to inspect ({Warnings.Count}):");
            foreach (var w in Warnings.Take(40)) Console.WriteLine($"  - {w}");
            if (Warnings.Count > 40) Console.WriteLine($"  ... and {Warnings.Count - 40} more");
        }
        else
        {
            Console.WriteLine("No anomalies flagged.");
        }
    }

    static void Spotlight(Corpus c, string name)
    {
        if (!c.Types.TryGetValue(name, out var t)) { Console.WriteLine($"[spotlight] {name}: not in catalog"); Console.WriteLine(); return; }
        Console.WriteLine($"=== {t.Name} ({t.Kind}) — {t.WritableCount}/{t.FieldCount} writable ===");
        foreach (var f in t.Fields)
        {
            string extra =
                f.Arms != null ? $" arms[{f.Arms.Count}]-> {string.Join(", ", f.Arms.Take(4))}{(f.Arms.Count > 4 ? ", ..." : "")}" :
                f.TypeRef != null ? $" -> {f.TypeRef}" :
                f.ElementTypeRef != null ? $" elem-> {f.ElementTypeRef}" :
                f.FormLinkTarget != null ? $" target:{f.FormLinkTarget}" :
                f.IsIdentity ? " [identity]" : "";
            Console.WriteLine($"  {(f.Writable ? "w" : "-")} {f.Name,-26} {f.Cardinality,-12} {f.Type}{extra}");
        }
        Console.WriteLine();
    }
}

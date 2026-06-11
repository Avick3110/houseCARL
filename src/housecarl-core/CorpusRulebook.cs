using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// A write request in the engine's internal representation (plan §3 P-ADDR): a verb applied at the
/// leaf of a path from a record root. The string/JSON wire format a user types is a step-8 (MCP API)
/// concern — deliberately deferred; this is the engine's internal shape.
/// </summary>
public sealed class WriteRequest
{
    public required string RecordType { get; init; }   // catalog name, e.g. "Npc"
    public required string[] Path { get; init; }        // field hops from record root to the leaf
    public required string Verb { get; init; }          // Set / Add / Remove / ReplaceAll / SetAtIndex / Merge
    public string? Key { get; init; }                   // dict key or list index at the leaf
    public string? Value { get; init; }                 // the value, where the verb takes one
    public string[]? Values { get; init; }              // list ReplaceAll — the whole new contents
    public Dictionary<string, string>? Entries { get; init; } // dict ReplaceAll / Merge — key→value pairs
    public StructSpec? Struct { get; init; }            // build-from-parts spec: the arm for a polymorphic Set, OR the new element for a struct-element Add
}

/// <summary>
/// A modeled struct built FROM PARTS — the one composition primitive (wave-1 half B), generalizing the prior
/// polymorphic-arm builder. Used in three places, all the same shape: a <b>polymorphic Set</b> (<see cref="Type"/>
/// = the chosen arm), a <b>struct-element Add</b> to a collection (<see cref="Type"/> = the list's element type),
/// and absent-composition materialization. <see cref="Fields"/> is flat-leaf sugar (coercible scalar/enum/
/// formlink/value sub-fields, one Set-leaf each — the ergonomic arm shape, unchanged). <see cref="Sets"/> carries
/// the general NESTED writes (sub-structs, struct-element Adds, lists) applied to the freshly-built instance
/// through the verb engine ITSELF — so a built struct can never miss a field kind the engine already handles, and
/// the build recurses (a struct element whose own field is a struct element) for free.
/// </summary>
public sealed class StructSpec
{
    public required string Type { get; init; }                 // concrete catalog name (arm type, list element type, …)
    public Dictionary<string, string>? Fields { get; init; }   // flat coercible sub-field → value (sugar = a Set-leaf each)
    public string[]? CtorArgs { get; init; }                   // positional ctor args for discriminator-/composition-ctor types
    public List<WriteRequest>? Sets { get; init; }             // general nested writes applied to the built instance (paths rooted at it)
}

/// <summary>
/// The in-memory rulebook — <c>corpus.json</c> deserialised into the generator's own schema model,
/// used for <b>pre-flight validation</b> (plan §3 P-VALIDATE) before any Mutagen mutation. The schema
/// IS the validator data, by construction: corpus.json and this model come out of the same reflection
/// walk, so they cannot disagree about field names or types.
///
/// Q3 — no silent failure: every rejection names what was checked and what's legal.
/// </summary>
public sealed class CorpusRulebook
{
    readonly Corpus _corpus;
    CorpusRulebook(Corpus corpus) => _corpus = corpus;

    public int TypeCount => _corpus.TotalTypes;
    public TypeSchema? Type(string name) => _corpus.Types.GetValueOrDefault(name);

    /// <summary>The ONE source of truth for where corpus.json lives — every corpus read in the core
    /// resolves through it. Defaults to the dev-harness location ("generated/corpus.json", relative to
    /// the CWD, which is the repo root when the harness runs). The MCP server is launched by MO2 from an
    /// arbitrary working directory, so it MUST set this to an absolute path at startup (§8.4) — a hardcoded
    /// relative path can't survive the process/CWD change. (§8.4 tidy: collapsed the ~7 duplicated
    /// `Deserialize&lt;Corpus&gt;("generated/corpus.json")` copies onto this + the loaders below.)</summary>
    public static string CorpusPath { get; set; } = Path.Combine("generated", "corpus.json");

    /// <summary>Load the validator rulebook from the configured <see cref="CorpusPath"/>.</summary>
    public static CorpusRulebook Load() => new(LoadCorpus());

    /// <summary>Load the validator rulebook from an explicit path (the harness; tests).</summary>
    public static CorpusRulebook Load(string corpusJsonPath) => new(LoadCorpus(corpusJsonPath));

    /// <summary>The raw deserialised <see cref="Corpus"/> from the configured <see cref="CorpusPath"/> — for
    /// consumers that want the catalog model directly (the read engine's field-name lookup; the harness'
    /// coerce-audit / census / write-proof) rather than the validator wrapper.</summary>
    public static Corpus LoadCorpus() => LoadCorpus(CorpusPath);

    /// <summary>The raw deserialised <see cref="Corpus"/> from an explicit path. Fail-loud (Q3): a missing
    /// file or a null deserialise throws a named exception — never a silent empty corpus.</summary>
    public static Corpus LoadCorpus(string corpusJsonPath)
    {
        if (!File.Exists(corpusJsonPath))
            throw new FileNotFoundException(
                $"corpus.json not found at {Path.GetFullPath(corpusJsonPath)}. Generate it first: " +
                "dotnet run --project src/housecarl-generator");
        return JsonSerializer.Deserialize<Corpus>(File.ReadAllText(corpusJsonPath))
            ?? throw new InvalidOperationException("corpus.json deserialised to null.");
    }

    /// <summary>Pre-flight (P-VALIDATE order). Returns null if the write is legal, else a fail-loud message.</summary>
    public string? Validate(WriteRequest req)
    {
        // (1) resolve the record, then validate rooted at it. ValidateFromType is shared with StructSpec validation
        // (a build-from-parts spec's nested writes) so the record path and the composition path can never disagree.
        var recType = Type(req.RecordType);
        if (recType is null)
            return $"Unknown record type '{req.RecordType}': absent from the Mutagen corpus ({TypeCount} types). " +
                   "If Mutagen models it, that's a real coverage gap to surface — never a value to guess.";
        return ValidateFromType(recType, req);
    }

    /// <summary>Validate a write rooted at an arbitrary type — a record OR a struct being built from parts (so a
    /// <see cref="StructSpec"/>'s nested writes validate by the identical leaf/path rules, recursively).</summary>
    string? ValidateFromType(TypeSchema root, WriteRequest req)
    {
        if (req.Path.Length == 0)
            return "Empty path: a write must target at least one field.";

        // (2) walk the path, validating each intermediate hop's existence + descendability. A plain hop descends a
        // substruct; a bracketed hop (Effects[0]) steps INTO a collection element (wave-1 collection-nav).
        var current = root;
        for (int i = 0; i < req.Path.Length - 1; i++)
        {
            if (!TrySeg(req.Path[i], out var segName, out var segKey, out var segErr)) return segErr;
            var field = FindField(current, segName, out _, out var polyErr);
            if (polyErr is not null) return polyErr;
            if (field is null) return FieldNotFound(current, segName);

            if (segKey is null)
            {
                // plain hop — the only descendable kind is a substruct (with a non-record TypeRef).
                if (field.Cardinality == "substruct" && field.TypeRef is { } tr)
                {
                    var next = Type(tr);
                    if (next is null)
                        return $"Path hop '{segName}' on '{current.Name}' points to type '{tr}', absent from the corpus.";
                    current = next;
                }
                else
                    return $"Cannot descend through '{segName}' on '{current.Name}': it is a {field.Cardinality}, not a substruct. " +
                           $"(To step INTO a collection element, index it: '{segName}[<index/key>]'.)";
            }
            else
            {
                // bracketed hop — step into a collection element. Must be a list/dict whose element is a navigable
                // STRUCT; a record-element is resolved on its own (nested-group wave), never walked into from a parent.
                if (field.Cardinality is not ("list" or "dict"))
                    return $"'{segName}[{segKey}]' on '{current.Name}' indexes a {field.Cardinality}, which is not a collection.";
                if (field.ElementTypeRef is not { } er)
                    return $"'{segName}' on '{current.Name}' is a collection of scalar values, not navigable structs — " +
                           "edit its element at the leaf with the verb + Key, don't step into it.";
                var elem = Type(er);
                if (elem is null)
                    return $"'{segName}' element type '{er}' on '{current.Name}' is absent from the corpus.";
                if (elem.Kind == "record")
                    return $"'{segName}' on '{current.Name}' holds records ({er}); a record is resolved on its own, " +
                           "not reached by stepping into a parent (nested-group wave).";
                if (field.Cardinality == "list" && !int.TryParse(segKey, out _))
                    return $"List '{segName}' on '{current.Name}' must be indexed by an integer; got '{segKey}'.";
                if (field.Cardinality == "dict" && CheckValue(field.KeyType, segKey, $"dict key for '{segName}'") is { } ke)
                    return ke;
                current = elem;
            }
        }

        // (3) the leaf field — a bracketed LEAF is rejected (brackets navigate mid-path only; the leaf uses Key).
        if (!TrySeg(req.Path[^1], out var leafName, out var leafKey, out var leafErr)) return leafErr;
        if (leafKey is not null)
            return $"Path '{req.Path[^1]}' brackets a collection element at the LEAF; brackets navigate mid-path only. " +
                   "To edit a list/dict element, target the collection field and use the verb + Key (SetAtIndex/Set/Remove).";
        var leaf = FindField(current, leafName, out var leafOwner, out var leafPolyErr);
        if (leafPolyErr is not null) return leafPolyErr;
        if (leaf is null) return FieldNotFound(current, leafName);

        // (3a) verb legal for this cardinality?
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // (3b) record identity (FormKey/ModKey) is a flat, honest reject regardless of Mutagen's setter (plan §3 P-DISC).
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{leafOwner.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // (3c) writable? (P-DISC routing for discriminators)
        if (!leaf.Writable) return WritabilityRejection(leafOwner, leaf);

        // (4) value / key coercion + enum/legal-set legality
        return ValueLegality(leaf, req);
    }

    // ---- verb × cardinality (plan §3 P-VERBS) ----
    static string? VerbLegality(FieldSchema leaf, WriteRequest req)
    {
        var c = leaf.Cardinality;
        var hasKey = req.Key is not null;
        switch (req.Verb)
        {
            case "Set":
                if (c == "dict") return hasKey ? null : $"Set on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return $"Set is not valid on list '{leaf.Name}' — use SetAtIndex (with an index) or ReplaceAll.";
                return hasKey ? $"Set on {c} field '{leaf.Name}' does not take a key." : null;
            case "Add":
                return c is "list" or "dict" ? null : $"Add is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "Remove":
                if (c is "list" or "dict") return null;
                return leaf.Nullable ? null : $"Remove on non-nullable {c} field '{leaf.Name}' is not valid.";
            case "ReplaceAll":
                return c is "list" or "dict" ? null : $"ReplaceAll is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "SetAtIndex":
                return c == "list" ? null : $"SetAtIndex is only valid on list; '{leaf.Name}' is {c}.";
            case "Merge":
                return c == "dict" ? null : $"Merge is only valid on dict; '{leaf.Name}' is {c}.";
            default:
                return $"Unknown verb '{req.Verb}'. Legal: Set, Add, Remove, ReplaceAll, SetAtIndex, Merge.";
        }
    }

    // ---- writability rejection (plan §3 P-DISC) ----
    static string WritabilityRejection(TypeSchema owner, FieldSchema leaf)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not an editable content field.";
        if (leaf.Cardinality == "polymorphic" && leaf.Arms is { Count: > 0 } arms)
            return $"'{leaf.Name}' on '{owner.Name}' is fixed by which arm is selected. To change it, Set '{leaf.Name}' " +
                   $"to one of its arms: {string.Join(", ", arms)}.";
        if (owner.Kind is "arm" or "polymorphic-base")
            return $"'{leaf.Name}' is a discriminator on '{owner.Name}' — its value is fixed by which arm is selected. " +
                   "To change it, Set the parent polymorphic field to a different arm (P-DISC).";
        return $"'{leaf.Name}' on '{owner.Name}' is not writable — Mutagen exposes no setter (computed / discriminator / " +
               "no-mutable-interface). houseCARL faithfully reports Mutagen's writability; this is not a houseCARL gap.";
    }

    // ---- value / key legality ----
    string? ValueLegality(FieldSchema leaf, WriteRequest req)
    {
        if (req.Verb is "Set" && leaf.Cardinality == "dict")
        {
            if (CheckValue(leaf.KeyType, req.Key, $"dict key for '{leaf.Name}'") is { } ke) return ke;
            if (req.Value is null) return $"Set on dict '{leaf.Name}' requires a value.";
            return CheckValue(leaf.ElementType, req.Value, $"dict value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
        }
        if (req.Verb is "Set" && leaf.Cardinality == "polymorphic")
            return ArmLegality(leaf, req.Struct);
        if (req.Verb is "Set")
        {
            if (req.Value is null) return $"Set on '{leaf.Name}' requires a value.";
            // formlink / substruct-whole: the engine must be able to coerce the leaf's whole type. A normal formlink
            // coerces; a condition FormLinkOrIndex is handled by the parent-aware SetFloi branch (wave 4) — validate
            // its target-value SHAPE here; a non-string substruct still rejects honestly (so pre-flight never
            // accepts-then-throws). FLOI is recognised via the engine's shared IsFormLinkOrIndex (no drift).
            if (leaf.Cardinality is "formlink" or "substruct")
            {
                var faq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
                if (WriteEngine.ResolveType(faq) is { } frt && WriteEngine.IsFormLinkOrIndex(frt))
                    return WriteEngine.TryClassifyFloiValue(req.Value) ? null
                        : $"Illegal condition target '{req.Value}' for '{leaf.Name}': expected a FormID " +
                          "(XXXXXX:Plugin.esp → form mode), a bare index, or 'alias N' / 'packdata N' (→ index mode).";
                return CoercibilityReject(leaf);
            }
            return CheckValue(leaf.Type, req.Value, $"value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // (step 4) collection-verb value legality. A struct-element OR arm-element list takes a build-from-parts
        // StructSpec on Add — NOT a plain value — which is wave-1 half B composition (an ARM element composes by its
        // concrete arm type, validated against that arm's own schema — the VMAD shape, #35; before this, arm-element
        // Adds fell through pre-flight UNVALIDATED and only failed at runtime — an accept-then-throw Q3 hole). A
        // coercible-element list takes a plain value the engine coerces (proven by the collection waves).
        if (leaf.Cardinality is "list" or "dict" && IsComposableElement(leaf))
        {
            // Composition lands through the LIST Add only — the engine's dict Add takes a coercible key + value
            // and ignores a spec (ApplyDictVerb), so admitting a dict compose here would be accept-then-throw
            // (PR review). Named deferred surface instead, never a runtime surprise.
            if (req.Verb == "Add")
                return leaf.Cardinality == "dict"
                    ? $"'{leaf.Name}' is a dict of modeled elements ({leaf.ElementTypeRef}); dict-element " +
                      "composition is a later surface — surfaced here, never accepted and thrown at apply time."
                    : StructElementLegality(leaf, req.Struct);
            if (req.Verb is "ReplaceAll" or "SetAtIndex")
                return $"'{leaf.Name}' holds modeled-struct elements ({leaf.ElementTypeRef}); only Add " +
                       "(build-from-parts) composes struct elements — ReplaceAll/SetAtIndex of structs is a later surface.";
        }
        return null;
    }

    /// <summary>True iff the leaf is a collection whose ELEMENT is built FROM PARTS on Add (so Add takes a
    /// StructSpec): a modeled-struct element, or a polymorphic-union (arm) element composed by its concrete arm
    /// type. Record-elements (nested-group wave) are resolved on their own axis, and a WHOLE-COERCIBLE element
    /// (an AssetLink path) is set as one value — both fall through to the plain-value Add path, never demanding
    /// a spec. Derived via the shared <see cref="SchemaClassifier"/> so the partition cannot be defined twice.</summary>
    bool IsComposableElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.Struct or ElementKind.Arm;

    /// <summary>Validate a struct-element Add: the spec must be present, its type must match the list's element
    /// type — or, when the element type is a <b>polymorphic-base</b>, be one of its ARMS (the VMAD shape, #35:
    /// <c>ScriptEntry.Properties</c> is a list of the base <c>ScriptProperty</c>, but a real new element is a
    /// concrete arm like <c>ScriptObjectProperty</c>) — and its contents must validate against the SPEC's own
    /// schema (the arm's fields, not the base's), recursively via the shared validator. Generic over every
    /// polymorphic-base element family — no per-type wiring (cornerstone).</summary>
    string? StructElementLegality(FieldSchema leaf, StructSpec? spec)
    {
        if (spec is null)
            return $"Add to struct-element collection '{leaf.Name}' requires a build-from-parts spec (the new element).";
        var er = leaf.ElementTypeRef!;
        var elemSchema = Type(er);
        if (elemSchema is null) return $"Element type '{er}' for '{leaf.Name}' absent from corpus.";

        TypeSchema specSchema;
        if (spec.Type == er) specSchema = elemSchema;
        else if (elemSchema is { Kind: "polymorphic-base", Arms: { Count: > 0 } arms } && arms.Contains(spec.Type))
            specSchema = Type(spec.Type)
                ?? throw new InvalidOperationException($"Arm '{spec.Type}' of '{er}' is listed but absent from the corpus — regenerate corpus.json.");
        else
        {
            var legal = elemSchema is { Kind: "polymorphic-base", Arms: { Count: > 0 } a }
                ? $" Legal element types: {string.Join(", ", a)}." : "";
            return $"Element spec type '{spec.Type}' does not match '{leaf.Name}' element type '{er}'.{legal}";
        }
        return StructSpecContents(spec, specSchema);
    }

    /// <summary>Validate a build-from-parts spec's CONTENTS against its declared struct type: flat <see cref="StructSpec.Fields"/>
    /// must exist + coerce; nested <see cref="StructSpec.Sets"/> validate by the identical path/leaf rules (recursively,
    /// through <see cref="ValidateFromType"/>). Shared by the polymorphic-arm Set and the struct-element Add so the two
    /// composition entry points can never disagree.</summary>
    string? StructSpecContents(StructSpec spec, TypeSchema structSchema)
    {
        foreach (var f in spec.Fields ?? new())
        {
            var af = structSchema.Fields.FirstOrDefault(x => x.Name == f.Key);
            if (af is null) return FieldNotFound(structSchema, f.Key);
            if (CheckValue(af.Type, f.Value, $"'{f.Key}' on '{spec.Type}'",
                    af.MutableTypeAssemblyQualified ?? af.GetterTypeAssemblyQualified) is { } e) return e;
        }
        foreach (var s in spec.Sets ?? new())
            if (ValidateFromType(structSchema, s) is { } e) return e;
        return null;
    }

    /// <summary>Honest reject when the engine cannot coerce a formlink/substruct leaf's whole type — a deferred
    /// typed-value target (e.g. a condition FormLinkOrIndex) or a substruct that must be navigated into, not Set.</summary>
    static string? CoercibilityReject(FieldSchema leaf)
    {
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return null; // unresolvable -> let the engine try (it fails loud)
        if (WriteEngine.CanCoerce(rt)) return null;                 // normal formlink etc. -> accept
        if (leaf.Cardinality == "substruct")
            return $"'{leaf.Name}' is a {leaf.TypeRef ?? leaf.Type} substruct — a direct Set isn't supported; " +
                   "navigate into it and Set a sub-field.";
        return $"'{leaf.Name}' ({leaf.Type}) needs a typed-value spec, not a plain value (e.g. a condition " +
               "FormLinkOrIndex target). Known deferred surface — surfaced, never silently accepted.";
    }

    /// <summary>Validate a polymorphic Set: the arm must be a legal arm of the field, and its contents (flat fields +
    /// nested sets) must validate against the arm type — the same composition-contents check a struct-element Add uses.</summary>
    string? ArmLegality(FieldSchema leaf, StructSpec? arm)
    {
        if (arm is null) return $"Set on polymorphic field '{leaf.Name}' requires an arm (which arm + its data).";
        var legal = leaf.Arms ?? (leaf.TypeRef is { } tr ? Type(tr)?.Arms : null) ?? new();
        if (!legal.Contains(arm.Type))
            return $"Illegal arm '{arm.Type}' for '{leaf.Name}'. Legal arms: {string.Join(", ", legal)}.";
        var armSchema = Type(arm.Type);
        if (armSchema is null) return $"Arm '{arm.Type}' absent from corpus.";
        return StructSpecContents(arm, armSchema);
    }

    /// <summary>Enum → must be a legal value of the field's REAL enum type; primitive → must coerce to the
    /// AQ-resolved type. Validation prefers the per-field assembly-qualified type, NOT the corpus's simple-name
    /// catalog key: many record-specific enums share simple names ("Flags", "MajorFlags", "Type", …) and a
    /// by-name catalog COLLIDES on them, so a name lookup can return a different enum's legal set entirely. The
    /// per-field AQ is unambiguous. (The corpus catalog by simple name is the fallback only when AQ won't
    /// resolve, and is flagged as best-effort — never silently trusted under a known collision.)</summary>
    string? CheckValue(string? typeName, string? value, string what, string? aq = null)
    {
        if (value is null) return $"Missing {what}.";

        if (aq is not null && WriteEngine.ResolveType(aq) is { } rt)
        {
            var u = Nullable.GetUnderlyingType(rt) ?? rt;
            if (u.IsEnum)
            {
                if (Enum.GetNames(u).Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase))) return null;
                if (WriteEngine.TryCoerce(value, rt, out _)) return null; // numeric / flags-combined the runtime accepts
                return $"Illegal {what}: '{value}' is not a legal {u.Name} value. Legal: {string.Join(", ", Enum.GetNames(u))}.";
            }
            if (!WriteEngine.TryCoerce(value, rt, out _))
                return $"Illegal {what}: '{value}' does not coerce to {typeName ?? u.Name}.";
            return null;
        }

        // Fallback: AQ unresolvable — lean on the catalog by simple name (may be ambiguous for colliding enum names).
        if (typeName is null) return null;
        if (Type(typeName) is { Kind: "enum", EnumValues: { } legal }
            && !legal.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
            return $"Illegal {what}: '{value}' is not a legal {typeName} value. Legal: {string.Join(", ", legal)}.";
        return null;
    }

    /// <summary>Parse a path segment via the engine's single parser (so the validator and the engine never
    /// disagree about path syntax), converting a malformed-bracket throw into a clean pre-flight reject string.</summary>
    static bool TrySeg(string segment, out string name, out string? key, out string? error)
    {
        try { (name, key) = WriteEngine.ParseSegment(segment); error = null; return true; }
        catch (Exception ex) { name = segment; key = null; error = ex.Message; return false; }
    }

    string FieldNotFound(TypeSchema owner, string name)
    {
        var sample = owner.Fields.Select(f => f.Name).Take(12).ToList();
        var more = owner.Fields.Count > sample.Count ? $", … (+{owner.Fields.Count - sample.Count} more)" : "";
        var arms = owner is { Kind: "polymorphic-base", Arms.Count: > 0 }
            ? $" Also searched its arms ({string.Join(", ", owner.Arms!.Where(a => a != owner.Name))})."
            : "";
        return $"No field '{name}' on '{owner.Name}'. Fields: {string.Join(", ", sample)}{more}.{arms}";
    }

    /// <summary>Find <paramref name="name"/> on <paramref name="owner"/>, looking through a <b>polymorphic-base</b>'s
    /// ARMS when the base itself lacks it — the VMAD shape (#35): <c>ScriptEntry.Properties</c> is modeled as a list
    /// of the base <c>ScriptProperty</c> (Name/Flags only), but every REAL element is a concrete arm
    /// (<c>ScriptObjectProperty</c> carries Object/Alias), so a path like <c>Properties[0].Object</c> is legal even
    /// though the BASE schema lacks 'Object'. Generic over every polymorphic-base family — no per-type wiring
    /// (cornerstone). The static validator cannot know WHICH arm sits at a given index, so: a name found on arms
    /// must AGREE in shape across all the arms that declare it (one shape validates for whichever arm the element
    /// turns out to be — the engine then resolves on the element's RUNTIME type and fails loud on a real mismatch);
    /// arms that DISAGREE reject named (Q3), never guess. <paramref name="effectiveOwner"/> is the schema the found
    /// field belongs to (the arm for an arm-found field), so downstream messages name the real owner.</summary>
    FieldSchema? FindField(TypeSchema owner, string name, out TypeSchema effectiveOwner, out string? error)
    {
        effectiveOwner = owner; error = null;
        if (owner.Fields.FirstOrDefault(f => f.Name == name) is { } direct) return direct;
        if (owner is not { Kind: "polymorphic-base", Arms.Count: > 0 }) return null;

        var hits = new List<(TypeSchema arm, FieldSchema field)>();
        foreach (var armName in owner.Arms!)
        {
            if (armName == owner.Name) continue;                       // the base lists itself as an arm; already checked
            if (Type(armName) is not { } arm)
            {
                // Arms and the catalog come out of the same reflection walk, so a listed-but-absent arm is a real
                // corpus defect — surfaced loud (Q3), never skipped: skipping could fake shape-agreement over an
                // incomplete arm set, or fake "no such field" for a field the missing arm exclusively declares.
                error = $"Arm '{armName}' of polymorphic-base '{owner.Name}' is listed but ABSENT from the corpus — " +
                        "corpus.json is stale or incompletely generated; regenerate it (dotnet run --project src/housecarl-generator).";
                return null;
            }
            if (arm.Fields.FirstOrDefault(f => f.Name == name) is { } af) hits.Add((arm, af));
        }
        if (hits.Count == 0) return null;

        var (firstArm, firstField) = hits[0];
        foreach (var (arm, f) in hits.Skip(1))
            if (!SameShape(firstField, f))
            {
                error = $"Field '{name}' exists on several arms of '{owner.Name}' with CONFLICTING shapes " +
                        $"('{firstArm.Name}': {firstField.Cardinality} {firstField.Type} vs '{arm.Name}': {f.Cardinality} {f.Type}) — " +
                        "the validator cannot pick one statically. Read the element first to learn its concrete arm, " +
                        "then target a field whose shape is unambiguous.";
                return null;
            }
        effectiveOwner = firstArm;
        return firstField;
    }

    /// <summary>Two arm declarations of the same field name agree iff every navigation/validation-relevant facet
    /// matches — cardinality, display + referenced types, element type, writability, AND the assembly-qualified
    /// CLR types (two arms can share a display name like 'Flags' while binding DIFFERENT enum types; ValueLegality
    /// validates against the AQ-resolved type, so AQ disagreement means the value would be checked against the
    /// wrong arm's legal set — PR review). Identity by what the validator USES, so "agrees" can never silently
    /// mean "close enough".</summary>
    static bool SameShape(FieldSchema a, FieldSchema b) =>
        a.Cardinality == b.Cardinality && a.Type == b.Type && a.TypeRef == b.TypeRef
        && a.ElementType == b.ElementType && a.ElementTypeRef == b.ElementTypeRef
        && a.Writable == b.Writable && a.Nullable == b.Nullable && a.IsIdentity == b.IsIdentity
        && a.GetterTypeAssemblyQualified == b.GetterTypeAssemblyQualified
        && a.MutableTypeAssemblyQualified == b.MutableTypeAssemblyQualified
        && a.ElementTypeAssemblyQualified == b.ElementTypeAssemblyQualified;
}

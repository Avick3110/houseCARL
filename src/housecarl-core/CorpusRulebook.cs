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
            var field = current.Fields.FirstOrDefault(f => f.Name == segName);
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
        var leaf = current.Fields.FirstOrDefault(f => f.Name == leafName);
        if (leaf is null) return FieldNotFound(current, leafName);

        // (3a) verb legal for this cardinality?
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // (3b) record identity (FormKey/ModKey) is a flat, honest reject regardless of Mutagen's setter (plan §3 P-DISC).
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{current.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // (3c) writable? (P-DISC routing for discriminators)
        if (!leaf.Writable) return WritabilityRejection(current, leaf);

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
        // (step 4) collection-verb value legality. A struct-element list (modeled-struct elements) takes a
        // build-from-parts StructSpec on Add — NOT a plain value — which is wave-1 half B composition; a
        // coercible-element list takes a plain value the engine coerces (proven by the collection waves).
        if (leaf.Cardinality is "list" or "dict" && IsStructElement(leaf))
        {
            if (req.Verb == "Add") return StructElementLegality(leaf, req.Struct);
            if (req.Verb is "ReplaceAll" or "SetAtIndex")
                return $"'{leaf.Name}' holds modeled-struct elements ({leaf.ElementTypeRef}); only Add " +
                       "(build-from-parts) composes struct elements — ReplaceAll/SetAtIndex of structs is a later surface.";
        }
        return null;
    }

    /// <summary>True iff the leaf is a collection whose ELEMENT is a BUILD-FROM-PARTS modeled struct (so Add takes a
    /// StructSpec). Record-elements (nested-group wave) and arm-elements (arm wave) are NOT structs; and a
    /// WHOLE-COERCIBLE element (an AssetLink path) is set as one value, not composed — so it falls through to the
    /// plain-value Add path, never demanding a spec.</summary>
    bool IsStructElement(FieldSchema leaf) => SchemaClassifier.IsStructElement(leaf, _corpus);

    /// <summary>Validate a struct-element Add: the spec must be present, its type must match the list's element
    /// type, and its contents must validate against that element type (recursively, via the shared validator).</summary>
    string? StructElementLegality(FieldSchema leaf, StructSpec? spec)
    {
        if (spec is null)
            return $"Add to struct-element collection '{leaf.Name}' requires a build-from-parts spec (the new element).";
        var er = leaf.ElementTypeRef!;
        if (spec.Type != er)
            return $"Element spec type '{spec.Type}' does not match '{leaf.Name}' element type '{er}'.";
        var elemSchema = Type(er);
        if (elemSchema is null) return $"Element type '{er}' for '{leaf.Name}' absent from corpus.";
        return StructSpecContents(spec, elemSchema);
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

    static string FieldNotFound(TypeSchema owner, string name)
    {
        var sample = owner.Fields.Select(f => f.Name).Take(12).ToList();
        var more = owner.Fields.Count > sample.Count ? $", … (+{owner.Fields.Count - sample.Count} more)" : "";
        return $"No field '{name}' on '{owner.Name}'. Fields: {string.Join(", ", sample)}{more}.";
    }
}

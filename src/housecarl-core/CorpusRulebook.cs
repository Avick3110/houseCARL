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
    public IReadOnlyList<StructSpec>? Structs { get; init; } // P8a: a LIST of build-from-parts elements (composes=) — Add appends each, ReplaceAll clears then appends each
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

    /// <summary>The legal shapes of a condition FormLinkOrIndex target value — shared by the nested-sets
    /// (<see cref="ValidateFromType"/>) and the flat-fields (<see cref="CheckValue"/>) rejects so the two compose entry
    /// points can't drift on what they tell the user is legal (the no-drift thesis of HCBR-2026-06-22).</summary>
    const string FloiTargetForms =
        "a FormID (XXXXXX:Plugin.esp → form mode), a bare index, or 'alias N' / 'packdata N' (→ index mode)";

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

    /// <summary>Pre-flight (P-VALIDATE order). Returns null if the write is legal, else a fail-loud message.
    /// <paramref name="siblingEditorIds"/> (non-null only on the CREATE-batch path) is the set of editorids created
    /// EARLIER in the same call PLUS the record being created itself (self-reference — HCBR-2026-07-10-01: a quest's
    /// VMAD fragment points at its own quest): a FormLink value of the form <c>@editorid</c> in that set is accepted
    /// as a forward-ref the create path resolves post-allocation (HCBR Layer B unit A). The set threads into composed
    /// StructSpec Fields/Sets too. Null (the override/set_field path) ⇒ an <c>@editorid</c> value is rejected loud —
    /// it has no meaning when there are no same-call creations.</summary>
    public string? Validate(WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // (1) resolve the record, then validate rooted at it. ValidateFromType is shared with StructSpec validation
        // (a build-from-parts spec's nested writes) so the record path and the composition path can never disagree.
        var recType = Type(req.RecordType);
        if (recType is null)
            return $"Unknown record type '{req.RecordType}': absent from the Mutagen corpus ({TypeCount} types). " +
                   "If Mutagen models it, that's a real coverage gap to surface — never a value to guess.";
        return ValidateFromType(recType, req, siblingEditorIds);
    }

    /// <summary>Validate a write rooted at an arbitrary type — a record OR a struct being built from parts (so a
    /// <see cref="StructSpec"/>'s nested writes validate by the identical leaf/path rules, recursively).</summary>
    string? ValidateFromType(TypeSchema root, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
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
                // plain hop — descend a substruct, OR a STANDALONE polymorphic field (NpcConfiguration.Level,
                // Npc.Sound, DialogResponsesAdapter.ScriptFragments). The poly case descends to the polymorphic-BASE
                // catalog entry (field.TypeRef); FindField's existing over-arms search (below) then resolves the next
                // hop against the base's arms — the standalone twin of the list-element poly branch (#35), keyed on
                // cardinality, no per-type wiring. The static validator can't know WHICH live arm sits here, so apply
                // resolves on the element's RUNTIME type and fails loud on a real arm mismatch (Q3) — the identical
                // accepted contract the list-element branch already carries.
                if (field.Cardinality == "substruct" && field.TypeRef is { } tr)
                {
                    var next = Type(tr);
                    if (next is null)
                        return $"Path hop '{segName}' on '{current.Name}' points to type '{tr}', absent from the corpus.";
                    current = next;
                }
                else if (field.Cardinality == "polymorphic" && field.TypeRef is { } ptr)
                {
                    var next = Type(ptr);
                    if (next is null)
                        return $"Path hop '{segName}' on '{current.Name}' points to polymorphic-base '{ptr}', absent from the corpus.";
                    current = next;
                }
                else
                    return $"Cannot descend through '{segName}' on '{current.Name}': it is a {field.Cardinality}, not a substruct. " +
                           $"(To step INTO a collection element, index it: '{segName}[<index/key>]'.)";
            }
            else
            {
                // Gendered field ([0]=male / [1]=female): a substruct whose TypeRef is GenderedItem<T>. The named arms
                // (.Male/.Female) descend as plain hops today; [0]/[1] is the render-matching navigable alias (HCBR
                // PR-H). Corpus-side recogniser = the "GenderedItem<" TypeRef; the engine's twin is the runtime
                // IGenderedItem<> in WriteEngine.StepIntoElement — two recognisers that must agree. Descends to the arm
                // type T so the next hop validates against the arm's own fields (a scalar/value arm has none → loud).
                if (field.Cardinality == "substruct" && field.TypeRef is { } gtr
                    && gtr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                {
                    if (segKey is not ("0" or "1"))
                        return $"Gendered field '{segName}' on '{current.Name}' is indexed by [0] (male) or [1] (female); got '{segKey}'. " +
                               $"(Its halves are also reachable by name: '{segName}.Male' / '{segName}.Female'.)";
                    var armRef = GenderedArmRef(gtr);
                    var armType = armRef is null ? null : Type(armRef);
                    if (armType is null)
                        return $"'{segName}[{segKey}]' on '{current.Name}' steps into a gendered scalar/value arm ('{armRef}'), which has " +
                               $"no sub-fields to navigate — set its halves by name ('{segName}.Male' / '{segName}.Female').";
                    current = armType;
                    continue;
                }

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
                // list mid-path index SHAPE — reconcile onto the SAME recognizer the LEAF step-4-key block uses
                // (WriteEngine.IsValidListIndexValue: a parseable NON-NEGATIVE int32), so the mid-path hop and the leaf
                // can't drift. A bare int.TryParse ACCEPTS a negative ('-1' parses), but apply's StepIntoElement list
                // branch requires idx >= 0 and throws a PLAIN InvalidOperationException (a SHAPE error, deliberately not
                // an ExpectedApplyRejection) → the misleading "real inconsistency" wrapper. Gating >= 0 here closes that
                // accept-then-throw, exactly as the leaf index was reconciled (the in-range bound stays apply's job, Q3).
                if (field.Cardinality == "list" && !WriteEngine.IsValidListIndexValue(segKey))
                    return $"List '{segName}' on '{current.Name}' must be indexed by a non-negative integer; got '{segKey}'.";
                // dict mid-path key SHAPE — reconcile onto the SAME recognizer pair (DictKeyType + CheckValue's AQ
                // branch) the PR #79 LEAF step-4-key block uses, so the mid-path hop and the leaf can't drift. Without
                // the key's real CLR type (DictKeyType -> the dict AQ's args[0], the type apply keys on via
                // StepIntoElement/ApplyDictVerb) CheckValue falls to the enum-catalog-by-name fallback and MISSES a
                // non-enum key — e.g. Package.Data's sbyte key ('Data[notasbyte]') was accepted then threw
                // FormatException at apply (StepIntoElement -> Coerce(key, sbyte)).
                if (field.Cardinality == "dict"
                    && CheckValue(field.KeyType, segKey, $"dict key for '{segName}'", DictKeyType(field)?.AssemblyQualifiedName) is { } ke)
                    return ke;
                current = elem;
            }
        }

        // (3) the leaf field — a bracketed LEAF is rejected (brackets navigate mid-path only; the leaf uses Key).
        if (!TrySeg(req.Path[^1], out var leafName, out var leafKey, out var leafErr)) return leafErr;
        if (leafKey is not null)
        {
            // A gendered field bracketed at the LEAF (Set Priority[0]) is NOT a list/dict — the renderer SHOWS [0]/[1]
            // but the halves are reached/set BY NAME, so point at .Male/.Female, not the list-verb message below (which
            // would mis-route the user to SetAtIndex/Set/Remove + Key on a field that takes none of them). Same corpus-
            // side "GenderedItem<" recogniser as the mid-path hop above; its engine twin is WriteEngine.GenderedInterface
            // in ApplyVerb's leaf throw — two recognisers that must agree. (HCBR-2026-06-15-01 PR-H follow-up: the leaf
            // seam of the mid-path scalar hint.)
            if (FindField(current, leafName, out _, out _) is { Cardinality: "substruct", TypeRef: { } ltr }
                && ltr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                return $"Gendered field '{leafName}' on '{current.Name}' renders as [0]/[1] but is not a list — set its " +
                       $"halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).";
            return $"Path '{req.Path[^1]}' brackets a collection element at the LEAF; brackets navigate mid-path only. " +
                   "To edit a list/dict element, target the collection field and use the verb + Key (SetAtIndex/Set/Remove).";
        }
        var leaf = FindField(current, leafName, out var leafOwner, out var leafPolyErr);
        if (leafPolyErr is not null) return leafPolyErr;
        if (leaf is null) return FieldNotFound(current, leafName);

        // (3a-composes) P8a batch struct-list surface: composes= is a distinct input shape (a LIST of build-from-parts
        // element specs) that short-circuits the singular verb/value pipeline — Add appends each, ReplaceAll clears then
        // appends each (the modeled-list replace the singular composable block below still defers; THIS is that input
        // surface). Validated whole, all-or-nothing per element (Q3). Gated FIRST so composes on a dict/substruct gets a
        // composes-specific message, not the singular VerbLegality reject.
        if (req.Structs is not null)
            return ComposesLegality(leaf, leafOwner, req, siblingEditorIds);

        // (3a-copyfrom) P8b CopyFrom transplants the WHOLE field from another plugin's version — a distinct input shape
        // (no wire value; the source is from_plugin). Gate writable + not-identity + a transplantable KIND here; the
        // SOURCE resolution (is from_plugin in the order / does it define the record) is the cleave's Phase 1. The one
        // non-transplantable kind is an owned-child record collection — refused by name (forward the whole record).
        if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
            return CopyFromLegality(leaf, leafOwner);

        // (3a) verb legal for this cardinality?
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // (3b) record identity (FormKey/ModKey) is a flat, honest reject regardless of Mutagen's setter (plan §3 P-DISC).
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{leafOwner.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // (3c) writable? (P-DISC routing for discriminators)
        if (!leaf.Writable) return WritabilityRejection(leafOwner, leaf);

        // (4) value / key coercion + enum/legal-set legality
        return ValueLegality(leaf, req, siblingEditorIds);
    }

    /// <summary>Extract the arm type T from a gendered field's <c>GenderedItem&lt;T&gt;</c> TypeRef — e.g.
    /// "GenderedItem&lt;ArmorModel&gt;" → "ArmorModel". Returns the inner ref verbatim: a nested generic like
    /// "FormLinkNullable&lt;TextureSet&gt;" (a scalar/value arm) comes back whole and simply won't resolve as a
    /// corpus type, which the caller correctly surfaces as a non-navigable arm. Null if the string isn't the
    /// expected GenderedItem&lt;…&gt; shape.</summary>
    static string? GenderedArmRef(string typeRef)
    {
        const string head = "GenderedItem<";
        if (!typeRef.StartsWith(head, StringComparison.Ordinal) || !typeRef.EndsWith(">", StringComparison.Ordinal))
            return null;
        var inner = typeRef[head.Length..^1].Trim();
        return inner.Length == 0 ? null : inner;
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
                // A dict Add coerces req.Key into the new entry's key (ApplyDictVerb -> Coerce(req.Key!, kType)); a
                // MISSING key reaches apply and throws UNNAMED (Coerce(null)). A list Add appends — no key. Gate dict-Add
                // key PRESENCE here, the structural twin of Set-on-dict above (key VALUE-shape is ValueLegality's step-4-key).
                if (c == "dict") return hasKey ? null : $"Add on dict field '{leaf.Name}' requires a key.";
                return c == "list" ? null : $"Add is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "Remove":
                // A dict Remove identifies the entry to drop BY KEY (ApplyDictVerb -> Coerce(req.Key!, kType)); a MISSING
                // key throws UNNAMED at apply. A list Remove is by-index-OR-by-value (ApplyListVerb): a null key legally
                // falls back to remove-by-value, so list Remove needs NO key — gate dict-Remove key PRESENCE only.
                if (c == "dict") return hasKey ? null : $"Remove on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                return leaf.Nullable ? null : $"Remove on non-nullable {c} field '{leaf.Name}' is not valid.";
            case "ReplaceAll":
                return c is "list" or "dict" ? null : $"ReplaceAll is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "SetAtIndex":
                // A list SetAtIndex parses req.Key as the index (ApplyListVerb -> int.Parse(req.Key!)); a MISSING index
                // throws ArgumentNullException at apply. Require it up front (PRESENCE; the parseable-as-int / non-negative
                // VALUE-shape is gated in ValueLegality's step-4-key block).
                if (c != "list") return $"SetAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"SetAtIndex on list '{leaf.Name}' requires an index.";
            case "Merge":
                return c == "dict" ? null : $"Merge is only valid on dict; '{leaf.Name}' is {c}.";
            default:
                return $"Unknown verb '{req.Verb}'. Legal: Set, Add, Remove, ReplaceAll, SetAtIndex, Merge.";
        }
    }

    /// <summary>P8a — validate a composes= batch (a LIST of build-from-parts element specs) whole: Add appends each,
    /// ReplaceAll clears then appends each. LIST-of-modeled-elements ONLY (a dict needs keyed entries; a substruct/
    /// scalar takes compose=/value=). Each element is validated by the SAME <see cref="StructElementLegality"/> the
    /// singular compose Add uses (poly-base arm resolution + recursive contents), so composes can never admit a shape
    /// the singular path rejects. All-or-nothing: the first bad element names itself (composes[i]) and refuses the whole
    /// op (Q3). ReplaceAll here OPENS the modeled-list replace the singular composable block defers — that block has no
    /// per-element input surface; composes IS that surface.</summary>
    string? ComposesLegality(FieldSchema leaf, TypeSchema owner, WriteRequest req,
        IReadOnlyCollection<string>? siblingEditorIds)
    {
        if (req.Verb is not ("Add" or "ReplaceAll"))
            return $"composes= appends/replaces a LIST of modeled elements — use it with Add (append each) or " +
                   $"ReplaceAll (clear, then append each), not {req.Verb}. (For one element use compose=; to overwrite " +
                   "one index use SetAtIndex with compose=.)";
        if (leaf.Cardinality != "list")
            return $"composes= builds a LIST of modeled elements, but '{leaf.Name}' on '{owner.Name}' is a " +
                   $"{leaf.Cardinality}. (A dict takes keyed entries, not a positional list; a substruct/scalar takes " +
                   "compose= / value=.)";
        if (!IsComposableElement(leaf))
            return $"'{leaf.Name}' on '{owner.Name}' holds " +
                   (leaf.FormLinkTarget is not null ? "formlink" : "coercible") +
                   $" values ({leaf.ElementTypeRef ?? leaf.ElementType}), not modeled structs — use values= " +
                   "(ReplaceAll) / value= (Add), not composes=.";
        if (req.Structs!.Count == 0)
            return $"composes= for '{leaf.Name}' is empty — supply one or more element specs (or compose= for exactly one).";
        for (int i = 0; i < req.Structs.Count; i++)
            if (StructElementLegality(leaf, req.Structs[i], siblingEditorIds) is { } elemErr)
                return $"composes[{i}]: {elemErr}";
        return null;
    }

    /// <summary>P8b — validate a CopyFrom target leaf: writable, not record identity, and a TRANSPLANTABLE kind. The one
    /// non-transplantable kind is an owned-child record collection (Cell.Persistent, DialogTopic.Responses, …) — CopyFrom
    /// copies a FIELD's value, not owned child records; refuse by name and redirect to forward_record (whole record) or
    /// the record axis (create_record). Everything else — scalar/enum/value, formlink, formlink/modeled list, sub-struct,
    /// polymorphic arm — WriteEngine.CopyField transplants by construction. The SAME record-element predicate the write
    /// verbs use (step 4-rec), so gate and apply can't drift on what counts as an owned child.</summary>
    string? CopyFromLegality(FieldSchema leaf, TypeSchema owner)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not a copyable content field.";
        if (!leaf.Writable) return WritabilityRejection(owner, leaf);
        if (leaf.Cardinality is "list" or "dict" && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record)
            return $"'{leaf.Name}' on '{owner.Name}' holds owned child records ({leaf.ElementTypeRef}); CopyFrom copies a " +
                   "FIELD's value, not owned child records. To carry the WHOLE record from another plugin use " +
                   "housecarl_forward_record; a child record is authored on its own (housecarl_create_record with parent=).";
        if (leaf.Cardinality == "dict")
            return $"'{leaf.Name}' on '{owner.Name}' is a dict field; CopyFrom transplants scalar / formlink / list / " +
                   "sub-struct fields — a dict isn't transplanted yet. Set its entries individually, or forward the whole record.";
        return null;
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
    string? ValueLegality(FieldSchema leaf, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // Same-call sibling reference ("@editorid", create-context forward-ref — HCBR Layer B unit A). Gate it BEFORE
        // any verb/cardinality dispatch: it names a record created EARLIER in this same create call — or the record
        // being created ITSELF (self-reference, HCBR-2026-07-10-01) — substituted with the real FormKey AFTER
        // allocation (WritePatchBuilder.CreateRecords), and ONLY in create context
        // (siblingEditorIds non-null) — the Apply/set_field path has no siblings, so an @editorid there rejects loud
        // (never an accept-then-substitute-nothing, Q3). Legal placements, all resolved with identical timing:
        //   • a SINGULAR value — Set on a singular FormLink leaf, OR Add on a FormLink LIST leaf (S4 Track D: LinkTo
        //     etc.; the substitution replaces the singular req.Value either way — ApplyListVerb's Add coerces it).
        //   • inside a ReplaceAll's req.Values on a FormLink LIST (S4 Track D) — each @-entry substituted in place.
        // Anywhere else a sibling token would slip past pre-flight and throw FormKey.Factory at apply (a Q3
        // accept-then-throw), so those cases stay refused loud below. Both gates sit ahead of the cardinality branches.
        if (WriteEngine.IsSameCallSiblingRef(req.Value, out var sibEdid))
        {
            if (siblingEditorIds is null)
                return $"'{req.Value}' for '{leaf.Name}': a '@editorid' reference names a record being created in the " +
                       "SAME housecarl_create_record / housecarl_bulk_create call — when editing an existing record " +
                       "there are no same-call creations to point at. Use the target's FormID (a record already " +
                       "written into a houseCARL patch is addressable by FormID with into= that patch).";
            // The singular value must land on a FormLink TARGET — a singular formlink leaf or a formlink-element list.
            var onFormLink = leaf.Cardinality == "formlink"
                          || (leaf.Cardinality == "list" && leaf.FormLinkTarget is not null);
            if (!onFormLink)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid on a FormLink field, but " +
                       $"'{leaf.Name}' is a {leaf.Cardinality}.";
            // …and the verb must fit the target's shape: Set a singular link, Add to a link list (S4 Track D).
            var verbFits = (leaf.Cardinality == "formlink" && req.Verb == "Set")
                        || (leaf.Cardinality == "list" && req.Verb == "Add");
            if (!verbFits)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid as a Set value on a singular " +
                       $"FormLink field or an Add value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' " +
                       $"is a {leaf.Cardinality}).";
            // A stray compose spec riding alongside an admitted '@' value would skip validation (this gate RETURNS
            // before the compose branches) yet be WALKED by apply's substitution recursion — a bad token inside it
            // would then fail under the "internal: pre-flight should have caught it" wrapper, blaming the engine for
            // input the gate never saw (PR #166 review finding 1). Refuse it loud instead.
            if (req.Struct is not null)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "value IS the whole FormLink target; remove struct=.";
            return siblingEditorIds.Contains(sibEdid) ? null
                : $"Same-call reference '{req.Value}' for '{leaf.Name}': no record with editorid '{sibEdid}' is created " +
                  "EARLIER in this call (a record may also reference ITSELF by its own editorid) — declare it before " +
                  "the record that references it (in spec order).";
        }
        // A sibling token inside req.Values — legal ONLY as a ReplaceAll on a FormLink LIST (S4 Track D); each entry is
        // substituted with its sibling's allocated FormKey (WritePatchBuilder.CreateRecords). Validate the WHOLE list
        // here (siblings + literal FormIDs may mix) and RETURN — do NOT fall through to step-4a, which would reject the
        // '@' tokens as malformed FormLinks. Any other placement (wrong verb, a non-FormLink list) stays refused loud
        // rather than slipping to a FormKey.Factory throw at apply (Q3).
        if (req.Values is { } vals && vals.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
        {
            if (siblingEditorIds is null)
                return $"a '@editorid' reference for '{leaf.Name}' names a record being created in the SAME " +
                       "housecarl_create_record / housecarl_bulk_create call — when editing an existing record there " +
                       "are no same-call creations to point at. Use the target's FormID (a record already written " +
                       "into a houseCARL patch is addressable by FormID with into= that patch).";
            if (!(req.Verb == "ReplaceAll" && leaf.Cardinality == "list" && leaf.FormLinkTarget is not null))
                return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported as an Add value or a " +
                       $"ReplaceAll value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' is a {leaf.Cardinality}).";
            // The values-branch twin of the stray-compose guard above (PR #166 review finding 1).
            if (req.Struct is not null)
                return $"a '@editorid' same-call reference for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "entries ARE the FormLink elements; remove struct=.";
            foreach (var v in vals)
            {
                if (WriteEngine.IsSameCallSiblingRef(v, out var vEd))
                {
                    if (!siblingEditorIds.Contains(vEd))
                        return $"Same-call reference '@{vEd}' for '{leaf.Name}': no record with editorid '{vEd}' is " +
                               "created EARLIER in this call (a record may also reference ITSELF by its own editorid) — " +
                               "declare it before the record that references it (in spec order).";
                }
                else if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            }
            return null;
        }
        // A sibling token inside a dict Entries' VALUES — no formlink-VALUED dict is modeled (0 fields in the corpus),
        // so this stays refused loud (the dormant-by-construction dict half; a dict KEY '@…' is caught by the step-4-key
        // shape gate below, which won't coerce '@…' to any modeled key type).
        if (req.Entries is { } ents && ents.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported on a FormLink list, not " +
                   "inside a dict value — no formlink-valued dict is modeled.";
        // (step 4-key) KEY / INDEX VALUE-SHAPE — the shape twin of the key/index PRESENCE gate (VerbLegality's
        // missing-key rejects). A PRESENT-but-malformed dict key / list index passes presence but throws UNNAMED at
        // apply: a dict Set/Add/Remove coerces req.Key into the entry (ApplyDictVerb -> Coerce(req.Key!, KeyType)) and
        // Merge/ReplaceAll coerce each Entries key the same way; a list SetAtIndex/Remove parses req.Key as the index
        // (ApplyListVerb -> int.Parse(req.Key!)). Gate both LOUD here, by construction, with the SAME recognizers the
        // apply path uses so gate and apply can't drift: the dict key's real CLR type is resolved from the field's own
        // dictionary AQ (DictKeyType — the identical type apply keys on, dictIface.GetGenericArguments()[0]), so
        // coercibility is checked for EVERY key kind (enum AND the one sbyte-keyed dict), not just enums by catalog-
        // name; the list index via WriteEngine.IsValidListIndexValue (parseable non-negative int32). This EXTENDS the
        // gate to Add/Remove/Merge/ReplaceAll keys AND reconciles the dict Set key check (below, now value-only) onto
        // the same recognizer — closing the prior enum-only gaps (a non-enum key slipped; a numeric enum key like '3',
        // which apply accepts, was over-rejected). PRESENCE stays VerbLegality's job; this is purely SHAPE.
        if (leaf.Cardinality == "dict")
        {
            var keyAq = DictKeyType(leaf)?.AssemblyQualifiedName;
            string? KeyShape(string? k) => CheckValue(leaf.KeyType, k, $"dict key for '{leaf.Name}'", keyAq);
            if (req.Verb is "Set" or "Add" or "Remove" && req.Key is { } dKey && KeyShape(dKey) is { } dKeyErr)
                return dKeyErr;
            if (req.Verb is "Merge" or "ReplaceAll" && req.Entries is { } keyEnts)
                foreach (var k in keyEnts.Keys)
                    if (KeyShape(k) is { } entKeyErr) return entKeyErr;
        }
        if (leaf.Cardinality == "list" && req.Verb is "SetAtIndex" or "Remove" && req.Key is { } lIdx
            && !WriteEngine.IsValidListIndexValue(lIdx))
            return $"Illegal list index '{lIdx}' for '{leaf.Name}': expected a non-negative integer. " +
                   "(Whether the index is in range is checked at apply, against the live list.)";
        if (req.Verb is "Set" && leaf.Cardinality == "dict")
        {
            // Key shape gated by the step-4-key block above (Set/Add/Remove share one recognizer). A struct/arm-VALUED
            // dict (Package.Data — the only one Mutagen models) Set REPLACES an entry's value with a build-from-parts
            // element (Gap 3, dict-element composition): validate the spec against the element type via the SAME
            // StructElementLegality the Add path uses (poly-base arm resolution + recursive contents) — gate and apply
            // share one recognizer, no drift. A coercible-VALUE dict (Class.SkillWeights, Race.Regen, …) Set coerces.
            if (IsComposableElement(leaf)) return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            if (req.Value is null) return $"Set on dict '{leaf.Name}' requires a value.";
            return CheckValue(leaf.ElementType, req.Value, $"dict value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
        }
        if (req.Verb is "Set" && leaf.Cardinality == "polymorphic")
            return ArmLegality(leaf, req.Struct, siblingEditorIds);
        // A whole modeled-STRUCT substruct leaf (FaceParts, ObjectBounds, FaceMorph, a concrete script-property arm, …) is
        // Set by composing its value FROM PARTS — the leaf twin of the dict-element (above) and polymorphic-arm (just above)
        // compose paths, validated by the SAME StructSpecContents. Apply already builds it (ApplyScalarVerb req.Struct ->
        // BuildStruct), so this LIFTS the documented safe over-reject (write-preflight audit §3(b)) so a one-shot subtree
        // author can fill an absent struct in ONE op. SchemaClassifier scopes it so a coercible substruct (TranslatedString)
        // keeps its plain-value Set below, and a composition-residual (GenderedItem — diverted to [0]/[1] upstream — /
        // Array2d — no paramless ctor) stays out (gate==apply, no accept-then-throw). Chain Stage 2b.
        if (req.Verb is "Set" && SchemaClassifier.IsComposableSubstructLeaf(leaf, _corpus))
            return StructLeafLegality(leaf, req.Struct, siblingEditorIds);
        if (req.Verb is "Set")
        {
            // A compose spec reaching HERE means the leaf isn't a compose target (not a composable substruct/dict/poly —
            // those branch above): a coercible substruct (a TranslatedString — set as one value, not built from parts), a
            // formlink, or a plain scalar. Name the plain-value path instead of the misleading "requires a value" (which
            // reads as "value= is absent"). compose is for a build-from-parts struct/dict/polymorphic field only.
            if (req.Value is null)
                return req.Struct is not null
                    ? $"'{leaf.Name}' is set from a plain value (value=…), not a compose spec."
                    : $"Set on '{leaf.Name}' requires a value.";
            // formlink / substruct-whole: the engine must be able to coerce the leaf's whole type. A normal formlink
            // coerces; a condition FormLinkOrIndex is handled by the parent-aware SetFloi branch (wave 4) — validate
            // its target-value SHAPE here; a non-string substruct still rejects honestly (so pre-flight never
            // accepts-then-throws). FLOI is recognised via the engine's shared IsFormLinkOrIndex (no drift).
            if (leaf.Cardinality is "formlink" or "substruct")
            {
                var faq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
                if (WriteEngine.ResolveType(faq) is { } frt && WriteEngine.IsFormLinkOrIndex(frt))
                    return WriteEngine.TryClassifyFloiValue(req.Value) ? null
                        : $"Illegal condition target '{req.Value}' for '{leaf.Name}': expected {FloiTargetForms}.";
                // A NORMAL FormLink Set — validate the FormKey VALUE shape at the gate (the FORMLINK arm ONLY; a
                // substruct still falls to the type-shape CoercibilityReject below). CoercibilityReject is type-only
                // and never inspected the string, so "00000000"/"0" were accepted then threw at FormKey.Factory on
                // apply — a Q3 accept-then-throw hole. A null-synonym clears the link; otherwise it must parse as a
                // FormKey. The recognizer is SHARED with the engine apply path (no drift). (HCBR-2026-06-15-01 PR-F.)
                if (leaf.Cardinality == "formlink")
                    return WriteEngine.IsValidFormLinkValue(req.Value) ? null
                        : $"Illegal FormLink target '{req.Value}' for '{leaf.Name}': expected a FormID " +
                          "(XXXXXX:Plugin.esp) or a null-clear ('0', '00000000', 'Null', '000000:Null').";
                return CoercibilityReject(leaf);
            }
            return CheckValue(leaf.Type, req.Value, $"value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // (step 4-pre) ELEMENT-VALUE PRESENCE — the collection twin of the singular Set "requires a value" reject
        // above (line ~328). Add / SetAtIndex on a COERCIBLE-element collection set the new element by coercing the
        // singular req.Value (ApplyListVerb / ApplyDictVerb -> Coerce(req.Value!, elem)); a MISSING value does NOT
        // fail loud at the gate — at apply Coerce(null) yields a null element that then throws a NullReferenceException
        // at SERIALIZE (surfaced as the misleading "compose the Data arm" NullArmSerializeException, nothing to do with
        // the real cause). That is the SAME accept-then-throw shape PR #76 closed for a MALFORMED (non-null) element,
        // but for the absent-value case: the step-4a formlink check below uses `is { } ev`, which SKIPS a null slot.
        // Gate it here for EVERY coercible element (formlink + non-formlink), by construction. NO req.Struct guard, by
        // design (PR #77 review finding 1): a coercible element is NEVER built from a StructSpec, so a struct supplied
        // with a null value is itself malformed — SetAtIndex now mirrors Add (ApplyListVerb builds from a non-null
        // req.Struct, else coerces req.Value), and BuildStruct on a coercible element throws for BOTH verbs, so a struct
        // here can never rescue a null value; firing on a
        // null value REGARDLESS of req.Struct closes both, and "requires an element value" is the right guidance either
        // way (this also matches the singular Set mirror, which carries no struct guard). Verb-scoped to the verbs that
        // consume the singular req.Value — ReplaceAll (req.Values) / Merge (req.Entries) carry their elements elsewhere
        // (a null ENTRY inside those is the step-4a formlink check's job, or the parked non-formlink value-shape
        // surface), and Remove is by-key-OR-value (a distinct identify-the-element concern, not a "requires a value"
        // mirror). Coercible-element-only: Struct/Arm elements compose via the composable block below (which DOES
        // require the spec), and Record / uncoercible elements have no plain-value Add path — both left as today.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex"
            && req.Value is null && IsValueCoercibleElement(leaf))
            return $"{req.Verb} on '{leaf.Name}' requires an element value.";
        // (step 4a) FormLink-ELEMENT collection value-shape — the collection twin of the singular formlink Set check
        // immediately above. A list/dict whose ELEMENT is a FormLink (corpus FormLinkTarget set — emitted BY
        // CONSTRUCTION by the SAME generator IsFormLink branch that flags a singular formlink) coerces each element
        // through FormKey.Factory at apply (ApplyListVerb / ApplyDictVerb -> Coerce -> TryFormLink -> ToFormKey). A
        // malformed element ("notaformkey"; "0"/"00000000"/"Null"/"000000:Null" stay legal null-clears) used to pass
        // pre-flight here and throw "Malformed FormKey string" at apply — a Q3 accept-then-throw, the GENERAL gap the
        // Layer-B sibling-ref collection gate (above) named as the broader pre-existing hole. Validate every supplied
        // element VALUE with the SAME recognizer the singular path uses (IsValidFormLinkValue — one predicate, no
        // drift between gate and apply): req.Value (list Add / SetAtIndex / Remove-by-value; dict Add — dict Set
        // returned at its own block above), req.Values (list ReplaceAll), and req.Entries' VALUES (dict Merge /
        // ReplaceAll). The dict KEY VALUE-SHAPE is gated up front by the step-4-key block above (coercible-to-KeyType
        // via the key's real CLR type, EVERY key kind) — this block validates element VALUES only. (Key/index PRESENCE
        // is a DISTINCT concern, gated by construction in VerbLegality's Add/Remove/SetAtIndex arms.) NOTE: every
        // formlink collection in the current corpus is a LIST (85 fields); a formlink-VALUED dict is
        // not modeled by Mutagen today (0 fields) and the generator's dict branch does not stamp FormLinkTarget for
        // one, so the req.Entries arm is dormant-by-construction — named here (Q3), and lit the moment such a field
        // (carrying its FormLinkTarget stamp) exists. The dict KEY-shape edge is now closed by the step-4-key block above.
        if (leaf.Cardinality is "list" or "dict" && leaf.FormLinkTarget is not null)
        {
            if (req.Value is { } ev && !WriteEngine.IsValidFormLinkValue(ev)) return FormLinkElementReject(ev, leaf);
            foreach (var v in req.Values ?? Array.Empty<string>())
                if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            foreach (var kv in req.Entries ?? new())
                if (!WriteEngine.IsValidFormLinkValue(kv.Value)) return FormLinkElementReject(kv.Value, leaf);
        }
        // (step 4b) NON-FORMLINK coercible-element collection value-SHAPE — the value twin of step-4a (which handles
        // FORMLINK elements via IsValidFormLinkValue) and of the dict-Set value block above (which gates dict Set's value
        // but not the other collection verbs). A list Add/SetAtIndex/ReplaceAll/Remove-by-value and a dict Add/Merge/
        // ReplaceAll coerce each supplied element value at apply (ApplyListVerb/ApplyDictVerb -> Coerce(req.Value!/v,
        // elem/vType)); a malformed value (e.g. "notafloat" into a List<Single>, "notabyte" into a Dictionary<Skill,Byte>)
        // used to pass pre-flight then throw UNNAMED (float.Parse/byte.Parse) at apply — the Q3 accept-then-throw this
        // closes. Scoped to IsValueCoercibleElement(leaf) && FormLinkTarget is null so formlink elements keep step-4a's
        // per-element message and the two together cover EVERY coercible element with no double-check and no gap. Uses the
        // SAME CheckValue recognizer (with the element AQ) the dict-Set value block uses — gate and apply can't drift.
        // Verb/key-FAITHFUL to which slot apply actually coerces: the singular req.Value is checked for list Add/SetAtIndex
        // and dict Add (always coerced), and for a list Remove-by-VALUE (Key null) only — a list Remove BY INDEX / a dict
        // Remove coerce only the key, so their value is apply-irrelevant and must NOT be over-rejected. req.Values is the
        // list ReplaceAll contents; req.Entries' VALUES are dict Merge/ReplaceAll (their keys are the step-4-key block's
        // job). Null PRESENCE on Add/SetAtIndex stays step-4-pre's; a null inside Values/Entries yields CheckValue's
        // "Missing element value …" (correct for those verbs, which have no presence mirror).
        if (leaf.Cardinality is "list" or "dict" && IsValueCoercibleElement(leaf) && leaf.FormLinkTarget is null)
        {
            string? ElemShape(string? v) =>
                CheckValue(leaf.ElementType, v, $"element value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
            if (req.Value is { } ev
                && (req.Verb is "Add" or "SetAtIndex" || (req.Verb is "Remove" && req.Key is null))
                && ElemShape(ev) is { } evErr)
                return evErr;
            // Slot-faithful to apply: a LIST ReplaceAll coerces req.Values (ApplyListVerb); a DICT Merge/ReplaceAll
            // coerces req.Entries' values (ApplyDictVerb). Scope each loop to its slot's cardinality so a stray
            // off-cardinality slot apply IGNORES (e.g. req.Entries supplied on a list ReplaceAll) is not over-rejected —
            // mirrors the singular-value verb/key-faithfulness above (review polish).
            if (leaf.Cardinality == "list" && req.Verb is "ReplaceAll")
                foreach (var v in req.Values ?? Array.Empty<string>())
                    if (ElemShape(v) is { } valsErr) return valsErr;
            if (leaf.Cardinality == "dict" && req.Verb is "Merge" or "ReplaceAll")
                foreach (var kv in req.Entries ?? new())
                    if (ElemShape(kv.Value) is { } entErr) return entErr;
        }
        // (step 4-rec) RECORD-ELEMENT collection verb — a list/dict whose ELEMENT is an owned child RECORD
        // (SchemaClassifier classifies record-families ElementKind.Record: DialogTopic.Responses -> DialogResponses;
        // Cell.Persistent/Temporary -> the all-record Placed arms; the typed record groups). A record element is neither
        // IsComposableElement (Record is excluded from Struct/Arm) nor IsValueCoercibleElement nor formlink, so an
        // Add/SetAtIndex/ReplaceAll fell through to `return null` (ACCEPT) and then THREW at apply: with a compose,
        // BuildStruct -> Instantiate -> CompositionRequiredException (the record class has no public parameterless ctor);
        // with a plain value, Coerce(value, <record getter>) -> "No coercion rule" — both Q3 accept-then-throw (the
        // second NAMED-but-misleading: it points at composition/coercion, not at "use create_record"). A child record is
        // allocated on the record axis, never built into a parent's collection by the verb engine; the supported path is
        // housecarl_create_record / housecarl_bulk_create with parent=. Redirect by construction (one classifier
        // predicate, no per-record-type list). Verb-scoped to the create-oriented verbs (Add/SetAtIndex/ReplaceAll); a
        // record Remove BY INDEX (RemoveAt) is throw-free and stays accepted, and a record Remove BY VALUE is the
        // non-plain-value Remove surface closed by the unified Remove-by-value reject in the step-4-rmv block below.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex" or "ReplaceAll"
            && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record)
            return $"'{leaf.Name}' holds owned child records ({leaf.ElementTypeRef}); a child record is created on its " +
                   "own (the record axis), not added into a parent's collection by a write verb. Use housecarl_create_record " +
                   "/ housecarl_bulk_create with parent= the parent's FormID (and collection= when the parent holds more " +
                   "than one fitting list) — surfaced here, never accepted and thrown at apply.";
        // (step 4) collection-verb value legality. A struct-element OR arm-element list takes a build-from-parts
        // StructSpec on Add — NOT a plain value — which is wave-1 half B composition (an ARM element composes by its
        // concrete arm type, validated against that arm's own schema — the VMAD shape, #35; before this, arm-element
        // Adds fell through pre-flight UNVALIDATED and only failed at runtime — an accept-then-throw Q3 hole). A
        // coercible-element list takes a plain value the engine coerces (proven by the collection waves).
        if (leaf.Cardinality is "list" or "dict" && IsComposableElement(leaf))
        {
            // Add composes a build-from-parts element against the element type — LIST and DICT alike (Gap 3 opened dict
            // Add: ApplyDictVerb now builds the entry value via BuildStruct(req.Struct) when composing, mirroring
            // ApplyListVerb's Add, so admitting a dict compose is apply-faithful — no longer the accept-then-throw the
            // earlier PR-review note guarded against). StructElementLegality resolves a polymorphic-base element's arm
            // (Package.Data -> an APackageData arm) + validates contents recursively. (A composable dict SET is gated at
            // the dict-Set block above — same StructElementLegality.)
            // Add (append a new element) and SetAtIndex (overwrite the element at an index) both build the element FROM
            // PARTS against the element type, validated by the SAME StructElementLegality (poly-base arm resolution +
            // recursive contents; a null spec → "supply a compose spec, not a plain value"). SetAtIndex's index PRESENCE
            // (VerbLegality) and SHAPE (step-4-key block above) are already gated by the time control reaches here, and
            // the in-RANGE bound is apply's (no live list at the gate) — so the only thing left for a SetAtIndex compose
            // is the spec, identical to Add. This closes HCBR-2026-07-10: an in-place condition-row REPLACEMENT no longer
            // needs Remove+Add (which moved the row to the list END — harmless for an AND row, but breaking an OR-group).
            // The composable fence (IsComposableElement = Struct/Arm ONLY) keeps this off owned-record elements, which
            // step-4-rec above already redirects to the record axis.
            if (req.Verb is "Add" or "SetAtIndex")
                return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            // ReplaceAll/Merge of modeled elements stay deferred: ReplaceAll would need a LIST of compose specs and Merge
            // a dict of them (req.Values / req.Entries are plain-string shapes), distinct input surfaces not opened here.
            // Merge is dict-only and was previously OMITTED, so a Package.Data Merge fell through to ACCEPT then threw
            // 'No coercion rule' at apply (matrix-critic finding) — folding it in closes that. Verb named in the message.
            if (req.Verb is "ReplaceAll" or "Merge")
                return $"'{leaf.Name}' holds modeled elements ({leaf.ElementTypeRef}); compose them one at a time " +
                       $"(Add to append, SetAtIndex to overwrite an index; Set for a dict key) — {req.Verb} of modeled " +
                       $"elements is a later surface.";
        }
        // (step 4-rmv) Remove-BY-VALUE on a NON-PLAIN-VALUE element — a list Remove with NO key is by-value
        // (ApplyListVerb -> Coerce(req.Value!, elem)); an element that is neither coercible (step-4b) nor formlink
        // (step-4a) has NO plain-value form, so the coerce throws 'No coercion rule' at apply (or an NRE if the value is
        // also null). ONE by-construction predicate covers composable (struct/arm), record, AND the dormant uncoercible
        // case — the value twin none of the other branches catch for Remove. A Remove BY INDEX (Key present -> RemoveAt,
        // no coercion) stays accepted, and a dict Remove (by key only, key-gated) is excluded (list-only). Redirect to
        // remove-by-index; value-based removal of a modeled/record element is a later surface.
        if (req.Verb == "Remove" && leaf.Cardinality == "list" && req.Key is null
            && leaf.FormLinkTarget is null && !IsValueCoercibleElement(leaf))
            return $"'{leaf.Name}' holds modeled/record elements ({leaf.ElementTypeRef ?? leaf.ElementType}); remove one " +
                   "BY INDEX (Remove with a Key = its position), not by value — a modeled or record element has no " +
                   "plain-value form to match. (Value-based removal of such an element is a later surface.)";
        return null;
    }

    /// <summary>True iff the leaf is a collection whose ELEMENT is built FROM PARTS on Add (so Add takes a
    /// StructSpec): a modeled-struct element, or a polymorphic-union (arm) element composed by its concrete arm
    /// type. Record-elements (nested-group wave) are resolved on their own axis, and a WHOLE-COERCIBLE element
    /// (an AssetLink path) is set as one value — both fall through to the plain-value Add path, never demanding
    /// a spec. Derived via the shared <see cref="SchemaClassifier"/> so the partition cannot be defined twice.</summary>
    bool IsComposableElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.Struct or ElementKind.Arm;

    /// <summary>True iff the leaf is a collection whose ELEMENT the engine sets by COERCING a single plain value
    /// (req.Value): a scalar/enum/formlink element (<see cref="ElementKind.ScalarCoercible"/>) or a whole-coercible
    /// AssetLink-path element (<see cref="ElementKind.WholeCoercible"/>). These are exactly the kinds an Add /
    /// SetAtIndex writes via <c>Coerce(req.Value!, elem)</c> at apply, so a null req.Value yields a null element that
    /// throws at serialize — the value-presence gate keys off this. Struct/Arm elements compose via req.Struct
    /// (<see cref="IsComposableElement"/>), and Record / uncoercible elements have no plain-value Add path at all; both
    /// are correctly EXCLUDED so the gate can't mis-diagnose them. Derived via the shared <see cref="SchemaClassifier"/>
    /// so the partition isn't redefined. (Broader than <see cref="SchemaClassifier.CoercibleElement"/>, which is
    /// ScalarCoercible only — a WholeCoercible element is ALSO set by one coerced value, so it shares the null hazard.)</summary>
    bool IsValueCoercibleElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.ScalarCoercible or ElementKind.WholeCoercible;

    /// <summary>Validate a struct-element Add: the spec must be present, its type must match the list's element
    /// type — or, when the element type is a <b>polymorphic-base</b>, be one of its ARMS (the VMAD shape, #35:
    /// <c>ScriptEntry.Properties</c> is a list of the base <c>ScriptProperty</c>, but a real new element is a
    /// concrete arm like <c>ScriptObjectProperty</c>) — and its contents must validate against the SPEC's own
    /// schema (the arm's fields, not the base's), recursively via the shared validator. Generic over every
    /// polymorphic-base element family — no per-type wiring (cornerstone).</summary>
    string? StructElementLegality(FieldSchema leaf, StructSpec? spec, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (spec is null)
            return $"'{leaf.Name}' takes a build-from-parts element (a modeled {leaf.ElementTypeRef}); supply a compose spec, not a plain value.";
        var er = leaf.ElementTypeRef!;
        var elemSchema = Type(er);
        if (elemSchema is null) return $"Element type '{er}' for '{leaf.Name}' absent from corpus.";

        // (G8) A polymorphic BASE is composed by choosing a concrete ARM, never the base itself. Naming the base
        // ({Type:"APackageData"} on the Package.Data dict, {Type:"Condition"} on a *.Conditions list) is a gate/apply
        // drift the validator must reject: the spec.Type==er short-circuit below would validate it against the base's
        // OWN fields and ACCEPT, then apply either SILENTLY writes a degenerate base instance (a CONCRETE base like
        // APackageData — Instantiate finds its public parameterless ctor: a Q3 silent-wrong-write) or THROWS at Invoke
        // ("cannot create an abstract class" — an ABSTRACT base like Condition). The recognizer is the corpus poly-base
        // KIND, NOT Type.IsAbstract: APackageData is concrete (IsAbstract=false), so an IsAbstract check would miss the
        // silent-write case — the worse one. A concrete poly-base also lists ITSELF among its arms (FindUnionArms keeps
        // a non-abstract base), so the base is filtered out of the legal-arms set everywhere it is used (the arm-match
        // check AND every message) — neither path can admit or advertise it. Generic over every poly-base; no per-type wiring.
        bool isPolyBase = elemSchema is { Kind: "polymorphic-base" };
        var legalArms = (elemSchema.Arms ?? new()).Where(a => a != er).ToList();

        TypeSchema specSchema;
        if (spec.Type == er)
        {
            if (isPolyBase)
                return $"'{spec.Type}' is the polymorphic base of '{leaf.Name}' — the base itself cannot be composed; " +
                       $"choose a concrete arm. Legal element types: {string.Join(", ", legalArms)}.";
            specSchema = elemSchema;   // a concrete (non-poly-base) struct element composed by its own name — the normal case
        }
        else if (isPolyBase && legalArms.Contains(spec.Type))
            specSchema = Type(spec.Type)
                ?? throw new InvalidOperationException($"Arm '{spec.Type}' of '{er}' is listed but absent from the corpus — regenerate corpus.json.");
        else
        {
            var legal = isPolyBase && legalArms.Count > 0
                ? $" Legal element types: {string.Join(", ", legalArms)}." : "";
            return $"Element spec type '{spec.Type}' does not match '{leaf.Name}' element type '{er}'.{legal}";
        }
        return StructSpecContents(spec, specSchema, siblingEditorIds);
    }

    /// <summary>Validate a whole-struct compose Set on a SUBSTRUCT leaf — the leaf twin of <see cref="StructElementLegality"/>,
    /// keyed on the leaf's own <see cref="FieldSchema.TypeRef"/>. The spec must be present and its type must match the leaf's
    /// struct type; its contents then validate against that type's schema via the shared <see cref="StructSpecContents"/>
    /// (flat Fields + nested Sets + ctor-args) — the SAME recognizer the struct-element Add and polymorphic-arm Set use, so
    /// the three composition entry points can't disagree. A substruct leaf's TypeRef is always a CONCRETE struct/arm (a
    /// polymorphic FIELD is cardinality "polymorphic", handled above), so a straight name-match is correct — no poly-base
    /// arm resolution. Reached only for a <see cref="SchemaClassifier.IsComposableSubstructLeaf"/> leaf (TypeRef non-null,
    /// corpus-present, apply-instantiable) — the null-spec branch replaces the misleading scalar "requires a value".</summary>
    string? StructLeafLegality(FieldSchema leaf, StructSpec? spec, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        var tr = leaf.TypeRef!;               // non-null by IsComposableSubstructLeaf
        var schema = Type(tr);
        if (schema is null) return $"Struct type '{tr}' for '{leaf.Name}' absent from corpus.";
        if (spec is null)
            return $"'{leaf.Name}' is a {tr} struct — set it by composing from parts (a compose spec, e.g. " +
                   $"{{\"type\":\"{tr}\", \"fields\":{{…}}}}), or navigate into it and Set a sub-field; a plain value can't express a struct.";
        if (spec.Type != tr)
            return $"Compose type '{spec.Type}' does not match '{leaf.Name}' struct type '{tr}'.";
        return StructSpecContents(spec, schema, siblingEditorIds);
    }

    /// <summary>Validate a build-from-parts spec's CONTENTS against its declared struct type: flat <see cref="StructSpec.Fields"/>
    /// must exist + coerce; nested <see cref="StructSpec.Sets"/> validate by the identical path/leaf rules (recursively,
    /// through <see cref="ValidateFromType"/>). Shared by the polymorphic-arm Set and the struct-element Add so the two
    /// composition entry points can never disagree.</summary>
    string? StructSpecContents(StructSpec spec, TypeSchema structSchema, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // (G4) positional ctor_args value-SHAPE + ARITY — the one compose part the gate never checked. A malformed arg
        // or a wrong arity passed pre-flight then threw at apply (Instantiate: Coerce(arg, paramType) / "no constructor
        // taking N arg(s)"). WriteEngine.TryRecognizeCtorArgs mirrors Instantiate EXACTLY (same ResolveStructType + ctor
        // selector + TryCoerce), so gate and apply can't drift. Checked at the TOP so it runs for BOTH StructSpecContents
        // call sites (ArmLegality + StructElementLegality) and reports before the per-field checks. Skipped when CtorArgs
        // is null (the common compose path — parameterless/fields only, unchanged).
        if (spec.CtorArgs is { } ctorArgs && WriteEngine.TryRecognizeCtorArgs(spec.Type, ctorArgs) is { } ctorErr)
            return ctorErr;
        foreach (var f in spec.Fields ?? new())
        {
            var af = structSchema.Fields.FirstOrDefault(x => x.Name == f.Key);
            if (af is null) return FieldNotFound(structSchema, f.Key);
            // A '@editorid' same-call reference in a compose FIELD (HCBR-2026-07-10-01: the VMAD alias-fragment
            // Property.Object=@<the quest itself> shape) — legal on a singular FORMLINK field in CREATE context only,
            // mirroring the top-level singular-value gate exactly (formlink-only + declared-earlier-or-self); the
            // create path substitutes it with the allocated FormKey (WritePatchBuilder.ResolveSiblingRefs). Gated
            // BEFORE CheckValue, which would otherwise reject the '@' token as a malformed FormLink.
            if (WriteEngine.IsSameCallSiblingRef(f.Value, out var fEd))
            {
                if (siblingEditorIds is null)
                    return $"a '@editorid' reference for '{f.Key}' on '{spec.Type}' names a record being created in the " +
                           "SAME housecarl_create_record / housecarl_bulk_create call — when editing an existing record " +
                           "there are no same-call creations to point at. Use the target's FormID.";
                if (af.Cardinality != "formlink")
                    return $"Same-call reference '{f.Value}' for '{f.Key}' on '{spec.Type}' is only valid on a FormLink " +
                           $"field, but '{f.Key}' is a {af.Cardinality}.";
                if (!siblingEditorIds.Contains(fEd))
                    return $"Same-call reference '{f.Value}' for '{f.Key}' on '{spec.Type}': no record with editorid " +
                           $"'{fEd}' is created EARLIER in this call (a record may also reference ITSELF by its own " +
                           "editorid) — declare it before the record that references it (in spec order).";
                continue;
            }
            if (CheckValue(af.Type, f.Value, $"'{f.Key}' on '{spec.Type}'",
                    af.MutableTypeAssemblyQualified ?? af.GetterTypeAssemblyQualified) is { } e) return e;
        }
        foreach (var s in spec.Sets ?? new())
            // siblingEditorIds threads through — a same-call @editorid ref inside a COMPOSED struct's nested Sets
            // (e.g. a VMAD quest-fragment's Property.Object=@<own quest>, HCBR-2026-07-10-01) validates by the SAME
            // gates as a top-level value (formlink-only + declared-earlier-or-self), recursively; on the edit path
            // (null) it still rejects loud rather than being silently accepted (Q3).
            if (ValidateFromType(structSchema, s, siblingEditorIds) is { } e) return e;
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

    /// <summary>The loud per-element rejection for a malformed FormLink collection ELEMENT — the SAME legal-set copy
    /// as the singular formlink Set reject, with "target" reading "element" so the two are visibly the one check at
    /// two cardinalities. The offending value is named (per-element, Q3): the gate says exactly which element it
    /// refused and what shape is legal, never a bare "internal inconsistency" surfaced from an apply-time throw.</summary>
    static string FormLinkElementReject(string value, FieldSchema leaf) =>
        $"Illegal FormLink element '{value}' for '{leaf.Name}': expected a FormID (XXXXXX:Plugin.esp) " +
        "or a null-clear ('0', '00000000', 'Null', '000000:Null').";

    /// <summary>Validate a polymorphic Set: the arm must be a legal arm of the field, and its contents (flat fields +
    /// nested sets) must validate against the arm type — the same composition-contents check a struct-element Add uses.</summary>
    string? ArmLegality(FieldSchema leaf, StructSpec? arm, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (arm is null) return $"Set on polymorphic field '{leaf.Name}' requires an arm (which arm + its data).";
        // (G8) The standalone-poly-FIELD twin of the StructElementLegality base-reject. A CONCRETE poly-base (e.g.
        // ScriptFragments on DialogResponsesAdapter.ScriptFragments) lists ITSELF among its arms, so legal.Contains(base)
        // would otherwise admit a Set composing the base by its OWN name, and apply then SILENTLY writes a degenerate
        // base instance. Filter the base (leaf.TypeRef — this method is only reached for a polymorphic field) out of the
        // legal set, and reject composing the base itself. Symmetric with StructElementLegality; closes the SECOND
        // composition entry point so the poly-base-by-own-name class is shut at both, by construction.
        var baseName = leaf.TypeRef;
        var legal = (leaf.Arms ?? (baseName is { } tr ? Type(tr)?.Arms : null) ?? new()).Where(a => a != baseName).ToList();
        if (baseName is not null && arm.Type == baseName)
            return $"'{arm.Type}' is the polymorphic base of '{leaf.Name}' — the base itself cannot be composed; " +
                   $"choose a concrete arm. Legal arms: {string.Join(", ", legal)}.";
        if (!legal.Contains(arm.Type))
            return $"Illegal arm '{arm.Type}' for '{leaf.Name}'. Legal arms: {string.Join(", ", legal)}.";
        var armSchema = Type(arm.Type);
        if (armSchema is null) return $"Arm '{arm.Type}' absent from corpus.";
        return StructSpecContents(arm, armSchema, siblingEditorIds);
    }

    /// <summary>Resolve a dict leaf's KEY clr type from its own dictionary AQ — the SAME type the apply path keys on
    /// (<c>ApplyDictVerb</c>: <c>dictIface.GetGenericArguments()[0]</c>), so the key-shape gate and the engine agree on
    /// the key type by construction. The corpus carries the whole <c>IDictionary&lt;K,V&gt;</c> /
    /// <c>IReadOnlyDictionary&lt;K,V&gt;</c> AQ with BOTH type args fully qualified, so no separate key-AQ schema field
    /// is needed. Returns null if it can't be resolved (the caller's <see cref="CheckValue"/> then degrades to the
    /// catalog-by-name enum check — still loud for enum keys, never a silent accept). Mutable AQ preferred over getter,
    /// matching <see cref="CoercibilityReject"/>.</summary>
    static System.Type? DictKeyType(FieldSchema leaf)
    {
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (aq is null || WriteEngine.ResolveType(aq) is not { IsGenericType: true } dt) return null;
        var args = dt.GetGenericArguments();
        return args.Length == 2 ? args[0] : null;
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
            // A condition FormLinkOrIndex target (e.g. GetEquipped.ItemOrList, GetGlobalValue.Global) does NOT coerce via
            // the parentless Coerce family — its parent arm carries the form/alias mode bit (set at apply by SetFloi). It
            // is reached here only as a flat compose FIELD (the nested-Sets path validates FLOI in ValidateFromType's
            // formlink branch); validate the target-value SHAPE the SAME way (TryClassifyFloiValue) so the two compose
            // entry points can't drift. Recognised via the engine's shared IsFormLinkOrIndex (no drift).
            if (WriteEngine.IsFormLinkOrIndex(rt))
                return WriteEngine.TryClassifyFloiValue(value) ? null
                    : $"Illegal {what}: '{value}' is not a legal condition target — expected {FloiTargetForms}.";
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
    /// mean "close enough".
    ///
    /// The CLR-type facets compare write-legal EQUIVALENCE, not raw-string identity (HCBR 1.2 / PR-B): a type and
    /// its <c>Nullable&lt;T&gt;</c> wrapper admit the identical value set, because <see cref="WriteEngine"/>'s
    /// Coerce/CanCoerce unwrap <c>Nullable&lt;T&gt;</c> before checking. So a field declared <c>float</c> on one
    /// arm and <c>float?</c> on another (the one such corpus field — <c>APerkEffect.Value</c>) AGREES: the
    /// over-arms search admits the path and the engine resolves on the live arm. The raw <c>Nullable</c> flag is
    /// therefore NOT compared — it is exactly the wrapper distinction the unwrap erases — while every GENUINE
    /// difference (cardinality, display type, or a different underlying CLR type) still rejects.</summary>
    internal static bool SameShape(FieldSchema a, FieldSchema b) =>
        a.Cardinality == b.Cardinality && a.Type == b.Type && a.TypeRef == b.TypeRef
        && a.ElementType == b.ElementType && a.ElementTypeRef == b.ElementTypeRef
        && a.Writable == b.Writable && a.IsIdentity == b.IsIdentity
        && SameWriteLegalType(a.GetterTypeAssemblyQualified, b.GetterTypeAssemblyQualified)
        && SameWriteLegalType(a.MutableTypeAssemblyQualified, b.MutableTypeAssemblyQualified)
        && SameWriteLegalType(a.ElementTypeAssemblyQualified, b.ElementTypeAssemblyQualified);

    /// <summary>Two assembly-qualified CLR-type names are write-legal-equivalent iff they resolve to the same
    /// runtime type after unwrapping <c>Nullable&lt;T&gt;</c> — mirroring <see cref="WriteEngine"/>'s own
    /// Coerce/CanCoerce, which unwrap <c>Nullable&lt;T&gt;</c> before validating, so <c>float</c> and <c>float?</c>
    /// admit the identical value set. A name that will not resolve to a runtime Type falls back to RAW-string
    /// identity, so a genuinely unknown type can never be silently widened (Q3 — fail loud, never "close enough").
    /// Null matches only null (one arm declaring the facet and the other not is a real difference).</summary>
    static bool SameWriteLegalType(string? a, string? b)
    {
        if (a == b) return true;                      // identical strings (incl. both-null) — the common case
        if (a is null || b is null) return false;     // one present, one absent → genuinely different
        var ta = WriteEngine.ResolveType(a);
        var tb = WriteEngine.ResolveType(b);
        if (ta is null || tb is null) return a == b;  // unresolvable → raw-string fallback (false here → stay rejected, Q3)
        return (Nullable.GetUnderlyingType(ta) ?? ta) == (Nullable.GetUnderlyingType(tb) ?? tb);
    }
}

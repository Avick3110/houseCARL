using System.Text.Json;

namespace HousecarlCore;

/// <summary>A write request in the engine's internal representation: a verb applied at the leaf of a path from a
/// record root. Not the wire format — the MCP layer translates into this shape.</summary>
public sealed class WriteRequest
{
    public required string RecordType { get; init; }   // catalog name, e.g. "Npc"
    public required string[] Path { get; init; }        // field hops from record root to the leaf
    public required string Verb { get; init; }          // Set / Add / Remove / ReplaceAll / SetAtIndex / InsertAtIndex / Merge
    public string? Key { get; init; }                   // dict key or list index at the leaf
    public string? Value { get; init; }                 // the value, where the verb takes one
    public string[]? Values { get; init; }              // list ReplaceAll — the whole new contents
    public Dictionary<string, string>? Entries { get; init; } // dict ReplaceAll / Merge — key→value pairs
    public StructSpec? Struct { get; init; }            // build-from-parts spec: the arm for a polymorphic Set, OR the new element for a struct-element Add
    public IReadOnlyList<StructSpec>? Structs { get; init; } // a LIST of build-from-parts elements (composes=) — Add appends each, ReplaceAll clears then appends each
}

/// <summary>A modeled struct built FROM PARTS — the one composition primitive, used by a polymorphic Set, a
/// struct-element Add and absent-composition materialization alike. <see cref="Fields"/> is flat-leaf sugar;
/// <see cref="Sets"/> carries the general nested writes, applied to the freshly-built instance through the verb
/// engine itself.</summary>
public sealed class StructSpec
{
    public required string Type { get; init; }                 // concrete catalog name (arm type, list element type, …)
    public Dictionary<string, string>? Fields { get; init; }   // flat coercible sub-field → value (sugar = a Set-leaf each)
    public string[]? CtorArgs { get; init; }                   // positional ctor args for discriminator-/composition-ctor types
    public List<WriteRequest>? Sets { get; init; }             // general nested writes applied to the built instance (paths rooted at it)
}

/// <summary>The in-memory rulebook — <c>corpus.json</c> deserialised into the generator's own schema model, used for
/// pre-flight validation before any Mutagen mutation. The schema IS the validator data, by construction, and every
/// rejection names what was checked and what is legal. Contracts in docs/architecture/corpus-rulebook.md.</summary>
public sealed class CorpusRulebook
{
    readonly Corpus _corpus;
    /// <summary>Non-null only on a validate call the write path handed a load order to resolve link targets against.</summary>
    readonly LinkTargetLookup? _linkTargets;
    /// <summary>Memo for the printed legal-type list, per link target type; only a refusal fills it. Unlocked: the
    /// derived rulebook is a per-call object used on the calling thread.</summary>
    readonly Dictionary<string, string> _linkTargetNames = new(StringComparer.Ordinal);
    /// <summary>Non-null only on a HARVEST rulebook (<see cref="WithLinkHarvest"/>): the walk collects the FormLink
    /// values it reaches instead of type-checking them.</summary>
    readonly ICollection<string>? _linkSink;
    CorpusRulebook(Corpus corpus, LinkTargetLookup? linkTargets = null, ICollection<string>? linkSink = null)
        => (_corpus, _linkTargets, _linkSink) = (corpus, linkTargets, linkSink);

    /// <summary>This rulebook plus a load-order link-target resolver: the same corpus, with the FormLink TARGET TYPE
    /// check turned on. Derived once per write call, not per edit.</summary>
    public CorpusRulebook WithLinkTargets(LinkTargetLookup linkTargets) => new(_corpus, linkTargets);

    /// <summary>This rulebook in HARVEST mode: the same walk, with every FormLink value it reaches added to
    /// <paramref name="sink"/> and nothing type-checked. One walk decides both what is resolved and what is checked;
    /// contract in docs/architecture/corpus-rulebook.md.</summary>
    public CorpusRulebook WithLinkHarvest(ICollection<string> sink) => new(_corpus, null, sink);

    /// <summary>Walk one write for its FormLink values into the harvest sink, and return the verdict: the walk is
    /// <see cref="Validate"/> itself, so a write that contributed no value was decided by a walk identical to the
    /// checking one and the caller keeps this verdict.</summary>
    public string? CollectLinkValues(WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (_linkSink is null)
            throw new InvalidOperationException("CollectLinkValues needs a rulebook derived by WithLinkHarvest.");
        return Validate(req, siblingEditorIds);
    }

    /// <summary>HARVEST pass: record a same-call '@editorid' reference in the sink, so the sink count says this
    /// write's verdict depends on the sibling set.</summary>
    void HarvestSibling(string? value)
    {
        if (_linkSink is not null && value is not null) _linkSink.Add(value);
    }

    /// <summary>Resolves a FormLink value to the runtime type of the record it points at, or null when nothing in the
    /// load order carries it; supplied by the write path, which holds the captured view.</summary>
    public delegate Type? LinkTargetLookup(string formIdToken);

    /// <summary>The legal shapes of a condition FormLinkOrIndex target value, shared by the nested-sets and
    /// flat-fields rejects.</summary>
    const string FloiTargetForms =
        "a FormID (XXXXXX:Plugin.esp → form mode), a bare index, or 'alias N' / 'packdata N' (→ index mode)";

    public int TypeCount => _corpus.TotalTypes;
    public TypeSchema? Type(string name) => _corpus.Types.GetValueOrDefault(name);

    /// <summary>The record types a scope token names — a catalog name, a 4-char signature, or a polymorphic-base
    /// whose arms are records. Empty where the token names none, which is never a refusal here: the caller's own
    /// type resolution has already refused an unknown type.</summary>
    public IReadOnlyList<TypeSchema> RecordTypesNamed(string token)
    {
        var t = token.Trim();
        var hits = new List<TypeSchema>();
        foreach (var ts in _corpus.Types.Values)
        {
            if (ts.Kind == "record"
                && (string.Equals(ts.Name, t, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ts.Signature, t, StringComparison.OrdinalIgnoreCase)))
                hits.Add(ts);
            else if (ts.Kind == "polymorphic-base" && ts.Arms is { Count: > 0 } arms
                     && string.Equals(ts.Name, t, StringComparison.OrdinalIgnoreCase))
                foreach (var arm in arms)
                    if (Type(arm) is { Kind: "record" } a && !hits.Contains(a)) hits.Add(a);
        }
        return hits;
    }

    /// <summary>The cardinality the schema gives one step of a read path, rooted at <paramref name="root"/> — "list",
    /// "dict", "substruct", "value", and so on. Null where the schema cannot say (a hop it cannot descend, a field it
    /// does not know, a polymorphic disagreement), which a caller must read as "no answer", never as a refusal.</summary>
    public string? StepCardinality(TypeSchema root, IReadOnlyList<string> path, int index)
    {
        var current = root;
        for (int i = 0; i < index; i++)
        {
            var hop = FindField(current, path[i], out _, out var hopErr);
            if (hop is null || hopErr is not null) return null;
            if (hop.Cardinality is not ("substruct" or "polymorphic") || hop.TypeRef is not { } tr) return null;
            if (Type(tr) is not { } next) return null;
            current = next;
        }
        var field = FindField(current, path[index], out _, out var err);
        return err is null ? field?.Cardinality : null;
    }

    /// <summary>The ONE source of truth for where corpus.json lives, defaulting to the dev-harness location. The MCP
    /// server is launched from an arbitrary working directory and MUST set this to an absolute path at startup.</summary>
    public static string CorpusPath { get; set; } = Path.Combine("generated", "corpus.json");

    /// <summary>Load the validator rulebook from the configured <see cref="CorpusPath"/>.</summary>
    public static CorpusRulebook Load() => new(LoadCorpus());

    /// <summary>Load the validator rulebook from an explicit path (the harness; tests).</summary>
    public static CorpusRulebook Load(string corpusJsonPath) => new(LoadCorpus(corpusJsonPath));

    /// <summary>The raw deserialised <see cref="Corpus"/> from the configured <see cref="CorpusPath"/>, for consumers
    /// that want the catalog model directly rather than the validator wrapper.</summary>
    public static Corpus LoadCorpus() => LoadCorpus(CorpusPath);

    /// <summary>The raw deserialised <see cref="Corpus"/> from an explicit path. A missing file or a null
    /// deserialise throws a named exception — never a silent empty corpus.</summary>
    public static Corpus LoadCorpus(string corpusJsonPath)
    {
        if (!File.Exists(corpusJsonPath))
            throw new FileNotFoundException(
                $"corpus.json not found at {Path.GetFullPath(corpusJsonPath)}. Generate it first: " +
                "dotnet run --project src/housecarl-generator");
        return JsonSerializer.Deserialize<Corpus>(File.ReadAllText(corpusJsonPath))
            ?? throw new InvalidOperationException("corpus.json deserialised to null.");
    }

    /// <summary>Pre-flight. Returns null if the write is legal, else a fail-loud message.
    /// <paramref name="siblingEditorIds"/>, non-null only on the CREATE-batch path, is the set of editorids created
    /// earlier in the same call plus the record being created itself; null means an <c>@editorid</c> value is rejected
    /// loud. The FormLink TARGET TYPE check runs only on a rulebook derived by
    /// <see cref="WithLinkTargets"/>.</summary>
    public string? Validate(WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // (1) resolve the record, then validate rooted at it. ValidateFromType is shared with StructSpec validation.
        var recType = Type(req.RecordType);
        if (recType is null)
            return $"Unknown record type '{req.RecordType}': absent from the Mutagen corpus ({TypeCount} types). " +
                   "If Mutagen models it, that's a real coverage gap to surface — never a value to guess.";
        return ValidateFromType(recType, req, siblingEditorIds);
    }

    /// <summary>Validate a write rooted at an arbitrary type — a record OR a struct being built from parts, so a
    /// <see cref="StructSpec"/>'s nested writes validate by the identical leaf/path rules, recursively.
    /// <paramref name="pathSlot"/> is what the caller's own input slot is called at this root, and the paths this walk
    /// builds are relative to that root.</summary>
    string? ValidateFromType(TypeSchema root, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null,
        string pathSlot = "field_path")
    {
        if (req.Path.Length == 0)
            return "Empty path: a write must target at least one field.";

        // (2) walk the path, validating each intermediate hop's existence + descendability. A plain hop descends a
        // substruct; a bracketed hop (Effects[0]) steps INTO a collection element.
        var current = root;
        // …remembering whether any HOP was an owned child record, since a record can sit mid-path.
        FieldSchema? ownedChildHop = null;
        TypeSchema? ownedChildHopOwner = null;
        // …and the bracketed hop that produced the type being walked right now, for FindField's conflicting-shapes
        // reject. Cleared on every plain hop.
        ElementHop? elementHop = null;
        for (int i = 0; i < req.Path.Length - 1; i++)
        {
            if (!TrySeg(req.Path[i], out var segName, out var segKey, out var segErr)) return segErr;
            var incoming = elementHop;
            elementHop = null;
            var field = FindField(current, segName, out _, out var polyErr, incoming);
            if (polyErr is not null) return polyErr;
            if (field is null) return FieldNotFound(current, segName);
            if (ownedChildHop is null && SchemaClassifier.IsOwnedChildRecord(field, _corpus))
                (ownedChildHop, ownedChildHopOwner) = (field, current);

            if (segKey is null)
            {
                // plain hop — descend a substruct, or a STANDALONE polymorphic field, which descends to the
                // polymorphic-BASE catalog entry so FindField's over-arms search resolves the next hop.
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
                // Gendered field ([0]=male / [1]=female): a substruct whose TypeRef is GenderedItem<T>, descending to
                // the arm type T. The corpus-side recogniser is the "GenderedItem<" TypeRef; its engine twin is
                // WriteEngine.StepIntoElement's runtime IGenderedItem<>.
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
                // STRUCT; a record-element is resolved on its own, never walked into from a parent.
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
                // list mid-path index SHAPE — the same recognizer the leaf key block uses, so the two cannot drift.
                // The in-range bound stays apply's job.
                if (KeyShapeError(field, current.Name, segName, segKey) is { } ke) return ke;
                current = elem;
                elementHop = new ElementHop(PathTo(req.Path, i, segName), segKey, field, pathSlot);
            }
        }

        // (3) the leaf field — a bracketed LEAF is rejected (brackets navigate mid-path only; the leaf uses Key).
        if (!TrySeg(req.Path[^1], out var leafName, out var leafKey, out var leafErr)) return leafErr;
        if (leafKey is not null)
        {
            // A gendered field bracketed at the LEAF is not a list/dict — its halves are reached by name, so point at
            // .Male/.Female rather than the list-verb message below.
            var bracketed = FindField(current, leafName, out _, out _);
            if (bracketed is { Cardinality: "substruct", TypeRef: { } ltr }
                && ltr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                return $"Gendered field '{leafName}' on '{current.Name}' renders as [0]/[1] but is not a list — set its " +
                       $"halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).";
            // The remedy is SHAPE-scoped: WriteVerbs derives the keyed verb set from the leaf's own shape. The
            // verbless fallback is still reachable — a bracketed typo resolves no field — so it names no verbs.
            var head = $"Path '{req.Path[^1]}' brackets a collection element at the LEAF; brackets navigate mid-path only. ";
            // The remedy hands back the caller's own key, but only when it passes the same shape recognisers the
            // mid-path hop uses; a key that fails them gets the rule and the verb menu instead.
            if (bracketed is not null && WriteVerbs.OfField(bracketed, _corpus) is { } bshape)
                return KeyShapeError(bracketed, current.Name, leafName, leafKey) is null
                    ? head + "Target the collection field itself and address the element with the verb + Key: "
                      + $"{pathSlot}='{PathTo(req.Path, req.Path.Length - 1, leafName)}', key='{leafKey}' — "
                      + $"{WriteVerbs.HowToAddress(bshape)}."
                    : head + $"Target the collection field '{leafName}' itself and address the element with the verb "
                      + $"+ Key — {WriteVerbs.HowToAddress(bshape)}.";
            return head + "Target the collection field itself and address the element with the verb + Key.";
        }
        var leaf = FindField(current, leafName, out var leafOwner, out var leafPolyErr, elementHop);
        if (leafPolyErr is not null) return leafPolyErr;
        if (leaf is null) return FieldNotFound(current, leafName);

        // (3a-composes) batch struct-list surface: composes= short-circuits the singular verb/value pipeline. Gated
        // FIRST so composes on a dict/substruct gets a composes-specific message.
        if (req.Structs is not null)
            return ComposesLegality(leaf, leafOwner, req, siblingEditorIds);

        // (3a-copyfrom) CopyFrom transplants the WHOLE field from another plugin's version. Writable, not-identity and
        // a transplantable KIND are gated here; the SOURCE resolution happens later.
        if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
            return CopyFromLegality(leaf, leafOwner, ownedChildHop, ownedChildHopOwner);

        // (3a) verb legal for this cardinality?
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // (3a-owned) …and the verb whose cardinality answer is wrong for an OWNED CHILD RECORD: a keyless Remove on a
        // leaf that holds a record deletes that record and everything under it. Deliberate owned-child deletion is on
        // the RECORD axis, where the caller names the record.
        if (string.Equals(req.Verb, "Remove", StringComparison.Ordinal)
            && SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
            return $"'{leaf.Name}' on '{leafOwner.Name}' holds an owned child RECORD ({leaf.TypeRef}): clearing the " +
                   "field deletes that record and every record under it, implicitly and in one call. The list form of " +
                   "this family deletes a child too, but by INDEX — you name which one; there is no such target here. " +
                   "Delete it on the record axis instead, where the record is named: " + ToolNames.Remove +
                   " with the child's own FormID, plus the FormIDs of any records that child carries — the delete " +
                   "refuses until every record it would drop is named. (To see which record this is: read the " +
                   $"parent at depth=2 — the '{leaf.Name}' field shows the child's FormID.)";

        // (3b) record identity (FormKey/ModKey) is a flat, honest reject regardless of Mutagen's setter.
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{leafOwner.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // (3c) writable? (discriminators route to their own rejection)
        if (!leaf.Writable) return WritabilityRejection(leafOwner, leaf);

        // (4) value / key coercion + enum/legal-set legality
        return ValueLegality(leaf, req, siblingEditorIds);
    }

    /// <summary>Extract the arm type T from a gendered field's <c>GenderedItem&lt;T&gt;</c> TypeRef, returning the
    /// inner ref verbatim; null if the string is not that shape.</summary>
    static string? GenderedArmRef(string typeRef)
    {
        const string head = "GenderedItem<";
        if (!typeRef.StartsWith(head, StringComparison.Ordinal) || !typeRef.EndsWith(">", StringComparison.Ordinal))
            return null;
        var inner = typeRef[head.Length..^1].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>True iff the leaf is a <c>[Flags]</c>-attributed enum — the only scalar/enum kind the bit verbs
    /// operate on. Resolved from the field's OWN assembly-qualified type, never the simple-name catalog, and false
    /// when the AQ will not resolve. The same resolution the apply path keys on.</summary>
    static bool IsFlagsEnumLeaf(FieldSchema leaf)
    {
        if (leaf.Cardinality != "enum") return false;
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return false;
        var u = Nullable.GetUnderlyingType(rt) ?? rt;
        return u.IsEnum && u.IsDefined(typeof(FlagsAttribute), false);
    }

    // ---- verb × cardinality ----
    // Instance, not static: the Set-on-list remedy derives its alternatives from the leaf's SHAPE, which needs the
    // corpus to classify the element. This switch decides; WriteVerbs describes.
    string? VerbLegality(FieldSchema leaf, WriteRequest req)
    {
        var c = leaf.Cardinality;
        var hasKey = req.Key is not null;
        switch (req.Verb)
        {
            case "Set":
                if (c == "dict") return hasKey ? null : $"Set on dict field '{leaf.Name}' requires a key.";
                // The alternatives are DERIVED from the leaf's shape, not recited.
                if (c == "list") return $"Set is not valid on list '{leaf.Name}' — {PlacingRemedy(leaf)}.";
                return hasKey ? $"Set on {c} field '{leaf.Name}' does not take a key." : null;
            case "Add":
                // A dict Add coerces req.Key into the new entry's key, so key PRESENCE is gated here; a list Add
                // appends and takes no key. The key VALUE-shape is ValueLegality's job.
                if (c == "dict") return hasKey ? null : $"Add on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // A [Flags] enum accepts Add as a bit-SET, preserving the other bits. No key; the flag VALUE is gated
                // in ValueLegality.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Add on flags field '{leaf.Name}' takes no key — the value IS the flag to set." : null;
                return $"Add is only valid on a list/dict or a [Flags] enum; '{leaf.Name}' is {c}.";
            case "Remove":
                // A dict Remove identifies the entry BY KEY, so its key PRESENCE is gated; a list Remove is
                // by-index-OR-by-value, so it needs no key.
                if (c == "dict") return hasKey ? null : $"Remove on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // A [Flags] enum accepts Remove as a bit-CLEAR of ONE bit, distinct from the nullable-scalar
                // whole-clear below. No key; the flag VALUE is gated in ValueLegality.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Remove on flags field '{leaf.Name}' takes no key — the value IS the flag to clear." : null;
                return leaf.Nullable ? null : $"Remove on non-nullable {c} field '{leaf.Name}' is not valid.";
            case "ReplaceAll":
                return c is "list" or "dict" ? null : $"ReplaceAll is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "SetAtIndex":
                // A list SetAtIndex parses req.Key as the index, so PRESENCE is required up front; the VALUE-shape is
                // gated in ValueLegality's key block.
                if (c != "list") return $"SetAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"SetAtIndex on list '{leaf.Name}' requires an index.";
            case "InsertAtIndex":
                // SetAtIndex's structural twin: the same PRESENCE gate on the position to insert AT. The in-RANGE
                // bound is apply's, and insert's admits index == count, the append slot.
                if (c != "list") return $"InsertAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"InsertAtIndex on list '{leaf.Name}' requires an index (the position to insert AT; the list's length appends).";
            case "Merge":
                return c == "dict" ? null : $"Merge is only valid on dict; '{leaf.Name}' is {c}.";
            default:
                // The verb set comes from its one home (WriteVerbs.All), never a hand-typed copy.
                return $"Unknown verb '{req.Verb}'. Legal: {string.Join(", ", WriteVerbs.All)}.";
        }
    }

    /// <summary>"How do I put an element into this collection", derived from the leaf's own shape. Every call site
    /// must stay cardinality-gated to a list or a dict; the null arm says nothing rather than guessing.</summary>
    string PlacingRemedy(FieldSchema leaf) =>
        WriteVerbs.OfField(leaf, _corpus) is { } shape
            ? WriteVerbs.HowToPlace(shape)
            : "the verbs this field takes are in the op member's description";

    /// <summary>Validate a composes= batch whole, on a LIST of modeled elements only, through the same
    /// <see cref="StructElementLegality"/> the singular compose Add uses. All-or-nothing: the first bad element names
    /// itself and refuses the whole op.</summary>
    string? ComposesLegality(FieldSchema leaf, TypeSchema owner, WriteRequest req,
        IReadOnlyCollection<string>? siblingEditorIds)
    {
        // SHAPE BEFORE VERB, with the owned-child answer first of all; ordering contract in
        // docs/architecture/corpus-rulebook.md.
        if (SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
            return OwnedChildSetRefusal(leaf);
        // …and the COLLECTION twin of that shape, asked before the not-composable label below and not verb-scoped: a
        // record is not built from parts under any verb.
        if (IsOwnedChildRecordCollection(leaf))
            return OwnedChildRecordCollectionRefusal(leaf);
        if (leaf.Cardinality != "list")
            return $"composes= builds a LIST of modeled elements, but '{leaf.Name}' on '{owner.Name}' is a " +
                   $"{leaf.Cardinality}. (A dict takes keyed entries, not a positional list; a substruct/scalar takes " +
                   "compose= / value=.)";
        if (!IsComposableElement(leaf))
            // The slot guidance is DERIVED, because any verb reaches this sentence — the verb check sits below.
            return $"'{leaf.Name}' on '{owner.Name}' holds " +
                   (leaf.FormLinkTarget is not null ? "formlink" : "coercible") +
                   $" values ({leaf.ElementTypeRef ?? leaf.ElementType}), not modeled structs, so composes= has " +
                   $"nothing to build — {PlacingRemedy(leaf)}.";
        if (req.Verb is not ("Add" or "ReplaceAll"))
            // Reached only on a LIST of modeled elements, so the alternatives are derived as the SINGULAR ones.
            return $"composes= appends/replaces a LIST of modeled elements — use it with Add (append each) or " +
                   $"ReplaceAll (clear, then append each), not {req.Verb}. " +
                   // The shape is settled by the two checks above, so it is NAMED rather than looked up.
                   $"(One element at a time: {WriteVerbs.HowToPlaceOne(new CollectionShape(CollectionKind.List, ElementPlacement.Composed))}.)";
        if (req.Structs!.Count == 0)
            return req.Verb is "ReplaceAll"
                ? null   // ReplaceAll composes=[] = CLEAR the modeled list (the modeled twin of ReplaceAll values=[]); apply Clears + appends nothing
                : $"composes= for '{leaf.Name}' is empty — supply one or more element specs (only ReplaceAll composes=[] is meaningful, to clear the list).";
        for (int i = 0; i < req.Structs.Count; i++)
            if (StructElementLegality(leaf, req.Structs[i], siblingEditorIds) is { } elemErr)
                return $"composes[{i}]: {elemErr}";
        return null;
    }

    /// <summary>Validate a CopyFrom target leaf: writable, not record identity, and a TRANSPLANTABLE kind. The one
    /// non-transplantable kind is an owned-child record, in either shape, refused by name; everything else
    /// WriteEngine.CopyField transplants by construction.</summary>
    string? CopyFromLegality(FieldSchema leaf, TypeSchema owner, FieldSchema? ownedChildHop = null, TypeSchema? hopOwner = null)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not a copyable content field.";
        if (!leaf.Writable) return WritabilityRejection(owner, leaf);
        // TRANSPLANT REFUSES AT ANY DEPTH, because a path that merely runs THROUGH an owned child would write one
        // plugin's child record into another's. The in-place verbs through the same hop stay accepted.
        if (ownedChildHop is not null)
            return $"the path runs through '{ownedChildHop.Name}' on '{hopOwner?.Name ?? owner.Name}', which holds an " +
                   $"owned child RECORD ({ownedChildHop.TypeRef}); CopyFrom would read one plugin's child record and " +
                   $"write into another's, with neither named by this call — reported as an edit to " +
                   $"'{hopOwner?.Name ?? owner.Name}'. Copy at the CHILD record itself, addressed by its own FormID " +
                   "(read the parent at depth=2 — the field shows it), or carry the whole record across with " +
                   ToolNames.Forward + ".";
        if (SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
            return $"'{leaf.Name}' on '{owner.Name}' holds an owned child RECORD ({leaf.TypeRef}); CopyFrom copies a " +
                   "FIELD's value, not a record — copying it here would write another plugin's record, with its own " +
                   "FormID and everything under it, in as this parent's child. To carry that record across from " +
                   "another plugin use " + ToolNames.Forward + " on the CHILD record itself; read the parent at " +
                   $"depth=2 and the '{leaf.Name}' field shows the child's FormID. To give a parent a child it does " +
                   "not have, create one on the record axis: " + ToolNames.Create + " with parent= the parent's " +
                   $"FormID and collection='{leaf.Name}' in its records= element.";
        // The same recogniser as the other two collection doors; shared predicate, per-door remedy.
        if (IsOwnedChildRecordCollection(leaf))
            return $"'{leaf.Name}' on '{owner.Name}' holds owned child records ({leaf.ElementTypeRef}); CopyFrom copies a " +
                   "FIELD's value, not owned child records. To carry the WHOLE record from another plugin use " +
                   ToolNames.Forward + "; a child record is authored on its own (" + ToolNames.Create + " with parent= in its records= element).";
        if (leaf.Cardinality == "dict")
            return $"'{leaf.Name}' on '{owner.Name}' is a dict field; CopyFrom transplants scalar / formlink / list / " +
                   "sub-struct fields — a dict isn't transplanted yet. Set its entries individually, or forward the whole record.";
        return null;
    }

    /// <summary>The ONE sentence every value-shaped Set at an owned child record gets — <c>value=</c>, <c>compose=</c>
    /// and <c>composes=</c> alike — so the three doors cannot point at each other's refused remedies. The descent
    /// clause is conditional because a path through the parent reaches a child only when the copy being written
    /// already carries one.</summary>
    static string OwnedChildSetRefusal(FieldSchema leaf) =>
        $"'{leaf.Name}' holds an owned child RECORD ({leaf.TypeRef}): a record is not a part of its parent, so it is " +
        $"neither built from parts (compose= / composes=) nor set from a value (value=). {AddressChildByFormId(leaf.Name)} A path through " +
        "the parent reaches a child only when the record being written already carries one, which a patch's fresh " +
        "override of a parent never does; to give a parent a child it lacks, create one on the record axis — " +
        ToolNames.Create + $" with parent= the parent's FormID and collection='{leaf.Name}'.";

    /// <summary>How an owned child record that ALREADY EXISTS is written: on the record axis, by its own FormID — one
    /// sentence, shared by the value-shaped Set refusal and the element remedy.</summary>
    static string AddressChildByFormId(string fieldName) =>
        $"Address the child record itself by its own FormID — read the parent at depth=2 and the '{fieldName}' field shows it.";

    /// <summary>True iff a leaf is the COLLECTION form of the owned-child shape — the collection twin of
    /// <see cref="SchemaClassifier.IsOwnedChildRecord"/>, which matches only the singular shape. One named predicate,
    /// because three separate doors ask the question.</summary>
    bool IsOwnedChildRecordCollection(FieldSchema leaf) =>
        leaf.Cardinality is "list" or "dict" && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record;

    /// <summary>The ONE sentence every element-PLACING door at an owned-child-record COLLECTION gets, shared for the
    /// reason <see cref="OwnedChildSetRefusal"/> is. Its remedy is the one that works for this shape: create with
    /// <c>parent=</c>, plus the <c>collection=</c> a parent with more than one fitting list requires.</summary>
    static string OwnedChildRecordCollectionRefusal(FieldSchema leaf) =>
        $"'{leaf.Name}' holds owned child records ({leaf.ElementTypeRef}); a child record is created on its " +
        "own (the record axis), not added into a parent's collection by a write verb. Use " + ToolNames.Create + " with " +
        "parent= the parent's FormID in its records= element (and collection= there when the parent holds more " +
        "than one fitting list) — surfaced here, never accepted and thrown at apply.";

    // ---- writability rejection ----
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
        // Same-call sibling reference ("@editorid"), gated BEFORE any verb/cardinality dispatch. Its legal placements
        // are in docs/architecture/corpus-rulebook.md; anywhere else it is refused loud below.
        if (WriteEngine.IsSameCallSiblingRef(req.Value, out var sibEdid))
        {
            HarvestSibling(req.Value);
            if (siblingEditorIds is null)
                return $"'{req.Value}' for '{leaf.Name}': a '@editorid' reference names a record being created in the " +
                       "SAME " + ToolNames.Create + " call — when editing an existing record " +
                       "there are no same-call creations to point at. Use the target's FormID (a record already " +
                       "written into a houseCARL patch is addressable by FormID with into= that patch).";
            // The singular value must land on a FormLink TARGET — a singular formlink leaf or a formlink-element list.
            var onFormLink = leaf.Cardinality == "formlink"
                          || (leaf.Cardinality == "list" && leaf.FormLinkTarget is not null);
            if (!onFormLink)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid on a FormLink field, but " +
                       $"'{leaf.Name}' is a {leaf.Cardinality}.";
            // …and the verb must fit the target's shape: Set a singular link, Add to a link list.
            var verbFits = (leaf.Cardinality == "formlink" && req.Verb == "Set")
                        || (leaf.Cardinality == "list" && req.Verb == "Add");
            if (!verbFits)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid as a Set value on a singular " +
                       $"FormLink field or an Add value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' " +
                       $"is a {leaf.Cardinality}).";
            // A stray compose spec riding alongside an admitted '@' value would skip validation yet be walked by
            // apply's substitution recursion, so refuse it loud.
            if (req.Struct is not null)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "value IS the whole FormLink target; remove struct=.";
            return siblingEditorIds.Contains(sibEdid) ? null
                : $"Same-call reference '{req.Value}' for '{leaf.Name}': no record with editorid '{sibEdid}' is created " +
                  "EARLIER in this call (a record may also reference ITSELF by its own editorid) — declare it before " +
                  "the record that references it (in spec order).";
        }
        // A sibling token inside req.Values — legal ONLY as a ReplaceAll on a FormLink LIST. Validated whole here and
        // RETURNED, so the '@' tokens never reach the FormLink value check below.
        if (req.Values is { } vals && vals.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
        {
            if (siblingEditorIds is null)
                return $"a '@editorid' reference for '{leaf.Name}' names a record being created in the SAME " +
                       ToolNames.Create + " call — when editing an existing record there " +
                       "are no same-call creations to point at. Use the target's FormID (a record already written " +
                       "into a houseCARL patch is addressable by FormID with into= that patch).";
            if (!(req.Verb == "ReplaceAll" && leaf.Cardinality == "list" && leaf.FormLinkTarget is not null))
                return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported as an Add value or a " +
                       $"ReplaceAll value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' is a {leaf.Cardinality}).";
            // The values-branch twin of the stray-compose guard above.
            if (req.Struct is not null)
                return $"a '@editorid' same-call reference for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "entries ARE the FormLink elements; remove struct=.";
            foreach (var v in vals)
            {
                if (WriteEngine.IsSameCallSiblingRef(v, out var vEd))
                {
                    HarvestSibling(v);
                    if (!siblingEditorIds.Contains(vEd))
                        return $"Same-call reference '@{vEd}' for '{leaf.Name}': no record with editorid '{vEd}' is " +
                               "created EARLIER in this call (a record may also reference ITSELF by its own editorid) — " +
                               "declare it before the record that references it (in spec order).";
                }
                else if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
                // A literal FormID mixed in beside the siblings is type-checked here; a sibling is not, because its
                // record does not exist yet.
                else if (LinkTypeRefusal(leaf, v, "element") is { } mixedTypeErr) return mixedTypeErr;
            }
            return null;
        }
        // A sibling token inside a dict Entries' VALUES — no formlink-valued dict is modeled, so this stays refused.
        if (req.Entries is { } ents && ents.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported on a FormLink list, not " +
                   "inside a dict value — no formlink-valued dict is modeled.";
        // KEY / INDEX VALUE-SHAPE — the shape twin of VerbLegality's key/index PRESENCE gate, through the same
        // recognizers apply uses: the dict key's real CLR type off the field's own dictionary AQ, the list index via
        // WriteEngine.IsValidListIndexValue. PRESENCE stays VerbLegality's job; this is purely SHAPE.
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
        if (leaf.Cardinality == "list" && req.Verb is "SetAtIndex" or "InsertAtIndex" or "Remove" && req.Key is { } lIdx
            && !WriteEngine.IsValidListIndexValue(lIdx))
            return $"Illegal list index '{lIdx}' for '{leaf.Name}': expected a non-negative integer. " +
                   "(Whether the index is in range is checked at apply, against the live list.)";
        if (req.Verb is "Set" && leaf.Cardinality == "dict")
        {
            // Key shape is gated by the key block above. A struct/arm-VALUED dict Set replaces an entry's value with
            // a build-from-parts element, through the same StructElementLegality the Add path uses; a
            // coercible-VALUE dict Set coerces.
            if (IsComposableElement(leaf)) return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            if (req.Value is null) return $"Set on dict '{leaf.Name}' requires a value.";
            return CheckValue(leaf.ElementType, req.Value, $"dict value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
        }
        if (req.Verb is "Set" && leaf.Cardinality == "polymorphic")
            return ArmLegality(leaf, req.Struct, siblingEditorIds);
        // A whole modeled-STRUCT substruct leaf is Set by composing its value FROM PARTS — the leaf twin of the
        // dict-element and polymorphic-arm compose paths, through the same StructSpecContents. SchemaClassifier
        // scopes it, so a coercible substruct keeps its plain-value Set below.
        if (req.Verb is "Set" && SchemaClassifier.IsComposableSubstructLeaf(leaf, _corpus))
            return StructLeafLegality(leaf, req.Struct, siblingEditorIds);
        if (req.Verb is "Set")
        {
            // A compose spec reaching HERE means the leaf is not a compose target: a coercible substruct, a formlink,
            // a plain scalar — which get the plain-value path named — or an OWNED CHILD RECORD, for which that advice
            // is a dead end, so it gets its own sentence.
            if (SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
                return OwnedChildSetRefusal(leaf);
            if (req.Value is null)
                return req.Struct is not null
                    ? $"'{leaf.Name}' is set from a plain value (value=…), not a compose spec."
                    : $"Set on '{leaf.Name}' requires a value.";
            // formlink / substruct-whole: the engine must be able to coerce the leaf's whole type. A condition
            // FormLinkOrIndex has its target-value SHAPE validated here, through the engine's shared recogniser.
            if (leaf.Cardinality is "formlink" or "substruct")
            {
                var faq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
                if (WriteEngine.ResolveType(faq) is { } frt && WriteEngine.IsFormLinkOrIndex(frt))
                    return WriteEngine.TryClassifyFloiValue(req.Value) ? null
                        : $"Illegal condition target '{req.Value}' for '{leaf.Name}': expected {FloiTargetForms}.";
                // A NORMAL FormLink Set — the FormKey VALUE shape is validated at the gate, through the recognizer
                // the engine's apply path shares. A null-synonym clears the link; otherwise it must parse.
                if (leaf.Cardinality == "formlink")
                    return WriteEngine.IsValidFormLinkValue(req.Value)
                        ? LinkTypeRefusal(leaf, req.Value, "target")
                        : $"Illegal FormLink target '{req.Value}' for '{leaf.Name}': expected a FormID " +
                          "(XXXXXX:Plugin.esp) or a null-clear ('0', '00000000', 'Null', '000000:Null').";
                return CoercibilityReject(leaf);
            }
            return CheckValue(leaf.Type, req.Value, $"value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // Add/Remove on a [Flags] enum are bit-SET / bit-CLEAR: the flag NAME or bits are validated here with the same
        // CheckValue recognizer a Set uses. Gated ahead of the collection branches, which would ignore an enum leaf.
        if (req.Verb is "Add" or "Remove" && IsFlagsEnumLeaf(leaf))
        {
            if (req.Value is null)
            {
                // Add always needs the bit to set. A VALUELESS Remove keeps its other meaning, the whole-clear of a
                // nullable scalar, so it is allowed iff the field is nullable, else redirected to Set '0'.
                if (req.Verb == "Add")
                    return $"Add on flags field '{leaf.Name}' requires a flag value (the bit to set).";
                return leaf.Nullable ? null
                    : $"Remove on flags field '{leaf.Name}' needs the flag to clear (value=<flag>) — a non-nullable flags " +
                      "field can't be whole-cleared; to turn ALL bits off, Set it to '0'.";
            }
            return CheckValue(leaf.Type, req.Value, $"flag value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // ELEMENT-VALUE PRESENCE — the collection twin of the singular Set "requires a value" reject above, scoped to
        // the verbs that consume the singular req.Value on a coercible-element collection.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex"
            && req.Value is null && IsValueCoercibleElement(leaf))
            return $"{req.Verb} on '{leaf.Name}' requires an element value.";
        // FormLink-ELEMENT collection value-SHAPE — the collection twin of the singular formlink Set check above,
        // validating every supplied element value with the same IsValidFormLinkValue predicate. Element VALUES only:
        // dict key shape is the key block's job, and key/index presence is VerbLegality's.
        if (leaf.Cardinality is "list" or "dict" && leaf.FormLinkTarget is not null)
        {
            if (req.Value is { } ev && !WriteEngine.IsValidFormLinkValue(ev)) return FormLinkElementReject(ev, leaf);
            foreach (var v in req.Values ?? Array.Empty<string>())
                if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            foreach (var kv in req.Entries ?? new())
                if (!WriteEngine.IsValidFormLinkValue(kv.Value)) return FormLinkElementReject(kv.Value, leaf);
            // …and the TYPE of every element whose shape just passed, scoped to the verbs that PUT a value IN. Remove
            // is exempt at every slot, or the gate would refuse the one call that repairs a wrong-typed list.
            if (req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex"
                && LinkTypeRefusal(leaf, req.Value, "element") is { } elemTypeErr) return elemTypeErr;
            if (req.Verb is "ReplaceAll")
                foreach (var v in req.Values ?? Array.Empty<string>())
                    if (LinkTypeRefusal(leaf, v, "element") is { } valsTypeErr) return valsTypeErr;
            if (req.Verb is "Merge" or "ReplaceAll")
                foreach (var kv in req.Entries ?? new())
                    if (LinkTypeRefusal(leaf, kv.Value, "element") is { } entTypeErr) return entTypeErr;
        }
        // NON-FORMLINK coercible-element collection value-SHAPE — the value twin of the formlink block above, scoped
        // so the two cover every coercible element with no double-check and no gap, and faithful to which slot apply
        // actually coerces, so a value apply never reads is not over-rejected.
        if (leaf.Cardinality is "list" or "dict" && IsValueCoercibleElement(leaf) && leaf.FormLinkTarget is null)
        {
            string? ElemShape(string? v) =>
                CheckValue(leaf.ElementType, v, $"element value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
            if (req.Value is { } ev
                && (req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex" || (req.Verb is "Remove" && req.Key is null))
                && ElemShape(ev) is { } evErr)
                return evErr;
            // Slot-faithful to apply: each loop is scoped to its slot's cardinality, so a stray off-cardinality slot
            // apply ignores is not over-rejected.
            if (leaf.Cardinality == "list" && req.Verb is "ReplaceAll")
                foreach (var v in req.Values ?? Array.Empty<string>())
                    if (ElemShape(v) is { } valsErr) return valsErr;
            if (leaf.Cardinality == "dict" && req.Verb is "Merge" or "ReplaceAll")
                foreach (var kv in req.Entries ?? new())
                    if (ElemShape(kv.Value) is { } entErr) return entErr;
        }
        // RECORD-ELEMENT collection verb — a child record is allocated on the record axis, never built into a
        // parent's collection by the verb engine. Verb-scoped to the create-oriented verbs, so a record Remove by
        // index stays accepted; sentence and predicate are shared with the composes= door.
        if (IsOwnedChildRecordCollection(leaf) && req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex" or "ReplaceAll")
            return OwnedChildRecordCollectionRefusal(leaf);
        // Collection-verb value legality. A struct-element OR arm-element list takes a build-from-parts StructSpec on
        // Add, NOT a plain value: an ARM element composes by its concrete arm type and is validated against that arm's
        // own schema (the VMAD shape). A coercible-element list takes a plain value the engine coerces.
        if (leaf.Cardinality is "list" or "dict" && IsComposableElement(leaf))
        {
            // Add, SetAtIndex and InsertAtIndex all build the element FROM PARTS through the same
            // StructElementLegality, list and dict alike, so a composed element is identical whichever slot it lands
            // in. Index presence and shape are already gated above; the in-RANGE bound is apply's.
            if (req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex")
                return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            // ReplaceAll/Merge of modeled elements stay deferred — distinct input surfaces not opened here — and must
            // stay listed, or they fall through to accept and throw at apply.
            if (req.Verb is "ReplaceAll" or "Merge")
                // Emitted for a list OR a dict, so its alternatives come from the leaf's own shape. It says what is
                // MISSING rather than what the caller sent, since a bare ReplaceAll also lands here.
                return $"'{leaf.Name}' holds modeled elements ({leaf.ElementTypeRef}); {req.Verb} has no " +
                       $"build-from-parts input on this call — {PlacingRemedy(leaf)}.";
        }
        // Remove-BY-VALUE on a NON-PLAIN-VALUE element: an element that is neither coercible nor formlink has no
        // plain-value form to match. A Remove by INDEX stays accepted, and a dict Remove is excluded.
        if (req.Verb == "Remove" && leaf.Cardinality == "list" && req.Key is null
            && leaf.FormLinkTarget is null && !IsValueCoercibleElement(leaf))
            return $"'{leaf.Name}' holds modeled/record elements ({leaf.ElementTypeRef ?? leaf.ElementType}); remove one " +
                   "BY INDEX (Remove with a Key = its position), not by value — a modeled or record element has no " +
                   "plain-value form to match. (Value-based removal of such an element is a later surface.)";
        return null;
    }

    /// <summary>True iff the leaf is a collection whose ELEMENT is built FROM PARTS on Add: a modeled-struct element,
    /// or a polymorphic-union arm element. Derived via the shared <see cref="SchemaClassifier"/>.</summary>
    bool IsComposableElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.Struct or ElementKind.Arm;

    /// <summary>True iff the leaf is a collection whose ELEMENT the engine sets by COERCING a single plain value —
    /// exactly the kinds the value-presence gate keys off. Struct/Arm and Record elements are excluded. Broader than
    /// <see cref="SchemaClassifier.CoercibleElement"/>, which is ScalarCoercible only.</summary>
    bool IsValueCoercibleElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.ScalarCoercible or ElementKind.WholeCoercible;

    /// <summary>Validate a struct-element Add: the spec must be present, its type must match the list's element type
    /// or, on a polymorphic base, be one of its ARMS, and its contents must validate against the SPEC's own schema.
    /// Generic over every polymorphic-base element family — no per-type wiring (cornerstone).</summary>
    string? StructElementLegality(FieldSchema leaf, StructSpec? spec, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (spec is null)
            return $"'{leaf.Name}' takes a build-from-parts element (a modeled {leaf.ElementTypeRef}); supply a compose spec, not a plain value.";
        var er = leaf.ElementTypeRef!;
        var elemSchema = Type(er);
        if (elemSchema is null) return $"Element type '{er}' for '{leaf.Name}' absent from corpus.";

        // A polymorphic BASE is composed by choosing a concrete ARM, never the base itself. The recognizer is the
        // corpus poly-base KIND, not Type.IsAbstract, and a concrete base lists ITSELF among its arms, so it is
        // filtered out of the legal-arms set everywhere — the arm-match check and every message alike.
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

    /// <summary>Validate a whole-struct compose Set on a SUBSTRUCT leaf — the leaf twin of
    /// <see cref="StructElementLegality"/>, keyed on the leaf's own <see cref="FieldSchema.TypeRef"/> and validated
    /// through the same <see cref="StructSpecContents"/>. Reached only for a
    /// <see cref="SchemaClassifier.IsComposableSubstructLeaf"/> leaf, whose TypeRef is a concrete struct or arm, so a
    /// straight name-match is correct — no poly-base arm resolution.</summary>
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

    /// <summary>Validate a build-from-parts spec's CONTENTS against its declared struct type: flat fields must exist
    /// and coerce, nested sets validate by the identical path/leaf rules. Shared by the polymorphic-arm Set and the
    /// struct-element Add.</summary>
    string? StructSpecContents(StructSpec spec, TypeSchema structSchema, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // Positional ctor_args value-SHAPE and ARITY, through WriteEngine.TryRecognizeCtorArgs, which mirrors
        // Instantiate exactly. Checked at the TOP so it runs for both call sites and reports before the field checks.
        if (spec.CtorArgs is { } ctorArgs && WriteEngine.TryRecognizeCtorArgs(spec.Type, ctorArgs) is { } ctorErr)
            return ctorErr;
        // The fields BuildStruct hands to the constructor instead of setting — legal to name even when the property
        // has no setter, and only on the no-ctor_args lane, which is exactly when BuildStruct skips them.
        var ctorCarried = spec.CtorArgs is null
            ? WriteEngine.CtorConsumedFields(spec.Type, spec.Fields)
            : (IReadOnlySet<string>)new HashSet<string>();
        foreach (var f in spec.Fields ?? new())
        {
            var af = structSchema.Fields.FirstOrDefault(x => x.Name == f.Key);
            if (af is null) return FieldNotFound(structSchema, f.Key);
            // A field the apply cannot set is refused HERE, not thrown mid-apply.
            if (!af.Writable && !ctorCarried.Contains(f.Key)) return WritabilityRejection(structSchema, af);
            // A '@editorid' same-call reference in a compose FIELD — legal on a singular FORMLINK field in create
            // context only, mirroring the top-level singular-value gate. Gated BEFORE CheckValue, which would reject
            // the '@' token as a malformed FormLink.
            if (WriteEngine.IsSameCallSiblingRef(f.Value, out var fEd))
            {
                HarvestSibling(f.Value);
                if (siblingEditorIds is null)
                    return $"a '@editorid' reference for '{f.Key}' on '{spec.Type}' names a record being created in the " +
                           "SAME " + ToolNames.Create + " call — when editing an existing record " +
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
            // A composed field is a link slot like any other.
            if (af.Cardinality == "formlink" && LinkTypeRefusal(af, f.Value, "target") is { } linkErr) return linkErr;
        }
        // With no ctor_args the type still has to be BUILDABLE, through the very method BuildStruct instantiates
        // through. Runs AFTER the field loop, so a bad field is reported as itself, not as a missing ctor arg.
        if (spec.CtorArgs is null && WriteEngine.TryRecognizeInstantiable(spec.Type, spec.Fields) is { } buildErr)
            return buildErr;
        foreach (var s in spec.Sets ?? new())
        {
            // The verbs whose input a nested set has no member to carry: unrefused, each consumes nothing and reports
            // a write that did not happen.
            if (NestedSlotlessRefusal(s.Verb, siblingEditorIds) is { } slotless) return slotless;
            // siblingEditorIds threads through, so a nested '@editorid' validates by the same gates as a top-level
            // value; and the slot name goes with it, because these paths are rooted at the STRUCT.
            if (ValidateFromType(structSchema, s, siblingEditorIds, "path") is { } e) return e;
        }
        return null;
    }

    /// <summary>The refusal for a verb a compose's nested sets cannot feed, else null. The transplanting verb's remedy
    /// splits by LANE on the same signal the <c>@editorid</c> gate reads, because the create surface has no CopyFrom
    /// op to send the caller to.</summary>
    static string? NestedSlotlessRefusal(string verb, IReadOnlyCollection<string>? siblingEditorIds)
    {
        var (reads, remedy) = verb switch
        {
            "ReplaceAll" => ("replaces a collection's whole contents from values=",
                             "set the elements one at a time instead"),
            "Merge" => ("merges the pairs given in entries=",
                        "set the entries one at a time instead, each with its own key="),
            WriteVerbs.Transplanting => ("copies a field from ANOTHER record, which only an op naming the source can do",
                             siblingEditorIds is null
                                 ? "make it its own op on the field itself (from= / from_source=)"
                                 : $"create the record here and copy the field in with a second {ToolNames.Apply} "
                                   + "call (op='CopyFrom') into the same patch"),
            _ => (null, null),
        };
        return reads is null ? null
            : $"'{verb}' is not a verb a compose's nested sets take — it {reads}, and a nested set is "
              + $"path/verb/value/key/compose with no member to carry that, so {remedy}. "
              + $"Legal here: {string.Join(", ", WriteVerbs.InCompose)}.";
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

    // ---- FormLink TARGET TYPE ---------------------------------------------------------------------------------
    //  The value-SHAPE checks above prove a FormID parses; this gate decides WHAT it may point at, from the link
    //  target interface the generator stamps on every formlink field. Contract in
    //  docs/architecture/corpus-rulebook.md.

    /// <summary>The refusal when a FormLink value points at a record type the field cannot link to, else null.
    /// <paramref name="slot"/> reads "target" for a singular link and "element" for a collection one.</summary>
    string? LinkTypeRefusal(FieldSchema leaf, string? value, string slot)
    {
        if (value is null) return null;
        if (WriteEngine.IsFormKeyNullSynonym(value)) return null;              // a clear points at nothing
        if (leaf.FormLinkTargetAssemblyQualified is not { } aq) return null;
        // HARVEST pass: collected here, at the single place the check reads a link value.
        if (_linkSink is not null) { _linkSink.Add(value); return null; }
        if (_linkTargets is null) return null;
        if (WriteEngine.ResolveType(aq) is not { } target) return null;
        if (_linkTargets(value) is not { } actual) return null;                // the order cannot say — never a guess
        if (target.IsAssignableFrom(actual)) return null;
        return $"Illegal FormLink {slot} '{value}' for '{leaf.Name}': that record is a " +
               $"{RecordNaming.StripOverlay(actual.Name)}, but '{leaf.Name}' links to {AllowedLinkTypes(leaf, target, aq)}.";
    }

    /// <summary>The record types the corpus says satisfy a link target interface, as one printed phrase: all named
    /// while few enough to act on, else the target's own kind and how many types it covers. Falls back to the
    /// interface's bare name where no modeled record satisfies it.</summary>
    string AllowedLinkTypes(FieldSchema leaf, Type target, string aq)
    {
        if (_linkTargetNames.TryGetValue(aq, out var cached)) return cached;
        var names = new List<string>();
        foreach (var ts in _corpus.Types.Values)
            if (ts.Kind == "record" && WriteEngine.ResolveType(ts.GetterInterfaceAssemblyQualified) is { } gi
                && target.IsAssignableFrom(gi))
                names.Add(ts.Name);
        names.Sort(StringComparer.Ordinal);
        var bare = RecordNaming.StripInterfaceToConcrete(leaf.FormLinkTarget ?? target.Name);
        const int cap = 12;
        var phrase = names.Count switch
        {
            0 => bare,
            1 => names[0],
            _ when names.Count <= cap => "one of: " + string.Join(", ", names),
            _ => $"any {bare} record ({names.Count} record types qualify)",
        };
        return _linkTargetNames[aq] = phrase;
    }

    /// <summary>The loud per-element rejection for a malformed FormLink collection ELEMENT — the same legal set as the
    /// singular formlink Set reject, naming the offending value.</summary>
    static string FormLinkElementReject(string value, FieldSchema leaf) =>
        $"Illegal FormLink element '{value}' for '{leaf.Name}': expected a FormID (XXXXXX:Plugin.esp) " +
        "or a null-clear ('0', '00000000', 'Null', '000000:Null').";

    /// <summary>Validate a polymorphic Set: the arm must be a legal arm of the field, and its contents (flat fields +
    /// nested sets) must validate against the arm type — the same composition-contents check a struct-element Add uses.</summary>
    string? ArmLegality(FieldSchema leaf, StructSpec? arm, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (arm is null) return $"Set on polymorphic field '{leaf.Name}' requires an arm (which arm + its data).";
        // The standalone-poly-FIELD twin of the StructElementLegality base-reject: the base is filtered out of the
        // legal set and composing it is rejected. A CONCRETE base is also the one shape where "no listed arm fits" is
        // real, so its refusal names the working dotted-subfield lane; the recognizer is the field's mutable AQ
        // resolving to a concrete class.
        var baseName = leaf.TypeRef;
        var legal = (leaf.Arms ?? (baseName is { } tr ? Type(tr)?.Arms : null) ?? new()).Where(a => a != baseName).ToList();
        if (baseName is not null && arm.Type == baseName)
        {
            var dottedLane = leaf.MutableTypeAssemblyQualified is { } aq
                             && WriteEngine.ResolveType(aq) is { IsAbstract: false, IsInterface: false }
                ? $" If no listed arm fits this record (the field's live type is the base itself), skip the compose " +
                  $"and Set the base's subfields by dotted path instead ('{leaf.Name}.<subfield>' — the field " +
                  $"auto-instantiates on first Set)."
                : "";
            return $"'{arm.Type}' is the polymorphic base of '{leaf.Name}' — the base itself cannot be composed; " +
                   $"choose a concrete arm. Legal arms: {string.Join(", ", legal)}.{dottedLane}";
        }
        if (!legal.Contains(arm.Type))
            return $"Illegal arm '{arm.Type}' for '{leaf.Name}'. Legal arms: {string.Join(", ", legal)}.";
        var armSchema = Type(arm.Type);
        if (armSchema is null) return $"Arm '{arm.Type}' absent from corpus.";
        return StructSpecContents(arm, armSchema, siblingEditorIds);
    }

    /// <summary>Resolve a dict leaf's KEY clr type from its own dictionary AQ — the same type the apply path keys on.
    /// Returns null if it cannot be resolved, and the caller's <see cref="CheckValue"/> then degrades to the
    /// catalog-by-name enum check. Mutable AQ preferred over getter.</summary>
    static System.Type? DictKeyType(FieldSchema leaf)
    {
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (aq is null || WriteEngine.ResolveType(aq) is not { IsGenericType: true } dt) return null;
        var args = dt.GetGenericArguments();
        return args.Length == 2 ? args[0] : null;
    }

    /// <summary>Enum → must be a legal value of the field's REAL enum type; primitive → must coerce to the AQ-resolved
    /// type. Validation prefers the per-field assembly-qualified type over the corpus's simple-name catalog, which
    /// collides on shared enum names; the catalog is the fallback only when the AQ will not resolve.</summary>
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
            // A condition FormLinkOrIndex target does not coerce via the parentless Coerce family — its parent arm
            // carries the mode bit — so its target-value SHAPE is validated the same way the nested-Sets path does.
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

    /// <summary>The bracketed hop that produced the type currently being walked: the collection field's dotted path,
    /// the key, its schema, and the input slot that path belongs in. Held for the immediately preceding hop only, and
    /// the slot travels with the hop because the path does.</summary>
    readonly record struct ElementHop(string Path, string Key, FieldSchema Field, string Slot);

    /// <summary>The dotted path of the hop at <paramref name="index"/>, with its own bracket dropped — earlier hops
    /// keep theirs, because they are still navigation. Relative to the root the walk started from, so the slot it is
    /// printed in is whatever that root's slot is called.</summary>
    internal static string PathTo(string[] path, int index, string name) =>
        string.Join(".", path.Take(index).Append(name));

    /// <summary>Is <paramref name="key"/> a shape this collection can actually be indexed by? A list wants a parseable
    /// non-negative int32, a dict a value of the key's real CLR type; the in-range bound stays apply's job. One
    /// recogniser for both the mid-path hop and the leaf bracket, so a remedy only hands back a key it has
    /// checked.</summary>
    string? KeyShapeError(FieldSchema field, string ownerName, string segName, string key) => field.Cardinality switch
    {
        "list" when !WriteEngine.IsValidListIndexValue(key) =>
            $"List '{segName}' on '{ownerName}' must be indexed by a non-negative integer; got '{key}'.",
        "dict" => CheckValue(field.KeyType, key, $"dict key for '{segName}'", DictKeyType(field)?.AssemblyQualifiedName),
        _ => null,
    };

    /// <summary>What to do about a field the over-arms search refused to pick an arm for: when the caller is standing
    /// inside a collection element it names the container's path, the caller's own key and the verbs that shape takes,
    /// through <see cref="WriteVerbs.HowToPlaceOneAt"/>, the filter that matches the sentence. Without a bracketed hop
    /// it states the rule instead, and an OWNED-RECORD element names no container path and no key.</summary>
    string ElementRemedy(ElementHop? elementHop) =>
        elementHop is { } h && WriteVerbs.OfField(h.Field, _corpus) is { } shape
            ? shape.Element == ElementPlacement.OwnedRecord
                ? $"'{h.Field.Name}' holds owned child RECORDS ({h.Field.ElementTypeRef}), so no write verb reaches " +
                  $"the element through its parent. {AddressChildByFormId(h.Field.Name)}"
                : $"Read the element to learn its concrete arm, then write the whole element in one call, composing " +
                  $"that arm: {h.Slot}='{h.Path}', key='{h.Key}' — {WriteVerbs.HowToPlaceOneAt(shape)}."
            : "Read the element first to learn its concrete arm, then target a field whose shape is unambiguous.";

    /// <summary>How many field names this refusal prints before it cuts, past which it names where the rest are.</summary>
    const int FieldListCap = 40;

    /// <summary>How far past the cap a type may run and still print whole, so a type a name or two over does not hide
    /// one field behind a sentence longer than the field.</summary>
    const int FieldListCapSlack = 6;

    string FieldNotFound(TypeSchema owner, string name)
    {
        var cuts = owner.Fields.Count > FieldListCap + FieldListCapSlack;
        var sample = owner.Fields.Select(f => f.Name).Take(cuts ? FieldListCap : owner.Fields.Count).ToList();
        var more = cuts
            ? $", … (+{owner.Fields.Count - sample.Count} more — the full field list for '{owner.Name}' is in the " +
              "mutagen-reference skill)"
            : "";
        var arms = owner is { Kind: "polymorphic-base", Arms.Count: > 0 }
            ? $" Also searched its arms ({string.Join(", ", owner.Arms!.Where(a => a != owner.Name))})."
            : "";
        return $"No field '{name}' on '{owner.Name}'. Fields: {string.Join(", ", sample)}{more}.{arms}";
    }

    /// <summary>Find <paramref name="name"/> on <paramref name="owner"/>, looking through a polymorphic-base's ARMS
    /// when the base itself lacks it. Generic over every polymorphic-base family — no per-type wiring (cornerstone).
    /// A name found on arms must AGREE in shape across every arm that declares it; arms that disagree reject by name,
    /// never guess. <paramref name="effectiveOwner"/> is the schema the found field belongs to.</summary>
    FieldSchema? FindField(TypeSchema owner, string name, out TypeSchema effectiveOwner, out string? error,
        ElementHop? elementHop = null)
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
                // A listed-but-absent arm is a real corpus defect — surfaced loud, never skipped.
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
                        "the validator cannot pick one statically. " + ElementRemedy(elementHop);
                return null;
            }
        effectiveOwner = firstArm;
        return firstField;
    }

    /// <summary>Two arm declarations of the same field name agree iff every navigation- and validation-relevant facet
    /// matches — identity by what the validator USES. The CLR-type facets compare write-legal EQUIVALENCE, not
    /// raw-string identity, so a type and its <c>Nullable&lt;T&gt;</c> wrapper agree and the raw Nullable flag is not
    /// compared; every genuine difference still rejects.</summary>
    internal static bool SameShape(FieldSchema a, FieldSchema b) =>
        a.Cardinality == b.Cardinality && a.Type == b.Type && a.TypeRef == b.TypeRef
        && a.ElementType == b.ElementType && a.ElementTypeRef == b.ElementTypeRef
        && a.Writable == b.Writable && a.IsIdentity == b.IsIdentity
        && SameWriteLegalType(a.GetterTypeAssemblyQualified, b.GetterTypeAssemblyQualified)
        && SameWriteLegalType(a.MutableTypeAssemblyQualified, b.MutableTypeAssemblyQualified)
        && SameWriteLegalType(a.ElementTypeAssemblyQualified, b.ElementTypeAssemblyQualified);

    /// <summary>Two assembly-qualified CLR-type names are write-legal-equivalent iff they resolve to the same runtime
    /// type after unwrapping <c>Nullable&lt;T&gt;</c>, mirroring <see cref="WriteEngine"/>'s own Coerce/CanCoerce. A
    /// name that will not resolve falls back to raw-string identity, and null matches only null.</summary>
    static bool SameWriteLegalType(string? a, string? b)
    {
        if (a == b) return true;                      // identical strings (incl. both-null) — the common case
        if (a is null || b is null) return false;     // one present, one absent → genuinely different
        var ta = WriteEngine.ResolveType(a);
        var tb = WriteEngine.ResolveType(b);
        if (ta is null || tb is null) return a == b;  // unresolvable → raw-string fallback (false here → stay rejected)
        return (Nullable.GetUnderlyingType(ta) ?? ta) == (Nullable.GetUnderlyingType(tb) ?? tb);
    }
}

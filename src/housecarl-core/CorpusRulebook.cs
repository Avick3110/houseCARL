using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// A write request in the engine's internal representation: a verb applied at the leaf of a path from
/// a record root. Not the wire format — the MCP layer translates into this shape.
/// </summary>
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

/// <summary>
/// A modeled struct built FROM PARTS — the one composition primitive. Used in three places, all the same shape: a
/// <b>polymorphic Set</b> (<see cref="Type"/> = the chosen arm), a <b>struct-element Add</b> to a collection
/// (<see cref="Type"/> = the list's element type), and absent-composition materialization. <see cref="Fields"/> is
/// flat-leaf sugar (coercible scalar/enum/formlink/value sub-fields, one Set-leaf each). <see cref="Sets"/> carries
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
/// used for <b>pre-flight validation</b> before any Mutagen mutation. The schema IS the validator data,
/// by construction: corpus.json and this model come out of the same reflection walk, so they cannot
/// disagree about field names or types. Every rejection names what was checked and what's legal.
/// </summary>
public sealed class CorpusRulebook
{
    readonly Corpus _corpus;
    CorpusRulebook(Corpus corpus) => _corpus = corpus;

    /// <summary>The legal shapes of a condition FormLinkOrIndex target value — shared by the nested-sets
    /// (<see cref="ValidateFromType"/>) and the flat-fields (<see cref="CheckValue"/>) rejects so the two compose entry
    /// points can't drift on what they tell the user is legal.</summary>
    const string FloiTargetForms =
        "a FormID (XXXXXX:Plugin.esp → form mode), a bare index, or 'alias N' / 'packdata N' (→ index mode)";

    public int TypeCount => _corpus.TotalTypes;
    public TypeSchema? Type(string name) => _corpus.Types.GetValueOrDefault(name);

    /// <summary>The ONE source of truth for where corpus.json lives — every corpus read in the core
    /// resolves through it. Defaults to the dev-harness location ("generated/corpus.json", relative to
    /// the CWD, which is the repo root when the harness runs). The MCP server is launched by MO2 from an
    /// arbitrary working directory, so it MUST set this to an absolute path at startup — a hardcoded
    /// relative path can't survive the process/CWD change.</summary>
    public static string CorpusPath { get; set; } = Path.Combine("generated", "corpus.json");

    /// <summary>Load the validator rulebook from the configured <see cref="CorpusPath"/>.</summary>
    public static CorpusRulebook Load() => new(LoadCorpus());

    /// <summary>Load the validator rulebook from an explicit path (the harness; tests).</summary>
    public static CorpusRulebook Load(string corpusJsonPath) => new(LoadCorpus(corpusJsonPath));

    /// <summary>The raw deserialised <see cref="Corpus"/> from the configured <see cref="CorpusPath"/> — for
    /// consumers that want the catalog model directly (the read engine's field-name lookup; the harness'
    /// coerce-audit / census / write-proof) rather than the validator wrapper.</summary>
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
    /// <paramref name="siblingEditorIds"/> (non-null only on the CREATE-batch path) is the set of editorids created
    /// EARLIER in the same call PLUS the record being created itself (a quest's VMAD fragment points at its own
    /// quest): a FormLink value of the form <c>@editorid</c> in that set is accepted as a forward-ref the create path
    /// resolves post-allocation. The set threads into composed StructSpec Fields/Sets too. Null (the override/set_field
    /// path) ⇒ an <c>@editorid</c> value is rejected loud — it has no meaning when there are no same-call
    /// creations.</summary>
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
    /// <see cref="StructSpec"/>'s nested writes validate by the identical leaf/path rules, recursively).
    /// <para><paramref name="pathSlot"/> is what the caller's OWN input slot for <see cref="WriteRequest.Path"/> is
    /// called at this root, and the paths this walk builds are relative to that root: <c>field_path</c> on a record,
    /// <c>path</c> inside a compose's nested <c>sets</c>. A remedy that names a path must spell the slot for its
    /// context or it names a call the caller cannot make.</para></summary>
    string? ValidateFromType(TypeSchema root, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null,
        string pathSlot = "field_path")
    {
        if (req.Path.Length == 0)
            return "Empty path: a write must target at least one field.";

        // (2) walk the path, validating each intermediate hop's existence + descendability. A plain hop descends a
        // substruct; a bracketed hop (Effects[0]) steps INTO a collection element.
        var current = root;
        // …remembering whether any HOP was an owned child record. The leaf is not the whole story once a record can
        // sit mid-path: the hops through it are where a verb acts on a record the call never named.
        FieldSchema? ownedChildHop = null;
        TypeSchema? ownedChildHopOwner = null;
        // …and the bracketed hop that produced the type being walked RIGHT NOW, for the one refusal that cannot name
        // the arm it needs (FindField's conflicting-shapes reject). Cleared on every plain hop, so it only ever
        // describes the element the caller is standing in.
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
                // plain hop — descend a substruct, OR a STANDALONE polymorphic field (NpcConfiguration.Level,
                // Npc.Sound, DialogResponsesAdapter.ScriptFragments). The poly case descends to the polymorphic-BASE
                // catalog entry (field.TypeRef); FindField's over-arms search (below) then resolves the next hop
                // against the base's arms, keyed on cardinality, no per-type wiring. The static validator can't know
                // WHICH live arm sits here, so apply resolves on the element's RUNTIME type and fails loud on a real
                // arm mismatch.
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
                // (.Male/.Female) descend as plain hops; [0]/[1] is the render-matching navigable alias. Corpus-side
                // recogniser = the "GenderedItem<" TypeRef; the engine's twin is the runtime IGenderedItem<> in
                // WriteEngine.StepIntoElement — two recognisers that must agree. Descends to the arm type T so the
                // next hop validates against the arm's own fields (a scalar/value arm has none → loud).
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
                // list mid-path index SHAPE — the SAME recognizer the LEAF key block uses
                // (WriteEngine.IsValidListIndexValue: a parseable NON-NEGATIVE int32), so the mid-path hop and the leaf
                // can't drift. A bare int.TryParse ACCEPTS a negative ('-1' parses), but apply's StepIntoElement list
                // branch requires idx >= 0 and throws a PLAIN InvalidOperationException, which surfaces as the
                // misleading "real inconsistency" wrapper. The in-range bound stays apply's job.
                if (KeyShapeError(field, current.Name, segName, segKey) is { } ke) return ke;
                current = elem;
                elementHop = new ElementHop(PathTo(req.Path, i, segName), segKey, field, pathSlot);
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
            // in ApplyVerb's leaf throw — two recognisers that must agree.
            var bracketed = FindField(current, leafName, out _, out _);
            if (bracketed is { Cardinality: "substruct", TypeRef: { } ltr }
                && ltr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                return $"Gendered field '{leafName}' on '{current.Name}' renders as [0]/[1] but is not a list — set its " +
                       $"halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).";
            // The remedy is SHAPE-scoped: WriteVerbs derives the keyed verb set from the leaf's own shape, so a dict
            // caller is never handed index verbs nor a list caller key verbs. The verbless fallback is still reachable
            // — a bracketed typo ('Nope[0]') resolves no field — so it states the rule without naming verbs.
            var head = $"Path '{req.Path[^1]}' brackets a collection element at the LEAF; brackets navigate mid-path only. ";
            // The remedy carries the caller's OWN key, not just the rule: the bracket they typed already says which
            // element they meant, so the message can hand back the call that works rather than a shape to fill in.
            // Only when the key PASSES the same shape recognisers the mid-path hop uses, though — an sbyte-keyed dict
            // handed back 'Data[notasbyte]' would be naming a call that throws at apply, which is the dead end this
            // refusal exists to close. A key that fails them gets the rule and the verb menu, as before.
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

        // (3a-composes) batch struct-list surface: composes= is a distinct input shape (a LIST of build-from-parts
        // element specs) that short-circuits the singular verb/value pipeline — Add appends each, ReplaceAll clears
        // then appends each. Validated whole, all-or-nothing per element. Gated FIRST so composes on a dict/substruct
        // gets a composes-specific message, not the singular VerbLegality reject.
        if (req.Structs is not null)
            return ComposesLegality(leaf, leafOwner, req, siblingEditorIds);

        // (3a-copyfrom) CopyFrom transplants the WHOLE field from another plugin's version — a distinct input shape
        // (no wire value; the source is from_plugin). Gate writable + not-identity + a transplantable KIND here; the
        // SOURCE resolution (is from_plugin in the order / does it define the record) happens later. The one
        // non-transplantable kind is an owned-child record collection — refused by name (forward the whole record).
        if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
            return CopyFromLegality(leaf, leafOwner, ownedChildHop, ownedChildHopOwner);

        // (3a) verb legal for this cardinality?
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // (3a-owned) …and the verb whose cardinality answer is wrong for an OWNED CHILD RECORD. Remove on a nullable
        // substruct clears a sub-object; on a leaf that holds a record it deletes the record itself and everything
        // under it — a Worldspace's TopCell takes its persistent references with it. The list form of the same family
        // does delete an owned child (Cell.Persistent Remove key=0), but by INDEX, so the caller names which one; a
        // keyless clear of a singular child deletes a whole subtree implicitly, in one call, with no backup on the
        // in-place lane. Deliberate owned-child deletion is an open gap, #350.
        if (string.Equals(req.Verb, "Remove", StringComparison.Ordinal)
            && SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
            return $"'{leaf.Name}' on '{leafOwner.Name}' holds an owned child RECORD ({leaf.TypeRef}): clearing the " +
                   "field deletes that record and every record under it, implicitly and in one call. The list form of " +
                   "this family deletes a child too, but by INDEX — you name which one; there is no such target here. " +
                   "Deliberate deletion of an owned child record is an open gap (#350), not something clearing a " +
                   $"field decides. (To see which record this is: read the parent at depth=2 — the '{leaf.Name}' " +
                   "field shows the child's FormID.)";

        // (3b) record identity (FormKey/ModKey) is a flat, honest reject regardless of Mutagen's setter.
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{leafOwner.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // (3c) writable? (discriminators route to their own rejection)
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

    /// <summary>True iff the leaf is a <c>[Flags]</c>-attributed enum — the ONLY scalar/enum kind the bit verbs
    /// <c>Add</c>/<c>Remove</c> operate on (a single-value enum like CastType has no bits to OR/clear, so it stays
    /// refused). Resolved from the field's OWN assembly-qualified type — never the simple-name catalog, which
    /// collides on shared enum names ("Flags", "MajorFlags", …) — checking <see cref="FlagsAttribute"/>. False when
    /// the AQ won't resolve — never ASSUME flags-ness, so a bit verb is refused rather than accepted-then-thrown.
    /// The SAME resolution the apply path keys on (WriteEngine.ApplyScalarVerb's [Flags] gate), so gate and apply
    /// can't drift on which leaves accept a bit verb.</summary>
    static bool IsFlagsEnumLeaf(FieldSchema leaf)
    {
        if (leaf.Cardinality != "enum") return false;
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return false;
        var u = Nullable.GetUnderlyingType(rt) ?? rt;
        return u.IsEnum && u.IsDefined(typeof(FlagsAttribute), false);
    }

    // ---- verb × cardinality ----
    // Instance, not static, since the Set-on-list remedy derives its alternatives from the leaf's SHAPE — which
    // needs the corpus to classify the element. This switch decides; WriteVerbs describes.
    string? VerbLegality(FieldSchema leaf, WriteRequest req)
    {
        var c = leaf.Cardinality;
        var hasKey = req.Key is not null;
        switch (req.Verb)
        {
            case "Set":
                if (c == "dict") return hasKey ? null : $"Set on dict field '{leaf.Name}' requires a key.";
                // The alternatives are DERIVED from the leaf's shape, not recited, so they cannot fall behind the
                // list verb set.
                if (c == "list") return $"Set is not valid on list '{leaf.Name}' — {PlacingRemedy(leaf)}.";
                return hasKey ? $"Set on {c} field '{leaf.Name}' does not take a key." : null;
            case "Add":
                // A dict Add coerces req.Key into the new entry's key (ApplyDictVerb -> Coerce(req.Key!, kType)); a
                // MISSING key reaches apply and throws UNNAMED (Coerce(null)). A list Add appends — no key. Gate dict-Add
                // key PRESENCE here, the structural twin of Set-on-dict above (key VALUE-shape is ValueLegality's job).
                if (c == "dict") return hasKey ? null : $"Add on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // A [Flags] enum accepts Add as a bit-SET (OR the flag in, other bits preserved), so it does not
                // clobber the other bits the way a whole-value Set would. A bit verb takes no key (it is not a
                // collection); the flag VALUE is gated in ValueLegality. Non-flags scalars/enums still refuse below.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Add on flags field '{leaf.Name}' takes no key — the value IS the flag to set." : null;
                return $"Add is only valid on a list/dict or a [Flags] enum; '{leaf.Name}' is {c}.";
            case "Remove":
                // A dict Remove identifies the entry to drop BY KEY (ApplyDictVerb -> Coerce(req.Key!, kType)); a MISSING
                // key throws UNNAMED at apply. A list Remove is by-index-OR-by-value (ApplyListVerb): a null key legally
                // falls back to remove-by-value, so list Remove needs NO key — gate dict-Remove key PRESENCE only.
                if (c == "dict") return hasKey ? null : $"Remove on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // A [Flags] enum accepts Remove as a bit-CLEAR (AND-NOT the flag out, other bits preserved) — the
                // Remove twin of the flags Add above. Distinct from the nullable-scalar whole-clear below: it clears
                // ONE bit, not the whole field. No key; the flag VALUE is gated in ValueLegality.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Remove on flags field '{leaf.Name}' takes no key — the value IS the flag to clear." : null;
                return leaf.Nullable ? null : $"Remove on non-nullable {c} field '{leaf.Name}' is not valid.";
            case "ReplaceAll":
                return c is "list" or "dict" ? null : $"ReplaceAll is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "SetAtIndex":
                // A list SetAtIndex parses req.Key as the index (ApplyListVerb -> int.Parse(req.Key!)); a MISSING index
                // throws ArgumentNullException at apply. Require it up front (PRESENCE; the parseable-as-int / non-negative
                // VALUE-shape is gated in ValueLegality's key block).
                if (c != "list") return $"SetAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"SetAtIndex on list '{leaf.Name}' requires an index.";
            case "InsertAtIndex":
                // SetAtIndex's structural twin: a list InsertAtIndex parses req.Key as the position to insert AT
                // (ApplyListVerb -> int.Parse(req.Key!)), so a MISSING index throws ArgumentNullException at apply.
                // Same PRESENCE gate here; the parseable / non-negative VALUE-shape is ValueLegality's key block, and
                // the in-RANGE bound is apply's (no live list at the gate) — insert's bound differs there (it admits
                // index == count, the append slot).
                if (c != "list") return $"InsertAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"InsertAtIndex on list '{leaf.Name}' requires an index (the position to insert AT; the list's length appends).";
            case "Merge":
                return c == "dict" ? null : $"Merge is only valid on dict; '{leaf.Name}' is {c}.";
            default:
                // The verb set comes from its one home (WriteVerbs.All), never a hand-typed copy.
                return $"Unknown verb '{req.Verb}'. Legal: {string.Join(", ", WriteVerbs.All)}.";
        }
    }

    /// <summary>"How do I put an element into this collection", derived from the leaf's own shape — the one answer
    /// every collection remedy in this file asks for.
    /// <para/>
    /// Every call site must stay cardinality-gated to a list or a dict: the null arm is reached only when the leaf IS
    /// a collection whose ELEMENT KIND <see cref="WriteVerbs"/> declines to describe, which no field in the corpus is
    /// today. It says nothing rather than guessing, because a declined kind has no settled legal-verb answer.</summary>
    string PlacingRemedy(FieldSchema leaf) =>
        WriteVerbs.OfField(leaf, _corpus) is { } shape
            ? WriteVerbs.HowToPlace(shape)
            : "the verbs this field takes are in the tool description";

    /// <summary>Validate a composes= batch (a LIST of build-from-parts element specs) whole: Add appends each,
    /// ReplaceAll clears then appends each. LIST-of-modeled-elements ONLY (a dict needs keyed entries; a substruct/
    /// scalar takes compose=/value=). Each element is validated by the SAME <see cref="StructElementLegality"/> the
    /// singular compose Add uses (poly-base arm resolution + recursive contents), so composes can never admit a shape
    /// the singular path rejects. All-or-nothing: the first bad element names itself (composes[i]) and refuses the
    /// whole op.</summary>
    string? ComposesLegality(FieldSchema leaf, TypeSchema owner, WriteRequest req,
        IReadOnlyCollection<string>? siblingEditorIds)
    {
        // SHAPE BEFORE VERB. The verb arm must run LAST: its sentence ("use composes= with Add or ReplaceAll") is
        // only true for a caller whose field is a list of modeled elements, so a dict, substruct or coercible-element
        // list caller reaching it would be pointed at a verb that refuses on the next call.
        //
        // The owned-child answer stays FIRST of all, because the cardinality sentence below ends by pointing at
        // compose= / value= — and on that shape both of those refuse too.
        if (SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
            return OwnedChildSetRefusal(leaf);
        // …and the COLLECTION twin of that shape, which the singular predicate above does not match. Asked before the
        // not-composable label below, which reads the element kind off FormLinkTarget and would call Cell.Persistent's
        // owned child records "coercible values". Answered from the same classification the collection verbs decide
        // with, and NOT verb-scoped: a record is not built from parts under any verb.
        //
        // The two shapes carry different remedies, so they cannot share one sentence: create with parent= is refused
        // for a singular child (which routes to the open gap #350) and is exactly the shape the collection serves.
        if (IsOwnedChildRecordCollection(leaf))
            return OwnedChildRecordCollectionRefusal(leaf);
        if (leaf.Cardinality != "list")
            return $"composes= builds a LIST of modeled elements, but '{leaf.Name}' on '{owner.Name}' is a " +
                   $"{leaf.Cardinality}. (A dict takes keyed entries, not a positional list; a substruct/scalar takes " +
                   "compose= / value=.)";
        if (!IsComposableElement(leaf))
            // The slot guidance is DERIVED, not written for one verb: any verb reaches this sentence (the verb check
            // sits below), so it names every verb this shape takes with the slot each one wants.
            return $"'{leaf.Name}' on '{owner.Name}' holds " +
                   (leaf.FormLinkTarget is not null ? "formlink" : "coercible") +
                   $" values ({leaf.ElementTypeRef ?? leaf.ElementType}), not modeled structs, so composes= has " +
                   $"nothing to build — {PlacingRemedy(leaf)}.";
        if (req.Verb is not ("Add" or "ReplaceAll"))
            // Reached only on a LIST of modeled elements. The alternatives are derived as the SINGULAR ones:
            // HowToPlace would also offer ReplaceAll, the batch verb the head sentence just recommended, and a
            // remedy labelled "one element at a time" must not end by naming the batch form again.
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

    /// <summary>Validate a CopyFrom target leaf: writable, not record identity, and a TRANSPLANTABLE kind. The
    /// non-transplantable kind is an owned-child record, in EITHER shape — a collection of them (Cell.Persistent,
    /// DialogTopic.Responses, …) or the singular one (Cell.Landscape, Worldspace.TopCell). CopyFrom copies a FIELD's
    /// value, not owned child records; refuse by name. Ungated, a CopyFrom on <c>Worldspace.TopCell</c> deep-copies the
    /// source's whole CELL — its FormKey, its persistent references and all — into the destination's worldspace, the
    /// silent child-record import <see cref="WriteEngine.RestoreChildGroup"/> refuses by name on the forward path.
    /// Everything else — scalar/enum/value, formlink, formlink/modeled list, sub-struct, polymorphic arm —
    /// WriteEngine.CopyField transplants by construction.
    /// <para/>
    /// The singular and collection arms word their remedies differently because only one remedy works per shape:
    /// <c>housecarl_forward</c> carries the child record itself across for both, but <c>create</c> with
    /// <c>parent=</c> is refused for a singular child, so only the collection arm may offer it; the singular case
    /// routes to the lifecycle gap #350 instead.</summary>
    string? CopyFromLegality(FieldSchema leaf, TypeSchema owner, FieldSchema? ownedChildHop = null, TypeSchema? hopOwner = null)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not a copyable content field.";
        if (!leaf.Writable) return WritabilityRejection(owner, leaf);
        // TRANSPLANT REFUSES AT ANY DEPTH. A leaf-only test passes a path that merely runs THROUGH an owned child —
        // apply then walks the source's child record and the destination's, and writes one record's field into the
        // other: two child records, neither named by the caller, reported as an edit to the parent. That is the same
        // act the leaf clause forbids, one hop further down.
        //
        // The in-place verbs through the same hop (Set / Remove / Add / SetAtIndex / InsertAtIndex at a leaf UNDER
        // the child) stay ACCEPTED: those edit the child this record already carries, in place, and are the only way
        // to edit a carried child at all. What CopyFrom adds is a SECOND record, from another plugin, as the source
        // of the value — the transplant this refuses, wherever in the path the child sits.
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
                   $"depth=2 and the '{leaf.Name}' field shows the child's FormID. Giving a parent a child it does " +
                   "not have is an open gap (#350).";
        // The same recogniser as the other two collection doors; the SENTENCE stays this door's own, because
        // CopyFrom's remedy is housecarl_forward (carrying across a record that already exists) rather than
        // housecarl_create with parent= alone. Shared predicate, per-door remedy.
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
    /// and <c>composes=</c> alike. Shared rather than phrased per door, so the three doors cannot point at each other's
    /// refused remedies.
    /// <para/>
    /// The remedy it names works: addressing the child record by its own FormID writes and reads back on the default
    /// lane. The descent clause is conditional on purpose — a path through the parent reaches a child only when the
    /// copy being written already carries one, and a patch's override of a parent never does (Mutagen's override copy
    /// leaves the children behind), so an unconditional "descend into it" would send a caller at a state the default
    /// lane cannot produce.</summary>
    static string OwnedChildSetRefusal(FieldSchema leaf) =>
        $"'{leaf.Name}' holds an owned child RECORD ({leaf.TypeRef}): a record is not a part of its parent, so it is " +
        $"neither built from parts (compose= / composes=) nor set from a value (value=). {AddressChildByFormId(leaf.Name)} A path through " +
        "the parent reaches a child only when the record being written already carries one, which a patch's fresh " +
        "override of a parent never does; giving a parent a child it lacks is an open gap (#350).";

    /// <summary>How an owned child record that ALREADY EXISTS is written: on the record axis, by its own FormID.
    /// One sentence, shared by the value-shaped Set refusal and the element remedy, so the two doors cannot drift
    /// on where the caller is being sent.</summary>
    static string AddressChildByFormId(string fieldName) =>
        $"Address the child record itself by its own FormID — read the parent at depth=2 and the '{fieldName}' field shows it.";

    /// <summary>True iff a leaf is the COLLECTION form of the owned-child shape — a list/dict whose ELEMENT is an
    /// owned child record (<c>Cell.Persistent</c>, <c>DialogTopic.Responses</c>, the typed record groups). The
    /// collection twin of <see cref="SchemaClassifier.IsOwnedChildRecord"/>, which matches only the SINGULAR shape.
    /// <para/>
    /// It is one named predicate because three separate doors ask the question — the collection verbs,
    /// <c>composes=</c> (<see cref="ComposesLegality"/>) and <c>CopyFrom</c> — and a door that has to remember to
    /// run the test can forget to, falling through to a label that reads the element kind off
    /// <c>FormLinkTarget</c> and calls an owned child record "coercible".</summary>
    bool IsOwnedChildRecordCollection(FieldSchema leaf) =>
        leaf.Cardinality is "list" or "dict" && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record;

    /// <summary>The ONE sentence every element-PLACING door at an owned-child-record COLLECTION gets — a plain-value
    /// or composed Add / SetAtIndex / InsertAtIndex / ReplaceAll, and <c>composes=</c> alike. Shared for the reason
    /// <see cref="OwnedChildSetRefusal"/> is: the doors reach one shape by different routes, and a per-door phrasing
    /// is where one of them starts describing the field differently from the others.
    /// <para/>
    /// The remedy it names is the one that works for THIS shape (and the one the singular twin must not name):
    /// create with <c>parent=</c> the parent's FormID. <c>collection=</c> is REQUIRED when the parent holds more than
    /// one fitting list, so the parenthetical names it rather than leaving the caller to a second refusal.</summary>
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
        // Same-call sibling reference ("@editorid", a create-context forward-ref). Gate it BEFORE any verb/cardinality
        // dispatch: it names a record created EARLIER in this same create call — or the record being created ITSELF —
        // substituted with the real FormKey AFTER allocation (WritePatchBuilder.CreateRecords), and ONLY in create
        // context (siblingEditorIds non-null). The Apply/set_field path has no siblings, so an @editorid there rejects
        // loud rather than substituting nothing. Legal placements, all resolved with identical timing:
        //   • a SINGULAR value — Set on a singular FormLink leaf, OR Add on a FormLink LIST leaf (the substitution
        //     replaces the singular req.Value either way — ApplyListVerb's Add coerces it).
        //   • inside a ReplaceAll's req.Values on a FormLink LIST — each @-entry substituted in place.
        // Anywhere else a sibling token would slip past pre-flight and throw FormKey.Factory at apply, so those cases
        // stay refused loud below. Both gates sit ahead of the cardinality branches.
        if (WriteEngine.IsSameCallSiblingRef(req.Value, out var sibEdid))
        {
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
            // A stray compose spec riding alongside an admitted '@' value would skip validation (this gate RETURNS
            // before the compose branches) yet be WALKED by apply's substitution recursion — a bad token inside it
            // would then fail under the "internal: pre-flight should have caught it" wrapper, blaming the engine for
            // input the gate never saw. Refuse it loud instead.
            if (req.Struct is not null)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "value IS the whole FormLink target; remove struct=.";
            return siblingEditorIds.Contains(sibEdid) ? null
                : $"Same-call reference '{req.Value}' for '{leaf.Name}': no record with editorid '{sibEdid}' is created " +
                  "EARLIER in this call (a record may also reference ITSELF by its own editorid) — declare it before " +
                  "the record that references it (in spec order).";
        }
        // A sibling token inside req.Values — legal ONLY as a ReplaceAll on a FormLink LIST; each entry is substituted
        // with its sibling's allocated FormKey (WritePatchBuilder.CreateRecords). Validate the WHOLE list here
        // (siblings + literal FormIDs may mix) and RETURN — do NOT fall through to the FormLink value check, which
        // would reject the '@' tokens as malformed FormLinks. Any other placement (wrong verb, a non-FormLink list)
        // stays refused loud rather than slipping to a FormKey.Factory throw at apply.
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
                    if (!siblingEditorIds.Contains(vEd))
                        return $"Same-call reference '@{vEd}' for '{leaf.Name}': no record with editorid '{vEd}' is " +
                               "created EARLIER in this call (a record may also reference ITSELF by its own editorid) — " +
                               "declare it before the record that references it (in spec order).";
                }
                else if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            }
            return null;
        }
        // A sibling token inside a dict Entries' VALUES — no formlink-VALUED dict is modeled, so this stays refused
        // loud. A dict KEY '@…' is caught by the key-shape gate below, which won't coerce '@…' to any modeled key type.
        if (req.Entries is { } ents && ents.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported on a FormLink list, not " +
                   "inside a dict value — no formlink-valued dict is modeled.";
        // KEY / INDEX VALUE-SHAPE — the shape twin of the key/index PRESENCE gate (VerbLegality's
        // missing-key rejects). A PRESENT-but-malformed dict key / list index passes presence but throws UNNAMED at
        // apply: a dict Set/Add/Remove coerces req.Key into the entry (ApplyDictVerb -> Coerce(req.Key!, KeyType)) and
        // Merge/ReplaceAll coerce each Entries key the same way; a list SetAtIndex/InsertAtIndex/Remove parses req.Key as the index
        // (ApplyListVerb -> int.Parse(req.Key!)). Gate both LOUD here, by construction, with the SAME recognizers the
        // apply path uses so gate and apply can't drift: the dict key's real CLR type is resolved from the field's own
        // dictionary AQ (DictKeyType — the identical type apply keys on, dictIface.GetGenericArguments()[0]), so
        // coercibility is checked for EVERY key kind (enum AND the one sbyte-keyed dict), not just enums by catalog-
        // name; the list index via WriteEngine.IsValidListIndexValue (parseable non-negative int32). An enum-name-only
        // check both lets a non-enum key through and over-rejects a numeric enum key like '3', which apply accepts.
        // PRESENCE stays VerbLegality's job; this is purely SHAPE.
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
            // Key shape gated by the key block above (Set/Add/Remove share one recognizer). A struct/arm-VALUED dict
            // (Package.Data — the only one Mutagen models) Set REPLACES an entry's value with a build-from-parts
            // element: validate the spec against the element type via the SAME StructElementLegality the Add path uses
            // (poly-base arm resolution + recursive contents), so gate and apply can't drift. A coercible-VALUE dict
            // (Class.SkillWeights, Race.Regen, …) Set coerces.
            if (IsComposableElement(leaf)) return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            if (req.Value is null) return $"Set on dict '{leaf.Name}' requires a value.";
            return CheckValue(leaf.ElementType, req.Value, $"dict value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
        }
        if (req.Verb is "Set" && leaf.Cardinality == "polymorphic")
            return ArmLegality(leaf, req.Struct, siblingEditorIds);
        // A whole modeled-STRUCT substruct leaf (FaceParts, ObjectBounds, FaceMorph, a concrete script-property arm, …) is
        // Set by composing its value FROM PARTS — the leaf twin of the dict-element (above) and polymorphic-arm (just
        // above) compose paths, validated by the SAME StructSpecContents. Apply builds it (ApplyScalarVerb req.Struct ->
        // BuildStruct), so an absent struct can be filled in ONE op. SchemaClassifier scopes it so a coercible substruct
        // (TranslatedString) keeps its plain-value Set below, and GenderedItem (diverted to [0]/[1] upstream) and
        // Array2d (no parameterless ctor) stay out — gate and apply agree, no accept-then-throw.
        if (req.Verb is "Set" && SchemaClassifier.IsComposableSubstructLeaf(leaf, _corpus))
            return StructLeafLegality(leaf, req.Struct, siblingEditorIds);
        if (req.Verb is "Set")
        {
            // A compose spec reaching HERE means the leaf isn't a compose target (not a composable substruct/dict/poly —
            // those branch above): a coercible substruct (a TranslatedString — set as one value, not built from parts), a
            // formlink, a plain scalar, or an OWNED CHILD RECORD, which is none of those. For the first
            // three, name the plain-value path instead of the misleading "requires a value" (which reads as "value= is
            // absent"). For a record that advice is a dead end: the plain-value Set below refuses it too, so a caller
            // following the sentence lands back here. Say what is true of a record instead. compose is for a
            // build-from-parts struct/dict/polymorphic field only.
            if (SchemaClassifier.IsOwnedChildRecord(leaf, _corpus))
                return OwnedChildSetRefusal(leaf);
            if (req.Value is null)
                return req.Struct is not null
                    ? $"'{leaf.Name}' is set from a plain value (value=…), not a compose spec."
                    : $"Set on '{leaf.Name}' requires a value.";
            // formlink / substruct-whole: the engine must be able to coerce the leaf's whole type. A normal formlink
            // coerces; a condition FormLinkOrIndex is handled by the parent-aware SetFloi branch — validate its
            // target-value SHAPE here; a non-string substruct still rejects honestly, so pre-flight never
            // accepts-then-throws. FLOI is recognised via the engine's shared IsFormLinkOrIndex (no drift).
            if (leaf.Cardinality is "formlink" or "substruct")
            {
                var faq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
                if (WriteEngine.ResolveType(faq) is { } frt && WriteEngine.IsFormLinkOrIndex(frt))
                    return WriteEngine.TryClassifyFloiValue(req.Value) ? null
                        : $"Illegal condition target '{req.Value}' for '{leaf.Name}': expected {FloiTargetForms}.";
                // A NORMAL FormLink Set — validate the FormKey VALUE shape at the gate (the FORMLINK arm ONLY; a
                // substruct still falls to the type-shape CoercibilityReject below). CoercibilityReject is type-only
                // and never inspects the string, so "00000000"/"0" would be accepted then throw at FormKey.Factory on
                // apply. A null-synonym clears the link; otherwise it must parse as a FormKey. The recognizer is
                // SHARED with the engine apply path (no drift).
                if (leaf.Cardinality == "formlink")
                    return WriteEngine.IsValidFormLinkValue(req.Value) ? null
                        : $"Illegal FormLink target '{req.Value}' for '{leaf.Name}': expected a FormID " +
                          "(XXXXXX:Plugin.esp) or a null-clear ('0', '00000000', 'Null', '000000:Null').";
                return CoercibilityReject(leaf);
            }
            return CheckValue(leaf.Type, req.Value, $"value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // Add/Remove on a [Flags] enum are bit-SET / bit-CLEAR: the value is the flag(s) to OR in or AND-NOT out.
        // VerbLegality already admitted the verb for a [Flags] leaf; validate the flag NAME/bits here with the SAME
        // CheckValue recognizer a Set uses (the field's real enum AQ), so a bogus flag fails LOUD at the gate instead
        // of throwing Enum.Parse at apply. Gated ahead of the collection branches (list/dict-scoped, so they would
        // ignore an enum leaf) to keep it from falling through to the terminal `return null` accept.
        if (req.Verb is "Add" or "Remove" && IsFlagsEnumLeaf(leaf))
        {
            if (req.Value is null)
            {
                // Add always needs the bit to set. A VALUELESS Remove keeps its other meaning — the WHOLE-CLEAR of a
                // nullable scalar, the ONLY path to make a nullable flags field ABSENT/null — so it is allowed iff
                // the field is nullable, else refused with the turn-all-off redirect (Set '0'), never a dead end.
                if (req.Verb == "Add")
                    return $"Add on flags field '{leaf.Name}' requires a flag value (the bit to set).";
                return leaf.Nullable ? null
                    : $"Remove on flags field '{leaf.Name}' needs the flag to clear (value=<flag>) — a non-nullable flags " +
                      "field can't be whole-cleared; to turn ALL bits off, Set it to '0'.";
            }
            return CheckValue(leaf.Type, req.Value, $"flag value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // ELEMENT-VALUE PRESENCE — the collection twin of the singular Set "requires a value" reject above. Add /
        // SetAtIndex / InsertAtIndex on a COERCIBLE-element collection set the new element by coercing the singular
        // req.Value (ApplyListVerb / ApplyDictVerb -> Coerce(req.Value!, elem)); at apply Coerce(null) yields a null
        // element that throws a NullReferenceException at SERIALIZE, surfaced as a misleading NullArmSerializeException.
        // The formlink check below uses `is { } ev`, which SKIPS a null slot, so gate presence here for EVERY coercible
        // element. NO req.Struct guard: a coercible element is never built from a StructSpec (BuildStruct throws on
        // one), so a struct can never rescue a null value. Verb-scoped to the verbs consuming the singular req.Value —
        // ReplaceAll (req.Values) / Merge (req.Entries) carry their elements elsewhere, and Remove is by-key-OR-value.
        // Coercible-element-only: Struct/Arm elements compose via the composable block below, and Record / uncoercible
        // elements have no plain-value Add path.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex"
            && req.Value is null && IsValueCoercibleElement(leaf))
            return $"{req.Verb} on '{leaf.Name}' requires an element value.";
        // FormLink-ELEMENT collection value-shape — the collection twin of the singular formlink Set check immediately
        // above. A list/dict whose ELEMENT is a FormLink (corpus FormLinkTarget set — emitted by the SAME generator
        // IsFormLink branch that flags a singular formlink) coerces each element through FormKey.Factory at apply, so a
        // malformed element ("notaformkey"; "0"/"00000000"/"Null"/"000000:Null" stay legal null-clears) would otherwise
        // throw "Malformed FormKey string" there. Validate every supplied element VALUE with the SAME recognizer the
        // singular path uses (IsValidFormLinkValue — one predicate, no drift): req.Value (list Add / SetAtIndex /
        // InsertAtIndex / Remove-by-value; dict Add — dict Set returned at its own block above), req.Values (list
        // ReplaceAll), and req.Entries' VALUES (dict Merge / ReplaceAll). Element VALUES only — dict key shape is the
        // key block's job, and key/index PRESENCE is VerbLegality's. Every formlink collection in the corpus is a LIST;
        // Mutagen models no formlink-VALUED dict and the generator's dict branch stamps no FormLinkTarget for one, so
        // the req.Entries arm is dormant until such a field exists.
        if (leaf.Cardinality is "list" or "dict" && leaf.FormLinkTarget is not null)
        {
            if (req.Value is { } ev && !WriteEngine.IsValidFormLinkValue(ev)) return FormLinkElementReject(ev, leaf);
            foreach (var v in req.Values ?? Array.Empty<string>())
                if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            foreach (var kv in req.Entries ?? new())
                if (!WriteEngine.IsValidFormLinkValue(kv.Value)) return FormLinkElementReject(kv.Value, leaf);
        }
        // NON-FORMLINK coercible-element collection value-SHAPE — the value twin of the formlink block above and of the
        // dict-Set value block (which gates dict Set's value but not the other collection verbs). A list Add/SetAtIndex/
        // InsertAtIndex/ReplaceAll/Remove-by-value and a dict Add/Merge/ReplaceAll coerce each supplied element value at
        // apply, so a malformed value ("notafloat" into a List<Single>) would throw UNNAMED (float.Parse) there. Scoped
        // to IsValueCoercibleElement(leaf) && FormLinkTarget is null so formlink elements keep their own per-element
        // message and the two blocks cover EVERY coercible element with no double-check and no gap. Uses the SAME
        // CheckValue recognizer (with the element AQ) as the dict-Set value block. Faithful to which slot apply actually
        // coerces: the singular req.Value for list Add/SetAtIndex/InsertAtIndex and dict Add, and for a list
        // Remove-by-VALUE (Key null) only — a list Remove BY INDEX and a dict Remove coerce only the key, so their value
        // must NOT be over-rejected. req.Values is the list ReplaceAll contents; req.Entries' VALUES are dict Merge/
        // ReplaceAll. Null PRESENCE on Add/SetAtIndex/InsertAtIndex is the presence gate's; a null inside Values/Entries
        // yields CheckValue's "Missing element value …", which those verbs have no presence mirror for.
        if (leaf.Cardinality is "list" or "dict" && IsValueCoercibleElement(leaf) && leaf.FormLinkTarget is null)
        {
            string? ElemShape(string? v) =>
                CheckValue(leaf.ElementType, v, $"element value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
            if (req.Value is { } ev
                && (req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex" || (req.Verb is "Remove" && req.Key is null))
                && ElemShape(ev) is { } evErr)
                return evErr;
            // Slot-faithful to apply: a LIST ReplaceAll coerces req.Values (ApplyListVerb); a DICT Merge/ReplaceAll
            // coerces req.Entries' values (ApplyDictVerb). Scope each loop to its slot's cardinality so a stray
            // off-cardinality slot apply IGNORES (e.g. req.Entries supplied on a list ReplaceAll) is not over-rejected.
            if (leaf.Cardinality == "list" && req.Verb is "ReplaceAll")
                foreach (var v in req.Values ?? Array.Empty<string>())
                    if (ElemShape(v) is { } valsErr) return valsErr;
            if (leaf.Cardinality == "dict" && req.Verb is "Merge" or "ReplaceAll")
                foreach (var kv in req.Entries ?? new())
                    if (ElemShape(kv.Value) is { } entErr) return entErr;
        }
        // RECORD-ELEMENT collection verb — a list/dict whose ELEMENT is an owned child RECORD (DialogTopic.Responses ->
        // DialogResponses; Cell.Persistent/Temporary -> the all-record Placed arms; the typed record groups). A record
        // element is neither composable (Record is excluded from Struct/Arm) nor value-coercible nor formlink, so
        // without this an Add/SetAtIndex/InsertAtIndex/ReplaceAll falls through to `return null` and throws at apply:
        // with a compose, BuildStruct -> Instantiate -> CompositionRequiredException (the record class has no public
        // parameterless ctor); with a plain value, "No coercion rule". A child record is allocated on the record axis,
        // never built into a parent's collection by the verb engine. Verb-scoped to the create-oriented verbs; a record
        // Remove BY INDEX (RemoveAt) is throw-free and stays accepted, and a record Remove BY VALUE is caught by the
        // Remove-by-value block below. Sentence and predicate are shared with the composes= door, which short-circuits
        // above and would otherwise answer the same shape with its own label.
        if (IsOwnedChildRecordCollection(leaf) && req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex" or "ReplaceAll")
            return OwnedChildRecordCollectionRefusal(leaf);
        // Collection-verb value legality. A struct-element OR arm-element list takes a build-from-parts StructSpec on
        // Add, NOT a plain value: an ARM element composes by its concrete arm type and is validated against that arm's
        // own schema (the VMAD shape). A coercible-element list takes a plain value the engine coerces.
        if (leaf.Cardinality is "list" or "dict" && IsComposableElement(leaf))
        {
            // Add (append), SetAtIndex (overwrite at an index) and InsertAtIndex (insert at a position) all build the
            // element FROM PARTS against the element type — LIST and DICT alike, since ApplyDictVerb builds the entry
            // value via the same BuildStruct(req.Struct) ApplyListVerb's Add uses. All three go through the SAME
            // StructElementLegality (poly-base arm resolution — Package.Data -> an APackageData arm — plus recursive
            // contents; a null spec → "supply a compose spec, not a plain value"), so a composed element is identical
            // whichever slot it lands in. Index PRESENCE (VerbLegality) and SHAPE (the key block above) are already
            // gated by the time control reaches here, and the in-RANGE bound is apply's (no live list at the gate),
            // which is also where insert's admission of the append slot is enforced. The composable fence
            // (IsComposableElement = Struct/Arm ONLY) keeps this off owned-record elements, redirected above.
            // (A composable dict SET is gated at the dict-Set block above — same StructElementLegality.)
            if (req.Verb is "Add" or "SetAtIndex" or "InsertAtIndex")
                return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            // ReplaceAll/Merge of modeled elements stay deferred: ReplaceAll would need a LIST of compose specs and
            // Merge a dict of them (req.Values / req.Entries are plain-string shapes), distinct input surfaces not
            // opened here. Merge must stay listed — a dict-only verb, it would otherwise fall through to ACCEPT and
            // throw 'No coercion rule' at apply.
            if (req.Verb is "ReplaceAll" or "Merge")
                // This message is emitted for a list OR a dict, so its alternatives come from the leaf's own shape —
                // the dict caller gets Set/Add with a key, the list caller the index verbs — rather than one recited
                // set that would hand a dict caller list verbs. It says what is MISSING (a build-from-parts input on
                // this call) rather than what the caller sent: a bare ReplaceAll with no input at all also lands here.
                return $"'{leaf.Name}' holds modeled elements ({leaf.ElementTypeRef}); {req.Verb} has no " +
                       $"build-from-parts input on this call — {PlacingRemedy(leaf)}.";
        }
        // Remove-BY-VALUE on a NON-PLAIN-VALUE element — a list Remove with NO key is by-value (ApplyListVerb ->
        // Coerce(req.Value!, elem)); an element that is neither coercible nor formlink has NO plain-value form, so the
        // coerce throws 'No coercion rule' at apply (or an NRE if the value is also null). One predicate covers
        // composable (struct/arm), record, and the dormant uncoercible case. A Remove BY INDEX (Key present ->
        // RemoveAt, no coercion) stays accepted, and a dict Remove (by key only, key-gated) is excluded.
        if (req.Verb == "Remove" && leaf.Cardinality == "list" && req.Key is null
            && leaf.FormLinkTarget is null && !IsValueCoercibleElement(leaf))
            return $"'{leaf.Name}' holds modeled/record elements ({leaf.ElementTypeRef ?? leaf.ElementType}); remove one " +
                   "BY INDEX (Remove with a Key = its position), not by value — a modeled or record element has no " +
                   "plain-value form to match. (Value-based removal of such an element is a later surface.)";
        return null;
    }

    /// <summary>True iff the leaf is a collection whose ELEMENT is built FROM PARTS on Add (so Add takes a
    /// StructSpec): a modeled-struct element, or a polymorphic-union (arm) element composed by its concrete arm
    /// type. Record-elements are resolved on their own axis, and a WHOLE-COERCIBLE element
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
    /// type — or, when the element type is a <b>polymorphic-base</b>, be one of its ARMS (the VMAD shape:
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

        // A polymorphic BASE is composed by choosing a concrete ARM, never the base itself. Naming the base
        // ({Type:"APackageData"} on the Package.Data dict, {Type:"Condition"} on a *.Conditions list) must be rejected:
        // the spec.Type==er short-circuit below would validate it against the base's OWN fields and ACCEPT, then apply
        // either silently writes a degenerate base instance (a CONCRETE base like APackageData — Instantiate finds its
        // public parameterless ctor) or throws at Invoke ("cannot create an abstract class", an ABSTRACT base like
        // Condition). The recognizer is the corpus poly-base KIND, NOT Type.IsAbstract: APackageData is concrete, so an
        // IsAbstract check would miss the silent-write case. A concrete poly-base also lists ITSELF among its arms
        // (FindUnionArms keeps a non-abstract base), so it is filtered out of the legal-arms set everywhere — the
        // arm-match check AND every message — and neither path can admit or advertise it.
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
    /// the three composition entry points can't disagree. A substruct leaf reaching HERE has a TypeRef that is a CONCRETE
    /// struct/arm (a polymorphic FIELD is cardinality "polymorphic", handled above; an owned child RECORD — a substruct
    /// TypeRef of Kind "record" — is excluded by the same <see cref="SchemaClassifier.IsComposableSubstructLeaf"/>
    /// guard, and refused upstream in its own words), so a straight name-match is correct — no poly-base arm resolution.
    /// Reached only for a <see cref="SchemaClassifier.IsComposableSubstructLeaf"/> leaf (TypeRef non-null,
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
        // Positional ctor_args value-SHAPE + ARITY. A malformed arg or a wrong arity would otherwise throw at apply
        // (Instantiate: Coerce(arg, paramType) / "no constructor taking N arg(s)"). WriteEngine.TryRecognizeCtorArgs
        // mirrors Instantiate EXACTLY (same ResolveStructType + ctor selector + TryCoerce), so gate and apply can't
        // drift. Checked at the TOP so it runs for BOTH call sites (ArmLegality + StructElementLegality) and reports
        // before the per-field checks. Skipped when CtorArgs is null (the parameterless/fields-only compose path).
        if (spec.CtorArgs is { } ctorArgs && WriteEngine.TryRecognizeCtorArgs(spec.Type, ctorArgs) is { } ctorErr)
            return ctorErr;
        foreach (var f in spec.Fields ?? new())
        {
            var af = structSchema.Fields.FirstOrDefault(x => x.Name == f.Key);
            if (af is null) return FieldNotFound(structSchema, f.Key);
            // A '@editorid' same-call reference in a compose FIELD (the VMAD alias-fragment
            // Property.Object=@<the quest itself> shape) — legal on a singular FORMLINK field in CREATE context only,
            // mirroring the top-level singular-value gate exactly (formlink-only + declared-earlier-or-self); the
            // create path substitutes it with the allocated FormKey (WritePatchBuilder.ResolveSiblingRefs). Gated
            // BEFORE CheckValue, which would otherwise reject the '@' token as a malformed FormLink.
            if (WriteEngine.IsSameCallSiblingRef(f.Value, out var fEd))
            {
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
        }
        foreach (var s in spec.Sets ?? new())
            // siblingEditorIds threads through — a same-call @editorid ref inside a COMPOSED struct's nested Sets
            // (e.g. a VMAD quest-fragment's Property.Object=@<own quest>) validates by the SAME gates as a top-level
            // value (formlink-only + declared-earlier-or-self), recursively; on the edit path (null) it still rejects
            // loud rather than being silently accepted.
            // …and the slot name goes with it: these paths are rooted at the STRUCT, and the caller typed them in the
            // nested 'path' slot, not the record-level 'field_path'. Any remedy naming a path must say which.
            if (ValidateFromType(structSchema, s, siblingEditorIds, "path") is { } e) return e;
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
    /// two cardinalities. The offending value is named, so the gate says exactly which element it refused and what
    /// shape is legal, never a bare "internal inconsistency" surfaced from an apply-time throw.</summary>
    static string FormLinkElementReject(string value, FieldSchema leaf) =>
        $"Illegal FormLink element '{value}' for '{leaf.Name}': expected a FormID (XXXXXX:Plugin.esp) " +
        "or a null-clear ('0', '00000000', 'Null', '000000:Null').";

    /// <summary>Validate a polymorphic Set: the arm must be a legal arm of the field, and its contents (flat fields +
    /// nested sets) must validate against the arm type — the same composition-contents check a struct-element Add uses.</summary>
    string? ArmLegality(FieldSchema leaf, StructSpec? arm, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (arm is null) return $"Set on polymorphic field '{leaf.Name}' requires an arm (which arm + its data).";
        // The standalone-poly-FIELD twin of the StructElementLegality base-reject. A CONCRETE poly-base (e.g.
        // ScriptFragments on DialogResponsesAdapter.ScriptFragments) lists ITSELF among its arms, so
        // legal.Contains(base) would otherwise admit a Set composing the base by its OWN name and apply would silently
        // write a degenerate base instance. Filter the base (leaf.TypeRef — this method is only reached for a
        // polymorphic field) out of the legal set, and reject composing the base itself.
        // A concrete base is ALSO the one shape where "no listed arm fits" is real — the field's live type can BE the
        // base (DialogResponses' ScriptFragments: only arm is the SCENE one) — so the refusal must name the working
        // lane (dotted-subfield Sets, which pre-flight descends) or it dead-ends the caller. Recognizer: the field's
        // MUTABLE AQ resolves to a concrete CLASS (the emitted arm lists have the base's self-listing stripped, so
        // arms can't tell). An abstract base resolves abstract — there a listed arm always fits, so the hint stays off.
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

    /// <summary>The bracketed hop that produced the type currently being walked: the collection field's own dotted
    /// path, the key the caller indexed it with, its schema, and the input slot that path belongs in at the root
    /// this walk started from. Held for the IMMEDIATELY preceding hop only, so a refusal raised on the element's
    /// type can name the call that rewrites that element and nothing else. The slot travels WITH the hop because
    /// the path does: both are meaningless without the root they were built against.</summary>
    readonly record struct ElementHop(string Path, string Key, FieldSchema Field, string Slot);

    /// <summary>The dotted path of the hop at <paramref name="index"/>, with its own bracket dropped — earlier hops
    /// keep theirs, because they are still navigation. Relative to the root the walk started from, so the slot it is
    /// printed in is whatever that root's slot is called.</summary>
    internal static string PathTo(string[] path, int index, string name) =>
        string.Join(".", path.Take(index).Append(name));

    /// <summary>Is <paramref name="key"/> a shape this collection can actually be indexed by? A list wants a
    /// parseable NON-NEGATIVE int32 (<see cref="WriteEngine.IsValidListIndexValue"/> — the recogniser apply's
    /// StepIntoElement list branch enforces; a bare int.TryParse accepts '-1', which then throws a plain
    /// InvalidOperationException that surfaces as the misleading "real inconsistency" wrapper). A dict wants a value
    /// of the key's real CLR type (DictKeyType → the dict AQ's args[0], the type apply keys on) — without that AQ,
    /// CheckValue falls to the enum-catalog-by-name fallback and MISSES a non-enum key, e.g. Package.Data's sbyte
    /// key ('Data[notasbyte]') accepted then throwing FormatException at apply. The in-range bound stays apply's job.
    /// <para>One recogniser for both the mid-path hop and the leaf bracket, so the two cannot drift on what a usable
    /// key is — and so a remedy only ever hands back a key it has checked.</para></summary>
    string? KeyShapeError(FieldSchema field, string ownerName, string segName, string key) => field.Cardinality switch
    {
        "list" when !WriteEngine.IsValidListIndexValue(key) =>
            $"List '{segName}' on '{ownerName}' must be indexed by a non-negative integer; got '{key}'.",
        "dict" => CheckValue(field.KeyType, key, $"dict key for '{segName}'", DictKeyType(field)?.AssemblyQualifiedName),
        _ => null,
    };

    /// <summary>What to do about a field the over-arms search refused to pick an arm for. The refusal genuinely
    /// cannot name the arm — that is what it is declining to guess — but when the caller is standing inside a
    /// collection element the WORKING call is a corpus fact the walk already has, so it is named: the container's
    /// path, the caller's own key, and the verbs that shape takes. Without a bracketed hop there is no container to
    /// name, so it states the rule instead.
    /// <para>The verbs are the placing-one-AT-a-key filter, not the keyed one: this sentence promises to write the
    /// whole element in one call, and <see cref="WriteVerbs.HowToAddress"/> would answer it with Remove — a verb
    /// that composes nothing and deletes the element the caller came to edit. It is not the plain
    /// <see cref="WriteVerbs.HowToPlaceOne"/> either: that names a list's keyless Add first, and the caller reading
    /// this has just been handed a key. <see cref="WriteVerbs.HowToPlaceOneAt"/> is the filter that matches the
    /// sentence.</para>
    /// <para>An OWNED-RECORD element has no such call at all — the element is a record, reached on the record axis
    /// by its own FormID — so that shape names no container path and no key: printing them would offer a call
    /// nothing consumes.</para></summary>
    string ElementRemedy(ElementHop? elementHop) =>
        elementHop is { } h && WriteVerbs.OfField(h.Field, _corpus) is { } shape
            ? shape.Element == ElementPlacement.OwnedRecord
                ? $"'{h.Field.Name}' holds owned child RECORDS ({h.Field.ElementTypeRef}), so no write verb reaches " +
                  $"the element through its parent. {AddressChildByFormId(h.Field.Name)}"
                : $"Read the element to learn its concrete arm, then write the whole element in one call, composing " +
                  $"that arm: {h.Slot}='{h.Path}', key='{h.Key}' — {WriteVerbs.HowToPlaceOneAt(shape)}."
            : "Read the element first to learn its concrete arm, then target a field whose shape is unambiguous.";

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
    /// ARMS when the base itself lacks it — the VMAD shape: <c>ScriptEntry.Properties</c> is modeled as a list
    /// of the base <c>ScriptProperty</c> (Name/Flags only), but every REAL element is a concrete arm
    /// (<c>ScriptObjectProperty</c> carries Object/Alias), so a path like <c>Properties[0].Object</c> is legal even
    /// though the BASE schema lacks 'Object'. Generic over every polymorphic-base family — no per-type wiring
    /// (cornerstone). The static validator cannot know WHICH arm sits at a given index, so: a name found on arms
    /// must AGREE in shape across all the arms that declare it (one shape validates for whichever arm the element
    /// turns out to be — the engine then resolves on the element's RUNTIME type and fails loud on a real mismatch);
    /// arms that DISAGREE reject by name, never guess. <paramref name="effectiveOwner"/> is the schema the found
    /// field belongs to (the arm for an arm-found field), so downstream messages name the real owner.</summary>
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
                // Arms and the catalog come out of the same reflection walk, so a listed-but-absent arm is a real
                // corpus defect — surfaced loud, never skipped: skipping could fake shape-agreement over an
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
                        "the validator cannot pick one statically. " + ElementRemedy(elementHop);
                return null;
            }
        effectiveOwner = firstArm;
        return firstField;
    }

    /// <summary>Two arm declarations of the same field name agree iff every navigation/validation-relevant facet
    /// matches — cardinality, display + referenced types, element type, writability, AND the assembly-qualified
    /// CLR types (two arms can share a display name like 'Flags' while binding DIFFERENT enum types; ValueLegality
    /// validates against the AQ-resolved type, so AQ disagreement means the value would be checked against the
    /// wrong arm's legal set). Identity by what the validator USES, so "agrees" can never silently mean
    /// "close enough".
    ///
    /// The CLR-type facets compare write-legal EQUIVALENCE, not raw-string identity: a type and
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
    /// identity, so a genuinely unknown type can never be silently widened.
    /// Null matches only null (one arm declaring the facet and the other not is a real difference).</summary>
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

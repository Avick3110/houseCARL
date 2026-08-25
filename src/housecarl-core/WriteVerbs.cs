namespace HousecarlCore;

/// <summary>Which half of the collection surface a leaf is. This is the fact that decides whether a remedy may
/// name an INDEX verb or a KEY verb, and it is the fact the class-A defect kept getting wrong: a dict caller was
/// reachable by a message reciting list verbs, because the recital was typed by hand next to a condition that did
/// not scope it.</summary>
public enum CollectionKind
{
    /// <summary>Positional — elements are addressed by index.</summary>
    List,
    /// <summary>Keyed — entries are addressed by their key.</summary>
    Dict,
}

/// <summary>How an element gets INTO the collection — the fact that decides which placing verbs work and which
/// input slot each of them consumes.</summary>
public enum ElementPlacement
{
    /// <summary>One coerced value (scalar / enum / formlink / whole-coercible asset path): value=, values=, entries=.</summary>
    Coerced,
    /// <summary>Built FROM PARTS against a modeled struct or a polymorphic arm: compose=, composes=.</summary>
    Composed,
    /// <summary>An OWNED CHILD RECORD — allocated on the record axis (housecarl_create_record with parent=), never
    /// placed into a parent's collection by a write verb.</summary>
    OwnedRecord,
}

/// <summary>The two facts together. Everything <see cref="WriteVerbs.On"/> says is a function of this and nothing
/// else, which is what makes "a dict caller cannot receive a list-verb remedy" structural rather than careful.</summary>
public readonly record struct CollectionShape(CollectionKind Kind, ElementPlacement Element);

/// <summary>The input slot a verb consumes on a given shape. Carried as DATA rather than prose so a guard can
/// build a well-formed request for every verb the formatter names and replay it through the real gate.</summary>
public enum VerbInput
{
    /// <summary>No value slot (the key alone identifies what to do).</summary>
    None,
    /// <summary>The singular <c>value=</c>.</summary>
    Value,
    /// <summary>The whole-list <c>values=</c>.</summary>
    Values,
    /// <summary>The key-to-value <c>entries=</c>.</summary>
    Entries,
    /// <summary>The singular build-from-parts <c>compose=</c>.</summary>
    Compose,
    /// <summary>The batch build-from-parts <c>composes=</c>.</summary>
    Composes,
}

/// <summary>One verb, as it actually works on one shape: what it consumes, whether it needs a key, whether it PUTS
/// an element in (as opposed to addressing one that is already there), and the phrase a remedy prints for it.</summary>
public readonly record struct VerbUse(string Verb, VerbInput Input, bool NeedsKey, bool Places, string Does);

/// <summary>
/// The one home IN CODE for houseCARL's write-verb vocabulary, and the one derivation of which of those verbs work
/// on a given collection shape.
///
/// <para><b>Why this exists.</b> Seven emitted messages recited verb names by hand, each next to the condition that
/// produced it, and they drifted: a dict caller reached a message offering <c>InsertAtIndex</c> (measured on
/// <c>Package.Data</c> — following the offer returns "InsertAtIndex is only valid on list"); the Set-on-list remedy
/// named two of the five list verbs; the leaf-bracket remedy offered both cardinalities' verbs at once and left the
/// caller to work out which half was theirs. Adding <c>InsertAtIndex</c> (#302) made the drift visible by adding a
/// seventh name to keep in sync, but it did not cause it — a hand-maintained copy of a set rots whether or not the
/// set is growing. Two consecutive pre-PR review rounds returned instances of it, which is the CLAUDE.md §11
/// class-stop: the fix is the generator, not another round of copies.</para>
///
/// <para><b>What a caller gets.</b> <see cref="On"/> maps a <see cref="CollectionShape"/> to the verbs that WORK on
/// it. Sites select a purpose subset by the flags on <see cref="VerbUse"/> — <c>NeedsKey</c> for "how do I address
/// ONE element", <c>Places</c> for "how do I put an element in", <c>Places || NeedsKey</c> for "the collection
/// verbs" — and never by naming verbs themselves. A site therefore cannot recite a verb the shape does not
/// support, and a verb added to (or removed from) the surface reaches every message at once.</para>
///
/// <para><b>How it is kept honest.</b> Not by resembling <see cref="CorpusRulebook"/>'s verb-indexed switch — a
/// mirror of that code would prove only that it had been copied correctly. This table is indexed by SHAPE, and the
/// agreement is MEASURED: <c>remedy-verbs-guard</c> buckets every collection field in the corpus by shape, and for
/// each populated bucket replays a well-formed request for every verb named here through the real
/// <see cref="CorpusRulebook.Validate"/> (each must be ACCEPTED) and for every verb NOT named here (each must be
/// REFUSED). Two independent routes to one fact; either one drifting turns the guard red.</para>
///
/// <para><b>Declared boundary.</b> This answers "which verbs suit this SHAPE". It does not answer "is this
/// particular field writable" (<see cref="FieldSchema.Writable"/> / <see cref="FieldSchema.IsIdentity"/>) or "is
/// this particular value well-formed" — later gates own those and name themselves when they fire. And it describes
/// COLLECTIONS only: <see cref="All"/> is the vocabulary a non-collection site asks for, because a caller who typed
/// a verb that does not exist has a vocabulary problem, not a shape problem.</para>
/// </summary>
public static class WriteVerbs
{
    /// <summary>Every verb the write surface accepts, in the order the shipped tool descriptions list them. The home
    /// for the names as a collection code can ITERATE, and the one of the two in-code statements whose membership is
    /// actually pinned: <c>remedy-verbs-guard</c>'s SITE-UNKNOWN-VERB holds this against a vocabulary written
    /// independently inside that probe. <see cref="AllRecital"/> states the same names a second time, as the
    /// caller-facing literal an attribute can concatenate, and carries no such pin — see its summary for what that
    /// does and does not establish. <see cref="On"/> is the one home for which of them apply where. The prose home
    /// for the set as DOCUMENTATION is the tool-surface SPEC, which CLAUDE.md points readers at — two audiences,
    /// not two authorities on one fact.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { "Set", "Add", "Remove", "SetAtIndex", "InsertAtIndex", "ReplaceAll", "Merge", "CopyFrom" };

    /// <summary>The same vocabulary as the CALLER-FACING recital a <c>[Description]</c> prints — the pipe-joined
    /// form, with the defaulting verb marked. Three shipped descriptions concatenate it today rather than typing
    /// the names out: <c>ApplyTools</c>'s <c>op=</c> method prose, the <c>ApplyOp.op</c> shape declaration, and
    /// the <c>BulkOp.verb</c> one.
    ///
    /// <para><b>Why a second member, rather than reading <see cref="All"/> there.</b> An attribute argument must be
    /// a compile-time constant, and <see cref="All"/> is a collection built at runtime — so a description literally
    /// cannot read it. The choice is therefore between one <c>const</c> the descriptions share and one hand-typed
    /// copy per description — and the hand-typed arrangement is the one #302 had to go round and edit site by site
    /// when it added <c>InsertAtIndex</c>, and the one PR #339 retired everywhere it could reach. This is that
    /// pattern applied to the description surface (#386).</para>
    ///
    /// <para><b>What this does NOT establish.</b> Two members are two statements of one fact, and nothing here
    /// holds them together: no build step asserts that this recital names exactly <see cref="All"/>. Nor does
    /// anything stop a future description from hand-typing the whole set instead of concatenating this — a site
    /// that did would be the #302 edit-every-site regression back again, silently. The three sites named above are
    /// a statement about the code as it stands, not a property anything enforces. Making both of those checkable
    /// is #386's job; this const is the single home the check would be written against.
    ///
    /// <para><b>One hazard this const CREATES, until #386's guard exists.</b> <c>BulkOp.verb</c> appends
    /// <c>" (deep-copy the field at field_path from from_plugin's version — see from_plugin)"</c> straight onto
    /// this recital, so that gloss describes whichever verb is LAST here — <c>CopyFrom</c> today, by position and
    /// nothing else. Appending a ninth verb — the very edit this const exists to make sufficient — silently moves
    /// the gloss onto the new verb and strips it off <c>CopyFrom</c>, shipping a false claim in
    /// <c>housecarl_bulk_apply</c>'s schema; reordering does the same. The other two sites are position-independent
    /// (one appends after a full stop, one reads <c>"op is " + AllRecital + ". "</c>). Until a guard pins the tail
    /// token to the verb the trailing gloss describes, the protection is the vocabulary gate before W6: changing
    /// this set is a reviewed act, not a drive-by. Recorded rather than fixed because the fix moves caller-facing
    /// text, and this const's whole warrant is that it changed none.</para></summary>
    public const string AllRecital = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge | CopyFrom";

    /// <summary>The verbs that work on <paramref name="shape"/>, each with the slot it consumes and the phrase a
    /// remedy prints for it. Indexed by shape — the two facts in <see cref="CollectionShape"/> are the whole input,
    /// so no site can reach a verb the shape does not support.</summary>
    public static IReadOnlyList<VerbUse> On(CollectionShape shape)
    {
        // An owned child record is not PLACED by a write verb at all: it is allocated on the record axis, and the
        // create-oriented verbs redirect there by construction. What survives is addressing one that already
        // exists — and CopyFrom is refused too (a field's VALUE transplants; owned children do not).
        if (shape.Element == ElementPlacement.OwnedRecord)
            return new[] { Address(shape) };

        bool composed = shape.Element == ElementPlacement.Composed;
        var one = composed ? VerbInput.Compose : VerbInput.Value;

        if (shape.Kind == CollectionKind.List)
            return new List<VerbUse>
            {
                // Set is absent by construction, not by omission: a list element is addressed by POSITION, so a
                // whole-field Set has no element to mean.
                new("Add", one, false, true, "appends a new element at the END"),
                // SetAtIndex before InsertAtIndex, deliberately. The keyed subset of this list is what a caller who
                // bracketed a leaf (`Keywords[0]`) is shown, and they bracketed an index that ALREADY holds an
                // element — so the verb that operates on the element already there is the one to lead with. Leading
                // with insert is the one wrong first choice on this branch that does not refuse: it succeeds, one
                // element longer, with the tail shifted, which on a CTDA OR-run changes what the record gates on.
                new("SetAtIndex", one, true, true, "overwrites the element already at that index, in place"),
                new("InsertAtIndex", one, true, true,
                    "inserts a new element AT that index and shifts the rest right (the list's length appends)"),
                // The batch slot differs with the element: a modeled list replaces through composes=, a coercible
                // one through values=. Same verb, different surface — the kind of detail a hand-recited name drops.
                new("ReplaceAll", composed ? VerbInput.Composes : VerbInput.Values, false, true,
                    "clears the list, then appends each"),
                Address(shape),
                Transplant,
            };

        var dict = new List<VerbUse>
        {
            new("Set", one, true, true, "sets that entry, replacing any entry already there"),
            new("Add", one, true, true, "adds a NEW entry under that key"),
            Address(shape),
        };
        // CopyFrom is deliberately absent from every DICT shape: transplanting a dict field is not built
        // ("a dict isn't transplanted yet" — CopyFromLegality), so naming it here would be the same over-claim in
        // the other direction. Measured, not assumed — the guard's refuse sweep is what found it.
        // ReplaceAll/Merge carry their elements in entries=, a plain key-to-value shape with no build-from-parts
        // form — so on a MODELED-element dict they are a later surface, and naming them would be exactly the lie
        // the class-A medium was made of.
        if (!composed)
        {
            dict.Add(new VerbUse("ReplaceAll", VerbInput.Entries, false, true, "clears the dict, then sets each entry"));
            dict.Add(new VerbUse("Merge", VerbInput.Entries, false, true, "sets each entry, leaving the rest alone"));
        }
        return dict;
    }

    /// <summary>Remove, phrased for the cardinality — the one verb that addresses an element already there, and the
    /// only one an owned-record collection has. A list Remove also accepts a by-VALUE form (no key); the keyed form
    /// is the one remedies name because it is the form every element kind supports.</summary>
    static VerbUse Address(CollectionShape shape) => shape.Kind == CollectionKind.List
        ? new VerbUse("Remove", VerbInput.None, true, false, "drops the element at that index")
        : new VerbUse("Remove", VerbInput.None, true, false, "drops that entry");

    /// <summary>CopyFrom is neither a placing verb nor a keyed one — it transplants the WHOLE field from another
    /// plugin's version of the record — so the purpose filters leave it out of element-level remedies while a site
    /// asking for everything the shape accepts still gets it. LIST shapes only: a dict field is not transplantable
    /// yet, and an owned-record collection is refused by kind.</summary>
    static readonly VerbUse Transplant =
        new("CopyFrom", VerbInput.None, false, false, "transplants the whole field from another plugin's version");

    /// <summary>Render a chosen subset as "Verb (slot= + key=) what it does", joined for a message. The site
    /// supplies the sentence around it and never the names inside it.</summary>
    public static string Sentence(IEnumerable<VerbUse> uses) =>
        string.Join("; ", uses.Select(u =>
        {
            var slots = new List<string>();
            if (u.Input != VerbInput.None) slots.Add(SlotName(u.Input) + "=");
            if (u.NeedsKey) slots.Add("key=");
            return slots.Count == 0 ? $"{u.Verb} {u.Does}" : $"{u.Verb} ({string.Join(" + ", slots)}) {u.Does}";
        }));

    /// <summary>Just the names, comma-joined — for a site listing vocabulary rather than giving guidance.</summary>
    public static string Names(IEnumerable<VerbUse> uses) => string.Join(", ", uses.Select(u => u.Verb));

    // ---- the three purposes a site asks about. Each is a FILTER over On(), never a second list of names. ----

    /// <summary>"How do I put an element INTO this collection." The placing verbs — or, for the one shape that has
    /// none, why it has none: an owned child record is allocated on the record axis, so there is no write verb to
    /// name and a remedy that named one would be sending the caller down a path that refuses.</summary>
    public static string HowToPlace(CollectionShape shape) =>
        shape.Element == ElementPlacement.OwnedRecord
            ? "its elements are owned child RECORDS, which are created on the record axis — use housecarl_create_record "
              + "/ housecarl_bulk_create with parent= the parent's FormID, not a write verb"
            : Sentence(On(shape).Where(u => u.Places));

    /// <summary>"How do I put in ONE element." <see cref="HowToPlace"/> minus the verbs that take a WHOLE
    /// collection — a site that says "one element at a time" and then names <c>ReplaceAll</c> is contradicting
    /// itself in the same sentence. Batch-ness is read off the input slot, which the table already carries, so it
    /// is not a second opinion about which verbs are batch.</summary>
    public static string HowToPlaceOne(CollectionShape shape) =>
        shape.Element == ElementPlacement.OwnedRecord
            ? HowToPlace(shape)
            : Sentence(On(shape).Where(u => u.Places && !IsBatch(u.Input)));

    /// <summary>The keyed verbs, in table order — how to reach ONE element of this collection by index or key.
    /// Named for what the filter is (a key is required), not for "an element that is already there": a list's
    /// <c>InsertAtIndex</c> takes a key and addresses the GAP at that index, and it belongs in the menu because a
    /// caller who bracketed a leaf may well have meant it. The table orders the existing-element verb first, which
    /// is what makes the menu safe to read top-down.</summary>
    public static string HowToAddress(CollectionShape shape) => Sentence(On(shape).Where(u => u.NeedsKey));

    /// <summary>Does this slot carry a WHOLE collection rather than one element?</summary>
    static bool IsBatch(VerbInput input) =>
        input is VerbInput.Values or VerbInput.Entries or VerbInput.Composes;

    /// <summary>The collection verbs by name — the ones that put an element in or address one — for a site that is
    /// naming a set rather than giving guidance. CopyFrom is excluded by the filter, not by hand: it is neither.</summary>
    public static string CollectionVerbNames(CollectionShape shape) =>
        Names(On(shape).Where(u => u.Places || u.NeedsKey));

    static string SlotName(VerbInput input) => input switch
    {
        VerbInput.Value => "value",
        VerbInput.Values => "values",
        VerbInput.Entries => "entries",
        VerbInput.Compose => "compose",
        VerbInput.Composes => "composes",
        _ => throw new InvalidOperationException($"No slot name for {input}."),
    };

    // ---- the two routes to a shape ----

    /// <summary>The SCHEMA route: a corpus <see cref="FieldSchema"/> plus the shared <see cref="SchemaClassifier"/>
    /// the gate itself classifies elements with. Null when the leaf is not a collection, or when the element kind
    /// is one this table declines to describe — a caller that gets null prints its message without naming verbs.
    /// <para/>
    /// DECLINED: <see cref="ElementKind.ScalarUncoercible"/> and <see cref="ElementKind.Unknown"/>. Neither has a
    /// settled legal-verb answer — an uncoercible element has no plain-value form, yet no gate check refuses an Add
    /// carrying one (a dormant accept-then-throw that predates this table and is not its to close). Describing that
    /// shape would mean printing a verb the gate accepts and apply rejects, so it prints nothing.
    /// <c>remedy-verbs-guard</c> reports the corpus population of every shape, declined ones included, so
    /// "declined" stays a measured statement rather than an assumption.</summary>
    public static CollectionShape? OfField(FieldSchema leaf, Corpus corpus)
    {
        var kind = leaf.Cardinality switch
        {
            "list" => CollectionKind.List,
            "dict" => CollectionKind.Dict,
            _ => (CollectionKind?)null,
        };
        if (kind is not { } k) return null;
        return SchemaClassifier.ClassifyElement(leaf, corpus) switch
        {
            ElementKind.ScalarCoercible or ElementKind.WholeCoercible => new CollectionShape(k, ElementPlacement.Coerced),
            ElementKind.Struct or ElementKind.Arm => new CollectionShape(k, ElementPlacement.Composed),
            ElementKind.Record => new CollectionShape(k, ElementPlacement.OwnedRecord),
            _ => null,
        };
    }

    /// <summary>The RUNTIME route, for the engine's own throws: no corpus in scope, so the shape comes off the live
    /// property type through the SAME interface tests <c>ApplyVerb</c> dispatches on and the SAME coercion
    /// recogniser <c>Coerce</c> is built from. It has to be a second route — threading the corpus into the verb
    /// path would cost the engine the schema-blindness <see cref="SchemaClassifier"/> is documented to protect —
    /// and <c>remedy-verbs-guard</c> replays every collection field in the corpus through BOTH routes and requires
    /// the same answer, which is the only thing that keeps two routes from becoming two opinions.</summary>
    public static CollectionShape? OfRuntimeType(Type leafType)
    {
        // Coercion owns a whole-coercible leaf even when its runtime type also implements IList/IDict — the same
        // order ApplyVerb dispatches in, so this cannot call something a collection that the engine does not.
        if (WriteEngine.CanCoerce(leafType)) return null;
        Type elem;
        CollectionKind kind;
        if (WriteEngine.ClosedInterface(leafType, typeof(IDictionary<,>)) is { } di)
        { kind = CollectionKind.Dict; elem = di.GetGenericArguments()[1]; }
        else if (WriteEngine.ClosedInterface(leafType, typeof(IList<>)) is { } li)
        { kind = CollectionKind.List; elem = li.GetGenericArguments()[0]; }
        else return null;
        return OfElement(kind, elem);
    }

    /// <summary>The runtime route for a caller that ALREADY knows it holds a collection of <paramref name="kind"/>
    /// — the engine's list-verb path, which only runs after <c>ApplyVerb</c> has matched <c>IList&lt;T&gt;</c>.
    /// Non-nullable on purpose: a site whose cardinality is settled by the control flow that reached it should not
    /// have to ship a fallback arm that cannot fire.</summary>
    public static CollectionShape OfElement(CollectionKind kind, Type elementType)
    {
        // A link is a value, not a child — tested first, exactly as the child-bearing walk tests it, so a
        // FormLink<T> element never reads as an owned record.
        if (typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter).IsAssignableFrom(elementType))
            return new CollectionShape(kind, ElementPlacement.Coerced);
        if (typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter).IsAssignableFrom(elementType))
            return new CollectionShape(kind, ElementPlacement.OwnedRecord);
        if (WriteEngine.CanCoerce(elementType)) return new CollectionShape(kind, ElementPlacement.Coerced);
        return new CollectionShape(kind, ElementPlacement.Composed);
    }
}

namespace HousecarlCore;

/// <summary>Which half of the collection surface a leaf is — the fact that decides whether a remedy may name an INDEX verb or a KEY verb.</summary>
public enum CollectionKind
{
    List,
    Dict,
}

/// <summary>How an element gets INTO the collection — the fact that decides which placing verbs work and which input slot each consumes.</summary>
public enum ElementPlacement
{
    /// <summary>One coerced value (scalar / enum / formlink / whole-coercible asset path): value=, values=, entries=.</summary>
    Coerced,
    /// <summary>Built FROM PARTS against a modeled struct or a polymorphic arm: compose=, composes=.</summary>
    Composed,
    OwnedRecord,
}

public readonly record struct CollectionShape(CollectionKind Kind, ElementPlacement Element);

public enum VerbInput
{
    None,
    Value,
    Values,
    Entries,
    Compose,
    Composes,
}

/// <summary>One verb as it works on one shape: what it consumes, whether it needs a key, whether it PUTS an element in, and the phrase a remedy prints.</summary>
public readonly record struct VerbUse(string Verb, VerbInput Input, bool NeedsKey, bool Places, string Does);

/// <summary>The one home IN CODE for the write-verb vocabulary and for which verbs work on a collection shape; contracts in docs/architecture/write-path.md.</summary>
public static class WriteVerbs
{
    /// <summary>Every verb the write surface accepts, in the order the shipped tool descriptions list them — the home for the names as a collection code can ITERATE.</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { "Set", "Add", "Remove", "SetAtIndex", "InsertAtIndex", "ReplaceAll", "Merge", "CopyFrom" };

    /// <summary>The same vocabulary as the CALLER-FACING recital a <c>[Description]</c> prints; its LAST token is load-bearing.</summary>
    public const string AllRecital = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge | CopyFrom";

    /// <summary>The verb that copies a field from another version of a record, named once so the surfaces that refuse it do not each spell it.</summary>
    public const string Transplanting = "CopyFrom";

    /// <summary>The verbs a CREATE surface accepts — <see cref="All"/> minus the one it refuses by name, DERIVED rather than hand-typed.</summary>
    public static readonly IReadOnlyList<string> OnCreate = BuildOnCreate();

    /// <summary>The CALLER-FACING recital for the create surface, a compile-time const for the same reason <see cref="AllRecital"/> is one.</summary>
    public const string OnCreateRecital = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex | ReplaceAll | Merge";

    /// <summary>The verbs that read an INPUT SLOT OF THEIR OWN, beside the value/key/compose every verb shares.</summary>
    public static readonly IReadOnlyList<string> SlotBearing = new[] { "ReplaceAll", "Merge", Transplanting };

    /// <summary>The verbs a compose's nested <c>sets</c> accept — <see cref="All"/> minus the slot-bearing ones, DERIVED rather than hand-typed.</summary>
    public static readonly IReadOnlyList<string> InCompose = BuildInCompose();

    /// <summary>The CALLER-FACING recital for a compose's nested sets, a compile-time const for the same reason.</summary>
    public const string InComposeRecital = "Set (default) | Add | Remove | SetAtIndex | InsertAtIndex";

    static IReadOnlyList<string> BuildInCompose()
    {
        var missing = SlotBearing.Where(v => !All.Contains(v, StringComparer.Ordinal)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                $"WriteVerbs.SlotBearing names [{string.Join(", ", missing)}], absent from WriteVerbs.All "
                + $"([{string.Join(", ", All)}]) — InCompose would subtract nothing and publish verbs the nested "
                + "gate refuses. Spell the verb the same way in both.");
        return All.Where(v => !SlotBearing.Contains(v, StringComparer.Ordinal)).ToArray();
    }

    static IReadOnlyList<string> BuildOnCreate()
    {
        if (!All.Contains(Transplanting, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"WriteVerbs.Transplanting is '{Transplanting}', which is not in WriteVerbs.All "
                + $"([{string.Join(", ", All)}]) — OnCreate would subtract nothing and become the whole vocabulary. "
                + "Spell the verb the same way in both.");
        return All.Where(v => !string.Equals(v, Transplanting, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>The verbs that work on <paramref name="shape"/>, each with the slot it consumes and the phrase a remedy prints for it.</summary>
    public static IReadOnlyList<VerbUse> On(CollectionShape shape)
    {
        if (shape.Element == ElementPlacement.OwnedRecord)
            return new[] { Address(shape) };

        bool composed = shape.Element == ElementPlacement.Composed;
        var one = composed ? VerbInput.Compose : VerbInput.Value;

        if (shape.Kind == CollectionKind.List)
            return new List<VerbUse>
            {
                // Set is absent by construction: a list element is addressed by POSITION, so it has no element to mean.
                new("Add", one, false, true, "appends a new element at the END"),
                new("SetAtIndex", one, true, true, "overwrites the element already at that index, in place"),
                new("InsertAtIndex", one, true, true,
                    "inserts a new element AT that index and shifts the rest right (the list's length appends)"),
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
        // ReplaceAll and Merge carry their elements in entries=, which has no build-from-parts form.
        if (!composed)
        {
            dict.Add(new VerbUse("ReplaceAll", VerbInput.Entries, false, true, "clears the dict, then sets each entry"));
            dict.Add(new VerbUse("Merge", VerbInput.Entries, false, true, "sets each entry, leaving the rest alone"));
        }
        return dict;
    }

    /// <summary>Remove, phrased for the cardinality — the one verb that addresses an element already there, and the only one an owned-record collection has.</summary>
    static VerbUse Address(CollectionShape shape) => shape.Kind == CollectionKind.List
        ? new VerbUse("Remove", VerbInput.None, true, false, "drops the element at that index")
        : new VerbUse("Remove", VerbInput.None, true, false, "drops that entry");

    /// <summary>CopyFrom, which is neither a placing verb nor a keyed one, so the purpose filters leave it out of element-level remedies. LIST shapes only.</summary>
    static readonly VerbUse Transplant =
        new("CopyFrom", VerbInput.None, false, false, "transplants the whole field from another plugin's version");

    /// <summary>Render a chosen subset as "Verb (slot= + key=) what it does", joined for a message.</summary>
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

    /// <summary>"How do I put an element INTO this collection" — or, for the owned-record shape, why there is no verb to name.</summary>
    public static string HowToPlace(CollectionShape shape) =>
        shape.Element == ElementPlacement.OwnedRecord
            ? "its elements are owned child RECORDS, which are created on the record axis — use " + ToolNames.Create + " "
              + "with parent= the parent's FormID in its records= element, not a write verb"
            : Sentence(On(shape).Where(u => u.Places));

    public static string HowToPlaceOne(CollectionShape shape) =>
        shape.Element == ElementPlacement.OwnedRecord
            ? HowToPlace(shape)
            : Sentence(On(shape).Where(u => u.Places && !IsBatch(u.Input)));

    public static string HowToPlaceOneAt(CollectionShape shape) =>
        shape.Element == ElementPlacement.OwnedRecord
            ? HowToPlace(shape)
            : Sentence(On(shape).Where(u => u.Places && u.NeedsKey && !IsBatch(u.Input)));

    public static string HowToAddress(CollectionShape shape) => Sentence(On(shape).Where(u => u.NeedsKey));

    static bool IsBatch(VerbInput input) =>
        input is VerbInput.Values or VerbInput.Entries or VerbInput.Composes;

    /// <summary>The collection verbs by name — the ones that put an element in or address one — for a site naming a set rather than giving guidance.</summary>
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

    /// <summary>The SCHEMA route to a shape; null for a non-collection leaf and for the two element kinds this table declines to describe.</summary>
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

    /// <summary>The RUNTIME route, for the engine's own throws: the shape off the live property type, through the same interface tests and coercion recogniser the engine dispatches on.</summary>
    public static CollectionShape? OfRuntimeType(Type leafType)
    {
        // Coercion owns a whole-coercible leaf even when its runtime type also implements IList/IDict.
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

    /// <summary>The runtime route for a caller that ALREADY knows it holds a collection of <paramref name="kind"/>.</summary>
    public static CollectionShape OfElement(CollectionKind kind, Type elementType)
    {
        // A link is a value, not a child — tested first, exactly as the child-bearing walk tests it.
        if (typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter).IsAssignableFrom(elementType))
            return new CollectionShape(kind, ElementPlacement.Coerced);
        if (typeof(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter).IsAssignableFrom(elementType))
            return new CollectionShape(kind, ElementPlacement.OwnedRecord);
        if (WriteEngine.CanCoerce(elementType)) return new CollectionShape(kind, ElementPlacement.Coerced);
        return new CollectionShape(kind, ElementPlacement.Composed);
    }
}

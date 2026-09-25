---
updated: 2026-09-23
covers: [src/housecarl-core/CorpusRulebook.cs, src/housecarl-core/WriteEngine.cs, src/housecarl-mcp/TypeLookup.cs]
---
# The corpus rulebook: what pre-flight decides, and why it can never disagree with apply

## What it is

The rulebook is the write surface's pre-flight: every write is validated against the generated schema before any
Mutagen mutation. This file is the home of the contracts that hold it, cited from the code under ADR 0001.

## Contracts

### Gate and apply share one recogniser, never two

Every value the gate judges is judged with the predicate the apply path will use on the same value — never a second
spelling of the same rule. That is what makes a refusal a real answer instead of a guess, and it is why so many
checks read as one call into `WriteEngine`:

| what is judged | the shared recogniser |
|---|---|
| a `[Flags]` leaf, for the bit verbs | the field's own assembly-qualified type, never the simple-name catalog, which collides on `Flags` / `MajorFlags` |
| a list index | `WriteEngine.IsValidListIndexValue` — parseable, non-negative int32 |
| a dict key | the dict AQ's `args[0]`, the type `ApplyDictVerb` keys on |
| a FormLink value | `WriteEngine.IsValidFormLinkValue`, plus `IsFormKeyNullSynonym` for a clear |
| a condition FormLinkOrIndex target | `WriteEngine.IsFormLinkOrIndex` + `TryClassifyFloiValue` |
| `ctor_args` shape and arity | `WriteEngine.TryRecognizeCtorArgs`, which mirrors `Instantiate` |
| "is this type buildable at all" | `WriteEngine.TryRecognizeInstantiable`, the method `BuildStruct` instantiates through |

Two bounds stay with apply, deliberately, because the gate has no live collection: whether an index is **in range**,
and insert's admission of the append slot (`index == count`).

### Presence, shape, and range are three different gates

- **PRESENCE** — does the verb have the key, index or value it consumes at all — is `VerbLegality`'s.
- **SHAPE** — is the present value one the coercion can read — is `ValueLegality`'s.
- **RANGE** is apply's, as above.

A slot the apply path never reads is not over-rejected: the value checks are scoped to the verbs that actually
consume each slot, so a list `Remove` by index does not have its (unused) value judged, and a stray off-cardinality
slot apply ignores is left alone.

### Shape before verb, and the owned-child doors

A refusal must not end by naming a call that refuses on the caller's next attempt. So at every compose door the
SHAPE is decided before the VERB: the verb sentence ("use composes= with Add or ReplaceAll") is true only of a list
of modeled elements, and the owned-child answer runs first of all, because the cardinality sentence it would
otherwise fall to ends by pointing at `compose=` / `value=`, which refuse on that shape too.

**An owned child record is written on the record axis, by its own FormID — never built into a parent by a write
verb.** The shape has two forms and three doors, and the doors share predicates but not sentences:

- the SINGULAR form (`Cell.Landscape`, `Worldspace.TopCell`) is `SchemaClassifier.IsOwnedChildRecord`;
- the COLLECTION form (`Cell.Persistent`, `DialogTopic.Responses`) is `IsOwnedChildRecordCollection`, the twin the
  singular predicate does not match;
- the doors are the collection verbs, `composes=`, and `CopyFrom`.

Each door keeps its own remedy because what the caller does next differs — `create` with `parent=` (and
`collection=` where the parent holds more than one fitting list) for a child that does not exist, `forward` for one
that does — but a door that forgets to ask falls through to a label that reads the element kind off `FormLinkTarget`
and calls an owned child record "coercible", which is the failure the named predicates exist to close.

**`CopyFrom` refuses at any depth.** A leaf-only test would admit a path that merely runs THROUGH an owned child,
and apply would then read one plugin's child record and write into another's, with neither named by the call and the
result reported as an edit to the parent. The in-place verbs through the same hop stay accepted: they edit the child
this record already carries, which is the only way to edit a carried child at all.

### A polymorphic base is never composed

An element or field whose type is a polymorphic base is composed by choosing a concrete ARM. Naming the base itself
is rejected at both entry points — `StructElementLegality` and `ArmLegality`.

The recogniser is the corpus's poly-base KIND, **not** `Type.IsAbstract`: a concrete base (`APackageData`) has a
public parameterless constructor, so an `IsAbstract` check would miss it and apply would silently write a degenerate
base instance; an abstract base (`Condition`) would instead throw at `Invoke`. A concrete base also lists ITSELF
among its arms, so it is filtered out of the legal-arms set everywhere — the arm-match test and every message alike
— and neither path can admit it or advertise it.

A concrete base is also the one shape where "no listed arm fits" is real, because the field's live type can BE the
base. Its refusal therefore names the working lane — dotted-subfield Sets, which pre-flight descends — rather than
dead-ending the caller. That hint is gated on the field's mutable AQ resolving to a concrete class.

### Over-arms search: agree in shape, or refuse by name

`FindField` looks through a polymorphic base's ARMS when the base itself lacks a name, which is what makes
`Properties[0].Object` legal on a list modeled as the base. The static validator cannot know which arm sits at a
given index, so a name found on arms must AGREE in shape across every arm that declares it; arms that disagree
reject by name and never guess.

"Agree" is identity by **what the validator uses**: cardinality, display and referenced types, element type,
writability, identity, and the assembly-qualified CLR types. The CLR-type facets compare **write-legal
equivalence**, not raw-string identity — `WriteEngine`'s Coerce/CanCoerce unwrap `Nullable<T>` before checking, so
`float` and `float?` admit the identical value set and agree (`APerkEffect.Value` is such a field).
The raw `Nullable` flag is therefore not compared, while every genuine difference still rejects. A name that will
not resolve to a runtime Type falls back to raw-string identity, so an unknown type can never be silently widened.

A listed-but-absent arm is a real corpus defect and is surfaced loud: skipping it could fake shape-agreement over an
incomplete arm set, or fake "no such field" for a field that arm exclusively declares.

### The FormLink target-type gate

The value-shape checks prove a FormID parses; they say nothing about WHAT it points at, and a link set to a record
of the wrong type serializes fine and is wrong in game. The allowed set is written down nowhere: the generator
stamps every formlink field with its Mutagen link-target interface, so the question is one `IsAssignableFrom`
against the resolved record's own runtime type, and the printed legal names are the corpus record types satisfying
the same interface.

Three things it deliberately does not do:

- a field whose link accepts any record admits everything by construction and is never refused;
- an **unresolvable** FormID is not type-checked — the order cannot say what it is, and a link to a record that is
  not present is the dangling-reference check's business;
- **`Remove` is exempt at every slot.** It takes an element OUT, and a list already carrying a wrong-typed link
  written by another mod is exactly what a caller needs to be able to repair.

### The harvest pass and the checking pass are one walk

The write path cannot type-check a link before it knows which records to resolve, so it runs `Validate` twice
through two derived rulebooks: `WithLinkHarvest(sink)` first, collecting every FormLink value the walk reaches and
type-checking nothing, then `WithLinkTargets(lookup)` with the resolved lookup.

Both passes are the SAME walk, which is the whole point: a link slot added to the validator is prefetched by
construction, where a hand-written slot list on the caller's side would drift and a slot it missed would go silently
unchecked. Two consequences follow:

- `CollectLinkValues` **returns its verdict**. A write that contributed no value to the sink was decided by a walk
  identical to the checking one, so the caller keeps that verdict and skips the second walk. A write that did
  contribute is re-walked against the resolved lookup.
- A same-call `@editorid` reference is added to the sink too, although it is not a FormID. The sink COUNT is what
  says this write's verdict depends on the sibling set — which the create lane offers in full to the harvest and
  only up to the current spec to the check — so without it a forward reference would be settled by the harvest's
  more permissive answer.

The link-target lookup rides on the rulebook rather than through the recursion, so every value slot sees it without
a parameter at each hop, and the printed-legal-names memo spans the whole call.

### `@editorid`: where a same-call reference is legal

An `@editorid` value names a record created earlier in the same `create` call — or the record being created ITSELF —
and is substituted with the allocated FormKey after allocation. It is legal **only** in create context
(`siblingEditorIds` non-null); on the edit path it is refused loud rather than substituting nothing. Its placements:

- a SINGULAR value: `Set` on a singular FormLink leaf, or `Add` on a FormLink LIST leaf;
- inside a `ReplaceAll`'s `values=` on a FormLink LIST, each entry substituted in place;
- a flat compose FIELD, on a singular formlink field.

Everywhere else it is refused, because a token that slipped past pre-flight would throw `FormKey.Factory` at apply.
A compose spec riding alongside an admitted `@` value is refused for the same reason: this gate returns before the
compose branches, so the spec would be walked by apply's substitution recursion without ever having been validated.

A literal FormID mixed in beside siblings IS type-checked; a sibling is not, because its record does not exist yet.

## Pinned by

- *Over-arms search: agree in shape, or refuse by name*: `SameShapeAgreeProbe` (ci probe `sameshape-agree-guard`) —
  `float` and `float?` agree (`APerkEffect.Value`, check A, and the synthetic E2), every genuine difference still
  rejects (C, D, and E1 on the underlying CLR type), and apply takes what pre-flight admitted (Apply-1).
- *The FormLink target-type gate*: `FormLinkTargetTypeTests.ASingularLinkToTheWrongRecordTypeIsRefused`,
  `AListElementLinkToTheWrongRecordTypeIsRefused` and `AComposedStructFieldLinkToTheWrongRecordTypeIsRefused` — a
  link to a record of the wrong type is refused; `ALinkThatAcceptsAnyRecordIsNotRefused` — a link that accepts any
  record is never refused; `AnUnresolvableTargetIsNotTypeChecked` — an unresolvable FormID is not type-checked;
  `RemovingALinkByValueIsNotTypeChecked` — `Remove` is exempt (all in the same class).
- *The harvest pass and the checking pass are one walk*: `LinkHarvestSkipTests.ALinkFreeWriteGetsTheSameVerdictFromBothWalks`
  and `ALinkFreeRefusalIsTheSameSentenceFromBothWalks` — a write that contributed nothing to the sink keeps the
  harvest's verdict, which is the checking walk's; `AFormLinkWriteContributesItsValue` — a write that sets a link
  contributes; `ASameCallSiblingReferenceContributesToo` — a same-call `@editorid` goes into the sink (all in the
  same class).
- *`@editorid`: where a same-call reference is legal*: `FormLinkTargetTypeTests.ALiteralBesideASameCallSiblingIsTypeChecked`
  — a literal FormID beside a sibling is type-checked, the sibling is not.

## Where

`src/housecarl-core/CorpusRulebook.cs` holds the rulebook: `Validate`, `CollectLinkValues`, and the two derived
rulebooks `WithLinkHarvest` and `WithLinkTargets`. `src/housecarl-core/WriteEngine.cs` holds the apply path and the
recognisers the gate shares with it (`IsValidListIndexValue`, `IsValidFormLinkValue`, `IsFormLinkOrIndex`,
`TryRecognizeCtorArgs`, `TryRecognizeInstantiable`). No tool of its own: it is the pre-flight of the write lanes in
`src/housecarl-mcp/RecordWrites.cs`, reached through `housecarl_apply` and `housecarl_create`.
`src/housecarl-mcp/TypeLookup.cs` holds the corpus-backed type lookup (a `type=` string to its getter types)
that the read, check, write and asset lanes resolve types through. It is its own shared type, `TypeLookup`, whose map is built
from the corpus on the first resolution that needs it (an absent type set never builds it); the head holds one per
service and hands it to every area as `Types`.

## Related

- `docs/architecture/records-owned-child-declarers.md` — which fields the schema classifies as owned-child
  declarers.
- `docs/architecture/tool-schema-publication.md` — how the generated schema reaches the published tool surface.

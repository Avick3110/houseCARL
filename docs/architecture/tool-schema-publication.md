---
updated: 2026-09-23
covers: [src/housecarl-mcp/ToolSchemas.cs, src/housecarl-mcp/NestedSchemaConstraints.cs, src/housecarl-mcp/SchemaDepthCap.cs, src/housecarl-mcp/Program.cs]
---
# Tool schema publication

## What it is

houseCARL's MCP tools are discovered by an assembly scan, and the SDK generates each tool's
`inputSchema` from its C# method signature. Three things that generator cannot get right on its
own are corrected once, at registration, before anything is served, and a fourth pass cuts the
result to a configured nesting depth when one is set. All four change only what is
**published**. The argument-binding shim does read a published schema, but only root members and each
parameter's own `type` — never the nested part these passes rewrite (the full list is under the depth
cut below, where it is the premise of the floor) — and the composed payloads are then
read by `ListParams.Read<T>`, which consults no schema and is stricter than the SDK binder.

## Contracts

### Why the rewrite happens at registration

The SDK's `WithToolsFromAssembly` has no overload carrying
`McpServerToolCreateOptions.SchemaCreateOptions`, so the per-node `TransformSchemaNode` hook
cannot reach an assembly-scanned tool. `Tool.InputSchema` is settable (and validates what it is
given), so the schema is rewritten instead — as a post-configure over `McpServerOptions`, which
is the one place the final tool collection exists whichever transport built the host. The
assembly scan registers each tool as a *factory*, so no instance exists while the service
collection is being built, and stdio and HTTP build their hosts separately: a post-configure
keeps this on one line inside the shared registration rather than a call site per transport
that could drift apart.

### Pass 1 — the `@file` union

A list-valued input accepts either an inline array of objects or the string
`"@<absolute path>"` (SPEC §5.1). C# has no type for that union, so the parameter is declared
`JsonElement` — and a schema generated from the declared type says "anything" (`{}`). The
element shape then lives only in the tool description, where a client's schema rendering cannot
use it.

So those parameters are republished as `anyOf[<the generated element-array schema>, string]`.
The array arm is **generated from the C# element type** by the same generator the SDK uses, so
adding a member to `ApplyOp` updates the published schema automatically — only the union
wrapper and the string arm are written by hand. The parameter's `[Description]` moves onto the
union node, where a client renders it.

A generated sub-schema is a standalone document: every `"#/..."` pointer inside it is relative
to *its own* root. Nesting it under `properties/<param>/anyOf/0` breaks all of them unless they
are rebased first, and its `$defs` must be hoisted to the tool schema's root for `#/$defs/…` to
resolve at all.

The hoist is **first-wins by name**, sound only while two distinct types cannot produce the same short
name here. Today's rows share literal C# types (`StructInput`, `NestedSet`), so a duplicate name is a
duplicate schema. Key the hoist by type if that stops holding.

### Pass 1b — the shape union

A parameter declared `JsonElement` so it can **bind** more than one wire shape publishes untyped, and
`ToolCallShim` — which judges off the published type — then lets every shape through to the binder.
`ToolSchemas.ShapeUnionParams` stamps such a parameter's published `type` with the JSON kinds it accepts,
plus `null` (every one of them is optional), so a shape the tool means to refuse reaches the tool and is
refused in the tool's own words rather than by the shim's generic type-mismatch sentence.
`housecarl_skse`'s `findings` is the one row: the tool takes one family and says so, while the array shape
is the `housecarl_check` habit, so it must bind and be answered by the tool rather than intercepted.

### Pass 2 — no `$ref` in a published schema

The schema generator does not expand a recursive type. It inlines it once and terminates the
second occurrence with a **positional back-reference** — `$ref: "#/properties/ops/anyOf/0/…"`
pointing at an ancestor. houseCARL's write DTOs are recursive by design (`StructInput` → the
`sets[]` element → its `compose` → `StructInput` again), so five tools published a cyclic
schema.

That is legal JSON Schema, and the Anthropic and OpenAI APIs accept it. A growing set of
smaller and relay-hosted models validate `tools/list` conservatively and reject recursion
outright — and the rejection takes down the **whole server**, not the offending tool, with an
error that does not name houseCARL (issue #451).

So every same-document pointer is inlined. A cycle is expanded a bounded number of times
(`MaxSelfExpansions`) and then closed with an open node: the target's `type`, the parameter's
own description, and a clause saying nesting continues below that level. Nothing is narrowed —
the open node accepts what the recursive form accepted, and the binder never consulted the
schema in the first place. `$defs` is dropped once nothing refers to it, because an unreferenced
definition still carries its cycle to a validator that walks definitions.

`FlattenRefs` normalizes what **this** SDK's schema generator emits. It is not a general JSON
Schema `$ref` implementation and must not be reused as one: it reads a `$ref` as a JSON pointer
wherever one appears, which holds for generator output and not for JSON Schema at large. Plain-name
`$anchor` fragments, percent-encoded and empty reference tokens, boolean schemas as a pointer
target, a `$ref`-shaped value under `default`/`enum`, and 2020-12's rule that `$ref` siblings apply
*in addition* to the target (this merge lets them override) are all outside what it handles. Widen the
handling before widening the input.

A `$ref` the pass does **not** handle is left exactly as it is — a pointer that resolves
nowhere, and equally a form that is not a same-document pointer at all (a plain-name anchor, a
`$ref` that is not even a JSON string). Replacing one with an open node would hide a broken
rebase behind a schema that looks finished, and reading one as a string would throw out of the
`PostConfigure` these passes run in and fail the server's whole start.

Left in place, it fails the one invariant the guard asserts — **no published tool schema
carries a `$ref` member, in any spelling.** That predicate is deliberately wider than this
pass's own resolve gate and shares no code with it: an earlier version of the arm spelled the
detector the way the flattener spells its gate, so a `$ref` the pass could not resolve was also
one the detector could not see, and an anchor-form `{"$ref":"Node"}` injected into all 51
schemas passed green. A detector that inherits its subject's blind spot measures nothing at the
only moment it matters.

The bound is a cost/legibility trade, not a correctness one: raising it deepens every recursive
branch of every affected schema (at 1, the five affected tools grew ~3 KB each).

### Pass 3 — `required` and `enum` inside a parameter

The generator reads requiredness and closed value sets off the C# **method signature**, so it
gets them right for a tool's own parameters and says nothing at all about the members inside
one. A nested object published only a type: `create.records[].editorid` was described as
"REQUIRED." in prose while the schema did not require it, and `apply.ops[].op`'s eight verbs
were named in a sentence and published as a bare string. A client could not check a nested call
before sending it, and the verb list had two homes — the table the gate reads, and the
description text.

So `NestedSchemaConstraints` walks each parameter's CLR type against the schema published for it
and stamps what the shape declares: `required` for a member marked `[SchemaRequired]` — the ones
the server refuses a call without — and `enum` for a member marked `[SchemaValues]`, whose values
come from the table the gate itself validates against (`WriteVerbs.All`, and `WriteVerbs.OnCreate`
for the surface that refuses the transplanting verb by name). The marks live on the member, not in
a path list, because a shape is reached from several parameters — `compose` from four — and the
recursion bound expands some of them twice.

The stamp is **additive**. `required` unions with whatever the generator already published for that
node — assigning over it would silently stop requiring a non-nullable member the binder still
requires — and an `enum` on a member the generator typed as nullable carries `null` alongside the
names, because JSON Schema applies `enum` to every instance and the server reads a null verb as
"none given" and defaults it to `Set`. Without that entry the published schema would refuse, a hop
earlier, a call the tool answers. (A blank verb defaults the same way and is not in the enum: an
enum cannot state "any whitespace-only string", and no description offers that spelling.)

The one **narrowing** is a required member's own `type`, which loses its null arm: the generator types
every `string?` member as `["string","null"]`, and a member the server refuses the call without is not
one an explicit null satisfies. The drop reads the **document**, in both spellings the generator uses —
a `type` array and an `anyOf` union whose arms carry their own types — so every marked member loses the
arm without anything in the pass naming one. A shape that is null and nothing else is left alone, since
narrowing it would publish a member no value can satisfy. It is ordered before the `enum` stamp so both
read the same type.

It runs **after** the flatten, so every expanded copy of a shape carries the same stamps its first
occurrence does, and the walk is bounded by the schema rather than the type: it descends only where
the published document still spells a shape out, so the open node that closes a recursive chain
still constrains nothing.

A closed set that exists only as prose is left alone. `walk.exclusions[].severity`,
`walk.direction`, `project.form` and `assets[].kind` are each decided by a literal pattern at the
call site, not by a collection anything else can read; publishing an enum for one would be
inventing a second home rather than exposing the first. `compose.sets[].verb` is a narrower case
of the same thing: its description names five verbs while the request it builds is validated by
the same rulebook switch an op is, which accepts all eight — so nothing backs the five.

### Pass 4 — the optional depth cut (`HOUSECARL_MAX_SCHEMA_DEPTH`)

**Unset — the default —
it does nothing, and the published schemas are byte for byte what the three passes above leave.** The
variable is spelled the way houseCARL's others are (`HOUSECARL_DATA_DIR`, and the installer's
`HOUSECARL_SETUP_HOME`).

Pass 2 removed the recursion but inlined the compose chain to its full depth, so `housecarl_create` and
`housecarl_apply` publish at 24 and 21 levels. A provider that enforces a nesting cap — 10, in the
report — then refuses the **whole server** at `tools/list`, naming no tool: the same user-facing shape
as the `$ref` case, and the same posture applies (issue #730).

Set the variable to that provider's cap and every published schema is cut there. Depth is **raw JSON
container nesting** — the schema object is level 1, every object or array below it one more, whatever
it holds — because that is the measure the refusing providers report and the one the issue measured
with. A branch that cannot be spelled out that shallow is replaced by the node pass 2 already closes a
recursion with — the node's own `type` and description — under a clause of its own. Nothing is narrowed,
and `tools/call` is untouched at every accepted cap: the binder consults no schema, and the floor below
keeps the two members the shim does read, so a call nested deeper than the cut is bound and answered
exactly as before. The schema is less descriptive below the cut, never wrong.

`properties`, `patternProperties` and `$defs` are name-to-schema **dictionaries, not schemas**. The cut
recurses into their values and never replaces the container: a terminator in place of a `properties`
object turns it into a property named `type` and a property named `description`, and the document stops
validating — which a strict provider reports as an `anyOf` failure rather than as a depth error.
(`additionalProperties` *is* a schema, and is cut as one.)

The two terminators are the same NODE under two clauses, one constant each. The bound's says the shape is
"shown above", which is there because a cycle repeated it; below a cut the shape is in no part of the
document, so the cut's says nesting continues and is accepted but is **not spelled out in this
document**, naming the depth and the variable that cut it. A reader sent looking for a shape that is not
there invents one. What the node claims is identical either way.

**The floor is 4, and a value below it is refused.** `ToolCallShim` reads four things of a published
schema and nothing else: the top-level `properties`, each parameter's `type`, the root's own `required`
list, and the root's `additionalProperties` (the free-form opt-out that switches the alias and
undeclared-key passes off). The three root members all sit at level 2, so the floor is set by the
deepest of them: a schema's root is level 1, its `properties` dictionary level 2, a parameter level 3,
and that parameter's `type` list
(`["array","null"]`, the spelling most of them carry) level 4. So a cap of 1 or 2 closes the ROOT and
takes `properties` with it, and a cap of 3 closes each PARAMETER with a node that has no room for the
type list — either way argument coercion, the named missing-parameter refusal, the typed-mismatch
refusal, the undeclared-key refusal and the in-place filename refusal quietly stop happening. At 4 every
parameter keeps its type and only the shapes below a parameter are cut. A cut may say less about a
nested shape; it may not change what a call gets back.

A value that is not a whole number of 4 or more refuses the server's start, in one sentence on stderr
naming the variable. Ignoring it would boot a server publishing the schemas the caller set the variable
to get away from, under a provider error naming neither houseCARL nor the variable. Two more refusals
have the same shape and the same reason: a member at a cut point that the pass has no rule for (these
schemas are generated from a closed set of shapes, so one is drift to report, not a case to widen at a
user's server start), and a finished document still measuring over the cap — `Cut` re-measures rather
than trusting the induction over its own branches, because an over-depth schema is refused by the very
provider the cap was set for.

The cap applies to the server entry, not to a model, so every model behind that entry gets the cut
schemas; two entries split them.

## Pinned by

- *Pass 1 — the `@file` union*: `PublishedSchemaShapeTests.EveryFileListUnionPublishesAnyOfGeneratedArrayOrString`
  and `EveryFileListUnionsArrayArmCarriesItsGeneratedElementMembers` — the union, with the array arm generated from
  the C# element type.
- *Pass 2 — no `$ref` in a published schema*: `PublishedSchemaShapeTests.NoPublishedToolSchemaCarriesARefMemberInAnySpelling`
  — the invariant, with a predicate wider than the pass's own gate; `EveryRecursiveSiteExpandsExactlyOneLevelBeforeClosing`
  and `EveryRecursiveSiteClosesOnAnOpenNodeSayingNestingContinues` in the same class — a cycle is expanded a bounded
  number of times and closed with an open node.
- *Pass 2 — no `$ref` in a published schema*: the emission grammar it depends on is asserted by
  `schema-flatten-guard` (`SchemaFlattenProbe`, arm 7), so a generator that drifts on an SDK bump reddens there rather
  than at a user's server start. The same probe: `$defs` is dropped once nothing refers to it (arm 2); a `$ref` the
  pass does not handle is left as it is, and a non-string one does not throw (arm 5).
- *Pass 3 — `required` and `enum` inside a parameter*:
  `PublishedNestedConstraintTests.EveryPublishedOccurrenceOfAMarkedShapeCarriesItsRequiredAndEnum` — every expanded
  copy of a marked shape carries its `required` and `enum`, and a member whose type admits null carries `null` in its
  `enum`; `EveryMemberWithAClosedValueSetPublishesATypeAdmittingNull` — a closed-set member's published type admits
  null; `NoRequiredMemberPublishesANullableType` — a required member's type loses its null arm (all in the same
  class).
- *Pass 4 — the optional depth cut*: `src/housecarl-mcp/SchemaDepthCap.cs`, pinned by `PublishedSchemaDepthTests`:
  `WithTheVariableUnsetTheCutChangesNoPublishedSchemaByAByte` — unset, it does nothing;
  `EveryPublishedSchemaFitsTheConfiguredDepth` — every schema is cut to the cap;
  `EveryCutSchemaIsStillAValidSchemaDocument` — a dictionary is never replaced by a terminator;
  `ACutNodeSaysTheShapeIsNotInThisDocumentAndTheBoundsWordingIsGone` — the cut's own clause;
  `ACallNestedDeeperThanTheCutStillReachesTheToolBody` — `tools/call` is untouched;
  `AtTheShallowestCapEveryParameterStillPublishesTheTypeItPublishedUncut` and
  `AtTheShallowestCapAMissingRequiredParameterIsStillRefusedByName` — the floor of 4;
  `AnInvalidDepthIsRefusedAtStartupInOneSentence` — a value below the floor refuses the server's start.

## Where

`src/housecarl-mcp/ToolSchemas.cs` holds passes 1, 1b and 2 (`FileListParams`, `ShapeUnionParams`, `FlattenRefs`,
`MaxSelfExpansions`) and `PublishSchemas`, which runs them; `src/housecarl-mcp/NestedSchemaConstraints.cs` is pass 3;
`src/housecarl-mcp/SchemaDepthCap.cs` is pass 4; `src/housecarl-mcp/Program.cs` registers the passes as the
post-configure over the shared tool registration. Entry points: `tools/list`, and the `HOUSECARL_MAX_SCHEMA_DEPTH`
variable.

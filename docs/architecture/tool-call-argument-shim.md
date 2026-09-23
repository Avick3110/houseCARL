---
updated: 2026-09-23
covers: [src/housecarl-mcp/ToolCallShim.cs, src/housecarl-mcp/Program.cs, src/housecarl-mcp/Guard.cs]
---
# The tool-call argument shim

## What it is

**Class:** LIVING. Subsystem: the files in `covers:` above. Pinned by `ToolCallShimCoercionTests` and `ToolCallShimWirePathTests` in
`src/housecarl-mcp-tests`.

The shim is a call-tool filter that runs **before** the SDK binds a call's JSON arguments to the tool
method's parameters. Without it a malformed argument shape throws inside SDK binding and the SDK
genericizes it to "An error occurred invoking '\<tool\>'." — an opaque dead end a caller cannot
self-correct from. Every pass is driven off the tool's own published `InputSchema`, so current and future
parameters are covered without per-tool wiring.

## Contracts

### Pass order, and why it is an order

1. **Retired tool name.** `MatchedPrimitive` is resolved by the SDK before filters run, so this check
   only ever sees a name the server does not register; `AliasTable` answers it with its successor call
   shape.
2. **`ResolveAliases`** renames an argument keyed by an underscore/case variant of a declared parameter
   onto the canonical spelling, on exactly one declared match. Only a key the schema does **not** declare
   is considered, and an explicitly supplied canonical is never clobbered, so a well-formed call is
   byte-identical. It is deliberately not kind-gated: it names the right parameter, so the rename
   proceeds even for an unbindable value and the type pass names the real fault. Skipped when a schema
   opts into free-form args, as pass 5 is: there an undeclared key may be intentional data, and
   rewriting it would destroy it. It must run **before** coercion, so the renamed value is still
   shape-coerced.
3. **`CoerceObviousShapes`** rewrites a value whose JSON kind mismatches the declared type but whose
   intent is unambiguous — a bare string where an array is declared, a string-encoded JSON array, a
   quoted bool or number, a number where only a string is declared. It only ever replaces values for keys
   the schema declares. A parameter declaring both `string` and `array` takes the string as it stands:
   wrapping it would hand the tool the very shape a caller spelled correctly as a scalar.
4. **`MissingRequired`** refuses a schema-required parameter that is absent. An explicit JSON `null`
   counts as missing unless the schema declares null legal, because the SDK binds it and the tool body
   then null-references into a misleading internal-failure message. When the same call also carries an
   undeclared key, the refusal names that key and the accepted list too — this pass returns first, so the
   pass that would otherwise report them does not run.
5. **`UnknownParameters`** refuses an undeclared argument, listing the offenders and the tool's supported
   parameters. Without it the SDK binder silently ignores an undeclared argument, so the call runs with
   that intent dropped and nothing tells the caller. Skipped when a schema opts into free-form args. It
   must run **after** coercion, which only rewrites declared keys.
6. **`InPlaceNamesAFile`** refuses an `in_place=` spelling a boolean, quoted or bare. The parameter takes
   the filename being overwritten, so `"true"`/`"false"` is not a file — and a quoted one satisfies both
   the schema type and the tool body's non-empty check, so the call would otherwise enter the opt-in
   overwrite lane and fail as though overwriting a plugin named `"false"`. The bare spelling is caught
   here too, so one sentence answers both rather than a type error steering the caller into the quoted
   one.
7. **`TypeMismatches`** refuses a declared argument whose JSON kind cannot bind, naming each offender,
   its expected types and the kind received; the SDK binder would otherwise throw a `JsonException`
   carrying a byte offset and no parameter name. It judges only keys declared with a concrete type:
   untyped properties are left for binding, unknown keys belong to pass 5 and an explicit null to pass 4.

No pass maps a 1.x **parameter** name onto a 2.0 one. That table was deleted at 2.0.0 (SPEC §5.4
amendment 2026-09-06), so an old spelling is refused by name like any other unknown parameter. The
retired **tool**-name rows are not scaffolding and have no removal date: nothing is accepted, redirected
or executed there — the call is refused with one sentence naming its 2.0 successor, where the caller
would otherwise get the SDK's bare "Unknown tool" and no way forward.

### What the shim reads of a schema

Stated once, in `docs/architecture/tool-schema-publication.md`, where it is the premise of the depth
cut's floor of 4 — read it there before changing what any pass here reads.

### Named failure, end to end

The passes run inside the same `try` as the call, so a throw from coercion or a refusal pass also comes
back named rather than as the SDK generic. A real request cancellation stays the SDK's, and so does
`McpException` (the protocol surface); an `OperationCanceledException` with a live request token — an
internal HttpClient timeout, say — is not a cancellation and is named here. The full stack goes to
stderr, the MCP log, never to stdout, which is the protocol channel.

`Guard` is the same rule one layer in: every MCP tool body runs inside `Guard.Tool`, so an unconverted
exception returns a named error instead of escaping to the SDK. Every body must stay wrapped — that is
what keeps `Guard`'s "the arguments bound fine" wording true and leaves only pre-body binding failures to
the shim.

## Pinned by

- *Pass order*, pass 3: `ToolCallShimCoercionTests` — what `CoerceObviousShapes` produces, as a value: a bare string
  for an array, a string spelling a real JSON array, a quoted bool or number, and a value of the declared type left
  alone.
- *Pass order*, pass 4: `ToolCallShimWirePathTests.EveryToolWithARequiredParameterRefusesAnEmptyCallNamingEveryMissingParameter`
  and `AnExplicitNullForARequiredParameterIsRefusedAsMissingAndSaysItWasNull` in the same class — the named
  missing-parameter refusal, an explicit null counted as missing.
- *Pass order*, pass 5 and the 1.x parameter names: `ToolCallShimWirePathTests.CreatePluginTakesPatchAndRefusesTheOldPluginSpellingByName`,
  `CompactPluginTakesSourceAndRefusesTheOldPluginSpellingByName` and
  `AStrayTargetOnCompactPluginIsANamedUnknown_NotAnInPlaceTypeError` in the same class — an old or undeclared
  parameter is refused by name with the supported list, never mapped.

## Where

`src/housecarl-mcp/ToolCallShim.cs` holds the passes, run from `ToolCallShim.LenientArguments`;
`src/housecarl-mcp/Program.cs` registers it as the call-tool filter; `src/housecarl-mcp/Guard.cs` is `Guard.Tool`,
which wraps every tool body. No tool of its own: it runs on every `tools/call`.

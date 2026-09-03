# Tool schema publication

**Class:** LIVING. Subsystem: `src/housecarl-mcp/ToolSchemas.cs`, registered from `Program.cs`.
Pinned by `PublishedSchemaShapeTests` in `src/housecarl-mcp-tests` (the real published surface) and
`schema-flatten-guard` (the flattening mechanism, over synthetic documents for the shapes the
real surface cannot produce, and over the real pre-flatten surface for the emission grammar and
the strict reader).

houseCARL's MCP tools are discovered by an assembly scan, and the SDK generates each tool's
`inputSchema` from its C# method signature. Two things that generator cannot get right on its
own are corrected once, at registration, before anything is served. Both change only what is
**published**. The argument-binding shim does read a published schema, but only its top-level
`properties` — never the nested part these passes rewrite — and the composed payloads are then
read by `ListParams.Read<T>`, which consults no schema and is stricter than the SDK binder.

## Why the rewrite happens at registration

The SDK's `WithToolsFromAssembly` has no overload carrying
`McpServerToolCreateOptions.SchemaCreateOptions`, so the per-node `TransformSchemaNode` hook
cannot reach an assembly-scanned tool. `Tool.InputSchema` is settable (and validates what it is
given), so the schema is rewritten instead — as a post-configure over `McpServerOptions`, which
is the one place the final tool collection exists whichever transport built the host. The
assembly scan registers each tool as a *factory*, so no instance exists while the service
collection is being built, and stdio and HTTP build their hosts separately: a post-configure
keeps this on one line inside the shared registration rather than a call site per transport
that could drift apart.

## Pass 1 — the `@file` union

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

## Pass 2 — no `$ref` in a published schema

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

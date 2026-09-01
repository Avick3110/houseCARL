# ADR 0002 — Published tool schemas carry no `$ref`

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-09-01 · **Status:** accepted · **Issue:** #451

## Context

houseCARL's write DTOs are recursive by design: a composed struct carries nested edits, and a
nested edit may compose another struct. The MCP SDK's schema generator does not expand a
recursive type — it inlines one level and terminates the next with a positional back-reference
(`$ref` at an ancestor), so five tools published a cyclic `inputSchema`.

That is legal JSON Schema. The Anthropic and OpenAI APIs accept it, and houseCARL shipped it
for months. But an external report (#451) measured a provider that validates `tools/list`
strictly and answers `Recursive JSON schemas are not currently supported` — refusing the
**entire server**, not the offending tool, with an error naming neither houseCARL nor the
schema. The user's only workaround was toggling the MCP server off for that model.

The set of clients between a model and a tool list is not ours and keeps growing. A schema
feature that some validators reject is a liability regardless of who is technically correct.

## Decision

**No published tool schema carries a `$ref` member, in any spelling.** That is wider than
"contains a same-document pointer" on purpose: spelling the invariant the same way as the
flattener's own resolve gate is what made an earlier version of the guard vacuous — a `$ref`
the pass could not resolve was also one the detector could not see, so an anchor-form
`{"$ref":"Node"}` injected into all 51 schemas passed green. The guard asks only whether the
member is there.

At registration, after the `@file` union rewrite, every same-document pointer is inlined:

1. A cycle is expanded a bounded number of times and then **closed with an open node** — the
   target's `type`, the parameter's own description, and a clause saying nesting continues.
   Nothing is narrowed: the schema at the bound accepts what the recursive form accepted, and
   nothing reads the nested part of a published schema, so rewriting it cannot move what a
   tool accepts (see the Consequences below for what does read one).
2. `$defs` is dropped once nothing refers to it — an unreferenced definition still carries its
   cycle to a validator that walks definitions.
3. A `$ref` the pass does **not** handle — a pointer that does not resolve, a form that is not
   a same-document pointer at all — is left in place, not replaced by an open node and not
   thrown on. A broken rebase must stay visible rather than hide behind a schema that looks
   finished, and the wider invariant above is what makes leaving it a report rather than a
   silence.
4. This is unconditional, not a config switch. The flattened form is accepted everywhere the
   recursive form is, so a switch would add a knob with no reachable benefit, leave the default
   broken for the users who hit this, and double what the guards must hold.

## Consequences

- The recursive shape stays legible to a caller — it is spelled out concretely to the bound
  rather than amputated — at the cost of schema size. The five affected tools grew ~3 KB each
  (~6% of `tools/list`). Raising the bound deepens every recursive branch of every schema.
- The invariant is guarded generically over all tools, so a future recursive DTO is covered
  by construction rather than by remembering this decision.
- Anything that consumes a published schema may now assume it is self-contained. One thing
  does: the argument-binding shim is schema-driven off `InputSchema`, coercing argument shapes
  and refusing unknown or missing parameters. It reads only the top-level `properties` and
  never descends into `items`/`anyOf`, which is the whole of what this pass rewrites — so the
  flattening is invisible to it. That is a measured property of today's shim, not a rule
  binding it: a future consumer that walks nested schemas is now free to, and would have been
  reading a cyclic document before.
- Should a provider one day reject something else in these schemas, the fix has a home: this
  is a publication layer that already normalizes what the generator emits.

Mechanics and the rest of the layer: `docs/architecture/tool-schema-publication.md`.

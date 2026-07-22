# AGENTS.md — houseCARL

Guidance for AI coding agents (Codex, and others that read `AGENTS.md`) working in this
repository. houseCARL's deep operating manual is **[`CLAUDE.md`](CLAUDE.md)** — read it first;
this file is the agent-agnostic companion, focused on a review checklist for pull requests.
For what houseCARL *is* and how to build it, see **[`README.md`](README.md)**.

> **Public-repo note.** `CLAUDE.md` references a private `dev/` working corpus (PRFAQ, plans,
> session handoffs) that is **gitignored — not part of this repository**, so links into `dev/`
> will not resolve in a clone. The code, standards, and skills are all here; the working notes
> are not.

## The cornerstones — read before touching coverage or the bulk surface

houseCARL's load-bearing claim is **comprehensive, data-layer access** to the Skyrim load
order. For records that means: **every record type Mutagen models is readable and writable — by
construction, not by hand.** A build-time generator walks Mutagen's record interfaces and emits
the schema + write-validation automatically, so houseCARL's coverage *is* Mutagen's coverage.

Both prior builds hand-wired a schema and a write-translation per record type and never
finished; this rebuild exists to end that. So:

- **Never** hand-map a record type or reintroduce per-record-type write translations.
- **Never** ship "just the common record types" — full coverage is not a scope choice.
- A record type that's **absent** is a real upstream gap (Mutagen's delta vs xEdit) to
  **surface loudly**, never to guess around or silently skip.

The same principle governs *operations* (**composition by construction**, elevated
2026-07-22): bulk capability is the closure of a small, bounded, individually-guarded set of
composition primitives — never a verb per job. So:

- **Never** ship a job-shaped bulk verb ("audit X", "copy the Y frame") — a bespoke verb is a
  bug report against the primitive set; add the missing primitive or file the gap.
- Domain knowledge (field bundles, forbidden prefixes) lives in **skills as data**, never in
  tool code — the one exception is a thin, typed, probe-pinned interpreter of *engine*
  semantics (the `effect_chain` posture).

If a change pressures any of the above, stop and raise it via the PRFAQ revalidation protocol
(`CLAUDE.md §4`) — don't work around it.

## Build & run

- **Full plugin build:** `./scripts/build-plugin.ps1` — regenerates the reflection rulebook,
  publishes the server (framework-dependent, **trimming off** — trimming would strip reflected
  types and silently lose coverage), bundles skills, packs the release zip. Needs the
  **.NET 9 SDK**, Windows, PowerShell.
- **Iterating on the generator/engine:** `dotnet build` then `dotnet run`. A
  `dotnet build && dotnet run` can serve a **stale** binary — run `dotnet clean` first when
  verifying a generator change, or the regenerated corpus won't reflect your edit.
- Runtime config is read from a user-config file beside the exe (not the blanked
  `appsettings.json`); the MO2 instance is set at runtime, never hard-coded.

## Conventions

- **MCP tools are `housecarl_<snake_case>`** — the prefix is locked (brand continuity); all
  names follow `standards/HOUSECARL_NAMING.md`.
- The brand string **"houseCARL"** lives in exactly one place in code (the server's config).
- **Atomic, focused commits** — one logical change per commit.
- **Writes are non-destructive by default** — every patch is a **new** MO2 mod folder; sources
  read-only. The one sanctioned exception is the **opt-in in-place lane** (`target=` +
  `in_place=true`, per-plugin consent, no backup), which rewrites an existing plugin only when the
  user explicitly asks.

## Review guidelines (for pull requests)

Reviewing a change to houseCARL — human's or agent's — check, in priority order:

1. **Cornerstones intact** — no hand-wired coverage, no per-record-type write map, no "subset for
   now"; coverage stays reflection-driven and complete by construction. And no job-shaped bulk
   verb — bulk capability composes from the guarded primitive set, with domain knowledge in
   skills as data, never a new one-off verb in tool code.
2. **No silent failure (Q3)** — no silently wrong answer, no silently degraded mode. A tool that
   can't do the thing says so plainly. A swallowed error or quiet fallback is a defect.
3. **No silent workarounds** — a stumbling block should have been *surfaced* (`CLAUDE.md §4`),
   not patched around in a way that trades away something that was supposed to hold.
4. **Writes stay non-destructive — or, in the in-place lane, safe-by-contract** — default writes go
   to a new plugin (originals untouched). The opt-in in-place lane (`target=` + `in_place=true`)
   deliberately rewrites an existing plugin and is sanctioned **only with its guards intact**: the
   explicit per-call flag, the persistent per-plugin consent handshake, the non-bypassable
   round-trip floor (verify the touched records, trust Mutagen for the rest), and the
   `editedInPlace` marker (never houseCARL-owned). Either way, masters are derived from the FormIDs
   actually referenced (plus the Skyrim.esm + Update.esm baseline every plugin carries, as the
   Creation Kit does), never hand-specified.
5. **Atomic & focused** — one logical change; the diff does what the message says, nothing extra.
6. **Correctness over performance** — where they conflict, correctness wins; perf concerns are
   raised factually, not as blockers.

Be concrete and candid: name the file and line, say what's wrong and why, and prefer surfacing a
real doubt over a tidy approval.

# houseCARL — project foundation & operating rules

*houseCARL is the reflection-driven Mutagen rebuild of Housecarl. This file is the first thing every session reads.*

> **Public-repo note.** This is houseCARL's internal operating manual — the working contract between the
> maintainer and the AI sessions that build it. It references a `dev/` working corpus (PRFAQ, plans,
> session handoffs) that is kept **private and is not part of the public repository**, so links into
> `dev/` below will not resolve in a clone. To *use* houseCARL, start with [README.md](README.md).

---

## 1. What houseCARL is

houseCARL gives Claude comprehensive, direct access to the Skyrim Special Edition modding workspace — every plugin record across the load order, every Papyrus script, every runtime and crash log, every asset path, and the live Mod Organizer 2 modlist state — at the **data layer**, beneath the human-interface tools (xEdit, CK, Synthesis). The modder works in plain English inside Claude Code; Claude does the mechanical work and, by default, writes results into a *new* plugin the modder reviews and enables in MO2 (originals untouched). **Comprehensive access is the load-bearing capability** — conflict resolution, patch authoring, mod creation, Papyrus triage, mod auditing, and crash diagnosis all emerge from access quality, not from features built one at a time. Full product framing lives in the PRFAQ corpus (`dev/PRFAQ/`).

Mechanically, houseCARL is a single C# process running an MCP server, with Mutagen — the Bethesda-format library — kept warm in memory for both reading and writing. It is **reflection-driven**: a build-time generator walks Mutagen's record interfaces and emits the schema + validation data automatically, so the set of record types houseCARL understands *is* the set Mutagen models — by construction, not by hand. Reads use Mutagen's binary overlay (lazy — records parse on access; the load order isn't held fully in memory); writes use a small set of generic op verbs (Set / Add / Remove / ReplaceAll / SetAtIndex / Merge) through that same reflection layer. Freshness comes from cheap mtime re-checks, not a process that live-tracks MO2.

houseCARL is a fresh start — no shared git history with the project formerly at `Housecarl/` (now `Housecarl [Legacy]/`, dormant but readable). Two failures drove the rebuild:

- **Coverage was hand-wired and never finished** — a schema and a write-translation per record type (134 schemas; 202 write-maps, 60 still placeholders). §3.
- **The old build ran a persistent daemon** that held full parsed state hot and deeply live-tracked MO2 — which exploded RAM usage and bred constant complexity working around MO2's file locking.

houseCARL answers both: reflection makes coverage complete by construction, and a single process with lazy overlay + mtime freshness replaces the hot daemon — no hot parsed state and no plugin file handles held at rest, so MO2 / xEdit can move or delete plugins freely.

---

## 2. Read in this order

This file is **how we operate** (stable). The latest session handoff is **where we are** (tactical). The PRFAQ corpus is **why** (foundational reference). Keep them in their lanes — don't pad this file with tactical state as insurance against a skipped handoff; that's how a CLAUDE.md bloats.

A session picking up work reads:

1. **This file** — how houseCARL works and how we operate.
2. **The latest handoff** in `dev/session-handoffs/` — what the last session did and what to pick up. The most important transition read; start here for "where are we."
3. **The PRFAQ corpus** (`dev/PRFAQ/`) — read once when new to the project, then consult on demand (it's ~60k tokens — not a per-session read). Product: P1 + P2. Direction: P7 + rebuild plan v1. Proof: spike findings (code at `dev/references/spike/`). Deeper, on demand: FAQs (P3/P4), Housecarl-HEAD eval (P5), pivot doc (§5's source).

The cornerstone (§3) and revalidation protocol (§4) are restated in this file, so you operate correctly from CLAUDE.md alone — the corpus is the authority you re-read when the protocol sends you there, not a tax every session pays.

---

## 3. Cornerstone — full Mutagen coverage, by construction

The PRFAQ's load-bearing claim is **comprehensive access** (§1). For records, that means **every record type Mutagen models is readable and writable — by construction, not by hand.**

This is the reason for the rebuild. Both prior builds hand-wired coverage: a schema per record type, a write-translation per record type (134 schemas; 202 write-mappings, 60 of them still placeholders). That wiring gap meant "comprehensive write access" was always one more hand-port from done. The reflection-driven generator closes it structurally — coverage *is* Mutagen's coverage, and Mutagen's delta vs xEdit is a known upstream surface we fail loud about, never silently around.

**Full coverage is not a scope choice.** If a stumbling block ever frames it as "smaller scope for v1" or "just the common record types," that's a cornerstone violation, not a pragmatic trim — invoke the protocol (§4). Per-record-type hand-mapping does **not** come back.

---

## 4. PRFAQ revalidation protocol

Aaron-named, and the single most important behavior in this file:

> "At all times we have to validate against the PRFAQ as we discover stumbling blocks. I am tired of getting 2 weeks into dev and claude pushing through and finding a work around that compromises everything."

When you hit a stumbling block, the default is **not** "find a workaround that unblocks." It is:

1. **STOP.** Don't reach for a workaround.
2. **Name which PRFAQ assumption the block challenges** — cite the Q-number, claim, or section.
3. **Re-read that section** — don't reason from memory.
4. **Surface to Aaron** with one of three framings:

| Outcome | Framing | What you do |
|---|---|---|
| (a) PRFAQ holds, clean solution exists | "§X assumes Y. The block resolves via Z, which respects Y because…" | Proceed with Z after surfacing |
| (b) PRFAQ assumption wrong, goal stands | "§X assumed Y. Reality says Y is false. Revising to Y′ preserves the goal. Aaron-go on the revision?" | Wait for Aaron-go on the revision |
| (c) PRFAQ wrong AND no revision preserves the goal | "§X assumed Y. Y is false AND no revision preserves the original goal. This is an architecture decision." | Wait for Aaron's architectural decision |

**Never:** take (a) when it's really (b) or (c); quietly change behavior so a PRFAQ claim becomes false; or normalize a compromise as "good enough" without Aaron-go. The protocol costs minutes per block; silent workarounds cost the whole project — which is why this rebuild exists.

---

## 5. Operating principles

Carried from the retrospective pivot (full doc in the corpus). How a session behaves:

1. **Empirical-first** — nothing locks without Aaron's empirical confirmation. A plan reviewed is not a thing proven.
2. **Candor is cheap** — surface doubt (about a decision, a doc, the direction itself) without Aaron-go ceremony. Honest opinion is a first-class deliverable, not something you wait to be asked for.
3. **Guardrails are tools, not sacred** — locks, conventions, and this file itself are revisable when reality contradicts them. Propose the revisit; Aaron decides.
4. **Anti-bloat** — orientation surface stays small. A session shouldn't burn its budget on stale narrative. Prune aggressively; archive, don't accrete.
5. **Lanes — Aaron architects and picks the execution method; conductor proposes and drafts** — Aaron owns capability scope, trade-offs, architecture, *and* how we approach the work (sequencing, parallel-vs-serial, session shape, which method). Conductor proposes execution options with a clear recommendation, then handles the mechanical drafting, decomposition, and ordering once Aaron picks. Surface method choices for Aaron; don't silently pick them — but don't over-gate either: once he's chosen, or when a call is plainly mechanical, proceed decisively. Honest opinion stays first-class — recommend, don't just lay out a menu.
6. **Explicit uncertainty over performed certainty** — "here's what I think, why, what I don't know, and how we'd find out" beats a tidy option matrix implying false confidence.
7. **Q3 — no silent failure** — never a silent wrong answer, never a silently degraded mode. If a tool is compromised or you can't do the thing, say so plainly with what you checked and what to try next.
8. **Atomic, focused commits** — one logical change per commit.
9. **No silent workarounds** — §4 generalized to any decision that trades away something that was supposed to hold, PRFAQ or not.
10. **Worktree & merge discipline — start every change in a worktree; land on `main` only on Aaron's go.** Before any change that will commit, check your branch (`git branch --show-current`) and state it up front; if you're on `main`, create a worktree (`.claude/worktrees/<name>/`, branch `claude/<name>`) FIRST — solo sessions included, not just parallel ones. The main repo folder stays on `main`, read-only except for landing reviewed branches into. Commit freely on the worktree branch — local and reversible. Landing on `main` — via push → open PR → independent review → **Aaron's explicit go each time** → FF merge → delete branch — is a separate, outward-facing act, never automatic. The same gate covers any commit that edits this operating manual or other self-governing config: surface it, don't self-commit.

---

## 6. Naming

All names follow `standards/HOUSECARL_NAMING.md`. Load-bearing rule: MCP tools are `housecarl_<snake_case>` — the `housecarl_` prefix **carries forward** from the prior build (brand continuity; locked 2026-05-27), even though the project is now houseCARL. The brand string "houseCARL" lives in exactly one place in code (the MCP server's name/config), not scattered through it.

---

## 7. Skills

houseCARL's skills live at `.claude/skills/<slug>/` and ship bundled in the plugin (namespaced `/housecarl:<name>`). The current set spans three kinds. **Reference-lookup** — `mutagen-reference` (record schemas) and `papyrus-reference` (Papyrus + SKSE signatures), each generated **by construction** from an upstream corpus (Mutagen's record library; BellCube's papyrus-index) so neither is a hand-maintained subset. **Distributor-grammar authoring** — `skypatcher-authoring`, `spid-authoring`, and `kid-authoring` (with `cid-authoring` and a routing micro-skill still to come). **Performance review** — `papyrus-optimization`, an investigation-flow skill that grades a `.psc` broken / suboptimal / clean and names the fix; the cost-and-habits complement to `papyrus-reference`, and houseCARL's first community-contributed skill (DrHeisen).

The `modlist-authoring` cluster (`skill-authoring`, `modlist-authoring`, `knowledge-file-authoring`) was **removed when packaging moved to a plugin** — it was built on the retired "unpack houseCARL into the user's workspace and author content alongside it" model. In the plugin the shipped skill set is curated by us and read-only to users; anyone extending their *own* project uses Claude Code's native skill authoring. The authoring *methodology* survives in `standards/HOUSECARL_SKILL_AUTHORING.md` for building houseCARL's own skills.

Tool-surface skills (`esp-patching`, `mod-dissection`, `bsa-archives`, `crash-diagnostics`, …) are **not** imported — they get rewritten against the new tool surface once it ships. A skill pointing at tools that don't exist yet is worse than no skill. (`crash-diagnostics` is methodology-rich but its body is built around specific tools — empirically tool-coupled — so it waits with this set.)

**Building houseCARL itself:** use the builder skills rather than hand-rolling — the Anthropic `skill-creator` for new skills, paired with the `standards/HOUSECARL_SKILL_AUTHORING.md` methodology and `standards/HOUSECARL_NAMING.md`; the Anthropic `mcp-builder` skill for MCP-server work. (mcp-builder targets Python/Node — for our C# server it's useful for MCP *design* guidance, not code scaffolding; confirm at the MCP-server wave.)

---

## 8. What NOT to do

- **Don't reconstruct context from prior sessions.** Assume nothing; read the docs (§2). Memory supplements, it doesn't substitute.
- **Don't work on `main`.** Every change that will commit starts in a worktree (`.claude/worktrees/<name>/`); the main repo folder is read-only except for landing reviewed branches (§5 #10). Check your branch at the start — booting onto `main` and editing there is the recurring drift this rule exists to stop.
- **Don't treat coverage as a subset.** §3. Tempted to ship "the common record types"? Stop and read §4.
- **Don't silently work around a block.** §4. Surfacing costs minutes; the alternative is why we rebuilt.
- **Don't edit the foundation corpus (`dev/PRFAQ/`) or other ARCHIVE docs.** Immutable record of why decisions were made. New docs supersede; old ones stay as written (typo-fix excepted). Doc classes (LIVING vs ARCHIVE) are defined in `standards/HOUSECARL_DOC_HYGIENE.md`.
- **Don't re-import the legacy lock-down.** The old repo's heavy guardrails are part of what we left behind. New guardrails earn their place from real need.
- **Don't "improve" the legacy repo.** `Housecarl [Legacy]/` is frozen reference — read it, don't touch it.

# HOUSECARL_DOC_HYGIENE — doc hygiene standard (LIVING vs ARCHIVE)

*A houseCARL standard. Revisable when reality contradicts it (CLAUDE.md §5.3) — propose the revisit, Aaron decides.*

This standard defines the two-class document system houseCARL runs on. It is the fuller statement of the rule CLAUDE.md §8 already gestures at ("don't edit the foundation corpus or other ARCHIVE docs… new docs supersede; old ones stay as written"). Its purpose is to **prevent staleness structurally** rather than catch it in a post-hoc audit: a doc's class tells you, by construction, whether it must track current state or is frozen history.

It is a **convention, not an enforced gate.** houseCARL deliberately carries no pre-commit hook or mandatory ship-gate for this (guardrails earn their place from real need — CLAUDE.md §5). The `Class:` marker plus session discipline is the mechanism. If drift ever becomes a real, recurring problem, *that* is when an enforcement tool earns its place — propose it then.

---

## 1. The two classes

Every `.md` file in the repo is **LIVING** or **ARCHIVE**. No third class. A file that fits neither shouldn't exist in the repo.

**LIVING** — must always reflect current HEAD state. Updated **in the same commit** that changes the thing it describes (not in a later cleanup pass). If a LIVING doc contradicts the code, the doc is the bug.

**ARCHIVE** — a timestamped record of what was known/decided at its creation. **Immutable from first commit.** If its content is later wrong, the correction goes in a *new* doc (or a LIVING doc that supersedes it) — never in an edit to the archive. A session reading an archive must be able to trust it represents what was true at its date.

- **Typo-fix exception:** a typo that makes an ARCHIVE doc ambiguous or misleading may be corrected, flagged `[ARCHIVE typo-fix]` in the commit message, with no content added or removed.

---

## 2. The `Class:` marker

Each doc that could be ambiguous declares its class in a header line, e.g.:

```
**Class:** ARCHIVE (session handoff). Immutable from first commit.
**Class:** LIVING plan doc (worked against + updated until superseded; not ARCHIVE).
```

The marker is the load-bearing signal: it tells the next session, at a glance, whether the doc is current truth or frozen history. Carry it on every handoff and every plan; carry it on anything whose class a reader might otherwise guess wrong.

---

## 3. houseCARL's doc map

| Path / doc | Class | Notes |
|---|---|---|
| `CLAUDE.md` | LIVING | The operating doc — how houseCARL works + how we operate. Stays stable; points at the handoff folder for tactical state (never a session log). |
| `standards/HOUSECARL_*.md` | LIVING | Naming, skill-authoring, this doc. Standards evolve; revise when reality contradicts (§5.3). |
| `README.md`, `CHANGELOG.md` (when they ship) | LIVING | Consumer-facing install/capability overview + version-by-version narrative. Update in the commit that changes what they describe. |
| `docs/architecture/*` | LIVING | Public per-subsystem notes — what it is, how it's shaped, the contracts that hold it together (§8). |
| `docs/decisions/*` | ARCHIVE | Public ADRs — numbered, immutable, superseded by a later ADR rather than edited (§8). |
| `dev/plans/*` | **LIVING → ARCHIVE** | LIVING while actively worked against; becomes ARCHIVE when **superseded or closed** (see §4). The active plan declares `Class: LIVING`; a closed one is frozen. |
| `dev/session-handoffs/*` | ARCHIVE | Frozen from first commit. The latest is "where we are"; older ones are history. Never edited to reflect later state. |
| `dev/PRFAQ/*` | ARCHIVE | The immutable foundation corpus — why decisions were made (CLAUDE.md §8). New docs supersede; these never change. |
| `dev/review/*`, `dev/references/*` | ARCHIVE | Review snapshots + captured upstream corpora — frozen at capture. |

The persistent **memory** system (`memory/MEMORY.md` + `memory/*.md`) is governed separately — it's the cross-session memory store, not repo documentation, and is pruned/updated by its own rules.

---

## 4. LIVING → ARCHIVE transitions

A plan is LIVING while it drives in-flight work, then crosses to ARCHIVE when the work **closes or a new doc supersedes it**. At that point:

1. Commit the doc in its final state.
2. From the next commit on, it's frozen — the typo-fix exception aside.
3. If later work needs to revisit the topic, write a *new* LIVING doc (or update the relevant standard / handoff) that supersedes it. Don't reopen the archived one.

This is why "where are we" lives in the **latest handoff** (ARCHIVE, one per session) and "how we operate" lives in **CLAUDE.md + standards** (LIVING): the two never compete to be the current-state authority.

---

## 5. The one rule that prevents the most staleness

**Supersede, don't edit; and update LIVING docs in the same commit as the change.** Most doc rot comes from two moves this standard forbids: editing an archive to "fix" history (which makes the record untrustworthy), and deferring a LIVING-doc update to "later" (which never comes). A change that retires a tool, renames a skill, or moves a path updates its LIVING docs *now*, in the same commit — or it isn't done.

---

## 6. One home per fact

**State a fact where it's authoritative; everywhere else, point.** A sentence that paraphrases another doc's fact is a future stale sentence — §5's same-commit discipline can only lose to a structure that multiplies the copies it has to chase. Most cross-doc rot is a paraphrase quietly outliving the fact it copied.

Concretely:

- **Pointer docs (a STATUS, an index) carry zero state predicates** — no dates, PR numbers, "done"s, or summaries of the docs they route to. If a sentence in a pointer doc can rot, the sentence is the bug: replace it with a link to the fact's home.
- **Each kind of fact gets exactly one home** — tactical state in the newest handoff, standing order/plan in its LIVING doc, contract text and its amendments in the contract's own header, and so on. A fact with no good home gets a home, never a copy in each doc that wants it.
- Before writing a sentence that restates another doc's fact, link instead.

Adopted 2026-08-16 from the tool-surface-2.0 doc-space audit: the sub-project's STATUS paraphrased the handoffs, charter, and SPEC it pointed at, and every paraphrase eventually contradicted its source. This is the same one-source-per-sentence rule PR #337 applied to code renders, applied to documentation.

---

## 7. Comment provenance: state the constraint, not the discovery

**Source comments state the constraint and its reasoning — never the PR, review round, or finding that discovered it.** Discovery provenance ("added in PR #311", "review finding 4", "hunt F2") lives in git blame; written into the source it is the commit log encoded into comments, addressed to an audience of one and turning to noise as the tracker ages.

The exception is §6 applied to code: a pointer (`#N` / `PR #N`) **stays** when it stands in for a rationale too large to restate in place — the pointer is the alternative to a paraphrase, and on the public repo those numbers are the only provenance an outside reader can resolve (the `dev/` corpus is private).

Existing discovery citations are cleaned opportunistically when a file is touched — never as a bulk scrub.

Adopted 2026-08-19 from the code-quality review (~511 `PR #` citations across `src/` at the time of the ruling, plus hundreds of review-marker variants — the *why* content was consistently worth keeping; the citation ritual was not).

---

## 8. Comment register, and the public home for engineering knowledge (`docs/`)

**Source comments are terse. No essay paragraphs in code.** (Aaron-ruled 2026-08-31.) The essay-register comments were an adaptation — with no persistent reader, files carried their own context — but they cost every session tokens on every read, and a paragraph asserting a guarantee can be false with nothing to notice (several were, measured 2026-08-26). The knowledge they carry is not lost; it has three kinds, and each has a proper home:

| Kind | Home | In the code |
|---|---|---|
| **Constraints & contracts** — "must not be called while X", "false means *could not read*, not *no*" | The code itself | One or two terse lines. This is what comments are for. |
| **Guarantees** — "crash-atomic", "both halves filtered" | The probe that proves it | A pointer at the probe arm. A guarantee without a probe is a claim, not a guarantee — don't write the sentence. |
| **Narrative & rationale** — why it's shaped this way, what was tried, what a prior approach got wrong | `docs/` (public, in-repo) | Nothing, or a one-line pointer. |

**Why `docs/` and not the `dev/` corpus:** `dev/` is private. Knowledge that lives only there is invisible to cloud sessions, outside contributors, and the public repo — the essay comments were partly compensating for exactly that. Durable *engineering* knowledge (architecture, contracts, why the code is shaped this way) belongs in the repo, in the standard place: `docs/architecture/` (LIVING, one short note per subsystem) and `docs/decisions/` (ADRs — Architecture Decision Records: short numbered files, one per decision, context → decision → consequences, immutable once merged, superseded by a later ADR rather than edited). Process and tactical state (handoffs, run orders, session mechanics) stay in `dev/`; an ADR derived from a private ruling is written fresh for public eyes — plain register, no machine paths, no session lore.

Three rules make it hold:

1. **A PR that makes an architectural decision lands its ADR in the same PR** — same discipline as the CHANGELOG rule (CLAUDE.md §5 #10).
2. **§6 governs the migration: `docs/` replaces the comment, never copies it.** The paragraph is deleted where the note is written; the code keeps at most a one-line pointer. A fact stated in both places is the stale-sentence factory §6 exists to close.
3. **Prospective immediately; retroactive opportunistically.** New code is written to this rule now. Existing essay comments are cleaned when a file is touched — never as a bulk scrub, and never in deletion-flagged 1.x code (rewriting comments in condemned code is wasted work by the demolition rule).

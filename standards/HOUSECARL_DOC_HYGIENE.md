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

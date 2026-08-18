# houseCARL — project foundation & operating rules

*houseCARL is the reflection-driven Mutagen rebuild of Housecarl. This file is the first thing every session reads.*

> **Public-repo note.** This is houseCARL's internal operating manual — the working contract between the
> maintainer and the AI sessions that build it. It references a `dev/` working corpus (PRFAQ, plans,
> session handoffs) that is kept **private and is not part of the public repository**, so links into
> `dev/` below will not resolve in a clone. To *use* houseCARL, start with [README.md](README.md).

---

## 1. What houseCARL is

houseCARL gives Claude comprehensive, direct access to the Skyrim Special Edition modding workspace — every plugin record across the load order, every Papyrus script, every runtime and crash log, every asset path, and the live Mod Organizer 2 modlist state — at the **data layer**, beneath the human-interface tools (xEdit, CK, Synthesis). The modder works in plain English inside Claude Code; Claude does the mechanical work and, by default, writes results into a *new* plugin the modder reviews and enables in MO2 (originals untouched in the default lane; an opt-in in-place lane — explicit flag + per-plugin consent — edits an existing plugin directly when the modder asks). **Comprehensive access is the load-bearing capability** — conflict resolution, patch authoring, mod creation, Papyrus triage, mod auditing, and crash diagnosis all emerge from access quality, not from features built one at a time. Full product framing lives in the PRFAQ corpus (`dev/PRFAQ/`).

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

**Standing follow-ups.** `dev/BACKLOG.md` is the durable home for small, dev-noticed follow-ups — tool ergonomics, doc gaps, minor papercuts spotted mid-session but not done inline — that would otherwise get lost in a superseded handoff. Skim it when picking up work; append when you notice one, prune when done. It is *not* the bug lane (below), and chartered work lives in `dev/plans/` or `dev/projects/`.

**The bug/gap lane is GitHub Issues.** Tool bugs and capability gaps — live-session or dev-noticed, from Aaron or anyone — are filed as issues on this repo (forms provided: *Bug report*, *Gap / capability request*; the `[Bug]`/`[GAP]`/`[NOTE]`/`[Docs]` title conventions carry on); a fix PR carries a **closing keyword** (`Fixes #N` / `Closes #N`) in its description so the issue auto-closes on merge, and references `#N`. Bugs in the authoria-requiem *skill pack* go to that repo's Issues instead. (The old local report store was retired 2026-07-15 — its live reports were migrated verbatim as issues #195–#201; `HCBR-<date>-<n>` ids in older docs are historical pointers into that store's archive.) **External reports** (an `.md` report handed to Aaron via Discord or similar) don't get pasted straight into an issue: triage first (valid? reproducible? duplicate of an open issue?), **scrub** — a third party's report carries *their* machine paths, usernames, and load-order details they never agreed to publish — then file it largely verbatim via the matching form/title convention with a provenance line (name the reporter only with their OK), and hand back the issue `#N` for the reply. **Self-review findings are fix-or-drop, not filed** (Aaron, 2026-08-11: *"things either get fixed or they don't. If any genuinely need a fix they wouldn't be lows or nits."*) — a low or nit found reviewing your own branch is fixed on the branch or dropped, never parked as an issue. The lane is for confirmed defects **outside the branch under review**: #324 — data loss in a code path nobody was touching, discovered while reviewing PR #323 — is the shape that belongs here.

**Sub-project sessions.** Some work runs as a self-contained sub-project under `dev/projects/<name>/` (its own tracking, walled off from the main gap/bug/release lane — see `dev/projects/README.md`). If a session is for one, the user will name it ("the follower skill", "the `<name>` project"); then read `dev/projects/<name>/STATUS.md` **instead of** the latest `dev/session-handoffs/` handoff (item 2), and stay in that lane. Absent that, boot normally — the default is unchanged.

**Advisor sessions.** `dev/advisor/` is a project-wide judgment lane (created 2026-08-12): fresh Fable-class sessions that render recommendations on escalations, review the reviews, and translate for Aaron — advisory only, Aaron decides, and the session never builds or holds a worktree. If a session is named as one ("advisor session", "the judgment seat"), boot per `dev/advisor/README.md` instead of items 2–3. Build sessions interact with the lane only through Aaron — an escalation goes to him, and he may bring back its ruling.

The cornerstones (§3) and revalidation protocol (§4) are restated in this file, so you operate correctly from CLAUDE.md alone — the corpus is the authority you re-read when the protocol sends you there, not a tax every session pays.

---

## 3. Cornerstones — coverage and composition, by construction

The PRFAQ's load-bearing claim is **comprehensive access** (§1). For records, that means **every record type Mutagen models is readable and writable — by construction, not by hand.**

This is the reason for the rebuild. Both prior builds hand-wired coverage: a schema per record type, a write-translation per record type (134 schemas; 202 write-mappings, 60 of them still placeholders). That wiring gap meant "comprehensive write access" was always one more hand-port from done. The reflection-driven generator closes it structurally — coverage *is* Mutagen's coverage, and Mutagen's delta vs xEdit is a known upstream surface we fail loud about, never silently around.

**Full coverage is not a scope choice.** If a stumbling block ever frames it as "smaller scope for v1" or "just the common record types," that's a cornerstone violation, not a pragmatic trim — invoke the protocol (§4). Per-record-type hand-mapping does **not** come back.

**Second cornerstone — composition by construction** (elevated 2026-07-22). For *operations*, the same principle: bulk capability is the **closure** of a small, bounded, individually-guarded set of composition primitives — never a verb per job. A proposed bespoke verb is a **bug report against the primitive set**: when a bulk gap appears, add the missing primitive (or file the gap), not the verb. Domain knowledge — field bundles, forbidden prefixes, analogue mappings — lives in **skills as data**; the tool surface stays generic (the one exception: thin, typed, probe-pinned interpreters of *engine* semantics, the `effect_chain` posture). If a stumbling block frames a one-off verb as "quicker for v1," that's a cornerstone violation — invoke §4. Doctrine: `dev/PRFAQ/PRFAQ_COMPOSITION_LAYER_2026-07-22.md`; primitive set + closure proof: `dev/plans/LAYER3_COMPOSITION_PRIMITIVES_BUILD_PLAN_2026-07-22.md`.

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
4. **Anti-bloat** — orientation surface stays small. A session shouldn't burn its budget on stale narrative. Prune aggressively; archive, don't accrete. Corollary — **plain register**: handoffs, STATUS, and commit messages say what changed and why in ordinary sentences; no capitalized lore, no allusions that need the corpus to decode. These documents are agent-to-agent — the reader pays tokens to parse them, and a message that needs the archive to understand is bloat wearing a style. (Added 2026-08-11: the 2.0-wave corpus drifted into a register later sessions imitated and escalated; a process dive needed its own subagents just to decode recent commit messages.)
5. **Lanes — Aaron architects and picks the execution method; conductor proposes and drafts** — Aaron owns capability scope, trade-offs, architecture, *and* how we approach the work (sequencing, parallel-vs-serial, session shape, which method). Conductor proposes execution options with a clear recommendation, then handles the mechanical drafting, decomposition, and ordering once Aaron picks. Surface method choices for Aaron; don't silently pick them — but don't over-gate either: once he's chosen, or when a call is plainly mechanical, proceed decisively. Honest opinion stays first-class — recommend, don't just lay out a menu.
6. **Explicit uncertainty over performed certainty** — "here's what I think, why, what I don't know, and how we'd find out" beats a tidy option matrix implying false confidence.
7. **Q3 — no silent failure** — never a silent wrong answer, never a silently degraded mode. If a tool is compromised or you can't do the thing, say so plainly with what you checked and what to try next.
8. **Atomic, focused commits** — one logical change per commit.
9. **No silent workarounds** — §4 generalized to any decision that trades away something that was supposed to hold, PRFAQ or not.
10. **Worktree & merge discipline — start every change in a worktree; land on `main` only on Aaron's go.** Before any change that will commit, check your branch (`git branch --show-current`) and state it up front; if you're on `main`, create a worktree (`.claude/worktrees/<name>/`, branch `claude/<name>`) FIRST — solo sessions included, not just parallel ones. The main repo folder stays on `main`, read-only except for landing reviewed branches into. Commit freely on the worktree branch — local and reversible. Landing on `main` — via **pre-PR review rounds (#11)** → push → open PR (closing-keyword-linked to its issue, §2; a user-facing change also lands an `## Unreleased` line in `plugin/CHANGELOG.md` in the same PR) → **Aaron's own independent review**, posted to the PR → fold its findings → **Aaron's explicit go each time** → **`gh pr merge <PR#> --rebase --delete-branch`** → remove the worktree, delete the local branch → confirm the linked issue closed — is a separate, outward-facing act, never automatic. **`main` is branch-protected: land through the PR, never by hand.** A local `git merge --ff-only` + `git push origin main` is rejected (a hook says so, and protection would reject it regardless) — `--rebase` keeps history linear, which is all the old "FF merge" wording was asking for. `--delete-branch` cannot delete the local branch while its worktree still holds it, so remove the worktree first (or delete the branch after); the remote branch is deleted either way. The same gate covers any commit that edits this operating manual or other self-governing config: surface it, don't self-commit.

11. **Pre-PR review rounds — the conductor reviews its own branch before the PR opens, with fresh independent agents, bounded by a convergence rule** (standing from 2026-08-07, trialled on PR #318; bounded 2026-08-11 after PR #323 spent ten rounds, ~4M tokens, and twenty agents on a three-fix branch — four of its five high findings were introduced by the branch's own folds, and Aaron's review still found the two mediums that mattered). Before pushing a code branch: spawn independent review agents over the branch diff, fold what survives triage, then spawn a **new** round. Stopping is governed by these rules, in order:
    - **Stop when a round returns only low findings.** The original terminator, still valid.
    - **Stop when the same failure CLASS recurs in two consecutive rounds.** A recurring class is a design signal, not a fix queue: invoke §4 and take the feature or seam generating it to Aaron — never fold a third instance. Severity is not this signal (#323's rounds 2/3/4/8 were one class, four folds, while rounds 5–10 returned no highs and the feature was still broken).
    - **Hard cap: three rounds.** Whatever is open after three goes to Aaron's review with the findings listed, not into round four.
    - **No new rounds after directed folds.** Fresh eyes are for *before* the PR opens. A reviewed, measured fold on a PR that has had its rounds plus an independent review does not reopen the loop.
    What makes the rounds worth the tokens rather than theatre:
    - **Fresh agents every round.** Never continue a reviewer that already saw the code, never let the session review itself. A reviewer that watched you fold its own finding is no longer independent.
    - **Review agents get their own worktree, never the tree you commit from.** Spawn them with the Agent tool's `isolation: "worktree"`. Break-testing reviewers mutate source by design (sabotage a constant, confirm RED, restore) — an agent killed mid-test leaves its sabotage behind, and on the response-layer branch (2026-08-12) a power cut did exactly that: three gutted sentences went into a fold commit unnoticed. Same rule for any agent that runs guards or builds against the tree.
    - **Read the diff before committing a fold.** Stage explicitly; never `git add -A` after agents have had the tree. The gutted-sentence commit shipped because the diff was never read — the sabotage was sitting in plain sight.
    - **Prompts seeded with the decision record, nothing else.** Give them the worktree, the branch, the base, "the repo documents its conventions in CLAUDE.md", **and the branch's settled-decisions list** — a reviewer without it re-litigates chartered design (#300's shape drew the same xEdit-comparison objection in four separate rounds). Still never: what you changed, where you think the risk is, what you're unsure of, or what an earlier round found. A prompt that names your worry gets that worry back.
    - **Triage every finding against the source before folding.** Reviewers are confidently wrong sometimes; one that you cannot reproduce in the code is one you refuse — and say so, with why. Folding an unverified finding is how a review makes code worse. **A round folded at 100% must state why nothing was refused** — the refusal rate is the triage health signal; obedience is not triage.
    - **A finding about a user-facing sentence gets a probe, not a rewording.** Folding a reviewer's wording verbatim is folding an unverified finding in its purest form — measure what the sentence tells the caller to do before changing what it says (#323's contested-remedy probe, which surfaced #324's data loss, is the standing example).
    - **A fold that adds a conditional ships an arm per branch, RED-checked in both directions.** And a branch that cannot be fixtured honestly is the design signal to escalate the conditional itself (§4), not a testing gap to work around — on PR #339, both-direction sabotage of `winnerTokenFree` stayed green, and the right fix deleted the conditional.
    - **Sabotage RED-checks run from a committed state.** Commit the fold, then sabotage, then `git checkout --` to restore, and verify the restore by grepping for a string the fold introduced rather than assuming it worked. Never sabotage a dirty tree — the restore discards the uncommitted work along with the sabotage, and stashing the fold first is the same trap wearing a different hat (a conflicted `stash pop` cost one session its fold). Two consecutive sessions hit this before it became text (2026-08-17, 2026-08-18).
    - **This does NOT replace Aaron's review.** His is still the gate on the open PR. The rounds exist so his review spends itself on judgement, not on things a first pass would have caught.
    - **Report the rounds in the PR body** — how many, what each found, what you refused and why. A reviewer reading the PR should be able to see what was already looked at.
    - **Scope:** code branches. A docs-only or manual-only PR doesn't need rounds — say so rather than performing them.

---

## 6. Naming

All names follow `standards/HOUSECARL_NAMING.md`. Load-bearing rule: MCP tools are `housecarl_<snake_case>` — the `housecarl_` prefix **carries forward** from the prior build (brand continuity; locked 2026-05-27), even though the project is now houseCARL. The brand string "houseCARL" lives in exactly one place in code (the MCP server's name/config), not scattered through it.

---

## 7. Skills

houseCARL's skills live at `.claude/skills/<slug>/` and ship bundled in the plugin (namespaced `/housecarl:<name>`). The current set is **14 skills** across a few kinds. **Reference-lookup** — `mutagen-reference` (record schemas) and `papyrus-reference` (Papyrus + SKSE signatures), each generated **by construction** from an upstream corpus (Mutagen's record library; BellCube's papyrus-index) so neither is a hand-maintained subset, plus `biped-slot-reference` (a biped slot → its `FirstPersonFlags` bit for a `cross_plugin_query ... has` filter). **Distributor-grammar authoring** — `skypatcher-authoring`, `spid-authoring`, and `kid-authoring` (with `cid-authoring` and a routing micro-skill still to come). **Content authoring / investigation** — `dialogue-authoring` (DIAL/INFO at the data layer), `facegen-diagnostics` (the dark-face NPC bug), `npc-appearance-copy` (the domain half of `copy` for NPC faces — the seed field set, the Race exclusion, the inline bundle that rides `apply`'s zip, and the FaceGen carry from the named donor rather than the VFS winner), `oar-authoring` (Open Animation Replacer configs), and `skse-plugin-authoring` (authoring an SKSE plugin in C++ against CommonLibSSE-NG — houseCARL's first code-layer skill, paired with the `skse_inventory` tool). **Performance review** — `papyrus-optimization`, an investigation-flow skill that grades a `.psc` broken / suboptimal / clean and names the fix; the cost-and-habits complement to `papyrus-reference`. **Operational guardrail** — `tool-output-awareness` (recognize generated tool output and keep it out of authored patches). **Bulk-job planning** — `bulk-record-jobs` (map "many records → one structured deliverable" jobs — catalogues, link graphs, conflict surveys, patch rebuilds, fan-out extraction — onto the bulk primitives instead of per-record loops, carry the game-generic CK crafting conventions, and pin the canonical deliverable schema that kills fleet drift; D7-bounded — no mod-specific knowledge). Three of these are community-contributed by **DrHeisen** (`papyrus-optimization`, `oar-authoring`, `tool-output-awareness`).

The `modlist-authoring` cluster (`skill-authoring`, `modlist-authoring`, `knowledge-file-authoring`) was **removed when packaging moved to a plugin** — it was built on the retired "unpack houseCARL into the user's workspace and author content alongside it" model. In the plugin the shipped skill set is curated by us and read-only to users; anyone extending their *own* project uses Claude Code's native skill authoring. The authoring *methodology* survives in `standards/HOUSECARL_SKILL_AUTHORING.md` for building houseCARL's own skills.

Tool-surface skills (`esp-patching`, `mod-dissection`, `bsa-archives`, `crash-diagnostics`, …) are **not** imported — they get rewritten against the new tool surface once it ships. A skill pointing at tools that don't exist yet is worse than no skill. (`crash-diagnostics` is methodology-rich but its body is built around specific tools — empirically tool-coupled — so it waits with this set.)

**Building houseCARL itself:** use the builder skills rather than hand-rolling — the Anthropic `skill-creator` for new skills, paired with the `standards/HOUSECARL_SKILL_AUTHORING.md` methodology and `standards/HOUSECARL_NAMING.md`; the Anthropic `mcp-builder` skill for MCP-server work. (mcp-builder targets Python/Node — for our C# server it's useful for MCP *design* guidance, not code scaffolding; confirm at the MCP-server wave.)

---

## 8. What NOT to do

- **Don't reconstruct context from prior sessions.** Assume nothing; read the docs (§2). Memory supplements, it doesn't substitute.
- **Don't work on `main`.** Every change that will commit starts in a worktree (`.claude/worktrees/<name>/`); the main repo folder is read-only except for landing reviewed branches (§5 #10). Check your branch at the start — booting onto `main` and editing there is the recurring drift this rule exists to stop.
- **Don't treat coverage as a subset.** §3. Tempted to ship "the common record types"? Stop and read §4.
- **Don't ship a bespoke bulk verb.** §3, second cornerstone. A job-shaped verb ("audit X", "copy the Y frame") is a bug report against the composition-primitive set — add the primitive or file the gap. Tempted because it's quicker? Stop and read §4.
- **Don't silently work around a block.** §4. Surfacing costs minutes; the alternative is why we rebuilt.
- **Don't edit the foundation corpus (`dev/PRFAQ/`) or other ARCHIVE docs.** Immutable record of why decisions were made. New docs supersede; old ones stay as written (typo-fix excepted). Doc classes (LIVING vs ARCHIVE) are defined in `standards/HOUSECARL_DOC_HYGIENE.md`.
- **Don't re-import the legacy lock-down.** The old repo's heavy guardrails are part of what we left behind. New guardrails earn their place from real need.
- **Don't "improve" the legacy repo.** `Housecarl [Legacy]/` is frozen reference — read it, don't touch it.

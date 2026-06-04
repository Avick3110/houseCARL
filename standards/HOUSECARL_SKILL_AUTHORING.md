# HOUSECARL_SKILL_AUTHORING — Skill Authoring Standard

**Status:** locked 2026-04-30 by Aaron; **pruned 2026-06-03** — the AD-4 meta-skill cluster (`skill-authoring` / `modlist-authoring` / `knowledge-file-authoring`) and the retired unpack-model "user-side authoring" surface were removed when houseCARL moved to plugin packaging. The methodology below governs houseCARL's own shipped skills; some legacy Claude_MO2 / Q-lock scaffolding remains as illustrative history (a fuller modernization is a separate pass). **Revised 2026-06-04** (Aaron-approved): the `name:` rule (§ 2) is reversed — shipped skills now install into **both Claude Code and Codex**, and Codex requires a `name:` field in `SKILL.md`.
**Scope:** Binding standard for every skill shipped under the Housecarl project (`.claude/skills/<name>/SKILL.md` inside the Housecarl repo, plus any skills bundled into the MCP plugin distribution). Shipped skills install into **both Claude Code and Codex** (Codex support added 2026-06-04), so every rule below must hold for both hosts.
This standard is operational. Every rule below is enforceable by a reviewer producing a binary "conforms / doesn't conform" verdict. Rules without that property don't belong here.

---

## 1 — Why this standard exists

The single most important skill in Claude_MO2 v2.9.4 (`session-strategy`) failed to trigger for months in production. Its description triggered on a meta-condition Claude cannot evaluate at trigger time (`"sessions involving extensive MCP work"`). A real consumer Claude on 2026-04-29 ran ~3,500 sequential `mo2_record_detail` calls instead of using v2.9.2's batch parameter — partly because `session-strategy` (which would have surfaced the batch guidance) never loaded. The v2.9.5 fix was reactive description-engineering. (See `REBUILD_RATIONALE.md` § Signal 1; v2.9.5 plan archive at `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/PLAN.md`.)

The pattern problem: skill descriptions in Claude_MO2 were written without applying Anthropic's published guidance on undertriggering and on action-shaped trigger language. **The same pattern exists in skills that haven't yet failed publicly.** This standard prevents Housecarl from re-running the same failure mode by codifying the rules at foundation level, before any skill is written.

**Empirical anchor:** Anthropic's `anthropic-skills:skill-creator` (introspected at `~/AppData/Roaming/Claude/local-agent-mode-sessions/skills-plugin/.../skills/skill-creator/SKILL.md`) is the canonical authority on description engineering. This standard is faithful to skill-creator's prescription and adapts it to Housecarl's scope (a Bethesda modding MCP plugin).

**Operational directive — invoke skill-creator at authoring time.** Before authoring, editing, or validating any Housecarl skill, invoke `anthropic-skills:skill-creator` via the Skill tool at the start of the session. This standard is the project-scoped overlay; skill-creator is Anthropic's upstream authority and carries the live procedural body — description-optimization loop, eval-set construction guidance, the `run_loop` script invocation referenced in § 6.1. Running it alongside this standard puts the full Anthropic guidance in context regardless of when this standard was last revised, and reduces drift between this standard's quoted snippets (lock-time snapshot) and skill-creator's current prescription (live). Skip only when Aaron has authorized the specific edit.

**Verifying Anthropic-skill claims.** The bundled-skills surface (the available-skills list shown at session start) is not exhaustive — many Anthropic-published skills ship via the public repo at <https://github.com/anthropics/skills> and install on-demand via `/install anthropics/skills/<skill-name>`. `mcp-builder` (referenced in HOUSECARL_MCP_AUTHORING.md § 1.1) is the canonical precedent: published but not bundled by default. Before concluding that no relevant Anthropic-published skill exists for an authoring concern, web-search or fetch the repo's skill index. Don't infer absence from the bundled list alone.
---

## 2 — Anatomy of a Housecarl skill

Every Housecarl skill is a directory under `.claude/skills/<skill-name>/` with `SKILL.md` as the entrypoint. Optional bundled resources sit alongside.

```
.claude/skills/<skill-name>/
├── SKILL.md            (required — frontmatter + body)
├── references/         (optional — large reference docs loaded on demand)
│   └── <topic>.md
├── scripts/            (optional — executable scripts the skill invokes)
│   └── <name>.py
└── assets/             (optional — templates, fixtures, examples)
    └── <name>.<ext>
```

Source: <https://code.claude.com/docs/en/skills> § "Add supporting files".

**Naming rules** (binding):

- **Skill folder name** is `kebab-case`, lowercase, ≤ 30 characters, alphanumeric + hyphens only. Conforms: `leveled-list-patching`, `crash-diagnostics`. Doesn't conform: `LeveledListPatching`, `leveled_list_patching`, `lvl_list`, `merge-leveled-list-patches-and-related-things`.
- **Folder name == `name:` frontmatter value.** Include `name: <folder-slug>` in the frontmatter, set **exactly equal to the folder name**. Codex *requires* a `name` field in `SKILL.md`; Claude Code derives the name from the folder when `name` is absent — so writing `name:` equal to the folder slug satisfies **both hosts** at once. The folder stays the conceptual source of truth and `name:` must mirror it exactly; a reviewer rejects any skill whose `name:` is missing or differs from its folder. *(Reversed 2026-06-04: this standard previously mandated **omitting** `name:` — correct for a Claude-only world, but it produces Codex-invalid skills, which is exactly why houseCARL's first four skills shipped without it and had to be retrofitted.)*
- **Skill names must NOT embed a Housecarl version.** `npc-analysis` conforms. `npc-analysis-v2`, `npc-analysis-v3-rebuild` don't — versioning happens through the repo's git history, not skill names.
- **Skill names must NOT carry the project name.** `crash-diagnostics` conforms. `housecarl-crash-diagnostics` doesn't — the namespace is implicit from the directory.
- **Skill names must describe the user-facing topic, not the internal mechanism.** `leveled-list-patching` conforms. `mutagen-bridge-write-helper` doesn't.

---

## 3 — Description format (the load-bearing rule)

### 3.1 — Action-first lead, no exceptions

The description's first sentence must lead with the **action the skill performs** plus **what the skill is for**. The description is ~100 words of "always-in-context" budget per Anthropic's progressive-disclosure model — every word must earn its place.

**The four-part formula:**

> [Action verb phrase describing what the skill does]. Use [this/when] [user-recognizable phrasing of the trigger contexts]. [Optional: pushy reinforcement countering undertriggering].

Conforms (Housecarl scope, illustrative):

> Create ESP patch plugins via `housecarl_create_patch` — overrides, leveled list merges, script attachments. Use when the user wants to create a patch, override a record, add keywords to armor, modify NPC stats, attach Papyrus scripts, or merge leveled lists across plugins. Load this even if you think the patch is simple — the operation matrix is non-obvious.

Doesn't conform:

> v3.0 patch authoring skill — Phase 2 axis. Covers the consumer-facing operations matrix introduced in the daemon-foundation refactor.

The non-conforming version leads with a version marker, references an internal phase axis, and never tells Claude **when to load** the skill. This is the exact failure mode v2.9.5 reactively fixed in `mo2_record_detail`'s tool description. Skill descriptions are subject to the identical rule.

### 3.2 — Trigger language must be user-recognizable, not Claude-introspective

The trigger language ("Use when…") must describe phrasings or topics **a user would actually say** — concrete domain nouns, named operations, observable symptoms.

**The session-strategy failure** (canonical anti-pattern):

> Old (failed for months in production): "Use this skill in any session involving extensive MCP work or when planning multi-step modlist investigations."

Why it failed: "extensive MCP work" is a property of the session in retrospect, not a property of the user's utterance. Claude cannot inspect the session and conclude "this session will become extensive" before deciding whether to load the skill. The trigger condition was epistemically unavailable at trigger time. Result: zero load events across many sessions where the skill would have prevented expensive failures.

> New (v2.9.5 ship, current `<old-repo>/.claude/skills/session-strategy/SKILL.md:2`): "Use this whenever the user mentions modlists, mods, plugins, conflicts, ESP patches, NPCs, leveled lists, BSAs, NIF meshes, FUZ audio, Papyrus scripts, or record investigations — even if you think you only need a few calls or can answer directly."

Why this conforms: every comma-separated item is a user-utterance keyword Claude can match against the conversation surface text. The "even if" tail is the pushy reinforcement (§ 3.3). This is the shape every Housecarl skill description must follow.

**Operational rule for Housecarl:** every "Use when…" must contain at least three concrete, user-utterance-level trigger nouns or symptom phrases. Triggers phrased as "sessions involving X", "when complex Y is needed", "for substantive Z work" are forbidden — they re-create the session-strategy failure.

**Audience calibration (per Q10 lock 2026-05-01).** Housecarl v1.0 is **modder-primary** under Aaron's corrected definitions: **modder = modlist-builder** (the user installing, configuring, conflict-resolving, and shipping a coherent modlist; needs all of Housecarl's tools). **mod-maker = isolated mod author** (creating a single mod from scratch; benefits as byproduct, not the primary audience). This **inverts** common conflations where "modder" sometimes means "consumer" and "mod-maker" means "creator" — Housecarl's terminology centers the modlist-builder, not the isolated mod author.

Trigger language in shipped skills should prioritize modder-vocabulary phrasings over mod-maker-vocabulary phrasings:
- **Prefer:** modlist names (Lorerim, Wildlander, Septim's Skyrim), plugin conflicts, ESP merges, BSA extracts, NPC outfit conflicts, leveled-list patches, override resolutions, crash logs, Papyrus log lines, MO2 instance names, USVFS overlays, load order positions
- **De-prioritize (still acceptable, just not lead):** "creating a new mod", "authoring SKSE plugin source", "drafting a quest from scratch", "designing custom NPC voice lines"

Modder-vocabulary triggers cover the v1.0 user surface; mod-maker triggers fire less often in v1.0 (Q12 SKSE source READ is the main mod-maker-leaning capability, and even that's read-only). v1.x ships may rebalance per Q8 spectrum philosophy.

### 3.3 — Pushy phrasing, deliberately

Per `anthropic-skills:skill-creator`'s SKILL.md (verbatim, lines 67):

> "currently Claude has a tendency to 'undertrigger' skills — to not use them when they'd be useful. To combat this, please make the skill descriptions a little bit 'pushy'."

For Housecarl this means **every skill description ends with one of these tail patterns** (or a domain-appropriate equivalent):

- `"... — even if you think you only need [trivial path]."` (counters under-load on tasks that look small)
- `"Load this before [concrete first action], not after [concrete failure mode]."` (anchors load timing)
- `"Use this whenever [topic], even if [common dismissal-rationalization]."` (counters topic-adjacent dismissals)

Conforms:

> "Use before performing any full-mod analysis — even if you think the mod is small. Many small-looking mods touch hundreds of records once you trace conflicts."

Doesn't conform:

> "Use this when appropriate."

The bias is asymmetric: a skill that triggers when not strictly needed wastes ~100 words of context (recoverable). A skill that fails to trigger wastes a full session of degraded behavior (unrecoverable inside that session). Optimize for recall.

### 3.4 — What the description must NOT contain

These items are forbidden in any Housecarl skill description and a reviewer rejects on first sight:

| Forbidden item | Example (don't ship this) | Why |
|---|---|---|
| Internal version markers | `"v1.0 introduced..."`, `"houseCARL v2 axis 3"` | Description is the consumer-facing trigger surface. Provenance belongs in CHANGELOG.md, not in the skill registry. |
| Phase tags | `"Phase 1 read-side capability"`, `"P3 ship"` | Same — internal dev artifact. |
| Implementation jargon | `"shells out to mutagen-bridge.exe via NDJSON"` | Jargon describes how, not when. Move to the body (§ 4). |
| Architecture nouns the user won't say | `"L2 query DSL", "daemon IPC layer"` | Claude can't match these to user utterances. |
| Vague qualifiers | `"complex"`, `"extensive"`, `"substantive"`, `"non-trivial"`, `"significant"` | Subjective; not match-able. Replace with concrete trigger nouns. |
| Negative-gated triggers | `"Use only when X is true"`, `"Avoid using if Y"` | Pushes Claude toward conservative under-load. Standard 3.3 mandates the opposite bias. |
| Run-on descriptions over the cap | (anything > 1,536 chars combined description+`when_to_use`) | Anthropic truncates beyond 1,536 chars, so over-cap content silently disappears from the registry. Source: <https://code.claude.com/docs/en/skills> § Frontmatter reference. |

### 3.5 — Description length: target 60-180 words

Hard cap: combined `description` + `when_to_use` ≤ 1,536 characters (Anthropic-imposed truncation). Soft target: 60-180 words. Below 60 words means insufficient trigger surface. Over 180 words means body content has leaked into the description — move it to § 4.

**Front-load the use case** within the first sentence so it survives mid-string truncation. If a reader sees only the first 200 characters, can they tell what the skill does and roughly when it loads? If no, restructure.

---

## 4 — Body structure standards

### 4.1 — Length budget: <500 lines, hard

Per skill-creator and per Anthropic docs (<https://code.claude.com/docs/en/skills> § "Add supporting files"):

> "Keep `SKILL.md` under 500 lines. Move detailed reference material to separate files."

Skill body in context budget: ~loaded once when triggered, persists for the session per Anthropic's content-lifecycle doc. A 500-line body is roughly 5,000 tokens — large but defensible as a one-time load. A 1,500-line body is not — split it.

**Operational rules:**
- Soft target: < 250 lines. The Claude_MO2 working skills (`leveled-list-patching` 203 lines, `esp-patching` 53 lines, `npc-analysis` 17 lines) all sit well under this and remain useful.
- Hard cap: 500 lines. Bodies approaching the cap require an additional layer of progressive disclosure — move the deep tail to `references/<topic>.md` and reference it from SKILL.md.
- Use a table of contents in `references/` files exceeding 300 lines (skill-creator guidance, line 99).

### 4.2 — Section conventions

Every Housecarl skill body opens with a single H1 that matches (case-insensitively) the skill folder name turned into Title Case. No exceptions.

After the H1, the body uses these standard section headings in this order (omit any that don't apply, but never reorder):

1. **Overview / What this skill does** — one paragraph; what the skill enables Claude to do, in user terms. Distinct from the description (which is about *when*); this is about *what*.
2. **First step** (optional) — for router skills (§ 5.2), the canonical opening parallel-call set or initial action. Example: `npc-analysis/SKILL.md` § "First Step (Always)" at `<old-repo>/.claude/skills/npc-analysis/SKILL.md:7`.
3. **Reasoning framework / Decision tree / Workflow** — the procedural body. May contain sub-headings.
4. **Common mistakes** (optional but recommended) — concrete anti-patterns with the consequence. The `leveled-list-patching` skill's "Common Mistakes" section at lines 113-145 is the reference shape.
5. **Verification / Checklist** (optional) — post-action verification steps if the skill produces an artifact (a patch, a merged file, etc.).
6. **Real example** (optional) — fully worked example with named records / FormIDs / plugins.
7. **Sub-topic routing** (router skills only) — table mapping sub-conditions to other skills. Example: `npc-analysis/SKILL.md:18`.
8. **Notes** (optional, terminal) — short bullet list of caveats, version-dependent behaviors, performance gotchas.

### 4.3 — Imperative voice, "explain the why"

Per skill-creator (lines 117, 302):

> "Prefer using the imperative form in instructions."

> "Explain the **why** behind everything you're asking the model to do. ... If you find yourself writing ALWAYS or NEVER in all caps, or using super rigid structures, that's a yellow flag — if possible, reframe and explain the reasoning."

**Conforms:**

> "Pull the conflict chain first. The chain is what tells you which plugins are involved and in what order — without it, you're guessing about the merge base."

**Doesn't conform:**

> "ALWAYS pull the conflict chain. NEVER skip this step. This is MANDATORY."

The all-caps-MUST style is brittle and degrades in proportion to how far the runtime situation drifts from what the author imagined. Imperative + reasoning travels better.

**Exception:** safety-critical instructions (data-destroying operations, write-side gotchas with no recovery path) may use the imperative + a single bolded warning. Don't escalate to all-caps stacked imperatives.

### 4.4 — Citation discipline

When the body references other skills, tools, files, or external docs:

- **Other Housecarl skills:** reference by skill name in backticks: `` `leveled-list-patching` skill ``. Don't link by relative path — paths drift across plugin/repo split.
- **Housecarl tools:** reference by full tool name in backticks: `` `housecarl_create_patch` ``. Tools are namespace-qualified (per `HOUSECARL_NAMING.md`, P1.8).
- **Files in the Housecarl repo:** reference by repo-relative path in backticks: `` `mo2_mcp/tools_records.py:335` `` — line numbers OK if stable.
- **External URLs:** full URL inline. The body is not auto-rendered as a clickable web doc; raw URLs read fine.
- **Anthropic doc references:** include the URL so the rule is verifiable. Per <https://code.claude.com/docs/en/skills>, Anthropic's own pattern is consistent.
- **Old-repo references** (during Phase 2 only): `<old-repo>/path/to/file.md` qualified with the prefix. After Housecarl v1.0, these references should not exist in any shipped skill body — they're acceptable in standards docs (this file) but not in skills.
### 4.5 — Forbidden body patterns

| Forbidden | Why |
|---|---|
| Embedded changelog ("v2.9.2 added...", "as of v2.9.5...") | Drift trap — skill body must describe current behavior. Changelog goes in `CHANGELOG.md`. |
| TODO / FIXME / XXX markers | Skills are user-facing; TODOs leak ongoing dev concerns. Move to issues or release plan. |
| Verbatim copies of MCP tool descriptions | Drift trap — duplication of the consumer surface. Per v2.9.5's `kb/KB_Tools.md` retirement (`REBUILD_RATIONALE.md` Signal 1). Reference the tool by name; don't restate its schema. |
| Per-record-type exhaustive enumerations | If a skill needs to enumerate "every operation by record type", it's a documentation file in disguise. Lift to `references/<table>.md` and reference from SKILL.md. |
| Pseudocode where actual JSON / actual tool calls fit | Skills are read by Claude, not by humans first. Show the real call shape Claude will produce. `leveled-list-patching` lines 100-108 (actual JSON of a `merge_leveled_list` operation) is the reference. |

---

## 5 — When to use a skill vs a tool (decision tree)

Skills and tools occupy different layers. Mistaking one for the other is the second most common failure mode after description engineering.

### 5.1 — The decision tree

Walk these questions in order. Stop at the first "yes."

1. **Does the work require executing code, querying data, or performing an effect (write a file, mutate state, call an API)?** → Build a **tool** (MCP `@mcp.tool` registration). Tools execute. Skills don't.

2. **Is the work a one-step capability invocation already covered by an existing tool, with no procedural composition?** → Don't build a skill. Improve the tool's description (per `HOUSECARL_MCP_AUTHORING.md`, P1.2) so Claude reaches for the tool directly.

3. **Does the work require a procedure across multiple tool calls, with decision logic between calls (e.g., "first check X, then if Y do Z, otherwise W")?** → Build a **skill**. Skills are playbooks for orchestrating tool calls.

4. **Is the work a domain-specific reasoning framework that informs how Claude reads tool output (e.g., classifying a leveled list conflict by intent type)?** → Build a **skill**. Skills carry domain knowledge tools can't embed.

5. **Is the work a long reference document the user wants Claude to internalize (modlist conventions, glossary, naming standards specific to a Skyrim modlist)?** → Build a **skill** with `disable-model-invocation: true` if it's purely reference and shouldn't auto-trigger from open-ended user prompts (`user-invocable: false` if it shouldn't be `/skill-name` invocable either; per <https://code.claude.com/docs/en/skills> § "Control who invokes a skill").

6. **Is the work a session-startup playbook that should always apply when the relevant domain comes up?** → Build a **skill** with a deliberately broad, pushy description (the `session-strategy` shape, post-v2.9.5).

### 5.2 — Four skill archetypes for Housecarl

Most Housecarl skills will fall into one of four shapes:

#### Operational skill (e.g., `session-strategy` post-v2.9.5)

Provides session-wide operational rules that apply across many tool calls. Typically loads on broad triggers. Pushy description is mandatory.

Reference: `<old-repo>/.claude/skills/session-strategy/SKILL.md`. Trigger covers a wide topic surface; body covers parallel-execution rules, agent-delegation rules, context-management rules, tool-specific notes.

#### Procedural skill (e.g., `leveled-list-patching`, `esp-patching`)

Walks Claude through a multi-step procedure with decision points and verification steps. Typically loads when the user names the procedure or its goal. Body has a clear "Step 1, Step 2..." structure.

Reference: `<old-repo>/.claude/skills/leveled-list-patching/SKILL.md`. The "Reasoning Framework" section (lines 42-110) is the canonical procedural-skill body shape.

#### Router skill (e.g., `npc-analysis`)

Kicks off the canonical opening moves for a topic and routes to sub-skills based on what the opening reveals. Body is short (<50 lines typically); the value is in the opening parallel-call set + the routing table.

Reference: `<old-repo>/.claude/skills/npc-analysis/SKILL.md` (17 lines total). "First Step (Always)" + "Sub-Topic Routing" table.

#### Investigation-flow skill (e.g., `crash-diagnostics`, `mod-dissection`)

Authors a pure-read investigation procedure where Claude reasons over read-access to reach a diagnosis or assessment. Distinct from procedural skills: no fixed decision tree, no write-side actions, no routing; the body authors an *investigation order* that Claude composes queries against on demand.

**Read-access surfaces (mechanism-aware):**
- L2 plugin substrate (Mutagen-bridged record/conflict queries; v1.0 always)
- Q7 file-level VFS (which mod owns which file under USVFS overwrite-priority resolution; pulled INTO v1.0 per Q7 amendment 2026-05-01; specific tool surface per Phase 2.7 V2.5 design lock)
- Q9 BSA contents (BSA support pulled INTO v1.0 per Q9 amendment 2026-05-01; daemon-side mechanism per Q9.1 — Phase 3.6 dev/research decides BSArch CLI wrapper vs from-scratch native)
- Q12 SKSE source READ (free byproduct of Q7 — `.cpp/.h` source when modlist or accompanying repo includes it)

**Q3 acceptance principle is load-bearing:** investigation-flow skills must emit either a correct diagnosis OR explicit "I cannot — here's what I checked + what to investigate next" framing. Silent wrong answers fail outright per Q3 lock + §6.6 conformance scoring. The v2.9.5 confident-but-wrong failure mode is the structural problem this archetype's eval discipline exists to prevent.

**Body shape:** Investigation-flow skill bodies enumerate the read steps in their canonical order (e.g., `crash-diagnostics`: read crash log → read papyrus logs → dig into related mods → reason about likely cause), without prescribing exact tool calls or query parameters. Claude composes the queries on demand based on the specific case. There is no mechanical scoring rubric, no quality-scoring engine, no L2 query templates pre-baked in the skill body.

References: Phase 3.2 dev/research authors `crash-diagnostics` + `mod-dissection` skill REBUILDs per Q11 lock (VISION_ALIGNMENT_PROGRESS.md §Q11) + FOUNDATION.md §"Read-only investigation flows (Q11 lock 2026-05-01)". Both skills' eval sets follow §6.6 Q3 conformance scoring (`crash-diagnostics` seed corpus = v2.9.4 100% real-world crash record; `mod-dissection` seed corpus = ≥ 8 reference mod-dissection cases authored at Phase 3.2 dev/research).

**Architecture note:** Investigation-flow skills are mechanism-agnostic at the skill-body level. Tool names underlying the read-access surfaces may shift per Phase 2.7 V2.5 lock (file-level VFS) or Phase 3.6 mechanism choice (BSA support per Q9.1) — investigation-flow bodies cite *capability* not *concrete tool name* to remain stable across mechanism decisions.

If a proposed skill doesn't fit any of these four shapes, the burden is on the author to justify the new shape in the SKILL.md's first paragraph. New shapes shouldn't proliferate without cause — the four shapes here cover the full v1.0 + foreseeable v1.x surface.
### 5.3 — Anti-patterns: what should be a tool, not a skill

Reviewer rejects on sight if a proposed skill matches any of these:

| Anti-pattern | What it should be instead |
|---|---|
| Skill whose entire body is "call this MCP tool with these parameters" | The tool's own description (per `HOUSECARL_MCP_AUTHORING.md`). Skills don't paraphrase tools. |
| Skill that wraps a single tool call with no decision logic | Improve the tool description. |
| Skill encoding a hardcoded modlist's facts ("in Lorerim, the Lydia override is plugin X") | Either an addon-style data file under `<modlist>/.claude/` (per the per-modlist convention from Claude_MO2; codified in `HOUSECARL_REPO_LAYOUT.md`, P1.7) or a project memory entry. Not a shipped skill. |
| Skill that duplicates a `kb/` topic reference | Either the `kb/` reference stays and the skill goes, or the skill stays and the kb file goes. Not both. (Per v2.9.5's `kb/KB_Tools.md` retirement.) |

**Capability-procedural-vs-modlist-fact distinction (per Q9 cascade item 3; mechanism-agnostic).** A skill is *capability-procedural* if its body authors a procedure for a Housecarl capability (e.g., "extract a BSA via the daemon's BSA-support tool surface in this case-flow" — concrete tool name per Q9.1 mechanism outcome at Phase 3.6). It is *modlist-fact* if its body encodes facts about a specific modlist (e.g., the Lorerim Lydia row above). The former is a valid skill; the latter is the anti-pattern. Distinguishing example: the v1.0 `bsa-archives` skill (per Q9 amendment) is capability-procedural; the original AD-1-DROPped `bsa-archives` skill was modlist-fact framed. Capability-procedural skills are mechanism-agnostic — the body authors the procedure; underlying tool names/binaries can change without the skill body shifting categories.

---

## 6 — Trigger reliability validation

A skill description is not shippable until trigger reliability has been measured. Claude_MO2 v2.9.5 attempted to ship validation as a post-edit step; this standard makes it a precondition.

### 6.1 — The validation method

Use `anthropic-skills:skill-creator`'s description-optimization loop. Invocation pattern (verbatim from skill-creator's SKILL.md, lines 381-388):

```bash
python -m scripts.run_loop \
  --eval-set <path-to-trigger-eval.json> \
  --skill-path <path-to-skill> \
  --model <model-id-powering-this-session> \
  --max-iterations 5 \
  --verbose
```

The loop:
1. Splits an eval set 60% train / 40% held-out test.
2. Evaluates the current description (3 reps per query for reliable trigger rate).
3. Calls Claude to propose improvements based on what failed.
4. Re-evaluates each candidate on both train and test.
5. Selects the description with the best **test** score (avoiding overfitting).

### 6.2 — Eval set construction (binding requirements)

An eval set is a JSON array of `{query, should_trigger}` objects. Source: skill-creator SKILL.md § "Description Optimization" (lines 339-357).

**Required composition:**

- **8-10 should-trigger queries.** Mix of phrasings — formal, casual, lowercase, partial-word, abbreviated. Include cases where the user **doesn't** name the skill or its topic explicitly but clearly needs it. Include cases where the skill competes with another skill but should win.
- **8-10 should-not-trigger queries.** **The valuable ones are near-misses** — queries that share keywords or domain concepts with the skill but actually need something different. The standard is that should-not-trigger queries must be **genuinely tricky**. "Write a fibonacci function" is not a useful negative test for any Housecarl skill — it doesn't probe anything.

**The query realism rule:** queries must be plausible user utterances. Concrete contexts (file paths, modlist names, real plugin names, observable symptoms, casual speech, typos) outperform abstract requests. The v2.9.5 eval set at `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/eval_set.json` is the reference shape.

Conforms (real example, from the v2.9.5 eval set):

> `"Aaron asked me to investigate why deer give 0 gold when looted - track down the BSA or the override"` — should_trigger: true (specific symptom; modding-domain; investigation flavor)

Doesn't conform:

> `"Help me with mods"` — too abstract; no signal whether the skill should fire

### 6.3 — Pass/fail thresholds

A skill ships only when its description hits these thresholds on the held-out test set, computed across 3 reps per query:

- **Recall (should-trigger rate)**: ≥ 80%. The skill fires for at least 8 of 10 legitimate trigger queries. Below 80% = the skill is shadow-failing (the v2.9.4 `session-strategy` failure mode).
- **Specificity (should-not-trigger rate)**: ≥ 50%. Per the asymmetric-cost argument in § 3.3, low specificity is acceptable. Below 50% means the skill is firing on truly unrelated queries — tighten the trigger nouns.

Skills can ship with recall < 80% **only** with explicit Aaron sign-off and a documented follow-up to re-validate on the next release. This gate exists because shipping below 80% recall is exactly the v2.9.4 failure mode the rebuild is designed to prevent.

### 6.4 — Windows environment caveat

`run_loop.py`'s underlying `run_eval.py` uses `select()` on subprocess pipes, which **does not work on Windows** (per the v2.9.5 retrospective at `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/PHASE_1_HANDOFF.md` § "Empirical skill description validation"). All v2.9.5 attempts on Aaron's Windows box returned constant "no trigger" / 0% trigger rate / meaningless results.

**Operational implications for Housecarl:**

- Description-validation runs are performed in WSL2 / Linux / macOS, not native Windows. Phase 3 setup (`HOUSECARL_REPO_LAYOUT.md` and dev-environment standards) must include a documented validation environment.
- A native-Windows-compatible rewrite of `run_eval.py` (substituting `threading + queue` or `subprocess.communicate()` for `select()`) is a follow-up candidate; if implemented, document at the Housecarl repo's `dev/` level.
- Until either path is in place, the validation gate (§ 6.3) requires a non-Windows execution environment. Skills authored without validation are not shippable per this standard.
### 6.5 — Manual validation as fallback (constrained)

When the empirical loop is unavailable for environmental reasons, **manual validation is permitted as a constrained fallback** with these rules:

1. The eval set (still 8-10 + 8-10) is constructed as in § 6.2.
2. The author walks each query and predicts whether the description will trigger, recording the prediction inline in the eval JSON (`{"query": "...", "should_trigger": true, "manual_predicted_trigger": true}`).
3. The skill ships only if predicted recall ≥ 80% and predicted specificity ≥ 50% **and** at least one peer reviewer has validated the predictions independently (concretely: the reviewer checks 3 random queries by reading the description cold and saying whether they think it should fire).
4. The skill is flagged for empirical re-validation on the next release that follows. Track via a follow-up entry; don't let it become permanent.

The v2.9.5 ship used this fallback (per `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/PHASE_1_HANDOFF.md`). It is not a substitute for the empirical loop — it is a documented exception path for environment-blocked moments.

### 6.6 — Q3 conformance scoring ("no silent wrong answers")

Per Q3 lock (vision-alignment 2026-05-01) + Q5.5 (runtime validator dropped), eval-set discipline is the v1.0 substitute for runtime validation of skill *outcome correctness*. §6.3 captures trigger-reliability thresholds; this subsection adds outcome correctness as a second eval axis for skills authoring artifacts where wrong-answer probability is non-zero.

**Scope:** Skills subject to Q3 conformance scoring include investigation-flow skills (`crash-diagnostics`, `mod-dissection`) and procedural-write skills (any skill that prescribes a write operation that produces a user-visible artifact).

**Required composition (outcome correctness eval set):**
- ≥ 8 input cases with known-correct outcomes. Seed corpora to date:
  - **`crash-diagnostics`:** v2.9.4 100% real-world crash-diagnosis record (per Q3 lock + REBUILD_RATIONALE.md Signal 1; the v2.9.4 successful diagnoses are the canonical seed corpus — Aaron's locked claim is that v2.9.4 hit 100% on real-world crashes despite the systemic silent-wrong-answer problem)
  - **`mod-dissection`:** Phase 3.2 dev/research authors a corpus of known mod-quality assessments (cross-mod conflicts, override patterns, ESL flag stickiness) with reviewer-validated reference outputs
  - Other Q3-scope skills: corpus authored fresh per skill at Phase 3.2 (or skill's land-phase) per the same shape
- Cases include both successful-diagnosis inputs (skill should reach correct outcome) and known-uncertain inputs (skill should explicitly say "I cannot — here's what I checked + what to investigate next" rather than emit a confident wrong answer)

**Pass criterion:** Skill must either emit the correct outcome OR explicit "I cannot — here's what I checked + what to investigate next" framing. **Silent wrong answers fail outright** (a confident answer that's wrong is the v2.9.5 failure mode this rebuild exists to prevent — it is worse than a clear non-answer per Q3 acceptance principle).

**Threshold:** [Aaron-decision pending — default candidate: 100% on seed corpus + 100% non-silent on uncertain inputs. Aaron may relax to e.g., 95%/95% if the seed-corpus shape demands it, or tighten to "any silent wrong answer = ship-blocker" for v1.0.] Threshold is checked at the eval-set's held-out test split per the §6.1 description-optimization-loop pattern, adapted for outcome scoring instead of trigger scoring.

**Cross-references:** §1 (why this standard exists), FOUNDATION.md §"v1.0 acceptance principle — 'no silent wrong answers'", VISION_ALIGNMENT_PROGRESS.md §Q3 + §Q5.5 + §Q5.6, REBUILD_RATIONALE.md Signal 1.
---

## 7 — Common mistakes from Claude_MO2 + how this standard prevents them

This section names every observed Claude_MO2 skill failure mode and the specific rule above that prevents recurrence. A reviewer auditing a proposed Housecarl skill walks this table and confirms each row's "fixed by" rule passes.

| Old project failure mode | Where observed | Fixed by rule |
|---|---|---|
| Trigger phrased as a meta-condition Claude can't evaluate ("sessions involving extensive MCP work") | `<old-repo>/.claude/skills/session-strategy/SKILL.md` (pre-v2.9.5) | § 3.2 (concrete user-utterance triggers; ≥ 3 named topics minimum) |
| Description leads with internal version marker ("v2.9.2 batch read mode") | Multiple `mo2_record_detail` parameter descriptions (pre-v2.9.5); same anti-pattern would have applied to skills if not flagged | § 3.1 (action-first formula) + § 3.4 (forbidden-content list) |
| Description uses vague qualifiers ("complex", "extensive", "non-trivial") | Multiple Claude_MO2 v2.9.4 skills (general pattern) | § 3.4 (vague qualifiers explicitly forbidden) |
| Skill body duplicates tool schema content (`kb/KB_Tools.md`) | Retired in v2.9.5 — the duplication source itself was a doc, not a skill, but the same trap applies | § 4.5 (no verbatim tool-description copies) |
| Body mixes changelog into instructions ("v2.9.2 added formids batching...") | General Claude_MO2 doc/skill pattern | § 4.5 (forbidden) |
| Description never empirically validated; first feedback comes from production failure | `session-strategy` 2026-04-29 failure (consumer ran 3,500 calls) | § 6 (validation gate is a precondition, not a follow-up) |
| Skill name embeds version or project name | Hypothetical for Housecarl if not flagged early | § 2 (naming rules) |
| Skill is really a tool wrapper (one tool call, no decision logic) | Hypothetical anti-pattern; would emerge if architecture standard absent | § 5.3 (decision tree + anti-pattern list) |
| Skill body exceeds 500 lines of monolithic content | Hypothetical | § 4.1 (hard cap + progressive disclosure to `references/`) |
| All-caps MUST/NEVER imperative stack | Common in lower-quality skill drafts | § 4.3 (imperative + reasoning, not all-caps stacked) |
| Skill emits confident output without flagging uncertainty (silent wrong answer) | v2.9.4 `session-strategy` 2026-04-29 episode (3,500 sequential `mo2_record_detail` calls) — Q3 acceptance principle (FOUNDATION.md) was authored in response to this | § 6.6 Q3 conformance scoring + skill body authors per Q3 lock acceptance principle ("emit correct outcome OR explicit I-cannot") |

---

## 8 — Skill-shipping checklist (reviewer-binding)

**Authoring-time prerequisite (per § 1):** `anthropic-skills:skill-creator` was invoked at the start of the authoring or editing session. Reviewer confirms verbally; lack of confirmation = re-author with the skill loaded.

Before a Housecarl skill merges to main:

1. ☐ Skill folder name conforms to § 2.
2. ☐ Frontmatter includes `name:` set **equal to the folder name** (§ 2) — Codex requires it; a missing or mismatched `name:` fails review.
3. ☐ Description leads with action verb phrase (§ 3.1).
4. ☐ Description "Use when..." contains ≥ 3 user-utterance trigger nouns (§ 3.2).
5. ☐ Description ends with one of the pushy reinforcement tail patterns (§ 3.3).
6. ☐ Description is free of all forbidden items in § 3.4.
7. ☐ Combined description + `when_to_use` is ≤ 1,536 characters (§ 3.5).
8. ☐ Body is ≤ 500 lines (hard cap, § 4.1).
9. ☐ Body H1 matches folder name in Title Case (§ 4.2).
10. ☐ Body uses standard sections in standard order (§ 4.2).
11. ☐ Body is in imperative voice; rationale accompanies any prescriptive instruction (§ 4.3).
12. ☐ Body cites tools by full name in backticks; references `<old-repo>` only if not in shipped surface (§ 4.4).
13. ☐ Body contains none of the forbidden patterns in § 4.5.
14. ☐ Skill is one of the three archetypes in § 5.2 (or has explicit justification for new shape).
15. ☐ Skill is not an anti-pattern from § 5.3.
16. ☐ Eval set ≥ 8 should-trigger + ≥ 8 should-not-trigger, including near-misses, all queries plausible utterances (§ 6.2).
17. ☐ Empirical validation (or documented manual fallback per § 6.5) shows recall ≥ 80%, specificity ≥ 50% (§ 6.3).
18. ☐ Eval set archived alongside the skill (e.g., `.claude/skills/<name>/evals/eval_set.json`) so future re-validation is reproducible.
19. ☐ Skill added to the `$Skills` array in `scripts/build-plugin.ps1` so it bundles into the plugin — and thus installs for **both** Claude Code and Codex. A skill absent from that list ships to neither host.

A skill failing any of items 1-15 or 19 fails review on the spot. Items 16-18 may be deferred only with explicit Aaron sign-off and a tracked follow-up.

---

## 9 — Sources

External:
- Anthropic Claude Code skills doc: <https://code.claude.com/docs/en/skills>
- Agent Skills open standard: <https://agentskills.io>
- `anthropic-skills:skill-creator`'s SKILL.md (introspected at `~/AppData/Roaming/Claude/local-agent-mode-sessions/skills-plugin/.../skills/skill-creator/SKILL.md`)
- Anthropic-bundled `pdf` and `docx` skills (same path tree) — reference shape for action-first descriptions and progressive disclosure to `references/`

Empirical anchors (Claude_MO2):
- Failure case (canonical): `<old-repo>/.claude/skills/session-strategy/SKILL.md` — pre-v2.9.5 description shape that failed for months
- Procedural skill reference: `<old-repo>/.claude/skills/leveled-list-patching/SKILL.md`
- Router skill reference: `<old-repo>/.claude/skills/npc-analysis/SKILL.md`
- Operational skill reference (post-fix): `<old-repo>/.claude/skills/session-strategy/SKILL.md` (current state)
- v2.9.5 plan archive (the reactive fix story): `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/PLAN.md`
- v2.9.5 ship handoff (validation environment caveat): `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/PHASE_1_HANDOFF.md`
- v2.9.5 eval set (reference shape): `<old-repo>/dev/plans/v2.9.5_descriptions_redesign/eval_set.json`

Strategic context:
- `<housecarl>/REBUILD_RATIONALE.md` § Signal 1
- `<housecarl>/FOUNDATION.md` § Vision (the user-facing surface is what the skills mediate)

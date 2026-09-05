# Skill authoring

houseCARL ships one set of skill sources to two hosts: Claude Code and OpenAI Codex. The source of
truth is `.claude/skills/<name>/SKILL.md` in this repository; the build produces one package per
host from it. Host difference is a first-class axis here: every rule says which host it holds for,
and where the hosts differ it says what the bundle does so both are satisfied.

Every rule cites the page it rests on. A rule that rests on no page is labelled **House choice** and
carries its reason in the same breath. Every house choice is repeated in one list at the end, with
its rule number. Every URL cited is listed after that.

## What a skill is for

1. **A skill is for doing things, not knowing things.** Both hosts put only a skill's name and
   description in front of the model at decision time and read the body after the skill is chosen.
   Claude Code: "Name and description at session start; full body loads when the skill is invoked"
   (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more). Anthropic
   platform: "At startup, the name and description from all Skills' YAML frontmatter are loaded into
   the system prompt"
   (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices). Codex builds
   the same listing and drops from it under pressure: "Codex may omit some skills from the initial
   list and show a warning" (https://learn.chatgpt.com/docs/build-skills.md). **House choice:**
   anything the agent must know *in order to decide* — that a capability exists at all, which tool
   family owns a job, a constraint that changes whether the work is attempted — does not go in a
   skill, because by the time the body is read the decision is made. The mechanism is cited above;
   the rule drawn from it is ours.

2. **Knowledge needed before the decision goes in the host's always-on file.** Both hosts, different
   files. Claude Code: `CLAUDE.md` — "Best for: 'Always do X' rules"; "**Put it in CLAUDE.md** if
   Claude should always know it" (https://code.claude.com/docs/en/features-overview.md), holding
   "facts Claude should hold in every session: build commands, conventions, project layout, 'always
   do X' rules" (https://code.claude.com/docs/en/memory.md). Codex: `AGENTS.md`, which "Codex reads …
   before doing any work" (https://learn.chatgpt.com/docs/agent-configuration/agents-md.md) and which
   is "an open-format README for agents" covering layout, commands, conventions, constraints and
   verification (https://learn.chatgpt.com/guides/best-practices). Where the fact binds only part of
   the tree, Claude Code scopes it with a `paths:` rule in `.claude/rules/`
   (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more) and Codex
   with a nested `AGENTS.md`: "Put repository-wide checks at the root and service-specific checks in
   a nested file" (https://learn.chatgpt.com/docs/agent-configuration/agents-md.md).

3. **The always-on file stays short and factual; a procedure that has grown there moves into a
   skill.** Both hosts. Claude Code: create a skill "when a section of CLAUDE.md has grown into a
   procedure rather than a fact", and "Keep CLAUDE.md under 200 lines"
   (https://code.claude.com/docs/en/skills.md;
   https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more). Codex: "Once
   a workflow becomes repeatable, stop relying on long prompts or repeated back-and-forth. Use a
   skill to package the instructions in a `SKILL.md` file"; "A short, accurate AGENTS.md is more
   useful than a long file full of vague rules" (https://learn.chatgpt.com/docs/codex-manual.md;
   https://learn.chatgpt.com/guides/best-practices). The reason is cost on both sides: "Every line
   loads into every session for every engineer working in the repo, whether it's relevant to their
   task or not" (blog), and Codex caps the file — "`project_doc_max_bytes` (32 KiB by default)"
   (https://learn.chatgpt.com/docs/agent-configuration/agents-md.md).

4. **Reference material the agent needs only sometimes belongs in a skill.** Claude Code states it:
   "**Put it in a skill** if it's reference material Claude needs sometimes (API docs, style
   guides)"; "long reference material costs almost nothing until you need it"
   (https://code.claude.com/docs/en/features-overview.md; https://code.claude.com/docs/en/skills.md).
   The pairing the docs name is the one the bundle uses: "CLAUDE.md says 'follow our API
   conventions,' a skill contains the full API style guide"
   (https://code.claude.com/docs/en/features-overview.md). **House choice:** use the same layout on
   Codex. Codex says only that a skill packages "the instructions in a `SKILL.md` file, context, and
   supporting logic" (https://learn.chatgpt.com/docs/codex-manual.md) and nothing on the Codex side
   argues against the split.

5. **Anything that must happen every time is a hook, not a skill.** Both hosts. Claude Code states
   the difference as determinism: a hook "Always fires on its event; the trigger is guaranteed"; a
   skill means "Claude interprets the instructions; outcome can vary"
   (https://code.claude.com/docs/en/features-overview.md), and "When there's something that
   absolutely must not happen, an instruction is the wrong tool… A real guardrail needs to be
   deterministic, and the enforcement methods are hooks and permissions"
   (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more). Codex says
   the same by mechanism: hooks run on lifecycle events and the hooks page never uses the word skill
   (https://learn.chatgpt.com/docs/hooks.md). A houseCARL skill never states a rule whose value
   depends on it firing every time.

6. **houseCARL skills carry no hooks.** **House choice.** Claude Code allows them — "**Skill
   hooks**: Claude Code registers them when you or Claude invoke the skill and keeps running them for
   the rest of the session… All hook events are supported"
   (https://code.claude.com/docs/en/hooks.md) — and Codex has no per-skill equivalent: its bundling
   unit for hooks is the plugin, `hooks/hooks.json` at the plugin root
   (https://learn.chatgpt.com/docs/hooks.md). Reason: one tree ships to both hosts, so a
   skill-carried hook is behaviour that exists on one host only, and the trust rules differ as well
   (Claude Code registers project-skill frontmatter hooks "including in a `-p` run in a folder you
   haven't trusted"; Codex "skips plugin-bundled hooks until you review and trust the current hook
   definition").

7. **Work that needs isolation is a subagent's job; a skill stays a procedure.** Claude Code keeps
   the two apart: "**Skills** are reusable content you can load into any context; **Subagents** are
   isolated workers that run separately from your main conversation"
   (https://code.claude.com/docs/en/features-overview.md), and "Use a skill when you want the
   procedure to play out inside the main thread so you can see and steer each step"
   (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more). **House
   choice:** where a job wants isolation the skill says so in the procedure rather than becoming a
   worker definition. Reason: Claude Code can preload a named skill into a subagent — "The full
   content of each listed skill is injected into the subagent's context at startup"
   (https://code.claude.com/docs/en/sub-agents.md) — and no Codex page describes preloading a named
   skill into a subagent, so a skill that assumed it would work on one host only.

8. **No skill depends on memory.** Both hosts. Claude Code's auto memory "skips anything your
   CLAUDE.md files already say" (https://code.claude.com/docs/en/memory.md); Codex: "Treat memories
   as a helpful recall layer, not as the only source for rules that must always apply" and "Treat
   these files as generated state" (https://learn.chatgpt.com/docs/customization/memories.md). A
   skill that only works because a prior session wrote something down fails on a fresh machine.

9. **One skill, one job.** Both hosts. "Keep each skill focused on one job."
   (https://learn.chatgpt.com/docs/build-skills.md); "Prefer one focused skill over a large
   collection of loosely related instructions. Split workflows when they have different triggers,
   inputs, or success criteria." (https://developers.openai.com/plugins/build/skills.md); the spec's
   form is "encapsulate a coherent unit of work that composes well with other skills", warning that
   skills "scoped too narrowly force multiple skills to load for a single task" and skills "scoped
   too broadly become hard to activate precisely"
   (https://agentskills.io/skill-creation/best-practices.md).

### Where houseCARL's own knowledge goes

| Kind of content | Claude Code | Codex |
|---|---|---|
| Facts true of every session — the naming rule `housecarl_<snake_case>`, the design cornerstones, build and test commands | `CLAUDE.md` | `AGENTS.md` |
| A constraint that binds only part of the tree | `.claude/rules/` with `paths:` | a nested `AGENTS.md` |
| That houseCARL exists and what it reaches — needed before the agent decides to act | standing context and the MCP server's own tool descriptions | same |
| A repeatable method for one job, with its lookup data | a skill | a skill |
| Something that must happen every time | a hook outside the bundle | a hook outside the bundle |

The third row is a **house choice**: no vendor page addresses a skill bundle sitting on top of an MCP
server, and the reason is rule 1 — a skill cannot advertise a capability the agent has not yet chosen
to use.

### Where the sources are silent or disagree

- **A published where-it-belongs map exists on one side only.** Claude Code publishes the seven-method
  table with load timing, compaction behaviour, context cost and best-for per method
  (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more). The Agent
  Skills spec and the Anthropic platform pages publish none; Codex publishes the pieces across
  separate pages but no single map. The bundle follows Claude Code's map and checks each row against
  the matching Codex page, because it is the only published one.
- **Slash commands and output styles are Claude Code categories with no Codex counterpart.** OpenAI's
  portal path: "Turn each Markdown command into a skill", and for `outputStyles` and five other
  declarations, "Move essential behavior into skills, then remove the Claude declaration"
  (https://developers.openai.com/plugins/guides/submit-claude-plugin). The bundle uses neither, so
  nothing has to be converted.
- **Standing context has an override file on one side.** Codex: "`AGENTS.override.md` if it exists.
  Otherwise, Codex reads `AGENTS.md`", and "Files closer to your current directory override earlier
  guidance because they appear later in the combined prompt"
  (https://learn.chatgpt.com/docs/agent-configuration/agents-md.md). Claude Code documents a
  hierarchy but no override file and no stated conflict rule between levels.
- **Nobody says what happens when a `CLAUDE.md` and an `AGENTS.md` are both present in one
  repository.** All three families are silent. **House choice:** keep the two files saying the same
  facts rather than dividing them, because a skill that behaves differently per host is a bug and
  there is no documented rule to appeal to.
- **Anthropic disagrees with itself about whether a shipped skill may compose with others.** The Help
  Center: "While skills can't explicitly reference other skills, Claude can use multiple skills
  together automatically" (https://support.claude.com/en/articles/12512198-creating-custom-skills);
  Claude Code documents explicit chaining, "Up to six skills can be chained"
  (https://code.claude.com/docs/en/commands.md); Codex is silent on composition and does not merge
  same-named skills. **House choice:** a houseCARL skill is written to stand alone and may name
  another skill as a next step, but never assumes the other skill's body is loaded. Reason: only
  Claude Code documents chaining, and Codex is where the assumption would break. See also rule 71.

## Where skills live and how the bundle ships

10. **A skill is a directory whose entry point is a file named exactly `SKILL.md`.** Both hosts.
    Claude Code: "`~/.claude/skills/<skill-name>/SKILL.md`"
    (https://code.claude.com/docs/en/skills.md). Codex: "A skill is a directory with a `SKILL.md`
    file plus optional scripts and references" (https://learn.chatgpt.com/docs/build-skills). A
    `SKILL.md` with no directory around it is not a skill on either host — Anthropic:
    "`.claude/skills/SKILL.md`: a `SKILL.md` with no skill directory around it"
    (https://platform.claude.com/docs/en/managed-agents/skills); Codex: "`skill_file_ignored` - Files
    directly under `skills/` aren't imported as skills"
    (https://developers.openai.com/plugins/deploy/submission-errors.md).

11. **The folder name is kebab-case and is the same string as the frontmatter `name`.** **House
    choice**, on top of `standards/NAMING.md` ("Skill folder | `kebab-case/`"). The sources do not
    agree: the reference validator requires the match — "Directory name '{skill_dir.name}' must match
    skill name '{name}'"
    (https://raw.githubusercontent.com/agentskills/agentskills/main/skills-ref/src/skills_ref/validator.py)
    — but that library is "intended for demonstration purposes only. It is not meant to be used in
    production"
    (https://raw.githubusercontent.com/agentskills/agentskills/main/skills-ref/README.md); Claude
    Code takes the invocation name from frontmatter `name` and falls back to the directory basename
    (https://code.claude.com/docs/en/plugins-reference.md); Codex states no rule. Reason: one string,
    so the folder on disk and the name a user types cannot drift apart.

12. **A skill folder name carries no version number, and does not change when the skill is
    rewritten.** **House choice**; `standards/NAMING.md` ("No version numbers in names"). Anthropic's
    skill-creator states the same as practice — "**Preserve the original name.** … if the installed
    skill is `research-helper`, output `research-helper.skill` (not `research-helper-v2`)"
    (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md).
    Reason: the name is what a user types, and on Claude Code a rename has to be carried forward by
    an append-only marketplace `renames` map
    (https://code.claude.com/docs/en/plugin-marketplaces.md).

13. **Supporting files sit in `references/` for documentation, `scripts/` for executable code, and
    `assets/` for templates and data files, inside the skill folder.** Codex documents exactly this
    tree (https://learn.chatgpt.com/docs/build-skills.md) and so does the spec, which adds "Any
    additional files or directories" (https://agentskills.io/specification). Claude Code names none
    of these directories — its examples show `reference.md`, `examples.md`, `scripts/helper.py` at
    the skill root (https://code.claude.com/docs/en/skills.md). **House choice:** use the three
    spec and Codex names on both hosts, because Codex names them and Claude Code has no competing
    convention to violate. Anthropic's own shipped skills break this — `pdf` puts `reference.md` and
    `forms.md` at the skill root, `mcp-builder` uses `reference/` singular, and the platform's
    good-practice example writes `reference/finance.md`
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices). That is
    practice, not rule, and not the practice to copy.

14. **Each skill carries its own eval set at `evals/eval_set.json` inside the skill folder.** **House
    choice**; no page on either side names an evals folder as a requirement. The documented filename
    is different: Anthropic's schemas and the spec both name `evals/evals.json` for outcome evals
    (skill-creator `references/schemas.md`; https://agentskills.io/specification), and the only other
    trace is Anthropic's packager excluding `evals/` from a `.skill` zip "only at the skill root"
    (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/scripts/package_skill.py),
    which shows the location is a convention rather than a requirement. Reason for keeping the
    bundle's own filename: the tests travel with the thing they test, the exclusion rule means they
    cost nothing at packaging time, and nothing reads the file but us. What the file holds is rule
    123.

15. **A Codex-only sidecar lives at `skills/<skill>/agents/openai.yaml`, one directory level inside
    the skill.** Codex only: "A bundled skill can define its own `interface` in
    `skills/<skill>/agents/openai.yaml`. This controls how the skill appears to users and is separate
    from the plugin manifest's `interface`" and "`skill_metadata_ignored` - Skill interface settings
    must use the `interface` mapping in `agents/openai.yaml`; `metadata` in `SKILL.md` doesn't
    configure the interface" (https://developers.openai.com/plugins/deploy/submission-errors.md).
    Claude Code has no per-skill sidecar and puts the equivalents in frontmatter; the file is inert
    there, so it ships in both packages. Its fields are rules 54 to 57.

16. **A skill folder name must not begin with `.`, and `synced` is not available as one.** Claude
    Code: "The folder name `synced` is reserved in the enterprise, personal, and project skills
    locations, in any capitalization" (https://code.claude.com/docs/en/skills.md). Codex, at
    submission: "`skill_directory_hidden` - Skill directory names must not begin with `.`"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Kebab-case satisfies both.

17. **Claude Code discovers skills at four levels, and a name collision has a winner.** Claude Code
    only. The levels are enterprise, personal `~/.claude/skills/<skill-name>/SKILL.md`, project
    `.claude/skills/<skill-name>/SKILL.md`, and plugin `<plugin>/skills/<skill-name>/SKILL.md`.
    "Across levels, enterprise overrides personal, and personal overrides project" — with a `deploy`
    skill in both personal and project locations, "`/deploy` runs the personal one"
    (https://code.claude.com/docs/en/skills.md).

18. **A plugin's skills are namespaced by the plugin name and cannot collide with the other levels.**
    Claude Code only: "Plugin skills use a `plugin-name:skill-name` namespace, so they can't conflict
    with other levels", and a plugin `deploy` "loads alongside a `deploy` skill in your project's
    `.claude/skills/`" (https://code.claude.com/docs/en/skills.md). This is why the bundle ships as a
    plugin on Claude Code: the namespace is the collision answer.

19. **Codex discovers skills in four scopes, and a name collision has no winner.** Codex only:
    "Codex reads skills from repository, user, admin, and system locations. For repositories, Codex
    scans `.agents/skills` in every directory from your current working directory up to the
    repository root. If two skills share the same `name`, Codex doesn't merge them; both can appear
    in skill selectors." The documented rows are `$CWD/.agents/skills`, `$CWD/../.agents/skills`,
    `$REPO_ROOT/.agents/skills`, `$HOME/.agents/skills`, `/etc/codex/skills`, and skills bundled with
    Codex by OpenAI (https://learn.chatgpt.com/docs/build-skills.md).

20. **Every skill name in the bundle is distinctive on its own.** **House choice**, forced by rule
    19. Codex's only stated uniqueness guarantee is inside one plugin — "`skill_identity_duplicate` -
    Each skill `name` must be unique within the plugin" — and it documents `plugin-name:skill-name`
    only as a 64-character identity cap, stating no rule that the prefix prevents a collision with a
    local skill (https://developers.openai.com/plugins/deploy/submission-errors.md). Where two skills
    of the same name both load, the user tells them apart by path: "In Codex, the initial list also
    includes each skill's file path" (https://learn.chatgpt.com/docs/build-skills). So a skill name
    says what the skill does specifically enough to survive without a prefix: `facegen-diagnostics`,
    not `diagnostics`.

21. **The bundle lands at `~/.claude/skills/housecarl/` on Claude Code and under `~/.agents/skills/`
    on Codex.** Both hosts, one landing place each. On Claude Code the installer copies the whole
    bundled plugin folder — skills, server, corpus — to `~/.claude/skills/housecarl/`, and the
    package carries a `.claude-plugin/marketplace.json` so the folder can be added with `claude
    plugin marketplace add <folder>`; the skills are invoked as `/housecarl:<name>`. On Codex, which
    scans `~/.agents/skills/` for skill folders, the fourteen skills are copied there flat, one
    folder each, with the umbrella beside them as `~/.agents/skills/housecarl/`, and the server and
    corpus go to a neutral per-user directory rather than the skills directory.

22. **The umbrella skill stands in for the namespace Codex does not give us.** **House choice.**
    Codex has no plugin namespace for locally installed skills (rule 20), and its scope table has no
    admin- or system-level place for a vendor bundle short of `/etc/codex/skills`
    (https://learn.chatgpt.com/docs/build-skills.md). Reason: a single named entry point that points
    at the rest keeps one recognisable name in the Codex skill list instead of fourteen unprefixed
    ones.

23. **Do not assume Codex reads the Claude Code skill tree.** Codex only, and this rests on a
    silence. The spec observes that "Some implementations also scan `.claude/skills/` (both
    project-level and user-level) for pragmatic compatibility"
    (https://agentskills.io/client-implementation/adding-skills-support.md), but Codex's own page
    lists only `.agents/skills` variants, `/etc/codex/skills`, and bundled skills, and states no rule
    about `.claude/skills` either way (https://learn.chatgpt.com/docs/build-skills). **House
    choice:** install a copy under `~/.agents/skills/` rather than relying on Codex to find the
    Claude tree. Reason: the behaviour is inferred, not confirmed.

24. **Both hosts pick up a changed skill without a restart, and both name a restart as the
    fallback.** Both hosts. Claude Code: adding, editing, or removing a skill under
    `~/.claude/skills/`, a project `.claude/skills/`, or an `--add-dir` directory is "picked up …
    within the current session, without a restart", but a newly created top-level skills directory
    needs a restart, and live detection "covers `SKILL.md` text only" — a skill folder that is also a
    plugin needs `/reload-plugins` for its `hooks/`, `.mcp.json`, `agents/`, and `output-styles/`
    (https://code.claude.com/docs/en/skills.md). Codex: "Codex detects skill changes automatically.
    If an update doesn't appear, restart Codex" (https://learn.chatgpt.com/docs/build-skills.md).
    Since the Claude package is a plugin folder, treat a change to anything but `SKILL.md` text as
    needing `/reload-plugins`.

25. **A skill writes no state beside itself.** Both hosts run an installed plugin from a
    version-keyed cache. Claude Code: "`~/.claude/plugins/cache` … each installed version is a
    separate directory in the cache, grouped by marketplace and plugin and named for the resolved
    version", and of the plugin root, "treat it as ephemeral and don't write state there"
    (https://code.claude.com/docs/en/plugins-reference.md). Codex: "ChatGPT installs plugins into
    `~/.codex/plugins/cache/$MARKETPLACE_NAME/$PLUGIN_NAME/$VERSION/` … ChatGPT loads the installed
    copy from that cache path rather than directly from the marketplace entry"
    (https://developers.openai.com/plugins/build/plugins.md). Both give a separate writable data
    directory — `${CLAUDE_PLUGIN_DATA}` at `~/.claude/plugins/data/{id}/`
    (https://code.claude.com/docs/en/plugins-reference.md) and Codex's `PLUGIN_DATA`
    (https://learn.chatgpt.com/docs/hooks.md).

26. **The manifest alone goes in the dotted directory; everything else sits at the plugin root.**
    Both hosts, in the same words. Claude Code: "Don't put `commands/`, `agents/`, `skills/`, or
    `hooks/` inside the `.claude-plugin/` directory. Only `plugin.json` goes inside
    `.claude-plugin/`. All other directories must be at the plugin root level."
    (https://code.claude.com/docs/en/plugins.md). Codex: "Only `plugin.json` belongs in
    `.codex-plugin/`. Keep `skills/`, `hooks/`, `assets/`, `.mcp.json`, and `.app.json` at the plugin
    root." (https://developers.openai.com/plugins/build/plugins.md).

27. **Every skill sits at `skills/<name>/SKILL.md`, one level under `skills/`.** Both hosts. Claude
    Code: "Skills live in the `skills/` directory. Each skill is a folder containing a `SKILL.md`
    file." (https://code.claude.com/docs/en/plugins.md). Codex: "A skills-only ZIP must contain a
    supported plugin manifest and at least one valid skill at `skills/<skill>/SKILL.md`" and
    "`skill_manifest_nested` - Each skill directory must be an immediate child of `skills/`"
    (https://developers.openai.com/plugins/deploy/submission-errors.md).

28. **Do not use the Claude Code shortcuts Codex rejects: a root-level `SKILL.md`, or flat `.md`
    files in `commands/`.** **House choice** on a real difference. Claude Code allows both — "A
    plugin that ships exactly one skill can place `SKILL.md` directly at the plugin root instead of
    creating a `skills/` directory" (https://code.claude.com/docs/en/plugins.md), and "The
    `commands/` directory holds skills as flat Markdown files. Use `skills/` for new plugins."
    (https://code.claude.com/docs/en/agent-sdk/plugins.md). Codex accepts neither —
    `skill_manifest_nested` and `skill_file_ignored`
    (https://developers.openai.com/plugins/deploy/submission-errors.md) — and a Claude plugin's
    `commands/` has to be rewritten: "Turn each Markdown command into a skill"
    (https://developers.openai.com/plugins/guides/submit-claude-plugin). Reason: one layout that both
    hosts accept beats two layouts and a conversion step.

29. **Manifest paths are relative to the plugin root, start with `./`, and never point outside it.**
    Both hosts. Claude Code: paths "must be relative to the plugin root and start with `./`"
    (https://code.claude.com/docs/en/plugins-reference.md); a component path that is a symlink out of
    the plugin directory is "refused with an error" (Claude Code CHANGELOG v2.1.257). Codex: "Keep
    manifest paths relative to the plugin root and start them with `./`" and hook paths "must stay
    inside that root" (https://developers.openai.com/plugins/build/plugins.md).

30. **Keep the default `skills/` layout so the manifest's `skills` field is not load-bearing.**
    **House choice** on a difference the sources do not reconcile. Claude Code treats `skills` as
    additive — "**Adds to the default**: `skills`. The default `skills/` directory is always scanned,
    and directories listed in `skills` are loaded alongside it" — and accepts `"."`
    (https://code.claude.com/docs/en/plugins-reference.md). Codex constrains it to one string
    resolving to the root: "`plugin_skills_path_wrong_type` - `skills` must be a string path for the
    root `skills/` directory" and "`plugin_skills_path_unsupported` - `skills` must resolve to the
    root `skills/` directory" (https://developers.openai.com/plugins/deploy/submission-errors.md);
    Codex's own minimal example is `"skills": "./skills/"`
    (https://learn.chatgpt.com/docs/build-plugins.md). No page says whether the portal's conversion
    fixes a Claude-shaped value. Reason: with everything in the default `skills/`, the Claude
    manifest can omit the field and the Codex manifest can carry the single string Codex requires,
    and neither host relies on undocumented behaviour.

31. **Skill folders in a shipped bundle are real directories, not symlinks.** **House choice** on a
    within-vendor contradiction. Codex's local runtime follows them — "Codex supports symlinked skill
    folders and follows the symlink target when scanning these locations"
    (https://learn.chatgpt.com/docs/build-skills) — while Codex's submission validation ignores them:
    "`skill_symlink_ignored` - Symbolic links directly under `skills/` aren't imported as skills;
    each skill must be a real directory containing `SKILL.md`"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Reason: a layout that only
    works before packaging is a layout that fails at packaging. Claude Code follows symlinks at
    discovery and loads a doubly-reachable target once
    (https://code.claude.com/docs/en/skills.md), so real directories cost nothing there.

32. **The Codex package carries skills only; the Claude package carries the server too, and both are
    built from the one source tree.** Codex only, with a consequence for both:
    "`mcp_configuration_excluded` - Skills-only ZIP uploads must not include `mcpServers` or
    `.mcp.json`; MCP-backed plugins must use **With MCP**"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Claude Code makes no such
    exclusion; a plugin mixes `skills/`, `.mcp.json`, `hooks/`, and `agents/` freely
    (https://code.claude.com/docs/en/agent-sdk/plugins.md). **House choice** that follows:
    `.claude/skills/` stays the single source and each package is generated from it, never edited per
    host.

33. **A skill calls its own bundled files by package-relative paths and depends on nothing outside
    the package.** Codex states it for a converted Claude plugin — "Call bundled executables with
    package-relative paths, and remove Claude-only settings", and after conversion "Confirm that each
    skill can find its referenced files and executables and doesn't depend on undeclared local
    packages, files, or credentials" (https://developers.openai.com/plugins/guides/submit-claude-plugin).
    It holds on Claude Code too, where the plugin root is a cache directory that changes on every
    update (rule 25).

34. **Know what conversion drops, and do not depend on the import flow.** Codex only. A direct Claude
    archive upload keeps `.claude-plugin/plugin.json` — "The portal converts it to
    `.codex-plugin/plugin.json`" — but the conversion normalises: "The portal adds missing interface
    defaults and normalizes text fields during conversion." Not carried at all:
    `.claude-plugin/marketplace.json`, `.mcp.json`, `mcpServers`, `.app.json`, `apps`. Six manifest
    declarations must be removed — `outputStyles`, `lspServers`, `experimental.themes`,
    `experimental.monitors`, `channels`, `dependencies` — with "Move essential behavior into skills,
    then remove the Claude declaration"; `commands`/`agents` must be converted to skills; "OpenAI
    doesn't run Claude installation prompts or expand `user_config` variables"; and "Claude
    marketplace listings and approvals don't transfer"
    (https://developers.openai.com/plugins/guides/submit-claude-plugin). The other route is the local
    import flow, which maps "Skills → Skills", "Plugins → Plugins", "Slash commands → Skills"
    (https://learn.chatgpt.com/docs/codex-manual.md, https://learn.chatgpt.com/docs/import); no
    OpenAI page says whether an imported Claude skill is copied verbatim or rewritten, or how it is
    namespaced at invocation time. **House choice:** ship a Codex package (rule 21) rather than rely
    on either path. Reason: both are stated to change the thing they carry.

35. **The bundle carries an explicit semantic version in its manifest, and every release changes
    it.** Both hosts. Claude Code: "follow semantic versioning (`MAJOR.MINOR.PATCH`): bump MAJOR for
    breaking changes, MINOR for new features, PATCH for bug fixes"; setting `version` "pins the
    plugin to that version string, so users only receive updates when you bump it", and if it is also
    set in the marketplace entry, "`plugin.json` wins"
    (https://code.claude.com/docs/en/plugins-reference.md). Codex: "`plugin_version_not_semver` -
    `version` must use semantic versioning, such as `1.0.0`" and "`plugin_version_unchanged` - A new
    release must use a different manifest `version`"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Claude Code would accept no
    version at all, falling back to a commit SHA, a SHA-256 digest, or `unknown` (same page); the
    bundle does not use that mode, because Codex rejects it.

36. **A Claude Code release is tagged `housecarl--v<version>`.** Claude Code only: "Tag each release
    as `{plugin-name}--v{version}`, where `{version}` matches the `version` field in that commit's
    `plugin.json`", created with `claude plugin tag --push`, which "validates the plugin contents,
    checks that `plugin.json` and the marketplace entry agree on the version, requires a clean
    working tree under the plugin directory, and refuses if the tag already exists"
    (https://code.claude.com/docs/en/plugin-dependencies.md). Codex documents no git tag convention,
    and the tag is harmless there.

37. **Skills have no version of their own; they version with the bundle.** Both hosts. Claude Code
    has no per-skill version, and `compatibility` is accepted but "Claude Code … doesn't act on it";
    there is "No documented way to declare a *minimum Claude Code version* a skill requires" and no
    dependency mechanism between skills, only between plugins
    (https://code.claude.com/docs/en/skills.md). Codex's per-skill version numbers exist only on the
    API surface, not for a bundled skill
    (https://developers.openai.com/api/docs/guides/tools-skills.md). A skill's compatibility
    statement, where one is needed, is prose in the skill.

38. **User-visible changes go in `plugin/CHANGELOG.md` under `## Unreleased` until a release names
    them.** **House choice**; the documentation is one sentence long. Claude Code says "Document
    changes in a `CHANGELOG.md`" and nothing more — no location, no format, and no statement that
    anything reads it (https://code.claude.com/docs/en/plugins-reference.md). The Agent Skills spec,
    the Anthropic platform docs, and every Codex page are silent on a changelog. Reason: the file is
    for people, so it lives where the shipped package lives and says what a user would notice,
    matching the repository's existing practice.

### Where the hosts differ

| Point | Claude Code | Codex | What the bundle does |
|---|---|---|---|
| User-scope skills directory | `~/.claude/skills/` (https://code.claude.com/docs/en/skills.md) | `$HOME/.agents/skills` (https://learn.chatgpt.com/docs/build-skills.md) | Installs to each; one source tree, two packages (rules 21, 32) |
| Name collision | Resolved: enterprise > personal > project; plugin skills namespaced (https://code.claude.com/docs/en/skills.md) | Not resolved: "both can appear in skill selectors" (https://learn.chatgpt.com/docs/build-skills.md) | Names distinctive without a prefix; an umbrella skill carries the identity (rules 20, 22) |
| Manifest directory | `.claude-plugin/plugin.json`, optional (https://code.claude.com/docs/en/agent-sdk/plugins.md) | `.codex-plugin/plugin.json`, required (https://developers.openai.com/plugins/build/plugins.md) | Always ships a manifest; the Codex package's is the converted or generated one (rules 26, 34) |
| Manifest `skills` field | Additive list, accepts `"."` (https://code.claude.com/docs/en/plugins-reference.md) | One string resolving to root `skills/` (https://developers.openai.com/plugins/deploy/submission-errors.md) | Default `skills/` layout, so the field is not load-bearing (rule 30) |
| Root `SKILL.md` / flat `commands/` | Both allowed (https://code.claude.com/docs/en/plugins.md) | Neither allowed (https://developers.openai.com/plugins/deploy/submission-errors.md) | Neither used (rule 28) |
| MCP wiring in the package | Free to mix (https://code.claude.com/docs/en/agent-sdk/plugins.md) | Excluded from a skills-only upload (https://developers.openai.com/plugins/deploy/submission-errors.md) | Server ships in the Claude package only (rule 32) |
| Version string | Optional; SHA or digest fallback (https://code.claude.com/docs/en/plugins-reference.md) | Required semver, must change per release (https://developers.openai.com/plugins/deploy/submission-errors.md) | Explicit semver always (rule 35) |
| Per-skill sidecar | None; frontmatter instead (Claude Code CHANGELOG v2.1.186) | `skills/<skill>/agents/openai.yaml` (https://developers.openai.com/plugins/deploy/submission-errors.md) | Sidecar ships in both packages; inert on Claude Code (rule 15) |

### Where the sources are silent or disagree

- **OpenAI contradicts itself on the user-scope skills path.** The scope table and the best-practices
  guide say `$HOME/.agents/skills`: "Personal skills are stored in `$HOME/.agents/skills`, and shared
  team skills can be checked into `.agents/skills` inside a repository"
  (https://learn.chatgpt.com/docs/codex-manual.md, https://learn.chatgpt.com/guides/best-practices).
  The use-case page instead stores skills in `~/.codex/skills`, and the app-server API examples use
  `"/Users/me/.codex/skills/skill-creator/SKILL.md"`
  (https://learn.chatgpt.com/use-cases/reusable-codex-skills.md,
  https://learn.chatgpt.com/docs/codex-manual.md); the `CODEX_HOME` entry — "Sets the root for Codex
  state, including config, auth, logs, sessions, skills" — is evidence for that side. **House
  choice: follow `$HOME/.agents/skills`,** because it is the path in the scope table on the page that
  documents discovery, and it is the cross-client convention the spec names: "The `.agents/skills/`
  paths have emerged as a widely-adopted convention for cross-client skill sharing"
  (https://agentskills.io/client-implementation/adding-skills-support.md).
- **OpenAI contradicts itself on symlinks under a skills directory:** the local runtime follows them,
  submission ignores them. Rule 31; the bundle follows the submission rule.
- **OpenAI contradicts itself on `.claude-plugin/marketplace.json`.** The Claude submission guide
  lists it among files not carried — "Don't rely on these files or declarations"
  (https://developers.openai.com/plugins/guides/submit-claude-plugin) — while the packaging page has
  the desktop app reading "a legacy-compatible marketplace at
  `$REPO_ROOT/.claude-plugin/marketplace.json`" and enterprise sync recognises "A Claude-compatible
  marketplace with a `plugins` array" (https://developers.openai.com/plugins/build/plugins,
  https://learn.chatgpt.com/docs/enterprise/plugin-management). **House choice: treat it as a Claude
  Code file only** and do not count on Codex reading it, because the guide that governs the direction
  we ship in — a Claude plugin going to Codex — is the one that says not to rely on it.
- **The Anthropic family gives three answers on collision handling.** agentskills.io: "project-level
  skills override user-level skills". code.claude.com: "enterprise overrides personal, and personal
  overrides project". platform.claude.com, for Managed Agents: "both are available; each is announced
  with its own path" (https://agentskills.io/client-implementation/adding-skills-support.md,
  https://code.claude.com/docs/en/skills.md, https://platform.claude.com/docs/en/managed-agents/skills).
  No page reconciles them. The bundle depends on none of them: a namespaced plugin on Claude Code
  (rule 18) and distinctive names on Codex (rule 20) mean the precedence question never has to be
  answered for our skills.
- **Nobody documents a changelog for a skill.** Claude Code's single sentence is for a plugin; the
  spec, the platform docs, and every Codex page are silent. House choice in rule 38.
- **Whether Codex reads `.claude/skills` locally is unstated**, so rule 23 is an inference from
  silence. The same applies to whether Codex's `plugin-name:skill-name` prefix prevents a collision
  with a local skill (rule 20) and to whether an imported Claude skill is copied verbatim (rule 34).
  In all three the bundle takes the conservative side.
- **Neither host documents a scan depth or directory-count bound.** Only the spec offers guidance —
  "Set reasonable bounds (e.g., max depth of 4-6 levels, max 2000 directories)"
  (https://agentskills.io/client-implementation/adding-skills-support.md). At fourteen skills, one
  level under `skills/`, nothing is close to a bound.
- **Claude Code states no archive size or entry limits; Codex states several.** For a submitted Codex
  package: "Compressed ZIP must be 100 MB or less", "Extracted archive must not exceed 512 MiB",
  "Archive must not contain more than 5,000 entries", "Archive entry path must contain at most 20
  segments, including the filename", and "Archive entry path must use `/`, not backslashes"
  (https://developers.openai.com/plugins/deploy/submission-errors.md). Only the last two are near a
  real constraint for a skill bundle, and rule 27's layout satisfies both.

## Frontmatter

Frontmatter is the only part of a skill both hosts parse mechanically. Claude Code documents twenty
fields; the Agent Skills specification allows six; Codex reads two and puts the rest in a sibling
file. A houseCARL skill writes **three** — `name`, `description`, `license` — so one `SKILL.md` is
valid on every surface the bundle reaches.

### The file and its frame

39. **The frontmatter is a YAML mapping opened and closed by `---`, and the opening `---` is the
    first line of the file.** Both hosts. Claude Code: "Claude Code reads the frontmatter only when
    the opening `---` is the file's first line. Otherwise it treats the whole file, `---` markers
    included, as skill content" (https://code.claude.com/docs/en/skills.md). Codex:
    `skill_frontmatter_missing` — "`SKILL.md` must start with YAML front matter between `---` lines";
    `skill_frontmatter_unclosed`; `skill_frontmatter_wrong_type` — "front matter must contain a YAML
    mapping" (https://developers.openai.com/plugins/deploy/submission-errors.md).

40. **The file is `SKILL.md`, uppercase, valid UTF-8, with no byte-order mark.** Both hosts name only
    `SKILL.md`; Codex adds `skill_manifest_invalid_utf8` — "`SKILL.md` must contain valid UTF-8"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). A BOM made Claude Code
    ignore the file silently until v2.1.239 fixed "agents, skills, and commands whose `.md` file
    starts with a UTF-8 BOM being silently ignored" (Claude Code CHANGELOG). **House choice:**
    uppercase and no BOM, because the reference parser accepts `skill.md` and the claude.ai Help
    Center spells it lowercase while both hosts name only the uppercase form — the uppercase spelling
    is the one every source accepts.

41. **A value containing a colon is quoted or written as a YAML block scalar.** Both hosts, via the
    shared YAML hazard: "The most common issue is unquoted values containing colons: `description:
    Use this skill when: the user asks about PDFs` — Technically invalid YAML — the colon breaks
    parsing" (https://agentskills.io/client-implementation/adding-skills-support.md). Claude Code's
    response to a parse failure is silent degradation: it "loads the skill body with empty metadata,
    so `/skill-name` still works but Claude has no `description` to match against", with the parse
    error visible under `--debug` (https://code.claude.com/docs/en/skills.md). Codex blocks the same
    file at submission with `skill_frontmatter_yaml_malformed`. A skill that stops triggering and
    still answers to its slash command has lost its frontmatter.

42. **Frontmatter values carry no internal line structure.** Codex only, but it constrains the
    bundle: "Skill `name` and `description` are normalized during import by trimming outer whitespace
    and collapsing internal whitespace" (`skill_frontmatter_adjusted`,
    https://developers.openai.com/plugins/deploy/submission-errors.md). No Anthropic page states any
    normalisation. A block scalar is a way to keep a long description readable in the file, not a way
    to give it lines that survive to the model.

### `name`

43. **Every houseCARL skill writes `name`, and its value is exactly the skill's folder name.**
    **House choice** — the sources disagree on whether the field is required at all (see the
    silences below). It is required on two of the three surfaces and free on the third: the
    specification says "Required: Yes" and "**Must match the parent directory name**"
    (https://agentskills.io/specification), the platform docs say "**Required fields:** `name` and
    `description`"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview), Codex says "`name`
    is required and must not be empty" (`skill_name_missing`), and Claude Code says "All fields are
    optional" but, for a plugin skill, uses it: `name` "sets the last segment of the command and the
    plugin prefix stays in place" (https://code.claude.com/docs/en/skills.md). Reason: one value
    satisfies all four and makes `/housecarl:<name>` on Claude Code, the skill's own name on Codex,
    and the folder on disk one string.

44. **`name` is ASCII kebab-case — `a`–`z`, `0`–`9`, hyphens; no leading or trailing hyphen; no
    `--` — and contains neither the words `anthropic` and `claude` nor `<` and `>`.** The
    specification states the character shape exactly (https://agentskills.io/specification), the
    platform docs state "Must contain only lowercase letters, numbers, and hyphens" and add that
    `name` "Cannot contain reserved words: \"anthropic\", \"claude\"" and "Cannot contain XML tags"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview, repeated at
    .../best-practices and .../skills-guide), and `standards/NAMING.md` already spells a skill folder
    `kebab-case/`. Claude Code states no character rule; Codex states only that the name "must use
    supported text" (`skill_name_character_unsupported`), and neither the specification page nor the
    reference validator implements the reserved-word rule. **House choice:** ASCII only, tighter than
    the reference validator, which checks `str.isalnum()` plus hyphen and so accepts non-ASCII
    letters and digits; and follow the reserved-word and angle-bracket rules although only one vendor
    states them. Reason: the tighter rule is the one every surface accepts, and it costs nothing —
    `standards/NAMING.md` already keeps the brand out of identifiers.

45. **`name` is at most 54 characters.** The specification, the platform docs, and the reference
    validator cap the name at 64 (https://agentskills.io/specification;
    https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview;
    `MAX_SKILL_NAME_LENGTH = 64`). Codex caps something else: "The combined plugin and skill name
    (`plugin-name:skill-name`) must be 64 characters or fewer" (`skill_identity_too_long`,
    https://developers.openai.com/plugins/deploy/submission-errors.md). Claude Code states no cap.
    **House choice:** the bundle's plugin name is `housecarl`, so `housecarl:` spends 10 characters
    of the Codex identity and 54 is what remains — the only number that satisfies both caps at once.

46. **`name` never carries the `housecarl:` prefix.** Claude Code only. In a plugin skill the prefix
    is added by the host, and a `name` that already carried it was rendered doubled —
    `/plugin:plugin:skill` — on v2.1.216 through v2.1.245, fixed on v2.1.246
    (https://code.claude.com/docs/en/skills.md; CHANGELOG v2.1.246).

### `description`

47. **`description` is required, non-empty, and at most 1,024 characters.** Codex and the
    specification make it required — "`description` is required and must not be empty"
    (`skill_description_missing`), "Required: Yes … Max 1024 characters. Non-empty"
    (https://agentskills.io/specification) — as do the platform docs and the Skills API. Claude Code
    alone calls it "Required: Recommended" and falls back to "the first paragraph of markdown
    content" if it is omitted (https://code.claude.com/docs/en/skills.md); the bundle never relies on
    that fallback, because on Codex the same file fails validation. The 1,024 figure is stated by the
    specification, the platform docs, the Skills API, `MAX_DESCRIPTION_LENGTH = 1024`, and Codex
    submission ("`description` must be 1,024 characters or fewer"). What the text should say, and the
    working budget it is written to, are rules 59 to 67.

48. **`description` contains no `<` or `>`.** Anthropic only. The platform docs say the field "Cannot
    contain XML tags"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview); Anthropic's own
    validator states it more tightly — "Description cannot contain angle brackets (< or >)"
    (anthropics/skills `skill-creator/scripts/quick_validate.py`) — and neither the specification
    page nor the reference validator checks it; Codex is silent. **House choice:** take the tighter
    of the two and write no angle brackets at all. The mechanism is visible even where the rule is
    not stated: the decision-time listing wraps each skill in `<available_skills>` / `<skill>` /
    `<name>` / `<description>` and HTML-escapes the values (skills-ref `prompt.py`). houseCARL skills
    talk about record signatures, so this bites: write `ARMO`, not `<ARMO>`.

49. **The description cap is 1,024, not 200.** **House choice** on a contradiction inside one vendor.
    Claude Code states no maximum and one listing truncation: the combined `description` and
    `when_to_use` text "is truncated at 1,536 characters in the skill listing to reduce context
    usage" (https://code.claude.com/docs/en/skills.md). The platform docs, the specification, the
    Skills API, the reference validator, and Codex submission all state a hard maximum of 1,024. The
    claude.ai Help Center states "**200 characters maximum**"
    (https://support.claude.com/en/articles/12512198-creating-custom-skills). Neither Anthropic page
    acknowledges the other. Reason: 1,024 is the number stated by every surface the bundle actually
    reaches, while the 200 governs the claude.ai upload path, which the bundle does not ship to. This
    is a case where the tighter number is not taken: 200 characters cannot carry a what-and-when
    description for a load-order tool, and Anthropic's own shipped skills run from 204 to 1,068
    characters against it. A skill uploaded to claude.ai must be cut to 200 for that surface only.

### The field set

50. **A houseCARL skill writes `name`, `description`, and `license`, and nothing else.** **House
    choice.** Claude Code documents twenty fields, and the same page says that outside Claude Code
    "packaging or upload fails with a hard error instead of ignoring the field", quoting the error
    naming one of its own documented fields: "Unexpected key(s) in SKILL.md frontmatter:
    argument-hint. Allowed properties are: allowed-tools, compatibility, description, license,
    metadata, name" (https://code.claude.com/docs/en/skills.md). The reference validator errors the
    same way. Codex is silent on unknown keys in either direction; the only positive signal is
    practice — OpenAI's curated `linear` skill ships a `metadata` key. Reason: one file that is valid
    everywhere, and no field whose behaviour depends on which host opened it. A Claude Code extension
    field is added only if a skill genuinely needs it, and then the skill says out loud that it has
    left the portable set.

51. **`allowed-tools` is not used.** **House choice.** It is legal — one of the specification's six —
    but its meaning splits: on Claude Code it is a real permission grant, "Tools Claude can use
    without asking permission during the turn that invokes this skill", cleared on the next message
    (https://code.claude.com/docs/en/skills.md); the specification calls it "(Experimental)" and warns
    "Support for this field may vary between agent implementations"
    (https://agentskills.io/specification); Codex states no effect for it, and OpenAI's import page
    flags "Tool restrictions or permissions in imported skills and agents" for manual follow-up.
    Reason: a field that grants permissions on one host and does nothing on the other is a silently
    different mode on the two hosts.

52. **`compatibility` and `metadata` are not used; the one runtime requirement is declared where a
    host acts on it.** **House choice.** `compatibility` is the field the specification points at for
    runtime requirements — "For runtime-level requirements, use the `compatibility` frontmatter
    field" — but it also says "Most skills do not need the `compatibility` field"
    (https://agentskills.io/specification), Claude Code does not act on it, and Codex states no
    effect. `metadata` is a client-extension slot no host acts on. Reason: a houseCARL skill's real
    requirement is the houseCARL MCP server, and it is declared in `dependencies.tools` in
    `agents/openai.yaml` on Codex (rule 56) and in the skill body on Claude Code. A `compatibility`
    string on either host is text nothing reads.

53. **`license` is written, short, and the same on every skill.** **House choice.** It is one of the
    six, so it is legal on every surface; the specification recommends "keeping it short (either the
    name of a license or the name of a bundled license file)" (https://agentskills.io/specification);
    Claude Code "accepts the field but doesn't act on it"; Codex is silent. Reason: the bundle is
    shipped software with notices in `plugin/`, and a per-skill line pointing at them costs one line
    and travels with a skill that is copied out of the bundle. Its value is a reference to the
    bundled notice file, not licence text.

### `agents/openai.yaml`, per skill

Codex only. Claude Code never names the file and does not read it; on a Claude Code install it is an
inert file in the skill folder (rule 15).

54. **If the file exists, `interface` exists, with `display_name` and `short_description`.** Codex
    only: "`interface` is required in `agents/openai.yaml` when that file is included" and must be a
    YAML mapping; `interface.display_name` and `interface.short_description` are each "required and
    must not be empty" (`skill_agent_interface_missing`, `skill_agent_display_name_missing`,
    `skill_agent_short_description_missing`,
    https://developers.openai.com/plugins/deploy/submission-errors.md). The other `interface` keys
    are optional with their own shape rules: `icon_small` and `icon_large` "must be a non-empty
    relative file path when provided", `brand_color` "must be a six-digit hex color, such as
    `#1ABCFE`", `default_prompt` "must be non-empty when provided". The bundle writes only the two
    required keys.

55. **`interface.short_description` is user-facing text, not the trigger surface.** **House choice.**
    It sits beside `description` and does not replace it: Codex's load decision still runs on
    `SKILL.md`'s `description` — "The description determines when the model considers the skill"
    (https://developers.openai.com/plugins/build/skills). Reason: one line is read by a person in a
    menu, the other by a model at decision time, and they are allowed to differ.

56. **`dependencies.tools` declares the houseCARL MCP server.** Codex only: "If a skill requires an
    MCP server, declare the dependency in `agents/openai.yaml`" under `dependencies.tools`, and "Only
    `dependencies.tools` is supported in `agents/openai.yaml`"
    (https://developers.openai.com/plugins/build/skills; `skill_agent_dependency_unsupported`). The
    same page states the limit of the mechanism: "A dependency makes the required tool available; it
    does not replace clear workflow instructions." Every houseCARL skill drives the server, so every
    one that ships this file declares it, and the body still says what to call and when.

57. **Booleans in `agents/openai.yaml` are written `true` or `false`, spelled out.** **House
    choice.** Codex accepts nothing else — "`allow_implicit_invocation` must be `true` or `false`"
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Claude Code accepts "`yes`,
    `no`, `on`, `off`, `1`, and `0` in any letter case, in addition to `true` and `false`" for its own
    frontmatter booleans, from v2.1.218. Reason: the strict spelling is the one both hosts read.

### `SKILL.md` frontmatter, field by field

Columns: Claude Code — https://code.claude.com/docs/en/skills.md (CHANGELOG rows marked); spec and
platform — https://agentskills.io/specification and
https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview, with the `skills-ref`
reference validator where it differs; Codex — https://learn.chatgpt.com/docs/build-skills and
https://developers.openai.com/plugins/deploy/submission-errors.md. "Silent" means no page in that
family states anything about the field.

| Field | Claude Code | Spec / platform | Codex | houseCARL writes |
|---|---|---|---|---|
| `name` | Optional; display label, defaults to the directory name; in a plugin skill sets the last command segment. No character or length rule | Required. Max 64; lowercase letters, numbers, hyphens; no edge hyphen; no `--`; must match the parent directory. Platform adds: no XML tags, no reserved words "anthropic"/"claude" | Required, non-empty, "supported text"; unique within the plugin; `plugin-name:skill-name` ≤ 64; whitespace-normalised on import | **Yes.** Folder name exactly; ASCII kebab-case; ≤ 54 characters; no `housecarl:` prefix |
| `description` | "Required: Recommended"; falls back to the first body paragraph; combined with `when_to_use`, truncated at 1,536 in the listing; no maximum stated | Required, non-empty, max 1,024; no XML tags (platform); no angle brackets (validator script); Help Center says 200 | Required, non-empty, "supported text", ≤ 1,024 at submission; shortened first when the listing budget overflows | **Yes.** ≤ 1,024 characters, written to a 400-character budget (rule 65); no `<` or `>`; quoted or block scalar if it contains a colon |
| `license` | Accepted, not acted on | Optional, in the six: license name or bundled file reference; keep it short | Silent | **Yes.** Short reference to the bundled notice file, same on every skill |
| `compatibility` | Accepted, not acted on; string up to 500 characters | Optional; 1–500 characters; "Most skills do not need" it | Silent | No — declared instead in `dependencies.tools` and the body |
| `metadata` | Free-form map; not acted on; non-map values dropped | Optional, in the six; string-to-string map | Named once to say it is inert for the interface; tolerated in practice | No |
| `allowed-tools` | Real permission grant for the invoking turn; clears next message | Optional, in the six; "(Experimental)"; support may vary | Silent | No — rule 51 |
| `when_to_use` | Claude Code extension; appended to `description` in the listing, counts toward the 1,536 cap | Silent; outside the six, so a hard error on the packaging path | Silent | No — everything goes in `description` |
| `argument-hint` | Autocomplete hint | Silent; named only as the example rejected key | Silent | No |
| `arguments` | Named positional arguments for `$name` substitution | Silent | Silent | No — rule 70 |
| `disable-model-invocation` | Blocks automatic loading and subagent preloading; default `false` | Silent | Silent; the counterpart is `policy.allow_implicit_invocation` | No — rule 72 |
| `user-invocable` | `false` hides it from `/` and blocks `/name`; default `true` | Silent | Silent | No — rule 72 |
| `disallowed-tools` | Removes tools from the pool while active | Silent | Silent | No |
| `model` | Model for the turn the skill is active | Silent | Silent | No |
| `effort` | `low`/`medium`/`high`/`xhigh`/`max` while active | Silent | Silent | No |
| `context` | `fork` runs the skill in a forked subagent | Silent | Silent | No — rule 74 |
| `agent` | Subagent type when `context: fork` | Silent | Silent | No |
| `background` | With `context: fork` only; default `true`; needs v2.1.218+ | Silent | Silent | No |
| `hooks` | Registers hooks for the rest of the session | Silent | Silent | No — rule 6 |
| `paths` | Globs limiting when the skill activates | Silent | Silent | No |
| `shell` | `bash` (default) or `powershell` for `!` blocks | Silent | Silent | No |
| `display-name` / `display_name` | `display-name` in the CHANGELOG only (v2.1.186); not in the docs table | `display_name` is API-level, not frontmatter: optional, ≤ 255, derives from `name` | Silent; the counterpart is `interface.display_name` | No |
| `default-enabled` | CHANGELOG only (v2.1.186); undocumented behaviour | Silent | Silent | No |
| `fallback` | CHANGELOG only (v2.1.186); undocumented behaviour | Silent | Silent | No |
| `dependencies` | Silent as a skill key | Named only by the claude.ai Help Center; absent from the spec, the API rules, and the validator | Silent for `SKILL.md`; `dependencies.tools` exists in `agents/openai.yaml` | No |

### `agents/openai.yaml`, field by field

Codex only; Claude Code and the spec are silent on every row.

| Field | Codex | houseCARL writes |
|---|---|---|
| `interface` | Required once the file exists; YAML mapping; keys are snake_case | Yes, on every skill that ships the file |
| `interface.display_name` | Required, string, non-empty | Yes — the skill's human name |
| `interface.short_description` | Required, string, non-empty | Yes — one line, user-facing; not the trigger text |
| `interface.icon_small` | Optional; non-empty relative file path | No |
| `interface.icon_large` | Optional; same rule | No |
| `interface.brand_color` | Optional; six-digit hex, e.g. `#1ABCFE` | No |
| `interface.default_prompt` | Optional; non-empty when provided; no page states what it does at invocation time | No |
| `policy` | Optional mapping; only `products` and `allow_implicit_invocation` | No — rule 72 |
| `policy.allow_implicit_invocation` | Boolean, default `true`; `false` leaves `$skill` working | No — rule 72 |
| `policy.products` | Must contain `CHAT`, `CODEX`, or both | No |
| `dependencies.tools` | The only supported key under `dependencies`; entries carry `type`, `value`, `description`, `transport`, `url` | Yes — the houseCARL MCP server |

### Where the sources are silent or disagree

- **Unknown frontmatter keys: nobody says what a host does with one, except by rejecting them.**
  Claude Code is silent for `SKILL.md` (it states the rule only for `plugin.json`, where it "ignores
  top-level fields it does not recognize"); the specification page, the platform docs, and Codex are
  silent. The two sources that answer both reject: Anthropic's packaging error, and the reference
  validator's "Unexpected fields in frontmatter: {…}. Only {…} are allowed." **House choice:** rule
  50 — write only fields inside the allowed six, so the question never has to be answered.
- **The `name` shape has three Anthropic answers.** The platform docs say a kebab identifier; the
  claude.ai Help Center says "A human-friendly name for your skill (64 characters maximum) —
  **Example:** Brand Guidelines"; Claude Code says "Display name shown in skill listings". **House
  choice:** the kebab identifier, because it is also the folder name and the command segment, and a
  display label that differs from the folder buys nothing on either host the bundle ships to.
- **OpenAI defers to a specification it does not enforce.** "Skill front matter validation follows
  the [agent skills specification]"
  (https://developers.openai.com/api/docs/guides/tools-skills.md), but the enforced submission rules
  require only "supported text" for `name`, cap no name length on its own, and state no folder-match
  rule. **House choice:** write to the specification, the tighter of the two and the one OpenAI
  names. Nothing valid under the specification is invalid under Codex's own checks.
- **Anthropic's own shipped skills do not all obey Anthropic's own limits.** The `claude-api` skill's
  description is 1,068 characters against a documented maximum of 1,024, and no page explains it.
  Recorded as observed; it does not raise the cap.
- **Three frontmatter keys exist only in a changelog line.** `display-name`, `default-enabled`, and
  `fallback` are named once, as accepting kebab-case, snake_case, and camelCase (Claude Code
  CHANGELOG v2.1.186), and appear in no docs table. **House choice:** do not use them, and the same
  for the `dependencies` frontmatter key, which appears only in a claude.ai Help Center article, and
  for `SKILL.json`, a third per-skill metadata file named once on the Codex side with no published
  schema. Reason: a field whose behaviour is undocumented cannot be relied on, and all of them sit
  outside the allowed six.

## The description and how a skill gets loaded

The body is not what decides whether the skill is used. Both hosts put only name and description in
front of the model. Everything here follows from that.

58. **Name and description at start, body on activation.** Both hosts. Claude Code: "Claude Code
    loads a listing of skill names and descriptions into context so Claude knows what's available"
    (https://code.claude.com/docs/en/skills.md). Codex: "ChatGPT and Codex start with each skill's
    name and description, then load the full `SKILL.md` instructions when they decide to use that
    skill" (https://learn.chatgpt.com/docs/build-skills.md). The spec states the same three tiers —
    metadata at startup, body on activation, resources as needed
    (https://agentskills.io/specification).

59. **The description carries the whole trigger.** Both hosts. "This means the description carries
    the entire burden of triggering"
    (https://agentskills.io/skill-creation/optimizing-descriptions.md); "The `description` is what
    Claude matches your request against when determining whether to trigger the Skill, so it must say
    both what the Skill does and when to use it"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview); "implicit matching
    depends on `description`" (https://learn.chatgpt.com/docs/build-skills.md). A description that
    says only what the skill does, and not when to reach for it, is half a description.

60. **Codex also shows the path.** Codex only: "In Codex, the initial list also includes each skill's
    file path" (https://learn.chatgpt.com/docs/build-skills.md); on the API side, "the platform adds
    each skill's `name`, `description`, and `path` to user prompt context"
    (https://developers.openai.com/api/docs/guides/tools-skills.md). Claude Code names only names and
    descriptions (https://code.claude.com/docs/en/skills.md). The path is not the author's to write,
    but it spends catalogue budget on Codex, so it is part of rule 64's arithmetic.

61. **Vague or overlapping descriptions cause the wrong load or no load.** Claude Code: "Claude
    matches your task against skill descriptions to decide which are relevant. If descriptions are
    vague or overlap, Claude may load the wrong skill or miss one that would help"
    (https://code.claude.com/docs/en/features-overview.md); the same failure is implied by Codex's
    "clear scope and boundaries" (https://learn.chatgpt.com/docs/build-skills.md). With fourteen
    skills in one bundle this is the main failure mode: each description names a decision no other
    description in the bundle names.

62. **A simple one-step request may not trigger a skill at all.** Anthropic sources, treated as
    holding on both hosts: "A simple, one-step request like 'read this PDF' may not trigger a PDF
    skill even if the description matches perfectly, because the agent can handle it with basic
    tools. Complex, multi-step, or specialized queries reliably trigger skills when the description
    matches" (https://agentskills.io/skill-creation/optimizing-descriptions.md, repeated at
    https://github.com/anthropics/claude-plugins-official/blob/main/plugins/skill-creator/skills/skill-creator/SKILL.md).
    So a description written for the one-line version of a job will underperform; write it for the
    real job. No Codex page states an equivalent.

63. **Claude Code's listing budget is 1% of the context window, with a hard per-entry cap of 1,536
    characters.** Claude Code only: "The budget scales at 1% of the model's context window. When the
    listing overflows, Claude Code drops descriptions starting with the skills you invoke least, so
    the skills you use most keep their full text"; "each entry's combined text is capped at 1,536
    characters regardless of budget. The cap is configurable with `skillListingMaxDescChars`"; the
    listing "always contains every skill name", and shortening "can strip the keywords Claude needs
    to match your request" (https://code.claude.com/docs/en/skills.md). The settings are
    `skillListingBudgetFraction` (default `0.01`), `skillListingMaxDescChars` (default `1536`), and
    the `SLASH_COMMAND_TOOL_CHAR_BUDGET` environment variable as a fixed character count
    (https://code.claude.com/docs/en/settings-reference.md). Fourteen skills at the cap would present
    14 × 1,536 = 21,504 characters of listing, competing with the user's own skills inside the same
    1%.

64. **Codex's listing budget is at most 2% of the context window, or 8,000 characters when it is
    unknown.** Codex only: "To avoid crowding out the rest of the prompt, this list uses at most 2%
    of the model's context window, or 8,000 characters when the context window is unknown. If many
    skills are installed, Codex shortens skill descriptions first. For large skill sets, Codex may
    omit some skills from the initial list and show a warning"
    (https://learn.chatgpt.com/docs/build-skills.md). The budget is set by
    `skills.max_context_tokens` — "Defaults to 2% of the model's context window. Explicit values are
    capped at `10000` tokens" (https://learn.chatgpt.com/docs/config-file/config-reference.md). Codex
    installs fifteen entries — the fourteen skills plus the umbrella — each carrying name,
    description and path. At the 8,000-character floor that is 533 characters per entry for
    everything, and name plus absolute path costs on the order of 80 to 100, leaving roughly 430 to
    450 characters of description before Codex starts shortening across the whole catalogue.

65. **Every houseCARL description fits in 400 characters.** **House choice**, derived from rule 64;
    no source states the number. Fifteen entries at 400 characters is 6,000 characters of
    description, plus roughly 1,350 of names and paths, leaving margin inside Codex's
    8,000-character worst case for the user's other skills; the same 400 sits well under Claude
    Code's 1,536-character per-entry cap, so no houseCARL entry is the one cut at the cap, and well
    under the field's own 1,024 hard cap (rule 47). Reason: the bundle is a guest in someone else's
    catalogue, and a description that survives intact on the tightest host survives everywhere.

66. **Front-load the trigger words.** Both hosts. "Front-load the key use case and trigger words so a
    host can still match the skill if descriptions are shortened"
    (https://learn.chatgpt.com/docs/build-skills.md); Claude Code makes the same point from the
    damage side, that shortening "can strip the keywords Claude needs to match your request"
    (https://code.claude.com/docs/en/skills.md). The first clause is load-bearing: what the skill
    does, in the words a user would use. Qualifiers, caveats and the "load before X" line come after.

67. **Say what the skill is not for.** **House choice**, adopting a documented Anthropic practice.
    Anthropic's shipped `claude-api` description carries a negative scope clause — "The skill does
    not activate for general programming tasks, ML/data-science work, or code that imports other AI
    SDKs (such as OpenAI)"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/claude-api-skill) — and Codex
    asks for "clear scope and boundaries" (https://learn.chatgpt.com/docs/build-skills.md). Every
    houseCARL skill whose subject borders another's carries one short boundary clause. Reason: the
    boundary sentence is the cheapest fix for rule 61 and costs a dozen words of the 400.

68. **The listing is measured, not guessed.** Both hosts. Claude Code reports it: `/doctor` gives "an
    estimate of the listing's context cost and its biggest contributors", the `/context` Skills row
    "reports the size of the listing after the budget is applied", `/skill-doctor` (v2.1.252+) shows
    "what each of your skills costs and how often it gets used", and an over-budget listing writes "a
    warning to the debug log, visible with `--debug`" (https://code.claude.com/docs/en/skills.md).
    Codex reports it as telemetry: `thread.skills.enabled_total`, `thread.skills.kept_total`,
    `thread.skills.truncated` — "Whether skill rendering truncated the enabled skills list (`1` or
    `0`)" — and `skill.injected` (https://learn.chatgpt.com/docs/codex-manual.md). These are where an
    author looks when a skill stops triggering.

### Invocation

69. **Both kinds of invocation are on by default, and the sigil differs.** Claude Code: "By default,
    both you and Claude can invoke any skill. You can type `/skill-name` to invoke it directly, and
    Claude can load it automatically when relevant to your conversation"
    (https://code.claude.com/docs/en/skills.md). Codex: "**Explicit invocation:** … In ChatGPT, type
    `@` to select a skill. In Codex CLI or the IDE extension, run `/skills` or type `$` to mention a
    skill. **Implicit invocation:** ChatGPT or Codex can choose a skill when your task matches the
    skill `description`" (https://learn.chatgpt.com/docs/build-skills.md); "ChatGPT supports `@`
    mentions, while Codex supports `$` mentions for skills"
    (https://learn.chatgpt.com/docs/skills-and-plugins.md). In Claude Code `$name` is an argument
    placeholder, never an invocation (https://code.claude.com/docs/en/skills.md). Any invocation
    example written in a skill body, or in the bundle's README, names the host or avoids the sigil.

70. **A houseCARL skill takes no declared arguments and behaves correctly when invoked with none.**
    **House choice.** Claude Code publishes a full grammar — `$ARGUMENTS`, `$ARGUMENTS[N]`, `$N`,
    `$name` from an `arguments` frontmatter list, plus `${CLAUDE_SKILL_DIR}`, `${CLAUDE_PROJECT_DIR}`,
    `${CLAUDE_PLUGIN_ROOT}`, `${CLAUDE_PLUGIN_DATA}`, `${CLAUDE_SESSION_ID}` and `${CLAUDE_EFFORT}` —
    and defines the fallback: if no placeholder receives an argument, "Claude Code appends
    `ARGUMENTS: <your input>` to the end of the skill content"
    (https://code.claude.com/docs/en/skills.md). Codex documents no parameter schema; its examples are
    bare prose after the sigil — `$skill-installer linear`, `$openai-docs migrate this project to
    GPT-6 Astra` (https://learn.chatgpt.com/docs/build-skills.md,
    https://developers.openai.com/api/docs/guides/latest-model.md). The spec is silent on arguments
    entirely (https://agentskills.io/client-implementation/adding-skills-support.md). Reason: a
    placeholder grammar that exists on one host cannot be load-bearing in a bundle that ships to
    both, and the Claude Code fallback already delivers the user's text to a skill that declares
    nothing. Whatever the user typed after the name is ordinary prose about the job.

71. **No skill depends on being stacked with another.** **House choice.** Claude Code only: "A skill
    invocation followed by more skills, such as `/skill-a /skill-b do XYZ`, loads every skill named
    at the start and passes the trailing text to each as arguments"
    (https://code.claude.com/docs/en/commands.md). Codex and the spec are silent on chaining. Reason:
    stacking is a convenience for the user; a skill that only works when another was stacked in front
    of it is broken on Codex.

72. **houseCARL skills stay invocable both ways on both hosts.** **House choice.** Claude Code offers
    `disable-model-invocation: true` — "Only you can invoke the skill. Use this for workflows with
    side effects or that you want to control timing, like `/commit`, `/deploy`" — and
    `user-invocable: false` — "Only Claude can invoke the skill. Use this for background knowledge
    that isn't actionable as a command" (https://code.claude.com/docs/en/skills.md). Codex offers
    `allow_implicit_invocation` — "(default: `true`): When `false`, Codex won't implicitly invoke the
    skill based on user prompt; explicit `$skill` invocation still works"
    (https://learn.chatgpt.com/docs/build-skills.md), declared as
    `policy.allow_implicit_invocation` in `skills/<skill>/agents/openai.yaml`
    (https://developers.openai.com/plugins/deploy/submission-errors.md). Reason: none of the skills
    performs a side effect itself — the writes are made by `housecarl_*` MCP tools under their own
    consent rules — so there is nothing for a timing lock to protect, and hiding a skill from the
    model would defeat rule 59. On Claude Code there is a second reason: "You can't preload skills
    that set `disable-model-invocation: true`, since preloading draws from the same set of skills
    Claude can invoke" (https://code.claude.com/docs/en/sub-agents.md), so setting it would make the
    skill unusable from a subagent's `skills` field.

73. **A skill never assumes its siblings are enabled.** Both hosts, with different levers. Claude
    Code: `skillOverrides` with four states — `"on"`, `"name-only"`, `"user-invocable-only"` and
    `"off"` — written from the `/skills` menu to `.claude/settings.local.json`, plus permission rules
    on the `Skill` tool (`Skill(commit)` exact, `Skill(name *)` prefix); "Plugin skills are not
    affected by `skillOverrides`" (https://code.claude.com/docs/en/skills.md), which is what the
    bundle installs as, so a Claude Code user disables a houseCARL skill through permission rules.
    Codex: a `[[skills.config]]` entry in `~/.codex/config.toml` with `path` and `enabled = false`,
    and "Restart Codex after changing `~/.codex/config.toml`"
    (https://learn.chatgpt.com/docs/build-skills.md); in the plugin UI only whole plugins toggle
    (https://learn.chatgpt.com/docs/plugins.md).

74. **No houseCARL skill sets `context: fork`.** **House choice.** Claude Code only: a skill can set
    `context: fork` to run in a subagent — the skill content becomes the subagent's prompt, it runs
    in the background by default — and the docs warn that "`context: fork` only makes sense for
    skills with explicit instructions. If your skill contains guidelines like 'use these API
    conventions' without a task, the subagent receives the guidelines but no actionable prompt, and
    returns without meaningful output" (https://code.claude.com/docs/en/skills.md). Codex has no
    equivalent field, removed skill model delegation (#39068, rust-v0.149.0), and delegates only when
    "Codex can also follow applicable `AGENTS.md` or skill instructions that request delegation"
    (https://learn.chatgpt.com/docs/agent-configuration/subagents.md); the spec calls subagent
    delegation "an advanced pattern only supported by some clients"
    (https://agentskills.io/client-implementation/adding-skills-support.md). Reason: these skills
    supply the knowledge for work the main session is already doing and carry guidance as well as
    steps — the shape the Claude Code warning calls useless in a fork — and the field has no Codex
    counterpart. They stay preloadable into a subagent's `skills` list, which is the portable half of
    the same idea (https://code.claude.com/docs/en/sub-agents.md).

75. **The namespaced identity is `housecarl:<name>` and stays inside 64 characters.** Both hosts.
    Claude Code namespaces plugin skills automatically: "Skills from plugins are automatically
    namespaced with the plugin name to avoid conflicts. To invoke one directly, send
    `/plugin-name:skill-name` as the prompt" (https://code.claude.com/docs/en/agent-sdk/plugins.md).
    Codex spells the same identity and caps it: "The combined plugin and skill name
    (`plugin-name:skill-name`) must be 64 characters or fewer", with "Each skill `name` must be
    unique within the plugin" (https://developers.openai.com/plugins/deploy/submission-errors.md).
    The arithmetic is rule 45.

76. **A description or body does not claim system-level authority.** **House choice** on a
    contradiction between vendors. The Anthropic platform puts skill metadata "in the system prompt"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview); the OpenAI API says
    "Skill instructions are user prompt input (not system prompt input), so they're handled with the
    same priority as other user-provided instructions"
    (https://developers.openai.com/api/docs/guides/tools-skills.md); Claude Code is silent. Reason:
    on one host the text demonstrably sits at user-message priority, so write nothing that depends on
    being read as system-level instruction — no "you must", no claim of higher authority than the
    user's message. See also rule 106.

### Where the hosts differ

| Point | Claude Code | Codex | What the bundle does |
|---|---|---|---|
| What the model sees at decision time | Names + descriptions (https://code.claude.com/docs/en/skills.md) | Names + descriptions + file paths (https://learn.chatgpt.com/docs/build-skills.md) | Write for the smaller Codex allowance; the path is not the author's to control |
| Catalogue budget | 1% of the context window, per-entry cap 1,536 characters (https://code.claude.com/docs/en/skills.md, .../settings-reference.md) | 2% of the context window, or 8,000 characters when unknown; `skills.max_context_tokens` capped at 10,000 tokens (https://learn.chatgpt.com/docs/build-skills.md, .../config-reference.md) | 400 characters per description (rule 65) fits both without shortening |
| Overflow | Names always kept; descriptions dropped "starting with the skills you invoke least" (https://code.claude.com/docs/en/skills.md) | Descriptions shortened first, then whole skills may be omitted with a warning (https://learn.chatgpt.com/docs/build-skills.md) | Codex is the worse case — a skill can vanish from the catalogue entirely, so keep the bundle small in characters |
| Explicit invocation | `/name`, `/housecarl:<name>` (https://code.claude.com/docs/en/skills.md, .../agent-sdk/plugins.md) | `$name` in CLI/IDE, `@name` in ChatGPT, `/skills` picker (https://learn.chatgpt.com/docs/build-skills.md) | Never write a bare sigil in a skill body without naming the host |
| Arguments | `$ARGUMENTS`, `$ARGUMENTS[N]`, `$N`, `$name`, `${CLAUDE_*}`, plus an `ARGUMENTS:` fallback (https://code.claude.com/docs/en/skills.md) | No schema; prose after the mention (https://learn.chatgpt.com/docs/build-skills.md) | No declared arguments; a skill works with none (rule 70) |
| Turning off model invocation | `disable-model-invocation: true` in frontmatter (https://code.claude.com/docs/en/skills.md) | `policy.allow_implicit_invocation: false` in `agents/openai.yaml` (https://developers.openai.com/plugins/deploy/submission-errors.md) | Neither is set (rule 72) |
| Turning off user invocation | `user-invocable: false` (https://code.claude.com/docs/en/skills.md) | No equivalent documented | Not set (rule 72) |
| Per-skill disabling by the user | `skillOverrides` four states + `Skill(name)` permission rules; plugin skills exempt from `skillOverrides` (https://code.claude.com/docs/en/skills.md) | `[[skills.config]]` `enabled = false` by absolute path, restart required (https://learn.chatgpt.com/docs/build-skills.md) | A skill never assumes its siblings are enabled (rule 73) |
| Running in a subagent | `context: fork` (+ `agent`, `background`, `effort`, `model`), and a subagent's `skills` preload field (https://code.claude.com/docs/en/skills.md, .../sub-agents.md) | No field; delegation only via instructions; skill model delegation removed (https://learn.chatgpt.com/docs/agent-configuration/subagents.md) | No skill sets `context: fork` (rule 74) |

### Where the sources are silent or disagree

- **How a description is shortened — silent on both hosts.** Claude Code says descriptions are
  shortened and dropped but never says what shortening does to the text; Codex says "Codex shortens
  skill descriptions first" and states neither how, nor which skills are omitted first, nor what the
  warning says. **House choice:** assume the worst — shortening is a cut from the end and a whole
  skill can disappear. That is what rules 65 and 66 are for; an assumption that costs a shorter first
  clause is cheap, and the opposite assumption is unrecoverable at runtime.
- **Codex states one budget in two units.** build-skills gives "at most 2% of the model's context
  window, or 8,000 characters when the context window is unknown"; the config reference names the
  same budget `skills.max_context_tokens`, "capped at `10000` tokens". No page reconciles them.
  **House choice:** author against the character figure, 8,000, because it is the smaller and more
  concrete of the two and the one stated for the case where the context window is unknown.
- **Codex spells `allow_implicit_invocation` two ways** — as a bare key on the build-skills page and
  as `policy.allow_implicit_invocation` there and in submission-errors. **House choice:** if it is
  ever set, write `policy.allow_implicit_invocation`, the spelling the validator publishes: "`policy`
  may contain only `products` and `allow_implicit_invocation`". Rule 72 sets neither spelling.
- **Claude Code says five or six for stacking.** The skills page says "the first skill plus up to
  five more stacked after it"; the commands page says "Up to six skills can be chained"; the
  v2.1.199 changelog says "up to 5". The standard picks no number, because rule 71 forbids depending
  on stacking at all.
- **Anthropic contradicts itself on how many skills is too many.** The overview says "you can install
  many Skills without context penalty"
  (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview); the enterprise page
  says "limit the number of Skills loaded simultaneously to maintain reliable recall accuracy … stop
  adding when performance degrades"
  (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/enterprise). **House choice:**
  follow the enterprise page, because it is the one that names a failure mode and it agrees with both
  hosts' published budgets. A new houseCARL skill earns its catalogue entry against the fourteen
  already there, rather than merely being useful.
- **The Agent Skills spec sets no catalogue budget at all** — only the advisory "~50-100 tokens per
  skill" (https://agentskills.io/client-implementation/adding-skills-support.md). Nothing here rests
  on it; the two hosts' published budgets govern.
- **Whether Claude Code's 1% budget is counted in characters or tokens is unstated.** The
  environment-variable override is "a fixed character count", but "1% of the model's context window"
  is naturally a token quantity, and no page reconciles them. Rule 63's arithmetic therefore stops at
  the per-entry cap.

## The body, supporting files, and instruction style

77. **Keep `SKILL.md` under 500 lines and 5,000 tokens.** **House choice** on a silence. Claude Code
    gives the line count: "Keep `SKILL.md` under 500 lines. Move detailed reference material to
    separate files" (https://code.claude.com/docs/en/skills.md). The spec gives both figures: "under
    500 lines and 5,000 tokens" (https://agentskills.io/skill-creation/best-practices.md). Codex
    publishes no body length and states only "Skill instructions must not be empty"
    (https://developers.openai.com/plugins/deploy/submission-errors). Reason: it is the only
    published number and no Codex rule competes with it. Neither vendor says what a host does with a
    longer body; the limits are advisory (https://agentskills.io/specification).

78. **Write the body as an overview that points to the detail, not as the detail.** Both hosts.
    "SKILL.md serves as an overview that points Claude to detailed materials as needed, like a table
    of contents in an onboarding guide"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices); Claude Code's
    layout marks `SKILL.md` "(required - overview and navigation)"
    (https://code.claude.com/docs/en/skills.md); Codex: "Keep `SKILL.md` concise and place detailed
    material next to it" (https://developers.openai.com/plugins/build/skills.md).

79. **Write standing instructions that stand alone.** Claude Code: "Claude Code does not re-read the
    skill file on later turns, so write guidance that should apply throughout a task as standing
    instructions rather than one-time steps"; the body "enters the conversation as a single message
    and stays there across later turns", and after compaction Claude Code re-attaches "the first
    5,000 tokens of each" skill within "a combined budget of 25,000 tokens"
    (https://code.claude.com/docs/en/skills.md), re-injecting invoked skills "up to a shared budget;
    oldest dropped first"
    (https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more), while the
    spec advises hosts to "exempt skill content from pruning"
    (https://agentskills.io/client-implementation/adding-skills-support.md). Codex states only that
    its listing budget "applies only to the initial skills list. When Codex selects a skill, it still
    reads the full SKILL.md instructions for that skill"
    (https://learn.chatgpt.com/docs/build-skills) and says nothing about persistence. So a body reads
    as complete instructions on its own, never as a continuation of something said earlier in the
    conversation — and a body over 5,000 tokens is the part Claude Code drops first, a second reason
    for rule 77.

80. **Apply the cut test to every line.** Both hosts. "Would the agent get this wrong without this
    instruction?' If the answer is no, cut it", and "Focus on what the agent *wouldn't* know without
    your skill: project-specific conventions, domain-specific procedures, non-obvious edge cases, and
    the particular tools or APIs to use" (https://agentskills.io/skill-creation/best-practices.md).
    Claude Code puts it as cost: "every line is a recurring token cost"
    (https://code.claude.com/docs/en/skills.md).

81. **Keep the gotchas in the body.** Both hosts. "The highest-value content in many skills is a list
    of gotchas — environment-specific facts that defy reasonable assumptions", and "Keep gotchas in
    `SKILL.md` where the agent reads them before encountering the situation. A separate reference
    file works if you tell the agent when to load it, but for non-obvious issues, the agent may not
    recognize the trigger" (https://agentskills.io/skill-creation/best-practices.md).

82. **The body makes six things clear: the input the workflow expects, the steps to follow, the
    output the user receives, which facts the model must not infer, when to ask a question, stop, or
    decline, and which supporting files to consult.** **House choice.** Codex states this list
    (https://developers.openai.com/plugins/build/skills.md). Anthropic states no equivalent — the
    spec says "There are no format restrictions" and only recommends "Step-by-step instructions",
    "Examples of inputs and outputs", "Common edge cases" (https://agentskills.io/specification).
    Reason: it is the only required-content list either vendor publishes, it conflicts with nothing
    Anthropic says, and one body has to satisfy both hosts.

83. **Do not try to cover every edge case.** Both hosts. "Concise, stepwise guidance with a working
    example tends to outperform exhaustive documentation. When you find yourself covering every edge
    case, consider whether most are better handled by the agent's own judgment"
    (https://agentskills.io/skill-creation/best-practices.md); "Don't try to cover every edge case up
    front. Start with one representative task, get it working well, then turn that workflow into a
    skill and improve from there" (https://learn.chatgpt.com/docs/codex-manual.md).

### Supporting files

84. **Progressive disclosure is the shared mechanism: name and description first, body on selection,
    supporting files only when the body sends the model to them.** Both hosts. Codex: "it sees
    metadata such as `name` and `description` first; it loads `SKILL.md` only when the skill is
    selected; it reads references or runs scripts only when needed"
    (https://developers.openai.com/blog/skills-agents-sdk); Claude Code shows the same shape in its
    layout example (https://code.claude.com/docs/en/skills.md). Bundled files cost nothing until
    read: "Reference files, data, or documentation don't consume context tokens until actually read"
    and "**No practical limit on bundled content**"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices,
    .../overview). The directories are rule 13.

85. **Every supporting file gets a stated load trigger in `SKILL.md`.** Both hosts, all three sources
    agreeing. Claude Code: "Reference supporting files from `SKILL.md` so Claude knows what each file
    contains and when to load it" (https://code.claude.com/docs/en/skills.md). Spec: "The key is
    telling the agent *when* to load each file. 'Read `references/api-errors.md` if the API returns a
    non-200 status code' is more useful than a generic 'see references/ for details.'"
    (https://agentskills.io/skill-creation/best-practices.md). Codex: "Reference supporting files
    from `SKILL.md` and explain when to load or run them"
    (https://developers.openai.com/plugins/build/skills.md).

86. **Link every reference file directly from `SKILL.md` — one level deep, no chains.** Both hosts,
    on the Anthropic rule; Codex is silent on nesting depth. "**Keep references one level deep from
    SKILL.md**. All reference files should link directly from SKILL.md to ensure Claude reads
    complete files when needed", because "Claude may partially read files when they're referenced
    from other referenced files. When encountering nested references, Claude might use commands like
    `head -100` to preview content rather than reading entire files, resulting in incomplete
    information" (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices;
    spec form at https://agentskills.io/specification).

87. **Put a table of contents at the top of any reference file longer than 100 lines.** **House
    choice** on a within-vendor difference. Anthropic states two thresholds: "For reference files
    longer than 100 lines, include a table of contents at the top. This ensures Claude can see the
    full scope of available information even when previewing with partial reads"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices) and "For large
    reference files (>300 lines), include a table of contents"
    (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md). Codex
    is silent. Reason: the stated reason — partial reads — applies at 100 lines as much as at 300,
    and a contents list costs a few lines.

88. **Split game-generic knowledge from Skyrim-specific knowledge structurally: separate files, or
    separate clearly-bounded parts of one file.** **House choice**, not a vendor rule. Reason: reuse
    — a future game target keeps the generic half. The mechanism is the vendors' own
    domain-organisation pattern: "For Skills with multiple domains, organize content by domain to
    avoid loading irrelevant context", with `reference/finance.md`, `reference/sales.md`
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices), and
    skill-creator's `references/aws.md`, `gcp.md`, `azure.md`, where "Claude reads only the relevant
    reference file"
    (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md).

89. **Domain knowledge goes in as data — tables, lists, reference files — not as prose the model has
    to read to do a lookup.** **House choice**, matching `CLAUDE.md`'s "Domain knowledge lives in
    skills as data". The vendors support the shape without stating it as a rule: templates are "more
    reliable than describing the format in prose, because agents pattern-match well against concrete
    structures" (https://agentskills.io/skill-creation/best-practices.md), and "Prefer structured
    formats — JSON, CSV, TSV — over free-form text" for what a script returns
    (https://agentskills.io/skill-creation/using-scripts.md).

90. **Short templates inline, long or conditional ones in `assets/`.** Both hosts. "Short templates
    can live inline in `SKILL.md`; for longer templates, or templates only needed in certain cases,
    store them in `assets/` and reference them from `SKILL.md` so they only load when needed"
    (https://agentskills.io/skill-creation/best-practices.md).

91. **Name files for their contents and reference them by relative path from the skill root, with
    forward slashes.** Both hosts. "**Name files descriptively:** Use names that indicate content:
    `form_validation_rules.md`, not `doc2.md`" and "Always use forward slashes in file paths, even on
    Windows" (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices); file
    references "use relative paths from the skill root" (https://agentskills.io/specification). Codex
    agrees on both counts: relative paths such as `assets/icon.png`, and "Archive entry path must use
    `/`, not backslashes" (https://developers.openai.com/plugins/deploy/submission-errors).

92. **The reference file is read on demand by the model's own file-read tool; nothing pre-loads it.**
    Both hosts, with a gap noted. "**Files read on-demand:** Claude uses bash Read tools to access
    SKILL.md and other files from the filesystem when needed"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices), and a host
    that enumerates supporting files "should **not eagerly read them**"
    (https://agentskills.io/client-implementation/adding-skills-support.md). No Claude Code page
    documents how a supporting file gets read, and Codex says nothing about whether it resolves paths
    for the model, so the pointer is a plain relative path plus an explicit instruction to read the
    file, which works either way.

### Scripts

93. **Do not add a script where instructions and existing tools do the job.** Both hosts. "Prefer
    instructions over scripts unless you need deterministic behavior or external tooling"
    (https://learn.chatgpt.com/docs/build-skills.md) and "Do not add a script when instructions and
    existing tools can complete the task reliably"
    (https://developers.openai.com/plugins/build/skills.md). The signal to write one: "If you notice
    the agent independently reinventing the same logic each run — building charts, parsing a specific
    format, validating output — that's a signal to write a tested script once and bundle it in
    `scripts/`" (https://agentskills.io/skill-creation/best-practices.md). OpenAI's split of labour:
    "interpretation, comparison, and reporting stay with the model" / "deterministic, repeated shell
    work goes in `scripts/`" (https://developers.openai.com/blog/skills-agents-sdk). For houseCARL
    the MCP tool is the existing tool, and a script that re-does what a tool already does is what
    this rule forbids.

94. **Say whether the model runs the script or reads it.** Both hosts. "**Important distinction:**
    Make clear in your instructions whether Claude should: **Execute the script** (most common): 'Run
    `analyze_form.py` to extract fields'; **Read it as reference** (for complex logic): 'See
    `analyze_form.py` for the field extraction algorithm'. For most utility scripts, execution is
    preferred" (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices).
    Codex is silent on the distinction, saying only "Use `scripts/` when the workflow needs
    deterministic computation or file processing"
    (https://developers.openai.com/plugins/build/skills.md), and has no rule against stating it.

95. **List the scripts in `SKILL.md` and call them by package-relative path.** Both hosts. "List
    available scripts in your `SKILL.md` so the agent knows they exist"
    (https://agentskills.io/skill-creation/using-scripts.md); a migrated Claude bundle must call
    bundled executables "with package-relative paths"
    (https://developers.openai.com/plugins/guides/submit-claude-plugin). Script paths in code blocks
    are relative to the skill directory root, in support files as well as in `SKILL.md`
    (https://agentskills.io/skill-creation/using-scripts.md).

96. **A script never blocks on input.** Both hosts. "This is a hard requirement of the agent
    execution environment. Agents operate in non-interactive shells — they cannot respond to TTY
    prompts, password dialogs, or confirmation menus. A script that blocks on interactive input will
    hang indefinitely" (https://agentskills.io/skill-creation/using-scripts.md).

97. **A bundled script behaves like a small command-line tool.** Both hosts. From the spec
    (https://agentskills.io/skill-creation/using-scripts.md): a concise `--help` — "`--help` output is
    the primary way an agent learns your script's interface"; structured output — "Prefer structured
    formats — JSON, CSV, TSV"; "**Separate data from diagnostics:** send structured data to stdout
    and progress messages, warnings, and other diagnostics to stderr"; "**Idempotency.** Agents may
    retry commands"; "**Input constraints.** Reject ambiguous input with a clear error rather than
    guessing"; "**Meaningful exit codes.**"; "**Dry-run support.**"; "**Safe defaults.**"; and
    "**Predictable output size.** Many agent harnesses automatically truncate tool output beyond a
    threshold (e.g., 10-30K characters)". Pin versions on one-off runners (`npx eslint@9.0.0`) and
    state prerequisites in `SKILL.md` (same page). OpenAI's advice is the same shape: "scripts that
    run from the command line, print deterministic stdout, fail loudly with usage or error messages,
    and write outputs to known file paths when needed"
    (https://developers.openai.com/blog/skills-agents-sdk).

98. **An error a script prints, or a refusal a skill teaches, is one plain sentence: what went wrong
    and what to try.** **House choice**, matching the houseCARL cornerstone in `CLAUDE.md`. The spec
    states the reason: "When an agent gets an error, the message directly shapes its next attempt. An
    opaque 'Error: invalid input' wastes a turn. Instead, say what went wrong, what was expected, and
    what to try" (https://agentskills.io/skill-creation/using-scripts.md). The house rule tightens
    that to one sentence.

99. **Handle the error in the script; do not hand it back to the model.** Both hosts. "**Solve, don't
    defer** — When writing scripts for Skills, handle error conditions rather than deferring to
    Claude" and "Configuration parameters should also be justified and documented to avoid 'voodoo
    constants' (Ousterhout's law). If you don't know the right value, how will Claude determine it?"
    (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices).

100. **Do not install anything globally, and do not assume network access.** Both hosts. Claude Code:
     "**Full network access:** Skills have the same network access as any other program on the user's
     computer" but "**Global package installation discouraged:** Skills should only install packages
     locally to avoid interfering with the user's computer"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview); the same page
     records that the API surface has no network access and no runtime package installation, and that
     claude.ai varies. Codex documents no sandbox or network model for skill scripts, only that
     script approval can be surfaced: `approval_policy.granular.skill_approval` — "When `true`,
     skill-script approval prompts are allowed to surface"
     (https://learn.chatgpt.com/docs/codex-manual.md). A script that needs neither a network nor an
     install runs on both hosts, and one that survives being approved late or refused does one job,
     says what it did, and is safe to retry (rule 97).

### Instruction style

101. **Write in the imperative.** Both hosts, all three sources. "State what to do rather than
     narrating how or why" (https://code.claude.com/docs/en/skills.md); "Prefer using the imperative
     form in instructions"
     (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md); "Write
     imperative steps with explicit inputs and outputs"
     (https://learn.chatgpt.com/docs/build-skills.md).

102. **Give the reason in a clause where the instruction is one the model would otherwise reasonably
     do differently; give the bare instruction where the operation is fragile.** **House choice** on
     a within-vendor contradiction. Claude Code: "State what to do rather than narrating how or why,
     and apply the same conciseness test you would for CLAUDE.md content"
     (https://code.claude.com/docs/en/skills.md). skill-creator: "Try hard to explain the **why**
     behind everything you're asking the model to do. Today's LLMs are *smart*"
     (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md),
     restated as "**Explain the why.** Reasoning-based instructions ('Do X because Y tends to cause
     Z') work better than rigid directives"
     (https://agentskills.io/skill-creation/evaluating-skills.md). Codex is silent on rationale.
     Reason: split them the way both vendors already split specificity — be prescriptive "when
     operations are fragile, consistency matters, or a specific sequence must be followed", and give
     freedom, where "explaining *why* can be more effective than rigid directives", when multiple
     approaches are valid (https://agentskills.io/skill-creation/best-practices.md; the same framing
     at https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices). So: a
     reason clause, not a paragraph, and only where it changes what the model would do.

103. **No all-caps emphasis, and no `MUST` / `ALWAYS` / `NEVER` shouting. Give the one-clause reason
     instead.** **House choice** on a within-vendor contradiction, and the strongest position here:
     it overrides a live Anthropic page. Platform best-practices endorses hard emphasis — "**For
     strict requirements** (such as API responses or data formats): ALWAYS use this exact template
     structure" — and suggests "using stronger language such as 'MUST filter' instead of 'always
     filter'" (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices).
     skill-creator calls it a fault: "If you find yourself writing ALWAYS or NEVER in all caps, or
     using super rigid structures, that's a yellow flag — if possible, reframe and explain the
     reasoning" and "Try to explain to the model why things are important in lieu of heavy-handed
     musty MUSTs"
     (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md). OpenAI
     says nothing about capitalisation or emphasis at all. Reason: two further published lines point
     the same way and none points back — Anthropic's prompting guidance says the current models
     over-trigger on it, "The fix is to dial back any aggressive language. Where you might have said
     'CRITICAL: You MUST use this tool when...', you can use more normal prompting like 'Use this
     tool when...'"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices),
     and OpenAI says its current generation "can be more sensitive to instructions contained in
     skills and other files" and recommends auditing them
     (https://developers.openai.com/api/docs/guides/latest-model.md). Both vendors' shipped skills
     break this — skill-creator ships `ALWAYS use this exact template:` as a pattern to copy, `docx`
     and `xlsx` write "Do NOT use for…", OpenAI's `security-best-practices` writes "identify ALL
     languages and ALL frameworks", and `linear` writes "**Follow these steps in order. Do not skip
     steps.**" (https://github.com/openai/skills) — and a shipped skill contradicting its vendor's
     stated rule is practice, not evidence for the practice.

104. **Say what to do, not what not to do.** Both hosts. "**Tell Claude what to do instead of what
     not to do** — Instead of: 'Do not use markdown in your response'. Try: 'Your response should be
     composed of smoothly flowing prose paragraphs.'"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices)
     and "Positive examples of the communication style you want tend to be more effective than
     instructions about what not to do"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5).
     Where a prohibition is genuinely load-bearing, Codex's required list keeps it — "Which facts the
     model must not infer" (https://developers.openai.com/plugins/build/skills.md) — so state it
     once, plainly, with its reason. A boundary clause in the description is a different matter: rule
     67.

105. **Pick one default; do not offer a menu.** Both hosts. "**Avoid offering too many options** —
     Don't present multiple approaches unless necessary"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices); "**Provide
     defaults, not menus.** When multiple tools or approaches could work, pick a default and mention
     alternatives briefly rather than presenting them as equal options", one of the three named
     causes of an agent wasting time being "too many options presented without a clear default"
     (https://agentskills.io/skill-creation/best-practices.md).

106. **Use one term for one thing, throughout the skill, and never state the same rule twice in
     different words.** Both hosts. "Choose one term and use it throughout the Skill… Consistency
     helps Claude parse and follow instructions", with the bad example "Mix 'API endpoint', 'URL',
     'API route', 'path'"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices). The cost is
     documented on the other host: "unclear or conflicting guidance in a skill file may cause the
     model to pause and block work early"
     (https://developers.openai.com/api/docs/guides/latest-model.md), and for the previous generation
     "poorly-constructed prompts containing contradictory or vague instructions can be more damaging
     to GPT-5 than to other models" because the model "expends reasoning tokens searching for a way
     to reconcile the contradictions"
     (https://developers.openai.com/cookbook/examples/gpt-5/gpt-5_prompting_guide).

107. **Include examples where output shape matters, as input/output pairs.** Both hosts. "For Skills
     where output quality depends on seeing examples, provide input/output pairs just like in regular
     prompting … Examples convey the desired style and level of detail to Claude more clearly than
     descriptions alone"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices); "Include 3–5
     examples for best results", relevant, diverse, and wrapped in `<example>` tags
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices);
     Codex asks for the same section: "**Examples:** Provide examples of possible inputs, along with
     the desired output from the model"
     (https://developers.openai.com/api/docs/guides/prompt-engineering.md).

108. **Use headings and numbered steps where order or completeness matters.** Both hosts. "Provide
     instructions as sequential steps using numbered lists or bullet points when the order or
     completeness of steps matters"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices);
     Codex: Markdown headers "communicate hierarchy to the model" and XML tags "help delineate where
     one piece of content … begins and ends"
     (https://developers.openai.com/api/docs/guides/prompt-engineering.md). OpenAI's
     prose-over-lists guidance is about the model's answers, not about instruction text
     (https://developers.openai.com/api/docs/guides/latest-model.md).

109. **Do not put anything in the body that goes out of date on someone else's schedule.** Both
     hosts. "Don't include information that will become outdated"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices).

110. **Do not add scaffolding the current models already do.** Both hosts. "Claude Opus 5 verifies
     its own work without being told to. If your prompt contains explicit verification instructions …
     remove them: instructions like these cause over-verification on Claude Opus 5, and removing them
     reduces wasted tokens with no loss in quality" and "Avoid instructing re-checks it already
     performs"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5);
     "**Refactor existing prompts and skills.** Skills developed for prior models are often too
     prescriptive for Claude Fable 5 and can degrade output quality"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-fable-5);
     "Do not introduce unsolicited warnings, disclaimers, approval flows, or safety/compliance
     checklists due to hypothetical risk"
     (https://developers.openai.com/api/docs/guides/latest-model.md).

111. **Do not tell the model to show or transcribe its reasoning.** Claude Code on its face, harmless
     on Codex, so it holds for the bundle. "Prompts, skills, or harness instructions that tell the
     model to echo, transcribe, or explain its internal reasoning as response text can trigger the
     `reasoning_extraction` refusal category on Claude Fable 5 … Audit existing skills and system
     prompts for reflection or show-your-thinking instructions when migrating"
     (https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-fable-5).

112. **The body does not claim precedence over the user, and carries no precedence boilerplate.**
     **House choice.** OpenAI supplies the sentence and recommends stating it: "The user's
     instructions take precedence over guidelines provided in a skill. If explicit user instructions
     conflict with a skill's instructions, prioritize the user's instructions"
     (https://developers.openai.com/api/docs/guides/latest-model.md). Claude Code never ranks a skill
     against `CLAUDE.md` and says of `CLAUDE.md` itself that it "is delivered as a user message after
     the system prompt… there's no guarantee of strict compliance"; the Anthropic platform is silent
     on the conflict. Reason: rather than adding a precedence line to fourteen skills — fourteen
     copies of context that only matters when a skill is already wrong — write each skill so the
     question does not arise: it gives the method for the job it names and does not restate,
     contradict, or claim to override a repo rule or the person asking. See also rule 76.

113. **Write the body provider-neutral, and use no Claude-only body syntax.** **House choice.** Codex
     requires the neutral wording for a Claude bundle submitted there: "Replace Claude-specific
     references in the skill instructions with provider-neutral language, such as 'the model.' Keep a
     product name only when the instruction genuinely applies to that product", and artifact-driving
     instructions must be removed — "Artifact-specific HTML, persistence, refresh behavior, and
     interactions aren't preserved"
     (https://developers.openai.com/plugins/guides/submit-claude-plugin). Claude Code's own body
     features fail open rather than erroring: `!` shell blocks, `@` file attachment, skill-declared
     hooks, the `ultrathink` token, and `${CLAUDE_PROJECT_DIR}` / `${CLAUDE_SESSION_ID}` substitution
     reach the model "as literal text" on other surfaces
     (https://code.claude.com/docs/en/skills.md), and a failed injected command aborts the whole
     invocation — "Claude never sees the skill content for that invocation" (same page). Reason: one
     source ships to both hosts; text that silently means nothing is worse than text that fails, a
     feature that only fires on one host produces a different skill on each, and a rewrite step at
     build is a second copy to keep in sync.

114. **Name an MCP tool exactly as `standards/NAMING.md` spells it — `housecarl_<snake_case>` — and
     say once in the skill which server it comes from.** **House choice** against a stated rule. The
     platform advice is to qualify: "always use fully qualified tool names to avoid 'tool not found'
     errors. **Format:** `ServerName:tool_name`… Without the server prefix, Claude may fail to locate
     the tool" (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices).
     Codex is silent on tool naming inside a body. Reason: the server prefix is host-specific, a
     prefix written for one host is wrong text in the other, and `standards/NAMING.md` fixes the bare
     name as the project's spelling. Where the prefix genuinely matters, name it in prose rather than
     baking it into every mention.

115. **Stay inside Codex's byte ceilings.** Codex sets hard ones for skills imported through MCP —
     `SKILL.md` 256 KiB, each supporting file 1 MiB, all resources for one skill 5 MiB, up to 100
     files per skill, up to five skills per import
     (https://learn.chatgpt.com/docs/codex-manual.md) — plus the archive caps recorded in the
     packaging silences above.
     No Anthropic page states a byte ceiling on bundled files; its nearest statement is about
     read-time context cost, not storage. They are the only hard numbers and nothing on the Claude
     side conflicts.

### Where the sources are silent or disagree

- **All-caps.** Anthropic contradicts itself, OpenAI is silent, and both vendors' shipped skills use
  all-caps anyway. House position and reason in rule 103; it is the rule here most likely to draw a
  challenge.
- **Rationale in instructions.** Claude Code says state what to do rather than narrating why;
  skill-creator and the open guidance say explain the why; Codex is silent. House position in rule
  102.
- **Table-of-contents threshold.** 100 lines on the platform page, 300 in skill-creator; Codex
  silent. House choice: 100 (rule 87).
- **Required content in a body.** Codex lists six things; the spec says "There are no format
  restrictions". House choice: adopt the Codex list (rule 82).
- **Over-long bodies.** Neither vendor says what a host does with a body past the recommendation.
  Treat the limit as real anyway: on Claude Code the part over 5,000 tokens is what compaction drops
  (rule 79).
- **Whether Codex re-reads a body on later turns is unstated**, so rule 79 rests on Claude Code's
  mechanics plus the fact that standing-instruction phrasing is harmless elsewhere.
- **How a supporting file is read.** The platform docs say "Claude uses bash Read tools"; no Claude
  Code page confirms it, and Codex says nothing about whether it resolves paths for the model. Rules
  85, 91 and 92 are written so either behaviour works.
- **Claude Code's `@`-import mechanics inside a body** are named in the feature comparison and
  documented nowhere: no page describes when the attach happens or what it costs. Another reason for
  rule 113.
- **Body length in shipped practice.** Anthropic's own skills run 26 to 561 lines, and `claude-api`
  at 561 exceeds Anthropic's own "under 500 lines" guidance with no published explanation. Practice,
  not rule, and not a licence to run long.
- **No source states a threshold at which a body should be split by domain**, so rule 88's split is
  stated as an always rather than above some size.

## How a skill is tested

116. **Triggering and task outcome are two measurements, made separately.** Both hosts. Claude Code:
     "Seeing a skill trigger tells you Claude found it, not that it did what you intended. To know a
     skill is working, measure two things separately: whether Claude invokes it on the prompts it
     should, and whether the output matches what you expect when it does"
     (https://code.claude.com/docs/en/skills.md). Codex: "Review both activation and output quality"
     (https://developers.openai.com/plugins/build/skills.md). Both split the remedy the same way: a
     wrong activation is a description problem, a wrong result is an instruction problem — "Refine
     the description when the skill activates at the wrong time. Refine the instructions when it
     chooses the right workflow but produces an inconsistent result" (Codex, same page); Claude
     Code's troubleshooting lists say the same in reverse ("Make the description more specific" for
     over-triggering; "Check the description includes keywords users would naturally say" for
     under-triggering).

117. **Structure is checked mechanically, by the host's own tool, before anything behavioural.**
     **House choice** on a host difference. Claude Code ships one: "run `claude plugin validate` on
     the skills directory, for example `claude plugin validate .claude/skills`… Requires Claude Code
     v2.1.233 or later", with `--json` from v2.1.259 (https://code.claude.com/docs/en/skills.md;
     CHANGELOG v2.1.259). It reports a parse failure — "`YAML frontmatter failed to parse: ...`…
     Until you do, a session reads no frontmatter fields from the file" — and does not follow
     symlinks (https://code.claude.com/docs/en/plugin-marketplaces.md). Codex has no such command;
     its mechanical check runs at submission and publishes named codes instead
     (`skill_manifest_missing`, `skill_frontmatter_yaml_malformed`, `skill_description_too_long`,
     `skill_identity_too_long`, and about thirty-five more,
     https://developers.openai.com/plugins/deploy/submission-errors). Reason for the house choice:
     hold the bundle to the union of the three published structural checks — Claude Code's parse
     check, the Codex submission codes, and the spec's `skills-ref validate` — because one tree ships
     to both hosts and the strictest check is the only one that clears all of them. A structural
     failure is not a judgement call.

118. **Triggering is measured with prompts that should trigger and prompts that should not.**
     Anthropic publishes numbers: "Aim for about 20 queries: 8-10 that should trigger and 8-10 that
     shouldn't"; "Run each query multiple times (3 is a reasonable starting point) and compute a
     **trigger rate**… A should-trigger query passes if its trigger rate is above a threshold (0.5 is
     a reasonable default). A should-not-trigger query passes if its trigger rate is below that
     threshold"; a 60/40 train/validation split, "Select the best iteration by its validation pass
     rate", "Five iterations is usually enough"
     (https://agentskills.io/skill-creation/optimizing-descriptions.md). The negative cases carry the
     weight: "The most valuable negative test cases are **near-misses** — queries that share keywords
     or concepts with your skill but actually need something different", while "'Write a fibonacci
     function' — obviously irrelevant, tests nothing". And the fix for a failure is not the failed
     wording: "Avoid adding specific keywords from failed queries — that's overfitting. Instead, find
     the general category or concept those queries represent" (same page).

119. **The eval set covers Codex's five categories and is scored with Anthropic's numbers.** **House
     choice.** Codex publishes the categories: "Test with representative requests from the use-case
     inventory: 1. Direct requests that should activate the skill. 2. Indirect requests that express
     the same goal. 3. Incomplete inputs that should trigger a follow-up question. 4. Requests that
     should not activate the skill. 5. Edge cases where the skill must avoid inventing information or
     taking an unsupported action" (https://developers.openai.com/plugins/build/skills.md), and no
     numbers. Reason: the categories are a shape and the numbers of rule 118 are a measurement, and
     Codex supplies no measurement of its own.

120. **Outcome is measured against the same task run without the skill, in a fresh session.** Both
     hosts. Claude Code: "The check for both is a baseline comparison. Collect a few realistic
     prompts, run each one in a fresh session with the skill available and again with it disabled,
     and compare the results. A fresh session matters because leftover context from authoring the
     skill will mask gaps in the written instructions"
     (https://code.claude.com/docs/en/skills.md). The spec explains the isolation: "Each eval run
     should start with a clean context… In environments that support subagents (Claude Code, for
     example), this isolation comes naturally: each child task starts fresh. Without subagents, use a
     separate session for each run" (https://agentskills.io/skill-creation/evaluating-skills.md).
     Codex requires a clean environment for an imported plugin — "Test the imported skills in a clean
     environment. Confirm that each skill can find its referenced files and executables and doesn't
     depend on undeclared local packages, files, or credentials"
     (https://developers.openai.com/plugins/guides/submit-claude-plugin) — and that the tree tested
     is the tree shipped: "You tested the skills locally with the final file tree"
     (https://developers.openai.com/plugins/deploy/submission).

121. **Expectations are verifiable statements, graded pass or fail with quoted evidence.** Both
     hosts, from the Anthropic method. Good: "'The output file is valid JSON' — programmatically
     verifiable"; "'The bar chart has labeled axes' — specific and observable". Weak: "'The output is
     good' — too vague to grade"; "'The output uses exactly the phrase …' — too brittle". "Not
     everything needs an assertion… Reserve assertions for things that can be checked objectively",
     and for mechanical checks "use a verification script — scripts are more reliable than LLM
     judgment" (https://agentskills.io/skill-creation/evaluating-skills.md). Grading is binary:
     "**PASS**: Clear evidence the expectation is true AND the evidence reflects genuine task
     completion, not just surface-level compliance"; "**When uncertain**: The burden of proof to pass
     is on the expectation"; "**No partial credit**" (skill-creator `agents/grader.md`). Evidence
     "should quote or reference the output, not just state an opinion" (evaluating-skills.md). The
     evals are treated as fallible: "A passing grade on a weak assertion is worse than useless — it
     creates false confidence" (grader.md).

122. **Evals come before the prose.** Both hosts, since nothing in it is host-specific. "**Create
     evaluations BEFORE writing extensive documentation.** This ensures your Skill solves real
     problems rather than documenting imagined ones", in five steps: identify gaps by running the
     task with no skill, create evaluations, establish a baseline, "Write minimal instructions:
     Create just enough content to address the gaps and pass evaluations", iterate
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices). The honest
     check that goes with it: "if the agent already handles the entire task well without the skill,
     the skill may not be adding value"
     (https://agentskills.io/skill-creation/best-practices.md). Assertions are refined after the
     first run — "you often don't know what 'good' looks like until the skill has run"
     (https://agentskills.io/skill-creation/evaluating-skills.md).

123. **What a tested skill looks like.** It has, in its own directory (rule 14), an eval set naming
     the prompts it should and should not trigger on, and expectations describing what a correct
     result contains — the published shape is a prompt, optional input files, an expected-output
     description in plain words, and a list of verifiable statements (skill-creator
     `references/schemas.md`;
     https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices, whose
     published record carries `skills`, `query`, `files`, `expected_behavior`). It has been run
     against a no-skill baseline at least once, and the result was recorded with the evidence that
     produced it.

124. **Cost is part of the result.** Both hosts on the measurement, Claude Code only on the standing
     half. The runs record `total_tokens` and `duration_ms` per configuration, and the aggregate
     reports mean, standard deviation, min and max for pass rate, time and tokens, plus a delta
     between with-skill and without-skill, over `runs_per_configuration` (glossed "e.g. 3")
     (skill-creator `references/schemas.md`). The rule of thumb: "A skill that adds 13 seconds but
     improves pass rate by 50 percentage points is probably worth it. A skill that doubles token
     usage for a 2-point improvement might not be"
     (https://agentskills.io/skill-creation/evaluating-skills.md). Claude Code measures the standing
     cost separately: `/skill-doctor` "flags skills in the listing that have never been invoked and
     says where to turn them off", and the `/plugin` Stats tab shows what each skill costs in context
     and how often it is used (https://code.claude.com/docs/en/skills.md;
     https://code.claude.com/docs/en/discover-plugins.md). Codex publishes no equivalent.

125. **A blind comparison decides whether a new version replaces the old; the pass rate and the token
     and time deltas are reported beside it.** **House choice** on a within-vendor contradiction.
     Anthropic ships a comparator that scores two outputs "WITHOUT knowing which skill produced
     them", on content and structure rubrics, with assertions demoted to secondary evidence —
     "Use expectation scores as secondary evidence (not the primary decision factor)" and "**Output
     quality first**" (skill-creator `agents/comparator.md`) — while the benchmark schema and grader
     make `pass_rate` the headline aggregate and `history.json` records `expectation_pass_rate` per
     version. No page reconciles them; no Codex page describes comparing two versions of a skill.
     Reason: the comparator is the only one of the two written to be read blind, and a pass rate over
     weak assertions is exactly the false confidence the grader warns about.

126. **A skill starts with two or three outcome cases and grows the set where a run showed a gap;
     the trigger set is sized by rule 118's twenty.** **House choice** on three published numbers
     that serve three different purposes: "At least three evaluations created" (Anthropic's pre-share
     checklist), "3–5 representative queries per Skill" (Anthropic's enterprise review gate,
     https://platform.claude.com/docs/en/agents-and-tools/agent-skills/enterprise), "at least five
     positive test cases and three negative test cases" (OpenAI submission,
     https://developers.openai.com/plugins/deploy/submission). Anthropic's own advice cuts against
     starting large: "**Start with 2-3 test cases.** Don't over-invest before you've seen your first
     round of results" (https://agentskills.io/skill-creation/evaluating-skills.md). Reason: twenty
     is the only trigger-set size published with a scoring method attached, and nothing else gates
     anything.

127. **Test on the models the bundle actually runs under.** **House choice** on a within-vendor
     difference. Anthropic asks for breadth — "Tested with Haiku, Sonnet, and Opus"
     (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices) — while
     skill-creator says, for the trigger measurement, "Use the model ID from your system prompt (the
     one powering the current session) so the triggering test matches what the user actually
     experiences"
     (https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md). Codex
     states no model-coverage requirement. Reason: a trigger rate measured on a model nobody uses is
     not the number the standard cares about.

128. **The bundle is tested by the Anthropic method and then re-run once on Codex against Codex's
     five categories.** **House choice** on a silence. Codex has no eval harness, no test runner, no
     trigger-rate metric, no CLI validator and no scoring rubric; its published mechanical bar is the
     submission code list plus an automated policy and security scan of skill content, which "may
     block submission or require remediation", and its published behavioural bar is human review of
     submitted cases (https://developers.openai.com/plugins/deploy/submission). Reason: Codex
     publishes a procedure but no measurement, and running the Claude Code measurement twice is
     cheaper than inventing a second one. The testing consequence of the two hosts' different
     invocation switches (rule 72) is the same: a measurement is run per host to confirm what took.

### Where the sources are silent or disagree

- **`claude plugin eval` is not documented.** The docs index contains no page whose path includes
  "eval", and the plugins-reference debugging section lists only `plugin validate` and `--debug`.
  **House choice:** the standard names no eval command as a method, because a method the standard
  cannot cite is a method a reader cannot run.
- **Anthropic contradicts itself on whether a runner exists.** The platform docs say "There is not
  currently a built-in way to run these evaluations. Users can create their own evaluation system"
  (https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices); the Claude Code
  docs say "The `skill-creator` plugin automates the comparison loop inside Claude Code", installed
  with `/plugin install skill-creator@claude-plugins-official`
  (https://code.claude.com/docs/en/skills.md). **House choice:** both are true and the bundle treats
  the harness as a plugin, not a host feature — the method in rules 118 to 121 is written so it can
  be carried out by hand, because Codex has no harness at all.
- **The spec takes no position on behavioural testing at all**, and `skills-ref` "is intended for
  demonstration purposes only. It is not meant to be used in production"
  (https://agentskills.io/specification; skills-ref README). It is used for what it does check — the
  closed frontmatter field set and the name, description and directory rules — and nothing else.
- **Nothing says what an unknown frontmatter key does in Claude Code or Codex**, while `skills-ref`
  errors on one. The testing consequence is that a structural pass on one validator is not a pass on
  the others, which is why rule 117 takes the union.
- **Where the skill listing sits is stated three ways.** The Anthropic platform says name and
  description "are loaded into the system prompt"; Claude Code says only that it "loads a listing of
  skill names and descriptions into context"; the spec's client guide treats placement as a host
  choice. Nothing in the bundle depends on which is true; it relies only on the fact that name and
  description are present at decision time and the body is not.

## House choices

Every house choice in this standard, with the rule it sits in.

| # | Rule | Choice | Reason |
|---|---|---|---|
| 1 | 1 | Pre-decision knowledge does not go in a skill | The body is read after the decision is made |
| 2 | 4 | The reference-versus-procedure split is used on Codex too | Codex is silent and nothing there argues against it |
| 3 | 6 | houseCARL skills carry no hooks | One tree, two hosts; a skill-carried hook exists on one host only |
| 4 | 7 | A skill stays a procedure; isolation is said in the procedure | Skill preloading into a subagent is Claude Code only |
| 5 | §1 table, row 3 | That houseCARL exists lives in standing context, not a skill | Rule 1; no vendor page addresses a skill bundle over an MCP server |
| 6 | §1 silences | `CLAUDE.md` and `AGENTS.md` say the same facts rather than dividing them | Nobody documents what happens when both are present |
| 7 | §1 silences | A skill stands alone and never assumes another's body is loaded | Only Claude Code documents chaining |
| 8 | 11 | Folder name equals frontmatter `name`, kebab-case | One string, so disk and invocation cannot drift |
| 9 | 12 | No version number in a folder name, and no rename on rewrite | The name is what a user types; a rename needs a marketplace `renames` entry |
| 10 | 13 | `references/`, `scripts/`, `assets/` on both hosts | Codex and the spec name them; Claude Code has no competing convention |
| 11 | 14 | Eval set at `evals/eval_set.json` in the skill folder | Tests travel with what they test; the documented name is `evals/evals.json` and nothing but us reads the file |
| 12 | 20 | Every skill name is distinctive without a prefix | Codex does not resolve collisions |
| 13 | 22 | An umbrella skill carries the bundle's identity on Codex | Codex gives locally installed skills no namespace |
| 14 | 23 | Install a copy under `~/.agents/skills/` rather than expect Codex to read the Claude tree | Codex is silent on `.claude/skills` |
| 15 | 28 | No root-level `SKILL.md`, no flat `commands/` | One layout both hosts accept beats two and a conversion step |
| 16 | 30 | Default `skills/` layout, so the manifest `skills` field is not load-bearing | The two hosts' rules for the field do not reconcile |
| 17 | 31 | Real directories, never symlinks | A layout that only works before packaging fails at packaging |
| 18 | 32 | Two packages generated from the one `.claude/skills/` source | Codex excludes MCP wiring from a skills-only upload |
| 19 | 34 | Ship a Codex package rather than rely on conversion or import | Both routes are stated to change what they carry |
| 20 | §2 silences | Follow `$HOME/.agents/skills` over `~/.codex/skills` | It is the path in the discovery page's scope table and the cross-client convention |
| 21 | §2 silences | `.claude-plugin/marketplace.json` is a Claude Code file only | The guide governing our direction says not to rely on it |
| 22 | 38 | User-visible changes go in `plugin/CHANGELOG.md` under `## Unreleased` | The file is for people; no page states a location or format |
| 23 | 40 | `SKILL.md` uppercase, no BOM | The uppercase spelling is the one every source accepts |
| 24 | 43 | Always write `name`, equal to the folder name | Required on two surfaces, free on the third |
| 25 | 44 | ASCII kebab-case, no reserved words, no angle brackets in `name` | The tighter rule is the one every surface accepts, and it costs nothing |
| 26 | 45 | `name` at most 54 characters | `housecarl:` spends 10 of Codex's 64-character identity cap |
| 27 | 48 | No angle brackets in `description` | The listing wraps and escapes the value; Anthropic's own validator bans them |
| 28 | 49 | The description cap is 1,024, not the Help Center's 200 | 1,024 is stated by every surface the bundle reaches; 200 governs the claude.ai upload path |
| 29 | 50 | Frontmatter is `name`, `description`, `license` and nothing else | One file valid everywhere; no field whose behaviour depends on the host |
| 30 | 51 | No `allowed-tools` | It grants permissions on one host and does nothing on the other |
| 31 | 52 | No `compatibility`, no `metadata`; declare the server where a host acts | A `compatibility` string is text nothing reads |
| 32 | 53 | Write `license`, short, same on every skill | Legal everywhere, and it travels with a skill copied out of the bundle |
| 33 | 55 | `interface.short_description` is human text, not the trigger | One line is read by a person, the other by a model |
| 34 | 57 | Booleans spelled `true` / `false` | The strict spelling is the one both hosts read |
| 35 | §3 silences | Write only fields inside the allowed six rather than answer the unknown-key question | Nobody states what a host does with an unknown key |
| 36 | §3 silences | The kebab identifier, not a human display label | It is also the folder name and the command segment |
| 37 | §3 silences | Write to the specification where OpenAI defers but does not enforce | Nothing valid under the specification is invalid under Codex's checks |
| 38 | §3 silences | Do not use `display-name`, `default-enabled`, `fallback`, `dependencies`, `SKILL.json` | Undocumented behaviour, and outside the allowed six |
| 39 | 65 | Every description fits in 400 characters | Derived from Codex's 8,000-character worst case; survives on the tightest host |
| 40 | 67 | A boundary clause where a skill's subject borders another's | The cheapest fix for a wrong load; costs a dozen words |
| 41 | 70 | No declared arguments; a skill works when invoked with none | The placeholder grammar exists on one host only |
| 42 | 71 | No skill depends on being stacked | Stacking is Claude Code only |
| 43 | 72 | Neither invocation switch is set on either host | No skill performs the side effect itself, and hiding one defeats triggering |
| 44 | 74 | No skill sets `context: fork` | These skills carry guidance as well as steps, and Codex has no counterpart |
| 45 | 76 | No description or body claims system-level authority | On one host the text sits at user-message priority |
| 46 | §4 silences | Assume shortening cuts from the end and a skill can vanish | The cheap assumption; the opposite is unrecoverable at runtime |
| 47 | §4 silences | Author against Codex's 8,000 characters, not its token figure | The smaller and more concrete of two unreconciled units |
| 48 | §4 silences | Write `policy.allow_implicit_invocation` if it is ever set | It is the spelling the validator publishes |
| 49 | §4 silences | Follow the enterprise page on how many skills is too many | It is the one that names a failure mode |
| 50 | 77 | Body under 500 lines and 5,000 tokens on both hosts | The only published number; no Codex rule competes |
| 51 | 82 | Adopt Codex's six-item body list on both hosts | The only required-content list either vendor publishes |
| 52 | 87 | Table of contents over 100 lines, not 300 | The stated reason, partial reads, applies at 100 |
| 53 | 88 | Game-generic and Skyrim-specific knowledge split structurally | Reuse: a future game target keeps the generic half |
| 54 | 89 | Domain knowledge as tables and lists, not prose | A lookup should not require reading a paragraph |
| 55 | 98 | An error or refusal is one plain sentence | Matches the houseCARL cornerstone; the message shapes the next attempt |
| 56 | 102 | A reason clause only where it changes what the model would do | Splits the two vendors' opposite advice along their own fragility line |
| 57 | 103 | No all-caps, no MUST/ALWAYS/NEVER | Two current-generation pages and skill-creator against one platform page |
| 58 | 112 | No precedence boilerplate; write so the question does not arise | Fourteen copies of a line that matters only when a skill is already wrong |
| 59 | 113 | Provider-neutral body, no Claude-only body syntax, written that way in the source | Text that silently means nothing is worse than text that fails |
| 60 | 114 | Bare `housecarl_<snake_case>` plus the server named once, not `ServerName:tool_name` | The prefix is host-specific; `standards/NAMING.md` fixes the bare name |
| 61 | 117 | Hold the bundle to the union of the three structural checks | One tree, two hosts; only the strictest clears all of them |
| 62 | 119 | Codex's five categories, Anthropic's numbers | Categories are a shape; Codex publishes no measurement |
| 63 | 125 | A blind comparison decides a version change | Only the comparator is written to be read blind |
| 64 | 126 | Two or three outcome cases to start; twenty trigger queries | The only trigger-set size published with a scoring method |
| 65 | 127 | Test on the models the bundle runs under | A trigger rate on a model nobody uses is not the number that matters |
| 66 | 128 | Anthropic's method, re-run once against Codex's five categories | Codex publishes a procedure but no measurement |
| 67 | §6 silences | Name no eval command as a method | A method the standard cannot cite is one a reader cannot run |
| 68 | §6 silences | Treat the comparison harness as a plugin, not a host feature | Codex has no harness, so the method must be runnable by hand |

## Sources

All fetched 2026-09-05.

**Anthropic — Claude Code**

- https://code.claude.com/docs/en/skills.md
- https://code.claude.com/docs/en/plugins.md
- https://code.claude.com/docs/en/plugins-reference.md
- https://code.claude.com/docs/en/plugin-marketplaces.md
- https://code.claude.com/docs/en/plugin-dependencies.md
- https://code.claude.com/docs/en/agent-sdk/plugins.md
- https://code.claude.com/docs/en/commands.md
- https://code.claude.com/docs/en/sub-agents.md
- https://code.claude.com/docs/en/features-overview.md
- https://code.claude.com/docs/en/settings-reference.md
- https://code.claude.com/docs/en/memory.md
- https://code.claude.com/docs/en/hooks.md
- https://code.claude.com/docs/en/discover-plugins.md
- Claude Code CHANGELOG (anthropics/claude-code), entries v2.1.178, v2.1.186, v2.1.199, v2.1.216, v2.1.218, v2.1.239, v2.1.246, v2.1.252, v2.1.257, v2.1.259, v2.1.260

**Anthropic — platform, support, blog, and shipped skills**

- https://platform.claude.com/docs/en/agents-and-tools/agent-skills/overview
- https://platform.claude.com/docs/en/agents-and-tools/agent-skills/best-practices
- https://platform.claude.com/docs/en/agents-and-tools/agent-skills/enterprise
- https://platform.claude.com/docs/en/agents-and-tools/agent-skills/claude-api-skill
- https://platform.claude.com/docs/en/agents-and-tools/agent-skills/skills-guide
- https://platform.claude.com/docs/en/managed-agents/skills
- https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices
- https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5
- https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-fable-5
- https://support.claude.com/en/articles/12512198-creating-custom-skills
- https://claude.com/blog/steering-claude-code-skills-hooks-rules-subagents-and-more
- https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/SKILL.md
- https://raw.githubusercontent.com/anthropics/skills/main/skills/skill-creator/scripts/package_skill.py
- anthropics/skills — `skills/skill-creator/scripts/quick_validate.py`, `agents/grader.md`, `agents/comparator.md`, `references/schemas.md`
- https://github.com/anthropics/claude-plugins-official/blob/main/plugins/skill-creator/skills/skill-creator/SKILL.md

**Agent Skills specification and reference implementation**

- https://agentskills.io/specification
- https://agentskills.io/client-implementation/adding-skills-support.md
- https://agentskills.io/skill-creation/best-practices.md
- https://agentskills.io/skill-creation/optimizing-descriptions.md
- https://agentskills.io/skill-creation/using-scripts.md
- https://agentskills.io/skill-creation/evaluating-skills.md
- https://raw.githubusercontent.com/agentskills/agentskills/main/skills-ref/src/skills_ref/validator.py
- https://raw.githubusercontent.com/agentskills/agentskills/main/skills-ref/README.md
- agentskills/agentskills — `skills-ref/src/skills_ref/prompt.py`

**OpenAI**

- https://learn.chatgpt.com/docs/build-skills.md
- https://learn.chatgpt.com/docs/build-plugins.md
- https://learn.chatgpt.com/docs/codex-manual.md
- https://learn.chatgpt.com/docs/config-file/config-reference.md
- https://learn.chatgpt.com/docs/hooks.md
- https://learn.chatgpt.com/docs/import
- https://learn.chatgpt.com/docs/plugins.md
- https://learn.chatgpt.com/docs/skills-and-plugins.md
- https://learn.chatgpt.com/docs/agent-configuration/agents-md.md
- https://learn.chatgpt.com/docs/agent-configuration/subagents.md
- https://learn.chatgpt.com/docs/customization/memories.md
- https://learn.chatgpt.com/docs/enterprise/plugin-management
- https://learn.chatgpt.com/guides/best-practices
- https://learn.chatgpt.com/use-cases/reusable-codex-skills.md
- https://developers.openai.com/plugins/build/plugins.md
- https://developers.openai.com/plugins/build/skills.md
- https://developers.openai.com/plugins/deploy/submission
- https://developers.openai.com/plugins/deploy/submission-errors.md
- https://developers.openai.com/plugins/guides/submit-claude-plugin
- https://developers.openai.com/api/docs/guides/tools-skills.md
- https://developers.openai.com/api/docs/guides/latest-model.md
- https://developers.openai.com/api/docs/guides/prompt-engineering.md
- https://developers.openai.com/blog/skills-agents-sdk
- https://developers.openai.com/cookbook/examples/gpt-5/gpt-5_prompting_guide
- https://github.com/openai/skills

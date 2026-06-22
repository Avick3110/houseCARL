# dialogue-authoring CTDA / quest reference — status & provenance

Provenance and staleness record for the **condition-decode and quest-structure** references added in 1.3.1:
`condition-functions.md`, `dialogue-branch.md`, `quest-objectives-tab.md`. (The older
`dialogue-flow-model.md`, `seq-file-format.md`, and `voice-file-naming.md` predate this file and were
empirically grounded through houseCARL reads — see their own headers.)

## What these are

Hand-curated references that let houseCARL **decode** a condition (CTDA) and read the quest layer dialogue
gates on — turning a read-back `Conditions`/`Stages`/`Objectives` array into meaning, instead of echoing raw
bytes. They are the **keystone for the static condition-lint** (shipped 1.3.1, roadmap item 4): you cannot
lint a condition you cannot decode.

**These are curated, not generated.** Like the bundled SkyPatcher/SPID/KID grammar references, they ship as
static prose and carry a **hand-maintained-staleness duty** — they are deliberately kept **out of** the
by-construction generator (which targets `mutagen-reference` only), so authoring them never contaminates the
generated corpus.

## Two-layer sourcing (read this before trusting a fact)

| Layer | Source | Confidence |
|---|---|---|
| **Structure** — which condition functions exist, each one's parameter shape, the Run On enum, the compare operators, the DLBR/Quest/QuestStage/QuestObjective record fields | **By construction from Mutagen**, via houseCARL's own `mutagen-reference` (`arms.jsonl` / `records.jsonl` / `structs.jsonl` / `enums.jsonl`), read at the 1.3.1 dev head. This is exactly what `housecarl_read_record` surfaces and what the write tools compose. | **High** — it *is* the model houseCARL decodes. Re-derivable any time from `mutagen-reference`. |
| **Semantics** — what each function tests, Run On scoping rules (Subject = speaker, Target = player), DLBR flag behavior, objective/stage display behavior | **Creation Kit wiki** (UESP-hosted, `ck.uesp.net`: *Condition Functions*, *Conditions*, *Dialogue Speech Checks*, *Dialogue Branch*, *Quest Objectives Tab*, *SetObjectiveDisplayed/Completed*) **+ modding-domain knowledge.** | **High** for the well-established facts; domain-authored where noted. |

### Why the wiki wasn't fetched directly (the source-substitution, stated plainly)

At authoring time (**2026-06-22**) the canonical **creationkit.com** wiki was **down for maintenance** (every
page 302-redirected to a Bethesda maintenance page), and direct automated fetches of both the Internet
Archive and the UESP mirror were unavailable in the build environment. So the **structural backbone was taken
by construction from Mutagen** (a *better* source for what houseCARL actually decodes than the wiki would be),
and the **semantics were sourced from the UESP-hosted CK wiki via web search + domain knowledge**, rather
than by bundling the wiki pages verbatim. This was a deliberate, surfaced call (Aaron-approved 2026-06-22),
not a silent workaround — but it means the prose carries the staleness duty below.

## Coverage

- **Condition functions:** a **curated dialogue/quest subset** (~40 functions) documented with full param
  shapes + semantics. Mutagen models **every** condition function (hundreds — all combat/VATS/weather/AI
  ones too); to decode one outside the subset, read its `ConditionData` arm directly and look it up in
  `mutagen-reference`. The CTDA-vs-Papyrus-only distinction is covered as a section in `condition-functions.md`.
- **Dialogue Branch:** all fields + the three flags (`TopLevel`/`Blocking`/`Exclusive`) + Category.
- **Quest stages & objectives:** stages, log entries, objectives, targets, and the index convention.

## Staleness duty

- **Structure** — re-derive from `mutagen-reference` whenever Mutagen updates; if a function's param shape
  here ever disagrees with `mutagen-reference`, **`mutagen-reference` wins** (it's by construction).
- **Semantics** — when **creationkit.com is back**, cross-check the curated descriptions, the DLBR flag
  behavior, and the objective/stage notes against the canonical pages, and correct any drift. Known soft
  spots flagged in-text: the objective-index/stage-number relationship is a **convention, not a rule**
  (corrected here vs. looser shorthand); a few param-less functions (`GetIsCreatureType`, `GetSitting`)
  return coded values whose exact codes are not enumerated here.
- This file is the place to record any future correction.

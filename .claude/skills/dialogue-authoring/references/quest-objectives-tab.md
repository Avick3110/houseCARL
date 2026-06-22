# Quest stages & objectives reference

The quest layer dialogue gates on. Dialogue conditions read the quest **stage** (`GetStage`/`GetStageDone`);
the **journal** the player sees is driven by **objectives** and stage **log entries**. These are three
distinct things people conflate — stage number, objective index, and log entry — and conflating them is how
a journal goes wrong while the dialogue is fine.

Field names are Mutagen spellings (by construction — see `_CORPUS_STATUS.md`); the display/script behavior is
from the Creation Kit wiki + practice. Confirm exact paths against `mutagen-reference`.

## The three things, kept separate

| Thing | Mutagen | What it is |
|---|---|---|
| **Stage** | `Quest.Stages[]` → `QuestStage` | a numbered progress point. `Index` (ushort), `Flags`, and `LogEntries`. Advancing the quest = setting a stage. |
| **Log entry** | `QuestStage.LogEntries[]` → `QuestLogEntry` | the **journal text** shown for a stage (`Entry`), optionally `Conditions`-gated, plus the stage's script bytes. A stage can have zero log entries (a silent/controller stage). |
| **Objective** | `Quest.Objectives[]` → `QuestObjective` | a **tracked goal**: `Index` (ushort), `DisplayText`, `Flags`, and `Targets`. What shows under the quest with a map marker. |

A **stage** moves the quest forward and (via its log entry) can write a journal line. An **objective** is the
"current goal" with a quest-marker. They are **independent numbering systems** — see the convention note.

## Stages (`QuestStage`)

- **`Index`** — the stage number (the value `GetStage` returns / `SetStage` sets).
- **`Flags`** (`QuestStage.Flag`): `StartUpStage` (the stage the quest enters when it starts),
  `ShutDownStage` (running it stops/completes the quest), `KeepInstanceDataFromHereOn`.
- **`LogEntries`** — the journal text candidates for the stage. Each `QuestLogEntry` has `Entry` (the text),
  optional `Conditions` (so a stage can show different journal text by condition), a `Flags`
  (`CompleteQuest`/etc.), `NextQuest`, and the stage **script fragment** bytes (`SCHR`/`SCTX`) — the Papyrus
  that runs **when the stage is set**.

The stage fragment is where most authors put `SetObjectiveDisplayed`/`SetObjectiveCompleted` calls and other
beats — so "what happens at stage N" usually lives in stage N's fragment, not in a condition. (Fragment
indices are not obviously tied to stage numbers; the CK wiki recommends comments for clarity.)

## Objectives (`QuestObjective`)

- **`Index`** — the objective number. This is the index `SetObjectiveDisplayed(index)` /
  `SetObjectiveCompleted(index)` take; **they only accept indices defined on the Objectives tab.**
- **`DisplayText`** — the goal text shown in the journal/HUD.
- **`Flags`** (`QuestObjective.Flag`): `OrWithPrevious` — groups this objective's display with the previous
  one (an "A **or** B" goal).
- **`Targets`** (`QuestObjectiveTarget`): where the quest marker points. Each target is an **`AliasID`** (an
  alias of *this* quest), with optional `Conditions` and `Flags` (`Quest.TargetFlag`). The marker resolves at
  runtime to whatever reference fills that alias — so a target whose alias never fills shows **no marker**.

## The index convention — a practice, not a rule

A widespread habit is to make an objective's index **match the stage number** that displays it (objective 10
shown at stage 10), because it reads cleanly. **It is not enforced** — objective indices and stage numbers
are independent numeric spaces, and `SetObjectiveDisplayed`/`SetObjectiveCompleted` reference the **objective
index**, wherever it's called from (UESP CK wiki). Don't assume "stage N ⇒ objective N exists"; read the
Objectives tab for the real indices. (This corrects the looser "objective-index = stage-number" shorthand —
treat aligned numbering as a convention authors *choose*, and verify it per quest.)

## How they drive the journal (the part that bites)

- `SetStage(n)` runs stage n's fragment and shows its (condition-passing) log entry. Advancing the quest is
  stage work.
- `SetObjectiveDisplayed(index, true)` shows an objective + its marker; `SetObjectiveCompleted(index, true)`
  ticks it done; `SetObjectiveDisplayed(index, false)` hides it.
- These are usually called **from stage fragments** — so the journal you see is the *combination* of which
  stage set what objective state. A common bug: setting a stage but never displaying/completing the matching
  objective (no goal shown), or completing the quest stage but leaving an objective still "displayed"
  (lingering goal).

## Relevance to dialogue

Dialogue lines gate on **stage** (`GetStage`/`GetStageDone`), almost never on objectives — objectives are a
display concern. So when "a line won't fire," check the **stage** condition against the quest's real stage
indices here; when "the journal looks wrong," check **objectives** and **log entries**. They fail
independently, and houseCARL can read all of them back (`housecarl_read_record` on the QUST,
`housecarl_validate_dialogue` on the quest for the dialogue side). Remember `Stop()` resets the stage to 0 —
never gate post-completion dialogue on a stopping quest's stage (see the flow-model reference).

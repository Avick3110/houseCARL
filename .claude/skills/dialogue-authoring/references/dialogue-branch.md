# Dialogue Branch (DLBR) reference

A **Dialogue Branch** is a conversation entry point — the record that makes a `Custom`-subtype topic
*reachable*. The flow-model reference introduces it; this one is the field-and-flag detail, because the three
branch flags change in-game behavior in ways a record-only glance won't reveal, and one of them
(`Exclusive`) can silently lock an NPC out of all dialogue.

Field names are Mutagen spellings (by construction from the record model — see `_CORPUS_STATUS.md`); flag
**semantics** are from the Creation Kit wiki. Confirm exact paths against `mutagen-reference`.

## Fields (Mutagen `DialogBranch`)

| Field | Type | Meaning |
|---|---|---|
| `Quest` | `FormLink<Quest>` | the quest that owns the branch (its topics' `Conditions` typically gate on this quest's stage). |
| `StartingTopic` | `FormLink<DialogTopic>?` | **the branch's entry point** — the first DIAL the game shows when the branch is entered. A branch with no `StartingTopic` is inert. |
| `Category` | `DialogBranch.CategoryType` enum | `Player` or `Command` (below). |
| `Flags` | `DialogBranch.Flag` enum | `TopLevel`, `Blocking`, `Exclusive` (below) — combinable. |

The reverse link `DialogTopic.Branch` (BNAM) points a topic back at its DLBR; it is **often unset** — generic
topics have no branch, and a `Custom` topic is reached via the branch's `StartingTopic`, not via its own
`Branch` field. Don't read a missing `DialogTopic.Branch` as a defect.

## Category — `Player` vs `Command`

- **`Player`** — a player-initiated conversation branch: the topics are choices the player picks from a menu.
  This is what you author for "add a dialogue option to an NPC."
- **`Command`** — a command/forced branch (used by the game's command and AI-driven dialogue), not a
  player-choice menu. You rarely author these by hand.

## Flags — the behavior that isn't in the bytes

(Creation Kit wiki, *Dialogue Branch*.) The flags decide how the branch's topics surface against the NPC's
other dialogue.

- **`TopLevel`** — the branch's topics appear as **separate options in the dialogue menu** when the player
  starts a conversation. Top-level branches **begin with a player line** (a choice), not an NPC line, so many
  branches from many sources sit **side by side** without any one of them pushing the conversation. This is
  the usual flag for "a new player-choice topic on an NPC."
- **`Blocking`** — **overrides the NPC's normal topic list.** When the NPC has a *valid* info in a blocking
  branch's `StartingTopic`, the NPC uses it as the **greeting**, and if that info has a choice list, those
  choices replace the normal topic list. Use it to force the player through a specific exchange (e.g. a quest
  beat that must happen before normal chatter resumes).
- **`Exclusive`** — like `Blocking`, but it **takes over the topic list once the branch is "entered"** (the
  NPC speaks a line from any of the branch's topics). The NPC stays "in" the exclusive branch — acting as if
  it were a valid blocking branch — **until it speaks a line from a different, non-exclusive branch.** So if
  you exit and re-open dialogue, the greeting is the exclusive branch's `StartingTopic` again.
  - ⚠️ **The deadlock:** if the exclusive branch has **no topic the NPC can currently speak** (e.g. all its
    infos' conditions fail), the NPC is stuck "in" the branch with nothing valid to say — which can **block
    all of that NPC's dialogue.** An exclusive branch must always have an exit: a valid info under the current
    conditions, or a line from another branch that releases it.

## Reachability — why a `Custom` topic needs a branch

A `Custom`-subtype topic is **not** matched by the generic subtype system (Hello/Goodbye/… are matched
automatically; `Custom` is not). It becomes reachable only by being a branch's `StartingTopic` **or** the
`LinkTo` target of another reachable topic. A byte-valid `Custom` topic with neither is **never entered** in
game — the most common "I added a topic and nothing happens" cause. When you author a new player-choice menu,
author the DLBR in the **same** `housecarl_bulk_create` call (declared *after* the topic) with
`StartingTopic` set to the topic's `@editorid`, `Category = Player`, and `Flags = TopLevel` for an ordinary
side-by-side option. (See the `SKILL.md` reachability note.)

## Quick decode

Reading a branch back: `Category` tells you player-menu vs command; the `Flags` tell you whether it sits
beside other options (`TopLevel`), hijacks the greeting/topic list (`Blocking`), or latches the NPC into
itself until released (`Exclusive`); `StartingTopic` is where it begins. A `Blocking`/`Exclusive` branch
whose starting topic has no currently-valid info is the thing to flag — it's how an NPC goes silent or
gets stuck.

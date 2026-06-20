# Skyrim SEQ file (start-game-enabled quests)

`Data\SEQ\<plugin>.seq` is what makes a plugin's **Start-Game-Enabled (SGE) quests** actually start.
Ticking the "Start Game Enabled" flag on a quest does **nothing on its own** — without the `.seq` the
quest, and any dialogue or world change gated on it, silently never runs. This is the exact silent-failure
class houseCARL refuses (Q3). The Creation Kit writes the `.seq` on save; xEdit's "Create SEQ file" does
the same; `housecarl_write_seq` is the data-layer equivalent.

You do not hand-author this file — `housecarl_write_seq` computes and writes it from the plugin's quests.
This reference exists so you can explain *why* a quest needs it, interpret an existing `.seq`, and know
when the tool will correctly write nothing.

## Format (empirically pinned over 145 real `.seq` files in a live load order)

A flat array of 4-byte **little-endian** FormIDs — **no header, no footer** — one entry per SGE quest.

Each FormID is the plugin-local **master-INDEX on-disk form**:
- **high byte** = the record's slot in the plugin's master list. An own / new record sits at the slot
  *after* the last master → its high byte = the plugin's master COUNT. An override sits at the overridden
  master's index.
- **low 3 bytes** = the object id.

It is **never** the runtime `0xFE` (ESL) or load-order address — independently confirmed by houseCARL's
ESL ground-truth work (master-index on disk, never `0xFE`, across 1.55M records).

## Load-order-independent — computable at author time

Because the encoding is the plugin-local master-index form (not a runtime address), the `.seq` is
**load-order-independent**: it is correct the moment it's written and travels with the mod, no runtime
FormID resolution needed. (Voice file paths share this property — see `voice-file-naming.md`.)

The 23 / 145 real files whose high byte ≠ the plugin's current master count are **stale**, not a different
encoding — a master was added or removed, or an ESL compaction / merge renumbered slots, *after* the
`.seq` was generated. houseCARL avoids this staleness **by construction**: it writes the plugin and its
`.seq` together, so the slot math always matches the plugin it ships with.

## Inclusion rule

A quest is in the `.seq` **iff** its `StartGameEnabled` flag is set **and** it is not deleted. A plugin
with zero SGE quests needs no `.seq` at all — `housecarl_write_seq` reports that and writes nothing (a
clean no-op, never a silent empty file).

## When dialogue needs it

If your dialogue is gated on a quest's stage (the common case — see `dialogue-flow-model.md`), and that
quest is meant to be running from game start, the quest must be Start-Game-Enabled **and** have its entry
in the `.seq`. Set the flag (a quest-record field write), then run `housecarl_write_seq` against the
plugin. The `.seq` makes the quest START; it does not verify the quest or its dialogue is otherwise
correct — that is `housecarl_validate_dialogue`'s job.

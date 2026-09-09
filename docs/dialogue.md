# Dialogue: what decides which line plays, and where a line lands

A dialogue line that is byte-valid still plays nothing if the order around it is wrong. Two of those rules were
measured in game rather than reasoned about, and a third is bookkeeping `housecarl_create` fills for you — with
one default that is the wrong one and one flag it never sets. This page carries all three.

## Quest priority decides the greeting, before intra-topic order

Which topic the game enters for a generic greeting is decided **across quests by quest priority**, and only then
is the order inside a topic consulted. Measured in game on 2026-09-09 on the ARR 2.0 order: a `Hello` INFO in
its own start-game-enabled quest at `Priority = 100`, gated `GetIsID(Hulda) = 1`, with the quest's `.seq` file
present, played on **every** approach and the vanilla greeting did not. The identical INFO with the quest at
`Priority = 0` never played, and vanilla greeted as normal. For scale: vanilla `DialogueWhiterun` is
`Priority = 30` and `DialogueGeneric` is `0`.

The consequence is the trap. A high-priority greeting whose condition passes on every activation replaces the
NPC's **whole** greeting pool for as long as it is active — the vanilla lines are not merged in behind it, they
are simply not reached. Gate the line so it passes only sometimes (a quest stage, a time of day, a one-shot
global), or accept that you have silenced everything else that NPC said.

## A re-listed INFO falls to the bottom unless it carries its PNAM

Any override of an existing INFO is a **re-list** — including an override that only adds a condition — and a
re-listed INFO moves to the bottom of the merged topic order unless the override carries `PreviousDialog`
(PNAM) naming the line immediately above it in the merged order. Most lines, vanilla ones included, carry no
PNAM and there is nothing to preserve — but some do, and on those the PNAM is load-bearing: a PNAM present with
value zero is the "I am first" marker, which pins the line to the **head** of the topic, and it is a normal shape
in shipped plugins rather than an edge case. Overwrite one with a position-minus-one FormID and a vanilla line
that was held ahead of the lines it guards now follows them.

Read the merged order, not the defining plugin's own list, and read the placement it reports before you choose a
PNAM:

```
housecarl_records(formids=["02707A:Skyrim.esm"], project={"form":"info_order"})
```

A line the order marks *pinned first by its own PNAM marker* carries that present-zero PNAM: leave it alone.
The record read cannot answer this for you — an absent PNAM and a present-zero one both render
`PreviousDialog = (null link)` under `project={"form":"everything"}`, and only `info_order`'s placement tells
them apart (#697).

Take the line at the target's position minus one and write that FormID into the override's `PreviousDialog`.
Adding a **new** line re-lists nothing and needs no PNAM for the lines around it.

Two cases the recipe does not cover. If the target is already at position 1 there is no line above it to name —
and writing no PNAM does not leave it there. Absent is the tail arm: the re-listed line goes to the **bottom**,
and every line that was beneath it now answers first. The only shape that holds a line at the head is the
present-zero PNAM above, and houseCARL cannot write one — Mutagen's writer emits no subrecord for a null link, so
a `PreviousDialog` you set to null reaches disk absent. Either leave the position-1 line alone, or re-list the
run beneath it in the same call, each of those lines carrying the FormID of the line above it: the merge places
them back in front of the line that fell to the bottom and the original sequence is restored. And because
`info_order` merges every plugin touching the topic, the line at position minus one can be one defined by a plugin
your patch does not master. That does not dangle: the patch lane is handed the whole load order and Mutagen
derives the master list from the records' own FormLinks, so writing that FormID **adds that plugin as a master**
of your patch — a new hard dependency, one more plugin your patch requires and must load after. Decide whether
you want it before you write the link; if you do not, name a line from a plugin you already master and accept the
position it gives you. (A PNAM that truly resolves to nothing — a target no active plugin defines — places the
line at the HEAD, not the bottom.)

## The bookkeeping create fills, and the one fill that is unreachable

`housecarl_create` fills the Creation Kit's bookkeeping on dialogue records and reports each fill, so nothing
here is silent. One default is still the wrong value: a `DialogBranch`'s `Flags` default to `0`, which is not
`TopLevel`, and a player branch that is not top level **never reaches the player's menu**. Pass
`Flags = TopLevel` explicitly on any `Category = Player` branch (#693).

A second value in that bookkeeping is an authoring choice rather than a default: the `Goodbye` flag, which ends
the conversation on the line that carries it, lives inside the INFO's own `Flags` struct. The create path
materialises that struct to all-zero, and all-zero is not `Goodbye` — a line meant to close the conversation
needs `Flags.Flags = Goodbye` set explicitly, and nothing fills it for you.

Two things cannot be measured on a patch that is not yet enabled:

- The dialogue findings family on `housecarl_check` resolves against the active load order, so a fresh,
  unenabled plugin cannot be dialogue-checked at all (#615).
- `info_order` has no off-order lane and refuses `source=` by design, because the merge across every plugin
  touching the topic *is* the answer (#694).

So the merged order **after** your patch is only measurable once the patch is enabled. Predict it before, then
enable and re-read `info_order` to confirm — do not report the prediction as the measurement.

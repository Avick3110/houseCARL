---
updated: 2026-09-23
covers: [src/housecarl-core/ScriptPropertyCheck.cs, src/housecarl-core/EditorIdNearMiss.cs]
---
# The scripts and dialogue families, and the EditorID near-miss hint

## What it is
The contracts of the scripts family's sweep, the dialogue family's seed words and the records scan's EditorID
near-miss hint, cited from `ScriptPropertyCheck`, `EditorIdNearMiss` and `CheckOutcome` under ADR 0001. The
axes, the budget and the outcome every family shares are in [`check-families.md`](check-families.md).

## Contracts

### The script-property sweep's boundary

What counts as a finding is kept high-signal, so that a clean result is trustworthy:

- **UNBOUND OBJECT property** — an `Auto` property of a form/object type declared in the chain but absent from the
  VMAD. Unbound means `None` at runtime: the silent-no-op footgun. HIGH.
- **UNBOUND SCALAR with no initializer** — defaults to 0 / 0.0 / false / "". MEDIUM. A scalar that DOES carry a
  baked initializer is not flagged: it has the author's intended default, so leaving it unbound is correct.
- **BOUND-BUT-NULL object property** — in the VMAD with a null Object link AND not bound to a quest alias instead
  (`Alias < 0`, because an alias-bound property has a null Object by design). Advisory.

And the degraded modes, every one named rather than silent: `Auto` properties only, because full properties with
custom Get/Set handlers are code-driven; a script whose OWN `.pex` is unreadable is reported UNVERIFIABLE, and a
missing ANCESTOR truncates the chain with a named note while the properties that could be read are still checked; a
BSA that failed to read this build is surfaced, so a "not found" that may merely be unscanned is never an
authoritative absence. An unbound object property is a flag to VERIFY, not a proven defect: it is sometimes filled
by script at runtime.

#### Unverifiable attachments ride through every filter

A script whose `.pex` could not be read might be the very one declaring the property being filtered for, so dropping
its note under a filter would turn "could not check" into a clean answer. They are outside `limit=` too. They are
therefore **collapsed** instead: a repeat of a note already listed for the same script class is counted in
`UnverifiableCollapsed` and in the total, and listed once — one unreadable class hits every record that attaches it,
and an uncapped wall of one sentence would push the findings the caller asked for past `max_chars`. A NAMELESS
attachment is exempt from the collapse, because it names no class and the record is the only identity the defect
has.

### The dialogue family's four seed words

Fixed here and nowhere else, so each has exactly one meaning wherever the response says it:

- **named** — how many seeds the caller wrote in `seeds=`.
- **reached** — how many of those the seed budget let this call actually try.
- **validated** — how many reached seeds produced a validation report.
- **unreachable** — how many reached seeds produced a named refusal instead. These are the `[X]` rows.

`named ≥ reached`, and the difference is the seed budget's cut — the one subtraction, taken once on
`DialogueOutcome`. `validated` and `unreachable` are each counted off the reached seeds independently rather than
asserted to sum to it.

### The EditorID near-miss hint

The winner lane of a records scan filters on the LOAD-ORDER WINNER's body, so a record whose winner RENAMES it is
invisible to `editorid = <the old name>`: the name the caller typed is real, it is simply carried by a losing copy.
That reads as a clean "0 matches", which is the one answer the scan must not leave standing unexplained.

**One sentence, and only a real one.** The look runs ONLY where that cause is the only one available — the scan's
own gate holds it to a zero-row, `types=`-bounded winner-lane scan whose `where=` is nothing but the exact
`editorid =` term. It reads the EDID header and nothing else, and it stops at the FIRST losing copy carrying the
name. No candidate, no sentence: the plain zero-row result stands as it did.

The walk is taken one plugin at a time, in load order, so the base game's own copy ends it early and a plugin that
indexed but will not open NOW skips rather than ending the stream — the hint must not switch itself off for a whole
order because one file moved. Out-of-memory and cancellation still leave by the same door the scan lane sends them
out of.

## Pinned by
- *The EditorID near-miss hint*: `WhereNearMissTests`, whose arms cover the rename sentence, the winner that dropped its EditorID, and
  each gate that must produce no sentence at all: a `contains` term, a second predicate, a `formids=` set,
  `conflicts_only`, a `references=` filter, an aggregate, and an explicit plugin scope.

## Where
- `src/housecarl-core/ScriptPropertyCheck.cs` — the scripts family: `ScriptPropertyCheck.Run`,
  `ScriptCheckResult`, `ScriptUnverifiable`.
- `src/housecarl-core/EditorIdNearMiss.cs` — `EditorIdNearMiss.Sentence`, called from the scan lane in
  `src/housecarl-mcp/RecordQuery.cs`.
- `DialogueOutcome` in `src/housecarl-mcp/CheckOutcome.cs` (covered by [`check-families.md`](check-families.md))
  — the four seed words as fields.
- Tools: `housecarl_check` (`findings=["scripts"]`, `findings=["dialogue"]`), `housecarl_records` (the scan).

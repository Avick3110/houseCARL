# Corpus notes — the maintainer lane

Read this only when **changing** the corpus. Answering a lookup never needs it; the procedure for
that is in SKILL.md.

## Provenance

The generated half of `references/` comes from BellCube's
[papyrus-index](https://github.com/BellCubeDev/papyrus-index): one markdown file per script (or per
plugin source, for the aggregates), plus `index.jsonl`, which resolves an unqualified name to a file
and a 1-indexed `line_start`..`line_end` block inside it.

Two files are **hand-authored** and are not produced by any generator:

- `silent-biters.md` — semantic traps a correct signature does not reveal. Never resolved by the
  index. **Preserve it across every regeneration**, and re-check its claims when the corpus moves.
- this file.

## Regenerating

Regenerate from upstream, then apply the four post-passes below in order. All four are part of the
regeneration, not one-off edits: skipping any of them puts the shipped tree back into a state the
body's promises do not hold in.

1. **Copy the hand-authored files back.** `silent-biters.md` and this file are outside the generated
   set and are lost by a clean regeneration.
2. **Drop function-less script files.** A generated file that carries no entry is reachable from
   neither the body nor the index; all it can ever produce is a dead read. 32 such vanilla stubs
   were dropped on 2026-09-08.
3. **Emit a `## Contents` table** at the head of every generated file **over 100 lines** — the
   stricter of the two thresholds the platform guidance and the checklist name, so it clears both.
   The table lists each `##` section with its entry count and its post-insertion line number, and
   says that the lookup path is the index, not the table. **List only headings that head a
   section** — `Properties`, `Events`, `Functions`, `Global Functions`, and the backticked class
   names. Upstream emits some function doc-comment lines as `##` headings, so a table built by
   walking `^## ` lists prose as sections and splits the enclosing section's entry count across
   them; `papyrusutil.md` carried 22 such lines and was corrected on 2026-09-08.
4. **Re-derive the index's line ranges.** Pass 3 inserts lines above every entry block in the files
   it touches; every affected row's `line_start` and `line_end` shift by the size of the inserted
   block. Verify afterwards that every row's `line_start` lands on a `### ` heading naming that
   row's `name` — 7,345 rows on the tree shipped 2026-09-08, all resolving.

## Hand-authoring an entry

For a source upstream does not carry, a hand-written file plus matching `index.jsonl` rows resolves
identically — same `name` / `qualified` / `source` / `file` / `kind` / `line_start` / `line_end`
shape, `requires_plugin` where the source is an SKSE plugin. Keep hand-written rows out of the
generated files: put them in their own file so a regeneration cannot silently overwrite them, and
list that file above.

## Recorded trades

- **The aggregates stay whole.** `papyrusutil.md` and `dylbills.md` each hold many scripts —
  6,366 and 13,354 lines on the tree shipped 2026-09-08. They are not split, because every read off
  them is line-exact through the index, so file size costs nothing at lookup time and splitting
  would churn every affected row's `file` field on each regeneration. The cost is the fallback read
  and human browsing, which the `## Contents` table covers.
- **The index is grepped, never loaded.** `index.jsonl` is about 1.4 MB (7,345 rows, 2026-09-08).
  One grep over it is cheap; a bulk load is not, and nothing in the lookup path does one. Splitting
  the index per source would move the cost into deciding which shard to grep, which is the question
  the lookup is asking.

## Known coverage bound

Base-class events on `Form.psc` are under-covered upstream: there is no vanilla or skse row for
`OnInit`, only script-specific ones. SKILL.md states this as a bound so a miss there is reported as
a corpus hole rather than answered as "absent from Papyrus". Closing it needs either an upstream
release that carries those rows or a hand-authored supplement per the section above; whichever
lands, drop the bound from the body in the same change.

Figures in this file are measurements with a date, kept here rather than in the body, where nothing
maintains them.

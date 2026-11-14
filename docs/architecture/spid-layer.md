---
updated: 2026-11-14
covers: [src/housecarl-core/SpidLines.cs, src/housecarl-core/SpidGrammar.cs, src/housecarl-mcp/SpidLayer.cs, src/housecarl-mcp/SpidWire.cs]
---
# The SPID layer

## What it is

SPID (Spell Perk Item Distributor) hands spells, perks, items, keywords, outfits, factions and packages to NPCs at
runtime from `_DISTR.ini` files in `Data`. `housecarl_layer` reads those files as SPID 7.3 reads them (7.3.0 and
7.3.3 read their files the same): which files SPID reads and in what order, how each line splits, every form a line
names and where, and what SPID does where a name finds nothing. It does not say which NPCs a line reaches: no string,
level, trait or chance filter is evaluated, and an outfit or leveled list is not followed to what it holds.

## Contracts

- **Files are the discovery door's.** `DistributorDiscovery.Scan` on SPID's row lists `Data`'s top level through the
  asset door, keeps names with `_DISTR` anywhere and the `.ini` extension (both ordinal), resolves each to its MO2
  winner and sorts them ordinally ([`runtime-formats.md`](runtime-formats.md)). A shadowed copy is named and never
  read; a copy only inside an archive is listed, said not applied, and not read.
- **A line splits as SimpleIni and SPID split it** (`SpidLines.Parse`): only the lines before the first named
  `[section]`; a line whose first non-space character is a comment lead (`;`, `#`) is a comment; the value runs to the
  line's end, so a `;` after a value is part of it; `sanitize()` runs first (the first ` - ` to `~` when there is no
  `~`, spaces around `|` and `,` removed, padded hex to `0x`), and a line it changes says SPID writes it back. The key
  takes its kind in SPID's order of tries: `ExclusiveGroup`, then `[Global]Linked[Final][Death]<Type>`, then
  `[Final][Death]<Type>`; `<Type>` is case-sensitive; `Final` on a type the row does not list is dropped with a note;
  `Global` without `Linked` is no key. More `|` sections than the kind takes, an unset value, or an empty filter item
  skips the line. `NONE` (any case) or an empty section is unset only as a whole section; as one item of a list it is a
  name.
- **Every form a line names resolves through the one target resolver on SPID's row**, `RuntimeTargets`, with the call's
  EditorID table: `~` addresses add the written number to the plugin's runtime base, bare `0x` hex is a runtime
  FormID, a token holding `.es` is a plugin term, anything else an EditorID looked up among every type. One call reads
  every form token of every line before any string token, so the table walks every type once and the string lookups
  (NPC, keyword) read what that walk filled.
- **Labels** (DECISIONS 2026-11-13 (13) SPID Q1, Q2): the value is `value`; an NPC or placed NPC named in a plain or
  `+` form filter, or a plain or `+` string filter equal to an NPC's EditorID, is `target`; every other resolved
  filter form, excluded NPCs, linked parents and group members are `filter`. A string filter that is no NPC or keyword
  EditorID names no form; the line says it is matched by name in the running game. A `*` string names no form.
- **What SPID does where a name finds nothing** (research answer 3): a value EditorID no record holds creates a keyword
  on a `Keyword` or `Form` line, creates one and hands nothing out on a `Spell` or `Package` line, and drops the line on
  every other type; a value FormID or plugin that names nothing drops the line. In the form filters a plain or `-`
  token that names nothing, or a record whose type the row's `filterTypes` does not list, is skipped and the line said
  to reach more NPCs (or exclude fewer), unless every token of that list fails, which drops the line; one `+` part
  that names nothing drops it, and `+` parts skip the type check. A filter never creates a keyword. A linked parent or
  group member that names nothing is said, and SPID's outcome for it is not claimed.
- **First wins** (Q4 (a)): each `Outfit`, `SleepOutfit` and `Skin` line, and each `Form` line whose value is an
  outfit, states its place among the lines of its type and kind (regular, death, linked) SPID keeps: files in their
  order, each file's lines by key (case-insensitive) then line, `Form` lines after every typed line. A skipped or
  dropped line takes no place. No conflict between first-wins lines is reported.
- **Selecting lines** (R42, pick 2026-11-14 (4)): `formids=` and `plugins=` keep a line when a form it names passes the
  records meaning (the same predicate SkyPatcher's selector uses), and `plugins=` also keeps a line whose plugin term
  names a selected plugin (DECISIONS 2026-11-13 (12)(iii)); a plugin term names no form, so `formids=` never keeps it.
  A line touches a plugin only through the forms it names, never through the items of an outfit it hands out.
- **What is resolved when.** The overview lists files and line counts and resolves nothing; `filter=` (a file name or
  providing mod) and the selectors resolve every line, since a first-wins place depends on which earlier lines SPID
  drops. Nothing is kept between calls (R15): each call reads the files again; there is no parse cache (DECISIONS
  2026-11-13 (15)).
- **Two formats, one call.** With `distributor` omitted the answer is SkyPatcher's render, then SPID's; SPID's part takes
  what it needs of `max_chars` up to half, SkyPatcher's the rest. SPID is read after SkyPatcher's scans in the same
  replay context, so the two share one EditorID table. SkyPatcher's part carries the build caveats; SPID's alone
  carries them when it is the whole answer.

## Known limitations

- A value whose record is of a type the line does not take (`Spell = <a perk>`) is not reported; SPID throws on it and
  drops the line.
- An EditorID two records share is not reported, and inside one plugin the tie falls to the table's type order, not
  the plugin's record order (research-topup X6). No dev SPID name is shared.
- Where a name lands under po3 Tweaks' absence follows the call's EditorID table; SPID's own keyword lookup reads the
  keyword array, which the table's kept types already cover.
- The keyword dependency re-sort of `Keyword` lines is not modelled; keyword lines have no place.
- A plugin term matches `plugins=` by name; it is not matched by `formids=` naming an NPC its plugin defines.
- An `ExclusiveGroup` member or `Linked` parent that names nothing is said without SPID's outcome.
- MergeMapper remaps are not read.

## Pinned by

- `SpidLinesTests` — each split rule (comment leads, trailing text, the unnamed section, key case, section counts,
  `NONE`, empty items, sanitize, `Final`, every kind), each label, each missing-name outcome by type and position, the
  load-index note, the plugin term, linked parents and group members, and the first-wins places.
- `SpidLayerTests` — the tool on a live order: `plugins=` by EditorID value, the target label, an outfit's item plugin
  not touched, a display-name token touching nothing, the plugin term under `plugins=` and not `formids=`, every line
  kind selected, the outcomes under `filter=`, first-wins places across files, the shadowed copy, an edited file read
  anew, one EditorID walk for both formats; `SpidWireTests` — an archive-only copy said not applied.
- `LayerSelectTests.DistributorOmittedReadsSkyPatcherThenSpid`, `DistributorSpidIsRead`,
  `ADistributorStillToComeIsNotReadYet`.

## Where

`src/housecarl-core/SpidLines.cs` (split, read, places), `src/housecarl-core/SpidGrammar.cs` (the row's lines block),
`src/housecarl-mcp/SpidLayer.cs` (the service read in the replay context), `src/housecarl-mcp/SpidWire.cs` (the render);
the tool front is `SkyPatcherTools.Layer`.

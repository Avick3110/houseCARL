---
updated: 2026-11-15
covers: [src/housecarl-core/BosLines.cs, src/housecarl-core/BosGrammar.cs, src/housecarl-core/DistributorLines.cs, src/housecarl-mcp/BosLayer.cs, src/housecarl-mcp/BosWire.cs, src/housecarl-mcp/DistributorLayer.cs, src/housecarl-mcp/DistributorWire.cs, src/housecarl-mcp/SkyPatcherEditorIdTable.cs]
---
# The BOS layer

## What it is

BOS (Base Object Swapper) swaps the base object of placed references, and sets their transforms, at runtime from
`_SWAP.ini` files in `Data`. `housecarl_layer` reads those files as BOS 3.5.0 reads them (3.4.0 and 3.5.0 read their
files the same; bos-lines research answer 2): which files BOS reads and in what order, how SimpleIni and BOS split each
line, every form a line names and where, what BOS does where a name finds nothing, and each kept line's place in BOS's
last-wins order. It does not say which references a line swaps: no condition, chance, random pick, leveled resolution
or encounter-zone rule is evaluated, and a property is not read beyond whether one is named (spec concern 15, plan
step 30).

## Contracts

- **Files are the discovery door's.** `DistributorDiscovery.Scan` on BOS's row, as for SPID
  ([`spid-layer.md`](spid-layer.md)): `Data`'s top level, `_SWAP` anywhere and the `.ini` extension (both ordinal), the
  MO2 winner of each, one ordinal sort. A shadowed copy is named and never read; a copy only inside an archive is listed,
  said not applied, and not read. A `_SWAP` file below the root (ARR's `SKSE/Plugins/BaseObjectSwapper/…`) is not BOS's.
- **A line splits as SimpleIni and BOS split it** (`BosLines.Parse`): a line whose first non-space character is `;` or
  `#` is a comment; a header is trimmed and its tail after `]` dropped; sections merge in any case and keep their first
  spelling and first place; the key runs to `=` (BOS reads only it, and the line says so); each key is kept once per
  section in any case, so a repeat is said to be read once, at its first line. The section's first spelling picks the
  map, compared exactly: a name holding `|` is conditional (head `Forms`: conditional form swaps; any other head:
  conditional property overrides), its conditions the text between the first and second `|` split on `,`, a leading
  `-` negating; `Transforms` or `Properties` property overrides; `Forms` form swaps; any other name, and none, reference
  swaps. A swap line is `base|swap|properties|chance`, a property line `base|properties|chance`; fields past those are
  said ignored; a line with no `|` indexes a field it lacks (undefined), so its outcome is not claimed.
- **Every form a line names resolves through the one target resolver on BOS's row**, `RuntimeTargets`: exactly one `~`
  is an address added to the plugin's runtime base, unmasked (a load index written in is said, DECISIONS 2026-11-13
  (12)(ii)); two or more `~` is a name; bare `0x` hex is a runtime FormID; anything else an EditorID looked up among
  every type. BOS has no plugin-name term (`targets.pluginTerm` = `notATerm`): a `.esp` text is a name.
- **Labels** (research answer 6): the base is `target` (a placed reference in reference sections, a base object
  elsewhere, either in property sections), each swap or swap-set member `value`, each condition of the section's header
  `filter`, on every line under it. Properties and chance name no form.
- **What BOS does where a name finds nothing, by field** (research answers 3-5): a base, or a single swap, BOS gets no
  FormID for drops the line with BOS's own log text (`BASE formID not found`, `SWAP formID not found`); a swap naming its
  own base with no properties is refused (`BASE formID == SWAP formID`); a set member naming nothing leaves the set and
  the rest stand, and a set keeping none drops the line; in a swap section a base holding `,` that names nothing whole
  is a base set, its swap set keeping one form or at least as many as the bases (a property line's such base drops the
  line, `BASE formID not found`); an address or runtime FormID naming no record is kept (a
  base then applies to no reference, a swap finds no object and an earlier line is tried); a condition naming no form is
  kept as text and matched in the running game, one whose FormID names no record kept as that FormID, and one naming a form that is not a
  Location, Region, Keyword, Cell or WorldSpace said never to match (ConditionalData.cpp:33-70; the row's
  `conditionTypes`), and under `-` to exclude nothing, the line applying as if it were absent (3.4.0 :99-109); a FormID side
  `stoul` cannot read throws out of BOS's loader with no handler, said so, the game's outcome not claimed; a base
  naming a record no reference is looked up by (form swaps look up a reference's base, a bound object, the row's
  `baseTypes`; property overrides also the reference itself, `propertyBaseTypes`; Manager.cpp:227-233, :266-273) said
  to apply to no reference, reference swaps and base-set members not judged; a property
  line naming none of `pos`, `rot`, `scale`, `flags` sets nothing and never applies.
- **Last wins** (research answer 4): every kept line states its place among its base's lines of its map: files in BOS's
  order, each file's sections in first appearance then lines; for a conditional map the line's condition set
  (`file|conditions`) and that set's place among the base's sets, which BOS tries from the last key in byte order back.
  The order line says the map order per reference (references, then conditional forms, then forms; properties after
  the winning swap's own). A dropped or skipped line takes no place.
- **Selecting lines** (R42): `formids=` and `plugins=` keep a line when a form it names passes the records meaning, as
  for SPID and KID; BOS names no plugin term, so the select loop's term branch is empty for it. A `[Forms]` line does
  not touch the plugins whose references it would swap (concern 15). A selector naming a plugin the order does not
  carry answers as for SPID and KID (#1409).
- **What is resolved when.** The overview lists files and line counts and resolves nothing; the selectors, and a
  `filter=` at least one BOS file matches, resolve every line, since a place depends on every file's lines. Nothing is
  kept between calls (R15); there is no parse cache (DECISIONS 2026-11-14 (26)).
- **Five formats, one call** (`DistributorWire.RenderLayers`). With `distributor` omitted (or `[]`) BOS's part follows
  SkyPatcher's, SPID's and KID's, and FLM's follows it ([`flm-layer.md`](flm-layer.md)): one summary line with no selector or `filter=` (pick 2026-11-15 (1) Q1 (b)), else it
  takes what the earlier parts leave and says where it is cut (Q2 (a)). BOS is read after KID in the same replay
  context, so all five share one EditorID table.
- **A name one plugin holds twice** (pick 2026-11-15 (13) Q1 (a)): the table breaks a same-plugin tie between two types
  by the record's place in the plugin's file (`RecordHeaderKeys.Places`, read off record headers once per plugin and key
  in the call; a tie a later walk adds reads the file again), the record the game's map keeps; dev's and ARR's `0DVDiverseDarkElfLanterns_SWAP.ini` swap to the
  Statics, not the MoveableStatics, whatever else the call reads.

## Known limitations

- Conditions, chance, random picks, leveled swaps and the encounter-zone rule are not evaluated; which references a
  line reaches is not said (spec concern 15).
- Properties are not read beyond whether one is named: a lower-case `rotr(…)` that BOS reads as absolute, or a
  property line losing to a swap's own transform, is not said.
- BOS's own conflict log is not reproduced as a report.
- That the engine names a plugin's records front to back, so the first in the file keeps a shared name, is an
  inference (research top-up T2); one in-game look settles it.
- A `stoul` throw is said on its line; the lines after it, which BOS never loads, are still listed as read.
- The `.ini` extension case and USVFS's spelling of a name two mods ship in different case are read as the top-up T4
  settles them (case-sensitive; the lowest-priority mod's spelling), with no dev or ARR file reaching either.
- MergeMapper remaps are not read.

## Pinned by

- `BosLinesTests` — each split rule (comment leads, the section names picking the map exactly, sections merged with
  their first spelling, a repeated line, `=`, no `|`, extra fields, conditional headers and their keys), each label,
  each outcome by field (base and swap misses, an absent plugin, set members, a transform in the swap field, base ==
  swap, a load index, an address naming no record, conditions kept as text or as their FormID, the second `|`, a
  property line naming no property, base sets, an unreadable FormID), and the order (per base and map, a repeated
  header, conditional sets).
- `BosLayerTests` — the tool on a live order: the same-plugin tie by file and by the file's own group order, whatever
  else the call reads; `plugins=` by EditorID value; `formids=` by a section's condition; the shadowed copy; an edited
  file read anew; no EditorID walk added; BOS taking what KID leaves with its cut; an absent plugin selector as for
  SPID and KID; the `lid` refusal naming BOS.
- `BosLayerHeldTests` — BOS's part held whole, byte for byte: the file list, the expanded lines, both selectors, the
  selector zero, its own file and line cuts, no files. `DistributorLayerHeldTests` — BOS's summary line and part after
  the held SPID and KID parts, and the over-cap sentence naming it.
- `RuntimeTargetsTests.BossRowReadsEachTokenAsBosDoes`, `LayerSelectTests.DistributorBosIsRead`,
  `DistributorOmittedReadsSkyPatcherThenSpidThenKidThenBos`, `SkyPatcherTargetResolverTests.ASamePluginTieGoesByFileOrderNotTypeOrder`.

## Where

`src/housecarl-core/BosLines.cs` (split, read, places), `src/housecarl-core/BosGrammar.cs` (the row's lines block),
`src/housecarl-core/DistributorLines.cs` (the shared token resolve), `src/housecarl-mcp/BosLayer.cs` (BOS's split, read
and rows given to the one reader, `DistributorLayer.cs`), `src/housecarl-mcp/BosWire.cs` (BOS's own words and order);
the render and the join are `DistributorWire` (`RenderLayers`), the tool front `SkyPatcherTools.Layer`; the tie is
`SkyPatcherEditorIdTable.Find`.

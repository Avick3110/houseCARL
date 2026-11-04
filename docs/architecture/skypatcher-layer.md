---
updated: 2026-11-04
covers: [src/housecarl-core/SkyPatcherParse.cs, src/housecarl-core/SkyPatcherCatalog.cs, src/housecarl-core/SkyPatcherFieldMap.cs, src/housecarl-core/SkyPatcherDiscovery.cs, src/housecarl-core/SkyPatcherReach.cs, src/housecarl-core/SkyPatcherOverlay.cs, src/housecarl-core/SkyPatcherConflicts.cs, src/housecarl-core/SkyPatcherDraft.cs, src/housecarl-core/SkyPatcherEditorIds.cs, src/housecarl-mcp/SkyPatcherTools.cs, src/housecarl-mcp/SkyPatcherReplay.cs]
---
# The SkyPatcher layer

## What it is

SkyPatcher is a runtime record patcher: it edits Bethesda records from INI files at load, so a
plugin read alone does not say what the game sees. houseCARL reads that layer in four tiers, each
of which only knows its own job — tokenizer, catalog, field map, overlay. The tokenizer is pure
grammar and cannot drift when the hand-modeled catalog changes.

## Contracts

**The catalog is transcribed, never invented.** It is the full enumeration of every documented
filter and operation per record type, taken from the bundled `skypatcher-authoring` reference at the
SkyPatcher version it names (`version`, 8.0.1; the layer header and the overlay's source line print
it), and a key resolving to no entry is reported as Unknown rather than assumed. Each filter row
carries its **role** on a line (direct, finder, narrower, gate, all; an `Excluded`/`Exclude`
connective always narrows), and each record row the patcher's differences from the filters
article: a filterless line that patches nothing, keys it does not read, keys that also narrow the
direct list, which finders can find the player and which narrowers never remove him, and other
records whose keywords its keyword filters read. The catalog also holds the patcher run order. Its record dimension (name,
sig, subfolder, primaryFilter) is cross-checked in CI against the router table in that skill's
`SKILL.md` — `SkyPatcherCatalogTests.TheCatalogMatchesTheSkillRouterTable` requires the row count to equal the
loaded record count and matches recordType and signature per subfolder. That is the one contract
here the code cannot express at all: it couples `src/housecarl-core/` to a file in
`.claude/skills/`, so adding a record type on one side without the other fails
that test.

**How far to trust the field map.** It is hand-modeled, but `SkyPatcherFieldMapGuardTests` walks every
`OpMap.Path` with the real write engine (`WriteEngine.ResolveProperty` over the actual Mutagen
types) and parses every `ValueMap` target against the real leaf enum, and rejects a stateful numeric
op on a non-numeric leaf, a flags op on a non-enum, and a dict op on a non-dict. A typo'd path or
enum member cannot survive CI; only a semantically-wrong-but-existing field can.

### The grammar

```
line         := (';' | '#') comment | blank | patch [ ';' comment ] | '[' label ']'   (houseCARL reads '#' as a comment: no key starts with '#')
patch        := segment ( ':' segment )*      (a key's second copy on one line is noted and dropped, as SkyPatcher ignores it)
segment      := key '=' value
value        := item ( ',' item )*
item         := address | name-literal | scalar | compound
address      := plugin '|' formid | editorid
name-literal := '~' text '~'                  (rename ops: fullName=~New Name~)
compound     := part ( '~' part )*            (mgefsToAdd=Form~Mag~Dur~Area)
```

Nothing fails silently, but the two malformed shapes are not treated alike. A segment with no `=`,
or with an empty key, is captured as a loud `Note` **and still surfaced** — it reaches the caller as
a segment. An empty `:`-segment or `,`-item (a stray or doubled delimiter) is noted and then
**skipped**, contributing nothing. So `filterByNpcs=X::level=5` yields a `Note` plus **two**
segments, not three, and the segment count of a noted line cannot be used to count delimiters.

Key names match without regard to case, as SkyPatcher does: `filterByNPCs` is `filterByNpcs`.
Case is folded once, when a key is classified against the catalog; every later lookup (field
map, built-in filters, conflicts, no-ops, warning dedupe) uses the catalog's name. A response
names a key exactly as the line spells it, except the two fixed `filterByModNames` and
`modNamesLastOverridden` warnings, which use the catalog's name.

A line starting with `#` can never patch anything, in game or here, because no legal key starts
with `#`; houseCARL reads it as a comment rather than skipping it as an unknown key. This is
houseCARL's reading, not a rule SkyPatcher documents.

### Addressing: `Plugin.esp|FormID`

The tokenizer resolves only the unambiguous `Plugin.esp|FormID` form. A bare identifier is left
un-addressed, because an EditorID (`filterByWeapons=IronSword`) and an enum scalar
(`armorType=heavy`) are lexically identical — telling them apart needs the catalog's knowledge of
whether the key is form-valued, so EditorID resolution belongs to the overlay. The catalog
classifies keys; it does not resolve values.

The FormID side is hex with trimmable leading zeros. A full load-indexed ESL FormID (`FExxxYYY`)
keeps only its 12-bit local id; anything else keeps the low 24 bits. That normalization lives in
`FormIdRange`, shared with the SKSE config audit, because getting it wrong inverts every verdict.

### The per-type subfolder rule and the filename gate

INIs live under `Data/SKSE/Plugins/SkyPatcher/<type>/`. The first path component under the root is
the record type; deeper nesting is organisation only. An INI sitting directly in the root is listed
with a note and applied to nothing — the DLL reads type subfolders only. An unrecognized subfolder
name is listed but not interpreted, never guessed.

Within a type folder, files apply breadth-first: the folder's own files, then each depth of
subfolders, A to Z case-insensitive by path component within a level (8.0.1's General Information
article; ARR's 7.0.x log shows the same apply order). Folders are listed in the patcher run order
the catalog holds. One rule set in `SkyPatcherDiscovery` decides this for the scan and the draft
fold alike.

A file is read when `.ini` appears anywhere in its name (`x.ini.bak` too), except a name ending in
an upper-case `.INI`. When the name before the first `.ini` ends in a plugin extension the file is
gated on that plugin and loads only while it is active; a name with more text after a plugin name
(`SomeMod.esp-extra.ini`) is never read. A file not read keeps its parsed content — an inactive
patch is still inspectable — and carries `NotApplied` naming why. `SkyPatcher.ini`'s `[Patcher]`
switches turn a whole type folder on or off: a missing key is **off**, except Light, which runs
without one; no loose `SkyPatcher.ini` leaves every patcher but Light off, said once; an
unreadable one is named, and every folder is marked as unknown (its lines replayed as if its
patcher runs, and said so). A switch value reads as its leading integer, 0 when there is none.
Each is a flag, never a silent drop.

Two further discovery contracts: the layer is **loose-only** (an INI resolving only into a BSA is
`NotApplied` for that reason), and it is a **union, not a filename winner** — every distinct loose
INI applies, and the VFS winner rule collapses only two mods shipping the *identical* relative
path, which is surfaced per file as `ShadowedProviders`.

### How the overlay replays onto a record

`SkyPatcherOverlay.Apply` takes the ordered, game-visible line union for a type folder and replays
it onto a deep **mutable copy** of the record's load-order winner:

1. Classify every key of the line against the type's catalog. An unknown key poisons the **whole
   line** — if it was an unrecognized filter, evaluating the rest would mis-scope the line, worst
   case applying its ops to every record of the type — so the line skips loud as unresolved.
2. Evaluate the line's reach against this record. `SkyPatcherLineShape` sorts the line's filters
   by role, and every consumer that asks which records a line can touch reads that shape: the
   replay, the conflict and ITM report, the no-op scan and the line index. In SkyPatcher's order of
   checks: a failed gate stops the line; a record the direct list names is patched, past every
   narrower but the patcher's own direct-list narrowers; otherwise some finder group must find it
   (one group per filter family, its AND and Or keys together), or, with no finder and no direct
   list, every record passes unless the patcher patches nothing then; then every narrower must
   leave it in. The player is found only by the finders the catalog names, by the direct list, and
   never by a filterless line, and some Excluded keys never remove him. The answer is three-valued:
   a filter the overlay cannot evaluate (explicitly unmapped-with-reason, or neither built-in nor
   mapped) leaves the line UNRESOLVED only when the other filters leave it open, and the line then
   skips loud. One value of one filter is evaluated as before: built-in families (primary,
   `noFilter…`, `hasPlugins`, keyword / EditorID / name contains, mod-name and override-aware
   tokens, attached mgefs, alternate textures) need no per-record path, the rest come from the
   field map's `FilterSpec`s. A keyword filter also reads the keywords of the records the catalog
   names (an NPC's race, a spell's effects). `filterByModNames` compares names case-exactly.
3. Apply each op, in segment order, onto the running copy. Because the copy carries state,
   `…Mult`/`…ToAdd` accumulate exactly as the DLL does and a later `set` of the same field
   overwrites an earlier one — apply-order replay, not last-write-wins bookkeeping.

Mutation rides `WriteEngine.ApplyVerb` and reads ride `ReadEngine.ReadLeaf`, so field addressing
cannot drift from the read/write surface.

**Tiered honesty.** CLEAN and COLLECTION ops resolve to a post-state value. HARD ops come back as
`SkyPatcherDirective`s — the directive text plus why it has no static resolution — never a silently
wrong value. Unknown keys, unmapped ops, unevaluable filters and value failures are all named
warnings. Every CLEAN and COLLECTION op in the catalog has exactly one field-map entry, either a
mapping or an explicit `Unmapped` with a reason; HARD ops have none, and CI rejects one that
acquires a mapping.

A warning rides an overlay read when its line bears on a record read: the line's verdict is Match
or Unresolved for some record, or its NoMatch rests on the warned value. Inside a finder a token
that resolves to nothing drops out and the rest still find; a bare (AND) finder whose tokens all
miss finds nothing, and those warnings decide the line when it would otherwise reach the record
(the line's verdict is recomputed with that finder passing). A narrower's missing token removes
nothing.
An unknown-key line always rides, and so does every warning from a draft INI, which no layer listing holds. Any other warning is a note, and the read prints one line per INI
naming it, `note(s) on lines that do not reach these records — housecarl_skypatcher_layer
filter=<INI> lists them`; in json the same lines join the `skypatcher_warnings` member. Each line's
lint lives under that line in `housecarl_skypatcher_layer filter=`: unknown keys and the filter form
tokens that resolve to nothing, resolved once per line, in the words a read uses.

**On a records scan.** `housecarl_records` with the post-state overlay over a scan replays only the rows its
`limit=` window keeps. The scan selects off the plugin records, so `types=`, `plugins=`, `where=` and `references=`
judge each record before the layer replays; when `where=` or `references=` is present the scan's note says so, naming
them (in `notes` on json and dense, a header line on text). The
window's keys go through `OverlayPostBatch`, the list lane's batch, which opens one replay context for the call; an
empty window opens none. A `summary` window is bounded by the render bound as the fields form is, and keeps the scan's
`matches=` un-merge. `aggregate` and `counts_only` are not replayed: they count the plugin records, and the scan's
note says that. A walk or `to_file=` with the post state on a scan is refused, because each covers every match or
every record reached (a walk would replay its whole reached set, not the window), as is `fields_source='winner'`, a
second display pole. The pre state on a scan is the winner scan, said in its source statement, so it serves a walk
and `to_file=`. A comparison form over a scan takes neither state: the post refusal names the replay, the pre one
says pre is the winner and to pass `"winner"`.

### Reports and drafts

`SkyPatcherConflicts` is report-only: it names same-field, same-target SET collisions across files
(the later-sorted file wins), plus the ITM classes — intra-file dead writes, cross-INI duplicates,
and no-op writes. Which value *should* win stays with the agent. Add/remove/mult/collection ops
accumulate by design and are not conflicts.

The replay and scan notes after the report sections are one list. It takes a quarter of what the
caveat block leaves of `max_chars`, never more than the header and owed notices leave, charged
before the body so the sections cannot spend it. A list that fits is shown whole; a cut one names
only the notes that fit, none if the first does not, and is counted with the caveat block's marker.

`SkyPatcherDraft` folds an INI that is not yet placed in a mod into the live scan, so a record can
be read as the game would see it once placed. A draft that is already one of the layer's live
files, or whose filename would collide at the same relative path, is refused: which copy wins would
then depend on mod order, not on the draft.

### EditorIDs and po3 Tweaks

Without powerofthree's Tweaks the game keeps EditorIDs only for keywords, globals, races, quests,
voice types, head parts, cells, worldspaces, idles and music types. Once per replay context
`SkyPatcherEditorIds.Read` checks the pinned asset view: `SKSE\Plugins\po3_Tweaks.dll` must be a
loose winner, and a winning `po3_Tweaks.ini` must not set `[Fixes] bLoadEditorIDs` (old name `Load
EditorIDs`) off. When it is missing, an EditorID value of any other type resolves to nothing, the
record's own EditorID matches no direct-list name or `filterByEditorIdContains`, and the warning
names po3 Tweaks. The ten kept types answer as before. A `po3_Tweaks.ini` that cannot be read is
replayed as the DLL's default (EditorIDs load), and the read and the layer say the state is unknown.

### Known limitations

- Cross-patcher effects: the replay is per record type and reads donors and keyword sources at
  their plugin winner, so a filter that would see another record's SkyPatcher change (a COBJ's
  created item patched by the Weapon patcher, an NPC's race patched by the Race patcher) answers
  from the pre-SkyPatcher record.
- A FormID that names a record absent from the order still counts as resolved; only EditorIDs and
  unparseable addresses resolve to nothing.
- An unknown key still skips its line loud; the game drops the key silently and the line can widen
  to every record of its type. houseCARL keeps refusing to guess.
- `noFilterLL`'s value and its 120-entry limit are not modelled; a filter value that is not a number
  still leaves the line UNRESOLVED (8.0.1 skips only that key).

### Delimiters

Line splitting on `:`, item splitting on `,` and compound splitting on `~` are naive, matching the
documented "`:` separates every segment". A rename literal containing `:` or `,`
(`fullName=~Amulet of Mara, Blessed~`), or a plugin filename containing `~`, over-splits. The real
DLL's delimiter precedence must be verified against the running binary before any of this is
hardened.

## Pinned by

- *Contracts*, the catalog paragraph: `SkyPatcherCatalogTests` — an unknown key
  is Unknown, never assumed, and `TheCatalogMatchesTheSkillRouterTable` holds the record dimension against the skill's router table.
- *Contracts*, the field-map paragraph: `SkyPatcherFieldMapGuardTests` — every path walked
  and every value target parsed against the real Mutagen types, with self-test arms catching a bad path and a bad
  value target. Its stateful-shape arm checks that the catalog's op shape and the map's semantic agree on being
  stateful; that is not the paragraph's leaf-type check, and the stateful-numeric-on-a-non-numeric-leaf, flags-on-a-non-enum
  and dict-on-a-non-dict checks have no self-test arm.
- *The grammar*: `SkyPatcherParseTests` — a segment with no `=` or an empty key is noted
  and still surfaced, and a doubled `,` is noted and skipped.
- *Addressing*: `SkyPatcherParseTests` — a bare EditorID is left un-addressed, and the FormID side trims leading
  zeros; `SkyPatcherOverlayWeaponReplayTests.AFullEslFormIdWithANonZeroSlotKeepsOnlyTheLocalId` (`FE001800`) — a
  full ESL FormID keeps its 12-bit local id.
- *The per-type subfolder rule and the filename gate*: `SkyPatcherDiscoveryTests` —
  the breadth-first order (`FilesAreReadBreadthFirst`), the name rules (`TheFileNameRulesReadIniAnywhereAndRefuseTheRest`),
  `<Plugin>.esp.ini` gated and still inspectable, the `[Patcher]` toggle, a missing switch off and Light on, no
  `SkyPatcher.ini`, an unreadable one, patcher order, the draft fold on the same rules
  (`ADraftFoldsInWithThePlacedFileRules`), a root-level INI and an undocumented subfolder each noted, loose-only,
  and the union with `ShadowedProviders`.
- *How the overlay replays onto a record*, the reach: `SkyPatcherReachTests` — finders and the direct list add up,
  only a gate stops the direct list, case-exact mod names, a missing finder token drops out, the Leveled List, Cell,
  FormList and Race Hook differences, race and effect keywords, the player rule, and the keys the earlier catalog lacked
  classifying; `SkyPatcherConflictsTests` — a direct list beside an Excluded key kills an earlier write, a finder's
  reach collides with a broad line.
- *EditorIDs and po3 Tweaks*: `SkyPatcherEditorIdTests` — without Tweaks an armor EditorID finds nothing and the
  warning says why while a keyword EditorID resolves; with Tweaks both apply; the header and source line name 8.0.1.
- *How the overlay replays onto a record*: `SkyPatcherOverlayWeaponReplayTests` — the stateful apply-order replay,
  an unknown key poisoning the whole line, an unmapped filter skipping the line loud, and a HARD op coming back as a
  directive; `SkyPatcherOverlayFilterTests`, `SkyPatcherOverlayEntryOpsTests` and `SkyPatcherOverlayOpClosureTests`
  — the filter kinds, the entry and list ops, and the op closures.
- *Tiered honesty*, which warnings ride a read: `SkyPatcherWarningRelevanceTests` — a record no line reaches gets the
  pointer and no warning text, a matched Or line and a bare line stopped by a missing keyword ride, an unknown key
  always rides, a draft's line rides whatever its verdict, and `filter=` lists the lint under its line.
- *How the overlay replays onto a record*: `SkyPatcherFieldMapGuardTests.AMappedHardOpIsCaught` — CI rejects a
  HARD op that acquires a mapping.
- *How the overlay replays onto a record*, on a records scan: `RecordsScanPoleTests` — a draft's post state read
  over a scan in text, json and dense; `limit=1` opens one replay context and reads one body; `where=` drops a record
  its winner fails though the draft would pass it, and says so, as `references=` does; a count opens no replay and
  carries the note, as does an empty window; a walk, `to_file=` and `fields_source='winner'` with the post state are
  refused and served with pre; a summary keeps `matches=` and is bounded.
- *Reports and drafts*: `SkyPatcherConflictsTests` — SET collisions with the later
  file winning, accumulating ops not conflicts, and the ITM classes.
- *Reports and drafts*: `RecordsSkyPatcherDraftTests.ADraftThatSetsALeafIsReadInThePostState` — a draft is folded into
  the live scan; `ADraftWhoseFilenameIsAlreadyPlacedIsRefused` and
  `ADraftThatIsAlreadyPlacedIsRefusedRatherThanFoldedInTwice` in the same class — the two refusals.
- *Reports and drafts*, the notes sentence: `SkyPatcherLayerNotesCutTests` — the notes keep their share under a full body,
  a list that fits is whole, and a cut one is counted inside the cap.

## Where

`src/housecarl-core/SkyPatcherParse.cs` is the tokenizer; `SkyPatcherCatalog.cs` the catalog; `SkyPatcherFieldMap.cs`
the field map; `SkyPatcherDiscovery.cs` the discovery and apply order; `SkyPatcherOverlay.cs` the replay
(`SkyPatcherOverlay.Apply`); `SkyPatcherConflicts.cs` the conflict and ITM report; `SkyPatcherDraft.cs` the draft fold.
`src/housecarl-mcp/SkyPatcherReplay.cs` is the service's replay: `OpenSkyPatcherReplay` opens
one call's context (asset capture, catalog, field map, scan, draft fold) and `Replay` runs one record's winner through
it, for both the layer tool and the overlay source. Both open it over an asset build taken in the same `_gate` hold as
the index pin their answer is stamped with (`CapturePinAndAssets`), so a profile switch mid-call cannot replay one
profile's INI layer onto another's winners. `src/housecarl-mcp/SkyPatcherTools.cs` is the tool front. Tool: `housecarl_skypatcher_layer`; the overlay and the
draft are also read through `housecarl_records`'s SkyPatcher overlay source.
The result records are in `src/housecarl-mcp/AssetResults.cs` ([`assets.md`](assets.md)).

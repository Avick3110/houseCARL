---
updated: 2026-09-23
covers: [src/housecarl-core/SkyPatcherParse.cs, src/housecarl-core/SkyPatcherCatalog.cs, src/housecarl-core/SkyPatcherFieldMap.cs, src/housecarl-core/SkyPatcherDiscovery.cs, src/housecarl-core/SkyPatcherOverlay.cs, src/housecarl-core/SkyPatcherConflicts.cs, src/housecarl-core/SkyPatcherDraft.cs, src/housecarl-mcp/SkyPatcherTools.cs, src/housecarl-mcp/AssetResults.cs]
---
# The SkyPatcher layer

## What it is

SkyPatcher is a runtime record patcher: it edits Bethesda records from INI files at load, so a
plugin read alone does not say what the game sees. houseCARL reads that layer in four tiers, each
of which only knows its own job — tokenizer, catalog, field map, overlay. The tokenizer is pure
grammar and cannot drift when the hand-modeled catalog changes.

## Contracts

**The catalog is transcribed, never invented.** It is the full enumeration of every documented
filter and operation per record type, taken from the bundled `skypatcher-authoring` reference, and
a key resolving to no entry is reported as Unknown rather than assumed. Its record dimension (name,
sig, subfolder, primaryFilter) is cross-checked in CI against the router table in that skill's
`SKILL.md` — `SkyPatcherCatalogProbe.CrossCheckRouterTable` requires the row count to equal the
loaded record count and matches recordType and signature per subfolder. That is the one contract
here the code cannot express at all: it couples `src/housecarl-core/` to a file in
`.claude/skills/`, so adding a record type on one side without the other fails
`skypatcher-catalog-guard`.

**How far to trust the field map.** It is hand-modeled, but `skypatcher-fieldmap-guard` walks every
`OpMap.Path` with the real write engine (`WriteEngine.ResolveProperty` over the actual Mutagen
types) and parses every `ValueMap` target against the real leaf enum, and rejects a stateful numeric
op on a non-numeric leaf, a flags op on a non-enum, and a dict op on a non-dict. A typo'd path or
enum member cannot survive CI; only a semantically-wrong-but-existing field can.

### The grammar

```
line         := ';' comment | blank | patch | '[' label ']'
patch        := segment ( ':' segment )*
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

Within a type folder, files apply `0`→`z` by their folder-relative path (ordinal, case-insensitive).
DECLARED ASSUMPTION: the grammar reference documents filename sort for a flat folder; the DLL's
order across nested subfolders is unverified, and relative-path sort matches flat-folder sort
exactly.

A file named `<Plugin>.esp.ini` loads only when that plugin is in the active load order. A
gated-off file keeps its parsed content — an inactive patch is still inspectable — and carries
`NotApplied` naming the gate. `SkyPatcher.ini`'s `[Patcher]` `iEnable<Type>Patching=0` switches a
whole type folder off the same way. Both are flags, never a silent drop.

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
2. Evaluate the filters against this record. Built-in families (primary, `noFilter…`, `hasPlugins`,
   keyword / EditorID / name contains, mod-name and override-aware tokens, attached mgefs,
   alternate textures) need no per-record path; the rest come from the field map's `FilterSpec`s. A
   filter that is neither, or is explicitly unmapped-with-reason, makes the line skip loud rather
   than be guessed either way. The player matches only a lone bare primary filter naming it.
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

### Reports and drafts

`SkyPatcherConflicts` is report-only: it names same-field, same-target SET collisions across files
(the later-sorted file wins), plus the ITM classes — intra-file dead writes, cross-INI duplicates,
and no-op writes. Which value *should* win stays with the agent. Add/remove/mult/collection ops
accumulate by design and are not conflicts.

`SkyPatcherDraft` folds an INI that is not yet placed in a mod into the live scan, so a record can
be read as the game would see it once placed. A draft that is already one of the layer's live
files, or whose filename would collide at the same relative path, is refused: which copy wins would
then depend on mod order, not on the draft.

### Known limitation

Line splitting on `:`, item splitting on `,` and compound splitting on `~` are naive, matching the
documented "`:` separates every segment". A rename literal containing `:` or `,`
(`fullName=~Amulet of Mara, Blessed~`), or a plugin filename containing `~`, over-splits. The real
DLL's delimiter precedence must be verified against the running binary before any of this is
hardened.

## Pinned by

- *Contracts*, the catalog paragraph: `SkyPatcherCatalogProbe` (ci probe `skypatcher-catalog-guard`) — an unknown key
  is Unknown, never assumed, and `CrossCheckRouterTable` holds the record dimension against the skill's router table.
- *Contracts*, the field-map paragraph: `SkyPatcherFieldMapProbe` (`skypatcher-fieldmap-guard`) — every path walked
  and every value target parsed against the real Mutagen types, with self-test arms catching a bad path and a bad
  value target. Its stateful-shape arm checks that the catalog's op shape and the map's semantic agree on being
  stateful; that is not the paragraph's leaf-type check, and the stateful-numeric-on-a-non-numeric-leaf, flags-on-a-non-enum
  and dict-on-a-non-dict checks have no self-test arm.
- *The grammar*: `SkyPatcherParseProbe` (`skypatcher-parse-guard`) — a segment with no `=` or an empty key is noted
  and still surfaced, and a doubled `,` is noted and skipped.
- *Addressing*: `SkyPatcherParseProbe` — a bare EditorID is left un-addressed, and the FormID side trims leading
  zeros; `SkyPatcherOverlayProbe`'s load-indexed ESL FormID arm (`FE000800`) — a full ESL FormID keeps its 12-bit
  local id.
- *The per-type subfolder rule and the filename gate*: `SkyPatcherDiscoveryProbe` (`skypatcher-discovery-guard`) —
  the `0`→`z` relative-path order, `<Plugin>.esp.ini` gated and still inspectable, the `[Patcher]` toggle, a
  root-level INI and an undocumented subfolder each noted, loose-only, and the union with `ShadowedProviders`.
- *How the overlay replays onto a record*: `SkyPatcherOverlayProbe` (`skypatcher-overlay-guard`) — the stateful
  apply-order replay, an unknown key poisoning the whole line, an unmapped filter skipping the line loud, and a HARD
  op coming back as a directive.
- *How the overlay replays onto a record*: `SkyPatcherFieldMapProbe`'s mapped-HARD-op self-test arm — CI rejects a
  HARD op that acquires a mapping.
- *Reports and drafts*: `SkyPatcherConflictsProbe` (`skypatcher-conflicts-guard`) — SET collisions with the later
  file winning, accumulating ops not conflicts, and the ITM classes.
- *Reports and drafts*: `RecordsSkyPatcherDraftTests.ADraftThatSetsALeafIsReadInThePostState` — a draft is folded into
  the live scan; `ADraftWhoseFilenameIsAlreadyPlacedIsRefused` and
  `ADraftThatIsAlreadyPlacedIsRefusedRatherThanFoldedInTwice` in the same class — the two refusals.

## Where

`src/housecarl-core/SkyPatcherParse.cs` is the tokenizer; `SkyPatcherCatalog.cs` the catalog; `SkyPatcherFieldMap.cs`
the field map; `SkyPatcherDiscovery.cs` the discovery and apply order; `SkyPatcherOverlay.cs` the replay
(`SkyPatcherOverlay.Apply`); `SkyPatcherConflicts.cs` the conflict and ITM report; `SkyPatcherDraft.cs` the draft fold.
`src/housecarl-mcp/SkyPatcherTools.cs` is the tool front. Tool: `housecarl_skypatcher_layer`; the overlay and the
draft are also read through `housecarl_records`'s SkyPatcher overlay source.

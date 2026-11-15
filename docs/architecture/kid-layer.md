---
updated: 2026-11-15
covers: [src/housecarl-core/KidLines.cs, src/housecarl-core/KidGrammar.cs, src/housecarl-core/DistributorLines.cs, src/housecarl-mcp/KidLayer.cs, src/housecarl-mcp/KidWire.cs, src/housecarl-mcp/DistributorLayer.cs, src/housecarl-mcp/DistributorWire.cs]
---
# The KID layer

## What it is

KID (Keyword Item Distributor) adds keywords to item records at runtime from `_KID.ini` files in `Data`.
`housecarl_layer` reads those files as KID 4.1.0 reads them (4.0.6 and 4.1.0 read their files the same): which files
KID reads and in what order, how each line splits, every form a line names and where, and what KID does where a name
finds nothing, by section. It does not say which items a line tags: no string, trait or chance filter is evaluated,
a form list is not followed to what it holds, and KID's pass order and keyword dependency sort are not modelled.

## Contracts

- **Files are the discovery door's.** `DistributorDiscovery.Scan` on KID's row, as for SPID
  ([`spid-layer.md`](spid-layer.md)): `Data`'s top level, `_KID` anywhere and the `.ini` extension (both ordinal), the
  MO2 winner of each, one ordinal sort. A shadowed copy is named and never read; a copy only inside an archive is listed,
  said not applied, and not read.
- **A line splits as SimpleIni and KID split it** (`KidLines.Parse`): only the unnamed section, the lines before
  the first named `[section]` and any after an empty `[]`, which returns to it; a line whose first non-space character is a comment lead (`;`, `#`) is a comment; the value runs to the
  line's end, so a `;` after a value is part of it. The key `ExclusiveGroup`, exactly, is a group line (`name|members`,
  members split on `,` untrimmed); any other key is a keyword line. The five `|` sections are not trimmed. A type that
  is not one of the row's 19, spelt exactly, drops the line with no log, and it is read first, so such a line throws on
  nothing else. A term holding one `~` whose part before it is no hex number, or a chance that does not start with a
  number, is a throw KID logs as `Failed to parse entry`. Sections after the fifth are ignored, and traits on a type
  that takes none; both are said. The filters split on `,` (each trimmed, `NONE` skipped); an entry holding `+` is a
  group of its own, each part trimmed and read for `-` then `*`.
- **Every form a line names resolves through the one target resolver on KID's row**, `RuntimeTargets`: a term with
  exactly one `~` is an address added to the plugin's runtime base, unmasked; a term with two or more is a name (KID's
  own rule, its row's `targets.manySeparators` = `name`, which the resolver reads first); bare `0x` hex is a runtime FormID; a filter ending `.esm`, `.esp` or `.esl`
  (ordinal) is a plugin term. A filter name is looked up among every type; the keyword section's name among keywords
  only. Every filter of every line is looked up before any keyword, so the call's table walks every type once.
- **Labels** (DECISIONS 2026-11-13 (13) KID Q1 (a)): the keyword is `value`; a filter form of the line's own type (its
  row's `record`), in the plain list or a `+` group, is `target`; any other resolved filter form, an excluded one of
  the line's own type and a group member is `filter`.
- **What KID does where a name finds nothing, by section** (research answer 5): the keyword section's name no keyword
  holds is created, also where a record of another type holds it; KID makes one keyword of a name for every line naming
  it in any case (`Cache.h` `addedKeywords`), so each such line says one keyword is created for all of them and none
  is named its creator, nor the spelling it takes (DECISIONS 2026-11-15 (6)); a keyword FormID that names nothing, or names no
  keyword, drops the line. Every line's keyword is created before any filter is read, so a dropped line still creates
  its keyword (said "KID still creates the keyword, and this line hands it to no item") and another line's filter finds it. A filter FormID,
  runtime FormID or plugin that names nothing, or a record of a type the row's `filterTypes` does not list, is skipped
  and the line said to reach more items (or exclude fewer); when every plain exact filter fails, or every `-` filter
  fails (a `-*` one always stands), the line is dropped. A `+` group with a failing part is dropped; when every group
  is, so is the line. A filter name no record holds names the keyword a KID line creates if one does, else is kept as
  text matched in the running game; a filter ending `.nif` is a model path and is not looked up; a `*` term is a
  partial string. A filter never creates a keyword. A group member that names no keyword is left out; a group keeping
  none is dropped.
- **Selecting lines** (R42; DECISIONS 2026-11-13 (12)(iii)): `formids=` and `plugins=` keep a line when a form it names
  passes the records meaning, the predicate SkyPatcher's and SPID's selectors use; `plugins=` also keeps a line whose
  plugin term names a selected plugin. A plugin term names no form, so `formids=` never keeps it. A created keyword
  names no form. A selector naming a plugin the order does not carry answers as for SPID (pick Q3 (a); #1409).
- **What is resolved when.** The overview lists files and line counts and resolves nothing; the selectors, and a
  `filter=` that at least one KID file matches, resolve every line, since a filter's name may be another line's
  created keyword. A `filter=` no KID file matches resolves nothing. Nothing is kept between calls (R15); there is no
  parse cache (DECISIONS 2026-11-14 (26)).
- **Three formats, one call** (`DistributorWire.RenderLayers`). With `distributor` omitted (or `[]`) the answer is
  SkyPatcher's render, then SPID's part, then KID's, then BOS's ([`bos-layer.md`](bos-layer.md)). With no selector and no `filter=` (pick 2026-11-15 (1) Q1 (b)),
  SPID's and KID's parts are one summary line each, the format's header line closed by the call that lists its files
  (`distributor=["spid"]`, `distributor=["kid"]`); named, the same formats list their files. Otherwise (Q2 (a), format
  order) each later part takes what the earlier ones leave, and a cut notice names the earlier parts and the call that
  reads the format alone (`KID files cut at max_chars after SkyPatcher's and SPID's parts — distributor=["kid"] reads
  KID alone at the whole max_chars`). Past the cap the answer closes on one over-cap sentence: the parts that came first
  with their lengths, then the parts that did not fit, each with its call. A call naming SkyPatcher and SPID only joins
  them as SPID's lane built it. KID is read after SPID in the same replay context, so all three share one EditorID
  table.

## Known limitations

- A form-list filter is named itself and not followed to its members (KID tests a list's members at runtime,
  `ItemData.cpp:212-226`); no dev or ARR line names one (top-up T3).
- Traits are not read beyond whether the type takes any; a misspelt trait is not said, whether KID ignores it or reads
  it as another trait (a substring or digits-only match, or any trait as a soul gem's capacity, KID 4.0.6
  `include/Data/Traits.h`), nor a trait KID throws on.
- An `ExclusiveGroup` member KID removes with `-` is not checked against the members it adds.
- Lines are listed in file order; KID's key-then-line order, its type pass order (Magic Effect first) and its keyword
  dependency sort are not modelled (plan step 30).
- A keyword several lines name is said to be created once for all of them; which line creates it, and so which
  spelling it takes, is not said.
- An EditorID two records share is not reported, as for SPID.
- MergeMapper remaps are not read.

## Pinned by

- `KidLinesTests` — each split rule (comment leads, the unnamed section, the exact type, untrimmed sections, a trailing
  comment by section, the group key, an unreadable FormID, two `~`, the chance throw, entries and groups, extra sections
  and traitless types), each label, each outcome by section (a created keyword, on a dropped line too, a keyword FormID
  that names no keyword, a filter that widens, all plain or all `-` failing, `+` groups, a filter naming a created
  keyword or kept as text, a model path, a partial string), the form list, the plugin term, the load-index note, groups,
  one row driving read and skill text, filters looked up before keywords.
- `KidLayerTests` — the tool on a live order: `plugins=` by EditorID target, the keyword value and the plugin term under
  `plugins=` and not `formids=`, the outcomes under `filter=`, a selector naming an absent plugin as for SPID, the
  shadowed and archive-only copies, an edited file read anew, no EditorID walk added after SPID's, none for a `filter=`
  no KID file matches, and KID taking what SPID leaves with its cut and over-cap sentence.
- `LayerSelectTests.DistributorOmittedReadsSkyPatcherThenSpidThenKidThenBos` (the summary lines), `DistributorKidIsRead`,
  `ADistributorStillToComeIsNotReadYet`, `AnUnknownDistributorIsNotAFormat`.

## Where

`src/housecarl-core/KidLines.cs` (split, read), `src/housecarl-core/KidGrammar.cs` (the row's lines block),
`src/housecarl-core/DistributorLines.cs` (what KID shares with SPID), `src/housecarl-mcp/KidLayer.cs` (KID's split and
read given to the one reader, `DistributorLayer.cs`), `src/housecarl-mcp/KidWire.cs` (KID's own words); the render
and the join are `DistributorWire` (`RenderLayers`), the tool front `SkyPatcherTools.Layer`.

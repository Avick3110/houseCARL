---
updated: 2026-11-16
covers: [src/housecarl-core/FlmLines.cs, src/housecarl-core/FlmGrammar.cs, src/housecarl-core/DistributorLines.cs, src/housecarl-mcp/FlmLayer.cs, src/housecarl-mcp/FlmWire.cs, src/housecarl-mcp/DistributorLayer.cs, src/housecarl-mcp/DistributorWire.cs]
---
# The FLM layer

## What it is

FLM (FormList Manipulator) adds forms to form lists at runtime from `_FLM.ini` files in `Data` and every `.ini` in
`Data\FLM`. `housecarl_layer` reads those files as FLM 1.8.1 reads them (dev and ARR both run it; its rules come from
the 1.7.0 source, the last published, and 1.8.1's own disassembly where they differ, flm-lines research answers 2-4 and
the top-up T1, T2): which files FLM reads and in what order, how SimpleIni and FLM split each line, the two passes and
the names they hold, every form a line names and where, each line's filter verdict, what FLM does where a name finds
nothing, and what each kept line adds to which list, a form already in it skipped. It does not say what a list holds in
the running game, which forms a collection gathers, or what a mod event adds (spec concern 15, plan step 30).

## Contracts

- **Files are the discovery door's.** `DistributorDiscovery.Scan` on FLM's row: `Data`'s top level, `_FLM` anywhere in
  the name and the `.ini` extension (both ordinal), sorted ordinally; then every `.ini` directly in `Data\FLM`, which
  FLM appends unsorted, so each says its place there is not FLM's; the MO2 winner of each. A shadowed copy is named and
  never read; a copy only inside an archive is listed, said not applied, and not read.
- **A line splits as SimpleIni and FLM split it** (`FlmLines.Parse`): a line whose first non-space character is `;` or
  `#` is a comment; only the unnamed section is read (the lines before the first named header and after an empty
  `[]`); a line with no `=` or no key is not read; the key is matched in any case against FLM 1.8.1's 16 keys, and one
  FLM does not know is listed as logged and invalid. The value is sanitized as FLM sanitizes it (spaces round `|` and
  `,`, the leading zeros after a lower-case `0x` when there are two or more, then a FormID written with index 02 or 04 rewritten to Dawnguard's or Dragonborn's,
  in any case and whatever plugin follows, noted as a load index is, DECISIONS 2026-11-13 (12)(ii)); a Filter line is
  then lowercased. Fields split on `|` and items on `,`, an empty field kept (CLibUtil's split), none for empty text.
- **FLM's order** (`FlmLines.Visits`): pass 1 reads, file by file, its Filter lines, then its Collection lines; pass 2, file
  by file, its Alias and Group lines, then the rest; each pass visits a file key by key (A to Z, any case) and each
  key's lines in file order. So filters and collections count from every file; an alias or group counts from its own
  file on, and a line of an earlier file naming it finds nothing. A name defined twice keeps the first (filter names
  lowercased; alias, group and collection names as written).
- **Every form a line names resolves through the one target resolver on FLM's row**, `RuntimeTargets`: any `~` is an
  address, the FormID first and the plugin up to the next `~`, a ten-character FormID losing characters 2-3 first
  (noted); `0x` hex with no `~` a runtime FormID, an upper-case `0X` a name; anything else an EditorID looked up among
  every type, then FLM's type check for the field (a list must be a FormList, a collection keyword a Keyword). FLM has no
  plugin-name term (`targets.pluginTerm` = `notATerm`): a filter's plugins gate the line and touch nothing (DECISIONS
  2026-11-13 (12)(iii)), so `plugins=` never selects a line by them.
- **Labels** (research answer 6, DECISIONS 2026-11-13 (14)): the list is `target` (through an alias, `list through
  alias`; a simplified key's fixed list `implied list`); each item, group member, pair half and a `*list`'s list
  `value`; what a group or `*list` brings in is labelled `items through group` / `items through list` (a `*list`'s forms
  are its winning record's, read once per call, never an earlier FLM add); a collection's keywords `filter`, and the
  keywords of a `#collection` item `items through collection`, its members not listed.
- **The filter verdict** (`FlmLines.Evaluate`): every filter field states its verdict on the order, true, false or
  invalid, and why. `,` separates alternatives; FLM's split takes the nearest plugin extension from the last cut and
  goes on only while another of `.esp`, `.esl` and `.esm` is still ahead, so a later condition of the same extension is
  never checked and the line says which part was skipped (top-up T2 (2)); a part led by neither `+` nor `-` makes the
  filter invalid; a part naming no extension reads as true. A plugin counts when the order carries it, Creation Club
  plugins the engine loads included (top-up T1). A `#name` reads the Filter line's verdict. A false filter counts the
  line filtered out and adds nothing; an invalid one counts it invalid.
- **What FLM does where a name finds nothing, by field** (research answers 4 and 6): a list, or an alias no Alias line
  defines by then, drops the line (`Unable to find FormList`, `Unknown Alias`); an item, a group member, an alias list,
  an empty item or a `#name` naming no group or collection is skipped and the rest stand; one collection keyword
  naming nothing drops the whole collection (`Keyword … do not exist!`); a pair half naming nothing drops the pair; a
  group or alias left with nothing is omitted. A wrong field count is invalid (`Wrong … format`). `Remove` lines are
  counted valid and do nothing at data load and `FormsPair` lines are always invalid (1.8.1's handlers, top-up T2 (1));
  `DragonbornSpiderCrafting` lines are read and never added; a mod event line, which FLM checks at load and acts on
  when the event is sent in the running game, is neither checked nor resolved.
- **What a kept line adds** (`FlmLines.Rows`): FLM fills its lists in the row's `addOrder` (the simplified keys, then
  FormList lines), each key's lines in pass-2 order, and skips a form already in the list: its winning record's own, or
  one an earlier add or the same line put there; each row says how many it adds and which it skips and why. A pair is
  skipped whole when either half is already in its list. What a collection gathers is said not read.
- **Selecting lines** (R42): `formids=` and `plugins=` keep a line when a form it names passes the records meaning,
  through a group, alias, list or collection included. A selector naming a plugin the order does not carry answers as
  for the other formats (#1409).
- **What is resolved when.** The overview lists files and line counts and resolves nothing; the selectors, and a
  `filter=` at least one FLM file matches, resolve every line, since names cross files. Nothing is kept between calls
  (R15); there is no parse cache (DECISIONS 2026-11-14 (26)).
- **Five formats, one call** (`DistributorWire.RenderLayers`). With `distributor` omitted (or `[]`) FLM's part follows
  SkyPatcher's, SPID's, KID's and BOS's: one summary line with no selector or `filter=` (pick 2026-11-15 (1) Q1 (b)),
  else it takes what the earlier parts leave and says where it is cut (Q2 (a)). FLM is read after BOS in the same
  replay context, so all five share one EditorID table. `lid`, a later format on the same table, is answered not read
  yet by name.

## Known limitations

- What a list holds in game, a collection's members, and a mod event's adds are not read (spec concern 15, step 30); a
  `#collection` item's row names the collection, not its forms. A filtered-out collection, whose keywords FLM has
  stored, may still be filled at load; that is said, not shown.
- A list's own forms are its winning record's; a runtime edit of the list by another framework (SkyPatcher's) is not
  counted in the duplicate skip.
- A `0x` written anywhere but at a token's start (`Ab0x12`, which FLM reads as a FormID) reads as a name; no dev or ARR
  token reaches it. MergeMapper remaps are not read. A ten-character FormID side with no `0x` loses its characters 2-3
  in FLM and its top byte here (the shared rule); no dev or ARR side is ten characters.
- `Data\FLM`'s order under MO2's virtual folder is not read (top-up T2 (3)); no dev or ARR file sits there.
- A collection name defined again after a filtered-out first one appends keywords in FLM; here the later one replaces it.

## Pinned by

- `FlmLinesTests` — each split rule (comment leads, the unnamed section, no `=`, keys in any case and an unknown key,
  Sanitize and the DLC rewrite, empty fields), each filter rule (alternatives, the same-extension skip, the trailing
  `|`, a named filter, a name defined twice, the verdict and that it touches nothing), each label and outcome by field,
  the passes and name scope, and the adds with their duplicate skips and fill order.
- `FlmLayerTests` — the tool on a live order: `plugins=` by EditorID value; through a group, a `*list` and an alias;
  an implied list; a filter's plugin no touch; the shadowed copy; `Data\FLM`'s files and their order sentence; no files;
  an edited file read anew; no EditorID walk added; FLM taking what BOS leaves with its cut; an absent plugin selector;
  the omitted overview's summary line.
- `FlmLayerHeldTests` — FLM's part held whole, byte for byte. `DistributorLayerHeldTests` — FLM's summary line and part
  after the held SPID, KID and BOS parts, and the over-cap sentence naming it.
- `RuntimeTargetsTests.FlmsRowReadsEachTokenAsFlmDoes`, `LayerSelectTests.DistributorFlmIsRead`, `LidIsNotReadYetByName`,
  `DistributorOmittedReadsSkyPatcherThenSpidThenKidThenBosThenFlm`.

## Where

`src/housecarl-core/FlmLines.cs` (split, passes, read, filter, adds), `src/housecarl-core/FlmGrammar.cs` (the row's lines
block), `src/housecarl-core/DistributorLines.cs` (the shared token resolve), `src/housecarl-mcp/FlmLayer.cs` (FLM's
split, read and rows given to the one reader, `DistributorLayer.cs`), `src/housecarl-mcp/FlmWire.cs` (FLM's own words
and order); the render and the join are `DistributorWire` (`RenderLayers`), the tool front `SkyPatcherTools.Layer`; a
list's own forms are read by `SkyPatcherReplay.ListItemsOf`.

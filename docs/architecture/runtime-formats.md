---
updated: 2026-11-11
covers: [src/housecarl-core/runtime-formats.json, src/housecarl-core/RuntimeFormats.cs, src/housecarl-core/DistributorFormats.cs, src/housecarl-core/DistributorDiscovery.cs, src/housecarl-core/RuntimeTargets.cs, src/housecarl-generator/RuntimeFormatSkillText.cs]
---
# The runtime-format source

## What it is
One data file holds, per runtime-layer format, where its files are found, the order they apply in, who wins a
conflict and how a line is split. The server reads those values; the generator prints the skill paragraphs that state
them. The file has two shapes of row: SkyPatcher's, which carries everything down to its line grammar, and a
**distributor** row for SPID, KID, BOS and FLM, which carries discovery and target spelling only — each of those
formats' line kinds, conflict model and labels land with its own reader.

## Contracts
- **A distributor row says where and how a target is spelt, not what a line means.** `folders` is one or more folders
  under Data, each with its name token, its extension, the comparison for each (the four are ordinal, where SkyPatcher's
  ending rule is lower-case), whether the read descends, and whether that folder's list is sorted. `targets` says which
  side of the separator the FormID is written on, what the written number means (the record's local object id, a
  number added to the plugin's runtime base as the game's own lookup adds it, with no mask, or that add with the
  written load index erased from a ten-character side first, as FLM's own reader erases it), whether bare hex or a
  plugin name is a term of its own, what a name no record holds does, and which records an EditorID is looked up among.
- **`unread` is not a claim.** A `targets` value the research has not settled for that format is written `unread`, and
  the resolver then answers "not read yet" for every token of that format carrying no separator, rather than guessing
  one; an address is read either way, because its rules are sourced for all four.
  BOS and FLM carry four of those today; filling one is its format lane's work.
- **One resolver, one spelling per row.** `RuntimeTargets` turns a token into a record for every format, SkyPatcher
  included: SkyPatcher's row is one `targets` block among five. SkyPatcher's address character is its `grammar.address`
  and is not repeated in its `targets`, so the row spells it once.
- **Discovery goes through the asset door's top-level listing.** `DistributorDiscovery.Scan` lists each row folder with
  `EnumerateTopLevel` (or the full walk, for a row that descends), keeps the names the row takes, and resolves each to
  its MO2 winner; a copy only inside an archive is listed and said not to be applied. Each row folder is listed in turn,
  as the DLL appends them, and every folder's files come back in one ordinal sort, the ones the row marks unsorted
  included: one merged listing cannot reproduce a filesystem's own order, so a file out of such a folder carries the
  sentence saying its place among that folder's files is not the DLL's rather than a place claimed to be.
- **One source, two readers.** `SkyPatcherDiscovery` and `SkyPatcherParse` take the root, the name token, the gate
  extensions, the `SkyPatcher.ini` section and key shape, the patchers that run without a key, the patcher run order and
  the delimiters from SkyPatcher's row. `RuntimeFormatSkillText` prints the same values into marked regions of the
  `skypatcher-authoring` files.
- **A kind is a name the code knows.** The file order, the comparer, the conflict rules, the gate kind, the
  missing-switch default and the other kind values select a rule written in code; a value the code has no rule for
  fails the load, naming the field. Changing a rule's behaviour is a code change; the row only picks it.
- **What a filter means stays code.** The per-type filter and operation vocabulary stays in `skypatcher-catalog.json`
  (checked against the skill's router table, [`skypatcher-layer.md`](skypatcher-layer.md)); what each filter selects
  stays in `SkyPatcherReach` and the field map, tested per filter.
- **Readings are marked.** A `reading` block holds a rule upstream does not state (an INI in the root is not read,
  loose only, `#` comments, `[label]` lines). The server applies it; no generated skill paragraph prints it.
- **Generated regions are committed.** Each region sits between `<!-- generated:<tag> -->` and
  `<!-- end generated:<tag> -->`; the text between is the print, byte for byte, in the file's own line ending. Edit the
  source, then `dotnet run --project src/housecarl-generator -c Release -- skill-text` from the repo root. The plugin
  build copies the skill files as they are, with no step of its own.
- **Printed text names no server tool.** A region speaks for SkyPatcher only and never names houseCARL. DECISIONS
  2026-11-03 (1) strips skill lines that mention houseCARL but no tool; a printed region holds the stricter rule, no
  mention at all.
- Not printed from the source, so still hand-written: SKILL.md's "Build the string" step and its conflict-model
  note, the per-type toggle reminder beside the placement paragraph, the `[Features]` switches, and the layer tool's
  own description. These restate a row value by hand, and a row edit plus a regenerate leaves them as they are:
  - `skypatcher-authoring`: SKILL.md's example path (`Data/SKSE/Plugins/SkyPatcher/weapon/…`) and its global
    `SkyPatcher.ini` mention; grammar-core.md's `iEnable<Type>Patching`, its read-order sentence, its `SkyPatcher.ini`
    toggles sentence and its §8 heading; placement-and-conflicts.md's example paths (`SkyPatcher/npc/…`), its `;`
    comment bullet, its contents line's and §6 heading's `SkyPatcher.ini` and its `Data/SKSE/Plugins/SkyPatcher.ini`
    line, grammar-core.md's §6 pointer's `[Patcher]`; object-modification.md's `iEnableObjectModificationPatching`,
    `SkyPatcher/objectModification/` and its toggle line's `SkyPatcher.ini`; each per-type reference's header line
    (`SkyPatcher/<folder>/`, `iEnable<Type>Patching`, and light.md's "on unless").
  - `kid-authoring`: SKILL.md's two SkyPatcher folder paths, grammar-core.md's one, and
    `references/_CORPUS_STATUS.md`'s `SKSE\Plugins\` per-type subfolders.
  - The server: the layer tool's two empty-layer answers spell `Data\SKSE\Plugins\SkyPatcher` and its folder
    lines' switch-off and unreadable marks spell `SkyPatcher.ini` (`SkyPatcherTools`); the draft's not-absolute
    refusal's example path spells `SkyPatcher\weapon\` (`SkyPatcherDraft.Prepare`); the overlay's packed-value unpack
    and two item splits use literal `=` and `,` (`SkyPatcherOverlay`); the records tool's source parameter text and
    its malformed-overlay error spell `.ini` (`RecordsTools`); the merge reminder's donor-named config spells
    `Plugin.esp.ini` (`WriteSentences`).

## Pinned by
- `RuntimeFormatSourceTests.TheCommittedSkillRegionsMatchAFreshPrint` — the committed regions are the print, and
  every region has a home.
- `RuntimeFormatSourceTests.MovingAPassChangesTheFolderOrderAndTheSkillText`,
  `APatcherOnWithoutItsKeyRunsAndTheSkillTextNamesIt`, `ChangingTheNameTokenChangesWhatIsReadAndTheSkillText`,
  `ChangingTheRootChangesTheTreeAndThePlacementPath`, `ChangingADelimiterChangesTheSkillText`,
  `TheSwitchesSectionAndKeyShapeOfTheRowDriveTheScanAndTheSkillText` — one row changes both outputs: the server's
  order, switch, name, gate, scan root, switches file, section, key shape or parse, and the printed text.
- `RuntimeFormatSourceTests.EveryDelimiterOfTheRowDrivesTheParse`, `AnEditedGateExtensionGatesTheName` — each
  delimiter, comment lead and the label reading drive the file and address parse; the gate extensions drive the gate.
- `RuntimeFormatSourceTests.TheOffReasonAndTheDraftRefusalSpellTheRow`,
  `TheSwitchesNotesAndTheDraftPathRefusalsNameTheRowsFile` — the answers that name the key shape, the switches file
  and the name token spell the row's.
- `RuntimeFormatSourceTests.AnIndentedOrMalformedMarkerIsUnplacedAndRefused` — one rule for which line opens a region.
- `RuntimeFormatSourceTests.AValueTheServerHasNoRuleForFailsTheLoad` — each kind value the code has no rule for.
- `RuntimeFormatSourceTests.NoPrintedRegionNamesTheServer`, `AHandEditInsideARegionIsRewritten`,
  `AnUnknownOrUnclosedRegionIsRefused`, `TheSourceAndTheCatalogNameOneVersion`.
- `DistributorDiscoveryTests` — each discovery rule is the row's: top level only (and a row that descends reads the
  nested file), the token and the extension case-sensitive (and a row that ignores case reads both near misses), one
  ordinal sort, FLM's second folder appended with its order sentence, the MO2 winner per name with the shadowed copies
  named, a BSA-only copy said not applied, and a value the server has no rule for failing the load, field by field.
- `RuntimeTargetsTests` — each target rule is the row's: a FormID written first, a load index written into it, bare
  hex, the two plugin-name rules, an every-type name lookup, a name no record holds, and an `unread` rule answering
  "not read yet". The same tokens under SkyPatcher's row read as SkyPatcher reads them today.
- `LayerSelectTests.ADistributorStillToComeIsNotReadYet` — the `distributor=` refusals spell the four rows' names.

## Where
`src/housecarl-core/runtime-formats.json` (embedded), loaded by `RuntimeFormats.SkyPatcher` and
`RuntimeFormats.Distributors`; `src/housecarl-core/DistributorFormats.cs` reads the distributor rows,
`src/housecarl-core/DistributorDiscovery.cs` finds their files and `src/housecarl-core/RuntimeTargets.cs` resolves
their targets; `src/housecarl-generator/RuntimeFormatSkillText.cs`, run as the generator's `skill-text` mode.
`dotnet run --project src/housecarl-generator -c Release -- distributor-front --mo2 "<instance>"` measures the listing,
the discovery counts and the parse on a real order.

## Related
- [`skypatcher-layer.md`](skypatcher-layer.md) — the layer these rules feed.

---
updated: 2026-11-16
covers: [src/housecarl-core/runtime-formats.json, src/housecarl-core/RuntimeFormats.cs, src/housecarl-core/DistributorFormats.cs, src/housecarl-core/SpidGrammar.cs, src/housecarl-core/KidGrammar.cs, src/housecarl-core/BosGrammar.cs, src/housecarl-core/FlmGrammar.cs, src/housecarl-core/DistributorDiscovery.cs, src/housecarl-core/RuntimeTargets.cs, src/housecarl-generator/RuntimeFormatSkillText.cs]
---
# The runtime-format source

## What it is
One data file holds, per runtime-layer format, where its files are found, the order they apply in, who wins a
conflict and how a line is split. The server reads those values; the generator prints the skill paragraphs that state
them. The file has two shapes of row: SkyPatcher's, which carries everything down to its line grammar, and a
**distributor** row for SPID, KID, BOS and FLM, which carries discovery and target spelling, and a `lines` block once
the format's reader has landed: all four have ([`spid-layer.md`](spid-layer.md), [`kid-layer.md`](kid-layer.md), [`bos-layer.md`](bos-layer.md),
[`flm-layer.md`](flm-layer.md)). BOS and FLM have no skill, so their rows print no region (bos-lines research answer 2,
flm-lines research answer 2).

## Contracts
- **A distributor row says where and how a target is spelt, not what a line means.** `folders` is one or more folders
  under Data, each with its name token, its extension, the comparison for each (the four are ordinal, where SkyPatcher's
  ending rule is lower-case), whether the read descends, and whether that folder's list is sorted. `targets` says which
  side of the separator the FormID is written on, what the written number means (the record's local object id, a
  number added to the plugin's runtime base as the game's own lookup adds it, with no mask, or that add with the
  written load index erased from a ten-character side first, as FLM's own reader erases it), whether bare hex or a
  plugin name is a term of its own and which extension text makes one (`pluginExtensions`, the row's own list: SPID
  takes `.es` anywhere in the term, KID `.esm`, `.esp` or `.esl` at its end), what a name no record holds does, and
  which records an EditorID is looked up among.
- **`unread` is not a claim.** A `targets` value the research has not settled for that format is written `unread`, and
  the resolver then answers "not read yet" for every token of that format carrying no separator, rather than guessing
  one; an address is read either way, because its rules are sourced for all four.
  No shipped row carries one since FLM's lane filled its four, as BOS's lane filled BOS's.
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
  `skypatcher-authoring` files. SPID's row does the same: `SpidLines` reads its `lines` block (`SpidGrammar`: the
  comment leads, the delimiters, the key kinds and their fields, the `Final` types, the type table with each type's
  run order, conflict and missing-value outcome, the filter, target and string-lookup record types) and its
  `targets`, and the generator prints the same values into the `spid-authoring` files' `spid.*` regions. KID's row
  does the same: `KidLines` reads its `lines` block (`KidGrammar`: the comment leads, the delimiters, the group key,
  the five sections, the type table with each type's record, pass and whether it takes traits, the model ending, the
  keyword record and the filter record types) and its `targets`, and the generator prints the same values into the
  `kid.*` regions of `kid-authoring`'s `grammar-core.md` ([`kid-layer.md`](kid-layer.md)).
- **A missing name by position.** Every distributor row's `targets.missingName` is `byPosition`: the resolver says only that a
  name finds nothing, and the format's reader says what it does there: SPID's by the line's type and the field (SPID
  research answer 3), KID's by the section (KID research answer 5: a keyword is created, a filter kept as text), BOS's by the field (a line dropped, a set member left out, a condition kept as text), FLM's by the field (a list missing drops the line, an item missing is skipped, a keyword missing drops its collection). No
  row has the resolver say a keyword is created.
- **A term holding the separator twice is the row's call.** Each distributor row states `targets.manySeparators`:
  `addressAtFirst` (SPID, FLM: split at the first `~`, the plugin up to the next) or `name` (KID, BOS: no address),
  which `RuntimeTargets.Read` reads before anything else; SkyPatcher's row states none and keeps the address at the
  first, as its reader always has (`RuntimeTargetsTests.ATermHoldingTheSeparatorTwiceIsReadByItsRowsRule`).
- **A lines block on a row with no reader fails the load.** SPID's, KID's, BOS's and FLM's readers have landed; a `lines`
  block on any other row has no rule. A format later on the same table with no row yet (LID) is answered "not read
  yet" by name from the refusal's own list (`SkyPatcherTools.Later`, DECISIONS 2026-11-13 (14) Q4).
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
    `references/_CORPUS_STATUS.md`'s `SKSE\Plugins\` per-type subfolders; its placement step, which restates KID's
    row's `_KID` name token and its flat `Data\` root, and grammar-core.md §2's flat-`Data\` paragraph; the version
    lines (SKILL.md, grammar-core.md's source note and §7, `_CORPUS_STATUS.md`, types.md's note), which name 4.1.0
    and 4.0.6 by hand beside the `kid.discovery` region's modelled version; and the line shapes SKILL.md and
    grammar-core.md §4 and §7 write with no space beside `|`.
  - `spid-authoring`: SKILL.md's placement step and its filename rule, which restate SPID's row's `_DISTR` name token,
    its flat `Data/` root and its byte-order file sort; grammar-core.md §2's subfolder paragraph (`_DISTR`, `.ini`);
    and the skill's version line (7.3.0), which the `spid.discovery` region's modelled version does not replace.
  - The server: the layer tool's `distributor=` parameter text names the four distributor rows' keys and restates
    SPID's, KID's, BOS's and FLM's discovery and labels, and the refusals name the rows (`SkyPatcherTools`); SPID's render spells its order
    sentence and the Npc/keyword string-lookup wording (`SpidWire`, `SpidLines`); KID's reader spells its log
    lines (`Failed to parse entry`, `FAIL - keyword doesn't exist`) and the string-match wording (`KidLines`); BOS's
    reader spells its log lines (`BASE formID not found`, `SWAP formID not found`, `failed to process`) and the
    condition-match wording (`BosLines`), and its render the map order sentence (`BosWire`); FLM's reader spells its log
    lines (`Unable to find FormList`, `Unknown key`, `Keyword … do not exist!`) and its verdict wording (`FlmLines`), and
    its render the pass and fill order sentence (`FlmWire`); the layer tool's two empty-layer answers spell `Data\SKSE\Plugins\SkyPatcher` and its folder
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
  hex, the two plugin-name rules, an every-type name lookup, a name no record holds (and `createsKeyword` no longer a
  value), and an `unread` rule answering "not read yet". The same tokens under SkyPatcher's row read as SkyPatcher
  reads them today.
- `LayerSelectTests.LidIsNotReadYetByName` — the `distributor=` refusals spell the later format's name and the formats
  read; `DistributorSpidIsRead`, `DistributorKidIsRead`, `DistributorBosIsRead`, `DistributorFlmIsRead` — a row with a
  lines block is read; `BosLinesTests` and `BosLayerTests` BOS's ([`bos-layer.md`](bos-layer.md)); `FlmLinesTests` and
  `FlmLayerTests` FLM's ([`flm-layer.md`](flm-layer.md)).
- `SpidLinesTests` and `SpidLayerTests` — SPID's lines block drives the read ([`spid-layer.md`](spid-layer.md));
  `KidLinesTests` and `KidLayerTests` KID's ([`kid-layer.md`](kid-layer.md)), and
  `KidLinesTests.OneRowDrivesTheReadAndTheSkillText` one edited type string both outputs;
  `RuntimeFormatSourceTests.TheCommittedSkillRegionsMatchAFreshPrint` holds the `spid.*` and `kid.*` regions too.

## Where
`src/housecarl-core/runtime-formats.json` (embedded), loaded by `RuntimeFormats.SkyPatcher` and
`RuntimeFormats.Distributors`; `src/housecarl-core/DistributorFormats.cs` reads the distributor rows,
`src/housecarl-core/DistributorDiscovery.cs` finds their files and `src/housecarl-core/RuntimeTargets.cs` resolves
their targets; `src/housecarl-generator/RuntimeFormatSkillText.cs`, run as the generator's `skill-text` mode.
`dotnet run --project src/housecarl-generator -c Release -- distributor-front --mo2 "<instance>"` measures the listing,
the discovery counts and the parse on a real order.

## Related
- [`skypatcher-layer.md`](skypatcher-layer.md) — the layer these rules feed.

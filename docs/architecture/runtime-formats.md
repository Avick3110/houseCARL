---
updated: 2026-11-11
covers: [src/housecarl-core/runtime-formats.json, src/housecarl-core/RuntimeFormats.cs, src/housecarl-generator/RuntimeFormatSkillText.cs]
---
# The runtime-format source

## What it is
One data file holds, per runtime-layer format, where its files are found, the order they apply in, who wins a
conflict and how a line is split. The server reads those values; the generator prints the skill paragraphs that state
them. Only SkyPatcher has a row today; SPID, KID, BOS and FLM join as rows when their readers are built.

## Contracts
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
- **Generated regions are committed.** Each region sits between `<!-- generated:<tag> … -->` and
  `<!-- end generated:<tag> -->`; the text between is the print, byte for byte, in the file's own line ending. Edit the
  source, then `dotnet run --project src/housecarl-generator -c Release -- skill-text` from the repo root. The plugin
  build copies the skill files as they are, with no step of its own.
- **Printed text names no server tool.** A region speaks for SkyPatcher only (the skill rule in DECISIONS
  2026-11-03).
- Not printed from the source, so still hand-written: SKILL.md's "Build the string" step and its conflict-model
  note, the per-type toggle reminder beside the placement paragraph, the `[Features]` switches, and the layer tool's
  own description.

## Pinned by
- `RuntimeFormatSourceTests.TheCommittedSkillRegionsMatchAFreshPrint` — the committed regions are the print, and
  every region has a home.
- `RuntimeFormatSourceTests.MovingAPassChangesTheFolderOrderAndTheSkillText`,
  `APatcherOnWithoutItsKeyRunsAndTheSkillTextNamesIt`, `ChangingTheNameTokenChangesWhatIsReadAndTheSkillText`,
  `ChangingTheRootChangesTheTreeAndThePlacementPath`, `ChangingADelimiterChangesTheSkillText` — one row changes both
  outputs.
- `RuntimeFormatSourceTests.AValueTheServerHasNoRuleForFailsTheLoad` — each kind value the code has no rule for.
- `RuntimeFormatSourceTests.NoPrintedRegionNamesTheServer`, `AHandEditInsideARegionIsRewritten`,
  `AnUnknownOrUnclosedRegionIsRefused`, `TheSourceAndTheCatalogNameOneVersion`.

## Where
`src/housecarl-core/runtime-formats.json` (embedded), loaded by `RuntimeFormats.SkyPatcher`;
`src/housecarl-generator/RuntimeFormatSkillText.cs`, run as the generator's `skill-text` mode.

## Related
- [`skypatcher-layer.md`](skypatcher-layer.md) — the layer these rules feed.

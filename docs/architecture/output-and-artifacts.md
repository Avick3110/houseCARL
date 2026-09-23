---
updated: 2026-09-23
covers: [src/housecarl-mcp/OutputLocations.cs, src/housecarl-mcp/Artifacts.cs, src/housecarl-mcp/ResultsStore.cs, src/housecarl-core/ResultArtifact.cs, src/housecarl-core/AtomicFile.cs, src/housecarl-core/FileStamp.cs, src/housecarl-core/OrderStamp.cs, src/housecarl-core/PathArguments.cs]
---
# Output locations and result artifacts: where a write lands, and the contracts that hold it

## What it is

Everything houseCARL writes lands in a houseCARL-owned MO2 mod folder, a folder the caller named outright, or a
result artifact file. These are the contracts those three share, cited from the files above under ADR 0001.

## Contracts

### Where output lands

- **A houseCARL-owned mod folder, by default.** Plugins and every non-`.esp` rider — compiled scripts, a packed
  `.bsa`, extracted loose files, a generated `.seq` — go into `<ModsDir>\houseCARL - <stem>`. Ownership is
  `[houseCARL] generated=true` in the folder's `meta.ini`, the one mod-root file MO2 does not deploy into Data. A
  missing or stripped marker reads as NOT owned.
- **`into=` extends an owned folder** through one resolver shared by the `.esp`, rider and asset lanes. Four
  ownership-gated arms, in order: canonical `houseCARL - <stem>`; the owned folder holding `<stem>.esp` (the `.esp`
  basename is fixed by whatever binds the patch, the folder name is the user's to rename); the folder's own name;
  then a refusal naming every place searched, offering only `into=` spellings that resolve back. `needEsp` tightens
  the canonical arm for the record lane.
- **A fresh stem is auto-suffixed** to `<stem>_NNN` when a mod folder of that name exists or an ACTIVE plugin is
  named `<stem>.esp`. Two departures from suffixing:
  - a stem that would shadow a plugin the active order is NOT loading REFUSES (#561);
  - a lane whose artifact's exact basename is load-bearing (the `.bsa` the game auto-loads under its plugin's
    basename, the merged plugin) refuses a TAKEN stem **the caller named**; a defaulted stem is suffixed like any
    other.
- **`out_path=` is the escape hatch:** the caller names a mod-folder ROOT and houseCARL appends the artifact's
  subfolder (`Scripts\`, `SEQ\`), never doubling a segment already there. The folder is the user's, so
  `CreatedFresh=false` and residue cleanup never touches it; `into=` stays ownership-gated on these lanes.
- **Deployability is a shape rule**, shared by every `out_path=` lane that emits a game-loaded file (`Scripts`,
  `SEQ`); the decompile lane appends nothing and checks nothing, because a `.psc` is compiler input. MO2 overlays
  a mod folder's CONTENTS onto the Data root, so the served shapes are exactly
  `<mods>\<modFolder>\<sub>`, `<overwriteDir>\<sub>` and
  `<data>\<sub>`; anything else warns. The rule is shared, the sentence naming what a mis-placed artifact costs
  stays with each caller.
- **A rider that failed after cutting a fresh folder** deletes it when it holds nothing but our own `meta.ini`, and
  otherwise keeps it and names its path. A reused `into=` folder is never touched.
- **Result artifacts:** an auto-spill goes to the server-managed results directory (`HOUSECARL_DATA_DIR`, else the
  server binary's folder, plus `results\`), pruned after `ResultsStore.PruneAfterDays` days. A caller-named
  `to_file=` never lands there, and one pointing into it is refused by name.
- **Two dispositions write one:** `to_file=`, because the caller asked, which renders only the manifest inline; and
  the `ceiling` auto-spill, because the inline render hit `max_chars`, which renders the prefix it managed and claims
  the complete result only when the file holds every match, naming where the missing matches are when it cannot.

### The reservation is the file

An auto-spill claims its path by CREATING the file with `FileMode.CreateNew`, not by probing `File.Exists`, and the
artifact is written through that same exclusive handle.

A caller-named target carries the opposite hazard — it is the CALLER's file and may already hold an artifact they
want — so it is written through a same-directory temp moved into place. A crash mid-write cannot pass a half
artifact off as a whole one: the manifest is line 1 and carries `row_count`, so a short file fails its own manifest.
An artifact is immutable once written; a re-run writes a new file, or overwrites a caller-named target wholesale.

### Re-entering an artifact

An `@<path>` list input whose target is an artifact yields that artifact's IDENTITY column as the list, and
server-side consumption is EPOCH-CHECKED against the current build: a mismatch is a loud refusal naming both
epochs, with deliberately NO stale-override parameter. The check happens after the consuming call's own capture,
inside the service, so the check and the answer read the same build. An artifact whose identity column is not the
one the parameter takes is refused by name, and an identity that is not a FormID is not epoch-checked at all,
because it names no record that could go stale.

**Error rows are not identity-bearing.** A row carrying an `error` member documents a failure and does not name a
record, so extraction SKIPS them: re-entering an artifact means "the records this file resolved". An all-error
artifact refuses by its real cause, never by accusing the file; the "was it edited?" refusal is reserved for a
SUCCESS row missing the identity column, which a server-written artifact never contains.

### The atomic-write contract

`AtomicFile.Commit` is the one primitive every houseCARL FINAL swap funnels through. The caller stages a complete
file into a temp on the SAME volume as the final path, then hands it here: an existing target is swapped with
`File.Replace`, which keeps the destination's NTFS identity and creation time; an absent one is served by
`File.Move`, because `File.Replace` cannot create.

Two contracts hold it. Same-volume staging is the caller's, and the `FileNotFoundException`-ONLY catch is what
keeps that honest: a cross-volume swap surfaces as `IOException` and an EFS or special-ACL target as a
metadata-merge error, and both must stay loud with the original byte-intact rather than degrade into a non-atomic
copy. The 3-argument overload is deliberate — the 4-argument `ignoreMetadataErrors: true` would swallow that
failure. `AtomicFile` holds no handle at rest.

Crash-atomicity itself is not demonstrable in one process and is not claimed anywhere. Fresh-CREATE writes
deliberately do not funnel through it: there is no original to lose.

### The freshness stamp: last write plus size

`FileStamp` is one filesystem entry's last-write time AND its length, and it is THE key every houseCARL cache
stamps against — the plugin read cache, the MO2 profile gate, the BSA and loose-subtree tables, the SkyPatcher INI
parse cache — so there is one answer to "has this changed" rather than one per cache. Length is in the key because
an edit inside the filesystem's timestamp granularity, or one whose tool restores the timestamp it found, leaves
the mtime where it was (#406). Both terms come from ONE stat. `FileStamp.Absent`, with its negative length, is the
single sentinel for missing, locked and unreadable.

### The order stamp: epoch plus health

`OrderStamp` carries an index build's epoch AND the plugins that build lost to a load failure as ONE value, because
a legitimate reorder changes the epoch too and the epoch alone cannot tell a degraded order from a reordered one
(#353). The health is a SIBLING of the epoch, never folded into it: the epoch is opaque and compared only for
equality.

### The absolute-path rule

Every path a CALLER names — an `out_path=` folder, a `to_file=` artifact, an `@file` list, a draft INI — must be
FULLY QUALIFIED, not merely rooted: the server's working directory is not the caller's, and `C:work` or `\work`
resolve against the server's own directory while the response names the path that was typed. One definition,
`PathArguments.NotAbsolute`, in core because the predicate and draft readers are there, and carrying no `error:`
prefix so a throwing lane and a returning lane can both use it.

## Pinned by

- *Where output lands*, ownership: *No test pins the fail-safe direction.*
- *Where output lands*, `into=`: *No test pins the arm order.*
- *Where output lands*, the shadowing stem: `PatchStemShadowTests.AStemThatWouldShadowAnInactivePluginInAForeignModFolderIsRefused`,
  with `…AForeignEsmOfTheSameStemIsNoShadowForTheEspTheLaneWrites` for the boundary.
- *Where output lands*, a caller-named taken stem: `PatchArtifactCollisionTests.Merge_folder_collision_refuses_by_name_and_writes_nothing`
  and `…Repack_folder_collision_refuses_by_name_rather_than_renaming_the_archive`, both of which name the stem.
- *Where output lands*, result artifacts: `RecordsArtifactTests.ToFileIntoTheServersResultsDirectoryIsRefusedNamingThePruneHazard`.
- *Where output lands*, the two dispositions: `RecordsTransportTests.ToFile_TheArtifactIsWrittenAndTheResponseIsManifestOnlyInline`,
  with `RecordsArtifactTests.ToFileJson_TheRowsAreOmittedWhileTheTrueTotalStaysIntact` for the json twin; and
  `RecordsArtifactTests.AnAutoSpillAnnouncesTheCompleteResultWithItsRowCountNotTheRenderedPrefix` and
  `…AWindowedAutoSpillSaysWindowAndNeverClaimsTheCompleteResult` for the `ceiling` auto-spill.
- *The reservation is the file*: the claims and their pins, three in `RecordsArtifactResultsStoreTests` and two in
  `RecordsArtifactTests`:

| contract | pinned by |
|---|---|
| two same-second reservations get distinct paths, because reserving creates the file | `RecordsArtifactResultsStoreTests.SameSecondReservationsGetDistinctNamesBecauseReservingCreatesTheFile` |
| the exclusive handle stays open across the write | `RecordsArtifactResultsStoreTests.NothingElseCanOpenAReservedFileWhileTheSpillIsBeingWritten` |
| disposing a reservation nothing wrote deletes the file it owns | `RecordsArtifactResultsStoreTests.AReservationNoSpillWroteIsDeletedWhenItIsDisposed` |
| a spill still lands while a scanner holds every new file without share-delete (#766) | `RecordsArtifactTests.AnAutoSpillLandsWhileAScannerGrabsEveryFileTheResultsDirectoryGains` |
| a failed write to a caller-named target leaves that file as it was | `RecordsArtifactTests.ToFile_AWriteThatFailsAfterItStartedLeavesTheCallersFileAsItWas` |

- *Re-entering an artifact*: `RecordsArtifactEpochTests.StaleReEntry_TheBodyLaneRefusalNamesBothEpochsAndTheNoOverridePosture`
  — the epoch mismatch refuses naming both epochs, with no override. `RecordsArtifactTests.AnArtifactDeclaringNoIdentityColumnRefusesReEntryByName`
  — an artifact with no identity column refuses re-entry by name. No test pins the refusal of an artifact whose
  identity column is not the one the parameter takes.
- *Re-entering an artifact*, error rows: pinned across `RecordsArtifactTests`' re-entry facts —
  `AMixedArtifactReEntersOnItsResolvedRowsWithNoWasItEditedMisdiagnosis` (error rows are skipped) and
  `AnAllErrorArtifactIsRefusedByItsRealCauseNeverByAccusingTheFile` (an all-error artifact refuses by its real cause).
- *The atomic-write contract*: what `AtomicCommitGuardTests` pins is that the code takes
  `File.Replace`'s path rather than `File.Move`'s — except on a host whose filesystem tunneling masks creation time,
  where that test stops at its control — that a fresh target still lands, and that a pre- or mid-swap
  failure throws with the prior target byte-for-byte intact.
- *The freshness stamp: last write plus size*: pinned by `FreshnessKeyTests` — an edit that leaves the mtime alone is
  still stale, the shared stamp separates two files that differ only in length, and one sentinel stands for a path
  that cannot be statted.
- *The order stamp: epoch plus health*: pinned by `DegradedOrderMarkerTests` —
  `ADegradedBuildsJsonResponseCarriesTheMarkerAndNamesWhatIsMissing` and
  `ADegradedBuildsTextHeadCarriesTheClauseBesideTheEpoch` for the marker riding beside the epoch on both transports,
  `HealthyOrderMarkerTests.AHealthyBuildCarriesNoMarkerOnEitherLane` for the silence on a healthy build — and by
  `AssetStatusSetTests.TheDegradedOrderRosterSurvivesTheArtifactRoundTrip` for the roster travelling with an
  artifact.
- *The absolute-path rule*: pinned per lane by `AbsolutePathArgumentTests`.

## Where

`src/housecarl-mcp/OutputLocations.cs` holds the output folders, the ownership marker, `into=`, the stem suffix and
`out_path=`; `src/housecarl-mcp/Artifacts.cs` is what a response says when its result lives in an artifact;
`src/housecarl-mcp/ResultsStore.cs` is the server-managed results directory, the reservation and the prune.
`src/housecarl-core/ResultArtifact.cs` is the artifact, its manifest, and the identity read re-entry takes
(`ReadIdentity`); `AtomicFile.cs` is `AtomicFile.Commit`;
`FileStamp.cs` and `OrderStamp.cs` are the two stamps; `PathArguments.cs` is `PathArguments.NotAbsolute`. Entry
points: the output lanes of the write tools (`patch=`, `into=`, `out_path=`), and `to_file=` and the auto-spill on
the tools that take them.

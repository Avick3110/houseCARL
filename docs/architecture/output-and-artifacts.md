# Output locations and result artifacts: where a write lands, and the contracts that hold it

**Class:** LIVING. Subsystem: `src/housecarl-mcp/{OutputLocations, Artifacts, ResultsStore}.cs`,
`src/housecarl-core/{ResultArtifact, AtomicFile, FileStamp, OrderStamp, PathArguments}.cs`.

Everything houseCARL writes lands in one of three places: a houseCARL-owned MO2 mod folder, a folder the caller
named outright, or a result artifact file. This note is the home of the contracts those three share, cited from
the files above under ADR 0001.

## Where output lands

**A houseCARL-owned mod folder, by default.** Plugins and every non-`.esp` rider — compiled scripts, a packed
`.bsa`, extracted loose files, a generated `.seq` — go into `<ModsDir>\houseCARL - <stem>`. The prefix groups the
patches in MO2's left pane and is the human-visible ownership signal; the structural one is
`[houseCARL] generated=true` in the folder's `meta.ini`, the one mod-root file MO2 does not deploy into the game
Data folder. The check is fail-safe: a missing or stripped marker reads as NOT owned, and houseCARL refuses the
folder rather than risk touching a user mod.

**`into=` extends an owned folder**, through one resolver shared by the `.esp`, rider and asset lanes, so "extend
my renamed patch" behaves identically everywhere. Four arms in order: the canonical `houseCARL - <stem>`; the
owned folder holding `<stem>.esp`, for a renamed folder — the `.esp` basename is fixed by whatever binds the patch
(SPID files, config JSON, masters) while the folder name is the user's to rename; the folder's own name; then a
refusal naming every place searched, which distinguishes a foreign un-owned collision from a genuine miss and
offers the owned patches it can actually route back to. Every arm is ownership-gated. `needEsp` tightens the
canonical arm for the record lane, which needs the plugin present; a rider targets the folder itself.

**A fresh stem is auto-suffixed** to `<stem>_NNN` when either a mod folder of that name exists or an ACTIVE
plugin is named `<stem>.esp` — the engine forbids two active plugins sharing a basename, and folder uniqueness
alone never sees a same-named plugin in another mod. Two kinds of lane REFUSE a taken stem instead of suffixing,
because their artifact's exact basename is load-bearing: the `.bsa`, which the game auto-loads only under its
plugin's basename, and the merged plugin (`StemRefusal`). A plugin file that the active order is not loading is
invisible to both tests, so the file the claiming lane will write also goes through `PatchStemShadow`, which
refuses rather than suffixing around a foreign inactive plugin (#561).

**`out_path=` is the escape hatch.** The caller names a mod-folder ROOT and houseCARL appends the artifact's
subfolder (`Scripts\`, `SEQ\`), matching MO2's deploy model so the file actually loads; a root already ending in
that segment is taken as-is rather than doubled. The folder is the user's, so `CreatedFresh=false`: no houseCARL
folder is cut and residue cleanup never deletes it. `into=`'s ownership check is deliberately untouched on these
lanes — letting `into=` name a folder houseCARL did not create would put the patch-folder machinery inside a third
party's mod, whereas `out_path=` only adds a sidecar file to a folder the user owns.

**Deployability is a shape rule**, shared by every `out_path=` lane. MO2 overlays a mod folder's CONTENTS onto the
Data root, so the served shapes are exactly `<mods>\<modFolder>\<sub>`, `<overwriteDir>\<sub>` and
`<data>\<sub>`. A bare `<mods>\<sub>` has no mod folder, and a nested `<mods>\X\Sub\<sub>` lands at `Data\Sub\…`,
so neither loads and both warn. The rule is shared; the sentence naming what a mis-placed artifact costs stays
with each caller, because the stakes differ — a `.pex` that does not deploy leaves the old behaviour, while a
`.seq` the engine never reads leaves every start-game-enabled quest in its plugin silently not starting.

**A rider that fails after cutting a fresh folder cleans up after itself**: a folder holding nothing but our own
`meta.ini` is deleted, so "no output written" is true of the disk; a folder holding real output stays and its path
is named, because houseCARL never deletes content it did not recognise as its own. A reused `into=` folder is
never touched.

**Result artifacts** land in one of two places. An auto-spill (the inline render hit `max_chars`) goes to the
server-managed results directory — `HOUSECARL_DATA_DIR`, else the server binary's folder, plus `results\` — which
is pruned by age after `ResultsStore.PruneAfterDays` days. A caller-named `to_file=` target never comes here, and
one pointing INTO this directory is refused by name: the prune is hygiene the server owns, and it would silently
delete the caller's artifact.

## The reservation is the file

An auto-spill claims its path by CREATING the file with `FileMode.CreateNew` and keeping the exclusive handle
OPEN; the artifact is then written through that same handle. Probing with `File.Exists` first would hand two
same-second parallel tool calls the same path, and a temp-then-move onto an already-reserved name fails while
anything holds the destination without share-delete (#766). Disposing a reservation that was never written closes
the handle and deletes the file it owns, best-effort, so a cancelled call strands nothing. A crash mid-write
cannot pass a half artifact off as a whole one: the manifest is line 1 and carries `row_count`, so a short file
fails its own manifest. A target is single-use — a reservation's handle is closed by the write, so a second write
would fail against a dead stream and take the landed artifact with it. Pinned by
`RecordsArtifactTests.AnAutoSpillLandsWhileAScannerGrabsEveryFileTheResultsDirectoryGains`.

A caller-named target carries the opposite hazard: it is the CALLER's file and may already hold an artifact they
still want, so it is written through a same-directory temp moved into place, and a failure anywhere — or a process
kill — leaves the destination untouched. There is no empty placeholder at the destination for a scanner to hold,
so the move is not the #766 hazard. Pinned by
`RecordsArtifactTests.ToFile_AWriteThatFailsAfterItStartedLeavesTheCallersFileAsItWas`.

An artifact is immutable once written: the server never appends to or mutates one. A re-run writes a new file, or
overwrites a caller-named target wholesale.

## Re-entering an artifact

An `@<path>` list input whose target is an artifact yields that artifact's IDENTITY column as the list — scan once,
project forever — and server-side consumption is EPOCH-CHECKED against the current build: a mismatch is a loud
refusal naming both epochs, with deliberately NO stale-override parameter. Fresh re-projection goes through the
server; honest-snapshot traversal of the file is the client's own lane, which the server cannot and should not
police. The check happens after the consuming call's own capture, inside the service, so the check and the answer
read the same build — a tool-layer pre-check would race a freshness rebuild.

**Error rows are not identity-bearing.** A row carrying an `error` member documents a failure — a malformed input
token, an absent record — and does not name a record; the resolve and batch lanes write the caller's raw token (or
the null FormKey) into such rows, so their identity values are legitimately not FormIDs. Extraction SKIPS them by
contract: re-entering an artifact means "the records this file resolved", and treating a failure row's raw token as
a record identity is how a reconciliation subtraction goes wrong. An all-error artifact refuses by its real cause,
never by accusing the file. The "was it edited?" refusal is reserved for a genuine mismatch — a SUCCESS row without
the identity column — which a server-written artifact never contains. An artifact whose identity column is not the
one the parameter takes is refused by name rather than read as the wrong kind of token, and an identity that is not
a FormID is not epoch-checked at all, because it names no record that could go stale. Pinned across
`RecordsArtifactTests`' re-entry facts.

## The atomic-write contract

`AtomicFile.Commit` is the one primitive every houseCARL FINAL swap funnels through. The caller stages a complete
file into a temp on the SAME volume as the final path, then hands it here:

- target EXISTS → `File.Replace` (Win32 `ReplaceFile`): an atomic content swap that keeps the destination's
  on-disk identity (its NTFS file record) and its creation time. A crash mid-commit leaves either the old
  complete file or the new complete one.
- target ABSENT → `File.Move` onto a free name, itself atomic. `File.Replace` requires an existing destination and
  throws without one, so the fresh-file case is served by a rename.

`File.Move(overwrite: true)` is not a substitute: `MoveFileEx MOVEFILE_REPLACE_EXISTING` can unlink the
destination BEFORE the rename commits, discards the destination's identity, and resets its creation time. The
same-volume staging is the caller's invariant, and the `FileNotFoundException`-ONLY catch is what keeps it honest:
a cross-volume swap surfaces as `IOException` and an EFS-encrypted or specially-ACL'd target as a metadata-merge
error, and both must stay loud with the original byte-intact rather than degrade into a non-atomic copy. The
3-argument overload is deliberate — the 4-argument `ignoreMetadataErrors: true` would swallow that failure.
`AtomicFile` holds no handle at rest.

Crash-atomicity itself is not demonstrable in one process and is not claimed. What the `atomic-commit-guard` probe
(`src/housecarl-generator/AtomicCommitProbe.cs`) pins is that the code takes `File.Replace`'s path rather than
`File.Move`'s, that a fresh target still lands, and that a pre- or mid-swap failure throws with the prior target
byte-for-byte intact. Fresh-CREATE writes deliberately do not funnel through it: there is no original to lose.

## The freshness stamp: last write plus size

`FileStamp` is one filesystem entry's last-write time AND its length, and it is THE key every houseCARL cache
stamps against — the plugin read cache, the MO2 profile gate, the BSA and loose-subtree tables, the SkyPatcher INI
parse cache — so there is one answer to "has this changed" rather than one per cache. Length is in the key because
last-write alone is coarse: an edit landing inside the filesystem's timestamp granularity, or one whose tool
restores the timestamp it found, leaves the mtime where it was and serves stale state with nothing saying so
(#406). Two terms do not make the key exact — an edit that changes neither is still invisible — they make the
common same-mtime edit visible. Both terms come from ONE stat, so the no-change path costs what the mtime-only
path cost. `FileStamp.Absent`, with its negative length, is the single sentinel for missing, locked and
unreadable, so a path that comes back is a change and one that stays gone is not. Pinned by `FreshnessKeyTests`.

`OrderStamp` carries an index build's epoch AND the plugins that build lost to a load failure, as one value. A
plugin that becomes unopenable mid-session drops out of the next build and every read after it answers off the
narrowed order; the epoch changes, but a legitimate reorder changes it too, so the epoch alone cannot tell a
degraded order from a reordered one (#353). The two facts travel together rather than being re-derived through a
side table, because a table lookup can MISS — which renders as a clean bill of health, the silence the marker
exists to end — and because an outcome then cannot hold an epoch without holding its health. The health is a
SIBLING of the epoch, never inside it: the epoch is opaque and compared only for equality, so folding health into
the string would leave two builds that differ only in health comparing as merely different.

## The absolute-path rule

Every path a CALLER names — an `out_path=` folder, a `to_file=` artifact, an `@file` list, a draft INI — must be
FULLY QUALIFIED, not merely rooted. The server's working directory is not the caller's, so a path that is not
absolute resolves somewhere neither of them meant while the response names the path that was typed; `C:work` and
`\work` are rooted and still resolve against the server's own directory. There is one definition,
`PathArguments.NotAbsolute`, and it lives in core because the predicate and draft readers are there — one
definition for the whole surface, not one per assembly. It carries no `error:` prefix, so a lane that throws and a
lane that returns a string can both use it. Pinned per lane by `AbsolutePathArgumentTests`.

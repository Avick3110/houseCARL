---
updated: 2026-11-13
covers: [src/housecarl-core/Mo2Instance.cs, src/housecarl-core/Mo2LoadOrder.cs, src/housecarl-core/QtIniEscapes.cs, src/housecarl-core/Mo2ModMeta.cs, src/housecarl-core/UserConfig.cs, src/housecarl-mcp/SetupTools.cs, src/housecarl-mcp/StatusTools.cs, src/housecarl-mcp/UpdateStatusTools.cs]
---
# The MO2 instance

## What it is

houseCARL reads a live MO2 portable instance off disk — never through the USVFS or a live
`IOrganizer`. A subprocess-spawned server does not inherit MO2's VFS (only MO2's own Executables
launch does, and that locks MO2), so the instance's ini and the three profile text files are the
only standalone source of truth.

## Contracts

### The priority model

**Overwrite beats every mod; a higher-priority enabled mod beats a lower one; the game Data folder
is the floor. First sighting of a filename wins.** That is MO2's own overwrite rule, and it is
stated here once:

1. The overwrite folder (`overwrite_directory`, default `base\overwrite`) — the top of MO2's VFS, where tool outputs land (Synthesis patches, xEdit "new
   file", Wrye Bash). MO2 lists those plugins in the profile files, so skipping this layer leaves
   them unresolvable.
2. Enabled mods under the mods folder (`mod_directory`, default `base\mods`), in `modlist.txt` order — **top = highest priority** — each mod
   root being a Data root, so plugins sit at its top level.
3. `<gamePath>\Data` — vanilla masters and base game, filling only names no mod provided.

`Mo2LoadOrder.Build` walks exactly that sequence for the ACTIVE order (enabled mods only).
`LocatePlugin` and `AllPluginFileNames` walk a longer sequence in the same precedence —
overwrite → enabled → **disabled** → **unlisted** (a folder on disk `modlist.txt` mentions in
neither list, the state of a patch houseCARL just wrote) → Data — and return *all* hits rather than
the first, so a filename several folders provide is reported instead of silently picked. The two
share one `CandidateFolders` sequence: a "not found" and the "did you mean" that follows it must be
drawn from the same set of places. `ServingCopy` walks the same sequence with the disabled and unlisted
layers skipped and stops at the first hit: the copy the game loads, for the serving-copy check.

A plugin the load order lists that no searched place provides goes into `Warnings`, never a silent
drop, and the warning names only the places actually searched.

### A folder that will not open

A folder that is not there and a folder that is there but will not open are two facts (#1385).
A folder that stats but will not list (a junction whose target is gone, a folder that denies listing) used to read as
empty, so its plugins went missing under the stale-profile warning. The listings of the mods folder itself and of
each enabled mod's folder in `Build`, and of every folder `AllPluginFileNames` walks, now throw rather than skip
(`IgnoreInaccessible = false`; hidden and system files are still skipped), and an `IOException` or
`UnauthorizedAccessException` marks the folder unopened. `Build`'s overwrite and game Data listings still read a
folder that will not open as empty. A folder that does not stat (removed, or on a drive that is offline) is missing
and still silent, since a `modlist.txt` entry can lack a real folder; a mods folder that does not stat reads as an
empty one, as before.

- **The whole mods folder.** `Mo2LoadOrder.ModsFolderFailure` reads one entry of the mods folder. When it throws,
  `Build` resolves the order from the folders outside it only (overwrite, when given, and Data), adds no per-plugin
  warning, and throws
  `ModsFolderUnreadableException` carrying that order: its message names the folder, the reason, and how many of the
  active plugins resolved. Every order read refuses with it (the record index, a call's first file read, a write's
  output roots), and the service asks again on each of those reads, so a mods folder that stops opening after the
  build refuses the next call. `load_order_status` catches it and answers off the order it carries, leading with
  the sentence and printing no containment line; with `filter=` it says where the plugin is served from is unknown
  and reads no copies. `set_mo2_instance` says the folder will not open, that those calls refuse, and that the
  order will not resolve until it opens. The judgement is the mods folder's own listing, so a mods folder that will
  not list is refused whole even when each enabled mod's folder opens by name.
- **Some mod folders.** An enabled mod whose folder will not list goes on `Mo2OrderResult.UnreadableMods` and the
  order answers without it. `Guard.Render` puts `UnreadableNote` (the mods named, resolved of active) at the head of
  every answer whose call read that order: the first line of text, or `order_note` as a JSON object's first member,
  on the outermost call only. In the order's warnings the folders are named ahead of the plugins they explain, and a
  plugin no searched place provides is told it may be in one of them, the stale profile second. The per-call
  freshness check re-reads the order when an unopened folder opens again, or when a folder holding a plugin the
  index could not reopen stops opening, so the note follows the disk without a profile change.
- **Where a file is looked for.** With no copy found, an answer asks which folders would not open before it calls a
  file absent: the plugin locate and the status `served from:` and `localized:` lines ask `UnopenedModFolders` over
  every mod the profile lists, `asset_status provider=` over the enabled ones, and the absence explainer reads the
  order's `UnreadableMods`. Each says the file may be in the folders named, not that it does not exist or the name
  is wrong. `AllPluginFileNames` names every folder it could not list (overwrite, Data, and enabled, disabled or
  unlisted mods), so a master it did not find is "not in any folder houseCARL could open", with "N folder(s) could
  not be opened" naming them, in the check text and its `to_file=` fix column, never "not installed". The BSA
  scan's own twin is in [`assets.md`](assets.md).

### The status verdict for one plugin

`housecarl_load_order_status filter=<plugin>` prints every copy `LocatePlugin` finds, each with its
layer label and size. `OutputLocations.JudgeServed`, the one judgement every plugin locate uses,
marks each copy: the first hit from an enabled layer serves (the rule `Build` resolves the order
by), and an enabled copy below it is shadowed. The locate runs over the composition and roots the verdict
captured in one gate hold, so the copies are searched in the profile's own instance. A dummy
copy serving the name while the real file sits in a disabled folder is then visible, not mistaken
for a read bug (#1001).

The header facts come from one read of the served copy, `PluginFile.ReadHeader`: the master, ESL
and localized flags, the masters in order, and HEDR's record count. `WriteEngine.PluginIsLocalized`
is that same reader, so the verdict and the in-place write refusal cannot disagree on the localized
flag. The master and ESL flags are the header bits; where the bit is clear but a `.esm` or `.esl`
extension makes the plugin a master or light anyway, the line says so. A header that will not open prints the localized line as UNKNOWN and no other header line.
With no copy serving, a lone copy's header is still read and the header line names that copy as not
served; with several, or none, no header is read and the localized line says so. Only `filter=<plugin>` reads these; a mod
name and the whole-profile summary do not.

### Deriving the roots from one path

The user configures a single "where is your MO2?" path — the instance folder holding
`ModOrganizer.ini`. Everything else is derived, and the active profile is auto-detected from
`selected_profile`, so no profile name is ever hand-typed and a profile switch is picked up by
re-reading that file (the service's freshness check).

The roots follow MO2's own `PathSettings` (v2.5.2 `getConfigurablePath`), with no fallback anywhere:

- `base` is `[Settings] base_directory` when the key is present (even when its folder is missing), otherwise the
  instance folder, the ini's own folder.
- Each of the mods, profiles and overwrite roots is its `[Settings]` key (`mod_directory`, `profiles_directory`,
  `overwrite_directory`) when present, otherwise `base\mods`, `base\profiles`, `base\overwrite`. Every `%BASE_DIR%`
  in a key's value (case-sensitive, as MO2's replace is) becomes `base`. A key's value is read through the Qt reader
  like any other, then normalised with `Path.GetFullPath`; an unset key keeps the `Path.Combine` text main always
  printed. `ProfileDir` is the profiles root plus `<profile>`; `DataDir = <gamePath>\Data`.
- The five keys are read from `[Settings]` only; `selected_profile` and `gamePath` stay section-blind.
- A value that resolves to a path that is not fully qualified (an empty or relative key, an empty or relative
  `base_directory` that a root resolves under) refuses, naming the key once: MO2 would read it against the folder it
  was started from, which houseCARL cannot see. An instance folder the caller gave as a relative path is still read
  as given.
- A missing mods or profile folder is NAMED in the problem list, with where its root came from (the key, or the
  default under `base_directory` or the instance folder), and the resolve FAILS — never a half-derived path set. A
  `base_directory` whose folder is missing therefore refuses through each root under it, and refuses nothing when
  all three keys point elsewhere.
- Overwrite never gates validity: MO2 creates it at every start, so a missing one means MO2 has not started since
  it went, and it reads as empty, the same answer MO2 would give.

Not read: `download_directory` and `cache_directory`, since houseCARL reads neither folder. A mods folder set inside
the overwrite folder labels its mods "overwrite" (the layer test checks overwrite first).

### The profile files

- `loadorder.txt` — every plugin in load order, masters first, **winner last**.
- `modlist.txt` — mod priority, `+`enabled / `-`disabled / `#`comment, `…_separator` entries
  skipped from both lists.
- `plugins.txt` — the `*` active flag. Listed without a `*` = present but unchecked, and dropped
  from the order. Absent from the file entirely = an implicit force-loaded base/CC master.

MO2 writes `plugins.txt` in the system ANSI code page (`QStringEncoder(System)`), so it is read in
that code page unless its bytes are valid UTF-8, which is kept so a file that read before still does;
a leading UTF-8 BOM is skipped before that choice, a UTF-16 or UTF-32 BOM names the encoding, and
where the system page is UTF-8 (65001, which .NET's code-page provider leaves to the built-in UTF-8)
the file is read as UTF-8. The one miss: an ANSI file whose every non-ASCII byte happens to form
valid UTF-8 (cp1252 `Ã©`, bytes `C3 A9`) is read as UTF-8, so that name matches nothing in
`loadorder.txt`. `loadorder.txt` (MO2 writes it as UTF-8) and `modlist.txt` are read as UTF-8.

MO2 holds `loadorder.txt` and `plugins.txt` open while it re-sorts, so a read landing in that
window is a transient, not a failure: only the Win32 sharing and lock violations become
`ProfileUnreadableException`, one sentence saying to retry. Every other read fault keeps its own
error, because telling a user to wait for a re-sort that is not happening is the silently-wrong
answer that type exists to avoid.

### The Qt/QSettings value grammar

`ModOrganizer.ini` and a mod's `meta.ini` are both QSettings files, and both readers go through
`QtIniEscapes.Clean` so they cannot behave differently. The two skip lists (`skip_file_suffixes`,
`skip_directories`) are the exception: `Mo2SkipRules.ParseList` reads them raw, split on commas, so
an item Qt wrote quoted or escaped is matched as written. One value is read in Qt's own order: trim
the raw line; take off a surrounding pair of double quotes FIRST (Qt quotes the whole serialized
string, `@ByteArray(` prefix included, when the value holds `; , =` or an edge space); unwrap
`@ByteArray(...)`, with `@Invalid()` meaning unset; undo the escaping.

Qt writes any byte outside printable ASCII as `\xHH`, so a CJK profile name is a run of hex
escapes. Those runs are read GREEDILY, which is safe because Qt escapes a hex digit that follows a
`\xHH` too; a run of byte-sized values decodes as UTF-8, and a value above `0xFF` is a single
UTF-16 code unit. The named escapes are `\\ \" \; \, \= \0 \a \b \f \n \r \t \v`.

Qt doubles every backslash, so a Qt-written value never carries a lone one. A value that does was
not written by Qt (hand-edited, or an installer writing the file itself) and its backslashes are
literal path separators — the whole value is then left exactly as it stands. That is decided once
per value, on whether *every* backslash in it begins a known escape, never per escape, so
`D:\newgame` cannot lose its `n`.

`Mo2ModMeta` reads that file's `[General]` Nexus cache fields raw (`modid`, `version`,
`newestVersion`, `ignoredVersion`, `lastNexusUpdate`) and lets the caller apply MO2's own
is-there-an-update rule, so the reasoning stays visible instead of collapsing into a hidden
boolean. `[installedFiles]` uses prefixed keys (`1\fileid=…`), which is the join key for a
FILE-level currency check; a FOMOD or manual install has `size=0` and no fileid, and the check says
so rather than guessing.

### houseCARL's own config file

`houseCARL.user.json` carries four independent concerns — the MO2 instance folder, external tool
paths, in-place write acknowledgements, and named Papyrus import sets. They MUST coexist, so the
only writer is `UserConfigStore.Update`: a read-modify-write, never a whole-object overwrite. It
runs under a process-local gate plus a named mutex derived from the file path, because the CLI
plugin and the desktop app can share the file; it commits through a temp file and an atomic rename;
and an unparseable file is copied to `.corrupt.bak` and REPORTED before the read proceeds as blank
— a later write starts from that blank, so the backup is the only copy of the old settings. A write
failure is returned rather than thrown, so a tool can say the choice works this session but will
not survive a restart.

That last point is a sharp edge, not a nicety: a corrupt config followed by one
`housecarl_set_tool_path` leaves the live file holding that one field and nothing else, and every
other saved setting is recoverable only from `.corrupt.bak`. The loudness is what makes it
survivable — `Load` and `Update` both return the note naming the backup — so a caller that
swallows the note turns a recoverable state into a silent loss.

## Pinned by

- *The priority model*: `OverwriteFolderResolveTests` — a plugin in overwrite resolves
  there and beats the same filename in an enabled mod; a genuinely missing plugin warns, naming only the places
  searched.
- *The priority model*: `MasterSplitInstallLocationsTests.TheSplitFilesEachMasterByWhichInstallLayerHoldsItsFile` —
  `AllPluginFileNames` walks all five layers. `StatusPluginFactsTests` drives `LocatePlugin` through the status filter;
  that the two draw from the same places rests on their shared `CandidateFolders`, not on an assertion.
- *The status verdict for one plugin*: `StatusPluginFactsTests` — a dummy copy in an enabled folder is named as
  serving and the real copy in a disabled folder beside it, both with sizes; of two enabled copies the lower is
  shadowed; game Data serves when a disabled mod's copy is found first; the ESL flag, masters and record count
  match the fixture's raw TES4 header, and a `.esl` with clear bits says it loads light by extension; a lone
  disabled copy's header is named as from that copy, several disabled copies read none, and a ticked plugin missing
  from loadorder.txt still gets its facts. `StatusLocalizedLookupTests` — the localized line from the same read.
- *A folder that will not open*: `ModsFolderUnreadableTests` — the whole mods folder: status leads with it, records,
  the tree, the sweep, a check, an asset read and `write_seq` refuse, `set_mo2_instance` names it, and a folder that
  stops opening after the build refuses the next read; one mod folder: status leads with it, a record read, a sweep,
  a write plan and a JSON answer carry the note, the lost plugin, its master, its status and an archive provider in
  it are not called stale, mistyped or uninstalled, and the next call follows the folder opening again or going.
- *Deriving the roots from one path*: `Mo2InstanceResolveTests` — the roots and the active profile
  derived from the instance folder, the `base_directory` override, a missing `base_directory` refused, and a missing
  required piece named rather than half-derived. `Mo2DirectoryKeysTests` — the three keys move their folders past a
  stale copy under the instance, `%BASE_DIR%` expands to either base and not in lower case, escaped and quoted values,
  an empty or relative value or base refused once naming it, a relative instance folder read as given, a missing base
  nothing resolves under, a missing folder from a key named, a missing overwrite not refused, a key outside
  `[Settings]` ignored, and the service's profile list, a new patch folder and an edited key following the moved
  folders.
- *The profile files*: `ProfileRewriteTests.AColdAssetCallOnAHeldProfileNamesTheHoldInsteadOfAnInternalFailure` — a
  held profile file is a named transient, not an internal failure.
- *The profile files*: `PluginsTxtEncodingTests` — `plugins.txt` in the host's ANSI page and in UTF-8 keeps its ticks
  through `ReadComposition` (`HostAnsiPluginsTxtKeepsTicks`, `Utf8PluginsTxtKeepsTicks`), a cp1251 file through
  `ParsePlugins` (`Cp1251PluginsTxtKeepsTicks`), and a UTF-8 BOM ahead of ANSI bytes keeps the ANSI page
  (`BomBeforeAnsiBytesKeepsAnsi`).
- *The Qt/QSettings value grammar*: `Mo2IniEscapeTests` — the quoted wrapper, the greedy hex runs, the named escapes,
  and a value with a lone backslash left as it stands (`HandWrittenPathsAreLeftAlone`).
- *The Qt/QSettings value grammar*: `Mo2ModMetaReadTests` — the `[General]` Nexus cache fields read
  raw, and the `[installedFiles]` file ids.
- *houseCARL's own config file*: all four are pinned. `UserConfigStoreClobberTests` writes a corrupt file and
  asserts the load comes back blank with a note naming the backup, that the backup is byte-identical, and that two
  `UserConfigStore` instances on one file — each with its own process-local gate, the way the two hosts share it —
  hammering different fields for 200 rounds each leave BOTH concerns' last values intact. The commit itself is
  `AtomicFile.Commit`, pinned by `AtomicCommitGuardTests`, one of whose three call sites is `UserConfig.Update`;
  those tests prove the `File.Replace` path is taken, not
  crash-atomicity across a power cut, which is not demonstrable in-process and is not claimed.

## Where

`src/housecarl-core/Mo2Instance.cs` derives the roots and the active profile (`Resolve`, `ReadSelectedProfile`);
`Mo2LoadOrder.cs` reads the profile files and walks the priority model (`Build`, `LocatePlugin`,
`AllPluginFileNames`, `CandidateFolders`); `QtIniEscapes.cs` is the one Qt value reader (`Clean`); `Mo2ModMeta.cs`
reads a mod's `meta.ini`; `UserConfig.cs` is `houseCARL.user.json` and `UserConfigStore`. The tool fronts are
`src/housecarl-mcp/SetupTools.cs`, `StatusTools.cs` and `UpdateStatusTools.cs`. Tools: `housecarl_set_mo2_instance`,
`housecarl_set_tool_path`, `housecarl_load_order_status`, `housecarl_update_status`.

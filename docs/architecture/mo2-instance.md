---
updated: 2026-09-18
covers: [src/housecarl-core/Mo2Instance.cs, src/housecarl-core/Mo2LoadOrder.cs, src/housecarl-core/QtIniEscapes.cs, src/housecarl-core/Mo2ModMeta.cs, src/housecarl-core/UserConfig.cs, src/housecarl-mcp/SetupTools.cs, src/housecarl-mcp/StatusTools.cs, src/housecarl-mcp/UpdateStatusTools.cs]
---
# The MO2 instance

**Class:** LIVING. Subsystem: `src/housecarl-core/Mo2Instance.cs` (one path → the load-order roots),
`Mo2LoadOrder.cs` (the active order and the file-priority model), `QtIniEscapes.cs` (the
QSettings value grammar both ini readers share), `Mo2ModMeta.cs` (a mod's Nexus update cache), and
`UserConfig.cs` (the settings houseCARL persists for itself), surfaced by
`src/housecarl-mcp/SetupTools.cs`, `StatusTools.cs` and `UpdateStatusTools.cs`. Pinned by the
generator probes `mo2instance-probe` (the root derivation), `overwrite-resolve-guard` (the priority
model), `mo2-modmeta-guard` (the `meta.ini` read), `tool-bridge` (the config file) and
`atomic-commit-guard` (its commit), and by `Mo2IniEscapeTests`, `ProfileRewriteTests` and
`MasterSplitInstallLocationsTests` in `src/housecarl-mcp-tests`.

houseCARL reads a live MO2 portable instance off disk — never through the USVFS or a live
`IOrganizer`. A subprocess-spawned server does not inherit MO2's VFS (only MO2's own Executables
launch does, and that locks MO2), so the instance's ini and the three profile text files are the
only standalone source of truth.

## The priority model

**Overwrite beats every mod; a higher-priority enabled mod beats a lower one; the game Data folder
is the floor. First sighting of a filename wins.** That is MO2's own overwrite rule, and it is
stated here once:

1. `base\overwrite` — the top of MO2's VFS, where tool outputs land (Synthesis patches, xEdit "new
   file", Wrye Bash). MO2 lists those plugins in the profile files, so skipping this layer leaves
   them unresolvable.
2. Enabled mods under `base\mods`, in `modlist.txt` order — **top = highest priority** — each mod
   root being a Data root, so plugins sit at its top level.
3. `<gamePath>\Data` — vanilla masters and base game, filling only names no mod provided.

`Mo2LoadOrder.Build` walks exactly that sequence for the ACTIVE order (enabled mods only).
`LocatePlugin` and `AllPluginFileNames` walk a longer sequence in the same precedence —
overwrite → enabled → **disabled** → **unlisted** (a folder on disk `modlist.txt` mentions in
neither list, the state of a patch houseCARL just wrote) → Data — and return *all* hits rather than
the first, so a filename several folders provide is reported instead of silently picked. The two
share one `CandidateFolders` sequence: a "not found" and the "did you mean" that follows it must be
drawn from the same set of places.

A plugin the load order lists that no searched place provides goes into `Warnings`, never a silent
drop, and the warning names only the places actually searched.

## Deriving the roots from one path

The user configures a single "where is your MO2?" path — the instance folder holding
`ModOrganizer.ini`. Everything else is derived, and the active profile is auto-detected from
`selected_profile`, so no profile name is ever hand-typed and a profile switch is picked up by
re-reading that file (the service's freshness check).

`base` is `[Settings] base_directory` when it is set and real, otherwise the instance folder (the
common portable case). Then `ModsDir = base\mods`, `ProfileDir = base\profiles\<profile>`,
`DataDir = <gamePath>\Data`, `OverwriteDir = base\overwrite`. A missing or empty required piece is
NAMED in the problem list and the resolve FAILS — never a half-derived path set. Overwrite is the
one exception: it is derived like the others but never gates validity, because MO2 does not create
it until a tool writes there.

## The profile files

- `loadorder.txt` — every plugin in load order, masters first, **winner last**.
- `modlist.txt` — mod priority, `+`enabled / `-`disabled / `#`comment, `…_separator` entries
  skipped from both lists.
- `plugins.txt` — the `*` active flag. Listed without a `*` = present but unchecked, and dropped
  from the order. Absent from the file entirely = an implicit force-loaded base/CC master.

MO2 holds `loadorder.txt` and `plugins.txt` open while it re-sorts, so a read landing in that
window is a transient, not a failure: only the Win32 sharing and lock violations become
`ProfileUnreadableException`, one sentence saying to retry. Every other read fault keeps its own
error, because telling a user to wait for a re-sort that is not happening is the silently-wrong
answer that type exists to avoid.

## The Qt/QSettings value grammar

`ModOrganizer.ini` and a mod's `meta.ini` are both QSettings files, and both readers go through
`QtIniEscapes.Clean` so they cannot behave differently. One value is read in Qt's own order: trim
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

## houseCARL's own config file

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

All four are pinned. `tool-bridge` writes a corrupt file and asserts the load comes back blank with
a note naming the backup, that the backup is byte-identical, and that two `UserConfigStore`
instances on one file — each with its own process-local gate, the way the two hosts share it —
hammering different fields for 200 rounds each leave BOTH concerns' last values intact. The commit
itself is `AtomicFile.Commit`, pinned by `atomic-commit-guard`, which names `UserConfig.Update` as
one of its three call sites; that guard is explicit that it proves the `File.Replace` path is taken,
not crash-atomicity across a power cut, which is not demonstrable in-process and is not claimed.

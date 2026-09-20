---
updated: 2026-09-18
covers: [src/housecarl-core/SksePluginReader.cs, src/housecarl-core/SksePeek.cs, src/housecarl-core/SkseConfigReferenceExtractor.cs, src/housecarl-core/NativePairing.cs, src/housecarl-mcp/SkseTools.cs, src/housecarl-mcp/SkseJsonDoc.cs, src/housecarl-mcp/AssetLayers.cs]
---
# The SKSE layer: what a DLL declares, and the static-load rule

**Class:** LIVING. Subsystem: `SksePluginReader`, `SksePeek`, `SkseConfigReferenceExtractor`, `NativePairing`
(`src/housecarl-core`); the `housecarl_skse` tool and its three family renders in `SkseTools.cs` plus the shared json
skeleton `SkseJsonDoc.cs` (`src/housecarl-mcp`); the service lanes that assemble the data in `AssetLayers.cs`.
Pinned by `SkseReaderProbe`, `SksePeekProbe`, `SkseConfigAuditProbe` and `NativePairingProbe`
(`src/housecarl-generator`), and by `SkseFamilySelectionTests`, `SkseFindingsWireShapeTests`,
`SkseTransportTests`, `SkseTransportWireTests`, `SkseDirectoryReadTests` and `SkseVersionSourceTests`
(`src/housecarl-mcp-tests`).

## The ceiling

Everything in this layer is what a FILE DECLARES, never what a DLL DOES. A version manifest, an import table, an
embedded string and a config token are static facts about bytes on disk; loading, registering and hooking are runtime
behaviour houseCARL never observes. So a finding is a plausibility verdict to verify, the absence of a token proves
nothing, and "nothing found" is not a clean bill of health. The ceiling is stated once to the caller, in the tool
description, because it is the same ceiling for all three families.

`housecarl_skse` runs ONE family per call — inventory, pairing or config. The three answer different questions over
different populations, so a merged response would have no honest summary line; every response instead names the family
it ran and the spelling of the two it did not.

## The static-load rule

A loose, top-level, x64, readable, version-matching, non-debug DLL is the only kind the SKSE loader loads. Each of the
following is a STATIC reason it will not, and this list is the rule's one home:

| blocker | why the loader refuses it |
|---|---|
| no active provider | nothing in the active order supplies the path at all, so there is no image to load; the chain's first arm |
| BSA-only | the loader scans loose `Data\SKSE\Plugins` only, so an archive-shipped DLL is never opened |
| subfolder | the scan is `SKSE\Plugins\*.dll` top-level only; a nested DLL is parent-loaded or bundled, not loaded |
| 32-bit | an x86 image cannot load in SE/AE |
| unreadable | not a valid PE image, or a corrupt export directory — surfaced, never skipped |
| version-locked mismatch | a plugin declaring no version-independence path loads only on a listed runtime |
| query-only on AE | the AE loader loads only `SKSEPlugin_Version` plugins, so an SE/VR-era query-only plugin is dead on 1.6+ |
| Debug-CRT | a debug-built DLL imports a runtime that ships only with Visual Studio, so the loader fails with error 126 |

Two of these are MACHINE-DEPENDENT and are checked rather than assumed, because houseCARL runs on the modder's own box:
the Debug-CRT blocker is null where the debug runtime resolves here (`SksePluginReader.IsSystemDllResolvable`), and
the version-lock and AE-era arms degrade to "verify" when the installed runtime could not be resolved. A DLL that loads
here and nowhere else is still named as broken for everyone else rather than reported clean.

Absence of evidence is never evidence of absence. `SksePluginInfo.Is64Bit` and `.Imports` are tri-state: a value, a
genuine empty, or `null` for a walk that never happened or failed. A null never becomes a 32-bit claim or an
"imports nothing", and `DebugCrtBlocker` returns null on a failed import walk.

The rule is applied in three shapes, on purpose, and they must agree:

- **the blocker string** — `AssetLayers.LooseDllBlocker` (unreadable, 32-bit, then `SksePluginReader.DebugCrtBlocker`)
  plus the no-provider / BSA-only / subfolder arms in `AssetLayers.cs`, composed into `NativePairedDll.LoadBlocker` /
  `SkseFileEntry.Note`, which the pairing verdict treats as dead;
- **the inventory render arms** — `SkseInventoryWire`'s diagnostic subsets and per-DLL detail block, which report each
  class as its own population with its own count;
- **the pairing fate ladder** — `NativePairingWire.Verdict` / `Judge`, which adds the loader's era rules and the
  version lock on top of the blocker and reduces a class to the best fate among its candidate DLLs.

Consolidating the three into one helper is issue #415. Until then, a new blocker lands in all three or the tools
disagree.

## The PE manifest read

`SksePluginReader` reads `SKSEPlugin_Version`, a data blob the AE loader itself reads without executing the DLL. The
layout is ABI-fixed by the loader and identical in both CommonLib lineages (alandtse/CommonLibVR@ng and
powerof3/CommonLibSSE@dev):

```
0x000 uint32  dataVersion
0x004 uint32  pluginVersion          (REL::Version.pack: maj<<24 | min<<16 | patch<<4 | build)
0x008 char    pluginName[256]
0x108 char    author[256]
0x208 char    supportEmail[252]      <-- 252, NOT 256 (the one non-obvious offset)
0x304 uint32  versionIndependenceEx  (bit0 = NoStructUse)
0x308 uint32  versionIndependence    (bit0 = AddressLibraryPostAE, bit1 = Signatures, bit2 = StructsPost629)
0x30C uint32  compatibleVersions[16] (zero-terminated list of REL::Version.pack values)
0x34C uint32  xseMinimum
```

`supportEmail` being 252 rather than 256 is what puts the two flag words at 0x304 and 0x308; the map is pinned by
`SkseReaderProbe` arms A and F, which fail the instant email is "fixed" to 256 and both flag fields shift. The reader
never guesses a layout — the offsets come from the pinned headers. A real blob is the full 0x350 bytes with a non-zero
`dataVersion`; a short or all-zero one (a forwarded or corrupt export) is named rather than presented as a phantom
`""` v0.0.0 plugin.

The older SE/VR loader instead CALLS an exported `SKSEPlugin_Query` that fills its info at runtime, so a query-only
plugin's metadata is not statically readable at all; it is classified and says so rather than inventing a name. A DLL
under `SKSE\Plugins` with none of the SKSE exports is a bundled dependency, not a plugin.

Kinds are named, never degraded: `Modern` (blob readable), `LegacyQuery` (SE/VR-era, metadata filled at runtime),
`NotSkse` (no SKSE export — a bundled dependency), `Unreadable`. A zero directory Size beside a declared RVA is not
absence: the export walk is bounded by the directory's own counts and reads it, while the import walk has only that
Size as a bound and so answers UNKNOWN instead. Both refuse a partial answer — a short list rendered as a complete
"imports (N): …" would let a dropped `vcruntime140d.dll` read as a clean bill of health.

Three version numbers are in sight and none is the truth about the others: the SKSE manifest's own declaration (what
the author typed, routinely stale — SPID 7.3.3 declares 7.0.0), the image's Win32 file version, and the mod's MO2
`meta.ini`. Each is labelled with its source and the others ride the same line only where they disagree on their
numeric prefix; a modder's tag ("7.0.19.0-AIO") is unknown, not different. `SkseVersionSourceTests` pins the row
separator the composed text may not use.

Reads hold no handle at rest: a read-share stream, closed before return, so MO2 and xEdit can still move the file.

## The peek

`SksePeek` scans one DLL's image for embedded strings — opt-in per DLL, because scanning a whole image is not free,
while imports ride the PE open the manifest read already pays for. Runs are scanned in ASCII AND UTF-16LE (modern C++
plugins use wide strings, so an ASCII-only scan is a confident half-blind answer) at both byte alignments, and only
runs matching an extraction class are kept. An image past the size cap is refused and NAMED, never half-read: there is
no partial-scan state, because a half-read image's "nothing embedded" would be a silent lie.

The two classifiers are held to DIFFERENT bars, and the asymmetry is deliberate. A config path is only ever SHOWN, so a
`{}` template costs nothing and is kept. A plugin name is ADJUDICATED against the load order and can come back "NOT in
your load order", so a false positive there is a false alarm: anything not shaped like a real filename is dropped,
including both format-string dialects (`%s.esp`, `{}.esp`). `SksePeekProbe` part 1 pins the UTF-16 arm and the negative
classification arms.

## Native pairing

A native Papyrus function is ONE thing declared in TWO places — a `.pex` class carrying a native-flagged function, and
a DLL that registers the implementation at runtime. The halves ship as separate files and fail independently, and the
engine's response is "unable to bind" plus calls that silently no-op.

`NativePairing` extracts the DECLARATION side only, purely over Mutagen's `PexFile` model. The native flag is raw bit1
(bit0 is Global): Mutagen's enum names sit one off from the file format, so the raw bit is the truth, and
`NativePairingProbe` part 1 pins it. A native-flagged property accessor is counted as `Prop.Get` / `Prop.Set`.

The baseline is honest by construction: a class carried by an official archive is the ENGINE's even when SKSE's loose
override wins the file, and skse64's own script additions are SKSE CORE, implemented by the game-root loader. Remaining
classes pair to a DLL their provider mod — or a mod in its conflict chain, the bundling case — ships. A class's verdict
is the BEST fate among its candidates, because which DLL implements it is not statically knowable. UNPAIRED is a verify
flag, never "broken". A class whose only loading candidate is a debug build is its own finding (#417) rather than a
line of the healthy roster, which prints class names and would have carried a checkmark over the file that needs
reporting.

The SKSE-CORE rescue pool — every non-official pairing identity shipping a copy of an ENGINE class — has three known
residual edges, all of them false-flag or missed-flag modes of the audit:

- an INI-injected third-party BSA reads official, so its classes read ENGINE;
- a paid Creation Club archive is not BaseMaster-owned, so its engine natives read third-party and flag;
- a provider co-shipping a vanilla override AND an orphan declaration copy has that copy rescued into the unflagged
  baseline — which for the game `Data` folder covers everything installed there.

## Config references

`SkseConfigReferenceExtractor` is catalog-free and framework-agnostic: it finds the two things checkable against the
load order without knowing what any framework MEANS. A form token is a hex FormID paired with a plugin filename by `|`
or `~` in either order, normalized through the one shared home `FormIdRange.LocalObjectId`; a path-segment gate is a
directory component that is itself a plugin filename, gating the whole file on that plugin's presence.

Extraction is a heuristic over token SHAPES, line-local, with no object model — so a token in a comment or a disabled
block still surfaces, and the framing is "references this file declares", never "references the DLL will use". Bare
EditorID and name strings are out of scope: a JSON string is not unambiguously an EditorID, and validating every string
would drown the signal. An over-wide or unparseable hex is CAPTURED and named, never guessed.

The plugin-name charset in `PluginRun` is two deliberate choices with one accepted cost. Apostrophes are ALLOWED,
because excluding them truncates real names mid-word (`kryptopyr's Trade & Barter.esp`). Parentheses are EXCLUDED,
because a token embedded in prose (`will cast fireball (Skyrim.esm|0x5)`) otherwise takes the whole prose prefix as the
plugin name. The price, which the extractor accepts rather than solves, is a KNOWN false negative: a plugin literally
named `Mod (v2).esp` is never matched, so a reference to it is silently absent from the audit rather than reported.
That is the safe direction for this family — a missed reference is a gap, a prose false positive would be a false
DANGLING — and both charset directions are pinned by `SkseConfigAuditProbe` arms 2b and 2d.

The verdict is the service's, over the active order: OK, PLUGIN MISSING, DANGLING, UNPARSEABLE. The headline keeps two
signals apart. BROKEN (dangling + unparseable) should resolve and does not, and is actionable. INERT (plugin missing) is
optional support for a mod you do not have; counting it as dead would make a healthy order read as thousands of dead
references. `SkseConfigAuditProbe` pins the extractor against every reference shape the evidence sample established,
because a false DANGLING is this family's worst failure mode.

## Transport

Every family render charges its tail before laying a row: the scope note, the caveats, the filter hint, the family
footer, each list's own cut notice, and the headings written whatever the rows cost. `cap` stays the caller's own
`max_chars` — the number every notice quotes — while `budget` is the room content has once that tail is charged. A row
is measured against what it is about to write, not against what the buffer holds, and a row that crossed is taken back
out whole. The one arm a bounded render can still exceed — a cap too small for what the response carries whatever the
budget — is named by `RenderCap.Settle`.

The json twin states the same rows and the same accounting in named fields, and classifies with the SAME judge the text
render uses, which is why each family's serializer lives beside its text render rather than in `SkseJsonDoc`. Rows are
dropped from the tail when the document reaches the cap; the serialized string is never cut, which would emit malformed
json. A census counts the population the document answers over — the filter's matches when there is a filter — so no
number describes a wider set than the rows beside it.

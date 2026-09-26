---
updated: 2026-09-25
covers: [src/housecarl-core/NifService.cs, src/housecarl-mcp/NifTools.cs]
---
# The NIF layer: reading mesh values, and the two write gates

## What it is

`NifService` is pure format logic: raw mesh bytes in, the model behind `nif_inspect` out — header and version, block
census, shapes, node tree, header string table. It knows nothing of MO2, the VFS or which mod won; the service layer
resolves the winning bytes and hands them here. Geometry, vertices and `.dds` pixels are out of scope: this reads and
writes DATA VALUES.

## Contracts

Reads ride NiflySharp, source-generated from `nif.xml`, as houseCARL's own build of a fork: NuGet `Nifly`
1.1.0-housecarl.4, vendored in `packages/local` and added by the root `nuget.config`. The fork is upstream 1.1.0's
commit plus one change: every count, string length and unknown-block size is checked against the bytes left before
anything is allocated for it (inside a block, against what the block's stored size has left), and a block that reads
past its stored size stops the load. Both throw
`InvalidDataException` naming the block and the number, where 1.1.0 could allocate gigabytes for one corrupted count
(#926). When a block and its stored size disagree, the exception carries `NifLoadErrors.BlockSizeMismatchKey`, and
`NifService` words that case apart from a count that does not fit the file by that key, never by the message text.
How the package is built and checked is under *The vendored NiflySharp fork* below. One library quirk binds every read: the
alpha / shader / skin refs are read DIRECTLY off `INiShape`, never via `NifFile.GetPropertyOfType<T>`, which NREs on
SE-style shapes whose legacy `Properties` list is null.

A parse failure is a named, recoverable outcome (`NifInspectOutcome.Error`), never a throw and never a half-built
model. The fork's `InvalidDataException` renders as "the mesh is malformed and was not read", carrying the block or
field and the number from its message. A file that loaded but threw while its structure was read is a real defect
and fails loud with the type and message. Unknown blocks are preserved byte-for-byte and reported by their REAL on-disk type from the header's
block-type table — `GetType().Name` would flatten every one of them to `NiUnknown`.

### Coverage comes from the library, never a hand list

The coverage cornerstone applies inside the format layer, and three pieces implement it:

- **`ReallyReads`** — whether a concrete shader block genuinely implements an `INiShader` value accessor, or merely
  inherits the interface's DEFAULT IMPLEMENTATION, which returns a CONSTANT whatever the mesh holds. Derived from the
  interface map, so a library bump is self-correcting: the day upstream implements a value houseCARL reports it, and the
  day upstream stubs one that value goes quiet instead of turning into a wrong number. On NiflySharp 1.1.0
  `BSLightingShaderProperty` implements all six and `BSEffectShaderProperty` stubs all six.
- **`ReallyWrites`** — the write side needs its own check, because `INiShader` declares all six values GET-ONLY: there
  is no `set_` accessor on the interface map to detect, so the setters are reflected off the CONCRETE block class.
  Getter-real and setter-present are independent facts that merely happen to agree today. Component arity is read off
  the property's own type (Single → 1, an R/G/B-bearing struct → 3), so a future widening cannot silently change the
  arity houseCARL enforces. Without this gate a write through the interface is a silent no-op: value discarded,
  success returned, mesh unchanged.
- **`DecodeFlagWord`** — flag-bit names come from `Enum.GetValues` over nifly's own enum, so coverage is the library's.
  Members are peeled largest-first, so a combo member wins over its constituent bits, and whatever no member covers is
  reported as an explicit `UnknownBits` mask: an unnamed bit is something the mesh really carries.

### The Skyrim-layout gate

Two layers of nifly dispatch on which game's stream a block was read as, and both produce a CONFIDENT DEFAULT rather
than a blank when read wrong:

- a value accessor may be layout-dispatched, reading a field only some games' streams carry. Glossiness is the live
  case — served before FO4, Smoothness from FO4 on — so on an FO4 layout it returns its `nif.xml` constructor default
  (80) whatever the mesh holds. `ReallyReads` keys on block TYPE and structurally cannot see that.
- the `Has*` / `IsType*` helpers used to name texture slots return `true` UNCONDITIONALLY for a layout where nifly does
  not model the concept. On an FO4-layout block with an all-zero flag word, `HasSoftlight`, `HasBacklight` and
  `Parallax` are all true.

The shader TYPE is read wrong in two more ways, each also answering a confident `Default` rather than a blank, and each
is why one line of `ShaderTypeName` cannot be removed. WRONG BLOCK: a `BSEffectShaderProperty` serializes no shader
type at all, but the property sits on the shared base, so reading it there yields 0 — which is what the
`blockType == nameof(BSEffectShaderProperty)` guard exists for. WRONG FIELD: the type property is layout-dispatched,
not shared, so `ShaderType_SK_FO4` reads 0 on a block parsed as FO76/SF while `ShaderType_FO76_SF` is garbage on an SK
block — which is why the switch picks the field by the LAYOUT the block was parsed as, never by its type name. A layout
with no type field of its own reports null and the renderer says so plainly.

So houseCARL's claim is scoped to its own coverage: it interprets a SKYRIM shader. The lighting values and the slot
semantics are reported only on an SK-layout block, and `nif_inspect` reads non-SE meshes on purpose, so this is
reachable rather than theoretical. The DECLINE is stated, not just performed — a bare `tex[2]:` is otherwise ambiguous
between "this Skyrim shader does not determine slot 2" and "this layout is not modelled at all" — and the reason names
scope rather than blaming the library, since several of those values would read fine elsewhere. Slot semantics cannot
be reflected out of the library at all (nifly models slots as a bare path list; the meanings are an engine convention),
so `SlotName` is a small explicit interpreter, each arm keyed to the flag or type that decides it, returning null
rather than a best guess. The index is always printed beside the name, because the index is what `nif_set`'s
`texture_slot=` takes.

An `INiShader` widening is narrowed back on read: emissive is a Color3 on disk and the interface widens it to Color4,
so the fourth component is a synthetic 0 that would render as a fully transparent emissive. RGB only, on read, on
write (any component past B is carried over from the current value, never invented) and on read-back (comparing a
synthetic A would fail every colour write).

### The two write gates

`NifService.Set` is bytes-in / VERIFIED-bytes-out. It refuses, with nothing written, when: the mesh will not parse; it
is not a Skyrim SE stream (user 12 / stream 100 — a normalized cross-game write is untested); a target is not found or
is ambiguous; or an op cannot apply. Every successful write passes two OFFSET-IMMUNE gates before its bytes are
returned:

1. **block-content diff** (`VerifyBlockContent`) — normalize the unedited and the edited mesh through nifly's
   canonical writer, slice BOTH by their own block-size tables, and compare block CONTENT by index. Content, not byte
   position: a raw position diff false-aborts on a length-changing rename. Only the block(s) or header the op declares
   may differ; a stray block, geometry or footer change aborts. A header change is legitimate in exactly two cases — a
   rename (which authors the string table) or an expected block that changed SIZE, which mechanically updates the
   header's derived block-size table.
2. **semantic read-back** (`VerifyReadBack`) — reload the written bytes, re-inspect, and assert each op's target now
   reads as REQUESTED, the block census and unknown-block count are unchanged, and the SE stream is intact. This is
   what catches a silent no-op, and for `set_shader_value` it re-reads the CONCRETE property rather than the interface,
   so a value accepted in memory and never serialized is caught even if the write gate ever mis-answers.

Cannot verify means will not write: a layout the diff cannot recover REFUSES rather than passing silently. Both gates
are `internal` so a probe can feed them a collateral change and a no-op write directly.

Block ids are resolved only AFTER the save, because nifly's save re-sorts the block list into its own canonical tree
order — a mesh whose on-disk order was not already that order is renumbered, and a gate comparing the right block at
the wrong index refuses a correct edit. An id that no longer resolves means the save replaced or dropped the block, so
nothing can vouch for the edit.

Two NiflySharp rules bind every op: a bitfield sub-value or a struct in a list (alpha flags, a partition) must be
read-modified-written and RE-ASSIGNED, and a block must be mutated via its OWNING ref — a freshly-built one does not
persist on save.

### Refusals, and what a green verify proves

Ambiguity is a named refusal, never a first-match write. `set_path` carries two addressing forms in one op, and the
header-string form refuses three cases by redirect rather than swapping: a shape's or node's NAME goes to
`rename_shape` / `rename_node`, which carry the rename-onto-an-existing-name guard; an extra-data block's Name is the
KEY the engine looks the block up by, so swapping it would hide the block; and a replacement already in the table is
refused because merging two entries renumbers every later index, which the verification cannot tell from a collateral
edit.

Out-of-convention shader values WARN and proceed rather than refuse. The 0–1 range is a convention, and real meshes
carry values outside it (negative and above-1 emissive components, alpha well above 1), so refusing would refuse edits
to meshes that exist. The warning names the NifSkope 0–255 colour picker, because that is the mistake it catches.

A green verify proves the DATA VALUE landed — not that the face or the armour RENDERS right. The geometry, the pixels
and the final render stay unseen, so a rewritten path or a renamed shape still needs the in-game check.

### The vendored NiflySharp fork

The fork is https://github.com/Avick3110/NiflySharp. The package in `packages/local` is built from commit
`55da70711949f77b879dcf5b1d2f9c18a2405792`: upstream `ousnius/NiflySharp` at `7b55e88a` (the commit Nifly 1.1.0
was built from), plus three commits:
- the first adds the bounds and the block-end check;
- the second caches each element type's smallest size without a lock;
- the third bounds a count inside a block by what the block's stored size has left, and marks a block-size
  disagreement with a data key.

The assembly is still `NiflySharp 1.1.0.0`. Its public API is 1.1.0's plus one class, `NifLoadErrors`. The
THIRD-PARTY-NOTICES entry names the same commit.

To rebuild it: clone the fork, check out that commit, and run `git submodule update --init` for `nifxml` (it should
be at `292bb940`). Then, with the .NET 10 SDK, run:

```
dotnet build NiflySharp/NiflySharp.csproj -c Release -p:Version=1.1.0-housecarl.4 -p:ContinuousIntegrationBuild=true -p:RepositoryUrl=https://github.com/Avick3110/NiflySharp -p:PackageProjectUrl=https://github.com/Avick3110/NiflySharp
```

The project builds the package itself, into `NiflySharp/bin/Release/`. A bare `dotnet pack` fails with NU5026 on a
fresh clone, because it packs before the builds exist. `ContinuousIntegrationBuild` keeps the build folder's path out
of the DLLs, so two clones at different paths give the same DLLs byte for byte.

Give every rebuild a new version suffix. NuGet keeps each version it has restored in `~/.nuget/packages` and never
reads the local source again for that version. CI's package cache can fall back to an older cache, so it relies on the
same rule.

Before a new build replaces the vendored one, check it the way each earlier version was checked:

- **Every real mesh, file by file.** Load and round-trip every loose `.nif` in the instance's mod folders, and every
  mesh in `Skyrim - Meshes0.bsa` and `Meshes1.bsa`, once with the old library and once with the new. Then compare
  the two runs file by file. The dev corpus's `spike-nif` scanner and `diff_ab.py` do this. Matching totals is not
  the check, because two runs can match in total and still disagree about which files they read. housecarl.4 against
  1.1.0: 22,047 archive meshes and 72,861 loose meshes, 0 files changed verdict.
- **Random corruption of the authored test mesh.** Change 4 random bytes (seeds 0 to 22,112), and change one byte at
  each of the 998 positions. Each input runs in a child process that is stopped at 10 s or 2 GB. On 1.1.0, 137 + 38
  inputs passed 2 GB. On housecarl.4, 0 did: each of those is a named error, and the worst input took 101 ms and
  26 MB. Of the 847 seeds that 1.1.0 reads, 829 read to the same result. The other 18 each carry a corrupted size
  that the fork now checks.
- **A corrupted count in the largest real mesh.** The largest loose mesh in the instance is a 40 MB FaceGen head
  (`00013386.nif`, 172 blocks). A copy of it was given a count of 10,000,000 in three places: the root node's
  children, the root node's extra data, and block 3's children. On 1.1.0, each ran past 2 GB in about 4 s. On
  housecarl.3 (bounded only by the bytes left in the file), each was refused after about 0.9 s and 450 MB. On
  housecarl.4, each was refused in 27 ms at a 66 MB peak, with the 40 MB file already in memory. Unchanged, the mesh
  reads the same on all three in about 0.6 s at 167 MB.

Earlier, 1.0.0 to 1.1.0 (2026-07-26, #287) was checked the same way: 12 more loose meshes read, and no file read
worse.

## Pinned by

- *Contracts*, the parse-failure paragraph: `NifInspectDecodeTests` — empty bytes and non-NIF garbage each return a
  named error, never a throw or a half-model (`EmptyBytesReturnANamedError`, `NonNifGarbageReturnsANamedErrorNotAThrow`);
  `NifInspectMalformedTests` — through the built server under a 2 GB heap cap and a 30 s call timeout, meshes that ran
  1.1.0 out of memory come back from `nif_inspect` and `nif_set` as the malformed error: a count past its block's
  stored size, a header count, and the write refusal ending "Nothing was written."
- *Coverage comes from the library, never a hand list*: `NifShaderDecodeTests` and `NifInspectRenderTests` pin both
  branches of `ReallyReads`, `NifSetGuardProbe` (`nif-set-guard`) pins all three `ReallyWrites` states including the
  unmarshalable one via a stand-in type, and the flag decode's gap and combo-peel behaviour is pinned rather than
  assumed (`NifShaderDecodeTests.AnUnnamedBitIsAResidualMaskAndAComboPeelsBeforeItsParts`).
- *The Skyrim-layout gate*: `NifShaderDecodeTests` and `NifInspectRenderTests` — the FO4-layout Glossiness default
  `ReallyReads` cannot see, and a texture slot nothing determines stays unnamed rather than getting a plausible label
  (`NifShaderDecodeTests.AnUndeterminedSlotStaysUnnamed`).
- *The two write gates*: both gates are fed a collateral change and a no-op write directly, which `NifSetGuardProbe`
  does — gate 1 refuses the collateral change and gate 2 the no-op; its refusal arms — a target not found or
  ambiguous, an op that cannot apply, and a non-SE stream (that arm prints SKIP rather than failing when its fixture
  cannot be built on the NiflySharp in use).
- *The two write gates*: `NifSetBlockOrderTests.SetPathWritesOnAMeshWhoseStoredBlockOrderIsNotTheSaveOrder` — block
  ids are resolved after the save's re-sort; `TheFixtureMeshStoresItsTextureSetAtADifferentIdThanASaveGivesIt` in the
  same class — the fixture really is out of save order.
- *Refusals, and what a green verify proves*: `NifSetGuardProbe`'s `set_path` header-string arm — a shape's NAME is
  refused and sent to `rename_shape`. No arm sends `set_path` onto a node's name, so that half is not pinned.

## Where

`src/housecarl-core/NifService.cs` is the format layer: `Inspect`, `Set`, `ReallyReads`, `ReallyWrites`,
`DecodeFlagWord`, `SlotName`, and the two gates `VerifyBlockContent` and `VerifyReadBack`.
`src/housecarl-mcp/NifTools.cs` is the tool front. Tools: `housecarl_nif_inspect`, `housecarl_nif_set`.
The result records are in `src/housecarl-mcp/AssetResults.cs` ([`assets.md`](assets.md)).
The service code, which resolves the winning bytes and writes the result into the VFS, is in `src/housecarl-mcp/AssetLayers.cs` ([`assets.md`](assets.md)).

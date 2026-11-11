---
updated: 2026-11-10
covers: [src/housecarl-core/PapyrusDecompiler.cs, src/housecarl-mcp/DecompileTools.cs, src/housecarl-mcp/DecompileBatch.cs, src/housecarl-mcp/ScriptSelection.cs]
---
# Papyrus: decompile

## What it is
What `housecarl_decompile_script` is, the class hierarchy it reads, and the compile lane beside it
are [`papyrus.md`](papyrus.md); this note is the decompiler's own contracts.

## Contracts

### The decompiler
- The codegen patterns it reads, each confirmed against compiler output: jump offsets are relative to
  the jump instruction itself; `while` is cond, `JMPF` to the end, body, backward `JMP`; `if` is cond,
  `JMPF` to the else label, then, `JMP` to the join (a `JMPF` straight to the join means no else);
  `&&`/`||` is a short-circuit `JMPF`/`JMPT` whose arm rewrites the same temp, read again at the join;
  a call returning None puts its result in the `::NoneVar` discard slot, and a read of that slot is
  that call's value; an auto property's backing var is `::Name_var`, and `AutoReadOnly` is a Get
  returning a literal; `GotoState`/`GetState` in the unnamed state are compiler-generated and skipped;
  in `FunctionFlags` bit 0 is Global and bit 1 is Native, the raw bits rather than Mutagen's enum
  names, which sit one off.
- A function whose flow does not match a verified pattern FAILS LOUD: it is counted and emitted as a
  comment block with its raw bytecode, never as silently wrong source.
- An optimizer hint means the `.pex` came from an optimizing compiler (Caprica class): the source is
  correct, but the CK compiler will not reproduce the original bytes. Detection is best-effort, so
  its absence proves nothing.
- Casts that are implicit in every Papyrus context — to Bool or String, Int to Float, an identity
  cast, an upcast — are emitted as the bare operand; re-emitting one explicitly changes codegen.
- A statement carrying a pending value refuses rather than pick an order the source cannot express,
  in either direction (#792). A held-back value that cannot observe the statement's effect is not
  refused.
- A discarded expression comes back as the bare statement it came from (#785) — the kinds that
  compile back to the one instruction they came from. A bare variable read, a literal, and an
  expression over only literals stay loud failures, because the compiler emits nothing for them.
- A parameter default is not in the `.pex`, so it survives only where a call in the same script
  omitted that argument, and the longest run any such call shows is the one declared (#786). A call
  on another script, or from a property handler standing in for a same-named function, is not
  evidence.
- A `.pex` object name that is not a plain script name is refused before any `.psc` is written, and
  every name in the file is checked up front so a bad one at the end cannot leave its neighbours on
  disk (#783). Writes go through `FileMode.CreateNew`, so an existing source file is never
  overwritten.

### Selecting the scripts (#1287)
- `paths=`, `under=` and `provider=` are `asset_status`'s selectors, through `FileSelection.Select` over one
  captured view (`AssetLayers.SelectScripts`, assets.md "Selecting a set of paths"): explicit paths in the order
  given, then the selectors' matches. A match that is not a `.pex` is dropped and counted in one note; an explicit
  path that is not one is its row's error. The path bound and the unknown-provider refusal are the selection's own.
- A `paths=` entry that is a full path (`Path.IsPathFullyQualified`) is a file on disk, read as itself, with no
  instance needed under `out_path=`; one with no separator is a bare name, refused naming `scripts/<name>.pex` and
  the `under=` search (DECISIONS 2026-11-10 (4), Q1 (a), Q4 (b)). Everything else is a Data path.
- One file on disk with no selector, no `source_provider=` and default transport answers as the one-file tool did,
  except that its no-such-file refusal names the Data-path form (`scripts/<name>.pex`) where it named
  `bsa_extract`; everything else is the
  many-script lane (`DecompileBatch`).
- `provider=` picks which scripts; each is read from its winning copy, and a note counts the ones another provider
  wins. `source_provider=` picks whose copy, through `AssetSourceSelection` as `nif_inspect` does, so it reaches a
  disabled mod's loose files and archives; a script it does not ship is that row's error.
- Bytes come through the view's `ReadSource`, a loose file or one archive entry, so each archive's table is read
  once per call and nothing is extracted. A many-script call parses each script twice (hierarchy, then write) and
  keeps no parsed file, so memory does not grow with the selection.
- The class hierarchy adds every script in the window and the `.pex` files beside each file on disk; when the call
  has a Data-path selection (`paths=` Data paths, `under=` or `provider=`), it also climbs each script's parent chain
  off the load order. It follows an edge it already has and keeps climbing; only a class with no edge is read, from
  `scripts\<class>.pex` (the winner, loose or archived), and never one of the window's own scripts, which were read
  already. The climb stops at a root class of the vanilla baseline (Form, Alias, ActiveMagicEffect, ...), which is not
  read; at a parent the order does not ship (an MCM script with SkyUI disabled); at a path the view refuses; or at a
  read that fails. So a mod class that only mods-tree `.psc` headers name as a parent (MCM Helper's `MCM_ConfigBase`,
  SkyUI's `SKI_ConfigBase`) is read from its `.pex`. Only a failed read is named in the hierarchy note: a parent the
  order does not ship ends the climb unnamed, and its children keep explicit casts.
- `limit=`/`offset=` window what is decompiled, not only what is listed (SPEC §2.1's list-driven offset). So
  `remaining` counts the scripts after the window, not after the rows listed, and while any remain the next-page
  advice names `limit=`, the `offset=` after the window and the knob that lands the page in this call's folder:
  `out_path=` or `into=` as given, else `into=` the stem of the patch this call wrote into, else `patch=` as given.
  Json carries it as `next_limit`, `next_offset` and `next_out_path`, `next_into` or `next_patch`. An existing target
  `.psc` is that script's row error and the rest still land. `max_chars=` cuts the listing only; every script in the
  window is written, and no page is advised over them. `counts_only=` gives the totals and notes, and no next page. `to_file=` is refused: decompile writes `.psc` files, and its output is the folder.
- The retired `pex=` is refused in one sentence naming `paths=`.

## Pinned by
- `DecompileSelectionTests` — a provider's loose and archived scripts in one call; `under=` across both;
  `source_provider=` reading the shadowed copy and a disabled mod's, and turning off the won-elsewhere note; a file
  on disk beside the selection, rows in the order given; a missing file on disk, an absent and an invalid Data path,
  a non-script path and a second object's existing target (with what was already written) each its own row; `paths=`
  from a file; the instance needed for a selection under `out_path=`; a bad `out_path=` beside `patch=`; the bare-name
  (with and without `.pex`) and retired-name refusals; an existing target as one row; the window choosing what is
  decompiled; an unreadable parent named in the many-script wording; a mod parent only a `.psc` header names read
  loose and archived up to the vanilla root (`AModParentOnlyAPscHeaderNamesIsReadLooseAndArchivedToTheVanillaRoot`);
  a vanilla root not read; one table read per archive per call; one file on disk alone answering as the one-file
  tool did, whole; `counts_only=`; a `max_chars=` cut writing every script and advising no page over them, text and
  json; the next page landing in the same folder (`into=` the patch written, as given, `patch=`, `out_path=`); json
  rows cut at the first that does not fit; a fresh folder nothing landed in removed; `to_file=` refused; json.
- `DecompileGuardTests` — the golden source for a canonical `.pex`, zero optimizer hints on
  canonical output, the hint firing on a statement-level `JMPT`, the refusal on an existing target
  leaving it byte-untouched, the Hidden flag kept on both objects of a two-object `.pex`, and the upcast
  suppression with and without a class map.
- `DecompileNoneResultTests.ACallArgumentNeverOvertakesACallProducedAfterIt` and its property-set,
  array-set and while-body siblings — the ordering refusal (#792);
  `…APureValueHeldBackPastAStoreIsNotRefused` — that the refusal is scoped to values that can
  observe the effect.
- `DecompileNoneResultTests.ADiscardedCastComesBackAsABareCastStatement`,
  `…ADiscardedPropertyReadComesBackAsABarePropertyRead`,
  `…ADiscardedArrayReadComesBackAsABareIndexStatement`, and
  `…ADiscardedPlainReadStaysALoudFailure` / `…ADiscardedExpressionOverOnlyLiteralsStaysALoudFailure`
  — the discarded-expression contract and its two exceptions (#785).
- `DecompileDefaultedParameterTests.AnOmittedTrailingArgumentPutsTheDefaultBackOnTheSignature`,
  `…TheLongestRunAnyCallShowsIsTheOneDeclared`, `…ACallOnAnotherObjectLeavesTheSignatureAlone` and
  `…APropertyHandlerNeverInheritsADefaultFromASameNamedFunction` — the parameter-default contract
  (#786).
- `DecompileOutPathTests.AnObjectNameThatWouldLeaveTheOutputFolderIsRefusedAndNothingIsWritten` and
  `…AnEscapingNameLaterInTheFileStopsTheCallBeforeTheFirstPscIsWritten` — the object-name refusal
  and the up-front check (#783).
- `DecompileShortCircuitTests` — the short-circuit arm shapes and the promoted condition temp.

## Where
- Core: `PapyrusDecompiler` (`DecompileFile`, the `Body` structurer).
- Tools: `DecompileTools` (`housecarl_decompile_script`, the one-file lane, `WriteObjects`, `HierarchySentence`);
  `DecompileBatch` (the many-script lane and its renders); `ScriptSelection.cs` (`AssetLayers.SelectScripts`,
  `PickScript`).

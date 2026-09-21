---
updated: 2026-09-22
covers: [src/housecarl-core/PapyrusSourceRoots.cs, src/housecarl-core/PapyrusDependencyFilter.cs, src/housecarl-core/PapyrusCompile.cs, src/housecarl-core/PapyrusClassParents.cs, src/housecarl-core/PapyrusDecompiler.cs, src/housecarl-mcp/CompileTools.cs, src/housecarl-mcp/DecompileTools.cs]
---
# Papyrus: compile and decompile

## What it is
Two lanes over the modlist's script layer: `housecarl_compile_script` drives the Creation Kit's
`PapyrusCompiler.exe` as a subprocess, and `housecarl_decompile_script` reconstructs source from a
`.pex` over Mutagen's own model, with no external tool. The import path the compiler searches is
discovered from the MO2 instance rather than retyped; the decompiler's class hierarchy is read the
same way. The service side of that discovery is `LoadOrderService`; the instance and its precedence
are [`mo2-instance.md`](mo2-instance.md), and the declaration side of a native Papyrus function is
[`skse-layer.md`](skse-layer.md).

## Contracts

### Source-root discovery
- MO2 stores no Papyrus import list and `SkyrimEditor.ini`'s `sScriptSourceFolder` is a single
  folder, so the import path is SCANNED off the VFS loose roots, never read from a config.
- Two on-disk layouts are recognised, `Source\Scripts` (SE/CK) and `Scripts\Source` (LE), and a
  folder counts only when it holds a top-level `.psc`.
- Roots are walked and emitted in the caller's given order, which is MO2's own precedence; dedup by
  absolute path keeps the FIRST occurrence, so the higher-precedence root keeps the slot.
- An unreadable or un-rootable root costs one import dir, never the scan and never the compile.
- The game's own Data sources are SPLIT out of the mod candidates and handed back rather than
  dropped: on a Stock Game setup the compiler's vanilla folder and MO2's data dir are different
  folders, and the returned one is what the compile lane uses when the compiler-relative folder is
  missing. Matching is exact against the folders discovery would produce for that data dir, and a
  blank data dir splits nothing.

### The reference walk that narrows it
- The path is narrowed to the folders the target script reaches: a folder providing only names this
  script never mentions contributes nothing to this compile.
- The walk mirrors the compiler: a name indexes to exactly ONE folder, its first (highest-precedence)
  provider, and the closure is transitive. Seed folders — the script's own folder and the caller's
  `import_dirs=` — are indexed but never returned, because the caller adds them unconditionally.
- It is deliberately over-inclusive: every identifier-shaped token is a candidate name.
- Vanilla is held out of the candidates: it is appended last unconditionally, so indexing it would
  only walk the base game.
- Both degraded outcomes are FLAGGED, never left to look like a clean empty answer — a walk
  truncated at the file ceiling, and a target whose own source could not be read, which says nothing
  about what the script references.

### The compile invocation
- The invocation is `PapyrusCompiler <object> -f="flags.flg" -i="dir;dir" -o="out"`; diagnostics
  print to stderr, one per line, as `<fullpath>(line,col): message`, and unmatched lines are noise.
- Success is THIS run WRITING the `.pex` — not the exit code, which is 0 even on a usage error, and
  not the absence of diagnostics, which would fail a compiled-with-warnings build.
- The compile is non-destructive: a prior `.pex` is left in place and success is decided on its
  write-time advancing, so a failed recompile never destroys the last good build.
- A `RunError` means the compiler could not be run at all (bad path, timeout) and is distinct from a
  compile that ran and produced nothing. Both output streams are read asynchronously, with a bounded
  post-exit drain so a grandchild holding the pipe cannot hang the call.
- Import-path ORDER is semantics: the script's own folder, then the caller's extras, then the
  discovered mod folders in MO2 priority order, then the vanilla sources LAST. Vanilla stays last
  even when the caller re-passes it or the modlist scan reaches it.
- Provenance labels are read off the FINAL assembled list, not the inputs, and where a dir belongs to
  two origins the caller outranks the scan. The assembled path is refused up front when it is too
  long for one command line.
- The "symbol could not be resolved" wording is its own class, distinct from a syntax error, and a
  failure dominated by it leads with the import-path hint instead of reading as code bugs.

### The class hierarchy
- The child→parent map is a SOFT dependency: a missing or partial map only leaves explicit casts in
  the output, which is correct and compilable, so every loader degrades to fewer edges, never a
  throw, and every degraded mode is named in the result rather than left silent.
- Three layered sources — the committed vanilla baseline beside the exe, loose `.psc`
  `ScriptName X extends Y` headers across the mods tree, and the input `.pex` plus its siblings.
  First edge per child wins.

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

## Pinned by
- `compile-ergonomics-guard` (generator) — part G pins the two layouts, the `.psc` gate, the
  precedence order, the dedup and `SplitGameData`'s exact match and blank-data-dir arm; part H pins
  the transitive walk, first-provider precedence, the unreferenced drop and the unreadable target;
  its render arms pin the summary's narrowing count, the unresolved-symbol class, and the
  missing-imports banner's three-diagnostic floor and two-thirds bar.
- `import-order-guard` (generator) — the import-path order, vanilla last even when re-passed or
  re-found, case-insensitive dedup, the provenance labels, the caller-outranks-scan rule, and the
  vanilla-missing claim agreeing with the assembled path.
- `compile-probe` (generator) — diagnostic parsing, and that a failed recompile leaves the prior
  `.pex` intact while a successful one advances its write-time.
- `decompile-guard` (generator) — the golden source for a canonical `.pex`, zero optimizer hints on
  canonical output, the hint firing on a statement-level `JMPT`, the refusal on an existing target
  leaving it byte-untouched, and the upcast suppression with and without a class map.
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
- Core: `PapyrusSourceRoots` (discovery, `SplitGameData`), `PapyrusDependencyFilter` (`Relevant`,
  `MaxFilesRead`), `PapyrusCompile` (`CompileObject`, `ParseDiagnostics`, `IsUnresolvedSymbol`),
  `PapyrusClassParents` (baseline, `.psc` headers, `.pex` top-ups), `PapyrusDecompiler`
  (`DecompileFile`, the `Body` structurer).
- Tools: `CompileTools` (`housecarl_compile_script`, `BuildImports`, `PlanImports`, the import
  summary and detail renders), `DecompileTools` (`housecarl_decompile_script`, `WriteObjects`,
  `HierarchySentence`).
- Service: `LoadOrderService.PapyrusSourceImportDirs` (in `LoadOrderService.cs`) and
  `ClassParentsForDecompile` (in `OutputLocations.cs`) supply the modlist-derived halves of both lanes.

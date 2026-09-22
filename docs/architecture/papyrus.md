---
updated: 2026-09-23
covers: [src/housecarl-core/PapyrusSourceRoots.cs, src/housecarl-core/PapyrusDependencyFilter.cs, src/housecarl-core/PapyrusCompile.cs, src/housecarl-core/PapyrusClassParents.cs, src/housecarl-mcp/CompileTools.cs]
---
# Papyrus: compile

## What it is
Two lanes over the modlist's script layer: `housecarl_compile_script` drives the Creation Kit's
`PapyrusCompiler.exe` as a subprocess, and `housecarl_decompile_script` reconstructs source from a
`.pex` over Mutagen's own model, with no external tool. The import path the compiler searches is
discovered from the MO2 instance rather than retyped; the decompiler's class hierarchy is read the
same way. The service side of that discovery is `LoadOrderService`; the instance and its precedence
are [`mo2-instance.md`](mo2-instance.md), and the declaration side of a native Papyrus function is
[`skse-layer.md`](skse-layer.md).
The decompiler itself is [`papyrus-decompile.md`](papyrus-decompile.md).

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
  throw.
- Three layered sources — the committed vanilla baseline beside the exe, loose `.psc`
  `ScriptName X extends Y` headers across the mods tree, and the input `.pex` plus its siblings.
  First edge per child wins.
- ALL THREE degraded modes are named in the result, one member per source: a missing or unreadable
  baseline (`ClassParents.BaselineNote`), mods-tree sources that were not all read (`TopUpMissing` —
  set both for a tree read NOTHING was read from and for a walk that lost some `.psc` files), and
  sibling `.pex` files that were not all read (`SiblingPexMissing`), all rendered by
  `DecompileTools.HierarchySentence` — one clause per thin source, then what the hierarchy IS instead,
  then the one cost sentence. `PapyrusClassParents.AddFromPexFolder` counts an unreadable sibling
  rather than swallowing it, and reports an absent folder and a listing that threw part-way as separate
  facts so a partial listing keeps its counts; the decompile lane turns the scan into the reason (the
  service cannot know it, so it sets the member after its own sibling walk).
- A PARTIAL read of a source NEVER disowns the edges it did add: both walks keep going past a file
  they cannot read, so the trigger is "not all read" and the "is" half credits the mods-tree sources
  and the sibling `.pex` files THAT COULD BE READ. Uniform across the two top-up sources; the baseline
  is all-or-nothing, since a corrupt one loads as an empty map. The input `.pex` is always read, so the
  "is" half is never empty, and the sibling count excludes it, which the clause has already excluded by
  saying "beside this one".
- Pinned by `ClassHierarchyNoteTests` — the two walks keep the edges of the files around the one they
  lost, and the sentence credits them — plus
  `DecompileOutPathTests.AnUnreadableSiblingPexIsNamedAndTheHierarchySaysWhatItIsInstead` and
  `.APartialSiblingFailureCountsOnlyTheFileItLost` for the rendered note.

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

## Where
- Core: `PapyrusSourceRoots` (discovery, `SplitGameData`), `PapyrusDependencyFilter` (`Relevant`,
  `MaxFilesRead`), `PapyrusCompile` (`CompileObject`, `ParseDiagnostics`, `IsUnresolvedSymbol`),
  `PapyrusClassParents` (baseline, `.psc` headers, `.pex` top-ups).
- Tools: `CompileTools` (`housecarl_compile_script`, `BuildImports`, `PlanImports`, the import
  summary and detail renders).
- Service: `LoadOrderService.PapyrusSourceImportDirs` (in `LoadOrderService.cs`) and
  `ClassParentsForDecompile` (in `OutputLocations.cs`) supply the modlist-derived halves of both lanes.

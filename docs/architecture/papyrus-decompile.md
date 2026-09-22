---
updated: 2026-09-23
covers: [src/housecarl-core/PapyrusDecompiler.cs, src/housecarl-mcp/DecompileTools.cs]
---
# Papyrus: decompile

## What it is
`housecarl_decompile_script` reconstructs source from a `.pex` over Mutagen's own model, with no
external tool. The class hierarchy it reads, and the compile lane beside it, are
[`papyrus.md`](papyrus.md).

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

## Pinned by
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
- Core: `PapyrusDecompiler` (`DecompileFile`, the `Body` structurer).
- Tools: `DecompileTools` (`housecarl_decompile_script`, `WriteObjects`, `HierarchySentence`).

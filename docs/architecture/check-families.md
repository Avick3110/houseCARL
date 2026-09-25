---
updated: 2026-09-25
covers: [src/housecarl-core/DeletedRecordRule.cs, src/housecarl-core/ErrorCheck.cs, src/housecarl-core/SweepExclusion.cs, src/housecarl-core/SweepFamilies.cs, src/housecarl-core/SweepScope.cs, src/housecarl-mcp/CheckOutcome.cs, src/housecarl-mcp/RecordChecks.cs, src/housecarl-mcp/CheckTools.cs, src/housecarl-mcp/CheckAccounting.cs, src/housecarl-mcp/CheckArtifact.cs, src/housecarl-mcp/CheckTextRender.cs, src/housecarl-mcp/CheckSentences.cs, src/housecarl-mcp/CheckSweep.cs, src/housecarl-mcp/SweepSharedInput.cs, src/housecarl-mcp/SweepOffOrderScope.cs, src/housecarl-mcp/FaceGenSweepRender.cs]
---
# The check families' contracts

## What it is
The home of the contracts the check-family code itself cannot express, cited from `ErrorCheck`,
`DeletedRecordRule`, `ScriptPropertyCheck`, `SweepScope`, `SweepExclusion`, `SweepFamilies` and `CheckOutcome`
under ADR 0001. The scripts family's own boundary, the dialogue family's seed words and the EditorID near-miss hint are in
[`check-scripts-and-dialogue-families.md`](check-scripts-and-dialogue-families.md).

## Contracts

### A DELETED record has no live body

A major record flagged Deleted carries no content by engine rule: the game reads the header, sees the flag, and
never looks at a body. So its outgoing links are not live — it references nothing, and there is no field to test.
`DeletedRecordRule.HasNoLiveBody` is the one place that rule lives, and every walker excludes a deleted record
BEFORE the link walk: the scan's `references=` arm, `ErrorCheck`'s dangling sweep (active and off-order),
`RemapEngine.IdentifyExternalReferencers`, and `WritePatchBuilder.TryScanMergeDonor`.

It is also the crash guard for those walks. An ENGINE-authored deleted record can leave a content-free-but-unclean
leftover body behind; Mutagen's lazy parse then throws when the walk reaches for its links, and the walker accounts
that as an UNSCANNABLE skip with a raw exception cause — a deleted record reading as a parser hole. Skipping it as
deleted gives the same answer with nothing left in the unscannable bucket. (A Mutagen-AUTHORED deleted record is
clean, so this only bites on records the engine or another tool wrote.)

**Scope: the link walk and the body-content filters only.** Anything read from the record HEADER stays live,
because the header is parsed eagerly and is not what throws — the external-OVERRIDER test is identity-only and runs
BEFORE the guard, and the `editorid_contains` filter stays live because EDID is an early subrecord.

**Consequence:** a deleted record whose body DOES parse and DOES link to a searched target is not returned by
`references=`, not reported as dangling, not listed as an external referencer, and does not refuse a merge whose
donor it sits in.

### The integrity sweep's honest boundary

`ErrorCheck` covers the FormLink-resolution, missing-master and parse class, and claims exactly that. Two things it
deliberately does not do, both named in the rendered footer so a clean result is never read as xEdit parity:

- **"used-but-undeclared master" is structurally undetectable through Mutagen.** A FormID's master-index byte is
  decoded against the plugin's declared master list, so every FormKey Mutagen yields has, by construction, a ModKey
  that is a declared master or the plugin itself. That corruption lives in the raw byte, below what Mutagen models.
  Its observable effect — refs that no longer resolve — is caught as DANGLING, and nothing claims to diagnose the
  cause.
- **"declared-but-unused master"** cannot be proved by a FormLink scan: a master used solely by scripts or unmodeled
  refs would read as unused. Deferred as a named item rather than shipped as an unprovable claim.

It also does not verify navmesh or terrain SPATIAL integrity, and does not flag a required field left null (a null
FormLink is a legal optional here).

#### The UntypedOwner VariableData exemption

A container or leveled-list item's ownership is a COED block: an owner FormID plus a SECOND four-byte word, which is
a `RequiredRank` int when the owner is a FACTION and a Global FormLink when it is an NPC. Mutagen picks the arm by
resolving the owner form's record TYPE; when the owner lives in a MASTER and the overlay carries no link cache —
which is EVERY override this sweep reads — that resolution throws and the arm falls back to `IUntypedOwnerGetter`,
which exposes BOTH words as FormLinks. `EnumerateFormLinks` then walks the second word, and a faction rank of -1
(`0xFFFFFFFF`) resolves to nothing, so without the exemption it reads as a dangling reference.

So only that second word is dropped, per record and by exact FormKey **with a count**, so it can never mask an
unrelated dangling link elsewhere in the record. The owner form itself — the FIRST word — is still checked, so a
genuinely broken owner still surfaces. What this gives up is one thing: a genuinely-dangling Global on an NPC-OWNED
item whose owner NPC lives in a master, traded for never false-flagging the far commoner faction-owner rank.

#### The listing budget spends in two phases

`limit=` is ONE counter, and spending it plugin by plugin in load order would let the vanilla baseline consume it at
index 0 — a plugin that collects an empty dangling list is dropped from the reports entirely, so its findings would
be absent with no per-plugin trace. The per-plugin sweep is therefore called in two phases: every non-base plugin
first, then the base-game masters on what is left. An off-order file swept in the same call is listed above them.

What each plugin FINDS is unchanged — the totals, the histograms and the missing-master reads never depend on
`limit=` — and the reports are re-sorted back into load order before they are returned, because which section comes
first is a load-order fact.

#### The baseline split, and "swept" means examined

`BaselineDangling` is derived from the ONE by-source tally, so "is this plugin baseline" is asked in exactly one
place. `BaseMastersSwept` is the swept SUBSET, not a yes/no: a render saying "N of M come from the base-game
masters" has to name the ones it counted, and naming all five over a sweep that touched one states a figure about
four plugins it never opened.

That subset is built from what the link walk **examined**, not from what the sweep opened, which covers both lanes
at once: an off-order base master swept as a FILE counts, and a base master whose every record a record scope
filtered out does NOT — the sweep opened it and examined nothing in it. A swept baseline that came back CLEAN is
therefore a different fact from a sweep that never looked at one, and the two stay distinguishable.

#### Null is "not computed", never "empty"

Every optional tally on `ErrorCheckResult` and `ScriptCheckResult` — both histograms, the by-source axis, and
`PluginErrors.InstalledButInactiveMasters` — is built only when the walk that fills it actually ran. An
empty-but-present collection would render as "nothing found" for a count never taken, so null means the question was
not asked and the render must fall back to the unqualified remedy.

The install-vs-enable split for missing masters is filled in by the layer that reads the MO2 composition, because
the core sweep knows the active order and stops there; a composition that cannot be read leaves that subset null.

#### Which plugins LOST entries is not a sweep fact

It is a fact about the RESPONSE. Computed on the result it could only ever report the listing budget's own
omissions, leaving a plugin whose entries the budget listed and `max_chars` then dropped in no sentence at all. It
is therefore computed in `CheckAccounting` against what the response emitted, which covers both truncators at once.

### Narrowing narrows the numbers — but not all of them

A scoped sweep's totals are the totals FOR THE SCOPE, and the render carries the scope label whenever anything is
applied, so a count can never be read as a wider claim than it is. Which narrowings carry the subset CLAIM is a
separate judgement, made per family:

| narrowing | does it make a reported count a subset of its own label? |
|---|---|
| a RECORD scope (`types=` / `formids=` / `editorid_contains=`) | yes — the claim fires |
| `property_contains=` (scripts) | no — it narrows the unbound and bound-null counts, which self-label in the header, and leaves `RecordsWithScripts` and `TotalUnverifiable` plugin-wide |
| a `findings=` class filter | no — every reported number stays complete for what it names, and an excluded class renders "NOT CHECKED" |
| the plugin-level missing-master count, under a record scope | no — it is read off the master table, so the errors family's claim is qualified whenever both are in play |

An excluded class is printed as "not checked", never as a zero: a check nobody ran must not read as one that came
back clean. Unscannable records, scan errors and unverifiable attachments are always reported and cannot be filtered
out at all.

`types=` is applied at the record STREAM, so a type scope costs nothing per skipped record; `formids=` and
`editorid_contains=` are per-record tests taken before any expensive work. On an off-order file the same narrowing
goes through `RecordArms`, the arm re-check the in-order lanes use, so an arm scope does not sweep the whole GRUP.

### The exclusion axis

`exclude=` is an AXIS, not a classification: the sweep has no "skip the boring ones" rule of its own, the baseline
it splits out stays Mutagen's `BaseMasters` exactly, and what counts as noise is the caller's judgement. Creation
Club and `_ResourcePack.esl` live under the `implicit` token and nowhere in the sweep's definitions — that token is
named for what the set observably IS, "not listed in plugins.txt", because "is a Creation Club plugin" is not
something houseCARL can observe.

Names and tokens share one namespace without a sigil, because Bethesda plugin filenames always carry an extension:
a value with one is a NAME, a value without one is a TOKEN, and an unknown token refuses by name rather than being
matched loosely against a plugin whose stem looks like it.

The two kinds are validated **separately**. A NAME is a claim that a specific plugin is in scope, so one that
matches nothing is a typo and refuses; a GROUP is a filter — "whichever of these are here, drop them" — so a member
that is not in scope is the ordinary case. Validated as one merged list instead, a narrowed scope would refuse every
group token while naming a plugin the caller never wrote.

The exclusion is applied to the SWEEP rather than to the listing, so an excluded plugin costs no record walk and no
budget, and it runs whenever the caller PASSED one even where it expands to nothing — otherwise a group with no
members in this order leaves no trace and the response never mentions that `exclude=` was written at all. The
`implicit` group is resolved at the MO2 layer, where the composition lives, and a profile that cannot be read
refuses rather than expanding the group to nothing.

### Family selection

Family tokens and class tokens are ONE vocabulary, not two parameters: a family token means every class in that
family, a class token means that family runs narrowed to that class. This is the device `unbound` already was,
applied one level up, so a call naming several families needs no second parameter and no guard clause.

Membership is declared once, in `SweepFamilySelection.Registered`, and family tokens are matched against that list
rather than a hand-written case per family — so a registered family is askable by construction and the vocabulary a
refusal offers cannot name a spelling the parser rejects.

The default is ONE family — errors — and the response says so. It cannot be every family: an unscoped scripts sweep
is too expensive to be a default, and an unscoped dialogue sweep is refused on cost outright. A default that
narrowed silently would answer a question the caller did not ask without saying which, so the selection carries
`NotRun` and the render states every registered family it did not run together with the exact spelling that adds it.

### Selection is not outcome

`SweepFamilySelection` says what the caller asked for; `CheckOutcome` says what came back, and a family can be
selected and refuse. Every response-level claim reads the outcome's `Ran` / `Refused` / `NotSelected`, never the
selection's own lists.

**The one-ground collapse.** A whole call refuses with one error exactly when the refused families' grounds are ONE.
Distinct grounds are distinct answers, and collapsing them would return one and hide the other, so the caller would
fix it, retry and meet the next. The rule needs no special case for a single selected family — one family has one
ground, so it collapses anyway. Two grounds short-circuit the collapse rather than joining it: the shared-input
ground was decided before any family was dispatched, and the order-seam ground means the families disagreed about
which build they read, so no section is an answer worth keeping.

**Where a claim may stay a literal at its own site**, rather than living on the outcome: only where it reads one
field of the artifact that did the work, does no arithmetic at the site that prints it, and composes across no
second family and no second moment. What that leaves is each family's own head and rows, each family's boundary, the
accounting's emitted counts, the `limit=` and `max_chars=` echoes, `findings_defaulted`, and the overrun notice.

## Pinned by
- *A DELETED record has no live body*: the `deleted-link-walk-guard` probe (`CI: DeletedLinkWalkProbe`), which drives both a SEMANTIC arm — a
  deleted record with an intact body carrying a link, which must not be reported — and a CRASH arm — a deleted record
  whose lazy parse throws, which must not be accounted unscannable — against live controls in the errors sweep and the
  compact/merge scan.
- *The listing budget spends in two phases*: `CheckErrorsFamilyTests.Fact14_PhaseOrderSentence_OnlyOnTheCappedSweepWithBaselineFindings` — which
  pins the SENTENCE and its gating: the phase-order line renders on a capped sweep with baseline findings and is
  absent at a wide `limit=`. The phase order itself, the limit-independence of the totals, and the re-sort into load
  order are **not separately pinned**; a refactor that inverted the phases would leave that test green.
- *The baseline split*: `RecordsTypeArmTests.AnArmTypeFilterOnTheOffOrderSweepExaminesThatArmAlone`, which is the arm that
  makes this a contract: the file IS located and opened (it is in `OffOrderScanned`) and `BaseMastersSwept` is
  nonetheless empty, because a type-arm scope filtered every record out. Its control,
  `TheArmTheOffOrderFileHoldsIsExamined`, asserts the off-order base master DOES land in the set when the scope
  admits a record — the other half of "covers both lanes at once".
- *The baseline split*: `CheckErrorsFamilyTests.Fact14_BaselineLinePrintsOnlyWhereABaseMasterWasSwept_AndNamesThatSubset` and
  `Fact28_ExcludeNarrowingIsStated_AndAFullyExcludedBaseMasterLeavesNoBaselineLine` pin the rendered baseline line and
  its absence, but reach their negative arm by EXCLUDING the base master, so neither distinguishes
  opened-but-not-examined.
- *Family selection*: `CheckMergeProbe`'s `REGISTERED-IS-THE-MEMBERSHIP` (every registered family is askable) and
  `CLASS-TOKEN-ROUND-TRIP` (each class set the probe DRIVES — three errors sets, six of the eight scripts flag
  combinations — spells tokens the family parsers read back as the same set, which is the trip the merged tool hands
  each family its classes through; the probe's own label says "every class set", which is wider than what it walks).

## Where
- `src/housecarl-core/ErrorCheck.cs` — the errors family: `ErrorCheck.Run`, `ErrorCheckResult`, `PluginErrors`.
- `src/housecarl-core/DeletedRecordRule.cs` — `DeletedRecordRule.HasNoLiveBody`.
- `src/housecarl-core/SweepScope.cs` — the record scope, the class parsers (`SweepFindings`) and `FilterNote`.
- `src/housecarl-core/SweepExclusion.cs` — `exclude=`: `SweepExclusion.Resolve` and its tokens.
- `src/housecarl-core/SweepFamilies.cs` — `SweepFamilySelection` and `SweepFamilySelection.Registered`.
- `src/housecarl-mcp/RecordChecks.cs` — the service lanes, in the class `RecordChecks`, which the head builds over
  itself and reaches through one-line delegators: `CheckErrors`, `ValidateScripts`, `CheckDialogue`,
  `ValidateDialogue`, `CheckFaceGen`, `SweepScopeError`. `CheckFaceGen` lists which plugins a mod folder ships and
  resolves `plugins=` and `exclude=` here, not in `FaceGenCheck`, because core cannot see the MO2 composition.
- `src/housecarl-mcp/CheckTextRender.cs`, `CheckSentences.cs` and `CheckSweep.cs` — the check text render
  (`CheckTextRender`), the check sentences (`CheckSentences`), and the `CheckSweep` record the renders take.
- `src/housecarl-mcp/SweepSharedInput.cs` — the input refusals every family shares, checked once before the merged
  response; `SweepOffOrderScope.cs` — the `plugins=` split into active and off-order names, and its per-call memo
  `SweepOffOrderMemo`.
- `src/housecarl-mcp/FaceGenSweepRender.cs` — the facegen family's render in both transports.
- `src/housecarl-mcp/SweepDemand.cs` — what each subject of a merged response wants, measured before the render. Its
  contract is in [`render-budget.md`](render-budget.md), which covers the file.
- `src/housecarl-mcp/CheckOutcome.cs` — `CheckOutcome`, and `DialogueOutcome`, whose four seed words are in
  [`check-scripts-and-dialogue-families.md`](check-scripts-and-dialogue-families.md).
- `src/housecarl-mcp/CheckAccounting.cs` and `CheckArtifact.cs` — the response's omission accounting and the
  `to_file=` artifact; their budget contracts are in [`render-budget.md`](render-budget.md).
- `src/housecarl-mcp/CheckTools.cs` — `CheckTools.CheckTool`. Tool: `housecarl_check`.

What the area needs from outside itself is the members of `ICheckHost`, declared at the top of `RecordChecks.cs`:
the FormID door from the head, and the type filter relayed from reads until reads is its own class. The dialogue fold
is a static, `LoadOrderService.OpenDialogueFold`, so it is called directly. The members every area
shares come through the door it extends, `ILoadOrderHost` in `src/housecarl-mcp/LoadOrderHost.cs`
([`load-order-service.md`](load-order-service.md)).

## Related

- `docs/architecture/render-budget.md` — how a merged check response divides `max_chars`, and what its accounting
  may claim.
- `docs/architecture/check-family-tests.md` — which lane a check-family fact is driven from.

---
updated: 2026-09-18
covers: [src/housecarl-mcp-tests/CheckErrorsFamilyTests.cs, src/housecarl-mcp-tests/CheckErrorsFixtures.cs, src/housecarl-mcp-tests/CheckErrorsWorld.cs, src/housecarl-mcp-tests/CheckErrorsWorldTests.cs, src/housecarl-mcp-tests/ScriptsFamilyTests.cs, src/housecarl-mcp-tests/ScriptsFixtures.cs, src/housecarl-mcp-tests/EpochCheckSweepTests.cs, src/housecarl-mcp-tests/EpochWorld.cs, src/housecarl-mcp-tests/DialogueFamilyTests.cs, src/housecarl-mcp-tests/DialogueWorld.cs]
---
# The check families' tests: which lane a fact is driven from, and why

**Class:** LIVING. Subsystem: `src/housecarl-mcp-tests/{CheckErrorsFamilyTests, CheckErrorsFixtures,
CheckErrorsWorld, CheckErrorsWorldTests, ScriptsFamilyTests, ScriptsFixtures, EpochCheckSweepTests,
EpochWorld, DialogueFamilyTests, DialogueWorld}.cs`.

The errors, scripts and dialogue families of `housecarl_check` are asserted from **two** driving lanes, and
which lane a given fact uses is a decision, not a convenience. This file is the home of that decision, cited
from `CheckErrorsFixtures`, `ScriptsFixtures` and `CheckErrorsFamilyTests` under ADR 0001 (a terse comment is
terse because the knowledge has a public home).

Written with #486 PR 2, which converted the last 1.x-renderer-driven arms of these families into this project.

## The two lanes

**LIVE — a real service call over a synthetic MO2 world.** `Svc.CheckErrors(...)` /
`Svc.ValidateScripts(...)` / `Svc.CheckDialogue(...)` against `CheckErrorsWorld`, `ScriptsWorld`, `EpochWorld`
or `DialogueWorld`, rendered through `Wire.RenderCheck` / `JsonWire.RenderCheck` — byte-identically the entry
point `CheckTools` calls. This is the default, and most facts use it.

**DTO — a hand-shaped result rendered through the same renderer.** `CheckErrorsFixtures.Result(...)` /
`ScriptsFixtures.Result(...)` build an `ErrorCheckResult` / `ScriptCheckResult` directly and hand it to the
same `Wire.RenderCheck` / `JsonWire.RenderCheck`. The renderer under test is identical; only the input's
provenance differs.

## The rule

**Drive LIVE unless the world cannot produce the shape the fact is about.** A fact about what the SWEEP
computes must be live — a hand-shaped result would be asserting the fixture, not the engine. A fact about what
the RENDERER does with a given result may be DTO-driven, and must be whenever the shape is one a synthetic
world cannot be made to emit without engineering the failure itself.

The shapes that force the DTO lane today — the population, derived from every `Result(...)` call in
`CheckErrorsFamilyTests` and `ScriptsFamilyTests` (8 sites) plus the one place a result is hand-shaped
without going through `Result(...)` — `FactS13`'s `listing with { ExcludedPlugins = … }`, 9 sites in all —
each named in the test that uses it:

| shape | why the live world cannot make it |
|---|---|
| a per-record or per-plugin **scan error** (`PluginErrors.ScanError`, `UnscannableRecords`) | it needs Mutagen to throw on a record body mid-walk; a plugin crafted to do that is a fixture engineered around a library's internals, and it would re-break whenever Mutagen's parser changes |
| an **excluded-plugin roster** of a chosen size | the world has one unparseable plugin; a roster wide enough to be CUT needs several, and each is sixteen bytes of garbage carrying no other fact |
| a **row-width** pair differing only in one field's length (the floor arm) | the floor is a property of the render, not of any world; two worlds differing only in an EditorID's length would be two whole MO2 instances asserting one number |
| an **empty histogram axis** (a sweep that ran the walk and tallied nothing) | every world that carries findings tallies them; an axis that ran and found nothing needs a world with no findings at all, which then proves nothing else |
| a **cap band** wider than the world's own body | the band has to reach caps at which this world simply renders whole |

Everything else — totals, class exclusion, budget and cut accounting over real findings, the baseline split,
the histograms, the epoch stamps, the off-order qualifier, the dialogue merge — is live.

## What the DTO lane must not be used for

- **Never for a fact about the sweep's own computation.** `TotalDangling`, `BaselineDangling`,
  `RecordsWithScripts`, which properties the property filter keeps: hand-shaping the result makes the
  assertion circular. Those are live or they are nothing.
- **Never to avoid a world that is merely inconvenient to build.** The five rows above are the population; a
  sixth needs a reason written beside it, in the test.

## The fixture-known totals have their own test

`CheckErrorsWorldTests` pins `CheckErrorsWorld`'s `TotalDangling` / `BaselineDangling` / `ScannedPlugins`
against a live sweep. Every live fact test takes its numbers from those constants, so a drift in the world
fails in one place with a clear message rather than in every fact test at once. `ScriptsWorld` carries the
same arrangement in `ScriptsWorldTests` (landed in #486 PR 1).

## The lock facts build their own world

`DialogueFamilyTests`' three lock facts (`FactD3`, `FactD4a`, `FactD4b`) construct a `DialogueWorld` with
`new()` rather than taking the shared collection fixture, because a held file is unreadable to everything else
in the process. Each also calls `Svc.Stats()` once, unlocked, before taking the hold — see `DialogueWorld`'s
own doc and issue #353 for the behaviour that makes this necessary.

---

# The check families' contracts

The sections above are about which lane a fact is asserted from. The rest of this file is the home of the contracts
the check-family code itself cannot express, cited from `ErrorCheck`, `ScriptPropertyCheck`, `DeletedRecordRule`,
`EditorIdNearMiss`, `SweepScope`, `SweepExclusion`, `SweepFamilies` and `CheckOutcome` under ADR 0001.

## A DELETED record has no live body

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

**Pinned by** the `deleted-link-walk-guard` probe (`CI: DeletedLinkWalkProbe`), which drives both a SEMANTIC arm — a
deleted record with an intact body carrying a link, which must not be reported — and a CRASH arm — a deleted record
whose lazy parse throws, which must not be accounted unscannable — against live controls in the errors sweep and the
compact/merge scan.

## The integrity sweep's honest boundary

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

### The UntypedOwner VariableData exemption

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

### The listing budget spends in two phases

`limit=` is ONE counter, and spending it plugin by plugin in load order would let the vanilla baseline consume it at
index 0 — a plugin that collects an empty dangling list is dropped from the reports entirely, so its findings would
be absent with no per-plugin trace. The per-plugin sweep is therefore called in two phases: every non-base plugin
first, then the base-game masters on what is left. An off-order file swept in the same call is listed above them.

What each plugin FINDS is unchanged — the totals, the histograms and the missing-master reads never depend on
`limit=` — and the reports are re-sorted back into load order before they are returned, because which section comes
first is a load-order fact.

**Pinned by** `CheckErrorsFamilyTests.Fact14_PhaseOrderSentence_OnlyOnTheCappedSweepWithBaselineFindings` — which
pins the SENTENCE and its gating: the phase-order line renders on a capped sweep with baseline findings and is
absent at a wide `limit=`. The phase order itself, the limit-independence of the totals, and the re-sort into load
order are **not separately pinned**; a refactor that inverted the phases would leave that test green.

### The baseline split, and "swept" means examined

`BaselineDangling` is derived from the ONE by-source tally, so "is this plugin baseline" is asked in exactly one
place. `BaseMastersSwept` is the swept SUBSET, not a yes/no: a render saying "N of M come from the base-game
masters" has to name the ones it counted, and naming all five over a sweep that touched one states a figure about
four plugins it never opened.

That subset is built from what the link walk **examined**, not from what the sweep opened, which covers both lanes
at once: an off-order base master swept as a FILE counts, and a base master whose every record a record scope
filtered out does NOT — the sweep opened it and examined nothing in it. A swept baseline that came back CLEAN is
therefore a different fact from a sweep that never looked at one, and the two stay distinguishable.

**Pinned by** `RecordsTypeArmTests.AnArmTypeFilterOnTheOffOrderSweepExaminesThatArmAlone`, which is the arm that
makes this a contract: the file IS located and opened (it is in `OffOrderScanned`) and `BaseMastersSwept` is
nonetheless empty, because a type-arm scope filtered every record out. Its control,
`TheArmTheOffOrderFileHoldsIsExamined`, asserts the off-order base master DOES land in the set when the scope
admits a record — the other half of "covers both lanes at once".

`CheckErrorsFamilyTests.Fact14_BaselineLinePrintsOnlyWhereABaseMasterWasSwept_AndNamesThatSubset` and
`Fact28_ExcludeNarrowingIsStated_AndAFullyExcludedBaseMasterLeavesNoBaselineLine` pin the rendered baseline line and
its absence, but reach their negative arm by EXCLUDING the base master, so neither distinguishes
opened-but-not-examined.

### Null is "not computed", never "empty"

Every optional tally on `ErrorCheckResult` and `ScriptCheckResult` — both histograms, the by-source axis, and
`PluginErrors.InstalledButInactiveMasters` — is built only when the walk that fills it actually ran. An
empty-but-present collection would render as "nothing found" for a count never taken, so null means the question was
not asked and the render must fall back to the unqualified remedy.

The install-vs-enable split for missing masters is filled in by the layer that reads the MO2 composition, because
the core sweep knows the active order and stops there; a composition that cannot be read leaves that subset null.

### Which plugins LOST entries is not a sweep fact

It is a fact about the RESPONSE. Computed on the result it could only ever report the listing budget's own
omissions, leaving a plugin whose entries the budget listed and `max_chars` then dropped in no sentence at all. It
is therefore computed in `CheckAccounting` against what the response emitted, which covers both truncators at once.

## The script-property sweep's boundary

What counts as a finding is kept high-signal, so that a clean result is trustworthy:

- **UNBOUND OBJECT property** — an `Auto` property of a form/object type declared in the chain but absent from the
  VMAD. Unbound means `None` at runtime: the silent-no-op footgun. HIGH.
- **UNBOUND SCALAR with no initializer** — defaults to 0 / 0.0 / false / "". MEDIUM. A scalar that DOES carry a
  baked initializer is not flagged: it has the author's intended default, so leaving it unbound is correct.
- **BOUND-BUT-NULL object property** — in the VMAD with a null Object link AND not bound to a quest alias instead
  (`Alias < 0`, because an alias-bound property has a null Object by design). Advisory.

And the degraded modes, every one named rather than silent: `Auto` properties only, because full properties with
custom Get/Set handlers are code-driven; a script whose OWN `.pex` is unreadable is reported UNVERIFIABLE, and a
missing ANCESTOR truncates the chain with a named note while the properties that could be read are still checked; a
BSA that failed to read this build is surfaced, so a "not found" that may merely be unscanned is never an
authoritative absence. An unbound object property is a flag to VERIFY, not a proven defect: it is sometimes filled
by script at runtime.

### Unverifiable attachments ride through every filter

A script whose `.pex` could not be read might be the very one declaring the property being filtered for, so dropping
its note under a filter would turn "could not check" into a clean answer. They are outside `limit=` too. They are
therefore **collapsed** instead: a repeat of a note already listed for the same script class is counted in
`UnverifiableCollapsed` and in the total, and listed once — one unreadable class hits every record that attaches it,
and an uncapped wall of one sentence would push the findings the caller asked for past `max_chars`. A NAMELESS
attachment is exempt from the collapse, because it names no class and the record is the only identity the defect
has.

## Narrowing narrows the numbers — but not all of them

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

## The exclusion axis

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

## Family selection

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

**Pinned by** `CheckMergeProbe`'s `REGISTERED-IS-THE-MEMBERSHIP` (every registered family is askable) and
`CLASS-TOKEN-ROUND-TRIP` (each class set the probe DRIVES — three errors sets, six of the eight scripts flag
combinations — spells tokens the family parsers read back as the same set, which is the trip the merged tool hands
each family its classes through; the probe's own label says "every class set", which is wider than what it walks).

## Selection is not outcome

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

## The dialogue family's four seed words

Fixed here and nowhere else, so each has exactly one meaning wherever the response says it:

- **named** — how many seeds the caller wrote in `seeds=`.
- **reached** — how many of those the seed budget let this call actually try.
- **validated** — how many reached seeds produced a validation report.
- **unreachable** — how many reached seeds produced a named refusal instead. These are the `[X]` rows.

`named ≥ reached`, and the difference is the seed budget's cut — the one subtraction, taken once on
`DialogueOutcome`. `validated` and `unreachable` are each counted off the reached seeds independently rather than
asserted to sum to it.

## The EditorID near-miss hint

The winner lane of a records scan filters on the LOAD-ORDER WINNER's body, so a record whose winner RENAMES it is
invisible to `editorid = <the old name>`: the name the caller typed is real, it is simply carried by a losing copy.
That reads as a clean "0 matches", which is the one answer the scan must not leave standing unexplained.

**One sentence, and only a real one.** The look runs ONLY where that cause is the only one available — the scan's
own gate holds it to a zero-row, `types=`-bounded winner-lane scan whose `where=` is nothing but the exact
`editorid =` term. It reads the EDID header and nothing else, and it stops at the FIRST losing copy carrying the
name. No candidate, no sentence: the plain zero-row result stands as it did.

The walk is taken one plugin at a time, in load order, so the base game's own copy ends it early and a plugin that
indexed but will not open NOW skips rather than ending the stream — the hint must not switch itself off for a whole
order because one file moved. Out-of-memory and cancellation still leave by the same door the scan lane sends them
out of.

**Pinned by** `WhereNearMissTests`, whose arms cover the rename sentence, the winner that dropped its EditorID, and
each gate that must produce no sentence at all: a `contains` term, a second predicate, a `formids=` set,
`conflicts_only`, a `references=` filter, an aggregate, and an explicit plugin scope.

## Related

- `docs/architecture/test-project-fixtures.md` — the Papyrus `.pex` writer and the file-lock harness these
  tests are built on.
- `docs/architecture/render-budget.md` — how a merged check response divides `max_chars`, and what its accounting
  may claim.
- `docs/decisions/0005-the-1x-tools-are-deleted-not-deprecated.md` — "guards on a deleted tool die with the
  tool; behaviour that survives gets a fresh test", the rule #486 PR 2 discharges.

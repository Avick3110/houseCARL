---
updated: 2026-09-25
covers: [src/housecarl-core/DialogueValidate.cs, src/housecarl-core/DialogueCkParity.cs, src/housecarl-core/DialogueScriptCheck.cs, src/housecarl-core/DialogueCheck.cs, src/housecarl-mcp/DialogueSweep.cs, src/housecarl-mcp/DialogueSweepRender.cs, src/housecarl-mcp/DialogueKindChecks.cs]
---
# Dialogue validation: CK parity, what a clean pass means, and the check family

## What it is
What houseCARL checks before a dialogue record goes out, and what the dialogue family on `housecarl_check` may
claim. The merged INFO order, the fold and the SNAM marker are [`dialogue.md`](dialogue.md); the modder-facing
page is [`docs/dialogue.md`](../dialogue.md).

## Contracts

### CK parity

Mutagen omits a null/unset optional subrecord on write; the Creation Kit writes it unconditionally, nulls included.
A record authored through houseCARL that sets only the fields the author cared about therefore differs STRUCTURALLY
from a CK-authored one of the same content. `DialogueCkParity` closes that by default-populating the nullable fields
the CK always emits, at create time, inside the Mutagen model.

Three invariants hold for every default, and the DialogTopic SNAM marker follows the same pattern:

1. NON-OVERRIDE — fill only where the author left the field null; never clobber an explicit value.
2. NEVER SILENT — every fill returns a `CkParityFill` the create path surfaces as an `OpResult`.
3. BY CONSTRUCTION — the values are what a CK-authored record of the same content carries, byte-verified against
   vanilla reference plugins.

Every fill path and its read-only counterpart share one presence predicate, so a create that FILLS a field and a
validate that FLAGS its absence cannot disagree. `DialogueCkParityGuardProbe` pins the pairs.

Three tiers:

| tier | fields | consequence of omission |
|---|---|---|
| confirmed crash | INFO CNAM (FavorLevel), INFO ENAM (Flags); DLVW DNAM, ENAM | the Creation Kit crashes when the owning topic or the Dialogue Views editor is opened; the game tolerates it |
| byte parity | DLBR TNAM (Category); DIAL PNAM (Priority); QUST ANAM, objective FNAM, alias FNAM, reference-alias VTCK | a byte mismatch against a CK-authored record, no confirmed crash |
| in-game behaviour | DLBR DNAM (Flags) | an absent DNAM reads as `TopLevel`, so a branch the author never marked top-level is published to the player's menu |

There is no honest default for DLBR `Flags`, so the create path REFUSES a branch whose `Flags` no op set; why
neither value is honest, and what to pass, is on [`docs/dialogue.md`](../dialogue.md).

Two exceptions to the is-null signal:

- DIAL `Priority` is a non-nullable float, so "the author left it unset" cannot be read off the record. The create
  path decides it from the author's OP LIST and passes it in; an explicit value, `0` included, always wins. That is
  also why the validator never flags its absence — doing so would false-positive every legitimately priority-0 topic.
- Alias VTCK is scoped to REFERENCE aliases; a Location alias resolves to a place, not an actor, and the same gate
  guards the fill and the gap.

A `0`-fill materialises the subrecord only, so every named flag inside it — `OrWithPrevious` on an objective, the
alias flags, and the INFO `Flags` struct's `Goodbye`, which [`docs/dialogue.md`](../dialogue.md) covers — stays an
explicit authoring choice.

### What a clean pass means

`DialogueValidate` runs on demand over a whole topic resolved against the LOAD-ORDER WINNERS. The per-INFO body
checks walk the WINNING topic's child list, so an INFO another plugin contributes but this winner does not re-list is
not body-checked: a clean pass means "every line this winner lists is sound", not "every line in this topic is". The
effective ORDER view is the merge across all of them.

PNAM ABSENCE is never flagged — vanilla leaves it empty and selects intra-topic by Conditions — and only a SET but
unresolvable PNAM is reported. Deleted INFOs are skipped and tallied. Resolution scope is the active order;
validating within {plugin + its masters} alone is a deliberately deferred capability. The whole run is wrapped, so a
resolve or asset failure rides `CheckError` rather than being swallowed.

Standing limits a render must state rather than let "checks passed" read as "this will play": CTDA conditions are
semantic and only the game evaluates them, and lip-sync and audio content are outside the data layer.

Two ownership gates keep the noisy findings off content the modder neither wrote nor can act on. The SNAM Problem
escalation fires only where the winner IS the FormKey's defining master — a blank-SNAM override ships in working
mods, so an override is a Warning. The subtype-disagreement and unmodeled-marker warnings fire only on a record a
force-loaded plugin does not own, since a base master, a Creation Club plugin or `_ResourcePack.esl` carries
Bethesda's stale number and is not something the modder can act on. The subtype-disagreement warning carries a
second exemption the unmodeled-marker warning does not: an override that copies the base record's (Subtype, SNAM)
pair forward verbatim changed neither field, so it stays quiet, while an override of an unmodeled marker still
warns. The verdict rides the ungated `subtype_stale` and `subtype_from_marker` fields either way.

The condition lints are the data-layer-decidable subset and every one is a structural true positive; all emit
Warning. The load-bearing gate is the FLOI mode gate: a condition form parameter is a `FormLinkOrIndex`, a form only
when `UseAliases` and `UsePackageData` are both false. On the binary overlay an index-mode FLOI's `.Link` is a bogus
low FormKey synthesised from the index bytes rather than null, so reading it as a form would false-flag a well-formed
alias-mode gate. The dangling-parameter sweep reflects over the Data arm's properties rather than listing functions,
so it covers every function Mutagen models — the generated-coverage cornerstone.

Deliberately not linted, as semantic rather than structural: Run On Subject-vs-Target intent, the faction-rank gate
value, and intra-topic Info-variant ordering.

### The check family

The dialogue family on the merged `check` surface is SEEDED, not swept. Selection is by record — a quest expands into
every topic it owns — so `plugins=` and `exclude=` do not scope it, and the response says so. An empty seed list is a
REFUSAL, never a widening: resolving it to "the whole order" would run a whole-order dialogue sweep, which is refused
on cost. A seed that does not resolve is carried as a named refusal, never dropped, because the scope IS the seed
list and a discarded seed silently narrows it.

The effective merged INFO order is deliberately absent from this family. It is an ordered sequence over the
touching-plugin stack rather than a findings list, so it belongs to `records project=info_order`. The family's topic
block does not carry it, and `records project=info_order` is the only surface that computes it
(`DialogueValidate.InfoOrders`) and renders it (`DialogueWire.AppendInfoOrderView`).

Which checks a seed's kind runs comes from one table, `DialogueKindChecks`, read both by the seed's own verdict line
and by the family's boundary claim, so the two cannot disagree. An unrecognised kind claims nothing rather than
defaulting to the widest set. The same table decides what the epoch stamp names: all three asset-substrate verdicts
(`.fuz`, `.pex`, `.seq`) live behind the graph checks, so a call whose every seed was a DLVW or DLBR is record
substrate throughout and the stamp caveats nothing.

## Pinned by
- `DialogueCkParityGuardProbe` (`src/housecarl-generator`) — the fill and gap pairs; its `*-WINS` arms pin
  NON-OVERRIDE, its `*-AUTOFILL` arms NEVER SILENT, `DIAL-PNAM-WINS` an explicit `0` Priority, and
  `QUST-ALIAS-LOCATION` VTCK's scope to reference aliases.
- `DialogBranchFlagsRefusalTests` — a branch with no `Flags` is refused naming both values, and an explicit
  value, `0` included, lands.
- `DialogueValidateGuardProbe` (`src/housecarl-generator`) arms PNAM-DANGLE / PNAM-RESOLVES — only a set but
  unresolvable PNAM is reported; DELETED-SKIP — deleted INFOs are skipped and tallied; COND-ALIAS-FLOI — the FLOI
  mode gate; QUEST-FANOUT — a quest expands into the topics it owns.
- `DialogueFamilyTests.AVanillaTopicNoPluginTouchesIsLabelledButNotWarnedAbout`,
  `ACreationClubOverrideAuthoringTheMismatchIsNotWarnedAbout` and
  `AnOverrideInheritingAStaleSubtypeIsNotWarnedAbout` — the subtype-disagreement ownership gate and its second
  exemption; `TheJsonSweepFlagsAStaleSubtype` and `TheJsonSweepNamesTheSubtypeTheMarkerGives` — the ungated
  `subtype_stale` and `subtype_from_marker` fields.
- `DialogueFamilyTests.FactD4b_WinnerLockIsLoud` — the whole run is wrapped: a locked winner comes back as the
  named error, not swallowed. `FactD4a_DefinerLockIsLoud` — a locked definer makes `records project=info_order`
  refuse and name it (the check family does not read the definer for that fixture).
- `DialogueFamilyTests.FactV2_TheStampDeclaresItsBound` — a topic seed's stamp names the verdict classes it does
  not cover, on both transports, and an empty seed list is a refusal. The all-DLVW-or-DLBR arm is not asserted
  there.

## Where
- `src/housecarl-core/DialogueCkParity.cs` — the fills and their read-only gap checks.
- `src/housecarl-core/DialogueValidate.cs` — the whole-topic validator, including `CheckConditions`.
- `src/housecarl-core/DialogueScriptCheck.cs` — the result-script check the validator reuses.
- `src/housecarl-core/DialogueCheck.cs` — the family's result over a seed list.
- `src/housecarl-mcp/DialogueSweep.cs` — the family's orchestration: seeds to validations, and the tally.
- `src/housecarl-mcp/DialogueSweepRender.cs` — the family's section in both transports.
- `src/housecarl-mcp/DialogueKindChecks.cs` — which checks a seed's kind runs.
- Tool: `housecarl_check findings=["dialogue"]`.

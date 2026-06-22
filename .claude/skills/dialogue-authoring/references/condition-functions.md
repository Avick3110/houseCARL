# Condition functions (CTDA) — decode reference

How to **decode** a condition you read back from a record (`Conditions` on an INFO, a quest's
`DialogConditions`/`EventConditions`, an alias, a magic effect, a perk, …) — and how to compose one
correctly. A condition is the gate the running game evaluates to decide *when* a line fires or an alias
fills; houseCARL cannot evaluate it (only the game can), but it **can** read it back in full, and this
reference is how you turn the bytes into meaning.

**Two layers, two sources.** The *structure* below — which functions exist, each one's parameter shape, the
Run On options, the operators — is taken **by construction from Mutagen** (houseCARL's `mutagen-reference`),
so it is exactly what `housecarl_read_record` surfaces and what `housecarl_bulk_apply`/`housecarl_set_field`
compose. The *semantics* — what each function tests, the Run On scoping rules — are from the Creation Kit
wiki + modding practice (see `_CORPUS_STATUS.md` for provenance and the staleness duty). Confirm any exact
field path or arm name against `mutagen-reference` before composing a write.

## The shape of a condition (CTDA)

A `Condition` is a polymorphic struct with **two concrete arms** — which one is used depends only on what the
comparison value is:

| Arm (Mutagen) | `ComparisonValue` is… | Use |
|---|---|---|
| `ConditionFloat` | a `float` literal | the common case — compare against a number (`GetStage >= 20`) |
| `ConditionGlobal` | a `FormLink<Global>` | compare against a global variable's runtime value |

Both arms carry the same four pieces:

- **`CompareOperator`** — one of `EqualTo`, `NotEqualTo`, `GreaterThan`, `GreaterThanOrEqualTo`,
  `LessThan`, `LessThanOrEqualTo` (xEdit shows these as `=`, `!=`, `>`, `>=`, `<`, `<=`).
- **`ComparisonValue`** — the number (Float) or global (Global) the function's result is compared to.
- **`Data`** — the function and its parameters (the polymorphic `ConditionData`, below). This is the part
  that says *which* function and *what* it is pointed at.
- **`Flags`** — `OR` and `SwapSubjectAndTarget` (below).

**The function result is always compared to the value.** A condition is read as
`<function>(<params>) <operator> <value>`. `GetStage(MyQuest) >= 20` is `Data=GetStage(Quest=MyQuest)`,
`CompareOperator=GreaterThanOrEqualTo`, `ComparisonValue=20`. There is no "boolean" type — a yes/no
function like `GetIsID` is written `GetIsID(X) = 1` / `= 0`.

### AND / OR between rows — the `OR` flag

A condition *list* is rows evaluated top to bottom. The join is **per-row**, carried by the `OR` flag on
each row:

- **Flag clear (default) = AND** with the *next* row.
- **Flag set (`OR`) = OR** with the *next* row.

`OR` only chains **adjacent** rows — there are **no parentheses**. So a list can express a flat
AND-of-ORs run, but **not** `(A AND B) OR (C AND D)`; to gate on a real sum-of-products you must duplicate
the whole INFO, one copy per AND-clause (see the flow-model reference's *Authoring traps the model implies
(non-lint)* section). Reading a list,
walk it row by row and group consecutive `OR` rows.

### `SwapSubjectAndTarget`

The other `Condition.Flag`. When set on a row, that row's Run On **Subject** and **Target** are swapped for
that single evaluation — a way to ask a Subject-shaped function about the Target without changing the Run On
selector. Rare; note it when you see it because it inverts who the row is really testing.

## Run On — *which* object the function is evaluated against

The parameter says *what to compare to*; **Run On** says *who the function runs on*. They are independent,
and getting Run On wrong is the classic "correctly-wired line still plays wrong" bug — the condition passes
or fails against the wrong actor, silently. Mutagen's `RunOnType` enum has exactly these eight values:

| Run On (`RunOnType`) | The function is evaluated on… |
|---|---|
| `Subject` | **the line's speaker** (the NPC saying the dialogue). The default. |
| `Target` | **who the speaker is talking to** — in dialogue, normally the **player**. |
| `Reference` | a specific placed reference you pick — the `Reference` FormLink field on the Data. |
| `CombatTarget` | the Subject's current combat target. |
| `LinkedReference` | the reference linked from the Subject (optionally by a keyword). |
| `QuestAlias` | a reference alias of the owning quest — which alias is the `RunOnTypeIndex`. |
| `PackageData` | a reference supplied by the running package's data. |
| `EventData` | a reference supplied by the triggering event (story-manager / scene). |

**The dialogue lint this enables:** a function that asks about the *player* (e.g. "is the player wearing
X", "does the player have keyword Y") left on **Subject** silently tests the **NPC** instead — it should be
on **Target**. Conversely a check about the speaker (`IsSneaking`, the NPC's own faction) belongs on
**Subject**. (Confirmed: in dialogue "Target = who the NPC is talking to", "Subject = the NPC speaking" —
UESP CK wiki, *Dialogue Speech Checks*.) Run On `Reference`/`QuestAlias` carry an index/link (`Reference`,
`RunOnTypeIndex`) — a `QuestAlias` Run On whose index isn't a real alias of the owning quest is a dead
condition.

## Reading a function's parameters

Every `ConditionData` arm exposes its **function-specific parameter(s) first**, then a common block:
`Function` (the read-only selector), `RunOnType` + `RunOnTypeIndex`, `Reference` (used when
`RunOnType=Reference`), `UseAliases`, `UsePackageData`, and unused int/string parameter slots. When you read
a condition back, the arm's **name *is* the function** (e.g. Data type `GetStageConditionData` ⇒ function
`GetStage`), and the named non-common fields are the parameters.

A parameter is one of: a **FormLink** to a specific record type (so `GetIsID`'s `Object` must be a *base
object*, `GetStage`'s `Quest` must be a *QUST*, etc. — a link of the wrong type is malformed), an **enum**
(`GetActorValue`'s `ActorValue`, `GetIsSex`'s gender), or a **raw int/string** (`GetStageDone`'s stage
number, `GetIsAliasRef`'s alias index, `GetVMQuestVariable`'s variable name). A few functions take **no**
parameter and read state off the Run On object alone (`GetLevel`, `GetDead`, `GetRandomPercent`, …).

> **Composing a form-link param — it is a `FormLinkOrIndex`, not a plain link.** A condition's form-link
> parameter (`Object`, `Quest`, `Perk`, `ItemOrList`, `Global`, …) is a Mutagen `FormLinkOrIndex<T>`, which
> `housecarl_read_record` and the `mutagen-reference` schema **normalize to `FormLink<T>`** in their type
> display — the shown type understates it, because the target can be *either* a real FormID *or* a numeric
> quest-alias / package-data index. When you compose one, give it a **FormID `XXXXXX:Plugin.esp`** (form mode)
> or **`alias N` / `packdata N`** (index mode, for an alias-/package-relative target); houseCARL sets the arm's
> `UseAliases`/`UsePackageData` discriminator for you, and **both** the `compose` `fields:` shorthand and the
> nested `sets:` path accept it. Don't read the displayed `FormLink<T>` on a condition param as "plain link".

## Dialogue / quest condition functions (curated)

The functions that show up in quest and dialogue gating, with their **Mutagen parameter shape** (authoritative)
and what they test. The `…ConditionData` Data-arm name is `<Function>ConditionData`. Compare each with an
operator + value as shown in the "Reads as" notes.

### Quest progress & state
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetStage` | `Quest` → QUST | the quest's **current** highest-set stage. Window gate = **two rows**: `>= N` AND `<= M`. |
| `GetStageDone` | `Quest` → QUST, `Stage` → int | whether a **specific** stage index has **ever** been set (1/0) — true even after the quest moved past it. **Not** `GetStage`. |
| `GetQuestRunning` | `Quest` → QUST | 1 if the quest is currently running. |
| `GetQuestCompleted` | `Quest` → QUST | 1 if the quest is completed. |
| `GetVMQuestVariable` | `Quest` → QUST, `VariableName` → string | value of a script variable on the quest's VM (the variable must exist on the quest's script). |

### Speaker / actor identity
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetIsID` | `Object` → **base** form (NPC_/etc.) | 1 if the Run On reference's **base form** equals `Object`. Pass the **base NPC_**, not a placed REFR. For a leveled actor it compares the spawned ActorBase. |
| `GetIsAliasRef` | `ReferenceAliasIndex` → int | 1 if the Run On reference fills that **reference alias of the owning quest**. Index is quest-relative — wrong index = dead. |
| `GetIsRace` | `Race` → RACE | 1 if the Run On actor's race matches. |
| `GetIsSex` | `MaleFemaleGender` → enum (`Male`/`Female`) | 1 if the Run On actor's sex matches. |
| `GetIsVoiceType` | `VoiceTypeOrList` → VTYP or FormList | 1 if the Run On actor's voice type is (in) the form. |
| `GetIsObjectType` | `FormType` → enum | 1 if the Run On object is of that record/form type. |
| `GetIsCurrentPackage` | `Package` → PACK | 1 if the Run On actor's running package matches. |
| `GetDead` | *(none)* | 1 if the Run On actor is dead. |
| `GetTalkedToPC` | *(none)* | 1 if the Run On actor has talked to the player before. |

### Faction & relationship
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetInFaction` | `Faction` → FACT | 1 if the Run On actor is in the faction. |
| `GetFactionRank` | `Faction` → FACT | the Run On actor's **rank** in the faction. **Gate on `< 0`**, not `== 0` (rank −1 = not in faction; 0 is a real rank). |
| `GetRelationshipRank` | `TargetNpc` → actor ref | relationship rank between the Run On actor and the other actor. |

### Location
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetInCurrentLoc` | `Location` → LCTN | 1 if the Run On actor's current location is / is within `Location`. |
| `GetInCurrentLocAlias` | `LocationAliasIndex` → int | same, against a **location alias** of the owning quest. |

### Inventory & equipment
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetItemCount` | `ItemOrList` → item or FormList | count of the item (or any in the list) in the Run On actor's inventory. |
| `GetEquipped` | `ItemOrList` → item or FormList | 1 if the Run On actor has it equipped. |
| `GetGold` | *(none)* | the Run On actor's gold. |
| `HasKeyword` | `Keyword` → KYWD | 1 if the Run On **object** has the keyword. |

### Stats, magic & misc state
| Function | Parameter(s) | What it tests |
|---|---|---|
| `GetActorValue` | `ActorValue` → enum | the Run On actor's current value of that actor value. |
| `GetLevel` | *(none)* | the Run On actor's level. |
| `GetGlobalValue` | `Global` → GLOB | the value of a global (Run On is irrelevant — it's a global). |
| `GetRandomPercent` | *(none)* | a fresh random 0–99 **each evaluation** — for chance gates; never stable across re-checks. |
| `GetDeadCount` | `Npc` → base NPC_ | how many instances of that base actor are dead. |
| `GetSitting` | *(none)* | the Run On actor's sit/furniture state (a code, not a plain bool). |
| `GetIsAlerted` | *(none)* | 1 if the Run On actor is alerted. |
| `HasSpell` | `Spell` → SPEL | 1 if the Run On actor knows the spell (also abilities/diseases). |
| `HasPerk` | `Perk` → PERK | 1 if the Run On actor has the perk. |
| `HasMagicEffect` | `MagicEffect` → MGEF | 1 if the effect is active on the Run On actor. |
| `HasMagicEffectKeyword` | `Keyword` → KYWD | 1 if an active effect on the Run On actor has the keyword. |
| `IsInList` | `FormList` → FLST | 1 if the Run On object is in the form list. |

This is a **curated dialogue/quest subset**, not the full set — Mutagen models **every** condition function
(hundreds, including all the combat/VATS/weather/AI ones). To decode a function not in this table, read the
`ConditionData` arm name and its named fields directly, and look the function up in `mutagen-reference`
(the arm `<Function>ConditionData`) or the CK wiki.

## CTDA conditions vs Papyrus-only functions

A common authoring trap: reaching for a **Papyrus** function name as a condition. The two namespaces overlap
but are **not** the same set — a function being callable in a `.psc` does **not** make it a condition
function, and vice-versa. The condition-function set is **fixed and complete** (Mutagen models all of it by
construction), so the test is simple: **if a name is not a condition function, it cannot go in a CTDA.**

The frequent confusions are the `Is…` Papyrus methods vs the `Get…` condition functions:

| You want (Papyrus habit) | The **condition** function is | Note |
|---|---|---|
| `Actor.IsPlayerTeammate()` | **`GetPlayerTeammate`** | `IsPlayerTeammate` is **not** a condition — using it in a CTDA is impossible. |
| `Actor.IsInFaction(f)` | **`GetInFaction`** | the condition is `Get…`, not `Is…`. |
| `Actor.IsEquipped(x)` / `IsEquippedWeapon` | **`GetEquipped`** | |
| `Actor.IsDead()` | **`GetDead`** | |
| `ObjectReference.GetItemCount(x)` | **`GetItemCount`** | same name here — but confirm, don't assume. |

Many `Is…` names **do** exist as conditions (`IsSneaking`, `IsInCombat`, `IsHostileToActor`, `IsWeaponOut`,
…), so the rule isn't "Is = Papyrus, Get = condition" — it's **"only a name in the condition-function set is
a condition."** When unsure: check the arm exists in `mutagen-reference` (as `<Name>ConditionData`), or read
a known-good CTDA back and copy its function. Never hand-write a function the CK's own dropdown wouldn't
offer.

## Decoding a condition you read back — worked example

`housecarl_read_record` on an INFO with `Conditions` deep returns, per row, the arm + operator + value +
the Data arm + its params. Read this row:

```
ConditionFloat
  CompareOperator = GreaterThanOrEqualTo
  ComparisonValue = 20
  Data = GetStageConditionData { Quest = 001234:MyMod.esp, RunOnType = Subject }
  Flags = (none)
```

→ **"`GetStage(MyMod quest 001234) >= 20`, evaluated on the speaker."** Because `GetStage` reads the quest in
its parameter, the `Subject` Run On is harmless here (the result doesn't depend on the speaker). Contrast a
row whose function *does* depend on the Run On — `GetItemCount` on `Subject` counts the **NPC's** items, on
`Target` the **player's** — there the Run On is the whole meaning.

To **write** conditions, never hand-synthesize the encoded operator/comparison bytes — read a verified gate
back and replay its rows verbatim (the `bulk_apply` clone recipe in `SKILL.md`).

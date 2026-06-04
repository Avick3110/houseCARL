# KID Value Tables — flat lookups

Enumerations the prose files (`filters.md`, `traits.md`) reference. Marked **[desc]** (printed in the
Nexus #55728 description), **[source]** (KID C++), or **[lib]** (confirmed from **CommonLibSSE** —
`powerof3/CommonLibSSE@dev`, the library KID compiles against — where KID forwards a number it doesn't
itself enumerate). Every value here is from one of these — none are unverified recall.

---

## Effect archetypes [desc + source]

String-filter values for **Magic Effect / Spell / Enchantment / Scroll / Potion** (the effect's
archetype). Full list (`Cache.h` `Archetype::map`):

```
ValueMod · Script · Dispel · CureDisease · Absorb · DualValueMod · Calm · Demoralize · Frenzy ·
Disarm · CommandSummoned · Invisibility · Light · Darkness · NightEye · Lock · Open · BoundWeapon ·
SummonCreature · DetectLife · Telekinesis · Paralysis · Reanimate · SoulTrap · TurnUndead · Guide ·
WerewolfFeed · CureParalysis · CureAddiction · CurePoison · Concussion · ValueAndParts ·
AccumulateMagnitude · Stagger · PeakValueMod · Cloak · Werewolf · SlowTime · Rally · EnhanceWeapon ·
SpawnHazard · Etherealize · Banish · SpawnScriptedRef · Disguise · GrabActor · VampireLord
```

Used as a bare string in section 2, e.g. `Keyword = MagicAbsorb|Magic Effect|Absorb`.

---

## Spell Types — `ST(value)` [desc + lib]

| # | Type | | # | Type |
|---:|---|---|---:|---|
| 0 | Spell | | 7 | Potion |
| 1 | Disease | | 8 | Ingredient |
| 2 | Power | | 9 | LeveledSpell |
| 3 | LesserPower | | 10 | Addiction |
| 4 | Ability | | 11 | VoicePower |
| 5 | Poison | | 12 | StaffEnchantment |
| 6 | Enchantment | | 13 | Scroll |

---

## Magic Schools — `<av>` for school/skill traits [desc]

The actor-value number naming a magic school (used by the MagicEffect `<av>(min/max)` trait, the
Spell/Book `<av>` trait, and by name in string filters).

| AV# | School |
|---:|---|
| -1 | None |
| 18 | Alteration |
| 19 | Conjuration |
| 20 | Destruction |
| 21 | Illusion |
| 22 | Restoration |

## Skill Actor Values — name ↔ number

For traits that take a numeric AV (Book `<av>`, Furniture `US()`, MagicEffect `R()`) **use the number**;
for section-2 actor-value **string** filters use the **name**. Schools 18–22 are **[desc]**; every index
here is **[lib]**-confirmed.

| AV# | Name | | AV# | Name |
|---:|---|---|---:|---|
| 6 | OneHanded | | 15 | Sneak |
| 7 | TwoHanded | | 16 | Alchemy |
| 8 | Marksman (Archery) | | 17 | Speechcraft |
| 9 | Block | | 18 | Alteration |
| 10 | Smithing | | 19 | Conjuration |
| 11 | HeavyArmor | | 20 | Destruction |
| 12 | LightArmor | | 21 | Illusion |
| 13 | Pickpocket | | 22 | Restoration |
| 14 | Lockpicking | | 23 | Enchanting |

> **Other actor values** (attributes, conditions, etc.) are accepted **by name** in section-2 string
> filters — KID's full AV-name set is the standard CommonLibSSE list (`Cache.h` `ActorValue::map`,
> ~160 names).

### Resistances — for the MagicEffect `R(value)` trait [lib]

`R(value)` takes a numeric actor value (the effect's *Resist Value*). The resist AVs, with the name KID
also accepts in a section-2 string filter:

| AV# | Resist | KID name | | AV# | Resist | KID name |
|---:|---|---|---|---:|---|---|
| 39 | Damage | `DamageResist` | | 43 | Frost | `FrostResist` |
| 40 | Poison | `PoisonResist` | | 44 | Magic | `MagicResist` |
| 41 | Fire | `FireResist` | | 45 | Disease | `DiseaseResist` |
| 42 | Shock | `ElectricResist` | | | | |

---

## Delivery — `D(value)` [lib]

| # | Delivery |
|---:|---|
| 0 | Self |
| 1 | Touch |
| 2 | Aimed |
| 3 | Target Actor |
| 4 | Target Location |

## Casting Type — `CT(value)` [lib]

| # | Casting Type |
|---:|---|
| 0 | Constant Effect |
| 1 | Fire and Forget |
| 2 | Concentration |
| 3 | Scroll |

---

## Soul Sizes — `SOUL(size)` / `GEM(size)` [desc]

| # | Size |
|---:|---|
| 1 | Petty |
| 2 | Lesser |
| 3 | Common |
| 4 | Greater |
| 5 | Grand |

## Furniture Types — `T(value)` [desc]

| # | Type |
|---:|---|
| 0 | Perch |
| 1 | Lean |
| 2 | Sit |
| 3 | Sleep |

## Bench Types — `BT(value)` [desc]

| # | Bench |
|---:|---|
| 1 | CreateObject |
| 2 | SmithingWeapon |
| 3 | Enchanting |
| 4 | EnchantingExperiment |
| 5 | Alchemy |
| 6 | AlchemyExperiment |
| 7 | SmithingArmor |

---

## Armor Body Slots — single number `30`–`61` [lib]

An Armor trait that is a lone number is a **biped object slot** (slot N → bit `1<<(N-30)`, from
`BIPED_MODEL::BipedObjectSlot`). KID accepts **30–61**. Slots 44–61 are the "extra"/mod slots — mods
reuse them freely (e.g. 44 is a common mask slot), so confirm the actual armor in xEdit:

| Slot | Part | | Slot | Part |
|---:|---|---|---:|---|
| 30 | Head | | 46 | Chest (primary) |
| 31 | Hair | | 47 | Back |
| 32 | Body | | 48 | Misc 1 |
| 33 | Hands | | 49 | Pelvis (primary) |
| 34 | Forearms | | 50 | Decapitate Head |
| 35 | Amulet | | 51 | Decapitate |
| 36 | Ring | | 52 | Pelvis (secondary) |
| 37 | Feet | | 53 | Leg (right) |
| 38 | Calves | | 54 | Leg (left) |
| 39 | Shield | | 55 | Face Jewelry |
| 40 | Tail | | 56 | Chest (secondary) |
| 41 | Long Hair | | 57 | Shoulder |
| 42 | Circlet | | 58 | Arm (left) |
| 43 | Ears | | 59 | Arm (right) |
| 44 | Mouth | | 60 | Misc 2 |
| 45 | Neck | | 61 | FX01 |

---

## Defaults (when a section is blank / absent)

| Section | Default |
|---|---|
| Type (1) | none — nothing to match |
| Filters (2) | none → matches every item of the type |
| Traits (3) | none → no narrowing |
| Chance (4) | **100** (always distribute) |

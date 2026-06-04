# SkyPatcher Grammar — Shared Value Tables

Enumerations referenced by more than one record type. Per-record files point here instead of
repeating these lists. Record-specific value lists (weapon animation types, projectile types,
armor/ammo/book/faction flags, etc.) stay in their own record file.

All values are lowercase, as SkyPatcher expects them.

---

## Cast type

Used by: Magic Effect (`castingType`, `restrictToCastingType`), Spell / Scroll / Enchantment
(`castType`).

```
concentration
constanteffect
fireandforget
scroll
```

## Delivery type

Used by: Magic Effect (`restrictToDeliveryType`).

```
aimed
self
targetactor
targetlocation
touch
```

## Resist type

Used by: Magic Effect (`restrictToResistType`).

```
resistdisease
resistfire
resistfrost
resistmagic
resistshock
```

## Magic-effect sound slots

Used by: Magic Effect (`filterBySounds`, `soundsToChange`).

```
drawsheathe
charge
readyloop
release
castloop
hit
```

## Skills

The 18 actor skills. Used by: Weapon (`skillType`), Book (`teachSkill`). Weapon's
`filterBySkills` / `restrictToSkills` accept only the combat-relevant subset (onehanded,
twohanded, marksman, destruction, illusion, conjuration, alteration, restoration).

```
alchemy
alteration
block
conjuration
destruction
enchanting
heavyarmor
illusion
lightarmor
lockpicking
marksman          ; archery
onehanded
pickpocket
restoration
smithing
sneak
speechcraft       ; speech
twohanded
```

## Soul types

Used by: Soul Gem (`currentSoul`, `soulCapacity`).

```
petty
lesser
common
greater
grand
null              ; sets to empty
```

## Biped slot index

Armor and NPC patchers reference biped slots **by index** (0–31), not by the 30–61 slot
numbers shown in xEdit. Index = slot number − 30 (slot 30 → index 0, slot 41 → index 11).
Used by: Armor (`filterByBipedSlots*`, `restrictToBipedSlots`, `bipedSlotsToAdd`,
`bipedSlotsToRemove`), and biped-slot filtering generally.

| Index | Slot | Index | Slot |
|---|---|---|---|
| 0 | Head | 16 | ModChestPrimary |
| 1 | Hair | 17 | ModBack |
| 2 | Body | 18 | ModMisc1 |
| 3 | Hand | 19 | ModPelvisPrimary |
| 4 | Forearms | 20 | DecapitateHead |
| 5 | Amulet | 21 | Decapitate |
| 6 | Ring | 22 | ModPelvisSecondary |
| 7 | Feet | 23 | ModLegRight |
| 8 | Calves | 24 | ModLegLeft |
| 9 | Shield | 25 | ModFaceJewelry |
| 10 | Tail | 26 | ModChestSecondary |
| 11 | LongHair | 27 | ModShoulder |
| 12 | Circlet | 28 | ModArmLeft |
| 13 | Ears | 29 | ModArmRight |
| 14 | ModMouth | 30 | ModMisc2 |
| 15 | ModNeck | 31 | FX01 |

## Magic Effect archetypes

Used by: Magic Effect (`filterByArchetypes`).

```
absorb              accumulatemagnitude   valuemodifier       script
dispel              curedisease           dualvaluemodifier   calm
demoralize          frenzy                disarm              commandsummoned
invisibility        light                 darkness            nighteye
lock                open                  boundweapon         summoncreature
detectlife          telekinesis           paralysis           reanimate
soultrap            turnundead            guide               werewolffeed
cureparalysis       cureaddiction         curepoison          concussion
valueandparts       stagger               peakvaluemodifier   cloak
werewolf            slowtime              rally               enhanceweapon
spawnhazard         etherealize           banish              spawnscriptedref
disguise            grabactor             vampirelord
```

## Actor values

Used by: Magic Effect (`restrictToActorValue`), and anywhere an actor value is named.

```
aggression          confidence            energy              morality
mood                assistance            onehanded           twohanded
archery             block                 smithing            heavyarmor
lightarmor          pickpocket            lockpicking         sneak
alchemy             speech                alteration          conjuration
destruction         illusion              restoration         enchanting
health              magicka               stamina             healrate
magickarate         staminarate           speedmult           inventoryweight
carryweight         criticalchance        meleedamage         unarmeddamage
mass                voicepoints           voicerate           damageresist
poisonresist        resistfire            resistshock         resistfrost
resistmagic         resistdisease         perceptioncondition endurancecondition
leftattackcondition rightattackcondition  leftmobilitycondition rightmobilitycondition
braincondition      paralysis             invisibility        nightvision
wardpower           rightitemcharge       armorperks          shieldperks
warddeflection      variable01            variable02          variable03
variable04          variable05            variable06          variable07
variable08          variable09            variable10          bowstaggerbonus
telekinesis         favoractive           favorsperday        favorsperdaytimer
leftitemcharge      absorbchance          blindness           fame
infamy              jumpingbonus          onehandedmodifier   twohandedmodifier
marksmanmodifier    blockmodifier         smithingmodifier    heavyarmormodifier
lightarmormodifier  pickpocketmodifier    lockpickingmodifier sneakingmodifier
alchemymodifier     speechcraftmodifier   alterationmodifier  conjurationmodifier
destructionmodifier illusionmodifier      restorationmodifier enchantingmodifier
dragonrend          attackdamagemult      healratemult        magickaratemult
staminaratemult     werewolfperks         vampireperks        grabactoroffset
grabbed             deprecated05          reflectdamage
```

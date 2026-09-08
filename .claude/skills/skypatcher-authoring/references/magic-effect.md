# Magic Effect Patcher — MGEF

`SkyPatcher/magicEffect/` · `iEnableMagicEffectPatching` · primary filter `filterByMgefs`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

## Filters

- `filterByMgefs` / `…Excluded` — target magic effects by form.
- `filterByModNames` / `…Excluded` — restrict by / exclude source plugin.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.
- `filterByArchetypes=<archetype>` — narrow to effect archetype(s). Values → `value-tables.md` (archetypes).
- `filterBySounds=<form>` — match effects with a given sound in any slot (slots → `value-tables.md`).
- `effectShadersExcluded=<form>` — skip effects carrying a given effect shader.
- `modNamesLastOverriddenExcluded=<plugin,…>` — skip records whose **last override** is from a
  named mod (yield to that mod instead of fighting its override).
- **restrictTo… (post-match):**
  - `restrictToCastingType=<castType>` — cast type (`value-tables.md`).
  - `restrictToDeliveryType=<deliveryType>` — delivery type (`value-tables.md`).
  - `restrictToActorValue=<actorValue>` — actor value (`value-tables.md`).
  - `restrictToResistType=<resistType>` — resist type (`value-tables.md`).
  - `restrictToAreaEffects=true` · `restrictToDetrimentalEffects=true`.

## Operations

- `fullName=~Name~`.
- `minimumSkillLevel=<n>` · `spellmakingCastingTime=<n>` · `spellmakingArea=<n>`.
- `baseCost=<n>` · `skillUsageMult=<n>`.
- `castingType=<castType>` — see `value-tables.md`.
- `projectile=<form>` · `enchantArt=<form>` · `castingArt=<form>` · `impactDataSet=<form>`.
- `enchantShader=<form>` (or `null` to remove) · `hitShader=<form>`.
- `removeEdgeGlow` — remove the effect's shader edge glow.
- `perkToApply=<form>` (or `null` to remove).
- `setFlags` / `removeFlags` — comma-separated, from the magic-effect flags below.
- `keywordsToAdd` / `keywordsToRemove`.
- `soundsToChange=<form>~<slot>[, null~<slot>]` — replace (or `null` to remove) a sound by slot.
  Slots → `value-tables.md` (magic-effect sound slots).

**Magic-effect flags** (legal values for `setFlags`/`removeFlags`):

```
hostile        recover         detrimental    snaptomesh    nohitevent
dispelwithkeywords   noduration   nomagnitude   noarea       fxpersist
goryvisuals    hideinui        norecast       poweraffectsmagnitude
poweraffectsduration   painless   nohiteffect   nodeathdispel
```

## Examples

```ini
filterByArchetypes=cloak
filterByMgefs=Skyrim.esm|4822:restrictToCastingType=fireandforget
filterByMgefs=Skyrim.esm|4822:restrictToActorValue=health
filterByMgefs=Skyrim.esm|4822:restrictToResistType=resistdisease
filterBySounds=Skyrim.esm|0003C8FA:soundsToChange=Skyrim.esm|0003C8F9~release, null~castloop
filterByMagicEffects=Skyrim.esm|12FD0:hitShader=Skyrim.esm|3F2C3
perkToApply=null
modNamesLastOverriddenExcluded=SomePatch.esp, MyBalanceOverhaul.esp
```

# NPC Patcher — NPC_

`SkyPatcher/npc/` · `iEnableNPCPatching` · primary filter `filterByNpcs`
Shared mechanics → `grammar-core.md`. Shared enums → `value-tables.md`.

The richest patcher. Runtime application to already-spawned actors is governed by `iUpdateNPC`
/ `iRefreshNPCStats` (`grammar-core.md` §8). **No filter → all NPCs except the player;** patch the
player explicitly with `filterByNpcs=Skyrim.esm|7`.

## Filters

- `filterByNpcs` / `…Excluded` — target NPCs by form.
- `filterByModNames` — by source plugin.
- `filterByRaces` — by race.
- `filterByDefaultOutfits` — by default outfit.
- `filterByFactions` / `…Or` / `…Excluded` — by faction.
- `filterByClass` / `filterByClassExclude` — by class.
- `filterByKeywords` / `…Or` / `…Excluded`.
- `filterByEditorIdContains` / `…Or` / `…Excluded`.
- `filterByGender=<female|male>`.
- Boolean flag filters: `filterByPCLevelMult`, `filterByAutoCalc`, `filterByEssential`,
  `filterByProtected` (e.g. `filterByPCLevelMult=true`).
- **restrictTo… (post-match):** `restrictToRaces` · `restrictToGender=<male|female>` ·
  `restrictToVoiceType=<form>` · `restrictToFlags=<flag>` (NPC actor flags) ·
  `restrictToTemplateFlags=<traits,stats,factions,…>` · `restrictToMaleModelContains=<text>`
  (skeleton path substring, e.g. `werewolf`) · `restrictToSkill=<skill~min~max>` ·
  `rvsRestrictToTraits=true` (limit `setRandomVisualStyle` to matching-trait NPCs).

## Operations

### Names & visuals
- `fullName=~Name~` · `shortName=~Short~`.
- `copyVisualStyle=<form>` — copy face+hair from another NPC. **Match gender & race too** (also
  patchable here) or crashes can occur.
- `setRandomVisualStyle=<formlist[~chance][~copyVoice]>` — pick a random style from a form list
  (race, sex, skin, face, hair, height, weight, body tint, optional voice). `chance` is a %;
  `~true`/`~false` controls voice copy (omit the voice part to never copy). Saved with the save.
- `addRandomVisualStyle=<formlist>` — add styles instead of replacing.

### Level & stats
- `level=<n>` · `calcLevelMin=<n>` · `calcLevelMax=<n>` (with kPCLevelMult).
- `setPcLevelMult=<false=<level>>` — toggle kPCLevelMult; the level is the failsafe used when
  none can be calculated.
- `setAutoCalcStats=<yes|no|true|false|none>` · `setEssential=…` · `setProtected=…`
  (`none` = leave unchanged).
- `levelRange=<min~max>` — scales level-ranged values to the NPC's level (pairs with `changeStats`).
- `changeStats=<…>` — `health/stamina/magicka=<n>` (direct) · `calcHealth/calcStamina/calcMagicka=<n>`
  (by class+level — not on leveled NPCs) · `healthMult/staminaMult/magickaMult=<n>` ·
  ranged form `health=625~900` with `levelRange`.
- `healthBonus` / `magickaBonus` / `staminaBonus` — bonus applied when using the matching `calc…`.
- `changeSkills=<skill=val,…>` — skills → `value-tables.md`.

### Flags
- `setFlags` / `removeFlags` — NPC actor flags (list below).
- `setTemplateFlags` / `removeTemplateFlags` — template flags (list below).

```
NPC flags:  female  essential  ischargenfacepreset  respawn  autocalcstats  unique
            doesntaffectstealthmeter  pclevelmult  usestemplate  calcforalltemplates
            protected  norumors  summonable  doesntbleed  bleedoutoverride
            oppositegenderanims  simpleactor  loopedscript  noactivation  loopedaudio
            isghost  invulnerable

Template flags:  traits  stats  factions  spells  aidata  aipackages  unused  basedata
                 inventory  script  aidefpacklist  attackdata  keywords  copiedtemplate
```

### Inventory, factions, perks, spells
- `objectsToAdd=<form=count,…>` — add items/LLs (FormID only). `addOnceToInventory=<form~count,…>`
  (only if absent). `objectsToReplace=<formA~formB>` · `objectsToRemove=<form,…>` ·
  `removeInventoryObjectsByKeywords=<form,…>` · `removeInventoryObjectsByCount=<form~count>` ·
  `clearInventory=true`.
- `factionsToAdd=<faction=rank,…>` (rank after `=`, 0 if unsure; FormID only) · `factionsToRemove=<form,…>`.
- `keywordsToAdd` / `keywordsToRemove`.
- `perksToAdd=<form,…>`.
- `spellsToAdd` / `spellsToRemove` · `levSpellsToAdd` / `levSpellsToRemove` · `shoutsToAdd` / `shoutsToRemove`.

### Identity & appearance refs
- `race=<form>` · `voiceType=<form>` · `class=<form>` · `skin=<form>` (`null` disables) ·
  `deathItem=<form>` (`null` disables) · `outfitDefault=<form>` · `outfitSleep=<form>` ·
  `weight=<n>` · `height=<n>`.

### AI
- `setAggression=<calmed|unaggressive|aggressive|veryaggressive|frenzied>`.
- `setAssistance=<helpsnobody|helpsallies|helpsfriends>`.
- `setConfidence=<cowardly|cautious|average|brave|foolhardy>`.
- `setMood=<neutral|angry|fear|happy|sad|surprised|puzzled|disgusted>`.
- `setMorality=<anycrime|violenceagainstenemy|propertycrimeonly|nocrime>`.
- `aggressionRadiusBehavior=<true|false>` · `aggressionRadiusRanges=<attack~N, warn~N, attackandwarn~N>`.

### Attack data
- `attackDataToAdd` / `attackDataToChange` / `attackDataToRemove` — full spec (options, flags,
  defaults) in `records/race-hook.md`; same syntax here.

## Examples

```ini
; Add two perks to all NPCs (no filter = everyone but the player):
perksToAdd=SkyValor.esp|1D8A,SkyValor.esp|3DE9

; Patch the player:
filterByNpcs=Skyrim.esm|7:changeStats=health=250

; Bears: scale health by level 1-30, recalc stamina/magicka by class, unlevel to 50, no autocalc:
filterByRaces=Skyrim.esm|000131E8:levelRange=1~30:changeStats=health=625~900,calcStamina=10,calcMagicka=10:setPcLevelMult=false=50:setAutoCalcStats=false

; Random visual style from a form list, 100% chance, copy voice:
filterByFactions=Skyrim.esm|0001BCC0:setRandomVisualStyle=Test_Styles.esp|FE000800~100~true

factionsToAdd=Skyrim.esm|0001CBED=0
filterByNpcs=Skyrim.esm|13BBF:objectsToAdd=Skyrim.esm|73F34=5, Skyrim.esm|398F3=5
setFlags=female, essential, unique
```

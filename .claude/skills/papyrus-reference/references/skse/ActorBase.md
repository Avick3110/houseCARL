# `ActorBase`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `GetClass() → class`

**Flags:** Native

Returns this actor's class

### `GetCombatStyle() → CombatStyle`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC
get/set the CombatStyle of the actor

### `GetDeadCount() → Int`

**Flags:** Native

Gets the number of actors of this type that have been killed

### `GetFaceMorph(index) → Float`

**Flags:** Native

Get/Set actors face morph value by index

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetFacePreset(index) → Int`

**Flags:** Native

Get/Set actors facemorph preset by index
0 - Nose
1 - ??
2 - Mouth
3 - Eyes

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `index` | `Int` | ✓ |  |

### `GetFaceTextureSet() → TextureSet`

**Flags:** Native

returns the face textureset of the actor (Player Only?)

### `GetGiftFilter() → FormList`

**Flags:** Native

Returns this actor's gift filter formlist

### `GetHairColor() → ColorForm`

**Flags:** Native

### `GetHeight() → Float`

**Flags:** Native

Get/Set the actors body height

### `GetIndexOfHeadPartByType(type) → Int`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `type` | `Int` | ✓ |  |

### `GetIndexOfOverlayHeadPartByType(type) → Int`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `type` | `Int` | ✓ |  |

### `GetNthHeadPart(slotPart) → HeadPart`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotPart` | `Int` | ✓ |  |

### `GetNthOverlayHeadPart(slotPart) → HeadPart`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `slotPart` | `Int` | ✓ |  |

### `GetNthSpell(n) → Spell`

**Flags:** Native

returns the specified spell defined in the base actor  form

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `n` | `Int` | ✓ |  |

### `GetNumHeadParts() → Int`

**Flags:** Native

Get/Set actors HeadPart by index

### `GetNumOverlayHeadParts() → Int`

**Flags:** Native

These functions are READ-ONLY they are for accessing the
HeadPart list when the ActorBase's Race has been overlayed
with another race (e.g. Vampires)

### `GetOutfit(bSleepOutfit) → Outfit`

**Flags:** Native

Get the Outfit of the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `bSleepOutfit` | `Bool` |  | `false` |

### `GetRace() → Race`

**Flags:** Native

Returns this actor's race

### `GetSex() → Int`

**Flags:** Native

Returns this actor's sex. Values for sex are:
-1 - None
0 - Male
1 - Female

### `GetSkin() → Armor`

**Flags:** Native

Gets/sets the skin of the actorbase

### `GetSkinFar() → Armor`

**Flags:** Native

Gets/sets the far away skin of the actorbase

### `GetSpellCount() → Int`

**Flags:** Native

returns the number of spells defined in the base actor form

### `GetTemplate() → ActorBase`

**Flags:** Native

Gets the root template of the ActorBase

### `GetVoiceType() → VoiceType`

**Flags:** Native

Gets/sets the Actor's voicetype

### `GetWeight() → Float`

**Flags:** Native

Get/Set the actors body weight

### `IsEssential() → Bool`

**Flags:** Native

Is this actor essential?

### `IsInvulnerable() → Bool`

**Flags:** Native

Is this actor invulnerable?

### `IsProtected() → Bool`

**Flags:** Native

Is this actor protected (can only be killed by player)?

### `IsUnique() → Bool`

**Flags:** Native

Is this actor base unique?

### `SetClass(c)`

**Flags:** Native

set the Class of the actor

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `c` | `Class` | ✓ |  |

### `SetCombatStyle(cs)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `cs` | `CombatStyle` | ✓ |  |

### `SetEssential(abEssential)`

**Flags:** Native

Sets this actor as essential or not - if set as essential, will UNSET protected

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abEssential` | `Bool` |  | `true` |

### `SetFaceMorph(value, index)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Float` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `SetFacePreset(value, index)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `value` | `Int` | ✓ |  |
| `index` | `Int` | ✓ |  |

### `SetFaceTextureSet(textures)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `textures` | `TextureSet` | ✓ |  |

### `SetHairColor(color)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `color` | `ColorForm` | ✓ |  |

### `SetHeight(height)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `height` | `Float` | ✓ |  |

### `SetInvulnerable(abInvulnerable)`

**Flags:** Native

Sets this actor as invulnerable or not

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abInvulnerable` | `Bool` |  | `true` |

### `SetNthHeadPart(headPart, slotPart)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `headPart` | `HeadPart` | ✓ |  |
| `slotPart` | `Int` | ✓ |  |

### `SetOutfit(akOutfit, abSleepOutfit)`

**Flags:** Native

Sets the actors outfit

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akOutfit` | `Outfit` | ✓ |  |
| `abSleepOutfit` | `Bool` |  | `false` |

### `SetProtected(abProtected)`

**Flags:** Native

Sets this actor as protected or not - if set as protected, will UNSET essential

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `abProtected` | `Bool` |  | `true` |

### `SetSkin(skin)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skin` | `Armor` | ✓ |  |

### `SetSkinFar(skin)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `skin` | `Armor` | ✓ |  |

### `SetVoiceType(nVoice)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `nVoice` | `VoiceType` | ✓ |  |

### `SetWeight(weight)`

**Flags:** Native

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `weight` | `Float` | ✓ |  |

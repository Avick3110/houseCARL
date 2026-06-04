# `Spell`

**Source:** `vanilla` • **Extends:** `Form` • **Flags:** Hidden

---

## Functions

### `Cast(akSource, akTarget)`

**Flags:** Native

Cast this spell from an ObjectReference, optionally toward another.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akTarget` | `ObjectReference` |  |  |

### `IsHostile() → Bool`

**Flags:** Native

Is this spell classified as hostile?

### `Preload()`

**Flags:** Native

Preload the art for this spell. Useful for spells you equip & unequip on the player.
Warning: Misuse of this function can lead to erroneous behavior as well as excessive
memory consumption. It's best to avoid using this. This function will likely be
deprecated in the future.

### `RemoteCast(akSource, akBlameActor, akTarget)`

**Flags:** Native

Cast this spell from an ObjectReference, optionally toward another, and blame it on a particular actor.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |
| `akBlameActor` | `Actor` | ✓ |  |
| `akTarget` | `ObjectReference` |  |  |

### `Unload()`

**Flags:** Native

Unload the art for this spell. Call this only if you've previously called Preload.
Warning: Misuse of this function can lead to erroneous behavior including spell art
being unloaded while in use, and excessive memory consumption. It's best to avoid using this.
This function will likely be deprecated in the future.

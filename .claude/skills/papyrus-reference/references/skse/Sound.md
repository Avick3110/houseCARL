# `Sound`

**Source:** `skse` (Skyrim Script Extender x64) • **Extends:** `Form` • **Flags:** Hidden

**Imports:** `ObjectReference`

---

## Global Functions

### `SetInstanceVolume(aiPlaybackInstance, afVolume)`

**Flags:** Native Global

Set the volume of a given playback instance of a sound. Clamped between 0 and 1.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiPlaybackInstance` | `Int` | ✓ |  |
| `afVolume` | `Float` | ✓ |  |

### `StopInstance(aiPlaybackInstance)`

**Flags:** Native Global

Stops a given playback instance of a sound

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `aiPlaybackInstance` | `Int` | ✓ |  |

---

## Functions

### `GetDescriptor() → SoundDescriptor`

**Flags:** Native

SKSE64 additions built 2024-01-17 20:01:40.731000 UTC

### `Play(akSource) → Int`

**Flags:** Native

Play this sound base object from the specified source

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |

### `PlayAndWait(akSource) → Bool`

**Flags:** Native

Play this sound from the specified source, and wait for it to finish

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `akSource` | `ObjectReference` | ✓ |  |

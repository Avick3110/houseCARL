# `SKSE`

**Source:** `skse` (Skyrim Script Extender x64) • **Flags:** Hidden

---

## Global Functions

### `GetPluginVersion(name) → Int`

**Flags:** Native Global

get a plugins version number, -1 if the plugin is not loaded

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `name` | `String` | ✓ |  |

### `GetScriptVersionRelease() → Int`

**Flags:** Global

get the release index of this script file.
Can be used to detect a script/runtime version mismatch

### `GetVersion() → Int`

**Flags:** Native Global

get the major version of SKSE

### `GetVersionBeta() → Int`

**Flags:** Native Global

get the beta version of SKSE

### `GetVersionMinor() → Int`

**Flags:** Native Global

get the minor version of SKSE

### `GetVersionRelease() → Int`

**Flags:** Native Global

get the release index of SKSE.  This number is incremented every time
SKSE is released outside of the development team

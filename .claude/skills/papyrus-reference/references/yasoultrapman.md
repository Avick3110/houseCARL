# `YASTMUtils`

**Source:** `yasoultrapman` (Yet Another Soul Trap Manager) • **Flags:** Hidden

---

## Global Functions

### `TrapSoulAndGetCaster(caster, victim) → actor`

**Flags:** Native Global

Traps a soul and returns the caster.

Useful when you need to handle soul diversion, since the returned caster may
differ from the input caster.

A return value of 'none' indicates that the soul trap has failed.

**Parameters**

| Name | Type | Required | Default |
|---|---|---|---|
| `caster` | `Actor` | ✓ |  |
| `victim` | `Actor` | ✓ |  |

# KID Filters — section 2

The filters section (position 2) chooses **which items** of the named type receive the keyword. Leave
it blank or `NONE` to match **every** item of that type. Entries are **comma-separated** and may freely
**mix String filters and Form/EditorID filters** on one line. **[desc]**

Two kinds of entry:

- **String filters** — match by text (name, archetype, actor value, model path). §1.
- **Form / EditorID filters** — match by record (FormID, EditorID, plugin, or an *associated* form). §2.

Both kinds take the **pattern-matching modifiers** (`+` / `-` / `*` / none). §3.

**Sections in this file:** 1. String filters · 2. Form / EditorID filters · 3. Pattern-matching
modifiers · 4. Worked examples.

---

## 1. String filters

A bare word/phrase matched against the item, by these channels: **[desc]**

| Channel | Applies to | Example term |
|---|---|---|
| **Item name** | all types | `Iron Sword` |
| **Effect archetype** | Magic Effect, Spell, Enchantment, Scroll, Potion | `Absorb`, `Paralysis` (full list: `value-tables.md`) |
| **Actor Value (by name)** | Book, Magic Effect, Spell, Enchantment, Scroll, Potion, Weapon | `Destruction`, `OneHanded` (names: `value-tables.md`) |
| **Nif model path** | weapons & others — **not armors** | `weapons/MyIronSword.nif` (string must end `.nif`) |

- Names are matched as written; an exact name term like `Iron Sword` contains a space and is one term.
- The **archetype** and **actor-value** channels are how you say "all Absorb effects" or "all
  Destruction spells/books" without naming each record.
- **Nif path** lets you tag every item sharing a mesh (`*steelmace.nif` with a wildcard is the common
  form) — explicitly **does not work for armors** [desc].

---

## 2. Form / EditorID filters

Match specific records (or records *associated* with another form):

- **FormID** — `0x1234~MyMod.esp` (tilde-suffix; `esp` omitted for vanilla/DLC).
- **EditorID** — `MyAwesomeSwordID`.
- **Plugin name** — `MyMod.esp` matches **all items of that type defined in the plugin** (`[desc]`
  "To get all items in a mod: `MyAwesomeSwords.esp`"). Combine several: `ModA.esp,ModB.esp`.
  **Defining plugin or winning plugin? — unverified.** The description says "defined in", and that is
  the reading to compose against: `-Skyrim.esm` excludes every record whose FormID belongs to
  `Skyrim.esm`, whoever overrides it. The other reading — the plugin that wins the record at runtime
  — gives a different set whenever a patch overrides a master's records, and on a heavily-patched
  order the two differ by a lot. Nothing in this corpus settles it; KID's own source would, at the
  point where it turns a plugin-name filter into a form set. Until it is checked, say which reading
  your count assumes and give the other number when the two diverge.

### Type-specific Form filters [desc]

For several types, a Form filter resolves against an **associated** record, not the item's own FormID —
so you can tag items by a property they reference:

| Type | A Form filter matches the item's… |
|---|---|
| Armor | enchantment |
| Weapon | enchantment |
| Ammo | projectile |
| Location | music type · crime faction · parent location |
| Magic Effect | effect shader · hit art · casting art · enchant visuals/effect shader · projectile |
| Book | learned (taught) spell |
| Spell / Potion / Scroll / Ingredient | magic effects · half-cast perk |
| Enchantment | magic effect · worn-restriction FormList |
| Activator | water type |
| Flora | produce item |
| Furniture | associated spell |
| Race | skin (armor) · racial ability |
| Talking Activator | voice type |

Plus two cross-type Form filters:

- **Equip slot** — for weapons/armor/other equippable items, filter by the slot(s) they use.
- **FormList (FLST)** — passes if **any** of the Form filters contained in the list is valid (a
  reusable, shareable filter set).

---

## 3. Pattern-matching modifiers **[desc + source]**

Every filter entry (string or form) carries one of four roles, set by a prefix/joiner:

| Modifier | Role | Form | Logic | Works on |
|---|---|---|---|---|
| `+` | **Requirement** | infix joiner: `A+B` | item must have **all** (AND) | strings, forms |
| `-` | **Exclusion** | prefix: `-X` | item must **not** have it (AND-NOT) | strings, forms |
| `*` | **Wildcard** | prefix: `*Iron` | substring of name/keyword (ANY) | **strings only** |
| *(none)* | **Match** | bare: `A` | item matches **any** listed (OR) | strings, forms |

**Parser specifics [source]:** `+` is detected anywhere in a comma-segment, which is then split on `+`
into the requirement set (so `ArmorHeavy+ArmorGauntlet` is one requirement-pair). `-` and `*` must be
the **first character** of their term. Wildcards go into a strings-only bucket (they are substring
tests, not resolved to forms).

**Which channels a wildcard tests.** A `*` term is a String filter, so it is a substring test over
string channels, and the strings-only bucket is `[source]`: a wildcard is never resolved to a form.
Two consequences. `*Iron` cannot test a keyword *record* — only text. And `*` before a FormID,
EditorID or plugin name is not rejected by the parser: `*0x1234~MyMod.esp` becomes a substring test
against the string channels, which will practically never match, so it is a mis-cut filter that
silently tags nothing, not a term KID refuses.

**Which strings are in that bucket is unverified.** §1's channel list — item name, effect archetype,
actor value name, nif path — is `[desc]`, and the modifier table's "name/keyword" wording is the Nexus
description's too; neither settles whether the item's own keyword EditorIDs are among the compared
strings. KID's source at the wildcard comparison would. Compose as if name is the channel that
matters, and say so when a count rests on it.

**Evaluation order [desc]:** `Requirements → Exclusions → Matches → Wildcards`.
*(The description prints "3. Matches / 3. Wildcards" — a numbering typo; Wildcards evaluate last.)*

**How they combine:** an item passes when it has **all** Requirements, **none** of the Exclusions, and
matches **at least one** of the Matches/Wildcards (when any positive terms are given). Each added filter
**narrows** the pool — "combining multiple filters will progressively restrict the pool of items"
[desc]. To distribute to a *union* of groups, write **multiple lines** for the same keyword.

---

## 4. Worked examples [desc]

```ini
;all magic effects in a mod (plugin-name form filter)
Keyword = MysticismSpells|Magic Effect|MysticismMagic.esp

;all iron-named weapons, but not wooden swords (wildcard ANY + exclusion)
Keyword = RustProne|Weapon|*Iron,-Wooden Sword

;non-enchanted heavy gauntlets: Requirement (two keywords) + a -E trait
Keyword = 0x1234~MyArmorMod.esp|Armor|ArmorHeavy+ArmorGauntlet|-E

;all bound arrows, by name wildcard
Keyword = MysticalAmmo|Ammo|*Bound

;magic effects with specific hit-art forms (Form filters, OR)
Keyword = MagicDamageSun|Magic Effect|0x02019C9D,0x0200A3BB,0x0200A3BC

;all books teaching destruction — Form-filter "Destruction" actor value + S trait, or the alt
Keyword = SpellTomeDestruction|Book|Destruction|S
Keyword = SpellTomeDestruction|Book|NONE|S,20

;every item sharing a mesh path
Keyword = SteelMace|Weapon|*steelmace.nif

;exclusion-only filters, then a trait: every poison NOT defined in Skyrim.esm
;the filters section carries only a -exclusion (no positive term is required), and the
;trait narrows what is left — this is the shape a "everything except vanilla" job takes
Keyword = MyModdedPoisonTag|Potion|-Skyrim.esm|P
```

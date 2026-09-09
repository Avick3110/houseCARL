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
- A bare term is an **exact** test on each channel (`Item::Data::HasStringFilter`,
  `src/LookupFilters.cpp`, KID v3.5.0.rc1): the whole name or the whole EditorID case-insensitively,
  the whole model path, the archetype or actor-value name as a whole. Substring matching is the
  `*wildcard`'s job, and it reaches a different channel set — see §3.

---

## 2. Form / EditorID filters

Match specific records (or records *associated* with another form):

- **FormID** — `0x1234~MyMod.esp` (tilde-suffix; `esp` omitted for vanilla/DLC).
- **EditorID** — `MyAwesomeSwordID`.
- **Plugin name** — `MyMod.esp` matches **all items of that type defined in the plugin** (`[desc]`
  "To get all items in a mod: `MyAwesomeSwords.esp`"). Combine several: `ModA.esp,ModB.esp`.
  **It is the defining plugin, not the winning one. [source]** A plugin-name term resolves to an
  `RE::TESFile*` (`detail::formID_to_form` → `dataHandler->LookupModByName`, `include/KeywordData.h`),
  and the test KID runs on each item is `a_file->IsFormInMod(item->GetFormID())`
  (`Item::Data::HasFormOrStringFilter`, `src/LookupFilters.cpp`, KID v3.5.0.rc1, commit `a7a5589`).
  `TESFile::IsFormInMod` compares the FormID's own index against that file's load index — the
  regular index for a full plugin, the `0xFE` light index for an ESL (CommonLibSSE-NG
  `src/RE/T/TESFile.cpp`). So the question it answers is "does this FormID belong to that plugin",
  never "does that plugin win the record": `-Skyrim.esm` excludes every record whose FormID belongs
  to `Skyrim.esm`, whoever overrides it, and `MyMod.esp` catches only the records `MyMod.esp` itself
  defines — never a vanilla record it merely overrides. Count with the defining plugin.

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

**Which channels a wildcard tests. [source]** A `*` term never resolves to a form; it goes to a
strings-only bucket and is compared in `Item::Data::ContainsStringFilter`
(`src/LookupFilters.cpp`, KID v3.5.0.rc1, commit `a7a5589`), which tests exactly three channels, the
same three for **every** item type — no per-type switch:

| Wildcard channel | Detail |
|---|---|
| **EditorID** | `string::icontains(edid, str)` — case-insensitive substring |
| **Display name** | `string::icontains(name, str)` — case-insensitive substring |
| **The item's own keyword EditorIDs** | `keyword->formEditorID.contains(str)` over the item's keyword array; `BSFixedString::contains` runs `_strnicmp`, so this too is case-insensitive |

One exception routes away from all three: a term containing `.nif` is tested **only** against the
model path (`model.contains(str)`), both sides lowercased and backslash-normalized by
`Filter::SanitizePath` (`src/LookupFilters.cpp`; the term is sanitized at load in
`include/KeywordData.h`). A `.nif` wildcard therefore never matches a name or a keyword.

What a wildcard does **not** test: effect archetypes and actor-value names. Those live in
`HasStringFilter`, the exact-match path used by Match, Requirement and Exclusion terms only — so
`*Absorb` will not catch Absorb-archetype effects the way the bare term `Absorb` does. And a `*`
before a FormID, EditorID or plugin name is not rejected by the parser: `*0x1234~MyMod.esp` becomes
a substring test against those three channels, which will practically never match, so it is a
mis-cut filter that silently tags nothing, not a term KID refuses.

**Evaluation order:** `Requirements → Exclusions → Matches → Wildcards` [desc], and that is the order
the four checks run in `Item::Data::PassedFilters` (`src/LookupFilters.cpp`, v3.5.0.rc1) `[source]`.
*(The description prints "3. Matches / 3. Wildcards" — a numbering typo; Wildcards evaluate last.)*

**How they combine [source]:** the four groups are four independent gates, each skipped when empty —
an item passes when it has **all** Requirements, **none** of the Exclusions, **at least one** Match,
**and** at least one Wildcard. Matches and Wildcards are separate `and`ed groups, not one pooled OR:
a line carrying both `Iron` and `*Steel` needs the item to satisfy each side, not either
(`PassedFilters` tests `MATCH` and `ANY` in sequence, failing on either). Each added filter
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

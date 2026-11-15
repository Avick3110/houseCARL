# KID Filters — section 2

The filters section (position 2) chooses **which items** of the named type receive the keyword. Leave
it blank or `NONE` to match **every** item of that type. Entries are **comma-separated** and may freely
**mix String filters and Form/EditorID filters** on one line. **[desc]**

Two kinds of entry:

- **String filters** — match by text (EditorID, name, archetype, actor value, model path). §1.
- **Form / EditorID filters** — match by record (FormID, EditorID, plugin, or an *associated* form). §2.

An EditorID term belongs to **both** lists, and which one it lands in is decided at load: KID tries
to resolve it to a form first, then to a keyword another KID line creates, and only falls through to
the string bucket when neither resolves (§2). That fall-through is what makes the EditorID a live
String-filter channel.

Both kinds take the **pattern-matching modifiers** (`+` / `-` / `*` / none). §3.

**Sections in this file:** 1. String filters · 2. Form / EditorID filters · 3. Pattern-matching
modifiers · 4. Worked examples.

---

## 1. String filters

A bare word/phrase matched against the item, by these channels: **[desc]**

| Channel | Applies to | Example term |
|---|---|---|
| **EditorID** | all types | `IronSword01` (only when it resolves to no form — §2) |
| **Item name** | all types | `Iron Sword` |
| **Effect archetype** | Magic Effect, Spell, Enchantment, Scroll, Potion | `Absorb`, `Paralysis` (full list: `value-tables.md`) |
| **Actor Value (by name)** | Book, Magic Effect, Spell, Enchantment, Scroll, Potion, Weapon | `Destruction`, `OneHanded` (names: `value-tables.md`) |
| **Nif model path** | weapons & others — **not armors** | `weapons/MyIronSword.nif` (string must end `.nif`) |

- Names are matched as written; an exact name term like `Iron Sword` contains a space and is one term.
- The **archetype** and **actor-value** channels are how you say "all Absorb effects" or "all
  Destruction spells/books" without naming each record.
- **Nif path** lets you tag every item sharing a mesh (`*steelmace.nif` with a wildcard is the common
  form) — explicitly **does not work for armors** [desc].
- A bare term is an **exact** test (`ItemData::HasStringFilter`, `src/Data/ItemData.cpp`, KID
  v4.0.6): a term ending `.nif` tests the whole model path and nothing else; any other term tests the
  whole EditorID or the whole name, both lower-cased on each side, then, when the term is an
  actor-value or archetype name, the item's actor value or archetype. Substring matching is the
  `*wildcard`'s job, and it reaches a different channel set — see §3.
- On the model-path channel, **the two sides are not normalized the same way**, on a bare term as on
  a wildcard. A **term** holding `.nif` is put through `RE::SanitizePath` at load
  (`src/Data/BaseFilters.cpp`, `include/RE.h`), which lowercases, collapses `/` and `\` runs to a
  single `\`, strips leading separators, and strips everything up to and including `meshes\`. The
  **item's model** is only lowercased (`ItemData::GetModel`). So a model path stored with forward
  slashes, or with a `meshes\` prefix the term no longer carries, will not match a term that looks
  correct. Write the term the way the record stores the path, minus `meshes\`.

---

## 2. Form / EditorID filters

Match specific records (or records *associated* with another form):

- **FormID** — `0x1234~MyMod.esp` (tilde-suffix; `esp` omitted for vanilla/DLC).
- **EditorID** — `MyAwesomeSwordID`. **It is a Form filter only if it resolves. [source]**
  `ResolvedFilter` (`src/Data/FormData.cpp`) tries `LookupByEditorID` among every record type first,
  then a keyword another KID line creates (every line's keyword is created before any filter is
  read); when neither finds it the term falls through into the **strings** bucket, where it is
  tested against the EditorID channel of §1 (exact). A `*` term is never looked up: it is a
  substring test from the start (§3). So an EditorID that names a loaded record filters by record;
  one that does not — a typo, a plugin not installed, a record type the lookup does not index —
  silently becomes a text test rather than an error. A record that is found but is of a type KID
  does not take as a filter (a cell, an NPC) is skipped instead.
- **Plugin name** — `MyMod.esp` matches **all items of that type defined in the plugin** (`[desc]`
  "To get all items in a mod: `MyAwesomeSwords.esp`"). Combine several: `ModA.esp,ModB.esp`.
  **It is the defining plugin, not the winning one. [source]** A term ending `.esm`, `.esp` or `.esl`
  (case-sensitive) resolves to an `RE::TESFile*` (`ResolvedFilter` → `dataHandler->LookupModByName`,
  `src/Data/FormData.cpp`), and the test KID runs on each item is
  `a_file->IsFormInMod(item->GetFormID())` (`DistributableFilterSet::Pass`,
  `src/Data/BaseFilters.cpp`, KID v4.0.6, commit `895df22`).
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
| `+` | **Requirement group** | infix joiner: `A+B` | item must have **all** parts of the group (AND); several groups on one line are alternatives (OR) | strings, forms |
| `-` | **Exclusion** | prefix: `-X`, also on a group's part (`A+-B`) | item must **not** have it (AND-NOT) | strings, forms |
| `*` | **Wildcard** | prefix: `*Iron`, after a `-` if both (`-*Iron`) | substring of EditorID / name / keyword EditorIDs (ANY); a term ending `.nif` tests the model path **instead** | **strings only** |
| *(none)* | **Match** | bare: `A` | item matches **any** listed (OR) | strings, forms |

**Parser specifics [source]** (`ConfigFilterSet`, `src/Data/BaseFilters.cpp`, KID v4.0.6): `+` is
detected anywhere in a comma-segment, which is then split on `+` into **one group** (so
`ArmorHeavy+ArmorGauntlet` is one requirement pair); each comma-segment holding `+` is a group of its
own, and the item needs to pass **one** whole group. Inside a group each part takes its own `-` and
`*`, read as the first characters of that part. On a plain entry `-` and `*` are the first characters
of the entry. Wildcards are substring tests, never resolved to forms.

**Which channels a wildcard tests. [source]** A `*` term never resolves to a form; it is compared in
`ItemData::ContainsStringFilter` (`src/Data/ItemData.cpp`, KID v4.0.6, commit `895df22`), which tests
exactly three channels, the same three for **every** item type — no per-type switch:

| Wildcard channel | Detail |
|---|---|
| **EditorID** | `GetEDID().contains(str)`, both sides lower-cased — case-insensitive substring |
| **Display name** | `GetName().contains(str)`, both sides lower-cased — case-insensitive substring |
| **The item's keyword EditorIDs** | `kywd->formEditorID.contains(str)` over the item's own keywords and the ones KID has already added to it; `BSFixedString::contains` runs `_strnicmp`, so this too is case-insensitive |

One exception routes away from all three: a term ending `.nif` is tested **only** against the
model path (`GetModel().contains(str)`, `src/Data/ItemData.cpp`). The two sides reach that comparison
**asymmetrically normalized**: a term holding `.nif` gets `RE::SanitizePath` at load
(`src/Data/BaseFilters.cpp`) — lowercase, `/` and `\` runs collapsed to a single `\`, leading separators
stripped, everything up to and including `meshes\` stripped — while the item's model is only
lower-cased (`ItemData::GetModel`). Only case is
reliably reconciled; separators and the `meshes\` prefix are not. A `.nif` wildcard therefore never
matches a name or a keyword, and can miss a mesh whose stored path is spelled differently from the
sanitized term.

What a wildcard does **not** test: effect archetypes and actor-value names. Those live in
`HasStringFilter`, the exact-match path used by Match, Requirement and Exclusion terms only — so
`*Absorb` will not catch Absorb-archetype effects the way the bare term `Absorb` does. And a `*`
before a FormID, EditorID or plugin name is not rejected by the parser: `*0x1234~MyMod.esp` becomes
a substring test against those three channels, which will practically never match, so it is a
mis-cut filter that silently tags nothing, not a term KID refuses.

**How they combine [source]** (`DistributableFilterSet::Pass`, `src/Data/BaseFilters.cpp`, KID
v4.0.6): the `+` groups are checked first, then the plain entries, each side skipped when empty.
- **The `+` groups:** the item must pass **one** whole group — every positive part present, every
  `-` part absent. Several groups on a line are alternatives (OR).
- **The plain entries:** any matching `-` entry fails the item. Then the exact entries (no `*`) are
  one OR list and the `*` entries another, and **each side present must pass**: a line carrying
  both `Iron` and `*Steel` needs the item to satisfy each side, not either.

So a line with groups and plain entries needs one group **and** the plain entries to pass. Each
added list **narrows** the pool — "combining multiple filters will progressively restrict the pool
of items" [desc]. To distribute to a *union* of lists, use several `+` groups or write **multiple
lines** for the same keyword.

---

## 4. Worked examples [desc]

```ini
;all magic effects in a mod (plugin-name form filter)
Keyword = MysticismSpells|Magic Effect|MysticismMagic.esp

;weapons with "iron" in EditorID, name or a keyword, but not wooden swords (wildcard ANY + exclusion)
Keyword = RustProne|Weapon|*Iron,-Wooden Sword

;non-enchanted heavy gauntlets: Requirement (two keywords) + a -E trait
Keyword = 0x1234~MyArmorMod.esp|Armor|ArmorHeavy+ArmorGauntlet|-E

;all bound arrows, by wildcard (EditorID, name and keyword EditorIDs)
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

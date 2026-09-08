---
name: mutagen-reference
description: >-
  Looks up the schema of any Skyrim record type — fields, types, cardinality, writability, and an enum field's legal values — from the by-construction reference bundled with this skill. Use before reading, editing, or patching any record (ARMO, WEAP, MGEF, NPC_, …) and for any "what fields does X have" or xEdit-signature question. Schema only, never instance values — the shape of a type, not what one record in one plugin holds. A type absent from the reference is a real library-coverage gap to surface, never something to guess.
compatibility: Requires the houseCARL MCP server and a configured Mod Organizer 2 instance.
---

# Mutagen Reference

## Overview

This skill answers one question, offline: **what is the shape of this record type?** For any Skyrim record type it gives the fields, each field's type and cardinality, whether each field is writable, an enum field's legal values, and the arms a polymorphic field can take. It carries that lookup and the reading view of what a field's shape lets you write — and nothing else: it does not read instance values, and it does not route a job to another skill. **Schema, not instance:** "what fields does ARMO have and which are editable" is this skill; "what is the armor rating of *this* steel armor in *this* plugin" is a record read laid against this schema. The sibling doing the same for Papyrus function signatures is `housecarl:papyrus-reference` — on a Codex install, the bare folder name `papyrus-reference`.

The reference is generated **by construction**, by reflecting over the game's record library, and ships in this skill's `references/` tree. So the set of types it knows *is* the set the library models, not a hand-maintained subset — which is what makes the bundled-or-warn path below trustworthy.

## First step — the index, then one line

Grep `references/index.jsonl` for the full quoted token — the `name` or `sig` key with its value and both closing quotes — and it gives you the shard and the 1-indexed line to read. It is the only file read without a line number.

Where a line lands, and when to open each shard:

- Record types resolve into `references/records.jsonl`; read the single line the index named and nothing else.
- A field whose `ref` names a sub-struct resolves into `references/structs.jsonl` — grep the index for that name and block-read it the same way.
- An enum field's legal values live once on its own entry in `references/enums.jsonl`; fetch it only when you need the spelling.
- A polymorphic field's permitted arms are on its base's entry in `references/polymorphic.jsonl`.
- Read the concrete arm you are composing from `references/arms.jsonl` rather than guessing its fields.

Every one of them is generated; never hand-edit a shard. Do **not** bulk-load the index or a shard. Grep the index for the one matching line, then read the single line it points at — each block is exactly one line, so the read is one line.

## Lookup procedure

1. **Identify the record type.** From the user's words or the task: a type name (`Armor`, `MagicEffect`), an xEdit signature (`ARMO`, `MGEF`), or the record you are about to read or patch. Modders think in signatures; the library names types in full words, and the two often differ (`ALCH` is `Ingestible`, `ENCH` is `ObjectEffect`, `CLFM` is `ColorRecord`). The index carries both, so either resolves.

2. **Grep the index on the full quoted token** — `"name":"<Type>"` or `"sig":"<SIG>"`, closing quote included — so the hit is exact and field-scoped. A partial hit (a 3-letter `"sig":"WEA"` brushing `WEAP`, or a substring landing inside another entry) is not a match: re-check the spelling, then route it to "Bundled-or-warn". Three result shapes:
   - **One exact match** — take its `file` and `line`, and go to step 3.
   - **Several matches for one signature** — the library splits some signatures into typed variants (`GMST` into `GameSettingBool` / `Float` / `Int` / `String`; `GLOB` into `GlobalFloat` / `Int` / `Short` / `Unknown`). Pick the variant whose data type matches the value in hand, reading more than one block if you must to disambiguate.
   - **No match** — go to "Bundled-or-warn". Never substitute the nearest-looking record.

3. **Block-read the schema.** Read exactly the line the index named (`offset` = `line`, `limit` = 1). That one line is the whole schema for the type; do not read the rest of the shard.

4. **Resolve `ref` / `arms` / `target` on demand.** A field pointing at another modeled type carries `ref` (the sub-struct, enum, or owned child record), `arms` (a polymorphic field's permitted types), or `target` (the record a FormLink points at). To learn that type's own shape, grep the index for its name and block-read it the same way. That is how an enum resolves: a field reads `"c":"enum","ref":"ActorValue"`, and the legal names live once on the `ActorValue` entry.

## Worked examples

**Can I set the armor rating on armor?**

```
grep '"sig":"ARMO"' references/index.jsonl  ->  records.jsonl line 9
read records.jsonl line 9, one line         ->  {"n":"ArmorRating","t":"float","c":"scalar","w":true}
answer: yes — writable, a float. The block also reports "writable":"32/32".
```

**Is an armor light or heavy?** There is no armor-type field on ARMO; the axis is a leaf on a sub-struct, and only the second hop gives the spelling.

```
records.jsonl line 9        ->  {"n":"BodyTemplate","c":"substruct","ref":"BodyTemplate","null":true}
index -> structs.jsonl 20   ->  BodyTemplate.ArmorType is {"c":"enum","ref":"ArmorType"}
index -> enums.jsonl 18     ->  values: ["LightArmor","HeavyArmor","Clothing"]
answer: BodyTemplate.ArmorType, one of those three spellings.
```

**Which GMST holds a float?** A `sig` grep that returns typed variants is disambiguated by data type, not by first match.

```
grep '"sig":"GMST"' references/index.jsonl  ->  four hits, records.jsonl lines 44-47
read line 45                                ->  GameSettingFloat, whose Data field is the float
answer: GameSettingFloat.
```

## Always fetch fresh

Answer every schema question from a read taken now, never from a schema you remember from earlier in the session. Two reasons: a half-remembered schema is a mis-stated one, and a confidently wrong field name or writability flag is exactly the silent wrong answer this project exists to prevent; and long sessions get compacted, so a schema you "saw" earlier may survive only as a stub that invents fields and line numbers when it is reconstructed. This covers *presence* as much as content — the "no schema for X" warning below follows a fresh grep that just missed, never a recollection that X was not there. The reads are one index line plus one block line; there is no economy worth buying with a guess.

## Bundled-or-warn — never invent a schema

Coverage *is* the library's coverage, by construction. So a type absent from the index means one thing: the library does not model it — the documented library-versus-xEdit gap, not a houseCARL bug, and not licence to guess.

When the index has no match for a requested type or signature, emit the warning in this shape:

```
No schema for record type `XXXX` — the bundled reference doesn't include it.
The reference is generated from the whole record library by construction, so an
absent type means the library doesn't model it — the documented library-vs-xEdit
coverage gap, not a houseCARL bug. I won't invent a schema.

To proceed:
- Double-check the type name / signature spelling (the index carries both forms)
- Confirming in xEdit that the type exists there but not in the library confirms the known gap
- For a write, stop here: composing against a guessed schema risks a malformed record
```

Stopping before the write is the point, and it is not the only guard: the server's own write pre-flight is the enforcement. It refuses an unknown field, an illegal verb or a bad enum value by name — naming the legal values verbatim — so a guess never reaches a plugin. This lookup is the cheaper path to the same answer: one grep and one line, against a refusal per attempt.

## The index and schema shapes

`references/index.jsonl` — one entry per line:

```json
{"name":"Armor","sig":"ARMO","kind":"record","file":"references/records.jsonl","line":9}
{"name":"ActorValue","kind":"enum","file":"references/enums.jsonl","line":8}
```

- `name` — the library's type name; the primary lookup key.
- `sig` — the xEdit 4-char signature (records only). One signature can map to several names, the typed-variant case above.
- `kind` — `record` / `header` / `struct` / `arm` / `polymorphic-base` / `enum`.
- `file` + `line` — the shard and 1-indexed line of the schema block; block-read it directly.

A record or struct block is one compact JSON object on its own line:

```json
{"name":"Armor","kind":"record","sig":"ARMO","getter":"...IArmorGetter","mutable":"...IArmor","writable":"32/32","fields":[{"n":"ArmorRating","t":"float","c":"scalar","w":true},{"n":"Keywords","t":"List<FormLink<IKeywordGetter>>","c":"list","w":true,"target":"IKeywordGetter"},{"n":"BodyTemplate","t":"IBodyTemplateGetter","c":"substruct","w":true,"ref":"BodyTemplate","null":true}]}
```

Field keys are terse to stay light:

- `n` name · `t` type (display) · `c` cardinality (`scalar` / `enum` / `formlink` / `list` / `dict` / `substruct` / `polymorphic` / `value`) · `w` writable.
- Sparse keys, present only when they apply: `ref` (the sub-struct or enum this field points to), `arms` (a polymorphic field's permitted types), `elem` / `elemRef` / `elemArms` (a list or dict element's type, modeled-type ref, or arms), `key` (a dict's key type), `target` (the record a FormLink points at), `null` (nullable), `id` (a record-identity field such as `FormKey` — not free-edit content).
- Provenance keys a lookup can ignore: `getter` / `mutable`, and on an arm `base`.

An enum block carries its legal values:

```json
{"name":"ActorValue","kind":"enum","values":["Aggression","Confidence", "...", "None"]}
```

A block's `writable` is the type's `writable/total` field count — a summary. A field's own `w` is what governs whether you can set that field.

## Addressing a field & what you can write

A field's `c` decides **how it is named in `housecarl_apply`'s `field_path`** and **which `op` verbs it accepts**; its `w` decides whether it may be written at all. The write pre-flight is the source of truth for both; this is the reading view of it, so an edit composes before the first call instead of by trial. One op is a set of one, and the bulk lane is the same array read from a manifest, so a single edit and a thousand compose identically.

| `c` | How to address it in `field_path` | Verbs | Notes |
|---|---|---|---|
| `scalar` / `enum` / `formlink` / `value` | the dotted name — `ArmorRating`, `BasicStats.Damage` | `Set`; `Remove` to clear a nullable field; on a bit-flag enum, `Add` / `Remove` for one bit | The value is coerced to the field's `t`: a number, an enum name from the referenced enum's `values`, or `XXXXXX:Plugin.esp` for a `formlink`. On a bit-flag enum (`MajorFlags`, `Flags`, whose entry lists independent bits) a `Set` writes the whole value and drops every bit you did not name; `Add` sets one bit and `Remove` clears one, leaving the rest. The block does not mark which enums carry bits, and pre-flight refuses a bit verb on a single-value enum by name |
| `substruct` | descend to the leaf — `WorldModel.Male.Model.File`, not `WorldModel` | `Set` on the leaf; at the field itself `Set` where the struct composes or coerces (Notes), `Remove` on a nullable one, `CopyFrom` | Setting the field itself fills an absent struct in one op — `compose:{type:'<Struct>', …}` for one built from parts, where `<Struct>` is the field's own `ref` and not its name (`ObjectBounds` is both; the `FaceParts` field composes as `NpcFaceParts`), a plain value where the struct coerces from one (`Name`, a `TranslatedString`). A struct that does neither refuses a `Set` at the field and names the way in — a `GenderedItem<T>` (`WorldModel`) is written at `.Male` / `.Female`, or at `[0]` / `[1]`. Where the `ref` resolves to a `"kind":"record"` entry the field holds an **owned child record** (`Cell.Landscape`, `Worldspace.TopCell`): every write at the field is refused — edit that child by its own FormID, and author one the parent lacks with `housecarl_create`'s `parent` and `collection` (the singular slot's own name). A path *through* a parent reaches a sub-field only when the record being written already carries the child — a fresh override does not |
| `list` | `[N]` mid-path (`Effects[0].Data.Magnitude`); at the leaf, name the list itself and pass the index as `key` | `Add`, `Remove`, `SetAtIndex`, `InsertAtIndex`, `ReplaceAll` — not `Set` | Add a modeled element with `compose:{type:'<ElementType>', …}`; a coercible-element list takes plain values. `Add` appends and takes no `key`; `SetAtIndex` overwrites at `key` (`0`..`count-1`); `InsertAtIndex` inserts at a gap (`0`..`count`) and shifts the rest right, which is what a CTDA `Or` chain needs |
| `dict` | `[key]` mid-path; at the leaf, the verb with `key` | `Set` (with `key`), `Add`, `Remove`; `Merge`, `ReplaceAll` where the element coerces | `Merge` and `ReplaceAll` take an `entries` key-to-value map, which has no build-from-parts form, so a dict whose element is modeled (`Package.Data`, elements `APackageData`) takes `Set` / `Add` / `Remove` only; the block's `key` gives the key type |
| `polymorphic` as a **list element** | step into the element and name the field on its concrete arm — `Scripts[0].Properties[0].Object` | the `list` verbs | Each element is a concrete arm of the modeled base, resolved at apply time. `Add` one with `compose:{type:'<arm>', …}` |
| `polymorphic` as a **standalone field** | descend by name — `Configuration.Level.Level`, never a bracket | `Set` carrying a `compose` arm, or descend and `Set` a sub-field of the live arm | `compose:{type:'<arm>', sets:[…]}` chooses which arm sits there; the legal arms are the field's own `arms`, or the referenced base's |

Brackets are for `list` and `dict` elements only. When you compose an element, set its required sub-arm in the same `compose` — a `Condition` carries its `Data` arm. Taking one field's value from a plugin you name rather than from the load-order winner is `op='CopyFrom'` with `from_source`; copying a whole record is `housecarl_forward`, then `housecarl_apply` with `into` the same patch. New records are `housecarl_create`, and the read that precedes any of this is `housecarl_records`.

**Condition (CTDA) form-link targets.** A form-link parameter on a `*ConditionData` arm — `GetEquipped.ItemOrList`, `GetStage.Quest`, `HasPerk.Perk` — is a `FormLinkOrIndex<T>` that this reference normalises to `FormLink<T>` in the displayed `t`, because such a target can hold either a real FormID or a numeric index. The schema therefore understates the type: when the form-versus-index nature matters, confirm it at the engine rather than from the displayed `t`.

## Common mistakes

- **An absent type gets the warning above**, not a schema built from the nearest-looking record (inventing one is the failure this skill exists to prevent).
- **A signature with several variants is disambiguated by data type** — a `GMST` grep returning four names needs the value in hand (first hit picks wrong three times in four).
- **Read the one line the index named**; widen only if a block looks malformed, which is a generation bug worth reporting (a whole-shard read pulls in hundreds of unrelated types).
- **A `ref`, `arms` or `target` is a pointer, so resolve it** — grep the index for that name and block-read it too (quoting the pointer answers a different question).
- **`w:false` is the real schema**: some fields are read-only in the library, computed or without a mutable accessor (reading that as a bug sends the user chasing nothing).
- **A field's own `w` governs that field**, not the block's `writable/total`, which is the type's summary count.
- **A `substruct` is descended by name, a standalone `polymorphic` field is set by `compose`** (a bracket on either is refused).
- **A condition parameter's displayed `FormLink<T>` is a normalisation** of `FormLinkOrIndex<T>` (reading it as a plain link understates what the field accepts).

## Notes

- **Provenance.** The `references/` tree is generated by construction, by reflecting over the game's record library — the same walk that produces houseCARL's write-surface rulebook, so this read view and the write tools cannot disagree about field names or types. It regenerates from the library on a version bump.
- **Coverage is the library's coverage.** Every record type, sub-struct, polymorphic arm and enum the library models is here, at full depth. What is not here is what the library does not model — the documented xEdit delta, which bundled-or-warn surfaces rather than papers over.

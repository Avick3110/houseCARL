# Changelog

All notable changes to houseCARL are documented here. Versioning is [semantic](https://semver.org);
the `version` in `.claude-plugin/plugin.json` is bumped on each release, so installed users update only
when it changes.

## 1.6.0 — 2026-07-06

houseCARL learns to **see the parts of your workspace it was blind to**: read a plugin that isn't in your
active load order (even one inside a disabled mod), inventory the SKSE-plugin (DLL) layer, and catch Papyrus
script properties a plugin declares but never fills. **Three new tools (→ 31) and one new skill (→ 12)** —
plus a substantial dialogue- and record-authoring push, and a batch of silent-wrong and fail-loud fixes.

**Three new tools (→ 31) — new visibility layers**

- **`housecarl_read_plugin_file` — read any plugin file's own records, even one that isn't active.** houseCARL
  builds its world from your active MO2 profile, so a plugin you've unchecked — or one sitting in a *disabled*
  mod folder — used to be invisible. This read-only tool opens a named plugin file directly (active or not, by
  filename or absolute path) and returns **that file's own version** of a record, enumerates the records it
  defines (optionally filtered by type or EditorID), or summarizes it by record type. Every result is loudly
  stamped **OUT-OF-LOAD-ORDER**, so a raw-file read is never mistaken for load-order truth; it has no
  winner/conflict semantics by construction, and it resolves FormLinks against the file's declared masters
  wherever they sit on disk — telling you if one is missing or inactive rather than guessing. This is the
  enabler for inspecting a donor mod *before* you enable it, with no MO2 enable/disable dance to read one file.
- **`housecarl_skse_inventory` — see the SKSE-plugin (DLL) layer.** A full-depth inventory of
  `Data\SKSE\Plugins`: every `.dll` and every config file (grouped by its derived subfolder — SkyPatcher,
  DynamicStringDistributor, OStim, … — never a hardcoded list), each resolved to its winning MO2 provider with
  the **full winner→loser conflict chain** (loose/BSA tagged), and each winning DLL's version metadata decoded
  **statically, without loading it** — name / author / version, Address-Library vs version-locked, target
  runtimes. The record layer has always been houseCARL's home; this is the first look at the binary layer beside
  it, and it's honest about its ceiling: it reads a DLL's declared metadata, never its behavior (a legacy DLL
  whose version is only set at runtime says exactly that; an unreadable PE is flagged, never guessed).
- **`housecarl_validate_scripts` — catch script properties left silently unbound.** A Papyrus script attached to
  a record (VMAD) can declare a property in its compiled `.pex` that the record never actually fills — a silent
  `None` at runtime that throws no error and just makes the script quietly misbehave. This read-only sweep
  compares each attached script's compiled property list against what the record binds and reports the gaps,
  correctly leaving **alias-bound** properties (filled by the quest at runtime, not on the record) alone so they
  aren't false-flagged.

**One new skill (→ 12)**

- **`skse-plugin-authoring` — author an SKSE plugin in C++ against CommonLibSSE-NG.** houseCARL's first skill
  that leaves the data layer for the code layer: it walks the full lifecycle of building an SKSE DLL — project
  scaffolding, the plugin entry points and messaging interface, Papyrus-native functions and hooks, and the
  CommonLibSSE-NG idioms — so Claude can help write a plugin, not just read the records around it. Pairs
  naturally with the new `skse_inventory` tool (see the DLL layer; author into it).

**Dialogue & record authoring**

- **Fill a whole modeled-struct field in one op (`compose-Set`).** `set_field` / `bulk_apply` can now set an
  entire sub-struct in a single operation instead of one leaf field at a time — the authoring ergonomics
  prerequisite behind composed multi-record flows (e.g. rebuilding an NPC's appearance subtree).
- **Clear a nullable field via `Remove`.** A nullable polymorphic field or a nullable sub-struct can now be
  cleared back to unset with `Remove` — for example, un-fragmenting an INFO whose script fragment you want gone,
  cleanly rather than by writing an empty stand-in.
- **`@editorid` same-call sibling references now work inside list fields.** An `Add` / `ReplaceAll` onto a
  FormLink **list** can reference a record created earlier in the same call by its EditorID, so a batch that
  creates records and wires them together no longer needs a second pass for the list-typed links.
- **Single-gender records just author cleanly.** Creating a record with only one half of a gendered FormLink set
  (a single-gender skin/armor) now materializes the un-set half as an empty link instead of crashing, and
  editing an existing single-gender record stays safe.

**Dialogue fixes (CK-parity byte tier + validator)**

- **A newly-authored dialogue record now matches what the Creation Kit writes.** `create_record` / `bulk_create`
  default-populate the byte-level fields the CK fills in on a fresh INFO / DLVW / DLBR / QUST / DIAL that a raw
  insert would leave blank — the class of "byte-valid but plays wrong / won't start" traps that make hand-built
  dialogue silently fail.
- **`housecarl_validate_dialogue` got sharper.** It no longer false-warns on the standard PlayerRef player-state
  gate (see the `check_errors` fix below — same engine-implicit whitelist), flags a `<Global=X>` text tag that
  names a global the owning quest doesn't carry (renders as `[…]` in game), and auto-flags a `.seq` left stale
  by an in-place edit.

**Fixes**

- **`housecarl_check_errors` no longer drowns in false PlayerRef errors.** The integrity sweep was reporting
  every reference to the engine-implicit PlayerRef (`000014`) and Player (`000007`) forms as a dangling
  reference — on a real load order that was hundreds of false positives that overflowed the response. Those
  hardcoded engine forms are now recognized and exempted (a precise two-form whitelist, not the whole reserved
  range, so a genuinely broken low reference still surfaces).
- **`Remove` on a FormLink is now correct in both directions.** Removing a *nullable* scalar FormLink clears it
  to an empty link (instead of throwing); removing a *required* FormLink that can't be legally emptied now fails
  loud with a clear message (instead of silently doing the wrong thing).
- **A failed BSA extraction says why.** When BSArch writes nothing, `housecarl_bsa_extract` now names the actual
  cause instead of reporting an empty success.

## 1.5.0 — 2026-07-02

houseCARL gains **plugin surgery**: ESL-compact a plugin with its FormID-keyed assets (facegen, voice, SEQ)
carried along automatically, and sweep the whole load order for record errors. **Two new tools (→ 28).**
Plus a crash fix for newly-authored dialogue topics, a compact in-place verify read-back, and a batch of
silent-failure and ergonomics fixes.

**Two new tools (→ 28) — plugin surgery arrives**

- **`housecarl_compact_plugin` — ESL-compact a plugin, records AND the files keyed to them.** Renumbers a
  plugin's own records into the ESL FormID window (`0x800`–`0xFFF`), sets the small-master flag, and repoints
  every internal reference — writing a **new** compacted file by default, with the original untouched; editing
  the original in place is opt-in and gated by the same consent handshake as the in-place write lane. What
  sets it apart from a manual compact: **the assets whose filenames encode a FormID move with the records** —
  facegen pairs (facegeom `.nif` + facetint `.dds`) and voice files (`.fuz`/`.lip`) are carried to the new
  FormID paths, and the plugin's `.seq` is regenerated (refresh-only — houseCARL updates a `.seq` that exists
  and warns if one is needed, it never invents one). Renumbering an NPC without renaming its facegen is how a
  compacted mod's faces go dark; the tool closes that whole failure class in one operation. If **other
  plugins** reference the records being renumbered, the tool identifies them by name and fails loud —
  repointing them is a separate opt-in, never a surprise edit.
- **`housecarl_check_errors` — load-order integrity sweep.** The data-layer twin of the Creation Kit's "Check
  For Errors" / xEdit's error check: for every plugin in scope (one, several, or the whole active order) it
  walks every record's FormLinks and reports **dangling references** (a link no active plugin defines),
  **missing masters** (a declared dependency not installed/enabled — the most common load-order break), and
  **parse failures** (records or whole plugins that couldn't be read). Read-only, and explicit about its
  boundary: it covers the reference/master/parse class, not navmesh or terrain spatial integrity.

**Write-lane fixes**

- **In-place verify read-back is now compact by default.** The forced post-write verify on an in-place edit
  used to deep-dump every touched record — on records already carrying big lists it overflowed the response
  budget and could read as "only some of your edits applied" when all of them had. It now renders one
  confirmation line per record (what landed, re-read clean), covering **all** touched records; the full
  field-by-field dump stays behind `full_readback=true`, now bounded so its truncation notice actually reaches
  you. Corruption detection is unchanged — every record is still deep re-read, only the output slimmed.
- **Silent write failures made loud.** A collection verb against an array-backed field now refuses with a
  clear message instead of crashing mid-write, and list elements typed as `IAssetLink` interface forms now
  coerce correctly on write.

**Ergonomics**

- **`housecarl_compile_script` names the real cause of a missing-import failure.** When a compile fails with
  errors dominated by unresolved symbols/types — the signature of an incomplete `import_dirs`, which can
  produce hundreds of errors that look like code bugs — the result now leads with a prominent "incomplete
  import_dirs, not a bug in the script" banner. The classifier is keyed on the compiler's actual error wording
  and gated on a supermajority, so a genuine typo is never mislabeled.
- **A near-miss plugin name now gets a "did you mean."** A `lookup=`/`plugins=`/FormID plugin name that isn't
  in the load order — an apostrophe slip, a typo, or the mod *folder* name passed for the plugin *filename* —
  now suggests the nearest real plugin(s) instead of a flat "not in the load order," across
  `housecarl_load_order_status`, `housecarl_cross_plugin_query`, and `housecarl_read_record`. No suggestion is
  offered unless a candidate genuinely clears the bar — a wrong "did you mean" is worse than none.
- **MO2 2.5.x instances are recognized.** `ModOrganizer.ini` files written in the spaced `key = value` form
  (MO2 2.5.x) now parse correctly.

**Dialogue fixes.** A dialogue topic (DIAL) carries its subtype in two places that must agree — the numeric
`Subtype` and a 4-character `SubtypeName` (SNAM) marker the game actually buckets topics by. houseCARL wrote
the number but left the marker blank, so a newly-authored topic crashed on load (community report #131, by
matashina).

- `housecarl_create_record` / `housecarl_bulk_create` — a new DIAL now gets its **SNAM marker auto-filled**
  from its `Subtype` (`Hello`→`HELO`, `Goodbye`→`GBYE`, a bare/`Custom` topic → `CUST`, …) and reported, so
  it's never silent; an explicit `SubtypeName` you set is never overridden. The subtype→marker table is
  sourced **by construction** from xEdit's DIAL definition (all ~100 subtypes) and CI-guarded against drift.
  A subtype with no modeled marker (an out-of-range value) fails loud instead of writing a blank marker.
- `housecarl_set_field` / `housecarl_bulk_apply` — changing an existing topic's `Subtype` without also setting
  `SubtypeName` now **syncs the marker** to match (both the new-patch and in-place lanes), so a subtype change
  isn't a silent in-game no-op.
- `housecarl_validate_dialogue` — a blank SNAM marker is now a reported issue that names the expected marker:
  an **error** on a newly-authored topic (the #131 crash), a **warning** on an override (where the base
  record's marker can still apply).

## 1.4.0 — 2026-06-24

houseCARL can now **edit an existing plugin in place** — including a mod it didn't author — instead of
only ever writing a separate patch, rounding out the write surface with the same fail-loud,
verify-what-you-touched discipline as the patch lane. Alongside it: three new tools (forward a named
plugin's record as an override, resolve a magic effect's carriers, author an empty trigger plugin), three
new bundled skills (→ **11**), a wider and sharper dialogue validator, a bitwise query predicate for
equip-slot and flag fields, and several silent-wrong-answer fixes. **Three new tools (→ 26), three new
skills (→ 11).** Carries further community contributions from **DrHeisen**.

**In-place write lane — edit, create, and remove records directly in an existing plugin**

- **houseCARL can now write straight into a plugin you point it at — including one it didn't author —
  instead of always emitting a separate patch.** The five write tools (`housecarl_set_field`,
  `housecarl_bulk_apply`, `housecarl_create_record`, `housecarl_bulk_create`, `housecarl_remove_record`)
  gain `target=`, `in_place=true`, and `acknowledge=`. With them houseCARL edits existing records, creates
  brand-new ones (flat, nested children like a dialogue line or a placed reference, or a whole cell), and
  removes records the file carries — rewriting the original plugin the way xEdit or the Creation Kit do on
  save (the author's master list and FormID counter preserved, every record it touched verified on
  read-back). The default new-patch lane is unchanged and stays the default.
- **Behavior change worth knowing:** the in-place lane edits your original file and keeps **no backup** — a
  deliberate departure from houseCARL's default "originals are never touched." It is strictly opt-in
  (`in_place=true`) and gated by a one-time consent handshake per plugin: the first in-place touch of a
  given file names the exact path and the no-undo trade-off and writes nothing until you re-call with
  `acknowledge=true`, then never asks again for that plugin (the consent is remembered across sessions and
  shared across edit / create / remove). Keep your own backup of anything you edit in place. Files edited
  this way are marked `editedInPlace` (never houseCARL-owned), so a later `into=` extend can't
  blind-overwrite your mod. A plugin whose own records use reserved sub-`0x800` FormIDs (vanilla / Creation
  Club) is refused in place — you override those, you don't edit them.

**New tools**

- **`housecarl_forward_record` — copy a named plugin's version of a record as an override.** The inverse of
  `set_field` / `bulk_apply`, and the data-layer equivalent of xEdit's "copy as override into": it copies a
  *named* earlier plugin's whole record verbatim into a patch so it wins again — re-assert one mod's version
  of a record over a later override, or name a master to revert a record to vanilla. Works for every record
  type, nested Cell / Placed / INFO families included. It refuses loudly (writing no file) on a bad source —
  one not in the load order, the output patch itself, a source that doesn't define the record, the same
  target named twice — and flags a forward whose version already wins as redundant.
- **`housecarl_effect_chain` — a magic effect's carriers and magnitudes in one call.** Point it at a
  MagicEffect (MGEF) and it resolves every spell, enchantment, potion, scroll, and ingredient across the
  load order that applies it, each with the magnitude / area / duration from the matching effect entry (as
  authored — conditions are not evaluated). It collapses the old "query references, then read each hit" loop
  across five record types into one read, and fails loud rather than returning a silent zero: a
  non-MagicEffect FormID errors naming the real type, an absent FormID errors, and a genuinely unused effect
  returns a clean, distinguishable zero.
- **`housecarl_create_plugin` — author an empty header-only "trigger" plugin.** Emits a valid plugin with a
  TES4 header and zero records — the clean primitive for "I just need `Foo.esp` to exist": a basename-bound
  SKSE config trigger (the CraftingCategories-style pattern where a config loads because `Foo.esp` is
  present), a placeholder ESL for FormID reservation, or a dummy master. Before, a trigger plugin had to
  carry a junk filler record that polluted the conflict tree. The name is used verbatim (the basename is
  load-bearing, so no auto-suffix) and a collision refuses loudly rather than renaming or overwriting;
  `esl=true` flags it a light master.

**Dialogue validation & authoring**

- **`housecarl_validate_dialogue` gained five new lint families** (all advisory — it warns, never blocks,
  never auto-fixes): text-encoding (a player-facing string carrying a non-ASCII character that would render
  as in-game mojibake, with the offending character and an ASCII substitute named); result-script fragment
  presence (how many of a topic's lines actually carry a script fragment, so you know whether to expect
  runtime behavior); SEQ staleness / coverage (a Start-Game-Enabled quest whose plugin has no `.seq`, a
  `.seq` that doesn't list it, or one older than the plugin — meaning the quest and all its dialogue
  silently never start on a fresh save — and it also tells you when a regen is *not* needed); and static
  condition (CTDA) well-formedness (dead run-on references, dead alias indices, dangling form / global
  parameters, GetIsID pointed at a placed reference instead of a base object).
- **Deep reads now show VMAD script-property values.** A `depth>=2` read of a script's Properties prints
  each property's value — the Object FormLink, the Data scalar, the alias — instead of stopping at the
  identity line, matching xEdit; a declared-but-unset Object shows a named `(null link)` rather than
  vanishing.
- **Condition form targets accept the flat `fields:` shorthand.** Composing a condition's data arm and
  setting its form-link-or-index target through the flat `fields:` map (e.g.
  `GetEquipped {ItemOrList: "0001F4:Skyrim.esm"}`) was wrongly refused at pre-flight; it now lands a target
  in both form and alias-index mode, byte-identical to the verbose path, across the whole FLOI
  condition-parameter class.
- **The `dialogue-authoring` skill gained substantial reference depth** — CK pages for decoding a CTDA
  condition (a ~40-function dialogue table), the DLBR branch entry point and its Exclusive-branch deadlock,
  and the quest stage / objective model; a set of authoring traps the flow model implies but the validator
  can't catch (Stop() resets a quest's stage, a monologue is several Responses in one line, CK conditions
  can't express (A AND B) OR (C AND D), GetStageDone vs GetStage); and write-side recipes for cloning a
  verified condition gate across many lines and writing a CK-refused INFO subtype.

**Querying & equip slots**

- **`housecarl_cross_plugin_query` gained a `has` bitwise predicate** for bitmask / flag fields:
  `where ... has Body` (or a bit value, decimal or `0x` hex) matches if that bit is set regardless of the
  others — so a multi-slot armor whose `BodyTemplate.FirstPersonFlags` carries body *plus* a modder slot is
  now findable, where exact `=` only matched a single-slot piece. For `[Flags]` enum fields, range operators
  now compare the numeric value (`>= 65536` no longer errors) and `=` / `!=` equate by resolved bits (so
  `= 16` matches a field that renders as the flag name). **Behavior change:** a query that relied on the old
  exact-string-or-error behavior for a `[Flags]` field may now return different results; non-flags enums are
  unchanged.
- **New `biped-slot-reference` skill** — the ergonomic layer over `has`: it turns a biped slot (a number
  like 52, a vanilla name like Body, or a community label like SOS / pelvis) into the `FirstPersonFlags` bit
  to query on, so finding every armor on a slot is a lookup instead of power-of-two mental math. Ships a
  verified slot 30–61 table (the named bits are non-contiguous — the trap a from-memory table gets wrong)
  and the multi-slot query pattern.

**Correctness fixes**

- **Localized fields on cleaned base-game masters read correctly again.** A localized master sitting in a
  folder with no strings of its own — the near-universal "Cleaned Base Game Masters" setup, where a cleaned
  DLC / Update `.esm` lives in a bare folder while its `.STRINGS` stay in the game-Data BSAs — was reading
  every localized field (Name, DESC, …) as **empty**, so `where Name contains …` silently zero-matched the
  DLC masters and a read showed a blank Name. houseCARL now points the strings lookup at the real game-Data
  folder when (and only when) the plugin's own folder carries no strings source. **Behavior change:**
  queries and reads against those masters can now return matches and content where they used to find
  nothing. As defense-in-depth, a genuinely unresolved localized string now renders the loud
  `(unresolved localized string)` note instead of a blank that looked like a real value.
- **`into=` resolves a renamed patch folder.** Extending your own houseCARL patch after you renamed its MO2
  mod folder for organization used to fail — `into=` demanded folder name, suffix, and `.esp` basename all
  match. It now resolves by plugin name (the folder holding `<stem>.esp`, whatever it's now called) then
  folder name, refusing loud only if two owned folders are genuinely ambiguous. The same fix was extended to
  the rider / asset write path behind `compile_script`, `decompile_script`, `bsa_repack`, `place_asset`, and
  `bulk_place_asset`, which had still carried the old three-way match. A foreign, un-owned plugin stays
  refused.

**New skills & reference depth**

- **New `oar-authoring` skill** *(DrHeisen)* — author or interpret Open Animation Replacer (OAR) configs:
  the runtime, condition-driven animation system (`config.json` / `user.json`) that supersedes DAR and still
  reads its legacy `_conditions.txt` folders. Ships a source-verified reference (the full schema, the
  ~120-condition roster, the authoritative `IsEquippedType` enum — which OAR deliberately diverges from the
  vanilla `GetEquippedItemType` enum — the DAR grammar, and the global INI) plus a playbook for the
  counter-intuitive parts: OAR ignores plugin load order and picks winners purely by `priority`; the
  top-level array is lowercase `conditions` while a nested `AND`/`OR` child array is capital-C `Conditions`;
  `user.json` is a full-document shadow of `config.json`; and an addon condition (Math / RaySense / IED /
  Detection / Dialogue) silently no-ops when its DLL is absent. It complements the distributor skills — it
  authors animation CONFIGS, while forms-to-NPCs is SPID, keywords-to-items is KID, and record fields is
  SkyPatcher.
- **New `tool-output-awareness` skill** *(DrHeisen)* — recognize the plugins and assets that generated tools
  produce (Reqtificator, ParallaxGen, DynDOLOD, Synthesis, TexGen, xLODGen, NPC Plugin Chooser 2) and keep
  their re-derived records and asset paths out of an authored patch, so you never bake a regenerable
  artifact into a hand patch that goes stale — or silently breaks — the next time the tool runs.
- **`papyrus-reference` now loads before any `.psc` read *or* edit** — including an edit that only reuses a
  call already in the file (a copied call is not a verified call; "the compiler will catch it" covers
  signatures, not semantics) — and bundles a new "silent-biters" reference of Papyrus traps that compile
  clean but misbehave at runtime (GetFormEx vs GetForm for the ESL range, SendModEvent handler arity,
  FormList.HasForm missing base NPCs, Utility.Wait in a paused-menu handler, and more).

## 1.3.0 — 2026-06-21

The biggest release since 1.0: a VFS-aware **asset layer** (read which copy of any file wins; place a file
as a winning override), end-to-end **dialogue authoring** (compose a whole conversation in one call, audit a
dialogue graph, write start-game-enabled `.seq` files), a Mutagen-native **script decompiler**, a much wider
and more honest **write pre-flight**, and a broad sweep of **crash-atomic / MO2-disk correctness**
hardening. Seven new tools, two new skills. Carries an outside code contribution from **AlmightyChan** (the
`create_record into=` upsert, #44/#45).

**VFS asset layer & FaceGen**

- **houseCARL now answers "which copy of a file actually wins?" the same way it answers it for records.** A new VFS-aware asset layer resolves any Data-relative path — mesh, texture, script, sound, interface file — against the active load order and reports the winning copy (the overwrite folder, a specific mod, Data, or inside a BSA), loose-vs-BSA aware. This is the file-layer counterpart to a record's load-order winner: before, houseCARL could tell you which plugin wins a record but had no way to tell you which mod or archive wins a given file path. Exposed through the new `housecarl_asset_status`, running against the real, live load order with zero file/archive handles held at rest — the same contract as the record resolver, so MO2/xEdit can move files freely.
- **You can now place a file as a winning override into a fresh houseCARL mod.** Two new tools write the asset side: `housecarl_place_asset` puts one file in place, and `housecarl_bulk_place_asset` puts many into a single mod folder in one call. The source can be a loose file, one entry pulled out of a BSA, or a whole BSA; the destination can be a raw asset path or — for an NPC's FaceGen — a FormID plus kind (mesh/tint), with the path computed from the FormKey. Before, houseCARL could read which copy of a file wins but could only tell you what to do by hand to make a different copy win. Writes are crash-atomic and non-destructive: originals are untouched, a fully-failed batch leaves no orphan folder, and a reused destination folder is never deleted. The tools are honest that "wrote it" is not "it wins" — every response reports the current winner and the MO2 enable-plus-sort step still required.
- **New `facegen-diagnostics` skill.** houseCARL's 7th shipped skill walks the dark / grey / black-face NPC bug end to end — it resolves the NPC to a FormKey and compares two independent precedence systems (which plugin wins the NPC record vs which mod or BSA wins the facegen `.nif`/`.dds` file), then either places the correct facegen as a winning override or forwards the matching appearance into a new plugin. Where the fix needs the Creation Kit, NifSkope, or RaceMenu it instructs the steps rather than faking them, and it gates every "done" behind an in-game verification handoff. It drives the new asset tools and ships with a 24-cause taxonomy and symptom table. (The asset tools themselves are framed as a general VFS capability — "which copy of any file wins" — with FaceGen as the headline use case rather than the framing.)

**Dialogue authoring & validation**

- **Author a whole dialogue conversation in one command.** `housecarl_create_record` gained optional parent/collection arguments, and a new `housecarl_bulk_create` allocates a parent and its children in a single all-or-nothing call — a DialogTopic with its INFO response lines nested under it, each with a fresh local `0x800+` FormKey. The add-target is found by construction over the parent's modeled child-collections (never per-record-type hand-wiring), and the unique / named / missing / ambiguous outcomes all fail loud. Before, the data layer could create flat records but had no way to allocate a brand-new child into a parent's collection, so a dialogue line under a topic was out of reach.
- **Same-call sibling references wire the conversation together.** Inside a `housecarl_bulk_create` call, a FormLink value of the form `@editorid` forward-references a record created earlier in the same call, resolved to that sibling's auto-allocated FormKey after allocation — so an INFO's Topic back-link and its PreviousDialog can point at sibling records that don't exist until the call runs. The mechanism is generic across any FormLink on any created record, and the sibling token is accepted only as a singular Set on a FormLink leaf, in create context, for an editorid declared earlier; everywhere else (a later/self reference, a non-FormLink field, a value inside a list or dict, the edit-existing path) it rejects loud rather than silently substituting nothing.
- **New dialogue lines are checked for the audio and scripts they need to actually work.** A byte-valid INFO can still do nothing in game — no `.fuz` on disk plays silent, and a half-built or uncompiled result-script binding fires nothing. On a successful create, the response now folds in two on-disk checks. Voice coverage flags each created voiced response that has no audio, printing the exact `Sound\Voice` `.fuz`/`.lip` path to place it at (and naming the winning provider when audio is already present); when the path can't be computed (no Speaker, or an unresolvable voice type) it says so with a named reason instead of a false "fine". Result-script binding flags an INFO whose VirtualMachineAdapter is hollow or whose bound script class has no compiled `Scripts\<class>.pex` on disk, naming the missing path. Script-free lines are never nagged.
- **New tool `housecarl_validate_dialogue` audits an existing dialogue graph on demand.** Point it at a DialogTopic (DIAL) or Quest (QUST) FormID and it resolves the load-order winners and reports the wiring: whether the quest is set and resolves (unowned is a warning), whether a set branch resolves to a real DLBR (unset is normal), the INFO.LinkTo topic-to-topic chain with broken links flagged, and a dangling PNAM **only** when it is set-but-unresolvable — empty PNAM is never flagged, since vanilla topics legitimately leave it empty and select within a topic by Conditions. It reuses the voice and result-script checks over every live INFO (closing the edit-path audit gap the create-time checks left open) and always declares the parts it cannot check (CTDA/lip-sync, the dropped-INFO conflict boundary). Read-only.
- **New tool `housecarl_write_seq` makes start-game-enabled quests actually start.** Without a `Data\SEQ\<plugin>.seq` file, a plugin's Start-Game-Enabled quests — and any dialogue gated on them — silently never start. `housecarl_write_seq` is the data-layer equivalent of the CK's on-save SEQ generation or xEdit's "Create SEQ file": point it at a plugin and it writes the `.seq` its SGE quests need. The encoding (a flat array of little-endian master-index FormIDs, never the runtime `0xFE` form) was pinned empirically against all 145 real `.seq` files in a live load order, so the file is computed wholly at author time with no runtime bridge. It writes crash-atomically and non-destructively, defaults the `.seq` into the plugin's own houseCARL folder so there's one mod to enable, and a plugin with zero SGE quests writes nothing rather than an empty file.
- **New `dialogue-authoring` skill** ties the dialogue tools into a playbook for the five Creation-Kit bookkeeping jobs a byte-valid insert skips — the silent-failure class houseCARL refuses (a line that passes xEdit but skips them plays nothing in game). It encodes the counter-intuitive dialogue policy (PNAM is ~unused; Conditions, not list order, select the line; the winning topic silently drops any line it doesn't re-list), drives `housecarl_bulk_create` / `housecarl_create_record`, `housecarl_compile_script`, `housecarl_write_seq`, and `housecarl_validate_dialogue` through those jobs, then validates the result — and reads or audits existing dialogue (what a topic does, why a line won't fire, a dropped-line conflict).

**Script toolchain**

- **Decompile any compiled script back to reviewable source.** New tool `housecarl_decompile_script` reconstructs readable `.psc` source from a compiled `.pex` with no external decompiler or compiler involved (Mutagen-native) — measured at 100% structuring (one named irreducible) and 98.80% byte-exact recompile round-trips across all 10,189 provable script pairs in a 3,400-plugin load order. Before, the only way to read what a shipped `.pex` actually does was an outside tool or guesswork. Inherent PEX losses are stated where you see them (parameter defaults are baked at call sites; comments and layout are gone), anything it can't prove fails loudly in the output (raw bytecode in the `.psc`, counted in the result) rather than rendering a silent wrong answer, and it never overwrites an existing file. BSA-packed scripts compose with `housecarl_bsa_extract` first.
- **Compiling against a mod's extended script copy now works.** When you pass `import_dirs` to `housecarl_compile_script`, your folders now outrank the vanilla auto-import (order is own folder, then your `import_dirs`, then vanilla last). Before, a vanilla copy of an extended source (SKSE's `Actor.psc` / `Game.psc` / `Form.psc` especially) shadowed the extended one — the CK compiler takes the first match — so any call to an extended function failed "not a function or does not exist" even when you had explicitly pointed at the right folder. Explicit now beats implicit, matching the game's runtime; calls passing no `import_dirs` behave exactly as before.
- **Reliability hardening across the script and BSA bridge.** The decompiler's optimizer-origin hint now also fires on statement-level `JMPT` (a flow pattern the CK compiler provably never emits), flagging a wider class of Caprica-optimized scripts — while the description keeps the honest floor: detection is pattern-based, two named flow-canonical files still stay silent, and the note's absence does **not** prove CK origin. The BSArch bridge no longer false-succeeds: `housecarl_bsa_extract` now judges success by this-run provenance (a new path, or a changed size/mtime vs a pre-run snapshot) instead of "a folder entry exists afterward" (which the ownership marker alone satisfied, so a failed run looked successful); packing refuses loud on a stale leftover scratch rather than moving it over the target; `housecarl_bsa_list` errors on any parse failure (a declared-vs-listed count mismatch is reported, not papered over); archive paths are fully normalized; and an unknown `format=` token refuses and names the legal set.

**Write pre-flight (records, fields, collections)**

- **More record shapes are composable end-to-end — edits that route through a polymorphic field validate and write where they used to be rejected.** Several common fields are polymorphic (they take one of a set of "arms" depending on what's there). Writes to a standalone polymorphic field (an NPC's Level or Sound, a dialogue script fragment, a Condition's data) used to be rejected by pre-flight because it couldn't see fields that exist only on one arm, or it over-rejected a field present on several write-identical arms as a "conflicting shape". Pre-flight now descends into the arms and admits exactly what the engine can write, and you can select a polymorphic sub-arm inside a nested compose (e.g. giving a Condition its data shape), which previously had no way to be expressed.
- **Bad collection-element edits now fail at the gate with a clear message, not a cryptic crash mid-write.** When you Add / Set / ReplaceAll / Merge / Remove an element of a list or dictionary field (a keyword on an NPC, a LinkTo on a dialogue response, a faction entry, a skill weight), houseCARL now checks the element up front — that a required value or dict key/list index is present, that a malformed FormID or unparseable value is caught, and that a key or index has the right shape. Before, these slipped past pre-flight and threw an unnamed exception deep in apply, which houseCARL surfaced as the alarming "pre-flight accepted it but apply threw — a real inconsistency", pointing you at an internal bug when the real problem was a fixable input. The check is by-construction across every collection field, so the gate rejects exactly what apply would have thrown on.
- **Malformed writes fail by name before touching disk.** Clearing a FormLink with a zero value (`00000000` or `0`) now clears the link instead of throwing "malformed FormKey" at write time (and a real FormID is never mistaken for a clear); a composed record left missing a required arm now fails with a named `NullArmSerializeException` saying what's missing instead of a bare null-reference crash; and bracketing a gendered field at its end now points you at the right `.Male`/`.Female` form rather than suggesting list verbs that don't apply. In every case the staged write is all-or-nothing — your originals and the in-progress output are untouched when a write is refused.
- **Expected, fixable write errors now read as fixable input, not internal inconsistencies.** A class of errors pre-flight legitimately cannot catch — an out-of-range list index, adding a dict key that already exists, removing a value/key that isn't there, navigating into an absent collection — used to be wrapped in the same "real inconsistency" alarm as genuine engine bugs. These now render cleanly with actionable guidance while still refusing the whole call and writing no file; genuine gate/apply drift still gets the loud wrapper, and malformed pre-existing source data is now called out as its own category rather than blamed on your input. **Behavior change worth noting:** Remove of an absent value or key is now a surfaced rejection rather than a silent no-op — a script that relied on "remove X if present" being a safe no-op will now have the whole call refused when X is absent.
- **Gendered fields are now editable through the same `[0]`/`[1]` form the reader shows you.** Fields holding a male/female pair (an armor's WorldModel, for example) display in a read as `[0]` and `[1]`, but feeding that exact form back used to be both unreadable and unwritable — only the longer `.Male`/`.Female` path worked. Now `[0]` (male) and `[1]` (female) are a true read-and-write alias, and a write to a not-yet-present arm is materialized and written through correctly rather than silently dropped.
- **Create GlobalVariable, GameSetting, and AI-package Data inputs — previously un-authorable.** GlobalVariable and GameSetting sit under an abstract group, so `create_record` refused them outright; it now creates the concrete shape you ask for (`GlobalFloat`, `GameSettingFloat`, and the rest), with the arms discovered from Mutagen's own type hierarchy and a bare "Global"/"GameSetting" request failing loud with the real choices. Separately, `Package.Data` — the one struct-valued dictionary Mutagen models, holding an AI package's typed inputs (travel/escort target, sandbox/patrol location, literals, object list, dialogue topic) and the last package piece houseCARL could read but not write — is now composable, with a duplicate-key add refused by name ("use Set to overwrite") rather than a raw library error.
- **Polymorphic base types are no longer offered as a legal arm of themselves.** A concrete base type (an AI-package Data entry, a Condition, a VMAD script property) was incorrectly listed as an arm of itself — and composing `Package.Data` by its own name silently wrote a degenerate empty entry, the worst failure class. The gate now rejects composing a base by its own name and filters it out of the legal-arms list, and the bundled `mutagen-reference` schema no longer self-lists those bases (a display fix, no change to which arms are actually composable).
- **Conflict diffs tell an identical-to-winner override apart from a field the plugin simply doesn't carry.** The winner-relative conflict view used to show only differences, so an override that restates a field identically to the winner (an ITM edit) looked the same as a plugin that doesn't touch that field at all — and a not-carried field rendered as a confusing phantom "(absent)" delta. The diff now reports an agreed-with-winner count so an ITM restate is detectable, and renders a not-carried nullable field as "ABSENT here (winner has X)". Per-field presence is reliable for nullable fields; the tool never claims a presence signal it can't prove.

**Robustness & MO2-disk correctness**

- **Every write is now crash-atomic — a power loss or crash mid-save can never leave a torn plugin.** All final-swap writes (the `.esp` patch, the BSA repack, the config save) now commit through one shared primitive that uses the Win32 atomic content-swap when the target exists and an atomic rename for a fresh file. Before, these used `File.Move(overwrite)`, which is not crash-atomic — it can unlink the destination before the rename commits. After, a crash leaves either the complete old file or the complete new file, never a missing or half-written one, and a cross-volume swap refuses loud rather than silently degrading to a non-atomic copy.
- **Re-running a write replaces the patch's own record in place instead of piling up duplicates** *(AlmightyChan — #44/#45)*. Re-running `create_record into=` an existing patch now replaces the patch's own same-EditorID record fresh at the same FormKey. Before, every re-run appended a duplicate, FormIDs crept upward, and external references went stale. The replace is never silent (flagged as same FormID kept, prior contents discarded), an attempt to clobber a carried override from the original plugin refuses loud and points you at `set_field`/`bulk_apply`, and leftover duplicate copies from the old bug refuse loud naming every copy's FormKey.
- **Concurrent tool calls can no longer collide on the same output plugin or cross-commit each other's bytes.** The whole resolve-stage-commit of a write is now serialized behind a write gate, and output-path allocation runs under the same lock. Before, the MCP SDK dispatches tool calls concurrently with no mutual exclusion, so two writes defaulting to the same plugin name could allocate the same folder and cross-commit (one call's success message shipping the other's bytes), and concurrent `into=` extends could silently lose the first call's edits. An instance switch can no longer tear a write in flight.
- **Plugins in MO2's overwrite folder now resolve, and a copy there correctly wins on top of the load order.** Plugins living in MO2's overwrite folder — where tool outputs land (Synthesis patches, xEdit "new file", Wrye Bash) — are now resolved, beating every mod (MO2's own rule) with enabled mods next and the game Data folder as the lowest fallback. Before, these were unresolvable and the warning misdiagnosed them as a stale-profile problem a re-sort can't fix. The lazy-mtime contract is unchanged: a plugin in overwrite changes the winning path once the profile files change, which MO2 writes on refresh.
- **A refused write no longer leaves behind an empty orphan folder.** When a write is refused at pre-flight ("NO patch written"), houseCARL now removes the empty folder and `meta.ini` it had created instead of accreting `_001`/`_002` folders on every retry. The deletion is content-checked — it only removes a folder holding nothing but our own `meta.ini` and an empty staging dir, never a reused `into=` folder — and the same cleanup covers failed rider tools (`bsa_repack` / `compile_script` / `decompile_script`), keeping (and naming) any folder that holds real output.
- **Your saved MO2 instance and tool paths can no longer be silently wiped, and a corrupt config is recovered loudly.** The user config now writes atomically (temp + rename) and guards the whole read-modify-write under a cross-process mutex, so the CLI plugin and desktop app sharing the file can't clobber each other. Before, a non-atomic write plus a corrupt file silently parsing as blank meant the next update wrote blank-plus-one-field back with `ok=true`, wiping your saved instance and tool paths. Now a corrupt file is backed up to `.corrupt.bak` and reported (a RECOVERED line on `set_mo2_instance`/`set_tool_path`, and at boot). Never silently blank.
- **Reads and writes now answer from a single consistent snapshot, and freshness no longer misses backups or restores.** The write path captures one index view up front and answers every edit's resolve/fetch/excluded-check off it (before, it re-resolved per edit, so a freshness rebuild landing mid-loop could resolve two edits of one call against two different builds — a silently mixed patch). Freshness detection was hardened too: it compares profile/ini mtimes by value rather than wall-clock, so MO2's "Restore Backup" (an older mtime) is no longer invisible; SetInstance stamps its baseline before the read; the status line is snapshotted under one lock; and a concurrent read's refresh defers rather than rebuilds under an in-flight write. The promise that an answer reflects current MO2 state holds without a daemon.

**Ergonomics & setup**

- **Load-order status now names the MO2 instance and reads any profile without switching to it.** The status header shows the resolved instance path, the default view lists the other profiles available, and `profile=<name>` inspects an inactive profile's mods and plugins read-only, leaving the active profile untouched. (Instance-mode only; a `profile=` read refuses loudly in explicit-paths mode, an unknown name lists the real profiles, and never-opened stray profile folders are skipped so they can't render as an all-zero phantom.)
- **The Papyrus compiler and BSA tools auto-detect their game directory and the real Steam install.** houseCARL looks for `PapyrusCompiler.exe` under the load order's game directory, and for the common MO2 "Stock Game" layout — where the load order points at a copy with no Creation Kit — it also locates the real Steam install (App 489830) where the CK and vanilla script sources live, with no new dependencies. A genuinely missing dependency now names exactly where it looked, and tool paths are locked so they survive an instance switch. (`housecarl_set_tool_path` becomes a fallback rather than a requirement.)
- **`housecarl_compile_script` takes an `output_dir=` so the compiled `.pex` lands where you choose.** The folder is treated as user-owned, so residue cleanup never deletes it, and deployability reporting is tight — only `<mods>\<modFolder>\Scripts` or `<data>\Scripts` counts as deployable, so a bare or nested path warns instead of falsely reporting a clean "done".
- **The setup installer pre-flights a locked, running server before it overwrites anything — and names the locked exe.** Re-running `houseCARL-Setup.exe` over a live install used to try to overwrite the running `housecarl-mcp.exe`, throw mid-copy, and leave a half-updated tree behind a generic "setup did not complete". Setup now checks for a locked server exe at every destination it would touch (both the Claude and Codex install locations) before any copy runs and refuses with actionable "fully quit Claude/Codex, then re-run" guidance — a clean first install is never blocked. A sharing-violation that slips past the pre-flight is caught with the same guidance, while an unrelated IOException (disk full, etc.) still fails honestly, and the refusal is worded precisely: "nothing was changed" only for a true pre-flight refusal.

**Display & schema honesty**

- **Display-honesty fixes across record schemas, Nexus text, and patch naming.** A read-only-projection leak was closed in the record-schema corpus: Mutagen concrete-class getters with no mutable twin (e.g. `SkyrimMultiModOverlay`, `MergedCellBlock`) were leaking into the catalog as the sole legal arm of their container and are now filtered to authorable arms only (losing no writable coverage — `CellBlock` even becomes a normally-composable struct — and caught by construction going forward via a new corpus guard). A cosmetic sweep fixed several rendering bugs: a dotted patch name like `My.Cool.Patch` was being clipped at the first dot (corrupting the plugin and MO2 folder name) and now strips only a trailing plugin extension; Nexus description truncation no longer splits an emoji/CJK glyph in half; double-encoded HTML entities decode correctly; and several stale tool descriptions/comments were corrected to match actual behavior.
- **The `mutagen-reference` skill now documents field addressing and per-op support.** It spells out the bracket/dot path grammar for reaching a field, a list element, or a dict entry, and which write verbs (Set / Add / Remove / ReplaceAll / SetAtIndex / Merge) each field shape accepts — so a write is composed from the reference rather than guessed.

## 1.2.3 — 2026-06-11

Opens the script-property write surface, adds a write-and-verify loop, and hardens tool-argument handling
and setup — carrying houseCARL's first outside code contributions (thanks, **WraithFallen**). No change to
the tool set.

- **Script-property (VMAD) writes work** *(WraithFallen — #35/#38)*: paths and composes that go through a
  polymorphic field's arms — setting a script property's target object, editing a quest alias's script
  fields, adding a `ScriptObjectProperty` to a script's property list — now validate against the arm's own
  schema and write end-to-end. Before, the write pre-flight couldn't see fields that exist only on one arm
  of a polymorphic type, so those edits were rejected; reads already worked. The surface is generic over
  every polymorphic family — no per-type wiring — and the cases an arm can't support still fail with a
  named reason.
- **Malformed tool arguments coerce or fail by name** *(#36, string-encoded-array case by WraithFallen)*:
  Claude Code's client sometimes sends an array argument as a plain string or as a JSON-array-in-a-string
  (`"[\"A.esp\"]"`); both shapes now bind as the array they spell. A missing required argument refuses by
  name, an uncoercible shape fails with a named reason, and an unexpected error inside any tool now returns
  a named error — never the SDK's generic text.
- **Write-and-verify in one step:** the write tools gain an opt-in `full_readback=` that returns every
  record the write touched or created — in full, read back off the *written file* — so a patch can be
  verified before it's ever enabled in MO2.
- **Honest answer for a plugin that isn't in the load order:** a `plugin=` read naming a plugin that isn't
  in the current order now gets its own named error saying exactly that, instead of the false "does not
  define this record".
- **Consistent answers while the load order changes:** each logical operation now reads from one captured
  index snapshot, so an MO2 change landing mid-query can no longer tear a result (winner, touching list,
  and counters always come from the same view).
- **Setup is self-contained and pre-flights the runtimes:** `houseCARL-Setup.exe` no longer needs .NET
  installed to run, and it checks for *both* required .NET runtimes up front with a specific fix message.
  The install docs are corrected accordingly — installing the ASP.NET Core Runtime does **not** include the
  base .NET runtime.
- **Authoring skills load for reading, not just writing:** the SkyPatcher / SPID / KID skills now also fire
  when *interpreting or auditing* an existing INI — "what does this `_DISTR.ini` do", "is this NPC
  affected", "why isn't this line applying" — so those answers come from the bundled grammar references
  instead of memory.

## 1.2.2 — 2026-06-10

Fixes four issues surfaced auditing a Requiem load order — all in reads and writes, no change to the tool
set.

- **New records get valid FormIDs:** creating a record in a patch that was first written by a bulk apply
  could allocate FormIDs starting at `000000` — the null range the game and other tools reject. houseCARL
  now floors every new-record allocation at `0x800` (the user range Bethesda reserves) from every write
  path, and persists a floored high-water mark into the patch so later edits and removals never regress it.
  Patches that already carry a `000000` record stay readable and editable — nothing is auto-renumbered,
  which would break references.
- **Conflict diffs compare real content:** the conflict tree's "what differs between these overrides"
  comparison looked only at top-level field counts, so two overrides that changed the *contents* of a list
  or struct without changing its length could be reported as identical. The diff now walks the record's
  full depth — list elements compared order-insensitively, sub-structs and nested values compared by value
  — so a genuine deep difference is no longer missed, and the output stays honest when a record is too
  large to fully expand.
- **One unreadable record no longer breaks a whole query:** a single record Mutagen can't parse — for
  example a malformed perk an upstream ESP ships — used to abort an *entire* `housecarl_cross_plugin_query`
  that scans references, returning nothing. houseCARL now isolates the offending record, scans past it, and
  reports how many records were skipped and why, so the rest of the results come through.
- **Form-targeted conditions read correctly:** a condition that points at a form — `HasPerk`,
  `HasMagicEffect`, `GetInFaction`, and the like — used to render a placeholder instead of the form's
  FormID when read. houseCARL now resolves the target through its link, so those condition payloads show
  their real FormID.

## 1.2.1 — 2026-06-08

Adds value-based record querying and a fuller Nexus lookup, and fixes several read and write rough edges.

- **Query by field value:** `housecarl_cross_plugin_query` gains a `where=` filter that matches records
  by a field's *value*, not just by record type or plugin — e.g. `where="MagicSkill = Destruction"` or
  `where="BasicStats.Damage >= 50"`. Operators are `=`, `!=`, `>`, `>=`, `<`, `<=`, and `contains`, and
  multiple `where=` conditions are ANDed. It works on any field you can read (by construction — the
  filterable set is the readable set); a path that can't resolve fails loud rather than silently matching
  nothing.
- **Full Nexus descriptions:** `housecarl_nexus_mod` takes an opt-in `description=true` that returns a
  mod's full Nexus page write-up — cleaned from the page markup to plain text — instead of only the short
  catalogue summary.
- **Write into an active patch:** writing into a patch that is itself active in the load order no longer
  fails with a file-lock error — houseCARL releases every mapped handle on the target before it saves.
- **Deep reads of condition-bearing records:** a deep read (`depth` 5+) of a record that carries
  conditions — a perk, spell, or magic effect — no longer floods the output with .NET reflection
  internals; the descent now stops at the modeled record content, so the real values stay visible.
- **Scoped queries show the scoped record:** under a `plugins=` scope, `housecarl_cross_plugin_query`
  now renders each match from that plugin's own record body rather than the global load-order winner.

## 1.2.0 — 2026-06-07

houseCARL reads Nexus Mods directly, and gains a community-contributed Papyrus performance reviewer.

- **Nexus Mods lookups:** two keyless, read-only tools — `housecarl_nexus_search` (search the Skyrim SE
  catalogue) and `housecarl_nexus_mod` (one mod's version, requirements, and *true* latest release — its
  newest MAIN file, since a mod's own version header can lag) — answer Nexus questions directly through
  the public Nexus catalogue API: no browser, no
  account, no API key. Read-only — houseCARL finds and informs; downloading stays your mod manager's
  "Mod Manager Download" handoff. Offline-tolerant: with no connection it says so plainly and every
  local capability keeps working.
- **`papyrus-optimization` skill:** a bundled Papyrus performance reviewer — classify each part of a
  `.psc` as broken / suboptimal / clean, explain what makes it heavy, and give the fix (event-driven,
  caching, states, native offload). houseCARL's first community-contributed skill, by DrHeisen.

## 1.1.3 — 2026-06-07

Hardens houseCARL against a malformed plugin that could otherwise make every command fail.

- **Resilient load-order indexing:** a single record Mutagen can't parse — for example a malformed package
  data-input count that an upstream ESP ships and the game engine ignores — used to make *every* houseCARL
  command fail with a Mutagen error, because the whole-load-order index is built up front and one bad record
  threw the entire build. houseCARL now isolates the offending plugin: it is excluded from the session and
  reported in `housecarl_load_order_status` (with the reason why), while every other plugin stays fully
  readable. Fix or remove the upstream plugin to restore access to it.

## 1.1.2 — 2026-06-06

Fixes a silent lookup failure in the Papyrus reference skill, and ships the corrected third-party credits.

- **Papyrus reference lookup fix:** the `papyrus-reference` skill documented its function-index grep with a
  format that no longer matched the shipped index, so a lookup written from the docs matched zero lines even
  for functions that are present — silently reporting a real function as "not in the corpus", the exact
  failure the skill exists to prevent. The doc now matches the compact index, uses a full-quoted-token match,
  and adds a self-check that validates the search against a known-present token before trusting an empty result.
- **Corrected attribution:** the bundled third-party notices now credit the distributor-grammar authors by
  name — Zzyxzz (SkyPatcher) and powerofthree (SPID + KID) — and list the KID-authoring skill.

## 1.1.1 — 2026-06-06

houseCARL now points you at your logs — completing the external-tool bridge.

- **Log folders in status:** `housecarl_load_order_status` now surfaces the resolved Papyrus script-log and
  SKSE crash-log folders, so houseCARL knows where to read them when you ask about a Papyrus error or a
  crash. Set a folder explicitly with `housecarl_set_tool_path` (`papyrus_logs` / `crash_logs`); when one is
  unset, houseCARL auto-detects the default location and says so, or tells you exactly how to point it at
  yours. Logs are the one bridge dependency with no wrapping tool — you Read the `.log` files directly.

## 1.1.0 — 2026-06-06

houseCARL now drives the external modding toolchain, not just the data layer.

- **Tool bridge:** `housecarl_set_tool_path` registers — and auto-detects — the external tools houseCARL
  wraps. When a tool a command needs isn't set, houseCARL fails loud with the exact path it wants, rather
  than silently doing nothing.
- **Papyrus compile:** `housecarl_compile_script` compiles a `.psc` through the Creation Kit's
  `PapyrusCompiler.exe`. Compiler warnings are non-fatal, and the recompile is non-destructive — the
  existing `.pex` is overwritten only when the compile succeeds.
- **BSA archives:** `housecarl_bsa_list`, `housecarl_bsa_extract`, and `housecarl_bsa_repack` wrap BSArch
  to inspect, extract from, and repack `.bsa` archives. Repack is non-destructive — the target archive is
  replaced only when the pack succeeds.

## 1.0.1 — 2026-06-05

- **Descendable reads:** `housecarl_read_record` / `housecarl_batch_record_detail` gain a `depth`
  parameter. `depth=1` (default) is unchanged; `depth>=2` enumerates the contents of lists,
  dictionaries, and sub-structs — each element shown with its index and an identity (e.g.
  `VirtualMachineAdapter.Scripts[0].Properties[5] = [ScriptObjectProperty] Name=...`) — so nested
  elements and their indices are visible in one call instead of probing each `[i]` by hand.
- **Bracket-grammar discoverability:** reading a collection with a dot-index (e.g. `Aliases.0`) now
  returns an actionable hint to use brackets (`Aliases[0]`); bracket indexing is documented in the
  read/write tool descriptions.

## 1.0.0 — 2026-06-03

Initial release.

- Local MCP server (stdio) with [Mutagen](https://github.com/Mutagen-Modding/Mutagen) kept warm in
  memory. Claude Code launches it — no port, no window, no manual start.
- **Reads:** the true load-order winner for any record, plus the full conflict tree on request; batch
  record detail; cross-plugin queries.
- **Writes:** set / add / remove fields, leveled-list and container edits, condition-target
  re-targeting — emitted as a **new** MO2 mod folder (`houseCARL - <name>`), originals untouched.
  Create brand-new records; remove records and individual entries; unused masters cleaned automatically.
- **Reflection-driven coverage:** every record type Mutagen models is readable and writable by
  construction — not a hand-maintained subset.
- **MO2 integration:** the instance is chosen via a folder picker at enable time; the active profile and
  load order are read statically from the instance's profile files and refresh automatically on the next
  tool call (MO2 need not be running).
- **Bundled skills:** `mutagen-reference` (record schemas), `papyrus-reference` (Papyrus + SKSE
  signatures), `skypatcher-authoring`, `spid-authoring`, and `kid-authoring`.

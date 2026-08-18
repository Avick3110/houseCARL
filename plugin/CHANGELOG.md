# Changelog

All notable changes to houseCARL are documented here. Versioning is [semantic](https://semver.org);
the `version` in `.claude-plugin/plugin.json` is bumped on each release, so installed users update only
when it changes.

## Unreleased

- **Fixed: on a large load order, a mod's broken references could vanish from `housecarl_check_errors` entirely.**
  The dangling-reference listing budget (`limit=`, default 1000) is one counter spent plugin by plugin in load order,
  and the base-game masters sit at index 0. A plugin whose findings collected an empty list was then dropped from the
  report altogether, so its broken references were not merely buried — they were unreachable, and the only trace was a
  global "capped at limit" line that named no plugin. The budget is now spent on every other plugin **before** the
  base-game masters, whose dangling references are permanent vanilla leftovers no load order can fix, and a capped
  listing now says how many plugins lost entries, names the ones that lost the most with a count each, and states
  how many it did not name.

  The response also splits the dangling total ("N of M come from the base-game masters ...; K from the rest"), and
  `counts_only=true` adds a **by-SOURCE-plugin** histogram beside the existing by-TARGET one: the target axis names the
  absent dependency behind a wall of findings, the source axis answers how much of the wall is vanilla and how much
  your mods introduced. Baseline means Mutagen's own base-master set — Skyrim, Update, Dawnguard, HearthFires,
  Dragonborn — and the response names them rather than leaving "base-game" to interpretation; Creation Club plugins are
  not in that set and still count as part of your load order.

  On a very large order this frees the budget without making every finding listable: measured on a 3800-plugin order,
  the base masters account for 398 of 4996 dangling references. That is what the named omissions and the source
  histogram are for — they tell you which plugin to scope `plugins=` to next, instead of leaving you to guess that
  anything was missing at all. Scoping to one plugin re-spends the whole budget on it, which lists its set in full
  unless that one set is itself larger than `limit=`.

- **Fixed: the refusal for "no such patch to extend" now says how to give a new patch a name.** Guessing
  `into="My Cool Patch.esp"` for a patch you have not created yet is refused, correctly — but the only remedy it
  offered was to omit `into=` and create the patch fresh, which is precisely the call that produces a generically
  named `Patch.esp`. `patch=`, the parameter that names a new patch, was documented only in the parameter list
  itself and in no workflow doc you would read first. The refusal now hands back the call you meant, with your own
  guessed name already in it — `pass patch="My Cool Patch"` — and says what omitting it costs. This matters more
  than a wording nit because there is no rename primitive: the name you get on the first write is the name you
  keep. `patch=` is now documented as well, in the core workflow beside `into=`.

  It offers the name without promising the filename, because houseCARL auto-suffixes a name that is already taken —
  either by a mod folder it already made under that name, or by an active plugin. **Read the patch name back off
  the response**; both names the sentence mentions carry that qualifier, the one you choose and the `Patch` default.

  The rule for which tools say it: exactly those whose `patch=` names a **new** patch defaulting to `Patch` — the
  field-write, record-creation and forward lanes. Everywhere else the shorter, always-true remedy stands, because
  the naming sentence would be false there: a removal edits a patch that already exists and cannot create one; a
  closure copy or an appearance copy names its fresh patch after the new EditorID; and the tools that place a file
  into a patch folder each default to their own stem, never to `Patch` — with `bsa_repack` a further case, where
  `patch=` names the `.bsa` itself rather than the mod folder.

- **Fixed: reading a cell's placed references could report an empty cell the game fills.** Placed references, a
  topic's INFO lines and a worldspace's cells are declared **per plugin**, and the game assembles a parent's
  children from every plugin that declares them. So a plugin that overrides a cell for an unrelated reason —
  occlusion planes, lighting, music — carries no references and deletes none either, yet reading that winner's
  `Persistent` / `Temporary` showed you its own empty list with nothing to suggest otherwise. Anyone auditing
  "what is in this cell" through the winner got a plausible, confident, wrong answer: on one order, Dawnstar's
  exterior cell read 0 references at its winner while the game loads the 201 that `Skyrim.esm` declares.

  `housecarl_read_record`, `housecarl_batch_record_detail` and `housecarl_cross_plugin_query` now say so, in two
  tiers. **A load-order read** of a field that owns child records notes how many other plugins touch that record
  and that this read did not open them — free, because it comes from the load-order index rather than from opening
  anything. (Two lanes deliberately stay silent, because a load-order statement would be wrong there: a read
  scoped to a file outside the load order, and the SkyPatcher overlay pole.) **`conflict_tree=true`**, which already fetches every touching plugin's body, additionally **names
  which of them declare content** for each field, at no extra cost. The split is deliberate: naming the plugins
  requires reading their bodies, and doing that on every read took a single Dawnstar cell read from 27 ms to
  588 ms, a worldspace read to 2.5 seconds, and a whole-order cell catalogue past ten minutes.

  What is annotated comes from the same list the write surface uses to preserve children — a cell's `Persistent`,
  `Temporary`, `Landscape` and `NavigationMeshes`, a topic's `Responses`, a worldspace's cells — never a
  hand-kept set of cell fields, and it costs nothing on a read of any other record type. Two kinds of child field
  get two different statements, because two different things are true of them: `Persistent`, `Temporary`,
  `NavigationMeshes`, `Responses` and a worldspace's cells hold **many** children and are merged from every plugin
  that declares any, while `Landscape` and `TopCell` hold **one** record that plugins override — for those, other
  plugins carrying one does not mean a bigger total, it means load order decides which you get, and that the child
  exists even though the body you are reading has none. A plugin that merely touches the parent without declaring
  children is never named, including a worldspace override carrying block scaffolding but no cells. Reading a
  specific plugin's version is annotated too, because that body is not the whole set either — the plugins **above**
  it declare children it cannot see. The value itself is untouched: the annotation sits beside it, and a write can
  still reuse the token verbatim. A `to_file=` artifact carries the note on its rows and the sentence explaining it
  on the manifest line, so a catalogue you re-open next week still says what it means.

  Note what this is not — houseCARL does not yet have a read that answers "what is actually live in this cell"
  (every child at its own winner, minus the deleted and disabled ones); that is separate work, and this annotation
  deliberately promises nothing about it.

- **`housecarl_copy`** — copy a record together with the records it depends on (its link closure) into a patch under new FormIDs, so the result no longer masters the plugin you copied from. The walk starts from the link-bearing fields you name (`seed_paths=`) and is bounded by the record types you exclude (`exclude_types=`), so the tool stays generic and the domain knowledge is yours to supply. `from_source=` is an ordered list of sources tried first-hit-wins — `winner` for the load order's winning version, or a plugin filename, active or sitting in a disabled mod — and the result names which source produced each copied record. Destination is either an existing record (`target=`) or a fresh clone (`new_editorid=`), whose remaining links into the source are stripped and reported by name — including when clearing one takes a whole property with it. When nothing is being copied away from at all — the source and every named source are base-game masters — the result is reported as an appearance transplant rather than a standalone-ization, because an always-loaded master is not being removed from anything. An off-order link the finished patch still carries is refused, and the refusal names which cause: your own `Type:stop`, a field `seed_paths` never named, or a record an earlier call left in the patch you are extending.

- **New skill: `npc-appearance-copy`** — the domain half of `housecarl_copy` for NPC faces, carried as data and flow rather than as tool code: which four link-bearing fields seed the walk, why a Race is excluded rather than walked into, which inline fields (tints, morphs, `TextureLighting`, weight) ride `housecarl_apply`'s copy zip instead, and how the FaceGen mesh + tint are placed under the new FormID from the **named donor** rather than the VFS winner. Also states the two things the split costs: three calls means three refusal surfaces, and a donor sitting in a disabled MO2 mod is a records-only job until its files can be named.

- **Fixed: the record reference called two owned child records FormLinks.** `Cell.Landscape` and
  `Worldspace.TopCell` hold a record outright — the cell owns its landscape, the worldspace owns its top cell — but
  the schema the `mutagen-reference` skill serves classified both as links to one. Following that, you would have
  set a FormID on a field that holds a whole record: accepted at the pre-flight check, then thrown at write time.
  Both now read as what they are, pointing at the child's own record entry, and every way of writing *at* the field
  answers in one voice that names it a record: a FormID, a composed value, a list of composed values, clearing it,
  and copying it across from another plugin. To edit the child, address **that record by its own FormID** — which
  works, and is what the refusals tell you. Three things worth knowing, because they are what the old classification
  hid. A patch's override of a parent does **not** bring the parent's child records with it, so a path *through* the
  parent finds nothing there even when the original carries one. Clearing the field would have deleted the whole
  child record and everything under it — a worldspace's top cell takes its persistent references with it — with no
  element named, where the list form of the same family makes you name which child by index. And copying the field
  from another plugin would have written *that* plugin's record, with its own FormID and its own children, in as
  this parent's child; to carry a child record across, `housecarl_forward_record` takes it directly. Giving a parent
  a child it does not have, and deleting one outright, are both open gaps — the refusals say so and point at the
  issue rather than pretending a way exists.

  The classifier confused the two because a record identifies itself (FormID plus type) exactly the way a link
  identifies its target. A build check now compares the classification the generator produces against the
  independent walk that preserves child records across a record replace, over every record type the library models
  — so the two can no longer disagree about which fields own a record. A second check lists every write verb against
  this shape with the answer it must give, so a verb cannot be left behind again.

- **Fixed: several list inputs didn't name every member of the object they take.** `housecarl_bulk_apply`'s `operations=` didn't mention `composes` or `from_plugin`; `housecarl_bulk_create`'s `records=` didn't mention `grid`; the `operations=` on both create tools didn't mention `composes`; the `sets=` list inside a `compose=` named two of its five members; and `housecarl_bulk_place_asset`'s `assets=` didn't mention `source_provider` (the member's own description was published all along, so it was discoverable — just not where you'd look for the element shape). Every one of these worked already; the gap was in what you were told. Separately, `source_provider` now spells out that it applies with **no** `source=` as well: there it says whose copy of the *destination* path to place, and is what resolves the contention an omitted source is otherwise refused for.

  Behind it, each documented element shape is now checked against the names the wire really binds: every name a shape lists must exist, and every member must be listed by some shape that carries its type. A member cannot be added, or a wire name misspelled, without a red build. Restatements of a shape elsewhere — in a tool's own prose, or in a refusal — are not read by that check, and neither is the case of a member documented on one carrier of a type but not another.

*Accumulating notes for the next cut — not yet released; `plugin.json` still reads the last shipped version.*

**Added: `housecarl_place_asset` can now place a copy from one path under a different name, and say whose copy to
take.** `source=` accepts a Data-relative path — the same kind of path `housecarl_asset_status` reports on — and
resolves it through the virtual file system, so the source no longer has to be a full path typed out from your
drive. Because the source and the destination are separate inputs, a source that differs from the destination is a
**rename**: one file's bytes land under another file's name, which is what carrying a baked FaceGen head onto a
different NPC's FormID path actually is. The new `source_provider=` says *whose* copy to read: `*winner` for
whichever copy currently wins, or a mod folder / BSA filename on its own. The `*` is part of the token, and it is
there so the two never collide — `*` is illegal in a Windows folder name, so a bare name always means a provider of
that name and a mod actually called `winner` stays reachable. Naming a mod means that mod: if it doesn't supply the
path, the call is refused rather than quietly served from somewhere else. Naming nothing keeps the old behaviour —
the sole provider, refused when several contend — except that the refusal now lists the providers by NAME rather
than by on-disk path, each in double quotes, and what's inside the quotes is exactly what you pass back.

**Fixed: the text and JSON responses to a write can no longer say different things.** Every write tool renders its
result twice — once as text, once as `format="json"` — and the two renders were maintained separately, so a warning
or a piece of advice could be corrected on one and left stale on the other. Nine sentences had already drifted that
way. The most consequential: the JSON note about creating an exterior cell had lost the clause saying a grid
collision is *engine* behaviour, so it read like something you could retry your way out of; a cut report block told
you "do not re-issue the write" on one transport and the more specific, actually-correct "do not re-issue the create —
that allocates the records again" on the other; and `write_seq`'s JSON standing-limit note had dropped the pointer to
`housecarl_validate_dialogue`, which is the half telling you what to do next. Every sentence named in the PR's inventory now has one
source that both transports read, so a correction reaches both or neither; the inventory also names what is left and
why. Wording is otherwise unchanged, except where the two copies already disagreed — those are resolved to the fuller
and truer reading.

**Fixed: a large `apply` no longer overflows its text response without saying so.** Its JSON twin has always dropped
trailing rows at `max_chars` and set `truncated`, but the text render listed every edit and let the host cut the
oversized response out of band — the silent cut `max_chars` exists to prevent, on the one write verb whose row list
was still unbounded. It now stops with the same explicit notice its four sibling renders carry. A truncated dry run
also no longer describes itself as a completed write.

**Fixed: forwarding a record no longer deletes the records nested under it (#324).** When `forward` targeted a record
the destination *already carried*, it replaced it by dropping the whole record and copying the source body in — and
the drop took the record's child group with it. Any dialogue line under a forwarded topic, or placed reference under
a forwarded cell, was destroyed, and the call reported success. The `in_place=` lane was the worse half: it deleted
those records out of your own plugin, on the lane that keeps no backup. The children are now lifted off before the
replace and re-attached after it, so a forward changes the record's *fields* and leaves what is nested under it
alone. Nothing about the forward semantic changes: the source's own children still stay in the source's plugin,
which is what lets a patched topic not fight another mod's added lines. If houseCARL ever cannot account for every
child across the replace, it refuses and writes nothing rather than emit a plugin it knows is short a record.

**Fixed: an in-place edit's "what landed" line is now read off the written file (#308).** The verify prints under a
banner saying every edited record was re-read off the file on disk — but the per-op half of each line ("`Add Ranks:
now 1 (+1)`") was read from memory *before* the file was written. When a composed structure exists in memory and
serializes to nothing — a Faction `Rank` composed with no fields is the reported case — the plugin didn't grow, the
list stayed empty, and the call still reported the addition as landed; it cost the reporter three more debugging
rounds on a live mod. That clause now comes off the re-opened file — for the op that last touched a given field in
the call; an earlier op on the same field is marked as the applied edit's own reading, because the file holds the
state after all of them and cannot answer for a middle one.

**houseCARL shows you both readings; it does not rule on them.** You get what the edit did in memory and what the file
says, and where the file could not answer, the response tells you which reading you are looking at. It deliberately
stops short of declaring "this op did not land" — deciding that means telling a real difference from a harmless one
(a percentage that rounds when saved, an internal type name, an empty field versus a missing one), and every version
of that judgement we built ended up accusing writes that had in fact landed. In JSON: `landed` (memory),
`landed_on_disk` (the file), and `landed_source` — `written_file`, `superseded` (a later op in the same call wrote
that field), `no_answer` (the file was re-read and didn't yield it), or `not_checked` (this lane runs no per-op file
check), plus `verify_ran` on the response. And the specific call that caused the bug is now
**refused before anything is written**, naming the fields that would give the structure content — a compose you gave
nothing to, whose every settable field is empty, cannot land, so houseCARL no longer pretends it did. **One shape that
used to work is refused with it:** composing an empty value and then filling its fields in a *later op of the same
call*. The check runs as the value is built and cannot see the ops after it, and the lane is all-or-nothing, so the
whole call is refused — the message says so and points at the one-op form (compose it *with* its fields), which cannot
half-land either way.

**Reads show the modelled type name for a sub-structure, never Mutagen's internal one.** A field like a weapon's
`BasicStats` read off a plugin on disk rendered as `[WeaponBasicStatsBinaryOverlay]` — the class houseCARL happens to
load — while the same field read from a record being edited said `[WeaponBasicStats]`. Same field, two names, and one
of them is an implementation detail. It now always reads `[WeaponBasicStats]`, in every read tool and in the in-place
write verify (where the two used to appear in a single response, two lines apart, looking like a disagreement).

**Fixed: adding a record inside an existing cell (or topic) no longer drags in the mod that wins it (#300).** Placing
a new reference into a vanilla cell pulled the cell's *load-order winner* into your patch — so a lighting overhaul
became a master your two new refs never needed, and the patch carried a frozen copy of that mod's lighting. Sorted
below the lighting mod, your patch then re-asserted those stale values, and would keep re-asserting them after the
mod updated. houseCARL now hosts the new child in the parent's **defining** plugin's version: no extra master, no
copied-in content from a mod you weren't patching. A placed reference lives in the cell's child group and survives
the cell record losing, so nothing about it needed the winner. Which plugin hosted the child is now reported per
created record (`parent: …`, `parent_host` in JSON) — including the two cases where the definer genuinely can't
answer (an injected record, or a plugin this session had to exclude), where the winner is still used and says so.

**Which shape you get is a decision, and houseCARL no longer makes it for you.** The host is a full record override and
still conflicts at record level, so wherever your patch out-ranks the mod that currently wins that cell or topic, the
parent resolves to the *defining* plugin's fields rather than that mod's. That is the **residual** shape: your patch
carries your child and a host lean enough to lose harmlessly.

Both shapes are legitimate and which one is right depends entirely on what the patch is for. A patch meant to *win* —
"patch this mod for my load order" — wants the winner's content carried forward, resolved. A patch meant to **feed**
one — "make this sit before the Reqtificator / Synthesis output" — must not master those, must not forward their
records, and must not bake in what they would regenerate. Same call, opposite right answers.

So this is now the base you build from rather than an answer imposed on you: the residual is what you get when nothing
else is said, because you can build the inlined shape deliberately, and you cannot cleanly subtract the winner's content
once it is baked in. The response **tells you** which mod currently wins that parent and which way the record will
resolve, so the choice is in front of you at write time. Whichever way you sort, the new child is carried.

**Building the inlined shape.** Forward the winner's version of the parent into a patch, then create into that patch —
your new record is hosted in the forwarded body, and you get both. Either order works: forwarding onto a parent a
patch already holds keeps the records nested under it (that order used to destroy them, which is the `forward` fix
above in this same release). The write response names the remedy too.

**Fixed: a full path to an *active* plugin is no longer treated as an off-order file (#321).** `housecarl_apply`'s
`CopyFrom` decided "is this source in the load order?" by looking up the name it was given, and a full path never
matches a filename — so `from_source=C:\…\SomeMod\Bar.esp` pointing at a plugin your order is actively serving read
the file directly and described the source as not in your load order. Usually that was only a wrong label; under a
profile switch, where the same filename is served by a different mod folder, it was a wrong *body*. `housecarl_forward` has had the rule since its own fix — a path that names the exact file your
order loads is that plugin — and `CopyFrom` now shares it. A path to a same-named *backup* still reads
off-order, as it should: the test is the file, not the name.

**`housecarl_write_seq` can finally put the `.seq` in the mod's own folder — `output_dir=` (#312).** Editing a
plugin **in place** left the two halves in different mods: the `.esp` in the mod's own folder, the `.seq` in a
freshly cut `houseCARL - houseCARL_SEQ_00N`, with `into=` unable to name a folder houseCARL didn't create. Every
regeneration then meant hand-comparing the temp output against the mod's live `Seq\` copy and deleting the
folder. `output_dir=` takes the mod-folder root and appends `SEQ\` — the same contract `housecarl_compile_script`'s
`output_dir=` already uses, including the don't-double-it guard, no houseCARL folder cut, your folder never touched
by cleanup, and a warning when the destination isn't somewhere the game actually reads SEQ files from (your MO2
mods folder, the overwrite folder, or the game's Data). It wins over `patch=`/`into=`, and says so rather than
quietly ignoring them. If a `.seq` was already at that path it is overwritten with no backup, and the response now
says `replaced` rather than `wrote`.

**The MO2 overwrite folder now counts as a place the game loads from.** `housecarl_compile_script`'s `output_dir=`
warned that a `.pex` landing in your overwrite folder wouldn't deploy. It does — MO2 maps overwrite onto the game's
Data at top priority, and it's where xEdit/CK/Synthesis output lands — so that warning was a false alarm. It no
longer fires there, and `housecarl_write_seq`'s new `output_dir=` shares the corrected rule rather than inheriting
the old one. Sharing the rule also fixed a second `compile_script` bug on the way past: `output_dir=C:\` (a bare
drive root) produced the drive-*relative* `C:Scripts`, so the `.pex` landed in whatever the working directory
happened to be on that drive. (The rule is otherwise unchanged and
still exact: a mod's own `Scripts\`/`SEQ\`, the overwrite folder, or the game's `Data\` — a *nested* path inside a
mod still warns, because MO2 would deploy it to `Data\<Sub>\…` where nothing loads it.)

**…and regenerating an unchanged `.seq` now writes nothing.** When a lane names the destination (`output_dir=`,
`into=`, or the plugin's own houseCARL folder) and it already holds exactly the bytes houseCARL would write, the
file is left alone and the response says `unchanged` (`written: false` in JSON) instead of churning the file.
Re-running after every in-place edit — which is the point of the tool — is now free. A skipped write still stamps
the file's timestamp forward when it was older than the plugin, so `housecarl_validate_dialogue`'s SEQ staleness
check keeps agreeing with it.

**Fixed: one broken plugin in your load order no longer breaks every write.** If a plugin houseCARL cannot open
sat in your active order — a truncated download, a half-copied mod folder, an interrupted xEdit save — then
*every* write failed, including writes that had nothing to do with it, with an opaque message that read like a
disk fault. Reads were never affected. Now writes that don't reference that plugin's records just work.

A write that genuinely *does* need it (its records can still be reached through a plugin that overrides them) is
refused **naming it as the cause**, with what to do — instead of the previous engine-level error. And if the
unopenable plugin is `Skyrim.esm` or `Update.esm` — the two masters every written plugin must list — nothing is
written at all until you repair it, rather than a plugin landing on disk without them.

**New tools: `housecarl_create`, `housecarl_remove`, `housecarl_forward` — the rest of the 2.0 write surface
(W3 PR 2).** The same shape `housecarl_apply` introduced — *what changes* × *where it lands* × *how it reads
back* — now covers authoring, removal and whole-record overrides.

- **`housecarl_create`** replaces `create_record` + `bulk_create`. `records=[{record_type, editorid, ops?,
  parent?, collection?, grid?}]` — **one record is a set of one**, so the single-record call is the degenerate
  case and the nested one-shot (a dialogue topic *and* its lines, a cell *and* its placed refs) needs no second
  tool: parent a spec on an **earlier sibling's editorid**, and reference one from a FormLink value as
  `'@editorid'`. `ops=` is `apply`'s op shape minus `formid` (a new record's id is allocated and reported back);
  the members a create *cannot* have — `formid`, `from`, `from_source` — are refused **by name** with what to do
  instead, rather than dropped. `records=` also takes `"@<absolute path>"`. Creating dialogue lines still
  reports **voice coverage** and **result-script binding**, and creating cells still reports the world content
  houseCARL does not author — now as typed data in `format="json"` too, not only prose.
- **`housecarl_remove`** replaces `remove_record` and is **plural**: `formids=` is set-valued, so dropping ten
  overrides is one call and one re-serialize instead of ten rewrites of the same file (the engine always took a
  list; only the tool surface didn't). All-or-nothing is unchanged — one target the file doesn't carry refuses
  the whole call. Its lane is **`into=`** (a houseCARL patch) or `in_place="X.esp"`: a removal edits an artifact
  that already exists, so it has no `patch=`, and naming *no* lane is refused with both spelled out.
- **`housecarl_forward`** replaces `forward_record`. `from_plugin=` is now **`source=`** — whose version to
  copy (a master reverts a record to vanilla). Unchanged otherwise: the response names
  the winner each copy will out-rank, a forward that was already winning is flagged redundant, `into=` replaces
  a FormKey the patch already carries, and `dry_run=` runs the real pipeline and stops before disk.

**`housecarl_forward` can now copy from a plugin that is NOT in your load order.** `source=` takes a **disabled
mod's plugin**, an unticked one, a folder MO2 never registered, or a full path to any copy on disk — the same
"read it wherever it lives" contract the read tools already had, and the same one `housecarl_apply`'s `CopyFrom`
already had. Re-asserting a disabled *old* patch's version of a record, or reading an old version's body to
convert it, no longer needs the mod switched back on first. Previously this was refused by name.

The response **states which copy on disk it opened** — a filename several mod folders provide identifies no
file, so it is reported rather than assumed (`format="json"`: `source_in_order` plus a `source_read` object),
along with the fact that the `epoch=` stamp fingerprints the *active* order and so does not cover that file's
content. A name several folders provide is refused naming each of them, with the full path as the fix. Two
things are still refused, by name: forwarding a record whose **origin plugin** isn't active (the patch would
need it as a master, which it cannot invent — unless that origin *is* the patch being written, since a plugin is
never its own master), and naming as `source=` the very file being written. A body that references some *other*
inactive plugin is refused **naming it**, instead of reading as a disk fault. And because a plugin's records are
keyed by its **filename**, a copy parked under a new name (`MyPatch_old.esp`) is a different plugin — that
refusal now says so rather than reporting the record as simply absent. Nothing changes for an active `source=`
— including a full path to one, which is recognised as that plugin rather than read as an outside file — and
`in_place=` is unaffected: the *target* of an in-place write must still be an active plugin.

All three take the one lane spelling (`patch=` | `into=` | `in_place="X.esp"` + `acknowledge=`, mutually
exclusive and **refused by name** when two are given) — except `housecarl_remove`, which has no `patch=` for the
reason above, so its lanes are `into=` | `in_place=`. All three also take `format="json"`, `max_chars=`, and the
`epoch=` stamp — **including the first-touch in-place consent prompt**, which previously carried none on these
lanes. In `format="json"` the `lane` field names **the parameter the call used** — `"patch" | "into" |
"in_place"` (`remove` can only report the last two), the
same three words on every write tool (`housecarl_apply` reported the `into=` lane as `"extend"`; it now says
`"into"` like its siblings), and it is correct on refusals and the consent prompt too, not only on success. The 1.x
tools are unchanged and stay registered through the build waves; from 2.0.0's clean cut a call naming one
answers with its successor spelling.

**`housecarl_write_seq` keeps its name and moves to the 2.0 vocabulary.** `plugin=` is **`source=`**, and it
now **resolves a plugin filename** across your MO2 mod folders (enabled, disabled, not-yet-listed), the
overwrite folder and game Data — an absolute path still works, and the response **states which copy it read**
(a filename several folders provide is refused, naming them). `patch_name=` is `patch=`; `format="json"` and
`max_chars=` are new. This call consults no load-order build, so it carries **no epoch — stated as a fact with
its reason** rather than left as a missing field.

**New tool: `housecarl_apply` — the 2.0 field-write surface (W3 PR 1).** One write call composes *what changes*
× *where it lands* × *how it reads back*, replacing `set_field` + `bulk_apply`. **Edits:** `ops=` is the edit
list — one op is a set of one, so the old single-field call is the degenerate case — and it takes the inline
array **or `"@<absolute path>"`**, the `@file` convention that retires `from_file=` along with its
inline-vs-file mutual exclusion. Both lanes now read through the same strict parser, so an **undeclared op
member is refused by name inline too** (the SDK's binder silently *drops* one, which in a large generated batch
sends every downstream refusal chasing the wrong op); a 1.x spelling inside an op — `verb`, `from_plugin` —
gets the new word in the refusal, because the alias layer only reaches top-level arguments.
**Copying a field bundle between records** is `bundle=` (the paths, uniform across pairs) × `assignments=`
(`[{target, from, from_source?}]`) — a **zip, never a product**: each target reads its own paired source, so N
targets never fan out to N×N. Everything outside the bundle is untouched *by construction*, `from_source`
defaults to the source record's load-order winner, and a cross-record pair must be the same record type
(refused by name at pre-flight, with every bad pair reported at once). It composes with `ops=` in one call.
**One lane spelling:** a new patch (`patch=`), `into=` an existing one, or **`in_place="X.esp"` — the string
names the file being overwritten**, replacing the old `target=` + `in_place=true` pair. The three destinations
are mutually exclusive and **naming two is refused by name**, as is `acknowledge=` without a lane (1.x silently
ignored `patch_name=` under `into=`). `dry_run=`, the one-time in-place consent handshake, and the
all-or-nothing pre-flight are unchanged. **Transport:** `readback=`, `max_chars=`, `format="json"` (the same
data machine-readable — a refusal is a document too, and the consent prompt is its own flag rather than an
error), and **every write response now carries `epoch=`**, the identity of the index build its winners were
resolved from. `set_field` / `bulk_apply` are unchanged and stay registered through the build waves; from
2.0.0's clean cut a call naming one answers with its successor `housecarl_apply` spelling.

**Fixed: `CopyFrom` never worked on the in-place lane.** A legal `verb="CopyFrom"` edit with `in_place` resolved
no source record and fell through to the verb engine, which has no CopyFrom branch — so it failed as *"pre-flight
accepted it but the apply threw"*, reporting a missing capability as an internal fault. The in-place lane now
resolves copy sources under the same contract as the patch lane (active-order and off-order alike), and copying a
record's own field onto itself is refused as the no-op it is.

**`housecarl_records` completed: the comparison and traversal forms, every SOURCE pole, and the full SELECT
composition (W2 PR 2).** Every staging refusal from the core wave is now a capability. **Comparisons:**
`project={"form": "delta"}` diffs the SUBJECT (`source=`) against a REFERENCE (`versus=`) and returns only what
differs — `versus="previous_provider"` answers *what did this plugin change relative to what sat beneath it*
(measured FROM the subject, never from the winner; a mid-stack subject reports what outranks it as plain fact, a
defining subject refuses rather than rendering an empty diff, and a subject that doesn't touch a record refuses
naming its actual touchers); `project={"form": "tree"}` is the conflict view as a form — every provider of each
record, winner last, each diffed against the reference pole — **now with a json render and a row form, so trees
spill to artifacts like every other result**. **Traversal:** `walk=` follows record-to-record links from the
call's own selection (seed paths, a named `follow=` chain or full closure, caller-supplied stop/refuse
exclusions, recorded cycles, explicit caps); `project={"form": "chain"}` renders the paths with provenance —
and an NPC `follow="Template"` walk adds the TemplateFlags report: per inheritance category, whether the NPC's
own data is active or masked and WHICH record in the chain provides it. `walk={"direction": "reverse"}` over an
MGEF traces every SPEL/ENCH/ALCH/SCRL/INGR applying it with the matching entry's magnitude/area/duration (the
`effect_chain` job); any other reading form consumes what a walk reaches as its selection.
**`project={"form": "info_order"}`** renders a dialogue topic's effective MERGED INFO sequence across every
touching plugin — the order the game actually walks, MOVED lines annotated — with honest incompleteness
reporting when a contributor can't be read (a quest fans out by composition: `types=["DIAL"]
where=["Quest = <formid>"]`). **Poles:** `source={"overlay": "skypatcher", "state": "pre"|"post"}` reads records
around the SkyPatcher INI layer (post-state bodies for any reading form; pre-vs-post is a delta of the two
poles; INI content is declared outside the epoch fingerprint). **Composition:** `formids=` now composes with
every scan term (the identity set intersects the scan, or alone IS the scan's bound); a `plugins=` scope
combines with a named `source=` (the scope selects, the pole's version is read — untouched records counted and
named, never silently absent); and the out-of-load-order scan lane carries the complete grammar (multi-type,
the full `where=` predicate set over the file's own bodies, `references=`, windows, counts, artifacts,
comparisons). Every two-capture seam (scan→compare, walk→read, probe→scan) is epoch-compared — a load-order
change mid-call refuses loudly rather than mixing builds. The 8 absorbed 1.x read tools stay registered through
the build waves; from 2.0.0's clean cut, a call naming one answers with its successor `housecarl_records`
spelling instead of a generic unknown-tool error. Guarded by the extended `records-guard` (incl. the four
`previous_provider` probe pins) in CI; alias CENSUS re-baked (113 → 119 — `versus=`/`walk=` activate the
`plugin_a`/`plugin_b`/`mod_a`/`mod_b`/`mgef_formid`/`closure` hints).

**New tool: `housecarl_records` — the 2.0 read surface, core forms (W2 PR 1).** One read call composes *which
records* (SELECT: `formids=` incl. `@file`/artifact re-entry · set-valued `types=` · a structured `plugins=` scope
with `defined_in` inside it · `conflicts_only=` · the `where=` grammar · `references=`) × *whose version* (SOURCE:
the winner by default, or `source=` naming a plugin **wherever it lives** — active in the order or sitting on disk
unticked; the response states which arm resolved, a record the plugin doesn't touch is refused naming the plugins
that DO touch it, and a plugin found in neither place is refused naming both places searched) × *what shape*
(PROJECT: a form-scoped `project=` object — `identity | summary | fields | everything | aggregate`, each
sub-parameter living only inside the form that uses it) × transport (`text`/`json`/`dense`, exact windows,
`counts_only=`, `to_file=` and auto-spill riding the W1 artifact lane). The comparison forms (`delta`/`tree`/
`chain`/`info_order`), `previous_provider`, the SkyPatcher-overlay source, and `formids=`×scan composition arrive
in W2 PR 2 — until then those spellings get a **named staging refusal** pointing at the 1.x tool that does the job
today. The 1.x read tools are unchanged and stay through the build waves (they retire at 2.0.0's clean cut).

**The `where=` grammar grows the chartered 2.0 SELECT terms — on `cross_plugin_query` too.** New everywhere the
grammar runs: `startswith`; an `editorid` term (`editorid contains/startswith/= …`, `editorid missing` — the
`editorid_contains=` job as plain grammar, live even on records whose deep body can't parse); the **`winner`
provenance term** (`where=["winner = X.esp"]` — *which records does X win*; it reads resolution, not content, and
declares its winner-resolution cost); `in`/`not in` **generalized to any scalar leaf** (`"Race in
[XXXXXX:A.esm, …]"`, enum names and numbers too, artifact `@file` lists included); and a single **`->` link step**
(`"Perks->editorid startswith REQ_NULL_"` — the predicate crosses ONE form link and tests the target's winner body,
ANY-match over list targets, wrong paths still fail loud in the accounting). A stray `conflict_tree=`/`fields=`/
`depth=`/`group_by=` on the new tool answers with the form-scoping rule by name, and the alias layer maps the 1.x
spellings (`plugin=`/`mod=`/`from_plugin=` → `source=`, `formid=` → `formids=`, `type=` → `types=`) so first
guesses bind. Guarded by the new `records-guard` in CI; alias CENSUS re-baked (99 → 113 activations, all on the
new tool).

**Big results now come back whole, as a file — truncation stops losing data on the record-bulk reads (the 2.0
artifact disposition, W1).** `cross_plugin_query`, `batch_record_detail`, and `resolve` gain the §2.1.1 artifact
lane. Pass `to_file=<absolute .jsonl path>` to write the **complete** result — never limit-windowed — as one
self-contained JSONL file (line 1 is a manifest: the query that produced it, row count and schema, per-type counts,
and the build's epoch fingerprint; then one JSON row per record, the same rows `format=json` emits), with only the
manifest rendered into context. And when an ordinary response hits its `max_chars` ceiling, the complete requested
result is now **auto-spilled** to a server-managed results folder (pruned by age after 7 days) and the response says
exactly where it went — a `spilled:` block in text, a `"spilled"` object in json/dense, always naming the file. If
the spill itself cannot be written, the response says *that* instead — a truncated response never again implies the
tail is simply gone, or silently lacks the file it promised. Artifacts **re-enter** through the existing `@file`
spelling — `formids=["@<path>"]` on `batch_record_detail`/`resolve`, `where=["formid in @<path>"]` on
`cross_plugin_query` — yielding the file's identity column ("scan once, project forever"), and re-entry is
**epoch-checked**: if the load order changed since the artifact was written, the call refuses loudly naming both
fingerprints (there is deliberately no stale-override switch; plain formid-list files keep working unchanged, and
unchanged worlds fingerprint identically across restarts, so artifacts survive them). A truncated
`conflict_tree` view is the one shape that does **not** auto-spill (its trees have no row form) — the response says
so and names the alternatives, and a spill of a `limit=`-windowed result says it holds the *window*, never claiming
the full result. Guarded by the new `artifact-guard` (59 checks) in CI.

**Fixed: `check_errors` / `validate_scripts` refusals in `format=json` returned an empty string.** Both sweeps'
json renders returned before flushing the JSON writer on their error path, so any refusal — a scope naming a plugin
not in the order, an excluded plugin, a bad filter — rendered as `""` instead of an `{"error": …}` document. Text
mode was unaffected. Latent since the json sweeps shipped; surfaced by the epoch guard's refusal-render arm and now
pinned by it.

**Every record-lane response now names the load-order build it was answered from (the 2.0 epoch fingerprint, W1).**
Each index build gets a deterministic 16-hex fingerprint of the world-state it was built from (plugin set + order +
file mtimes) — unchanged order means an identical fingerprint, across rebuilds and server restarts alike. Bulk reads
(`cross_plugin_query` in all three formats and under `group_by=`, `batch_record_detail`, `resolve`, `effect_chain`,
`check_errors`, `validate_scripts`), `read_record`, and `diff_record` carry it in-band as `epoch=<hex>` (text) or an
`"epoch"` field (json), and `load_order_status` names the current build's fingerprint on its resolver line. The
point: paged reads (`offset=` windows) that silently straddled a mid-session load-order change were previously
indistinguishable from coherent ones — now the stamps disagree and the drift is visible. Also the foundation for the
2.0 artifact disposition, whose saved result files will be validated against the live build by this fingerprint.

**Parameter-name tolerance is now dictionary-driven (the 2.0 alias layer, W0).** The shim that already fixed
first-guess parameter misses (`plugin=` for `plugins=`, `form_id` for `formid`) now works from the houseCARL 2.0
naming dictionary instead of two hard-wired synonym pairs. Visible today: several new-vocabulary spellings bind on
the current tools (`patch=` for `patch_name=`, `ops=` for `operations=`, `readback=` for `full_readback=`,
`filter=` for load-order-status's `lookup=`, `types=` for `type=`), and `remove_record` (whose parameter was
always the odd one out, bare `patch`) now binds every artifact-name spelling — `patch_name=`, `plugin_name=`,
`archive_name=`, `output=`. On tools declaring several output names, `patch=` lands on the ARTIFACT: the merged
plugin (`output`) on `merge_plugins`, the repacked archive (`archive_name`) on `bsa_repack`, the mod-folder
name elsewhere. A rename never fires into a type
mismatch: a spelling whose value can't bind to its would-be target (say, an array `types=` where a single
`type=` string is declared) keeps its own name in the refusal, alongside the tool's supported-parameter list.
Everything else in the dictionary stays dormant by construction until the 2.0 build waves rename the tools it
describes; behaviour of well-formed calls — and of every 1.x tolerated spelling, `plugin=`/`plugin_name=`
cross-bindings included — is unchanged.

**Skill descriptions no longer overflow the context budget — every one compressed and re-validated (#294).** The 13
skill descriptions cost ~12,150 characters (~3,040 est. tokens) of always-resident context in every session — about
1.5× the budget Claude Code gives the *entire* skill listing across all installed sources. Past that budget, entries
get truncated, and a truncated description routes *worse* than a short one, so the length was actively degrading the
routing it was written to improve. All 13 are rewritten to a 200–400 character target — keeping the distinctive
trigger vocabulary routing actually keys on (`_DISTR.ini`, `facegen`, `.psc`, `Occlusion.esp`) and cutting the
synonym chains and capability inventories (that content lives in the skill body, which loads on invocation and is
free). New listing cost: ~5,150 characters (~1,290 est. tokens), roughly a **1,750-token saving in every session**.
Every description was re-validated through the full trigger-reliability fan-out (283 eval queries, one fresh agent
per query against the complete new listing): 12 of 13 passed immediately; `tool-output-awareness` initially lost the
generator filenames it routes on and went through three fix iterations before passing — its final text deliberately
keeps the artifact names (`PGPatcher.esp`, `Reqtificated` patches, `NPC_Token.json`). `papyrus-reference` also gains
its first eval set. The authoring standard's length rules were amended to match, so future skills are written to the
budget instead of re-discovering it.

**`compile_script` now finds your script's dependencies itself, instead of making you list them every time (#200).**
Compiling a Papyrus script means telling the compiler every folder its dependencies' source code lives in — and it
takes the **whole list, every call**. A script built on the usual frameworks (SKSE, SkyUI, PapyrusUtil, PO3,
JContainers, RaceMenu, UIExtensions…) meant retyping eight or ten folder paths for each compile, and getting one
wrong produced an avalanche of errors that read like bugs in your code. houseCARL now **scans your enabled mods**
for the source folders they already ship and puts them on the path for you, in MO2 priority order — so a mod's own
extended copy of a script wins over the vanilla one exactly as it does everywhere else. It does **not** hand the
compiler all of them: a big modlist ships far more script folders than you'd guess (501 on a 3,617-mod order, whose
combined length a Windows command line cannot even carry), and nearly all belong to quest and follower mods shipping
their own scripts that nothing else ever calls. So houseCARL keeps the folders your script actually **references** —
by name, followed onward through those scripts, since the compiler loads your dependencies' dependencies too. In
practice that turns 501 folders into eight or ten, and it reports both numbers so a folder that was left out reads as
*dropped as unreferenced*, not *missed*. Anything the scan can't
reach (your own stubs, a dev project folder, sources you had to extract out of a BSA — the Creation Kit's compiler
cannot read archives) still goes in `import_dirs=`, and you can now **save that list under a name** with
`save_import_set=` and recall it later with `import_set=`, so it is typed once rather than once per call. Pass
`auto_imports=false` to leave your enabled mods off the import path. **The folders actually searched are now reported on every
compile** — a summary on success, the full ordered list on failure. That was previously invisible, which made the
two ways a compile goes wrong impossible to tell apart from the output: a dependency that was never on the path, and
a dependency found in the *wrong* folder because something earlier in the order shadowed it. Relatedly, a failure
that looks like missing dependencies now gives you the right next step for your situation: if the scan already ran,
it points at the causes a scan cannot fix (the mod isn't enabled, its sources are inside a BSA, or they sit one
folder deeper) rather than repeating "list every dependency".

**`validate_dialogue` now reports a topic's EFFECTIVE line order — and which mod moved a line (#275).** A dialogue
topic is a list of possible lines, and the game plays the **first** one whose conditions pass. That list is not held
by any single record: every mod that touches the topic contributes its own list, and the real order is the merge of
all of them. houseCARL could not see that order at all, so the delta that breaks dialogue was invisible — a pure
reorder changes which line answers while leaving every field identical, so no field diff can show it. The report now
prints the merged order the game actually walks, marks any line whose position **moved**, and names the mod that
moved it. The rule it makes visible: **re-listing a line appends it to the bottom** unless that mod also carries the
line's previous-line link. So a mod that touches one line of an eight-line topic can silently send it to the back,
and a broader line answers in its place. That is the shape behind the report this came from — a hireling asking 5000
gold but hiring for 79, because the "you can't afford me" refusal had stopped being the first line reached — and
behind most "my dialogue mod stopped working" complaints, where the line is still present but no longer reached in
time. Whether any given topic is affected depends on your load order: a mod that re-lists a line **and carries its
previous-line link** moves nothing, which is what a well-behaved patch does. The order is computed to match what
xEdit's INFO Order (INOM/INOA) rows show, which completes the half of this that 1.9.0 left open (that release
taught the conflict diff to stop calling a pure reorder "identical to
winner"; this one shows you the order itself). One line you may see flagged: a line whose previous-line link was
written as an explicit "I am first" marker is pinned to the **top** of the topic, and the report says so — that is
correct behaviour, not a fault, and it is often what keeps a vanilla refusal ahead of the line it guards.

**Correction — houseCARL previously said lines get dropped by a dialogue conflict. They do not (#275).** The tool
used to state that a line another mod adds but the winning topic does not re-list "is dropped in game", and advised
resolving conflicts so the winning topic carries every line. That is wrong, and the advice was for a failure mode
that does not exist. Measured on a real load order: a topic whose winning record lists **one** line plays **eight**,
and the line that misbehaved was not in the winner's list at all. Nothing is dropped by a conflict — the lines merge,
and what changes is their **order**. The footer now states the real scope: the per-line checks (voice, result script,
CK parity) audit the winning topic's list, while every contributing mod's lines appear in the effective order above.
If you previously restructured a patch to make one topic re-list every line, that was not necessary — and because
re-listing appends, it may itself have reordered them. The `dialogue-authoring` skill taught the same wrong model and
its "carry forward every line" advice; both are corrected, and the guidance is now the opposite — **list only the
lines you add or change**, and set a line's previous-line link (PNAM) when its position matters.

**`nif_set` can now write a mesh's shader lighting values — the read/write asymmetry #287 opened is closed (#291).**
A new `op=set_shader_value` sets any of the six values `nif_inspect sections=shader` reports: *glossiness, specular
strength, specular colour, emissive colour, emissive multiple,* and *alpha*. Pass `shader_value=` and `value=` — one
number for a scalar (`glossiness` `55`), three comma-separated components for a colour (`specular_color`
`1,0.5,0.25`). Colours and alpha are conventionally 0–1 rather than 0–255 — a value outside that range is still
written (the format stores the number it is given, and real meshes do carry out-of-range values), but the result
says so, which is what catches a `255,255,255` pasted out of NifSkope's colour picker. It rides the same lanes and
the same two verification gates as every other op: by default the edited mesh lands in a new MO2 mod folder with the
original untouched, and a write that did not actually persist is refused
rather than reported as success. This is the fix for an armour that reads shiny and plastic because its specular
strength came in wrong, a glow mesh whose emissive multiple is off by an order of magnitude, or a folder of BodySlide
output sharing one bad glossiness — previously NifSkope, by hand, one mesh at a time. Note it is **not** `set_alpha`,
which remains the separate `NiAlphaProperty` blend/test word; `set_shader_value alpha` is the shader's own opacity
scalar.
**It refuses rather than pretending on a block that cannot carry the value.** The library implements these six on a
`BSLightingShaderProperty` and answers them from a do-nothing stub on other shader blocks — so writing to, say, a
`BSEffectShaderProperty` would have accepted the value, reported success, and left the mesh unchanged. houseCARL
checks whether the specific block genuinely accepts the write, and where it does not, refuses and names the block
type. Which blocks those are is read out of the bundled library rather than kept as a list, so the answer tracks the
library across future updates instead of going stale — the same by-construction approach the read side already uses,
though it necessarily asks a different question, since the values are read-only on the shared interface and settable
only on the concrete block.

**The bundled mesh library moves to NiflySharp 1.1.0 — `nif_inspect` gains five shader values, and mesh reads pick up
upstream's crash/corruption fixes (#287).**
The pinned 1.0.0 was published from a commit 52 changes behind upstream, and the gap was costing two different things.
The visible one: `sections=shader` could not report *glossiness, specular strength, specular colour, emissive multiple*
or *alpha* — 1.0.0's accessors returned a constant regardless of what the mesh held, so houseCARL named them as unread
rather than print a wrong number. 1.1.0 implements all five, and they now report their real values on any **Skyrim-layout**
lighting shader. The less visible one, and the reason the bump is worth taking on its own: upstream's most recent work is a code
audit fixing *crashes, hangs, data corruption and leaks*, alongside a batch of community fixes (UV writes landing in
the wrong buffer, a file-handle leak, block-array resizing). Every mesh read and write houseCARL does now runs on that
code. Nothing in the tool surface changed shape — the shader section detects which values the library genuinely reads
by inspecting the library, so the five started reporting with no change to the reporting logic, and an effect shader
(where upstream still stubs them) correctly keeps saying so.
**One group of values is withheld on purpose, and it is a scope decision rather than a library one.** On a mesh read as another
game's layout — an unconverted Fallout 4 or Fallout 3 mesh shipped inside a Skyrim mod — houseCARL now declines *all*
the lighting values and says so. The reason is that some of these accessors read a field the other layout's stream
never carried, and answer a fixed constant rather than the mesh's own number (glossiness on a lighting shader is the
live case: it reads the same value whatever the file holds). Rather than interpret the ones that survive the layout
change and guess at the rest, the whole group is declined there. That withholds up to four values that *would* have
been correct, which is a deliberate trade — houseCARL models the Skyrim shader layout, the same reason it already
declines to name texture slots on a foreign-layout mesh. Unlike the library detection above, this half does **not**
track the bundled version; it is houseCARL's own boundary.
Parse fidelity was **re-proven, not assumed to carry over** — both versions were run back to back over the same
91,601 workspace meshes and compared file by file, not by headline percentage. **Nine meshes that houseCARL could not
read at all now read**: 1.0.0 threw on a malformed boolean byte in them, taking Glorious Doors of Skyrim, Golden
Dwemer Pipeworks and every door in Reimperialized Abandoned Prison with it. Three more that parsed but carried blocks
1.0.0 did not recognise now parse fully. The single mesh still unreadable now *refuses* cleanly instead of throwing.
**Nothing regressed**: no mesh that parsed stopped parsing, no mesh that round-tripped byte-identical stopped, and the
18,862 vanilla Bethesda meshes came out verdict-for-verdict identical on both versions.

**The two sweep tools can now be scoped, filtered, counted, and returned as JSON (#282).**
`check_errors` and `validate_scripts` had exactly one scope knob between them — `plugins=` — and on a script-heavy
plugin that was not enough to get an answer at all: ~183 scripted records render past the tool-result size cap, and
`limit=` does not help because it caps *findings*, not the record roster, so even `limit=1` still printed a header line
for all 183. Both tools now take a record scope — `type=` (applied at the record stream, so skipped records cost
nothing), `formids=`, `editorid_contains=` — plus a `findings=` class filter, `counts_only=true`, and
`format="json"`. `validate_scripts` additionally takes `property_contains=` for chasing one property across a plugin.
On `validate_scripts`, `findings=["unbound_object"]` narrows to the HIGH silent-`None` class; on `check_errors`,
`findings=["missing_masters"]` **skips the per-record link walk entirely**, turning "is any master missing anywhere in
my order" from a full sweep into a master-table read.
`counts_only=true` returns the exact totals plus a histogram and builds no per-record listing at all — unbound
properties by NAME for `validate_scripts`, dangling refs by TARGET plugin for `check_errors` (which plugin the broken
refs point *into*, i.e. the one absent dependency behind a wall of findings). It is the pass-to-pass comparison for a
multi-pass edit: did this property's count drop, and did anything new appear.
**Every number states its own scope.** A **record** scope narrows all of a sweep's counts, exactly as `plugins=` always
did, and the response says so on its own line. Nothing else carries that blanket claim, because nothing else narrows
*every* number: a `findings=` class filter self-labels instead (an excluded class reads `NOT CHECKED`), and
`property_contains=` labels the two counts it does narrow — `4 unbound matching 'MySpell'` — while records-with-scripts
and unverifiable stay plugin-wide and unlabelled, because it does not narrow them. `check_errors`' missing-master count
comes off the plugin's master *table*, so even a record scope cannot narrow it; there the response marks that one number
plugin-level explicitly rather than sweeping it into the claim.
Two things are deliberately *not* filterable: unscannable records and unverifiable script attachments always report,
under every filter — a suppressed "could not check" would read as a clean result, and an unreadable `.pex` may be the
very one declaring the property you filtered for. A finding class you excluded renders as `NOT CHECKED` (and `null` in
JSON) on **both** tools, never as a `0`; `validate_scripts` also reports `unbound_object` / `unbound_scalar`
separately, so a partial filter's count can't be mistaken for the whole unbound population.
Also fixed: the truncation notice on both tools advised *"scope `plugins=`"* — the one scope the caller had already
applied. It now names the knobs that exist.

**`nif_inspect` can read a shape's SHADER — `sections=shader`, plus named texture slots (#272).**
The one question every visual diagnosis actually asks — *how is this shape shaded, and does it emit or scatter light?*
— was unanswerable. `nif_inspect` reached the shader block only to hop to its texture set, then threw it away, so you
got slot paths and nothing about the shader itself. The new section reports, per shape: the shader block type
(`BSLightingShaderProperty` / `BSEffectShaderProperty`), the shader **type** enum (`SkinTint`, `FaceTint`, `HairTint`,
`EnvironmentMap`, `Parallax`, `MultiLayerParallax`, …), and the **SLSF1 / SLSF2 flags decoded to their names** —
`Soft_Lighting`, `Glow_Map`, `Model_Space_Normals`, `Double_Sided` and the rest. The flag names come from the mesh
library's own enums, so they are the library's coverage rather than a hand-kept bit table, and any bit no member names
is stated as an explicit `(+unknown bits 0x…)` mask instead of vanishing.
Texture slots now also carry their **semantic name** wherever the shader determines it — `tex[2] (SoftLighting)`,
`tex[6] (TintMask)`, `tex[7] (Specular)` — in `sections=paths` and `sections=shapes` too, not just the new section.
Slot 2 is glow *or* skin-subsurface *or* soft-lighting and slot 7 backlight *or* specular depending on type and flags,
so the name is derived from those, never from the index; a slot the shader doesn't determine stays a bare `tex[N]`
rather than getting a plausible wrong label, and the index is always kept (it is what `nif_set`'s `texture_slot=`
takes). Slot naming is a **Skyrim** convention, so it declines entirely on a mesh read as another game's layout —
an unconverted Fallout 4 mesh shipped inside a Skyrim mod gets bare indices, and the output says so rather than
leaving "unnamed" to read as "undetermined".
**Lighting values** — *emissive colour and multiple, glossiness, specular strength, specular colour, alpha* — are
reported for a Skyrim-layout lighting shader (see the mesh-library bump above; they were unreadable when this section
was first built, and never shipped that way). Two cases report nothing instead, each saying which it is: on an
**effect** shader the library answers them from a stub returning a constant, and on a **non-Skyrim layout** houseCARL
declines them as a matter of its own scope. Either way you get a named reason, never a number houseCARL cannot vouch
for. The first is detected from the library itself rather than a hard-coded list, so it tracks the bundled version
automatically in both directions; the second is houseCARL's own boundary and does not.

**`check_errors` and the compact/merge dependency scan now treat a deleted record the same way (#279).**
The #276 fix taught `cross_plugin_query` that a deleted record links to nothing; the two sibling walkers that ride the
same form-link enumeration — `check_errors`' dangling-reference sweep and the external-dependency scan behind
`compact_plugin` / `merge_plugins` — still walked them. Two consequences, both now fixed. A deleted record's links
were reported as findings: `check_errors` flagged one as a *dangling reference*, and the compact scan counted one as
an external *referencer*, which could refuse a compaction over a dependency that isn't live. And a deleted record with
an engine-authored malformed body threw on the walk and landed in the "could not be scanned" bucket with a raw
exception cause — the same untyped skip #276 removed, in two more places. The rule now lives in one place shared by
all three walkers, so they cannot drift apart on it again. Deliberately unchanged: the compact scan still warns about
a deleted record that *overrides* something being renumbered — that test reads the record's own identity, not its
body, and such an override is still a dependent worth naming. Surfaced by the independent review of the #276 fix.
**An asset path that's missing its `meshes\` / `textures\` root now says so, instead of a bare ABSENT (#273).**
A model path read straight off a record — `Model.File` on an NPC, ARMA, STAT — is stored relative to `meshes\`, but
every asset tool wants it Data-relative. So the *normal* way one arrives at a mesh produced a flat, hint-free
`ABSENT — no active mod or BSA provides this mesh path`: a true answer for the string as given, but a dead end unless
you already knew the convention. `nif_inspect`, `nif_set` and `asset_status` now retry the root-prefixed form and,
when it hits, name it — ``did you mean `meshes\Actors\…\wolf.nif`?``. The suggestion is **verified, not guessed**: it
comes from actually re-resolving the candidate through the same VFS, so a path it names is always one a mod or BSA
really provides, and when nothing resolves nothing is suggested. `asset_status` tries both roots (it can't know a
path's kind) and stays silent otherwise — `sound\`, `scripts\` and the rest get no lecture. The mesh tools, which
only ever deal in meshes, add one weaker note when the prefixed form misses too: it names the convention and the
form the path would take, and says plainly that form isn't provided either. Reported externally.

**`place_asset` / `bulk_place_asset` carry that same missing-root suggestion (#283).**
The fourth lane with the same dead end: asked to auto-place a path taken straight off a record, the refusal
(`nothing in the active load order provides '…'`) named no way forward. It now retries both roots and, when a real
mod or BSA provides the prefixed form, names it — the same **verified, never guessed** suggestion, so it always
points at a copy that exists and stays silent when there is nothing honest to offer. It fires only on the
auto-resolve arm: with `source=` named, a destination nothing currently provides is the normal case (you are placing
a brand-new file), not a mistake to correct. A placement batch now also answers from one pinned asset build, so two
assets in the same call can never describe two different states of the VFS. Surfaced by the independent review of
the #273 fix.

**`cross_plugin_query` no longer trips over deleted records and reports them as an unexplained skip (#276).**
A `references=` (or `where=`) scan over a load order holding *deleted* records — the wild case was deleted `Package`
records in a follower mod — ended with a raw `NullReferenceException … could not be scanned and were skipped` note.
A deleted record carries no body by engine rule, but the scan still tried to read its form links, and an
engine-authored deleted body can leave just enough behind to NullRef on that read. The skip was accounted (not
silent), but its *cause* read as a parser hole — which means a genuine match hiding in a "skipped" record looks
possible when it isn't (Q3). A deleted record is now excluded from the content filters up front: it links to nothing
live and has no field to test, so it is a clean non-match, not an unscannable skip. This also means a deleted record
that *does* parse and carries a link to the target is no longer returned by `references=` — treating a deleted record
as referencing nothing, the resolution the report itself proposed. `editorid_contains=` is unaffected (EditorID reads
from the early EDID subrecord, before the body parse that throws). Reported by DrHeisen.

**A pure list reorder no longer reads as "identical to winner" in a conflict diff (#275, partial).**
The `conflict_tree` / `diff_record` content compare is order-insensitive by design — the same relations stored in a
different order (the USSEP case that motivated it) shouldn't over-report. But for a list where order IS the
semantics — a DIAL's INFO children decide which line the game plays — folding a reorder into "no delta" is a silent
wrong answer: the record reads "identical to winner" when the very thing that changed is invisible. Now, when two
lists hold the same contents in a different order, the diff emits an explicit `Field: same N item(s), ORDER DIFFERS
from winner` note instead of silence — type-agnostic, so it fires for any reordered list (over-reporting a noise
reorder is the safe direction; silently equating a semantic one is not). No-delta renders no longer claim "list
order ignored," since order is now compared. Reported by DrHeisen. *(This closes the diff-honesty half of #275; the
larger ask — an effective, merged INFO-order view for a topic, xEdit INOM/INOA parity — remains open.)*

**A plugin addressed by its file path is no longer mislabeled off-order and disabled (#269).**
`diff_record` stamped `OUT-OF-LOAD-ORDER (direct path, disabled)` on a plugin that is enabled and winning whenever it
was passed as an absolute path instead of a filename — the diff values were right, but the provenance line said the
live file wasn't live, which is exactly the claim a diff is consulted for. Two causes, both fixed: the shared on-disk
locate asserted "not enabled" for every direct path instead of computing it, and `diff_record` routed poles by plugin
NAME only, so a path could never match the active order. Enabled-ness now comes from the same enumerator the filename
lane uses (so the two can't disagree about one file), and a path that IS the copy the order loads resolves back to its
plugin name and diffs as `active order`.

The flag a located plugin carries — what `read_plugin_file` renders as `NOT active` and `diff_record` as `disabled` —
now means one thing in every lane: **the game loads this file**. That needs both halves, and each was wrong before:

- **The right copy.** Judged by full path against the copy the install actually serves (the first hit from an enabled
  layer — the same rule that builds the real load order). An archived backup sharing a filename stays
  `OUT-OF-LOAD-ORDER`, so the old-version-vs-live diff is unchanged; a copy shadowed by a higher-priority mod is not
  called active merely because its own mod is enabled; and a plugin served from game `Data` is not called inactive
  merely because some disabled mod holds the same filename.
- **Ticked.** A plugin sitting in an *enabled mod* but *unchecked in MO2's right pane* is no longer reported as
  active — the game does not load it. Implicit base/CC masters, which are force-loaded and never listed in
  `plugins.txt`, still count as active. This half applies to the filename and `mod=` lanes too, so all three now
  state the same fact.

Reported by a houseCARL user. (The banner wording and the one-flag-many-causes question this left open are resolved by
the next entry.)

**Every "not active" now says WHY, and so does every refusal that turns one away (#271).**
`NOT active` / `disabled` named the state but never the cause — and the causes have different fixes: the plugin is
unticked in `plugins.txt`, its mod is switched off, a higher-priority mod shadows this copy, or MO2 has not registered
it. Reading `[mod 'X' (enabled); NOT active]`, you could not tell which, so the answer had to be rediscovered by hand
every time. Two things changed:

- **The located-plugin flag became two facts** — *is this the copy the install serves* and *is this plugin ticked* —
  carried separately so each renderer can explain rather than classify. `read_plugin_file`, `diff_record`'s off-order
  pole, and `copy_npc_appearance`'s donor line now state the cause, from one shared composer so they cannot drift
  apart; the JSON lane gains a `why_not_active` field beside `enabled`. A file the game *does* load now says so
  positively instead of saying nothing. Vocabulary is consistent throughout: a **mod** is enabled/disabled, a
  **plugin** is active/inactive — `diff_record` no longer calls an unticked plugin "disabled".
- **Refusals explain themselves.** A tool that reads *through* the load order still refuses on a plugin the game does
  not load — that guard is the point — but instead of a flat "not in the load order", it now says the plugin is
  installed and unticked (or that its mod is off), and points at `read_plugin_file` for a raw read. This covers
  `read_record`, `check_errors`, `validate_scripts`, `merge_plugins`, every in-place write target, and the forward
  source check. A genuine typo still gets its "did you mean" — the explanation replaces the spelling guess only when
  there is a real cause to state. The four not-loaded causes each carry their own remedy, and the two that look
  alike from outside — a mod switched **off** versus a folder MO2 has **never registered** (where there is nothing
  to switch on) — are told apart from MO2's own mod list rather than guessed.

**The `read_plugin_file` banner no longer overclaims.** It read `OUT-OF-LOAD-ORDER (raw file read; the game does not
load this file)` — true of the read, false about the file whenever the one you passed IS the live plugin. It now reads
`(raw file read — not resolved through the load order)`, which holds in every case; what the game does with the file
is stated separately, per file, with its reason. The same sentence is gone from `read_plugin_file`'s own tool
description and from `copy_npc_appearance`'s donor line, where it was asserted even for a donor the game does load.

**The Codex umbrella router now covers the whole tool surface, and a CI guard keeps it that way (Codex parity).**
The Codex packaging ships one umbrella routing skill (`plugin/codex/housecarl/SKILL.md`); it had drifted to naming
only 9 of the ~45 MCP tools and 5 of the 13 helper skills, so a Codex user asking about facegen, Nexus, BSA
archives, dialogue, SKSE audits, or plugin compaction got no routing from it. It now carries a capability-grouped
map of **all 45 tools** and routes to **all 13 helper skills** (the discrete skills themselves already install to
Codex under `~/.agents/skills/`, so this restores the router, not the skills). A new `codex-umbrella-coverage`
CI guard reflects the real `[McpServerTool]` names and the `.claude/skills/*` folders and fails if any is unrouted
by the umbrella (or explicitly allow-listed) — so the drift cannot silently recur: adding a tool or skill without
updating the Codex router turns CI red. Codex packaging + CI only; no change to Claude Code behavior.

**The `bulk-record-jobs` skill now teaches how to get a big enumeration out — paging vs. persist-to-file (#249).**
A new section distinguishes the two independent caps a bulk read hits — the row cap (`limit=` → `capped`) and the
per-call output cap (`max_chars` → `truncated`) — and names the two lanes for clearing them: `offset=` paging on
`cross_plugin_query` (deterministic tiling windows; the primary lane, previously undocumented in the skill) and, as the
complement, raising `max_chars` so an oversized result **persists to a file** to post-process with scripts instead of
reading it into context (the move that made a 7,479-NPC facegen sweep single-session — one call per plugin, a 5,118-row
JSON document). Both come with the mandatory guardrail (verify `truncated == false` and `rendered == total` in the
persisted file) and a caution that huge tool *inputs* are their own stall risk (batch to a few hundred per call).
Skill documentation only; no tool behavior changed.

**The 13 bundled skills' descriptions were trimmed to cut always-loaded startup context (#256).**
A skill's `name` + `description` frontmatter load into every session's context — they are not Tool-Search-deferrable —
and the 13 descriptions totalled ~3,650 tokens. Each carried, past its trigger surface, a trailing
"Load this before X — <counter-intuitive mechanics>" rationale and grammar detail that is already spelled out in full
in the skill body (lazy-loaded, free at rest). Those tails and the "using the bundled reference rather than invented
syntax" boilerplate were trimmed while **every "Use when…" trigger cue and every not-this-other-skill disambiguation
line was kept**, so trigger accuracy is unchanged and ~590 tokens (16%) come off the always-resident cost. Descriptions
only — no skill body, reference, or behavior changed; every trimmed frontmatter still parses (the `plugin-validate`
CI guard, which exists because a colon-space once silently dropped a whole skill, stays green).

**houseCARL's MCP server instructions now orient tool discovery across the whole surface, not just Nexus (#257).**
The server's `initialize` `instructions` blurb — always resident in the agent's context — was ~90% a Nexus how-to that
duplicated each Nexus tool's own description and named none of houseCARL's core capabilities, so an agent relying on
Tool Search had nothing telling it houseCARL could read inactive plugins, see through the SKSE/SkyPatcher runtime
layers, author dialogue, or compact/merge/decompile. It now leads with the data-layer / MO2 domain and an explicit
"reach for these tools when…" cue, then sweeps the real surface in five groups (read/query, write, fix,
reshape/drive-tools, Nexus). Per-tool parameter detail was dropped from the blurb — each tool carries its own,
fetched on demand — leaving the string broader yet slightly leaner (1,902 bytes, within the ~2 KB per-server budget)
and ordered so any truncation loses only the least load-bearing Nexus tail.

**`housecarl_nif_inspect` `sections=` now accepts the JSON-array form and fails loud on an all-unrecognized value (#247).**
Passing `sections` as a JSON array — the natural MCP way to send a list — arrived as the literal string
`["shapes","paths"]`, and, split only on comma/space, tokenized to `["shapes` / `"paths"]` (brackets and quotes glued
on), read as unrecognized, and the tool **quietly fell back to rendering the default summary** — so a batch could run
with the wrong sections while the warning scrolled off the top of a large persisted file. The tokenizer now treats
bracket and quote as delimiters too, so `["shapes","paths"]` parses; and a `sections=` in which **nothing** is
recognized is now a loud error naming the tokens, never a silent summary (a partial request still renders the valid
sections plus a warning). The unrecognized-section message — and the tool description — now also point out there is no
`textures` section: a mesh's embedded texture-set slot paths appear under `shapes` (per-shape detail) and `paths`.

**`housecarl_bulk_apply` read-back now reports the true count for a `composes=` Add of N — no more misleading `(+1)` (#259).**
Appending N elements to a list field in one op via `composes=` rendered a verify line that reported only the last element,
as if a single element was added — `✓ … Add Conditions: now 37 (+1), new [36] = [ConditionFloat]` for a six-element
compose — because the Add read-back hardcoded a `+1` delta and the single last index. The data on disk was correct (the
list total was the only clue all six landed), but the summary contradicted the op and cost real mid-session doubt. The
read-back now carries the op's appended count and reports the whole run: `now 37 (+6), new [31..36]`. A single-element
Add is unchanged (`now 29 (+1), new [28] = …`).

**A `[Flags]` enum field with unknown bits now decodes the known bits instead of collapsing to a bare decimal (#255).**
When a flags field carries a bit the record catalog doesn't name (a modded slot, or a game-version bit houseCARL's
Mutagen build predates), `.NET`'s `[Flags].ToString()` abandons the name list and renders the whole value as one
decimal — e.g. `Configuration.Flags = 2490402` on Dawnguard vampire NPCs — silently losing even the *known* bits
(gender, uniqueness, ghost state) a consumer needs. A read now appends a display-only decode of the form
`2490402   (<known flag names> (+unknown bits 0x…))` — the known bits by name, the unnamed remainder as an explicit hex
mask, so the common bits stay directly consumable and the presence of unknown bits is stated rather than hidden. The
known bits are peeled the way `[Flags].ToString()` itself names them (fully-contained members, combos before their
constituent bits), so the name slot is never itself a bare decimal. The round-trip token is unchanged (the bare decimal
still round-trips through `Enum.Parse`, so write / read-proof / diff are untouched) — the decode rides the same display
channel as the existing biped-slot annotation, which keeps its slot-number decode.

**`housecarl_read_record` / `housecarl_batch_record_detail` `depth=2` now surfaces an owned-record list element's own FormID (#252).**
A list whose elements are themselves owned records — most commonly a DIAL topic's `Responses` (each element an INFO record) —
surfaced no FormID at `depth=2`: an element rendered a bare `[DialogResponses]` (or `[DialogResponses] EditorID=…` when the INFO
carried an EditorID), never the FormKey that is an owned record's canonical identity. Mapping topics to their child INFOs meant a
second call with explicit `[i].FormKey` paths (and you couldn't enumerate those paths without first reading the element count).
The `depth=2` "index + identity" contract now holds for owned-record elements the way it already did for lone-FormLink structs
(#198): each leads with `[DialogResponses 000ABC:Plugin.esp editorid=…]` — its own FormKey, plus EditorID when present. Applies
wherever `depth=` expands (text and `format=json`).

**`Conditions[].Data` arm parameters now expand in a `Conditions` list dump (#258).** A polymorphic condition-data arm reached by
expanding a `Conditions` list stopped at its bare `[GetFactionRankConditionData]` type — its parameter fields (`Faction`, `Global`,
`Reference`, `RunOnType`, …) surfaced only when `Data` was addressed directly (`fields=["Conditions[2].Data"]`) or at an extra depth
level, so a `fields=["Conditions"] depth=3` dump silently stopped one level short of the params. The depth-floor "open one bounded
level" exception — previously VMAD-script-property-only — now also opens a condition arm's parameters, so a whole condition stack
(function + params) reads in one call at the natural depth. Bounded to one level (an arm's members are leaves/links) and
type-targeted (every other substruct still stops at the floor, unchanged).

**`housecarl_cross_plugin_query` `group_by=` now case-folds plugin keys — case-variant spellings of one plugin no
longer split into separate groups (#248).** A load-order-wide `group_by=defined_in` counted the same plugin twice when
different plugins spelled a shared master with different casing — e.g. `ccBGSSSE025-AdvDSGS.esm = 40` *and*
`ccbgssse025-advdsgs.esm = 35` as two rows — because a defining-plugin key carries each plugin's own master-list
spelling. Any consumer summing per-plugin counts silently double-grouped. Plugin filenames are case-insensitive
identifiers everywhere else in houseCARL (and in the game); group keys now match, merging the counts (first-seen casing
is displayed). The same case-fold covers `group_by=winner` for consistency (its keys come from one canonical name array,
so they don't vary in casing the way `defined_in` keys do); `group_by=type` is unaffected (record-type names never
differ only by case).

**`housecarl_cross_plugin_query` gains `where_source=winner` — filter on the live winner, not the scoped body (#233).**
Under a `plugins=` scope the body filters (`where=`, `references=`, `editorid_contains=`) decided the match against
each match's *scoped* plugin body, even with `winner_fields=true` — so a post-patch audit like "Bruma-defined NPCs
whose live winner still uses a PC-level multiplier" returned every record that *ever* had one (259), not the 82 whose
winner *still* does. `winner_fields=` only changed what was *displayed*, never what *matched*. Now `where_source=winner`
retargets the whole match onto the live load-order winner: `plugins=[…] defined_in=true where=["Configuration.Level.LevelMult >= 0"]
where_source=winner` returns exactly the winners still on the multiplier, server-side, in one call — no more scanning every
winner in the order and filtering FormKeys by hand. (It re-fetches each candidate's winner body, so a very *broad*
winner-source scan is not yet as fast as it can be — an O(order) fetch is tracked in #251; correctness is unaffected.)
It stays decoupled
from `winner_fields=` (DISPLAY), so `where_source=winner winner_fields=false` matches on the winner while showing the
scoped origin body — a real audit. Loud refusals (Q3): an unknown value names `scoped`/`winner`; `where_source=winner`
with no body filter to retarget is refused; and under a `type=`-only scope (already the winner) it's accepted with a
"redundant" note, never a silent no-op. Default (`scoped`) is unchanged.

**`housecarl_cross_plugin_query` learns `depth=` (#231).** Nested list contents were unreachable in a scan:
`fields=["Effects"]` rendered only `[list: N item(s)]`, and the workaround — hand-written bracket paths like
`Effects[2].Data.Magnitude` — meant guessing the longest list up front and eating an out-of-bounds error triple
for every shorter record (66 spells → hundreds of wasted lines, twice past the token cap). Now `depth=` rides the
scan with the same semantics as `housecarl_read_record` / `batch_record_detail`: `fields=["Effects"], depth=4`
expands every match's per-effect Data in one call — each record shows exactly its OWN elements, no index guessing,
no out-of-bounds noise — and `resolve_names=` composes with the expansion. Works in `format=text` and `json`;
`dense` refuses `depth>1` loud (its columnar cells align 1:1 with the requested paths — the container cells name
the text/json hop), and `depth=` without `fields=` is refused loud too. The old container hint ("cross_plugin_query
has no depth=; expand via batch_record_detail") is retired with the gap it described.

**`housecarl_nif_inspect` goes batch — `mesh_paths` takes one or many (#229).** The last per-file loop in a
load-order-wide dark-face scan is gone: `mesh_path` is now `mesh_paths`, an `asset_status`-style array (a single
path is simply a batch of one). One call resolves the whole flagged subset — **one load-order resolution for the
entire batch** instead of one per mesh — with results in input order, `sections=` / `mod=` / `max_chars` applying
batch-wide, and every per-path failure (ABSENT, bad path, a `mod=` that doesn't provide it, unreadable bytes, a
parse refusal) reported LOUD on **that** path without aborting the rest (Q3). The batch-level caveats (unreadable
archives, discovery warnings) render once, first, so a long batch can't truncate them away — and every ABSENT is
additionally **hedged at point of use** when the scan behind it was incomplete (`asset_status` parity: a bare
"absent" is never over-trusted just because the top-of-output alarm scrolled away). An over-cap output is cut with
the omitted-mesh count named, never silently — and `max_chars` can never starve a single-path call of its core
answer (the first mesh's resolution/error always renders). The `facegen-diagnostics` batch flow no longer needs to
sample the flagged subset — inspect all of it in one call.

**`resolve_names` / `housecarl_resolve` no longer call PlayerRef "unresolved" (#230).** The engine-implicit forms —
PlayerRef (`000014:Skyrim.esm`) and the Player base NPC (`000007:Skyrim.esm`) — are hardcoded engine references no
plugin defines, so the identity resolver flagged every condition or link pointing at them as
`unresolved: target not in the active order` (67 false suspects in one 67-record audit), while `check_errors`
correctly called the same links clean. The resolver now applies the same precise two-form exemption the integrity
sweep and dialogue lints already share: those links annotate `→ PlayerRef` / `→ Player` (winner `<engine>` in a
`housecarl_resolve` row), and any OTHER sub-0x800 form still reports unresolved — the exemption is the two known
forms, never the whole reserved range.

**`housecarl_bulk_apply` learns manifest files — `from_file=` (#224).** A big write job used to mean pasting the
whole ops array inline (the 745-record stress test generated 20 local `ops_*.json` chunk files and fed them through
piecewise). Now `from_file=<absolute path>` reads the SAME operations array as a JSON manifest on disk: generate
the manifest once, validate the **whole file** with `dry_run=true` before the first write, apply it in one call —
and re-run the same manifest to recover an interrupted write (overrides are idempotent). The parsed ops ride the
identical pipeline as inline ones (every lane and `dry_run` compose by construction; the dry-run report is
string-identical to the same ops inline). The file contract is all named refusals (Q3): `operations` XOR
`from_file`, absolute path required, invalid JSON named with line+column, a non-array root, an empty array, and —
stricter than inline binding, which silently drops unknown members — a misspelled op member (`feild_path`) is
refused **by name at its element** instead of becoming a null-field op whose downstream error points away from the
typo.

**Dry-run mode for the write tools (#225).** `housecarl_set_field`, `housecarl_bulk_apply`, and
`housecarl_forward_record` gain `dry_run=true`: the **full real write pipeline** runs — winner resolve, schema
pre-flight, every op applied to the in-memory would-be plugin, a reference-resolution check that pre-empts the
serialize's missing-master failure — and stops at the point of no return, so **nothing touches disk** (no patch
file, no mod folder, no in-place rewrite). The report says what *would* change (per-op would-be values, the
expected master set, `full_readback=true` for the full in-memory record preview), and a bad batch gets **exactly
the all-or-nothing refusal the real call would give** — so a wrong field path in a 700-op batch is caught before
the first write, not diagnosed after the last. Works on every lane: fresh patch, `into=`, and `in_place` (an
in-place dry run needs no `acknowledge` and never records consent — it's read-only; the pending consent is noted
so the real write's one-time prompt isn't a surprise).

**BSA reads move in-process — `housecarl_bsa_list` / `housecarl_bsa_extract` (#217).** Listing and extracting a
`.bsa` no longer shell out to BSArch; they read through Mutagen's own in-process BSA reader. This fixes an archive
class BSArch's *unpacker* rejected: an archive written by a non-BSArch tool could list and load in-game yet extract
to **zero files**. It also now handles **compressed** archives, and was verified byte-for-byte identical to BSArch's
own unpack (uncompressed *and* compressed). Consequences:

- **No external tool is needed to list or extract** — only `housecarl_bsa_repack` still calls BSArch, because Mutagen
  ships a BSA reader but no writer (confirmed by exhaustive reflection over its archive surface). Repack is now the
  one BSA operation that still prompts for the BSArch path.
- Extraction gains two safety properties the BSArch path never had: it is **path-traversal-guarded** (an entry
  resolving outside the destination is refused) and **content-aware/idempotent** (a byte-identical file already
  present is skipped).
- A per-entry size ceiling and a header-vs-reader file-count cross-check keep a corrupt or hostile archive to a
  **loud, named failure** rather than an out-of-memory or a silent empty extract.

**Wrong-type arguments are now named (#222).** Passing an argument of the wrong type — an object where a number
is expected, a string where a boolean is — used to surface as a raw `JsonException … BytePositionInLine: 34`: a
byte offset with no parameter name, forcing you to bisect which argument was at fault. The argument-binding shim
now catches the type mismatch *before* binding and names the offender, its expected type, and the kind received
(e.g. `parameter whose type could not be bound: conflicts_only (expects boolean, received string)`) — the same
named-and-actionable style the missing-parameter and unknown-parameter paths already use. Obvious-intent shapes
(a bare string for an array, a quoted number or boolean) are still auto-coerced, so well-formed calls are
unaffected.

**Obvious parameter aliases now bind (#221).** Tool parameters aren't uniformly named — one tool takes `plugins`,
another `plugin`, another `plugin_name` — so a first-guess miss (`form_id` for `formid`, `plugin` for a tool's
`plugins`) cost a round-trip. The argument-binding shim now recognizes an obvious synonym of a declared parameter
and renames it to the canonical one, so the call binds instead of erroring. It's deliberately conservative: it
resolves an underscore/case variant (`form_id` ≡ `formid`) or a known singular/plural synonym, and **only** when
exactly one declared, not-already-supplied parameter matches — an ambiguous or unmatched name still gets the named
unknown-parameter error, a declared parameter is never treated as an alias (a tool's real `plugin=` is untouched),
and an explicit canonical value is never overwritten. The published schema still advertises only the canonical
name.

**`cross_plugin_query` learns identity membership — `formid in` / `formid not in` a supplied list (#226).** The
reconciliation subtraction — "every record of these types in plugin X, *minus* these ~1,200 already-claimed
FormIDs" — used to run client-side over verbose enumerations because `where=` had no exclusion predicate. It now
does: `where=["formid not in [XXXXXX:A.esp, YYYYYY:B.esp]"]` (inline, comma-separated — spaces in plugin
filenames are safe, and a pasted JSON array works as-is) or `where=["formid not in @C:\\work\\claimed.txt"]` (an
absolute-path file of FormIDs, comma- or newline-separated). `formid in` is the symmetric keep-only form, and both
AND with the existing value predicates. The list is fully validated before any scan — a malformed FormID, an
unreadable or relative file path, or an empty list refuses the call by name, never a silent wrong result.

**`cross_plugin_query` learns bulk-enumeration economy — `format="dense"` + `offset=` pagination (#223).** A
whole-mod enumeration used to be the context-budget drain: `format="json"` repeated the identity envelope and a
`{path, value}` object per field on every match (~80 records per 40k chars at two fields), and with `limit` but no
offset, paging meant slicing by `editorid_contains`. Now:

- **`format="dense"`** renders one columnar document — a `columns` array once, then one positional row per match
  (`[formid, editorid, field values…]`; summary rows are `[formid, type, editorid, winner, override_depth]`; under
  a `plugins=` scope, detail rows gain a `source` column naming the body each row's values came from). Same
  read path and accounting as the other formats; a no-value field shows its note (`"(absent)"`) in-cell, a failed
  row lands in a separate `errors` array, and `resolve_names` annotations still work. In the probe's own
  measurement the same one-field query renders **2.6× smaller** than `format="json"` — and the gap widens with
  more fields.
- **`offset=`** skips the first N matches, so `offset=0/500/1000…` + `limit=` pages an enumeration in windows that
  tile exactly (scan order is deterministic while the load order is unchanged). The true total always counts all
  matches; the text header names the window and the next offset (`showing matches 501–1000; continue with
  offset=1000`), and json/dense carry `offset` in-band. Negative offsets and `offset=` under `group_by=` (a count
  table has no window) are refused by name.

## 1.9.0 — 2026-07-17

houseCARL's view of the **SKSE-plugin layer** grows from *inventory* into *diagnosis*: two new audit tools
that catch a broken native pairing or a dead config reference statically — before the game fails silently on
it — completing the SKSE layer-visibility ladder (tiers A→D, #199). **Two new tools (→ 45), no new skills
(still 13).**

**SKSE layer diagnosis — two new audit tools**

- **`housecarl_native_pairing_audit` — the native functions your scripts declare vs the DLLs that must
  implement them.** A native Papyrus function is one thing declared in two files that ship and fail
  independently: a `.pex` class flags the function, and an SKSE DLL registers the implementation at runtime.
  When the halves don't meet, the engine logs a cryptic "unable to bind" and the calls silently no-op. This
  scans the winning copy of every compiled script (loose + BSA) and pairs each native-declaring class to the
  DLLs its mod ships under `SKSE\Plugins`, leading with the findings: **PAIRED-BUT-DEAD** — scripts installed,
  but every candidate DLL statically will not load (wrong game runtime for a version-locked plugin, BSA-only,
  shipped in a subfolder, 32-bit, unreadable, or built against the debug CRT) — and **UNPAIRED** — no DLL in
  sight, a VERIFY flag, typically a declaration copy of a framework you don't have. It keeps the engine
  baseline honest by construction (a class carried by an official archive is the engine's, even when an SKSE
  loose override wins the file), and answers "is the pairing plausible and healthy", never "does the DLL
  register exactly these functions" — runtime behaviour, the honest tier-E ceiling it never crosses.
- **`housecarl_skse_config_audit` — the form references your SKSE configs declare vs your real load order.**
  Reads the winning copy of every `.ini` / `.toml` / `.json` / `.yaml` config across the full depth of
  `Data\SKSE\Plugins`, extracts every form-shaped reference (a hex FormID paired with a plugin name in either
  order — the DSD/po3, SkyPatcher, and tilde forms — plus plugin-named folder gates), and resolves each to a
  verdict: **OK**, **PLUGIN MISSING**, **DANGLING** (plugin present but no such record), or **UNPARSEABLE**.
  The summary separates **BROKEN** (dangling / unparseable — actionable) from **INERT** (a plugin you don't
  have installed — usually optional support), so a genuinely broken patch is caught by houseCARL instead of by
  a silent in-game failure. Framework-agnostic: it checks reference *validity*, never what a reference is
  *for* (that's per-framework skill territory) or what the DLL *does* with it (the honest ceiling).

**Enhancement**

- **`housecarl_skse_inventory` gains `peek=` — a static peek inside a specific DLL's image.** With `filter=`
  naming a DLL, `peek=true` reports the DLL's imports and the config paths and plugin names it embeds —
  answering "what does this unfamiliar DLL touch" without loading it. Per-DLL by design (a whole-layer peek
  would read every image and drown signal in noise), so a bare `peek=true` fails loud asking for a filter.

**Fixes**

- **`check_errors` no longer false-flags a faction owner's required rank as a dangling reference.** A record
  owned by a faction at a required rank was misread as carrying a broken link; the rank is a valid part of the
  ownership structure, not a form reference, and is now recognized. (#207)
- **`bulk_create` / `create_record` now fill a dialogue branch's `Flags` (DNAM).** Mutagen omits a null
  optional subrecord, and the engine reads an absent DNAM as **top-level** — so a `DialogBranch` you never
  marked top-level was silently published to the player's dialogue menu: byte-valid, passing every check, wrong
  only once the game loaded it. The CK-parity auto-fill now seeds `Flags` (matching how it already handles
  `Category`), surfaced as an explicit op, and `validate_dialogue` warns when a branch carries no DNAM. (#212)

## 1.8.1 — 2026-07-16

A small read/query-surface patch: two refinements that let a whole-plugin audit and a record read land the
answer in one call instead of a workaround. No new tools or skills (still 43 tools / 13 skills).

- **`cross_plugin_query where=` gains `exists` / `missing` — presence filtering on whole fields.** The value
  operators (`=`, `>=`, `contains`, the bitwise `has`, …) test a *scalar* leaf, so "which records actually
  CARRY a script adapter / an effects list / conditions" meant a sweep-then-filter, one call per record
  type. `where=["VirtualMachineAdapter exists"]` now returns exactly the records that carry that field, and
  `missing` is its complement. "Present" means present **and non-empty** — a non-null substruct or a list
  with at least one element — so an empty list reads as absent, not carried. A mistyped field still fails
  loud, never a silent "0 matches". (#197)
- **A struct's lone FormLink now renders as its identity at `depth=2`.** Reading an NPC's `Perks` at depth 2
  showed each entry as a bare `[PerkPlacement]` with no perk FormID, so the links read as "not surfaced".
  Any struct that has no name-like identity but exactly one FormLink now surfaces it —
  `Perks[0] = [PerkPlacement] Perk=03AF81:Skyrim.esm` — matching what script properties already do with
  `Name=`. Two or more links stay opaque (ambiguous — not guessed). (#198)

## 1.8.0 — 2026-07-15

houseCARL gains two capability layers at once: a **keyless Nexus update-checking** stack — know which of
your installed files are actually out of date, at the exact-file level, trace a file to its mod by hash,
with a raw GraphQL backstop under it — and a **bulk / fleet data surface** — resolve many FormIDs to
identity, diff two plugins' versions of a record, and a batch of aggregation flags on the query, write, and
read surfaces, fronted by a new planning skill. **Six new tools (→ 43) and one new skill (→ 13).**

**Nexus update checking — four new tools and a wider `nexus_mod`**

- **`housecarl_update_status` — read MO2's own update cache, with no network.** MO2 already records, per
  mod, the newest file it last saw on Nexus; this reads that local cache (each mod's `meta.ini`) and reports
  every installed mod whose cached "newest" differs from what's installed — narrowing a whole-order update
  pass to just the candidates before a single network call — and prints each mod's `id#fileid` verify token
  for the live check below. It reports a *difference to verify*, never an asserted "a newer version exists"
  (the cache can lag or lead), and sets the mods with no fileid (FOMOD / manual installs) aside as their own
  manual-verify bucket instead of folding them into "fine".
- **`housecarl_nexus_check_updates` — batch, file-level "is this the current file?".** Pass each mod as
  `id#fileid` and it tells CURRENT from OUTDATED for the **exact file you installed**, not the mod's newest
  MAIN file — the distinction that matters on a multi-file page, where an old-version compatibility patch and
  the current main file share one mod id and a mod-level version compare lies. It resolves manager-only
  (`nxm`, "author disabled direct download") mods that the Nexus search collection excludes — reading them
  through their file data rather than stamping them "not found" — and calls out a genuinely hidden / deleted
  page, and an installed file that's since been pulled, as their own distinct, loud outcomes.
- **`housecarl_nexus_identify` — trace a file to its mod by MD5 hash.** Give it one or more file MD5 hashes
  (the fingerprint a mod manager matches an unknown download by) and it names the source mod, file, and
  version straight from Nexus — keyless — for a file whose origin you've lost.
- **`housecarl_nexus_graphql` — raw, read-only query over the keyless GraphQL.** The completeness backstop
  beneath the curated Nexus tools: when you need a field they don't surface yet (a mod's page tags, say),
  this passes a read-only query through the same public keyless API, so nothing on Nexus is permanently out
  of reach. Prefer the curated tools; this is the escape hatch, not the front door.
- **`housecarl_nexus_mod` reads more of a page.** `files=true` lists every uploaded file; `changelog=true`
  (with an optional `since=`) returns the per-version changelog and the delta between your version and the
  latest; and a mod's page tags now come through — so "what changed since my version, and what kind of mod
  is this?" is answered without a browser. All Nexus traffic now identifies houseCARL by name and version,
  per Nexus's Acceptable Use Policy.

**Bulk / fleet data surface — two new tools, plus query, write, and read flags**

The tool surface was shaped for one record at a time; heavy fan-out runs used it as a bulk data API and had
to improvise the aggregation themselves. This wave adds that aggregation as type-agnostic primitives —
coverage still inherited from the reflection layer, so there are no per-record-type tools.

- **`housecarl_resolve` — many FormIDs → identity, in one call.** Hand it a list of FormIDs and it returns
  each one's record type, EditorID, display name, and load-order winner, with a malformed entry isolated as
  its own per-item error instead of failing the batch. The bulk answer to "what *are* all these forms?".
- **`housecarl_diff_record` — a field-level diff between two plugins' versions of a record.** Point it at a
  FormID and two plugins (`plugin_a` vs `plugin_b`) and it returns only what differs between their two
  versions — either pole active in the load order or read off-order from a plugin sitting on disk — each
  delta self-labeled by the plugin it came from, so a patch rebuild reads the change instead of re-deriving
  it from two full reads and hand subtraction.
- **`housecarl_cross_plugin_query` gains aggregation.** `defined_in=` narrows a `plugins=` scope from every
  record a plugin *touches* (definitions plus overrides) to only the ones it *defines*; a list-valued
  `references=` runs N reverse-lookups in one scan (each hit labeled with which target it matched);
  `group_by=winner|type|defined_in` returns a count table over **all** matches instead of a capped list; and
  `winner_fields=` reads each match's field values from the true load-order winner — with a loud note when a
  `plugins=` scope is otherwise showing scoped-not-winner values, closing a subtle "these numbers don't
  match what I see in game" trap.
- **`resolve_names=` and `format="json"` across the read surfaces.** `resolve_names=true` annotates every
  FormLink in a read with its EditorID and name inline (display-only — the underlying wire token is
  untouched); `format="json"` returns the same outcomes as a machine-readable document with the accounting
  (truncation, no-value leaves) carried in-band, so a fan-out consumer parses the result instead of scraping
  the text.
- **Batch writes — `composes=` and `CopyFrom`.** A write op can now carry `composes=`: a list of
  build-from-parts elements applied in one operation (`Add` appends each, `ReplaceAll` replaces the whole
  modeled list — and `ReplaceAll composes=[]` clears it). And a new **`CopyFrom`** verb transplants a
  field's value from another plugin's version of the record — active or off-order, any field the reflection
  layer models — the reflection-generic generalization of copying an appearance or a stat block across
  plugins.

**New skill (→ 13)**

- **`bulk-record-jobs` — plan "many records → one structured deliverable" jobs.** Catalogues, link and
  recipe graphs, conflict surveys, patch rebuilds, and any fan-out that extracts structured data: this skill
  routes them onto the bulk primitives above (scoped queries, `group_by`, `resolve`, `diff_record`, batch
  writes) instead of per-record loops, carries the game-generic Creation-Kit conventions those jobs need
  (craft vs temper, what a workbench keyword means structurally), and pins one canonical deliverable schema
  so a fleet of subagents stops inventing eight different output shapes. Game-generic only — a specific mod's
  own conventions stay in that mod's skill.

**Finishing an unenabled plugin, and fixes**

- **A pre-enable finishing lane.** `housecarl_check_errors` and `housecarl_compact_plugin` now accept a
  plugin that isn't enabled yet, and `housecarl_read_plugin_file` locates a mod folder that isn't in your
  load order (a freshly-authored houseCARL patch, found by filename) — so you can sweep, compact, or read a
  just-authored plugin before enabling and sorting it in MO2.
- **SkyPatcher interpretation reads more INIs honestly.** A `skin=<donor NPC>` directive is now classified
  rather than throwing, and a bracketed `[Label]` line is treated as the inert grouping label it is, not a
  malformed patch.
- **New-patch naming is collision-safe and un-doubled.** The default patch stem is now `Patch` — dropping
  the doubled `houseCARL - houseCARL_Patch` — and it is checked against the active load order so a fresh
  patch can't collide with an existing plugin's name.
- **A container-read hint now names a knob that actually exists** (it had pointed at a parameter the tool
  doesn't take).

## 1.7.1 — 2026-07-13

A maintenance release: sharper, louder tool feedback and a write-lane fix. No new tools (still 37) or
skills (still 12).

**Query & read surface**

- **A `where=` filter that finds nothing now tells you *why*.** When a field-value predicate read no value
  on every scanned record, the note used to guess "likely a mistyped path" for *every* cause — so a perfectly
  valid field that simply happens to be unset everywhere read as "this field is unreadable." It now classifies
  the actual cause — a wrong/mistyped field, a container/list path, a read fault, or a valid-but-unset field —
  and names the matching next move (for example, that a dialogue topic's player-facing text lives on the DIAL
  `Name`, not the INFO `Prompt`).
- **Unknown tool parameters are now rejected instead of ignored.** A misspelled or unsupported parameter name
  fails loud, rather than being silently dropped and looking like it had no effect.
- **Clearer container/list rendering and biped-slot decode in reads**, and the `depth=` hint now appears only
  on the tools that actually support it.

**Write lane**

- **`housecarl_set_field` `SetAtIndex` composes modeled list elements correctly** — setting an element of a
  modeled list (rather than a plain scalar) no longer mis-applies.

## 1.7.0 — 2026-07-11

houseCARL learns to **see through more of the runtime layer than the record alone**: merge plugins together,
read and write the data baked inside a mesh (`.nif`), and read the entire SkyPatcher layer end to end — what
it does, where it conflicts, and what a record truly looks like after it replays. **Six new tools (→ 37)**;
the skill set is unchanged at 12. Plus a dialogue CK-parity push and a batch of write-lane fixes.

**Six new tools (→ 37)**

- **`housecarl_merge_plugins` — merge several plugins into one new plugin.** A RECORDS-level merge with a
  winner-first graft walk: it renumbers FormIDs **only on collision**, drops masters the merged set no longer
  needs, and swaps the donor mods out at the **MO2 layer** (their folders stay on disk, just disabled) — your
  originals are never rewritten. References it can't fold are reported, never silently dropped.
- **`housecarl_copy_npc_appearance` — copy an NPC's appearance into a standalone.** The composed payoff of the
  standalone-NPC-copy chain: lift a donor NPC's whole appearance — head parts, tints, and the FaceGen mesh and
  textures — into a fresh record that carries **no dependency on the donor's plugin**, auto-widening the file
  lane to the donor's defining plugin. The build behind a portable follower or a face moved between mods, in
  one call instead of a dozen papercut edits.
- **`housecarl_nif_inspect` — read the data values inside a Skyrim mesh (`.nif`).** Open the winning copy of a
  mesh and read what's baked inside it: shape names, the embedded skin / FaceTint texture paths, shader flags,
  alpha, and partitions. houseCARL's first look *inside* a NIF, and the diagnostic half of the dark-face bug —
  is the face mesh pointing at the wrong texture, or is a shape misnamed?
- **`housecarl_nif_set` — write a whitelisted value back into a mesh (`.nif`).** The write companion to
  `nif_inspect`: rewrite a wrong embedded texture path or rename a shape, gated to a whitelist of safe fields
  and verified two ways — an offset-immune block-content diff **plus** a semantic read-back — so a mesh is
  never quietly corrupted. The repair half of the dark-face fix, without leaving houseCARL for NifSkope.
- **`housecarl_skypatcher_layer` — inventory the entire SkyPatcher layer at once.** Every SkyPatcher INI across
  your load order resolved into one picture: apply-order union, VFS shadows, filename gates, and INI-vs-INI
  conflicts — plus the full **ITM triple** (intra-file dead writes, cross-INI duplicates, and true no-op writes
  that change nothing). Report-only: it tells you what the layer does, it never touches it.
- **`housecarl_skypatcher_read` — a record's true state after SkyPatcher.** Replays the whole SkyPatcher layer
  over a single record, in order, and reports what it actually looks like at runtime — the complete filter
  surface, stateful op-by-op, with tiered honesty on the operations it can't fully model. A runtime INI edit
  made as visible as a plugin override.

**Dialogue & authoring (CK parity)**

- **Authored dialogue now matches the Creation Kit down to the byte on quest aliases.** Beyond the
  INFO / DLVW / DLBR / QUST / DIAL fields 1.6.0 closed, a newly-authored quest alias now gets the flags (FNAM)
  and voice types (VTCK) the CK fills in — so a hand-built quest's aliases don't read as subtly wrong.
- **`housecarl_validate_dialogue` closes the matching residual.** It validates the DLVW / DLBR inputs and the
  quest ANAM / FNAM the CK-parity pass fills, and flags a live INFO that's missing its prompt / response text
  (CNAM / ENAM).
- **The SkyPatcher authoring reference is extended to the full Wave 0–2 grammar** and the 27-record-type
  operation→field map that underpins the two new SkyPatcher tools.

**Fixes**

- **Edit tools resolve the extended patch.** An `@editorid` self-reference, and an edit whose `into=` targets a
  patch you haven't enabled yet, now resolve correctly against the patch being extended.
- **In-place and forward-record flows hardened** against a batch of edge-case defects surfaced in live testing.

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

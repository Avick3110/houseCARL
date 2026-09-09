---
name: papyrus-reference
description: >-
  Looks up Papyrus function, event and property signatures — parameter order, types, defaults,
  return type and flags — in a bundled offline corpus covering vanilla, SKSE and the shipped
  SKSE-plugin APIs (PapyrusUtil, JContainers, MCMHelper, po3, SkyUI). Use for any .psc read or
  edit, any added or changed function call, and for an unknown-identifier or type-mismatch
  compile error. Covers the API surface only — not reading a modlist's .psc source, not
  compiling it. A function the corpus does not carry gets a warning and a check path, never an
  invented signature.
compatibility: >-
  Requires the houseCARL MCP server and a configured Mod Organizer 2 instance. The lookup is
  offline; only what reads the live load order needs them.
---

# Papyrus Reference

## Overview

An offline corpus of Papyrus signatures — parameters in order, their types and defaults, return
types, flags and doc-comments — for vanilla scripts, SKSE's additions, and the SKSE-plugin APIs
this skill ships. It is generated from BellCube's
[papyrus-index](https://github.com/BellCubeDev/papyrus-index) and lives under `references/`.

The lookup itself is offline: the index and the reference files ship inside this skill, and reading
them needs no server and no configured instance. The frontmatter's compatibility line is the
prerequisite for the availability half only — the checks that ask whether a function is real on this
machine call the houseCARL MCP server, and a lookup never has to wait on one.

This covers the **API surface**. Reading a modlist's actual `.psc` source is a file job (ask
`housecarl_asset_status` which mod or BSA wins a `Scripts\...` path first, then read it with your
own file tool — or, when the winner is inside an archive, which is where most framework scripts
ship, `housecarl_bsa_extract` with `archive=` and `out_path=` first and read the extracted path);
compiling is `housecarl_compile_script`.

The corpus is the cheap route to a signature, not the only one and not the proof. The deterministic
check is the compile: `housecarl_compile_script` binds every call against the real sources on the
import path, so a signature it rejects is wrong whatever a lookup said, and one it accepts that the
corpus lacks is a corpus hole worth reporting. That is what stands behind "never invent a
signature" — the corpus gets you there in three greps instead of a build.

Invoked directly with a name (`/housecarl:papyrus-reference Substring`), treat the argument as the
function to look up. On Codex the skill is the bare folder name, `papyrus-reference`, and the same
text arrives as prose.

## Look it up

Open the function index at `references/index.jsonl` whenever you need to verify a Papyrus call or
author a `.psc`, and match the full quoted token `"name":"FunctionName"` — the entries are compact
JSON, so a spaced pattern matches nothing.

That trap fails silently. A spaced `"name": "Dispel"`, or one missing the leading quote, matches
**zero lines** for a function that is present, and a format-induced zero is indistinguishable from
a real miss — it routes a present function into the warning path, the exact failure this skill
exists to prevent. **Validate the instrument before you trust a zero:** grep a token you know is
there (`"name":"OnInit"` resolves to several entries). Only once the pattern is proven is an empty
result "not in the corpus".

Casing manufactures the same silent zero, and more often, because Papyrus is case-insensitive and a
call site is written however its author liked. `Debug.notification("...")` is legal Papyrus; the
corpus carries the row as `Notification`, and a case-exact grep for `"name":"notification"` returns
zero for a function that is one line away. **Grep case-insensitively** (`grep -i`) and take the
casing off the matched row, never off the call site.

1. **Take the unqualified name from the call site.** `Self.GetActorValue("Health")` → `GetActorValue`;
   `StringUtil.Substring(s, 0, 4)` → `Substring`.
2. **Grep `references/index.jsonl` case-insensitively for `"name":"<Name>"`.** Three result shapes:
   - **One match.** Take its `file`, `line_start`, `line_end`.
   - **Several matches.** Disambiguate on `qualified`. A qualified call site is authoritative —
     `StringUtil.Substring` is the row whose `qualified` is `StringUtil.Substring`; `solveObjSetter`
     exists on `JDB`, `JFormDB` and `JValue`, and only the call site says which. An unqualified call
     inside a script body resolves up the calling script's `extends` chain — `Self.GetActorValue(...)`
     in a script extending `Actor` is `Actor#GetActorValue`. **`qualified` does not always settle
     it:** many class members carry both a `vanilla` and an `skse` row under one `qualified`, and
     `Actor#GetActorValue` is one of them. When it still ties, break on `source` and prefer `skse` —
     an SKSE install extends the vanilla class. Almost every such pair renders the same block; where
     the two differ it is usually the doc-comment the `skse` block drops or replaces, and only rarely
     a parameter's required-ness. Read both, and call it a declaration conflict only when the
     signature differs, not when a sentence of prose does.
   - **No match.** Go to "Bundled-or-warn" below.
3. **Read the block at `line_start`..`line_end`.** Line-exact, never the whole file: a whole-file
   read pulls in hundreds of unrelated entries and costs orders of magnitude more than the block.
   Line-exact is the rule; one call per entry is not — several blocks can come back in one call
   (a `sed` script over the ranges you resolved), and on a multi-signature job they should.
4. **Read the signature off the block**: parameter order, types, which parameters have defaults and
   what those defaults are, return type, flags (`Native` / `Global` / `Hidden` / `BetaOnly` /
   `DebugOnly`), doc-comment if one exists. Many entries are signature-only — normal, not missing
   data, and never a reason to fall through to the warning path.

Events and properties come out of the same index; the `kind` field discriminates them.

**One stated coverage bound.** Base-class events on `Form.psc` are under-covered: there is no
vanilla or skse row for `OnInit` today, only script-specific ones. A miss on a base-class event is
a hole in this corpus to report, **never** an answer that the event is absent from Papyrus.

## A worked lookup

Call site, in a Quest script that has to survive a re-init:

```papyrus
StorageUtil.FormListAdd(Self, "HC_Audit.forms", theForm)
```

Grep `"name":"FormListAdd"` in `references/index.jsonl`; the `StorageUtil` row reads:

```json
{"name":"FormListAdd","qualified":"StorageUtil.FormListAdd","source":"papyrusutil","file":"references/papyrusutil.md","kind":"global","requires_plugin":"PapyrusUtilSE.dll","line_start":5163,"line_end":5175}
```

Read `references/papyrusutil.md` lines 5163-5175 and the block gives
`FormListAdd(ObjKey, KeyName, value, allowDuplicate) → Int`, `Native Global`, with
`allowDuplicate: Bool` defaulting to **`true`**. That default is the payoff: without a
`FormListClear` first, a re-init silently doubles the list — and it still compiles.

## Silent biters

Eight traps a correct signature does not reveal. Each compiles clean and then misbehaves:

- `Game.GetForm` returns `None` for any FormID `>= 0x80000000` — the whole ESL range *and* load
  index `0x80+` — use `Game.GetFormEx`.
- `SendModEvent` sends three arguments; the handler receives **four** (the engine appends the
  sender), and a three-parameter handler silently never runs.
- Papyrus string `==` is case-**insensitive**, so a case difference is never the cause of a missed
  string match.
- `FormList.HasForm` misses base-`NPC_` entries when you pass a placed reference — check
  `list.HasForm(akNPC) || list.HasForm(akNPC.GetActorBase())`.
- `JFormDB` and `StorageUtil` are separate backends: write to one, read from the other, and the
  read comes back empty with no error.
- `Utility.Wait` inside a paused-menu or input handler does not count real time and bursts on
  unpause — use `RegisterForSingleUpdate`.
- `\n` in a string literal is not safe across compilers, and a docstring cannot contain a `{`.
- `key`, `quest`, `actor`, `form` and other type names used as identifiers collide with the type
  and surface as "variable X is undefined".

Read `references/silent-biters.md` before any call that rebuilds a form from a stored FormID, sends
or handles a mod event, gates on a `FormList` of NPCs, reads or writes external storage, waits
inside a menu handler, or embeds free text in a literal — the eight rules are above, that file
carries why each one bites.

## Bundled-or-warn — never invent a signature

When the index has no match, say so:

```
No Papyrus reference for `FunctionName` — not in the bundled corpus.
I will not invent a signature.
```

Then work the checks, in cost order:

1. **Spelling and the grep pattern.** A typo and a spaced pattern produce the same empty result.
2. **Is the function real on this machine?** `housecarl_skse` with `findings='pairing'` and
   `filter=<Class or providing mod>` lists the native function names the winning compiled script
   actually declares for that class. That is a declaration, not a runtime guarantee: the tool
   answers whether a pairing is plausible and healthy, never whether the DLL registers exactly
   these functions, and the absence of a token proves nothing.
3. **The declaration itself.** `housecarl_decompile_script` with `pex=` recovers the real
   declaration — names, types, properties, states, events and docstrings all survive. For a class
   inside an archive, `housecarl_bsa_extract` with `archive=` and `out_path=` first, then decompile the
   extracted path. **Parameter defaults do not survive a decompile** — they never existed in the
   `.pex` — so a decompiled declaration answers arity and types and cannot answer a default.
4. **Compile it.** `housecarl_compile_script` with `script=` is the deterministic answer: it puts
   the enabled mods' own Papyrus sources on the import path and returns per-line errors. A call
   that compiles binds; a call the corpus lacks that compiles is a corpus hole to report.

Never invent a signature, including under pressure to "just guess". A confidently wrong signature
ships code that compiles and misbehaves at runtime, or turns an authoring error into a filed
upstream bug — both cost far more than the non-answer.

## Tier 2 — the corpus carries more than a modlist installs

The corpus ships two tiers, and the tier is a fact about the corpus, not about the user's machine.
**Read the tier off `source`, never off the presence of `requires_plugin`.** Tier 1 is `source`
`vanilla` or `skse`, which any Skyrim + SKSE install has. Everything else is Tier 2 — the
SKSE-plugin sources, PapyrusUtil, JContainers, MCMHelper, po3, SkyUI and the rest — whether or not
the row names a DLL. Several Tier-2 sources carry no `requires_plugin` on any row (`clib`,
`dynamicwetness`, `skyprompt` among them), and reading a missing key as Tier 1 reports a
third-party function as always present and skips the availability check below.

`requires_plugin` is an **indicative hint, not a gate**. The filename it carries differs between
builds: StorageUtil rows name `PapyrusUtilSE.dll` while the AE build installs `PapyrusUtil.dll`,
so matching that filename against the load order fails on a plugin that is present and working.
Match on the mod or plugin *name* when you need a quick read, and defer the real question to
`housecarl_skse` with `findings='pairing'` and `filter=<class or providing mod>`, which pairs the
declaring scripts to the DLLs the providing mod ships and leads with **PAIRED-BUT-DEAD** and
**UNPAIRED**.

Installed is not the same as working. `findings='pairing'` calls a plugin PAIRED-BUT-DEAD when
every candidate DLL statically cannot load — wrong game runtime, BSA-only, subfolder-shipped,
32-bit, unreadable, debug-built. `findings='inventory'` leads with its own diagnostics over the
whole SKSE layer: version-LOCKED plugins, legacy query-only plugins, non-plugin DLLs, subfolder
DLLs, DLLs contested by more than one mod, and debug-build plugins. Report either answer as a
verify flag, not as a promise about a running game.

A Tier-2 function whose provider is missing or dead is not a corpus miss: the signature is correct
and the call will no-op at runtime. Say that, rather than the bundled-or-warn warning.

## The index format

`references/index.jsonl` is one compact-JSON entry per line. Three real rows:

```json
{"name":"GetActorValue","qualified":"Actor#GetActorValue","source":"vanilla","file":"references/vanilla/Actor.md","kind":"instance-method","line_start":734,"line_end":745}
{"name":"Substring","qualified":"StringUtil.Substring","source":"skse","file":"references/skse/StringUtil.md","kind":"global","line_start":142,"line_end":156}
{"name":"solveObjSetter","qualified":"JDB.solveObjSetter","source":"jcontainers","file":"references/jcontainers.md","kind":"global","requires_plugin":"JContainers64.dll","line_start":1317,"line_end":1328}
```

- `name` — unqualified function, event or property name. The lookup key.
- `qualified` — `Script.Function`, `Script#Method`, `Script.Event`, `Script.Property`. Disambiguates
  a `name` that collides across sources.
- `source` — the source directory the entry came from (`vanilla`, `skse`, `papyrusutil`, …). Also
  the tier discriminator, and the tie-break when `qualified` collides.
- `file` — the reference file holding the entry, rooted at the skill folder.
- `kind` — `global`, `instance-method`, `event` or `property`.
- `line_start` / `line_end` — 1-indexed inclusive block range inside `file`.
- `requires_plugin` — on some Tier-2 rows only, and indicative (see above). Its absence says
  nothing about the tier.

**The index is the intended second hop.** Every generated file under `references/` is reached
through a row's `file` field and read at its `line_start`..`line_end`, and no read off the index is
ever whole-file. Generated files **over 100 lines** carry a `## Contents` table for the rare
fallback read; the shorter ones do not, and that is not a corpus bug. The index itself is grepped,
never bulk-loaded. Two files sit outside all of this: `silent-biters.md` and `corpus-notes.md` are
hand-authored, are reachable from no row, and are read whole by name.

## Getting it wrong, and the rule instead

- Grep the index case-insensitively with the compact token, `grep -i '"name":"Dispel"'` (a spaced
  `"name": "Dispel"` matches nothing at any casing, and a case-*exact* grep for `"name":"dispel"`
  matches nothing either — both read as absence; under `-i` the miscased pattern resolves, so a zero
  from it is a real miss).
- Look up by the unqualified `name`, and use `qualified` only to disambiguate, then `source` when
  `qualified` ties (the index's key is `Substring`, not `StringUtil.Substring`).
- Trust the index's `file` field over any file a user names (if a lookup for `Foo` resolves to
  `references/skse/Form.md` and the user said `Actor.md`, the index wins — investigate the gap).
- Read the block at `line_start`..`line_end`, batching several ranges into one call where you can
  (a whole-file read on a large source is pure waste; fall back to one only for a malformed block,
  which is a corpus bug worth reporting).
- Treat a signature-only entry as complete (a missing doc-comment is not a missing function).
- Say that events are engine-invoked when someone asks how to call one — `kind: "event"` rows look
  like functions and are not called.
- Re-grep and re-read for every signature question, including a name you looked up earlier in the
  same session. A remembered signature is a recalled signature; the index costs one grep.

## Maintaining the corpus

Regenerating the corpus, or hand-authoring an entry for a source it does not carry, is
`references/corpus-notes.md` — read it only when changing the corpus, never to answer a lookup.

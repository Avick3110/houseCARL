# Papyrus silent-biters — compiles clean, misbehaves at runtime

A hand-curated companion to this skill's by-construction signature corpus. The function blocks under
`references/` (generated from BellCube's papyrus-index) give you the correct *signature*; this file carries
the *semantic* traps a correct signature does not reveal — each one compiles without error and then silently
no-runs or returns wrong at runtime. Read it whenever a `.psc` does any of: rebuilds forms from stored
FormIDs, sends or handles a mod event, gates on a `FormList` of NPCs, reads/writes external storage, calls
`Utility.Wait` from a menu/input handler, or embeds free text in a literal or docstring. These are editorial
notes, not signatures — confirm any exact signature against the `references/` corpus via the lookup procedure
in SKILL.md.

## 1. `Game.GetFormEx`, never `Game.GetForm`

`GetForm` returns `None` for any FormID `>= 0x80000000` — the entire ESL / ESPFE light-plugin range *and*
load index `0x80+`. ESL-flagged followers and content are everywhere, so a quest that rebuilds actors or
aliases from stored FormIDs silently fails for them. Always `Game.GetFormEx(id)`. (`Game.GetFormFromFile(localID, "Plugin.esp")`
is unaffected, and is also clobber-proof and load-order-independent.) The bundled `GetFormEx` doc says only
"same as GetForm, but also works for formIds >= 0x80000000" — this is the *why* that matters.

## 2. `Form.SendModEvent` delivers FOUR args — a 3-param handler never runs

`SendModEvent(eventName, strArg, numArg)` looks 3-arg from the *sender* side, but the engine appends the
sender, so the *handler* receives four: `(string eventName, string strArg, float numArg, Form sender)`. A
handler declared with only three parameters throws an arg-count error and **silently never runs** — which
can kill an entire quest's event-driven stage advancement with no log line. A handler registered via
`RegisterForModEvent` must be `Event MyHandler(string eventName, string strArg, float numArg, Form sender)`.

## 3. Papyrus string `==` is case-INSENSITIVE — don't chase a case bug on event strings

A trap of misdiagnosis, not of behavior: `==` on Papyrus `String`s ignores case, so a mod-event `strArg` you
send as `"OpenMenu"` still matches `payload == "openmenu"`. Case can NOT cause a missed match, and normalizing
case "to be safe" before comparing is wasted work, not a fix. So if an event comparison that should match is
failing, the cause is something else — leading/trailing whitespace, or a genuinely different string — never
case. (This is the opposite of most languages, which is why it's worth stating.)

## 4. `FormList.HasForm` misses base-`NPC_` entries

A `FormList` populated by dragging NPCs from the Object Window (or any base-form fill) holds *base* `NPC_`
forms, but a runtime check almost always passes a placed *reference* (an `Actor`). `list.HasForm(akActorRef)`
then returns false even though "the NPC is in the list." Check both:
`list.HasForm(akNPC) || list.HasForm(akNPC.GetActorBase())`.

## 5. Storage: read from the backend you wrote to (JFormDB vs StorageUtil)

JContainers' `JFormDB` and PapyrusUtil's `StorageUtil` are separate backends. Write to one and read from the
other and the read comes back **empty/zero** with no error — a stage gate then silently evaluates against the
default value. Pick one backend per data item and read it back from the same one; don't mix them for the same
key.

## 6. `Utility.Wait` in a paused-menu / input handler resumes wrong

`Utility.Wait` inside an input or menu handler that runs while a pause-game UI is open does not count real
time (the VM is paused), and every queued handler bursts on unpause → doubled stage advances / doubled fires.
Use `RegisterForSingleUpdate` for any delay taken from a menu/input context. (The `papyrus-optimization` skill
covers `Utility.Wait` thread-pinning and cost more generally.)

## 7. String-literal and docstring escaping rules

The Papyrus grammar documents four string-literal escapes — `\\`, `\"`, `\n`, `\t` — but only `\\` and `\"`
are safe to rely on across compilers. Bethesda's stock `PapyrusCompiler.exe` has been reported to REJECT `\n`
in a literal (it fails to compile, surfacing as a generic `required (...)+ loop did not match anything` parser
error that does NOT name the escape), even though the grammar and Caprica accept it; `\r` has no documented
escape at all. So avoid `\n`/`\r`/`\t` in a literal unless you've confirmed your compiler accepts them — build
the string from pieces instead. Docstrings `{ ... }` are legal only immediately after `ScriptName`, `Property`,
`Function`, or `Event`, and **cannot contain a literal `{`** — so pasting JSON into a `{ ... }` comment cascades
parse errors across the whole compile.

## 8. Common-noun type collisions

`key`, `quest`, `actor`, `form`, `cell`, `weapon` (and other type names) used as plain variable or parameter
identifiers collide with the type and surface as `variable X is undefined`. Prefix them (`myKey`, `theQuest`,
`akActor`) — the compiler error names the *identifier*, not the collision, so this one wastes debugging time.

## When to escalate

None of these is in the signature corpus — they are behavioral, not declarative. If a `.psc` exhibits the
symptom (a handler that "compiled fine" but never fires, a `FormList` gate that's always false, a doubled
stage advance, state that reads back empty), check this list before assuming an upstream bug.

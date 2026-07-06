# Load failures — why a DLL refuses to load, and how to triage it

The triage backbone for "my SKSE plugin won't load" and "the game CTDs at startup." Every refusal SKSE
can produce writes one exact-format line to a log and, usually, one popup — and the popup *caption* plus
the log *string* together pin the cause. This reference maps symptom → log line → cause → fix.

This is also the material houseCARL's future **crash-diagnostics** skill consumes. Where a plugin-author
reads this table to fix their own plugin, crash-diagnostics reads the same table in reverse — from a
user's `skse64.log` line or popup caption back to a cause and a fix. Keep the strings verbatim; they are
version-pinned (below) because SKSE has reworded dialogs across releases.

The log-path and some string differences are **per runtime** (SE/AE vs VR, and Steam vs GOG vs Epic).
Those splits are called out inline and expanded in `multi-runtime.md`; get them right, because reading
the wrong log file is itself a common dead end.

## Contents

1. The two discriminators — popup caption and log line
2. Where the logs live (per runtime)
3. The load pipeline: loader → scan → real load
4. Rejection table (exact `skse64.log` strings → fix)
5. The scan-vs-load dependency signature
6. The CommonLib self-abort surface (plugin passed SKSE, then killed the game)
7. Not yet verified in-game
8. The crash-diagnostics handoff

---

## 1. The two discriminators — popup caption and log line

Two things localize almost any load failure:

- **The popup caption.** `SKSE Plugin Loader (…)` means **SKSE refused the plugin** — the game is usually
  still startable without it. A caption that is a **plugin's own `.dll` filename** means **that plugin's
  CommonLib aborted itself** — the process is already dead and SKSE's own error report never appears.
  This one distinction routes you to the right half of this document (§4 vs §6).
- **The exact log line.** Every SKSE rejection writes one line of a fixed shape to `skse64.log`:
  `plugin <dll> (<dataVersion> <name> <pluginVersion>) <reason> <errCode> (handle <n>)`. A successful
  load instead logs `… loaded correctly (handle <n>)`. The `<reason>` substring is the lookup key for
  §4.

---

## 2. Where the logs live (per runtime)

| File | Path | Written by |
|---|---|---|
| `skse64.log` | `<Documents>\My Games\<FOLDER>\SKSE\skse64.log` | the SKSE runtime DLL |
| `skse64_loader.log` | same folder | `skse64_loader.exe` — a failure **here** means no plugin was ever evaluated (§3, loader layer) |
| `<PluginName>.log` | same folder | each CommonLib plugin's own spdlog |

`<FOLDER>` is the per-edition Documents folder: `Skyrim Special Edition` (Steam), `Skyrim Special
Edition GOG`, `Skyrim Special Edition EPIC`, or `Skyrim VR`. **Reading the wrong folder is a common false
"no log" result** — an Epic or GOG install writes to its own folder, and VR to `Skyrim VR`. The exact
edition-detection rules (and the subtly different probe NG's per-plugin `log_directory()` uses) are in
`multi-runtime.md`.

The distinction between the three files matters for triage: a problem in `skse64_loader.log` is upstream
of *any* plugin (§3 loader layer); a `<PluginName>.log` that is **empty or absent** when the plugin was
expected to run is itself a signal (§6).

---

## 3. The load pipeline: loader → scan → real load

SKSE decides loading in stages, and knowing which stage failed narrows the cause immediately.

**Loader layer (no plugin evaluated yet).** Before any plugin is scanned, `skse64_loader.exe` checks the
game itself. These produce loader popups, not plugin log lines:

- game **newer** than SKSE → *"You are using a newer version of Skyrim than this version of SKSE64
  supports…"* — SKSE needs updating (or wait for an update after a Skyrim patch).
- game **older** than SKSE → *"…which is out of date and incompatible…"*.
- **wrong store build** → *"This version of SKSE is compatible with the %s version of the game. You have
  the %s version…"* — you installed the Steam SKSE on a GOG/Epic game or vice-versa.
- **MS Store / Game Pass** → *"The Windows Store (gamepass) version of Skyrim is not supported."*
- **Epic** → *"The Epic Store version of Skyrim is not supported."* (a **distinct** message from the
  WinStore one — do not conflate them).

**Scan layer (code-free).** SKSE enumerates `<runtime dir>\Data\SKSE\Plugins\*.dll`, maps each as a
resource — **no plugin code runs** — and judges the exported `SKSEPlugin_Version` blob. All the
`disabled, …` reasons in §4 come from here.

**Real-load layer.** Survivors are actually `LoadLibrary`'d and their `SKSEPlugin_Load` called. Any
failure frees the DLL and drops it from the list. The `couldn't load plugin` + Win32 code reasons come
from here — and this is where a **missing dependency DLL** first bites (§5), because the scan never
resolved imports.

---

## 4. Rejection table (exact `skse64.log` strings → fix)

Grouped by the stage that emits them. "In popup?" tells you whether the user also saw a dialog.

| Stage | Exact reason string | In popup? | Cause | Fix |
|---|---|---|---|---|
| scan | `couldn't load plugin` + Win32 code | yes | corrupt DLL (failed even as a resource) | reinstall the plugin |
| scan | `LE plugin cannot be used with SE` | yes | a 32-bit Oldrim/LE DLL | get the SE/AE build |
| scan | `no version data` | **no — silent skip** | (a) an SE-era Query-only plugin under AE SKSE; (b) a non-plugin helper DLL sitting in Plugins/ | (a) update the plugin; (b) ignore it |
| scan | `disabled, bad version data` | yes | zeroed version struct (`dataVersion == 0`) | plugin author bug |
| scan | `disabled, no name specified` | yes | empty name field | plugin author bug |
| scan | `disabled, unsupported version independence method` | yes | unknown version-independence bits — plugin built for a **future** SKSE API | update SKSE |
| scan | `disabled, address library needs to be updated` | yes + a dedicated Address-Library prompt (Nexus 32444) | plugin declared Address Library, but `versionlib-<M>-<m>-<b>-0.bin` for **this exact runtime** is absent | install/update Address Library for the runtime |
| scan | `disabled, only compatible with versions earlier than 1.6.629` | yes | version-independent plugin on runtime ≥1.6.629 that declared neither post-629 structs nor no-structs | update the plugin (author adds the right struct flag) |
| scan | `disabled, incompatible with current version of the game` | yes | an exact-version-list plugin whose list didn't match — **including a GOG/Epic build whose store sub-nibble differs from a Steam-built list** | update the plugin for this runtime, or switch it to Address Library |
| scan | `disabled, requires newer script extender` | yes | plugin's minimum SKSE version exceeds the running one | update SKSE |
| scan | `disabled, fatal error occurred while checking plugin compatibility` | yes | an exception during the check (garbage struct) | plugin author bug |
| load | `couldn't load plugin` + Win32 code (log **decimal**, popup **hex**) | yes | a real `LoadLibrary` failure — **most commonly 126 / ERROR_MOD_NOT_FOUND: a dependency DLL is missing** (SKSE special-cases `EngineFixes.dll`+126 → *"SSE Engine Fixes (Part 2) not installed. Read the mod page."*). A common author-side cause: the DLL is a **Debug-configuration build**, whose debug CRT (`MSVCP140D.dll` …) exists only on developer machines | install the missing dependency (VC++ redist, Engine Fixes Part 2 files, etc.); if you built the DLL, ship a release-CRT build (RelWithDebInfo) |
| load | `does not appear to be an SKSE plugin` | yes | passed the data check but exports neither `SKSEPlugin_Load` nor `SKSEPlugin_Preload` | export `SKSEPlugin_Load` |
| load | `reported as incompatible during load` | yes | `SKSEPlugin_Load` returned false | an intentional self-refusal — read the plugin's own `<PluginName>.log` for why |
| load | `disabled, fatal error occurred while loading plugin` | yes | an exception inside the load function | crash-triage the plugin |
| postload | `crashed during postload` | **no** (the popup already fired earlier) | an exception in a kPostLoad/kPostPostLoad handler | update or report the plugin |
| — | *(no reason line; log ends near `loading plugin "<name>"`)* | no popup; game CTDs at startup | an **unguarded** crash in the plugin's DllMain / static init during `LoadLibrary` | binary-search the last `loading plugin` line to find the culprit |

**The aggregate popup** (when one or more plugins failed): title `SKSE Plugin Loader (<version>)`, body
*"A DLL plugin has failed to load correctly. If a new version of Skyrim was just released, the plugin
needs to be updated…"*, one `<dll>: <reason> (<hex code>)` line per failure, footer *"Continuing to load
may result in lost save data… Exit game? (yes highly suggested)"*. Choosing **No** keeps the game running
without the failed plugins (they were already pruned).

**Interface skew is not a rejection.** If a plugin asks for an interface version SKSE doesn't recognize,
SKSE returns null for that one query and NG merely warns `interface definition is out of date` while
still handing back the pointer. The plugin degrades; it is not refused. Don't chase an
`out of date` warning as a load failure.

---

## 5. The scan-vs-load dependency signature

The scan maps DLLs as resources, which resolves **no imports and runs no DllMain**. So a plugin with a
**missing dependency DLL passes the entire compatibility scan** and only fails later at the real
`LoadLibrary`, with `couldn't load plugin` + a Win32 code (typically 126).

That gives a clean diagnostic signature: **"passed checking, failed loading" in `skse64.log` means a
dependency is missing, not a version problem.** Don't send the user chasing Address Library or version
mismatches when the log shows the plugin cleared the scan and only tripped at load — point them at the
missing runtime/dependency instead.

---

## 6. The CommonLib self-abort surface (plugin passed SKSE, then killed the game)

An NG plugin declares Address Library + struct-independence by default, so **SKSE's static scan passes on
any runtime where the versionlib file merely exists — the real version gate has moved *inside* the
plugin.** `SKSE::Init` forces the Address Library / REL layer to initialize early (before logging is even
set up), and if that layer can't reconcile itself with the running exe it calls `report_and_fail`, which
pops a MessageBox **captioned with the plugin's own DLL filename** and then terminates the whole process.

This is the other half of the caption discriminator from §1: a **plugin-named** popup is a self-abort in
the REL/Address-Library layer, not a plugin-logic bug and not an SKSE refusal.

| Message (popup body / plugin-log critical line) | Cause |
|---|---|
| `interface is null` | `SKSE::Init(nullptr)` — a plugin wiring bug |
| `failed to open address library file` | the expected Address Library `.bin`/`.csv` is absent or unreadable |
| `Failed to locate an appropriate address library with the path: <path>…` | stream error reading the DB — the file for **this exact game version** is missing |
| `Unsupported address library format: <n>…` | an SE-format file (`version-*.bin`) where an AE-format one (`versionlib-*.bin`) was needed, or vice-versa |
| `version mismatch` | the `.bin`'s embedded version ≠ the running exe's version |
| `Failed to find the id within the address library: <id>\nThis means this script extender plugin is incompatible with the address library for this version of the game…` | the plugin uses an address ID that doesn't exist for this runtime — genuinely unsupported version |
| `Required VR Address Library file <f> does not exist` / row-count mismatch | the VR Address Library `.csv` is missing or truncated |
| `Failed to obtain module handle for: "<exe>". You have likely renamed the executable…` | the host exe is neither `SkyrimSE.exe` nor `SkyrimVR.exe` (only those names are probed; a `SKSE_RUNTIME` env var can override) |
| `Failed to obtain file version info for: <exe>` | the exe lacks a version resource |

**Expected Address Library filenames** (version string uses `-` separators): AE
`Data/SKSE/Plugins/versionlib-1-6-1170-0.bin`, SE `version-1-5-97-0.bin`, VR `version-1-4-15-0.csv`. The
runtime is classified from the exe's file-version minor field (4 → VR, 6 → AE, else SE). A format/name
mismatch here (SE `.bin` on an AE game, missing VR `.csv`) is the concrete cause behind the
`Unsupported address library format` and `does not exist` lines above.

**The empty-log heuristic.** Because `SKSE::Init` touches the REL layer *before* logging is initialized,
a REL-layer abort typically leaves the plugin's own `<PluginName>.log` **empty or entirely absent**. So:
a **plugin-captioned popup plus no per-plugin log** points at an Address Library / module problem, **not**
at the plugin's own logic. (A plugin that hand-inits logging before `SKSE::Init` — see the logging
setup in `plugin-skeleton.md` — can capture the critical line anyway, since it also goes to the logger.)

---

## 7. Not yet verified in-game

The AE rejection pipeline above is proven against current SKSE source. Some entries rest on inference or
on loaders not present in the source corpus, and should be build-tested on a real install before being
relied on as exact:

- **The SE (1.5.97) and VR loader wording and behavior** — the AE-era `skse64` is the pinned source; the
  SE/VR loaders were not dissected, so their exact log strings and popup captions (and whether VR
  dispatches the full message set) are inferred.
- **GOG/Epic exact-match rejection** — that a GOG/Epic build actually refuses a Steam-constant version
  list (via the store sub-nibble), and that the shipped GOG Address Library naming matches SKSE's
  hardcoded `-0` component, are inferred from the exact-match code, not observed on those installs.
- **The unguarded-DllMain crash presentation** — the "no reason line, game CTDs" row is inferred from the
  absence of a guard around the install-time `LoadLibrary`; the exact on-screen presentation is unconfirmed.
- **Popup wording is version-pinned** to SKSE 2.2.6. Older SKSE releases may word the aggregate dialog
  differently — when crash-diagnostics consumes these strings, pin them to the user's SKSE version.

Present none of these as proven runtime behavior; where a triage hinges on one, say it's inferred and
what would confirm it.

---

## 8. The crash-diagnostics handoff

This table is deliberately structured for reuse. houseCARL's crash-diagnostics skill will read a user's
`skse64.log` line or popup caption and walk it **backwards** through §1 (which discriminator), §2 (which
log file, per runtime), and §4/§6 (line → cause → fix). The load-order-independent facts it relies on:

- caption `SKSE Plugin Loader (…)` ⇒ SKSE refusal, game usually still startable ⇒ use §4;
- caption = a plugin `.dll` name ⇒ CommonLib self-abort, process already dead ⇒ use §6;
- "passed checking, failed loading" ⇒ missing dependency, not a version issue (§5);
- plugin-captioned popup + empty/absent `<PluginName>.log` ⇒ Address Library / module problem, not plugin
  logic (§6);
- the last `dispatch message (<n>)` / `loading plugin "<name>"` line before a crash localizes the failing
  phase or plugin.

When crash-diagnostics ships, these strings live in one place; keep this reference and that skill in sync
rather than duplicating the table, and keep every string pinned to the SKSE version it was read from.

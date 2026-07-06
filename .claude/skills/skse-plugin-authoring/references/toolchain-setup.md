# Toolchain & scaffold — from nothing to a built plugin DLL

The setup gate. Before you write a line of a plugin's logic you need a build environment that can consume
CommonLibSSE-NG, a scaffold that wires the library in correctly, and a build→deploy→verify loop tight
enough that "does it load?" is answered in seconds, not by guesswork. This reference walks that path.

Everything here is the C++ / build-system / SKSE-loader surface. The moment a plugin registers Papyrus
native functions, the `.psc` signatures on the other side of that boundary belong to houseCARL's
`papyrus-reference` skill — this reference names the C++ registration call and stops. See
`native-papyrus-functions.md` for the marshalling layer.

## Contents

- **The lineage you are building against** — why "CommonLibSSE-NG" is not one thing, and which one to use.
- **The blessed environment** — VS2022 Build Tools + CMake + vcpkg (and the xmake alternative).
- **Getting the library** — no live registry; the git-submodule route.
- **Consumer obligations** — the PCH and `/Zc:preprocessor` that every consumer must supply.
- **The minimal scaffold** — the proven xmake file-set, and the honestly-unbuilt CMake one.
- **`add_commonlibsse_plugin`** — what the CMake helper generates and its availability trap.
- **Per-runtime presets** — configuring for SE / AE / VR.
- **Deploy into an MO2 mod** — the `SKSE/Plugins/<name>.dll` artifact shape.
- **The loop** — build → deploy → verify (`skse64.log`) → iterate.
- **Rebuilding an existing plugin from source** — ABI-faithful rebuilds for one-defect patches.
- **ClibDT** — the beginner automation that does the whole setup for you.
- **Not yet verified in-game** — the empirical build-tests still owed.

---

## The lineage you are building against

"CommonLibSSE-NG" is not one repository, and getting the lineage wrong means either the dependency won't
resolve or the idioms you copy from a template won't compile. Four lineages coexist, distinguished by
**package identity**:

| Lineage | Repo | vcpkg name | CMake package | VR? | State |
|---|---|---|---|---|---|
| **alandtse NG** (the one to use) | `alandtse/CommonLibVR` branch `ng` | `commonlibsse-ng` @ 4.x | `CommonLibSSE` → `CommonLibSSE::CommonLibSSE` | SE+AE+VR, **single DLL** | Live |
| CharmedBaryon (frozen origin) | `CharmedBaryon/CommonLibSSE-NG` | `commonlibsse-ng` @ 3.7.0 | `CommonLibSSE` | SE+AE+VR | Frozen since 2023-05 |
| powerof3 dev | `powerof3/CommonLibSSE` branch `dev` | `commonlibsse` @ "1" | `CommonLibSSE` | **SE/AE only** | Active, different lineage |
| libxse | `libxse/commonlibsse` | (xmake-only) | — | unverified | Early fork of po3 |

Build against **alandtse NG** — this is the current live lineage and houseCARL's charter-locked target. Do
not switch off it. The other three are here so you *recognize* them in the wild, not so you reach for them:

- The **triple-naming trap**: the live lineage is `commonlibsse-ng` in a vcpkg manifest, `CommonLibSSE` as a
  CMake package (you write `find_package(CommonLibSSE)` / link `CommonLibSSE::CommonLibSSE`), and
  `commonlibsse-ng` as an xmake target. Three names, one library.
- **powerof3 `dev`** publishes the bare `commonlibsse` identity — a *different* package name, not a version
  of `commonlibsse-ng` — and has **no VR**. Idioms lifted from a po3 plugin may not map cleanly.
- **libxse** (observed 2026-07) is an emerging org. Its *older, adopted* `commonlibsse-ng-template` still
  submodules alandtse `ng` (so it is a real NG consumer), but its *newer* `commonlibsse-template` moved to
  `libxse/commonlibsse`, a fork of po3 — not NG. A tell: the new-lineage template logs via `REX::INFO`, and
  **`REX::INFO` does not exist in alandtse NG** (the NG `REX/` headers ship only `Enum`, `EnumSet`, `INI`,
  `JSON`, `Setting`, `TOML`, `Singleton` — no logging header). If you see `REX::INFO` in a template you are
  not looking at NG code; do not copy it into an NG plugin.

---

## The blessed environment

Two build systems consume the current lineage. Both end at the same DLL. Pick one and stay in it.

**xmake** is the battle-tested path — the current lineage's own repository builds with xmake, the community
`ClibDT` bootstrapper drives it, and the plugin scaffolding it generates is the most robust (see the
availability trap below). Install: **VS2022 Build Tools** (the MSVC C++23 toolset, not full Visual Studio —
Build Tools suffice), **xmake ≥ 3.0**, and **Git**. Run builds from a **VS Developer Shell** — outside it,
the toolchain can mis-detect the compiler.

**CMake + vcpkg** is the hand-wired alternative, familiar to most C++ developers. Install: **VS2022 Build
Tools**, **CMake ≥ 3.21**, **vcpkg** (bootstrapped and on `PATH` or pointed at via a toolchain file), and
**Git**. This route needs more explicit wiring — and its minimal-scaffold story carries an honest caveat
(below).

Both need a **C++23-capable MSVC** — CommonLibSSE-NG is a C++23 library and older toolsets will not build it.

---

## Getting the library — there is no live registry

The one that surprises people: **the current lineage publishes no vcpkg registry, no overlay port, and no
Conan package.** The frozen registry routes you will find scattered through 2023-era templates
(`gitlab.com/colorglass/vcpkg-colorglass`, the `charmedbaryon.jfrog.io` Conan remote, repo-local overlay
ports) all top out at CharmedBaryon **3.7.0** — never the live 4.x lineage. The NG README explicitly
disowns the old vcpkg and Conan routes as "not part of this repository and not currently maintained."
Consuming through any registry you see in the wild yields a **3.x build**, not the current one.

**The maintained acquisition is a git submodule:**

```powershell
git submodule add -b ng https://github.com/alandtse/CommonLibVR.git lib/commonlibsse-ng
git submodule update --init --recursive
```

That single command is the difference between building the live library and silently pinning a frozen 2023
fork. When you audit an unfamiliar plugin's build, check *how* it acquires CommonLib before trusting that
it is on the current lineage — a `vcpkg-configuration.json` pointing at the colorglass registry is a
frozen-3.x tell.

**A faster route for CI or a slow machine:** every NG release tag attaches a prebuilt static-lib bundle
(all-runtimes, MSVC release `/MD`, C++23) that drops in for the submodule and turns a ~16-minute cold
source compile into a seconds-long link. It auto-fetches when the submodule sits on a clean release tag and
either you are in CI or you set `COMMONLIB_PREBUILT=1` locally; it falls back to a source build on any
unsafe condition (dirty tree, a static-CRT `/MT` consumer, offline). Local dev stays source-by-default,
which is fine. One landmine if you take the **xmake** prebuilt route: keep `rex_json` / `rex_toml`
**OFF** or configure fails — the xmake prebuilt path refuses them even though the docs read otherwise. The
CMake bundle does not have that restriction.

---

## Consumer obligations — the two things every consumer must supply

Whether you build from source or link the prebuilt, two requirements are non-negotiable and easy to miss,
because they fail with errors that don't name the real cause.

**A force-included precompiled header.** CommonLibSSE-NG headers are PCH-dependent — consumers force-include
a PCH. The canonical minimum is `#include <SKSE/Impl/PCH.h>` plus `using namespace std::literals;`. But
`Impl/PCH.h` pulls in only std/Windows headers — **not** `RE/Skyrim.h`, `SKSE/SKSE.h`, or
`SKSE/Interfaces.h`. So if your `plugin.cpp` has no includes of its own, put the game headers *into the
PCH*:

```cpp
// src/pch.h — the hello-world-shaped PCH that most templates use
#pragma once
#include <RE/Skyrim.h>
#include <SKSE/SKSE.h>
namespace logs = SKSE::log;
```

This PCH is load-bearing **twice**: it supplies the game API to your code, and it supplies the
`using namespace std::literals;` that the build system's generated declaration file needs to compile its
`"...sv` string literals (more on that under `add_commonlibsse_plugin`). Skip it and you get undeclared
`SKSEPluginLoad` / `RE::ConsoleLog` and cryptic `sv`-suffix errors.

**`/Zc:preprocessor`** (MSVC's conforming preprocessor) in your compile flags. Without it, CommonLib's *own*
headers fail in your translation unit with `STATIC_ASSERT_SIZE requires at least 2 arguments` — its
variadic-macro dispatcher breaks under the traditional preprocessor. The classic symptom is "CommonLib
builds fine in isolation, but fails to compile when consumed as a dependency." The prebuilt IMPORTED target
propagates this flag for you; a hand-assembled build must set it explicitly.

From your **own** `vcpkg.json` you must also supply CommonLib's public/transitive header deps — a source
build does not vendor them: always `spdlog`, `fmt`, `directxtk`, `directxmath`; plus `rapidcsv` whenever VR
support is on (the default); `xbyak` only if you enable xbyak support; and `simpleini` / `nlohmann-json` /
`toml11` only with the matching REX option. Field note: in practice a source checkout's `find_package` calls
often demand `rapidcsv` and `xbyak` even in configurations that don't obviously use them — when a configure
fails on a missing package config, add the dep rather than papering over it with include paths; the vcpkg
binary cache keeps the cost at seconds.

---

## The minimal scaffold

### The proven xmake file-set

The `libxse-commonlibsse-ng-template` is the minimal current-lineage scaffold, and it is wired to the live
authority **by construction**. Its whole authored file-set is: `.gitmodules` (submodule `lib/commonlibsse-ng`
→ alandtse `ng`), `xmake.lua`, `src/main.cpp`, `src/pch.h`. **No vcpkg manifest and no presets** — because
the library's own `xmake.lua` declares every package requirement and the plugin rule generates the export
declaration for you.

```lua
-- xmake.lua (libxse-commonlibsse-ng-template shape)
includes("lib/commonlibsse-ng")            -- runs the library's own xmake.lua; deps auto-declared there
set_project("my-plugin")
set_version("1.0.0")
set_languages("c++23")
add_rules("mode.debug", "mode.releasedbg")

target("my-plugin")
    add_rules("commonlibsse-ng.plugin", {   -- generates the SKSEPluginInfo declaration TU
        name = "my-plugin", author = "You", description = "..." })
    add_files("src/**.cpp")
    add_includedirs("src")
    set_pcxxheader("src/pch.h")             -- headers are PCH-dependent
```

The `commonlibsse-ng.plugin` rule is what makes one DLL load on SE, AE, *and* VR — it generates the
`Query` + `Version` export half automatically, leaving you to write only `SKSEPlugin_Load` (the full
cross-runtime export contract is `plugin-skeleton.md`'s subject; see also `multi-runtime.md`). That is why
the entire authored entry point can be four lines:

```cpp
// src/main.cpp — the WHOLE authored entry point
SKSEPluginLoad(const SKSE::LoadInterface* a_skse) {
    SKSE::Init(a_skse);            // NG: SKSE::Init(const LoadInterface*, bool a_log = true)
    logs::info("Hello World!");
    return true;
}
```

Build with `xmake build`; generate a VS project with `xmake project -k vsxmake`; generate a `clangd`
database with `xmake project -k compile_commands`. Run from a VS Developer Shell.

### The CMake source-submodule scaffold — proven route first, this one marked

**Status, stated precisely:** no *public template* in the wild demonstrates this minimal file-set — every
in-corpus CMake NG-consumer is either dead-registry (3.x) or a frozen overlay. But the shape itself is
**field-proven**: multiple production plugins have been built, shipped, and run in-game from exactly this
pattern — a local NG source checkout (submodule or fixed path), the explicit
`include(…/cmake/CommonLibSSE.cmake)`, `add_commonlibsse_plugin`, and a consumer vcpkg manifest — under
Ninja + MSVC. So teach it with confidence; what field experience adds beyond the sketch:

- **The manifest must feed CommonLib, not just you.** vcpkg manifest mode reads *only* the top-level
  `vcpkg.json`, so it must satisfy CommonLib's own `find_package` set too. In practice that means including
  `rapidcsv` and `xbyak` even in configurations that don't obviously use them — a source checkout's
  `find_package` calls can demand them regardless. The vcpkg binary cache makes the repeat cost seconds.
- **Run configure and build from a VS Developer Shell** (`VsDevCmd.bat`). On a typical machine neither
  `cmake` nor `cl` is on PATH outside it, and Ninja + MSVC needs the dev environment's SDK variables.
- **The first CommonLib source compile is big** (~500 targets, ~15 min cold). Run it with a long timeout or
  in the background, and don't misread a timeout as a failure; incremental rebuilds of your own sources are
  seconds.

```cmake
cmake_minimum_required(VERSION 3.21)
project(MyPlugin VERSION 1.0.0 LANGUAGES CXX)

set(BUILD_TESTS OFF CACHE BOOL "" FORCE)              # NG defaults tests ON → would need Catch2
add_subdirectory(lib/commonlibsse-ng commonlib)

# A plain source add_subdirectory does NOT define add_commonlibsse_plugin (availability trap, below)
if(NOT COMMAND add_commonlibsse_plugin)
    include(lib/commonlibsse-ng/cmake/CommonLibSSE.cmake)
endif()

add_commonlibsse_plugin(${PROJECT_NAME} AUTHOR "You" SOURCES plugin.cpp)   # generates the export TU
target_compile_features(${PROJECT_NAME} PRIVATE cxx_std_23)
target_compile_options(${PROJECT_NAME} PRIVATE /Zc:preprocessor)           # required (above)
target_precompile_headers(${PROJECT_NAME} PRIVATE PCH.h)                   # required (above)
```

Your `vcpkg.json` must carry `spdlog`, `fmt`, `directxtk`, `directxmath`, `rapidcsv` (rapidcsv because VR
defaults on). One pairing hazard: the bare `<SKSE/Impl/PCH.h>`-only PCH is **incompatible** with an
include-free `plugin.cpp` — `Impl/PCH.h` supplies no `RE`/`SKSE` API, so `SKSEPluginLoad`,
`SKSE::GetMessagingInterface`, `RE::ConsoleLog` would be undeclared. Either make the PCH hello-world-shaped
(as shown above) or give `plugin.cpp` explicit includes.

**Do not lift build wiring from the frozen templates.** Two more traps worth naming so you recognize them:
the richly-commented `colorglass-sample-plugin` is the best *teaching* source for C++ lifecycle patterns but
its vcpkg wiring is dead-registry archaeology; and both `skypal-ng` and `skyrimscripting-hello-world` set
`VCPKG_OVERLAY_TRIPLETS` to a `cmake/` dir that does not exist, silently no-op'ing — strip that line if you
clone from them.

---

## `add_commonlibsse_plugin`

On the CMake side this ~200-line helper (in `lib/commonlibsse-ng/cmake/CommonLibSSE.cmake`) generates the
export declaration TU that gives your plugin its SKSE identity. Its signature and defaults:

```cmake
add_commonlibsse_plugin(<target>
    NAME <string>              # default = target name
    AUTHOR <string>            # default = ""
    VERSION <ver>              # default = ${PROJECT_VERSION}; 1–4 numeric dot components ONLY
    MINIMUM_SKSE_VERSION <ver> # default = 0; leave it 0 (both wiki and SKSE advise this)
    USE_ADDRESS_LIBRARY        # the default mode when nothing else is given — the trustworthy path
    SOURCES <path>...
)
```

When you give no compatibility flags, `USE_ADDRESS_LIBRARY` is force-enabled and the helper writes a
`__<Target>Plugin.cpp` containing an `SKSEPluginInfo(...)` block. It links `CommonLibSSE::CommonLibSSE` for
you. It sets **no PCH and no `SKSEPlugin_Load`** — both remain your job (that is why the PCH obligation and
the four-line `main.cpp` above coexist). Note the generated TU's `"...sv` string literals compile only
because your force-included PCH declares `using namespace std::literals;` at global scope — a second reason
the PCH is mandatory.

**Choose exactly one metadata path.** When the build system generates the plugin declaration — this CMake
helper, or the xmake `commonlibsse-ng.plugin` rule — do **not** also hand-write an `SKSEPluginInfo` block or
a manual export triad in source. Duplicate `SKSEPlugin_Version` / `SKSEPlugin_Query` exports produce
confusing load failures that don't name their cause. Generated metadata *or* hand-written, never both.

**Teach only the default Address-Library path.** The helper accepts `USE_SIGNATURE_SCANNING`,
`COMPATIBLE_RUNTIMES`, and `EXCLUDE_FROM_ALL`, but each has a code-read-predicted defect (an unqualified
enum, a semicolon-list splice, a phantom source file) and none is verified working — see *Not yet verified
in-game*. If you need anything other than the default, go fully manual per `plugin-skeleton.md` — hand-write the
`SKSEPluginInfo` block yourself *instead of* letting `add_commonlibsse_plugin` generate one, never both: a
hand-written block on top of the helper's generated one is the duplicate-export collision *Choose exactly
one metadata path* above warns about.

### The availability trap

How you *get* the `add_commonlibsse_plugin` function differs by acquisition path, and this is the number-one
CMake author trap:

| Acquisition path | Helper defined? | Why |
|---|---|---|
| `find_package(CommonLibSSE CONFIG REQUIRED)` via a port | **Yes** | the port's config includes the helper |
| `add_subdirectory(<repo>)` with a **prebuilt** bundle resolved | **Yes** | the helper's `include()` sits inside the prebuilt branch |
| `add_subdirectory(<repo>)` plain **source** build | **No** | control falls past the prebuilt branch; you must `include(…/cmake/CommonLibSSE.cmake)` yourself |
| po3 lineage (any path) | **Never** | po3 lacks the `SKSEPluginInfo` machinery entirely |

This is exactly why the CMake scaffold above guards with `if(NOT COMMAND add_commonlibsse_plugin)` and
includes the helper manually — a plain source `add_subdirectory` does not define it. And it is why the
**xmake path is structurally more robust**: the xmake `commonlibsse-ng.plugin` rule is defined
unconditionally, reachable by any consumer regardless of source-vs-prebuilt. If you keep hitting "unknown
command `add_commonlibsse_plugin`," this table is the reason.

---

## Per-runtime presets

The current lineage produces **one DLL for SE+AE+VR** — that is the design, and it means you generally do
*not* build separate per-runtime binaries. What varies per runtime is the deploy destination and the
Address-Library data file the *user's game* must carry (below), not usually the build.

A CMake preset for the plugin should use the **`x64-windows-static-md`** triplet — dynamic CRT is the
forward-compatible default, because the NG prebuilt bundles are `/MD` and the auto-fetch refuses static-CRT
(`/MT`) consumers. A representative flag set: `/permissive- /Zc:preprocessor /EHsc /MP /W4
-DWIN32_LEAN_AND_MEAN -DNOMINMAX -DUNICODE -D_UNICODE`. The full three-conditional-pattern story for code
that must branch on SE vs AE vs VR — and the footgun catalog — lives in `multi-runtime.md`; here the point
is only that one preset produces the one cross-runtime DLL.

---

## Deploy into an MO2 mod

A deployed plugin is **a mod folder whose root is Data-shaped**, with the DLL at `SKSE/Plugins/<name>.dll` —
because SKSE's loader scans exactly `<game>/Data/SKSE/Plugins/*.dll`. Under MO2 the mod folder is projected
into `Data` by the virtual file system, so one mod folder serves every runtime.

```
MyPlugin/                      <- one MO2 mod (root == Data)
└── SKSE/Plugins/
    ├── MyPlugin.dll
    ├── MyPlugin.pdb           <- symbols; makes crash logs name your functions
    └── MyPlugin.ini/.toml     <- optional plugin config
```

**Deploy hooks are not standardized** — every template ships a POST_BUILD copy, but the env-var name that
points it at your MO2 `mods` dir differs by ecosystem: `SKYRIM_MODS_FOLDER` (hello-world),
`XSE_TES5_MODS_PATH` (the xmake plugin rule — and it copies **automatically** after every changed build),
`CompiledPluginsPath` (OAR), a semicolon-separated `SkyrimPluginTargets` list (colorglass), a
runtime-specific `SkyrimAEPath`/`SkyrimVRPath` (SPID). When you clone an unfamiliar repo, **grep its
CMake/xmake for `SKSE/Plugins`** to find its hook, and check whether the copy defaults ON (OAR hard-fails a
bare configure if its path is unset) or OFF (SPID silently skips). Set your chosen var once to the MO2
`mods` directory and the build lands the DLL where MO2 can enable it.

Two deployment rules that bite in the field:

- **Ship a release-CRT build.** Deploy RelWithDebInfo (optimized + PDB), never the Debug configuration — a
  Debug build links the debug CRT (`MSVCP140D.dll` and friends), which exists only on developer machines,
  so on any other machine the DLL dies at `LoadLibrary` with the missing-dependency signature
  (`load-failures.md` §5). Keep the PDB beside the DLL either way; it makes crash logs name your functions.
- **Refresh MO2 after external writes.** Files a POST_BUILD copy drops into `mods\` are invisible to an
  already-open MO2 until it refreshes — verify against the refreshed VFS, not the bare folder.

### The one runtime data dependency: Address Library

Independent of your build, the **user's game** must carry the Address-Library database for their exact EXE
version, or the plugin dies at load. NG resolves it from `Data/SKSE/Plugins/`:

| Runtime | File NG loads | Source mod |
|---|---|---|
| SE 1.5.x | `version-<m>-<n>-<b>-0.bin` | Nexus **32444** |
| AE 1.6.x | `versionlib-<m>-<n>-<b>-0.bin` | Nexus **32444** |
| VR 1.4.15 | `version-<m>-<n>-<b>-0.csv` | **separate** Nexus **58101** |

The failure is loud, not silent — NG prints "Failed to locate an appropriate address library…" (and on VR
"Required VR Address Library file … does not exist"), and the AE loader itself disables mismatched plugins
with "address library needs to be updated." That AE loader address-library pre-check is **AE-only** — VR's
loader has no equivalent line, so don't tell a VR user to look for it.

---

## The loop

1. **Set the deploy env var once** (`SKYRIM_MODS_FOLDER` / `XSE_TES5_MODS_PATH` / whichever your scaffold
   uses) to the MO2 `mods` dir.
2. **Build** — `cmake --build --preset <x>` or `xmake build` (xmake auto-installs on every changed build).
   The DLL and PDB land in `mods/<Name>/SKSE/Plugins/`.
3. **Enable the mod once in MO2** and launch SKSE **through MO2**.
4. **Verify load** — read the SKSE runtime log for a success line, then the plugin's own log for its init
   output.
5. **Distribute** — `xmake package`, a CPack ZIP, or a FOMOD; all mod-manager-installable, Data-shaped.

**Reading the log — two distinct families, do not conflate them:**

| Family | AE filename | VR filename | Location |
|---|---|---|---|
| SKSE runtime log | `skse64.log` | `sksevr.log` | `<Documents>\My Games\<folder>\SKSE\` |
| Your plugin's own log | `<PluginName>.log` | `<PluginName>.log` | same folder |

`<folder>` = `Skyrim Special Edition` / `… GOG` / `… EPIC` on flat Skyrim, or **`Skyrim VR`** on VR. In
`skse64.log`, key a "did it load?" check on the **`loaded correctly`** / trailing `(handle N)` success form,
not on the bare `plugin ...` prefix (the error line shares that prefix). Diagnosable failure strings you may
see: `couldn't load plugin` (usually a missing DLL dependency), `does not appear to be an SKSE plugin`
(wrong/missing exports — see `plugin-skeleton.md` and `load-failures.md`), `reported as incompatible during
load`, `disabled, address library needs to be updated`. SKSE also pops a startup message box listing failed
plugins — a load failure is never silent. When a DLL refuses to load, `load-failures.md` is the triage path.

**Per-runtime log divergence a VR verifier must know:** the VR loader emits only `plugin directory`,
`checking plugin`, and the load line — it does **not** emit `scanning plugin directory` and has **no**
address-library line. Telling a VR user to grep for either sends them chasing lines their log never
contains.

---

## Rebuilding an existing plugin from source

A recurring job that is neither authoring nor porting: rebuild an open-source plugin *as installed* to
patch one defect (a guard, a null-check, a re-entrancy fix). The goal is ABI fidelity to the shipped DLL —
field rules that keep the rebuild faithful:

- **Build the exact installed tag/commit, not HEAD.** Match by release tag and DLL file date — version
  resources and mod-manager metadata routinely lag the source tag, and HEAD may have drifted past the
  installed ABI (exported interfaces, co-save format).
- **Keep the project's own CommonLib acquisition and lineage** — its registry pin, submodule URL + branch,
  or vendored checkout — rather than re-pointing it at your preferred one; swapping lineages changes idioms
  and layouts (`multi-runtime.md`). Remember a `--depth 1` clone leaves submodules empty:
  `git submodule update --init --recursive` first.
- **Keep the original `ENABLE_SKYRIM_*` runtime set.** Turning a runtime off to "simplify" the rebuild can
  break the compile outright (source that references another runtime's `RE::` types unconditionally) or
  change struct layouts relative to the shipped DLL.
- **Expect author-machine residue and strip it locally:** hardcoded output paths, deploy env vars,
  overlay-triplet dirs that don't exist in the repo (the no-op trap above), and `/WX` that a newer MSVC's
  new warnings turn fatal. None of it is part of the plugin.
- **Build only the DLL target** when the repo's ALL target drags in packaging, Papyrus-compile, or zip
  steps wired to the author's machine.

---

## ClibDT — the beginner automation

**ClibDT** (Nexus **154240**, currently v5.2.2) automates the entire xmake / alandtse-NG setup: it installs
the toolchain (VS Build Tools + xmake + Git), permanently sets the `XSE_*` env vars (dev root, game path,
mods output path), and generates projects whose `xmake.lua` uses exactly the
`includes("lib/commonlibsse-ng")` + `add_rules("commonlibsse-ng.plugin")` rule described above — so its env
vars drive the same auto-install-on-build loop. It is the on-ramp for someone who does not want to hand-wire
the environment; the CMake/vcpkg route is the hand-wired alternative, and both end at the same Data-shaped
artifact and the same `XSE_TES5_*`-style deploy conventions. Recommend ClibDT to a first-time plugin author;
reach for the hand-wired scaffold when you need to understand or customize the wiring.

---

## Not yet verified in-game

Several claims here are read from the library's source and docs but have **not** been confirmed by a real
build-and-load on a live machine. Keep them honest — present them as expected behavior to burn in, never as
proven fact:

- **The exact minimal CMake file-set above has not been compiled verbatim as a unit** — but the pattern it
  encodes (source checkout + explicit `include(…/cmake/CommonLibSSE.cmake)` + `add_commonlibsse_plugin` +
  consumer-manifest deps) **is field-proven by multiple shipped production plugins**; the caveats that
  matter in practice are the manifest-feeds-CommonLib rule, the VS-dev-shell requirement, and the cold-build
  time, all noted inline above. What remains genuinely unproven is only the word-for-word sketch.
- **The non-default `add_commonlibsse_plugin` options** (`USE_SIGNATURE_SCANNING`, multi-runtime
  `COMPATIBLE_RUNTIMES`, `EXCLUDE_FROM_ALL`) have code-read-predicted defects and are **not** verified
  working. Do not present them as usable options; hand-write `SKSEPluginInfo` instead.
- **The cross-runtime export auto-emit** — that the build generates `{Query, Version}` and you supply
  `{Load}`, producing one DLL that loads on SE, AE, and VR — is proven from the codegen source but wants a
  `dumpbin /EXPORTS` + a live SKSEVR load to confirm end to end.
- **The prebuilt auto-fetch** (download / SHA256 / fallback) and **MO2 VFS mechanics** (refresh while MO2 is
  open, DLL file-lock while the game runs) are proven as code-and-docs intent only.
- **The exact VR log line-set** (the emitted `sksevr.log` shape, its absences, its steam-loader filename) is
  a runtime claim awaiting a real VR log.
- **The libxse lineage's runtime coverage** (does its non-NG fork do AE/VR at all?) is unknown; treat any
  libxse `commonlibsse-template` multi-runtime story as unproven.

# HOUSECARL_NAMING — houseCARL naming conventions

**Status:** trimmed 2026-05-29 to the architecture-agnostic core, from the locked 2026-04-30 standard (Aaron). The original's architecture-specific sections — Python module naming, the MO2 plugin slug, and installer artifacts — are **deferred**: they return, authored fresh, when those surfaces actually exist (e.g. if the open MO2-integration question adds a Python shim). The tool-prefix rule and the rebrand-resilience principle carry forward unchanged.

This is the authoritative naming standard for houseCARL. Every rule here is meant to be enforceable via linter, hook, or review (hooks don't exist yet — they emerge from need).

---

## 1. Why these rules exist

**Core lesson from the predecessor builds:** the brand name was scattered across tool prefixes, folder names, installer artifacts, config strings, and the repo name — so a single rebrand touched every layer. These rules are designed so a future rebrand changes exactly one constant and one directory name (§6). Brand belongs on the surface (tool names, binary name, display strings); never in the interior (namespaces, class names, file names).

---

## 2. Tool prefix

**Rule:** all MCP tool names are prefixed `housecarl_`. **Locked** — the prefix carries forward from the prior build for brand continuity, even though the project is now houseCARL.

**Format:** `housecarl_` + `snake_case` verb-noun or noun phrase.

| Good | Bad | Reason |
|------|-----|--------|
| `housecarl_query_records` | `housecarl_QueryRecords` | CamelCase in tool name |
| `housecarl_record_detail` | `housecarl-record-detail` | kebab in tool name |
| `housecarl_conflict_summary` | `conflictSummary` | no prefix |

**Check:** tool-name string literals in MCP registration match `^housecarl_[a-z][a-z0-9_]*$` (hook-enforceable once hooks exist); any non-matching name is a hard failure.

**Rebrand resilience:** the prefix is a string literal in each tool registration — the *only* place the brand appears as a prefix. It does NOT appear in C# namespaces, class names, or file names (those are purpose-based — §6).

---

## 3. C# project naming

houseCARL is a single C# process (the MCP server). The pattern:

- **AssemblyName / project dir:** `kebab-case` — filesystem artifacts, not C# identifiers; idiomatic for .NET.
- **RootNamespace:** `PascalCase`, the PascalCase transform of the kebab project name.
- **Class + file names:** `PascalCase.cs` (file name = class name).
- **Helper subdirectories:** `PascalCase` (= namespace segment).

**Examples** — illustrative; the actual MCP-server project name is set at scaffolding. The brand MAY appear in the binary/project name (a user-visible artifact), but nowhere in the interior:

| AssemblyName / dir | RootNamespace |
|---|---|
| `housecarl-mcp` | `HousecarlMcp` |
| `some-component` | `SomeComponent` |

### 3.1 Test projects

- **xUnit/NUnit:** `<component>-tests/` dir + `<component>-tests.csproj`; RootNamespace `<PascalCase>Tests`; test classes `<TestedClass>Tests.cs`.
- **Executable harnesses** (smoke runners, probes — not xUnit): purpose-based kebab names with NO `-tests` suffix (e.g. `coverage-smoke/`, `race-probe/`), `Program.cs` entry point. The distinction matters: a harness runs a fixed scenario; a `-tests` project implies xUnit discovery.

---

## 4. Repo and directory naming

### 4.1 Repo name
`houseCARL` — the project's proper brand name. No technology suffix; no predecessor or dependency brand embedded (the old `Claude_MO2` mistake of baking in both an AI brand and a dependency brand is not repeated).

### 4.2 Top-level directories
`kebab-case` for all top-level dirs — filesystem artifacts, not code identifiers. Examples: `dev/`, `standards/`, `tools/`, `tests/`, `.claude/`.

### 4.3 File names (general)

| File type | Pattern | Example |
|---|---|---|
| C# source | `PascalCase.cs` | `RecordReader.cs` |
| C# project | `kebab-case.csproj` | `housecarl-mcp.csproj` |
| Markdown — living docs | `SCREAMING_SNAKE_CASE.md` | `README.md`, `CLAUDE.md` |
| Markdown — archive | `kebab-case.md` / dated | `rebuild-plan-v1.md` |
| Config files | lowercase / tool convention | `.gitignore` |
| Shell / PS scripts | `kebab-case.ps1` / `.sh` | `build-release.ps1` |

---

## 5. Skill folder naming

**Rule:** `kebab-case` for skill folders — the Claude Code de-facto standard. Skill folder names are filesystem paths, not identifiers, and are purpose-based (never brand-prefixed).

**Pattern:** `.claude/skills/<lowercase-hyphenated>/SKILL.md`

| Good | Bad | Reason |
|------|-----|--------|
| `crash-diagnostics/` | `crash_diagnostics/` | underscore (inconsistent with CC convention) |
| `mod-dissection/` | `ModDissection/` | PascalCase |
| `esp-patching/` | `esp-patching-skill/` | redundant `-skill` suffix |

**Check:** every dir under `.claude/skills/` matches `^[a-z][a-z0-9-]*$` and contains exactly one `SKILL.md` (hook-enforceable once hooks exist).

---

## 6. Future-rebrand resilience

The Claude_MO2 → Housecarl → houseCARL transitions showed how painful an embedded brand is. These rules make a future rebrand a one-change job, not an audit.

### 6.1 Single source of truth
The product name appears as a string literal in exactly one place: a single constant in the MCP server's config. Everything else references that constant. No bare brand strings scattered across source.

### 6.2 Brand-free internals
C# namespaces, class names, and file names are purpose-based, not brand-based: `RecordReader`, not `HousecarlRecordReader`. The one allowed exception is the MCP server's project/binary name and its namespace (a user-visible artifact).

### 6.3 Rebrand touch-points
On a future rebrand, exactly these change: (1) the brand constant in the server config; (2) the `housecarl_` tool prefix in registrations; (3) the MCP-server project/binary name; (4) the repo name + README. Internal code, skill folder names, and build scripts do not.

### 6.4 No version numbers in names
No version in any directory, file, or identifier — versioning lives in config constants and release tags. (`CHANGELOG.md`, not `CHANGELOG_v1.md`; skills don't version-suffix their folders.)

---

## 7. Consolidated rules reference

| Context | Pattern | Example |
|---|---|---|
| MCP tool names | `housecarl_snake_case` | `housecarl_query_records` |
| C# project dir / AssemblyName | `kebab-case` | `housecarl-mcp` |
| C# RootNamespace | `PascalCase` | `HousecarlMcp` |
| C# class + file | `PascalCase.cs` | `RecordReader.cs` |
| C# test projects | `<component>-tests/` | `housecarl-mcp-tests/` |
| Skill folders | `kebab-case/` | `crash-diagnostics/` |
| Top-level dirs | `kebab-case/` | `dev/`, `standards/` |
| Living docs | `SCREAMING_SNAKE_CASE.md` | `CLAUDE.md` |
| Archive docs | `kebab-case.md` / dated | `rebuild-plan-v1.md` |
| Brand strings | one constant, never bare literals | server config constant |

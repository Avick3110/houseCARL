# Naming

| What | Pattern | Example |
|---|---|---|
| MCP tool | `housecarl_<snake_case>` | `housecarl_records` |
| C# project directory and assembly | `kebab-case` | `housecarl-mcp` |
| C# root namespace | `PascalCase` | `HousecarlMcp` |
| C# class and file | `PascalCase.cs`, file name = class name | `RecordReader.cs` |
| Test project | `<component>-tests/` | `housecarl-mcp-tests/` |
| Test class | `<Subject>Tests.cs` | `RecordsScanLaneTests.cs` |
| Skill folder | `kebab-case/` | `facegen-diagnostics/` |
| Top-level directory | `kebab-case/` | `standards/` |
| Repo-level doc | `UPPERCASE.md` | `CLAUDE.md`, `TESTING.md` |

Tool names are compile-time constants in `src/housecarl-core/ToolNames.cs`; nothing else spells one out. The `housecarl_` prefix carries over from the 1.x build and stays.

The brand string "houseCARL" appears once in code, in the server's name. Namespaces, classes, and files are named for what they do, not for the brand, so a rename touches one constant.

No version numbers in names.

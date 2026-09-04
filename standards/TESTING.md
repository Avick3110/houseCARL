# Testing

Tests live in `src/housecarl-mcp-tests` (xUnit). CI builds the solution, runs this project, and runs the older probe harness through the generator's `ci-all` command.

## Two kinds of test

- **Engine tests** call a tool's C# entry point (for example `RecordsTools.Records(...)`) against a synthetic MO2 instance and assert on the response.
- **Wire tests** drive the built `housecarl-mcp.exe` over stdio through `ServerFixture`, so they see exactly what a caller sees: the published tool list, the schemas, the bound parameters.

Most tests are engine tests. Write a wire test when the thing under test is the publication itself: a tool's name, its schema, or whether a call reaches the tool body.

## Worlds

A world (`RecordsWorld`, `ScriptsWorld`, `DialogueWorld`, `CheckErrorsWorld`, `ArtifactWorld`, `BulkRecordsWorld`) builds a temporary MO2 instance with real plugins written by Mutagen: a master, overrides at different load positions, a disabled patch, scripts. One instance is shared through an xUnit collection fixture or class fixture. A test that mutates the world (rewrites a plugin, touches an mtime) builds its own instance so it cannot poison the shared one.

To test a new behaviour, first look for a world that already has the records you need. Add to one before creating another.

## Writing a test

1. Arrange with the world: pick the record, the plugin, the state.
2. Act by calling the tool once.
3. Assert on what the caller would look for: the record that should appear, the value it should carry, the refusal it should get. Most test bases have `Served(response, ...)` and `Refused(response, ...)` helpers for the common cases.
4. Tag the class with a tier trait: `[Trait("tier", "unit")]` when it needs no world, `"integration"` when it drives a world, `"stdio"` when it goes through `ServerFixture`.

Assert on the specific thing, not the whole text. A refusal test names the one word that carries the fix (the parameter, the rule) and nothing more, so a rewording of the sentence does not break it.

A test must fail before the fix and pass after. If it cannot fail, it is not a test; delete it. A wrong test should be obvious to someone reading it cold: one behaviour, one name that says it, no setup the reader cannot follow.

## What not to write

- No tests about tests: no guards over test files, no baseline counts, no sweeps that check the suite's own shape. The few that still exist are being deleted; do not add to them. If you doubt the suite, run Stryker.NET once, fix what it shows, and move on.
- No test that needs the real game. Anything that needs a real load order runs locally; the PR says what was run and what it showed.
- No duplicate of a probe. The probes in `src/housecarl-generator` are the old harness and still cover real behaviour. Leave them alone until you change what one covers; then move that coverage here and delete the probe.

## Running

```
dotnet build housecarl.sln -c Release
dotnet test src/housecarl-mcp-tests -c Release --no-build --filter "tier!=bridge"
dotnet src/housecarl-generator/bin/Release/net9.0/housecarl-generator.dll ci-all
```

This block runs `ci-all` explicitly, so the filter leaves out the one `bridge`-tier test, which exists only to run `ci-all` from an unfiltered `dotnet test`. CI does the same.

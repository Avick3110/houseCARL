---
updated: 2026-09-22
covers: [src/housecarl-mcp/NexusClient.cs, src/housecarl-mcp/NexusTools.cs]
---
# Nexus

## What it is
Read-only access to the Nexus Mods public v2 GraphQL API: catalog search, one mod's detail and files, a
file-level update check, MD5 identify, and a raw-query backstop. It is keyless — the v2 read surface is
public and anonymous — and it never downloads, installs or endorses; that stays the mod manager's nxm handoff.

## Contracts
- It is the server's only outbound network dependency, and it lives in `housecarl-mcp`; `housecarl-core` is network-free.
- The tools never touch the MO2 instance, so they run with no instance set and no load order configured.
- Every failure — no connection, timeout, HTTP error, rate limit, malformed body, GraphQL error — is returned as a plain message and never thrown, so the local load-order tools keep working offline.
- Mutation and subscription documents are refused before the request, matching the keyword only at document start or after a prior operation's closing brace, so a field merely containing the word is not a false refusal.
- User input rides the GraphQL variable channel and is never concatenated into the query text; only the game id, integer mod ids and the page count are inlined.
- Search, mod lookup and the update check are scoped to one game per call: Skyrim Special Edition unless `game=` names another, as a Nexus domain name or a numeric game id.
- The four games of #835 — `skyrimspecialedition` 1704, `baldursgate3` 3474, `cyberpunk2077` 3333, `starfield` 4187 — map with no network call; any other value is resolved once through `game(domainName:)` or `game(id:)` and cached for the process, because a game's id and domain never change.
- A game Nexus does not know is refused in one sentence naming what was asked, never quietly searched as Skyrim SE.
- That refusal is decided on the graph's own `GAME_NOT_FOUND` error code, never on the message text, which also carries HTTP status words: an HTTP 404 reads `Not Found` and is a failed request, not a missing game. Every failure that never reached the graph carries no code and is passed through as itself.
- A game resolved through the graph is named by the name it gave; a game with no name reads as its domain.
- Rendered prose names the game rather than its URL slug, and the default game's wording — including the not-found group's `an LE/other-game mod` hint, which is Skyrim's most common not-found — is what it was before `game=` existed.
- A mod URL's domain segment names its game, so any game's URL works as pasted; a `game=` that names a different game than the URL is refused rather than guessed.
- Every rendered mod page URL carries the game that was asked for, and the update check's not-found group names it, so no output claims Skyrim SE for another game's call.
- `housecarl_nexus_identify` and `housecarl_update_status` stay Skyrim-SE-bound by design: they read the MO2 instance's own cache, and a match on another game's file is flagged rather than scoped away.
- The raw-query backstop renders exactly what the graph returned — `+`, `&` and non-ASCII literal, not escaped — bounded with an explicit truncation marker.
- Update currency is decided per installed file id, never by comparing a mod's version header to the page's newest MAIN file: one Nexus page hosts many independently-versioned files.
- `OLD_VERSION` and `ARCHIVED` are the retirement buckets and `REMOVED` and `DELETED` the withdrawal buckets; every other category counts as live, and the category string is always carried into the output.
- A withdrawn file outranks a retired one in a mod's verdict, and the newest same-name live file is named as a lead rather than as the replacement.
- A mod's verdict is Current when every installed file is live, FileRemoved when any was withdrawn, and Outdated when any was retired and none withdrawn.
- An installed file id that matches nothing on the page is FileGone, and neither it nor NotFound nor Error is ever folded into Current.
- With no installed file id the check degrades rather than falling back to a mod-level version compare: an installed version given is NoFileId, a bare mod id is LatestOnly.
- A mod absent from the `mods()` search but whose direct `modFiles` lookup returns files — the manager-only (nxm) class Nexus hides from search — is resolved from those files rather than stamped NotFound.
- That gate leans on upstream behaviour the code cannot check: a genuinely absent mod (wrong id, LE-only, hidden) returns an empty `modFiles` list rather than an error or another game's files.
- `InstalledFileCurrency` carries Name and Category for every verdict except `Missing`, whose file is not on the page to resolve; Version is null whenever the page file has no version string.
- `LiveMainCount` above one means a multi-main page, which a version compare cannot safely resolve; one is the labelled version-compare case.
- Requests are grouped by mod id with file ids merged, because one Nexus page split across several MO2 mod folders shares a mod id.
- A batched request passes `count` at least the chunk size, because the `mods` field otherwise returns a 20-item page and the overflow loses its name and header version.
- A chunk that fails marks only its own mods Error; the call fails outright only when every chunk failed.
- An MD5 match carries its game id, so a match on a non-Skyrim-SE file is flagged rather than mis-attributed; unmatched hashes are absent from the response and mapped back to an explicit no-match.
- Rendered sections are bounded as a per-section delta with an explicit truncation marker, and a clamp never splits a surrogate pair.

## Pinned by
- `NexusFileCheckProbe` (ci probe `nexus-file-check-guard`) — the per-file currency verdicts, the withdrawn-over-retired order, the NoFileId and LatestOnly degrade, the nxm-only fall-through, the `LiveMainCount` sentence (arms E and H), the id#fileid parse, and the mod id grouping.
- `NexusGraphqlProbe` (`nexus-graphql-guard`) — the mutation and subscription refusal with no false refusal, and the literal, bounded raw-query rendering sentence.
- `RenderClampProbe` (`render-clamp-guard`) — the surrogate-safe clamp in the last sentence above.
- `NexusGameProbe` (`nexus-game-guard`) — the `game=` domain and id mapping including the Skyrim SE default, the unknown value that does not map, the mod URL parse, the rendered page URL and the not-found label (the default's wording unchanged), the requested game's id on the wire, and the refusal that separates `GAME_NOT_FOUND` from an HTTP 404, an HTTP 500, another GraphQL error and an unreachable endpoint.

## Where
`src/housecarl-mcp/NexusClient.cs` holds the HTTP and GraphQL layer, the currency computation, and the result
records; `src/housecarl-mcp/NexusTools.cs` holds the five tools and their text rendering. Tools:
`housecarl_nexus_search`, `housecarl_nexus_mod`, `housecarl_nexus_graphql`, `housecarl_nexus_check_updates`,
`housecarl_nexus_identify`. The check list is built cheaply with `housecarl_update_status`, which reads MO2's
own local cache offline.

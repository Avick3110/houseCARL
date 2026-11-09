# ADR 0006 — Off Windows, serve the record reads and refuse the rest per call

**Class:** ARCHIVE (ADR). Immutable once merged; superseded by a later ADR, never edited.
**Date:** 2026-11-09 · **Status:** proposed · **Issue:** #1256

## Context

Since #1257 the server refuses to start on any OS but Windows, because on Linux the loose-file layer never descends
below a mod folder's top level and answers came back silently wrong (#1256). That refusal also took away the part
that works.

Measured on Linux (.NET 10 SDK container, `main` at `0f47159`) against a records-only copy of a 3,751-plugin MO2
instance, with the start refusal lifted, and compared with Windows on the same instance: plugins resolved, enabled
and disabled mods, mapped child records, three records' winners, override depths and names, and a 68-match
`where=` scan were identical. Loose SKSE DLLs and a loose mesh came back absent, and the profile's lower-case
`skyrim.ini` was not found, so the base-game archives were skipped.

## Decision

The server starts on any OS. Off Windows a call-tool filter serves the record reads over the active plugins and
refuses everything else, per call, in one sentence that names #1256. The asset, SKSE, NIF, BSA, SkyPatcher, Papyrus,
check and write tools are refused, and so is a `records` pole that can read a file outside the active order.

## Consequences

- A Linux caller gets the reads that are right there and a named refusal for the rest; nothing is silently degraded.
- The published tool list is unchanged on every OS. A caller learns a tool is unavailable from its refusal.
- Each later Linux fix widens the served set in `SupportedPlatform` and adds its tests. Full support removes the gate.
- A localized plugin whose string tables come from an archive reads its name as `unreadable` on Linux, because
  Mutagen looks for the plugin listings path there. That is loud, not wrong, and is left for a later change.

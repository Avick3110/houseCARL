---
updated: 2026-11-09
covers: [src/housecarl-mcp/SupportedPlatform.cs, src/housecarl-mcp/Program.cs, src/housecarl-core/Mo2LoadOrder.cs]
---
# Platform

## What it is
houseCARL starts on any OS. Off Windows it serves the record reads over the active plugins and refuses every
other call in one sentence that names #1256, because the loose-file and archive layers resolve paths unsoundly there.
The gate is a call-tool filter, so the published tool list is the same on every OS.

## Contracts
- On Windows the gate refuses nothing.
- Off Windows the served tools are `records`, `load_order_status`, `set_mo2_instance` and the four Nexus lookups
  that need no local file; every other registered tool is refused before its body runs.
- Off Windows a `records` call whose `source=` or `versus=` names anything but the winner or the provider below the
  subject is refused naming that pole, because a named plugin, a `{file, mod}` pair and the SkyPatcher overlay can
  read files outside the active order.
- Pole names are matched in any case and with or without underscores, so the gate does not depend on running after
  the argument shim.
- A profile file another process holds is a transient on both OSes: Win32 errors 32 and 33 on Windows, the raw
  EWOULDBLOCK errno (11) .NET raises for the same refusal on Linux.

## Pinned by
- `SupportedPlatformTests` — the served set, the served and refused poles, one sentence naming the issue.
- `SupportedPlatformProcessTests.ANonWindowsServerServesTheRecordReadsAndRefusesTheRest` — the gate on the wire.
- `HeldProfileFileTests` — the held-file errors per OS.

## Where
`SupportedPlatform.Refusal` and `SupportedPlatform.OffWindowsGate`, registered in `Program.cs` beside
`ToolCallShim.LenientArguments`; `Mo2LoadOrder.IsHeld`.

## Related
- `docs/decisions/0006-off-windows-serve-the-record-reads.md` — why per call and not at start.
- `tool-call-argument-shim.md` — the other call-tool filter.

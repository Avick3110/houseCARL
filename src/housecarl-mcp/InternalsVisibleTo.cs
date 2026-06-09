using System.Runtime.CompilerServices;

// The proof harness (housecarl-generator) drives the SERVICE-layer scan logic (LoadOrderService.CrossQuery
// via the ForGuard seam) in its CI regression guards — the first CI coverage of the mcp layer (the follow-up
// logged at PR #23: the winner-vs-source guard could only reach the core's RecordsIn, not the service loop).
// Declaring the harness a friend keeps the guard on the REAL product path without widening the public surface.
[assembly: InternalsVisibleTo("housecarl-generator")]

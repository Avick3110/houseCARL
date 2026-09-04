using System.Runtime.CompilerServices;

// The generator's CI checks drive the service-layer scan logic (LoadOrderService.CrossQuery via the ForGuard
// seam), so they need friend access rather than a widened public surface.
[assembly: InternalsVisibleTo("housecarl-generator")]

// The xUnit tests drive the same service/tool seams.
[assembly: InternalsVisibleTo("housecarl-mcp-tests")]

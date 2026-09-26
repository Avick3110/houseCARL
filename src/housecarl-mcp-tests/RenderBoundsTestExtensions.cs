using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Moves one service's render bounds for a single call and puts them back whatever happens, so a test lowers
/// only its own world's bound — building 300,000 records to reach the real one is not a test.</summary>
internal static class RenderBoundsTestExtensions
{
    internal static string WithBounds(this LoadOrderService svc, Func<RenderBounds, RenderBounds> change, Func<string> call)
    {
        var prior = svc.Bounds;
        svc.Bounds = change(prior);
        try { return call(); }
        finally { svc.Bounds = prior; }
    }
}

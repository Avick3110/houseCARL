using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The group reads one call adds, per (plugin, group), after checking its answer.</summary>
static class GroupReadDelta
{
    public static Dictionary<(string, string), int> Of(LoadOrderService svc, Func<string> call, params string[] expect)
        => Reads(svc, () =>
        {
            var r = call();
            Assert.False(r.StartsWith("error", StringComparison.Ordinal), r);
            foreach (var e in expect) Assert.True(r.Contains(e), r);
        });

    /// <summary>The group reads <paramref name="call"/> adds, per (plugin, group).</summary>
    public static Dictionary<(string, string), int> Reads(LoadOrderService svc, Action call)
    {
        var before = new Dictionary<(string Plugin, string Group), int>(svc.Counters.GroupReads);
        call();
        return svc.Counters.GroupReads.Where(kv => kv.Value != before.GetValueOrDefault(kv.Key))
                                      .ToDictionary(kv => (kv.Key.Plugin, kv.Key.Group), kv => kv.Value - before.GetValueOrDefault(kv.Key));
    }
}

using System.Reflection;
using ModelContextProtocol.Server;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>The SDK's OWN tool-discovery predicate, in one home: a tool is REGISTERED when
/// <c>[McpServerToolType]</c> is on the declaring TYPE and <c>[McpServerTool]</c> is on the METHOD — the pair
/// <c>WithToolsFromAssembly()</c> scans for, and the only derivation any probe may call a tool "registered" by.
///
/// <para>The type half is load-bearing. <c>CheckTools</c> carries the method attribute and no type attribute, so
/// <c>housecarl_check</c> is declared and has never been registered (#470) — a predicate reading the method alone
/// reports it live. Two probes wrote that predicate separately and disagreed about it in one PR: the description
/// vocabulary guard read both attributes, the in-place guard's arm-H sweep read the method alone, so the sweep was
/// a superset of the surface it claimed (Aaron's gate review on PR #474, finding 4). Nothing was wrongly swept when
/// that was found — <c>housecarl_check</c> declares no <c>in_place</c> — so the fix removes a latent hole, not a
/// live one. One home means the two cannot disagree again.</para></summary>
internal static class RegisteredTools
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>The shipped tool surface — the assembly the SDK scans.</summary>
    static readonly Assembly Surface = typeof(WriteTools).Assembly;

    /// <summary>Every registered (tool name, declaring method) pair, ordinal-sorted by name. Callers that need to
    /// reflect over a tool's PARAMETERS (arm H's in_place sweep) filter this; callers that need only the names call
    /// <see cref="Names"/>.</summary>
    internal static List<(string Tool, MethodInfo Method)> All()
    {
        var found = new List<(string Tool, MethodInfo Method)>();
        foreach (var t in Surface.GetTypes())
        {
            if (t.GetCustomAttribute<McpServerToolTypeAttribute>(inherit: false) is null) continue;
            foreach (var m in t.GetMethods(Flags))
                if (m.GetCustomAttribute<McpServerToolAttribute>(inherit: false)?.Name is { Length: > 0 } name)
                    found.Add((name, m));
        }
        found.Sort((x, y) => string.CompareOrdinal(x.Tool, y.Tool));
        return found;
    }

    /// <summary>The registered names alone.</summary>
    internal static HashSet<string> Names() => new(All().Select(p => p.Tool), StringComparer.Ordinal);
}

using System.Reflection;
using ModelContextProtocol.Server;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>The SDK's OWN tool-discovery predicate, in one home: a tool is REGISTERED when
/// <c>[McpServerToolType]</c> is on the declaring TYPE and <c>[McpServerTool]</c> is on the METHOD — the pair
/// <c>WithToolsFromAssembly()</c> scans for, and the only derivation any probe may call a tool "registered" by.
/// Both halves are load-bearing: a predicate reading the method alone reports a type missing its attribute as
/// live, and over-reports the surface to every sweep built on it. Every consumer goes through here so two of
/// them cannot answer the question differently.</summary>
internal static class RegisteredTools
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                             | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>The shipped tool surface — the assembly the SDK scans, read off the one home for that fact
    /// rather than by naming a type expected to live there.</summary>
    static readonly Assembly Surface = ToolSurface.Assembly;

    /// <summary>Every registered (tool name, declaring method) pair, ordinal-sorted by name. Callers that need to
    /// reflect over a tool's PARAMETERS filter this; callers that need only the names call
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

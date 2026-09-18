using System.Reflection;

namespace HousecarlMcp;

/// <summary>The one home for which assembly is the tool surface; Program.cs passes it to <c>WithToolsFromAssembly</c>.</summary>
public static class ToolSurface
{
    /// <summary>The assembly the server registers tools from, and the only one that may declare them. Every census
    /// reflects THIS property rather than naming an assembly by a type it expects to find there, so a tool declared
    /// elsewhere is a loud failure instead of a silent absence from <c>tools/list</c>.</summary>
    public static Assembly Assembly { get; } = typeof(ToolSurface).Assembly;
}

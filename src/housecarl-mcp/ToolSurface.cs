using System.Reflection;

namespace HousecarlMcp;

/// <summary>
/// The one home for "which assembly is the tool surface".
///
/// <para>Program.cs passes this to <c>WithToolsFromAssembly</c>, so it is the assembly the server actually
/// registers tools from, and every census reflects the same property rather than naming an assembly by
/// picking a type it expects to find there. A tool declared in any other assembly is then a loud failure
/// instead of a silent absence from <c>tools/list</c>.</para>
/// </summary>
public static class ToolSurface
{
    /// <summary>The assembly the server registers tools from, and the only one that may declare them.</summary>
    public static Assembly Assembly { get; } = typeof(ToolSurface).Assembly;
}

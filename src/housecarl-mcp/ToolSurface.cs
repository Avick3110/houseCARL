using System.Reflection;

namespace HousecarlMcp;

/// <summary>The one home for which assembly is the tool surface; Program.cs passes it to <c>WithToolsFromAssembly</c>.</summary>
public static class ToolSurface
{
    public static Assembly Assembly { get; } = typeof(ToolSurface).Assembly;
}

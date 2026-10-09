namespace HousecarlCore;

/// <summary>The levers a collapsed container's note names, in the calling tool's spelling; the measured depth and the
/// field path are filled per container by <see cref="ReadEngine"/>. Contract in docs/architecture/read-engine.md.</summary>
public sealed class ExpandHint
{
    public ExpandHint(string depthLever, string formatHop = "")
    {
        DepthLever = depthLever;
        FormatHop = formatHop;
    }

    /// <summary>The depth knob with its '=', e.g. "depth=" or "project.depth=".</summary>
    public string DepthLever { get; }

    /// <summary>Said after the depth when this format cannot expand, e.g. " with format=text/json".</summary>
    public string FormatHop { get; }

    /// <summary>The spelling of a surface whose depth knob is <c>depth=</c>, and the read engine's default.</summary>
    public static readonly ExpandHint Legacy = new("depth=");

    /// <summary>No hint: for a surface with no depth knob, such as a write read-back.</summary>
    public static readonly ExpandHint None = new("");

    /// <summary>The note's suffix for the container at <paramref name="path"/>: the <c>[*]</c> lever when its children
    /// are elements, and <paramref name="depth"/>, or the cap when the walk under it hit <paramref name="cap"/>.</summary>
    public string For(string path, bool elements, int? depth, int cap)
    {
        if (ReferenceEquals(this, None)) return "";
        var star = elements ? $" — name '{path}[*]' for one row per element" : "";
        if (depth is { } n)
            return star + (elements ? ", or " : " — ") + $"pass {DepthLever}{n}{FormatHop} to reach every leaf under it";
        return star + (elements ? "; " : " — ")
             + $"no {DepthLever}N reaches every leaf under it within the {cap}-line expansion cap, so narrow to a path under it";
    }
}

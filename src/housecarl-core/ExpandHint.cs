namespace HousecarlCore;

/// <summary>The levers a collapsed container's note names, in the calling tool's spelling: a constant sentence, or on a
/// read of named records the depth measured per container by <see cref="ReadEngine"/>. Contract in
/// docs/architecture/read-engine.md.</summary>
public sealed class ExpandHint
{
    ExpandHint(string constant, string? depthLever, string formatHop)
    {
        Constant = constant;
        DepthLever = depthLever;
        FormatHop = formatHop;
    }

    /// <summary>A hint that is always <paramref name="text"/> and measures nothing; the scan lanes' per-cell hint.</summary>
    public static ExpandHint Fixed(string text) => new(text, null, "");

    /// <summary>A hint that measures the depth under each container, saying <paramref name="fallback"/> when it cannot.</summary>
    public static ExpandHint Measured(string depthLever, string fallback, string formatHop = "") => new(fallback, depthLever, formatHop);

    /// <summary>The constant sentence: the whole hint when nothing is measured, and the fallback when measuring fails.</summary>
    public string Constant { get; }

    /// <summary>The depth knob with its '=', e.g. "project.depth="; null when this hint measures nothing.</summary>
    public string? DepthLever { get; }

    /// <summary>Said after the depth when this format cannot expand, e.g. " with format=text/json".</summary>
    public string FormatHop { get; }

    /// <summary>The read engine's default: the <c>depth=</c> sentence, unmeasured.</summary>
    public static readonly ExpandHint Legacy = Fixed(" — pass depth=2 to expand");

    /// <summary>No hint: for a surface with no depth knob, such as a write read-back, or a read that never shows it.</summary>
    public static readonly ExpandHint None = Fixed("");

    /// <summary>The note's suffix for the container at <paramref name="path"/>. <paramref name="measure"/> gives whether
    /// its children are elements and the depth reaching every leaf (null past <paramref name="cap"/>, 1 or less when
    /// nothing is under it); it runs only on a measured hint, and a throw says <see cref="Constant"/>.</summary>
    public string For(string path, Func<(bool Elements, int? Depth)> measure, int cap)
    {
        if (DepthLever is null) return Constant;
        bool elements; int? depth;
        try { (elements, depth) = measure(); }
        catch { return Constant; }
        if (depth is <= 1) return "";
        var star = elements ? $" — name '{path}[*]' for one row per element" : "";
        if (depth is { } n)
            return star + (elements ? ", or " : " — ") + $"pass {DepthLever}{n}{FormatHop} to reach every leaf under it";
        return star + (elements ? "; " : " — ")
             + $"no {DepthLever}N reaches every leaf under it within the {cap}-line expansion cap, so narrow to a path under it";
    }
}

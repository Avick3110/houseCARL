namespace HousecarlCore;

/// <summary>Marks a CI guard's entry point; the <c>ci-all</c> roster is the set of methods carrying this.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CiProbeAttribute : Attribute
{
    public CiProbeAttribute(string name) => Name = name;

    /// <summary>The verb CI and the local CLI invoke; unique across the whole guard population.</summary>
    public string Name { get; }

    /// <summary>True when CI runs this verb as its own workflow step rather than inside <c>ci-all</c>.</summary>
    public bool Standalone { get; init; }
}

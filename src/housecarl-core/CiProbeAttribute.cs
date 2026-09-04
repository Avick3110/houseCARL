namespace HousecarlCore;

/// <summary>
/// Marks a CI guard's entry point. The <c>ci-all</c> roster IS the set of methods carrying this, so a
/// guard enrols itself and deleting its file deletes its row.
/// </summary>
/// <remarks>
/// Lives in housecarl-core because guard entry points do: two of them are <c>WriteEngine</c>'s coerce verbs,
/// and core is the only assembly every guard host can reference.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CiProbeAttribute : Attribute
{
    public CiProbeAttribute(string name) => Name = name;

    /// <summary>The verb CI and the local CLI invoke. Must be unique across the whole guard population.</summary>
    public string Name { get; }

    /// <summary>
    /// True when CI runs this verb as its OWN workflow step rather than inside <c>ci-all</c> — for a check that
    /// needs a cold process. Standalone verbs are dispatchable and counted, but are not roster rows and do not run
    /// in <c>ci-all</c>.
    /// </summary>
    public bool Standalone { get; init; }
}

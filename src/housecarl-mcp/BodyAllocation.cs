using HousecarlCore;

namespace HousecarlMcp;

/// <summary>How the merged <c>check</c> sweep divides the room its body may occupy: max-min fairness
/// (water-filling) over measured demand, hierarchical and computed before the render. The fill, what it does
/// not divide, the properties that pin it and the known under-fills: docs/architecture/render-budget.md.</summary>
internal sealed class BodyAllocation
{
    /// <summary>A demand this large means "more than anything could give it" — the bounded pass stopped early.</summary>
    internal const int Unconstrained = int.MaxValue;

    readonly Dictionary<SweepSubject, int> _allocation = new();
    readonly Dictionary<SweepSubject, int> _spent = new();

    /// <summary>Build the allocation, once and before the first unit is emitted, from the plan and the measured
    /// demands. A subject the caller did not measure is unconstrained rather than zero, so a missing measurement
    /// never silently allocates nothing.</summary>
    /// <param name="rowBudget">the room left for rows: the body budget less everything reserved.</param>
    /// <param name="responseSubjects">subjects belonging to the response rather than to any family.</param>
    internal BodyAllocation(int rowBudget,
                            IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)> plan,
                            IReadOnlyDictionary<SweepSubject, int>? demand = null,
                            IReadOnlyList<SweepSubject>? responseSubjects = null)
    {
        rowBudget = Math.Max(0, rowBudget);
        int Demand(SweepSubject s) => demand is not null && demand.TryGetValue(s, out var d) ? d : Unconstrained;

        // The top-level participants: the response's own subjects as one group, then one group per family.
        var groups = new List<(int Key, IReadOnlyList<SweepSubject> Subjects)>();
        if (responseSubjects is { Count: > 0 }) groups.Add((groups.Count, responseSubjects));
        foreach (var (_, subjects) in plan)
            if (subjects.Count > 0) groups.Add((groups.Count, subjects));

        var wants = new List<(int Key, int Demand)>();
        foreach (var (key, subjects) in groups)
        {
            long total = 0;
            foreach (var s in subjects)
            {
                int d = Demand(s);
                if (d == Unconstrained) { total = Unconstrained; break; }
                total += d;
            }
            wants.Add((key, total >= Unconstrained ? Unconstrained : (int)total));
        }

        var groupShare = WaterFill(rowBudget, wants);
        foreach (var (key, subjects) in groups)
        {
            var own = new List<(SweepSubject Key, int Demand)>();
            foreach (var s in subjects) own.Add((s, Demand(s)));
            foreach (var kv in WaterFill(groupShare[key], own)) _allocation[kv.Key] = kv.Value;
        }
    }

    /// <summary>Max-min fairness over one level: every child gets <c>min(its demand, lambda)</c>, computed by
    /// satisfying the cheapest demands first, which is why the list is sorted. The division remainder is left
    /// unallocated rather than handed to an arbitrary child.</summary>
    static Dictionary<T, int> WaterFill<T>(int budget, List<(T Key, int Demand)> items) where T : notnull
    {
        var result = new Dictionary<T, int>();
        foreach (var i in items) result[i.Key] = 0;
        if (items.Count == 0 || budget <= 0) return result;

        var open = new List<(T Key, int Demand)>(items);
        open.Sort((a, b) => a.Demand.CompareTo(b.Demand));
        int remaining = budget;
        for (int i = 0; i < open.Count; i++)
        {
            int share = remaining / (open.Count - i);
            if (open[i].Demand <= share)
            {
                result[open[i].Key] = open[i].Demand;
                remaining -= open[i].Demand;
                continue;
            }
            // Nobody left wants less than an equal share, so the level is that share.
            for (int j = i; j < open.Count; j++) result[open[j].Key] = share;
            break;
        }
        return result;
    }

    /// <summary>Does this subject have an allocation at all? False for a subject no plan declared, which then
    /// answers to the response-wide budget alone.</summary>
    internal bool Governs(SweepSubject s) => _allocation.ContainsKey(s);

    internal bool Fits(SweepSubject s, int cost)
        => !Governs(s) || Spent(s) + cost <= _allocation[s];

    /// <summary>Charge what a unit actually appended, never the declared cost.</summary>
    internal void Charge(SweepSubject s, int chars)
    {
        if (Governs(s)) _spent[s] = Spent(s) + chars;
    }

    /// <summary>This subject will emit nothing further. A deliberate no-op: handing leftovers on at render time is
    /// what makes an allocation order-dependent and non-monotone. Do not fill it in.</summary>
    internal void Done(SweepSubject s) { }

    internal int AllocationOf(SweepSubject s) => _allocation.TryGetValue(s, out var n) ? n : 0;

    /// <summary>What this subject actually spent, charged unit by unit with what each one wrote.</summary>
    internal int SpentOn(SweepSubject s) => Spent(s);

    int Spent(SweepSubject s) => _spent.TryGetValue(s, out var n) ? n : 0;
}

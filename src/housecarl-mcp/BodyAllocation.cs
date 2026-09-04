using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// How the merged <c>check</c> sweep divides the room its body may occupy among the things competing for it.
///
/// <para><b>Max-min fairness (water-filling) over measured demand, computed before the render.</b> Hand out one
/// row's worth at a time; a child that runs out of rows drops out and its remainder goes to the others; run that to
/// completion. Equivalently, and how it is computed here: every child gets <c>min(its demand, λ)</c>, where λ is
/// the level at which the budget is exhausted.</para>
///
/// <para>Each child's allocation is therefore a function of the budget and the demand vector alone, never of render
/// order, so nothing is handed back and nothing is stranded. It is also monotone: λ is non-decreasing in the row
/// budget, so raising <c>max_chars=</c> can never render less of anything — which is what makes the response's own
/// "raise max_chars= to see more" remedy true.</para>
///
/// <para>No stranding is a claim about the ALLOCATION, not about spending. Some subjects' units are reachable only
/// through another subject's — a plugin's dangling entries through its section, a seed's topic blocks through its
/// head — so when a parent stops, children behind it become unreachable and a response can render less than its
/// allocation permits. That is under-fill, not an overrun, and monotonicity is unaffected.</para>
///
/// <para><b>Hierarchical, not flat.</b> The top-level participants water-fill the row budget among themselves —
/// one per family, plus one for the response's own subjects (the excluded-plugin roster, which belongs to no family
/// because it is a fact about the scope); each participant's subjects then water-fill that participant's
/// allocation. A flat fill over the leaf subjects would hand a subject-rich family more of the budget purely
/// because it has more parts.</para>
///
/// <para><b>Every quantity read here is measured.</b> A demand is the cumulative width of a subject's actual units,
/// composed by the same helper that will write them, in the transport's own unit — never a mean, a row count times
/// a width, or an estimate. It is measured with a bound: a subject whose units exceed the room its parent has to
/// give is <see cref="Unconstrained"/> and measuring stops there, so the pass costs O(budget) rather than O(all
/// rows). Such a subject will be cut whatever λ is, and <c>min(demand, λ)</c> needs nothing more precise.</para>
///
/// <para><b>What it does not divide.</b> The fixed part — the title, the scope sentence, every family's head, each
/// subject's unconditional lines and its closing disclosure, the accounting, the boundary — is outside allocation
/// entirely; a share of the body is not what pays for those. It is measured rather than assembled: the render
/// composes the whole response once through a <see cref="BoundedBody.Skeleton"/>, and what comes back less what its
/// units wrote is the fixed part. The pieces that vary with the cut cannot be skeleton-composed and go through
/// <see cref="BoundedBody.Reserve"/> as an upper bound instead. Leaving any of it inside the row budget puts the
/// response-wide test ahead of the allocation, and render order decides who loses again.</para>
///
/// <para><b>Whole-unit granularity.</b> A subject renders the largest prefix of its units that fits its allocation;
/// a unit is emitted whole or not at all, so a site declaring a cost of 0 overshoots by one unit and no more, and
/// the subject's closing disclosure states the cut either way.</para>
/// </summary>
internal sealed class BodyAllocation
{
    /// <summary>A demand this large means "more than anything could give it" — the bounded pass stopped early.</summary>
    internal const int Unconstrained = int.MaxValue;

    readonly Dictionary<SweepSubject, int> _allocation = new();
    readonly Dictionary<SweepSubject, int> _spent = new();

    /// <summary>Build the allocation. Computed once, before the first unit is emitted, from the plan and the
    /// measured demands — building it lazily at a first write would make it depend on render order.</summary>
    /// <param name="rowBudget">the room left for rows: the body budget less everything reserved.</param>
    /// <param name="plan">the families this response renders and which of each family's subjects have rows. A
    /// subject with nothing to render is left out by the caller; one in the plan with demand 0 is allocated 0.</param>
    /// <param name="demand">each planned subject's measured demand, or <see cref="Unconstrained"/>. A subject the
    /// caller did not measure is treated as unconstrained rather than as zero, so a missing measurement never
    /// silently allocates nothing.</param>
    /// <param name="responseSubjects">subjects that belong to the response rather than to any family — the
    /// excluded-plugin roster, a fact about the scope emitted once however many families ran. It is a top-level
    /// participant beside the families, so it takes <c>min(its demand, λ)</c> of the row budget as a family does.
    /// Reserved off the top instead it would be either ungoverned (its rows spending against the whole body budget)
    /// or unspendable, since a reserve is room the emission test holds standing.</param>
    internal BodyAllocation(int rowBudget,
                            IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)> plan,
                            IReadOnlyDictionary<SweepSubject, int>? demand = null,
                            IReadOnlyList<SweepSubject>? responseSubjects = null)
    {
        rowBudget = Math.Max(0, rowBudget);
        int Demand(SweepSubject s) => demand is not null && demand.TryGetValue(s, out var d) ? d : Unconstrained;

        // The top-level participants: the response's own subjects as one group, then one group per family. A
        // group's demand is what its members want together, saturating rather than overflowing — a group with one
        // unconstrained member wants more than the budget, and that is all the fill needs to know.
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

    /// <summary>Max-min fairness over one level: every child gets <c>min(its demand, λ)</c>.
    ///
    /// <para>Computed by satisfying the cheapest demands first — a child wanting less than an equal share of what is
    /// left takes what it wants and drops out, raising the share for everyone still open. Requires the list to be
    /// walked in ascending demand order, which is why it is sorted.</para>
    ///
    /// <para>The division remainder (at most one char per open child) is left unallocated rather than handed to an
    /// arbitrary child; it cannot affect no-stranding, because a level where every demand is satisfied has no
    /// remainder.</para></summary>
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
            // Nobody left wants less than an equal share, so the level is that share and everyone still open sits
            // at it. Every demand from here up is >= this one and therefore also above the share.
            for (int j = i; j < open.Count; j++) result[open[j].Key] = share;
            break;
        }
        return result;
    }

    /// <summary>Does this subject have an allocation at all? False for a subject no plan declared — it then answers
    /// to the response-wide budget alone, which is the right rule for a lane that declared no families.</summary>
    internal bool Governs(SweepSubject s) => _allocation.ContainsKey(s);

    /// <summary>Would one more unit of <paramref name="s"/>, costing at most <paramref name="cost"/>, stay inside
    /// this subject's allocation?</summary>
    internal bool Fits(SweepSubject s, int cost)
        => !Governs(s) || Spent(s) + cost <= _allocation[s];

    /// <summary>Charge what a unit actually appended. Called from <see cref="BoundedBody.Emit"/> with the measured
    /// delta, never with the declared cost.</summary>
    internal void Charge(SweepSubject s, int chars)
    {
        if (Governs(s)) _spent[s] = Spent(s) + chars;
    }

    /// <summary>This subject will emit nothing further.
    ///
    /// <para>Deliberately a no-op on the allocation: under exact demands there is nothing to hand back, and
    /// handing leftovers on at render time is what makes an allocation order-dependent and non-monotone. Do not
    /// fill it in. <see cref="BoundedBody"/> tracks stopped subjects for its own reasons.</para></summary>
    internal void Done(SweepSubject s) { }

    /// <summary>What this subject was allocated.</summary>
    internal int AllocationOf(SweepSubject s) => _allocation.TryGetValue(s, out var n) ? n : 0;

    /// <summary>What this subject actually spent — charged unit by unit with what each one wrote. With nothing cut
    /// a subject's allocation is its measured demand, so this equals <see cref="AllocationOf"/> unless the
    /// measurement is not measuring the write.</summary>
    internal int SpentOn(SweepSubject s) => Spent(s);

    int Spent(SweepSubject s) => _spent.TryGetValue(s, out var n) ? n : 0;
}

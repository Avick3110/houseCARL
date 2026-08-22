using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// How the merged <c>check</c> sweep divides the room its BODY may occupy among the things competing for it
/// (#394, advisor ruling 2026-08-21, option D; revised 2026-08-21 by the phase-3 escalation — see below).
///
/// <para><b>What it replaces.</b> The subjects of a sweep response used to spend one budget IN SERIES, in
/// declaration order, each against everything still left. That is invisible while the budget is ample and brutal
/// the moment it is not, and the thing it starves is whatever renders last. Measured on the live ARR 2.0 order:
/// the <c>counts_only</c> by-SOURCE histogram — the axis #344 exists for — rendered 5 rows of 180 at
/// <c>max_chars=4000</c> and 0 at 3000, while the by-TARGET axis above it rendered all 74; and in the LISTING
/// lane the errors family alone came to 79,600 characters of an 80,000 default, so a second findings family
/// spending after it would have inherited 400 — at the no-arguments call, not at a cap anyone tightened.</para>
///
/// <para><b>THE RULE: MAX-MIN FAIRNESS (water-filling) over MEASURED DEMAND, computed BEFORE the render.</b>
/// Hand out one row's worth at a time; a subject that runs out of rows drops out and its remainder goes to the
/// others; run that to completion. Equivalently, and how it is computed here: every child gets
/// <c>min(its demand, λ)</c>, where λ is the level at which the budget is exhausted. A child that needs less than
/// an equal share takes only what it needs and the rest is divided among the children that want more.</para>
///
/// <para><b>Why this class was rewritten, stated plainly because the record needs it.</b> That water-filling IS
/// what option D ruled. What phase 1a built instead was a SEQUENTIAL RECOUNT — each child's ceiling fixed on its
/// first unit as "what is left, divided by the children that have not rendered yet" — and the advisor lane
/// ratified that as equivalent. It is not equivalent, and the difference is not cosmetic:
/// <list type="number">
/// <item><b>It stranded budget.</b> A child that finished under its ceiling only handed the remainder on if
/// something told the allocation it was finished, and in the LISTING lane nothing ever did. Measured on
/// check-guard's own fixture at the 80,000 default: the errors family alone renders in 8,998 characters and the
/// scripts family alone in 38,253, so both whole is 47,251 — comfortably inside the cap. The merged response
/// stopped at 49,440 with the scripts family cut to 35 of its 40 record sections, reported <c>truncated: true</c>,
/// told the caller to raise <c>max_chars=</c>, and left 30,560 characters unspent.</item>
/// <item><b>Handing the remainder on would have broken monotonicity.</b> Under a sequential recount a successor's
/// room is <c>rowBudget − predecessorSpent</c>, and a predecessor's spend is a STEP function of the budget — so
/// the remainder dips wherever the predecessor fits one more chunky unit. Measured while trying exactly that fix:
/// at <c>max_chars=3206</c> the response was 2,709 characters and carried a scripts record section; at 3,226 it
/// was 2,373 and carried none, because the errors family's newly-affordable dangling entry cost the scripts
/// family its whole section. A wider cap returning less makes the response's own printed remedy false.</item>
/// </list>
/// Water-filling has neither problem, and it is the ruled rule rather than a third option: each child's
/// allocation is <c>min(demand, λ)</c>, a function of the budget and the demand vector ALONE. It does not depend
/// on render order, so there is nothing to hand back and nothing to strand.</para>
///
/// <para><b>MONOTONE IN max_chars, BY CONSTRUCTION.</b> λ is non-decreasing in the row budget, so every child's
/// <c>min(demand, λ)</c> is non-decreasing too: raising <c>max_chars=</c> can never render less of anything. That
/// is what makes the response's own remedy — "raise max_chars= to see more" — a true sentence rather than one
/// that happens to hold at the caps anyone tried.</para>
///
/// <para><b>NO STRANDING.</b> If every child's demand fits inside the budget, every child is allocated its whole
/// demand and the response claims no cut. A merged call that could have shown everything does.</para>
///
/// <para><b>HIERARCHICAL, not flat, and that is still load-bearing.</b> The top-level participants water-fill the
/// row budget among themselves on their own demands — one per family, plus ONE for the response's own subjects
/// (the excluded-plugin roster, which belongs to no family because it is a fact about the SCOPE); each
/// participant's subjects then water-fill THAT participant's allocation. A flat fill over
/// the leaf subjects would hand a subject-rich family more of the budget purely because it has more parts — the
/// same complaint the 148:1 findings-count ratio between the scripts and errors families makes one level up,
/// arriving through the back door. <c>ALLOCATION-FAMILY-SHARES-IGNORE-SUBJECT-COUNT</c> is the arm.</para>
///
/// <para><b>Every quantity read here is MEASURED.</b> A demand is the cumulative width of a subject's ACTUAL
/// units, composed by the same helper that will write them, in the transport's own unit — never a mean, a row
/// count times a width, or an estimate. Demand is measured with a bound: a subject whose units exceed the room
/// its parent has to give is UNCONSTRAINED, and measuring stops there, so the pass costs O(budget) rather than
/// O(all rows) on a sweep carrying 180,028 findings. An unconstrained subject is one that will be cut whatever λ
/// turns out to be, and <c>min(demand, λ)</c> needs nothing more precise than that about it.</para>
///
/// <para><b>What it deliberately does NOT divide, and how that number is arrived at.</b> The fixed part — the
/// title, the scope sentence, every family's section head and its own head, each subject's unconditional lines and
/// its closing disclosure, the accounting, the boundary — is outside allocation entirely. Those are the things a
/// response may never drop, and a share of the body is not what pays for them. It is MEASURED, never assembled:
/// the render composes the WHOLE response once through a <see cref="BoundedBody.Skeleton"/>, which admits one unit
/// of each subject and refuses the rest, and what comes back less what those units wrote is the fixed part. (One
/// unit rather than none because a json array closes on its own indented line when it holds something and on the
/// same line when it does not — a fixed part measured with every array empty is short by that on every array the
/// response opens.) The pieces that vary with the CUT — a closing disclosure says a
/// different thing at a different length depending on what fit — cannot be skeleton-composed and go through
/// <see cref="BoundedBody.Reserve"/> as an upper bound instead, which is the one place a bound is still taken.
/// <b>Counting only the pieces that happened to call Reserve was the defect this replaces:</b> the row budget then
/// included the response's own title and heads, so the global test bit before any subject reached its share and
/// render order decided who lost — the order-dependence water-filling exists to remove, re-entering one level
/// up.</para>
///
/// <para><b>Whole-unit granularity.</b> A subject renders the largest PREFIX of its units that fits its
/// allocation; a unit is emitted whole or not at all. 4a's one-unit residual posture stands — a site that declares
/// a cost of 0 overshoots its allocation by one unit and no more — and the subject's own closing disclosure states
/// the cut either way.</para>
/// </summary>
internal sealed class BodyAllocation
{
    /// <summary>A demand this large means "more than anything could give it" — the bounded pass stopped early.</summary>
    internal const int Unconstrained = int.MaxValue;

    readonly Dictionary<SweepSubject, int> _allocation = new();
    readonly Dictionary<SweepSubject, int> _spent = new();

    /// <summary>Build the allocation. Computed ONCE, before the first unit is emitted, from the plan and the
    /// MEASURED demands — never lazily on a first write, which is what made the old rule order-dependent.</summary>
    /// <param name="rowBudget">the room left for ROWS: the body budget less everything reserved.</param>
    /// <param name="plan">the families this response renders and which of each family's subjects have rows. A
    /// subject with nothing to render is left OUT by the caller; one that is in the plan and has demand 0 is
    /// allocated 0, which is the same answer arrived at honestly.</param>
    /// <param name="demand">each planned subject's measured demand, or <see cref="Unconstrained"/>. A subject the
    /// caller did not measure is treated as unconstrained rather than as zero: a missing measurement must never
    /// silently allocate nothing.</param>
    /// <param name="responseSubjects">subjects that belong to the RESPONSE rather than to any family — today the
    /// excluded-plugin roster, which is a fact about the SCOPE and is emitted once however many families ran. They
    /// are a top-level participant beside the families, so a roster takes <c>min(its demand, λ)</c> of the row
    /// budget exactly as a family does.
    ///
    /// <para><b>Why they are in the fill rather than reserved off the top.</b> Held as a reserve the roster was
    /// governed by nothing: its rows were admitted against the whole body budget before the first family head was
    /// written, and the fixed part then went past the cap — measured at 4,494 chars against a 4,000 cap. Given its
    /// own reserve instead, the rows could not spend it, because a reserve is room the emission test holds
    /// STANDING. A response-level participant is the shape that is both bounded and spendable, and it inherits the
    /// three properties the families already have: monotone in the budget, no stranding, allocation equals
    /// spend.</para></param>
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

    /// <summary>MAX-MIN FAIRNESS over one level: every child gets <c>min(its demand, λ)</c>.
    ///
    /// <para>Computed by satisfying the cheapest demands first — a child wanting less than an equal share of what
    /// is left takes what it wants and drops out, which raises the share for everyone still open. That loop IS the
    /// "hand out one row's worth at a time and let the finished drop out" rule, run to completion in closed form
    /// rather than by simulation.</para>
    ///
    /// <para>The division remainder (at most one char per open child) is left unallocated rather than handed to an
    /// arbitrary child: whichever child received it would be receiving it for a reason nothing could state, and it
    /// cannot affect the no-stranding property — a level where every demand is satisfied has no remainder to give
    /// away.</para></summary>
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
    /// to the global budget alone, which is the pre-#394 behaviour and the right one for a lane that declared no
    /// families.</summary>
    internal bool Governs(SweepSubject s) => _allocation.ContainsKey(s);

    /// <summary>Would one more unit of <paramref name="s"/>, costing at most <paramref name="cost"/>, stay inside
    /// this subject's allocation?</summary>
    internal bool Fits(SweepSubject s, int cost)
        => !Governs(s) || Spent(s) + cost <= _allocation[s];

    /// <summary>Charge what a unit ACTUALLY appended. Called from <see cref="BoundedBody.Emit"/> with the measured
    /// delta, never with the declared cost.</summary>
    internal void Charge(SweepSubject s, int chars)
    {
        if (Governs(s)) _spent[s] = Spent(s) + chars;
    }

    /// <summary>This subject will emit nothing further.
    ///
    /// <para><b>A NO-OP on the allocation, deliberately.</b> Under exact demands there is nothing to hand back —
    /// a subject that rendered everything it had was allocated exactly that, and one that was cut was allocated
    /// less than it wanted, so neither has a remainder anyone else could use. Re-introducing render-time
    /// leftover-taking is what made the old rule order-dependent and non-monotone, so it is not done here and
    /// this method does not exist to be filled in later. <see cref="BoundedBody"/> still tracks stopped subjects
    /// for its own reasons — a stopped subject emits nothing further — and that is what the Stop paths are
    /// for.</para></summary>
    internal void Done(SweepSubject s) { }

    /// <summary>What this subject was allocated — for the arms, which assert against it rather than against a
    /// number the render printed.</summary>
    internal int AllocationOf(SweepSubject s) => _allocation.TryGetValue(s, out var n) ? n : 0;

    /// <summary>What this subject actually spent — charged unit by unit with what each one wrote. Beside
    /// <see cref="AllocationOf"/> it is the exactness test: with nothing cut, a subject's allocation IS its measured
    /// demand, so the two numbers agree exactly or the measurement is not measuring the write.</summary>
    internal int SpentOn(SweepSubject s) => Spent(s);

    int Spent(SweepSubject s) => _spent.TryGetValue(s, out var n) ? n : 0;
}

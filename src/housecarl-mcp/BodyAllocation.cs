using HousecarlCore;

namespace HousecarlMcp;

/// <summary>
/// How the merged <c>check</c> sweep divides the room its BODY may occupy among the things competing for it
/// (#394, advisor ruling 2026-08-21, option D).
///
/// <para><b>What it replaces.</b> The subjects of a sweep response used to spend one budget IN SERIES, in
/// declaration order, each against everything still left. That is invisible while the budget is ample and brutal
/// the moment it is not, and the thing it starves is whatever renders last. Measured on the live ARR 2.0 order:
/// the <c>counts_only</c> by-SOURCE histogram — the axis #344 exists for — rendered 5 rows of 180 at
/// <c>max_chars=4000</c> and 0 at 3000, while the by-TARGET axis above it rendered all 74; and in the LISTING
/// lane the errors family alone came to 79,600 characters of an 80,000 default, so a second findings family
/// spending after it would have inherited 400 — at the no-arguments call, not at a cap anyone tightened.</para>
///
/// <para><b>The rule: equal shares, recounted against what was actually spent.</b> When a child begins, its
/// ceiling is what is left of its parent's room divided by the number of that parent's children that have not
/// rendered yet, itself included. A child that finishes under its ceiling therefore hands the remainder straight
/// to the ones after it — the recount IS the redistribution, and there is no separate pool to keep in step with
/// the spending.</para>
///
/// <para><b>HIERARCHICAL, not flat, and that is the load-bearing part.</b> Families divide the body budget among
/// themselves; a family's subjects then divide THAT family's share. A flat walk over the leaf subjects would hand
/// a subject-rich family more shares purely because it has more parts — the same complaint the 148:1
/// findings-count ratio between the scripts and errors families makes one level up, arriving through the back
/// door. Two families get equal shares whether one has a single axis and the other has four.
/// <c>ALLOCATION-FAMILY-SHARES-IGNORE-SUBJECT-COUNT</c> is the arm.</para>
///
/// <para><b>Every quantity read here is MEASURED.</b> Nothing in this class knows a mean row width, a row count,
/// or an estimate of what anything will cost. It reads two things: the room it was given, and what has actually
/// been appended — handed to it by <see cref="BoundedBody"/> at the write, in the transport's own unit. A
/// declared cost is a test before a write, exactly as it is for the global budget, and the response's length
/// only grows, so a site that under-declares overshoots its ceiling by one unit and every later test in every
/// subject is a comparison against a number already over. The damage is one unit, and it is not silent — the
/// subject discloses its own cut either way.</para>
///
/// <para><b>What it deliberately does NOT divide.</b> The reserved fixed part — the header, the accounting, each
/// subject's unconditional lines and its closing disclosure, the boundary — is outside allocation entirely. Those
/// are the things a response may never drop, and a share of the body is not what pays for them
/// (<see cref="BoundedBody.Reserve"/>). Allocation divides the room left for ROWS, once, after the reserves are
/// taken and before the first unit is emitted.</para>
///
/// <para><b>Order effects, stated rather than claimed absent.</b> A recount at each child's start gives the
/// starved child at least its equal share in every order, which is what the measured defect was. What it does
/// not do is let a child that already STOPPED at its ceiling resume when a later sibling turns out to be short:
/// that room goes unspent. The residual is bounded by one share and it is a waste, never a starvation, and the
/// subject that stopped says so in its own closing disclosure with the knob that moves it — <c>max_chars=</c>,
/// which is correct, because a larger body budget is a larger share.</para>
/// </summary>
internal sealed class BodyAllocation
{
    readonly List<SweepFamily> _families = new();
    readonly Dictionary<SweepFamily, List<SweepSubject>> _pending = new();
    readonly Dictionary<SweepFamily, int> _familyShare = new();
    readonly Dictionary<SweepFamily, int> _familySpent = new();
    readonly Dictionary<SweepSubject, SweepFamily> _familyOf = new();
    readonly Dictionary<SweepSubject, int> _ceiling = new();
    readonly Dictionary<SweepSubject, int> _spent = new();

    int _rowBudget;
    int _spentByFinishedFamilies;

    /// <summary>Declare the plan: which families this response renders, and which of each family's subjects have
    /// rows to render. A subject with nothing to render is left OUT — a share held for rows that do not exist is
    /// the equal-split waste this rule is chosen over.</summary>
    /// <param name="rowBudget">the room left for ROWS: the body budget less everything reserved.</param>
    internal BodyAllocation(int rowBudget, IReadOnlyList<(SweepFamily Family, IReadOnlyList<SweepSubject> Subjects)> plan)
    {
        _rowBudget = Math.Max(0, rowBudget);
        foreach (var (family, subjects) in plan)
        {
            if (subjects.Count == 0) continue;
            _families.Add(family);
            _pending[family] = new List<SweepSubject>(subjects);
            _familySpent[family] = 0;
            foreach (var s in subjects) _familyOf[s] = family;
        }
    }

    /// <summary>Does this subject have a ceiling at all? False for a subject no plan declared — it then answers to
    /// the global budget alone, which is the pre-#394 behaviour and the right one for a lane that declared no
    /// families.</summary>
    internal bool Governs(SweepSubject s) => _familyOf.ContainsKey(s);

    /// <summary>Would one more unit of <paramref name="s"/>, costing at most <paramref name="cost"/>, stay inside
    /// this subject's ceiling? Fixes the ceiling on the subject's first unit, which is where the recount happens.
    /// </summary>
    internal bool Fits(SweepSubject s, int cost)
    {
        if (!Governs(s)) return true;
        return Spent(s) + cost <= Ceiling(s);
    }

    /// <summary>Charge what a unit ACTUALLY appended. Called from <see cref="BoundedBody.Emit"/> with the measured
    /// delta, never with the declared cost.</summary>
    internal void Charge(SweepSubject s, int chars)
    {
        if (!Governs(s)) return;
        _spent[s] = Spent(s) + chars;
        var f = _familyOf[s];
        _familySpent[f] += chars;
    }

    /// <summary>This subject will emit nothing further — it rendered everything it had, or it stopped. Its
    /// remainder is not carried anywhere: the next sibling's recount reads what was SPENT, so an unfinished
    /// share is given back by arithmetic rather than by being moved.</summary>
    internal void Done(SweepSubject s)
    {
        if (!Governs(s)) return;
        var f = _familyOf[s];
        _pending[f].Remove(s);
        if (_pending[f].Count == 0) FamilyDone(f);
    }

    void FamilyDone(SweepFamily f)
    {
        if (!_families.Remove(f)) return;
        _spentByFinishedFamilies += _familySpent[f];
        _familyShare.Remove(f);
    }

    int Spent(SweepSubject s) => _spent.TryGetValue(s, out var n) ? n : 0;

    /// <summary>This family's share, fixed the first time one of its subjects is asked about and recounted against
    /// what the families before it actually spent. Equal among the families still to render — <b>the count of
    /// FAMILIES, never of their subjects</b>.</summary>
    int Share(SweepFamily f)
    {
        if (_familyShare.TryGetValue(f, out var had)) return had;
        int remaining = Math.Max(0, _rowBudget - _spentByFinishedFamilies);
        int openFamilies = Math.Max(1, _families.Count);
        int share = remaining / openFamilies;
        _familyShare[f] = share;
        return share;
    }

    /// <summary>This subject's ceiling, fixed on its first unit and recounted against what its SIBLINGS in the
    /// same family have already spent — so a sibling that finished short raises this one's ceiling without
    /// anything having to hand the room over.</summary>
    int Ceiling(SweepSubject s)
    {
        if (_ceiling.TryGetValue(s, out var had)) return had;
        var f = _familyOf[s];
        int siblingsSpent = _familySpent[f];
        int remaining = Math.Max(0, Share(f) - siblingsSpent);
        int openSubjects = Math.Max(1, _pending[f].Count);
        int ceiling = remaining / openSubjects;
        _ceiling[s] = ceiling;
        return ceiling;
    }
}

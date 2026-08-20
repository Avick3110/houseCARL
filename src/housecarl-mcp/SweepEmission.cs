namespace HousecarlMcp;

/// <summary>
/// The countable things a sweep response can carry. A lane DECLARES the subjects it has
/// (<see cref="CheckAccounting"/>), and every sentence about what is missing is computed from a declared subject
/// — so a lane without sections cannot claim about sections, and a lane with them cannot fail to.
///
/// <para>These are LANE FACTS, not a findings taxonomy: "how many plugin sections did this response render" is a
/// question about the render, and it stays the same question whatever classes of finding the sections carry. The
/// merged <c>check</c> surface's families are a separate design (SPEC §6.1) and no enum here anticipates them.</para>
/// </summary>
internal enum SweepSubject
{
    /// <summary>Dangling references listed one line at a time. The only subject the listing budget (<c>limit=</c>)
    /// can also drop, which is why it is the only one whose omission is decomposed into two causes.</summary>
    DanglingEntries,

    /// <summary>Per-plugin report sections. Present in every LISTING lane, including one where <c>findings=</c>
    /// excluded 'dangling' and there are no entries at all — the render still cuts sections there.</summary>
    PluginSections,

    /// <summary>Rows of the excluded-plugin roster: the plugins the index could not parse.</summary>
    ExcludedRows,

    /// <summary>Rows of the <c>counts_only</c> honesty layer: the plugins whose records could not be read.</summary>
    UnreadRows,

    /// <summary>Histogram rows under <c>counts_only</c>. Deliberately NOT a declared accounting subject: the
    /// histogram already discloses its own cut in both transports (a "... N more row(s)" line in text, the
    /// <c>distinct</c> vs <c>rendered</c> pair in json), and a second statement of one fact is how a twin starts.
    /// It is a subject HERE so that the framing lines and the rows go through the same bound as everything else.
    /// </summary>
    HistogramRows,
}

/// <summary>
/// THE ONE PLACE either sweep transport appends anything the caller's <c>max_chars</c> can refuse.
///
/// <para><b>Why one helper rather than a test per write site.</b> Boundedness used to be asserted at each site, so
/// the unbounded ones were found one at a time and each fix bred a sibling: the json plugin head (7,296 chars
/// against a 5,270 cap), then its exact twin the <c>counts_only</c> unread roster (9,823 against 8,000), then the
/// histogram framing lines, then the json excluded roster (1,188 chars past, written above the point the header is
/// even measured at). Four instances of one class. Every body write now goes through <see cref="Emit"/>, and the
/// bound is enforced by this class rather than promised by the caller.</para>
///
/// <para><b>Why a site that under-states its cost still cannot run away.</b> A caller passes a cost only where one
/// unit can be LARGE — a plugin object carrying three exception messages, an unread row carrying a scan error —
/// because there the test before the write is what keeps the response inside its cap rather than one unit over it.
/// Everywhere else the cost is zero and the test degenerates to "is there room at all". That is enough on its own,
/// because the response's length only ever GROWS: the first unit whose declared cost was too small takes it past
/// the budget, and from that moment every test — in every subject, whether or not that subject has been tried —
/// is a comparison against a length already over. So the damage of a forgotten cost is ONE unit, and it is never
/// silent, because the accounting is a subtraction taken after emission and states what was left out either way.</para>
///
/// <para>An explicit "the response has gone over, stop everything" flag was written here first and then deleted:
/// monotonic length already makes it true, so the flag could be removed with every arm still green. A conditional
/// that cannot be fixtured honestly is the signal to delete it, not a testing gap to work around (CLAUDE.md §5 #11,
/// PR #339's precedent).</para>
///
/// <para>The header and the accounting are outside this: the header is the response's fixed part and the accounting
/// plus boundary are RESERVED out of <c>max_chars</c> before the body renders. Those are the two things a response
/// is never allowed to drop, which is exactly why they are not emitted through a helper that can refuse.</para>
/// </summary>
internal sealed class BoundedBody
{
    readonly int _budget;
    readonly Func<int> _length;
    readonly CheckAccounting? _acct;
    readonly HashSet<SweepSubject> _stopped = new();

    /// <param name="acct">the accounting to register emissions with, or null for a lane that keeps no accounting
    /// (validate_scripts, whose response layer is not this branch's — it still gets the same bound).</param>
    /// <param name="budget">the chars the BODY may occupy: the caller's max_chars less the accounting's reserve.</param>
    /// <param name="length">what the response has emitted so far, in the transport's own unit.</param>
    internal BoundedBody(CheckAccounting? acct, int budget, Func<int> length)
    {
        _acct = acct;
        _budget = budget;
        _length = length;
    }

    /// <summary>Emit one unit of <paramref name="subject"/>, or refuse. Returns false when the unit did not fit —
    /// the caller's loop breaks and the accounting already knows, because the count it will report is the count of
    /// units that came back true.</summary>
    /// <param name="cost">an UPPER BOUND on what <paramref name="commit"/> will append, or 0 where the site has no
    /// cheap way to measure one — see the class summary for why a zero here bounds the damage at one unit.</param>
    /// <param name="source">for <see cref="SweepSubject.DanglingEntries"/>, the plugin the entry came FROM — the
    /// by-source roster is tallied off the same registration as the count, so the two cannot disagree.</param>
    internal bool Emit(SweepSubject subject, int cost, Action commit, string? source = null)
    {
        if (_stopped.Contains(subject)) return false;
        if (_length() + cost > _budget) { _stopped.Add(subject); return false; }
        commit();
        _acct?.Emitted(subject, source);
        return true;
    }

    /// <summary>Emit a subject's own CLOSING DISCLOSURE — the line that says how much of it did not fit. It is not
    /// a unit, so it is not registered and it is not refused because the subject stopped: a subject that stopped is
    /// exactly when this has to be said. The budget still applies, and the room for it is what the caller held back
    /// while emitting the units.
    ///
    /// <para>Without this it was refused by its own subject's stop flag, and a histogram cut by max_chars rendered
    /// its rows and then said nothing about the ones it had dropped — the silent cut, one level down from the one
    /// the accounting states.</para></summary>
    internal bool Close(SweepSubject subject, int cost, Action commit)
    {
        if (_length() + cost > _budget) return false;
        commit();
        _stopped.Add(subject);   // nothing follows a subject's closing disclosure
        return true;
    }

    /// <summary>Did this subject stop short? For the one caller that states the fact in its own words rather than
    /// through the accounting (validate_scripts).</summary>
    internal bool Stopped(SweepSubject subject) => _stopped.Contains(subject);
}

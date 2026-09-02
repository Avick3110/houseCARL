using System.Diagnostics;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The stdio transport underneath <see cref="ServerFixture"/>: exactly one reader on the server's stream,
/// and one timeout that stops the run rather than repeating itself once per remaining test.
///
/// <para>Both properties are about the SHARED fixture. 73 stdio tests drive one server process, so a read
/// that outlives its caller, or a hang that every later test re-discovers on its own 60-second budget, are
/// run-level failures wearing a single test's name.</para>
/// </summary>
public sealed class ServerTransportTests
{
    /// <summary>
    /// A stream whose first line arrives late, with every reader serialised behind it — the shape a real
    /// stdio stream has when one response is slow: the late line is not lost, it is merely late.
    /// </summary>
    sealed class GatedReader : TextReader
    {
        readonly object _gate = new();
        readonly Queue<string> _lines;
        readonly TimeSpan _firstDelay;
        bool _first = true;

        public GatedReader(TimeSpan firstDelay, params string[] lines)
        { _firstDelay = firstDelay; _lines = new Queue<string>(lines); }

        public override string? ReadLine()
        {
            lock (_gate)
            {
                if (_first) { _first = false; Thread.Sleep(_firstDelay); }
                return _lines.Count > 0 ? _lines.Dequeue() : null;
            }
        }
    }

    /// <summary>
    /// The orphaned-read defect, stated as a property. Measured RED against the shape this replaced: with
    /// a per-call <c>Task.Run(ReadLine)</c> the abandoned read takes "A" and the next caller is handed "B",
    /// one line behind for the rest of the process.
    /// </summary>
    [Fact]
    [Trait("tier", "unit")]
    public void ALineArrivingAfterItsCallerGaveUpIsQueued_NotEatenByAnAbandonedRead()
    {
        using var reader = new GatedReader(TimeSpan.FromMilliseconds(400), "A", "B");
        using var pump = new LinePump(reader);

        // Caller 1 runs out of budget while the first line is still in flight.
        Assert.False(pump.TryTake(TimeSpan.FromMilliseconds(50), out _),
                     "the gated reader answered inside 50ms, so this arm never reached the case it exists for");

        // Caller 2 gets the LATE line, then the one after it. Nothing was consumed by caller 1.
        Assert.True(pump.TryTake(TimeSpan.FromSeconds(5), out var first));
        Assert.Equal("A", first);
        Assert.True(pump.TryTake(TimeSpan.FromSeconds(5), out var second));
        Assert.Equal("B", second);
    }

    /// <summary>
    /// A drained, ended stream answers at once. Without this a dead server would cost every remaining
    /// caller its whole deadline before anything reported the death.
    /// </summary>
    [Fact]
    [Trait("tier", "unit")]
    public void OnceTheStreamHasEndedATakeReturnsAtOnceRatherThanBurningItsDeadline()
    {
        using var reader = new GatedReader(TimeSpan.Zero, "only");
        using var pump = new LinePump(reader);

        Assert.True(pump.TryTake(TimeSpan.FromSeconds(5), out var only));
        Assert.Equal("only", only);

        var sw = Stopwatch.StartNew();
        Assert.False(pump.TryTake(TimeSpan.FromSeconds(30), out _));
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
                    $"a take on an ended stream waited {sw.Elapsed.TotalSeconds:0.#}s of its 30s budget");
    }

    /// <summary>
    /// One timeout is terminal for the fixture. The later call fails immediately, and its message names the
    /// method and id of the FIRST timeout — so a slow runner produces one cause instead of seventy copies
    /// of its own wreckage.
    ///
    /// <para>Driven on a PRIVATE fixture with its deadline shortened; poisoning the shared one would be the
    /// very failure this arm describes.</para>
    ///
    /// <para>What discriminates here is the exception and its wording, not the clock. Measured: with the
    /// poison removed, this scenario's second call SUCCEEDS — the pump queued the late response and the id
    /// check skipped it — so the run would carry on past a timeout it had already suffered. The 60-second
    /// cascade the poison is really aimed at needs a server that is hung rather than merely slow, which is
    /// not honestly fixturable here; the elapsed bound below is a floor on "immediately", not the proof.</para>
    /// </summary>
    [Fact]
    [Trait("tier", "stdio")]
    public void AfterOneTimeoutEveryLaterCallFailsAtOnceAndNamesTheFirst()
    {
        using var f = new ServerFixture();

        f.RpcTimeout = TimeSpan.Zero;         // nothing can answer inside no time at all
        var firstFailure = Assert.Throws<TimeoutException>(() => f.Call(ToolNames.Check, "{}"));
        Assert.Contains("tools/call", firstFailure.Message, StringComparison.Ordinal);

        // Full budget restored, so nothing but the poison can make the next call fail fast.
        f.RpcTimeout = TimeSpan.FromSeconds(60);
        var sw = Stopwatch.StartNew();
        var later = Assert.Throws<InvalidOperationException>(() => f.Call(ToolNames.Check, "{}"));
        sw.Stop();

        Assert.Contains("poisoned", later.Message, StringComparison.Ordinal);
        Assert.Contains("tools/call", later.Message, StringComparison.Ordinal);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
                    $"the poisoned call took {sw.Elapsed.TotalSeconds:0.#}s — it waited a fresh deadline " +
                    "instead of failing on what it already knew");
    }
}

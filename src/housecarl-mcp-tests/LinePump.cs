using System.Collections.Concurrent;

namespace HousecarlMcpTests;

/// <summary>
/// One background reader draining a text stream into a queue, so that a caller which gives up waiting
/// cannot orphan a read.
///
/// <para>This exists because of the shape it replaces. Reading a line under a deadline as
/// <c>Task.Run(reader.ReadLine)</c> plus <c>Wait(remaining)</c> looks like a bounded read and is not one:
/// <see cref="TextReader.ReadLine"/> is not cancellable, so when the wait expires the read is still parked
/// on the shared stream and consumes the next line that arrives. In a shared stdio fixture that is not one
/// failed test — the accounting is one line behind from then on, every later call waits its whole deadline,
/// and the original slow call is indistinguishable from the wreckage it caused.</para>
///
/// <para>With exactly one reader on the stream, forever, there is no second read to orphan: a line that
/// arrives late is queued, and the caller it was meant for either takes it or skips it by id. The class of
/// defect is gone rather than handled.</para>
/// </summary>
public sealed class LinePump : IDisposable
{
    readonly BlockingCollection<string> _lines = new();
    readonly Task _pump;

    public LinePump(TextReader reader)
    {
        _pump = Task.Run(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line) _lines.Add(line);
            }
            catch
            {
                // The stream was torn down under us — the process was killed, or the collection stopped
                // accepting. That is the end of the pump, not a failure to report: the caller's own
                // deadline is what turns a dead transport into a test failure.
            }
            finally
            {
                try { _lines.CompleteAdding(); } catch { /* already completed or disposed */ }
            }
        });
    }

    /// <summary>
    /// The next queued line, or <c>false</c> if none arrived within <paramref name="timeout"/> — and also
    /// <c>false</c>, immediately, once the stream has ended and the queue is drained. A dead transport
    /// therefore fails fast instead of burning the caller's whole deadline.
    /// </summary>
    public bool TryTake(TimeSpan timeout, out string line)
    {
        line = "";
        if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
        try { return _lines.TryTake(out line!, timeout); }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }   // completed while this call was waiting
    }

    public void Dispose()
    {
        try { _lines.CompleteAdding(); } catch { /* already completed or disposed */ }
        try { _pump.Wait(TimeSpan.FromSeconds(2)); } catch { /* the pump swallows its own faults */ }
        _lines.Dispose();
    }
}

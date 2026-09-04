using System.Collections.Concurrent;

namespace HousecarlMcpTests;

/// <summary>One background reader draining a text stream into a queue, so a caller that gives up waiting
/// cannot orphan a read. <see cref="TextReader.ReadLine"/> is not cancellable, so a per-call timed read left
/// parked on a shared stream would swallow the next line and put every later caller one line behind; with
/// exactly one reader, a late line is simply queued for whoever wants it.</summary>
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

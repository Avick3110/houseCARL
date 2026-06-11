namespace HousecarlMcp;

/// <summary>
/// The last-line tool-body guard (Q3 hardening, HCBR-2026-06-11-01 follow-through). Every MCP tool body runs
/// inside <see cref="Tool(string,System.Func{string})"/>: an exception the body's own handling didn't convert to
/// a named error — the unguarded residue was the <see cref="LoadOrderService"/> resolver getter (freshness/IO
/// throws during a mid-call profile re-read) and the render layer's per-match reads — returns a NAMED error
/// string instead of escaping to the SDK, whose own catch genericizes to "An error occurred invoking '…'."
/// (the opaque dead end the binding shim exists to kill; see <see cref="ToolCallShim"/>).
///
/// Division of honesty with the shim: with every body wrapped HERE, the only exceptions still reaching the
/// shim's catch are pre-body (argument binding), so its "could not be bound" wording stays accurate — and this
/// guard's "the arguments bound fine" wording is accurate in turn, because a body only runs after binding
/// succeeded. Cancellation is rethrown — a cancelled request is the SDK's to finish, not a tool failure.
/// </summary>
internal static class Guard
{
    /// <summary>Rethrow ONLY a real request cancellation (the SDK's to finish), mirroring the SDK's own test —
    /// an OperationCanceledException whose REQUEST token is live (e.g. a TaskCanceledException from an internal
    /// HttpClient timeout) is a body FAILURE and must be named, or it lands in the SDK generic (review #1
    /// finding 1). Tools without a CancellationToken parameter pass none, so every body OCE there is named.</summary>
    public static string Tool(string tool, Func<string> body, CancellationToken ct = default)
    {
        try { return body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Named(tool, ex); }
    }

    /// <summary>The async twin (the Nexus tools).</summary>
    public static async Task<string> Tool(string tool, Func<Task<string>> body, CancellationToken ct = default)
    {
        try { return await body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Named(tool, ex); }
    }

    static string Named(string tool, Exception ex)
    {
        Console.Error.WriteLine($"[houseCARL] {tool} unhandled exception: {ex}");   // full stack → stderr (the MCP log), never stdout (the protocol channel)
        return $"error: {tool} failed unexpectedly — {ex.GetType().Name}: {Flatten(ex.Message)} This is an " +
               "internal houseCARL failure (the arguments bound fine), not bad input. Retry once — a transient " +
               "mid-refresh hiccup self-heals on the next call; if it persists, capture this exact message in a bug report.";
    }

    /// <summary>One-line, bounded essence of an exception message for the wire (System.Text.Json and IO messages
    /// span lines); the full exception, stack included, already went to stderr.</summary>
    internal static string Flatten(string message)
    {
        var s = message.Replace("\r", "").Replace("\n", " | ").Trim();
        if (s.Length > 0 && !s.EndsWith('.')) s += ".";
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

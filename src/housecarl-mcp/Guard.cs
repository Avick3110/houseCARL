namespace HousecarlMcp;

/// <summary>
/// The last-line tool-body guard: every MCP tool body runs inside <see cref="Tool(string,System.Func{string})"/>
/// so an unconverted exception returns a named error instead of escaping to the SDK, whose own catch genericizes
/// it to "An error occurred invoking '…'." Every body must stay wrapped — that is what keeps this guard's "the
/// arguments bound fine" wording true and leaves only pre-body binding failures for <see cref="ToolCallShim"/>.
/// </summary>
internal static class Guard
{
    /// <summary>Rethrow only a real request cancellation (the SDK's to finish). An OperationCanceledException
    /// whose request token is still live — e.g. an HttpClient timeout — is a body failure and must be named,
    /// or it lands in the SDK's generic message.</summary>
    public static string Tool(string tool, Func<string> body, CancellationToken ct = default)
    {
        try { return body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Named(tool, ex); }
    }

    /// <summary>The async twin, for tool bodies that await.</summary>
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

    /// <summary>Collapse an exception message to one bounded line for the wire (System.Text.Json and IO messages
    /// span lines); the full exception, stack included, already went to stderr.</summary>
    internal static string Flatten(string message)
    {
        var s = message.Replace("\r", "").Replace("\n", " | ").Trim();
        if (s.Length > 0 && !s.EndsWith('.')) s += ".";
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

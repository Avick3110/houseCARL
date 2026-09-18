namespace HousecarlMcp;

/// <summary>The last-line tool-body guard: every MCP tool body runs inside
/// <see cref="Tool(string,System.Func{string})"/> so an unconverted exception returns a named error.</summary>
internal static class Guard
{
    /// <summary>Rethrow only a real request cancellation; a cancel with a live request token is named as a body failure.</summary>
    public static string Tool(string tool, Func<string> body, CancellationToken ct = default)
    {
        try { return body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProfileUnreadableException ex) { return Transient(ex); }
        catch (Exception ex) { return Named(tool, ex); }
    }

    /// <summary>The async twin, for tool bodies that await.</summary>
    public static async Task<string> Tool(string tool, Func<Task<string>> body, CancellationToken ct = default)
    {
        try { return await body(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ProfileUnreadableException ex) { return Transient(ex); }
        catch (Exception ex) { return Named(tool, ex); }
    }

    /// <summary>A profile file locked by MO2 mid-re-sort is a named transient, not an internal failure.</summary>
    static string Transient(ProfileUnreadableException ex) => "error: " + ex.Message;

    static string Named(string tool, Exception ex)
    {
        Console.Error.WriteLine($"[houseCARL] {tool} unhandled exception: {ex}");   // full stack → stderr (the MCP log), never stdout (the protocol channel)
        return $"error: {tool} failed unexpectedly — {ex.GetType().Name}: {Flatten(ex.Message)} This is an " +
               "internal houseCARL failure (the arguments bound fine), not bad input. Retry once — a transient " +
               "mid-refresh hiccup self-heals on the next call; if it persists, capture this exact message in a bug report.";
    }

    /// <summary>Collapse an exception message to one bounded line for the wire.</summary>
    internal static string Flatten(string message)
    {
        var s = message.Replace("\r", "").Replace("\n", " | ").Trim();
        if (s.Length > 0 && !s.EndsWith('.')) s += ".";
        return s.Length > 300 ? s[..300] + "…" : s;
    }
}

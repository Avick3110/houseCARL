using HousecarlCore;
namespace HousecarlMcp;

/// <summary>The last-line tool-body guard: every MCP tool body runs inside
/// <see cref="Tool(string,System.Func{ToolCall,ToolAnswer},System.Threading.CancellationToken)"/>, which renders the body's
/// <see cref="ToolAnswer"/> once, so an unconverted exception returns a named error and a refusal carries <c>isError</c>;
/// the stay-wrapped contract is in <c>docs/architecture/tool-call-argument-shim.md</c>.</summary>
internal static class Guard
{
    /// <summary>Rethrow only a real request cancellation; a cancel with a live request token is named as a body failure.</summary>
    public static string Tool(string tool, Func<ToolCall, ToolAnswer> body, CancellationToken ct = default)
    {
        using var call = new ToolCall();
        try { var answer = body(call); return Render(Changed(call) ?? answer); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Render(Caught(tool, call, ex)); }
    }

    /// <summary>The async twin, for tool bodies that await.</summary>
    public static async Task<string> Tool(string tool, Func<ToolCall, Task<ToolAnswer>> body, CancellationToken ct = default)
    {
        using var call = new ToolCall();
        try { var answer = await body(call); return Render(Changed(call) ?? answer); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return Render(Caught(tool, call, ex)); }
    }

    /// <summary>The one render: the answer's text, and a refusal marked on the call's result.</summary>
    static string Render(ToolAnswer answer)
    {
        if (answer.Refused) CallVerdict.MarkRefused();
        return answer.Render();
    }

    /// <summary>The Guard's own refusals, each text on every transport.</summary>
    static ToolAnswer Caught(string tool, ToolCall call, Exception ex) => Changed(call) ?? ex switch
    {
        // a profile file locked by MO2 mid-re-sort is a named transient, not an internal failure
        ProfileUnreadableException or GameLanguageRefusedException => ToolAnswer.Refusal(Wire.RefusalPrefix + ex.Message),
        _ => ToolAnswer.Refusal(Named(tool, ex)),
    };

    /// <summary>The call's refusal when its views split across a profile change, or a plugin it relied on changed on disk mid-call, else null.</summary>
    static ToolAnswer? Changed(ToolCall call) =>
        (call.Context.Refusal(call.Mark) ?? call.Mark.Settle()) is { } why ? ToolAnswer.Refusal(Wire.RefusalPrefix + why) : null;

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

/// <summary>One tool call, opened by <see cref="Guard"/> around its body and handed to it; the call's own scopes live here.</summary>
internal sealed class ToolCall : IDisposable
{
    /// <summary>A plugin changed mid-call refuses the whole call.</summary>
    internal MidCallChange.Scope Mark { get; } = MidCallChange.Begin();

    /// <summary>The service binds its order's language and string winners to this call.</summary>
    readonly GameLanguage.Scope _language = GameLanguage.Begin();

    /// <summary>The call's one view of the load order, which the service's getters fill and serve.</summary>
    readonly CallContext.Scope _context = CallContext.Begin();

    /// <summary>The call's context.</summary>
    internal CallContext Context => _context.Context;

    public void Dispose() { _context.Dispose(); _language.Dispose(); Mark.Dispose(); }
}

/// <summary>Whether the call was refused, opened by the call-tool filter and set by <see cref="Guard"/>, so the result the
/// filter returns carries <c>isError</c>; outside a filter (a test calling a tool method) nothing is opened and nothing is set.</summary>
internal sealed class CallVerdict
{
    static readonly AsyncLocal<CallVerdict?> Current = new();

    /// <summary>True once the call's answer was a refusal.</summary>
    public bool Refused { get; private set; }

    /// <summary>Open the verdict for one call; the filter reads it after the call returns.</summary>
    public static CallVerdict Begin() => Current.Value = new CallVerdict();

    internal static void MarkRefused() { if (Current.Value is { } v) v.Refused = true; }
}

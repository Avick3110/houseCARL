// The per-call context: one load-order view per tool call; contract in docs/architecture/load-order-service.md.
namespace HousecarlMcp;

/// <summary>One tool call's view of the load order, opened by <see cref="ToolCall"/>: the record pin, its roots and
/// epoch, taken at the call's first record read, and the file view, taken only at its first file read. The service's
/// getters fill and serve it under their own lock; outside a call there is none and every getter refreshes.</summary>
internal sealed class CallContext
{
    static readonly AsyncLocal<CallContext?> Current = new();

    /// <summary>The call's record pin; null until its first record read.</summary>
    internal LoadOrderService.ViewPin? Pin { get; private set; }

    /// <summary>The four roots taken in the pin's hold.</summary>
    internal Mo2Roots Roots { get; private set; }

    /// <summary>The game language and string winners of the pinned build.</summary>
    internal InstanceText? Text { get; private set; }

    /// <summary>The call's file view; null until its first file read, so a records-only call never builds one.</summary>
    internal FileView? Files { get; private set; }

    /// <summary>The asset build a call holds once it first asks a file question, with what was read beside it.</summary>
    internal sealed record FileView(AssetResolver Resolver, IReadOnlyList<string> Warnings, IReadOnlyList<ActiveArchive> Archives,
                                    IReadOnlyList<string> EnabledMods, string ProfileName, Mo2Roots Roots, LoadOrderResolver? Order);

    /// <summary>Open the context for one call; a nested tool body shares the outer call's. Dispose ends it.</summary>
    internal static Scope Begin()
    {
        var outer = Current.Value;
        if (outer is null) Current.Value = new CallContext();
        return new Scope(Current.Value!, outer is null);
    }

    internal readonly struct Scope(CallContext context, bool owns) : IDisposable
    {
        /// <summary>The context this call reads through.</summary>
        public CallContext Context => context;

        public void Dispose() { if (owns) Current.Value = null; }
    }

    /// <summary>This call's context, or null outside a call.</summary>
    internal static CallContext? Open => Current.Value;

    /// <summary>Hold <paramref name="pin"/> for the rest of the call; a write's pin taken inside the write gate replaces an earlier one.</summary>
    internal void HoldPin(LoadOrderService.ViewPin pin, Mo2Roots roots, InstanceText? text) { Pin = pin; Roots = roots; Text = text; }

    /// <summary>Hold the file view for the rest of the call.</summary>
    internal void HoldFiles(FileView files) => Files = files;

    /// <summary>The call's refusal once its record view and file view were found to describe two profiles, else null.</summary>
    internal string? Refusal { get; private set; }

    /// <summary>Refuse the whole call, even where a lane folds the throw into a row.</summary>
    internal Exception Refuse() { Refusal = MovedBetweenViews; return new LoadOrderMovedMidCallException(); }

    /// <summary>The refusal when the MO2 profile moved between the call's record view and its file view.</summary>
    internal const string MovedBetweenViews =
        "the load order changed while this call was running (the MO2 profile changed between reading its records and " +
        "reading its files) — the response would describe two builds; nothing was answered or written. Retry the call.";
}

/// <summary>The load order moved between a call's record view and its file view, so the call refuses whole.</summary>
internal sealed class LoadOrderMovedMidCallException() : Exception(CallContext.MovedBetweenViews);

// The per-call context: one load-order view per tool call; contract in docs/architecture/load-order-service.md.
using HousecarlCore;
namespace HousecarlMcp;

/// <summary>One tool call's view of the load order, opened by <see cref="ToolCall"/>: the record pin, whose view carries
/// the epoch, taken at the call's first record read; the file view, taken only at its first file read; and the roots and
/// order the first of the two was taken against. The service's getters fill and serve it under their own lock; outside a
/// call there is none and every getter refreshes.</summary>
internal sealed class CallContext
{
    static readonly AsyncLocal<CallContext?> Current = new();

    /// <summary>The call's record pin; null until its first record read.</summary>
    internal LoadOrderService.ViewPin? Pin { get; private set; }

    /// <summary>The call's file view; null until its first file read, so a records-only call never builds one.</summary>
    internal FileView? Files { get; private set; }

    /// <summary>The roots and order the call's views were taken against; the order is null when files came first, before any index.</summary>
    internal (Mo2Roots Roots, LoadOrderResolver? Order)? Build { get; private set; }

    /// <summary>The call's roots, once it holds a view.</summary>
    internal Mo2Roots Roots => Build!.Value.Roots;

    /// <summary>The asset build a call holds once it first asks a file question, with what was read beside it.</summary>
    internal sealed record FileView(AssetResolver Resolver, IReadOnlyList<string> Warnings, IReadOnlyList<ActiveArchive> Archives,
                                    IReadOnlyList<string> EnabledMods, string ProfileName);

    /// <summary>Open the context for one call; a nested tool body shares the outer call's, as the mark and the language do. Dispose ends it.</summary>
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
    internal void HoldPin(LoadOrderService.ViewPin pin, Mo2Roots roots) { Pin = pin; Build = (roots, pin.Resolver); }

    /// <summary>Hold the file view for the rest of the call; the first view taken sets the build.</summary>
    internal void HoldFiles(FileView files, Mo2Roots roots, LoadOrderResolver? order) { Files = files; Build ??= (roots, order); }

    /// <summary>True once the call's two views were found to describe two builds.</summary>
    bool _split;

    /// <summary>Refuse the whole call, even where a lane folds the throw into a row.</summary>
    internal Exception Refuse() { _split = true; return new LoadOrderMovedMidCallException(); }

    /// <summary>The call's refusal once its views split, naming what it wrote if a write committed first; else null.</summary>
    internal string? Refusal(MidCallChange.Scope mark) => !_split ? null : mark.AfterWrites(Moved) ?? MovedBetweenViews;

    /// <summary>What moved when the roots or the order moved between the call's two views.</summary>
    const string Moved =
        "the load order changed while this call was running (the MO2 profile, or the plugins it enables, changed between " +
        "reading its records and reading its files)";

    /// <summary>The refusal when the views split before anything was written.</summary>
    internal const string MovedBetweenViews =
        Moved + " — the response would describe two builds; nothing was answered or written. Retry the call.";
}

/// <summary>The load order moved between a call's record view and its file view, so the call refuses whole.</summary>
internal sealed class LoadOrderMovedMidCallException() : Exception(CallContext.MovedBetweenViews);

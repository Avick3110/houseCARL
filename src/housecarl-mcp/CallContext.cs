// The per-call context: one load-order view per tool call; contract in docs/architecture/load-order-service.md.
using HousecarlCore;
namespace HousecarlMcp;

/// <summary>One tool call's view of the load order, opened by <see cref="ToolCall"/>: the record pin, whose view carries
/// the epoch, taken at the call's first record read; the file view, taken only at its first file read; and the build, the
/// roots and order each record pin sets, else the first file view's. The service's getters fill and serve it under their own lock; outside a
/// call there is none and every getter refreshes.</summary>
internal sealed class CallContext
{
    static readonly AsyncLocal<CallContext?> Current = new();

    /// <summary>The call's record pin, over a frozen copy of the resolver; null until its first record read.</summary>
    internal LoadOrderService.ViewPin? Pin { get; private set; }

    /// <summary>The call's file view; null until its first file read, so a records-only call never builds one.</summary>
    internal FileView? Files { get; private set; }

    /// <summary>The build the latest record pin was taken against, else the first file view's, whose order is null before any index.</summary>
    internal CallBuild? Build { get; private set; }

    /// <summary>The call's roots, once it holds a view.</summary>
    internal Mo2Roots Roots => Build!.Roots;

    /// <summary>True once a write took its view inside the write gate; every later read in the call serves it.</summary>
    internal bool WriteViewTaken { get; private set; }

    /// <summary>What a call's view was taken against: the roots, the live order (null before any index), the profile read
    /// behind it (null when none was kept) and the profile's name.</summary>
    internal sealed record CallBuild(Mo2Roots Roots, LoadOrderResolver? Order, Mo2Composition? Composition, string ProfileName);

    /// <summary>The asset build a call holds once it first asks a file question, with what was read beside it: a frozen
    /// copy, and the live build it was taken from.</summary>
    internal sealed record FileView(AssetResolver Resolver, AssetResolver Source, IReadOnlyList<string> Warnings, IReadOnlyList<ActiveArchive> Archives,
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
    internal void HoldPin(LoadOrderService.ViewPin pin, CallBuild build) { Pin = pin; Build = build; }

    /// <summary>Hold the file view for the rest of the call; it sets the build only when none is held.</summary>
    internal void HoldFiles(FileView files, CallBuild build) { Files = files; Build ??= build; }

    /// <summary>Let go of files a write's in-gate pin moved past, so its next file read takes the pin's build.</summary>
    internal void DropFiles() => Files = null;

    /// <summary>Mark the write's view taken; the gate holds it for the rest of the call.</summary>
    internal void TakeWriteView() => WriteViewTaken = true;

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

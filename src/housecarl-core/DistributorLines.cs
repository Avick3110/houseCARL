using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>What SPID's and KID's line readers share: the record of one form a line names, the line and read shapes the
/// layer's one reader and renderer take, and the one token resolve under a format's row. Each format's splitting and
/// reading stay its own (<see cref="SpidLines"/>, <see cref="KidLines"/>).</summary>
public static class DistributorLines
{
    /// <summary>One form a line names: where (target, filter or value), in which field or section and list, the token as
    /// written, the record, and whether it was named by EditorID.</summary>
    public sealed record Named(SkyPatcherOverlay.FormPlace Place, string Where, string Token, FormKey Form, bool ByEditorId);

    /// <summary>One line of a file as its format splits it: its number, its text, why the format reads no entry from it
    /// (null when it reads one), the notes the split owes, and the key its header counts it under.</summary>
    public interface ILine
    {
        int Number { get; }
        string Raw { get; }
        string? Skipped { get; }
        IReadOnlyList<string> Notes { get; }
        /// <summary>The header's count key: a group line's key, else its type.</summary>
        string HeaderKey { get; }
    }

    /// <summary>What one line names and what its format does with it: the forms that resolve, the plugins named as a
    /// term, the notes its tokens owe, and why the line is dropped (null when kept).</summary>
    public interface IRead
    {
        IReadOnlyList<Named> Forms { get; }
        IReadOnlyList<string> PluginTerms { get; }
        IReadOnlyList<string> Notes { get; }
        string? Dropped { get; }
    }

    /// <summary>One token resolved: the record it names, the plugin it names as a term (and whether the order holds it),
    /// why it names nothing, a note it owes even when it resolves (a load index written in), and whether it was a name.</summary>
    internal sealed record Resolved(FormKey? Form, string? Plugin, bool PluginPresent, string? Miss, string? Note, bool ByEditorId);

    /// <summary>Resolve one token under the format's row; <paramref name="loader"/> is the name its reports give the format.</summary>
    internal static Resolved Resolve(string raw, SkyPatcherOverlay.IFormResolver resolver, TargetSpelling s, string loader)
    {
        var read = RuntimeTargets.Read(raw, s);
        var fk = RuntimeTargets.Resolve(read, null, resolver, s);
        // A resolved address still owes the load-index note; a resolved name or runtime FormID owes nothing.
        var report = fk is null || read.Kind == RuntimeTargets.TargetKind.Address ? RuntimeTargets.Report(read, null, resolver, loader, s) : null;
        bool byName = read.Kind == RuntimeTargets.TargetKind.EditorId;
        if (read.Kind == RuntimeTargets.TargetKind.PluginName)
        {
            bool present = resolver.PluginPresent(read.Raw);
            return new Resolved(null, read.Raw, present, present ? null : report, null, false);
        }
        if (fk is not null) return new Resolved(fk, null, false, null, report, byName);
        return new Resolved(null, null, false, report ?? "names nothing", null, byName);
    }
}

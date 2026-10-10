using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The dialogue family's orchestration on the merged <c>check</c> surface: expand a seed list into
/// per-seed validations (core's <see cref="DialogueValidate"/>) and tally what they found. Seeded, not swept;
/// contract in docs/architecture/dialogue-validation.md.</summary>
internal static class DialogueSweep
{
    /// <summary>What this sweep needs off the load order, pinned to one build.</summary>
    /// <param name="Fold">the off-order plugin folded in, or null. Owned by the sweep, which closes it.</param>
    /// <param name="FoldError">why the fold could not be read; the sweep refuses on it.</param>
    internal readonly record struct Binding(Func<FormKey, DialogueValidationReport> Validate,
                                            Func<string?, FormKey> ParseFormId,
                                            string Epoch,
                                            DialogueFold? Fold = null,
                                            string? FoldError = null);

    /// <summary>Validate each seed and tally the result.</summary>
    /// <param name="bind">pins the build; called only once the seed list is non-empty, per <see cref="SweepSharedInput"/>.</param>
    /// <param name="seeds">the FormIDs the caller named. Null or empty refuses, never widens.</param>
    /// <param name="limit">how many seeds this call may expand; the response states the cut.</param>
    /// <param name="countsOnly">carry the totals and the unreachable-seed roster, and no topic blocks.</param>
    internal static DialogueCheckResult Run(Func<Binding> bind,
                                            IReadOnlyList<string>? seeds, int limit, bool countsOnly = false)
    {
        var named = (seeds ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (named.Length == 0) return DialogueCheckResult.Fail(CheckSentences.DialogueNeedsSeeds);

        var (validate, parseFormId, epoch, fold, foldError) = bind();
        using var _ = fold;                                   // the sweep owns the folded file for its own run
        if (foldError is not null) return DialogueCheckResult.Fail(foldError, epoch);

        var results = new List<DialogueSeedResult>();
        int topics = 0, problems = 0;
        bool readIncomplete = false;
        var rootFailures = new List<string>();

        foreach (var raw in named)
        {
            string seed = raw.Trim();
            if (results.Count >= limit) break;    // the seed budget; the accounting states the rest

            FormKey fk;
            try { fk = parseFormId(seed); }
            catch (Exception ex)
            {
                // A malformed seed is named and carried, never dropped — a discarded seed narrows the scope.
                results.Add(new DialogueSeedResult(seed, null, $"not a FormID ({ex.Message}) — expected 'XXXXXX:Plugin.esp'"));
                continue;
            }

            var report = validate(fk);
            if (report.CheckError is not null)
            {
                results.Add(new DialogueSeedResult(seed, null, $"the check did not finish — {report.CheckError}"));
                continue;
            }
            if (report.Error is not null)
            {
                results.Add(new DialogueSeedResult(seed, null, report.Error));
                continue;
            }

            results.Add(new DialogueSeedResult(seed, report, null));
            topics += report.Topics.Count;
            problems += Problems(report);
            readIncomplete |= report.ReadIncomplete;
            rootFailures.AddRange(report.RootFailures);
        }

        // The placement is the FOLD's own spelling, shared with the info_order form.
        string? folded = fold is null ? null
                       : string.Format(CheckSentences.DialogueFolded, fold.Plugin, fold.Where, fold.Placement)
                         + (fold.PlacementKind == DialogueFold.Where3.ActiveSlot
                                ? CheckSentences.DialogueFoldedShadowBound : "");

        // Every seed was malformed or unresolvable: one refusal rather than a section of nothing.
        if (results.Count > 0 && results.All(r => r.Report is null))
            return DialogueCheckResult.Fail(string.Format(CheckSentences.DialogueNoSeedResolved, results.Count,
                string.Join(" ", results.Select(r => $"{r.Seed}: {r.Refusal}.")),
                fold is null ? CheckSentences.DialogueNoSeedResolvedPlain : CheckSentences.DialogueNoSeedResolvedFolded),
                epoch) with { Folded = folded };

        return new DialogueCheckResult(results, topics, problems, readIncomplete, Limit: limit,
                                       SeedsNamed: named.Length, CountsOnly: countsOnly, Epoch: epoch,
                                       // Every seed's list, not the last one's; CheckOutcome de-dupes and orders them.
                                       RootFailures: rootFailures)
            { Folded = folded };
    }

    /// <summary>Every finding one report carries, counted off the report rather than off what rendered.</summary>
    static int Problems(DialogueValidationReport r) => Findings(r).Count();

    /// <summary>One dialogue finding in the artifact's row shape; <see cref="Findings"/> is the only list of them.
    /// Its detail and script are read off <paramref name="Source"/> on demand, so counting formats nothing.</summary>
    internal readonly record struct Finding(string Class, string? Plugin, FormKey Record, string? EditorId,
                                            string? RecordType, string? Target, object Source)
    {
        internal string Detail => Source switch
        {
            DialogueIssue i => i.Message,
            SeqLintFinding s => DialogueWire.SeqVerdict(s),
            VoiceLine l => DialogueWire.SilentVerdict(l),
            ScriptBindingFinding f => f.Detail,
            _ => (string)Source,
        };

        /// <summary>The script class whose .pex <see cref="Target"/> names, mapped back from <c>Scripts\A\B.pex</c>.</summary>
        internal string? Script => Source is ScriptBindingFinding && Target is { Length: > 12 } pex
            ? pex[8..^4].Replace('\\', ':').Replace('/', ':')
            : null;
    }

    /// <summary>Every finding one report carries, in report order: the count and the <c>to_file=</c> rows both read
    /// this. An INFO row's plugin and EditorID are null: the report carries only its topic's.</summary>
    internal static IEnumerable<Finding> Findings(DialogueValidationReport r)
    {
        foreach (var i in r.InputIssues)
            yield return new(Severity(i), r.InputWinnerPlugin, r.Input, r.InputEditorId, InputSignature(r.InputKind), null, i);
        // The coverage gaps count too: "0 findings" over a report that lost a plugin reads as a clean pass.
        foreach (var gap in r.ScanGaps)
            yield return new("scan_error", r.InputWinnerPlugin, r.Input, r.InputEditorId, InputSignature(r.InputKind), null, gap);
        if (DialogueWire.SeqIsFinding(r.SeqLint))
        {
            string seqPlugin = DialogueWire.SeqPlugin(r.SeqLint!);
            yield return new("seq_unconfirmed", seqPlugin, r.Input, r.InputEditorId, "QUST", "SEQ/" + seqPlugin + ".seq", r.SeqLint!);
        }
        foreach (var t in r.Topics)
        {
            foreach (var i in t.Issues)
                yield return new(Severity(i), t.WinnerPlugin, t.Topic, t.TopicEditorId, "DIAL", null, i);
            foreach (var l in t.VoiceLines.Where(l => !l.FuzPresent))
                yield return new("silent_line", null, l.Info, null, "INFO", l.FuzPath, l);
            foreach (var f in t.ScriptFindings)
            {
                if (f.Status == ScriptBindingStatus.BindingIncomplete)
                    yield return new("binding_incomplete", null, f.Info, null, "INFO", null, f);
                // One row per missing .pex, so each row names one file and one script.
                else if (f.Status == ScriptBindingStatus.ScriptNotCompiled)
                    foreach (var pex in f.MissingPex)
                        yield return new("script_not_compiled", null, f.Info, null, "INFO", pex, f);
            }
        }
    }

    static string Severity(DialogueIssue i) => i.Severity.ToString().ToLowerInvariant();

    static string? InputSignature(string kind) => kind switch
    {
        "quest" => "QUST", "topic" => "DIAL", "view" => "DLVW", "branch" => "DLBR", _ => null,
    };
}

using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The dialogue family's orchestration on the merged <c>check</c> surface: expand a seed list into
/// per-seed validations (core's <see cref="DialogueValidate"/>) and tally what they found. Seeded, not swept;
/// contract in docs/architecture/dialogue.md.</summary>
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
        if (named.Length == 0) return DialogueCheckResult.Fail(ReadSentences.DialogueNeedsSeeds);

        var (validate, parseFormId, epoch, fold, foldError) = bind();
        using var _ = fold;                                   // the sweep owns the folded file for its own run
        if (foldError is not null) return DialogueCheckResult.Fail(foldError, epoch);

        var results = new List<DialogueSeedResult>();
        int topics = 0, problems = 0;
        bool readIncomplete = false;

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
        }

        // The placement is the FOLD's own spelling, shared with the info_order form.
        string? folded = fold is null ? null
                       : string.Format(ReadSentences.DialogueFolded, fold.Plugin, fold.Where, fold.Placement)
                         + (fold.PlacementKind == DialogueFold.Where3.ActiveSlot
                                ? ReadSentences.DialogueFoldedShadowBound : "");

        // Every seed was malformed or unresolvable: one refusal rather than a section of nothing.
        if (results.Count > 0 && results.All(r => r.Report is null))
            return DialogueCheckResult.Fail(string.Format(ReadSentences.DialogueNoSeedResolved, results.Count,
                string.Join(" ", results.Select(r => $"{r.Seed}: {r.Refusal}.")),
                fold is null ? ReadSentences.DialogueNoSeedResolvedPlain : ReadSentences.DialogueNoSeedResolvedFolded),
                epoch) with { Folded = folded };

        return new DialogueCheckResult(results, topics, problems, readIncomplete, Limit: limit,
                                       SeedsNamed: named.Length, CountsOnly: countsOnly, Epoch: epoch)
            { Folded = folded };
    }

    /// <summary>Every finding one report carries, counted off the report rather than off what rendered.</summary>
    static int Problems(DialogueValidationReport r)
    {
        // The coverage gaps count too: "0 findings" over a report that lost a plugin reads as a clean pass.
        int n = r.InputIssues.Count + r.ScanGaps.Count;
        if (r.SeqLint is { QuestIsSge: true } s && !(s.SeqExists && s.SeqContainsQuest == true && s.SeqNewerThanPlugin == true))
            n++;
        foreach (var t in r.Topics)
        {
            n += t.Issues.Count;
            n += t.VoiceLines.Count(l => !l.FuzPresent);
            n += t.ScriptFindings.Count(f => f.Status is ScriptBindingStatus.ScriptNotCompiled
                                                      or ScriptBindingStatus.BindingIncomplete);
        }
        return n;
    }
}

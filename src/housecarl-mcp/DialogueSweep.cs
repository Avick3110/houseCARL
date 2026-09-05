using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>The dialogue family's orchestration on the merged <c>check</c> surface: expand a seed list into
/// per-seed validations (core's <see cref="DialogueValidate"/>) and tally what they found. Selection is by record,
/// not by plugin — a quest expands into every topic it owns and each topic's contributing plugins — so
/// <c>plugins=</c> and <c>exclude=</c> do not scope this family, and the response says so.</summary>
internal static class DialogueSweep
{
    /// <summary>Validate each seed and tally the result.</summary>
    /// <param name="validate">the per-seed validation — the service's own <c>ValidateDialogue</c>, passed in so this
    /// class needs nothing of the service but the one call it makes.</param>
    /// <param name="seeds">the FormIDs the caller named. Null or empty refuses, never widens: an empty scope read as
    /// "the whole order" would run a whole-order dialogue sweep.</param>
    /// <param name="limit">how many seeds this call may expand. Over it, the extra seeds are not validated and the
    /// response states how many and which knob moves them.</param>
    /// <param name="countsOnly">carry the totals and the unreachable-seed roster, and no topic blocks.</param>
    /// <param name="epoch">the record build every seed was validated against, stamped onto the result — and onto the
    /// refusals too, as the sibling families stamp theirs: the caller pins the view before this runs, so a refusal
    /// decided here still names the build it was decided against.</param>
    internal static DialogueCheckResult Run(Func<FormKey, DialogueValidationReport> validate,
                                            Func<string?, FormKey> parseFormId,
                                            IReadOnlyList<string>? seeds, int limit, bool countsOnly = false,
                                            string? epoch = null)
    {
        var named = (seeds ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (named.Length == 0) return DialogueCheckResult.Fail(ReadSentences.DialogueNeedsSeeds, epoch);

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
                // A malformed seed is named and carried, never dropped: the scope is the seed list, so a discarded
                // seed silently narrows it and the caller reads the result as a clean answer.
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

        // Every seed named was malformed or unresolvable: there is nothing to render and nothing to claim, so the
        // family answers with one refusal rather than a section of nothing.
        if (results.Count > 0 && results.All(r => r.Report is null))
            return DialogueCheckResult.Fail(string.Format(ReadSentences.DialogueNoSeedResolved, results.Count,
                string.Join(" ", results.Select(r => $"{r.Seed}: {r.Refusal}."))), epoch);

        return new DialogueCheckResult(results, topics, problems, readIncomplete, Limit: limit,
                                       SeedsNamed: named.Length, CountsOnly: countsOnly, Epoch: epoch);
    }

    /// <summary>Every finding one report carries, at both levels. Counted off the report rather than off what
    /// rendered, because the accounting subtracts from these totals.</summary>
    static int Problems(DialogueValidationReport r)
    {
        // The coverage gaps count too: they are not parity findings, but a response headlining "0 findings" over a
        // report that lost a plugin reads as a clean pass.
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

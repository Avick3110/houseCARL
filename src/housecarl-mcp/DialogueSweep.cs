using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// The DIALOGUE family's own orchestration on the merged <c>check</c> surface: expand a seed list into validations
/// and tally what they found (SPEC §6.1, classes 1-7).
///
/// <para><b>Its own file, because it is its own thing.</b> The per-seed validation is core's
/// (<see cref="DialogueValidate"/>); what lives here is the family's selection grammar — the seed parse, the
/// cost-refusal, the seed budget and the tally the accounting subtracts against. Hanging that off
/// <c>LoadOrderService</c> is what CLAUDE.md §8's don't-append-a-new-domain rule names, and the service keeps only
/// the thin call.</para>
///
/// <para><b>Seeds, not a sweep</b> (SPEC §6.1 F1.1). This family does not take the other families' plugin scope: a
/// dialogue validation expands a QUEST into every topic it owns and walks each topic's contributing plugins, so its
/// unit of selection is a record, not a plugin. Which is also why <c>plugins=</c> and <c>exclude=</c> do not scope
/// it, and why the response says so rather than letting a caller read a seeded answer as a scoped one.</para>
/// </summary>
internal static class DialogueSweep
{
    /// <summary>Validate each seed and tally the result.</summary>
    /// <param name="validate">the per-seed validation — the service's own <c>ValidateDialogue</c>, passed in so this
    /// class needs nothing of the service but the one call it makes.</param>
    /// <param name="seeds">the FormIDs the caller named. Null or empty is the COST-REFUSAL, never a widening: an
    /// empty scope resolved to "the whole order" would be the whole-order dialogue sweep F1.2 refuses.</param>
    /// <param name="limit">how many seeds this call may expand. Over it, the extra seeds are not validated and the
    /// response states how many and which knob moves them.</param>
    /// <param name="countsOnly">carry the totals and the unreachable-seed roster, and no topic blocks.</param>
    internal static DialogueCheckResult Run(Func<FormKey, DialogueValidationReport> validate,
                                            IReadOnlyList<string>? seeds, int limit, bool countsOnly = false)
    {
        var named = (seeds ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (named.Length == 0) return DialogueCheckResult.Fail(ReadSentences.DialogueNeedsSeeds);

        var results = new List<DialogueSeedResult>();
        int topics = 0, problems = 0;
        bool readIncomplete = false;

        foreach (var raw in named)
        {
            string seed = raw.Trim();
            if (results.Count >= limit) break;    // the seed budget; the accounting states the rest

            FormKey fk;
            try { fk = FormKey.Factory(seed); }
            catch (Exception ex)
            {
                // A malformed seed is NAMED and carried, never dropped: the family's scope IS the seed list, so a
                // discarded seed is a silently narrowed scope the caller would read as a clean answer (Q3).
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
                string.Join(" ", results.Select(r => $"{r.Seed}: {r.Refusal}."))));

        return new DialogueCheckResult(results, topics, problems, readIncomplete, Limit: limit,
                                       SeedsNamed: named.Length, CountsOnly: countsOnly);
    }

    /// <summary>What one report FOUND, in the units the accounting subtracts against: every finding this family
    /// reports, at both levels. Counted off the report rather than off what rendered, because the accounting's whole
    /// construction is a subtraction from the sweep's own totals.</summary>
    static int Problems(DialogueValidationReport r)
    {
        int n = r.InputIssues.Count;
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

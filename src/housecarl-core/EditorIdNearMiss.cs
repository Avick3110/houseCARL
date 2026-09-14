using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>
/// The near-miss hint for a scan that asked for an exact EditorID and matched nothing.
///
/// <para>The winner lane of a records scan filters on the LOAD-ORDER WINNER's body, so a record whose winner
/// RENAMES it is invisible to <c>editorid = &lt;the old name&gt;</c>: the name the caller typed is real, it is
/// simply carried by a losing copy. That reads as a clean "0 matches", which is the one answer the scan must not
/// leave standing unexplained (Requiem renaming <c>ArmorIronCuirass</c> to <c>REQ_Heavy_Iron_Body</c> is the case
/// this exists for, #669).</para>
///
/// <para><b>One sentence, and only a real one.</b> The look runs ONLY where that cause is the only one available
/// — the scan's own gate holds it to a zero-row, types=-bounded winner-lane scan whose <c>where=</c> is nothing
/// but the exact <c>editorid =</c> term — it reads the EDID header and nothing else, and it stops at the FIRST
/// losing copy carrying the name. No candidate, no sentence: the plain zero-row result stands as it did.</para>
///
/// <para>The walk is taken one plugin at a time so a plugin that indexed but will not open NOW skips rather than
/// ending the stream, which is how the scan lane treats the same fault — the hint must not switch itself off for
/// a whole order because one file moved.</para>
/// </summary>
public static class EditorIdNearMiss
{
    /// <summary>The record copies the look will read a header off before giving up. The caller's gate already
    /// bounds the walk to the scan's own types= scope, so this is the backstop for an unusually broad type set,
    /// not the normal stop.</summary>
    public const int Budget = 400_000;

    /// <summary>The sentence, or null when nothing was found within the budget. <paramref name="wanted"/> is the
    /// operand of the exact <c>editorid =</c> term; <paramref name="getterTypes"/> is the scan's own type scope,
    /// so the look reads exactly the records the scan read.</summary>
    public static string? Sentence(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                                   IReadOnlyList<Type>? getterTypes, string wanted, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        int seen = 0;
        using var session = resolver.OpenSession();
        // In load order, so the base game's own copy — the likeliest carrier of a renamed record's original name —
        // ends the walk early.
        foreach (var plugin in view.ScannablePluginNames)
        {
            var one = new[] { plugin };
            try
            {
                foreach (var (fk, depth, body, source) in view.RecordsIn(one, getterTypes))
                {
                    if (ct.IsCancellationRequested) return null;
                    if (++seen > Budget) return null;
                    if (depth <= 1) continue;                      // only one plugin provides it, so its copy IS the winner the scan already read
                    if (!string.Equals(body.EditorID, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    if (view.ResolveWinner(fk) is not { } w) continue;
                    if (string.Equals(w.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase)) continue;   // this IS the winner — the scan saw this name and judged it
                    // A winner body that will not fetch makes THIS candidate unjudgeable, not the walk: the next
                    // copy carrying the name may be the rename the caller is asking about.
                    var winnerBody = view.GetRecord(session, w.WinnerPlugin, fk);
                    if (winnerBody is null) continue;
                    var winnerEid = winnerBody.EditorID;
                    if (winnerEid is null)
                        return $"near miss: {source} carries '{wanted}' at {FormIdToken.Of(fk)}, but {w.WinnerPlugin} wins that record and gives it NO EditorID, "
                             + $"so no editorid= value reaches it — ask by FormID, or scope the scan with plugins=[\"{source}\"].";
                    if (string.Equals(winnerEid, wanted, StringComparison.OrdinalIgnoreCase)) continue;        // same name at the top — not the rename this is for
                    return $"near miss: {source} carries '{wanted}' at {FormIdToken.Of(fk)}, but {w.WinnerPlugin} wins that record and names it "
                         + $"'{winnerEid}' — editorid= is matched against the winner, so ask for that name, or scope the scan with plugins=[\"{source}\"].";
                }
            }
            // A plugin that indexed but will not open now is already named in the scan's own coverage gap; the rest
            // of the order still answers. Any other fault on one plugin is skipped on the same reasoning: a hint is
            // never worth failing the answer it rides on.
            catch (Exception) { continue; }
        }
        return null;
    }
}

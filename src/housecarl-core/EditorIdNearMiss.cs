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
/// <para><b>One sentence, and only a real one.</b> The look runs ONLY on a zero-row winner-lane scan whose
/// predicate set carries an exact <c>editorid =</c> term, it reads the EDID header and nothing else, it stops at
/// the FIRST losing copy carrying the name, and it gives up — saying nothing at all — past
/// <see cref="Budget"/> record copies. No candidate, no sentence: the plain zero-row result stands as it did.</para>
/// </summary>
public static class EditorIdNearMiss
{
    /// <summary>The record copies the look will read a header off before giving up. The scope is already bounded
    /// (a where= scan must name types= or plugins=), so this is the backstop for an unusually broad one, not the
    /// normal stop.</summary>
    public const int Budget = 400_000;

    /// <summary>The sentence, or null when nothing was found within the budget. <paramref name="wanted"/> is the
    /// operand of the exact <c>editorid =</c> term; <paramref name="getterTypes"/> is the scan's own type scope,
    /// so the look reads exactly the records the scan read.</summary>
    public static string? Sentence(LoadOrderResolver resolver, LoadOrderResolver.IndexView view,
                                   IReadOnlyList<Type>? getterTypes, string wanted, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        int seen = 0;
        try
        {
            // Every copy of the scoped types, in load order, so the base game's own definition — the likeliest
            // carrier of a renamed record's original name — ends the walk early.
            foreach (var (fk, depth, body, source) in view.RecordsIn(Array.Empty<string>(), getterTypes))
            {
                if (ct.IsCancellationRequested) return null;
                if (++seen > Budget) return null;
                if (depth <= 1) continue;                       // only one plugin provides it, so its copy IS the winner the scan already read
                if (!string.Equals(body.EditorID, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                if (view.ResolveWinner(fk) is not { } w) continue;
                if (string.Equals(w.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase)) continue;   // this IS the winner — the scan saw this name and judged it
                using var session = resolver.OpenSession();
                var winnerBody = view.GetRecord(session, w.WinnerPlugin, fk);
                if (winnerBody is null) return null;
                var winnerEid = winnerBody.EditorID;
                if (string.Equals(winnerEid, wanted, StringComparison.OrdinalIgnoreCase)) return null;     // same name at the top — the miss is not a rename
                return $"near miss: {source} defines '{wanted}' at {FormIdToken.Of(fk)}, but {w.WinnerPlugin} wins that record and names it "
                     + $"'{winnerEid ?? "(no EditorID)"}' — editorid= is matched against the winner, so ask for that name, or scope the scan with plugins=[\"{source}\"].";
            }
        }
        // A hint is never worth failing the answer it rides on: a plugin that will not open here is already
        // reported by the scan itself, and anything else leaves the zero-row result exactly as it was.
        catch (Exception) { return null; }
        return null;
    }
}

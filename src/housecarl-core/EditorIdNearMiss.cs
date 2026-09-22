using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>The near-miss hint for a scan that asked for an exact EditorID and matched nothing: a record whose winner
/// renames it is invisible to <c>editorid = &lt;the old name&gt;</c>. Gate and walk contracts in
/// docs/architecture/check-scripts-and-dialogue-families.md.</summary>
public static class EditorIdNearMiss
{
    /// <summary>The record copies the look will read a header off before giving up — the backstop for an unusually
    /// broad type set, not the normal stop.</summary>
    public const int Budget = 400_000;

    /// <summary>The sentence, or null when nothing was found within the budget. <paramref name="getterTypes"/> is the
    /// scan's own type scope, so the look reads exactly the records the scan read.</summary>
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
                    ct.ThrowIfCancellationRequested();   // a client that aborted stops the walk, as it stops the scan
                    if (++seen > Budget) return null;
                    if (depth <= 1) continue;                      // only one plugin provides it, so its copy IS the winner the scan already read
                    if (!string.Equals(body.EditorID, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    if (view.ResolveWinner(fk) is not { } w) continue;
                    if (string.Equals(w.WinnerPlugin, source, StringComparison.OrdinalIgnoreCase)) continue;   // this IS the winner — the scan saw this name and judged it
                    // A winner body that will not fetch skips this candidate, not the walk. The candidate's own getter
                    // type is passed so the winner body is sought in that record's GRUP rather than by a flat walk (#354).
                    var winnerBody = view.GetRecord(session, w.WinnerPlugin, fk,
                                                    getterTypes?.FirstOrDefault(t => t.IsInstanceOfType(body)));
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
            // Out of memory is the machine's fault, not this plugin's: it leaves by the same door the body gather does.
            catch (OutOfMemoryException) { throw; }
            // A cancelled call is the caller's answer, not a fault to absorb.
            catch (OperationCanceledException) { throw; }
            // Any other fault on one plugin is skipped; the rest of the order still answers.
            catch (Exception) { continue; }
        }
        return null;
    }
}

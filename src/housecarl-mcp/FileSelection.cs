using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>One entry of a file selection: the path, and on a <c>formids=</c> row the NPC, the slot, the other half's path and the face door's line for a template root. <c>Error</c> answers as its own row.</summary>
internal readonly record struct SelectedFile(string Path, string? FormId, FaceGenSlot? Slot, string? PairPath, string? Error = null, string? FaceNote = null);

/// <summary>A file selection: the entries in order, what each selector had to say, the provider every row is read
/// against, and the refusal (a provider the order does not load) under which nothing was selected. <see cref="OverBound"/>
/// says the selection crossed the path bound and stopped there, so <see cref="Entries"/> is a partial count.</summary>
internal sealed record FileSelectionResult(IReadOnlyList<SelectedFile> Entries, IReadOnlyList<string> Notes, string? Provider,
                                           string? Refusal = null, bool OverBound = false);

/// <summary>The file selectors asset_status takes (explicit Data paths, <c>under=</c> folders and globs, <c>formids=</c>
/// FaceGen pairs, <c>provider=</c>), read through the one view the caller captured, so every tool that takes them
/// selects the same files with the same notes and the same refusal.</summary>
internal static class FileSelection
{
    /// <summary>The selection over <paramref name="view"/>: explicit paths first, never deduped; then each seed's pair
    /// halves from <paramref name="faces"/> (or a row saying why it has none, <paramref name="noOrder"/> when the order
    /// could not be read); then each <c>under=</c> selector's matches, deduped, or the whole provider when no other
    /// selector is given. <paramref name="maxPaths"/> bounds the selection (null: no bound); past it the enumeration
    /// stops and <see cref="FileSelectionResult.OverBound"/> is set.</summary>
    internal static FileSelectionResult Select(
        AssetResolver.AssetView view,
        IReadOnlyList<string> relPaths,
        IReadOnlyList<string>? under,
        IReadOnlyList<FaceGenSeed>? seeds,
        IReadOnlyDictionary<FormKey, NpcFaceAnswer>? faces,
        string? noOrder,
        string? provider,
        int? maxPaths)
    {
        provider = provider?.Trim() is { Length: > 0 } named ? named : null;
        // A provider the build does not have would walk nothing and read every row as not provided, so it is refused by name.
        if (provider is not null && !view.HasProvider(provider))
            return new FileSelectionResult(Array.Empty<SelectedFile>(), Array.Empty<string>(), provider,
                                           Refusal: $"error: provider '{provider}' is not in the active load order — name an " +
                                                    "enabled mod folder, 'overwrite', 'Data' or an active archive's filename, " +
                                                    "as a row's providers line spells it.");
        var notes = new List<string>();
        var selected = new List<SelectedFile>(relPaths.Count);
        foreach (var p in relPaths) selected.Add(new SelectedFile(p ?? "", null, null, null));   // explicit paths first, in the order given, never deduped
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in relPaths)
            try { seen.Add(AssetResolver.ValidateRelPath((p ?? "").Trim())); } catch (ArgumentException) { /* a bad explicit path answers per-path below */ }

        // formids=: each NPC contributes BOTH halves of every pair the face door names (its own, or each template
        // root's), or one row per reason it has none.
        foreach (var seed in seeds ?? Array.Empty<FaceGenSeed>())
        {
            if (seed.Error is not null || seed.Key is not { } fk)
            {
                selected.Add(new SelectedFile(seed.Token, seed.Token, null, null, seed.Error));
                continue;
            }
            if (faces?.GetValueOrDefault(fk) is not { } face)
            {
                selected.Add(new SelectedFile(seed.Token, seed.Token, null, null, NpcFaceAnswer.NoPair(noOrder!)));
                continue;
            }
            foreach (var why in face.Reasons()) selected.Add(new SelectedFile(seed.Token, seed.Token, null, null, NpcFaceAnswer.NoPair(why)));
            foreach (var root in face.Roots)
            {
                if (root.Reason is not null) continue;
                var note = face.NoteFor(root);
                selected.Add(new SelectedFile(root.Mesh!, seed.Token, FaceGenSlot.Mesh, root.Tint, FaceNote: note));
                selected.Add(new SelectedFile(root.Tint!, seed.Token, FaceGenSlot.Tint, root.Mesh, FaceNote: note));
                seen.Add(root.Mesh!);
                seen.Add(root.Tint!);
            }
        }

        bool overBound = false;
        // A provider with no other selector walks everything it ships: '**', narrowed to that provider.
        bool wholeProvider = provider is not null && under is not { Count: > 0 } && relPaths.Count == 0 && seeds is not { Count: > 0 };
        // The walk's own count of what MO2 hid in scope, never a guess from the selector's spelling.
        string NoMatchNote(string sel, HiddenTally hidden)
        {
            if (wholeProvider)
                return $"provider '{provider}' provides no file in the active load order"
                     + (hidden.Sentence("of its paths", "of its paths") is { } all ? $" — {all}." : ".");
            var scope = provider is null ? "the active load order" : $"provider '{provider}'";
            var why = hidden.Sentence() is { } h
                ? $" — {h}."
                : " — check the spelling, or nothing enabled provides that folder.";
            return $"under '{sel}' matched no file in {scope}{why}" + (hidden.Count > 0 ? "" : AssetGlob.BareNameHint(view, sel, provider));
        }
        foreach (var raw in wholeProvider ? new[] { "**" } : under ?? Array.Empty<string>())
        {
            if (overBound) break;
            var sel = (raw ?? "").Trim();
            if (sel.Length == 0) { notes.Add("under: an empty selector was skipped — pass a Data-relative directory or glob."); continue; }
            try
            {
                // One past what is left of the budget: a selector that fills it has proved the selection is over. 0 = no cap.
                int room = maxPaths is { } max ? Math.Max(max - selected.Count, 0) + 1 : 0;
                var hidden = new HiddenTally();
                var matched = AssetGlob.Select(view, sel, out var namedOneFile, room, out var stopped, provider, hidden);
                overBound |= stopped;
                // A selector that named a FILE is said out loud too, so the sweep's own count is explained.
                if (namedOneFile)
                    notes.Add($"under '{sel}' names a file, not a directory — it was resolved as that one path.");
                // A selector that matched nothing is said out loud: silent, a typo would read as a clean sweep.
                else if (matched.Count == 0)
                    notes.Add(NoMatchNote(sel, hidden));
                // A provider's whole census says what of it MO2 hides, which no row carries.
                else if (wholeProvider && hidden.Sentence("of its paths", "of its paths") is { } some)
                    notes.Add($"provider '{provider}': {some}; they are left out of this listing.");
                foreach (var m in matched) if (seen.Add(m)) selected.Add(new SelectedFile(m, null, null, null));
            }
            catch (ArgumentException ex) { notes.Add($"under '{sel}': {ex.Message}"); }
        }
        return new FileSelectionResult(selected, notes, provider, OverBound: overBound);
    }
}

using HousecarlCore;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>One entry of a file selection: the path, and on a <c>formids=</c> row the NPC, the slot, the other half's path and the face door's line for a template root. <c>Error</c> answers as its own row. <c>Given</c> is an explicit path's index in the list given, -1 on any other row.</summary>
internal readonly record struct SelectedFile(string Path, string? FormId, FaceGenSlot? Slot, string? PairPath, string? Error = null, string? FaceNote = null,
                                             int Given = -1);

/// <summary>What a file selection reads: the call's asset capture, the <c>formids=</c> seeds it was taken for and,
/// when they name an NPC, the face door's answers over the pin taken with it, or why the load order could not be read.</summary>
internal sealed record SelectionCapture(AssetCapture Captured, IReadOnlyList<FaceGenSeed>? Seeds,
                                        IReadOnlyDictionary<FormKey, NpcFaceAnswer>? Faces, string? NoOrder);

/// <summary>A file selection: the entries in order, what each selector had to say, the provider every row is read
/// against, and the refusal (a provider the order does not load) under which nothing was selected. <see cref="StoppedAt"/>
/// is set when the path bound stopped the enumeration: the stop's count, which can exceed the deduped entries (assets.md, "Selecting a set of paths").</summary>
internal sealed record FileSelectionResult(IReadOnlyList<SelectedFile> Entries, IReadOnlyList<string> Notes, string? Provider,
                                           string? Refusal = null, int? StoppedAt = null);

/// <summary>The file selectors asset_status takes (explicit Data paths, <c>under=</c> folders and globs, <c>formids=</c>
/// FaceGen pairs, <c>provider=</c>), read through the one view the caller captured, so every tool that takes them
/// selects the same files with the same notes and the same refusal.</summary>
internal static class FileSelection
{
    /// <summary>A caller's paths= entries in the order given, files on disk among them, less each Data path
    /// <c>exclude=</c> removed from <paramref name="kept"/>, the selection over those Data paths in order.</summary>
    internal static IEnumerable<T> KeptAsGiven<T>(IEnumerable<T> entries, Func<T, bool> onDisk, IEnumerable<SelectedFile> kept)
    {
        var keep = kept.Where(f => f.Given >= 0).Select(f => f.Given).ToHashSet();
        int next = 0;
        foreach (var e in entries)
            if (onDisk(e) || keep.Contains(next++)) yield return e;
    }

    /// <summary>The refusal for a selection the path bound stopped, in the caller's words: what was not done, and a narrower selector to try.</summary>
    internal static string BoundRefusal(int stoppedAt, int max, string notDone, string example) =>
        $"error: the selection names at least {stoppedAt:N0} paths, past the {max:N0}-path bound one " +
        $"call is given, so nothing was {notDone} — narrow it with a tighter under= selector " +
        $"(e.g. '{example}') or fewer paths= entries.";

    /// <summary>The selection over <paramref name="capture"/>'s view: explicit paths first, never deduped; then each of
    /// its seeds' pair halves from its faces (or a row saying why it has none); then each <c>under=</c> selector's matches,
    /// deduped, or the whole provider when no other selector is given. <paramref name="maxPaths"/> (null: none) bounds
    /// only that <c>under=</c> or whole-provider enumeration, which stops once the selection would cross it and sets
    /// <see cref="FileSelectionResult.StoppedAt"/>; explicit and seed rows are never cut, only leave it less room. The
    /// caller words the stop. <paramref name="filesOnDisk"/>: the caller reads files on disk beside the selection, which
    /// are explicit paths to the whole-provider test. <paramref name="exclude"/> then removes what it names from every
    /// row, an explicit path's included.</summary>
    internal static FileSelectionResult Select(
        SelectionCapture capture,
        IReadOnlyList<string> relPaths,
        IReadOnlyList<string>? under,
        string? provider,
        int? maxPaths,
        bool filesOnDisk = false,
        FileExclusion? exclude = null)
    {
        var (view, seeds, faces, noOrder) = (capture.Captured.View, capture.Seeds, capture.Faces, capture.NoOrder);
        provider = provider?.Trim() is { Length: > 0 } named ? named : null;
        // A provider the build does not have would walk nothing and read every row as not provided, so it is refused by name.
        // Off a mod folder that would not open, the provider may be in it, so the name is not called wrong.
        if (provider is not null && !view.HasProvider(provider)
            && HousecarlCore.Mo2LoadOrder.UnopenedModFolders(capture.Captured.EnabledMods, capture.Captured.Roots.ModsDir) is { Count: > 0 } unopened)
            return new FileSelectionResult(Array.Empty<SelectedFile>(), Array.Empty<string>(), provider,
                                           Refusal: $"error: provider '{provider}' is in no folder houseCARL could open, and {unopened.Count} " +
                                                    $"enabled mod folder(s) could not be opened ({HousecarlCore.Mo2LoadOrder.Named(unopened)}): " +
                                                    "it may be in one of them. Make those folders readable and run this again.");
        if (provider is not null && !view.HasProvider(provider))
            return new FileSelectionResult(Array.Empty<SelectedFile>(), Array.Empty<string>(), provider,
                                           Refusal: $"error: provider '{provider}' is not in the active load order — name an " +
                                                    "enabled mod folder, 'overwrite', 'Data' or an active archive's filename, " +
                                                    "as a row's providers line spells it.");
        var notes = new List<string>();
        var selected = new List<SelectedFile>(relPaths.Count);
        for (int i = 0; i < relPaths.Count; i++) selected.Add(new SelectedFile(relPaths[i] ?? "", null, null, null, Given: i));   // explicit paths first, in the order given, never deduped
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
        bool wholeProvider = provider is not null && under is not { Count: > 0 } && relPaths.Count == 0 && !filesOnDisk && seeds is not { Count: > 0 };
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
                // One past what is left of the budget; the walk counts paths already selected too, so filling it does not prove the deduped selection over. 0 = no cap.
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
        // The stop's count, not the deduped one: at least one past the bound, even when dedup left fewer entries.
        if (overBound)
            return new FileSelectionResult(selected, notes, provider, StoppedAt: Math.Max(selected.Count, maxPaths!.Value + 1));
        return new FileSelectionResult(exclude?.Apply(selected, notes) ?? selected, notes, provider);
    }
}

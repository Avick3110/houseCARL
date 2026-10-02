using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    /// <summary>Create an empty, header-only plugin named exactly <paramref name="pluginName"/> — the primitive for
    /// "plugin Foo.esp needs to exist". The name is never auto-suffixed: a collision refuses loudly (#561).</summary>
    public WritePatchBuilder.CreatePluginOutcome CreatePlugin(string pluginName, bool esl = false, string? author = null, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                "patch is required — a header-only plugin has no record to derive a name from, so name it explicitly (e.g. 'Authoria - CraftingCategories').");

        var stem = OutputLocations.PatchStem(pluginName);
        if (string.IsNullOrWhiteSpace(stem))
            return WritePatchBuilder.CreatePluginOutcome.Fail(
                $"patch '{pluginName}' has no usable name once path parts and the plugin extension are stripped — give a plain name like 'MyTrigger'.");

        lock (_writeGate)                                                 // one write at a time, resolve through commit
        {
            // The view first: check (a) reads it, and the capture below then carries the built plugin names.
            var view = Resolver.Capture();
            var snapshot = ConfiguredRoots();                             // before the allocation lock, which never wraps a _gate hold
            var roots = snapshot.Roots;
            if (!Directory.Exists(roots.ModsDir))
                return WritePatchBuilder.CreatePluginOutcome.Fail($"cannot write: ModsDir '{roots.ModsDir}' does not exist. Check HouseCarl:ModsDir.");

            // The basename is load-bearing for a trigger, so a collision is never auto-suffixed — refuse instead.
            // (a) an active plugin already owns this basename — a second one would shadow it (MO2 picks one by mod order).
            foreach (var ext in OutputLocations.PluginExts)              // .esp / .esm / .esl
                if (view.ContainsPlugin(stem + ext))
                    return WritePatchBuilder.CreatePluginOutcome.Fail(
                        $"a plugin named '{stem + ext}' is already active in your load order — a header-only trigger needs a UNIQUE basename (a second one would shadow it, MO2 picking the winner by mod order). Choose a different name.");
            var folder = Path.Combine(roots.ModsDir, OutputLocations.ModFolderName(stem));
            var plugin = stem + ".esp";
            var active = OutputLocations.ActivePluginBasenames(roots, snapshot.BuiltPluginNames);   // before the lock: may build the order
            lock (_outputLocations.FolderAllocationGate)                 // the same allocation lock as the other fresh-folder sites
            {
                // (b) a houseCARL mod folder of this exact name already exists — don't overwrite (could clobber a real patch
                //     sharing the name) and don't auto-rename (would break the basename trigger): refuse and point at it.
                if (Directory.Exists(folder))
                    return WritePatchBuilder.CreatePluginOutcome.Fail(
                        $"a houseCARL output folder '{OutputLocations.ModFolderName(stem)}' already exists — houseCARL won't auto-rename a header-only plugin (its exact basename is what makes the trigger resolve). Remove that folder in MO2, or choose a different name.");
                // (c) a plugin of this BASENAME sits somewhere the order is NOT loading — the shadow the fresh patch lanes take (#561).
                if (active.Count > 0 && OutputLocations.ReadCompositionForShadow(roots) is { } comp)
                    foreach (var ext in OutputLocations.PluginExts)       // .esp / .esm / .esl — the basename is what binds
                        if (PatchStemShadow.Find(comp, roots.ModsDir, roots.DataDir, roots.OverwriteDir, stem + ext, active) is { } shadow)
                            return WritePatchBuilder.CreatePluginOutcome.Fail(
                                PatchStemShadow.Refusal(plugin, shadow, "patch", stem + ext,
                                                        "a header-only trigger needs a UNIQUE basename"));

                Directory.CreateDirectory(folder);
                OutputLocations.WriteOwnerMeta(folder, plugin);
            }
            var outPath = Path.Combine(folder, plugin);

            var outcome = WritePatchBuilder.CreatePlugin(outPath, esl, author, description);
            if (!outcome.Success) RemoveFolderCreatedThisCall(outPath);   // a refused create leaves no orphan folder
            return outcome;
        }
    }

    /// <summary>Compact / ESL-renumber a plugin — the data-layer twin of xEdit's "Compact FormIDs for ESL": renumber the
    /// originating records into the light window, repoint every internal reference, and emit a new plugin keeping the source's
    /// basename, or overwrite the original under <paramref name="inPlace"/>. External referencers refuse unless <paramref name="repointExternals"/>.</summary>
    public WritePatchBuilder.CompactOutcome CompactPlugin(
        string pluginName, bool esl = true, bool inPlace = false, bool repointExternals = false,
        bool acknowledge = false, string? patchName = null)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            return WritePatchBuilder.CompactOutcome.Fail("plugin is required — name the plugin filename to compact (e.g. 'CoolMod.esp').");

        lock (_writeGate)                                                 // one write at a time; the whole resolve→build→repoint runs under it
        {
            var resolver = Resolver;                                      // builds/refreshes; reentrant with _writeGate
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.CompactOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");

            var name = pluginName.Trim();
            string? srcPath;
            string? offOrderNote = null;
            if (view.ContainsPlugin(name))
            {
                if (view.ExcludedPlugins.TryGetValue(name, out var excluded))
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"cannot compact '{name}': it was EXCLUDED from this session ({excluded}) — houseCARL won't renumber a plugin it can't fully parse. The file is untouched.");
                srcPath = view.PluginPath(name);
                if (srcPath is null || !File.Exists(srcPath))
                    return WritePatchBuilder.CompactOutcome.Fail($"'{name}' not found on disk at {srcPath ?? "<unresolved>"} — nothing to compact.");
            }
            else
            {
                // Not in the active order → resolve the file on disk through the shared locate contract; every declared master must still be active.
                string modsDir, dataDir, overwriteDir, profileDir;
                lock (_gate) { EnsurePathsDerived(); modsDir = _modsDir; dataDir = _dataDir; overwriteDir = _overwriteDir; profileDir = _profileDir; }
                var comp = Mo2LoadOrder.ReadComposition(profileDir);
                var loc = OutputLocations.LocatePluginFileOnDisk(comp, modsDir, dataDir, overwriteDir, name, null);
                if (loc.Error is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not an active plugin in your load order, and no on-disk copy was found either ({loc.Error})");
                if (loc.Ambiguous is not null)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' is not in the active load order and {loc.Ambiguous.Count} mod folders provide a file with that name " +
                        $"({string.Join(", ", loc.Ambiguous.Select(h => h.Where))}) — ambiguous, refusing to guess which to compact. " +
                        "Enable the one you mean in MO2, or remove the duplicates.");
                srcPath = loc.Path!;
                offOrderNote = $"'{name}' is not in the active load order (found: {loc.Where}) — compacted OFF-ORDER; " +
                               "masters resolved from the active order. Enable the result in MO2 to use it.";
            }

            ModKey modKey;
            try { modKey = ModKey.FromFileName(name); }
            catch (Exception ex) { return WritePatchBuilder.CompactOutcome.Fail($"'{name}' is not a valid plugin filename ({ex.Message})."); }

            // A localized target refuses the in-place lane, checked before the identify pass, the consent gate and anything
            // staged; the new-file lane reports on it below instead. Read once, and keyed on the header flag.
            var srcShape = LocalizedStrings.Assess(srcPath, view.DataDir);
            // The decision collapses and stays fail-closed; the WORDS do not — see CompactInPlaceRefusal.
            bool srcLocalized = srcShape.Shape != LocalizedShape.NotLocalized;
            if (inPlace && srcLocalized)
                return WritePatchBuilder.CompactOutcome.Fail(CompactInPlaceRefusal(name, srcShape));

            // The NEW-FILE lane's own refusal: a localized source whose strings resolve nowhere reads every value EMPTY.
            if (LocalizedStrings.ResolvesNowhere(srcShape.Shape))
                return WritePatchBuilder.CompactOutcome.Fail(
                    UnresolvableStringsRefusal(name, srcShape, "compact"));

            // 1. originating record keys + the remap into the (light, by default) window.
            if (!WritePatchBuilder.TryReadOriginatingKeys(srcPath, modKey, out var keys, out var keyErr))
                return WritePatchBuilder.CompactOutcome.Fail(keyErr!);
            string? flagOnlyNote = null;
            uint floor = RemapEngine.EslFloor;
            IReadOnlyDictionary<FormKey, FormKey> remapDict;
            if (keys.Count == 0)
            {
                // An override-only or empty plugin has nothing to renumber, but esl=true is still satisfiable: an empty remap.
                if (!esl)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"'{name}' defines no originating records to renumber (it carries only overrides, or is empty) — nothing to compact. " +
                        "(With esl=true this would still set the ESL/light header flag — always valid for an override-only plugin.)");
                remapDict = new Dictionary<FormKey, FormKey>();
                flagOnlyNote = $"'{name}' defines no originating records — nothing renumbered; every record copied verbatim with the ESL (light) flag set (always valid for an override-only plugin).";
            }
            else
            {
                uint ceiling = esl ? RemapEngine.EslCeiling : FormIdRange.ObjectIdMax;   // light window, or the full 24-bit object-ID range
                var plan = RemapEngine.BuildSequentialRemap(keys, modKey, floor, ceiling);
                if (!plan.Success) return WritePatchBuilder.CompactOutcome.Fail(plan.Error!);
                remapDict = plan.Dict;
            }

            // The identify pass: which plugins outside the target reference a record being renumbered.
            var targets = remapDict.Keys.ToHashSet();
            var transformSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
            var id = targets.Count == 0
                ? new RemapEngine.IdentifyResult(Array.Empty<RemapEngine.ExternalRef>(), Array.Empty<string>(), 0, 0,
                                                 Array.Empty<string>(), Array.Empty<RemapEngine.ExternalOverride>(), Array.Empty<string>())
                : RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet);

            // External-referencer policy: never silently ship a compaction that dangles an external reference.
            if (id.HasExternalReferencers)
            {
                var refList = $"{string.Join(", ", id.ExternalPlugins.Take(25))}{(id.ExternalPlugins.Count > 25 ? $", … (+{id.ExternalPlugins.Count - 25} more)" : "")}";
                if (!repointExternals)
                {
                    // This refusal's remedy is a re-run, so it says up front when that re-run would itself be refused.
                    var blocked = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                    var repointClause = blocked.Count == 0
                        ? "Re-run with repoint_externals=true AND in_place=true (+ acknowledge=true) to ALSO rewrite those plugins in place to follow "
                          + "the renumber, or handle them yourself first."
                        // Split by class: LocalizedAmong fails closed on a referencer it could not read.
                        : $"Re-running with repoint_externals=true will NOT work here: {BlockedReferencerCensus(blocked)}. "
                          + "houseCARL rewrites neither a localized plugin nor one it cannot read in place, so the "
                          + "repoint would refuse before touching anything. "
                          // Reasons are attributed once per class.
                          + BlockedReferencerReasons(blocked)
                          + " Until that is resolved, handle the references yourself instead.";
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — {id.ExternalPlugins.Count} plugin(s) outside '{name}' reference records it is about to renumber; compacting it " +
                        $"WOULD BREAK those references (they would point at FormIDs that no longer exist). Referencers: {refList}. " +
                        repointClause + " Nothing was written.");
                }
                // Repointing is only coherent paired with in_place.
                if (!inPlace)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — repoint_externals requires in_place=true. {id.ExternalPlugins.Count} plugin(s) reference records being renumbered " +
                        $"({refList}); in the new-file lane those records exist ONLY in the not-yet-active P′, so repointing the externals now would leave " +
                        "them dangling against the still-active original until you complete the MO2 swap (and broken if you reject P′). Either compact IN " +
                        "PLACE (in_place=true) so the target and its referrers move together, or handle the externals yourself after enabling P′. Nothing was written.");
            }

            // The consent gate: any in-place overwrite — the target, the external referencers, or both — needs acknowledge.
            bool willOverwriteTarget = inPlace;
            bool willRepoint = id.HasExternalReferencers && repointExternals;

            // A plugin the identify pass could not read through REFUSES the in-place lane, before anything is written.
            // The new-plugin lane keeps the note — its output is reviewed before it replaces anything.
            if ((willOverwriteTarget || willRepoint) && id.UnscannablePlugins is { Count: > 0 } unread)
            {
                var c = new System.Text.StringBuilder();
                c.Append($"refused — this is an IN-PLACE rewrite (no houseCARL backup or undo) and the external-reference pass could not read ")
                 .Append(unread.Count).Append(unread.Count == 1 ? " plugin, so houseCARL cannot tell whether it references records "
                                                               : " plugins, so houseCARL cannot tell whether they reference records ")
                 .Append($"'{name}' is about to renumber: ");
                c.Append(string.Join("; ", unread.Take(25).Select(WriteSentences.UnscannablePlugin)));
                if (unread.Count > 25) c.Append($"; … (+{unread.Count - 25} more)");
                c.Append(". NOTHING was written — ").Append($"'{name}' is untouched. ")
                 .Append("Either resolve that and run this again, or compact into a NEW plugin (in_place=false), which renumbers the same records into a ")
                 .Append("file you review and swap in yourself, leaving the original and its referencers alone.");
                return WritePatchBuilder.CompactOutcome.Fail(c.ToString());
            }

            // Before the consent gate and before ANY write: a run whose referencer rewrites include a localized plugin can
            // never happen, and the rewrites run only after the compacted plugin is on disk. No remedy is named.
            if (willRepoint)
            {
                var localized = RemapEngine.LocalizedAmong(resolver, id.ExternalPlugins);
                if (localized.Count > 0)
                    return WritePatchBuilder.CompactOutcome.Fail(
                        $"refused — compacting '{name}' means rewriting the plugins that reference it, and houseCARL " +
                        // Split by class, count and label both.
                        $"cannot rewrite all of them: {BlockedReferencerCensus(localized)}. " +
                        // The referencer's own reason, verbatim from the same decision the write would have made, once per class.
                        $"{BlockedReferencerReasons(localized)} " +
                        "NOTHING was written and nothing was staged — " +
                        $"'{name}' is untouched. Following the renumber means rewriting those referencers in place, and " +
                        "houseCARL rewrites neither a localized plugin nor one it cannot read in place.");
            }
            if ((willOverwriteTarget || willRepoint) && !acknowledge)
            {
                var c = new System.Text.StringBuilder();
                c.Append("CONFIRM in-place rewrite (your ORIGINAL file(s) will be rewritten — no houseCARL backup or undo; keep your own):\n");
                if (willOverwriteTarget) c.Append($"  - '{name}' will be OVERWRITTEN in place with its compacted form.\n");
                if (willRepoint)
                {
                    c.Append($"  - {id.ExternalPlugins.Count} external referencer(s) will be REWRITTEN in place to repoint to the new FormIDs:\n");
                    foreach (var pl in id.ExternalPlugins.Take(25)) c.Append($"      · {pl}\n");
                    if (id.ExternalPlugins.Count > 25) c.Append($"      · … (+{id.ExternalPlugins.Count - 25} more)\n");
                }
                // No unread-plugin note here: this lane refuses above when the scan could not read a plugin through.
                c.Append("Re-call with acknowledge=true to proceed.");
                return WritePatchBuilder.CompactOutcome.Confirm(c.ToString());
            }
            // Pre-flight that the in-place target's parent is writable before any work.
            if (inPlace && InPlaceParentUnwritable(srcPath, out var unwritable))
                return WritePatchBuilder.CompactOutcome.Fail(unwritable);

            // The round-trip check (#961) on every file this call rewrites in place, before any of them is written.
            if ((willOverwriteTarget || willRepoint) &&
                WritePatchBuilder.CompactRoundTripRefusal(view, srcPath, willOverwriteTarget, willRepoint ? id.ExternalPlugins : null) is { } lost)
                return WritePatchBuilder.CompactOutcome.Fail(lost);

            // Output location: in place over the original, or a new file keeping the source's exact basename.
            string outPath; bool createdFresh = false; OutputLocations.RiderFolder rf = default;
            if (inPlace) outPath = srcPath;
            else
            {
                try { rf = _outputLocations.ResolvePatchModFolder(patchName, null, Path.GetFileNameWithoutExtension(name) + " compacted", naming: null); }
                catch (InvalidOperationException ex) { return WritePatchBuilder.CompactOutcome.Fail(ex.Message); }
                createdFresh = rf.CreatedFresh;
                OutputLocations.WriteOwnerMeta(rf.ModFolder, name);       // the output keeps the source's exact basename
                outPath = Path.Combine(rf.OutputDir, name);
            }

            // Build and write the compacted plugin.
            var build = WritePatchBuilder.CompactBuild(srcPath, modKey, remapDict, view.PluginPath, outPath, esl, floor, view.DataDir);
            if (!build.Success)
            {
                if (!inPlace && createdFresh) OutputLocations.RemoveOrNameRiderResidue(rf);   // a refused build leaves no orphan folder
                return WritePatchBuilder.CompactOutcome.Fail(build.Error!);
            }

            // Opt-in: repoint each external referencer in place, per-plugin all-or-nothing, with every result reported.
            var repointed = new List<WritePatchBuilder.RepointReport>();
            if (willRepoint)
                foreach (var ext in id.ExternalPlugins)
                {
                    var rep = RemapEngine.RepointInPlace(resolver, ext, remapDict);
                    repointed.Add(new WritePatchBuilder.RepointReport(ext, rep.Success, rep.Error));
                }

            // Carry the FormID-keyed assets a renumber moves — FaceGen head mesh and tint, voice .fuz/.lip — into the P′
            // mod-folder root. Best-effort and reported, since the records are already written. One captured asset view feeds
            // both carries and the SEQ gate, which asks the VFS whether the source SHIPPED a .seq rather than one folder.
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS gate result — SET the moment the view resolves, BEFORE the carries
            var srcSeqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(srcPath)}.seq";
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
                var assetView = assetResolver.Capture();
                seqGate = assetView.ResolveForPlacement(srcSeqRel).Sources.Count > 0;   // VFS-aware (loose roots + active BSAs)
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, remapDict, assetView, outDir);
                voiceRename = AssetRenameService.CarryVoice(outPath, remapDict, assetView, outDir);
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // The check is the VFS answer whenever the view resolved; only an unresolved view falls back to the loose check.
            bool sourceHadSeq = seqGate ?? File.Exists(Path.Combine(Path.GetDirectoryName(srcPath)!, srcSeqRel));

            // Refresh the start-game-enabled-quest .seq from the renumbered plugin when the source shipped one, never
            // inventing one. Best-effort and reported: it never throws and never fails the compact.
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, sourceHadSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if '{name}' has start-game-enabled quests, run {ToolNames.WriteSeq} on the compacted plugin." });
            }

            // Audit markers: stamp the editedInPlace breadcrumb into every file rewritten in place. Compact keeps its own
            // per-call confirm rather than the persistent acknowledgement, and the markers are best-effort.
            var markerNotes = new List<string>();
            if (offOrderNote is not null) markerNotes.Add(offOrderNote);
            // The new-file lane produces the SAME de-localized plugin the in-place lane is refused for, so it says so.
            // Gated on the SHAPE, not on srcLocalized: fail-closed is the wrong answer for a note.
            if (!inPlace && LocalizedStrings.ConfirmedLocalized(srcShape.Shape))
                markerNotes.Add(
                    $"'{name}' is flagged LOCALIZED — its text lives in separate .STRINGS files rather than in the "
                    + "plugin"
                    + (srcShape.Languages.Count > 0 ? " (" + string.Join(", ", srcShape.Languages) + ")" : "")
                    + ". The compacted plugin houseCARL wrote is NOT localized: it carries whatever this read of the "
                    + "source produced, written into the plugin itself, with no .STRINGS files of its own — so the "
                    + "source's .STRINGS files do not describe it, and any language it shipped that this read did not "
                    + "resolve is not in the output. Read the output before you enable it in place of the original.");
            if (flagOnlyNote is not null) markerNotes.Add(flagOnlyNote);
            if (inPlace) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(srcPath)); if (n is not null) markerNotes.Add(n); }
            foreach (var r in repointed.Where(r => r.Success))
            {
                var rp = view.PluginPath(r.Plugin);
                if (rp is not null) { var n = MergeEditedInPlaceMarker(Path.GetDirectoryName(rp)); if (n is not null) markerNotes.Add(n); }
            }

            return new WritePatchBuilder.CompactOutcome(
                true, null, false, outPath, name, inPlace, esl, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                build.Bytes, id.ExternalPlugins, repointed, id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples,
                markerNotes.Count > 0 ? string.Join(" ", markerNotes) : null, assetRename, id.ExternalOverriders, voiceRename, seqRegen,
                id.UnscannablePlugins);
        }
    }

    /// <summary>A blocked referencer list split into the two classes it holds — flagged LOCALIZED, and could not be read — which do not have the same fix.</summary>
    static (IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Localized,
            IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> Unread)
        SplitBlockedReferencers(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
        => (blocked.Where(b => LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList(),
            blocked.Where(b => !LocalizedStrings.ConfirmedLocalized(b.Shape)).ToList());

    /// <summary>"2 flagged LOCALIZED (A.esp, B.esp), and 1 houseCARL could not read (C.esp)" — counts and names per class, and a class with no hits contributes nothing.</summary>
    internal static string BlockedReferencerCensus(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0) parts.Add($"{localized.Count} flagged LOCALIZED ({NameList(localized)})");
        if (unread.Count > 0) parts.Add($"{unread.Count} houseCARL could not read ({NameList(unread)})");
        return string.Join(", and ", parts);

        static string NameList(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> l)
            => string.Join(", ", l.Take(25).Select(x => x.Plugin)) + (l.Count > 25 ? $", … (+{l.Count - 25} more)" : "");
    }

    /// <summary>An attributed reason for the first of EACH class, never only the first hit overall.</summary>
    internal static string BlockedReferencerReasons(IReadOnlyList<(string Plugin, LocalizedShape Shape, string Why)> blocked)
    {
        var (localized, unread) = SplitBlockedReferencers(blocked);
        var parts = new List<string>();
        if (localized.Count > 0)
        {
            parts.Add($"Where {localized[0].Plugin}'s text is: {localized[0].Why}");
            if (localized.Count > 1)
                parts.Add($"(The other {localized.Count - 1} localized referencer(s) are reported the same way if you compact them.)");
        }
        if (unread.Count > 0)
        {
            parts.Add($"Why {unread[0].Plugin} is blocked: {unread[0].Why}");
            if (unread.Count > 1)
                parts.Add($"(The other {unread.Count - 1} unreadable referencer(s) are the same.)");
        }
        return string.Join(" ", parts);
    }

    /// <summary>The refusal for a plugin flagged localized whose <c>.STRINGS</c> houseCARL can find NOWHERE, so the read
    /// itself is empty; shared by compact and merge, with the words per shape. It says houseCARL cannot FIND the tables,
    /// never that the plugin has none. <paramref name="verb"/> is the operation as the report names it.</summary>
    internal static string UnresolvableStringsRefusal(string name, LocalizedAssessment a, string verb)
    {
        // WHERE the text is comes from the one renderer that already gets it right for both shapes.
        // What the READ will be is per shape: nowhere resolves reads empty, an unlistable folder reads unknown.
        var unlistable = a.Shape is LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable;
        var consequence = unlistable
            ? "houseCARL cannot tell what its text reads as, or an empty value from a real one"
            : "every name, description and message it carries reads back EMPTY";

        // And so is the REMEDY: an unlistable folder needs freeing rather than populating, and the sentence names which folder.
        var folder = a.Shape == LocalizedShape.StringsFolderUnreadable
            ? $"the Strings folder beside '{name}'"
            : $"the folder '{name}' sits in";
        var remedy = unlistable
            ? $"Let houseCARL read {folder} — close whatever is holding it open, or fix its permissions — and run "
              + "this again."
            : "Put this plugin's .STRINGS where houseCARL can see them — enable the mod that provides them, or place "
              + "them in a Strings folder beside the plugin — and run this again.";
        return $"refused — houseCARL did not {verb} '{name}'. "
             + LocalizedTargetUnsupportedException.WhereTheTextIs(a) + " "
             + $"So {consequence}, and a {verb} writes whatever this read produced into a NEW plugin you keep, with "
             + "nothing left in it to tell that text from a plugin that never had any. "
             + remedy + " Nothing was written.";
    }

    /// <summary>The in-place compaction's refusal, rendered per shape: one fail-closed boolean decides it, but its words cannot be shared.</summary>
    static string CompactInPlaceRefusal(string name, LocalizedAssessment a)
    {
        var head = $"houseCARL did not compact '{name}' in place — the file is unchanged and nothing was staged. "
                 + LocalizedTargetUnsupportedException.ShapeClause(a) + " ";
        return a.Shape switch
        {
            // Never opened: no claim about tables, and no lane to switch to.
            LocalizedShape.Unreadable =>
                head + "houseCARL does not rewrite a destination it cannot classify. Compacting into a NEW plugin is "
                     + "not the lane to switch to either — it reads the same file and fails the same way. "
                     + LocalizedTargetUnsupportedException.RemedyUnreadable,

            LocalizedShape.LooseComplete or LocalizedShape.LoosePartial or LocalizedShape.LooseWithGameDataDuplicate
                or LocalizedShape.BsaEmbedded or LocalizedShape.GameDataOnly
                or LocalizedShape.StringsFolderUnreadable or LocalizedShape.ModFolderUnreadable
                or LocalizedShape.Nowhere =>
                head + "A compaction does not re-serialize your plugin, it builds a NEW one and writes that over the "
                     + "original, and houseCARL will not replace a translated plugin's .STRINGS files on your own copy: "
                     + "it cannot swap the plugin and its tables as one operation, and the file the game loads would "
                     + "stop being the translated plugin you have, with no backup and nothing to undo it. "
                     + $"Re-run without in_place to compact '{name}' into a NEW plugin instead: the same renumber, left "
                     + "in its own mod folder for you to check and enable yourself. That output is NOT localized — it "
                     + "carries the text that resolved when houseCARL read the source, written into the plugin itself, "
                     + "and the source's .STRINGS files do not describe it. Read it before you swap it in.",

            // NotLocalized cannot reach here, and a new shape has no wording, so this arm says only what is certain.
            _ => head + "houseCARL will not compact this plugin in place.",
        };
    }

    /// <summary>Merge one or more active plugins into one new plugin: the donors' records combine under a new name with a
    /// collision-only renumber, cross-donor conflicts resolving to the load-order winner. The donors are never touched —
    /// new-file lane only — external referencers are named rather than refused, and the FormID-keyed facegen, voice and
    /// <c>.seq</c> follow per donor. With a single donor the operation IS a rename. <paramref name="patch"/> names the output MOD FOLDER.</summary>
    public WritePatchBuilder.MergeOutcome MergePlugins(
        IReadOnlyList<string>? plugins, string? patch)
    {
        // ---- argument shape; every refusal names the fix ----
        var donorsRaw = (plugins ?? Array.Empty<string>()).Select(p => (p ?? "").Trim()).Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // One donor is legitimate and IS the rename; the donor list stays a SET, so duplicates collapse here.
        if (donorsRaw.Count == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "merge needs at least ONE donor plugin — pass plugins=[\"A.esp\"] to move one plugin's records to a new " +
                "name (a rename), or plugins=[\"A.esp\", \"B.esp\", …] to combine several.");
        var patchName = (patch ?? "").Trim();
        if (patchName.Length == 0)
            return WritePatchBuilder.MergeOutcome.Fail(
                "patch is required — name the NEW mod folder to create (e.g. 'MyMerge'). The merged plugin inside it takes that name ('MyMerge.esp'), and it must not already exist in your load order.");
        // patch= names the FOLDER and the plugin takes the folder's name, the rule on every tool that writes one.
        var outName = OutputLocations.PatchStem(patchName) + ".esp";
        ModKey outKey;
        try { outKey = ModKey.FromFileName(outName); }
        catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"patch='{patchName}' does not name a valid plugin: '{outName}' ({ex.Message})."); }
        // The .esl spelling is REFUSED rather than stripped, because it asks for what the merge cannot deliver.
        if (patchName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            return WritePatchBuilder.MergeOutcome.Fail(
                // The reason is what the merge does NOT do, and only that.
                $"refused — patch='{patchName}' asks for the .esl extension, which the game engine force-treats as a LIGHT master regardless " +
                "of the header flag, but a merge never constrains object ids to the light window: it renumbers only what it must " +
                "(cross-donor collisions, and ids below the write floor), so an id above 0xFFF would be misread in game. Pass " +
                $"patch='{OutputLocations.PatchStem(patchName)}' instead, which writes '{outName}': if every donor was light and every merged id landed in the window, the output is written LIGHT " +
                "already; otherwise the report says so, and " + ToolNames.CompactPlugin + " on it renumbers every id into the light " +
                "window (the tools compose). Nothing was written.");
        if (donorsRaw.Any(d => string.Equals(d, outName, StringComparison.OrdinalIgnoreCase)))
            return WritePatchBuilder.MergeOutcome.Fail($"the output '{outName}' (the plugin patch='{patchName}' names) cannot also be a donor — pass patch= a NEW name.");

        lock (_writeGate)                                                 // one write at a time
        {
            var resolver = Resolver;
            var view = resolver.Capture();
            if (!Directory.Exists(_modsDir))
                return WritePatchBuilder.MergeOutcome.Fail($"cannot write: ModsDir '{_modsDir}' does not exist. Check HouseCarl:ModsDir.");
            if (view.ContainsPlugin(outName))
                return WritePatchBuilder.MergeOutcome.Fail(
                    $"'{outName}' — the plugin patch='{patchName}' names — is already an active plugin in your load order, and the merge " +
                    "output must be a NEW plugin name (merging over an existing plugin would shadow it in MO2). Pass patch= another name.");

            // ---- validate and load-order-sort the donors: merge semantics are load-order semantics, so sort rather
            //      than trusting argument order. One name-to-position index serves this sort and the master sort. ----
            var orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < resolver.PluginNames.Count; i++) orderIndex[resolver.PluginNames[i]] = i;
            var donorInfos = new List<(string Name, string Path, ModKey Key, int Order)>();
            foreach (var d in donorsRaw)
            {
                if (!view.ContainsPlugin(d))
                {
                    // Once a cause is stated it carries its own remedy.
                    var dClause = view.AbsenceClause(d, out var dWhy);
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"donor '{d}' is not an active plugin in your load order." +
                        dClause +
                        " Merge reads each donor's records and conflict position from the ACTIVE order." +
                        (dWhy is not null ? "" : " Activate it in MO2 first (pass the exact plugin filename, e.g. 'CoolMod.esp')."));
                }
                if (view.ExcludedPlugins.TryGetValue(d, out var excluded))
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge '{d}': it was EXCLUDED from this session ({excluded}) — houseCARL won't merge a plugin it " +
                        "can't fully parse (it would risk dropping records it couldn't read, Q3). Nothing was written.");
                var p = view.PluginPath(d);
                if (p is null || !File.Exists(p))
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' not found on disk at {p ?? "<unresolved>"} — nothing to merge.");
                ModKey dk;
                try { dk = ModKey.FromFileName(d); }
                catch (Exception ex) { return WritePatchBuilder.MergeOutcome.Fail($"'{d}' is not a valid plugin filename ({ex.Message})."); }
                if (!orderIndex.TryGetValue(d, out var order))            // unreachable after ContainsPlugin (same source table) — refuse rather than mis-sort
                    return WritePatchBuilder.MergeOutcome.Fail($"donor '{d}' has no load-order position (index inconsistency, Q3). Nothing was written.");
                donorInfos.Add((d, p, dk, order));
            }
            donorInfos.Sort((a, b) => a.Order.CompareTo(b.Order));
            var donorNames = donorInfos.Select(d => d.Name).ToList();
            var transformSet = new HashSet<string>(donorNames, StringComparer.OrdinalIgnoreCase);

            // ---- 2. masters = union(donor declared masters) − donors, load-order sorted, from the donor HEADERS only.
            //      Run HERE, before the record reads and the identify pass, because a master the
            //      active order does not carry refuses the whole merge (#729). ----
            var masterSet = new List<string>();
            var seenMasters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dName, _, _, _) in donorInfos)
            {
                IReadOnlyList<string> declared;
                try { declared = view.DeclaredMasters(dName); }
                catch (Exception ex)
                {
                    return WritePatchBuilder.MergeOutcome.Fail($"cannot read donor '{dName}' masters ({ex.Message}) — nothing written.");
                }
                foreach (var mfn in declared)
                    if (!transformSet.Contains(mfn) && seenMasters.Add(mfn)) masterSet.Add(mfn);
            }
            masterSet.Sort((a, b) =>
                (orderIndex.TryGetValue(a, out var ia) ? ia : int.MaxValue).CompareTo(orderIndex.TryGetValue(b, out var ib) ? ib : int.MaxValue));
            // The same predicate the serialize applies, asked from the headers instead; MergeBuild still asks it.
            foreach (var mfn in masterSet)
                if (view.PluginPath(mfn) is null)
                    return WritePatchBuilder.MergeOutcome.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");

            // ---- the donors' strings, read once and used twice. A donor whose .STRINGS resolve NOWHERE reads every
            //      value EMPTY and is refused here, before anything is written; a donor that IS localized earns the
            //      de-localization note the report carries. ----
            var localizedDonors = new List<string>();
            foreach (var (dName, dPath, _, _) in donorInfos)
            {
                var shape = LocalizedStrings.Assess(dPath, view.DataDir);
                if (LocalizedStrings.ResolvesNowhere(shape.Shape))
                    return WritePatchBuilder.MergeOutcome.Fail(UnresolvableStringsRefusal(dName, shape, "merge"));
                // ConfirmedLocalized, not "anything but NotLocalized": the note ASSERTS where a donor's text lives.
                if (LocalizedStrings.ConfirmedLocalized(shape.Shape)) localizedDonors.Add(dName);
            }

            // ---- 3. what each donor holds, one enumeration per donor: the records it defines, the donor-space records
            //      it carries without defining (injected records), and its links into donor space. ----
            var donorModKeys = donorInfos.Select(d => d.Key).ToHashSet();
            var scans = new List<(string Donor, IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried)>();
            var donorLinks = new List<(string Donor, IReadOnlyList<FormKey> Records, IReadOnlyList<(FormKey Source, FormKey Target)> Links)>();
            foreach (var (dName, dPath, dKey, _) in donorInfos)
            {
                if (!WritePatchBuilder.TryScanMergeDonor(dPath, dKey, donorModKeys, out var scan, out var keyErr))
                    return WritePatchBuilder.MergeOutcome.Fail(keyErr!);
                scans.Add((dName, scan.Originating, scan.Carried));       // a pure-override donor (0 originating keys) is a legit patch donor
                donorLinks.Add((dName, scan.Records, scan.DonorLinks));
            }
            // An injected record is renumbered with the donor carrying it, not copied at an identity the merge removes (#715).
            var donorKeys = MergeInjection.Renumberable(scans);
            // ---- output folder and plugin: the same fresh-write resolver the record lanes use, so patch= names the
            //      mod folder and the plugin inside it takes the stem. Resolved HERE, because the remap is keyed on the
            //      output ModKey and the stem can still be REFUSED by a mod folder of that name. ----
            string outPath;
            bool createdFolder;
            try
            {
                outPath = ResolveOutputPath(patchName, into: null, out _, out createdFolder,
                    refuseTaken: new OutputLocations.StemRefusal(
                        "the merged plugin",
                        "Remove it in MO2, or pass patch= a name no mod folder or active plugin already carries."));
            }
            catch (InvalidOperationException ex) { return WritePatchBuilder.MergeOutcome.Fail(ex.Message); }
            outName = Path.GetFileName(outPath);
            try { outKey = ModKey.FromFileName(outName); }
            catch (Exception ex) { return FailAfterFolder($"'{outName}' is not a valid plugin filename ({ex.Message})."); }

            // A refusal past the folder allocation removes the folder again, so no orphan accretes suffixes on retry.
            WritePatchBuilder.MergeOutcome FailAfterFolder(string msg)
            {
                if (createdFolder) RemoveFolderCreatedThisCall(outPath);
                return WritePatchBuilder.MergeOutcome.Fail(msg);
            }

            var plan = RemapEngine.BuildMergeRemap(donorKeys, outKey, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);
            if (!plan.Success) return FailAfterFolder(plan.Error!);

            // A donor reference the remap cannot carry is named HERE, before the identify pass and the build.
            if (MergeInjection.UnremappableLink(plan.Dict, donorLinks) is { } linkRefusal)
                return FailAfterFolder(linkRefusal);

            // ---- 4. identify-pass — WARN-and-proceed (the A4 posture); unlike compact this NEVER refuses: the donors
            //      stay active until the user swaps, so nothing breaks at write time, and the report names each affected plugin. ----
            var targets = plan.Dict.Keys.ToHashSet();
            // readDeclaredMasters: a merge renames the donors' records, so a dependent that only lists a donor as a master loses it.
            var id = RemapEngine.IdentifyExternalReferencers(resolver, targets, transformSet, readDeclaredMasters: true);

            // ---- build and write the merged plugin ----
            var build = WritePatchBuilder.MergeBuild(
                donorInfos.Select(d => (d.Name, d.Path, d.Key)).ToList(), outKey, plan.Dict, masterSet, view.PluginPath, outPath, view.DataDir);
            if (!build.Success)
            {
                return FailAfterFolder(build.Error!);                      // a refused build leaves no orphan folder
            }

            // ---- FormID-keyed assets follow the renumber, per donor, because the plugin NAME is a segment of the
            //      facegen and voice paths. One captured asset view feeds the carries and the SEQ check, best-effort and reported. ----
            AssetRenameOutcome assetRename;
            VoiceCarryOutcome voiceRename;
            bool? seqGate = null;                                          // the VFS answer, set the moment the view resolves
            try
            {
                AssetResolver assetResolver;
                lock (_gate) { assetResolver = Assets; }                  // reentrant under the held _writeGate
                var assetView = assetResolver.Capture();
                seqGate = false;                                          // the view resolved — the answer below is authoritative
                foreach (var (dName, _, _, _) in donorInfos)              // did any donor ship a .seq? VFS-aware, per donor
                    if (assetView.ResolveForPlacement($@"SEQ\{Path.GetFileNameWithoutExtension(dName)}.seq").Sources.Count > 0)
                        { seqGate = true; break; }
                var outDir = Path.GetDirectoryName(outPath)!;
                assetRename = AssetRenameService.CarryFaceGen(outPath, plan.Dict, assetView, outDir);
                var voiceParts = donorInfos
                    .Select(d => AssetRenameService.CarryVoice(outPath, plan.Dict, assetView, outDir, sourcePlugin: d.Name))
                    .ToList();
                voiceRename = new VoiceCarryOutcome(
                    voiceParts.Sum(v => v.FilesScanned), voiceParts.Sum(v => v.FilesCarried), voiceParts.Sum(v => v.LinesCarried),
                    voiceParts.SelectMany(v => v.Failures).ToList(), voiceParts.Any(v => v.ReadIncomplete))
                    // The donors share one asset view, so the same root fails for each: named once, not per donor.
                    { RootFailures = voiceParts.SelectMany(v => v.RootFailures).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            }
            catch (Exception ex)
            {
                assetRename = new AssetRenameOutcome(0, 0, 0,
                    new[] { $"facegen carry skipped — the asset layer could not be built ({ex.Message}); verify NPC faces in-game." }, false);
                voiceRename = new VoiceCarryOutcome(0, 0, 0,
                    new[] { $"voice carry skipped — the asset layer could not be built ({ex.Message}); verify voiced lines in-game." }, false);
            }
            // Only when the view never resolved does this fall back to a loose per-donor-folder check.
            bool anyDonorSeq = seqGate ?? donorInfos.Any(d =>
                File.Exists(Path.Combine(Path.GetDirectoryName(d.Path)!, "SEQ", Path.GetFileNameWithoutExtension(d.Name) + ".seq")));

            // ---- SEQ, refresh-only, off the merged plugin: rebuilt when any donor shipped a .seq, because all their
            //      quests now live in the output. A donor with SGE quests but no shipped .seq gets a named advisory. ----
            SeqRegenOutcome seqRegen;
            try { seqRegen = AssetRenameService.RegenerateSeq(outPath, Path.GetDirectoryName(outPath)!, anyDonorSeq); }
            catch (Exception ex)
            {
                seqRegen = new SeqRegenOutcome(0, false, null,
                    new[] { $"SEQ regenerate skipped ({ex.Message}) — if the donors have start-game-enabled quests, run {ToolNames.WriteSeq} on '{outName}'." });
            }

            // Surface the one behaviour change the any-donor rule can introduce: a quest that was not auto-starting gains an entry.
            string? note = seqRegen.Written
                ? "the regenerated .seq lists EVERY start-game-enabled quest in the output — including quests no donor's own .seq " +
                  "listed, whether because that donor shipped none or because its .seq was trimmed. Such quests were NOT " +
                  "auto-starting before; they will now."
                : null;

            // Where the output has to sit, off the positions, masters and dependents this call already computed.
            var placement = MergeLoadPosition.Derive(
                donorInfos.Select(d => (d.Name, d.Order)).ToList(), build.Masters,
                p => orderIndex.TryGetValue(p, out var i) ? i : null);

            return new WritePatchBuilder.MergeOutcome(
                true, null, outPath, outName, donorNames, build.Masters, build.RecordsCopied, build.RecordsRenumbered,
                plan.Donors, build.Conflicts, id.ExternalPlugins, id.ExternalOverriders,
                id.PluginsScanned, id.UnscannableRecords, id.UnscannableSamples, build.Bytes, note,
                assetRename, voiceRename, seqRegen, build.LightDonors, build.HeaderMetaDonors, build.MasterDonors,
                id.UnscannablePlugins, localizedDonors, id.MasterDeclarers, build.LightCarried, build.OriginatingRecords,
                placement);
        }
    }
}

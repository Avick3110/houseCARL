using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda;

namespace HousecarlCore;

/// <summary>The SCRIPT-PROPERTY binding sweep (the scripts family on housecarl_check): for every record carrying a
/// VMAD it cross-checks the properties BOUND on the attachment against the properties the attached script's compiled
/// <c>.pex</c> and its whole <c>extends</c> chain DECLARES, and reports those declared but left unbound. What counts
/// as a finding, what the sweep does not claim, and how unverifiable attachments ride the filters are in
/// docs/architecture/check-scripts-and-dialogue-families.md.</summary>
public static class ScriptPropertyCheck
{
    /// <summary>The script name an attachment with no class name is reported under; not a real class, so the
    /// unverifiable collapse skips it.</summary>
    public const string NamelessScript = "(unnamed)";

    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null or empty = the whole active order minus excluded
    /// plugins), resolving each attached script's <c>.pex</c> through <paramref name="assets"/>.
    /// <paramref name="limit"/> caps the findings COLLECTED, never the totals, and a bad or excluded scope name fails
    /// loud. The narrowing knobs are <paramref name="recordScope"/>, <paramref name="propertyContains"/>,
    /// <paramref name="classes"/> and <paramref name="countsOnly"/>; <paramref name="offOrder"/> verifies pre-enable.</summary>
    public static ScriptCheckResult Run(LoadOrderResolver resolver, AssetResolver assets,
                                        IReadOnlyList<string>? scope, int limit,
                                        SweepScope? recordScope = null, string? propertyContains = null,
                                        ScriptFindingClass classes = ScriptFindingClass.All, bool countsOnly = false,
                                        SweepExclusion.Resolved? exclude = null,
                                        IReadOnlyList<(string Name, string Path)>? offOrder = null)
        => Run(resolver, resolver.Capture(), assets.Capture(), scope, limit, recordScope, propertyContains, classes, countsOnly,
               exclude, offOrder);

    /// <summary>The view-threaded body: the caller's captured view decides membership, drives the sweep and stamps
    /// success and refusals alike. The caller's asset capture <paramref name="av"/> resolves every .pex, and
    /// <see cref="ScriptCheckResult.ReadIncomplete"/> carries its caveat separately.</summary>
    public static ScriptCheckResult Run(LoadOrderResolver resolver, LoadOrderResolver.IndexView view, AssetResolver.AssetView av,
                                        IReadOnlyList<string>? scope, int limit,
                                        SweepScope? recordScope = null, string? propertyContains = null,
                                        ScriptFindingClass classes = ScriptFindingClass.All, bool countsOnly = false,
                                        SweepExclusion.Resolved? exclude = null,
                                        IReadOnlyList<(string Name, string Path)>? offOrder = null)
    {
        var propFilter = string.IsNullOrWhiteSpace(propertyContains) ? null : propertyContains.Trim();
        bool PropOk(string name) => propFilter is null || name.Contains(propFilter, StringComparison.OrdinalIgnoreCase);
        int excludedFromScope = 0;

        // --- resolve the plugin set to scan; a bad or excluded explicit scope name fails loud, never a silent skip ---
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                // Membership refusals are decided against THIS view and carry its epoch.
                if (!view.ContainsPlugin(name))
                    return ScriptCheckResult.Fail($"plugin not in the load order: {name}.{view.AbsenceClause(name)}")
                           with { Epoch = view.Epoch };
                if (view.ExcludedPlugins.TryGetValue(name, out var why))
                    return ScriptCheckResult.Fail(
                        $"plugin '{name}' was excluded from this session because it could not be parsed ({why}) — fix or remove it upstream; it cannot be checked.")
                           with { Epoch = view.Epoch };
                targets.Add(name);
            }
        }
        else if (offOrder is { Count: > 0 })
        {
            targets = new List<string>();   // the caller's explicit scope resolved ENTIRELY off-order — don't widen to the whole order
        }
        else
        {
            targets = new List<string>();
            foreach (var n in resolver.PluginNames)
                if (!view.ExcludedPlugins.ContainsKey(n)) targets.Add(n);
        }

        // The exclusion axis, applied to the SWEEP exactly as ErrorCheck applies it, off-order files included.
        if (exclude is not null)
        {
            var drop = new HashSet<string>(exclude.Names, StringComparer.OrdinalIgnoreCase);
            // Only the names the CALLER TYPED are held against the scope; a group member that is not here is ordinary.
            var inScope = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            foreach (var name in offOrder ?? Array.Empty<(string Name, string Path)>()) inScope.Add(name.Name);
            foreach (var name in exclude.TypedNames)
                if (!inScope.Contains(name))
                    return ScriptCheckResult.Fail(
                        $"exclude= names '{name}', which is not in the scope this sweep would cover.{view.AbsenceClause(name)} " +
                        "Nothing was swept — an exclusion that matches nothing would return the findings you asked to leave out.")
                           with { Epoch = view.Epoch };
            int before = targets.Count, offBefore = offOrder?.Count ?? 0;
            // The whole scope this sweep would have covered, captured before either filter runs.
            int scopeBefore = before + offBefore;
            targets.RemoveAll(drop.Contains);
            excludedFromScope = before - targets.Count;
            if (offOrder is { Count: > 0 })
            {
                offOrder = offOrder.Where(o => !drop.Contains(o.Name)).ToList();
                excludedFromScope += offBefore - offOrder.Count;
            }
            if (targets.Count == 0 && (offOrder is null || offOrder.Count == 0))
                return ScriptCheckResult.Fail(
                    $"exclude= removed every plugin this sweep would have covered ({scopeBefore} in scope, all excluded) — " +
                    "there is nothing left to check. Narrow exclude=, or widen plugins=.")
                       with { Epoch = view.Epoch };
        }

        // The subset claim fires for a RECORD SCOPE and nothing else; property_contains= narrows only two counts and
        // those self-label in the header instead.
        var filterNote = SweepFindings.FilterNote(
            recordScope is not null ? SweepFindings.ScopedCountsClaim : null,
            recordScope?.Label,
            SweepFindings.Describe(classes),
            propFilter is null ? null : $"property_contains='{propFilter}'",
            // Stated whenever the caller PASSED an exclusion, zero included.
            exclude is not null ? $"exclude= left out {excludedFromScope} plugin(s)" : null);

        // A per-sweep .pex property-set cache, so a class many records attach is chain-read once.
        var chainCache = new Dictionary<string, ChainResult>(StringComparer.OrdinalIgnoreCase);

        var reports = new List<RecordScriptFindings>();
        int recordsWithScripts = 0, totalUnbound = 0, totalNull = 0, totalUnverifiable = 0;
        // Split by class so each number's scope is self-evident: an excluded class renders not-checked, not 0.
        int totalUnboundObject = 0, totalUnboundScalar = 0;
        int findingBudget = limit;
        bool capped = false;
        // One unreadable script class produces the same note on every record that attaches it: the first is listed,
        // the repeats counted here.
        var seenUnverifiable = new HashSet<(string Script, string Reason)>();
        int collapsedUnverifiable = 0;
        // counts_only=: the unbound-by-property-name tally, never limit-capped, and built only when a class that feeds
        // it is being collected — null means "not computed", never "empty".
        bool tallyable = classes.HasFlag(ScriptFindingClass.UnboundObject) || classes.HasFlag(ScriptFindingClass.UnboundScalar);
        var histogram = countsOnly && tallyable ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;

        // ONE record's cross-check, shared by both lanes, so the indexed and off-order lanes cannot drift on what
        // counts as a finding.
        void ScanRecord(FormKey fk, IMajorRecordGetter body, string plugin)
        {
            // The record scope, tested BEFORE the VMAD/.pex work so a narrow scope is cheap as well as small.
            if (recordScope is not null && !recordScope.Matches(fk, body)) return;
            if (body is not IHaveVirtualMachineAdapterGetter have) return;
            if (have.VirtualMachineAdapter is not { } vmad) return;
            var scriptEntries = CollectScriptEntries(vmad);
            if (scriptEntries.Count == 0) return;
            recordsWithScripts++;

            var unbound = new List<UnboundProperty>();
            var nulls = new List<NullObjectProperty>();
            var unver = new List<ScriptUnverifiable>();

            foreach (var entry in scriptEntries)
            {
                var scriptClass = entry.Name?.Trim();
                if (string.IsNullOrEmpty(scriptClass))
                {
                    unver.Add(new ScriptUnverifiable(NamelessScript,
                        "the script attachment carries no class name — can't resolve its .pex to check its properties."));
                    continue;
                }

                // Bound-but-null object properties: the slot exists, its Object link is null, and it is not bound to a
                // quest alias instead — an alias-bound property has a null Object by design.
                if (classes.HasFlag(ScriptFindingClass.BoundNull))
                    foreach (var p in entry.Properties)
                        if (p is IScriptObjectPropertyGetter op && op.Object.FormKey.IsNull && op.Alias < 0 && !string.IsNullOrWhiteSpace(p.Name))
                        {
                            var pname = p.Name!.Trim();
                            if (PropOk(pname)) nulls.Add(new NullObjectProperty(scriptClass, pname));
                        }

                var chain = ResolveChain(av, chainCache, scriptClass);
                if (chain.OwnLoadError is not null)
                {
                    unver.Add(new ScriptUnverifiable(scriptClass, chain.OwnLoadError));
                    continue;   // can't read the script's own properties — nothing to compare against
                }

                var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in entry.Properties)
                    if (!string.IsNullOrWhiteSpace(p.Name)) boundNames.Add(p.Name!.Trim());

                foreach (var d in chain.Declared)
                {
                    if (boundNames.Contains(d.Name)) continue;
                    // A scalar with a baked initializer has the author's intended default, so it is NOT a finding.
                    if (!d.IsObjectType && d.HasInitializer) continue;
                    // The caller's class + property-name narrowing.
                    if (!classes.HasFlag(d.IsObjectType ? ScriptFindingClass.UnboundObject : ScriptFindingClass.UnboundScalar)) continue;
                    if (!PropOk(d.Name)) continue;
                    unbound.Add(new UnboundProperty(scriptClass, d.DeclaringScript, d.Name, d.TypeName, d.IsObjectType));
                }

                if (chain.ChainNote is not null)
                    unver.Add(new ScriptUnverifiable(scriptClass, chain.ChainNote));
            }

            if (unbound.Count == 0 && nulls.Count == 0 && unver.Count == 0) return;

            // The true totals are counted regardless of the cap.
            totalUnbound += unbound.Count;
            foreach (var u in unbound) { if (u.IsObjectType) totalUnboundObject++; else totalUnboundScalar++; }
            totalNull += nulls.Count;
            totalUnverifiable += unver.Count;

            // counts_only=: tally and move on, so the per-record roster never forms. Gated on countsOnly, not on the
            // histogram, which is null when both unbound classes are excluded.
            if (countsOnly)
            {
                if (histogram is not null)
                    foreach (var u in unbound)
                        histogram[u.PropertyName] = histogram.TryGetValue(u.PropertyName, out var c) ? c + 1 : 1;
                return;
            }

            var keptUnbound = new List<UnboundProperty>();
            var keptNull = new List<NullObjectProperty>();
            foreach (var u in unbound) { if (findingBudget > 0) { keptUnbound.Add(u); findingBudget--; } else capped = true; }
            foreach (var n in nulls)   { if (findingBudget > 0) { keptNull.Add(n);   findingBudget--; } else capped = true; }

            // Unverifiable notes ride outside the finding budget; the first record carrying a given class+reason lists
            // it and the repeats are counted. A NAMELESS attachment is exempt, because the record is its only identity.
            var keptUnver = new List<ScriptUnverifiable>();
            foreach (var uv in unver)
            {
                if (uv.Script == NamelessScript || seenUnverifiable.Add((uv.Script, uv.Reason))) keptUnver.Add(uv);
                else collapsedUnverifiable++;
            }
            // A record whose only findings were repeats has nothing left to say.
            if (unbound.Count == 0 && nulls.Count == 0 && keptUnver.Count == 0) return;

            reports.Add(new RecordScriptFindings(
                fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, plugin,
                keptUnbound, keptNull, keptUnver));
        }

        foreach (var plugin in targets)
        {
            string? scanError = null;
            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, recordScope?.Types))
                {
                    // Per-record fault isolation: a VMAD Mutagen cannot parse is excluded and accounted per record.
                    try { ScanRecord(fk, body, plugin); }
                    catch (Exception ex) { scanError = RecordFault(scanError, fk, ex); }
                }
            }
            // The plugin enumeration itself faulting is NAMED per-plugin and the sweep continues.
            catch (Exception ex)
            {
                reports.Add(RecordScriptFindings.PluginScanError(plugin,
                    $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"));
            }

            if (scanError is not null)
                reports.Add(RecordScriptFindings.PluginScanError(plugin, scanError));
        }

        // --- off-order files (the pre-enable verify lane): the file's OWN overlay, its script attachments checked
        //     against the .pex chain the ACTIVE order supplies, so a .pex only in the not-yet-enabled mod lands as
        //     UNVERIFIABLE rather than clean. Same fault-isolation contract as the active loop.
        var offOrderScanned = new List<string>();
        foreach (var (name, path) in offOrder ?? Array.Empty<(string Name, string Path)>())
        {
            offOrderScanned.Add(name);
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path)); }
            catch (Exception ex)
            {
                reports.Add(RecordScriptFindings.PluginScanError(name,
                    $"could not open '{path}' as a Skyrim plugin: {ex.GetType().Name}: {ex.Message}"));
                continue;
            }

            string? scanError = null;
            try
            {
                foreach (var rec in SweepScope.RecordsFrom(ov, recordScope))
                {
                    try { ScanRecord(rec.FormKey, rec, name); }
                    catch (Exception ex) { scanError = RecordFault(scanError, rec.FormKey, ex); }
                }
            }
            catch (Exception ex)
            {
                reports.Add(RecordScriptFindings.PluginScanError(name,
                    $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"));
            }
            finally { (ov as IDisposable)?.Dispose(); }

            if (scanError is not null)
                reports.Add(RecordScriptFindings.PluginScanError(name, scanError));
        }

        return new ScriptCheckResult(reports, targets.Count + offOrderScanned.Count, recordsWithScripts, totalUnbound,
                                     totalNull, totalUnverifiable, capped, av.ReadIncomplete, view.ExcludedPlugins, null,
                                     filterNote, histogram is null ? null : SweepFindings.Histogram(histogram), countsOnly,
                                     classes, totalUnboundObject, totalUnboundScalar, propFilter, view.Epoch, limit,
                                     offOrderScanned, collapsedUnverifiable, recordScope?.TypeScopeLabel,
                                     av.RootFailures, ArchiveFailures: av.BsaFailures);
    }

    /// <summary>One record's fault, appended to the plugin's running scan-error line — the same sentence on both
    /// lanes.</summary>
    static string RecordFault(string? soFar, FormKey fk, Exception ex)
        => (soFar is null ? "" : soFar + "; ")
         + $"a record's script adapter could not be read ({FormIdToken.Of(fk)} — {ex.GetType().Name}: {ex.Message})";

    /// <summary>Every script attachment on the record: the adapter's own scripts plus, for a QUEST, each alias's
    /// scripts, which a quest binds in a separate collection. Each entry is the same
    /// <see cref="IScriptEntryGetter"/> the per-attachment cross-check handles.</summary>
    static List<IScriptEntryGetter> CollectScriptEntries(IAVirtualMachineAdapterGetter vmad)
    {
        var entries = new List<IScriptEntryGetter>(vmad.Scripts);
        if (vmad is IQuestAdapterGetter qa)
            foreach (var alias in qa.Aliases)
                entries.AddRange(alias.Scripts);
        return entries;
    }

    // ---- .pex extends-chain resolution ----------------------------------------------------------------

    /// <summary>Every <c>Auto</c> property declared across a script's extends chain (most-derived first, de-duplicated
    /// by name), a <see cref="ChainNote"/> for an ancestor whose <c>.pex</c> could not be read, and
    /// <see cref="OwnLoadError"/> when the script's OWN <c>.pex</c> could not be read.</summary>
    sealed record ChainResult(IReadOnlyList<DeclaredProp> Declared, string? ChainNote, string? OwnLoadError);

    /// <summary>One <c>Auto</c> property the chain declares: its name, the <c>.pex</c> type name, the script it is
    /// declared in, whether it carries a baked initializer, and whether its type is a form/object type.</summary>
    sealed record DeclaredProp(string Name, string TypeName, string DeclaringScript, bool HasInitializer, bool IsObjectType);

    static ChainResult ResolveChain(AssetResolver.AssetView av, Dictionary<string, ChainResult> cache, string scriptClass)
    {
        if (cache.TryGetValue(scriptClass, out var hit)) return hit;
        var result = BuildChain(av, scriptClass);
        cache[scriptClass] = result;
        return result;
    }

    static ChainResult BuildChain(AssetResolver.AssetView av, string scriptClass)
    {
        var byName = new Dictionary<string, DeclaredProp>(StringComparer.OrdinalIgnoreCase);   // first (most-derived) wins
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = scriptClass;
        string? chainNote = null;

        for (int hops = 0; current is { Length: > 0 } && hops < 64; hops++)
        {
            if (!visited.Add(current)) break;   // cycle guard (a malformed hierarchy) — stop, never spin

            if (!TryLoadPex(av, current, out var pex, out var reason))
            {
                if (hops == 0)
                    return new ChainResult(Array.Empty<DeclaredProp>(), null, reason);   // the script's OWN .pex — unverifiable
                chainNote = $"the extends chain was truncated at '{current}' ({reason}) — properties it and its ancestors declare were not checked.";
                break;
            }

            // A single-script .pex has one object named for the class; take the matching object (else the first).
            var obj = pex!.Objects.FirstOrDefault(o => string.Equals(o.Name, current, StringComparison.OrdinalIgnoreCase))
                      ?? pex.Objects.FirstOrDefault();
            if (obj is null) { chainNote ??= $"'{current}.pex' held no script object — its properties were not checked."; break; }

            foreach (var p in obj.Properties)
            {
                if (!p.Flags.HasFlag(PropertyFlags.AutoVar)) continue;   // Auto (editable, silently-defaulting) props only
                var name = p.Name?.Trim();
                if (string.IsNullOrEmpty(name) || byName.ContainsKey(name)) continue;
                var typeName = (p.TypeName ?? "").Trim();
                var backing = obj.Variables.FirstOrDefault(v =>
                    string.Equals(v.Name, p.AutoVarName, StringComparison.OrdinalIgnoreCase));
                bool hasInit = backing is not null && PapyrusDecompiler.InitText(backing.VariableData) is not null;
                byName[name] = new DeclaredProp(name, typeName, current, hasInit, IsObjectType(typeName));
            }

            current = string.IsNullOrWhiteSpace(obj.ParentClassName) ? null : obj.ParentClassName.Trim();
        }

        return new ChainResult(byName.Values.ToList(), chainNote, null);
    }

    /// <summary>Open <c>Scripts\&lt;class&gt;.pex</c> off the winning VFS source, loose or BSA-packed. Returns false
    /// with a NAMED reason, never a silent absence.</summary>
    static bool TryLoadPex(AssetResolver.AssetView av, string scriptClass, out PexFile? pex, out string? reason)
    {
        pex = null; reason = null;
        var rel = $@"Scripts\{scriptClass.Replace(':', '\\')}.pex";   // a namespaced class Ns:Script lives at Scripts\Ns\Script.pex
        var res = av.ResolveForPlacement(rel);
        if (res.Sources.Count == 0)
        {
            // The hedge names the folder it could not read, so a modder has something to act on rather than a warning
            // that one failed; bounded to the first root and a count, because this reason repeats per property.
            reason = $"'{rel}' is not on disk (the script is not compiled, or not in the load order){(av.ReadIncomplete ? " — and a BSA or a loose mod folder failed to read this build, so it may merely be unscanned" + av.RootFailureBrief : "")}.";
            return false;
        }
        var src = res.Sources[0];   // winner first
        try
        {
            if (src.LooseFilePath is not null)
                pex = PexFile.CreateFromFile(src.LooseFilePath, GameCategory.Skyrim);
            else if (src.ArchivePath is not null)
            {
                var bytes = AssetResolver.TryReadArchiveEntry(src.ArchivePath, src.EntryPath);
                if (bytes is null) { reason = $"'{rel}' vanished from '{Path.GetFileName(src.ArchivePath)}' between listing and read."; return false; }
                using var ms = new MemoryStream(bytes);
                pex = PexFile.CreateFromStream(ms, GameCategory.Skyrim);
            }
            else { reason = $"'{rel}' resolved to no readable source."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            pex = null;
            reason = $"Mutagen cannot read '{rel}' ({ex.GetType().Name}: {ex.Message}) — the known unreadable-pex class.";
            return false;
        }
    }

    /// <summary>A property TYPE is a scalar (Int/Float/Bool/String); everything else, a form type or an array, is an
    /// OBJECT type whose unbound value is <c>None</c>.</summary>
    static bool IsObjectType(string typeName)
        => !(typeName.Equals("Int", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Float", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Bool", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("String", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One Auto property declared in a record's attached script, or an ancestor, but NOT bound in the record's
/// VMAD. <paramref name="IsObjectType"/> marks the form/object type whose unbound value is <c>None</c>.</summary>
public sealed record UnboundProperty(string Script, string DeclaringScript, string PropertyName, string PexTypeName, bool IsObjectType);

/// <summary>One object property present in the VMAD but with a NULL Object link — the same <c>None</c> at runtime, but
/// more often intentional, so it is advisory.</summary>
public sealed record NullObjectProperty(string Script, string PropertyName);

/// <summary>A script attachment houseCARL could not fully check, with the NAMED reason — surfaced, never a silent
/// pass.</summary>
public sealed record ScriptUnverifiable(string Script, string Reason);

/// <summary>Every script-property finding on one record: its unbound properties, bound-but-null object properties, and
/// any unverifiable attachments — or, when the plugin's own enumeration faulted, a <paramref name="ScanError"/>.</summary>
public sealed record RecordScriptFindings(
    FormKey Record, string RecordType, string? EditorId, string Plugin,
    IReadOnlyList<UnboundProperty> Unbound, IReadOnlyList<NullObjectProperty> NullObjects,
    IReadOnlyList<ScriptUnverifiable> Unverifiable, string? ScanError = null)
{
    public static RecordScriptFindings PluginScanError(string plugin, string error) =>
        new(FormKey.Null, "", null, plugin,
            Array.Empty<UnboundProperty>(), Array.Empty<NullObjectProperty>(),
            Array.Empty<ScriptUnverifiable>(), error);
}

/// <summary>The result of <see cref="ScriptPropertyCheck.Run"/>: the per-record findings (only records WITH findings),
/// the sweep totals, whether the finding list was capped, whether a BSA or a loose mod folder failed to read this build, the plugins the
/// index build excluded, and — on a scope error — a recoverable <see cref="Error"/> with no reports. Which counts each
/// narrowing does and does not narrow is in docs/architecture/check-families.md.</summary>
public sealed record ScriptCheckResult(
    IReadOnlyList<RecordScriptFindings> Reports,
    int PluginsScanned,
    int RecordsWithScripts,
    int TotalUnbound,
    int TotalNullObject,
    int TotalUnverifiable,
    bool Capped,
    bool ReadIncomplete,
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Error,
    string? FilterNote = null,
    IReadOnlyList<SweepCount>? Histogram = null,
    bool CountsOnly = false,
    ScriptFindingClass Classes = ScriptFindingClass.All,
    int TotalUnboundObject = 0,
    int TotalUnboundScalar = 0,
    string? PropertyContains = null,
    string? Epoch = null,   // the swept INDEXED build's fingerprint; null only on the pre-sweep refusals. OffOrderScanned files are located OUTSIDE the index — their content is not under this fingerprint, so the renders qualify the stamp when any were swept
    int Limit = 0,   // the finding budget this sweep was GIVEN, so the response names the knob to raise off the number actually used
    IReadOnlyList<string>? OffOrderScanned = null,   // the files swept OFF-ORDER: on disk, not in the active order — the pre-enable verify lane
    int UnverifiableCollapsed = 0,   // records whose unverifiable note repeated one already listed for the same script class; counted in TotalUnverifiable, not listed again
    string? TypeScopeLabel = null,   // the scope's types, spelled with any expanded arms, when it covered MORE than one; null otherwise — the same rule the errors family carries, because both families fill one listing over one record stream
    IReadOnlyList<string>? RootFailures = null,   // the loose roots this build could not walk or list, each named with the reason; null or empty when every root read
    IReadOnlyList<string>? AssetWarnings = null,  // the asset build's own warnings (an archive list not found, a kept profile), set by the host on a sweep that ran; null or empty when it had none
    IReadOnlyList<string>? ArchiveFailures = null) // the archives this build could not open, each named with the reason, set on a sweep that ran; null or empty when every archive read
{
    public bool Success => Error is null;
    public static ScriptCheckResult Fail(string error) =>
        new(Array.Empty<RecordScriptFindings>(), 0, 0, 0, 0, 0, false, false,
            new Dictionary<string, string>(), error);
}

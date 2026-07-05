using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda;

namespace HousecarlCore;

/// <summary>
/// The SCRIPT-PROPERTY binding sweep (housecarl_validate_scripts): for every record carrying a VMAD it cross-checks
/// the properties BOUND on the record's script attachment against the properties the attached script's compiled
/// <c>.pex</c> — and its whole <c>extends</c> chain — actually DECLARES, and reports the ones that are declared but
/// left UNBOUND (a silent <c>None</c>). This is the failure the bug store names on 2026-07-04: a quest script declared
/// <c>Spell Property CallVesyraPower Auto</c>, the code guarded on it, the VMAD never bound it → <c>AddSpell(None)</c>
/// no-op while the trace logged "power granted". Maximally misleading (function runs, effect absent, logs clean), and
/// the same class as the CK's STMain auto-add bug — every scripted mod hits it eventually.
///
/// WHY THIS IS A COMPOSE-PRIMITIVES JOB (no new dependency, the <see cref="ErrorCheck"/> pattern): both halves are
/// already modeled. The RECORD half is Mutagen's VMAD (<see cref="IHaveVirtualMachineAdapterGetter"/> →
/// <see cref="IAVirtualMachineAdapterGetter.Scripts"/> → <see cref="IScriptEntryGetter"/> with its bound
/// <see cref="IScriptPropertyGetter"/> list) — PLUS a QUEST's alias-attached scripts (<see cref="IQuestAdapterGetter.Aliases"/>),
/// collected by <see cref="CollectScriptEntries"/> so an alias-bound property isn't silently skipped. The SCRIPT half is
/// Mutagen's <c>.pex</c> model (<see cref="PexObject.Properties"/>,
/// each an <see cref="PexObjectProperty"/> with its <c>Auto</c> flag + declared type) — the same model
/// <see cref="PapyrusDecompiler"/> reads. The two are joined through the VFS: <see cref="AssetResolver"/> resolves
/// <c>Scripts\&lt;class&gt;.pex</c> (loose OR BSA-packed), and each <c>.pex</c> self-declares its parent
/// (<see cref="PexObject.ParentClassName"/>), so the extends chain walks itself with no external hierarchy map.
///
/// WHAT COUNTS AS A FINDING (kept HIGH-SIGNAL so a clean result is trustworthy and the sweep never cries wolf — Q3):
///   • UNBOUND OBJECT property — an <c>Auto</c> property of a FORM/object type (Spell, Quest, ObjectReference, …)
///     declared in the chain but absent from the VMAD. Unbound ⇒ <c>None</c> ⇒ the silent-no-op footgun. HIGH.
///   • UNBOUND SCALAR property with NO initializer — an <c>Auto</c> Int/Float/Bool/String declared without a baked
///     default and absent from the VMAD ⇒ defaults to 0 / 0.0 / false / "" which may be wrong (a "% chance" meant to
///     be 50 silently becomes 0). MEDIUM. A scalar that DOES carry an initializer is NOT flagged — it has the author's
///     intended default, so leaving it unbound is correct, not a bug.
///   • BOUND-BUT-NULL object property — present in the VMAD as a <see cref="IScriptObjectPropertyGetter"/> whose Object
///     link is null AND which is not bound to a quest alias instead (Alias &lt; 0). The same <c>None</c> at runtime, but
///     MORE often intentional (the slot exists, filled at runtime), so it is ADVISORY, ranked below the absent findings.
///
/// HONEST BOUNDARY (Q3 — the sweep claims exactly this, never more, and every degraded mode is NAMED, never silent):
///   • It checks <c>Auto</c> properties only — the CK-editable, silently-defaulting kind. Full properties with custom
///     Get/Set handlers are code-driven and are not flagged.
///   • "Unbound may be intentional" — an object property is sometimes filled by script at runtime; the finding is a
///     flag to VERIFY, not a proven defect. Object-type absences are ranked first because they are the silent-None class.
///   • If a script's OWN <c>.pex</c> is not on disk (uncompiled, or not in the VFS) the attachment is reported
///     UNVERIFIABLE, never silently passed. If an ANCESTOR <c>.pex</c> is missing the chain is truncated with a named
///     note — the properties we COULD read are still checked.
///   • A BSA that failed to read this build is surfaced (<see cref="AssetResolver.AssetView.ReadIncomplete"/>), so a
///     "not found" that may merely be unscanned is never read as an authoritative absence.
///
/// Composes existing primitives only, holds nothing past each record (the <see cref="ErrorCheck"/> Option-B discipline):
/// the per-plugin record stream (<c>RecordsIn</c>), the VFS resolver (one captured <see cref="AssetResolver.AssetView"/>
/// so every <c>.pex</c> lookup + the read-incomplete caveat agree on ONE build), and per-record fault isolation.
/// </summary>
public static class ScriptPropertyCheck
{
    /// <summary>Sweep <paramref name="scope"/> (plugin filenames; null/empty = the whole active order minus excluded
    /// plugins) over the order <paramref name="resolver"/> holds, resolving each attached script's <c>.pex</c> through
    /// <paramref name="assets"/>. <paramref name="limit"/> caps the number of property FINDINGS collected across the
    /// sweep (the true totals are always counted). A bad/excluded scope name fails LOUD (Q3) with no partial result —
    /// the <see cref="ErrorCheck"/> contract, verbatim.</summary>
    public static ScriptCheckResult Run(LoadOrderResolver resolver, AssetResolver assets,
                                        IReadOnlyList<string>? scope, int limit)
    {
        var view = resolver.Capture();
        var av = assets.Capture();          // ONE asset build → every .pex lookup + ReadIncomplete describe the same build

        // --- resolve the plugin set to scan (Q3: a bad or excluded explicit scope name fails loud, never a silent skip). ---
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                if (!view.ContainsPlugin(name))
                    return ScriptCheckResult.Fail($"plugin not in the load order: {name}");
                if (view.ExcludedPlugins.TryGetValue(name, out var why))
                    return ScriptCheckResult.Fail(
                        $"plugin '{name}' was excluded from this session because it could not be parsed ({why}) — fix or remove it upstream; it cannot be checked.");
                targets.Add(name);
            }
        }
        else
        {
            targets = new List<string>();
            foreach (var n in resolver.PluginNames)
                if (!view.ExcludedPlugins.ContainsKey(n)) targets.Add(n);
        }

        // A per-sweep .pex property-set cache: many records attach the SAME script class (SPID-distributed abilities,
        // a mod's shared controller), so the chain read is resolved ONCE per class, not once per record.
        var chainCache = new Dictionary<string, ChainResult>(StringComparer.OrdinalIgnoreCase);

        var reports = new List<RecordScriptFindings>();
        int recordsWithScripts = 0, totalUnbound = 0, totalNull = 0, totalUnverifiable = 0;
        int findingBudget = limit;
        bool capped = false;

        foreach (var plugin in targets)
        {
            string? scanError = null;
            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // PER-RECORD FAULT ISOLATION (the ErrorCheck / cross_plugin_query idiom): a VMAD Mutagen can't parse
                    // is excluded + accounted per record, never an opaque whole-call abort and never a silent skip (Q3).
                    try
                    {
                        if (body is not IHaveVirtualMachineAdapterGetter have) continue;
                        if (have.VirtualMachineAdapter is not { } vmad) continue;
                        var scriptEntries = CollectScriptEntries(vmad);
                        if (scriptEntries.Count == 0) continue;
                        recordsWithScripts++;

                        var unbound = new List<UnboundProperty>();
                        var nulls = new List<NullObjectProperty>();
                        var unver = new List<ScriptUnverifiable>();

                        foreach (var entry in scriptEntries)
                        {
                            var scriptClass = entry.Name?.Trim();
                            if (string.IsNullOrEmpty(scriptClass))
                            {
                                unver.Add(new ScriptUnverifiable("(unnamed)",
                                    "the script attachment carries no class name — can't resolve its .pex to check its properties."));
                                continue;
                            }

                            // Bound-but-null object properties: the slot exists in the VMAD, its Object link is null AND
                            // it is NOT bound to a quest alias instead (Alias >= 0). A ScriptObjectProperty binds EITHER an
                            // Object FormLink OR a quest Alias index (Alias -1 = unset) — an alias-bound property has a null
                            // Object by design, so flagging it as bound-but-null is a false positive (PR #145 review finding 2).
                            foreach (var p in entry.Properties)
                                if (p is IScriptObjectPropertyGetter op && op.Object.FormKey.IsNull && op.Alias < 0 && !string.IsNullOrWhiteSpace(p.Name))
                                    nulls.Add(new NullObjectProperty(scriptClass, p.Name!.Trim()));

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
                                // A scalar with a baked initializer has the author's intended default — leaving it
                                // unbound is correct, so it is NOT a finding (keeps the scalar signal from crying wolf).
                                if (!d.IsObjectType && d.HasInitializer) continue;
                                unbound.Add(new UnboundProperty(scriptClass, d.DeclaringScript, d.Name, d.TypeName, d.IsObjectType));
                            }

                            if (chain.ChainNote is not null)
                                unver.Add(new ScriptUnverifiable(scriptClass, chain.ChainNote));
                        }

                        if (unbound.Count == 0 && nulls.Count == 0 && unver.Count == 0) continue;

                        // Apply the shared finding budget (unbound + null are the capped population; unverifiable notes
                        // are few and always kept). The true totals are counted regardless of the cap.
                        totalUnbound += unbound.Count;
                        totalNull += nulls.Count;
                        totalUnverifiable += unver.Count;

                        var keptUnbound = new List<UnboundProperty>();
                        var keptNull = new List<NullObjectProperty>();
                        foreach (var u in unbound) { if (findingBudget > 0) { keptUnbound.Add(u); findingBudget--; } else capped = true; }
                        foreach (var n in nulls)   { if (findingBudget > 0) { keptNull.Add(n);   findingBudget--; } else capped = true; }

                        reports.Add(new RecordScriptFindings(
                            fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, plugin,
                            keptUnbound, keptNull, unver));
                    }
                    catch (Exception ex)
                    {
                        scanError = (scanError is null ? "" : scanError + "; ")
                                  + $"a record's script adapter could not be read ({fk} — {ex.GetType().Name}: {ex.Message})";
                    }
                }
            }
            // The plugin enumeration itself faulting is NAMED per-plugin and the sweep continues — never the MCP
            // layer's opaque transport error (Q3, the ErrorCheck contract).
            catch (Exception ex)
            {
                reports.Add(RecordScriptFindings.PluginScanError(plugin,
                    $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"));
            }

            if (scanError is not null)
                reports.Add(RecordScriptFindings.PluginScanError(plugin, scanError));
        }

        return new ScriptCheckResult(reports, targets.Count, recordsWithScripts, totalUnbound, totalNull,
                                     totalUnverifiable, capped, av.ReadIncomplete, view.ExcludedPlugins, null);
    }

    /// <summary>Every script attachment on the record: the adapter's own <see cref="IAVirtualMachineAdapterGetter.Scripts"/>
    /// PLUS, for a QUEST, each alias's scripts (<see cref="IQuestAdapterGetter.Aliases"/> →
    /// <see cref="IQuestFragmentAliasGetter.Scripts"/>). A quest binds scripts to its reference aliases in a SEPARATE
    /// collection from its own — a property declared on an alias script (the CK's alias-fragment path) would otherwise be
    /// swept over entirely, a false "clean" on the tool's headline quest use case (PR #145 review finding 1). Each entry
    /// is the same <see cref="IScriptEntryGetter"/> the per-attachment cross-check handles, so alias scripts flow through
    /// the identical property comparison.</summary>
    static List<IScriptEntryGetter> CollectScriptEntries(IAVirtualMachineAdapterGetter vmad)
    {
        var entries = new List<IScriptEntryGetter>(vmad.Scripts);
        if (vmad is IQuestAdapterGetter qa)
            foreach (var alias in qa.Aliases)
                entries.AddRange(alias.Scripts);
        return entries;
    }

    // ---- .pex extends-chain resolution ----------------------------------------------------------------

    /// <summary>Every <c>Auto</c> property declared across a script's extends chain (most-derived class first;
    /// de-duplicated by name so an override does not double-count), any <see cref="ChainNote"/> for an ancestor whose
    /// <c>.pex</c> could not be read (partial — what we could read is still checked), and <see cref="OwnLoadError"/> set
    /// when the script's OWN <c>.pex</c> could not be read (→ the attachment is unverifiable, nothing to compare).</summary>
    sealed record ChainResult(IReadOnlyList<DeclaredProp> Declared, string? ChainNote, string? OwnLoadError);

    /// <summary>One <c>Auto</c> property the chain declares: its <see cref="Name"/>, the <c>.pex</c> <see cref="TypeName"/>,
    /// the script it is <see cref="DeclaringScript"/>d in (the most-derived class or an ancestor), whether it carries a
    /// baked <see cref="HasInitializer"/> default, and whether its type is a FORM/object type (<see cref="IsObjectType"/> —
    /// the silent-None class) versus a scalar.</summary>
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

    /// <summary>Open <c>Scripts\&lt;class&gt;.pex</c> off the winning VFS source (loose OR BSA-packed). Returns false with
    /// a NAMED reason (not on disk / Mutagen can't read it) — never a silent absence (Q3). Loose reads from the file
    /// path; a BSA member is read into memory and opened from a stream (Mutagen 0.53.1 <see cref="PexFile.CreateFromStream"/>).</summary>
    static bool TryLoadPex(AssetResolver.AssetView av, string scriptClass, out PexFile? pex, out string? reason)
    {
        pex = null; reason = null;
        var rel = $@"Scripts\{scriptClass.Replace(':', '\\')}.pex";   // a namespaced class Ns:Script lives at Scripts\Ns\Script.pex
        var res = av.ResolveForPlacement(rel);
        if (res.Sources.Count == 0)
        {
            reason = $"'{rel}' is not on disk (the script is not compiled, or not in the load order){(av.ReadIncomplete ? " — and a BSA failed to read this build, so it may merely be unscanned" : "")}.";
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

    /// <summary>A property TYPE is a scalar (Int/Float/Bool/String) — leaving it unbound defaults to 0/0.0/false/"".
    /// Everything else (a form type, or an array) is an OBJECT type: unbound ⇒ <c>None</c>, the silent-no-op footgun.</summary>
    static bool IsObjectType(string typeName)
        => !(typeName.Equals("Int", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Float", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Bool", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("String", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One Auto property declared in a record's attached script (or an ancestor) but NOT bound in the record's
/// VMAD. <paramref name="Script"/> is the attached class; <paramref name="DeclaringScript"/> is where the property is
/// declared (the class itself or an ancestor it extends). <paramref name="IsObjectType"/> = a form/object type whose
/// unbound value is <c>None</c> (HIGH risk) vs a scalar (MEDIUM).</summary>
public sealed record UnboundProperty(string Script, string DeclaringScript, string PropertyName, string PexTypeName, bool IsObjectType);

/// <summary>One object property present in the VMAD but with a NULL Object link — the same <c>None</c> at runtime,
/// but more often intentional (a slot filled at runtime), so it is advisory.</summary>
public sealed record NullObjectProperty(string Script, string PropertyName);

/// <summary>A script attachment houseCARL could not fully check, with the NAMED reason (its <c>.pex</c> is not on disk /
/// unreadable, an ancestor .pex was missing so the chain was truncated, or the attachment had no class name) —
/// surfaced, never a silent pass (Q3).</summary>
public sealed record ScriptUnverifiable(string Script, string Reason);

/// <summary>Every script-property finding on one record: its unbound properties (the primary findings), bound-but-null
/// object properties (advisory), and any unverifiable script attachments — OR, when the plugin's own record enumeration
/// faulted, a <paramref name="ScanError"/>.</summary>
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

/// <summary>The result of <see cref="ScriptPropertyCheck.Run"/>: the per-record findings (only records WITH findings;
/// clean scripted records are counted in <paramref name="RecordsWithScripts"/> but omitted), the sweep totals, whether
/// the finding list was capped, whether a BSA failed to read this build (so a "not on disk" may be unscanned), the
/// plugins the index build excluded, and — on a Q3 scope error — a recoverable <see cref="Error"/> with no reports.</summary>
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
    string? Error)
{
    public bool Success => Error is null;
    public static ScriptCheckResult Fail(string error) =>
        new(Array.Empty<RecordScriptFindings>(), 0, 0, 0, 0, 0, false, false,
            new Dictionary<string, string>(), error);
}

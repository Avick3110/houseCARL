using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The PUBLIC write cleave (MCP §8.4 Beat C) — the one <c>(edits) → (patch)</c> method both the MCP server
/// (<c>housecarl_set_field</c> / <c>housecarl_bulk_apply</c>) and the <c>apply-proof</c> harness call, so the
/// harness proof transfers to the server BY CONSTRUCTION (the same code path). It factors the proven embryo
/// (<see cref="WriteEngine.RunPatch"/> + <see cref="MultiMasterProof"/>) into a clean reusable surface:
///
///   resolve each edit's WINNER across the load order → derive its RecordType from the runtime record →
///   pre-flight EVERY edit through <see cref="CorpusRulebook"/> (refuse the WHOLE call if ANY rejects — Q3, no
///   partial patches) → override each winner into ONE patch mod (<see cref="WriteEngine.GenericGetOrAddAsOverride"/>)
///   → <see cref="WriteEngine.ApplyVerb"/> each → serialize ONCE with the FULL known-master set
///   (<see cref="WriteEngine.WritePatch(SkyrimMod,System.Collections.Generic.IReadOnlyList{ISkyrimModGetter},string)"/>,
///   the proven multi-master path) → re-open and report masters.
///
/// OUTPUT MODEL (Aaron-locked 2026-06-01): <b>Option 1 — one complete .esp per call</b>. A fresh patch by default;
/// <see cref="Apply"/> with <c>extend:true</c> opens an EXISTING patch and adds to it — the <c>into=</c> capability
/// Aaron required for multi-session large-patch building via handoffs. Extend is file-based: the disk <c>.esp</c> IS
/// the accumulating state, so it survives a server restart / a session boundary with NO server-held state (the locked
/// <c>Stateless</c> transport stays clean). (Option 2, a server-held accumulating session patch, is a deferred 1.x item.)
///
/// ORIGINALS UNTOUCHED is structural (CLAUDE.md §1): this only ever WRITES <paramref name="outPath"/> (sandboxed to the
/// server's OutputDir by the caller); every original is opened read-only as a lazy overlay by the resolver and never
/// written. Cross-master is CLOSED — a patch referencing forms across several plugins serializes with a lean
/// only-referenced master header (proven + xEdit-confirmed 2026-06-01).
/// </summary>
public static class WritePatchBuilder
{
    /// <summary>One edit: locate a record by <see cref="Target"/> (its FormKey), apply <see cref="Verb"/> at
    /// <see cref="Path"/>. RecordType is NOT declared by the caller — it is derived from the resolved winner's runtime
    /// type (the record itself is authoritative), so an edit can never disagree with what it targets. Mirrors the
    /// content of <see cref="WriteRequest"/> minus its RecordType.</summary>
    public sealed record PatchEdit
    {
        public required FormKey Target { get; init; }
        public required string[] Path { get; init; }
        public required string Verb { get; init; }
        public string? Key { get; init; }
        public string? Value { get; init; }
        public string[]? Values { get; init; }
        public Dictionary<string, string>? Entries { get; init; }
        public StructSpec? Struct { get; init; }
    }

    /// <summary>Per-edit result. On a successful call every op has <see cref="Applied"/>=true (all-or-nothing);
    /// <see cref="After"/> is a best-effort read-back of the edited leaf (xEdit remains the authority).</summary>
    public sealed record OpResult(FormKey Target, string RecordType, string Label, bool Applied, string? Error, string? After);

    /// <summary>One record read back IN FULL from the WRITTEN patch file (opt-in — the pre-enable verify loop,
    /// wishlist #3 re-scoped / HCBR-2026-06-11-02 wave (b)): every modeled field, deep, read off the re-opened
    /// on-disk file — the same bytes MO2 will load — so the caller can confirm the WHOLE record (untouched fields
    /// included) landed intact without enabling the patch. <see cref="Error"/> names a record the re-opened file
    /// failed to yield (a real inconsistency, Q3) — never silently absent.</summary>
    public sealed record FullReadback(FormKey Target, RecordFields? Record, string? Error);

    /// <summary>The call outcome. <see cref="Error"/> non-null ⇒ the whole call was refused (no patch written) with a
    /// named, recoverable reason (Q3). Otherwise the patch at <see cref="OutputPath"/> carries every op; <see cref="Masters"/>
    /// is its (lean, only-referenced) master header; <see cref="Extended"/> says whether an existing patch was grown;
    /// <see cref="ReadBack"/> is the opt-in full read-back of every record this call touched (null unless requested).</summary>
    public sealed record PatchOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<string> Masters, IReadOnlyList<OpResult> Ops, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        public static PatchOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0);
    }

    /// <summary>One record dropped by <see cref="RemoveRecords"/> — its FormKey, the catalog type, and the editorid (if
    /// any), captured during the present-check so the confirmation says WHAT was removed.</summary>
    public sealed record RemovedRecord(FormKey Target, string RecordType, string? EditorId);

    /// <summary>The outcome of a <see cref="RemoveRecords"/> call. <see cref="Error"/> non-null ⇒ the whole call was
    /// refused (no file written) with a named, recoverable reason (Q3 — e.g. a target the patch doesn't carry).
    /// Otherwise <see cref="Removed"/> lists every dropped record; <see cref="Masters"/> is the patch's now-lean header
    /// (a master orphaned by the removal is gone); <see cref="RemainingRecords"/>=0 means the patch is now inert.</summary>
    public sealed record RemovalOutcome(
        bool Success, string? Error, string OutputPath,
        IReadOnlyList<RemovedRecord> Removed, IReadOnlyList<string> Masters, int RemainingRecords, long Bytes)
    {
        public static RemovalOutcome Fail(string error) =>
            new(false, error, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0);
    }

    /// <summary>One brand-new record to create: its (caller-DECLARED) <see cref="RecordType"/> catalog name, the required
    /// <see cref="EditorId"/> it'll be referenced by, and the field <see cref="Edits"/> to apply to it (each a
    /// <see cref="WriteRequest"/> rooted at the create type — the same shape <see cref="WriteEngine.ApplyVerb"/> consumes).
    /// Unlike <see cref="PatchEdit"/>, RecordType is declared, not derived — there's no existing winner to read it from.</summary>
    public sealed record CreateSpec
    {
        public required string RecordType { get; init; }
        public required string EditorId { get; init; }
        public required IReadOnlyList<WriteRequest> Edits { get; init; }

        /// <summary>Optional — the PARENT this record nests UNDER (nested-create, Layer A). Either an EXISTING parent's
        /// FormKey (<c>XXXXXX:Plugin.esp</c> — add a line to an existing topic, a ref to an existing cell) OR the
        /// <see cref="EditorId"/> of a record created EARLIER in this same call (the one-shot "topic + its lines" unit).
        /// A FormKey is recognised by parsing; anything else is a same-call sibling EditorId. Null ⇒ a flat top-level
        /// record (the existing create path, unchanged).</summary>
        public string? ParentRef { get; init; }

        /// <summary>Optional — which of the parent's child-collections to add into, BY NAME (the outcome-(ii)
        /// discriminator, e.g. a Cell's <c>Persistent</c>/<c>Temporary</c>). Null ⇒ the unique collection that accepts
        /// this child type (outcome (i), e.g. a DialogTopic's one <c>Responses</c> list). Ignored when
        /// <see cref="ParentRef"/> is null.</summary>
        public string? IntoCollection { get; init; }

        /// <summary>Optional — the exterior-cell GRID as "X,Y" (the coordinate-keyed §4-(b) create path). Set on a
        /// <c>Cell</c> create, it places the new cell into a Worldspace's block tree by block=floor(grid/32),
        /// subblock=floor(grid/8) (STEP-0 proven vs 4000 vanilla cells); <see cref="ParentRef"/> must then resolve to a
        /// Worldspace. A <c>Cell</c> create with NO <see cref="Grid"/> and NO <see cref="ParentRef"/> ⇒ an INTERIOR cell
        /// (self-files into the top-level Cells group by its own FormID). Ignored for non-Cell types.</summary>
        public string? Grid { get; init; }
    }

    /// <summary>One record created by <see cref="CreateRecords"/> — its freshly-allocated <see cref="FormKey"/> (the
    /// caller can't predict it; it's the local 0x800+ id), its type + editorid, and the per-field op results.
    /// <see cref="ReplacedExisting"/> = this create REPLACED a record the patch already defined with the same
    /// editorid (an into= re-run): same FormKey, prior contents — including any set_field edits made since the
    /// original create — discarded and rebuilt from this call's spec. MUST be surfaced to the user (Q3 — a replace
    /// is never silent).</summary>
    public sealed record CreatedRecord(FormKey FormKey, string RecordType, string EditorId, IReadOnlyList<OpResult> Ops,
        bool ReplacedExisting = false);

    /// <summary>The outcome of a <see cref="CreateRecords"/> call. <see cref="Error"/> non-null ⇒ the whole call was
    /// refused (no file written) with a named, recoverable reason (Q3 — missing editorid, an un-createable type, a rejected
    /// edit). Otherwise <see cref="Created"/> lists every new record with its allocated FormKey; <see cref="Masters"/> is
    /// the patch's (lean, derived) header; <see cref="Extended"/> says whether an existing patch was grown;
    /// <see cref="ReadBack"/> is the opt-in full read-back of every record this call created (null unless requested).</summary>
    public sealed record CreateOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<CreatedRecord> Created, IReadOnlyList<string> Masters, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>The voice-coverage report for the INFOs this call created (Layer B unit B) — null unless the call
        /// created ≥1 dialogue line. Filled by the SERVICE post-write (it owns the live AssetResolver), NOT by the core
        /// create path: a `with { Voice = … }` enrich on the returned outcome, so <see cref="CreateRecords"/> stays a
        /// pure record-write and the asset-layer dependency lives in the service. See <see cref="VoiceCheck"/>.</summary>
        public VoiceReport? Voice { get; init; }

        /// <summary>The result-script binding report for the INFOs this call created (Layer B unit C / per-create
        /// structural check) — null unless the call created ≥1 scripted dialogue line. Filled by the SERVICE post-write
        /// the SAME way as <see cref="Voice"/> (it owns the live AssetResolver), so <see cref="CreateRecords"/> stays a
        /// pure record-write. See <see cref="DialogueScriptCheck"/>.</summary>
        public ScriptBindingReport? ScriptBinding { get; init; }

        /// <summary>The structural-shell report for the cells this call created (the coordinate-keyed §4-(b) teeth —
        /// what world content the author must still provide; Aaron 2026-06-20: no CK work) — null unless the call created
        /// ≥1 Cell. Filled by the SERVICE post-write the SAME way as <see cref="Voice"/>, so <see cref="CreateRecords"/>
        /// stays a pure record-write. See <see cref="CellShellCheck"/>.</summary>
        public CellShellReport? CellShell { get; init; }

        public static CreateOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0);
    }

    /// <summary>How deep the full read-back reads each written record — the same rationale as the conflict diff's
    /// depth: deep enough to reach every modeled scalar leaf (condition payloads included — the report's otherwise
    /// unverifiable perk gate), bounded by the modeled-corpus boundary + ReadEngine's expansion cap, whose
    /// truncation sentinel stays an explicit note (Q3).</summary>
    public const int FullReadbackDepth = 16;

    /// <summary>The coordinate-keyed cell-create kind for a spec (the §4-(b) path): <see cref="None"/> = the flat or
    /// FormKey-nested path (unchanged); <see cref="Exterior"/> = a Cell placed under a Worldspace by grid
    /// (<c>block=floor(grid/32)</c>, <c>subblock=floor(grid/8)</c>); <see cref="Interior"/> = a parentless Cell self-filed
    /// into the top-level Cells group by its own FormID digits. See <c>WriteEngine.AddExteriorCell</c>/<c>AddInteriorCell</c>.</summary>
    enum CellCreate { None, Exterior, Interior }

    /// <summary>Is <paramref name="recordType"/> the <c>Cell</c> record type (case-insensitive — the system's catalog convention)?</summary>
    static bool IsCellType(string recordType) => string.Equals(recordType, nameof(Cell), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse an exterior-cell grid "X,Y" into two ints (whitespace-tolerant). False ⇒ malformed (the call refuses loud, Q3).</summary>
    static bool TryParseGrid(string? grid, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(grid)) return false;
        var parts = grid.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>Build/extend a patch from <paramref name="edits"/> and serialize it to <paramref name="outPath"/>.
    /// <paramref name="extend"/>=false writes a fresh patch (the ModKey = the output filename); =true opens the existing
    /// patch at <paramref name="outPath"/> mutably and adds to it (the <c>into=</c> path). All-or-nothing: any
    /// resolve/pre-flight rejection refuses the whole call with no file written (Q3). <paramref name="fullReadback"/>
    /// additionally reads every touched record back IN FULL off the re-opened written file (the pre-enable verify
    /// loop — see <see cref="FullReadback"/>).</summary>
    public static PatchOutcome Apply(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string outPath, bool extend, bool fullReadback = false)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Per-call overlay session (Option B): every source plugin this write reads (winner bodies, the nested link
        // cache, the known-master set) is opened THROUGH it and disposed when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();

        // --- Phase 1: resolve winner + derive RecordType + pre-flight EVERY edit. Collect ALL problems (so the caller
        //     sees every fix at once), then refuse the whole call if any (Q3 — never a silently-partial patch).
        //     ONE captured view answers EVERY edit (2026-06-12 hunt F5): the per-edit single-shot resolves each took
        //     a fresh capture, so a freshness rebuild landing mid-loop (a concurrent read's refresh) could resolve
        //     two edits of ONE call against two different builds' winners — a silently MIXED patch
        //     (freshness-capture-guard arm 4, RED pre-fix: 2 of 12 hammered multi-op writes mixed). The write is one
        //     logical operation; it reads one build — the same HCBR-2026-06-11-02 discipline the read service follows. ---
        var view = resolver.Capture();
        var resolved = new List<(PatchEdit edit, IMajorRecordGetter body, string winnerPlugin, WriteRequest req, string label)>(edits.Count);
        var problems = new List<string>();
        foreach (var e in edits)
        {
            var w = view.ResolveWinner(e.Target);
            if (w is null) { problems.Add($"{e.Target}: not present in the load order ({view.PluginCount} plugins)."); continue; }
            var body = view.GetRecord(session, w.Value.WinnerPlugin, e.Target);
            if (body is null) { problems.Add($"{e.Target}: winner '{w.Value.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }

            var recType = RecordNaming.StripOverlay(body.GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct,
            };
            var label = Label(req);
            if (rulebook.Validate(req) is { } reject) { problems.Add($"{recType} {e.Target} [{label}]: {reject}"); continue; }
            resolved.Add((e, body, w.Value.WinnerPlugin, req, label));
        }
        if (problems.Count > 0)
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; NO patch written:\n  - "
                + string.Join("\n  - ", problems));

        // --- Phase 2: open (extend) or create the patch mod. The serializer ties the output filename to the ModKey. ---
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return PatchOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return PatchOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // --- Phase 3: override each winner into the ONE patch mod, then apply. A flat record needs no link cache; a
        //     NESTED record (Cell/Placed*/INFO/Navmesh/Landscape) gets the winner overlay's cache built on demand
        //     (costly → only here, never for the flat common case, never held). A throw here AFTER pre-flight passed is
        //     a real engine inconsistency — fail the WHOLE call (no partial patch), surfaced not swallowed (Q3). ---
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, winnerPlugin, req, label) in resolved)
        {
            try
            {
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(winnerPlugin) : null;
                var ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, body, cache);
                WriteEngine.ApplyVerb(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, TryReadAfter(ov, req)));
            }
            catch (ExpectedApplyRejectionException ex)
            {
                // An EXPECTED apply-time refusal pre-flight legitimately can't pre-empt (live state — e.g. a duplicate dict
                // key): render its clean guidance, NOT the gate/apply-inconsistency wrapper. All-or-nothing still holds —
                // the whole call is refused and no file is written (gap-audit Finding 3).
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (no patch written)");
            }
            catch (MalformedTargetDataException ex)
            {
                // The THIRD category: the TARGET record's own data is malformed (a present-but-null element/entry) — neither
                // a user input error nor a gate/apply inconsistency. Render it accurately, NOT under the "pre-flight ACCEPTED
                // … a real inconsistency" wrapper (which would mislabel pre-existing bad source data as an engine bug).
                // All-or-nothing holds — no file written (PR #83 follow-up Gap 2).
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (no patch written)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {e.Target}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- Phase 4: serialize ONCE with the FULL known-master set (multi-master). Mutagen keeps the header lean
        //     (only-referenced); a referenced master genuinely absent from the order still fails loud (Q3). ---
        // Two-part active-patch self-lock guard (Heisen 2026-06-08 + PR #24 review): no mapped handle on the file we're
        // about to write may survive to the serialize, from ANY source. ReleaseOverlay closes one we already hold (Apply's
        // Phase-1 winner fetch, when re-editing the patch's OWN override — there the winner IS the target); AllMastersExcept
        // keeps the target out of the master set. (writelock-probe / writelock-apply-probe; both halves guarded.)
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex)
            { return PatchOutcome.Fail($"writing the patch failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // --- Phase 5: re-open the written patch and report its master header — and, on request, each touched
        //     record's FULL read-back off that same re-opened file (the on-disk bytes, not the in-memory mod — the
        //     strongest pre-enable confirmation). Dispose the overlay so the patch file isn't left mmap'd (a later
        //     extend re-opens it; the server writes many over its lifetime). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, outPath, extend, masters, ops, bytes) { ReadBack = readBack };
    }

    /// <summary>
    /// Remove WHOLE records the patch ITSELF carries — literal drop-from-plugin (<c>mod.Remove(FormKey)</c>), NOT
    /// flag-as-deleted (Aaron-locked 2026-06-02). The companion to <see cref="Apply"/>: where Apply overrides a
    /// load-order winner INTO the patch, this drops a record OUT of it — a created record, or an override the patch
    /// accumulated via <c>into=</c>. A master's own record can't be literally removed (it lives in the master); only
    /// the patch's override of it is dropped, so the load-order winner reverts by absence.
    ///
    /// <para>ONE call shape serves flat AND nested groups: Mutagen's <c>Remove(FormKey, Type, throwIfUnknown)</c> reaches
    /// every group (incl. the nested Cell/Placed*/INFO/Navmesh/Landscape families) — no flat-vs-nested fork, no
    /// parent-chain reconstruction (proven by the remove-record-probe: the bare <c>Remove(FormKey)</c> is [Obsolete],
    /// and the typed overload was measured to remove a nested Cell too, Q7). Clean-masters rides along for free: the
    /// serialize re-derives the header from the SURVIVING records' links, so a master orphaned by the removal drops
    /// automatically (probe Q5).</para>
    ///
    /// <para>PRESENT-CHECK FIRST (Q3 — no silent non-removal): <c>Remove</c> is a silent <c>void</c> no-op on a
    /// key the patch doesn't carry, so every target is verified carried before any removal, and the WHOLE call is refused
    /// (nothing written) if ANY isn't — the all-or-nothing contract <see cref="Apply"/> uses. The patch must already
    /// exist (removal targets a patch houseCARL created); the caller resolves + ownership-gates the path.</para>
    /// </summary>
    public static RemovalOutcome RemoveRecords(LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string outPath)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Per-call overlay session (Option B): the known-master set for the re-serialize is opened through it and
        // disposed when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();

        var fileName = Path.GetFileName(outPath);
        if (!File.Exists(outPath))
            return RemovalOutcome.Fail($"cannot remove: no existing patch at {outPath}. Removal targets a patch houseCARL already created.");

        SkyrimMod patchMod;
        try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex) { return RemovalOutcome.Fail($"cannot open patch to remove from ({fileName}): {ex.GetType().Name}: {ex.Message}"); }

        // Present-check: index what the patch ACTUALLY carries (one enumeration — walks flat + nested), so a target the
        // patch doesn't define is refused loud rather than silently no-op'd by Remove. Captures type+editorid for the
        // report AND the runtime type, which routes the typed Remove straight to the record's group below.
        var carried = new Dictionary<FormKey, (string type, string? edid, Type runtime)>();
        foreach (var r in patchMod.EnumerateMajorRecords())
            carried[r.FormKey] = (RecordNaming.StripOverlay(r.GetType().Name), r.EditorID, r.GetType());

        var problems = new List<string>();
        var toRemove = new List<RemovedRecord>(targets.Count);
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;   // de-dup repeated targets in one call
            if (!carried.TryGetValue(fk, out var info))
            {
                problems.Add(
                    $"{fk}: not carried by patch '{fileName}' — only a record the patch ITSELF defines (a created record " +
                    "or an accumulated override) can be removed; a master's record can't be literally removed, only its " +
                    "override dropped (and this patch has no override of it).");
                continue;
            }
            toRemove.Add(new RemovedRecord(fk, info.type, info.edid));
        }
        if (problems.Count > 0)
            return RemovalOutcome.Fail(
                $"refused — {problems.Count} of {targets.Count} target(s) not carried by the patch; NOTHING removed:\n  - "
                + string.Join("\n  - ", problems));

        // Literal drop-from-group (NOT flag-as-deleted). The typed overload Remove(FormKey, Type, throwIfUnknown) is
        // Mutagen's blessed path (the bare Remove(FormKey) is [Obsolete] — "use as a last resort"), and the
        // remove-record-probe (Q7) proved it reaches NESTED records (Cell/Placed*/INFO/Navmesh/Landscape) too, not just
        // flat groups — so ONE call shape serves every record type by construction. The runtime type captured in the
        // present-check routes it straight to the right group; throwIfUnknown:true keeps an unrecognized type loud (Q3),
        // never a silent no-op. A throw here AFTER the present-check passed is a real engine inconsistency — surfaced.
        try
        {
            foreach (var rr in toRemove)
                ((IMajorRecordEnumerable)patchMod).Remove(rr.Target, carried[rr.Target].runtime, throwIfUnknown: true);
        }
        catch (Exception ex)
        {
            return RemovalOutcome.Fail(
                $"present-check passed but Remove threw — a real engine inconsistency, surfaced not swallowed (Q3): "
                + $"{ex.GetType().Name}: {ex.Message}");
        }

        // Serialize ONCE with the full known-master set; Mutagen keeps the header lean (only-referenced), so a master
        // orphaned by the removal drops here automatically. A referenced master genuinely absent still fails loud (Q3).
        // Two-part active-patch self-lock guard (Heisen 2026-06-08 + PR #24 review): no mapped handle on the file we're
        // about to write may survive to the serialize, from ANY source. ReleaseOverlay closes one we already hold (Apply's
        // Phase-1 winner fetch, when re-editing the patch's OWN override — there the winner IS the target); AllMastersExcept
        // keeps the target out of the master set. (writelock-probe / writelock-apply-probe; both halves guarded.)
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex) { return RemovalOutcome.Fail($"writing the patch after removal failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Re-open: report the (possibly shrunk) master header + how many records remain (0 ⇒ the patch is now an inert
        // header-only plugin the user can disable/delete). Dispose the overlay so the file isn't left mmap'd for a later call.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            remaining = back.EnumerateMajorRecords().Count();
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { return RemovalOutcome.Fail($"records removed + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new RemovalOutcome(true, null, outPath, toRemove, masters, remaining, bytes);
    }

    /// <summary>One record to FORWARD: take plugin <see cref="FromPlugin"/>'s version of <see cref="Target"/> and carry
    /// it INTO the patch as an override (xEdit's "copy as override into"). Unlike <see cref="PatchEdit"/> there is no
    /// path/verb — the WHOLE source record is deep-copied verbatim, so the SOURCE plugin (not the load-order winner)
    /// decides the content.</summary>
    public sealed record ForwardSpec
    {
        public required FormKey Target { get; init; }
        public required string FromPlugin { get; init; }
    }

    /// <summary>One record forwarded by <see cref="ForwardRecords"/> — its FormKey + type + editorid, the source plugin
    /// whose version was copied, and the load-order winner it will out-rank once the patch is enabled (so the caller
    /// sees what the forward CHANGES). <see cref="WasAlreadyWinner"/>=true ⇒ the forwarded version WAS already the
    /// winner, so this override is a redundant no-op copy — surfaced, never silent (Q3).</summary>
    public sealed record ForwardedRecord(
        FormKey Target, string RecordType, string? EditorId, string FromPlugin, string PriorWinner, bool WasAlreadyWinner);

    /// <summary>The outcome of a <see cref="ForwardRecords"/> call. <see cref="Error"/> non-null ⇒ the whole call was
    /// refused (no file written) with a named, recoverable reason (Q3 — a source plugin not in the order / excluded /
    /// the output itself / one that doesn't define the target). Otherwise the patch at <see cref="OutputPath"/> carries
    /// each forwarded record; <see cref="Masters"/> is its lean header (the forwarded content's ORIGIN master + whatever
    /// it references — NOT the source plugin, which is copied FROM, never mastered ON). <see cref="ReadBack"/> is the
    /// opt-in full read-back of every forwarded record (null unless requested).</summary>
    public sealed record ForwardOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<ForwardedRecord> Forwarded, IReadOnlyList<string> Masters, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        public static ForwardOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0);
    }

    /// <summary>The outcome of a <see cref="CreatePlugin"/> call. <see cref="Error"/> non-null ⇒ the call was refused
    /// (no file written) with a named reason (Q3). Otherwise the empty plugin lives at <see cref="OutputPath"/> with the
    /// exact <see cref="PluginName"/> the caller asked for (never auto-suffixed — a header-only plugin's basename is
    /// load-bearing). <see cref="RecordCount"/> is 0 by definition (re-read off the written file to confirm, not
    /// assumed); <see cref="Masters"/> is empty (an empty plugin references nothing — exactly what the CK stamps on one);
    /// <see cref="Esl"/> echoes the light-master flag as it round-tripped through the write.</summary>
    public sealed record CreatePluginOutcome(
        bool Success, string? Error, string OutputPath, string PluginName, bool Esl,
        IReadOnlyList<string> Masters, int RecordCount, long Bytes)
    {
        public static CreatePluginOutcome Fail(string error) =>
            new(false, error, "", "", false, Array.Empty<string>(), 0, 0);
    }

    /// <summary>
    /// FORWARD a NAMED plugin's version of each record INTO the patch as an override — xEdit's "copy as override into",
    /// the inverse of <see cref="Apply"/>'s winner-override. Where Apply/Create author NEW content, this re-asserts an
    /// EARLIER plugin's already-authored version over a later override (the HCBR-2026-06-21 case: restore ATweaks'
    /// Searing-Sun spell over Sacrilege's). The whole source record is DEEP-COPIED via
    /// <see cref="WriteEngine.GenericGetOrAddAsOverride"/> — the SAME override primitive <see cref="Apply"/> uses, so
    /// it's generic over EVERY record type by construction (the nested Cell/Placed*/INFO/Navmesh/Landscape families
    /// included, via the source link cache). There is NO field edit, so NO rulebook pre-flight: a complete, valid source
    /// record copied verbatim is legal by definition (the rulebook validates field EDITS, of which this has none).
    ///
    /// <para>Forwarding copies CONTENT, it does not add a master: the patch overrides the target's ORIGIN FormKey with
    /// the source's body, so the source plugin is read FROM, never recorded as a master — the resulting header carries
    /// the origin master + whatever the forwarded content references (exactly what xEdit's "copy as override into a new
    /// patch" produces). Forwarding the ORIGIN master's own version reverts the record to vanilla.</para>
    ///
    /// <para>Q3 — every refusal is named and the WHOLE call is all-or-nothing (no partial patch): a source plugin not in
    /// the order, EXCLUDED (unparseable), the OUTPUT patch itself (a no-op self-forward), the SAME target twice, or one
    /// that simply doesn't DEFINE the target — the distinct null shapes
    /// <see cref="LoadOrderResolver.IndexView.GetRecord"/> returns, told apart here so the caller gets the real reason.
    /// A forwarded version that WAS already the winner is reported (<see cref="ForwardedRecord.WasAlreadyWinner"/>), not
    /// silently dropped. <paramref name="extend"/>=false writes a fresh patch; =true adds to an existing one (into=).
    /// <paramref name="fullReadback"/> reads every forwarded record back IN FULL off the written file (the pre-enable
    /// verify loop — see <see cref="FullReadback"/>).</para>
    /// </summary>
    public static ForwardOutcome ForwardRecords(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string outPath, bool extend, bool fullReadback = false)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Per-call overlay session (Option B): every source plugin this forward reads is opened THROUGH it and disposed
        // when the method returns — no handle held at rest (the same model Apply / RemoveRecords / CreateRecords use).
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(outPath);

        // --- Phase 1: resolve each source body from its NAMED plugin (NOT the load-order winner) + classify any miss
        //     (Q3 — collect ALL problems, then refuse the whole call if any). ONE captured build answers every spec (the
        //     hunt-F5 one-view discipline Apply follows: a freshness rebuild mid-loop can't mix two builds' resolutions).
        //     PERF (accuracy-over-perf, not a blocker): each GetRecord re-enumerates from_plugin's overlay, so N targets
        //     from ONE source = N in-memory walks of that overlay (the file is opened ONCE — the session caches it). Fine
        //     for realistic use (re-assert a few of a mod's records); if a "forward 100 records out of a huge overhaul"
        //     case ever bites, the clean fix is a single-pass batch fetch keyed by from_plugin (group specs by source,
        //     enumerate the overlay once collecting all wanted FormKeys) — deferred until measured. ---
        var view = resolver.Capture();
        var resolved = new List<(ForwardSpec spec, IMajorRecordGetter body, string priorWinner, bool wasWinner)>(specs.Count);
        var problems = new List<string>();
        var seen = new HashSet<FormKey>();
        foreach (var s in specs)
        {
            if (!seen.Add(s.Target))
            { problems.Add($"{s.Target}: forwarded more than once in this call — name each target once (one source per record)."); continue; }
            if (string.Equals(s.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase))
            { problems.Add($"{s.Target}: from_plugin '{s.FromPlugin}' is the output patch itself — forwarding a patch's own version into itself is a no-op; name the EARLIER plugin whose version you want to re-assert."); continue; }
            if (!view.ContainsPlugin(s.FromPlugin))
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' is not in the load order — name an active plugin that defines or overrides this record."); continue; }
            if (view.ExcludedPlugins.TryGetValue(s.FromPlugin, out var why))
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
            var body = view.GetRecord(session, s.FromPlugin, s.Target);
            if (body is null)
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' is in the load order but does NOT define or override this record (it doesn't touch it) — there is no version of it there to forward."); continue; }
            var w = view.ResolveWinner(s.Target);
            resolved.Add((s, body, w?.WinnerPlugin ?? "(none)",
                w is { } wi && string.Equals(wi.WinnerPlugin, s.FromPlugin, StringComparison.OrdinalIgnoreCase)));
        }
        if (problems.Count > 0)
            return ForwardOutcome.Fail(
                $"refused — {problems.Count} of {specs.Count} forward(s) rejected; NO patch written:\n  - "
                + string.Join("\n  - ", problems));

        // --- Phase 2: open (extend) or create the patch mod (identical to Apply Phase 2). ---
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return ForwardOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return ForwardOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // --- Phase 3: deep-copy each source body INTO the patch as an override. NO ApplyVerb — the copy IS the forward
        //     (GenericGetOrAddAsOverride duplicates the source's whole content; a nested record gets the source overlay's
        //     link cache on demand, the SAME session.LinkCacheFor path Apply uses + guards). A throw here is a real
        //     engine inconsistency — fail the WHOLE call (no partial patch), surfaced not swallowed (Q3). ---
        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner) in resolved)
        {
            try
            {
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(spec.FromPlugin) : null;
                WriteEngine.GenericGetOrAddAsOverride(patchMod, body, cache);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {spec.Target} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // --- Phase 4: serialize ONCE with the FULL known-master set (identical to Apply Phase 4 — release any overlay
        //     on the target before the serialize + keep the target out of the master set; the two-part self-lock guard). ---
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"writing the patch failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // --- Phase 5: re-open + report the (lean, derived) master header + bytes — and, on request, each forwarded
        //     record's FULL read-back off that same re-opened file (see Apply's Phase 5). Dispose the overlay after. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, outPath, extend, forwarded, masters, bytes) { ReadBack = readBack };
    }

    /// <summary>
    /// Create an EMPTY, HEADER-ONLY plugin — a valid <c>TES4</c> header and ZERO records (HCBR-2026-06-19-02). The
    /// whole point is a plugin that exists purely so its BASENAME resolves: the artifact SKSE configs that bind by
    /// plugin name need (a CraftingCategories-style trigger that must ship <c>Foo.esp</c> so <c>Foo.json</c> loads), a
    /// placeholder ESL for FormID reservation, a deliberate empty master, a dummy plugin to satisfy a dependency check.
    /// It is the clean primitive behind those: where the record-centric create/forward paths can only materialise a
    /// plugin by giving it a record (forcing an unwanted conflict-tree participant — the report's redundant backpack
    /// override), this authors NO record at all.
    ///
    /// <para>CORNERSTONE-CLEAN: a <see cref="SkyrimMod"/> with no records added IS a header-only plugin — there is no
    /// per-type anything here, so it is trivially generic. ZERO MASTERS (Aaron 2026-06-23): an empty plugin references
    /// nothing, so it carries no masters — passing an EMPTY known-master set means <see cref="WriteEngine.WritePatch"/>
    /// forces no baseline masters either (its baseline force-include filters to masters present in the set; the empty
    /// set yields none). That is exactly what the Creation Kit stamps on a truly empty plugin, so it honours the same
    /// "match the CK" convention the baseline-master rule is built on. The atomic staged write + FormID floor still
    /// apply (every product write funnels through that one chokepoint).</para>
    ///
    /// <para>Q3 — the written file is RE-READ to confirm it is what was promised (0 records, the ESL flag as requested)
    /// before reporting success; a mismatch refuses loud rather than return a wrong artifact. Caller resolves
    /// <paramref name="outPath"/> (the service uses an EXACT, never-suffixed name with a loud collision refusal — the
    /// basename must be precise for the trigger to bind).</para>
    /// </summary>
    public static CreatePluginOutcome CreatePlugin(string outPath, bool esl, string? author, string? description)
    {
        var fileName = Path.GetFileName(outPath);

        SkyrimMod mod;
        try
        {
            mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE)
                { IsSmallMaster = esl };
            if (!string.IsNullOrWhiteSpace(author)) mod.ModHeader.Author = author.Trim();
            if (!string.IsNullOrWhiteSpace(description)) mod.ModHeader.Description = description.Trim();
        }
        catch (Exception ex) { return CreatePluginOutcome.Fail($"could not build the plugin in memory: {ex.GetType().Name}: {ex.Message}"); }

        // Serialize through the single WriteEngine.WritePatch chokepoint with an EMPTY known-master set → zero masters,
        // plus the crash-atomic staged write + the FormID floor every product write gets. Nothing is on disk on a throw.
        try { WriteEngine.WritePatch(mod, Array.Empty<ISkyrimModGetter>(), outPath); }
        catch (Exception ex)
            { return CreatePluginOutcome.Fail($"writing the plugin failed (serialize or commit; nothing left on disk): {WriteEngine.Describe(ex)}"); }

        // Re-open + CONFIRM the artifact (Q3 — never report success on an unverified file): zero records, the master
        // header (empty), the ESL flag as written, the byte size.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int recordCount = -1; bool eslBack = false; long bytes = 0;
        string? confirmFail = null;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            recordCount = back.EnumerateMajorRecords().Count();
            eslBack = back.IsSmallMaster;
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { confirmFail = $"plugin written but could not be re-opened to confirm it: {ex.Message}"; }
        finally { (back as IDisposable)?.Dispose(); }   // dispose the overlay BEFORE any delete below (it maps the file)

        if (confirmFail is null && recordCount != 0)
            confirmFail = $"internal error: the created plugin carries {recordCount} record(s), expected 0 (a header-only plugin) — refusing to report success on a wrong artifact (Q3).";
        if (confirmFail is null && eslBack != esl)
            confirmFail = $"internal error: the created plugin's light-master (ESL) flag is {eslBack}, expected {esl} — refusing to report success on a wrong artifact (Q3).";

        if (confirmFail is not null)
        {
            // The file we just wrote is wrong or unverifiable — remove it so a refusal leaves NO bad artifact behind
            // (Q3; the service's folder cleanup then finds an empty folder and removes that too). Safe here: create_plugin
            // always writes a FRESH file in a fresh folder (no extend), so there is never a prior file to lose.
            try { File.Delete(outPath); } catch { /* best-effort; the loud refusal stands regardless */ }
            return CreatePluginOutcome.Fail(confirmFail);
        }

        return new CreatePluginOutcome(true, null, outPath, fileName, esl, masters, recordCount, bytes);
    }

    /// <summary>
    /// Create BRAND-NEW records (new FormIDs) in a patch — the net-new authoring capability, the sibling of
    /// <see cref="Apply"/> (which overrides an EXISTING record). A FLAT top-level <see cref="CreateSpec"/> (no
    /// <see cref="CreateSpec.ParentRef"/>) allocates a fresh record of its (caller-declared) type via
    /// <see cref="WriteEngine.GenericUpsertNew"/>; a NESTED spec (a ParentRef — a dialogue line under a topic, a placed
    /// ref into a cell) resolves its parent (an existing load-order winner, a same-call sibling by editorid, OR a record
    /// the patch being extended already carries from a prior into= call) and allocates the child into the parent's modeled
    /// child-collection via <see cref="WriteEngine.NestedAddNew"/> — the add-target found by construction, named via
    /// <see cref="CreateSpec.IntoCollection"/> when more than one fits. Either way the new record gets a local 0x800+
    /// ESP-range FormID and the SAME <see cref="WriteEngine.ApplyVerb"/> path sets its fields; RecordType is DECLARED
    /// (no existing winner to derive it from). Still failed loud (Q3): an abstract-group subtype, and a coordinate-keyed
    /// EXTERIOR cell (FormKey-less worldspace block parents) — via <see cref="WriteEngine.CanCreateType"/> /
    /// <see cref="WriteEngine.CanCreateNested"/>. <paramref name="extend"/>=false writes a fresh patch (ModKey = filename);
    /// =true adds to an existing one (the into= path). ALL-OR-NOTHING (Q3): any pre-flight problem — missing editorid, an
    /// un-createable type, an unresolvable parent, a rejected edit — refuses the WHOLE call with no file written.
    /// </summary>
    public static CreateOutcome CreateRecords(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string outPath, bool extend, bool fullReadback = false)
    {
        if (specs.Count == 0) return CreateOutcome.Fail("no records to create supplied.");

        // Per-call overlay session (Option B): the known-master set for the serialize is opened through it and disposed
        // when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();

        // --- Phase 0: open (extend) or create the patch mod FIRST — moved AHEAD of pre-flight so a FormKey parent can
        //     resolve from the PATCH being extended (a parent created in a PRIOR into= call), not the load order alone
        //     (the former N9 gap). CreateFromBinary reads the file fully into memory and holds NO handle at rest (the
        //     active-patch self-lock invariant is untouched — Phase 4's ReleaseOverlay + AllMastersExcept still guard the
        //     serialize); nothing is mutated until Phase 3 and nothing serialized until Phase 4, so all-or-nothing holds. ---
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return CreateOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return CreateOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // --- Phase 1: pre-flight EVERY spec before any mutation (Q3, all-or-nothing). editorid required + unique; the
        //     type must be createable — a FLAT top-level type (CanCreateType), OR a NESTED child given a valid parent
        //     (CanCreateNested): the parent is resolved to its TYPE (an existing parent FormKey's load-order winner, a
        //     record created EARLIER in this same call — the one-shot order rule — or a record the PATCH being extended
        //     already carries, from a prior into= call), and the child must nest under it by construction (§1.4 Q2). Every
        //     edit is validated by the rulebook rooted at the create type. The new FormID isn't known until allocation
        //     (Phase 3), so creatability is STRUCTURAL; a FormKey parent is resolved here only to learn its TYPE + stash
        //     the route to make it settable in Phase 3. ONE captured build answers every parent resolve (hunt-F5). ---
        var view = resolver.Capture();
        var problems = new List<string>();
        var seenEdid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredEdidType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // editorid -> RecordType (same-call sibling parents)
        // editorids declared in EARLIER specs (NOT the current one) — the legal targets of a "@editorid" same-call
        // field forward-ref (HCBR Layer B unit A). Grown at the END of each spec's iteration, so during spec i's edit
        // validation it holds {0..i-1}: a self-ref (@its-own-editorid) and a forward-ref (a later sibling) both reject.
        var priorEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentPlans = new List<(IMajorRecordGetter? body, string? winnerPlugin, string? sibling, IMajorRecord? patchParent)?>(specs.Count);
        var cellKinds = new CellCreate[specs.Count];   // coordinate-keyed §4-(b) routing per spec (None / Exterior / Interior)
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            parentPlans.Add(null);   // flat default; overwritten on the nested path
            if (string.IsNullOrWhiteSpace(s.EditorId)) { problems.Add($"{s.RecordType}: an editorid is required to create a record (it's how the record is referenced)."); continue; }
            if (!seenEdid.Add(s.EditorId)) { problems.Add($"editorid '{s.EditorId}' is used by more than one record in this call — each created record needs a distinct editorid."); continue; }
            declaredEdidType[s.EditorId] = s.RecordType;

            if (s.ParentRef is null)
            {
                if (IsCellType(s.RecordType))
                {
                    // A parentless Cell (coordinate-keyed §4-(b)): NO grid ⇒ an INTERIOR cell (self-files by FormID —
                    // CanCreateType would refuse a bare Cell, so bypass it). A grid here is malformed (exterior needs a Worldspace).
                    if (s.Grid is not null) { problems.Add($"Cell '{s.EditorId}': an exterior cell (grid=) needs parent= a Worldspace; an interior cell takes no parent and no grid."); continue; }
                    cellKinds[i] = CellCreate.Interior;
                }
                else if (!WriteEngine.CanCreateType(s.RecordType, out var why)) { problems.Add($"{s.RecordType} '{s.EditorId}': {why}"); continue; }
            }
            else
            {
                Type? parentType = null;
                if (FormKey.TryFactory(s.ParentRef, out var parentFk))
                {
                    var w = view.ResolveWinner(parentFk);
                    if (w is not null)
                    {
                        // An EXISTING load-order parent (a topic/cell from a master or mod): override it INTO the patch in Phase 3.
                        var parentBody = view.GetRecord(session, w.Value.WinnerPlugin, parentFk);
                        if (parentBody is null) { problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} winner '{w.Value.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(parentBody.GetType().Name));
                        parentPlans[i] = (parentBody, w.Value.WinnerPlugin, null, null);
                    }
                    else if (patchMod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == parentFk) is { } patchParent)
                    {
                        // A parent created in a PRIOR into= call — it lives in the patch being extended, not the load order
                        // (the former N9 gap, now resolvable because Phase 0 opens the patch BEFORE this loop). It's already
                        // a settable patch-local record, so Phase 3 uses it directly (no override needed).
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(patchParent.GetType().Name));
                        parentPlans[i] = (null, null, null, patchParent);
                    }
                    else
                    {
                        // Genuinely absent from BOTH the load order and the patch — the surviving loud refusal (Q3, never a
                        // misleading "wrong FormID"). Name the one-call workaround for the common new-topic case.
                        problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} is not present in the load order"
                            + (extend ? " or this patch" : "") + " — name an existing parent, or create the parent and this "
                            + "child in ONE call (a same-call sibling parent, by the parent's editorid).");
                        continue;
                    }
                }
                else   // a same-call sibling parent — must be DECLARED EARLIER in this call (topic before its lines)
                {
                    if (s.ParentRef.Equals(s.EditorId, StringComparison.OrdinalIgnoreCase) || !declaredEdidType.TryGetValue(s.ParentRef, out var parentCatalog))
                    { problems.Add($"{s.RecordType} '{s.EditorId}': parent '{s.ParentRef}' is neither an existing FormID nor a record created EARLIER in this call — create the parent (e.g. the topic) before its children, in spec order."); continue; }
                    parentType = WriteEngine.ResolveConcreteRecordType(parentCatalog);
                    parentPlans[i] = (null, null, s.ParentRef, null);
                }
                if (parentType is null) { problems.Add($"{s.RecordType} '{s.EditorId}': could not resolve the parent's record type."); continue; }
                if (IsCellType(s.RecordType))
                {
                    // A Cell WITH a parent (coordinate-keyed §4-(b)): a grid ⇒ an EXTERIOR cell placed into the
                    // Worldspace's block tree (NOT a child-collection nest). No grid ⇒ ambiguous — refuse loud (Q3).
                    // (Parent resolution above already populated parentPlans[i] so Phase 3 can make the Worldspace settable.)
                    if (s.Grid is null) { problems.Add($"Cell '{s.EditorId}': a Cell with parent= but no grid= is ambiguous — an exterior cell needs grid=<X,Y> under a Worldspace; an interior cell takes no parent."); continue; }
                    if (parentType != typeof(Worldspace)) { problems.Add($"Cell '{s.EditorId}': an exterior cell nests under a Worldspace, but parent '{s.ParentRef}' resolved to a {parentType.Name}."); continue; }
                    if (!TryParseGrid(s.Grid, out _, out _)) { problems.Add($"Cell '{s.EditorId}': grid '{s.Grid}' must be two integers \"X,Y\" (e.g. \"5,-12\")."); continue; }
                    cellKinds[i] = CellCreate.Exterior;
                }
                else if (!WriteEngine.CanCreateNested(s.RecordType, parentType, s.IntoCollection, out var nestedWhy)) { problems.Add($"{s.RecordType} '{s.EditorId}': {nestedWhy}"); continue; }
            }

            foreach (var req in s.Edits)
                // siblingEditorIds = priorEditorIds: a "@editorid" FormLink value is accepted iff that editorid was
                // created in an EARLIER spec of THIS call (resolved to its real FormKey in Phase 3); else rejected loud.
                if (rulebook.Validate(req, priorEditorIds) is { } reject) problems.Add($"{s.RecordType} '{s.EditorId}' [{Label(req)}]: {reject}");

            priorEditorIds.Add(s.EditorId);   // now available as a same-call sibling target for LATER specs (not its own)
        }
        if (problems.Count > 0)
            return CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) creating {specs.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));

        // --- Phase 3: UPSERT each record, then apply its edits. A throw here AFTER pre-flight passed is a real engine
        //     inconsistency — fail the WHOLE call (the in-memory patch is discarded; nothing serialized), surfaced not
        //     swallowed (Q3). All upserts are in-memory until the single WritePatch, so all-or-nothing holds even mid-loop.
        //     UPSERT (idempotency): on the into=/extend path, a re-run of the same create used to APPEND a duplicate of
        //     every record (nothing checked whether the EditorID already existed in the opened patch). GenericUpsertNew
        //     replaces a same-EditorID record THE PATCH ITSELF DEFINES fresh at its same FormKey instead — re-runs are
        //     idempotent, list fields can't accumulate, and stable FormKeys keep cross-record links + external references
        //     valid. Collisions it will NOT absorb (carried overrides, duplicate residue, cross-type) refuse loud there;
        //     every replace that DOES happen is carried on CreatedRecord.ReplacedExisting and rendered to the user. ---
        //     NESTED create (a spec with a ParentRef): the parent is made settable IN the patch first — an existing
        //     load-order parent is overridden in (a flat parent needs no link cache; a nested parent — a Cell — gets the
        //     winner overlay's cache, the SAME session.LinkCacheFor path Apply uses + guards), a same-call sibling parent
        //     is the record created earlier in this loop, and a parent the patch ALREADY carries (created in a prior
        //     into= call — Phase 0) is used directly — then WriteEngine.NestedAddNew allocates the child into the
        //     parent's modeled collection (named, or the unique one). Idempotency: nested create APPENDS (no
        //     upsert-replace) — Aaron-accepted for Layer A (2026-06-14): nested children carry no stable EditorID
        //     handle to de-dup on (unlike flat GenericUpsertNew), so a re-run into= re-adds. Watch in real use;
        //     revisit only if it bites. ---
        var created = new List<CreatedRecord>(specs.Count);
        var createdByEditorId = new Dictionary<string, IMajorRecord>(StringComparer.OrdinalIgnoreCase);
        var linkCacheByPlugin = new Dictionary<string, Mutagen.Bethesda.Plugins.Cache.ILinkCache>(StringComparer.OrdinalIgnoreCase);

        // Resolve a spec's parent to a SETTABLE record IN the patch — shared by nested-create AND exterior-cell create
        // (both make the parent settable identically: a prior-into= patch record used directly; an existing load-order
        // parent overridden in, with its source link cache only when the override needs one; or a same-call sibling
        // created earlier in this loop). Returns (parent, null) on success, (null, error) to fail the WHOLE call (Q3).
        (IMajorRecord? parent, string? error) MakeSettableParent(int idx)
        {
            var plan = parentPlans[idx]!.Value;
            if (plan.patchParent is not null) return (plan.patchParent, null);   // a prior-into= patch-local record
            if (plan.body is not null)
            {
                Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache = null;
                if (WriteEngine.RecordNeedsSourceCache(plan.body))
                {
                    if (!linkCacheByPlugin.TryGetValue(plan.winnerPlugin!, out cache))
                        linkCacheByPlugin[plan.winnerPlugin!] = (cache = session.LinkCacheFor(plan.winnerPlugin!))!;
                }
                return (WriteEngine.GenericGetOrAddAsOverride(patchMod, plan.body, cache), null);
            }
            if (!createdByEditorId.TryGetValue(plan.sibling!, out var sib))
                return (null, $"internal: same-call parent '{plan.sibling}' for '{specs[idx].EditorId}' was not created before it — surfaced, not swallowed (Q3).");
            return (sib, null);
        }

        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            IMajorRecord rec; bool replaced = false;
            try
            {
                if (cellKinds[i] == CellCreate.Interior)
                {
                    // INTERIOR cell (coordinate-keyed §4-(b)): self-files into the patch's Cells group by FormID digits.
                    rec = WriteEngine.AddInteriorCell(patchMod, s.EditorId);
                }
                else if (cellKinds[i] == CellCreate.Exterior)
                {
                    // EXTERIOR cell (coordinate-keyed §4-(b)): make the Worldspace settable (thin override), then place
                    // the cell into its block tree by grid. Pre-flight (Phase 1) guaranteed a Worldspace parent + a grid that parses.
                    var (wsParent, perr) = MakeSettableParent(i);
                    if (perr is not null) return CreateOutcome.Fail(perr);
                    TryParseGrid(s.Grid!, out var gx, out var gy);
                    rec = WriteEngine.AddExteriorCell(patchMod, (Worldspace)wsParent!, gx, gy, s.EditorId);
                }
                else if (s.ParentRef is null)
                {
                    (rec, replaced) = WriteEngine.GenericUpsertNew(patchMod, s.RecordType, s.EditorId);
                }
                else
                {
                    var (settableParent, perr) = MakeSettableParent(i);
                    if (perr is not null) return CreateOutcome.Fail(perr);
                    rec = WriteEngine.NestedAddNew(patchMod, settableParent!, s.RecordType, s.IntoCollection, s.EditorId);
                }
            }
            catch (Exception ex) { return CreateOutcome.Fail($"could not create {s.RecordType} '{s.EditorId}': {ex.Message}"); }

            createdByEditorId[s.EditorId] = rec;
            var ops = new List<OpResult>(s.Edits.Count);
            foreach (var rawReq in s.Edits)
            {
                // Resolve a same-call sibling reference (@editorid) in a FormLink value to the sibling's now-allocated
                // FormKey (HCBR Layer B unit A — the INFO PNAM chain + Topic back-link in one bulk_create). Pre-flight
                // (Phase 1) already guaranteed any surviving @-token is on a FormLink leaf AND names a record created
                // EARLIER in this call — so it is already in createdByEditorId; a miss here is a real engine
                // inconsistency, surfaced not swallowed (Q3). The substituted value is a normal intra-patch FormKey
                // that ApplyVerb coerces exactly as a literal FormID would (no apply-path change).
                var req = rawReq;
                if (WriteEngine.IsSameCallSiblingRef(rawReq.Value, out var sibEdid))
                {
                    if (!createdByEditorId.TryGetValue(sibEdid, out var sibRec))
                        return CreateOutcome.Fail(
                            $"internal: same-call reference '@{sibEdid}' on new {s.RecordType} '{s.EditorId}' resolved to no " +
                            "record created in this call — pre-flight should have caught it; surfaced, not swallowed (Q3).");
                    req = CopyWithValue(rawReq, sibRec.FormKey.ToString());
                }
                try { WriteEngine.ApplyVerb(rec, req); ops.Add(new OpResult(rec.FormKey, s.RecordType, Label(req), true, null, TryReadAfter(rec, req))); }
                catch (ExpectedApplyRejectionException ex)
                {
                    // EXPECTED apply-time refusal (live state pre-flight can't see — e.g. a duplicate dict key): clean
                    // guidance, NOT the inconsistency wrapper. Whole call still refused, nothing serialized (gap-audit Finding 3).
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}) — {ex.Message} (nothing created)");
                }
                catch (MalformedTargetDataException ex)
                {
                    // THIRD category: the target record's own data is malformed (present-but-null element/entry) — render it
                    // accurately, NOT under the inconsistency wrapper. Whole call refused, nothing serialized (PR #83 Gap 2).
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}) — {ex.Message} (nothing created)");
                }
                catch (Exception ex)
                {
                    return CreateOutcome.Fail(
                        $"engine error applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}): " +
                        $"pre-flight ACCEPTED it but the apply threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
                }
            }
            created.Add(new CreatedRecord(rec.FormKey, s.RecordType, s.EditorId, ops, replaced));
        }

        // --- Phase 4: serialize ONCE with the full known-master set. A created record referencing existing content pulls
        //     its master into the (lean, derived) header; a self-contained one yields a masterless plugin. A referenced
        //     master genuinely absent still fails loud (Q3). ---
        // Two-part active-patch self-lock guard (Heisen 2026-06-08 + PR #24 review): no mapped handle on the file we're
        // about to write may survive to the serialize, from ANY source. ReleaseOverlay closes one we already hold (Apply's
        // Phase-1 winner fetch, when re-editing the patch's OWN override — there the winner IS the target); AllMastersExcept
        // keeps the target out of the master set. (writelock-probe / writelock-apply-probe; both halves guarded.)
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex) { return CreateOutcome.Fail($"writing the patch after create failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // --- Phase 5: re-open + report the (derived) master header + bytes — and, on request, each created record's
        //     FULL read-back off that same re-opened file (see Apply's Phase 5). Dispose the overlay so the file isn't
        //     left mmap'd (a later into= re-opens it). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, created.Select(c => c.FormKey));
        }
        catch (Exception ex) { return CreateOutcome.Fail($"records created + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new CreateOutcome(true, null, outPath, extend, created, masters, bytes) { ReadBack = readBack };
    }

    /// <summary>Read each just-written record IN FULL off the re-opened written file — the overlay Phase 5 already
    /// opens to confirm masters, so no new handle class (opened AFTER the serialize, disposed with Phase 5; the
    /// active-patch self-lock invariant is untouched). ONE enumeration pass serves every target (flat + nested
    /// groups — the same walk <see cref="RemoveRecords"/>' present-check relies on); tokens are materialised while
    /// the overlay is open. NEVER throws (PR #40 review #1): the write itself already SUCCEEDED by the time this
    /// runs (serialize done, masters confirmed), so a read-back failure must not convert the outcome to Fail —
    /// that would read as "my write was lost" and invite re-issuing the ops (the duplicate-Add trap this read-back
    /// exists to close). Every degraded path is named per-record on <see cref="FullReadback.Error"/> (Q3).</summary>
    static IReadOnlyList<FullReadback> ReadBackInFull(ISkyrimModGetter back, IEnumerable<FormKey> targets)
    {
        var order = new List<FormKey>();                                   // caller order, de-duped (several ops may hit one record)
        var want = new HashSet<FormKey>();
        foreach (var fk in targets) if (want.Add(fk)) order.Add(fk);

        var found = new Dictionary<FormKey, RecordFields>();
        string? walkError = null;
        try
        {
            foreach (var rec in back.EnumerateMajorRecords())
                if (want.Contains(rec.FormKey) && !found.ContainsKey(rec.FormKey))
                    found[rec.FormKey] = ReadEngine.ReadFields(rec, null, FullReadbackDepth);
        }
        catch (Exception ex) { walkError = $"the full read-back walk failed: {ex.GetType().Name}: {ex.Message}"; }

        var result = new List<FullReadback>(order.Count);
        foreach (var fk in order)
            result.Add(found.TryGetValue(fk, out var rf)
                ? new FullReadback(fk, rf, null)
                : new FullReadback(fk, null, walkError is not null
                    ? $"{walkError} — the WRITE ITSELF SUCCEEDED (the patch was serialized and re-opened); inspect the patch in xEdit; do not re-issue the ops."
                    : $"the written file did not yield {fk} on re-open — a real inconsistency, surfaced not swallowed (Q3); inspect the patch in xEdit."));
        return result;
    }

    /// <summary>The xEdit-style edit label: <c>Verb path[key] = value</c> (matches <see cref="WriteEngine.RunPatch"/>).</summary>
    static string Label(WriteRequest r) =>
        $"{r.Verb} {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")}{(r.Value is not null ? " = " + r.Value : "")}";

    /// <summary>Clone a <see cref="WriteRequest"/> changing ONLY its <see cref="WriteRequest.Value"/> — it's an
    /// init-only class (no <c>with</c>). Used to substitute a resolved same-call sibling FormKey for the
    /// <c>@editorid</c> token before apply (HCBR Layer B unit A); every other field is carried verbatim.</summary>
    static WriteRequest CopyWithValue(WriteRequest r, string value) => new()
    {
        RecordType = r.RecordType, Path = r.Path, Verb = r.Verb, Key = r.Key,
        Value = value, Values = r.Values, Entries = r.Entries, Struct = r.Struct,
    };

    /// <summary>Best-effort read-back of the edited leaf off the override (so the caller sees the value landed without a
    /// follow-up read). Reads the leaf PATH (not the keyed element — that's xEdit's job); null on any difficulty — never
    /// load-bearing, never throws into the write result.</summary>
    static string? TryReadAfter(IMajorRecord ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf });
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            return f is null ? null : (f.HasValue ? f.Token : f.Note);
        }
        catch { return null; }
    }
}

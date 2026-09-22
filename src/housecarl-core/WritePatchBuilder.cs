using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The one <c>(edits) → (patch)</c> surface the write tools go through; contracts in docs/architecture/write-path.md.</summary>
public static class WritePatchBuilder
{
    /// <summary>One edit: locate a record by its FormKey, apply <see cref="Verb"/> at <see cref="Path"/>; RecordType is derived, never declared.</summary>
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
        public IReadOnlyList<StructSpec>? Structs { get; init; } // batch struct-list ops (composes=): Add appends each, ReplaceAll clears+appends each.
        public string? FromPlugin { get; init; } // verb=CopyFrom: the plugin whose version of the SOURCE record to deep-copy the field FROM (active, or off-order on disk).

        /// <summary>The SOURCE RECORD a CopyFrom reads when it differs from <see cref="Target"/>; null ⇒ a same-FormKey copy.</summary>
        public FormKey? FromTarget { get; init; }

        /// <summary>The record a CopyFrom actually READS — the named source record, else the target itself.</summary>
        public FormKey CopySource => FromTarget ?? Target;
    }

    /// <summary>Per-edit result; which readings come from memory and which from the written file is in docs/architecture/write-path.md.</summary>
    public sealed record OpResult(FormKey Target, string RecordType, string Label, bool Applied, string? Error, string? After, string? Landed = null)
    {
        /// <summary><see cref="Landed"/> re-derived off the WRITTEN file; null when the file could not answer for this op.</summary>
        public string? LandedOnDisk { get; init; }

        /// <summary><see cref="After"/> re-derived off the WRITTEN file; on a SUPERSEDED op it is the leaf's FINAL state.</summary>
        public string? AfterOnDisk { get; init; }

        /// <summary>Byte LENGTH when <see cref="AfterOnDisk"/> is an opaque blob whose structure was never looked at.</summary>
        public int? AfterOnDiskBytes { get; init; }

        /// <summary>The completed walk of the written file did not contain this op's target record; false when the walk failed.</summary>
        public bool RecordAbsentFromFile { get; init; }

        /// <summary><see cref="After"/> is a SENTENCE about what the write did, not a field reading, and prints as it stands.</summary>
        public bool AfterIsNote { get; init; }

        /// <summary>A LATER op in the same call wrote into this op's leaf, so the written file cannot answer for this one.</summary>
        public bool SupersededInCall { get; init; }

        /// <summary>The file-verify examined this op; false means it was never asked.</summary>
        public bool VerifyAttempted { get; init; }

        /// <summary>What the write DID that the written file cannot say afterwards — the list Add's membership answer.</summary>
        public string? ApplyNote { get; init; }
    }

    /// <summary>One record read back IN FULL off the re-opened written file; <see cref="Error"/> names one it failed to yield.</summary>
    public sealed record FullReadback(FormKey Target, RecordFields? Record, string? Error);

    /// <summary>The call outcome; <see cref="Error"/> non-null ⇒ the whole call was refused with a named reason and no patch written.</summary>
    public sealed record PatchOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<string> Masters, IReadOnlyList<OpResult> Ops, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>The fingerprint of the index build THIS OUTCOME was decided from; contract in docs/architecture/write-path.md.</summary>
        public OrderStamp? Stamp { get; init; }

        /// <summary>That build's fingerprint, read through the stamp.</summary>
        public string? Epoch => Stamp?.Epoch;

        /// <summary>True ⇒ the edits landed in the USER's own file at <see cref="OutputPath"/>, not a new patch.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ the first-touch in-place CONSENT handshake; <see cref="Error"/> is the prompt and nothing was written.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly.</summary>
        public string? Note { get; init; }

        /// <summary>The fork warning: a record this write overrides is already overridden by a plugin that can out-load the patch.</summary>
        public string? Warning { get; init; }

        /// <summary>True ⇒ a DRY RUN: the real pipeline ran and stopped at the serialize; contract in docs/architecture/write-path.md.</summary>
        public bool DryRun { get; init; }

        public static PatchOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error, carrying <paramref name="prompt"/>.</summary>
        public static PatchOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>One record dropped by <see cref="RemoveRecords"/> — its FormKey, catalog type and editorid.</summary>
    public sealed record RemovedRecord(FormKey Target, string RecordType, string? EditorId);

    /// <summary>The outcome of a <see cref="RemoveRecords"/> call; <see cref="RemainingRecords"/>=0 means the patch is now inert.</summary>
    public sealed record RemovalOutcome(
        bool Success, string? Error, string OutputPath,
        IReadOnlyList<RemovedRecord> Removed, IReadOnlyList<string> Masters, int RemainingRecords, long Bytes)
    {
        /// <summary>The build this outcome was decided from, on <see cref="PatchOutcome.Stamp"/>'s contract.</summary>
        public OrderStamp? Stamp { get; init; }

        /// <summary>That build's fingerprint, read through the stamp.</summary>
        public string? Epoch => Stamp?.Epoch;

        /// <summary>True ⇒ the records were dropped from the USER's own file at <see cref="OutputPath"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ the first-touch in-place CONSENT handshake; <see cref="Error"/> is the prompt and nothing was written.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly.</summary>
        public string? Note { get; init; }

        public static RemovalOutcome Fail(string error) =>
            new(false, error, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error, carrying <paramref name="prompt"/>.</summary>
        public static RemovalOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0) { NeedsAcknowledge = true };
    }

    /// <summary>One brand-new record to create: its DECLARED <see cref="RecordType"/>, required <see cref="EditorId"/>, and field <see cref="Edits"/>.</summary>
    public sealed record CreateSpec
    {
        public required string RecordType { get; init; }
        public required string EditorId { get; init; }
        public required IReadOnlyList<WriteRequest> Edits { get; init; }

        /// <summary>Optional — the PARENT this record nests UNDER, an existing FormKey or an earlier sibling's editorid; null ⇒ flat.</summary>
        public string? ParentRef { get; init; }

        /// <summary>Optional — which of the parent's child SLOTS to add into; null ⇒ the unique slot that accepts this child type.</summary>
        public string? IntoCollection { get; init; }

        /// <summary>Optional — the exterior-cell GRID as "X,Y"; a Cell with neither grid nor parent is an INTERIOR cell.</summary>
        public string? Grid { get; init; }
    }

    /// <summary>One record created by <see cref="CreateRecords"/>; <see cref="ReplacedExisting"/> ⇒ it replaced a record the patch already defined.</summary>
    public sealed record CreatedRecord(FormKey FormKey, string RecordType, string EditorId, IReadOnlyList<OpResult> Ops,
        bool ReplacedExisting = false)
    {
        /// <summary>For a NESTED create: which record hosts this child and whose version was copied in; contract in docs/architecture/write-path.md.</summary>
        public string? ParentHost { get; init; }

        /// <summary>The parent is CONTESTED: renders that hoist a warning select on THIS, never on <see cref="ParentHost"/>'s prose.</summary>
        public bool ParentContested { get; init; }

        /// <summary>The parent this nested create hosted the child in, so the file check can answer for it too; null for a flat create.</summary>
        public FormKey? ParentKey { get; init; }

        /// <summary>The completed walk of the written file did not contain this record; false when the walk failed.</summary>
        public bool AbsentFromFile { get; init; }

        /// <summary>The same verdict for <see cref="ParentKey"/>: the completed walk did not find the parent.</summary>
        public bool ParentAbsentFromFile { get; init; }

        /// <summary>The file check examined this record; false ⇒ no verify ran and the render says not checked.</summary>
        public bool VerifyAttempted { get; init; }
    }

    /// <summary>The outcome of a <see cref="CreateRecords"/> call; <see cref="Created"/> lists every new record with its allocated FormKey.</summary>
    public sealed record CreateOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<CreatedRecord> Created, IReadOnlyList<string> Masters, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>The build this outcome was decided from, on <see cref="PatchOutcome.Stamp"/>'s contract.</summary>
        public OrderStamp? Stamp { get; init; }

        /// <summary>That build's fingerprint, read through the stamp.</summary>
        public string? Epoch => Stamp?.Epoch;

        /// <summary>True ⇒ the new records were allocated into the USER's own file at <see cref="OutputPath"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ the first-touch in-place CONSENT handshake; <see cref="Error"/> is the prompt and nothing was written.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly.</summary>
        public string? Note { get; init; }

        /// <summary>The fork warning, on <see cref="PatchOutcome.Warning"/>'s contract — here about the PARENT a nested create overrode in.</summary>
        public string? Warning { get; init; }

        /// <summary>The voice-coverage report for the INFOs this call created, filled by the SERVICE post-write.</summary>
        public VoiceReport? Voice { get; init; }

        /// <summary>The result-script binding report for the INFOs this call created, filled by the SERVICE post-write.</summary>
        public ScriptBindingReport? ScriptBinding { get; init; }

        /// <summary>The structural-shell report for the cells this call created, filled by the SERVICE post-write.</summary>
        public CellShellReport? CellShell { get; init; }

        public static CreateOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error, carrying <paramref name="prompt"/>.</summary>
        public static CreateOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>How deep the full read-back reads each written record — the same depth as the conflict diff.</summary>
    public const int FullReadbackDepth = 16;

    /// <summary>The cell-create kind for a spec: the flat path, an exterior Cell placed by grid, or a parentless interior Cell.</summary>
    enum CellCreate { None, Exterior, Interior }

    /// <summary>Is <paramref name="recordType"/> the <c>Cell</c> record type (case-insensitive — the catalog convention)?</summary>
    static bool IsCellType(string recordType) => string.Equals(recordType, nameof(Cell), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse an exterior-cell grid "X,Y" into two ints (whitespace-tolerant). False ⇒ malformed; the call refuses loud.</summary>
    static bool TryParseGrid(string? grid, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(grid)) return false;
        var parts = grid.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>Test seam, called once inside <see cref="Apply"/>'s Phase-1 resolve loop; null in the product, and
    /// parked on only by freshness-capture-guard arm 4 — see docs/architecture/write-path.md.</summary>
    internal static Action? InsidePhase1ResolveForGuard;

    /// <summary>Build or extend a patch from <paramref name="edits"/> to <paramref name="outPath"/>; <paramref name="dryRun"/> stops at the serialize.</summary>
    public static PatchOutcome Apply(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string outPath, bool extend, bool fullReadback = false,
        IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources = null, bool dryRun = false)
    {
        OrderStamp? epoch = null;
        var outcome = ApplyCore(resolver, rulebook, edits, outPath, extend, fullReadback, copyFromSources, dryRun, ref epoch);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>The body of <see cref="Apply"/>, split so the ONE captured build's fingerprint stamps EVERY outcome from one place.</summary>
    static PatchOutcome ApplyCore(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string outPath, bool extend, bool fullReadback,
        IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources, bool dryRun, ref OrderStamp? epoch)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Per-call overlay session: every source plugin this write reads is opened THROUGH it and disposed on return.
        using var session = resolver.OpenSession();

        // --- Phase 0: open (extend) or create the patch mod BEFORE resolving targets, so a record the PATCH ITSELF defines resolves. ---
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return PatchOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
            catch (Exception ex) { return PatchOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // An EXTEND owes the same master-grow re-sort note the in-place and create lanes emit; a fresh patch has no before-state.
        var mastersBefore = extend
            ? patchMod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // --- Phase 1: resolve, derive RecordType and pre-flight EVERY edit against ONE captured view; contracts in docs/architecture/write-path.md. ---
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down
        // The FormLink values this call sets are harvested by the RULEBOOK's own walk, which needs the type the resolve derives.
        var linkTokens = new LinkHarvestSink();
        var harvestRulebook = rulebook.WithLinkHarvest(linkTokens);
        var staged = new List<(int order, PatchEdit edit, IMajorRecordGetter? body, string? winnerPlugin, IMajorRecord? patchLocal, WriteRequest req, string label, IMajorRecordGetter? srcBody, string? harvestVerdict, bool carriesLinks)>(edits.Count);
        // Ordered, so a report mixing a resolve problem with a pre-flight one still reads in the caller's edit order.
        var problems = new List<(int Order, string Message)>();
        // Records the extended patch DEFINES, built lazily ONCE on the first load-order miss; never an override it merely carries.
        Dictionary<FormKey, IMajorRecord>? patchDefined = null;
        // Every body this call reads, gathered a PLUGIN at a time (#723), declared from the same resolution the loop below runs.
        var gather = new BodyGather(view, session);
        foreach (var e in edits)
        {
            if (view.ResolveWinner(e.Target) is { } gw) gather.Want(gw.WinnerPlugin, e.Target);
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)) continue;
            if (TryOffOrderCopyBody(copyFromSources, e, view, out _)) continue;       // pre-fetched by the service
            var srcPole = ResolveCopyPole(e, view, out var poleErr);
            if (poleErr is not null || string.IsNullOrWhiteSpace(srcPole)) continue;  // the loop below reports it
            if (!string.Equals(srcPole, fileName, StringComparison.OrdinalIgnoreCase) && view.ContainsPlugin(srcPole))
                gather.Want(srcPole, e.CopySource);
        }
        gather.Gather();
        int order = -1;
        void Problem(string message) => problems.Add((order, message));
        foreach (var e in edits)
        {
            order++;
            if (order == 1) InsidePhase1ResolveForGuard?.Invoke();         // test seam; null in the product
            IMajorRecordGetter? body = null; string? winnerPlugin = null; IMajorRecord? patchLocal = null;
            var w = view.ResolveWinner(e.Target);
            if (w is not null)
            {
                body = gather.Body(w.Value.WinnerPlugin, e.Target);
                if (body is null) { Problem($"{FormIdToken.Of(e.Target)}: winner '{w.Value.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                winnerPlugin = w.Value.WinnerPlugin;
            }
            else
            {
                if (extend)
                {
                    if (patchDefined is null)
                    {
                        patchDefined = new Dictionary<FormKey, IMajorRecord>();
                        foreach (var r in patchMod.EnumerateMajorRecords())
                            if (r.FormKey.ModKey == patchMod.ModKey) patchDefined.TryAdd(r.FormKey, r);
                    }
                    if (patchDefined.TryGetValue(e.Target, out var own)) patchLocal = own;
                }
                if (patchLocal is null)
                {
                    Problem($"{FormIdToken.Of(e.Target)}: not present in the load order ({view.PluginCount} plugins)"
                        + (extend
                            ? $", and not a record '{fileName}' (the patch being extended) itself defines — a record " +
                              "the patch merely OVERRIDES resolves via the load order, so its defining plugin must be enabled."
                            : "."));
                    continue;
                }
            }

            // CopyFrom source: the SERVICE's pre-located off-order file, else resolved from the ACTIVE order via this captured view.
            IMajorRecordGetter? srcBody = null;
            if (string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))
            {
                // With a source RECORD named, from_source defaults to that record's winner off the one captured view.
                var srcPlugin = ResolveCopyPole(e, view, out var poleErr);
                if (poleErr is not null) { Problem(poleErr); continue; }

                if (TryOffOrderCopyBody(copyFromSources, e, view, out var offSrc))
                    srcBody = offSrc;
                else if (string.IsNullOrWhiteSpace(srcPlugin))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom is missing from_plugin (internal — the mapper should have caught this)."); continue; }
                else if (string.Equals(srcPlugin, fileName, StringComparison.OrdinalIgnoreCase))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom from_plugin '{srcPlugin}' is the output patch itself — name the OTHER plugin whose version to copy from."); continue; }
                else if (!view.ContainsPlugin(srcPlugin))
                // No AbsenceClause: the service pre-resolves every off-order source, so a name reaching this arm has no on-disk copy at all.
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' is not in the load order (and no plugin file by that name was located on disk) — name an active plugin, or a plugin file present on disk."); continue; }
                else if (view.ExcludedPlugins.TryGetValue(srcPlugin, out var why))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
                else
                {
                    srcBody = gather.Body(srcPlugin, e.CopySource);
                    if (srcBody is null)
                    { Problem(CopySourceMissing(e, srcPlugin)); continue; }
                }
                // The same-runtime-record-type gate: a cross-record cross-type transplant is refused BY NAME before it reaches CopyField.
                if (CrossTypeRefusal(e, srcBody, patchLocal ?? (object?)body) is { } typeErr)
                { Problem(typeErr); continue; }
            }

            var recType = RecordNaming.StripOverlay((patchLocal ?? (object)body!).GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct, Structs = e.Structs,
            };
            var label = Label(req);
            // The harvest walk IS a validate, so its verdict is kept along with whether this edit put anything in the sink.
            var sunk = linkTokens.Adds;
            var harvestVerdict = harvestRulebook.CollectLinkValues(req);
            staged.Add((order, e, body, winnerPlugin, patchLocal, req, label, srcBody, harvestVerdict, linkTokens.Adds != sunk));
        }

        // --- Phase 1b: resolve the harvested link targets ONCE, then pre-flight every edit that CONTRIBUTED a value again. ---
        var (linkTypes, linkNote) = LinkTypeLookup(view, session, linkTokens.Tokens);
        var linkRulebook = rulebook.WithLinkTargets(linkTypes);
        var resolved = new List<(PatchEdit edit, IMajorRecordGetter? body, string? winnerPlugin, IMajorRecord? patchLocal, WriteRequest req, string label, IMajorRecordGetter? srcBody)>(staged.Count);
        foreach (var s in staged)
        {
            if ((s.carriesLinks ? linkRulebook.Validate(s.req) : s.harvestVerdict) is { } reject)
            { problems.Add((s.order, $"{s.req.RecordType} {FormIdToken.Of(s.edit.Target)} [{s.label}]: {reject}")); continue; }
            resolved.Add((s.edit, s.body, s.winnerPlugin, s.patchLocal, s.req, s.label, s.srcBody));
        }
        if (problems.Count > 0)
        {
            problems.Sort((a, b) => a.Order.CompareTo(b.Order));
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; NO patch written:\n  - "
                + string.Join("\n  - ", problems.Select(p => p.Message)));
        }

        // Is another patch already overriding one of these records? Off the captured view, and a warning, never a block.
        var forkWarning = ForkWarning.For(view, resolved.Select(r => r.edit.Target), fileName);

        // --- Phase 3: override each winner into the ONE patch mod, then apply; a throw after pre-flight passed fails the WHOLE call. ---
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, winnerPlugin, patchLocal, req, label, srcBody) in resolved)
        {
            try
            {
                // A patch-local target is already a settable record IN patchMod: edit it directly, no override, no source cache.
                IMajorRecord ov;
                if (patchLocal is not null) ov = patchLocal;
                else
                {
                    ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body!) ? session.LinkCacheFor(winnerPlugin!) : null;
                    ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, body!, cache);
                }
                // CopyFrom transplants the field FROM the source body into ov; every other verb applies to ov directly.
                string? applyNote = null;
                if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
                    WriteEngine.CopyField(srcBody!, ov, req.Path);
                else
                    applyNote = WriteEngine.ApplyVerb(ov, req);
                var (after, landed, _, _) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed) { ApplyNote = applyNote });
            }
            catch (ExpectedApplyRejectionException ex)
            {
                // An EXPECTED apply-time refusal pre-flight cannot pre-empt: its own guidance, not the gate/apply-inconsistency wrapper.
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)} — {ex.Message} (no patch written)");
            }
            catch (MalformedTargetDataException ex)
            {
                // The TARGET record's own data is malformed — rendered as that, never as an engine inconsistency.
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)} — {ex.Message} (no patch written)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Sync the SNAM marker when this call set Subtype without SubtypeName; refuses loud on an unmodeled Subtype.
        if (SyncEditedTopicMarkers(patchMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} (no patch written).");

        // --- DRY RUN: stop AT the point of no return, with the one Phase-4 hazard the halt skips re-checked by the same test. ---
        if (dryRun)
        {
            if (DryRunMastersPreview(patchMod, resolver, patchLane: true, out var wouldMasters) is { } dryErr)
                return PatchOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(patchMod, resolved.Select(r => r.edit.Target), inMemory: true) : null;
            return new PatchOutcome(true, null, outPath, extend, wouldMasters, ops, 0)
            {
                DryRun = true, ReadBack = dryBack, Warning = forkWarning,
                Note = JoinNotes(linkNote, mastersBefore is null ? null : MasterGrowWouldNote(fileName, mastersBefore, wouldMasters)),
            };
        }

        // --- Phase 4: serialize ONCE with the FULL known-master set, behind the two-part active-patch self-lock. ---
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex)
            { return PatchOutcome.Fail(SerializeFailure("writing the patch failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // --- Phase 5: re-open the patch ONCE, report its masters and each op's re-read leaf, then dispose the overlay. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        IReadOnlyList<OpResult> reported = ops;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            // The strings-aware factory: the verify COMPARES what it reads, and a localized plugin opened bare reads every string empty.
            back = LoadOrderResolver.OpenOverlay(outPath, resolver.DataDir);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
            // The per-op file reading the per-edit line prints; its own try, so a compare fault leaves the ops unverified.
            try { reported = VerifyLandedAgainstFile(back, resolved.Select(r => (r.edit.Target, (WriteRequest?)r.req)).ToList(), ops); }
            catch
            {
                int asked = resolved.Count;
                reported = ops.Select((o, k) => k < asked ? o with { VerifyAttempted = true } : o).ToList();
            }
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, outPath, extend, masters, reported, bytes)
            { ReadBack = readBack, Warning = forkWarning,
              Note = JoinNotes(linkNote, mastersBefore is null ? null : MasterGrowNote(fileName, mastersBefore, masters)) };
    }

    /// <summary>Two honesty notes on one outcome, in one string; two nulls stay null.</summary>
    static string? JoinNotes(string? a, string? b) =>
        a is null ? b : b is null ? a : a + " " + b;

    /// <summary>The "source plugin doesn't carry the record to copy" refusal, worded so the reader can tell WHICH record is missing.</summary>
    static string CopySourceMissing(PatchEdit e, string srcPlugin) =>
        e.FromTarget is null
            ? $"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' is in the load order but does NOT define or override this record — there is no version of it there to copy."
            : $"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' is in the load order but does NOT define or override the SOURCE record {FormIdToken.Of(e.CopySource)} — there is no version of it there to copy from.";

    /// <summary>Does this edit's CopyFrom source resolve through the service's OFF-ORDER pre-locate? Decided from THIS
    /// call's capture, which cannot yet disagree with the pre-locate's; contract in docs/architecture/write-path.md.</summary>
    static bool TryOffOrderCopyBody(
        IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources, PatchEdit e,
        LoadOrderResolver.IndexView view,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMajorRecordGetter? body)
    {
        body = null;
        return copyFromSources is not null
            && IsOffOrderCopySource(e, view)
            && copyFromSources.TryGetValue(e, out body);
    }

    /// <summary>The harvest sink: it DEDUPS tokens for the one resolve and separately COUNTS every value, which the set size cannot.</summary>
    sealed class LinkHarvestSink : ICollection<string>
    {
        readonly HashSet<string> _tokens = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>How many values the walk has offered, duplicates included.</summary>
        public int Adds { get; private set; }
        /// <summary>The distinct tokens, for the one up-front resolve.</summary>
        public IReadOnlyCollection<string> Tokens => _tokens;
        public void Add(string item) { Adds++; _tokens.Add(item); }
        public int Count => _tokens.Count;
        public bool IsReadOnly => false;
        public bool Contains(string item) => _tokens.Contains(item);
        public void CopyTo(string[] array, int arrayIndex) => _tokens.CopyTo(array, arrayIndex);
        public IEnumerator<string> GetEnumerator() => _tokens.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Clear() => throw new NotSupportedException("the harvest sink only accumulates.");
        public bool Remove(string item) => throw new NotSupportedException("the harvest sink only accumulates.");
    }

    /// <summary>The pre-flight's link-TARGET resolver, off the rulebook's own harvest tokens resolved up front per winner plugin; null where it cannot answer.</summary>
    static (CorpusRulebook.LinkTargetLookup lookup, string? note) LinkTypeLookup(
        LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session, IReadOnlyCollection<string> tokens)
    {
        var keyOf = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        var byPlugin = new Dictionary<string, HashSet<FormKey>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tokens)
        {
            if (!FormKey.TryFactory(t, out var fk) || fk == FormKey.Null) continue;
            keyOf[t] = fk;
            if (view.ResolveWinner(fk) is not { } w) continue;
            if (!byPlugin.TryGetValue(w.WinnerPlugin, out var set)) byPlugin[w.WinnerPlugin] = set = new HashSet<FormKey>();
            set.Add(fk);
        }
        var types = new Dictionary<FormKey, Type>();
        List<string>? skipped = null;
        foreach (var (plugin, keys) in byPlugin)
        {
            var sink = new Dictionary<FormKey, IMajorRecordGetter>();
            // The exception carries the right sentence for its own fault, so the note quotes it rather than guessing.
            try { view.CollectRecords(session, plugin, keys, null, sink); }
            catch (PluginUnreadableException ex)
            {
                (skipped ??= new List<string>()).Add(
                    // keys is the DISTINCT FormKeys, so the note counts records, not the write's link value slots.
                    $"this write links to {keys.Count} distinct record(s) in '{plugin}', whose types were NOT " +
                    $"checked: {ex.Message}");
                continue;
            }
            foreach (var kv in sink) types[kv.Key] = kv.Value.GetType();
        }
        return (token => keyOf.TryGetValue(token, out var fk) && types.TryGetValue(fk, out var t) ? t : null,
                skipped is null ? null : string.Join(" ", skipped));
    }

    /// <summary>Does this edit's CopyFrom source need the OFF-ORDER on-disk locate? The ONE rule both ends share.</summary>
    public static bool IsOffOrderCopySource(PatchEdit e, LoadOrderResolver.IndexView view)
        => string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)
           && !string.IsNullOrWhiteSpace(e.FromPlugin)
           && !view.ContainsPlugin(e.FromPlugin!);

    /// <summary>Resolve the PLUGIN a CopyFrom reads from: named <c>from_source</c>, else the source record's winner off the one captured build.</summary>
    static string? ResolveCopyPole(PatchEdit e, LoadOrderResolver.IndexView view, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(e.FromPlugin)) return e.FromPlugin;
        if (e.FromTarget is null) return null;                     // same-record copy with no pole — the caller's mapper already refused it
        var w = view.ResolveWinner(e.CopySource);
        if (w is null)
        {
            error = $"{FormIdToken.Of(e.Target)}: the source record {FormIdToken.Of(e.CopySource)} is not present in the load order ({view.PluginCount} plugins), " +
                    "so there is no winning version of it to copy from. Enable the plugin that defines it, or name a specific " +
                    "plugin in from_source.";
            return null;
        }
        return w.Value.WinnerPlugin;
    }

    /// <summary>The same-runtime-record-type gate for a CROSS-record copy, compared on the OVERLAY-STRIPPED type name.</summary>
    static string? CrossTypeRefusal(PatchEdit e, IMajorRecordGetter srcBody, object? targetBody)
    {
        if (e.FromTarget is null || targetBody is null) return null;
        var srcType = RecordNaming.StripOverlay(srcBody.GetType().Name);
        var tgtType = RecordNaming.StripOverlay(targetBody.GetType().Name);
        if (string.Equals(srcType, tgtType, StringComparison.Ordinal)) return null;
        return $"{FormIdToken.Of(e.Target)}: cannot copy from {FormIdToken.Of(e.CopySource)} — the source is a {srcType} and the target is a {tgtType}. " +
               "A field bundle copies between records of the SAME record type (a field path means different things on " +
               "different types); pair each target with a source of its own type.";
    }

    /// <summary>Sync the SNAM marker for each DialogTopic this call set <c>Subtype</c> on but not <c>SubtypeName</c>; non-null FAILS the whole call.</summary>
    static string? SyncEditedTopicMarkers(SkyrimMod mod, IReadOnlyList<PatchEdit> edits, List<OpResult> ops)
    {
        // Which top-level fields did this call edit, per target? (path[0]; Subtype/SubtypeName are scalar leaves.)
        var editedTop = new Dictionary<FormKey, HashSet<string>>();
        foreach (var e in edits)
        {
            if (e.Path.Length == 0) continue;
            if (!editedTop.TryGetValue(e.Target, out var set)) editedTop[e.Target] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(e.Path[0]);
        }
        foreach (var (fk, set) in editedTop)
        {
            if (!set.Contains("Subtype") || set.Contains("SubtypeName")) continue;   // only Subtype-set-without-marker
            if (mod.DialogTopics.FirstOrDefault(t => t.FormKey == fk) is not { } dt) continue;   // not a DialogTopic
            switch (DialogueSubtype.SyncMarkerToSubtype(dt, out var marker))
            {
                case MarkerFill.Filled:
                    ops.Add(new OpResult(fk, "DialogTopic",
                        $"SubtypeName (SNAM subtype marker) synced to {marker}", true, null,
                        $"{marker} — you set Subtype={dt.Subtype}; the game buckets by the SNAM marker, so it was synced to match (#131 — otherwise the Subtype change is a silent no-op)")
                        { AfterIsNote = true });
                    break;
                case MarkerFill.Unmodeled:
                    return $"cannot set Subtype on DialogTopic {FormIdToken.Of(fk)}: no SNAM marker is modeled for Subtype={dt.Subtype} " +
                           $"((int){(int)dt.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). Use a valid Subtype, or set SubtypeName explicitly";
            }
        }
        return null;
    }

    /// <summary>EDIT records IN PLACE inside an EXISTING plugin the user owns — <see cref="Apply"/>'s opt-in sibling; contracts in docs/architecture/write-path.md.</summary>
    public static PatchOutcome ApplyInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string targetPath, string targetName, bool fullReadback = true,
        bool dryRun = false, IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources = null)
    {
        OrderStamp? epoch = null;
        var outcome = ApplyInPlaceCore(resolver, rulebook, edits, targetPath, targetName, fullReadback, dryRun, copyFromSources, ref epoch);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>The body of <see cref="ApplyInPlace"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static PatchOutcome ApplyInPlaceCore(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string targetPath, string targetName, bool fullReadback,
        bool dryRun, IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources, ref OrderStamp? epoch)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Per-call overlay session, same as Apply: every read is opened THROUGH it and disposed on return.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: resolve each edit's body FROM THE TARGET (not the winner), derive its type, pre-flight — one view. ---
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down
        if (!view.ContainsPlugin(targetName))
            return PatchOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return PatchOutcome.Fail(
                $"cannot edit '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");

        // Two passes, exactly as the patch lane: the rulebook's walk harvests, then one lookup answers every pre-flight.
        var linkTokens = new LinkHarvestSink();
        var harvestRulebook = rulebook.WithLinkHarvest(linkTokens);
        var staged = new List<(int order, PatchEdit edit, IMajorRecordGetter body, WriteRequest req, string label, IMajorRecordGetter? srcBody, bool selfSource, string? harvestVerdict, bool carriesLinks)>(edits.Count);
        var problems = new List<(int Order, string Message)>();
        // One walk of the target per CALL instead of one per edit (#723), and the same for every in-order copy source.
        var gather = new BodyGather(view, session);
        foreach (var e in edits)
        {
            gather.Want(targetName, e.Target);
            if (!string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal)) continue;
            if (TryOffOrderCopyBody(copyFromSources, e, view, out _)) continue;       // pre-fetched by the service
            var srcPole = ResolveCopyPole(e, view, out var poleErr);
            if (poleErr is not null || string.IsNullOrWhiteSpace(srcPole)) continue;  // the loop below reports it
            if (view.ContainsPlugin(srcPole)) gather.Want(srcPole, e.CopySource);
        }
        gather.Gather();
        int order = -1;
        void Problem(string message) => problems.Add((order, message));
        foreach (var e in edits)
        {
            order++;
            bool selfSource = false;   // the copy source lives in the TARGET's own file — see the lifetime note below
            var body = gather.Body(targetName, e.Target);
            if (body is null)
            {
                Problem($"{FormIdToken.Of(e.Target)}: '{targetName}' does not define or override this record — in-place edits only what the " +
                        "file OWNS. To change a record defined in another plugin, use the default patch lane (a new override) instead.");
                continue;
            }
            var recType = RecordNaming.StripOverlay(body.GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct, Structs = e.Structs,
            };
            var label = Label(req);

            // CopyFrom SOURCE resolution, the same contract Apply enforces; the target itself is a source only for a cross-record copy.
            IMajorRecordGetter? srcBody = null;
            if (string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))
            {
                var srcPlugin = ResolveCopyPole(e, view, out var poleErr);
                if (poleErr is not null) { Problem(poleErr); continue; }

                if (TryOffOrderCopyBody(copyFromSources, e, view, out var offSrc))
                    srcBody = offSrc;
                else if (string.IsNullOrWhiteSpace(srcPlugin))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom is missing from_plugin (internal — the mapper should have caught this)."); continue; }
                else if (e.FromTarget is null && string.Equals(srcPlugin, targetName, StringComparison.OrdinalIgnoreCase))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom from_plugin '{srcPlugin}' is the in-place target itself — copying this record's own field onto itself is a no-op; name the OTHER plugin whose version to copy from."); continue; }
                else if (!view.ContainsPlugin(srcPlugin))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' is not in the load order (and no plugin file by that name was located on disk) — name an active plugin, or a plugin file present on disk."); continue; }
                else if (view.ExcludedPlugins.TryGetValue(srcPlugin, out var cfWhy))
                { Problem($"{FormIdToken.Of(e.Target)}: CopyFrom source '{srcPlugin}' was excluded from this session ({cfWhy}) — its records aren't resolvable."); continue; }
                else
                {
                    srcBody = gather.Body(srcPlugin, e.CopySource);
                    if (srcBody is null) { Problem(CopySourceMissing(e, srcPlugin)); continue; }
                    // LIFETIME: this body's overlay dies in Phase 4, so the APPLY re-resolves the source from the mutable targetMod.
                    if (string.Equals(srcPlugin, targetName, StringComparison.OrdinalIgnoreCase)) selfSource = true;
                }
                if (CrossTypeRefusal(e, srcBody, body) is { } typeErr) { Problem(typeErr); continue; }
            }
            // Last, as on the patch lane, for the MESSAGE ORDER: a bad from_plugin is reported before a bad field_path.
            var sunk = linkTokens.Adds;
            var harvestVerdict = harvestRulebook.CollectLinkValues(req);
            staged.Add((order, e, body, req, label, srcBody, selfSource, harvestVerdict, linkTokens.Adds != sunk));
        }

        // --- Phase 1b: one resolve of the harvested link targets, then pre-flight every staged edit against it. ---
        var (linkTypes, linkNote) = LinkTypeLookup(view, session, linkTokens.Tokens);
        var linkRulebook = rulebook.WithLinkTargets(linkTypes);
        var resolved = new List<(PatchEdit edit, IMajorRecordGetter body, WriteRequest req, string label, IMajorRecordGetter? srcBody, bool selfSource)>(staged.Count);
        foreach (var s in staged)
        {
            if ((s.carriesLinks ? linkRulebook.Validate(s.req) : s.harvestVerdict) is { } reject)
            { problems.Add((s.order, $"{s.req.RecordType} {FormIdToken.Of(s.edit.Target)} [{s.label}]: {reject}")); continue; }
            resolved.Add((s.edit, s.body, s.req, s.label, s.srcBody, s.selfSource));
        }
        if (problems.Count > 0)
        {
            problems.Sort((a, b) => a.Order.CompareTo(b.Order));
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; '{fileName}' is UNTOUCHED:\n  - "
                + string.Join("\n  - ", problems.Select(p => p.Message)));
        }

        // The same fork question the patch lane asks, on the POSITIONED arm; read off the captured view, no scan.
        var forkWarning = ForkWarning.For(view, resolved.Select(r => r.edit.Target), fileName);

        // --- Phase 2: open the TARGET mutably. EAGER, the SINGLE plugin only; an unparseable plugin is REFUSED here. ---
        if (!File.Exists(targetPath))
            return PatchOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(targetPath)); }
        catch (Exception ex)
            { return PatchOutcome.Fail($"cannot open '{fileName}' to edit in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // The author's DECLARED masters before any mutation, diffed against the re-opened header for the re-sort note.
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --- Phase 2b: SNAPSHOT every same-file copy source BEFORE any op mutates anything, ONE snapshot PER OP. ---
        Dictionary<PatchEdit, IMajorRecordGetter>? selfSnapshots = null;
        if (resolved.Any(r => r.selfSource))
        {
            var byKey = new Dictionary<FormKey, IMajorRecordGetter>();
            foreach (var rec in targetMod.EnumerateMajorRecords()) byKey.TryAdd(rec.FormKey, rec);
            selfSnapshots = new Dictionary<PatchEdit, IMajorRecordGetter>();
            foreach (var r in resolved)
            {
                if (!r.selfSource) continue;
                if (!byKey.TryGetValue(r.edit.CopySource, out var live))
                    return PatchOutcome.Fail(
                        $"refused: the copy source {r.edit.CopySource} resolved in '{fileName}' at pre-flight but is not " +
                        "present in the opened file (a real inconsistency, surfaced not swallowed — Q3). The file is UNTOUCHED.");
                if (WriteEngine.TryDeepCopyRecord(live) is not { } snap)
                    return PatchOutcome.Fail(
                        $"refused: cannot copy from {r.edit.CopySource} inside '{fileName}' — Mutagen models no deep copy for " +
                        $"{RecordNaming.StripOverlay(live.GetType().Name)}, and copying from the live record would alias the two " +
                        "records' data. Copy from another plugin's version instead. The file is UNTOUCHED.");
                selfSnapshots[r.edit] = snap;
            }
        }

        // --- Phase 3: apply each verb to the TARGET's OWN record, which GenericGetOrAddAsOverride returns by get-semantics. ---
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, req, label, srcBody, selfSource) in resolved)
        {
            try
            {
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(targetName) : null;
                var ov = WriteEngine.GenericGetOrAddAsOverride(targetMod, body, cache);
                // CopyFrom transplants the field from the resolved source body; every other verb applies to the record directly.
                string? applyNote = null;
                if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
                    WriteEngine.CopyField(
                        selfSource ? selfSnapshots![e] : srcBody!,   // the pre-mutation private snapshot, never the live record
                        ov, req.Path);
                else
                    applyNote = WriteEngine.ApplyVerb(ov, req);
                var (after, landed, _, _) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed) { ApplyNote = applyNote });
            }
            catch (ExpectedApplyRejectionException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)} — {ex.Message} (the file is untouched)");
            }
            catch (MalformedTargetDataException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)} — {ex.Message} (the file is untouched)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {FormIdToken.Of(e.Target)}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Same as Apply: a Subtype change without a SubtypeName syncs the SNAM marker; refuses loud on an unmodeled Subtype.
        if (SyncEditedTopicMarkers(targetMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} ('{fileName}' is UNTOUCHED).");

        // --- DRY RUN: stop AT the point of no return (see Apply's twin). patchLane:false — in-place adds no baseline masters. ---
        if (dryRun)
        {
            if (DryRunMastersPreview(targetMod, resolver, patchLane: false, out var wouldMasters) is { } dryErr)
                return PatchOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(targetMod, resolved.Select(r => r.edit.Target), inMemory: true) : null;
            return new PatchOutcome(true, null, targetPath, false, wouldMasters, ops, 0)
            {
                DryRun = true, InPlace = true, ReadBack = dryBack, Warning = forkWarning,
                Note = JoinNotes(linkNote, MasterGrowWouldNote(fileName, mastersBefore, wouldMasters)),
            };
        }

        // --- Phase 4: re-serialize the WHOLE target over itself via WriteInPlace, self-lock first, against the WHOLE known-master set. ---
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath, resolver.DataDir); }
        catch (MissingModException ex)
        {
            // This arm fires FIRST, so an unopenable-but-ACTIVE master lands here and prefers the named cause over "NOT active".
            return PatchOutcome.Fail(UnopenableMasterClause(ex, session) is { Length: > 0 } why
                ? $"writing '{fileName}' in place failed: the edited records reference a plugin the write cannot " +
                  $"resolve ({ex.Message}).{why} The existing file is untouched."
                : $"writing '{fileName}' in place failed: the edited records reference a plugin that is NOT active in " +
                  $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                  "Enable that plugin in MO2 (or reference an active one) and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return PatchOutcome.Fail(SerializeFailure($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // --- Phase 5: re-open, report the master header, and run the touched-record verify (default ON for in-place). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        IReadOnlyList<OpResult> reported = ops;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            // The strings-aware factory: a localized plugin opened bare reads every string empty, and the verify COMPARES.
            back = LoadOrderResolver.OpenOverlay(targetPath, resolver.DataDir);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
            // The per-op half of the same verify; its own try, so a COMPARE fault leaves the ops unverified, never failing the write.
            try { reported = VerifyLandedAgainstFile(back, resolved.Select(r => (r.edit.Target, (WriteRequest?)r.req)).ToList(), ops); }
            // A pass that THREW is not "no file check ran". Only the ops it would have ASKED about, never the appended SNAM syncs.
            catch
            {
                int asked = resolved.Count;
                reported = ops.Select((o, k) => k < asked ? o with { VerifyAttempted = true } : o).ToList();
            }
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"'{fileName}' was edited in place but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, targetPath, false, masters, reported, bytes)
            { ReadBack = readBack, InPlace = true, Warning = forkWarning,
              Note = JoinNotes(linkNote, MasterGrowNote(fileName, mastersBefore, masters)) };
    }

    /// <summary>The re-sort note when a write GREW the target's master header, since a plugin loads only AFTER its masters.</summary>
    static string? MasterGrowNote(string fileName, HashSet<string> mastersBefore, IReadOnlyList<string> mastersAfter)
    {
        var grown = mastersAfter.Where(m => !mastersBefore.Contains(m)).ToList();
        if (grown.Count == 0) return null;
        return $"{string.Join(", ", grown)} {(grown.Count == 1 ? "was" : "were")} added as a master of '{fileName}' — " +
               "a plugin loads only if its masters load BEFORE it, so re-sort your load order (LOOT / MO2) before playing.";
    }

    /// <summary>The predictive twin of <see cref="MasterGrowNote"/> for a dry run, where nothing was added yet.</summary>
    static string? MasterGrowWouldNote(string fileName, HashSet<string> mastersBefore, IReadOnlyList<string> wouldMasters)
    {
        var grown = wouldMasters.Where(m => !mastersBefore.Contains(m)).ToList();
        if (grown.Count == 0) return null;
        return $"the real write would ADD {string.Join(", ", grown)} as master(s) of '{fileName}' — a plugin " +
               "loads only if its masters load BEFORE it, so re-sort your load order (LOOT / MO2) after the real write.";
    }

    /// <summary>The dry run's pre-serialize reference check and master preview: a referenced plugin not in the active order is refused.</summary>
    static string? DryRunMastersPreview(SkyrimMod mod, LoadOrderResolver resolver, bool patchLane, out IReadOnlyList<string> masters)
    {
        masters = Array.Empty<string>();
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < resolver.PluginNames.Count; i++) priority[resolver.PluginNames[i]] = i;

        var referenced = new Dictionary<ModKey, FormKey>();   // referenced plugin -> first record referencing it (for the refusal)
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec.FormKey.ModKey != mod.ModKey) referenced.TryAdd(rec.FormKey.ModKey, rec.FormKey);
            try
            {
                foreach (var link in rec.EnumerateFormLinks())
                    if (!link.FormKey.IsNull && link.FormKey.ModKey != mod.ModKey)
                        referenced.TryAdd(link.FormKey.ModKey, rec.FormKey);
            }
            catch (Exception ex)
            {
                // A throw here is usually a COMPOSED record whose required polymorphic sub-field was left null, per WriteEngine.RootNullArm.
                if (WriteEngine.RootNullArm(ex) is not null)
                    return $"dry run caught what the real write would fail on: {FormIdToken.Of(rec.FormKey)} carries a required modeled " +
                           "sub-field left null (the same null-dereference Mutagen's writer refuses at serialize). The " +
                           "cause is a COMPOSED record that left a required polymorphic sub-field unset — e.g. a Condition " +
                           "composed without its Data arm, or a leveled-list / effect element missing a required part. " +
                           "Compose that sub-field too (select the arm via compose). Nothing was written.";
                return $"dry run: enumerating {FormIdToken.Of(rec.FormKey)}'s references threw ({ex.GetType().Name}: {ex.Message}) — " +
                       "the would-be content could not be fully checked; the real write would hit the same data. Nothing was written.";
            }
        }

        var missing = referenced.Where(kv => !priority.ContainsKey(kv.Key.FileName.String))
                                .Select(kv => $"{kv.Key.FileName} (referenced by {kv.Value})").ToList();
        if (missing.Count > 0)
            return $"dry run caught what the real write would fail on: the would-be content references " +
                   $"{missing.Count} plugin(s) NOT active in the load order — a reference into an inactive plugin " +
                   $"can't resolve in game, and the real serialize refuses it (MissingModException): " +
                   $"{string.Join("; ", missing)}. Enable the plugin(s) in MO2 (or reference active ones). Nothing was written.";

        var set = new HashSet<string>(referenced.Keys.Select(mk => mk.FileName.String), StringComparer.OrdinalIgnoreCase);
        if (patchLane)
            foreach (var bm in WriteEngine.BaselineMasters)
                if (priority.ContainsKey(bm.FileName.String)) set.Add(bm.FileName.String);

        // An UNOPENABLE referenced plugin is ACTIVE, so membership passes it; the BASELINE case is asked first, without the
        // threshold, and the threshold itself is the measured header rule in docs/architecture/write-path.md.
        foreach (var bm in WriteEngine.BaselineMasters)
            // IsUnopenable already returns false for a name absent from the order, so no membership pre-test is needed.
            if (resolver.IsUnopenable(bm.FileName.String))
                // The REAL call's own message, constructed rather than paraphrased, so prediction and refusal cannot drift.
                return "dry run caught what the real write would fail on: "
                       + new UnopenableBaselineMasterException(bm.FileName.String).Message;

        var unopenable = set.Where(resolver.IsUnopenable).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (unopenable.Count > 0 && set.Count > 1)
            return $"dry run caught what the real write would fail on: the would-be content references " +
                   $"{string.Join(", ", unopenable.Select(n => $"'{n}'"))}, which {(unopenable.Count == 1 ? "is" : "are")} " +
                   "ACTIVE in your load order but cannot be opened by houseCARL (see load_order_status for the reason), " +
                   "so the master header this write needs cannot be sorted against " +
                   $"{(unopenable.Count == 1 ? "it" : "them")}. Repair or remove " +
                   $"{(unopenable.Count == 1 ? "that plugin" : "those plugins")} in MO2 and retry — writes that do NOT " +
                   "reference their records are unaffected. Nothing was written.";

        masters = set.OrderBy(n => priority[n]).ToList();
        return null;
    }

    /// <summary>Resolve the target's OWN declared masters to overlays in declared order; one absent from the order is a loud refusal.</summary>
    static ISkyrimModGetter[] ResolveOwnMasters(
        LoadOrderResolver.IndexView view, SkyrimMod targetMod, List<IDisposable> overlays, out string? missing)
    {
        missing = null;
        var resolved = new List<ISkyrimModGetter>();
        foreach (var mr in targetMod.ModHeader.MasterReferences)
        {
            var mfn = mr.Master.FileName.String;
            var mpath = view.PluginPath(mfn);
            if (mpath is null)
            {
                missing = $"cannot re-serialize '{targetMod.ModKey.FileName}' in place: its declared master '{mfn}' is not active " +
                          "in the load order, so a faithful re-serialize can't resolve the references into it. Enable that master " +
                          "(or fix the target's masters in xEdit) first. The file is UNTOUCHED.";
                return Array.Empty<ISkyrimModGetter>();
            }
            // This lane opens the declared masters itself, so the unopenable skip never reaches it; asked BEFORE the open.
            if (view.IsUnopenable(mfn))
            {
                missing = $"cannot re-serialize '{targetMod.ModKey.FileName}' in place: its declared master '{mfn}' is ACTIVE " +
                          "but cannot be opened by houseCARL (see load_order_status for the reason), so a faithful " +
                          "re-serialize can't resolve the references into it. Repair or remove that plugin in MO2 and " +
                          "retry. The file is UNTOUCHED.";
                return Array.Empty<ISkyrimModGetter>();
            }
            ISkyrimModGetter ov;
            try { ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(mpath)); }
            catch (Exception ex)
            {
                // A master that opens fine at index time can still fail here (the file changed). Named, never an escaping throw.
                missing = $"cannot re-serialize '{targetMod.ModKey.FileName}' in place: its declared master '{mfn}' could " +
                          $"not be opened ({WriteEngine.Describe(ex)}) — a faithful re-serialize can't resolve the " +
                          "references into it. Repair or remove that plugin in MO2 and retry. The file is UNTOUCHED.";
                return Array.Empty<ISkyrimModGetter>();
            }
            overlays.Add((IDisposable)ov);
            resolved.Add(ov);
        }
        return resolved.ToArray();
    }

    /// <summary>Remove WHOLE records the patch ITSELF carries, present-checked first; a singular owned child is detached instead.</summary>
    public static RemovalOutcome RemoveRecords(LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string outPath)
    {
        OrderStamp? epoch = null;
        var outcome = RemoveRecordsCore(resolver, targets, outPath, ref epoch);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>The body of <see cref="RemoveRecords"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static RemovalOutcome RemoveRecordsCore(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string outPath, ref OrderStamp? epoch)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Per-call overlay session: the known-master set for the re-serialize is opened through it and disposed on return.
        using var session = resolver.OpenSession();
        // This lane resolves no winner but is not build-free: the re-serialize's master context is the session's.
        epoch = resolver.Capture().Stamp;

        var fileName = Path.GetFileName(outPath);
        if (!File.Exists(outPath))
            return RemovalOutcome.Fail($"cannot remove: no existing patch at {outPath}. Removal targets a patch houseCARL already created.");

        SkyrimMod patchMod;
        try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
        catch (Exception ex) { return RemovalOutcome.Fail($"cannot open patch to remove from ({fileName}): {ex.GetType().Name}: {ex.Message}"); }

        // Present-check in one enumeration: type+editorid for the report, and the record's FLAT GROUP's T for the routing.
        var carried = new Dictionary<FormKey, (string type, string? edid, Type runtime)>();
        foreach (var r in patchMod.EnumerateMajorRecords())
            carried[r.FormKey] = (RecordNaming.StripOverlay(r.GetType().Name), r.EditorID, WriteEngine.RemovalTypeFor(r));

        var problems = new List<string>();
        var toRemove = new List<RemovedRecord>(targets.Count);
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;   // de-dup repeated targets in one call
            if (!carried.TryGetValue(fk, out var info))
            {
                problems.Add(
                    $"{FormIdToken.Of(fk)}: not carried by patch '{fileName}' — only a record the patch ITSELF defines (a created record " +
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

        // Literal drop-from-group via the typed overload, which reaches NESTED records but not a SINGULAR owned child.
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
        if (DetachOwnedChildren(patchMod, toRemove) is { } detachFailed)
            return RemovalOutcome.Fail(detachFailed + $" '{fileName}' is UNTOUCHED.");
        if (RemoveSurvivors(patchMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" '{fileName}' is UNTOUCHED.");

        // Serialize ONCE with the full known-master set, behind the two-part self-lock; an orphaned master drops from the lean header.
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex) { return RemovalOutcome.Fail(SerializeFailure("writing the patch after removal failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // Re-open: report the master header and how many records remain (0 ⇒ an inert plugin). Dispose the overlay.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            remaining = back.EnumerateMajorRecords().Count();
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { return RemovalOutcome.Fail($"records removed + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new RemovalOutcome(true, null, outPath, toRemove, masters, remaining, bytes);
    }

    /// <summary>Drop the targets the typed <c>Remove</c> could not reach by DETACHING them from the parent slot that holds them.</summary>
    static string? DetachOwnedChildren(SkyrimMod mod, IReadOnlyList<RemovedRecord> toRemove)
    {
        var named = new HashSet<FormKey>(toRemove.Select(rr => rr.Target));
        var stillHere = new HashSet<FormKey>(named);
        stillHere.IntersectWith(mod.EnumerateMajorRecords().Select(r => r.FormKey));
        foreach (var fk in stillHere)
        {
            // Not found in a slot is NOT an error here: the survivor check below is the one that refuses for it.
            if (!OwnedChildLifecycle.TryFindSlot(mod, fk, out var slot)) continue;
            var unnamed = OwnedChildLifecycle.DescendantsOf(slot.Child).Where(d => !named.Contains(d.FormKey)).ToList();
            if (unnamed.Count > 0)
                return $"removing {FormIdToken.Of(fk)} means detaching it from '{slot.Describe()}', which takes the "
                     + $"{unnamed.Count} record(s) under it with it — and you named none of them: "
                     + string.Join(", ", unnamed.Take(5).Select(d => $"{FormIdToken.Of(d.FormKey)}{(d.EditorID is { } e ? $" ({e})" : "")}"))
                     + (unnamed.Count > 5 ? $", and {unnamed.Count - 5} more" : "")
                     + ". Name them in the same call so the removal reports every record it drops, or leave this one.";
            if (OwnedChildLifecycle.Detach(slot) is { } err) return err;
        }
        return null;
    }

    /// <summary>IN-MEMORY absence verify before any serialize, because the typed <c>Remove</c> can no-op WITHOUT throwing.</summary>
    static string? RemoveSurvivors(SkyrimMod mod, IReadOnlyList<RemovedRecord> toRemove)
    {
        var mustBeGone = toRemove.Select(rr => rr.Target).ToHashSet();
        var survivors = new List<FormKey>();
        foreach (var r in mod.EnumerateMajorRecords())
            if (mustBeGone.Contains(r.FormKey)) survivors.Add(r.FormKey);
        if (survivors.Count == 0) return null;
        return $"Remove did not drop {survivors.Count} record(s) ({string.Join(", ", survivors)}) — the engine " +
               "no-op'd without throwing; a real inconsistency surfaced BEFORE any rewrite, not swallowed (Q3).";
    }

    /// <summary>Remove WHOLE records IN PLACE — <see cref="RemoveRecords"/>'s in-place sibling, verified as ABSENCE on the re-opened file.</summary>
    public static RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string targetPath, string targetName)
    {
        OrderStamp? epoch = null;
        var outcome = RemoveRecordsInPlaceCore(resolver, targets, targetPath, targetName, ref epoch);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>The body of <see cref="RemoveRecordsInPlace"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static RemovalOutcome RemoveRecordsInPlaceCore(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string targetPath, string targetName,
        ref OrderStamp? epoch)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Per-call overlay session, same as ApplyInPlace: every read is opened THROUGH it and disposed on return.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: the target must be an active, FULLY-PARSEABLE plugin — the ApplyInPlace guard. ---
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down
        if (!view.ContainsPlugin(targetName))
            return RemovalOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return RemovalOutcome.Fail(
                $"cannot remove from '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");

        // --- Phase 2: open the TARGET mutably. EAGER, the SINGLE plugin only; an unparseable plugin is REFUSED here. ---
        if (!File.Exists(targetPath))
            return RemovalOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(targetPath)); }
        catch (Exception ex)
            { return RemovalOutcome.Fail($"cannot open '{fileName}' to remove from in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return RemovalOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");

        // --- Phase 3: present-check against what the TARGET carries; a key the file does not own is REFUSED loud. ---
        var carried = new Dictionary<FormKey, (string type, string? edid, Type runtime)>();
        foreach (var r in targetMod.EnumerateMajorRecords())
            carried[r.FormKey] = (RecordNaming.StripOverlay(r.GetType().Name), r.EditorID, WriteEngine.RemovalTypeFor(r));

        var problems = new List<string>();
        var toRemove = new List<RemovedRecord>(targets.Count);
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;   // de-dup repeated targets in one call
            if (!carried.TryGetValue(fk, out var info))
            {
                problems.Add(
                    $"{FormIdToken.Of(fk)}: not carried by '{fileName}' — in-place removes only a record the file ITSELF defines or " +
                    "overrides. To stop ANOTHER plugin's record from winning, use the default patch lane (forward the " +
                    "master version, or override it) instead.");
                continue;
            }
            toRemove.Add(new RemovedRecord(fk, info.type, info.edid));
        }
        if (problems.Count > 0)
            return RemovalOutcome.Fail(
                $"refused — {problems.Count} of {targets.Count} target(s) not carried by '{fileName}'; NOTHING removed:\n  - "
                + string.Join("\n  - ", problems));

        // --- Phase 4: literal drop-from-group via the typed overload; a singular owned child goes through the detach below. ---
        try
        {
            foreach (var rr in toRemove)
                ((IMajorRecordEnumerable)targetMod).Remove(rr.Target, carried[rr.Target].runtime, throwIfUnknown: true);
        }
        catch (Exception ex)
        {
            return RemovalOutcome.Fail(
                $"present-check passed but Remove threw — a real engine inconsistency, surfaced not swallowed (Q3): "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
        if (DetachOwnedChildren(targetMod, toRemove) is { } detachFailed)
            return RemovalOutcome.Fail(detachFailed + $" Your original '{fileName}' is UNTOUCHED.");
        if (RemoveSurvivors(targetMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" Your original '{fileName}' is UNTOUCHED.");

        // --- Phase 5: re-serialize the target over itself against its OWN declared masters, self-lock first. ---
        session.ReleaseOverlay(fileName);
        var masterOverlays = new List<IDisposable>();
        try
        {
            ISkyrimModGetter[] ownMasters = ResolveOwnMasters(view, targetMod, masterOverlays, out var missing);
            if (missing is not null) return RemovalOutcome.Fail(missing);
            try { WriteEngine.WriteInPlace(targetMod, ownMasters, targetPath, resolver.DataDir); }
            // The localized-target refusal names its own whole sentence, which this lane's lead would contradict.
            catch (LocalizedTargetUnsupportedException ex) { return RemovalOutcome.Fail(ex.Message); }
            catch (Exception ex)
                { return RemovalOutcome.Fail($"writing '{fileName}' in place after removal failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // --- Phase 6: re-open, report the master header and remaining count, and VERIFY each removed FormKey is ABSENT. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(targetPath));
            var removedKeys = toRemove.Select(rr => rr.Target).ToHashSet();
            var stillThere = new List<FormKey>();
            foreach (var r in back.EnumerateMajorRecords())
            {
                remaining++;
                if (removedKeys.Contains(r.FormKey)) stillThere.Add(r.FormKey);
            }
            if (stillThere.Count > 0)
                return RemovalOutcome.Fail(
                    $"'{fileName}' was rewritten but the verify found {stillThere.Count} record(s) that should have been removed still present " +
                    $"({string.Join(", ", stillThere)}) — a real inconsistency surfaced, not swallowed (Q3). The on-disk file may differ from intent; re-check in xEdit.");
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
        }
        catch (Exception ex) { return RemovalOutcome.Fail($"records removed + written but '{fileName}' could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new RemovalOutcome(true, null, targetPath, toRemove, masters, remaining, bytes) { InPlace = true };
    }

    /// <summary>Phase-1 source resolution SHARED by the two forward lanes, off ONE captured view, collecting ALL problems.</summary>
    static List<(ForwardSpec spec, IMajorRecordGetter body, string? priorWinner, bool wasWinner, bool offOrderBody)> ResolveForwardSources(
        LoadOrderResolver.OverlaySession session, LoadOrderResolver.IndexView view,
        IReadOnlyList<ForwardSpec> specs, string targetPath, bool selfIsTarget, string sourceParam, out string? refusal,
        OffOrderForwardSource? offOrder = null)
    {
        var fileName = Path.GetFileName(targetPath);
        var resolved = new List<(ForwardSpec spec, IMajorRecordGetter body, string? priorWinner, bool wasWinner, bool offOrderBody)>(specs.Count);
        var problems = new List<string>();
        var seen = new HashSet<FormKey>();
        // AbsenceClause costs a profile parse plus a whole-install sweep, so it is memoized per CALL, never per resolver.
        var absenceMemo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // One walk per SOURCE plugin for the whole call (#723), declared under the same guards the loop refuses on first.
        var gather = new BodyGather(view, session);
        var wantSeen = new HashSet<FormKey>();
        foreach (var s in specs)
        {
            if (!wantSeen.Add(s.Target)) continue;
            if (IsOffOrderSource(offOrder, s, view)) continue;                       // pre-fetched off the file's own overlay
            if (string.Equals(s.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase)) continue;
            var wantOrigin = s.Target.ModKey.FileName.String;
            if (!string.Equals(wantOrigin, fileName, StringComparison.OrdinalIgnoreCase) && !view.ContainsPlugin(wantOrigin)) continue;
            if (view.ContainsPlugin(s.FromPlugin)) gather.Want(s.FromPlugin, s.Target);
        }
        gather.Gather();
        string Absence(string plugin)
        {
            if (!absenceMemo.TryGetValue(plugin, out var clause)) absenceMemo[plugin] = clause = view.AbsenceClause(plugin);
            return clause;
        }

        foreach (var s in specs)
        {
            if (!seen.Add(s.Target))
            { problems.Add($"{FormIdToken.Of(s.Target)}: forwarded more than once in this call — name each target once (one source per record)."); continue; }
            // Is the source THE FILE THIS CALL IS ABOUT TO WRITE? By NAME in order, by FULL-PATH identity off-order.
            bool sourceIsSelf = IsOffOrderSource(offOrder, s, view)
                ? SameFile(offOrder!.Path, targetPath)
                : string.Equals(s.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase);
            if (sourceIsSelf)
            {
                problems.Add(selfIsTarget
                    ? $"{FormIdToken.Of(s.Target)}: {sourceParam} '{s.FromPlugin}' is the in-place target itself — forwarding a plugin's own version into itself is a no-op; name the OTHER plugin whose version you want carried in."
                    : $"{FormIdToken.Of(s.Target)}: {sourceParam} '{s.FromPlugin}' is the output patch itself — forwarding a patch's own version into itself is a no-op; name the EARLIER plugin whose version you want to re-assert.");
                continue;
            }
            // The ORIGIN plugin must be active, since the patch overrides the ORIGIN FormKey — except when the ORIGIN is this artifact.
            var originMaster = s.Target.ModKey.FileName.String;
            if (!string.Equals(originMaster, fileName, StringComparison.OrdinalIgnoreCase) && !view.ContainsPlugin(originMaster))
            { problems.Add($"{FormIdToken.Of(s.Target)}: the record ORIGINATES in '{originMaster}', which is not active — a forward overrides the record's origin FormKey, so the patch would need '{originMaster}' as a master. Enable it first (forwarding copies FROM source, but it cannot invent the origin master).{Absence(originMaster)}"); continue; }
            IMajorRecordGetter? body;
            bool offOrderBody = IsOffOrderSource(offOrder, s, view);
            if (offOrderBody)
            {
                // Pre-fetched by the service off the file's own overlay, so a miss here is an engine inconsistency, not user error.
                if (!offOrder!.Bodies.TryGetValue(s.Target, out body) || body is null)
                { problems.Add($"{FormIdToken.Of(s.Target)}: source plugin '{s.FromPlugin}' resolved off-order ({offOrder.Path}) but its body was not pre-fetched — surfaced, not skipped (Q3)."); continue; }
            }
            else
            {
                if (!view.ContainsPlugin(s.FromPlugin))
                { problems.Add($"{FormIdToken.Of(s.Target)}: source plugin '{s.FromPlugin}' is not in the load order — name an active plugin that defines or overrides this record.{Absence(s.FromPlugin)}"); continue; }
                if (view.ExcludedPlugins.TryGetValue(s.FromPlugin, out var why))
                { problems.Add($"{FormIdToken.Of(s.Target)}: source plugin '{s.FromPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
                body = gather.Body(s.FromPlugin, s.Target);
                if (body is null)
                { problems.Add($"{FormIdToken.Of(s.Target)}: source plugin '{s.FromPlugin}' is in the load order but does NOT define or override this record (it doesn't touch it) — there is no version of it there to forward."); continue; }
            }
            var w = view.ResolveWinner(s.Target);
            // An OFF-ORDER source is never in the order, so wasWinner is false by construction, never a name comparison.
            resolved.Add((s, body, w?.WinnerPlugin,
                !offOrderBody && w is { } wi && string.Equals(wi.WinnerPlugin, s.FromPlugin, StringComparison.OrdinalIgnoreCase),
                offOrderBody));
        }
        refusal = problems.Count > 0
            ? $"refused — {problems.Count} of {specs.Count} forward(s) rejected; NOTHING written:\n  - " + string.Join("\n  - ", problems)
            : null;
        return resolved;
    }

    /// <summary>Does this spec's source resolve through <paramref name="offOrder"/>? A spelling match re-checked against this call's capture.</summary>
    static bool IsOffOrderSource(OffOrderForwardSource? offOrder, ForwardSpec s, LoadOrderResolver.IndexView view) =>
        offOrder is not null
        && string.Equals(offOrder.Plugin, s.FromPlugin, StringComparison.OrdinalIgnoreCase)
        && !view.ContainsPlugin(s.FromPlugin);

    /// <summary>Do two paths denote the same file? Full-path compare, never a filename compare.</summary>
    static bool SameFile(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>The source link cache for ONE forward copy: null when flat, the off-order file's own, else the session's.</summary>
    static ILinkCache? SourceCacheFor(
        LoadOrderResolver.OverlaySession session, ForwardSpec spec, IMajorRecordGetter body,
        bool offOrderBody, OffOrderForwardSource? offOrder)
    {
        if (!WriteEngine.RecordNeedsSourceCache(body)) return null;
        return offOrderBody ? offOrder!.LinkCache() : session.LinkCacheFor(spec.FromPlugin);
    }

    /// <summary>Forward records INTO an EXISTING plugin the user owns, IN PLACE; a FormKey it already carries is dropped before the copy.</summary>
    public static ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string targetPath, string targetName,
        string sourceParam, bool fullReadback = true, bool dryRun = false,
        OffOrderForwardSource? offOrder = null)
    {
        OrderStamp? epoch = null;
        bool usedOffOrder = false;
        var outcome = ForwardRecordsInPlaceCore(resolver, specs, targetPath, targetName, fullReadback, dryRun, sourceParam, offOrder, ref epoch, ref usedOffOrder);
        outcome = StampOffOrderSource(outcome, offOrder, usedOffOrder);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>What each forward lane left alone, SUBSTITUTED into a child-group refusal rather than appended after it.</summary>
    const string InPlaceUntouched = "Nothing was serialized; your original is UNTOUCHED.";
    const string ExtendUntouched = "Nothing was serialized; the extended patch's on-disk file is UNTOUCHED.";

    /// <summary>The body of <see cref="ForwardRecordsInPlace"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static ForwardOutcome ForwardRecordsInPlaceCore(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string targetPath, string targetName,
        bool fullReadback, bool dryRun, string sourceParam, OffOrderForwardSource? offOrder, ref OrderStamp? epoch, ref bool usedOffOrder)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Per-call overlay session, same as ApplyInPlace — no handle held at rest.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: target guards (the ApplyInPlace posture) plus source resolution off ONE captured view. ---
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down
        if (!view.ContainsPlugin(targetName))
            return ForwardOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return ForwardOutcome.Fail(
                $"cannot forward into '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");
        var resolved = ResolveForwardSources(session, view, specs, targetPath, selfIsTarget: true, sourceParam, out var refusal, offOrder);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);
        usedOffOrder = resolved.Any(r => r.offOrderBody);   // the arm ACTUALLY taken, not the one the caller planned

        // The same fork question the patch lane asks, on the POSITIONED arm: the target is an active plugin.
        var forkWarning = ForkWarning.For(view, resolved.Select(r => r.spec.Target), fileName);

        // --- Phase 2: open the TARGET mutably (EAGER, the single plugin only — never the order). ---
        if (!File.Exists(targetPath))
            return ForwardOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(targetPath)); }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"cannot open '{fileName}' to forward into in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // Declared masters before any mutation — diffed against the re-opened header (see MasterGrowNote).
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --- Phase 3: replace-or-copy each source body into the TARGET; nothing is serialized until Phase 4. ---
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        foreach (var r in targetMod.EnumerateMajorRecords())
            alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner, offOrderBody) in resolved)
        {
            try
            {
                bool replaced = false;
                var carriedChildren = default(WriteEngine.ChildGroupCarry);
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    // The drop takes the record's CHILD GROUP and the copy carries none back in, so lift them off first, re-attach after.
                    if (WriteEngine.TryCaptureChildGroup(existing, InPlaceUntouched, out carriedChildren) is { } captureRefusal)
                        return ForwardOutcome.Fail(captureRefusal);
                    ((IMajorRecordEnumerable)targetMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    if (targetMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {FormIdToken.Of(spec.Target)}: '{fileName}' already carries this record and its existing " +
                            "version could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); your original is UNTOUCHED.");
                    replaced = true;
                }
                var fresh = WriteEngine.GenericGetOrAddAsOverride(targetMod, body, SourceCacheFor(session, spec, body, offOrderBody, offOrder));
                if (WriteEngine.RestoreChildGroup(fresh, carriedChildren, InPlaceUntouched) is { } childRefusal)
                    return ForwardOutcome.Fail(childRefusal);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced, PreservedChildren: carriedChildren.Count));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {FormIdToken.Of(spec.Target)} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}. Your original is UNTOUCHED.");
            }
        }

        // --- DRY RUN: stop AT the point of no return (see Apply's twin block); WriteInPlace adds no baseline masters. ---
        if (dryRun)
        {
            if (DryRunMastersPreview(targetMod, resolver, patchLane: false, out var wouldMasters) is { } dryErr)
                return ForwardOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(targetMod, resolved.Select(r => r.spec.Target), inMemory: true) : null;
            return new ForwardOutcome(true, null, targetPath, false, forwarded, wouldMasters, 0)
            {
                DryRun = true, InPlace = true, ReadBack = dryBack, Warning = forkWarning,
                Note = MasterGrowWouldNote(fileName, mastersBefore, wouldMasters),
            };
        }

        // --- Phase 4: re-serialize over the original (WriteInPlace, whole known-master set; the self-lock first, atomic swap). ---
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath, resolver.DataDir); }
        catch (MissingModException ex)
        {
            // Same shadowing as the apply twin — see there.
            return ForwardOutcome.Fail(UnopenableMasterClause(ex, session) is { Length: > 0 } why
                ? $"writing '{fileName}' in place failed: the forwarded records reference a plugin the write cannot " +
                  $"resolve ({ex.Message}).{why} The existing file is untouched."
                : $"writing '{fileName}' in place failed: the forwarded records reference a plugin that is NOT active in " +
                  $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                  "Enable that plugin in MO2 and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail(SerializeFailure($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // --- Phase 5: re-open + report masters/bytes + the touched-record verify (default ON for in-place). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(targetPath));
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"'{fileName}' was rewritten but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, targetPath, false, forwarded, masters, bytes)
            { ReadBack = readBack, InPlace = true, Warning = forkWarning,
              Note = MasterGrowNote(fileName, mastersBefore, masters) };
    }


    /// <summary>The NAMED cause when a serialize failed on a master skipped as unopenable; empty string otherwise.</summary>
    public static string UnopenableMasterClause(Exception ex, LoadOrderResolver.OverlaySession session)
    {
        // NOT the baseline class: its Message IS the whole refusal, and SerializeFailure SUBSTITUTES rather than appends.
        for (Exception? b = ex; b is not null; b = b.InnerException)
            if (b is UnopenableBaselineMasterException) return "";
        if (session.SkippedUnopenable.Count == 0) return "";
        var hit = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMissingMods(ex, session, hit, depth: 0);
        if (hit.Count == 0) return "";
        var names = string.Join(", ", hit.Select(n => $"'{n}'"));
        bool one = hit.Count == 1;
        return $" CAUSE: {names} {(one ? "is" : "are")} ACTIVE in your load order but cannot be opened by " +
               "houseCARL (see load_order_status for the reason), so the header this write needs cannot be sorted against " +
               $"{(one ? "it" : "them")}. Repair or remove {(one ? "that plugin" : "those plugins")} " +
               "in MO2 and retry — writes that do NOT reference their records are unaffected.";
    }

    /// <summary>Render a serialize-failure message, except that a BASELINE refusal SUBSTITUTES its own message for the lot.</summary>
    public static string SerializeFailure(string lead, Exception ex, LoadOrderResolver.OverlaySession session, string trailer = "")
    {
        for (Exception? b = ex; b is not null; b = b.InnerException)
        {
            if (b is UnopenableBaselineMasterException ub) return ub.Message;
            // Same reason as the baseline refusal: a LOCALIZED target is refused before any lead here could be true.
            if (b is LocalizedTargetUnsupportedException lt) return lt.Message;
            // And the same for a value the target's encoding cannot spell; the exception's message is the whole sentence.
            if (b is UnspellableTextException ut) return ut.Message;
        }
        var body = lead + WriteEngine.Describe(ex) + UnopenableMasterClause(ex, session);
        if (trailer.Length == 0) return body;
        // Exactly ONE terminator before a lane's tail, which a fixed trailer cannot do because the clause is conditional.
        return body.TrimEnd().EndsWith('.') ? body + trailer : body + "." + trailer;
    }

    /// <summary>Walk an exception chain collecting the SKIPPED plugins a <see cref="MissingModException"/> names; depth-capped.</summary>
    static void CollectMissingMods(Exception? ex, LoadOrderResolver.OverlaySession session, SortedSet<string> into, int depth)
    {
        if (ex is null || depth > 8) return;
        if (ex is MissingModException mme)
            foreach (var mp in mme.ModPaths)
            {
                var name = mp.ModKey.FileName.String;
                if (session.SkippedUnopenable.Contains(name)) into.Add(name);
            }
        if (ex is AggregateException agg)
        {
            // …and NOT the tail below: AggregateException.InnerException IS InnerExceptions[0], so both would halve the cap.
            foreach (var inner in agg.InnerExceptions) CollectMissingMods(inner, session, into, depth + 1);
            return;
        }
        CollectMissingMods(ex.InnerException, session, into, depth + 1);
    }

    /// <summary>One record to FORWARD: <see cref="FromPlugin"/>'s version of <see cref="Target"/>, deep-copied whole, so the SOURCE decides.</summary>
    public sealed record ForwardSpec
    {
        public required FormKey Target { get; init; }
        public required string FromPlugin { get; init; }
    }

    /// <summary>A forward source resolved OFF-ORDER, pre-fetched by the SERVICE, which also OWNS the overlay past the serialize.</summary>
    public sealed class OffOrderForwardSource
    {
        /// <summary>The <c>source=</c> spelling the caller passed (a filename, or a direct path).</summary>
        public required string Plugin { get; init; }
        /// <summary>The FULL path that spelling located — the identity the self-forward guard compares and the report names.</summary>
        public required string Path { get; init; }
        /// <summary>The install layer it was found in, as the shared locate labels it.</summary>
        public required string Where { get; init; }
        /// <summary>Every wanted record's body, pre-fetched off the overlay; a key absent here never reaches the engine.</summary>
        public required IReadOnlyDictionary<FormKey, IMajorRecordGetter> Bodies { get; init; }
        /// <summary>The overlay the bodies came from, kept for the NESTED-record link cache below. Not owned here.</summary>
        public required ISkyrimModGetter Overlay { get; init; }
        /// <summary>Non-null ⇒ the order's own copy of a plugin the session EXCLUDED, reached by PATH; allowed, never silently.</summary>
        public string? ExcludedReason { get; init; }

        ILinkCache? _cache;
        /// <summary>The source link cache a NESTED record needs when overridden — the off-order twin of <c>LinkCacheFor</c>.</summary>
        public ILinkCache LinkCache() => _cache ??= Overlay.ToImmutableLinkCache();
    }

    /// <summary>WHICH on-disk copy an OFF-ORDER forward source read: spelling, full path, install layer, exclusion reason.</summary>
    public sealed record OffOrderSourceRead(string Plugin, string Path, string Where, string? ExcludedReason = null);

    /// <summary>One record forwarded by <see cref="ForwardRecords"/>: its identity, the source copied from, and the winner it out-ranks.</summary>
    public sealed record ForwardedRecord(
        FormKey Target, string RecordType, string? EditorId, string FromPlugin, string? PriorWinner, bool WasAlreadyWinner,
        bool ReplacedExisting = false, int PreservedChildren = 0);

    /// <summary>The outcome of a <see cref="ForwardRecords"/> call; the source plugin is copied FROM and never mastered ON.</summary>
    public sealed record ForwardOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<ForwardedRecord> Forwarded, IReadOnlyList<string> Masters, long Bytes)
    {
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>The build this outcome was decided from; on the OFF-ORDER arm it covers the winners but not the BODIES.</summary>
        public OrderStamp? Stamp { get; init; }

        /// <summary>That build's fingerprint, read through the stamp.</summary>
        public string? Epoch => Stamp?.Epoch;

        /// <summary>True ⇒ the forwards were written INTO the target's own file, not a houseCARL patch folder.</summary>
        public bool InPlace { get; init; }

        /// <summary>Non-null ⇒ the bodies came from a source the ACTIVE ORDER does not contain; which copy is reported as a fact.</summary>
        public OffOrderSourceRead? OffOrderSource { get; init; }

        /// <summary>True ⇒ the first-touch in-place CONSENT handshake; <see cref="Error"/> is the prompt and nothing was written.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly.</summary>
        public string? Note { get; init; }

        /// <summary>The fork warning, on <see cref="PatchOutcome.Warning"/>'s contract.</summary>
        public string? Warning { get; init; }

        /// <summary>True ⇒ a DRY RUN: the real forward pipeline ran and stopped before the serialize, so nothing was written.</summary>
        public bool DryRun { get; init; }

        public static ForwardOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error, carrying <paramref name="prompt"/>.</summary>
        public static ForwardOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>The outcome of a <see cref="CreatePlugin"/> call, whose record count and masters are re-read off the written file.</summary>
    public sealed record CreatePluginOutcome(
        bool Success, string? Error, string OutputPath, string PluginName, bool Esl,
        IReadOnlyList<string> Masters, int RecordCount, long Bytes)
    {
        public static CreatePluginOutcome Fail(string error) =>
            new(false, error, "", "", false, Array.Empty<string>(), 0, 0);
    }

    /// <summary>One external referencer's in-place repoint result, with the named reason on failure — never silent.</summary>
    public sealed record RepointReport(string Plugin, bool Success, string? Error);

    /// <summary>The outcome of a <see cref="LoadOrderService.CompactPlugin"/> call; <see cref="NeedsAcknowledge"/> is a consent prompt, not an error.</summary>
    public sealed record CompactOutcome(
        bool Success, string? Error, bool NeedsAcknowledge, string OutputPath, string PluginName, bool InPlace, bool Esl,
        IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<RepointReport> Repointed,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples, string? Note = null,
        AssetRenameOutcome? AssetRename = null, IReadOnlyList<string>? ExternalOverriders = null,
        VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null,
        IReadOnlyList<RemapEngine.UnscannablePlugin>? UnscannablePlugins = null)
    {
        public static CompactOutcome Fail(string error) =>
            new(false, error, false, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());
        public static CompactOutcome Confirm(string prompt) =>
            new(false, prompt, true, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());
    }

    /// <summary>The merge tool's outcome; merge has no in-place lane and no consent gate.</summary>
    public sealed record MergeOutcome(
        bool Success, string? Error, string OutputPath, string OutputName,
        IReadOnlyList<string> Donors, IReadOnlyList<string> Masters,
        int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeDonorRemap> DonorRemaps,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<string> ExternalOverriders,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples,
        long Bytes, string? Note = null,
        AssetRenameOutcome? AssetRename = null, VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null,
        IReadOnlyList<string>? LightDonors = null, IReadOnlyList<string>? HeaderMetaDonors = null,
        IReadOnlyList<string>? MasterDonors = null, IReadOnlyList<RemapEngine.UnscannablePlugin>? UnscannablePlugins = null,
        IReadOnlyList<string>? LocalizedDonors = null,
        IReadOnlyList<RemapEngine.MasterDeclarer>? MasterDeclarers = null,
        bool LightCarried = false, int OriginatingRecords = 0,
        MergeSiting? Placement = null)
    {
        public static MergeOutcome Fail(string error) =>
            new(false, error, "", "", Array.Empty<string>(), Array.Empty<string>(), 0, 0,
                Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
                Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), 0);
    }

    /// <summary>FORWARD a NAMED plugin's version of each record INTO the patch as an override; contracts in docs/architecture/write-path.md.</summary>
    public static ForwardOutcome ForwardRecords(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string outPath, bool extend, string sourceParam,
        bool fullReadback = false, bool dryRun = false, OffOrderForwardSource? offOrder = null)
    {
        OrderStamp? epoch = null;
        bool usedOffOrder = false;
        var outcome = ForwardRecordsCore(resolver, specs, outPath, extend, fullReadback, dryRun, sourceParam, offOrder, ref epoch, ref usedOffOrder);
        outcome = StampOffOrderSource(outcome, offOrder, usedOffOrder);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>Stamp WHICH off-order copy a SUCCESSFUL forward read, single-point; a REFUSAL is left unstamped.</summary>
    static ForwardOutcome StampOffOrderSource(ForwardOutcome outcome, OffOrderForwardSource? offOrder, bool usedOffOrder) =>
        offOrder is null || !usedOffOrder || !outcome.Success
            ? outcome
            : outcome with { OffOrderSource = new OffOrderSourceRead(offOrder.Plugin, offOrder.Path, offOrder.Where, offOrder.ExcludedReason) };

    /// <summary>The body of <see cref="ForwardRecords"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static ForwardOutcome ForwardRecordsCore(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string outPath, bool extend, bool fullReadback,
        bool dryRun, string sourceParam, OffOrderForwardSource? offOrder, ref OrderStamp? epoch, ref bool usedOffOrder)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Per-call overlay session: every source plugin this forward reads is opened THROUGH it and disposed on return.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(outPath);

        // --- Phase 1: resolve each source body from its NAMED plugin off ONE captured build, gathered a PLUGIN at a time (#723). ---
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down
        var resolved = ResolveForwardSources(session, view, specs, outPath, selfIsTarget: false, sourceParam, out var refusal, offOrder);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);
        usedOffOrder = resolved.Any(r => r.offOrderBody);   // the arm ACTUALLY taken, not the one the caller planned

        // --- Phase 2: open (extend) or create the patch mod (identical to Apply Phase 2). ---
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return ForwardOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
            catch (Exception ex) { return ForwardOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // Same before-state, same reason as Apply's extend lane: a forwarded body's references can grow the header.
        var mastersBefore = extend
            ? patchMod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // --- Phase 3: deep-copy each source body in as an override; a FormKey the patch ALREADY carries is dropped first. ---
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        if (extend)
            foreach (var r in patchMod.EnumerateMajorRecords())
                alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner, offOrderBody) in resolved)
        {
            try
            {
                bool replaced = false;
                var carriedChildren = default(WriteEngine.ChildGroupCarry);
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    // The drop takes the record's CHILD GROUP and the copy carries none back in, so lift them off before the Remove.
                    if (WriteEngine.TryCaptureChildGroup(existing, ExtendUntouched, out carriedChildren) is { } captureRefusal)
                        return ForwardOutcome.Fail(captureRefusal);
                    ((IMajorRecordEnumerable)patchMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    // The typed Remove can no-op WITHOUT throwing — verify the slot is genuinely empty before the copy.
                    if (patchMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {FormIdToken.Of(spec.Target)}: the patch already carries this record and its existing " +
                            "override could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); nothing was serialized (the extended patch's on-disk file is untouched).");
                    replaced = true;
                }
                var fresh = WriteEngine.GenericGetOrAddAsOverride(patchMod, body, SourceCacheFor(session, spec, body, offOrderBody, offOrder));
                if (WriteEngine.RestoreChildGroup(fresh, carriedChildren, ExtendUntouched) is { } childRefusal)
                    return ForwardOutcome.Fail(childRefusal);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced, PreservedChildren: carriedChildren.Count));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {FormIdToken.Of(spec.Target)} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Same fork question Apply asks, off the same captured view: a forwarded body lands as an override like any other.
        var forkWarning = ForkWarning.For(view, resolved.Select(r => r.spec.Target), fileName);

        // --- DRY RUN: stop AT the point of no return (see Apply's twin block) — report what WOULD be forwarded, write nothing. ---
        if (dryRun)
        {
            if (DryRunMastersPreview(patchMod, resolver, patchLane: true, out var wouldMasters) is { } dryErr)
                return ForwardOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(patchMod, resolved.Select(r => r.spec.Target), inMemory: true) : null;
            return new ForwardOutcome(true, null, outPath, extend, forwarded, wouldMasters, 0)
            {
                DryRun = true, ReadBack = dryBack, Warning = forkWarning,
                Note = mastersBefore is null ? null : MasterGrowWouldNote(fileName, mastersBefore, wouldMasters),
            };
        }

        // --- Phase 4: serialize ONCE with the FULL known-master set (Apply Phase 4's two-part self-lock). ---
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (MissingModException ex)
        {
            // Named, not just loud: the generic catch would render a disabled mod's missing master as a disk fault with no remedy.
            return ForwardOutcome.Fail(UnopenableMasterClause(ex, session) is { Length: > 0 } why
                ? $"writing the patch failed: the forwarded records reference a plugin the write cannot resolve " +
                  $"({ex.Message}).{why} Nothing was written."
                : $"writing the patch failed: the forwarded records reference a plugin that is NOT active in the load " +
                  $"order ({ex.Message}) — a reference into an inactive plugin can't resolve in game, so the patch is " +
                  "refused rather than written with a master nothing loads. Enable that plugin in MO2 and retry. Nothing was written.");
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail(SerializeFailure("writing the patch failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // --- Phase 5: re-open, report the lean derived master header and bytes, and on request each record's read-back. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, outPath, extend, forwarded, masters, bytes)
            { ReadBack = readBack, Warning = forkWarning,
              Note = mastersBefore is null ? null : MasterGrowNote(fileName, mastersBefore, masters) };
    }

    /// <summary>Create an EMPTY, HEADER-ONLY plugin, re-read before success is reported; an empty master set forces no baseline.</summary>
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

        // Serialize through the single WritePatch chokepoint with an EMPTY known-master set → zero masters.
        try { WriteEngine.WritePatch(mod, Array.Empty<ISkyrimModGetter>(), outPath); }
        catch (Exception ex)
            { return CreatePluginOutcome.Fail($"writing the plugin failed (serialize or commit; nothing left on disk): {WriteEngine.Describe(ex)}"); }

        // Re-open and CONFIRM the artifact — never report success on an unverified file.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int recordCount = -1; bool eslBack = false; long bytes = 0;
        string? confirmFail = null;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            recordCount = back.EnumerateMajorRecords().Count();
            eslBack = back.IsSmallMaster;
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { confirmFail = $"plugin written but could not be re-opened to confirm it: {ex.Message}"; }
        finally { (back as IDisposable)?.Dispose(); }   // dispose the overlay BEFORE any delete below (it maps the file)

        if (confirmFail is null && recordCount != 0)
            confirmFail = $"internal error: the created plugin carries {recordCount} record(s), expected 0 (a header-only plugin) — refusing to report success on a wrong artifact (Q3).";
        if (confirmFail is null && masters.Count != 0)
            confirmFail = $"internal error: the created plugin carries {masters.Count} master(s) ({string.Join(", ", masters)}), expected 0 (a header-only plugin references nothing) — refusing to report success on a wrong artifact (Q3).";
        if (confirmFail is null && eslBack != esl)
            confirmFail = $"internal error: the created plugin's light-master (ESL) flag is {eslBack}, expected {esl} — refusing to report success on a wrong artifact (Q3).";

        if (confirmFail is not null)
        {
            // The file we just wrote is wrong or unverifiable — remove it so a refusal leaves NO bad artifact behind.
            try { File.Delete(outPath); } catch { /* best-effort; the loud refusal stands regardless */ }
            return CreatePluginOutcome.Fail(confirmFail);
        }

        return new CreatePluginOutcome(true, null, outPath, fileName, esl, masters, recordCount, bytes);
    }

    // --- COMPACT: the core build half of housecarl_compact_plugin; the service owns the policy half. ---

    /// <summary>What one merge donor holds, read in ONE enumeration; false with a named reason if it cannot be parsed.</summary>
    public static bool TryScanMergeDonor(
        string srcPath, ModKey modKey, IReadOnlySet<ModKey> donorKeys, out MergeDonorScan scan, out string? error)
    {
        scan = new MergeDonorScan(Array.Empty<FormKey>(), Array.Empty<FormKey>(), Array.Empty<FormKey>(), Array.Empty<(FormKey, FormKey)>());
        error = null;
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
            var originating = new List<FormKey>();
            var carried = new List<FormKey>();
            var records = new List<FormKey>();
            var links = new List<(FormKey, FormKey)>();
            // Keyed on the PAIR, because which record a link came from is part of the fact the caller keeps.
            var seenLink = new HashSet<(FormKey, FormKey)>();
            foreach (var rec in ov.EnumerateMajorRecords())
            {
                // Identity first: it is read from the record HEADER, so it is safe on the records the link walk below cannot touch.
                records.Add(rec.FormKey);
                if (rec.FormKey.ModKey == modKey) originating.Add(rec.FormKey);
                else if (donorKeys.Contains(rec.FormKey.ModKey)) carried.Add(rec.FormKey);
                // A deleted record's links are not live, and reaching for them throws on the bodies DeletedRecordRule describes.
                if (DeletedRecordRule.HasNoLiveBody(rec)) continue;
                try
                {
                    foreach (var link in rec.EnumerateFormLinks())
                        if (!link.FormKey.IsNull && donorKeys.Contains(link.FormKey.ModKey) && seenLink.Add((rec.FormKey, link.FormKey)))
                            links.Add((rec.FormKey, link.FormKey));
                }
                // One unparseable record costs this pre-flight that record's links, never the merge: MergeBuild re-checks after.
                catch { /* per-record isolation, as the sibling walkers do */ }
            }
            scan = new MergeDonorScan(originating, carried, records, links);
            return true;
        }
        catch (Exception ex)
        {
            error = $"cannot parse '{modKey.FileName}' to renumber it ({WriteEngine.Describe(ex)}) — houseCARL won't renumber a " +
                    "plugin it can't fully read (it would risk dropping a record it couldn't parse, Q3).";
            return false;
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>One donor's merge-relevant contents: what it defines, what it carries, every key, and its donor links.</summary>
    public sealed record MergeDonorScan(
        IReadOnlyList<FormKey> Originating, IReadOnlyList<FormKey> Carried, IReadOnlyList<FormKey> Records,
        IReadOnlyList<(FormKey Source, FormKey Target)> DonorLinks);

    /// <summary>Read a plugin's ORIGINATING record FormKeys in document order — the set a compaction renumbers.</summary>
    public static bool TryReadOriginatingKeys(string srcPath, ModKey modKey, out IReadOnlyList<FormKey> keys, out string? error)
    {
        keys = Array.Empty<FormKey>(); error = null;
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(srcPath));
            keys = ov.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == modKey).Select(r => r.FormKey).ToList();
            return true;
        }
        catch (Exception ex)
        {
            error = $"cannot parse '{modKey.FileName}' to renumber it ({WriteEngine.Describe(ex)}) — houseCARL won't renumber a " +
                    "plugin it can't fully read (it would risk dropping a record it couldn't parse, Q3).";   // op-neutral: compact AND merge surface this verbatim
            return false;
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>The result of the core compact build: masters, record accounting and bytes, or a refusal with the file UNTOUCHED.</summary>
    public sealed record CompactBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes)
    {
        public static CompactBuildResult Fail(string error) => new(false, error, Array.Empty<string>(), 0, 0, 0);
    }

    /// <summary>Build the compacted plugin P′ and write it to <paramref name="outPath"/>, which in place is <paramref name="srcPath"/> itself.</summary>
    public static CompactBuildResult CompactBuild(
        string srcPath, ModKey modKey, IReadOnlyDictionary<FormKey, FormKey> dict,
        Func<string, string?> resolveMasterPath, string outPath, bool esl, uint floor, string? dataDir)
    {
        // 1. Build P′ in memory, then DISPOSE the source overlay, whose handle the in-place swap must not hold.
        SkyrimMod pPrime;
        RemapEngine.RenumberResult ren;
        List<string> declaredMasters;
        ISkyrimModGetter? srcOv = null;
        try
        {
            // The strings-aware factory: a LOCALIZED source opened bare reads every string EMPTY and the renumber bakes it in.
            srcOv = LoadOrderResolver.OpenOverlay(srcPath, dataDir);
            declaredMasters = srcOv.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
            // A compaction builds a FRESH mod with no header flags, so a compacted localized plugin comes out DE-LOCALIZED.
            pPrime = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = esl };
            ren = RemapEngine.RenumberModInto(pPrime, srcOv, dict);
        }
        catch (Exception ex)
        {
            return CompactBuildResult.Fail($"cannot open/renumber '{modKey.FileName}' to compact it ({WriteEngine.Describe(ex)}) — nothing written.");
        }
        finally { (srcOv as IDisposable)?.Dispose(); }

        if (!ren.Success) return CompactBuildResult.Fail(ren.Error!);

        // NextObjectID = the next free originating id above the renumbered run, which WriteInPlace persists verbatim.
        pPrime.ModHeader.Stats.NextFormID = Math.Max(floor, (uint)(floor + dict.Count));

        // 2. Resolve P's OWN declared masters to overlays; one absent from the active order is a loud refusal.
        var overlays = new List<IDisposable>();
        try
        {
            var resolved = new List<ISkyrimModGetter>();
            foreach (var mfn in declaredMasters)
            {
                var mp = resolveMasterPath(mfn);
                if (mp is null)
                    return CompactBuildResult.Fail(
                        $"cannot compact '{modKey.FileName}': its declared master '{mfn}' is not active in the load order, so a " +
                        "faithful re-serialize can't resolve the references into it. Enable that master (or fix the masters in xEdit) first. Nothing was written.");
                // Wrapped, like MergeBuild's twin: an open throw escaping HERE skips the caller's rider-folder cleanup.
                ISkyrimModGetter mov;
                try { mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(mp)); }
                catch (Exception ex)
                {
                    return CompactBuildResult.Fail(
                        $"cannot compact '{modKey.FileName}': its declared master '{mfn}' could not be opened for the " +
                        $"serialize ({WriteEngine.Describe(ex)}) — if that plugin is active but unreadable, repair or " +
                        "remove it in MO2 and retry. Nothing was written.");
                }
                overlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            // P′ is a fresh mod and never flagged localized, so its serialize emits no string tables.
            try { WriteEngine.WriteInPlace(pPrime, resolved, outPath, dataDir); }
            catch (Exception ex)
            {
                return CompactBuildResult.Fail(
                    $"writing the compacted plugin failed (serialize or commit; nothing partial left): {WriteEngine.Describe(ex)} — " +
                    $"note: a sub-0x{RemapEngine.EslFloor:X} originating record, or (for the light range) one above 0x{RemapEngine.EslCeiling:X}, is rejected by the light-/master-aware write here.");
            }
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        long bytes = 0; try { bytes = new FileInfo(outPath).Length; } catch { }
        return new CompactBuildResult(true, null, declaredMasters, ren.RecordsCopied, ren.RecordsRenumbered, bytes);
    }

    /// <summary>The result of the core merge build, or a refusal with the output UNTOUCHED.</summary>
    public sealed record MergeBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts, long Bytes,
        IReadOnlyList<string>? LightDonors = null, IReadOnlyList<string>? HeaderMetaDonors = null,
        IReadOnlyList<string>? MasterDonors = null, bool LightCarried = false, int OriginatingRecords = 0)
    {
        public static MergeBuildResult Fail(string error) =>
            new(false, error, Array.Empty<string>(), 0, 0, Array.Empty<RemapEngine.MergeConflict>(), 0);
    }

    /// <summary>Build the merged plugin M from the donors in LOAD ORDER; a link still pointing INTO a donor REFUSES the build.</summary>
    public static MergeBuildResult MergeBuild(
        IReadOnlyList<(string Name, string Path, ModKey Key)> donorsByLoadOrder, ModKey outKey,
        IReadOnlyDictionary<FormKey, FormKey> dict, IReadOnlyList<string> masters,
        Func<string, string?> resolveMasterPath, string outPath, string? dataDir)
    {
        // 1. Open every donor overlay, build M in memory, dispose the donors before the write — no handle held at rest.
        var m = new SkyrimMod(outKey, SkyrimRelease.SkyrimSE);
        RemapEngine.MergeResult mr;
        var donorSet = new HashSet<ModKey>(donorsByLoadOrder.Select(d => d.Key));
        var overlays = new List<IDisposable>();
        var lightDonors = new List<string>();                              // donors carrying header flags/fields the output will not
        var masterDonors = new List<string>();
        var headerMetaDonors = new List<string>();
        try
        {
            var mods = new List<(string, ISkyrimModGetter)>(donorsByLoadOrder.Count);
            foreach (var (name, path, _) in donorsByLoadOrder)
            {
                ISkyrimModGetter ov;
                // The strings-aware factory: a LOCALIZED donor opened bare reads every string EMPTY and the merge bakes it in.
                try { ov = LoadOrderResolver.OpenOverlay(path, dataDir); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail($"cannot open donor '{name}' to merge it ({WriteEngine.Describe(ex)}) — nothing written.");
                }
                overlays.Add((IDisposable)ov);
                mods.Add((name, ov));
                // A bare SkyrimMod leaves a donor's HEADER behind, so LIGHT and MASTER are each measured BOTH ways while the overlay is open.
                if (ov.IsSmallMaster || ov.ModKey.Type == ModType.Light) lightDonors.Add(name);
                if (ov.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master) || ov.ModKey.Type == ModType.Master)
                    masterDonors.Add(name);
                if (!string.IsNullOrWhiteSpace(ov.ModHeader.Author) || !string.IsNullOrWhiteSpace(ov.ModHeader.Description))
                    headerMetaDonors.Add(name);
            }
            mr = RemapEngine.MergeModsInto(m, mods, dict);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort */ } } }
        if (!mr.Success) return MergeBuildResult.Fail(mr.Error!);

        // The LIGHT (ESL) flag is carried only when every donor was light AND every renumbered id fits the window; MASTER never is.
        bool lightIdsFit = true;
        foreach (var nk in dict.Values) if (nk.ID < FormIdRange.EslWindowFloor || nk.ID > FormIdRange.EslWindowCeiling) { lightIdsFit = false; break; }
        bool lightCarried = lightDonors.Count == donorsByLoadOrder.Count && lightIdsFit;
        m.IsSmallMaster = lightCarried;

        // 2. Donor-master-survives check: a link still pointing into a donor would keep it as a master of its own merge.
        var dangling = new List<string>();
        int danglingCount = 0;
        foreach (var rec in m.EnumerateMajorRecords())
            foreach (var link in rec.EnumerateFormLinks())
                if (!link.FormKey.IsNull && donorSet.Contains(link.FormKey.ModKey))
                {
                    danglingCount++;
                    if (dangling.Count < 10) dangling.Add($"{FormIdToken.Of(rec.FormKey)} → {FormIdToken.Of(link.FormKey)}");
                }
        if (danglingCount > 0)
            return MergeBuildResult.Fail(
                $"refused — {danglingCount} reference(s) in the merged content still point INTO a donor after the renumber. " +
                "Each targets a FormID its donor never DEFINES (a dangling reference already broken in the source), so it cannot " +
                $"be remapped, and writing it would keep the donor as a master of its own merge. Fix the source (xEdit: check for " +
                $"deleted/injected records) or drop that donor. Samples: {string.Join("; ", dangling)}. Nothing was written.");

        // 3. NextObjectID above the highest merged id, floor-minimum and clamped to the 24-bit object range.
        uint maxUsed = 0;
        foreach (var nk in dict.Values) if (nk.ID > maxUsed) maxUsed = nk.ID;
        m.ModHeader.Stats.NextFormID = Math.Min(FormIdRange.ObjectIdMax, Math.Max(FormIdRange.EslWindowFloor, maxUsed + 1));

        // 4. Resolve the computed master set to overlays — absence or unparseability is a loud refusal — then serialize.
        var masterOverlays = new List<IDisposable>();
        try
        {
            var resolved = new List<ISkyrimModGetter>(masters.Count);
            foreach (var mfn in masters)
            {
                var mp = resolveMasterPath(mfn);
                if (mp is null)
                    return MergeBuildResult.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");
                ISkyrimModGetter mov;
                try { mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(mp)); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail(
                        $"cannot merge: donor master '{mfn}' could not be opened for the serialize ({WriteEngine.Describe(ex)}). Nothing was written.");
                }
                masterOverlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            try { WriteEngine.WriteInPlace(m, resolved, outPath, dataDir); }
            catch (Exception ex)
            {
                return MergeBuildResult.Fail(
                    $"writing the merged plugin failed (serialize or commit; nothing partial left): {WriteEngine.Describe(ex)}.");
            }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // 5. Report the masters the written HEADER carries, since Mutagen lean-derives it; best-effort, else the union.
        IReadOnlyList<string> writtenMasters = masters;
        long bytes = 0;
        try
        {
            bytes = new FileInfo(outPath).Length;
            using var wr = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
            writtenMasters = wr.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
        }
        catch { /* best-effort read-back; the union is a correct superset */ }
        return new MergeBuildResult(true, null, writtenMasters, mr.RecordsCopied, mr.RecordsRenumbered, mr.Conflicts, bytes,
            lightDonors, headerMetaDonors, masterDonors, lightCarried, dict.Count);
    }

    /// <summary>Create BRAND-NEW records (new FormIDs) in a patch — <see cref="Apply"/>'s sibling; contracts in docs/architecture/write-path.md.</summary>
    public static CreateOutcome CreateRecords(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string outPath, bool extend, bool fullReadback = false, string? inPlaceTarget = null,
        bool replaceExisting = false)
    {
        OrderStamp? epoch = null;
        var outcome = CreateRecordsCore(resolver, rulebook, specs, outPath, extend, fullReadback, inPlaceTarget, replaceExisting, ref epoch);
        return epoch is null ? outcome : outcome with { Stamp = epoch };
    }

    /// <summary>The body of <see cref="CreateRecords"/> — split for the same single-point epoch stamp as <see cref="ApplyCore"/>.</summary>
    static CreateOutcome CreateRecordsCore(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string outPath, bool extend, bool fullReadback, string? inPlaceTarget,
        bool replaceExisting, ref OrderStamp? epoch)
    {
        if (specs.Count == 0) return CreateOutcome.Fail("no records to create supplied.");
        bool inPlace = inPlaceTarget is not null;

        // Per-call overlay session, and the view captured up front where the in-place Phase-0 guard needs it.
        using var session = resolver.OpenSession();
        var view = resolver.Capture();
        epoch = view.Stamp;                                               // stamped on every outcome from here down

        // The link-TARGET types this call's edits name, resolved once by the RULEBOOK's own walk with every editorid offered as a sibling.
        var linkTokens = new LinkHarvestSink();
        var allEditorIds = specs.Select(s => s.EditorId).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var harvestRulebook = rulebook.WithLinkHarvest(linkTokens);
        // The harvest walk IS a validate, so an edit that put nothing in the sink is decided and Phase 1 does not re-walk it.
        var harvestVerdicts = new Dictionary<WriteRequest, string?>();
        foreach (var s in specs)
            foreach (var req in s.Edits)
            {
                var sunk = linkTokens.Adds;
                var verdict = harvestRulebook.CollectLinkValues(req, allEditorIds);
                if (linkTokens.Adds == sunk) harvestVerdicts[req] = verdict;
            }
        var (linkTypes, linkNote) = LinkTypeLookup(view, session, linkTokens.Tokens);
        var linkRulebook = rulebook.WithLinkTargets(linkTypes);

        // --- Phase 0: open the destination FIRST, so a FormKey parent can resolve from it; nothing is mutated until Phase 3. ---
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (inPlace)
        {
            // The target must be an active, fully-parseable plugin — the excluded-plugin guard, same as ApplyInPlace.
            if (!view.ContainsPlugin(inPlaceTarget!))
                return CreateOutcome.Fail($"in-place target '{inPlaceTarget}' is not an active plugin in the load order.{view.AbsenceClause(inPlaceTarget!)}");
            if (view.ExcludedPlugins.TryGetValue(inPlaceTarget!, out var excluded))
                return CreateOutcome.Fail(
                    $"cannot create into '{inPlaceTarget}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                    "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"in-place target '{fileName}' not found on disk at {outPath} — the file is untouched.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
            catch (Exception ex)
                { return CreateOutcome.Fail($"cannot open '{fileName}' to create into it in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        }
        else if (extend)
        {
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath)); }
            catch (Exception ex) { return CreateOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return CreateOutcome.Fail($"{(inPlace ? "in-place" : "patch")} ModKey '{patchMod.ModKey.FileName}' must match {(inPlace ? "the target filename" : "output filename")} '{fileName}'.");
        // Every lane that OPENED AN EXISTING FILE captures the author's DECLARED masters first, so Phase 5 can diff for the re-sort note.
        var mastersBefore = inPlace || extend
            ? patchMod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        // --- Phase 1: pre-flight EVERY spec before any mutation; creatability is STRUCTURAL, the FormID unknown until Phase 3. ---
        var problems = new List<string>();
        var seenEdid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredEdidType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // editorid -> RecordType (same-call sibling parents)
        // editorids declared in EARLIER specs plus the current one — the legal targets of a "@editorid" same-call field ref.
        var priorEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentPlans = new List<(IMajorRecordGetter? body, string? sourcePlugin, string? sibling, IMajorRecord? patchParent)?>(specs.Count);
        // Per-spec provenance of the parent override this create hosted the child in, reported because it is invisible after.
        var parentHosts = new string?[specs.Count];
        var parentContested = new bool[specs.Count];
        // The destination's own records, indexed ONCE and lazily, because the carried-parent check runs per parented spec.
        var parentBodies = new Dictionary<(string Plugin, FormKey Key), IMajorRecordGetter?>();
        // Every parent body read out of the load order, gathered a PLUGIN at a time (#757); the memo collapses a shared parent.
        var gather = new BodyGather(view, session);
        IMajorRecordGetter? ParentBodyFrom(string plugin, FormKey fk)
        {
            if (parentBodies.TryGetValue((plugin, fk), out var hit)) return hit;
            return parentBodies[(plugin, fk)] = gather.Body(plugin, fk);
        }
        // The parent's real body in the ORDER, asked when the destination's own copy is an override carrying no children.
        IMajorRecordGetter? OrderBodyOf(FormKey fk)
            => ParentBodyFrom(fk.ModKey.FileName.String, fk)
               ?? (view.ResolveWinner(fk) is { } ow ? ParentBodyFrom(ow.WinnerPlugin, fk) : null);
        // Both destination indexes off ONE walk; the editorid side keeps the WHOLE match set, which is what the upsert judges.
        Dictionary<FormKey, IMajorRecord>? carried = null;
        Dictionary<string, List<IMajorRecord>>? carriedByEdid = null;
        void IndexDestination()
        {
            if (carried is not null) return;
            carried = new Dictionary<FormKey, IMajorRecord>();
            carriedByEdid = new Dictionary<string, List<IMajorRecord>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in patchMod.EnumerateMajorRecords())
            {
                carried.TryAdd(r.FormKey, r);   // first wins, as it did when this was a GroupBy
                if (r.EditorID is not { } edid) continue;
                if (!carriedByEdid.TryGetValue(edid, out var same)) carriedByEdid[edid] = same = new List<IMajorRecord>();
                same.Add(r);
            }
        }
        IMajorRecord? AlreadyCarried(FormKey fk)
        {
            IndexDestination();
            return carried!.TryGetValue(fk, out var rec) ? rec : null;
        }
        IReadOnlyList<IMajorRecord> CarriedUnder(string editorId)
        {
            IndexDestination();
            return carriedByEdid!.TryGetValue(editorId, out var recs) ? recs : Array.Empty<IMajorRecord>();
        }
        // A replace the upsert would honour AND that takes nothing down with it, since the replace arm drops the record's CHILD GROUP.
        bool ReplaceKeepsEverything(string wantType, IReadOnlyList<IMajorRecord> clash)
            => WriteEngine.UpsertWouldReplace(patchMod, wantType, clash) && WriteEngine.ChildCountOf(clash[0]) == 0;

        // An overwrite is offered ONLY where the upsert honours it and nothing is lost; elsewhere replace= would not help.
        string ClashReason(string wantType, IReadOnlyList<IMajorRecord> clash)
        {
            if (clash.FirstOrDefault(r => r.FormKey.ModKey != patchMod.ModKey) is { } foreign)
                return $"{fileName} carries an override of {FormIdToken.Of(foreign.FormKey)} under that editorid — re-creating over an "
                     + $"override would blank the original plugin's record. Edit it with {ToolNames.Apply}, or pick another editorid.";
            if (clash.Count > 1)
                return $"{fileName} defines {clash.Count} records with that editorid "
                     + $"({string.Join(", ", clash.Select(c => c.FormKey.ID.ToString("X6")))}) — external references may point at "
                     + $"either copy, so which survives is your call: remove the extra(s) with {ToolNames.Remove}, then re-run.";
            var one = clash[0];
            var itsType = RecordNaming.StripOverlay(one.GetType().Name);
            if (!WriteEngine.UpsertWouldReplace(patchMod, wantType, clash))
                return string.Equals(itsType, wantType, StringComparison.OrdinalIgnoreCase)
                    // Same type and still not replaceable: the cell route files by coordinates, so there is no overwrite to offer.
                    ? $"{fileName} already defines {itsType} {one.FormKey.ID:X6} with that editorid, and a create never "
                      + $"overwrites a {itsType}. Edit it with {ToolNames.Apply}, or pick another editorid."
                    : $"{fileName} already defines {itsType} {one.FormKey.ID:X6} with that editorid — an editorid collision "
                      + $"across record types, which no overwrite resolves: a {itsType} cannot be re-created as a {wantType}. Pick another editorid.";
            var kids = WriteEngine.ChildCountOf(one);
            if (kids > 0)
                return $"{fileName} already defines {itsType} {one.FormKey.ID:X6} with that editorid, and {kids} record(s) "
                     + $"live under it ({string.Join(", ", WriteEngine.ChildNamesOf(one, 5))}{(kids > 5 ? ", …" : "")}) — re-creating "
                     + $"it drops the record and its children together, and no overwrite puts them back. Edit it with "
                     + $"{ToolNames.Apply}, or pick another editorid.";
            return $"{fileName} already defines {itsType} {one.FormKey.ID:X6} with that editorid. "
                 + "Pass replace=true to overwrite it, or pick another editorid.";
        }
        // Declare every parent body the loop will read, then gather one walk per DEFINER plugin (#757), under the loop's own guards.
        var wantedEdids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in specs)
        {
            if (string.IsNullOrWhiteSpace(s.EditorId) || !wantedEdids.Add(s.EditorId)) continue;
            if (s.ParentRef is null || !FormKey.TryFactory(s.ParentRef, out var wantFk)) continue;
            if (AlreadyCarried(wantFk) is not null) continue;
            if (view.ResolveWinner(wantFk) is null) continue;
            var wantDefiner = wantFk.ModKey.FileName.String;
            if (view.ContainsPlugin(wantDefiner)) gather.Want(wantDefiner, wantFk);
        }
        gather.Gather();
        var cellKinds = new CellCreate[specs.Count];   // cell-create routing per spec (None / Exterior / Interior)
        var singularClaims = new HashSet<(string Parent, string Slot)>();   // one create per singular slot per call
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            parentPlans.Add(null);   // flat default; overwritten on the nested path
            if (string.IsNullOrWhiteSpace(s.EditorId)) { problems.Add($"{s.RecordType}: an editorid is required to create a record (it's how the record is referenced)."); continue; }
            if (!seenEdid.Add(s.EditorId)) { problems.Add($"editorid '{s.EditorId}' is used by more than one record in this call — each created record needs a distinct editorid."); continue; }
            declaredEdidType[s.EditorId] = s.RecordType;
            priorEditorIds.Add(s.EditorId);   // declared ⇒ referenceable by its own edits (self) and by LATER specs

            if (s.ParentRef is null)
            {
                if (IsCellType(s.RecordType))
                {
                    // A parentless Cell with no grid is an INTERIOR cell, self-filed by FormID; a grid here is malformed.
                    if (s.Grid is not null) { problems.Add($"Cell '{s.EditorId}': an exterior cell (grid=) needs parent= a Worldspace; an interior cell takes no parent and no grid."); continue; }
                    cellKinds[i] = CellCreate.Interior;
                }
                else if (!WriteEngine.CanCreateType(s.RecordType, out var why)) { problems.Add($"{s.RecordType} '{s.EditorId}': {why}"); continue; }
                // In place, a flat create over an editorid the target already defines discards the rest of that record, so it refuses.
                if (inPlace && CarriedUnder(s.EditorId) is { Count: > 0 } clash
                    && !(replaceExisting && ReplaceKeepsEverything(s.RecordType, clash)))
                {
                    problems.Add($"{s.RecordType} '{s.EditorId}': " + ClashReason(s.RecordType, clash));
                    continue;
                }
            }
            else
            {
                Type? parentType = null;
                if (FormKey.TryFactory(s.ParentRef, out var parentFk))
                {
                    // IN-PLACE target-owned parent: use the TARGET's OWN copy, so the user's parent content is not clobbered.
                    if (inPlace && AlreadyCarried(parentFk) is { } ownParent)
                    {
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(ownParent.GetType().Name));
                        parentPlans[i] = (null, null, null, ownParent);
                        parentHosts[i] = $"{RecordNaming.StripOverlay(ownParent.GetType().Name)} {FormIdToken.Of(parentFk)} is the target plugin's OWN record — it hosts the child directly (nothing copied in, no master added)";
                    }
                    else if (AlreadyCarried(parentFk) is { } already)
                    {
                        // The DESTINATION already carries this parent, so use that record; ordered BEFORE the load-order branch, which would lie.
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(already.GetType().Name));
                        parentPlans[i] = (null, null, null, already);
                        parentHosts[i] = $"{RecordNaming.StripOverlay(already.GetType().Name)} {FormIdToken.Of(parentFk)} was already carried by this artifact — its existing record hosts the child (nothing copied in)";
                    }
                    else if (view.ResolveWinner(parentFk) is { } w)
                    {
                        // An EXISTING load-order parent, overridden in as the DEFINING plugin's version; contract in docs/architecture/write-path.md.
                        var definer = parentFk.ModKey.FileName.String;
                        // GetRecord answers null for a plugin the order lacks and for an EXCLUDED one alike, so both fall to the winner below.
                        var fromDefiner = ParentBodyFrom(definer, parentFk);
                        var parentBody = fromDefiner ?? ParentBodyFrom(w.WinnerPlugin, parentFk);
                        if (parentBody is null) { problems.Add($"{s.RecordType} '{s.EditorId}': parent {FormIdToken.Of(parentFk)} winner '{w.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                        var readFrom = fromDefiner is not null ? definer : w.WinnerPlugin;
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(parentBody.GetType().Name));
                        parentPlans[i] = (parentBody, readFrom, null, null);
                        bool overWinner = fromDefiner is not null && !string.Equals(readFrom, w.WinnerPlugin, StringComparison.OrdinalIgnoreCase);
                        parentContested[i] = overWinner;
                        parentHosts[i] = $"{RecordNaming.StripOverlay(parentBody.GetType().Name)} {FormIdToken.Of(parentFk)} hosted from '{readFrom}'"
                            + (fromDefiner is null
                                ? $" (the load-order WINNER — its defining plugin '{definer}' does not carry it: an injected or excluded parent)"
                                : !overWinner
                                    ? " (its DEFINING plugin, which is also the load-order winner)"
                                    // The LEAN host is the default and the sort is the caller's control, so the choice is REPORTED as a statement.
                                    : $" (its DEFINING plugin — the LEAN host, carrying the residual only: '{w.WinnerPlugin}' currently WINS this record "
                                      + $"and its version is deliberately NOT inlined here, so wherever this artifact out-ranks '{w.WinnerPlugin}' the parent "
                                      + $"record resolves to '{readFrom}'s fields. That is the control this lane gives you: sort below '{w.WinnerPlugin}' to "
                                      + $"keep the residual shape, sort above it to assert this host, or inline '{w.WinnerPlugin}'s content deliberately: "
                                      + $"forward '{w.WinnerPlugin}'s version of the parent into a patch and create into THAT patch — the child hosts in the "
                                      + "forwarded body. Either order works: forwarding onto a parent a patch already holds keeps the children under it. "
                                      + "Whichever way you sort, the new child is carried.)");
                    }
                    else
                    {
                        // Genuinely absent from the order AND the destination — a loud refusal naming the one-call workaround.
                        problems.Add($"{s.RecordType} '{s.EditorId}': parent {FormIdToken.Of(parentFk)} is not present in the load order"
                            + (extend ? " or this patch" : "") + (inPlace ? " or the target plugin" : "") + " — name an existing parent, or create the parent and this "
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
                // A Cell under a parent has TWO routes: grid= files it by coordinate, collection= names a slot. Only grid skips the resolver.
                if (IsCellType(s.RecordType) && s.Grid is not null)
                {
                    if (s.IntoCollection is not null) { problems.Add($"Cell '{s.EditorId}': grid= and collection= are the two different routes a cell goes under a parent — name one, not both."); continue; }
                    if (parentType != typeof(Worldspace)) { problems.Add($"Cell '{s.EditorId}': an exterior cell nests under a Worldspace, but parent '{s.ParentRef}' resolved to a {parentType.Name}."); continue; }
                    if (!TryParseGrid(s.Grid, out _, out _)) { problems.Add($"Cell '{s.EditorId}': grid '{s.Grid}' must be two integers \"X,Y\" (e.g. \"5,-12\")."); continue; }
                    cellKinds[i] = CellCreate.Exterior;
                }
                else if (!WriteEngine.TryResolveChildSlot(s.RecordType, parentType, s.IntoCollection, out var slotName, out var slotShape, out var nestedWhy))
                { problems.Add($"{s.RecordType} '{s.EditorId}': {nestedWhy}"); continue; }
                // A SINGULAR slot holds exactly one child, and occupancy is knowable HERE, before a FormKey is allocated.
                else if (slotShape == OwnedChildShape.Singular)
                {
                    // BOTH copies answer, because the destination's override carries none of the parent's children.
                    var plan = parentPlans[i]!.Value;
                    IMajorRecordGetter? Occupant(IMajorRecordGetter? b)
                        => b is null ? null : OwnedChildLifecycle.OccupantOf(b, slotName!);
                    var inOrder = plan.body
                        ?? (plan.patchParent is not null && FormKey.TryFactory(s.ParentRef!, out var orderFk) ? OrderBodyOf(orderFk) : null);
                    if ((Occupant(plan.patchParent) ?? Occupant(inOrder)) is { } occupant)
                    {
                        problems.Add($"{s.RecordType} '{s.EditorId}': '{parentType.Name}.{slotName}' already holds a {s.RecordType} ({FormIdToken.Of(occupant.FormKey)}"
                            + (occupant.EditorID is { } oe ? $" editorid={oe}" : "") + ") and it holds exactly one, so there is no room to create another. "
                            + "Edit the one that is there by its own FormID, or remove it first and create again.");
                        continue;
                    }
                    // Two specs claiming the same singular slot in one call is the same collision, one call earlier.
                    if (!singularClaims.Add((s.ParentRef!, slotName!)))
                    { problems.Add($"{s.RecordType} '{s.EditorId}': two records in this call are created into '{parentType.Name}.{slotName}' on parent '{s.ParentRef}', which holds exactly one."); continue; }
                }
            }

            foreach (var req in s.Edits)
            {
                // A "@editorid" value is accepted iff that editorid was declared no later than this spec, else rejected loud.
                var reject = harvestVerdicts.TryGetValue(req, out var settled) ? settled : linkRulebook.Validate(req, priorEditorIds);
                if (reject is not null) problems.Add($"{s.RecordType} '{s.EditorId}' [{Label(req)}]: {reject}");
            }

            // DLBR Flags (DNAM) has no honest default, so a branch that passes none is refused with the other pre-flight refusals.
            if (string.Equals(s.RecordType, nameof(DialogBranch), StringComparison.OrdinalIgnoreCase))
            {
                bool authorSetFlags = s.Edits.Any(e => e.Path.Length >= 1 &&
                    string.Equals(e.Path[0], nameof(DialogBranch.Flags), StringComparison.OrdinalIgnoreCase));
                if (DialogueCkParity.BranchFlagsRefusal(authorSetFlags, s.EditorId) is { } flagsRefusal)
                    problems.Add(flagsRefusal);
            }
        }
        if (problems.Count > 0)
            return CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) creating {specs.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));

        // --- Phase 3: UPSERT each record, then apply its edits; the upsert is what makes into= idempotent. ---
        var created = new List<CreatedRecord>(specs.Count);
        // Positional with each created record's Ops: the WriteRequest behind the op, or null for a CK-parity fill.
        var opRequests = new List<List<WriteRequest?>>(specs.Count);
        var createdByEditorId = new Dictionary<string, IMajorRecord>(StringComparer.OrdinalIgnoreCase);
        var linkCacheByPlugin = new Dictionary<string, Mutagen.Bethesda.Plugins.Cache.ILinkCache>(StringComparer.OrdinalIgnoreCase);

        // Resolve a spec's parent to a SETTABLE record IN the patch; (null, error) fails the WHOLE call.
        (IMajorRecord? parent, string? error) MakeSettableParent(int idx)
        {
            var plan = parentPlans[idx]!.Value;
            if (plan.patchParent is not null) return (plan.patchParent, null);   // a prior-into= patch-local record
            if (plan.body is not null)
            {
                Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache = null;
                if (WriteEngine.RecordNeedsSourceCache(plan.body))
                {
                    if (!linkCacheByPlugin.TryGetValue(plan.sourcePlugin!, out cache))
                        linkCacheByPlugin[plan.sourcePlugin!] = (cache = session.LinkCacheFor(plan.sourcePlugin!))!;
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
                    // INTERIOR cell: self-files into the patch's Cells group by FormID digits.
                    rec = WriteEngine.AddInteriorCell(patchMod, s.EditorId);
                }
                else if (cellKinds[i] == CellCreate.Exterior)
                {
                    // EXTERIOR cell: make the Worldspace settable, then place the cell into its block tree by grid.
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
            var reqs = new List<WriteRequest?>(s.Edits.Count);
            foreach (var rawReq in s.Edits)
            {
                // Resolve every same-call @editorid to its now-allocated FormKey; a miss here is a real engine inconsistency.
                var (req, refErr) = ResolveSiblingRefs(rawReq, createdByEditorId, $"new {s.RecordType} '{s.EditorId}'");
                if (refErr is not null) return CreateOutcome.Fail(refErr);
                try
                {
                    var applyNote = WriteEngine.ApplyVerb(rec, req);
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, Label(req), true, null, TryReadAfter(rec, req)) { ApplyNote = applyNote });
                    reqs.Add(req);
                }
                catch (ExpectedApplyRejectionException ex)
                {
                    // EXPECTED apply-time refusal: clean guidance, NOT the inconsistency wrapper, with the whole call still refused.
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({FormIdToken.Of(rec.FormKey)}) — {ex.Message} (nothing created)");
                }
                catch (MalformedTargetDataException ex)
                {
                    // The target record's own data is malformed — rendered accurately, NOT under the inconsistency wrapper.
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({FormIdToken.Of(rec.FormKey)}) — {ex.Message} (nothing created)");
                }
                catch (Exception ex)
                {
                    return CreateOutcome.Fail(
                        $"engine error applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({FormIdToken.Of(rec.FormKey)}): " +
                        $"pre-flight ACCEPTED it but the apply threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
                }
            }
            // Auto-fill the DialogTopic SNAM marker, because a blank one is a load CTD; never overriding an explicit marker.
            if (rec is IDialogTopic dtopic)
            {
                switch (DialogueSubtype.NormalizeMarker(dtopic, out var marker))
                {
                    case MarkerFill.Filled:
                        // AfterIsNote: a sentence about what the write did, not a field reading, so nothing re-reads it.
                        ops.Add(new OpResult(rec.FormKey, s.RecordType,
                            $"SubtypeName (SNAM subtype marker) auto-set to {marker}", true, null,
                            $"{marker} — derived from Subtype={dtopic.Subtype}; a new topic with a blank marker is a load CTD (#131)")
                            { AfterIsNote = true });
                        reqs.Add(null);
                        break;
                    case MarkerFill.Unmodeled:
                        // Fail loud, never ship a silent blank: a Subtype outside the modeled range has no derivable marker.
                        return CreateOutcome.Fail(
                            $"cannot create DialogTopic '{s.EditorId}': no SNAM subtype marker is modeled for Subtype={dtopic.Subtype} " +
                            $"((int){(int)dtopic.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). A blank marker is malformed " +
                            "(a new topic with a blank marker is a load CTD, #131). Use a valid Subtype, or set SubtypeName explicitly to the correct 4-char marker (nothing created).");
                    // AlreadySet: an explicit marker the author set — never overridden, nothing to report.
                }

                // CK-parity seed: Priority is non-nullable, so "unset" is "no edit touched the path"; an explicit 0 still wins.
                bool authorSetPriority = s.Edits.Any(e => e.Path.Length >= 1 &&
                    string.Equals(e.Path[0], "Priority", StringComparison.OrdinalIgnoreCase));
                if (DialogueCkParity.ApplyTopicPriorityDefault(dtopic, authorSetPriority) is { } pfill)
                    { ops.Add(Fill(rec.FormKey, s.RecordType, pfill)); reqs.Add(null); }
            }
            // CK-parity default-populate across the DIAL/INFO/DLVW/DLBR/QUST family, because Mutagen omits optionals the CK writes.
            else if (rec is IDialogResponses infoRec)
            {
                foreach (var fill in DialogueCkParity.ApplyInfoDefaults(infoRec))
                    { ops.Add(Fill(rec.FormKey, s.RecordType, fill)); reqs.Add(null); }
            }
            else if (rec is IDialogView viewRec)
            {
                foreach (var fill in DialogueCkParity.ApplyViewDefaults(viewRec))
                    { ops.Add(Fill(rec.FormKey, s.RecordType, fill)); reqs.Add(null); }
            }
            else if (rec is IDialogBranch branchRec)   // DLBR Category (TNAM); Flags (DNAM) is required, refused in Phase 1 when unset
            {
                foreach (var fill in DialogueCkParity.ApplyBranchDefaults(branchRec))
                    { ops.Add(Fill(rec.FormKey, s.RecordType, fill)); reqs.Add(null); }
            }
            else if (rec is IQuest questRec)           // QUST NextAliasID (ANAM) + objective Flags (FNAM)
            {
                foreach (var fill in DialogueCkParity.ApplyQuestDefaults(questRec))
                    { ops.Add(Fill(rec.FormKey, s.RecordType, fill)); reqs.Add(null); }
            }
            opRequests.Add(reqs);
            // The parent override this create dragged in; a same-call SIBLING parent is verified as a created record instead.
            var parentKey = parentPlans[i] is { } plan2
                ? plan2.body?.FormKey ?? plan2.patchParent?.FormKey : null;
            created.Add(new CreatedRecord(rec.FormKey, s.RecordType, s.EditorId, ops, replaced)
            { ParentHost = parentHosts[i], ParentContested = parentContested[i], ParentKey = parentKey });
        }

        // A nested create overrides its PARENT into the artifact, which forks it like any other override.
        var forkWarning = ForkWarning.For(
            view, parentPlans.Where(p => p?.body is not null).Select(p => p!.Value.body!.FormKey), fileName);

        // --- Phase 4: serialize ONCE with the full known-master set, behind the two-part active-patch self-lock. ---
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try
        {
            if (inPlace)
                // Re-emit the WHOLE target over itself, counter preserved and no baseline force-include, against the same whole-master set.
                WriteEngine.WriteInPlace(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath, resolver.DataDir);
            else
                WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath);
        }
        catch (Exception ex) { return CreateOutcome.Fail(SerializeFailure($"writing {(inPlace ? $"'{fileName}' in place" : "the patch")} after create failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        // --- Phase 5: re-open, report the derived master header and bytes, and on request each record's read-back. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            // The strings-aware factory: a localized plugin opened bare would report a Name this call just wrote as missing.
            back = LoadOrderResolver.OpenOverlay(outPath, resolver.DataDir);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, created.Select(c => c.FormKey));
            // The file reading every response's per-field line prints; its own try, so a fault leaves the records unchecked.
            try { created = VerifyCreatedAgainstFile(back, created, opRequests); }
            catch { /* every record keeps VerifyAttempted=false — the render says not-checked */ }
        }
        catch (Exception ex) { return CreateOutcome.Fail($"records created + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new CreateOutcome(true, null, outPath, extend, created, masters, bytes)
        {
            ReadBack = readBack, InPlace = inPlace, Warning = forkWarning,
            Note = JoinNotes(linkNote, mastersBefore is null ? null : MasterGrowNote(fileName, mastersBefore, masters)),
        };
    }

    /// <summary>Create BRAND-NEW records IN PLACE — the in-place ENTRY POINT into the shared <see cref="CreateRecords"/> core.</summary>
    public static CreateOutcome CreateRecordsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string targetPath, string targetName, bool fullReadback = true,
        bool replaceExisting = false)
        => CreateRecords(resolver, rulebook, specs, targetPath, extend: false, fullReadback, inPlaceTarget: targetName,
                         replaceExisting: replaceExisting);

    /// <summary>Report every CREATED record from the written file, off ONE walk, with the per-record verdicts no op can carry.</summary>
    static List<CreatedRecord> VerifyCreatedAgainstFile(
        ISkyrimModGetter back, IReadOnlyList<CreatedRecord> created, IReadOnlyList<List<WriteRequest?>> opRequests)
    {
        var walk = WalkWrittenFileFor(back, created.Select(c => c.FormKey)
            .Concat(created.Where(c => c.ParentKey is not null).Select(c => c.ParentKey!.Value)));

        var pairs = new List<(FormKey Target, WriteRequest? Req)>();
        var flat = new List<OpResult>();
        for (int i = 0; i < created.Count; i++)
        {
            var reqs = i < opRequests.Count ? opRequests[i] : null;
            for (int k = 0; k < created[i].Ops.Count; k++)
            {
                flat.Add(created[i].Ops[k]);
                pairs.Add((created[i].FormKey, reqs is not null && k < reqs.Count ? reqs[k] : null));
            }
        }
        var verified = VerifyAgainstWalk(walk, pairs, flat);

        var reported = new List<CreatedRecord>(created.Count);
        int at = 0;
        foreach (var c in created)
        {
            var slice = new List<OpResult>(c.Ops.Count);
            for (int k = 0; k < c.Ops.Count; k++) slice.Add(verified[at + k]);
            at += c.Ops.Count;
            reported.Add(c with
            {
                Ops = slice,
                // REACHED, not merely "the walk finished": a walk that threw part way still yielded the records it got to.
                VerifyAttempted = walk.Found.ContainsKey(c.FormKey) || walk.Finished,
                // A walk that FAILED answers about nothing: not reached is then not checked, never absent.
                AbsentFromFile = walk.Finished && !walk.Found.ContainsKey(c.FormKey),
                ParentAbsentFromFile = walk.Finished && c.ParentKey is { } pk && !walk.Found.ContainsKey(pk),
            });
        }
        return reported;
    }

    /// <summary>A CK-parity fill as an op: its reading is a SENTENCE about what the write did, so nothing re-reads it.</summary>
    static OpResult Fill(FormKey key, string recordType, CkParityFill fill) =>
        new(key, recordType, fill.Label, true, null, fill.Reason) { AfterIsNote = true };

    /// <summary>Read each just-written record IN FULL off the re-opened file in ONE pass; NEVER throws, the write already succeeded.</summary>
    static IReadOnlyList<FullReadback> ReadBackInFull(ISkyrimModGetter back, IEnumerable<FormKey> targets, bool inMemory = false)
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
                    ? (inMemory
                        ? $"{walkError} — this was a DRY RUN read of the in-memory would-be content; nothing was written."
                        : $"{walkError} — the WRITE ITSELF SUCCEEDED (the patch was serialized and re-opened); inspect the patch in xEdit; do not re-issue the ops.")
                    : (inMemory
                        ? $"the in-memory would-be content did not yield {FormIdToken.Of(fk)} — a real inconsistency, surfaced not swallowed (Q3); nothing was written."
                        : $"the written file did not yield {FormIdToken.Of(fk)} on re-open — a real inconsistency, surfaced not swallowed (Q3); inspect the patch in xEdit.")));
        return result;
    }

    /// <summary>The xEdit-style edit label: <c>Verb path[key] = value</c>.</summary>
    static string Label(WriteRequest r) =>
        $"{r.Verb} {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")}{(r.Value is not null ? " = " + r.Value : "")}";

    /// <summary>Resolve every same-call <c>@editorid</c> in a request to the referenced record's allocated FormKey, recursively.</summary>
    static (WriteRequest req, string? error) ResolveSiblingRefs(
        WriteRequest r, IReadOnlyDictionary<string, IMajorRecord> created, string onWhat)
    {
        string? err = null;
        string? One(string? v)
        {
            if (err is not null || !WriteEngine.IsSameCallSiblingRef(v, out var ed)) return v;
            if (created.TryGetValue(ed, out var rec)) return rec.FormKey.ToString();
            err = $"internal: same-call reference '@{ed}' on {onWhat} resolved to no record created in this call — " +
                  "pre-flight should have caught it; surfaced, not swallowed (Q3).";
            return v;
        }
        var value = One(r.Value);
        var values = r.Values;
        if (values is not null && Array.Exists(values, v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            values = Array.ConvertAll(values, v => One(v)!);
        var strct = r.Struct;
        if (strct is not null)
        {
            var (rs, sErr) = ResolveStructSiblingRefs(strct, created, onWhat);
            err ??= sErr;
            strct = rs;
        }
        // A composes= op carries a LIST of specs, each of which may @editorid-reference a same-call sibling.
        var structs = r.Structs;
        if (structs is not null)
        {
            List<StructSpec>? repl = null;
            for (int i = 0; i < structs.Count; i++)
            {
                var (rs2, e2) = ResolveStructSiblingRefs(structs[i], created, onWhat);
                err ??= e2;
                if (repl is null && !ReferenceEquals(rs2, structs[i])) repl = new List<StructSpec>(structs);
                if (repl is not null) repl[i] = rs2;
            }
            if (repl is not null) structs = repl;
        }
        if (err is not null) return (r, err);
        if (ReferenceEquals(value, r.Value) && ReferenceEquals(values, r.Values) && ReferenceEquals(strct, r.Struct)
            && ReferenceEquals(structs, r.Structs))
            return (r, null);
        return (new WriteRequest
        {
            RecordType = r.RecordType, Path = r.Path, Verb = r.Verb, Key = r.Key,
            Value = value, Values = values, Entries = r.Entries, Struct = strct, Structs = structs,
        }, null);
    }

    /// <summary>The <see cref="StructSpec"/> half of <see cref="ResolveSiblingRefs"/>, on the same contract.</summary>
    static (StructSpec spec, string? error) ResolveStructSiblingRefs(
        StructSpec sp, IReadOnlyDictionary<string, IMajorRecord> created, string onWhat)
    {
        var fields = sp.Fields;
        if (fields is not null && fields.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
        {
            var nf = new Dictionary<string, string>(fields.Count);
            foreach (var kv in fields)
            {
                if (WriteEngine.IsSameCallSiblingRef(kv.Value, out var ed))
                {
                    if (!created.TryGetValue(ed, out var rec))
                        return (sp, $"internal: same-call reference '@{ed}' on {onWhat} resolved to no record created " +
                                    "in this call — pre-flight should have caught it; surfaced, not swallowed (Q3).");
                    nf[kv.Key] = rec.FormKey.ToString();
                }
                else nf[kv.Key] = kv.Value;
            }
            fields = nf;
        }
        var sets = sp.Sets;
        if (sets is not null)
        {
            List<WriteRequest>? ns = null;
            for (int i = 0; i < sets.Count; i++)
            {
                var (rr, e) = ResolveSiblingRefs(sets[i], created, onWhat);
                if (e is not null) return (sp, e);
                if (ns is null && !ReferenceEquals(rr, sets[i])) ns = new List<WriteRequest>(sets);
                if (ns is not null) ns[i] = rr;
            }
            if (ns is not null) sets = ns;
        }
        if (ReferenceEquals(fields, sp.Fields) && ReferenceEquals(sets, sp.Sets)) return (sp, null);
        return (new StructSpec { Type = sp.Type, Fields = fields, CtorArgs = sp.CtorArgs, Sets = sets }, null);
    }

    /// <summary>Best-effort read-back of the edited leaf PATH off the override; null on any difficulty, never throws.</summary>
    static string? TryReadAfter(IMajorRecord ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf }, containerHint: null);   // a write confirmation has no depth= knob — the count IS the read-back
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            return f is null ? null : (f.HasValue ? f.Token : f.Note);
        }
        catch { return null; }
    }

    /// <summary>Re-derive every op's "what landed" off the RE-OPENED WRITTEN FILE and compare; the compare's detection
    /// bound and the superseded-op rule are in docs/architecture/write-path.md, which links json-wire.md for the rest.</summary>
    internal static IReadOnlyList<OpResult> VerifyLandedAgainstFile(   // internal: pinned by a test
        ISkyrimModGetter back, IReadOnlyList<(FormKey Target, WriteRequest? Req)> perOp, IReadOnlyList<OpResult> ops)
    {
        if (ops.Count == 0) return ops;
        var walk = WalkWrittenFileFor(back, perOp.Select(p => p.Target));
        return VerifyAgainstWalk(walk, perOp, ops);
    }

    /// <summary>The written file walked ONCE for a set of records: what it yielded, and whether the walk finished.</summary>
    internal record struct WrittenFileWalk(Dictionary<FormKey, IMajorRecordGetter> Found, bool Finished);   // internal: the create lane reads both halves

    /// <summary>Walk the re-opened file once and collect the wanted records; only a FINISHED walk can say a record is absent.</summary>
    internal static WrittenFileWalk WalkWrittenFileFor(ISkyrimModGetter back, IEnumerable<FormKey> targets)
    {
        var want = new HashSet<FormKey>(targets);
        var found = new Dictionary<FormKey, IMajorRecordGetter>();
        bool walkFinished = false;
        try
        {
            foreach (var rec in back.EnumerateMajorRecords())
            {
                if (want.Contains(rec.FormKey) && !found.ContainsKey(rec.FormKey)) found[rec.FormKey] = rec;
                if (found.Count == want.Count) break;    // every target in hand — the rest of the file is not ours
            }
            walkFinished = true;
        }
        catch { /* leave every op unverified — the render says so, and ReadBackInFull names the walk failure itself */ }
        return new WrittenFileWalk(found, walkFinished);
    }

    /// <summary>Judge each op against a walk already done; a NULL request is an op with no leaf and is left alone.</summary>
    internal static IReadOnlyList<OpResult> VerifyAgainstWalk(   // internal: the create lane calls it after one shared walk
        WrittenFileWalk walk, IReadOnlyList<(FormKey Target, WriteRequest? Req)> perOp, IReadOnlyList<OpResult> ops)
    {
        if (ops.Count == 0) return ops;
        var (found, walkFinished) = (walk.Found, walk.Finished);
        var verified = new List<OpResult>(ops.Count);
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (i >= perOp.Count) { verified.Add(op); continue; }                       // appended past the edits — never asked
            // No request behind this op: a CK-parity fill, whose reading is a sentence and has no leaf to re-read.
            if (perOp[i].Req is not { } askedReq) { verified.Add(op); continue; }
            // The record is not in the file this call just wrote: a completed walk makes that a VERDICT, a failed walk no answer.
            if (!found.TryGetValue(perOp[i].Target, out var rec))
                { verified.Add(op with { VerifyAttempted = true, RecordAbsentFromFile = walkFinished }); continue; }
            // SUPERSEDED ops are not comparable, so only the LAST op touching a leaf is answerable by the file.
            if (LaterOpTouchesSameLeaf(perOp, i))
            {
                var (finalAfter, _, finalReadable, finalBytes) = DescribeApplied(rec, askedReq);
                verified.Add(op with
                {
                    SupersededInCall = true, VerifyAttempted = true,
                    AfterOnDisk = finalReadable ? finalAfter : null,
                    AfterOnDiskBytes = finalReadable ? finalBytes : null,
                });
                continue;
            }
            var (afterDisk, landedDisk, diskReadable, diskBytes) = DescribeApplied(rec, askedReq);
            // ONE comparison, on the leaf: a second pass over Landed would be inert, and making it live compares element TEXT.
            verified.Add(op with
            {
                // A file side that could NOT BE READ is not the file's answer, so its note is not presented as the file's content.
                LandedOnDisk = diskReadable ? landedDisk : null,
                // The leaf reading travels with it: a null there is what makes the per-edit line say not-checked.
                AfterOnDisk = diskReadable ? afterDisk : null,
                AfterOnDiskBytes = diskReadable ? diskBytes : null,
                VerifyAttempted = true,
            });
        }
        return verified;
    }

    /// <summary>Does a LATER op write into the same leaf family as op <paramref name="i"/>? Containment, with suffixes stripped.</summary>
    static bool LaterOpTouchesSameLeaf(IReadOnlyList<(FormKey Target, WriteRequest? Req)> perOp, int i)
    {
        if (perOp[i].Req is not { } here) return false;   // no leaf of its own — nothing can supersede it
        for (int j = i + 1; j < perOp.Count; j++)
        {
            if (perOp[j].Req is not { } later) continue;  // a fill writes no path a request named; it supersedes nothing
            if (perOp[j].Target != perOp[i].Target || !PathFamiliesOverlap(here.Path, later.Path)) continue;
            // A key-addressed pair on ONE path can be two ELEMENTS, but only where neither op can move the container's COUNT.
            if (here.Key is { } a && later.Key is { } b
                && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                && CountNeutralKeyedVerb(here.Verb) && CountNeutralKeyedVerb(later.Verb)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Does this KEY-addressed verb leave the container's element count alone? Only <c>SetAtIndex</c>.</summary>
    static bool CountNeutralKeyedVerb(string verb) => string.Equals(verb, "SetAtIndex", StringComparison.Ordinal);

    /// <summary>Is one dotted path a prefix of the other, with any <c>[index]</c>/<c>[key]</c> suffix stripped? Equal paths count.</summary>
    static bool PathFamiliesOverlap(string[] a, string[] b)
    {
        if (a.Length == 0 || b.Length == 0) return false;   // no leaf to share; a 0-segment path would otherwise
                                                           // "overlap" everything and silently drop the verify
        int n = Math.Min(a.Length, b.Length);
        for (int k = 0; k < n; k++)
            if (!SameSegment(a[k], b[k])) return false;
        return true;

        // Two segments that BOTH carry an index/key compare whole, so Ranks[0] and Ranks[1] stay independent elements.
        static bool SameSegment(string x, string y)
        {
            int bx = x.IndexOf('['), by = y.IndexOf('[');
            return bx >= 0 && by >= 0
                ? string.Equals(x, y, StringComparison.OrdinalIgnoreCase)
                : string.Equals(Bare(x), Bare(y), StringComparison.OrdinalIgnoreCase);
            static string Bare(string s) { int b = s.IndexOf('['); return b < 0 ? s : s[..b]; }
        }
    }

    static (string? After, string? Landed, bool Readable, int? Bytes) DescribeApplied(IMajorRecordGetter ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf }, containerHint: null);   // same: no depth= on the write surface, don't hint it
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            if (f is null) return (null, null, false, null);
            var after = f.HasValue ? f.Token : f.Note;
            // Scalar: Landed reuses the token read. List/dict: the touched element plus the new count, an Add naming how many.
            int added = req.Verb == "Add" ? (req.Structs?.Count ?? 1) : 1;
            var landed = f.HasValue ? f.Token : (ReadEngine.TouchedElement(ov, req.Path, req.Verb, req.Key, added) ?? f.Note);
            // The presence PAIR rides along as the structural fact the tokens hide, and the blob's byte length with its caveat.
            return (after, landed, f.Readable, f.Bytes);
        }
        catch { return (null, null, false, null); }
    }
}

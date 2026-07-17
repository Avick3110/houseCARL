using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Exceptions;
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
        public IReadOnlyList<StructSpec>? Structs { get; init; } // P8a batch struct-list ops (composes=): Add appends each, ReplaceAll clears+appends each.
        public string? FromPlugin { get; init; } // P8b verb=CopyFrom: the plugin whose version of Target to deep-copy the field FROM (active, or off-order on disk).
    }

    /// <summary>Per-edit result. On a successful call every op has <see cref="Applied"/>=true (all-or-nothing);
    /// <see cref="After"/> is a best-effort read-back of the edited leaf (xEdit remains the authority).
    /// <see cref="Landed"/> is the compact "what landed" descriptor the in-place verify renders by default
    /// (HCBR-2026-06-28-01) — the new scalar value, or the touched list element + new count; null when not derivable.</summary>
    public sealed record OpResult(FormKey Target, string RecordType, string Label, bool Applied, string? Error, string? After, string? Landed = null);

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

        /// <summary>True ⇒ this outcome came from the IN-PLACE lane (<see cref="Apply"/>'s sibling
        /// <see cref="ApplyInPlace"/>) — the edits landed in the USER's own file at <see cref="OutputPath"/>, not a new
        /// patch. Drives the distinct "edited in place" confirmation (and the "no undo; keep your own backup" note).</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake. The
        /// in-place lane refused to write this plugin until the user acknowledges the trade-off; <see cref="Error"/>
        /// carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation prompt, never "error:"
        /// (Q3 — a required confirmation is not a failure). Nothing was written; the original is untouched.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the write did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace audit
        /// marker couldn't be written). Null when there's nothing to add.</summary>
        public string? Note { get; init; }

        public static PatchOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no
        /// downstream success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt.</summary>
        public static PatchOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0) { NeedsAcknowledge = true };
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
        /// <summary>True ⇒ this outcome came from the IN-PLACE remove lane (<see cref="RemoveRecords"/>'s sibling
        /// <see cref="RemoveRecordsInPlace"/>) — the records were dropped from the USER's own file at
        /// <see cref="OutputPath"/>, not a houseCARL patch. Drives the distinct "removed in place" confirmation (and the
        /// "no undo; keep your own backup" note). Mirrors <see cref="PatchOutcome.InPlace"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake. The
        /// in-place lane refused to write this plugin until the user acknowledges the trade-off; <see cref="Error"/>
        /// carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation prompt, never "error:"
        /// (Q3 — a required confirmation is not a failure). Nothing was written; the original is untouched.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the removal did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace
        /// audit marker couldn't be written). Null when there's nothing to add.</summary>
        public string? Note { get; init; }

        public static RemovalOutcome Fail(string error) =>
            new(false, error, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no
        /// downstream success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt.</summary>
        public static RemovalOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0) { NeedsAcknowledge = true };
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

        /// <summary>True ⇒ this outcome came from the IN-PLACE create lane (<see cref="CreateRecords"/>'s sibling
        /// <see cref="CreateRecordsInPlace"/>) — the new records were allocated into the USER's own file at
        /// <see cref="OutputPath"/>, not a new patch. Drives the distinct "created in place" confirmation (and the
        /// "no undo; keep your own backup" note). Mirrors <see cref="PatchOutcome.InPlace"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake.
        /// The in-place create lane refused to write this plugin until the user acknowledges the trade-off;
        /// <see cref="Error"/> carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation
        /// prompt, never "error:" (Q3). Nothing was written; the original is untouched. Mirrors
        /// <see cref="PatchOutcome.NeedsAcknowledge"/>.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the write did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace audit
        /// marker couldn't be written). Null when there's nothing to add. Mirrors <see cref="PatchOutcome.Note"/>.</summary>
        public string? Note { get; init; }

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

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no downstream
        /// success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt. Mirrors
        /// <see cref="PatchOutcome.NeedsAck"/>.</summary>
        public static CreateOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
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
        IReadOnlyList<PatchEdit> edits, string outPath, bool extend, bool fullReadback = false,
        IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources = null)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Per-call overlay session (Option B): every source plugin this write reads (winner bodies, the nested link
        // cache, the known-master set) is opened THROUGH it and disposed when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();

        // --- Phase 0: open (extend) or create the patch mod BEFORE resolving targets (HCBR-2026-07-10-01 F3, the
        //     edit-lane twin of CreateRecords' Phase 0): an extend edit may target a record the PATCH ITSELF defines
        //     (created by a prior into= call, not yet enabled in MO2), so the opened patch must be consultable by the
        //     resolve loop. The serializer ties the output filename to the ModKey. ---
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

        // --- Phase 1: resolve winner + derive RecordType + pre-flight EVERY edit. Collect ALL problems (so the caller
        //     sees every fix at once), then refuse the whole call if any (Q3 — never a silently-partial patch).
        //     ONE captured view answers EVERY edit (2026-06-12 hunt F5): the per-edit single-shot resolves each took
        //     a fresh capture, so a freshness rebuild landing mid-loop (a concurrent read's refresh) could resolve
        //     two edits of ONE call against two different builds' winners — a silently MIXED patch
        //     (freshness-capture-guard arm 4, RED pre-fix: 2 of 12 hammered multi-op writes mixed). The write is one
        //     logical operation; it reads one build — the same HCBR-2026-06-11-02 discipline the read service follows.
        //     A target absent from the load order that the EXTENDED patch itself defines (a record a prior into= call
        //     created, patch not yet enabled in MO2 — HCBR-2026-07-10-01 F3) resolves to the patch's OWN settable copy
        //     (patchLocal; Phase 3 edits it directly, no override) — this consults ONLY the named output artifact of
        //     the current authoring session, never an arbitrary un-enabled plugin, so the Q3 winner-confusion hazard
        //     the declined read-un-enabled-plugins feature guards against doesn't arise. ---
        var view = resolver.Capture();
        var resolved = new List<(PatchEdit edit, IMajorRecordGetter? body, string? winnerPlugin, IMajorRecord? patchLocal, WriteRequest req, string label, IMajorRecordGetter? srcBody)>(edits.Count);
        var problems = new List<string>();
        // Records the extended patch DEFINES (FormKey in the patch's own master space — created by a prior into=
        // call), built lazily ONCE on the first load-order miss (PR #166 review finding 3: not a per-miss deep walk).
        // Deliberately NOT every record the patch contains: an override the patch merely CARRIES resolves via the
        // load order like any other record, so a target whose defining plugin is disabled stays a loud refusal —
        // never a silent edit of the patch's possibly-stale override copy (PR #166 review finding 2).
        Dictionary<FormKey, IMajorRecord>? patchDefined = null;
        foreach (var e in edits)
        {
            IMajorRecordGetter? body = null; string? winnerPlugin = null; IMajorRecord? patchLocal = null;
            var w = view.ResolveWinner(e.Target);
            if (w is not null)
            {
                body = view.GetRecord(session, w.Value.WinnerPlugin, e.Target);
                if (body is null) { problems.Add($"{e.Target}: winner '{w.Value.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
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
                    problems.Add($"{e.Target}: not present in the load order ({view.PluginCount} plugins)"
                        + (extend
                            ? $", and not a record '{fileName}' (the patch being extended) itself defines — a record " +
                              "the patch merely OVERRIDES resolves via the load order, so its defining plugin must be enabled."
                            : "."));
                    continue;
                }
            }

            // P8b CopyFrom source: from_plugin's version of e.Target — an OFF-ORDER file the SERVICE pre-located (passed in
            // copyFromSources), ELSE resolved from the ACTIVE order via this same captured view (the forward_record contract:
            // in the order, defines/overrides the record, and not the output patch itself). Refused loud, all-or-nothing (Q3).
            IMajorRecordGetter? srcBody = null;
            if (string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))
            {
                if (copyFromSources is not null && copyFromSources.TryGetValue(e, out var offSrc))
                    srcBody = offSrc;
                else if (string.IsNullOrWhiteSpace(e.FromPlugin))
                { problems.Add($"{e.Target}: CopyFrom is missing from_plugin (internal — the mapper should have caught this)."); continue; }
                else if (string.Equals(e.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase))
                { problems.Add($"{e.Target}: CopyFrom from_plugin '{e.FromPlugin}' is the output patch itself — name the OTHER plugin whose version to copy from."); continue; }
                else if (!view.ContainsPlugin(e.FromPlugin))
                { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' is not in the load order (and no plugin file by that name was located on disk) — name an active plugin, or a plugin file present on disk."); continue; }
                else if (view.ExcludedPlugins.TryGetValue(e.FromPlugin, out var why))
                { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
                else
                {
                    srcBody = view.GetRecord(session, e.FromPlugin, e.Target);
                    if (srcBody is null)
                    { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' is in the load order but does NOT define or override this record — there is no version of it there to copy."); continue; }
                }
            }

            var recType = RecordNaming.StripOverlay((patchLocal ?? (object)body!).GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct, Structs = e.Structs,
            };
            var label = Label(req);
            if (rulebook.Validate(req) is { } reject) { problems.Add($"{recType} {e.Target} [{label}]: {reject}"); continue; }
            resolved.Add((e, body, winnerPlugin, patchLocal, req, label, srcBody));
        }
        if (problems.Count > 0)
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; NO patch written:\n  - "
                + string.Join("\n  - ", problems));

        // --- Phase 3: override each winner into the ONE patch mod, then apply. A flat record needs no link cache; a
        //     NESTED record (Cell/Placed*/INFO/Navmesh/Landscape) gets the winner overlay's cache built on demand
        //     (costly → only here, never for the flat common case, never held). A throw here AFTER pre-flight passed is
        //     a real engine inconsistency — fail the WHOLE call (no partial patch), surfaced not swallowed (Q3). ---
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, winnerPlugin, patchLocal, req, label, srcBody) in resolved)
        {
            try
            {
                // A patch-local target (defined only in the extended patch — HCBR-2026-07-10-01 F3) is already a
                // settable record IN patchMod: edit it directly, no override and no source link cache needed.
                IMajorRecord ov;
                if (patchLocal is not null) ov = patchLocal;
                else
                {
                    ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body!) ? session.LinkCacheFor(winnerPlugin!) : null;
                    ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, body!, cache);
                }
                // P8b CopyFrom transplants the field FROM the source body into ov; every other verb applies to ov directly.
                if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
                    WriteEngine.CopyField(srcBody!, ov, req.Path);
                else
                    WriteEngine.ApplyVerb(ov, req);
                var (after, landed) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed));
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

        // #131 F1 — edit-lane intent-following: if this call changed a DialogTopic's Subtype without also setting its
        // SubtypeName, sync the SNAM marker so the change isn't a silent in-game no-op. Refuses loud on an unmodeled
        // Subtype (no partial patch written).
        if (SyncEditedTopicMarkers(patchMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} (no patch written).");

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

    /// <summary>#131 F1 (edit-lane intent-following), shared by <see cref="Apply"/> and <see cref="ApplyInPlace"/>:
    /// after the edits are applied, for each DialogTopic this call edited where it SET <c>Subtype</c> but NOT
    /// <c>SubtypeName</c>, sync the SNAM marker to the new Subtype — otherwise the change is a silent in-game no-op
    /// (the engine buckets topics by the SNAM marker, so a stale marker keeps the old bucket; #131). It fires ONLY
    /// when Subtype was actively set in THIS call, so it never rewrites the SNAM of a topic whose subtype the call
    /// didn't touch — which is what keeps it off the countless vanilla topics whose DATA\Subtype is legitimately
    /// noisy (a blanket "SubtypeName != marker" lint would false-positive on those). Adds a report op on a real
    /// change (Q3 — never silent); returns a non-null error to FAIL the whole call on an unmodeled Subtype (never
    /// leave a mismatched/blank marker), else null. <paramref name="mod"/> is the mutable mod the overrides live in.</summary>
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
                        $"{marker} — you set Subtype={dt.Subtype}; the game buckets by the SNAM marker, so it was synced to match (#131 — otherwise the Subtype change is a silent no-op)"));
                    break;
                case MarkerFill.Unmodeled:
                    return $"cannot set Subtype on DialogTopic {fk}: no SNAM marker is modeled for Subtype={dt.Subtype} " +
                           $"((int){(int)dt.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). Use a valid Subtype, or set SubtypeName explicitly";
            }
        }
        return null;
    }

    /// <summary>
    /// EDIT records IN PLACE inside an EXISTING plugin the user owns — the opt-in second write lane (in-place write
    /// lane, Wave 1), the sibling of <see cref="Apply"/>. Where <see cref="Apply"/> overrides the load-order WINNER into
    /// a NEW patch (originals untouched), this opens the TARGET plugin itself mutably, edits the TARGET's OWN record, and
    /// re-serializes the whole plugin back over itself — the user's original file IS the output. Three deliberate
    /// divergences from <see cref="Apply"/>, each load-bearing:
    /// <list type="bullet">
    /// <item>CONTENT SOURCE (§4.1 winner-injection fix): the body is the TARGET's own record
    /// (<c>view.GetRecord(session, target, fk)</c>), NEVER the load-order winner — and the call REFUSES loud if the
    /// target doesn't itself define/override the FormKey ("in-place edits only what the file OWNS"). So pre-flight
    /// validates the body actually mutated, and another mod's content can never be injected into the user's file.</item>
    /// <item>DESTINATION: <paramref name="targetPath"/> IS the target's real on-disk path (the caller resolved it via
    /// the load order, dropping the houseCARL-owned gate); the mutable mod IS the target (CreateFromBinary), so
    /// <see cref="WriteEngine.GenericGetOrAddAsOverride"/> returns the target's OWN record (get-semantics).</item>
    /// <item>SERIALIZE (model C — what the Wave 0 probe validated, NOT <see cref="WriteEngine.WritePatch"/>):
    /// <see cref="WriteEngine.WriteInPlace"/> re-emits with the target's OWN declared masters, no baseline force-include,
    /// no FormID floor — preserving the author's master list + NextObjectID as xEdit/CK do on save.</item>
    /// </list>
    /// The reused full read-back (<paramref name="fullReadback"/>, default ON here) VERIFIES the records actually touched
    /// landed; Mutagen is trusted for the rest (the xEdit-parity bar). CONSENT + the persistent acknowledge handshake are
    /// enforced by the SERVICE before this is reached — this is the mechanism. All-or-nothing (Q3): any resolve/pre-flight
    /// reject, or a serialize failure, leaves the original file UNTOUCHED (staged temp + atomic swap).
    /// </summary>
    public static PatchOutcome ApplyInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string targetPath, string targetName, bool fullReadback = true)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Per-call overlay session (Option B), same as Apply: every read (the target's own bodies, the nested link
        // cache) is opened THROUGH it and disposed when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: resolve each edit's body FROM THE TARGET (not the winner) + derive type + pre-flight. The §4.1
        //     content-source guard. ONE captured view answers every edit (the hunt-F5 one-view discipline). ---
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return PatchOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return PatchOutcome.Fail(
                $"cannot edit '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");

        var resolved = new List<(PatchEdit edit, IMajorRecordGetter body, WriteRequest req, string label)>(edits.Count);
        var problems = new List<string>();
        foreach (var e in edits)
        {
            var body = view.GetRecord(session, targetName, e.Target);
            if (body is null)
            {
                problems.Add($"{e.Target}: '{targetName}' does not define or override this record — in-place edits only what the " +
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
            if (rulebook.Validate(req) is { } reject) { problems.Add($"{recType} {e.Target} [{label}]: {reject}"); continue; }
            resolved.Add((e, body, req, label));
        }
        if (problems.Count > 0)
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; '{fileName}' is UNTOUCHED:\n  - "
                + string.Join("\n  - ", problems));

        // --- Phase 2: open the TARGET mutably. EAGER, the SINGLE plugin only — NEVER the load order (the legacy 12–14 GB
        //     RAM trap; CLAUDE.md §1). CreateFromBinary is the same call Apply's extend path uses; an unparseable plugin
        //     throws here and is REFUSED, never silently re-emitted minus the record Mutagen couldn't read (Q3). ---
        if (!File.Exists(targetPath))
            return PatchOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return PatchOutcome.Fail($"cannot open '{fileName}' to edit in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // The author's DECLARED masters, captured before any mutation — the re-opened header is diffed against this
        // to surface a master GROW as an explicit re-sort note (PR #163 review #1: a grown master is itself a
        // re-sort trigger — the plugin is invalid until it loads AFTER its new master — independent of winners).
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --- Phase 3: apply each verb to the TARGET's OWN record. GenericGetOrAddAsOverride on the target's own body
        //     (already present in targetMod) returns THAT record (get-semantics) — the verb edits the file's own body,
        //     never a foreign override's. A nested record gets the target overlay's link cache on demand (released in
        //     Phase 4). A throw after pre-flight passed is a real engine inconsistency — fail the WHOLE call (Q3). ---
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, req, label) in resolved)
        {
            try
            {
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(targetName) : null;
                var ov = WriteEngine.GenericGetOrAddAsOverride(targetMod, body, cache);
                WriteEngine.ApplyVerb(ov, req);
                var (after, landed) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed));
            }
            catch (ExpectedApplyRejectionException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (the file is untouched)");
            }
            catch (MalformedTargetDataException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (the file is untouched)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {e.Target}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // #131 F1 — edit-lane intent-following (same as Apply): a Subtype change without a SubtypeName in this call
        // syncs the SNAM marker, so the edit isn't a silent in-game no-op. Refuses loud on an unmodeled Subtype (the
        // original file stays UNTOUCHED — nothing serialized yet).
        if (SyncEditedTopicMarkers(targetMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} ('{fileName}' is UNTOUCHED).");

        // --- Phase 4: re-serialize the WHOLE target back over itself (model C — the probe's incantation via WriteInPlace,
        //     NOT WritePatch). Release the target overlay first (the winner-IS-the-target common case made common — the
        //     two-part self-lock guard, here on a FOREIGN target: ReleaseOverlay disposes every session overlay on the
        //     target, flat via GetRecord and nested via LinkCacheFor, before the File.Replace). The resolution context is
        //     the WHOLE known-master set (AllMastersExcept — the same context the in-place CREATE lane serializes
        //     against), NOT just the target's declared masters: an edit that composes a FormLink to an ACTIVE plugin the
        //     target didn't yet master must GROW the header (Mutagen lean-derives it from the records' actual links —
        //     exactly how forward_record grows masters), instead of failing MissingModException against a context that
        //     artificially excluded the referenced plugin (HCBR-2026-07-08-01 F2). A link to a plugin genuinely NOT
        //     active still fails loud below (Q3), now meaning what it says. ---
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath); }
        catch (MissingModException ex)
        {
            return PatchOutcome.Fail(
                $"writing '{fileName}' in place failed: the edited records reference a plugin that is NOT active in " +
                $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                "Enable that plugin in MO2 (or reference an active one) and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // --- Phase 5: re-open the now-edited file and report its master header — and the touched-record verify (default
        //     ON for in-place): each edited record read back IN FULL off the on-disk bytes (the model-C substitute for
        //     the dropped whole-plugin floor — confirm what you TOUCHED landed; Mutagen is trusted for the rest). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"'{fileName}' was edited in place but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, targetPath, false, masters, ops, bytes)
            { ReadBack = readBack, InPlace = true, Note = MasterGrowNote(fileName, mastersBefore, masters) };
    }

    /// <summary>The explicit re-sort note when an in-place write GREW the target's master header (PR #163 review #1):
    /// Skyrim loads a plugin only AFTER its masters, so a newly added master — e.g. an edit's FormLink into an
    /// active-but-undeclared plugin (F2), or a forwarded body's references (F4) — leaves the file invalid until the
    /// order is re-sorted, independent of any conflict-winner change. Null when nothing was added (the common case,
    /// and any prune: a shrink never breaks load eligibility).</summary>
    static string? MasterGrowNote(string fileName, HashSet<string> mastersBefore, IReadOnlyList<string> mastersAfter)
    {
        var grown = mastersAfter.Where(m => !mastersBefore.Contains(m)).ToList();
        if (grown.Count == 0) return null;
        return $"{string.Join(", ", grown)} {(grown.Count == 1 ? "was" : "were")} added as a master of '{fileName}' — " +
               "a plugin loads only if its masters load BEFORE it, so re-sort your load order (LOOT / MO2) before playing.";
    }

    /// <summary>Resolve the target's OWN declared masters to on-disk overlays in declared order — the load order
    /// <see cref="WriteEngine.WriteInPlace"/> hands Mutagen (the probe's faithful set). Each master filename resolves to
    /// its WINNING on-disk path via the order (<see cref="LoadOrderResolver.IndexView.PluginPath"/>); a declared master
    /// ABSENT from the active order makes <paramref name="missing"/> a loud Q3 refusal (re-serializing would leave the
    /// target's references unresolvable) rather than emit a broken plugin. Opened overlays are added to
    /// <paramref name="overlays"/> for the caller to dispose after the write.</summary>
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
            var ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE);
            overlays.Add((IDisposable)ov);
            resolved.Add(ov);
        }
        return resolved.ToArray();
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
        // report AND the removal-routing type: the record's FLAT GROUP's T, not the concrete runtime type — a subclass
        // of an abstract group's T (GlobalShort) makes Mutagen's Remove silently no-op (HCBR-2026-07-08-01 F3).
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
        if (RemoveSurvivors(patchMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" '{fileName}' is UNTOUCHED.");

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

    /// <summary>IN-MEMORY absence verify run BEFORE any serialize, shared by both remove lanes: Mutagen's typed
    /// <c>Remove</c> can no-op WITHOUT throwing (<c>throwIfUnknown:true</c> notwithstanding — HCBR-2026-07-08-01 F3
    /// proved it for a concrete subclass of an abstract group's T), so trusting the void return and serializing anyway
    /// rewrites the whole file to no effect and only the post-write verify catches it. Checking the mutable mod first
    /// keeps a future no-op loud with the file UNTOUCHED (Q3). Null ⇒ all targets gone; else the refusal text (the
    /// caller appends its lane's file-untouched suffix).</summary>
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

    /// <summary>
    /// Remove WHOLE records IN PLACE — the in-place write lane's Wave-2 sibling of <see cref="RemoveRecords"/> and the
    /// remove counterpart of <see cref="ApplyInPlace"/>. Drops a record the TARGET's own file carries (one it DEFINES, or
    /// an override it HOLDS) back over the user's ORIGINAL file, instead of dropping it from a houseCARL patch. REUSES
    /// every in-place mechanic <see cref="ApplyInPlace"/> proved: a per-call overlay session, the ContainsPlugin/
    /// ExcludedPlugins refusal (never re-serialize a plugin Mutagen can't fully parse — that would risk dropping the
    /// record it couldn't read, Q3), an EAGER CreateFromBinary of the SINGLE target (NEVER the order — the legacy
    /// 12–14 GB RAM trap), <see cref="LoadOrderResolver.OverlaySession.ReleaseOverlay"/> before the swap (the self-lock
    /// guard, here on a FOREIGN target), own-declared-masters re-emit via <see cref="WriteEngine.WriteInPlace"/> (NOT
    /// WritePatch — no Skyrim.esm/Update.esm baseline force-include, the author's HEDR.NextObjectID preserved), and the
    /// crash-atomic swap.
    ///
    /// <para>REMOVAL SEMANTICS are <see cref="RemoveRecords"/>'s, unchanged: PRESENT-CHECK FIRST against what the TARGET
    /// carries (Q3 — a key the file doesn't carry is REFUSED, the whole call, nothing written; <c>Remove</c> is a silent
    /// no-op otherwise), then the typed <c>Remove(FormKey, Type, throwIfUnknown)</c> that reaches every group (flat AND
    /// nested). MASTER-PRUNE rides along for free exactly as the patch lane gets it: <see cref="WriteEngine.WriteInPlace"/>
    /// hands Mutagen the target's own masters as the resolution context and lean-derives the emitted header from the
    /// SURVIVING records' links, so a master the removal orphaned drops from the header automatically. The model-C
    /// touched-record verify is applied to a removal as ABSENCE — every removed FormKey is confirmed GONE on the
    /// re-opened on-disk file (confirm what you DROPPED actually went; Mutagen is trusted for the untouched rest).</para>
    ///
    /// <para>The caller (the service in-place branch) has already resolved <paramref name="targetPath"/> to the real
    /// active-plugin path, run the consent handshake, and checked the writable parent — exactly as it does for
    /// <see cref="ApplyInPlace"/>. Returns <see cref="RemovalOutcome.InPlace"/>=true on success.</para>
    /// </summary>
    public static RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string targetPath, string targetName)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Per-call overlay session (Option B), same as ApplyInPlace: every read (the master set for the re-serialize) is
        // opened THROUGH it and disposed when the method returns — no handle held at rest.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: the target must be an active, FULLY-PARSEABLE plugin (the ApplyInPlace guard — never re-serialize a
        //     plugin Mutagen excluded, which would risk dropping the record it couldn't read on the rewrite, Q3). ---
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return RemovalOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return RemovalOutcome.Fail(
                $"cannot remove from '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");

        // --- Phase 2: open the TARGET mutably. EAGER, the SINGLE plugin only — NEVER the load order. CreateFromBinary is
        //     the same call ApplyInPlace uses; an unparseable plugin throws here and is REFUSED, never silently
        //     re-emitted minus the record Mutagen couldn't read (Q3). ---
        if (!File.Exists(targetPath))
            return RemovalOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return RemovalOutcome.Fail($"cannot open '{fileName}' to remove from in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return RemovalOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");

        // --- Phase 3: present-check against what the TARGET carries (RemoveRecords' contract, unchanged). One enumeration
        //     (flat + nested) captures type+editorid for the report AND the removal-routing type — the record's FLAT
        //     GROUP's T, not the concrete runtime type: a subclass of an abstract group's T (GlobalShort) makes Mutagen's
        //     Remove silently no-op (HCBR-2026-07-08-01 F3). A key the file doesn't define/override is REFUSED loud —
        //     in-place removes only what the file OWNS. ---
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
                    $"{fk}: not carried by '{fileName}' — in-place removes only a record the file ITSELF defines or " +
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

        // --- Phase 4: literal drop-from-group (NOT flag-as-deleted), the typed overload RemoveRecords proved reaches
        //     nested groups (Cell/Placed*/INFO/Navmesh/Landscape) too. throwIfUnknown:true keeps an unrecognized type
        //     loud (Q3). A throw AFTER the present-check passed is a real engine inconsistency — surfaced, not swallowed. ---
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
        if (RemoveSurvivors(targetMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" Your original '{fileName}' is UNTOUCHED.");

        // --- Phase 5: re-serialize the WHOLE target back over itself (model C — WriteInPlace, NOT WritePatch). Release
        //     the target overlay first (the self-lock guard on a FOREIGN target: ReleaseOverlay disposes every session
        //     overlay on the target — none here, since the present-check read targetMod directly, not via an overlay, but
        //     the discipline is kept identical to ApplyInPlace). Resolve the target's OWN declared masters; Mutagen orders
        //     against them and lean-derives the emitted header from the SURVIVING records, so an orphaned master drops. ---
        session.ReleaseOverlay(fileName);
        var masterOverlays = new List<IDisposable>();
        try
        {
            ISkyrimModGetter[] ownMasters = ResolveOwnMasters(view, targetMod, masterOverlays, out var missing);
            if (missing is not null) return RemovalOutcome.Fail(missing);
            try { WriteEngine.WriteInPlace(targetMod, ownMasters, targetPath); }
            catch (Exception ex)
                { return RemovalOutcome.Fail($"writing '{fileName}' in place after removal failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // --- Phase 6: re-open the now-rewritten file; report its (possibly shrunk) master header + how many records
        //     remain, and VERIFY each removed FormKey is ABSENT (the model-C touched-record verify applied to a removal
        //     as absence). One enumeration counts survivors AND catches any removed key that wrongly survived. ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
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

    /// <summary>Phase-1 source resolution SHARED by <see cref="ForwardRecords"/> and <see cref="ForwardRecordsInPlace"/>:
    /// resolve each spec's body from its NAMED plugin (NOT the load-order winner) off ONE captured view, collecting ALL
    /// problems (Q3 — refuse the whole call if any; <paramref name="refusal"/> non-null ⇒ the caller fails with it).
    /// <paramref name="selfIsTarget"/> only shapes the self-forward message (output patch vs in-place target).</summary>
    static List<(ForwardSpec spec, IMajorRecordGetter body, string priorWinner, bool wasWinner)> ResolveForwardSources(
        LoadOrderResolver.OverlaySession session, LoadOrderResolver.IndexView view,
        IReadOnlyList<ForwardSpec> specs, string fileName, bool selfIsTarget, out string? refusal)
    {
        var resolved = new List<(ForwardSpec spec, IMajorRecordGetter body, string priorWinner, bool wasWinner)>(specs.Count);
        var problems = new List<string>();
        var seen = new HashSet<FormKey>();
        foreach (var s in specs)
        {
            if (!seen.Add(s.Target))
            { problems.Add($"{s.Target}: forwarded more than once in this call — name each target once (one source per record)."); continue; }
            if (string.Equals(s.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(selfIsTarget
                    ? $"{s.Target}: from_plugin '{s.FromPlugin}' is the in-place target itself — forwarding a plugin's own version into itself is a no-op; name the OTHER plugin whose version you want carried in."
                    : $"{s.Target}: from_plugin '{s.FromPlugin}' is the output patch itself — forwarding a patch's own version into itself is a no-op; name the EARLIER plugin whose version you want to re-assert.");
                continue;
            }
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
        refusal = problems.Count > 0
            ? $"refused — {problems.Count} of {specs.Count} forward(s) rejected; NOTHING written:\n  - " + string.Join("\n  - ", problems)
            : null;
        return resolved;
    }

    /// <summary>
    /// Forward records INTO an EXISTING plugin the user owns, IN PLACE — the forward lane's sibling of
    /// <see cref="ApplyInPlace"/> (HCBR-2026-07-08-01 F4: <c>into=</c> is reserved for houseCARL-owned patches, so a
    /// non-houseCARL original gets forwards through THIS explicit, consent-gated lane instead). Semantics:
    /// <list type="bullet">
    /// <item>SOURCE: each body resolves from its NAMED plugin exactly as <see cref="ForwardRecords"/> Phase 1 (the
    /// shared <see cref="ResolveForwardSources"/>).</item>
    /// <item>COLLISION = REPLACE: a FormKey the target already carries has its existing record dropped
    /// (<see cref="WriteEngine.RemovalTypeFor"/> + the no-op verify) before the copy — the same xEdit
    /// copy-as-override-into overwrite the default lane's extend now performs (F1), flagged per record.</item>
    /// <item>SERIALIZE: model C via <see cref="WriteEngine.WriteInPlace"/> against the WHOLE known-master set (the
    /// in-place create/edit lanes' context) — the header grows from the copied bodies' actual links (F2's mechanic),
    /// the author's counter survives, no baseline force-include. Atomic swap; any failure leaves the original intact.</item>
    /// </list>
    /// CONSENT + the persistent acknowledge handshake are enforced by the SERVICE before this is reached. The full
    /// read-back (default ON, like every in-place lane) verifies each forwarded record landed on the re-opened file.
    /// </summary>
    public static ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string targetPath, string targetName,
        bool fullReadback = true)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Per-call overlay session (Option B), same as ApplyInPlace — no handle held at rest.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // --- Phase 1: target guards (the ApplyInPlace posture — never re-serialize a plugin Mutagen can't fully
        //     parse) + source resolution off ONE captured view. ---
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return ForwardOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return ForwardOutcome.Fail(
                $"cannot forward into '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");
        var resolved = ResolveForwardSources(session, view, specs, targetName, selfIsTarget: true, out var refusal);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);

        // --- Phase 2: open the TARGET mutably (EAGER, the single plugin only — never the order). ---
        if (!File.Exists(targetPath))
            return ForwardOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"cannot open '{fileName}' to forward into in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // Declared masters before any mutation — diffed against the re-opened header for the master-grow re-sort
        // note (PR #163 review #1; see MasterGrowNote).
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // --- Phase 3: replace-or-copy each source body into the TARGET (the F1 semantic — a carried FormKey is
        //     REPLACED, verified dropped before the copy; nothing is serialized until Phase 4, so any refusal here
        //     leaves the on-disk file untouched). ---
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        foreach (var r in targetMod.EnumerateMajorRecords())
            alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner) in resolved)
        {
            try
            {
                bool replaced = false;
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    ((IMajorRecordEnumerable)targetMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    if (targetMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {spec.Target}: '{fileName}' already carries this record and its existing " +
                            "version could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); your original is UNTOUCHED.");
                    replaced = true;
                }
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(spec.FromPlugin) : null;
                WriteEngine.GenericGetOrAddAsOverride(targetMod, body, cache);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {spec.Target} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}. Your original is UNTOUCHED.");
            }
        }

        // --- Phase 4: model-C re-serialize over the original (WriteInPlace, whole known-master set — the copied
        //     bodies' links grow the header; the self-lock ReleaseOverlay first; atomic swap). ---
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath); }
        catch (MissingModException ex)
        {
            return ForwardOutcome.Fail(
                $"writing '{fileName}' in place failed: the forwarded records reference a plugin that is NOT active in " +
                $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                "Enable that plugin in MO2 and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // --- Phase 5: re-open + report masters/bytes + the touched-record verify (default ON for in-place). ---
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"'{fileName}' was rewritten but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, targetPath, false, forwarded, masters, bytes)
            { ReadBack = readBack, InPlace = true, Note = MasterGrowNote(fileName, mastersBefore, masters) };
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
    /// winner, so this override is a redundant no-op copy — surfaced, never silent (Q3).
    /// <see cref="ReplacedExisting"/>=true ⇒ the patch ALREADY carried this FormKey and its existing record was
    /// REPLACED by the source's body (the xEdit copy-as-override-into semantic; HCBR-2026-07-08-01 F1 — the old
    /// GetOrAdd path kept the existing record and skipped the copy while still reporting "forwarded").</summary>
    public sealed record ForwardedRecord(
        FormKey Target, string RecordType, string? EditorId, string FromPlugin, string PriorWinner, bool WasAlreadyWinner,
        bool ReplacedExisting = false);

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

        /// <summary>True ⇒ the forwards were written INTO the target's own file (<see cref="ForwardRecordsInPlace"/> —
        /// the in-place write lane), not a houseCARL patch folder. Mirrors <see cref="PatchOutcome"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake.
        /// <see cref="Error"/> carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation
        /// prompt, never "error:" (Q3). Nothing was written. Mirrors <see cref="PatchOutcome.NeedsAcknowledge"/>.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the write did. Null when there's nothing to add. Mirrors <see cref="PatchOutcome.Note"/>.</summary>
        public string? Note { get; init; }

        public static ForwardOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Mirrors
        /// <see cref="PatchOutcome.NeedsAck"/>.</summary>
        public static ForwardOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
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

    /// <summary>One external referencer's in-place repoint result (the opt-in compact rewrite): the plugin, whether its
    /// references were successfully rewritten to the new keys, and the named reason if not (Q3 — a per-plugin failure is
    /// reported, never silent; the file is left untouched on failure by <see cref="RemapEngine.RepointInPlace"/>).</summary>
    public sealed record RepointReport(string Plugin, bool Success, string? Error);

    /// <summary>The outcome of a <see cref="LoadOrderService.CompactPlugin"/> call (the compact/ESL-renumber tool).
    /// <see cref="NeedsAcknowledge"/> ⇒ a required first-time in-place CONSENT prompt (the operation will overwrite an
    /// existing file — the target in the in-place lane, and/or each external referencer being repointed — so the caller
    /// must re-call with acknowledge=true); it is NOT an error (Q3). <see cref="Error"/> non-null (with NeedsAcknowledge
    /// false) ⇒ refused, nothing written, named reason. On success the compacted P′ is at <see cref="OutputPath"/>
    /// (<see cref="InPlace"/> ⇒ the original file was overwritten; else a NEW file keeping the source's basename in a fresh
    /// mod folder). <see cref="RecordsRenumbered"/> originating records moved into the (light if <see cref="Esl"/>) window;
    /// <see cref="RecordsCopied"/> total (originating + overrides copied at their master keys). <see cref="ExternalPlugins"/>
    /// lists plugins outside the target that reference a renumbered record — empty on the clean path; on the refused path
    /// (externals present, no opt-in) the list IS the refusal detail; with opt-in repoint, <see cref="Repointed"/> reports
    /// each. <see cref="PluginsScanned"/>/<see cref="UnscannableRecords"/> are the identify-pass coverage accounting.
    /// <see cref="AssetRename"/> (null until the asset-carry runs) is the FormID-keyed-asset accounting — A1: facegen
    /// carried to the new FormIDs (so a compacted NPC mod no longer silently dark-faces). <see cref="VoiceRename"/>
    /// (A2, null until the carry runs) is the same for voice (.fuz/.lip), so a compacted voiced mod no longer goes mute.
    /// <see cref="ExternalOverriders"/>
    /// (gap #2) are plugins OUTSIDE the target that OVERRIDE a renumbered record — surfaced as a WARN (they orphan after
    /// the renumber and houseCARL can't auto-repoint an override's identity); they never gate the compaction.
    /// <see cref="SeqRegen"/> (A3, null until the regen runs) is the start-game-enabled-quest <c>.seq</c> accounting — a
    /// renumber shifts the on-disk FormIDs a <c>.seq</c> lists, so a <c>.seq</c> the source SHIPPED is REBUILT from P′ (not
    /// carried) so those quests still start; REFRESH-ONLY — a source with no <c>.seq</c> gets a named advisory, not an
    /// invented file; a plugin with no SGE quests is a clean no-op.</summary>
    public sealed record CompactOutcome(
        bool Success, string? Error, bool NeedsAcknowledge, string OutputPath, string PluginName, bool InPlace, bool Esl,
        IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<RepointReport> Repointed,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples, string? Note = null,
        AssetRenameOutcome? AssetRename = null, IReadOnlyList<string>? ExternalOverriders = null,
        VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null)
    {
        public static CompactOutcome Fail(string error) =>
            new(false, error, false, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());
        public static CompactOutcome Confirm(string prompt) =>
            new(false, prompt, true, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());
    }

    /// <summary>The merge tool's outcome (A4): the merged plugin's identity + per-donor remap accounting + every
    /// cross-donor conflict (load-order winner, reported never silent) + the identify-pass WARN surfaces (external
    /// referencers AND overriders — merge never refuses on them, the donors stay installed and active until the user
    /// swaps in MO2, so nothing breaks at write time; the report names each with the remedy) + the FormID-keyed asset
    /// carry accounting (facegen/voice/SEQ — a merge renames the plugin, so EVERY donor NPC's facegen and EVERY voiced
    /// line moves to the new-name folders, not just the collided ones). Merge has NO in-place lane and overwrites
    /// nothing, so there is no consent gate.</summary>
    public sealed record MergeOutcome(
        bool Success, string? Error, string OutputPath, string OutputName,
        IReadOnlyList<string> Donors, IReadOnlyList<string> Masters,
        int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeDonorRemap> DonorRemaps,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<string> ExternalOverriders,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples,
        long Bytes, string? Note = null,
        AssetRenameOutcome? AssetRename = null, VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null)
    {
        public static MergeOutcome Fail(string error) =>
            new(false, error, "", "", Array.Empty<string>(), Array.Empty<string>(), 0, 0,
                Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
                Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), 0);
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
        var resolved = ResolveForwardSources(session, view, specs, fileName, selfIsTarget: false, out var refusal);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);

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
        //     link cache on demand, the SAME session.LinkCacheFor path Apply uses + guards). A FormKey the patch ALREADY
        //     carries (an extend) is REPLACED — its existing record dropped first, then the source body copied — the
        //     xEdit copy-as-override-into semantic (HCBR-2026-07-08-01 F1: GetOrAdd's get-semantics on a collision kept
        //     the existing record and SKIPPED the copy while still reporting "forwarded" — a silent wrong result; the
        //     drop also lets the serialize grow the master list from the NEW body, which the skip path never did).
        //     A throw here is a real engine inconsistency — fail the WHOLE call (no partial patch; nothing has been
        //     serialized, the on-disk file is untouched), surfaced not swallowed (Q3). ---
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        if (extend)
            foreach (var r in patchMod.EnumerateMajorRecords())
                alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner) in resolved)
        {
            try
            {
                bool replaced = false;
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    ((IMajorRecordEnumerable)patchMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    // The typed Remove can no-op WITHOUT throwing (F3's sibling defect) — verify the slot is genuinely
                    // empty before the copy, else GetOrAdd would silently return the old record again (Q3).
                    if (patchMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {spec.Target}: the patch already carries this record and its existing " +
                            "override could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); nothing was serialized (the extended patch's on-disk file is untouched).");
                    replaced = true;
                }
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(spec.FromPlugin) : null;
                WriteEngine.GenericGetOrAddAsOverride(patchMod, body, cache);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced));
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
    /// placeholder ESL for FormID reservation, a dummy plugin another mod can list as a master to satisfy a dependency.
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
        if (confirmFail is null && masters.Count != 0)
            confirmFail = $"internal error: the created plugin carries {masters.Count} master(s) ({string.Join(", ", masters)}), expected 0 (a header-only plugin references nothing) — refusing to report success on a wrong artifact (Q3).";
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

    // ======================================================================
    //  COMPACT (A2) — the core build half of housecarl_compact_plugin (the
    //  service does the policy half: resolve P, identify externals, consent,
    //  folder allocation, opt-in external repoint). Renumber-mechanism + nested
    //  coverage live in RemapEngine (remap-wave1/2). Output model = a NEW P′ or
    //  an in-place overwrite (Aaron 2026-06-26: new-file default, in-place opt-in).
    // ======================================================================

    /// <summary>Read a plugin's ORIGINATING record FormKeys (<c>FormKey.ModKey == modKey</c>) in document order — the set
    /// a compaction renumbers (overrides, which reference a master's record, are NOT renumbered). Opens the plugin as a
    /// binary overlay (the lazy read path) and disposes it before returning, so no handle is held at rest (Option B).
    /// Returns false with a named reason (Q3) if the plugin can't be parsed — the same honesty the in-place lane uses
    /// (houseCARL won't renumber a plugin it can't fully read, lest it drop a record it couldn't parse).</summary>
    public static bool TryReadOriginatingKeys(string srcPath, ModKey modKey, out IReadOnlyList<FormKey> keys, out string? error)
    {
        keys = Array.Empty<FormKey>(); error = null;
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
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

    /// <summary>The result of the core compact build: success + the written file's masters / record accounting / byte
    /// size, or a loud Q3 refusal with the file UNTOUCHED (a missing master, a renumber fault, a serialize fault).</summary>
    public sealed record CompactBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes)
    {
        public static CompactBuildResult Fail(string error) => new(false, error, Array.Empty<string>(), 0, 0, 0);
    }

    /// <summary>
    /// Build the compacted plugin P′ from <paramref name="srcPath"/> and write it to <paramref name="outPath"/> (a NEW
    /// file, or — in the in-place lane — <paramref name="srcPath"/> itself). EAGER-loads the source mutable overlay,
    /// renumbers EVERY record (flat + nested) into a fresh <see cref="SkyrimMod"/> via
    /// <see cref="RemapEngine.RenumberModInto"/> under <paramref name="dict"/> (originating records → the window; overrides
    /// copied at their master keys), sets the light flag (<paramref name="esl"/>) and the NextObjectID, resolves P's OWN
    /// declared masters to overlays via <paramref name="resolveMasterPath"/>, and re-serializes through
    /// <see cref="WriteEngine.WriteInPlace"/> (own masters, no baseline force, crash-atomic staged swap — the faithful
    /// re-emit). The source overlay is DISPOSED before the write so the in-place lane (outPath == srcPath) can swap over
    /// it. All-or-nothing (Q3): any refusal or fault leaves <paramref name="outPath"/> untouched.
    /// </summary>
    public static CompactBuildResult CompactBuild(
        string srcPath, ModKey modKey, IReadOnlyDictionary<FormKey, FormKey> dict,
        Func<string, string?> resolveMasterPath, string outPath, bool esl, uint floor)
    {
        // 1. Build P′ in memory, then DISPOSE the source overlay (the in-place lane overwrites srcPath — its handle must
        //    be released before the atomic swap).
        SkyrimMod pPrime;
        RemapEngine.RenumberResult ren;
        List<string> declaredMasters;
        ISkyrimModGetter? srcOv = null;
        try
        {
            srcOv = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
            declaredMasters = srcOv.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
            pPrime = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = esl };
            ren = RemapEngine.RenumberModInto(pPrime, srcOv, dict);
        }
        catch (Exception ex)
        {
            return CompactBuildResult.Fail($"cannot open/renumber '{modKey.FileName}' to compact it ({WriteEngine.Describe(ex)}) — nothing written.");
        }
        finally { (srcOv as IDisposable)?.Dispose(); }

        if (!ren.Success) return CompactBuildResult.Fail(ren.Error!);

        // NextObjectID = the next free originating id above the renumbered run (floor + #originating). WriteInPlace
        // persists it verbatim (NoNextFormIDProcessing) — the CK reads it on the next save. Note (PR #122 review #6): for
        // an exactly-full light master (2048 records) this lands at 0x1000, one past the ESL ceiling — INTENTIONAL and
        // harmless: it's only header metadata for the NEXT new record, and compact creates none (it renumbers existing ones).
        pPrime.ModHeader.Stats.NextFormID = Math.Max(floor, (uint)(floor + dict.Count));

        // 2. Resolve P's OWN declared masters to overlays (the faithful re-serialize set). A declared master absent from
        //    the active order is a loud refusal (the refs into it can't resolve), file untouched.
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
                var mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE);
                overlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            try { WriteEngine.WriteInPlace(pPrime, resolved, outPath); }
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

    /// <summary>The result of the core merge build: success + the RESOLVED master set / record accounting / cross-donor
    /// conflicts / byte size, or a loud Q3 refusal with <c>outPath</c> UNTOUCHED (an unopenable donor, an engine fault,
    /// a dangling donor reference that would keep a donor as a master, an absent master, a serialize fault).</summary>
    public sealed record MergeBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts, long Bytes)
    {
        public static MergeBuildResult Fail(string error) =>
            new(false, error, Array.Empty<string>(), 0, 0, Array.Empty<RemapEngine.MergeConflict>(), 0);
    }

    /// <summary>
    /// Build the merged plugin M from the donors (in LOAD ORDER) and write it to <paramref name="outPath"/> (always a
    /// NEW file — merge has no in-place lane; the donors are never touched). Opens every donor as a lazy overlay,
    /// renumbers them into one fresh <see cref="SkyrimMod"/> via <see cref="RemapEngine.MergeModsInto"/> (load-order
    /// winner on cross-donor conflicts, losers' un-relisted children grafted), then enforces the donor-master-survives
    /// check (Q3, the zMerge "Clean" pattern): any link still pointing INTO a donor after the remap is a DANGLING source
    /// reference (the donor never defined that FormID, so the dict couldn't map it) — writing it would re-declare the
    /// donor as a master of its own merge, so the build REFUSES with the offending links NAMED. Masters =
    /// <paramref name="masters"/> (union of donor declared masters minus the donors, load-order sorted — the
    /// orchestrator computes it off the captured view), resolved to overlays for the master-aware serialize;
    /// <see cref="WriteEngine.WriteInPlace"/> then derives the header's master list from actual content against them.
    /// All-or-nothing (Q3): any refusal or fault leaves <paramref name="outPath"/> untouched.
    /// </summary>
    public static MergeBuildResult MergeBuild(
        IReadOnlyList<(string Name, string Path, ModKey Key)> donorsByLoadOrder, ModKey outKey,
        IReadOnlyDictionary<FormKey, FormKey> dict, IReadOnlyList<string> masters,
        Func<string, string?> resolveMasterPath, string outPath)
    {
        // 1. Open every donor overlay, build M in memory, dispose the donors before the write (no handle at rest;
        //    merge never writes over a donor, but the discipline is uniform).
        var m = new SkyrimMod(outKey, SkyrimRelease.SkyrimSE);
        RemapEngine.MergeResult mr;
        var donorSet = new HashSet<ModKey>(donorsByLoadOrder.Select(d => d.Key));
        var overlays = new List<IDisposable>();
        try
        {
            var mods = new List<(string, ISkyrimModGetter)>(donorsByLoadOrder.Count);
            foreach (var (name, path, _) in donorsByLoadOrder)
            {
                ISkyrimModGetter ov;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail($"cannot open donor '{name}' to merge it ({WriteEngine.Describe(ex)}) — nothing written.");
                }
                overlays.Add((IDisposable)ov);
                mods.Add((name, ov));
            }
            mr = RemapEngine.MergeModsInto(m, mods, dict);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort */ } } }
        if (!mr.Success) return MergeBuildResult.Fail(mr.Error!);

        // 2. Donor-master-survives check (Q3): after RemapLinks, NO link may still point into a donor. One that does is
        //    a reference to a FormID the donor never DEFINED (a dangling source ref — the dict maps every real donor key),
        //    and serializing it would re-declare the donor as a master of its own merge. Named, never silent.
        var dangling = new List<string>();
        int danglingCount = 0;
        foreach (var rec in m.EnumerateMajorRecords())
            foreach (var link in rec.EnumerateFormLinks())
                if (!link.FormKey.IsNull && donorSet.Contains(link.FormKey.ModKey))
                {
                    danglingCount++;
                    if (dangling.Count < 10) dangling.Add($"{rec.FormKey} → {link.FormKey}");
                }
        if (danglingCount > 0)
            return MergeBuildResult.Fail(
                $"refused — {danglingCount} reference(s) in the merged content still point INTO a donor after the renumber. " +
                "Each targets a FormID its donor never DEFINES (a dangling reference already broken in the source), so it cannot " +
                $"be remapped, and writing it would keep the donor as a master of its own merge. Fix the source (xEdit: check for " +
                $"deleted/injected records) or drop that donor. Samples: {string.Join("; ", dangling)}. Nothing was written.");

        // 3. NextObjectID above the highest merged id (header metadata for the CK's next new record; write floor minimum,
        //    ceiling-clamped — a donor legitimately holding 0xFFFFFF would otherwise push it past the 24-bit object range).
        uint maxUsed = 0;
        foreach (var nk in dict.Values) if (nk.ID > maxUsed) maxUsed = nk.ID;
        m.ModHeader.Stats.NextFormID = Math.Min(FormIdRange.ObjectIdMax, Math.Max(FormIdRange.EslWindowFloor, maxUsed + 1));

        // 4. Resolve the computed master set to overlays (absence OR unparseability is a loud refusal — an open throw
        //    escaping here would skip the caller's rider-folder cleanup) + the master-aware serialize.
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
                try { mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail(
                        $"cannot merge: donor master '{mfn}' could not be opened for the serialize ({WriteEngine.Describe(ex)}). Nothing was written.");
                }
                masterOverlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            try { WriteEngine.WriteInPlace(m, resolved, outPath); }
            catch (Exception ex)
            {
                return MergeBuildResult.Fail(
                    $"writing the merged plugin failed (serialize or commit; nothing partial left): {WriteEngine.Describe(ex)}.");
            }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // 5. Report the masters the written HEADER actually carries (Mutagen lean-derives the list from referenced
        //    content, so a declared-but-unreferenced donor master vanishes here) — the report must match what xEdit
        //    shows, not the pre-computed union. Read-back is best-effort: on a re-open fault, fall back to the union.
        IReadOnlyList<string> writtenMasters = masters;
        long bytes = 0;
        try
        {
            bytes = new FileInfo(outPath).Length;
            using var wr = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            writtenMasters = wr.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
        }
        catch { /* best-effort read-back; the union is a correct superset */ }
        return new MergeBuildResult(true, null, writtenMasters, mr.RecordsCopied, mr.RecordsRenumbered, mr.Conflicts, bytes);
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
    ///
    /// <paramref name="inPlaceTarget"/> (non-null) switches to the IN-PLACE lane (Wave 1b): <paramref name="outPath"/> IS
    /// the target plugin's real on-disk path and the records are allocated INTO it + the whole plugin re-serialized over
    /// itself (model C, <see cref="WriteEngine.WriteInPlace"/>) instead of a new patch — full create parity, incl. nesting
    /// under a parent the target doesn't itself own (the parent is overridden IN, exactly as the patch lane does into a new
    /// patch; a parent the target DOES own is sourced from the target so its content is preserved). Every in-place fork is
    /// additive + gated on this param: the patch lane (inPlaceTarget null) is behaviourally unchanged.
    /// </summary>
    public static CreateOutcome CreateRecords(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string outPath, bool extend, bool fullReadback = false, string? inPlaceTarget = null)
    {
        if (specs.Count == 0) return CreateOutcome.Fail("no records to create supplied.");
        bool inPlace = inPlaceTarget is not null;

        // Per-call overlay session (Option B): the known-master set for the serialize is opened through it and disposed
        // when the method returns — no handle held at rest. The view is captured up front (the in-place Phase-0 guard needs
        // it; the patch lane uses it identically in Phase 1).
        using var session = resolver.OpenSession();
        var view = resolver.Capture();

        // --- Phase 0: open the destination FIRST — moved AHEAD of pre-flight so a FormKey parent can resolve from it (a
        //     parent created in a PRIOR into= call, or — in place — a parent the target itself owns). CreateFromBinary reads
        //     the file fully into memory and holds NO handle at rest (the active-patch self-lock invariant is untouched —
        //     Phase 4's ReleaseOverlay + AllMastersExcept still guard the serialize); nothing is mutated until Phase 3 and
        //     nothing serialized until Phase 4, so all-or-nothing holds. IN-PLACE: the destination IS the target plugin. ---
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (inPlace)
        {
            // The target must be an active, fully-parseable plugin (the excluded-plugin guard, same as ApplyInPlace):
            // houseCARL won't re-serialize a plugin it can't fully parse — that would risk DROPPING a record it couldn't read (Q3).
            if (!view.ContainsPlugin(inPlaceTarget!))
                return CreateOutcome.Fail($"in-place target '{inPlaceTarget}' is not an active plugin in the load order.");
            if (view.ExcludedPlugins.TryGetValue(inPlaceTarget!, out var excluded))
                return CreateOutcome.Fail(
                    $"cannot create into '{inPlaceTarget}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                    "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"in-place target '{fileName}' not found on disk at {outPath} — the file is untouched.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex)
                { return CreateOutcome.Fail($"cannot open '{fileName}' to create into it in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        }
        else if (extend)
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
            return CreateOutcome.Fail($"{(inPlace ? "in-place" : "patch")} ModKey '{patchMod.ModKey.FileName}' must match {(inPlace ? "the target filename" : "output filename")} '{fileName}'.");

        // --- Phase 1: pre-flight EVERY spec before any mutation (Q3, all-or-nothing). editorid required + unique; the
        //     type must be createable — a FLAT top-level type (CanCreateType), OR a NESTED child given a valid parent
        //     (CanCreateNested): the parent is resolved to its TYPE (an existing parent FormKey's load-order winner, a
        //     record created EARLIER in this same call — the one-shot order rule — or a record the PATCH being extended
        //     already carries, from a prior into= call), and the child must nest under it by construction (§1.4 Q2). Every
        //     edit is validated by the rulebook rooted at the create type. The new FormID isn't known until allocation
        //     (Phase 3), so creatability is STRUCTURAL; a FormKey parent is resolved here only to learn its TYPE + stash
        //     the route to make it settable in Phase 3. ONE captured build answers every parent resolve (hunt-F5). ---
        var problems = new List<string>();
        var seenEdid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredEdidType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // editorid -> RecordType (same-call sibling parents)
        // editorids declared in EARLIER specs PLUS the current one — the legal targets of a "@editorid" same-call
        // field ref (HCBR Layer B unit A). Grown as each spec DECLARES its editorid (before its edits validate), so
        // during spec i's edit validation it holds {0..i}: an earlier sibling AND the record itself (self-reference —
        // HCBR-2026-07-10-01: a quest's VMAD fragment points at its own quest; apply registers the record in
        // createdByEditorId before applying its edits, so the substitution timing already holds). A forward-ref to a
        // LATER sibling still rejects loud.
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
            priorEditorIds.Add(s.EditorId);   // declared ⇒ referenceable by its own edits (self) and by LATER specs

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
                    // IN-PLACE target-owned parent (the create-side of the edit lane's content-source guard): if the TARGET
                    // itself carries the parent (defines or overrides it), use ITS OWN copy directly — preserve the user's
                    // parent content + just add the child. The winner-source path below would instead override the load-order
                    // WINNER in, clobbering the user's content for a parent they own but don't win. (Patch lane: inPlace false
                    // => skips this; the prior-into= patchMod-carries branch still serves it, unchanged.)
                    if (inPlace && patchMod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == parentFk) is { } ownParent)
                    {
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(ownParent.GetType().Name));
                        parentPlans[i] = (null, null, null, ownParent);
                    }
                    else if (view.ResolveWinner(parentFk) is { } w)
                    {
                        // An EXISTING load-order parent (a topic/cell from a master or mod): override it INTO the destination
                        // in Phase 3. In place, this is the FOREIGN-parent case — the parent the target doesn't own — and
                        // overriding it in to host the child is exactly what the patch lane does into a new patch (correct,
                        // necessary nesting, NOT injection: the user explicitly named the parent; the override is reported).
                        var parentBody = view.GetRecord(session, w.WinnerPlugin, parentFk);
                        if (parentBody is null) { problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} winner '{w.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(parentBody.GetType().Name));
                        parentPlans[i] = (parentBody, w.WinnerPlugin, null, null);
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
                        // Genuinely absent from the load order AND the destination — the surviving loud refusal (Q3, never a
                        // misleading "wrong FormID"). Name the one-call workaround for the common new-topic case.
                        problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} is not present in the load order"
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
                // declared in an EARLIER spec of THIS call OR is the record itself (resolved to its real FormKey in
                // Phase 3); else rejected loud.
                if (rulebook.Validate(req, priorEditorIds) is { } reject) problems.Add($"{s.RecordType} '{s.EditorId}' [{Label(req)}]: {reject}");
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
                // Resolve every same-call reference (@editorid) to its now-allocated FormKey (HCBR Layer B unit A —
                // the INFO PNAM chain + Topic back-link in one bulk_create; HCBR-2026-07-10-01 — self-reference +
                // compose-struct refs, e.g. a quest's VMAD fragment pointing at its own quest). Pre-flight (Phase 1)
                // already guaranteed any surviving @-token is on a FormLink target AND names a record declared no
                // later than this spec — the current record was registered in createdByEditorId just above, so SELF
                // resolves too; a miss here is a real engine inconsistency, surfaced not swallowed (Q3). The
                // substituted value is a normal intra-patch FormKey that ApplyVerb coerces exactly as a literal
                // FormID would (no apply-path change). Slots that carry a ref: the SINGULAR req.Value, req.Values
                // (ReplaceAll on a link list), and — recursively — a compose Struct's formlink Fields values and
                // nested Sets (ResolveSiblingRefs walks all of them).
                var (req, refErr) = ResolveSiblingRefs(rawReq, createdByEditorId, $"new {s.RecordType} '{s.EditorId}'");
                if (refErr is not null) return CreateOutcome.Fail(refErr);
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
            // #131 — auto-fill the DialogTopic SNAM subtype marker. A new topic with a Subtype but a blank SNAM marker
            // (the default when only Subtype is set — or nothing, which defaults to Custom) is a load CTD: the engine
            // buckets topics by the 4-char marker, and a new topic with a blank one walks an invalid list. This
            // COMPLETES the write the author under-specified (never overriding an explicit marker) and surfaces it as
            // an op — auto-filled, not silent (Q3). The authority + why-not-derivable live in DialogueSubtype.
            if (rec is IDialogTopic dtopic)
            {
                switch (DialogueSubtype.NormalizeMarker(dtopic, out var marker))
                {
                    case MarkerFill.Filled:
                        ops.Add(new OpResult(rec.FormKey, s.RecordType,
                            $"SubtypeName (SNAM subtype marker) auto-set to {marker}", true, null,
                            $"{marker} — derived from Subtype={dtopic.Subtype}; a new topic with a blank marker is a load CTD (#131)"));
                        break;
                    case MarkerFill.Unmodeled:
                        // Fail loud, never ship a silent blank (Q3 + the cornerstone's "fail loud on a Mutagen/xEdit
                        // delta"): the ONLY way here is a Subtype outside the modeled 0..N (an out-of-range enum value
                        // that coerced past pre-flight, or a future Mutagen addition the table doesn't cover yet). We
                        // can't derive its marker and a blank SNAM is malformed — refuse with actionable guidance
                        // rather than write a crash-prone record (nothing serialized; the guard pins that every real
                        // enum value IS modeled, so this only bites genuinely-out-of-range input).
                        return CreateOutcome.Fail(
                            $"cannot create DialogTopic '{s.EditorId}': no SNAM subtype marker is modeled for Subtype={dtopic.Subtype} " +
                            $"((int){(int)dtopic.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). A blank marker is malformed " +
                            "(a new topic with a blank marker is a load CTD, #131). Use a valid Subtype, or set SubtypeName explicitly to the correct 4-char marker (nothing created).");
                    // AlreadySet: an explicit marker the author set — never overridden, nothing to report.
                }

                // CK-parity seed (S2 — the byte-only tier): DIAL Priority (PNAM). Priority is a NON-NULLABLE float
                // (defaults to 0), so "the author left it unset" is NOT is-null — it's "no edit touched the Priority
                // path." Compute that from this record's op list and let DialogueCkParity seed the CK's 50 only when
                // Priority was never mentioned; an explicit value (including 0) always wins. Surfaced as an op (Q3).
                bool authorSetPriority = s.Edits.Any(e => e.Path.Length >= 1 &&
                    string.Equals(e.Path[0], "Priority", StringComparison.OrdinalIgnoreCase));
                if (DialogueCkParity.ApplyTopicPriorityDefault(dtopic, authorSetPriority) is { } pfill)
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, pfill.Label, true, null, pfill.Reason));
            }
            // CK-parity default-populate (S1 confirmed-CK-crash tier + S2 byte-only tier; same #131 asymmetry across
            // the whole DIAL/INFO/DLVW/DLBR/QUST family: Mutagen omits null optionals, the CK writes them
            // unconditionally). An INFO created without CNAM (FavorLevel) / ENAM (Flags) crashes the CK when its topic
            // is opened; a bare DLVW crashes the CK Dialogue Views editor; the S2 fields (DLBR Category, QUST
            // NextAliasID + objective Flags) are byte-parity only (no crash) but complete the write the same way.
            // These COMPLETE the write the author under-specified (never overriding an explicit value) and surface each
            // fill as an op — auto-filled, not silent (Q3). The authority + by-construction values live in
            // DialogueCkParity (else-if: a record is exactly one of these types).
            else if (rec is IDialogResponses infoRec)
            {
                foreach (var fill in DialogueCkParity.ApplyInfoDefaults(infoRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IDialogView viewRec)
            {
                foreach (var fill in DialogueCkParity.ApplyViewDefaults(viewRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IDialogBranch branchRec)   // S2 — DLBR Category (TNAM); S3 — DLBR Flags (DNAM), #212
            {
                foreach (var fill in DialogueCkParity.ApplyBranchDefaults(branchRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IQuest questRec)           // S2 — QUST NextAliasID (ANAM) + objective Flags (FNAM)
            {
                foreach (var fill in DialogueCkParity.ApplyQuestDefaults(questRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
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
        try
        {
            if (inPlace)
                // Model C (the Wave 0 probe's incantation): re-emit the WHOLE target over itself — the author's counter
                // preserved (NoNextFormIDProcessing, no re-floor; the allocation already floored+advanced it), no baseline
                // force-include. Handed the SAME whole-master set as WritePatch, so a new record's cross-mod reference (incl.
                // an overridden-in foreign parent) resolves + pulls its master into the lean derived header — xEdit-parity.
                WriteEngine.WriteInPlace(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath);
            else
                WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath);
        }
        catch (Exception ex) { return CreateOutcome.Fail($"writing {(inPlace ? $"'{fileName}' in place" : "the patch")} after create failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

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

        return new CreateOutcome(true, null, outPath, extend, created, masters, bytes) { ReadBack = readBack, InPlace = inPlace };
    }

    /// <summary>Create BRAND-NEW records IN PLACE inside an EXISTING plugin the user owns (in-place write lane, Wave 1b) —
    /// the create sibling of <see cref="ApplyInPlace"/>, and the in-place ENTRY POINT into the shared
    /// <see cref="CreateRecords"/> core: it forwards with <paramref name="targetName"/> as the in-place target, so the FULL
    /// create capability — flat, nested (incl. under a parent the target doesn't own, overridden in to host the child), and
    /// cells — runs the SAME proven path as the patch lane, pointed at <paramref name="targetPath"/> and serialized model-C
    /// over the file itself (<see cref="WriteEngine.WriteInPlace"/>). The created-record verify
    /// (<paramref name="fullReadback"/>) defaults ON. CONSENT + the persistent acknowledge handshake are enforced by the
    /// SERVICE before this is reached.</summary>
    public static CreateOutcome CreateRecordsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string targetPath, string targetName, bool fullReadback = true)
        => CreateRecords(resolver, rulebook, specs, targetPath, extend: false, fullReadback, inPlaceTarget: targetName);

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

    /// <summary>Resolve every same-call <c>@editorid</c> reference in a request to the referenced record's allocated
    /// FormKey — the singular <see cref="WriteRequest.Value"/>, each <see cref="WriteRequest.Values"/> entry, and
    /// (HCBR-2026-07-10-01) a compose <see cref="WriteRequest.Struct"/>'s formlink Fields values + nested Sets,
    /// recursively. WriteRequest/StructSpec are init-only, so substitution clones; the ORIGINAL instance is returned
    /// untouched when nothing needed resolving (the common no-token path allocates nothing). A token naming a record
    /// not in <paramref name="created"/> is a real engine inconsistency (pre-flight gates the declared-earlier-or-self
    /// rule) — returned as <c>error</c>, surfaced not swallowed (Q3).</summary>
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
        // P8a: a composes= op carries a LIST of specs; in create context each may @editorid-reference a same-call
        // sibling, so resolve every element the same clone-only-on-change way, fail loud on a miss (Q3).
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

    /// <summary>The <see cref="StructSpec"/> half of <see cref="ResolveSiblingRefs"/>: substitute <c>@editorid</c>
    /// tokens in the spec's flat Fields values and recurse through its nested Sets (a struct element whose own field
    /// is a struct element resolves for free). Same clone-only-on-change + fail-loud-on-miss contract.</summary>
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

    /// <summary>Best-effort read-back of the edited leaf off the override (so the caller sees the value landed without a
    /// follow-up read). Reads the leaf PATH (not the keyed element — that's xEdit's job); null on any difficulty — never
    /// load-bearing, never throws into the write result.</summary>
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

    /// <summary>Read the edited leaf ONCE and derive BOTH best-effort descriptors an edit-lane <see cref="OpResult"/>
    /// carries (PR #127 review #1 — previously two identical reflective reads of the same leaf per op, doubling the very
    /// readback cost on the large-list records the report was about). <c>After</c> = the leaf read-back (xEdit remains
    /// the authority). <c>Landed</c> = the compact "what landed" line the in-place verify renders by default
    /// (HCBR-2026-06-28-01): a SCALAR leaf reuses the value just read (names exactly what was set); a LIST/DICT leaf names
    /// the touched element + new count via <see cref="ReadEngine.TouchedElement"/> (the element you Added/Set, NOT the
    /// whole list), falling back to the container summary. Read-only; NEVER throws into the write result (both null on any
    /// difficulty). The create lane keeps <see cref="TryReadAfter"/> (it needs only <c>After</c>, never <c>Landed</c>).</summary>
    static (string? After, string? Landed) DescribeApplied(IMajorRecord ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf }, containerHint: null);   // same: no depth= on the write surface, don't hint it
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            if (f is null) return (null, null);
            var after = f.HasValue ? f.Token : f.Note;
            // Scalar: Landed reuses the token just read. List/dict: name the touched element (+ new count); else the summary.
            var landed = f.HasValue ? f.Token : (ReadEngine.TouchedElement(ov, req.Path, req.Verb, req.Key) ?? f.Note);
            return (after, landed);
        }
        catch { return (null, null); }
    }
}

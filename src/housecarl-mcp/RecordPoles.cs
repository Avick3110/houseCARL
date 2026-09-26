using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

public sealed partial class LoadOrderService
{
    // ---- comparison poles and the delta/tree batches --------------------------------------------------

    /// <summary>Which pole a source= or versus= expression names.</summary>
    public enum PoleKind { Winner, Named, PreviousProvider, Overlay }

    /// <summary>A parsed pole expression, from the wire spelling into this engine value.</summary>
    public sealed record PoleSpec(PoleKind Kind, string? Plugin = null, string? Mod = null, string? OverlayState = null,
                                  SkyPatcherDraft.Plan? Draft = null)
    {
        public static readonly PoleSpec Winner = new(PoleKind.Winner);
        /// <summary>The arm statement a render leads with when the pole is uniform across the batch.</summary>
        public string Label => Kind switch
        {
            PoleKind.Winner => "winner",
            PoleKind.PreviousProvider => "previous_provider (the provider immediately below the subject, per record)",
            PoleKind.Overlay => $"skypatcher overlay ({OverlayState})" + (Draft is null ? "" : $", with {Draft.Arm}"),
            _ => Plugin ?? "?",
        };
    }

    /// <summary>One record's delta: subject pole versus reference pole, compared by <see
    /// cref="FieldsDiff"/>.</summary>
    public sealed record DeltaRow(string Formid, DiffPole? Subject, DiffPole? Reference, FieldsDiff.Result? Diff,
                                  IReadOnlyList<string>? StackAbove, string? Note, string? Error);

    /// <summary>The project=delta batch: every pole of every record resolves against ONE captured build, so a
    /// comparison can never span two.</summary>
    public IReadOnlyList<DeltaRow> DeltaBatch(
        IReadOnlyList<string> formids, PoleSpec subject, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? subjectArm, out string? referenceArm, out bool epochCoversAll,
        out string? refusal, out OrderStamp? epoch, SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        subjectArm = null; referenceArm = null; epochCoversAll = true; refusal = null;
        var (pin, roots) = Host.CapturePinAndRoots(AfterReadPinForGuard);   // one build and one set of roots for every pole of every record
        var resolver = pin.Resolver;
        var view = pin.View;
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<DeltaRow>();
        }
        using var session = resolver.OpenSession();

        // Pre-parse the keys so an off-order pole's cache materializes only the requested records.
        var parsed = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wanted = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsed.Add((raw, fk0, null)); wanted.Add(fk0); }
            catch (Exception ex) { parsed.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        // Resolve the uniform arms once; winner and overlay are per-record but uniform in statement.
        var sGather = new PoleGather();
        var rGather = new PoleGather();
        var sReader = MakePoleReader(view, roots, session, subject, fields, wanted, out subjectArm, out var sCovers, out var sErr, out var sOffOrder, overlayWarnings, sGather);
        if (sErr is not null) { refusal = "source: " + sErr; return Array.Empty<DeltaRow>(); }
        var rReader = MakePoleReader(view, roots, session, reference, fields, wanted, out referenceArm, out var rCovers, out var rErr, out _, overlayWarnings, rGather);
        if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<DeltaRow>(); }
        epochCoversAll = sCovers && rCovers;

        // previous_provider is measured from the SUBJECT's position in the active touching stack, which an
        // off-order subject holds in no record. That is a fact about the arm, not about any record.
        if (reference.Kind == PoleKind.PreviousProvider && sOffOrder is not null)
        {
            refusal = $"versus: the subject is the off-order file '{sOffOrder.Plugin}' ({sOffOrder.Where}), which holds no position in the " +
                      "active touching stack previous_provider is measured in. Name an active plugin as source=, or compare against a named versus= plugin.";
            return Array.Empty<DeltaRow>();
        }

        var rows = new List<DeltaRow>(formids.Count);
        // A chunk of rows at a time, so each pole walks a plugin once for the whole chunk.
        for (int start = 0; start < parsed.Count; start = ChunkEnd(start, parsed.Count))
        {
            int end = ChunkEnd(start, parsed.Count);
            var chunkKeys = new List<FormKey>(end - start);
            for (int i = start; i < end; i++) if (parsed[i].Fk is { } k) chunkKeys.Add(k);
            sGather.Open(view, session, chunkKeys, _ => null);
            // previous_provider is measured FROM the subject, so the reference's declaration needs the plugin the
            // subject resolved to.
            var subjects = new string?[chunkKeys.Count];
            for (int j = 0; j < chunkKeys.Count; j++) subjects[j] = sGather.PluginOf?.Invoke(chunkKeys[j], null);
            rGather.Open(view, session, chunkKeys, j => subjects[j]);

            for (int i = start; i < end; i++)
            {
                var (raw, fkOpt, parseError) = parsed[i];
                if (parseError is not null) { rows.Add(new DeltaRow(raw?.Trim() ?? "", null, null, null, null, null, parseError)); continue; }
                var fk = fkOpt!.Value;

                var s = sReader(fk, null);
                if (s.Error is not null) { rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, null, null, null, null, "subject: " + s.Error)); continue; }
                // previous_provider is measured from the SUBJECT, so the reference reader is handed the subject's
                // resolved plugin for this record.
                var r = rReader(fk, s.Pole!.Plugin);
                if (r.Error is not null) { rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, r.Pole, null, r.StackAbove, null, "versus: " + r.Error)); continue; }

                string? note = string.Equals(s.Pole.Plugin, r.Pole!.Plugin, StringComparison.OrdinalIgnoreCase) && s.Pole.Where == r.Pole.Where
                    ? "the two poles resolved to the SAME provider — the diff is trivially empty by construction"
                    : null;
                // Two copies of one filename on opposite arms: the delta line names the off-order side's mod
                // folder, or the reader cannot tell which side a value came from.
                var diff = FieldsDiff.Compare(s.Fields!, r.Fields!, referenceLabel: r.Pole.LabelVersus(s.Pole.Plugin));
                rows.Add(new DeltaRow(FormIdToken.Of(fk), s.Pole, r.Pole, diff, r.StackAbove, note, null));
            }
        }
        return rows;
    }

    /// <summary>A pole reader's per-record result: the deep-read fields plus the pole identity for the render, or
    /// a per-item error. <see cref="StackAbove"/> names what outranks the subject.</summary>
    internal sealed record PoleReading(RecordFields? Fields, DiffPole? Pole, IReadOnlyList<string>? StackAbove, string? Error);

    internal delegate PoleReading PoleReader(FormKey fk, string? subjectPlugin);

    /// <summary>Build the per-record reader for one pole against the shared captured view and session; uniform arm
    /// resolution happens here once and per-record work stays in the returned reader.</summary>
    PoleReader MakePoleReader(LoadOrderResolver.IndexView view, Mo2Roots roots, LoadOrderResolver.OverlaySession session,
                              PoleSpec spec, IReadOnlyList<string>? fields, IReadOnlyCollection<FormKey>? wanted,
                              out string? armStatement, out bool covers, out string? error, out PoleInfo? offOrderArm,
                              SkyPatcherOverlay.WarningSink? overlayWarnings = null, PoleGather? gather = null)
    {
        error = null; covers = true; offOrderArm = null;
        // '*parent' on fields=: every in-order arm reads through this captured view and open session.
        var hop = ContainmentIndex.ReadHop(view, session);
        switch (spec.Kind)
        {
            case PoleKind.Winner:
                armStatement = "winner";
                if (gather is not null) gather.PluginOf = (fk, _) => view.ResolveWinner(fk)?.WinnerPlugin;
                return (fk, _) =>
                {
                    var w = view.ResolveWinner(fk);
                    if (w is null)
                        return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                    var body = gather is { Live: true } ? gather.Body(w.Value.WinnerPlugin, fk)
                                                        : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the winner body of {FormIdToken.Of(fk)} could not be read from '{w.Value.WinnerPlugin}'.");
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                           new DiffPole(w.Value.WinnerPlugin, "winner (active order)", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                };

            case PoleKind.PreviousProvider:
                // Subject-relative: resolved per record against the touching list, anchored on the plugin the
                // subject resolved to. Always active-order, since the touching list is the order's.
                armStatement = spec.Label;
                if (gather is not null) gather.PluginOf = (fk, subjectPlugin) =>
                {
                    if (subjectPlugin is null) return null;
                    var t = view.TouchingPlugins(fk);
                    if (t is null) return null;
                    for (int i = 1; i < t.Count; i++)                    // i starts at 1: the bottom provider has nothing beneath it
                        if (string.Equals(t[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) return t[i - 1];
                    return null;
                };
                return (fk, subjectPlugin) =>
                {
                    var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                    if (touchers.Count == 0)
                        return new PoleReading(null, null, null, $"no active plugin touches {FormIdToken.Of(fk)} — there is no provider stack to measure previous_provider in.");
                    int idx = -1;
                    for (int i = 0; i < touchers.Count; i++)
                        if (string.Equals(touchers[i], subjectPlugin, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
                    if (idx < 0)   // the subject doesn't touch the record at all
                        return new PoleReading(null, null, null,
                            $"the subject '{subjectPlugin}' does not touch {FormIdToken.Of(fk)}, so it has no position to measure previous_provider from. " +
                            $"Touched by (active order, winner last): {string.Join(", ", touchers)}.");
                    if (idx == 0)  // the subject IS the origin; never a silent empty diff
                        return new PoleReading(null, null, null,
                            $"no previous provider — '{subjectPlugin}' DEFINES {FormIdToken.Of(fk)} (bottom of the touching list); there is nothing beneath it to compare against.");
                    var refPlugin = touchers[idx - 1];
                    var body = gather is { Live: true } ? gather.Body(refPlugin, fk) : view.GetRecord(session, refPlugin, fk);
                    if (body is null)
                        return new PoleReading(null, null, null, $"the previous provider '{refPlugin}' of {FormIdToken.Of(fk)} could not be read.");
                    // Mid-stack subject: what sits above is surfaced as neutral fact, never advice.
                    IReadOnlyList<string>? above = idx < touchers.Count - 1 ? touchers.Skip(idx + 1).ToList() : null;
                    return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                           new DiffPole(refPlugin, $"previous provider (immediately below '{subjectPlugin}')", true,
                                                        RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), above, null);
                };

            case PoleKind.Overlay:
                return MakeOverlayPoleReader(view, session, spec, fields, out armStatement, out covers, out error, overlayWarnings, gather);

            default:   // Named — the one-pole rule: active in the order, else an on-disk file.
                var (arm, armErr) = ResolvePoleArm(view, roots, spec.Plugin!, spec.Mod);
                if (armErr is not null) { armStatement = null; error = armErr; return (_, _) => new PoleReading(null, null, null, armErr); }
                armStatement = $"{arm!.Plugin} — {arm.Where}";
                if (arm.InOrder)
                {
                    if (view.ExcludedPlugins.TryGetValue(arm.Plugin, out var why))
                    {
                        var exclMsg = $"'{arm.Plugin}' was excluded from this session ({why}) — its records aren't resolvable.";
                        error = exclMsg;
                        return (_, _) => new PoleReading(null, null, null, exclMsg);
                    }
                    if (gather is not null) gather.PluginOf = (_, _) => arm.Plugin;
                    return (fk, _) =>
                    {
                        var body = gather is { Live: true } ? gather.Body(arm.Plugin, fk) : view.GetRecord(session, arm.Plugin, fk);
                        if (body is null)
                        {
                            // Name the actual touchers, never a silent absence.
                            var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                            return new PoleReading(null, null, null,
                                $"'{arm.Plugin}' does not define or override {FormIdToken.Of(fk)} — it has no version of this record. " +
                                (touchers.Count > 0
                                    ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                    : "No active plugin touches it either."));
                        }
                        return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                               new DiffPole(arm.Plugin, arm.Where, true,
                                                            RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
                    };
                }
                // Off-order arm: open the overlay lazily once; per-record lookups sweep it on first use and
                // memoise every record seen, so one enumeration pass serves the whole batch.
                covers = false;   // the file's content sits outside the epoch fingerprint
                offOrderArm = arm;
                var lazy = new OffOrderPoleCache(arm, fields, wanted);
                return (fk, _) =>
                {
                    var (rec, oerr) = lazy.Find(fk);
                    if (oerr is not null) return new PoleReading(null, null, null, oerr);
                    if (rec is null)
                    {
                        var touchers = view.TouchingPlugins(fk) ?? Array.Empty<string>();
                        return new PoleReading(null, null, null,
                            $"file '{arm.Plugin}' ({arm.Where}) does not define or override {FormIdToken.Of(fk)} — it has no version of this record. " +
                            (touchers.Count > 0
                                ? $"Touched by (active order, winner last): {string.Join(", ", touchers)}."
                                : "No active plugin touches it either."));
                    }
                    return new PoleReading(rec, new DiffPole(arm.Plugin, arm.Where, false, rec.Type, rec.EditorId)
                                                { Qualifier = arm.Layer ?? "off-order" }, null, null);
                };
        }
    }

    /// <summary>The SkyPatcher-overlay pole.</summary>
    PoleReader MakeOverlayPoleReader(LoadOrderResolver.IndexView view, LoadOrderResolver.OverlaySession session,
                                     PoleSpec spec, IReadOnlyList<string>? fields,
                                     out string? armStatement, out bool covers, out string? error,
                                     SkyPatcherOverlay.WarningSink? overlayWarnings = null, PoleGather? gather = null)
    {
        error = null;
        var hop = ContainmentIndex.ReadHop(view, session);   // both overlay arms read through the order's own index
        var state = (spec.OverlayState ?? "post").Trim().ToLowerInvariant();
        if (state is not ("pre" or "post"))
        {
            armStatement = null; covers = true;
            error = $"overlay state '{spec.OverlayState}' is not recognized — use \"pre\" (the winner before the INI layer) or \"post\" (after it; the default).";
            var msg = error;
            return (_, _) => new PoleReading(null, null, null, msg);
        }

        if (state == "pre")
        {
            covers = true;
            armStatement = "skypatcher overlay (pre) — the plain load-order winner, before the INI layer";
            if (gather is not null) gather.PluginOf = (fk, _) => view.ResolveWinner(fk)?.WinnerPlugin;
            return (fk, _) =>
            {
                var w = view.ResolveWinner(fk);
                if (w is null) return new PoleReading(null, null, null, UnresolvedFormId(view, fk));
                var body = gather is { Live: true } ? gather.Body(w.Value.WinnerPlugin, fk)
                                                     : view.GetRecord(session, w.Value.WinnerPlugin, fk);
                if (body is null) return new PoleReading(null, null, null, $"the winner body of {FormIdToken.Of(fk)} could not be read from '{w.Value.WinnerPlugin}'.");
                return new PoleReading(ReadEngine.ReadFields(body, fields, ConflictDiffDepth, parentOf: hop),
                                       new DiffPole(w.Value.WinnerPlugin, "skypatcher overlay (pre) = winner", true,
                                                    RecordNaming.StripOverlay(body.GetType().Name), body.EditorID), null, null);
            };
        }

        covers = false;   // the INI layer's files are outside the index fingerprint (a draft INI likewise)
        armStatement = "skypatcher overlay (post) — the winner after the SkyPatcher INI layer replays"
                     + (spec.Draft is null ? "" : $", with {spec.Draft.Arm}");
        // The replay context is built lazily once for the whole batch.
        AssetLayers.SkyPatcherReplay? replay = null;
        // Per-key memo: the scratch mod is shared across the reader's lifetime, so a repeated key's second replay
        // would re-apply every INI line onto the already-mutated copy.
        var postMemo = new Dictionary<FormKey, PoleReading>();
        string? setupError = null;
        void Setup()
        {
            if (replay is not null || setupError is not null) return;
            try
            {
                replay = Host.AssetArea.OpenSkyPatcherReplay(view, session, out var draftRefusal, spec.Draft, overlayWarnings);
                if (draftRefusal is not null) setupError = draftRefusal;
            }
            catch (Exception ex)
            {
                setupError = $"the SkyPatcher layer could not be discovered for the overlay pole: {ex.Message}";
            }
        }
        // A draft is folded up front rather than on the first record: whether it can be folded at all is a fact
        // about the whole call, so it refuses by name here.
        if (spec.Draft is not null)
        {
            Setup();
            if (setupError is not null) { error = setupError; return (_, _) => new PoleReading(null, null, null, setupError); }
        }
        return (fk, _) =>
        {
            if (setupError is not null) return new PoleReading(null, null, null, setupError);
            if (postMemo.TryGetValue(fk, out var memoized)) return memoized;
            Setup();
            if (setupError is not null) return new PoleReading(null, null, null, setupError);
            var r = replay!.Replay(fk);
            CollectOverlayWarnings(r.Folders, overlayWarnings);
            if (r.Error is not null) return postMemo[fk] = new PoleReading(null, null, null, r.Error);
            // An unpatchable type is an answer, not a failure: the layer cannot touch it, so post IS pre.
            if (r.IsUnpatchable)
                return postMemo[fk] = new PoleReading(ReadEngine.ReadFields(r.Copy!, fields, ConflictDiffDepth, parentOf: hop),
                                       new DiffPole(r.WinnerPlugin!,
                                                    "skypatcher overlay (post) = winner — type not SkyPatcher-patchable, the layer cannot touch it",
                                                    true, RecordNaming.StripOverlay(r.Copy!.GetType().Name), r.EditorId), null, null);
            int applied = r.Folders.Where(f => f.Result is not null).Sum(f => f.Result!.Applied.Count);
            var post = ReadEngine.ReadFields(r.Copy!, fields, ConflictDiffDepth, parentOf: hop);
            return postMemo[fk] = new PoleReading(post,
                new DiffPole(r.WinnerPlugin!, $"skypatcher overlay (post) — {applied} op(s) applied onto the winner", true,
                             post.Type, r.EditorId), null, null);
        };
    }

    /// <summary>The off-order pole's lazy single-pass cache: opens the file's overlay on first lookup and sweeps it
    /// once, materialising every wanted record's deep fields as a value snapshot.</summary>
    sealed class OffOrderPoleCache
    {
        readonly PoleInfo _arm;
        readonly IReadOnlyList<string>? _fields;
        readonly HashSet<FormKey>? _wanted;   // materialize only the requested keys, never the whole file
        Dictionary<FormKey, RecordFields>? _all;
        string? _error;

        public OffOrderPoleCache(PoleInfo arm, IReadOnlyList<string>? fields, IReadOnlyCollection<FormKey>? wanted)
        { _arm = arm; _fields = fields; _wanted = wanted is null ? null : new HashSet<FormKey>(wanted); }

        public (RecordFields? Fields, string? Error) Find(FormKey fk)
        {
            if (_error is not null) return (null, _error);
            if (_all is null && Sweep() is { } err) { _error = err; return (null, err); }
            return (_all!.TryGetValue(fk, out var rec) ? rec : null, null);
        }

        string? Sweep()
        {
            ISkyrimModGetter ov;
            try { ov = LoadOrderResolver.OpenOverlay(_arm.Path!, string.IsNullOrEmpty(_arm.DataDir) ? null : _arm.DataDir); }
            catch (Exception ex) { return $"could not open '{_arm.Path}' as a Skyrim plugin: {ex.Message}"; }
            try
            {
                var all = new Dictionary<FormKey, RecordFields>();
                foreach (var r in ov.EnumerateMajorRecords())
                {
                    if (_wanted is not null && !_wanted.Contains(r.FormKey)) continue;   // one pass, only what was asked
                    if (!all.ContainsKey(r.FormKey))
                        all[r.FormKey] = ReadEngine.ReadFields(r, _fields, ConflictDiffDepth);
                    if (_wanted is not null && all.Count == _wanted.Count) break;
                }
                _all = all;
                return null;
            }
            catch (Exception ex) { return $"file '{_arm.Plugin}' could not be fully read — a record Mutagen cannot parse: {ex.Message}"; }
            finally { (ov as IDisposable)?.Dispose(); }
        }
    }

    /// <summary>The list-driven `records` read under the SkyPatcher-overlay post source: each record's winner is
    /// replayed through the discovered INI layer and the replayed body is what the projection reads.</summary>
    public IReadOnlyList<ReadOutcome> OverlayPostBatch(
        IReadOnlyList<string> formids, IReadOnlyList<string>? fields, int depth, bool resolveNames,
        ArtifactDemand? demand, out string? refusal, out OrderStamp? refusalEpoch, out OrderStamp? epoch,
        string? containerHint = ReadEngine.DepthExpandHint,
        IReadOnlyList<int>? depths = null,
        CancellationToken ct = default,
        SkyPatcherDraft.Plan? draft = null,
        SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        refusal = null; refusalEpoch = null;
        var resolver = Host.Resolver;
        var view = resolver.Capture();
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        var pin = new ViewPin(resolver, view);
        using var session = resolver.OpenSession();

        AssetLayers.SkyPatcherReplay? replay;
        string? draftRefusal;
        try
        {
            replay = Host.AssetArea.OpenSkyPatcherReplay(view, session, out draftRefusal, draft, overlayWarnings);
        }
        catch (Exception ex)
        {
            refusal = $"the SkyPatcher layer could not be discovered for the overlay source: {ex.Message}";
            refusalEpoch = view.Stamp;
            return Array.Empty<ReadOutcome>();
        }
        if (replay is null) { refusal = draftRefusal; refusalEpoch = view.Stamp; return Array.Empty<ReadOutcome>(); }

        // Per-batch replay memo: the scratch mod is shared, so a duplicated key's second replay would run every
        // INI line onto the already-mutated copy. One replay per key.
        var replayMemo = new Dictionary<FormKey, ReadOutcome>();
        LinkMemo? overlayLinkMemo = null;   // resolve_names cache, one per batch
        var outcomes = new List<ReadOutcome>(formids.Count);
        foreach (var raw in formids)
        {
            ct.ThrowIfCancellationRequested();   // a client that aborted stops the replay inside one record
            FormKey fk;
            try { fk = view.ParseFormId(raw); }
            catch (Exception ex) { outcomes.Add(ReadOutcome.Fail(default, $"bad FormID '{raw}': {ex.Message}")); continue; }
            if (replayMemo.TryGetValue(fk, out var memoized)) { outcomes.Add(memoized); continue; }
            var winner = view.ResolveWinner(fk);
            if (winner is null)
            {
                var miss = ReadOutcome.Fail(fk, UnresolvedFormId(view, fk))
                           with { Stamp = view.Stamp, Pin = pin };
                replayMemo[fk] = miss; outcomes.Add(miss);
                continue;
            }
            var r = replay.Replay(fk);
            CollectOverlayWarnings(r.Folders, overlayWarnings);
            if (r.Error is not null)
            {
                var fail = ReadOutcome.Fail(fk, r.Error) with { Stamp = view.Stamp, Pin = pin };
                replayMemo[fk] = fail; outcomes.Add(fail);
                continue;
            }
            // For an unpatchable type the copy is the winner itself: post IS pre.
            var record = ReadEngine.ReadFields(r.Copy!, fields, depth, containerHint, ContainmentIndex.ReadHop(view, session), depths);
            if (resolveNames) record = AnnotateLinks(record, view, session, overlayLinkMemo ??= new LinkMemo());
            var ok = (new ReadOutcome(fk, record, winner.Value.WinnerPlugin, winner.Value.WinnerPlugin,
                                      winner.Value.OverrideDepth, null, null)
                      with { Stamp = view.Stamp, Pin = pin }).WithRuntime(view.RuntimeAddressOf(fk));
            replayMemo[fk] = ok; outcomes.Add(ok);
        }
        return outcomes;
    }

    /// <summary>Carry one replay's warnings into the caller's sink, deduplicated; each already names its own file and line.</summary>
    static void CollectOverlayWarnings(IReadOnlyList<SkyPatcherFolderOutcome> folders, SkyPatcherOverlay.WarningSink? sink)
    {
        if (sink is null) return;
        foreach (var f in folders)
            foreach (var w in f.Result?.Warnings ?? Array.Empty<string>())
                sink.Add(w);
    }

    /// <summary>One provider's node in a project=tree row: its position plus its delta against the row's reference
    /// pole. Empty deltas together with Complete means genuinely identical to the reference.</summary>
    public sealed record TreeNodeDelta(string Plugin, bool IsWinner, bool IsReference,
                                       IReadOnlyList<string> Deltas, int AgreedCount, bool Complete, string? Error);

    /// <summary>One record's project=tree row: every provider in priority order, winner last, each diffed against the
    /// reference pole; a non-null Error is a per-item refusal.</summary>
    public sealed record TreeRow(string Formid, string? Type, string? EditorId,
                                 IReadOnlyList<string> Touchers, string? ReferencePlugin,
                                 IReadOnlyList<TreeNodeDelta> Nodes, string? Error,
                                 IReadOnlyList<ChildDeclarers> ChildDeclarers);

    /// <summary>The project=tree batch: per record, the full provider stack (touching list, winner last) with each
    /// provider diffed against the reference pole — the winner by default, or a named plugin under the one-pole
    /// rule, with untouched records refused by naming the touchers. One captured build for everything.</summary>
    public IReadOnlyList<TreeRow> TreeBatch(
        IReadOnlyList<string> formids, PoleSpec reference, IReadOnlyList<string>? fields,
        ArtifactDemand? demand,
        out string? referenceArm, out bool epochCoversAll, out string? refusal, out OrderStamp? epoch,
        SkyPatcherOverlay.WarningSink? overlayWarnings = null)
    {
        referenceArm = null; epochCoversAll = true; refusal = null;
        var (pin, roots) = Host.CapturePinAndRoots(AfterReadPinForGuard);
        var resolver = pin.Resolver;
        var view = pin.View;
        epoch = view.Stamp;
        if (demand is not null && demand.Epoch != view.Epoch)
        {
            refusal = ArtifactEpochMismatch(demand, view.Epoch);
            return Array.Empty<TreeRow>();
        }
        using var session = resolver.OpenSession();

        // A winner reference reads each node off the tree itself; a named reference goes through the same pole
        // reader the delta form uses. Pre-parsed for the same reason as DeltaBatch.
        var parsedT = new List<(string Raw, FormKey? Fk, string? ParseError)>(formids.Count);
        var wantedT = new HashSet<FormKey>();
        foreach (var raw in formids)
        {
            try { var fk0 = view.ParseFormId(raw); parsedT.Add((raw, fk0, null)); wantedT.Add(fk0); }
            catch (Exception ex) { parsedT.Add((raw, null, $"bad FormID '{raw}': {ex.Message}")); }
        }

        // A named or overlay-pre versus= pole reads one body per row out of ONE plugin, opened on the same chunk
        // boundaries the fold uses below.
        var refGather = new PoleGather();
        PoleReader? refReader = null;
        if (reference.Kind is not PoleKind.Winner)
        {
            refReader = MakePoleReader(view, roots, session, reference, fields, wantedT, out referenceArm, out var rCovers, out var rErr, out _, overlayWarnings, refGather);
            if (rErr is not null) { refusal = "versus: " + rErr; return Array.Empty<TreeRow>(); }
            epochCoversAll = rCovers;
        }
        else referenceArm = "winner";

        // Every row that answers from the INDEX alone is settled here, so the fold's chunks hold only rows that
        // will actually read bodies.
        var rows = new TreeRow[parsedT.Count];
        var liveRow = new List<int>();
        var liveKey = new List<FormKey>();
        var liveTouchers = new List<IReadOnlyList<string>>();
        for (int i = 0; i < parsedT.Count; i++)
        {
            var (raw, fkOpt, parseError) = parsedT[i];
            if (parseError is not null)
            {
                rows[i] = new TreeRow(raw?.Trim() ?? "", null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(), parseError, Array.Empty<ChildDeclarers>());
                continue;
            }
            var fk0 = fkOpt!.Value;
            var t = view.TouchingPlugins(fk0) ?? Array.Empty<string>();
            if (t.Count == 0)
            {
                rows[i] = new TreeRow(FormIdToken.Of(fk0), null, null, Array.Empty<string>(), null, Array.Empty<TreeNodeDelta>(),
                                      UnresolvedFormId(view, fk0), Array.Empty<ChildDeclarers>());
                continue;
            }
            liveRow.Add(i); liveKey.Add(fk0); liveTouchers.Add(t);
        }

        // A chunk of rows at a time, so each provider plugin is walked once for the whole chunk.
        for (int start = 0; start < liveRow.Count; start = ChunkEnd(start, liveRow.Count))
        {
            int end = ChunkEnd(start, liveRow.Count), c = end - start;
            var keys = new FormKey[c];
            var refFields = new RecordFields?[c];
            var refPlugin = new string[c];
            var refPole = new DiffPole?[c];
            var refIsActiveProvider = new bool[c];
            var refLabel = new string[c];
            var versusError = new string?[c];
            var nodes = new TreeNodeDelta?[c][];
            for (int j = 0; j < c; j++)
            {
                keys[j] = liveKey[start + j];
                refPlugin[j] = ""; refLabel[j] = "";
                nodes[j] = new TreeNodeDelta?[liveTouchers[start + j].Count];
            }
            // The versus= pole does not depend on the fold, so the whole chunk's references are resolved here and
            // the gather dropped before a single provider is walked; read inside the fold instead, that plugin's
            // chunk share would stay alive beside every other plugin's.
            if (refReader is not null)
            {
                refGather.Open(view, session, keys, _ => null);   // one walk of the versus plugin for the chunk
                for (int j = 0; j < c; j++)
                {
                    var rr = refReader(keys[j], null);
                    if (rr.Error is not null) { versusError[j] = "versus: " + rr.Error; continue; }
                    refFields[j] = rr.Fields; refPlugin[j] = rr.Pole!.Plugin; refPole[j] = rr.Pole;
                }
                refGather.Release();
            }

            var fills = FoldTreeChunkPinned(new ViewPin(resolver, view), session, keys, fields,
                (j, node, plugin, read, isWinner) =>
                {
                    var touchers = liveTouchers[start + j];
                    if (isWinner)
                    {
                        if (refReader is null) { refFields[j] = read; refPlugin[j] = plugin; }
                        // A refused versus= stops the row at the winner, so the row still carries the type and
                        // editorid the winner body just gave it and no nodes.
                        else if (versusError[j] is not null) return false;
                        // A node IS the reference only when the reference resolved IN the order: an off-order pole is
                        // never one of the active providers.
                        refIsActiveProvider[j] = refPole[j] is null || refPole[j]!.InOrder;
                        refLabel[j] = refPole[j] is not null && touchers.Any(t => string.Equals(t, refPlugin[j], StringComparison.OrdinalIgnoreCase))
                                    ? refPole[j]!.LabelVersus(refPlugin[j]) : refPlugin[j];
                    }
                    bool isRef = refReader is null ? isWinner
                               : refIsActiveProvider[j] && string.Equals(plugin, refPlugin[j], StringComparison.OrdinalIgnoreCase);
                    if (isRef) { nodes[j][node] = new TreeNodeDelta(plugin, isWinner, true, Array.Empty<string>(), 0, true, null); return true; }
                    var d = FieldsDiff.Compare(read, refFields[j]!, referenceLabel: refLabel[j]);
                    nodes[j][node] = new TreeNodeDelta(plugin, isWinner, false, d.Deltas, d.AgreedCount, d.Complete, null);
                    return true;
                });

            for (int j = 0; j < c; j++)
            {
                int i = liveRow[start + j];
                var fk = keys[j];
                var touchers = liveTouchers[start + j];
                // The versus refusal is checked FIRST: it stops the row at the winner, so it leaves no nodes, and
                // the empty-nodes row below would otherwise name the wrong cause.
                if (versusError[j] is not null)
                {
                    rows[i] = new TreeRow(FormIdToken.Of(fk), fills[j]?.Type, fills[j]?.EditorId,
                                          touchers, null, Array.Empty<TreeNodeDelta>(), versusError[j],
                                          Array.Empty<ChildDeclarers>());
                    continue;
                }
                var ordered = new List<TreeNodeDelta>(nodes[j].Length);
                for (int node = nodes[j].Length - 1; node >= 0; node--)   // the fold read winner first; the row reads winner last
                    if (nodes[j][node] is { } nd) ordered.Add(nd);
                if (fills[j] is null || ordered.Count == 0)
                {
                    rows[i] = new TreeRow(FormIdToken.Of(fk), null, null, touchers, null, Array.Empty<TreeNodeDelta>(),
                                          $"the provider bodies of {FormIdToken.Of(fk)} could not be read.", Array.Empty<ChildDeclarers>());
                    continue;
                }
                rows[i] = new TreeRow(FormIdToken.Of(fk), fills[j]!.Type, fills[j]!.EditorId,
                                      touchers, refLabel[j], ordered, null, fills[j]!.ChildDeclarers);
            }
        }
        return rows;
    }
}

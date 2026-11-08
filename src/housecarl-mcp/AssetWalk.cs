using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>Where a walked file stands: shipped by the named source, provided by another provider (the winner),
/// present with no source named, absent everywhere, or a string that names no Data path.</summary>
public enum WalkStanding { FromSource, Elsewhere, Present, Absent, Error }

/// <summary>One file the walk reached: its Data path, the edge and the node or record that pulled it, its standing, the
/// copy that answers it (the source's, else the winner), whether the game finds it by FormID, and a note.</summary>
public sealed record AssetWalkNode(string Path, string Edge, string PulledBy, WalkStanding Standing,
                                   PlacementSource? Copy, bool Keyed = false, string? Note = null, string? Error = null,
                                   HiddenCopies? Hidden = null);

/// <summary>The data behind <c>asset_status walk=true</c>: every node shown in walk order, what the walk noted (records
/// not found, boundaries, gaps), the source it read files from, the record sources (none on a path-only walk), the
/// build caveats, and the record cycles it found, each a loop of records, with whether the search cap stopped them.</summary>
public sealed record AssetWalkData(
    IReadOnlyList<AssetWalkNode> Nodes,
    IReadOnlyList<string> Notes,
    string? SourceProvider,
    IReadOnlyList<string> RecordSources,
    bool Truncated,
    IReadOnlyList<string> BsaFailures,
    IReadOnlyList<string> RootFailures,
    IReadOnlyList<string> Warnings,
    string ProfileName,
    IReadOnlyList<string> Cycles,
    bool CyclesCapped,
    string? Refusal = null);

internal sealed partial class AssetLayers
{
    /// <summary>The face door's sentence for a key a walk with from_source= finds nothing at.</summary>
    internal const string RecordSourcesUnset = "no plugin in this order or the record sources defines it";

    /// <summary>The face door's not-found sentence for a lane reading <paramref name="poles"/>: the order's alone when
    /// every pole is the winner, else <paramref name="named"/>.</summary>
    internal static string UnsetFor(IEnumerable<string> poles, string named) => poles.All(SourcePoles.IsWinner) ? NpcFace.Unset : named;

    /// <summary>The asset walk (SPEC §3 with a file edge): from seed paths and seed records to every file they reach,
    /// through the paths a record lists, its implicit files (a seed NPC's FaceGen, scripts, fragments, .seq, an entered
    /// line's voice) and the links a
    /// file names (a mesh's textures, SMP XML and BODYTRI, a script's parent), each node resolved against
    /// <paramref name="sourceProvider"/> first and then the load order. Records are read through
    /// <paramref name="fromSource"/> (default the winner; a reached record no arm holds, the load order), and the walk
    /// follows the links in each record's part fields and its child records, or the fields walk.follow names. <paramref name="keyedSeeds"/> are the seed
    /// paths a formids= row derived from an NPC's id. <paramref name="values"/> are the walk's follow, depth, budget, through and
    /// exclusions (null: the defaults); <paramref name="badSeeds"/> are seed tokens that are not FormIDs, each an error
    /// node. Contract in docs/architecture/assets.md, Walking the links.</summary>
    public AssetWalkData AssetWalk(IReadOnlyList<string> seedPaths, IReadOnlyList<FormKey> seedRecords,
                                   IReadOnlyList<string>? fromSource, string? sourceProvider,
                                   IReadOnlyDictionary<string, string?>? keyedSeeds = null,
                                   WalkGrammar.Values? values = null, IReadOnlyList<(string Raw, string Error)>? badSeeds = null)
    {
        values ??= WalkGrammar.Values.Defaults(WalkGrammar.AssetDefaultDepth);
        badSeeds ??= Array.Empty<(string, string)>();
        var (pin, captured) = _host.CapturePinAndAssets(null);
        var view = captured.View;
        sourceProvider = sourceProvider?.Trim() is { Length: > 0 } sp ? sp : null;
        if (sourceProvider is not null && AssetSourceChoice.Parse(sourceProvider).Pole != AssetSourcePole.Named) sourceProvider = null;
        var poles = fromSource is { Count: > 0 } ? fromSource : new[] { SourcePoles.Winner };
        AssetWalkData Done(List<AssetWalkNode> nodes, List<string> notes, bool truncated, string? refusal = null, RecordCycles? cycles = null)
            => new(nodes, notes, sourceProvider, seedRecords.Count > 0 ? poles : Array.Empty<string>(), truncated, view.BsaFailures, view.RootFailures, captured.Warnings,
                   captured.ProfileName, cycles?.Loops ?? Array.Empty<string>(), cycles?.Capped == true, refusal);

        if (seedPaths.Count == 0 && seedRecords.Count == 0 && badSeeds.Count == 0)
            return Done(new(), new(), false, "error: the walk has no seed — pass asset_paths=, under=, provider=, formids= or records=.");
        // A source no provider and no mod folder answers to would mark every node elsewhere or absent, so it is refused by name.
        if (sourceProvider is not null && !view.HasProvider(sourceProvider)
            && !(captured.ModsRootOrNull is { } mr && Directory.Exists(LooseListings.OnDisk(mr, sourceProvider))))
            return Done(new(), new(), false,
                $"error: source_provider '{sourceProvider}' is neither a provider in the active order nor a mod folder — name a " +
                "mod folder (enabled or not), 'overwrite', 'Data' or an active archive's filename.");

        var nodes = new List<AssetWalkNode>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<int>();
        int cap = MaxAssetPaths;
        bool truncated = false, filesCut = false;

        // Every node enters here, an error node too: one row per path, counted against the file cap.
        bool Admit(string path)
        {
            if (!seen.Add(path)) return false;
            if (nodes.Count >= cap) { truncated = filesCut = true; return false; }
            return true;
        }
        void AddError(string raw, WalkEdgeKind edge, string pulledBy, string error, bool keyed = false, string? note = null)
        {
            if (Admit(raw)) nodes.Add(ErrorNode(raw, edge.Token(), pulledBy, error) with { Keyed = keyed, Note = note });
        }
        void AddFile(string raw, WalkEdgeKind edge, string pulledBy, bool keyed = false, string? note = null)
        {
            string rel;
            try { rel = AssetResolver.ValidateRelPath(raw); }
            catch (ArgumentException ex) { AddError(raw, edge, pulledBy, ex.Message, keyed, note); return; }
            if (!Admit(rel)) return;
            nodes.Add(Stand(view, rel, edge.Token(), pulledBy, keyed, note, sourceProvider));
            queue.Enqueue(nodes.Count - 1);
        }

        foreach (var p in seedPaths) AddFile(p, WalkEdgeKind.Seed, "the selection", keyed: keyedSeeds?.ContainsKey(p) == true, note: keyedSeeds?.GetValueOrDefault(p));
        foreach (var (raw, error) in badSeeds) AddError(raw, WalkEdgeKind.Seed, "the selection", error);

        using var session = pin.Resolver.OpenSession();
        var overlays = new List<IDisposable>();
        var leftOutTypes = new List<string>();
        int leftOutAt = -1;
        RecordCycles? cycles = null;
        try
        {
            if (seedRecords.Count > 0)
            {
                var chain = _host.BuildWalkSources(pin.View, session, captured.Roots, poles, overlays, out var chainErr);
                if (chain is null) return Done(new(), new(), false, "error: " + chainErr);
                try { cycles = WalkRecords(view, sourceProvider, pin.View, session, chain, seedRecords, overlays, AddFile, notes, values, leftOutTypes, ref leftOutAt, ref truncated); }
                catch (WalkGrammar.WalkRefused r) { return Done(new(), new(), false, "error: " + r.Message); }
            }

            // File edges: a mesh's links and a script's parent, read off the copy that answers the node.
            while (queue.Count > 0)
            {
                var n = nodes[queue.Dequeue()];
                if (n.Copy is null) continue;
                if (n.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    var (links, err) = MeshLinks(view, n.Copy);
                    if (links is null) { notes.Add($"{n.Path}: its links were not read — {err}"); continue; }
                    foreach (var l in links)
                    {
                        if (l.DataPath is { } d) AddFile(d, l.Kind.Edge(), n.Path);
                        else AddError(l.Raw, l.Kind.Edge(), n.Path, l.Error!);
                    }
                }
                else if (n.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
                {
                    // A parent that cannot be read is a note on the script, as a mesh's unread links are, not a second row at its path.
                    var (bytes, readErr) = view.ReadSource(n.Copy);
                    if (bytes is null) { notes.Add($"{n.Path}: its parent class was not read — {readErr ?? "could not read the script bytes."}"); continue; }
                    try { if (AssetWalkEdges.ParentPex(bytes) is { } parent) AddFile(parent, WalkEdgeKind.Parent, n.Path); }
                    catch (Exception ex) { notes.Add($"{n.Path}: its parent class was not read — {ex.Message}"); }
                }
            }
        }
        finally { foreach (var o in overlays) { try { o.Dispose(); } catch { /* releasing the off-order overlays */ } } }

        if (filesCut)
            notes.Add($"the walk stopped at {cap} files (the asset_status path bound); "
                      + (values.Edges is null ? "what it reached so far is listed."
                         : "of what it reached so far, the rows of walk.through's edge kinds are listed and the rest counted on the left-out line."));
        // The walk is whole; through's edge kinds choose only the rows shown, so every lane reads the same set.
        if (values.Edges is { } edges)
        {
            var shown = edges.Select(k => k.Token()).Append(WalkEdgeKind.Seed.Token()).ToHashSet(StringComparer.Ordinal);
            var hidden = nodes.Where(n => !shown.Contains(n.Edge)).Select(n => n.Edge).ToList();
            nodes = nodes.Where(n => shown.Contains(n.Edge)).ToList();
            // The record half's line where it stood, now counting the rows too; a path-only walk's goes last.
            var line = WalkGrammar.LeftOutLine(leftOutTypes, hidden);
            if (leftOutAt >= 0) notes[leftOutAt] = line; else notes.Add(line);
        }
        if (cycles is { Loops.Count: > 0 } c)
        {
            notes.Add($"{c.Loops.Count} record cycle(s) found (recorded, not an error): a lower bound, one per link the search finds closing a loop; a loop met again through a record already searched is not counted, and one through a record not entered (past walk.depth or walk.max_nodes, a reached NPC kept as a boundary, or past a link the walk does not follow) is not seen; the format=json walk answer (not counts_only=) lists the loops that fit max_chars and counts the rest, and a to_file= or spilled file lists every loop found.");
            if (c.Capped) notes.Add($"the cycle search stopped at {RecordReads.WalkCycleCap} loops — more loops are there than are counted.");
        }
        return Done(nodes, notes, truncated, cycles: cycles);
    }

    /// <summary>The note counting the NPCs a walk reached past a seed and kept as boundaries.</summary>
    internal static string NpcBoundaryNote(int count)
        => $"{count} NPC(s) the walk reached past a seed were kept as boundaries, not entered (no files, no links); seed one with records= to walk it, or name Npc in walk.through beside every other record type to keep, since a set walk.through enters only the types it names.";

    /// <summary>The record cycles a walk found, each stated as its loop of records, and whether the search cap stopped it.</summary>
    internal sealed record RecordCycles(IReadOnlyList<string> Loops, bool Capped);

    /// <summary>A node whose path could not be resolved or read on.</summary>
    static AssetWalkNode ErrorNode(string path, string edge, string pulledBy, string error)
        => new(path, edge, pulledBy, WalkStanding.Error, null, Error: error);

    /// <summary>The files one copy of a mesh names, each once at its first site, unresolved; or why they could not be read.</summary>
    internal static (IReadOnlyList<NifLink>? Links, string? Error) MeshLinks(AssetResolver.AssetView view, PlacementSource copy)
    {
        var (bytes, readErr) = view.ReadSource(copy);
        if (bytes is null) return (null, readErr ?? "could not read the mesh bytes.");
        var (links, err) = NifLinks.Read(bytes);
        return links is null ? (null, err) : (NifLinks.FirstSites(links), null);
    }

    /// <summary>One node's standing: the named source's copy if it ships the path, else the winner, else absent.</summary>
    static AssetWalkNode Stand(AssetResolver.AssetView view, string rel, string edge, string pulledBy, bool keyed, string? note, string? source)
    {
        var place = view.ResolveForPlacement(rel);
        // The named folder's own hidden copy, else the build's: a hidden node keeps its node and says why it is absent.
        var hidden = place.Hidden;
        if (source is not null)
        {
            var pick = AssetSourceSelection.Select(place, AssetSourceChoice.Named(source), n => view.TryResolveOffOrderProvider(n, rel));
            if (pick.Verdict == AssetSourceVerdict.Selected)
                return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.FromSource, pick.Source, keyed, note);
            hidden = pick.OffOrderHidden ?? hidden;
        }
        if (place.Sources.Count == 0) return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.Absent, null, keyed, note, Hidden: hidden);
        if (source is null) return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.Present, place.Sources[0], keyed, note);
        // Another layer wins; the named folder's own hidden copy rides along so the node says why it ships none.
        return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.Elsewhere, place.Sources[0], keyed, note,
                                 Hidden: hidden?.HeldBy(source) == true ? hidden : null);
    }

    internal delegate void FileSink(string raw, WalkEdgeKind edge, string pulledBy, bool keyed = false, string? note = null);

    /// <summary>The record half: every seed and every record it reaches, each contributing the paths it lists and its
    /// implicit files; a seed NPC its FaceGen too; a reached NPC is a boundary unless walk.through names Npc. By default
    /// a record is left by the links in its part fields (<see cref="WalkParts"/>) and by its child records, read across
    /// every plugin that touches it; walk.follow names the fields instead ("*" every form link). A reached record no
    /// from_source arm holds is read off the load order, and its rows say so. <paramref name="leftOut"/> gets the type of
    /// each record walk.through left out, and <paramref name="leftOutAt"/> the index of the note that counts them.
    /// Returns the record cycles over the links it followed, one out of a record at walk.depth too.</summary>
    RecordCycles WalkRecords(AssetResolver.AssetView view, string? source, LoadOrderResolver.IndexView index, LoadOrderResolver.OverlaySession session, SourceChain chain, IReadOnlyList<FormKey> seeds, List<IDisposable> overlays, FileSink addFile,
                             List<string> notes, WalkGrammar.Values values, List<string> leftOut, ref int leftOutAt, ref bool truncated)
    {
        int maxDepth = values.Depth, maxRecords = values.MaxNodes;
        // null: the part fields and children; else the named fields, or every form link when it holds "*".
        var follow = values.Follow;
        bool everyLink = WalkGrammar.FollowsEveryLink(follow);
        // What walk.exclusions kept as boundaries.
        int stopped = 0;
        var seenRec = new HashSet<FormKey>();
        // Every link followed out of an entered record, one at walk.depth too though not entered, and each entered record's EditorID, for the cycles.
        var edges = new Dictionary<FormKey, List<FormKey>>();
        var editorIds = new Dictionary<FormKey, string?>();
        // The NPCs reached past depth 0 kept as boundaries, walk.through not naming Npc.
        int npcBoundaries = 0;
        var frontier = new List<(FormKey Key, string PulledBy)>();
        foreach (var s in seeds) if (seenRec.Add(s)) frontier.Add((s, "seed"));
        int pastDepth = 0;
        // The form links on entered records the follow rule did not walk, per holding type.
        var notFollowed = new Dictionary<string, int>(StringComparer.Ordinal);
        // The arms already read the whole order when one is the winner, so a miss there is a miss everywhere.
        bool readsOrder = chain.Arms.Any(a => SourcePoles.IsWinner(a.Spelling));
        var inferCache = AssetWalkEdges.EmptyCache();
        IAssetLinkCache? voiceCache = null;
        IAssetLinkCache Voice() => voiceCache ??= VoiceCache(session, overlays);
        int entered = 0;
        // One buffer for every entered record's followed links, one for its whole link set, cleared by each read.
        var links = new List<FormKey>();
        var allLinks = new List<FormKey>();
        var unreadParts = new List<string>();

        // True when walk.exclusions or walk.through keeps the record out: a stop is a boundary, a type outside through
        // is left out, neither entered; a refuse ends the call. Unset, no record is judged.
        bool judging = values.Exclusions.Count > 0 || values.Through is not null;
        bool Judged(IMajorRecordGetter body, FormKey fk, string pulledBy)
        {
            var type = RecordNaming.StripOverlay(body.GetType().Name);
            if (WalkExclusionMatch.Match(values.Exclusions, type) is { } x)
            {
                if (x.Refuse) throw new WalkGrammar.WalkRefused(WalkExclusionMatch.RefuseSentence(type, fk, pulledBy));
                stopped++;
                return true;
            }
            if (values.Through is { } through && !through.Contains(type)) { leftOut.Add(type); return true; }
            return false;
        }

        // Template members, races and speakers: the walk's own read when it reached them, else a round of their own.
        // Only these types are kept, so no cell or worldspace body holds its group's bytes past its depth.
        var members = new MemberReads(chain, keys => BodyGather.Winners(index, session, keys));
        IMajorRecordGetter? Fetch(FormKey fk) => members.Get(fk);
        var faces = new NpcFace(Fetch, UnsetFor(chain.Arms.Select(a => a.Spelling), RecordSourcesUnset));

        // Breadth-first by depth, each depth's records read at once through the door: the order a queue would enter them.
        for (int depth = 0; frontier.Count > 0; depth++)
        {
            var fetched = chain.FetchMany(frontier.Select(f => f.Key).ToList());
            // A reached record no arm holds is read off the load order, as a round; a seed is read only through the arms.
            var orderKeys = depth > 0 && !readsOrder ? frontier.Where(f => fetched[f.Key].IsMiss).Select(f => f.Key).ToList() : null;
            var fromOrder = orderKeys is { Count: > 0 } ? BodyGather.Winners(index, session, orderKeys) : null;
            // What this depth does with each record, in order: a fault or a miss is a note, the rest are entered until the
            // record cap, where the walk stops.
            var steps = new List<(FormKey Key, string PulledBy, IMajorRecordGetter? Body, SourceArm? Arm, string? Note)>();
            bool capped = false;
            foreach (var (fk, pulledBy) in frontier)
            {
                var got = fetched[fk];
                IMajorRecordGetter? body = got.Hit?.Body;
                string? note = null;
                if (got.Fault is { } fault) note = $"record {FormIdToken.Of(fk)} (pulled by {pulledBy}): '{fault.Arm.Spelling}' has it but could not read it — {fault.Cause}";
                else if (body is null && fromOrder is not null)
                {
                    try { body = fromOrder(fk); }
                    catch (Exception ex) { note = $"record {FormIdToken.Of(fk)} (pulled by {pulledBy}): the load order has it but could not read it — {ex.Message}"; }
                }
                if (note is null && body is null)
                    note = $"record {FormIdToken.Of(fk)} (pulled by {pulledBy}) is in none of the sources consulted: " +
                           string.Join(", ", chain.Arms.Select(a => a.Where)) + (fromOrder is null ? "." : ", nor the load order.");
                if (note is not null) { steps.Add((fk, pulledBy, null, null, note)); continue; }
                // A reached record is judged on the type its read body reports; a seed never is.
                if (depth > 0 && judging && Judged(body!, fk, pulledBy)) continue;
                // A reached NPC is a boundary when walk.through is unset (#1371); a through set has judged it above.
                if (depth > 0 && values.Through is null && body is INpcGetter) { npcBoundaries++; continue; }
                if (++entered > maxRecords) { capped = true; break; }
                steps.Add((fk, pulledBy, body, got.Hit?.Arm, null));
            }
            members.Know(steps.Select(s => s.Body).Where(b => b is INpcGetter or ILeveledNpcGetter or IRaceGetter)!);
            // Only the seed NPCs have their template chains read, for their FaceGen.
            if (depth == 0) members.Templates(steps.Select(s => s.Body).OfType<INpcGetter>());
            // The child unions of this depth's cells and topics, read together across their plugins.
            var children = follow is null ? ChildUnions(index, session, steps.Where(s => s.Body is not null).Select(s => (s.Key, s.Body!, s.Arm))) : null;
            var next = new List<(FormKey Key, string PulledBy)>();
            foreach (var (fk, pulledBy, entry, arm, note) in steps)
            {
                if (entry is not { } body) { notes.Add(note!); continue; }
                var type = RecordNaming.StripOverlay(body.GetType().Name);
                var label = (body.EditorID is { Length: > 0 } edid ? $"{type} {edid} [{FormIdToken.Of(fk)}]" : $"{type} [{FormIdToken.Of(fk)}]")
                            + (arm is null ? " (read from the load order: no from_source plugin has it)" : "");

                editorIds[fk] = body.EditorID;
                var listed = AssetLinkHarvest.HarvestAssetPaths(new[] { body }, out var unlisted);
                foreach (var p in listed) addFile(p, WalkEdgeKind.Listed, label);
                if (unlisted is not null) notes.Add($"{label}: part of the paths it lists could not be read, so files it names there may be missing — {unlisted}");
                try { foreach (var e in AssetWalkEdges.Inferred(body, inferCache)) addFile(e.Path, e.Edge, label, e.Keyed, e.Note); }
                catch (Exception ex) { notes.Add($"{label}: its inferred links were not read — {ex.Message}"); }
                foreach (var e in AssetWalkEdges.Fragments(body)) addFile(e.Path, e.Edge, label, e.Keyed, e.Note);

                // Only a seed NPC's face is listed.
                if (depth == 0 && body is INpcGetter npc) FaceGenOf(npc, label, faces, addFile, notes);
                // A line the walk entered, a seed or reached forward, is voiced; no line is found off an NPC.
                if (body is IDialogResponsesGetter info)
                    VoiceOf(view, source, info, label, Voice(), Fetch, addFile, notes);

                if (everyLink)
                {
                    // Through the one link door: a deleted record links to nothing, a perk read leniently is followed and
                    // said, and one whose links cannot be read is said and not followed.
                    if (RecordLinks.List(body, links, out var unread) is { } lenient) notes.Add($"{label}: its links were read leniently — {RecordReads.LenientNoteBody(fk, lenient)}");
                    if (unread is not null) notes.Add($"{label}: its links could not be read, so the walk does not go past it — {unread}");
                }
                else
                {
                    unreadParts.Clear();
                    WalkParts.Links(body, follow ?? WalkParts.For(type), links, unreadParts);
                    foreach (var u in unreadParts) notes.Add($"{label}: the links under {u} could not be read, so the walk does not follow them");
                    if (follow is null) ChildrenOf(body, children!.GetValueOrDefault(fk), label, links, notes);
                    // The rest of its form links, counted per type; the link door's own faults are said by a walk that follows them.
                    RecordLinks.List(body, allLinks, out _);
                    var kept = links.ToHashSet();
                    int left = allLinks.Where(k => !k.IsNull && k != fk && kept.Add(k)).Count();
                    if (left > 0) notFollowed[type] = notFollowed.GetValueOrDefault(type) + left;
                }
                var outgoing = edges[fk] = new List<FormKey>();
                foreach (var link in links)
                {
                    if (link.IsNull) continue;
                    // A self-link is a loop of one, recorded as the records walk records it, and walks nowhere new.
                    if (link == fk) { outgoing.Add(link); continue; }
                    // Recorded at walk.depth too, so a link back to an entered record closes its loop; one to a record
                    // never entered closes none.
                    outgoing.Add(link);
                    if (depth >= maxDepth) { if (seenRec.Add(link)) pastDepth++; continue; }
                    if (seenRec.Add(link)) next.Add((link, label));
                }
            }
            if (capped)
            {
                truncated = true;
                notes.Add($"the walk stopped entering records at walk.max_nodes={maxRecords} (every record entered counts, seeds included); raise walk.max_nodes to walk further.");
                next.Clear();
            }
            frontier = next;
        }
        if (pastDepth > 0)
            notes.Add($"{pastDepth} record(s) sit more than walk.depth={maxDepth} hops from a seed and were not entered; raise walk.depth to walk them (an NPC among them is then a boundary unless walk.through names Npc).");
        if (stopped > 0)
            notes.Add($"{stopped} reached record(s) matched a stop exclusion and were kept as boundaries, not entered (no files, no links).");
        if (values.Through is not null || values.Edges is not null)
        {
            leftOutAt = notes.Count;
            notes.Add(WalkGrammar.LeftOutLine(leftOut));
        }
        if (notFollowed.Count > 0) notes.Add(NotFollowedNote(notFollowed, follow is null));
        if (npcBoundaries > 0) notes.Add(NpcBoundaryNote(npcBoundaries));

        // Every loop over the recorded links, capped as the records walk caps its search (one cap, the shared budget's).
        var loops = RecordReads.CycleLines(edges, k => editorIds.GetValueOrDefault(k), out var cyclesCapped);
        return new RecordCycles(loops, cyclesCapped);
    }

    /// <summary>The note counting, per holding type, the form links the walk's follow rule left unwalked.</summary>
    internal static string NotFollowedNote(IReadOnlyDictionary<string, int> perType, bool partFields)
        => $"{perType.Values.Sum()} form link(s) on the records entered sit outside the {(partFields ? "part fields" : "fields walk.follow names")} and were not walked: "
           + string.Join(", ", perType.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Value} on {t.Key}"))
           + "; walk.follow names the fields to follow, and walk={\"follow\": \"*\"} follows every form link.";

    /// <summary>The child-bearing records among one depth's entered records, each with the union of its children across
    /// every plugin that touches it, read together (null: one toucher); the plugin a body came from is its subject, so
    /// the union reuses it, and an off-order file is no toucher.</summary>
    static Dictionary<FormKey, IReadOnlyDictionary<string, ChildUnion>?> ChildUnions(LoadOrderResolver.IndexView index, LoadOrderResolver.OverlaySession session,
                                                                                    IEnumerable<(FormKey Key, IMajorRecordGetter Body, SourceArm? Arm)> entered)
    {
        var items = new List<(FormKey, string, IMajorRecordGetter, IReadOnlyDictionary<string, OwnedChildShape>)>();
        foreach (var (fk, body, arm) in entered)
        {
            var fields = OwnedChildContent.Fields(body);
            if (fields.Count == 0) continue;
            var plugin = arm is null || SourcePoles.IsWinner(arm.Spelling) ? index.ResolveWinner(fk)?.WinnerPlugin : Path.GetFileName(arm.Spelling);
            items.Add((fk, plugin ?? "", body, fields));
        }
        var unions = OwnedChildUnion.ComputeMany(index, session, items);
        var byKey = new Dictionary<FormKey, IReadOnlyDictionary<string, ChildUnion>?>();
        for (int i = 0; i < items.Count; i++) byKey[items[i].Item1] = unions[i];
        return byKey;
    }

    /// <summary>Adds the child records of <paramref name="body"/> (a cell's placed references, a topic's lines, a
    /// worldspace's cells) to <paramref name="into"/>: its own, then those of every other plugin that touches it.</summary>
    static void ChildrenOf(IMajorRecordGetter body, IReadOnlyDictionary<string, ChildUnion>? unions, string label, List<FormKey> into, List<string> notes)
    {
        var fields = OwnedChildContent.Fields(body);
        if (fields.Count == 0) return;
        var have = into.ToHashSet();
        foreach (var f in fields.Keys)
            foreach (var k in OwnedChildUnion.ChildKeys(body, f) ?? (IReadOnlyList<FormKey>)Array.Empty<FormKey>())
                if (have.Add(k)) into.Add(k);
        if (unions is null) return;
        foreach (var u in unions.Values)
        {
            foreach (var k in u.Members)
                if (have.Add(k)) into.Add(k);
            if (u.Unreadable.Count > 0)
                notes.Add($"{label}: its {u.Field} children in {string.Join(", ", u.Unreadable)} could not be read, so the walk does not enter them.");
        }
    }

    /// <summary>Races, template members and speakers, read through the record sources, else off the active order, as the
    /// game would, a round of keys at a time, and kept by key for the whole walk. A key neither holds is null; one an arm
    /// has but cannot read throws with why, each time it is asked.</summary>
    internal sealed class MemberReads(SourceChain chain, Func<IReadOnlyCollection<FormKey>, Func<FormKey, IMajorRecordGetter?>> winners)
    {
        readonly Dictionary<FormKey, (IMajorRecordGetter? Body, Exception? Error)> _read = new();

        /// <summary>Bodies the walk read itself through the same sources, kept so no round reads them again.</summary>
        public void Know(IEnumerable<IMajorRecordGetter> bodies)
        {
            foreach (var body in bodies) _read.TryAdd(body.FormKey, (body, null));
        }

        /// <summary>Read every key not read yet, at once.</summary>
        public void Read(IEnumerable<FormKey> keys)
        {
            var want = keys.Where(k => !_read.ContainsKey(k)).Distinct().ToList();
            if (want.Count == 0) return;
            var got = Through(chain, winners)(want);
            foreach (var k in want)
                try { _read[k] = (got(k), null); }
                catch (Exception ex) { _read[k] = (null, ex); }
        }

        /// <summary>A round read through <paramref name="chain"/>, each key it holds none of from <paramref name="winners"/>;
        /// a key an arm has but cannot read throws with why.</summary>
        internal static Func<IReadOnlyCollection<FormKey>, Func<FormKey, IMajorRecordGetter?>> Through(
            SourceChain chain, Func<IReadOnlyCollection<FormKey>, Func<FormKey, IMajorRecordGetter?>> winners) => keys =>
        {
            var got = chain.FetchMany(keys);
            var misses = keys.Where(k => got[k].IsMiss).ToList();
            var winner = misses.Count > 0 ? winners(misses) : null;
            return k => got[k] switch
            {
                { Fault: { } f } => throw new InvalidOperationException($"'{f.Arm.Spelling}' has it but could not read it — {f.Cause}"),
                { Hit: { } hit } => hit.Body,
                _ => winner!(k),
            };
        };

        /// <summary>One key's body, read as a round of one if no round has read it.</summary>
        public IMajorRecordGetter? Get(FormKey fk)
        {
            if (!_read.ContainsKey(fk)) Read(new[] { fk });
            var (body, error) = _read[fk];
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return body;
        }

        /// <summary>The records the face door asks for <paramref name="npcs"/> (template members, races), read a round per
        /// hop through <see cref="NpcFace.Prefetch"/>, so a face root and its race are in hand before FaceGenOf asks.</summary>
        public void Templates(IEnumerable<INpcGetter> npcs)
            => NpcFace.Prefetch(npcs, k => _read.TryGetValue(k, out var r) ? (true, r.Body) : (false, null), Read);
    }

    /// <summary>An NPC's FaceGen nodes from the face door: its own pair, or its template root's, each member of a
    /// leveled template marked one of N; a note with the reason for every root that has none.</summary>
    internal static void FaceGenOf(INpcGetter npc, string label, NpcFace door, FileSink addFile, List<string> notes)
    {
        var face = door.Of(npc.FormKey, npc);
        // A reason ending in an exception's message may carry its own period.
        static string Ended(string why) => why.EndsWith('.') ? why : why + ".";
        foreach (var err in face.Errors) notes.Add($"{label}: " + (face.Roots.Count == 0 ? "no FaceGen node" : "a template member has no FaceGen node") + $" — {Ended(err)}");
        foreach (var root in face.Roots)
        {
            if (root.Reason is { } why) { notes.Add($"{label}: no FaceGen node for {FormIdToken.Of(root.Root)} — {Ended(why)}"); continue; }
            var note = face.NoteFor(root);
            addFile(root.Tint!, WalkEdgeKind.FaceGen, label, keyed: true, note: note);
            addFile(root.Mesh!, WalkEdgeKind.FaceGen, label, keyed: true, note: note);
        }
    }

    /// <summary>A dialogue line's voice files: the .fuz Mutagen resolves, or a same-stem .xwm / .wav with its .lip
    /// when no .fuz ships; both listed when both ship, with no claim on which plays. A file ships when the order or
    /// the walk's source provides it.</summary>
    static void VoiceOf(AssetResolver.AssetView view, string? source, IDialogResponsesGetter info, string label, IAssetLinkCache cache,
                        Func<FormKey, IMajorRecordGetter?> fetch, FileSink addFile, List<string> notes)
    {
        bool Ships(string rel) => Stand(view, rel, "", "", false, null, source).Standing != WalkStanding.Absent;
        IReadOnlyList<string> fuzes;
        try { fuzes = AssetWalkEdges.Voice(info, cache); }
        catch (Exception ex) { notes.Add($"{label}: its voice paths were not resolved — {ex.GetType().Name}: {ex.Message}"); return; }
        if (fuzes.Count == 0) { notes.Add($"{label}: no voice node — {NoVoiceReason(info, fetch)}."); return; }
        foreach (var fuz in fuzes)
        {
            var stem = fuz[..^".fuz".Length];
            var loose = new[] { ".xwm", ".wav" }.Select(x => stem + x).Where(Ships).ToList();
            bool hasFuz = Ships(fuz);
            if (hasFuz || loose.Count == 0)
                addFile(fuz, WalkEdgeKind.Voice, label, keyed: true,
                        note: hasFuz && loose.Count > 0 ? "a same-stem " + string.Join(" and ", loose.Select(Path.GetExtension)) + " ships too; which one plays is not claimed" : null);
            foreach (var x in loose) addFile(x, WalkEdgeKind.Voice, label, keyed: true, note: hasFuz ? null : "no .fuz ships; this line plays from this file");
            if (!hasFuz && loose.Count > 0) addFile(stem + ".lip", WalkEdgeKind.Lip, label, keyed: true);
        }
    }

    /// <summary>Why Mutagen found no voice for a line, in VoiceCheck's terms: no responses of its own, no speaker, a
    /// speaker that is not an NPC or not found, or a speaker NPC with no voice type.</summary>
    static string NoVoiceReason(IDialogResponsesGetter info, Func<FormKey, IMajorRecordGetter?> fetch)
    {
        if (info.Responses.Count == 0)
            return info.ResponseData.IsNull
                ? "it has no responses of its own and no shared response data, so it speaks no line"
                : $"it has no responses of its own: it draws its audio from shared response data {FormIdToken.Of(info.ResponseData.FormKey)}, which the walk enters and voices as its own line when its plugin is walked, and counts as a boundary otherwise";
        if (info.Speaker.IsNull)
            return "no Speaker set, and Mutagen resolves no voice type for it from the rest of the line";
        var fk = info.Speaker.FormKey;
        IMajorRecordGetter? speaker;
        try { speaker = fetch(fk); }
        catch (Exception ex) { return $"its Speaker {FormIdToken.Of(fk)} was not read: {ex.Message}"; }
        return speaker switch
        {
            null => $"its Speaker {FormIdToken.Of(fk)} is in none of the record sources nor the load order",
            INpcGetter npc when npc.Voice.IsNull => $"its Speaker NPC {FormIdToken.Of(fk)} has no Voice type set",
            INpcGetter => "Mutagen resolves no voice type for its speaker",
            _ => $"its Speaker {FormIdToken.Of(fk)} is not an NPC, and the voice type is read off an NPC's Voice",
        };
    }

    /// <summary>The whole order's link cache, with any off-order source overlays last, for Mutagen's voice resolution,
    /// which reads the line's topic, quest, speaker and conditions. Built once per walk, only when a line is reached.</summary>
    static IAssetLinkCache VoiceCache(LoadOrderResolver.OverlaySession session, List<IDisposable> overlays)
        => session.AllMasters().Concat(overlays.OfType<ISkyrimModGetter>()).ToImmutableLinkCache().CreateImmutableAssetLinkCache();
}

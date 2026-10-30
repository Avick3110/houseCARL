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
                                   PlacementSource? Copy, bool Keyed = false, string? Note = null, string? Error = null);

/// <summary>The data behind <c>asset_status walk=true</c>: every node in walk order, what the walk noted (records not
/// found, boundaries, gaps), the source it read files from, the record sources, and the build caveats.</summary>
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
    string? Refusal = null);

internal sealed partial class AssetLayers
{
    /// <summary>Records the walk enters, past which it stops and says so.</summary>
    internal int MaxWalkRecords { get; set; } = 2_000;

    /// <summary>How many form-link hops from a seed record the walk enters: NPC → head part → its extra part's texture set.</summary>
    internal const int RecordDepth = 3;

    /// <summary>The asset walk (SPEC §3 with a file edge): from seed paths and seed records to every file they reach,
    /// through the paths a record lists, its implicit files (FaceGen, scripts, fragments, .seq, voice) and the links a
    /// file names (a mesh's textures, SMP XML and BODYTRI, a script's parent), each node resolved against
    /// <paramref name="sourceProvider"/> first and then the load order. Records are read through
    /// <paramref name="fromSource"/> (default the winner) and the walk follows form links only into records the seed's
    /// own plugin or a from_source plugin defines; others are boundaries. Contract in docs/architecture/assets.md, Walking the links.</summary>
    public AssetWalkData AssetWalk(IReadOnlyList<string> seedPaths, IReadOnlyList<FormKey> seedRecords,
                                   IReadOnlyList<string>? fromSource, string? sourceProvider)
    {
        var (pin, captured) = _host.CapturePinAndAssets(null);
        var view = captured.View;
        sourceProvider = sourceProvider?.Trim() is { Length: > 0 } sp ? sp : null;
        if (sourceProvider is not null && AssetSourceChoice.Parse(sourceProvider).Pole != AssetSourcePole.Named) sourceProvider = null;
        var poles = fromSource is { Count: > 0 } ? fromSource : new[] { SourcePoles.Winner };
        AssetWalkData Done(List<AssetWalkNode> nodes, List<string> notes, bool truncated, string? refusal = null)
            => new(nodes, notes, sourceProvider, poles, truncated, view.BsaFailures, view.RootFailures, captured.Warnings,
                   captured.ProfileName, refusal);

        if (seedPaths.Count == 0 && seedRecords.Count == 0)
            return Done(new(), new(), false, "error: the walk has no seed — pass asset_paths=, under=, provider=, formids= or records=.");
        // A source no provider and no mod folder answers to would mark every node elsewhere or absent, so it is refused by name.
        if (sourceProvider is not null && !view.HasProvider(sourceProvider)
            && !(captured.ModsRootOrNull is { } mr && Directory.Exists(Path.Combine(mr, sourceProvider))))
            return Done(new(), new(), false,
                $"error: source_provider '{sourceProvider}' is neither a provider in the active order nor a mod folder — name a " +
                "mod folder (enabled or not), 'overwrite', 'Data' or an archive's filename.");

        var nodes = new List<AssetWalkNode>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<int>();
        int cap = MaxAssetPaths;
        bool truncated = false, filesCut = false;

        void AddFile(string raw, string edge, string pulledBy, bool keyed = false, string? note = null)
        {
            string rel;
            try { rel = AssetResolver.ValidateRelPath(raw); }
            catch (ArgumentException ex)
            {
                nodes.Add(ErrorNode(raw, edge, pulledBy, ex.Message) with { Keyed = keyed, Note = note });
                return;
            }
            if (!seen.Add(rel)) return;
            if (nodes.Count >= cap) { truncated = filesCut = true; return; }
            nodes.Add(Stand(view, rel, edge, pulledBy, keyed, note, sourceProvider));
            queue.Enqueue(nodes.Count - 1);
        }

        foreach (var p in seedPaths) AddFile(p, "seed", "the selection");

        using var session = pin.Resolver.OpenSession();
        var overlays = new List<IDisposable>();
        try
        {
            if (seedRecords.Count > 0)
            {
                var chain = _host.BuildWalkSources(pin.View, session, captured.Roots, poles, overlays, out var chainErr);
                if (chain is null) return Done(new(), new(), false, "error: " + chainErr);
                WalkRecords(view, session, chain, seedRecords, overlays, AddFile, notes, ref truncated);
            }

            // File edges: a mesh's links and a script's parent, read off the copy that answers the node.
            while (queue.Count > 0)
            {
                var n = nodes[queue.Dequeue()];
                if (n.Copy is null) continue;
                if (n.Path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                {
                    var (links, err) = MeshLinks(n.Copy);
                    if (links is null) { notes.Add($"{n.Path}: its links were not read — {err}"); continue; }
                    foreach (var l in links)
                    {
                        if (l.DataPath is { } d) AddFile(d, l.Kind.Token(), n.Path);
                        else nodes.Add(ErrorNode(l.Raw, l.Kind.Token(), n.Path, l.Error!));
                    }
                }
                else if (n.Path.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
                {
                    var (bytes, readErr) = AssetResolver.ReadPlacementSource(n.Copy);
                    if (bytes is null) { nodes.Add(ErrorNode(n.Path, "parent", n.Path, $"its parent class was not read — {readErr ?? "could not read the script bytes."}")); continue; }
                    try { if (AssetWalkEdges.ParentPex(bytes) is { } parent) AddFile(parent, "parent", n.Path); }
                    catch (Exception ex) { nodes.Add(ErrorNode(n.Path, "parent", n.Path, $"its parent class was not read — {ex.Message}")); }
                }
            }
        }
        finally { foreach (var o in overlays) { try { o.Dispose(); } catch { /* releasing the off-order overlays */ } } }

        if (filesCut)
            notes.Add($"the walk stopped at {cap} files (the asset_status path bound); what it reached so far is listed.");
        return Done(nodes, notes, truncated);
    }

    /// <summary>A node whose path could not be resolved or read on.</summary>
    static AssetWalkNode ErrorNode(string path, string edge, string pulledBy, string error)
        => new(path, edge, pulledBy, WalkStanding.Error, null, Error: error);

    /// <summary>The files one copy of a mesh names, unresolved; or why they could not be read.</summary>
    internal static (IReadOnlyList<NifLink>? Links, string? Error) MeshLinks(PlacementSource copy)
    {
        var (bytes, readErr) = AssetResolver.ReadPlacementSource(copy);
        return bytes is null ? (null, readErr ?? "could not read the mesh bytes.") : NifLinks.Read(bytes);
    }

    /// <summary>One node's standing: the named source's copy if it ships the path, else the winner, else absent.</summary>
    static AssetWalkNode Stand(AssetResolver.AssetView view, string rel, string edge, string pulledBy, bool keyed, string? note, string? source)
    {
        var place = view.ResolveForPlacement(rel);
        if (source is not null)
        {
            var pick = AssetSourceSelection.Select(place, AssetSourceChoice.Named(source), n => view.TryResolveOffOrderProvider(n, rel));
            if (pick.Verdict == AssetSourceVerdict.Selected)
                return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.FromSource, pick.Source, keyed, note);
        }
        if (place.Sources.Count == 0) return new AssetWalkNode(rel, edge, pulledBy, WalkStanding.Absent, null, keyed, note);
        return new AssetWalkNode(rel, edge, pulledBy, source is null ? WalkStanding.Present : WalkStanding.Elsewhere,
                                 place.Sources[0], keyed, note);
    }

    delegate void FileSink(string raw, string edge, string pulledBy, bool keyed = false, string? note = null);

    /// <summary>The record half: every seed and every record its form links reach inside the seed's own plugin and the
    /// from_source plugins, each contributing the paths it lists and its implicit files.</summary>
    void WalkRecords(AssetResolver.AssetView view, LoadOrderResolver.OverlaySession session, SourceChain chain, IReadOnlyList<FormKey> seeds, List<IDisposable> overlays, FileSink addFile,
                     List<string> notes, ref bool truncated)
    {
        var seenRec = new HashSet<FormKey>();
        var queue = new Queue<(FormKey Key, string PulledBy, IReadOnlySet<ModKey> Home, int Depth)>();
        // The plugins each from_source arm names, as copy binds them (RecordCopy): an override donor's own records are entered.
        var named = new HashSet<ModKey>();
        foreach (var arm in chain.Arms)
        {
            if (string.Equals(arm.Spelling, SourcePoles.Winner, StringComparison.OrdinalIgnoreCase)) continue;
            try { named.Add(ModKey.FromFileName(Path.GetFileName(arm.Spelling))); } catch { /* a spelling that names no plugin binds nothing */ }
        }
        foreach (var s in seeds) if (seenRec.Add(s)) queue.Enqueue((s, "seed", new HashSet<ModKey>(named) { s.ModKey }, 0));
        int pastDepth = 0;
        var boundaries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inferCache = AssetWalkEdges.EmptyCache();
        IAssetLinkCache? voiceCache = null;
        var raceCaches = new Dictionary<FormKey, IAssetLinkCache?>();
        int entered = 0;

        IMajorRecordGetter? Fetch(FormKey fk) => chain.Fetch(fk).Hit?.Body;

        while (queue.Count > 0)
        {
            var (fk, pulledBy, home, depth) = queue.Dequeue();
            var got = chain.Fetch(fk, pulledBy);
            if (got.Fault is { } fault) { notes.Add($"record {FormIdToken.Of(fk)} (pulled by {pulledBy}): '{fault.Arm.Spelling}' has it but could not read it — {fault.Cause}"); continue; }
            if (got.Hit is not { } hit)
            {
                notes.Add($"record {FormIdToken.Of(fk)} (pulled by {pulledBy}) is in none of the sources consulted: " +
                          string.Join(", ", chain.Arms.Select(a => a.Where)) + ".");
                continue;
            }
            if (++entered > MaxWalkRecords) { truncated = true; notes.Add($"the walk stopped entering records at {MaxWalkRecords}."); break; }
            var body = hit.Body;
            var type = RecordNaming.StripOverlay(body.GetType().Name);
            var label = body.EditorID is { Length: > 0 } edid ? $"{type} {edid} [{FormIdToken.Of(fk)}]" : $"{type} [{FormIdToken.Of(fk)}]";

            foreach (var p in AssetLinkHarvest.HarvestAssetPaths(new[] { body })) addFile(p, "listed", label);
            try { foreach (var e in AssetWalkEdges.Inferred(body, inferCache)) addFile(e.Path, e.Edge, label, e.Keyed, e.Note); }
            catch (Exception ex) { notes.Add($"{label}: its inferred links were not read — {ex.Message}"); }
            foreach (var e in AssetWalkEdges.Fragments(body)) addFile(e.Path, e.Edge, label, e.Keyed, e.Note);

            if (body is INpcGetter npc) FaceGenOf(npc, label, Fetch, raceCaches, addFile, notes);
            if (body is IDialogResponsesGetter info)
            {
                voiceCache ??= VoiceCache(session, overlays);
                VoiceOf(view, info, label, voiceCache, addFile, notes);
            }

            foreach (var link in body.EnumerateFormLinks())
            {
                if (link.IsNull || link.FormKey == fk) continue;
                if (!home.Contains(link.FormKey.ModKey)) { boundaries[link.FormKey.ModKey.FileName] = boundaries.GetValueOrDefault(link.FormKey.ModKey.FileName) + 1; continue; }
                if (depth >= RecordDepth) { if (seenRec.Add(link.FormKey)) pastDepth++; continue; }
                if (seenRec.Add(link.FormKey)) queue.Enqueue((link.FormKey, label, home, depth + 1));
            }
        }
        if (pastDepth > 0)
            notes.Add($"{pastDepth} record(s) of the seed's own plugin or a from_source plugin sit more than {RecordDepth} form-link hops from a seed and were not entered; seed them with records= to walk them.");
        if (boundaries.Count > 0)
            notes.Add("form links into other plugins were kept as boundaries, not walked (their files are that plugin's): " +
                      string.Join(", ", boundaries.OrderByDescending(b => b.Value).Select(b => $"{b.Key} ×{b.Value}")) + ".");
    }

    /// <summary>The FaceGen pair an NPC's face is drawn from: its own, or its template root's, each member of a
    /// leveled template marked one of N.</summary>
    static void FaceGenOf(INpcGetter npc, string label, Func<FormKey, IMajorRecordGetter?> fetch,
                          Dictionary<FormKey, IAssetLinkCache?> raceCaches, FileSink addFile, List<string> notes)
    {
        var (roots, errs) = AssetWalkEdges.FaceRoots(npc, fetch);
        foreach (var err in errs) notes.Add($"{label}: " + (roots.Count == 0 ? "no FaceGen node" : "a template member has no FaceGen node") + $" — {err}.");
        foreach (var root in roots)
        {
            // One cache per race per walk: a walk of many NPCs shares a few races.
            IAssetLinkCache? race = null;
            if (!root.Race.IsNull && !raceCaches.TryGetValue(root.Race.FormKey, out race))
                raceCaches[root.Race.FormKey] = race = fetch(root.Race.FormKey) is IRaceGetter r ? AssetWalkEdges.RaceCache(r) : null;
            var why = root.FormKey == npc.FormKey ? null : $"the face of template root {FormIdToken.Of(root.FormKey)}"
                      + (roots.Count > 1 ? $", one of {roots.Count}" : "");
            foreach (var p in AssetWalkEdges.FaceGen(root, race)) addFile(p, "facegen", label, keyed: true, note: why);
        }
    }

    /// <summary>A dialogue line's voice files: the .fuz Mutagen resolves, or a same-stem .xwm / .wav with its .lip
    /// when no .fuz ships; both listed when both ship, with no claim on which plays.</summary>
    static void VoiceOf(AssetResolver.AssetView view, IDialogResponsesGetter info, string label, IAssetLinkCache cache, FileSink addFile, List<string> notes)
    {
        IReadOnlyList<string> fuzes;
        try { fuzes = AssetWalkEdges.Voice(info, cache); }
        catch (Exception ex) { notes.Add($"{label}: its voice paths were not resolved — {ex.GetType().Name}: {ex.Message}"); return; }
        if (fuzes.Count == 0) { notes.Add($"{label}: Mutagen finds no speaker or voice type for this line, so it has no voice node."); return; }
        foreach (var fuz in fuzes)
        {
            var stem = fuz[..^".fuz".Length];
            var loose = new[] { ".xwm", ".wav" }.Select(x => stem + x).Where(x => view.Resolve(x).Exists).ToList();
            bool hasFuz = view.Resolve(fuz).Exists;
            if (hasFuz || loose.Count == 0)
                addFile(fuz, "voice", label, keyed: true,
                        note: hasFuz && loose.Count > 0 ? "a same-stem " + string.Join(" and ", loose.Select(Path.GetExtension)) + " ships too; which one plays is not claimed" : null);
            foreach (var x in loose) addFile(x, "voice", label, keyed: true, note: hasFuz ? null : "no .fuz ships; this line plays from this file");
            if (!hasFuz && loose.Count > 0) addFile(stem + ".lip", "lip", label, keyed: true);
        }
    }

    /// <summary>The whole order's link cache, with any off-order source overlays last, for Mutagen's voice resolution,
    /// which reads the line's topic, quest, speaker and conditions. Built once per walk, only when a line is reached.</summary>
    static IAssetLinkCache VoiceCache(LoadOrderResolver.OverlaySession session, List<IDisposable> overlays)
        => session.AllMasters().Concat(overlays.OfType<ISkyrimModGetter>()).ToImmutableLinkCache().CreateImmutableAssetLinkCache();
}

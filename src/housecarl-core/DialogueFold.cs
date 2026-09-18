using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// DialogueFold — ONE off-order plugin projected into a dialogue read as if it were enabled. Every answer built
// on it is a PROJECTION, and the caller is told so. Placement, the two depths and the wins rule are contracts in
// docs/architecture/dialogue.md.

/// <summary>One folded topic: the DIAL's EditorID and child list, projected to what the merge reads.</summary>
public sealed record FoldedTopic(FormKey Topic, string? EditorId, IReadOnlyList<InfoLine> Lines);

/// <summary>The content of one off-order plugin, read once; with <see cref="Open"/>, its record bodies too.</summary>
public sealed class DialogueFold : IDisposable
{
    /// <summary>The plugin's filename.</summary>
    public string Plugin { get; }

    /// <summary>The file on disk this fold read.</summary>
    public string Path { get; private init; } = "";

    /// <summary>What rows this fold placed are labelled with: the filename, or something else when it is shadowed.</summary>
    public string Label { get; }

    /// <summary>Where the file was found, in the source pole's own words, so a response can name the copy.</summary>
    public string Where { get; }

    /// <summary>Would this file land in the MASTER BLOCK? The ESL header flag alone does not put it there.</summary>
    public bool InMasterBlock { get; private set; }

    /// <summary>Why this file lands where it does, in the words a response states it with — read off the header,
    /// never guessed.</summary>
    public string Kind { get; private set; } = "it is a regular plugin";

    /// <summary>Which of the three places MO2 would load this file in; set by <see cref="PlaceIn"/>.</summary>
    public enum Where3
    {
        /// <summary>A regular plugin: the END of the order, where a newly enabled plugin lands.</summary>
        EndOfOrder,

        /// <summary>A master: after the LAST master in the load order, ahead of every regular plugin.</summary>
        EndOfMasterBlock,

        /// <summary>A copy of a filename the order ALREADY carries: it takes that plugin's own slot.</summary>
        ActiveSlot,
    }

    /// <summary>Which case this fold is; <see cref="PlaceIn"/> decides it before any lane merges or resolves.</summary>
    public Where3 PlacementKind { get; private set; } = Where3.EndOfOrder;

    /// <summary>The order index the fold sits AT or after — the one number every wins question is asked against.</summary>
    public int SlotIndex { get; private set; } = -1;

    /// <summary>Where the fold was placed, in the one spelling every response states it with.</summary>
    public string Placement { get; private set; } = "not yet placed in the order";

    /// <summary>Work out where this file would load; the three cases are in docs/architecture/dialogue.md.</summary>
    public void PlaceIn(LoadOrderResolver.IndexView view)
    {
        // Named out of the ORDER's own index space, which is what SlotIndex is in.
        string At(int i) => view.PluginNameAt(i) ?? "<none>";
        int Position(int i) => i + 1;

        if (view.ContainsPlugin(Plugin))
        {
            PlacementKind = Where3.ActiveSlot;
            SlotIndex = view.OrderIndexOf(Plugin);
            Placement = $"folded into '{Plugin}'s OWN slot in the load order (position {Position(SlotIndex)} of {view.PluginCount}) — "
                      + "enabling that mod folder swaps the bytes at a position the order already has, so every plugin below it still wins what it overrides";
            return;
        }
        if (InMasterBlock)
        {
            // The last master IN LOAD ORDER, walked — the master block is not always a contiguous prefix.
            int last = -1;
            foreach (var name in view.ScannablePluginNames)
                if (view.IsMasterBlock(name)) last = Math.Max(last, view.OrderIndexOf(name));
            PlacementKind = Where3.EndOfMasterBlock;
            SlotIndex = last;
            Placement = $"folded in at the END OF THE MASTER BLOCK — immediately after '{At(last)}' (position {Position(last)} of {view.PluginCount}), "
                      + $"the last master in the load order, and ahead of every regular plugin below it, because {Kind}";
            return;
        }
        PlacementKind = Where3.EndOfOrder;
        SlotIndex = view.PluginCount - 1;
        Placement = $"folded in LAST, after '{At(SlotIndex)}' (position {Position(SlotIndex)} of {view.PluginCount}), "
                  + "where MO2 puts a newly enabled regular plugin";
    }

    /// <summary>Does this fold WIN a record? Only where nothing below its slot touches it.</summary>
    public bool WinsAgainst(LoadOrderResolver.IndexView view, IReadOnlyList<string>? touching)
        => touching is null || !touching.Any(p => view.OrderIndexOf(p) > SlotIndex);

    readonly Dictionary<FormKey, FoldedTopic> _topics = new();

    /// <summary>Every record the file carries, set only by <see cref="Open"/> and valid until Dispose.</summary>
    Dictionary<FormKey, IMajorRecordGetter>? _records;
    IDisposable? _openFile;

    DialogueFold(string plugin, string label, string where) { Plugin = plugin; Label = label; Where = where; }

    /// <summary>Read the file's own kind off its header: which block it loads in, and the sentence saying why.</summary>
    void TakeKind(ISkyrimModGetter ov)
    {
        bool headerMaster = ov.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master);
        bool esm = ov.ModKey.Type == ModType.Master;
        bool esl = ov.ModKey.Type == ModType.Light;
        InMasterBlock = headerMaster || esm || esl;
        Kind = esm ? "it is a .esm"
             : esl ? "it is a .esl, which the engine loads in the master block"
             : headerMaster ? "it is ESM-flagged in its header"
             : ov.IsSmallMaster ? "it is a regular plugin (its ESL flag makes it light in the FormID space, not a master)"
             : "it is a regular plugin";
    }

    /// <summary>Open the plugin, project every DIAL topic's child list, and close it. Throws what Mutagen throws.
    /// <paramref name="dataDir"/> is the game Data folder, so a localized plugin's strings resolve.</summary>
    public static DialogueFold Read(string plugin, string where, string path, string? dataDir, string? label = null)
    {
        var fold = new DialogueFold(plugin, string.IsNullOrEmpty(label) ? plugin : label!, where) { Path = path };
        var ov = LoadOrderResolver.OpenOverlay(path, string.IsNullOrEmpty(dataDir) ? null : dataDir);
        try
        {
            fold.TakeKind(ov);
            // Typed enumeration walks the DIAL group only; each topic is projected while its body is live.
            foreach (var topic in ov.DialogTopics) fold.TakeTopic(topic);
        }
        finally { (ov as IDisposable)?.Dispose(); }
        return fold;
    }

    /// <summary>As <see cref="Read"/>, but the file STAYS OPEN and EVERY record is held, so a caller can resolve
    /// whole records against the fold; why every record, and the #795 alternative, are in the architecture note.</summary>
    public static DialogueFold Open(string plugin, string where, string path, string? dataDir, string? label = null)
    {
        var fold = new DialogueFold(plugin, string.IsNullOrEmpty(label) ? plugin : label!, where) { Path = path };
        var ov = LoadOrderResolver.OpenOverlay(path, string.IsNullOrEmpty(dataDir) ? null : dataDir);
        fold._openFile = ov as IDisposable;
        try
        {
            fold.TakeKind(ov);
            fold._records = new Dictionary<FormKey, IMajorRecordGetter>();
            foreach (var r in ov.EnumerateMajorRecords())
            {
                fold._records[r.FormKey] = r;
                if (r is IDialogTopicGetter topic) fold.TakeTopic(topic);
            }
        }
        catch { fold.Dispose(); throw; }
        return fold;
    }

    void TakeTopic(IDialogTopicGetter topic)
    {
        _topics[topic.FormKey] = new FoldedTopic(topic.FormKey, topic.EditorID, DialogueInfoOrder.LinesOf(topic));
    }

    /// <summary>The folded copy of this topic, or null when the file does not touch it.</summary>
    public FoldedTopic? Topic(FormKey fk) => _topics.TryGetValue(fk, out var t) ? t : null;

    /// <summary>This file's own body for a record, or null. Only an <see cref="Open"/> fold holds bodies.</summary>
    public IMajorRecordGetter? Record(FormKey fk)
        => _records is not null && _records.TryGetValue(fk, out var r) ? r : null;

    /// <summary>Does the folded file carry this record at all — the existence half of the resolution.</summary>
    public bool Holds(FormKey fk) => _records?.ContainsKey(fk) ?? _topics.ContainsKey(fk);

    /// <summary>Every DIAL topic the file carries, with its body. Empty on a <see cref="Read"/> fold.</summary>
    public IEnumerable<IDialogTopicGetter> TopicBodies
    {
        get
        {
            // The dictionary is taken ONCE: a Dispose mid-enumeration would otherwise throw, not end.
            var records = _records;
            if (records is null) return Array.Empty<IDialogTopicGetter>();
            return _topics.Keys.Select(k => records.TryGetValue(k, out var r) ? r as IDialogTopicGetter : null)
                               .Where(t => t is not null)!;
        }
    }

    public void Dispose()
    {
        _records = null;
        _openFile?.Dispose();
        _openFile = null;
    }
}

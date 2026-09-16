using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

// ======================================================================
//  DialogueFold — ONE off-order plugin projected into a dialogue read as if it were active at the END of the
//  load order, which is where MO2 puts a freshly enabled plugin.
//
//  WHY. A dialogue patch is written, and the question it asks — what does the merged INFO order look like with
//  my file in it — cannot be answered until the file is enabled: the merge reads the active order, and the file
//  is not in it. Folding it at the end answers the question before the enable, and every answer built on it is
//  a PROJECTION: the caller is told which file was folded, on the response and on every line the file placed.
//
//  PLAIN DATA, NOT GETTERS. The DIAL child lists are projected while the overlay is open (the
//  consume-before-advance contract) and the overlay is closed before this object is handed back, so a fold
//  outlives the file handle and holds no plugin open — the same rule the rest of the read surface keeps.
// ======================================================================

/// <summary>One folded topic: the DIAL record the off-order file carries, projected to what the merge reads —
/// its EditorID (for a topic the active order does not have at all) and its child list.</summary>
public sealed record FoldedTopic(FormKey Topic, string? EditorId, IReadOnlyList<InfoLine> Lines);

/// <summary>The DIAL content of one off-order plugin, read once and held as plain data: what
/// <see cref="DialogueValidate.InfoOrders"/> appends to each topic's merge as the last contributor.</summary>
public sealed class DialogueFold
{
    /// <summary>The plugin's filename.</summary>
    public string Plugin { get; }

    /// <summary>The file on disk this fold read.</summary>
    public string Path { get; private init; } = "";

    /// <summary>What every row this fold placed is labelled with. The filename, unless an ACTIVE plugin already
    /// carries that filename — a shadowed on-disk copy addressed by {file, mod} — in which case the label has to
    /// differ, or two contributors to one merge would render under one name and the reader could not tell the
    /// folded lines from the live ones.</summary>
    public string Label { get; }

    /// <summary>Where the file was found, in the words the source pole resolved it with (the off-order arm
    /// statement), so a response can say which copy on disk was folded.</summary>
    public string Where { get; }

    /// <summary>Would this file land in the MASTER BLOCK — the run of plugins the order keeps ahead of every
    /// regular one? A header Master flag or a .esm/.esl filename puts it there; the ESL header flag alone does NOT
    /// (an esp-fe is light in the FormID space and a regular plugin in the order). This decides where the fold is
    /// placed: appending a master to the END would project its lines behind every regular plugin that re-lists
    /// them, which is the opposite of where they would land.</summary>
    public bool InMasterBlock { get; private set; }

    /// <summary>Why this file lands where it does, in the words a response states it with — read off the header,
    /// never guessed.</summary>
    public string Kind { get; private set; } = "it is a regular plugin";

    /// <summary>Where the fold was placed, as every response states it. One spelling, so the banner, the per-topic
    /// note and any other frame cannot describe the position differently.</summary>
    public string Placement => InMasterBlock
        ? $"folded in at the END OF THE MASTER BLOCK — ahead of every regular plugin, because {Kind}"
        : "folded in LAST, where MO2 puts a newly enabled regular plugin";

    readonly Dictionary<FormKey, FoldedTopic> _topics = new();

    DialogueFold(string plugin, string label, string where) { Plugin = plugin; Label = label; Where = where; }

    /// <summary>Read the file's own kind off its header while the overlay is open: which block of the order it
    /// would load in, and the sentence that says why.</summary>
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

    /// <summary>Open the plugin at <paramref name="path"/>, project every DIAL topic's child list, and close it.
    /// Throws what Mutagen throws on a file it cannot parse; the caller names the file in its refusal.
    /// <paramref name="dataDir"/> is the game Data folder, needed so a localized plugin's strings resolve.
    /// <paramref name="label"/> is what rows this fold places are labelled with (see <see cref="Label"/>).</summary>
    public static DialogueFold Read(string plugin, string where, string path, string? dataDir, string? label = null)
    {
        var fold = new DialogueFold(plugin, string.IsNullOrEmpty(label) ? plugin : label!, where) { Path = path };
        var ov = LoadOrderResolver.OpenOverlay(path, string.IsNullOrEmpty(dataDir) ? null : dataDir);
        try
        {
            fold.TakeKind(ov);
            // Typed enumeration walks the DIAL group only, and each topic is projected while its body is live.
            foreach (var topic in ov.DialogTopics)
                fold._topics[topic.FormKey] = new FoldedTopic(topic.FormKey, topic.EditorID,
                                                              DialogueInfoOrder.LinesOf(topic));
        }
        finally { (ov as IDisposable)?.Dispose(); }
        return fold;
    }

    /// <summary>How many DIAL topics this file carries — what a response states so a fold that touches none of
    /// the read's topics reads as a fact rather than as silence.</summary>
    public int TopicCount => _topics.Count;

    /// <summary>The folded copy of this topic, or null when the file does not touch it.</summary>
    public FoldedTopic? Topic(FormKey fk) => _topics.TryGetValue(fk, out var t) ? t : null;
}

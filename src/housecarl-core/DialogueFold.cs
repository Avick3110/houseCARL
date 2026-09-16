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
    /// <summary>The plugin's filename — what every row this fold placed is labelled with.</summary>
    public string Plugin { get; }

    /// <summary>Where the file was found, in the words the source pole resolved it with (the off-order arm
    /// statement), so a response can say which copy on disk was folded.</summary>
    public string Where { get; }

    readonly Dictionary<FormKey, FoldedTopic> _topics = new();

    DialogueFold(string plugin, string where) { Plugin = plugin; Where = where; }

    /// <summary>Open the plugin at <paramref name="path"/>, project every DIAL topic's child list, and close it.
    /// Throws what Mutagen throws on a file it cannot parse; the caller names the file in its refusal.
    /// <paramref name="dataDir"/> is the game Data folder, needed so a localized plugin's strings resolve.</summary>
    public static DialogueFold Read(string plugin, string where, string path, string? dataDir)
    {
        var fold = new DialogueFold(plugin, where);
        var ov = LoadOrderResolver.OpenOverlay(path, string.IsNullOrEmpty(dataDir) ? null : dataDir);
        try
        {
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

using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
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
//  TWO DEPTHS. Read() projects the DIAL child lists to plain data and CLOSES the file, so the merge lane holds
//  no plugin open — the rule the rest of the read surface keeps. Open() also keeps the file's record BODIES,
//  which are overlay-backed and so live only while the file is open: that fold is disposable, and the one lane
//  that takes it (the dialogue check, which resolves whole records against the fold) disposes it at the end of
//  its run.
// ======================================================================

/// <summary>One folded topic: the DIAL record the off-order file carries, projected to what the merge reads —
/// its EditorID (for a topic the active order does not have at all) and its child list.</summary>
public sealed record FoldedTopic(FormKey Topic, string? EditorId, IReadOnlyList<InfoLine> Lines);

/// <summary>The content of one off-order plugin, read once: the DIAL child lists
/// <see cref="DialogueValidate.InfoOrders"/> appends to each topic's merge as the last contributor, and — when the
/// fold was opened with <see cref="Open"/> — the record bodies a validation resolves against.</summary>
public sealed class DialogueFold : IDisposable
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

    /// <summary>Every record the file carries, by FormKey — set only by <see cref="Open"/>. The bodies are
    /// overlay-backed, so they are valid only until <see cref="Dispose"/>.</summary>
    Dictionary<FormKey, IMajorRecordGetter>? _records;
    IDisposable? _openFile;

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
            foreach (var topic in ov.DialogTopics) fold.TakeTopic(topic);
        }
        finally { (ov as IDisposable)?.Dispose(); }
        return fold;
    }

    /// <summary>As <see cref="Read"/>, but the file STAYS OPEN and every record it carries is held, so a caller
    /// can resolve whole records against the fold. Disposable, and the bodies die with it. Throws what Mutagen
    /// throws on a file it cannot parse.
    /// <para>EVERY record, deliberately, where <see cref="Read"/> walks the DIAL group alone: the validation this
    /// serves resolves whatever a record points at — a line's Speaker, that NPC's voice type, a condition's form
    /// parameter, a quest's globals — and a CTDA parameter can name any record type at all. A type filter here
    /// would leave a record the folded file DEFINES reading as "not in the active load order", which is the
    /// silently-wrong answer the fold exists to prevent. The cost is bounded by what the caller named: one
    /// plugin, and the lane that opens it is a patch check.</para></summary>
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

    /// <summary>How many DIAL topics this file carries — what a response states so a fold that touches none of
    /// the read's topics reads as a fact rather than as silence.</summary>
    public int TopicCount => _topics.Count;

    /// <summary>The folded copy of this topic, or null when the file does not touch it.</summary>
    public FoldedTopic? Topic(FormKey fk) => _topics.TryGetValue(fk, out var t) ? t : null;

    /// <summary>This file's own body for a record, or null when it carries none — the fold sits LAST in the
    /// projected order, so what it holds wins. Only an <see cref="Open"/> fold holds bodies.</summary>
    public IMajorRecordGetter? Record(FormKey fk)
        => _records is not null && _records.TryGetValue(fk, out var r) ? r : null;

    /// <summary>Does the folded file carry this record at all? The existence half of the resolution, so a link
    /// into the folded file does not read as dangling.</summary>
    public bool Holds(FormKey fk) => _records?.ContainsKey(fk) ?? _topics.ContainsKey(fk);

    /// <summary>Every DIAL topic the file carries, with its body — what a quest fan-out walks to find the topics
    /// the folded file owns. Empty on a <see cref="Read"/> fold, which holds no bodies.</summary>
    public IEnumerable<IDialogTopicGetter> TopicBodies
    {
        get
        {
            // The dictionary is taken ONCE, here: the sequence is lazy, and a Dispose landing mid-enumeration
            // would otherwise null the field the closure reads and throw instead of ending the sequence.
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

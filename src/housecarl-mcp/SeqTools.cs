using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Writes the start-game-enabled-quest sequence file (<c>Data\SEQ\&lt;plugin&gt;.seq</c>) a plugin needs for
/// its Start-Game-Enabled quests to run at all: ticking the SGE flag does nothing on its own, and without the .seq the
/// quest and anything gated on it silently never starts. The byte format and FormID encoding are
/// load-order-independent (see <see cref="HousecarlCore.SeqFile"/>), so the file is correct the moment it is written
/// and travels with the mod. Both renders state that there is no epoch on this call: it consults no load-order build,
/// so a stamp would name evidence that was never read.</summary>
[McpServerToolType]
public static class SeqTools
{
    [McpServerTool(Name = ToolNames.WriteSeq, Title = "Write a start-game-enabled-quest .seq file"),
     Description(
         "Write the SEQ file (Data\\SEQ\\<plugin>.seq) a plugin needs for its START-GAME-ENABLED quests to actually run. " +
         "Ticking 'Start Game Enabled' on a quest does NOTHING on its own — without the .seq the quest, and any dialogue or " +
         "change gated on it, silently never starts. Pass source= the plugin: its FILENAME (e.g. 'MyQuestMod.esp' — " +
         "located across your MO2 mod folders, enabled or not, the overwrite folder and game Data) or an ABSOLUTE PATH " +
         "(e.g. the path " + ToolNames.Create + " reported for a fresh patch, which is not in the load order yet). The response " +
         "states WHICH copy it read: with the same filename in several folders the call is refused, naming them, rather " +
         "than picking one. houseCARL reads that plugin's start-game-enabled quests and writes the .seq into a houseCARL " +
         "mod folder you enable in MO2. If the plugin is itself in a houseCARL patch folder, the .seq defaults into THAT " +
         "same folder (so enabling the one mod deploys both .esp and .seq); otherwise it lands in a fresh folder (pass " +
         "into= an existing houseCARL patch to keep them together, or patch= to name the new folder). After an IN-PLACE " +
         "edit the .esp is in the MOD's own folder, so pass output_dir= that mod folder and the .seq lands beside it in " +
         "its SEQ\\. When a LANE names the destination (output_dir=/into=, or the plugin's own houseCARL folder) and it " +
         "already holds exactly these bytes, nothing is written and the response says so; with no lane named the fresh " +
         "folder is empty by construction, so that re-run always writes. " +
         "A plugin with no " +
         "start-game-enabled quests needs no .seq — that's reported, nothing is written. The .seq makes the quest START; " +
         "it does not verify the quest or its dialogue is otherwise correct. format='json' returns the same data " +
         "machine-readable. No epoch on this call, and that is a fact not an omission: a .seq is derived from the plugin " +
         "FILE alone (its encoding is load-order-independent), so this call consults no load-order build. Needs houseCARL " +
         "pointed at your MO2 instance (for the output folder).")]
    public static string WriteSeq(
        LoadOrderService svc,
        [Description("SOURCE: the plugin whose start-game-enabled quests need a .seq — a FILENAME ('MyQuestMod.esp', located across enabled, disabled and not-yet-listed mod folders, overwrite, and game Data) or an ABSOLUTE path to the .esp/.esm/.esl. A filename provided by several locations is refused, naming them.")]
            string source,
        [Description("LANE: base name for a NEW patch-mod folder the .seq lands in (default: the plugin's own houseCARL folder if it's in one, else 'houseCARL_SEQ'); auto-suffixed if taken.")]
            string? patch = null,
        [Description("LANE: filename of an existing houseCARL patch mod to write the .seq into (e.g. the patch that holds the .esp, so one mod deploys both).")]
            string? into = null,
        [Description("LANE: land the .seq in a folder of YOUR choosing instead of a houseCARL patch folder — pass the mod-folder ROOT (typically the plugin's own mod, after an in-place edit); houseCARL appends SEQ\\ (and won't double it if you already point at a ...\\SEQ folder). When set, patch=/into= are ignored. An existing .seq at that path is OVERWRITTEN with no backup (the response says 'replaced'), and a byte-identical one is left alone with only its timestamp refreshed if it was older than the plugin. The game reads SEQ files from exactly <mods>\\<YourMod>\\SEQ, the MO2 overwrite folder, or <Data>\\SEQ — anywhere else the .seq is still written and you're warned it won't be read (a nested path like <mods>\\<YourMod>\\Sub is 'under mods' but does NOT deploy).")]
            string? output_dir = null,
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the render; past it trailing quest rows are dropped with an explicit notice (never silent). 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.WriteSeq, () =>
    {
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } cfgPrompt)
            return json ? JsonWire.RenderError(cfgPrompt, null) : cfgPrompt;

        // Lane exclusivity, matching the sibling write tools. output_dir= supersedes patch=/into= and says so rather
        // than silently ignoring them; patch= and into= together are two ways of naming a houseCARL folder with no
        // way to choose, so that pair refuses.
        //
        // Order matters: output_dir= is checked first, because running the pair check first would refuse a call that
        // named all three over two parameters output_dir='s own description promises to ignore. The compile lane
        // resolves output_dir first for the same reason.
        string? outputNote = null;
        if (!string.IsNullOrWhiteSpace(output_dir))
        {
            if (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: output_dir= was given, so patch=/into= are ignored (the .seq lands in output_dir, not a houseCARL patch folder).";
        }
        else if (!string.IsNullOrWhiteSpace(patch) && !string.IsNullOrWhiteSpace(into))
        {
            var laneErr = $"patch='{patch}' names a NEW mod folder for the .seq, but into='{into}' writes it into an existing houseCARL "
                        + "patch — the two lanes are exclusive. Drop patch= to write into that patch, or drop into= to make a new folder.";
            return json ? JsonWire.RenderError(laneErr, null) : "error: " + laneErr;
        }

        var o = svc.WriteSeq(source, patch, into, output_dir);
        if (json) return JsonWire.RenderSeqOutcome(o, max_chars, outputNote);
        // The ignored-lane note rides the refusal too: a lane named and ignored still needs saying when the write
        // failed.
        if (!o.Success) return "error: " + o.Error + (outputNote is { Length: > 0 } ne ? "\n" + ne : "");
        return Render(o, max_chars, outputNote);
    });

    internal static string Render(SeqOutcome o, int maxChars = 0, string? outputNote = null)
    {
        // No SGE quests is an explicit no-op: never a silent empty .seq, never a misleading "done". It carries the
        // ignored-lane note too, so this render and the json twin say the same thing.
        if (o.Quests.Count == 0)
            return $"no start-game-enabled quests in {o.PluginFileName}{ReadFrom(o)} — {WriteSentences.Twins.SeqNoQuests}. " +
                   "If a quest SHOULD start at game start, set its Start Game Enabled flag first, then write the .seq."
                   // This return happens before any folder is resolved, so an unusable output_dir= was never
                   // diagnosed: "your folder is fine" and "we never checked your folder" must not read the same.
                   + (o.UserChoseOutput ? "\nnote: output_dir= was not resolved or checked — nothing needed writing, so no destination was touched." : "")
                   + (outputNote is { Length: > 0 } n0 ? "\n" + n0 : "");

        var sb = new StringBuilder();
        var seqName = Path.GetFileName(o.SeqPath);
        // "already current" is its own headline, not a "wrote" with a caveat further down: the first line is what a
        // caller reads, and a skipped write reported as a write reads exactly like a silent failure.
        sb.Append(o.Unchanged ? "unchanged — " : o.Replaced ? "replaced " : "wrote ").Append(seqName).Append(": ").Append(o.Quests.Count)
          .Append(o.Quests.Count == 1 ? " start-game-enabled quest" : " start-game-enabled quests")
          .Append(o.Unchanged
              ? "; " + WriteSentences.Twins.SeqUnchanged + "."
              // "replaced" is its own word because on the output_dir lane the file that was there may be the mod's own
              // .seq and no backup is kept. The no-backup alarm is scoped to that lane: re-generating over
              // houseCARL's own previous output is the ordinary workflow, not a loss.
              : o.Replaced
                  // Identical replaced bytes lost nothing — the only way here is a byte-identical destination whose
                  // timestamp refresh failed — so no alarm.
                  ? (o.ReplacedSameBytes
                      ? "; " + WriteSentences.Twins.SeqReplacedSameBytes
                      : o.UserChoseOutput
                          ? "; " + WriteSentences.Twins.SeqReplacedUserFolder
                          : "; " + WriteSentences.Twins.SeqReplacedOwnFolder)
                  : "")
          .Append('\n');
        // Only the quest rows are budgeted; the path and next-step lines below stay outside it, because a truncated
        // list still has to say where the file landed.
        int cap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Quests.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // Not "raise max_chars to see the rest": re-running writes the .seq again, and with no lane named for
                // a plugin outside a houseCARL folder that means a second auto-suffixed mod folder holding a
                // duplicate. Nothing is missing from the file, so the notice prices the re-run instead of
                // prescribing it. The json twin's quest-row cut uses the same remedy sentence.
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Quests.Count)
                  .Append(" quest(s) listed at max_chars=").Append(cap).Append("; ")
                  .Append(WriteSentences.Twins.SeqListCutRemedy).Append("]\n");
                break;
            }
            var q = o.Quests[i];
            sb.Append("  ").Append(q.EditorId is { Length: > 0 } e ? e : "(no EditorID)")
              .Append("  →  0x").AppendFormat("{0:X8}", q.OnDiskFormId).Append('\n');
        }
        // Which copy of the source was read: a filename can be provided by more than one layer, and the quests came
        // from exactly one of them.
        if (o.ResolvedFrom is { Length: > 0 })
            sb.Append("source: ").Append(o.PluginFileName).Append(" — read from ").Append(o.ResolvedFrom)
              .Append(o.PluginPath is { Length: > 0 } p ? $" ({p})" : "").Append('\n');
        sb.Append("path: ").Append(o.SeqPath).Append('\n');
        // Where the file landed decides the next step, so the three destinations get three different sentences. An
        // output_dir= folder is the user's own mod, so "enable this houseCARL mod" would name a mod that does not exist.
        sb.Append(o.UserChoseOutput
            ? "the .seq is in the folder you named (output_dir) — no houseCARL mod folder was created; make sure that mod is enabled in MO2 so the game reads Data\\SEQ\\."
            : o.WroteIntoPluginFolder
                ? "the .seq is in the plugin's OWN houseCARL folder — enabling that one mod in MO2 deploys both the .esp and its .seq."
                : "the .seq is in a houseCARL mod folder — enable it in MO2 (AND make sure the plugin itself is enabled) so the game reads Data\\SEQ\\.");
        // A skipped write still refreshes the timestamp: a real change to the file's metadata, and what keeps
        // validate_dialogue's mtime-based SEQ lint agreeing with this call.
        if (o.TimestampRefreshed)
            // The claim is only that THIS file is now newer than the plugin. validate_dialogue lints the .seq the VFS
            // serves, which is this file only when this folder wins the SEQ\ conflict and is enabled, so the sentence
            // must not promise that tool's verdict.
            sb.Append('\n').Append(WriteSentences.Twins.SeqTimestampRefreshed);
        // Never a clean "done" for a .seq the engine will not read — the quests would stay silently dead.
        if (o.DeployWarning is { Length: > 0 } dw) sb.Append('\n').Append(dw);
        if (outputNote is { Length: > 0 }) sb.Append('\n').Append(outputNote);
        // The absent epoch is stated here as well as in the json twin's `epoch_note`: a response with no epoch line
        // is otherwise indistinguishable from one that dropped the stamp.
        sb.Append("\nno epoch on this call: ").Append(WriteSentences.Twins.SeqNoEpoch);
        // A written .seq makes the quest start; it is no guarantee the quest or its dialogue is otherwise correct.
        sb.Append("\nnote: ").Append(WriteSentences.Twins.SeqStandingLimit);
        return sb.ToString();
    }

    /// <summary>The read-from clause for the nothing-to-do render. "No SGE quests" is a claim about one specific file,
    /// so it names which: a stale copy in a disabled folder has a different quest set.</summary>
    static string ReadFrom(SeqOutcome o)
        => o.ResolvedFrom is { Length: > 0 } w ? $" (read from {w}{(o.PluginPath is { Length: > 0 } p ? $": {p}" : "")})" : "";
}

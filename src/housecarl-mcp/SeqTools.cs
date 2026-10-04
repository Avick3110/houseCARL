using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_write_seq — writes the <c>Data\SEQ\&lt;plugin&gt;.seq</c> a plugin's Start-Game-Enabled quests need to run at all. Its bytes are load-order-independent (<see cref="HousecarlCore.SeqFile"/>), so the call consults no load-order build and both renders say there is no epoch.</summary>
[McpServerToolType]
public static class SeqTools
{
    [McpServerTool(Name = ToolNames.WriteSeq, Title = "Write a start-game-enabled-quest .seq file"),
     Description(
         "Write the .seq file (Data\\SEQ\\<plugin>.seq) a plugin needs for its Start Game Enabled quests to run. Without it " +
         "such a quest, and anything gated on it, silently never starts. The .seq makes the quests start; it does not check " +
         "that they or their dialogue are otherwise correct. A plugin with no such quests needs no .seq: that is reported " +
         "and nothing is written.\n\n" +
         "By default the .seq lands in the plugin's own houseCARL mod folder when it is in one, so enabling that one mod " +
         "deploys both; otherwise in a fresh houseCARL folder you enable in MO2. After an in-place edit, pass out_path= the " +
         "plugin's own mod folder. When the destination is the plugin's own houseCARL folder, into= or out_path= and it " +
         "already holds exactly these bytes, nothing is written and the reply says 'unchanged'. A fresh folder (patch=, or " +
         "the default for a plugin not in a houseCARL folder) always gets a write, so such a re-run makes another folder.\n\n" +
         "The reply carries no epoch: a .seq is derived from the plugin file alone. Needs houseCARL pointed at your MO2 " +
         "instance.")]
    public static string WriteSeq(
        LoadOrderService svc,
        [Description("The plugin: a filename ('MyQuestMod.esp', found across enabled and disabled mod folders, the overwrite folder and game Data) or an absolute path to the .esp/.esm/.esl, e.g. the path " + ToolNames.Create + " reported for a patch not yet in the load order. The reply says which copy it read.")]
            string source,
        [Description("Base name for a new mod folder for the .seq (default 'houseCARL_SEQ'); auto-suffixed if taken. Not with into=.")]
            string? patch = null,
        [Description("Filename of an existing houseCARL patch mod to write the .seq into (e.g. the patch holding the .esp). Not with patch=.")]
            string? into = null,
        [Description("Absolute path to a mod-folder root of your choosing, typically the plugin's own mod after an in-place edit; houseCARL appends SEQ\\ unless the path already ends in it. patch= and into= are then ignored. An existing .seq there is overwritten with no backup (the reply says 'replaced'). The game reads .seq files only from <mods>\\<YourMod>\\SEQ, the MO2 overwrite folder or <Data>\\SEQ; anywhere else, including a nested folder under a mod, the file is still written with a warning that it will not be read.")]
            string? out_path = null,
        [Description("'text' (default) or 'json' (the same data, machine-readable).")]
            string? format = null,
        [Description("Character ceiling on the reply; past it trailing quest rows are cut with a notice. 0 = a default under the host's response limit.")]
            int max_chars = 0) => Guard.Tool(ToolNames.WriteSeq, () =>
    {
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } cfgPrompt)
            return json ? JsonWire.RenderError(cfgPrompt, null) : cfgPrompt;

        // Lane exclusivity, out_path= first; contract in docs/architecture/write-path.md.
        string? outputNote = null;
        if (!string.IsNullOrWhiteSpace(out_path))
        {
            if (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into))
                outputNote = "note: out_path= was given, so patch=/into= are ignored (the .seq lands in out_path, not a houseCARL patch folder).";
        }
        else if (!string.IsNullOrWhiteSpace(patch) && !string.IsNullOrWhiteSpace(into))
        {
            var laneErr = $"patch='{patch}' names a NEW mod folder for the .seq, but into='{into}' writes it into an existing houseCARL "
                        + "patch — the two lanes are exclusive. Drop patch= to write into that patch, or drop into= to make a new folder.";
            return json ? JsonWire.RenderError(laneErr, null) : "error: " + laneErr;
        }

        var o = svc.WriteSeq(source, patch, into, out_path);
        if (json) return JsonWire.RenderSeqOutcome(o, max_chars, outputNote);
        // The ignored-lane note rides the refusal too.
        if (!o.Success) return "error: " + o.Error + (outputNote is { Length: > 0 } ne ? "\n" + ne : "");
        return Render(o, max_chars, outputNote);
    });

    internal static string Render(SeqOutcome o, int maxChars = 0, string? outputNote = null)
    {
        // No SGE quests is an explicit no-op: never a silent empty .seq, never a misleading "done".
        if (o.Quests.Count == 0)
            return $"no start-game-enabled quests in {o.PluginFileName}{ReadFrom(o)} — {WriteSentences.Twins.SeqNoQuests}. " +
                   "If a quest SHOULD start at game start, set its Start Game Enabled flag first, then write the .seq."
                   // This returns before any folder is resolved, so an unusable out_path= was never diagnosed.
                   + (o.UserChoseOutput ? "\nnote: out_path= was not resolved or checked — nothing needed writing, so no destination was touched." : "")
                   + (outputNote is { Length: > 0 } n0 ? "\n" + n0 : "");

        var sb = new StringBuilder();
        var seqName = Path.GetFileName(o.SeqPath);
        // "already current" is its own headline; contract in docs/architecture/write-path.md.
        sb.Append(o.Unchanged ? "unchanged — " : o.Replaced ? "replaced " : "wrote ").Append(seqName).Append(": ").Append(o.Quests.Count)
          .Append(o.Quests.Count == 1 ? " start-game-enabled quest" : " start-game-enabled quests")
          .Append(o.Unchanged
              ? "; " + WriteSentences.Twins.SeqUnchanged + "."
              // "replaced" is its own word, and the no-backup alarm is scoped to the out_path lane.
              : o.Replaced
                  // Identical replaced bytes lost nothing, so no alarm.
                  ? (o.ReplacedSameBytes
                      ? "; " + WriteSentences.Twins.SeqReplacedSameBytes
                      : o.UserChoseOutput
                          ? "; " + WriteSentences.Twins.SeqReplacedUserFolder
                          : "; " + WriteSentences.Twins.SeqReplacedOwnFolder)
                  : "")
          .Append('\n');
        // Only the quest rows are budgeted: a truncated list still has to say where the file landed.
        int cap = WriteSentences.Cap(maxChars);
        for (int i = 0; i < o.Quests.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // Not "raise max_chars to see the rest": with no lane named a re-run writes a second mod folder.
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Quests.Count)
                  .Append(" quest(s) listed at max_chars=").Append(cap).Append("; ")
                  .Append(WriteSentences.Twins.SeqListCutRemedy).Append("]\n");
                break;
            }
            var q = o.Quests[i];
            sb.Append("  ").Append(q.EditorId is { Length: > 0 } e ? e : "(no EditorID)")
              .Append("  →  0x").AppendFormat("{0:X8}", q.OnDiskFormId).Append('\n');
        }
        // Which copy of the source was read, several layers being able to provide one filename.
        if (o.ResolvedFrom is { Length: > 0 })
            sb.Append("source: ").Append(o.PluginFileName).Append(" — read from ").Append(o.ResolvedFrom)
              .Append(o.PluginPath is { Length: > 0 } p ? $" ({p})" : "").Append('\n');
        sb.Append("path: ").Append(o.SeqPath).Append('\n');
        // Where the file landed decides the next step, so the three destinations get three different sentences.
        sb.Append(o.UserChoseOutput
            ? "the .seq is in the folder you named (out_path) — no houseCARL mod folder was created; make sure that mod is enabled in MO2 so the game reads Data\\SEQ\\."
            : o.WroteIntoPluginFolder
                ? "the .seq is in the plugin's OWN houseCARL folder — enabling that one mod in MO2 deploys both the .esp and its .seq."
                : "the .seq is in a houseCARL mod folder — enable it in MO2 (AND make sure the plugin itself is enabled) so the game reads Data\\SEQ\\.");
        // A skipped write still refreshes the timestamp, keeping the SEQ lint in agreement with this call.
        if (o.TimestampRefreshed)
            // The claim is only that THIS file is now newer than the plugin, not what the VFS serves the lint.
            sb.Append('\n').Append(WriteSentences.Twins.SeqTimestampRefreshed);
        // Never a clean "done" for a .seq the engine will not read.
        if (o.DeployWarning is { Length: > 0 } dw) sb.Append('\n').Append(dw);
        if (outputNote is { Length: > 0 }) sb.Append('\n').Append(outputNote);
        // The absent epoch is STATED; contract in docs/architecture/write-path.md.
        sb.Append("\nno epoch on this call: ").Append(WriteSentences.Twins.SeqNoEpoch);
        sb.Append("\nnote: ").Append(WriteSentences.Twins.SeqStandingLimit);
        return sb.ToString();
    }

    /// <summary>The read-from clause for the nothing-to-do render, because "no SGE quests" is a claim about one file.</summary>
    static string ReadFrom(SeqOutcome o)
        => o.ResolvedFrom is { Length: > 0 } w ? $" (read from {w}{(o.PluginPath is { Length: > 0 } p ? $": {p}" : "")})" : "";
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL SEQ rider — housecarl_write_seq. Writes the start-game-enabled-quest "sequence" file
/// (<c>Data\SEQ\&lt;plugin&gt;.seq</c>) a plugin needs for its Start-Game-Enabled quests to actually run. Ticking the SGE
/// flag in the CK/xEdit does nothing on its own; without the .seq the quest — and any dialogue or world change gated on it
/// — silently never starts (the exact silent-failure class houseCARL refuses, Q3). This is the data-layer equivalent of
/// the CK's on-save SEQ generation / xEdit's "Create SEQ file": it reads the plugin's SGE quests and emits the file into a
/// reviewable houseCARL mod folder (originals untouched — same folder-per-patch model as every other write). The byte
/// format and FormID encoding are load-order-independent (see <see cref="HousecarlCore.SeqFile"/>), so the .seq is correct
/// the moment it's written and travels with the mod.
/// </summary>
[McpServerToolType]
public static class SeqTools
{
    [McpServerTool(Name = "housecarl_write_seq", Title = "Write a start-game-enabled-quest .seq file"),
     Description(
         "Write the SEQ file (Data\\SEQ\\<plugin>.seq) a plugin needs for its START-GAME-ENABLED quests to actually run. " +
         "Ticking 'Start Game Enabled' on a quest does NOTHING on its own — without the .seq the quest, and any dialogue or " +
         "change gated on it, silently never starts. Pass plugin= the path to the .esp/.esm/.esl (e.g. the path " +
         "housecarl_create_record reported for your patch); houseCARL reads its start-game-enabled quests and writes the " +
         ".seq into a houseCARL mod folder you enable in MO2. If the plugin is itself in a houseCARL patch folder, the .seq " +
         "defaults into THAT same folder (so enabling the one mod deploys both .esp and .seq); otherwise it lands in a fresh " +
         "folder (pass into= an existing houseCARL patch to keep them together). A plugin with no start-game-enabled quests " +
         "needs no .seq — that's reported, nothing is written. The .seq makes the quest START; it does not verify the quest " +
         "or its dialogue is otherwise correct. Needs houseCARL pointed at your MO2 instance (for the output folder).")]
    public static string WriteSeq(
        LoadOrderService svc,
        [Description("Full path to the plugin (.esp/.esm/.esl) whose start-game-enabled quests need a .seq.")]
            string plugin,
        [Description("Optional. Base name for a NEW patch-mod folder the .seq lands in (default: the plugin's own houseCARL folder if it's in one, else 'houseCARL_SEQ'); auto-suffixed if taken.")]
            string? patch_name = null,
        [Description("Optional. Filename of an existing houseCARL patch mod to write the .seq into (e.g. the patch that holds the .esp, so one mod deploys both).")]
            string? into = null) => Guard.Tool("housecarl_write_seq", () =>
    {
        if (svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;

        var o = svc.WriteSeq(plugin, patch_name, into);
        if (!o.Success) return "error: " + o.Error;
        return Render(o);
    });

    internal static string Render(SeqOutcome o)
    {
        // No SGE quests → a clean, explicit no-op (Q3: never a silent empty .seq, never a misleading "done").
        if (o.Quests.Count == 0)
            return $"no start-game-enabled quests in {o.PluginFileName} — no .seq is needed (a .seq lists only quests with the " +
                   "Start Game Enabled flag). Nothing written. If a quest SHOULD start at game start, set its Start Game Enabled " +
                   "flag first, then write the .seq.";

        var sb = new StringBuilder();
        var seqName = Path.GetFileName(o.SeqPath);
        sb.Append("wrote ").Append(seqName).Append(": ").Append(o.Quests.Count)
          .Append(o.Quests.Count == 1 ? " start-game-enabled quest" : " start-game-enabled quests").Append('\n');
        foreach (var q in o.Quests)
            sb.Append("  ").Append(q.EditorId is { Length: > 0 } e ? e : "(no EditorID)")
              .Append("  →  0x").AppendFormat("{0:X8}", q.OnDiskFormId).Append('\n');
        sb.Append("path: ").Append(o.SeqPath).Append('\n');
        sb.Append(o.WroteIntoPluginFolder
            ? "the .seq is in the plugin's OWN houseCARL folder — enabling that one mod in MO2 deploys both the .esp and its .seq."
            : "the .seq is in a houseCARL mod folder — enable it in MO2 (AND make sure the plugin itself is enabled) so the game reads Data\\SEQ\\.");
        // Q3 standing limit: a written .seq makes the quest START; it is not a guarantee the quest/dialogue is otherwise correct.
        sb.Append("\nnote: this makes the quest(s) START at game start; it does not verify the quest or its dialogue is otherwise " +
                  "well-formed (use housecarl_validate_dialogue for the dialogue graph).");
        return sb.ToString();
    }
}

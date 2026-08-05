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
///
/// <para><b>The 2.0 surface (tool-surface-2.0 W3 PR 2).</b> The tool NAME is kept (SPEC §6.1: an S1 selection with a
/// declared S2 file output, tiny, and its teaching — when a .seq is needed at all — is real), so this is a
/// parameter migration rather than a new tool: <c>plugin</c> → <c>source</c> (the SOURCE pole, §5.3, now resolving a
/// bare FILENAME across the MO2 folders instead of demanding a full path), <c>patch_name</c> → <c>patch</c>, plus
/// TRANSPORT (<c>format=json</c>, <c>max_chars</c>). The alias layer maps the old spellings by name.</para>
///
/// <para><b>No epoch, stated rather than omitted</b> (SPEC §2.1.1): every other write response carries the
/// fingerprint of the index build it resolved against. This call consults NO build — the .seq is derived from one
/// plugin FILE, and its FormID encoding is load-order-independent by design — so a stamp here would name evidence
/// that was never read. The render says so instead of leaving a caller to wonder which of the two it is.</para>
/// </summary>
[McpServerToolType]
public static class SeqTools
{
    [McpServerTool(Name = "housecarl_write_seq", Title = "Write a start-game-enabled-quest .seq file"),
     Description(
         "Write the SEQ file (Data\\SEQ\\<plugin>.seq) a plugin needs for its START-GAME-ENABLED quests to actually run. " +
         "Ticking 'Start Game Enabled' on a quest does NOTHING on its own — without the .seq the quest, and any dialogue or " +
         "change gated on it, silently never starts. Pass source= the plugin: its FILENAME (e.g. 'MyQuestMod.esp' — " +
         "located across your MO2 mod folders, enabled or not, the overwrite folder and game Data) or an ABSOLUTE PATH " +
         "(e.g. the path housecarl_create reported for a fresh patch, which is not in the load order yet). The response " +
         "states WHICH copy it read: with the same filename in several folders the call is refused, naming them, rather " +
         "than picking one. houseCARL reads that plugin's start-game-enabled quests and writes the .seq into a houseCARL " +
         "mod folder you enable in MO2. If the plugin is itself in a houseCARL patch folder, the .seq defaults into THAT " +
         "same folder (so enabling the one mod deploys both .esp and .seq); otherwise it lands in a fresh folder (pass " +
         "into= an existing houseCARL patch to keep them together, or patch= to name the new folder). A plugin with no " +
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
        [Description("TRANSPORT: 'text' (default) | 'json' (the same data, machine-readable).")]
            string? format = null,
        [Description("TRANSPORT: character ceiling on the render; past it trailing quest rows are dropped with an explicit notice (never silent). 0 = a safe default kept under the host's per-response limit.")]
            int max_chars = 0) => Guard.Tool("housecarl_write_seq", () =>
    {
        bool json = Wire.WantsJson(format, out var ferr);
        if (ferr is not null) return ferr;
        if (svc.ConfigPromptOrNull() is { } cfgPrompt)
            return json ? JsonWire.RenderError(cfgPrompt, null) : cfgPrompt;

        // LANE exclusivity, the same rule the sibling write tools enforce (PR #311 review 4 [low]). This PR is what
        // renamed the pair onto the 2.0 words and labelled BOTH "LANE:", and a LANE that can be named alongside
        // another and silently lose is the accepted-and-ignored class the grammar exists to close:
        // ResolvePatchModFolder returns from the into= branch before patch= is ever read, so the .seq landed in
        // into='s folder and the response said nothing about the folder patch= asked for.
        if (!string.IsNullOrWhiteSpace(patch) && !string.IsNullOrWhiteSpace(into))
        {
            var laneErr = $"patch='{patch}' names a NEW mod folder for the .seq, but into='{into}' writes it into an existing houseCARL "
                        + "patch — the two lanes are exclusive. Drop patch= to write into that patch, or drop into= to make a new folder.";
            return json ? JsonWire.RenderError(laneErr, null) : "error: " + laneErr;
        }

        var o = svc.WriteSeq(source, patch, into);
        if (json) return JsonWire.RenderSeqOutcome(o, max_chars);
        if (!o.Success) return "error: " + o.Error;
        return Render(o, max_chars);
    });

    internal static string Render(SeqOutcome o, int maxChars = 0)
    {
        // No SGE quests → a clean, explicit no-op (Q3: never a silent empty .seq, never a misleading "done").
        if (o.Quests.Count == 0)
            return $"no start-game-enabled quests in {o.PluginFileName}{ReadFrom(o)} — no .seq is needed (a .seq lists only quests with the " +
                   "Start Game Enabled flag). Nothing written. If a quest SHOULD start at game start, set its Start Game Enabled " +
                   "flag first, then write the .seq.";

        var sb = new StringBuilder();
        var seqName = Path.GetFileName(o.SeqPath);
        sb.Append("wrote ").Append(seqName).Append(": ").Append(o.Quests.Count)
          .Append(o.Quests.Count == 1 ? " start-game-enabled quest" : " start-game-enabled quests").Append('\n');
        // Budgeted like the sibling write renders (PR #311 review [low-medium]): max_chars= promises "past it
        // trailing quest rows are dropped with an explicit notice (never silent)", and a plugin with hundreds of
        // start-game-enabled quests would otherwise render every row and let the HOST cut the response with no
        // in-band signal. The path/next-step lines below stay outside the budget — a truncated list still needs
        // to say where the file landed.
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;
        for (int i = 0; i < o.Quests.Count; i++)
        {
            if (sb.Length >= cap)
            {
                // Not "raise max_chars to see the rest" — the same class as create's notice (PR #311 review 3
                // round-2 / review 4), one tool over: re-running write_seq with a wider ceiling WRITES THE .seq
                // AGAIN, and with no lane named for a plugin outside a houseCARL folder that is a second
                // auto-suffixed mod folder holding a duplicate. Nothing is missing from the FILE, so the honest
                // notice says so and prices the re-run instead of prescribing it. (Not a review-4 finding — a
                // sibling spotted while folding one; declared on the PR rather than folded silently.)
                sb.Append("  ... [truncated: ").Append(i).Append(" of ").Append(o.Quests.Count)
                  .Append(" quest(s) listed at max_chars=").Append(cap)
                  .Append("; the .seq itself carries ALL of them — nothing is missing from the FILE. Re-run only if you need this "
                        + "LIST widened: that writes the .seq again (into= the folder named below to keep it in one)]\n");
                break;
            }
            var q = o.Quests[i];
            sb.Append("  ").Append(q.EditorId is { Length: > 0 } e ? e : "(no EditorID)")
              .Append("  →  0x").AppendFormat("{0:X8}", q.OnDiskFormId).Append('\n');
        }
        // WHICH copy of the source was read (§4.2 — the arm is always stated): a filename can be provided by more
        // than one layer, and the quests came from exactly one of them.
        if (o.ResolvedFrom is { Length: > 0 })
            sb.Append("source: ").Append(o.PluginFileName).Append(" — read from ").Append(o.ResolvedFrom)
              .Append(o.PluginPath is { Length: > 0 } p ? $" ({p})" : "").Append('\n');
        sb.Append("path: ").Append(o.SeqPath).Append('\n');
        sb.Append(o.WroteIntoPluginFolder
            ? "the .seq is in the plugin's OWN houseCARL folder — enabling that one mod in MO2 deploys both the .esp and its .seq."
            : "the .seq is in a houseCARL mod folder — enable it in MO2 (AND make sure the plugin itself is enabled) so the game reads Data\\SEQ\\.");
        // The ABSENT epoch, stated on the DEFAULT transport too (PR #311 review 6 [low]). The json twin has carried
        // `epoch_note` since it was written; the text render said nothing — so a caller on the transport most of
        // them use saw a response with no epoch= line, which is the same observable a DROPPED stamp would produce.
        // The class-doc paragraph above claimed "the render says so"; it was true of one render out of two.
        sb.Append("\nno epoch on this call, and that is a fact rather than an omission: a .seq is derived from the plugin " +
                  "FILE alone (its FormID encoding is load-order-independent), so nothing here consulted a load-order build.");
        // Q3 standing limit: a written .seq makes the quest START; it is not a guarantee the quest/dialogue is otherwise correct.
        sb.Append("\nnote: this makes the quest(s) START at game start; it does not verify the quest or its dialogue is otherwise " +
                  "well-formed (use housecarl_validate_dialogue for the dialogue graph).");
        return sb.ToString();
    }

    /// <summary>The read-from clause for the nothing-to-do render — "no SGE quests" is a claim ABOUT a specific file,
    /// so it names which one (a stale copy in a disabled folder has a different quest set, and reporting that as
    /// "nothing to do" without saying whose is the silent-wrong-answer class Q3 refuses).</summary>
    static string ReadFrom(SeqOutcome o)
        => o.ResolvedFrom is { Length: > 0 } w ? $" (read from {w}{(o.PluginPath is { Length: > 0 } p ? $": {p}" : "")})" : "";
}

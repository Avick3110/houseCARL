using System.Text;

namespace HousecarlMcp;

/// <summary>The composers a dialogue report is rendered from: a topic block, a findings list, a .seq note.
/// Budget-bounded, with an explicit cut at max_chars. The validation itself is
/// <see cref="HousecarlCore.DialogueValidate"/>; whole-report composition is <see cref="DialogueSweepRender"/>.</summary>
internal static class DialogueWire
{
    /// <summary>Render a finding list, each as "[X]/[!] message" at <paramref name="pad"/>. At the cap it appends an
    /// explicit truncation notice and returns false so the caller stops. Shared by both finding levels.</summary>
    internal static bool AppendIssues(StringBuilder sb, IReadOnlyList<DialogueIssue> issues, string pad, int cap)
    {
        foreach (var iss in issues)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("... [truncated at max_chars]\n"); return false; }
            sb.Append(pad).Append(iss.Severity == DialogueIssueSeverity.Problem ? "[X] " : "[!] ")
              .Append(iss.Message).Append('\n');
        }
        return true;
    }

    /// <summary>One topic's block, without the merged INFO order (<c>records project=info_order</c> has it).</summary>
    internal static void AppendTopic(StringBuilder sb, TopicValidation t, bool indent, int cap)
    {
        string pad = indent ? "  " : "";
        sb.Append(pad).Append("topic ").Append(Edid(t.TopicEditorId)).Append(" (").Append(FormIdToken.Of(t.Topic)).Append(')')
          .Append(" — winner ").Append(t.WinnerPlugin);
        // Which COPY provided it, in the text render only: the name itself stays a plain filename.
        if (t.WinnerIsFolded) sb.Append(" [the folded off-order copy]");
        sb.Append('\n');
        sb.Append(pad).Append("  ").Append(t.InfoCount).Append(t.InfoCount == 1 ? " INFO record" : " INFO records");
        if (t.ConditionedInfoCount > 0) sb.Append("; ").Append(t.ConditionedInfoCount).Append(" carry conditions (CTDA)");
        if (t.DeletedInfoCount > 0) sb.Append("; ").Append(t.DeletedInfoCount).Append(" deleted line(s) skipped");
        sb.Append('\n');
        // When the number and the SNAM marker disagree, the marker wins (DialogueSubtype.MarkerDisagreesWithSubtype).
        sb.Append(pad).Append("  category=").Append(t.Category).Append("  subtype=").Append(t.Subtype)
          .Append(t.SubtypeDisagreesWithMarker ? " (stale)" : "")
          .Append("  subtype_marker=").Append(t.SubtypeName)
          .Append(t.SubtypeDisagreesWithMarker ? " (authoritative)" : "").Append('\n');

        // Whether a Papyrus.log entry is even possible for a line. Always shown for a topic with live INFOs.
        if (t.InfoCount > 0)
            sb.Append(pad).Append("  result-script fragments: ").Append(t.FragmentInfoCount).Append(" of ").Append(t.InfoCount)
              .Append(t.InfoCount == 1 ? " INFO carries one" : " INFOs carry one")
              .Append(" — a fragment runs code that can surface in Papyrus.log (on error or an explicit trace); a plain voiced line has no code path, so no log entry doesn't mean it didn't play.\n");

        // --- graph issues (PNAM chain, quest + branch wiring) ---
        if (t.Issues.Count == 0)
        {
            // The conditions clause is asserted only when the topic actually has conditioned INFOs.
            sb.Append(pad).Append("  graph: OK — quest + branch wiring resolve, LinkTo targets resolve, no dangling PNAM");
            sb.Append(t.ConditionedInfoCount > 0
                ? ", conditions well-formed (their form references + alias indices resolve).\n"
                : ".\n");
        }
        else
        {
            sb.Append(pad).Append("  graph: ").Append(t.Issues.Count).Append(" issue(s):\n");
            if (!AppendIssues(sb, t.Issues, pad + "    ", cap)) return;
        }

        AppendVoice(sb, t, pad, cap);
        AppendScripts(sb, t, pad, cap);
    }

    /// <summary>Voice: silent lines named with their .fuz path, present lines a count, not-checkable ones grouped.</summary>
    static void AppendVoice(StringBuilder sb, TopicValidation t, string pad, int cap)
    {
        if (t.VoiceLines.Count == 0 && t.VoiceUndetermined.Count == 0) return;
        var silent = t.VoiceLines.Where(l => !l.FuzPresent).ToList();
        int present = t.VoiceLines.Count - silent.Count;
        sb.Append(pad).Append("  voice: ").Append(present).Append(" present, ").Append(silent.Count).Append(" SILENT");
        if (t.VoiceUndetermined.Count > 0) sb.Append(", ").Append(t.VoiceUndetermined.Count).Append(" not checkable");
        sb.Append('\n');
        foreach (var l in silent)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
            sb.Append(pad).Append("    [!] WILL BE SILENT  ").Append(FormIdToken.Of(l.Info)).Append(" resp ").Append(l.ResponseNumber)
              .Append(" — no .fuz at ").Append(l.FuzPath).Append("  (place the audio here)");
            if (!l.LipPresent) sb.Append("; .lip also absent");
            sb.Append('\n');
        }
        foreach (var grp in t.VoiceUndetermined.GroupBy(u => u.Reason))
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
            int n = grp.Count();
            sb.Append(pad).Append("    [?] ").Append(n).Append(n == 1 ? " line: " : " lines: ").Append(grp.Key).Append('\n');
        }
    }

    /// <summary>Result scripts: WILL NOT FIRE lines named with any missing .pex, bound ones a count.</summary>
    static void AppendScripts(StringBuilder sb, TopicValidation t, string pad, int cap)
    {
        if (t.ScriptFindings.Count == 0) return;
        int ok = t.ScriptFindings.Count(f => f.Status == ScriptBindingStatus.BoundAndCompiled);
        var bad = t.ScriptFindings.Where(f => f.Status is ScriptBindingStatus.ScriptNotCompiled or ScriptBindingStatus.BindingIncomplete).ToList();
        var undet = t.ScriptFindings.Where(f => f.Status == ScriptBindingStatus.Undetermined).ToList();
        sb.Append(pad).Append("  result scripts: ").Append(ok).Append(" bound + compiled, ").Append(bad.Count).Append(" WILL NOT FIRE");
        if (undet.Count > 0) sb.Append(", ").Append(undet.Count).Append(" undetermined");
        sb.Append('\n');
        foreach (var f in bad)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
            sb.Append(pad).Append("    [!] WILL NOT FIRE  ").Append(FormIdToken.Of(f.Info)).Append("  — ").Append(f.Detail);
            if (f.MissingPex.Count > 0) sb.Append("  (missing: ").Append(string.Join(", ", f.MissingPex)).Append(')');
            sb.Append('\n');
        }
        foreach (var f in undet)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
            sb.Append(pad).Append("    [?] ").Append(FormIdToken.Of(f.Info)).Append("  — ").Append(f.Detail).Append('\n');
        }
    }

    /// <summary>The SEQ staleness and coverage block for a Start-Game-Enabled quest; skipped where SeqLint is null.</summary>
    internal static void AppendSeq(StringBuilder sb, SeqLintFinding? s)
    {
        if (s is null || !s.QuestIsSge) return;
        string fid = $"0x{s.OnDiskFormId:X8}";
        bool overrideInPlay = !string.Equals(s.WinnerPlugin, s.DefiningPlugin, StringComparison.OrdinalIgnoreCase);
        bool covered = s.SeqExists && s.SeqContainsQuest == true && s.SeqNewerThanPlugin == true;

        if (!s.SeqExists && s.Note is not null)
            sb.Append("  SEQ: [?] this quest is Start-Game-Enabled but the .seq check could not run — ").Append(s.Note).Append('\n');
        else if (s.SeqExists && (s.SeqContainsQuest is null || s.SeqNewerThanPlugin is null))
            sb.Append("  SEQ: [?] a .seq for ").Append(s.DefiningPlugin).Append(" exists but couldn't be fully checked — ")
              .Append(s.Note ?? "its contents/mtime were undeterminable").Append('\n');
        else if (covered)
            sb.Append("  SEQ: OK — ").Append(s.DefiningPlugin).Append(".seq lists this start-game-enabled quest (").Append(fid)
              .Append(") and is newer than the plugin.\n");
        else if (overrideInPlay)
            // Not covered, but the winner is an override that may itself set SGE — so the definer is not blamed.
            sb.Append("  SEQ: [?] this start-game-enabled quest's .seq coverage couldn't be confirmed — its defining plugin ")
              .Append(s.DefiningPlugin).Append(" has no listing/fresh .seq, but the WINNING override ").Append(s.WinnerPlugin)
              .Append(" is the record the game reads and may itself be what sets Start-Game-Enabled (which would need ITS own .seq). ")
              .Append("Run " + ToolNames.WriteSeq + " against whichever plugin sets the flag.\n");
        else if (!s.SeqExists)
            sb.Append("  SEQ: [!] this quest is Start-Game-Enabled but NO .seq for ").Append(s.DefiningPlugin)
              .Append(" lists it — on a fresh save the quest stays DORMANT and its dialogue never shows. Run " + ToolNames.WriteSeq + " source=")
              .Append(s.DefiningPlugin).Append(".\n");
        else if (s.SeqContainsQuest == false)
            sb.Append("  SEQ: [!] ").Append(s.DefiningPlugin).Append(".seq exists but does NOT list this quest (").Append(fid)
              .Append(") — it stays dormant on a fresh save. Regenerate with " + ToolNames.WriteSeq + ".\n");
        else // s.SeqNewerThanPlugin == false — the .seq does list the quest, it is just older by mtime
            // mtime alone cannot tell why the plugin changed, so this is advisory rather than a "regenerate".
            sb.Append("  SEQ: [?] ").Append(s.DefiningPlugin).Append(".seq lists this quest (").Append(fid)
              .Append(") but is OLDER than ").Append(s.DefiningPlugin)
              .Append(" — if your last change altered which quests are start-game-enabled or the master list (a master added/removed, an ESL compaction), regenerate with " + ToolNames.WriteSeq + "; if it was a dialogue- or condition-only edit, the .seq is still correct (an older mtime alone does not mean stale).\n");
        sb.Append("  SEQ note: a .seq is needed only when WHICH quests are start-game-enabled changes (a new SGE quest, or a quest " +
                  "alias/topic that depends on one) — NOT for a dialogue-only or condition-only edit; those never need a regen.\n");
    }

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;
}

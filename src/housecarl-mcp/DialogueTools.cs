using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda.Plugins;

namespace HousecarlMcp;

/// <summary>
/// houseCARL dialogue tools (Layer B unit C2). The on-demand whole-topic dialogue-graph validator — the counterpart
/// of the per-create voice (unit B) + result-script (unit C1) teeth, but run over EXISTING dialogue resolved against
/// the load-order winners. Read-only: it inspects + reports, never mutates. The Skyrim-typed validation lives in
/// <see cref="HousecarlCore.DialogueValidate"/>; this file is the wire surface + the render.
/// </summary>
[McpServerToolType]
public static class DialogueTools
{
    [McpServerTool(Name = "housecarl_validate_dialogue", ReadOnly = true, Title = "Validate a dialogue topic or quest"),
     Description(
         "Validate a dialogue topic's whole graph against the load order — what the game actually sees. Pass a " +
         "dialogue topic (DIAL) FormID to validate that one topic, or a quest (QUST) FormID to validate EVERY topic " +
         "the quest owns. Checks the things houseCARL CAN verify at the data layer: the topic is wired to a quest, " +
         "the dialogue branch resolves, the INFO.LinkTo conversation chain (topic -> next topic) has no dangling " +
         "targets, and no previous-link (PNAM) is dangling — an EMPTY PNAM is normal (vanilla selects among a " +
         "topic's lines by their conditions, not a previous-link chain), so absence is never flagged; plus (reusing " +
         "the create-time teeth over every existing line) each voiced line has its .fuz on disk and each result " +
         "script is bound + compiled. It LOUDLY declares what it cannot verify — the CTDA conditions that gate WHEN " +
         "a line fires (semantic, only the game evaluates them) and lip-sync/audio content — so 'checks " +
         "passed' never reads as 'this will play'. Resolves against the load-order WINNERS like every other read. " +
         "A FormID is 'XXXXXX:Plugin.esp'. Does NOT modify anything. To create dialogue lines use " +
         "housecarl_create_record; to inspect a single record use housecarl_read_record.")]
    public static string ValidateDialogue(
        LoadOrderService svc,
        [Description("The dialogue topic (DIAL) or quest (QUST) FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. A DIAL validates one topic; a QUST validates every topic that quest owns.")]
            string formid,
        [Description("Optional. Max characters before the report is cut with an explicit notice (never silent). 0 = the server default (~80k). Raise for a quest that owns many topics.")]
            int max_chars = 0) => Guard.Tool("housecarl_validate_dialogue", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        FormKey fk;
        try { fk = FormKey.Factory(formid.Trim()); }
        catch (Exception ex) { return $"error: bad FormID '{formid}': {ex.Message}. Expected 'XXXXXX:Plugin.esp', e.g. '0F1AC1:Skyrim.esm'."; }

        return DialogueWire.Render(svc.ValidateDialogue(fk), max_chars);
    });
}

/// <summary>Renders a <see cref="HousecarlCore.DialogueValidationReport"/> as a compact, honest text report: a
/// topic (or per-topic-of-a-quest) block with its graph issues, voice + result-script verdicts, and ALWAYS the
/// standing-limits footer (grill-rev C2 — the un-checkable CTDA/lip-sync set, so a clean structural pass is never
/// mistaken for "this will play"). Budget-bounded like the read tools (explicit cut at max_chars, never silent).</summary>
static class DialogueWire
{
    public static string Render(DialogueValidationReport r, int maxChars)
    {
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

        // A check that didn't run to completion is NOT a clean pass (Q3) — say so, loudly, never an empty "ok".
        if (r.CheckError is not null)
            return $"validate_dialogue: could NOT complete the check for {r.Input} — {r.CheckError}. " +
                   "Nothing here is a verified pass; the validation did not finish. Try again (the next call rebuilds the index); if it persists, inspect the topic in xEdit.";
        if (r.Error is not null) return "error: " + r.Error;

        var sb = new StringBuilder();
        if (r.InputKind == "quest")
        {
            sb.Append("validate_dialogue: quest ").Append(Edid(r.InputEditorId)).Append(" (").Append(r.Input).Append(')')
              .Append(" — ").Append(r.Topics.Count).Append(r.Topics.Count == 1 ? " topic owned" : " topics owned").Append('\n');
            if (r.Topics.Count == 0)
                sb.Append("  no dialogue topics in the active load order are owned by this quest — nothing to validate. " +
                          "If you expected some, check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n");
        }

        for (int i = 0; i < r.Topics.Count; i++)
        {
            if (sb.Length >= cap)
            {
                sb.Append("... [truncated: rendered ").Append(i).Append(" of ").Append(r.Topics.Count)
                  .Append(" topics at max_chars=").Append(cap).Append("; raise max_chars to see the rest]\n");
                break;
            }
            AppendTopic(sb, r.Topics[i], indent: r.InputKind == "quest", cap);
        }

        // The standing CTDA limit sums conditioned lines across ALL topics (a global honesty note), not just the ones
        // that fit under the render cap.
        AppendStandingLimits(sb, SumConditioned(r), r.ReadIncomplete);
        return sb.ToString().TrimEnd('\n');
    }

    static int SumConditioned(DialogueValidationReport r)
    {
        int n = 0;
        foreach (var t in r.Topics) n += t.ConditionedInfoCount;
        return n;
    }

    static void AppendTopic(StringBuilder sb, TopicValidation t, bool indent, int cap)
    {
        string pad = indent ? "  " : "";
        sb.Append(pad).Append("topic ").Append(Edid(t.TopicEditorId)).Append(" (").Append(t.Topic).Append(')')
          .Append(" — winner ").Append(t.WinnerPlugin).Append('\n');
        sb.Append(pad).Append("  ").Append(t.InfoCount).Append(t.InfoCount == 1 ? " INFO record" : " INFO records");
        if (t.ConditionedInfoCount > 0) sb.Append("; ").Append(t.ConditionedInfoCount).Append(" carry conditions (CTDA)");
        if (t.DeletedInfoCount > 0) sb.Append("; ").Append(t.DeletedInfoCount).Append(" deleted line(s) skipped");
        sb.Append('\n');
        sb.Append(pad).Append("  category=").Append(t.Category).Append("  subtype=").Append(t.Subtype)
          .Append("  subtype_marker=").Append(t.SubtypeName).Append('\n');

        // --- graph issues (PNAM chain, quest + branch wiring) ---
        if (t.Issues.Count == 0)
            sb.Append(pad).Append("  graph: OK — quest + branch wiring resolve, LinkTo targets resolve, no dangling PNAM.\n");
        else
        {
            sb.Append(pad).Append("  graph: ").Append(t.Issues.Count).Append(" issue(s):\n");
            foreach (var iss in t.Issues)
            {
                if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
                sb.Append(pad).Append("    ").Append(iss.Severity == DialogueIssueSeverity.Problem ? "[X] " : "[!] ")
                  .Append(iss.Message).Append('\n');
            }
        }

        AppendVoice(sb, t, pad, cap);
        AppendScripts(sb, t, pad, cap);
    }

    /// <summary>Voice: a SILENT line is the actionable one (named with its .fuz path), present lines are summarised as
    /// a count, and not-checkable lines (no Speaker / unresolvable voice type) are grouped by reason — the same Q3
    /// honesty as the create-time report, but bounded for a whole topic. Skipped entirely when the topic has no voiced
    /// content (a topic of pure link/branch nodes) — nothing to check, not a hidden pass.</summary>
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
            sb.Append(pad).Append("    [!] WILL BE SILENT  ").Append(l.Info).Append(" resp ").Append(l.ResponseNumber)
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

    /// <summary>Result scripts: a WILL NOT FIRE line is the actionable one (named, with any missing .pex), bound +
    /// compiled lines are a count, and undetermined ones are listed. Skipped when no line carries a result script.</summary>
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
            sb.Append(pad).Append("    [!] WILL NOT FIRE  ").Append(f.Info).Append("  — ").Append(f.Detail);
            if (f.MissingPex.Count > 0) sb.Append("  (missing: ").Append(string.Join(", ", f.MissingPex)).Append(')');
            sb.Append('\n');
        }
        foreach (var f in undet)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return; }
            sb.Append(pad).Append("    [?] ").Append(f.Info).Append("  — ").Append(f.Detail).Append('\n');
        }
    }

    /// <summary>The ALWAYS-printed footer (grill-rev C2): the validator is the only non-advisory enforcement, so it
    /// must enumerate what it could NOT verify — the CTDA gate (semantic, game-only) and lip-sync/audio — so a clean
    /// structural pass is never mistaken for "this dialogue will play as intended". The BSA read-incomplete caveat
    /// rides here too when an archive failed to read (an "absent" above may merely be unscanned, Q3).</summary>
    static void AppendStandingLimits(StringBuilder sb, int conditioned, bool readIncomplete)
    {
        sb.Append("standing limits — what houseCARL could NOT verify (so a clean pass above does NOT mean the dialogue will play as intended):\n");
        sb.Append("  • CTDA conditions gate WHEN each line fires; they are semantic and only the game evaluates them, so a wrong/missing condition silently stops a line from ever playing");
        if (conditioned > 0) sb.Append(" — ").Append(conditioned).Append(" line(s) here carry conditions, unverified");
        sb.Append(".\n");
        sb.Append("  • voice presence is an on-disk file check only — lip-sync accuracy and the audio content itself are not verified (voice acting is out of scope).\n");
        sb.Append("  • this validates the WINNING topic's INFO list (what the game plays); a line another plugin adds but this topic override does not re-list is dropped in game and is not seen here — resolve dialogue conflicts so the winning topic carries every line.\n");
        if (readIncomplete)
            sb.Append("  • a BSA failed to read this build, so an \"absent\" voice/.pex above may merely be unscanned — see housecarl_load_order_status.\n");
    }

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;
}

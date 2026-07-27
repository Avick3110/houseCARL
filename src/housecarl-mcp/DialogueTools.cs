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
         "the quest owns (plus the quest's own CK-parity subrecords, checked once). A dialogue view (DLVW) or " +
         "dialogue branch (DLBR) FormID runs a record-level CK-parity check (the DNAM/ENAM and TNAM subrecords the " +
         "Creation Kit always writes — a bare DLVW crashes the CK's Dialogue Views editor). " +
         "Checks the things houseCARL CAN verify at the data layer: the topic is wired to a quest, " +
         "the dialogue branch resolves, the INFO.LinkTo conversation chain (topic -> next topic) has no dangling " +
         "targets, and no previous-link (PNAM) is dangling — an EMPTY PNAM is normal (vanilla selects among a " +
         "topic's lines by their conditions, not a previous-link chain), so absence is never flagged; plus (reusing " +
         "the create-time teeth over every existing line) each voiced line has its .fuz on disk and each result " +
         "script is bound + compiled; non-ASCII characters in the player-facing text (topic name, line prompt, " +
         "response text) are flagged as likely in-game mojibake (the CK/Papyrus surface is Windows-1252/ASCII); " +
         "and each line's CTDA conditions are statically checked for a meaningful subset of MALFORMED shapes (a " +
         "dangling form reference, a dead quest-alias index, an unset Run On reference, GetIsID pointed at a placed " +
         "instance). Reports the EFFECTIVE, MERGED INFO ORDER — the sequence the game walks top to bottom, merged " +
         "across EVERY plugin that touches the topic (xEdit INOM/INOA parity) — and flags any line whose position " +
         "MOVED, naming the plugin that moved it. That is the answer to 'why does the wrong line play': the game " +
         "plays the FIRST line whose conditions pass, re-listing a line appends it to the BOTTOM unless the plugin " +
         "also carries its PNAM, and a pure reorder changes which line answers while leaving every field identical " +
         "— so no field diff can show it. It LOUDLY declares what it still cannot verify — it cannot EVALUATE whether a WELL-FORMED " +
         "condition passes (only the running game can) nor check lip-sync/audio content — so 'checks " +
         "passed' never reads as 'this will play'. Resolves against the load-order WINNERS like every other read. " +
         "A FormID is 'XXXXXX:Plugin.esp'. Does NOT modify anything. To create dialogue lines use " +
         "housecarl_create_record; to inspect a single record use housecarl_read_record.")]
    public static string ValidateDialogue(
        LoadOrderService svc,
        [Description("The dialogue topic (DIAL), quest (QUST), dialogue view (DLVW), or dialogue branch (DLBR) FormID as 'XXXXXX:Plugin.esp' — 6 hex digits, a colon, then the defining master's filename. A DIAL validates one topic; a QUST validates every topic that quest owns; a DLVW/DLBR runs a record-level CK-parity check.")]
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
    /// <summary>The ONE home for rendering a finding list — each as "[X]/[!] message" at <paramref name="pad"/>,
    /// budget-aware: at the cap it appends an EXPLICIT truncation notice and returns false so the caller can stop
    /// (max_chars' "cut with an explicit notice (never silent)" contract, Q3). Shared by the per-topic graph
    /// issues and the input-level (quest/DLVW/DLBR) findings, so the severity glyph and the cap discipline can
    /// never diverge between the two levels of one report.</summary>
    static bool AppendIssues(StringBuilder sb, IReadOnlyList<DialogueIssue> issues, string pad, int cap)
    {
        foreach (var iss in issues)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("... [truncated at max_chars]\n"); return false; }
            sb.Append(pad).Append(iss.Severity == DialogueIssueSeverity.Problem ? "[X] " : "[!] ")
              .Append(iss.Message).Append('\n');
        }
        return true;
    }

    public static string Render(DialogueValidationReport r, int maxChars)
    {
        int cap = maxChars > 0 ? maxChars : Wire.DefaultMaxChars;

        // A check that didn't run to completion is NOT a clean pass (Q3) — say so, loudly, never an empty "ok".
        if (r.CheckError is not null)
            return $"validate_dialogue: could NOT complete the check for {r.Input} — {r.CheckError}. " +
                   "Nothing here is a verified pass; the validation did not finish. Try again (the next call rebuilds the index); if it persists, inspect the topic in xEdit.";
        if (r.Error is not null) return "error: " + r.Error;

        var sb = new StringBuilder();

        // DLVW/DLBR inputs: a RECORD-LEVEL CK-parity check — no topic graph, so no per-topic blocks and no
        // topic-shaped standing-limits footer. The scope note names what this did NOT check (Q3 — a clean pass
        // here must never read as a graph/voice/script validation).
        if (r.InputKind is "view" or "branch")
        {
            string kind = r.InputKind == "view" ? "dialogue view (DLVW)" : "dialogue branch (DLBR)";
            sb.Append("validate_dialogue: ").Append(kind).Append(' ').Append(Edid(r.InputEditorId))
              .Append(" (").Append(r.Input).Append(") — winner ").Append(r.InputWinnerPlugin).Append('\n');
            if (r.InputIssues.Count == 0)
                sb.Append("  CK-parity: OK — ").Append(r.InputKind == "view"
                    ? "the DNAM and ENAM byte subrecords the Creation Kit always writes are both present.\n"
                    : "the TNAM (Category) and DNAM (Flags) subrecords the Creation Kit always writes are both present.\n");
            else
                AppendIssues(sb, r.InputIssues, "  ", cap);
            sb.Append("scope: this is a record-level CK-parity check only — it does not validate any dialogue graph, "
                    + "voice, script, or condition surface. Validate the owning topics (DIAL) or quest (QUST) for those.");
            return sb.ToString();
        }

        if (r.InputKind == "quest")
        {
            sb.Append("validate_dialogue: quest ").Append(Edid(r.InputEditorId)).Append(" (").Append(r.Input).Append(')')
              .Append(" — ").Append(r.Topics.Count).Append(r.Topics.Count == 1 ? " topic owned" : " topics owned").Append('\n');
            if (r.Topics.Count == 0)
                sb.Append("  no dialogue topics in the active load order are owned by this quest — nothing to validate. " +
                          "If you expected some, check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n");
            AppendSeq(sb, r.SeqLint);

            // Quest-level CK-parity (ANAM / objective FNAM) — input-level findings, printed ONCE here, never per
            // topic. An explicit OK line, matching the per-topic "graph: OK" style (a sub-check that ran and passed
            // is stated, not silent).
            if (r.InputIssues.Count == 0)
                sb.Append("  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its Flags (FNAM).\n");
            else
                AppendIssues(sb, r.InputIssues, "  ", cap);
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

        // Fragment-presence note (item 8): tells a dev whether a Papyrus.log entry is even POSSIBLE for a line — a
        // result-script FRAGMENT runs code that CAN surface in the log (on error / an explicit trace); a plain voiced
        // line has no code path, so it never can. Always shown for a topic with live INFOs.
        if (t.InfoCount > 0)
            sb.Append(pad).Append("  result-script fragments: ").Append(t.FragmentInfoCount).Append(" of ").Append(t.InfoCount)
              .Append(t.InfoCount == 1 ? " INFO carries one" : " INFOs carry one")
              .Append(" — a fragment runs code that can surface in Papyrus.log (on error or an explicit trace); a plain voiced line has no code path, so no log entry doesn't mean it didn't play.\n");

        // --- graph issues (PNAM chain, quest + branch wiring) ---
        if (t.Issues.Count == 0)
        {
            // The conditions clause is asserted ONLY when the topic actually has conditioned INFOs — otherwise a
            // condition-free topic would read as "conditions checked + well-formed" when there were none to check.
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

        if (!AppendInfoOrder(sb, t, pad, cap)) return;

        AppendVoice(sb, t, pad, cap);
        AppendScripts(sb, t, pad, cap);
    }

    /// <summary>How many order rows are listed in full before the render falls back to listing only the MOVED lines
    /// — a big topic's whole order is rarely the question, and the moved set always is.</summary>
    const int MaxOrderRows = 25;

    /// <summary>The effective, merged INFO order (#275) — the sequence the game walks top-to-bottom, playing the
    /// FIRST line whose conditions pass. Rendered only where it can differ from a single plugin's own list: with one
    /// contributing plugin this says so in one line rather than printing a list that merges nothing. The MOVED
    /// annotation is the diagnostic payload — a pure reorder changes which line answers while leaving every field
    /// identical, so it is invisible to a field diff. Budget-aware like <see cref="AppendIssues"/>: returns false at
    /// the cap so the caller stops (Q3 — an explicit notice, never a silent cut).</summary>
    static bool AppendInfoOrder(StringBuilder sb, TopicValidation t, string pad, int cap)
    {
        // An EMPTY order is normally nothing to say — unless it is empty because nothing could be READ, which is
        // the total-drop case and the one that must never render as silence (most topics are touched by exactly
        // one plugin, so for them any read failure IS a total failure).
        if (t.InfoOrder is not { } io || (io.Order.Count == 0 && io.Complete)) return true;

        // "Nothing merges here" is a CLAIM, and it holds only if EVERY touching plugin's list was actually read.
        // With one dropped, a genuinely contested topic presents as single-plugin — so the claim is gated on
        // Complete, and the incomplete case says what it is instead of asserting something false (Q3).
        if (!io.Contested && io.Complete)
        {
            sb.Append(pad).Append("  INFO order: ").Append(io.Order.Count)
              .Append(io.Order.Count == 1 ? " line, from a single plugin (" : " lines, from a single plugin (")
              .Append(io.ContributingPlugins[0])
              .Append(") — nothing merges here, so the effective order IS that plugin's own list.\n");
            AppendOrderNote(sb, io, pad);          // a degraded merge is degraded whether or not anything contests it
            return true;
        }

        if (!io.Complete)
        {
            int total = io.ContributingPlugins.Count + io.UnreadContributors.Count;
            sb.Append(pad).Append("  INFO order: INCOMPLETE — read from ").Append(io.ContributingPlugins.Count)
              .Append(" of ").Append(total).Append(" plugin(s) that touch this topic.");
            sb.Append(io.Order.Count == 0
                ? " NOTHING could be read, so no order is shown at all — this is a read failure, NOT an empty topic.\n"
                : " The sequence below is NOT authoritative — lines are missing and positions may be wrong.\n");
            if (io.Order.Count == 0) { AppendOrderNote(sb, io, pad); return true; }
        }

        var moved = io.Moved;
        bool listAll = io.Order.Count <= MaxOrderRows;

        sb.Append(pad).Append("  effective INFO order — merged across ").Append(io.ContributingPlugins.Count)
          .Append(" plugins that touch this topic; the game walks it top to bottom and plays the FIRST line whose conditions pass:\n");
        if (!listAll)
            sb.Append(pad).Append("    (").Append(io.Order.Count).Append(" lines total — listing only the ")
              .Append(moved.Count).Append(" that moved; raise max_chars is not needed, the rest are in original order)\n");

        foreach (var e in listAll ? io.Order : moved)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return false; }
            sb.Append(pad).Append("    #").Append(e.Index + 1).Append("  ").Append(e.Info);
            if (e.Deleted) sb.Append("  (deleted)");
            if (e.Moved) sb.Append("  MOVED from #").Append(e.OriginIndex!.Value + 1);
            else if (e.OriginIndex is null) sb.Append("  (added by a later plugin)");
            sb.Append("  placed by ").Append(e.PlacedBy);
            if (e.Placement == InfoPlacement.Head) sb.Append(" [PNAM names no reachable line — forced to the top]");
            sb.Append('\n');
        }

        if (moved.Count > 0)
        {
            var w = moved[0];
            sb.Append(pad).Append("  [!] ").Append(moved.Count)
              .Append(moved.Count == 1 ? " line sits" : " lines sit")
              .Append(" at a different position than this topic's defining plugin laid down — the biggest shift is ")
              .Append(w.Info).Append(" #").Append(w.OriginIndex!.Value + 1).Append(" -> #").Append(w.Index + 1)
              .Append(", moved there by ").Append(w.PlacedBy)
              .Append(". Re-listing a line appends it to the BOTTOM unless the plugin also carries that line's PNAM. Nothing is dropped — but a line the game now reaches later can be pre-empted by any earlier line whose conditions also pass, so the wrong line answers.\n");
        }

        AppendOrderNote(sb, io, pad);
        return true;
    }

    /// <summary>The per-topic DEGRADATION note (a malformed PNAM, a cycle, a truncated chain, skipped move
    /// analysis) — data problems in the plugins, so they ride the topic they belong to. The STANDING PNAM-zero
    /// fidelity limit is deliberately NOT here: it applies to every topic of 2+ lines whether contested or not, so
    /// it lives in the standing-limits footer where it is stated once and cannot be missed by a topic that happened
    /// to report no moves.</summary>
    static void AppendOrderNote(StringBuilder sb, InfoOrderView io, string pad)
    {
        if (io.Note is { } note)
            sb.Append(pad).Append("  [!] INFO order — ").Append(note).Append(".\n");
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

    /// <summary>The SEQ staleness/coverage block (item 7) for a Start-Game-Enabled quest: a WARN (`[!]`) when its
    /// `.seq` is missing or doesn't list it; an OK line when clean; a `[?]` ADVISORY when the `.seq` lists the quest
    /// but is merely older by mtime (mtime can't tell whether the plugin changed for a SGE/master reason that needs a
    /// regen or a dialogue-only edit that doesn't — so it's a hint, not a "regenerate"), when the result is
    /// undeterminable (the winning `.seq` is in a BSA / unreadable), OR when the WINNING record is an override
    /// (winner != defining) — since that override may itself be the plugin that flags SGE and need its OWN .seq, a
    /// not-covered verdict is surfaced as an ambiguity, never a confident "dormant" against the wrong plugin (Q3).
    /// Plus the inverse guidance that stops needless regen. Skipped entirely for a non-SGE quest (SeqLint null).</summary>
    static void AppendSeq(StringBuilder sb, SeqLintFinding? s)
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
            // not covered, but the WINNING record is an override — its plugin (not the defining master) may be the one
            // that flags SGE and would then need its OWN .seq, so don't confidently blame the defining plugin (Q3).
            sb.Append("  SEQ: [?] this start-game-enabled quest's .seq coverage couldn't be confirmed — its defining plugin ")
              .Append(s.DefiningPlugin).Append(" has no listing/fresh .seq, but the WINNING override ").Append(s.WinnerPlugin)
              .Append(" is the record the game reads and may itself be what sets Start-Game-Enabled (which would need ITS own .seq). ")
              .Append("Run housecarl_write_seq against whichever plugin sets the flag.\n");
        else if (!s.SeqExists)
            sb.Append("  SEQ: [!] this quest is Start-Game-Enabled but NO .seq for ").Append(s.DefiningPlugin)
              .Append(" lists it — on a fresh save the quest stays DORMANT and its dialogue never shows. Run housecarl_write_seq plugin=")
              .Append(s.DefiningPlugin).Append(".\n");
        else if (s.SeqContainsQuest == false)
            sb.Append("  SEQ: [!] ").Append(s.DefiningPlugin).Append(".seq exists but does NOT list this quest (").Append(fid)
              .Append(") — it stays dormant on a fresh save. Regenerate with housecarl_write_seq.\n");
        else // s.SeqNewerThanPlugin == false — the .seq DOES list the quest, it's just older by mtime
            // mtime alone can't tell WHY the plugin changed, so this is ADVISORY, not a confident "regenerate":
            // a genuinely stale .seq (a master added/removed, an ESL compaction) is the real failure mode, but a
            // dialogue/condition-only edit bumps the plugin's mtime too and needs NO regen (the note below). Don't
            // contradict that note with a hard [!] (Q3 — the mtime is a hint, not proof of staleness).
            sb.Append("  SEQ: [?] ").Append(s.DefiningPlugin).Append(".seq lists this quest (").Append(fid)
              .Append(") but is OLDER than ").Append(s.DefiningPlugin)
              .Append(" — if your last change altered which quests are start-game-enabled or the master list (a master added/removed, an ESL compaction), regenerate with housecarl_write_seq; if it was a dialogue- or condition-only edit, the .seq is still correct (an older mtime alone does not mean stale).\n");
        sb.Append("  SEQ note: a .seq is needed only when WHICH quests are start-game-enabled changes (a new SGE quest, or a quest " +
                  "alias/topic that depends on one) — NOT for a dialogue-only or condition-only edit; those never need a regen.\n");
    }

    /// <summary>The ALWAYS-printed footer (grill-rev C2): the validator is the only non-advisory enforcement, so it
    /// must enumerate what it could NOT verify — the CTDA gate (semantic, game-only) and lip-sync/audio — so a clean
    /// structural pass is never mistaken for "this dialogue will play as intended". The BSA read-incomplete caveat
    /// rides here too when an archive failed to read (an "absent" above may merely be unscanned, Q3).</summary>
    static void AppendStandingLimits(StringBuilder sb, int conditioned, bool readIncomplete)
    {
        sb.Append("standing limits — what houseCARL could NOT verify (so a clean pass above does NOT mean the dialogue will play as intended):\n");
        sb.Append("  • CTDA conditions gate WHEN each line fires. houseCARL statically catches a meaningful subset of MALFORMED conditions (a dangling form reference, a dead quest-alias index, an unset Run On reference — surfaced above as graph issues); it still cannot EVALUATE whether a WELL-FORMED condition passes — only the running game can, so a well-formed but wrong condition can still silently stop a line from ever playing");
        if (conditioned > 0) sb.Append(" — ").Append(conditioned).Append(" line(s) here carry conditions, checked for malformedness but not evaluated");
        sb.Append(".\n");
        sb.Append("  • voice presence is an on-disk file check only — lip-sync accuracy and the audio content itself are not verified (voice acting is out of scope).\n");
        sb.Append("  • the per-line checks above (voice, result script, CK parity) audit the WINNING topic's INFO list only. A line another plugin contributes but this winner does not re-list is NOT dropped from the game — it still plays, and it DOES appear in the effective INFO order above — but its voice/script/parity is not audited here.\n");
        sb.Append("  • line ORDER has one blind spot: a line whose previous-line link (PNAM) was written as an explicit \"I am first\" marker reads here identically to a line carrying no link at all, so it is placed LAST where the game places it FIRST. Nothing in the data marks which line that is, so it can affect any topic of 2+ lines — if a line's position above looks wrong, confirm it against xEdit's INFO Order (INOA) row for the topic.\n");
        if (readIncomplete)
            sb.Append("  • a BSA failed to read this build, so an \"absent\" voice/.pex above may merely be unscanned — see housecarl_load_order_status.\n");
    }

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;
}

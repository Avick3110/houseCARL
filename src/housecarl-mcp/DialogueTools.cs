using System.Text;

namespace HousecarlMcp;

/// <summary>
/// The dialogue report render. The tool that used to sit above it, housecarl_validate_dialogue, was deleted at the
/// 1.x cut: its findings are housecarl_check findings=dialogue, and its merged INFO-order render is
/// housecarl_records project=info_order. The Skyrim-typed validation lives in
/// <see cref="HousecarlCore.DialogueValidate"/>; this file is the render alone.
/// </summary>
/// <summary>The composers a dialogue report is rendered FROM: a topic block (graph issues, voice and
/// result-script verdicts), a findings list, an effective INFO order, a .seq note. Budget-bounded like the read
/// tools (explicit cut at max_chars, never silent). The whole-report composer that assembled them, and the
/// standing-limits footer it always printed, went with housecarl_validate_dialogue (#486); the merged surface
/// composes these through <see cref="DialogueSweepRender"/> and states its own standing limits.</summary>
internal static class DialogueWire
{
    /// <summary>The ONE home for rendering a finding list — each as "[X]/[!] message" at <paramref name="pad"/>,
    /// budget-aware: at the cap it appends an EXPLICIT truncation notice and returns false so the caller can stop
    /// (max_chars' "cut with an explicit notice (never silent)" contract, Q3). Shared by the per-topic graph
    /// issues and the input-level (quest/DLVW/DLBR) findings, so the severity glyph and the cap discipline can
    /// never diverge between the two levels of one report.</summary>
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

    /// <param name="includeInfoOrder">render the effective merged INFO order (class 8) inside this block.
    /// <c>validate_dialogue</c> does; the merged <c>check</c> surface's dialogue family does NOT, because an ordered
    /// sequence over the touching-plugin stack is not a findings list and SPEC §6.1 routes it to
    /// <c>records project=info_order</c>. ONE composer with the class gated here rather than two spellings of a
    /// topic block: the same facts rendered twice is how the two surfaces would drift apart.</param>
    internal static void AppendTopic(StringBuilder sb, TopicValidation t, bool indent, int cap,
                                     bool includeInfoOrder = true)
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

        if (includeInfoOrder && !AppendInfoOrder(sb, t, pad, cap, indent)) return;

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
    static bool AppendInfoOrder(StringBuilder sb, TopicValidation t, string pad, int cap, bool indent)
        => AppendInfoOrderView(sb, t.InfoOrder, pad, cap, indent);

    /// <summary>The view-level body, shared with the records info_order PROJECT form (the §6.1 F1 split carried
    /// this render's MOVED annotations and honesty gates across intact — one render, no drift).</summary>
    internal static bool AppendInfoOrderView(StringBuilder sb, InfoOrderView? view, string pad, int cap, bool indent)
    {
        // An EMPTY order is normally nothing to say — unless it is empty because nothing could be READ, which is
        // the total-drop case and the one that must never render as silence (most topics are touched by exactly
        // one plugin, so for them any read failure IS a total failure).
        if (view is not { } io || (io.Order.Count == 0 && io.Complete)) return true;

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
        // The row cap exists so a quest owning many topics doesn't bury its findings under hundreds of lines. A
        // SINGLE-topic report has nothing to bury, so it always lists in full — abbreviating there withheld the
        // whole answer and offered no way to get it back, since the cap counts ROWS and max_chars counts
        // characters. Measured live: a 37-line topic printed a header and nothing under it.
        bool listAll = !indent || io.Order.Count <= MaxOrderRows;

        // Count the plugins that TOUCH the topic, not the ones successfully READ. On the incomplete path this
        // line sits directly beneath a banner giving the true total, so printing the read count put two different
        // numbers for the same quantity on adjacent lines, the second one wrong (and "1 plugins").
        int touching = io.ContributingPlugins.Count + io.UnreadContributors.Count;
        sb.Append(pad).Append("  effective INFO order — merged across ").Append(touching)
          .Append(touching == 1 ? " plugin that touches" : " plugins that touch")
          .Append(" this topic; the game walks it top to bottom and plays the FIRST line whose conditions pass:\n");

        // Over the cap AND nothing moved: say so. Falling through printed "listing only the 0 that moved"
        // followed by an EMPTY list — a header promising an order and delivering none.
        //
        // But an empty moved set is evidence of nothing unless the analysis RAN over COMPLETE input. Two separate
        // ways it can fail, and the claim needs both:
        //   MovesComputed — the analysis ran at all (not skipped by the line ceiling or a suspect baseline);
        //   Complete      — it ran over every touching plugin's list. An unread plugin sitting AFTER the definer
        //                   leaves the baseline trusted, so MovesComputed stays true while lines are missing —
        //                   and the unread plugin is precisely the one that could have moved something.
        // Gating on the first alone put "none of which changed position" between two lines saying positions may
        // be wrong.
        bool movesKnown = io.MovesComputed && io.Complete;
        if (!listAll && moved.Count == 0)
        {
            sb.Append(pad).Append("    ").Append(io.Order.Count).Append(movesKnown
                ? " lines, none of which changed position — the merged order matches the defining plugin's own list."
                : " lines. Which lines moved is NOT known here (see the note below), so this is not a statement that none did.")
              .Append(" Validate this topic's DIAL on its own to see every line.\n");
            AppendOrderNote(sb, io, pad);
            return true;
        }

        // Same gate as the branch above, for the same reason. "the rest keep their original relative order" is a
        // claim about the rows this branch deliberately WITHHOLDS, so the reader cannot check it — and it held
        // only if the analysis ran over complete input. Worse than the sibling case, not better.
        if (!listAll)
            sb.Append(pad).Append("    (").Append(io.Order.Count).Append(" lines; listing only the ")
              .Append(moved.Count).Append(movesKnown
                  ? " that MOVED — the rest keep their original relative order."
                  : " found to have MOVED — whether the rest held position is NOT known here (see the note below).")
              .Append(" Validate this topic's DIAL on its own to see every line.)\n");

        foreach (var e in listAll ? io.Order : moved)
        {
            if (sb.Length >= cap) { sb.Append(pad).Append("    ... [truncated at max_chars]\n"); return false; }
            sb.Append(pad).Append("    #").Append(e.Index + 1).Append("  ").Append(e.Info);
            if (e.Deleted) sb.Append("  (deleted)");
            if (e.Moved) sb.Append("  MOVED from #").Append(e.OriginIndex!.Value + 1);
            // Gated on BaselineTrusted: with a shifted baseline the DEFINER's own lines have no OriginIndex,
            // and this would call them late additions as flat fact under a banner saying the baseline is suspect.
            else if (e.OriginIndex is null && io.BaselineTrusted) sb.Append("  (added by a later plugin)");
            sb.Append("  placed by ").Append(e.PlacedBy);
            // The zero "I am first" marker and a broken link both land at the head, but only one is a fault —
            // and the marker is the COMMON shape, so identical wording meant most readers met a correct vanilla
            // line described in the vocabulary of a malformed one, and would go and "fix" it.
            if (e.Placement == InfoPlacement.HeadFirstMarker)
                sb.Append("  [pinned first by its own PNAM marker — deliberate, not a fault]");
            else if (e.Placement == InfoPlacement.HeadUnresolvable)
                sb.Append("  [PNAM names no reachable line — forced to the top; worth a look]");
            sb.Append('\n');
        }

        if (moved.Count > 0)
        {
            var w = moved[0];
            // QUALIFIED, not gated, when the read is incomplete — deliberately unlike the two negative claims
            // above. Those assert an absence the banner contradicts, and a false negative ends the
            // investigation; this is a positive lead the banner already qualifies, it carries [!], and an
            // unread plugin could equally re-list the line WITH its PNAM and put it back. Suppressing it would
            // withhold the lead exactly where a modder most wants one, so it says how far the evidence reaches
            // instead. The per-row "MOVED from #N" annotations inherit this same sentence.
            sb.Append(pad).Append("  [!] ").Append(io.Complete ? "" : "as far as could be read, ").Append(moved.Count)
              .Append(moved.Count == 1 ? " line sits" : " lines sit")
              .Append(" at a different position than this topic's defining plugin laid down — the biggest shift is ")
              .Append(w.Info).Append(" #").Append(w.OriginIndex!.Value + 1).Append(" -> #").Append(w.Index + 1)
              .Append(", moved there by ").Append(w.PlacedBy)
              .Append(". Re-listing a line appends it to the BOTTOM unless the plugin also carries that line's PNAM. Nothing is dropped — but a line the game now reaches later can be pre-empted by any earlier line whose conditions also pass, so the wrong line answers.\n");
        }

        AppendOrderNote(sb, io, pad);
        return true;
    }

    /// <summary>The per-topic DEGRADATION note (a malformed PNAM, a cycle, a truncated chain, an unread
    /// contributor, skipped move analysis) — these are data problems in the plugins, so they ride the topic they
    /// belong to rather than a report-wide footer.
    ///
    /// There is deliberately NO standing PNAM-zero caveat, here or in the footer: the reader distinguishes a
    /// present-but-zero PNAM from an absent one (see <c>DialogueInfoOrder.PnamZeroIsDistinguishable</c>), so the
    /// caveat that shipped for four review rounds described a limitation that does not exist. It was retracted,
    /// and <c>RENDER-NO-FALSE-CAVEAT</c> pins its absence — do not re-add it.</summary>
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
            // not covered, but the WINNING record is an override — its plugin (not the defining master) may be the one
            // that flags SGE and would then need its OWN .seq, so don't confidently blame the defining plugin (Q3).
            sb.Append("  SEQ: [?] this start-game-enabled quest's .seq coverage couldn't be confirmed — its defining plugin ")
              .Append(s.DefiningPlugin).Append(" has no listing/fresh .seq, but the WINNING override ").Append(s.WinnerPlugin)
              .Append(" is the record the game reads and may itself be what sets Start-Game-Enabled (which would need ITS own .seq). ")
              .Append("Run " + ToolNames.WriteSeq + " against whichever plugin sets the flag.\n");
        else if (!s.SeqExists)
            sb.Append("  SEQ: [!] this quest is Start-Game-Enabled but NO .seq for ").Append(s.DefiningPlugin)
              .Append(" lists it — on a fresh save the quest stays DORMANT and its dialogue never shows. Run " + ToolNames.WriteSeq + " plugin=")
              .Append(s.DefiningPlugin).Append(".\n");
        else if (s.SeqContainsQuest == false)
            sb.Append("  SEQ: [!] ").Append(s.DefiningPlugin).Append(".seq exists but does NOT list this quest (").Append(fid)
              .Append(") — it stays dormant on a fresh save. Regenerate with " + ToolNames.WriteSeq + ".\n");
        else // s.SeqNewerThanPlugin == false — the .seq DOES list the quest, it's just older by mtime
            // mtime alone can't tell WHY the plugin changed, so this is ADVISORY, not a confident "regenerate":
            // a genuinely stale .seq (a master added/removed, an ESL compaction) is the real failure mode, but a
            // dialogue/condition-only edit bumps the plugin's mtime too and needs NO regen (the note below). Don't
            // contradict that note with a hard [!] (Q3 — the mtime is a hint, not proof of staleness).
            sb.Append("  SEQ: [?] ").Append(s.DefiningPlugin).Append(".seq lists this quest (").Append(fid)
              .Append(") but is OLDER than ").Append(s.DefiningPlugin)
              .Append(" — if your last change altered which quests are start-game-enabled or the master list (a master added/removed, an ESL compaction), regenerate with " + ToolNames.WriteSeq + "; if it was a dialogue- or condition-only edit, the .seq is still correct (an older mtime alone does not mean stale).\n");
        sb.Append("  SEQ note: a .seq is needed only when WHICH quests are start-game-enabled changes (a new SGE quest, or a quest " +
                  "alias/topic that depends on one) — NOT for a dialogue-only or condition-only edit; those never need a regen.\n");
    }

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;
}

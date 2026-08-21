using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// THE BINDING MEASUREMENT for 4b phase 2 (<c>dialogue-width-measure</c>): are the DIALOGUE family's rows
/// WIDTH-COMPUTABLE BEFORE THEY ARE WRITTEN, in both transports?
///
/// <para>The #394 ruling (2026-08-21, item 4) made the allocation shape conditional on this: the per-subject
/// ceiling is an added term on the emission test and every quantity it reads is MEASURED, never a mean or an
/// estimate. A family whose rows cannot be sized until after they land cannot be allocated that way, and the
/// ruling's standing instruction is to RE-ESCALATE rather than approximate. So this runs before the fold.</para>
///
/// <para><b>The measurement, and why it is not circular.</b> Composing a row into a string and calling its
/// <c>Length</c> the row's width proves nothing on its own — the two are the same act. What has to hold is that a
/// row composed INDEPENDENTLY, with no access to what the response has already written, is the SAME row the
/// one-pass render writes. So the probe composes every unit of a real report into its own builder, concatenates
/// them, and compares the result against what the render produces in one pass. Equality means each unit's width
/// was knowable before the response existed; a difference means some unit reads the response's own state and the
/// allocation would be sizing something other than what lands.</para>
///
/// <para><b>Both transports.</b> Text composes to a string and measures chars. The dialogue family has no json
/// render yet, so the json half measures the technique the other two families already use in production
/// (<c>JsonWire.ScriptRecordCost</c> — serialize the row into a throwaway writer, take the byte length) applied to
/// a dialogue row: the pre-write cost against what the row actually appends to a live writer.</para>
///
/// <para>Read-only. Needs <c>--mo2 &lt;instance&gt;</c>: the report shapes that matter (a quest owning many topics,
/// silent voice lines, unbound result scripts, degraded merges) are live-order facts, and #342s1's lesson is that a
/// toy fixture stood 15-20x off the order it stood for. It is BOUNDED by construction — seeds are validated one at
/// a time and the seed count is a parameter, never a whole-order dialogue sweep (SPEC §6.1 F1.2 refuses that).</para>
///
/// Run: dotnet run --project src/housecarl-generator -- dialogue-width-measure --mo2 "E:\Skyrim Modding\ARR 2.0"
/// </summary>
public static class DialogueWidthProbe
{
    public static int Run(string[] args)
    {
        string? mo2 = ArgVal(args, "--mo2");
        if (mo2 is null) { Console.WriteLine("dialogue-width-measure needs --mo2 <MO2 instance folder>"); return 2; }
        int seedCount = int.TryParse(ArgVal(args, "--seeds"), out var n) ? n : 6;
        int scan = int.TryParse(ArgVal(args, "--scan"), out var sc) ? sc : 300;

        var store = new UserConfigStore(Path.Combine(Path.GetTempPath(), "hc-dlg-width-" + Guid.NewGuid().ToString("N") + ".json"));
        using var svc = LoadOrderService.WithInstance(mo2, 0, store);

        Console.WriteLine($"# dialogue-width-measure — live order at {mo2}");
        Console.WriteLine("# the binding check: are the dialogue family's rows width-computable BEFORE they are written?");
        Console.WriteLine();

        List<(FormKey Seed, string Kind)> seeds;
        if (ArgVal(args, "--seed") is { } named)
        {
            seeds = new();
            foreach (var raw in named.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                try { seeds.Add((FormKey.Factory(raw.Trim()), "named on the command line")); }
                catch (Exception ex) { Console.WriteLine($"FAIL  --seed '{raw.Trim()}' is not a FormID: {ex.Message}. Expected 'XXXXXX:Plugin.esp'."); return 2; }
            }
        }
        else seeds = DiscoverSeeds(svc, seedCount, scan);
        if (seeds.Count == 0) { Console.WriteLine("FAIL  no dialogue seeds discovered — cannot measure."); return 1; }

        bool allOk = true;
        int measured = 0;
        foreach (var (fk, kind) in seeds)
        {
            var sw = Stopwatch.StartNew();
            var r = svc.ValidateDialogue(fk);
            sw.Stop();
            if (r.Error is not null || r.CheckError is not null)
            {
                Console.WriteLine($"  SKIP  {fk} ({kind}) — {r.Error ?? r.CheckError}");
                continue;
            }
            Console.WriteLine($"— seed {fk} ({r.InputKind}, {kind}) — {r.Topics.Count} topic(s), validated in {sw.ElapsedMilliseconds} ms");
            allOk &= MeasureText(r);
            allOk &= MeasureJson(r);
            measured++;
            Console.WriteLine();
        }

        if (measured == 0) { Console.WriteLine("FAIL  every seed errored — nothing was measured."); return 1; }
        Console.WriteLine(allOk
            ? "RESULT  WIDTH-COMPUTABLE in both transports — the allocation shape holds for the dialogue family."
            : "RESULT  NOT width-computable — RE-ESCALATE the allocation shape (#394 ruling item 4). Do not approximate.");
        return allOk ? 0 : 1;
    }

    /// <summary>TEXT: every unit composed into its OWN builder, concatenated, and compared against the one-pass
    /// render. The comparison is the measurement — an independently composed unit that differs from the one the
    /// render writes is a unit whose width was not knowable in advance.</summary>
    static bool MeasureText(DialogueValidationReport r)
    {
        var units = new List<(string Name, string Text)>();

        if (r.InputKind == "quest")
        {
            var head = new StringBuilder();
            head.Append("validate_dialogue: quest ").Append(Edid(r.InputEditorId)).Append(" (").Append(r.Input).Append(')')
                .Append(" — ").Append(r.Topics.Count).Append(r.Topics.Count == 1 ? " topic owned" : " topics owned").Append('\n');
            if (r.Topics.Count == 0)
                head.Append("  no dialogue topics in the active load order are owned by this quest — nothing to validate. " +
                            "If you expected some, check those topics set DialogTopic.Quest to this quest and that their plugin is enabled.\n");
            DialogueWire.AppendSeq(head, r.SeqLint);
            if (r.InputIssues.Count == 0)
                head.Append("  quest CK-parity: OK — the NextAliasID (ANAM) subrecord is present and every objective carries its Flags (FNAM).\n");
            else
                DialogueWire.AppendIssues(head, r.InputIssues, "  ", int.MaxValue);
            units.Add(("seed head", head.ToString()));
        }

        for (int i = 0; i < r.Topics.Count; i++)
        {
            var one = new StringBuilder();
            DialogueWire.AppendTopic(one, r.Topics[i], indent: r.InputKind == "quest", int.MaxValue);
            units.Add(($"topic #{i + 1}", one.ToString()));
        }

        var foot = new StringBuilder();
        DialogueWire.AppendStandingLimits(foot, DialogueWire.SumConditioned(r), r.ReadIncomplete);
        units.Add(("standing limits", foot.ToString()));

        var assembled = new StringBuilder();
        foreach (var u in units) assembled.Append(u.Text);
        string composed = assembled.ToString().TrimEnd('\n');
        string onePass = DialogueWire.Render(r, int.MaxValue);

        bool ok = composed == onePass;
        int widest = 0;
        foreach (var u in units) widest = Math.Max(widest, u.Text.Length);
        Console.WriteLine($"  text  {units.Count} unit(s), widest {widest} chars, response {onePass.Length} chars — "
                        + (ok ? "PASS composed == one-pass" : "FAIL composed != one-pass"));
        if (!ok)
        {
            int at = FirstDifference(composed, onePass);
            Console.WriteLine($"        first difference at char {at}:");
            Console.WriteLine($"        composed: {Excerpt(composed, at)}");
            Console.WriteLine($"        one-pass: {Excerpt(onePass, at)}");
        }
        return ok;
    }

    /// <summary>JSON: the production cost technique (serialize the row into a throwaway writer, take the byte
    /// length) applied to a dialogue row, against what the row actually appends to a live writer. A cost that is a
    /// measured upper bound on the write is what <c>BoundedBody</c> asks for — it charges the ceiling with what the
    /// row ACTUALLY wrote, so an exact serialization of the row plus its envelope is a measurement, never an
    /// estimate.</summary>
    static bool MeasureJson(DialogueValidationReport r)
    {
        if (r.Topics.Count == 0) { Console.WriteLine("  json  no topic rows on this seed — nothing to measure."); return true; }

        bool ok = true;
        int rows = 0, overhead = 0;
        using var live = new MemoryStream();
        using (var w = new Utf8JsonWriter(live, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteStartArray("topics");
            foreach (var t in r.Topics)
            {
                int cost = TopicRowCost(t);              // measured BEFORE the write
                w.Flush();
                long before = live.Length;
                WriteTopicRow(w, t);
                w.Flush();
                int wrote = (int)(live.Length - before); // what it actually appended
                rows++;
                if (cost < wrote) { ok = false; Console.WriteLine($"  json  FAIL row cost {cost} < wrote {wrote}"); }
                overhead = Math.Max(overhead, cost - wrote);
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Console.WriteLine($"  json  {rows} row(s) — {(ok ? "PASS" : "FAIL")} pre-write cost bounds every write; "
                        + $"widest envelope overhead {overhead} bytes");
        return ok;
    }

    /// <summary>One dialogue topic row's json width, measured the way the scripts family measures its record rows:
    /// serialize it into a throwaway writer inside the same frame the live one writes it in, and take the byte
    /// length. The frame is included, so the number is an upper bound on the write rather than a floor.</summary>
    static int TopicRowCost(TopicValidation t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteStartArray("topics");
            WriteTopicRow(w, t);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return (int)ms.Length;
    }

    /// <summary>The candidate json shape for one topic row — classes 1-7 only. Class 8 (the effective merged INFO
    /// order) is deliberately absent: it is <c>records project=info_order</c>'s surface, not this one (SPEC §6.1).</summary>
    static void WriteTopicRow(Utf8JsonWriter w, TopicValidation t)
    {
        w.WriteStartObject();
        w.WriteString("topic", t.Topic.ToString());
        w.WriteString("editor_id", t.TopicEditorId);
        w.WriteString("winner_plugin", t.WinnerPlugin);
        w.WriteNumber("info_count", t.InfoCount);
        w.WriteNumber("conditioned_info_count", t.ConditionedInfoCount);
        w.WriteNumber("deleted_info_count", t.DeletedInfoCount);
        w.WriteNumber("fragment_info_count", t.FragmentInfoCount);
        w.WriteString("category", t.Category);
        w.WriteString("subtype", t.Subtype);
        w.WriteString("subtype_marker", t.SubtypeName);
        w.WriteStartArray("issues");
        foreach (var i in t.Issues)
        {
            w.WriteStartObject();
            w.WriteString("severity", i.Severity == DialogueIssueSeverity.Problem ? "problem" : "warning");
            w.WriteString("message", i.Message);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("silent_lines");
        foreach (var l in t.VoiceLines)
        {
            if (l.FuzPresent) continue;
            w.WriteStartObject();
            w.WriteString("info", l.Info.ToString());
            w.WriteNumber("response", l.ResponseNumber);
            w.WriteString("fuz_path", l.FuzPath);
            w.WriteBoolean("lip_present", l.LipPresent);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("script_findings");
        foreach (var f in t.ScriptFindings)
        {
            w.WriteStartObject();
            w.WriteString("info", f.Info.ToString());
            w.WriteString("status", f.Status.ToString());
            w.WriteString("detail", f.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>A FormID as every read render spells it: six hex digits, a colon, the defining plugin's filename.</summary>
    const string FormIdPattern = @"([0-9A-Fa-f]{6}):([^\s,\]]+\.es[pml])";

    /// <summary>Find seeds through the PRODUCT surface, and BOUNDED: one <c>cross_plugin_query</c> over DIAL
    /// winners carrying each topic's <c>Quest</c> link, capped by <paramref name="scan"/> topics. The quests are
    /// tallied by how many of those topics name them and the busiest are taken, because a quest owning ONE topic
    /// exercises none of what the allocation has to size — the multi-row case is the measurement's whole subject.
    /// A single topic is seeded alongside them so the DIAL input kind is measured too.
    ///
    /// <para><b>The scan default is small on purpose.</b> Carrying a field costs a body fetch per topic, and at
    /// <c>--scan 3000</c> on the live ARR order this pass had not returned after ten minutes — the discovery would
    /// then cost more than the thing it sets up. Pass <c>--seed</c> with FormIDs directly to skip it.</para>
    ///
    /// <para>It deliberately does not sweep. SPEC §6.1 F1.2 refuses a whole-order dialogue sweep on cost, and a
    /// measurement that performed one to set itself up would be refuting its own premise.</para></summary>
    static List<(FormKey Seed, string Kind)> DiscoverSeeds(LoadOrderService svc, int want, int scan)
    {
        string text;
        try { text = ReadTools.CrossPluginQuery(svc, type: "DIAL", fields: new[] { "Quest" }, limit: scan, max_chars: 4_000_000); }
        catch (Exception ex) { Console.WriteLine($"  note  seed discovery failed: {ex.Message}"); return new(); }

        var owned = new Dictionary<string, int>();
        foreach (Match m in Regex.Matches(text, @"Quest = " + FormIdPattern))
        {
            string id = m.Groups[1].Value + ":" + m.Groups[2].Value;
            owned[id] = owned.TryGetValue(id, out var c) ? c + 1 : 1;
        }
        Console.WriteLine($"# seeds drawn from {Regex.Matches(text, @"formid=" + FormIdPattern).Count} DIAL winners "
                        + $"(limit={scan}) naming {owned.Count} distinct quest(s)");

        var seeds = new List<(FormKey, string)>();
        foreach (var q in owned.OrderByDescending(kv => kv.Value).Take(Math.Max(1, want - 1)))
        {
            try { seeds.Add((FormKey.Factory(q.Key), $"quest named by {q.Value} of the scanned topics")); }
            catch { /* a link this render spelled but FormKey will not parse is not this probe's subject */ }
        }
        if (Regex.Match(text, @"formid=" + FormIdPattern) is { Success: true } first)
        {
            try { seeds.Add((FormKey.Factory(first.Groups[1].Value + ":" + first.Groups[2].Value), "single topic")); }
            catch { /* as above */ }
        }
        return seeds;
    }

    static int FirstDifference(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }

    static string Excerpt(string s, int at)
    {
        int start = Math.Max(0, at - 40), len = Math.Min(120, s.Length - start);
        return len <= 0 ? "<end of string>" : s.Substring(start, len).Replace("\n", "\\n");
    }

    static string Edid(string? e) => string.IsNullOrEmpty(e) ? "<none>" : e;

    static string? ArgVal(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }
}

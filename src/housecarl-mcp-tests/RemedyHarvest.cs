using System.Text.Json;
using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>
/// Every remedy sentence <c>housecarl_records</c> emits, harvested once per collection across the site
/// families (fields / container / tree / scan / delta / poles / scoped / spilled artifact) and the three
/// transports plus the artifact rows.
///
/// <para>The harvest names NO lever and filters on NO key: an over-harvest costs a triage row, an
/// under-harvest hides a wrong lever. The only shape filter is on the LITERAL — a multi-word sentence vs a
/// bare key — never on which lever a sentence names.</para>
/// </summary>
public sealed class RemedyHarvest
{
    public static readonly Regex RemedyLine = new(
        @"max_chars|narrow|expand|raise |lower |drop |pass |page with|request fewer|continue with",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Every lever housecarl_records spells differently, or does not have at all.</summary>
    public static readonly (string Pattern, string Claim)[] WrongLevers =
    {
        (@"(?<!project\.)\bfields=", "narrow with 'fields=' (it has project.fields=)"),
        (@"(?<!project\.)\bdepth=",  "lower or pass 'depth=' (it has project.depth=)"),
        (@"\bconflict_tree\b",       "drop 'conflict_tree' (it has no such parameter — the tree is a project FORM)"),
        (@"\bwinner_fields\b",       "pass 'winner_fields=true' (it has fields_source=\"winner\")"),
        (@"\bproject\.fields(?!=)",  "narrow with 'project.fields' without the '=' (the pre-fix spelling)"),
        (@"drop project\.fields",    "drop 'project.fields' (the 'fields' form refuses without its paths)"),
    };

    public static readonly string[] Lanes = { "json", "text", "dense", "artifact" };

    public IReadOnlyList<(string Lane, string Label, string Text)> Sentences { get; }
    public IReadOnlyList<string> ProbeLabels { get; }

    public RemedyHarvest(RecordsWorld w)
    {
        var svc = w.Svc;
        JsonElement Je(string j) => JsonDocument.Parse(j).RootElement.Clone();
        JsonElement Plugin(string n) => Je("\"" + n + "\"");
        string Fid(Mutagen.Bethesda.Plugins.FormKey k) => RecordsWorld.Fid(k);

        var containerFields = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects" } };
        var scanFields = new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats.Damage" } };
        var ovlPost = Je("{\"overlay\": \"skypatcher\", \"state\": \"post\"}");
        var poleScope = Plugin(w.MasterName);
        string[] wf = w.Weapons.Select(Fid).ToArray();

        var tinyProject = new RecordsTools.RecordsProject
        { form = "fields", fields = new[] { "BasicStats.Damage", "EditorID", "Name" } };
        var tinyJson = RecordsTools.Records(svc, formids: wf, format: "json", max_chars: 220, project: tinyProject);
        var tinyText = RecordsTools.Records(svc, formids: wf, max_chars: 220, project: tinyProject);

        ArtifactPath = Path.Combine(w.Root, "remedy-rows.jsonl");
        var artResp = RecordsTools.Records(svc, types: new[] { "SPEL" }, to_file: ArtifactPath, project: containerFields);

        var probes = new (string Label, string Lane, string Resp)[]
        {
            ("fields/json", "json", tinyJson),
            ("fields/text", "text", tinyText),
            ("container/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, project: containerFields)),
            ("container/json", "json", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, format: "json", project: containerFields)),
            ("tree/text", "text", RecordsTools.Records(svc, formids: wf, max_chars: 300, project: new RecordsTools.RecordsProject { form = "tree" })),
            ("tree/json", "json", RecordsTools.Records(svc, formids: wf, max_chars: 300, format: "json", project: new RecordsTools.RecordsProject { form = "tree" })),
            ("scan/text", "text", RecordsTools.Records(svc, types: new[] { "WEAP" }, max_chars: 300, project: scanFields)),
            ("scan/json", "json", RecordsTools.Records(svc, types: new[] { "WEAP" }, max_chars: 300, format: "json", project: scanFields)),
            ("container/dense", "dense", RecordsTools.Records(svc, types: new[] { "SPEL" }, format: "dense", project: containerFields)),
            ("delta/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.Weapons[0]) }, source: Plugin(w.OverrideName),
                                                        versus: Plugin("previous_provider"), max_chars: 320,
                                                        project: new RecordsTools.RecordsProject { form = "delta" })),
            ("deltaIdentical/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.BigList) }, source: Plugin(w.OverrideName),
                                                                 versus: Plugin("previous_provider"),
                                                                 project: new RecordsTools.RecordsProject { form = "delta" })),
            ("pole/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, source: poleScope, project: containerFields)),
            ("pole/json", "json", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, source: poleScope, format: "json", project: containerFields)),
            ("overlay/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, source: ovlPost, project: containerFields)),
            ("overlay/json", "json", RecordsTools.Records(svc, formids: new[] { Fid(w.SpellA) }, source: ovlPost, format: "json", project: containerFields)),
            ("poleOff/text", "text", RecordsTools.Records(svc, formids: new[] { Fid(w.Weapons[1]) }, source: Plugin(w.OldName),
                                                          project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats" } })),
            ("poleOff/json", "json", RecordsTools.Records(svc, formids: new[] { Fid(w.Weapons[1]) }, source: Plugin(w.OldName), format: "json",
                                                          project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "BasicStats" } })),
            ("scoped/text", "text", RecordsTools.Records(svc, plugins: new RecordsTools.RecordsScope { names = new[] { w.MasterName } },
                                                         types: new[] { "WEAP" }, project: scanFields)),
            ("scoped/json", "json", RecordsTools.Records(svc, plugins: new RecordsTools.RecordsScope { names = new[] { w.MasterName } },
                                                         types: new[] { "WEAP" }, format: "json", project: scanFields)),
            ("scoped/dense", "dense", RecordsTools.Records(svc, plugins: new RecordsTools.RecordsScope { names = new[] { w.MasterName } },
                                                           types: new[] { "WEAP" }, format: "dense", project: scanFields)),
            ("spill/artifact", "artifact", artResp),
        };

        ProbeLabels = probes.Select(p => p.Label).ToList();

        var sentences = new List<(string, string, string)>();
        foreach (var (label, lane, resp) in probes)
        {
            var hits = lane switch
            {
                "artifact" => HarvestArtifact(ArtifactPath),
                "text" => resp.Split('\n').Where(l => RemedyLine.IsMatch(l)).ToList(),
                _ => HarvestAllStrings(resp),
            };
            foreach (var h in hits.Select(x => x.Trim()).Distinct())
                sentences.Add((lane, label, h));
        }
        Sentences = sentences;
    }

    public string ArtifactPath { get; }

    public static List<string> HarvestAllStrings(string json)
    {
        var found = new List<string>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) found.Add(e.GetString()!);
            else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value);
            else if (e.ValueKind == JsonValueKind.Array) foreach (var it in e.EnumerateArray()) Walk(it);
        }
        try { Walk(JsonDocument.Parse(json).RootElement); } catch { /* not a document — nothing to harvest */ }
        return found;
    }

    public static List<string> HarvestArtifact(string path)
    {
        var found = new List<string>();
        if (!File.Exists(path)) return found;
        foreach (var line in File.ReadAllLines(path))
            if (!string.IsNullOrWhiteSpace(line)) found.AddRange(HarvestAllStrings(line));
        return found;
    }
}

// Converted-from: RecordsGuardProbe
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

    /// <summary>The lane that is not a <c>format=</c> value at all — a <c>to_file</c> spill, whose rows are
    /// read back off the artifact rather than out of the response.</summary>
    public const string ArtifactLane = "artifact";

    /// <summary>
    /// Every lane housecarl_records renders, DERIVED from the product's own format vocabulary plus the
    /// artifact lane. A format the product gains is in this list the day it lands, and every consumer of the
    /// list gets its rows without being edited.
    /// </summary>
    public static readonly string[] Lanes =
        Enum.GetNames<Wire.QueryFormat>().Select(n => n.ToLowerInvariant()).Append(ArtifactLane).ToArray();

    /// <summary>
    /// HOW ONE LANE IS HARVESTED — the one home for the discriminant. The artifact lane's rows are read off
    /// the file the call spilled. Every other lane is decided by the RESPONSE rather than by the lane's name:
    /// a response that parses as a JSON document is walked for its strings, and one that does not is read
    /// line by line.
    ///
    /// <para>Asking the response is what keeps this from drifting. <c>dense</c> is named like a text lane and
    /// is a JSON render (<c>JsonWire.RenderCrossQueryDense</c>), so a discriminant keyed on the name
    /// <c>"json"</c> narrows dense from every string in the document to only the lines already matching
    /// <see cref="RemedyLine"/> — a strict narrowing, which goes green either way. A format the product gains
    /// is harvested for what it is on the day it lands.</para>
    /// </summary>
    public static List<string> HarvestLane(string lane, string response, string? artifactPath)
    {
        if (lane == ArtifactLane)
        {
            if (artifactPath is null)
                throw new ArgumentNullException(nameof(artifactPath),
                    "The artifact lane is harvested off the file the call spilled, so its path is required.");
            return HarvestArtifact(artifactPath);
        }

        return IsDocument(response)
            ? HarvestAllStrings(response)
            : response.Split('\n').Where(l => RemedyLine.IsMatch(l)).ToList();
    }

    /// <summary>Whether a response is a JSON render — asked of the response, never of the lane's name.</summary>
    public static bool IsDocument(string response)
    {
        try { using var _ = JsonDocument.Parse(response); return true; }
        catch (JsonException) { return false; }
    }

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
            var hits = HarvestLane(lane, resp, ArtifactPath);
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

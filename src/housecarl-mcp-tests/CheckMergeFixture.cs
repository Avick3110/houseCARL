using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The merged check render's fixture, moved from the retired <c>check-guard</c> probe: one plugin carrying
/// both swept families' findings (NPCs whose Race links into an absent master, weapons whose VMAD binds none of the
/// properties their .pex declares), a dialogue result built through the real <c>DialogueSweep.Run</c> with a stub
/// validator, and a facegen result. Built once per run and read only.</summary>
internal static class CheckMergeFixture
{
    internal const int Npcs = 40;
    internal const int Weapons = 40;
    internal const int Topics = 12;
    internal const int IssuesPerTopic = 2;
    internal const int SilentPerTopic = 1;
    internal const int UnreachableSeeds = 2;
    internal const int DialogueFindings = Topics * (IssuesPerTopic + SilentPerTopic);

    sealed record Built(ErrorCheckResult Errors, ScriptCheckResult Scripts);

    static readonly string Dir = Path.Combine(Path.GetTempPath(), "hc-check-merge-" + Guid.NewGuid().ToString("N"));
    static readonly Lazy<Built> Swept = new(Build);

    internal static ErrorCheckResult Errors => Swept.Value.Errors;
    internal static ScriptCheckResult Scripts => Swept.Value.Scripts;

    static Built Build()
    {
        Directory.CreateDirectory(Dir);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Directory.Delete(Dir, true); } catch { /* best-effort */ } };
        var scriptsDir = Path.Combine(Dir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        string ghostPath = Path.Combine(Dir, "HcCmGhost.esm");
        string mainPath = Path.Combine(Dir, "HcCm.esp");
        PexWriter.WritePex(Path.Combine(scriptsDir, "HcCmScript.pex"), "HcCmScript", parent: null,
            PexWriter.AutoObj("HcCmSpell", "Spell"),
            PexWriter.AutoObj("HcCmOther", "Spell"),
            PexWriter.AutoScalar("HcCmChance", "Int", null));

        var ghost = new SkyrimMod(new ModKey("HcCmGhost", ModType.Master), SkyrimRelease.SkyrimSE);
        var gRace = ghost.Races.AddNew(); gRace.EditorID = "HcCmGhostRace";
        ghost.BeginWrite.ToPath(ghostPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var mod = new SkyrimMod(new ModKey("HcCm", ModType.Plugin), SkyrimRelease.SkyrimSE);
        for (int i = 0; i < Npcs; i++)
        { var npc = mod.Npcs.AddNew(); npc.EditorID = $"HcCmNpc{i:D2}"; npc.Race.SetTo(gRace.FormKey); }
        for (int i = 0; i < Weapons; i++)
        { var w = mod.Weapons.AddNew(); w.EditorID = $"HcCmWeapon{i:D2}"; w.VirtualMachineAdapter = Vmad("HcCmScript"); }
        mod.BeginWrite.ToPath(mainPath).WithLoadOrder(new ISkyrimModGetter[] { ghost }).Write();

        using var resolver = LoadOrderResolver.Build(new[] { mainPath });
        using var assets = AssetResolver.Build("", "", Dir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        var errors = ErrorCheck.Run(resolver, null, 1000);
        var scripts = ScriptPropertyCheck.Run(resolver, assets, null, 1000);
        if (!errors.Success || !scripts.Success)
            throw new InvalidOperationException($"the check-merge fixture sweep failed: {errors.Error ?? scripts.Error}");
        return new Built(errors, scripts);
    }

    /// <summary>A VMAD binding one script with no properties.</summary>
    internal static VirtualMachineAdapter Vmad(string scriptClass)
    {
        var vmad = new VirtualMachineAdapter();
        vmad.Scripts.Add(new ScriptEntry { Name = scriptClass });
        return vmad;
    }

    // ---- the dialogue family ---------------------------------------------------------------------------

    internal const string Epoch = "7af654ddc8af948e";

    internal static DialogueCheckResult Dialogue()
        => DialogueRun(new[] { "000A01:HcCm.esp", "000B02:HcCm.esp", "not-a-formid" }, 1000);

    /// <summary><c>000A01</c> (or any seed in <c>A.esp</c>) is a quest owning <see cref="Topics"/> topics; every
    /// other resolvable seed is a named miss.</summary>
    internal static DialogueCheckResult DialogueRun(IReadOnlyList<string>? seeds, int limit)
        => DialogueSweep.Run(() => Bind(fk => fk.ID == 0x000A01 || fk.ModKey.Name == "A" ? QuestReport(fk)
                                 : DialogueValidationReport.ForError(fk, "no DIAL, QUST, DLVW or DLBR with this FormID is in the active order")),
                             seeds, limit);

    static DialogueSweep.Binding Bind(Func<FormKey, DialogueValidationReport> validate)
        => new(validate, token => FormKey.Factory((token ?? "").Trim()), Epoch);

    /// <summary>Two passing record-level seeds: a DLVW and a DLBR.</summary>
    internal static DialogueCheckResult RecordLevel()
        => DialogueSweep.Run(() => Bind(fk => RecordLevelReport(fk, fk.ID == 0x000E01 ? "view" : "branch", Array.Empty<DialogueIssue>())),
                             new[] { "000E01:HcCm.esp", "000E02:HcCm.esp" }, 1000);

    /// <summary>The same two kinds, both failing their parity.</summary>
    internal static DialogueCheckResult RecordLevelFailing()
        => DialogueSweep.Run(() => Bind(fk => RecordLevelReport(fk, fk.ID == 0x000E01 ? "view" : "branch",
                                       new[] { new DialogueIssue(DialogueIssueSeverity.Problem,
                                                   "the DNAM byte subrecord the Creation Kit always writes is absent") })),
                             new[] { "000E01:HcCm.esp", "000E02:HcCm.esp" }, 1000);

    /// <summary>One quest that owns topics, one passing DLVW.</summary>
    internal static DialogueCheckResult MixedKind()
        => DialogueSweep.Run(() => Bind(fk => fk.ID == 0x000A01 ? QuestReport(fk)
                                 : RecordLevelReport(fk, "view", Array.Empty<DialogueIssue>())),
                             new[] { "000A01:HcCm.esp", "000E01:HcCm.esp" }, 1000);

    static DialogueValidationReport RecordLevelReport(FormKey seed, string kind, IReadOnlyList<DialogueIssue> issues)
        => new(seed, kind, kind == "view" ? "HcCmView" : "HcCmBranch", "HcCm.esp",
               Array.Empty<TopicValidation>()) { InputIssues = issues };

    static DialogueValidationReport QuestReport(FormKey seed)
    {
        var topics = new List<TopicValidation>();
        for (int i = 0; i < Topics; i++)
        {
            var topicFk = new FormKey(seed.ModKey, (uint)(0x000C00 + i));
            var infoFk = new FormKey(seed.ModKey, (uint)(0x000D00 + i));
            var issues = Enumerable.Range(0, IssuesPerTopic)
                .Select(n => new DialogueIssue(DialogueIssueSeverity.Problem,
                    $"LinkTo target {topicFk} is not defined by any plugin in the active order (issue {n})"))
                .ToArray();
            var voice = Enumerable.Range(0, SilentPerTopic)
                .Select(n => new VoiceLine(infoFk, $"HcCmTopic{i:D2}", n + 1,
                    $"Sound\\Voice\\HcCm.esp\\MaleNord\\{infoFk.ID:X8}_{n + 1}.fuz", false, null, false,
                    $"Sound\\Voice\\HcCm.esp\\MaleNord\\{infoFk.ID:X8}_{n + 1}.lip", false, false))
                .ToArray();
            topics.Add(new TopicValidation(
                topicFk, $"HcCmTopic{i:D2}", "HcCm.esp",
                InfoCount: 3, ConditionedInfoCount: 2, DeletedInfoCount: 0, FragmentInfoCount: 1,
                Category: "Topic", Subtype: "CUST", SubtypeName: "Custom",
                Issues: issues, VoiceLines: voice,
                VoiceUndetermined: Array.Empty<VoiceUndetermined>(),
                ScriptFindings: Array.Empty<ScriptBindingFinding>()));
        }
        return new DialogueValidationReport(seed, "quest", "HcCmQuest", "HcCm.esp", topics);
    }

    /// <summary>The same result with its first resolved seed trimmed to <paramref name="n"/> topics.</summary>
    internal static DialogueCheckResult WithTopics(DialogueCheckResult r, int n)
    {
        var seed = r.Resolved.First();
        var trimmed = seed with { Report = seed.Report! with { Topics = seed.Report!.Topics.Take(n).ToArray() } };
        var seeds = r.Seeds.Select(s => ReferenceEquals(s, seed) ? trimmed : s).ToArray();
        return r with { Seeds = seeds, TopicsFound = n };
    }

    /// <summary>The smallest cap at which the sweep renders none of <paramref name="unit"/> and prints no remedy;
    /// 0 where there is none.</summary>
    internal static int QuietFloor(CheckSweep s, string unit, int ceiling = 40_000)
    {
        int noisy = CheckTextRender.RenderCheck(s, 1, 1000).Length;
        for (int cap = noisy; cap < ceiling; cap += 16)
        {
            var body = CheckTextRender.RenderCheck(s, cap, 1000);
            if (body.Contains("raise max_chars to at least ", StringComparison.Ordinal)) continue;
            if (Count(body, unit) > 0) break;
            return body.Length;
        }
        return 0;
    }

    /// <summary>One topic block's width in the text lane, measured through the render's own composer.</summary>
    internal static int TopicBlockWidth(DialogueCheckResult r)
    {
        var sb = new System.Text.StringBuilder();
        DialogueWire.AppendTopic(sb, r.Topics.First().Topic, indent: true, int.MaxValue);
        return sb.Length;
    }

    // ---- the facegen family ----------------------------------------------------------------------------

    internal static FaceGenCheckResult FaceGen()
    {
        FaceGenFinding Row(FaceGenFindingClass c, string id, string? mesh, string? tint, string? detail = null) =>
            new(id, "HcFgNpc" + id[..2], "HcCheckMerge.esp", "HcCheckMerge.esp", mesh, tint,
                FaceGenCheck.Token(c), FaceGenCheck.Fix(c), detail, mesh is null ? null : "ModA");
        var rows = new[]
        {
            Row(FaceGenFindingClass.TintAbsent, "000801:HcCheckMerge.esp", "ModA (loose)", null),
            Row(FaceGenFindingClass.MeshAbsent, "000802:HcCheckMerge.esp", null, "ModB (loose)"),
            Row(FaceGenFindingClass.SplitBake, "000803:HcCheckMerge.esp", "ModA (loose)", "ModB (loose)"),
            Row(FaceGenFindingClass.StaleBake, "000804:HcCheckMerge.esp", "ModA (loose)", "ModA (loose)",
                "winner HcCheckMerge.esp disagrees with the bake's own plugin ModA.esp on TintLayers"),
            Row(FaceGenFindingClass.Inert, "000805:HcCheckMerge.esp", null, null,
                "no plugin in this order defines this FormID, so no NPC reads this bake"),
        };
        var byClass = rows.GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byMod = rows.Where(r => r.OwningMod is not null).GroupBy(r => r.OwningMod!)
                        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return new FaceGenCheckResult(rows, NpcsScanned: 12, NpcsTemplated: 2, FilesSeen: 9,
                                      TotalFound: rows.Length, NoComparisonPole: 1,
                                      ByClass: SweepFindings.Histogram(byClass),
                                      ByOwningMod: SweepFindings.Histogram(byMod),
                                      CountsOnly: false, ExcludedPlugins: new Dictionary<string, string>(),
                                      Error: null, Epoch: "e2-facegenfixture", Limit: 1000, WholeOrder: true);
    }

    // ---- the shapes the tests ask about ----------------------------------------------------------------

    internal static CheckSweep Both() => new(Sel("errors", "scripts"), Errors, Scripts);

    internal static CheckSweep All()
        => new(Sel("errors", "scripts", "dialogue", "facegen"), Errors, Scripts, Dialogue(), FaceGen());

    internal static readonly Dictionary<string, string> Roster = new()
    {
        ["HcCmBroken.esp"] = "header could not be parsed",
        ["HcCmAlsoBroken.esp"] = "header could not be parsed",
    };

    internal static CheckSweep WithRoster()
        => new(Sel("errors", "scripts"), Errors with { ExcludedPlugins = Roster }, Scripts with { ExcludedPlugins = Roster });

    internal static SweepFamilySelection Sel(params string[] tokens)
    {
        Assert.True(SweepFamilySelection.TryParse(tokens.Length == 0 ? null : tokens, out var sel, out var err), err);
        return sel!;
    }

    internal static string Text(CheckSweep s, int cap) => CheckTextRender.RenderCheck(s, cap);
    internal static string Json(CheckSweep s, int cap) => JsonWire.RenderCheck(s, cap);

    // ---- readers ---------------------------------------------------------------------------------------

    /// <summary>The first number of a "{shown} of the {total}" / "all {n}" pair ending in <paramref name="tail"/>,
    /// or -1 where the sentence is absent.</summary>
    internal static int StatedPair(string text, string tail)
    {
        int at = text.IndexOf(tail, StringComparison.Ordinal);
        if (at < 0) return -1;
        var head = text[..at];
        var last = new string(head.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (!int.TryParse(last, out var total)) return -1;
        var rest = head[..(head.Length - last.Length)];
        if (rest.EndsWith(" all ", StringComparison.Ordinal)) return total;
        if (!rest.EndsWith(" of the ", StringComparison.Ordinal)) return -1;
        var shown = new string(rest[..^8].Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(shown, out var n) ? n : -1;
    }

    internal static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    internal static string FirstLineWith(string text, string needle)
        => text.Split('\n').FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal)) ?? "<absent>";

    internal static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    internal static int? Num(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    internal static string? Str(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static bool? Bool(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    internal static JsonElement? Obj(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static JsonElement? Arr(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Array ? v : null;

    internal static JsonElement? At(JsonElement? e, int i)
        => e is { ValueKind: JsonValueKind.Array } a && i >= 0 && i < a.GetArrayLength() ? a[i] : null;

    internal static bool Has(JsonElement? e, string name)
        => e is { } o && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out _);

    internal static string[] Strings(JsonElement? e)
        => e is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "<not-a-string>").ToArray()
            : Array.Empty<string>();

    internal static JsonElement? Family(string json, string family) => Obj(Obj(Root(json), "families"), family);

    /// <summary>Every object in <paramref name="e"/> that names one key twice, walked whole.</summary>
    internal static void CollectDuplicateKeys(JsonElement e, string where, List<string> into)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var g in e.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal))
                if (g.Count() > 1) into.Add($"{where}: '{g.Key}' written {g.Count()} times in one object");
            foreach (var p in e.EnumerateObject()) CollectDuplicateKeys(p.Value, where + "." + p.Name, into);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var item in e.EnumerateArray()) CollectDuplicateKeys(item, $"{where}[{i++}]", into);
        }
    }

    /// <summary>How many elements one family's row array carries, or -1 where it is absent.</summary>
    internal static int ArrayLength(JsonElement root, string family, string array)
        => Arr(Obj(Obj(root, "families"), family), array) is { } rows ? rows.GetArrayLength() : -1;
}

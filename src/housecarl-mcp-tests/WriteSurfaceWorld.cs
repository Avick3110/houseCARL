using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>
/// The write-surface order the old write-surface-guard probe built: a master and a replacer that WINS the subject weapon
/// at a different damage (master 10, winner 99), a disabled mod overriding it at a third (42) and originating a weapon
/// of its own, a disabled chain plugin whose override links into another disabled master, and one filename provided by
/// two disabled folders. The master also defines a topic and a cell the replacer wins, each with a child of its own.
/// <para>Class fixtures share one; a test that writes in place builds its own, since that rewrites the replacer.</para>
/// </summary>
public sealed class WriteSurfaceWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    public string ModsDir { get; }
    public string MasterName { get; }
    public string ReplacerName { get; }
    public string SubjectFid { get; }
    public FormKey SubjectKey { get; }
    public string OffName { get; }
    public string OffPath { get; }
    public string OffFolder => "W2Off";
    public string OffOwnFid { get; }
    public string MasterOnlyFid { get; }
    public string TopicFid { get; }
    public FormKey TopicKey { get; }
    public string CellFid { get; }
    public FormKey CellKey { get; }
    public string WinnerLineFid { get; }
    public string AmbName { get; }
    public string ChainName { get; }

    public string ReplacerPath => Path.Combine(ModsDir, "W2Repl", ReplacerName);
    public string MasterPath => Path.Combine(ModsDir, "W2Master", MasterName);

    readonly string _priorCorpusPath;
    readonly ResultsDirScope _results;

    public WriteSurfaceWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-write-surface-" + Guid.NewGuid().ToString("N"));
        _results = new ResultsDirScope(Path.Combine(Root, "server-results"));

        string instance = Path.Combine(Root, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        ModsDir = Path.Combine(instance, "mods");
        Directory.CreateDirectory(profiles); Directory.CreateDirectory(ModsDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcW2Master", ModType.Master);
        var rKey = new ModKey("HcW2Repl", ModType.Plugin);
        MasterName = mKey.FileName.String;
        ReplacerName = rKey.FileName.String;
        Directory.CreateDirectory(Path.GetDirectoryName(MasterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(ReplacerPath)!);

        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var subject = m.Weapons.AddNew();
        subject.EditorID = "W2Subject";
        subject.Name = "Master Sword";
        subject.BasicStats = new WeaponBasicStats { Damage = 10 };
        var masterOnly = m.Weapons.AddNew();
        masterOnly.EditorID = "W2MasterOnly";
        masterOnly.BasicStats = new WeaponBasicStats { Damage = 3 };

        var topic = m.DialogTopics.AddNew();
        topic.EditorID = "W2Topic";
        topic.Name = "Master Topic";
        topic.Responses.Add(new DialogResponses(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2MasterLine" });

        var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2Cell", Name = "Master Cell" };
        cell.Persistent.Add(new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2MasterRef" });
        var cSub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        cSub.Cells.Add(cell);
        var cBlock = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        cBlock.SubBlocks.Add(cSub);
        m.Cells.Records.Add(cBlock);
        m.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var mCache = m.ToImmutableLinkCache();

        var r = new SkyrimMod(rKey, SkyrimRelease.SkyrimSE);
        var rw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(r, subject);
        rw.Name = "Winner Sword";
        rw.BasicStats = new WeaponBasicStats { Damage = 99 };
        // The winner's topic links a quest the replacer owns, so hosting from the winner would make it a master.
        var winnerQuest = r.Quests.AddNew();
        winnerQuest.EditorID = "W2WinnerQuest";
        var topicOverride = (IDialogTopic)WriteEngine.GenericGetOrAddAsOverride(r, topic);
        topicOverride.Name = "Winner Topic";
        topicOverride.Quest.SetTo(winnerQuest.FormKey);
        var winnerLine = new DialogResponses(r.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2WinnerLine" };
        topicOverride.Responses.Add(winnerLine);
        var cellOverride = (ICell)WriteEngine.GenericGetOrAddAsOverride(r, cell, mCache);
        cellOverride.Name = "Winner Cell";
        cellOverride.Persistent.Add(new PlacedObject(r.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "W2WinnerRef" });
        r.BeginWrite.ToPath(ReplacerPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // A disabled mod: overrides the subject at 42 and originates a weapon of its own.
        var oKey = new ModKey("HcW2Off", ModType.Plugin);
        OffName = oKey.FileName.String;
        OffPath = Path.Combine(ModsDir, "W2Off", OffName);
        Directory.CreateDirectory(Path.GetDirectoryName(OffPath)!);
        var o = new SkyrimMod(oKey, SkyrimRelease.SkyrimSE);
        var ow = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(o, subject);
        ow.Name = "Disabled Sword";
        ow.BasicStats = new WeaponBasicStats { Damage = 42 };
        var ownWeapon = o.Weapons.AddNew();
        ownWeapon.EditorID = "W2OffOwn";
        ownWeapon.BasicStats = new WeaponBasicStats { Damage = 7 };
        o.BeginWrite.ToPath(OffPath).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();

        // A disabled master and a disabled plugin whose override of the subject links into it.
        var depKey = new ModKey("HcW2OffDep", ModType.Master);
        var depPath = Path.Combine(ModsDir, "W2OffDep", depKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(depPath)!);
        var dep = new SkyrimMod(depKey, SkyrimRelease.SkyrimSE);
        var depKw = dep.Keywords.AddNew();
        depKw.EditorID = "W2OffDepKw";
        dep.BeginWrite.ToPath(depPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var cKey = new ModKey("HcW2OffChain", ModType.Plugin);
        ChainName = cKey.FileName.String;
        var chainPath = Path.Combine(ModsDir, "W2OffChain", ChainName);
        Directory.CreateDirectory(Path.GetDirectoryName(chainPath)!);
        var c = new SkyrimMod(cKey, SkyrimRelease.SkyrimSE);
        var cw = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(c, subject);
        cw.BasicStats = new WeaponBasicStats { Damage = 33 };
        (cw.Keywords ??= new()).Add(depKw);
        c.BeginWrite.ToPath(chainPath).WithLoadOrder(new ISkyrimModGetter[] { m, dep }).Write();

        // One filename, two disabled folders.
        var aKey = new ModKey("HcW2Amb", ModType.Plugin);
        AmbName = aKey.FileName.String;
        foreach (var folder in new[] { "W2AmbA", "W2AmbB" })
        {
            var ap = Path.Combine(ModsDir, folder, AmbName);
            Directory.CreateDirectory(Path.GetDirectoryName(ap)!);
            var a = new SkyrimMod(aKey, SkyrimRelease.SkyrimSE);
            ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, subject)).BasicStats = new WeaponBasicStats { Damage = 1 };
            a.BeginWrite.ToPath(ap).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + ReplacerName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterName + "\r\n*" + ReplacerName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"),
            "# header\r\n-W2AmbB\r\n-W2AmbA\r\n-W2OffChain\r\n-W2OffDep\r\n-W2Off\r\n+W2Repl\r\n+W2Master\r\n");

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();

        SubjectFid = $"{subject.FormKey.ID:X6}:{MasterName}";
        SubjectKey = subject.FormKey;
        OffOwnFid = $"{ownWeapon.FormKey.ID:X6}:{OffName}";
        MasterOnlyFid = $"{masterOnly.FormKey.ID:X6}:{MasterName}";
        TopicFid = $"{topic.FormKey.ID:X6}:{MasterName}";
        TopicKey = topic.FormKey;
        CellFid = $"{cell.FormKey.ID:X6}:{MasterName}";
        CellKey = cell.FormKey;
        WinnerLineFid = $"{winnerLine.FormKey.ID:X6}:{ReplacerName}";
    }

    /// <summary>The written artifact's path, parsed out of the text render a caller gets.</summary>
    public string? ArtifactPathFrom(string render)
    {
        if (!render.StartsWith("wrote ", StringComparison.Ordinal) && !render.StartsWith("extended ", StringComparison.Ordinal)) return null;
        var file = render[(render.IndexOf(' ') + 1)..];
        file = file[..file.IndexOf(' ')];
        var mod = render.Contains("mod folder: ", StringComparison.Ordinal)
            ? render[(render.IndexOf("mod folder: ", StringComparison.Ordinal) + 12)..].Split('\n')[0].Split("  ")[0].Trim()
            : null;
        return mod is null ? null : Path.Combine(ModsDir, mod, file);
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        _results.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>Readers over a written plugin and a render, shared by the write-surface tests.</summary>
static class WriteSurfaceReads
{
    public static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>A Windows path escaped for a JSON string body.</summary>
    public static string JsonPath(string path) => path.Replace("\\", "\\\\");

    public static JsonDocument Doc(string raw) => JsonDocument.Parse(raw);

    public static List<string> EditorIdsIn(string espPath)
    {
        var found = new List<string>();
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        foreach (var rec in ov.EnumerateMajorRecords())
            if (rec.EditorID is { Length: > 0 } e) found.Add(e);
        return found;
    }

    public static ushort? DamageIn(string espPath, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        return ov.Weapons.FirstOrDefault(w => w.FormKey == fk)?.BasicStats?.Damage;
    }

    public static string? TopicNameIn(string espPath, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        return ov.DialogTopics.FirstOrDefault(d => d.FormKey == fk)?.Name?.String;
    }

    public static List<string> ChildLinesIn(string espPath, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        return (ov.DialogTopics.FirstOrDefault(d => d.FormKey == fk)?.Responses ?? Enumerable.Empty<IDialogResponsesGetter>())
            .Select(r => r.EditorID ?? r.FormKey.ToString()).ToList();
    }

    static IEnumerable<ICellGetter> CellsIn(ISkyrimModGetter ov) =>
        (ov.Cells.Records ?? Enumerable.Empty<ICellBlockGetter>())
            .SelectMany(b => b.SubBlocks ?? Enumerable.Empty<ICellSubBlockGetter>())
            .SelectMany(s => s.Cells ?? Enumerable.Empty<ICellGetter>());

    public static List<string> CellRefsIn(string espPath, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var cell = CellsIn(ov).FirstOrDefault(c => c.FormKey == fk);
        return (cell?.Persistent ?? Enumerable.Empty<IPlacedGetter>())
            .Concat(cell?.Temporary ?? Enumerable.Empty<IPlacedGetter>())
            .Select(p => p.EditorID ?? p.FormKey.ToString()).ToList();
    }

    public static string? CellNameIn(string espPath, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        return CellsIn(ov).FirstOrDefault(c => c.FormKey == fk)?.Name?.String;
    }

    /// <summary>Set every weapon's damage in a plugin, rewriting it.</summary>
    public static void BumpDamage(string espPath, ushort damage)
    {
        var mod = SkyrimMod.CreateFromBinary(espPath, SkyrimRelease.SkyrimSE);
        foreach (var w in mod.Weapons) w.BasicStats = new WeaponBasicStats { Damage = damage };
        mod.BeginWrite.ToPath(espPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    /// <summary>The allocated FormIDs out of a create render's per-record lines ("  Keyword 000800:Patch.esp  Edid").</summary>
    public static List<string> FormIdsFrom(string render)
    {
        var ids = new List<string>();
        foreach (var line in render.Split('\n'))
        {
            var parts = line.Trim().Split("  ", StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var head = parts[0].Split(' ');
            if (head.Length == 2 && head[1].Contains(':') && head[1].Contains(".es", StringComparison.OrdinalIgnoreCase))
                ids.Add(head[1]);
        }
        return ids;
    }

    /// <summary>The render's "masters:" line alone.</summary>
    public static string MastersLineOf(string render)
        => render.Split('\n').FirstOrDefault(l => l.StartsWith("masters:", StringComparison.Ordinal)) ?? "";

    /// <summary>The rest of one render line from <paramref name="after"/> on, or null when absent.</summary>
    public static string? LineAfter(string render, string after)
    {
        int at = render.IndexOf(after, StringComparison.Ordinal);
        if (at < 0) return null;
        var tail = render[(at + after.Length)..];
        int eol = tail.IndexOf('\n');
        return eol < 0 ? tail : tail[..eol];
    }

    /// <summary>The (source file, types) of the read-back call a truncated create render names.</summary>
    public static (string? file, string[]? types) ParseReadBackCall(string render)
    {
        const string marker = "housecarl_records source=\"";
        int at = render.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return (null, null);
        var tail = render[(at + marker.Length)..];
        int quote = tail.IndexOf('"');
        int open = tail.IndexOf("types=[", StringComparison.Ordinal);
        int close = open < 0 ? -1 : tail.IndexOf(']', open);
        if (quote < 0 || open < 0 || close < 0) return (null, null);
        var types = tail[(open + 7)..close].Split(',').Select(t => t.Trim().Trim('"')).Where(t => t.Length > 0).ToArray();
        return (tail[..quote], types.Length > 0 ? types : null);
    }

    public static string CopyOps(string formid, string fieldPath, string fromSource) =>
        $$"""[{"formid":"{{formid}}","field_path":"{{fieldPath}}","op":"CopyFrom","from_source":"{{JsonPath(fromSource)}}"}]""";

    public static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

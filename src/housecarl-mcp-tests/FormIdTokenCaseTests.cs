using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world whose patch plugin spells its master in a DIFFERENT CASE from the file on disk — the shape
/// every real load order has, because a plugin's master list carries whatever its author typed. The master's
/// records are read two ways: straight off the master (Mutagen spells that key from the file it opened) and
/// through the patch's link into it (spelled from the patch's master list).</summary>
public sealed class MasterCaseDriftWorld : IDisposable
{
    public const string MasterName = "HcCaseMaster.esm";     // the spelling ON DISK — the canonical one
    public const string PatchName = "HcCasePatch.esp";
    const string MasterInMasterList = "hccasemaster.esm";    // what the patch's header says instead

    public string Root { get; }
    public string MasterPath { get; }
    public string PatchPath { get; }
    public LoadOrderService Svc { get; }
    public FormKey Race { get; }
    public FormKey Npc { get; }

    readonly string _priorCorpusPath;

    public MasterCaseDriftWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;    // a process-global this world repoints and restores
        Root = Path.Combine(Path.GetTempPath(), "hc-case-drift-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "inst");
        var masterDir = Path.Combine(instance, "mods", "CaseMaster");
        var patchDir = Path.Combine(instance, "mods", "CasePatch");
        Directory.CreateDirectory(masterDir);
        Directory.CreateDirectory(patchDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var master = new SkyrimMod(new ModKey("HcCaseMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var race = master.Races.AddNew();
        race.EditorID = "HcCaseRace";
        Race = race.FormKey;
        MasterPath = Path.Combine(masterDir, MasterName);
        master.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var patch = new SkyrimMod(new ModKey("HcCasePatch", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var npc = patch.Npcs.AddNew();
        npc.EditorID = "HcCaseNpc";
        npc.Race.SetTo(Race);
        Npc = npc.FormKey;
        PatchPath = Path.Combine(patchDir, PatchName);
        patch.BeginWrite.ToPath(PatchPath).WithLoadOrder(new ISkyrimModGetter[] { master }).Write();

        LowercaseTheMasterEntry(PatchPath);

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n*" + PatchName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+CasePatch\r\n+CaseMaster\r\n");

        var store = new UserConfigStore(Path.Combine(Root, "user.json"));
        Svc = LoadOrderService.WithInstance(instance, 0, store);
    }

    /// <summary>Respell the MAST entry in the plugin's header to lowercase — same length, so the file stays valid.</summary>
    static void LowercaseTheMasterEntry(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var want = Encoding.ASCII.GetBytes(MasterName);
        var to = Encoding.ASCII.GetBytes(MasterInMasterList);
        int at = IndexOf(bytes, want);
        if (at < 0) throw new InvalidOperationException($"'{MasterName}' is not spelled in {Path.GetFileName(path)}'s header.");
        Array.Copy(to, 0, bytes, at, to.Length);
        File.WriteAllBytes(path, bytes);
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>A FormID token spells its plugin the way the file is spelled on disk, whichever read produced it —
/// so two reads of the same record hand back the same string and a join of them lines up (#664).</summary>
[Trait("tier", "integration")]
public sealed class FormIdTokenCaseTests : IDisposable
{
    readonly MasterCaseDriftWorld _w = new();

    public void Dispose() => _w.Dispose();

    /// <summary>The one token in this response that names the master, however either half is spelled.</summary>
    static string MasterToken(string text)
    {
        var m = Regex.Match(text, "[0-9A-F]{6}:" + Regex.Escape(MasterCaseDriftWorld.MasterName), RegexOptions.IgnoreCase);
        Assert.True(m.Success, $"no token naming {MasterCaseDriftWorld.MasterName} in: {text}");
        return m.Value;
    }

    [Fact]
    public void TheSameRecordSpellsItsPluginTheSameWayReadDirectlyAndReadThroughALink()
    {
        // The bug's precondition: the patch's own header names the master in another case.
        Assert.Contains("hccasemaster.esm", Encoding.ASCII.GetString(File.ReadAllBytes(_w.PatchPath)));

        var scanned = RecordsTools.Records(_w.Svc, types: new[] { "RACE" }, format: "json");
        var linked = RecordsTools.Records(_w.Svc,
            formids: new[] { $"{_w.Npc.ID:X6}:{MasterCaseDriftWorld.PatchName}" },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Race" } },
            format: "json");

        var direct = MasterToken(scanned);
        var throughTheLink = MasterToken(linked);

        Assert.Equal(direct, throughTheLink, StringComparer.Ordinal);
        Assert.Equal($"{_w.Race.ID:X6}:{Path.GetFileName(_w.MasterPath)}", direct);
    }
}

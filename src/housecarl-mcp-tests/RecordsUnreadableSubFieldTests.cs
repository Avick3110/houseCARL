using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin whose single weapon carries a SHORT DATA subrecord: the record opens, its BasicStats
/// substruct opens, and reading the last field out of that substruct throws inside Mutagen. Two bytes of the
/// plugin are the whole fixture — the file is written by Mutagen, then DATA's declared length is cut from 10 to
/// 8 (and the enclosing record and group sizes with it), which is what a truncated or badly merged plugin looks
/// like on disk. It sits in a switched-OFF mod folder and is read by name, the same off-order lane a caller uses
/// to look inside a plugin that is not in the order; a clean master holds the active order.</summary>
public sealed class TruncatedSubFieldWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    /// <summary>The plugin holding the weapon whose BasicStats.Damage cannot be read. Value and Weight, which sit
    /// before the cut, still read.</summary>
    public string TruncName { get; }
    public const string WeaponEditorId = "HcTruncWeap";
    public const int Value = 34;

    static int Find(byte[] b, string sig, int from)
    {
        for (int i = from; i + 4 <= b.Length; i++)
            if (b[i] == sig[0] && b[i + 1] == sig[1] && b[i + 2] == sig[2] && b[i + 3] == sig[3]) return i;
        return -1;
    }

    readonly string _priorCorpusPath;

    public TruncatedSubFieldWorld()
    {
        // CorpusRulebook.CorpusPath is a process-global: capture before repointing, and restore before the
        // directory the new value names is deleted.
        _priorCorpusPath = CorpusRulebook.CorpusPath;

        Root = Path.Combine(Path.GetTempPath(), "hc-truncated-subfield-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        string P(string folder, ModKey k)
        {
            var p = Path.Combine(mods, folder, k.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            return p;
        }

        var cleanKey = new ModKey("HcTruncClean", ModType.Master);
        var clean = new SkyrimMod(cleanKey, SkyrimRelease.SkyrimSE);
        clean.Weapons.AddNew().EditorID = "HcCleanWeap";
        clean.BeginWrite.ToPath(P("CleanMod", cleanKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var truncKey = new ModKey("HcTrunc", ModType.Plugin);
        TruncName = truncKey.FileName.String;
        var m = new SkyrimMod(truncKey, SkyrimRelease.SkyrimSE);
        var weapon = m.Weapons.AddNew();
        weapon.EditorID = WeaponEditorId;
        weapon.BasicStats = new WeaponBasicStats { Damage = 12, Value = Value, Weight = 5.5f };
        var path = P("TruncMod", truncKey);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // …and now cut two bytes off DATA, so the last field in the struct reads past the end of its own
        // subrecord. The record, the group and the file all stay internally consistent: nothing but that one
        // field is broken.
        var bytes = File.ReadAllBytes(path);
        int grup = Find(bytes, "GRUP", 0);           // the WEAP group header; its own label is "WEAP" at +8,
        int rec = Find(bytes, "WEAP", grup + 24);    // so the record itself is searched for past the header
        int sub = Find(bytes, "DATA", rec + 24);
        int len = BitConverter.ToUInt16(bytes, sub + 4);
        const int Keep = 8;
        int cut = len - Keep;
        BitConverter.GetBytes((ushort)Keep).CopyTo(bytes, sub + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, rec + 4) - (uint)cut).CopyTo(bytes, rec + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, grup + 4) - (uint)cut).CopyTo(bytes, grup + 4);
        File.WriteAllBytes(path, bytes[..(sub + 6 + Keep)].Concat(bytes[(sub + 6 + len)..]).ToArray());

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + cleanKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + cleanKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n-TruncMod\r\n+CleanMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TruncatedSubFieldFixture : IDisposable
{
    public TruncatedSubFieldWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A sub-field that cannot be read says so, on the field, wherever fields are rendered. It is never
/// dropped: an omitted line and an absent optional are the same thing to a caller, so a swallowed fault reports
/// "I could not look" as "there is nothing there".</summary>
[Trait("tier", "integration")]
public sealed class RecordsUnreadableSubFieldTests : IClassFixture<TruncatedSubFieldFixture>
{
    readonly TruncatedSubFieldWorld _w;
    public RecordsUnreadableSubFieldTests(TruncatedSubFieldFixture f) => _w = f.W;

    string Read(RecordsTools.RecordsProject project, string? format = null) =>
        RecordsTools.Records(_w.Svc, source: JsonDocument.Parse("\"" + _w.TruncName + "\"").RootElement.Clone(),
                             types: new[] { "WEAP" }, project: project, format: format);

    static RecordsTools.RecordsProject Stats =>
        new() { form = "fields", fields = new[] { "BasicStats" }, depth = 2 };

    /// <summary>The fault is isolated to its own line: the sub-field names itself and its reason, and the siblings
    /// beside it are real reads and are kept.</summary>
    [Fact]
    public void TheFieldsFormNamesTheSubFieldAndTheReasonAndKeepsItsSiblings()
    {
        var r = Read(Stats);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage = (unreadable: ", r);
        Assert.Contains("BasicStats.Value = " + TruncatedSubFieldWorld.Value, r);
        // The reason is Mutagen's own, not a wrapper's: reflection's "target of an invocation" names nothing.
        Assert.DoesNotContain("target of an invocation", r);
    }

    [Fact]
    public void TheJsonRenderCarriesTheFaultAsThatFieldsOwnNote()
    {
        using var doc = JsonDocument.Parse(Read(Stats, "json"));
        var field = doc.RootElement.GetProperty("records")[0].GetProperty("fields")
                       .EnumerateArray().Single(f => f.GetProperty("path").GetString() == "BasicStats.Damage");
        Assert.StartsWith("(unreadable: ", field.GetProperty("note").GetString());
        Assert.False(field.TryGetProperty("value", out _));
    }

    /// <summary>A whole-record read is still a whole-record read: every other field answers, and the one that
    /// could not be read is named among them.</summary>
    [Fact]
    public void AnEverythingReadKeepsTheRestOfTheRecordAndStillNamesTheFault()
    {
        var r = Read(new RecordsTools.RecordsProject { form = "everything", depth = 2 });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(TruncatedSubFieldWorld.WeaponEditorId, r);
        Assert.Contains("BasicStats.Value = " + TruncatedSubFieldWorld.Value, r);
        Assert.Contains("BasicStats.Damage = (unreadable: ", r);
    }
}

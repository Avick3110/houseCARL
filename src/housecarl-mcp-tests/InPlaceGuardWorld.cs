using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[CollectionDefinition(Name)]
public sealed class InPlaceGuardCollection : ICollectionFixture<InPlaceGuardWorld>
{
    public const string Name = "InPlaceGuard";
}

/// <summary>
/// The in-place lane's world, moved from the <c>inplace-guard</c> probe: a master weapon, a user override of it
/// (Damage 20, Name "UserSword", author counter 0x123), a higher override (Damage 99, "HighSword") that also defines a
/// keyword, and a localized twin of the user override with its Strings folder beside it. Every test edits a fresh copy
/// of the user plugin in its own folder, so the pristine files are never written.
/// </summary>
public sealed class InPlaceGuardWorld : IDisposable
{
    public const string MasterName = "HcInPlaceMaster.esm";
    public const string UserName = "HcInPlaceUser.esp";
    public const string HighName = "HcInPlaceHigh.esp";
    public const string LocName = "HcInPlaceLoc.esp";

    public string Root { get; }
    public string MasterPath { get; }
    public string UserPristine { get; }
    public string HighPath { get; }
    public string LocPristine { get; }
    public string CorpusPath { get; }
    public CorpusRulebook Rulebook { get; }

    public FormKey Weapon { get; }
    public FormKey Weapon2 { get; }
    public FormKey Topic { get; }
    public FormKey World { get; }
    public FormKey HighKeyword { get; }

    /// <summary>The weapon as the tools take it: 6 hex, colon, defining master.</summary>
    public string WeaponId => $"{Weapon.ID:X6}:{MasterName}";
    public string Weapon2Id => $"{Weapon2.ID:X6}:{MasterName}";
    public string TopicId => $"{Topic.ID:X6}:{MasterName}";
    public string WorldId => $"{World.ID:X6}:{MasterName}";

    readonly string _priorCorpusPath;
    int _next;

    public InPlaceGuardWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
        Root = Path.Combine(Path.GetTempPath(), "hc-inplace-guard-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusPath = Path.Combine(genDir, "corpus.json");
        CorpusRulebook.CorpusPath = CorpusPath;
        Rulebook = CorpusRulebook.Load(CorpusPath);

        MasterPath = Path.Combine(Root, MasterName);
        UserPristine = Path.Combine(Root, "pristine", UserName);
        HighPath = Path.Combine(Root, HighName);
        LocPristine = Path.Combine(Root, "locpristine", LocName);
        Directory.CreateDirectory(Path.GetDirectoryName(UserPristine)!);
        Directory.CreateDirectory(Path.GetDirectoryName(LocPristine)!);

        var m = new SkyrimMod(new ModKey("HcInPlaceMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcIP_Weap"; w.BasicStats = new WeaponBasicStats { Damage = 10 }; w.Name = "MasterSword";
        var w2 = m.Weapons.AddNew(); w2.EditorID = "HcIP_Weap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
        var t = m.DialogTopics.AddNew(); t.EditorID = "HcIP_Topic";
        var ws = m.Worldspaces.AddNew(); ws.EditorID = "HcIP_World";
        Weapon = w.FormKey; Weapon2 = w2.FormKey; Topic = t.FormKey; World = ws.FormKey;
        m.BeginWrite.ToPath(MasterPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var mOv = SkyrimMod.CreateFromBinaryOverlay(MasterPath, SkyrimRelease.SkyrimSE);
        var master = mOv.Weapons.First(x => x.FormKey == Weapon);

        var u = new SkyrimMod(new ModKey("HcInPlaceUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var uw = u.Weapons.GetOrAddAsOverride(master);
        uw.BasicStats!.Damage = 20; uw.Name = "UserSword";
        u.ModHeader.Stats.NextFormID = 0x123;   // a sub-0x800 author counter, which the patch lane's floor would move
        u.BeginWrite.ToPath(UserPristine).WithLoadOrder(new ISkyrimModGetter[] { mOv }).NoNextFormIDProcessing().Write();

        var h = new SkyrimMod(new ModKey("HcInPlaceHigh", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var hw = h.Weapons.GetOrAddAsOverride(master);
        hw.BasicStats!.Damage = 99; hw.Name = "HighSword";
        var hkw = h.Keywords.AddNew(); hkw.EditorID = "HcIP_HighKw";
        HighKeyword = hkw.FormKey;
        h.BeginWrite.ToPath(HighPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();

        var lo = new SkyrimMod(new ModKey("HcInPlaceLoc", ModType.Plugin), SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var low = lo.Weapons.GetOrAddAsOverride(master);
        low.BasicStats!.Damage = 20; low.Name = "LocSword";
        lo.BeginWrite.ToPath(LocPristine).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(LocPristine)!, "Strings")))
            throw new InvalidOperationException("the localized fixture wrote no Strings folder, so it is not localized");
    }

    /// <summary>Points the process-global corpus path back at this world's corpus; another world may have moved it.</summary>
    public void UseCorpus() => CorpusRulebook.CorpusPath = CorpusPath;

    /// <summary>A fresh copy of the user plugin, under its own filename, in a folder no other test uses.</summary>
    public string FreshUser() => CopyInto(NewDir(), UserPristine, UserName);

    /// <summary>A fresh copy of the localized plugin together with its Strings folder.</summary>
    public string FreshLocalized()
    {
        var dir = NewDir();
        var path = CopyInto(dir, LocPristine, LocName);
        var src = Path.Combine(Path.GetDirectoryName(LocPristine)!, "Strings");
        var dst = Path.Combine(dir, "Strings");
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        return path;
    }

    /// <summary>A consent store path no other test uses.</summary>
    public string NewStorePath() => Path.Combine(NewDir(), "user.json");

    public string NewDir()
    {
        var dir = Path.Combine(Root, "t" + Interlocked.Increment(ref _next));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string CopyInto(string dir, string source, string name)
    {
        var path = Path.Combine(dir, name);
        File.Copy(source, path);
        return path;
    }

    // ---- read-backs off the written file -----------------------------------------------------------------

    static T Read<T>(string path, Func<ISkyrimModDisposableGetter, T> read)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return read(ov);
    }

    public static int Damage(string path, FormKey fk) =>
        Read(path, ov => (int?)ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.BasicStats?.Damage ?? -1);

    public static string? Name(string path, FormKey fk) =>
        Read(path, ov => ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.Name?.String);

    public static bool WeaponHasKeyword(string path, FormKey weapon, FormKey keyword) =>
        Read(path, ov => ov.Weapons.FirstOrDefault(x => x.FormKey == weapon)?.Keywords?.Any(k => k.FormKey == keyword) ?? false);

    public static string? EditorIdAt(string path, FormKey fk) =>
        Read(path, ov => ov.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == fk)?.EditorID);

    public static bool Present(string path, FormKey fk) =>
        Read(path, ov => ov.EnumerateMajorRecords().Any(r => r.FormKey == fk));

    public static uint NextFormId(string path) => Read(path, ov => ov.ModHeader.Stats.NextFormID);

    public static List<string> Masters(string path) =>
        Read(path, ov => ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList());

    public static bool HasMaster(string path, string name) =>
        Masters(path).Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Name and hash of every file in a folder's Strings, or a token for a missing folder.</summary>
    public static string StringsSnapshot(string folder)
    {
        var dir = Path.Combine(folder, "Strings");
        if (!Directory.Exists(dir)) return "<no Strings folder>";
        return string.Join(";", Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => Path.GetFileName(f) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One record created through the service's batch entry point, the call the create tool makes.</summary>
internal static class InPlaceGuardCreateCall
{
    public static WritePatchBuilder.CreateOutcome InPlaceGuardCreate(
        this LoadOrderService svc, string recordType, string editorid, IReadOnlyList<BulkOp> operations,
        string? patchName, string? into, bool fullReadback = false, string? parent = null,
        string? collection = null, string? grid = null,
        string? target = null, bool inPlace = false, bool acknowledge = false) =>
        svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp
                {
                    RecordType = recordType, Editorid = editorid, Operations = operations.ToArray(),
                    Parent = parent, Collection = collection, Grid = grid,
                }
            },
            patchName, into, fullReadback, target, inPlace, acknowledge);
}

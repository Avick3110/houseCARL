using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;

namespace HousecarlMcpTests;

/// <summary>One localized plugin, <c>ZRef.esp</c>, in a mod folder beside a game Data folder holding an empty
/// Skyrim.esm, with its .STRINGS tables rearranged into one <see cref="Arrangement"/>. Each test builds its own and
/// disposes it; moved from the <c>localized-write-guard</c> probe's fixture.</summary>
public sealed class LocalizedWriteGuardFixture : IDisposable
{
    public enum Arrangement
    {
        LooseComplete, LoosePartial, LooseAndGameData, GameDataOnly, Nowhere, MalformedBsa, SiblingStem,
        GameDataBsa, NeighbourTablesOnly, UnknownLanguageToken, ManyNeighbourTables,
    }

    const int Records = 4;
    readonly string _root;
    readonly List<string> _denied = new();

    public string Plugin { get; }
    public string DataDir { get; }
    public string SkyrimEsm { get; }
    public string ModDir => Path.GetDirectoryName(Plugin)!;
    public string StagingDir => Path.Combine(ModDir, ".housecarl-tmp");

    public LocalizedWriteGuardFixture(Arrangement v)
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-locwrite-" + Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(_root, "game", "Data");
        var modDir = Path.Combine(_root, "mods", "ZRefMod");
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(modDir);

        var skyrimKey = new ModKey("Skyrim", ModType.Master);
        SkyrimEsm = Path.Combine(DataDir, skyrimKey.FileName.String);
        new SkyrimMod(skyrimKey, SkyrimRelease.SkyrimSE)
            .BeginWrite.ToPath(SkyrimEsm).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        Plugin = WriteLocalized(modDir, "ZRef");
        // A second plugin in the same folder whose name begins with the first's.
        if (v == Arrangement.SiblingStem) WriteLocalized(modDir, "ZRef_extra");

        var own = Path.Combine(modDir, "Strings");
        var gameStrings = Path.Combine(DataDir, "Strings");
        switch (v)
        {
            case Arrangement.LoosePartial:
                foreach (var k in new[] { "DLSTRINGS", "ILSTRINGS" }) File.Delete(Path.Combine(own, $"ZRef_French.{k}"));
                break;
            case Arrangement.LooseAndGameData:
                Directory.CreateDirectory(gameStrings);
                foreach (var p in Directory.GetFiles(own)) File.Copy(p, Path.Combine(gameStrings, Path.GetFileName(p)));
                break;
            case Arrangement.GameDataOnly:
                Directory.CreateDirectory(gameStrings);
                foreach (var p in Directory.GetFiles(own)) File.Move(p, Path.Combine(gameStrings, Path.GetFileName(p)));
                Directory.Delete(own, true);
                break;
            case Arrangement.Nowhere:
                Directory.Delete(own, true);
                break;
            case Arrangement.UnknownLanguageToken:
                // This plugin's own tables, named for language tokens Mutagen does not model.
                foreach (var q in Directory.GetFiles(own))
                    File.Move(q, Path.Combine(own, Path.GetFileName(q).Replace("_English", "_ptbr").Replace("_French", "_ptpt")));
                break;
            case Arrangement.NeighbourTablesOnly:
                // Six of a different plugin's tables, under the naming cap.
                foreach (var p in Directory.GetFiles(own))
                    File.Move(p, Path.Combine(own, Path.GetFileName(p).Replace("ZRef_", "ZOther_")));
                break;
            case Arrangement.ManyNeighbourTables:
                // Twelve of a different plugin's tables, over the naming cap of eight.
                foreach (var p in Directory.GetFiles(own))
                    File.Move(p, Path.Combine(own, Path.GetFileName(p).Replace("ZRef_", "ZOther_")));
                foreach (var lang in new[] { "German", "Italian" })
                    foreach (var kind in new[] { "STRINGS", "DLSTRINGS", "ILSTRINGS" })
                        File.WriteAllBytes(Path.Combine(own, $"ZOther_{lang}.{kind}"), new byte[] { 0 });
                break;
            case Arrangement.MalformedBsa:
                File.WriteAllBytes(Path.Combine(modDir, "ZRef.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
                break;
            case Arrangement.GameDataBsa:
                // Nothing loose anywhere and no archive beside the plugin; one unparseable archive in game Data.
                Directory.Delete(own, true);
                File.WriteAllBytes(Path.Combine(DataDir, "ZGame.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
                break;
        }
    }

    public void Dispose()
    {
        foreach (var d in _denied) { try { Undeny(d); } catch { /* the delete below reports nothing either */ } }
        try { Directory.Delete(_root, true); } catch { /* temp scratch */ }
    }

    /// <summary>Weapons, books and dialogue responses in English and French, so all three table kinds carry entries.</summary>
    string WriteLocalized(string modDir, string stem)
    {
        var key = new ModKey(stem, ModType.Plugin);
        var path = Path.Combine(modDir, key.FileName.String);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        for (int i = 0; i < Records; i++)
            m.Weapons.Add(new Weapon(new FormKey(key, (uint)(0xA02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Weap" + i,
                Name = Loc("REF NAME " + i, "FR NAME " + i),
                Description = Loc("REF DESC " + i, "FR DESC " + i),
                BasicStats = new WeaponBasicStats { Damage = (ushort)(7 + i) },
            });
        for (int i = 0; i < Records; i++)
            m.Books.Add(new Book(new FormKey(key, (uint)(0xB02 + i)), SkyrimRelease.SkyrimSE)
            {
                EditorID = stem + "Book" + i,
                Name = Loc("BOOK NAME " + i, "FR BOOK " + i),
                BookText = Loc("BOOK TEXT " + i, "FR TEXT " + i),
            });
        var topic = new DialogTopic(new FormKey(key, 0xC02), SkyrimRelease.SkyrimSE) { EditorID = stem + "Topic" };
        for (int i = 0; i < Records; i++)
        {
            var info = new DialogResponses(new FormKey(key, (uint)(0xC03 + i)), SkyrimRelease.SkyrimSE);
            info.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = Loc("LINE " + i, "FR LINE " + i) });
            topic.Responses.Add(info);
        }
        m.DialogTopics.Add(topic);
        m.ModHeader.Stats.NextFormID = (uint)(0xC03 + Records);
        WithSkyrim(sky => m.BeginWrite.ToPath(path).WithLoadOrder(new[] { sky }).NoNextFormIDProcessing().Write());
        if (!Directory.Exists(Path.Combine(modDir, "Strings")))
            throw new InvalidOperationException($"fixture: '{key.FileName}' was written localized but produced no Strings folder.");
        return path;
    }

    public static TranslatedString Loc(string en, string fr)
    {
        var ts = new TranslatedString(Language.English, en);
        ts.Set(Language.French, fr);
        return ts;
    }

    void WithSkyrim(Action<ISkyrimModGetter> body)
    {
        var sky = SkyrimMod.CreateFromBinaryOverlay(SkyrimEsm, SkyrimRelease.SkyrimSE);
        try { body(sky); }
        finally { ((IDisposable)sky).Dispose(); }
    }

    /// <summary>Drive the real <see cref="WriteEngine.WriteInPlace"/> with <paramref name="mod"/> aimed at <paramref name="outPath"/>; the refusal, or null if it wrote.</summary>
    public string? WriteInPlace(SkyrimMod mod, string outPath)
    {
        string? refusal = null;
        WithSkyrim(sky =>
        {
            try { WriteEngine.WriteInPlace(mod, new[] { sky }, outPath, DataDir); }
            catch (LocalizedTargetUnsupportedException ex) { refusal = ex.Message; }
        });
        return refusal;
    }

    /// <summary>Open the plugin as the in-place lanes do, apply <paramref name="edit"/> to its first weapon, and write it back in place.</summary>
    public string? WriteThrough(Action<Weapon> edit)
    {
        var mut = SkyrimMod.CreateFromBinary(Plugin, SkyrimRelease.SkyrimSE);
        edit(mut.Weapons.First(w => w.EditorID == "ZRefWeap0"));
        return WriteInPlace(mut, Plugin);
    }

    /// <summary>A freshly built localized mod named for <paramref name="outPath"/>'s stem, for destinations the plugin itself cannot express.</summary>
    public static SkyrimMod LocalizedModFor(string outPath)
    {
        var key = new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        mod.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE)
        { EditorID = key.Name + "Weap0", Name = Loc("EDITED 0", "FR EDITED 0") });
        return mod;
    }

    /// <summary>A plain, NON-localized plugin beside ZRef.esp.</summary>
    public string WritePlain(string fileName)
    {
        var path = Path.Combine(ModDir, fileName);
        var key = new ModKey(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE) { EditorID = key.Name + "Weap", Name = "PLAIN" });
        WithSkyrim(sky => m.BeginWrite.ToPath(path).WithLoadOrder(new[] { sky }).Write());
        return path;
    }

    /// <summary>SHA-256 of the plugin and each of its own tables, keyed by name.</summary>
    public Dictionary<string, string> Snapshot()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Plugin)) d[Path.GetFileName(Plugin)] = Hash(Plugin);
        foreach (var p in LocalizedStrings.TableFilesIn(Path.Combine(ModDir, "Strings"), "ZRef"))
            d["Strings/" + Path.GetFileName(p)] = Hash(p);
        return d;
    }

    static string Hash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>ZRefWeap0's name in <paramref name="lang"/>, read back through the resolver's overlay.</summary>
    public string WeaponName(Language lang)
    {
        var ov = LoadOrderResolver.OpenOverlay(Plugin, DataDir);
        try
        {
            var n = ov.Weapons.First(w => w.EditorID == "ZRefWeap0").Name;
            return n is null ? "" : n.TryLookup(lang, out var v) ? v : n.String ?? "";
        }
        finally { ((IDisposable)ov).Dispose(); }
    }

    /// <summary>Deny LISTING (not traverse) on <paramref name="dir"/> until disposed; false when the deny did not bite on this host.</summary>
    public bool DenyListing(string dir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var di = new DirectoryInfo(dir);
        var sec = di.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory,
                                                   AccessControlType.Deny));
        di.SetAccessControl(sec);
        _denied.Add(dir);
        if (!Directory.Exists(dir)) return false;
        try { Directory.EnumerateFiles(dir).ToList(); }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return true; }
        return false;
    }

    /// <summary>Lift a deny <see cref="DenyListing"/> added.</summary>
    public void LiftDeny(string dir)
    {
        Undeny(dir);
        _denied.Remove(dir);
    }

    static void Undeny(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var di = new DirectoryInfo(dir);
        var sec = di.GetAccessControl();
        sec.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory,
                                                         AccessControlType.Deny));
        di.SetAccessControl(sec);
    }
}

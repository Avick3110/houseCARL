using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A world of ABSTRACT-GROUP records only: one GLOB group holding all three of Mutagen's concrete arms
/// (GlobalShort / GlobalInt / GlobalFloat) and one GMST group holding two of its own. Mutagen models these groups as
/// an abstract base with concrete subclasses, so a type filter naming an arm is the only case where the resolved
/// getter type is narrower than the GRUP a typed enumeration seeks.
///
/// <para>Its own world: the shared records and bulk worlds carry no globals, and adding any would move counts their
/// tests assert.</para>
/// </summary>
public sealed class TypeArmWorld : IDisposable
{
    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The two GlobalShort records — what a <c>type='GlobalShort'</c> filter must return, and all it must
    /// return.</summary>
    public const string Short1 = "HcArmShortA";
    public const string Short2 = "HcArmShortB";
    public const string Int1 = "HcArmInt";
    public const string Float1 = "HcArmFloat";

    /// <summary>The GMST arms — the same shape on a second abstract group, so the fix is known not to be one type's
    /// special case.</summary>
    public const string GmstInt = "HcArmGmstInt";
    public const string GmstFloat = "HcArmGmstFloat";

    /// <summary>A plugin FILE on disk that is NOT in the active order — the errors sweep's off-order lane, its own
    /// record stream. Named for a base-game master because <c>BaseMastersSwept</c> is what exposes that lane's
    /// "examined" set to a test: a swept off-order base master is listed there, and one whose every record a scope
    /// filtered out is not. It holds a GlobalFloat and a GlobalInt and deliberately NO GlobalShort, so a
    /// <c>type='GlobalShort'</c> sweep of it must examine nothing.</summary>
    public const string OffOrderName = "Update.esm";
    public const string OffOrderFloat = "HcArmOffFloat";
    public const string OffOrderInt = "HcArmOffInt";

    readonly string _priorCorpusPath;

    public TypeArmWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;

        Root = Path.Combine(Path.GetTempPath(), "hc-typearm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));

        var masterKey = new ModKey("HcArmMaster", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        master.Globals.Add(new GlobalShort(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = Short1, Data = 1 });
        master.Globals.Add(new GlobalShort(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = Short2, Data = 2 });
        master.Globals.Add(new GlobalInt(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = Int1, Data = 3 });
        master.Globals.Add(new GlobalFloat(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = Float1, Data = 4.5f });

        master.GameSettings.Add(new GameSettingInt(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = GmstInt, Data = 7 });
        master.GameSettings.Add(new GameSettingFloat(master.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = GmstFloat, Data = 8.5f });

        var instance = Path.Combine(Root, "inst");
        var modDir = Path.Combine(instance, "mods", "ArmMasterMod");
        Directory.CreateDirectory(modDir);
        master.BeginWrite.ToPath(Path.Combine(modDir, MasterName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        // The off-order file: written into an ENABLED mod folder but left out of plugins.txt and loadorder.txt, which
        // is what the errors sweep locates on disk and sweeps as a file.
        var offKey = new ModKey(Path.GetFileNameWithoutExtension(OffOrderName), ModType.Master);
        var off = new SkyrimMod(offKey, SkyrimRelease.SkyrimSE);
        off.Globals.Add(new GlobalFloat(off.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = OffOrderFloat, Data = 1.5f });
        off.Globals.Add(new GlobalInt(off.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = OffOrderInt, Data = 6 });
        var offDir = Path.Combine(instance, "mods", "ArmOffOrderMod");
        Directory.CreateDirectory(offDir);
        off.BeginWrite.ToPath(Path.Combine(offDir, OffOrderName)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
        var prof = Path.Combine(instance, "profiles", "Default");
        Directory.CreateDirectory(prof);
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+ArmOffOrderMod\r\n+ArmMasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TypeArmFixture : IDisposable
{
    public TypeArmWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>Its own collection, for the reason the records and bulk ones have theirs:
/// <c>CorpusRulebook.CorpusPath</c> is a process-global and only one world may own it at a time.</summary>
[CollectionDefinition("type-arms")]
public sealed class TypeArmCollection : ICollectionFixture<TypeArmFixture> { }

using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The read side's strings-redirect gate: does the plugin's own folder carry a strings source FOR THIS
/// PLUGIN? The gate used to answer yes to any <c>.bsa</c> at all and to any <c>Strings\</c> folder at all, so an
/// asset-only archive — meshes and textures, the common case — suppressed the redirect for a localized plugin whose
/// tables live in game-Data, and every value it read came back blank (#369).</summary>
[Trait("tier", "unit")]
public sealed class LocalizedStringsSourceTests : IDisposable
{
    readonly string _root;

    public LocalizedStringsSourceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-strings-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    /// <summary>A fresh mod folder holding an empty file standing in for the plugin — the gate reads the folder
    /// around the path and the file's name, never the plugin's bytes.</summary>
    string Folder(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var plugin = Path.Combine(dir, "ZRef.esp");
        File.WriteAllBytes(plugin, Array.Empty<byte>());
        return plugin;
    }

    static void WriteArchive(string pluginPath, string archiveName, (string Folder, (string Name, byte[] Data)[] Files)[] contents)
        => File.WriteAllBytes(
            Path.Combine(Path.GetDirectoryName(pluginPath)!, archiveName),
            BsaBuilder.Build(105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames, contents));

    static void WriteTable(string pluginPath, string tableName)
    {
        var dir = Path.Combine(Path.GetDirectoryName(pluginPath)!, "Strings");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, tableName), new byte[] { 0 });
    }

    /// <summary>#369: an archive carrying only meshes is not a strings source, so the gate must let the redirect
    /// through. This is the shape that blanked a whole population of localized plugins.</summary>
    [Fact]
    public void AnAssetOnlyArchiveIsNotAStringsSource()
    {
        var plugin = Folder("asset-only");
        WriteArchive(plugin, "ZRef - Meshes.bsa", new[]
        {
            ("meshes", new[] { ("a.nif", BsaBuilder.Bytes("NIF-a", 64)) }),
        });
        Assert.False(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    /// <summary>The other half of the same question: an archive that really does embed this plugin's tables keeps
    /// the folder-adjacent open, so a mod shipping its strings in its own .bsa is never redirected away from them.</summary>
    [Fact]
    public void AnArchiveEmbeddingThisPluginsTablesIsAStringsSource()
    {
        var plugin = Folder("bsa-strings");
        WriteArchive(plugin, "ZRef.bsa", new[]
        {
            ("strings", new[] { ("zref_english.strings", BsaBuilder.Bytes("STR", 32)) }),
        });
        Assert.True(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    /// <summary>An archive embedding a NEIGHBOUR's tables answers for that neighbour, not for this plugin — two
    /// plugins share a mod folder, and a prefix-loose match would hand one plugin the other's source.</summary>
    [Fact]
    public void AnArchiveEmbeddingANeighboursTablesIsNotThisPluginsSource()
    {
        var plugin = Folder("bsa-neighbour");
        WriteArchive(plugin, "Other.bsa", new[]
        {
            ("strings", new[] { ("zref_shrubs_english.strings", BsaBuilder.Bytes("STR", 32)) }),
        });
        Assert.False(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    /// <summary>A Strings folder is shared by every plugin beside it, so its mere existence says nothing: one
    /// holding only a neighbour's tables is not this plugin's source either.</summary>
    [Fact]
    public void ALooseFolderHoldingOnlyANeighboursTablesIsNotThisPluginsSource()
    {
        var plugin = Folder("loose-neighbour");
        WriteTable(plugin, "ZRef_shrubs_English.STRINGS");
        Assert.False(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    [Fact]
    public void ALooseTableForThisPluginIsAStringsSource()
    {
        var plugin = Folder("loose-own");
        WriteTable(plugin, "ZRef_English.STRINGS");
        Assert.True(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    /// <summary>An archive houseCARL cannot parse may hold the tables, so the gate keeps the unchanged
    /// folder-adjacent open rather than redirecting past a source it could not rule out.</summary>
    [Fact]
    public void AnUnreadableArchiveKeepsTheUnchangedOpen()
    {
        var plugin = Folder("bsa-malformed");
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(plugin)!, "ZRef.bsa"), new byte[] { 0x42, 0x53, 0x41, 0x00 });
        Assert.True(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
    }

    /// <summary>A plain mod folder with nothing beside the plugin: the redirect is the whole point of the gate.</summary>
    [Fact]
    public void AnEmptyFolderCarriesNoStringsSource()
        => Assert.False(LocalizedStrings.OwnFolderCarriesStringsFor(Folder("bare")));

    /// <summary>The mod folder itself will not list, so the archive look tells us nothing about what is beside the
    /// plugin — the same fact as an unlistable <c>Strings\</c> folder, and it takes the same answer: keep the
    /// unchanged folder-adjacent open. Swallowed as "no archives", the gate answers false and redirects a plugin
    /// whose tables may be sitting right there, which is the failure #369 was about.</summary>
    [Fact]
    public void AModFolderThatCannotBeListedKeepsTheUnchangedOpen()
    {
        if (!OperatingSystem.IsWindows()) return;   // the deny ACE that makes a folder unlistable is Windows-only
        var plugin = Folder("folder-unlistable");
        var dir = new DirectoryInfo(Path.GetDirectoryName(plugin)!);
        var acl = dir.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory, AccessControlType.Deny);
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            // The fixture is VERIFIED to bite: without this the arm passes on a folder that lists fine, which is
            // exactly the shape it exists to rule out.
            Assert.ThrowsAny<Exception>(() => Directory.GetFiles(dir.FullName, "*.bsa"));
            Assert.True(LocalizedStrings.OwnFolderCarriesStringsFor(plugin));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            dir.SetAccessControl(acl);
        }
    }
}

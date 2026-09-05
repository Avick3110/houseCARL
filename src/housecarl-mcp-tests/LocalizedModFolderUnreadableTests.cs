using System.Security.AccessControl;
using System.Security.Principal;
using HousecarlGenerator;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A localized plugin in a MOD FOLDER that will not list, with its tables in a <c>.bsa</c> sitting in that
/// same folder. The classifier read the folder's third answer and then discarded it, so this fell through to
/// <see cref="LocalizedShape.Nowhere"/> — whose refusal tells the modder there is "no archive beside it" and sends
/// them off to find tables that are right there, while the read side's gate answers that a source may well exist.
///
/// <para>Windows is what makes it reachable: listing a directory is a separate right from traversing it, so the
/// plugin still opens and a <c>Strings\</c> subfolder still enumerates while <c>GetFiles(folder, "*.bsa")</c>
/// throws.</para></summary>
[Trait("tier", "unit")]
public sealed class LocalizedModFolderUnreadableTests : IDisposable
{
    readonly string _root;
    readonly string _modDir;
    readonly string _dataDir;
    readonly string _plugin;

    public LocalizedModFolderUnreadableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-modfolder-" + Guid.NewGuid().ToString("N"));
        _modDir = Path.Combine(_root, "mods", "ZRefMod");
        _dataDir = Path.Combine(_root, "game", "Data");
        Directory.CreateDirectory(_modDir);
        Directory.CreateDirectory(_dataDir);
        _plugin = WriteLocalized(_modDir, "ZRef");
        // The tables, in an archive in the mod folder — a real one, so the classifier finds it while the folder lists.
        File.WriteAllBytes(Path.Combine(_modDir, "ZRef.bsa"), BsaBuilder.Build(
            105, BsaBuilder.HasFolderNames | BsaBuilder.HasFileNames,
            new[] { ("strings", new[] { ("zref_english.strings", BsaBuilder.Bytes("STR", 32)) }) }));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* temp scratch */ } }

    /// <summary>A localized plugin whose text is written into its own <c>Strings\</c> tables, which the fixture then
    /// deletes: the archive beside it is the only source left.</summary>
    static string WriteLocalized(string modDir, string stem)
    {
        var key = new ModKey(stem, ModType.Plugin);
        var path = Path.Combine(modDir, key.FileName.String);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE) { UsingLocalization = true };
        var name = new TranslatedString(Language.English, "REF NAME");
        m.Weapons.Add(new Weapon(new FormKey(key, 0xA02), SkyrimRelease.SkyrimSE) { EditorID = stem + "Weap", Name = name });
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var own = Path.Combine(modDir, "Strings");
        if (!Directory.Exists(own))
            throw new InvalidOperationException($"fixture: '{key.FileName}' was written localized but produced no Strings folder.");
        Directory.Delete(own, recursive: true);
        return path;
    }

    /// <summary>Deny LISTING (not traverse) on the mod folder, run <paramref name="body"/>, and lift it again.
    /// Returns false when the ACE did not bite on this host, so the caller can say so rather than passing.</summary>
    bool WithDeniedListing(Action body)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var dir = new DirectoryInfo(_modDir);
        var acl = dir.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().Name, FileSystemRights.ListDirectory, AccessControlType.Deny);
        acl.AddAccessRule(deny);
        dir.SetAccessControl(acl);
        try
        {
            // VERIFIED to bite, and verified to leave the plugin readable — a deny that took the whole folder down
            // would be measuring a different state.
            try { Directory.GetFiles(_modDir, "*.bsa"); return false; }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            body();
            return true;
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            dir.SetAccessControl(acl);
        }
    }

    [Fact]
    public void AnUnlistableModFolderIsNotAFolderFoundEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;   // the deny ACE that makes a folder unlistable is Windows-only

        Assert.Equal(LocalizedShape.BsaEmbedded, LocalizedStrings.Assess(_plugin, _dataDir).Shape);

        var bit = WithDeniedListing(() =>
        {
            var a = LocalizedStrings.Assess(_plugin, _dataDir);
            Assert.Equal(LocalizedShape.ModFolderUnreadable, a.Shape);

            // The refusal says what happened, and asserts none of the absences Nowhere's sentence asserts.
            var msg = LocalizedStrings.RefusalFor(_plugin, "ZRef.esp", _dataDir);
            Assert.NotNull(msg);
            Assert.Contains("could not read the folder the plugin sits in", msg);
            Assert.DoesNotContain("no archive beside it", msg);
            Assert.DoesNotContain("cannot find its text", msg);
            Assert.DoesNotContain("no .STRINGS files beside it", msg);

            // The remedy points at the folder that is actually stuck, named — not at filling a Strings folder the
            // modder does not have, and not at the Strings folder, which is not the one that would not read.
            var refusal = LoadOrderService.UnresolvableStringsRefusal("ZRef.esp", a, "merge");
            Assert.Contains("the folder 'ZRef.esp' sits in", refusal);
            Assert.Contains("fix its permissions", refusal);
            Assert.DoesNotContain("place them in a Strings folder beside the plugin", refusal);

            // THE TWO SIDES AGREE: the read gate keeps the folder-adjacent open because a source may be in there, and
            // the classifier must not answer with the shape that says one is not.
            Assert.True(LocalizedStrings.OwnFolderCarriesStringsFor(_plugin));
            Assert.NotEqual(LocalizedShape.Nowhere, a.Shape);
        });
        Assert.True(bit, "the deny-listing ACE did not take on this host — the fixture cannot be built");

        // The other direction: with the deny lifted the archive is found again, so the assertions above cannot pass
        // by the classifier answering ModFolderUnreadable for everything.
        Assert.Equal(LocalizedShape.BsaEmbedded, LocalizedStrings.Assess(_plugin, _dataDir).Shape);
    }
}

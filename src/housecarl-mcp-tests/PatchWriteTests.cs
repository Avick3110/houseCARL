using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The patch write itself — <c>WritePatch</c> and <c>WriteInPlace</c> — on mods built in memory: a localized extend
/// target is refused (T08), the base-game masters are force-included (T09), a UTF-8 lane carries into a new file
/// (T10), and the staging folder never outlives a write (T11). Each test writes into its own temp folder.
/// </summary>
[Trait("tier", "unit")]
public sealed class PatchWriteTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-patch-write-" + Guid.NewGuid().ToString("N"));

    public PatchWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    static SkyrimMod Mod(string name, ModType type = ModType.Plugin) =>
        new(new ModKey(name, type), SkyrimRelease.SkyrimSE);

    string StagingDir => Path.Combine(_dir, ".housecarl-tmp");

    // ---- T08: extend into a localized plugin ----

    [Fact]
    public void AnExtendIntoALocalizedPluginIsSentToAFreshPatch()
    {
        var patch = Mod("HcLocalizedPatch");
        patch.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Localized;

        var ex = Assert.Throws<LocalizedTargetUnsupportedException>(() =>
            WriteEngine.WritePatch(patch, Array.Empty<ISkyrimModGetter>(), Path.Combine(_dir, "HcLocalizedPatch.esp")));
        Assert.Contains("into=", ex.Message);
    }

    // ---- T09: baseline masters ----

    /// <summary>The patch overrides a record of a third plugin only; Skyrim.esm and Update.esm are still masters.</summary>
    [Fact]
    public void APatchHeaderCarriesSkyrimAndUpdateWhenNoRecordReferencesThem()
    {
        var skyrim = Mod("Skyrim", ModType.Master);
        var update = Mod("Update", ModType.Master);
        var other = Mod("HcBaselineOther");
        var kw = other.Keywords.AddNew("HcBaselineKw");
        var patch = Mod("HcBaselinePatch");
        patch.Keywords.GetOrAddAsOverride(kw);

        var path = Path.Combine(_dir, "HcBaselinePatch.esp");
        WriteEngine.WritePatch(patch, new ISkyrimModGetter[] { skyrim, update, other }, path);

        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        Assert.Contains("Skyrim.esm", masters);
        Assert.Contains("Update.esm", masters);
    }

    // ---- T10: encoding lane ----

    /// <summary>A plugin read as UTF-8 contributes a record to a new patch; the patch writes its accented text as
    /// UTF-8 bytes, not as the Windows-1252 bytes the language default would also be able to spell.</summary>
    [Fact]
    public void ANewPatchFromAUtf8PluginWritesNonAsciiTextAsUtf8()
    {
        const string name = "Épée d'acier";
        var srcName = "HcUtf8Src" + Guid.NewGuid().ToString("N")[..8];
        var src = Mod(srcName);
        var w = src.Weapons.AddNew("HcUtf8SrcWeap");
        w.Name = name;
        var srcPath = Path.Combine(_dir, srcName + ".esp");
        src.BeginWrite.ToPath(srcPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>())
            .WithEmbeddedEncodings(PluginTextEncoding.Utf8Bundle).Write();

        var patchName = "HcUtf8Patch" + Guid.NewGuid().ToString("N")[..8];
        var outPath = Path.Combine(_dir, "out", patchName + ".esp");
        using (var read = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(srcPath)))
        {
            var weapon = read.Weapons.Single();
            Assert.Equal(name, weapon.Name?.String);   // the read decodes the name, which records the plugin's lane
            var patch = Mod(patchName);
            patch.Weapons.GetOrAddAsOverride(weapon);
            WriteEngine.WritePatch(patch, new ISkyrimModGetter[] { read }, outPath);
        }

        var bytes = File.ReadAllBytes(outPath);
        Assert.True(Contains(bytes, Encoding.UTF8.GetBytes(name)), "the name is not in the patch as UTF-8 bytes");
    }

    static bool Contains(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    // ---- T11: the staging folder ----

    [Fact]
    public void ASuccessfulPatchWriteLeavesNoStagingFolder()
    {
        var patch = Mod("HcStagePatch");
        patch.Keywords.AddNew("HcStageKw");
        WriteEngine.WritePatch(patch, Array.Empty<ISkyrimModGetter>(), Path.Combine(_dir, "HcStagePatch.esp"));

        Assert.True(File.Exists(Path.Combine(_dir, "HcStagePatch.esp")));
        Assert.False(Directory.Exists(StagingDir));
    }

    /// <summary>A condition with no data arm fails at serialize; nothing staged survives the refusal.</summary>
    [Fact]
    public void AFailedPatchSerializeLeavesNoStagingFolder()
    {
        var patch = Mod("HcStageFailPatch");
        var mgef = patch.MagicEffects.AddNew("HcStageFailEffect");
        mgef.Conditions.Add(new ConditionFloat { Data = null! });

        Assert.ThrowsAny<Exception>(() =>
            WriteEngine.WritePatch(patch, Array.Empty<ISkyrimModGetter>(), Path.Combine(_dir, "HcStageFailPatch.esp")));
        Assert.False(Directory.Exists(StagingDir));
    }

    /// <summary>The target is held open by another program, so the swap fails after the patch is staged; the staged
    /// file is removed with its folder and the target is untouched.</summary>
    [Fact]
    public void AFailedCommitLeavesNoStagingFolder()
    {
        var target = Path.Combine(_dir, "HcStageLockedPatch.esp");
        File.WriteAllText(target, "held");
        var patch = Mod("HcStageLockedPatch");
        patch.Keywords.AddNew("HcStageLockedKw");

        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => WriteEngine.WritePatch(patch, Array.Empty<ISkyrimModGetter>(), target));

        Assert.False(Directory.Exists(StagingDir));
        Assert.Equal("held", File.ReadAllText(target));
    }

    [Fact]
    public void ASuccessfulInPlaceWriteLeavesNoStagingFolder()
    {
        var target = Mod("HcStageInPlace");
        target.Keywords.AddNew("HcStageInPlaceKw");
        WriteEngine.WriteInPlace(target, Array.Empty<ISkyrimModGetter>(), Path.Combine(_dir, "HcStageInPlace.esp"), null);

        Assert.True(File.Exists(Path.Combine(_dir, "HcStageInPlace.esp")));
        Assert.False(Directory.Exists(StagingDir));
    }

    /// <summary>A name the target's own (Windows-1252) encoding cannot spell stops the in-place write mid-serialize;
    /// nothing staged survives the refusal.</summary>
    [Fact]
    public void AFailedInPlaceSerializeLeavesNoStagingFolder()
    {
        var name = "HcStageInPlaceFail" + Guid.NewGuid().ToString("N")[..8];
        var target = Mod(name);
        var w = target.Weapons.AddNew("HcStageInPlaceFailWeap");
        w.Name = "炎の剣";

        Assert.ThrowsAny<Exception>(() =>
            WriteEngine.WriteInPlace(target, Array.Empty<ISkyrimModGetter>(), Path.Combine(_dir, name + ".esp"), null));
        Assert.False(Directory.Exists(StagingDir));
    }
}

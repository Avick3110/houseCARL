using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The result-script check on a created dialogue line: a script adapter that binds nothing, a fragment file
/// with no fragment, a bound fragment with and without its .pex, an attached script, a namespaced script's subfolder, a
/// line with no script, and a check that cannot read the patch. Migrated from the nested-create-guard probe (SCRIPT arms).</summary>
[Trait("tier", "integration")]
public sealed class CreatedLineScriptCheckTests : IDisposable
{
    readonly WritePathRig _rig = new();

    /// <summary>A plugin with one topic and one INFO configured by <paramref name="configure"/>.</summary>
    (string Path, FormKey Info) Fixture(string name, Action<DialogResponses> configure)
    {
        var mod = new SkyrimMod(new ModKey(name, ModType.Plugin), SkyrimRelease.SkyrimSE);
        var topic = mod.DialogTopics.AddNew(); topic.EditorID = name + "Topic";
        var info = new DialogResponses(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = name + "Info" };
        configure(info);
        topic.Responses.Add(info);
        return (_rig.Write(mod, name), info.FormKey);
    }

    string DataRoot(params string[] files)
    {
        var dir = Path.Combine(_rig.Root, "data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var rel in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[] { 0, 1, 2 });
        }
        return dir;
    }

    static ScriptBindingReport Check(string patch, string name, FormKey info, string dataDir)
    {
        using var assets = AssetResolver.Build("", "", dataDir, Array.Empty<string>(), Array.Empty<ActiveArchive>());
        var created = new[] { new WritePatchBuilder.CreatedRecord(info, "DialogResponses", name + "Info", Array.Empty<WritePatchBuilder.OpResult>()) };
        return DialogueScriptCheck.Run(patch, created, assets);
    }

    static DialogResponsesAdapter Fragment(string cls) => new()
    {
        ScriptFragments = new ScriptFragments { FileName = cls, OnEnd = new ScriptFragment { ScriptName = cls, FragmentName = "Fragment_0" } },
    };

    static DialogResponsesAdapter Attached(string cls)
    {
        var a = new DialogResponsesAdapter();
        a.Scripts.Add(new ScriptEntry { Name = cls });
        return a;
    }

    // SCRIPT-INCOMPLETE: an empty VMAD is BindingIncomplete.
    [Fact]
    public void AScriptAdapterThatBindsNothingIsIncomplete()
    {
        var (path, info) = Fixture("HcScIncomplete", i => i.VirtualMachineAdapter = new DialogResponsesAdapter());
        var find = Assert.Single(Check(path, "HcScIncomplete", info, DataRoot()).Findings);
        Assert.Equal(ScriptBindingStatus.BindingIncomplete, find.Status);
        Assert.Equal(info, find.Info);
    }

    // SCRIPT-NOFRAG: a FileName with no Begin/End fragment is BindingIncomplete even with its .pex on disk.
    [Fact]
    public void AFragmentFileWithNoFragmentIsIncompleteEvenWhenCompiled()
    {
        var (path, info) = Fixture("HcScNoFrag", i => i.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments { FileName = "HcScNoFragClass" } });
        var find = Assert.Single(Check(path, "HcScNoFrag", info, DataRoot(@"Scripts\HcScNoFragClass.pex")).Findings);
        Assert.Equal(ScriptBindingStatus.BindingIncomplete, find.Status);
    }

    // SCRIPT-NOTCOMPILED: a bound fragment with no Scripts\<class>.pex is ScriptNotCompiled naming that path.
    [Fact]
    public void ABoundFragmentWithNoPexIsNotCompiled()
    {
        var (path, info) = Fixture("HcScNotComp", i => i.VirtualMachineAdapter = Fragment("HcScNotCompClass"));
        var find = Assert.Single(Check(path, "HcScNotComp", info, DataRoot()).Findings);
        Assert.Equal(ScriptBindingStatus.ScriptNotCompiled, find.Status);
        Assert.Equal(@"Scripts\HcScNotCompClass.pex", Assert.Single(find.MissingPex));
    }

    // SCRIPT-BOUND: a bound fragment with its .pex is BoundAndCompiled.
    [Fact]
    public void ABoundFragmentWithItsPexIsBoundAndCompiled()
    {
        var (path, info) = Fixture("HcScBound", i => i.VirtualMachineAdapter = Fragment("HcScBoundClass"));
        var find = Assert.Single(Check(path, "HcScBound", info, DataRoot(@"Scripts\HcScBoundClass.pex")).Findings);
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, find.Status);
        Assert.Empty(find.MissingPex);
    }

    // SCRIPT-ATTACHED: an attached Scripts[] class with its .pex is BoundAndCompiled and named.
    [Fact]
    public void AnAttachedScriptWithItsPexIsBoundAndCompiled()
    {
        var (path, info) = Fixture("HcScAttached", i => i.VirtualMachineAdapter = Attached("HcScAttachedClass"));
        var find = Assert.Single(Check(path, "HcScAttached", info, DataRoot(@"Scripts\HcScAttachedClass.pex")).Findings);
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, find.Status);
        Assert.Contains("HcScAttachedClass", find.Scripts);
    }

    // SCRIPT-NAMESPACED: Namespace:Script resolves to Scripts\Namespace\Script.pex.
    [Fact]
    public void ANamespacedScriptIsLookedForInItsSubfolder()
    {
        var (path, info) = Fixture("HcScNs", i => i.VirtualMachineAdapter = Attached("HcScNsSpace:HcScNsClass"));
        var find = Assert.Single(Check(path, "HcScNs", info, DataRoot(@"Scripts\HcScNsSpace\HcScNsClass.pex")).Findings);
        Assert.Equal(ScriptBindingStatus.BoundAndCompiled, find.Status);
        Assert.Empty(find.MissingPex);
    }

    // SCRIPT-NOVMAD: a line with no VMAD yields no finding.
    [Fact]
    public void ALineWithNoScriptIsNotChecked()
    {
        var (path, info) = Fixture("HcScNoVmad", _ => { });
        Assert.True(Check(path, "HcScNoVmad", info, DataRoot()).IsEmpty);
    }

    // SCRIPT-CHECKERROR: a corrupt patch sets CheckError, does not throw, reports no findings.
    [Fact]
    public void AScriptCheckThatCannotReadThePatchReportsTheError()
    {
        var corrupt = Path.Combine(_rig.Root, "HcScCorrupt.esp");
        File.WriteAllText(corrupt, "this is not a valid Skyrim plugin");
        var report = Check(corrupt, "HcScCk", new FormKey(new ModKey("HcScCk", ModType.Plugin), 0x800), DataRoot());
        Assert.NotNull(report.CheckError);
        Assert.Empty(report.Findings);
    }

    public void Dispose() => _rig.Dispose();
}

using HousecarlCore;
using HousecarlMcp;
using Xunit;
using S = HousecarlMcpTests.NifSourceSoleWorld;

namespace HousecarlMcpTests;

/// <summary>#1061: compile, decompile, copy, nif_set and place refuse <c>patch=</c> beside <c>into=</c> before any
/// work, as apply, create, forward and write_seq do, rather than letting <c>into=</c> win and dropping the name.
/// Copy also reads a blank <c>patch=</c> as no name, so the <c>new_editorid=</c> default applies.</summary>
[Collection("scripts")]
[Trait("tier", "integration")]
public sealed class PatchIntoLaneRefusalTests
{
    readonly ScriptsWorld W;
    public PatchIntoLaneRefusalTests(ScriptsFixture f) => W = f.W;

    static string[] Folders(string mods) => Directory.GetDirectories(mods).OrderBy(d => d, StringComparer.Ordinal).ToArray();

    [Fact]
    public void CompileRefusesPatchWithInto()
    {
        var before = Folders(W.ModsDir);

        var store = new UserConfigStore(Path.Combine(W.Root, "hc-1061-" + Guid.NewGuid().ToString("N") + ".json"));

        var r = CompileTools.CompileScript(W.Svc, new ToolPathResolver(store), store,
                                           Path.Combine(W.ScriptsDir, ScriptsWorld.BaseScript + ".psc"),
                                           patch: "HcLaneA", into: "houseCARL_Scripts");

        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
        Assert.Equal(before, Folders(W.ModsDir));
    }

    [Fact]
    public void CompileWithOutPathIgnoresPatchAndIntoInsteadOfRefusing()
    {
        // A stub compiler gets the call past the tool prompt; a relative out_path= then stops it at the output folder,
        // before anything runs, where the ignored-lane note rides the refusal.
        var own = Path.Combine(W.Root, "hc-1061-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(own);
        var stub = Path.Combine(own, "PapyrusCompiler.exe");
        File.WriteAllText(stub, "stub");
        var psc = Path.Combine(own, "HcLaneScript.psc");
        File.WriteAllText(psc, "ScriptName HcLaneScript\n");
        var store = new UserConfigStore(Path.Combine(own, "user.json"));
        var bridge = new ToolPathResolver(store);
        Assert.True(bridge.Save(ToolDependency.PapyrusCompiler, stub).ok);

        var r = CompileTools.CompileScript(W.Svc, bridge, store, psc,
                                           auto_imports: false, patch: "HcLaneA", into: "houseCARL_Scripts",
                                           out_path: "hc-1061-relative");

        Assert.DoesNotContain("exclusive", r);
        Assert.Contains("out_path= was given, so patch=/into= are ignored", r);
    }

    [Fact]
    public void DecompileWithOutPathIgnoresPatchAndIntoInsteadOfRefusing()
    {
        var dest = Path.Combine(W.Root, "hc-1061-" + Guid.NewGuid().ToString("N"));

        var r = DecompileTools.DecompileScript(W.Svc, Path.Combine(W.ScriptsDir, ScriptsWorld.BaseScript + ".pex"),
                                               patch: "HcLaneA", into: "houseCARL_Scripts", out_path: dest);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("out_path= was given, so patch=/into= are ignored", r);
        Assert.True(File.Exists(Path.Combine(dest, ScriptsWorld.BaseScript + ".psc")), r);
    }

    [Fact]
    public void DecompileRefusesPatchWithInto()
    {
        var before = Folders(W.ModsDir);

        var r = DecompileTools.DecompileScript(W.Svc, Path.Combine(W.ScriptsDir, ScriptsWorld.BaseScript + ".pex"),
                                               patch: "HcLaneA", into: "houseCARL_Scripts");

        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
        Assert.Equal(before, Folders(W.ModsDir));
    }

    [Fact]
    public void CopyRefusesPatchWithInto()
    {
        using var w = new TwoDisabledDonorsWorld();
        var before = Folders(w.ModsDir);

        var r = CopyTools.Copy(w.Svc, w.Fid(w.DonorNpc), new[] { "Donor.esp" }, new[] { "HeadParts" },
                               new[] { "Race:refuse" }, null, "HcLaneClone", "HcLaneA", "HcLaneB.esp");

        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
        Assert.Equal(before, Folders(w.ModsDir));
    }

    [Fact]
    public void CopyWithABlankPatchNamesThePatchAfterNewEditorid()
    {
        using var w = new TwoDisabledDonorsWorld();

        var r = CopyTools.Copy(w.Svc, w.Fid(w.DonorNpc), new[] { "Donor.esp" }, new[] { "HeadParts" },
                               new[] { "Race:refuse" }, null, "HcTrimClone", "  ", null);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Single(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - HcTrimClone"));
    }

    [Fact]
    public void NifSetRefusesPatchWithInto()
    {
        using var own = new NifSourceSoleWorld();
        var mods = Path.Combine(own.Root, "inst", "mods");
        var before = Folders(mods);

        var r = NifTools.NifSet(own.Svc, mesh_path: S.SoleRel, op: "set_flags", target: "GuardShape",
                                flags: "0x800000E", source_provider: S.SoleMod, patch: "HcLaneA", into: "HcLaneB");

        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
        Assert.Equal(before, Folders(mods));
    }

    [Fact]
    public void PlaceRefusesPatchWithInto()
    {
        using var own = new PlaceSpecWorld();
        var before = Folders(own.P.Mods);

        var r = PlaceTools.Place(own.Svc, new[] { new PlaceTarget { Formid = PlaceInstance.FacegenFormId } },
                                 patch: "HcLaneA", into: "HcLaneB.esp");

        Assert.StartsWith("error:", r);
        Assert.Contains("exclusive", r);
        Assert.Equal(before, Folders(own.P.Mods));
    }
}

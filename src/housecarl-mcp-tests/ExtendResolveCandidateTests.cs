using System.Text.RegularExpressions;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ExtendResolveRig;

namespace HousecarlMcpTests;

/// <summary>The owned-patch candidates an into= refusal offers: each spelling printed resolves to the row it was
/// printed for, the near-stem comes first, three is the cap and the rest are counted, a pluginless folder is offered
/// on the rider lane only, unreachable patches are counted rather than denied, a scan that failed says so, and an
/// install owning nothing says how a patch gets made. Migrated from the extend-resolve-guard probe (arms 9c to 9g).</summary>
[Trait("tier", "integration")]
public sealed class ExtendResolveCandidateTests
{
    static int Dropped(string s) => int.Parse(Regex.Match(s, @"\(\+(\d+) more\)").Groups[1].Value);

    /// <summary>The probe's inventory at its arm 9c: two owned folders carrying SeedA.esp, an owned folder literally
    /// named "SeedA" holding Solo.esp, and an owned folder holding Alpha.esp and Beta.esp.</summary>
    static void Inventory(ExtendResolveRig w)
    {
        var renamed = w.SeedRenamed();
        var dup = w.MarkOwned("houseCARL - DupHome", "SeedA.esp");
        File.Copy(Path.Combine(renamed, "SeedA.esp"), Path.Combine(dup, "SeedA.esp"));
        w.OwnedWithPlugins("SeedA", "Solo");
        w.OwnedWithPlugins("houseCARL - TwoEsp", "Alpha", "Beta");
    }

    // the refusal names candidates in one sentence (#380); a folder whose own name resolves elsewhere is named by its
    // PLUGIN instead; following into="Solo.esp" extends THAT patch
    [Fact]
    public void AFolderWhoseNameResolvesElsewhereIsOfferedByItsPluginAndThatSpellingResolves()
    {
        using var w = new ExtendResolveRig();
        Inventory(w);

        var list = w.Into("Soloo", w.Wgt(1)).Error ?? "";
        Assert.Contains("; try into=\"", list);
        Assert.True(OneSentence(list), list);
        Assert.DoesNotContain("into=\"SeedA\"", list);
        Assert.Contains("into=\"Solo.esp\"", list);

        var followed = w.Into("Solo.esp", w.Wgt(6));
        Assert.True(followed.Success, followed.Error);
        Assert.Equal(("SeedA", "Solo.esp"), Tail(followed.OutputPath));
    }

    // a folder holding two plugins is named by a plugin, not by the folder spelling it would refuse;
    // following into="Alpha.esp" extends the named plugin inside the two-plugin folder
    [Fact]
    public void ATwoPluginFolderIsOfferedByAPluginAndThatSpellingResolves()
    {
        using var w = new ExtendResolveRig();
        Inventory(w);

        var two = w.Into("Alphaa", w.Wgt(1)).Error ?? "";
        Assert.Contains("into=\"Alpha.esp\"", two);
        Assert.DoesNotContain("into=\"houseCARL - TwoEsp\"", two);

        var followed = w.Into("Alpha.esp", w.Wgt(6));
        Assert.True(followed.Success, followed.Error);
        Assert.Equal(("houseCARL - TwoEsp", "Alpha.esp"), Tail(followed.OutputPath));
    }

    // into="Betta" names the near-stem candidate FIRST, ahead of the alphabet (#380)
    [Fact]
    public void ATypoedStemNamesTheNearStemCandidateFirst()
    {
        using var w = new ExtendResolveRig();
        Inventory(w);

        var ranked = Candidates(w.Into("Betta", w.Wgt(1)).Error ?? "");

        Assert.NotEmpty(ranked);
        Assert.Equal("into=\"Beta.esp\"", ranked[0]);
    }

    // the RECORD lane leaves out an owned folder holding no plugin; the RIDER lane keeps it
    [Fact]
    public void APluginlessOwnedFolderIsOfferedOnTheRiderLaneOnly()
    {
        using var w = new ExtendResolveRig();
        w.Seed();
        w.MarkOwned("houseCARL - AssetsOnly", "");
        const string nearBare = "houseCARL - AssetsOnli";

        // Refused with candidates, so the omission is the filter's and not an empty or absent list.
        var rec = w.Into(nearBare, w.Wgt(1));
        Assert.False(rec.Success);
        Assert.Contains("; try into=\"", rec.Error);
        Assert.DoesNotContain("AssetsOnly", rec.Error);

        var rider = RiderRefusal(() => w.Svc.ResolvePatchModFolder(null, nearBare, "HcRiderDefault", BsaTools.RepackNaming));
        Assert.Contains("into=\"houseCARL - AssetsOnly\"", rider);
    }

    // the RECORD lane leaves out an owned folder holding no plugin: with nothing else owned it says houseCARL owns no
    // patch holding a plugin, rather than counting the pluginless folder as one no spelling reaches
    [Fact]
    public void APluginlessOwnedFolderIsNotCountedAsAnUnreachablePatchOnTheRecordLane()
    {
        using var w = new ExtendResolveRig();
        w.MarkOwned("houseCARL - AssetsOnly", "");

        var rec = w.Into("Ghost", w.Wgt(1));

        Assert.False(rec.Success);
        Assert.Contains("houseCARL owns no patch holding a plugin yet", rec.Error);
        Assert.DoesNotContain("renaming one of", rec.Error);
    }

    // the sentence names three candidates and no more, led by the near-miss the caller meant, in the ruled shape
    // "A, B or C (+N more), or <fresh>"; the count tracks the inventory, one higher with one patch more
    [Fact]
    public void PastTheCapThreeCandidatesAreNamedAndTheRestCounted()
    {
        using var w = new ExtendResolveRig();
        Inventory(w);
        Assert.True(w.Svc.ApplyEdits(new[] { w.Wgt(2) }, "GhostPatch", null).Success);
        for (int i = 0; i < 9; i++)
        {
            var d = w.MarkOwned($"houseCARL - Bulk{i:D2}", $"Bulk{i:D2}.esp");
            File.WriteAllText(Path.Combine(d, $"Bulk{i:D2}.esp"), "b");
        }

        var capText = w.Into("GhostCapped", w.Wgt(1)).Error ?? "";
        var rows = Candidates(capText);
        Assert.Equal(3, rows.Count);
        Assert.True(OneSentence(capText), capText);
        Assert.Equal("into=\"houseCARL - GhostPatch\"", rows[0]);
        Assert.Contains($"; try {rows[0]}, {rows[1]} or {rows[2]} (+", capText);
        Assert.Contains(" more), or dropping into= and passing patch=\"GhostCapped\" for a fresh patch", capText);

        var d9 = w.MarkOwned("houseCARL - Bulk09", "Bulk09.esp");
        File.WriteAllText(Path.Combine(d9, "Bulk09.esp"), "b");
        var capText2 = w.Into("GhostCapped", w.Wgt(1)).Error ?? "";
        Assert.Equal(Dropped(capText) + 1, Dropped(capText2));
    }

    // the refusal counts the patches no spelling reaches and says what to do about it, rather than telling the caller
    // houseCARL owns none; following it (the rename) makes into="Alpha" resolve to that patch
    [Fact]
    public void OwnedPatchesNoSpellingReachesAreCountedAndRenamingOneReachesIt()
    {
        using var w = new ExtendResolveRig();
        w.OwnedWithPlugins("houseCARL - Twin", "Alpha", "Beta");
        w.OwnedWithPlugins("houseCARL - Twin backup", "Alpha", "Beta");
        w.Svc.Stats();

        var text = w.Into("GhostNowhere", w.Wgt(1)).Error ?? "";
        Assert.Contains("try renaming one of the 2 patches houseCARL owns in MO2", text);
        Assert.Contains("no single into= spelling reaches any of them", text);
        Assert.DoesNotContain("owns no patch", text);
        Assert.True(OneSentence(text), text);

        Directory.Move(Path.Combine(w.ModsDir, "houseCARL - Twin backup"), Path.Combine(w.ModsDir, "houseCARL - Alpha"));
        w.Svc.Stats();
        var renamed = w.Into("Alpha", w.Wgt(2));
        Assert.True(renamed.Success, renamed.Error);
        Assert.Equal(("houseCARL - Alpha", "Alpha.esp"), Tail(renamed.OutputPath));
    }

    // an unreadable folder mid-scan yields NO candidates, not the half the scan got to; says the scan failed rather
    // than claiming houseCARL owns none; never names a spelling that only looks unambiguous; the removal lane no
    // longer sends them to mint one on a scan that failed
    [Fact]
    public void AScanThatFailsPartwayOffersNoCandidatesAndSaysSo()
    {
        using var w = new ExtendResolveRig();
        w.OwnedWithPlugins("houseCARL - Twin", "Alpha", "Beta");
        w.OwnedWithPlugins("houseCARL - Twin backup", "Alpha", "Beta");
        w.Svc.Stats();

        // The folder that sorts LAST is the one held, so the scan has already read its twin when it throws.
        using (File.Open(Path.Combine(w.ModsDir, "houseCARL - Twin backup", "meta.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            w.Svc.Stats();
            var r = w.Into("GhostLocked", w.Wgt(3));
            Assert.False(r.Success);
            Assert.Empty(Candidates(r.Error!));
            Assert.DoesNotContain("owns no patch", r.Error);
            Assert.Contains("could not scan it just now", r.Error);
            Assert.DoesNotContain("into=\"Alpha.esp\"", r.Error);
            Assert.True(OneSentence(r.Error!), r.Error);

            var rm = RemoveTools.Remove(w.Svc, new[] { w.Fid }, into: "GhostLocked").Trim();
            Assert.Contains("could not scan it just now", rm);
            Assert.DoesNotContain("owns no patch", rm);
            Assert.DoesNotContain("making the patch first", rm);
            Assert.True(OneSentence(rm), rm);
        }
    }

    // the removal refusal on an install owning nothing states the empty inventory in ONE sentence, with the facts as
    // clauses rather than two stacked ", and" openings, says how the patch gets made, naming the tools ON the surface
    [Fact]
    public void TheRemovalLaneOwningNothingSaysHowThePatchGetsMade()
    {
        using var w = new ExtendResolveRig();

        var bare = RemoveTools.Remove(w.Svc, new[] { w.Fid }, into: "Nothing").Trim();

        Assert.Contains("houseCARL owns no patch holding a plugin yet", bare);
        Assert.True(OneSentence(bare), bare);
        Assert.True(Regex.Matches(bare, ", and ").Count <= 1, bare);
        Assert.Contains("; try making the patch first with a write that creates one", bare);
        Assert.Contains($"({ToolNames.Apply}, {ToolNames.Create} or {ToolNames.Forward})", bare);
        Assert.DoesNotContain("housecarl_create_record", bare);
        Assert.DoesNotContain("housecarl_forward_record", bare);
    }
}

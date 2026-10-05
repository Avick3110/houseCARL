using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.ExtendResolveRig;

namespace HousecarlMcpTests;

/// <summary>The into= extend refusals and the remedy each one offers: one sentence, what went wrong then what to try,
/// the lane's own fresh-patch parameter where it has one (#343, #357), none on the removal lane (#356), the owned
/// candidates (#380, #359), never an in-place clause (Aaron, 2026-09-05). A remedy is a claim about a call, so the
/// tests make the call. Migrated from the extend-resolve-guard probe (arms NOT-FOUND, REMEDY, REMOVE-TAIL, 8e, 9, 9b).</summary>
[Trait("tier", "integration")]
public sealed class ExtendResolveRefusalTests
{
    static JsonElement Doc(string s) => JsonDocument.Parse(s).RootElement;

    /// <summary>An active plugin in a mod folder no write owns, added to the order.</summary>
    static string AddActivePlugin(ExtendResolveRig w, string folder, string stem, Action<SkyrimMod>? fill = null)
    {
        var path = Path.Combine(w.ModsDir, folder, stem + ".esp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var m = new SkyrimMod(new ModKey(stem, ModType.Plugin), SkyrimRelease.SkyrimSE);
        fill?.Invoke(m);
        m.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        var master = w.MasterKey.FileName.String;
        w.Mo2.Profile(master + "\r\n" + stem + ".esp\r\n", "*" + master + "\r\n*" + stem + ".esp\r\n",
                      "+" + folder + "\r\n+MasterMod\r\n");
        w.Svc.Stats();
        return path;
    }

    // into="GhostPatch" refuses, naming the .esp + the folder searched; the remedy names patch="GhostPatch" (#343),
    // qualified with the auto-suffix; the whole refusal is ONE sentence that names what to try and never in_place
    [Fact]
    public void IntoANameNothingAnswersToRefusesInOneSentenceNamingTheFreshPatchParameter()
    {
        using var w = new ExtendResolveRig();
        w.Seed();

        var r = w.Into("GhostPatch", w.Wgt(1));

        Assert.False(r.Success);
        Assert.Contains("GhostPatch.esp", r.Error);
        Assert.Contains("houseCARL - GhostPatch", r.Error);
        Assert.Contains("patch=\"GhostPatch\" for a fresh patch", r.Error);
        Assert.Contains("auto-suffixed if that name is taken", r.Error);
        Assert.True(OneSentence(r.Error!), r.Error);
        Assert.Contains("; try ", r.Error);
        Assert.DoesNotContain("in_place", r.Error);
    }

    // patch="GhostPatch" writes the patch under that name (following the remedy produces GhostPatch.esp, not Patch.esp)
    [Fact]
    public void FollowingTheNotFoundRemedyWritesThePatchUnderTheGuessedName()
    {
        using var w = new ExtendResolveRig();
        Assert.False(w.Into("GhostPatch", w.Wgt(1)).Success);

        var r = w.Svc.ApplyEdits(new[] { w.Wgt(2) }, "GhostPatch", null);

        Assert.True(r.Success, r.Error);
        Assert.Equal(("houseCARL - GhostPatch", "GhostPatch.esp"), Tail(r.OutputPath));
    }

    // into="GhostActive" (an active plugin, foreign folder) still refuses with the naming remedy; following it
    // auto-suffixes off the active plugin; the refusal never names the file the write produces
    [Fact]
    public void ARemedyNameTakenByAnActivePluginIsOfferedWithoutPromisingTheFilename()
    {
        using var w = new ExtendResolveRig();
        AddActivePlugin(w, "OddlyNamedMod", "GhostActive");

        var r = w.Into("GhostActive", w.Wgt(4));
        Assert.False(r.Success);
        Assert.Contains("patch=\"GhostActive\" for a fresh patch", r.Error);

        var followed = w.Svc.ApplyEdits(new[] { w.Wgt(4) }, "GhostActive", null);
        Assert.True(followed.Success, followed.Error);
        Assert.Equal(("houseCARL - GhostActive_001", "GhostActive_001.esp"), Tail(followed.OutputPath));
        Assert.DoesNotContain(Path.GetFileNameWithoutExtension(followed.OutputPath), r.Error, StringComparison.OrdinalIgnoreCase);
    }

    // the rider lane still refuses in ONE sentence, naming the .esp searched; hands back patch= with the caller's
    // guessed name, qualified with the auto-suffix; never the retired patch_name=; says to DROP into=;
    // keeping into= and adding patch= is refused for naming both lanes (#1061); following it creates that folder fresh
    [Fact]
    public void TheRiderLaneNamesItsOwnFolderParameterAndFollowingItCreatesTheFolder()
    {
        using var w = new ExtendResolveRig();

        var err = RiderRefusal(() => w.Svc.ResolvePatchModFolder(null, "GhostRider", "HcRiderDefault", BsaTools.RepackNaming));
        Assert.Contains("GhostRider.esp", err);
        Assert.True(OneSentence(err), err);
        Assert.Contains("patch=\"GhostRider\" for a fresh folder", err);
        Assert.Contains("auto-suffixed if that name is taken", err);
        Assert.DoesNotContain("patch_name", err);
        Assert.Contains("dropping into= and passing patch=\"GhostRider\"", err);

        var both = RiderRefusal(() => w.Svc.ResolvePatchModFolder("GhostRider", "GhostRider", "HcRiderDefault", BsaTools.RepackNaming));
        Assert.Contains("patch='GhostRider' names a NEW mod folder for the .bsa", both);
        Assert.Contains("the two lanes are exclusive", both);

        var fresh = w.Svc.ResolvePatchModFolder("GhostRider", null, "HcRiderDefault", BsaTools.RepackNaming);
        Assert.True(fresh.CreatedFresh);
        Assert.Equal("houseCARL - GhostRider", Path.GetFileName(fresh.ModFolder));
    }

    // a null naming refuses without offering a parameter it was never told about; names the owned patches to try,
    // as into= spellings, in one sentence (#380)
    [Fact]
    public void ARiderWithNoNamingOffersOnlyTheOwnedPatches()
    {
        using var w = new ExtendResolveRig();
        w.Seed();

        var err = RiderRefusal(() => w.Svc.ResolvePatchModFolder(null, "GhostRider", "houseCARL_Extract", naming: null));

        Assert.Contains("GhostRider.esp", err);
        Assert.DoesNotContain("patch=", err);
        Assert.Contains("; try into=\"", err);
        Assert.True(OneSentence(err), err);
    }

    // housecarl_remove into a patch that does not exist still refuses, naming the .esp searched; does NOT name patch=;
    // no longer offers 'create it fresh' (#356); falls back to the owned candidates (#380); states why there is no
    // create route; ONE sentence naming no in-place lane. The missing-patch= refusal names no spelling either.
    [Fact]
    public void TheRemovalLaneRefusalOffersNoFreshPatchAndSaysWhy()
    {
        using var w = new ExtendResolveRig();
        w.Seed();

        var r = w.Svc.RemoveRecords(new[] { w.Fid }, "GhostRemove");
        Assert.False(r.Success);
        Assert.Contains("GhostRemove.esp", r.Error);
        Assert.DoesNotContain("patch=", r.Error);
        Assert.DoesNotContain("create it fresh", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; try into=\"", r.Error);
        Assert.Contains(WriteSentences.RemoveNoFreshPatch, r.Error);
        Assert.True(OneSentence(r.Error!), r.Error);
        Assert.DoesNotContain("in_place", r.Error);

        var bare = w.Svc.RemoveRecords(new[] { w.Fid }, null);
        Assert.False(bare.Success);
        Assert.Contains("patch is required", bare.Error);
        Assert.DoesNotContain("in_place", bare.Error);
    }

    // housecarl_remove's not-found refusal names no in-place lane, and never target=; offers the owned candidates;
    // housecarl_remove answers a lane-less call itself, before the service's own arm
    [Fact]
    public void TheRemoveToolRefusalNamesTheOwnedCandidatesAndAnswersALanelessCall()
    {
        using var w = new ExtendResolveRig();
        w.Seed();

        var modern = RemoveTools.Remove(w.Svc, new[] { w.Fid }, into: "GhostRemove");
        Assert.DoesNotContain("in_place", modern);
        Assert.DoesNotContain("target=", modern);
        Assert.Contains("; try into=\"", modern);

        var noLane = RemoveTools.Remove(w.Svc, new[] { w.Fid });
        Assert.Contains("no lane named", noLane);
        Assert.DoesNotContain("patch is required", noLane);
    }

    // the not-found refusal fires for this record too; in_place="<plugin filename>" reaches the first-time
    // confirmation; acknowledging it REMOVES the record, and it is GONE from the file on disk
    [Fact]
    public void TheInPlaceLaneTheRefusalNoLongerNamesStillRemovesTheRecord()
    {
        using var w = new ExtendResolveRig();
        FormKey fk = default;
        var path = AddActivePlugin(w, "RemoveHereMod", "RemoveHere", m =>
        {
            var x = m.Weapons.AddNew(); x.EditorID = "HcRemoveMe";
            x.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
            fk = x.FormKey;
        });
        var fid = ScratchMo2.Fid(fk);

        Assert.Contains("cannot extend: no houseCARL patch named 'GhostRemove'", RemoveTools.Remove(w.Svc, new[] { fid }, into: "GhostRemove"));
        Assert.Contains("first-time confirmation", RemoveTools.Remove(w.Svc, new[] { fid }, in_place: "RemoveHere.esp"));

        var done = RemoveTools.Remove(w.Svc, new[] { fid }, in_place: "RemoveHere.esp", acknowledge: true);
        Assert.Contains("removed 1 record from RemoveHere.esp IN PLACE", done);
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        Assert.DoesNotContain(back.Weapons, x => x.FormKey == fk);
    }

    // housecarl_forward's not-found refusal names patch=<the guessed name>; housecarl_create's does too
    [Fact]
    public void ForwardAndCreateStateThatPatchNamesAFreshPatch()
    {
        using var w = new ExtendResolveRig();

        var fwd = w.Svc.ForwardRecords(new[] { w.Fid }, w.MasterKey.FileName, null, "GhostFwd");
        Assert.False(fwd.Success);
        Assert.Contains("patch=\"GhostFwd\" for a fresh patch", fwd.Error);

        var cre = w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Keyword", Editorid = "HcExtKw" } }, null, "GhostCre");
        Assert.False(cre.Success);
        Assert.Contains("patch=\"GhostCre\" for a fresh patch", cre.Error);
    }

    // the un-owned refusal names the fresh lane's parameter (#359); does NOT hand back the colliding stem; names the
    // patches houseCARL owns; is ONE sentence with no in-place clause. housecarl_remove's un-owned refusal offers no
    // in-place lane either; the RIDER lane names its own parameter plus the owned patches, in one sentence
    [Fact]
    public void TheUnownedRefusalNamesAFreshParameterAndTheOwnedPatchesNeverTheCollidingStem()
    {
        using var w = new ExtendResolveRig();
        w.Seed();
        var foreign = Path.Combine(w.ModsDir, "houseCARL - Foreign");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "Foreign.esp"), "not a real plugin");

        var r = w.Into("Foreign", w.Wgt(1));
        Assert.False(r.Success);
        Assert.Contains("patch= a name no mod folder already uses for a fresh patch", r.Error);
        Assert.DoesNotContain("patch=\"Foreign\"", r.Error);
        Assert.Contains("; try into=\"", r.Error);
        Assert.True(OneSentence(r.Error!), r.Error);
        Assert.DoesNotContain("in_place", r.Error);

        var tooled = RemoveTools.Remove(w.Svc, new[] { w.Fid }, into: "Foreign");
        Assert.DoesNotContain("in_place", tooled);
        Assert.Contains("; try into=\"", tooled);

        var rider = RiderRefusal(() => w.Svc.ResolvePatchModFolder(null, "Foreign", "HcRiderDefault", BsaTools.RepackNaming));
        Assert.Contains("patch= a name no mod folder already uses for a fresh folder", rider);
        Assert.Contains("; try into=\"", rider);
        Assert.True(OneSentence(rider), rider);
    }

    // every write tool's un-owned refusal (on a folder holding an ACTIVE plugin) names the owned candidates and no
    // in-place lane: apply (ONE sentence), create, forward, remove; remove states why it offers no fresh patch
    [Fact]
    public void EveryWriteToolsUnownedRefusalNamesTheCandidatesAndNoInPlaceLane()
    {
        using var w = new ExtendResolveRig();
        w.Seed();
        AddActivePlugin(w, "RemoveHereMod", "RemoveHere");
        var fid = w.Fid;

        var ap = ApplyTools.Apply(w.Svc, ops: Doc($"[{{\"formid\":\"{fid}\",\"field_path\":\"BasicStats.Weight\",\"value\":\"2\"}}]"),
                                  into: "RemoveHereMod");
        Assert.DoesNotContain("in_place", ap);
        Assert.Contains("; try into=\"", ap);
        Assert.True(OneSentence(ap.Trim()), ap);

        var cr = CreateTools.Create(w.Svc, records: Doc("[{\"record_type\":\"Keyword\",\"editorid\":\"HcExtUnowned\"}]"),
                                    into: "RemoveHereMod");
        Assert.DoesNotContain("in_place", cr);
        Assert.Contains("; try into=\"", cr);

        var fw = ForwardTools.Forward(w.Svc, formids: new[] { fid }, source: w.MasterKey.FileName.String, into: "RemoveHereMod");
        Assert.DoesNotContain("in_place", fw);
        Assert.Contains("; try into=\"", fw);

        var rm = RemoveTools.Remove(w.Svc, new[] { fid }, into: "RemoveHereMod");
        Assert.DoesNotContain("in_place", rm);
        Assert.Contains("; try into=\"", rm);
        Assert.Contains(WriteSentences.RemoveNoFreshPatch, rm);
        Assert.DoesNotContain("patch=", rm);
    }
}

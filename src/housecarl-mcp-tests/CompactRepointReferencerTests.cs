using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>An in-place compact that repoints its external referencers: a localized or unreadable referencer blocks it
/// up front with nothing written, both refusals split their hit list by shape, and with nothing localized anywhere the
/// full opt-in path compacts and repoints. Each test builds its own instance.</summary>
[Trait("tier", "integration")]
public sealed class CompactRepointReferencerTests
{
    static LocalizedStringsFixture.Spec Target(string folder, string name) =>
        new(folder, new ModKey(name, ModType.Plugin), "TGT NAME", "TGT DESC", Localized: false);

    static LocalizedStringsFixture.Spec Referencer(string folder, string name, LocalizedStringsFixture.Spec tgt, bool localized = true,
                                                   bool beside = false) =>
        new(folder, new ModKey(name, ModType.Plugin), "REF NAME", "REF DESC", LinksTo: LocalizedStringsFixture.WeaponKey(tgt),
            Localized: localized, StringsBeside: beside, SecondLanguage: beside ? "French" : null);

    // REPOINT-LOC refused up front, naming the localized referencer; target AND referencer untouched.
    [Fact]
    public void ALocalizedReferencerRefusesTheRepointUpFrontWithNothingWritten()
    {
        var tgt = Target("RlTgt", "HcCsRlTgt");
        var rf = Referencer("RlRef", "HcCsRlRef", tgt);
        using var w = new LocalizedCompactWorld(tgt, rf);
        string tgtPath = w.PluginPath(tgt), refPath = w.PluginPath(rf);
        byte[] tgtBefore = File.ReadAllBytes(tgtPath), refBefore = File.ReadAllBytes(refPath);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);

        Assert.False(o.Success);
        Assert.Contains("LOCALIZED", o.Error);
        Assert.Contains(rf.Key.FileName.String, o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(LocalizedCompactWorld.Same(tgtPath, tgtBefore));
        Assert.True(LocalizedCompactWorld.Same(refPath, refBefore));
        Assert.True(LocalizedCompactWorld.NoStaging(tgtPath) && LocalizedCompactWorld.NoStaging(refPath));
    }

    // REPOINT-LOC fixture: the referencer IS an external referencer of the target (plain compact names it).
    // REPOINT-LOC the referencer refusal does NOT send the caller down a repoint that would refuse (#374).
    [Fact]
    public void APlainCompactWithALocalizedReferencerWarnsTheRepointWillNotWork()
    {
        var tgt = Target("RlTgt", "HcCsRlTgt");
        var rf = Referencer("RlRef", "HcCsRlRef", tgt);
        using var w = new LocalizedCompactWorld(tgt, rf);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String);

        Assert.False(o.Success);
        Assert.Contains(rf.Key.FileName.String, o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will NOT work here", o.Error);
        Assert.DoesNotContain("Re-run with repoint_externals=true AND in_place=true", o.Error);
    }

    // REPOINT-LOC refuses BEFORE the consent gate (no CONFIRM prompt for a rewrite that cannot happen).
    [Fact]
    public void ALocalizedReferencerRefusesBeforeTheConsentPrompt()
    {
        var tgt = Target("RlTgt", "HcCsRlTgt");
        var rf = Referencer("RlRef", "HcCsRlRef", tgt);
        using var w = new LocalizedCompactWorld(tgt, rf);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true);

        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);
        Assert.Contains("LOCALIZED", o.Error);
    }

    // REPOINT-LOC backstop: RepointInPlace itself refuses the localized referencer verbatim, file untouched.
    [Fact]
    public void RepointInPlaceItselfRefusesALocalizedReferencer()
    {
        var tgt = Target("RlTgt", "HcCsRlTgt");
        var rf = Referencer("RlRef", "HcCsRlRef", tgt);
        using var w = new LocalizedCompactWorld(tgt, rf);
        string tgtPath = w.PluginPath(tgt), refPath = w.PluginPath(rf);
        using var rr = LoadOrderResolver.Build(new[] { Path.Combine(w.Fx.Data, "Skyrim.esm"), tgtPath, refPath });
        var before = File.ReadAllBytes(refPath);

        var rep = RemapEngine.RepointInPlace(rr, rf.Key.FileName.String,
            new Dictionary<FormKey, FormKey> { [LocalizedStringsFixture.WeaponKey(tgt)] = new FormKey(tgt.Key, 0x900) });

        Assert.False(rep.Success);
        Assert.StartsWith("houseCARL did not write", rep.Error);
        Assert.True(LocalizedCompactWorld.Same(refPath, before));
    }

    static (LocalizedCompactWorld W, LocalizedStringsFixture.Spec Tgt, string LocPath, string LockPath) Mixed()
    {
        var tgt = Target("RmTgt", "HcCsRmTgt");
        var refLoc = new LocalizedStringsFixture.Spec("RmRefLoc", new ModKey("HcCsRmRefLoc", ModType.Plugin), "L NAME", "L DESC",
                                                      LinksTo: LocalizedStringsFixture.WeaponKey(tgt));
        var refLock = new LocalizedStringsFixture.Spec("RmRefLock", new ModKey("HcCsRmRefLock", ModType.Plugin), "K NAME", "K DESC",
                                                       LinksTo: LocalizedStringsFixture.WeaponKey(tgt), Localized: false);
        var w = new LocalizedCompactWorld(tgt, refLoc, refLock);
        return (w, tgt, w.PluginPath(refLoc), w.PluginPath(refLock));
    }

    // REPOINT-MIXED fixture: one referencer is localized, the other is a plain plugin (unheld).
    // REPOINT-MIXED (not a render arm) a held referencer never reaches the pre-flight — the identify pass drops it first.
    [Fact]
    public void AHeldReferencerIsDroppedByTheIdentifyPassBeforeThePreflight()
    {
        var (w, tgt, locPath, lockPath) = Mixed();
        using (w)
        {
            Assert.NotEqual(LocalizedShape.NotLocalized, LocalizedStrings.Assess(locPath, w.Fx.Data).Shape);
            Assert.Equal(LocalizedShape.NotLocalized, LocalizedStrings.Assess(lockPath, w.Fx.Data).Shape);

            WritePatchBuilder.CompactOutcome held;
            using (new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None))
                held = w.Svc.CompactPlugin(tgt.Key.FileName.String);

            Assert.False(held.Success);
            Assert.Contains("Referencers: HcCsRmRefLoc.esp.", held.Error);
        }
    }

    // REPOINT-MIXED end to end, a localized-only hit list gets one class and no unreadable clause.
    [Fact]
    public void ALocalizedOnlyHitListGetsOneClassAndNoUnreadableClause()
    {
        var (w, tgt, _, _) = Mixed();
        using (w)
        {
            var err = w.Svc.CompactPlugin(tgt.Key.FileName.String).Error ?? "";

            Assert.Contains("1 flagged LOCALIZED (HcCsRmRefLoc.esp)", err);
            Assert.Contains("Where HcCsRmRefLoc.esp's text is:", err);
            Assert.DoesNotContain("houseCARL could not read (", err);
            Assert.DoesNotContain("is blocked:", err);
        }
    }

    static readonly (string Plugin, LocalizedShape Shape, string Why)[] MixedHits =
    {
        ("A.esp", LocalizedShape.GameDataOnly, "A's text is in game-Data."),
        ("B.esp", LocalizedShape.Unreadable, "houseCARL could not read the file at that path to see where its text lives."),
        ("C.esp", LocalizedShape.LooseComplete, "C's text is beside it."),
    };

    // REPOINT-MIXED render: the census counts and names per class.
    // REPOINT-MIXED render: the unreadable hit is NOT in the localized count or its name list.
    [Fact]
    public void TheCensusCountsAndNamesEachClassSeparately()
    {
        var census = RecordWrites.BlockedReferencerCensus(MixedHits);

        Assert.Contains("2 flagged LOCALIZED (A.esp, C.esp)", census);
        Assert.Contains("1 houseCARL could not read (B.esp)", census);
        Assert.DoesNotContain("3 flagged LOCALIZED", census);
        Assert.DoesNotContain("B.esp, C.esp", census);
    }

    // REPOINT-MIXED render: the FIRST of EACH class is attributed, with its own lead-in.
    // REPOINT-MIXED render: the per-class tails count their own class, and a class of one gets none.
    [Fact]
    public void TheReasonsAttributeTheFirstOfEachClassAndTailOnlyTheirOwnClass()
    {
        var reasons = RecordWrites.BlockedReferencerReasons(MixedHits);

        Assert.Contains("Where A.esp's text is:", reasons);
        Assert.Contains("Why B.esp is blocked:", reasons);
        Assert.Contains("could not read the file at that path", reasons);
        Assert.Contains("The other 1 localized referencer(s)", reasons);
        Assert.DoesNotContain("unreadable referencer(s)", reasons);
    }

    // REPOINT-MIXED render: a class with no hits contributes nothing.
    [Fact]
    public void AClassWithNoHitsContributesNothingToTheCensus()
    {
        var locOnly = RecordWrites.BlockedReferencerCensus(MixedHits.Where(m => m.Shape != LocalizedShape.Unreadable).ToArray());
        var unreadOnly = RecordWrites.BlockedReferencerCensus(MixedHits.Where(m => m.Shape == LocalizedShape.Unreadable).ToArray());

        Assert.DoesNotContain("could not read", locOnly);
        Assert.DoesNotContain("flagged LOCALIZED", unreadOnly);
        Assert.Contains("1 houseCARL could not read (B.esp)", unreadOnly);
    }

    // REPOINT-BESIDE fixture: the referencer really is the complete-loose-set arrangement.
    // REPOINT-BESIDE a complete-loose-set referencer blocks the repoint; target, referencer AND its tables untouched.
    [Fact]
    public void ALooseCompleteReferencerBlocksTheRepointWithItsTablesUntouched()
    {
        var tgt = Target("RbTgt", "HcCsRbTgt");
        var rf = Referencer("RbRef", "HcCsRbRef", tgt, beside: true);
        using var w = new LocalizedCompactWorld(tgt, rf);
        string tgtPath = w.PluginPath(tgt), refPath = w.PluginPath(rf);
        var shape = LocalizedStrings.Assess(refPath, w.Fx.Data);
        Assert.Equal(LocalizedShape.LooseComplete, shape.Shape);
        Assert.Equal(2, shape.Languages.Count);
        byte[] tgtBefore = File.ReadAllBytes(tgtPath), refBefore = File.ReadAllBytes(refPath);
        var tablesBefore = LocalizedStrings.OwnTableFiles(refPath).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(tablesBefore);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);

        Assert.False(o.Success);
        Assert.Contains(rf.Key.FileName.String, o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(LocalizedCompactWorld.Same(tgtPath, tgtBefore));
        Assert.True(LocalizedCompactWorld.Same(refPath, refBefore));
        var tablesAfter = LocalizedStrings.OwnTableFiles(refPath).ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(tablesBefore.Count, tablesAfter.Count);
        Assert.All(tablesBefore, kv => Assert.True(tablesAfter.TryGetValue(kv.Key, out var b) && b.AsSpan().SequenceEqual(kv.Value), kv.Key));
    }

    // REPOINT-PLAIN a referencer houseCARL CAN rewrite still gets the ordinary repoint remedy (#374, other direction).
    [Fact]
    public void APlainReferencerStillGetsTheOrdinaryRepointRemedy()
    {
        var tgt = Target("RpTgt", "HcCsRpTgt");
        var rf = Referencer("RpRef", "HcCsRpRef", tgt, localized: false);
        using var w = new LocalizedCompactWorld(tgt, rf);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String);

        Assert.False(o.Success);
        Assert.Contains("Re-run with repoint_externals=true AND in_place=true", o.Error);
        Assert.DoesNotContain("will NOT work here", o.Error);
    }

    // REPOINT-PLAIN nothing localized: compacts in place + repoints.
    [Fact]
    public void WithNothingLocalizedTheInPlaceCompactRepointsTheReferencer()
    {
        var tgt = Target("RpTgt", "HcCsRpTgt");
        var rf = Referencer("RpRef", "HcCsRpRef", tgt, localized: false);
        using var w = new LocalizedCompactWorld(tgt, rf);

        var o = w.Svc.CompactPlugin(tgt.Key.FileName.String, inPlace: true, repointExternals: true, acknowledge: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var r = Assert.Single(o.Repointed);
        Assert.True(r.Success);
        FormKey weap, link;
        using (var tb = SkyrimMod.CreateFromBinaryOverlay(w.PluginPath(tgt), SkyrimRelease.SkyrimSE))
            weap = tb.Weapons.Single(x => x.EditorID == LocalizedStringsFixture.WeaponEdid(tgt)).FormKey;
        using (var rb = SkyrimMod.CreateFromBinaryOverlay(w.PluginPath(rf), SkyrimRelease.SkyrimSE))
            link = rb.FormLists.First().Items.First().FormKey;
        Assert.Equal(weap, link);
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A world holding the shape #301 found in the wild: a PERK whose entry-point effect names an actor-value
/// function in its DATA while its EPFT says Float, so Mutagen refuses that ONE effect. The perk's other effect is a
/// perfectly readable ability link — the thing a <c>references=</c> scan has to still find.
///
/// <para>It is its own world rather than an addition to <see cref="WalkUnscannableWorld"/> because that fixture's
/// corruption pass rewrites EVERY EPFT byte in its plugin and asserts it hit exactly one: a second entry-point
/// effect there would make both fixtures wrong. The two malformations are also deliberately different — that one is
/// a parameter type nothing can decode and must stay unscannable, this one is a parameter type that decodes.</para></summary>
public sealed class PerkEncodingWorld : IDisposable
{
    public string Root { get; }
    public string MasterName { get; }
    public LoadOrderService Svc { get; }

    /// <summary>The perk whose second effect Mutagen refuses.</summary>
    public string InconsistentPerkFid { get; }
    /// <summary>The spell that perk's FIRST, readable effect grants — the reference a scan must still find.</summary>
    public string AbilityFid { get; }
    /// <summary>A perk named ONLY by a condition INSIDE the refused effect. Nothing but Mutagen's own condition
    /// parse over those raw bytes can reach it, so it is what proves that parse runs.</summary>
    public string ConditionTargetFid { get; }
    /// <summary>A perk with nothing wrong with it, so a scan's answer is not one row wide.</summary>
    public string SoundPerkFid { get; }
    /// <summary>A perk whose own RECORD-LEVEL condition Mutagen will not read, while its effect list is fine — the
    /// row that must NOT be explained with an effect's bytes.</summary>
    public string BadConditionPerkFid { get; }
    /// <summary>A perk whose refused effect declares a FormID parameter of all zeroes — a declared-but-null link.</summary>
    public string NullParamPerkFid { get; }
    /// <summary>A perk whose refused effect declares a LOCALIZED string parameter, in a plugin flagged localized:
    /// those four bytes are a strings-table key, not characters.</summary>
    public string LocalizedParamPerkFid { get; }

    public PerkEncodingWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-perk-encoding-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var masterKey = new ModKey("HcPerkEncoding", ModType.Master);
        MasterName = masterKey.FileName.String;
        var master = new SkyrimMod(masterKey, SkyrimRelease.SkyrimSE);

        var ability = master.Spells.AddNew();
        ability.EditorID = "HcPerkEncodingAbility";

        var conditionTarget = master.Perks.AddNew();
        conditionTarget.EditorID = "HcPerkEncodingConditionTarget";

        var sound = master.Perks.AddNew();
        sound.EditorID = "HcPerkEncodingSound";
        sound.Effects.Add(new PerkAbilityEffect { Ability = ability.ToNullableLink() });

        // The one entry-point effect in the plugin, so the byte patch below hits exactly it. Its ability sibling is
        // written FIRST so the readable link sits on the far side of the refusal from the list's start.
        var inconsistent = master.Perks.AddNew();
        inconsistent.EditorID = "HcPerkEncodingInconsistent";
        inconsistent.Effects.Add(new PerkAbilityEffect { Ability = ability.ToNullableLink() });
        var refused = NewMultiplyAvMultEffect();
        // The link that ONLY the condition parse can reach: a CTDA inside the effect Mutagen refuses.
        var inside = new HasPerkConditionData { RunOnType = Condition.RunOnType.Subject };
        inside.Perk = new FormLinkOrIndex<IPerkGetter>(inside, conditionTarget.FormKey);
        refused.Conditions.Add(new PerkCondition
        {
            RunOnTabIndex = 0,
            Conditions = { new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = inside } },
        });
        inconsistent.Effects.Add(refused);

        // Its own record-level condition is what breaks here; its effect list is perfectly decodable, which is the
        // trap — a marker built from the effect list would be a confident answer about the wrong subrecord.
        var badCondition = master.Perks.AddNew();
        badCondition.EditorID = "HcPerkEncodingBadCondition";
        badCondition.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetActorValueConditionData { ActorValue = ActorValue.Alteration },
        });
        badCondition.Effects.Add(NewMultiplyAvMultEffect());

        var nullParam = master.Perks.AddNew();
        nullParam.EditorID = "HcPerkEncodingNullParam";
        nullParam.Effects.Add(NewMultiplyAvMultEffect());

        var localizedParam = master.Perks.AddNew();
        localizedParam.EditorID = "HcPerkEncodingLocalizedParam";
        localizedParam.Effects.Add(NewMultiplyAvMultEffect());

        InconsistentPerkFid = $"{inconsistent.FormKey.ID:X6}:{masterKey.FileName}";
        BadConditionPerkFid = $"{badCondition.FormKey.ID:X6}:{masterKey.FileName}";
        NullParamPerkFid = $"{nullParam.FormKey.ID:X6}:{masterKey.FileName}";
        LocalizedParamPerkFid = $"{localizedParam.FormKey.ID:X6}:{masterKey.FileName}";
        AbilityFid = $"{ability.FormKey.ID:X6}:{masterKey.FileName}";
        ConditionTargetFid = $"{conditionTarget.FormKey.ID:X6}:{masterKey.FileName}";
        SoundPerkFid = $"{sound.FormKey.ID:X6}:{masterKey.FileName}";

        var path = Path.Combine(mods, "PerkEncodingMod", MasterName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        master.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        Assert.Equal(1, ProbeBytes.MakeEpftFunctionMismatch(path, inconsistent.FormKey.ID));
        Assert.Equal(1, ProbeBytes.MakeEpftFunctionMismatch(path, badCondition.FormKey.ID));
        Assert.Equal(1, ProbeBytes.TruncateCondition(path, badCondition.FormKey.ID));
        Assert.Equal(1, ProbeBytes.MakeEpftNullFormIdParameter(path, nullParam.FormKey.ID));
        Assert.Equal(1, ProbeBytes.MakeEpftLocalizedTextParameter(path, localizedParam.FormKey.ID));
        Assert.True(ProbeBytes.SetLocalizedFlag(path));

        // The fixtures must still exhibit their faults, or a green below proves nothing.
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            var bad = overlay.Perks.First(p => p.FormKey == inconsistent.FormKey);
            Assert.ThrowsAny<Exception>(() => ((IFormLinkContainerGetter)bad).EnumerateFormLinks().Count());
            Assert.Equal(2, bad.Effects.Count);
            Assert.IsAssignableFrom<IPerkAbilityEffectGetter>(bad.Effects[0]);
            Assert.ThrowsAny<Exception>(() => bad.Effects[1]);

            var cond = overlay.Perks.First(p => p.FormKey == badCondition.FormKey);
            Assert.ThrowsAny<Exception>(() => cond.Conditions[0]);
        }

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+PerkEncodingMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    /// <summary>The effect every fixture perk carries: function byte 13 (MultiplyAVMult), which Mutagen writes as
    /// EPFT 2 over an 8-byte EPFD — the shape the byte surgery then rewrites.</summary>
    static PerkEntryPointModifyActorValue NewMultiplyAvMultEffect() => new()
    {
        EntryPoint = APerkEntryPointEffect.EntryType.ModSpellMagnitude,
        ActorValue = ActorValue.Alteration,
        Value = 10f,
        Modification = PerkEntryPointModifyActorValue.ModificationType.MultiplyAVMult,
    };

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class PerkEncodingFixture : IDisposable
{
    public PerkEncodingWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A PERK effect whose function byte and EPFT flag disagree used to take the whole Effects field and the
/// whole record with it — unreadable in a read, absent from every PERK scan (#301). It now renders as its own
/// marked row, and the record's readable effects still answer a references= scan.</summary>
[Trait("tier", "integration")]
public sealed class PerkEffectEncodingTests : IClassFixture<PerkEncodingFixture>
{
    readonly PerkEncodingWorld _w;
    public PerkEffectEncodingTests(PerkEncodingFixture f) => _w = f.W;

    [Fact]
    public void TheInconsistentEffectIsOneMarkedRowAndItsSiblingStillReads()
    {
        var r = Effects(_w.InconsistentPerkFid);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        // The readable sibling is still read — the field did not fail as a whole.
        Assert.Contains("Effects[0]", r);
        Assert.Contains(_w.AbilityFid, r);
        // …and the refused one says which bytes disagree and what the parameter was decoded off.
        Assert.Contains("Effects[1] = (unreadable:", r);
        Assert.Contains("read off its own bytes", r);
        Assert.Contains("entry point is ModSpellMagnitude", r);          // Mutagen's own entry-point enum, not a table of ours
        Assert.Contains("function byte is 13", r);
        Assert.Contains("parameter value, decoded off EPFT alone as xEdit does, is 10", r);
    }

    string Effects(string fid) => RecordsTools.Records(
        _w.Svc, formids: new[] { fid },
        project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects" }, depth = 3 });

    /// <summary>The marker belongs to the EFFECTS list. A PERK's own record-level Conditions are a different list,
    /// and explaining one of its rows with an effect's entry point, function byte and parameter would be a confident
    /// answer about the wrong subrecord.</summary>
    [Fact]
    public void AFaultOnTheRecordsOwnConditionsIsNotExplainedWithAnEffectsBytes()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.BadConditionPerkFid },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Conditions" }, depth = 3 });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("Conditions[0] = (unreadable:", r);
        Assert.DoesNotContain("decoded off EPFT alone", r);
        Assert.DoesNotContain("function byte", r);
        Assert.DoesNotContain("entry point is", r);
    }

    /// <summary>A FormID parameter of all zeroes is a declared-but-null link, which is what Mutagen reads it as.
    /// Read with the wrong flag it becomes record 000000 of the plugin's first master — a link the record does not
    /// carry, which a references= scan could then match on.</summary>
    [Fact]
    public void AnAllZeroFormIdParameterReadsAsANullLink()
    {
        var r = Effects(_w.NullParamPerkFid);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("Effects[0] = (unreadable:", r);
        Assert.Contains("(null link)", r);
        Assert.DoesNotContain("000000:", r);
    }

    /// <summary>In a plugin flagged localized, an EPFT 7 parameter is a strings-table key. Decoding those four bytes
    /// as text hands back mojibake under a sentence claiming the value was read.</summary>
    [Fact]
    public void ALocalizedStringParameterIsReportedAsAKeyRatherThanDecodedAsText()
    {
        var r = Effects(_w.LocalizedParamPerkFid);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("Effects[0] = (unreadable:", r);
        Assert.Contains("lstring:0x", r);
        Assert.Contains("not resolved", r);
    }

    /// <summary>references= and references_none= in one call are one question about one record: the record used to
    /// be read twice, so it could be reported as read leniently by one arm and unscannable by the other.</summary>
    [Fact]
    public void ReferencesAndReferencesNoneAgreeOnOneRecord()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "PERK" },
                                     references: new[] { _w.AbilityFid, "!" + _w.SoundPerkFid });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    /// <summary>The refused effect's own conditions are parsed by Mutagen's condition parser over the raw bytes.
    /// This target is named ONLY there, so a scan that finds the perk through it is proof that parse ran — and the
    /// only proof, since no other field on the record mentions it.</summary>
    [Fact]
    public void AScanReachesTheRecordThroughALinkInsideTheRefusedEffectsConditions()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "PERK" }, references: new[] { _w.ConditionTargetFid });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        // The lenient note names the record too, so the MATCH COUNT is what proves it was reached.
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    /// <summary>The shape #301 is written around: references= with no types=, answered off the reverse-reference
    /// index. The record has to be a key in the index at all, which is a different walk from the scoped scan's.</summary>
    [Fact]
    public void AnUnboundedReferencesScanFindsTheRecordThroughTheReverseIndex()
    {
        var r = RecordsTools.Records(_w.Svc, references: new[] { _w.ConditionTargetFid });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.Contains("read leniently", r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    /// <summary>The same question with the universe bounded by formids= instead — a third lane, which used to
    /// answer differently from the scoped one.</summary>
    [Fact]
    public void AFormidsUniverseScanAnswersTheSameWayTheScopedOneDoes()
    {
        var r = RecordsTools.Records(_w.Svc,
                                     formids: new[] { _w.InconsistentPerkFid, _w.SoundPerkFid },
                                     references: new[] { _w.ConditionTargetFid });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("scan: 1 match", r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.DoesNotContain("could not be scanned", r);
    }

    /// <summary>The transitive reverse WALK re-tests every index candidate against the winner body, so it has its
    /// own link walk. Left alone it would drop the record as an unreadable winner while references= listed it —
    /// the two spellings of the reverse question disagreeing about one record.</summary>
    [Fact]
    public void AReverseWalkReachesTheRecordTheReferencesScanFinds()
    {
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.ConditionTargetFid },
            walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 },
            project: new RecordsTools.RecordsProject { form = "summary" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        // The walk's own lenient line names the record too, so the SELECTION COUNT is what proves it was reached:
        // the seed plus the one referrer.
        Assert.Contains("selection = 2 record(s)", r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.DoesNotContain("whose winning plugin could not be read", r);
    }

    [Fact]
    public void AReferencesScanFindsTheRecordThroughItsReadableEffect()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "PERK" }, references: new[] { _w.AbilityFid });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(_w.InconsistentPerkFid, r);
        Assert.Contains(_w.SoundPerkFid, r);
        Assert.DoesNotContain("could not be scanned", r);
        // Degraded, never silent: the scan says the record was read leniently and what that did not reach.
        Assert.Contains("read leniently", r);
    }
}

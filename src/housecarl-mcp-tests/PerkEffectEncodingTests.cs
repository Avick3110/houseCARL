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
    /// <summary>A perk with nothing wrong with it, so a scan's answer is not one row wide.</summary>
    public string SoundPerkFid { get; }

    readonly string _priorCorpusPath;

    public PerkEncodingWorld()
    {
        _priorCorpusPath = CorpusRulebook.CorpusPath;
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

        var sound = master.Perks.AddNew();
        sound.EditorID = "HcPerkEncodingSound";
        sound.Effects.Add(new PerkAbilityEffect { Ability = ability.ToNullableLink() });

        // The one entry-point effect in the plugin, so the byte patch below hits exactly it. Its ability sibling is
        // written FIRST so the readable link sits on the far side of the refusal from the list's start.
        var inconsistent = master.Perks.AddNew();
        inconsistent.EditorID = "HcPerkEncodingInconsistent";
        inconsistent.Effects.Add(new PerkAbilityEffect { Ability = ability.ToNullableLink() });
        inconsistent.Effects.Add(new PerkEntryPointModifyActorValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.ModSpellMagnitude,
            ActorValue = ActorValue.Alteration,
            Value = 10f,
            Modification = PerkEntryPointModifyActorValue.ModificationType.MultiplyAVMult,   // function byte 13
        });

        InconsistentPerkFid = $"{inconsistent.FormKey.ID:X6}:{masterKey.FileName}";
        AbilityFid = $"{ability.FormKey.ID:X6}:{masterKey.FileName}";
        SoundPerkFid = $"{sound.FormKey.ID:X6}:{masterKey.FileName}";

        var path = Path.Combine(mods, "PerkEncodingMod", MasterName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        master.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        Assert.Equal(1, ProbeBytes.MakeEpftFunctionMismatch(path));

        // The fixture must still exhibit the fault, and only on the one effect, or a green below proves nothing.
        using (var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE))
        {
            var bad = overlay.Perks.First(p => p.FormKey == inconsistent.FormKey);
            Assert.ThrowsAny<Exception>(() => ((IFormLinkContainerGetter)bad).EnumerateFormLinks().Count());
            Assert.Equal(2, bad.Effects.Count);
            Assert.IsAssignableFrom<IPerkAbilityEffectGetter>(bad.Effects[0]);
            Assert.ThrowsAny<Exception>(() => bad.Effects[1]);
        }

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + MasterName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+PerkEncodingMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
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
        var r = RecordsTools.Records(
            _w.Svc, formids: new[] { _w.InconsistentPerkFid },
            project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects" }, depth = 3 });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        // The readable sibling is still read — the field did not fail as a whole.
        Assert.Contains("Effects[0]", r);
        Assert.Contains(_w.AbilityFid, r);
        // …and the refused one says which bytes disagree and what the parameter was decoded off.
        Assert.Contains("Effects[1] = (unreadable:", r);
        Assert.Contains("internally inconsistent", r);
        Assert.Contains("function byte 13", r);
        Assert.Contains("decoded off EPFT alone, as xEdit does: 10", r);
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

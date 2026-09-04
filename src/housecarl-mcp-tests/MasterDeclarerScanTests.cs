using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The identify pass against a plugin that DECLARES the transform target as a master and references none of
/// its records — a compat patch that exists to be load-ordered, or one whose overrides were trimmed. A walk over
/// record links and record identity cannot see it, and it loses a master the moment the donor is deactivated.</summary>
[Trait("tier", "integration")]
public sealed class MasterDeclarerScanTests : IDisposable
{
    readonly string _root;
    readonly ModKey _targetKey = new("HcDeclTarget", ModType.Plugin);
    readonly ModKey _declarerKey = new("HcDeclOnly", ModType.Plugin);
    readonly ModKey _referencerKey = new("HcDeclRef", ModType.Plugin);
    readonly string _targetPath;
    readonly string _declarerPath;
    readonly string _referencerPath;
    readonly FormKey _weaponKey;

    public MasterDeclarerScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-declarer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _weaponKey = new FormKey(_targetKey, 0x800);
        var target = new SkyrimMod(_targetKey, SkyrimRelease.SkyrimSE);
        target.Weapons.Add(new Weapon(_weaponKey, SkyrimRelease.SkyrimSE) { EditorID = "HcDeclWeapon" });
        target.ModHeader.Stats.NextFormID = 0x801;
        _targetPath = Path.Combine(_root, _targetKey.FileName.String);
        target.BeginWrite.ToPath(_targetPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        using var targetOverlay = SkyrimMod.CreateFromBinaryOverlay(_targetPath, SkyrimRelease.SkyrimSE);

        // The declarer: its own record, no link to anything the target defines, and the target carried in the master
        // table anyway — what an extra included master is for, and what a trimmed compat patch looks like on disk.
        var declarer = new SkyrimMod(_declarerKey, SkyrimRelease.SkyrimSE);
        declarer.Weapons.Add(new Weapon(new FormKey(_declarerKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcDeclOwn" });
        declarer.ModHeader.Stats.NextFormID = 0x801;
        _declarerPath = Path.Combine(_root, _declarerKey.FileName.String);
        declarer.BeginWrite.ToPath(_declarerPath).WithLoadOrder(new[] { targetOverlay })
                .WithExtraIncludedMasters(_targetKey).NoNextFormIDProcessing().Write();

        // A real referencer alongside it: every referencer declares the donor as a master too, so this is what keeps
        // the new category from simply repeating the referencer list.
        var referencer = new SkyrimMod(_referencerKey, SkyrimRelease.SkyrimSE);
        var list = new FormList(new FormKey(_referencerKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcDeclList" };
        list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(_weaponKey));
        referencer.FormLists.Add(list);
        referencer.ModHeader.Stats.NextFormID = 0x801;
        _referencerPath = Path.Combine(_root, _referencerKey.FileName.String);
        referencer.BeginWrite.ToPath(_referencerPath).WithLoadOrder(new[] { targetOverlay }).NoNextFormIDProcessing().Write();
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    RemapEngine.IdentifyResult Identify(LoadOrderResolver resolver)
        => RemapEngine.IdentifyExternalReferencers(
            resolver,
            new HashSet<FormKey> { _weaponKey },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _targetKey.FileName.String },
            readDeclaredMasters: true);

    /// <summary>The declarer must be found, named with what it declares, and must not be mistaken for a referencer:
    /// the pass reported "external referencers: none" for this plugin and the user lost a master at the swap.</summary>
    [Fact]
    public void ADeclarerOnlyDependentIsFoundAndNamed()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _targetPath, _declarerPath, _referencerPath });
        var id = Identify(resolver);

        var found = Assert.Single(id.MasterDeclarers!);
        Assert.Equal(_declarerKey.FileName.String, found.Plugin, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(_targetKey.FileName.String, Assert.Single(found.Declared), StringComparer.OrdinalIgnoreCase);

        // It links to nothing and overrides nothing, so the two existing categories are right to leave it out.
        Assert.DoesNotContain(_declarerKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(_declarerKey.FileName.String, id.ExternalOverriders, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A referencer declares the donor as a master by construction, so listing every declarer would repeat
    /// the referencer list under a new heading. Only the dependents the record walk did NOT find are reported.</summary>
    [Fact]
    public void AReferencerIsNotAlsoListedAsADeclarer()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _targetPath, _declarerPath, _referencerPath });
        var id = Identify(resolver);

        Assert.Contains(_referencerKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(id.MasterDeclarers!, d => string.Equals(d.Plugin, _referencerKey.FileName.String, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The category's claim is "declares a donor and references none of its records". A plugin the pass
    /// could not read through had no records walked, so it may not be put in that list — it is named, with its
    /// reason, in the unscannable accounting instead.
    ///
    /// <para>The CAUSE is asserted, not merely the presence of an entry: a held-open file is a file that will not
    /// open, and the report's remedy for that cause is "close the program holding it". Reported as an enumeration
    /// fault instead, the same plugin is told that closing programs will not help.</para></summary>
    [Fact]
    public void APluginThePassCouldNotReadIsNotCalledADeclarer()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _targetPath, _declarerPath });
        using var hold = HeldOpen.Hold(_declarerPath);
        var id = Identify(resolver);

        Assert.Empty(id.MasterDeclarers!);
        var bad = Assert.Single(id.UnscannablePlugins!);
        Assert.Equal(_declarerKey.FileName.String, bad.Plugin, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(RemapEngine.UnscannableCause.Unopenable, bad.Cause);
    }

    /// <summary>Detection is only half of it: the merge report has to NAME the plugin and what it declares, because
    /// the remedy — remove the stale master, or include it in the merge — is per plugin.</summary>
    [Fact]
    public void TheMergeReportNamesADeclarerAndWhatItDeclares()
    {
        var outcome = new WritePatchBuilder.MergeOutcome(
            true, null, Path.Combine(_root, "Merged.esp"), "Merged.esp",
            new[] { _targetKey.FileName.String }, Array.Empty<string>(), 1, 1,
            Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
            Array.Empty<string>(), Array.Empty<string>(), 3, 0, Array.Empty<string>(), 1024,
            MasterDeclarers: new[] { new RemapEngine.MasterDeclarer(_declarerKey.FileName.String, new[] { _targetKey.FileName.String }) });

        var rendered = HousecarlMcp.WriteTools.RenderMerge(outcome);
        Assert.Contains("DECLARE a donor as a MASTER", rendered);
        Assert.Contains(_declarerKey.FileName.String, rendered);
        Assert.Contains("declares " + _targetKey.FileName.String, rendered);
        // The pass line may no longer claim declared masters are unread — it reads them now.
        Assert.Contains("record identity and declared masters", rendered);
    }

    /// <summary>A plugin declaring nothing IN THE TRANSFORM SET is not a dependent of it at all — the filter over the
    /// declared master list, not the empty-header case. The scanned plugin here declares a real master
    /// (HcDeclTarget.esp) that the transform set does not contain, so a pass that reported every declared master
    /// would name it and this arm would fail; a plugin declaring nothing at all cannot tell the two apart.</summary>
    [Fact]
    public void APluginDeclaringAMasterOutsideTheTransformSetIsNotADeclarer()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _targetPath, _declarerPath, _referencerPath });
        var id = RemapEngine.IdentifyExternalReferencers(
            resolver, new HashSet<FormKey> { _weaponKey },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _referencerKey.FileName.String },
            readDeclaredMasters: true);

        Assert.Empty(id.MasterDeclarers!);
    }
}

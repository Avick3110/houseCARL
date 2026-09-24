using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Migrated from the skse-config-audit-guard probe's render arms: the config audit's summary keeps BROKEN
/// apart from INERT, never says DEAD, and the filter= did-you-mean pool carries referenced plugin names.</summary>
[Trait("tier", "unit")]
public sealed class SkseConfigRenderFramingTests
{
    static SkseAuditedRef Ref(string plugin, SkseRefVerdict v)
        => new(new SkseConfigRef($"0x1|{plugin}", SkseRefShape.FormToken, plugin, 0x1, "0x1", 1, null), v, null);

    static SkseConfigFileAudit File(string rel, string group, string provider, params SkseAuditedRef[] refs)
        => new(rel, Path.GetFileName(rel), group, provider, 1, Array.Empty<SkseProvider>(), refs, null);

    static string Render(string? filter, params SkseConfigFileAudit[] files)
        => SkseConfigAuditWire.Render(new SkseConfigAuditData(files, files.Length, Array.Empty<string>(), Array.Empty<string>(),
            false, Array.Empty<string>(), "TestProfile"), filter, 80_000);

    [Fact] // ② healthy+inert: no 'DEAD', shows ✓ no-broken + inert/optional-support framing
    public void InertReferencesAloneReadAsNoBrokenAndOptionalSupport()
    {
        var txt = Render(null,
            File(@"SKSE\Plugins\Foo\ok.ini", "Foo", "ModA", Ref("Skyrim.esm", SkseRefVerdict.Ok)),
            File(@"SKSE\Plugins\Bar\opt.ini", "Bar", "ModB", Ref("NotInstalled.esp", SkseRefVerdict.PluginMissing)));
        Assert.DoesNotContain("DEAD", txt);
        Assert.Contains("no broken references", txt);
        Assert.Contains("inert", txt);
        Assert.Contains("optional support", txt);
    }

    [Fact] // ② broken: 'BROKEN reference(s)' headline + '1 dangling', no 'DEAD'
    public void ADanglingReferenceHeadlinesAsBrokenWithItsCount()
    {
        var txt = Render(null, File(@"SKSE\Plugins\Foo\x.ini", "Foo", "ModA", Ref("Skyrim.esm", SkseRefVerdict.Dangling)));
        Assert.DoesNotContain("DEAD", txt);
        Assert.Contains("BROKEN reference(s)", txt);
        Assert.Contains("1 dangling", txt);
    }

    [Fact] // ② broken+inert: BROKEN headline + 'more inert' note
    public void BrokenAndInertTogetherNoteTheInertRemainderApart()
    {
        var txt = Render(null,
            File(@"SKSE\Plugins\Foo\x.ini", "Foo", "ModA", Ref("Skyrim.esm", SkseRefVerdict.Dangling)),
            File(@"SKSE\Plugins\Bar\y.ini", "Bar", "ModB", Ref("Absent.esp", SkseRefVerdict.PluginMissing)));
        Assert.Contains("BROKEN reference(s)", txt);
        Assert.Contains("more inert", txt);
    }

    [Fact] // ② reconciliation: mixed file's OK ref counted in 'accounted for' (okInMixed via notOk)
    public void AnOkReferenceInAMixedFileIsCountedInAccountedFor()
    {
        var txt = Render(null, File(@"SKSE\Plugins\Foo\mix.ini", "Foo", "ModA",
            Ref("Skyrim.esm", SkseRefVerdict.Ok), Ref("Skyrim.esm", SkseRefVerdict.Dangling)));
        Assert.Contains("1 more OK ref(s) in files that also carry a non-OK reference", txt);
    }

    [Fact] // ② all-clear: clean ✓ line, no 'dead'/'BROKEN' alarm
    public void ALoneOkReferenceReadsAllClear()
    {
        var txt = Render(null, File(@"SKSE\Plugins\Foo\ok.ini", "Foo", "ModA", Ref("Skyrim.esm", SkseRefVerdict.Ok)));
        Assert.Contains("nothing broken, nothing inert", txt);
        Assert.DoesNotContain("dead", txt);
        Assert.DoesNotContain("BROKEN", txt);
    }

    [Fact] // ③ filter typo of a referenced plugin → 'Did you mean' offers ZzTestRefPlugin.esp
    public void AFilterTypoOfAReferencedPluginIsOfferedThatPlugin()
    {
        // A transposed stem matches nothing, so the render falls to the did-you-mean path.
        var txt = Render("ZzTestRefPlugni", File(@"SKSE\Plugins\Foo\x.ini", "Foo", "ModA", Ref("ZzTestRefPlugin.esp", SkseRefVerdict.Ok)));
        Assert.Contains(@"nothing under SKSE\Plugins matched", txt);
        Assert.Contains("ZzTestRefPlugin.esp", txt);
    }
}

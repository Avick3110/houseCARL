using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>
/// The SNAM-marker world. HcSnamMaster.esm holds three Hello topics: <see cref="BlankTopic"/> with its marker left
/// blank (the #131 crash shape), <see cref="MarkedTopic"/> marked HELO, and <see cref="OverriddenTopic"/> marked HELO,
/// which HcSnamOverride.esp overrides with the marker blanked (a blank-SNAM override, as shipped in working mods).
/// </summary>
public sealed class DialogueSubtypeMarkerWorld : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-snam-marker-tests-" + Guid.NewGuid().ToString("N"));

    public FormKey BlankTopic { get; }
    public FormKey MarkedTopic { get; }
    public FormKey OverriddenTopic { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }

    public DialogueSubtypeMarkerWorld()
    {
        var instance = SyntheticInstance.Create(_root);
        ModsDir = Path.Combine(instance, "mods");

        var m = new SkyrimMod(new ModKey("HcSnamMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var blank = m.DialogTopics.AddNew(); blank.EditorID = "HcSnamBlank";
        blank.Subtype = DialogTopic.SubtypeEnum.Hello;
        var marked = m.DialogTopics.AddNew(); marked.EditorID = "HcSnamOk";
        marked.Subtype = DialogTopic.SubtypeEnum.Hello; marked.SubtypeName = new RecordType("HELO");
        var overridden = m.DialogTopics.AddNew(); overridden.EditorID = "HcSnamOverridable";
        overridden.Subtype = DialogTopic.SubtypeEnum.Hello; overridden.SubtypeName = new RecordType("HELO");
        BlankTopic = blank.FormKey; MarkedTopic = marked.FormKey; OverriddenTopic = overridden.FormKey;
        SyntheticInstance.WriteMod(instance, "MasterMod", m);

        var o = new SkyrimMod(new ModKey("HcSnamOverride", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var ov = o.DialogTopics.GetOrAddAsOverride(overridden);
        ov.SubtypeName = RecordType.Null;
        SyntheticInstance.WriteMod(instance, "OverrideMod", o, m);

        SyntheticInstance.WriteProfile(instance,
            new[] { "# header", "+OverrideMod", "+MasterMod" },
            new[] { "# header", "HcSnamMaster.esm", "HcSnamOverride.esp" },
            new[] { "*HcSnamMaster.esm", "*HcSnamOverride.esp" });

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Three active plugins, one per way a walked record's links fail to read (#1316), and a master holding one
/// spell. Each holds a FormList whose Items are [a head part, the perk, another head part], and the perk links a spell
/// (a readable ability effect: Lenient and Corrupt master the base spell, Dead has its own) and a
/// static (inside a condition of its one entry-point effect), each head part and the static listing a mesh. Dead also
/// holds a deleted FormList whose Items still read the static (<c>GoneList</c>) and a live FormList listing it
/// (<c>Holder</c>), and a deleted DialogTopic whose Quest still reads (<c>GoneTopic</c>, <c>Quest</c>) holding a live
/// response (<c>Info</c>). The perks are written by Mutagen and patched on disk:
/// <list type="bullet">
/// <item><see cref="Lenient"/>: the entry-point effect gets the encoding Mutagen refuses and xEdit reads
///   (<see cref="ProbeBytes.MakeEpftFunctionMismatch"/>), so its links read only leniently.</item>
/// <item><see cref="Dead"/>: the perk is DELETED and its EPFT corrupt (<see cref="ProbeBytes.CorruptEpftBytes"/>), so
///   its links throw, as ARR's deleted package overrides do.</item>
/// <item><see cref="Corrupt"/>: the EPFT is corrupt on a live perk, so its links throw and nothing is recovered.</item>
/// </list>
/// Built fresh per test class; copy writes its patch folder under mods, which a test then looks for.</summary>
public sealed class UnreadLinksWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-unread-links-");
    public string ModsDir => _mo2.ModsDir;
    public LoadOrderService Svc { get; }

    public const string BaseMaster = "HcIsoBase.esm";
    public const string Lenient = "HcIsoLenient.esp";
    public const string Dead = "HcIsoDead.esp";
    public const string Corrupt = "HcIsoCorrupt.esp";
    public const string BeforeNif = @"meshes\hciso\before.nif";
    public const string AfterNif = @"meshes\hciso\after.nif";
    public const string RecoveredNif = @"meshes\hciso\recovered.nif";

    /// <summary>By plugin: its list, perk, spell and static, keyed "List", "Perk", "Spell", "Static"; Dead's also "GoneList", "Holder", "GoneTopic", "Quest", "Info".</summary>
    public Dictionary<string, Dictionary<string, FormKey>> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An empty FormList in the master, for copy's target= lane.</summary>
    public FormKey Target { get; }

    public UnreadLinksWorld()
    {
        // The lenient and corrupt perks' ability spell lives in a master, so the asset walk counts it as a boundary, once per link.
        var baseMod = new SkyrimMod(ModKey.FromFileName(BaseMaster), SkyrimRelease.SkyrimSE);
        var baseSpell = baseMod.Spells.AddNew(); baseSpell.EditorID = "HcIsoBaseSpell";
        var target = baseMod.FormLists.AddNew(); target.EditorID = "HcIsoTarget";
        Target = target.FormKey;
        baseMod.BeginWrite.ToPath(_mo2.InMod("HcIsoBaseMod", baseMod.ModKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        foreach (var plugin in new[] { Lenient, Dead, Corrupt })
        {
            var key = ModKey.FromFileName(plugin);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            bool lenient = plugin == Lenient, mastered = plugin != Dead;
            var tag = key.Name;
            HeadPart Part(string eid, string nif)
            {
                var hp = mod.HeadParts.AddNew();
                hp.EditorID = eid;
                hp.Model = new Model { File = nif };
                return hp;
            }
            FormKey spell;
            if (mastered) spell = baseSpell.FormKey;
            else { var own = mod.Spells.AddNew(); own.EditorID = tag + "Spell"; spell = own.FormKey; }
            var stat = mod.Statics.AddNew(); stat.EditorID = tag + "Static";
            stat.Model = new Model { File = RecoveredNif };

            var before = Part(tag + "Before", BeforeNif);
            var perk = mod.Perks.AddNew();
            perk.EditorID = tag + "Perk";
            // The readable effect first, so Mutagen's own walk hands over its link before the refused one throws.
            perk.Effects.Add(new PerkAbilityEffect { Ability = new FormLinkNullable<ISpellGetter>(spell) });
            var refused = new PerkEntryPointModifyActorValue
            {
                EntryPoint = APerkEntryPointEffect.EntryType.ModSpellMagnitude,
                ActorValue = ActorValue.Alteration,
                Value = 10f,
                Modification = PerkEntryPointModifyActorValue.ModificationType.MultiplyAVMult,
            };
            var isId = new GetIsIDConditionData { RunOnType = Condition.RunOnType.Subject };
            isId.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(isId, stat.FormKey);
            refused.Conditions.Add(new PerkCondition
            {
                RunOnTabIndex = 0,
                Conditions = { new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1f, Data = isId } },
            });
            perk.Effects.Add(refused);
            var after = Part(tag + "After", AfterNif);

            var list = mod.FormLists.AddNew();
            list.EditorID = tag + "List";
            list.Items.Add(before.FormKey); list.Items.Add(perk.FormKey); list.Items.Add(after.FormKey);
            Keys[plugin] = new(StringComparer.Ordinal)
            {
                ["List"] = list.FormKey, ["Perk"] = perk.FormKey, ["Spell"] = spell, ["Static"] = stat.FormKey,
            };
            FormList? gone = null;
            DialogTopic? goneTopic = null;
            if (plugin == Dead)
            {
                // A deleted list whose Items still read, and a live list holding it, for the field-path walks.
                gone = mod.FormLists.AddNew(); gone.EditorID = tag + "GoneList"; gone.Items.Add(stat.FormKey);
                var holder = mod.FormLists.AddNew(); holder.EditorID = tag + "Holder"; holder.Items.Add(gone.FormKey);
                Keys[plugin]["GoneList"] = gone.FormKey;
                Keys[plugin]["Holder"] = holder.FormKey;
                // A deleted topic whose Quest still reads, holding a live response, for the '*parent.X' walk.
                var quest = mod.Quests.AddNew(); quest.EditorID = tag + "Quest";
                goneTopic = mod.DialogTopics.AddNew(); goneTopic.EditorID = tag + "GoneTopic";
                goneTopic.Quest.SetTo(quest);
                var info = new DialogResponses(mod) { EditorID = tag + "Info" };
                goneTopic.Responses.Add(info);
                Keys[plugin]["Quest"] = quest.FormKey;
                Keys[plugin]["GoneTopic"] = goneTopic.FormKey;
                Keys[plugin]["Info"] = info.FormKey;
            }

            var path = _mo2.InMod(tag + "Mod", key);
            mod.BeginWrite.ToPath(path).WithLoadOrder(mastered ? new ISkyrimModGetter[] { baseMod } : Array.Empty<ISkyrimModGetter>()).Write();
            // The lenient plugin has one master, so its own records sit at index 1 on disk.
            if (lenient) Assert.Equal(1, ProbeBytes.MakeEpftFunctionMismatch(path, (1u << 24) | perk.FormKey.ID));
            else
            {
                Assert.Equal(1, ProbeBytes.CorruptEpftBytes(path));
                if (plugin == Dead) Assert.Equal(1, ProbeBytes.SetDeletedFlag(path, "PERK", perk.FormKey.ID));
            }
            if (gone is not null) Assert.Equal(1, ProbeBytes.SetDeletedFlag(path, "FLST", gone.FormKey.ID));
            if (goneTopic is not null) Assert.Equal(1, ProbeBytes.SetDeletedFlag(path, "DIAL", goneTopic.FormKey.ID));

            // Each fixture must still show its fault, or a green below proves nothing.
            using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var bad = ov.Perks.First(p => p.FormKey == perk.FormKey);
            Assert.Equal(plugin == Dead, bad.IsDeleted);
            Assert.ThrowsAny<Exception>(() => ((IFormLinkContainerGetter)bad).EnumerateFormLinks().Count());
            Assert.Equal(plugin == Lenient, PerkEffectDecode.ReadLinks(bad) is not null);
            if (gone is not null)
            {
                var g = ov.FormLists.First(l => l.FormKey == gone.FormKey);
                Assert.True(g.IsDeleted);
                Assert.Equal(new[] { stat.FormKey }, g.Items.Select(i => i.FormKey));
            }
            if (goneTopic is not null)
            {
                var t = ov.DialogTopics.First(d => d.FormKey == goneTopic.FormKey);
                Assert.True(t.IsDeleted);
                Assert.Equal(Keys[plugin]["Quest"], t.Quest.FormKey);
                Assert.Equal(new[] { Keys[plugin]["Info"] }, t.Responses.Select(r => r.FormKey));
            }
        }

        _mo2.Profile(BaseMaster + "\r\n" + Lenient + "\r\n" + Dead + "\r\n" + Corrupt + "\r\n",
                     "*" + BaseMaster + "\r\n*" + Lenient + "\r\n*" + Dead + "\r\n*" + Corrupt + "\r\n",
                     "+HcIsoBaseMod\r\n+HcIsoLenientMod\r\n+HcIsoDeadMod\r\n+HcIsoCorruptMod\r\n");
        Svc = _mo2.Open();
    }

    /// <summary>The perk of <paramref name="plugin"/> read off its own file, for the door's own tests.</summary>
    public T ReadPerk<T>(string plugin, Func<IPerkGetter, T> read)
    {
        var path = _mo2.InMod(ModKey.FromFileName(plugin).Name + "Mod", ModKey.FromFileName(plugin));
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return read(ov.Perks.First(p => p.FormKey == Keys[plugin]["Perk"]));
    }

    public string Id(string plugin, string what) => FormIdToken.Of(Keys[plugin][what]);

    public void Dispose()
    {
        Svc.Dispose();
        _mo2.Delete();
    }
}

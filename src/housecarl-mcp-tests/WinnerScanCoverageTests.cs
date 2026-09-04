using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The whole-order winner scan against a plugin that becomes unreadable AFTER the index was built — held
/// open by xEdit, MO2 or the running game, with an unchanged last-write time so nothing rebuilds. Each tool that
/// scans the order that way must name the plugin, never render a clean scan over the rest. The file lock mechanism:
/// <c>docs/architecture/test-project-fixtures.md</c>.</summary>
[Trait("tier", "integration")]
public sealed class WinnerScanCoverageTests
{
    /// <summary>A throwaway MO2 instance a test owns outright, because a held file is unreadable to everything else
    /// in the process. A base plugin holds the magic effect and the quest; a second, later plugin — the one the tests
    /// hold open — holds a weapon, a spell carrying that effect, and a topic owned by that quest, so each of the
    /// three scans loses something it would otherwise report.</summary>
    sealed class ScanWorld : IDisposable
    {
        public const string BaseName = "HcWsBase.esp";
        public const string HeldName = "HcWsHeld.esp";

        public string Root { get; }
        public string HeldPath { get; }
        public FormKey Mgef { get; }
        public FormKey Quest { get; }
        public LoadOrderService Svc { get; }

        /// <summary>What <c>CorpusRulebook.CorpusPath</c> named before this world repointed it — restored on
        /// Dispose, which deletes the directory the repointed path names.</summary>
        readonly string _priorCorpusPath;

        public ScanWorld()
        {
            _priorCorpusPath = CorpusRulebook.CorpusPath;
            Root = Path.Combine(Path.GetTempPath(), "hc-winner-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
            var inst = Path.Combine(Root, "inst");
            var mods = Path.Combine(inst, "mods");
            Directory.CreateDirectory(Path.Combine(mods, "BaseMod"));
            Directory.CreateDirectory(Path.Combine(mods, "HeldMod"));

            var baseKey = ModKey.FromNameAndExtension(BaseName);
            var heldKey = ModKey.FromNameAndExtension(HeldName);

            var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
            var mgef = baseMod.MagicEffects.AddNew(); mgef.EditorID = "HcWsEffect";
            Mgef = mgef.FormKey;
            var quest = baseMod.Quests.AddNew(); quest.EditorID = "HcWsQuest";
            Quest = quest.FormKey;
            var basePath = Path.Combine(mods, "BaseMod", BaseName);
            baseMod.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

            var held = new SkyrimMod(heldKey, SkyrimRelease.SkyrimSE);
            var weapon = held.Weapons.AddNew(); weapon.EditorID = "HcWsHeldWeapon";
            var spell = held.Spells.AddNew(); spell.EditorID = "HcWsHeldSpell";
            var effect = new Effect();
            effect.BaseEffect.SetTo(Mgef);
            spell.Effects.Add(effect);
            var topic = held.DialogTopics.AddNew(); topic.EditorID = "HcWsHeldTopic";
            topic.Quest.SetTo(Quest);
            HeldPath = Path.Combine(mods, "HeldMod", HeldName);
            held.BeginWrite.ToPath(HeldPath).WithLoadOrder(new ISkyrimModGetter[] { baseMod }).Write();

            var genDir = Path.Combine(Root, "corpus-gen");
            CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
            CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

            File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
                "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
                + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");
            var prof = Path.Combine(inst, "profiles", "Default");
            Directory.CreateDirectory(prof);
            File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "# header\r\n" + BaseName + "\r\n" + HeldName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*" + BaseName + "\r\n*" + HeldName + "\r\n");
            File.WriteAllText(Path.Combine(prof, "modlist.txt"), "# header\r\n+HeldMod\r\n+BaseMod\r\n");

            Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
            Svc.Stats();                                  // build the index BEFORE any hold, which is the live shape
        }

        public void Dispose()
        {
            Svc.Dispose();
            CorpusRulebook.CorpusPath = _priorCorpusPath;
            try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>The type= scan streams winner bodies over the whole order, so a plugin it cannot open takes every
    /// record that plugin wins out of the answer. Zero matches with no note reads as "no such weapon exists".</summary>
    [Fact]
    public void TheCrossQueryScanNamesAPluginItCouldNotRead()
    {
        using var world = new ScanWorld();

        // Vacuity: unheld, the same call finds the weapon — so what follows is about the lock.
        var open = world.Svc.CrossQuery("Weapon", null, null, false, null, null, 50);
        Assert.Equal(1, open.Total);
        Assert.Null(open.ScanNote);

        using var hold = HeldOpen.Hold(world.HeldPath);
        var locked = world.Svc.CrossQuery("Weapon", null, null, false, null, null, 50);

        Assert.Equal(0, locked.Total);
        Assert.NotNull(locked.ScanNote);
        Assert.Contains(ScanWorld.HeldName, locked.ScanNote!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The effect chain scans the same winner stream for carriers, so a plugin it cannot open drops that
    /// plugin's carriers. A carrier-free chain is a real answer, which is exactly why the gap must be named.</summary>
    [Fact]
    public void TheEffectChainNamesAPluginItCouldNotRead()
    {
        using var world = new ScanWorld();

        var open = world.Svc.ResolveEffectChain(world.Mgef, null, 50);
        Assert.Equal(1, open.Total);
        Assert.Null(open.ScanNote);

        using var hold = HeldOpen.Hold(world.HeldPath);
        var locked = world.Svc.ResolveEffectChain(world.Mgef, null, 50);

        Assert.Equal(0, locked.Total);
        Assert.NotNull(locked.ScanNote);
        Assert.Contains(ScanWorld.HeldName, locked.ScanNote!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A quest input fans out over the winning topics, so a plugin the sweep cannot open takes its topics
    /// out of the report. A quest that validates clean on a short list is the misleading answer.</summary>
    [Fact]
    public void TheQuestFanOutNamesAPluginItCouldNotRead()
    {
        using var world = new ScanWorld();

        var open = world.Svc.ValidateDialogue(world.Quest);
        Assert.Single(open.Topics);
        Assert.DoesNotContain(open.InputIssues, i => i.Message.Contains(ScanWorld.HeldName, StringComparison.OrdinalIgnoreCase));

        using var hold = HeldOpen.Hold(world.HeldPath);
        var locked = world.Svc.ValidateDialogue(world.Quest);

        Assert.Empty(locked.Topics);
        Assert.Contains(locked.InputIssues, i => i.Message.Contains(ScanWorld.HeldName, StringComparison.OrdinalIgnoreCase));
    }
}

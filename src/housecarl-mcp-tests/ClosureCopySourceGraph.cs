using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>The closure-copy tests' in-memory graph: a head part and its texture set in a bound Source plugin,
/// walked from an NPC's HeadParts seed and internalized into an extended Patch whose counter sits past its floor
/// and which already holds a user head part deliberately pointing at the source texture set.</summary>
sealed class ClosureCopySourceGraph
{
    public static readonly ModKey Src = new("Source", ModType.Plugin);
    public static readonly ModKey Keep = new("Keep", ModType.Master);
    public static readonly ModKey PatchKey = new("Patch", ModType.Plugin);

    public static readonly FormKey Txst = new(Src, 0x800);
    public static readonly FormKey Hp = new(Src, 0x801);
    public static readonly FormKey FactionKey = new(Src, 0x802);
    public static readonly FormKey OutfitKey = new(Src, 0x803);
    public static readonly FormKey ClassKey = new(Src, 0x804);
    public static readonly FormKey SrcNpc = new(Src, 0x805);
    public static readonly FormKey Prior = new(PatchKey, 0x800);

    /// <summary>The extended patch's own counter, past the 0x801 floor its prior record implies.</summary>
    public const uint PatchCounter = 0x810;

    public SkyrimMod Patch { get; }
    public WalkResult Walk { get; }
    public CopyResult Copy { get; }

    public static bool IsBound(FormKey fk) => fk.ModKey == Src;

    public ClosureCopySourceGraph()
    {
        var bodies = SourceMod().EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (IMajorRecordGetter)r);
        var chain = SourceChain.Single(FileArm("Source.esp", fk => bodies.GetValueOrDefault(fk)));
        Walk = Run(chain);

        Patch = new SkyrimMod(PatchKey, SkyrimRelease.SkyrimSE);
        var prior = new HeadPart(Prior, SkyrimRelease.SkyrimSE) { EditorID = "UserPrior" };
        prior.TextureSet.SetTo(Txst);
        Patch.HeadParts.Add(prior);
        Patch.ModHeader.Stats.NextFormID = PatchCounter;

        Copy = ClosureCopy.Internalize(Patch, Walk.Reached);
    }

    /// <summary>Walk the graph from the NPC's HeadParts seed, expanding Source and keeping Keep as a link.</summary>
    public static WalkResult Run(SourceChain chain) => ClosureWalk.Run(
        new[] { new WalkSeed(Hp, "HeadParts", "Npc.HeadParts") }, chain,
        WalkScope.StandaloneFrom(new HashSet<ModKey> { Src }, fk => fk.ModKey == Keep),
        Array.Empty<WalkExclusion>());

    public static SourceArm FileArm(string spelling, Func<FormKey, IMajorRecordGetter?> fetch) =>
        new(spelling, SourceArmKind.File, $"file '{spelling}'", fetch);

    public static SkyrimMod SourceMod()
    {
        var src = new SkyrimMod(Src, SkyrimRelease.SkyrimSE);
        src.TextureSets.Add(new TextureSet(Txst, SkyrimRelease.SkyrimSE) { EditorID = "SrcTex" });
        var hp = new HeadPart(Hp, SkyrimRelease.SkyrimSE) { EditorID = "SrcHair" };
        hp.TextureSet.SetTo(Txst);
        src.HeadParts.Add(hp);
        src.Factions.Add(new Faction(FactionKey, SkyrimRelease.SkyrimSE) { EditorID = "SrcFaction" });
        src.Outfits.Add(new Outfit(OutfitKey, SkyrimRelease.SkyrimSE) { EditorID = "SrcOutfit" });
        src.Classes.Add(new Class(ClassKey, SkyrimRelease.SkyrimSE) { EditorID = "SrcClass" });
        return src;
    }

    public HeadPart HeadPartAt(FormKey key) => Patch.HeadParts.First(h => h.FormKey == key);
}

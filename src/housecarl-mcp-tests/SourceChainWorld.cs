using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>
/// The source-chain world. Base.esm defines 'Shared' and 'BaseOnly'; Over.esp (active, last) overrides 'Shared' and
/// winner.esp's own record; Donor.esp is in a DISABLED mod folder, overrides 'Shared' and alone defines 'DonorOnly';
/// winner.esp is a plugin actually named like the bare pole. Every version carries a distinct NAME, so which arm
/// answered is read off the value.
///
///   key         | Base.esm | winner.esp | Over.esp | Donor.esp (disabled)
///   Shared      |   B      |    —       |   O      |   D
///   BaseOnly    |   B      |    —       |   —      |   —
///   WinnerOnly  |   —      |    W       |   OW     |   —
///   DonorOnly   |   —      |    —       |   —      |   D
/// </summary>
public sealed class SourceChainWorld : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-source-chain-tests-" + Guid.NewGuid().ToString("N"));

    public static readonly ModKey BaseKey = new("Base", ModType.Master);
    public static readonly FormKey Shared = new(BaseKey, 0x800);
    public static readonly FormKey BaseOnly = new(BaseKey, 0x801);
    public static readonly FormKey WinnerOnly = new(new ModKey("winner", ModType.Plugin), 0x800);
    public static readonly FormKey DonorOnly = new(new ModKey("Donor", ModType.Plugin), 0xD01);

    /// <summary>A plain in-memory copy of Base's 'Shared', for a hand-built arm that answers with a body.</summary>
    public INpcGetter BaseShared { get; }

    public LoadOrderService Svc { get; }

    public SourceChainWorld()
    {
        var instance = SyntheticInstance.Create(_root);

        var baseMod = new SkyrimMod(BaseKey, SkyrimRelease.SkyrimSE);
        baseMod.Npcs.Add(new Npc(Shared, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "B" });
        baseMod.Npcs.Add(new Npc(BaseOnly, SkyrimRelease.SkyrimSE) { EditorID = "BaseOnly", Name = "B" });
        SyntheticInstance.WriteMod(instance, "BaseMod", baseMod);
        BaseShared = baseMod.Npcs.First();

        var winnerMod = new SkyrimMod(WinnerOnly.ModKey, SkyrimRelease.SkyrimSE);
        winnerMod.Npcs.Add(new Npc(WinnerOnly, SkyrimRelease.SkyrimSE) { EditorID = "WinnerOnly", Name = "W" });
        SyntheticInstance.WriteMod(instance, "WinnerNamedMod", winnerMod);

        var overMod = new SkyrimMod(new ModKey("Over", ModType.Plugin), SkyrimRelease.SkyrimSE);
        overMod.Npcs.Add(new Npc(Shared, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "O" });
        overMod.Npcs.Add(new Npc(WinnerOnly, SkyrimRelease.SkyrimSE) { EditorID = "WinnerOnly", Name = "OW" });
        SyntheticInstance.WriteMod(instance, "OverMod", overMod, baseMod, winnerMod);

        var donorMod = new SkyrimMod(DonorOnly.ModKey, SkyrimRelease.SkyrimSE);
        donorMod.Npcs.Add(new Npc(Shared, SkyrimRelease.SkyrimSE) { EditorID = "Shared", Name = "D" });
        donorMod.Npcs.Add(new Npc(DonorOnly, SkyrimRelease.SkyrimSE) { EditorID = "DonorOnly", Name = "D" });
        SyntheticInstance.WriteMod(instance, "DonorMod", donorMod, baseMod);

        SyntheticInstance.WriteProfile(instance,
            new[] { "+OverMod", "+WinnerNamedMod", "+BaseMod", "-DonorMod" },
            new[] { "Base.esm", "winner.esp", "Over.esp" },
            new[] { "*Base.esm", "*winner.esp", "*Over.esp" });
        File.WriteAllText(Path.Combine(instance, "profiles", "Default", "Skyrim.ini"), "[General]\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    /// <summary>Build a chain from <paramref name="poles"/> and hand it (or the refusal) to <paramref name="body"/> while
    /// its sources are open.</summary>
    public T Chain<T>(IReadOnlyList<string> poles, Func<SourceChain?, string?, T> body)
        => Svc.WithSourceChainForGuard(poles, "from_source", body);

    public static string? NameOf(SourceFetch f) => (f.Hit?.Body as INpcGetter)?.Name?.String;

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The two-donor shape from #387, built for real: <c>DisabledA</c> holds <c>Override.esp</c> (it overrides
/// the NPC), <c>DisabledB</c> holds <c>Donor.esp</c> (it DEFINES the NPC), the FaceGen sits beside the defining
/// plugin only, and both mods are switched off in MO2. It is the shape where the plugin filename alone does not tell
/// a caller which folder ships the file: the record comes from the first arm and the FaceGen from the second.</summary>
public sealed class TwoDisabledDonorsWorld : IDisposable
{
    public const string OverrideFolder = "DisabledA";
    /// <summary>An apostrophe and a parenthesis, both legal in a Windows folder name and both present in real mods
    /// (`JK's Skyrim`, `SkyUI (SE)`) — so the readback's delimiter has to survive them or the name it prints cannot
    /// be copied back into the placement.</summary>
    public const string DefiningFolder = "Bijin's NPCs (SE)";

    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }
    /// <summary>The NPC, defined in Donor.esp and overridden in Override.esp.</summary>
    public FormKey DonorNpc { get; }
    /// <summary>The FaceGen mesh path, Data-relative — its folder segment names Donor.esp, its bytes live in DisabledB.</summary>
    public string FaceRel { get; }
    public byte[] FaceBytes { get; } = Encoding.ASCII.GetBytes("FACEGEN-BESIDE-THE-DEFINING-PLUGIN");

    public TwoDisabledDonorsWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-two-donors-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "inst");
        ModsDir = Path.Combine(instance, "mods");
        var prof = Path.Combine(instance, "profiles", "Default");
        foreach (var d in new[] { ModsDir, Path.Combine(instance, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(instance, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseKey = new ModKey("HcBase", ModType.Master);
        var raceFk = new FormKey(baseKey, 0x800);
        var baseMod = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        baseMod.Races.Add(new Race(raceFk, SkyrimRelease.SkyrimSE) { EditorID = "HcTwoRace" });
        Write(baseMod, "BaseMod");

        var donorKey = new ModKey("Donor", ModType.Plugin);
        var hairFk = new FormKey(donorKey, 0x801);
        DonorNpc = new FormKey(donorKey, 0x805);
        var donor = new SkyrimMod(donorKey, SkyrimRelease.SkyrimSE);
        donor.HeadParts.Add(new HeadPart(hairFk, SkyrimRelease.SkyrimSE) { EditorID = "HcTwoHair" });
        var npc = new Npc(DonorNpc, SkyrimRelease.SkyrimSE) { EditorID = "HcTwoDonorNpc" };
        npc.Race.SetTo(raceFk);
        npc.HeadParts.Add(hairFk);
        donor.Npcs.Add(npc);
        Write(donor, DefiningFolder, baseMod);

        var ov = new SkyrimMod(new ModKey("Override", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ov.Npcs.GetOrAddAsOverride(npc);
        Write(ov, OverrideFolder, baseMod, donor);

        // The ONLY copy of the FaceGen, beside the DEFINING plugin — the second arm's folder.
        FaceRel = FaceGenPath.For(DonorNpc, FaceGenSlot.Mesh);
        var loose = Path.Combine(ModsDir, DefiningFolder, FaceRel);
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllBytes(loose, FaceBytes);

        File.WriteAllText(Path.Combine(prof, "modlist.txt"), $"+BaseMod\r\n-{OverrideFolder}\r\n-{DefiningFolder}\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    void Write(SkyrimMod mod, string folder, params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(ModsDir, folder);
        Directory.CreateDirectory(dir);
        mod.BeginWrite.ToPath(Path.Combine(dir, mod.ModKey.FileName)).WithLoadOrder(masters).Write();
    }

    public string Fid(FormKey fk) => $"{fk.ID:X6}:{fk.ModKey.FileName}";

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

/// <summary>#387: the copy readback names the MO2 mod folder each source arm resolved from, so the caller can hand
/// the right folder to the placement that carries the FaceGen. Nothing else on the surface knows which of two
/// disabled mods ships that file — the plugin filename in the FaceGen path names the plugin, not its folder.</summary>
[Trait("tier", "integration")]
public sealed class CopySourceFolderReadbackTests : IDisposable
{
    readonly TwoDisabledDonorsWorld _w = new();

    public void Dispose() => _w.Dispose();

    static readonly string[] Seeds = { "HeadParts", "HairColor", "HeadTexture", "WornArmor" };

    string Copy(string patch) => CopyTools.Copy(
        _w.Svc, _w.Fid(_w.DonorNpc), new[] { "Override.esp", "Donor.esp" }, Seeds,
        new[] { "Race:refuse" }, null, "HcTwoClone", patch, null);

    /// <summary>The folder named for a given source arm, read out of the readback the way a caller reads it. The
    /// delimiter is the double quote a Windows folder name cannot contain, so a name holding an apostrophe or a
    /// parenthesis still ends where the reader thinks it does.</summary>
    static string? FolderNamedFor(string readback, string spelling)
    {
        var m = Regex.Match(readback, Regex.Escape(spelling) + " \\(MO2 mod folder \"([^\"]+)\"\\)");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Fact]
    public void TheReadbackNamesTheModFolderBehindEverySourceArm()
    {
        var r = Copy("HcTwoFolders");

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Equal(TwoDisabledDonorsWorld.OverrideFolder, FolderNamedFor(r, "Override.esp"));
        Assert.Equal(TwoDisabledDonorsWorld.DefiningFolder, FolderNamedFor(r, "Donor.esp"));
        // The record itself came from the FIRST arm, and the readback says which — the two facts are separate, and
        // the folder that ships the FaceGen is the OTHER one.
        Assert.Contains("the source record was read from Override.esp", r);
    }

    /// <summary>The claim that matters: the folder the readback names for an arm is the folder that actually holds
    /// that plugin's files. Proven by following it — the placement that names it lands the FaceGen's real bytes,
    /// and the folder of the arm the RECORD came from does not supply the file at all.</summary>
    [Fact]
    public void TheFolderNamedForAnArmIsTheOneHoldingThatPluginsFaceGen()
    {
        var r = Copy("HcTwoCarry");
        var folder = FolderNamedFor(r, "Donor.esp");
        Assert.NotNull(folder);
        Assert.True(File.Exists(Path.Combine(_w.ModsDir, folder!, _w.FaceRel)),
                    $"the readback named '{folder}', which does not hold {_w.FaceRel}");

        var patchPath = Directory.EnumerateDirectories(_w.ModsDir, "houseCARL - HcTwoCarry*")
            .SelectMany(d => Directory.EnumerateFiles(d, "*.esp")).Single();
        var cloneKey = CloneKeyIn(patchPath);

        var placed = PlaceTools.Place(_w.Svc, new[]
        {
            new PlaceTarget { Formid = $"{cloneKey.ID:X6}:{cloneKey.ModKey.FileName}", Kind = "mesh",
                              Source = _w.FaceRel, SourceProvider = folder },
        }, into: Path.GetFileName(patchPath));
        Assert.False(placed.StartsWith("error:", StringComparison.Ordinal), "refused: " + placed.Split('\n')[0]);

        var carried = Directory.EnumerateFiles(Path.GetDirectoryName(patchPath)!, "*.nif", SearchOption.AllDirectories)
            .Single(f => f.Contains("facegeom", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(_w.FaceBytes, File.ReadAllBytes(carried));

        // …and the folder a caller would otherwise reach for — the one the RECORD came from — does not have it.
        var wrong = PlaceTools.Place(_w.Svc, new[]
        {
            new PlaceTarget { Formid = $"{cloneKey.ID:X6}:{cloneKey.ModKey.FileName}", Kind = "mesh",
                              Source = _w.FaceRel, SourceProvider = TwoDisabledDonorsWorld.OverrideFolder },
        }, patch: "HcTwoWrong");
        Assert.Contains(TwoDisabledDonorsWorld.OverrideFolder, wrong);
        Assert.Contains("does not supply", wrong);
    }

    static FormKey CloneKeyIn(string pluginPath)
    {
        var m = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        try
        {
            return m.Npcs.First(n => string.Equals(n.EditorID, "HcTwoClone", StringComparison.OrdinalIgnoreCase)).FormKey;
        }
        finally { (m as IDisposable)?.Dispose(); }
    }
}

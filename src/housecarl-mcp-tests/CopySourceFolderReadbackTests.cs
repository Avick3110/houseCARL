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

    /// <summary>A real mod folder whose NAME is one the placement surface reserves for a layer. Present only when
    /// the world is asked for it, because it is a collision, not the ordinary shape.</summary>
    public const string ReservedNameFolder = "Data";

    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }
    /// <summary>The NPC, defined in Donor.esp and overridden in Override.esp.</summary>
    public FormKey DonorNpc { get; }
    /// <summary>The FaceGen mesh path, Data-relative — its folder segment names Donor.esp, its bytes live in DisabledB.</summary>
    public string FaceRel { get; }
    public byte[] FaceBytes { get; } = Encoding.ASCII.GetBytes("FACEGEN-BESIDE-THE-DEFINING-PLUGIN");

    /// <param name="enableDefining">Tick the DEFINING mod and put its plugin in the active order — the enabled-donor
    /// shape, where the source resolves through the active order and still lives in exactly one mod folder.</param>
    /// <param name="reservedNameOverride">Also ship the override plugin from a second, disabled mod folder literally
    /// named <c>Data</c>.</param>
    public TwoDisabledDonorsWorld(bool enableDefining = false, bool reservedNameOverride = false)
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

        // The collision: the same override, shipped from a mod folder whose name is one the placement lane reserves.
        // Its own plugin filename, because two folders holding the same filename is a different refusal entirely.
        if (reservedNameOverride)
        {
            var collide = new SkyrimMod(new ModKey("Collide", ModType.Plugin), SkyrimRelease.SkyrimSE);
            collide.Npcs.GetOrAddAsOverride(npc);
            Write(collide, ReservedNameFolder, baseMod, donor);
            // A copy of the FaceGen inside that folder, so a placement handed the name "Data" and reaching the mod
            // folder would SUCCEED. It must not: the name means the layer, and the failure is the proof.
            var collideFace = Path.Combine(ModsDir, ReservedNameFolder, FaceGenPath.For(DonorNpc, FaceGenSlot.Mesh));
            Directory.CreateDirectory(Path.GetDirectoryName(collideFace)!);
            File.WriteAllBytes(collideFace, Encoding.ASCII.GetBytes("FACEGEN-IN-THE-RESERVED-NAME-FOLDER"));
        }

        // The ONLY copy of the FaceGen, beside the DEFINING plugin — the second arm's folder.
        FaceRel = FaceGenPath.For(DonorNpc, FaceGenSlot.Mesh);
        var loose = Path.Combine(ModsDir, DefiningFolder, FaceRel);
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllBytes(loose, FaceBytes);

        var reservedLine = reservedNameOverride ? $"-{ReservedNameFolder}\r\n" : "";
        File.WriteAllText(Path.Combine(prof, "modlist.txt"),
            $"+BaseMod\r\n{reservedLine}-{OverrideFolder}\r\n{(enableDefining ? "+" : "-")}{DefiningFolder}\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"),
            enableDefining ? "HcBase.esm\r\nDonor.esp\r\n" : "HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"),
            enableDefining ? "*HcBase.esm\r\n*Donor.esp\r\n" : "*HcBase.esm\r\n");
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

/// <summary>The ENABLED donor: a named source that resolves through the active order still sits in exactly one mod
/// folder, and the placement that carries its FaceGen needs that folder just as much as a disabled donor's. Only the
/// bare 'winner' pole is the whole order and has no folder to name.</summary>
[Trait("tier", "integration")]
public sealed class CopyEnabledDonorFolderTests : IDisposable
{
    readonly TwoDisabledDonorsWorld _w = new(enableDefining: true);

    public void Dispose() => _w.Dispose();

    static readonly string[] Seeds = { "HeadParts", "HairColor", "HeadTexture", "WornArmor" };

    string Copy(string source, string patch) => CopyTools.Copy(
        _w.Svc, _w.Fid(_w.DonorNpc), new[] { source }, Seeds,
        new[] { "Race:refuse" }, null, "HcEnabledClone", patch, null);

    [Fact]
    public void AnActivePluginSourceNamesTheModFolderItLivesIn()
    {
        var r = Copy("Donor.esp", "HcEnabledFolder");

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains(
            $"Donor.esp (from the active load order, MO2 mod folder \"{TwoDisabledDonorsWorld.DefiningFolder}\")", r);
    }

    /// <summary>…and the folder it names is the one that really holds that plugin's FaceGen — the same proof the
    /// disabled shape gets, because an enabled mod's file can still be contested by a replacer above it.</summary>
    [Fact]
    public void TheFolderNamedForAnActiveArmHoldsThatPluginsFaceGen()
    {
        var r = Copy("Donor.esp", "HcEnabledCarry");
        var m = Regex.Match(r, "Donor\\.esp \\(from the active load order, MO2 mod folder \"([^\"]+)\"\\)");

        Assert.True(m.Success, r);
        Assert.True(File.Exists(Path.Combine(_w.ModsDir, m.Groups[1].Value, _w.FaceRel)),
                    $"the readback named '{m.Groups[1].Value}', which does not hold {_w.FaceRel}");
    }

    /// <summary>The pole that genuinely has no folder still says so, rather than borrowing the winning plugin's.
    /// 'winner' is the whole order, and the record's winner can differ per key.</summary>
    [Fact]
    public void TheWinnerPoleStillNamesNoFolder()
    {
        var r = Copy("winner", "HcEnabledWinner");

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains("winner (from the active load order)", r);
        Assert.DoesNotContain("winner (from the active load order,", r);
    }
}

/// <summary>A real mod folder whose name is one the placement surface reserves. The readback may not call it the
/// layer it merely spells like, and may not hand the name back as a provider the placement would answer from the
/// layer instead.</summary>
[Trait("tier", "integration")]
public sealed class CopyReservedFolderNameTests : IDisposable
{
    readonly TwoDisabledDonorsWorld _w = new(reservedNameOverride: true);

    public void Dispose() => _w.Dispose();

    static readonly string[] Seeds = { "HeadParts", "HairColor", "HeadTexture", "WornArmor" };

    [Fact]
    public void AModFolderNamedDataIsNotDescribedAsTheGamesDataFolder()
    {
        var r = CopyTools.Copy(
            _w.Svc, _w.Fid(_w.DonorNpc), new[] { "Collide.esp", "Donor.esp" }, Seeds,
            new[] { "Race:refuse" }, null, "HcReservedClone", "HcReservedFolder", null);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r.Split('\n')[0]);
        Assert.Contains($"Collide.esp (MO2 mod folder \"{TwoDisabledDonorsWorld.ReservedNameFolder}\"", r);
        Assert.DoesNotContain("Collide.esp (the game's Data folder", r);
        // …and the name is withdrawn in the same breath, because a placement handed it reads the layer.
        Assert.Contains("RESERVED", r);
        Assert.Contains("Rename that mod folder in MO2", r);
    }

    /// <summary>The claim behind the withdrawal, proven rather than asserted: that mod folder HOLDS the file, and
    /// the placement still cannot get at it by name — the name means the layer, and the folder is never scanned.</summary>
    [Fact]
    public void ThePlacementLaneRefusesThatNameAsAProvider()
    {
        Assert.True(AssetResolver.IsReservedLayerName(TwoDisabledDonorsWorld.ReservedNameFolder));
        Assert.True(File.Exists(Path.Combine(_w.ModsDir, TwoDisabledDonorsWorld.ReservedNameFolder, _w.FaceRel)));

        var placed = PlaceTools.Place(_w.Svc, new[]
        {
            new PlaceTarget { Formid = _w.Fid(_w.DonorNpc), Kind = "mesh",
                              Source = _w.FaceRel, SourceProvider = TwoDisabledDonorsWorld.ReservedNameFolder },
        }, patch: "HcReservedPlace");

        Assert.Contains("placed 0 of 1", placed);
    }
}

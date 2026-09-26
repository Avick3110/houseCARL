using System.Text.Json;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>#926: a mesh whose corrupted count or size once ran NiflySharp 1.1.0 out of memory comes back from the
/// built server's housecarl_nif_inspect and housecarl_nif_set as a named error inside the 30 s call timeout. The server
/// runs under a 2 GB GC heap cap, so a regression fails its own call instead of taking the test host down.</summary>
[Trait("tier", "stdio")]
public sealed class NifInspectMalformedTests : IClassFixture<MalformedMeshWorld>
{
    readonly MalformedMeshWorld _w;
    public NifInspectMalformedTests(MalformedMeshWorld w) => _w = w;

    // The first input the random 4-byte corruption of the authored mesh ran out of memory on (seed 10): a count past its block's stored size.
    [Fact]
    public void TheSeedTenCorruptionIsANamedBlockSizeErrorNotARunaway()
    {
        var text = _w.Inspect(MalformedMeshWorld.SeedTen);

        Assert.Contains("the mesh is malformed and was not read (Block 0 (NiNode): A list count of 117 needs at least 468 bytes", text);
        Assert.Contains("size table", text);
    }

    // Byte 385 alone is the low byte of the root node's effect count; 0x75 makes it 117 refs, past the block's end.
    [Fact]
    public void TheRootEffectCountByteIsANamedBlockSizeErrorNotARunaway()
    {
        var text = _w.Inspect(MalformedMeshWorld.EffectCountByte);

        Assert.Contains("the mesh is malformed and was not read (Block 0 (NiNode): A list count of 117 needs at least 468 bytes", text);
        Assert.Contains("size table", text);
    }

    // A header count is refused before any block is read, so its message carries no block.
    [Fact]
    public void AHeaderBlockCountLargerThanTheFileIsANamedErrorWithNoBlock()
    {
        var text = _w.Inspect(MalformedMeshWorld.HeaderCount);

        Assert.Contains($"the mesh is malformed and was not read (The header's block count of {int.MaxValue} needs", text);
        Assert.Contains("damaged or cut short", text);
    }

    // The write path refuses the same mesh by name and says the disk is unchanged.
    [Fact]
    public void NifSetRefusesAMalformedMeshAndSaysNothingWasWritten()
    {
        var text = _w.Set(MalformedMeshWorld.SeedTen);

        Assert.Contains("the mesh is malformed and was not read (Block 0 (NiNode): A list count of 117", text);
        Assert.Contains("Nothing was written.", text);
    }
}

/// <summary>Its own instance: one mod holds the malformed meshes, built from NifInspectFixtures.BuildSyntheticSe plus
/// the byte edits below (the recipe is the fixture; no binary), and the server is configured on it once.</summary>
public sealed class MalformedMeshWorld : IDisposable
{
    public const string SeedTen = @"meshes\hc926\seed10.nif";
    public const string EffectCountByte = @"meshes\hc926\byte385.nif";
    public const string HeaderCount = @"meshes\hc926\headercount.nif";

    readonly string _root;
    readonly ServerFixture _server;

    public MalformedMeshWorld()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-malformed-" + Guid.NewGuid().ToString("N"));
        ServerFixture? server = null;
        // xUnit does not dispose a fixture whose constructor threw, so a failure here stops the server and removes the tree itself.
        try
        {
            var instance = Stage(_root);
            // 2 GB of GC heap: a runaway allocation fails inside the server, never in the test host.
            server = new ServerFixture(new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = "0x80000000" })
            {
                RpcTimeout = TimeSpan.FromSeconds(30),
            };
            var set = server.Call(ToolNames.SetMo2Instance, $$"""{"path": {{JsonSerializer.Serialize(instance)}}}""");
            Assert.Contains("configured houseCARL -> MO2 instance", set.Text, StringComparison.Ordinal);
            _server = server;
        }
        catch
        {
            server?.Dispose();
            try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
            throw;
        }
    }

    // Writes the instance under root: two masters and one mod holding the malformed meshes; returns the instance folder.
    static string Stage(string root)
    {
        var instance = Path.Combine(root, "instance");
        var profile = Path.Combine(instance, "profiles", "Default");
        var pluginMod = Path.Combine(instance, "mods", "PluginMod");
        var meshMod = Path.Combine(instance, "mods", "MalformedMod");
        foreach (var d in new[] { profile, Path.Combine(root, "game", "Data"), pluginMod })
            Directory.CreateDirectory(d);

        // Two masters, so the order resolves: an instance with no active plugin answers differently.
        foreach (var name in new[] { "HcMalformedOne", "HcMalformedTwo" })
        {
            var key = new ModKey(name, ModType.Master);
            var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
            mod.Weapons.AddNew().EditorID = name + "Weapon";
            mod.BeginWrite.ToPath(Path.Combine(pluginMod, key.FileName.String))
               .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        }

        WriteMesh(meshMod, SeedTen, Edit((948, 0xC0), (756, 0xB1), (719, 0x4C), (385, 0x75)));
        WriteMesh(meshMod, EffectCountByte, Edit((385, 0x75)));
        var header = NifInspectFixtures.BuildSyntheticSe();
        // After the version line: file version (4), endian (1), user version (4), then the block count.
        BitConverter.GetBytes(int.MaxValue).CopyTo(header, Array.IndexOf(header, (byte)'\n') + 1 + 4 + 1 + 4);
        WriteMesh(meshMod, HeaderCount, header);

        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\nHcMalformedOne.esm\r\nHcMalformedTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*HcMalformedOne.esm\r\n*HcMalformedTwo.esm\r\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+MalformedMod\r\n+PluginMod\r\n");
        File.WriteAllText(Path.Combine(profile, "Skyrim.ini"), "[Archive]\r\nsResourceArchiveList=\r\n");
        return instance;
    }

    public string Inspect(string meshPath)
        => _server.Call(ToolNames.NifInspect, $$"""{"mesh_paths": [{{JsonSerializer.Serialize(meshPath)}}]}""").Text;

    public string Set(string meshPath)
        => _server.Call(ToolNames.NifSet,
            $$"""{"mesh_path": {{JsonSerializer.Serialize(meshPath)}}, "op": "set_scale", "target": "GuardShape", "scale": "2"}""").Text;

    static byte[] Edit(params (int Pos, byte Val)[] edits)
    {
        var bytes = NifInspectFixtures.BuildSyntheticSe();
        Assert.Equal(998, bytes.Length);
        foreach (var (pos, val) in edits)
            bytes[pos] = val;
        return bytes;
    }

    static void WriteMesh(string mod, string rel, byte[] bytes)
    {
        var path = Path.Combine(mod, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}

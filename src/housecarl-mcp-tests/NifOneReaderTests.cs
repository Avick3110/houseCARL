using System.Text.RegularExpressions;
using HousecarlCore;
using HousecarlMcp;
using NiflySharp;
using NiflySharp.Blocks;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>nif_inspect's paths section and asset_status links= read a mesh's links through one reader and print them
/// in one line form: the same files, each with the same Data path and standing (#1276).</summary>
[Trait("tier", "integration")]
public sealed class NifOneReaderTests : IDisposable
{
    const string Cape = @"meshes\hc\cape.nif";
    const string Bare = @"meshes\hc\bare.nif";
    const string Fo4 = @"meshes\hc\fo4.nif";
    const string Diffuse = @"textures\hc\cape.dds";
    const string Normal = @"textures\hc\cape_n.dds";
    const string Shared = @"textures\hc\glow.dds";
    const string Hidden = @"textures\hc\x.dds.mohidden";
    const string LowerDiffuse = @"textures\hc\lower.dds";
    const string Smp = @"SKSE\Plugins\hdtSkinnedMeshConfigs\hccape.xml";
    const string BodyTri = @"hc\cape.tri";

    readonly PlaceInstance _p = new();
    readonly LoadOrderService _svc;

    public NifOneReaderTests()
    {
        // The glow shape's effect texture comes first, so the same file at the body's slot 4 is its second site.
        var top = _p.Mod("Top");
        PlaceInstance.Loose(top, Cape, Mesh(new[] { Diffuse, Normal, "\n", "  ", Shared, Hidden }, Shared, Smp, BodyTri));
        PlaceInstance.Loose(top, Bare, Mesh(Array.Empty<string>(), null, null, null));
        foreach (var rel in new[] { Diffuse, Shared, Smp, @"meshes\" + BodyTri, Hidden })
            PlaceInstance.Loose(top, rel, "x"u8.ToArray());
        var lower = _p.Mod("Lower");
        // Lower's copy names a file both mods ship, so its standing shows whose resolve it is.
        PlaceInstance.Loose(lower, Cape, Mesh(new[] { LowerDiffuse, Diffuse }, null, null, null));
        PlaceInstance.Loose(top, Fo4, NifInspectFixtures.BuildSyntheticSe(streamVersion: 130));
        PlaceInstance.Loose(lower, LowerDiffuse, "y"u8.ToArray());
        PlaceInstance.Loose(lower, Diffuse, "y"u8.ToArray());
        _p.Profile(Array.Empty<string>(), Array.Empty<string>(), new[] { "+Top", "+Lower" });
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    [Fact]
    public void PathsAndLinksListTheSameFilesWithTheSameDataPathAndStanding()
    {
        var paths = Paths(Cape);
        var links = Links(Cape);

        Assert.Equal(DataLines(links), DataLines(paths));
        foreach (var site in new[]
        {
            "smp — root extra data 'HDT Skinned Mesh Physics Object': `" + Smp + "`",
            "bodytri — root extra data 'BODYTRI': `" + BodyTri + "`",
            "texture — shape 'Glow' effect source texture: `" + Shared + "`",
            "texture — shape 'Body' texture slot 0 (Diffuse): `" + Diffuse + "`",
        })
            Assert.Contains(site, paths, StringComparison.Ordinal);
        Assert.Contains("data path: " + Smp + "  WINS: \"Top\" (loose)", paths, StringComparison.Ordinal);
        Assert.Contains("data path: " + @"meshes\" + BodyTri + "  WINS: \"Top\" (loose)", paths, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWhitespaceSlotIsNoLinkOnEitherSurfaceAndTheShapesSectionStillPrintsIt()
    {
        foreach (var text in new[] { Paths(Cape), Links(Cape) })
        {
            Assert.DoesNotContain("texture slot 2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("texture slot 3", text, StringComparison.Ordinal);
            Assert.DoesNotContain("tex[2]", text, StringComparison.Ordinal);
        }
        var shapes = NifTools.NifInspect(_svc, mesh_paths: new[] { Cape }, sections: "shapes");
        Assert.Contains("tex[2]: \n\n", shapes, StringComparison.Ordinal);
        Assert.Contains("tex[3]:   \n", shapes, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNamedAtTwoSitesIsListedAtBothInPathsAndOnceInLinks()
    {
        var paths = Paths(Cape);
        var links = Links(Cape);

        Assert.Contains("texture — shape 'Glow' effect source texture: `" + Shared + "`", paths, StringComparison.Ordinal);
        Assert.Contains("texture — shape 'Body' texture slot 4: `" + Shared + "`", paths, StringComparison.Ordinal);
        Assert.Equal(1, CheckMergeFixture.Count(links, "`" + Shared + "`"));
        Assert.Contains("texture — shape 'Glow' effect source texture: `" + Shared + "`", links, StringComparison.Ordinal);
    }

    /// <summary>links= JSON keeps each file once at its first site, as its text does.</summary>
    [Fact]
    public void AFileNamedAtTwoSitesIsOnceInLinksJson()
    {
        using var json = System.Text.Json.JsonDocument.Parse(AssetTools.AssetStatus(_svc, asset_paths: new[] { Cape }, links: true, format: "json"));
        var shared = json.RootElement.GetProperty("results")[0].GetProperty("links").EnumerateArray()
                         .Where(l => l.GetProperty("raw").GetString() == Shared).ToList();
        Assert.Equal("shape 'Glow' effect source texture", Assert.Single(shared).GetProperty("where").GetString());
    }

    /// <summary>paths names a slot past 0 where a Skyrim-layout shader determines it, and prints every slot of a
    /// foreign-layout shader bare.</summary>
    [Fact]
    public void PathsNamesTheSlotsTheShaderDeterminesAndNoOthers()
    {
        Assert.Contains("texture — shape 'Body' texture slot 1 (Normal): `" + Normal + "`", Paths(Cape), StringComparison.Ordinal);

        var fo4 = Paths(Fo4);
        Assert.Contains(@"texture slot 0: `textures\guard\diffuse.dds`", fo4, StringComparison.Ordinal);
        Assert.Contains(@"texture slot 1: `textures\guard\normal.dds`", fo4, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"texture slot \d+ \(", fo4);
    }

    [Fact]
    public void AHiddenLinkPrintsTheSameAbsentSentenceOnBothSurfaces()
    {
        var line = "data path: " + Hidden + "  ABSENT — hidden by MO2";
        var paths = Paths(Cape);
        Assert.Contains(line, paths, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Regex.Match(Links(Cape), Regex.Escape(Hidden) + "  ABSENT — [^\n]*").Value,
                     Regex.Match(paths, Regex.Escape(Hidden) + "  ABSENT — [^\n]*").Value);
    }

    [Fact]
    public void ANamedLowerCopySaysWhoseLinksAndThatTheStandingIsTheLoadOrders()
    {
        var paths = Paths(Cape, "Lower");

        Assert.Contains("  links read from \"Lower\" (loose); each Data path's standing is the load order's\n", paths, StringComparison.Ordinal);
        Assert.Contains("texture slot 0 (Diffuse): `" + LowerDiffuse + "`\n      data path: " + LowerDiffuse + "  WINS: \"Lower\" (loose)", paths, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("texture slot 1 (Normal): `" + Diffuse + "`\n      data path: " + Diffuse + "  WINS: \"Top\" (loose)", paths, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Smp, paths, StringComparison.Ordinal);
        Assert.Contains("  links read from \"Top\" (loose); each Data path's standing is the load order's\n", Paths(Cape), StringComparison.Ordinal);
    }

    [Fact]
    public void LinksWithAProviderNameTheWinningCopyAndWithoutOneStayAsTheyWere()
    {
        var named = AssetTools.AssetStatus(_svc, asset_paths: new[] { Cape }, links: true, provider: "Lower");
        Assert.Contains("  links (6), of the winning copy \"Top\" (loose):\n", named, StringComparison.Ordinal);
        Assert.Contains("  links (6):\n", Links(Cape), StringComparison.Ordinal);
    }

    [Fact]
    public void AMeshThatNamesNoFileSaysSo()
    {
        Assert.EndsWith("\n  (the mesh names no file)", Paths(Bare).TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("  links (0):\n", Links(Bare), StringComparison.Ordinal);
    }

    string Paths(string mesh, string source = "") => NifTools.NifInspect(_svc, mesh_paths: new[] { mesh }, sections: "paths", source_provider: source);

    string Links(string mesh) => AssetTools.AssetStatus(_svc, asset_paths: new[] { mesh }, links: true);

    /// <summary>The distinct Data path lines a render prints, each with its standing.</summary>
    static string[] DataLines(string text) => Regex.Matches(text, @"data path: [^\n]*").Select(m => m.Value)
                                                   .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>A two-shape SE mesh: 'Glow' drawn by an effect shader with <paramref name="effect"/>, then 'Body' whose
    /// lighting shader's texture set holds <paramref name="slots"/>; the root carries the SMP and BODYTRI strings given.</summary>
    static byte[] Mesh(IReadOnlyList<string> slots, string? effect, string? smp, string? bodyTri)
    {
        var f = new NifFile();
        f.Create(new NiVersion { FileVersion = NiVersion.ToFile("20.2.0.7"), UserVersion = 12, StreamVersion = 100 }, withRootNode: true);
        var root = f.GetRootNodes().First();
        root.Name = new NiStringRef("Root"); root.Flags_ui = 0xE;
        if (effect is not null)
        {
            var glow = new BSTriShape { Name = new NiStringRef("Glow"), Flags_ui = 0xE, Scale = 1f };
            root.Children.AddBlockRef(f.AddBlock(glow));
            var fx = new BSEffectShaderProperty { Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK, SourceTexture = new NiString4(effect, false) };
            glow.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(fx));
        }
        var texSet = new BSShaderTextureSet { Textures = slots.Select(s => new NiString4(s, false)).ToList() };
        texSet.NumTextures = (uint)texSet.Textures.Count;
        var shader = new BSLightingShaderProperty { Type = NiflySharp.Helpers.ShaderHelper.ShaderGameType.SK };
        shader.TextureSetRef = new NiBlockRef<BSShaderTextureSet>(f.AddBlock(texSet));
        var body = new BSTriShape { Name = new NiStringRef("Body"), Flags_ui = 0xE, Scale = 1f };
        body.ShaderPropertyRef = new NiBlockRef<BSShaderProperty>(f.AddBlock(shader));
        root.Children.AddBlockRef(f.AddBlock(body));
        foreach (var (key, value) in new[] { (NifLinks.SmpKey, smp), (NifLinks.BodyTriKey, bodyTri) })
        {
            if (value is null) continue;
            var sd = new NiStringExtraData { Name = new NiStringRef(key), StringData = new NiStringRef(value) };
            root.ExtraDataList ??= new NiBlockRefArray<NiExtraData>();
            root.ExtraDataList.AddBlockRef(f.AddBlock(sd));
            root.NumExtraDataList = (uint)root.ExtraDataList.Count;
        }
        using var ms = new MemoryStream();
        if (f.Save(ms) != 0) throw new InvalidOperationException("authoring the test mesh failed to save");
        return ms.ToArray();
    }
}

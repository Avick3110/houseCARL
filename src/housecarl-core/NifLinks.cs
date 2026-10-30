using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlCore;

/// <summary>What a file a NIF names is: a texture, an HDT-SMP physics XML, or a BODYTRI morph file. The kind decides
/// how the string becomes a Data path.</summary>
public enum NifLinkKind { Texture, Smp, BodyTri }

/// <summary>One file a mesh names: where in the mesh (<paramref name="Where"/>), the string as stored, its kind, and
/// the Data path it names, or why it names none.</summary>
public sealed record NifLink(string Where, string Raw, NifLinkKind Kind, string? DataPath, string? Error);

/// <summary>The file-to-file references of one mesh: every texture-set slot, every effect-shader texture, and the root
/// node's <c>HDT Skinned Mesh Physics Object</c> and <c>BODYTRI</c> string extra data. Each string goes through the one
/// link-to-Data-path door (docs/architecture/assets.md, From a link to a Data path).</summary>
public static class NifLinks
{
    /// <summary>The kind as every surface spells it: texture, smp, bodytri.</summary>
    public static string Token(this NifLinkKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The string extra data FSMP reads off the root node for a mesh's physics XML.</summary>
    public const string SmpKey = "HDT Skinned Mesh Physics Object";

    /// <summary>The string extra data that names a body's morph file, relative to meshes\.</summary>
    public const string BodyTriKey = "BODYTRI";

    /// <summary>Every link the mesh names, in block order, duplicates dropped; or why the mesh will not read.</summary>
    public static (IReadOnlyList<NifLink>? Links, string? Error) Read(byte[] bytes)
    {
        var (nif, err) = NifService.Load(bytes);
        if (nif is null) return (null, err);
        try { return (Collect(nif), null); }
        catch (Exception ex) { return (null, $"the mesh parsed but reading its links failed — {ex.GetType().Name}: {ex.Message}"); }
    }

    static List<NifLink> Collect(NifFile nif)
    {
        var links = new List<NifLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string where, string? raw, NifLinkKind kind)
        {
            if (string.IsNullOrWhiteSpace(raw) || !seen.Add(kind + "|" + raw)) return;
            try { links.Add(new NifLink(where, raw, kind, DataPathOf(raw, kind), null)); }
            catch (ArgumentException ex) { links.Add(new NifLink(where, raw, kind, null, ex.Message)); }
        }

        // FSMP reads only the root node's own extra data (hdtDefaultBBP.cpp scanBBP), so only the roots are read.
        foreach (var root in nif.GetRootNodes())
        {
            if (root.ExtraDataList is null) continue;
            foreach (var r in root.ExtraDataList.References)
                if (nif.GetBlock(r) is NiStringExtraData sd && sd.Name?.String is { } key)
                {
                    if (string.Equals(key, SmpKey, StringComparison.OrdinalIgnoreCase)) Add($"root extra data '{SmpKey}'", sd.StringData?.String, NifLinkKind.Smp);
                    else if (string.Equals(key, BodyTriKey, StringComparison.OrdinalIgnoreCase)) Add($"root extra data '{BodyTriKey}'", sd.StringData?.String, NifLinkKind.BodyTri);
                }
        }

        foreach (var shape in nif.GetShapes())
        {
            var name = shape.Name?.String ?? "";
            var shader = nif.GetShader(shape);
            if (shader?.TextureSetRef is not null && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
                for (int i = 0; i < ts.Textures.Count; i++)
                    Add($"shape '{name}' texture slot {i}", ts.Textures[i]?.Content, NifLinkKind.Texture);
            if (shader is BSEffectShaderProperty fx)
            {
                Add($"shape '{name}' effect source texture", fx.SourceTexture?.Content, NifLinkKind.Texture);
                Add($"shape '{name}' effect greyscale texture", fx.GreyscaleTexture?.Content, NifLinkKind.Texture);
                Add($"shape '{name}' effect env map texture", fx.EnvMapTexture?.Content, NifLinkKind.Texture);
                Add($"shape '{name}' effect normal texture", fx.NormalTexture?.Content, NifLinkKind.Texture);
                Add($"shape '{name}' effect env mask texture", fx.EnvMaskTexture?.Content, NifLinkKind.Texture);
            }
        }
        return links;
    }

    /// <summary>The Data path a link names: a texture through the typed door, an SMP XML as Data-relative (FSMP reads it
    /// from Data), a BODYTRI as meshes-relative. Throws <see cref="ArgumentException"/> naming the string.</summary>
    public static string DataPathOf(string raw, NifLinkKind kind) => kind switch
    {
        NifLinkKind.Texture => AssetDataPath.For(raw, AssetLinkKind.Texture),
        NifLinkKind.BodyTri => AssetDataPath.For(raw, AssetLinkKind.Model),
        _ => AssetResolver.ValidateRelPath(raw),
    };
}

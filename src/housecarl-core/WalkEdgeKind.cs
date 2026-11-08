namespace HousecarlCore;

/// <summary>How the asset walk reached a node, its <c>edge</c>: a seed, or one of the file edge kinds a record or a
/// file leads to. Every emit site names one of these, so no edge can be emitted that <c>walk.through</c> cannot name.</summary>
public enum WalkEdgeKind { Seed, Listed, Script, Seq, Inferred, Fragment, FaceGen, Voice, Lip, Texture, Smp, BodyTri, Parent }

/// <summary>The edge kinds' one spelling, the token every surface writes and <c>walk.through</c> reads.</summary>
public static class WalkEdgeKinds
{
    /// <summary>The kind as every surface spells it: seed, listed, script, …, bodytri, parent.</summary>
    public static string Token(this WalkEdgeKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>The kinds a walk can be narrowed to: every kind but <see cref="WalkEdgeKind.Seed"/>, which is always shown.</summary>
    public static readonly IReadOnlyList<WalkEdgeKind> FileKinds = Enum.GetValues<WalkEdgeKind>().Where(k => k != WalkEdgeKind.Seed).ToList();

    /// <summary>The kind a token spells, ignoring case, or null.</summary>
    public static WalkEdgeKind? Parse(string token)
        => Enum.GetValues<WalkEdgeKind>().Where(k => string.Equals(k.Token(), token.Trim(), StringComparison.OrdinalIgnoreCase))
               .Select(k => (WalkEdgeKind?)k).FirstOrDefault();

    /// <summary>The edge kind a mesh link is reached by.</summary>
    public static WalkEdgeKind Edge(this NifLinkKind kind) => kind switch
    {
        NifLinkKind.Texture => WalkEdgeKind.Texture,
        NifLinkKind.Smp => WalkEdgeKind.Smp,
        NifLinkKind.BodyTri => WalkEdgeKind.BodyTri,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a mesh link kind with no edge kind"),
    };
}

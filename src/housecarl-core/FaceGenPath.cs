using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// FaceGenPath — the FaceGen asset path as a PURE TRANSFORM of an NPC's FormKey: no load order, no runtime FormID,
// no file read. The transform itself, and why the folder is the defining master, are in docs/facegen.md.

/// <summary>Which FaceGen file: the head mesh (.nif under facegeom) or the face tint (.dds under facetint).</summary>
public enum FaceGenSlot { Mesh, Tint }

public static class FaceGenPath
{
    /// <summary>The Data-relative folder each slot's files live under, trailing separator included.</summary>
    public static string Root(FaceGenSlot slot) => slot == FaceGenSlot.Mesh
        ? @"meshes\actors\character\facegendata\facegeom\"
        : @"textures\actors\character\facegendata\facetint\";

    public static string Extension(FaceGenSlot slot) => slot == FaceGenSlot.Mesh ? ".nif" : ".dds";

    public static FaceGenSlot Other(FaceGenSlot slot) => slot == FaceGenSlot.Mesh ? FaceGenSlot.Tint : FaceGenSlot.Mesh;

    public static string Token(FaceGenSlot slot) => slot == FaceGenSlot.Mesh ? "mesh" : "tint";

    /// <summary>The Data-relative FaceGen path for one NPC FormKey + slot (docs/facegen.md). Backslash-separated.</summary>
    public static string For(FormKey fk, FaceGenSlot slot)
    {
        var master = fk.ModKey.FileName.ToString();          // the DEFINING plugin in the FormKey — never the winner
        var name = "00" + fk.ID.ToString("X6");              // index byte masked to 00, then the 6-hex local id
        return Root(slot) + master + "\\" + name + Extension(slot);
    }

    /// <summary>Both FaceGen paths for one NPC, mesh first — the dark-face pair placed together.</summary>
    public static IReadOnlyList<(FaceGenSlot Slot, string RelPath)> Both(FormKey fk)
        => new[] { (FaceGenSlot.Mesh, For(fk, FaceGenSlot.Mesh)), (FaceGenSlot.Tint, For(fk, FaceGenSlot.Tint)) };

}

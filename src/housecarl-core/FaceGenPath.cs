using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

// ======================================================================
//  FaceGenPath — the FaceGen asset path is a PURE TRANSFORM of an NPC's FormKey. NO load
//  order, NO runtime FormID, NO file read: just the FormKey → the two Data-relative paths
//  the game looks the NPC's generated head geometry + tint up under.
//
//  THE RULE (get it wrong and you resolve or place a DIFFERENT NPC's asset):
//    • FOLDER  = the DEFINING MASTER — the plugin in the FormKey 'XXXXXX:Plugin.esp'
//      (fk.ModKey.FileName), NOT the conflict winner. The CK writes an NPC's facegen under
//      the folder named for the plugin that DEFINES the record, so the path is stable across
//      load order (the record winner can change; the defining plugin in the FormKey cannot).
//    • FILE    = the load-order index byte MASKED to "00", then the 6-hex local FormID:
//      "00" + fk.ID("X6"). The index byte is masked because the file lives in the defining
//      plugin's OWN named folder — the index is redundant there, so the on-disk convention
//      normalizes it to 00 (the same masking xEdit applies to voice/InfoFileName paths).
//      fk.ID is Mutagen's 24-bit local id, so "00" + the 6 hex == the 8-hex filename the
//      CK / loose mods / CC BSAs use. e.g. 01A51A:Dawnguard.esm →
//      facegeom\Dawnguard.esm\0001A51A.nif.
//
//    • MESH (.nif): meshes\actors\character\facegendata\facegeom\<Master>\00<6hex>.nif
//    • TINT (.dds): textures\actors\character\facegendata\facetint\<Master>\00<6hex>.dds
//
//  Built in core (not in the place tool) so the read side (housecarl_asset_status and other
//  consumers) reuses the EXACT same transform, never two subtly-different copies.
//  Backslash-separated (the AssetResolver match form is
//  backslash, OrdinalIgnoreCase). Hex is UPPERCASE to match the CK's on-disk convention;
//  resolution is case-insensitive regardless.
// ======================================================================

/// <summary>Which FaceGen file: the head MESH geometry (.nif under facegeom) or the face TINT texture
/// (.dds under facetint). An NPC's appearance needs BOTH in sync — a dark/grey face is usually the tint
/// (facetint .dds) desynced from the geometry, but both are placed together to fully resolve it.</summary>
public enum FaceGenSlot { Mesh, Tint }

public static class FaceGenPath
{
    /// <summary>The Data-relative FaceGen path for one NPC FormKey + slot — the pure transform (see the file header).
    /// Folder = the defining master (fk.ModKey.FileName); file = "00" + the 6-hex local FormID. Backslash-separated.</summary>
    public static string For(FormKey fk, FaceGenSlot slot)
    {
        var master = fk.ModKey.FileName.ToString();          // the DEFINING plugin in the FormKey — never the winner
        var name = "00" + fk.ID.ToString("X6");              // index byte masked to 00, then the 6-hex local id
        return slot == FaceGenSlot.Mesh
            ? $@"meshes\actors\character\facegendata\facegeom\{master}\{name}.nif"
            : $@"textures\actors\character\facegendata\facetint\{master}\{name}.dds";
    }

    /// <summary>Both FaceGen paths for one NPC, mesh first — the dark-face pair placed together. Each entry is the
    /// slot and its Data-relative path.</summary>
    public static IReadOnlyList<(FaceGenSlot Slot, string RelPath)> Both(FormKey fk)
        => new[] { (FaceGenSlot.Mesh, For(fk, FaceGenSlot.Mesh)), (FaceGenSlot.Tint, For(fk, FaceGenSlot.Tint)) };
}

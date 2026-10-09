namespace HousecarlMcp;

/// <summary>One source for the parameter-description sentences the record write tools share (create, apply, forward,
/// copy, remove), so a lane reads the same on every tool that has it, and the patch=/into= refusal every writing lane shares.</summary>
internal static class LaneSentences
{
    /// <summary>patch=: the default name, on every lane whose default is 'Patch'.</summary>
    internal const string PatchDefault = "Base filename for the new patch (default 'Patch'). ";

    /// <summary>patch=: the auto-suffix and the shadow refusal, which fires only on a name the caller passed.</summary>
    internal const string PatchSuffix =
        "A name already taken gets a suffix, so a prior patch is never overwritten, except that a '<name>.esp' you " +
        "pass that your order is not loading (in another mod folder, the overwrite folder or game Data) is refused instead.";

    /// <summary>into=: what it is, on the lanes that add to a patch.</summary>
    internal const string IntoLead =
        "Filename of an existing houseCARL patch to extend instead of writing a new one, to build one patch across calls. ";

    /// <summary>into=: how the patch is found.</summary>
    internal const string IntoFound =
        "Found by filename even if its MO2 mod folder was renamed; for two patches sharing a filename, pass the " +
        "mod-folder name instead.";

    /// <summary>The refusal for patch= beside into= on a lane that writes a patch plugin, or null unless both are named.</summary>
    internal static string? PatchIntoRefusal(string? patch, string? into) =>
        string.IsNullOrWhiteSpace(patch) || string.IsNullOrWhiteSpace(into) ? null
        : $"patch='{patch}' names a NEW patch to write, but into='{into}' extends an existing one — the two lanes are " +
          "exclusive. Drop patch= to extend, or drop into= to write fresh.";

    /// <summary>The refusal for patch= beside into= on a lane that writes <paramref name="noun"/> into a mod folder, or null unless both are named.</summary>
    internal static string? PatchIntoFolderRefusal(string? patch, string? into, string noun) =>
        string.IsNullOrWhiteSpace(patch) || string.IsNullOrWhiteSpace(into) ? null
        : $"patch='{patch}' names a NEW mod folder for the {noun}, but into='{into}' names an existing houseCARL patch " +
          "to write into — the two lanes are exclusive. Drop patch= to write into that patch, or drop into= to make a new folder.";

    /// <summary>in_place=: follows the tool's own "the filename of an active plugin to ..." lead.</summary>
    internal const string InPlaceAnyPlugin = ", e.g. \"CoolWeapons.esp\", including one houseCARL did not author. ";

    /// <summary>in_place=: what an in-place write does to the file.</summary>
    internal const string InPlaceRewrite =
        "The original file is rewritten with no backup or undo; keep your own. The whole plugin is re-saved the way " +
        "xEdit or the Creation Kit save it. ";

    /// <summary>acknowledge=, whole.</summary>
    internal const string Acknowledge =
        "Confirms the in-place trade-off for the plugin named by in_place=. Needed only until the first in-place " +
        "write to that plugin lands (an edit, create, remove or forward); a refused call records nothing. Without it, " +
        "that first call returns a confirmation prompt instead of writing. It confirms consent only. Refused without in_place=.";

    /// <summary>dry_run=: what holds on every lane.</summary>
    internal const string DryRunLanes =
        "Works on every lane; an in-place dry run needs no acknowledge= and records no consent. A fault while saving " +
        "the file still shows only on the real call.";

    /// <summary>readback=: what the read-back is not.</summary>
    internal const string ReadbackIsTheFile =
        "The read-back is the written file, not the load order: a new patch wins nothing until enabled in MO2, and a " +
        "write into an existing mod keeps that mod's priority, so it may still need sorting above the current winner.";

    /// <summary>format=: the epoch stamp.</summary>
    internal const string Epoch =
        "A reply answered from an index build carries that build's epoch: epoch=<hex> in text, an 'epoch' member in " +
        "json. A refusal that consulted no build carries none.";

    /// <summary>max_chars=: the cut and the default.</summary>
    internal const string MaxCharsCut =
        "Rows past it are dropped with a notice; the write is unaffected. 0 = the server default (~40k)";
}

namespace HousecarlCore;

/// <summary>The Skyrim plugin filename extensions — the one home for the {.esp, .esm, .esl} set, matched
/// case-INSENSITIVELY.</summary>
public static class PluginFile
{
    /// <summary>The ONE definition; the per-class <c>PluginExts</c> fields alias this.</summary>
    public static readonly string[] Extensions = { ".esp", ".esm", ".esl" };
}

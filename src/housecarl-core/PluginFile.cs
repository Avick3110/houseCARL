namespace HousecarlCore;

/// <summary>
/// The single home for "which filename extensions are Skyrim plugins" — the {.esp, .esm, .esl} set. Every place that
/// decides whether a name is a plugin, or strips a plugin extension, reads THIS one array rather than restating the
/// set, so a fourth extension (or an exclusion) is a one-line change here instead of a fix that silently leaves the
/// load-order reader, the write-lane patch-name resolver, and the "did you mean?" suggester disagreeing (the
/// <see cref="EngineImplicit"/> "one shared home" discipline, for plugin extensions). Match case-INSENSITIVELY.
/// </summary>
public static class PluginFile
{
    /// <summary>The Skyrim plugin filename extensions — lowercase, leading dot. The ONE definition; the per-class
    /// <c>PluginExts</c> fields alias this so they can't diverge.</summary>
    public static readonly string[] Extensions = { ".esp", ".esm", ".esl" };
}

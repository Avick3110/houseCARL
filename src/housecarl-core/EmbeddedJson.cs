namespace HousecarlCore;

/// <summary>The ONE embedded-JSON resource reader — the SkyPatcher catalog and field map ship as
/// housecarl-core embedded resources and each hand-rolled an identical loader (deduped, PR #165
/// review cleanup). Throws loudly on a missing resource: a silently-empty load is exactly the Q3
/// degrade both call sites' Load() contracts forbid.</summary>
internal static class EmbeddedJson
{
    public static string Read(string fileName, string what)
    {
        var asm = typeof(EmbeddedJson).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{what} resource '{fileName}' is not embedded in housecarl-core.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}

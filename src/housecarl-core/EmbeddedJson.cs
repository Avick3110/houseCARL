namespace HousecarlCore;

/// <summary>The ONE embedded-JSON resource reader — the SkyPatcher catalog and field map both ship as
/// housecarl-core embedded resources. Throws loudly on a missing resource: a silently-empty load is
/// the degrade both call sites' Load() contracts forbid.</summary>
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

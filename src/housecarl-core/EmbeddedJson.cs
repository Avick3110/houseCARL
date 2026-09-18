namespace HousecarlCore;

/// <summary>The one embedded-JSON resource reader; throws loudly on a missing resource rather than loading empty.</summary>
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

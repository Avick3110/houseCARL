using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// plugins.txt is written by MO2 in the system ANSI code page, so a non-ASCII plugin name arrives as cp1252 or cp1251
/// bytes, not UTF-8; a file that is valid UTF-8 still reads as UTF-8. A misdecoded name drops out of plugins.txt, and a
/// plugin in loadorder.txt but not in plugins.txt is read as force-loaded, so an unticked plugin would load.
/// </summary>
[Trait("tier", "unit")]
public sealed class PluginsTxtEncodingTests : IDisposable
{
    const string Ticked = "Rüstung.esp";
    const string Unticked = "Épée.esp";
    const string TickedCyrillic = "Броня.esp";
    const string UntickedCyrillic = "Меч.esp";

    readonly string profileDir =
        Path.Combine(Path.GetTempPath(), "hc-plugins-encoding-" + Guid.NewGuid().ToString("N"));

    static Encoding CodePage(int cp) => CodePagesEncodingProvider.Instance.GetEncoding(cp)!;

    public PluginsTxtEncodingTests() => Directory.CreateDirectory(profileDir);

    public void Dispose()
    {
        try { Directory.Delete(profileDir, recursive: true); } catch (IOException) { }
    }

    Mo2Composition Compose(Encoding pluginsEncoding)
    {
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), $"# header\r\n{Ticked}\r\n{Unticked}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"# header\r\n*{Ticked}\r\n{Unticked}\r\n", pluginsEncoding);
        return Mo2LoadOrder.ReadComposition(profileDir);
    }

    // cp1252 is read in the machine's own ANSI code page, which is 1252 on this machine and on the CI runner.
    [Theory]
    [InlineData("cp1252")]
    [InlineData("utf-8")]
    public void PluginsTxtKeepsTicks(string written)
    {
        var comp = Compose(written == "cp1252" ? CodePage(1252) : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Contains(Ticked, comp.ActivePluginNames);
        Assert.Contains(Unticked, comp.InactivePluginNames);
        Assert.DoesNotContain(Unticked, comp.ActivePluginNames);
        Assert.DoesNotContain(Unticked, comp.ImplicitPluginNames);
    }

    // This machine's ANSI code page is 1252, so cp1251 is forced by handing the parse the code page a Russian Windows reports.
    [Fact]
    public void Cp1251PluginsTxtKeepsTicks()
    {
        var path = Path.Combine(profileDir, "plugins.txt");
        File.WriteAllText(path, $"# header\r\n*{TickedCyrillic}\r\n{UntickedCyrillic}\r\n", CodePage(1251));
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new List<string>();

        Mo2LoadOrder.ParsePlugins(path, active, inactive, CodePage(1251));

        Assert.Contains(TickedCyrillic, active);
        Assert.Contains(UntickedCyrillic, inactive);
        Assert.DoesNotContain(UntickedCyrillic, active);
    }

    // A UTF-8 BOM ahead of ANSI bytes is skipped, not taken as the file's encoding.
    [Fact]
    public void BomBeforeAnsiBytesKeepsAnsi()
    {
        var path = Path.Combine(profileDir, "plugins.txt");
        File.WriteAllBytes(path, [.. Encoding.UTF8.Preamble, .. CodePage(1252).GetBytes($"# header\r\n*{Ticked}\r\n{Unticked}\r\n")]);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new List<string>();

        Mo2LoadOrder.ParsePlugins(path, active, inactive, CodePage(1252));

        Assert.Equal([Ticked], active);
        Assert.Equal([Unticked], inactive);
    }
}

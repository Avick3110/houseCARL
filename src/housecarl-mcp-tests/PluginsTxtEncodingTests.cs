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

    // Names for the host's own ANSI page, first ones first: Œ is 0x8C in cp1252 and a control character in Latin-1.
    static readonly string[] HostNames = ["Œuvre.esp", "Épée.esp", "Rüstung.esp", TickedCyrillic, UntickedCyrillic, "鎧.esp", "剣.esp"];

    Mo2Composition Compose(Encoding pluginsEncoding, string ticked, string unticked)
    {
        File.WriteAllText(Path.Combine(profileDir, "modlist.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), $"# header\r\n{ticked}\r\n{unticked}\r\n");
        File.WriteAllText(Path.Combine(profileDir, "plugins.txt"), $"# header\r\n*{ticked}\r\n{unticked}\r\n", pluginsEncoding);
        return Mo2LoadOrder.ReadComposition(profileDir);
    }

    static void AssertTicks(Mo2Composition comp, string ticked, string unticked)
    {
        Assert.Contains(ticked, comp.ActivePluginNames);
        Assert.Contains(unticked, comp.InactivePluginNames);
        Assert.DoesNotContain(unticked, comp.ActivePluginNames);
        Assert.DoesNotContain(unticked, comp.ImplicitPluginNames);
    }

    // Written in the host's ANSI page (UTF-8 where Windows reports none) with names it can spell, so it passes on any host.
    [Fact]
    public void HostAnsiPluginsTxtKeepsTicks()
    {
        var host = CodePagesEncodingProvider.Instance.GetEncoding(0) ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var names = HostNames.Where(n => host.GetString(host.GetBytes(n)) == n).ToArray();
        Assert.True(names.Length >= 2, $"no two test names fit code page {host.CodePage}");

        AssertTicks(Compose(host, names[0], names[1]), names[0], names[1]);
    }

    [Fact]
    public void Utf8PluginsTxtKeepsTicks()
        => AssertTicks(Compose(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), Ticked, Unticked), Ticked, Unticked);

    // cp1251 is forced by handing the parse the code page a Russian Windows reports.
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

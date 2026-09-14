using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2 stores selected_profile and gamePath as QByteArrays, and Qt writes every non-ASCII byte as a
/// <c>\xHH</c> escape — quoting the whole value, wrapper included, when it holds <c>; , =</c> or an edge space. The
/// readers unescaped <c>\\</c> only, so a CJK profile name came back as the literal escape text and every tool
/// refused with "the active profile's folder is missing" (#733). Both readers now go through one Qt reader, and
/// <see cref="QtWrite"/> here is the fidelity anchor: it encodes the way QSettingsPrivate::iniEscapedString does.</summary>
[Trait("tier", "integration")]
public sealed class Mo2IniEscapeTests : IDisposable
{
    const string CjkProfile = "大肥鱼整合";
    const string CjkGameDir = "游戏";

    readonly string _root;

    public Mo2IniEscapeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-qtini-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ } }

    /// <summary>Serialize one value as a QByteArray the way Qt does: each byte named-escaped, hex-escaped when it is
    /// outside printable ASCII, or written as itself — and, after any hex escape, a FOLLOWING hex digit is hex-escaped
    /// too (Qt's escapeNextIfDigit), which is what makes the decoder's greedy hex run unambiguous. The whole
    /// serialized string, <c>@ByteArray(</c> prefix included, is then quoted when the value holds <c>; , =</c> or a
    /// leading/trailing space.</summary>
    static string QtWrite(string value)
    {
        var sb = new StringBuilder("@ByteArray(");
        bool escapeNextIfDigit = false;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            char c = (char)b;
            string? named = c switch
            {
                '\\' => @"\\", '"' => "\\\"", '\0' => @"\0", '\a' => @"\a", '\b' => @"\b",
                '\f' => @"\f", '\n' => @"\n", '\r' => @"\r", '\t' => @"\t", '\v' => @"\v",
                _ => null,
            };
            if (named is not null) { sb.Append(named); escapeNextIfDigit = c == '\0'; continue; }
            bool hex = b < 0x20 || b >= 0x7F || (escapeNextIfDigit && Uri.IsHexDigit(c));
            if (hex) { sb.Append(@"\x").Append(b.ToString("x")); escapeNextIfDigit = true; }
            else { sb.Append(c); escapeNextIfDigit = false; }
        }
        sb.Append(')');
        var text = sb.ToString();
        bool needsQuotes = value.Length > 0 && (value[0] == ' ' || value[^1] == ' ')
                           || value.Contains(';') || value.Contains(',') || value.Contains('=');
        return needsQuotes ? '"' + text + '"' : text;
    }

    /// <summary>Build an instance with the given profile name and game folder, both written Qt's way.</summary>
    string BuildInstance(string profile, string gameDir)
    {
        var instance = Path.Combine(_root, "instance-" + Guid.NewGuid().ToString("N")[..8]);
        var gamePath = Path.Combine(_root, gameDir, "Stock Game");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(gamePath, "Data"));
        var profileDir = Path.Combine(instance, "profiles", profile);
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "Skyrim.esm\n");
        File.WriteAllText(
            Mo2Instance.IniPath(instance),
            "[General]\r\ngameName=Skyrim Special Edition\r\n"
            + "selected_profile=" + QtWrite(profile) + "\r\n"
            + "gamePath=" + QtWrite(gamePath) + "\r\n",
            new UTF8Encoding(false));
        return instance;
    }

    /// <summary>A CJK profile name and a CJK game path — the reported failure (#733).</summary>
    [Fact]
    public void CjkProfileAndGamePathResolve()
    {
        var instance = BuildInstance(CjkProfile, CjkGameDir);
        var paths = Mo2Instance.Resolve(instance);
        Assert.Equal(CjkProfile, paths.ProfileName);
        Assert.Equal(Path.Combine(instance, "profiles", CjkProfile), paths.ProfileDir);
        Assert.Equal(Path.Combine(_root, CjkGameDir, "Stock Game", "Data"), paths.DataDir);
        Assert.True(Directory.Exists(paths.DataDir));
    }

    /// <summary>A comma in the profile name makes Qt quote the whole value, wrapper included.</summary>
    [Fact]
    public void CommaProfileResolvesThroughTheQuotedWrapper()
    {
        const string profile = "Requiem, AE";
        Assert.StartsWith("\"@ByteArray(", QtWrite(profile), StringComparison.Ordinal);
        var paths = Mo2Instance.Resolve(BuildInstance(profile, "Game"));
        Assert.Equal(profile, paths.ProfileName);
    }

    [Fact]
    public void CjkProfileReadsBackFromTheIni()
        => Assert.Equal(CjkProfile, Mo2Instance.ReadSelectedProfile(BuildInstance(CjkProfile, CjkGameDir)));

    /// <summary>Whatever Qt's writer emits, the reader gives back — including the hex-digit-after-a-hex-escape case
    /// that is the reason a greedy hex run is safe to read.</summary>
    [Theory]
    [InlineData(CjkProfile)]
    [InlineData("大a")]                       // Qt escapes the trailing 'a' too: \xe5\xa4\xa7\x61
    [InlineData("游戏7")]                     // and a trailing digit
    [InlineData(@"D:\newgame\xEdit")]         // Qt doubles both backslashes
    [InlineData("Requiem, AE")]
    [InlineData(" leading and trailing ")]
    [InlineData("say \"hi\"; done")]
    [InlineData("Default")]
    public void QtWrittenValuesReadBackWhole(string value)
        => Assert.Equal(value, QtIniEscapes.Clean(QtWrite(value)));

    /// <summary>A value holding a LONE backslash was not written by Qt (Qt doubles every one), so its backslashes are
    /// literal path separators and the value is left alone rather than half-decoded.</summary>
    [Theory]
    [InlineData(@"D:\newgame\Skyrim SE")]
    [InlineData(@"E:\xEdit\Stock Game")]
    [InlineData(@"C:\Games\Skyrim")]
    public void HandWrittenPathsAreLeftAlone(string value)
        => Assert.Equal(value, QtIniEscapes.Clean("@ByteArray(" + value + ")"));

    [Theory]
    // Qt5 wrote a code point above 0xFF as one UTF-16 code unit.
    [InlineData(@"\x5927\x80a5", "大肥")]
    // The named escapes.
    [InlineData(@"a\\b\;c\,d\=e\tf\vg\""h", "a\\b;c,d=e\tf\vg\"h")]
    [InlineData("Default", "Default")]
    public void UnescapeReadsQtsGrammar(string written, string expected)
        => Assert.Equal(expected, QtIniEscapes.Unescape(written));

    /// <summary>The quote pair exists to preserve an edge space, so the decoded value keeps it — the raw line is
    /// trimmed, the value is not, and both readers share that one order.</summary>
    [Fact]
    public void QuotedEdgeSpaceSurvives()
        => Assert.Equal(" Skyrim Mod.7z", QtIniEscapes.Clean("  \" Skyrim Mod.7z\"  "));
}

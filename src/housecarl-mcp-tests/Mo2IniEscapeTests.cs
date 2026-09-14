using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>MO2 stores selected_profile and gamePath as QByteArrays, and Qt writes every non-ASCII byte as a
/// <c>\xHH</c> escape. The reader unescaped <c>\\</c> only, so a CJK profile name came back as the literal escape
/// text and every tool refused with "the active profile's folder is missing" (#733). Both readers now go through
/// the one Qt unescaper.</summary>
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

    /// <summary>Write one value the way Qt does: printable ASCII as itself, a backslash doubled, everything else as
    /// the UTF-8 bytes in <c>\xHH</c> escapes.</summary>
    static string QtEscape(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b == (byte)'\\') sb.Append(@"\\");
            else if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
            else sb.Append('\\').Append('x').Append(b.ToString("x2"));
        }
        return sb.ToString();
    }

    /// <summary>Build an instance whose active profile and game path both carry CJK, written Qt's way.</summary>
    string BuildInstance()
    {
        var instance = Path.Combine(_root, "instance");
        var gamePath = Path.Combine(_root, CjkGameDir, "Stock Game");
        Directory.CreateDirectory(Path.Combine(instance, "mods"));
        Directory.CreateDirectory(Path.Combine(gamePath, "Data"));
        var profileDir = Path.Combine(instance, "profiles", CjkProfile);
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "loadorder.txt"), "Skyrim.esm\n");
        File.WriteAllText(
            Mo2Instance.IniPath(instance),
            "[General]\r\ngameName=Skyrim Special Edition\r\n"
            + "selected_profile=@ByteArray(" + QtEscape(CjkProfile) + ")\r\n"
            + "gamePath=@ByteArray(" + QtEscape(gamePath) + ")\r\n",
            new UTF8Encoding(false));
        return instance;
    }

    [Fact]
    public void CjkProfileAndGamePathResolve()
    {
        var instance = BuildInstance();
        var paths = Mo2Instance.Resolve(instance);
        Assert.Equal(CjkProfile, paths.ProfileName);
        Assert.Equal(Path.Combine(instance, "profiles", CjkProfile), paths.ProfileDir);
        Assert.Equal(Path.Combine(_root, CjkGameDir, "Stock Game", "Data"), paths.DataDir);
        Assert.True(Directory.Exists(paths.DataDir));
    }

    [Fact]
    public void CjkProfileReadsBackFromTheIni()
        => Assert.Equal(CjkProfile, Mo2Instance.ReadSelectedProfile(BuildInstance()));

    [Theory]
    // A CJK run is UTF-8 bytes, decoded together.
    [InlineData(@"\xe5\xa4\xa7\xe8\x82\xa5\xe9\xb1\xbc\xe6\x95\xb4\xe5\x90\x88", CjkProfile)]
    // Qt5 wrote a code point above 0xFF as one UTF-16 code unit.
    [InlineData(@"\x5927\x80a5", "大肥")]
    // The named escapes.
    [InlineData(@"a\\b\;c\,d\=e\tf", "a\\b;c,d=e\tf")]
    // Plain ASCII, and a value MO2 wrote with single (unescaped) backslashes, are unchanged.
    [InlineData("Default", "Default")]
    [InlineData(@"C:\Games\Skyrim", @"C:\Games\Skyrim")]
    // A surrounding pair of double quotes is the wrapper, not content.
    [InlineData("\"Stock Game\"", "Stock Game")]
    public void UnescapeReadsQtsGrammar(string written, string expected)
        => Assert.Equal(expected, QtIniEscapes.Unescape(written));
}

using System.Text;

namespace HousecarlMcpTests;

/// <summary>Builds a synthetic <c>SKSEPlugin_Version</c> blob at the confirmed offsets, the answer key
/// <see cref="HousecarlCore.SksePluginReader.DecodeVersionBlob"/> must agree with (moved from the skse-reader-guard probe).</summary>
static class SkseVersionBlobFixture
{
    /// <summary>Lays out the 0x350-byte blob; supportEmail is 252 bytes, which puts viEx at 0x304 and vi at 0x308.</summary>
    public static byte[] Blob(uint pluginVersion, string name, string author, string email, uint viEx, uint vi, uint[] compat, uint xseMin)
    {
        var b = new byte[0x350];
        BitConverter.GetBytes(1u).CopyTo(b, 0x000);
        BitConverter.GetBytes(pluginVersion).CopyTo(b, 0x004);
        WriteAscii(b, 0x008, name, 256);
        WriteAscii(b, 0x108, author, 256);
        WriteAscii(b, 0x208, email, 252);
        BitConverter.GetBytes(viEx).CopyTo(b, 0x304);
        BitConverter.GetBytes(vi).CopyTo(b, 0x308);
        for (int i = 0; i < compat.Length && i < 16; i++) BitConverter.GetBytes(compat[i]).CopyTo(b, 0x30C + i * 4);
        BitConverter.GetBytes(xseMin).CopyTo(b, 0x34C);
        return b;
    }

    /// <summary>REL::Version::pack: major 8 bits, minor 8, patch 12, build 4.</summary>
    public static uint Pack(int maj, int min, int patch, int build) =>
        (uint)(((maj & 0xFF) << 24) | ((min & 0xFF) << 16) | ((patch & 0xFFF) << 4) | (build & 0xF));

    static void WriteAscii(byte[] b, int off, string s, int max)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        Array.Copy(bytes, 0, b, off, Math.Min(bytes.Length, max - 1));
    }
}

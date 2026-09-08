using System.Buffers.Binary;
using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The import walk's bound on a declared directory. A data directory whose Size is zero but whose RVA is not
/// is a table that IS there and cannot be bounded; the walk used to read that as "genuinely absent", so a DLL's whole
/// delay-import set was dropped and the read still reported success — a complete-looking "imports (N): …" missing the
/// very names the Debug-CRT verdict is built on (#416). Synthetic PEs, because the shape is a header field: the image
/// below is the minimum <see cref="System.Reflection.PortableExecutable.PEReader"/> will open, with one .rdata section
/// carrying a real import descriptor table, a real delay-load descriptor table, and the two name strings.</summary>
[Trait("tier", "unit")]
public sealed class SkseImportDirectoryBoundTests
{
    /// <summary>A well-formed delay directory is walked, and what it carries is a debug-CRT import — the thing the
    /// Size==0 read dropped, and the reason dropping it is not cosmetic: absent from the list, the DLL reads as a clean
    /// build that will load, while the loader would refuse it with error 126.</summary>
    [Fact]
    public void DelayDirectoryWithItsSizeDeclaredIsWalked()
    {
        var info = SksePluginReader.ReadBytes("delay.dll", Image(ImportRva, ImportSize, DelayRva, DelaySize));

        Assert.NotNull(info.Imports);
        Assert.Equal(new[] { "kernel32.dll", "vcruntime140d.dll" }, info.Imports);
        Assert.Equal(new[] { "vcruntime140d.dll" }, SksePluginReader.DebugCrtImportsOf(info));
    }

    /// <summary>The bug: same image, same delay table, Size zeroed. The walk must answer UNKNOWN (null Imports), not a
    /// short list that renders as the whole truth.</summary>
    [Fact]
    public void DelayDirectoryWithZeroSizeAnswersUnknown()
    {
        var info = SksePluginReader.ReadBytes("delay.dll", Image(ImportRva, ImportSize, DelayRva, 0));

        Assert.Null(info.Imports);
    }

    /// <summary>The same bound guards the ordinary import directory — one walk serves both, so the zero Size refuses
    /// there too.</summary>
    [Fact]
    public void ImportDirectoryWithZeroSizeAnswersUnknown()
    {
        var info = SksePluginReader.ReadBytes("imports.dll", Image(ImportRva, 0, DelayRva, DelaySize));

        Assert.Null(info.Imports);
    }

    /// <summary>The other half of the fork: a directory with NO RVA is genuinely absent, and stays a walked, honest
    /// answer. Refusing on either zero would turn every DLL that delay-imports nothing — nearly all of them — into an
    /// UNKNOWN.</summary>
    [Fact]
    public void AbsentDelayDirectoryStaysAWalkedAnswer()
    {
        var info = SksePluginReader.ReadBytes("plain.dll", Image(ImportRva, ImportSize, 0, 0));

        Assert.Equal(new[] { "kernel32.dll" }, info.Imports);
    }

    /// <summary>A DLL importing nothing at all: both directories absent, so the walk succeeds and says empty. Pins that
    /// "walked, genuinely empty" survives the refusal, since it is the answer the refusal must not swallow.</summary>
    [Fact]
    public void ImageWithNoDirectoriesImportsNothing()
    {
        var info = SksePluginReader.ReadBytes("bare.dll", Image(0, 0, 0, 0));

        Assert.Empty(info.Imports!);
    }

    // ── the synthetic image ──────────────────────────────────────────────────────────────────────────────────────

    const int HeaderBytes = 0x200;      // SizeOfHeaders, one file-alignment unit
    const int SectionRva = 0x1000;      // the one section, .rdata
    const int SectionBytes = 0x200;

    const int ImportRva = SectionRva + 0x000;    // IMAGE_IMPORT_DESCRIPTOR[2]: one entry + the all-zero terminator
    const int ImportSize = 40;                   // 2 * 20
    const int DelayRva = SectionRva + 0x040;     // ImgDelayDescr[2]: one entry + terminator
    const int DelaySize = 64;                    // 2 * 32
    const int Kernel32Rva = SectionRva + 0x0C0;
    const int DebugCrtRva = SectionRva + 0x100;

    /// <summary>A minimal x64 PE image declaring the two import directories at the given RVA/Size. The tables and their
    /// name strings are always written; only what the header declares about them varies, which is exactly the axis
    /// under test.</summary>
    static byte[] Image(int importRva, int importSize, int delayRva, int delaySize)
    {
        var img = new byte[HeaderBytes + SectionBytes];

        img[0] = (byte)'M'; img[1] = (byte)'Z';
        const int PeSig = 0x80;
        U32(img, 0x3C, PeSig);                       // e_lfanew
        img[PeSig] = (byte)'P'; img[PeSig + 1] = (byte)'E';

        int coff = PeSig + 4;
        U16(img, coff + 0x00, 0x8664);               // Machine = AMD64
        U16(img, coff + 0x02, 1);                    // NumberOfSections
        U16(img, coff + 0x10, 0xF0);                 // SizeOfOptionalHeader (PE32+ with 16 directories)
        U16(img, coff + 0x12, 0x2022);               // EXECUTABLE_IMAGE | LARGE_ADDRESS_AWARE | DLL

        int opt = coff + 20;
        U16(img, opt + 0x00, 0x20B);                 // PE32+
        U32(img, opt + 0x20, 0x1000);                // SectionAlignment
        U32(img, opt + 0x24, 0x200);                 // FileAlignment
        U16(img, opt + 0x30, 6);                     // MajorSubsystemVersion
        U32(img, opt + 0x38, 0x2000);                // SizeOfImage
        U32(img, opt + 0x3C, HeaderBytes);           // SizeOfHeaders
        U16(img, opt + 0x44, 3);                     // Subsystem = CONSOLE
        U32(img, opt + 0x6C, 16);                    // NumberOfRvaAndSizes

        int dirs = opt + 0x70;
        U32(img, dirs + 1 * 8, (uint)importRva);     // IMAGE_DIRECTORY_ENTRY_IMPORT
        U32(img, dirs + 1 * 8 + 4, (uint)importSize);
        U32(img, dirs + 13 * 8, (uint)delayRva);     // IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT
        U32(img, dirs + 13 * 8 + 4, (uint)delaySize);

        int sec = opt + 0xF0;
        Ascii(img, sec, ".rdata");
        U32(img, sec + 0x08, SectionBytes);          // VirtualSize
        U32(img, sec + 0x0C, SectionRva);            // VirtualAddress
        U32(img, sec + 0x10, SectionBytes);          // SizeOfRawData
        U32(img, sec + 0x14, HeaderBytes);           // PointerToRawData
        U32(img, sec + 0x24, 0x40000040);            // CNT_INITIALIZED_DATA | MEM_READ

        U32(img, At(ImportRva) + 0x0C, Kernel32Rva); // IMAGE_IMPORT_DESCRIPTOR.Name
        U32(img, At(DelayRva) + 0x00, 1);            // ImgDelayDescr.Attributes, bit0 = RvaBased
        U32(img, At(DelayRva) + 0x04, DebugCrtRva);  // ImgDelayDescr.DllName
        Ascii(img, At(Kernel32Rva), "kernel32.dll");
        Ascii(img, At(DebugCrtRva), "vcruntime140d.dll");
        return img;
    }

    /// <summary>File offset of an RVA inside the one section.</summary>
    static int At(int rva) => HeaderBytes + rva - SectionRva;

    static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    static void Ascii(byte[] b, int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b.AsSpan(off));
}

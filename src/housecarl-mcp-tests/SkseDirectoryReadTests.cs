using System.Buffers.Binary;
using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>What the reader makes of a PE data directory the header declares oddly. A directory whose Size is zero but
/// whose RVA is not is a table that IS there, and both walks used to read that as "genuinely absent": the import walk
/// dropped a DLL's whole delay-import set and still reported success — a complete-looking "imports (N): …" missing the
/// very names the Debug-CRT verdict is built on (#416) — and the export walk answered an empty map, which classifies a
/// real plugin as a bundled dependency. Synthetic PEs, because the shape is a header field: the image below is the
/// minimum <see cref="System.Reflection.PortableExecutable.PEReader"/> will open, with one .rdata section carrying a
/// real import descriptor table, a real delay-load descriptor table, a real export directory, and their name strings.
/// The same image serves the sibling drop in the middle: a VA-based delay descriptor, which used to be skipped the
/// same way.</summary>
[Trait("tier", "unit")]
public sealed class SkseDirectoryReadTests
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

    /// <summary>The sibling drop, in the same walk: a delay descriptor with Attributes bit0 clear is VA-based, and its
    /// name field is an absolute address. That is resolvable — VA minus the image base is what the loader does — so it
    /// must be read, not skipped past into a list that renders as the whole truth.</summary>
    [Fact]
    public void VaBasedDelayDescriptorIsResolvedAgainstTheImageBase()
    {
        var info = SksePluginReader.ReadBytes("va.dll", Image(ImportRva, ImportSize, DelayRva, DelaySize, delayAttributes: 0, imageBase: 0x400000));

        Assert.Equal(new[] { "kernel32.dll", "vcruntime140d.dll" }, info.Imports);
        Assert.Equal(new[] { "vcruntime140d.dll" }, SksePluginReader.DebugCrtImportsOf(info));
    }

    /// <summary>The same VA-based table under a 64-bit image base, which a 32-bit name field cannot express, so the
    /// address lands outside the image. Unresolvable is UNKNOWN here, never a silently short list.</summary>
    [Fact]
    public void VaBasedDelayDescriptorOutsideTheImageAnswersUnknown()
    {
        var info = SksePluginReader.ReadBytes("va.dll", Image(ImportRva, ImportSize, DelayRva, DelaySize, delayAttributes: 0));

        Assert.Null(info.Imports);
    }

    /// <summary>The same declared-but-unsized shape on the export directory, where the header Size bounds nothing at
    /// all — the walk is driven by the directory's own counts. A zero Size there answered an empty map, so a DLL that
    /// does export SKSE's entry points was reported to the modder as "a bundled dependency DLL, not a plugin" and its
    /// version and runtime checks never ran.</summary>
    [Fact]
    public void ExportDirectoryWithZeroSizeIsStillRead()
    {
        var info = SksePluginReader.ReadBytes("exports.dll", Image(0, 0, 0, 0, exportRva: ExportRva, exportSize: 0));

        Assert.Equal(SksePluginReader.SksePluginKind.LegacyQuery, info.Kind);
    }

    /// <summary>The control for the case above: the same export directory with its Size declared, so what changes
    /// between the two is the header field and nothing else.</summary>
    [Fact]
    public void ExportDirectoryWithItsSizeDeclaredIsRead()
    {
        var info = SksePluginReader.ReadBytes("exports.dll", Image(0, 0, 0, 0, exportRva: ExportRva, exportSize: ExportSize));

        Assert.Equal(SksePluginReader.SksePluginKind.LegacyQuery, info.Kind);
    }

    /// <summary>The other half of that fork: no export RVA is a genuinely absent export table, and still classifies
    /// NotSkse. Reading a declared directory must not stop a real bundled dependency from being named as one.</summary>
    [Fact]
    public void AbsentExportDirectoryIsNotSkse()
    {
        var info = SksePluginReader.ReadBytes("dep.dll", Image(ImportRva, ImportSize, 0, 0));

        Assert.Equal(SksePluginReader.SksePluginKind.NotSkse, info.Kind);
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
    const int ExportRva = SectionRva + 0x120;    // IMAGE_EXPORT_DIRECTORY, exporting SKSEPlugin_Query
    const int ExportSize = 40;
    const int EatRva = SectionRva + 0x150;       // AddressOfFunctions[1]
    const int NameTableRva = SectionRva + 0x158; // AddressOfNames[1]
    const int OrdinalTableRva = SectionRva + 0x160;
    const int QueryNameRva = SectionRva + 0x168;

    /// <summary>A minimal x64 PE image declaring the two import directories and the export directory at the given
    /// RVA/Size. The tables and their name strings are always written; only what the header declares about them varies,
    /// which is exactly the axis under test. <paramref name="delayAttributes"/> and <paramref name="imageBase"/> pick
    /// how the delay table addresses its name: bit0 set is an RVA, clear is the absolute address a VA-based table would
    /// carry.</summary>
    static byte[] Image(int importRva, int importSize, int delayRva, int delaySize, uint delayAttributes = 1, ulong imageBase = 0x180000000,
                        int exportRva = 0, int exportSize = 0)
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
        U64(img, opt + 0x18, imageBase);             // ImageBase, what a VA-based delay name is measured from
        U32(img, opt + 0x20, 0x1000);                // SectionAlignment
        U32(img, opt + 0x24, 0x200);                 // FileAlignment
        U16(img, opt + 0x30, 6);                     // MajorSubsystemVersion
        U32(img, opt + 0x38, 0x2000);                // SizeOfImage
        U32(img, opt + 0x3C, HeaderBytes);           // SizeOfHeaders
        U16(img, opt + 0x44, 3);                     // Subsystem = CONSOLE
        U32(img, opt + 0x6C, 16);                    // NumberOfRvaAndSizes

        int dirs = opt + 0x70;
        U32(img, dirs + 0 * 8, (uint)exportRva);     // IMAGE_DIRECTORY_ENTRY_EXPORT
        U32(img, dirs + 0 * 8 + 4, (uint)exportSize);
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
        uint delayName = (delayAttributes & 1) != 0 ? (uint)DebugCrtRva : (uint)(imageBase + (ulong)DebugCrtRva);
        U32(img, At(DelayRva) + 0x00, delayAttributes);  // ImgDelayDescr.Attributes, bit0 = RvaBased
        U32(img, At(DelayRva) + 0x04, delayName);        // ImgDelayDescr.DllName
        Ascii(img, At(Kernel32Rva), "kernel32.dll");
        Ascii(img, At(DebugCrtRva), "vcruntime140d.dll");

        U32(img, At(ExportRva) + 0x14, 1);                       // NumberOfFunctions
        U32(img, At(ExportRva) + 0x18, 1);                       // NumberOfNames
        U32(img, At(ExportRva) + 0x1C, (uint)EatRva);            // AddressOfFunctions
        U32(img, At(ExportRva) + 0x20, (uint)NameTableRva);      // AddressOfNames
        U32(img, At(ExportRva) + 0x24, (uint)OrdinalTableRva);   // AddressOfNameOrdinals
        U32(img, At(EatRva), (uint)SectionRva);                     // the export's own RVA; unread for a Query export, which classifies on the name
        U32(img, At(NameTableRva), (uint)QueryNameRva);
        U16(img, At(OrdinalTableRva), 0);
        Ascii(img, At(QueryNameRva), "SKSEPlugin_Query");
        return img;
    }

    /// <summary>File offset of an RVA inside the one section.</summary>
    static int At(int rva) => HeaderBytes + rva - SectionRva;

    static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    static void U64(byte[] b, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off), v);
    static void Ascii(byte[] b, int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b.AsSpan(off));
}

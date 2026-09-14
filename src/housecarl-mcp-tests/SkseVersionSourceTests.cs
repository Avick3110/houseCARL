using System.Buffers.Binary;
using System.Text;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Which version a DLL's report is showing, and where it was read from (#667). The number houseCARL reads out
/// of the SKSE manifest is the author's own declaration and is routinely stale: SPID 7.3.3 declares 7.0.0, so the
/// report used to show 7.0.0 with nothing to say it was not the build. The DLL's Win32 version resource is the second,
/// build-stamped number, and it rides the same PE read. Synthetic PEs, because the resource tree is a byte layout: the
/// image below is the minimum <see cref="System.Reflection.PortableExecutable.PEReader"/> will open, with one .rsrc
/// section carrying a real three-level resource tree and a real VS_VERSIONINFO block.</summary>
[Trait("tier", "unit")]
public sealed class SkseVersionSourceTests
{
    /// <summary>The read itself: a version resource declaring 7.3.3.0 is reported as 7.3.3.0.</summary>
    [Fact]
    public void TheVersionResourceIsRead()
    {
        var info = SksePluginReader.ReadBytes("plugin.dll", Image(fileVersionMs: 0x0007_0003, fileVersionLs: 0x0003_0000));

        Assert.Equal("7.3.3.0", info.FileVersion);
    }

    /// <summary>A DLL carrying no version resource at all — many do not. That is UNKNOWN, and it must not become a
    /// fabricated 0.0.0.0.</summary>
    [Fact]
    public void AnImageWithNoVersionResourceHasNoFileVersion()
    {
        var info = SksePluginReader.ReadBytes("plain.dll", Image(0x0007_0003, 0x0003_0000, withResources: false));

        Assert.Null(info.FileVersion);
    }

    /// <summary>The struct is believed only when it says it is the struct: a block whose 0xFEEF04BD signature is wrong
    /// yields no number rather than four fields read out of unrelated bytes.</summary>
    [Fact]
    public void AVersionBlockWithoutItsSignatureIsNotRead()
    {
        var info = SksePluginReader.ReadBytes("odd.dll", Image(0x0007_0003, 0x0003_0000, signature: 0xDEADBEEF));

        Assert.Null(info.FileVersion);
    }

    /// <summary>The report: where the manifest's declaration and the file version disagree, the row says which source
    /// the version came from and prints the other one beside it — the SPID case, where 7.0.0 alone sent an agent to the
    /// wrong grammar.</summary>
    [Fact]
    public void ARowWhoseManifestDisagreesWithTheFileShowsBothAndNamesTheSource()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: "7.0.0", fileVersion: "7.3.3.0", modVersion: "7.3.3.0"), null, 80_000);

        Assert.Contains("7.0.0 (SKSE manifest; DLL file version 7.3.3.0)", text);
    }

    /// <summary>The meta.ini version is the third number, and it rides the same line when it agrees with neither — what
    /// the modder installed, against what the DLL says it is.</summary>
    [Fact]
    public void AModVersionAgreeingWithNeitherIsShownToo()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: "7.0.0", fileVersion: "7.3.3.0", modVersion: "7.3.4"), null, 80_000);

        Assert.Contains("meta.ini 7.3.4", text);
    }

    /// <summary>The control: versions that agree add nothing to the row. "7.3.3" and "7.3.3.0" are the same version
    /// written two ways, so neither is a disagreement to report.</summary>
    [Fact]
    public void AgreeingVersionsStayOneNumber()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: "7.3.3", fileVersion: "7.3.3.0", modVersion: "7.3.3.0"), null, 80_000);

        Assert.Contains("7.3.3 (SKSE manifest)", text);
        Assert.DoesNotContain("SKSE manifest;", text);
        Assert.DoesNotContain("DLL file version", text);
        Assert.DoesNotContain("meta.ini", text);
    }

    /// <summary>A meta.ini version carrying a tag the modder added — MO2 records "7.0.19.0-AIO" where the DLL stamps
    /// "7.0.19.0" — is the SAME version, so it is not reported as a third number.</summary>
    [Fact]
    public void ATaggedVersionAgreeingOnTheNumbersIsNotADisagreement()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: "7.0.19", fileVersion: "7.0.19.0", modVersion: "7.0.19.0-AIO"), null, 80_000);

        Assert.DoesNotContain("meta.ini", text);
    }

    /// <summary>The same disagreement on a DLL carrying no version resource at all — 61 of the 313 DLLs on the order
    /// this was measured against carry none. The manifest and meta.ini are then the only two numbers there are, so the
    /// disagreement between them must still be shown.</summary>
    [Fact]
    public void AModVersionDisagreeingWithTheManifestIsShownWithNoFileVersion()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: "7.0.0", fileVersion: null, modVersion: "7.3.3"), null, 80_000);

        Assert.Contains("7.0.0 (SKSE manifest; meta.ini 7.3.3)", text);
    }

    /// <summary>A DLL with no manifest at all and no version resource still has the version MO2 recorded for the mod
    /// that ships it, and that is the only number in sight — so it is the one printed.</summary>
    [Fact]
    public void AManifestLessDllFallsBackToTheModVersion()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: null, fileVersion: null, modVersion: "2.1"), "spid", 80_000);

        Assert.Contains("2.1 (mod meta.ini)", text);
    }

    /// <summary>The same DLL with a file version the mod's meta.ini disagrees with: the disagreement the tool exists to
    /// surface does not disappear because there is no manifest to anchor it.</summary>
    [Fact]
    public void AManifestLessDllStillShowsAModVersionThatDisagrees()
    {
        var text = SkseInventoryWire.Render(Layer(manifest: null, fileVersion: "1.0.0.0", modVersion: "3.4"), "spid", 80_000);

        Assert.Contains("1.0.0.0 (DLL file version; meta.ini 3.4)", text);
    }

    /// <summary>The version text never contains " — ": the pairing audit's fate line joins its own fields with that,
    /// and a version carrying one would make the load verdict read as part of the version.</summary>
    [Fact]
    public void TheVersionTextDoesNotUseTheRowSeparator()
    {
        var text = SkseInventoryWire.VersionText(
            new SksePluginReader.SksePluginInfo("p.dll", SksePluginReader.SksePluginKind.Modern, true,
                new SksePluginReader.SkseVersionInfo("P", "a", "", "7.0.0", true, false, false, false, Array.Empty<string>(), null),
                null, null, "7.3.3.0"),
            "7.3.4");

        Assert.DoesNotContain(" — ", text);
    }

    // ── the synthetic layer ──────────────────────────────────────────────────────────────────────────────────────

    static SkseInventoryData Layer(string? manifest, string? fileVersion, string? modVersion)
    {
        // manifest null = a plugin whose metadata is not statically readable (the legacy SE/VR export), which is the
        // case the mod's own meta.ini version has to carry.
        var version = manifest is null ? null : new SksePluginReader.SkseVersionInfo("Spell Perk Item Distributor", "powerofthree", "", manifest,
            UsesAddressLibrary: true, UsesSignatureScanning: false, UsesUpdatedStructs: false, DeclaresNoStructs: false,
            new[] { "1.6.1170.0" }, null);
        var plugin = new SksePluginReader.SksePluginInfo("spid.dll",
            manifest is null ? SksePluginReader.SksePluginKind.LegacyQuery : SksePluginReader.SksePluginKind.Modern, true, version,
            manifest is null ? "legacy SE/VR plugin: metadata is filled at runtime" : null,
            new[] { "kernel32.dll" }, fileVersion);
        var entry = new SkseFileEntry("SKSE/Plugins/spid.dll", "spid.dll", "", new[] { new SkseProvider("SPID", "loose") },
            plugin, null, ModVersion: modVersion);
        return new SkseInventoryData(new[] { entry }, Array.Empty<SkseFileEntry>(), OtherFileCount: 0,
            InstalledRuntime: "1.6.1170.0", BsaFailures: Array.Empty<string>(), ReadIncomplete: false,
            Warnings: Array.Empty<string>(), ProfileName: "Default");
    }

    // ── the synthetic image ──────────────────────────────────────────────────────────────────────────────────────

    const int HeaderBytes = 0x200;      // SizeOfHeaders, one file-alignment unit
    const int SectionRva = 0x1000;      // the one section, .rsrc
    const int SectionBytes = 0x200;

    const int RootDir = 0x00;           // the type level, carrying one id: RT_VERSION (16)
    const int NameDir = 0x18;           // the name level
    const int LangDir = 0x30;           // the language level, whose child is the data entry
    const int DataEntry = 0x48;         // IMAGE_RESOURCE_DATA_ENTRY
    const int VersionBlock = 0x60;      // VS_VERSIONINFO

    /// <summary>A minimal x64 PE image whose one .rsrc section carries the resource tree a version resource lives in:
    /// type (RT_VERSION) → name → language → data entry → VS_VERSIONINFO. <paramref name="withResources"/> declares the
    /// directory or leaves the image without one; <paramref name="signature"/> is the VS_FIXEDFILEINFO magic, so a test
    /// can hand over a block that is not the struct it claims to be.</summary>
    static byte[] Image(uint fileVersionMs, uint fileVersionLs, bool withResources = true, uint signature = 0xFEEF04BD)
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
        U64(img, opt + 0x18, 0x180000000);           // ImageBase
        U32(img, opt + 0x20, 0x1000);                // SectionAlignment
        U32(img, opt + 0x24, 0x200);                 // FileAlignment
        U16(img, opt + 0x30, 6);                     // MajorSubsystemVersion
        U32(img, opt + 0x38, 0x2000);                // SizeOfImage
        U32(img, opt + 0x3C, HeaderBytes);           // SizeOfHeaders
        U16(img, opt + 0x44, 3);                     // Subsystem = CONSOLE
        U32(img, opt + 0x6C, 16);                    // NumberOfRvaAndSizes

        int dirs = opt + 0x70;
        if (withResources)
        {
            U32(img, dirs + 2 * 8, SectionRva);      // IMAGE_DIRECTORY_ENTRY_RESOURCE
            U32(img, dirs + 2 * 8 + 4, SectionBytes);
        }

        int sec = opt + 0xF0;
        Ascii(img, sec, ".rsrc");
        U32(img, sec + 0x08, SectionBytes);          // VirtualSize
        U32(img, sec + 0x0C, SectionRva);            // VirtualAddress
        U32(img, sec + 0x10, SectionBytes);          // SizeOfRawData
        U32(img, sec + 0x14, HeaderBytes);           // PointerToRawData
        U32(img, sec + 0x24, 0x40000040);            // CNT_INITIALIZED_DATA | MEM_READ

        // The three directory levels. Each is a header whose last two WORDs count its name- and id-keyed entries,
        // followed by the entries; an entry value's high bit marks a subdirectory.
        Directory(img, RootDir, id: 16, child: NameDir, isDir: true);     // RT_VERSION
        Directory(img, NameDir, id: 1, child: LangDir, isDir: true);
        Directory(img, LangDir, id: 1033, child: DataEntry, isDir: false);

        U32(img, At(DataEntry) + 0x00, (uint)(SectionRva + VersionBlock));   // OffsetToData is an RVA, not a tree offset
        U32(img, At(DataEntry) + 0x04, 0x60);                                // Size

        // VS_VERSIONINFO: wLength, wValueLength, wType, the UTF-16 key, 4-byte padding, then VS_FIXEDFILEINFO.
        int block = At(VersionBlock);
        U16(img, block + 0x00, 0x60);                // wLength
        U16(img, block + 0x02, 52);                  // wValueLength = sizeof(VS_FIXEDFILEINFO)
        U16(img, block + 0x04, 0);                   // wType = binary
        Utf16(img, block + 0x06, "VS_VERSION_INFO");
        int fixedInfo = block + 0x28;                // (6 + 32 key bytes) rounded up to 4
        U32(img, fixedInfo + 0x00, signature);
        U32(img, fixedInfo + 0x04, 0x00010000);      // strucVersion
        U32(img, fixedInfo + 0x08, fileVersionMs);
        U32(img, fixedInfo + 0x0C, fileVersionLs);
        return img;
    }

    /// <summary>One resource directory holding exactly one entry.</summary>
    static void Directory(byte[] img, int dirOff, int id, int child, bool isDir)
    {
        U16(img, At(dirOff) + 0x0C, 0);              // NumberOfNamedEntries
        U16(img, At(dirOff) + 0x0E, 1);              // NumberOfIdEntries
        U32(img, At(dirOff) + 0x10, (uint)id);
        U32(img, At(dirOff) + 0x14, isDir ? 0x80000000u | (uint)child : (uint)child);
    }

    /// <summary>File offset of a resource-tree offset inside the one section.</summary>
    static int At(int treeOffset) => HeaderBytes + treeOffset;

    static void U16(byte[] b, int off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(off), v);
    static void U32(byte[] b, int off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off), v);
    static void U64(byte[] b, int off, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off), v);
    static void Ascii(byte[] b, int off, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b.AsSpan(off));
    static void Utf16(byte[] b, int off, string s) => Encoding.Unicode.GetBytes(s).CopyTo(b.AsSpan(off));
}

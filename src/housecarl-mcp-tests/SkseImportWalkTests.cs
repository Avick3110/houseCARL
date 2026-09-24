using System.Reflection.PortableExecutable;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The import walk and the Debug-CRT verdict in <see cref="SksePluginReader"/>: a real native PE walks, a corrupt import
/// name fails the whole walk, a managed image walks empty rather than unknown, and the curated debug-CRT list and blocker
/// never turn a failed walk into a claim. Reads this machine's kernel32.dll and the core assembly; no world.
/// Migrated from the skse-peek-guard probe, part 2 (arms E to G2).
/// </summary>
[Trait("tier", "unit")]
public sealed class SkseImportWalkTests
{
    static readonly string Kernel32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

    static SksePluginReader.SksePluginInfo Info(IReadOnlyList<string>? imports) =>
        new("x.dll", SksePluginReader.SksePluginKind.Modern, true,
            new SksePluginReader.SkseVersionInfo("Test", "Tester", "", "1.0.0", true, false, false, false, [], null),
            null, imports);

    /// <summary>File offset of the first import descriptor's Name RVA (descriptor + 0x0C), mapped with the BCL's PE
    /// reader so the fixture does not reuse the code under test; -1 when there is no import table.</summary>
    static int FirstImportNameOffset(byte[] raw)
    {
        using var pe = new PEReader(new MemoryStream(raw, writable: false));
        int rva = pe.PEHeaders.PEHeader!.ImportTableDirectory.RelativeVirtualAddress;
        if (rva == 0) return -1;
        foreach (var s in pe.PEHeaders.SectionHeaders)
            if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                return s.PointerToRawData + (rva - s.VirtualAddress) + 0x0C;
        return -1;
    }

    // probe E: "a real native PE's import directory WALKS"; "…and yields its imports"
    [Fact]
    public void ARealNativePesImportDirectoryWalksToANonEmptyList()
    {
        Assert.True(File.Exists(Kernel32), Kernel32);
        var info = SksePluginReader.Read(Kernel32);
        Assert.NotNull(info.Imports);
        Assert.NotEmpty(info.Imports);
    }

    // probe E: "import names are normalized lower-case for comparison"
    [Fact]
    public void ImportNamesAreLowerCase()
    {
        var info = SksePluginReader.Read(Kernel32);
        Assert.NotNull(info.Imports);
        Assert.All(info.Imports, i => Assert.Equal(i.ToLowerInvariant(), i));
    }

    // probe E: "a system DLL is still classified NotSkse (no SKSE export)"
    [Fact]
    public void ASystemDllIsClassifiedNotSkse()
    {
        Assert.Equal(SksePluginReader.SksePluginKind.NotSkse, SksePluginReader.Read(Kernel32).Kind);
    }

    // probe E2: "an unresolvable import-name RVA fails the WHOLE walk → null (UNKNOWN), never a silent short list"
    [Fact]
    public void AnUnresolvableImportNameFailsTheWholeWalk()
    {
        var raw = File.ReadAllBytes(Kernel32);
        int nameOff = FirstImportNameOffset(raw);
        Assert.True(nameOff > 0, "no import descriptor found in kernel32.dll");
        // control: "the unpatched image walks"
        Assert.NotEmpty(SksePluginReader.ReadBytes("k32.dll", raw).Imports ?? []);

        BitConverter.GetBytes(0x7FFFFFFF).CopyTo(raw, nameOff);   // an RVA that maps to no section
        Assert.Null(SksePluginReader.ReadBytes("k32-corrupt.dll", raw).Imports);
    }

    // probe F: "a managed assembly's (absent) import directory is a SUCCESSFUL walk → empty, not null"; "…and it imports no debug CRT"
    [Fact]
    public void AManagedAssemblyWalksEmptyNotUnknown()
    {
        var info = SksePluginReader.Read(typeof(SksePluginReader).Assembly.Location);
        Assert.NotNull(info.Imports);
        Assert.Empty(info.DebugCrtImports);
    }

    // probe G: "the curated list pins the modern debug-CRT family (vcruntime140d / msvcp140d / ucrtbased)"
    [Theory]
    [InlineData("vcruntime140d.dll")]
    [InlineData("msvcp140d.dll")]
    [InlineData("ucrtbased.dll")]
    public void TheDebugCrtListCarriesTheModernDebugFamily(string dll) =>
        Assert.Contains(dll, SksePluginReader.DebugCrtDlls);

    // probe G: "…and NOT their release twins or the d-suffixed innocents (the list is curated, not a 'ends in d' pattern)"
    [Theory]
    [InlineData("vcruntime140.dll")]
    [InlineData("d3d11.dll")]
    [InlineData("dinput8.dll")]
    public void TheDebugCrtListLeavesOutReleaseTwinsAndDSuffixedInnocents(string dll) =>
        Assert.DoesNotContain(dll, SksePluginReader.DebugCrtDlls);

    // probe G: "a debug-CRT import is caught case-INSENSITIVELY (image tables are not case-normalized)"
    [Fact]
    public void ADebugCrtImportIsCaughtCaseInsensitively() =>
        Assert.Single(SksePluginReader.DebugCrtImportsOf(Info(["kernel32.dll", "VCRUNTIME140D.dll"])));

    // probe G: "a RELEASE runtime import is not a debug finding"
    [Fact]
    public void AReleaseRuntimeImportIsNotADebugFinding() =>
        Assert.Empty(SksePluginReader.DebugCrtImportsOf(Info(["kernel32.dll", "vcruntime140.dll"])));

    // probe G: "a FAILED walk yields no debug-CRT claim — absence of evidence is not evidence of absence"
    [Fact]
    public void AFailedWalkYieldsNoDebugCrtClaim() =>
        Assert.Empty(SksePluginReader.DebugCrtImportsOf(Info(null)));

    // probe G2: "debug runtime ABSENT ⇒ a load blocker naming the culprit and the loader failure"
    [Fact]
    public void AnAbsentDebugRuntimeIsABlockerNamingTheDllAndError126()
    {
        var blocker = SksePluginReader.DebugCrtBlocker(Info(["kernel32.dll", "vcruntime140d.dll"]), _ => false);
        Assert.NotNull(blocker);
        Assert.Contains("vcruntime140d.dll", blocker);
        Assert.Contains("error 126", blocker);
    }

    // probe G2: "debug runtime PRESENT (a dev box) ⇒ NO blocker"
    [Fact]
    public void APresentDebugRuntimeIsNoBlocker() =>
        Assert.Null(SksePluginReader.DebugCrtBlocker(Info(["kernel32.dll", "vcruntime140d.dll"]), _ => true));

    // probe G2: "a RELEASE runtime is never a blocker"
    [Fact]
    public void AReleaseRuntimeIsNeverABlocker() =>
        Assert.Null(SksePluginReader.DebugCrtBlocker(Info(["kernel32.dll", "vcruntime140.dll"]), _ => false));

    // probe G2: "a FAILED import walk is never a blocker — an unknown must not become a DEAD verdict"
    [Fact]
    public void AFailedImportWalkIsNeverABlocker() =>
        Assert.Null(SksePluginReader.DebugCrtBlocker(Info(null), _ => false));
}

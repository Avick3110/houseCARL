using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>Synthetic audit data for the native-pairing renderer tests, as the native-pairing-guard probe built it.</summary>
static class NativePairingRenderFixtures
{
    public static SksePluginReader.SkseVersionInfo Ver(bool independent, params string[] compat) =>
        new("Test Plugin", "tester", "", "1.0.0",
            UsesAddressLibrary: independent, UsesSignatureScanning: false,
            UsesUpdatedStructs: false, DeclaresNoStructs: false,
            CompatibleVersions: compat, MinimumXseVersion: null);

    public static SksePluginReader.SksePluginInfo Modern(string file, bool independent, string[]? compat = null, string[]? imports = null) =>
        new(file, SksePluginReader.SksePluginKind.Modern, true, Ver(independent, compat ?? Array.Empty<string>()), null, imports);

    /// <summary>A version-independent DLL that imports the debug CRT.</summary>
    public static SksePluginReader.SksePluginInfo DebugBuild(string file, bool independent = true, string[]? compat = null) =>
        Modern(file, independent, compat, new[] { "kernel32.dll", "vcruntime140d.dll" });

    public static NativePairedDll Dll(string file, string mod, SksePluginReader.SksePluginInfo? info, string? blocker = null) =>
        new($@"SKSE\Plugins\{file}", file, "", mod, info, blocker);

    public static NativeClassEntry Cls(string name, NativeProvenance prov, NativePairingRung? rung, string? pairedMod,
        IReadOnlyList<NativePairedDll>? dlls = null, string winner = "SomeMod") =>
        new($@"Scripts\{name}.pex", name, new[] { "FnA", "FnB" },
            new[] { new SkseProvider(winner, "loose") }, prov, rung, pairedMod, dlls ?? Array.Empty<NativePairedDll>());

    /// <summary>One third-party class paired SameMod to <paramref name="mod"/> with the given DLLs.</summary>
    public static NativeClassEntry Paired(string name, string mod, params NativePairedDll[] dlls) =>
        Cls(name, NativeProvenance.ThirdParty, NativePairingRung.SameMod, mod, dlls);

    public static NativePairingAuditData Data(IReadOnlyList<NativeClassEntry> classes, string? runtime, bool? loaderSeen = true,
        IReadOnlyList<NativeUnreadablePex>? unreadable = null) =>
        new(classes, 1000, unreadable ?? Array.Empty<NativeUnreadablePex>(), loaderSeen, runtime,
            Array.Empty<string>(), Array.Empty<string>(), false, Array.Empty<string>(), "TestProfile");

    public static NativePairingAuditData Data(NativeClassEntry cls, string? runtime) => Data(new[] { cls }, runtime);

    public static string Render(NativePairingAuditData d, string? filter = null) => NativePairingWire.Render(d, filter, 80_000);
}

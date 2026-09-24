using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

// The DECLARATION half of the native-function pairing audit, pure over Mutagen's PexFile model; contract in docs/architecture/skse-layer.md.

/// <summary>One <c>.pex</c> script object declaring ≥1 native-flagged function, with those function names.</summary>
public sealed record NativeClassDecl(string ClassName, IReadOnlyList<string> NativeFunctions);

public static class NativePairing
{
    /// <summary>The native flag: raw bit1 (bit0 = Global), because Mutagen's enum names sit one off; pinned by NativePairingExtractTests.TheNativeFlagIsRawBit1AndGlobalAloneIsNotNative.</summary>
    const uint NativeFlagBit = 0x2;

    /// <summary>Every script object in <paramref name="pex"/> that declares native functions; pure, never throws on a parsed model.</summary>
    public static IReadOnlyList<NativeClassDecl> ExtractNativeClasses(PexFile pex)
    {
        var result = new List<NativeClassDecl>();
        foreach (var obj in pex.Objects)
        {
            var natives = new List<string>();
            foreach (var st in obj.States)
                foreach (var f in st.Functions.Cast<PexObjectNamedFunction>())
                    if (IsNative(f.Function)) natives.Add(f.FunctionName ?? "(unnamed)");
            foreach (var p in obj.Properties)
            {
                if (p.ReadHandler is { } get && IsNative(get)) natives.Add($"{p.Name}.Get");
                if (p.WriteHandler is { } set && IsNative(set)) natives.Add($"{p.Name}.Set");
            }
            if (natives.Count > 0)
                result.Add(new NativeClassDecl(obj.Name ?? "(unnamed object)", natives));
        }
        return result;
    }

    static bool IsNative(PexObjectFunction f) => ((uint)f.Flags & NativeFlagBit) != 0;
}

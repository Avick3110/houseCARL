using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

// ======================================================================
//  NativePairing — the PURE half of the native-function pairing audit
//  (housecarl_skse findings='pairing').
//
//  A native Papyrus function is ONE thing declared in TWO places: a .pex script class carrying a
//  native-flagged function (what quests/MCMs/effects compile against) and a DLL that registers the
//  implementation at runtime. The halves ship as separate files and fail INDEPENDENTLY — scripts
//  installed, DLL absent / wrong-runtime / 32-bit — and the engine's response is "unable to bind" +
//  silent no-op calls. This file extracts the DECLARATION side from a compiled .pex: which script
//  objects declare native functions, and which functions those are.
//
//  Pure over Mutagen's PexFile model, so it runs against fixture .pex files with no live order. The
//  sweep, provenance classification, and pairing ladder live in LoadOrderService (they need the VFS
//  + archive provenance). This reads what a script DECLARES, never what a DLL actually registers at
//  runtime.
// ======================================================================

/// <summary>One script object (class) in a <c>.pex</c> that declares ≥1 native-flagged function, with the declared
/// native function names (named state functions; a native-flagged property accessor surfaces as <c>Prop.Get</c>/
/// <c>Prop.Set</c>). <see cref="ClassName"/> is the pex object's own name — the class identity scripts compile
/// against, which for a namespaced script includes the namespace (<c>Ns:Script</c>).</summary>
public sealed record NativeClassDecl(string ClassName, IReadOnlyList<string> NativeFunctions);

public static class NativePairing
{
    /// <summary>The native flag on <see cref="PexObjectFunction.Flags"/>: raw bit1 (bit0 = Global). Mutagen's enum
    /// names sit one off from the file format — the off-by-one the PapyrusDecompiler header describes — so the raw
    /// bit is the truth.</summary>
    const uint NativeFlagBit = 0x2;

    /// <summary>Extract every script object in <paramref name="pex"/> that declares native functions. An object with
    /// no native functions yields nothing (the common case — most scripts are pure Papyrus). Pure; never throws on a
    /// model Mutagen managed to parse (an UNPARSEABLE .pex fails at <c>PexFile.Create*</c> in the caller, which names
    /// it).</summary>
    public static IReadOnlyList<NativeClassDecl> ExtractNativeClasses(PexFile pex)
    {
        var result = new List<NativeClassDecl>();
        foreach (var obj in pex.Objects)
        {
            var natives = new List<string>();
            foreach (var st in obj.States)
                foreach (var f in st.Functions.Cast<PexObjectNamedFunction>())
                    if (IsNative(f.Function)) natives.Add(f.FunctionName ?? "(unnamed)");
            // Property accessors are functions too; a native-flagged one is rare but real — count it, never drop it.
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

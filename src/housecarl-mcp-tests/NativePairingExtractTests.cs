using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Migrated from the native-pairing-guard probe, part 1: the pure .pex extractor over a synthetic PexFile.
/// The native flag is raw bit1, because Mutagen's enum names sit one off.</summary>
[Trait("tier", "unit")]
public sealed class NativePairingExtractTests
{
    static PexObjectNamedFunction Fn(string name, uint flags) =>
        new() { FunctionName = name, Function = new PexObjectFunction { Flags = (FunctionFlags)flags } };

    static IReadOnlyList<NativeClassDecl> StorageUtilAndPlainScript()
    {
        var pex = new PexFile(GameCategory.Skyrim);
        var obj = new PexObject { Name = "StorageUtil" };
        var st = new PexObjectState();
        st.Functions.Add(Fn("SetIntValue", 0x3));   // Global|Native
        st.Functions.Add(Fn("GetIntValue", 0x2));   // Native only
        st.Functions.Add(Fn("LocalHelper", 0x1));   // Global only
        st.Functions.Add(Fn("PlainFunc", 0x0));
        obj.States.Add(st);
        pex.Objects.Add(obj);

        var plain = new PexObject { Name = "PlainQuestScript" };
        var pst = new PexObjectState();
        pst.Functions.Add(Fn("OnInit", 0x0));
        plain.States.Add(pst);
        pex.Objects.Add(plain);
        return NativePairing.ExtractNativeClasses(pex);
    }

    [Fact] // probe: "one native class extracted (the all-Papyrus object yields nothing)"
    public void OnlyTheObjectDeclaringNativesIsExtracted() =>
        Assert.Equal("StorageUtil", Assert.Single(StorageUtilAndPlainScript()).ClassName);

    [Fact] // probe: "native = raw bit1: SetIntValue + GetIntValue in, Global-only + plain OUT"
    public void TheNativeFlagIsRawBit1AndGlobalAloneIsNotNative() =>
        Assert.Equal(new[] { "SetIntValue", "GetIntValue" }, Assert.Single(StorageUtilAndPlainScript()).NativeFunctions);

    [Fact] // probe: "native property accessor → 'Version.Get' declared"
    public void ANativePropertyAccessorIsDeclaredAsPropGet()
    {
        var pex = new PexFile(GameCategory.Skyrim);
        var obj = new PexObject { Name = "NativeProps" };
        obj.Properties.Add(new PexObjectProperty { Name = "Version", ReadHandler = new PexObjectFunction { Flags = (FunctionFlags)0x2 } });
        pex.Objects.Add(obj);

        Assert.Equal(new[] { "Version.Get" }, Assert.Single(NativePairing.ExtractNativeClasses(pex)).NativeFunctions);
    }
}

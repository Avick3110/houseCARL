using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Named import sets in <c>UserConfigStore</c> (migrated from the <c>compile-ergonomics-guard</c> probe, part F): they
/// round-trip in order, match by trimmed case-insensitive name, replace across case, and never clobber the file's other fields.
/// </summary>
[Trait("tier", "unit")]
public sealed class ImportSetStoreTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), "hc-importset-tests-" + Guid.NewGuid().ToString("N") + ".json");
    readonly UserConfigStore _store;
    static readonly string[] Dirs = { @"C:\proj\stubs", @"C:\proj\src" };

    public ImportSetStoreTests() => _store = new UserConfigStore(_path);

    public void Dispose()
    {
        try { File.Delete(_path); } catch { /* non-fatal */ }
    }

    // Probe F: "an unknown set reads as null (never an empty set that would compile short)" and "no sets saved yet → no names".
    [Fact]
    public void AnUnknownSetIsNullAndNoNamesAreListed()
    {
        Assert.Null(_store.GetImportSet("nope"));
        Assert.Empty(_store.ImportSetNames());
    }

    // Probe F: "an unknown set reads as null", with another set saved beside it.
    [Fact]
    public void AnUnknownNameBesideASavedSetIsNull()
    {
        _store.SaveImportSet("MyProject", Dirs);
        Assert.Null(_store.GetImportSet("nope"));
    }

    // Probe F: "a set saves" and "the set round-trips in ORDER (order is compiler semantics)".
    [Fact]
    public void ASetRoundTripsInOrder()
    {
        Assert.True(_store.SaveImportSet("MyProject", Dirs).ok);
        Assert.Equal(Dirs, _store.GetImportSet("MyProject"));
    }

    // Probe F: "lookup is case-INSENSITIVE" and "lookup trims the name".
    [Theory]
    [InlineData("myproject")]
    [InlineData(" MyProject ")]
    public void LookupIgnoresCaseAndSurroundingSpace(string name)
    {
        _store.SaveImportSet("MyProject", Dirs);
        Assert.NotNull(_store.GetImportSet(name));
    }

    // Probe F: "saving a set does NOT clobber the MO2 instance dir", "…nor the saved tool paths", "…nor the in-place acknowledgements".
    [Fact]
    public void SavingASetKeepsTheOtherFields()
    {
        _store.Update(c =>
        {
            c.Mo2InstanceDir = @"C:\MO2";
            c.ToolPaths = new Dictionary<string, string> { ["papyrus_compiler"] = @"C:\CK\PapyrusCompiler.exe" };
        });
        _store.RecordInPlaceAcknowledged(@"C:\MO2\mods\X\Y.esp");

        _store.SaveImportSet("MyProject", Dirs);

        var after = _store.Load();
        Assert.Equal(@"C:\MO2", after.Mo2InstanceDir);
        Assert.True(after.ToolPaths?.ContainsKey("papyrus_compiler"));
        Assert.Single(after.InPlaceAcknowledged!);
    }

    // Probe F: "re-saving with different case REPLACES (one set, not two)" and "the replacement's dirs win".
    [Fact]
    public void ReSavingUnderAnotherCaseReplacesTheSet()
    {
        _store.SaveImportSet("MyProject", Dirs);
        _store.SaveImportSet("myproject", new[] { @"C:\other" });
        Assert.Single(_store.Load().ImportSets!);
        Assert.Equal(new[] { @"C:\other" }, _store.GetImportSet("MYPROJECT"));
    }

    // Probe F: "names come back sorted (for the unknown-name 'saved sets:' list)".
    [Fact]
    public void NamesComeBackSorted()
    {
        _store.SaveImportSet("myproject", Dirs);
        _store.SaveImportSet("alpha", new[] { @"C:\a" });
        Assert.Equal(new[] { "alpha", "myproject" }, _store.ImportSetNames());
    }
}

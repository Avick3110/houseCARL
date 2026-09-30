using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <c>UserConfigStore</c> on houseCARL.user.json (migrated from the <c>tool-bridge</c> probe, arms 1 and 7): the MO2
/// instance dir and the tool paths never clobber each other, a corrupt file is backed up and reported, and two stores on
/// one file serialize. Every store here writes under this test's own temp folder, never the real user config.
/// </summary>
[Trait("tier", "unit")]
public sealed class UserConfigStoreClobberTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-userconfig-clobber-tests-" + Guid.NewGuid().ToString("N"));
    readonly string _path;

    public UserConfigStoreClobberTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "houseCARL.user.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* non-fatal */ }
    }

    static void SetTool(UserConfigStore s, string key, string path) => s.Update(c => (c.ToolPaths ??= new())[key] = path);

    // Probe 1: "absent file loads blank".
    [Fact]
    public void AnAbsentFileLoadsBlank()
    {
        var cfg = new UserConfigStore(_path).Load(out var note);
        Assert.Null(cfg.Mo2InstanceDir);
        Assert.Null(cfg.ToolPaths);
        Assert.Null(note);
    }

    // Probe 1: "MO2 dir survives a later tool-path write" and "tool path persisted alongside the MO2 dir".
    [Fact]
    public void TheMo2DirSurvivesALaterToolPathWrite()
    {
        var store = new UserConfigStore(_path);
        store.Update(c => c.Mo2InstanceDir = @"C:\MO2\Instance");
        SetTool(store, "bsarch", @"C:\Tools\bsarch.exe");

        var cfg = new UserConfigStore(_path).Load();
        Assert.Equal(@"C:\MO2\Instance", cfg.Mo2InstanceDir);
        Assert.Equal(@"C:\Tools\bsarch.exe", cfg.ToolPaths!["bsarch"]);
    }

    // Probe 1: "tool path survives a later MO2-dir write (no clobber, both directions)".
    [Fact]
    public void AToolPathSurvivesALaterMo2DirWrite()
    {
        var store = new UserConfigStore(_path);
        SetTool(store, "bsarch", @"C:\Tools\bsarch.exe");
        store.Update(c => c.Mo2InstanceDir = @"D:\Other");

        var cfg = store.Load();
        Assert.Equal(@"D:\Other", cfg.Mo2InstanceDir);
        Assert.Equal(@"C:\Tools\bsarch.exe", cfg.ToolPaths!["bsarch"]);
    }

    // Probe 1: "a second tool path merges; MO2 dir intact".
    [Fact]
    public void ASecondToolPathMergesAndTheMo2DirStays()
    {
        var store = new UserConfigStore(_path);
        store.Update(c => c.Mo2InstanceDir = @"D:\Other");
        SetTool(store, "bsarch", @"C:\Tools\bsarch.exe");
        SetTool(store, "papyrus_compiler", @"C:\CK\PapyrusCompiler.exe");

        var cfg = store.Load();
        Assert.Equal(2, cfg.ToolPaths!.Count);
        Assert.Equal(@"C:\Tools\bsarch.exe", cfg.ToolPaths["bsarch"]);
        Assert.Equal(@"C:\CK\PapyrusCompiler.exe", cfg.ToolPaths["papyrus_compiler"]);
        Assert.Equal(@"D:\Other", cfg.Mo2InstanceDir);
    }

    // Probe 1: "atomic write leaves no .tmp residue".
    [Fact]
    public void AWriteLeavesNoTmpResidue()
    {
        var store = new UserConfigStore(_path);
        store.Update(c => c.Mo2InstanceDir = @"C:\MO2\Instance");
        store.Update(c => c.Mo2InstanceDir = @"D:\Other");

        Assert.True(File.Exists(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    // Probe 1: "corrupt file loads blank, no throw (Q3)" and "corrupt load is REPORTED, naming the backup".
    [Fact]
    public void ACorruptFileLoadsBlankWithANoteNamingTheBackup()
    {
        var store = new UserConfigStore(_path);
        store.Update(c => c.Mo2InstanceDir = @"D:\Other");
        File.WriteAllText(_path, "{ this is not valid json");

        var cfg = store.Load(out var note);
        Assert.Null(cfg.Mo2InstanceDir);
        Assert.Contains(".corrupt.bak", note);
    }

    // Probe 1: "the corrupt original is backed up byte-for-byte beside the file".
    [Fact]
    public void TheCorruptOriginalIsBackedUpByteForByte()
    {
        File.WriteAllText(_path, "{ this is not valid json");
        var original = File.ReadAllBytes(_path);
        new UserConfigStore(_path).Load();

        Assert.Equal(original, File.ReadAllBytes(_path + ".corrupt.bak"));
    }

    // Probe 1: "Update over a corrupt file succeeds AND reports the recovery" and "the fresh file holds the new setting and reads clean".
    [Fact]
    public void AnUpdateOverACorruptFileSucceedsReportsAndLeavesAFreshFile()
    {
        File.WriteAllText(_path, "{ this is not valid json");
        var store = new UserConfigStore(_path);

        var (ok, error, note) = store.Update(c => c.Mo2InstanceDir = @"E:\Fresh");
        Assert.True(ok);
        Assert.Null(error);
        Assert.Contains(".corrupt.bak", note);

        var fresh = store.Load(out var freshNote);
        Assert.Equal(@"E:\Fresh", fresh.Mo2InstanceDir);
        Assert.Null(freshNote);
    }

    // Probe 1: "no corruption under two-store contention (every write atomic + serialized)" and "BOTH concerns' LAST values
    // survive two-store concurrent updates (no cross-process clobber)". Strengthened: every one of the 400 updates must succeed.
    [Fact]
    public async Task TwoStoresOnOneFileKeepBothConcernsLastValues()
    {
        const int rounds = 200;
        var s1 = new UserConfigStore(_path);
        var s2 = new UserConfigStore(_path);
        int failed = 0;
        var t1 = Task.Run(() =>
        {
            for (int i = 1; i <= rounds; i++)
                if (!s1.Update(c => c.Mo2InstanceDir = @"C:\Race\" + i).ok) Interlocked.Increment(ref failed);
        });
        var t2 = Task.Run(() =>
        {
            for (int i = 1; i <= rounds; i++)
                if (!s2.Update(c => (c.ToolPaths ??= new())["bsarch"] = @"C:\Race\bsarch" + i + ".exe").ok) Interlocked.Increment(ref failed);
        });
        await Task.WhenAll(t1, t2);

        Assert.Equal(0, failed);
        var final = s1.Load(out var note);
        Assert.Null(note);
        Assert.Equal(@"C:\Race\" + rounds, final.Mo2InstanceDir);
        Assert.Equal(@"C:\Race\bsarch" + rounds + ".exe", final.ToolPaths!["bsarch"]);
    }

    // Probe 7: "the last instance switch is recorded" and "BOTH tool paths survive every instance switch unchanged
    // (set once → shared across instances)".
    [Fact]
    public void ToolPathsSurviveRepeatedInstanceSwitches()
    {
        var store = new UserConfigStore(_path);
        SetTool(store, "papyrus_compiler", @"C:\CK\Papyrus Compiler\PapyrusCompiler.exe");
        SetTool(store, "bsarch", @"C:\Tools\bsarch.exe");
        for (int i = 1; i <= 5; i++) store.Update(c => c.Mo2InstanceDir = @"C:\MO2\Instance" + i);

        var cfg = store.Load();
        Assert.Equal(@"C:\MO2\Instance5", cfg.Mo2InstanceDir);
        Assert.Equal(@"C:\CK\Papyrus Compiler\PapyrusCompiler.exe", cfg.ToolPaths!["papyrus_compiler"]);
        Assert.Equal(@"C:\Tools\bsarch.exe", cfg.ToolPaths["bsarch"]);
    }
}

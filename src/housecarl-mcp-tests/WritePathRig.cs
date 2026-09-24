using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A temp folder for one write-path test: plugins written by Mutagen, a load order over them, and the output
/// paths the write lanes are pointed at. Everything it opened or built is released on dispose.</summary>
public sealed class WritePathRig : IDisposable
{
    readonly List<IDisposable> _owned = new();

    public string Root { get; }

    public WritePathRig()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-wp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Write <paramref name="mod"/> into <paramref name="folder"/> under the rig and return its path.</summary>
    public string Write(SkyrimMod mod, string folder = "plugins", params ISkyrimModGetter[] masters)
    {
        var dir = Path.Combine(Root, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, mod.ModKey.FileName.String);
        mod.BeginWrite.ToPath(path).WithLoadOrder(masters).Write();
        return path;
    }

    /// <summary>A load order over <paramref name="paths"/>, lowest priority first.</summary>
    public LoadOrderResolver Order(params string[] paths)
    {
        var r = LoadOrderResolver.Build(paths);
        _owned.Add(r);
        return r;
    }

    /// <summary>A path for a write lane's output file, in its own folder so two outputs never share one.</summary>
    public string Out(string fileName)
    {
        var dir = Path.Combine(Root, "out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    /// <summary>A fresh read-only view of a plugin on disk, released with the rig.</summary>
    public ISkyrimModGetter Open(string path)
    {
        var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        _owned.Add((IDisposable)ov);
        return ov;
    }

    /// <summary>Fails if anything still holds <paramref name="path"/> open: an exclusive open succeeds only on a free file.</summary>
    public static void AssertNotHeld(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (IOException ex) { Assert.Fail($"'{Path.GetFileName(path)}' is still held open after the call: {ex.Message}"); }
    }

    public static WritePatchBuilder.PatchEdit Set(FormKey target, string path, string value) => new()
    {
        Target = target, Path = path.Split('.'), Verb = "Set", Value = value,
    };

    public static WriteRequest Req(string recordType, string path, string verb, string? value = null, string? key = null) => new()
    {
        RecordType = recordType, Path = path.Split('.'), Verb = verb, Value = value, Key = key,
    };

    public void Dispose()
    {
        foreach (var d in _owned) { try { d.Dispose(); } catch { /* best-effort */ } }
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

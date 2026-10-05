using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>housecarl_apply with patch= omitted used to hand the resolver the default "Patch" as if the caller had
/// named it, so a disabled mod holding "Patch.esp" refused the call instead of suffixing (#1062). Driven through the
/// tool entry, where the default was applied. Each test builds its own world: it adds a mod folder and edits the modlist.</summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class ApplyDefaultStemShadowTests
{
    static void AddDisabledForeignMod(RecordsWorld w, string folder, string plugin)
    {
        var dir = Path.Combine(w.ModsDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, plugin), "a user's file houseCARL must never shadow");
        File.AppendAllText(Path.Combine(w.Instance, "profiles", "Default", "modlist.txt"), "-" + folder + "\r\n");
    }

    static string SetDamage(RecordsWorld w, string? patch) =>
        ApplyTools.Apply(w.Svc,
            ops: JsonDocument.Parse("[{\"formid\":\"" + RecordsWorld.Fid(w.Weapons[0]) + "\",\"field_path\":\"BasicStats.Damage\",\"value\":\"12\"}]").RootElement.Clone(),
            patch: patch);

    [Fact]
    public void AnOmittedPatchStepsPastAShadowedDefaultStem()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Default Mod", "Patch.esp");

        var r = SetDamage(w, null);

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), "refused: " + r);
        Assert.True(File.Exists(Path.Combine(w.ModsDir, "houseCARL - Patch_001", "Patch_001.esp")));
        Assert.Empty(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - Patch"));
    }

    [Fact]
    public void APatchNamedPatchIsStillRefusedByTheShadow()
    {
        using var w = new RecordsWorld();
        AddDisabledForeignMod(w, "Foreign Default Mod", "Patch.esp");

        var r = SetDamage(w, "Patch");

        Assert.StartsWith("error:", r);
        Assert.Contains("Foreign Default Mod", r);
        Assert.Contains("Patch.esp", r);
        Assert.Empty(Directory.EnumerateDirectories(w.ModsDir, "houseCARL - Patch*"));
    }
}

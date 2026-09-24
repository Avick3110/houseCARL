using HousecarlCore;
using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.InPlaceGuardWorld;

namespace HousecarlMcpTests;

/// <summary>An in-place write to a mod folder under ModsDir stamps <c>editedInPlace=</c> into its meta.ini and never
/// <c>generated=true</c>, so a later <c>into=</c> still refuses the user's mod; the create and remove lanes ride the same
/// acknowledgement and the same marker. Moved from the <c>inplace-guard</c> probe (arm I).</summary>
[Trait("tier", "integration")]
[Collection(InPlaceGuardCollection.Name)]
public sealed class InPlaceGuardMarkerTests
{
    readonly W _w;
    public InPlaceGuardMarkerTests(W w) { _w = w; }

    // I editedInPlace marker + into= boundary + create+remove-lane share (never generated=true)
    [Fact]
    public void AnInPlaceWriteStampsTheMarkerButNeverGeneratedAndIntoStillRefuses()
    {
        var root = _w.NewDir();
        var inst = Path.Combine(root, "instance");
        var profiles = Path.Combine(inst, "profiles", "Default");
        var mods = Path.Combine(inst, "mods");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Path.Combine(root, "game", "Data"));
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(root, "game").Replace(@"\", @"\\") + ")\r\n");
        var masterMod = Directory.CreateDirectory(Path.Combine(mods, "MasterMod")).FullName;
        File.Copy(_w.MasterPath, Path.Combine(masterMod, W.MasterName));
        var userMod = Directory.CreateDirectory(Path.Combine(mods, "UserMod")).FullName;
        var userPlugin = Path.Combine(userMod, W.UserName);
        File.Copy(_w.UserPristine, userPlugin);
        var meta = Path.Combine(userMod, "meta.ini");
        File.WriteAllText(meta, "[General]\r\ngameName=skyrimse\r\ncomments=USER SENTINEL\r\n");   // not houseCARL's
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + W.MasterName + "\r\n" + W.UserName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + W.MasterName + "\r\n*" + W.UserName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+UserMod\r\n+MasterMod\r\n");

        using var svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(root, "user.json")));
        svc.Stats();
        static bool NoGenerated(string text) => !text.Replace(" ", "").Contains("generated=true", StringComparison.OrdinalIgnoreCase);

        var edit = new[] { new BulkOp { Formid = _w.WeaponId, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "61" } };
        var wrote = svc.ApplyEdits(edit, null, null, fullReadback: false, target: W.UserName, inPlace: true, acknowledge: true);
        Assert.True(wrote.Success && wrote.InPlace, wrote.Error);
        Assert.Equal(61, W.Damage(userPlugin, _w.Weapon));
        var text = File.ReadAllText(meta);
        Assert.Contains("editedInPlace=", text);
        Assert.True(NoGenerated(text));
        Assert.Contains("USER SENTINEL", text);

        // Would succeed if the marker had written generated=true.
        Assert.False(svc.ApplyEdits(edit, null, W.UserName, fullReadback: false).Success);

        var created = svc.InPlaceGuardCreate("Keyword", "HcIP_InstKw", Array.Empty<BulkOp>(), null, null, false, null, null, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(created.Success && created.InPlace, created.Error);
        Assert.False(created.NeedsAcknowledge);
        text = File.ReadAllText(meta);
        Assert.Contains("editedInPlace=", text);
        Assert.True(NoGenerated(text));

        var removed = svc.RemoveRecords(new[] { _w.WeaponId }, null, target: W.UserName, inPlace: true, acknowledge: false);
        Assert.True(removed.Success && removed.InPlace, removed.Error);
        Assert.False(removed.NeedsAcknowledge);
        text = File.ReadAllText(meta);
        Assert.Contains("editedInPlace=", text);
        Assert.True(NoGenerated(text));
    }
}

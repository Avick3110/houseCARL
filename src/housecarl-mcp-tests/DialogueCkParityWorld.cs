using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The CK-parity create tests' instance, moved from the retired <c>dialogue-ckparity-guard</c> probe: one
/// master carrying a branch-less Custom topic, a Custom topic wired to a branch, and that branch. A create writes a
/// patch plugin into the instance, so each test builds its own world. The readers take a written patch back off
/// disk, so an assert sees the bytes MO2 would load, not the in-memory record.</summary>
internal sealed class DialogueCkParityWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }

    /// <summary>A Custom topic with no Branch (BNAM): the shape that crashes the CK's Dialogue Views editor.</summary>
    public FormKey NoBranchTopic { get; }

    /// <summary>A Custom topic wired to a branch: the same shape with BNAM set.</summary>
    public FormKey BranchedTopic { get; }

    public DialogueCkParityWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-dial-ckparity-" + Guid.NewGuid().ToString("N"));
        string instance = Path.Combine(Root, "instance");
        string profiles = Path.Combine(instance, "profiles", "Default");
        string modDir = Path.Combine(instance, "mods", "MasterMod");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(modDir);
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        var mKey = new ModKey("HcCkpMaster", ModType.Master);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var branch = m.DialogBranches.AddNew(); branch.EditorID = "HcCkpBranch";

        // Both topics carry a well-formed SNAM marker, so the only thing that differs is BNAM.
        var noBranch = m.DialogTopics.AddNew(); noBranch.EditorID = "HcCkpNoBranch";
        noBranch.Subtype = DialogTopic.SubtypeEnum.Custom;
        noBranch.SubtypeName = new RecordType("CUST");
        NoBranchTopic = noBranch.FormKey;

        var branched = m.DialogTopics.AddNew(); branched.EditorID = "HcCkpBranched";
        branched.Subtype = DialogTopic.SubtypeEnum.Custom;
        branched.SubtypeName = new RecordType("CUST");
        branched.Branch.SetTo(branch.FormKey);
        BranchedTopic = branched.FormKey;

        m.BeginWrite.ToPath(Path.Combine(modDir, mKey.FileName.String)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + mKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+MasterMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    /// <summary>Create one record through the batch entry point the create tool calls.</summary>
    public WritePatchBuilder.CreateOutcome Create(string recordType, string editorId, params BulkOp[] ops) =>
        Svc.CreateRecordsBatch(
            new[] { new CreateOp { RecordType = recordType, Editorid = editorId, Operations = ops } }, editorId, null);

    /// <summary>Create a Custom topic and one INFO under it, the INFO carrying <paramref name="infoOps"/>.</summary>
    public WritePatchBuilder.CreateOutcome CreateTopicWithInfo(string stem, params BulkOp[] infoOps) =>
        Svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp { RecordType = "DialogTopic", Editorid = stem + "Topic", Operations = new[] { Set("Subtype", "Custom") } },
                new CreateOp { RecordType = "DialogResponses", Editorid = stem + "Info", Parent = stem + "Topic", Operations = infoOps },
            },
            stem, null);

    public static BulkOp Set(string path, string value) => new() { FieldPath = path, Verb = "Set", Value = value };

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }

    static T Read<T>(string patchPath, Func<ISkyrimModGetter, T> read)
    {
        var ov = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
        try { return read(ov); }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>A written INFO off disk; it is nested in its topic, so it is found by enumeration.</summary>
    public static (FavorLevel? favor, bool hasFlags, float resetHours) ReadInfo(string patchPath, FormKey fk) => Read(patchPath, ov =>
    {
        var info = ov.EnumerateMajorRecords<IDialogResponsesGetter>().FirstOrDefault(x => x.FormKey == fk);
        return info is null ? ((FavorLevel?)null, false, 0f) : (info.FavorLevel, info.Flags is not null, info.Flags?.ResetHours ?? 0f);
    });

    /// <summary>A written DialogView's DNAM and ENAM as hex, each null when the subrecord is absent.</summary>
    public static (string? dnam, string? enam) ReadView(string patchPath, FormKey fk) => Read(patchPath, ov =>
    {
        var view = ov.DialogViews.FirstOrDefault(x => x.FormKey == fk);
        static string? Hex(Noggog.ReadOnlyMemorySlice<byte>? b) => b is { } s ? Convert.ToHexString(s.ToArray()) : null;
        return view is null ? ((string?)null, (string?)null) : (Hex(view.DNAM), Hex(view.ENAM));
    });

    /// <summary>A written DialogBranch's Category (TNAM), null when the subrecord is absent.</summary>
    public static DialogBranch.CategoryType? ReadBranchCategory(string patchPath, FormKey fk) =>
        Read(patchPath, ov => ov.DialogBranches.FirstOrDefault(x => x.FormKey == fk)?.Category);

    /// <summary>A written DialogTopic's Priority (PNAM), null only when the topic is not found.</summary>
    public static float? ReadTopicPriority(string patchPath, FormKey fk) =>
        Read(patchPath, ov => ov.DialogTopics.FirstOrDefault(x => x.FormKey == fk)?.Priority);

    /// <summary>A written Quest's NextAliasID (ANAM), null when the subrecord is absent.</summary>
    public static uint? ReadQuestNextAliasId(string patchPath, FormKey fk) =>
        Read(patchPath, ov => ov.Quests.FirstOrDefault(x => x.FormKey == fk)?.NextAliasID);
}

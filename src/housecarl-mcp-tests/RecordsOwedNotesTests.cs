using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Every note a records scan owes (predicate, unmatched, scan coverage, where_source, reverse index) reaches every lane, form, transport and manifest that consumes the scan.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsOwedNotesTests : RecordsTestBase
{
    public RecordsOwedNotesTests(RecordsFixture f) : base(f) { }

    const string MissingPlugin = "HcNoSuchScopePlugin.esp";

    // Each note's trigger, at zero matches and at some, with the words that identify that note and no other.
    static readonly Dictionary<string, (string[]? Types, string[]? Where, string? WhereSource, bool Scope, string? Refs, string Identity)> Notes = new()
    {
        ["P0"] = (new[] { "WEAP" }, new[] { "Description contains x" }, null, false, null, "predicate field 'Description'"),
        ["P+"] = (new[] { "MGEF", "SPEL", "WEAP" }, new[] { "BasicStats.Damage >= 0" }, null, false, null, "'BasicStats.Damage' had no value on"),
        ["U0"] = (new[] { "WEAP" }, new[] { "editorid in [NoSuchArmorXyz, NoSuchB]" }, null, false, null, "have no record in this selection: NoSuchArmorXyz"),
        ["U+"] = (new[] { "WEAP" }, new[] { "editorid in [NoSuchArmorXyz, HcRecW0, HcRecW1]" }, null, false, null, "have no record in this selection: NoSuchArmorXyz"),
        ["S0"] = (new[] { "WEAP" }, new[] { "editorid in [NoSuchArmorXyz]" }, null, true, null, "nothing from the 1 it does not"),
        ["S+"] = (new[] { "WEAP" }, null, null, true, null, "nothing from the 1 it does not"),
        ["W0"] = (new[] { "WEAP" }, new[] { "BasicStats.Damage > 100000" }, "winner", false, null, "where_source=winner is redundant"),
        ["W+"] = (new[] { "WEAP" }, new[] { "editorid in [HcRecW0]" }, "winner", false, null, "where_source=winner is redundant"),
        ["R0"] = (null, null, null, false, "Package", "reverse-reference index:"),
        ["R+"] = (null, null, null, false, "MgefA", "reverse-reference index:"),
    };

    // The lanes: (form, format, to_file manifest, off-order source, forward walk). A reverse walk takes formids= only, so no scan seeds one.
    static readonly Dictionary<string, (string? Form, string? Format, bool Manifest, bool OffOrder, bool Walk)> Lanes = new()
    {
        ["summary text"] = (null, null, false, false, false),
        ["summary json"] = (null, "json", false, false, false),
        ["summary dense"] = (null, "dense", false, false, false),
        ["fields text"] = ("fields", null, false, false, false),
        ["fields json"] = ("fields", "json", false, false, false),
        ["aggregate text"] = ("aggregate", null, false, false, false),
        ["aggregate json"] = ("aggregate", "json", false, false, false),
        ["summary manifest"] = (null, null, true, false, false),
        ["everything text"] = ("everything", null, false, false, false),
        ["everything json"] = ("everything", "json", false, false, false),
        ["everything manifest"] = ("everything", null, true, false, false),
        ["delta text"] = ("delta", null, false, false, false),
        ["delta json"] = ("delta", "json", false, false, false),
        ["tree text"] = ("tree", null, false, false, false),
        ["tree json"] = ("tree", "json", false, false, false),
        ["info_order text"] = ("info_order", null, false, false, false),
        ["info_order json"] = ("info_order", "json", false, false, false),
        ["off-order summary text"] = (null, null, false, true, false),
        ["off-order summary manifest"] = (null, null, true, true, false),
        ["off-order everything text"] = ("everything", null, false, true, false),
        ["off-order everything json"] = ("everything", "json", false, true, false),
        ["off-order everything manifest"] = ("everything", null, true, true, false),
        ["summary ceiling manifest"] = (null, "json", false, false, false),
        ["everything ceiling manifest"] = ("everything", "json", false, false, false),
        ["delta manifest"] = ("delta", null, true, false, false),
        ["tree manifest"] = ("tree", null, true, false, false),
        ["info_order manifest"] = ("info_order", null, true, false, false),
        ["chain manifest"] = ("chain", null, true, false, true),
        ["delta ceiling manifest"] = ("delta", "json", false, false, false),
        ["tree ceiling manifest"] = ("tree", "json", false, false, false),
        ["info_order ceiling manifest"] = ("info_order", "json", false, false, false),
        ["chain ceiling manifest"] = ("chain", "json", false, false, true),
        ["walk summary text"] = (null, null, false, false, true),
        ["walk everything json"] = ("everything", "json", false, false, true),
        ["walk summary manifest"] = (null, null, true, false, true),
        ["walk everything manifest"] = ("everything", null, true, false, true),
        ["walk everything ceiling manifest"] = ("everything", "json", false, false, true),
    };

    // An off-order file scan owes only the notes its engine sets: no where_source, no reverse index, and its one weapon cannot make the mixed-type soft note.
    // A ceiling spill needs rows to cut, so it is judged at some matches only. A walk takes no where= or references=, so only a scope's coverage note reaches it.
    static bool Owes(string note, string lane) =>
        !((lane.StartsWith("chain") || lane.StartsWith("walk")) && note != "S+") &&
        !(lane.StartsWith("off-order") && note is "W0" or "W+" or "R0" or "R+" or "P+")
        && !(lane.Contains("ceiling") && note.EndsWith('0'));

    public static TheoryData<string, string> Cells()
    {
        var d = new TheoryData<string, string>();
        foreach (var lane in Lanes.Keys)
            foreach (var note in Notes.Keys)
                if (Owes(note, lane)) d.Add(note, lane);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void TheOwedNoteArrives(string note, string lane)
    {
        var n = Notes[note];
        var l = Lanes[lane];
        string? art = l.Manifest ? W.Scratch("owed-notes", Guid.NewGuid().ToString("N") + ".jsonl") : null;
        RecordsTools.RecordsProject? project = l.Form switch
        {
            null => null,
            "fields" => Fields("EditorID"),
            "aggregate" => new() { form = "aggregate", group_by = "winner" },
            _ => Form(l.Form),
        };
        var r = RecordsTools.Records(Svc, types: n.Types, where: n.Where, where_source: n.WhereSource,
                                     plugins: n.Scope ? Scope(W.MasterName, MissingPlugin) : null,
                                     references: n.Refs is null ? null : new[] { Fid(n.Refs == "MgefA" ? W.MgefA : W.Package) },
                                     source: l.OffOrder ? Plugin(W.OldName) : null,
                                     versus: l.Form == "delta" ? Plugin(W.MasterName) : null,
                                     walk: l.Walk ? new RecordsTools.RecordsWalk { depth = 1 } : null,
                                     project: project, format: l.Format, to_file: art,
                                     max_chars: lane.Contains("ceiling") ? 300 : 0);
        if (lane.Contains("ceiling")) art = Je(r).GetProperty("spilled").GetProperty("path").GetString();

        Assert.False(r.StartsWith("error:") || r.Contains("\"ok\": false"), "refused: " + First(r));
        if (art is not null)
        {
            var manifest = Je(File.ReadLines(art).First());   // the manifest line, read raw so a 0-row artifact is judged too
            Assert.True(manifest.TryGetProperty("notes", out var mNotes), "manifest carries no notes");
            Assert.Contains(mNotes.EnumerateArray(), e => e.GetString()!.Contains(n.Identity));
        }
        else if (l.Format is null)
            Assert.Contains(n.Identity, r);
        else
            Assert.Contains(Je(r).GetProperty("notes").EnumerateArray(), e => e.GetString()!.Contains(n.Identity));
    }

    // A body or comparison lane's ceiling spill says the manifest names every unmatched member, as the scan lane does, not "to_file= keeps the whole list".
    [Theory]
    [InlineData(false, null, "everything")]
    [InlineData(false, "json", "everything")]
    [InlineData(true, null, "everything")]
    [InlineData(true, "json", "everything")]
    [InlineData(false, null, "delta")]
    [InlineData(false, "json", "delta")]
    [InlineData(true, null, "delta")]
    [InlineData(false, null, "tree")]
    [InlineData(false, "json", "info_order")]
    public void ACeilingSpillPointsAtItsManifest(bool offOrder, string? format, string form)
    {
        var names = Enumerable.Range(1, 21).Select(i => $"NoSuchArmor{i:D2}").Prepend("HcRecW1").Prepend("HcRecW0");
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { $"editorid in [{string.Join(", ", names)}]" },
                                     source: offOrder ? Plugin(W.OldName) : null, project: Form(form),
                                     versus: form == "delta" ? Plugin(W.MasterName) : null,
                                     format: format, max_chars: 300);

        Assert.True(r.Contains("spilled"), r);
        Assert.Contains("notes name them all", r);   // json escapes the apostrophe
        Assert.DoesNotContain("keeps the whole list", r);
    }
}

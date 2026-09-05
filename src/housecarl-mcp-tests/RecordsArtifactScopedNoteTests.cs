using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The scoped-vs-winner field-source note on a <c>to_file=</c> artifact. The three inline transports all warn that a
/// <c>plugins=</c>-scoped fields scan is showing each match's scoped plugin's own values rather than the live winner;
/// the artifact carries the same values and is read later with no conversation attached, so it has to carry the same
/// sentence. Its home is the manifest — line 1, the row set's one header, and what a re-entering caller reads first.
/// </summary>
[Trait("tier", "integration")]
public sealed class RecordsArtifactScopedNoteTests : ArtifactTestBase, IClassFixture<ArtifactFixture>
{
    public RecordsArtifactScopedNoteTests(ArtifactFixture f) : base(f) { }

    static RecordsTools.RecordsProject Damage => new() { form = "fields", fields = new[] { "BasicStats.Damage" } };

    RecordsTools.RecordsScope Master => new() { names = new[] { W.MasterName } };

    /// <summary>The note the INLINE json answer to the same call carries — read off the product rather than spelled
    /// here, so the artifact is pinned to the inline wording and the two cannot drift.</summary>
    string InlineNote(string? fieldsSource = null)
    {
        var json = Je(RecordsTools.Records(Svc, types: new[] { "WEAP" }, plugins: Master, project: Damage,
                                           fields_source: fieldsSource, format: "json"));
        return json.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!)
                   .Single(n => n.Contains("field values", StringComparison.Ordinal));
    }

    [Fact]
    public void ToFile_TheManifestCarriesTheScopedFieldsNoteTheInlineRendersCarry()
    {
        var art = Art("scoped-note.jsonl");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, plugins: Master, project: Damage, to_file: art);

        var notes = ManifestOf(art).Notes;
        Assert.NotNull(notes);
        Assert.Contains(InlineNote(), notes!);
    }

    /// <summary>The note is a 4-way matrix over the two poles, so the artifact must state the arm this call is on —
    /// not a fixed sentence that happens to be right on the default.</summary>
    [Fact]
    public void ToFile_TheManifestNoteFollowsTheDisplayPole()
    {
        var art = Art("scoped-note-winner.jsonl");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, plugins: Master, project: Damage,
                             fields_source: "winner", to_file: art);

        Assert.Contains(InlineNote("winner"), ManifestOf(art).Notes!);
    }

    /// <summary>No scoped bodies, no note: an unscoped scan reads the winner, and a sentence saying otherwise would
    /// be a warning about something that did not happen.</summary>
    [Fact]
    public void ToFile_AnUnscopedScanCarriesNoScopedFieldsNote()
    {
        var art = Art("scoped-note-none.jsonl");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Damage, to_file: art);

        var notes = ManifestOf(art).Notes ?? Array.Empty<string>();
        Assert.DoesNotContain(notes, n => n.Contains("field values", StringComparison.Ordinal));
    }

    /// <summary>A summary artifact carries no field values at all, so it is owed no field-source note.</summary>
    [Fact]
    public void ToFile_ASummaryArtifactCarriesNoScopedFieldsNote()
    {
        var art = Art("scoped-note-summary.jsonl");
        RecordsTools.Records(Svc, types: new[] { "WEAP" }, plugins: Master, to_file: art);

        var notes = ManifestOf(art).Notes ?? Array.Empty<string>();
        Assert.DoesNotContain(notes, n => n.Contains("field values", StringComparison.Ordinal));
    }
}

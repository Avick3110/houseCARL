using System.Text.Json;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// <see cref="Facts"/>' own arms. Two claims per helper: the LOUD path fires on the shape it exists to
/// refuse, and the GREEN path navigates a real tool response.
///
/// <para>The loud paths are driven over hand-built json rather than a fixture, deliberately: the point of
/// each is a document a real lane will not produce — two nodes carrying one formid, a record that is not
/// there, a member nobody wrote. A helper whose loudness is only ever exercised by accident is a helper
/// whose loudness nobody has seen.</para>
///
/// <para>The green paths drive <c>housecarl_records</c> over <see cref="RecordsWorld"/>, so the navigation is
/// proven against the wire shape the product actually emits rather than against a shape this file imagined.
/// </para>
///
/// <para><b>No assertion here spells a sentence.</b> Every expected value is a fixture symbol, a catalogue
/// member, or a whitespace-free wire/field token named once — which is the shape <see cref="TestProseGuardTests"/>
/// asks for, demonstrated on the file that introduces the helper.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class FactsTests : RecordsTestBase
{
    public FactsTests(RecordsFixture f) : base(f) { }

    // Whitespace-free tokens, named once: the field path this file drives and reads back, and the wire
    // members it asks for. Naming them is what keeps every assertion below literal-free.
    const string Damage = "BasicStats.Damage";
    const string Weight = "BasicStats.Weight";
    const string Formid = "formid";
    const string EditorId = "editorid";
    const string Value = "value";
    const string Count = "count";
    const string Truncated = "truncated";
    const string Distinct = "distinct";
    const string Complete = "complete";
    const string Name = "name";

    /// <summary>A FormKey that is real, valid, and in nothing — the absent-subject case, which must throw
    /// rather than report an empty answer.</summary>
    static FormKey Absent => FormKey.Factory("FF00FF:HcAbsent.esp");

    static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    // ---- Facts.Record ----------------------------------------------------------------------------------

    [Fact]
    public void Record_TwoNodesCarryingTheSameFormid_Throws_RatherThanPickingOne()
    {
        var key = W.Weapons[0];
        var fid = Facts.Fid(key);
        using var doc = Doc($$"""{"records":[{"formid":"{{fid}}","a":1},{"formid":"{{fid}}","a":2}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Record(doc, key));
        Assert.Contains(fid, ex.Message);
    }

    [Fact]
    public void Record_NoNodeCarryingTheFormid_Throws_NamingWhatIsThereInstead()
    {
        var present = Facts.Fid(W.Weapons[0]);
        using var doc = Doc($$"""{"records":[{"formid":"{{present}}"}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Record(doc, Absent));
        Assert.Contains(Facts.Fid(Absent), ex.Message);
        Assert.Contains(present, ex.Message);          // it says what IS here, not just that the subject is not
    }

    [Fact]
    public void Record_OverARealResponse_ReachesTheOneRecordItIsAbout()
    {
        using var doc = JsonDocument.Parse(Json());

        var rec = Facts.Record(doc, W.Weapons[1]);

        Assert.Equal(Facts.Fid(W.Weapons[1]), Facts.Text(rec, Formid));
        Assert.Equal(W.WeaponBodies[1].EditorID, Facts.Text(rec, EditorId));
    }

    // ---- Facts.Plugin ----------------------------------------------------------------------------------

    [Fact]
    public void Plugin_TwoNodesCarryingTheSameName_Throws_RatherThanPickingOne()
    {
        using var doc = Doc($$"""{"rows":[{"plugin":"{{W.MasterName}}"},{"plugin":"{{W.MasterName}}"}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Plugin(doc, W.MasterName));
        Assert.Contains(W.MasterName, ex.Message);
    }

    [Fact]
    public void Plugin_NoNodeCarryingThatName_Throws_NamingThePluginsThatAreThere()
    {
        using var doc = Doc($$"""{"rows":[{"plugin":"{{W.MasterName}}"},{"plugin":"{{W.MidName}}"}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Plugin(doc, W.OverrideName));
        Assert.Contains(W.MasterName, ex.Message);
        Assert.Contains(W.MidName, ex.Message);
    }

    [Fact]
    public void Plugin_OverARealResponse_ReachesTheNamedPole()
    {
        using var doc = JsonDocument.Parse(Delta());

        Assert.Equal(W.MidName, Facts.Text(Facts.Plugin(doc, W.MidName), "plugin"));
    }

    // ---- Facts.Field -----------------------------------------------------------------------------------

    [Fact]
    public void Field_TwoFieldNodesAtOnePath_Throws_RatherThanPickingOne()
    {
        using var doc = Doc($$"""{"fields":[{"path":"{{Damage}}","value":"1"},{"path":"{{Damage}}","value":"2"}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Field(doc.RootElement, Damage));
        Assert.Contains(Damage, ex.Message);
    }

    [Fact]
    public void Field_APathTheLaneDidNotRender_Throws_NamingWhatItDidRender()
    {
        using var doc = Doc($$"""{"fields":[{"path":"{{Damage}}","value":"20"}]}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Field(doc.RootElement, Weight));
        Assert.Contains(Damage, ex.Message);
    }

    [Fact]
    public void Field_OverARealResponse_ReachesTheFieldInsideTheRecordItIsAbout()
    {
        using var doc = JsonDocument.Parse(Json());

        var f = Facts.Field(Facts.Record(doc, W.Weapons[1]), Damage);

        Assert.Equal(DamageOf(1), Facts.Text(f, Value));
    }

    // ---- Facts.Number / Text / Flag --------------------------------------------------------------------

    [Fact]
    public void Number_AnAbsentMember_Throws_RatherThanReadingAsZero()
    {
        using var doc = Doc($$"""{"{{Distinct}}":2}""");

        var ex = Assert.ThrowsAny<Exception>(() => Facts.Number(doc.RootElement, Count));
        Assert.Contains(Count, ex.Message);
        Assert.Contains(Distinct, ex.Message);         // and it says which members ARE there
    }

    [Fact]
    public void Text_AnAbsentMember_Throws_RatherThanReadingAsEmpty()
    {
        using var doc = Doc($$"""{"{{EditorId}}":"x"}""");

        Assert.Contains(Name, Assert.ThrowsAny<Exception>(() => Facts.Text(doc.RootElement, Name)).Message);
    }

    [Fact]
    public void Flag_AnAbsentMember_Throws_RatherThanReadingAsFalse()
    {
        using var doc = Doc($$"""{"{{Complete}}":true}""");

        Assert.Contains(Truncated, Assert.ThrowsAny<Exception>(() => Facts.Flag(doc.RootElement, Truncated)).Message);
    }

    [Fact]
    public void TheTypedReads_RefuseAMemberOfTheWrongKind_RatherThanCoercingIt()
    {
        using var doc = Doc($$"""{"{{Count}}":"12","{{EditorId}}":12,"{{Complete}}":"true"}""");

        Assert.ThrowsAny<Exception>(() => Facts.Number(doc.RootElement, Count));
        Assert.ThrowsAny<Exception>(() => Facts.Text(doc.RootElement, EditorId));
        Assert.ThrowsAny<Exception>(() => Facts.Flag(doc.RootElement, Complete));
    }

    [Fact]
    public void TheTypedReads_OverARealResponse_ReadTheMembersTheWireCarries()
    {
        using var doc = JsonDocument.Parse(Json());

        Assert.Equal(W.Weapons.Count, Facts.Number(doc.RootElement, Count));
        Assert.False(Facts.Flag(doc.RootElement, Truncated));
    }

    // ---- Facts.SoleSubject -----------------------------------------------------------------------------

    [Fact]
    public void SoleSubject_AResponseNamingTwoRecords_Throws_NamingWhatItFound()
    {
        var r = TextOf(AllWeaponIds);

        var ex = Assert.ThrowsAny<Exception>(() => Facts.SoleSubject(r, W.Weapons[0]));
        Assert.Contains(Facts.Fid(W.Weapons[0]), ex.Message);
        Assert.Contains(Facts.Fid(W.Weapons[1]), ex.Message);
    }

    [Fact]
    public void SoleSubject_AResponseAboutADifferentRecord_Throws()
    {
        var r = TextOf(new[] { Fid(W.Weapons[0]) });

        Assert.Contains(Facts.Fid(W.Weapons[0]),
                        Assert.ThrowsAny<Exception>(() => Facts.SoleSubject(r, W.Weapons[1])).Message);
    }

    [Fact]
    public void SoleSubject_AResponseNamingNoFormIdAtAll_Throws_RatherThanPassingVacuously()
    {
        Assert.Contains(Facts.Fid(W.Weapons[0]),
                        Assert.ThrowsAny<Exception>(() => Facts.SoleSubject("", W.Weapons[0])).Message);
    }

    [Fact]
    public void SoleSubject_AOneRecordResponse_HandsBackTheWholeTextUnsliced()
    {
        var r = TextOf(new[] { Fid(W.Weapons[0]) });

        Assert.Same(r, Facts.SoleSubject(r, W.Weapons[0]));
    }

    // ---- Facts.States ----------------------------------------------------------------------------------

    [Fact]
    public void States_AnEmptiedSentence_Throws_RatherThanPassingOverAnyTextAtAll() =>
        Assert.ThrowsAny<Exception>(() => Facts.States(TextOf(AllWeaponIds), ""));

    [Fact]
    public void States_ASentenceOfNothingButHoles_ThrowsForTheSameReason() =>
        Assert.ThrowsAny<Exception>(() => Facts.States(TextOf(AllWeaponIds), "{0}{1}"));

    [Fact]
    public void States_ATextThatDoesNotCarryTheSentence_Throws_NamingTheSegmentThatIsMissing()
    {
        var ex = Assert.ThrowsAny<Exception>(() => Facts.States(TextOf(AllWeaponIds), ReadSentences.NoDeclarers));

        Assert.Contains(ReadSentences.NoDeclarers, ex.Message);
    }

    [Fact]
    public void States_ATemplatesSegmentsAreRequiredInOrder_SoAShuffledRenderFails()
    {
        // The two literal segments of " {0} of {1} plugin section(s) were rendered.", deliberately reversed.
        var segments = ReadSentences.SweepSections.Split(new[] { "{0}", "{1}" }, StringSplitOptions.None);
        var shuffled = segments[2] + segments[1] + segments[0];

        Assert.ThrowsAny<Exception>(() => Facts.States(shuffled, ReadSentences.SweepSections));
    }

    [Fact]
    public void States_ATemplateTheSurfaceComposed_Passes_WithoutThisTestSpellingAnyOfIt()
    {
        var composed = string.Format(ReadSentences.SweepSections, 1, 2);

        Facts.States(TextOf(AllWeaponIds) + composed + TextOf(AllWeaponIds), ReadSentences.SweepSections);
    }

    /// <summary>
    /// Every catalogue sentence whose longest literal run is under the threshold — DERIVED, so a short
    /// sentence added tomorrow is in this arm the day it lands rather than when someone remembers. Each one
    /// PASSED over unrelated text while the emptiness check was the whole guard: an arm that cannot fail,
    /// arriving through the very disposition the prose guard recommends.
    /// </summary>
    [Fact]
    public void States_EverySentenceItCannotIdentify_IsRefused_NotAssertedVacuously()
    {
        var unrelated = TextOf(AllWeaponIds);

        var unidentifiable = SentenceCatalogue.Members(typeof(ReadSentences))
            .Where(m => m.Kind == SentenceCatalogue.Shape.Value)
            .Select(m => (m.Name, Text: SentenceCatalogue.Value(typeof(ReadSentences), m.Name) as string))
            .Where(x => x.Text is not null && !Facts.Identifiable(x.Text))
            .ToArray();

        var passed = new List<string>();
        foreach (var (name, text) in unidentifiable)
        {
            // The probe text CARRIES every literal segment of the sentence, in order, so a States that did
            // not refuse would pass. Without this the arm's strength would depend on whether the response
            // happened to contain a bracket, which is the vacuity it is about.
            var carries = unrelated + " " + text!.Replace("{", "").Replace("}", "");

            try { Facts.States(carries, text!); passed.Add($"{name} ({Facts.LongestRun(text!)} run)"); }
            catch (Exception) { /* refused, which is the claim */ }
        }

        Assert.True(passed.Count == 0,
            "Facts.States asserted these sentences over text that has nothing to do with them:\n  " +
            string.Join("\n  ", passed) +
            $"\nA run under {Facts.IdentifiableRun} non-space characters is not an identity, and an arm over " +
            "one passes on almost any response.");

        // The arm is never vacuous: a sentence gutted to punctuation is refused whether or not the catalogue
        // currently carries a short member, and over a text that does carry the segment.
        Assert.ThrowsAny<Exception>(() => Facts.States(unrelated + " . ", "{0}.{1}"));
    }

    // ---- driving ---------------------------------------------------------------------------------------

    string Json() =>
        RecordsTools.Records(Svc, formids: AllWeaponIds, format: "json", project: Fields(Damage));

    string TextOf(string[] formids) =>
        RecordsTools.Records(Svc, formids: formids, project: Fields(Damage));

    string Delta() =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, source: Plugin(W.OverrideName),
                             versus: Plugin(W.MidName), format: "json",
                             project: new RecordsTools.RecordsProject { form = "delta" });

    /// <summary>The fixture's own damage value for weapon <paramref name="i"/>, as the wire spells it.</summary>
    string DamageOf(int i) => ((IWeaponGetter)W.WeaponBodies[i]).BasicStats!.Damage.ToString();
}

using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A FormLink set to a record of the WRONG type (#663). Pre-flight checked that the value parsed as a FormID and
/// stopped there, so pointing an armor's Race at a spell passed and wrote a plugin that is valid on disk and wrong
/// in game. The gate now resolves the FormID and compares the record's own type against the link's Mutagen target.
///
/// <para>Dry runs: the shared world must stay unwritten.</para>
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class FormLinkTargetTypeTests : RecordsTestBase
{
    public FormLinkTargetTypeTests(RecordsFixture f) : base(f) { }

    string Apply(string formid, string path, string op, string value) => ApplyTools.Apply(Svc,
        ops: Je($@"[{{""formid"":""{formid}"",""field_path"":""{path}"",""op"":""{op}"",""value"":""{value}""}}]"),
        dry_run: true);

    /// <summary>The reported shape: a singular link handed a record of another type is refused, naming the field,
    /// what the FormID actually is, and what the field takes.</summary>
    [Fact]
    public void ASingularLinkToTheWrongRecordTypeIsRefused()
    {
        var r = Apply(Fid(W.Armor), "Race", "Set", Fid(W.SpellA));

        Refused(r, "'Race'", "is a Spell", "links to Race");
    }

    /// <summary>…and the same value added to a link LIST, which is the other half of the reported call.</summary>
    [Fact]
    public void AListElementLinkToTheWrongRecordTypeIsRefused()
    {
        var r = Apply(Fid(W.Armor), "Keywords", "Add", Fid(W.SpellA));

        Refused(r, "'Keywords'", "is a Spell", "links to Keyword");
    }

    /// <summary>A link handed the type it declares still lands — the check refuses a mismatch, not a FormID.</summary>
    [Fact]
    public void ALinkToTheDeclaredTypeStillPasses()
        => Served(Apply(Fid(W.Weapons[0]), "Template", "Set", Fid(W.Weapons[1])), "Set Template");

    /// <summary>A FormList's Items link to any record at all, so nothing put in one is a type mismatch.</summary>
    [Fact]
    public void ALinkThatAcceptsAnyRecordIsNotRefused()
        => Served(Apply(Fid(W.BigList), "Items", "Add", Fid(W.Armor)), "Add Items");

    /// <summary>A FormID no plugin in the order defines is not type-checked: the order cannot say what it is, and a
    /// link to a record that is not there is the dangling-reference check's business, not this gate's.</summary>
    [Fact]
    public void AnUnresolvableTargetIsNotTypeChecked()
        => Served(Apply(Fid(W.Armor), "Race", "Set", "ABCDEF:" + W.MasterName), "Set Race");

    /// <summary>A null-clear points at nothing, so it is not a wrong type.</summary>
    [Fact]
    public void ANullClearIsNotTypeChecked()
        => Served(Apply(Fid(W.Armor), "Race", "Set", "Null"), "Set Race");
}

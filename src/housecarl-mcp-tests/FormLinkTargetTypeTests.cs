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

    /// <summary>The create lane's own version of "the shared world must stay unwritten": CreateRecordsBatch has no
    /// dry_run, so a refused create is only clean if it wrote no patch. Removes the mod folder first if a regression
    /// left one, so one broken gate fails its own test instead of cascading into every later test in the
    /// collection.</summary>
    void AssertNoPatchWritten(string patchStem)
    {
        var left = Directory.EnumerateDirectories(W.ModsDir, "houseCARL - " + patchStem + "*").ToList();
        foreach (var dir in left) Directory.Delete(dir, recursive: true);
        Assert.Empty(left);
    }

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

    /// <summary>A composed struct's own FormLink field is a link slot like any other — an Effect built with a
    /// BaseEffect that is not a magic effect is refused at the compose, not at a leaf.</summary>
    [Fact]
    public void AComposedStructFieldLinkToTheWrongRecordTypeIsRefused()
    {
        var r = ApplyTools.Apply(Svc,
            ops: Je($@"[{{""formid"":""{Fid(W.SpellA)}"",""field_path"":""Effects"",""op"":""Add"",""compose"":
                {{""type"":""Effect"",""fields"":{{""BaseEffect"":""{Fid(W.SpellB)}""}}}}}}]"),
            dry_run: true);

        Refused(r, "'BaseEffect'", "is a Spell", "links to MagicEffect");
    }

    /// <summary>A link two composition levels down — a composed struct's NESTED set into a sub-struct's own FormLink
    /// field — is checked like any other. The value slots are enumerated by the rulebook's own walk, so a slot the
    /// check reads is a slot the harvest resolved; nothing here depends on a hand-kept list of where links can sit.</summary>
    [Fact]
    public void ANestedComposedLinkToTheWrongRecordTypeIsRefused()
    {
        var r = ApplyTools.Apply(Svc,
            ops: Je($@"[{{""formid"":""{Fid(W.NpcParent)}"",""field_path"":""Items"",""op"":""Add"",""compose"":
                {{""type"":""ContainerEntry"",""sets"":[{{""path"":""Item.Item"",""value"":""{Fid(W.SpellA)}""}},
                {{""path"":""Item.Count"",""value"":""1""}}]}}}}]"),
            dry_run: true);

        Refused(r, "'Item'", "is a Spell");
    }

    /// <summary>Removing a link is exempt: a list may already carry a wrong-typed FormID (another mod wrote it), and
    /// the Remove that repairs it must not be refused for naming the very type it is taking out. The call still
    /// fails — this armor has no Keywords at all — but on the LIST, never on the value's type.</summary>
    [Fact]
    public void RemovingALinkByValueIsNotTypeChecked()
    {
        var r = Apply(Fid(W.Armor), "Keywords", "Remove", Fid(W.SpellA));

        Refused(r, "nothing to remove");
        Assert.DoesNotContain("links to Keyword", r);
    }

    /// <summary>The create lane runs the same gate: a brand-new record whose link names the wrong type is refused
    /// before anything is allocated.</summary>
    [Fact]
    public void ACreatedRecordsLinkToTheWrongRecordTypeIsRefused()
    {
        var o = Svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp
                {
                    RecordType = "Armor", Editorid = "HcLinkTypeArmor",
                    Operations = new[] { new BulkOp { FieldPath = "Race", Verb = "Set", Value = Fid(W.SpellA) } },
                },
            },
            "HcLinkTypeCreatePatch", null);

        Assert.False(o.Success);
        Assert.Contains("'Race'", o.Error);
        Assert.Contains("is a Spell", o.Error);
        Assert.Contains("links to Race", o.Error);
        AssertNoPatchWritten("HcLinkTypeCreatePatch");
    }

    /// <summary>A create's ReplaceAll may mix same-call '@editorid' siblings with literal FormIDs. The sibling has no
    /// record yet, so it is not type-checked; the literal beside it is.</summary>
    [Fact]
    public void ALiteralBesideASameCallSiblingIsTypeChecked()
    {
        var o = Svc.CreateRecordsBatch(
            new[]
            {
                new CreateOp { RecordType = "Keyword", Editorid = "HcLinkTypeKw" },
                new CreateOp
                {
                    RecordType = "Armor", Editorid = "HcLinkTypeArmor2",
                    Operations = new[]
                    {
                        new BulkOp
                        {
                            FieldPath = "Keywords", Verb = "ReplaceAll",
                            Values = new[] { "@HcLinkTypeKw", Fid(W.SpellA) },
                        },
                    },
                },
            },
            "HcLinkTypeSiblingPatch", null);

        Assert.False(o.Success);
        Assert.Contains("is a Spell", o.Error);
        Assert.Contains("links to Keyword", o.Error);
        AssertNoPatchWritten("HcLinkTypeSiblingPatch");
    }
}

using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The two pre-flight refusals a caller hits when editing one element of a POLYMORPHIC collection — an AI
/// package's <c>Data</c> dict of <c>APackageData</c> arms is the reported shape (#319). Both were correct and both
/// dead-ended: neither named the call that works, so landing one edit took four tries.
///
/// <para>Corpus-only, so it needs no records — the world is here for the generated corpus
/// <c>CorpusRulebook.CorpusPath</c> points at.</para>
/// </summary>
[Trait("tier", "integration")]
[Collection("bulk-records")]
public sealed class ElementRefusalRemedyTests : BulkRecordsTestBase
{
    public ElementRefusalRemedyTests(BulkRecordsFixture f) : base(f) { }

    static CorpusRulebook Book() => CorpusRulebook.Load();

    static string Refusal(string[] path, string verb, string? key = null, string? value = null)
    {
        var r = Book().Validate(new WriteRequest
        {
            RecordType = "Package", Path = path, Verb = verb, Key = key, Value = value,
        });
        Assert.NotNull(r);
        return r!;
    }

    /// <summary>Reaching THROUGH a polymorphic element to a field whose arms disagree on shape: the validator still
    /// refuses to pick an arm, but it now hands back the container call — the field, the caller's own key, and the
    /// verbs that shape takes — instead of "target a field whose shape is unambiguous".</summary>
    [Fact]
    public void AConflictingArmFieldUnderADictElementNamesTheContainerCall()
    {
        var r = Refusal(new[] { "Data[7]", "Data" }, "Set", value: "True");

        Assert.Contains("CONFLICTING shapes", r);
        Assert.Contains("field_path='Data'", r);
        Assert.Contains("key='7'", r);
        Assert.Contains("compose=", r);
    }

    /// <summary>…and the verbs it names are the DICT ones. Naming <c>SetAtIndex</c> here is the wrong turn the
    /// reporter took on their second call.</summary>
    [Fact]
    public void TheContainerCallOnADictNamesNoIndexVerb()
        => Assert.DoesNotContain("SetAtIndex", Refusal(new[] { "Data[7]", "Data" }, "Set", value: "True"));

    /// <summary>A bracket at the LEAF was already told the rule and the verb menu; it now also carries the path and
    /// the key the caller typed, which is the difference between a shape to fill in and a call to make.</summary>
    [Fact]
    public void ABracketedLeafNamesThePathAndKeyTheCallerTyped()
    {
        var r = Refusal(new[] { "Data[7]" }, "Set");

        Assert.Contains("brackets a collection element at the LEAF", r);
        Assert.Contains("field_path='Data'", r);
        Assert.Contains("key='7'", r);
    }

    /// <summary>The container path keeps the hops ABOVE it — they are still navigation — and drops only its own
    /// bracket, so a nested container is named in full rather than by its last segment. Pinned to the refusal that
    /// produces it: the whole-path claim is only about the LEAF-bracket remedy, and a walk that started refusing
    /// somewhere earlier could satisfy the path assertion while proving nothing.</summary>
    [Fact]
    public void ANestedContainerIsNamedByItsWholePath()
    {
        var r = Book().Validate(new WriteRequest
        {
            RecordType = "Npc", Path = new[] { "VirtualMachineAdapter", "Scripts[0]", "Properties[0]" },
            Verb = "Set",
        });

        Assert.NotNull(r);
        Assert.Contains("brackets a collection element at the LEAF", r!);
        Assert.Contains("field_path='VirtualMachineAdapter.Scripts[0].Properties'", r);
        Assert.Contains("key='0'", r);
    }

    /// <summary>The remedy for "write the whole element, composing that arm" names the verbs that PLACE one element
    /// — not the keyed menu, which answers a different question and leads with a verb that deletes the element the
    /// caller came to edit.</summary>
    [Fact]
    public void TheContainerCallNamesNoDeleteVerb()
    {
        var r = Refusal(new[] { "Data[7]", "Data" }, "Set", value: "True");

        Assert.Contains("composing that arm", r);
        Assert.Contains("Set (compose= + key=)", r);
        Assert.Contains("Add (compose= + key=)", r);
        Assert.DoesNotContain("Remove", r);
    }

    // ---- the slot a named path belongs in ----

    /// <summary>Rooted inside a compose's nested <c>sets</c>, the path the remedy names is relative to the STRUCT
    /// being built and the caller typed it in the <c>path</c> slot. Printing <c>field_path=</c> there would name a
    /// top-level call that resolves no such field — the exact dead end this refusal exists to close.</summary>
    [Fact]
    public void AComposeNestedRefusalNamesTheComposeSlot()
    {
        var r = Book().Validate(new WriteRequest
        {
            RecordType = "Npc", Path = new[] { "VirtualMachineAdapter", "Scripts" }, Verb = "Add",
            Struct = new StructSpec
            {
                Type = "ScriptEntry",
                Sets = new() { new WriteRequest { RecordType = "Npc", Path = new[] { "Properties[0]" }, Verb = "Set" } },
            },
        });

        Assert.NotNull(r);
        Assert.Contains("brackets a collection element at the LEAF", r!);
        Assert.Contains("path='Properties'", r);
        Assert.DoesNotContain("field_path=", r);
    }

    // ---- a key is only handed back once it is known to work ----

    /// <summary>A key that cannot index this collection is NOT promoted into the remedy. <c>Package.Data</c> is
    /// keyed by <c>sbyte</c>, so echoing 'notasbyte' back would name a call that throws FormatException at apply —
    /// the accept-then-throw the mid-path hop already guards against. The rule and the verb menu still stand.</summary>
    [Fact]
    public void AMalformedDictKeyIsNotEchoedAsACallToMake()
    {
        var r = Refusal(new[] { "Data[notasbyte]" }, "Set");

        Assert.Contains("brackets a collection element at the LEAF", r);
        Assert.DoesNotContain("key='notasbyte'", r);
        Assert.DoesNotContain("field_path=", r);
        Assert.Contains("Set (compose= + key=)", r);   // the menu is still shape-derived
    }

    /// <summary>Same gate on the LIST half: a negative index parses as an int but is refused by apply, so it never
    /// becomes a suggested call either.</summary>
    [Fact]
    public void ANegativeListIndexIsNotEchoedAsACallToMake()
    {
        var r = Book().Validate(new WriteRequest { RecordType = "Perk", Path = new[] { "Effects[-3]" }, Verb = "Set" });

        Assert.NotNull(r);
        Assert.Contains("brackets a collection element at the LEAF", r!);
        Assert.DoesNotContain("key='-3'", r);
        Assert.Contains("SetAtIndex", r);
    }

    /// <summary>…and a well-formed key of the same shapes still IS handed back, so the gate above narrows the
    /// remedy rather than removing it.</summary>
    [Fact]
    public void AWellFormedListIndexIsStillHandedBack()
    {
        var r = Book().Validate(new WriteRequest { RecordType = "Perk", Path = new[] { "Effects[3]" }, Verb = "Set" });

        Assert.NotNull(r);
        Assert.Contains("field_path='Effects'", r!);
        Assert.Contains("key='3'", r);
    }

    // ---- the engine twin ----

    /// <summary>The engine's own leaf-bracket throw, driven directly: pre-flight normally refuses first, so this
    /// message only ever reaches a caller who bypassed the gate (a direct / CLI call) — which is precisely why it
    /// must carry the same facts rather than a shorter version of them.</summary>
    static string EngineThrow(string[] path) => Assert.Throws<InvalidOperationException>(
        () => WriteEngine.ApplyVerb(new Perk(FormKey.Null, SkyrimRelease.SkyrimSE),
            new WriteRequest { RecordType = "Perk", Path = path, Verb = "Set" })).Message;

    /// <summary>The two recognisers must agree. Both are asked about the same bracketed leaf and must name the same
    /// container path, the same key, and the same verb menu — the rulebook off the corpus, the engine off the live
    /// property type. A drift in either (including the hand-rolled path join this replaced) fails here.</summary>
    [Fact]
    public void TheEngineTwinNamesTheSameCallAsTheRulebook()
    {
        var engine = EngineThrow(new[] { "Effects[3]" });
        var gate = Book().Validate(new WriteRequest { RecordType = "Perk", Path = new[] { "Effects[3]" }, Verb = "Set" });

        Assert.NotNull(gate);
        foreach (var fact in new[] { "field_path='Effects'", "key='3'", "SetAtIndex" })
        {
            Assert.Contains(fact, engine);
            Assert.Contains(fact, gate!);
        }
    }

    /// <summary>The engine names the compose slot too, on the lane that actually reaches it: a compose's nested
    /// <c>sets</c> are replayed through <c>ApplyVerb</c> against the freshly-built struct, so a bracketed leaf there
    /// is rooted at the struct and belongs in <c>path=</c>.</summary>
    [Fact]
    public void TheEngineTwinNamesTheComposeSlotOnNestedSets()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WriteEngine.ApplyVerb(
            new Npc(FormKey.Null, SkyrimRelease.SkyrimSE),
            new WriteRequest
            {
                RecordType = "Npc", Path = new[] { "VirtualMachineAdapter", "Scripts" }, Verb = "Add",
                Struct = new StructSpec
                {
                    Type = "ScriptEntry",
                    Sets = new() { new WriteRequest { RecordType = "Npc", Path = new[] { "Properties[0]" }, Verb = "Set" } },
                },
            }));

        Assert.Contains("brackets a collection element at the LEAF", ex.Message);
        Assert.Contains("path='Properties'", ex.Message);
        Assert.DoesNotContain("field_path=", ex.Message);
    }
}

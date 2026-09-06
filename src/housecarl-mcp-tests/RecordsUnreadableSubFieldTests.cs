using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlGenerator;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin whose single weapon carries a SHORT DATA subrecord: the record opens, its BasicStats
/// substruct opens, and reading the last field out of that substruct throws inside Mutagen. Two bytes of the
/// plugin are the whole fixture — the file is written by Mutagen, then DATA's declared length is cut from 10 to
/// 8 (and the enclosing record and group sizes with it), which is what a truncated or badly merged plugin looks
/// like on disk. It sits in a switched-OFF mod folder and is read by name, the same off-order lane a caller uses
/// to look inside a plugin that is not in the order; the clean master it OVERRIDES holds the active order and is
/// the reference pole a delta compares it against.</summary>
public sealed class TruncatedSubFieldWorld : IDisposable
{
    public string Root { get; }
    public LoadOrderService Svc { get; }
    /// <summary>The plugin holding the weapon whose BasicStats.Damage cannot be read. Value and Weight, which sit
    /// before the cut, still read.</summary>
    public string TruncName { get; }
    /// <summary>The master that DEFINES the weapon with a whole DATA — the clean pole a delta compares against.</summary>
    public string CleanName { get; }
    /// <summary>The weapon, addressed as the master defines it — the FormID both poles carry.</summary>
    public string WeaponFid { get; }
    public const string WeaponEditorId = "HcTruncWeap";
    public const int Value = 34;
    public const int Damage = 12;

    static int Find(byte[] b, string sig, int from)
    {
        for (int i = from; i + 4 <= b.Length; i++)
            if (b[i] == sig[0] && b[i + 1] == sig[1] && b[i + 2] == sig[2] && b[i + 3] == sig[3]) return i;
        return -1;
    }

    readonly string _priorCorpusPath;

    public TruncatedSubFieldWorld()
    {
        // CorpusRulebook.CorpusPath is a process-global: capture before repointing, and restore before the
        // directory the new value names is deleted.
        _priorCorpusPath = CorpusRulebook.CorpusPath;

        Root = Path.Combine(Path.GetTempPath(), "hc-truncated-subfield-tests-" + Guid.NewGuid().ToString("N"));
        var instance = Path.Combine(Root, "instance");
        var profiles = Path.Combine(instance, "profiles", "Default");
        var mods = Path.Combine(instance, "mods");
        foreach (var d in new[] { profiles, mods, Path.Combine(Root, "game", "Data") }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(Root, "game").Replace(@"\", @"\\") + ")\r\n");

        string P(string folder, ModKey k)
        {
            var p = Path.Combine(mods, folder, k.FileName.String);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            return p;
        }

        // The master DEFINES the weapon, cleanly; the truncated plugin OVERRIDES it with the same content. The two
        // are byte-identical until the cut below, so a delta between them differs in exactly the one field that
        // cannot be read — and the master doubles as the clean reference pole.
        var cleanKey = new ModKey("HcTruncClean", ModType.Master);
        var clean = new SkyrimMod(cleanKey, SkyrimRelease.SkyrimSE);
        CleanName = cleanKey.FileName.String;
        var cleanWeapon = clean.Weapons.AddNew();
        cleanWeapon.EditorID = WeaponEditorId;
        cleanWeapon.BasicStats = new WeaponBasicStats { Damage = Damage, Value = Value, Weight = 5.5f };
        WeaponFid = $"{cleanWeapon.FormKey.ID:X6}:{cleanWeapon.FormKey.ModKey.FileName}";
        clean.BeginWrite.ToPath(P("CleanMod", cleanKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var truncKey = new ModKey("HcTrunc", ModType.Plugin);
        TruncName = truncKey.FileName.String;
        var m = new SkyrimMod(truncKey, SkyrimRelease.SkyrimSE);
        var weapon = m.Weapons.GetOrAddAsOverride(cleanWeapon);
        weapon.BasicStats = new WeaponBasicStats { Damage = Damage, Value = Value, Weight = 5.5f };
        var path = P("TruncMod", truncKey);
        m.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { clean }).Write();

        // …and now cut two bytes off DATA, so the last field in the struct reads past the end of its own
        // subrecord. The record, the group and the file all stay internally consistent: nothing but that one
        // field is broken.
        var bytes = File.ReadAllBytes(path);
        int grup = Find(bytes, "GRUP", 0);           // the WEAP group header; its own label is "WEAP" at +8,
        int rec = Find(bytes, "WEAP", grup + 24);    // so the record itself is searched for past the header
        int sub = Find(bytes, "DATA", rec + 24);
        // Every offset below is unchecked arithmetic on these three: a miss (-1) or a DATA that is not the 10-byte
        // WEAP one still writes a plugin, and the test then fails somewhere far from the cause. Assert the fixture's
        // assumptions here so a Mutagen layout change fails as itself.
        Assert.True(grup >= 0 && rec >= 0 && sub >= 0, $"WEAP GRUP/record/DATA not found (grup={grup} rec={rec} sub={sub})");
        int len = BitConverter.ToUInt16(bytes, sub + 4);
        Assert.Equal(10, len);
        const int Keep = 8;
        int cut = len - Keep;
        BitConverter.GetBytes((ushort)Keep).CopyTo(bytes, sub + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, rec + 4) - (uint)cut).CopyTo(bytes, rec + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, grup + 4) - (uint)cut).CopyTo(bytes, grup + 4);
        File.WriteAllBytes(path, bytes[..(sub + 6 + Keep)].Concat(bytes[(sub + 6 + len)..]).ToArray());

        var genDir = Path.Combine(Root, "corpus-gen");
        CorpusGenerator.GenerateAll(genDir, Path.Combine(Root, "corpus-ref"));
        CorpusRulebook.CorpusPath = Path.Combine(genDir, "corpus.json");

        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + cleanKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), "*" + cleanKey.FileName + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n-TruncMod\r\n+CleanMod\r\n");

        Svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(Root, "houseCARL.user.json")));
        Svc.Stats();
    }

    public void Dispose()
    {
        CorpusRulebook.CorpusPath = _priorCorpusPath;
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

public sealed class TruncatedSubFieldFixture : IDisposable
{
    public TruncatedSubFieldWorld W { get; } = new();
    public void Dispose() => W.Dispose();
}

/// <summary>A sub-field that cannot be read says so, on the field, wherever fields are rendered. It is never
/// dropped: an omitted line and an absent optional are the same thing to a caller, so a swallowed fault reports
/// "I could not look" as "there is nothing there".</summary>
[Trait("tier", "integration")]
public sealed class RecordsUnreadableSubFieldTests : IClassFixture<TruncatedSubFieldFixture>
{
    readonly TruncatedSubFieldWorld _w;
    public RecordsUnreadableSubFieldTests(TruncatedSubFieldFixture f) => _w = f.W;

    string Read(RecordsTools.RecordsProject project, string? format = null) =>
        RecordsTools.Records(_w.Svc, source: JsonDocument.Parse("\"" + _w.TruncName + "\"").RootElement.Clone(),
                             types: new[] { "WEAP" }, project: project, format: format);

    static RecordsTools.RecordsProject Stats =>
        new() { form = "fields", fields = new[] { "BasicStats" }, depth = 2 };

    /// <summary>The fault is isolated to its own line: the sub-field names itself and its reason, and the siblings
    /// beside it are real reads and are kept.</summary>
    [Fact]
    public void TheFieldsFormNamesTheSubFieldAndTheReasonAndKeepsItsSiblings()
    {
        var r = Read(Stats);
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage = (unreadable: ", r);
        Assert.Contains("BasicStats.Value = " + TruncatedSubFieldWorld.Value, r);
        // The reason is Mutagen's own, not a wrapper's: reflection's "target of an invocation" names nothing.
        Assert.DoesNotContain("target of an invocation", r);
    }

    [Fact]
    public void TheJsonRenderCarriesTheFaultAsThatFieldsOwnNote()
    {
        using var doc = JsonDocument.Parse(Read(Stats, "json"));
        var field = doc.RootElement.GetProperty("records")[0].GetProperty("fields")
                       .EnumerateArray().Single(f => f.GetProperty("path").GetString() == "BasicStats.Damage");
        Assert.StartsWith("(unreadable: ", field.GetProperty("note").GetString());
        Assert.False(field.TryGetProperty("value", out _));
    }

    /// <summary>A whole-record read is still a whole-record read: every other field answers, and the one that
    /// could not be read is named among them.</summary>
    [Fact]
    public void AnEverythingReadKeepsTheRestOfTheRecordAndStillNamesTheFault()
    {
        var r = Read(new RecordsTools.RecordsProject { form = "everything", depth = 2 });
        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains(TruncatedSubFieldWorld.WeaponEditorId, r);
        Assert.Contains("BasicStats.Value = " + TruncatedSubFieldWorld.Value, r);
        Assert.Contains("BasicStats.Damage = (unreadable: ", r);
    }

    /// <summary>The comparison forms carry the fault too: a field that could not be read is a NO-VERDICT, named as
    /// its own line and leaving the comparison incomplete — never asserted as a value that differs from the
    /// reference's, which claims a reading the engine does not have.</summary>
    [Fact]
    public void ADeltaNamesTheUnreadableFieldInsteadOfCallingItAValueDifference()
    {
        var r = RecordsTools.Records(_w.Svc, formids: new[] { _w.WeaponFid },
            source: JsonDocument.Parse("\"" + _w.TruncName + "\"").RootElement.Clone(),
            versus: JsonDocument.Parse("\"" + _w.CleanName + "\"").RootElement.Clone(),
            project: new RecordsTools.RecordsProject { form = "delta" });

        Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
        Assert.Contains("BasicStats.Damage: UNREADABLE here — not compared", r);
        Assert.Contains("the comparison is INCOMPLETE", r);
        // The reading it must NOT give: the reference's 12 asserted as a difference from a value never read.
        Assert.DoesNotContain("BasicStats.Damage=(unreadable", r);
    }

    string Delta(bool countsOnly) => RecordsTools.Records(_w.Svc, formids: new[] { _w.WeaponFid },
        source: JsonDocument.Parse("\"" + _w.TruncName + "\"").RootElement.Clone(),
        versus: JsonDocument.Parse("\"" + _w.CleanName + "\"").RootElement.Clone(),
        project: new RecordsTools.RecordsProject { form = "delta" }, counts_only: countsOnly);

    /// <summary>A no-verdict is a THIRD state, not a difference: this record restates every field the reference
    /// carries and differs in none, so counting the UNREADABLE line as a difference would report a difference
    /// nobody read.</summary>
    [Fact]
    public void ANoVerdictIsCountedApartFromValueDifferences()
    {
        Assert.Contains("differing=0 identical=0 no_verdict=1", Delta(countsOnly: true));

        var r = Delta(countsOnly: false);
        Assert.Contains("1 field that could not be read", r);
        Assert.DoesNotContain("1 difference —", r);
    }
}

/// <summary>The tree form's node line says the comparison was incomplete WHENEVER it was, not only where the
/// node has no deltas — an unreadable leaf always produces a delta line, so gating the note on an empty delta
/// list left the normal shape rendering one UNREADABLE line and nothing about what was skipped beside it.
/// Driven against a hand-built row: the render is the subject, and no fixture makes this node shape.</summary>
[Trait("tier", "unit")]
public sealed class TreeIncompleteNoteTests
{
    static string Render(params string[] deltas)
    {
        var row = new LoadOrderService.TreeRow("000800:A.esm", "Weapon", "HcWeap",
            new[] { "A.esm", "B.esp" }, "A.esm",
            new[] { new LoadOrderService.TreeNodeDelta("A.esm", false, true, Array.Empty<string>(), 0, true, null),
                    new LoadOrderService.TreeNodeDelta("B.esp", true, false, deltas, 0, false, null) },
            null, Array.Empty<ChildDeclarers>());
        return RecordsTools.RenderRecordsTree(new[] { row }, 1, 1, 0, false, "records  form=tree", null,
                                              100_000, null, out _);
    }

    [Fact]
    public void ANodeWithDeltasStillSaysTheComparisonWasIncomplete()
    {
        var r = Render("BasicStats.Damage: UNREADABLE here — not compared (unreadable: out of range)");

        Assert.Contains("BasicStats.Damage: UNREADABLE here", r);
        Assert.Contains("the comparison is INCOMPLETE", r);
    }

    /// <summary>The no-delta node keeps its own note — the only cause left there is the cap.</summary>
    [Fact]
    public void ANodeWithNoDeltasStillNamesTheCap()
    {
        Assert.Contains("TRUNCATED at the cap", Render());
    }
}

/// <summary>A deep read carries the readable bit on a LINK leaf too. A condition target (FormLinkOrIndex) the
/// emit cannot read is a fault, and a fault that renders with Present=false and Readable=true tells every
/// consumer reading the bits that nothing is there.</summary>
[Trait("tier", "unit")]
public sealed class UnreadableLinkLeafReadTests
{
    [Fact]
    public void AnUnreadableConditionTargetIsNotReadAsAbsent()
    {
        var mod = new SkyrimMod(new ModKey("HcFloiRead", ModType.Plugin), SkyrimRelease.SkyrimSE);
        var mgef = mod.MagicEffects.AddNew();
        mgef.EditorID = "HcFloiMgef";
        // Index mode (UseAliases), whose index accessor the emit cannot resolve — the fault EmitFloi reports.
        mgef.Conditions.Add(new ConditionFloat { Data = new GetInFactionConditionData { UseAliases = true } });

        var f = ReadEngine.ReadFields(mgef, new[] { "Conditions" }, depth: 3).Fields
                          .Single(x => x.Path == "Conditions[0].Data.Faction");

        Assert.StartsWith("(floi: ", f.Note);
        Assert.False(f.Readable);
    }
}

/// <summary>The pole-symmetric half of the same fact, driven at <see cref="FieldsDiff"/> directly: when BOTH poles
/// are unreadable at one path there is nothing to compare there, so the record must not be reported identical.
/// Driven here because one truncated plugin cannot be both poles of a lane call — the shape of the failure belongs
/// to the comparison, not to the read.</summary>
[Trait("tier", "unit")]
public sealed class UnreadableLeafComparisonTests
{
    static RecordFields Rec(params FieldValue[] fields) => new("WEAP", "000800:A.esm", "HcWeap", fields);

    static FieldValue Unreadable(string path) =>
        new(path, false, null, "(unreadable: out of range)", Present: false, Readable: false);

    [Fact]
    public void TwoUnreadablePolesAtOnePathAreNotIdentical()
    {
        var d = FieldsDiff.Compare(Rec(new FieldValue("EditorID", true, "HcWeap", null), Unreadable("BasicStats.Damage")),
                                   Rec(new FieldValue("EditorID", true, "HcWeap", null), Unreadable("BasicStats.Damage")));

        Assert.False(d.Complete);
        Assert.Contains(d.Deltas, x => x.StartsWith("BasicStats.Damage: UNREADABLE on both sides — not compared (unreadable: ", StringComparison.Ordinal));
    }

    static FieldValue List(string path, int n) =>
        new(path, false, null, $"[list: {n} item(s)]", Present: true, Count: n);

    /// <summary>What an unreadable leaf costs is its own path, not the record: the rest still compares, so a
    /// differing list beside an unreadable scalar is still reported. Complete stays false, so nothing claims
    /// identity.</summary>
    [Fact]
    public void ADifferingListBesideAnUnreadableLeafIsStillCompared()
    {
        var d = FieldsDiff.Compare(
            Rec(Unreadable("BasicStats.Damage"), List("Keywords", 2),
                new FieldValue("Keywords[0]", true, "000AAA:A.esm", null),
                new FieldValue("Keywords[1]", true, "000BBB:A.esm", null)),
            Rec(new FieldValue("BasicStats.Damage", true, "12", null), List("Keywords", 2),
                new FieldValue("Keywords[0]", true, "000AAA:A.esm", null),
                new FieldValue("Keywords[1]", true, "000CCC:A.esm", null)));

        Assert.False(d.Complete);
        Assert.Contains(d.Deltas, x => x.StartsWith("BasicStats.Damage: UNREADABLE here — not compared (unreadable: ", StringComparison.Ordinal));
        Assert.Contains(d.Deltas, x => x.StartsWith("Keywords: ", StringComparison.Ordinal));
        // …and the winner's readable value at the unreadable path is not reported as one-sided presence.
        Assert.DoesNotContain(d.Deltas, x => x.Contains("BasicStats.Damage", StringComparison.Ordinal)
                                          && !x.Contains("UNREADABLE", StringComparison.Ordinal));
    }

    /// <summary>An unreadable leaf INSIDE a list element takes that list's element comparison down with it — the
    /// missing line changes the element's fingerprint — and nothing else: a second list still compares.</summary>
    [Fact]
    public void AnUnreadableLeafInsideAnElementSuppressesOnlyThatList()
    {
        var d = FieldsDiff.Compare(
            Rec(List("Conditions", 1), new FieldValue("Conditions[0].ComparisonValue", true, "1", null),
                Unreadable("Conditions[0].Data.Reference"),
                List("Keywords", 1), new FieldValue("Keywords[0]", true, "000AAA:A.esm", null)),
            Rec(List("Conditions", 1), new FieldValue("Conditions[0].ComparisonValue", true, "2", null),
                new FieldValue("Conditions[0].Data.Reference", true, "000FFF:A.esm", null),
                List("Keywords", 1), new FieldValue("Keywords[0]", true, "000BBB:A.esm", null)));

        Assert.Contains(d.Deltas, x => x.StartsWith("Keywords: ", StringComparison.Ordinal));
        Assert.DoesNotContain(d.Deltas, x => x.StartsWith("Conditions: ", StringComparison.Ordinal));
    }

    /// <summary>A fault the walk spells some other way — an FLOI whose mode or index it could not read — is a
    /// no-verdict too: what makes a line incomparable is the Readable bit, not the note's prose.</summary>
    [Fact]
    public void AnFloiFaultIsNotComparedEither()
    {
        var floi = new FieldValue("Conditions[0].Data.Reference", false, null,
                                  "(floi: form mode, null or unreadable FormKey on FormLinkOrIndex`1)",
                                  Present: false, Readable: false);
        var d = FieldsDiff.Compare(Rec(new FieldValue("EditorID", true, "HcWeap", null), floi),
                                   Rec(new FieldValue("EditorID", true, "HcWeap", null), floi));

        Assert.False(d.Complete);
        Assert.Contains(d.Deltas, x => x.StartsWith("Conditions[0].Data.Reference: UNREADABLE on both sides — not compared (floi: ", StringComparison.Ordinal));
    }

    /// <summary>A no-such-field is the OTHER Readable=false answer and stays comparable: it says something true
    /// about the record, so two sides that both lack the field still agree on that.</summary>
    [Fact]
    public void ANoSuchFieldStillCompares()
    {
        var missing = new FieldValue("Nope", false, null, "(no field Nope)", Present: false, Readable: false);
        var d = FieldsDiff.Compare(Rec(missing), Rec(missing));

        Assert.True(d.Complete);
        Assert.Empty(d.Deltas);
    }
}

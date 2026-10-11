using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The where= note after a '->' hop whose target lists can be empty (#1160 review): it does not hang on the
/// order of a record's links, and beside a match it does not claim 0 matches. In memory, no load order.</summary>
[Trait("tier", "unit")]
public sealed class WrongPathNoteLinkOrderTests
{
    readonly SkyrimMod _mod = new(new ModKey("LinkOrderWorld", ModType.Master), SkyrimRelease.SkyrimSE);
    readonly Dictionary<FormKey, IMajorRecordGetter> _targets = new();
    readonly FormKey _kw, _withKw, _noKw;

    public WrongPathNoteLinkOrderTests()
    {
        var kw = _mod.Keywords.AddNew(); kw.EditorID = "LoKw"; _kw = kw.FormKey;
        var a = _mod.Armors.AddNew(); a.EditorID = "LoArmorKw";
        a.Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>> { new FormLink<IKeywordGetter>(_kw) };
        var b = _mod.Armors.AddNew(); b.EditorID = "LoArmorBare";   // no keywords: its '[*none]' is decided by an empty list
        _withKw = a.FormKey; _noKw = b.FormKey;
        _targets[_withKw] = a; _targets[_noKw] = b;
    }

    IFormListGetter List(string eid, params FormKey[] items)
    {
        var l = _mod.FormLists.AddNew(); l.EditorID = eid;
        foreach (var i in items) l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(i));
        return l;
    }

    (int Hits, string Note) Run(string clause, params IMajorRecordGetter[] bodies)
    {
        var (set, err) = FieldPredicateSet.Parse(new[] { clause });
        Assert.Null(err);
        set!.BindResolution(_ => null, fk => _targets.GetValueOrDefault(fk));
        int hits = bodies.Count(b => set.Matches(b));
        return (hits, set.AccountingNote() ?? "");
    }

    // A read keyword on one target makes the path real, whichever link comes first.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AReadTargetKeepsTheNoteAwayInEitherLinkOrder(bool keywordedFirst)
    {
        var list = keywordedFirst ? List("LoAB", _withKw, _noKw) : List("LoBA", _noKw, _withKw);
        var inner = List("LoInner"); _targets[inner.FormKey] = inner;
        var toInner = List("LoToInner", inner.FormKey);   // a FormList has no Keywords, so this record is a no-field miss
        var (hits, note) = Run($"Items->Keywords[*none] = {_kw.ID:X6}:{_kw.ModKey.FileName}", list, toInner);
        Assert.Equal(1, hits);   // the bare armor has no keyword, so the list matches
        Assert.DoesNotContain("NOT A FIELD", note);
        Assert.DoesNotContain("yielded no readable value", note);
    }

    // '[*none]' matches the empty list, so the wrong hop has a match and the note must not say 0.
    [Fact]
    public void AWrongHopBesideAMatchDoesNotClaimZeroMatches()
    {
        var (hits, note) = Run("Items[*none]->type = Armor", List("LoEmpty"), List("LoFull", _withKw));
        Assert.Equal(1, hits);
        Assert.Contains("NOT A FIELD", note);
        Assert.Contains("the 1 scanned record(s) that reached it (another 1 had an empty list", note);
        Assert.Contains("The match count on that basis is NOT a confirmed answer", note);
        Assert.DoesNotContain("0 matches on that basis", note);
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The collect pass gathers every '->' target the scan judges, so each target group is read once.</summary>
[Trait("tier", "integration")]
public sealed class DoorCollectTests : IClassFixture<DoorCollectWorld>
{
    readonly DoorCollectWorld _c;
    public DoorCollectTests(DoorCollectWorld c) => _c = c;

    Dictionary<(string, string), int> Reads(string type, string where, string expect) =>
        GroupReadDelta.Of(_c.Svc, () => RecordsTools.Records(_c.Svc, types: new[] { type }, where: new[] { where }), "scan: 1 match", expect);

    /// <summary>A fold over a list of structs follows the link inside each element: Top's Factions, which wins every faction, is read once.</summary>
    [Fact]
    public void AFieldUnderAFoldReadsTheTargetGroupOnce()
    {
        string B = _c.BaseName, T = _c.TopName;
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "Npcs")] = 2, [(T, "Npcs")] = 2,
            [(T, "Factions")] = 1,
        }, Reads("Npc", "Factions[*any].Faction->editorid = HcColFaction3", "HcColMember"));
    }

    /// <summary>A link with no fold is collected too: Top's NPCs, the templates, are read once more, not once per record.</summary>
    [Fact]
    public void ALinkWithNoFoldReadsTheTargetGroupOnce()
    {
        string B = _c.BaseName, T = _c.TopName;
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [(B, "Npcs")] = 2, [(T, "Npcs")] = 3,
        }, Reads("Npc", "Template->editorid = HcColTemplate2", "HcColTemplated2"));
    }

    /// <summary>A list whose last element cannot be read is named unscannable, as the scan names it; the other list matches.</summary>
    [Fact]
    public void AnUnreadableElementIsAnUnscannableRowNotAnError()
    {
        var r = RecordsTools.Records(_c.Svc, types: new[] { "FormList" }, where: new[] { "Items[*any]->editorid = HcColArmor1" });
        Assert.True(r.Contains("scan: 1 match") && r.Contains("HcColGoodList")
                    && r.Contains("1 record instance(s) could not be scanned"), r);
    }
}

/// <summary>Base defines factions, NPCs, armors and a FLST; Top re-states the factions and templates; Cut adds a FLST whose last LNAM is cut.</summary>
public sealed class DoorCollectWorld : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-door-collect-");
    public LoadOrderService Svc { get; }
    public string BaseName { get; }
    public string TopName { get; }

    public DoorCollectWorld()
    {
        ModKey baseKey = new("HcColBase", ModType.Master), topKey = new("HcColTop", ModType.Plugin), cutKey = new("HcColCut", ModType.Plugin);
        BaseName = baseKey.FileName.String; TopName = topKey.FileName.String;

        var b = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        var factions = Enumerable.Range(1, 3).Select(i => { var f = b.Factions.AddNew(); f.EditorID = "HcColFaction" + i; return f; }).ToList();
        foreach (var (name, held) in new[] { ("HcColMember", factions), ("HcColOther", factions.Take(1).ToList()) })
        {
            var n = b.Npcs.AddNew(); n.EditorID = name;
            foreach (var f in held) n.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(f.FormKey), Rank = 0 });
        }
        var templates = Enumerable.Range(1, 2).Select(i => { var n = b.Npcs.AddNew(); n.EditorID = "HcColTemplate" + i; return n; }).ToList();
        for (int i = 0; i < 2; i++)
        {
            var n = b.Npcs.AddNew(); n.EditorID = "HcColTemplated" + (i + 1);
            n.Template = new FormLinkNullable<INpcSpawnGetter>(templates[i].FormKey);
        }
        var armors = Enumerable.Range(1, 2).Select(i => { var a = b.Armors.AddNew(); a.EditorID = "HcColArmor" + i; return a; }).ToList();
        var good = b.FormLists.AddNew(); good.EditorID = "HcColGoodList";
        good.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(armors[0].FormKey));
        b.BeginWrite.ToPath(_mo2.InMod("ColBase", baseKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        var top = new SkyrimMod(topKey, SkyrimRelease.SkyrimSE);
        foreach (var f in factions) top.Factions.Add(new Faction(f.FormKey, SkyrimRelease.SkyrimSE) { EditorID = f.EditorID });
        foreach (var n in templates) top.Npcs.Add(new Npc(n.FormKey, SkyrimRelease.SkyrimSE) { EditorID = n.EditorID });
        top.BeginWrite.ToPath(_mo2.InMod("ColTop", topKey)).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();

        var cut = new SkyrimMod(cutKey, SkyrimRelease.SkyrimSE);
        var bad = cut.FormLists.AddNew(); bad.EditorID = "HcColCutList";
        foreach (var a in armors) bad.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(a.FormKey));
        var path = _mo2.InMod("ColCut", cutKey);
        cut.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { b }).Write();
        CutLastLnam(path);

        var names = new[] { BaseName, TopName, cutKey.FileName.String };
        _mo2.Profile(string.Join("\r\n", names) + "\r\n", string.Join("", names.Select(n => "*" + n + "\r\n")),
                     "+ColCut\r\n+ColTop\r\n+ColBase\r\n");
        Svc = _mo2.Open();
    }

    /// <summary>Cut the FLST's last LNAM to 2 bytes, keeping the record and group sizes consistent.</summary>
    static void CutLastLnam(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int Find(string sig, int from)
        {
            for (int i = from; i + 4 <= bytes.Length; i++)
                if (bytes[i] == sig[0] && bytes[i + 1] == sig[1] && bytes[i + 2] == sig[2] && bytes[i + 3] == sig[3]) return i;
            return -1;
        }
        int grup = Find("GRUP", 0), rec = Find("FLST", grup + 24), sub = -1;
        for (int at = Find("LNAM", rec + 24); at >= 0; at = Find("LNAM", at + 4)) sub = at;
        Assert.True(grup >= 0 && rec >= 0 && sub >= 0, $"FLST GRUP/record/LNAM not found (grup={grup} rec={rec} sub={sub})");
        Assert.Equal(4, BitConverter.ToUInt16(bytes, sub + 4));
        const int Keep = 2, Cut = 2;
        BitConverter.GetBytes((ushort)Keep).CopyTo(bytes, sub + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, rec + 4) - Cut).CopyTo(bytes, rec + 4);
        BitConverter.GetBytes(BitConverter.ToUInt32(bytes, grup + 4) - Cut).CopyTo(bytes, grup + 4);
        File.WriteAllBytes(path, bytes[..(sub + 6 + Keep)].Concat(bytes[(sub + 6 + Keep + Cut)..]).ToArray());
    }

    public void Dispose() { Svc.Dispose(); _mo2.Delete(); }
}

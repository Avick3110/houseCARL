using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin carrying one FormID in two groups: the reverse index merges both copies' links, while a body read
/// returns the first copy. The reverse walk judges the copy a body read returns, as references= does.</summary>
[Trait("tier", "integration")]
public sealed class ReverseWalkDupeFormIdTests
{
    /// <summary>One FormID as a FormList linking Other and, later, as an Outfit linking Seed; the Outfit's header is
    /// patched onto the FormList's FormID after Mutagen writes it, since Mutagen will not write a duplicate.</summary>
    [Fact]
    public void ARecordWhoseFirstCopyDoesNotLinkTheSeedIsNotReached()
    {
        var mo2 = new ScratchMo2("hc-dupe-");
        try
        {
            var m = new SkyrimMod(new ModKey("HcDupeMaster", ModType.Master), SkyrimRelease.SkyrimSE);
            var seed = m.Keywords.AddNew();
            seed.EditorID = "HcDupSeed";
            var other = m.Keywords.AddNew();
            other.EditorID = "HcDupOther";
            var p = new SkyrimMod(new ModKey("HcDupe", ModType.Plugin), SkyrimRelease.SkyrimSE);
            var list = p.FormLists.AddNew();
            list.EditorID = "HcDupList";
            list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(other.FormKey));
            var outfit = p.Outfits.AddNew();
            outfit.EditorID = "HcDupOutfit";
            outfit.Items = new Noggog.ExtendedList<IFormLinkGetter<IOutfitTargetGetter>> { new FormLink<IOutfitTargetGetter>(seed.FormKey) };
            var control = p.FormLists.AddNew();
            control.EditorID = "HcDupControl";
            control.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(seed.FormKey));

            m.BeginWrite.ToPath(mo2.InMod("DupeMaster", m.ModKey)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            var path = mo2.InMod("Dupe", p.ModKey);
            p.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { m }).Write();
            Renumber(path, "OTFT", outfit.FormKey.ID, list.FormKey.ID);
            mo2.Profile("HcDupeMaster.esm\r\nHcDupe.esp\r\n", "*HcDupeMaster.esm\r\n*HcDupe.esp\r\n", "+Dupe\r\n+DupeMaster\r\n");

            using var svc = mo2.Open();
            var walk = RecordsTools.Records(svc, formids: new[] { ScratchMo2.Fid(seed.FormKey) },
                                            walk: new RecordsTools.RecordsWalk { direction = "reverse", depth = 1 });

            Assert.Contains("HcDupControl", walk);
            Assert.DoesNotContain(ScratchMo2.Fid(list.FormKey), walk);
        }
        finally { mo2.Delete(); }
    }

    /// <summary>Rewrite the one record header of this signature carrying <paramref name="from"/> to carry <paramref name="to"/>.</summary>
    static void Renumber(string path, string sig, uint from, uint to)
    {
        var b = File.ReadAllBytes(path);
        var s = System.Text.Encoding.ASCII.GetBytes(sig);
        int hits = 0;
        for (int i = 0; i + 24 <= b.Length; i++)
        {
            if (!b.AsSpan(i, 4).SequenceEqual(s) || BitConverter.ToUInt32(b, i + 12) != (0x01000000u | from)) continue;
            BitConverter.GetBytes(0x01000000u | to).CopyTo(b, i + 12);
            hits++;
        }
        Assert.Equal(1, hits);
        File.WriteAllBytes(path, b);
    }
}

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>The walk shapes copy's caps and order are pinned on, in its own temp folder. Active: HcBase.esm. On disk but
/// switched off: Walk.esp, the donor, holding one FormList per shape. Built fresh per test, because every copy writes
/// a patch folder under mods.</summary>
public sealed class CopyWalkWorld : IDisposable
{
    public string Root { get; }
    public string ModsDir { get; }
    public LoadOrderService Svc { get; }

    /// <summary>A FormList of 128 keywords: the node cap reached exactly.</summary>
    public FormKey List128 { get; }
    /// <summary>A FormList of 129 keywords: one past the node cap.</summary>
    public FormKey List129 { get; }
    /// <summary>The 129th keyword, the pull the node cap refuses.</summary>
    public FormKey Keyword129 { get; }
    /// <summary>A FormList seeding a chain of 33 head parts, each the next one's ExtraParts parent: depth 32 reached exactly.</summary>
    public FormKey Chain33 { get; }
    /// <summary>A FormList seeding a chain of 34 head parts: depth 33, one past the depth cap.</summary>
    public FormKey Chain34 { get; }
    /// <summary>The 34th head part, the pull the depth cap refuses.</summary>
    public FormKey Chain34Last { get; }
    /// <summary>A FormList whose one item is a Walk.esp FormID no record carries.</summary>
    public FormKey DanglingList { get; }
    /// <summary>The FormID <see cref="DanglingList"/> names.</summary>
    public FormKey Dangling { get; }
    /// <summary>A FormList seeding a small tree whose breadth-first order differs from its depth-first one.</summary>
    public FormKey OrderList { get; }
    /// <summary>A FormList whose one head part links back to the list: a walk that cycles to the from record.</summary>
    public FormKey CycleList { get; }

    public CopyWalkWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "hc-copy-walk-" + Guid.NewGuid().ToString("N"));
        var inst = Path.Combine(Root, "inst");
        ModsDir = Path.Combine(inst, "mods");
        var prof = Path.Combine(inst, "profiles", "Default");
        foreach (var d in new[] { ModsDir, Path.Combine(inst, "game", "Data"), prof }) Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(inst, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(inst, "game").Replace(@"\", @"\\") + ")\r\n");

        var baseMod = new SkyrimMod(new ModKey("HcBase", ModType.Master), SkyrimRelease.SkyrimSE);
        baseMod.Keywords.Add(new Keyword(new FormKey(baseMod.ModKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcKeyword" });
        Write(baseMod, "BaseMod");

        var walkKey = new ModKey("Walk", ModType.Plugin);
        var mod = new SkyrimMod(walkKey, SkyrimRelease.SkyrimSE);
        uint next = 0x800;
        FormKey NextKey() => new(walkKey, next++);

        FormKey Keywords(int n, string name)
        {
            var list = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = name };
            for (int i = 1; i <= n; i++)
            {
                var kw = new Keyword(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = $"{name}Kw{i}" };
                mod.Keywords.Add(kw);
                list.Items.Add(kw.FormKey);
            }
            mod.FormLists.Add(list);
            return list.FormKey;
        }
        List128 = Keywords(128, "Nodes128");
        List129 = Keywords(129, "Nodes129");
        Keyword129 = new FormKey(walkKey, next - 1);

        HeadPart Part(string eid)
        {
            var hp = new HeadPart(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = eid };
            mod.HeadParts.Add(hp);
            return hp;
        }
        FormKey Chain(int n, string name)
        {
            var list = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = name };
            HeadPart? prev = null;
            for (int i = 1; i <= n; i++)
            {
                var hp = Part($"{name}Hp{i}");
                if (prev is null) list.Items.Add(hp.FormKey);
                else prev.ExtraParts.Add(hp.FormKey);
                prev = hp;
            }
            mod.FormLists.Add(list);
            return list.FormKey;
        }
        Chain33 = Chain(33, "Depth32");
        Chain34 = Chain(34, "Depth33");
        Chain34Last = new FormKey(walkKey, next - 1);

        Dangling = new FormKey(walkKey, 0xFFF);
        var dangling = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "DanglingList" };
        dangling.Items.Add(Dangling);
        mod.FormLists.Add(dangling);
        DanglingList = dangling.FormKey;

        // Items [A, B]; A -> [A1, A2]; B -> [A2, B1]; A1 -> texture set. Breadth-first: A B A1 A2 B1 Tex.
        var a = Part("OrdA"); var b = Part("OrdB"); var a1 = Part("OrdA1"); var a2 = Part("OrdA2"); var b1 = Part("OrdB1");
        var tex = new TextureSet(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "OrdTex" };
        mod.TextureSets.Add(tex);
        a.ExtraParts.Add(a1.FormKey); a.ExtraParts.Add(a2.FormKey);
        b.ExtraParts.Add(a2.FormKey); b.ExtraParts.Add(b1.FormKey);
        a1.TextureSet.SetTo(tex.FormKey);
        var order = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "OrderList" };
        order.Items.Add(a.FormKey); order.Items.Add(b.FormKey);
        mod.FormLists.Add(order);
        OrderList = order.FormKey;

        // Items [CycPart]; CycPart's ValidRaces is the list itself, so the walk reaches the from record.
        var cycPart = Part("CycPart");
        var cycle = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "CycleList" };
        cycle.Items.Add(cycPart.FormKey);
        cycPart.ValidRaces.SetTo(cycle.FormKey);
        mod.FormLists.Add(cycle);
        CycleList = cycle.FormKey;
        Write(mod, "WalkMod");

        File.WriteAllText(Path.Combine(prof, "modlist.txt"), "+BaseMod\r\n-WalkMod\r\n");
        File.WriteAllText(Path.Combine(prof, "loadorder.txt"), "HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "plugins.txt"), "*HcBase.esm\r\n");
        File.WriteAllText(Path.Combine(prof, "Skyrim.ini"), "[General]\r\n");

        Svc = LoadOrderService.WithInstance(inst, 0, new UserConfigStore(Path.Combine(Root, "user.json")));
    }

    /// <summary>Clones <paramref name="from"/> out of Walk.esp through the tool, seeded by the FormList's Items.</summary>
    public string Clone(FormKey from, string stem)
        => CopyTools.Copy(Svc, FormIdToken.Of(from), new[] { "Walk.esp" }, new[] { "Items" }, null, null, stem, stem);

    /// <summary>Opens the patch a copy wrote as <c>&lt;stem&gt;.esp</c>, wherever under mods it landed, and reads it.</summary>
    public T Patch<T>(string stem, Func<ISkyrimModGetter, T> read) => CopyServiceWorld.ReadBack(
        Directory.EnumerateFiles(ModsDir, stem + ".esp", SearchOption.AllDirectories).Single(), read);

    void Write(SkyrimMod mod, string folder)
    {
        var dir = Path.Combine(ModsDir, folder);
        Directory.CreateDirectory(dir);
        mod.BeginWrite.ToPath(Path.Combine(dir, mod.ModKey.FileName)).WithNoLoadOrder().Write();
    }

    public void Dispose()
    {
        Svc.Dispose();
        try { Directory.Delete(Root, true); } catch { /* temp cleanup best-effort */ }
    }
}

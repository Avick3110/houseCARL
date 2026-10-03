using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>walk.inherit: the NPC template categories a walk resolves as the game does, and how one NPC's links
/// split under them — masked by a set flag, or carried across its Template link.</summary>
public sealed class NpcInherit
{
    /// <summary>The NPC_ fields each template category takes from the template, from the Template Data Flags list on
    /// UESP's Skyrim Mod File Format NPC_ page; a category missing here refuses by name.</summary>
    static readonly (NpcConfiguration.TemplateFlag Flag, string[] Fields)[] Masks =
    {
        (NpcConfiguration.TemplateFlag.Inventory, new[] { "Items", "DefaultOutfit", "SleepingOutfit" }),
        (NpcConfiguration.TemplateFlag.Stats, new[] { "Class" }),
        (NpcConfiguration.TemplateFlag.Factions, new[] { "Factions", "CrimeFaction" }),
        (NpcConfiguration.TemplateFlag.SpellList, new[] { "ActorEffect", "Perks" }),
        (NpcConfiguration.TemplateFlag.AIData, new[] { "CombatStyle", "GiftFilter" }),
        (NpcConfiguration.TemplateFlag.AIPackages, new[] { "Packages" }),
        (NpcConfiguration.TemplateFlag.Script, new[] { "VirtualMachineAdapter" }),
        (NpcConfiguration.TemplateFlag.DefPackList, new[] { "DefaultPackageList", "SpectatorOverridePackageList",
            "ObserveDeadBodyOverridePackageList", "GuardWarnOverridePackageList", "CombatOverridePackageList" }),
        (NpcConfiguration.TemplateFlag.AttackData, new[] { "AttackRace", "Attacks" }),
        (NpcConfiguration.TemplateFlag.Keywords, new[] { "Keywords" }),
    };

    /// <summary>The supported category names, in the table's order, for the refusal sentence.</summary>
    static string Supported => string.Join(", ", Masks.Select(m => m.Flag.ToString()));

    readonly (NpcConfiguration.TemplateFlag Flag, string Name, PropertyInfo[] Props)[] _cats;

    NpcInherit((NpcConfiguration.TemplateFlag, string, PropertyInfo[])[] cats) => _cats = cats;

    /// <summary>The named categories, as the response spells them.</summary>
    public string Label => string.Join(", ", _cats.Select(c => c.Name));

    /// <summary>The categories these names pick; an unknown or unmapped name is a one-sentence refusal.</summary>
    public static NpcInherit? Parse(IReadOnlyList<string> names, out string? refusal)
    {
        refusal = null;
        if (names.Count == 0)
        {
            refusal = $"walk.inherit is empty — name the NPC template categories to resolve (one of: {Supported}), or omit it to follow raw links.";
            return null;
        }
        var cats = new List<(NpcConfiguration.TemplateFlag, string, PropertyInfo[])>();
        foreach (var raw in names)
        {
            var name = raw?.Trim() ?? "";
            if (!Enum.TryParse<NpcConfiguration.TemplateFlag>(name, ignoreCase: true, out var flag)
                || !Enum.IsDefined(flag) || int.TryParse(name, out _))
            {
                refusal = $"walk.inherit '{raw}' is not an NPC template category — use one of: {Supported}.";
                return null;
            }
            var row = Array.FindIndex(Masks, m => m.Flag == flag);
            if (row < 0)
            {
                refusal = $"walk.inherit '{flag}' is a template category the walk has no field map for — the supported ones are: {Supported}.";
                return null;
            }
            if (cats.Any(c => c.Item1 == flag)) continue;
            cats.Add((flag, flag.ToString(), Masks[row].Fields.Select(Property).ToArray()));
        }
        return new NpcInherit(cats.ToArray());
    }

    /// <summary>A field of the NPC getter by name, searched across the interfaces it inherits.</summary>
    static PropertyInfo Property(string name)
        => new[] { typeof(INpcGetter) }.Concat(typeof(INpcGetter).GetInterfaces())
               .Select(t => t.GetProperty(name)).FirstOrDefault(p => p is not null)
           ?? throw new InvalidOperationException($"INpcGetter has no field '{name}'.");

    /// <summary>One NPC's links under the named categories: the keys the walk does not follow, which of them a set
    /// flag masks (per category), and the template the walk crosses (null when no named flag is set).</summary>
    public sealed record Split(IReadOnlySet<FormKey> Removed, IReadOnlyDictionary<string, HashSet<FormKey>> MaskedBy,
                               FormKey? Crossed);

    static readonly Split Nothing = new(new HashSet<FormKey>(), new Dictionary<string, HashSet<FormKey>>(), null);

    /// <summary>Split this NPC's links; a key stays followed while any occurrence of it sits outside the masked fields.</summary>
    public Split Of(INpcGetter npc)
    {
        var flags = npc.Configuration.TemplateFlags;
        var template = npc.Template is { IsNull: false } t ? t.FormKey : (FormKey?)null;
        bool crosses = _cats.Any(c => flags.HasFlag(c.Flag));
        Dictionary<FormKey, int>? hidden = null;
        Dictionary<string, HashSet<FormKey>>? maskedBy = null;
        foreach (var c in _cats)
        {
            if (!flags.HasFlag(c.Flag)) continue;
            var inCat = new Dictionary<FormKey, int>();
            foreach (var p in c.Props) Count(p.GetValue(npc), inCat);
            if (inCat.Count == 0) continue;
            hidden ??= new();
            foreach (var (k, n) in inCat) hidden[k] = hidden.GetValueOrDefault(k) + n;
            (maskedBy ??= new())[c.Name] = new HashSet<FormKey>(inCat.Keys);
        }
        // With no named flag set, the template carries none of the named data, so its link is not crossed.
        if (!crosses && template is { } tk) { hidden ??= new(); hidden[tk] = hidden.GetValueOrDefault(tk) + 1; }
        if (hidden is null) return crosses ? Nothing with { Crossed = template } : Nothing;

        var all = new Dictionary<FormKey, int>();
        foreach (var l in ((IFormLinkContainerGetter)npc).EnumerateFormLinks())
            if (!l.FormKey.IsNull) all[l.FormKey] = all.GetValueOrDefault(l.FormKey) + 1;
        var removed = new HashSet<FormKey>();
        foreach (var (k, n) in hidden)
            if (all.GetValueOrDefault(k) <= n) removed.Add(k);
        var masked = new Dictionary<string, HashSet<FormKey>>();
        if (maskedBy is not null)
            foreach (var (cat, keys) in maskedBy)
            {
                keys.IntersectWith(removed);
                if (keys.Count > 0) masked[cat] = keys;
            }
        return new Split(removed, masked, crosses ? template : null);
    }

    /// <summary>Every non-null link in one field's value, counted per key.</summary>
    static void Count(object? v, Dictionary<FormKey, int> into)
    {
        switch (v)
        {
            case null: return;
            case IFormLinkGetter l:
                if (!l.IsNull) into[l.FormKey] = into.GetValueOrDefault(l.FormKey) + 1;
                return;
            case IFormLinkContainerGetter c:
                foreach (var x in c.EnumerateFormLinks())
                    if (!x.FormKey.IsNull) into[x.FormKey] = into.GetValueOrDefault(x.FormKey) + 1;
                return;
            case IEnumerable e when v is not string:
                foreach (var x in e) Count(x, into);
                return;
        }
    }

    /// <summary>A leveled NPC list's entries — the records a template through it resolves to.</summary>
    public static HashSet<FormKey> Entries(ILeveledNpcGetter list)
    {
        var set = new HashSet<FormKey>();
        foreach (var e in list.Entries ?? (IReadOnlyList<ILeveledNpcEntryGetter>)Array.Empty<ILeveledNpcEntryGetter>())
            if (e.Data?.Reference is { IsNull: false } r) set.Add(r.FormKey);
        return set;
    }
}

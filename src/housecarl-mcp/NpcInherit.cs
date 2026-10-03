using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcp;

/// <summary>walk.inherit: the one NPC template category a reverse walk resolves as the game does, and how one NPC's
/// links split under it — masked by a set flag, or carried across its Template link.</summary>
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

    readonly NpcConfiguration.TemplateFlag _flag;
    readonly PropertyInfo[] _props;

    NpcInherit(NpcConfiguration.TemplateFlag flag, PropertyInfo[] props) { _flag = flag; _props = props; }

    /// <summary>The named category, as the response spells it.</summary>
    public string Name => _flag.ToString();

    /// <summary>The one category these names pick; none, more than one, or an unknown or unmapped name is a
    /// one-sentence refusal.</summary>
    public static NpcInherit? Parse(IReadOnlyList<string> names, out string? refusal)
    {
        refusal = null;
        if (names.Count == 0)
        {
            refusal = $"walk.inherit is empty — name the NPC template category to resolve (one of: {Supported}), or omit it to follow raw links.";
            return null;
        }
        if (names.Count > 1)
        {
            refusal = "walk.inherit names more than one category, and a call follows one category's inheritance — run one call per category.";
            return null;
        }
        var raw = names[0];
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
        return new NpcInherit(flag, Masks[row].Fields.Select(Property).ToArray());
    }

    /// <summary>A field of the NPC getter by name, searched across the interfaces it inherits.</summary>
    static PropertyInfo Property(string name)
        => new[] { typeof(INpcGetter) }.Concat(typeof(INpcGetter).GetInterfaces())
               .Select(t => t.GetProperty(name)).FirstOrDefault(p => p is not null)
           ?? throw new InvalidOperationException($"INpcGetter has no field '{name}'.");

    /// <summary>One NPC's links under the category: the keys its own fields for it hold (empty when the flag is set),
    /// the keys the walk does not follow, those of them the set flag masked, and the template the walk crosses (null
    /// when the flag is clear).</summary>
    public sealed record Split(IReadOnlySet<FormKey> Own, IReadOnlySet<FormKey> Removed, IReadOnlySet<FormKey> Masked,
                               FormKey? Crossed);

    /// <summary>Split this NPC's links; a key stays followed while any occurrence of it sits outside the masked fields.</summary>
    public Split Of(INpcGetter npc)
    {
        bool set = npc.Configuration.TemplateFlags.HasFlag(_flag);
        var template = npc.Template is { IsNull: false } t ? t.FormKey : (FormKey?)null;
        var inCat = new Dictionary<FormKey, int>();
        foreach (var p in _props) Count(p.GetValue(npc), inCat);
        var hidden = set ? new Dictionary<FormKey, int>(inCat) : new Dictionary<FormKey, int>();
        // With the flag clear, the template carries none of the category, so its link is not crossed.
        if (!set && template is { } tk) hidden[tk] = hidden.GetValueOrDefault(tk) + 1;
        var removed = new HashSet<FormKey>();
        if (hidden.Count > 0)
        {
            var all = new Dictionary<FormKey, int>();
            foreach (var l in ((IFormLinkContainerGetter)npc).EnumerateFormLinks())
                if (!l.FormKey.IsNull) all[l.FormKey] = all.GetValueOrDefault(l.FormKey) + 1;
            foreach (var (k, n) in hidden)
                if (all.GetValueOrDefault(k) <= n) removed.Add(k);
        }
        var masked = set ? new HashSet<FormKey>(inCat.Keys.Where(removed.Contains)) : new HashSet<FormKey>();
        return new Split(set ? new HashSet<FormKey>() : new HashSet<FormKey>(inCat.Keys), removed, masked, set ? template : null);
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

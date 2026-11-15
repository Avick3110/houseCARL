using System.Text.Json;

namespace HousecarlCore;

/// <summary>BOS's 'lines' block of the runtime-format source: the section kinds, the field shapes, the delimiters and the
/// order the server reads a <c>_SWAP.ini</c> line by. A value the code has no rule for fails the load, naming the field.</summary>
public static partial class RuntimeFormats
{
    /// <summary>BOS's row of the embedded source, with its lines block (memoized).</summary>
    public static DistributorFormat Bos => Distributor("bos") ?? throw new InvalidOperationException("runtime-format source: no bos row.");

    /// <summary>BOS's lines block; null when its row has none.</summary>
    static BosGrammar? BosLinesBlock(JsonElement row, string ctx)
    {
        if (!row.TryGetProperty("lines", out var l)) return null;
        ctx += ".lines";
        Known(l, "section", ctx, "every");
        Known(l, "keys", ctx, "onceIgnoreCase");
        Known(l, "conflict", ctx, "lastWins");
        // The reader takes the fields at these places; another order is a rule the code does not have.
        var swap = Strs(l, "swapFields", ctx);
        if (!swap.SequenceEqual(new[] { "base", "swap", "properties", "chance" })) throw Unknown(ctx, "swapFields", string.Join(",", swap));
        var prop = Strs(l, "propertyFields", ctx);
        if (!prop.SequenceEqual(new[] { "base", "properties", "chance" })) throw Unknown(ctx, "propertyFields", string.Join(",", prop));
        var order = Strs(l, "swapOrder", ctx).Select(k => k switch
        {
            "references" => BosKind.References,
            "conditionalForms" => BosKind.ConditionalForms,
            "forms" => BosKind.Forms,
            var v => throw Unknown(ctx, "swapOrder", v),
        }).ToList();
        if (order.Distinct().Count() != 3) throw Unknown(ctx, "swapOrder", string.Join(",", order));
        return new BosGrammar(
            Strs(l, "reads", ctx),
            Strs(l, "commentLeads", ctx).Select(c => Single(c, ctx + ".commentLeads")).ToArray(),
            Single(Str(l, "assign", ctx), ctx + ".assign"), Single(Str(l, "fields", ctx), ctx + ".fields"),
            Single(Str(l, "item", ctx), ctx + ".item"), Single(Str(l, "not", ctx), ctx + ".not"),
            Single(Str(l, "conditional", ctx), ctx + ".conditional"),
            Str(l, "unset", ctx), Str(l, "formsSection", ctx), Strs(l, "propertySections", ctx),
            Strs(l, "propertyNames", ctx), order);
    }
}

/// <summary>Which of BOS's five maps a section's lines go to.</summary>
public enum BosKind { References, Forms, Properties, ConditionalForms, ConditionalProperties }

/// <summary>BOS's line grammar, as its row states it.</summary>
public sealed record BosGrammar(
    IReadOnlyList<string> Reads,
    IReadOnlyList<char> CommentLeads,
    char Assign, char Fields, char Item, char Not, char Conditional,
    string Unset,
    string FormsSection,
    IReadOnlyList<string> PropertySections,
    IReadOnlyList<string> PropertyNames,
    IReadOnlyList<BosKind> SwapOrder)
{
    /// <summary>The map a section's lines go to, by the section's name as SimpleIni keeps it (its first spelling), compared exactly as BOS compares it.</summary>
    public BosKind KindOf(string section)
    {
        if (section.Contains(Conditional))
            return section.Split(Conditional)[0] == FormsSection ? BosKind.ConditionalForms : BosKind.ConditionalProperties;
        if (PropertySections.Contains(section)) return BosKind.Properties;
        return section == FormsSection ? BosKind.Forms : BosKind.References;
    }

    /// <summary>Whether a kind's lines swap a base (base|swap|properties|chance), not set properties alone (base|properties|chance).</summary>
    public static bool Swaps(BosKind k) => k is BosKind.References or BosKind.Forms or BosKind.ConditionalForms;

    /// <summary>Whether a properties field is an entry: BOS's <c>is_valid_entry</c>, not empty and not the unset word in any case.</summary>
    public bool Set(string field) => field.Length > 0 && !field.Equals(Unset, StringComparison.OrdinalIgnoreCase);

    /// <summary>A kind as the answer names it.</summary>
    public static string Name(BosKind k) => k switch
    {
        BosKind.References => "reference swaps",
        BosKind.Forms => "form swaps",
        BosKind.Properties => "property overrides",
        BosKind.ConditionalForms => "conditional form swaps",
        _ => "conditional property overrides",
    };
}

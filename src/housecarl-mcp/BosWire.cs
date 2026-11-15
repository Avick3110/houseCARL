using System.Text;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>BOS's own words in its part of the layer answer, and its last-wins order; <see cref="DistributorWire"/> renders the rest.</summary>
static class BosWire
{
    public static readonly DistributorWords Words = new(
        "BOS", "bos", f => f.BosLines!.Reads,
        skipped => skipped + " more line(s) BOS reads no entry from as SimpleIni and BOS split the file (a repeat of an earlier line of its section, no key, no '|', or a header with no ']')",
        "not read as a BOS entry",
        "; BOS names no plugin as a term.",
        "", "", ReadHoldsParseNotes: false, OrderLine);

    static string OrderLine(DistributorLayerData s)
        => "order: files by name in byte order, each file's sections in first appearance (a header repeated in any case joins the first) and each section's lines in order; " +
           "per reference BOS tries " + string.Join(", then ", s.Format.BosLines!.SwapOrder.Select(Map)) +
           ", each from its last line back, and the first whose chance passes and whose swap finds an object wins; property overrides are tried the same way after the winning swap's own. " +
           "Conditions and chance are not evaluated here.\n";

    static string Map(BosKind k) => k switch
    {
        BosKind.References => "the reference's own swaps (not for a reference made at runtime)",
        BosKind.ConditionalForms => "its base's conditional form swaps (condition sets from the last 'file|conditions' in byte order back)",
        _ => "its base's form swaps",
    };

    /// <summary>A kept line's rows: its place among its base's lines of its map, for each base it is filed under.</summary>
    internal static string PlaceRow(IReadOnlyList<BosLines.Place> places)
    {
        var sb = new StringBuilder();
        foreach (var p in places)
        {
            sb.Append("          last wins: line ").Append(p.Index).Append(" of ").Append(p.Total).Append(" for base ").Append(p.Base)
              .Append(" among its ").Append(BosGrammar.Name(p.Kind));
            if (p.ConditionId is { } set) sb.Append(" under '").Append(set).Append("' (set ").Append(p.SetIndex).Append(" of ").Append(p.Sets).Append(" for the base)");
            sb.Append(", tried from the last back\n");
        }
        return sb.ToString();
    }
}

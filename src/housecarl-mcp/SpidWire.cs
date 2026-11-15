using HousecarlCore;

namespace HousecarlMcp;

/// <summary>SPID's own words in its part of the layer answer, and its first-wins order; <see cref="DistributorWire"/> renders the rest.</summary>
static class SpidWire
{
    public static readonly DistributorWords Words = new(
        "SPID", "spid", f => f.Lines!.Reads, skipped => skipped + " line(s) SPID skips",
        "SPID skips this line",
        ", and a plugin through a term naming it; a string filter names a form only when it is an NPC's or a keyword's EditorID.",
        "form filter", "NPCs", ReadHoldsParseNotes: false, OrderLine);

    static string OrderLine(DistributorLayerData s)
        => "order: files by name in byte order, each file's lines by key (A to Z, any case) then line, Form lines after every typed line; " +
           "for " + string.Join(", ", s.Format.Lines!.Types.Where(t => t.Conflict == SpidConflict.FirstWins).Select(t => t.Key)) +
           " the first line whose filters pass an NPC wins it.\n";

    /// <summary>A first-wins line's row: its place in its type's order.</summary>
    internal static string PlaceRow(SpidLines.Place p)
        => "          first wins: " + p.Type + (p.Group == nameof(SpidLineKind.Regular) ? "" : " (" + p.Group + ")")
           + " line " + p.Index + " of " + p.Total + " in SPID's order\n";
}

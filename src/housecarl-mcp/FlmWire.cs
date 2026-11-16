namespace HousecarlMcp;

/// <summary>FLM's own words in its part of the layer answer, and its passes and fill order; <see cref="DistributorWire"/> renders the rest.</summary>
static class FlmWire
{
    public static readonly DistributorWords Words = new(
        "FLM", "flm", f => f.FlmLines!.Reads,
        skipped => skipped + " more line(s) FLM reads no entry from or counts invalid as SimpleIni and FLM split the file (no '=', under a named section, or a key FLM does not know)",
        "FLM does not read this line",
        "; FLM names no plugin as a term: a filter's plugins gate the line and touch nothing.",
        "", "", ReadHoldsParseNotes: false, OrderLine);

    static string OrderLine(DistributorLayerData s)
        => "order: files by name in byte order, then " + string.Join(", then ", s.Format.Folders.Skip(1).Select(f => "Data\\" + f.Root + "'s")) + "; FLM reads every file's Filter, then Collection lines first, then file by file its Alias and Group lines, then the rest, " +
           "each file's lines by key (A to Z, any case) then line; it fills " + string.Join(", ", s.Format.FlmLines!.AddOrder) +
           " lists in that order, each key's lines in that order, and skips a form already in its list. Collection members are not read here.\n";
}

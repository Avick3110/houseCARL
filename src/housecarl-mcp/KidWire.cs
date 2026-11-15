namespace HousecarlMcp;

/// <summary>KID's own words in its part of the layer answer; <see cref="DistributorWire"/> renders the rest.</summary>
static class KidWire
{
    public static readonly DistributorWords Words = new(
        "KID", "kid", f => f.KidLines!.Reads,
        skipped => skipped + " more line(s) KID drops or ignores as it parses the file, before it reads any form (no '=', under a named section, a type or entry KID cannot parse, or a group with no members)",
        "KID does not apply this line",
        "a filter naming it; a keyword KID creates names no form.",
        "filters", "items", ReadHoldsParseNotes: true);
}

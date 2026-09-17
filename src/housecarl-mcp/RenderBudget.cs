namespace HousecarlMcp;

/// <summary>What a scan's RENDER may cost before it refuses instead of going silent (#582); contract, and where the
/// bounds come from, in docs/architecture/render-budget.md.</summary>
internal static class RenderBudget
{
    /// <summary>The render ceiling one call is given: ten minutes.</summary>
    internal const double CeilingMillis = 600_000;

    /// <summary>The declared cost of one detail row: reading the NAMED field paths off the match's body.</summary>
    internal const double MillisPerRow = 2.0;

    internal const double MillisPerWholeRecordRow = 40.0;

    /// <summary>The declared cost of one <c>form='identity'</c> row: an UNTYPED whole-plugin seek per FormID.</summary>
    internal const double MillisPerIdentityRow = 15.0;

    /// <summary>The declared cost of one comparison row, a floor rather than an average.</summary>
    internal const double MillisPerComparisonRow = 250.0;

    /// <summary>THE BOUND for the comparison forms: about a minute at <see cref="MillisPerComparisonRow"/> (#716).</summary>
    internal const int DefaultMaxComparisonRows = 250;

    internal const int DefaultMaxRenderRows = 300_000;

    internal const int DefaultMaxWholeRecordRows = 15_000;

    internal const int DefaultMaxIdentityRows = 40_000;

    /// <summary>The declared cost of resolving ONE asset path through the VFS.</summary>
    internal const double MillisPerAssetPath = 0.5;

    internal const int DefaultMaxAssetPaths = 1_200_000;

    /// <summary>The bounds in force. Settable so a test can drive the seam; production never assigns them.</summary>
    internal static int MaxRenderRows { get; set; } = DefaultMaxRenderRows;

    internal static int MaxWholeRecordRows { get; set; } = DefaultMaxWholeRecordRows;

    internal static int MaxIdentityRows { get; set; } = DefaultMaxIdentityRows;

    internal static int MaxComparisonRows { get; set; } = DefaultMaxComparisonRows;

    internal static int MaxAssetPaths { get; set; } = DefaultMaxAssetPaths;

    /// <summary>The chars a text render holds back from <c>max_chars</c> for its accounting line; pinned by
    /// RecordsRenderCostTests.TheAccountingLineIsReservedFromTheRowBudget.</summary>
    internal const int AccountingReserve = 64;

    internal static string AccountingLine(int rows, long ms) =>
        $"rendered {rows}{(rows == 1 ? " row in " : " rows in ")}{ms} ms\n";

    /// <summary>The batch lane's twin: the count is the bodies actually read, not the list's length.</summary>
    internal static string BodiesLine(int rows, long ms) =>
        $"read {rows}{(rows == 1 ? " record body in " : " record bodies in ")}{ms} ms\n";

    internal static string Projected(int rows, bool wholeRecord)
        => ProjectedAt(rows, wholeRecord ? MillisPerWholeRecordRow : MillisPerRow);

    /// <summary>The projected render at a stated per-row cost, in minutes only from 90 s up.</summary>
    internal static string ProjectedAt(int rows, double millisPerRow)
    {
        var ms = rows * millisPerRow;
        return ms >= 90_000 ? $"about {ms / 60_000:F0} minutes" : $"about {ms / 1000:F0} seconds";
    }

    /// <summary>What moves a SCAN's row count. Every remedy opens with its own verb in lower case.</summary>
    internal const string ScanRemedy =
        "narrow the scan terms (types=, plugins=, where=), or take the selection " +
        "in windows with limit= and offset= — offset= re-scans the selection from the start, so a window costs " +
        "more the further in it is. to_file= captures the COMPLETE selection rather than a window, so it renders " +
        "every row and does not combine with offset=: narrow the scan until the whole set fits, then write it in " +
        "one call.";

    internal const string ListLever =
        "pass fewer formids= entries: this lane reads a body for EVERY id in the list";

    internal const string ListClose =
        " Re-enter a big artifact a slice at a time, or narrow the selection that wrote it.";

    internal const string ListRemedy = ListLever +
        " before limit= and offset= window the render, so paging the same list does not lower what it costs." + ListClose;

    internal const string ListCensusRemedy = ListLever +
        " before it counts them, so counts_only= does not lower what it costs." + ListClose;

    /// <summary>What moves a WALK's row count: the seeds, the walk's own caps, or the chain form.</summary>
    internal const string WalkLever =
        "narrow the seeds you passed, or lower walk.depth or " +
        "walk.max_nodes, until the set the walk reaches fits";

    internal const string WalkRemedy = WalkLever +
        " — the rows are what the walk reached, seeds included, " +
        "so limit= and offset= window the render and not the walk. project.form='chain' lists the same reached set " +
        "without reading a body per rendered row, which is what to run first.";

    internal const string WalkCensusRemedy = WalkLever +
        " — the count is what the walk reached, seeds included, " +
        "and counts_only= does not lower it: the bodies are read before they are counted. project.form='chain' " +
        "counts the same reached set without reading a body per record, which is what to run first.";

    /// <summary>What moves the REVERSE CARRIER walk's row count: its budget is per seed, so walk.depth is no lever.</summary>
    internal const string ReverseCarrierLever =
        "pass fewer formids= seeds, or lower walk.max_nodes (the per-seed carrier " +
        "bound), until the set the walk reaches fits";

    internal const string ReverseCarrierRemedy = ReverseCarrierLever +
        " — the rows are the carriers it reached, seeds included, so " +
        "limit= and offset= window the render and not the walk. types= narrows the carrier types, and " +
        "project.form='chain' lists the same reached set without reading a body per rendered row, which is what to " +
        "run first.";

    internal const string ReverseCarrierCensusRemedy = ReverseCarrierLever +
        " — the count is the carriers it reached, seeds included, and " +
        "counts_only= does not lower it: the bodies are read before they are counted. types= narrows the carrier " +
        "types, and project.form='chain' counts the same reached set without reading a body per record, which is " +
        "what to run first.";

    /// <summary>What moves the TRANSITIVE REVERSE walk's row count. project.form='chain' is not a lever here.</summary>
    internal const string ReverseTransitiveLever =
        "pass fewer formids= seeds, or lower walk.depth or walk.max_nodes " +
        "(one budget shared across every seed and hop on this lane), until the set the walk reaches fits";

    internal const string ReverseTransitiveRemedy = ReverseTransitiveLever +
        " — the rows " +
        "are what the walk reached, seeds included, so limit= and offset= window the render and not the walk. " +
        "project.form='chain' is not a lever here: this walk expands one shared frontier and has no per-seed path " +
        "for chain to draw.";

    internal const string ReverseTransitiveCensusRemedy = ReverseTransitiveLever +
        " — the count " +
        "is what the walk reached, seeds included, and counts_only= does not lower it: the bodies are read before " +
        "they are counted. project.form='chain' is not a lever here: this walk expands one shared frontier and has " +
        "no per-seed path for chain to draw.";

    /// <summary>The refusal for a render over its lane's bound, or null when it fits. The
    /// <c>form='everything'</c> lane and a <c>counts_only</c> census each take their own wording.</summary>
    internal static string? Refuse(int rows, bool wholeRecord, string? remedy = null, bool census = false)
    {
        int bound = wholeRecord ? MaxWholeRecordRows : MaxRenderRows;
        if (rows <= bound) return null;
        var opens = census ? $"this call counts {rows:N0} records and each one reads a "
                           : $"this call renders {rows:N0} rows and each one reads a ";
        var when = census ? " before it is counted" : "";
        var spend = census ? " of reading" : " of render";
        var lead = wholeRecord
            ? $"error: {opens}WHOLE record body{when} — {Projected(rows, true)}" +
              $"{spend}, past the {bound:N0}-row bound form='everything' is given (a client stops waiting at 30 " +
              $"minutes). Name the fields you need instead — project.form='fields' with fields=[…] reads a body per " +
              $"row too but costs a fraction of a whole-record read, and is bounded at {MaxRenderRows:N0} rows. Or "
            : $"error: {opens}record body{when} — {Projected(rows, false)}{spend}, " +
              $"past the {bound:N0}-row bound one call is given (a client stops waiting at 30 minutes). ";
        var lever = remedy ?? ScanRemedy;
        return lead + (wholeRecord ? lever : char.ToUpperInvariant(lever[0]) + lever[1..]);
    }

    /// <summary>The refusal for a comparison form (delta/tree) over its own bound, or null when it fits;
    /// <paramref name="lever"/> is one of the four below, picked by the caller's lane.</summary>
    internal static string? RefuseComparison(int rows, string form, string lever) =>
        rows <= MaxComparisonRows
            ? null
            : $"error: this {form} reads {(form == "delta" ? "two versions" : "every override")} of each of {rows:N0} records — " +
              $"{ProjectedAt(rows, MillisPerComparisonRow)} at the {MillisPerComparisonRow / 1000:0.##} s a row measured for these forms, " +
              $"past the {MaxComparisonRows:N0}-row bound the comparison forms are given; " +
              lever;

    /// <summary>The comparison bound's levers, one per lane.</summary>
    internal const string ComparisonScanLever =
        "take it in a window with limit= at or below the bound, narrow the selection with where= or types=, or read the winning value alone with project.form='fields'.";

    internal const string ComparisonWholeSelectionLever =
        "narrow the selection with where= or types= — a census and a to_file= artifact cover every selected record, so limit= does not lower what they read — or read the winning value alone with project.form='fields'.";

    internal const string ComparisonListLever =
        "pass fewer formids= entries: this lane reads every provider of every id in the list before limit= windows the render.";

    internal const string ComparisonWalkLever =
        "narrow the seeds you passed, or lower walk.depth or walk.max_nodes, until the set the walk reaches fits — the rows are what the walk reached, so limit= windows the render and not the walk.";

    /// <summary>The refusal for an <c>asset_status</c> call over <see cref="MaxAssetPaths"/>, or null when it fits;
    /// <paramref name="atLeast"/> is the lane whose enumeration stopped at the bound, so the count is a floor.</summary>
    internal static string? RefuseAssetPaths(int paths, bool wholeSelection, bool atLeast = false)
    {
        if (paths <= MaxAssetPaths) return null;
        return $"error: this call resolves {(atLeast ? "at least " : "")}{paths:N0} asset path(s) through the VFS, each one a lookup in every " +
               $"active archive plus a loose-directory warm across every mod folder — {(atLeast ? "over " : "")}{ProjectedAt(paths, MillisPerAssetPath)}, " +
               $"past the {MaxAssetPaths:N0}-path bound one call is given (a client stops waiting at 30 minutes). " +
               (atLeast ? "The sweep stopped counting there, so nothing was walked past the bound and nothing was resolved. " : "") +
               (wholeSelection
                   ? "this call resolves the COMPLETE selection — to_file= and counts_only= both do — so limit= " +
                     "does not lower what it resolves: narrow the " +
                     "selection itself — a tighter under= selector (anchor it at the folder you mean, not at " +
                     "'meshes/**'), or fewer asset_paths=/formids= entries — and ask for it in one call."
                   : "Narrow the selection with a tighter under= selector, pass fewer asset_paths=/formids= entries, " +
                     "or take it in windows with limit= and offset=.");
    }

    /// <summary>The refusal for an <c>form='identity'</c> render over its own bound, or null when it fits.</summary>
    internal static string? RefuseIdentity(int rows, string remedy, bool census = false)
    {
        if (rows <= MaxIdentityRows) return null;
        return $"error: this call resolves {rows:N0} FormIDs and each one reads its winner's body by an UNTYPED " +
               $"whole-plugin seek — {ProjectedAt(rows, MillisPerIdentityRow)}{(census ? " of reading" : " of render")}, past the {MaxIdentityRows:N0}-row " +
               $"bound form='identity' is given (a client stops waiting at 30 minutes). project.form='summary' reads the " +
               $"same type, editorid and winner off a read gathered per plugin, at a fraction of the cost and bounded at " +
               $"{MaxRenderRows:N0} rows — project.form='fields' with fields=[\"Name\"] if you need the display name too. Or " + remedy;
    }
}

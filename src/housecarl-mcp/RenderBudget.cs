namespace HousecarlMcp;

/// <summary>
/// What a scan's RENDER may cost before the call refuses instead of going silent (#582).
///
/// <para>The scan terms (types=/plugins=/where=) bound the scan. Nothing bounded the render, and the render is the
/// other half: every rendered row reads a body. A 66,856-row fields projection therefore ran for about 45 minutes
/// with no progress, past the client's 30-minute idle timeout, and the caller learned the cost by waiting.</para>
///
/// <para>So the cost is stated up front. Rows times the per-row cost is what the call will spend rendering; past the
/// bound it refuses and names the shapes that fit. What each call ACTUALLY spent comes back in the accounting as
/// <c>render_ms</c>, which is how the estimates here are checked against a real order rather than trusted.</para>
///
/// <para>TWO costs, because there are two lanes and they are two orders of magnitude apart. A row that reads NAMED
/// field paths off a body costs a field read; a row of <c>form='everything'</c> materialises every field of the
/// record, measured at ~30 ms a row against ~0.013 ms for a three-field projection on the same world. One number
/// over both would either wave the expensive lane through or refuse the cheap one for nothing.</para>
/// </summary>
internal static class RenderBudget
{
    /// <summary>The render ceiling one call is given: ten minutes — a third of the 30-minute idle timeout a Claude
    /// Code client gives a call, leaving the scan itself, the artifact write and a retry inside the same
    /// window.</summary>
    internal const double CeilingMillis = 600_000;

    /// <summary>The declared cost of rendering one detail row: reading the NAMED field paths off the match's body.
    /// Deliberately pessimistic. Measured at 0.013–0.06 ms a row on a synthetic order; the reported cost on the ARR
    /// order before the per-row whole-plugin seek was removed was ~40 ms a row, and removing it measured a 30x–200x
    /// reduction, so 2 ms leaves better than an order of magnitude over the extrapolated real-order figure.</summary>
    internal const double MillisPerRow = 2.0;

    /// <summary>The declared cost of one <c>form='everything'</c> row: the whole record materialised, every field.
    /// Measured at 29.9–32 ms a row on a 2,000-weapon synthetic scan — the body seek is not what costs here, the
    /// field materialisation is, so gathering bodies per plugin does not move this number. 40 ms carries the
    /// measurement plus a margin for a record type fatter than a weapon.</summary>
    internal const double MillisPerWholeRecordRow = 40.0;

    /// <summary>The declared cost of one <c>form='identity'</c> row. Resolving a FormID to its winner's type,
    /// editorid and name reads that winner's BODY, and the index carries no record type to seek by, so the row is an
    /// UNTYPED whole-plugin scan — the per-row seek the other lanes had removed. Measured at 12.5–14.0 ms a row over
    /// 3,000 ids on the ARR order (3,801 plugins), against 0.05–0.07 ms for the same ids read as named fields, which
    /// gather per plugin. 15 ms carries the measurement plus a margin.</summary>
    internal const double MillisPerIdentityRow = 15.0;

    /// <summary>The declared cost of one comparison row. Neither form reads ONE body: a tree row reads every
    /// provider of its record, a delta row reads its two poles, and each of those is a seek through its plugin
    /// rather than a gathered read. Measured over placed references on a 3,571-line order, where the contested ones
    /// have two providers: 0.28 s a tree row, 0.19 s a delta row on the same records. A record with more providers
    /// costs the tree more again — a worldspace has hundreds — so this is a floor, not an average.</summary>
    internal const double MillisPerComparisonRow = 250.0;

    /// <summary>THE BOUND for the comparison forms: about a minute at <see cref="MillisPerComparisonRow"/>. Far
    /// lower than the other lanes' because the row is: a job past this announces itself instead of going quiet
    /// (#716).</summary>
    internal const int DefaultMaxComparisonRows = 250;

    /// <summary>THE BOUND for a named-fields render: ten minutes at <see cref="MillisPerRow"/>.</summary>
    internal const int DefaultMaxRenderRows = 300_000;

    /// <summary>THE BOUND for <c>form='everything'</c>: ten minutes at <see cref="MillisPerWholeRecordRow"/>.</summary>
    internal const int DefaultMaxWholeRecordRows = 15_000;

    /// <summary>THE BOUND for <c>form='identity'</c>: ten minutes at <see cref="MillisPerIdentityRow"/>.</summary>
    internal const int DefaultMaxIdentityRows = 40_000;

    /// <summary>The declared cost of resolving ONE asset path through the VFS: a lookup in every active archive's
    /// table, plus — the first time a path in that DIRECTORY is asked for — a loose warm that stats the directory in
    /// every mod folder. Measured end to end at 0.22 ms a path on the ARR order (131,496 FaceGen paths across 65,748
    /// NPCs in 28.8 s, artifact write included); 0.5 ms carries that plus better than a 2x margin for a sweep spread
    /// over more directories, which is what makes the warm bite.</summary>
    internal const double MillisPerAssetPath = 0.5;

    /// <summary>THE BOUND for one <c>asset_status</c> call: ten minutes at <see cref="MillisPerAssetPath"/>. The
    /// whole-order FaceGen pairing sweep this bound was measured on is 131,496 paths and sits an order of magnitude
    /// inside it; an unanchored <c>under=["meshes/**"]</c> is the shape that reaches it.</summary>
    internal const int DefaultMaxAssetPaths = 1_200_000;

    /// <summary>The bounds in force. Settable so a test can drive the seam over a world of a few records instead of
    /// building 300,000 — the same reason <see cref="Artifacts.WriteCrossQuery"/> takes a row cap. Production never
    /// assigns them.</summary>
    internal static int MaxRenderRows { get; set; } = DefaultMaxRenderRows;

    /// <inheritdoc cref="MaxRenderRows"/>
    internal static int MaxWholeRecordRows { get; set; } = DefaultMaxWholeRecordRows;

    /// <inheritdoc cref="MaxRenderRows"/>
    internal static int MaxIdentityRows { get; set; } = DefaultMaxIdentityRows;

    /// <inheritdoc cref="MaxRenderRows"/>
    internal static int MaxComparisonRows { get; set; } = DefaultMaxComparisonRows;

    /// <inheritdoc cref="MaxRenderRows"/>
    internal static int MaxAssetPaths { get; set; } = DefaultMaxAssetPaths;

    /// <summary>The chars a text render holds back from <c>max_chars</c> for the accounting line it appends after
    /// its rows. Held back for the same reason the owned-child clause is: a line the response is going to state is
    /// spoken for. Wide enough for either line at its longest values, which a test holds it to.</summary>
    internal const int AccountingReserve = 64;

    /// <summary>The accounting line itself: the rows this render produced and what they cost.</summary>
    internal static string AccountingLine(int rows, long ms) =>
        $"rendered {rows}{(rows == 1 ? " row in " : " rows in ")}{ms} ms\n";

    /// <summary>The batch lane's twin: its bodies are READ before the render, so the count is the bodies actually
    /// read — not what a max_chars cut left showing, and not the list's length, which under a source= pole or a
    /// malformed token is more ids than the lane read a body for.</summary>
    internal static string BodiesLine(int rows, long ms) =>
        $"read {rows}{(rows == 1 ? " record body in " : " record bodies in ")}{ms} ms\n";

    /// <summary>The projected render for <paramref name="rows"/> at the lane's own per-row cost.</summary>
    internal static string Projected(int rows, bool wholeRecord)
        => ProjectedAt(rows, wholeRecord ? MillisPerWholeRecordRow : MillisPerRow);

    /// <summary>The projected render at a stated per-row cost. Minutes only from 90 s up: rounding to whole minutes
    /// below that says "about 1 minutes", and the 60–90 s band reads better in seconds anyway. The comparison bound
    /// is the first that can land there — every other lane's starts at ten minutes.</summary>
    internal static string ProjectedAt(int rows, double millisPerRow)
    {
        var ms = rows * millisPerRow;
        return ms >= 90_000 ? $"about {ms / 60_000:F0} minutes" : $"about {ms / 1000:F0} seconds";
    }

    /// <summary>What moves a SCAN's row count: the scan terms, or a window over them. Each remedy opens with its own
    /// verb in lower case; <see cref="Refuse"/> capitalises it when the sentence starts there.</summary>
    internal const string ScanRemedy =
        "narrow the scan terms (types=, plugins=, where=), or take the selection " +
        "in windows with limit= and offset= — offset= re-scans the selection from the start, so a window costs " +
        "more the further in it is. to_file= captures the COMPLETE selection rather than a window, so it renders " +
        "every row and does not combine with offset=: narrow the scan until the whole set fits, then write it in " +
        "one call.";

    /// <summary>What moves the <c>formids=</c> lane's row count. It has no scan terms to narrow, and it reads a body
    /// for every id it was handed BEFORE the render window applies, so paging the same call does not lower its
    /// cost.</summary>
    internal const string ListLever =
        "pass fewer formids= entries: this lane reads a body for EVERY id in the list";

    /// <summary>The formids= lane's closing move, the same whether the call renders or counts.</summary>
    internal const string ListClose =
        " Re-enter a big artifact a slice at a time, or narrow the selection that wrote it.";

    internal const string ListRemedy = ListLever +
        " before limit= and offset= window the render, so paging the same list does not lower what it costs." + ListClose;

    /// <summary>Its counts_only twin: a census windows nothing, so what does not lower its cost is the census
    /// itself.</summary>
    internal const string ListCensusRemedy = ListLever +
        " before it counts them, so counts_only= does not lower what it costs." + ListClose;

    /// <summary>What moves a WALK's row count. The rows are what the walk REACHED, not what the scan selected, so
    /// the scan window is the wrong lever: the seeds, the walk's own caps, or the chain form, which lists the same
    /// reached set without reading a body PER RENDERED ROW — the walk reads one per reached node whatever the form,
    /// so chain saves the render's read and not the walk's. The seeds are named without the scan terms, because a
    /// walk is reached from the formids= lane too and there they are not part of the call.</summary>
    internal const string WalkLever =
        "narrow the seeds you passed, or lower walk.depth or " +
        "walk.max_nodes, until the set the walk reaches fits";

    internal const string WalkRemedy = WalkLever +
        " — the rows are what the walk reached, seeds included, " +
        "so limit= and offset= window the render and not the walk. project.form='chain' lists the same reached set " +
        "without reading a body per rendered row, which is what to run first.";

    /// <summary>Its counts_only twin: the walk's levers are the same, and what does not lower the cost is the
    /// census — chain still answers the same reached set without the list lane's read.</summary>
    internal const string WalkCensusRemedy = WalkLever +
        " — the count is what the walk reached, seeds included, " +
        "and counts_only= does not lower it: the bodies are read before they are counted. project.form='chain' " +
        "counts the same reached set without reading a body per record, which is what to run first.";

    /// <summary>What moves the REVERSE CARRIER walk's row count. Its seeds are formids= and its budget is per seed,
    /// so walk.depth is not a lever — the walk reaches nothing past hop 1.</summary>
    internal const string ReverseCarrierLever =
        "pass fewer formids= seeds, or lower walk.max_nodes (the per-seed carrier " +
        "bound), until the set the walk reaches fits";

    internal const string ReverseCarrierRemedy = ReverseCarrierLever +
        " — the rows are the carriers it reached, seeds included, so " +
        "limit= and offset= window the render and not the walk. types= narrows the carrier types, and " +
        "project.form='chain' lists the same reached set without reading a body per rendered row, which is what to " +
        "run first.";

    /// <summary>Its counts_only twin.</summary>
    internal const string ReverseCarrierCensusRemedy = ReverseCarrierLever +
        " — the count is the carriers it reached, seeds included, and " +
        "counts_only= does not lower it: the bodies are read before they are counted. types= narrows the carrier " +
        "types, and project.form='chain' counts the same reached set without reading a body per record, which is " +
        "what to run first.";

    /// <summary>What moves the TRANSITIVE REVERSE walk's row count. project.form='chain' is not a lever here: that
    /// walk expands one shared frontier and has no per-seed path to draw, which is why chain refuses on it.</summary>
    internal const string ReverseTransitiveLever =
        "pass fewer formids= seeds, or lower walk.depth or walk.max_nodes " +
        "(one budget shared across every seed and hop on this lane), until the set the walk reaches fits";

    internal const string ReverseTransitiveRemedy = ReverseTransitiveLever +
        " — the rows " +
        "are what the walk reached, seeds included, so limit= and offset= window the render and not the walk. " +
        "project.form='chain' is not a lever here: this walk expands one shared frontier and has no per-seed path " +
        "for chain to draw.";

    /// <summary>Its counts_only twin.</summary>
    internal const string ReverseTransitiveCensusRemedy = ReverseTransitiveLever +
        " — the count " +
        "is what the walk reached, seeds included, and counts_only= does not lower it: the bodies are read before " +
        "they are counted. project.form='chain' is not a lever here: this walk expands one shared frontier and has " +
        "no per-seed path for chain to draw.";

    /// <summary>The refusal for a render over its lane's bound, or null when it fits. One sentence for the cost, one
    /// for the shapes that fit, each carrying the caveat that decides between them. <paramref name="wholeRecord"/>
    /// is the <c>form='everything'</c> lane, whose row is a whole record and whose bound is therefore its own —
    /// and whose first remedy is naming the fields, since that is what moves it between the two.
    /// <paramref name="remedy"/> is the second sentence's lever, defaulting to the scan's — the lanes whose levers
    /// differ in kind pass their own (<see cref="ListRemedy"/>, <see cref="WalkRemedy"/>).
    /// <para><paramref name="census"/> is a <c>counts_only</c> call, which renders NOTHING: the render's words would
    /// be false of it, and the fact that explains its refusal — the list lane reads every body BEFORE it counts them
    /// — is the one the render's lead never states. Its lever is the census twin of the lane's remedy, because
    /// windowing a render the caller did not ask for is not a remedy.</para></summary>
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

    /// <summary>The refusal for a comparison form (delta/tree) over its own bound, or null when it fits. One
    /// sentence: what it would read, what that costs, and the lever. <paramref name="form"/> is the form's own name
    /// and decides what the sentence says the row READS — a tree reads every provider, a delta reads two poles —
    /// so neither form is described as the other. <paramref name="lever"/> is the one the caller's lane can actually
    /// pull, picked by the caller from the four below.</summary>
    internal static string? RefuseComparison(int rows, string form, string lever) =>
        rows <= MaxComparisonRows
            ? null
            : $"error: this {form} reads {(form == "delta" ? "two versions" : "every override")} of each of {rows:N0} records — " +
              $"{ProjectedAt(rows, MillisPerComparisonRow)} at the {MillisPerComparisonRow / 1000:0.##} s a row measured for these forms, " +
              $"past the {MaxComparisonRows:N0}-row bound the comparison forms are given; " +
              lever;

    /// <summary>The comparison bound's levers, one per lane: a scan can take a window (the default 500 is one, so
    /// the lever is a limit= at or below the bound whether or not the caller already passed one), a census or a
    /// to_file= artifact covers the whole selection whatever limit= says, the formids= lane reads the list it was
    /// handed, and a walk's rows are what it reached.</summary>
    internal const string ComparisonScanLever =
        "take it in a window with limit= at or below the bound, narrow the selection with where= or types=, or read the winning value alone with project.form='fields'.";

    /// <inheritdoc cref="ComparisonScanLever"/>
    internal const string ComparisonWholeSelectionLever =
        "narrow the selection with where= or types= — a census and a to_file= artifact cover every selected record, so limit= does not lower what they read — or read the winning value alone with project.form='fields'.";

    /// <inheritdoc cref="ComparisonScanLever"/>
    internal const string ComparisonListLever =
        "pass fewer formids= entries: this lane reads every provider of every id in the list before limit= windows the render.";

    /// <inheritdoc cref="ComparisonScanLever"/>
    internal const string ComparisonWalkLever =
        "narrow the seeds you passed, or lower walk.depth or walk.max_nodes, until the set the walk reaches fits — the rows are what the walk reached, so limit= windows the render and not the walk.";

    /// <summary>The refusal for an <c>asset_status</c> call whose selection is over <see cref="MaxAssetPaths"/>, or
    /// null when it fits. Its own tier, because the row is not a record read at all: it is a VFS resolution, and what
    /// it costs is archive tables and loose directory warms rather than a record body.
    /// <paramref name="wholeSelection"/> is the <c>to_file=</c> disposition, whose artifact covers every selected
    /// path — so limit= is not its lever and the sentence does not offer it.</summary>
    internal static string? RefuseAssetPaths(int paths, bool wholeSelection)
    {
        if (paths <= MaxAssetPaths) return null;
        return $"error: this call resolves {paths:N0} asset path(s) through the VFS, each one a lookup in every " +
               $"active archive plus a loose-directory warm across every mod folder — {ProjectedAt(paths, MillisPerAssetPath)}, " +
               $"past the {MaxAssetPaths:N0}-path bound one call is given (a client stops waiting at 30 minutes). " +
               (wholeSelection
                   ? "to_file= writes the COMPLETE selection, so limit= does not lower what it resolves: narrow the " +
                     "selection itself — a tighter under= selector (anchor it at the folder you mean, not at " +
                     "'meshes/**'), or fewer asset_paths=/formids= entries — and write it in one call."
                   : "Narrow the selection with a tighter under= selector, pass fewer asset_paths=/formids= entries, " +
                     "or take it in windows with limit= and offset=.");
    }

    /// <summary>The refusal for an <c>form='identity'</c> render over its own bound, or null when it fits. Its own
    /// tier and its own lead, because its row is neither a named-field read nor a whole record: it is one untyped
    /// whole-plugin seek per FormID. The shape it names first is the form that answers the same question off a
    /// gathered read, the way the whole-record lead names the fields form.</summary>
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

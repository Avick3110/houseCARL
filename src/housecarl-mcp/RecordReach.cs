using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlMcp;

// The reach term on records where=: each SPID or KID line read with its layer in one replay context over the scan's
// own view, and judged over every record of its type by the format's own rules. Read-only; nothing is kept between calls.
internal sealed partial class RecordReads
{
    /// <summary>What the reach terms select: the records every term says yes to, the record types they read, and one
    /// note per term; or the refusal naming the line a format cannot read.</summary>
    internal sealed record ReachSelection(IReadOnlyList<FormKey> Yes, IReadOnlyList<string> Types, string Note, string? Refusal);

    /// <summary>Judge every reach term over <paramref name="view"/>, the layer read once per format in one replay context.</summary>
    internal ReachSelection Reach(IReadOnlyList<ReachTerm> terms, LoadOrderResolver.IndexView view, CancellationToken ct)
    {
        var captured = _host.CaptureAssets();
        using var session = _host.Resolver.OpenSession();
        var replay = _host.OpenSkyPatcherReplay(captured, view, session, out _, null, null)!;   // no draft, so never refused
        var world = new World(view, replay, ct);
        var fields = ReachFields.Load();
        var results = new List<ReachResult>();
        var spid = terms.Where(t => t.Format == "spid").ToList();
        var kid = terms.Where(t => t.Format == "kid").ToList();
        if (spid.Count > 0)
        {
            var format = RuntimeFormats.Spid;
            var g = format.Lines!;
            var asked = new List<SpidLines.Line>();
            foreach (var t in spid)
            {
                var parsed = SpidLines.Parse(t.Line, g);
                if (parsed.Count != 1) return Refuse(t, "is no SPID line (expected '<Type> = <form>|<strings>|<forms>|<level>|<traits>|<count>|<chance>')");
                var l = parsed[0];
                if (l.Skipped is { } why) return Refuse(t, $"SPID skips it: it {why}");
                if (l.Kind is SpidLineKind.ExclusiveGroup or SpidLineKind.Linked)
                    return Refuse(t, $"is {(l.Kind == SpidLineKind.Linked ? "a Linked line, which goes where its parent line goes; ask for the parent line" : "an ExclusiveGroup line, which hands nothing out")}");
                asked.Add(l);
            }
            var layer = Layer(format, replay, (text, _) => SpidLines.Parse(text, g), ct);
            var ctx = new SpidLines.Context(replay.Forms, view.WinnerClassOf, format);
            // One read of every line, the asked ones last, so the call's EditorID table walks every type once.
            var reads = SpidLines.ReadAll(layer.Concat(asked).ToList(), ctx);
            var pairs = layer.Select((l, i) => (l, reads[i])).ToList();
            for (int i = 0; i < spid.Count; i++)
                results.Add(SpidReach.Evaluate(spid[i], asked[i], reads[layer.Count + i]!, pairs, ctx, world, fields));
        }
        if (kid.Count > 0)
        {
            var format = RuntimeFormats.Kid;
            var asked = new List<KidLines.Line>();
            foreach (var t in kid)
            {
                var parsed = KidLines.Parse(t.Line, format);
                if (parsed.Count != 1) return Refuse(t, "is no KID line (expected '<keyword> = <keyword>|<type>|<filters>|<traits>|<chance>')");
                var l = parsed[0];
                if (l.Skipped is { } why) return Refuse(t, $"KID skips it: it {why}");
                if (l.IsGroup) return Refuse(t, "is an ExclusiveGroup line, which hands nothing out");
                asked.Add(l);
            }
            var layer = Layer(format, replay, (text, _) => KidLines.Parse(text, format), ct);
            var ctx = new KidLines.Context(replay.Forms, view.WinnerClassOf, format);
            var reads = KidLines.ReadAll(layer.Concat(asked).ToList(), ctx);
            var pairs = layer.Select((l, i) => (l, reads[i])).ToList();
            for (int i = 0; i < kid.Count; i++)
                results.Add(KidReach.Evaluate(kid[i], asked[i], reads[layer.Count + i]!, pairs, ctx, world, fields));
        }
        // Several terms are ANDed like every where= term: the rows are the records every one says yes to.
        var ordered = terms.Select(t => results.First(r => ReferenceEquals(r.Term, t))).ToList();
        IEnumerable<FormKey> yes = ordered[0].Yes;
        foreach (var r in ordered.Skip(1)) yes = yes.Where(r.Yes.Contains);
        return new ReachSelection(yes.ToList(), ordered.Select(r => r.RecordType).Distinct().ToList(),
                                  string.Join(" ", ordered.Select(r => r.Sentence())), null);

        static ReachSelection Refuse(ReachTerm t, string why) =>
            new(Array.Empty<FormKey>(), Array.Empty<string>(), "", $"where= term '{t.Text}': the line {why}.");
    }

    /// <summary>Every line of the format's files as it finds them, in file order; a file it does not apply or cannot read gives none.</summary>
    static List<TLine> Layer<TLine>(DistributorFormat format, AssetLayers.SkyPatcherReplay replay, Func<string, string, IReadOnlyList<TLine>> parse, CancellationToken ct)
    {
        var lines = new List<TLine>();
        foreach (var f in DistributorDiscovery.Scan(replay.Assets, format))
        {
            ct.ThrowIfCancellationRequested();
            if (f.NotApplied is not null || f.LooseFilePath is null) continue;
            try { lines.AddRange(parse(File.ReadAllText(f.LooseFilePath), f.RelPath)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return lines;
    }

    /// <summary>The load order as the evaluators read it: the scan's view, and the replay context's names and lists.</summary>
    sealed class World(LoadOrderResolver.IndexView view, AssetLayers.SkyPatcherReplay replay, CancellationToken ct) : IReachWorld
    {
        public CancellationToken Ct => ct;

        public IEnumerable<IMajorRecordGetter> Winners(Type getter)
            => view.WinnerRecordsOfType(new[] { getter }).Select(x => x.body);

        public Type? ClassOf(FormKey fk) => view.WinnerClassOf(fk);

        public IReadOnlyList<FormKey>? ListItems(FormKey list) => replay.ListItemsOf(list);

        public string? EditorIdGap(string mutagenType) => replay.Forms.EditorIdGap(mutagenType);

        public uint? RuntimeFormId(FormKey fk)
            => HousecarlCore.RuntimeFormId.TryParse(view.RuntimeAddressOf(fk).FormId, out var v) ? v : null;
    }
}

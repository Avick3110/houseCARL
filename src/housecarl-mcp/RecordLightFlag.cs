namespace HousecarlMcp;

internal sealed partial class RecordWrites
{
    /// <summary>The refusal for a plugin no houseCARL folder owns: its header is not houseCARL's to change.</summary>
    internal static string NotAHouseCarlPatch(string name)
        => $"refused: '{name}' is not a houseCARL patch (no houseCARL mod folder holds it), and houseCARL sets the light flag "
         + "only on its own patches; tick ESL in xEdit's file header instead. Nothing was written.";

    /// <summary>Set or clear the light flag on one houseCARL patch, found the way <c>into=</c> finds it, moving no FormID.</summary>
    public LightFlag.SetOutcome SetLightFlag(string source, bool light)
    {
        lock (_host.WriteGate)                                           // one write at a time, resolve through commit
        {
            var snapshot = _host.ConfiguredRoots();
            string path;
            try { path = ResolveOutputPath(snapshot, patchName: null, into: source, out _, out _); }
            catch (InvalidOperationException) when (Directory.Exists(snapshot.Roots.ModsDir) && OutputLocations.ReachesNoOwnedFolder(snapshot.Roots, source))
                { return LightFlag.SetOutcome.Fail(NotAHouseCarlPatch(Path.GetFileName(source.Trim()))); }
            catch (Exception ex) { return LightFlag.SetOutcome.Fail(ex.Message); }
            return LightFlag.Set(_host.Resolver, path, light);
        }
    }

    /// <summary>A successful write into a houseCARL patch ends with the light offer when the patch is not light and fits.</summary>
    static string? LightOfferAfter(bool written, string? path)
        => written && !string.IsNullOrEmpty(path) && File.Exists(path) && HousecarlOwnerMeta.MarksOwned(Path.GetDirectoryName(path))
            ? LightFlag.OfferFor(path)
            : null;

    internal static WritePatchBuilder.PatchOutcome WithLightOffer(WritePatchBuilder.PatchOutcome o)
        => LightOfferAfter(o.Success && !o.DryRun, o.OutputPath) is { } offer ? o with { Note = JoinNotes(o.Note, offer) } : o;
    internal static WritePatchBuilder.CreateOutcome WithLightOffer(WritePatchBuilder.CreateOutcome o)
        => LightOfferAfter(o.Success, o.OutputPath) is { } offer ? o with { Note = JoinNotes(o.Note, offer) } : o;
    internal static WritePatchBuilder.ForwardOutcome WithLightOffer(WritePatchBuilder.ForwardOutcome o)
        => LightOfferAfter(o.Success && !o.DryRun, o.OutputPath) is { } offer ? o with { Note = JoinNotes(o.Note, offer) } : o;
    internal static WritePatchBuilder.RemovalOutcome WithLightOffer(WritePatchBuilder.RemovalOutcome o)
        => LightOfferAfter(o.Success, o.OutputPath) is { } offer ? o with { Note = JoinNotes(o.Note, offer) } : o;
    internal static ClosureCopyOutcome WithLightOffer(ClosureCopyOutcome o)
        => LightOfferAfter(o.Success, o.OutPath) is { } offer ? o with { Note = offer } : o;
}

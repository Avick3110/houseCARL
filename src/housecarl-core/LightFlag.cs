using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Analysis;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>The light (ESL) flag on a houseCARL patch: the window Mutagen gives for its header and masters, whether its
/// own records fit it, and the one write that sets or clears the flag with every FormID kept. Contract in
/// docs/architecture/write-path.md.</summary>
public static class LightFlag
{
    /// <summary>What a plugin's own records say about the light window.</summary>
    /// <param name="Low">The lowest object id Mutagen allows in a light plugin with this header and masters.</param>
    /// <param name="High">The highest, 0xFFF on Skyrim SE.</param>
    /// <param name="Own">How many records the plugin itself defines.</param>
    /// <param name="Highest">The highest own object id, null when it defines none.</param>
    /// <param name="Outside">How many own ids fall outside the window.</param>
    /// <param name="FirstOutside">The lowest of those, null when none.</param>
    public sealed record Fit(uint Low, uint High, int Own, uint? Highest, int Outside, uint? FirstOutside,
                             IReadOnlyList<string> NewWorldspaces, IReadOnlyList<string> NewInteriorCells)
    {
        /// <summary>Every own id sits in the window and the plugin adds no worldspace, so the flag can go on as it is.</summary>
        public bool Fits => Outside == 0 && NewWorldspaces.Count == 0;
    }

    /// <summary>Read <paramref name="mod"/>'s own records against Mutagen's light window for its header and masters.</summary>
    public static Fit Assess(ISkyrimModGetter mod)
    {
        var range = RecordCompactionCompatibilityDetection.GetSmallMasterRange(mod);
        uint low = range?.Min ?? 0x800, high = range?.Max ?? FormIdRange.EslWindowCeiling;
        int own = 0, outside = 0;
        uint? highest = null, firstOutside = null;
        foreach (var r in mod.EnumerateMajorRecords())
        {
            if (r.FormKey.ModKey != mod.ModKey) continue;
            own++;
            var id = r.FormKey.ID;
            if (highest is null || id > highest) highest = id;
            if (id >= low && id <= high) continue;
            outside++;
            if (firstOutside is null || id < firstOutside) firstOutside = id;
        }
        var worlds = mod.Worldspaces.Where(w => w.FormKey.ModKey == mod.ModKey).Select(Name).ToList();
        var cells = mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells)
            .Where(c => c.FormKey.ModKey == mod.ModKey).Select(Name).ToList();
        return new Fit(low, high, own, highest, outside, firstOutside, worlds, cells);
    }

    static string Name(ISkyrimMajorRecordGetter r) => r.EditorID is { Length: > 0 } e ? $"{e} ({FormIdToken.Of(r.FormKey)})" : FormIdToken.Of(r.FormKey);

    static string Hex(uint id) => $"0x{id:X3}";

    /// <summary>The refusal a write into a light patch gets when it would put an own id past the window: nothing is written.</summary>
    public static string GrowthRefusal(string fileName, Fit fit)
        => $"houseCARL did not write '{fileName}', which is unchanged: it is flagged light (ESL), which holds its own records "
         + $"only at {Hex(fit.Low)}–{Hex(fit.High)}, and this write would put {fit.Outside} of its {fit.Own} at {Hex(fit.FirstOutside ?? 0)} "
         + $"and above; to keep building it, rebuild it as a full plugin with {ToolNames.EslFlag} source=\"{fileName}\" esl=false "
         + "(every FormID stays as it is; it then takes a full load-order slot), then run this write again.";

    /// <summary>The line a write into a houseCARL patch that is not light, and fits, ends with; null when it does not fit.</summary>
    public static string? Offer(string fileName, Fit fit)
        => fit.Fits
            ? $"this patch fits the light (ESL) window: {fit.Own} own record{(fit.Own == 1 ? "" : "s")}"
              + (fit.Highest is { } h ? $", the highest at {Hex(h)} of {Hex(fit.High)}" : "")
              + $"; when you are done building it, {ToolNames.EslFlag} source=\"{fileName}\" flags it light without moving a FormID."
            : null;

    /// <summary>The note a light plugin with a new interior cell carries, null when it adds none.</summary>
    public static string? InteriorCellNote(Fit fit)
        => fit.NewInteriorCells.Count == 0 ? null
         : $"it adds {fit.NewInteriorCells.Count} new interior cell{(fit.NewInteriorCells.Count == 1 ? "" : "s")} "
         + $"({string.Join(", ", fit.NewInteriorCells.Take(3))}{(fit.NewInteriorCells.Count > 3 ? ", …" : "")}): in a light plugin, a new "
         + "interior cell loses its temporary references when another plugin overrides that cell, unless SSE Engine Fixes "
         + "7.0.13 or later is installed (its bESLCELLLoadBug fix).";

    /// <summary>The outcome of <see cref="Set"/>: the flag as re-read off the written file.</summary>
    public sealed record SetOutcome(bool Success, string? Error, string OutputPath, bool Light, bool Changed, Fit? Fit,
                                    IReadOnlyList<string> Masters, long Bytes)
    {
        public static SetOutcome Fail(string error) => new(false, error, "", false, false, null, Array.Empty<string>(), 0);
    }

    /// <summary>Set (<paramref name="light"/> true) or clear the light flag on the patch at <paramref name="outPath"/>,
    /// opened and written the way an <c>into=</c> write is, so no FormID moves. Refuses to flag a plugin whose own ids
    /// do not fit or that adds a worldspace, writing nothing.</summary>
    public static SetOutcome Set(LoadOrderResolver resolver, string outPath, bool light)
    {
        var fileName = Path.GetFileName(outPath);
        using var session = resolver.OpenSession();
        resolver.Capture();                                                // the build the commit check holds this call to
        SkyrimMod mod;
        try { mod = MidCallChange.Open(outPath, () => SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath))); }
        catch (Exception ex) { return SetOutcome.Fail($"cannot open '{fileName}': {ex.GetType().Name}: {ex.Message}. Nothing was written."); }

        var fit = Assess(mod);
        if (mod.IsSmallMaster == light)
            return new SetOutcome(true, null, outPath, light, false, fit, mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList(),
                                  new FileInfo(outPath).Length);
        if (light && fit.NewWorldspaces.Count > 0)
            return SetOutcome.Fail(
                $"refused: '{fileName}' adds a new worldspace ({string.Join(", ", fit.NewWorldspaces.Take(3))}), and the game loses a "
                + "new worldspace's landscape in a light plugin (Engine Fixes does not fix that), so it stays a full plugin; nothing was written.");
        if (light && fit.Outside > 0)
            return SetOutcome.Fail(
                $"refused: {fit.Outside} of the {fit.Own} records '{fileName}' defines sit outside the light window "
                + $"{Hex(fit.Low)}–{Hex(fit.High)} (the first at {Hex(fit.FirstOutside!.Value)}), and flagging it would mean "
                + "renumbering them, which houseCARL does not do, so it stays a full plugin; nothing was written.");

        var before = OwnKeys(mod);
        mod.IsSmallMaster = light;
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WritePatch(mod, session.AllMastersExcept(fileName), outPath, isFaction: null); }
        catch (Exception ex)
            { return SetOutcome.Fail(WritePatchBuilder.SerializeFailure($"writing '{fileName}' failed (serialize or commit; the existing file is untouched): ", ex, session)); }

        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(outPath));
            if (back.IsSmallMaster != light || !OwnKeys(back).SetEquals(before))
                return SetOutcome.Fail($"'{fileName}' was written, but re-reading it does not show the flag {(light ? "set" : "cleared")} "
                                       + "with every FormID kept; check it in xEdit before enabling it.");
            return new SetOutcome(true, null, outPath, light, true, Assess(back),
                                  back.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList(), new FileInfo(outPath).Length);
        }
        catch (Exception ex) { return SetOutcome.Fail($"'{fileName}' was written but could not be re-opened to confirm it: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }
    }

    static HashSet<FormKey> OwnKeys(ISkyrimModGetter mod)
        => mod.EnumerateMajorRecords().Select(r => r.FormKey).Where(k => k.ModKey == mod.ModKey).ToHashSet();

    /// <summary>The offer for the houseCARL patch at <paramref name="path"/>, read off the file; null when it is light,
    /// does not fit, or cannot be read (the write it follows already succeeded, so a read fault only drops the line).</summary>
    public static string? OfferFor(string path)
    {
        ISkyrimModGetter? mod = null;
        try
        {
            mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
            return mod.IsSmallMaster ? null : Offer(Path.GetFileName(path), Assess(mod));
        }
        catch (Exception) { return null; }
        finally { (mod as IDisposable)?.Dispose(); }
    }
}

/// <summary>A write into a light patch that would put an own id past its window; the message is the whole refusal.</summary>
public sealed class LightWindowExceededException(string message) : InvalidOperationException(message);

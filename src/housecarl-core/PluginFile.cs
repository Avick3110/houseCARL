using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>What a plugin's TES4 header says: its master, light (ESL) and localized flags, its declared masters in order, and HEDR's record count.</summary>
public sealed record PluginHeaderFacts(bool Master, bool Light, bool Localized, IReadOnlyList<string> Masters, uint RecordCount);

/// <summary>The ONE home for the Skyrim plugin filename extensions, matched case-INSENSITIVELY, and the one plugin header reader.</summary>
public static class PluginFile
{
    public static readonly string[] Extensions = { ".esp", ".esm", ".esl" };

    /// <summary>Read the header of the plugin at <paramref name="path"/>; null when the file would not open as a plugin.</summary>
    public static PluginHeaderFacts? ReadHeader(string path)
    {
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
            var h = ov.ModHeader;
            return new PluginHeaderFacts(
                h.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master),
                h.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small),
                h.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Localized),
                h.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(),
                h.Stats.NumRecords);
        }
        catch { return null; }
        finally { if (ov is IDisposable d) { try { d.Dispose(); } catch { } } }
    }
}

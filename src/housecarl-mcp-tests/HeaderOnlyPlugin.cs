using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlMcpTests;

/// <summary>Reads back what a header-only plugin promises: the TES4 signature, and the record count, masters,
/// light-master flag, author and description off a fresh overlay of the written file.</summary>
static class HeaderOnlyPlugin
{
    public static bool IsTes4(string path)
    {
        using var fs = File.OpenRead(path);
        var b = new byte[4];
        return fs.Read(b, 0, 4) == 4 && b[0] == 'T' && b[1] == 'E' && b[2] == 'S' && b[3] == '4';
    }

    public static (int Records, List<string> Masters, bool Esl, string? Author, string? Description) Reopen(string path)
    {
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return (back.EnumerateMajorRecords().Count(),
                back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList(),
                back.IsSmallMaster, back.ModHeader.Author, back.ModHeader.Description);
    }
}

namespace HousecarlCore;

/// <summary>
/// Crash-atomic file commit — the one primitive every houseCARL write funnels its FINAL swap through.
///
/// A complete file is first STAGED into a temp on the SAME volume as its final path (the caller's job), then handed
/// here. <see cref="Commit"/> swaps it into place with NO unlink-then-rename window:
///
///  • target EXISTS  → <c>File.Replace</c> (the Win32 <c>ReplaceFile</c> primitive): an atomic content swap that
///    keeps the destination's on-disk IDENTITY (its NTFS file record). A crash mid-commit leaves either the OLD
///    complete file or the NEW complete file — never a missing or half-written one. This is the crash-ATOMIC
///    guarantee, stronger than the crash-TEAR safety a staged write already buys.
///  • target ABSENT  → <c>File.Move</c> (an atomic rename onto a free name): <c>File.Replace</c> cannot create — it
///    requires an existing target — so the fresh-file case it throws on is served by a rename, itself atomic.
///
/// This REPLACES the product-wide <c>File.Move(overwrite: true)</c> (MoveFileEx MOVEFILE_REPLACE_EXISTING), which the
/// in-place-write-lane review named as not crash-atomic: it can unlink the destination BEFORE the rename commits, and
/// it discards the destination's identity (the result becomes the SOURCE file). Same-volume staging is the caller's
/// invariant — <c>File.Replace</c> THROWS across volumes (a loud, correct refusal) rather than silently degrading to a
/// non-atomic copy (Q3).
///
/// Holds NO handle at rest: it opens nothing it keeps. Proven by the atomic-commit guard, whose overwrite arm is
/// RED-sensitive to a <c>File.Move(overwrite)</c> regression via the destination's PRESERVED creation time
/// (<c>File.Replace</c> keeps the replaced file's creation time; <c>File.Move</c> resets it). The guard self-calibrates:
/// on a host where file-system tunneling would restore the creation time under <c>File.Move</c> too, that one check
/// self-skips with a loud note rather than false-pass (the distinction is unprovable in-process there).
/// </summary>
internal static class AtomicFile
{
    /// <summary>Commit a fully-written <paramref name="stagedPath"/> onto <paramref name="finalPath"/> crash-atomically.
    /// Both MUST be on the same volume. THROWS (never a silent no-op) if the staged file is missing or the swap fails —
    /// the caller reports it (Q3); on any throw the prior <paramref name="finalPath"/>, if it existed, is byte-intact.</summary>
    public static void Commit(string stagedPath, string finalPath)
    {
        try
        {
            File.Replace(stagedPath, finalPath, destinationBackupFileName: null);
        }
        catch (FileNotFoundException)
        {
            // File.Replace requires an existing destination; the fresh-file case (no prior output) it throws on is
            // served by an atomic rename instead. A MISSING SOURCE also surfaces here — File.Move then re-throws,
            // surfacing the real fault loud rather than masking it as a silent no-op.
            File.Move(stagedPath, finalPath);
        }
    }
}

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
/// non-atomic copy (Q3). An EFS-encrypted or specially-ACL'd target can likewise make <c>File.Replace</c> throw a
/// metadata-merge error where <c>File.Move</c> would not — also surfaced loud, original byte-intact; the 3-arg overload
/// is deliberate (the 4-arg <c>ignoreMetadataErrors: true</c> would silently swallow that failure, violating Q3).
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
        // Catch FileNotFoundException ONLY, and deliberately: a cross-volume swap surfaces here as IOException
        // (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2) and, given the same-volume invariant, that's a caller bug we WANT loud
        // with the original retained — widening this catch to IOException would silently degrade it into a non-atomic
        // cross-volume copy (Q3). An EFS/special-ACL merge error likewise surfaces loud here, original byte-intact.
        catch (FileNotFoundException)
        {
            // File.Replace requires an existing destination; the fresh-file case (no prior output) it throws on is
            // served by a rename instead. overwrite:true keeps that branch idempotent against the (mutex-guarded,
            // houseCARL-owned-path) sub-millisecond TOCTOU where the target appears between the probe and here — there
            // is no original to lose on a path just judged fresh. A MISSING SOURCE still throws FileNotFoundException
            // here, so File.Move re-throws it loud rather than masking it as a silent no-op.
            File.Move(stagedPath, finalPath, overwrite: true);
        }
    }
}

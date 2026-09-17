namespace HousecarlCore;

/// <summary>Crash-atomic file commit — the FINAL swap every houseCARL write funnels through; contract in docs/architecture/output-and-artifacts.md.</summary>
public static class AtomicFile
{
    /// <summary>Crash-atomically write <paramref name="bytes"/> to <paramref name="finalPath"/> via a sibling temp; throws on any failure, leaving the prior file byte-intact.</summary>
    public static void WriteAllBytes(string finalPath, byte[] bytes)
    {
        var staged = finalPath + ".houseCARL-tmp";                 // sibling of the target ⇒ same volume ⇒ Commit's invariant holds
        try { if (File.Exists(staged)) File.Delete(staged); } catch { /* a stuck temp surfaces on the write below */ }
        try
        {
            File.WriteAllBytes(staged, bytes);
            Commit(staged, finalPath);
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best-effort: never mask the real failure */ }
            throw;
        }
    }

    /// <summary>Commit a fully-written <paramref name="stagedPath"/> onto <paramref name="finalPath"/>, which must be on the same volume; throws rather than degrading.</summary>
    public static void Commit(string stagedPath, string finalPath)
    {
        try
        {
            File.Replace(stagedPath, finalPath, destinationBackupFileName: null);
        }
        // FileNotFoundException ONLY: every other failure must stay loud — see the note.
        catch (FileNotFoundException)
        {
            // File.Replace cannot create, so the fresh-file case is served by a rename; a missing source still throws.
            File.Move(stagedPath, finalPath, overwrite: true);
        }
    }
}

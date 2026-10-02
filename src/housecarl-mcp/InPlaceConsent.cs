namespace HousecarlMcp;

/// <summary>The in-place consent the writes and assets lanes share: the persistent first-touch handshake over the user
/// config store, the writable-parent pre-flight and the handshake's opening. Contract in docs/architecture/write-path.md.</summary>
internal sealed class InPlaceConsent(UserConfigStore store)
{
    readonly UserConfigStore _store = store;

    /// <summary>Whether an in-place write to <paramref name="path"/> has already been acknowledged.</summary>
    internal bool IsAcknowledged(string path) => _store.IsInPlaceAcknowledged(path);

    /// <summary>PERSIST the in-place acknowledgement for <paramref name="targetPath"/>, called by every in-place lane only
    /// AFTER the write it gated has landed; returns the store's error for the caller's note. Ordering contract in docs/architecture/write-path.md.</summary>
    internal string? Persist(bool owed, string targetPath, string what, string subject = "plugin")
    {
        if (!owed) return null;
        string? err;
        // This runs AFTER the file changed, so the last step of a successful call must not be able to throw.
        try { err = _store.RecordInPlaceAcknowledged(targetPath) is { ok: false, error: var e } ? (e ?? "unknown error") : null; }
        catch (Exception ex) { err = $"{ex.GetType().Name}: {ex.Message}"; }
        return err is null ? null
            : $"the in-place acknowledgement could not be saved ({err}) — the {what} proceeded, " +
              $"but the next in-place call will ask for this {subject} again.";
    }

    /// <summary>Writable-parent pre-flight for the in-place swap, probed with a sibling temp; true, with a named <paramref name="why"/>, means refuse.</summary>
    internal static bool ParentUnwritable(string targetPath, out string why)
    {
        why = "";
        var dir = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            why = $"in-place refused: the target's parent folder '{dir}' does not exist — nothing written.";
            return true;
        }
        try
        {
            var probe = Path.Combine(dir, ".housecarl-writeprobe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return false;
        }
        catch (Exception ex)
        {
            why = $"in-place refused: the target's folder '{dir}' is not writable ({ex.GetType().Name}: {ex.Message}) — houseCARL " +
                  "won't degrade to a non-atomic write. Make the mod folder writable (or move the plugin somewhere writable) and retry. Nothing written.";
            return true;
        }
    }

    /// <summary>The opening claims both first-touch prompts make: the prompt is shown until an in-place write LANDS,
    /// and the file claim is direction-neutral. Contract in docs/architecture/write-path.md.</summary>
    internal static string HandshakeLead(string name, string path, string subject, string verb) =>
        $"in-place edit of '{name}' — first-time confirmation (shown until an in-place write to this {subject} LANDS; " +
        "a call that is refused records nothing, so you may see this again):\n" +
        $"  • This {verb} your ORIGINAL file ({path}) — not a copy. houseCARL keeps NO backup or undo and cannot " +
        "restore what it overwrites, so keep your own.\n";
}

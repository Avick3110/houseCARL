// The file-lock harness (#486 PR 1, item 2). Mechanism ported from
// src/housecarl-generator/DialogueInfoOrderProbe.cs's UNREAD-WIRED / DEFINER-LOCK-LOUD / WINNER-LOCK-LOUD
// arms, which each open a plugin FileShare.None, drive a product path across the lock, and assert the
// product faulted LOUDLY rather than reporting a clean-looking result. The test project had nothing for
// that before this file.

namespace HousecarlMcpTests;

/// <summary>
/// Holds one file open with <see cref="FileShare.None"/> for the lifetime of the object, and releases it on
/// dispose — MO2 or xEdit sitting on a plugin, which is the scenario houseCARL's no-handles-at-rest design
/// explicitly invites. A read attempted while the hold is alive cannot open the file.
///
/// <para><b>Acquisition failure is loud, by construction.</b> Each of the three probe arms this is ported
/// from wraps its own <c>new FileStream(...)</c> in a try/catch whose catch marks the arm FAILED — because
/// an arm that could not take the lock has not driven the path it names, and a swallowed acquisition would
/// leave it asserting nothing at all while still reporting a pass. <see cref="Hold"/> throws a message that
/// names the path and the underlying reason, so that failure mode cannot be reached by omission: there is no
/// "returns null and the caller moved on" branch to forget to check.</para>
/// </summary>
public sealed class HeldOpen : IDisposable
{
    readonly FileStream _stream;

    /// <summary>The file being held.</summary>
    public string Path { get; }

    HeldOpen(string path, FileStream stream) { Path = path; _stream = stream; }

    /// <summary>Take an exclusive hold on <paramref name="path"/>. Throws — naming the path and the reason —
    /// if the hold cannot be taken, including when the path does not exist.</summary>
    public static HeldOpen Hold(string path)
    {
        try
        {
            return new HeldOpen(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"could not take an exclusive hold on '{path}' to simulate a locked file — " +
                $"{ex.GetType().Name}: {ex.Message}. Nothing was locked, so a test continuing past this " +
                "point would assert against an ordinary readable file.", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}

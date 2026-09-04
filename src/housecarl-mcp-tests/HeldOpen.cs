namespace HousecarlMcpTests;

/// <summary>Holds one file open with <see cref="FileShare.None"/> for the lifetime of the object — MO2 or xEdit
/// sitting on a plugin. Details: <c>docs/architecture/test-project-fixtures.md</c>.</summary>
public sealed class HeldOpen : IDisposable
{
    readonly FileStream _stream;

    HeldOpen(FileStream stream) => _stream = stream;

    /// <summary>Take an exclusive hold on <paramref name="path"/>. Throws — naming the path and the reason —
    /// if the hold cannot be taken, so no caller can proceed lockless past a swallowed failure.</summary>
    public static HeldOpen Hold(string path)
    {
        try
        {
            return new HeldOpen(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None));
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

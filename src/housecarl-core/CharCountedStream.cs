namespace HousecarlCore;

/// <summary>The buffer a json response is written into, counting CHARACTERS not bytes — the unit <c>max_chars</c>
/// names; contract in docs/architecture/render-budget.md.</summary>
public sealed class CharCountedStream : MemoryStream
{
    /// <summary>What has reached the stream so far, in characters; a writer still holding bytes must be flushed first.</summary>
    public int Chars { get; private set; }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Chars += CountOf(buffer.AsSpan(offset, count));
        base.Write(buffer, offset, count);
    }

    public override void WriteByte(byte value)
    {
        Chars += CountOf(stackalloc byte[] { value });
        base.WriteByte(value);
    }

    /// <summary>UTF-8 to UTF-16 code units without decoding — the one conversion in the server.</summary>
    public static int CountOf(ReadOnlySpan<byte> utf8)
    {
        int chars = 0;
        foreach (byte b in utf8)
        {
            if ((b & 0xC0) != 0x80) chars++;
            if (b >= 0xF0) chars++;
        }
        return chars;
    }
}

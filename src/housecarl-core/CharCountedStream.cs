namespace HousecarlCore;

/// <summary>The buffer a json response is written into, which also knows how long that response is in the unit
/// <c>max_chars</c> names: CHARACTERS, not bytes.
///
/// <para>A json document is written as UTF-8, so the stream's own <c>Length</c> is a byte count, and a byte count
/// is not what the caller capped or what the response's overrun notice states back. The two agree only while every
/// character is ASCII — which held exactly as long as the writer escaped non-ASCII to <c>\uXXXX</c>, and stopped
/// the moment it did not (#754). Counting here, as the bytes go past, is the one place the whole json lane takes
/// its length from, so no site can measure one unit and state the other.</para>
///
/// <para>The count is UTF-16 code units, which is what <c>string.Length</c> returns for the finished response and
/// what the text lane budgets its StringBuilder against — so both transports cap the same quantity. It is kept
/// incrementally rather than rescanned, because a per-row budget test asks for it once a row.</para></summary>
public sealed class CharCountedStream : MemoryStream
{
    /// <summary>What has been written so far, in characters. Only what has reached the stream — a writer that
    /// still holds bytes must be flushed first, exactly as a <c>Length</c> read would have to be.</summary>
    public int Chars { get; private set; }

    /// <summary>The one write every other one arrives through: <c>MemoryStream</c>'s span overload hands a DERIVED
    /// type's writes to the base <c>Stream</c>, which copies into an array and calls this — so counting the span
    /// overload too would count those bytes twice.</summary>
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

    /// <summary>UTF-8 to UTF-16 code units without decoding: a character starts at every byte that is not a
    /// continuation byte, and a character outside the BMP arrives as a four-byte sequence that <c>string.Length</c>
    /// counts as the two halves of its surrogate pair. The one conversion in the server, so a buffer counted as it
    /// was written and one measured afterwards cannot disagree.</summary>
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

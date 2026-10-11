namespace HousecarlCore;

/// <summary>Whether a fault reading a record is a PARSE of its content: Mutagen's own exceptions and the
/// argument/format failures its lazy span reads raise. Anything else, a cancellation and running out of memory
/// above all, is the CALL's and goes on up.</summary>
public static class RecordParseFault
{
    public static bool Is(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is OperationCanceledException or OutOfMemoryException) return false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Namespace is { } ns && ns.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return true;
            if (e is ArgumentException or FormatException or IndexOutOfRangeException or OverflowException or InvalidCastException) return true;
        }
        return false;
    }
}

namespace HousecarlCore;

/// <summary>The record classes one index build saw, each interned as a one-byte code with the seek type a fetch walks by;
/// learned from Mutagen's own parse while the build holds every record, so nothing here is hand-written.</summary>
internal sealed class IndexTypes
{
    /// <summary>The code a key carries when its providers differ in seek type; their own codes sit beside the overriders.</summary>
    public const byte Mixed = byte.MaxValue;

    readonly List<Type> _class = new();
    readonly List<Type> _seek = new();
    readonly Dictionary<Type, byte> _code = new();
    Type? _last;
    byte _lastCode;

    /// <summary>The code for a record class, added on first sight; records arrive grouped by type, so the last one usually answers.</summary>
    public byte Intern(Type cls)
    {
        if (ReferenceEquals(cls, _last)) return _lastCode;
        if (!_code.TryGetValue(cls, out var c))
        {
            _code[cls] = c = (byte)_class.Count;
            _class.Add(cls);
            _seek.Add(WriteEngine.SeekTypeFor(cls));
        }
        _last = cls;
        return _lastCode = c;
    }

    public Type Class(byte code) => _class[code];

    public Type Seek(byte code) => _seek[code];
}

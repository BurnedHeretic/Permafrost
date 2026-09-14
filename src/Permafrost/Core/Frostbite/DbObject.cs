namespace Permafrost.Core.Frostbite;

public enum DbValueKind
{
    Invalid,
    List,
    Object,
    Boolean,
    String,
    Int,
    Long,
    Float,
    Double,
    Guid,
    Sha1,
    ByteArray
}

public sealed class DbValue
{
    private DbValue(DbValueKind kind, object? value)
    {
        Kind = kind;
        Value = value;
    }

    public DbValueKind Kind { get; }
    public object? Value { get; }

    public static DbValue Invalid() => new(DbValueKind.Invalid, null);
    public static DbValue From(DbValueKind kind, object? value) => new(kind, value);

    public IReadOnlyDictionary<string, DbValue> AsObject() =>
        Value as IReadOnlyDictionary<string, DbValue>
        ?? throw new InvalidDataException($"DB value is {Kind}, not Object.");

    public IReadOnlyList<DbValue> AsList() =>
        Value as IReadOnlyList<DbValue>
        ?? throw new InvalidDataException($"DB value is {Kind}, not List.");

    public string AsString() => Value as string
        ?? throw new InvalidDataException($"DB value is {Kind}, not String.");

    public uint AsUInt32() => Value is uint value
        ? value
        : throw new InvalidDataException($"DB value is {Kind}, not Int.");

    public bool TryGet(string key, out DbValue value)
    {
        if (Value is IReadOnlyDictionary<string, DbValue> map && map.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }
        value = Invalid();
        return false;
    }

    public DbValue Require(string key)
    {
        if (TryGet(key, out var value)) return value;
        throw new InvalidDataException($"DB object is missing required key '{key}'.");
    }
}

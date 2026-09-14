namespace Permafrost.Core.Frostbite;

/// <summary>
/// Reader for Frostbite's compact DB object format used by layout.toc.
/// Battlefront II's layout DB begins after the 0x22C Frostbite file preamble.
/// </summary>
public static class DbObjectReader
{
    private const byte TypeInvalid = 0;
    private const byte TypeList = 1;
    private const byte TypeObject = 2;
    private const byte TypeBoolean = 6;
    private const byte TypeString = 7;
    private const byte TypeInt = 8;
    private const byte TypeLong = 9;
    private const byte TypeFloat = 11;
    private const byte TypeDouble = 12;
    private const byte TypeGuid = 15;
    private const byte TypeSha1 = 16;
    private const byte TypeByteArray = 19;

    public static DbValue ReadLayoutRoot(string layoutPath)
    {
        using var file = File.OpenRead(layoutPath);
        if (file.Length <= 0x22C) throw new InvalidDataException("layout.toc is too small.");
        file.Position = 0x22C;
        using var reader = new BinaryReader(file);
        var result = ReadNamedValue(reader);
        if (result == null) throw new InvalidDataException("layout.toc contains no root DB object.");
        return result.Value.Value;
    }

    public static (string Name, DbValue Value)? ReadNamedValue(BinaryReader reader)
    {
        var tag = reader.ReadByte();
        var type = (byte)(tag & 0x1F);
        if (type == TypeInvalid) return null;

        var name = (tag & 0x80) == 0 ? reader.ReadNullTerminatedUtf8() : string.Empty;
        DbValue value;

        switch (type)
        {
            case TypeList:
            {
                var size = checked((long)reader.Read7BitEncodedUInt64Compat());
                var end = checked(reader.BaseStream.Position + size);
                var list = new List<DbValue>();
                while (reader.BaseStream.Position < end)
                {
                    var child = ReadNamedValue(reader);
                    if (child == null) break;
                    list.Add(child.Value.Value);
                }
                value = DbValue.From(DbValueKind.List, list);
                break;
            }
            case TypeObject:
            {
                var size = checked((long)reader.Read7BitEncodedUInt64Compat());
                var end = checked(reader.BaseStream.Position + size);
                var map = new Dictionary<string, DbValue>(StringComparer.Ordinal);
                while (reader.BaseStream.Position < end)
                {
                    var child = ReadNamedValue(reader);
                    if (child == null) break;
                    map[child.Value.Name] = child.Value.Value;
                }
                value = DbValue.From(DbValueKind.Object, map);
                break;
            }
            case TypeBoolean:
                value = DbValue.From(DbValueKind.Boolean, reader.ReadByte() == 1);
                break;
            case TypeString:
            {
                var length = checked((int)reader.Read7BitEncodedUInt64Compat());
                value = DbValue.From(DbValueKind.String, reader.ReadSizedUtf8(length));
                break;
            }
            case TypeInt:
                value = DbValue.From(DbValueKind.Int, reader.ReadUInt32());
                break;
            case TypeLong:
                value = DbValue.From(DbValueKind.Long, reader.ReadInt64());
                break;
            case TypeFloat:
                value = DbValue.From(DbValueKind.Float, reader.ReadSingle());
                break;
            case TypeDouble:
                value = DbValue.From(DbValueKind.Double, reader.ReadDouble());
                break;
            case TypeGuid:
                value = DbValue.From(DbValueKind.Guid, new Guid(reader.ReadBytes(16)));
                break;
            case TypeSha1:
                value = DbValue.From(DbValueKind.Sha1, Convert.ToHexString(reader.ReadBytes(20)));
                break;
            case TypeByteArray:
            {
                var length = checked((int)reader.Read7BitEncodedUInt64Compat());
                value = DbValue.From(DbValueKind.ByteArray, reader.ReadBytes(length));
                break;
            }
            default:
                throw new InvalidDataException($"Unsupported Frostbite DB object type {type} at 0x{reader.BaseStream.Position - 1:X}.");
        }

        return (name, value);
    }
}

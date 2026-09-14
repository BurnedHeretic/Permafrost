using System.Text;

namespace Permafrost.Core.Ebx;

/// <summary>
/// Small, standalone EBX v4 reader focused on Battlefront II level data.
/// It reads the self-describing type table embedded in exported EBX files, so it does not
/// require Frosty's generated SDK classes. This is intentionally a read-first implementation;
/// fixed-size Float32 fields can also be patched in-place through EbxDocument.
/// </summary>
public sealed class EbxV4Reader
{
    private const uint Version2 = 0x0FB2D1CE;
    private const uint Version4 = 0x0FB4D1CE;

    private readonly EbxDocument _document;
    private readonly BinaryReader _reader;
    private readonly List<(uint Offset, ushort ClassRef, ushort Type)> _boxedValues = new();
    private long _boxedValuesOffset;
    private uint _boxedValuesCount;

    private EbxV4Reader(byte[] data)
    {
        _document = new EbxDocument(data);
        _reader = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8, leaveOpen: false);
    }

    public static EbxDocument Read(string path)
    {
        var instance = new EbxV4Reader(File.ReadAllBytes(path));
        instance.ReadHeaderAndObjects();
        instance._document.SourcePath = path;
        instance._document.AssetName = Path.GetFileNameWithoutExtension(path);
        return instance._document;
    }

    public static EbxDocument Read(byte[] data, string? assetName = null)
    {
        var instance = new EbxV4Reader((byte[])data.Clone());
        instance.ReadHeaderAndObjects();
        instance._document.AssetName = assetName;
        return instance._document;
    }

    public static bool TryReadFileGuid(ReadOnlySpan<byte> data, out Guid guid)
    {
        guid = Guid.Empty;
        if (data.Length < 56) return false;
        var magic = BitConverter.ToUInt32(data[..4]);
        if (magic != Version2 && magic != Version4) return false;
        guid = new Guid(data.Slice(40, 16));
        return guid != Guid.Empty;
    }

    private void ReadHeaderAndObjects()
    {
        var magic = _reader.ReadUInt32();
        if (magic != Version2 && magic != Version4)
            throw new InvalidDataException($"Not a supported EBX v2/v4 file. Magic: 0x{magic:X8}");

        _document.Magic = magic;
        var stringsOffset = _reader.ReadUInt32();
        _ = _reader.ReadUInt32(); // strings + data length
        var guidCount = _reader.ReadUInt32();
        var instanceCount = _reader.ReadUInt16();
        var exportedCount = _reader.ReadUInt16();
        _ = _reader.ReadUInt16(); // unique class count
        var classTypeCount = _reader.ReadUInt16();
        var fieldTypeCount = _reader.ReadUInt16();
        var typeNamesLength = _reader.ReadUInt16();
        var stringsLength = _reader.ReadUInt32();
        var arrayCount = _reader.ReadUInt32();
        var dataLength = _reader.ReadUInt32();

        _document.StringsOffset = stringsOffset;
        _document.StringsLength = stringsLength;
        _document.ArraysOffset = stringsOffset + stringsLength + dataLength;
        _document.FileGuid = ReadGuid();

        if (magic == Version4)
        {
            _boxedValuesCount = _reader.ReadUInt32();
            _boxedValuesOffset = _reader.ReadUInt32() + stringsOffset + stringsLength;
        }
        else
        {
            Align(16);
        }

        for (var i = 0; i < guidCount; i++)
            _document.Imports.Add(new EbxImportReference(ReadGuid(), ReadGuid()));

        var typeNames = new Dictionary<int, string>();
        var typeNamesStart = Position;
        while (Position - typeNamesStart < typeNamesLength)
        {
            var text = ReadNullTerminatedString();
            typeNames.TryAdd(HashString(text), text);
        }

        for (var i = 0; i < fieldTypeCount; i++)
        {
            var nameHash = _reader.ReadInt32();
            var type = _reader.ReadUInt16();
            if (magic == Version4) type >>= 1;

            _document.Fields.Add(new EbxFieldDescriptor
            {
                Name = typeNames.TryGetValue(nameHash, out var name) ? name : $"0x{unchecked((uint)nameHash):X8}",
                Type = type,
                ClassRef = _reader.ReadUInt16(),
                DataOffset = _reader.ReadUInt32(),
                SecondOffset = _reader.ReadUInt32()
            });
        }

        for (var i = 0; i < classTypeCount; i++)
        {
            var nameHash = _reader.ReadInt32();
            var fieldIndex = _reader.ReadInt32();
            var fieldCount = _reader.ReadByte();
            var alignment = _reader.ReadByte();
            var type = _reader.ReadUInt16();
            if (magic == Version4) type >>= 1;

            _document.Classes.Add(new EbxClassDescriptor
            {
                Name = typeNames.TryGetValue(nameHash, out var name) ? name : $"0x{unchecked((uint)nameHash):X8}",
                FieldIndex = fieldIndex,
                FieldCount = fieldCount,
                Alignment = alignment,
                Type = type,
                Size = _reader.ReadUInt16(),
                SecondSize = _reader.ReadUInt16()
            });
        }

        var exportsRemaining = exportedCount;
        for (var i = 0; i < instanceCount; i++)
        {
            var classRef = _reader.ReadUInt16();
            var count = _reader.ReadUInt16();
            var exported = exportsRemaining != 0;
            if (exported) exportsRemaining--;
            _document.Instances.Add(new EbxInstanceDescriptor(classRef, count, exported));
        }

        Align(16);
        for (var i = 0; i < arrayCount; i++)
            _document.Arrays.Add(new EbxArrayDescriptor(_reader.ReadUInt32(), _reader.ReadUInt32(), _reader.ReadInt32()));

        Align(16);
        for (var i = 0; i < _boxedValuesCount; i++)
            _boxedValues.Add((_reader.ReadUInt32(), _reader.ReadUInt16(), _reader.ReadUInt16()));

        // The object data begins immediately after the string section.
        Position = stringsOffset + stringsLength;
        ReadObjects();
    }

    private void ReadObjects()
    {
        foreach (var instance in _document.Instances)
        {
            var descriptor = _document.Classes[instance.ClassRef];
            for (var i = 0; i < instance.Count; i++)
                _document.Objects.Add(new EbxObject(descriptor));
        }

        var objectIndex = 0;
        foreach (var instance in _document.Instances)
        {
            var descriptor = _document.Classes[instance.ClassRef];
            for (var i = 0; i < instance.Count; i++)
            {
                Align(descriptor.Alignment);
                var instanceGuid = instance.IsExported ? ReadGuid() : Guid.Empty;
                if (descriptor.Alignment != 4)
                    Position += 8;

                var obj = _document.Objects[objectIndex];
                obj.InstanceGuid = instanceGuid;
                obj.ObjectIndex = objectIndex;
                objectIndex++;

                ReadClass(descriptor, obj);
            }
        }
    }

    private void ReadClass(EbxClassDescriptor classType, EbxObject obj)
    {
        for (var j = 0; j < classType.FieldCount; j++)
        {
            var field = _document.Fields[classType.FieldIndex + j];
            if (field.DebugType == EbxFieldType.Inherited)
            {
                ReadClass(_document.Classes[field.ClassRef], obj);
                continue;
            }

            AlignForField(field.DebugType);

            if (field.DebugType == EbxFieldType.Array)
            {
                var arrayClass = _document.Classes[field.ClassRef];
                var arrayIndex = _reader.ReadInt32();
                if (arrayIndex < 0 || arrayIndex >= _document.Arrays.Count)
                    throw new InvalidDataException($"Invalid EBX array index {arrayIndex} in {classType.Name}.{field.Name}");

                var array = _document.Arrays[arrayIndex];
                var returnPosition = Position;
                Position = _document.ArraysOffset + array.Offset;

                var values = new List<object?>();
                var elementField = _document.Fields[arrayClass.FieldIndex];
                for (var i = 0; i < array.Count; i++)
                    values.Add(ReadField(arrayClass, elementField.DebugType, elementField.ClassRef));

                Position = returnPosition;
                obj.Fields[field.Name] = values;
                continue;
            }

            var valueStart = Position;
            var value = ReadField(classType, field.DebugType, field.ClassRef);
            obj.Fields[field.Name] = value;

            var length = checked((int)(Position - valueStart));
            if (IsPatchablePrimitive(field.DebugType))
                obj.FieldLocations[field.Name] = new EbxFieldLocation(valueStart, field.DebugType, length);
        }

        Align(classType.Alignment);
    }

    private object? ReadField(EbxClassDescriptor parentClass, EbxFieldType type, ushort classRef)
    {
        switch (type)
        {
            case EbxFieldType.Boolean: return _reader.ReadByte() != 0;
            case EbxFieldType.Int8: return _reader.ReadSByte();
            case EbxFieldType.UInt8: return _reader.ReadByte();
            case EbxFieldType.Int16: return _reader.ReadInt16();
            case EbxFieldType.UInt16: return _reader.ReadUInt16();
            case EbxFieldType.Int32: return _reader.ReadInt32();
            case EbxFieldType.UInt32: return _reader.ReadUInt32();
            case EbxFieldType.Int64: return _reader.ReadInt64();
            case EbxFieldType.UInt64: return _reader.ReadUInt64();
            case EbxFieldType.Float32: return _reader.ReadSingle();
            case EbxFieldType.Float64: return _reader.ReadDouble();
            case EbxFieldType.Guid: return ReadGuid();
            case EbxFieldType.ResourceRef: return _reader.ReadUInt64();
            case EbxFieldType.Sha1: return Convert.ToHexString(_reader.ReadBytes(20));
            case EbxFieldType.String: return ReadFixedString(32);
            case EbxFieldType.CString: return ReadString(_reader.ReadUInt32());
            case EbxFieldType.FileRef:
            {
                var offset = _reader.ReadUInt32();
                Position += 4;
                return ReadString(offset);
            }
            case EbxFieldType.TypeRef:
            {
                var offset = _reader.ReadUInt32();
                Position += 4;
                return ReadString(offset);
            }
            case EbxFieldType.BoxedValueRef:
                return _reader.ReadUInt64();
            case EbxFieldType.Struct:
            {
                var descriptor = _document.Classes[classRef];
                Align(descriptor.Alignment);
                var value = new EbxObject(descriptor, isStruct: true);
                ReadClass(descriptor, value);
                return value;
            }
            case EbxFieldType.Enum:
                return _reader.ReadInt32();
            case EbxFieldType.Pointer:
            {
                var index = _reader.ReadUInt32();
                if ((index >> 31) == 1)
                {
                    var importIndex = checked((int)(index & 0x7FFFFFFF));
                    if (importIndex < 0 || importIndex >= _document.Imports.Count)
                        throw new InvalidDataException($"Invalid external EBX import index {importIndex}.");
                    return new EbxPointer
                    {
                        Kind = EbxPointerKind.External,
                        ImportIndex = importIndex,
                        External = _document.Imports[importIndex]
                    };
                }

                if (index == 0)
                    return new EbxPointer { Kind = EbxPointerKind.Null };

                return new EbxPointer
                {
                    Kind = EbxPointerKind.Internal,
                    InternalObjectIndex = checked((int)index - 1)
                };
            }
            case EbxFieldType.DbObject:
                throw new InvalidDataException("DbObject fields are not currently supported by the standalone level reader.");
            default:
                throw new InvalidDataException($"Unsupported EBX field type {type} in {parentClass.Name}.");
        }
    }

    private void AlignForField(EbxFieldType type)
    {
        switch (type)
        {
            case EbxFieldType.ResourceRef:
            case EbxFieldType.TypeRef:
            case EbxFieldType.FileRef:
            case EbxFieldType.BoxedValueRef:
            case EbxFieldType.UInt64:
            case EbxFieldType.Int64:
            case EbxFieldType.Float64:
                Align(8);
                break;
            case EbxFieldType.Array:
            case EbxFieldType.Pointer:
                Align(4);
                break;
        }
    }

    private static bool IsPatchablePrimitive(EbxFieldType type) => type is
        EbxFieldType.Boolean or EbxFieldType.Int8 or EbxFieldType.UInt8 or EbxFieldType.Int16 or
        EbxFieldType.UInt16 or EbxFieldType.Int32 or EbxFieldType.UInt32 or EbxFieldType.Int64 or
        EbxFieldType.UInt64 or EbxFieldType.Float32 or EbxFieldType.Float64;

    private string ReadString(uint offset)
    {
        if (offset == 0xFFFFFFFF)
            return string.Empty;

        var old = Position;
        Position = _document.StringsOffset + offset;
        var value = ReadNullTerminatedString();
        Position = old;
        return value;
    }

    private string ReadFixedString(int bytes)
    {
        var raw = _reader.ReadBytes(bytes);
        var zero = Array.IndexOf(raw, (byte)0);
        if (zero < 0) zero = raw.Length;
        return Encoding.UTF8.GetString(raw, 0, zero);
    }

    private string ReadNullTerminatedString()
    {
        using var ms = new MemoryStream();
        byte b;
        while ((b = _reader.ReadByte()) != 0)
            ms.WriteByte(b);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private Guid ReadGuid() => new(_reader.ReadBytes(16));

    private void Align(int alignment)
    {
        if (alignment <= 1) return;
        while ((Position % alignment) != 0)
            Position++;
    }

    private long Position
    {
        get => _reader.BaseStream.Position;
        set => _reader.BaseStream.Position = value;
    }

    private static int HashString(string text)
    {
        unchecked
        {
            var hash = 5381;
            foreach (var ch in text)
                hash = (hash * 33) ^ ch;
            return hash;
        }
    }
}

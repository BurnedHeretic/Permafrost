using System.Text;

namespace Permafrost.Core.Ebx;

/// <summary>
/// Minimal self-describing EBX v4 serializer used by Permafrost for structural level edits.
/// It intentionally reuses the class/field descriptors embedded in the retail EBX instead of
/// depending on Frosty's generated SDK. This makes append-only placement imports possible while
/// keeping the retail install read-only.
/// </summary>
public static class EbxV4Writer
{
    private const uint Version4 = 0x0FB4D1CE;

    public static byte[] Write(EbxDocument document)
    {
        if (document.Magic != Version4)
            throw new NotSupportedException("Structural EBX writing is currently enabled only for Battlefront II EBX v4 documents.");
        if (document.BoxedValueCount != 0)
            throw new NotSupportedException("Structural writing of EBX boxed values is not enabled yet. Choose a different LayerData target.");
        if (document.Objects.Count == 0)
            throw new InvalidDataException("Cannot write an EBX with no objects.");

        var context = new WriterContext(document);
        var objectData = context.BuildObjectData();
        var arrayData = context.BuildArrayData();
        var instances = BuildInstances(document);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        // Header (v4 = 64 bytes). Offsets/lengths patched once the body is complete.
        writer.Write(Version4);
        writer.Write(0u); // stringsOffset
        writer.Write(0u); // strings + data + arrays length
        writer.Write((uint)document.Imports.Count);
        writer.Write((ushort)instances.Count);
        writer.Write((ushort)instances.Count(x => x.Exported));
        writer.Write((ushort)document.Objects.Select(x => context.GetClassRef(x.Descriptor)).Distinct().Count());
        writer.Write((ushort)document.Classes.Count);
        writer.Write((ushort)document.Fields.Count);
        writer.Write((ushort)0); // typeNamesLen
        writer.Write(0u); // stringsLen
        writer.Write((uint)context.Arrays.Count);
        writer.Write(0u); // dataLen
        WriteGuid(writer, document.FileGuid);
        writer.Write(0u); // boxed count
        writer.Write(0u); // boxed offset

        foreach (var import in document.Imports)
        {
            WriteGuid(writer, import.FileGuid);
            WriteGuid(writer, import.ClassGuid);
        }

        Align(writer, 16);
        var typeNamesStart = writer.BaseStream.Position;
        foreach (var name in BuildTypeNames(document))
            WriteNullTerminatedUtf8(writer, name);
        Align(writer, 16);
        var typeNamesLength = checked((ushort)(writer.BaseStream.Position - typeNamesStart));

        foreach (var field in document.Fields)
        {
            writer.Write(field.NameHash);
            writer.Write((ushort)(field.Type << 1));
            writer.Write(field.ClassRef);
            writer.Write(field.DataOffset);
            writer.Write(field.SecondOffset);
        }

        foreach (var cls in document.Classes)
        {
            writer.Write(cls.NameHash);
            writer.Write(cls.FieldIndex);
            writer.Write(cls.FieldCount);
            writer.Write(cls.Alignment);
            writer.Write((ushort)(cls.Type << 1));
            writer.Write(cls.Size);
            writer.Write(cls.SecondSize);
        }

        foreach (var instance in instances)
        {
            writer.Write(instance.ClassRef);
            writer.Write(instance.Count);
        }
        Align(writer, 16);

        var arrayDescriptorOffset = writer.BaseStream.Position;
        foreach (var _ in context.Arrays)
        {
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0);
        }
        Align(writer, 16);

        // Boxed-value descriptors are intentionally absent (count == 0).
        Align(writer, 16);
        var stringsOffset = checked((uint)writer.BaseStream.Position);

        foreach (var value in context.Strings.Values)
            WriteNullTerminatedUtf8(writer, value);
        Align(writer, 16);
        var stringsLength = checked((uint)(writer.BaseStream.Position - stringsOffset));

        var dataStart = writer.BaseStream.Position;
        writer.Write(objectData);
        writer.Write((byte)0);
        Align(writer, 16);
        var dataLength = checked((uint)(writer.BaseStream.Position - dataStart));

        writer.Write(arrayData);
        Align(writer, 16);
        var stringsAndDataLength = checked((uint)(writer.BaseStream.Position - stringsOffset));

        // Patch header.
        writer.BaseStream.Position = 4;
        writer.Write(stringsOffset);
        writer.Write(stringsAndDataLength);
        writer.BaseStream.Position = 0x1A;
        writer.Write(typeNamesLength);
        writer.Write(stringsLength);
        writer.BaseStream.Position = 0x24;
        writer.Write(dataLength);

        // Patch array descriptors. Offsets are relative to stringsOffset+stringsLength+dataLength.
        writer.BaseStream.Position = arrayDescriptorOffset;
        foreach (var array in context.Arrays)
        {
            writer.Write(array.Offset);
            writer.Write(array.Count);
            writer.Write(array.ClassRef);
        }

        writer.BaseStream.Position = stream.Length;
        return stream.ToArray();
    }

    private static List<InstanceGroup> BuildInstances(EbxDocument document)
    {
        var result = new List<InstanceGroup>();
        var sawNonExported = false;
        foreach (var obj in document.Objects)
        {
            var exported = obj.InstanceGuid != Guid.Empty;
            if (!exported) sawNonExported = true;
            else if (sawNonExported)
                throw new InvalidDataException("EBX exported objects must precede non-exported objects for v4 serialization.");

            var classIndex = document.Classes.IndexOf(obj.Descriptor);
            if (classIndex < 0 || classIndex > ushort.MaxValue)
                throw new InvalidDataException($"Object {obj} references a class descriptor outside this EBX.");
            var classRef = checked((ushort)classIndex);

            if (result.Count > 0 && result[^1].ClassRef == classRef && result[^1].Exported == exported && result[^1].Count < ushort.MaxValue)
            {
                var last = result[^1];
                result[^1] = last with { Count = checked((ushort)(last.Count + 1)) };
            }
            else
            {
                result.Add(new InstanceGroup(classRef, 1, exported));
            }
        }
        return result;
    }

    private static IEnumerable<string> BuildTypeNames(EbxDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in document.Fields.Select(x => x.Name).Concat(document.Classes.Select(x => x.Name)))
        {
            if (string.IsNullOrEmpty(name) || IsSyntheticHashName(name)) continue;
            if (seen.Add(name)) yield return name;
        }
    }

    private static bool IsSyntheticHashName(string name) =>
        name.Length == 10 && name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(2).ToString().All(Uri.IsHexDigit);

    private static void WriteGuid(BinaryWriter writer, Guid guid) => writer.Write(guid.ToByteArray());

    private static void WriteNullTerminatedUtf8(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void Align(BinaryWriter writer, int alignment)
    {
        if (alignment <= 1) return;
        while ((writer.BaseStream.Position % alignment) != 0)
            writer.Write((byte)0);
    }

    private sealed record InstanceGroup(ushort ClassRef, ushort Count, bool Exported);

    private sealed class WriterContext
    {
        private readonly EbxDocument _document;
        private readonly Dictionary<EbxClassDescriptor, int> _classRefs;

        public WriterContext(EbxDocument document)
        {
            _document = document;
            _classRefs = new Dictionary<EbxClassDescriptor, int>();
            for (var index = 0; index < document.Classes.Count; index++)
                _classRefs[document.Classes[index]] = index;
            Arrays.Add(new PendingArray { Offset = 0, Count = 0, ClassRef = 0, Values = Array.Empty<object?>() });
        }

        public StringTable Strings { get; } = new();
        public List<PendingArray> Arrays { get; } = new();

        public int GetClassRef(EbxClassDescriptor descriptor) =>
            _classRefs.TryGetValue(descriptor, out var value)
                ? value
                : throw new InvalidDataException($"Class descriptor '{descriptor.Name}' is not part of this EBX.");

        public byte[] BuildObjectData()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            foreach (var obj in _document.Objects)
            {
                var cls = obj.Descriptor;
                Align(writer, cls.Alignment);
                if (obj.InstanceGuid != Guid.Empty)
                    WriteGuid(writer, obj.InstanceGuid);
                if (cls.Alignment != 4)
                    writer.Write(0UL);
                WriteClass(writer, obj, cls);
            }
            return stream.ToArray();
        }

        public byte[] BuildArrayData()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

            // Index 0 is the conventional empty-array sentinel and has no payload. Frosty's
            // writer builds each array payload in its own stream (position 0), then prefixes it
            // with a count word in the global array block. That distinction matters for member
            // types with 8/16-byte alignment: aligning against the global position after the
            // 4-byte count would insert padding the EBX reader does not expect.
            for (var arrayIndex = 1; arrayIndex < Arrays.Count; arrayIndex++)
            {
                var array = Arrays[arrayIndex];
                var arrayClass = _document.Classes[array.ClassRef];
                if (arrayClass.FieldCount == 0)
                    throw new InvalidDataException($"Array class {array.ClassRef} has no member field.");
                var member = _document.Fields[arrayClass.FieldIndex];

                using var payloadStream = new MemoryStream();
                using (var payloadWriter = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
                {
                    foreach (var value in array.Values)
                        WriteField(payloadWriter, member, value);
                }

                writer.Write(array.Count);
                array.Offset = checked((uint)writer.BaseStream.Position);
                writer.Write(payloadStream.ToArray());
                Align(writer, 16);
            }
            return stream.ToArray();
        }

        private void WriteClass(BinaryWriter writer, EbxObject obj, EbxClassDescriptor cls)
        {
            for (var i = 0; i < cls.FieldCount; i++)
            {
                var field = _document.Fields[cls.FieldIndex + i];
                if (field.DebugType == EbxFieldType.Inherited)
                {
                    WriteClass(writer, obj, _document.Classes[field.ClassRef]);
                    continue;
                }

                if (!obj.Fields.TryGetValue(field.Name, out var value))
                    throw new InvalidDataException($"{obj.ClassName} is missing field '{field.Name}' required by its embedded type descriptor.");
                WriteField(writer, field, value);
            }
            Align(writer, cls.Alignment);
        }

        private void WriteField(BinaryWriter writer, EbxFieldDescriptor field, object? value)
        {
            AlignForField(writer, field.DebugType);
            switch (field.DebugType)
            {
                case EbxFieldType.Boolean: writer.Write(value is bool b && b ? (byte)1 : (byte)0); break;
                case EbxFieldType.Int8: writer.Write(Convert.ToSByte(value)); break;
                case EbxFieldType.UInt8: writer.Write(Convert.ToByte(value)); break;
                case EbxFieldType.Int16: writer.Write(Convert.ToInt16(value)); break;
                case EbxFieldType.UInt16: writer.Write(Convert.ToUInt16(value)); break;
                case EbxFieldType.Int32: writer.Write(Convert.ToInt32(value)); break;
                case EbxFieldType.UInt32: writer.Write(Convert.ToUInt32(value)); break;
                case EbxFieldType.Int64: writer.Write(Convert.ToInt64(value)); break;
                case EbxFieldType.UInt64: writer.Write(Convert.ToUInt64(value)); break;
                case EbxFieldType.Float32: writer.Write(Convert.ToSingle(value)); break;
                case EbxFieldType.Float64: writer.Write(Convert.ToDouble(value)); break;
                case EbxFieldType.Guid: WriteGuid(writer, value is Guid g ? g : Guid.Empty); break;
                case EbxFieldType.ResourceRef: writer.Write(Convert.ToUInt64(value)); break;
                case EbxFieldType.Sha1:
                {
                    var bytes = value is string text && text.Length == 40 ? Convert.FromHexString(text) : new byte[20];
                    writer.Write(bytes);
                    break;
                }
                case EbxFieldType.String:
                    WriteFixedString(writer, value as string ?? string.Empty, 32);
                    break;
                case EbxFieldType.CString:
                    writer.Write(Strings.Add(value as string ?? string.Empty));
                    break;
                case EbxFieldType.FileRef:
                case EbxFieldType.TypeRef:
                    writer.Write((ulong)Strings.Add(value as string ?? string.Empty));
                    break;
                case EbxFieldType.Enum:
                    writer.Write(Convert.ToInt32(value));
                    break;
                case EbxFieldType.Pointer:
                    WritePointer(writer, value as EbxPointer);
                    break;
                case EbxFieldType.Struct:
                {
                    if (value is not EbxObject nested)
                        throw new InvalidDataException($"Struct field '{field.Name}' is null or not an EbxObject.");
                    var cls = _document.Classes[field.ClassRef];
                    Align(writer, cls.Alignment);
                    WriteClass(writer, nested, cls);
                    break;
                }
                case EbxFieldType.Array:
                {
                    if (value is not List<object?> list || list.Count == 0)
                    {
                        writer.Write(0);
                        break;
                    }
                    var index = Arrays.Count;
                    Arrays.Add(new PendingArray
                    {
                        ClassRef = field.ClassRef,
                        Count = checked((uint)list.Count),
                        Values = list.ToArray()
                    });
                    writer.Write(index);
                    break;
                }
                case EbxFieldType.BoxedValueRef:
                    throw new NotSupportedException("BoxedValueRef structural writing is not enabled yet.");
                case EbxFieldType.DbObject:
                    throw new NotSupportedException("DbObject structural writing is not enabled yet.");
                default:
                    throw new NotSupportedException($"Cannot structurally write EBX field type {field.DebugType} ({field.Name}).");
            }
        }

        private void WritePointer(BinaryWriter writer, EbxPointer? pointer)
        {
            if (pointer == null || pointer.Kind == EbxPointerKind.Null)
            {
                writer.Write(0u);
                return;
            }

            if (pointer.Kind == EbxPointerKind.Internal)
            {
                if (pointer.InternalObjectIndex < 0 || pointer.InternalObjectIndex >= _document.Objects.Count)
                    throw new InvalidDataException($"Internal EBX pointer targets invalid object index {pointer.InternalObjectIndex}.");
                writer.Write(checked((uint)pointer.InternalObjectIndex + 1u));
                return;
            }

            if (pointer.External == null)
                throw new InvalidDataException("External EBX pointer has no import reference.");
            var importIndex = _document.Imports.FindIndex(x => x == pointer.External);
            if (importIndex < 0)
                throw new InvalidDataException($"External EBX import {pointer.External} is not present in the document import table.");
            writer.Write(0x80000000u | checked((uint)importIndex));
        }

        private static void AlignForField(BinaryWriter writer, EbxFieldType type)
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
                    Align(writer, 8);
                    break;
                case EbxFieldType.Array:
                case EbxFieldType.Pointer:
                    Align(writer, 4);
                    break;
            }
        }

        private static void WriteFixedString(BinaryWriter writer, string value, int length)
        {
            var buffer = new byte[length];
            var encoded = Encoding.UTF8.GetBytes(value);
            Buffer.BlockCopy(encoded, 0, buffer, 0, Math.Min(encoded.Length, Math.Max(0, length - 1)));
            writer.Write(buffer);
        }
    }

    internal sealed class PendingArray
    {
        public uint Offset { get; set; }
        public uint Count { get; init; }
        public int ClassRef { get; init; }
        public IReadOnlyList<object?> Values { get; init; } = Array.Empty<object?>();
    }

    public sealed class StringTable
    {
        private readonly List<string> _values = new();
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
        private uint _length;

        public IReadOnlyList<string> Values => _values;

        public uint Add(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0xFFFFFFFF;
            if (_offsets.TryGetValue(value, out var offset)) return offset;
            offset = _length;
            _offsets[value] = offset;
            _values.Add(value);
            _length = checked(_length + (uint)Encoding.UTF8.GetByteCount(value) + 1u);
            return offset;
        }
    }
}

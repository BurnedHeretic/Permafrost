using System.Collections.ObjectModel;

namespace Permafrost.Core.Ebx;

public enum EbxFieldType : byte
{
    Inherited = 0x00,
    DbObject = 0x01,
    Struct = 0x02,
    Pointer = 0x03,
    Array = 0x04,
    String = 0x06,
    CString = 0x07,
    Enum = 0x08,
    FileRef = 0x09,
    Boolean = 0x0A,
    Int8 = 0x0B,
    UInt8 = 0x0C,
    Int16 = 0x0D,
    UInt16 = 0x0E,
    Int32 = 0x0F,
    UInt32 = 0x10,
    UInt64 = 0x11,
    Int64 = 0x12,
    Float32 = 0x13,
    Float64 = 0x14,
    Guid = 0x15,
    Sha1 = 0x16,
    ResourceRef = 0x17,
    Delegate = 0x18,
    TypeRef = 0x19,
    BoxedValueRef = 0x1A
}

public sealed record EbxImportReference(Guid FileGuid, Guid ClassGuid);
public sealed record EbxArrayDescriptor(uint Offset, uint Count, int ClassRef);
public sealed record EbxInstanceDescriptor(ushort ClassRef, ushort Count, bool IsExported);

public sealed class EbxFieldDescriptor
{
    public required string Name { get; init; }
    public ushort Type { get; init; }
    public ushort ClassRef { get; init; }
    public uint DataOffset { get; init; }
    public uint SecondOffset { get; init; }
    public EbxFieldType DebugType => (EbxFieldType)((Type >> 4) & 0x1F);
}

public sealed class EbxClassDescriptor
{
    public required string Name { get; init; }
    public int FieldIndex { get; init; }
    public byte FieldCount { get; init; }
    public byte Alignment { get; init; }
    public ushort Type { get; init; }
    public ushort Size { get; init; }
    public ushort SecondSize { get; init; }
    public EbxFieldType DebugType => (EbxFieldType)((Type >> 4) & 0x1F);
}

public sealed record EbxFieldLocation(long Offset, EbxFieldType Type, int Length);

public enum EbxPointerKind { Null, Internal, External }

public sealed class EbxPointer
{
    public EbxPointerKind Kind { get; init; }
    public int InternalObjectIndex { get; init; } = -1;
    public int ImportIndex { get; init; } = -1;
    public EbxImportReference? External { get; init; }

    public override string ToString() => Kind switch
    {
        EbxPointerKind.Internal => $"Internal #{InternalObjectIndex}",
        EbxPointerKind.External when External != null => $"{External.FileGuid} / {External.ClassGuid}",
        _ => "(null)"
    };
}

public sealed class EbxObject
{
    public EbxObject(EbxClassDescriptor descriptor, bool isStruct = false)
    {
        Descriptor = descriptor;
        IsStruct = isStruct;
    }

    public EbxClassDescriptor Descriptor { get; }
    public bool IsStruct { get; }
    public Guid InstanceGuid { get; set; }
    public int ObjectIndex { get; set; } = -1;
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EbxFieldLocation> FieldLocations { get; } = new(StringComparer.Ordinal);

    public string ClassName => Descriptor.Name;

    public T? Get<T>(string name)
    {
        if (!Fields.TryGetValue(name, out var value) || value is not T typed)
            return default;
        return typed;
    }

    public override string ToString() => IsStruct ? Descriptor.Name : $"{Descriptor.Name} #{ObjectIndex}";
}

public sealed class EbxDocument
{
    internal EbxDocument(byte[] data) => Data = data;

    internal byte[] Data { get; }
    public string? SourcePath { get; internal set; }
    public string? AssetName { get; internal set; }
    public bool IsDirty { get; set; }
    public uint Magic { get; internal set; }
    public Guid FileGuid { get; internal set; }
    public long StringsOffset { get; internal set; }
    public uint StringsLength { get; internal set; }
    public long ArraysOffset { get; internal set; }
    public List<EbxImportReference> Imports { get; } = new();
    public List<EbxFieldDescriptor> Fields { get; } = new();
    public List<EbxClassDescriptor> Classes { get; } = new();
    public List<EbxArrayDescriptor> Arrays { get; } = new();
    public List<EbxInstanceDescriptor> Instances { get; } = new();
    public List<EbxObject> Objects { get; } = new();

    public EbxObject RootObject => Objects[0];

    public void PatchFloat(EbxObject owner, string fieldName, float value)
    {
        if (!owner.FieldLocations.TryGetValue(fieldName, out var location) || location.Type != EbxFieldType.Float32)
            throw new InvalidOperationException($"Field '{fieldName}' is not a patchable Float32 field.");

        var bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, Data, checked((int)location.Offset), 4);
        owner.Fields[fieldName] = value;
        IsDirty = true;
    }

    public byte[] GetBytesCopy() => (byte[])Data.Clone();

    public void SaveAs(string path)
    {
        File.WriteAllBytes(path, Data);
        IsDirty = false;
    }
}

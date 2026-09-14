using System.Buffers.Binary;
using System.Text;

namespace Permafrost.Core.Frostbite;

internal static class BinaryIo
{
    public static ushort ReadUInt16BE(this BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[2];
        if (reader.Read(bytes) != 2) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt16BigEndian(bytes);
    }

    public static uint ReadUInt32BE(this BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (reader.Read(bytes) != 4) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    public static int ReadInt32BE(this BinaryReader reader) => unchecked((int)reader.ReadUInt32BE());

    public static ulong ReadUInt64BE(this BinaryReader reader)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (reader.Read(bytes) != 8) throw new EndOfStreamException();
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    public static long ReadInt64BE(this BinaryReader reader) => unchecked((long)reader.ReadUInt64BE());

    public static string ReadNullTerminatedUtf8(this BinaryReader reader)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            stream.WriteByte(b);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ReadSizedUtf8(this BinaryReader reader, int length)
    {
        if (length < 0) throw new InvalidDataException("Negative string length.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException();
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    public static ulong Read7BitEncodedUInt64Compat(this BinaryReader reader)
    {
        ulong value = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            var b = reader.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new FormatException("Invalid 7-bit encoded integer.");
    }
}

using ZstdSharp;

namespace Permafrost.Core.Frostbite;

/// <summary>
/// Decompresses the CAS block stream used by Battlefront II resources.
/// Each block begins with BE decompressed-size, LE compression flags/type, and BE stored-size.
/// Compression type 0 is stored/raw and 0x0F is Zstandard.
/// </summary>
public static class CasBlockCodec
{
    public const int MaxChunkSize = 0x10000;

    public static byte[] Decompress(ReadOnlySpan<byte> input, int? neededSize = null)
    {
        using var output = new MemoryStream();
        var cursor = 0;

        while (cursor < input.Length)
        {
            if (input.Length - cursor < 8)
                throw new InvalidDataException("CAS stream ended inside a block header.");

            var decompressedSize = ReadUInt32BE(input.Slice(cursor, 4));
            cursor += 4;
            var compressionRaw = (ushort)(input[cursor] | (input[cursor + 1] << 8));
            cursor += 2;
            var bufferSize = (input[cursor] << 8) | input[cursor + 1];
            cursor += 2;

            var flags = (compressionRaw & 0xFF00) >> 8;
            if ((flags & 0x0F) != 0)
                bufferSize = ((flags & 0x0F) << 16) + bufferSize;
            if ((bufferSize & unchecked((int)0xFF000000)) != 0)
                bufferSize &= 0x00FFFFFF;

            if (bufferSize < 0 || bufferSize > MaxChunkSize)
                throw new InvalidDataException($"Unsupported CAS block size 0x{bufferSize:X}.");
            if (input.Length - cursor < bufferSize)
                throw new EndOfStreamException("CAS resource is truncated before the current block payload.");

            var compressionType = (ushort)(compressionRaw & 0x7F);
            var block = input.Slice(cursor, bufferSize);
            cursor += bufferSize;

            if (compressionType == 0)
            {
                output.Write(block);
            }
            else if (compressionType == 0x0F)
            {
                using var decompressor = new Decompressor();
                var decoded = new byte[checked((int)decompressedSize)];
                if (!decompressor.TryUnwrap(block, decoded, out var written))
                    throw new InvalidDataException("Zstd block decompression failed.");
                if (written != decoded.Length)
                    throw new InvalidDataException($"Zstd block decoded to {written} bytes; expected {decoded.Length}.");
                output.Write(decoded, 0, written);
            }
            else
            {
                throw new NotSupportedException($"CAS compression code 0x{compressionType:X} is not implemented.");
            }

            if (neededSize is { } required && output.Length >= required)
                break;
        }

        var result = output.ToArray();
        if (neededSize is { } limit && result.Length > limit)
            Array.Resize(ref result, limit);
        return result;
    }

    private static uint ReadUInt32BE(ReadOnlySpan<byte> data) =>
        ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
}

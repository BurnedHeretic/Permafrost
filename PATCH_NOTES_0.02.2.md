# Permafrost v0.02.2

Build fix for ZstdSharp.Port 0.8.8.

- Replaced an invalid `Decompressor.Unwrap(..., bufferSizePrecheck: false)` call with the supported `TryUnwrap(ReadOnlySpan<byte>, Span<byte>, out int)` API.
- Validates the decoded byte count against the Frostbite CAS block decompressed size.

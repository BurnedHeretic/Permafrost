# Permafrost v0.02.10

## BF2 bundle-header byte order

The v0.02.9 diagnostic established that the BF2 bundle aggregation database is now loading correctly: 23 catalogs, 4,777 manifest bundles, and a validated aggregation map. All 4,777 bundle headers still failed because `BundleReader` used .NET `BinaryReader.ReadUInt32()` / `ReadUInt64()`, which are little-endian.

Battlefront II bundle metadata is big-endian. Public Glacier source confirms its bundle reader uses Tokio `read_u32` / `read_u64`, which read big-endian values. A valid on-disk bundle magic `9D 79 8E D5` was therefore being interpreted by Permafrost as `0xD58E799D` instead of `0x9D798ED5`.

### Changes

- Bundle header data offset and magic: BE.
- Total / EBX / RES / chunk counts: BE.
- String/meta/data offsets: BE.
- EBX/RES name offsets and original sizes: BE.
- RES type and RES id tables: BE.
- CAS block decoding is still performed before bundle parsing, matching the BF2 storage pipeline.
- Per-bundle failures now report the mapped segment, raw/decompressed first bytes, and the exact parse or CAS-decode error.
- Removed the misleading final diagnostic that blamed aggregation after the aggregation map had already validated.

This release remains source-ready rather than precompiled in the current environment; run Clean/Rebuild in Visual Studio before testing.

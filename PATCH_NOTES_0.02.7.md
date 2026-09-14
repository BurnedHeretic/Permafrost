# Permafrost v0.02.7

## Fixes

- Corrected `VanillaBundleAggregation.kb` decoded-size prefix back to **big-endian**. Glacier reads this prefix through `bytes::Buf::get_i32()`, which is big-endian; the decoded aggregation payload itself is also parsed big-endian.
- Replaced one-shot Zstd `TryUnwrap` for the aggregation blob with `ZstdSharp.DecompressionStream`, matching Glacier's streaming-decoder behavior and avoiding `Src size is incorrect` on the large compatibility blob.
- Added exact decoded-length validation after streaming decompression.
- Corrected assembly/file version metadata and the diagnostics header to `v0.02.7`.

Expected aggregation decoded size for the current public BF2 table is approximately 76 MiB (0x04C00000), not 49,156 bytes.

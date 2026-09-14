# Permafrost v0.02.6

## Fixes

- Corrected the Battlefront II `VanillaBundleAggregation.kb` decoded-size prefix to **little-endian**.
- Glacier reads the first four bytes with `get_i32()` (little-endian), then reads the decompressed table itself as big-endian.
- Added clearer aggregation decompression diagnostics showing packed and expected decoded sizes.

This fixes the v0.02.5 scan failure:

`Bundle aggregation error: Src size is incorrect`

The cached aggregation file does not need to be deleted unless it was only partially downloaded; v0.02.6 can re-read the existing cache correctly.
